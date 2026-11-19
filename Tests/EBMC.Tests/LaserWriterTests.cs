using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using MatterHackers.MatterControl.DesignTools.Laser;
using NUnit.Framework;
using static EBMC.Tests.LaserOutlineTests;

namespace EBMC.Tests
{
	/// <summary>Export for laser: the SVG and DXF files (size in mm, Y flip, number format, DXF structure).</summary>
	[TestFixture]
	public class LaserWriterTests
	{
		private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

		private static string SvgOf(LaserOutline o, string title = "Part") => LaserSvgWriter.Write(o, new LaserFileOptions { Title = title });

		private static string DxfOf(LaserOutline o, DxfFlavor flavor = DxfFlavor.R12Polyline) => LaserDxfWriter.Write(o, new LaserFileOptions { Dxf = flavor });

		/// <summary>The DXF as (code, value) pairs; fails if the lines do not pair up.</summary>
		private static List<(int code, string value)> Pairs(string dxf)
		{
			Assert.That(dxf.Replace("\r\n", ""), Does.Not.Contain("\n").And.Not.Contain("\r"), "CRLF line endings only");
			var lines = dxf.Split(new[] { "\r\n" }, StringSplitOptions.None).ToList();
			if (lines.Last() == "")
			{
				lines.RemoveAt(lines.Count - 1);
			}

			Assert.That(lines.Count % 2, Is.EqualTo(0), "group codes and values pair up");
			var pairs = new List<(int, string)>();
			for (int i = 0; i < lines.Count; i += 2)
			{
				Assert.That(int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code), Is.True, $"line {i + 1} is a group code: '{lines[i]}'");
				pairs.Add((code, lines[i + 1]));
			}

			return pairs;
		}

		private static double Number(string text)
		{
			Assert.That(text, Does.Match(@"^-?\d+(\.\d+)?$"), "invariant number");
			return double.Parse(text, CultureInfo.InvariantCulture);
		}

		/// <summary>Every point of every path in the SVG (subpaths per path).</summary>
		private static List<List<List<(double x, double y)>>> SvgPaths(string svg)
		{
			var doc = XDocument.Parse(svg);
			var result = new List<List<List<(double, double)>>>();
			foreach (var path in doc.Descendants(Svg + "path"))
			{
				Assert.That((string)path.Attribute("fill-rule"), Is.EqualTo("evenodd"));
				var subpaths = new List<List<(double, double)>>();
				foreach (var sub in ((string)path.Attribute("d")).Split('Z').Select(s => s.Trim()).Where(s => s.Length > 0))
				{
					var points = new List<(double, double)>();
					foreach (var token in sub.Split(' '))
					{
						var xy = token.TrimStart('M', 'L').Split(',');
						Assert.That(xy.Length, Is.EqualTo(2), token);
						points.Add((Number(xy[0]), Number(xy[1])));
					}

					subpaths.Add(points);
				}

				result.Add(subpaths);
			}

			return result;
		}

