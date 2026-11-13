// Stage 0 spike: exercises libebmc_occt from .NET and prints a PASS/FAIL table.
// Run from this folder: dotnet run -c Release

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MatterHackers.MatterControl.DesignTools.Solid; // EbmcOcct.cs is linked from the app (see the .csproj)

namespace EbmcOcctSpike
{
	internal static class Program
	{
		private sealed class Row
		{
			public string Name = "";
			public bool Pass;
			public string Detail = "";
		}

		private static readonly List<Row> Rows = new List<Row>();

		private static void Report(string name, bool pass, string detail)
		{
			Rows.Add(new Row { Name = name, Pass = pass, Detail = detail });
			Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}: {detail}");
		}

		private static void Run(string name, Func<(bool pass, string detail)> test)
		{
			try
			{
				var (pass, detail) = test();
				Report(name, pass, detail);
			}
			catch (Exception ex)
			{
				Report(name, false, "threw " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		private static double Rel(double a, double b) => Math.Abs(a - b) / Math.Abs(b);

		private const double Deg = Math.PI / 180;
		private static readonly double FilletVolume = 8000 - (1 - Math.PI / 4) * 3 * 3 * 20;
		private static readonly double CutVolume = FilletVolume - Math.PI * 2.5 * 2.5 * 20;

		/// <summary>Picks the box edge along +Z through the origin, the way a UI would pick by geometry.</summary>
		private static int FindEdge(OcctShape shape, double x, double y)
		{
			EbmcOcct.Counts(shape, out _, out var edges, out _);
			for (int i = 0; i < edges; i++)
			{
				var e = EbmcOcct.EdgeInfo(shape, i);
				if (Math.Abs(e.StartX - x) < 1e-9 && Math.Abs(e.StartY - y) < 1e-9 && Math.Abs(e.EndX - x) < 1e-9
					&& Math.Abs(e.EndY - y) < 1e-9 && Math.Abs(e.Length - 20) < 1e-9)
				{
					return i;
				}
			}
			throw new InvalidOperationException("edge not found");
		}

		private static OcctShape MakeFilletedCutBox()
		{
			using var box = EbmcOcct.MakeBox(20, 20, 20);
			using var fil = EbmcOcct.Fillet(box, new[] { FindEdge(box, 0, 0) }, 3);
			using var cyl = EbmcOcct.MakeCylinder(2.5, 40);
			using var cylMoved = EbmcOcct.Translate(cyl, 10, 10, -10);
			return EbmcOcct.Boolean(fil, cylMoved, EbmcOcct.BooleanOp.Cut);
		}

		private sealed class MeshCheck
		{
			public bool FaceIdsValid, EveryFaceMeshed, Closed, Oriented;
			public int BadEdges, NonManifoldEdges, DegenerateTriangles;
			public double Volume;
		}

		private static MeshCheck CheckMesh(OcctMesh m, int faceCount)
		{
			var r = new MeshCheck { FaceIdsValid = true };
			var faceSeen = new bool[faceCount];
			var undirected = new Dictionary<long, int>();
			var directed = new Dictionary<long, int>();
			long Key(int a, int b) => ((long)a << 32) | (uint)b;
			double vol = 0;
			for (int t = 0; t < m.TriangleCount; t++)
			{
				int f = m.TriangleFaces[t];
				if (f < 0 || f >= faceCount) r.FaceIdsValid = false; else faceSeen[f] = true;
				int a = m.Triangles[3 * t], b = m.Triangles[3 * t + 1], c = m.Triangles[3 * t + 2];
				if (a == b || b == c || a == c) r.DegenerateTriangles++;
				foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
				{
					var k = Key(Math.Min(p, q), Math.Max(p, q));
					undirected[k] = undirected.TryGetValue(k, out var n) ? n + 1 : 1;
					var d = Key(p, q);
					directed[d] = directed.TryGetValue(d, out var n2) ? n2 + 1 : 1;
				}
				double ax = m.Vertices[3 * a], ay = m.Vertices[3 * a + 1], az = m.Vertices[3 * a + 2];
				double bx = m.Vertices[3 * b], by = m.Vertices[3 * b + 1], bz = m.Vertices[3 * b + 2];
				double cx = m.Vertices[3 * c], cy = m.Vertices[3 * c + 1], cz = m.Vertices[3 * c + 2];
				vol += (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)) / 6;
			}
			foreach (var kv in undirected)
			{
				if (kv.Value < 2) r.BadEdges++;
				if (kv.Value > 2) r.NonManifoldEdges++;
			}
			r.Closed = r.BadEdges == 0 && r.NonManifoldEdges == 0;
			r.Oriented = true;
			foreach (var kv in directed)
			{
				int p = (int)(kv.Key >> 32), q = (int)(kv.Key & 0xffffffff);
				if (kv.Value != 1 || !directed.ContainsKey(Key(q, p))) { r.Oriented = false; break; }
			}
			r.EveryFaceMeshed = Array.TrueForAll(faceSeen, s => s);
			r.Volume = vol;
			return r;
		}

		private static long Rss()
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			using var p = Process.GetCurrentProcess();
			p.Refresh();
			return p.WorkingSet64;
		}

		private static int Main()
		{
			Console.WriteLine($".NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, "
				+ $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
			Console.WriteLine("Base directory: " + AppContext.BaseDirectory);

			Run("load library + version", () =>
			{
				var v = EbmcOcct.Version;
				return (v.Contains("OCCT 8.0.1"), v);
			});

			Run("box 20x20x20 volume + topology", () =>
			{
				using var box = EbmcOcct.MakeBox(20, 20, 20);
				var v = EbmcOcct.Volume(box);
				EbmcOcct.Counts(box, out var f, out var e, out var n);
				var bb = EbmcOcct.BoundingBox(box);
				bool ok = Rel(v, 8000) < 1e-6 && f == 6 && e == 12 && n == 8 && Math.Abs(bb[3] - 20) < 1e-9;
				return (ok, $"V={v:R}, faces={f} edges={e} vertices={n}, area={EbmcOcct.Area(box):R}");
			});

			Run("fillet one edge r=3", () =>
			{
				using var box = EbmcOcct.MakeBox(20, 20, 20);
				int edge = FindEdge(box, 0, 0);
				using var fil = EbmcOcct.Fillet(box, new[] { edge }, 3);
				var v = EbmcOcct.Volume(fil);
				EbmcOcct.Counts(fil, out var f, out var e, out var n);
				bool ok = Rel(v, FilletVolume) < 1e-6 && f == 7 && EbmcOcct.IsValid(fil);
				return (ok, $"edge #{edge}, V={v:R} expected {FilletVolume:R} (rel err {Rel(v, FilletVolume):E1}), faces={f} edges={e} vertices={n}");
			});

			Run("chamfer one edge d=2", () =>
			{
				using var box = EbmcOcct.MakeBox(20, 20, 20);
				using var ch = EbmcOcct.Chamfer(box, new[] { FindEdge(box, 0, 0) }, 2);
				var v = EbmcOcct.Volume(ch);
				EbmcOcct.Counts(ch, out var f, out _, out _);
				double expect = 8000 - 0.5 * 2 * 2 * 20;
				return (Rel(v, expect) < 1e-6 && f == 7, $"V={v:R} expected {expect}, faces={f}");
			});

			Run("cut through-hole d=5", () =>
			{
				using var cut = MakeFilletedCutBox();
				var v = EbmcOcct.Volume(cut);
				EbmcOcct.Counts(cut, out var f, out var e, out var n);
				return (Rel(v, CutVolume) < 1e-6 && EbmcOcct.IsValid(cut),
					$"V={v:R} expected {CutVolume:R} (rel err {Rel(v, CutVolume):E1}), faces={f} edges={e} vertices={n}");
			});

			Run("tessellate 0.05 mm / 0.5 deg", () =>
			{
				using var cut = MakeFilletedCutBox();
				EbmcOcct.Counts(cut, out var faces, out var edges, out _);
				var sw = Stopwatch.StartNew();
				var m = EbmcOcct.Tessellate(cut, 0.05, 0.5 * Deg);
				sw.Stop();
				var c = CheckMesh(m, faces);
				double err = Rel(c.Volume, CutVolume);
				bool ok = m.TriangleCount > 0 && c.FaceIdsValid && c.EveryFaceMeshed && c.Closed && c.Oriented
					&& c.DegenerateTriangles == 0 && err < 0.005;
				return (ok, $"{m.VertexCount} vertices, {m.TriangleCount} triangles, {m.EdgeIds.Length}/{edges} edge polylines, "
					+ $"{sw.Elapsed.TotalMilliseconds:F1} ms; faceIds valid={c.FaceIdsValid}, every face meshed={c.EveryFaceMeshed}, "
					+ $"closed={c.Closed} (open edges {c.BadEdges}, non-manifold {c.NonManifoldEdges}), consistently oriented={c.Oriented}, "
					+ $"degenerate tris={c.DegenerateTriangles}, mesh V={c.Volume:F4} (rel err {err:E2})");
			});

			Run("tessellate leaves input untouched", () =>
			{
				using var cut = MakeFilletedCutBox();
				var coarse = EbmcOcct.Tessellate(cut, 1.0, 20 * Deg);
				var fine = EbmcOcct.Tessellate(cut, 0.05, 0.5 * Deg);
				var coarse2 = EbmcOcct.Tessellate(cut, 1.0, 20 * Deg);
				bool ok = fine.TriangleCount > coarse.TriangleCount && coarse2.TriangleCount == coarse.TriangleCount;
				return (ok, $"coarse {coarse.TriangleCount} tris, fine {fine.TriangleCount}, coarse again {coarse2.TriangleCount}");
			});

			Run("STEP write + read round trip", () =>
			{
				var path = Path.Combine(Path.GetTempPath(), "ebmc_occt_spike_" + Guid.NewGuid().ToString("N") + ".step");
				try
				{
					using var cut = MakeFilletedCutBox();
					EbmcOcct.WriteStep(cut, path);
					using var back = EbmcOcct.ReadStep(path);
					double v0 = EbmcOcct.Volume(cut), v1 = EbmcOcct.Volume(back);
					EbmcOcct.Counts(cut, out var f0, out var e0, out var n0);
					EbmcOcct.Counts(back, out var f1, out var e1, out var n1);
					bool ok = Rel(v1, v0) < 1e-6 && f0 == f1 && e0 == e1 && n0 == n1 && EbmcOcct.IsValid(back);
					return (ok, $"{new FileInfo(path).Length} bytes, V {v0:R} -> {v1:R} (rel {Rel(v1, v0):E1}), "
						+ $"F/E/V {f0}/{e0}/{n0} -> {f1}/{e1}/{n1}");
				}
				finally
				{
					File.Delete(path);
				}
			});

			Run("error: fillet r=25 on 20 mm box", () =>
			{
				using var box = EbmcOcct.MakeBox(20, 20, 20);
				try
				{
					using var bad = EbmcOcct.Fillet(box, new[] { FindEdge(box, 0, 0) }, 25);
					return (false, "no error, V=" + EbmcOcct.Volume(bad));
				}
				catch (OcctException ex)
				{
					bool stillWorks = Rel(EbmcOcct.Volume(box), 8000) < 1e-9;
					return (ex.Code == EbmcOcct.ErrNotDone && ex.Message.Length > 0 && stillWorks, $"code {ex.Code}: \"{ex.Message}\"; library usable afterwards={stillWorks}");
				}
			});

			Run("error: bad edge index / missing file / bad arg", () =>
			{
				using var box = EbmcOcct.MakeBox(20, 20, 20);
				var codes = new List<string>();
				try { EbmcOcct.Fillet(box, new[] { 99 }, 1).Dispose(); } catch (OcctException ex) { codes.Add($"index:{ex.Code}"); }
				try { EbmcOcct.ReadStep("/nonexistent/x.step").Dispose(); } catch (OcctException ex) { codes.Add($"io:{ex.Code}"); }
				try { EbmcOcct.MakeBox(-1, 1, 1).Dispose(); } catch (OcctException ex) { codes.Add($"arg:{ex.Code}"); }
				var s = string.Join(", ", codes);
				return (s == $"index:{EbmcOcct.ErrIndex}, io:{EbmcOcct.ErrIo}, arg:{EbmcOcct.ErrInvalidArg}", s);
			});

			Run("loop 200x box+fillet+tessellate (0.05 mm/0.5 deg)", () =>
			{
				const int n = 200;
				var rss = new List<long>();
				long tris = 0;
				var sw = Stopwatch.StartNew();
				for (int i = 0; i < n; i++)
				{
					using (var box = EbmcOcct.MakeBox(20, 20, 20))
					using (var fil = EbmcOcct.Fillet(box, new[] { 0 }, 3))
					{
						tris += EbmcOcct.Tessellate(fil, 0.05, 0.5 * Deg).TriangleCount;
					}
					if ((i + 1) % 50 == 0)
					{
						sw.Stop();
						rss.Add(Rss());
						sw.Start();
					}
				}
				sw.Stop();
				double growthMb = (rss[rss.Count - 1] - rss[0]) / 1048576.0;
				string rssText = string.Join(" / ", rss.ConvertAll(r => (r / 1048576.0).ToString("F1")));
				return (growthMb < 10, $"{sw.Elapsed.TotalMilliseconds / n:F2} ms/cycle, {tris / n} tris/cycle; RSS MB after 50/100/150/200: {rssText} (growth 50->200: {growthMb:F1} MB)");
			});

			Run("two threads, independent shapes", () =>
			{
				const int perThread = 40;
				var errors = new List<string>();
				var volumes = new double[2];
				var meshTris = new int[2];
				var sw = Stopwatch.StartNew();
				var threads = new Thread[2];
				for (int t = 0; t < 2; t++)
				{
					int id = t;
					threads[t] = new Thread(() =>
					{
						try
						{
							for (int i = 0; i < perThread; i++)
							{
								using var cut = MakeFilletedCutBox();
								volumes[id] = EbmcOcct.Volume(cut);
								meshTris[id] = EbmcOcct.Tessellate(cut, 0.05, 0.5 * Deg).TriangleCount;
								if (i % 10 == 0)
								{
									var path = Path.Combine(Path.GetTempPath(), $"ebmc_occt_thread{id}_{Guid.NewGuid():N}.step");
									EbmcOcct.WriteStep(cut, path);
									using var back = EbmcOcct.ReadStep(path);
									File.Delete(path);
									if (Rel(EbmcOcct.Volume(back), volumes[id]) > 1e-6) throw new Exception("STEP volume mismatch");
								}
								try { EbmcOcct.Fillet(cut, new[] { 9999 }, 1).Dispose(); }
								catch (OcctException ex) when (ex.Code == EbmcOcct.ErrIndex && ex.Message.Contains("9999")) { }
							}
						}
						catch (Exception ex)
						{
							lock (errors) errors.Add($"thread {id}: {ex.Message}");
						}
					});
				}
				foreach (var th in threads) th.Start();
				foreach (var th in threads) th.Join();
				sw.Stop();
				bool ok = errors.Count == 0 && Rel(volumes[0], CutVolume) < 1e-6 && volumes[0] == volumes[1] && meshTris[0] == meshTris[1];
				return (ok, $"2x{perThread} cycles (cut+volume+tessellate, STEP every 10th, error path each) in {sw.Elapsed.TotalSeconds:F1} s; "
					+ $"V {volumes[0]:R}/{volumes[1]:R}, tris {meshTris[0]}/{meshTris[1]}, errors: {(errors.Count == 0 ? "none" : string.Join("; ", errors))}");
			});

			Run("two threads, shapes derived from one shared box (informational)", () =>
			{
				using var box = EbmcOcct.MakeBox(20, 20, 20);
				using var cyl = EbmcOcct.MakeCylinder(2.5, 40);
				using var cylMoved = EbmcOcct.Translate(cyl, 10, 10, -10);
				var errors = new List<string>();
				var results = new double[2];
				var threads = new Thread[2];
				for (int t = 0; t < 2; t++)
				{
					int id = t;
					threads[t] = new Thread(() =>
					{
						try
						{
							for (int i = 0; i < 40; i++)
							{
								using var derived = id == 0 ? EbmcOcct.Fillet(box, new[] { 0 }, 3) : EbmcOcct.Boolean(box, cylMoved, EbmcOcct.BooleanOp.Cut);
								EbmcOcct.Tessellate(derived, 0.05, 0.5 * Deg);
								results[id] = EbmcOcct.Volume(derived);
							}
						}
						catch (Exception ex)
						{
							lock (errors) errors.Add($"thread {id}: {ex.Message}");
						}
					});
				}
				foreach (var th in threads) th.Start();
				foreach (var th in threads) th.Join();
				double cutOnly = 8000 - Math.PI * 2.5 * 2.5 * 20;
				bool ok = errors.Count == 0 && Rel(results[0], FilletVolume) < 1e-6 && Rel(results[1], cutOnly) < 1e-6;
				return (ok, $"fillet V={results[0]:R}, cut V={results[1]:R}; errors: {(errors.Count == 0 ? "none" : string.Join("; ", errors))}");
			});

			Console.WriteLine();
			Console.WriteLine("| Test | Result | Measured |");
			Console.WriteLine("|---|---|---|");
			foreach (var r in Rows) Console.WriteLine($"| {r.Name} | {(r.Pass ? "PASS" : "FAIL")} | {r.Detail.Replace("|", "/")} |");
			int failed = Rows.FindAll(r => !r.Pass).Count;
			Console.WriteLine();
			Console.WriteLine(failed == 0 ? $"ALL {Rows.Count} PASSED" : $"{failed} of {Rows.Count} FAILED");
			return failed == 0 ? 0 : 1;
		}
	}
}
