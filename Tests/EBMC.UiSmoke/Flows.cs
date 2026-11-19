using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MatterHackers.Agg.UI;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.ClubMembers;
using MatterHackers.MatterControl.DataStorage;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.MatterControl.SlicerConfiguration;
using static EBMC.UiSmoke.Smoke;

namespace EBMC.UiSmoke
{
	/// <summary>One flow: what to do before the app starts (optional) and the steps once its window is up.</summary>
	public sealed class Flow
	{
		public string Name { get; set; }

		public string Summary { get; set; }

		/// <summary>True: runs on the data folder a previous flow left (members-restore after members).</summary>
		public bool ReusesData { get; set; }

		public Action BeforeStart { get; set; }

		public Action Run { get; set; }
	}

	/// <summary>
	/// The on-screen flows. Names and passphrases are invented throwaways; the passphrase is never printed.
	/// </summary>
	public static partial class Flows
	{
		/// <summary>An obvious throwaway; never a real one, never printed.</summary>
		private const string Passphrase = "smoke test throwaway 2026";

		private const string MemberA = "Avery Smoketest";

		private const string MemberB = "Rowan Testcase";

		private const string Role = "Smoke Tester";

		private const string NoteText = "Smoke test visit";

		public static readonly IReadOnlyList<Flow> All = new List<Flow>
		{
			new Flow { Name = "start", Summary = "main window, scratch database in use, welcome dismissed", Run = StartFlow },
			new Flow { Name = "members", Summary = "create (refusals first), add, stars, role, note, lock, wrong and right passphrase, copy refused", Run = MembersFlow },
			new Flow { Name = "members-restore", Summary = "damaged files: unreadable view, restore newest backup, files moved aside", ReusesData = true, BeforeStart = DamageMembersFiles, Run = MembersRestoreFlow },
			new Flow { Name = "modes", Summary = "Simple/Advanced keeps the page and the typed draft (Members) and the part (design tab)", Run = ModesFlow },
			new Flow { Name = "slice", Summary = "club printer set up, default part sliced, nothing sent", Run = SliceFlow },
			new Flow { Name = "club-printer", Summary = "set-up only: the club printer in a fresh profile (for the flows that reuse it)", Run = ClubPrinterFlow },
			new Flow { Name = "update-presets", Summary = "an older installed profile: Update club presets, backup, hand override kept, second run has nothing to do", ReusesData = true, BeforeStart = StaleForUpdate, Run = UpdatePresetsFlow },
			new Flow { Name = "presets-menu-not-club", Summary = "a printer that is not the club's (Define New): no \"Update club presets\" in Printer options", Run = PresetsMenuNotClubFlow },
			new Flow { Name = "ready-to-print", Summary = "Send and print to the OctoPrint mock: the summary with hand overrides, then Cancel (nothing sent)", ReusesData = true, BeforeStart = StaleForPrint, Run = ReadyToPrintCancelFlow },
			new Flow { Name = "ready-to-print-confirm", Summary = "as ready-to-print, then Start print: exactly one upload with print", ReusesData = true, BeforeStart = StaleForPrint, Run = ReadyToPrintConfirmFlow },
			new Flow { Name = "laser-export", Summary = "Export for laser to SVG in the scratch folder", Run = LaserExportFlow },
		};

		public static ScratchFileDialogs Dialogs { get; set; }

		public static FakeClipboard Clipboard { get; set; }

		// --- shared steps ---

		private static void MainWindow()
		{
			Step("main-window", () =>
			{
				WaitForWidget("Members Tab", 120, what: "(main window)");
			});
		}

		/// <summary>The first-start "Setup EBMC" dialog: checks it is that dialog, then presses <paramref name="choice"/>.</summary>
		private static void Welcome(string stepName, Action choice)
		{
			Step(stepName, () =>
			{
				var button = WaitForWidget("Cancel Wizard Button", 30, what: "(the Setup EBMC dialog)");
				string title = Ui(() => button.Parents<DialogPage>().FirstOrDefault()?.WindowTitle);
				Log($"dialog: \"{title}\"");
				Require(title == "Setup EBMC", $"expected the \"Setup EBMC\" dialog, found \"{title}\"");
				choice();
				WaitUntil(() => !Visible("Cancel Wizard Button").Any(b => Ui(() => b.Parents<DialogPage>().FirstOrDefault()?.WindowTitle) == "Setup EBMC"),
					10, "the Setup EBMC dialog is still shown");
			});
		}

		private static void DismissWelcome() => Welcome("welcome-dismissed", () => Click("Cancel Wizard Button"));

