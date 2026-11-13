/*
 * Copyright (c) 2026, East Bay Makers Club and contributors
 * SPDX-License-Identifier: BSD-2-Clause
 *
 * Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
 * following conditions are met:
 * 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
 *    disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
 *    disclaimer in the documentation and/or other materials provided with the distribution.
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * This file is EBMC's own wrapper code. The library built from it (libebmc_occt) also contains Open CASCADE
 * Technology, which is under the GNU LGPL 2.1 with the Open CASCADE exception; that library as a whole is
 * distributed under those terms (see native/occt/README.md and StaticData/License/OpenCASCADE.txt).
 */
// ebmc_occt.cpp - implementation of the plain-C OCCT wrapper declared in ebmc_occt.h.
// Every exported function is wrapped in guard(), which turns any C++ exception into a status code and a
// thread-local message, so nothing C++ ever crosses the C boundary.

#include "ebmc_occt.h"

#include <BRepAdaptor_Curve.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepBndLib.hxx>
#include <BRepBuilderAPI_Copy.hxx>
#include <BRepBuilderAPI_GTransform.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepFilletAPI_MakeChamfer.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepGProp.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <GCPnts_AbscissaPoint.hxx>
#include <GProp_GProps.hxx>
#include <IFSelect_ReturnStatus.hxx>
#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Poly_PolygonOnTriangulation.hxx>
#include <Poly_Triangulation.hxx>
#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <Standard_Failure.hxx>
#include <Standard_VersionInfo.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_ShapeMapHasher.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Vertex.hxx>
#include <NCollection_IndexedMap.hxx>
#include <NCollection_IndexedDataMap.hxx>
#include <NCollection_List.hxx>
#include <gp_GTrsf.hxx>
#include <gp_Trsf.hxx>
#include <gp_Vec.hxx>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <mutex>
#include <new>
#include <string>
#include <unordered_map>
#include <vector>

using ShapeMap = NCollection_IndexedMap<TopoDS_Shape, TopTools_ShapeMapHasher>;  // == TopTools_IndexedMapOfShape
using AncestorMap =
  NCollection_IndexedDataMap<TopoDS_Shape, NCollection_List<TopoDS_Shape>, TopTools_ShapeMapHasher>;

struct ebmc_shape
{
  TopoDS_Shape shape;
  ShapeMap     faces, edges, vertices;  // deterministic 1-based maps; API index = map index - 1

  explicit ebmc_shape(const TopoDS_Shape& s)
      : shape(s)
  {
    TopExp::MapShapes(shape, TopAbs_FACE, faces);
    TopExp::MapShapes(shape, TopAbs_EDGE, edges);
    TopExp::MapShapes(shape, TopAbs_VERTEX, vertices);
  }
};

