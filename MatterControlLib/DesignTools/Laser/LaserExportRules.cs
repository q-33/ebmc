using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools.Laser
{
	/// <summary>What a laser export's Generate result means.</summary>
	public enum LaserExportOutcome
	{
		Saved,
		SavedWithNotes,
		Cancelled,
		Failed,
	}

	/// <summary>Where a part's 2D outline comes from.</summary>
	public enum LaserSourceKind
	{
		Mesh,
		Path,
	}

	/// <summary>
	/// EBMC Edition: the rules and wording for "Export for laser" (SVG or DXF outlines). Pure: no agg-sharp, no GUI, so
	/// Tests/EBMC.Tests checks them. The exporter writes a file only; nothing here slices, sends or prints.
	/// </summary>
	public static class LaserExportRules
	{
		/// <summary>ID of the result Generate returns when the export failed.</summary>
		public const string FailedId = "ItemToLaserExportInvalid";

		/// <summary>ID of the result Generate returns when there was nothing to cut (no file is written).</summary>
		public const string EmptyId = "ItemToLaserExportEmpty";

		/// <summary>ID of the result Generate returns when the export was cancelled.</summary>
		public const string CancelledId = "ItemToLaserExportCancelled";

		/// <summary>ID of a note about a saved file (a hole left out, a gap closed...).</summary>
		public const string NoteId = "ItemToLaserExportNote";

		public static LaserExportOutcome Classify(IEnumerable<string> resultIds)
		{
			var ids = resultIds?.ToList();
			if (ids == null || ids.Count == 0)
			{
				return LaserExportOutcome.Saved;
			}

			if (ids.All(id => id == CancelledId))
			{
				return LaserExportOutcome.Cancelled;
			}

			return ids.All(id => id == NoteId) ? LaserExportOutcome.SavedWithNotes : LaserExportOutcome.Failed;
		}

		/// <summary>The title of the box that lists Generate's result.</summary>
		public static string ResultTitle(LaserExportOutcome outcome)
		{
			switch (outcome)
			{
				case LaserExportOutcome.Cancelled:
					return "Export Cancelled";
				case LaserExportOutcome.SavedWithNotes:
					return "Export Saved";
				default:
					return "Export Error";
			}
		}

		/// <summary>True when there is something to export: at least one object, all of them already in memory.</summary>
		public static bool Applies<T>(IReadOnlyList<T> roots) => roots != null && roots.Count > 0 && roots.All(r => r != null);

		/// <summary>
		/// The parts under root that give an outline, each with where it comes from and whether it is a hole. Walks the
		/// visible children: a pure 2D drawing lying flat (its mesh only shows the path: drawn paths, 2D text, image
		/// traces) gives its path, with smooth curves; otherwise a part with its own mesh gives its mesh (and its children
		/// are not visited, as for STL export); otherwise its children are visited. Solid parts that also have a path (an
		/// extrusion, an image base) are not drawings: they go through their mesh, or their children when they have none,
		/// so a part standing on a base is not lost and a bevel shows. Each part is taken once. A part whose role is null
		/// (support, wipe tower, fuzzy) is skipped with everything under it.
		/// </summary>
		public static List<(T item, LaserSourceKind kind, LaserRole role)> Collect<T>(
			T root,
			Func<T, bool> hasMesh,
			Func<T, bool> isFlatDrawing,
			Func<T, IEnumerable<T>> visibleChildren,
			Func<T, LaserRole?> roleOf)
		{
			var found = new List<(T, LaserSourceKind, LaserRole)>();
			var stack = new Stack<T>();
			stack.Push(root);
			while (stack.Count > 0)
			{
				var item = stack.Pop();
				var role = roleOf(item);
				if (role == null)
				{
					continue;
				}

				if (isFlatDrawing(item))
				{
					found.Add((item, LaserSourceKind.Path, role.Value));
				}
				else if (hasMesh(item))
				{
					found.Add((item, LaserSourceKind.Mesh, role.Value));
				}
				else
				{
					var children = (visibleChildren(item) ?? Enumerable.Empty<T>()).ToList();
					for (int i = children.Count - 1; i >= 0; i--)
					{
						stack.Push(children[i]);
					}
				}
			}

			return found;
		}

		/// <summary>
		/// True when a path drawn in its part's XY plane stays flat on the bed under this affine (row-major 3x4, as
		/// SolidMesher.ToKernelMatrix): Z maps to ±Z, and X and Y get no Z component. Null is identity.
		/// </summary>
		public static bool PathIsFlat(double[] affine)
		{
			if (affine == null)
			{
				return true;
			}

			if (affine.Length < 12)
			{
				return false;
			}

			const double eps = 1e-6;

			// columns are the images of the X, Y and Z axes; row 2 is the Z component
			bool xFlat = Math.Abs(affine[8]) < eps;
			bool yFlat = Math.Abs(affine[9]) < eps;
			bool zVertical = Math.Abs(affine[2]) < eps && Math.Abs(affine[6]) < eps && Math.Abs(affine[10]) > eps;
			return xFlat && yFlat && zVertical;
		}

		// ---- wording ----

		public const string FailedMessage = "The laser file could not be saved.";

		public const string NotInMemoryMessage = "Only parts in the design can be exported for the laser.";

		public const string CancelledMessage = "Laser export cancelled. No file was written.";

		/// <summary>
		/// Why nothing was written: when gaps in a part's surface took its whole slice, says so and what to try; otherwise
		/// the plain message for the method.
		/// </summary>
		public static string EmptyMessage(LaserOutlineMethod method, LaserOutlineReport report)
		{
			if (method == LaserOutlineMethod.Section
				&& report?.PartsWithGaps != null
				&& report.PartsWithGaps.Count > 0)
			{
				var names = string.Join(", ", report.PartsWithGaps.Distinct().Select(n => $"'{n}'"));
				var verb = report.PartsWithGaps.Distinct().Count() == 1 ? "has" : "have";
				return $"Nothing to cut: {names} {verb} gaps in the surface, so the slice is missing. Try 'Outline seen from above'. No file was written.";
			}

			return EmptyMessage(method);
		}

		public static string EmptyMessage(LaserOutlineMethod method)
		{
			return method == LaserOutlineMethod.Section
				? "Nothing to cut: nothing is there at this height. No file was written."
				: "Nothing to cut: the parts have no outline seen from above. No file was written.";
		}

		/// <summary>Most "does not go all the way through" lines worded; the rest are one <see cref="MoreLikeThisMessage"/>.</summary>
		public const int MaxHoleWarnings = 5;

		public static string MoreLikeThisMessage(int more) => $"and {more} more like this";

		/// <summary>A Hole part that overlaps a solid part from above without going all the way through it.</summary>
		public static string HoleNotThroughMessage(string hole, string solid)
		{
			return $"'{hole}' does not go all the way through '{solid}', so it is not cut there. Combine them first, or use Slice at height.";
		}

		/// <summary>A part whose slice lost lines to gaps in its surface (nothing is made up to fill them).</summary>
		public static string GapsMessage(string name, bool wholeSliceMissing)
		{
			return wholeSliceMissing
				? $"'{name}' has gaps in its surface, so its slice is missing. Try 'Outline seen from above'."
				: $"'{name}' has gaps in its surface, so part of its slice is missing. Try 'Outline seen from above'.";
		}

		public static string OpenLoopsMessage(int closed)
		{
			return closed == 1 ? "1 very small gap in the slice was closed." : $"{closed} very small gaps in the slice were closed.";
		}

		public static string TinyLoopsMessage(int count)
		{
			return count == 1 ? "1 very small piece was left out." : $"{count} very small pieces were left out.";
		}

		/// <summary>Every note to show after a saved file (holes left out, gaps closed or dropped, tiny pieces).</summary>
		public static List<string> Notes(LaserOutlineReport report)
		{
			var notes = new List<string>();
			if (report == null)
			{
				return notes;
			}

			// the warnings name the holes left out and the parts with gaps
			notes.AddRange(report.Warnings);
			if (report.OpenLoopsClosed > 0)
			{
				notes.Add(OpenLoopsMessage(report.OpenLoopsClosed));
			}

			if (report.TinyLoopsDropped > 0)
			{
				notes.Add(TinyLoopsMessage(report.TinyLoopsDropped));
			}

			return notes;
		}

		/// <summary>"20 × 20 mm, 1 shape, 1 hole".</summary>
		public static string Summary(LaserOutline outline)
		{
			if (outline == null || outline.IsEmpty)
			{
				return "nothing to cut";
			}

			string Mm(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
			int shapes = outline.Report?.Shapes ?? 0;
			int holes = outline.Report?.Holes ?? 0;
			var text = $"{Mm(outline.Width)} × {Mm(outline.Height)} mm, {shapes} {(shapes == 1 ? "shape" : "shapes")}";
			if (holes > 0)
			{
				text += $", {holes} {(holes == 1 ? "hole" : "holes")}";
			}

			return text;
		}

		/// <summary>"Saved name.svg: 20 × 20 mm, 1 shape, 1 hole".</summary>
		public static string SavedMessage(string fileName, LaserOutline outline) => $"Saved {fileName}: {Summary(outline)}";

		/// <summary>The size line on the Export for laser page.</summary>
		public static string SizeLine(double width, double depth)
		{
			string Mm(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
			return $"Size on the bed: {Mm(width)} × {Mm(depth)} mm (seen from above)";
		}

		/// <summary>The height a person typed for Slice at height: kept at zero or more.</summary>
		public static double CleanSliceHeight(double value)
		{
			return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
		}
	}
}
