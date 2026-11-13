using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.SlicerConfiguration
{
	/// <summary>
	/// EBMC Edition: which group a printer-profile setting belongs to for the club's maintainer lock.
	/// </summary>
	public enum SettingCategory
	{
		/// <summary>The machine itself: hardware, connection, G-code, calibration and printer identity.</summary>
		Printer,

		/// <summary>The loaded filament: temperatures, fan, retraction and anything a material preset changes.</summary>
		Material,

		/// <summary>Choices for one print (layers, infill, speeds, adhesion, support, ...). Never locked.</summary>
		PerPrint,
	}

	/// <summary>
	/// EBMC Edition: the rule for which settings an ordinary club member may change when the maintainer lock is on.
	/// Printer and material settings are locked; per-print settings are not. Kept free of any GUI code so that it can
	/// be unit tested; the app calls it through <c>Maintainer.CanEdit</c>.
	/// </summary>
	public static class SettingsLock
	{
		/// <summary>
		/// Keys that are not in the Printer Settings layout but describe the machine, its connection or its
		/// calibration state.
		/// </summary>
		private static readonly string[] ExtraPrinterKeys =
		{
			SettingsKey.printer_name,
			SettingsKey.device_token,
			SettingsKey.device_type,
			SettingsKey.driver_type,
			SettingsKey.oem_profile_token,
			SettingsKey.publish_bed_image,
			SettingsKey.calibration_files,
			SettingsKey.print_leveling_data,
			SettingsKey.print_leveling_enabled,
			SettingsKey.probe_has_been_calibrated,
			SettingsKey.xy_offsets_have_been_calibrated,
			SettingsKey.baby_step_z_offset,
			SettingsKey.z_offset,
			SettingsKey.manual_movement_speeds,
			SettingsKey.enable_line_splitting,
			SettingsKey.use_firmware_retraction,
			SettingsKey.use_relative_e_distances,
		};

		/// <summary>
		/// Keys that are not in the Filament layout but are material values.
		/// </summary>
		private static readonly string[] ExtraMaterialKeys =
		{
			SettingsKey.active_material_key,
			SettingsKey.first_layer_temperature,
			SettingsKey.first_layer_bed_temperature,
			SettingsKey.standby_temperature_delta,
			SettingsKey.material_sku,
			SettingsKey.default_material_presets,
		};

		/// <summary>
		/// Bookkeeping keys every preset layer carries (its name and identity). They are not settings, so a material
		/// layer containing them does not make them material settings.
		/// </summary>
		private static readonly HashSet<string> LayerMetadataKeys = new HashSet<string>
		{
			SettingsKey.layer_name,
			"layer_id",
			"layer_etag",
			"layer_source",
		};

		private static readonly Lazy<HashSet<string>> PrinterKeys = new Lazy<HashSet<string>>(() =>
			new HashSet<string>(AllKeys(SliceSettingsLayouts.PrinterSettings()).Concat(ExtraPrinterKeys)));

		private static readonly Lazy<HashSet<string>> MaterialKeys = new Lazy<HashSet<string>>(() =>
			new HashSet<string>(
				AllKeys(SliceSettingsLayouts.SliceSettings().Where(category => category.categoryName == "Filament"))
					.Concat(ExtraMaterialKeys)));

		/// <summary>
		/// Every key in the "Speed" category. Speeds stay editable for each print (owner decision), so a material preset
		/// that sets one (Pro Flex sets several) does not make it a material setting.
		/// </summary>
		private static readonly Lazy<HashSet<string>> SpeedKeys = new Lazy<HashSet<string>>(() =>
			new HashSet<string>(AllKeys(SliceSettingsLayouts.SliceSettings().Where(category => category.categoryName == "Speed"))));

		/// <summary>
		/// The category of <paramref name="key"/>. <paramref name="activeMaterialKeys"/> is the keys the active material
		/// preset sets (may be null): such a key counts as a material setting, except keys in the Speed category and the
		/// preset's own name and id. A key that is both a printer and a material setting is a printer setting. Unknown
		/// keys are per-print.
		/// </summary>
		public static SettingCategory Category(string key, IEnumerable<string> activeMaterialKeys)
		{
			if (string.IsNullOrEmpty(key))
			{
				return SettingCategory.PerPrint;
			}

			if (PrinterKeys.Value.Contains(key))
			{
				return SettingCategory.Printer;
			}

			if (MaterialKeys.Value.Contains(key))
			{
				return SettingCategory.Material;
			}

			if (!LayerMetadataKeys.Contains(key)
				&& !SpeedKeys.Value.Contains(key)
				&& activeMaterialKeys != null
				&& activeMaterialKeys.Contains(key))
			{
				return SettingCategory.Material;
			}

			return SettingCategory.PerPrint;
		}

		/// <summary>
		/// True for printer and material settings.
		/// </summary>
		public static bool IsLocked(string key, IEnumerable<string> activeMaterialKeys)
		{
			return Category(key, activeMaterialKeys) != SettingCategory.PerPrint;
		}

		/// <summary>
		/// Whether <paramref name="key"/> may be changed now: always when the lock is off or a maintainer has unlocked
		/// it, otherwise only per-print settings.
		/// </summary>
		public static bool CanEdit(bool lockEnabled, bool unlocked, string key, IEnumerable<string> activeMaterialKeys)
		{
			return !lockEnabled
				|| unlocked
				|| !IsLocked(key, activeMaterialKeys);
		}

		/// <summary>
		/// The part-settings overrides from the scene that may take effect now: all of them when editing is allowed,
		/// otherwise only the per-print ones. Returns a new dictionary; the input is not changed.
		/// </summary>
		public static Dictionary<string, string> FilterOverrides(IEnumerable<KeyValuePair<string, string>> overrides, bool lockEnabled, bool unlocked, IEnumerable<string> activeMaterialKeys)
		{
			var result = new Dictionary<string, string>();
			if (overrides == null)
			{
				return result;
			}

			// Read the material keys once; they are checked for every override
			var materialKeys = activeMaterialKeys == null ? null : new HashSet<string>(activeMaterialKeys);

			foreach (var setting in overrides)
			{
				if (CanEdit(lockEnabled, unlocked, setting.Key, materialKeys))
				{
					result[setting.Key] = setting.Value;
				}
			}

			return result;
		}

		/// <summary>
		/// Whether a G-code file opened from elsewhere must be refused for sending or printing: it carries another
		/// slicer's temperatures, coordinates and start moves, so only a maintainer may print one while the lock is on.
		/// </summary>
		public static bool RefuseGCodeSource(bool isGCodeSource, bool lockEnabled, bool unlocked)
		{
			return isGCodeSource && lockEnabled && !unlocked;
		}

		/// <summary>
		/// Whether a plate must be refused for sending or printing because it holds parts that write their own G-code
		/// into the print (Set Temperature, Send G-Code, Pause and any other scene G-code transformer): their
		/// temperatures and commands bypass the locked material and printer settings, so only a maintainer may print
		/// such a plate while the lock is on.
		/// </summary>
		public static bool RefuseCommandParts(bool hasCommandParts, bool lockEnabled, bool unlocked)
		{
			return hasCommandParts && lockEnabled && !unlocked;
		}

		private static IEnumerable<string> AllKeys(IEnumerable<(string categoryName, (string groupName, string[] settings)[] groups)> layout)
		{
			return layout.SelectMany(category => category.groups).SelectMany(group => group.settings);
		}
	}
}