namespace
{
thread_local std::string g_lastError;
std::mutex               g_stepMutex;

// Thrown inside guard() to return a specific status with a message.
struct EbmcError
{
  int         code;
  std::string message;
};

[[noreturn]] void fail(int code, std::string message)
{
  throw EbmcError{code, std::move(message)};
}

// One-time library setup: OCCT's default messenger prints translator statistics and warnings to stdout;
// a library loaded into a GUI app should be silent (errors come back through ebmc_occt_last_error).
void initOnce()
{
  static std::once_flag once;
  std::call_once(once, [] { Message::DefaultMessenger()->ChangePrinters().Clear(); });
}

template <typename F>
int guard(const char* fn, F&& body)
{
  g_lastError.clear();
  try
  {
    initOnce();
    body();
    return EBMC_OK;
  }
  catch (const EbmcError& e)
  {
    g_lastError = std::string(fn) + ": " + e.message;
    return e.code;
  }
  catch (const Standard_Failure& e)
  {
    const char* msg = e.what();
    g_lastError     = std::string(fn) + ": OCCT " + e.ExceptionType() + ": " + (msg && *msg ? msg : "(no message)");
    return EBMC_ERR_OCCT;
  }
  catch (const std::exception& e)
  {
    g_lastError = std::string(fn) + ": C++ exception: " + e.what();
    return EBMC_ERR_STD;
  }
  catch (...)
  {
    g_lastError = std::string(fn) + ": unknown C++ exception";
    return EBMC_ERR_UNKNOWN;
  }
}

void need(const void* p, const char* name)
{
  if (p == nullptr)
    fail(EBMC_ERR_NULL_ARG, std::string(name) + " is NULL");
}

void emit(const TopoDS_Shape& s, ebmc_shape** out)
{
  if (s.IsNull())
    fail(EBMC_ERR_NOT_DONE, "operation produced an empty shape");
  *out = new ebmc_shape(s);
}

// Fillet/chamfer can report IsDone() and still return an invalid solid; check it before handing it out.
void emitValid(const TopoDS_Shape& s, ebmc_shape** out, const char* what)
{
  if (s.IsNull())
    fail(EBMC_ERR_NOT_DONE, std::string(what) + " produced an empty shape");
  if (!BRepCheck_Analyzer(s).IsValid())
    fail(EBMC_ERR_NOT_DONE, std::string(what) + " produced an invalid shape (BRepCheck_Analyzer)");
  *out = new ebmc_shape(s);
}

const TopoDS_Edge& edgeAt(const ebmc_shape* s, int32_t index)
{
  if (index < 0 || index >= s->edges.Extent())
    fail(EBMC_ERR_INDEX, "edge index " + std::to_string(index) + " out of range [0," + std::to_string(s->edges.Extent()) + ")");
  return TopoDS::Edge(s->edges.FindKey(index + 1));
}

template <typename T>
T* copyOut(const std::vector<T>& v)
{
  T* p = static_cast<T*>(std::malloc(sizeof(T) * (v.empty() ? 1 : v.size())));
  if (p == nullptr)
    throw std::bad_alloc();
  if (!v.empty())
    std::memcpy(p, v.data(), sizeof(T) * v.size());
  return p;
}

template <typename Maker>
void addEdges(Maker& mk, const ebmc_shape* shape, const int32_t* idx, int32_t n, double size)
{
  if (n <= 0)
    fail(EBMC_ERR_INVALID_ARG, "need at least one edge");
  for (int32_t i = 0; i < n; ++i)
    mk.Add(size, edgeAt(shape, idx[i]));
}

int32_t toI32(size_t n, const char* what)
{
  if (n > 0x7fffffff)
    fail(EBMC_ERR_INVALID_ARG, std::string("too many ") + what);
  return static_cast<int32_t>(n);
}
} // namespace

extern "C" {

EBMC_API const char* ebmc_occt_version(void)
{
  static const std::string v = std::string("ebmc_occt 0.3; OCCT ") + OCCT_Version_String_Complete();
  return v.c_str();
}

EBMC_API const char* ebmc_occt_last_error(void)
{
  return g_lastError.c_str();
}

EBMC_API void ebmc_shape_free(ebmc_shape* shape)
{
  try
  {
    delete shape;
  }
  catch (...)
  {
  }
}

EBMC_API int ebmc_make_box(double dx, double dy, double dz, ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(out, "out");
    *out = nullptr;
    if (!(dx > 0 && dy > 0 && dz > 0))
      fail(EBMC_ERR_INVALID_ARG, "box dimensions must be > 0");
    BRepPrimAPI_MakeBox mk(dx, dy, dz);
    emit(mk.Shape(), out);
  });
}

EBMC_API int ebmc_make_cylinder(double radius, double height, ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(out, "out");
    *out = nullptr;
    if (!(radius > 0 && height > 0))
      fail(EBMC_ERR_INVALID_ARG, "cylinder radius and height must be > 0");
    BRepPrimAPI_MakeCylinder mk(radius, height);
    emit(mk.Shape(), out);
  });
}

EBMC_API int ebmc_translate(const ebmc_shape* shape, double dx, double dy, double dz, ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(out, "out");
    *out = nullptr;
    gp_Trsf t;
    t.SetTranslation(gp_Vec(dx, dy, dz));
    BRepBuilderAPI_Transform tr(shape->shape, t, /*copy*/ true);
    if (!tr.IsDone())
      fail(EBMC_ERR_NOT_DONE, "transform failed");
    emit(tr.Shape(), out);
  });
}

