using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.MatterControl.DesignTools.Solid;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The exact-solid rules without the kernel: which edges an "Edges" choice picks, parameter limits, the message
	/// for each kernel error and the mesh clean-up, using made-up edge descriptors and tessellations.
	/// </summary>
	[TestFixture]
	public class SolidRulesTests
	{
		/// <summary>The 12 edges of a w×d×h box centred on the origin, in a scrambled order with arbitrary indices.</summary>
		private static List<SolidEdge> BoxEdges(double w, double d, double h)
		{
			double x = w / 2, y = d / 2, z = h / 2;
			var edges = new List<SolidEdge>();
			int i = 100;
			foreach (var sx in new[] { -x, x })
			{
				foreach (var sy in new[] { -y, y })
				{
					edges.Add(SolidEdge.Line(i++, sx, sy, 0, 0, 0, sy > 0 ? 1 : -1)); // vertical, either direction
				}
			}

			foreach (var sz in new[] { -z, z })
			{
				edges.Add(SolidEdge.Line(i++, 0, -y, sz, 1, 0, 0));
				edges.Add(SolidEdge.Line(i++, 0, y, sz, -1, 0, 0));
				edges.Add(SolidEdge.Line(i++, -x, 0, sz, 0, 1, 0));
				edges.Add(SolidEdge.Line(i++, x, 0, sz, 0, -1, 0));
			}

			// reverse so the selection cannot depend on input order
			edges.Reverse();
			return edges;
		}

		private static int[] Pick(SolidBoxEdges which) => SolidEdgeSelection.BoxEdges(BoxEdges(40, 30, 20), -10, 10, which, 1e-6);

		[Test]
		public void BoxEdgeChoicesPickTheRightCounts()
		{
			Assert.That(Pick(SolidBoxEdges.All), Has.Length.EqualTo(12));
			Assert.That(Pick(SolidBoxEdges.Vertical_Only), Is.EqualTo(new[] { 100, 101, 102, 103 }));
			Assert.That(Pick(SolidBoxEdges.Top_Only), Is.EqualTo(new[] { 108, 109, 110, 111 }));
			Assert.That(Pick(SolidBoxEdges.Top_And_Bottom), Is.EqualTo(new[] { 104, 105, 106, 107, 108, 109, 110, 111 }));
		}

		[Test]
		public void BoxEdgeSelectionIsSortedAndDeterministic()
		{
			var first = Pick(SolidBoxEdges.All);
			Assert.That(first, Is.Ordered);
			Assert.That(first, Is.Unique);
			var shuffled = BoxEdges(40, 30, 20).OrderBy(e => (e.Index * 7919) % 13).ToList();
			Assert.That(SolidEdgeSelection.BoxEdges(shuffled, -10, 10, SolidBoxEdges.All, 1e-6), Is.EqualTo(first));
		}

		[Test]
		public void BoxSelectionIgnoresCurvesAndDegenerateEdges()
		{
			var edges = BoxEdges(40, 30, 20);
			edges.Add(SolidEdge.Circle(500, 0, 0, 10, 3));
			edges.Add(new SolidEdge { Index = 501, CurveType = SolidEdge.CurveLine, MidZ = 10, TanX = 1, IsDegenerate = true });
			edges.Add(SolidEdge.Line(502, 0, 0, 3, 1, 0, 0)); // horizontal but in the middle: not top or bottom
			var all = SolidEdgeSelection.BoxEdges(edges, -10, 10, SolidBoxEdges.All, 1e-6);
			Assert.That(all, Has.Length.EqualTo(12));
			Assert.That(all, Has.None.AnyOf(500, 501, 502));
		}

		[Test]
		public void CylinderRimsByHeight()
		{
			var edges = new List<SolidEdge>
			{
				SolidEdge.Line(0, 10, 0, 0, 0, 0, 1), // seam
				SolidEdge.Circle(1, 0, 0, 15, 10),
				SolidEdge.Circle(2, 0, 0, -15, 10),
			};
			Assert.That(SolidEdgeSelection.CylinderEdges(edges, -15, 15, 10, SolidCylinderEdges.Top, 1e-6), Is.EqualTo(new[] { 1 }));
			Assert.That(SolidEdgeSelection.CylinderEdges(edges, -15, 15, 10, SolidCylinderEdges.Bottom, 1e-6), Is.EqualTo(new[] { 2 }));
			Assert.That(SolidEdgeSelection.CylinderEdges(edges, -15, 15, 10, SolidCylinderEdges.Both, 1e-6), Is.EqualTo(new[] { 1, 2 }));
		}

		[Test]
		public void HoleRimIsTheTopCircleOfTheHoleOnly()
		{
			var edges = BoxEdges(40, 40, 5);
			edges.Add(SolidEdge.Circle(200, 6, -4, 2.5, 3));  // top rim of the hole
			edges.Add(SolidEdge.Circle(201, 6, -4, -2.5, 3)); // bottom rim
			edges.Add(SolidEdge.Circle(202, 0, 0, 2.5, 3));   // same radius, elsewhere
			Assert.That(SolidEdgeSelection.HoleRimEdges(edges, 2.5, 6, -4, 3, 1e-6), Is.EqualTo(new[] { 200 }));
		}

		[TestCase(40, 30, 20, SolidBoxEdges.All, 10)]
		[TestCase(40, 30, 20, SolidBoxEdges.Vertical_Only, 15)]
		[TestCase(40, 30, 10, SolidBoxEdges.Top_Only, 10)]
		[TestCase(40, 30, 20, SolidBoxEdges.Top_Only, 15)]
		[TestCase(40, 30, 20, SolidBoxEdges.Top_And_Bottom, 10)]
		public void BoxEdgeSizeLimit(double w, double d, double h, SolidBoxEdges edges, double expected)
		{
			Assert.That(SolidLimits.MaxBoxEdgeSize(w, d, h, edges), Is.EqualTo(expected));
		}

		[Test]
		public void BoxCheckReportsTooLargeBeforeTheKernelRuns()
		{
			var spec = new SolidBoxSpec { Width = 20, Depth = 20, Height = 20, Treatment = SolidEdgeTreatment.Fillet, EdgeSize = 10, Edges = SolidBoxEdges.All };
			Assert.That(spec.Check(), Is.EqualTo(SolidProblem.FilletTooLarge));
			spec.Treatment = SolidEdgeTreatment.Chamfer;
			Assert.That(spec.Check(), Is.EqualTo(SolidProblem.ChamferTooLarge));
			spec.EdgeSize = 9.9;
			Assert.That(spec.Check(), Is.EqualTo(SolidProblem.None));
			spec.Treatment = SolidEdgeTreatment.None;
			spec.EdgeSize = 100;
			Assert.That(spec.Check(), Is.EqualTo(SolidProblem.None), "size is ignored without a treatment");
			spec.Width = 0;
			Assert.That(spec.Check(), Is.EqualTo(SolidProblem.InvalidSize));
		}

		[Test]
		public void CylinderAndPlateChecks()
		{
			var cylinder = new SolidCylinderSpec { Diameter = 20, Height = 6, Treatment = SolidEdgeTreatment.Fillet, EdgeSize = 3, Edges = SolidCylinderEdges.Both };
			Assert.That(cylinder.Check(), Is.EqualTo(SolidProblem.FilletTooLarge));
			cylinder.Edges = SolidCylinderEdges.Top;
			Assert.That(cylinder.Check(), Is.EqualTo(SolidProblem.None));

			var plate = new SolidPlateSpec { Width = 40, Depth = 30, Height = 5, HoleDiameter = 10, HoleX = 16, HoleY = 0 };
			Assert.That(plate.Check(), Is.EqualTo(SolidProblem.HoleDoesNotFit), "16 + 5 > 20");
			plate.HoleX = 15;
			Assert.That(plate.Check(), Is.EqualTo(SolidProblem.HoleDoesNotFit), "the hole would touch the edge");
			plate.HoleX = 10;
			Assert.That(plate.Check(), Is.EqualTo(SolidProblem.None));
			plate.Rim = SolidHoleRim.Countersink;
			plate.RimSize = 5;
			Assert.That(plate.Check(), Is.EqualTo(SolidProblem.CountersinkTooLarge), "the wall left is 5 mm");
			plate.RimSize = 1;
			Assert.That(plate.Check(), Is.EqualTo(SolidProblem.None));
		}

		[Test]
		public void ClampingKeepsValuesInRange()
		{
			Assert.That(SolidLimits.ClampSize(-5), Is.EqualTo(SolidLimits.MinSize));
			Assert.That(SolidLimits.ClampSize(double.NaN), Is.EqualTo(SolidLimits.MinSize));
			Assert.That(SolidLimits.ClampSize(1e9), Is.EqualTo(SolidLimits.MaxSize));
			Assert.That(SolidLimits.ClampSize(12.5), Is.EqualTo(12.5));
			Assert.That(SolidLimits.ClampEdgeSize(-1), Is.EqualTo(0));
			Assert.That(SolidLimits.ClampOffset(-1e9), Is.EqualTo(-SolidLimits.MaxSize));
		}

		[TestCase(4, SolidStep.Fillet, SolidProblem.FilletTooLarge)]
		[TestCase(5, SolidStep.Fillet, SolidProblem.FilletTooLarge)]
		[TestCase(4, SolidStep.Chamfer, SolidProblem.ChamferTooLarge)]
		[TestCase(4, SolidStep.Countersink, SolidProblem.CountersinkTooLarge)]
		[TestCase(3, SolidStep.Fillet, SolidProblem.KernelRefused)]
		[TestCase(2, SolidStep.Make, SolidProblem.InvalidSize)]
		[TestCase(8, SolidStep.ReadStep, SolidProblem.StepUnreadable)]
		[TestCase(4, SolidStep.ReadStep, SolidProblem.StepUnreadable)]
		[TestCase(8, SolidStep.WriteStep, SolidProblem.StepNotWritten)]
		[TestCase(9, SolidStep.Tessellate, SolidProblem.TooDetailed)]
		[TestCase(9, SolidStep.ReadStep, SolidProblem.TooDetailed)]
		[TestCase(4, SolidStep.Cut, SolidProblem.KernelRefused)]
		[TestCase(0, SolidStep.Cut, SolidProblem.None)]
		public void KernelCodesBecomeProblems(int code, SolidStep step, SolidProblem expected)
		{
			Assert.That(SolidMessages.FromKernel(code, step), Is.EqualTo(expected));
		}

		[Test]
		public void MessagesArePlainWords()
		{
			Assert.That(SolidMessages.For(SolidProblem.FilletTooLarge, SolidShapeKind.Box, s => s),
				Is.EqualTo("That fillet is too large for this box. The previous shape is kept."));
			Assert.That(SolidMessages.For(SolidProblem.ChamferTooLarge, SolidShapeKind.Cylinder, s => s),
				Does.StartWith("That chamfer is too large for this cylinder."));
			Assert.That(SolidMessages.For(SolidProblem.StepUnreadable, SolidShapeKind.Box, s => s, "bracket.step"),
				Does.Contain("bracket.step"));
			Assert.That(SolidMessages.For(SolidProblem.None, SolidShapeKind.Box, s => s), Is.Null);
			Assert.That(SolidMessages.For(SolidProblem.Cancelled, SolidShapeKind.Box, s => s), Is.Null);

			foreach (SolidProblem problem in Enum.GetValues(typeof(SolidProblem)))
			{
				foreach (SolidShapeKind kind in Enum.GetValues(typeof(SolidShapeKind)))
				{
					var text = SolidMessages.For(problem, kind, s => s);
					if (text != null)
					{
						Assert.That(text, Does.Not.Contain("MatterControl").And.Not.Contain("OCCT").And.Not.Contain("kernel"), problem.ToString());
					}
				}
			}

			// the localize delegate is applied to the templates
			Assert.That(SolidMessages.For(SolidProblem.FilletTooLarge, SolidShapeKind.Box, s => "[" + s + "]"), Does.StartWith("[That fillet"));
		}

		[Test]
		public void ShimVersionParsing()
		{
			Assert.That(SolidKernel.ParseShimVersion("ebmc_occt 0.2; OCCT 8.0.1"), Is.EqualTo(new Version(0, 2)));
			Assert.That(SolidKernel.ParseShimVersion("ebmc_occt 1.12; OCCT 9"), Is.EqualTo(new Version(1, 12)));
			Assert.That(SolidKernel.ParseShimVersion("OCCT 8.0.1"), Is.Null);
			Assert.That(SolidKernel.ParseShimVersion(null), Is.Null);
			Assert.That(SolidKernel.IsCompatible(SolidKernel.ParseShimVersion("ebmc_occt 0.2; OCCT 8.0.1")), Is.False, "0.2 has no triangle cap");
			Assert.That(SolidKernel.IsCompatible(SolidKernel.ParseShimVersion("ebmc_occt 0.3; OCCT 8.0.1")), Is.True);
		}

		[Test]
		public void MesherDropsBadTrianglesAndKeepsFaceIdsInStep()
		{
			var tessellation = new SolidTessellation
			{
				Vertices = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1 },
				Triangles = new[]
				{
					0, 2, 1, // face 7
					0, 1, 1, // repeated index
					0, 1, 3, // face 8
					0, 9, 3, // index out of range
					-1, 1, 2, // negative index
					1, 2, 3, // face 9
					0, 3, 2, // face 10
				},
				TriangleFaces = new[] { 7, 7, 8, 8, 8, 9, 10 },
			};

			int dropped = tessellation.Clean(out var triangles, out var faces);
			Assert.That(dropped, Is.EqualTo(3));
			Assert.That(triangles, Is.EqualTo(new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 0, 3, 2 }));
			Assert.That(faces, Is.EqualTo(new[] { 7, 8, 9, 10 }));

			// that tetrahedron is closed
			SolidTessellation.CountOpenEdges(triangles, out int open, out int nonManifold);
			Assert.That(open, Is.EqualTo(0));
			Assert.That(nonManifold, Is.EqualTo(0));
		}

		[Test]
		public void MesherCopesWithMissingFaceIdsAndBadPolylineOffsets()
		{
			var tessellation = new SolidTessellation
			{
				Vertices = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
				Triangles = new[] { 0, 1, 2 },
				TriangleFaces = new int[0],
				EdgeIds = new[] { 4, 5 },
				EdgeOffsets = new[] { 0, 2, 9 },
				EdgePoints = new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0 },
			};

			tessellation.Clean(out var triangles, out var faces);
			Assert.That(triangles, Has.Length.EqualTo(3));
			Assert.That(faces, Is.EqualTo(new[] { -1 }));
			Assert.That(tessellation.EdgePolyline(0), Is.EqualTo(new double[] { 0, 0, 0, 1, 0, 0 }));
			Assert.That(tessellation.EdgePolyline(1), Is.Empty, "offset 9 is past the points");
			Assert.That(tessellation.EdgePolyline(2), Is.Empty);
			SolidTessellation.CountOpenEdges(triangles, out int open, out _);
			Assert.That(open, Is.EqualTo(3));
		}
	}
}
