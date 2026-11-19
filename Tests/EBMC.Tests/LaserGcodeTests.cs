using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using MatterHackers.MatterControl.DesignTools.Laser;
using NUnit.Framework;
using static EBMC.Tests.LaserOutlineTests;

namespace EBMC.Tests
{
	/// <summary>
	/// "G-code for laser (.nc)": the rules, the writer (laser never on during travel, loops closed, order, passes, F and S)
	/// and the data files. A file for a laser cutter only; nothing here is 3D-printer G-code.
	/// </summary>
	[TestFixture]
	public class LaserGcodeTests
	{
		private static string root;

		[OneTimeSetUp]
		public void FindRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "EBMC.md")))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null, "repository root");
			root = directory.FullName;
		}

		private static string Source(string relative) => File.ReadAllText(Path.Combine(root, relative));

		internal static LaserMachineProfile Generic() => new LaserMachineProfile
		{
			Id = "test",
			Name = "Test GRBL",
			PowerSMin = 0,
			PowerSMax = 1000,
			MaxPowerPercent = 100,
			MaxCutSpeedMmS = 100,
			TravelSpeedMmS = 100,
			LaserOnCommand = "M4",
			LaserOffCommand = "M5",
			Footer = new List<string> { "M2" },
		};

		internal static LaserJobSettings Job(double power = 50, double speed = 10, int passes = 1, string note = "") =>
			new LaserJobSettings { Operation = "Cut", PowerPercent = power, SpeedMmS = speed, Passes = passes, MaterialNote = note };

		private static string[] Lines(string text) => text.Split('\n').Where(l => l.Length > 0).ToArray();

		private static string Code(string line)
		{
			int semicolon = line.IndexOf(';');
			return (semicolon >= 0 ? line.Substring(0, semicolon) : line).Trim();
		}

		private static double Word(string code, char letter)
		{
			var m = Regex.Match(code, @"(?:^|\s)" + letter + @"(-?\d+(?:\.\d+)?)(?:\s|$)");
			return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
		}

		/// <summary>The cut loops as point lists (G0 start, then every G1 while the laser is on).</summary>
		private static List<List<Pt>> CutLoops(string text, LaserMachineProfile machine)
		{
			var loops = new List<List<Pt>>();
			List<Pt> current = null;
			Pt last = new Pt(0, 0);
			foreach (var code in Lines(text).Select(Code).Where(c => c.Length > 0))
			{
				if (code.StartsWith("G0"))
				{
					last = new Pt(Word(code, 'X'), Word(code, 'Y'));
				}
				else if (code.StartsWith(machine.LaserOnCommand))
				{
					current = new List<Pt> { last };
				}
				else if (code.StartsWith("G1"))
				{
					current.Add(new Pt(Word(code, 'X'), Word(code, 'Y')));
				}
				else if (code.StartsWith(machine.LaserOffCommand) && current != null)
				{
					loops.Add(current);
					current = null;
				}
			}

			return loops;
		}

		/// <summary>Scans every line: the laser is never on during a G0, a G0 never carries power, G1 only while on.</summary>
		private static void AssertLaserSafe(string text, LaserMachineProfile machine)
		{
			bool on = false;
			int n = 0;
			foreach (var line in Lines(text))
			{
				n++;
				var code = Code(line);
				if (code.Length == 0)
				{
					continue;
				}

				if (code.StartsWith(machine.LaserOnCommand))
				{
					Assert.That(Word(code, 'S'), Is.GreaterThan(0), $"line {n}");
					on = true;
				}
				else if (code.StartsWith(machine.LaserOffCommand))
				{
					on = false;
				}
				else if (code.StartsWith("G0"))
				{
					Assert.That(on, Is.False, $"line {n}: G0 with the laser on: {line}");
					var s = Word(code, 'S');
					Assert.That(double.IsNaN(s) || s == 0, Is.True, $"line {n}: G0 with power: {line}");
				}
				else if (code.StartsWith("G1"))
				{
					Assert.That(on, Is.True, $"line {n}: G1 with the laser off: {line}");
				}
			}

			Assert.That(on, Is.False, "the file ends with the laser off");
		}

		private static LaserGcodeResult Write(LaserOutline o, LaserJobSettings job = null, LaserMachineProfile machine = null)
		{
			var result = LaserGcodeWriter.Write(o, job ?? Job(), machine ?? Generic());
			Assert.That(result.Ok, Is.True, string.Join("; ", result.Problems.Select(p => p.Message)));
			return result;
		}

		private static LaserOutline Square20() => Footprint(Input(Cube20()));

		private static LaserOutline SquareWithHole() => Footprint(Input(PlateWithHole()));

		[Test]
		public void HeaderAndFooter()
		{
			var text = Write(Square20(), Job(note: "Plywood 3 mm")).Text;
			Assert.That(text, Does.Not.Contain("\r"));
			var lines = Lines(text);
			Assert.That(lines[0], Does.StartWith("; G-code for a laser cutter"));
			Assert.That(text, Does.Contain("; This is NOT 3D-printer G-code. Do not send it to a 3D printer."));
			Assert.That(text, Does.Contain("; Units: mm. Size: 20 x 20 mm"));
			Assert.That(text, Does.Contain("; Operation: Cut"));
			Assert.That(text, Does.Contain("; Power: 50 % (S500 in S0-1000), speed: 10 mm/s (F600), passes: 1"));
			Assert.That(text, Does.Contain("; Material: Plywood 3 mm"));
			Assert.That(text, Does.Contain("; Machine profile: Test GRBL (NOT verified on the machine)"));
			Assert.That(text, Does.Contain("; Work area: not known; the size was not checked."));
			Assert.That(text, Does.Contain("; Estimated cutting length: 80 mm; estimated time: about"));
			Assert.That(text, Does.Contain("lid closed, air assist on, focus set, stay with the machine, extinguisher to hand"));

			var codes = lines.Select(Code).Where(c => c.Length > 0).ToList();
			Assert.That(codes.Take(3), Is.EqualTo(new[] { "G21", "G90", "M5" }), "units, absolute, laser off before anything moves");
			Assert.That(codes.Skip(codes.Count - 3), Is.EqualTo(new[] { "M5", "G0 X0 Y0 S0", "M2" }), "off, back to the origin, end");
		}

		[Test]
		public void LaserIsNeverOnDuringTravel()
		{
			var islands = Footprint(Input(BodyBuilder.Box(0, 0, 0, 10, 10, 2)), Input(BodyBuilder.Box(30, 0, 0, 40, 10, 2)), Input(BodyBuilder.Box(0, 30, 0, 10, 40, 2)));
			var ring = BodyBuilder.Extrude(Rect(0, 0, 40, 40), 0, 2, Rect(5, 5, 35, 35));
			var nested = Footprint(Input(ring), Input(BodyBuilder.Box(15, 15, 0, 25, 25, 2)));
			foreach (var machine in new[] { Generic(), WithM3() })
			{
				foreach (var o in new[] { Square20(), SquareWithHole(), islands, nested })
				{
					foreach (var passes in new[] { 1, 3 })
					{
						AssertLaserSafe(Write(o, Job(passes: passes), machine).Text, machine);
					}
				}
			}
		}

		private static LaserMachineProfile WithM3()
		{
			var m = Generic();
			m.LaserOnCommand = "M3";
			m.PowerSMax = 255;
			return m;
		}

		[Test]
		public void EveryLoopIsClosed()
		{
			var loops = CutLoops(Write(SquareWithHole(), Job(passes: 2)).Text, Generic());
			Assert.That(loops.Count, Is.EqualTo(4), "2 loops x 2 passes");
			foreach (var loop in loops)
			{
				Assert.That(loop.Count, Is.EqualTo(5), "4 corners and back to the start");
				Assert.That(loop.Last().X, Is.EqualTo(loop[0].X).Within(1e-9));
				Assert.That(loop.Last().Y, Is.EqualTo(loop[0].Y).Within(1e-9));
			}
		}

		[Test]
		public void HolesBeforeOutersAndPassesRepeat()
		{
			var result = Write(SquareWithHole(), Job(passes: 2));
			var loops = CutLoops(result.Text, Generic());
			bool IsHole(List<Pt> loop) => loop.All(p => p.X >= 10 - 1e-9 && p.X <= 20 + 1e-9 && p.Y >= 10 - 1e-9 && p.Y <= 20 + 1e-9);
			Assert.That(loops.Select(IsHole), Is.EqualTo(new[] { true, false, true, false }));
			Assert.That(Regex.Matches(result.Text, @"^; Pass \d of 2$", RegexOptions.Multiline).Count, Is.EqualTo(2));
			Assert.That(Lines(result.Text).Count(l => Code(l).StartsWith("M4")), Is.EqualTo(4));
			Assert.That(result.LoopsPerPass, Is.EqualTo(2));
			Assert.That(result.CutLengthMm, Is.EqualTo(2 * (120 + 40)).Within(1e-6));
		}

		[Test]
		public void IslandsAreCutBeforeTheShapeAroundThem()
		{
			// a ring (outer 40, hole 30) with a 10 mm square island in its hole
			var ring = BodyBuilder.Extrude(Rect(0, 0, 40, 40), 0, 2, Rect(5, 5, 35, 35));
			var o = Footprint(Input(ring), Input(BodyBuilder.Box(15, 15, 0, 25, 25, 2)));
			Assert.That(o.Report.Shapes, Is.EqualTo(2));
			var loops = CutLoops(Write(o).Text, Generic());
			Assert.That(loops.Count, Is.EqualTo(3));
			Assert.That(loops[0].Max(p => p.X) - loops[0].Min(p => p.X), Is.EqualTo(10).Within(1e-9), "the island first");
			Assert.That(loops[1].Max(p => p.X) - loops[1].Min(p => p.X), Is.EqualTo(30).Within(1e-9), "then the ring's hole");
			Assert.That(loops[2].Max(p => p.X) - loops[2].Min(p => p.X), Is.EqualTo(40).Within(1e-9), "then the ring's outside");
		}

		[Test]
		public void NearestShapeNext()
		{
			// three squares at x 0, 100 and 20: cut 0, then 20, then 100
			var o = Footprint(Input(BodyBuilder.Box(0, 0, 0, 10, 10, 2)), Input(BodyBuilder.Box(100, 0, 0, 110, 10, 2)), Input(BodyBuilder.Box(20, 0, 0, 30, 10, 2)));
			var starts = CutLoops(Write(o).Text, Generic()).Select(l => l.Min(p => p.X)).ToList();
			Assert.That(starts, Is.EqualTo(new[] { 0.0, 20.0, 100.0 }));

			// each loop starts at its corner nearest the head
			var first = CutLoops(Write(o).Text, Generic())[0];
			Assert.That(first[0].X, Is.EqualTo(0).Within(1e-9));
			Assert.That(first[0].Y, Is.EqualTo(0).Within(1e-9));
		}

		[Test]
		public void FeedIsMillimetresPerMinuteOfTheSpeedTyped()
		{
			var text = Write(Square20(), Job(speed: 12.5)).Text;
			var firstG1 = Lines(text).Select(Code).First(c => c.StartsWith("G1"));
			Assert.That(Word(firstG1, 'F'), Is.EqualTo(750));
			Assert.That(LaserGcodeRules.Feed(7), Is.EqualTo(420));
		}

		[Test]
		public void PowerPercentMapsToTheMachinesRange()
		{
			Assert.That(LaserGcodeRules.SValue(37, Generic()), Is.EqualTo(370));
			Assert.That(LaserGcodeRules.SValue(37, WithM3()), Is.EqualTo(94), "37 % of 0-255");
			var offset = Generic();
			offset.PowerSMin = 100;
			Assert.That(LaserGcodeRules.SValue(50, offset), Is.EqualTo(550));

			var text = Write(Square20(), Job(power: 37), WithM3()).Text;
			Assert.That(Lines(text).Select(Code).Where(c => c.StartsWith("M3")).Select(c => Word(c, 'S')).Distinct(), Is.EqualTo(new[] { 94.0 }));
		}

		[Test]
		public void PowerOverTheLimitIsRefusedAndNamed()
		{
			var machine = Generic();
			machine.MaxPowerPercent = 80;
			var result = LaserGcodeWriter.Write(Square20(), Job(power: 90), machine);
			Assert.That(result.Ok, Is.False);
			var power = result.Problems.Single(p => p.Id == LaserGcodeRules.PowerId);
			Assert.That(power.Message, Is.EqualTo("Power must be more than 0 % and no more than 80 % in the profile 'Test GRBL' (a placeholder, not checked on the machine)."));

			Assert.That(LaserGcodeWriter.Write(Square20(), Job(power: 80), machine).Ok, Is.True, "the limit itself is allowed");
			Assert.That(LaserGcodeRules.ValidateJob(Job(power: 0), Generic()).Single().Id, Is.EqualTo(LaserGcodeRules.PowerId));
			Assert.That(LaserGcodeRules.ValidateJob(Job(power: 101), Generic()).Single().Message, Does.Contain("no more than 100 %"));
			Assert.That(LaserGcodeRules.ValidateJob(Job(power: double.NaN), Generic()).Single().Message, Is.EqualTo("Enter the power in %."));

			// a profile asking for more than 100 % is refused as a profile
			machine.MaxPowerPercent = 150;
			Assert.That(LaserGcodeRules.ValidateMachine(machine).Single().Message, Does.Contain("power limit outside 0-100 %"));
			Assert.That(LaserGcodeRules.PowerLimit(machine), Is.EqualTo(100));

			// a verified profile is named without the placeholder words
			var verified = Generic();
			verified.Verified = true;
			Assert.That(LaserGcodeRules.ValidateJob(Job(power: 101), verified).Single().Message, Is.EqualTo("Power must be more than 0 % and no more than 100 % in the profile 'Test GRBL'."));
		}

		[Test]
		public void SpeedAndPassesAreChecked()
		{
			Assert.That(LaserGcodeRules.ValidateJob(Job(speed: 0), Generic()).Single().Id, Is.EqualTo(LaserGcodeRules.SpeedId));
			Assert.That(LaserGcodeRules.ValidateJob(Job(speed: 101), Generic()).Single().Message, Is.EqualTo("Speed must be more than 0 and no more than 100 mm/s in the profile 'Test GRBL' (a placeholder, not checked on the machine)."));
			Assert.That(LaserGcodeRules.ValidateJob(Job(speed: double.NaN), Generic()).Single().Message, Is.EqualTo("Enter the speed in mm/s."));
			Assert.That(LaserGcodeRules.ValidateJob(Job(passes: 0), Generic()).Single().Message, Is.EqualTo("Passes must be from 1 to 20."));
			Assert.That(LaserGcodeRules.ValidateJob(Job(passes: 21), Generic()).Single().Id, Is.EqualTo(LaserGcodeRules.PassesId));
			Assert.That(LaserGcodeRules.ValidateJob(Job(passes: 20), Generic()), Is.Empty);
		}

		[Test]
		public void TooBigForTheWorkAreaSaysByHowMuch()
		{
			var machine = Generic();
			machine.WorkAreaXMm = 50;
			machine.WorkAreaYMm = 40;
			var wide = Footprint(Input(BodyBuilder.Box(0, 0, 0, 60, 10, 2)));
			var result = LaserGcodeWriter.Write(wide, Job(), machine);
			Assert.That(result.Ok, Is.False);
			Assert.That(result.Problems.Single(p => p.Id == LaserGcodeRules.TooBigId).Message,
				Is.EqualTo("The outline (60 × 10 mm) does not fit the work area (50 × 40 mm) in the profile 'Test GRBL' (a placeholder, not checked on the machine): 10 mm too wide."));

			var both = LaserGcodeWriter.Write(Footprint(Input(BodyBuilder.Box(0, 0, 0, 55, 45.5, 2))), Job(), machine);
			Assert.That(both.Problems.Single(p => p.Id == LaserGcodeRules.TooBigId).Message, Does.EndWith("5 mm too wide and 5.5 mm too deep."));

			var fits = LaserGcodeWriter.Write(Square20(), Job(), machine);
			Assert.That(fits.Ok, Is.True);
			Assert.That(fits.Problems.Select(p => p.Id), Does.Not.Contain(LaserGcodeRules.SizeNotCheckedId));
			Assert.That(fits.Text, Does.Contain("; Work area: 50 x 40 mm (NOT verified)"));
		}

		[Test]
		public void EmptyOutlineIsRefused()
		{
			var result = LaserGcodeWriter.Write(Footprint(), Job(), Generic());
			Assert.That(result.Ok, Is.False);
			Assert.That(result.Problems.Select(p => p.Id), Does.Contain(LaserGcodeRules.EmptyId));
		}

		[Test]
		public void UnverifiedProfileAndUnknownWorkAreaAreNotes()
		{
			var result = Write(Square20());
			var notes = result.Problems.Where(p => p.Level == LaserProblemLevel.Warning).Select(p => p.Id).ToList();
			Assert.That(notes, Is.EquivalentTo(new[] { LaserGcodeRules.SizeNotCheckedId, LaserGcodeRules.NotVerifiedId }));

			var verified = Generic();
			verified.Verified = true;
			Assert.That(Write(Square20(), null, verified).Text, Does.Contain("(verified)"));
		}

		[Test]
		public void YIsFlippedWhenTheOriginIsAtTheBack()
		{
			// a triangle pointing to the back of the bed
			var o = Footprint(Input(BodyBuilder.Extrude(new List<Pt> { new Pt(0, 0), new Pt(10, 0), new Pt(5, 10) }, 0, 2)));
			var front = CutLoops(Write(o).Text, Generic()).Single();
			Assert.That(front.Any(p => p.X == 5 && p.Y == 10), Is.True, "origin at the front: the tip is at Y 10");

			var back = Generic();
			back.OriginAtBack = true;
			var flipped = CutLoops(Write(o, null, back).Text, back).Single();
			Assert.That(flipped.Any(p => p.X == 5 && p.Y == 0), Is.True, "origin at the back: the tip is at Y 0");
			Assert.That(flipped.Where(p => p.X != 5).All(p => p.Y == 10), Is.True);
		}

		[Test]
		public void NumbersIgnoreTheComputersLanguage()
		{
			var saved = CultureInfo.CurrentCulture;
			try
			{
				CultureInfo.CurrentCulture = new CultureInfo("de-DE");
				var o = Footprint(Input(BodyBuilder.Box(0, 0, 0, 30, 10, 3), RotateZ(30, 1.5)));
				var text = Write(o, Job(power: 12.5, speed: 7.25)).Text;
				foreach (var code in Lines(text).Select(Code).Where(c => c.Length > 0))
				{
					Assert.That(code, Does.Not.Contain(","), code);
				}

				Assert.That(text, Does.Contain("F435"));
				Assert.That(text, Does.Contain("12.5 %"));
			}
			finally
			{
				CultureInfo.CurrentCulture = saved;
			}
		}

		[Test]
		public void MaterialNoteCannotAddLines()
		{
			var text = Write(Square20(), Job(note: "Ply\nG1 X500 S1000\r\u0007é")).Text;
			Assert.That(Lines(text).Any(l => l.StartsWith("G1 X500")), Is.False);
			Assert.That(text, Does.Contain("; Material: PlyG1 X500 S1000"));
			Assert.That(text.All(c => c == '\n' || (c >= 0x20 && c < 0x7F)), Is.True, "printable ASCII only");
		}

		[Test]
		public void MachineProfilesThatCouldFireTheLaserAreRefused()
		{
			var bad = Generic();
			bad.Header = new List<string> { "M3 S1000" };
			Assert.That(LaserGcodeRules.ValidateMachine(bad).Single().Message, Does.Contain("start or end line that is not allowed: 'M3 S1000' (M3 is not allowed)"));
			bad = Generic();
			bad.LaserOffCommand = "M3";
			Assert.That(LaserGcodeRules.ValidateMachine(bad), Is.Not.Empty);
			bad = Generic();
			bad.LaserOnCommand = "G1";
			Assert.That(LaserGcodeRules.ValidateMachine(bad), Is.Not.Empty);
			Assert.That(LaserGcodeRules.ValidateMachine(null).Single().Message, Is.EqualTo("Choose a laser machine."));
			Assert.That(LaserGcodeWriter.Write(Square20(), Job(), bad).Ok, Is.False);
		}

		[Test]
		public void EstimateIsReturned()
		{
			var result = Write(Square20(), Job(speed: 10, passes: 2));
			Assert.That(result.CutLengthMm, Is.EqualTo(160).Within(1e-6));
			Assert.That(result.EstimatedSeconds, Is.GreaterThanOrEqualTo(16));
			Assert.That(result.TravelLengthMm, Is.GreaterThanOrEqualTo(0));
			Assert.That(LaserGcodeWriter.Duration(40), Is.EqualTo("about 40 s"));
			Assert.That(LaserGcodeWriter.Duration(61), Is.EqualTo("about 2 min"));
		}

		// ---- data ----

		[Test]
		public void TheShippedDataFilesLoad()
		{
			var machines = LaserData.LoadMachines(Source("StaticData/Laser/machines.json"));
			Assert.That(machines.Select(m => m.Id), Is.EqualTo(new[] { "generic-grbl", "xtool-s1" }));
			Assert.That(machines.All(m => !m.Verified), Is.True, "nothing is verified on a machine yet");
			foreach (var machine in machines)
			{
				Assert.That(LaserGcodeRules.ValidateMachine(machine), Is.Empty, machine.Id);
			}

			var s1 = machines.Single(m => m.Id == "xtool-s1");
			Assert.That(s1.Name, Does.Contain("not confirmed"));
			Assert.That(s1.Note, Does.Contain("NOT confirmed on the machine"));
			Assert.That(s1.WorkAreaXMm, Is.Null, "no S1 work area is stated");
			Assert.That(s1.WorkAreaYMm, Is.Null);

			var generic = machines.Single(m => m.Id == "generic-grbl");
			Assert.That((generic.PowerSMin, generic.PowerSMax, generic.LaserOnCommand, generic.LaserOffCommand), Is.EqualTo((0.0, 1000.0, "M4", "M5")));

			Assert.That(LaserData.LoadMaterials(Source("StaticData/Laser/materials.json")), Is.Empty, "no material values until the owner approves them");
		}

		[Test]
		public void BadOrEmptyDataGivesEmptyLists()
		{
			Assert.That(LaserData.LoadMaterials("{\"materials\": []}"), Is.Empty);
			Assert.That(LaserData.LoadMaterials(""), Is.Empty);
			Assert.That(LaserData.LoadMaterials("not json"), Is.Empty);
			Assert.That(LaserData.LoadMachines(null), Is.Empty);
			Assert.That(LaserData.LoadMachines("{\"machines\": [ {\"id\": \"x\", \"powerSMax\": \"lots\"}, {\"id\": \"y\"} ]}").Select(m => m.Id), Is.EqualTo(new[] { "y" }), "a bad entry is skipped");
		}

		[Test]
		public void OnlyApprovedPresetsAreOffered()
		{
			var json = @"{ ""materials"": [
				{ ""id"": ""a"", ""name"": ""Not approved"", ""approved"": false, ""approvedBy"": ""Someone"", ""operations"": { ""cut"": { ""powerPercent"": 50, ""speedMmS"": 5, ""passes"": 1 } } },
				{ ""id"": ""b"", ""name"": ""Approved by nobody"", ""approved"": true, ""approvedBy"": """", ""operations"": { ""cut"": { ""powerPercent"": 50, ""speedMmS"": 5, ""passes"": 1 } } },
				{ ""id"": ""c"", ""name"": ""Approved"", ""approved"": true, ""approvedBy"": ""Owner"", ""approvedOn"": ""2026-11-14"", ""operations"": { ""cut"": { ""powerPercent"": 50, ""speedMmS"": 5, ""passes"": 1 } } },
				{ ""id"": ""d"", ""name"": ""Approved, missing approved"", ""approvedBy"": ""Owner"", ""operations"": { ""cut"": { ""powerPercent"": 50, ""speedMmS"": 5, ""passes"": 1 } } }
			] }";
			var presets = LaserData.LoadMaterials(json);
			Assert.That(presets.Count, Is.EqualTo(4));
			Assert.That(LaserData.OfferedPresets(presets, "Cut").Select(p => p.Id), Is.EqualTo(new[] { "c" }));
			Assert.That(LaserData.OfferedPresets(presets, "Score"), Is.Empty, "no score values: not offered for score");
			Assert.That(LaserData.OfferedPresets(null, "Cut"), Is.Empty);
		}

		// ---- wiring ----

		[Test]
		public void SwitchIsOffInStockAndInTheClubAndNeedsTheMaintainer()
		{
			Assert.That(Source("MatterControlLib/SettingsManagement/OemSettings.cs"), Does.Contain("public bool EnableLaserGcode = false;"));
			Assert.That(Source("Tools/EBMC/generate_ebmc_files.py"), Does.Contain("'EnableLaserGcode': False"));
			Assert.That(Source("StaticData/OEMSettings/Settings.json"), Does.Contain("\"EnableLaserGcode\": false"));

			var tools = Source("MatterControlLib/ApplicationView/LaserTools.cs");
			var gate = tools.Substring(tools.IndexOf("public static bool GcodeAvailable", StringComparison.Ordinal));
			Assert.That(gate, Does.Contain("IsEnabled"));
			Assert.That(gate, Does.Contain("OemSettings.Instance.EnableLaserGcode"));
			Assert.That(gate, Does.Contain("Maintainer.LockEnabled"));
			Assert.That(gate, Does.Contain("Maintainer.IsUnlocked"));

			// the page offers it only when the gate is open, and checks again before saving
			var page = Source("MatterControlLib/CustomWidgets/LaserExportPage.cs");
			Assert.That(page, Does.Contain("bool gcode = LaserTools.GcodeAvailable;"));
			Assert.That(page, Does.Match(@"if \(gcode\)\s*\{\s*// maintainers only[^\n]*\n\s*gcodeOption = AddOption"));
			Assert.That(Regex.Matches(page, @"LaserTools\.GcodeAvailable").Count, Is.EqualTo(2));
			Assert.That(Source("MatterControlLib/Library/Export/LaserGcodeExport.cs"), Does.Contain("if (!LaserTools.GcodeAvailable"));
		}

		[Test]
		public void TheFileIsNcAndNeverTouchesThePrinter()
		{
			var source = Source("MatterControlLib/Library/Export/LaserGcodeExport.cs");
			Assert.That(source, Does.Contain("public string FileExtension => \".nc\";"));
			Assert.That(source, Does.Contain("\"Save as laser G-code|*.nc\""));
			Assert.That(source, Does.Contain("\"G-code for laser (.nc)\""));
			foreach (var banned in new[] { "GCodeExport", ".gcode", ".gco", "Slicer", "SliceItem", "OctoPrint", "PushToPrinter", "printer.", "Printer.", "ExportLibraryItems", "PrinterSettings", "SettingsKey" })
			{
				Assert.That(source, Does.Not.Contain(banned), banned);
			}

			Assert.That(Regex.Matches(source, "PrinterConfig").Count, Is.EqualTo(1), "only the empty Initialize the interface requires");
			Assert.That(source, Does.Match(@"public void Initialize\(PrinterConfig printer\)\s*\{\s*\}"));

			// never listed in the general Export dialog (reachable from printer tabs): only the laser page uses it
			Assert.That(source, Does.Contain("public bool Enabled => false;"));
			Assert.That(source, Does.Contain("public string DisabledReason => \"\";"));
			Assert.That(source, Does.Contain("public bool CanExport(IEnumerable<ILibraryItem> libraryItems) => false;"));

			var core = Source("MatterControlLib/DesignTools/Laser/LaserGcode.cs");
			foreach (var banned in new[] { "PrinterConfig", "GCodeExport", "SettingsKey", "OctoPrint" })
			{
				Assert.That(core, Does.Not.Contain(banned), banned);
			}

			// command search keeps one row (the page); no G-code row and no forbidden word
			var popup = Source("MatterControlLib/PartPreviewWindow/CommandSearch/CommandSearchPopup.cs");
			Assert.That(popup, Does.Not.Contain("LaserGcode"));
			Assert.That(MatterHackers.MatterControl.DesignTools.CommandSearchRules.IsForbidden("G-code for laser (.nc)"), Is.False);

			// the page still exports with no printer
			Assert.That(Source("MatterControlLib/CustomWidgets/LaserExportPage.cs"), Does.Contain("ExportPrintItemPage.DoExport(new[] { item }, null, plugin);"));
		}

		[Test]
		public void TheGeneratorWritesBothFilesAndNoMaterialValues()
		{
			var generator = Source("Tools/EBMC/generate_ebmc_files.py");
			Assert.That(generator, Does.Contain("W('StaticData/Laser/machines.json'"));
			Assert.That(generator, Does.Contain("W('StaticData/Laser/materials.json'"));
			Assert.That(generator, Does.Contain("'materials': [],"));

			var materials = Source("StaticData/Laser/materials.json");
			Assert.That(materials, Does.Contain("\"materials\": []"));
			Assert.That(materials, Does.Not.Contain("powerPercent"));
			Assert.That(materials, Does.Not.Contain("speedMmS"));

			// no S1 number is stated as fact
			var machines = Source("StaticData/Laser/machines.json");
			Assert.That(machines, Does.Contain("NOT confirmed on the machine"));
			Assert.That(Regex.Matches(machines, "\"verified\": false").Count, Is.EqualTo(2));
		}

		// ---- review fixes (stage B) ----

		[Test]
		public void StartAndEndLinesUseAnAllowList()
		{
			foreach (var bad in new[]
			{
				"M3", "M4 S500", "M03 S1000", "m03", "m4", "N10 M3 S1000", "M5 M3 S900", "G4 P1 M4 S500", "(c) M3", "G90\nM3 S1000",
				"G91", "G92", "G92 X0", "G20", "$32=0", "$H", "G0 X10", "G1 X1 F100", "G2", "G28", "S1000", "F6000", "X10",
				"G4", "G4 P100", "M5 S1000", "G21 (unclosed", "G21 )", "M3.0", "G21\tM3", "G21\r", "%", "G21 #1=2", "M 3", "G21 M5 T1",
			})
			{
				Assert.That(LaserGcodeRules.StartEndLineProblem(bad), Is.Not.Null, bad);
				var m = Generic();
				m.Header = new List<string> { bad };
				Assert.That(LaserGcodeWriter.Write(Square20(), Job(), m).Ok, Is.False, bad);
				m = Generic();
				m.Footer = new List<string> { bad };
				Assert.That(LaserGcodeWriter.Write(Square20(), Job(), m).Ok, Is.False, bad);
			}

			foreach (var good in new[]
			{
				"", "G21", "g90", "G021", "N5 G21", "M5 ; laser off", "(start) G4 P0.5", "M8", "M9", "G4 P60", "G17", "M2", "M30", "M05",
			})
			{
				Assert.That(LaserGcodeRules.StartEndLineProblem(good), Is.Null, good);
			}

			// accepted lines are written exactly as they are, on their own line
			var ok = Generic();
			ok.Header = new List<string> { "M8 ; air assist" };
			ok.Footer = new List<string> { "M9", "M2" };
			var text = Write(Square20(), null, ok).Text;
			Assert.That(text, Does.Contain("\nM8 ; air assist\n"));
			Assert.That(text, Does.EndWith("G0 X0 Y0 S0\nM9\nM2\n"));
		}

		[Test]
		public void ProfileNumbersMustBeSane()
		{
			void Refused(Action<LaserMachineProfile> change, string why)
			{
				var m = Generic();
				change(m);
				Assert.That(LaserGcodeRules.ValidateMachine(m), Is.Not.Empty, why);
				Assert.That(LaserGcodeWriter.Write(Square20(), Job(), m).Ok, Is.False, why);
			}

			Refused(m => m.PowerSMax = 1e12, "huge S");
			Refused(m => m.PowerSMax = double.PositiveInfinity, "infinite S");
			Refused(m => m.PowerSMax = double.NaN, "NaN S");
			Refused(m => m.PowerSMax = 100001, "S over 100000");
			Refused(m => m.PowerSMin = -1, "negative S");
			Refused(m => { m.PowerSMin = 500; m.PowerSMax = 500; }, "min = max");
			Refused(m => { m.PowerSMin = 600; m.PowerSMax = 500; }, "min > max");
			Refused(m => m.MaxCutSpeedMmS = 1e300, "huge speed");
			Refused(m => m.MaxCutSpeedMmS = 2001, "speed over 2000");
			Refused(m => m.TravelSpeedMmS = 0, "no travel speed");
			Refused(m => m.TravelSpeedMmS = double.PositiveInfinity, "infinite travel speed");
			Refused(m => m.MaxPowerPercent = double.NaN, "NaN clamp");
			Refused(m => m.MaxPowerPercent = 0, "no power");
			Refused(m => { m.WorkAreaXMm = 5001; m.WorkAreaYMm = 100; }, "work area over 5000");
			Refused(m => { m.WorkAreaXMm = 100; m.WorkAreaYMm = null; }, "half a work area");
			Refused(m => { m.WorkAreaXMm = double.NaN; m.WorkAreaYMm = 100; }, "NaN work area");

			var edge = Generic();
			edge.PowerSMax = 100000;
			edge.MaxCutSpeedMmS = 2000;
			edge.TravelSpeedMmS = 2000;
			edge.WorkAreaXMm = 5000;
			edge.WorkAreaYMm = 5000;
			Assert.That(LaserGcodeRules.ValidateMachine(edge), Is.Empty, "the bounds themselves are allowed");

			// a value the JSON cannot hold (1e400) leaves that profile out with a note
			var notes = new List<string>();
			var loaded = LaserData.LoadMachines("{\"machines\": [ {\"id\": \"big\", \"powerSMax\": 1e400}, {\"id\": \"ok\"} ]}", notes);
			Assert.That(loaded.Any(m => m.Id == "big" && LaserGcodeRules.ValidateMachine(m).Count == 0), Is.False);
		}

		[Test]
		public void ValuesTooSmallToHaveAnEffectAreRefused()
		{
			var power = LaserGcodeRules.ValidateJob(Job(power: 0.04), Generic()).Single();
			Assert.That(power.Id, Is.EqualTo(LaserGcodeRules.PowerId));
			Assert.That(power.Message, Does.StartWith("Power 0.04 % is too small to have any effect"));
			Assert.That(power.Message, Does.EndWith("it comes out as S0."));
			Assert.That(LaserGcodeRules.ValidateJob(Job(power: 0.05), Generic()), Is.Empty, "0.05 % of 1000 is S1");

			var offset = Generic();
			offset.PowerSMin = 100;
			offset.PowerSMax = 200;
			Assert.That(LaserGcodeRules.ValidateJob(Job(power: 0.4), offset).Single().Message, Does.EndWith("it comes out as S100."));

			// a profile whose slowest speed is tiny still cannot give F0
			var slow = Generic();
			slow.MinCutSpeedMmS = 1e-9;
			var speed = LaserGcodeRules.ValidateJob(Job(speed: 1e-7), slow).Single();
			Assert.That(speed.Id, Is.EqualTo(LaserGcodeRules.SpeedId));
			Assert.That(speed.Message, Is.EqualTo("Speed 1E-07 mm/s is too small to have any effect: it comes out as F0."));
			Assert.That(LaserGcodeRules.ValidateJob(Job(speed: 0.0001), slow), Is.Empty, "F0.006 still moves");

			Assert.That(LaserGcodeWriter.Write(Square20(), Job(power: 0.04), Generic()).Ok, Is.False);
			Assert.That(LaserGcodeWriter.Write(Square20(), Job(speed: 1e-7), slow).Ok, Is.False);
		}

		[Test]
		public void TypedNumbersAreReadStrictly()
		{
			foreach (var (text, value) in new[] { ("12", 12.0), ("12.5", 12.5), ("12,5", 12.5), ("0.05", 0.05), ("0,05", 0.05), (" 7 ", 7.0), ("100", 100.0), ("012", 12.0) })
			{
				Assert.That(LaserNumberText.TryParse(text, out var v), Is.True, text);
				Assert.That(v, Is.EqualTo(value), text);
			}

			foreach (var text in new[] { "1,000", "1.000", "1,000.5", "1.000,5", "0.125", "1,2,3", "1.2.3", "-5", "+5", "1e3", "1E3", "Infinity", "NaN", "∞", "", " ", "1 000", ".5", "5.", ",5", "5,", "0x10", "1234567", "12%" })
			{
				Assert.That(LaserNumberText.TryParse(text, out var v), Is.False, text);
				Assert.That(double.IsNaN(v), Is.True, text);
			}

			Assert.That(LaserNumberText.TryParseWhole("3", out var passes) && passes == 3, Is.True);
			foreach (var text in new[] { "3.0", "3,0", "-1", "1e1", "", "two" })
			{
				Assert.That(LaserNumberText.TryParseWhole(text, out _), Is.False, text);
			}

			// the page's reading: "1,000" for speed is refused, never read as 1
			var job = new LaserJobSettings();
			var problems = LaserNumberText.ReadJob("50", "1,000", "1", job);
			Assert.That(problems.Single().Id, Is.EqualTo(LaserGcodeRules.SpeedId));
			Assert.That(problems.Single().Message, Is.EqualTo("Speed: type a number like 12 or 12.5 (at most 2 decimals, no thousands separator)."));
			Assert.That(double.IsNaN(job.SpeedMmS), Is.True);

			job = new LaserJobSettings();
			Assert.That(LaserNumberText.ReadJob("12,5", "20", "2", job), Is.Empty);
			Assert.That((job.PowerPercent, job.SpeedMmS, job.Passes), Is.EqualTo((12.5, 20.0, 2)));
			Assert.That(LaserNumberText.ReadJob("", "", "", new LaserJobSettings()).Select(p => p.Message),
				Is.EqualTo(new[] { "Enter the power in %.", "Enter the speed in mm/s.", "Enter the number of passes." }));

			// the page uses the strict reader, then the range checks
			var page = Source("MatterControlLib/CustomWidgets/LaserExportPage.cs");
			Assert.That(page, Does.Contain("LaserNumberText.ReadJob(powerEdit.Text, speedEdit.Text, passesEdit.Text, job)"));
			Assert.That(page, Does.Not.Contain("Replace(',', '.')"));
		}

		private static LaserOutline ManySquares(int count)
		{
			var parts = new List<LaserMeshInput>();
			var mesh = new ClubMesh();
			for (int i = 0; i < count; i++)
			{
				double x = (i % 50) * 5, y = (i / 50) * 5;
				mesh.Add(BodyBuilder.Box(x, y, 0, x + 3, y + 3, 2));
			}

			return Footprint(Input(mesh));
		}

		[Test]
		public void ThousandsOfShapesAreQuickAndCancellable()
		{
			var o = ManySquares(2000);
			Assert.That(o.Report.Shapes, Is.EqualTo(2000));
			Write(Square20());
			var watch = System.Diagnostics.Stopwatch.StartNew();
			var result = Write(o);
			watch.Stop();
			TestContext.Out.WriteLine($"Laser G-code for 2,000 shapes: {watch.ElapsedMilliseconds} ms");
			Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(1));
			Assert.That(result.LoopsPerPass, Is.EqualTo(2000));
			AssertLaserSafe(result.Text, Generic());

			// nearest-next still holds: each next square starts no further away than any square left
			var loops = CutLoops(result.Text, Generic());
			Assert.That(loops[0][0].X == 0 && loops[0][0].Y == 0, Is.True);
			var head = new Pt(0, 0);
			var remaining = loops.Select(l => l.Take(l.Count - 1).ToList()).ToList();
			for (int i = 0; i < 200; i++)
			{
				var chosen = remaining[0];
				var best = remaining.Min(l => l.Min(p => (p - head).Length));
				Assert.That(chosen.Min(p => (p - head).Length), Is.EqualTo(best).Within(1e-9), $"loop {i}");
				head = chosen[0];
				remaining.RemoveAt(0);
			}

			using (var cts = new System.Threading.CancellationTokenSource())
			{
				cts.Cancel();
				Assert.That(() => LaserGcodeWriter.Write(o, Job(), Generic(), cts.Token), Throws.InstanceOf<OperationCanceledException>());
			}

			Assert.That(Source("MatterControlLib/Library/Export/LaserGcodeExport.cs"), Does.Contain("LaserGcodeWriter.Write(outline, job, machine, cancellationToken)"));
		}

		[Test]
		public void NestingStillCountsWithTheBoxTest()
		{
			// three nested rings and an island: deepest first, then outwards
			var a = BodyBuilder.Extrude(Rect(0, 0, 100, 100), 0, 2, Rect(10, 10, 90, 90));
			var b = BodyBuilder.Extrude(Rect(20, 20, 80, 80), 0, 2, Rect(30, 30, 70, 70));
			var c = BodyBuilder.Box(40, 40, 0, 60, 60, 2);
			var loops = CutLoops(Write(Footprint(Input(a), Input(b), Input(c))).Text, Generic());
			var widths = loops.Select(l => l.Max(p => p.X) - l.Min(p => p.X)).ToList();
			Assert.That(widths, Is.EqualTo(new[] { 20.0, 40.0, 60.0, 80.0, 100.0 }));
		}

		[Test]
		public void LoopsTooSmallToCutAreLeftOutAndCounted()
		{
			LaserLoop Loop(params (double x, double y)[] pts) => new LaserLoop { Points = pts.Select(p => new Pt(p.x, p.y)).ToList() };
			var shapes = new List<LaserShape>
			{
				new LaserShape { Outer = Loop((0, 0), (10, 0), (10, 10), (0, 10)), Holes = new List<LaserLoop> { Loop((5, 5), (5, 5), (5, 5)) } },
				new LaserShape { Outer = Loop((20, 0), (20.0001, 0)), Holes = new List<LaserLoop>() },
				new LaserShape { Outer = Loop((30, 0), (30.0002, 0), (30.0001, 0.0002)), Holes = new List<LaserLoop>() },
				new LaserShape { Outer = Loop((40, 0), (41, 0), (41, 0), (41, 1)), Holes = new List<LaserLoop>() },
			};
			var o = new LaserOutline
			{
				Layers = new List<LaserLayer> { new LaserLayer { Name = "Cut", Shapes = shapes } },
				MinX = 0,
				MinY = 0,
				MaxX = 41,
				MaxY = 10,
				Report = new LaserOutlineReport(),
			};

			var result = Write(o);
			Assert.That(result.SkippedLoops, Is.EqualTo(3), "a hole of one repeated point, a 2-point loop, and one that rounds to a single point");
			Assert.That(result.LoopsPerPass, Is.EqualTo(2));
			Assert.That(result.Problems.Single(p => p.Id == LaserGcodeRules.SkippedLoopsId).Message, Is.EqualTo("3 loops too small to cut were left out."));
			Assert.That(result.Text, Does.Contain("; Left out: 3 loop(s) too small to cut."));
			AssertLaserSafe(result.Text, Generic());

			// every laser-on is followed by at least two moves that go somewhere
			foreach (var loop in CutLoops(result.Text, Generic()))
			{
				Assert.That(loop.Select(p => (p.X, p.Y)).Distinct().Count(), Is.GreaterThanOrEqualTo(3));
			}

			// the repeated corner (41, 0) is written once
			var last = CutLoops(result.Text, Generic()).Single(l => l.Any(p => p.X == 41));
			Assert.That(last.Count, Is.EqualTo(4));

			// nothing cuttable at all: refused
			o.Layers = new List<LaserLayer> { new LaserLayer { Name = "Cut", Shapes = shapes.Skip(1).Take(2).ToList() } };
			var none = LaserGcodeWriter.Write(o, Job(), Generic());
			Assert.That(none.Ok, Is.False);
			Assert.That(none.Problems.Select(p => p.Id), Does.Contain(LaserGcodeRules.EmptyId));
		}

		[Test]
		public void RepeatedIdsKeepTheFirstAndOperationsMatchAnyCase()
		{
			var notes = new List<string>();
			var machines = LaserData.LoadMachines("{\"machines\": [ {\"id\": \"a\", \"name\": \"First\"}, {\"id\": \"A\", \"name\": \"Second\"}, {\"id\": \"b\", \"name\": \"Other\"} ]}", notes);
			Assert.That(machines.Select(m => m.Name), Is.EqualTo(new[] { "First", "Other" }));
			Assert.That(notes, Is.EqualTo(new[] { "The machine profile id 'A' appears more than once; only the first one is used." }));

			notes.Clear();
			var presets = LaserData.LoadMaterials(@"{ ""materials"": [
				{ ""id"": ""p"", ""name"": ""Ply"", ""approved"": true, ""approvedBy"": ""Owner"", ""operations"": { ""Cut"": { ""powerPercent"": 50, ""speedMmS"": 5, ""passes"": 1 }, ""SCORE"": { ""powerPercent"": 10, ""speedMmS"": 20, ""passes"": 1 } } },
				{ ""id"": ""p"", ""name"": ""Ply again"", ""approved"": true, ""approvedBy"": ""Owner"", ""operations"": { ""cut"": { ""powerPercent"": 50, ""speedMmS"": 5, ""passes"": 1 } } }
			] }", notes);
			Assert.That(presets.Select(p => p.Name), Is.EqualTo(new[] { "Ply" }));
			Assert.That(notes.Single(), Does.Contain("material preset id 'p' appears more than once"));
			Assert.That(LaserData.OfferedPresets(presets, "cut").Count, Is.EqualTo(1));
			Assert.That(LaserData.OfferedPresets(presets, "Score").Count, Is.EqualTo(1));
			Assert.That(presets[0].Operations.ContainsKey("score"), Is.True);
			Assert.That(presets[0].Operations["CUT"].PowerPercent, Is.EqualTo(50));

			// the page shows the load notes
			Assert.That(Source("MatterControlLib/CustomWidgets/LaserExportPage.cs"), Does.Contain("LaserDataFiles.LoadNotes"));
		}

		[Test]
		public void PlaceholderProfilesShowNoLimitHints()
		{
			var page = Source("MatterControlLib/CustomWidgets/LaserExportPage.cs");
			Assert.That(page, Does.Contain("powerLimit.Text = machine.Verified ? "));
			Assert.That(page, Does.Contain("speedLimit.Text = machine.Verified ? "));

			// refusals say the limit comes from a placeholder profile
			var s1 = LaserData.LoadMachines(Source("StaticData/Laser/machines.json")).Single(m => m.Id == "xtool-s1");
			var message = LaserGcodeRules.ValidateJob(Job(power: 101), s1).Single().Message;
			Assert.That(message, Is.EqualTo("Power must be more than 0 % and no more than 100 % in the profile 'xTool S1 (not confirmed)' (a placeholder, not checked on the machine)."));
		}

		// ---- hardening ----

		[Test]
		public void StartEndLinesHaveOneWordPerLetterAndAreShort()
		{
			foreach (var bad in new[] { "G4 P1 P2", "G21 G90", "G17 G94 G54", "M5 M9", "N1 N2 G21", "M8 m9" })
			{
				Assert.That(LaserGcodeRules.StartEndLineProblem(bad), Does.Contain("appears more than once"), bad);
				var m = Generic();
				m.Header = new List<string> { bad };
				Assert.That(LaserGcodeWriter.Write(Square20(), Job(), m).Ok, Is.False, bad);
			}

			var seventy = "G21 ;" + new string('x', 65);
			Assert.That(seventy.Length, Is.EqualTo(70));
			Assert.That(LaserGcodeRules.StartEndLineProblem(seventy), Is.Null);
			Assert.That(LaserGcodeRules.StartEndLineProblem(seventy + "x"), Is.EqualTo("it is longer than 70 characters"));
			var m2 = Generic();
			m2.Footer = new List<string> { seventy + "x" };
			Assert.That(LaserGcodeWriter.Write(Square20(), Job(), m2).Ok, Is.False);
		}

		[Test]
		public void DwellTimeIsPOnly()
		{
			foreach (var bad in new[] { "G4 P1 S50", "G4 S1", "G4 S0", "G4", "G4 P61" })
			{
				Assert.That(LaserGcodeRules.StartEndLineProblem(bad), Is.Not.Null, bad);
				var m = Generic();
				m.Header = new List<string> { bad };
				Assert.That(LaserGcodeWriter.Write(Square20(), Job(), m).Ok, Is.False, bad);
			}

			Assert.That(LaserGcodeRules.StartEndLineProblem("G4 P1 S50"), Is.EqualTo("S50 is not allowed"));
			Assert.That(LaserGcodeRules.StartEndLineProblem("G4"), Is.EqualTo("G4 needs a P dwell time"));
			Assert.That(LaserGcodeRules.StartEndLineProblem("G4 P1"), Is.Null);
		}

		[Test]
		public void TooSlowIsRefusedAsABurnRisk()
		{
			var m = Generic();
			m.MinCutSpeedMmS = 2;
			var problem = LaserGcodeRules.ValidateJob(Job(speed: 1.5), m).Single();
			Assert.That(problem.Id, Is.EqualTo(LaserGcodeRules.SpeedId));
			Assert.That(problem.Message, Is.EqualTo("Speed must be at least 2 mm/s in the profile 'Test GRBL' (a placeholder, not checked on the machine): a very slow speed with the laser on can start a fire."));
			Assert.That(LaserGcodeRules.ValidateJob(Job(speed: 2), m), Is.Empty, "the minimum itself is allowed");

			// the writer refuses too, not only the page
			var result = LaserGcodeWriter.Write(Square20(), Job(speed: 0.5), Generic());
			Assert.That(result.Ok, Is.False);
			Assert.That(result.Problems.Single(p => p.Id == LaserGcodeRules.SpeedId).Message, Does.StartWith("Speed must be at least 1 mm/s"));

			// the profile's own value must be sane
			foreach (var bad in new[] { 0.0, -1, double.NaN, double.PositiveInfinity, 100, 150 })
			{
				var p = Generic();
				p.MinCutSpeedMmS = bad;
				Assert.That(LaserGcodeRules.ValidateMachine(p).Any(x => x.Message.Contains("slowest cutting speed")), Is.True, bad.ToString(CultureInfo.InvariantCulture));
			}

			// both shipped profiles carry 1 mm/s
			var machines = LaserData.LoadMachines(Source("StaticData/Laser/machines.json"));
			Assert.That(machines.Select(x => x.MinCutSpeedMmS), Is.EqualTo(new[] { 1.0, 1.0 }));
			Assert.That(Source("StaticData/Laser/machines.json"), Does.Contain("\"minCutSpeedMmS\": 1"));
			Assert.That(Source("Tools/EBMC/generate_ebmc_files.py"), Does.Contain("'minCutSpeedMmS': 1"));
		}

		// ---- stage A follow-ups ----

		[Test]
		public void HoleWarningsAreCapped()
		{
			var parts = new List<LaserMeshInput> { Input(BodyBuilder.Box(0, 0, 0, 100, 10, 20), name: "Tower") };
			for (int i = 0; i < 7; i++)
			{
				parts.Add(Input(BodyBuilder.Box(2 + i * 12, 2, 10, 8 + i * 12, 8, 21), role: LaserRole.Hole, name: "Dimple " + (i + 1)));
			}

			var o = Footprint(parts.ToArray());
			Assert.That(o.Report.HoleWarnings, Is.EqualTo(7));
			Assert.That(o.Report.HolesNotThrough.Count, Is.EqualTo(7));
			Assert.That(o.Report.Warnings.Count, Is.EqualTo(6));
			Assert.That(o.Report.Warnings.Take(5).All(w => w.Contains("does not go all the way through 'Tower'")), Is.True);
			Assert.That(o.Report.Warnings.Last(), Is.EqualTo("and 2 more like this"));

			// five or fewer: no extra line
			Assert.That(Footprint(parts.Take(6).ToArray()).Report.Warnings.Count, Is.EqualTo(5));
		}

		[Test]
		public void AClickOnAFormatsOptionsSelectsThatFormat()
		{
			var source = Source("MatterControlLib/Library/Export/LaserExport.cs");
			var at = source.IndexOf("panel.MouseDown +=", StringComparison.Ordinal);
			Assert.That(at, Is.GreaterThan(0));
			Assert.That(source.Substring(at, 250), Does.Contain("radioButton.InvokeClick()"));
		}
	}
}
