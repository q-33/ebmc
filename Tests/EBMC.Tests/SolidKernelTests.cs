using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MatterHackers.MatterControl.DesignTools.Solid;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The exact solids built by the real kernel (libebmc_occt). These run only where native/occt/build.sh has staged
	/// the library (native/occt/dist/osx-arm64); elsewhere every test reports Ignored with the reason.
	/// </summary>
	[TestFixture]
	public class SolidKernelTests
	{
		[OneTimeSetUp]
		public void RequireKernel()
		{
			if (!SolidKernel.IsAvailable)
			{
				Assert.Ignore("Solid kernel not available, kernel-backed tests skipped: " + SolidKernel.UnavailableReason);
			}

			TestContext.Progress.WriteLine("Solid kernel: " + SolidKernel.Version);
		}

		private static double Volume(Func<OcctShape> make)
		{
			using (var shape = make())
			{
				return EbmcOcct.Volume(shape);
			}
		}

		private static int Faces(Func<OcctShape> make)
		{
			using (var shape = make())
			{
				EbmcOcct.Counts(shape, out int faces, out _, out _);
				return faces;
			}
		}

		private static SolidBoxSpec Box(double w, double d, double h, SolidEdgeTreatment treatment = SolidEdgeTreatment.None, double size = 0, SolidBoxEdges edges = SolidBoxEdges.All)
		{
			return new SolidBoxSpec { Width = w, Depth = d, Height = h, Treatment = treatment, EdgeSize = size, Edges = edges };
		}

		[Test]
		public void BoxVolumeAndCentring()
		{
			using (var shape = SolidBuilders.Box(Box(40, 30, 20)))
			{
				Assert.That(EbmcOcct.Volume(shape), Is.EqualTo(24000).Within(1e-6));
				var bounds = EbmcOcct.BoundingBox(shape);
				Assert.That(bounds, Is.EqualTo(new double[] { -20, -15, -10, 20, 15, 10 }).Within(1e-6));
			}
		}

		[Test]
		public void FilletedBoxVolumeMatchesTheFormula()
		{
			// a w×d×h box with every edge rounded to r: V = wdh - (4 - π) r² (w + d + h) + (16 - 14π/3) r³
			double w = 40, d = 30, h = 20, r = 3;
			var expected = w * d * h - (4 - Math.PI) * r * r * (w + d + h) + (16 - 14 * Math.PI / 3) * r * r * r;
			var actual = Volume(() => SolidBuilders.Box(Box(w, d, h, SolidEdgeTreatment.Fillet, r, SolidBoxEdges.All)));
			Assert.That(actual, Is.EqualTo(expected).Within(expected * 1e-7));

			// vertical edges only: four quarter-round columns
			var vertical = w * d * h - (4 - Math.PI) * r * r * h;
			Assert.That(Volume(() => SolidBuilders.Box(Box(w, d, h, SolidEdgeTreatment.Fillet, r, SolidBoxEdges.Vertical_Only))), Is.EqualTo(vertical).Within(vertical * 1e-7));

			// chamfer on the top edges: four prisms of section c²/2, overlapping at the corners
			double c = 2;
			var chamfered = Volume(() => SolidBuilders.Box(Box(w, d, h, SolidEdgeTreatment.Chamfer, c, SolidBoxEdges.Top_Only)));
			var chamferExpected = w * d * h - c * c / 2 * 2 * (w + d) + 4 * (c * c * c / 3);
			Assert.That(chamfered, Is.EqualTo(chamferExpected).Within(chamferExpected * 1e-7));
		}

		[Test]
		public void EdgeChoicesGiveTheExpectedFaceCounts()
		{
			Assert.That(Faces(() => SolidBuilders.Box(Box(40, 30, 20))), Is.EqualTo(6));
			Assert.That(Faces(() => SolidBuilders.Box(Box(40, 30, 20, SolidEdgeTreatment.Fillet, 3, SolidBoxEdges.Top_Only))), Is.EqualTo(6 + 4));
			Assert.That(Faces(() => SolidBuilders.Box(Box(40, 30, 20, SolidEdgeTreatment.Fillet, 3, SolidBoxEdges.Vertical_Only))), Is.EqualTo(6 + 4));
			Assert.That(Faces(() => SolidBuilders.Box(Box(40, 30, 20, SolidEdgeTreatment.Fillet, 3, SolidBoxEdges.Top_And_Bottom))), Is.EqualTo(6 + 8));
			// 12 edge rounds plus 8 corner patches
			Assert.That(Faces(() => SolidBuilders.Box(Box(40, 30, 20, SolidEdgeTreatment.Fillet, 3, SolidBoxEdges.All))), Is.EqualTo(6 + 12 + 8));
		}

		[Test]
		public void CylinderRims()
		{
			double d = 20, h = 10, c = 1;
			var plain = Volume(() => SolidBuilders.Cylinder(new SolidCylinderSpec { Diameter = d, Height = h }));
			Assert.That(plain, Is.EqualTo(Math.PI * 100 * h).Within(1e-6));

			// a 45° chamfer of c on one rim removes a ring of triangular section: π c² (r - c/3)
			var oneRim = Volume(() => SolidBuilders.Cylinder(new SolidCylinderSpec { Diameter = d, Height = h, Treatment = SolidEdgeTreatment.Chamfer, EdgeSize = c, Edges = SolidCylinderEdges.Top }));
			var expected = Math.PI * 100 * h - Math.PI * c * c * (d / 2 - c / 3);
			Assert.That(oneRim, Is.EqualTo(expected).Within(expected * 1e-6));

			Assert.That(Faces(() => SolidBuilders.Cylinder(new SolidCylinderSpec { Diameter = d, Height = h, Treatment = SolidEdgeTreatment.Fillet, EdgeSize = 2, Edges = SolidCylinderEdges.Both })), Is.EqualTo(5));
		}

		[Test]
		public void PlateWithHoleVolume()
		{
			var spec = new SolidPlateSpec { Width = 40, Depth = 30, Height = 5, HoleDiameter = 8, HoleX = 6, HoleY = -4 };
			var expected = 40 * 30 * 5 - Math.PI * 16 * 5;
			using (var shape = SolidBuilders.Plate(spec))
			{
				Assert.That(EbmcOcct.Volume(shape), Is.EqualTo(expected).Within(expected * 1e-9));
				Assert.That(EbmcOcct.IsValid(shape), Is.True);
			}

			// a 45° countersink of c on the top rim removes π c² (r + c/3)
			spec.Rim = SolidHoleRim.Countersink;
			spec.RimSize = 1;
			var countersunk = expected - Math.PI * 1 * (4 + 1.0 / 3);
			Assert.That(Volume(() => SolidBuilders.Plate(spec)), Is.EqualTo(countersunk).Within(countersunk * 1e-6));
		}

		[Test]
		public void TooLargeFilletIsRefusedByTheKernelWithoutCrashing()
		{
			// the kernel itself refuses (the pre-check would have stopped this radius before calling it)
			using (var box = EbmcOcct.MakeBox(20, 20, 20))
			{
				var all = Enumerable.Range(0, 12).ToArray();
				var ex = Assert.Throws<OcctException>(() => EbmcOcct.Fillet(box, all, 10.5).Dispose());
				Assert.That(SolidMessages.FromKernel(ex.Code, SolidStep.Fillet), Is.EqualTo(SolidProblem.FilletTooLarge));
				Assert.That(EbmcOcct.Volume(box), Is.EqualTo(8000).Within(1e-6), "library still usable");
			}

			var result = SolidBuilders.Build(() => SolidBuilders.Box(Box(20, 20, 20, SolidEdgeTreatment.Fillet, 10, SolidBoxEdges.All)), CancellationToken.None);
			Assert.That(result.Succeeded, Is.False);
			Assert.That(result.Problem, Is.EqualTo(SolidProblem.FilletTooLarge));
		}

		[Test]
		public void MeshIsClosedAndConsistent()
		{
			var result = SolidBuilders.Build(
				() => SolidBuilders.Plate(new SolidPlateSpec { Width = 40, Depth = 30, Height = 5, HoleDiameter = 8, Rim = SolidHoleRim.Countersink, RimSize = 1 }),
				CancellationToken.None,
				withProperties: true);
			Assert.That(result.Succeeded, Is.True, result.Detail);

			var tessellation = result.Tessellation;
			int dropped = tessellation.Clean(out var triangles, out var faces);
			Assert.That(dropped, Is.EqualTo(0));
			Assert.That(faces.All(f => f >= 0 && f < result.FaceCount), Is.True, "every triangle has a valid face id");
			Assert.That(faces.Distinct().Count(), Is.EqualTo(result.FaceCount), "every face is meshed");
			SolidTessellation.CountOpenEdges(triangles, out int open, out int nonManifold);
			Assert.That(open, Is.EqualTo(0), "closed");
			Assert.That(nonManifold, Is.EqualTo(0));

			// signed volume of the mesh is close to the exact one, and positive (counter-clockwise from outside)
			double volume = 0;
			var v = tessellation.Vertices;
			for (int t = 0; t < triangles.Length; t += 3)
			{
				int a = 3 * triangles[t], b = 3 * triangles[t + 1], c = 3 * triangles[t + 2];
				volume += (v[a] * (v[b + 1] * v[c + 2] - v[b + 2] * v[c + 1])
					- v[a + 1] * (v[b] * v[c + 2] - v[b + 2] * v[c])
					+ v[a + 2] * (v[b] * v[c + 1] - v[b + 1] * v[c])) / 6;
			}

			Assert.That(volume, Is.EqualTo(result.Volume).Within(result.Volume * 2e-3));
			Assert.That(tessellation.EdgeIds.Length, Is.EqualTo(result.EdgeCount), "a polyline for every edge");
		}

		[Test]
		public void StepRoundTrip()
		{
			var path = Path.Combine(Path.GetTempPath(), "ebmc-solid-test-" + Guid.NewGuid().ToString("N") + ".step");
			try
			{
				double before;
				using (var shape = SolidBuilders.Box(Box(40, 30, 20, SolidEdgeTreatment.Fillet, 3, SolidBoxEdges.Top_Only)))
				using (var moved = SolidBuilders.Transform(shape, new double[] { 0, -1, 0, 50, 1, 0, 0, 0, 0, 0, 1, 10 }))
				{
					before = EbmcOcct.Volume(moved);
					SolidBuilders.WriteStep(moved, path);
				}

				Assert.That(new FileInfo(path).Length, Is.GreaterThan(1000));
				using (var read = SolidBuilders.ReadStep(path))
				{
					Assert.That(EbmcOcct.Volume(read), Is.EqualTo(before).Within(before * 1e-9));
					EbmcOcct.Counts(read, out int faces, out _, out _);
					Assert.That(faces, Is.EqualTo(10));
					var bounds = EbmcOcct.BoundingBox(read);
					Assert.That(bounds[0], Is.EqualTo(35).Within(1e-6), "rotated 90° then moved 50 in x");
					Assert.That(bounds[5], Is.EqualTo(20).Within(1e-6));
				}

				File.WriteAllText(path, "not a step file");
				var ex = Assert.Throws<SolidBuildException>(() => SolidBuilders.ReadStep(path).Dispose());
				Assert.That(ex.Problem, Is.EqualTo(SolidProblem.StepUnreadable));
			}
			finally
			{
				File.Delete(path);
			}
		}

		[Test]
		public void AssembleMakesACompoundOrCutsHoles()
		{
			using (var a = SolidBuilders.Box(Box(10, 10, 10)))
			using (var b0 = SolidBuilders.Box(Box(10, 10, 10)))
			using (var b = SolidBuilders.Transform(b0, new double[] { 1, 0, 0, 5, 0, 1, 0, 0, 0, 0, 1, 0 }))
			using (var hole0 = SolidBuilders.Cylinder(new SolidCylinderSpec { Diameter = 2, Height = 20 }))
			{
				using (var compound = SolidBuilders.Assemble(new[] { a, b }, null))
				{
					EbmcOcct.Counts(compound, out int faces, out _, out _);
					Assert.That(faces, Is.EqualTo(12), "bodies kept separate");
				}

				using (var cut = SolidBuilders.Assemble(new[] { a, b }, new[] { hole0 }))
				{
					var expected = 15 * 10 * 10 - Math.PI * 1 * 10;
					Assert.That(EbmcOcct.Volume(cut), Is.EqualTo(expected).Within(expected * 1e-9));
				}
			}
		}

		[Test]
		public void TriangleCapStopsBeforeAnyOutput()
		{
			using (var box = SolidBuilders.Box(Box(100, 100, 100, SolidEdgeTreatment.Fillet, 5, SolidBoxEdges.All)))
			{
				var full = EbmcOcct.Tessellate(box, 0.02, 5 * Math.PI / 180, 0);
				var ex = Assert.Throws<OcctException>(() => EbmcOcct.Tessellate(box, 0.02, 5 * Math.PI / 180, full.TriangleCount - 1));
				Assert.That(ex.Code, Is.EqualTo(EbmcOcct.ErrTooBig));
				Assert.That(EbmcOcct.Tessellate(box, 0.02, 5 * Math.PI / 180, full.TriangleCount).TriangleCount, Is.EqualTo(full.TriangleCount));
				var problem = Assert.Throws<SolidBuildException>(() => SolidBuilders.Tessellate(box, 0.02, 5 * Math.PI / 180, 100));
				Assert.That(problem.Problem, Is.EqualTo(SolidProblem.TooDetailed));
			}
		}

		[Test]
		public void BuildReportsCancellation()
		{
			using (var source = new CancellationTokenSource())
			{
				source.Cancel();
				var result = SolidBuilders.Build(() => SolidBuilders.Box(Box(10, 10, 10)), source.Token);
				Assert.That(result.Problem, Is.EqualTo(SolidProblem.Cancelled));
			}
		}

		/// <summary>Timings and triangle counts for the tolerances the app uses (informational; also asserts the budget).</summary>
		[Test]
		public void RebuildTimeForAFilletedHundredMillimetreBox()
		{
			var spec = Box(100, 100, 100, SolidEdgeTreatment.Fillet, 5, SolidBoxEdges.All);
			SolidBuilders.Build(() => SolidBuilders.Box(spec), CancellationToken.None); // warm up

			var times = new double[5];
			SolidBuildResult result = null;
			for (int i = 0; i < times.Length; i++)
			{
				var timer = Stopwatch.StartNew();
				result = SolidBuilders.Build(() => SolidBuilders.Box(spec), CancellationToken.None);
				times[i] = timer.Elapsed.TotalMilliseconds;
				Assert.That(result.Succeeded, Is.True, result.Detail);
			}

			Array.Sort(times);
			TestContext.Progress.WriteLine(
				$"Filleted 100 mm box (r 5, all edges) at {SolidTolerance.For(173.2, 0).linear} mm / {SolidTolerance.AngularDegrees}°: "
				+ $"{result.Tessellation.TriangleCount} triangles, median {times[2]:F1} ms (shape {result.ShapeMilliseconds:F1} ms, mesh {result.TessellateMilliseconds:F1} ms)");
			Assert.That(times[2], Is.LessThan(100), "rebuild budget");

			// the alternatives that were compared when choosing the tolerances (informational). 0.02 mm / 0.5° was
			// also measured once: 1,076,132 triangles and 16.4 s, far too slow to rebuild while dragging a handle
			using (var shape = SolidBuilders.Box(spec))
			{
				foreach (var (linear, degrees) in new[] { (0.02, 2.0), (0.02, 5.0), (0.05, 5.0), (0.01, 5.0), (0.05, 10.0) })
				{
					SolidBuilders.Tessellate(shape, linear, degrees * Math.PI / 180);
					var timer = Stopwatch.StartNew();
					var mesh = SolidBuilders.Tessellate(shape, linear, degrees * Math.PI / 180);
					TestContext.Progress.WriteLine($"  tessellate {linear} mm / {degrees}°: {mesh.TriangleCount} triangles, {timer.Elapsed.TotalMilliseconds:F1} ms");
				}
			}
		}
	}
}