		/// <summary>From the welcome dialog: "Start New Design", then the badge starter on the new design tab.</summary>
		private static void NewDesignWithBadge()
		{
			// StartupPage's rule: after Start New Design the stock Welcome page follows unless it was turned off. Read it
			// before the click, so the step knows whether to wait for the page (a slow page is then not "no page")
			bool expectWelcomePage = false;
			Welcome("welcome-new-design", () =>
			{
				expectWelcomePage = Ui(() => UserSettings.Instance.get(UserSettingsKey.ShownWelcomeMessage) != "false");
				Click("Start New Design");
			});

			Step("welcome-page-dismissed", () =>
			{
				if (expectWelcomePage)
				{
					var button = WaitForWidget("Cancel Wizard Button", 20, w => w.Parents<DialogPage>().FirstOrDefault() is MatterHackers.MatterControl.Tour.WelcomePage, "(the Welcome page, which this profile shows after Start New Design)");
					Log($"dialog: \"{Ui(() => button.Parents<DialogPage>().FirstOrDefault()?.WindowTitle)}\" (WelcomePage), closing it");
					ClickWidget(button);
					WaitForGone("Cancel Wizard Button");
				}
				else
				{
					// turned off: it must not come at all
					Require(!WaitFor(() => IsShown("Cancel Wizard Button"), 5), "a dialog appeared although the Welcome page is turned off");
					Log("the Welcome page is turned off in this profile, and none appeared");
				}
			});

			Step("badge-on-bed", () =>
			{
				WaitForWidget("Design Start Card", 15);
				Click("Use a starter Start Button");
				Click("Club badge Starter Tile");
				WaitUntil(() => DesignScenePartCount() == 1, 20, "the badge did not arrive on the design tab's bed");
				WaitForGone("Design Start Card", 10);
				Log("design tab scene parts: " + DesignScenePartCount());
			});
		}

		private static int DesignScenePartCount() => Ui(() =>
		{
			var workspace = ApplicationController.Instance.Workspaces.FirstOrDefault(w => w.Printer == null);
			return workspace?.SceneContext?.Scene?.Children?.Count ?? -1;
		});

		private static MembersSessionState MembersState => Ui(() => MembersSession.Core.State);

		private static List<Member> StoredMembers() => Ui(() => MembersSession.Core.Store?.Snapshot().Members.ToList() ?? new List<Member>());

		private static string MembersFolder => Path.Combine(DataDir, "Members");

		private static void CreateMembersList()
		{
			Step("members-tab", () =>
			{
				Click("Members Tab");
				WaitForWidget("Members Create List Button", 10, what: "(fresh data folder)");
			});

			Step("create-list", () =>
			{
				TypeInto("Members New Passphrase Field", Passphrase, secret: true);
				TypeInto("Members Repeat Passphrase Field", Passphrase, secret: true);
				Click("Members Create List Button");
				WaitForWidget("Members Quick Add Name", 15, what: "(the open list)");
				Require(MembersState == MembersSessionState.Open, "the list is not open after creating it");
				Require(File.Exists(Path.Combine(MembersFolder, "members.ebmc")), "members.ebmc was not written in the scratch Members folder");
			});
		}

		// --- 1. start ---

		private static void StartFlow()
		{
			MainWindow();
			Step("data-folder", CheckOpenFiles);
			DismissWelcome();
		}

		/// <summary>The app's own datastore path, and the files the process has open (lsof).</summary>
		private static void CheckOpenFiles()
		{
			string dbPath = ApplicationDataStorage.Instance.DatastorePath.Replace("//", "/");
			Log("app datastore path: " + dbPath);
			Require(Path.GetFullPath(dbPath).StartsWith(DataDir + "/", StringComparison.Ordinal), "the app's datastore path is not inside EBMC_DATA_DIR");

			var lsof = Process.Start(new ProcessStartInfo("/usr/sbin/lsof", $"-n -P -Fn -p {Environment.ProcessId}")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			});
			string output = lsof.StandardOutput.ReadToEnd();
			lsof.WaitForExit();

			var openFiles = output.Split('\n').Where(l => l.StartsWith("n/")).Select(l => l.Substring(1)).Distinct().ToList();
			string real = SmokeProgram.RealDataFolder;
			foreach (var file in openFiles.Where(f => f.EndsWith(".db") || f.Contains(real) || f.Contains(DataDir)))
			{
				Log("open: " + file);
			}

			bool Matches(string open, string path) => open == path || open == "/private" + path;
			Require(openFiles.Any(f => Matches(f, dbPath)), "lsof does not show the scratch database open: " + dbPath);
			Require(!openFiles.Any(f => f.StartsWith(real + "/") || f == real), "the process has a file open in the real data folder " + real);
		}

		// --- 2. members ---

