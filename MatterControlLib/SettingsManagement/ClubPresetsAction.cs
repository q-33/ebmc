using System;
using System.IO;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.SlicerConfiguration;

namespace MatterHackers.MatterControl.SettingsManagement
{
	/// <summary>
	/// EBMC Edition: "Update club presets…" in Printer options, and the one-line notice in the pre-print summary. The
	/// comparison, the wording and the file update are in <see cref="ClubPresets"/> (MatterControl.Printing, unit
	/// tested); this only reads the bundled club profile, asks, and reloads the printer. Nothing happens at start-up:
	/// the update runs only when chosen and confirmed.
	/// </summary>
	public static class ClubPresetsAction
	{
		/// <summary>The printer is the club printer (by its profile's own make and model) and the app has the club profile.</summary>
		public static bool Applies(PrinterConfig printer)
		{
			var oem = printer?.Settings?.OemLayer;
			return oem != null
				&& ClubPresets.IsClubPrinter(oem.ValueOrDefault(SettingsKey.make), oem.ValueOrDefault(SettingsKey.model))
				&& StaticData.Instance.FileExists(ClubPresets.BundledProfilePath);
		}

		/// <summary>
		/// One line for "Ready to print?" when the active material is a club preset that differs from the club's current
		/// one; null otherwise, and null (never an error) if anything cannot be read.
		/// </summary>
		public static string ActivePresetNotice(PrinterConfig printer)
		{
			try
			{
				if (!Applies(printer))
				{
					return null;
				}

				var installed = printer.Settings.MaterialLayers.Select(layer => new MaterialPreset(layer.ToDictionary(p => p.Key, p => p.Value)));
				var club = ClubPresets.ReadMaterialPresets(StaticData.Instance.ReadAllText(ClubPresets.BundledProfilePath));

				return ClubPresets.ActivePresetNotice(
					ClubPresets.Compare(installed, club),
					printer.Settings.MaterialLayer?.ValueOrDefault(ClubPresets.LayerIdKey),
					s => s.Localize());
			}
			catch
			{
				return null;
			}
		}

		public static void Run(PrinterConfig printer)
		{
			string title = "Update club presets".Localize();

			if (!Applies(printer))
			{
				return;
			}

			if (printer.Connection.Printing)
			{
				StyledMessageBox.ShowMessageBox("Wait until the print has finished, then update the presets.".Localize(), title);
				return;
			}

			string profilePath = ProfileManager.Instance.ProfilePath(printer.Settings.ID);
			if (profilePath == null || !File.Exists(profilePath))
			{
				StyledMessageBox.ShowMessageBox("This printer's profile file could not be found. Nothing was changed.".Localize(), title);
				return;
			}

			ClubPresetComparison comparison;
			string clubJson;
			try
			{
				// the file is what gets updated: make sure it holds what the app has in memory
				printer.Settings.Save(userDrivenChange: false);

				clubJson = StaticData.Instance.ReadAllText(ClubPresets.BundledProfilePath);
				comparison = ClubPresets.Compare(
					ClubPresets.ReadMaterialPresets(File.ReadAllText(profilePath)),
					ClubPresets.ReadMaterialPresets(clubJson));
			}
			catch (Exception e)
			{
				StyledMessageBox.ShowMessageBox("The presets could not be compared. Nothing was changed.".Localize() + "\n\n" + e.Message, title);
				return;
			}

			string text = ClubPresets.Describe(comparison, KeyLabel, s => s.Localize());
			if (!comparison.HasDifferences)
			{
				StyledMessageBox.ShowMessageBox(text, title);
				return;
			}

			StyledMessageBox.ShowMessageBox(
				(update) =>
				{
					if (update)
					{
						UiThread.RunOnIdle(() => Update(printer, profilePath, clubJson, title));
					}
				},
				text,
				title,
				StyledMessageBox.MessageType.YES_NO,
				"Update presets".Localize(),
				"Cancel".Localize(),
				new ClubMessageOptions()
				{
					YesLabel = "Update presets".Localize(),
					NoLabel = "Cancel".Localize(),
				});
		}

		private static void Update(PrinterConfig printer, string profilePath, string clubJson, string title)
		{
			ClubPresetUpdate result;
			try
			{
				printer.Settings.Save(userDrivenChange: false);
				result = ClubPresets.UpdateFile(profilePath, clubJson, DateTime.Now);
			}
			catch (Exception e)
			{
				StyledMessageBox.ShowMessageBox("The presets could not be updated. The profile was not changed.".Localize() + "\n\n" + e.Message, title);
				return;
			}

			if (result.BackupPath == null)
			{
				StyledMessageBox.ShowMessageBox(ClubPresets.Describe(result.Comparison, KeyLabel, s => s.Localize()), title);
				return;
			}

			var updated = PrinterSettings.LoadFile(profilePath);
			if (updated == null)
			{
				// should not happen; put the backup back rather than leave a profile the app cannot read
				File.Copy(result.BackupPath, profilePath, overwrite: true);
				StyledMessageBox.ShowMessageBox("The updated profile could not be read, so the backup was put back. Nothing was changed.".Localize(), title);
				return;
			}

			// load the updated file into the open printer (this reloads the printer's views) and save it in the app's format
			printer.SwapToSettings(updated);
			printer.Settings.Save(userDrivenChange: false);

			int count = result.Comparison.Changed.Count + result.Comparison.Added.Count;
			StyledMessageBox.ShowMessageBox(
				string.Format("Club presets updated or added: {0}.".Localize(), count)
					+ "\n\n" + "Backup of the previous profile:".Localize() + "\n" + Path.GetFileName(result.BackupPath),
				title);
		}

		private static string KeyLabel(string key)
		{
			return PrinterSettings.SettingsData.TryGetValue(key, out SliceSettingData data) && !string.IsNullOrWhiteSpace(data.PresentationName)
				? data.PresentationName
				: key;
		}
	}
}
