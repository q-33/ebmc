using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClipperLib;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using IntPath = System.Collections.Generic.List<ClipperLib.IntPoint>;
using IntPaths = System.Collections.Generic.List<System.Collections.Generic.List<ClipperLib.IntPoint>>;

namespace MatterHackers.MatterControl.DesignTools.Laser
{
	/// <summary>
	/// EBMC Edition: turns meshes and flat paths (in bed coordinates, mm) into a 2D outline for a laser cutter. Pure
	/// geometry: Clipper does the joins on a 0.1 µm integer grid. Never touches the scene, so it can run on any thread.
	/// </summary>
	public static class LaserOutlineBuilder
	{
		/// <summary>Clipper units per mm (0.1 µm per unit).</summary>
		public const double Scale = 10000;

		/// <summary>A hole counts as going all the way through when it reaches within this of the solids' bottom and top (mm).</summary>
		public const double SpanToleranceMm = 0.01;

		/// <summary>Slice at height closes a gap in a cut no wider than this (mm); wider gaps are dropped.</summary>
		public const double SectionGapMm = 0.01;

		/// <summary>The cut height is nudged up by this (mm) so no flat face lies exactly on it.</summary>
		public const double SectionNudgeMm = 1e-6;

		private const double CleanDistanceMm = 0.0005;

		private const double MiterLimit = 10;

		public static LaserOutline Build(IReadOnlyList<LaserMeshInput> meshes, IReadOnlyList<LaserPathInput> paths, LaserOutlineOptions o, CancellationToken ct)
		{
			o = o ?? new LaserOutlineOptions();
			meshes = meshes ?? Array.Empty<LaserMeshInput>();
			paths = paths ?? Array.Empty<LaserPathInput>();
			var report = new LaserOutlineReport();
			bool footprint = o.Method == LaserOutlineMethod.Footprint;

			var meshZ = meshes.Select(ZRange).ToList();
			bool HasContent(int i) => meshes[i] != null && meshZ[i].min <= meshZ[i].max;
			var usablePaths = paths.Where(p => p?.Loops != null && p.Loops.Count > 0).ToList();

			// Slice at height is measured from the bottom of the solid parts: a Hole part reaching below the plate, or a
			// drawing lying under it, must not move the cut. With no solid mesh at all, the bottom of everything is used.
			double bottom = double.MaxValue;
			for (int i = 0; i < meshes.Count; i++)
			{
				if (HasContent(i) && meshes[i].Role == LaserRole.Solid)
				{
					bottom = Math.Min(bottom, meshZ[i].min);
				}
			}

			if (bottom == double.MaxValue)
			{
				for (int i = 0; i < meshes.Count; i++)
				{
					if (HasContent(i))
					{
						bottom = Math.Min(bottom, meshZ[i].min);
					}
				}

				foreach (var p in usablePaths)
				{
					bottom = Math.Min(bottom, p.MinZ);
				}
			}

			double sectionZ = (bottom == double.MaxValue ? 0 : bottom) + o.SliceHeightAboveBottom + SectionNudgeMm;

			var solids = new List<Part>();
			var holes = new List<Part>();

			for (int i = 0; i < meshes.Count; i++)
			{
				ct.ThrowIfCancellationRequested();
				if (!HasContent(i))
				{
					continue;
				}

				var m = meshes[i];
				IntPaths loops;
				if (footprint)
				{
					loops = Footprint(m, o, ct);
				}
				else
				{
					loops = Section(m, sectionZ, ct, out int closed, out int dropped);
					report.OpenLoopsClosed += closed;
					if (dropped > 0)
					{
						// keep dropping (no made-up lines), but say which part and what to do
						report.OpenLoopsDropped += dropped;
						report.PartsWithGaps.Add(m.Name ?? "");
						report.Warnings.Add(LaserExportRules.GapsMessage(m.Name ?? "", wholeSliceMissing: loops.Count == 0));
					}
				}

				(m.Role == LaserRole.Hole ? holes : solids).Add(new Part { Name = m.Name ?? "", Paths = loops, MinZ = meshZ[i].min, MaxZ = meshZ[i].max });
			}

			foreach (var p in usablePaths)
			{
				ct.ThrowIfCancellationRequested();

				// a drawing has no depth and always counts; the path of a solid part only where the part is
				if (!p.IsDrawing
					&& !footprint
					&& (sectionZ < p.MinZ || sectionZ > p.MaxZ))
				{
					continue;
				}

				(p.Role == LaserRole.Hole ? holes : solids).Add(new Part { Name = p.Name ?? "", Paths = PathLoops(p), MinZ = p.MinZ, MaxZ = p.MaxZ, IsDrawing = p.IsDrawing });
			}

			ct.ThrowIfCancellationRequested();

			var joined = footprint
				? JoinSeenFromAbove(solids, holes, o, report, ct)
				: JoinSlice(solids, holes);

			// seal hairline cracks between projected triangles (close: grow, then shrink back)
			var closedPaths = Close(joined, o.CloseGapMm * Scale);
			closedPaths = Clipper.CleanPolygons(closedPaths, CleanDistanceMm * Scale);

			var tree = new PolyTree();
			var final = new Clipper();
			final.AddPaths(closedPaths, PolyType.ptSubject, true);
			final.Execute(ClipType.ctUnion, tree, PolyFillType.pftNonZero, PolyFillType.pftNonZero);

			var outline = FromPolyTree(tree, o);
			outline.Report.HolesNotThrough.AddRange(report.HolesNotThrough);
			outline.Report.Warnings.AddRange(report.Warnings);
			outline.Report.OpenLoopsClosed = report.OpenLoopsClosed;
			outline.Report.OpenLoopsDropped = report.OpenLoopsDropped;
			outline.Report.PartsWithGaps.AddRange(report.PartsWithGaps);
			outline.Report.HoleWarnings = report.HoleWarnings;
			return outline;
		}

