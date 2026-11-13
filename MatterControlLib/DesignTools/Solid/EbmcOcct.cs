// EbmcOcct.cs - P/Invoke bindings and a thin managed wrapper for libebmc_occt (see native/occt/shim/ebmc_occt.h).
// EBMC Edition: copied from native/occt/dotnet-test/EbmcOcct.cs (the Stage 0 spike), made internal to the app and
// extended with the 0.2 functions (edge geometry, general transform, compound). App code should go through
// SolidKernel / SolidBuilders, which turn every failure into a managed result; this file still throws
// OcctException on a non-zero status.
//
// Written to compile unchanged for netstandard2.0, net6.0 and later: it uses only [DllImport], SafeHandle,
// Marshal.Copy / PtrToStructure<T> and explicit UTF-8 encoding. No LibraryImport, no NativeLibrary, no
// function pointers, no unsafe code. The native library is found by the runtime's normal probing for
// "ebmc_occt" (libebmc_occt.dylib / libebmc_occt.so / ebmc_occt.dll next to the application).

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>A non-zero status returned by libebmc_occt, with the native error message.</summary>
	internal sealed class OcctException : Exception
	{
		public OcctException(int code, string message) : base(message) { Code = code; }

		/// <summary>One of the EBMC_ERR_* values in ebmc_occt.h (see <see cref="EbmcOcct"/> constants).</summary>
		public int Code { get; }
	}

	/// <summary>Owns one native ebmc_shape. Dispose (or let the finalizer run) to free it.</summary>
	internal sealed class OcctShape : SafeHandle
	{
		private OcctShape() : base(IntPtr.Zero, ownsHandle: true) { }

		public override bool IsInvalid => handle == IntPtr.Zero;

		protected override bool ReleaseHandle()
		{
			EbmcOcct.Native.ebmc_shape_free(handle);
			return true;
		}
	}

	/// <summary>Managed copy of an ebmc_mesh (the native buffers are freed as soon as it is copied).</summary>
	internal sealed class OcctMesh
	{
		/// <summary>x,y,z per vertex.</summary>
		public double[] Vertices = new double[0];
		/// <summary>Three 0-based vertex indices per triangle, counter-clockwise from outside.</summary>
		public int[] Triangles = new int[0];
		/// <summary>0-based face index per triangle.</summary>
		public int[] TriangleFaces = new int[0];
		/// <summary>0-based edge index per polyline.</summary>
		public int[] EdgeIds = new int[0];
		/// <summary>EdgeIds.Length + 1 offsets (in points) into EdgePoints.</summary>
		public int[] EdgeOffsets = new int[0];
		/// <summary>x,y,z per polyline point.</summary>
		public double[] EdgePoints = new double[0];

		public int VertexCount => Vertices.Length / 3;
		public int TriangleCount => Triangles.Length / 3;
	}

	internal struct OcctEdgeInfo
	{
		public double StartX, StartY, StartZ, EndX, EndY, EndZ, Length;
		public bool IsDegenerate, IsClosed;
	}

	/// <summary>Result of ebmc_edge_geom: curve type (EbmcOcct.Curve*), midpoint, unit tangent there, circle radius.</summary>
	internal struct OcctEdgeGeom
	{
		public int CurveType;
		public double MidX, MidY, MidZ, TanX, TanY, TanZ, Radius;
	}

	internal static class EbmcOcct
	{
		public const int Ok = 0;
		public const int ErrNullArg = 1;
		public const int ErrInvalidArg = 2;
		public const int ErrIndex = 3;
		public const int ErrNotDone = 4;
		public const int ErrOcct = 5;
		public const int ErrStd = 6;
		public const int ErrUnknown = 7;
		public const int ErrIo = 8;

		public const int CurveLine = 0;
		public const int CurveCircle = 1;
		public const int CurveOther = 2;

		public enum BooleanOp { Fuse = 0, Cut = 1, Common = 2 }

		internal const string LibraryName = "ebmc_occt";

		[StructLayout(LayoutKind.Sequential)]
		internal struct NativeMesh
		{
			public IntPtr vertices;
			public IntPtr triangles;
			public IntPtr triangle_faces;
			public IntPtr edge_ids;
			public IntPtr edge_offsets;
			public IntPtr edge_points;
			public int vertex_count;
			public int triangle_count;
			public int edge_count;
			public int edge_point_count;
		}

		internal static class Native
		{
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr ebmc_occt_version();
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr ebmc_occt_last_error();
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void ebmc_shape_free(IntPtr shape);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_make_box(double dx, double dy, double dz, out OcctShape shape);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_make_cylinder(double r, double h, out OcctShape shape);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_translate(OcctShape s, double dx, double dy, double dz, out OcctShape shape);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_boolean(OcctShape a, OcctShape b, int op, out OcctShape shape);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_shape_counts(OcctShape s, out int faces, out int edges, out int vertices);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_edge_info(OcctShape s, int edge, [Out] double[] start, [Out] double[] end, out double length, out int degenerate, out int closed);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_fillet_edges(OcctShape s, int[] edges, int n, double radius, out OcctShape shape);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_chamfer_edges(OcctShape s, int[] edges, int n, double distance, out OcctShape shape);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_volume(OcctShape s, out double volume);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_area(OcctShape s, out double area);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_bbox(OcctShape s, [Out] double[] out6);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_is_valid(OcctShape s, out int valid);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_tessellate(OcctShape s, double linear, double angular, out IntPtr mesh);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void ebmc_mesh_free(IntPtr mesh);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_write_step(OcctShape s, byte[] utf8Path);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_read_step(byte[] utf8Path, out OcctShape shape);
			// 0.2
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_edge_geom(OcctShape s, int edge, out int curveType, [Out] double[] mid, [Out] double[] tangent, out double radius);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_transform(OcctShape s, double[] m12, out OcctShape shape);
			[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int ebmc_compound(IntPtr[] shapes, int n, out OcctShape shape);
		}

		private static string Utf8String(IntPtr p)
		{
			if (p == IntPtr.Zero) return string.Empty;
			int len = 0;
			while (Marshal.ReadByte(p, len) != 0) len++;
			var bytes = new byte[len];
			Marshal.Copy(p, bytes, 0, len);
			return Encoding.UTF8.GetString(bytes);
		}

		private static byte[] Utf8Z(string s)
		{
			var n = Encoding.UTF8.GetByteCount(s);
			var b = new byte[n + 1];
			Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
			return b;
		}

		/// <summary>Throws <see cref="OcctException"/> if rc is not Ok. Must be called on the same thread as the
		/// failing call, because the native message is thread-local.</summary>
		private static void Check(int rc)
		{
			if (rc != Ok) throw new OcctException(rc, Utf8String(Native.ebmc_occt_last_error()));
		}

		private static OcctShape Result(int rc, OcctShape shape)
		{
			if (rc != Ok)
			{
				shape.Dispose(); // the marshaller always creates the SafeHandle; on failure it wraps NULL
				Check(rc);
			}
			return shape;
		}

		public static string Version => Utf8String(Native.ebmc_occt_version());

		public static OcctShape MakeBox(double dx, double dy, double dz) => Result(Native.ebmc_make_box(dx, dy, dz, out var s), s);
		public static OcctShape MakeCylinder(double r, double h) => Result(Native.ebmc_make_cylinder(r, h, out var s), s);
		public static OcctShape Translate(OcctShape shape, double dx, double dy, double dz) => Result(Native.ebmc_translate(shape, dx, dy, dz, out var s), s);
		public static OcctShape Boolean(OcctShape a, OcctShape b, BooleanOp op) => Result(Native.ebmc_boolean(a, b, (int)op, out var s), s);
		public static OcctShape Fillet(OcctShape shape, int[] edges, double radius) => Result(Native.ebmc_fillet_edges(shape, edges, edges.Length, radius, out var s), s);
		public static OcctShape Chamfer(OcctShape shape, int[] edges, double distance) => Result(Native.ebmc_chamfer_edges(shape, edges, edges.Length, distance, out var s), s);
		public static OcctShape ReadStep(string path) => Result(Native.ebmc_read_step(Utf8Z(path), out var s), s);
		public static void WriteStep(OcctShape shape, string path) => Check(Native.ebmc_write_step(shape, Utf8Z(path)));

		public static void Counts(OcctShape shape, out int faces, out int edges, out int vertices) => Check(Native.ebmc_shape_counts(shape, out faces, out edges, out vertices));

		public static OcctEdgeInfo EdgeInfo(OcctShape shape, int edge)
		{
			var a = new double[3];
			var b = new double[3];
			Check(Native.ebmc_edge_info(shape, edge, a, b, out var len, out var deg, out var closed));
			return new OcctEdgeInfo
			{
				StartX = a[0], StartY = a[1], StartZ = a[2], EndX = b[0], EndY = b[1], EndZ = b[2],
				Length = len, IsDegenerate = deg != 0, IsClosed = closed != 0,
			};
		}

		public static OcctEdgeGeom EdgeGeom(OcctShape shape, int edge)
		{
			var mid = new double[3];
			var tangent = new double[3];
			Check(Native.ebmc_edge_geom(shape, edge, out var type, mid, tangent, out var radius));
			return new OcctEdgeGeom
			{
				CurveType = type, MidX = mid[0], MidY = mid[1], MidZ = mid[2], TanX = tangent[0], TanY = tangent[1], TanZ = tangent[2], Radius = radius,
			};
		}

		/// <summary>Row-major 3x4 affine matrix (see ebmc_transform).</summary>
		public static OcctShape Transform(OcctShape shape, double[] m12)
		{
			if (m12 == null || m12.Length != 12) throw new ArgumentException("need 12 values", nameof(m12));
			return Result(Native.ebmc_transform(shape, m12, out var s), s);
		}

		public static OcctShape Compound(OcctShape[] shapes)
		{
			if (shapes == null || shapes.Length == 0) throw new ArgumentException("need at least one shape", nameof(shapes));
			var pointers = new IntPtr[shapes.Length];
			var added = new bool[shapes.Length];
			try
			{
				for (int i = 0; i < shapes.Length; i++)
				{
					shapes[i].DangerousAddRef(ref added[i]);
					pointers[i] = shapes[i].DangerousGetHandle();
				}

				return Result(Native.ebmc_compound(pointers, pointers.Length, out var s), s);
			}
			finally
			{
				for (int i = 0; i < shapes.Length; i++)
				{
					if (added[i]) shapes[i].DangerousRelease();
				}
			}
		}

		public static double Volume(OcctShape shape) { Check(Native.ebmc_volume(shape, out var v)); return v; }
		public static double Area(OcctShape shape) { Check(Native.ebmc_area(shape, out var a)); return a; }
		public static double[] BoundingBox(OcctShape shape) { var b = new double[6]; Check(Native.ebmc_bbox(shape, b)); return b; }
		public static bool IsValid(OcctShape shape) { Check(Native.ebmc_is_valid(shape, out var v)); return v != 0; }

		public static OcctMesh Tessellate(OcctShape shape, double linearDeflection, double angularDeflectionRadians)
		{
			Check(Native.ebmc_tessellate(shape, linearDeflection, angularDeflectionRadians, out var p));
			try
			{
				var n = Marshal.PtrToStructure<NativeMesh>(p);
				var m = new OcctMesh
				{
					Vertices = new double[n.vertex_count * 3],
					Triangles = new int[n.triangle_count * 3],
					TriangleFaces = new int[n.triangle_count],
					EdgeIds = new int[n.edge_count],
					EdgeOffsets = new int[n.edge_count + 1],
					EdgePoints = new double[n.edge_point_count * 3],
				};
				Marshal.Copy(n.vertices, m.Vertices, 0, m.Vertices.Length);
				Marshal.Copy(n.triangles, m.Triangles, 0, m.Triangles.Length);
				Marshal.Copy(n.triangle_faces, m.TriangleFaces, 0, m.TriangleFaces.Length);
				Marshal.Copy(n.edge_ids, m.EdgeIds, 0, m.EdgeIds.Length);
				Marshal.Copy(n.edge_offsets, m.EdgeOffsets, 0, m.EdgeOffsets.Length);
				Marshal.Copy(n.edge_points, m.EdgePoints, 0, m.EdgePoints.Length);
				return m;
			}
			finally
			{
				Native.ebmc_mesh_free(p);
			}
		}
	}
}
