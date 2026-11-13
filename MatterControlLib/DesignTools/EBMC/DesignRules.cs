using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>
	/// EBMC Edition: where Duplicate puts the copy. Pure logic, unit tested.
	/// </summary>
	public static class DuplicatePlacement
	{
		/// <summary>Space left between the original and its copy (mm).</summary>
		public const double GapMm = 5;

		/// <summary>
		/// How far along +X to move the copy so it sits beside the original: the selection's X size plus
		/// <see cref="GapMm"/>. An empty or unmeasurable selection moves by the gap alone.
		/// </summary>
		public static double XOffset(double selectionXSize)
		{
			if (double.IsNaN(selectionXSize) || double.IsInfinity(selectionXSize) || selectionXSize < 0)
			{
				selectionXSize = 0;
			}

			return selectionXSize + GapMm;
		}
	}

	/// <summary>EBMC Edition: the unit the Text tool's size is given in.</summary>
	public enum TextSizeUnits
	{
		/// <summary>Font points (the stock behaviour and the default, so existing files are unchanged).</summary>
		Points,

		/// <summary>The height of a capital letter, in millimetres.</summary>
		Millimetres,
	}

	/// <summary>
	/// EBMC Edition: converts between the Text tool's point size and the height of its capital letters in mm.
	/// The Text tool scales glyphs from font units to pixels (96 per inch, <c>StyledTypeFace</c>) and then by
	/// 25.4 / 72 mm per unit, so a capital letter is <c>points × 96/72 × capHeightPerEm × 25.4/72</c> mm tall.
	/// Pure logic, unit tested.
	/// </summary>
	public static class TextSize
	{
		public const double PointsPerInch = 72;

		public const double PixelsPerInch = 96;

		public const double MmPerInch = 25.4;

		/// <summary>Used when a font does not report its capital height (typical for Latin fonts).</summary>
		public const double FallbackCapHeightPerEm = 0.7;

		/// <summary>The smallest and largest point size the tool will produce from a mm height.</summary>
		public const double MinPointSize = 0.1;

		public const double MaxPointSize = 10000;

		/// <summary>Millimetres of capital letter per point of font size.</summary>
		public static double CapHeightMmPerPoint(double capHeightPerEm)
		{
			if (double.IsNaN(capHeightPerEm) || capHeightPerEm <= 0 || capHeightPerEm > 2)
			{
				capHeightPerEm = FallbackCapHeightPerEm;
			}

			return (PixelsPerInch / PointsPerInch) * capHeightPerEm * (MmPerInch / PointsPerInch);
		}

		/// <summary>The point size whose capital letters are <paramref name="capHeightMm"/> tall.</summary>
		public static double PointSizeForCapHeight(double capHeightMm, double capHeightPerEm)
		{
			if (double.IsNaN(capHeightMm) || double.IsInfinity(capHeightMm) || capHeightMm <= 0)
			{
				return MinPointSize;
			}

			var points = capHeightMm / CapHeightMmPerPoint(capHeightPerEm);
			return Math.Max(MinPointSize, Math.Min(MaxPointSize, points));
		}

		/// <summary>The height of a capital letter (mm) at <paramref name="pointSize"/>.</summary>
		public static double CapHeightForPointSize(double pointSize, double capHeightPerEm)
		{
			return pointSize * CapHeightMmPerPoint(capHeightPerEm);
		}

		/// <summary>The cap height as a fraction of the em, from the font's cap height and units per em.</summary>
		public static double CapHeightPerEm(int capHeightUnits, int unitsPerEm)
		{
			if (capHeightUnits <= 0 || unitsPerEm <= 0)
			{
				return FallbackCapHeightPerEm;
			}

			return (double)capHeightUnits / unitsPerEm;
		}
	}

	/// <summary>
	/// EBMC Edition: which parts Subtract removes by default. Pure logic, unit tested.
	/// </summary>
	public static class SubtractDefaults
	{
		/// <summary>
		/// The parts to remove when a Subtract is first made: every part marked as a Hole, if there are any;
		/// otherwise the fallback (Subtract: the last part, as before; Subtract &amp; Replace: none, as before).
		/// Never returns every part, which would leave nothing to keep.
		/// </summary>
		/// <param name="parts">The selected parts in order: their ID and whether they are a Hole.</param>
		/// <param name="lastPartWhenNoHoles">True for Subtract (falls back to the last part).</param>
		public static IReadOnlyList<string> PartsToRemove(IReadOnlyList<(string id, bool isHole)> parts, bool lastPartWhenNoHoles)
		{
			if (parts == null || parts.Count == 0)
			{
				return new string[0];
			}

			var holes = parts.Where(p => p.isHole).Select(p => p.id).ToList();
			if (holes.Count > 0 && holes.Count < parts.Count)
			{
				return holes;
			}

			if (lastPartWhenNoHoles)
			{
				return new[] { parts[parts.Count - 1].id };
			}

			return new string[0];
		}
	}
}
