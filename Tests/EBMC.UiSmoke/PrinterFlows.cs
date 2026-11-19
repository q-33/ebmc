using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatterHackers.Agg.UI;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.OctoPrint;
using MatterHackers.MatterControl.SlicerConfiguration;
using static EBMC.UiSmoke.Smoke;

namespace EBMC.UiSmoke
{
	/// <summary>
	/// The flows that put the club-preset update and the "Ready to print?" summary on screen. Both reuse a folder the
	/// club-printer flow made, and change the installed profile file before the app starts, the way an older install
	/// would look.
	/// </summary>
	public static partial class Flows
	{
		private const string NylonX = "NylonX";

		private const string NylonG = "NylonG";

		/// <summary>An obvious throwaway for the mock; never a real key.</summary>
		private const string MockKey = "smoke-mock-key";

		private static OctoPrintMock mock;

		// --- the installed profile file ---

		internal static string ProfileFile()
		{
			var files = Directory.GetFiles(Path.Combine(DataDir, "Profiles"), "*.printer", SearchOption.AllDirectories);
			Require(files.Length == 1, $"expected one printer profile in the scratch folder, found {files.Length}: run the club-printer flow first");
			return files[0];
		}

		internal static JsonObject ReadProfile(string path) => JsonNode.Parse(File.ReadAllText(path)).AsObject();