EBMC_API int ebmc_boolean(const ebmc_shape* a, const ebmc_shape* b, int op, ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(a, "a");
    need(b, "b");
    need(out, "out");
    *out = nullptr;
    TopoDS_Shape result;
    auto         run = [&](BRepAlgoAPI_BooleanOperation& algo) {
      if (!algo.IsDone() || algo.HasErrors())
        fail(EBMC_ERR_NOT_DONE, "boolean operation failed");
      result = algo.Shape();
    };
    switch (op)
    {
      case EBMC_BOOL_FUSE: { BRepAlgoAPI_Fuse f(a->shape, b->shape); run(f); break; }
      case EBMC_BOOL_CUT: { BRepAlgoAPI_Cut c(a->shape, b->shape); run(c); break; }
      case EBMC_BOOL_COMMON: { BRepAlgoAPI_Common c(a->shape, b->shape); run(c); break; }
      default: fail(EBMC_ERR_INVALID_ARG, "op must be EBMC_BOOL_FUSE, _CUT or _COMMON");
    }
    emit(result, out);
  });
}

EBMC_API int ebmc_shape_counts(const ebmc_shape* shape, int32_t* faces, int32_t* edges, int32_t* vertices)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    if (faces) *faces = shape->faces.Extent();
    if (edges) *edges = shape->edges.Extent();
    if (vertices) *vertices = shape->vertices.Extent();
  });
}

EBMC_API int ebmc_edge_info(const ebmc_shape* shape, int32_t edge_index, double* start_xyz, double* end_xyz,
                            double* length, int32_t* is_degenerate, int32_t* is_closed)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    const TopoDS_Edge& e = edgeAt(shape, edge_index);
    TopoDS_Vertex      v1, v2;
    TopExp::Vertices(e, v1, v2, /*CumOri*/ false);
    const bool degenerate = BRep_Tool::Degenerated(e);
    if (start_xyz && !v1.IsNull())
    {
      gp_Pnt p = BRep_Tool::Pnt(v1);
      start_xyz[0] = p.X(); start_xyz[1] = p.Y(); start_xyz[2] = p.Z();
    }
    if (end_xyz && !v2.IsNull())
    {
      gp_Pnt p = BRep_Tool::Pnt(v2);
      end_xyz[0] = p.X(); end_xyz[1] = p.Y(); end_xyz[2] = p.Z();
    }
    if (length)
      *length = degenerate ? 0.0 : GCPnts_AbscissaPoint::Length(BRepAdaptor_Curve(e));
    if (is_degenerate) *is_degenerate = degenerate ? 1 : 0;
    if (is_closed) *is_closed = (!v1.IsNull() && v1.IsSame(v2)) ? 1 : 0;
  });
}

EBMC_API int ebmc_fillet_edges(const ebmc_shape* shape, const int32_t* edge_indices, int32_t n, double radius,
                               ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(edge_indices, "edge_indices");
    need(out, "out");
    *out = nullptr;
    if (!(radius > 0))
      fail(EBMC_ERR_INVALID_ARG, "radius must be > 0");
    BRepFilletAPI_MakeFillet mk(shape->shape);
    addEdges(mk, shape, edge_indices, n, radius);
    mk.Build();
    if (!mk.IsDone())
      fail(EBMC_ERR_NOT_DONE, "fillet failed (radius too large for the adjacent faces, or unsupported "
                              "configuration); " + std::to_string(mk.NbFaultyContours()) + " faulty contour(s)");
    emitValid(mk.Shape(), out, "fillet");
  });
}

EBMC_API int ebmc_chamfer_edges(const ebmc_shape* shape, const int32_t* edge_indices, int32_t n,
                                double distance, ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(edge_indices, "edge_indices");
    need(out, "out");
    *out = nullptr;
    if (!(distance > 0))
      fail(EBMC_ERR_INVALID_ARG, "distance must be > 0");
    BRepFilletAPI_MakeChamfer mk(shape->shape);
    addEdges(mk, shape, edge_indices, n, distance);
    mk.Build();
    if (!mk.IsDone())
      fail(EBMC_ERR_NOT_DONE, "chamfer failed (distance too large or unsupported configuration)");
    emitValid(mk.Shape(), out, "chamfer");
  });
}

