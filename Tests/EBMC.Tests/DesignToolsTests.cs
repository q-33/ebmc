using System;
using System.Linq;
using MatterHackers.MatterControl.DesignTools;
using NUnit.Framework;

namespace EBMC.Tests
{
	[TestFixture]
	public class DuplicatePlacementTests
	{
		[TestCase(0, 5)]
		[TestCase(20, 25)]
		[TestCase(12.5, 17.5)]
		[TestCase(-3, 5)]
		[TestCase(double.NaN, 5)]
		[TestCase(double.PositiveInfinity, 5)]
		[TestCase(double.NegativeInfinity, 5)]
		public void CopyGoesBesideTheOriginal(double xSize, double expected)
		{
			Assert.That(DuplicatePlacement.XOffset(xSize), Is.EqualTo(expected).Within(1e-9));
		}

		[Test]
		public void CopyNeverOverlapsTheOriginal()
		{
			foreach (var size in new[] { 0.1, 1, 10, 100, 1000 })
			{
				// the copy's left edge (original left edge + offset) is a full gap past the original's right edge
				Assert.That(DuplicatePlacement.XOffset(size) - size, Is.EqualTo(DuplicatePlacement.GapMm).Within(1e-9));
			}
		}
	}

	[TestFixture]
	public class TextSizeTests
	{
		[Test]
		public void MillimetresPerPointMatchesTheTextToolScaling()
		{
			// Text tool: font units -> pixels at 96 dpi, then x 25.4/72 mm
			Assert.That(TextSize.CapHeightMmPerPoint(1), Is.EqualTo(96.0 / 72 * 25.4 / 72).Within(1e-12));
			Assert.That(TextSize.CapHeightMmPerPoint(0.7), Is.EqualTo(0.7 * 96.0 / 72 * 25.4 / 72).Within(1e-12));
		}

		[TestCase(10, 0.7)]
		[TestCase(5, 0.72)]
		[TestCase(25.4, 0.65)]
		[TestCase(0.5, 0.7)]
		public void RoundTrip(double capHeightMm, double capPerEm)
		{
			var points = TextSize.PointSizeForCapHeight(capHeightMm, capPerEm);
			Assert.That(TextSize.CapHeightForPointSize(points, capPerEm), Is.EqualTo(capHeightMm).Within(1e-9));
		}

		[Test]
		public void TallerLettersNeedBiggerPointSize()
		{
			Assert.That(TextSize.PointSizeForCapHeight(20, 0.7), Is.EqualTo(2 * TextSize.PointSizeForCapHeight(10, 0.7)).Within(1e-9));
			// a font with shorter capitals needs more points for the same height
			Assert.That(TextSize.PointSizeForCapHeight(10, 0.6), Is.GreaterThan(TextSize.PointSizeForCapHeight(10, 0.7)));
		}

		[Test]
		public void KnownValue()
		{
			// cap height 0.7 em: 10 mm capitals = 10 / (0.7 * 4/3 * 25.4/72) = 30.37... points
			Assert.That(TextSize.PointSizeForCapHeight(10, 0.7), Is.EqualTo(30.3712).Within(1e-3));
		}

		[TestCase(0)]
		[TestCase(-2)]
		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		public void BadHeightGivesTheSmallestSize(double capHeightMm)
		{
			Assert.That(TextSize.PointSizeForCapHeight(capHeightMm, 0.7), Is.EqualTo(TextSize.MinPointSize));
		}

		[Test]
		public void HugeHeightIsCapped()
		{
			Assert.That(TextSize.PointSizeForCapHeight(1e9, 0.7), Is.EqualTo(TextSize.MaxPointSize));
		}

		[TestCase(0, 0.7)]
		[TestCase(-1, 0.7)]
		[TestCase(double.NaN, 0.7)]
		[TestCase(5, 0.7)]
		public void UnusableFontCapHeightFallsBack(double capPerEm, double expectedUsed)
		{
			Assert.That(TextSize.CapHeightMmPerPoint(capPerEm), Is.EqualTo(TextSize.CapHeightMmPerPoint(expectedUsed)).Within(1e-12));
		}

		[Test]
		public void CapHeightPerEmFromFontMetrics()
		{
			Assert.That(TextSize.CapHeightPerEm(1400, 2000), Is.EqualTo(0.7).Within(1e-12));
			Assert.That(TextSize.CapHeightPerEm(0, 2048), Is.EqualTo(TextSize.FallbackCapHeightPerEm));
			Assert.That(TextSize.CapHeightPerEm(1400, 0), Is.EqualTo(TextSize.FallbackCapHeightPerEm));
		}

		[Test]
		public void PointsIsTheDefaultUnit()
		{
			// existing files have no unit saved and must keep their size: the enum's default value is Points
			Assert.That(default(TextSizeUnits), Is.EqualTo(TextSizeUnits.Points));
		}
	}

