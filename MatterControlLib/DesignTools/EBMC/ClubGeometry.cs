using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	/// <summary>
	/// EBMC Edition: a 2D point in mm. Pure geometry for the club shapes and starters (no agg-sharp, so
	/// Tests/EBMC.Tests can check the profiles and meshes).
	/// </summary>
	public readonly struct Pt
	{
		public Pt(double x, double y)
		{
			X = x;
			Y = y;
		}

		public double X { get; }

		public double Y { get; }

		public static Pt operator +(Pt a, Pt b) => new Pt(a.X + b.X, a.Y + b.Y);

		public static Pt operator -(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);

		public static Pt operator *(Pt a, double s) => new Pt(a.X * s, a.Y * s);

		public double Length => Math.Sqrt(X * X + Y * Y);

		public override string ToString() => $"({X:0.###}, {Y:0.###})";
	}

	/// <summary>
	/// EBMC Edition: closed 2D outlines (counter-clockwise, first point not repeated) for the club shapes, and the
	/// checks the tests use (area, winding, self-intersection).
	/// </summary>
	public static class Profiles
	{
		public const double Tau = Math.PI * 2;

		/// <summary>Signed area (positive for counter-clockwise).</summary>
		public static double SignedArea(IReadOnlyList<Pt> polygon)
		{
			double area = 0;
			for (int i = 0; i < polygon.Count; i++)
			{
				var a = polygon[i];
				var b = polygon[(i + 1) % polygon.Count];
				area += a.X * b.Y - b.X * a.Y;
			}

			return area / 2;
		}

		public static bool IsCounterClockwise(IReadOnlyList<Pt> polygon) => SignedArea(polygon) > 0;

		public static List<Pt> CounterClockwise(IEnumerable<Pt> polygon)
		{
			var list = polygon.ToList();
			if (SignedArea(list) < 0)
			{
				list.Reverse();
			}

			return list;
		}

		public static List<Pt> Translate(IEnumerable<Pt> polygon, double dx, double dy) => polygon.Select(p => new Pt(p.X + dx, p.Y + dy)).ToList();

		public static List<Pt> Rotate(IEnumerable<Pt> polygon, double radians)
		{
			var c = Math.Cos(radians);
			var s = Math.Sin(radians);
			return polygon.Select(p => new Pt(p.X * c - p.Y * s, p.X * s + p.Y * c)).ToList();
		}

		/// <summary>True when two non-adjacent edges cross or touch, or two consecutive points coincide.</summary>
		public static bool SelfIntersects(IReadOnlyList<Pt> polygon)
		{
			int n = polygon.Count;
			for (int i = 0; i < n; i++)
			{
				var a = polygon[i];
				var b = polygon[(i + 1) % n];
				if ((b - a).Length < 1e-9)
				{
					return true;
				}

				for (int j = i + 2; j < n; j++)
				{
					if (i == 0 && j == n - 1)
					{
						continue;
					}

					if (SegmentsIntersect(a, b, polygon[j], polygon[(j + 1) % n]))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>True when any edge of <paramref name="a"/> crosses or touches any edge of <paramref name="b"/>.</summary>
		public static bool OutlinesIntersect(IReadOnlyList<Pt> a, IReadOnlyList<Pt> b)
		{
			for (int i = 0; i < a.Count; i++)
			{
				for (int j = 0; j < b.Count; j++)
				{
					if (SegmentsIntersect(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count]))
					{
						return true;
					}
				}
			}

			return false;
		}

		public static bool Contains(IReadOnlyList<Pt> polygon, Pt point)
		{
			bool inside = false;
			for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
			{
				var pi = polygon[i];
				var pj = polygon[j];
				if ((pi.Y > point.Y) != (pj.Y > point.Y)
					&& point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
				{
					inside = !inside;
				}
			}

			return inside;
		}

		private static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

		private static bool SegmentsIntersect(Pt p1, Pt p2, Pt p3, Pt p4)
		{
			const double eps = 1e-12;
			var d1 = Cross(p3, p4, p1);
			var d2 = Cross(p3, p4, p2);
			var d3 = Cross(p1, p2, p3);
			var d4 = Cross(p1, p2, p4);
			if (((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps))
				&& ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps)))
			{
				return true;
			}

			bool OnSegment(Pt a, Pt b, Pt c) => Math.Min(a.X, b.X) - 1e-9 <= c.X && c.X <= Math.Max(a.X, b.X) + 1e-9
				&& Math.Min(a.Y, b.Y) - 1e-9 <= c.Y && c.Y <= Math.Max(a.Y, b.Y) + 1e-9;

			return (Math.Abs(d1) <= eps && OnSegment(p3, p4, p1))
				|| (Math.Abs(d2) <= eps && OnSegment(p3, p4, p2))
				|| (Math.Abs(d3) <= eps && OnSegment(p1, p2, p3))
				|| (Math.Abs(d4) <= eps && OnSegment(p1, p2, p4));
		}

		/// <summary>A circle of <paramref name="sides"/> points on radius <paramref name="radius"/>, first point at angle 0.</summary>
		public static List<Pt> Circle(double radius, int sides, double cx = 0, double cy = 0)
		{
			sides = Math.Max(3, sides);
			var points = new List<Pt>(sides);
			for (int i = 0; i < sides; i++)
			{
				var a = Tau * i / sides;
				points.Add(new Pt(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
			}

			return points;
		}

		/// <summary>Points per quarter circle for a corner or end of radius r (2 to 12).</summary>
		public static int ArcSegments(double radius) => Math.Max(2, Math.Min(12, (int)Math.Ceiling(radius * 1.5)));

		/// <summary>
		/// A rectangle centred on the origin with rounded corners. Radius 0 gives four points; otherwise each corner is a
		/// quarter circle of <paramref name="segments"/> + 1 points (the radius is limited to just under half the shorter side).
		/// </summary>
		public static List<Pt> RoundedRectangle(double width, double depth, double radius, int segments)
		{
			radius = Math.Max(0, Math.Min(radius, Math.Min(width, depth) / 2 * 0.998));
			var hx = width / 2;
			var hy = depth / 2;
			var points = new List<Pt>();
			if (radius <= 1e-6)
			{
				points.Add(new Pt(-hx, -hy));
				points.Add(new Pt(hx, -hy));
				points.Add(new Pt(hx, hy));
				points.Add(new Pt(-hx, hy));
				return points;
			}

			segments = Math.Max(1, segments);
			var centers = new[]
			{
				new Pt(hx - radius, -hy + radius),
				new Pt(hx - radius, hy - radius),
				new Pt(-hx + radius, hy - radius),
				new Pt(-hx + radius, -hy + radius),
			};
			for (int corner = 0; corner < 4; corner++)
			{
				var start = -Math.PI / 2 + corner * Math.PI / 2;
				for (int i = 0; i <= segments; i++)
				{
					var a = start + Math.PI / 2 * i / segments;
					points.Add(new Pt(centers[corner].X + radius * Math.Cos(a), centers[corner].Y + radius * Math.Sin(a)));
				}
			}

			return points;
		}

		/// <summary>Area of <see cref="RoundedRectangle"/> with a true circular corner.</summary>
		public static double RoundedRectangleArea(double width, double depth, double radius) => width * depth - (4 - Math.PI) * radius * radius;

		/// <summary>
		/// A stadium (slot): <paramref name="length"/> along X, <paramref name="width"/> along Y, ends fully rounded.
		/// Centred on the origin. The length is at least the width.
		/// </summary>
		public static List<Pt> Stadium(double length, double width, int segmentsPerEnd)
		{
			length = Math.Max(length, width);
			var r = width / 2;
			var half = length / 2 - r;
			var points = new List<Pt>();
			segmentsPerEnd = Math.Max(2, segmentsPerEnd);
			if (half < 1e-6)
			{
				return Circle(r, segmentsPerEnd * 2);
			}

			for (int i = 0; i <= segmentsPerEnd; i++)
			{
				var a = -Math.PI / 2 + Math.PI * i / segmentsPerEnd;
				points.Add(new Pt(half + r * Math.Cos(a), r * Math.Sin(a)));
			}

			for (int i = 0; i <= segmentsPerEnd; i++)
			{
				var a = Math.PI / 2 + Math.PI * i / segmentsPerEnd;
				points.Add(new Pt(-half + r * Math.Cos(a), r * Math.Sin(a)));
			}

			return points;
		}

		public static double StadiumArea(double length, double width)
		{
			length = Math.Max(length, width);
			return (length - width) * width + Math.PI * width * width / 4;
		}

		/// <summary>
		/// A regular polygon with <paramref name="sides"/> corners on a circle of <paramref name="cornerRadius"/>, turned so
		/// one flat side faces the front (−Y).
		/// </summary>
		public static List<Pt> RegularPolygon(int sides, double cornerRadius)
		{
			sides = Math.Max(3, sides);
			var start = -Math.PI / 2 + Math.PI / sides;
			var points = new List<Pt>(sides);
			for (int i = 0; i < sides; i++)
			{
				var a = start + Tau * i / sides;
				points.Add(new Pt(cornerRadius * Math.Cos(a), cornerRadius * Math.Sin(a)));
			}

			return points;
		}

		public static double RegularPolygonArea(int sides, double cornerRadius) => sides * cornerRadius * cornerRadius * Math.Sin(Tau / sides) / 2;

		/// <summary>The corner radius of a regular polygon whose flats are <paramref name="acrossFlats"/> apart.</summary>
		public static double CornerRadiusFromFlats(int sides, double acrossFlats) => acrossFlats / 2 / Math.Cos(Math.PI / Math.Max(3, sides));

		/// <summary>A star with <paramref name="points"/> tips, one pointing to the back (+Y).</summary>
		public static List<Pt> Star(int points, double outerRadius, double innerRadius)
		{
			points = Math.Max(3, points);
			var result = new List<Pt>(points * 2);
			for (int i = 0; i < points * 2; i++)
			{
				var r = i % 2 == 0 ? outerRadius : innerRadius;
				var a = Math.PI / 2 + Math.PI * i / points;
				result.Add(new Pt(r * Math.Cos(a), r * Math.Sin(a)));
			}

			return result;
		}

		public static double StarArea(int points, double outerRadius, double innerRadius) => points * outerRadius * innerRadius * Math.Sin(Math.PI / points);

		/// <summary>
		/// A heart <paramref name="width"/> wide (X), from the classic curve x = 16 sin³t, y = 13 cos t − 5 cos 2t − 2 cos 3t −
		/// cos 4t, centred on its bounding box.
		/// </summary>
		public static List<Pt> Heart(double width, int samples = 96)
		{
			samples = Math.Max(24, samples);
			var raw = new List<Pt>(samples);
			for (int i = 0; i < samples; i++)
			{
				// clockwise as t increases; walked backwards for counter-clockwise
				var t = -Tau * i / samples;
				var x = 16 * Math.Pow(Math.Sin(t), 3);
				var y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
				raw.Add(new Pt(x, y));
			}

			var minX = raw.Min(p => p.X);
			var maxX = raw.Max(p => p.X);
			var minY = raw.Min(p => p.Y);
			var maxY = raw.Max(p => p.Y);
			var scale = width / (maxX - minX);
			var cx = (minX + maxX) / 2;
			var cy = (minY + maxY) / 2;
			return CounterClockwise(raw.Select(p => new Pt((p.X - cx) * scale, (p.Y - cy) * scale)));
		}

		/// <summary>Height (Y) of <see cref="Heart"/> for a width.</summary>
		public static double HeartDepth(double width)
		{
			var outline = Heart(width, 720);
			return outline.Max(p => p.Y) - outline.Min(p => p.Y);
		}
	}

	/// <summary>
	/// EBMC Edition: spur gear sizes (20° pressure angle, addendum 1 module, dedendum 1.25 modules). The tooth outline itself
	/// comes from DesignTools/Primitives/Gear2D.cs (checked: outside diameter module × (teeth + 2), closed when extruded).
	/// </summary>
	public static class GearProfile
	{
		public const double PressureAngleDegrees = 20;

		public const int MinTeeth = 6;

		public const int MaxTeeth = 100;

		public static double PitchDiameter(int teeth, double module) => module * teeth;

		public static double OutsideDiameter(int teeth, double module) => module * (teeth + 2);

		public static double RootDiameter(int teeth, double module) => module * (teeth - 2.5);

		/// <summary>Gear2D works in circular pitch: the tooth spacing along the pitch circle, π × module.</summary>
		public static double CircularPitch(double module) => Math.PI * module;
	}
}