		/// <summary>One part's outline with its height on the bed.</summary>
		private sealed class Part
		{
			public string Name;

			public IntPaths Paths;

			public double MinZ;

			public double MaxZ;

			public bool IsDrawing;
		}

		/// <summary>
		/// Seen from above, a Hole part is cut from each solid part whose whole height it spans (a drawn hole has no depth
		/// and always counts); then the solids are joined. A hole that overlaps a solid part from above without going all
		/// the way through it is left out there and reported.
		/// </summary>
		private static IntPaths JoinSeenFromAbove(List<Part> solids, List<Part> holes, LaserOutlineOptions o, LaserOutlineReport report, CancellationToken ct)
		{
			double minArea = o.MinLoopAreaMm2 * Scale * Scale;
			var notThrough = new List<(string hole, string solid)>();
			var all = new IntPaths();
			foreach (var solid in solids)
			{
				ct.ThrowIfCancellationRequested();
				var cutting = new IntPaths();
				foreach (var hole in holes)
				{
					bool spans = hole.IsDrawing
						|| (hole.MinZ <= solid.MinZ + SpanToleranceMm && hole.MaxZ >= solid.MaxZ - SpanToleranceMm);
					if (spans)
					{
						cutting.AddRange(hole.Paths);
					}
					else if (Overlap(hole.Paths, solid.Paths) > minArea)
					{
						notThrough.Add((hole.Name, solid.Name));
					}
				}

				all.AddRange(cutting.Count == 0 ? solid.Paths : Clip(ClipType.ctDifference, solid.Paths, cutting));
			}

			// every pair is counted in the report; at most a few are worded, then one line for the rest
			var pairs = notThrough.Distinct().ToList();
			report.HoleWarnings = pairs.Count;
			for (int i = 0; i < pairs.Count; i++)
			{
				var (hole, solid) = pairs[i];
				if (!report.HolesNotThrough.Contains(hole))
				{
					report.HolesNotThrough.Add(hole);
				}

				if (i < LaserExportRules.MaxHoleWarnings)
				{
					report.Warnings.Add(LaserExportRules.HoleNotThroughMessage(hole, solid));
				}
			}

			if (pairs.Count > LaserExportRules.MaxHoleWarnings)
			{
				report.Warnings.Add(LaserExportRules.MoreLikeThisMessage(pairs.Count - LaserExportRules.MaxHoleWarnings));
			}

			return Union(all);
		}

		/// <summary>Slice at height: the cut through the solids minus the cut through the holes.</summary>
		private static IntPaths JoinSlice(List<Part> solids, List<Part> holes)
		{
			var solidPaths = solids.SelectMany(s => s.Paths).ToList();
			var holePaths = holes.SelectMany(h => h.Paths).ToList();
			return holePaths.Count == 0 ? Union(solidPaths) : Clip(ClipType.ctDifference, solidPaths, holePaths);
		}

