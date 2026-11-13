using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The club shapes' outlines and meshes (DesignTools/EBMC/ClubGeometry.cs, ClubMesh.cs, ClubShapeBuilders.cs): closed
	/// counter-clockwise outlines with the right area, and closed, outward-facing meshes with the right volume, for the
	/// default and extreme parameters.
	/// </summary>
	[TestFixture]
	public class ClubShapesTests
	{
		private static void AssertOutline(IReadOnlyList<Pt> outline, double expectedArea, double tolerance, string what)
		{
			Assert.That(outline.Count, Is.GreaterThanOrEqualTo(3), what);
			Assert.That(Profiles.IsCounterClockwise(outline), Is.True, what + " is counter-clockwise");
			Assert.That(Profiles.SelfIntersects(outline), Is.False, what + " does not cross itself");
			Assert.That(Profiles.SignedArea(outline), Is.EqualTo(expectedArea).Within(tolerance * expectedArea), what + " area");
		}

		private static void AssertSolid(ClubMesh mesh, double expectedVolume, double tolerance, string what)
		{
			Assert.That(mesh.TriangleCount, Is.GreaterThan(0), what);
			Assert.That(mesh.IsClosed(), Is.True, what + " is closed and consistently oriented");
			Assert.That(mesh.Volume(), Is.EqualTo(expectedVolume).Within(tolerance * expectedVolume), what + " volume");
			Assert.That(mesh.MinTriangleArea(), Is.GreaterThan(1e-9), what + " has no degenerate triangles");
			var b = mesh.Bounds();
			Assert.That(b.minZ, Is.EqualTo(0).Within(1e-9), what + " sits on Z = 0");
		}

		[Test]
		public void CircleAndPolygons()
		{
			AssertOutline(Profiles.Circle(10, 64), 64 * 100 * Math.Sin(2 * Math.PI / 64) / 2, 1e-9, "circle");
			for (int sides = 3; sides <= 12; sides++)
			{
				AssertOutline(Profiles.RegularPolygon(sides, 10), Profiles.RegularPolygonArea(sides, 10), 1e-9, $"{sides}-gon");
				var p = Profiles.RegularPolygon(sides, 10);
				Assert.That(p.Min(q => q.Y), Is.EqualTo(p.Where(q => q.Y < 0).Min(q => q.Y)), "flat at the front");
			}

			// across flats: a hexagon 20 mm across flats has corners on a circle of 20 / √3
			var hex = Profiles.RegularPolygon(6, Profiles.CornerRadiusFromFlats(6, 20));
			Assert.That(hex.Max(q => q.Y) - hex.Min(q => q.Y), Is.EqualTo(20).Within(1e-9), "hexagon across flats (front to back)");
		}

		[TestCase(20, 20, 3)]
		[TestCase(20, 20, 0)]
		[TestCase(60, 10, 4.99)]
		[TestCase(5, 5, 2.5)]
		public void RoundedRectangle(double w, double d, double r)
		{
			var outline = Profiles.RoundedRectangle(w, d, r, 8);
			var rr = Math.Min(r, Math.Min(w, d) / 2 * 0.998);
			var exact = Profiles.RoundedRectangleArea(w, d, rr);
			AssertOutline(outline, exact, 0.01, "rounded rectangle");
			Assert.That(outline.Max(p => p.X) - outline.Min(p => p.X), Is.EqualTo(w).Within(1e-9));
		}

		[TestCase(40, 10)]
		[TestCase(10, 10)]
		[TestCase(200, 3)]
		public void Stadium(double length, double width)
		{
			AssertOutline(Profiles.Stadium(length, width, 16), Profiles.StadiumArea(length, width), 0.01, "stadium");
		}

		[TestCase(5, 20, 10)]
		[TestCase(3, 20, 2)]
		[TestCase(12, 40, 36)]
		public void Star(int points, double outer, double inner)
		{
			var star = Profiles.Star(points, outer / 2, inner / 2);
			Assert.That(star.Count, Is.EqualTo(points * 2));
			AssertOutline(star, Profiles.StarArea(points, outer / 2, inner / 2), 1e-9, "star");
		}

		[Test]
		public void Heart()
		{
			var heart = Profiles.Heart(20);
			Assert.That(heart.Max(p => p.X) - heart.Min(p => p.X), Is.EqualTo(20).Within(1e-6));
			var fine = Profiles.Heart(20, 4096);
			AssertOutline(heart, Profiles.SignedArea(fine), 0.01, "heart");
			Assert.That(Profiles.HeartDepth(20), Is.InRange(17, 20));
		}

		[TestCase(20, 1.0, 22, 20, 17.5)]
		[TestCase(6, 2.0, 16, 12, 7)]
		public void GearSizes(int teeth, double module, double outside, double pitch, double root)
		{
			Assert.That(GearProfile.OutsideDiameter(teeth, module), Is.EqualTo(outside));
			Assert.That(GearProfile.PitchDiameter(teeth, module), Is.EqualTo(pitch));
			Assert.That(GearProfile.RootDiameter(teeth, module), Is.EqualTo(root));
			Assert.That(GearProfile.CircularPitch(module), Is.EqualTo(Math.PI * module));
		}

		[Test]
		public void TriangulatorKeepsTheArea()
		{
			var outer = Profiles.RoundedRectangle(40, 30, 5, 6).ToArray();
			var holeA = Profiles.Circle(4, 24, -10, 0).AsEnumerable().Reverse().ToArray();
			var holeB = Profiles.Circle(4, 24, 10, 0).AsEnumerable().Reverse().ToArray();
			var positions = new List<Pt>();
			int[] Add(Pt[] pts)
			{
				var ids = Enumerable.Range(positions.Count, pts.Length).ToArray();
				positions.AddRange(pts);
				return ids;
			}

			var oi = Add(outer);
			var ai = Add(holeA);
			var bi = Add(holeB);
			var tris = Triangulator.Triangulate(outer, oi, new List<(Pt[], int[])> { (holeA, ai), (holeB, bi) });
			double area = 0;
			for (int i = 0; i < tris.Count; i += 3)
			{
				var t = new[] { positions[tris[i]], positions[tris[i + 1]], positions[tris[i + 2]] };
				var a = Profiles.SignedArea(t);
				Assert.That(a, Is.GreaterThan(0), "every triangle is counter-clockwise");
				area += a;
			}

			Assert.That(area, Is.EqualTo(Profiles.SignedArea(outer) + Profiles.SignedArea(holeA) + Profiles.SignedArea(holeB)).Within(1e-6));
			Assert.That(tris.Count / 3, Is.EqualTo(outer.Length + holeA.Length + holeB.Length + 2 * 2 - 2), "n + 2h - 2 triangles");
		}

		[TestCase(20, 20, 20, 3, 0, TopEdgeStyle.Rounded)]
		[TestCase(20, 20, 20, 3, 2, TopEdgeStyle.Rounded)]
		[TestCase(20, 20, 20, 3, 2, TopEdgeStyle.Bevelled)]
		[TestCase(30, 10, 5, 0, 2, TopEdgeStyle.Rounded)]
		[TestCase(30, 10, 5, 0, 0, TopEdgeStyle.Rounded)]
		[TestCase(10, 10, 2, 4.9, 1.9, TopEdgeStyle.Rounded)]
		public void RoundedBox(double w, double d, double h, double corner, double top, TopEdgeStyle style)
		{
			corner = ShapeLimits.BoxCornerRadius(corner, w, d);
			top = ShapeLimits.BoxTopRadius(top, w, d, h, corner);
			var mesh = ShapeBuilders.RoundedBox(w, d, h, corner, top, style);
			var b = mesh.Bounds();
			Assert.That(b.maxX - b.minX, Is.EqualTo(w).Within(1e-6));
			Assert.That(b.maxZ, Is.EqualTo(h).Within(1e-9));

			// without the top rounding the volume is the outline area × height; with it, less, but at least the box under it
			var footprint = Profiles.SignedArea(Profiles.RoundedRectangle(w, d, corner, Profiles.ArcSegments(corner)));
			if (top == 0)
			{
				AssertSolid(mesh, footprint * h, 1e-9, "rounded box");
			}
			else
			{
				Assert.That(mesh.IsClosed(), Is.True);
				Assert.That(mesh.Volume(), Is.InRange(footprint * (h - top), footprint * h));
				Assert.That(mesh.MinTriangleArea(), Is.GreaterThan(0));
			}
		}

		[TestCase(20, 15, 20, 64)]
		[TestCase(20, 18.4, 5, 3)]
		[TestCase(4, 2, 1, 360)]
		public void Tube(double outer, double inner, double height, int sides)
		{
			inner = ShapeLimits.TubeInner(outer, inner);
			var mesh = ShapeBuilders.Tube(outer, inner, height, sides);
			var area = Profiles.SignedArea(Profiles.Circle(outer / 2, sides)) - Profiles.SignedArea(Profiles.Circle(inner / 2, sides));
			AssertSolid(mesh, area * height, 1e-9, "tube");
		}

		[TestCase(6, 20, 20)]
		[TestCase(3, 5, 1)]
		[TestCase(12, 100, 50)]
		public void PolygonPrism(int sides, double diameter, double height)
		{
			AssertSolid(ShapeBuilders.PolygonPrism(sides, diameter / 2, height), Profiles.RegularPolygonArea(sides, diameter / 2) * height, 1e-9, "prism");
		}

		[TestCase(5, 20, 10, 5)]
		[TestCase(3, 20, 19, 1)]
		[TestCase(20, 60, 50, 3)]
		public void StarSolid(int points, double outer, double inner, double height)
		{
			AssertSolid(ShapeBuilders.Star(points, outer, inner, height), Profiles.StarArea(points, outer / 2, inner / 2) * height, 1e-9, "star");
		}

		[Test]
		public void HeartSolid()
		{
			AssertSolid(ShapeBuilders.Heart(20, 5), Profiles.SignedArea(Profiles.Heart(20)) * 5, 1e-9, "heart");
		}

		[TestCase(40, 20, 5, false, 0, 0)]
		[TestCase(40, 20, 5, true, 20, 6)]
		[TestCase(40, 20, 5, true, 100, 100)]
		[TestCase(20, 20, 3, true, 4, 4)]
		public void SlotPlate(double length, double width, double height, bool slot, double slotLength, double slotWidth)
		{
			length = Math.Max(length, width);
			var (sl, sw) = ShapeLimits.SlotHole(length, width, slotLength, slotWidth);
			Assert.That(sw, Is.LessThanOrEqualTo(width - 2 * ShapeLimits.MinWall + 1e-9));
			var mesh = ShapeBuilders.SlotPlate(length, width, height, slot, sl, sw);
			var segs = Math.Max(8, Profiles.ArcSegments(width / 2) * 2);
			var area = Profiles.SignedArea(Profiles.Stadium(length, width, segs));
			if (slot)
			{
				area -= Profiles.SignedArea(Profiles.Stadium(sl, sw, Math.Max(6, Profiles.ArcSegments(sw / 2) * 2)));
			}

			AssertSolid(mesh, area * height, 1e-9, "slot plate");
		}

		[TestCase(20, 1.0, 5, 5)]
		[TestCase(6, 2.0, 100, 3)]
		[TestCase(100, 1.0, 1000, 95.5)]
		public void GearBoreLeavesMaterialUnderTheTeeth(int teeth, double module, double bore, double expected)
		{
			Assert.That(ShapeLimits.GearBore(teeth, module, bore), Is.EqualTo(expected).Within(1e-9));
		}

		[Test]
		public void PocketsStepsAndCountersinks()
		{
			// a cup: pocket from the top
			var cup = BodyBuilder.Build(Contour.Prism(Profiles.Circle(10, 32), 0, 10), Contour.Prism(Profiles.Circle(8, 32), 2, 10));
			var ring = Profiles.SignedArea(Profiles.Circle(10, 32)) - Profiles.SignedArea(Profiles.Circle(8, 32));
			AssertSolid(cup, Profiles.SignedArea(Profiles.Circle(10, 32)) * 2 + ring * 8, 1e-9, "cup");

			// a pocket from the bottom
			var cap = BodyBuilder.Build(Contour.Prism(Profiles.Circle(10, 32), 0, 10), Contour.Prism(Profiles.Circle(3, 16), 0, 6));
			AssertSolid(cap, Profiles.SignedArea(Profiles.Circle(10, 32)) * 10 - Profiles.SignedArea(Profiles.Circle(3, 16)) * 6, 1e-9, "blind hole");

			// a step: wide plate, narrower block on top
			var big = Profiles.RoundedRectangle(30, 30, 0, 1);
			var small = Profiles.RoundedRectangle(20, 20, 3, 4);
			var stepped = BodyBuilder.Build(new Contour(new Ring(0, big), new Ring(2, big), new Ring(2, small), new Ring(6, small)));
			AssertSolid(stepped, 900 * 2 + Profiles.SignedArea(small) * 4, 1e-9, "stepped");

			// a countersunk hole: volume of a plate minus the cylinder and the frustum
			var plate = BodyBuilder.Build(Contour.Prism(Profiles.RoundedRectangle(20, 20, 0, 1), 0, 4), Countersink.Hole(0, 0, 4, 2, 0, 4, 64));
			var a = Profiles.SignedArea(Profiles.Circle(2, 64));
			var A = Profiles.SignedArea(Profiles.Circle(4, 64));
			AssertSolid(plate, 400 * 4 - a * 2 - 2 * (a + A + Math.Sqrt(a * A)) / 3, 1e-9, "countersunk plate");
		}

		[Test]
		public void MirroredBodiesStayOutwardFacing()
		{
			var body = BodyBuilder.Extrude(Profiles.Circle(5, 24), 0, 3).Transform(ClubMesh.XyToXz);
			Assert.That(body.IsClosed(), Is.True);
			Assert.That(body.Volume(), Is.GreaterThan(0));
			var b = body.Bounds();
			Assert.That(b.maxY - b.minY, Is.EqualTo(3).Within(1e-9), "extrusion now runs along Y");
		}
	}
}
