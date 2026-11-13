using System;
using System.Collections.Generic;
using System.Threading;
using MatterHackers.MatterControl.DesignTools.Solid;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>Pure seams of the solid code run for real: the tessellation retry, the STEP export outcome, the finished-build rule and NaN values.</summary>
	[TestFixture]
	public class SolidSeamTests
	{
		private sealed class TooBig : Exception
		{
		}

		private static bool IsTooBig(Exception ex) => ex is TooBig;

		[Test]
		public void FineTooBigThenCoarseSucceeds()
		{
			var calls = new List<(double linear, double degrees)>();
			var result = SolidTolerance.TessellateWithRetry(173.2, (l, d) =>
			{
				calls.Add((l, d));
				if (calls.Count == 1)
				{
					throw new TooBig();
				}

				return "coarse mesh";
			}, IsTooBig, CancellationToken.None);

			Assert.That(result, Is.EqualTo("coarse mesh"));
			Assert.That(calls, Has.Count.EqualTo(2));
			Assert.That(calls[0], Is.EqualTo(SolidTolerance.For(173.2, 0)));
			Assert.That(calls[1], Is.EqualTo(SolidTolerance.For(173.2, 1)));
		}

		[Test]
		public void FineSucceedsWithoutRetry()
		{
			int calls = 0;
			Assert.That(SolidTolerance.TessellateWithRetry(10, (l, d) => { calls++; return "fine"; }, IsTooBig, CancellationToken.None), Is.EqualTo("fine"));
			Assert.That(calls, Is.EqualTo(1));
		}

		[Test]
		public void BothTooBigGivesNull()
		{
			int calls = 0;
			var result = SolidTolerance.TessellateWithRetry<string>(10, (l, d) => { calls++; throw new TooBig(); }, IsTooBig, CancellationToken.None);
			Assert.That(result, Is.Null, "the caller reports TooDetailed");
			Assert.That(calls, Is.EqualTo(SolidTolerance.Attempts));
		}

		[Test]
		public void OtherFailuresAndCancellationGoToTheCaller()
		{
			Assert.Throws<InvalidOperationException>(() => SolidTolerance.TessellateWithRetry<string>(10, (l, d) => throw new InvalidOperationException(), IsTooBig, CancellationToken.None));

			using (var source = new CancellationTokenSource())
			{
				int calls = 0;
				Assert.Throws<OperationCanceledException>(() => SolidTolerance.TessellateWithRetry<string>(10, (l, d) =>
				{
					calls++;
					source.Cancel(); // cancelled while the fine attempt ran
					throw new TooBig();
				}, IsTooBig, source.Token));
				Assert.That(calls, Is.EqualTo(1), "no coarse attempt after a cancel");
			}
		}

		[Test]
		public void StepExportOutcome()
		{
			Assert.That(StepExportRules.Classify(null), Is.EqualTo(MatterHackers.MatterControl.DesignTools.Solid.StepExportOutcome.Saved));
			Assert.That(StepExportRules.Classify(new string[0]), Is.EqualTo(MatterHackers.MatterControl.DesignTools.Solid.StepExportOutcome.Saved));
			Assert.That(StepExportRules.Classify(new[] { StepExportRules.CancelledId }), Is.EqualTo(MatterHackers.MatterControl.DesignTools.Solid.StepExportOutcome.Cancelled));
			Assert.That(StepExportRules.Classify(new[] { StepExportRules.FailedId }), Is.EqualTo(MatterHackers.MatterControl.DesignTools.Solid.StepExportOutcome.Failed));
			Assert.That(StepExportRules.Classify(new[] { StepExportRules.CancelledId, StepExportRules.FailedId }), Is.EqualTo(MatterHackers.MatterControl.DesignTools.Solid.StepExportOutcome.Failed));
		}

		[TestCase(true, true, true, true)]   // in the scene: mesh and parents told
		[TestCase(true, false, true, false)]  // deleted while building (Undo may bring it back): mesh yes, parents no
		[TestCase(false, false, true, false)] // not added anywhere yet (Create, palette scaling): mesh yes
		public void FinishedBuildAlwaysAssignsItsMesh(bool hasParent, bool inChildren, bool expectMesh, bool expectNotify)
		{
			var (assign, notify) = SolidApplyRule.ForFinishedBuild(true, hasParent, inChildren);
			Assert.That(assign, Is.EqualTo(expectMesh));
			Assert.That(notify, Is.EqualTo(expectNotify));
			Assert.That(SolidApplyRule.ForFinishedBuild(false, hasParent, inChildren), Is.EqualTo((false, false)));
		}

		[Test]
		public void NaNValuesAreInvalidNotTheMinimum()
		{
			Assert.That(SolidLimits.ClampOrNaN(double.NaN, 0.01, 100), Is.NaN);
			Assert.That(SolidLimits.ClampOrNaN(double.PositiveInfinity, 0.01, 100), Is.NaN);
			Assert.That(SolidLimits.ClampOrNaN(-3, 0.01, 100), Is.EqualTo(0.01));

			// a NaN edge size would otherwise silently mean "no fillet"
			Assert.That(new SolidBoxSpec { Width = 20, Depth = 20, Height = 20, Treatment = SolidEdgeTreatment.Fillet, EdgeSize = double.NaN }.Check(), Is.EqualTo(SolidProblem.InvalidSize));
			Assert.That(new SolidCylinderSpec { Diameter = 20, Height = double.NaN }.Check(), Is.EqualTo(SolidProblem.InvalidSize));
			Assert.That(new SolidPlateSpec { Width = 40, Depth = 40, Height = 5, HoleDiameter = 8, HoleX = double.NaN }.Check(), Is.EqualTo(SolidProblem.InvalidSize));
		}
	}
}