	[TestFixture]
	public class SubtractDefaultsTests
	{
		[Test]
		public void HolesAreRemovedWhateverTheSelectionOrder()
		{
			var parts = new[] { ("hole", true), ("box", false) };
			Assert.That(SubtractDefaults.PartsToRemove(parts, true), Is.EqualTo(new[] { "hole" }));

			parts = new[] { ("box", false), ("hole", true) };
			Assert.That(SubtractDefaults.PartsToRemove(parts, true), Is.EqualTo(new[] { "hole" }));
		}

		[Test]
		public void SeveralHolesAreAllRemoved()
		{
			var parts = new[] { ("h1", true), ("box", false), ("h2", true), ("box2", false) };
			Assert.That(SubtractDefaults.PartsToRemove(parts, true), Is.EqualTo(new[] { "h1", "h2" }));
		}

		[Test]
		public void NoHolesKeepsTheOldRuleForSubtract()
		{
			var parts = new[] { ("a", false), ("b", false), ("c", false) };
			Assert.That(SubtractDefaults.PartsToRemove(parts, true), Is.EqualTo(new[] { "c" }));
		}

		[Test]
		public void NoHolesKeepsTheOldRuleForSubtractAndReplace()
		{
			var parts = new[] { ("a", false), ("b", false) };
			Assert.That(SubtractDefaults.PartsToRemove(parts, false), Is.Empty);
		}

		[Test]
		public void AllHolesFallsBackSoSomethingIsKept()
		{
			var parts = new[] { ("a", true), ("b", true) };
			Assert.That(SubtractDefaults.PartsToRemove(parts, true), Is.EqualTo(new[] { "b" }));
			Assert.That(SubtractDefaults.PartsToRemove(parts, false), Is.Empty);
		}

		[Test]
		public void EmptySelection()
		{
			Assert.That(SubtractDefaults.PartsToRemove(new (string, bool)[0], true), Is.Empty);
			Assert.That(SubtractDefaults.PartsToRemove(null, true), Is.Empty);
		}
	}

	[TestFixture]
	public class ModelFitTests
	{
		[Test]
		public void NormalPartOnDesignTabIsFine()
		{
			Assert.That(ModelFit.Advise(20, 30, 10, onPrinter: false).Issue, Is.EqualTo(ModelFitIssue.None));
		}

		[Test]
		public void NormalPartOnPrinterIsFine()
		{
			Assert.That(ModelFit.Advise(20, 30, 10, true, 230, 210, 200).Issue, Is.EqualTo(ModelFitIssue.None));
		}

		[TestCase(1.99, ModelFitIssue.Tiny)]
		[TestCase(2.0, ModelFitIssue.None)]
		[TestCase(0.5, ModelFitIssue.Tiny)]
		public void TinyThreshold(double largest, ModelFitIssue expected)
		{
			Assert.That(ModelFit.Advise(largest, largest / 2, largest / 4, false).Issue, Is.EqualTo(expected));
			Assert.That(ModelFit.Advise(largest / 2, largest / 4, largest, true, 230, 210, 200).Issue, Is.EqualTo(expected));
		}

		[Test]
		public void TinyOffersInchesThenCentimetres()
		{
			var advice = ModelFit.Advise(0.8, 0.5, 0.2, false);
			Assert.That(advice.Issue, Is.EqualTo(ModelFitIssue.Tiny));
			Assert.That(advice.LargestSizeMm, Is.EqualTo(0.8));
			Assert.That(advice.Choices.Select(c => c.Scale), Is.EqualTo(new[] { 25.4, 10.0 }));
			Assert.That(advice.Choices.Any(c => c.IsShrinkToFit), Is.False);
		}

		[TestCase(1000.0, ModelFitIssue.None)]
		[TestCase(1000.1, ModelFitIssue.Huge)]
		[TestCase(5000.0, ModelFitIssue.Huge)]
		public void DesignTabHugeThreshold(double largest, ModelFitIssue expected)
		{
			Assert.That(ModelFit.Advise(10, largest, 10, false).Issue, Is.EqualTo(expected));
		}

		[Test]
		public void HugeOffersSmallerChoices()
		{
			var advice = ModelFit.Advise(2540, 100, 100, false);
			Assert.That(advice.Choices.Select(c => c.Scale), Is.EqualTo(new[] { 0.1, 1 / 25.4 }));
		}

		[Test]
		public void PrinterIgnoresTheDesignLimitWhenItFits()
		{
			// a big-bed printer: 1200 mm fits, so no prompt even though it is over the design-tab limit
			Assert.That(ModelFit.Advise(1200, 10, 10, true, 1500, 1500, 1500).Issue, Is.EqualTo(ModelFitIssue.None));
		}