		// 1
		[Test]
		public void CubeFilesAreTwentyMillimetres()
		{
			var o = Footprint(Input(Cube20()));
			var svg = SvgOf(o, "Cube");
			Assert.That(svg, Does.StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"));
			Assert.That(svg, Does.Contain("width=\"20mm\" height=\"20mm\" viewBox=\"0 0 20 20\""));
			Assert.That(svg, Does.Contain("<title>Cube</title>"));
			Assert.That(svg, Does.Contain("<g id=\"Cut\" fill=\"none\" stroke=\"#FF0000\" stroke-width=\"0.1\">"));
			Assert.That(svg, Does.Not.Contain("\r"));
			var points = SvgPaths(svg).Single().Single();
			Assert.That(points.All(p => p.x >= 0 && p.x <= 20 && p.y >= 0 && p.y <= 20), Is.True);

			var pairs = Pairs(DxfOf(o));
			int extMax = pairs.FindIndex(p => p.code == 9 && p.value == "$EXTMAX");
			Assert.That(pairs[extMax + 1], Is.EqualTo((10, "20")));
			Assert.That(pairs[extMax + 2], Is.EqualTo((20, "20")));
			var xs = pairs.Where(p => p.code == 10).Select(p => Number(p.value)).ToList();
			var ys = pairs.Where(p => p.code == 20).Select(p => Number(p.value)).ToList();
			Assert.That(xs.Concat(ys).All(v => v >= 0 && v <= 20), Is.True);
		}

		// 2
		[Test]
		public void SquareWithHoleFiles()
		{
			var o = Footprint(Input(PlateWithHole()));
			var paths = SvgPaths(SvgOf(o));
			Assert.That(paths.Count, Is.EqualTo(1), "one path per island");
			Assert.That(paths[0].Count, Is.EqualTo(2), "outer and hole subpaths");

			var pairs = Pairs(DxfOf(o));
			var polylines = Enumerable.Range(0, pairs.Count).Where(i => pairs[i] == (0, "POLYLINE")).ToList();
			Assert.That(polylines.Count, Is.EqualTo(2));
			foreach (var start in polylines)
			{
				var entity = pairs.Skip(start + 1).TakeWhile(p => p.code != 0).ToList();
				Assert.That(entity, Does.Contain((70, "1")), "closed");
				Assert.That(entity, Does.Contain((8, "CUT")));
				Assert.That(entity, Does.Contain((66, "1")));
			}

			Assert.That(pairs.Count(p => p == (0, "VERTEX")), Is.EqualTo(8));
			Assert.That(pairs.Count(p => p == (0, "SEQEND")), Is.EqualTo(2));
		}

		// 14
		[Test]
		public void SvgIsFlippedSoTheBackOfTheBedIsAtTheTop()
		{
			// a triangle pointing to the back of the bed (+Y)
			var mesh = BodyBuilder.Extrude(new List<Pt> { new Pt(0, 0), new Pt(10, 0), new Pt(5, 10) }, 0, 2);
			var points = SvgPaths(SvgOf(Footprint(Input(mesh)))).Single().Single();
			Assert.That(points, Does.Contain((5.0, 0.0)), "the tip is at the top of the SVG");
			Assert.That(points, Does.Contain((0.0, 10.0)));
			Assert.That(points, Does.Contain((10.0, 10.0)));

			// the DXF is not flipped
			var pairs = Pairs(DxfOf(Footprint(Input(mesh))));
			var vertices = Enumerable.Range(0, pairs.Count).Where(i => pairs[i] == (0, "VERTEX"))
				.Select(i => (x: Number(pairs[i + 2].value), y: Number(pairs[i + 3].value))).ToList();
			Assert.That(vertices, Does.Contain((5.0, 10.0)));
		}

		// 15
		[Test]
		public void NumbersIgnoreTheComputersLanguage()
		{
			var saved = CultureInfo.CurrentCulture;
			try
			{
				CultureInfo.CurrentCulture = new CultureInfo("de-DE");
				var o = Footprint(Input(BodyBuilder.Box(0, 0, 0, 30, 10, 3), RotateZ(30, 1.5)));
				var svg = SvgOf(o);
				var all = SvgPaths(svg).SelectMany(p => p).SelectMany(p => p).ToList();
				Assert.That(all.Any(p => p.x != Math.Round(p.x)), Is.True, "the test has fractions in it");
				Assert.That(Regex.IsMatch(svg, "width=\"\\d+\\.\\d+mm\""), Is.True);

				foreach (var flavor in new[] { DxfFlavor.R12Polyline, DxfFlavor.R2000Lwpolyline })
				{
					foreach (var pair in Pairs(DxfOf(o, flavor)).Where(p => p.code == 10 || p.code == 20 || p.code == 30 || p.code == 40))
					{
						Number(pair.value);
					}
				}

				Assert.That(LaserNumberText(-0.00001), Is.EqualTo("0"), "never -0");
				Assert.That(LaserNumberText(1.23456), Is.EqualTo("1.2346"));
				Assert.That(LaserNumberText(2.5), Is.EqualTo("2.5"));
			}
			finally
			{
				CultureInfo.CurrentCulture = saved;
			}
		}

		private static string LaserNumberText(double value)
		{
			// the number format as the writers use it, read back from a one-point outline
			var o = new LaserOutline
			{
				Layers = new List<LaserLayer>
				{
					new LaserLayer
					{
						Name = "Cut",
						Shapes = new List<LaserShape>
						{
							new LaserShape { Outer = new LaserLoop { Points = new List<Pt> { new Pt(value, 0), new Pt(1, 0), new Pt(1, 1) } }, Holes = new List<LaserLoop>() }
						}
					}
				},
				MinX = 0,
				MinY = 0,
				MaxX = 1,
				MaxY = 1,
				Report = new LaserOutlineReport(),
			};
			var pairs = Pairs(DxfOf(o));
			int first = pairs.IndexOf((0, "VERTEX"));
			return pairs[first + 2].value;
		}

		// 16
		[Test]
		public void DxfStructure()
		{
			var dxf = DxfOf(Footprint(Input(PlateWithHole())));
			Assert.That(dxf.TrimEnd(), Does.EndWith("0\r\nEOF"));
			var pairs = Pairs(dxf);

			var sections = Enumerable.Range(0, pairs.Count - 1).Where(i => pairs[i] == (0, "SECTION")).Select(i => pairs[i + 1].value).ToList();
			Assert.That(sections, Is.EqualTo(new[] { "HEADER", "TABLES", "BLOCKS", "ENTITIES" }));
			Assert.That(pairs.Count(p => p == (0, "ENDSEC")), Is.EqualTo(4));

			int Header(string name) => pairs.FindIndex(p => p.code == 9 && p.value == name);
			Assert.That(pairs[Header("$ACADVER") + 1], Is.EqualTo((1, "AC1009")));
			Assert.That(pairs[Header("$INSUNITS") + 1], Is.EqualTo((70, "4")), "millimetres");
			Assert.That(pairs[Header("$MEASUREMENT") + 1], Is.EqualTo((70, "1")), "metric");

			int layer = pairs.IndexOf((0, "LAYER"));
			Assert.That(pairs[layer + 1], Is.EqualTo((2, "CUT")));
			Assert.That(pairs, Does.Contain((2, "CONTINUOUS")));
			Assert.That(pairs.Last(), Is.EqualTo((0, "EOF")));

			// the R2000 flavour (code only, no option in the app): one LWPOLYLINE per loop, closed
			var r2000 = Pairs(DxfOf(Footprint(Input(PlateWithHole())), DxfFlavor.R2000Lwpolyline));
			Assert.That(r2000[r2000.FindIndex(p => p.code == 9 && p.value == "$ACADVER") + 1], Is.EqualTo((1, "AC1015")));
			Assert.That(r2000.Count(p => p == (0, "LWPOLYLINE")), Is.EqualTo(2));
			Assert.That(r2000.Count(p => p == (90, "4")), Is.EqualTo(2));
		}

		[Test]
		public void EmptyOutlineStillMakesAValidSvg()
		{
			// Generate never writes an empty outline (EmptyId, no file); the writer still copes
			var empty = Footprint();
			var svg = SvgOf(empty);
			Assert.That(() => XDocument.Parse(svg), Throws.Nothing);
			Assert.That(svg, Does.Contain("width=\"0mm\" height=\"0mm\""));
			Assert.That(Pairs(DxfOf(empty)).Last(), Is.EqualTo((0, "EOF")));
		}

		[Test]
		public void TitleIsEscaped()
		{
			var svg = SvgOf(Footprint(Input(Cube20())), "Tom & Jerry <tag>");
			Assert.That(svg, Does.Contain("<title>Tom &amp; Jerry &lt;tag&gt;</title>"));
			Assert.That(() => XDocument.Parse(svg), Throws.Nothing);
		}

		[Test]
		public void ControlCharactersInTheTitleStillMakeValidXml()
		{
			var svg = SvgOf(Footprint(Input(Cube20())), "Bad\u0001name\u0008 \uFFFE\uD800 ok \uD83D\uDE00\ttab");
			Assert.That(() => XDocument.Parse(svg), Throws.Nothing);
			var title = XDocument.Parse(svg).Descendants(Svg + "title").Single().Value;
			Assert.That(title, Is.EqualTo("Badname  ok \uD83D\uDE00\ttab"), "illegal characters dropped, a real emoji pair and a tab kept");

			// the DXF writes no free text; layer names are plain ASCII
			var o = Footprint(Input(Cube20()));
			var odd = new LaserOutline { Layers = new List<LaserLayer> { new LaserLayer { Name = "Cut\u0001é", Shapes = o.Layers[0].Shapes } }, MinX = o.MinX, MinY = o.MinY, MaxX = o.MaxX, MaxY = o.MaxY, Report = o.Report };
			var dxf = DxfOf(odd);
			Assert.That(dxf.All(c => c == '\r' || c == '\n' || (c >= 0x20 && c < 0x7F)), Is.True);
			Assert.That(Pairs(dxf), Does.Contain((8, "CUT")));
		}

		[Test]
		public void ResultWordingAndOutcome()
		{
			Assert.That(LaserExportRules.Classify(null), Is.EqualTo(LaserExportOutcome.Saved));
			Assert.That(LaserExportRules.Classify(new[] { LaserExportRules.CancelledId }), Is.EqualTo(LaserExportOutcome.Cancelled));
			Assert.That(LaserExportRules.Classify(new[] { LaserExportRules.NoteId, LaserExportRules.NoteId }), Is.EqualTo(LaserExportOutcome.SavedWithNotes));
			Assert.That(LaserExportRules.Classify(new[] { LaserExportRules.EmptyId }), Is.EqualTo(LaserExportOutcome.Failed));
			Assert.That(LaserExportRules.Classify(new[] { LaserExportRules.NoteId, LaserExportRules.FailedId }), Is.EqualTo(LaserExportOutcome.Failed));
			Assert.That(LaserExportRules.ResultTitle(LaserExportOutcome.Cancelled), Is.EqualTo("Export Cancelled"));
			Assert.That(LaserExportRules.ResultTitle(LaserExportOutcome.Failed), Is.EqualTo("Export Error"));

			var o = Footprint(Input(PlateWithHole()));
			Assert.That(LaserExportRules.SavedMessage("plate.svg", o), Is.EqualTo("Saved plate.svg: 30 × 30 mm, 1 shape, 1 hole"));
			Assert.That(LaserExportRules.Summary(Footprint(Input(Cube20()))), Is.EqualTo("20 × 20 mm, 1 shape"));
			Assert.That(LaserExportRules.EmptyMessage(LaserOutlineMethod.Footprint), Does.StartWith("Nothing to cut"));
			Assert.That(LaserExportRules.EmptyMessage(LaserOutlineMethod.Section), Does.Contain("at this height"));
			Assert.That(LaserExportRules.CleanSliceHeight(-1), Is.EqualTo(0));
			Assert.That(LaserExportRules.CleanSliceHeight(double.NaN), Is.EqualTo(0));
			Assert.That(LaserExportRules.CleanSliceHeight(1.5), Is.EqualTo(1.5));
		}

		/// <summary>
		/// Writes sample files to open by hand (20 mm cube and square with hole, SVG and DXF). Explicit: runs only when asked,
		/// into the folder named by LASER_SAMPLES_DIR.
		/// </summary>
		[Test]
		[Explicit("writes sample files; set LASER_SAMPLES_DIR")]
		public void WriteSamples()
		{
			var folder = Environment.GetEnvironmentVariable("LASER_SAMPLES_DIR");
			if (string.IsNullOrEmpty(folder))
			{
				Assert.Ignore("LASER_SAMPLES_DIR is not set");
			}

			Directory.CreateDirectory(folder);
			var utf8 = new UTF8Encoding(false);
			foreach (var (name, outline) in new[] { ("cube-20mm", Footprint(Input(Cube20()))), ("square-30mm-with-10mm-hole", Footprint(Input(PlateWithHole()))) })
			{
				File.WriteAllText(Path.Combine(folder, name + ".svg"), LaserSvgWriter.Write(outline, new LaserFileOptions { Title = name }), utf8);
				File.WriteAllText(Path.Combine(folder, name + ".dxf"), LaserDxfWriter.Write(outline, new LaserFileOptions { Title = name }), utf8);
			}

			// laser G-code with the shipped generic profile. The power and speed are placeholders to show the file's shape,
			// not a setting for any material
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "EBMC.md")))
			{
				directory = directory.Parent;
			}

			var machine = LaserData.LoadMachines(File.ReadAllText(Path.Combine(directory.FullName, "StaticData", "Laser", "machines.json"))).Single(m => m.Id == "generic-grbl");
			foreach (var (name, outline, passes) in new[] { ("square-20mm", Footprint(Input(Cube20())), 1), ("square-30mm-with-10mm-hole-2-passes", Footprint(Input(PlateWithHole())), 2) })
			{
				var job = new LaserJobSettings { Operation = "Cut", PowerPercent = 10, SpeedMmS = 10, Passes = passes, MaterialNote = "SAMPLE ONLY - placeholder power and speed, not a material setting" };
				var result = LaserGcodeWriter.Write(outline, job, machine);
				Assert.That(result.Ok, Is.True);
				File.WriteAllText(Path.Combine(folder, name + ".nc"), result.Text, utf8);
			}
		}
	}
}
