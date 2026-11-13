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
	/// The Simple · Advanced switch: its rules (default, slice-settings detail mapping), what Simple shows on the palette
	/// and toolbar, and that Advanced is exactly today's lists. Checked against the registry and settings files it names.
	/// </summary>
	[TestFixture]
	public class UiModeTests
	{
		private const string OperationsPath = "MatterControlLib/ApplicationView/SceneOperations.cs";

		private static string root;

		[OneTimeSetUp]
		public void FindRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, OperationsPath)))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null, "repository root above the test assembly");
			root = directory.FullName;
		}

		private static string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));

		// ---- rules ----

		[TestCase("Simple", UiMode.Simple)]
		[TestCase(" simple ", UiMode.Simple)]
		[TestCase("ADVANCED", UiMode.Advanced)]
		public void ParsesStoredValues(string text, UiMode expected)
		{
			Assert.That(UiModeRules.TryParse(text, out var mode), Is.True);
			Assert.That(mode, Is.EqualTo(expected));
			Assert.That(UiModeRules.TryParse(UiModeRules.Format(expected), out var roundTrip) && roundTrip == expected, Is.True);
		}

		[TestCase(null)]
		[TestCase("")]
		[TestCase("Intermediate")]
		[TestCase("Expert")]
		public void RejectsOtherValues(string text)
		{
			Assert.That(UiModeRules.TryParse(text, out _), Is.False);
		}

		[TestCase("Advanced", "Simple", UiMode.Advanced)] // the user's choice wins
		[TestCase("Simple", "Advanced", UiMode.Simple)]
		[TestCase(null, "Simple", UiMode.Simple)] // else the OEM default
		[TestCase("", "Advanced", UiMode.Advanced)]
		[TestCase("junk", "Simple", UiMode.Simple)]
		[TestCase(null, null, UiMode.Advanced)] // else Advanced
		[TestCase(null, "junk", UiMode.Advanced)]
		public void DefaultRule(string stored, string oemDefault, UiMode expected)
		{
			Assert.That(UiModeRules.Resolve(stored, oemDefault), Is.EqualTo(expected));
		}

		[TestCase(UiMode.Simple, null, "Simple")]
		[TestCase(UiMode.Simple, "Advanced", "Simple")]
		[TestCase(UiMode.Advanced, null, "Intermediate")]
		[TestCase(UiMode.Advanced, "Simple", "Intermediate")]
		[TestCase(UiMode.Advanced, "Intermediate", "Intermediate")]
		[TestCase(UiMode.Advanced, "Advanced", "Advanced")]
		public void ModeToSliceSettingsDetail(UiMode mode, string lastNonSimple, string expected)
		{
			Assert.That(UiModeRules.DetailFor(mode, lastNonSimple), Is.EqualTo(expected));
		}

		[TestCase("Simple", UiMode.Simple)]
		[TestCase("Intermediate", UiMode.Advanced)]
		[TestCase("Advanced", UiMode.Advanced)]
		public void SliceSettingsDetailToMode(string detail, UiMode expected)
		{
			Assert.That(UiModeRules.ModeForDetail(detail), Is.EqualTo(expected));
		}

		[Test]
		public void UnknownDetailHasNoMode()
		{
			Assert.That(UiModeRules.ModeForDetail(null), Is.Null);
			Assert.That(UiModeRules.ModeForDetail("Expert"), Is.Null);
		}

		[Test]
		public void DetailAgreement()
		{
			// an unset level shows the Simple list, so it agrees with Simple only
			Assert.That(UiModeRules.DetailAgrees(UiMode.Simple, null), Is.True);
			Assert.That(UiModeRules.DetailAgrees(UiMode.Advanced, null), Is.False);
			Assert.That(UiModeRules.DetailAgrees(UiMode.Simple, "Intermediate"), Is.False);
			Assert.That(UiModeRules.DetailAgrees(UiMode.Advanced, "Intermediate"), Is.True);
			Assert.That(UiModeRules.DetailAgrees(UiMode.Advanced, "Advanced"), Is.True);

			// every mode's own level agrees with it, and the reverse rule gives the mode back
			foreach (var mode in new[] { UiMode.Simple, UiMode.Advanced })
			{
				foreach (var last in new[] { null, "Simple", "Intermediate", "Advanced" })
				{
					var detail = UiModeRules.DetailFor(mode, last);
					Assert.That(UiModeRules.DetailAgrees(mode, detail), Is.True, $"{mode} {last}");
					Assert.That(UiModeRules.ModeForDetail(detail), Is.EqualTo(mode), $"{mode} {last}");
				}
			}
		}

		[Test]
		public void SyncExistingAdvancedUserStartingInSimpleGetsAdvancedBack()
		{
			// an existing user whose Slice Settings showed Advanced, first started with the club default (Simple)
			var (detail, last) = UiModeRules.Sync(UiMode.Simple, "Advanced", null);
			Assert.That(detail, Is.EqualTo("Simple"));
			Assert.That(last, Is.EqualTo("Advanced"), "the Advanced level is remembered");

			// ... later switches to Advanced: the Advanced level comes back
			Assert.That(UiModeRules.Sync(UiMode.Advanced, detail, last), Is.EqualTo(("Advanced", "Advanced")));
		}

		[Test]
		public void SyncFreshUser()
		{
			// nothing stored: Simple already agrees, nothing to write
			Assert.That(UiModeRules.Sync(UiMode.Simple, null, null), Is.EqualTo(((string)null, (string)null)));

			// a fresh user who switches to Advanced gets Intermediate
			Assert.That(UiModeRules.Sync(UiMode.Advanced, null, null), Is.EqualTo(("Intermediate", (string)null)));
		}

		[Test]
		public void SyncIntermediate()
		{
			Assert.That(UiModeRules.Sync(UiMode.Advanced, "Intermediate", null), Is.EqualTo(("Intermediate", "Intermediate")), "agrees, remembered");
			Assert.That(UiModeRules.Sync(UiMode.Simple, "Intermediate", "Advanced"), Is.EqualTo(("Simple", "Intermediate")), "the newest level is remembered");
			Assert.That(UiModeRules.Sync(UiMode.Advanced, "Simple", "Intermediate"), Is.EqualTo(("Intermediate", "Intermediate")));
		}

		[Test]
		public void SyncRunsOnceAtStartupAndNeverWithTheSwitchOff()
		{
			var service = Read("MatterControlLib/ApplicationView/UiModes.cs");

			// not inside the Current getter any more
			var getter = service.Substring(service.IndexOf("public static UiMode Current"), service.IndexOf("public static void InitializeAtStartup") - service.IndexOf("public static UiMode Current"));
			Assert.That(getter, Does.Not.Contain("SyncSliceSettingsDetail"));

			// switch off: InitializeAtStartup returns before touching anything
			Assert.That(service, Does.Contain("if (initialized || !SwitchEnabled)\n\t\t\t{\n\t\t\t\treturn;"));
			Assert.That(service, Does.Contain("UiModeRules.Sync(mode, stored, storedLast)"));

			// called once, from startup, before the main view is built
			var application = Read("MatterControlLib/ApplicationView/Application.cs");
			Assert.That(application.IndexOf("UiModes.InitializeAtStartup();"), Is.GreaterThan(0));
			Assert.That(application.IndexOf("UiModes.InitializeAtStartup();"), Is.LessThan(application.IndexOf("applicationController.MainView = new MainViewWidget")));
		}

		[Test]
		public void ModeSwitchWaitsForTheRebuildAndAnyTaskARebuildCouldDisturb()
		{
			var service = Read("MatterControlLib/ApplicationView/UiModes.cs");
			Assert.That(service, Does.Contain("var tasks = controller.Tasks.RunningTasks.Select(t => (t.Title, t.Options?.AllowsWindowRebuild == true));"), "every running task, unless it is marked as safe for a rebuild");
			Assert.That(service, Does.Contain("UiModeBusyRules.Reason(slicing, controller.IsReloading || reloadPending, tasks, s => s.Localize())"));
			Assert.That(service, Does.Contain("reloadPending = true;"));
			Assert.That(service, Does.Contain("finally\n\t\t\t\t{\n\t\t\t\t\treloadPending = false;"));
		}

		[Test]
		public void SimpleKeepsTheBedToggle()
		{
			var view = Read("MatterControlLib/PartPreviewWindow/View3D/View3DWidget.cs");
			Assert.That(view, Does.Contain("var bedButtonOffset = fullViewControls ? 18 * scale : 0;"));
			Assert.That(view, Does.Contain("AddRoundButton(bedButton, new Vector2((cubeCenterFromRightTop.X + bedButtonOffset - bedButton.Width / 2) / scale, startHeight));"));
			Assert.That(view, Does.Contain("AddRoundButton(printAreaButton, new Vector2((cubeCenterFromRightTop.X - 18 * scale - bedButton.Width / 2) / scale, startHeight)).Visible = fullViewControls;"));
		}

		[Test]
		public void TheDetailLevelsAreTheOnesTheSliceSettingsPanelReads()
		{
			// SliceSettingsWidget picks the layout section from exactly these three strings
			var widget = Read("MatterControlLib/SlicerConfiguration/SliceSettingsWidget.cs");
			foreach (var level in new[] { UiModeRules.DetailSimple, UiModeRules.DetailIntermediate, UiModeRules.DetailAdvanced })
			{
				Assert.That(widget, Does.Contain($"case \"{level}\":"), level);
			}

			// no "More" pill while the switch is on, and the View menu goes through the mode
			Assert.That(widget, Does.Contain("&& !UiModes.SwitchEnabled"));
			Assert.That(widget, Does.Contain("if (UiModes.PickSliceSettingsDetail(level))"));
		}

		// ---- palette ----

		private static IEnumerable<string> Ids(PaletteTab tab, bool withSolids, UiMode mode) =>
			PaletteCatalog.For(tab, withSolids, mode).SelectMany(s => s.Families).Select(f => f.Id);

		private static IEnumerable<PaletteMember> Members(PaletteTab tab, bool withSolids, UiMode mode) =>
			PaletteCatalog.For(tab, withSolids, mode).SelectMany(s => s.Families).SelectMany(f => f.Members);

		[Test]
		public void SimpleDesignPalette()
		{
			foreach (var withSolids in new[] { false, true })
			{
				Assert.That(Ids(PaletteTab.Design, withSolids, UiMode.Simple), Is.EqualTo(new[] { "box", "round", "text", "image", "measure" }));

				var names = Members(PaletteTab.Design, withSolids, UiMode.Simple).Select(m => m.Name).ToList();
				Assert.That(names, Does.Contain("Measure Tool"));
				Assert.That(names, Has.None.EqualTo("Description").And.None.EqualTo("Variable Sheet"));
				Assert.That(names, Has.None.EqualTo("Solid Box").And.None.EqualTo("Solid Cylinder").And.None.EqualTo("Plate with Hole"));

				// no modelling tools: Combine, Align, Mirror, Array, Plane Cut are all scene operations
				Assert.That(Members(PaletteTab.Design, withSolids, UiMode.Simple).Any(m => m.Source == PaletteSource.SceneOperation), Is.False);
				Assert.That(PaletteCatalog.For(PaletteTab.Design, withSolids, UiMode.Simple), Has.None.SameAs(PaletteCatalog.Solids));
			}

			Assert.That(PaletteCatalog.Families(PaletteTab.Design, UiMode.Simple).Single(f => f.Id == "measure").Members.Select(m => m.Name),
				Is.EqualTo(new[] { "Measure Tool" }));

			// the shape families are the same as Advanced (same members, flyouts, holes and size chips)
			foreach (var id in new[] { "box", "round", "text", "image" })
			{
				Assert.That(PaletteCatalog.Families(PaletteTab.Design, UiMode.Simple).Single(f => f.Id == id),
					Is.SameAs(PaletteCatalog.Families(PaletteTab.Design).Single(f => f.Id == id)), id);
			}

			Assert.That(PaletteCatalog.HasHoleSwitch(PaletteTab.Design), Is.True, "the Solid/Hole switch stays in Simple");
		}

		[Test]
		public void SimplePrinterPalette()
		{
			foreach (var withSolids in new[] { false, true })
			{
				Assert.That(Ids(PaletteTab.Printer, withSolids, UiMode.Simple), Is.EqualTo(new[] { "add", "arrange", "layflat", "supports" }));
			}

			var names = Members(PaletteTab.Printer, false, UiMode.Simple).Select(m => m.Name).ToList();
			Assert.That(names, Has.None.EqualTo("Measure Tool").And.None.EqualTo("Cube").And.None.EqualTo("Cylinder").And.None.EqualTo("Text"));
			Assert.That(Members(PaletteTab.Printer, false, UiMode.Simple).Any(m => m.Source == PaletteSource.CalibrationParts), Is.False);
			Assert.That(PaletteCatalog.PrinterSimple[0].Label, Is.Null, "no divider above Add model");
		}

		[Test]
		public void AdvancedIsTodaysPaletteExactly()
		{
			foreach (var tab in new[] { PaletteTab.Design, PaletteTab.Printer })
			{
				foreach (var withSolids in new[] { false, true })
				{
					Assert.That(PaletteCatalog.For(tab, withSolids, UiMode.Advanced), Is.EqualTo(PaletteCatalog.For(tab, withSolids)), $"{tab} {withSolids}");
				}
			}

			Assert.That(PaletteCatalog.For(PaletteTab.Design, false, UiMode.Advanced), Is.SameAs(PaletteCatalog.Design));
			Assert.That(PaletteCatalog.For(PaletteTab.Printer, true, UiMode.Advanced), Is.SameAs(PaletteCatalog.Printer));
			Assert.That(Ids(PaletteTab.Design, false, UiMode.Advanced),
				Is.EqualTo(new[] { "box", "round", "text", "image", "marks", "combine", "align", "mirror", "array", "planecut" }));
			Assert.That(Ids(PaletteTab.Design, true, UiMode.Advanced),
				Is.EqualTo(new[] { "box", "round", "text", "image", "marks", "solids", "combine", "align", "mirror", "array", "planecut" }));
			Assert.That(Ids(PaletteTab.Printer, false, UiMode.Advanced),
				Is.EqualTo(new[] { "add", "pcube", "pcylinder", "ptext", "calibration", "arrange", "layflat", "supports", "pmeasure" }));
		}

		[Test]
		public void LabelsAlwaysOnOnlyForDesignTabsInSimple()
		{
			Assert.That(PaletteCatalog.LabelsAlwaysShown(PaletteTab.Design, UiMode.Simple), Is.True);
			Assert.That(PaletteCatalog.LabelsAlwaysShown(PaletteTab.Printer, UiMode.Simple), Is.False);
			Assert.That(PaletteCatalog.LabelsAlwaysShown(PaletteTab.Design, UiMode.Advanced), Is.False);
			Assert.That(PaletteCatalog.LabelsAlwaysShown(PaletteTab.Printer, UiMode.Advanced), Is.False);
		}

		[Test]
		public void SimpleFamilyIdsAreUniqueAndTextAvoidsTheUpstreamName()
		{
			foreach (var tab in new[] { PaletteTab.Design, PaletteTab.Printer })
			{
				var families = PaletteCatalog.Families(tab, UiMode.Simple).ToList();
				Assert.That(families.Select(f => f.Id), Is.Unique, tab.ToString());
				foreach (var text in families.Select(f => f.Label).Concat(families.SelectMany(f => f.Members).Select(m => m.Title)))
				{
					Assert.That(text, Does.Not.Contain("MatterControl"));
				}

				// every Simple glyph is one Advanced already loads (so it is built)
				var advancedGlyphs = PaletteCatalog.Glyphs(tab).ToList();
				foreach (var member in families.SelectMany(f => f.Members))
				{
					Assert.That(advancedGlyphs, Does.Contain(member.Glyph), member.Name);
				}
			}
		}

		[Test]
		public void PaletteAsksForTheModesLists()
		{
			var palette = Read("MatterControlLib/PartPreviewWindow/ShapesPalette/ShapesPalette.cs");
			Assert.That(palette, Does.Contain("PaletteCatalog.For(tab, DesignTools.Solid.SolidModeling.IsEnabled, mode, DesignTools.ClubShapes.ClubShapesFeature.IsEnabled)"));
			Assert.That(palette, Does.Contain("PaletteCatalog.LabelsAlwaysShown(tab, mode)"));
		}

		// ---- toolbar ----

		/// <summary>The Ids of the operations the registry wraps in SimpleToo(...), resolved through their factory methods.</summary>
		private static List<string> SimpleRegistryIds()
		{
			var source = Read(OperationsPath);
			var ids = new List<string>();
			foreach (Match match in Regex.Matches(source, @"SimpleToo\((\w+)\(\)\)"))
			{
				var factory = match.Groups[1].Value;
				var idMatch = Regex.Match(source, @"static SceneOperation " + factory + @"\(\)\s*\{\s*return new SceneOperation\(""([^""]+)""\)");
				Assert.That(idMatch.Success, Is.True, $"{factory} creates a SceneOperation with an Id");
				ids.Add(idMatch.Groups[1].Value);
			}

			return ids;
		}

		/// <summary>The marked operations that sit inside an operation group (shown on their own on design tabs only).</summary>
		private static List<string> GroupedSimpleIds()
		{
			var source = Read(OperationsPath);
			var grouped = new List<string>();
			foreach (Match match in Regex.Matches(source, @"new OperationGroup\(""[^""]+""\)\s*\{(?:[^{}]|\{[^{}]*\})*\}", RegexOptions.Singleline))
			{
				foreach (Match marked in Regex.Matches(match.Value, @"SimpleToo\((\w+)\(\)\)"))
				{
					var factory = marked.Groups[1].Value;
					grouped.Add(Regex.Match(source, @"static SceneOperation " + factory + @"\(\)\s*\{\s*return new SceneOperation\(""([^""]+)""\)").Groups[1].Value);
				}
			}

			return grouped;
		}

		[Test]
		public void RegistryMarksExactlyTheSimpleOperations()
		{
			// The Simple toolbar, right-click menu and properties panel all follow the registry's ShowInSimpleMode marks
			// (there is no second list). This is the spec those marks must meet.
			var marked = SimpleRegistryIds();
			Assert.That(marked, Is.EquivalentTo(new[] { "ArrangeAllParts", "Lay Flat", "Group", "Ungroup", "Duplicate", "Remove", "Align", "Scale" }));

			// Align (Placement) and Scale (Transform) are the grouped ones: design tabs show them on their own, printer tabs
			// (which promote no grouped operation) leave them out
			Assert.That(GroupedSimpleIds(), Is.EquivalentTo(new[] { "Align", "Scale" }));

			// Mirror stays Advanced-only
			Assert.That(marked, Does.Not.Contain("Mirror"));

			// groups are never marked: in Simple they are hidden
			Assert.That(Read(OperationsPath), Does.Not.Contain("SimpleToo(new OperationGroup"));
			Assert.That(Read("MatterControlLib/CustomWidgets/SceneOperation.cs"), Does.Contain("public bool ShowInSimpleMode { get; set; } = false;"));
			Assert.That(Read(OperationsPath), Does.Contain("return Enumerable.Empty<SceneOperation>();"), "printer tabs promote no grouped operation");

			// the properties panel's primary buttons follow the same marks in Simple
			var panel = Read("MatterControlLib/PartPreviewWindow/SelectedObjectPanel.cs");
			Assert.That(panel, Does.Contain("if (UiModes.IsSimple)"));
			Assert.That(panel, Does.Contain("primaryOperations.Where(operation => operation != null && operation.ShowInSimpleMode)"));
		}

		[Test]
		public void TheSecondListIsGone()
		{
			Assert.That(Read("MatterControlLib/PartPreviewWindow/ShapesPalette/PaletteCatalog.cs"), Does.Not.Contain("SimpleToolbarOperations"));
		}

		[Test]
		public void SimpleFilterSitsWhereThePrinterTrimDoes()
		{
			// toolbar, Tools options menu, right-click Modify menu and the properties Action menu
			var toolbar = Read("MatterControlLib/PartPreviewWindow/ViewToolBarControls.cs");
			Assert.That(toolbar, Does.Contain("bool simple = UiModes.IsSimple;"));
			Assert.That(toolbar, Does.Contain("SceneOperations.ShowInSimple(namedAction)"));
			Assert.That(toolbar, Does.Contain("Visible = !simple,"));

			var operations = Read(OperationsPath);
			Assert.That(operations, Does.Contain("bool simple = UiModes.IsSimple;"));
			Assert.That(operations, Does.Contain("SimpleGroupedOperations(simpleGroup, printerTab)"));
		}

		// ---- OEM switch ----

		[Test]
		public void SwitchIsOffInStockAndOnForTheClub()
		{
			var oem = Read("MatterControlLib/SettingsManagement/OemSettings.cs");
			Assert.That(oem, Does.Contain("public bool ShowUiModeSwitch = false;"));
			Assert.That(oem, Does.Contain("public string DefaultUiMode = \"Advanced\";"));

			using (var document = JsonDocument.Parse(Read("StaticData/OEMSettings/Settings.json")))
			{
				Assert.That(document.RootElement.GetProperty("ShowUiModeSwitch").GetBoolean(), Is.True);
				Assert.That(document.RootElement.GetProperty("DefaultUiMode").GetString(), Is.EqualTo("Simple"));
			}

			var generator = Read("Tools/EBMC/generate_ebmc_files.py");
			Assert.That(generator, Does.Contain("'ShowUiModeSwitch': True"));
			Assert.That(generator, Does.Contain("'DefaultUiMode': 'Simple'"));

			// with the switch off the mode is always Advanced (stock behaviour)
			var service = Read("MatterControlLib/ApplicationView/UiModes.cs");
			Assert.That(service, Does.Contain("if (!SwitchEnabled)\n\t\t\t\t{\n\t\t\t\t\treturn UiMode.Advanced;"));
		}
	}
}
