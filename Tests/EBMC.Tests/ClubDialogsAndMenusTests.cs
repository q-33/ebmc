using System.Collections.Generic;
using System.IO;
using System.Linq;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.ClubUi;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The club dialog template and menu style (OemSettings.ClubDialogsAndMenus): which button Enter presses, the footer
	/// order, the menu descriptions the UI builds (order, sections, danger last), menu wording and the toast rules.
	/// </summary>
	[TestFixture]
	public class ClubDialogsAndMenusTests
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

		private static PageActionStyle[] S(params PageActionStyle[] styles) => styles;

		// ---- Enter default ----

		[Test]
		public void PlainConfirmationEnterPressesThePrimary()
		{
			var styles = S(PageActionStyle.Primary, PageActionStyle.Cancel);
			Assert.That(ClubDialogRules.DefaultButton(styles, safeDefault: false), Is.EqualTo(0));
		}

		[Test]
		public void DestructiveConfirmationEnterPressesTheSafeButton()
		{
			var styles = S(PageActionStyle.Destructive, PageActionStyle.Cancel);
			Assert.That(ClubDialogRules.DefaultButton(styles, safeDefault: false), Is.EqualTo(1));
		}

		[Test]
		public void DestructiveButtonIsNeverTheDefault()
		{
			var cases = new[]
			{
				S(PageActionStyle.Destructive),
				S(PageActionStyle.Destructive, PageActionStyle.Cancel),
				S(PageActionStyle.Aside, PageActionStyle.Destructive, PageActionStyle.Secondary),
				S(PageActionStyle.Primary, PageActionStyle.Destructive, PageActionStyle.Cancel),
			};

			foreach (var styles in cases)
			{
				foreach (var safe in new[] { false, true })
				{
					int index = ClubDialogRules.DefaultButton(styles, safe);
					Assert.That(index < 0 || styles[index] != PageActionStyle.Destructive, string.Join(",", styles));
				}
			}
		}

		[Test]
		public void SafeDefaultWithoutVisibleCancelHasNoDefault()
		{
			Assert.That(ClubDialogRules.DefaultButton(S(PageActionStyle.Destructive, PageActionStyle.Secondary), false), Is.EqualTo(-1));
			Assert.That(ClubDialogRules.DefaultButton(S(PageActionStyle.Destructive), false), Is.EqualTo(-1));

			// a safe-default message box whose Cancel is hidden: its affirmative (styled Secondary) must not take Enter
			Assert.That(ClubDialogRules.DefaultButton(S(PageActionStyle.Secondary, PageActionStyle.Aside), safeDefault: true), Is.EqualTo(-1));
		}

		[Test]
		public void SafeDefaultMovesEnterToCancelEvenWithoutDestructive()
		{
			// the hardware error box: "Resume print" (secondary), "Export print log" (aside), "Stay paused" (cancel)
			var styles = S(PageActionStyle.Secondary, PageActionStyle.Aside, PageActionStyle.Cancel);
			Assert.That(ClubDialogRules.DefaultButton(styles, safeDefault: true), Is.EqualTo(2));
		}

		[Test]
		public void NoHighlightMeansNoDefault()
		{
			// YES_NO_WITHOUT_HIGHLIGHT: nothing is primary, so Enter does nothing (as stock)
			var resolved = ClubDialogRules.ResolveStyles(S(PageActionStyle.Default, PageActionStyle.Cancel), new[] { false, false });
			Assert.That(resolved, Is.EqualTo(S(PageActionStyle.Secondary, PageActionStyle.Cancel)));
			Assert.That(ClubDialogRules.DefaultButton(resolved, false), Is.EqualTo(-1));
		}

		[Test]
		public void ResolveStylesFollowsTheStockFirstHighlightedRule()
		{
			// stock AddPageAction counts only highlighted calls: the first highlighted action is the accept button
			var resolved = ClubDialogRules.ResolveStyles(
				S(PageActionStyle.Default, PageActionStyle.Default, PageActionStyle.Default),
				new[] { false, true, true });
			Assert.That(resolved, Is.EqualTo(S(PageActionStyle.Secondary, PageActionStyle.Primary, PageActionStyle.Secondary)));

			// an explicit primary wins; no default action is promoted
			resolved = ClubDialogRules.ResolveStyles(S(PageActionStyle.Default, PageActionStyle.Primary), new[] { true, false });
			Assert.That(resolved, Is.EqualTo(S(PageActionStyle.Secondary, PageActionStyle.Primary)));
		}

		[Test]
		public void ReadyToPrintKeepsStartPrintAsDefault()
		{
			// OctoPrintSender's box: YES_NO, no club options -> the affirmative ("Start print") stays the Enter default
			var resolved = ClubDialogRules.ResolveStyles(S(PageActionStyle.Default, PageActionStyle.Cancel), new[] { true, false });
			Assert.That(ClubDialogRules.DefaultButton(resolved, false), Is.EqualTo(0));
			Assert.That(ClubDialogRules.MessageBoxSeverity(DialogSeverity.Auto, isQuestion: true, destructive: false), Is.EqualTo(DialogSeverity.Question));
		}

		// ---- footer ----

		[Test]
		public void FooterPutsAsideLeftAndPrimaryRight()
		{
			var styles = S(PageActionStyle.Primary, PageActionStyle.Aside, PageActionStyle.Secondary, PageActionStyle.Cancel);
			Assert.That(ClubDialogRules.FooterOrder(styles), Is.EqualTo(new[] { 1, 2, 3, 0 }));
			Assert.That(ClubDialogRules.AsideCount(styles), Is.EqualTo(1));

			var destructive = S(PageActionStyle.Destructive, PageActionStyle.Cancel);
			Assert.That(ClubDialogRules.FooterOrder(destructive), Is.EqualTo(new[] { 1, 0 }));
		}

		[Test]
		public void WindowTitleIsTheDialogsOwnTitle()
		{
			Assert.That(ClubDialogRules.WindowTitle("Delete Printer?", "EBMC"), Is.EqualTo("Delete Printer?"));
			Assert.That(ClubDialogRules.WindowTitle("  ", "EBMC"), Is.EqualTo("EBMC"));
			Assert.That(ClubDialogRules.WindowTitle(null, "EBMC"), Is.EqualTo("EBMC"));
		}

		[Test]
		public void MessageBoxSeverityDefaults()
		{
			Assert.That(ClubDialogRules.MessageBoxSeverity(DialogSeverity.Auto, false, false), Is.EqualTo(DialogSeverity.Info));
			Assert.That(ClubDialogRules.MessageBoxSeverity(DialogSeverity.Auto, true, true), Is.EqualTo(DialogSeverity.Danger));
			Assert.That(ClubDialogRules.MessageBoxSeverity(DialogSeverity.Warning, true, true), Is.EqualTo(DialogSeverity.Warning));
		}

		// ---- menus ----

		private static string[] Rows(ClubMenuSpec spec, System.Func<string, bool> shown) =>
			ClubMenuLayout.Arrange(spec, shown).Select(r => r.ToString()).ToArray();

		[Test]
		public void PrinterOptionsMenuOrder()
		{
			var rows = Rows(ClubMenus.PrinterOptions, id => true);
			Assert.That(rows, Is.EqualTo(new[]
			{
				"#Show", ClubMenus.ShowPrinter, ClubMenus.ShowControls, ClubMenus.ShowTerminal,
				"-",
				"#Maintenance", ClubMenus.Calibrate, ClubMenus.UpdateSettings, ClubMenus.RestoreSettings, ClubMenus.ImportSettings, ClubMenus.ExportSettings, ClubMenus.Eeprom,
				"-",
				ClubMenus.MaintainerLock,
				"-",
				ClubMenus.ResetSettings, ClubMenus.DeletePrinter,
			}));
		}

		[Test]
		public void PrinterOptionsWithoutLockOrFffItems()
		{
			// no maintainer lock (LockEnabled false) and a non-FFF printer (no Controls/Terminal): no empty sections
			var hidden = new HashSet<string> { ClubMenus.MaintainerLock, ClubMenus.ShowControls, ClubMenus.ShowTerminal };
			var rows = Rows(ClubMenus.PrinterOptions, id => !hidden.Contains(id));
			Assert.That(rows, Is.EqualTo(new[]
			{
				"#Show", ClubMenus.ShowPrinter,
				"-",
				"#Maintenance", ClubMenus.Calibrate, ClubMenus.UpdateSettings, ClubMenus.RestoreSettings, ClubMenus.ImportSettings, ClubMenus.ExportSettings, ClubMenus.Eeprom,
				"-",
				ClubMenus.ResetSettings, ClubMenus.DeletePrinter,
			}));
		}

		[Test]
		public void BedMenuOrderKeepsPrintWhereItWas()
		{
			var rows = Rows(ClubMenus.Bed, id => true);
			Assert.That(rows, Is.EqualTo(new[]
			{
				ClubMenus.OpenRecent, ClubMenus.AddFile,
				"-", ClubMenus.Edit,
				"-", ClubMenus.Print,
				"-", ClubMenus.Save, ClubMenus.SaveAs, ClubMenus.Export,
				"-", ClubMenus.ArrangeAll,
				"-", ClubMenus.ClearBed,
			}));

			// the stock Bed menu's items keep their relative order: Open recent, Edit, Print, Export, Clear bed
			var stock = new[] { ClubMenus.OpenRecent, ClubMenus.Edit, ClubMenus.Print, ClubMenus.Export, ClubMenus.ClearBed };
			var positions = stock.Select(id => System.Array.IndexOf(rows, id)).ToArray();
			Assert.That(positions, Is.Ordered);
		}

		[Test]
		public void BedRightClickKeepsStockRelativeOrder()
		{
			var rows = Rows(ClubMenus.BedContext, id => true);
			var stock = new[] { ClubMenus.Paste, ClubMenus.Save, ClubMenus.SaveAs, ClubMenus.Export, ClubMenus.Print, ClubMenus.ArrangeAll, ClubMenus.ClearBed };
			Assert.That(stock.Select(id => System.Array.IndexOf(rows, id)).ToArray(), Is.Ordered);
			Assert.That(rows.Last(), Is.EqualTo(ClubMenus.ClearBed));
		}

		[Test]
		public void DangerItemsAreLastInEveryMenu()
		{
			foreach (var spec in new[] { ClubMenus.PrinterOptions, ClubMenus.Bed, ClubMenus.BedContext, ClubMenus.PartContext, ClubMenus.Library, ClubMenus.Brand })
			{
				var rows = ClubMenuLayout.Arrange(spec, id => true);
				var items = rows.Where(r => r.Kind == ClubMenuRowKind.Item).ToList();
				int firstDanger = items.FindIndex(r => r.Entry.Danger);
				if (firstDanger >= 0)
				{
					Assert.That(items.Skip(firstDanger).All(r => r.Entry.Danger), spec.Name);
					Assert.That(rows[rows.ToList().IndexOf(items[firstDanger]) - 1].Kind, Is.EqualTo(ClubMenuRowKind.Separator), spec.Name);
				}

				// no separator at either end or twice in a row
				Assert.That(rows.First().Kind, Is.Not.EqualTo(ClubMenuRowKind.Separator), spec.Name);
				Assert.That(rows.Last().Kind, Is.Not.EqualTo(ClubMenuRowKind.Separator), spec.Name);
				for (int i = 1; i < rows.Count; i++)
				{
					Assert.That(!(rows[i].Kind == ClubMenuRowKind.Separator && rows[i - 1].Kind == ClubMenuRowKind.Separator), spec.Name);
				}

				// ids are unique
				Assert.That(spec.Entries.Select(e => e.Id).Distinct().Count(), Is.EqualTo(spec.Entries.Count), spec.Name);
			}
		}

		[Test]
		public void PartMenuWithOnlyClipboardAndDelete()
		{
			// an immutable part: no quick tools, rename or sub-menus
			var shown = new HashSet<string> { ClubMenus.Cut, ClubMenus.Copy, ClubMenus.Paste, ClubMenus.PasteInto, ClubMenus.SaveAs, ClubMenus.ExportPart, ClubMenus.Delete };
			var rows = Rows(ClubMenus.PartContext, shown.Contains);
			Assert.That(rows, Is.EqualTo(new[]
			{
				ClubMenus.Cut, ClubMenus.Copy, ClubMenus.Paste, ClubMenus.PasteInto,
				"-", ClubMenus.SaveAs, ClubMenus.ExportPart,
				"-", ClubMenus.Delete,
			}));
		}

		[Test]
		public void CollapseSeparatorsAroundHiddenItems()
		{
			// rows: sep, A(hidden), sep, B, sep, C(hidden), sep, D, sep
			var isSeparator = new[] { true, false, true, false, true, false, true, false, true };
			var isShown = new[] { false, false, false, true, false, false, false, true, false };
			var keep = ClubMenuLayout.CollapseSeparators(isSeparator, isShown);
			Assert.That(keep, Is.EqualTo(new[] { false, false, false, true, true, false, false, true, false }));

			// nothing shown: nothing kept
			Assert.That(ClubMenuLayout.CollapseSeparators(new[] { true, false, true }, new[] { false, false, false }).Any(k => k), Is.False);
		}

		[Test]
		public void EllipsisRule()
		{
			Assert.That(ClubMenuText.WithEllipsis("Export", true), Is.EqualTo("Export…"));
			Assert.That(ClubMenuText.WithEllipsis("Update Settings...", true), Is.EqualTo("Update Settings…"));
			Assert.That(ClubMenuText.WithEllipsis("Save as…", true), Is.EqualTo("Save as…"));
			Assert.That(ClubMenuText.WithEllipsis("Clear bed...", false), Is.EqualTo("Clear bed"));
			Assert.That(ClubMenuText.WithEllipsis("", true), Is.EqualTo(""));
		}

		[Test]
		public void ShortcutTextUsesTheComputersKeys()
		{
			Assert.That(ClubMenuText.ShortcutText("Ctrl+S", mac: true), Is.EqualTo("Cmd + S"));
			Assert.That(ClubMenuText.ShortcutText("Ctrl + S", mac: false), Is.EqualTo("Ctrl + S"));
			Assert.That(ClubMenuText.ShortcutText("Delete", mac: true), Is.EqualTo("Delete"));
		}

		[Test]
		public void MenuShortcutsExistInTheShortcutTable()
		{
			// a hint must name a key that really does that (ShortcutTable is checked against the handlers elsewhere)
			var keys = ShortcutTable.All.SelectMany(e => e.Keys.Split(',')).Select(k => k.Trim()).ToHashSet();
			foreach (var spec in new[] { ClubMenus.Bed, ClubMenus.BedContext, ClubMenus.PartContext, ClubMenus.Brand })
			{
				foreach (var entry in spec.Entries.Where(e => e.Shortcut != null))
				{
					Assert.That(keys, Does.Contain(entry.Shortcut), $"{spec.Name}: {entry.Id}");
				}
			}
		}

		[Test]
		public void ShowOnDiskWording()
		{
			Assert.That(ClubMenuText.ShowOnDiskTitle(mac: true, windows: false), Is.EqualTo("Show in Finder"));
			Assert.That(ClubMenuText.ShowOnDiskTitle(mac: false, windows: true), Is.EqualTo("Show in Explorer"));
			Assert.That(ClubMenuText.ShowOnDiskTitle(mac: false, windows: false), Is.EqualTo("Show in Files"));
		}

		[Test]
		public void MenuTitlesAreSentenceCase()
		{
			foreach (var spec in new[] { ClubMenus.PrinterOptions, ClubMenus.Bed, ClubMenus.BedContext, ClubMenus.PartContext, ClubMenus.Library, ClubMenus.Brand })
			{
				foreach (var entry in spec.Entries.Where(e => e.Title != null))
				{
					var words = entry.Title.Split(' ').Skip(1).Where(w => w.Length > 1 && char.IsLetter(w[0]));
					var allowed = new[] { "EEPROM", "G-code", "Library", "EBMC" };
					foreach (var word in words.Where(w => !allowed.Any(a => w.Trim('(', ')') == a)))
					{
						Assert.That(char.IsLower(word[0]), $"{spec.Name}: '{entry.Title}'");
					}
				}
			}
		}

		[Test]
		public void TheUiBuildsTheseDescriptions()
		{
			// every Printer Options and Bed entry is mapped by the menu code that builds it
			var printerBar = Source("MatterControlLib/PartPreviewWindow/View3D/PrinterBar/PrinterActionsBar.cs");
			Assert.That(printerBar, Does.Contain("ClubMenuBuilder.Build(popupMenu, ClubMenus.PrinterOptions, sources)"));
			foreach (var entry in ClubMenus.PrinterOptions.Entries)
			{
				Assert.That(printerBar, Does.Contain("ID = ClubMenus." + entry.Id), entry.Id);
			}

			var toolbar = Source("MatterControlLib/PartPreviewWindow/ViewToolBarControls.cs");
			Assert.That(toolbar, Does.Contain("ClubMenuBuilder.Build(popupMenu, ClubMenus.Bed, sources)"));
			foreach (var entry in ClubMenus.Bed.Entries)
			{
				Assert.That(toolbar, Does.Contain("[ClubMenus." + entry.Id + "]"), entry.Id);
			}
		}

		[Test]
		public void SwitchIsOffInStockAndOnForTheClub()
		{
			Assert.That(Source("MatterControlLib/SettingsManagement/OemSettings.cs"), Does.Contain("public bool ClubDialogsAndMenus = false;"));
			Assert.That(Source("Tools/EBMC/generate_ebmc_files.py"), Does.Contain("'ClubDialogsAndMenus': True"));
			Assert.That(Source("StaticData/OEMSettings/Settings.json"), Does.Contain("\"ClubDialogsAndMenus\": true"));
		}

		// ---- toasts ----

		[Test]
		public void ToastDuration()
		{
			Assert.That(ClubToastRules.DisplaySeconds(10), Is.EqualTo(6));
			Assert.That(ClubToastRules.DisplaySeconds(0), Is.EqualTo(6));
			Assert.That(ClubToastRules.DisplaySeconds(2), Is.EqualTo(4));
			Assert.That(ClubToastRules.DisplaySeconds(8), Is.EqualTo(8));
			Assert.That(ClubToastRules.DisplaySeconds(60), Is.EqualTo(12));
		}

		[Test]
		public void AtMostThreeToasts()
		{
			Assert.That(ClubToastRules.ToRemoveBeforeAdding(0), Is.EqualTo(0));
			Assert.That(ClubToastRules.ToRemoveBeforeAdding(2), Is.EqualTo(0));
			Assert.That(ClubToastRules.ToRemoveBeforeAdding(3), Is.EqualTo(1));
			Assert.That(ClubToastRules.ToRemoveBeforeAdding(5), Is.EqualTo(3));
		}

		[Test]
		public void EllipsizeFitsTheWidth()
		{
			// one unit per character
			System.Func<string, double> measure = t => t.Length;
			Assert.That(ClubMenuText.Ellipsize("Properties", 10, measure), Is.EqualTo("Properties"));
			Assert.That(ClubMenuText.Ellipsize("Properties", 6, measure), Is.EqualTo("Prope\u2026"));
			Assert.That(ClubMenuText.Ellipsize("Mirror Copy Name Tag", 8, measure), Is.EqualTo("Mirror\u2026"));
			Assert.That(ClubMenuText.Ellipsize("Name", 0, measure), Is.EqualTo("\u2026"));
			Assert.That(ClubMenuText.Ellipsize("", 0, measure), Is.EqualTo(""));
		}

		[Test]
		public void BrandMenuOffersFindATool()
		{
			var rows = ClubMenuLayout.Arrange(ClubMenus.Brand, id => true).Select(r => r.ToString()).ToList();
			Assert.That(rows.IndexOf(ClubMenus.FindTool), Is.EqualTo(rows.IndexOf(ClubMenus.OpenFile) + 1));
			Assert.That(ClubMenus.Brand.Find(ClubMenus.FindTool).Shortcut, Is.EqualTo("Ctrl + K"));
			var brand = Source("MatterControlLib/ApplicationView/BrandMenuButton.cs");
			Assert.That(brand, Does.Contain("sources[ClubMenus.FindTool]"));
			Assert.That(brand, Does.Contain("UiThread.RunOnIdle(CommandSearchPopup.Toggle, .2)"));
		}
	}
}
