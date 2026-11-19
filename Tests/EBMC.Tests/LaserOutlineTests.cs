using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using MatterHackers.MatterControl.DesignTools.Laser;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Export for laser: the 2D outline core (LaserOutlineBuilder). Fixtures are ClubMesh bodies, so every mesh is closed
	/// and wound counter-clockwise from outside, as the app's meshes are.
	/// </summary>
	[TestFixture]
	public class LaserOutlineTests
	{
		internal const double Tol = 1e-4;

		internal static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
			new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

		internal static LaserMeshInput Input(ClubMesh mesh, double[] affine = null, LaserRole role = LaserRole.Solid, string name = "Part") =>
			new LaserMeshInput
			{
				Name = name,
				Positions = mesh.Positions.ToArray(),
				Triangles = mesh.Triangles.ToArray(),
				Affine = affine,
				Role = role,
			};

		internal static LaserOutline Build(LaserOutlineOptions options, params LaserMeshInput[] meshes) =>
			LaserOutlineBuilder.Build(meshes, null, options ?? new LaserOutlineOptions(), CancellationToken.None);

		internal static LaserOutline Footprint(params LaserMeshInput[] meshes) => Build(null, meshes);

		internal static LaserOutline Section(double height, params LaserMeshInput[] meshes) =>
			Build(new LaserOutlineOptions { Method = LaserOutlineMethod.Section, SliceHeightAboveBottom = height }, meshes);

		internal static IEnumerable<LaserShape> Shapes(LaserOutline o) => o.Layers.SelectMany(l => l.Shapes);

		/// <summary>Area of the outline (outers counter-clockwise, holes clockwise, so holes subtract).</summary>
		internal static double Area(LaserOutline o) =>
			Shapes(o).Sum(s => Profiles.SignedArea(s.Outer.Points) + s.Holes.Sum(h => Profiles.SignedArea(h.Points)));

		/// <summary>Row-major 3x4 affine (as SolidMesher.ToKernelMatrix): rotate about Z, uniform scale, then move.</summary>
		internal static double[] RotateZ(double degrees, double scale = 1, double tx = 0, double ty = 0)
		{
			var r = degrees * Math.PI / 180;
			double c = Math.Cos(r) * scale, s = Math.Sin(r) * scale;
			return new[] { c, -s, 0, tx, s, c, 0, ty, 0, 0, scale, 0 };
		}

		internal static ClubMesh Cube20() => BodyBuilder.Box(0, 0, 0, 20, 20, 20);

		/// <summary>30 mm square plate, 5 mm thick, with a 10 mm square through-hole in the middle.</summary>
		internal static ClubMesh PlateWithHole() => BodyBuilder.Extrude(Rect(0, 0, 30, 30), 0, 5, Rect(10, 10, 20, 20));

		// 1
		[Test]
		public void CubeFootprintIsExactlyTwentyMillimetres()
		{
			var o = Footprint(Input(Cube20()));
			Assert.That(o.IsEmpty, Is.False);
			Assert.That(o.MinX, Is.EqualTo(0).Within(Tol));
			Assert.That(o.MinY, Is.EqualTo(0).Within(Tol));
			Assert.That(o.Width, Is.EqualTo(20).Within(Tol));
			Assert.That(o.Height, Is.EqualTo(20).Within(Tol));
			Assert.That(o.Report.Shapes, Is.EqualTo(1));
			Assert.That(o.Report.Holes, Is.EqualTo(0));
			Assert.That(Area(o), Is.EqualTo(400).Within(1e-3));
			Assert.That(o.Layers.Single().Name, Is.EqualTo("Cut"));
		}

		// 2
		[Test]
		public void SquareWithThroughHole()
		{
			foreach (var o in new[] { Footprint(Input(PlateWithHole())), Section(0.2, Input(PlateWithHole())) })
			{
				Assert.That(o.Report.Shapes, Is.EqualTo(1));
				Assert.That(o.Report.Holes, Is.EqualTo(1));
				Assert.That(Area(o), Is.EqualTo(800).Within(1e-3));

				var shape = Shapes(o).Single();
				Assert.That(Profiles.IsCounterClockwise(shape.Outer.Points), Is.True, "outer counter-clockwise");
				Assert.That(Profiles.IsCounterClockwise(shape.Holes.Single().Points), Is.False, "hole clockwise");
			}
		}

		// 3
		[Test]
		public void BlindPocketIsNotAHoleExceptWhereTheCutMeetsIt()
		{
			// a 10 mm square pocket from the top, 5 mm deep, in a 30 mm plate 10 mm thick
			var mesh = BodyBuilder.Build(
				Contour.Prism(Rect(0, 0, 30, 30), 0, 10),
				new Contour(new Ring(5, Rect(10, 10, 20, 20)), new Ring(10, Rect(10, 10, 20, 20))));
			Assert.That(mesh.IsClosed(), Is.True);

			Assert.That(Footprint(Input(mesh)).Report.Holes, Is.EqualTo(0), "seen from above the pocket floor covers it");
			Assert.That(Section(0.2, Input(mesh)).Report.Holes, Is.EqualTo(0), "below the pocket");

			var top = Section(9, Input(mesh));
			Assert.That(top.Report.Holes, Is.EqualTo(1), "through the pocket");
			Assert.That(Area(top), Is.EqualTo(800).Within(1e-3));
		}

		// 4
		[Test]
		public void RotatedAndScaledThroughTheMatrix()
		{
			var o = Footprint(Input(BodyBuilder.Box(0, 0, 0, 30, 10, 3), RotateZ(30, 2, 50, 40)));
			double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
			Assert.That(Area(o), Is.EqualTo(4 * 300).Within(1e-2));
			Assert.That(o.Width, Is.EqualTo(2 * (30 * c + 10 * s)).Within(1e-3));
			Assert.That(o.Height, Is.EqualTo(2 * (30 * s + 10 * c)).Within(1e-3));
			Assert.That(o.MinY, Is.EqualTo(40).Within(1e-3), "the corner at the origin moved to (50, 40) and is the lowest point");
		}

		// 5
		[Test]
		public void TurnedOnItsSideShowsTheSideProfile()
		{
			// 30 x 10 x 5 box turned 90° about X: y' = -z, z' = y, so from above it is 30 x 5
			var affine = new double[] { 1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0 };
			var o = Footprint(Input(BodyBuilder.Box(0, 0, 0, 30, 10, 5), affine));
			Assert.That(o.Width, Is.EqualTo(30).Within(Tol));
			Assert.That(o.Height, Is.EqualTo(5).Within(Tol));
			Assert.That(Area(o), Is.EqualTo(150).Within(1e-3));
		}

		// 6
		[Test]
		public void MirroredPartIsStillRight()
		{
			var mirror = new double[] { -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
			foreach (var o in new[] { Footprint(Input(PlateWithHole(), mirror)), Section(0.2, Input(PlateWithHole(), mirror)) })
			{
				Assert.That(Area(o), Is.EqualTo(800).Within(1e-3));
				Assert.That(o.Report.Holes, Is.EqualTo(1));
				Assert.That(o.MinX, Is.EqualTo(-30).Within(Tol));
				Assert.That(Profiles.IsCounterClockwise(Shapes(o).Single().Outer.Points), Is.True);
			}
		}

		// 7
		[Test]
		public void SeparatePartsAreSeparateShapesAndOverlapsJoin()
		{
			var apart = Footprint(Input(BodyBuilder.Box(0, 0, 0, 10, 10, 2)), Input(BodyBuilder.Box(20, 0, 0, 30, 10, 2)));
			Assert.That(apart.Report.Shapes, Is.EqualTo(2));
			Assert.That(Area(apart), Is.EqualTo(200).Within(1e-3));

			var overlapping = Footprint(Input(BodyBuilder.Box(0, 0, 0, 10, 10, 2)), Input(BodyBuilder.Box(5, 5, 0, 15, 15, 4)));
			Assert.That(overlapping.Report.Shapes, Is.EqualTo(1));
			Assert.That(Area(overlapping), Is.EqualTo(175).Within(1e-3));

			foreach (var o in new[] { Section(0.2, Input(BodyBuilder.Box(0, 0, 0, 10, 10, 2)), Input(BodyBuilder.Box(5, 5, 0, 15, 15, 4))) })
			{
				Assert.That(o.Report.Shapes, Is.EqualTo(1));
				Assert.That(Area(o), Is.EqualTo(175).Within(1e-3));
			}
		}

		// 8
		[Test]
		public void HolePartsGoingThroughAreCutAndOthersAreReported()
		{
			var solid = Input(BodyBuilder.Box(0, 0, 0, 30, 30, 10), name: "Plate");

			var through = Footprint(solid, Input(BodyBuilder.Box(10, 10, -1, 20, 20, 11), role: LaserRole.Hole, name: "Through"));
			Assert.That(through.Report.Holes, Is.EqualTo(1));
			Assert.That(Area(through), Is.EqualTo(800).Within(1e-3));
			Assert.That(through.Report.HolesNotThrough, Is.Empty);

			var partial = Footprint(solid, Input(BodyBuilder.Box(10, 10, 5, 20, 20, 11), role: LaserRole.Hole, name: "Dimple"));
			Assert.That(partial.Report.Holes, Is.EqualTo(0));
			Assert.That(Area(partial), Is.EqualTo(900).Within(1e-3));
			Assert.That(partial.Report.HolesNotThrough, Is.EqualTo(new[] { "Dimple" }));
			Assert.That(partial.Report.Warnings.Single(), Does.Contain("'Dimple' does not go all the way through"));

			// Slice at height cuts the hole wherever the cut meets it
			var sliced = Section(6, solid, Input(BodyBuilder.Box(10, 10, 5, 20, 20, 11), role: LaserRole.Hole, name: "Dimple"));
			Assert.That(sliced.Report.Holes, Is.EqualTo(1));
			Assert.That(sliced.Report.HolesNotThrough, Is.Empty);
			var below = Section(0.2, solid, Input(BodyBuilder.Box(10, 10, 5, 20, 20, 11), role: LaserRole.Hole, name: "Dimple"));
			Assert.That(below.Report.Holes, Is.EqualTo(0));

			// only holes: nothing to cut
			Assert.That(Footprint(Input(BodyBuilder.Box(10, 10, 0, 20, 20, 5), role: LaserRole.Hole)).IsEmpty, Is.True);
		}

		// 9
		[Test]
		public void NothingToCutIsEmpty()
		{
			Assert.That(LaserOutlineBuilder.Build(null, null, null, CancellationToken.None).IsEmpty, Is.True);
			Assert.That(Footprint().IsEmpty, Is.True);

			// a single vertical triangle has no area seen from above
			var wall = new ClubMesh();
			wall.AddTriangle(wall.AddVertex(0, 0, 0), wall.AddVertex(10, 0, 0), wall.AddVertex(10, 0, 10));
			var o = Footprint(Input(wall));
			Assert.That(o.IsEmpty, Is.True);
			Assert.That(o.Report.Shapes, Is.EqualTo(0));
			Assert.That(o.Width, Is.EqualTo(0));

			// a cut above everything
			Assert.That(Section(50, Input(Cube20())).IsEmpty, Is.True);
		}

		// 10
		[Test]
		public void SmallHolesStayAndSpecksAreDropped()
		{
			var plate = BodyBuilder.Extrude(Rect(0, 0, 10, 10), 0, 2, Rect(4.9, 4.9, 5.1, 5.1));
			var o = Footprint(Input(plate));
			Assert.That(o.Report.Holes, Is.EqualTo(1), "0.2 mm hole kept");
			Assert.That(Area(o), Is.EqualTo(100 - 0.04).Within(1e-3));

			var speck = BodyBuilder.Box(20, 20, 0, 20.005, 20.005, 2);
			var withSpeck = Footprint(Input(BodyBuilder.Box(0, 0, 0, 10, 10, 2)), Input(speck));
			Assert.That(withSpeck.Report.Shapes, Is.EqualTo(1));
			Assert.That(withSpeck.Report.TinyLoopsDropped, Is.EqualTo(1));
			Assert.That(withSpeck.MaxX, Is.EqualTo(10).Within(Tol), "the speck is not in the bounds");
			Assert.That(LaserExportRules.Notes(withSpeck.Report).Single(), Is.EqualTo("1 very small piece was left out."));
		}

		// 11
		[Test]
		public void HairlineCrackBetweenTrianglesIsSealed()
		{
			// a 10 mm square: one triangle below the diagonal, two above it meeting at a point 1.4 µm off the diagonal
			var mesh = new ClubMesh();
			int a = mesh.AddVertex(0, 0, 0), b = mesh.AddVertex(10, 0, 0), c = mesh.AddVertex(10, 10, 0), d = mesh.AddVertex(0, 10, 0);
			int p = mesh.AddVertex(5.001, 5.001, 0);
			mesh.AddTriangle(a, b, d);
			mesh.AddTriangle(b, c, p);
			mesh.AddTriangle(p, c, d);

			var sealedOutline = Footprint(Input(mesh));
			Assert.That(sealedOutline.Report.Shapes, Is.EqualTo(1));
			Assert.That(sealedOutline.Report.Holes, Is.EqualTo(0));
			Assert.That(Shapes(sealedOutline).Single().Outer.Points.Count, Is.EqualTo(4));
			Assert.That(Area(sealedOutline), Is.EqualTo(100).Within(1e-3));

			// the crack is really there: without the close the outline loses its sliver (about 0.01 mm²) and has extra corners
			var open = Build(new LaserOutlineOptions { CloseGapMm = 0, MinLoopAreaMm2 = 0 }, Input(mesh));
			Assert.That(Area(open), Is.LessThan(100 - 0.005));
			Assert.That(Shapes(open).Sum(s => s.Outer.Points.Count + s.Holes.Sum(h => h.Points.Count)), Is.GreaterThan(4));
		}

		// 12
		[Test]
		public void SliceOfAMeshWithAGap()
		{
			// a 20 mm square with an extra corner point 0.005 mm from (0, 20); the short wall between them is removed
			var outline = new List<Pt> { new Pt(0, 0), new Pt(20, 0), new Pt(20, 20), new Pt(0.005, 20), new Pt(0, 20) };
			var mesh = BodyBuilder.Extrude(outline, 0, 5);
			var small = Without(mesh, (x, y, z) => Math.Abs(y - 20) < 1e-9 && x <= 0.0051);
			Assert.That(small.TriangleCount, Is.EqualTo(mesh.TriangleCount - 2));

			var closed = Section(0.2, Input(small));
			Assert.That(closed.Report.OpenLoopsClosed, Is.EqualTo(1));
			Assert.That(closed.Report.OpenLoopsDropped, Is.EqualTo(0));
			Assert.That(closed.Report.Shapes, Is.EqualTo(1));
			Assert.That(Area(closed), Is.EqualTo(400).Within(1e-3));

			// a whole wall missing: the gap is 20 mm, so the line is dropped and counted
			var cube = Cube20();
			var open = Without(cube, (x, y, z) => Math.Abs(x - 20) < 1e-9);
			var dropped = Section(0.2, Input(open));
			Assert.That(dropped.Report.OpenLoopsDropped, Is.EqualTo(1));
			Assert.That(dropped.IsEmpty, Is.True);
			Assert.That(LaserExportRules.Notes(dropped.Report).Single(), Is.EqualTo("'Part' has gaps in its surface, so its slice is missing. Try 'Outline seen from above'."));

			// the closed cube has nothing to close
			var whole = Section(0.2, Input(cube));
			Assert.That(whole.Report.OpenLoopsClosed + whole.Report.OpenLoopsDropped, Is.EqualTo(0));
			Assert.That(Area(whole), Is.EqualTo(400).Within(1e-3));
		}

		/// <summary>A copy of the mesh without the triangles whose three corners all pass the test.</summary>
		private static ClubMesh Without(ClubMesh mesh, Func<double, double, double, bool> corner)
		{
			var copy = new ClubMesh();
			copy.Positions.AddRange(mesh.Positions);
			bool Is(int v) => corner(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
			for (int i = 0; i < mesh.Triangles.Count; i += 3)
			{
				int a = mesh.Triangles[i], b = mesh.Triangles[i + 1], c = mesh.Triangles[i + 2];
				if (!(Is(a) && Is(b) && Is(c)))
				{
					copy.AddTriangle(a, b, c);
				}
			}

			return copy;
		}

		// 13
		[Test]
		public void PathWithACounterGivesAHole()
		{
			// a glyph-like outer with a counter; both wound the same way, as some fonts do: even-odd still makes a hole
			var path = new LaserPathInput
			{
				Name = "O",
				Loops = new List<List<Pt>> { Rect(0, 0, 10, 10), Rect(3, 3, 7, 7) },
				Affine = RotateZ(0, 1, 5, 5),
				MinZ = 0,
				MaxZ = 0.2,
				IsDrawing = true,
			};

			var o = LaserOutlineBuilder.Build(null, new[] { path }, new LaserOutlineOptions(), CancellationToken.None);
			Assert.That(o.Report.Shapes, Is.EqualTo(1));
			Assert.That(o.Report.Holes, Is.EqualTo(1));
			Assert.That(Area(o), Is.EqualTo(84).Within(1e-3));
			Assert.That(o.MinX, Is.EqualTo(5).Within(Tol));

			// a drawing counts at any Slice at height; the profile of a solid part only where the part is
			var sliced = LaserOutlineBuilder.Build(null, new[] { path }, new LaserOutlineOptions { Method = LaserOutlineMethod.Section, SliceHeightAboveBottom = 3 }, CancellationToken.None);
			Assert.That(sliced.Report.Shapes, Is.EqualTo(1));
			path.IsDrawing = false;
			sliced = LaserOutlineBuilder.Build(null, new[] { path }, new LaserOutlineOptions { Method = LaserOutlineMethod.Section, SliceHeightAboveBottom = 3 }, CancellationToken.None);
			Assert.That(sliced.IsEmpty, Is.True);

			Assert.That(LaserExportRules.PathIsFlat(null), Is.True);
			Assert.That(LaserExportRules.PathIsFlat(RotateZ(37, 2, 10, 10)), Is.True);
			Assert.That(LaserExportRules.PathIsFlat(new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0 }), Is.True, "upside down is still flat");
			var tilt = 30 * Math.PI / 180;
			Assert.That(LaserExportRules.PathIsFlat(new double[] { 1, 0, 0, 0, 0, Math.Cos(tilt), -Math.Sin(tilt), 0, 0, Math.Sin(tilt), Math.Cos(tilt), 0 }), Is.False, "tilted about X");
			Assert.That(LaserExportRules.PathIsFlat(new double[] { 1, 0, 0 }), Is.False);
		}

		[Test]
		public void CancelStopsTheBuild()
		{
			using (var cts = new CancellationTokenSource())
			{
				cts.Cancel();
				Assert.That(
					() => LaserOutlineBuilder.Build(new[] { Input(Cube20()) }, null, new LaserOutlineOptions(), cts.Token),
					Throws.InstanceOf<OperationCanceledException>());
			}
		}

		[Test]
		public void ChunkedJoinGivesTheSameAnswer()
		{
			var sphere = Sphere(20, 24, 48);
			var one = Build(new LaserOutlineOptions { ChunkSize = 1000000 }, Input(sphere));
			var many = Build(new LaserOutlineOptions { ChunkSize = 7 }, Input(sphere));
			Assert.That(many.Report.Shapes, Is.EqualTo(1));
			Assert.That(Area(many), Is.EqualTo(Area(one)).Within(1e-3));
			Assert.That(many.Width, Is.EqualTo(one.Width).Within(Tol));
		}

		/// <summary>A closed UV sphere: rings x segments x 2 triangles (less the poles' fans).</summary>
		internal static ClubMesh Sphere(double radius, int rings, int segments)
		{
			var mesh = new ClubMesh();
			int top = mesh.AddVertex(0, 0, radius);
			var ids = new int[rings - 1, segments];
			for (int r = 1; r < rings; r++)
			{
				var phi = Math.PI * r / rings;
				for (int s = 0; s < segments; s++)
				{
					var theta = 2 * Math.PI * s / segments;
					ids[r - 1, s] = mesh.AddVertex(radius * Math.Sin(phi) * Math.Cos(theta), radius * Math.Sin(phi) * Math.Sin(theta), radius * Math.Cos(phi));
				}
			}

			int bottom = mesh.AddVertex(0, 0, -radius);
			for (int s = 0; s < segments; s++)
			{
				int n = (s + 1) % segments;
				mesh.AddTriangle(top, ids[0, s], ids[0, n]);
				for (int r = 0; r < rings - 2; r++)
				{
					mesh.AddTriangle(ids[r, s], ids[r + 1, s], ids[r + 1, n]);
					mesh.AddTriangle(ids[r, s], ids[r + 1, n], ids[r, n]);
				}

				mesh.AddTriangle(bottom, ids[rings - 2, n], ids[rings - 2, s]);
			}

			return mesh;
		}

		private sealed class Node
		{
			public string Name;
			public bool HasMesh;
			public bool FlatDrawing;
			public LaserRole? Role = LaserRole.Solid;
			public List<Node> Children = new List<Node>();
		}

		private static List<(string, LaserSourceKind, LaserRole)> CollectNodes(Node root)
		{
			return LaserExportRules.Collect(root, n => n.HasMesh, n => n.FlatDrawing, n => n.Children, n => n.Role)
				.Select(f => (f.item.Name, f.kind, f.role)).ToList();
		}

		[Test]
		public void SolidPathPartsGoThroughTheirMeshOrChildren()
		{
			// an image base (a solid path provider with no mesh of its own) with the part standing on it: both are found
			var image = new Node { Name = "Image", HasMesh = true };
			var baseNode = new Node { Name = "Base", Children = { new Node { Name = "Base mesh", HasMesh = true }, image } };
			Assert.That(CollectNodes(new Node { Name = "Scene", Children = { baseNode } }), Is.EqualTo(new[]
			{
				("Base mesh", LaserSourceKind.Mesh, LaserRole.Solid),
				("Image", LaserSourceKind.Mesh, LaserRole.Solid),
			}));

			// a bevelled extrusion (solid path provider with its own mesh): its mesh, once, and its source path child is not visited
			var extrude = new Node { Name = "Extrude", HasMesh = true, Children = { new Node { Name = "Source path", FlatDrawing = true } } };
			Assert.That(CollectNodes(extrude), Is.EqualTo(new[] { ("Extrude", LaserSourceKind.Mesh, LaserRole.Solid) }));

			// a 2D drawing (its display mesh only shows the path): its path, once, not also its mesh
			var drawing = new Node { Name = "Text", HasMesh = true, FlatDrawing = true };
			Assert.That(CollectNodes(new Node { Name = "Scene", Children = { drawing } }), Is.EqualTo(new[] { ("Text", LaserSourceKind.Path, LaserRole.Solid) }));

			// a hole group and a support: role comes through, the support is left out with everything under it
			var hole = new Node { Name = "Hole", HasMesh = true, Role = LaserRole.Hole };
			var support = new Node { Name = "Support", Role = null, Children = { new Node { Name = "Inside support", HasMesh = true } } };
			Assert.That(CollectNodes(new Node { Name = "Scene", Children = { hole, support } }), Is.EqualTo(new[] { ("Hole", LaserSourceKind.Mesh, LaserRole.Hole) }));
		}

		// ---- review fixes ----

		[Test]
		public void SliceHeightIsMeasuredFromTheSolidsNotTheHoles()
		{
			// a 30 x 30 x 10 plate with a Hole box reaching below and above it, and a low separate block 1.5 mm tall
			var plate = Input(BodyBuilder.Box(0, 0, 0, 30, 30, 10), name: "Plate");
			var hole = Input(BodyBuilder.Box(10, 10, -1, 20, 20, 11), role: LaserRole.Hole, name: "Hole");
			var low = Input(BodyBuilder.Box(40, 0, 0, 45, 5, 1.5), name: "Low");

			var at02 = Section(0.2, plate, hole);
			Assert.That(at02.IsEmpty, Is.False, "the hole's bottom at -1 must not move the cut below the plate");
			Assert.That(at02.Report.Holes, Is.EqualTo(1));
			Assert.That(Area(at02), Is.EqualTo(800).Within(1e-3));

			// cut at 2 above the plate's bottom: the 1.5 mm block is below it (cutting at z = 1 would include it)
			var at2 = Section(2, plate, hole, low);
			Assert.That(at2.Report.Shapes, Is.EqualTo(1));
			Assert.That(at2.MaxX, Is.EqualTo(30).Within(Tol));

			// with no solid at all, the bottom of everything is used
			var drawing = new LaserPathInput { Name = "Drawing", Loops = new List<List<Pt>> { Rect(0, 0, 5, 5) }, MinZ = -3, MaxZ = -2.8, IsDrawing = true };
			var onlyDrawing = LaserOutlineBuilder.Build(null, new[] { drawing }, new LaserOutlineOptions { Method = LaserOutlineMethod.Section }, CancellationToken.None);
			Assert.That(onlyDrawing.Report.Shapes, Is.EqualTo(1));

			// a drawing lying under the plate does not move the cut either
			var withDrawing = LaserOutlineBuilder.Build(new[] { plate, low }, new[] { drawing }, new LaserOutlineOptions { Method = LaserOutlineMethod.Section, SliceHeightAboveBottom = 2 }, CancellationToken.None);
			Assert.That(withDrawing.MaxX, Is.EqualTo(30).Within(Tol), "the low block stays below a cut at z = 2");
		}

		[Test]
		public void HoleThroughThePlateButNotTheTowerIsStillCut()
		{
			var plate = Input(BodyBuilder.Box(0, 0, 0, 30, 30, 5), name: "Plate");
			var tower = Input(BodyBuilder.Box(50, 0, 0, 60, 10, 20), name: "Tower");
			var hole = Input(BodyBuilder.Box(10, 10, -1, 20, 20, 6), role: LaserRole.Hole, name: "Hole");

			var o = Footprint(plate, tower, hole);
			Assert.That(o.Report.Shapes, Is.EqualTo(2));
			Assert.That(o.Report.Holes, Is.EqualTo(1), "the hole goes through the plate it overlaps");
			Assert.That(Area(o), Is.EqualTo(900 - 100 + 100).Within(1e-3));
			Assert.That(o.Report.HolesNotThrough, Is.Empty);
			Assert.That(o.Report.Warnings, Is.Empty, "it does not overlap the tower, so nothing to say");

			// a hole that overlaps the tower from above but stops halfway is left out there, and the tower is named
			var dimple = Input(BodyBuilder.Box(52, 2, 10, 58, 8, 21), role: LaserRole.Hole, name: "Dimple");
			var withDimple = Footprint(plate, tower, hole, dimple);
			Assert.That(withDimple.Report.Holes, Is.EqualTo(1));
			Assert.That(withDimple.Report.HolesNotThrough, Is.EqualTo(new[] { "Dimple" }));
			Assert.That(withDimple.Report.Warnings.Single(), Is.EqualTo("'Dimple' does not go all the way through 'Tower', so it is not cut there. Combine them first, or use Slice at height."));

			// a hole spanning one part is cut from that part only: the tower standing over the same spot keeps its area
			var over = Input(BodyBuilder.Box(0, 0, 0, 10, 10, 20), name: "Post");
			var thin = Input(BodyBuilder.Box(0, 0, 0, 30, 30, 2), name: "Base");
			var cut = Input(BodyBuilder.Box(2, 2, -1, 8, 8, 3), role: LaserRole.Hole, name: "Cut");
			var both = Footprint(thin, over, cut);
			Assert.That(both.Report.Holes, Is.EqualTo(0), "the post still covers the spot from above");
			Assert.That(both.Report.HolesNotThrough, Is.EqualTo(new[] { "Cut" }));
		}

		[Test]
		public void MissingTriangleNamesThePartAndWhatToDo()
		{
			var cube = Cube20();
			var holed = new ClubMesh();
			holed.Positions.AddRange(cube.Positions);
			bool removed = false;
			for (int i = 0; i < cube.Triangles.Count; i += 3)
			{
				int a = cube.Triangles[i], b = cube.Triangles[i + 1], c = cube.Triangles[i + 2];
				bool wall = Math.Abs(cube.Positions[a * 3] - 20) < 1e-9 && Math.Abs(cube.Positions[b * 3] - 20) < 1e-9 && Math.Abs(cube.Positions[c * 3] - 20) < 1e-9;
				if (wall && !removed)
				{
					removed = true;
					continue;
				}

				holed.AddTriangle(a, b, c);
			}

			Assert.That(removed, Is.True);
			var o = Section(1, Input(holed, name: "Cube"));
			Assert.That(o.IsEmpty, Is.True);
			Assert.That(o.Report.PartsWithGaps, Is.EqualTo(new[] { "Cube" }));
			Assert.That(o.Report.Warnings.Single(), Is.EqualTo("'Cube' has gaps in its surface, so its slice is missing. Try 'Outline seen from above'."));
			Assert.That(
				LaserExportRules.EmptyMessage(LaserOutlineMethod.Section, o.Report),
				Is.EqualTo("Nothing to cut: 'Cube' has gaps in the surface, so the slice is missing. Try 'Outline seen from above'. No file was written."));

			// with nothing missing the empty message is the plain one
			Assert.That(LaserExportRules.EmptyMessage(LaserOutlineMethod.Section, new LaserOutlineReport()), Is.EqualTo(LaserExportRules.EmptyMessage(LaserOutlineMethod.Section)));

			// seen from above still works for that part
			Assert.That(Area(Footprint(Input(holed, name: "Cube"))), Is.EqualTo(400).Within(1e-3));
		}

		/// <summary>20,000 separate vertical triangles 1 mm apart: each gives one open slice segment that cannot be joined.</summary>
		private static ClubMesh ManyOpenSegments(int count)
		{
			var mesh = new ClubMesh();
			for (int i = 0; i < count; i++)
			{
				double x = (i % 200) * 1.0, y = (i / 200) * 1.0;
				mesh.AddTriangle(mesh.AddVertex(x, y, 0), mesh.AddVertex(x + 0.5, y, 0), mesh.AddVertex(x, y, 2));
			}

			return mesh;
		}

		[Test]
		public void ManyOpenSegmentsAreQuickAndCancellable()
		{
			var mesh = ManyOpenSegments(20000);
			var watch = Stopwatch.StartNew();
			var o = Section(1, Input(mesh, name: "Grass"));
			watch.Stop();
			TestContext.Out.WriteLine($"Slice with 20,000 open segments: {watch.ElapsedMilliseconds} ms");
			Assert.That(o.IsEmpty, Is.True);
			Assert.That(o.Report.OpenLoopsDropped, Is.EqualTo(20000));
			Assert.That(o.Report.PartsWithGaps, Is.EqualTo(new[] { "Grass" }));
			Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(2));

			// the cut itself honours the cancel (not only the checks between parts)
			using (var cts = new CancellationTokenSource())
			{
				cts.Cancel();
				Assert.That(
					() => LaserOutlineBuilder.Section(Input(mesh), 1, cts.Token, out _, out _),
					Throws.InstanceOf<OperationCanceledException>());
			}
		}

		[Test]
		public void SmallGapsAmongManyChainsStillClose()
		{
			// 500 squares 30 mm apart, each with one corner pulled 0.005 mm open: every one closes across its own gap
			var all = new ClubMesh();
			for (int i = 0; i < 500; i++)
			{
				double x0 = (i % 25) * 30, y0 = (i / 25) * 30;
				var outline = new List<Pt> { new Pt(x0, y0), new Pt(x0 + 20, y0), new Pt(x0 + 20, y0 + 20), new Pt(x0 + 0.005, y0 + 20), new Pt(x0, y0 + 20) };
				var body = BodyBuilder.Extrude(outline, 0, 5);
				var copy = new ClubMesh();
				copy.Positions.AddRange(body.Positions);
				for (int t = 0; t < body.Triangles.Count; t += 3)
				{
					int a = body.Triangles[t], b = body.Triangles[t + 1], c = body.Triangles[t + 2];
					bool gapWall = new[] { a, b, c }.All(v => Math.Abs(body.Positions[v * 3 + 1] - (y0 + 20)) < 1e-9 && body.Positions[v * 3] <= x0 + 0.0051);
					if (!gapWall)
					{
						copy.AddTriangle(a, b, c);
					}
				}

				all.Add(copy);
			}

			var o = Section(1, Input(all));
			Assert.That(o.Report.Shapes, Is.EqualTo(500));
			Assert.That(o.Report.OpenLoopsClosed, Is.EqualTo(500));
			Assert.That(o.Report.OpenLoopsDropped, Is.EqualTo(0));
		}

		// 17
		[Test]
		[Category("Slow")]
		public void HundredThousandTriangleFootprintIsQuick()
		{
			var sphere = Sphere(40, 226, 224);
			Assert.That(sphere.TriangleCount, Is.GreaterThanOrEqualTo(100000));
			Assert.That(sphere.IsClosed(), Is.True);

			// warm up the JIT on a small one first, so the time is the work
			Footprint(Input(Sphere(5, 8, 16)));

			var watch = Stopwatch.StartNew();
			var o = Footprint(Input(sphere));
			watch.Stop();
			TestContext.Out.WriteLine($"Footprint of {sphere.TriangleCount} triangles: {watch.ElapsedMilliseconds} ms");

			var sectionWatch = Stopwatch.StartNew();
			var cut = Section(40, Input(sphere));
			sectionWatch.Stop();
			TestContext.Out.WriteLine($"Slice at height of {sphere.TriangleCount} triangles: {sectionWatch.ElapsedMilliseconds} ms");

			Assert.That(o.Report.Shapes, Is.EqualTo(1));
			Assert.That(o.Width, Is.EqualTo(80).Within(0.01));
			Assert.That(cut.Report.Shapes, Is.EqualTo(1));
			Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(MaxFootprintSeconds));
			Assert.That(sectionWatch.Elapsed.TotalSeconds, Is.LessThan(MaxFootprintSeconds));
		}

		/// <summary>
		/// Generous: measured on an Apple silicon Mac in November 2026 at 100,800 triangles, footprint 195-201 ms and slice
		/// 5 ms (see the test output); the margin allows for a busy machine.
		/// </summary>
		private const double MaxFootprintSeconds = 2;
	}
}
