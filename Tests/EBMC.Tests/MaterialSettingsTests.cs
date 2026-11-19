using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl.OctoPrint;
using MatterHackers.MatterControl.SlicerConfiguration;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Plan A: one source of truth for material settings. The settings precedence, the club profile's nylon presets, the
	/// pre-print summary, and the comparison and update of an installed profile's club presets.
	/// </summary>
	[TestFixture]
	[SetCulture("en-US")]
	public class MaterialSettingsTests
	{
		private const string ProfilePath = "StaticData/Profiles/East Bay Makers Club/Pulse S-232 Mosquito.printer";

		private const string NylonXId = "72a66f1c-2c30-43d8-8748-2a5ab2035143";

		private const string NylonGId = "837244a6-03d8-46d2-8f84-4ecc607da4d9";

		private static readonly Func<string, string> Identity = s => s;

		private static string root;

		private string tempFolder;

		[OneTimeSetUp]
		public void FindRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, ProfilePath)))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null, "repository root");
			root = directory.FullName;
		}

		[SetUp]
		public void MakeTempFolder()
		{
			tempFolder = Path.Combine(Path.GetTempPath(), "ebmc-presets-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempFolder);
		}

		[TearDown]
		public void RemoveTempFolder()
		{
			try
			{
				Directory.Delete(tempFolder, recursive: true);
			}
			catch
			{
			}
		}

		private static string ClubJson => File.ReadAllText(Path.Combine(root, ProfilePath));

		private static Dictionary<string, string> D(params string[] keyValues)
		{
			var result = new Dictionary<string, string>();
			for (int i = 0; i < keyValues.Length; i += 2)
			{
				result[keyValues[i]] = keyValues[i + 1];
			}

			return result;
		}

		// ---- precedence ----

		[Test]
		public void EachLayerBeatsTheOnesBelowIt()
		{
			const string key = "k";
			var cascade = new SettingsCascade()
				.With(SettingsCascade.Base, D(key, "base"))
				.With(SettingsCascade.Oem, D(key, "oem"))
				.With(SettingsCascade.Quality, D(key, "quality"))
				.With(SettingsCascade.Material, D(key, "material"))
				.With(SettingsCascade.Scene, D(key, "scene"))
				.With(SettingsCascade.User, D(key, "user"));

			// remove the top layer one at a time: the next one down wins each time
			var expected = new[] { ("user", "User"), ("scene", "Scene"), ("material", "Material"), ("quality", "Quality"), ("oem", "Oem"), ("base", "Base") };
			foreach (var (value, layer) in expected)
			{
				Assert.That(cascade.GetValueAndLayerName(key), Is.EqualTo((value, layer)));
				cascade.With(layer, null);
			}

			Assert.That(cascade.GetValueAndLayerName(key), Is.EqualTo(("", (string)null)));
		}

		[Test]
		public void BelowUserSkipsOnlyTheUserLayer()
		{
			var cascade = new SettingsCascade()
				.With(SettingsCascade.User, D("a", "1", "b", "2"))
				.With(SettingsCascade.Material, D("a", "10"))
				.With(SettingsCascade.Base, D("a", "100", "b", "200"));

			Assert.That(cascade.GetValueAndLayerNameBelowUser("a"), Is.EqualTo(("10", "Material")));
			Assert.That(cascade.GetValueAndLayerNameBelowUser("b"), Is.EqualTo(("200", "Base")));
			Assert.Throws<ArgumentException>(() => cascade.With("Bogus", D()));
		}

		[Test]
		public void TheModelOrderIsTheAppsDefaultLayerCascade()
		{
			// PrinterSettings cannot be compiled into this project (agg-sharp, Newtonsoft); read its cascade instead
			string source = File.ReadAllText(Path.Combine(root, "MatterControl.Printing/Settings/PrinterSettings.cs"));
			int start = source.IndexOf("public IEnumerable<PrinterSettingsLayer> DefaultLayerCascade", StringComparison.Ordinal);
			Assert.That(start, Is.GreaterThan(0));
			int end = source.IndexOf("yield return this.BaseLayer;", start, StringComparison.Ordinal);
			string body = source.Substring(start, end - start + "yield return this.BaseLayer;".Length);

			var map = new Dictionary<string, string>
			{
				["this.UserLayer"] = SettingsCascade.User,
				["sceneLayer"] = SettingsCascade.Scene,
				["this.MaterialLayer"] = SettingsCascade.Material,
				["this.QualityLayer"] = SettingsCascade.Quality,
				["this.OemLayer"] = SettingsCascade.Oem,
				["this.BaseLayer"] = SettingsCascade.Base,
			};
			var order = Regex.Matches(body, @"yield return ([\w.]+);").Select(m => map[m.Groups[1].Value]).ToList();
			Assert.That(order, Is.EqualTo(SettingsCascade.Order));

			// and GetValueAndLayerName names the layers as the model does
			int names = source.IndexOf("public (string currentValue, string layerName) GetValueAndLayerName", StringComparison.Ordinal);
			string namesBody = source.Substring(names, source.IndexOf("return (\"\", \"\");", names, StringComparison.Ordinal) - names);
			foreach (string name in SettingsCascade.Order)
			{
				Assert.That(namesBody, Does.Contain("\"" + name + "\""), name);
			}
		}

		// ---- the club profile ----

		[Test]
		public void ClubProfileNylonPresets()
		{
			var presets = ClubPresets.ReadMaterialPresets(ClubJson);

			var nylonX = presets.Single(p => p.Name == "NylonX");
			Assert.That(nylonX.Id, Is.EqualTo(NylonXId));
			Assert.That(nylonX.Values[SettingsKey.temperature], Is.EqualTo("265"));
			Assert.That(nylonX.Values[SettingsKey.bed_temperature], Is.EqualTo("80"));

			var nylonG = presets.Single(p => p.Name == "NylonG");
			Assert.That(nylonG.Id, Is.EqualTo(NylonGId));
			Assert.That(nylonG.Values[SettingsKey.temperature], Is.EqualTo("260"));
			Assert.That(nylonG.Values[SettingsKey.bed_temperature], Is.EqualTo("80"));

			// NylonG's fans, first-layer width and retraction are NylonX's
			foreach (string key in new[] { "min_fan_speed", "max_fan_speed", "bridge_fan_speed", "raft_fan_speed_percent", "first_layer_extrusion_width", "retract_restart_extra", "retract_restart_extra_time_to_apply" })
			{
				Assert.That(nylonG.Values[key], Is.EqualTo(nylonX.Values[key]), key);
			}
		}

		[Test]
		public void EveryClubPresetHasAUniqueLayerId()
		{
			var json = JsonNode.Parse(ClubJson);
			var ids = new List<string>();
			foreach (string list in new[] { "MaterialLayers", "QualityLayers" })
			{
				foreach (var layer in json[list].AsArray())
				{
					string id = (string)layer["layer_id"];
					Assert.That(id, Is.Not.Null.And.Not.Empty, (string)layer["layer_name"]);
					Assert.That(Guid.TryParse(id, out _), Is.True, id);
					ids.Add(id);
				}
			}

			Assert.That(ids, Is.Unique);
		}

		[Test]
		public void NylonGLayerIdIsFixedInTheGenerator()
		{
			// a random id per run would make every regenerated profile look like a new preset to installed copies
			string generator = File.ReadAllText(Path.Combine(root, "Tools/EBMC/generate_ebmc_files.py"));
			Assert.That(generator, Does.Contain("'layer_id': '" + NylonGId + "'"));
		}

		[Test]
		public void ClubProfileIsRecognised()
		{
			Assert.That(ClubPresets.IsClubProfileJson(ClubJson), Is.True);
			Assert.That(ClubPresets.IsClubPrinter("East Bay Makers Club", "Pulse S-232 Mosquito"), Is.True);
			Assert.That(ClubPresets.IsClubPrinter("Pulse", "S-232"), Is.False);
			Assert.That(ClubPresets.IsClubPrinter("East Bay Makers Club", ""), Is.False);
			Assert.That(ClubPresets.IsClubPrinter(null, null), Is.False);

			string stock = File.ReadAllText(Path.Combine(root, "StaticData/Profiles/Pulse/S-232.printer"));
			Assert.That(ClubPresets.IsClubProfileJson(stock), Is.False);
			Assert.That(File.Exists(Path.Combine(root, "StaticData", ClubPresets.BundledProfilePath)), Is.True);
		}

		// ---- the summary ----

		/// <summary>A small club-like printer: NylonX preset, Standard quality, profile values and defaults.</summary>
		private static SettingsCascade Printer(Dictionary<string, string> user = null, bool heatedBed = true)
		{
			return new SettingsCascade() { MaterialName = "NylonX", QualityName = "Standard" }
				.With(SettingsCascade.User, user ?? D())
				.With(SettingsCascade.Material, D(SettingsKey.temperature, "265", SettingsKey.bed_temperature, "80"))
				.With(SettingsCascade.Quality, D(SettingsKey.layer_height, "0.15"))
				.With(SettingsCascade.Oem, D(SettingsKey.layer_height, "0.15", SettingsKey.first_layer_height, "0.2", SettingsKey.has_heated_bed, heatedBed ? "1" : "0"))
				.With(SettingsCascade.Base, D(SettingsKey.temperature, "200", SettingsKey.bed_temperature, "70", SettingsKey.layer_height, "0.4",
					SettingsKey.first_layer_height, "0.3", SettingsKey.has_heated_bed, "0", SettingsKey.create_brim, "0", SettingsKey.brims, "8mm"));
		}

		private static string[] Lines(IPrintSummarySource source) => PrintSummary.Build(source, Identity).Select(l => l.Text).ToArray();

		[Test]
		public void SummaryFromPresetsOnly()
		{
			Assert.That(Lines(Printer()), Is.EqualTo(new[]
			{
				"Material: NylonX",
				"Nozzle: 265 °C  (NylonX preset)",
				"Bed: 80 °C  (NylonX preset)",
				"Layer height: 0.15 mm  (Standard quality)",
				"First layer: 0.2 mm  (printer profile)",
				"Brim: off  (default)",
			}));
			Assert.That(PrintSummary.Build(Printer(), Identity).Any(l => l.ChangedByHand), Is.False);
		}

		[Test]
		public void SummaryShowsHandOverridesWithThePresetValue()
		{
			var user = D(SettingsKey.bed_temperature, "60", SettingsKey.temperature, "255", SettingsKey.layer_height, "0.2",
				SettingsKey.create_brim, "1", SettingsKey.brims, "3");
			var lines = PrintSummary.Build(Printer(user), Identity);

			Assert.That(lines.Select(l => l.Text), Is.EqualTo(new[]
			{
				"Material: NylonX",
				"Nozzle: 255 °C\n    changed by hand; NylonX preset says 265 °C",
				"Bed: 60 °C\n    changed by hand; NylonX preset says 80 °C",
				"Layer height: 0.2 mm\n    changed by hand; Standard quality says 0.15 mm",
				"First layer: 0.2 mm  (printer profile)",
				"Brim: on, 3 loops\n    changed by hand; default says off",
			}));
			Assert.That(lines.Where(l => l.ChangedByHand).Select(l => l.Key), Is.EqualTo(new[]
			{
				SettingsKey.temperature, SettingsKey.bed_temperature, SettingsKey.layer_height, SettingsKey.create_brim,
			}));
			Assert.That(lines[2].LayerName, Is.EqualTo(SettingsCascade.User));
		}

		[Test]
		public void SummaryHandOverrideEqualToThePresetIsNotAChange()
		{
			var lines = PrintSummary.Build(Printer(D(SettingsKey.bed_temperature, "80.0")), Identity);
			Assert.That(lines[2].Text, Is.EqualTo("Bed: 80 °C  (NylonX preset)"));
			Assert.That(lines[2].ChangedByHand, Is.False);
			Assert.That(lines[2].LayerName, Is.EqualTo(SettingsCascade.Material));
		}

		/// <summary>The club-like printer with the NylonX preset turning the brim on and the profile giving 2 loops.</summary>
		private static SettingsCascade BrimByPreset(Dictionary<string, string> user)
		{
			return Printer(user)
				.With(SettingsCascade.Material, D(SettingsKey.temperature, "265", SettingsKey.bed_temperature, "80", SettingsKey.create_brim, "1"))
				.With(SettingsCascade.Oem, D(SettingsKey.layer_height, "0.15", SettingsKey.first_layer_height, "0.2", SettingsKey.has_heated_bed, "1", SettingsKey.brims, "2"));
		}

		[Test]
		public void SummaryBrimNamesTheLayerThatDiffers()
		{
			// the preset turns the brim on, the profile gives the loops, the user changed only the loops
			Assert.That(Lines(BrimByPreset(D(SettingsKey.brims, "5"))).Last(), Is.EqualTo("Brim: on, 5 loops\n    loops changed by hand; printer profile says 2 loops"));

			// nothing changed by hand: both sources named
			Assert.That(Lines(BrimByPreset(D())).Last(), Is.EqualTo("Brim: on, 2 loops  (NylonX preset; loops: printer profile)"));

			// the brim turned off by hand
			Assert.That(Lines(BrimByPreset(D(SettingsKey.create_brim, "0"))).Last(), Is.EqualTo("Brim: off\n    changed by hand; NylonX preset says on, 2 loops"));

			// loops changed by hand while the brim is off: nothing to report
			Assert.That(Lines(Printer(D(SettingsKey.brims, "5"))).Last(), Is.EqualTo("Brim: off  (default)"));

			// a brim given in mm
			Assert.That(Lines(Printer(D(SettingsKey.create_brim, "1"))).Last(), Is.EqualTo("Brim: on, 8 mm wide\n    changed by hand; default says off"));
		}

		[Test]
		public void SummaryBrimIsOffUnderARaft()
		{
			// MatterSlice draws no brim when a raft is on (LayerDataStorage.GetSkirtBounds), and the printable area is worked
			// out the same way (PrinterSettings: create_brim && !create_raft)
			var line = PrintSummary.Build(Printer(D(SettingsKey.create_brim, "1", SettingsKey.brims, "3", SettingsKey.create_raft, "1")), Identity).Last();
			Assert.That(line.Text, Is.EqualTo("Brim: off  (a raft is on)"));
			Assert.That(line.ChangedByHand, Is.False);
		}

		[Test]
		public void SummaryFirstLayerAsSliced()
		{
			// a percentage of the layer height (AsPercentOfReferenceOrDirect on layer_height), with the layer height that
			// will be sliced, hand override included
			var percent = Printer(D(SettingsKey.layer_height, "0.2"))
				.With(SettingsCascade.Oem, D(SettingsKey.layer_height, "0.15", SettingsKey.first_layer_height, "150%", SettingsKey.has_heated_bed, "1"));
			Assert.That(Lines(percent)[4], Is.EqualTo("First layer: 0.3 mm (150%)  (printer profile)"));

			// 0 means the layer height
			var zero = Printer().With(SettingsCascade.Oem, D(SettingsKey.layer_height, "0.15", SettingsKey.first_layer_height, "0", SettingsKey.has_heated_bed, "1"));
			Assert.That(Lines(zero)[4], Is.EqualTo("First layer: 0.15 mm  (printer profile)"));

			// changed by hand to a percentage: both sides in mm
			Assert.That(Lines(Printer(D(SettingsKey.first_layer_height, "200%")))[4],
				Is.EqualTo("First layer: 0.3 mm (200%)\n    changed by hand; printer profile says 0.2 mm"));
		}

		[Test]
		public void SummaryWithoutHeatedBedLeavesTheBedOut()
		{
			var lines = Lines(Printer(D(SettingsKey.bed_temperature, "60"), heatedBed: false));
			Assert.That(lines.Any(l => l.StartsWith("Bed", StringComparison.Ordinal)), Is.False);
			Assert.That(lines, Has.Length.EqualTo(5));
		}

		[Test]
		public void SummaryWithoutMaterialOrQualityPreset()
		{
			var cascade = new SettingsCascade()
				.With(SettingsCascade.User, D(SettingsKey.temperature, "230"))
				.With(SettingsCascade.Base, D(SettingsKey.temperature, "200", SettingsKey.layer_height, "0.4", SettingsKey.first_layer_height, "150%"));
			Assert.That(Lines(cascade), Is.EqualTo(new[]
			{
				"Material: none chosen",
				"Nozzle: 230 °C\n    changed by hand; default says 200 °C",
				"Layer height: 0.4 mm  (default)",
				"First layer: 0.6 mm (150%)  (default)",
				"Brim: off",
			}));
		}

		// ---- bed temperature key: the one the G-code heats to ----

		[TestCase(false, true, "Garolite", SettingsKey.bed_temperature)]
		[TestCase(true, false, "Garolite", SettingsKey.bed_temperature)]
		[TestCase(true, true, "Garolite", SettingsKey.bed_temperature_garolite)]
		[TestCase(true, true, "Blue Tape", SettingsKey.bed_temperature_blue_tape)]
		[TestCase(true, true, "BuildTak", SettingsKey.bed_temperature_buildtak)]
		[TestCase(true, true, "Glass", SettingsKey.bed_temperature_glass)]
		[TestCase(true, true, "Kapton", SettingsKey.bed_temperature_kapton)]
		[TestCase(true, true, "PEI", SettingsKey.bed_temperature_pei)]
		[TestCase(true, true, "Polypropylene", SettingsKey.bed_temperature_pp)]
		[TestCase(true, true, "Something else", SettingsKey.bed_temperature)]
		[TestCase(true, true, "", SettingsKey.bed_temperature)]
		public void BedTemperatureKeySelection(bool swappable, bool materialHasSurfaceTemperatures, string surface, string expected)
		{
			Assert.That(PrintSummary.ActiveBedTemperatureKey(swappable, materialHasSurfaceTemperatures, surface), Is.EqualTo(expected));
		}

		[Test]
		public void TheAppAndTheSummaryUseTheSameBedKey()
		{
			// SettingsHelpers.ActiveBedTemperatureSetting (what ReplaceSettingsNamesWithValues puts in the G-code) calls the
			// same selection, and the app's summary source reads it from there
			string helpers = File.ReadAllText(Path.Combine(root, "MatterControl.Printing/Settings/SettingsHelpers.cs"));
			int property = helpers.IndexOf("public string ActiveBedTemperatureSetting", StringComparison.Ordinal);
			Assert.That(property, Is.GreaterThan(0));
			Assert.That(helpers.IndexOf("PrintSummary.ActiveBedTemperatureKey(", property, StringComparison.Ordinal), Is.GreaterThan(property));
			Assert.That(helpers.Substring(property, 600), Does.Not.Contain("switch"));

			string source = File.ReadAllText(Path.Combine(root, "MatterControl.Printing/Settings/PrinterSettingsSummarySource.cs"));
			Assert.That(source, Does.Contain("BedTemperatureKey => settings.Helpers.ActiveBedTemperatureSetting"));
		}

		[Test]
		public void SummaryBedUsesTheSurfaceTemperature()
		{
			var swappable = Printer()
				.With(SettingsCascade.Material, D(SettingsKey.temperature, "265", SettingsKey.bed_temperature, "80", SettingsKey.bed_temperature_garolite, "75", SettingsKey.bed_temperature_pei, "NC"))
				.With(SettingsCascade.Oem, D(SettingsKey.layer_height, "0.15", SettingsKey.first_layer_height, "0.2", SettingsKey.has_heated_bed, "1",
					SettingsKey.has_swappable_bed, "1", SettingsKey.bed_surface, "Garolite"));
			Assert.That(swappable.BedTemperatureKey, Is.EqualTo(SettingsKey.bed_temperature_garolite));
			Assert.That(Lines(swappable)[2], Is.EqualTo("Bed (Garolite): 75 °C  (NylonX preset)"));

			// a hand change of bed_temperature does not reach the G-code then, and the summary does not show it
			swappable.With(SettingsCascade.User, D(SettingsKey.bed_temperature, "60"));
			Assert.That(Lines(swappable)[2], Is.EqualTo("Bed (Garolite): 75 °C  (NylonX preset)"));

			// the material sets no surface temperature ("NC" is not a number): plain bed_temperature
			var noSurface = Printer(D(SettingsKey.bed_temperature, "60"))
				.With(SettingsCascade.Oem, D(SettingsKey.layer_height, "0.15", SettingsKey.first_layer_height, "0.2", SettingsKey.has_heated_bed, "1",
					SettingsKey.has_swappable_bed, "1", SettingsKey.bed_surface, "Garolite", SettingsKey.bed_temperature_garolite, "NC"));
			Assert.That(noSurface.BedTemperatureKey, Is.EqualTo(SettingsKey.bed_temperature));
			Assert.That(Lines(noSurface)[2], Is.EqualTo("Bed: 60 °C\n    changed by hand; NylonX preset says 80 °C"));
		}

		[Test]
		public void ConfirmationWithSummaryAndNotice()
		{
			var summary = Lines(Printer(D(SettingsKey.bed_temperature, "60")));
			string notice = "This printer's NylonX preset is not the club's.\nTo update: Printer options > Update club presets.";
			string text = OctoPrintMessages.ConfirmPrintBody("NylonX", summary, notice, Identity);

			Assert.That(text, Is.EqualTo(
				"Is the bed empty and clean?\nIs NylonX loaded?\n\n"
				+ string.Join("\n", summary)
				+ "\n\n" + notice
				+ "\n\nThe printer will heat, home, and probe the bed,\nthen print."));

			Assert.That(OctoPrintMessages.ConfirmPrintBody(null, summary, null, Identity), Does.Contain("\nIs the right material loaded?\n\nMaterial: NylonX\n"));
			Assert.That(OctoPrintMessages.ConfirmPrintBody("NylonX", summary, "  ", Identity), Does.Not.Contain("\n\n\n"));
		}

		/// <summary>The longest realistic "Ready to print?": every summary value changed by hand, a surface bed, a stale preset.</summary>
		public static string WorstCaseConfirmation()
		{
			var cascade = new SettingsCascade() { MaterialName = "Pro Series Nylon", QualityName = "Standard" }
				.With(SettingsCascade.Material, D(SettingsKey.temperature, "250", SettingsKey.bed_temperature, "55", SettingsKey.bed_temperature_pp, "100"))
				.With(SettingsCascade.Quality, D(SettingsKey.layer_height, "0.15"))
				.With(SettingsCascade.Oem, D(SettingsKey.layer_height, "0.15", SettingsKey.first_layer_height, "0.2", SettingsKey.has_heated_bed, "1",
					SettingsKey.has_swappable_bed, "1", SettingsKey.bed_surface, "Polypropylene"))
				.With(SettingsCascade.Base, D(SettingsKey.create_brim, "0", SettingsKey.brims, "8mm"));
			cascade.With(SettingsCascade.User, D(SettingsKey.temperature, "255", SettingsKey.bed_temperature_pp, "110",
				SettingsKey.layer_height, "0.2", SettingsKey.first_layer_height, "150%", SettingsKey.create_brim, "1", SettingsKey.brims, "10mm"));

			var club = new[] { P("n", "Pro Series Nylon", "temperature", "250") };
			var installed = new[] { P("n", "Pro Series Nylon", "temperature", "245") };
			string notice = ClubPresets.ActivePresetNotice(ClubPresets.Compare(installed, club), "n", Identity);

			return OctoPrintMessages.ConfirmPrintBody(cascade.MaterialName, Lines(cascade), notice, Identity);
		}

		[Test]
		public void WorstCaseConfirmationFitsItsBox()
		{
			string text = WorstCaseConfirmation();
			var rows = text.Split('\n');
			TestContext.Out.WriteLine(text);
			TestContext.Out.WriteLine($"rows {rows.Length}, longest {rows.Max(r => r.Length)}, box {MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize(text)}");

			Assert.That(rows.Max(r => r.Length), Is.LessThanOrEqualTo(60));
			var (width, height) = MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize(text);
			Assert.That(height, Is.LessThan(MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxMaxHeight), "the closing question is not pushed out of the box");
			Assert.That(width, Is.LessThanOrEqualTo(MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxMaxWidth));
		}

		[Test]
		public void MessageBoxSizeGrowsWithTheText()
		{
			Assert.That(MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize("Short."), Is.EqualTo((400, 300)));
			Assert.That(MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize(null), Is.EqualTo((400, 300)));
			Assert.That(MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize(new string('x', 60)).width, Is.EqualTo(60 * 9 + 90));
			Assert.That(MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize(new string('x', 500)), Is.EqualTo((640, 9 * 17 + 170)), "wrapped at 61 characters a row");
			Assert.That(MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize(string.Join("\n", Enumerable.Repeat("row", 40))), Is.EqualTo((400, 640)));
			Assert.That(MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize(string.Join("\n", Enumerable.Repeat("row", 20))).height, Is.EqualTo(20 * 17 + 170));
		}

		[Test]
		public void BothBoxesAreSizedToTheirText()
		{
			string sender = File.ReadAllText(Path.Combine(root, "MatterControlLib/OctoPrint/OctoPrintSender.cs"));
			Assert.That(sender, Does.Contain("ClubDialogRules.MessageBoxSize(message)"));
			string action = File.ReadAllText(Path.Combine(root, "MatterControlLib/SettingsManagement/ClubPresetsAction.cs"));
			Assert.That(action, Does.Contain("ClubDialogRules.MessageBoxSize(message)"));
			Assert.That(action, Does.Not.Contain("StyledMessageBox.ShowMessageBox("), "every box of the action goes through the sized Show");
		}

		[Test]
		public void UpdateIsRefusedWhileBusy()
		{
			// a local print, a Send to OctoPrint (its busy lock) or a slice for this printer
			string action = File.ReadAllText(Path.Combine(root, "MatterControlLib/SettingsManagement/ClubPresetsAction.cs"));
			Assert.That(action, Does.Contain("printer.Connection.Printing"));
			Assert.That(action, Does.Contain("OctoPrintSender.IsBusy"));
			Assert.That(action, Does.Contain("printer.ViewState.SlicingItem"));
			string sender = File.ReadAllText(Path.Combine(root, "MatterControlLib/OctoPrint/OctoPrintSender.cs"));
			Assert.That(sender, Does.Contain("public static bool IsBusy => Volatile.Read(ref busy) != 0;"));

			// checked when the action opens and again when the update is confirmed
			Assert.That(Regex.Matches(action, @"Busy\(printer\) is string busy").Count, Is.EqualTo(2));
		}

		[Test]
		public void GCodeFilesAndCommandPartsKeepTheGenericQuestion()
		{
			// the summary is only built when the material's values are what will be printed
			string sender = File.ReadAllText(Path.Combine(root, "MatterControlLib/OctoPrint/OctoPrintSender.cs"));
			int method = sender.IndexOf("private static string ConfirmationText(PrinterConfig printer)", StringComparison.Ordinal);
			int generic = sender.IndexOf("return OctoPrintMessages.ConfirmPrintBodyGeneric(", method, StringComparison.Ordinal);
			int summary = sender.IndexOf("PrintSummary.Build(", method, StringComparison.Ordinal);
			Assert.That(method, Is.GreaterThan(0));
			Assert.That(generic, Is.GreaterThan(method));
			Assert.That(summary, Is.GreaterThan(generic));
			Assert.That(OctoPrintMessages.UseGenericConfirmation(isGCodeSource: true, hasCommandParts: false), Is.True);
			Assert.That(OctoPrintMessages.ConfirmPrintBodyGeneric(Identity), Does.Not.Match("[0-9]"));
		}

		// ---- comparison ----

		private static MaterialPreset P(string id, string name, params string[] keyValues)
		{
			var values = D(keyValues);
			values["layer_id"] = id;
			values["layer_name"] = name;
			return new MaterialPreset(values);
		}

		private static readonly MaterialPreset[] Club =
		{
			P("x", "NylonX", "temperature", "265", "bed_temperature", "80"),
			P("g", "NylonG", "temperature", "260", "bed_temperature", "80"),
		};

		[Test]
		public void IdenticalPresetsHaveNoDifferences()
		{
			var installed = new[]
			{
				P("x", "NylonX", "temperature", "265", "bed_temperature", "80", "layer_etag", "abc"),
				P("g", "NylonG", "temperature", "260", "bed_temperature", "80"),
			};
			var comparison = ClubPresets.Compare(installed, Club);
			Assert.That(comparison.HasDifferences, Is.False);
			Assert.That(comparison.Same.Select(d => d.Club.Name), Is.EqualTo(new[] { "NylonX", "NylonG" }));
			Assert.That(ClubPresets.Describe(comparison, null, Identity), Is.EqualTo("This printer's club presets match the club's\ncurrent ones. Nothing to update."));
		}

		[Test]
		public void ChangedAndMissingKeysAreListedWithBothValues()
		{
			var installed = new[]
			{
				P("x", "NylonX", "temperature", "265", "bed_temperature", "60", "max_fan_speed", "50"),
				P("g", "NylonG", "bed_temperature", "80"),
			};
			var comparison = ClubPresets.Compare(installed, Club);

			Assert.That(comparison.Changed, Has.Count.EqualTo(2));
			var x = comparison.Changed[0];
			Assert.That(x.Club.Name, Is.EqualTo("NylonX"));
			Assert.That(x.MatchedByName, Is.False);
			Assert.That(x.Changes.Select(c => (c.Key, c.InstalledValue, c.ClubValue)), Is.EqualTo(new[]
			{
				("bed_temperature", "60", "80"),
				("max_fan_speed", "50", (string)null),
			}));
			Assert.That(comparison.Changed[1].Changes.Select(c => (c.Key, c.InstalledValue, c.ClubValue)), Is.EqualTo(new[]
			{
				("temperature", (string)null, "260"),
			}));

			var labels = new Dictionary<string, string> { ["bed_temperature"] = "Bed Temperature", ["temperature"] = "Extruder Temperature", ["max_fan_speed"] = "Maximum Fan Speed" };
			Assert.That(ClubPresets.Describe(comparison, k => labels[k], Identity), Is.EqualTo(
				"These club presets differ from the club's:"
				+ "\n\nNylonX:"
				+ "\n  Bed Temperature: 60 here; club's is 80"
				+ "\n  Maximum Fan Speed: 50 here; not in the club's"
				+ "\n\nNylonG:"
				+ "\n  Extruder Temperature: not set here; club's is 260"
				+ "\n\nValues marked \"here\" are replaced by the club's;"
				+ "\nthe backup keeps the old ones."
				+ "\nYour hand overrides on the printer (the"
				+ "\nhighlighted settings) are not changed."
				+ "\nPresets that are not club presets are not changed."));

			Assert.That(ClubPresets.ActivePresetNotice(comparison, "x", Identity),
				Is.EqualTo("This printer's NylonX preset is not the club's.\nTo update: Printer options > Update club presets."));
			Assert.That(ClubPresets.ActivePresetNotice(comparison, "other", Identity), Is.Null);
			Assert.That(ClubPresets.ActivePresetNotice(comparison, null, Identity), Is.Null);
		}

		[Test]
		public void DescribeSaysAPresetMatchedByNameIsReplacedToo()
		{
			var comparison = ClubPresets.Compare(new[] { P("mine", "nylonx", "temperature", "265", "bed_temperature", "60"), Club[1] }, Club);
			string text = ClubPresets.Describe(comparison, null, Identity);
			Assert.That(text, Does.Contain("\nNylonX (matched by name):\n"));
			Assert.That(text, Does.Contain("\nA preset \"matched by name\" is this printer's own\npreset with that name; it is replaced too.\n"));

			// only presets added: nothing is replaced
			string added = ClubPresets.Describe(ClubPresets.Compare(new[] { Club[0] }, Club), null, Identity);
			Assert.That(added, Does.Not.Contain("replaced"));
			Assert.That(added, Does.Contain("\nA backup of the printer profile is saved first.\n"));
		}

		[Test]
		public void ActivePresetNoticeFromTheAppsLayers()
		{
			// the app passes PrinterSettings.MaterialLayers (key/value dictionaries) through FromLayers
			var layers = new List<Dictionary<string, string>>
			{
				D("layer_id", "x", "layer_name", "NylonX", "temperature", "265", "bed_temperature", "60"),
				D("layer_id", "g", "layer_name", "NylonG", "temperature", "260", "bed_temperature", "80"),
				D("layer_id", "mine", "layer_name", "My PETG", "temperature", "240"),
			};
			var installed = ClubPresets.FromLayers(layers);
			Assert.That(installed.Select(p => (p.Id, p.Name)), Is.EqualTo(new[] { ("x", "NylonX"), ("g", "NylonG"), ("mine", "My PETG") }));

			var comparison = ClubPresets.Compare(installed, Club);
			Assert.That(ClubPresets.ActivePresetNotice(comparison, "x", Identity), Does.StartWith("This printer's NylonX preset is not the club's."));
			Assert.That(ClubPresets.ActivePresetNotice(comparison, "g", Identity), Is.Null, "NylonG matches");
			Assert.That(ClubPresets.ActivePresetNotice(comparison, "mine", Identity), Is.Null, "not a club preset");
			Assert.That(ClubPresets.FromLayers(null), Is.Empty);
		}

		// ---- hand overrides that still win over the updated preset ----

		[Test]
		public void HiddenOverridesOfTheActivePreset()
		{
			var installed = new[] { P("x", "NylonX", "temperature", "265", "bed_temperature", "60"), Club[1] };
			var comparison = ClubPresets.Compare(installed, Club);
			var user = D("bed_temperature", "60", "temperature", "265.0", "layer_height", "0.2", "printer_name", "Pulse XE");

			var hidden = ClubPresets.HiddenOverrides(comparison, user, "x");
			Assert.That(hidden.Select(h => (h.Key, h.UserValue, h.ClubValue, h.PresetName)), Is.EqualTo(new[] { ("bed_temperature", "60", "80", "NylonX") }),
				"only keys the preset sets, and only where the hand value differs (265.0 is 265)");

			Assert.That(ClubPresets.DescribeHiddenOverrides(hidden, null, Identity, done: false), Is.EqualTo(
				"These hand overrides will still win over the preset:"
				+ "\nBed stays 60 °C: changed by hand on this printer;"
				+ "\n    the NylonX preset will say 80 °C."
				+ "\nClear them in the settings to use the preset."));
			Assert.That(ClubPresets.DescribeHiddenOverrides(hidden, null, Identity, done: true), Is.EqualTo(
				"These hand overrides still win over the preset:"
				+ "\nBed stays 60 °C: changed by hand on this printer;"
				+ "\n    the NylonX preset now says 80 °C."
				+ "\nClear them in the settings to use the preset."));

			// in the confirmation, before the closing lines
			string text = ClubPresets.Describe(comparison, null, Identity, hidden);
			Assert.That(text, Does.Contain("club's is 80\n\nThese hand overrides will still win over the preset:\nBed stays 60 °C"));

			// not the active material, an unchanged active preset, no active material, no overrides: nothing
			Assert.That(ClubPresets.HiddenOverrides(comparison, user, "g"), Is.Empty);
			Assert.That(ClubPresets.HiddenOverrides(comparison, user, "mine"), Is.Empty);
			Assert.That(ClubPresets.HiddenOverrides(comparison, user, null), Is.Empty);
			Assert.That(ClubPresets.HiddenOverrides(comparison, D("bed_temperature", "80"), "x"), Is.Empty);
			Assert.That(ClubPresets.DescribeHiddenOverrides(new List<HiddenOverride>(), null, Identity, done: true), Is.Null);
		}

		[Test]
		public void HiddenOverridesOfAPresetMatchedByNameUseTheInstalledId()
		{
			var comparison = ClubPresets.Compare(new[] { P("old", "NylonX", "temperature", "255", "bed_temperature", "80") }, Club);
			var hidden = ClubPresets.HiddenOverrides(comparison, D("temperature", "250"), "old");
			Assert.That(ClubPresets.DescribeHiddenOverrides(hidden, null, Identity, done: true), Does.Contain("\nNozzle stays 250 °C: changed by hand on this printer;\n    the NylonX preset now says 265 °C."));
		}

		[Test]
		public void MissingClubPresetIsAdded()
		{
			var comparison = ClubPresets.Compare(new[] { Club[0] }, Club);
			Assert.That(comparison.HasDifferences, Is.True);
			Assert.That(comparison.Changed, Is.Empty);
			Assert.That(comparison.Added.Select(p => p.Name), Is.EqualTo(new[] { "NylonG" }));
			Assert.That(ClubPresets.Describe(comparison, null, Identity), Does.Contain("\nNylonG: new preset (nozzle 260 °C, bed 80 °C)\n"));
		}

		[Test]
		public void ExtraUserPresetsAreLeftAlone()
		{
			var mine = P("mine", "My PETG", "temperature", "240");
			var comparison = ClubPresets.Compare(new[] { Club[0], Club[1], mine }, Club);
			Assert.That(comparison.HasDifferences, Is.False);
			Assert.That(comparison.Other, Is.EqualTo(new[] { mine }));
		}

		[Test]
		public void PresetWithAnotherIdIsMatchedByName()
		{
			var installed = new[] { P("old-id", "nylonx", "temperature", "265", "bed_temperature", "60"), Club[1] };
			var comparison = ClubPresets.Compare(installed, Club);
			Assert.That(comparison.Added, Is.Empty);
			Assert.That(comparison.Changed.Single().MatchedByName, Is.True);
			Assert.That(comparison.Changed.Single().Changes.Select(c => c.Key), Is.EqualTo(new[] { "bed_temperature", "layer_name" }));
			Assert.That(ClubPresets.Describe(comparison, null, Identity), Does.Contain("\nNylonX (matched by name):\n  bed_temperature: 60 here; club's is 80\n  Name: nylonx here; club's is NylonX\n"));

			// a preset whose id is a club id is never taken by name for another club preset
			var swapped = ClubPresets.Compare(new[] { P("g", "NylonX", "temperature", "260", "bed_temperature", "80") }, Club);
			Assert.That(swapped.Added.Select(p => p.Name), Is.EqualTo(new[] { "NylonX" }));
		}

		// ---- update ----

		/// <summary>
		/// An installed profile as an older copy would be: the bundled profile with NylonX's bed at 60 and no NylonG,
		/// hand overrides, and a preset of the printer's own.
		/// </summary>
		private string WriteOldProfile()
		{
			var json = JsonNode.Parse(ClubJson).AsObject();
			var layers = json["MaterialLayers"].AsArray();
			layers.Remove(layers.Single(l => (string)l["layer_name"] == "NylonG"));
			layers.Single(l => (string)l["layer_name"] == "NylonX")["bed_temperature"] = "60";
			layers.Add(new JsonObject { ["layer_name"] = "My PETG", ["layer_id"] = "11111111-2222-3333-4444-555555555555", ["temperature"] = "240" });
			json["UserLayer"] = new JsonObject
			{
				["printer_name"] = "Pulse XE",
				["bed_temperature"] = "80",
				["layer_height"] = "0.15",
				["active_material_key"] = NylonXId,
			};
			json["ID"] = "test-printer";

			string path = Path.Combine(tempFolder, "test-printer.printer");
			// written as the app writes it (indented, quotes escaped as \" and nothing else escaped beyond what JSON needs)
			File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
			return path;
		}

		private static string Raw(string json, Func<JsonElement, JsonElement> pick)
		{
			using (var document = JsonDocument.Parse(json))
			{
				return pick(document.RootElement).GetRawText();
			}
		}

		private static JsonElement Layer(JsonElement root, string name) =>
			root.GetProperty("MaterialLayers").EnumerateArray().Single(l => l.GetProperty("layer_name").GetString() == name);

		[Test]
		public void UpdateReplacesOnlyClubPresetsAndWritesABackup()
		{
			string path = WriteOldProfile();
			string before = File.ReadAllText(path);

			var result = ClubPresets.UpdateFile(path, ClubJson, new DateTime(2026, 11, 19, 9, 5, 0));

			Assert.That(result.BackupPath, Is.EqualTo(path + ".bak-2026-11-19-0905"));
			Assert.That(File.ReadAllText(result.BackupPath), Is.EqualTo(before), "the backup is the old file");
			Assert.That(result.Comparison.Changed.Select(d => d.Club.Name), Is.EqualTo(new[] { "NylonX" }));
			Assert.That(result.Comparison.Added.Select(p => p.Name), Is.EqualTo(new[] { "NylonG" }));
			Assert.That(File.Exists(path + ".updating"), Is.False);

			string after = File.ReadAllText(path);

			// hand overrides, the printer's own preset and everything outside MaterialLayers: unchanged, byte for byte
			Assert.That(Raw(after, r => r.GetProperty("UserLayer")), Is.EqualTo(Raw(before, r => r.GetProperty("UserLayer"))));
			Assert.That(Raw(after, r => Layer(r, "My PETG")), Is.EqualTo(Raw(before, r => Layer(r, "My PETG"))));
			foreach (string property in new[] { "OemLayer", "QualityLayers", "StagedUserSettings", "Macros", "ID", "DocumentVersion" })
			{
				Assert.That(Raw(after, r => r.GetProperty(property)), Is.EqualTo(Raw(before, r => r.GetProperty(property))), property);
			}

			// the club presets are now the club's, in the same place, NylonG added at the end
			var presets = ClubPresets.ReadMaterialPresets(after);
			Assert.That(presets.Single(p => p.Name == "NylonX").Values["bed_temperature"], Is.EqualTo("80"));
			Assert.That(presets.Single(p => p.Name == "NylonG").Id, Is.EqualTo(NylonGId));
			Assert.That(presets.Select(p => p.Name).ToList().IndexOf("NylonX"), Is.EqualTo(ClubPresets.ReadMaterialPresets(before).Select(p => p.Name).ToList().IndexOf("NylonX")));
			Assert.That(presets.Last().Name, Is.EqualTo("NylonG"));
			Assert.That(ClubPresets.Compare(presets, ClubPresets.ReadMaterialPresets(ClubJson)).HasDifferences, Is.False);

			// a second run finds nothing to do and writes nothing
			var again = ClubPresets.UpdateFile(path, ClubJson, new DateTime(2026, 11, 19, 9, 6, 0));
			Assert.That(again.BackupPath, Is.Null);
			Assert.That(File.ReadAllText(path), Is.EqualTo(after));
			Assert.That(Directory.GetFiles(tempFolder).Length, Is.EqualTo(2));
		}

		[Test]
		public void UpdateKeepsTheLayerIdOfAPresetMatchedByName()
		{
			string path = WriteOldProfile();
			var json = JsonNode.Parse(File.ReadAllText(path)).AsObject();
			json["MaterialLayers"].AsArray().Single(l => (string)l["layer_name"] == "NylonX")["layer_id"] = "aaaaaaaa-0000-0000-0000-000000000000";
			File.WriteAllText(path, json.ToJsonString());

			var result = ClubPresets.UpdateFile(path, ClubJson, DateTime.Now);
			Assert.That(result.Comparison.Changed.Single().MatchedByName, Is.True);

			var nylonX = ClubPresets.ReadMaterialPresets(File.ReadAllText(path)).Single(p => p.Name == "NylonX");
			Assert.That(nylonX.Id, Is.EqualTo("aaaaaaaa-0000-0000-0000-000000000000"));
			Assert.That(nylonX.Values["bed_temperature"], Is.EqualTo("80"));
		}

		[Test]
		public void ApplyToAProfileWithoutMaterialLayers()
		{
			var json = JsonNode.Parse(ClubJson).AsObject();
			json.Remove("MaterialLayers");
			string updated = ClubPresets.ApplyToJson(json.ToJsonString(), ClubJson);

			var presets = ClubPresets.ReadMaterialPresets(updated);
			Assert.That(presets.Select(p => p.Id), Is.EqualTo(ClubPresets.ReadMaterialPresets(ClubJson).Select(p => p.Id)));
			Assert.That(Raw(updated, r => r.GetProperty("OemLayer")), Is.EqualTo(Raw(ClubJson, r => r.GetProperty("OemLayer"))));
		}

		[Test]
		public void ApplyToAPresetWithoutLayerId()
		{
			var json = JsonNode.Parse(ClubJson).AsObject();
			var nylonX = json["MaterialLayers"].AsArray().Single(l => (string)l["layer_name"] == "NylonX").AsObject();
			nylonX.Remove("layer_id");
			nylonX["bed_temperature"] = "60";

			var comparison = ClubPresets.Compare(ClubPresets.ReadMaterialPresets(json.ToJsonString()), ClubPresets.ReadMaterialPresets(ClubJson));
			Assert.That(comparison.Changed.Single().MatchedByName, Is.True);

			// matched by name; with no id of its own it takes the club's (never an empty layer_id)
			var after = ClubPresets.ReadMaterialPresets(ClubPresets.ApplyToJson(json.ToJsonString(), ClubJson)).Single(p => p.Name == "NylonX");
			Assert.That(after.Id, Is.EqualTo(NylonXId));
			Assert.That(after.Values["bed_temperature"], Is.EqualTo("80"));
		}

		[Test]
		public void ApplyWithADuplicateLayerId()
		{
			// two installed presets with NylonX's id: the first is the club's, the second is left exactly as it is
			var json = JsonNode.Parse(ClubJson).AsObject();
			var layers = json["MaterialLayers"].AsArray();
			layers.Single(l => (string)l["layer_name"] == "NylonX")["bed_temperature"] = "60";
			layers.Add(new JsonObject { ["layer_name"] = "NylonX copy", ["layer_id"] = NylonXId, ["bed_temperature"] = "50" });
			string before = json.ToJsonString(new JsonSerializerOptions() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

			var comparison = ClubPresets.Compare(ClubPresets.ReadMaterialPresets(before), ClubPresets.ReadMaterialPresets(ClubJson));
			Assert.That(comparison.Other.Select(p => p.Name), Is.EqualTo(new[] { "NylonX copy" }));

			string after = ClubPresets.ApplyToJson(before, ClubJson);
			Assert.That(Raw(after, r => Layer(r, "NylonX copy")), Is.EqualTo(Raw(before, r => Layer(r, "NylonX copy"))));
			Assert.That(ClubPresets.ReadMaterialPresets(after).Single(p => p.Name == "NylonX").Values["bed_temperature"], Is.EqualTo("80"));
		}

		[Test]
		public void UpdateRefusesAPrinterThatIsNotTheClubs()
		{
			string path = Path.Combine(tempFolder, "stock.printer");
			File.Copy(Path.Combine(root, "StaticData/Profiles/Pulse/S-232.printer"), path);
			string before = File.ReadAllText(path);

			Assert.Throws<InvalidOperationException>(() => ClubPresets.UpdateFile(path, ClubJson, DateTime.Now));
			Assert.That(File.ReadAllText(path), Is.EqualTo(before));
			Assert.That(Directory.GetFiles(tempFolder).Length, Is.EqualTo(1), "no backup");
		}

		[Test]
		public void BackupNameNeverOverwrites()
		{
			var taken = new HashSet<string> { "p.printer.bak-2026-11-19-0905", "p.printer.bak-2026-11-19-0905-2" };
			Assert.That(ClubPresets.BackupPath("p.printer", new DateTime(2026, 11, 19, 9, 5, 59), taken.Contains), Is.EqualTo("p.printer.bak-2026-11-19-0905-3"));
			Assert.That(ClubPresets.BackupPath("p.printer", new DateTime(2026, 1, 2, 23, 7, 0), taken.Contains), Is.EqualTo("p.printer.bak-2026-01-02-2307"));
		}
	}
}
