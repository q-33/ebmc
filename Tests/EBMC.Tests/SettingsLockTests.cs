using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.SlicerConfiguration;
using NUnit.Framework;

namespace EBMC.Tests
{
	[TestFixture]
	public class SettingsLockTests
	{
		private static readonly IEnumerable<string> NoMaterial = null;

		// Like the stock Pro Flex preset: it sets speeds and some other per-print keys
		private static readonly IEnumerable<string> ProFlexLike = new[]
		{
			SettingsKey.temperature,
			SettingsKey.infill_speed,
			SettingsKey.perimeter_speed,
			SettingsKey.first_layer_speed,
			SettingsKey.fill_angle,
			SettingsKey.merge_overlapping_lines,
			"layer_id",
			SettingsKey.layer_name,
		};

		[TestCase(SettingsKey.bed_size)]
		[TestCase(SettingsKey.start_gcode)]
		[TestCase(SettingsKey.octoprint_host)]
		[TestCase(SettingsKey.octoprint_enabled)]
		[TestCase(SettingsKey.nozzle_diameter)]
		[TestCase(SettingsKey.printer_name)]
		[TestCase(SettingsKey.baby_step_z_offset)]
		[TestCase(SettingsKey.print_leveling_data)]
		public void PrinterSettings(string key)
		{
			Assert.That(SettingsLock.Category(key, NoMaterial), Is.EqualTo(SettingCategory.Printer));
			Assert.That(SettingsLock.IsLocked(key, NoMaterial), Is.True);
		}

		[TestCase(SettingsKey.temperature)]
		[TestCase(SettingsKey.bed_temperature)]
		[TestCase(SettingsKey.retract_length)]
		[TestCase(SettingsKey.retract_restart_extra)]
		[TestCase(SettingsKey.max_fan_speed)]
		[TestCase(SettingsKey.first_layer_extrusion_width)]
		[TestCase(SettingsKey.active_material_key)]
		public void MaterialSettings(string key)
		{
			Assert.That(SettingsLock.Category(key, NoMaterial), Is.EqualTo(SettingCategory.Material));
			Assert.That(SettingsLock.IsLocked(key, NoMaterial), Is.True);
		}

		[TestCase(SettingsKey.fill_density)]
		[TestCase(SettingsKey.layer_height)]
		[TestCase(SettingsKey.create_per_layer_support)]
		[TestCase(SettingsKey.perimeters)]
		[TestCase(SettingsKey.infill_speed)]
		[TestCase(SettingsKey.first_layer_speed)]
		[TestCase(SettingsKey.active_quality_key)]
		[TestCase(SettingsKey.merge_overlapping_lines)]
		[TestCase("not_a_real_setting")]
		[TestCase("")]
		[TestCase(null)]
		public void PerPrintSettings(string key)
		{
			Assert.That(SettingsLock.Category(key, NoMaterial), Is.EqualTo(SettingCategory.PerPrint));
			Assert.That(SettingsLock.IsLocked(key, NoMaterial), Is.False);
		}

		[TestCase(SettingsKey.infill_speed)]
		[TestCase(SettingsKey.perimeter_speed)]
		[TestCase(SettingsKey.first_layer_speed)]
		public void SpeedSetByTheActiveMaterialStaysPerPrint(string key)
		{
			Assert.That(SettingsLock.Category(key, ProFlexLike), Is.EqualTo(SettingCategory.PerPrint));
			Assert.That(SettingsLock.CanEdit(true, false, key, ProFlexLike), Is.True);
		}

		[TestCase(SettingsKey.fill_angle)]
		[TestCase(SettingsKey.merge_overlapping_lines)]
		public void NonSpeedKeySetByTheActiveMaterialIsMaterial(string key)
		{
			Assert.That(SettingsLock.Category(key, ProFlexLike), Is.EqualTo(SettingCategory.Material));
			Assert.That(SettingsLock.Category(key, NoMaterial), Is.EqualTo(SettingCategory.PerPrint));
			Assert.That(SettingsLock.Category(key, new string[0]), Is.EqualTo(SettingCategory.PerPrint));
		}

		[Test]
		public void ActiveMaterialDoesNotChangeOtherKeys()
		{
			Assert.That(SettingsLock.Category(SettingsKey.fill_density, ProFlexLike), Is.EqualTo(SettingCategory.PerPrint));
			Assert.That(SettingsLock.Category(SettingsKey.bed_size, ProFlexLike), Is.EqualTo(SettingCategory.Printer));
		}

		[Test]
		public void PrinterWinsOverActiveMaterial()
		{
			var material = new[] { SettingsKey.start_gcode, SettingsKey.nozzle_diameter };

			Assert.That(SettingsLock.Category(SettingsKey.start_gcode, material), Is.EqualTo(SettingCategory.Printer));
			Assert.That(SettingsLock.Category(SettingsKey.nozzle_diameter, material), Is.EqualTo(SettingCategory.Printer));
		}

		[Test]
		public void PresetNameAndIdAreNotMaterialSettings()
		{
			Assert.That(SettingsLock.Category(SettingsKey.layer_name, ProFlexLike), Is.EqualTo(SettingCategory.PerPrint));
			Assert.That(SettingsLock.Category("layer_id", ProFlexLike), Is.EqualTo(SettingCategory.PerPrint));
		}

