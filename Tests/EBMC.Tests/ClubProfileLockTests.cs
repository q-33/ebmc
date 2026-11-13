using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MatterHackers.MatterControl.SlicerConfiguration;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The maintainer lock applied to the real club printer profile with its default (active) material.
	/// </summary>
	[TestFixture]
	public class ClubProfileLockTests
	{
		private const string ProfilePath = "StaticData/Profiles/East Bay Makers Club/Pulse S-232 Mosquito.printer";

		private static List<string> activeMaterialKeys;

		[OneTimeSetUp]
		public void LoadProfile()
		{
			string path = Path.Combine(FindRepositoryRoot(), ProfilePath);

			using (var document = JsonDocument.Parse(File.ReadAllText(path)))
			{
				var root = document.RootElement;
				string activeId = root.GetProperty("OemLayer").GetProperty(SettingsKey.active_material_key).GetString();
				Assert.That(activeId, Is.Not.Empty, "the club profile has a default material");

				var layer = root.GetProperty("MaterialLayers").EnumerateArray()
					.Single(l => l.TryGetProperty("layer_id", out var id) && id.GetString() == activeId);

				activeMaterialKeys = layer.EnumerateObject().Select(p => p.Name).ToList();
			}

			Assert.That(activeMaterialKeys, Does.Contain(SettingsKey.temperature), "the active material is a real material preset");
		}

		/// <summary>
		/// The repository root: the first folder above the test assembly that holds the profile.
		/// </summary>
		private static string FindRepositoryRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null)
			{
				if (File.Exists(Path.Combine(directory.FullName, ProfilePath)))
				{
					return directory.FullName;
				}

				directory = directory.Parent;
			}

			throw new FileNotFoundException("Could not find the repository root above " + TestContext.CurrentContext.TestDirectory, ProfilePath);
		}

		[TestCase(SettingsKey.layer_height)]
		[TestCase(SettingsKey.first_layer_height)]
		[TestCase(SettingsKey.fill_density)]
		[TestCase(SettingsKey.create_per_layer_support)]
		[TestCase(SettingsKey.create_raft)]
		[TestCase(SettingsKey.create_brim)]
		[TestCase(SettingsKey.brims)]
		[TestCase(SettingsKey.create_skirt)]
		[TestCase(SettingsKey.skirts)]
		[TestCase(SettingsKey.active_quality_key)]
		public void MemberCanChange(string key)
		{
			Assert.That(SettingsLock.Category(key, activeMaterialKeys), Is.EqualTo(SettingCategory.PerPrint));
			Assert.That(SettingsLock.CanEdit(true, false, key, activeMaterialKeys), Is.True);
		}

		[Test]
		public void EverySpeedIsPerPrint()
		{
			var speedKeys = SliceSettingsLayouts.SliceSettings()
				.Where(c => c.categoryName == "Speed")
				.SelectMany(c => c.groups)
				.SelectMany(g => g.settings)
				.ToList();
			Assert.That(speedKeys, Is.Not.Empty);

			foreach (var key in speedKeys)
			{
				Assert.That(SettingsLock.Category(key, activeMaterialKeys), Is.EqualTo(SettingCategory.PerPrint), key);
			}
		}

		[TestCase(SettingsKey.temperature, SettingCategory.Material)]
		[TestCase(SettingsKey.bed_temperature, SettingCategory.Material)]
		[TestCase(SettingsKey.retract_length, SettingCategory.Material)]
		[TestCase(SettingsKey.max_fan_speed, SettingCategory.Material)]
		[TestCase(SettingsKey.active_material_key, SettingCategory.Material)]
		[TestCase(SettingsKey.start_gcode, SettingCategory.Printer)]
		[TestCase(SettingsKey.z_offset, SettingCategory.Printer)]
		[TestCase(SettingsKey.octoprint_host, SettingCategory.Printer)]
		public void MemberCannotChange(string key, SettingCategory expected)
		{
			Assert.That(SettingsLock.Category(key, activeMaterialKeys), Is.EqualTo(expected));
			Assert.That(SettingsLock.CanEdit(true, false, key, activeMaterialKeys), Is.False);
			Assert.That(SettingsLock.CanEdit(true, true, key, activeMaterialKeys), Is.True);
		}
	}
}