		private static IntPaths Clip(ClipType type, IntPaths subject, IntPaths clip)
		{
			var clipper = new Clipper();
			clipper.AddPaths(subject, PolyType.ptSubject, true);
			clipper.AddPaths(clip, PolyType.ptClip, true);
			var result = new IntPaths();
			clipper.Execute(type, result, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
			return result;
		}

		/// <summary>Area (Clipper units²) where two outlines overlap seen from above.</summary>
		private static double Overlap(IntPaths a, IntPaths b)
		{
			if (a.Count == 0 || b.Count == 0)
			{
				return 0;
			}

			return Math.Abs(Clip(ClipType.ctIntersection, a, b).Sum(p => Clipper.Area(p)));
		}

		/// <summary>The bottom and top of a mesh on the bed (min greater than max when it has no triangles).</summary>
		private static (double min, double max) ZRange(LaserMeshInput m)
		{
			double min = double.MaxValue, max = double.MinValue;
			if (m?.Positions == null || m.Triangles == null || m.Triangles.Length < 3)
			{
				return (min, max);
			}

			var a = m.Affine;
			for (int i = 0; i + 2 < m.Positions.Length; i += 3)
			{
				double x = m.Positions[i], y = m.Positions[i + 1], z = m.Positions[i + 2];
				var wz = a == null ? z : a[8] * x + a[9] * y + a[10] * z + a[11];
				min = Math.Min(min, wz);
				max = Math.Max(max, wz);
			}

			return (min, max);
		}

		/// <summary>Every vertex of the mesh on the bed (x, y, z per vertex).</summary>
		private static double[] World(LaserMeshInput m)
		{
			var p = m.Positions;
			var a = m.Affine;
			var w = new double[p.Length - p.Length % 3];
			for (int i = 0; i + 2 < p.Length; i += 3)
			{
				double x = p[i], y = p[i + 1], z = p[i + 2];
				if (a == null)
				{
					w[i] = x;
					w[i + 1] = y;
					w[i + 2] = z;
				}
				else
				{
					w[i] = a[0] * x + a[1] * y + a[2] * z + a[3];
					w[i + 1] = a[4] * x + a[5] * y + a[6] * z + a[7];
					w[i + 2] = a[8] * x + a[9] * y + a[10] * z + a[11];
				}
			}

			return w;
		}

		private static bool ValidTriangle(int[] t, int i, int vertexCount)
		{
			return t[i] >= 0 && t[i] < vertexCount
				&& t[i + 1] >= 0 && t[i + 1] < vertexCount
				&& t[i + 2] >= 0 && t[i + 2] < vertexCount;
		}

		/// <summary>
		/// The outline seen from above: every triangle projected onto the bed (vertical ones add nothing), turned
		/// counter-clockwise and joined. Joins run in chunks, in parallel, then pairwise until one is left.
		/// </summary>
		internal static IntPaths Footprint(LaserMeshInput m, LaserOutlineOptions o, CancellationToken ct)
		{
			var world = World(m);
			int vertexCount = world.Length / 3;
			var points = new IntPoint[vertexCount];
			for (int v = 0; v < vertexCount; v++)
			{
				points[v] = new IntPoint(world[v * 3] * Scale, world[v * 3 + 1] * Scale);
			}

			var triangles = new List<IntPath>(m.Triangles.Length / 3);
			var t = m.Triangles;
			for (int i = 0; i + 2 < t.Length; i += 3)
			{
				if (!ValidTriangle(t, i, vertexCount))
				{
					continue;
				}

				var a = points[t[i]];
				var b = points[t[i + 1]];
				var c = points[t[i + 2]];
				long cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
				if (cross == 0)
				{
					// vertical or degenerate: no area seen from above
					continue;
				}

				triangles.Add(cross > 0 ? new IntPath(3) { a, b, c } : new IntPath(3) { a, c, b });
			}

			if (triangles.Count == 0)
			{
				return new IntPaths();
			}

			int chunkSize = Math.Max(1, o.ChunkSize);
			int chunks = (triangles.Count + chunkSize - 1) / chunkSize;
			var parts = new IntPaths[chunks];
			var parallel = new ParallelOptions { CancellationToken = ct };
			Parallel.For(0, chunks, parallel, k =>
			{
				var chunk = triangles.GetRange(k * chunkSize, Math.Min(chunkSize, triangles.Count - k * chunkSize));
				parts[k] = Union(chunk);
			});

			var level = parts.ToList();
			while (level.Count > 1)
			{
				ct.ThrowIfCancellationRequested();
				var next = new IntPaths[(level.Count + 1) / 2];
				var current = level;
				Parallel.For(0, next.Length, parallel, k =>
				{
					if (2 * k + 1 < current.Count)
					{
						var both = new IntPaths(current[2 * k].Count + current[2 * k + 1].Count);
						both.AddRange(current[2 * k]);
						both.AddRange(current[2 * k + 1]);
						next[k] = Union(both);
					}
					else
					{
						next[k] = current[2 * k];
					}
				});

				level = next.ToList();
			}

			return level[0];
		}

		private static IntPaths Union(IntPaths paths, PolyFillType fill = PolyFillType.pftNonZero)
		{
			var clipper = new Clipper();
			clipper.AddPaths(paths, PolyType.ptSubject, true);
			var result = new IntPaths();
			clipper.Execute(ClipType.ctUnion, result, fill, fill);
			return result;
		}

		/// <summary>
		/// The cut through the mesh at height z: one segment per triangle that crosses it, oriented by the triangle's
		/// winding, chained end to start into loops. Open chains are joined across gaps up to
		/// <see cref="SectionGapMm"/> and dropped otherwise. An inside-out (mirrored) result is turned the right way.
		/// </summary>
		internal static IntPaths Section(LaserMeshInput m, double z, CancellationToken ct, out int closed, out int dropped)
		{
			closed = 0;
			dropped = 0;
			var world = World(m);
			int vertexCount = world.Length / 3;
			var t = m.Triangles;

			// the crossing on an edge, computed from its lower-numbered end so both triangles sharing it agree exactly
			IntPoint Crossing(int i, int j)
			{
				int lo = Math.Min(i, j), hi = Math.Max(i, j);
				double dl = world[lo * 3 + 2] - z, dh = world[hi * 3 + 2] - z;
				double s = dl / (dl - dh);
				double x = world[lo * 3] + (world[hi * 3] - world[lo * 3]) * s;
				double y = world[lo * 3 + 1] + (world[hi * 3 + 1] - world[lo * 3 + 1]) * s;
				return new IntPoint(x * Scale, y * Scale);
			}

			bool Above(int v) => world[v * 3 + 2] - z >= 0;

			var starts = new List<IntPoint>();
			var ends = new List<IntPoint>();
			for (int i = 0; i + 2 < t.Length; i += 3)
			{
				if ((i & 0x3FFF) == 0)
				{
					ct.ThrowIfCancellationRequested();
				}

				if (!ValidTriangle(t, i, vertexCount))
				{
					continue;
				}

				int[] v = { t[i], t[i + 1], t[i + 2] };
				IntPoint? from = null, to = null;
				for (int e = 0; e < 3; e++)
				{
					int a = v[e], b = v[(e + 1) % 3];
					bool aAbove = Above(a), bAbove = Above(b);
					if (aAbove && !bAbove)
					{
						// going down: the cut enters here (counter-clockwise seen from above for an outward-facing mesh)
						from = Crossing(a, b);
					}
					else if (!aAbove && bAbove)
					{
						to = Crossing(a, b);
					}
				}

				if (from.HasValue
					&& to.HasValue
					&& !(from.Value.X == to.Value.X && from.Value.Y == to.Value.Y))
				{
					starts.Add(from.Value);
					ends.Add(to.Value);
				}
			}

			var loops = ChainSegments(starts, ends, ct, out closed, out dropped);

			// an inside-out mesh (mirrored, or wound the other way) gives loops that run backwards
			double net = loops.Sum(l => Clipper.Area(l));
			if (net < 0)
			{
				Clipper.ReversePaths(loops);
			}

			return Union(loops);
		}

		private static (long, long) Key(IntPoint p) => (p.X, p.Y);

		private static IntPaths ChainSegments(List<IntPoint> starts, List<IntPoint> ends, CancellationToken ct, out int closed, out int dropped)
		{
			closed = 0;
			dropped = 0;
			int n = starts.Count;
			var byStart = new Dictionary<(long, long), List<int>>();
			for (int s = 0; s < n; s++)
			{
				var key = Key(starts[s]);
				if (!byStart.TryGetValue(key, out var list))
				{
					byStart[key] = list = new List<int>(1);
				}

				list.Add(s);
			}

			var used = new bool[n];
			int NextFrom(IntPoint p)
			{
				if (byStart.TryGetValue(Key(p), out var list))
				{
					foreach (var s in list)
					{
						if (!used[s])
						{
							return s;
						}
					}
				}

				return -1;
			}

			int steps = 0;
			void Tick()
			{
				if ((++steps & 0x3FFF) == 0)
				{
					ct.ThrowIfCancellationRequested();
				}
			}

			var loops = new IntPaths();
			var open = new IntPaths();
			for (int s = 0; s < n; s++)
			{
				Tick();
				if (used[s])
				{
					continue;
				}

				used[s] = true;
				var chain = new IntPath { starts[s] };
				var cur = ends[s];
				bool isClosed = false;
				while (true)
				{
					Tick();
					if (Key(cur) == Key(chain[0]))
					{
						isClosed = true;
						break;
					}

					int next = NextFrom(cur);
					if (next < 0)
					{
						break;
					}

					used[next] = true;
					chain.Add(cur);
					cur = ends[next];
				}

				if (isClosed)
				{
					if (chain.Count >= 3)
					{
						loops.Add(chain);
					}
				}
				else
				{
					chain.Add(cur);
					open.Add(chain);
				}
			}

			if (open.Count == 0)
			{
				return loops;
			}

			// start with chains that nothing ends at, so a broken loop is followed from its real beginning
			var lastPoints = new HashSet<(long, long)>(open.Select(c => Key(c[c.Count - 1])));
			var chains = open.OrderBy(c => lastPoints.Contains(Key(c[0])) ? 1 : 0).ToList();
			var alive = Enumerable.Repeat(true, chains.Count).ToArray();

			// chain starts in a grid of gap-sized cells: a start within the gap of a point is in its cell or a neighbour
			long cell = Math.Max(1, (long)Math.Ceiling(SectionGapMm * Scale));
			(long, long) Cell(IntPoint p) => (FloorDiv(p.X, cell), FloorDiv(p.Y, cell));
			var grid = new Dictionary<(long, long), List<int>>();
			for (int k = 0; k < chains.Count; k++)
			{
				var key = Cell(chains[k][0]);
				if (!grid.TryGetValue(key, out var list))
				{
					grid[key] = list = new List<int>(1);
				}

				list.Add(k);
			}

			double tolerance = SectionGapMm * Scale;
			double toleranceSquared = tolerance * tolerance;
			double DistanceSquared(IntPoint a, IntPoint b)
			{
				double dx = a.X - b.X, dy = a.Y - b.Y;
				return dx * dx + dy * dy;
			}

			// the nearest live chain starting within the gap of p, or -1
			int Nearest(IntPoint p, out double best)
			{
				best = double.MaxValue;
				int found = -1;
				var (cx, cy) = Cell(p);
				for (long gx = cx - 1; gx <= cx + 1; gx++)
				{
					for (long gy = cy - 1; gy <= cy + 1; gy++)
					{
						if (!grid.TryGetValue((gx, gy), out var list))
						{
							continue;
						}

						foreach (var k in list)
						{
							if (!alive[k])
							{
								continue;
							}

							var d = DistanceSquared(p, chains[k][0]);
							if (d <= toleranceSquared && d < best)
							{
								best = d;
								found = k;
							}
						}
					}
				}

				return found;
			}

			for (int i = 0; i < chains.Count; i++)
			{
				Tick();
				if (!alive[i])
				{
					continue;
				}

				alive[i] = false;
				var chain = chains[i];
				int joins = 0;
				while (true)
				{
					Tick();
					var last = chain[chain.Count - 1];
					int other = Nearest(last, out double otherDistance);

					var selfDistance = DistanceSquared(last, chain[0]);
					if (selfDistance <= toleranceSquared
						&& chain.Count >= 3
						&& selfDistance <= otherDistance)
					{
						if (selfDistance == 0)
						{
							chain.RemoveAt(chain.Count - 1);
						}

						if (selfDistance > 0 || joins > 0)
						{
							closed++;
						}

						loops.Add(chain);
						break;
					}

					if (other >= 0)
					{
						alive[other] = false;
						chain.AddRange(otherDistance == 0 ? chains[other].Skip(1) : chains[other]);
						if (otherDistance > 0)
						{
							joins++;
						}

						continue;
					}

					dropped++;
					break;
				}
			}

			return loops;
		}

		private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

		/// <summary>The loops of one flat path on the bed, joined even-odd (a letter's counter is a hole, as fonts expect).</summary>
		internal static IntPaths PathLoops(LaserPathInput p)
		{
			var a = p.Affine;
			var paths = new IntPaths();
			foreach (var loop in p.Loops)
			{
				if (loop == null || loop.Count < 3)
				{
					continue;
				}

				var path = new IntPath(loop.Count);
				foreach (var pt in loop)
				{
					double x = pt.X, y = pt.Y;
					double wx = a == null ? x : a[0] * x + a[1] * y + a[3];
					double wy = a == null ? y : a[4] * x + a[5] * y + a[7];
					path.Add(new IntPoint(wx * Scale, wy * Scale));
				}

				paths.Add(path);
			}

			return Union(paths, PolyFillType.pftEvenOdd);
		}

		/// <summary>Grow by delta then shrink by delta (Clipper units): seals cracks narrower than 2 × delta, keeps the size.</summary>
		private static IntPaths Close(IntPaths paths, double delta)
		{
			if (delta <= 0 || paths.Count == 0)
			{
				return paths;
			}

			var grown = new IntPaths();
			var offset = new ClipperOffset(MiterLimit);
			offset.AddPaths(paths, JoinType.jtMiter, EndType.etClosedPolygon);
			offset.Execute(ref grown, delta);

			var shrunk = new IntPaths();
			offset = new ClipperOffset(MiterLimit);
			offset.AddPaths(grown, JoinType.jtMiter, EndType.etClosedPolygon);
			offset.Execute(ref shrunk, -delta);
			return shrunk;
		}

		/// <summary>Islands (outer counter-clockwise, holes clockwise) in mm with their bounds; tiny loops dropped and counted.</summary>
		internal static LaserOutline FromPolyTree(PolyTree tree, LaserOutlineOptions o)
		{
			o = o ?? new LaserOutlineOptions();
			var report = new LaserOutlineReport();
			var shapes = new List<LaserShape>();
			double minArea = o.MinLoopAreaMm2 * Scale * Scale;
			double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

			LaserLoop ToLoop(IntPath path, bool counterClockwise)
			{
				var pts = path.Select(p => new Pt(p.X / Scale, p.Y / Scale)).ToList();
				if ((Clipper.Area(path) > 0) != counterClockwise)
				{
					pts.Reverse();
				}

				return new LaserLoop { Points = pts };
			}

			void AddOuter(PolyNode node)
			{
				bool keep = Math.Abs(Clipper.Area(node.Contour)) >= minArea;
				if (!keep)
				{
					report.TinyLoopsDropped++;
				}

				var holes = new List<LaserLoop>();
				foreach (var hole in node.Childs)
				{
					if (keep && Math.Abs(Clipper.Area(hole.Contour)) >= minArea)
					{
						holes.Add(ToLoop(hole.Contour, false));
					}
					else
					{
						report.TinyLoopsDropped++;
					}

					// islands inside a hole are shapes of their own
					foreach (var island in hole.Childs)
					{
						AddOuter(island);
					}
				}

				if (!keep)
				{
					return;
				}

				var outer = ToLoop(node.Contour, true);
				foreach (var p in outer.Points)
				{
					minX = Math.Min(minX, p.X);
					minY = Math.Min(minY, p.Y);
					maxX = Math.Max(maxX, p.X);
					maxY = Math.Max(maxY, p.Y);
				}

				shapes.Add(new LaserShape { Outer = outer, Holes = holes });
				report.Holes += holes.Count;
			}

			foreach (var node in tree.Childs)
			{
				AddOuter(node);
			}

			report.Shapes = shapes.Count;
			bool empty = shapes.Count == 0;
			return new LaserOutline
			{
				Layers = new List<LaserLayer> { new LaserLayer { Name = LaserOutline.CutLayer, Shapes = shapes } },
				MinX = empty ? 0 : minX,
				MinY = empty ? 0 : minY,
				MaxX = empty ? 0 : maxX,
				MaxY = empty ? 0 : maxY,
				Report = report,
				IsEmpty = empty,
			};
		}
	}
}