EBMC_API int ebmc_volume(const ebmc_shape* shape, double* volume)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(volume, "volume");
    GProp_GProps p;
    BRepGProp::VolumeProperties(shape->shape, p);
    *volume = p.Mass();
  });
}

EBMC_API int ebmc_area(const ebmc_shape* shape, double* area)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(area, "area");
    GProp_GProps p;
    BRepGProp::SurfaceProperties(shape->shape, p);
    *area = p.Mass();
  });
}

EBMC_API int ebmc_bbox(const ebmc_shape* shape, double* out6)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(out6, "out6");
    Bnd_Box box;
    BRepBndLib::AddOptimal(shape->shape, box, /*useTriangulation*/ false, /*useShapeTolerance*/ false);
    if (box.IsVoid())
      fail(EBMC_ERR_NOT_DONE, "empty bounding box");
    box.Get(out6[0], out6[1], out6[2], out6[3], out6[4], out6[5]);
  });
}

EBMC_API int ebmc_is_valid(const ebmc_shape* shape, int32_t* is_valid)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(is_valid, "is_valid");
    BRepCheck_Analyzer an(shape->shape);
    *is_valid = an.IsValid() ? 1 : 0;
  });
}

// ---------------------------------------------------------------------------------------------------------
// Tessellation
// ---------------------------------------------------------------------------------------------------------

