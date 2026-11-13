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
/*
 * ebmc_occt.h - a small plain-C wrapper around Open CASCADE Technology (OCCT) for the EBMC app.
 *
 * ABI rules
 *  - Plain C. No C++ types and no exceptions cross this boundary. Every function catches everything
 *    (Standard_Failure, std::exception, ...) and returns an int status: EBMC_OK (0) or an EBMC_ERR_* code.
 *  - After a non-zero status, ebmc_occt_last_error() returns a message for the failure. The message is
 *    thread-local and stays valid until the next ebmc_* call on the same thread.
 *  - Shapes are opaque pointers (ebmc_shape*). Each one returned through an out-parameter is owned by the
 *    caller and must be released exactly once with ebmc_shape_free(). Operations never modify their input
 *    shapes; they always return a new shape.
 *  - Meshes returned by ebmc_tessellate() are library-allocated and must be released with ebmc_mesh_free().
 *
 * Sub-shape indexing (faces, edges, vertices)
 *  - Indices are 0-based and come from TopExp::MapShapes(shape, TopAbs_FACE / TopAbs_EDGE / TopAbs_VERTEX,
 *    TopTools_IndexedMapOfShape) on the shape, minus one. That traversal is deterministic: the same shape
 *    (or a copy, or a STEP round trip of an unchanged solid) always yields the same order. Indices are only
 *    meaningful for the shape they were obtained from; a new shape (boolean, fillet, ...) has its own
 *    indexing.
 *  - A sub-shape shared by several faces (e.g. an edge between two faces) has one index.
 *
 * Threading (as tested in Stage 0, see README): different shapes may be used from different threads at the
 * same time. Do not use or free the same ebmc_shape from two threads at once. STEP read/write are serialised
 * internally by a mutex because OCCT's STEP translator uses global parameter tables.
 */
#ifndef EBMC_OCCT_H
#define EBMC_OCCT_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(EBMC_OCCT_BUILD)
#    define EBMC_API __declspec(dllexport)
#  else
#    define EBMC_API __declspec(dllimport)
#  endif
#else
#  define EBMC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Status codes */
#define EBMC_OK               0
#define EBMC_ERR_NULL_ARG     1  /* a required pointer argument was NULL */
#define EBMC_ERR_INVALID_ARG  2  /* a numeric argument is out of range (negative size, bad op, ...) */
#define EBMC_ERR_INDEX        3  /* a sub-shape index is out of range */
#define EBMC_ERR_NOT_DONE     4  /* the OCCT algorithm ran but reported failure (e.g. fillet radius too big) */
#define EBMC_ERR_OCCT         5  /* an OCCT exception (Standard_Failure or subclass) was caught */
#define EBMC_ERR_STD          6  /* a C++ std::exception was caught (e.g. std::bad_alloc) */
#define EBMC_ERR_UNKNOWN      7  /* any other C++ exception */
#define EBMC_ERR_IO           8  /* file could not be read/written or parsed */
#define EBMC_ERR_TOO_BIG      9  /* the result would exceed a size limit the caller set (e.g. max_triangles) */

/* Boolean operations */
#define EBMC_BOOL_FUSE   0
#define EBMC_BOOL_CUT    1  /* a minus b */
#define EBMC_BOOL_COMMON 2

typedef struct ebmc_shape ebmc_shape;

/* Triangle mesh of a shape. All arrays are owned by the library; free the whole thing with ebmc_mesh_free.
 *  vertices        vertex_count * 3 doubles (x,y,z), in model units (mm).
 *                  Vertices are shared between faces: nodes that lie on a B-rep edge or vertex are welded
 *                  topologically (same edge/vertex => same index), so the mesh of a closed solid is closed.
 *  triangles       triangle_count * 3 vertex indices (0-based), counter-clockwise seen from outside
 *                  (face orientation is applied).
 *  triangle_faces  triangle_count face indices (0-based, see "Sub-shape indexing").
 *  edge_*          one polyline per non-degenerate B-rep edge that has a discretisation:
 *                  polyline i has id edge_ids[i] (0-based edge index) and points
 *                  edge_points[3*edge_offsets[i] .. 3*edge_offsets[i+1]-1]; edge_offsets has edge_count+1
 *                  entries. */
typedef struct ebmc_mesh {
    double*  vertices;
    int32_t* triangles;
    int32_t* triangle_faces;
    int32_t* edge_ids;
    int32_t* edge_offsets;
    double*  edge_points;
    int32_t  vertex_count;
    int32_t  triangle_count;
    int32_t  edge_count;
    int32_t  edge_point_count;
} ebmc_mesh;

/* Version string, e.g. "ebmc_occt 0.3; OCCT 8.0.1". Static storage; never fails. */
EBMC_API const char* ebmc_occt_version(void);

/* Message for the last failed call on this thread ("" if none). */
EBMC_API const char* ebmc_occt_last_error(void);

/* Releases a shape. NULL is allowed and ignored. */
EBMC_API void ebmc_shape_free(ebmc_shape* shape);

/* Primitives. Box: corner at the origin, extending to (dx,dy,dz). Cylinder: axis +Z through the origin,
 * base at z=0, top at z=h. */
