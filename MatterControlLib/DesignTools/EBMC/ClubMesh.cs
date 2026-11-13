using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	/// <summary>
	/// EBMC Edition: a plain triangle mesh (positions in mm, triangles counter-clockwise seen from outside). The club
	/// shapes build these directly from 2D outlines, so every body is closed by construction and needs no mesh boolean.
	/// The app turns one into an agg-sharp Mesh (ClubShapeObject3D).
	/// </summary>
	public sealed class ClubMesh
	{
		public List<double> Positions { get; } = new List<double>();

		public List<int> Triangles { get; } = new List<int>();

		/// <summary>Number of separate closed bodies added (each Add of a body counts one).</summary>
		public int Bodies { get; private set; }

		public int VertexCount => Positions.Count / 3;

		public int TriangleCount => Triangles.Count / 3;

		public int AddVertex(double x, double y, double z)
		{
			Positions.Add(x);
			Positions.Add(y);
			Positions.Add(z);
			return VertexCount - 1;
		}

		public void AddTriangle(int a, int b, int c)
		{
			Triangles.Add(a);
			Triangles.Add(b);
			Triangles.Add(c);
		}

		/// <summary>Appends another mesh as one more body.</summary>
		public ClubMesh Add(ClubMesh other)
		{
			var offset = VertexCount;
			Positions.AddRange(other.Positions);
			Triangles.AddRange(other.Triangles.Select(i => i + offset));
			Bodies += Math.Max(1, other.Bodies);
			return this;
		}

		internal void MarkBody() => Bodies++;

		public ClubMesh Translate(double dx, double dy, double dz)
		{
			for (int i = 0; i < Positions.Count; i += 3)
			{
				Positions[i] += dx;
				Positions[i + 1] += dy;
				Positions[i + 2] += dz;
			}

			return this;
		}

		/// <summary>
		/// Maps every point through a 3x3 matrix (rows give the new x, y and z). A mirroring matrix (negative determinant)
		/// also flips the triangles so they stay counter-clockwise from outside.
		/// </summary>
		public ClubMesh Transform(double[,] m)
		{
			for (int i = 0; i < Positions.Count; i += 3)
			{
				var x = Positions[i];
				var y = Positions[i + 1];
				var z = Positions[i + 2];
				Positions[i] = m[0, 0] * x + m[0, 1] * y + m[0, 2] * z;
				Positions[i + 1] = m[1, 0] * x + m[1, 1] * y + m[1, 2] * z;
				Positions[i + 2] = m[2, 0] * x + m[2, 1] * y + m[2, 2] * z;
			}

			var det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
				- m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
				+ m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
			if (det < 0)
			{
				for (int i = 0; i < Triangles.Count; i += 3)
				{
					(Triangles[i + 1], Triangles[i + 2]) = (Triangles[i + 2], Triangles[i + 1]);
				}
			}

			return this;
		}

		/// <summary>A body drawn in the XY plane and extruded along Z, stood up so the drawing is in XZ and the extrusion runs along +Y.</summary>
		public static readonly double[,] XyToXz = { { 1, 0, 0 }, { 0, 0, 1 }, { 0, 1, 0 } };

		/// <summary>A body drawn in the XY plane and extruded along Z, turned so the drawing's X runs along Z, its Y stays Y and the extrusion runs along +X.</summary>
		public static readonly double[,] XyToZy = { { 0, 0, 1 }, { 0, 1, 0 }, { 1, 0, 0 } };

		public (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) Bounds()
		{
			double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
			double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
			for (int i = 0; i < Positions.Count; i += 3)
			{
				minX = Math.Min(minX, Positions[i]);
				maxX = Math.Max(maxX, Positions[i]);
				minY = Math.Min(minY, Positions[i + 1]);
				maxY = Math.Max(maxY, Positions[i + 1]);
				minZ = Math.Min(minZ, Positions[i + 2]);
				maxZ = Math.Max(maxZ, Positions[i + 2]);
			}

			return (minX, minY, minZ, maxX, maxY, maxZ);
		}

		/// <summary>Enclosed volume (sum over bodies; positive when the triangles face outwards).</summary>
		public double Volume()
		{
			double v = 0;
			for (int i = 0; i < Triangles.Count; i += 3)
			{
				int a = Triangles[i] * 3, b = Triangles[i + 1] * 3, c = Triangles[i + 2] * 3;
				var ax = Positions[a]; var ay = Positions[a + 1]; var az = Positions[a + 2];
				var bx = Positions[b]; var by = Positions[b + 1]; var bz = Positions[b + 2];
				var cx = Positions[c]; var cy = Positions[c + 1]; var cz = Positions[c + 2];
				v += (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)) / 6;
			}

			return v;
		}

		/// <summary>
		/// True when the mesh is closed and consistently oriented: every directed edge a→b appears exactly once and its
		/// reverse b→a exactly once (so every edge is shared by exactly two triangles), and no triangle repeats a vertex.
		/// </summary>
		public bool IsClosed()
		{
			var edges = new Dictionary<(int, int), int>();
			for (int i = 0; i < Triangles.Count; i += 3)
			{
				int a = Triangles[i], b = Triangles[i + 1], c = Triangles[i + 2];
				if (a == b || b == c || c == a)
				{
					return false;
				}

				foreach (var e in new[] { (a, b), (b, c), (c, a) })
				{
					edges.TryGetValue(e, out int count);
					edges[e] = count + 1;
				}
			}

			foreach (var pair in edges)
			{
				if (pair.Value != 1 || !edges.TryGetValue((pair.Key.Item2, pair.Key.Item1), out int back) || back != 1)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Smallest triangle area (mm²); a sliver check.</summary>
		public double MinTriangleArea()
		{
			double min = double.MaxValue;
			for (int i = 0; i < Triangles.Count; i += 3)
			{
				int a = Triangles[i] * 3, b = Triangles[i + 1] * 3, c = Triangles[i + 2] * 3;
				var ux = Positions[b] - Positions[a]; var uy = Positions[b + 1] - Positions[a + 1]; var uz = Positions[b + 2] - Positions[a + 2];
				var vx = Positions[c] - Positions[a]; var vy = Positions[c + 1] - Positions[a + 1]; var vz = Positions[c + 2] - Positions[a + 2];
				var nx = uy * vz - uz * vy; var ny = uz * vx - ux * vz; var nz = ux * vy - uy * vx;
				min = Math.Min(min, Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2);
			}

			return min;
		}
	}

	/// <summary>One cross-section of a contour: the outline at a height.</summary>
	public sealed class Ring
	{
		public Ring(double z, IReadOnlyList<Pt> points)
		{
			Z = z;
			Points = points;
		}

		public double Z { get; }

		public IReadOnlyList<Pt> Points { get; }
	}

	/// <summary>
	/// A closed outline followed up through a body: rings in rising Z. Two rings at different heights must have the same
	/// number of points (the wall joins point i to point i); two rings at the same height make a flat step between them.
	/// </summary>
	public sealed class Contour
	{
		public Contour(params Ring[] rings)
		{
			Rings = rings.ToList();
		}

		public List<Ring> Rings { get; }

		public double Bottom => Rings[0].Z;

		public double Top => Rings[Rings.Count - 1].Z;

		/// <summary>A straight prism contour: the same outline at z0 and z1.</summary>
		public static Contour Prism(IReadOnlyList<Pt> outline, double z0, double z1) => new Contour(new Ring(z0, outline), new Ring(z1, outline));
	}

	/// <summary>
	/// EBMC Edition: builds closed bodies from contours: one outer contour and any number of holes. A hole that reaches
	/// the bottom or top of the body opens there; one that stops short is a pocket with a flat end. The caps are
	/// triangulated by ear clipping (holes joined by bridges), using only the contours' own points, so every edge is
	/// shared by exactly two triangles.
	/// </summary>
	public static class BodyBuilder
	{
		/// <summary>A straight prism with optional through holes.</summary>
		public static ClubMesh Extrude(IReadOnlyList<Pt> outline, double z0, double z1, params IReadOnlyList<Pt>[] holes)
		{
			return Build(Contour.Prism(outline, z0, z1), holes.Select(h => Contour.Prism(h, z0, z1)).ToArray());
		}

		public static ClubMesh Build(Contour outer, params Contour[] holes)
		{
			var mesh = new ClubMesh();
			var z0 = outer.Bottom;
			var z1 = outer.Top;

			// vertex indices per ring
			int[] AddRing(Ring ring) => ring.Points.Select(p => mesh.AddVertex(p.X, p.Y, ring.Z)).ToArray();

			var outerRings = Oriented(outer, true).Select(r => (ring: r, ids: AddRing(r))).ToList();
			var holeRings = holes.Select(h => Oriented(h, false).Select(r => (ring: r, ids: AddRing(r))).ToList()).ToList();

			void Walls(List<(Ring ring, int[] ids)> rings, bool isOuter)
			{
				for (int k = 0; k + 1 < rings.Count; k++)
				{
					var lower = rings[k];
					var upper = rings[k + 1];
					if (Math.Abs(lower.ring.Z - upper.ring.Z) < 1e-9)
					{
						Step(mesh, lower, upper, isOuter);
						continue;
					}

					if (lower.ids.Length != upper.ids.Length)
					{
						throw new ArgumentException("rings at different heights need the same number of points");
					}

					int n = lower.ids.Length;
					for (int i = 0; i < n; i++)
					{
						int a = lower.ids[i], b = lower.ids[(i + 1) % n], c = upper.ids[(i + 1) % n], d = upper.ids[i];
						mesh.AddTriangle(a, b, c);
						mesh.AddTriangle(a, c, d);
					}
				}
			}

			Walls(outerRings, true);
			foreach (var h in holeRings)
			{
				Walls(h, false);
			}

			// bottom cap: the outer's first ring with the holes that open at the bottom; facing down
			var bottomHoles = holeRings.Where(h => Math.Abs(h[0].ring.Z - z0) < 1e-9).Select(h => h[0]).ToList();
			Cap(mesh, outerRings[0], bottomHoles, false);

			var top = outerRings[outerRings.Count - 1];
			var topHoles = holeRings.Where(h => Math.Abs(h[h.Count - 1].ring.Z - z1) < 1e-9).Select(h => h[h.Count - 1]).ToList();
			Cap(mesh, top, topHoles, true);

			// pocket ends
			foreach (var h in holeRings)
			{
				if (h[0].ring.Z > z0 + 1e-9)
				{
					// a pocket from the top: its floor faces up into the pocket
					PocketEnd(mesh, h[0], true);
				}

				if (h[h.Count - 1].ring.Z < z1 - 1e-9)
				{
					// a pocket from the bottom: its ceiling faces down
					PocketEnd(mesh, h[h.Count - 1], false);
				}
			}

			mesh.MarkBody();
			return mesh;
		}

		/// <summary>The contour's rings with the outer counter-clockwise and holes clockwise (all rings flipped alike).</summary>
		private static List<Ring> Oriented(Contour contour, bool counterClockwise)
		{
			var ccw = Profiles.SignedArea(contour.Rings[0].Points) > 0;
			if (ccw == counterClockwise)
			{
				return contour.Rings;
			}

			return contour.Rings.Select(r => new Ring(r.Z, r.Points.Reverse().ToList())).ToList();
		}

		private static Pt[] Points(Ring ring) => ring.Points.ToArray();

		private static void Cap(ClubMesh mesh, (Ring ring, int[] ids) outer, List<(Ring ring, int[] ids)> holes, bool up)
		{
			var tris = Triangulator.Triangulate(Points(outer.ring), outer.ids, holes.Select(h => (Points(h.ring), h.ids)).ToList());
			Emit(mesh, tris, up);
		}

		private static void PocketEnd(ClubMesh mesh, (Ring ring, int[] ids) hole, bool up)
		{
			// the hole ring is clockwise; walked backwards it is a counter-clockwise outline facing up
			var pts = Points(hole.ring).Reverse().ToArray();
			var ids = hole.ids.Reverse().ToArray();
			Emit(mesh, Triangulator.Triangulate(pts, ids, new List<(Pt[], int[])>()), up);
		}

		/// <summary>A flat step between two rings of one contour at the same height.</summary>
		private static void Step(ClubMesh mesh, (Ring ring, int[] ids) lower, (Ring ring, int[] ids) upper, bool isOuter)
		{
			var lowerArea = Math.Abs(Profiles.SignedArea(lower.ring.Points));
			var upperArea = Math.Abs(Profiles.SignedArea(upper.ring.Points));
			var upperSmaller = upperArea < lowerArea;

			// The material between the rings faces up when an outer contour narrows or a hole widens.
			var facesUp = isOuter ? upperSmaller : !upperSmaller;
			var big = upperSmaller ? lower : upper;
			var small = upperSmaller ? upper : lower;

			// triangulate the big ring counter-clockwise with the small one as a clockwise hole
			var bigPts = Points(big.ring);
			var bigIds = big.ids;
			var smallPts = Points(small.ring);
			var smallIds = small.ids;
			if (Profiles.SignedArea(bigPts) < 0)
			{
				bigPts = bigPts.Reverse().ToArray();
				bigIds = bigIds.Reverse().ToArray();
			}

			if (Profiles.SignedArea(smallPts) > 0)
			{
				smallPts = smallPts.Reverse().ToArray();
				smallIds = smallIds.Reverse().ToArray();
			}

			Emit(mesh, Triangulator.Triangulate(bigPts, bigIds, new List<(Pt[], int[])> { (smallPts, smallIds) }), facesUp);
		}

		private static void Emit(ClubMesh mesh, List<int> tris, bool up)
		{
			for (int i = 0; i < tris.Count; i += 3)
			{
				if (up)
				{
					mesh.AddTriangle(tris[i], tris[i + 1], tris[i + 2]);
				}
				else
				{
					mesh.AddTriangle(tris[i], tris[i + 2], tris[i + 1]);
				}
			}
		}

		/// <summary>A box from (x0, y0, z0) to (x1, y1, z1).</summary>
		public static ClubMesh Box(double x0, double y0, double z0, double x1, double y1, double z1)
		{
			var outline = new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };
			return Extrude(outline, z0, z1);
		}
	}

	/// <summary>
	/// EBMC Edition: ear-clipping triangulation of a counter-clockwise outline with clockwise holes. Holes are joined to
	/// the outline by bridges (David Eberly, "Triangulation by Ear Clipping"); no points are added.
	/// </summary>
	public static class Triangulator
	{
		/// <returns>Triangles as vertex ids, counter-clockwise.</returns>
		public static List<int> Triangulate(Pt[] outer, int[] outerIds, List<(Pt[] pts, int[] ids)> holes)
		{
			var pts = outer.ToList();
			var ids = outerIds.ToList();

			// rightmost holes first
			foreach (var hole in holes.OrderByDescending(h => h.pts.Max(p => p.X)))
			{
				Bridge(pts, ids, hole.pts, hole.ids);
			}

			return Clip(pts, ids);
		}

		private static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

		private static void Bridge(List<Pt> pts, List<int> ids, Pt[] hole, int[] holeIds)
		{
			// the hole's rightmost point
			int m = 0;
			for (int i = 1; i < hole.Length; i++)
			{
				if (hole[i].X > hole[m].X || (hole[i].X == hole[m].X && hole[i].Y < hole[m].Y))
				{
					m = i;
				}
			}

			var M = hole[m];

			// nearest edge crossed by the ray from M towards +X
			int bestEdge = -1;
			double bestX = double.MaxValue;
			int n = pts.Count;
			for (int i = 0; i < n; i++)
			{
				var a = pts[i];
				var b = pts[(i + 1) % n];

				// outer edges run counter-clockwise; the ray hits edges going up (a below, b above) from inside
				if (a.Y <= M.Y && b.Y >= M.Y && a.Y != b.Y)
				{
					var x = a.X + (M.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
					if (x >= M.X - 1e-12 && x < bestX)
					{
						bestX = x;
						bestEdge = i;
					}
				}
			}

			if (bestEdge < 0)
			{
				throw new InvalidOperationException("hole is not inside the outline");
			}

			var I = new Pt(bestX, M.Y);
			var ea = bestEdge;
			var eb = (bestEdge + 1) % n;
			int p = pts[ea].X > pts[eb].X ? ea : eb;

			if (!(Math.Abs(pts[p].X - I.X) < 1e-12 && Math.Abs(pts[p].Y - I.Y) < 1e-12))
			{
				// a reflex point inside triangle (M, I, P) would block the bridge; take the one closest in angle to the ray
				var P = pts[p];
				double bestAngle = double.MaxValue;
				double bestDist = double.MaxValue;
				for (int i = 0; i < n; i++)
				{
					var q = pts[i];
					if (i == p)
					{
						continue;
					}

					var prev = pts[(i - 1 + n) % n];
					var next = pts[(i + 1) % n];
					var reflex = Cross(prev, q, next) <= 0;
					if (!reflex || !InTriangle(M, I, P, q))
					{
						continue;
					}

					var dx = q.X - M.X;
					var dy = Math.Abs(q.Y - M.Y);
					var angle = Math.Atan2(dy, dx);
					var dist = dx * dx + dy * dy;
					if (angle < bestAngle - 1e-12 || (Math.Abs(angle - bestAngle) <= 1e-12 && dist < bestDist))
					{
						bestAngle = angle;
						bestDist = dist;
						p = i;
					}
				}
			}

			// splice: ... P, M, hole..., M, P, ...
			var insertPts = new List<Pt>();
			var insertIds = new List<int>();
			for (int i = 0; i <= hole.Length; i++)
			{
				insertPts.Add(hole[(m + i) % hole.Length]);
				insertIds.Add(holeIds[(m + i) % hole.Length]);
			}

			insertPts.Add(pts[p]);
			insertIds.Add(ids[p]);
			pts.InsertRange(p + 1, insertPts);
			ids.InsertRange(p + 1, insertIds);
		}

		private static bool InTriangle(Pt a, Pt b, Pt c, Pt p)
		{
			var d1 = Cross(a, b, p);
			var d2 = Cross(b, c, p);
			var d3 = Cross(c, a, p);
			var hasNeg = d1 < -1e-12 || d2 < -1e-12 || d3 < -1e-12;
			var hasPos = d1 > 1e-12 || d2 > 1e-12 || d3 > 1e-12;
			return !(hasNeg && hasPos);
		}

		private static bool Same(Pt a, Pt b) => Math.Abs(a.X - b.X) < 1e-12 && Math.Abs(a.Y - b.Y) < 1e-12;

		private static List<int> Clip(List<Pt> pts, List<int> ids)
		{
			var result = new List<int>();
			var p = new List<Pt>(pts);
			var id = new List<int>(ids);
			int guard = 0;
			int start = 0;
			while (p.Count > 3)
			{
				int n = p.Count;
				bool clipped = false;
				for (int k = 0; k < n; k++)
				{
					int i = (start + k) % n;
					var a = p[(i - 1 + n) % n];
					var b = p[i];
					var c = p[(i + 1) % n];
					if (Cross(a, b, c) <= 1e-12)
					{
						continue;
					}

					bool blocked = false;
					for (int j = 0; j < n; j++)
					{
						var q = p[j];
						if (Same(q, a) || Same(q, b) || Same(q, c))
						{
							continue;
						}

						if (InTriangle(a, b, c, q))
						{
							blocked = true;
							break;
						}
					}

					if (blocked)
					{
						continue;
					}

					result.Add(id[(i - 1 + n) % n]);
					result.Add(id[i]);
					result.Add(id[(i + 1) % n]);
					p.RemoveAt(i);
					id.RemoveAt(i);
					start = Math.Max(0, i - 1);
					clipped = true;
					break;
				}

				if (!clipped)
				{
					// numerically stuck (nearly straight runs): clip the most convex corner so the loop always ends
					int best = 0;
					double bestCross = double.MinValue;
					for (int i = 0; i < n; i++)
					{
						var cr = Cross(p[(i - 1 + n) % n], p[i], p[(i + 1) % n]);
						if (cr > bestCross)
						{
							bestCross = cr;
							best = i;
						}
					}

					result.Add(id[(best - 1 + n) % n]);
					result.Add(id[best]);
					result.Add(id[(best + 1) % n]);
					p.RemoveAt(best);
					id.RemoveAt(best);
				}

				if (++guard > 1000000)
				{
					throw new InvalidOperationException("triangulation did not finish");
				}
			}

			if (p.Count == 3)
			{
				result.Add(id[0]);
				result.Add(id[1]);
				result.Add(id[2]);
			}

			return result;
		}
	}
}