EBMC_API int ebmc_tessellate(const ebmc_shape* shape, double linear_deflection, double angular_deflection,
                             int32_t max_triangles, ebmc_mesh** out)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(out, "out");
    *out = nullptr;
    if (!(linear_deflection > 0 && angular_deflection > 0))
      fail(EBMC_ERR_INVALID_ARG, "deflections must be > 0");

    // Mesh a copy: BRepMesh stores triangulations inside the (shared) TShapes, so meshing the input would
    // mutate it and every other shape sharing those faces (e.g. the box a fillet was made from), which is
    // a data race across threads and makes results depend on earlier meshing. The copy has the same
    // topology, so TopExp::MapShapes gives the same indices as on the input.
    BRepBuilderAPI_Copy copier(shape->shape, /*copyGeom*/ false, /*copyMesh*/ false);
    const TopoDS_Shape  work = copier.Shape();
    ShapeMap            faces, edges, vertices;
    TopExp::MapShapes(work, TopAbs_FACE, faces);
    TopExp::MapShapes(work, TopAbs_EDGE, edges);
    TopExp::MapShapes(work, TopAbs_VERTEX, vertices);
    if (faces.Extent() != shape->faces.Extent() || edges.Extent() != shape->edges.Extent())
      fail(EBMC_ERR_NOT_DONE, "internal: copied shape has different topology");

    BRepMesh_IncrementalMesh mesher(work, linear_deflection, /*isRelative*/ false, angular_deflection,
                                    /*isInParallel*/ false);
    if (!mesher.IsDone())
      fail(EBMC_ERR_NOT_DONE, "meshing failed");

    // Size check before any output is built, so a huge mesh is never copied into our arrays or the caller's.
    if (max_triangles > 0)
    {
      int64_t total = 0;
      for (int fi = 1; fi <= faces.Extent(); ++fi)
      {
        TopLoc_Location                        loc;
        const occ::handle<Poly_Triangulation>& tri = BRep_Tool::Triangulation(TopoDS::Face(faces.FindKey(fi)), loc);
        if (!tri.IsNull())
          total += tri->NbTriangles();
        if (total > max_triangles)
          fail(EBMC_ERR_TOO_BIG, "more than " + std::to_string(max_triangles) + " triangles");
      }
    }

    std::vector<double>  verts;
    std::vector<int32_t> tris, triFaces;
    // Welding keys: a node on B-rep vertex v -> vertexKey[v]; the j-th node of edge e's polygon ->
    // edgeNodeKey[(e,j)]. Polygons on triangulation of the same edge in two faces share the edge's
    // parameters, so node j is the same point in both.
    std::vector<int32_t>                 vertexGlobal(vertices.Extent() + 1, -1);
    std::unordered_map<int64_t, int32_t> edgeNodeGlobal;
    auto addVertex = [&](const gp_Pnt& p) {
      verts.push_back(p.X()); verts.push_back(p.Y()); verts.push_back(p.Z());
      return static_cast<int32_t>(verts.size() / 3 - 1);
    };

    for (int fi = 1; fi <= faces.Extent(); ++fi)
    {
      const TopoDS_Face&                    face = TopoDS::Face(faces.FindKey(fi));
      TopLoc_Location                       loc;
      const occ::handle<Poly_Triangulation>& tri = BRep_Tool::Triangulation(face, loc);
      if (tri.IsNull())
        fail(EBMC_ERR_NOT_DONE, "face " + std::to_string(fi - 1) + " has no triangulation");
      const gp_Trsf        trsf = loc.Transformation();
      std::vector<int32_t> local(tri->NbNodes() + 1, -1);
      auto nodePoint = [&](int n) { return tri->Node(n).Transformed(trsf); };

      for (TopExp_Explorer ex(face, TopAbs_EDGE); ex.More(); ex.Next())
      {
        const TopoDS_Edge&                              e   = TopoDS::Edge(ex.Current());
        const int                                       ei  = edges.FindIndex(e);
        const occ::handle<Poly_PolygonOnTriangulation>& pol = BRep_Tool::PolygonOnTriangulation(e, tri, loc);
        if (pol.IsNull())
          continue;
        TopoDS_Vertex v1, v2;
        TopExp::Vertices(e, v1, v2, /*CumOri*/ false);
        const int  nb         = pol->NbNodes();
        const bool degenerate = BRep_Tool::Degenerated(e);
        for (int j = 1; j <= nb; ++j)
        {
          const int n = pol->Node(j);
          if (n < 1 || n > tri->NbNodes())
            continue;
          int32_t* slot = nullptr;
          if ((j == 1 || degenerate) && !v1.IsNull())
            slot = &vertexGlobal[vertices.FindIndex(v1)];
          else if (j == nb && !v2.IsNull())
            slot = &vertexGlobal[vertices.FindIndex(v2)];
          else
            slot = &edgeNodeGlobal.try_emplace((int64_t(ei) << 32) | int64_t(j), -1).first->second;
          if (*slot < 0)
            *slot = local[n] >= 0 ? local[n] : addVertex(nodePoint(n));
          local[n] = *slot;
        }
      }
      for (int n = 1; n <= tri->NbNodes(); ++n)
        if (local[n] < 0)
          local[n] = addVertex(nodePoint(n));

      const bool reversed = face.Orientation() == TopAbs_REVERSED;
      for (int t = 1; t <= tri->NbTriangles(); ++t)
      {
        int n1, n2, n3;
        tri->Triangle(t).Get(n1, n2, n3);
        if (reversed)
          std::swap(n2, n3);
        tris.push_back(local[n1]); tris.push_back(local[n2]); tris.push_back(local[n3]);
        triFaces.push_back(fi - 1);
      }
    }

    // Edge polylines, taken from the polygon on the triangulation of the first face using each edge.
    AncestorMap edgeFaces;
    TopExp::MapShapesAndAncestors(work, TopAbs_EDGE, TopAbs_FACE, edgeFaces);
    std::vector<int32_t> edgeIds, edgeOffsets{0};
    std::vector<double>  edgePts;
    for (int ei = 1; ei <= edges.Extent(); ++ei)
    {
      const TopoDS_Edge& e = TopoDS::Edge(edges.FindKey(ei));
      if (BRep_Tool::Degenerated(e))
        continue;
      const int k = edgeFaces.FindIndex(e);
      if (k == 0 || edgeFaces(k).IsEmpty())
        continue;
      const TopoDS_Face&                    f = TopoDS::Face(edgeFaces(k).First());
      TopLoc_Location                       loc;
      const occ::handle<Poly_Triangulation>& tri = BRep_Tool::Triangulation(f, loc);
      if (tri.IsNull())
        continue;
      const occ::handle<Poly_PolygonOnTriangulation>& pol = BRep_Tool::PolygonOnTriangulation(e, tri, loc);
      if (pol.IsNull())
        continue;
      const gp_Trsf trsf = loc.Transformation();
      for (int j = 1; j <= pol->NbNodes(); ++j)
      {
        const int n = pol->Node(j);
        if (n < 1 || n > tri->NbNodes())
          continue;
        gp_Pnt p = tri->Node(n).Transformed(trsf);
        edgePts.push_back(p.X()); edgePts.push_back(p.Y()); edgePts.push_back(p.Z());
      }
      edgeIds.push_back(ei - 1);
      edgeOffsets.push_back(toI32(edgePts.size() / 3, "edge points"));
    }

    ebmc_mesh* m = static_cast<ebmc_mesh*>(std::calloc(1, sizeof(ebmc_mesh)));
    if (m == nullptr)
      throw std::bad_alloc();
    try
    {
      m->vertex_count     = toI32(verts.size() / 3, "vertices");
      m->triangle_count   = toI32(tris.size() / 3, "triangles");
      m->edge_count       = toI32(edgeIds.size(), "edges");
      m->edge_point_count = toI32(edgePts.size() / 3, "edge points");
      m->vertices         = copyOut(verts);
      m->triangles        = copyOut(tris);
      m->triangle_faces   = copyOut(triFaces);
      m->edge_ids         = copyOut(edgeIds);
      m->edge_offsets     = copyOut(edgeOffsets);
      m->edge_points      = copyOut(edgePts);
    }
    catch (...)
    {
      ebmc_mesh_free(m);
      throw;
    }
    *out = m;
  });
}

