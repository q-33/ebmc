using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	// EBMC Edition: the rules behind the exact-solid objects (DesignTools/Solid), kept free of the GUI, agg-sharp and
	// the native library so Tests/EBMC.Tests can check them directly: which B-rep edges an "Edges" choice means,
	// parameter limits, and the plain-words message for each way a build can fail.
	// Enum member names are shown in the property editor with '_' replaced by a space, and are stored in .mcx files
	// by value, so only ever append members.

	public enum SolidEdgeTreatment
	{
		None,
		Fillet,
		Chamfer
	}

	public enum SolidBoxEdges
	{
		All,
		Vertical_Only,
		Top_Only,
		Top_And_Bottom
	}

	public enum SolidCylinderEdges
	{
		Top,
		Bottom,
		Both
	}

	public enum SolidHoleRim
	{
		Plain,
		Countersink
	}

	public enum SolidShapeKind
	{
		Box,
		Cylinder,
		Plate
	}

	/// <summary>Why a solid could not be (re)built. The previous mesh is kept in every case.</summary>
	public enum SolidProblem
	{
		None,
		KernelUnavailable,
		InvalidSize,
		FilletTooLarge,
		ChamferTooLarge,
		HoleDoesNotFit,
		CountersinkTooLarge,
		KernelRefused,
		StepUnreadable,
		StepNotWritten,
		Cancelled,
		TooDetailed,
		StepTooLarge
	}

	/// <summary>The kernel step a native status code came from, to turn that code into a <see cref="SolidProblem"/>.</summary>
	public enum SolidStep
	{
		Make,
		Fillet,
		Chamfer,
		Cut,
		Countersink,
		Tessellate,
		ReadStep,
		WriteStep,
		Transform
	}

	/// <summary>Geometry of one B-rep edge as reported by the kernel (ebmc_edge_info + ebmc_edge_geom).</summary>
	public struct SolidEdge
	{
		public const int CurveLine = 0;
		public const int CurveCircle = 1;
		public const int CurveOther = 2;

		public int Index;
		public int CurveType;
		public double MidX, MidY, MidZ;
		public double TanX, TanY, TanZ;
		public double Radius;
		public bool IsDegenerate;

		public static SolidEdge Line(int index, double midX, double midY, double midZ, double tanX, double tanY, double tanZ)
		{
			return new SolidEdge { Index = index, CurveType = CurveLine, MidX = midX, MidY = midY, MidZ = midZ, TanX = tanX, TanY = tanY, TanZ = tanZ };
		}

		/// <summary>A circle in a horizontal plane at height z (its midpoint lies on the circle).</summary>
		public static SolidEdge Circle(int index, double centerX, double centerY, double z, double radius)
		{
			return new SolidEdge { Index = index, CurveType = CurveCircle, MidX = centerX + radius, MidY = centerY, MidZ = z, TanX = 0, TanY = 1, TanZ = 0, Radius = radius };
		}
	}

	/// <summary>
	/// Picks the edges an "Edges" choice means, by geometry only (curve type, direction, height), never by remembered
	/// indices, so the choice survives any change of size. Results are sorted ascending and contain no duplicates.
	/// </summary>
	public static class SolidEdgeSelection
	{
		/// <summary>Tolerance on unit-vector components when deciding vertical / horizontal.</summary>
		public const double DirectionTolerance = 1e-6;

		/// <summary>Position tolerance in mm, scaled up for big parts.</summary>
		public static double PositionTolerance(double largestSize) => Math.Max(1e-6, 1e-7 * largestSize);

		private static bool IsVertical(SolidEdge e) => !e.IsDegenerate && e.CurveType == SolidEdge.CurveLine && Math.Abs(Math.Abs(e.TanZ) - 1) < DirectionTolerance;

		private static bool IsHorizontal(SolidEdge e) => !e.IsDegenerate && Math.Abs(e.TanZ) < DirectionTolerance;

		private static bool At(double value, double target, double tolerance) => Math.Abs(value - target) <= tolerance;

		/// <summary>Edges of a box running from zMin to zMax (all its edges are straight lines).</summary>
		public static int[] BoxEdges(IEnumerable<SolidEdge> edges, double zMin, double zMax, SolidBoxEdges which, double tolerance)
		{
			bool Wanted(SolidEdge e)
			{
				if (e.IsDegenerate || e.CurveType != SolidEdge.CurveLine)
				{
					return false;
				}

				bool vertical = IsVertical(e);
				bool top = IsHorizontal(e) && At(e.MidZ, zMax, tolerance);
				bool bottom = IsHorizontal(e) && At(e.MidZ, zMin, tolerance);

				switch (which)
				{
					case SolidBoxEdges.All:
						return vertical || top || bottom;
					case SolidBoxEdges.Vertical_Only:
						return vertical;
					case SolidBoxEdges.Top_Only:
						return top;
					case SolidBoxEdges.Top_And_Bottom:
						return top || bottom;
					default:
						return false;
				}
			}

			return Sorted(edges.Where(Wanted));
		}

		/// <summary>The rim circles of an upright cylinder running from zMin to zMax.</summary>
		public static int[] CylinderEdges(IEnumerable<SolidEdge> edges, double zMin, double zMax, double radius, SolidCylinderEdges which, double tolerance)
		{
			bool Wanted(SolidEdge e)
			{
				if (e.IsDegenerate || e.CurveType != SolidEdge.CurveCircle || !IsHorizontal(e) || !At(e.Radius, radius, tolerance))
				{
					return false;
				}

				bool top = At(e.MidZ, zMax, tolerance);
				bool bottom = At(e.MidZ, zMin, tolerance);
				switch (which)
				{
					case SolidCylinderEdges.Top:
						return top;
					case SolidCylinderEdges.Bottom:
						return bottom;
					case SolidCylinderEdges.Both:
						return top || bottom;
					default:
						return false;
				}
			}

			return Sorted(edges.Where(Wanted));
		}

		/// <summary>
		/// The top rim of a round hole of the given radius centred at (centerX, centerY): a horizontal circle at zTop
		/// whose midpoint is radius away from the centre.
		/// </summary>
		public static int[] HoleRimEdges(IEnumerable<SolidEdge> edges, double zTop, double centerX, double centerY, double radius, double tolerance)
		{
			bool Wanted(SolidEdge e)
			{
				if (e.IsDegenerate || e.CurveType != SolidEdge.CurveCircle || !IsHorizontal(e))
				{
					return false;
				}

				var distance = Math.Sqrt((e.MidX - centerX) * (e.MidX - centerX) + (e.MidY - centerY) * (e.MidY - centerY));
				return At(e.MidZ, zTop, tolerance)
					&& At(e.Radius, radius, tolerance)
					&& At(distance, radius, Math.Max(tolerance, 1e-6 * radius));
			}

			return Sorted(edges.Where(Wanted));
		}

		private static int[] Sorted(IEnumerable<SolidEdge> edges) => edges.Select(e => e.Index).Distinct().OrderBy(i => i).ToArray();
	}

	/// <summary>Parameter limits. Sizes are in mm.</summary>
	public static class SolidLimits
	{
		public const double MinSize = 0.01;

		public const double MaxSize = 10000;

		/// <summary>Hole edges must stay at least this far inside the plate.</summary>
		public const double MinWall = 0.01;

		/// <summary>value limited to [min, max]; NaN or infinity stays NaN, so the spec's Check() reports it.</summary>
		public static double ClampOrNaN(double value, double min, double max)
		{
			return double.IsNaN(value) || double.IsInfinity(value) ? double.NaN : Math.Min(max, Math.Max(min, value));
		}

		/// <summary>True when any value is NaN or infinite.</summary>
		public static bool AnyInvalid(params double[] values) => values.Any(v => double.IsNaN(v) || double.IsInfinity(v));

		public static double ClampSize(double value) => double.IsNaN(value) ? MinSize : Math.Min(MaxSize, Math.Max(MinSize, value));

		/// <summary>Edge sizes (fillet radius, chamfer distance, countersink) are never negative or huge.</summary>
		public static double ClampEdgeSize(double value) => double.IsNaN(value) ? 0 : Math.Min(MaxSize, Math.Max(0, value));

		/// <summary>Hole offsets are limited to the plate size range; whether the hole fits is checked separately.</summary>
		public static double ClampOffset(double value) => double.IsNaN(value) ? 0 : Math.Min(MaxSize, Math.Max(-MaxSize, value));

		/// <summary>
		/// Largest fillet radius / chamfer distance the selected box edges can take, exclusive. A fillet or chamfer of
		/// half the box's smallest relevant side or more makes faces vanish, which the kernel refuses.
		/// </summary>
		public static double MaxBoxEdgeSize(double width, double depth, double height, SolidBoxEdges edges)
		{
			var horizontal = Math.Min(width, depth) / 2;
			switch (edges)
			{
				case SolidBoxEdges.Vertical_Only:
					return horizontal;
				case SolidBoxEdges.Top_Only:
					return Math.Min(horizontal, height);
				default:
					return Math.Min(horizontal, height / 2);
			}
		}

		/// <summary>Largest rim size for a cylinder, exclusive: the radius, and the height (half of it when both rims are done).</summary>
		public static double MaxCylinderEdgeSize(double diameter, double height, SolidCylinderEdges edges)
		{
			return Math.Min(diameter / 2, edges == SolidCylinderEdges.Both ? height / 2 : height);
		}

		/// <summary>True when a round hole of this diameter at this offset from the plate's centre lies inside the plate.</summary>
		public static bool HoleFits(double width, double depth, double diameter, double offsetX, double offsetY)
		{
			var r = diameter / 2;
			return Math.Abs(offsetX) + r <= width / 2 - MinWall
				&& Math.Abs(offsetY) + r <= depth / 2 - MinWall;
		}

		/// <summary>Largest countersink (45° chamfer distance), exclusive: the plate thickness and the wall left around the hole.</summary>
		public static double MaxCountersink(double width, double depth, double height, double diameter, double offsetX, double offsetY)
		{
			var r = diameter / 2;
			var wall = Math.Min(width / 2 - Math.Abs(offsetX) - r, depth / 2 - Math.Abs(offsetY) - r);
			return Math.Max(0, Math.Min(height, wall));
		}
	}

	/// <summary>A validated box: what SolidBuilders.Box makes. Centred on the origin like the Cube primitive.</summary>
	public sealed class SolidBoxSpec
	{
		public double Width, Depth, Height;
		public SolidEdgeTreatment Treatment;
		public double EdgeSize;
		public SolidBoxEdges Edges;

		/// <summary>The problem that stops this box being built, checked without the kernel; None when it may be built.</summary>
		public SolidProblem Check()
		{
			if (!(Width > 0 && Depth > 0 && Height > 0) || SolidLimits.AnyInvalid(Width, Depth, Height, EdgeSize))
			{
				return SolidProblem.InvalidSize;
			}

			if (Treatment != SolidEdgeTreatment.None
				&& EdgeSize > 0
				&& EdgeSize >= SolidLimits.MaxBoxEdgeSize(Width, Depth, Height, Edges))
			{
				return Treatment == SolidEdgeTreatment.Fillet ? SolidProblem.FilletTooLarge : SolidProblem.ChamferTooLarge;
			}

			return SolidProblem.None;
		}

		/// <summary>True when an edge treatment is actually applied (a size of 0 means none).</summary>
		public bool HasEdgeTreatment => Treatment != SolidEdgeTreatment.None && EdgeSize > 0;
	}

	/// <summary>A validated upright cylinder, centred on the origin.</summary>
	public sealed class SolidCylinderSpec
	{
		public double Diameter, Height;
		public SolidEdgeTreatment Treatment;
		public double EdgeSize;
		public SolidCylinderEdges Edges;

		public SolidProblem Check()
		{
			if (!(Diameter > 0 && Height > 0) || SolidLimits.AnyInvalid(Diameter, Height, EdgeSize))
			{
				return SolidProblem.InvalidSize;
			}

			if (HasEdgeTreatment && EdgeSize >= SolidLimits.MaxCylinderEdgeSize(Diameter, Height, Edges))
			{
				return Treatment == SolidEdgeTreatment.Fillet ? SolidProblem.FilletTooLarge : SolidProblem.ChamferTooLarge;
			}

			return SolidProblem.None;
		}

		public bool HasEdgeTreatment => Treatment != SolidEdgeTreatment.None && EdgeSize > 0;
	}

	/// <summary>A validated plate with one round through-hole, centred on the origin.</summary>
	public sealed class SolidPlateSpec
	{
		public double Width, Depth, Height;
		public double HoleDiameter, HoleX, HoleY;
		public SolidHoleRim Rim;
		public double RimSize;

		public SolidProblem Check()
		{
			if (!(Width > 0 && Depth > 0 && Height > 0 && HoleDiameter > 0) || SolidLimits.AnyInvalid(Width, Depth, Height, HoleDiameter, HoleX, HoleY, RimSize))
			{
				return SolidProblem.InvalidSize;
			}

			if (!SolidLimits.HoleFits(Width, Depth, HoleDiameter, HoleX, HoleY))
			{
				return SolidProblem.HoleDoesNotFit;
			}

			if (HasCountersink && RimSize >= SolidLimits.MaxCountersink(Width, Depth, Height, HoleDiameter, HoleX, HoleY))
			{
				return SolidProblem.CountersinkTooLarge;
			}

			return SolidProblem.None;
		}

		public bool HasCountersink => Rim == SolidHoleRim.Countersink && RimSize > 0;
	}

	/// <summary>
	/// How finely solids are turned into triangles. The chord error is at most <see cref="MinLinear"/> mm or 1/10,000 of
	/// the part's diagonal, whichever is larger (so a 2 m part is not cut into tens of millions of triangles), with at most
	/// 5° between neighbouring facets. A mesh over <see cref="MaxTriangles"/> is redone once, four times coarser and at
	/// 15°; if that is still too big the part is refused with a message.
	/// Measured on a 100 mm box with every edge filleted at 5 mm (SolidKernelTests): 11,940 triangles, about 36 ms for
	/// shape plus mesh; 2° gave 70,372 triangles / 220 ms and 0.5° gave 1,076,132 triangles / 16 s.
	/// </summary>
	public static class SolidTolerance
	{
		public const double MinLinear = 0.02;

		public const double RelativeLinear = 1e-4;

		public const double AngularDegrees = 5;

		public const double CoarseFactor = 4;

		public const double CoarseAngularDegrees = 15;

		public const int Attempts = 2;

		public const int MaxTriangles = 2000000;

		/// <summary>STEP files over this size are refused rather than read into memory.</summary>
		public const long MaxStepFileBytes = 50L * 1024 * 1024;

		/// <summary>
		/// Runs <paramref name="tessellate"/> (linear mm, angular degrees) at each setting in turn, finest first, and returns
		/// the first result; an attempt that throws something <paramref name="isTooBig"/> accepts moves on to the next.
		/// Returns null when every setting was too big. Any other exception, and cancellation (checked before each
		/// attempt), goes to the caller.
		/// </summary>
		public static T TessellateWithRetry<T>(double diagonal, Func<double, double, T> tessellate, Func<Exception, bool> isTooBig, System.Threading.CancellationToken cancellationToken)
			where T : class
		{
			for (int attempt = 0; attempt < Attempts; attempt++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var (linear, degrees) = For(diagonal, attempt);
				try
				{
					return tessellate(linear, degrees);
				}
				catch (Exception ex) when (isTooBig(ex))
				{
					// try the coarser setting
				}
			}

			return null;
		}

		/// <summary>Linear (mm) and angular (degrees) deflection for a part with this bounding-box diagonal.</summary>
		public static (double linear, double angularDegrees) For(double diagonal, int attempt)
		{
			var linear = Math.Max(MinLinear, (double.IsNaN(diagonal) || diagonal < 0 ? 0 : diagonal) * RelativeLinear);
			return attempt <= 0 ? (linear, AngularDegrees) : (linear * CoarseFactor, CoarseAngularDegrees);
		}
	}

	/// <summary>What a finished rebuild does with its mesh (SolidObject3D.Apply).</summary>
	public static class SolidApplyRule
	{
		/// <summary>
		/// A successful build always puts its mesh on the part, even one that has left its parent's children (deleted
		/// while building: Undo re-adds the same object, which must match its values). The parents are told only while
		/// the part is actually among their children (a part with no parent at all, not yet added, has none to tell).
		/// </summary>
		public static (bool assignMesh, bool notifyParents) ForFinishedBuild(bool succeeded, bool hasParent, bool inParentsChildren)
		{
			if (!succeeded)
			{
				return (false, false);
			}

			return (true, hasParent && inParentsChildren);
		}
	}

	public enum StepExportOutcome
	{
		Saved,
		Failed,
		Cancelled
	}

	/// <summary>Which selections the STEP export applies to (the export dialog asks this for every selection).</summary>
	public static class StepExportRules
	{
		/// <summary>
		/// The exact solids under root, each with its parents from root down (root first, the solid itself excluded), or
		/// null when root holds anything else that would be exported: a part with its own mesh that is not a solid, or an
		/// operation (Combine, ...) whose visible result is a mesh. Empty containers add nothing; a root with no solid at
		/// all gives null.
		/// </summary>
		public static List<(T item, List<T> ancestors)> Collect<T>(T root, Func<T, bool> isSolid, Func<T, bool> hasOwnMesh, Func<T, IEnumerable<T>> visibleChildren)
		{
			var found = new List<(T, List<T>)>();
			var path = new List<T>();

			bool Walk(T item)
			{
				if (isSolid(item))
				{
					found.Add((item, new List<T>(path)));
					return true;
				}

				if (hasOwnMesh(item))
				{
					return false;
				}

				path.Add(item);
				foreach (var child in visibleChildren(item) ?? Enumerable.Empty<T>())
				{
					if (!Walk(child))
					{
						return false;
					}
				}

				path.RemoveAt(path.Count - 1);
				return true;
			}

			return Walk(root) && found.Count > 0 ? found : null;
		}

		/// <summary>ID of the result Generate returns when the export was cancelled.</summary>
		public const string CancelledId = "ItemToSTEPExportCancelled";

		/// <summary>ID of the result Generate returns when the export failed.</summary>
		public const string FailedId = "ItemToSTEPExportInvalid";

		/// <summary>What a Generate result means: nothing returned is a saved file; the cancel marker is a cancel; anything else failed.</summary>
		public static StepExportOutcome Classify(IEnumerable<string> resultIds)
		{
			var ids = resultIds?.ToList();
			if (ids == null || ids.Count == 0)
			{
				return StepExportOutcome.Saved;
			}

			return ids.All(id => id == CancelledId) ? StepExportOutcome.Cancelled : StepExportOutcome.Failed;
		}

		/// <summary>True when there is at least one root and every root qualifies.</summary>
		public static bool Applies(IEnumerable<bool> rootsQualify)
		{
			if (rootsQualify == null)
			{
				return false;
			}

			var list = rootsQualify.ToList();
			return list.Count > 0 && list.All(q => q);
		}
	}

	/// <summary>Turns a native status code into a <see cref="SolidProblem"/> and every problem into plain words.</summary>
	public static class SolidMessages
	{
		// native status codes (ebmc_occt.h)
		public const int ErrInvalidArg = 2;
		public const int ErrIndex = 3;
		public const int ErrNotDone = 4;
		public const int ErrOcct = 5;
		public const int ErrIo = 8;
		public const int ErrTooBig = 9;

		/// <summary>The problem behind a non-zero native status from <paramref name="step"/>.</summary>
		public static SolidProblem FromKernel(int code, SolidStep step)
		{
			if (code == 0)
			{
				return SolidProblem.None;
			}

			if (code == ErrTooBig)
			{
				return SolidProblem.TooDetailed;
			}

			if (code == ErrInvalidArg && step != SolidStep.ReadStep && step != SolidStep.WriteStep)
			{
				return SolidProblem.InvalidSize;
			}

			switch (step)
			{
				case SolidStep.Fillet:
					return code == ErrNotDone || code == ErrOcct ? SolidProblem.FilletTooLarge : SolidProblem.KernelRefused;
				case SolidStep.Chamfer:
					return code == ErrNotDone || code == ErrOcct ? SolidProblem.ChamferTooLarge : SolidProblem.KernelRefused;
				case SolidStep.Countersink:
					return code == ErrNotDone || code == ErrOcct ? SolidProblem.CountersinkTooLarge : SolidProblem.KernelRefused;
				case SolidStep.ReadStep:
					return SolidProblem.StepUnreadable;
				case SolidStep.WriteStep:
					return SolidProblem.StepNotWritten;
				default:
					return SolidProblem.KernelRefused;
			}
		}

		private static string Noun(SolidShapeKind kind)
		{
			switch (kind)
			{
				case SolidShapeKind.Cylinder:
					return "cylinder";
				case SolidShapeKind.Plate:
					return "plate";
				default:
					return "box";
			}
		}

		/// <summary>
		/// The message for a problem, or null for None and Cancelled. <paramref name="localize"/> is applied to each
		/// English template ("s => s.Localize()" in the app, "s => s" in tests).
		/// </summary>
		public static string For(SolidProblem problem, SolidShapeKind kind, Func<string, string> localize, string fileName = null)
		{
			localize = localize ?? (s => s);
			var noun = localize(Noun(kind));
			switch (problem)
			{
				case SolidProblem.None:
				case SolidProblem.Cancelled:
					return null;
				case SolidProblem.KernelUnavailable:
					return localize("Exact solids are switched off or not available in this copy of the app. The shape keeps its last saved form, and its sizes cannot be changed here.");
				case SolidProblem.InvalidSize:
					return string.Format(localize("Every size of the {0} must be more than zero."), noun);
				case SolidProblem.FilletTooLarge:
					return string.Format(localize("That fillet is too large for this {0}. The previous shape is kept."), noun);
				case SolidProblem.ChamferTooLarge:
					return string.Format(localize("That chamfer is too large for this {0}. The previous shape is kept."), noun);
				case SolidProblem.HoleDoesNotFit:
					return localize("The hole does not fit inside the plate. Make it smaller or move it nearer the middle.");
				case SolidProblem.CountersinkTooLarge:
					return localize("That countersink is too large for this plate. The previous shape is kept.");
				case SolidProblem.StepUnreadable:
					return string.Format(localize("Couldn't read {0}: the STEP file is damaged or has no solid in it."), string.IsNullOrEmpty(fileName) ? localize("the file") : fileName);
				case SolidProblem.TooDetailed:
					return localize("This part is too detailed to show, even at a coarser setting.");
				case SolidProblem.StepTooLarge:
					return string.Format(localize("{0} is larger than 50 MB, too large to open as a STEP part."), string.IsNullOrEmpty(fileName) ? localize("the file") : fileName);
				case SolidProblem.StepNotWritten:
					return string.Format(localize("Couldn't save {0} as a STEP file."), string.IsNullOrEmpty(fileName) ? localize("the file") : fileName);
				default:
					return string.Format(localize("The {0} could not be built with these values. The previous shape is kept."), noun);
			}
		}
	}

	/// <summary>
	/// A triangle mesh as the kernel returns it (welded vertices, counter-clockwise triangles, a face id per triangle
	/// and a polyline per B-rep edge), plus the clean-up the app's mesh needs. Pure data, so it can be tested with a
	/// made-up tessellation.
	/// </summary>
	public sealed class SolidTessellation
	{
		/// <summary>x,y,z per vertex.</summary>
		public double[] Vertices = new double[0];

		/// <summary>Three vertex indices per triangle.</summary>
		public int[] Triangles = new int[0];

		/// <summary>B-rep face index per triangle.</summary>
		public int[] TriangleFaces = new int[0];

		/// <summary>B-rep edge index per polyline.</summary>
		public int[] EdgeIds = new int[0];

		/// <summary>EdgeIds.Length + 1 offsets, in points, into EdgePoints.</summary>
		public int[] EdgeOffsets = new int[0];

		/// <summary>x,y,z per polyline point.</summary>
		public double[] EdgePoints = new double[0];

		public int VertexCount => Vertices.Length / 3;

		public int TriangleCount => Triangles.Length / 3;

		/// <summary>
		/// The triangles the app's mesh gets: drops any triangle with an index out of range or two equal indices (it
		/// has no area and no normal) and keeps the face id array in step with the triangles that remain.
		/// Returns the number of triangles dropped.
		/// </summary>
		public int Clean(out int[] triangles, out int[] triangleFaces)
		{
			int vertexCount = VertexCount;
			int count = TriangleCount;
			bool haveFaces = TriangleFaces != null && TriangleFaces.Length == count;
			var keptTriangles = new List<int>(count * 3);
			var keptFaces = new List<int>(count);
			int dropped = 0;

			for (int t = 0; t < count; t++)
			{
				int a = Triangles[3 * t];
				int b = Triangles[3 * t + 1];
				int c = Triangles[3 * t + 2];
				if (a < 0 || b < 0 || c < 0
					|| a >= vertexCount || b >= vertexCount || c >= vertexCount
					|| a == b || b == c || a == c)
				{
					dropped++;
					continue;
				}

				keptTriangles.Add(a);
				keptTriangles.Add(b);
				keptTriangles.Add(c);
				keptFaces.Add(haveFaces ? TriangleFaces[t] : -1);
			}

			triangles = keptTriangles.ToArray();
			triangleFaces = keptFaces.ToArray();
			return dropped;
		}

		/// <summary>Points of polyline i (x,y,z triples), or an empty array if the offsets are out of range.</summary>
		public double[] EdgePolyline(int i)
		{
			if (i < 0 || i + 1 >= EdgeOffsets.Length)
			{
				return new double[0];
			}

			int start = EdgeOffsets[i];
			int end = EdgeOffsets[i + 1];
			if (start < 0 || end < start || end * 3 > EdgePoints.Length)
			{
				return new double[0];
			}

			var points = new double[(end - start) * 3];
			Array.Copy(EdgePoints, start * 3, points, 0, points.Length);
			return points;
		}

		/// <summary>Number of mesh edges used by exactly one triangle (0 for a closed mesh) and by more than two.</summary>
		public static void CountOpenEdges(int[] triangles, out int open, out int nonManifold)
		{
			var uses = new Dictionary<long, int>();
			for (int t = 0; t + 2 < triangles.Length; t += 3)
			{
				for (int k = 0; k < 3; k++)
				{
					long a = triangles[t + k];
					long b = triangles[t + (k + 1) % 3];
					long key = a < b ? (a << 32) | b : (b << 32) | a;
					uses.TryGetValue(key, out int n);
					uses[key] = n + 1;
				}
			}

			open = uses.Values.Count(n => n == 1);
			nonManifold = uses.Values.Count(n => n > 2);
		}
	}
}
