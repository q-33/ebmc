using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	/// <summary>How a Rounded Box finishes its top edges.</summary>
	public enum TopEdgeStyle
	{
		Rounded,
		Bevelled
	}

	/// <summary>
	/// EBMC Edition: the club's basic shapes as closed meshes, bottom at Z = 0 and centred on X and Y. Inputs are already
	/// clamped (see <see cref="ShapeLimits"/>); everything here is pure so the tests can check it.
	/// </summary>
	public static class ShapeBuilders
	{
		/// <summary>
		/// A box with rounded vertical edges and, optionally, rounded or bevelled top edges (never the bottom, so it prints
		/// flat). With a corner radius the top radius stays just under it, so the top corners keep a curve.
		/// </summary>
		public static ClubMesh RoundedBox(double width, double depth, double height, double cornerRadius, double topRadius, TopEdgeStyle style)
		{
			var segs = Profiles.ArcSegments(cornerRadius);
			var rings = new List<Ring> { new Ring(0, Profiles.RoundedRectangle(width, depth, cornerRadius, segs)) };
			if (topRadius <= 1e-6)
			{
				rings.Add(new Ring(height, Profiles.RoundedRectangle(width, depth, cornerRadius, segs)));
				return BodyBuilder.Build(new Contour(rings.ToArray()));
			}

			// a top radius equal to the height would put a ring at Z = 0 on top of the base ring (overlapping, inverted
			// triangles): keep the straight part at least 0.01 mm tall (ShapeLimits.BoxTopRadius already clamps to this)
			topRadius = Math.Min(topRadius, height - ShapeLimits.MinStraightHeight);
			var steps = style == TopEdgeStyle.Bevelled ? 1 : Math.Max(3, Profiles.ArcSegments(topRadius));
			rings.Add(new Ring(height - topRadius, Profiles.RoundedRectangle(width, depth, cornerRadius, segs)));
			for (int i = 1; i <= steps; i++)
			{
				double inset, z;
				if (style == TopEdgeStyle.Bevelled)
				{
					inset = topRadius;
					z = height;
				}
				else
				{
					var a = Math.PI / 2 * i / steps;
					inset = topRadius * (1 - Math.Cos(a));
					z = height - topRadius + topRadius * Math.Sin(a);
				}

				var radius = cornerRadius > 0 ? Math.Max(cornerRadius - inset, 0.01) : 0;
				rings.Add(new Ring(z, CornerRect(width - 2 * inset, depth - 2 * inset, radius, segs, cornerRadius > 0)));
			}

			return BodyBuilder.Build(new Contour(rings.ToArray()));
		}

		/// <summary>A rounded rectangle that keeps its point count even when the radius is tiny.</summary>
		private static List<Pt> CornerRect(double width, double depth, double radius, int segs, bool rounded)
		{
			if (!rounded)
			{
				return Profiles.RoundedRectangle(width, depth, 0, segs);
			}

			// RoundedRectangle limits the radius to under half the short side; keep it positive so the count is stable
			return Profiles.RoundedRectangle(width, depth, Math.Max(radius, 1e-4), segs);
		}

		public static ClubMesh Tube(double outerDiameter, double innerDiameter, double height, int sides)
		{
			return BodyBuilder.Extrude(Profiles.Circle(outerDiameter / 2, sides), 0, height, Profiles.Circle(innerDiameter / 2, sides));
		}

		public static ClubMesh PolygonPrism(int sides, double cornerRadius, double height)
		{
			return BodyBuilder.Extrude(Profiles.RegularPolygon(sides, cornerRadius), 0, height);
		}

		public static ClubMesh Star(int points, double outerDiameter, double innerDiameter, double height)
		{
			return BodyBuilder.Extrude(Profiles.Star(points, outerDiameter / 2, innerDiameter / 2), 0, height);
		}

		public static ClubMesh Heart(double width, double height)
		{
			return BodyBuilder.Extrude(Profiles.Heart(width), 0, height);
		}

		public static ClubMesh SlotPlate(double length, double width, double height, bool slot, double slotLength, double slotWidth)
		{
			var segs = Math.Max(8, Profiles.ArcSegments(width / 2) * 2);
			var outline = Profiles.Stadium(length, width, segs);
			if (!slot)
			{
				return BodyBuilder.Extrude(outline, 0, height);
			}

			return BodyBuilder.Extrude(outline, 0, height, Profiles.Stadium(slotLength, slotWidth, Math.Max(6, Profiles.ArcSegments(slotWidth / 2) * 2)));
		}
	}

	/// <summary>
	/// EBMC Edition: limits for the club shapes, shared by the objects (which clamp their stored values) and the tests.
	/// </summary>
	public static class ShapeLimits
	{
		public const double MinSize = 0.5;

		public const double MaxSize = 1000;

		/// <summary>The thinnest wall or web the shapes leave (about two lines of the club's 0.3 mm nozzle).</summary>
		public const double MinWall = 0.8;

		public static double Clamp(double value, double min, double max)
		{
			if (double.IsNaN(value))
			{
				return min;
			}

			return Math.Max(min, Math.Min(max, value));
		}

		public static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

		/// <summary>Corner radius for a box: at most just under half the shorter side.</summary>
		public static double BoxCornerRadius(double radius, double width, double depth) => Clamp(radius, 0, Math.Min(width, depth) / 2 * 0.99);

		/// <summary>The straight part a Rounded Box keeps below its top rounding, so no ring lies on the base.</summary>
		public const double MinStraightHeight = 0.01;

		/// <summary>Top edge radius: a little under the height, a little under half the shorter side, and (with rounded corners) a little under the corner radius.</summary>
		public static double BoxTopRadius(double radius, double width, double depth, double height, double cornerRadius)
		{
			var max = Math.Min(height - MinStraightHeight, Math.Min(width, depth) / 2 * 0.98);
			if (cornerRadius > 0)
			{
				max = Math.Min(max, cornerRadius - 0.02);
			}

			return Clamp(radius, 0, Math.Max(0, max));
		}

		/// <summary>
		/// The wall a tube must be given (measured corner to corner, as the outer and inner diameters are) so its thinnest
		/// part, across the flats of an n-sided tube, is at least <see cref="MinWall"/>: MinWall / cos(π/n). 0 sides means a
		/// true circle.
		/// </summary>
		public static double TubeMinWall(int sides) => sides >= 3 ? MinWall / Math.Cos(Math.PI / sides) : MinWall;

		/// <summary>The thinnest part of a tube's wall: across the flats, wall · cos(π/n).</summary>
		public static double TubeTrueWall(double wall, int sides) => sides >= 3 ? wall * Math.Cos(Math.PI / sides) : wall;

		/// <summary>A tube's inner diameter: leaves a true wall (across the flats) of at least <see cref="MinWall"/>.</summary>
		public static double TubeInner(double outer, double inner, int sides = 0) => Clamp(inner, MinSize, outer - 2 * TubeMinWall(sides));

		/// <summary>A tube's wall thickness for an outer diameter and side count: a true wall of at least <see cref="MinWall"/>, and an inside of at least <see cref="MinSize"/>.</summary>
		public static double TubeWall(double wall, double outer, int sides)
		{
			var min = TubeMinWall(sides);
			return Clamp(wall, min, Math.Max(min, (outer - MinSize) / 2));
		}

		/// <summary>The smallest outer diameter a tube can have with <paramref name="sides"/> sides.</summary>
		public static double TubeMinOuter(int sides) => 2 * TubeMinWall(sides) + MinSize;

		public static double StarInner(double outer, double inner) => Clamp(inner, MinSize, outer * 0.95);

		/// <summary>The slot hole of a Slot Plate: at least <see cref="MinWall"/> from the edge all round.</summary>
		public static (double length, double width) SlotHole(double plateLength, double plateWidth, double slotLength, double slotWidth)
		{
			var width = Clamp(slotWidth, MinSize, plateWidth - 2 * MinWall);
			var length = Clamp(slotLength, width, Math.Max(width, plateLength - 2 * MinWall));
			return (length, width);
		}

		/// <summary>A gear's bore: leaves at least two modules (and <see cref="MinWall"/>) below the teeth.</summary>
		public static double GearBore(int teeth, double module, double bore)
		{
			var max = GearProfile.RootDiameter(teeth, module) - 2 * Math.Max(MinWall, module);
			return Clamp(bore, 0, Math.Max(0, max));
		}
	}
}
