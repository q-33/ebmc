using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using MatterHackers.MatterControl.DesignTools.Solid;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Fixes from the review of the club shapes, starters and mode code: QR tag and name tag holes, the Rounded Box top
	/// radius, the cable clip opening, the wall hook's lower screw, the tube's true wall, NaN input, one font per build,
	/// the rebuild worker's rules and the mode switch's busy rule. Every mesh here is also checked for inverted cap
	/// triangles (IsClosed alone does not catch them).
	/// </summary>
	[TestFixture]
	public class ClubReviewFixTests
	{
		private static string root;

		[OneTimeSetUp]
		public void FindRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "EBMC.md")))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null);
			root = directory.FullName;
		}

		private static string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));

		/// <summary>
		/// Closed, outward facing, no slivers, and every flat (horizontal) triangle at the bottom of the part faces down and
		/// at the top faces up with a real area: a triangulation that folds over itself leaves some of them the wrong way
		/// round even when every edge is shared twice.
		/// </summary>
		private static void AssertSound(ClubMesh mesh, string what, double? expectedVolume = null)
		{
			Assert.That(mesh.TriangleCount, Is.GreaterThan(0), what);
			Assert.That(mesh.IsClosed(), Is.True, what + ": closed and consistently oriented");
			Assert.That(mesh.MinTriangleArea(), Is.GreaterThan(1e-9), what + ": no degenerate triangles");

			var b = mesh.Bounds();
			var p = mesh.Positions;
			int wrongBottom = 0, wrongTop = 0;
			for (int i = 0; i < mesh.Triangles.Count; i += 3)
			{
				int a = mesh.Triangles[i] * 3, c1 = mesh.Triangles[i + 1] * 3, c2 = mesh.Triangles[i + 2] * 3;
				double za = p[a + 2], zb = p[c1 + 2], zc = p[c2 + 2];
				if (Math.Abs(za - zb) > 1e-9 || Math.Abs(za - zc) > 1e-9)
				{
					continue;
				}

				// z of the normal: twice the signed area of the triangle seen from above
				var nz = (p[c1] - p[a]) * (p[c2 + 1] - p[a + 1]) - (p[c1 + 1] - p[a + 1]) * (p[c2] - p[a]);
				if (Math.Abs(za - b.minZ) < 1e-9 && !(nz < -1e-12))
				{
					wrongBottom++;
				}

				if (Math.Abs(za - b.maxZ) < 1e-9 && !(nz > 1e-12))
				{
					wrongTop++;
				}
			}

			Assert.That(wrongBottom, Is.EqualTo(0), what + ": bottom triangles face down");
			Assert.That(wrongTop, Is.EqualTo(0), what + ": top triangles face up");
			Assert.That(mesh.Volume(), Is.GreaterThan(0), what);
			if (expectedVolume.HasValue)
			{
				Assert.That(mesh.Volume(), Is.EqualTo(expectedVolume.Value).Within(1e-6 * Math.Max(1, expectedVolume.Value)), what + ": volume");
			}
		}

		private static double CircleArea(IReadOnlyList<Pt> circle) => Profiles.SignedArea(circle);

		/// <summary>The hole circle lies inside the outline with at least <paramref name="wall"/> to spare (bounds check on a rectangle-ish plate).</summary>
		private static void AssertHoleInside(IReadOnlyList<Pt> outline, IReadOnlyList<Pt> hole, double wall, string what)
		{
			foreach (var point in hole)
			{
				Assert.That(Profiles.Contains(outline, point), Is.True, what + ": hole inside the plate");
			}

			double minX = outline.Min(q => q.X), maxX = outline.Max(q => q.X), minY = outline.Min(q => q.Y), maxY = outline.Max(q => q.Y);
			Assert.That(hole.Min(q => q.X) - minX, Is.GreaterThanOrEqualTo(wall - 1e-6), what + ": wall left");
			Assert.That(maxX - hole.Max(q => q.X), Is.GreaterThanOrEqualTo(wall - 1e-6), what + ": wall right");
			Assert.That(maxY - hole.Max(q => q.Y), Is.GreaterThanOrEqualTo(wall - 1e-6), what + ": wall above");
			Assert.That(Profiles.OutlinesIntersect(outline, hole), Is.False, what);
		}

		// --------------------------------------------------------------------------------------------------------------
		// 1. QR tag

		[TestCase(15, 15)]
		[TestCase(15, 2)]
		[TestCase(15, 10)]
		[TestCase(16, 15)]
		[TestCase(40, 15)]
		[TestCase(200, 15)]
		[TestCase(200, 2)]
		public void QrTagHoleStaysInsideWithARingWallAllRound(double size, double hole)
		{
			var problems = new StarterProblems();
			var rules = QrTagRules.Compute(size, 29, true, hole, problems);

			Assert.That(rules.HoleDiameter, Is.LessThanOrEqualTo(size - 2 * QrTagRules.RingWall + 1e-9));
			Assert.That(rules.PlateHeight, Is.EqualTo(rules.Size + rules.HoleDiameter + 2 * QrTagRules.RingWall).Within(1e-9));
			Assert.That(problems.Messages.Any(m => m.Contains("ring hole")), Is.EqualTo(hole > size - 2 * QrTagRules.RingWall), "a notice when the hole was made smaller");

			var outline = rules.Outline();
			var holeOutline = rules.HoleOutline();
			AssertHoleInside(outline, holeOutline, QrTagRules.RingWall - 0.05, "qr");

			// a ring wall above the hole and a ring wall between the hole and the code
			var codeTop = rules.CodeCenterY + rules.Size / 2;
			Assert.That(rules.PlateHeight / 2 - (rules.HoleCenterY + rules.HoleDiameter / 2), Is.EqualTo(QrTagRules.RingWall).Within(1e-9));
			Assert.That(rules.HoleCenterY - rules.HoleDiameter / 2 - codeTop, Is.EqualTo(QrTagRules.RingWall).Within(1e-9));
			Assert.That(rules.CodeCenterY - rules.Size / 2, Is.EqualTo(-rules.PlateHeight / 2).Within(1e-9), "the code fills the bottom of the plate");

			var plate = rules.Plate(2);
			AssertSound(plate, "qr plate", (Profiles.SignedArea(outline) - CircleArea(holeOutline)) * 2);
		}

		[TestCase(15)]
		[TestCase(200)]
		public void QrTagWithoutAHole(double size)
		{
			var rules = QrTagRules.Compute(size, 25, false, 15);
			Assert.That(rules.PlateHeight, Is.EqualTo(rules.Size));
			Assert.That(rules.HoleOutline(), Is.Null);
			AssertSound(rules.Plate(1), "qr plate", Profiles.SignedArea(rules.Outline()) * 1);
		}

		// --------------------------------------------------------------------------------------------------------------
		// 2. Rounded Box

		[TestCase(20, 20, 5, 6, TopEdgeStyle.Rounded)]
		[TestCase(20, 20, 5, 6, TopEdgeStyle.Bevelled)]
		[TestCase(20, 20, 2, 0, TopEdgeStyle.Rounded)]
		[TestCase(40, 30, 1, 0, TopEdgeStyle.Bevelled)]
		public void RoundedBoxTopRadiusNeverReachesTheBase(double w, double d, double h, double corner, TopEdgeStyle style)
		{
			corner = ShapeLimits.BoxCornerRadius(corner, w, d);
			var top = ShapeLimits.BoxTopRadius(1e6, w, d, h, corner);
			Assert.That(top, Is.LessThanOrEqualTo(h - ShapeLimits.MinStraightHeight + 1e-12));
			AssertSound(ShapeBuilders.RoundedBox(w, d, h, corner, top, style), "rounded box at its limit");

			// the builder itself also refuses a radius equal to the height (a ring on the base)
			AssertSound(ShapeBuilders.RoundedBox(w, d, h, corner, Math.Min(h, Math.Max(top, h)), style), "rounded box, radius = height");
		}

		// --------------------------------------------------------------------------------------------------------------
		// 3. Name Tag

		[TestCase(0.6, 60, 50)]
		[TestCase(0.0, 30, 40)]
		[TestCase(0.3, 200, 15)]
		public void NameTagTopHoleFitsANarrowAutoWidth(double widthPerCap, double height, double hole)
		{
			var problems = new StarterProblems();
			var layout = NameTagLayout.Compute(widthPerCap, null, height, 4, RingHolePosition.Top, hole, problems);
			Assert.That(layout.HoleDiameter, Is.LessThanOrEqualTo(layout.PlateWidth - 2 * NameTagLayout.RingWall + 1e-9));
			AssertHoleInside(layout.Outline(), layout.HoleOutline(), NameTagLayout.RingWall - 0.05, "name tag");
			AssertSound(BodyBuilder.Extrude(layout.Outline(), 0, 3, layout.HoleOutline()), "name tag plate");
			if (hole > layout.PlateWidth - 2 * NameTagLayout.RingWall)
			{
				Assert.That(problems.Messages.Any(m => m.Contains("ring hole")), Is.True);
			}
		}

		[TestCase(RingHolePosition.Left)]
		[TestCase(RingHolePosition.Top)]
		[TestCase(RingHolePosition.None)]
		public void NameTagAutoWidthStopsAt400AndShrinksTheLetters(RingHolePosition hole)
		{
			var problems = new StarterProblems();
			var layout = NameTagLayout.Compute(100, null, 40, 4, hole, 5, problems);
			Assert.That(layout.PlateWidth, Is.EqualTo(NameTagLayout.MaxWidth));
			Assert.That(layout.Shrunk, Is.True);
			Assert.That(100 * layout.CapHeight, Is.LessThanOrEqualTo(layout.TextWidth + 1e-9), "the letters fit");
			Assert.That(problems.Messages.Any(m => m.Contains("400")), Is.True);
			AssertSound(BodyBuilder.Extrude(layout.Outline(), 0, 3, layout.HasHole ? new[] { layout.HoleOutline() } : new List<Pt>[0]), "long name tag");
		}

		[Test]
		public void NameTagShortNamesAreUnchanged()
		{
			var problems = new StarterProblems();
			var layout = NameTagLayout.Compute(3, null, 20, 4, RingHolePosition.Left, 5, problems);
			Assert.That(layout.Shrunk, Is.False);
			Assert.That(problems.Messages, Is.Empty);
		}

		// --------------------------------------------------------------------------------------------------------------
		// 5. Cable Clip

		[TestCase(2.0)]
		[TestCase(3.0)]
		[TestCase(3.6)]
		[TestCase(6.0)]
		[TestCase(40.0)]
		public void CableClipOpeningIsAlwaysNarrowerThanTheCable(double cable)
		{
			var problems = new StarterProblems();
			var clip = CableClipRules.Compute(cable, 100, 10, 2, true, 4, problems);
			Assert.That(clip.Opening, Is.LessThan(cable), "the opening holds the cable");
			Assert.That(clip.Opening, Is.LessThanOrEqualTo(0.8 * cable + 1e-9).Or.EqualTo(1));
			Assert.That(clip.Opening, Is.LessThanOrEqualTo(0.9 * 2 * clip.InnerRadius + 1e-9));
			Assert.That(problems.Messages, Is.Not.Empty);
			AssertSound(clip.Mesh(), "cable clip");
		}

		[Test]
		public void TheReviewersThinCableCase()
		{
			var clip = CableClipRules.Compute(2, 2.1, 10, 2, false, 4);
			Assert.That(clip.Opening, Is.EqualTo(1.6).Within(1e-9));
		}

		// --------------------------------------------------------------------------------------------------------------
		// 6. One font per build

		[Test]
		public void TheFontIsLookedUpOnceAndFallsBackToTheDefault()
		{
			var calls = new List<string>();
			string Loader(string name)
			{
				calls.Add(name);
				return name == "Fredoka" ? "fredoka-face" : null;
			}

			Assert.That(ClubTextRules.ResolveFont("Unknown Font", "Fredoka", Loader), Is.EqualTo("fredoka-face"));
			Assert.That(calls, Is.EqualTo(new[] { "Unknown Font", "Fredoka" }));

			calls.Clear();
			Assert.That(ClubTextRules.ResolveFont("", "Fredoka", Loader), Is.EqualTo("fredoka-face"));
			Assert.That(calls, Is.EqualTo(new[] { "Fredoka" }));

			Assert.That(ClubTextRules.ResolveFont<string>("X", "Fredoka", n => throw new InvalidOperationException()), Is.Null);
		}

		[Test]
		public void TheStartersResolveTheFaceOnceAndPassItToMeasuringAndDrawing()
		{
			var starters = Read("MatterControlLib/DesignTools/ClubShapes/ClubStarters.cs");
			Assert.That(starters, Does.Not.Contain("var font = Font;"));
			Assert.That(System.Text.RegularExpressions.Regex.Matches(starters, "var font = ClubText.Resolve\\(Font\\);").Count, Is.EqualTo(3));

			var text = Read("MatterControlLib/DesignTools/ClubShapes/ClubText.cs");
			Assert.That(text, Does.Contain("public static Polygons Line(string text, TypeFace typeFace, double capHeight"));
			Assert.That(text, Does.Contain("public static double WidthPerCap(string text, TypeFace typeFace)"));
		}

		[TestCase("Ana\nLee", "Ana Lee")]
		[TestCase("Ana\r\nLee", "Ana Lee")]
		[TestCase("Ana", "Ana")]
		[TestCase("", "")]
		public void ALineBreakInAOneLineFieldBecomesASpace(string text, string expected)
		{
			Assert.That(ClubTextRules.OneLine(text), Is.EqualTo(expected));
		}

		// --------------------------------------------------------------------------------------------------------------
		// 7. Wall Hook

		[TestCase(12, 8)]
		[TestCase(60, 8)]
		[TestCase(3, 16)]
		[TestCase(200, 30)]
		public void TheLowerScrewIsReachablePastTheTip(double hookHeight, double head)
		{
			var problems = new StarterProblems();
			var hook = WallHookRules.Compute(30, 60, 5, 25, hookHeight, 6, 4, head, problems);
			var (lower, upper) = hook.HoleHeights;
			Assert.That(lower - hook.HeadDiameter / 2, Is.GreaterThanOrEqualTo(hook.HookHeight + WallHookRules.TipClearance - 1e-9), "clear of the tip");
			Assert.That(upper - lower, Is.GreaterThanOrEqualTo(hook.HeadDiameter + WallHookRules.HeadGap - 1e-9), "the heads do not overlap");
			Assert.That(upper + hook.HeadDiameter / 2, Is.LessThanOrEqualTo(hook.PlateHeight + 1e-9), "inside the plate");
			Assert.That(hook.PlateHeight, Is.LessThanOrEqualTo(WallHookRules.MaxPlateHeight + 1e-9));
			AssertSound(hook.Mesh(), "wall hook");
		}

		[Test]
		public void AVeryTallTipIsLoweredWithANotice()
		{
			var problems = new StarterProblems();
			var hook = WallHookRules.Compute(30, 60, 5, 25, 1000, 6, 4, 30, problems);
			Assert.That(hook.HookHeight, Is.LessThan(1000));
			Assert.That(problems.Messages.Any(m => m.Contains("tip was made lower")), Is.True);
		}

		// --------------------------------------------------------------------------------------------------------------
		// 10. Tube

		[TestCase(3)]
		[TestCase(4)]
		[TestCase(6)]
		[TestCase(12)]
		[TestCase(64)]
		[TestCase(360)]
		public void TheTubesThinnestWallIsAtLeastTheMinimum(int sides)
		{
			foreach (var outer in new[] { ShapeLimits.TubeMinOuter(sides), 4.0, 20.0, 400.0 })
			{
				var wall = ShapeLimits.TubeWall(0.1, Math.Max(outer, ShapeLimits.TubeMinOuter(sides)), sides);
				Assert.That(ShapeLimits.TubeTrueWall(wall, sides), Is.GreaterThanOrEqualTo(ShapeLimits.MinWall - 1e-12), $"{sides} sides, Ø{outer}");

				var inner = ShapeLimits.TubeInner(outer, outer, sides);
				Assert.That(ShapeLimits.TubeTrueWall((outer - inner) / 2, sides), Is.GreaterThanOrEqualTo(ShapeLimits.MinWall - 1e-12));
			}

			var o = Math.Max(20, ShapeLimits.TubeMinOuter(sides));
			var w = ShapeLimits.TubeWall(0.1, o, sides);
			AssertSound(ShapeBuilders.Tube(o, o - 2 * w, 5, sides), "thinnest tube");
		}

		[Test]
		public void TheTubeObjectUsesTheTrueWall()
		{
			var tube = Read("MatterControlLib/DesignTools/Primitives/TubeObject3D.cs");
			Assert.That(tube, Does.Contain("ShapeLimits.TubeMinOuter(sides)"));
			Assert.That(tube, Does.Contain("ShapeLimits.TubeWall(0, outer, sides)"));
			Assert.That(tube, Does.Not.Contain("ShapeLimits.MinWall / 2"));
		}

		// --------------------------------------------------------------------------------------------------------------
		// every starter at its limits: no inverted cap triangles

		[TestCase(8.0, 8.0, 3.0, 0.8, 0.6, 0.0, 0.0)]
		[TestCase(300.0, 300.0, 300.0, 10.0, 10.0, 200.0, 1.0)]
		[TestCase(8.0, 300.0, 3.0, 10.0, 0.6, 500.0, 1.0)]
		public void BoxWithLidAtItsLimits(double w, double d, double h, double wall, double floor, double corner, double clearance)
		{
			var r = BoxLidRules.Compute(w, d, h, wall, floor, corner, clearance);
			AssertSound(r.Box(), "box");
			AssertSound(r.Lid(), "lid");
		}

		[TestCase(8.0, 4.0, KnobGrip.Smooth, 3, KnobShaft.Round, 1.0, 1.0, 0.0)]
		[TestCase(150.0, 100.0, KnobGrip.Fluted, 1000, KnobShaft.DShaft, 200.0, 200.0, 1.0)]
		[TestCase(8.0, 4.0, KnobGrip.Fluted, 3, KnobShaft.DShaft, 6.0, 10.0, 1.0)]
		public void KnobAtItsLimits(double diameter, double height, KnobGrip grip, int flutes, KnobShaft shaft, double shaftDiameter, double depth, double clearance)
		{
			AssertSound(KnobRules.Compute(diameter, height, grip, flutes, shaft, shaftDiameter, depth, clearance).Mesh(), "knob");
		}

		[TestCase(5.0, 60.0, 30.0, 3.0, true)]
		[TestCase(30.0, 80.0, 200.0, 30.0, true)]
		[TestCase(30.0, 60.0, 30.0, 30.0, false)]
		public void PhoneStandAtItsLimits(double phone, double angle, double width, double lip, bool gap)
		{
			AssertSound(PhoneStandRules.Compute(phone, angle, width, lip, gap).Mesh(), "phone stand");
		}

		[TestCase(2.0, 1.0, 4.0, 1.2, true, 1.5)]
		[TestCase(40.0, 100.0, 100.0, 6.0, true, 8.0)]
		[TestCase(2.0, 1.0, 4.0, 1.2, false, 8.0)]
		public void CableClipAtItsLimits(double cable, double opening, double width, double wall, bool tab, double hole)
		{
			AssertSound(CableClipRules.Compute(cable, opening, width, wall, tab, hole).Mesh(), "cable clip");
		}

		[TestCase(10.0, 10.0, 2.0, 5.0, 2.0, 2.0, 1.5, 1.5)]
		[TestCase(200.0, 400.0, 20.0, 200.0, 200.0, 30.0, 10.0, 30.0)]
		public void WallHookAtItsLimits(double w, double h, double t, double reach, double hook, double hookT, double screw, double head)
		{
			AssertSound(WallHookRules.Compute(w, h, t, reach, hook, hookT, screw, head).Mesh(), "wall hook");
		}

		[TestCase(15.0, 8.0, 5.0, 3.0, true)]
		[TestCase(250.0, 100.0, 300.0, 100.0, true)]
		[TestCase(15.0, 8.0, 5.0, 8.0, false)]
		public void PlantLabelAtItsLimits(double blade, double width, double stake, double stakeWidth, bool pointed)
		{
			var r = PlantLabelRules.Compute(blade, width, stake, stakeWidth, pointed);
			AssertSound(BodyBuilder.Extrude(r.Outline(), 0, 2), "plant label", Profiles.SignedArea(r.Outline()) * 2);
		}

		[TestCase(40.0, 25.0, true, true)]
		[TestCase(400.0, 300.0, true, true)]
		[TestCase(40.0, 25.0, false, false)]
		public void SignAtItsLimits(double width, double height, bool border, bool holes)
		{
			var layout = SignLayout.Compute(width, height, border, holes, new[] { 3.0, 5.0 });
			var holeOutlines = layout.Holes.Select(h => (IReadOnlyList<Pt>)Profiles.Circle(SignLayout.HoleDiameter / 2, 24, h.x, h.y)).ToArray();
			AssertSound(BodyBuilder.Extrude(layout.Outline(), 0, 2, holeOutlines), "sign plate");
			if (border)
			{
				AssertSound(BodyBuilder.Extrude(layout.Outline(), 2, 3.2, layout.BorderInside()), "sign border");
			}
		}

		[TestCase(8.0, 1.5, RingHolePosition.Left)]
		[TestCase(200.0, 50.0, RingHolePosition.Left)]
		[TestCase(8.0, 50.0, RingHolePosition.Top)]
		[TestCase(200.0, 50.0, RingHolePosition.Top)]
		public void NameTagAtItsLimits(double height, double hole, RingHolePosition position)
		{
			foreach (var fixedWidth in new double?[] { null, 20, 400 })
			{
				var layout = NameTagLayout.Compute(4, fixedWidth, height, 100, position, hole);
				AssertSound(BodyBuilder.Extrude(layout.Outline(), 0, 3, layout.HoleOutline()), $"name tag {fixedWidth}");
			}
		}

		// --------------------------------------------------------------------------------------------------------------
		// 4. the rebuild worker

		[Test]
		public void TheWorkerRestartsWheneverANewerBuildIsQueued()
		{
			Assert.That(ClubBuildRules.Restart(failed: true, hasPendingBuild: true), Is.True, "a failure does not drop a newer build");
			Assert.That(ClubBuildRules.Restart(failed: false, hasPendingBuild: true), Is.True);
			Assert.That(ClubBuildRules.Restart(failed: true, hasPendingBuild: false), Is.False);
			Assert.That(ClubBuildRules.Restart(failed: false, hasPendingBuild: false), Is.False);
			Assert.That(ClubBuildRules.Cancellable, Is.False, "mesh and values always agree");
		}

		[Test]
		public void ApplyWaitsForABuildAndNeverRemovesAPartWithoutAMesh()
		{
			Assert.That(ClubBuildRules.ForApply(isBuilding: true, hasMesh: true), Is.EqualTo(ClubApplyDecision.WaitForBuild));
			Assert.That(ClubBuildRules.ForApply(isBuilding: true, hasMesh: false), Is.EqualTo(ClubApplyDecision.WaitForBuild));
			Assert.That(ClubBuildRules.ForApply(isBuilding: false, hasMesh: false), Is.EqualTo(ClubApplyDecision.NothingToApply));
			Assert.That(ClubBuildRules.ForApply(isBuilding: false, hasMesh: true), Is.EqualTo(ClubApplyDecision.Apply));
		}

		[Test]
		public void AFinishedBuildAlwaysPutsItsMeshOnThePart()
		{
			// deleted while building: the mesh still goes on (Undo re-adds the same object); the parents are not told
			Assert.That(SolidApplyRule.ForFinishedBuild(true, hasParent: true, inParentsChildren: false), Is.EqualTo((true, false)));
			Assert.That(SolidApplyRule.ForFinishedBuild(true, hasParent: true, inParentsChildren: true), Is.EqualTo((true, true)));
		}

		[Test]
		public void TheClubWorkerUsesTheseRules()
		{
			var source = Read("MatterControlLib/DesignTools/ClubShapes/ClubShapeObject3D.cs");
			Assert.That(source, Does.Contain("restart = ClubBuildRules.Restart(failed, pendingBuild != null);"));
			Assert.That(source, Does.Contain("var token = ClubBuildRules.Cancellable ? cancellation.Token : CancellationToken.None;"));
			Assert.That(source, Does.Contain("mesh = build(token).ToMesh();"));
			Assert.That(source, Does.Contain("Solid.SolidApplyRule.ForFinishedBuild("));
			Assert.That(source, Does.Contain("switch (ClubBuildRules.ForApply(IsBuilding, base.Mesh != null))"));
			Assert.That(source, Does.Not.Contain("Cancel(undoBuffer)"), "Apply never removes the part");
			Assert.That(source, Does.Not.Contain("// removed (deleted, undone, replaced) while it was building\n\t\t\t\treturn;"));
		}

		// --------------------------------------------------------------------------------------------------------------
		// 9. NaN

		[TestCase(double.NaN, 20, 20, true)]
		[TestCase(double.PositiveInfinity, 20, 20, true)]
		[TestCase(double.NegativeInfinity, 3, 3, true)]
		[TestCase(12.5, 20, 12.5, false)]
		public void NotANumberBecomesTheDefault(double value, double fallback, double expected, bool replaced)
		{
			Assert.That(ClubValueRules.Finite(value, fallback), Is.EqualTo((expected, replaced)));
		}

		[TestCase("MatterControlLib/DesignTools/ClubShapes/ClubStarters.cs")]
		[TestCase("MatterControlLib/DesignTools/Primitives/RoundedBoxObject3D.cs")]
		[TestCase("MatterControlLib/DesignTools/Primitives/TubeObject3D.cs")]
		[TestCase("MatterControlLib/DesignTools/Primitives/SlotPlateObject3D.cs")]
		[TestCase("MatterControlLib/DesignTools/Primitives/PolygonPrismObject3D.cs")]
		[TestCase("MatterControlLib/DesignTools/Primitives/StarObject3D.cs")]
		[TestCase("MatterControlLib/DesignTools/Primitives/HeartObject3D.cs")]
		[TestCase("MatterControlLib/DesignTools/Primitives/GearObject3D.cs")]
		public void EveryClubFieldGoesThroughTheFiniteCheck(string file)
		{
			var source = Read(file);
			Assert.That(source, Does.Not.Contain(".ClampIfNotCalculated(this,"));
			Assert.That(source, Does.Contain(".ClampFinite(this,"));
		}

		// --------------------------------------------------------------------------------------------------------------
		// 8. the mode switch's busy rule

		[Test]
		public void APrintInProgressDoesNotHoldUpTheSwitch()
		{
			var tasks = new[] { ("Printing", true), ("", true), ("Heating Bed", true), ("Printer finished", true) };
			Assert.That(UiModeBusyRules.Reason(false, false, tasks, null), Is.Null);
		}

		[Test]
		public void TheHintNamesWhatIsBlocking()
		{
			Assert.That(UiModeBusyRules.Reason(false, false, new[] { ("Printing", true), ("Saving \"Box\" ...", false) }, null), Is.EqualTo("Switch modes after saving \"Box\" has finished."));
			Assert.That(UiModeBusyRules.Reason(false, false, new[] { ("Saving", false) }, null), Is.EqualTo("Switch modes after saving has finished."));
			Assert.That(UiModeBusyRules.Reason(true, false, new (string, bool)[0], null), Is.EqualTo("Switch modes after slicing has finished."));
			Assert.That(UiModeBusyRules.Reason(false, true, new (string, bool)[0], null), Does.Contain("window has been rebuilt"));
			Assert.That(UiModeBusyRules.Reason(false, false, new[] { ("", false) }, null), Is.EqualTo("Switch modes after the running task has finished."));
			Assert.That(UiModeBusyRules.Reason(false, false, new[] { ("STEP import", false) }, null), Is.EqualTo("Switch modes after STEP import has finished."));
			Assert.That(UiModeBusyRules.Reason(false, false, null, null), Is.Null);
		}

		[Test]
		public void TheLongRunningPrinterTasksAreMarked()
		{
			var controller = Read("MatterControlLib/ApplicationView/ApplicationController.cs");
			Assert.That(System.Text.RegularExpressions.Regex.Matches(controller, "AllowsWindowRebuild = true").Count, Is.EqualTo(3), "print, heater hold, timed notice");
			var printer = Read("MatterControlLib/ApplicationView/Config/PrinterConfig.cs");
			Assert.That(System.Text.RegularExpressions.Regex.Matches(printer, "AllowsWindowRebuild = true").Count, Is.EqualTo(3), "the three heating waits");
		}

		// --------------------------------------------------------------------------------------------------------------
		// nits

		[Test]
		public void ARefusedSliceSettingsPickStoresNothing()
		{
			var service = Read("MatterControlLib/ApplicationView/UiModes.cs");
			var busy = service.IndexOf("&& BusyReason() is string busy", StringComparison.Ordinal);
			var remember = service.IndexOf("UserSettings.Instance.set(UserSettingsKey.UiModeLastAdvancedDetail, level);", StringComparison.Ordinal);
			Assert.That(busy, Is.GreaterThan(0));
			Assert.That(remember, Is.GreaterThan(busy), "the busy check comes before anything is stored");
		}

		[Test]
		public void TheViewStyleStaysReachableInSimple()
		{
			var view = Read("MatterControlLib/PartPreviewWindow/View3D/View3DWidget.cs");
			Assert.That(view, Does.Contain("AddRoundButton(modelViewStyleButton, new Vector2(marginCenter, startHeight + 1 * ySpacing), true).Visible = true;"));
			Assert.That(view, Does.Contain("modelViewStyleButton.Visible = e.ViewMode == PartViewMode.Model;"));
			Assert.That(view, Does.Contain("gridSnapButton.Visible = fullViewControls && e.ViewMode == PartViewMode.Model;"));
		}

		[TestCase(true, true, false, true, ExpectedResult = true)]
		[TestCase(false, true, false, true, ExpectedResult = false)]
		[TestCase(true, false, false, true, ExpectedResult = false)]
		[TestCase(true, true, true, true, ExpectedResult = false)]
		[TestCase(true, true, false, false, ExpectedResult = false)]
		public bool TheStartCardComesBackOnlyOnAStillEmptyTabThatHadIt(bool hadCard, bool empty, bool hidden, bool startsEmpty)
		{
			return StartCardRules.ShowAgain(hadCard, empty, hidden, startsEmpty);
		}

		[Test]
		public void TheRestoredTabsAskForTheCard()
		{
			var main = Read("MatterControlLib/PartPreviewWindow/MainViewWidget.cs");
			Assert.That(main, Does.Contain("DesignStartCard.RestoreAfterRebuild(newTab);"));
		}

		[Test]
		public void WallHookScrewAndHeadStayInsideANarrowPlate()
		{
			// the narrowest plate with the biggest screw: the screw is clamped first, then the head, both to width - 4
			var hook = WallHookRules.Compute(10, 60, 5, 25, 12, 6, 10, 10);
			Assert.That(hook.ScrewDiameter, Is.LessThanOrEqualTo(hook.PlateWidth - 4 + 1e-9));
			Assert.That(hook.HeadDiameter, Is.LessThanOrEqualTo(hook.PlateWidth - 4 + 1e-9), "the countersink stays 2 mm inside each edge");
			Assert.That(hook.HeadDiameter, Is.GreaterThanOrEqualTo(hook.ScrewDiameter));
			AssertSound(hook.Mesh(), "wall hook at its narrowest");
		}
	}
}
