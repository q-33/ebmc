using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MatterHackers.MatterControl;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The club build has one theme: OemSettings.LockedThemeSet picks it, and the rule falls back to stock loading when
	/// it is empty or missing.
	/// </summary>
	[TestFixture]
	public class ThemeLockTests
	{
		private static readonly (string, IEnumerable<string>)[] Providers =
		{
			("Modern", new[] { "Modern-Dark", "Modern-Light" }),
			("EBMC", new[] { "EBMC-Dark", "EBMC-Paper" }),
		};

		[Test]
		public void LockedThemeFindsItsProvider()
		{
			Assert.That(ThemeLock.ProviderFor("EBMC-Dark", Providers), Is.EqualTo("EBMC"));
		}

		[TestCase("")]
		[TestCase(null)]
		[TestCase("   ")]
		public void EmptyMeansStock(string locked)
		{
			Assert.That(ThemeLock.ProviderFor(locked, Providers), Is.Null);
		}

		[Test]
		public void MissingThemesetFallsBackToStock()
		{
			Assert.That(ThemeLock.ProviderFor("EBMC-Neon", Providers), Is.Null);
			Assert.That(ThemeLock.ProviderFor("ebmc-dark", Providers), Is.Null, "names are matched exactly, as the files are");
		}

		[Test]
		public void ClubBuildLocksTheClubDarkTheme()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "StaticData", "OEMSettings", "Settings.json")))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null);
			string root = directory.FullName;

			using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "StaticData", "OEMSettings", "Settings.json"))))
			{
				string locked = document.RootElement.GetProperty("LockedThemeSet").GetString();
				Assert.That(locked, Is.EqualTo("EBMC-Dark"));

				// the themeset the generator writes is where the app looks for it
				var clubThemes = Directory.GetFiles(Path.Combine(root, "StaticData", "Themes", "System", "EBMC"), "*.themeset")
					.Select(Path.GetFileNameWithoutExtension);
				Assert.That(ThemeLock.ProviderFor(locked, new[] { ("EBMC", clubThemes) }), Is.EqualTo("EBMC"));
			}
		}
	}
}