EBMC_API void ebmc_mesh_free(ebmc_mesh* mesh)
{
  if (mesh == nullptr)
    return;
  std::free(mesh->vertices);
  std::free(mesh->triangles);
  std::free(mesh->triangle_faces);
  std::free(mesh->edge_ids);
  std::free(mesh->edge_offsets);
  std::free(mesh->edge_points);
  std::free(mesh);
}

// ---------------------------------------------------------------------------------------------------------
// STEP
// ---------------------------------------------------------------------------------------------------------

EBMC_API int ebmc_write_step(const ebmc_shape* shape, const char* path)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(path, "path");
    std::lock_guard<std::mutex> lock(g_stepMutex);
    STEPControl_Writer          w;
    if (w.Transfer(shape->shape, STEPControl_AsIs) != IFSelect_RetDone)
      fail(EBMC_ERR_NOT_DONE, "STEP transfer failed");
    if (w.Write(path) != IFSelect_RetDone)
      fail(EBMC_ERR_IO, std::string("could not write STEP file '") + path + "'");
  });
}

EBMC_API int ebmc_read_step(const char* path, ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(path, "path");
    need(out, "out");
    *out = nullptr;
    std::lock_guard<std::mutex> lock(g_stepMutex);
    STEPControl_Reader          r;
    if (r.ReadFile(path) != IFSelect_RetDone)
      fail(EBMC_ERR_IO, std::string("could not read STEP file '") + path + "'");
    if (r.TransferRoots() <= 0)
      fail(EBMC_ERR_NOT_DONE, "STEP file contains no transferable shapes");
    emit(r.OneShape(), out);
  });
}

// ---------------------------------------------------------------------------------------------------------
// Added in 0.2: edge geometry, general transform, compound
// ---------------------------------------------------------------------------------------------------------