		[Test]
		public void EveryPrinterLayoutKeyIsPrinter()
		{
			var keys = SliceSettingsLayouts.PrinterSettings().SelectMany(c => c.groups).SelectMany(g => g.settings).ToList();
			Assert.That(keys, Is.Not.Empty);

			foreach (var key in keys)
			{
				Assert.That(SettingsLock.Category(key, ProFlexLike), Is.EqualTo(SettingCategory.Printer), key);
			}
		}

		[Test]
		public void EveryFilamentKeyIsMaterial()
		{
			var keys = SliceSettingsLayouts.SliceSettings()
				.Where(c => c.categoryName == "Filament")
				.SelectMany(c => c.groups)
				.SelectMany(g => g.settings)
				.ToList();
			Assert.That(keys, Is.Not.Empty);

			foreach (var key in keys)
			{
				Assert.That(SettingsLock.Category(key, NoMaterial), Is.EqualTo(SettingCategory.Material), key);
			}
		}

		// lockEnabled, unlocked, key, expected
		[TestCase(false, false, SettingsKey.bed_size, true)]
		[TestCase(false, false, SettingsKey.temperature, true)]
		[TestCase(false, false, SettingsKey.fill_density, true)]
		[TestCase(true, false, SettingsKey.bed_size, false)]
		[TestCase(true, false, SettingsKey.temperature, false)]
		[TestCase(true, false, SettingsKey.active_material_key, false)]
		[TestCase(true, false, SettingsKey.fill_density, true)]
		[TestCase(true, false, SettingsKey.active_quality_key, true)]
		[TestCase(true, true, SettingsKey.bed_size, true)]
		[TestCase(true, true, SettingsKey.temperature, true)]
		[TestCase(true, true, SettingsKey.fill_density, true)]
		[TestCase(false, true, SettingsKey.bed_size, true)]
		public void CanEdit(bool lockEnabled, bool unlocked, string key, bool expected)
		{
			Assert.That(SettingsLock.CanEdit(lockEnabled, unlocked, key, NoMaterial), Is.EqualTo(expected));
		}

		[Test]
		public void CanEditUsesTheActiveMaterial()
		{
			Assert.That(SettingsLock.CanEdit(true, false, SettingsKey.merge_overlapping_lines, ProFlexLike), Is.False);
			Assert.That(SettingsLock.CanEdit(true, false, SettingsKey.merge_overlapping_lines, NoMaterial), Is.True);
		}

		private static readonly Dictionary<string, string> SceneOverrides = new Dictionary<string, string>
		{
			[SettingsKey.fill_density] = "40%",
			[SettingsKey.infill_speed] = "20",
			[SettingsKey.temperature] = "300",
			[SettingsKey.bed_size] = "500,500",
			[SettingsKey.fill_angle] = "10",
		};

		[Test]
		public void FilterOverridesWhenLockedKeepsOnlyPerPrint()
		{
			var result = SettingsLock.FilterOverrides(SceneOverrides, true, false, ProFlexLike);

			Assert.That(result.Keys, Is.EquivalentTo(new[] { SettingsKey.fill_density, SettingsKey.infill_speed }));
			Assert.That(result[SettingsKey.fill_density], Is.EqualTo("40%"));
			Assert.That(SceneOverrides, Has.Count.EqualTo(5), "the input is not changed");
		}

		[Test]
		public void FilterOverridesWithoutActiveMaterialKeepsFillAngle()
		{
			var result = SettingsLock.FilterOverrides(SceneOverrides, true, false, NoMaterial);

			Assert.That(result.Keys, Is.EquivalentTo(new[] { SettingsKey.fill_density, SettingsKey.infill_speed, SettingsKey.fill_angle }));
		}

		[TestCase(false, false)]
		[TestCase(true, true)]
		[TestCase(false, true)]
		public void FilterOverridesKeepsEverythingWhenEditingIsAllowed(bool lockEnabled, bool unlocked)
		{
			var result = SettingsLock.FilterOverrides(SceneOverrides, lockEnabled, unlocked, ProFlexLike);

			Assert.That(result, Is.EquivalentTo(SceneOverrides));
		}

		[Test]
		public void FilterOverridesOfNothing()
		{
			Assert.That(SettingsLock.FilterOverrides(null, true, false, NoMaterial), Is.Empty);
		}

		[Test]
		public void LockedWording()
		{
			Func<string, string> identity = s => s;

			Assert.That(MaintainerMessages.Locked(identity), Is.EqualTo("Locked: a club maintainer can change this"));
			Assert.That(MaintainerMessages.StatusLocked(identity), Is.EqualTo("Locked"));
			Assert.That(MaintainerMessages.StatusUnlocked(identity), Is.EqualTo("Unlocked until the app closes"));
			Assert.That(MaintainerMessages.WrongPin(s => "[" + s + "]"), Is.EqualTo("[That PIN is not right]"));
			Assert.That(MaintainerMessages.PinNotValid(null), Is.EqualTo("A PIN is 4 to 8 digits"));
			Assert.That(MaintainerMessages.SetupNeeded(identity), Is.EqualTo("A club maintainer needs to set up this printer"));
		}
	}
}