		private static void MembersFlow()
		{
			MainWindow();
			DismissWelcome();

			Step("members-tab", () =>
			{
				Click("Members Tab");
				WaitForWidget("Members Create List Button", 10, what: "(fresh data folder)");
			});

			Step("create-refused-mismatch", () =>
			{
				TypeInto("Members New Passphrase Field", Passphrase, secret: true);
				TypeInto("Members Repeat Passphrase Field", Passphrase + "x", secret: true);
				Click("Members Create List Button");
				var error = WaitForWidget("Members Create Error", 10, w => !string.IsNullOrEmpty(w.Text), "(with an error)");
				Log("error shown: " + Ui(() => error.Text));
				Require(IsShown("Members Create List Button"), "the create view went away after a mismatched repeat");
				Require(!File.Exists(Path.Combine(MembersFolder, "members.ebmc")), "a members file was written after a mismatched repeat");
			});

			Step("create-refused-short", () =>
			{
				string before = Ui(() => Visible("Members Create Error").FirstOrDefault()?.Text);
				TypeInto("Members New Passphrase Field", "short1", secret: true);
				TypeInto("Members Repeat Passphrase Field", "short1", secret: true);
				Click("Members Create List Button");
				var error = WaitForWidget("Members Create Error", 10, w => !string.IsNullOrEmpty(w.Text) && w.Text != before, "(with a new error)");
				Log("error shown: " + Ui(() => error.Text));
				Require(!File.Exists(Path.Combine(MembersFolder, "members.ebmc")), "a members file was written for a too-short passphrase");
			});

			Step("create-list", () =>
			{
				TypeInto("Members New Passphrase Field", Passphrase, secret: true);
				TypeInto("Members Repeat Passphrase Field", Passphrase, secret: true);
				Click("Members Create List Button");
				WaitForWidget("Members Quick Add Name", 15, what: "(the open list)");
				Require(MembersState == MembersSessionState.Open, "the list is not open after creating it");
				Require(File.Exists(Path.Combine(MembersFolder, "members.ebmc")), "members.ebmc was not written in the scratch Members folder");
			});

			Step("copy-works-in-normal-field", () =>
			{
				// control for the passphrase check below: the same keys do copy from an ordinary field
				TypeInto("Members Quick Add Name", MemberA);
				PressCommand(Keys.A);
				PressCommand(Keys.C);
				Require(Clipboard.Text == MemberA, $"Cmd+C in the name field did not copy (clipboard holds \"{Clipboard.Text}\"), so the passphrase check would prove nothing");
				Log("Cmd+C in \"Members Quick Add Name\" copied the name to the test clipboard");
			});

			Step("quick-add-two", () =>
			{
				Click("Members Quick Add Button");
				WaitUntil(() => StoredMembers().Count == 1, 10, $"\"{MemberA}\" was not added");
				WaitUntil(() => Visible("Members Row").Count == 1, 10, "one row is not shown");
				TypeInto("Members Quick Add Name", MemberB);
				Click("Members Quick Add Button");
				WaitUntil(() => StoredMembers().Count == 2, 10, $"\"{MemberB}\" was not added");
				WaitUntil(() => Visible("Members Row").Count == 2, 10, "two rows are not shown");
				foreach (var row in Visible("Members Row"))
				{
					RequireOnScreen(row, "a \"Members Row\"");
				}
			});

			Step("select-member", () =>
			{
				ClickWidget(RowFor(MemberA));
				string id = StoredMembers().Single(m => m.Name == MemberA).Id;
				WaitUntil(() => Ui(() => MembersSession.Core.Drafts.SelectedMemberId) == id, 10, $"\"{MemberA}\" was not selected");
				WaitForWidget("Member Name Field", 10, w => w.Text == MemberA, $"(showing {MemberA})");
			});

			Step("importance-three-stars", () =>
			{
				Click("Member Importance 3 Stars");
				WaitUntil(() => StoredMembers().Single(m => m.Name == MemberA).Importance == 3, 10, "importance was not set to three stars");
				var stars = WaitForWidget("Members Row Stars", 10, what: "(stars in the list row)");
				RequireOnScreen(stars, "the row's stars");
			});

			Step("role", () =>
			{
				// the detail panel was rebuilt by the importance change: wait for the new field before typing
				WaitForWidget("Member Importance 3 Stars", 10);
				TypeInto("Member Role Field", Role);
				PressEnter();
				WaitUntil(() => StoredMembers().Single(m => m.Name == MemberA).Role == Role, 10, "the role was not stored");
				var badge = WaitForWidget("Members Row Role", 10, w => w.Descendants<GuiWidget>().Any(d => d.Text == Role), "(role badge in the list row)");
				RequireOnScreen(badge, "the row's role badge");
			});

			string today = DateTime.Now.ToString("yyyy-MM-dd");
			Step("log-note", () =>
			{
				TypeInto("Member Note Date Field", today);
				TypeInto("Member Note Text Field", NoteText);
				Click("Member Add Note Button");
				WaitUntil(() => StoredMembers().Single(m => m.Name == MemberA).Log.Any(e => e.Date == today && e.Text == NoteText), 10, "the dated note was not stored");
				WaitForText(NoteText, 10);
			});

			Step("lock", () =>
			{
				Click("Members Lock Button");
				WaitForWidget("Members Open Button", 15, what: "(the locked view)");
				Require(MembersState == MembersSessionState.Locked, "the list is not locked");
			});

			Step("passphrase-copy-refused", () =>
			{
				TypeInto("Members Passphrase Field", "wrong smoke passphrase", secret: true);
				string clipboardBefore = Clipboard.Text;
				int setsBefore = Clipboard.SetCount;
				PressCommand(Keys.A);
				PressCommand(Keys.C);
				PressCommand(Keys.X);
				Require(Clipboard.SetCount == setsBefore && Clipboard.Text == clipboardBefore, "Cmd+C or Cmd+X put text from the passphrase field on the clipboard");
				Require(Ui(() => Visible("Members Passphrase Field").First().Text) == "wrong smoke passphrase", "Cmd+X removed text from the passphrase field");
				Log("Cmd+A, Cmd+C, Cmd+X in the passphrase field: the test clipboard was not written");
			});

			Step("wrong-passphrase-refused", () =>
			{
				Click("Members Open Button");
				var error = WaitForWidget("Members State Error", 10, w => !string.IsNullOrEmpty(w.Text), "(with an error)");
				Log("error shown: " + Ui(() => error.Text));
				Require(MembersState == MembersSessionState.Locked, "the list opened with a wrong passphrase");
			});

			Step("reopen", () =>
			{
				TypeInto("Members Passphrase Field", Passphrase, secret: true);
				Click("Members Open Button");
				WaitForWidget("Members Quick Add Name", 15, what: "(the open list)");
				var members = StoredMembers();
				Require(members.Count == 2, $"{members.Count} members after reopening, expected 2");
				var a = members.Single(m => m.Name == MemberA);
				Require(a.Role == Role && a.Importance == 3, $"{MemberA} came back with role \"{a.Role}\" and importance {a.Importance}");
				Require(a.Log.Any(e => e.Date == today && e.Text == NoteText), "the note did not survive");
				Require(members.Any(m => m.Name == MemberB), $"{MemberB} did not survive");
				WaitUntil(() => Visible("Members Row").Count == 2, 10, "two rows are not shown after reopening");
				WaitForWidget("Members Row Role", 10);
				WaitForWidget("Members Row Stars", 10);
			});

			Step("lock-before-exit", () =>
			{
				Click("Members Lock Button");
				WaitForWidget("Members Open Button", 15);
				var files = Directory.GetFiles(MembersFolder, "*", SearchOption.AllDirectories).Select(f => f.Substring(MembersFolder.Length + 1));
				Log("members files: " + string.Join(", ", files));
			});
		}

