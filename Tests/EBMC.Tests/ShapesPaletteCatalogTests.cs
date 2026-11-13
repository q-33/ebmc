using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl.PartPreviewWindow;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The shapes palette's catalog checked against the code and files it names: every shape is a registered generator,
	/// every tool a registered scene operation, every size property a real property, every glyph a built icon.
	/// </summary>
	[TestFixture]
	public class ShapesPaletteCatalogTests
	{
		private const string PrimitivesPath = "MatterControlLib/Library/Providers/MatterControl/Primitives3DContainer.cs";

		private const string CalibrationPath = "MatterControlLib/Library/Providers/MatterControl/CalibrationPartsContainer.cs";

		private const string OperationsPath = "MatterControlLib/ApplicationView/SceneOperations.cs";

		private static string root;

		private static IEnumerable<PaletteTab> Tabs => new[] { PaletteTab.Design, PaletteTab.Printer };

		private static IEnumerable<PaletteMember> Members(PaletteTab tab) => PaletteCatalog.Families(tab).SelectMany(f => f.Members);

		private static IEnumerable<PaletteMember> AllMembers => Tabs.SelectMany(Members);

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

		/// <summary>The "Name".Localize() generator names in a container source, split by whether they sit inside #if DEBUG.</summary>
		private static (HashSet<string> release, HashSet<string> debugOnly) GeneratorNames(string source)
		{
			var release = new HashSet<string>();
			var debugOnly = new HashSet<string>();
			bool inDebug = false;
			foreach (var line in source.Split('\n'))
			{
				var trimmed = line.Trim();
				if (trimmed.StartsWith("#if DEBUG"))
				{
					inDebug = true;
				}
				else if (trimmed.StartsWith("#endif"))
				{
					inDebug = false;
				}

				foreach (Match match in Regex.Matches(line, "\"([^\"]+)\"\\.Localize\\(\\)"))
				{
					(inDebug ? debugOnly : release).Add(match.Groups[1].Value);
				}
			}

			return (release, debugOnly);
		}

		[Test]
		public void EveryShapeIsARegisteredPrimitive()
		{
			var (release, debugOnly) = GeneratorNames(Read(PrimitivesPath));
			foreach (var member in AllMembers.Where(m => m.Source == PaletteSource.Primitives))
			{
				Assert.That(release.Contains(member.Name) || (member.Optional && debugOnly.Contains(member.Name)), Is.True,
					$"{member.Name} is a GeneratorItem in Primitives3DContainer (release builds unless marked optional)");
			}
		}

		[Test]
		public void QrCodeIsInReleaseBuilds()
		{
			var source = Read(PrimitivesPath);
			var (release, _) = GeneratorNames(source);
			Assert.That(release, Does.Contain("QR Code"));

			// ... but only kept in release builds when the shapes palette is on, so the stock build is unchanged
			Assert.That(source, Does.Contain("if (!SettingsManagement.OemSettings.Instance.UseShapesPalette)"));

			// the club's default QR text comes from the generated OEM settings; the stock default stays in the code
			Assert.That(Read("MatterControlLib/DesignTools/Primitives/QrCodeObject3D.cs"), Does.Contain("\"https://www.matterhackers.com\""));
			using (var document = JsonDocument.Parse(Read("StaticData/OEMSettings/Settings.json")))
			{
				Assert.That(document.RootElement.GetProperty("QrCodeDefaultText").GetString(), Is.EqualTo("https://eastbaymakersclub.com"));
				Assert.That(document.RootElement.GetProperty("ShapesPaletteStyle").GetString(), Is.EqualTo("palette"));
			}
		}

		[Test]
		public void EveryCalibrationPartExists()
		{
			var (release, debugOnly) = GeneratorNames(Read(CalibrationPath));
			foreach (var member in AllMembers.Where(m => m.Source == PaletteSource.CalibrationParts))
			{
				if (member.Name.Contains('.'))
				{
					Assert.That(File.Exists(Path.Combine(root, "StaticData", "OEMSettings", "SampleParts", member.Name)), Is.True, $"sample part {member.Name}");
				}
				else
				{
					Assert.That(release.Contains(member.Name) || (member.Optional && debugOnly.Contains(member.Name)), Is.True,
						$"{member.Name} is a GeneratorItem in CalibrationPartsContainer (release builds unless marked optional)");
				}
			}
		}

		[Test]
		public void EveryToolIsARegisteredSceneOperation()
		{
			var source = Read(OperationsPath);
			foreach (var member in AllMembers.Where(m => m.Source == PaletteSource.SceneOperation))
			{
				Assert.That(source, Does.Contain($"new SceneOperation(\"{member.Name}\")"), member.Name);
			}
		}

		[Test]
		public void CommandsAreKnown()
		{
			foreach (var member in AllMembers.Where(m => m.Source == PaletteSource.Command))
			{
				Assert.That(member.Name, Is.AnyOf(PaletteCatalog.AddModelCommand, PaletteCatalog.SupportsCommand));
			}
		}

		[Test]
		public void SizeChipsScaleRealProperties()
		{
			var primitives = Read(PrimitivesPath);
			foreach (var member in AllMembers.Where(m => m.Scalable))
			{
				Assert.That(primitives, Does.Contain($"{member.ObjectType}.Create()"), $"{member.Name} is made by {member.ObjectType}");
				var classSource = Read($"MatterControlLib/DesignTools/Primitives/{member.ObjectType}.cs");
				foreach (var property in member.ScaledProperties)
				{
					Assert.That(classSource, Does.Contain($"public DoubleOrExpression {property} {{ get; set; }}"), $"{member.ObjectType}.{property}");
				}
			}
		}

		[Test]
		public void DefaultSizesMatchTheGenerators()
		{
			// the default of the first scaled property is the first dimension shown
			foreach (var member in AllMembers.Where(m => m.Scalable))
			{
				var classSource = Read($"MatterControlLib/DesignTools/Primitives/{member.ObjectType}.cs");
				var match = Regex.Match(classSource, $"public DoubleOrExpression {member.ScaledProperties[0]} {{ get; set; }} = ([0-9.]+);");
				Assert.That(match.Success, Is.True, $"{member.ObjectType}.{member.ScaledProperties[0]} has a numeric default");
				Assert.That(double.Parse(match.Groups[1].Value), Is.EqualTo(member.Dimensions[0]), member.Name);
			}
		}

		[Test]
		public void ChipsOnlyWhereSizeMakesSense()
		{
			var withChips = PaletteCatalog.Families(PaletteTab.Design).Where(f => f.HasSizeChips).Select(f => f.Id);
			Assert.That(withChips, Is.EquivalentTo(new[] { "box", "round" }));

			foreach (var family in PaletteCatalog.Families(PaletteTab.Design).Where(f => f.Id == "box" || f.Id == "round"))
			{
				Assert.That(family.Members.All(m => m.Scalable), Is.True, family.Id);
			}

			// single test shapes on the printer tab have no flyout, so no chips either
			Assert.That(PaletteCatalog.Families(PaletteTab.Printer).Where(f => f.HasFlyout).Any(f => f.HasSizeChips), Is.False);
		}

		[TestCase("Cube", 10, "10 × 10 × 10")]
		[TestCase("Cube", 40, "40 × 40 × 40")]
		[TestCase("Half Wedge", 10, "10 × 10 × 5")]
		[TestCase("Cylinder", 40, "Ø40 × 40")]
		[TestCase("Ring", 10, "Ø10 / Ø7.5 × 2.5")]
		[TestCase("Sphere", 20, "Ø20")]
		public void SizeTextFollowsTheChip(string name, int chip, string expected)
		{
			var member = Members(PaletteTab.Design).First(m => m.Name == name);
			Assert.That(member.SizeText(PaletteCatalog.ScaleFor(chip)), Is.EqualTo(expected));
		}

		[Test]
		public void DesignAndPrinterLists()
		{
			Assert.That(PaletteCatalog.HasHoleSwitch(PaletteTab.Design), Is.True);
			Assert.That(PaletteCatalog.HasHoleSwitch(PaletteTab.Printer), Is.False);

			Assert.That(PaletteCatalog.Families(PaletteTab.Design).Select(f => f.Id),
				Is.EqualTo(new[] { "box", "round", "text", "image", "marks", "combine", "align", "mirror", "array", "planecut" }));
			Assert.That(PaletteCatalog.Families(PaletteTab.Printer).Select(f => f.Id),
				Is.EqualTo(new[] { "add", "pcube", "pcylinder", "ptext", "calibration", "arrange", "layflat", "supports", "pmeasure" }));

			// the design tab's tools come after its shapes, behind a divider
			Assert.That(PaletteCatalog.Design.Select(s => s.Label), Is.EqualTo(new[] { "Shapes", "Tools" }));
			Assert.That(PaletteCatalog.Printer[0].Label, Is.Null, "no divider above Add model");

			Assert.That(Members(PaletteTab.Design).Any(m => m.Source == PaletteSource.Command || m.Source == PaletteSource.CalibrationParts), Is.False);
			Assert.That(Members(PaletteTab.Printer).Select(m => m.Name), Does.Not.Contain("Combine"));

			// holes only for shapes that are cut into others
			Assert.That(PaletteCatalog.Families(PaletteTab.Design).Where(f => f.CanBeHole).Select(f => f.Id), Is.EquivalentTo(new[] { "box", "round", "text", "image" }));
			Assert.That(PaletteCatalog.Families(PaletteTab.Printer).Any(f => f.CanBeHole), Is.False);
		}

		[Test]
		public void NoTemperatureTowersOnThePalette()
		{
			// towers write their own hotend temperatures into the print, past the maintainer lock
			Assert.That(AllMembers.Select(m => m.Name), Has.None.Contains("Temperature Tower"));
			Assert.That(PaletteCatalog.Families(PaletteTab.Printer).Single(f => f.Id == "calibration").Members.Select(m => m.Name),
				Is.EqualTo(new[] { "XY Calibration", "Calibration - Box.stl", "Calibration - Circle.stl", "Retraction Cubes.mcx" }));
		}

		[Test]
		public void FamilyIdsAreUnique()
		{
			foreach (var tab in Tabs)
			{
				var ids = PaletteCatalog.Families(tab).Select(f => f.Id).ToList();
				Assert.That(ids, Is.Unique, tab.ToString());
			}
		}

		[Test]
		public void OnlyDebugOnlyItemsAreOptional()
		{
			Assert.That(AllMembers.Where(m => m.Optional).Select(m => m.Name).Distinct(), Is.EqualTo(new[] { "XY Calibration" }));
		}

		[Test]
		public void EveryGlyphIsBuilt()
		{
			using (var document = JsonDocument.Parse(Read("Tools/EBMC/icons/map.json")))
			{
				var icons = document.RootElement.GetProperty("icons");
				foreach (var glyph in Tabs.SelectMany(PaletteCatalog.Glyphs).Distinct())
				{
					var target = $"Shapes/{glyph}.png";
					Assert.That(icons.TryGetProperty(target, out var entry), Is.True, $"{target} is in map.json");
					Assert.That(entry.GetProperty("size").GetInt32(), Is.EqualTo(PaletteCatalog.GlyphSize * 4), target);
					Assert.That(File.Exists(Path.Combine(root, "StaticData", "Icons", "Shapes", glyph + ".png")), Is.True, $"{target} is built");
				}
			}
		}

		[Test]
		public void UserVisibleTextAvoidsTheUpstreamName()
		{
			var texts = Tabs.SelectMany(PaletteCatalog.For).Select(s => s.Label)
				.Concat(Tabs.SelectMany(PaletteCatalog.Families).Select(f => f.Label))
				.Concat(AllMembers.Select(m => m.Title))
				.Concat(AllMembers.Select(m => m.SizeText()))
				.Where(t => t != null);

			foreach (var text in texts)
			{
				Assert.That(text, Does.Not.Contain("MatterControl"));
			}
		}
	}
}
