using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The Club Starters' dimension rules (DesignTools/EBMC/ClubStarterRules.cs) and the meshes they build without text.
	/// </summary>
	[TestFixture]
	public class ClubStarterTests
	{
		private static void AssertClosedBodies(ClubMesh mesh, string what)
		{
			Assert.That(mesh.TriangleCount, Is.GreaterThan(0), what);
			Assert.That(mesh.IsClosed(), Is.True, what + ": every body closed and outward facing");
			Assert.That(mesh.Volume(), Is.GreaterThan(0), what);
			Assert.That(mesh.MinTriangleArea(), Is.GreaterThan(1e-9), what + ": no degenerate triangles");
			Assert.That(mesh.Bounds().minZ, Is.EqualTo(0).Within(1e-9), what + ": on the bed");
		}

		[TestCase(2, 4, 1)]
		[TestCase(3.5, 7, 1.75)]
		[TestCase(4, 8, 2)]
		public void CountersinkDepthIsHalfTheDifference(double shaft, double head, double expected)
		{
			Assert.That(Countersink.Depth(shaft, head), Is.EqualTo(expected).Within(1e-12));
		}

		[Test]
		public void CountersinkFitsThePlate()
		{
			Assert.That(Countersink.Fit(4, 8, 4), Is.EqualTo((2.0, false)));
			Assert.That(Countersink.Fit(4, 12, 4), Is.EqualTo((3.0, true)), "leaves 1 mm of straight hole");
		}

		[Test]
		public void NameTagAutoWidthFollowsTheText()
		{
			var shortName = NameTagLayout.Compute(2, null, 20, 3, RingHolePosition.Left, 5);
			var longName = NameTagLayout.Compute(6, null, 20, 3, RingHolePosition.Left, 5);
			var cap = 0.45 * 20;
			Assert.That(shortName.CapHeight, Is.EqualTo(cap));
			Assert.That(longName.PlateWidth - shortName.PlateWidth, Is.EqualTo((6 - 2) * cap).Within(1e-9), "width grows with the text");

			// the hole sits in its own zone left of the text, with a wall round it
			Assert.That(shortName.HoleX - shortName.HoleDiameter / 2, Is.EqualTo(-shortName.PlateWidth / 2 + NameTagLayout.RingWall).Within(1e-9));
			Assert.That(shortName.TextCenterX - shortName.TextWidth / 2, Is.GreaterThanOrEqualTo(shortName.HoleX + shortName.HoleDiameter / 2));
			Assert.That(shortName.TextWidth, Is.EqualTo(2 * cap).Within(1e-9));
			Assert.That(shortName.Shrunk, Is.False);
		}

		[Test]
		public void NameTagFixedWidthShrinksLongText()
		{
			var problems = new StarterProblems();
			var tag = NameTagLayout.Compute(10, 60, 20, 3, RingHolePosition.None, 5, problems);
			Assert.That(tag.PlateWidth, Is.EqualTo(60));
			Assert.That(tag.Shrunk, Is.True);
			Assert.That(tag.CapHeight * 10, Is.EqualTo(tag.TextWidth).Within(1e-9), "the text exactly fills the room");
			Assert.That(problems.Messages, Has.Count.EqualTo(1));
			Assert.That(problems.Messages[0], Does.Not.Contain("MatterControl"));

			var roomy = NameTagLayout.Compute(1, 100, 20, 3, RingHolePosition.None, 5);
			Assert.That(roomy.Shrunk, Is.False);
			Assert.That(roomy.CapHeight, Is.EqualTo(9));
		}

		[Test]
		public void NameTagTopHoleMakesTheTagTaller()
		{
			var tag = NameTagLayout.Compute(3, null, 20, 3, RingHolePosition.Top, 5);
			Assert.That(tag.PlateHeight, Is.EqualTo(20 + 5 + NameTagLayout.RingWall).Within(1e-9));
			Assert.That(tag.HoleY + tag.HoleDiameter / 2, Is.EqualTo(tag.PlateHeight / 2 - NameTagLayout.RingWall).Within(1e-9));
			Assert.That(tag.TextCenterY + tag.CapHeight / 2, Is.LessThan(tag.HoleY - tag.HoleDiameter / 2), "the text stays below the hole");

			// the plate with its hole is a closed body
			var mesh = BodyBuilder.Extrude(tag.Outline(), 0, 3, tag.HoleOutline());
			AssertClosedBodies(mesh, "name tag plate");
		}

		[Test]
		public void NameTagHoleIsLimited()
		{
			var tag = NameTagLayout.Compute(3, null, 12, 3, RingHolePosition.Left, 50);
			Assert.That(tag.HoleDiameter, Is.EqualTo(12 - 2 * NameTagLayout.RingWall).Within(1e-9));
		}

		[TestCase(1)]
		[TestCase(2)]
		[TestCase(3)]
		public void SignLinesFitInside(int lines)
		{
			var widths = Enumerable.Range(0, lines).Select(i => 4.0 + i).ToList();
			var sign = SignLayout.Compute(120, 60, true, true, widths);
			Assert.That(sign.LineCenters, Has.Count.EqualTo(lines));
			Assert.That(sign.CapHeight * widths.Max(), Is.LessThanOrEqualTo(sign.TextWidth + 1e-9), "the widest line fits");
			var topLine = sign.LineCenters.First() + sign.CapHeight / 2;
			var holeBottom = sign.Height / 2 - SignLayout.HoleInset - SignLayout.HoleDiameter / 2;
			Assert.That(topLine, Is.LessThan(holeBottom), "text stays below the mounting holes");
			Assert.That(sign.LineCenters.Last() - 0.65 * sign.CapHeight, Is.GreaterThan(-sign.Height / 2 + SignLayout.BorderWidth), "text stays inside the border");
			Assert.That(sign.Holes, Has.Count.EqualTo(2));

			var plate = BodyBuilder.Extrude(sign.Outline(), 0, 4, sign.Holes.Select(h => (IReadOnlyList<Pt>)Profiles.Circle(SignLayout.HoleDiameter / 2, 24, h.x, h.y)).ToArray());
			AssertClosedBodies(plate, "sign plate");
			var border = BodyBuilder.Extrude(sign.Outline(), 0, 1, sign.BorderInside());
			Assert.That(border.IsClosed(), Is.True, "border frame");
		}

		[Test]
		public void BoxLidLipIsInnerMinusTwiceTheClearance()
		{
			var r = BoxLidRules.Compute(40, 30, 20, 2, 2, 4, 0.3);
			Assert.That(r.OuterWidth, Is.EqualTo(44));
			Assert.That(r.OuterDepth, Is.EqualTo(34));
			Assert.That(r.OuterHeight, Is.EqualTo(22));
			Assert.That(r.LipOuterWidth, Is.EqualTo(40 - 0.6).Within(1e-12));
			Assert.That(r.LipOuterDepth, Is.EqualTo(30 - 0.6).Within(1e-12));
			Assert.That(r.InnerCornerRadius, Is.EqualTo(2));
			Assert.That(r.LipCornerRadius, Is.EqualTo(1.7).Within(1e-12));
			Assert.That(r.LipInnerWidth, Is.EqualTo(40 - 0.6 - 4).Within(1e-12));

			var box = r.Box();
			AssertClosedBodies(box, "box");
			var outer = Profiles.SignedArea(Profiles.RoundedRectangle(44, 34, 4, Profiles.ArcSegments(4)));
			var inner = Profiles.SignedArea(Profiles.RoundedRectangle(40, 30, 2, Profiles.ArcSegments(4)));
			Assert.That(box.Volume(), Is.EqualTo(outer * 22 - inner * 20).Within(1e-6));

			var lid = r.Lid();
			AssertClosedBodies(lid, "lid");
			var lb = lid.Bounds();
			Assert.That(lb.maxZ, Is.EqualTo(2 + r.LipHeight).Within(1e-9));

			var (boxX, lidX) = r.Centers(true);
			Assert.That(lidX - boxX, Is.EqualTo(44 + BoxLidRules.Gap).Within(1e-9), "side by side with a gap");
		}

		[Test]
		public void BoxLidSharpCornersAndTinyBox()
		{
			var r = BoxLidRules.Compute(8, 8, 3, 3, 1, 0, 1);
			AssertClosedBodies(r.Box(), "sharp box");
			AssertClosedBodies(r.Lid(), "sharp lid");
			Assert.That(r.LipInnerWidth, Is.GreaterThanOrEqualTo(2 - 1e-9));
		}

		[Test]
		public void WallHook()
		{
			var problems = new StarterProblems();
			var hook = WallHookRules.Compute(30, 60, 5, 25, 12, 6, 4, 8, problems);
			Assert.That(problems.Messages, Is.Empty);
			Assert.That(hook.CountersinkDepth, Is.EqualTo(2));
			var (lower, upper) = hook.HoleHeights;
			Assert.That(lower - hook.HeadDiameter / 2, Is.GreaterThanOrEqualTo(hook.HookHeight + WallHookRules.TipClearance), "the lower head clears the upturned tip, so a screwdriver reaches it");
			Assert.That(upper - lower, Is.GreaterThanOrEqualTo(hook.HeadDiameter), "the heads do not overlap");
			var mesh = hook.Mesh();
			AssertClosedBodies(mesh, "wall hook");
			var b = mesh.Bounds();
			Assert.That(b.maxX, Is.EqualTo(5 + 25).Within(1e-9), "reach from the plate face");
			Assert.That(b.maxZ, Is.EqualTo(30).Within(1e-9), "printed on its side: the width is the height");

			// too short a plate grows; too thin a plate gets a shallower countersink
			var cramped = WallHookRules.Compute(30, 10, 2, 25, 12, 6, 4, 8, problems);
			Assert.That(cramped.PlateHeight, Is.EqualTo(12 + 16 + WallHookRules.TipClearance + 2 * WallHookRules.HeadGap), "tip, two heads, the tip clearance and two gaps");
			Assert.That(cramped.CountersinkDepth, Is.EqualTo(1));
			Assert.That(problems.Messages, Has.Count.EqualTo(2));
			AssertClosedBodies(cramped.Mesh(), "cramped hook");
		}

		[TestCase(12, 70, 70, 8, true)]
		[TestCase(12, 70, 70, 8, false)]
		[TestCase(5, 60, 30, 3, true)]
		[TestCase(30, 80, 200, 30, true)]
		public void PhoneStand(double phone, double angle, double width, double lip, bool gap)
		{
			var stand = PhoneStandRules.Compute(phone, angle, width, lip, gap);
			Assert.That(Profiles.SelfIntersects(stand.RestProfile()), Is.False);
			Assert.That(Profiles.IsCounterClockwise(stand.RestProfile()), Is.True);
			Assert.That(Profiles.IsCounterClockwise(stand.LipProfile()), Is.True);
			var mesh = stand.Mesh();
			AssertClosedBodies(mesh, "phone stand");
			var b = mesh.Bounds();
			Assert.That(b.maxY - b.minY, Is.EqualTo(stand.Width).Within(1e-9));
			Assert.That(mesh.Bodies, Is.EqualTo(stand.CableGap ? 3 : 2));
			Assert.That(stand.CableGap, Is.EqualTo(gap && width >= PhoneStandRules.CableGapWidth + 20));
		}

		[Test]
		public void PhoneStandLimitsTheAngle()
		{
			Assert.That(PhoneStandRules.Compute(12, 30, 70, 8, false).AngleDegrees, Is.EqualTo(60));
			Assert.That(PhoneStandRules.Compute(12, 90, 70, 8, false).AngleDegrees, Is.EqualTo(80));
		}

		[TestCase(30, 12, 12)]
		[TestCase(30, 100, 31)]
		[TestCase(10, 12, 10)]
		[TestCase(10, 1, 3)]
		public void KnobFluteCount(double diameter, int flutes, int expected)
		{
			Assert.That(KnobRules.ClampFlutes(flutes, diameter), Is.EqualTo(expected));
		}

		[TestCase(KnobGrip.Smooth, KnobShaft.Round)]
		[TestCase(KnobGrip.Fluted, KnobShaft.Round)]
		[TestCase(KnobGrip.Fluted, KnobShaft.DShaft)]
		public void Knob(KnobGrip grip, KnobShaft shaft)
		{
			var knob = KnobRules.Compute(30, 15, grip, 12, shaft, 6, 10, 0.2);
			Assert.That(knob.HoleRadius, Is.EqualTo(3.2).Within(1e-12));
			Assert.That(knob.FlatDistance, Is.EqualTo(1.5 + 0.2).Within(1e-12), "6 mm D-shaft: 4.5 mm across the flat");
			Assert.That(Profiles.SelfIntersects(knob.Outline()), Is.False);
			Assert.That(Profiles.SelfIntersects(knob.ShaftOutline()), Is.False);
			AssertClosedBodies(knob.Mesh(), "knob");
			var shaftArea = Profiles.SignedArea(knob.ShaftOutline());
			Assert.That(knob.Mesh().Volume(), Is.EqualTo(Profiles.SignedArea(knob.Outline()) * 15 - shaftArea * 10).Within(1e-6));
		}

		[Test]
		public void KnobShaftStaysInside()
		{
			var problems = new StarterProblems();
			var knob = KnobRules.Compute(12, 5, KnobGrip.Smooth, 3, KnobShaft.Round, 10, 20, 0.2, problems);
			Assert.That(knob.ShaftDepth, Is.EqualTo(3.5));
			Assert.That(knob.HoleRadius * 2, Is.LessThanOrEqualTo(12 - 6 + 1e-9));
			Assert.That(problems.Messages, Has.Count.EqualTo(1));
			AssertClosedBodies(knob.Mesh(), "small knob");
		}

		[TestCase(6, 4.5, true)]
		[TestCase(6, 4.5, false)]
		[TestCase(20, 30, true)]
		[TestCase(2, 1, true)]
		public void CableClip(double cable, double opening, bool tab)
		{
			var problems = new StarterProblems();
			var clip = CableClipRules.Compute(cable, opening, 10, 2, tab, 3.5, problems);
			Assert.That(clip.Opening, Is.LessThan(2 * clip.InnerRadius), "the opening is narrower than the cable");
			Assert.That(Profiles.SelfIntersects(clip.ClipProfile()), Is.False);
			Assert.That(Profiles.IsCounterClockwise(clip.ClipProfile()), Is.True);
			var mesh = clip.Mesh();
			AssertClosedBodies(mesh, "cable clip");
			var b = mesh.Bounds();
			// the top is the opening's edge: above the ring's centre, below a closed ring's top
			Assert.That(b.maxZ, Is.InRange(clip.Wall + clip.InnerRadius, clip.Wall + clip.InnerRadius + clip.OuterRadius));
			Assert.That(problems.Messages.Count, Is.EqualTo(opening > 2 * clip.InnerRadius * 0.9 ? 1 : 0));
		}

		[TestCase(60, 20, 80, 10, true)]
		[TestCase(60, 20, 80, 10, false)]
		[TestCase(30, 10, 20, 10, true)]
		[TestCase(30, 10, 20, 50, false)]
		public void PlantLabel(double blade, double bladeWidth, double stake, double stakeWidth, bool pointed)
		{
			var label = PlantLabelRules.Compute(blade, bladeWidth, stake, stakeWidth, pointed);
			var outline = label.Outline();
			Assert.That(Profiles.IsCounterClockwise(outline), Is.True);
			Assert.That(Profiles.SelfIntersects(outline), Is.False);
			Assert.That(outline.Max(p => p.X), Is.EqualTo(label.TotalLength).Within(1e-9));
			Assert.That(label.StakeWidth, Is.LessThanOrEqualTo(label.BladeWidth));
			AssertClosedBodies(BodyBuilder.Extrude(outline, 0, 2), "plant label");
			Assert.That(label.CapHeight(20) * 20, Is.LessThanOrEqualTo(label.BladeLength - 2 * PlantLabelRules.Margin + 1e-9));
		}

		[Test]
		public void QrRunsCoverEveryDarkModule()
		{
			var rnd = new Random(7);
			var modules = Enumerable.Range(0, 29).Select(r => (IReadOnlyList<bool>)Enumerable.Range(0, 29).Select(c => rnd.Next(2) == 0).ToList()).ToList();
			var runs = QrTagRules.Runs(modules);
			Assert.That(runs.Sum(r => r.end - r.start + 1), Is.EqualTo(modules.Sum(r => r.Count(x => x))));
			foreach (var (row, start, end) in runs)
			{
				Assert.That(Enumerable.Range(start, end - start + 1).All(c => modules[row][c]), Is.True);
			}

			var problems = new StarterProblems();
			var tag = QrTagRules.Compute(40, 37, true, 5, problems);
			Assert.That(problems.Messages, Is.Empty);
			Assert.That(tag.ModuleSize, Is.EqualTo(40.0 / 37).Within(1e-12));
			AssertClosedBodies(tag.Plate(2), "qr tag plate");
			var rectangles = tag.ModuleRectangles(modules);
			Assert.That(rectangles, Has.Count.EqualTo(runs.Count));
			Assert.That(rectangles.Sum(r => Profiles.SignedArea(r)), Is.EqualTo(modules.Sum(r => r.Count(x => x)) * tag.ModuleSize * tag.ModuleSize).Within(1e-9));
			var hole = tag.HoleOutline();
			foreach (var r in rectangles)
			{
				Assert.That(r.All(p => Math.Abs(p.X) <= tag.PlateWidth / 2 + 1e-9 && Math.Abs(p.Y) <= tag.PlateHeight / 2 + 1e-9), Is.True, "on the plate");
				Assert.That(Profiles.OutlinesIntersect(r, hole), Is.False, "clear of the ring hole");
			}

			QrTagRules.Compute(15, 57, false, 5, problems);
			Assert.That(problems.Messages, Has.Count.EqualTo(1), "too small to print is said");
		}
	}
}