		private static GuiWidget RowFor(string name)
		{
			GuiWidget row = null;
			WaitUntil(() =>
			{
				row = Ui(() => Visible("Members Row").FirstOrDefault(r => r.Descendants<TextWidget>().Any(t => t.Text == name)));
				return row != null;
			}, 10, $"no row for \"{name}\"");
			return row;
		}

		// --- 3. members-restore ---

		private static readonly byte[] Garbage = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("damaged by the EBMC UI smoke test. ", 64)));

		/// <summary>Name and SHA-256 of every file in Members/Backups, taken before the app starts.</summary>
		private static Dictionary<string, string> backupsBefore;

		private static Dictionary<string, string> ListBackupHashes() =>
			Directory.GetFiles(Path.Combine(MembersFolder, "Backups"))
				.ToDictionary(Path.GetFileName, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

		/// <summary>Before the app starts: keep copies, then overwrite the main file and the previous copy.</summary>
		private static void DamageMembersFiles()
		{
			string main = Path.Combine(MembersFolder, "members.ebmc");
			string prev = main + ".prev";
			string backups = Path.Combine(MembersFolder, "Backups");
			Require(File.Exists(main) && File.Exists(prev), "run the members flow first: members.ebmc and members.ebmc.prev are needed");
			Require(Directory.Exists(backups) && Directory.GetFiles(backups, "members-*.ebmc").Length > 0, "there is no daily backup to restore");

			string copies = Path.Combine(DataDir, "smoke-originals");
			Directory.CreateDirectory(copies);
			File.Copy(main, Path.Combine(copies, "members.ebmc"), true);
			File.Copy(prev, Path.Combine(copies, "members.ebmc.prev"), true);
			File.WriteAllBytes(main, Garbage);
			File.WriteAllBytes(prev, Garbage);
			backupsBefore = ListBackupHashes();
			Console.WriteLine("    before start: copies kept in smoke-originals/, members.ebmc and members.ebmc.prev overwritten with junk");
			Console.WriteLine("    backups: " + string.Join(", ", backupsBefore.Select(p => $"{p.Key} (sha256 {p.Value.Substring(0, 12)}...)")));
		}

		/// <summary>
		/// Opens a copy of a members file with the vault itself (the test passphrase, normalized as the app does) in a
		/// folder of its own, and returns the data. The copy is removed afterwards; the file itself is only read.
		/// </summary>
		private static MembersData ReadWithVault(string file)
		{
			string folder = Path.Combine(DataDir, "smoke-verify", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(folder);
			try
			{
				File.Copy(file, Path.Combine(folder, MembersVault.FileName));
				var vault = new MembersVault(folder, () => DateTime.UtcNow);
				var result = vault.Open(MembersRules.NormalizePassphrase(Passphrase));
				vault.Close();
				Require(result.IsOpen, $"the vault did not open {Path.GetFileName(file)} with the test passphrase ({result.Status})");
				return result.Data;
			}
			finally
			{
				Directory.Delete(folder, true);
			}
		}

		/// <summary>The member and project data in one comparable text (records only, not file metadata).</summary>
		private static string Content(MembersData data) => Newtonsoft.Json.JsonConvert.SerializeObject(new
		{
			data.Members,
			data.Projects,
			data.DeletedMembers,
			data.DeletedProjects,
		});

		private static void MembersRestoreFlow()
		{
			MainWindow();
			DismissWelcome();

			Step("unreadable-view", () =>
			{
				// the files exist, so the tab first asks for the passphrase; that a file is damaged is found on opening
				Click("Members Tab");
				WaitForWidget("Members Open Button", 15, what: "(the locked view)");
				TypeInto("Members Passphrase Field", Passphrase, secret: true);
				Click("Members Open Button");
				WaitForWidget("Members Restore Backup Button", 15, what: "(the unreadable view)");
				Log("shown: " + Ui(() => string.Join(" | ", Visible("Members State View").First().Descendants<TextWidget>().Select(t => t.Text).Where(t => t.Length > 0).Take(4))));
				Require(MembersState == MembersSessionState.Unreadable, $"state is {MembersState}, expected Unreadable");
			});

			string chosen = null;
			Step("restore-backup", () =>
			{
				// the backup the app will restore, as the app chooses it (the newest daily backup, named by UTC date)
				chosen = Ui(() => MembersSession.Core.NewestBackup);
				Require(chosen != null, "the app offers no backup to restore");
				Log("the app's newest backup: " + Path.GetFileName(chosen));
				TypeInto("Members Passphrase Field", Passphrase, secret: true);
				Click("Members Restore Backup Button");
				WaitForWidget("Members Quick Add Name", 20, what: "(the open list after restoring)");
				Require(MembersState == MembersSessionState.Open, "the list did not open after restoring");
			});

			Step("restored-contents", () =>
			{
				// the list now open, the restored main file and the chosen backup, each read with the vault: the same records
				string fromBackup = Content(ReadWithVault(chosen));
				string fromMainFile = Content(ReadWithVault(Path.Combine(MembersFolder, MembersVault.FileName)));
				string inApp = Ui(() => Content(MembersSession.Core.Store.Snapshot()));
				var original = ReadWithVault(Path.Combine(DataDir, "smoke-originals", MembersVault.FileName));

				int restoredCount = StoredMembers().Count;
				Log($"chosen backup {Path.GetFileName(chosen)}: {ReadWithVault(chosen).Members.Count} members; "
					+ $"restored list: {restoredCount} members; the damaged file held {original.Members.Count} before");
				Require(fromMainFile == fromBackup, "the restored members.ebmc does not hold the chosen backup's records");
				Require(inApp == fromBackup, "the list open in the app is not the chosen backup's records");
				if (Content(original) == fromBackup)
				{
					Log("note: the backup and the damaged file held the same records, so this run cannot tell them apart");
				}
				else
				{
					Log("the restored records match the backup and differ from the file that was damaged");
				}
			});

			Step("backups-untouched", () =>
			{
				var after = ListBackupHashes();
				foreach (var pair in backupsBefore)
				{
					Require(after.TryGetValue(pair.Key, out string hash), $"the backup {pair.Key} is gone after the restore");
					Require(hash == pair.Value, $"the backup {pair.Key} changed during the restore");
				}

				Log($"Backups/ after the restore: {string.Join(", ", after.Keys)} ({backupsBefore.Count} unchanged"
					+ (after.Count > backupsBefore.Count ? $", {after.Count - backupsBefore.Count} new" : "") + ")");
			});

			Step("damaged-files-moved-aside", () =>
			{
				var aside = Directory.GetFiles(MembersFolder).Where(f => Path.GetFileName(f).Contains(".replaced-")).ToList();
				foreach (var file in aside)
				{
					Log("moved aside: " + Path.GetFileName(file));
				}

				Require(aside.Any(f => Path.GetFileName(f).StartsWith("members.ebmc.replaced-")), "the damaged members.ebmc was not moved aside");
				Require(aside.Any(f => Path.GetFileName(f).StartsWith("members.ebmc.prev.replaced-")), "the damaged members.ebmc.prev was not moved aside");
				Require(aside.All(f => File.ReadAllBytes(f).SequenceEqual(Garbage)), "a moved-aside file does not hold the damaged bytes");
				Require(File.Exists(Path.Combine(MembersFolder, "members.ebmc")), "there is no members.ebmc after restoring");
				Require(File.Exists(Path.Combine(DataDir, "smoke-originals", "members.ebmc")), "the test's own copies are gone");
				Click("Members Lock Button");
				WaitForWidget("Members Open Button", 15);
			});
		}

		// --- 4. modes ---

		private static void ModesFlow()
		{
			MainWindow();
			NewDesignWithBadge();

			foreach (var mode in new[] { UiMode.Advanced, UiMode.Simple })
			{
				Step("design-tab-" + mode.ToString().ToLower(), () =>
				{
					SwitchMode(mode);
					WaitForWidget("Bed Options Menu", 10, what: "(the design tab's toolbar)");
					Require(ActiveTabIsDesign(), "the active tab is no longer the design tab");
					Require(DesignScenePartCount() == 1, "the part on the design tab is gone");
				});
			}

			CreateMembersList();

			const string Draft = "Draft Person Smoke";
			Step("members-draft-typed", () =>
			{
				TypeInto("Members Quick Add Name", Draft);
			});

			foreach (var mode in new[] { UiMode.Advanced, UiMode.Simple })
			{
				Step("members-" + mode.ToString().ToLower(), () =>
				{
					SwitchMode(mode);
					WaitForWidget("Members Tab Page", 10);
					WaitForWidget("Members Quick Add Name", 10, w => w.Text == Draft, $"(still holding \"{Draft}\")");
				});
			}

			Step("clear-draft", () =>
			{
				// so that closing is not held up by "you would lose typed text"
				TypeInto("Members Quick Add Name", "");
				Click("Members Lock Button");
				WaitForWidget("Members Open Button", 15);
			});
		}

		private static void SwitchMode(UiMode mode)
		{
			Require(UiModes.Current != mode, $"already in {mode} mode");
			Click(mode + " Mode Segment");
			WaitUntil(() => UiModes.Current == mode, 10, $"mode did not change to {mode}");

			// the main window is rebuilt: wait for the new switch, showing the new mode
			WaitForWidget((mode == UiMode.Simple ? "Advanced" : "Simple") + " Mode Segment", 10);
			Log("mode: " + UiModes.Current);
		}

		private static bool ActiveTabIsDesign() => Ui(() =>
			ApplicationController.Instance.MainView.TabControl.ActiveTab?.TabContent is DesignTabPage page && !(page is PrinterTabPage));

		// --- 5. slice ---

		/// <summary>
		/// The club printer in a fresh profile (the welcome dialog's "Setup New Printer", the only printer offered, no
		/// connection), with the OctoPrint address pointed at a closed loopback port in the scratch profile's user layer.
		/// Returns the printer.
		/// </summary>
		private static PrinterConfig SetUpClubPrinter()
		{
			MainWindow();
			Welcome("welcome-setup-printer", () => ClickWidget(ClickableFor(WaitForText("Setup New Printer"))));

			Step("choose-club-printer", () =>
			{
				WaitForWidget("AddPrinterWidget", 15);
				GuiWidget node = null;
				WaitUntil(() =>
				{
					node = Ui(() => SystemWindow.AllOpenSystemWindows.Last().Descendants<GuiWidget>()
						.FirstOrDefault(w => w.Name != null && w.Name.StartsWith("NodeEast Bay Makers Club") && w.ActuallyVisibleOnScreen()));
					return node != null;
				}, 15, "the club printer is not in the printer list");
				var offered = Ui(() => SystemWindow.AllOpenSystemWindows.Last().Descendants<GuiWidget>()
					.Where(w => w.Name != null && w.Name.StartsWith("Node") && w.ActuallyVisibleOnScreen()).Select(w => w.Name).ToList());
				Log("printers offered: " + string.Join(", ", offered));
				ClickWidget(node);
				Click("Next Button");
			});

			Step("skip-connection", () =>
			{
				ClickWidget(WaitForText("Skip Connection Setup", 30));
				WaitUntil(() => Ui(() => ApplicationController.Instance.ActivePrinters.Any()), 30, "no printer tab after setup");
				WaitForWidget("Generate Gcode Button", 30, what: "(the printer tab's Slice button)");
				WaitUntil(() => SystemWindow.AllOpenSystemWindows.Count() == 1, 10, "a dialog is still open over the printer tab");
			});

			PrinterConfig printer = null;
			Step("octoprint-to-loopback", () =>
			{
				printer = Ui(() => ApplicationController.Instance.ActivePrinters.First());
				string host = Ui(() => printer.Settings.GetValue(SettingsKey.octoprint_host));
				string enabled = Ui(() => printer.Settings.GetValue(SettingsKey.octoprint_enabled));
				Log($"club profile: octoprint_enabled={enabled}, octoprint_host={host}");

				// the scratch profile's user layer only: anything that tried to send would reach a closed port here
				Ui(() => printer.Settings.SetValue(SettingsKey.octoprint_host, ClosedLoopback));
				Require(Ui(() => printer.Settings.GetValue(SettingsKey.octoprint_host)) == ClosedLoopback, "the OctoPrint host could not be pointed at loopback");
				Log($"octoprint_host set to {ClosedLoopback} in the scratch profile (closed port)");
			});

			return printer;
		}

		private const string ClosedLoopback = "http://127.0.0.1:9";

		/// <summary>Only sets up the club printer and closes, leaving the profile for a flow that reuses the folder.</summary>
		private static void ClubPrinterFlow()
		{
			var printer = SetUpClubPrinter();
			Step("profile-saved", () =>
			{
				Ui(() => printer.Settings.Save(userDrivenChange: false));
				string path = Ui(() => ProfileManager.Instance.ProfilePath(printer.Settings.ID));
				Require(path != null && File.Exists(path), "the printer profile file was not written");
				Log("profile: " + path.Substring(DataDir.Length + 1));
			});
		}

		private static void SliceFlow()
		{
			var printer = SetUpClubPrinter();

			Step("part-on-bed", () =>
			{
				string make = Ui(() => printer.Settings.OemLayer.ValueOrDefault(SettingsKey.make));
				string model = Ui(() => printer.Settings.OemLayer.ValueOrDefault(SettingsKey.model));
				Log($"printer profile: {make} / {model}");
				Require(ClubPresets.IsClubPrinter(make, model), $"the printer set up is not the club printer ({make} / {model})");
				int parts = Ui(() => printer.Bed.Scene.Children.Count);
				Log("parts on the printer's bed: " + parts);
				Require(parts > 0, "the printer's bed is empty: no default part to slice");
			});

			Step("slice", () =>
			{
				// the active material preset as stored in the scratch profile file (no hand overrides of these here)
				var profile = ReadProfile(ProfileFile());
				string activeId = Value(profile["UserLayer"] as System.Text.Json.Nodes.JsonObject, "active_material_key")
					?? Value(profile["OemLayer"].AsObject(), "active_material_key");
				var preset = profile["MaterialLayers"].AsArray().OfType<System.Text.Json.Nodes.JsonObject>().First(m => Value(m, "layer_id") == activeId);
				string nozzle = Value(preset, "temperature");
				string bed = Value(preset, "bed_temperature");
				Log($"active preset in the scratch profile: {Value(preset, "layer_name")}, nozzle {nozzle}, bed {bed}");
				Require(Ui(() => printer.Settings.GetValue(SettingsKey.temperature)) == nozzle && Ui(() => printer.Settings.GetValue(SettingsKey.bed_temperature)) == bed,
					"the printer's settings are not the active preset's temperatures (a hand override?)");

				var clicked = DateTime.UtcNow.AddSeconds(-1);
				Click("Generate Gcode Button");
				WaitUntil(() => Ui(() => printer.Bed.LoadedGCode != null && printer.Bed.LoadedGCode.LayerCount > 0), 240, "slicing did not produce loaded G-code");
				int layers = Ui(() => printer.Bed.LoadedGCode.LayerCount);
				var gcode = Directory.GetFiles(ApplicationDataStorage.Instance.GCodeOutputPath, "*.gcode")
					.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
				Require(gcode != null && gcode.Length > 0, "no G-code file in the scratch gcode folder");
				Require(gcode.FullName.StartsWith(DataDir) || gcode.FullName.StartsWith("/private" + DataDir), "the G-code file is not in the scratch folder");
				Require(gcode.LastWriteTimeUtc >= clicked, $"the newest G-code ({gcode.Name}, {gcode.LastWriteTimeUtc:u}) was not written by this slice");
				string text = File.ReadAllText(gcode.FullName);
				var nozzleLine = System.Text.RegularExpressions.Regex.Match(text, $@"^M10[49] S{System.Text.RegularExpressions.Regex.Escape(nozzle)}\b.*$", System.Text.RegularExpressions.RegexOptions.Multiline);
				var bedLine = System.Text.RegularExpressions.Regex.Match(text, $@"^M1[49]0 S{System.Text.RegularExpressions.Regex.Escape(bed)}\b.*$", System.Text.RegularExpressions.RegexOptions.Multiline);
				Require(nozzleLine.Success, $"the G-code has no M104/M109 S{nozzle} (the preset's nozzle temperature)");
				Require(bedLine.Success, $"the G-code has no M140/M190 S{bed} (the preset's bed temperature)");
				Log($"G-code: {gcode.Name}, {gcode.Length} bytes, {layers} layers, written {gcode.LastWriteTimeUtc:HH:mm:ss} UTC; "
					+ $"\"{nozzleLine.Value.Trim()}\", \"{bedLine.Value.Trim()}\"");
			});

			Step("nothing-sent", () =>
			{
				var seen = NetworkWatch.AllSeen;
				Log("network requests, lookups and connects seen: " + (seen.Count == 0 ? "none" : string.Join("; ", seen)));
				Require(seen.Count == 0, "the run made network requests (even loopback): " + string.Join("; ", seen));
				Require(Ui(() => !printer.Connection.Printing && !printer.Connection.Paused), "the printer connection reports a print");
			});
		}

		/// <summary>The clickable widget a label belongs to (the label itself is often not selectable).</summary>
		private static GuiWidget ClickableFor(GuiWidget label)
		{
			var button = Ui(() =>
			{
				static bool IsButton(GuiWidget w) => w is ThemedIconButton || w is ThemedButton || w is ThemedTextButton;

				// the label inside a button, or a caption next to its button (the welcome dialog's choices)
				return label.Parents<GuiWidget>().FirstOrDefault(IsButton)
					?? label.Parent?.Children.FirstOrDefault(IsButton);
			});

			Require(button != null, $"no button belongs to the label \"{Ui(() => label.Text)}\"");
			return button;
		}

		// --- 6. laser-export ---

		private static void LaserExportFlow()
		{
			MainWindow();
			NewDesignWithBadge();

			Step("open-laser-export", () =>
			{
				Click("Bed Options Menu");
				Click("Export for laser Menu Item");
				WaitForWidget("Laser Export Window", 10);
				var svgOption = WaitForText("SVG (xTool, LightBurn, Inkscape)", 10);
				bool svgChosen = Ui(() => svgOption.Parents<RadioButton>().FirstOrDefault()?.Checked ?? (svgOption as RadioButton)?.Checked ?? false);
				if (!svgChosen)
				{
					ClickWidget(svgOption);
				}

				Require(Ui(() => svgOption.Parents<RadioButton>().FirstOrDefault()?.Checked ?? (svgOption as RadioButton)?.Checked ?? false), "the SVG format is not chosen");
			});

			Step("export-svg", () =>
			{
				Click("Laser Export Button");
				string svg = null;
				WaitUntil(() =>
				{
					svg = Directory.Exists(Dialogs.SaveFolder)
						? Directory.GetFiles(Dialogs.SaveFolder, "*.svg").FirstOrDefault(f => new FileInfo(f).Length > 0)
						: null;
					return svg != null;
				}, 60, "no SVG file was written to the scratch exports folder");

				// the export finishes on a background task: wait until the file stops growing and parses
				XDocument document = null;
				WaitUntil(() =>
				{
					try
					{
						document = XDocument.Load(svg);
						return true;
					}
					catch (Exception)
					{
						return false;
					}
				}, 20, "the SVG does not parse as XML");

				int paths = document.Descendants().Count(e => e.Name.LocalName == "path");
				Log($"SVG: {Path.GetFileName(svg)}, {new FileInfo(svg).Length} bytes, root <{document.Root?.Name.LocalName}>, {paths} path elements");
				Require(document.Root?.Name.LocalName == "svg", "the file's root element is not <svg>");
				Require(paths >= 1, "the SVG has no path");
				WaitForGone("Laser Export Window", 10);
			});
		}
	}
}
