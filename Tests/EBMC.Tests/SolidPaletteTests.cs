using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl.PartPreviewWindow;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The exact-solid feature's wiring checked against the files it names: the palette's Solids family, the gated
	/// generators, the glyphs, the OEM switch and the Open CASCADE notices.
	/// </summary>
	[TestFixture]
	public class SolidPaletteTests
	{
		private const string PrimitivesPath = "MatterControlLib/Library/Providers/MatterControl/Primitives3DContainer.cs";

		private static string root;

		[OneTimeSetUp]
		public void FindRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, PrimitivesPath)))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null, "repository root above the test assembly");
			root = directory.FullName;
		}

		private static string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));

		[Test]
		public void SolidsSectionOnlyOnDesignTabsWhenAskedFor()
		{
			Assert.That(PaletteCatalog.For(PaletteTab.Design, false), Is.SameAs(PaletteCatalog.Design));
			Assert.That(PaletteCatalog.For(PaletteTab.Printer, true), Is.SameAs(PaletteCatalog.Printer));

			var withSolids = PaletteCatalog.For(PaletteTab.Design, true);
			Assert.That(withSolids.Select(s => s.Label), Is.EqualTo(new[] { "Shapes", "Solids", "Tools" }));
			Assert.That(withSolids[1], Is.SameAs(PaletteCatalog.Solids));

			var family = PaletteCatalog.Solids.Families.Single();
			Assert.That(family.Id, Is.EqualTo("solids"));
			Assert.That(family.CanBeHole, Is.True);
			Assert.That(family.HasSizeChips, Is.False, "solids have their own size handles");
			Assert.That(family.Members.Select(m => m.Name), Is.EqualTo(new[] { "Solid Box", "Solid Cylinder", "Plate with Hole" }));

			// family ids stay unique with the solids in
			var ids = withSolids.SelectMany(s => s.Families).Select(f => f.Id).ToList();
			Assert.That(ids, Is.Unique);
		}

		[Test]
		public void SolidGeneratorsAreRegisteredBehindTheSwitch()
		{
			var source = Read(PrimitivesPath);
			var gate = source.IndexOf("if (DesignTools.Solid.SolidModeling.IsEnabled)");
			Assert.That(gate, Is.GreaterThan(0), "the solids are only added when solid modelling is enabled");

			foreach (var member in PaletteCatalog.Solids.Families.SelectMany(f => f.Members))
			{
				var at = source.IndexOf($"\"{member.Name}\".Localize()");
				Assert.That(at, Is.GreaterThan(gate), $"{member.Name} is a generator inside the solid modelling gate");
				Assert.That(source, Does.Contain($"{member.ObjectType}.Create()"), member.Name);

				// the default size shown is the class's default
				var classSource = Read($"MatterControlLib/DesignTools/Solid/{member.ObjectType}.cs");
				var firstProperty = member.ObjectType == "SolidCylinderObject3D" ? "Diameter" : "Width";
				var match = Regex.Match(classSource, $"public DoubleOrExpression {firstProperty} {{ get; set; }} = ([0-9.]+);");
				Assert.That(match.Success, Is.True, $"{member.ObjectType}.{firstProperty} default");
				Assert.That(double.Parse(match.Groups[1].Value), Is.EqualTo(member.Dimensions[0]), member.Name);

				Assert.That(member.Hint, Does.StartWith("Exact solid:"), member.Name);
				Assert.That(member.Hint + member.Title + member.SizeText(), Does.Not.Contain("MatterControl"));
			}

			// the palette asks for the solids section through the same switch
			Assert.That(Read("MatterControlLib/PartPreviewWindow/ShapesPalette/ShapesPalette.cs"),
				Does.Contain("PaletteCatalog.For(tab, DesignTools.Solid.SolidModeling.IsEnabled)"));
		}

		[Test]
		public void SolidGlyphsAreBuilt()
		{
			using (var document = JsonDocument.Parse(Read("Tools/EBMC/icons/map.json")))
			{
				var icons = document.RootElement.GetProperty("icons");
				foreach (var glyph in PaletteCatalog.Solids.Families.SelectMany(f => f.Members).Select(m => m.Glyph))
				{
					var target = $"Shapes/{glyph}.png";
					Assert.That(icons.TryGetProperty(target, out var entry), Is.True, $"{target} is in map.json");
					Assert.That(entry.GetProperty("size").GetInt32(), Is.EqualTo(PaletteCatalog.GlyphSize * 4), target);
					Assert.That(entry.GetProperty("source").GetString(), Is.EqualTo("custom:" + glyph));
					Assert.That(File.Exists(Path.Combine(root, "Tools", "EBMC", "icons", "custom", glyph + ".svg")), Is.True, $"{glyph}.svg");
					Assert.That(File.Exists(Path.Combine(root, "StaticData", "Icons", "Shapes", glyph + ".png")), Is.True, $"{target} is built");
				}
			}
		}

		[Test]
		public void SwitchIsOffInStockAndOnForTheClub()
		{
			Assert.That(Read("MatterControlLib/SettingsManagement/OemSettings.cs"), Does.Contain("public bool EnableSolidModeling = false;"));
			using (var document = JsonDocument.Parse(Read("StaticData/OEMSettings/Settings.json")))
			{
				Assert.That(document.RootElement.GetProperty("EnableSolidModeling").GetBoolean(), Is.True);
			}

			Assert.That(Read("Tools/EBMC/generate_ebmc_files.py"), Does.Contain("'EnableSolidModeling': True"));
		}

		[Test]
		public void OpenCascadeNoticesShip()
		{
			using (var document = JsonDocument.Parse(Read("StaticData/License/license.json")))
			{
				var entry = document.RootElement.EnumerateArray().Single(e => e.GetProperty("Name").GetString() == "OpenCASCADE");
				Assert.That(entry.GetProperty("Title").GetString(), Is.EqualTo("Open CASCADE Technology"));
			}

			var notice = Read("StaticData/License/OpenCASCADE.txt");
			Assert.That(notice, Does.Contain("b8f597c677811d1f9f4d8a97f5ae2825c0353a42"));
			Assert.That(notice, Does.Contain("Open CASCADE exception (version 1.0) to GNU LGPL version 2.1."));
			Assert.That(notice, Does.Contain("GNU LESSER GENERAL PUBLIC LICENSE"));
			Assert.That(notice, Does.Contain("Contact: (club to fill in)"));
			Assert.That(notice, Does.Contain("https://github.com/q-33/ebmc/releases"));
			Assert.That(notice, Does.Not.Contain("MatterControl"));
			Assert.That(Read("NOTICE.md"), Does.Contain("Open CASCADE Technology 8.0.1"));
		}

		[Test]
		public void KernelBinaryIsOptionalAndNotCommitted()
		{
			var project = Read("MatterControl.CrossPlatform/MatterControl.CrossPlatform.csproj");
			Assert.That(project, Does.Contain("<ItemGroup Condition=\"Exists('$(EbmcOcctOsxArm64)')\">"));
			Assert.That(Read(".gitignore"), Does.Contain("native/occt/dist/"));
		}
	}
}
