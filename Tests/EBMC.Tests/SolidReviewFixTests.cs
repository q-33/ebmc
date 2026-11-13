using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.DesignTools.Solid;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>Round-bed fit, tiny-model choices, the bed message, the STEP export decision, the tolerance rule and the kernel version check.</summary>
	[TestFixture]
	public class SolidReviewFixTests
	{
		private static string Identity(string s) => s;

		[Test]
		public void DiscFitsARoundBedOfItsOwnDiameter()
		{
			// a 100 mm disc: bounding box 100 x 100 (diagonal 141), footprint diameter 100
			var disc = Enumerable.Range(0, 360).Select(a => (50 * Math.Cos(a * Math.PI / 180), 50 * Math.Sin(a * Math.PI / 180)));
			var footprint = ModelFit.FootprintDiameter(disc, 0, 0);
			Assert.That(footprint, Is.EqualTo(100).Within(1e-9));
			Assert.That(ModelFit.Fits(100, 100, 5, 100, 0, 200, true, footprint), Is.True);
			Assert.That(ModelFit.Advise(100, 100, 5, true, 100, 0, 200, roundBed: true, footprintDiameter: footprint).Issue, Is.EqualTo(ModelFitIssue.None));

			// without a measured footprint the diagonal is still used (too strict, but never too lenient)
			Assert.That(ModelFit.Fits(100, 100, 5, 100, 0, 200, true), Is.False);

			// a square of the same box does not fit: its corners are 70.7 mm from the centre
			var square = new[] { (-50.0, -50.0), (50.0, -50.0), (50.0, 50.0), (-50.0, 50.0) };
			var squareFootprint = ModelFit.FootprintDiameter(square, 0, 0);
			Assert.That(ModelFit.Fits(100, 100, 5, 100, 0, 200, true, squareFootprint), Is.False);
			var shrink = ModelFit.ShrinkToFitScale(100, 100, 5, 100, 0, 200, true, squareFootprint);
			Assert.That(shrink, Is.EqualTo(100 * 0.98 / squareFootprint).Within(1e-12));
		}

		[Test]
		public void RoundFootprintStaysBetweenTheSideAndTheDiagonal()
		{
			Assert.That(ModelFit.RoundFootprint(100, 60, 0), Is.EqualTo(Math.Sqrt(13600)).Within(1e-9));
			Assert.That(ModelFit.RoundFootprint(100, 60, 50), Is.EqualTo(100), "never less than the larger side");
			Assert.That(ModelFit.RoundFootprint(100, 60, 500), Is.EqualTo(Math.Sqrt(13600)).Within(1e-9), "never more than the diagonal");
		}

		[Test]
		public void TinyChoicesThatWouldNotFitAreLeftOut()
		{
			// 1.5 mm part on a 30 x 30 x 30 printer: 25.4x (38 mm) does not fit, 10x (15 mm) does
			var advice = ModelFit.Advise(1.5, 1, 1, true, 30, 30, 30);
			Assert.That(advice.Issue, Is.EqualTo(ModelFitIssue.Tiny));
			Assert.That(advice.Choices.Select(c => c.Scale), Is.EqualTo(new[] { ModelFit.CentimetresToMm }));

			// on a 12 mm printer neither 25.4x (38 mm) nor 10x (15 mm) fits: no prompt at all
			Assert.That(ModelFit.Advise(1.5, 1, 1, true, 12, 12, 12).Issue, Is.EqualTo(ModelFitIssue.None));

			// on a normal bed both are offered
			Assert.That(ModelFit.Advise(1.5, 1, 1, true, 230, 210, 200).Choices, Has.Count.EqualTo(2));

			// on a design tab the design limit applies
			Assert.That(ModelFit.Advise(1.9, 1, 1, false).Choices, Has.Count.EqualTo(2));
		}

		[Test]
		public void BedMessageLeavesOutUnsetSizes()
		{
			Assert.That(DesignMessages.ModelTooBigForBed(230, 210, 0, Identity), Is.EqualTo("This model is bigger than the printer's bed (230 × 210 mm)."));
			Assert.That(DesignMessages.ModelTooBigForBed(230, 210, 200, Identity), Is.EqualTo("This model is bigger than the printer's bed (230 × 210 × 200 mm)."));
			Assert.That(DesignMessages.ModelTooBigForBed(200, 200, 180, Identity, roundBed: true), Is.EqualTo("This model is bigger than the printer's bed (Ø200 × 180 mm)."));
			Assert.That(DesignMessages.ModelTooBigForBed(0, 0, 0, Identity), Is.EqualTo("This model is bigger than the printer's bed."));
			Assert.That(DesignMessages.ModelTooBigForBed(230, 210, 0, Identity), Does.Not.Contain("× 0"));
		}

		private sealed class Node
		{
			public string Name;
			public bool Solid;
			public bool Mesh;
			public bool Visible = true;
			public List<Node> Children = new List<Node>();

			public Node(string name, bool solid = false, bool mesh = false, params Node[] children)
			{
				Name = name;
				Solid = solid;
				Mesh = mesh;
				Children.AddRange(children);
			}
		}

		private static List<(Node item, List<Node> ancestors)> Collect(Node root)
		{
			return StepExportRules.Collect(root, n => n.Solid, n => n.Mesh, n => n.Children.Where(c => c.Visible));
		}

		[Test]
		public void StepExportDecision()
		{
			// a single solid
			var single = Collect(new Node("box", solid: true));
			Assert.That(single.Select(f => f.item.Name), Is.EqualTo(new[] { "box" }));
			Assert.That(single[0].ancestors, Is.Empty);

			// a scene holding a group of two solids: both found, with their parents root first
			var group = new Node("group", false, false, new Node("a", solid: true), new Node("b", solid: true));
			var scene = new Node("scene", false, false, group);
			var found = Collect(scene);
			Assert.That(found.Select(f => f.item.Name), Is.EqualTo(new[] { "a", "b" }));
			Assert.That(found[0].ancestors.Select(a => a.Name), Is.EqualTo(new[] { "scene", "group" }));

			// any mesh part means no STEP
			Assert.That(Collect(new Node("scene", false, false, new Node("a", solid: true), new Node("stl", mesh: true))), Is.Null);

			// a Combine: its visible result is a mesh even though its hidden source holds solids
			var source = new Node("source", false, false, new Node("a", solid: true)) { Visible = false };
			Assert.That(Collect(new Node("combine", false, false, source, new Node("result", mesh: true))), Is.Null);

			// nothing solid at all
			Assert.That(Collect(new Node("empty")), Is.Null);

			// every root must qualify; no roots means no
			Assert.That(StepExportRules.Applies(new[] { true, true }), Is.True);
			Assert.That(StepExportRules.Applies(new[] { true, false }), Is.False);
			Assert.That(StepExportRules.Applies(new bool[0]), Is.False);
			Assert.That(StepExportRules.Applies(null), Is.False);
		}

		[TestCase(10, 0.02, 5)]
		[TestCase(173.2, 0.02, 5)]
		[TestCase(2000, 0.2, 5)]
		[TestCase(-1, 0.02, 5)]
		public void ToleranceGrowsWithSize(double diagonal, double linear, double degrees)
		{
			var (l, d) = SolidTolerance.For(diagonal, 0);
			Assert.That(l, Is.EqualTo(linear).Within(1e-12));
			Assert.That(d, Is.EqualTo(degrees));

			var (coarseLinear, coarseDegrees) = SolidTolerance.For(diagonal, 1);
			Assert.That(coarseLinear, Is.EqualTo(linear * SolidTolerance.CoarseFactor).Within(1e-12));
			Assert.That(coarseDegrees, Is.EqualTo(SolidTolerance.CoarseAngularDegrees));
		}

		[Test]
		public void TooLargeAndTooDetailedHavePlainMessages()
		{
			Assert.That(SolidMessages.For(SolidProblem.StepTooLarge, SolidShapeKind.Box, Identity, "big.step"), Does.StartWith("big.step is larger than 50 MB"));
			Assert.That(SolidMessages.For(SolidProblem.TooDetailed, SolidShapeKind.Box, Identity), Does.StartWith("This part is too detailed"));
			Assert.That(SolidTolerance.MaxStepFileBytes, Is.EqualTo(50L * 1024 * 1024));
		}

		[TestCase("0.3", true)]
		[TestCase("0.9", true)]
		[TestCase("0.2", false)]
		[TestCase("0.1", false)]
		[TestCase("1.2", false)]
		public void KernelVersionNeedsTheSameMajorAndANewerMinor(string version, bool compatible)
		{
			Assert.That(SolidKernel.IsCompatible(Version.Parse(version)), Is.EqualTo(compatible));
		}
	}
}