EBMC_API int ebmc_make_box(double dx, double dy, double dz, ebmc_shape** out);
EBMC_API int ebmc_make_cylinder(double radius, double height, ebmc_shape** out);

/* Returns a translated copy of shape. */
EBMC_API int ebmc_translate(const ebmc_shape* shape, double dx, double dy, double dz, ebmc_shape** out);

/* Boolean a <op> b with op = EBMC_BOOL_*. */
EBMC_API int ebmc_boolean(const ebmc_shape* a, const ebmc_shape* b, int op, ebmc_shape** out);

/* Number of distinct faces, edges and vertices (sizes of the index maps). Any out pointer may be NULL. */
EBMC_API int ebmc_shape_counts(const ebmc_shape* shape, int32_t* faces, int32_t* edges, int32_t* vertices);

/* Information about edge edge_index: 3D start/end points (3 doubles each), curve length, and flags
 * (is_degenerate: 1 for degenerated edges such as a cone apex; is_closed: 1 if start == end vertex).
 * Any out pointer may be NULL. */
EBMC_API int ebmc_edge_info(const ebmc_shape* shape, int32_t edge_index, double* start_xyz, double* end_xyz,
                            double* length, int32_t* is_degenerate, int32_t* is_closed);

/* Constant-radius fillet / equal-distance chamfer on the given edges (0-based edge indices). */
EBMC_API int ebmc_fillet_edges(const ebmc_shape* shape, const int32_t* edge_indices, int32_t n, double radius,
                               ebmc_shape** out);
EBMC_API int ebmc_chamfer_edges(const ebmc_shape* shape, const int32_t* edge_indices, int32_t n,
                                double distance, ebmc_shape** out);

/* Exact mass properties from the B-rep (not the mesh). */
EBMC_API int ebmc_volume(const ebmc_shape* shape, double* volume);
EBMC_API int ebmc_area(const ebmc_shape* shape, double* area);

/* Axis-aligned bounding box: out6 = {xmin, ymin, zmin, xmax, ymax, zmax}. Uses the precise (optimal) box. */
EBMC_API int ebmc_bbox(const ebmc_shape* shape, double* out6);

/* BRepCheck_Analyzer validity: *is_valid = 1 if the shape is topologically/geometrically valid. */
EBMC_API int ebmc_is_valid(const ebmc_shape* shape, int32_t* is_valid);

/* Triangulates a copy of the shape (the input shape is not modified) with BRepMesh_IncrementalMesh.
 * linear_deflection in model units (mm), angular_deflection in radians.
 * max_triangles > 0 caps the result: when the triangulation has more triangles than that, the call fails with
 * EBMC_ERR_TOO_BIG before any output array is built (OCCT's own triangulation of the copy is still made and freed).
 * 0 or less means no cap. (Changed in 0.3: the max_triangles parameter is new.) */
EBMC_API int ebmc_tessellate(const ebmc_shape* shape, double linear_deflection, double angular_deflection,
                             int32_t max_triangles, ebmc_mesh** out);
EBMC_API void ebmc_mesh_free(ebmc_mesh* mesh);

/* STEP AP214 export / import, units mm. path is UTF-8. Reading returns all roots as one shape (a compound
 * if the file has several). */
EBMC_API int ebmc_write_step(const ebmc_shape* shape, const char* path);
EBMC_API int ebmc_read_step(const char* path, ebmc_shape** out);

/* ---- Added for the app's first solid objects (ebmc_occt 0.2; 0.3 added max_triangles to ebmc_tessellate) ---- */

/* Curve types reported by ebmc_edge_geom. */
#define EBMC_CURVE_LINE   0
#define EBMC_CURVE_CIRCLE 1
#define EBMC_CURVE_OTHER  2

/* Geometry of edge edge_index, for picking edges by position and direction: curve type (EBMC_CURVE_*), the point
 * at the middle of the edge's parameter range, the unit tangent there, and the radius for a circle (0 otherwise).
 * A degenerated edge reports EBMC_CURVE_OTHER with its vertex as midpoint and a zero tangent. Any out pointer may
 * be NULL. */
EBMC_API int ebmc_edge_geom(const ebmc_shape* shape, int32_t edge_index, int32_t* curve_type, double* mid_xyz,
                            double* tangent_xyz, double* radius);

/* Returns a transformed copy of shape. m12 is a row-major 3x4 affine matrix {a11,a12,a13,a14, a21,...,a34}
 * mapping (x,y,z) to (a11 x + a12 y + a13 z + a14, ...). A rotation/translation/uniform scale/mirror is applied
 * exactly; any other matrix (non-uniform scale, shear) goes through BRepBuilderAPI_GTransform, which converts
 * the affected surfaces to B-splines. */
EBMC_API int ebmc_transform(const ebmc_shape* shape, const double* m12, ebmc_shape** out);

/* Puts n shapes (n >= 1) into one compound without merging them. The inputs are not modified. */
EBMC_API int ebmc_compound(const ebmc_shape* const* shapes, int32_t n, ebmc_shape** out);

#ifdef __cplusplus
}
#endif

#endif /* EBMC_OCCT_H */
