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
				"Nozzle: 255 °C  (changed by hand; NylonX preset says 265 °C)",
				"Bed: 60 °C  (changed by hand; NylonX preset says 80 °C)",
				"Layer height: 0.2 mm  (changed by hand; Standard quality says 0.15 mm)",
				"First layer: 0.2 mm  (printer profile)",
				"Brim: on, 3 loops  (changed by hand; default says off)",
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
			var lines = Lines(Printer(D(SettingsKey.bed_temperature, "80.0")));
			Assert.That(lines[2], Is.EqualTo("Bed: 80 °C  (NylonX preset)"));
		}

		[Test]
		public void SummaryBrimLoopsChangedByHand()
		{
			var cascade = Printer(D(SettingsKey.brims, "5"))
				.With(SettingsCascade.Oem, D(SettingsKey.layer_height, "0.15", SettingsKey.first_layer_height, "0.2", SettingsKey.has_heated_bed, "1",
					SettingsKey.create_brim, "1", SettingsKey.brims, "2"));
			Assert.That(Lines(cascade).Last(), Is.EqualTo("Brim: on, 5 loops  (changed by hand; printer profile says on, 2 loops)"));

			// loops changed by hand while the brim is off: nothing to report
			Assert.That(Lines(Printer(D(SettingsKey.brims, "5"))).Last(), Is.EqualTo("Brim: off  (default)"));

			// a brim given in mm
			Assert.That(Lines(Printer(D(SettingsKey.create_brim, "1"))).Last(), Is.EqualTo("Brim: on, 8 mm wide  (changed by hand; default says off)"));
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
				"Nozzle: 230 °C  (changed by hand; default says 200 °C)",
				"Layer height: 0.4 mm  (default)",
				"First layer: 150%  (default)",
				"Brim: off",
			}));
		}

		[Test]
		public void ConfirmationWithSummaryAndNotice()
		{
			var summary = Lines(Printer(D(SettingsKey.bed_temperature, "60")));
			string notice = "This printer's NylonX preset differs from the club's current one (Printer options > Update club presets).";
			string text = OctoPrintMessages.ConfirmPrintBody("NylonX", summary, notice, Identity);

			Assert.That(text, Is.EqualTo(
				"Is the bed empty and clean?\nIs NylonX loaded?\n\n"
				+ string.Join("\n", summary)
				+ "\n\n" + notice
				+ "\n\nThe printer will heat, home, and probe the bed before it prints."));

			Assert.That(OctoPrintMessages.ConfirmPrintBody(null, summary, null, Identity), Does.Contain("\nIs the right material loaded?\n\nMaterial: NylonX\n"));
			Assert.That(OctoPrintMessages.ConfirmPrintBody("NylonX", summary, "  ", Identity), Does.Not.Contain("\n\n\n"));
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
			Assert.That(ClubPresets.Describe(comparison, null, Identity), Is.EqualTo("This printer's club presets match the club's current ones. Nothing to update."));
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
				"This printer's club presets differ from the club's current ones:\n"
				+ "\nNylonX:"
				+ "\n  Bed Temperature: 60 here; club's is 80"
				+ "\n  Maximum Fan Speed: 50 here; not in the club's"
				+ "\n\nNylonG:"
				+ "\n  Extruder Temperature: not set here; club's is 260"
				+ "\n\nA backup of the printer profile is saved first."
				+ "\nHand-changed settings and other presets stay as they are."));

			Assert.That(ClubPresets.ActivePresetNotice(comparison, "x", Identity),
				Is.EqualTo("This printer's NylonX preset differs from the club's current one (Printer options > Update club presets)."));
			Assert.That(ClubPresets.ActivePresetNotice(comparison, "other", Identity), Is.Null);
			Assert.That(ClubPresets.ActivePresetNotice(comparison, null, Identity), Is.Null);
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
