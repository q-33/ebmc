using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members tab, stage 2: how it is wired into the app (source checks). Behind OemSettings.EnableClubMembers (off in
	/// stock, on for the club), shown whenever the switch is on, with its own members passphrase and no tie to the
	/// maintainer lock; the members folder comes only from the per-user data folder; nothing in ClubMembers reaches
	/// the network, logs, StaticData, UserSettings, slicing, G-code or printers.
	/// </summary>
	[TestFixture]
	public class MembersWiringTests
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

		private static string[] MembersFiles() => Directory.GetFiles(Path.Combine(root, "MatterControlLib", "ClubMembers"), "*.cs", SearchOption.AllDirectories);

		/// <summary>The body of the method that starts at <paramref name="signature"/> (to its closing brace at the same depth).</summary>
		private static string Method(string source, string signature)
		{
			int start = source.IndexOf(signature, StringComparison.Ordinal);
			Assert.That(start, Is.GreaterThanOrEqualTo(0), signature);
			int open = source.IndexOf('{', start);
			int depth = 0;
			for (int i = open; i < source.Length; i++)
			{
				if (source[i] == '{')
				{
					depth++;
				}
				else if (source[i] == '}' && --depth == 0)
				{
					return source.Substring(start, i - start + 1);
				}
			}

			Assert.Fail("unbalanced " + signature);
			return null;
		}

		private static void InOrder(string text, params string[] parts)
		{
			int at = -1;
			foreach (var part in parts)
			{
				int next = text.IndexOf(part, StringComparison.Ordinal);
				Assert.That(next, Is.GreaterThan(at), "'" + part + "' comes after the previous step");
				Assert.That(text.IndexOf(part, next + 1, StringComparison.Ordinal), Is.LessThan(0), "'" + part + "' appears once");
				at = next;
			}
		}

		[Test]
		public void SwitchIsOffInStockAndOnForTheClub()
		{
			Assert.That(Source("MatterControlLib/SettingsManagement/OemSettings.cs"), Does.Contain("public bool EnableClubMembers = false;"));
			Assert.That(Source("Tools/EBMC/generate_ebmc_files.py"), Does.Contain("'EnableClubMembers': True"));
			Assert.That(Source("StaticData/OEMSettings/Settings.json"), Does.Contain("\"EnableClubMembers\": true"));
			Assert.That(Source("MatterControlLib/ClubMembers/MembersSession.cs"), Does.Contain("OemSettings.Instance.EnableClubMembers"));
		}

		[Test]
		public void TheTabIsShownWheneverTheSwitchIsOn()
		{
			var session = Source("MatterControlLib/ClubMembers/MembersSession.cs");
			Assert.That(session, Does.Contain("public static bool ShowTab => Enabled;"));
			Assert.That(session, Does.Not.Contain("InUse"));
			Assert.That(Source("MatterControlLib/ClubMembers/Core/MembersRules.cs"), Does.Not.Contain("ShowTab"));
			Assert.That(Method(Source("MatterControlLib/ClubMembers/MembersTabPage.cs"), "private void Refresh()"), Does.Contain("if (this.HasBeenClosed || !MembersSession.Enabled)"));

			// added once, after the Printers tab and before the first tab is chosen, only under the rule, in both modes
			var main = Source("MatterControlLib/PartPreviewWindow/MainViewWidget.cs");
			Assert.That(Regex.Matches(main, @"ClubMembers\.MembersTabPage\.Create\(theme\)").Count, Is.EqualTo(1));
			Assert.That(Regex.Matches(main, "ClubMembers\\.").Count, Is.EqualTo(2), "the rule and the page, nothing else");
			int printers = main.IndexOf("\"Hardware\",", StringComparison.Ordinal);
			int rule = main.IndexOf("if (ClubMembers.MembersSession.ShowTab)", StringComparison.Ordinal);
			int page = main.IndexOf("ClubMembers.MembersTabPage.Create(theme)", StringComparison.Ordinal);
			int initial = main.IndexOf("SetInitialTab();", StringComparison.Ordinal);
			Assert.That(printers, Is.GreaterThan(0));
			Assert.That(rule, Is.GreaterThan(printers));
			Assert.That(page, Is.GreaterThan(rule));
			Assert.That(initial, Is.GreaterThan(page));
			Assert.That(main.Substring(rule, page - rule), Does.Not.Contain("UiMode"));
			Assert.That(main.Substring(rule, page - rule), Does.Not.Contain("}"), "the page is inside the rule's block");
		}

		[Test]
		public void TheMaintainerLockAndTheMembersListAreSeparate()
		{
			// nothing in ClubMembers knows the maintainer lock, and the lock knows nothing of the members list
			foreach (var file in MembersFiles())
			{
				Assert.That(File.ReadAllText(file), Does.Not.Contain("Maintainer"), Path.GetFileName(file));
			}

			var maintainer = Source("MatterControlLib/ApplicationView/Maintainer.cs");
			var pinStore = Source("MatterControlLib/ApplicationView/MaintainerPinStore.cs");
			var pinPage = Source("MatterControlLib/SettingsManagement/MaintainerPinPage.cs");
			foreach (var (name, source) in new[] { ("Maintainer.cs", maintainer), ("MaintainerPinStore.cs", pinStore), ("MaintainerPinPage.cs", pinPage) })
			{
				Assert.That(source, Does.Not.Contain("ClubMembers"), name);
				Assert.That(source, Does.Not.Contain("MembersSession"), name);
				Assert.That(source, Does.Not.Contain("members list"), name);
			}

			// unlock and lock: the PIN check, the state and the rebuild, nothing else
			InOrder(Method(maintainer, "public static bool TryUnlock(string pin)"), "if (!CheckPin(pin))", "IsUnlocked = true;", "OnChanged();", "return true;");
			InOrder(Method(maintainer, "public static void Lock()"), "if (IsUnlocked)", "IsUnlocked = false;", "OnChanged();");

			// PIN change: the second-window and read-back checks stay
			var setPin = Method(maintainer, "public static bool SetPin(string pin)");
			InOrder(
				setPin,
				"var lastConfirmed = LastConfirmedPinHash;",
				"MaintainerPin.IsValidPin(pin)",
				"if (HasPin)",
				"bool readOk = TryReadStoredPin(out var onDisk);",
				"MaintainerPinStore.BeforeStore(readOk, onDisk, lastConfirmed)",
				"MaintainerPinStore.ChangedElsewhereText.Localize()",
				"MaintainerPinStore.ReadFailedText.Localize()",
				"UserSettings.Instance.set(UserSettingsKey.MaintainerPin, hash);",
				"MaintainerPinStore.Stored(TryReadStoredPin(out var readBack), readBack, hash)",
				"MaintainerPinStore.NotStoredText.Localize()",
				"lastConfirmedPinHash = hash;");
			foreach (var gone in new[] { "PinRefusalCanForce", "PinForceWarning", "PinRefusalOffersReload", "force" })
			{
				Assert.That(maintainer, Does.Not.Contain(gone), gone);
			}

			Assert.That(pinStore, Does.Not.Contain("NotStoredMembersMovedText"));
			Assert.That(pinStore, Does.Not.Contain("NotStored("));

			// another window's PIN change is found by reading the settings database again, not the copy read at start
			var reread = Method(maintainer, "private static bool TryReadStoredPin(out string onDisk)");
			Assert.That(reread, Does.Contain("\"SELECT * FROM UserSetting WHERE Name = ?;\""));
			Assert.That(reread, Does.Contain("return false;"), "a failure to read is reported as such");
			Assert.That(Method(maintainer, "public static bool TryUnlock(string pin)"), Does.Not.Contain("TryReadStoredPin"));
			Assert.That(Method(maintainer, "public static void Lock()"), Does.Not.Contain("TryReadStoredPin"));
			Assert.That(Method(maintainer, "public static bool CheckPin(string pin)"), Does.Contain("MaintainerPin.Verify(pin, UserSettings.Instance.get(UserSettingsKey.MaintainerPin))"));

			// the lock's own pages and events are as before
			foreach (var pageType in new[] { "typeof(SlicePresetsPage)", "typeof(ImportSettingsPage)", "typeof(UpdateSettingsPage)", "typeof(PrinterProfileHistoryPage)" })
			{
				Assert.That(maintainer, Does.Contain(pageType));
			}

			// the PIN page says why a change was refused, and offers no "change anyway" or "load the list again"
			InOrder(pinPage, "if (!Maintainer.SetPin(pin))", "if (Maintainer.PinRefusal != null)", "ShowError(Maintainer.PinRefusal);");
			foreach (var gone in new[] { "anywayButton", "reloadButton", "ShowChoice", "HideChoices", "ReloadMembers", "Submit(true)" })
			{
				Assert.That(pinPage, Does.Not.Contain(gone), gone);
			}

			// the session: no PIN check, the tab's Lock button locks the list, held changes stop being retried once open
			var session = Source("MatterControlLib/ClubMembers/MembersSession.cs");
			Assert.That(session, Does.Contain("new MembersSessionCore(Folder, () => DateTime.UtcNow)"));
			foreach (var gone in new[] { "OnUnlocked", "BeforePinStored", "ReloadList", "AfterPinStored", "void BeforeLock(" })
			{
				Assert.That(session, Does.Not.Contain(gone), gone);
			}

			InOrder(Method(session, "public static void Lock()"), "saveGeneration++;", "report = core.Lock();", "Tell(report.Render(Localize));", "RetryHeld(1);");
			Assert.That(Method(session, "private static void RetryHeld(int attempt)"), Does.Contain("core.State == MembersSessionState.Open"));
			var page = Source("MatterControlLib/ClubMembers/MembersTabPage.cs");
			var header = Method(page, "private GuiWidget BuildHeader(FlowLayoutWidget column)");
			var onLock = header.Substring(header.IndexOf("lockButton.Click +=", StringComparison.Ordinal));
			InOrder(onLock, "Core.State != MembersSessionState.Open", "ClearFields();", "MembersSession.Lock();");

			// closing the app asks first when members work would be lost (nothing when the feature is off)
			var root = Source("MatterControlLib/RootSystemWindow.cs");
			InOrder(root, "ClubMembers.MembersSession.ExitWouldLoseChanges()", "ClubMembers.MembersSession.ConfirmExit();", "string caption = null;");
			InOrder(Method(session, "public static bool ExitWouldLoseChanges()"), "if (core == null || exitConfirmed)", "return false;", "core.SaveBeforeExit()");

			// closing the app saves and forgets
			Assert.That(Method(Source("MatterControlLib/ApplicationView/ApplicationController.cs"), "public void OnApplicationClosed()"), Does.Contain("ClubMembers.MembersSession.FlushAndClose();"));
		}

		[Test]
		public void TheFileItselfIsTheOnlyCheckOfThePassphraseAndOnlyCreateListMakesAList()
		{
			var core = Source("MatterControlLib/ClubMembers/Session/MembersSessionCore.cs");
			foreach (var gone in new[] { "PinPasses", "isCurrentPin", "PinChangeCheck", "PrepareForNewPin", "BeginOpening" })
			{
				Assert.That(core, Does.Not.Contain(gone), gone);
			}

			// one open of the vault, reached from CreateList, Unlock and Reload only
			Assert.That(Regex.Matches(core, @"Vault\.Open\(").Count, Is.EqualTo(1));
			Assert.That(Regex.Matches(core, @"\.Open\(").Count, Is.EqualTo(1));
			Assert.That(Regex.Matches(core, @"OpenVault\(").Count, Is.EqualTo(3), "the method, CreateList and OpenExisting");
			InOrder(Method(core, "public string Unlock(string passphrase)"), "string.IsNullOrEmpty(passphrase)", "if (!HasMembersFiles)", "return MembersSessionText.NoListYet;", "OpenExisting(");
			InOrder(Method(core, "private string OpenExisting(string pass)"), "OpenVault(pass)", "if (result.Status == OpenStatus.NewEmpty)", "Vault.Close();", "return MembersSessionText.NoListYet;", "if (result.Status == OpenStatus.WrongPin)", "Apply(result);");
			InOrder(Method(core, "public string CreateList(string passphrase, string repeat)"), "MembersRules.CheckNewPassphrase(passphrase, repeat)", "HasMembersFiles", "OpenVault(");

			// a restore checks the passphrase against the open key before anything is written
			Assert.That(Regex.Matches(core, @"Vault\.Restore\(").Count, Is.EqualTo(1));
			InOrder(Method(core, "private string RestoreFile(string file, string pin, string filePin)"), "if (Vault.IsOpen && !Vault.KeyOpensWith(pass))", "Vault.Restore(file, filePass, pass, null)");
			InOrder(Method(core, "public string ChangePassphrase(string current, string next, string repeat)"), "MembersRules.CheckNewPassphrase(next, repeat)", "Vault.KeyOpensWith(", "MoveToNewPin(");

			// the UI never calls the vault itself
			foreach (var file in MembersFiles().Where(f => !f.EndsWith("MembersSessionCore.cs", StringComparison.Ordinal) && !f.Contains(Path.DirectorySeparatorChar + "Core" + Path.DirectorySeparatorChar)))
			{
				var source = File.ReadAllText(file);
				foreach (var call in new[] { "Vault.", "new MembersVault", ".Rewrap(" })
				{
					Assert.That(source, Does.Not.Contain(call), Path.GetFileName(file) + ": " + call);
				}
			}
		}

		[Test]
		public void NothingInClubMembersReachesTheNetworkLogsOrPrinting()
		{
			var banned = new[]
			{
				"HttpClient", "WebClient", "WebRequest", "System.Net", "Socket", "WebCache",
				"Console.", "Debug.", "Trace.", "Logger", "ReportFatal",
				"StaticData", "UserSettings", "ApplicationSettings",
				"OctoPrint", "Slice", "GCode", "Gcode", "PrinterConfig", "PrinterSettings", "PrinterConnection", "ActivePrinters",
				"SetUiHint", "WindowTitle",
			};
			foreach (var file in MembersFiles())
			{
				var source = File.ReadAllText(file);
				foreach (var word in banned)
				{
					Assert.That(source, Does.Not.Contain(word), Path.GetFileName(file) + ": " + word);
				}
			}
		}

		[Test]
		public void TheDataFolderComesOnlyFromThePerUserDataFolder()
		{
			var all = string.Join("\n", MembersFiles().Select(File.ReadAllText));
			Assert.That(Regex.Matches(all, @"ApplicationDataStorage\.").Count, Is.EqualTo(1));
			Assert.That(Source("MatterControlLib/ClubMembers/MembersSession.cs"), Does.Contain("Path.Combine(ApplicationDataStorage.ApplicationUserDataPath, \"Members\")"));
			foreach (var word in new[] { "GetFolderPath", "SpecialFolder", "GetTempPath", "CurrentDirectory", "AppContext.BaseDirectory", "\"/Users", "\"C:\\\\" })
			{
				Assert.That(all, Does.Not.Contain(word), word);
			}

			// the app side makes exactly one session, on that folder
			Assert.That(Regex.Matches(all, @"new MembersSessionCore\(").Count, Is.EqualTo(1));
		}

		[Test]
		public void NoMemberTextGoesToUserSettingsOrTheTitle()
		{
			// nothing in ClubMembers touches UserSettings (checked above), and nothing about the passphrase is stored:
			// the passphrase fields are never put in the drafts
			var page = Source("MatterControlLib/ClubMembers/MembersTabPage.cs");
			foreach (var method in new[] { "private GuiWidget BuildHeader(FlowLayoutWidget column)", "private GuiWidget BuildStateView(MembersSessionState state)", "private GuiWidget BuildSetView(FlowLayoutWidget column, Action<string> say)" })
			{
				Assert.That(Method(page, method), Does.Not.Contain("Drafts"), method);
			}

			var main = Source("MatterControlLib/PartPreviewWindow/MainViewWidget.cs");
			int rule = main.IndexOf("if (ClubMembers.MembersSession.ShowTab)", StringComparison.Ordinal);
			var block = main.Substring(rule, main.IndexOf("SetInitialTab();", rule, StringComparison.Ordinal) - rule);
			Assert.That(block, Does.Not.Contain("UserSettings"));
			Assert.That(block, Does.Contain("new ChromeTab(\"Members\", \"Members\".Localize()"));
		}

		[Test]
		public void AFailureToBuildTheTabNeverStopsTheWindowAndNeverShowsErrorText()
		{
			var page = Source("MatterControlLib/ClubMembers/MembersTabPage.cs");
			InOrder(Method(page, "public static GuiWidget Create(ThemeConfig theme)"), "return new MembersTabPage(theme);", "catch (Exception)", "CouldNotShow(theme,");
			// the app-side files (the reviewed core only ever throws its own fixed-message exception)
			foreach (var file in MembersFiles().Where(f => !f.Contains(Path.DirectorySeparatorChar + "Core" + Path.DirectorySeparatorChar)))
			{
				var source = File.ReadAllText(file);
				Assert.That(Regex.IsMatch(source, @"catch \(\w*Exception \w+\)(?!\s*when)"), Is.False, Path.GetFileName(file) + ": no exception object is ever used");
				Assert.That(source, Does.Not.Contain(".Message,"), Path.GetFileName(file));
				Assert.That(source, Does.Not.Contain(".Message)"), Path.GetFileName(file));
			}

			// the page tells when a retry still did not open the list, and keeps the focus in the passphrase field
			InOrder(Method(page, "private GuiWidget BuildStateView(MembersSessionState state)"), "problem = MembersSessionText.StillNotOpen.Localize();", "SetErrorText(error, problem);", "passField.Focus();");
			Assert.That(page, Does.Contain("\"Members could not be shown. Restart the app to try again.\""));
			Assert.That(page, Does.Not.Contain("Lock and unlock to try again"));

			// notices: the fixed sentence is translated, then the counts and names put in
			Assert.That(page, Does.Contain("Core.Notices.Select(n => n.Render(Localize))"));
			Assert.That(page, Does.Not.Contain("line.Localize()"));

			// the delete confirmation goes through the session (refused after a lock cycle), not through the panel
			var detail = Source("MatterControlLib/ClubMembers/MemberDetailPanel.cs");
			InOrder(detail, "int askedInLockGeneration = Core.LockGeneration;", "Core.Delete(id, askedInLockGeneration);");

			// background saves never wait on the UI thread
			Assert.That(Source("MatterControlLib/ClubMembers/MembersSession.cs"), Does.Contain("core.SaveNow(waitIfBusy: false);"));
		}

		[Test]
		public void RowsInTopToBottomColumnsAreFitOnly()
		{
			// agg-sharp leaves a child with a single Top/Center/Bottom anchor out when a Fit parent measures its children
			// (GuiWidget.VAnchorIsFloating), so a row with VAnchor.Center | VAnchor.Fit in a top-to-bottom column was cut
			// off: the detail panel ended above "Log" and the quick-add bar was drawn above its own top. Source check only:
			// the test project has no agg-sharp; the layout was measured with a separate agg-sharp program
			var centerFit = new Regex(@"VAnchor\.Center\s*\|\s*VAnchor\.Fit|VAnchor\.Fit\s*\|\s*VAnchor\.Center");
			var detail = Source("MatterControlLib/ClubMembers/MemberDetailPanel.cs");
			Assert.That(centerFit.IsMatch(detail), Is.False, "every row of the detail column");

			// the whole tab page (quick-add, header, change panel, Set, Enter and state views), except the notice text,
			// which sits in a left-to-right panel where a centred child is still measured
			var page = Source("MatterControlLib/ClubMembers/MembersTabPage.cs");
			var notices = Method(page, "private void RefreshNotices()");
			Assert.That(centerFit.IsMatch(page.Replace(notices, "")), Is.False, "the tab page");
			Assert.That(Regex.IsMatch(notices, @"var panel = new FlowLayoutWidget\(\)\s*\{\s*HAnchor = HAnchor\.Stretch,\s*VAnchor = VAnchor\.Fit,"), Is.True, "the notice panel (left to right)");

			var quickAdd = Method(page, "private GuiWidget BuildQuickAdd()");
			Assert.That(Regex.IsMatch(quickAdd, @"var row = new FlowLayoutWidget\(\)\s*\{\s*HAnchor = HAnchor\.Stretch,\s*VAnchor = VAnchor\.Fit,\s*\};\s*bar\.AddChild\(row\);"), Is.True, "the quick-add row");
			Assert.That(Regex.IsMatch(Method(page, "private ThemedPasswordTextEditWidget AddPassphraseRow(GuiWidget parent, string label, string name)"), @"var row = new FlowLayoutWidget\(\)\s*\{\s*HAnchor = HAnchor\.Stretch,\s*VAnchor = VAnchor\.Fit,"), Is.True, "the passphrase rows");

			// wrapped texts placed at the top of a plain panel fit their text
			Assert.That(Regex.IsMatch(page, @"VAnchor = VAnchor\.Top,"), Is.False);
			Assert.That(Regex.Matches(page, @"VAnchor = VAnchor\.Top \| VAnchor\.Fit,").Count, Is.EqualTo(2));
		}

		[Test]
		public void ImportanceAndRoleAreShownAndPickedWithFitOnlyRows()
		{
			var page = Source("MatterControlLib/ClubMembers/MembersTabPage.cs");
			var detail = Source("MatterControlLib/ClubMembers/MemberDetailPanel.cs");
			var style = Source("MatterControlLib/ClubMembers/MemberImportanceStyle.cs");

			// the filter sits in the list column under Show archived, as a Fit-only row, and only sets the view
			InOrder(Method(page, "private GuiWidget BuildOpen()"), "left.AddChild(search);", "left.AddChild(archived);", "left.AddChild(BuildImportanceFilter());", "left.AddChild(scroll);");
			var filter = Method(page, "private GuiWidget BuildImportanceFilter()");
			Assert.That(Regex.IsMatch(filter, @"var row = new FlowLayoutWidget\(\)\s*\{\s*HAnchor = HAnchor\.Left \| HAnchor\.Fit,\s*VAnchor = VAnchor\.Fit,"), Is.True, "the filter row");
			foreach (var name in new[] { "\"Members Importance Filter\"", "\"Members Filter All\"", "\"Members Filter 1 Star And Up\"", "\"Members Filter 2 Stars And Up\"", "\"Members Filter 3 Stars\"" })
			{
				Assert.That(filter, Does.Contain(name));
			}

			InOrder(filter, "Core.Drafts.MinImportance = chosen;", "RebuildList();");
			Assert.That(filter, Does.Not.Contain("Store."), "a view setting, not an edit");
			Assert.That(Method(page, "private void RebuildList()"), Does.Contain("Core.EmptyListMessage().Localize()"), "the empty list names its cause");
			Assert.That(Method(Source("MatterControlLib/ClubMembers/Session/MembersSessionCore.cs"), "public IReadOnlyList<Member> ListMembers(out int total)"), Does.Contain("Drafts.MinImportance"));
			foreach (var core in new[] { "MemberRecords.cs", "MembersExport.cs", "MembersVault.cs" })
			{
				Assert.That(Source("MatterControlLib/ClubMembers/Core/" + core), Does.Not.Contain("MinImportance"), core + ": the filter is never stored");
			}

			// a list row: level 0 without a role is built exactly as before; otherwise the name, stars and badge share a
			// Fit-only line, and the stripe is drawn over the row's left padding
			var row = Method(page, "private GuiWidget BuildRow(Member member)");
			InOrder(row, "if (level == 0 && string.IsNullOrEmpty(member.Role))", "row.AddChild(nameText);", "var title = new FlowLayoutWidget()", "\"Members Row Stars\"", "\"Members Row Role\"", "row.AddChild(title);", "var stripe = MemberImportanceStyle.LevelColor(theme, level);", "row.AfterDraw +=");
			Assert.That(Regex.IsMatch(row, @"var title = new FlowLayoutWidget\(\)\s*\{\s*HAnchor = HAnchor\.Left \| HAnchor\.Fit,\s*VAnchor = VAnchor\.Fit,"), Is.True, "the row's title line");
			Assert.That(row, Does.Contain("OnAccent = selected"), "stars keep an outline on the selected row's accent background");

			// the member panel: the picker and the Role field directly under Name, both rows made by AddRow (Fit only)
			InOrder(detail, "AddTextRow(\"Name\".Localize(), MemberField.Name, 280, \"Member Name Field\");", "AddImportanceRow();", "AddRoleRow();", "AddTextRow(\"Contact\".Localize()");
			var picker = Method(detail, "private void AddImportanceRow()");
			InOrder(picker, "var row = AddRow(\"Importance\".Localize());", "\"Member Importance None\"", "\"Member Importance 1 Star\"", "\"Member Importance 2 Stars\"", "\"Member Importance 3 Stars\"", "Core.SetImportance(chosen)", "changed(true);");
			var role = Method(detail, "private void AddRoleRow()");
			InOrder(role, "var row = AddRow(\"Role\".Localize());", "MakeDraftField(MemberField.Role, 200, \"Member Role Field\"", "\"Member Role Badge\"");
			InOrder(Method(detail, "private void Commit()"), "Core.CommitDetailDrafts();", "ShowProblem(result);", "ShowStoredRole();");

			// stars are vector shapes, never a font glyph the interface font may lack; fixed colours live in one place
			foreach (var file in MembersFiles())
			{
				var source = File.ReadAllText(file);
				Assert.That(source, Does.Not.Contain("\u2605"), Path.GetFileName(file));
				Assert.That(source, Does.Not.Contain("\\u2605"), Path.GetFileName(file));
				if (!file.EndsWith("MemberImportanceStyle.cs", StringComparison.Ordinal))
				{
					Assert.That(source, Does.Not.Contain("new Color("), Path.GetFileName(file) + ": fixed colours are defined in MemberImportanceStyle");
				}
			}

			Assert.That(Method(style, "private static VertexStorage Star("), Does.Contain("new VertexStorage()"));
			Assert.That(Method(style, "public static Color LevelColor(ThemeConfig theme, int importance)"), Does.Contain("theme.IsDarkTheme"));
		}

		[Test]
		public void ErrorLinesClearAndTheNotesBoxHasItsHeight()
		{
			// an emptied WrappedTextWidget keeps showing its old text: every error line goes through one helper that
			// clears the inner text too
			var page = Source("MatterControlLib/ClubMembers/MembersTabPage.cs");
			var helper = Method(page, "internal static void SetErrorText(WrappedTextWidget line, string text)");
			InOrder(helper, "line.Text = text ?? \"\";", "if (string.IsNullOrEmpty(text))", "line.TextWidget.Text = \"\";");

			var detail = Source("MatterControlLib/ClubMembers/MemberDetailPanel.cs");
			Assert.That(Method(detail, "public void ShowProblem(ValidationResult result)"), Does.Contain("MembersTabPage.SetErrorText(errorText,"));
			foreach (var file in MembersFiles().Where(f => !f.Contains(Path.DirectorySeparatorChar + "Core" + Path.DirectorySeparatorChar)))
			{
				var source = File.ReadAllText(file);
				Assert.That(Regex.IsMatch(source, @"\b(error|errorText)\.Text\s*="), Is.False, Path.GetFileName(file) + ": error lines are set through SetErrorText");
			}

			// passphrase fields are emptied only through one helper, which also clears the undo history; copy and cut
			// from the keyboard are refused in them
			var clear = Method(page, "private static void ClearPassphrase(ThemedPasswordTextEditWidget field)");
			InOrder(clear, "field.Text = \"\";", "field.ActualTextEditWidget.ClearUndoHistory();");
			var rest = page.Replace(clear, "");
			Assert.That(Regex.IsMatch(rest, @"\b(\w*[Ff]ield)\.Text\s*="), Is.False, "every passphrase field clear goes through ClearPassphrase");
			Assert.That(Regex.Matches(page, @"ClearPassphrase\(").Count, Is.GreaterThanOrEqualTo(6), "the helper and its callers (Run, Create, the change panel)");
			var passRow = Method(page, "private ThemedPasswordTextEditWidget AddPassphraseRow(GuiWidget parent, string label, string name)");
			InOrder(passRow, "InternalTextEditWidget.KeyDown +=", "e.KeyCode == Keys.C || e.KeyCode == Keys.X", "e.Handled = true;");
			Assert.That(Regex.Matches(page, "new ThemedPasswordTextEditWidget\\(").Count, Is.EqualTo(1), "every passphrase field is made by AddPassphraseRow");

			// the notes editor gets its own height (otherwise one line inside an 80 px frame)
			var makeField = Method(detail, "private ThemedTextEditWidget MakeDraftField(");
			Assert.That(makeField, Does.Contain("pixelHeight: pixelHeight"));
			Assert.That(Method(detail, "private void AddNotes()"), Does.Contain("pixelHeight: 80 * Scale"));
		}

		[Test]
		public void TheLockNoticeWrapsToTheWidthItIsShownIn()
		{
			// the stock box wraps to a width that counts the padding in logical units; the Members box wraps in device pixels
			var session = Source("MatterControlLib/ClubMembers/MembersSession.cs");
			InOrder(Method(session, "private static void Tell(string message)"), "DialogWindow.Show(new NoticePage(message, \"Members\".Localize()), 0)");
			Assert.That(Method(session, "private static void Tell(string message)"), Does.Not.Contain("StyledMessageBox.ShowMessageBox(message"));
			InOrder(Method(session, "private void Rewrap()"), "this.ContentRow.DevicePadding.Width", "scroll.ScrollArea.DeviceMarginAndBorder.Width", "text.DeviceMarginAndBorder.Width", "text.PointSize * GuiWidget.DeviceScale");
			InOrder(Method(session, "public override void OnBoundsChanged(EventArgs e)"), "base.OnBoundsChanged(e);", "Rewrap();");
		}

		[Test]
		public void TheListRowShowsTheLocalDayOfTheLastActivity()
		{
			// the row's date comes from MembersSearch.LastActivity, which turns the UTC change time into a local day
			var page = Source("MatterControlLib/ClubMembers/MembersTabPage.cs");
			Assert.That(Method(page, "private GuiWidget BuildRow(Member member)"), Does.Contain("MembersRules.ToIso(MembersSearch.LastActivity(member))"));
			Assert.That(Method(Source("MatterControlLib/ClubMembers/Core/MembersSearch.cs"), "public static DateTime LastActivity("), Does.Not.Contain("UpdatedUtc.Date"));
		}

		[Test]
		public void TooltipsAndWidgetNamesNeverCarryMemberText()
		{
			// the GUI files (the data model's own Name property is not a widget name)
			foreach (var file in Directory.GetFiles(Path.Combine(root, "MatterControlLib", "ClubMembers"), "*.cs", SearchOption.TopDirectoryOnly))
			{
				var source = File.ReadAllText(file);
				foreach (Match m in Regex.Matches(source, @"\b(ToolTipText|Name)\s*=(?!=)\s*([^,;\r\n]+)"))
				{
					var value = m.Groups[2].Value.Trim();
					Assert.That(value.StartsWith("\"", StringComparison.Ordinal) || value.StartsWith("name", StringComparison.Ordinal) || value.StartsWith("theme", StringComparison.Ordinal),
						Is.True, Path.GetFileName(file) + ": " + m.Value);
				}
			}
		}
	}
}