		private static void WriteProfile(string path, JsonObject profile) =>
			File.WriteAllText(path, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		private static JsonObject Preset(JsonObject profile, string name) =>
			profile["MaterialLayers"].AsArray().OfType<JsonObject>().FirstOrDefault(m => (string)m["layer_name"] == name);

		internal static string Value(JsonObject layer, string key) => layer?[key]?.GetValue<string>();

		/// <summary>Makes the installed profile an older one: NylonX with bed 60 and (optionally) no NylonG; hand overrides in the User layer.</summary>
		private static void MakeProfileStale(bool removeNylonG, IDictionary<string, string> userLayer)
		{
			string path = ProfileFile();
			var profile = ReadProfile(path);

			var nylonX = Preset(profile, NylonX);
			Require(nylonX != null, "the installed profile has no NylonX preset");
			Require(Value(profile["OemLayer"].AsObject(), "active_material_key") == Value(nylonX, "layer_id"), "NylonX is not the active material in the installed profile");
			nylonX["bed_temperature"] = "60";

			if (removeNylonG)
			{
				var nylonG = Preset(profile, NylonG);
				Require(nylonG != null, "the installed profile has no NylonG preset to remove");
				profile["MaterialLayers"].AsArray().Remove(nylonG);
			}

			var user = profile["UserLayer"] as JsonObject ?? new JsonObject();
			profile["UserLayer"] = user;
			foreach (var pair in userLayer)
			{
				user[pair.Key] = pair.Value;
			}

			WriteProfile(path, profile);
			Console.WriteLine($"    before start: {Path.GetFileName(path)}: NylonX bed 60{(removeNylonG ? ", NylonG removed" : "")}; User layer "
				+ string.Join(", ", userLayer.Select(p => p.Key + "=" + p.Value)));
		}

		private static PrinterConfig WaitForPrinterTab()
		{
			PrinterConfig printer = null;
			Step("printer-tab", () =>
			{
				WaitForWidget("Members Tab", 120, what: "(main window)");
				WaitUntil(() => Ui(() => ApplicationController.Instance.ActivePrinters.Any()), 30, "the club printer did not load");
				printer = Ui(() => ApplicationController.Instance.ActivePrinters.First());
				if (!IsShown("Generate Gcode Button"))
				{
					// the printer's tab was not reopened: open it from the Printers tab
					Click("Hardware Tab");
					var node = WaitForText(Ui(() => printer.Settings.GetValue(SettingsKey.printer_name)), 10);
					Runner.ClickWidget(node);
					Runner.ClickWidget(node, isDoubleClick: true);
				}

				WaitForWidget("Generate Gcode Button", 30, what: "(the printer tab)");
				WaitUntil(() => SystemWindow.AllOpenSystemWindows.Count() == 1, 15, "a dialog is open over the printer tab");
				Log($"material: {Ui(() => printer.Settings.MaterialLayer?.Name)}, bed (as sliced): {Ui(() => printer.Settings.GetValue(SettingsKey.bed_temperature))}");
			});

			return printer;
		}

		/// <summary>Opens Printer options and clicks the "Update club presets" item.</summary>
		private static void ChooseUpdateClubPresets()
		{
			Click("Printer Overflow Menu");
			GuiWidget item = null;
			WaitUntil(() =>
			{
				item = Ui(() => SystemWindow.AllOpenSystemWindows.Reverse()
					.SelectMany(w => w.Descendants<GuiWidget>())
					.FirstOrDefault(w => w.Name != null && w.Name.StartsWith("Update club presets") && w.Name.EndsWith("Menu Item") && w.ActuallyVisibleOnScreen()));
				return item != null;
			}, 10, "Printer options has no \"Update club presets\" item");
			Log($"menu item: \"{Ui(() => item.Name)}\", enabled: {Ui(() => item.Enabled)}");
			Require(Ui(() => item.Enabled), "\"Update club presets\" is disabled");
			ClickWidget(item);
		}

		private static void RequireRow(MessageBoxReport box, string row)
		{
			var rows = box.Message.Replace("\r", "").Split('\n');
			Require(rows.Any(r => r.Trim() == row.Trim()), $"the text has no row \"{row}\"");
		}

		private static void RequireRowContaining(MessageBoxReport box, string part)
		{
			var rows = box.Message.Replace("\r", "").Split('\n');
			Require(rows.Any(r => r.Contains(part)), $"the text has no row containing \"{part}\"");
		}

		// --- update-presets ---

		private static void StaleForUpdate() => MakeProfileStale(
			removeNylonG: true,
			new Dictionary<string, string> { ["bed_temperature"] = "60" });

		private static void UpdatePresetsFlow()
		{
			string profilePath = ProfileFile();
			var printer = WaitForPrinterTab();

			Step("confirmation", () =>
			{
				ChooseUpdateClubPresets();
				var box = MessageBoxes.WaitFor("Update club presets", 15);
				Log("title: " + box.Title);
				RequireRow(box, "These club presets differ from the club's:");
				RequireRow(box, "NylonX:");
				RequireRowContaining(box, "60 here; club's is 80");
				RequireRow(box, "NylonG: new preset (nozzle 260 °C, bed 80 °C)");
				RequireRow(box, "These hand overrides will still win over the preset:");
				RequireRow(box, "Bed stays 60 °C: changed by hand on this printer;");
				RequireRow(box, "the NylonX preset will say 80 °C.");
				MessageBoxes.RequireFits(box);
			});

			Step("done-message", () =>
			{
				Click("Yes Button");
				MessageBoxReport box = null;
				WaitUntil(() =>
				{
					box = MessageBoxes.Read();
					return box != null && box.Message.StartsWith("Club presets updated");
				}, 20, "no message after updating");
				System.Threading.Thread.Sleep(500);
				box = MessageBoxes.Read();
				RequireRow(box, "Club presets updated or added: 2.");
				RequireRow(box, "Backup of the previous profile:");
				RequireRowContaining(box, ".printer.bak-");
				RequireRow(box, "These hand overrides still win over the preset:");
				RequireRow(box, "Bed stays 60 °C: changed by hand on this printer;");
				RequireRow(box, "the NylonX preset now says 80 °C.");
				MessageBoxes.RequireFits(box);
			});

			Step("profile-on-disk", () =>
			{
				Click("Ok Button");
				WaitUntil(() => MessageBoxes.Read() == null, 10, "the message did not close");

				var backups = Directory.GetFiles(Path.GetDirectoryName(profilePath), Path.GetFileName(profilePath) + ".bak-*");
				Require(backups.Length == 1, $"expected one backup beside the profile, found {backups.Length}");
				Log("backup: " + Path.GetFileName(backups[0]));
				var old = ReadProfile(backups[0]);
				Require(Value(Preset(old, NylonX), "bed_temperature") == "60", "the backup's NylonX bed is not 60");
				Require(Preset(old, NylonG) == null, "the backup has NylonG (it was removed before the update)");

				var updated = ReadProfile(profilePath);
				Require(Value(Preset(updated, NylonX), "bed_temperature") == "80", $"NylonX bed is {Value(Preset(updated, NylonX), "bed_temperature")}, expected 80");
				var nylonG = Preset(updated, NylonG);
				Require(nylonG != null, "NylonG was not added");
				Require(Value(nylonG, "temperature") == "260" && Value(nylonG, "bed_temperature") == "80",
					$"NylonG is {Value(nylonG, "temperature")}/{Value(nylonG, "bed_temperature")}, expected 260/80");
				Require(Value(updated["UserLayer"] as JsonObject, "bed_temperature") == "60", "the hand bed override of 60 was not kept");
				Log("on disk: NylonX bed 80, NylonG 260/80, User layer bed 60; backup NylonX bed 60, no NylonG");
				Log($"bed as sliced now: {Ui(() => printer.Settings.GetValue(SettingsKey.bed_temperature))} (the hand override)");
			});

			Step("second-run-nothing-to-update", () =>
			{
				ChooseUpdateClubPresets();
				MessageBoxReport box = null;
				WaitUntil(() =>
				{
					box = MessageBoxes.Read();
					return box != null && box.Title == "Update club presets";
				}, 15, "no box on the second run");
				System.Threading.Thread.Sleep(500);
				box = MessageBoxes.Read();
				RequireRow(box, "This printer's club presets match the club's");
				RequireRow(box, "current ones. Nothing to update.");
				Require(box.Buttons.Keys.All(k => k.StartsWith("Ok Button")), "the second run offers more than OK: " + string.Join(", ", box.Buttons.Keys));
				MessageBoxes.RequireFits(box);
			});

			Step("closed", () =>
			{
				Click("Ok Button");
				WaitUntil(() => MessageBoxes.Read() == null, 10, "the message did not close");
				var backups = Directory.GetFiles(Path.GetDirectoryName(profilePath), Path.GetFileName(profilePath) + ".bak-*");
				Require(backups.Length == 1, $"the second run wrote another backup ({backups.Length} now)");
			});
		}

		// --- presets-menu-not-club ---

		/// <summary>
		/// A printer that is not the club printer ("Define New" on the setup page: the bundled Other/Other profile, no
		/// network) has no "Update club presets" item in Printer options.
		/// </summary>
		private static void PresetsMenuNotClubFlow()
		{
			MainWindow();
			Welcome("welcome-setup-printer", () => ClickWidget(ClickableFor(WaitForText("Setup New Printer"))));

			Step("define-new-printer", () =>
			{
				WaitForWidget("AddPrinterWidget", 15);
				var define = WaitForText("Define New", 10);
				ClickWidget(Ui(() => define is ThemedTextButton ? define : define.Parents<ThemedTextButton>().FirstOrDefault() ?? define));
				var done = WaitForWidget("Cancel Wizard Button", 20, w => w.Parents<DialogPage>().FirstOrDefault()?.GetType().Name == "SetupCustomPrinter", "(the custom printer page)");
				ClickWidget(done);
				WaitUntil(() => Ui(() => ApplicationController.Instance.ActivePrinters.Any()), 30, "no printer after Define New");
				WaitForWidget("Printer Overflow Menu", 30, what: "(the new printer's tab)");
				WaitUntil(() => SystemWindow.AllOpenSystemWindows.Count() == 1, 15, "a dialog is open over the printer tab");
				var printer = Ui(() => ApplicationController.Instance.ActivePrinters.First());
				Log($"printer: \"{Ui(() => printer.Settings.GetValue(SettingsKey.printer_name))}\", profile make/model: "
					+ $"{Ui(() => printer.Settings.OemLayer.ValueOrDefault(SettingsKey.make))} / {Ui(() => printer.Settings.OemLayer.ValueOrDefault(SettingsKey.model))}");
				Require(!Ui(() => MatterHackers.MatterControl.SettingsManagement.ClubPresetsAction.Applies(printer)), "ClubPresetsAction.Applies is true for a non-club printer");
			});

			Step("no-update-club-presets-item", () =>
			{
				Click("Printer Overflow Menu");
				List<string> items = null;
				WaitUntil(() =>
				{
					items = Ui(() => SystemWindow.AllOpenSystemWindows.Reverse()
						.SelectMany(w => w.Descendants<GuiWidget>())
						.Where(w => w.Name != null && w.Name.EndsWith(" Menu Item") && w.ActuallyVisibleOnScreen())
						.Select(w => w.Name.Substring(0, w.Name.Length - " Menu Item".Length))
						.Distinct()
						.ToList());
					return items.Count > 0;
				}, 10, "Printer options did not open");
				Log("Printer options: " + string.Join(" | ", items));
				Require(!items.Any(i => i.StartsWith("Update club presets")), "\"Update club presets\" is offered for a printer that is not the club printer");
				Require(items.Any(i => i.StartsWith("Update Settings") || i.StartsWith("Restore Settings")), "the menu does not look like Printer options");
			});
		}

		// --- ready-to-print ---

		/// <summary>Before start: the mock on a free loopback port, and the stale preset with hand overrides pointing at it.</summary>
		private static void StaleForPrint()
		{
			mock = new OctoPrintMock(MockKey, "operational");
			OnExit.Add(mock.Dispose);
			MakeProfileStale(
				removeNylonG: false,
				new Dictionary<string, string>
				{
					["bed_temperature"] = "70",
					["temperature"] = "255",
					["layer_height"] = "0.2",
					["create_brim"] = "1",
					["octoprint_host"] = mock.BaseUrl,
				});
		}

		private static void ReadyToPrintCancelFlow() => ReadyToPrintFlow(confirm: false);

		private static void ReadyToPrintConfirmFlow() => ReadyToPrintFlow(confirm: true);

		private static void ReadyToPrintFlow(bool confirm)
		{
			var printer = WaitForPrinterTab();

			Step("api-key", () =>
			{
				string host = Ui(() => printer.Settings.GetValue(SettingsKey.octoprint_host));
				Require(host == mock.BaseUrl, $"octoprint_host is {host}, expected the mock at {mock.BaseUrl}");
				Require(OctoPrintEndpoint.TryNormalize(host, out Uri baseUri, out string error), error);

				// the app's own store for the key (UserSettings in the scratch database)
				Ui(() => OctoPrintSettings.SetApiKey(baseUri, MockKey));
				Require(Ui(() => OctoPrintSettings.GetApiKey(baseUri)) == MockKey, "the key was not stored");
				int parts = Ui(() => printer.Bed.Scene.Children.Count);
				Log($"OctoPrint host {host} (the mock); key stored in the scratch user settings; parts on the bed: {parts}");
				Require(parts > 0, "the printer's bed is empty");
			});

			Step("ready-to-print", () =>
			{
				Click("OctoPrintSendPopupMenu");
				Click("OctoPrint Upload And Print Button", 15);
				var box = MessageBoxes.WaitFor("Ready to print?", 30);
				RequireRow(box, "Is the bed empty and clean?");
				RequireRow(box, "Is NylonX loaded?");
				RequireRow(box, "Material: NylonX");
				RequireRow(box, "Nozzle: 255 °C");
				RequireRow(box, "    changed by hand; NylonX preset says 265 °C");
				RequireRow(box, "Bed: 70 °C");
				RequireRow(box, "    changed by hand; NylonX preset says 60 °C");
				RequireRow(box, "Layer height: 0.2 mm");
				RequireRowContaining(box, "changed by hand; Standard quality says 0.15 mm");
				RequireRowContaining(box, "First layer: ");
				RequireRowContaining(box, "Brim: on");
				RequireRowContaining(box, "changed by hand; printer profile says off");
				RequireRow(box, "This printer's NylonX preset is not the club's.");
				RequireRow(box, "To update: Printer options > Update club presets.");
				RequireRow(box, "The printer will heat, home, and probe the bed,");
				RequireRow(box, "then print.");
				MessageBoxes.RequireFits(box);
				Log("mock requests so far: " + string.Join(" | ", mock.Requests));
			});

			if (!confirm)
			{
				Step("cancelled-nothing-sent", () =>
				{
					var no = Visible("No Button").First();
					Log($"choosing \"{Ui(() => no.Text)}\"");
					ClickWidget(no);
					WaitUntil(() => MessageBoxes.Read() == null, 10, "the box did not close");
					WaitUntil(() => !OctoPrintSender.IsBusy, 15, "the send task did not end after cancelling");
					System.Threading.Thread.Sleep(1000);
					var requests = mock.Requests;
					Log("mock request log:");
					foreach (var line in requests)
					{
						Log("  " + line);
					}

					Require(!requests.Any(r => r.Contains("POST /api/files")), "the mock received an upload after Cancel");
					Require(!requests.Any(r => r.Contains("POST /api/job")), "the mock received a job command after Cancel");
					string files = mock.Files();
					Log("mock files: " + files);
					Require(files.Contains("\"files\": []") && files.Contains("\"printer\": \"operational\""), "the mock holds files or is printing after Cancel");
				});
			}
			else
			{
				Step("confirmed-one-upload", () =>
				{
					var yes = Visible("Yes Button").First();
					Log($"choosing \"{Ui(() => yes.Text)}\"");
					ClickWidget(yes);

					// the outcome box (OctoPrint, OK and "Open OctoPrint": the test never opens the browser)
					MessageBoxReport box = null;
					WaitUntil(() =>
					{
						box = MessageBoxes.Read();
						return box != null && box.Title != "Ready to print?";
					}, 120, "no outcome after confirming");
					System.Threading.Thread.Sleep(500);
					box = MessageBoxes.Read();
					Log($"outcome box \"{box.Title}\":");
					foreach (var row in box.DisplayedRows)
					{
						Log("  | " + row);
					}

					var requests = mock.Requests;
					Log("mock request log:");
					foreach (var line in requests)
					{
						Log("  " + line);
					}

					var uploads = requests.Where(r => r.Contains("POST /api/files/local")).ToList();
					Require(uploads.Count == 1, $"expected exactly one upload, the mock logged {uploads.Count}");
					Require(uploads[0].Contains("-> 201"), "the upload was not accepted: " + uploads[0]);
					string files = mock.Files();
					Log("mock files: " + files);
					Require(files.Contains("\"printer\": \"printing\""), "the mock is not printing after the upload (print was not requested)");

					// the outcome names the file the mock stored and says it is printing
					using (var json = JsonDocument.Parse(files))
					{
						var stored = json.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
						Require(stored.Count == 1, $"the mock stored {stored.Count} files");
						Require(box.Title == "OctoPrint", $"the outcome box is titled \"{box.Title}\", expected \"OctoPrint\"");
						string expected = $"Printing {stored[0]} on OctoPrint.";
						Require(box.Message.Replace("\n", " ").Replace("  ", " ").Trim() == expected,
							$"the outcome says \"{box.Message.Replace("\n", " | ")}\", expected \"{expected}\"");
						Log("outcome text matches: " + expected);
					}
				});

				Step("outcome-closed", () =>
				{
					Click("Ok Button");
					WaitUntil(() => MessageBoxes.Read() == null, 10, "the outcome box did not close");
				});
			}

			Step("loopback-only", () =>
			{
				var seen = NetworkWatch.AllSeen;
				var outside = NetworkWatch.OutsideSeen;
				Require(seen.Count > 0, "the watch saw no traffic at all, although the app talked to the mock");
				Require(outside.Count == 0, "traffic left loopback: " + string.Join("; ", outside));
				Log($"network: {seen.Count} requests, lookups and connects, every one loopback: " + string.Join("; ", seen.Distinct()));
			});
		}
	}
}
