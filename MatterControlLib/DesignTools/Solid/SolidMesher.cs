using System.Diagnostics;
using MatterHackers.PolygonMesh;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>
	/// The app's mesh for a solid, plus what the kernel knows about it that a plain mesh does not: the B-rep face each
	/// triangle came from (index-aligned with <see cref="Mesh.Faces"/>) and a polyline per B-rep edge. The extra data
	/// is kept in memory only (not saved) for later features such as edge highlighting and picking.
	/// </summary>
	public sealed class SolidMeshResult
	{
		public Mesh Mesh { get; set; }

		/// <summary>B-rep face index of each triangle, same order as Mesh.Faces.</summary>
		public int[] TriangleFaces { get; set; }

		/// <summary>The kernel tessellation the mesh was made from (edge polylines in EdgeIds/EdgeOffsets/EdgePoints).</summary>
		public SolidTessellation Tessellation { get; set; }
	}

	/// <summary>EBMC Edition: turns a kernel tessellation into the app's <see cref="Mesh"/>.</summary>
	public static class SolidMesher
	{
		public static SolidMeshResult ToMesh(SolidTessellation tessellation)
		{
			int dropped = tessellation.Clean(out var triangles, out var triangleFaces);
			if (dropped > 0)
			{
				Trace.WriteLine($"Solid mesh: dropped {dropped} degenerate triangle(s)");
			}

			// Vertices are already welded by the kernel (shared B-rep edges and vertices have one index), so the mesh
			// is closed as it is; the (double[], int[]) constructor keeps the indices and computes the face normals.
			var mesh = new Mesh(tessellation.Vertices, triangles);

			return new SolidMeshResult
			{
				Mesh = mesh,
				TriangleFaces = triangleFaces,
				Tessellation = tessellation,
			};
		}

		/// <summary>
		/// A world matrix as the kernel's row-major 3x4 affine matrix. agg-sharp transforms row vectors (p' = p * M,
		/// translation in row 3), the kernel column vectors (p' = A p + t), so A is the transpose of M's 3x3 part.
		/// </summary>
		public static double[] ToKernelMatrix(Matrix4X4 m)
		{
			return new[]
			{
				m.Row0.X, m.Row1.X, m.Row2.X, m.Row3.X,
				m.Row0.Y, m.Row1.Y, m.Row2.Y, m.Row3.Y,
				m.Row0.Z, m.Row1.Z, m.Row2.Z, m.Row3.Z,
			};
		}
	}
}