		[TestCase(231, 10, 10)]
		[TestCase(10, 211, 10)]
		[TestCase(10, 10, 201)]
		public void TooBigOnAnyAxis(double x, double y, double z)
		{
			var advice = ModelFit.Advise(x, y, z, true, 230, 210, 200);
			Assert.That(advice.Issue, Is.EqualTo(ModelFitIssue.TooBigForBed));
			Assert.That(advice.Choices.Count, Is.EqualTo(1));
			Assert.That(advice.Choices[0].IsShrinkToFit, Is.True);
		}

		[Test]
		public void ExactlyBedSizeFits()
		{
			Assert.That(ModelFit.Advise(230, 210, 200, true, 230, 210, 200).Issue, Is.EqualTo(ModelFitIssue.None));
		}

		[Test]
		public void ShrinkUsesTheTightestAxisWithTwoPercentMargin()
		{
			// X: 230*0.98/460 = 0.49; Y: 210*0.98/100 = 2.058; Z: 200*0.98/50 = 3.92
			var scale = ModelFit.ShrinkToFitScale(460, 100, 50, 230, 210, 200, false);
			Assert.That(scale, Is.EqualTo(0.49).Within(1e-12));

			var advice = ModelFit.Advise(460, 100, 50, true, 230, 210, 200);
			Assert.That(advice.Choices[0].Scale, Is.EqualTo(0.49).Within(1e-12));
		}

		[Test]
		public void ShrunkModelFitsWithMargin()
		{
			var sizes = new[] { (500.0, 300.0, 50.0), (100.0, 100.0, 900.0), (240.0, 215.0, 205.0) };
			foreach (var (x, y, z) in sizes)
			{
				var s = ModelFit.ShrinkToFitScale(x, y, z, 230, 210, 200, false);
				Assert.That(ModelFit.Fits(x * s, y * s, z * s, 230 * 0.98 + 1e-9, 210 * 0.98 + 1e-9, 200 * 0.98 + 1e-9, false), Is.True);
				// uniform: the tightest axis touches the margin
				Assert.That(Math.Max(x * s / 230, Math.Max(y * s / 210, z * s / 200)), Is.EqualTo(0.98).Within(1e-9));
			}
		}

		[Test]
		public void UnsetBuildHeightIsNoLimit()
		{
			Assert.That(ModelFit.Advise(10, 10, 5000, true, 230, 210, 0).Issue, Is.EqualTo(ModelFitIssue.None));
			Assert.That(ModelFit.ShrinkToFitScale(460, 10, 5000, 230, 210, 0, false), Is.EqualTo(0.49).Within(1e-12));
		}

		[Test]
		public void NoLimitsAtAllGivesNoShrink()
		{
			Assert.That(ModelFit.ShrinkToFitScale(460, 10, 10, 0, 0, 0, false), Is.EqualTo(0));
			Assert.That(ModelFit.Advise(5000, 10, 10, true, 0, 0, 0).Issue, Is.EqualTo(ModelFitIssue.None));
		}

		[Test]
		public void RoundBedUsesTheDiagonal()
		{
			// 150 x 150 has a 212 mm diagonal: does not fit a 200 mm round bed though each side is under 200
			Assert.That(ModelFit.Fits(150, 150, 10, 200, 0, 200, true), Is.False);
			Assert.That(ModelFit.Fits(140, 140, 10, 200, 0, 200, true), Is.True);
			var s = ModelFit.ShrinkToFitScale(150, 150, 10, 200, 0, 200, true);
			Assert.That(Math.Sqrt(2) * 150 * s, Is.EqualTo(196).Within(1e-9));
		}

		[TestCase(0, 0, 0)]
		[TestCase(double.NaN, double.NaN, double.NaN)]
		[TestCase(-1, -1, -1)]
		public void NothingMeasurableIsNoIssue(double x, double y, double z)
		{
			Assert.That(ModelFit.Advise(x, y, z, false).Issue, Is.EqualTo(ModelFitIssue.None));
			Assert.That(ModelFit.Advise(x, y, z, true, 230, 210, 200).Issue, Is.EqualTo(ModelFitIssue.None));
		}

		[Test]
		public void FlatModelUsesItsLargestSide()
		{
			// a flat (2D) part with no height is judged on its width
			Assert.That(ModelFit.Advise(1.5, 1, 0, false).Issue, Is.EqualTo(ModelFitIssue.Tiny));
			Assert.That(ModelFit.Advise(50, 30, 0, false).Issue, Is.EqualTo(ModelFitIssue.None));
		}
	}

	[TestFixture]
	public class DesignMessagesTests
	{
		private static readonly Func<string, string> Identity = s => s;
		private static readonly Func<string, string> Bracket = s => "[" + s + "]";

