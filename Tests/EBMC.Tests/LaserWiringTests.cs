using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl.ClubUi;
using MatterHackers.MatterControl.DesignTools;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Export for laser: how it is wired into the app (source checks). It is a file export only: no printer, no G-code,
	/// no slicing, behind OemSettings.EnableLaserExport (off in stock, on for the club).
	/// </summary>
	[TestFixture]
	public class LaserWiringTests
	{
		private static string root;

		[OneTimeSetUp]
		public void FindRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "EBMC.md")))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null, "repository root");
			root = directory.FullName;
		}

		private static string Source(string relative) => File.ReadAllText(Path.Combine(root, relative));

		[Test]
		public void SwitchIsOffInStockAndOnForTheClub()
		{
			Assert.That(Source("MatterControlLib/SettingsManagement/OemSettings.cs"), Does.Contain("public bool EnableLaserExport = false;"));
			Assert.That(Source("Tools/EBMC/generate_ebmc_files.py"), Does.Contain("'EnableLaserExport': True"));
			Assert.That(Source("StaticData/OEMSettings/Settings.json"), Does.Contain("\"EnableLaserExport\": true"));
			Assert.That(Source("MatterControlLib/ApplicationView/LaserTools.cs"), Does.Contain("OemSettings.Instance.EnableLaserExport"));
		}

		[Test]
		public void TheExporterNeverReachesAPrintPath()
		{
			var source = Source("MatterControlLib/Library/Export/LaserExport.cs");
			foreach (var banned in new[] { "GCodeExport", "Slicer", "SliceItem", "PushToPrinter", "OctoPrint", "printer.", "Printer.", "ExportLibraryItems" })
			{
				Assert.That(source, Does.Not.Contain(banned), banned);
			}

			// PrinterConfig appears only in the two Initialize methods the interface requires, and they are empty
			Assert.That(Regex.Matches(source, "PrinterConfig").Count, Is.EqualTo(2));
			Assert.That(Regex.Matches(source, @"public void Initialize\(PrinterConfig printer\)\s*\{\s*\}").Count, Is.EqualTo(2));

			// both exporters are gated by the switch and hide themselves instead of showing a disabled button
			Assert.That(Regex.Matches(source, @"public bool Enabled => LaserTools\.IsEnabled;").Count, Is.EqualTo(2));
			Assert.That(Regex.Matches(source, "public string DisabledReason => \"\";").Count, Is.EqualTo(2));
			Assert.That(Regex.Matches(source, "public int Priority => 5;").Count, Is.EqualTo(2));
		}

		[Test]
		public void NothingToCutWritesNoFile()
		{
			var source = Source("MatterControlLib/Library/Export/LaserExport.cs");
			int empty = source.IndexOf("if (outline.IsEmpty)", System.StringComparison.Ordinal);
			int write = source.IndexOf("File.WriteAllText(", System.StringComparison.Ordinal);
			Assert.That(empty, Is.GreaterThan(0));
			Assert.That(write, Is.GreaterThan(empty), "the empty check comes before the only write");
			Assert.That(source.Substring(empty, write - empty), Does.Contain("LaserExportRules.EmptyId"));
			Assert.That(source, Does.Contain("new UTF8Encoding(false)"));
		}

		[Test]
		public void ThePageExportsWithNoPrinter()
		{
			var page = Source("MatterControlLib/CustomWidgets/LaserExportPage.cs");
			Assert.That(page, Does.Contain("ExportPrintItemPage.DoExport(new[] { item }, null, plugin);"));
			foreach (var banned in new[] { "ExportLibraryItems", "GCodeExport", "PrinterConfig", ".Printer" })
			{
				Assert.That(page, Does.Not.Contain(banned), banned);
			}
		}

		[Test]
		public void OnlyDrawingsGiveTheirPathAndSolidProvidersAreNeverAskedForOne()
		{
			var source = Source("MatterControlLib/Library/Export/LaserExport.cs");
			int solidCheck = source.IndexOf("|| provider.MeshIsSolidObject", System.StringComparison.Ordinal);
			int rawPath = source.IndexOf("provider.GetRawPath()", System.StringComparison.Ordinal);
			Assert.That(solidCheck, Is.GreaterThan(0));
			Assert.That(rawPath, Is.GreaterThan(solidCheck), "a solid path provider returns before GetRawPath is called");
			Assert.That(Regex.Matches(source, @"GetRawPath\(\)").Count, Is.EqualTo(1));
			Assert.That(source, Does.Contain("IsDrawing = true,"));
		}

		[Test]
		public void TheThreadingCommentSaysWhatHappens()
		{
			var source = Source("MatterControlLib/Library/Export/LaserExport.cs");
			Assert.That(source, Does.Not.Contain("Reads the scene on the calling thread"));
			Assert.That(source, Does.Not.Contain("The scene is read on the calling thread"));
			Assert.That(source, Does.Contain("RunningTasksConfig.Execute"));
			Assert.That(source, Does.Contain("Stock STL export reads the scene from the same worker thread"));
		}

		[Test]
		public void OpenPathRunsAreTreatedAsClosedOnPurpose()
		{
			// MatterControl's own Extrude (CreatePolygons) closes every run, so the laser outline matches the mesh
			var source = Source("MatterControlLib/Library/Export/LaserExport.cs");
			Assert.That(source, Does.Contain("Every run of three or"));
			Assert.That(source, Does.Contain("ignores close flags and closes every run"));
			var create = Source("Submodules/agg-sharp/DataConverters2D/VertexSourceToClipperPolygons.cs");
			Assert.That(create, Does.Contain("MoveTo always creates a new polygon"), "the run rule this copies");
		}

		[Test]
		public void OutlineChoiceIsOneSharedSetting()
		{
			var source = Source("MatterControlLib/Library/Export/LaserExport.cs");

			// the per-export choice holds no method or height of its own
			Assert.That(source, Does.Contain("get => LaserExportSettings.Method;"));
			Assert.That(source, Does.Contain("get => LaserExportSettings.SliceHeight;"));
			Assert.That(source, Does.Not.Contain("public LaserOutlineMethod Method = "));
			Assert.That(source, Does.Not.Contain("public double SliceHeight = "));

			// every chooser shows the shared value when it changes elsewhere, and lets go when it closes
			Assert.That(source, Does.Contain("LaserExportSettings.Changed += onChanged;"));
			Assert.That(source, Does.Contain("LaserExportSettings.Changed -= onChanged;"));
			Assert.That(source, Does.Contain("Checked = LaserExportSettings.Method == LaserOutlineMethod.Footprint,"));

			// the page uses the same setting (no copy of its own)
			var page = Source("MatterControlLib/CustomWidgets/LaserExportPage.cs");
			Assert.That(page, Does.Not.Contain(".Method ="));
			Assert.That(page, Does.Not.Contain(".SliceHeight ="));
			Assert.That(page, Does.Contain("LaserOutlineChooser.Create(theme, null)"));
		}

		[Test]
		public void TheEmptyMessageGetsTheReport()
		{
			var source = Source("MatterControlLib/Library/Export/LaserExport.cs");
			Assert.That(source, Does.Contain("LaserExportRules.EmptyMessage(options.Method, outline.Report)"));
		}

		[Test]
		public void NoPublicAbstractExportPlugin()
		{
			// PluginFinder creates every public IExportPlugin class; a public abstract one throws there and drops the
			// export plugins after it in the assembly
			foreach (var file in Directory.GetFiles(Path.Combine(root, "MatterControlLib", "Library", "Export"), "*.cs"))
			{
				var text = File.ReadAllText(file);
				Assert.That(Regex.IsMatch(text, @"public\s+abstract\s+class\s+\w+[^{]*IExport"), Is.False, Path.GetFileName(file));
			}
		}

		[Test]
		public void TheCoreUsesNoAppCode()
		{
			foreach (var file in Directory.GetFiles(Path.Combine(root, "MatterControlLib", "DesignTools", "Laser"), "*.cs"))
			{
				var usings = File.ReadAllLines(file).Where(l => l.StartsWith("using ")).ToList();
				foreach (var line in usings)
				{
					Assert.That(
						line.StartsWith("using System") || line.StartsWith("using ClipperLib") || line.StartsWith("using MatterHackers.MatterControl.DesignTools.ClubShapes")
							|| line.StartsWith("using IntPath") || line.StartsWith("using IntPaths"),
						Is.True,
						$"{Path.GetFileName(file)}: {line}");
				}
			}
		}

		[Test]
		public void CommandSearchOpensTheLaserPageOnDesignTabsOnly()
		{
			Assert.That(CommandSearchRules.WorkspaceWhitelist, Does.Contain("LaserExport"));
			Assert.That(CommandSearchRules.IsForbidden("LaserExport"), Is.False);
			Assert.That(CommandSearchRules.IsForbidden("Export for laser (SVG, DXF)…"), Is.False);

			foreach (var word in new[] { "laser", "svg", "dxf", "lightburn", "xtool", "xcs", "cutter" })
			{
				Assert.That(CommandSearchRules.Synonyms[word], Does.Contain("LaserExport"), word);
			}

			// "cut" keeps meaning Subtract and Plane Cut; "outline" is Outline Path
			Assert.That(CommandSearchRules.Synonyms["cut"], Does.Not.Contain("LaserExport"));
			Assert.That(CommandSearchRules.Synonyms.ContainsKey("outline"), Is.False);

			var popup = Source("MatterControlLib/PartPreviewWindow/CommandSearch/CommandSearchPopup.cs");
			var at = popup.IndexOf("\"LaserExport\",", System.StringComparison.Ordinal);
			Assert.That(at, Is.GreaterThan(0));
			var before = popup.Substring(System.Math.Max(0, at - 300), 300);
			Assert.That(before, Does.Contain("LaserTools.IsEnabled"));
			Assert.That(before, Does.Contain("sceneContext.Printer == null"));
			Assert.That(popup, Does.Contain("() => LaserExportPage.Show(sceneContext)"));
		}

		[Test]
		public void MenusOfferItAfterExport()
		{
			var bed = ClubMenus.Bed.Entries.Select(e => e.Id).ToList();
			Assert.That(bed.IndexOf(ClubMenus.ExportLaser), Is.EqualTo(bed.IndexOf(ClubMenus.Export) + 1));

			var part = ClubMenus.PartContext.Entries.Select(e => e.Id).ToList();
			Assert.That(part.IndexOf(ClubMenus.ExportLaser), Is.EqualTo(part.IndexOf(ClubMenus.ExportPart) + 1));

			// the sources exist only with the switch on, and only on design tabs
			var toolbar = Source("MatterControlLib/PartPreviewWindow/ViewToolBarControls.cs");
			var at = toolbar.IndexOf("sources[ClubMenus.ExportLaser]", System.StringComparison.Ordinal);
			Assert.That(at, Is.GreaterThan(0));
			Assert.That(toolbar.Substring(at - 300, 300), Does.Contain("LaserTools.IsEnabled").And.Contain("sceneContext.Printer == null"));

			var partMenu = Source("MatterControlLib/ApplicationView/ClubUi/ClubSceneMenus.cs");
			at = partMenu.IndexOf("sources[ClubMenus.ExportLaser]", System.StringComparison.Ordinal);
			Assert.That(at, Is.GreaterThan(0));
			Assert.That(partMenu.Substring(at - 300, 300), Does.Contain("designTab").And.Contain("LaserTools.IsEnabled"));
		}
	}
}
