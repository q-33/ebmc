using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// An axis aligned rectangle on the bed plane, in mm.
	/// </summary>
	public readonly struct BedRect
	{
		public BedRect(double minX, double minY, double maxX, double maxY)
		{
			MinX = minX;
			MinY = minY;
			MaxX = maxX;
			MaxY = maxY;
		}

		public double MinX { get; }

		public double MinY { get; }

		public double MaxX { get; }

		public double MaxY { get; }

		public static BedRect Around(double centerX, double centerY, double width, double depth)
		{
			return new BedRect(centerX - width / 2, centerY - depth / 2, centerX + width / 2, centerY + depth / 2);
		}

		/// <summary>True when the two rectangles share some area (touching edges do not count).</summary>
		public bool Overlaps(BedRect other)
		{
			return MinX < other.MaxX && other.MinX < MaxX
				&& MinY < other.MaxY && other.MinY < MaxY;
		}

		public bool Contains(BedRect other)
		{
			return other.MinX >= MinX && other.MaxX <= MaxX
				&& other.MinY >= MinY && other.MaxY <= MaxY;
		}

		public override string ToString() => $"({MinX}, {MinY}) - ({MaxX}, {MaxY})";
	}

	/// <summary>
	/// EBMC Edition: where the shapes palette puts a part it adds with a click. Pure geometry, so it is unit tested.
	/// The part goes to the bed centre (the printer's print_center on printer tabs, the origin on design tabs). If that spot
	/// is taken it goes to the nearest free spot, searched on square rings of StepMm around the centre, keeping GapMm
	/// clear of every other part and, on a printer, staying on the bed.
	/// (The stock placement, PlatingHelper.MoveToOpenPositionRelativeGroup, first moves the part's lower-left corner to the
	/// scene origin and then searches only towards +X/+Y: the origin is the bed centre on design tabs but the bed's
	/// front-left corner on printers whose print_center is not 0,0, so parts landed at the corner, half off the bed.)
	/// </summary>
	public static class PalettePlacement
	{
		public const double GapMm = 2;

		public const double StepMm = 5;

		/// <summary>
		/// True when a part of <paramref name="width"/> x <paramref name="depth"/> mm could lie on the bed at all (no bed:
		/// always). When it cannot, an unplaced part is "bigger than the bed", not "the bed is full".
		/// </summary>
		public static bool FitsOnBed(double width, double depth, BedRect? bed)
		{
			if (bed is BedRect b)
			{
				return width <= b.MaxX - b.MinX && depth <= b.MaxY - b.MinY;
			}

			return true;
		}

		/// <summary>
		/// The centre for a part of <paramref name="width"/> x <paramref name="depth"/> mm.
		/// </summary>
		/// <param name="centerX">Preferred centre X (the bed centre).</param>
		/// <param name="centerY">Preferred centre Y.</param>
		/// <param name="width">The part's X size.</param>
		/// <param name="depth">The part's Y size.</param>
		/// <param name="occupied">Footprints of the parts already on the bed.</param>
		/// <param name="bed">The bed, or null when there is no bed limit (design tabs).</param>
		/// <returns>The centre to move the part to, and whether it is free; when nothing free is found, the preferred
		/// centre with Found false (the caller tells the user the bed is full).</returns>
		public static (double X, double Y, bool Found) FindOpenCenter(double centerX, double centerY, double width, double depth, IEnumerable<BedRect> occupied, BedRect? bed)
		{
			var others = occupied.ToList();
			int maxRing = 200;
			if (bed is BedRect limits)
			{
				var span = Math.Max(limits.MaxX - limits.MinX, limits.MaxY - limits.MinY);
				maxRing = (int)Math.Ceiling(span / StepMm) + 1;
			}

			for (int ring = 0; ring <= maxRing; ring++)
			{
				foreach (var (dx, dy) in Ring(ring))
				{
					var x = centerX + dx * StepMm;
					var y = centerY + dy * StepMm;

					if (bed is BedRect b
						&& !b.Contains(BedRect.Around(x, y, width, depth)))
					{
						continue;
					}

					var footprint = BedRect.Around(x, y, width + 2 * GapMm, depth + 2 * GapMm);
					if (!others.Any(o => o.Overlaps(footprint)))
					{
						return (x, y, true);
					}
				}
			}

			return (centerX, centerY, false);
		}

		/// <summary>The grid offsets on the square ring at distance <paramref name="ring"/>, nearest first (then counter-clockwise from +X).</summary>
		private static IEnumerable<(int, int)> Ring(int ring)
		{
			if (ring == 0)
			{
				return new[] { (0, 0) };
			}

			var cells = new List<(int, int)>();
			for (int i = -ring; i <= ring; i++)
			{
				cells.Add((i, -ring));
				cells.Add((i, ring));
				if (i != -ring && i != ring)
				{
					cells.Add((-ring, i));
					cells.Add((ring, i));
				}
			}

			double Angle((int x, int y) c)
			{
				var a = Math.Atan2(c.y, c.x);
				return a < 0 ? a + 2 * Math.PI : a;
			}

			return cells.OrderBy(c => c.Item1 * c.Item1 + c.Item2 * c.Item2).ThenBy(Angle);
		}
	}
}
