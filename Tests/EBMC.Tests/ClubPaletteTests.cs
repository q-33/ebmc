using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.PartPreviewWindow;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The club shapes and Club Starters on the shapes palette (PaletteCatalog.For with withClubShapes), checked against the
	/// generators, classes, glyphs and OEM settings they name. Without the switch the palette is exactly as before (the
	/// existing ShapesPaletteCatalogTests and UiModeTests are unchanged).
	/// </summary>
	[TestFixture]
	public class ClubPaletteTests
	{
		private const string PrimitivesPath = "MatterControlLib/Library/Providers/MatterControl/Primitives3DContainer.cs";

		private const string StartersPath = "MatterControlLib/Library/Providers/MatterControl/ClubStartersContainer.cs";

		private static string root;

		[OneTimeSetUp]
		public void FindRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, PrimitivesPath)))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null);
			root = directory.FullName;
		}

		private static string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));

		private static IEnumerable<PaletteFamily> Families(PaletteTab tab, bool solids, UiMode mode) => PaletteCatalog.For(tab, solids, mode, true).SelectMany(s => s.Families);

		private static IEnumerable<PaletteMember> AllClubMembers =>
			new[] { UiMode.Simple, UiMode.Advanced }.SelectMany(m => Families(PaletteTab.Design, true, m)).SelectMany(f => f.Members).Distinct();

		private static HashSet<string> Names(string source) =>
			new HashSet<string>(Regex.Matches(source, "\"([^\"]+)\"\\.Localize\\(\\)").Cast<Match>().Select(m => m.Groups[1].Value));

		[Test]
		public void WithoutTheSwitchNothingChanges()
		{
			foreach (var tab in new[] { PaletteTab.Design, PaletteTab.Printer })
			{
				foreach (var mode in new[] { UiMode.Simple, UiMode.Advanced })
				{
					foreach (var solids in new[] { false, true })
					{
						Assert.That(PaletteCatalog.For(tab, solids, mode, false), Is.EqualTo(PaletteCatalog.For(tab, solids, mode)), $"{tab} {mode} {solids}");
					}
				}
			}
		}

		[Test]
		public void PrinterTabsNeverShowClubShapesOrStarters()
		{
			foreach (var mode in new[] { UiMode.Simple, UiMode.Advanced })
			{
				Assert.That(PaletteCatalog.For(PaletteTab.Printer, true, mode, true), Is.SameAs(PaletteCatalog.For(PaletteTab.Printer, true, mode)));
				var names = Families(PaletteTab.Printer, true, mode).SelectMany(f => f.Members).Select(m => m.Name).ToList();
				Assert.That(names, Has.None.EqualTo("Rounded Box").And.None.EqualTo("Name Tag").And.None.EqualTo("Gear"));
				Assert.That(Families(PaletteTab.Printer, true, mode).SelectMany(f => f.Members).Any(m => m.Source == PaletteSource.Starters), Is.False);
			}
		}

		[Test]
		public void AdvancedDesignLists()
		{
			Assert.That(Families(PaletteTab.Design, false, UiMode.Advanced).Select(f => f.Id),
				Is.EqualTo(new[] { "box", "round", "more", "text", "image", "starters", "marks", "combine", "align", "mirror", "array", "planecut" }));
			Assert.That(PaletteCatalog.For(PaletteTab.Design, true, UiMode.Advanced, true).Select(s => s.Label), Is.EqualTo(new[] { "Shapes", "Solids", "Tools" }));

			var box = Families(PaletteTab.Design, false, UiMode.Advanced).Single(f => f.Id == "box");
			Assert.That(box.Members.Select(m => m.Name), Is.EqualTo(new[] { "Cube", "Pyramid", "Wedge", "Half Wedge", "Rounded Box", "Slot Plate" }));
			var round = Families(PaletteTab.Design, false, UiMode.Advanced).Single(f => f.Id == "round");
			Assert.That(round.Members.Last().Name, Is.EqualTo("Tube"));
			var more = Families(PaletteTab.Design, false, UiMode.Advanced).Single(f => f.Id == "more");
			Assert.That(more.Members.Select(m => m.Name), Is.EqualTo(new[] { "Polygon Prism", "Star", "Heart", "Gear" }));
		}

		[Test]
		public void SimpleDesignLists()
		{
			foreach (var solids in new[] { false, true })
			{
				Assert.That(Families(PaletteTab.Design, solids, UiMode.Simple).Select(f => f.Id),
					Is.EqualTo(new[] { "box", "round", "text", "image", "starters", "measure" }));
			}

			// Gear, Star, Heart and Polygon Prism are not in Simple at all; Rounded Box, Slot Plate and Tube sit in the flyouts
			var names = Families(PaletteTab.Design, false, UiMode.Simple).SelectMany(f => f.Members).Select(m => m.Name).ToList();
			Assert.That(names, Has.None.EqualTo("Gear").And.None.EqualTo("Star").And.None.EqualTo("Heart").And.None.EqualTo("Polygon Prism"));
			Assert.That(names, Does.Contain("Rounded Box").And.Contain("Slot Plate").And.Contain("Tube"));
			Assert.That(Families(PaletteTab.Design, false, UiMode.Simple).Any(f => f.Members.Any(m => m.Source == PaletteSource.SceneOperation)), Is.False);
		}

		[Test]
		public void StartersFamily()
		{
			var starters = Families(PaletteTab.Design, false, UiMode.Simple).Single(f => f.Id == "starters");
			Assert.That(starters, Is.SameAs(PaletteCatalog.Starters));
			Assert.That(starters.Members.Select(m => m.Name), Is.EqualTo(new[] { "Name Tag", "Sign", "Box with Lid", "Wall Hook", "Phone Stand", "Knob", "Cable Clip", "Plant Label", "QR Tag" }));
			Assert.That(starters.Members.All(m => m.Source == PaletteSource.Starters), Is.True);
			Assert.That(starters.Members.All(m => !string.IsNullOrEmpty(m.Hint)), Is.True, "each starter has a one-line description");
			Assert.That(starters.ClickOpensFlyout, Is.True, "starters are chosen from the list");
			Assert.That(starters.CanBeHole, Is.False);
			Assert.That(starters.HasSizeChips, Is.False);
			Assert.That(starters.FamilyGlyph, Is.EqualTo(PaletteCatalog.StartersGlyph));
		}

		[Test]
		public void FlyoutsStayShort()
		{
			foreach (var mode in new[] { UiMode.Simple, UiMode.Advanced })
			{
				foreach (var family in Families(PaletteTab.Design, true, mode).Where(f => f.Id != "starters"))
				{
					Assert.That(family.Members.Count, Is.LessThanOrEqualTo(PaletteCatalog.MaxShapeFlyoutRows), family.Id);
				}
			}
		}

		[Test]
		public void EveryShapeAndStarterIsRegistered()
		{
			var primitives = Read(PrimitivesPath);
			var primitiveNames = Names(primitives);
			var starterNames = Names(Read(StartersPath));
			foreach (var member in AllClubMembers)
			{
				switch (member.Source)
				{
					case PaletteSource.Primitives:
						Assert.That(primitiveNames, Does.Contain(member.Name), member.Name);
						break;
					case PaletteSource.Starters:
						Assert.That(starterNames, Does.Contain(member.Name), member.Name);
						break;
				}
			}

			// the club shapes and starters are only listed when the switch is on
			Assert.That(primitives, Does.Contain("if (DesignTools.ClubShapes.ClubShapesFeature.IsEnabled)"));
			Assert.That(Read("MatterControlLib/DesignTools/EditorTools/EditorToolsPlugin.cs"), Does.Contain("ClubShapesFeature.IsEnabled"));
			Assert.That(Read("MatterControlLib/Library/Providers/MatterControl/DesignAppsCollectionContainer.cs"), Does.Contain("ClubShapesFeature.IsEnabled"));
		}

		[Test]
		public void SizeChipsScaleRealProperties()
		{
			var primitives = Read(PrimitivesPath);
			foreach (var member in AllClubMembers.Where(m => m.Scalable))
			{
				Assert.That(primitives, Does.Contain($"{member.ObjectType}.Create()"), member.Name);
				var classSource = Read($"MatterControlLib/DesignTools/Primitives/{member.ObjectType}.cs");
				foreach (var property in member.ScaledProperties)
				{
					Assert.That(classSource, Does.Contain($"public DoubleOrExpression {property} {{ get; set; }}"), $"{member.ObjectType}.{property}");
				}

				var match = Regex.Match(classSource, $"public DoubleOrExpression {member.ScaledProperties[0]} {{ get; set; }} = ([0-9.]+);");
				Assert.That(match.Success && double.Parse(match.Groups[1].Value) == member.Dimensions[0], Is.True, member.Name + " default size");
			}

			// chips on the Box and Round flyouts only, where every member scales
			var withChips = Families(PaletteTab.Design, true, UiMode.Advanced).Where(f => f.HasSizeChips).ToList();
			Assert.That(withChips.Select(f => f.Id), Is.EquivalentTo(new[] { "box", "round" }));
			Assert.That(withChips.All(f => f.Members.All(m => m.Scalable)), Is.True);
		}

		[TestCase("Rounded Box", 40, "40 × 40 × 40")]
		[TestCase("Slot Plate", 10, "20 × 10 × 2.5")]
		[TestCase("Tube", 20, "Ø20 × 20")]
		public void SizeText(string name, int chip, string expected)
		{
			Assert.That(AllClubMembers.First(m => m.Name == name).SizeText(PaletteCatalog.ScaleFor(chip)), Is.EqualTo(expected));
		}

		[Test]
		public void HolesOnlyForShapes()
		{
			Assert.That(Families(PaletteTab.Design, false, UiMode.Advanced).Where(f => f.CanBeHole).Select(f => f.Id), Is.EquivalentTo(new[] { "box", "round", "more", "text", "image" }));
		}

		[Test]
		public void EveryGlyphIsBuilt()
		{
			using (var document = JsonDocument.Parse(Read("Tools/EBMC/icons/map.json")))
			{
				var icons = document.RootElement.GetProperty("icons");
				foreach (var glyph in PaletteCatalog.ClubGlyphs(PaletteTab.Design))
				{
					var target = $"Shapes/{glyph}.png";
					Assert.That(icons.TryGetProperty(target, out var entry), Is.True, $"{target} is in map.json");
					Assert.That(entry.GetProperty("size").GetInt32(), Is.EqualTo(PaletteCatalog.GlyphSize * 4), target);
					Assert.That(File.Exists(Path.Combine(root, "StaticData", "Icons", "Shapes", glyph + ".png")), Is.True, $"{target} is built");
				}
			}
		}

		[Test]
		public void TextAvoidsTheUpstreamName()
		{
			var texts = AllClubMembers.Select(m => m.Title).Concat(AllClubMembers.Select(m => m.Hint)).Concat(AllClubMembers.Select(m => m.SizeText()))
				.Concat(Families(PaletteTab.Design, true, UiMode.Advanced).Select(f => f.Label))
				.Where(t => t != null);
			foreach (var text in texts)
			{
				Assert.That(text, Does.Not.Contain("MatterControl"));
			}
		}

		[Test]
		public void StartCardOnlyOnNewEmptyDesignTabs()
		{
			var main = Read("MatterControlLib/PartPreviewWindow/MainViewWidget.cs");
			Assert.That(Regex.Matches(main, "DesignStartCard\\.StartEmpty\\(").Count, Is.EqualTo(1), "only CreateNewDesignTab shows it");
			Assert.That(main, Does.Contain("if (addPhilToBed\n\t\t\t\t&& !DesignStartCard.StartEmpty(newTab))"));

			var card = Read("MatterControlLib/PartPreviewWindow/DesignStartCard.cs");
			Assert.That(card, Does.Contain("OemSettings.Instance.NewDesignStartsEmpty"));
			Assert.That(card, Does.Contain("!(page is PrinterTabPage)"));
			Assert.That(card, Does.Contain("UserSettingsKey.HideDesignStartCard"));

			// every handler it adds is removed when it closes (tab closed, card dismissed or the scene got a part)
			foreach (var handler in new[] { "Scene.Children.ItemsModified", "Scene.Invalidated" })
			{
				Assert.That(card, Does.Contain(handler + " += Scene_Changed"));
				Assert.That(card, Does.Contain(handler + " -= Scene_Changed"));
			}
		}

		[Test]
		public void OemSwitchesAreOffInStockAndOnForTheClub()
		{
			var oem = Read("MatterControlLib/SettingsManagement/OemSettings.cs");
			Assert.That(oem, Does.Contain("public bool EnableClubShapes = false;"));
			Assert.That(oem, Does.Contain("public bool NewDesignStartsEmpty = false;"));
			using (var document = JsonDocument.Parse(Read("StaticData/OEMSettings/Settings.json")))
			{
				Assert.That(document.RootElement.GetProperty("EnableClubShapes").GetBoolean(), Is.True);
				Assert.That(document.RootElement.GetProperty("NewDesignStartsEmpty").GetBoolean(), Is.True);
			}
		}
	}
}