EBMC_API int ebmc_edge_geom(const ebmc_shape* shape, int32_t edge_index, int32_t* curve_type, double* mid_xyz,
                            double* tangent_xyz, double* radius)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    const TopoDS_Edge& e = edgeAt(shape, edge_index);
    int32_t type = EBMC_CURVE_OTHER;
    gp_Pnt  mid;
    gp_Vec  tangent(0, 0, 0);
    double  r = 0;
    if (BRep_Tool::Degenerated(e))
    {
      TopoDS_Vertex v1, v2;
      TopExp::Vertices(e, v1, v2, /*CumOri*/ false);
      if (!v1.IsNull())
        mid = BRep_Tool::Pnt(v1);
    }
    else
    {
      BRepAdaptor_Curve c(e);
      switch (c.GetType())
      {
        case GeomAbs_Line: type = EBMC_CURVE_LINE; break;
        case GeomAbs_Circle: type = EBMC_CURVE_CIRCLE; r = c.Circle().Radius(); break;
        default: type = EBMC_CURVE_OTHER; break;
      }
      const double u = 0.5 * (c.FirstParameter() + c.LastParameter());
      c.D1(u, mid, tangent);
      if (tangent.Magnitude() > 1e-12)
        tangent.Normalize();
    }
    if (curve_type) *curve_type = type;
    if (mid_xyz) { mid_xyz[0] = mid.X(); mid_xyz[1] = mid.Y(); mid_xyz[2] = mid.Z(); }
    if (tangent_xyz) { tangent_xyz[0] = tangent.X(); tangent_xyz[1] = tangent.Y(); tangent_xyz[2] = tangent.Z(); }
    if (radius) *radius = r;
  });
}

EBMC_API int ebmc_transform(const ebmc_shape* shape, const double* m, ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(shape, "shape");
    need(m, "m12");
    need(out, "out");
    *out = nullptr;
    for (int i = 0; i < 12; ++i)
      if (!std::isfinite(m[i]))
        fail(EBMC_ERR_INVALID_ARG, "matrix contains a non-finite value");

    // A similitude (rotation * uniform scale, possibly mirrored) keeps the exact geometry with gp_Trsf.
    // Check it here rather than relying on gp_Trsf::SetValues throwing.
    const gp_XYZ c0(m[0], m[4], m[8]), c1(m[1], m[5], m[9]), c2(m[2], m[6], m[10]);
    const double s0 = c0.Modulus(), s1 = c1.Modulus(), s2 = c2.Modulus();
    if (s0 < 1e-12 || s1 < 1e-12 || s2 < 1e-12)
      fail(EBMC_ERR_INVALID_ARG, "matrix is singular");
    const double tol        = 1e-9 * std::max(s0, std::max(s1, s2));
    const bool   similitude = std::fabs(s0 - s1) < tol && std::fabs(s0 - s2) < tol
                            && std::fabs(c0.Dot(c1)) < tol * s0 && std::fabs(c0.Dot(c2)) < tol * s0
                            && std::fabs(c1.Dot(c2)) < tol * s0;
    TopoDS_Shape result;
    if (similitude)
    {
      gp_Trsf t;
      t.SetValues(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11]);
      BRepBuilderAPI_Transform tr(shape->shape, t, /*copy*/ true);
      if (!tr.IsDone())
        fail(EBMC_ERR_NOT_DONE, "transform failed");
      result = tr.Shape();
    }
    else
    {
      gp_GTrsf g;
      g.SetValue(1, 1, m[0]); g.SetValue(1, 2, m[1]); g.SetValue(1, 3, m[2]); g.SetValue(1, 4, m[3]);
      g.SetValue(2, 1, m[4]); g.SetValue(2, 2, m[5]); g.SetValue(2, 3, m[6]); g.SetValue(2, 4, m[7]);
      g.SetValue(3, 1, m[8]); g.SetValue(3, 2, m[9]); g.SetValue(3, 3, m[10]); g.SetValue(3, 4, m[11]);
      BRepBuilderAPI_GTransform tr(shape->shape, g, /*copy*/ true);
      if (!tr.IsDone())
        fail(EBMC_ERR_NOT_DONE, "general transform failed");
      result = tr.Shape();
    }
    emit(result, out);
  });
}

EBMC_API int ebmc_compound(const ebmc_shape* const* shapes, int32_t n, ebmc_shape** out)
{
  return guard(__func__, [&] {
    need(shapes, "shapes");
    need(out, "out");
    *out = nullptr;
    if (n <= 0)
      fail(EBMC_ERR_INVALID_ARG, "need at least one shape");
    BRep_Builder    b;
    TopoDS_Compound c;
    b.MakeCompound(c);
    for (int32_t i = 0; i < n; ++i)
    {
      need(shapes[i], "shapes[i]");
      b.Add(c, shapes[i]->shape);
    }
    emit(c, out);
  });
}

} // extern "C"