		[Test]
		public void OperationFailed()
		{
			Assert.That(DesignMessages.OperationFailed("Subtract", Identity),
				Is.EqualTo("Subtract could not be completed. One of the parts may have gaps or overlapping surfaces; try Repair on it."));
			Assert.That(DesignMessages.OperationFailed("Combine", Bracket), Does.StartWith("[Combine could not"));
		}

		[Test]
		public void CouldNotOpenOneFile()
		{
			Assert.That(DesignMessages.CouldNotOpen(new[] { "bracket.stl" }, Identity),
				Is.EqualTo("Couldn't open bracket.stl: the file is damaged or is not a 3D model."));
		}

		[Test]
		public void CouldNotOpenThreeFiles()
		{
			Assert.That(DesignMessages.CouldNotOpen(new[] { "a.stl", "b.obj", "c.3mf" }, Identity),
				Is.EqualTo("Couldn't open a.stl, b.obj, c.3mf: the files are damaged or are not 3D models."));
		}

		[Test]
		public void CouldNotOpenListsThreeAndCountsTheRest()
		{
			Assert.That(DesignMessages.CouldNotOpen(new[] { "a.stl", "b.stl", "c.stl", "d.stl", "e.stl" }, Identity),
				Is.EqualTo("Couldn't open a.stl, b.stl, c.stl and 2 more: the files are damaged or are not 3D models."));
			Assert.That(DesignMessages.CouldNotOpen(new[] { "a", "b", "c", "d" }, Identity), Does.Contain("and 1 more"));
		}

		[Test]
		public void CouldNotOpenNothing()
		{
			Assert.That(DesignMessages.CouldNotOpen(new string[0], Identity), Is.Null);
			Assert.That(DesignMessages.CouldNotOpen(null, Identity), Is.Null);
			Assert.That(DesignMessages.CouldNotOpen(new[] { "", "  ", null }, Identity), Is.Null);
		}

		[Test]
		public void CouldNotOpenIsLocalized()
		{
			Assert.That(DesignMessages.CouldNotOpen(new[] { "a", "b", "c", "d" }, Bracket),
				Is.EqualTo("[Couldn't open a, b, c [and 1 more]: the files are damaged or are not 3D models.]"));
		}

		[Test]
		public void ModelPrompts()
		{
			Assert.That(DesignMessages.ModelTiny(0.787, Identity), Is.EqualTo("This model is only 0.79 mm across. Was it made in inches?"));
			Assert.That(DesignMessages.ModelTooBigForBed(230, 210, 200, Identity), Is.EqualTo("This model is bigger than the printer's bed (230 × 210 × 200 mm)."));
			Assert.That(DesignMessages.ModelHuge(2540.4, Identity), Does.StartWith("This model is 2540 mm across"));
		}

		[Test]
		public void ScaleChoiceButtons()
		{
			Assert.That(DesignMessages.ScaleChoice(new ModelFitChoice(25.4), Identity), Is.EqualTo("Make 25.4× bigger"));
			Assert.That(DesignMessages.ScaleChoice(new ModelFitChoice(10), Identity), Is.EqualTo("Make 10× bigger"));
			Assert.That(DesignMessages.ScaleChoice(new ModelFitChoice(0.1), Identity), Is.EqualTo("Make 10× smaller"));
			Assert.That(DesignMessages.ScaleChoice(new ModelFitChoice(1 / 25.4), Identity), Is.EqualTo("Make 25.4× smaller"));
			Assert.That(DesignMessages.ScaleChoice(new ModelFitChoice(0.49, true), Identity), Is.EqualTo("Shrink to fit"));
			Assert.That(DesignMessages.LeaveIt(Identity), Is.EqualTo("Leave it"));
		}

		[Test]
		public void EveryAdviceChoiceHasAButtonLabel()
		{
			foreach (var advice in new[] { ModelFit.Advise(1, 1, 1, false), ModelFit.Advise(3000, 1, 1, false), ModelFit.Advise(500, 1, 1, true, 230, 210, 200) })
			{
				Assert.That(advice.Choices, Is.Not.Empty);
				foreach (var choice in advice.Choices)
				{
					Assert.That(DesignMessages.ScaleChoice(choice, Identity), Is.Not.Empty);
				}
			}
		}

		[Test]
		public void NoProductNameInAnyText()
		{
			var all = new[]
			{
				DesignMessages.OperationFailed("X", Identity),
				DesignMessages.CouldNotOpen(new[] { "a", "b", "c", "d" }, Identity),
				DesignMessages.ModelTiny(1, Identity),
				DesignMessages.ModelTooBigForBed(1, 2, 3, Identity),
				DesignMessages.ModelHuge(2000, Identity),
				DesignMessages.ModelSizeTitle(Identity),
			};
			foreach (var text in all)
			{
				Assert.That(text, Does.Not.Contain("MatterControl"));
			}
		}
	}
}
