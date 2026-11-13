using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>A kernel step failed; carries the problem it means and the native message (for the log only).</summary>
	public sealed class SolidBuildException : Exception
	{
		public SolidBuildException(SolidProblem problem, int nativeCode, string detail)
			: base(detail)
		{
			Problem = problem;
			NativeCode = nativeCode;
		}

		public SolidProblem Problem { get; }

		public int NativeCode { get; }
	}

	/// <summary>The outcome of one build: a tessellation, or the problem that stopped it. Never throws.</summary>
	public sealed class SolidBuildResult
	{
		public SolidProblem Problem { get; set; }

		/// <summary>Native or exception message, for the log; members see <see cref="SolidMessages"/> text instead.</summary>
		public string Detail { get; set; } = "";

		public SolidTessellation Tessellation { get; set; }

		public int FaceCount { get; set; }

		public int EdgeCount { get; set; }

		/// <summary>Exact volume from the B-rep, mm³ (only filled in when asked for).</summary>
		public double Volume { get; set; }

		/// <summary>xmin, ymin, zmin, xmax, ymax, zmax of the B-rep (only filled in when asked for).</summary>
		public double[] Bounds { get; set; }

		public double ShapeMilliseconds { get; set; }

		public double TessellateMilliseconds { get; set; }

		public bool Succeeded => Problem == SolidProblem.None && Tessellation != null;

		public static SolidBuildResult Failed(SolidProblem problem, string detail = "") => new SolidBuildResult { Problem = problem, Detail = detail ?? "" };
	}

	/// <summary>
	/// EBMC Edition: makes the exact solids with the kernel. Every method either returns a shape the caller owns (and
	/// must dispose) or throws <see cref="SolidBuildException"/>; <see cref="Build"/> wraps a whole build into a
	/// <see cref="SolidBuildResult"/> so nothing escapes. Never call these on the UI thread, and never use one shape
	/// from two threads at once.
	/// </summary>
	internal static class SolidBuilders
	{
		private static T Call<T>(SolidStep step, Func<T> body)
		{
			try
			{
				return body();
			}
			catch (OcctException ex)
			{
				throw new SolidBuildException(SolidMessages.FromKernel(ex.Code, step), ex.Code, ex.Message);
			}
		}

		/// <summary>Geometry of every edge of a shape, in the kernel's edge order.</summary>
		public static List<SolidEdge> Edges(OcctShape shape)
		{
			return Call(SolidStep.Make, () =>
			{
				EbmcOcct.Counts(shape, out _, out int edgeCount, out _);
				var edges = new List<SolidEdge>(edgeCount);
				for (int i = 0; i < edgeCount; i++)
				{
					var info = EbmcOcct.EdgeInfo(shape, i);
					var geom = EbmcOcct.EdgeGeom(shape, i);
					edges.Add(new SolidEdge
					{
						Index = i,
						CurveType = geom.CurveType,
						MidX = geom.MidX,
						MidY = geom.MidY,
						MidZ = geom.MidZ,
						TanX = geom.TanX,
						TanY = geom.TanY,
						TanZ = geom.TanZ,
						Radius = geom.Radius,
						IsDegenerate = info.IsDegenerate,
					});
				}

				return edges;
			});
		}

		private static OcctShape Treat(OcctShape shape, int[] edges, SolidEdgeTreatment treatment, double size)
		{
			if (edges.Length == 0)
			{
				// nothing matched (cannot happen for the shapes made here); hand back an unchanged copy
				return Call(SolidStep.Make, () => EbmcOcct.Translate(shape, 0, 0, 0));
			}

			return treatment == SolidEdgeTreatment.Fillet
				? Call(SolidStep.Fillet, () => EbmcOcct.Fillet(shape, edges, size))
				: Call(SolidStep.Chamfer, () => EbmcOcct.Chamfer(shape, edges, size));
		}

		private static void Require(SolidProblem problem)
		{
			if (problem != SolidProblem.None)
			{
				throw new SolidBuildException(problem, 0, problem.ToString());
			}
		}

		/// <summary>A box centred on the origin, with its edge treatment.</summary>
		public static OcctShape Box(SolidBoxSpec spec)
		{
			Require(spec.Check());
			using (var corner = Call(SolidStep.Make, () => EbmcOcct.MakeBox(spec.Width, spec.Depth, spec.Height)))
			{
				var centred = Call(SolidStep.Make, () => EbmcOcct.Translate(corner, -spec.Width / 2, -spec.Depth / 2, -spec.Height / 2));
				if (!spec.HasEdgeTreatment)
				{
					return centred;
				}

				using (centred)
				{
					var tolerance = SolidEdgeSelection.PositionTolerance(Math.Max(spec.Width, Math.Max(spec.Depth, spec.Height)));
					var edges = SolidEdgeSelection.BoxEdges(Edges(centred), -spec.Height / 2, spec.Height / 2, spec.Edges, tolerance);
					return Treat(centred, edges, spec.Treatment, spec.EdgeSize);
				}
			}
		}

		/// <summary>An upright cylinder centred on the origin, with its rim treatment.</summary>
		public static OcctShape Cylinder(SolidCylinderSpec spec)
		{
			Require(spec.Check());
			using (var based = Call(SolidStep.Make, () => EbmcOcct.MakeCylinder(spec.Diameter / 2, spec.Height)))
			{
				var centred = Call(SolidStep.Make, () => EbmcOcct.Translate(based, 0, 0, -spec.Height / 2));
				if (!spec.HasEdgeTreatment)
				{
					return centred;
				}

				using (centred)
				{
					var tolerance = SolidEdgeSelection.PositionTolerance(Math.Max(spec.Diameter, spec.Height));
					var edges = SolidEdgeSelection.CylinderEdges(Edges(centred), -spec.Height / 2, spec.Height / 2, spec.Diameter / 2, spec.Edges, tolerance);
					return Treat(centred, edges, spec.Treatment, spec.EdgeSize);
				}
			}
		}

		/// <summary>A plate centred on the origin with a round through-hole cut by an exact boolean, optionally countersunk.</summary>
		public static OcctShape Plate(SolidPlateSpec spec)
		{
			Require(spec.Check());
			var radius = spec.HoleDiameter / 2;
			using (var corner = Call(SolidStep.Make, () => EbmcOcct.MakeBox(spec.Width, spec.Depth, spec.Height)))
			using (var plate = Call(SolidStep.Make, () => EbmcOcct.Translate(corner, -spec.Width / 2, -spec.Depth / 2, -spec.Height / 2)))
			using (var rod = Call(SolidStep.Make, () => EbmcOcct.MakeCylinder(radius, spec.Height + 2)))
			using (var tool = Call(SolidStep.Make, () => EbmcOcct.Translate(rod, spec.HoleX, spec.HoleY, -spec.Height / 2 - 1)))
			{
				var drilled = Call(SolidStep.Cut, () => EbmcOcct.Boolean(plate, tool, EbmcOcct.BooleanOp.Cut));
				if (!spec.HasCountersink)
				{
					return drilled;
				}

				using (drilled)
				{
					var tolerance = SolidEdgeSelection.PositionTolerance(Math.Max(spec.Width, Math.Max(spec.Depth, spec.Height)));
					var rim = SolidEdgeSelection.HoleRimEdges(Edges(drilled), spec.Height / 2, spec.HoleX, spec.HoleY, radius, tolerance);
					if (rim.Length == 0)
					{
						throw new SolidBuildException(SolidProblem.KernelRefused, 0, "hole rim edge not found");
					}

					return Call(SolidStep.Countersink, () => EbmcOcct.Chamfer(drilled, rim, spec.RimSize));
				}
			}
		}

		public static OcctShape ReadStep(string path) => Call(SolidStep.ReadStep, () => EbmcOcct.ReadStep(path));

		public static void WriteStep(OcctShape shape, string path) => Call(SolidStep.WriteStep, () => { EbmcOcct.WriteStep(shape, path); return 0; });

		/// <summary>A copy of shape moved by a row-major 3x4 affine matrix.</summary>
		public static OcctShape Transform(OcctShape shape, double[] m12) => Call(SolidStep.Transform, () => EbmcOcct.Transform(shape, m12));

		/// <summary>
		/// One shape for export: a compound of the solids when nothing is a hole (bodies stay separate, nothing is
		/// merged), otherwise the solids fused into one and every hole cut from it, as Combine does with meshes.
		/// </summary>
		public static OcctShape Assemble(IList<OcctShape> solids, IList<OcctShape> holes)
		{
			if (solids == null || solids.Count == 0)
			{
				throw new SolidBuildException(SolidProblem.KernelRefused, 0, "nothing to export");
			}

			if (holes == null || holes.Count == 0)
			{
				return Call(SolidStep.Make, () => EbmcOcct.Compound(solids.ToArray()));
			}

			var result = Call(SolidStep.Make, () => EbmcOcct.Translate(solids[0], 0, 0, 0));
			try
			{
				foreach (var solid in solids.Skip(1))
				{
					var next = Call(SolidStep.Cut, () => EbmcOcct.Boolean(result, solid, EbmcOcct.BooleanOp.Fuse));
					result.Dispose();
					result = next;
				}

				foreach (var hole in holes)
				{
					var next = Call(SolidStep.Cut, () => EbmcOcct.Boolean(result, hole, EbmcOcct.BooleanOp.Cut));
					result.Dispose();
					result = next;
				}

				return result;
			}
			catch
			{
				result.Dispose();
				throw;
			}
		}

		/// <summary>maxTriangles &gt; 0: throws SolidBuildException with SolidProblem.TooDetailed above that many, before anything is copied.</summary>
		public static SolidTessellation Tessellate(OcctShape shape, double linearDeflection, double angularDeflectionRadians, int maxTriangles = 0)
		{
			var mesh = Call(SolidStep.Tessellate, () => EbmcOcct.Tessellate(shape, linearDeflection, angularDeflectionRadians, maxTriangles));
			return new SolidTessellation
			{
				Vertices = mesh.Vertices,
				Triangles = mesh.Triangles,
				TriangleFaces = mesh.TriangleFaces,
				EdgeIds = mesh.EdgeIds,
				EdgeOffsets = mesh.EdgeOffsets,
				EdgePoints = mesh.EdgePoints,
			};
		}

		/// <summary>
		/// Makes a shape, tessellates it with the app's tolerances and returns the result. Never throws: kernel
		/// failures, a missing kernel, cancellation and unexpected exceptions all come back as a problem.
		/// </summary>
		public static SolidBuildResult Build(Func<OcctShape> make, CancellationToken cancellationToken, bool withProperties = false)
		{
			if (!SolidKernel.IsAvailable)
			{
				return SolidBuildResult.Failed(SolidProblem.KernelUnavailable, SolidKernel.UnavailableReason);
			}

			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				var timer = Stopwatch.StartNew();
				using (var shape = make())
				{
					var result = new SolidBuildResult { ShapeMilliseconds = timer.Elapsed.TotalMilliseconds };
					cancellationToken.ThrowIfCancellationRequested();

					timer.Restart();
					var bounds = Call(SolidStep.Tessellate, () => EbmcOcct.BoundingBox(shape));
					var diagonal = Math.Sqrt(
						(bounds[3] - bounds[0]) * (bounds[3] - bounds[0])
						+ (bounds[4] - bounds[1]) * (bounds[4] - bounds[1])
						+ (bounds[5] - bounds[2]) * (bounds[5] - bounds[2]));
					// the kernel refuses past the cap before building any output, so no huge mesh is copied
					result.Tessellation = SolidTolerance.TessellateWithRetry(
						diagonal,
						(linear, degrees) => Tessellate(shape, linear, degrees * Math.PI / 180, SolidTolerance.MaxTriangles),
						ex => ex is SolidBuildException sbe && sbe.Problem == SolidProblem.TooDetailed,
						cancellationToken);

					result.TessellateMilliseconds = timer.Elapsed.TotalMilliseconds;
					if (result.Tessellation == null)
					{
						return SolidBuildResult.Failed(SolidProblem.TooDetailed, "more than " + SolidTolerance.MaxTriangles + " triangles at the coarsest setting");
					}

					Call(SolidStep.Make, () =>
					{
						EbmcOcct.Counts(shape, out int faces, out int edges, out _);
						result.FaceCount = faces;
						result.EdgeCount = edges;
						if (withProperties)
						{
							result.Volume = EbmcOcct.Volume(shape);
							result.Bounds = EbmcOcct.BoundingBox(shape);
						}

						return 0;
					});

					cancellationToken.ThrowIfCancellationRequested();
					return result;
				}
			}
			catch (OperationCanceledException)
			{
				return SolidBuildResult.Failed(SolidProblem.Cancelled);
			}
			catch (SolidBuildException ex)
			{
				return SolidBuildResult.Failed(ex.Problem, ex.Message);
			}
			catch (Exception ex)
			{
				// DllNotFoundException after a successful probe, OutOfMemoryException, a bug here...
				return SolidBuildResult.Failed(SolidProblem.KernelRefused, ex.GetType().Name + ": " + ex.Message);
			}
		}
	}
}
