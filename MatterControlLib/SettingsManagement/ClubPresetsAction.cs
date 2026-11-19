using System;
using System.IO;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.ClubUi;
using MatterHackers.MatterControl.OctoPrint;
using MatterHackers.MatterControl.SlicerConfiguration;

namespace MatterHackers.MatterControl.SettingsManagement
{
	/// <summary>
	/// EBMC Edition: "Update club presets…" in Printer options, and the notice in the pre-print summary. The
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
		/// Two short lines for "Ready to print?" when the active material is a club preset that differs from the club's
		/// current one; null otherwise, and null (never an error) if anything cannot be read.
		/// </summary>
		public static string ActivePresetNotice(PrinterConfig printer)
		{
			try
			{
				if (!Applies(printer))
				{
					return null;
				}

				var club = ClubPresets.ReadMaterialPresets(StaticData.Instance.ReadAllText(ClubPresets.BundledProfilePath));

				return ClubPresets.ActivePresetNotice(
					ClubPresets.Compare(ClubPresets.FromLayers(printer.Settings.MaterialLayers), club),
					printer.Settings.MaterialLayer?.ValueOrDefault(ClubPresets.LayerIdKey),
					s => s.Localize());
			}
			catch
			{
				return null;
			}
		}

		/// <summary>Why the presets cannot be updated right now, or null: a local print, or a Send or slice for this printer.</summary>
		private static string Busy(PrinterConfig printer)
		{
			if (printer.Connection.Printing)
			{
				return "This printer is printing. Update the presets when the print has finished.".Localize();
			}

			if (OctoPrintSender.IsBusy)
			{
				return "A job is being sent to OctoPrint. Update the presets when it has finished.".Localize();
			}

			if (printer.ViewState.SlicingItem)
			{
				return "The bed is being sliced. Update the presets when slicing has finished.".Localize();
			}

			return null;
		}

		public static void Run(PrinterConfig printer)
		{
			string title = "Update club presets".Localize();

			if (!Applies(printer))
			{
				return;
			}

			if (Busy(printer) is string busy)
			{
				Show(null, busy, title, StyledMessageBox.MessageType.OK);
				return;
			}

			string profilePath = ProfileManager.Instance.ProfilePath(printer.Settings.ID);
			if (profilePath == null || !File.Exists(profilePath))
			{
				Show(null, "This printer's profile file could not be found. Nothing was changed.".Localize(), title, StyledMessageBox.MessageType.OK);
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
				Show(null, "The presets could not be compared. Nothing was changed.".Localize() + "\n\n" + e.Message, title, StyledMessageBox.MessageType.OK);
				return;
			}

			string text = ClubPresets.Describe(comparison, KeyLabel, s => s.Localize(), Hidden(printer, comparison));
			if (!comparison.HasDifferences)
			{
				Show(null, text, title, StyledMessageBox.MessageType.OK);
				return;
			}

			Show(
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
				"Update presets".Localize());
		}

		private static void Update(PrinterConfig printer, string profilePath, string clubJson, string title)
		{
			if (Busy(printer) is string busy)
			{
				Show(null, busy, title, StyledMessageBox.MessageType.OK);
				return;
			}

			ClubPresetUpdate result;
			try
			{
				printer.Settings.Save(userDrivenChange: false);
				result = ClubPresets.UpdateFile(profilePath, clubJson, DateTime.Now);
			}
			catch (Exception e)
			{
				Show(null, "The presets could not be updated. The profile was not changed.".Localize() + "\n\n" + e.Message, title, StyledMessageBox.MessageType.OK);
				return;
			}

			if (result.BackupPath == null)
			{
				Show(null, ClubPresets.Describe(result.Comparison, KeyLabel, s => s.Localize()), title, StyledMessageBox.MessageType.OK);
				return;
			}

			var updated = PrinterSettings.LoadFile(profilePath);
			if (updated == null)
			{
				// should not happen; put the backup back rather than leave a profile the app cannot read
				File.Copy(result.BackupPath, profilePath, overwrite: true);
				Show(null, "The updated profile could not be read, so the backup was put back. Nothing was changed.".Localize(), title, StyledMessageBox.MessageType.OK);
				return;
			}

			// the overrides that still win, read before the swap (the User layer and active material are not changed by it)
			var hidden = Hidden(printer, result.Comparison);

			// load the updated file into the open printer (this reloads the printer's views) and save it in the app's format
			printer.SwapToSettings(updated);
			printer.Settings.Save(userDrivenChange: false);

			int count = result.Comparison.Changed.Count + result.Comparison.Added.Count;
			string done = string.Format("Club presets updated or added: {0}.".Localize(), count)
				+ "\n\n" + "Backup of the previous profile:".Localize() + "\n" + Path.GetFileName(result.BackupPath);

			string hiddenText = ClubPresets.DescribeHiddenOverrides(hidden, KeyLabel, s => s.Localize(), done: true);
			if (hiddenText != null)
			{
				done += "\n\n" + hiddenText;
			}

			Show(null, done, title, StyledMessageBox.MessageType.OK);
		}

		private static System.Collections.Generic.List<HiddenOverride> Hidden(PrinterConfig printer, ClubPresetComparison comparison)
		{
			return ClubPresets.HiddenOverrides(comparison, printer.Settings.UserLayer, printer.Settings.MaterialLayer?.ValueOrDefault(ClubPresets.LayerIdKey));
		}

		/// <summary>The club message box, sized to its text (ClubDialogRules.MessageBoxSize).</summary>
		private static void Show(Action<bool> callback, string message, string title, StyledMessageBox.MessageType type, string yesLabel = "")
		{
			var (width, height) = ClubDialogRules.MessageBoxSize(message);
			DialogWindow.Show(
				new StyledMessageBox.MessageBoxPage(
					callback,
					message,
					title,
					type,
					null,
					width,
					height,
					yesLabel,
					type == StyledMessageBox.MessageType.OK ? "" : "Cancel".Localize(),
					ApplicationController.Instance.Theme,
					false,
					new ClubMessageOptions()
					{
						YesLabel = string.IsNullOrEmpty(yesLabel) ? null : yesLabel,
						NoLabel = type == StyledMessageBox.MessageType.OK ? null : "Cancel".Localize(),
					}),
				0);
		}

		private static string KeyLabel(string key)
		{
			return PrinterSettings.SettingsData.TryGetValue(key, out SliceSettingData data) && !string.IsNullOrWhiteSpace(data.PresentationName)
				? data.PresentationName
				: key;
		}
	}
}
