using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members tab, stage 2: the session (MembersSessionCore) without any GUI, in a temporary folder. The list has its
	/// own members passphrase: created with CreateList, opened with Unlock, changed with ChangePassphrase. Nothing
	/// about the passphrase is stored; the file's authenticated decryption is the only check. Every name here is
	/// invented.
	/// </summary>
	[TestFixture]
	public class MembersSessionTests
	{
		private const string Pass = "amber-lantern-42";
		private const string NewPass = "violet-harbor-77";
		private const string OtherPass = "copper-meadow-19";

		private string root;
		private string folder;
		private DateTime now;
		private int ids;
		private int waits;
		private Action onWait;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "ebmc-members-session-test-" + Guid.NewGuid().ToString("N"));
			folder = Path.Combine(root, "Members");
			now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
			ids = 0;
			waits = 0;
			onWait = null;
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}

		private MembersSessionCore NewCore()
		{
			return new MembersSessionCore(
				folder,
				() => now,
				wait: ms =>
				{
					waits++;
					onWait?.Invoke();
				},
				newId: () => (++ids).ToString("x32"),
				today: () => now.Date);
		}

		private FileStream HoldLock()
		{
			Directory.CreateDirectory(folder);
			return new FileStream(Path.Combine(folder, MembersVault.FileName + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}

		private string Main => Path.Combine(folder, MembersVault.FileName);

		/// <summary>The member names in the file, opened with <paramref name="pass"/> by a fresh vault (null: did not
		/// open). The vault takes the key exactly as given (no normalization).</summary>
		private List<string> NamesOnDisk(string pass)
		{
			var result = new MembersVault(folder, () => now).Open(pass);
			return result.IsOpen ? result.Data.Members.Select(m => m.Name).ToList() : null;
		}

		/// <summary>Every file under the members folder with its hash (to prove nothing was written). The lock file
		/// is left out: taking the write lock creates it empty.</summary>
		private Dictionary<string, string> FolderSnapshot()
		{
			if (!Directory.Exists(folder))
			{
				return new Dictionary<string, string>();
			}

			return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
				.Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
				.ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
		}

		private MembersSessionCore OpenWithRobin()
		{
			var core = NewCore();
			Assert.That(core.CreateList(Pass, Pass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.QuickAdd("Robin Example", "met at open night").Ok, Is.True);
			core.SaveNow();
			Assert.That(core.Dirty, Is.False);
			return core;
		}

		/// <summary>A members file under <see cref="Pass"/>, then the session closed.</summary>
		private void FileWithRobin()
		{
			var core = OpenWithRobin();
			core.CloseForExit();
		}

		// --- create and open ---

		[Test]
		public void CreateListOpensAnEmptyListWritesItAtOnceAndAQuickAddIsSavedEncrypted()
		{
			var core = NewCore();
			int saveWanted = 0;
			core.SaveWanted += (s, e) => saveWanted++;

			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(core.HasMembersFiles, Is.False);
			Assert.That(core.CreateList(Pass, Pass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.Unlocked, Is.True);
			Assert.That(core.ListMembers(out _), Is.Empty);

			// the file exists at once, under the passphrase, before anything is typed
			Assert.That(File.Exists(Main), Is.True);
			Assert.That(core.HasMembersFiles, Is.True);
			Assert.That(core.Dirty, Is.False);
			Assert.That(NamesOnDisk(Pass), Is.Empty);

			var added = core.QuickAdd("  Robin   Example ", "met at open night");
			Assert.That(added.Ok, Is.True);
			Assert.That(core.Dirty, Is.True);
			Assert.That(saveWanted, Is.GreaterThan(0), "a change asks the app to save after a pause");
			Assert.That(core.Drafts.SelectedMemberId, Is.EqualTo(added.Value.Id), "the new member is shown");

			core.SaveNow();
			Assert.That(core.Dirty, Is.False);
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));
			Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(Main)), Does.Not.Contain("Robin"));
		}

		[Test]
		public void CreateListRefusesAMismatchOrAShortPassphraseAndWritesNothing()
		{
			var core = NewCore();
			Assert.That(core.CreateList(Pass, Pass + "x"), Is.EqualTo(MembersRules.PassphraseMismatch));
			Assert.That(core.CreateList("short12", "short12"), Is.EqualTo(MembersRules.PassphraseTooShort));
			Assert.That(core.CreateList("", ""), Is.EqualTo(MembersRules.PassphraseEmpty));
			Assert.That(core.CreateList(null, null), Is.EqualTo(MembersRules.PassphraseEmpty));
			Assert.That(core.CreateList("amber\tlantern42", "amber\tlantern42"), Is.EqualTo(MembersRules.PassphraseInvalidCharacters));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(core.Unlocked, Is.False);
			Assert.That(Directory.Exists(folder), Is.False, "the vault was never asked, so nothing was created");
		}

		[Test]
		public void CreateListIsRefusedWhenAnyMembersFileExists()
		{
			FileWithRobin();
			var before = FolderSnapshot();

			var core = NewCore();
			Assert.That(core.HasMembersFiles, Is.True);
			Assert.That(core.CreateList(NewPass, NewPass), Is.EqualTo(MembersSessionText.ListAlreadyExists));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));

			// only a daily backup left: still a list that a restore could bring back
			File.Delete(Main);
			File.Delete(Main + ".prev");
			Assert.That(core.NewestBackup, Is.Not.Null);
			Assert.That(core.HasMembersFiles, Is.True);
			Assert.That(core.CreateList(NewPass, NewPass), Is.EqualTo(MembersSessionText.ListAlreadyExists));

			// an open list refuses too
			var open = OpenWithRobinIn(Path.Combine(root, "second"));
			Assert.That(open.CreateList(NewPass, NewPass), Is.EqualTo(MembersSessionText.ListAlreadyExists));
			Assert.That(open.State, Is.EqualTo(MembersSessionState.Open));
		}

		private MembersSessionCore OpenWithRobinIn(string at)
		{
			var saved = folder;
			folder = at;
			try
			{
				return OpenWithRobin();
			}
			finally
			{
				folder = saved;
			}
		}

		[Test]
		public void UnlockWithNoFilesRefusesAndCreatesNothing()
		{
			var core = NewCore();
			Assert.That(core.Unlock(Pass), Is.EqualTo(MembersSessionText.NoListYet));
			Assert.That(core.Unlock(""), Is.EqualTo(MembersSessionText.PassphraseRequired));
			Assert.That(core.Unlock(null), Is.EqualTo(MembersSessionText.PassphraseRequired));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(core.Unlocked, Is.False);
			Assert.That(core.Vault.IsOpen, Is.False);
			Assert.That(Directory.Exists(folder), Is.False, "nothing was created");

			// an empty folder, or one holding only a file moved aside, has no list either: Unlock never makes one
			Directory.CreateDirectory(folder);
			File.WriteAllText(Main + ".replaced-20261116T120000Z", "moved aside earlier");
			var before = FolderSnapshot();
			Assert.That(core.HasMembersFiles, Is.False);
			Assert.That(core.Unlock(Pass), Is.EqualTo(MembersSessionText.NoListYet));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));
			Assert.That(File.Exists(Main), Is.False);

			// CreateList does, and leaves the moved-aside file alone
			Assert.That(core.CreateList(Pass, Pass), Is.Null);
			Assert.That(File.Exists(Main), Is.True);
			Assert.That(File.ReadAllText(Main + ".replaced-20261116T120000Z"), Is.EqualTo("moved aside earlier"));
		}

		[Test]
		public void AWrongPassphraseStaysLockedAndChangesNothing()
		{
			FileWithRobin();
			var before = FolderSnapshot();

			var core = NewCore();
			int changes = 0;
			core.StateChanged += (s, e) => changes++;
			Assert.That(core.Unlock(OtherPass), Is.EqualTo(MembersSessionText.WrongPassphrase));
			Assert.That(core.Unlock(Pass + " "), Is.EqualTo(MembersSessionText.WrongPassphrase), "never trimmed");
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(core.Unlocked, Is.False);
			Assert.That(core.Store, Is.Null);
			Assert.That(core.Vault.IsOpen, Is.False);
			Assert.That(core.ListMembers(out _), Is.Empty);
			Assert.That(FolderSnapshot(), Is.EqualTo(before));

			Assert.That(core.Unlock(Pass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"));
		}

		[Test]
		public void AnExistingFileKeyedWithAFourDigitPinOpensWithIt()
		{
			// a list saved when the maintainer PIN was its key
			var vault = new MembersVault(folder, () => now);
			Assert.That(vault.Open("4821").Status, Is.EqualTo(OpenStatus.NewEmpty));
			Assert.That(vault.Save(new MembersData { Members = { new Member { Id = 7.ToString("x32"), Name = "Robin Example" } } }).Ok, Is.True);
			vault.Close();

			var core = NewCore();
			Assert.That(core.HasMembersFiles, Is.True);
			Assert.That(core.Unlock("48210"), Is.EqualTo(MembersSessionText.WrongPassphrase));
			Assert.That(core.Unlock("4821"), Is.Null, "entering has no length rule");
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"));

			// it can then move to a proper passphrase
			Assert.That(core.ChangePassphrase("4821", NewPass, NewPass), Is.Null);
			Assert.That(NamesOnDisk(NewPass), Is.EqualTo(new[] { "Robin Example" }));
			Assert.That(NamesOnDisk("4821"), Is.Null);
		}

		[Test]
		public void NfcEquivalentPassphrasesOpenTheSameFile()
		{
			const string composed = "café-terrace-9";
			const string decomposed = "café-terrace-9";

			var core = NewCore();
			Assert.That(core.CreateList(decomposed, composed), Is.Null, "the same text typed two ways");
			Assert.That(core.QuickAdd("Robin Example", "").Ok, Is.True);
			core.Lock();

			// the key is the NFC form
			Assert.That(NamesOnDisk(composed), Is.EqualTo(new[] { "Robin Example" }));
			Assert.That(NamesOnDisk(decomposed), Is.Null, "the vault itself takes the key exactly as given");

			Assert.That(core.Unlock(decomposed), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			core.Lock();
			Assert.That(core.Unlock(composed), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.ChangePassphrase(decomposed, NewPass, NewPass), Is.Null, "the current one is compared in NFC too");
		}

		// --- passphrase change ---

		[Test]
		public void ChangePassphraseMovesTheFileAndOnlyTheNewOneOpensIt()
		{
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");   // unsaved: saved first, then moved to the new passphrase

			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.Null);
			Assert.That(NamesOnDisk(NewPass), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
			Assert.That(NamesOnDisk(Pass), Is.Null, "the old passphrase no longer opens the file");
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.StorageNotice, Does.Contain(MembersSessionText.PassphraseChanged));

			// the list keeps working and saving under the new passphrase
			core.QuickAdd("Alex Sample", "");
			core.SaveNow();
			Assert.That(NamesOnDisk(NewPass), Has.Count.EqualTo(3));

			core.Lock();
			Assert.That(core.Unlock(Pass), Is.EqualTo(MembersSessionText.WrongPassphrase));
			Assert.That(core.Unlock(NewPass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
		}

		[Test]
		public void ChangePassphraseRefusesAWrongCurrentOrABadNewOneAndChangesNothing()
		{
			var core = OpenWithRobin();
			var before = FolderSnapshot();

			Assert.That(core.ChangePassphrase(OtherPass, NewPass, NewPass), Is.EqualTo(MembersSessionText.CurrentPassphraseWrong));
			Assert.That(core.ChangePassphrase(Pass + " ", NewPass, NewPass), Is.EqualTo(MembersSessionText.CurrentPassphraseWrong));
			Assert.That(core.ChangePassphrase("", NewPass, NewPass), Is.EqualTo(MembersSessionText.PassphraseRequired));
			Assert.That(core.ChangePassphrase(Pass, NewPass, OtherPass), Is.EqualTo(MembersRules.PassphraseMismatch));
			Assert.That(core.ChangePassphrase(Pass, "short12", "short12"), Is.EqualTo(MembersRules.PassphraseTooShort));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));
			Assert.That(core.StorageNotice, Does.Not.Contain(MembersSessionText.PassphraseChanged));

			// only while open
			core.Lock();
			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.EqualTo(MembersSessionText.PassphraseRefusedNotOpen));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));
		}

		[Test]
		public void AChangeThatCannotMoveTheFileIsRefusedAndTheOldPassphraseStillWorks()
		{
			var core = OpenWithRobin();
			var before = File.ReadAllBytes(Main);

			using (HoldLock())
			{
				Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.EqualTo(MembersSessionText.PassphraseRefusedBusy));
				Assert.That(waits, Is.EqualTo(MembersSessionCore.BusyAttempts - 1), "tried again for a moment first");
			}

			Assert.That(File.ReadAllBytes(Main), Is.EqualTo(before));
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));

			// once free, the change goes through
			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.Null);
			Assert.That(NamesOnDisk(NewPass), Is.EqualTo(new[] { "Robin Example" }));
		}

		[Test]
		public void AStorageFailureRefusesTheChangeAndNothingIsDropped()
		{
			var core = OpenWithRobin();
			core.Vault.FaultPoint = step =>
			{
				if (step == "main-write")
				{
					throw new IOException("simulated");
				}
			};
			core.QuickAdd("Sam Testcase", "");

			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.EqualTo(MembersSessionText.PassphraseRefusedUnsaved));
			Assert.That(core.ListMembers(out _), Has.Count.EqualTo(2), "nothing was dropped");
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));
			Assert.That(NamesOnDisk(NewPass), Is.Null);

			// the key change itself fails every time: refused, still under the old passphrase
			core.Vault.FaultPoint = null;
			core.SaveNow();
			core.Vault.FaultPoint = step =>
			{
				if (step.StartsWith("rewrap", StringComparison.Ordinal))
				{
					throw new IOException("simulated");
				}
			};
			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.EqualTo(MembersSessionText.PassphraseRefusedFailed));
			core.Vault.FaultPoint = null;
			Assert.That(NamesOnDisk(Pass), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
			Assert.That(NamesOnDisk(NewPass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));

			// storage recovers: the change goes through
			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.Null);
			Assert.That(NamesOnDisk(NewPass), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
		}

		[Test]
		public void ASingleFailedSaveIsTriedAgainBeforeRefusing()
		{
			var core = OpenWithRobin();
			int failures = 0;
			core.Vault.FaultPoint = step =>
			{
				if (step == "main-write" && failures++ == 0)
				{
					throw new IOException("simulated, once");
				}
			};
			core.QuickAdd("Sam Testcase", "");

			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.Null);
			Assert.That(NamesOnDisk(NewPass), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
		}

		[Test]
		public void AChangeIsRefusedWhenTheFileChangedElsewhere()
		{
			var core = OpenWithRobin();
			var other = NewCore();
			Assert.That(other.Unlock(Pass), Is.Null);
			other.QuickAdd("Alex Sample", "");
			other.SaveNow();

			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.EqualTo(MembersSessionText.PassphraseRefusedChangedElsewhere));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.ChangedElsewhere));
			Assert.That(NamesOnDisk(Pass), Is.EquivalentTo(new[] { "Robin Example", "Alex Sample" }));
			Assert.That(NamesOnDisk(NewPass), Is.Null);

			// loaded again on purpose, then changed
			Assert.That(core.Unlock(Pass), Is.Null);
			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.Null);
			Assert.That(NamesOnDisk(NewPass), Is.EquivalentTo(new[] { "Robin Example", "Alex Sample" }));
		}

		[Test]
		public void AnInterruptedChangeIsFinishedByOpeningWithTheNewPassphrase()
		{
			var core = OpenWithRobin();

			// the renames after the main file fail, and the app stops
			core.Vault.FaultPoint = step =>
			{
				if (step == "rewrap-switch" || step == "rewrap-journal-delete")
				{
					throw new IOException("simulated");
				}
			};
			var rewrap = core.Vault.Rewrap(NewPass);
			Assert.That(rewrap.Ok, Is.True);
			Assert.That(rewrap.PinChangePending, Is.True);
			core.Vault.FaultPoint = null;
			core.CloseForExit();
			var before = FolderSnapshot();

			// next start: the old passphrase no longer opens it, and the person is told why; nothing is written
			var next = NewCore();
			Assert.That(next.Unlock(Pass), Is.EqualTo(MembersSessionText.WrongPassphraseInterrupted));
			Assert.That(next.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(next.PinChangeInterrupted, Is.True);
			Assert.That(next.Unlock(OtherPass), Is.EqualTo(MembersSessionText.WrongPassphraseInterrupted));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));
			Assert.That(File.Exists(Main + ".rewrap"), Is.True);

			// the new passphrase opens it and finishes the change
			Assert.That(next.Unlock(NewPass), Is.Null);
			Assert.That(next.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(next.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"));
			next.SaveNow();
			Assert.That(File.Exists(Main + ".rewrap"), Is.False, "the journal is gone");
			Assert.That(NamesOnDisk(NewPass), Is.EqualTo(new[] { "Robin Example" }));
		}

		[Test]
		public void RestoreWithTheListOpenRefusesAWrongPassphraseAndChangesNothing()
		{
			var core = OpenWithRobin();
			now = now.AddDays(1);
			core.QuickAdd("Sam Testcase", "");
			core.SaveNow();
			Assert.That(core.NewestBackup, Is.Not.Null);
			var before = FolderSnapshot();

			Assert.That(core.RestoreNewestBackup(OtherPass, ""), Is.EqualTo(MembersSessionText.WrongPassphrase));
			Assert.That(core.RestoreNewestBackup(OtherPass, Pass), Is.EqualTo(MembersSessionText.WrongPassphrase), "the copy's own passphrase is not enough");
			Assert.That(core.RestoreNewestBackup("", ""), Is.EqualTo(MembersSessionText.PassphraseRequired));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.ListMembers(out _), Has.Count.EqualTo(2));

			Assert.That(core.RestoreNewestBackup(Pass, ""), Is.Null);
			Assert.That(core.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"));
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));
		}

		[Test]
		public void AWrongPassphraseWithChangesHeldSinceTheLockKeepsThemSavable()
		{
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");
			using (HoldLock())
			{
				Assert.That(core.Lock().NotSaved, Is.True);
			}

			var before = FolderSnapshot();
			Assert.That(core.Unlock(OtherPass), Is.EqualTo(MembersSessionText.WrongPassphrase));
			Assert.That(FolderSnapshot(), Is.EqualTo(before), "the held changes were not saved on a wrong passphrase");
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(core.HoldsChangesFromLock, Is.True, "still held, never dropped");
			Assert.That(core.Vault.IsOpen, Is.True, "the held key is kept");

			// the background retry saves them once the file is free
			Assert.That(core.TrySaveHeld(waitIfBusy: false), Is.EqualTo(HeldChanges.Saved));
			Assert.That(core.HoldsChangesFromLock, Is.False);
			Assert.That(NamesOnDisk(Pass), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));

			// and the list opens as usual
			Assert.That(core.Unlock(Pass), Is.Null);
			Assert.That(core.ListMembers(out _).Select(m => m.Name), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
		}

		[Test]
		public void AfterAWrongPassphraseHeldChangesAreStillSavedOnTheWayOut()
		{
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");
			using (HoldLock())
			{
				Assert.That(core.Lock().NotSaved, Is.True);
				var before = FolderSnapshot();
				Assert.That(core.Unlock(OtherPass), Is.EqualTo(MembersSessionText.WrongPassphrase));
				Assert.That(FolderSnapshot(), Is.EqualTo(before));
			}

			// closing the app: saved without asking, nothing lost
			Assert.That(core.SaveBeforeExit(), Is.False);
			Assert.That(NamesOnDisk(Pass), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
			core.CloseForExit();
			Assert.That(core.Vault.IsOpen, Is.False);
		}

		[Test]
		public void AWrongPassphraseInTheBusyOrUnreadableViewChangesNothing()
		{
			FileWithRobin();
			File.Delete(Main + ".prev");
			File.WriteAllText(Main, "damaged");

			// busy: the damaged file needs the write lock to decide
			var core = NewCore();
			using (HoldLock())
			{
				Assert.That(core.Unlock(Pass), Is.Null);
				Assert.That(core.State, Is.EqualTo(MembersSessionState.Busy));
				var before = FolderSnapshot();
				core.Unlock(OtherPass);
				Assert.That(core.State, Is.EqualTo(MembersSessionState.Busy));
				Assert.That(FolderSnapshot(), Is.EqualTo(before));
			}

			// unreadable
			Assert.That(core.Unlock(Pass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Unreadable));
			var unreadable = FolderSnapshot();
			core.Unlock(OtherPass);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Unreadable));
			Assert.That(FolderSnapshot(), Is.EqualTo(unreadable));
			Assert.That(core.RestorePrevious(OtherPass, ""), Is.EqualTo(MembersSessionText.NothingToRestore));
			Assert.That(FolderSnapshot(), Is.EqualTo(unreadable));
		}

		[Test]
		public void AChangeStoppedBeforeTheMainFileSwitchedIsUndoneByTheOldPassphrase()
		{
			var core = OpenWithRobin();
			var crash = Path.Combine(root, "crash");

			// the app stops after the journal is written, before the main file is switched (a copy of the folder at
			// that moment is what the next start finds)
			core.Vault.FaultPoint = step =>
			{
				if (step == "rewrap-switch-main")
				{
					CopyFolder(folder, crash);
					throw new IOException("simulated");
				}
			};
			Assert.That(core.ChangePassphrase(Pass, NewPass, NewPass), Is.EqualTo(MembersSessionText.PassphraseRefusedFailed));
			core.Vault.FaultPoint = null;
			core.CloseForExit();
			Directory.Delete(folder, recursive: true);
			CopyFolder(crash, folder);
			Assert.That(File.Exists(Main + ".rewrap"), Is.True, "the change was under way");

			var next = NewCore();
			Assert.That(next.Unlock(NewPass), Is.Not.Null, "the new passphrase never took over");
			Assert.That(next.Unlock(Pass), Is.Null);
			Assert.That(next.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(next.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"));
			next.SaveNow();
			next.CloseForExit();
			Assert.That(File.Exists(Main + ".rewrap"), Is.False, "the change was undone");
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));
			Assert.That(NamesOnDisk(NewPass), Is.Null);
		}

		private static void CopyFolder(string from, string to)
		{
			if (Directory.Exists(to))
			{
				Directory.Delete(to, recursive: true);
			}

			foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
			{
				if (file.EndsWith(".lock", StringComparison.Ordinal))
				{
					continue;
				}

				var target = Path.Combine(to, Path.GetRelativePath(from, file));
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				File.Copy(file, target);
			}
		}

		// --- lock, close, two copies, restore, drafts ---

		[Test]
		public void LockSavesWhatWasTypedAndForgetsEverythingOnDisk()
		{
			var core = OpenWithRobin();
			var id = core.Drafts.SelectedMemberId;
			core.Drafts.SetField(id, MemberField.Contact, "robin@example.invalid");
			core.QuickAdd("Sam Testcase", "");

			core.Lock();

			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(core.Store, Is.Null);
			Assert.That(core.Vault.IsOpen, Is.False, "the key is forgotten");
			Assert.That(core.Unlocked, Is.False);

			var reopened = new MembersVault(folder, () => now).Open(Pass);
			Assert.That(reopened.Data.Members.Select(m => m.Name), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
			Assert.That(reopened.Data.Members.Single(m => m.Name == "Robin Example").Contact, Is.EqualTo("robin@example.invalid"));
			Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(Main)), Does.Not.Contain("Robin"));
		}

		[Test]
		public void ChangesThatCannotBeSavedAtLockAreSavedAfterTheNextUnlock()
		{
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");
			using (HoldLock())
			{
				core.Lock();
			}

			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked), "the lock happened anyway");
			Assert.That(core.Store, Is.Null);
			Assert.That(core.HoldsChangesFromLock, Is.True);
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));

			// saved first thing at the next unlock, with the key held since the lock
			core.Unlock(Pass);
			Assert.That(core.HoldsChangesFromLock, Is.False);
			Assert.That(NamesOnDisk(Pass), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
			Assert.That(core.ListMembers(out _).Select(m => m.Name), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
		}

		[Test]
		public void ChangesKeptAtLockAreNotPutOverAFileChangedMeanwhile()
		{
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");
			using (HoldLock())
			{
				core.Lock();
			}

			var other = NewCore();
			other.Unlock(Pass);
			other.QuickAdd("Alex Sample", "");
			other.Lock();

			core.Unlock(Pass);
			Assert.That(core.ListMembers(out _).Select(m => m.Name), Is.EquivalentTo(new[] { "Robin Example", "Alex Sample" }));
			Assert.That(core.StorageNotice, Does.Contain(MembersSessionText.LostBeforeLock));
		}

		[Test]
		public void CloseForExitSavesAndClearsTheDrafts()
		{
			var core = OpenWithRobin();
			core.Drafts.QuickAddName = "Sam Test";
			core.Drafts.SearchText = "rob";
			core.QuickAdd("Alex Sample", "");

			core.CloseForExit();

			Assert.That(core.Drafts.QuickAddName, Is.Empty);
			Assert.That(core.Drafts.SearchText, Is.Empty);
			Assert.That(core.Drafts.SelectedMemberId, Is.Null);
			Assert.That(NamesOnDisk(Pass), Has.Count.EqualTo(2));
		}

		[Test]
		public void BusyIsTriedAgainBrieflyThenShown()
		{
			// creating a list needs the write lock: busy all the time, so nothing is made and the state stays Locked
			var core = NewCore();
			using (HoldLock())
			{
				Assert.That(core.CreateList(Pass, Pass), Is.EqualTo(MembersSessionText.StateBusy));
			}

			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(waits, Is.EqualTo(MembersSessionCore.BusyAttempts - 1));
			Assert.That(File.Exists(Main), Is.False);

			// the other copy finishes during the short wait: it is created
			var held = HoldLock();
			onWait = () => held.Dispose();
			Assert.That(core.CreateList(Pass, Pass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			core.CloseForExit();
			onWait = null;

			// a damaged main file needs the write lock to decide: the busy view, and Try again decides once free
			File.Delete(Main + ".prev");
			File.WriteAllText(Main, "damaged");
			var busy = NewCore();
			using (HoldLock())
			{
				Assert.That(busy.Unlock(Pass), Is.Null);
				Assert.That(busy.State, Is.EqualTo(MembersSessionState.Busy));
			}

			Assert.That(busy.Unlock(Pass), Is.Null);
			Assert.That(busy.State, Is.EqualTo(MembersSessionState.Unreadable));
		}

		[Test]
		public void ASaveBusyForLongerKeepsTheChangesAndAsksAgain()
		{
			var core = OpenWithRobin();
			int saveWanted = 0;
			core.SaveWanted += (s, e) => saveWanted++;
			core.QuickAdd("Sam Testcase", "");
			saveWanted = 0;

			using (HoldLock())
			{
				// the save after a pause never waits: one try, and the app tries again later
				core.SaveNow(waitIfBusy: false);
				Assert.That(waits, Is.EqualTo(0));
				Assert.That(core.LastSaveBusy, Is.True);
				Assert.That(core.SaveProblem, Is.Null, "no notice for a moment's wait");
				core.SaveNow(waitIfBusy: false);
				core.SaveNow(waitIfBusy: false);
				Assert.That(core.SaveProblem, Is.EqualTo(MembersSessionText.SaveBusy), "shown after a few tries");
				Assert.That(waits, Is.EqualTo(0));
			}

			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.Dirty, Is.True);
			Assert.That(saveWanted, Is.EqualTo(0), "trying again is the app's (backed-off) job");

			core.SaveNow();
			Assert.That(core.Dirty, Is.False);
			Assert.That(core.SaveProblem, Is.Null);
		}

		[Test]
		public void AFileChangedElsewhereIsLoadedAgainWithThePassphrase()
		{
			var core = OpenWithRobin();
			var other = NewCore();
			other.Unlock(Pass);
			other.QuickAdd("Alex Sample", "");
			other.SaveNow();

			core.QuickAdd("Sam Testcase", "");
			core.SaveNow();
			Assert.That(core.State, Is.EqualTo(MembersSessionState.ChangedElsewhere));
			Assert.That(core.Store, Is.Null);
			Assert.That(core.OfferRestorePrevious, Is.False);
			Assert.That(NamesOnDisk(Pass), Is.EquivalentTo(new[] { "Robin Example", "Alex Sample" }), "nothing was overwritten");

			// the change made here could not be kept: said in a notice
			Assert.That(core.StorageNotice, Does.Contain("Another window changed the members list, so 1 change(s) made here could not be kept."));

			// a wrong passphrase shows the error and leaves the view
			var before = FolderSnapshot();
			Assert.That(core.Unlock(OtherPass), Is.EqualTo(MembersSessionText.WrongPassphrase));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.ChangedElsewhere));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));

			Assert.That(core.Unlock(Pass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.ListMembers(out _).Select(m => m.Name), Is.EquivalentTo(new[] { "Robin Example", "Alex Sample" }));
		}

		[Test]
		public void AfterOpeningThePreviousCopyARefusedSaveOffersToRestoreIt()
		{
			// the main file is under another passphrase, the previous copy under ours
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");
			core.SaveNow();
			core.CloseForExit();

			var elsewhere = Path.Combine(root, "elsewhere");
			var foreign = new MembersVault(elsewhere, () => now);
			foreign.Open(OtherPass);
			foreign.Save(new MembersData { Members = { new Member { Id = 99.ToString("x32"), Name = "Alex Sample" } } });
			File.Copy(Path.Combine(elsewhere, MembersVault.FileName), Main, overwrite: true);

			var next = NewCore();
			next.Unlock(Pass);
			Assert.That(next.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(next.OpenedFromPrevious, Is.True);
			Assert.That(next.StorageNotice, Does.Contain(MembersSessionText.OpenedFromPrevious));
			Assert.That(next.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"));

			next.QuickAdd("Jo Placeholder", "");
			next.SaveNow();
			Assert.That(next.State, Is.EqualTo(MembersSessionState.ChangedElsewhere));
			Assert.That(next.OfferRestorePrevious, Is.True);

			// the passphrase must open the key this window holds, checked before anything is written
			var before = FolderSnapshot();
			Assert.That(next.RestorePrevious(OtherPass, ""), Is.EqualTo(MembersSessionText.WrongPassphrase));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));
			Assert.That(next.RestorePrevious(Pass, ""), Is.Null);
			Assert.That(next.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(next.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"));
			Assert.That(next.StorageNotice.Any(l => l.StartsWith("Storage: ", StringComparison.Ordinal) && l.Contains("members.ebmc.replaced-")), Is.True);
			Assert.That(NamesOnDisk(Pass), Is.EqualTo(new[] { "Robin Example" }));
		}

		[Test]
		public void AnUnreadableFileOffersTheNewestBackup()
		{
			var core = OpenWithRobin();
			now = now.AddDays(1);
			core.QuickAdd("Sam Testcase", "");
			core.SaveNow();
			core.CloseForExit();
			File.WriteAllText(Main, "damaged");
			File.WriteAllText(Main + ".prev", "damaged");

			var next = NewCore();
			next.Unlock(Pass);
			Assert.That(next.State, Is.EqualTo(MembersSessionState.Unreadable));
			Assert.That(next.BackupsAvailable, Is.True);
			Assert.That(next.NewestBackup, Is.Not.Null);

			// nothing readable to check against: the passphrase must open the copy itself; a wrong one changes nothing
			var before = FolderSnapshot();
			Assert.That(next.RestoreNewestBackup(OtherPass, ""), Is.EqualTo(MembersSessionText.RestoreWrongPin));
			Assert.That(next.RestoreNewestBackup("", ""), Is.EqualTo(MembersSessionText.PassphraseRequired));
			Assert.That(next.State, Is.EqualTo(MembersSessionState.Unreadable));
			Assert.That(FolderSnapshot(), Is.EqualTo(before));
			Assert.That(next.RestoreNewestBackup(Pass, ""), Is.Null);
			Assert.That(next.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(next.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"), "the backup from before the second day's first save");
		}

		[Test]
		public void IdsFromAnEarlyVersionAreSavedSoon()
		{
			var vault = new MembersVault(folder, () => now);
			vault.Open(Pass);
			vault.Save(new MembersData { Members = { new Member { Id = "robin", Name = "Robin Example" } } });
			vault.Close();

			var core = NewCore();
			int saveWanted = 0;
			core.SaveWanted += (s, e) => saveWanted++;
			core.Unlock(Pass);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			Assert.That(core.Dirty, Is.True);
			Assert.That(saveWanted, Is.EqualTo(1));
			core.SaveNow();
			Assert.That(MembersRules.IsWellFormedId(new MembersVault(folder, () => now).Open(Pass).Data.Members.Single().Id), Is.True);
		}

		[Test]
		public void DraftsSurviveARebuildAndAreCommittedOnSave()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			core.Drafts.QuickAddName = "Sam Te";
			core.Drafts.QuickAddNote = "asked about ny";
			core.Drafts.SearchText = "rob";
			core.Drafts.SetField(robin, MemberField.Notes, "Half typed");
			core.Drafts.SetField(robin, MemberField.NoteText, "brought a");

			// a rebuilt page reads everything back from the session
			var member = core.Store.FindMember(core.Drafts.SelectedMemberId);
			Assert.That(core.Drafts.QuickAddName, Is.EqualTo("Sam Te"));
			Assert.That(core.Drafts.QuickAddNote, Is.EqualTo("asked about ny"));
			Assert.That(core.FieldText(member, MemberField.Notes), Is.EqualTo("Half typed"));
			Assert.That(core.Drafts.Field(MemberField.NoteText), Is.EqualTo("brought a"));
			Assert.That(core.ListMembers(out _).Single().Name, Is.EqualTo("Robin Example"));

			// the notes field is stored with the next save; the half-typed log note is only added by its button
			core.SaveNow();
			Assert.That(core.Store.FindMember(robin).Notes, Is.EqualTo("Half typed"));
			Assert.That(core.Drafts.HasField(MemberField.Notes), Is.False);
			Assert.That(core.Drafts.Field(MemberField.NoteText), Is.EqualTo("brought a"));
			Assert.That(core.Store.FindMember(robin).Log, Has.Count.EqualTo(1));

			// lock: typed text is not kept while locked; the person is told how many entries were not saved
			var report = core.Lock();
			Assert.That(report.NotSaved, Is.False);
			Assert.That(report.DiscardedEntries, Is.EqualTo(3), "quick-add name and note, and the half-typed log note");
			Assert.That(report.Render(null), Does.Contain("3 typed entries"));
			Assert.That(core.Drafts.QuickAddName, Is.Empty);
			Assert.That(core.Drafts.QuickAddNote, Is.Empty);
			Assert.That(core.Drafts.SearchText, Is.Empty);
			Assert.That(core.Drafts.Field(robin, MemberField.NoteText), Is.Null);
			Assert.That(core.Drafts.SelectedMemberId, Is.EqualTo(robin), "only the selection is kept");
			core.Unlock(Pass);
			Assert.That(core.Store.FindMember(robin).Notes, Is.EqualTo("Half typed"));
		}

		[Test]
		public void AnInvalidFieldStaysTypedAndIsNotStored()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			core.Drafts.SetField(robin, MemberField.Name, "   ");
			core.Drafts.SetField(robin, MemberField.MetOn, "16/11/2026");

			var problems = core.CommitDetailDrafts();
			Assert.That(problems.IsValid, Is.False);
			Assert.That(problems.Problems, Does.Contain(MembersRules.DateInvalid));
			Assert.That(problems.Problems, Does.Contain(MembersRules.NameRequired));
			Assert.That(core.Store.FindMember(robin).Name, Is.EqualTo("Robin Example"));
			Assert.That(core.FieldText(core.Store.FindMember(robin), MemberField.Name), Is.EqualTo("   "));

			core.Drafts.SetField(robin, MemberField.Name, "Robin Example-Smith");
			core.Drafts.SetField(robin, MemberField.MetOn, "today");
			Assert.That(core.CommitDetailDrafts().IsValid, Is.True);
			var stored = core.Store.FindMember(robin);
			Assert.That(stored.Name, Is.EqualTo("Robin Example-Smith"));
			Assert.That(stored.MetOn, Is.EqualTo("2026-11-16"));
		}

		[Test]
		public void AnUnchangedFieldDoesNotCountAsAChange()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			var updated = core.Store.FindMember(robin).UpdatedUtc;
			now = now.AddMinutes(5);
			core.Drafts.SetField(robin, MemberField.Name, "Robin Example");
			core.CommitDetailDrafts();
			Assert.That(core.Dirty, Is.False);
			Assert.That(core.Store.FindMember(robin).UpdatedUtc, Is.EqualTo(updated));
		}

		[Test]
		public void SelectingAnotherMemberCommitsTheFirstOnesFields()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			var sam = core.QuickAdd("Sam Testcase", "").Value.Id;
			core.Select(robin);
			core.Drafts.SetField(robin, MemberField.Contact, "robin@example.invalid");
			core.Drafts.SetField(sam, MemberField.Contact, "ignored: Sam is not shown");

			core.Select(sam);
			Assert.That(core.Store.FindMember(robin).Contact, Is.EqualTo("robin@example.invalid"));
			Assert.That(core.Store.FindMember(sam).Contact, Is.Empty);
			Assert.That(core.Drafts.HasField(MemberField.Contact), Is.False);
		}

		[Test]
		public void TagsNotesArchiveAndDelete()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;

			Assert.That(core.AddTags(MemberField.InterestText, "nylon, CAD,, nylon").IsValid, Is.True);
			Assert.That(core.AddTags(MemberField.MachineText, "Pulse XE").IsValid, Is.True);
			Assert.That(core.AddTags(MemberField.InterestText, new string('x', 41)).IsValid, Is.False);
			Assert.That(core.Store.FindMember(robin).Interests, Is.EqualTo(new[] { "nylon", "CAD" }));
			Assert.That(core.RemoveTag(MemberField.InterestText, "nylon").IsValid, Is.True);
			Assert.That(core.Store.FindMember(robin).Interests, Is.EqualTo(new[] { "CAD" }));

			core.Drafts.SetField(robin, MemberField.NoteText, "printed a bracket");
			Assert.That(core.AddNote("", "printed a bracket").Ok, Is.True);
			Assert.That(core.Drafts.HasField(MemberField.NoteText), Is.False);
			Assert.That(core.AddNote("tomorrow-ish", "x").Ok, Is.False);
			Assert.That(core.Store.FindMember(robin).Log, Has.Count.EqualTo(2));

			Assert.That(core.SetArchived(true), Is.True);
			Assert.That(core.ListMembers(out _), Is.Empty, "archived members are hidden");
			core.Drafts.ShowArchived = true;
			Assert.That(core.ListMembers(out _).Single().Id, Is.EqualTo(robin));

			Assert.That(core.Delete(robin, core.LockGeneration), Is.True);
			Assert.That(core.Drafts.SelectedMemberId, Is.Null);
			core.SaveNow();
			Assert.That(NamesOnDisk(Pass), Is.Empty);
		}

		[Test]
		public void QuickAddHintsAtPossibleDuplicates()
		{
			var core = OpenWithRobin();
			Assert.That(core.PossibleDuplicateNames("robin"), Is.EqualTo(new[] { "Robin Example" }));
			Assert.That(core.PossibleDuplicateNames("Sam"), Is.Empty);
			Assert.That(core.QuickAdd("", "a note").Ok, Is.False);
		}

		[Test]
		public void TheStorageNoticeNamesFilesNeverFoldersOrContents()
		{
			var lines = MembersSessionCore.StorageLines(
				new[] { Path.Combine(root, "Members", "members.ebmc.replaced-20261116T120000Z") },
				new[] { Path.Combine(root, "Members", "Backups", "members-2026-11-15.ebmc") },
				null,
				new[] { Path.Combine(root, "Members", "members.ebmc.prev") },
				backupSkipped: true,
				previousSkipped: false);

			Assert.That(lines, Has.Count.EqualTo(4));
			Assert.That(lines[0], Is.EqualTo("Storage: 1 file(s) moved aside, never deleted: members.ebmc.replaced-20261116T120000Z"));
			Assert.That(lines.All(l => !l.Contains(root)), Is.True);
			Assert.That(lines, Does.Contain(MembersSessionText.StorageBackupSkipped));

			var many = MembersSessionCore.StorageLines(Enumerable.Range(1, 6).Select(i => "/x/members.ebmc.replaced-" + i), null, null, null, false, false);
			Assert.That(many.Single(), Does.EndWith(", +2"));
		}

		[Test]
		public void TheSessionTextHoldsNoPlaceholdersButCountsAndNames()
		{
			foreach (var field in typeof(MembersSessionText).GetFields())
			{
				var text = (string)field.GetValue(null);
				var placeholders = System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
				Assert.That(placeholders.All(p => p == "0" || p == "1"), Is.True, field.Name);
				if (placeholders.Contains("1"))
				{
					Assert.That(text, Does.StartWith("Storage: "), field.Name + ": only Storage lines take file names");
				}
			}
		}

		[Test]
		public void TypingAsksForASaveAndValidFieldsAreStoredOnTheNextSave()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			int saveWanted = 0;
			core.SaveWanted += (s, e) => saveWanted++;

			core.Drafts.SetField(robin, MemberField.Contact, "robin@example.invalid");
			Assert.That(saveWanted, Is.EqualTo(1), "typing alone asks for the save after a pause");

			core.SaveNow(waitIfBusy: false);
			Assert.That(core.Dirty, Is.False);
			Assert.That(new MembersVault(folder, () => now).Open(Pass).Data.Members.Single().Contact, Is.EqualTo("robin@example.invalid"));
		}

		[Test]
		public void OneInvalidFieldNeitherBlocksTheOthersNorIsDropped()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			core.Drafts.SetField(robin, MemberField.Name, "");
			core.Drafts.SetField(robin, MemberField.Contact, "robin@example.invalid");
			core.Drafts.SetField(robin, MemberField.Notes, "likes nylon");

			core.SaveNow();
			var stored = new MembersVault(folder, () => now).Open(Pass).Data.Members.Single();
			Assert.That(stored.Contact, Is.EqualTo("robin@example.invalid"));
			Assert.That(stored.Notes, Is.EqualTo("likes nylon"));
			Assert.That(stored.Name, Is.EqualTo("Robin Example"));
			Assert.That(core.ProblemsFor(robin).Problems, Does.Contain(MembersRules.NameRequired));

			// choosing another member keeps the invalid draft with Robin, shown again when Robin is chosen
			var sam = core.QuickAdd("Sam Testcase", "").Value.Id;
			Assert.That(core.Drafts.SelectedMemberId, Is.EqualTo(sam));
			core.Select(robin);
			var robinNow = core.Store.FindMember(robin);
			Assert.That(core.FieldText(robinNow, MemberField.Name), Is.Empty);
			Assert.That(core.ProblemsFor(robin).IsValid, Is.False);

			// fixed: stored, the problem goes
			core.Drafts.SetField(robin, MemberField.Name, "Robin Example-Smith");
			core.SaveNow();
			Assert.That(core.Store.FindMember(robin).Name, Is.EqualTo("Robin Example-Smith"));
			Assert.That(core.ProblemsFor(robin).IsValid, Is.True);
		}

		[Test]
		public void LockingWithAnInvalidDraftTellsHowManyEntriesWereNotSaved()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			core.Drafts.SetField(robin, MemberField.Name, "");
			core.Drafts.SetField(robin, MemberField.Contact, "robin@example.invalid");

			var report = core.Lock();
			Assert.That(report.DiscardedEntries, Is.EqualTo(0), "an emptied name holds no typed text");
			Assert.That(core.Drafts.EnteredCount, Is.EqualTo(0));

			core.Unlock(Pass);
			core.Select(robin);
			core.Drafts.SetField(robin, MemberField.MetOn, "16/11/2026");
			report = core.Lock();
			Assert.That(report.DiscardedEntries, Is.EqualTo(1));
			Assert.That(report.HasNews, Is.True);
			Assert.That(new MembersVault(folder, () => now).Open(Pass).Data.Members.Single().Contact, Is.EqualTo("robin@example.invalid"));
		}

		[Test]
		public void AFailedSaveAtLockIsReportedHeldAndSavedWhenPossible()
		{
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");
			LockReport report;
			using (HoldLock())
			{
				report = core.Lock();
				Assert.That(core.TrySaveHeld(waitIfBusy: false), Is.EqualTo(HeldChanges.StillHeld));
				Assert.That(core.SaveBeforeExit(), Is.True, "closing now would lose them: the app warns");
			}

			Assert.That(report.NotSaved, Is.True);
			Assert.That(report.Render(null), Does.Contain(MembersSessionText.LockNotSaved));
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));
			Assert.That(core.Store, Is.Null);

			Assert.That(core.TrySaveHeld(waitIfBusy: false), Is.EqualTo(HeldChanges.Saved));
			Assert.That(core.Vault.IsOpen, Is.False, "the key goes once they are saved");
			Assert.That(core.SaveBeforeExit(), Is.False);
			Assert.That(NamesOnDisk(Pass), Is.EquivalentTo(new[] { "Robin Example", "Sam Testcase" }));
		}

		[Test]
		public void HeldChangesAreNeverPutOverAFileChangedElsewhere()
		{
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");
			using (HoldLock())
			{
				core.Lock();
			}

			var other = NewCore();
			other.Unlock(Pass);
			other.QuickAdd("Alex Sample", "");
			other.Lock();

			Assert.That(core.TrySaveHeld(waitIfBusy: false), Is.EqualTo(HeldChanges.Lost));
			Assert.That(core.StorageNotice, Does.Contain(MembersSessionText.LostBeforeLock));
			Assert.That(NamesOnDisk(Pass), Is.EquivalentTo(new[] { "Robin Example", "Alex Sample" }));
		}

		[Test]
		public void ClosingWithUnsavedWorkIsWarnedAboutAfterOneMoreTry()
		{
			var core = OpenWithRobin();
			core.QuickAdd("Sam Testcase", "");
			using (HoldLock())
			{
				Assert.That(core.SaveBeforeExit(), Is.True);
			}

			Assert.That(core.SaveBeforeExit(), Is.False, "the second try saved them");
			core.Drafts.QuickAddName = "Alex Sam";
			Assert.That(core.SaveBeforeExit(), Is.True, "typed text not added yet counts too");
		}

		[Test]
		public void ADeleteConfirmedAfterTheLockWasCycledIsRefused()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			int asked = core.LockGeneration;
			core.Lock();
			core.Unlock(Pass);

			Assert.That(core.Delete(robin, asked), Is.False);
			Assert.That(core.Store.FindMember(robin), Is.Not.Null);

			int changes = 0;
			core.StateChanged += (s, e) => changes++;
			int revision = core.Revision;
			Assert.That(core.Delete(robin, core.LockGeneration), Is.True);
			Assert.That(core.Revision, Is.GreaterThan(revision), "whichever page is showing rebuilds its list");
			Assert.That(changes, Is.GreaterThan(0));
		}

		[Test]
		public void NoticesAreTranslatedBeforeFileNamesArePutIn()
		{
			var translated = new List<string>();
			string Localize(string s)
			{
				translated.Add(s);
				return s.Replace("moved aside", "MOVED");
			}

			var notices = MembersSessionCore.StorageNotices(new[] { Path.Combine(root, "members.ebmc.replaced-20261116T120000Z") }, null, null, null, true, false);
			var lines = notices.Select(n => n.Render(Localize)).ToList();

			Assert.That(lines[0], Is.EqualTo("Storage: 1 file(s) MOVED, never deleted: members.ebmc.replaced-20261116T120000Z"));
			Assert.That(translated, Is.EqualTo(new[] { MembersSessionText.StorageSetAside, MembersSessionText.StorageBackupSkipped }));
			Assert.That(translated.Any(t => t.Contains("members.ebmc.replaced")), Is.False);

			var report = new LockReport { NotSaved = true, DiscardedEntries = 2 };
			translated.Clear();
			report.Render(Localize);
			Assert.That(translated, Is.EqualTo(new[] { MembersSessionText.LockNotSaved, MembersSessionText.LockDiscardedEntries }));
		}

		[Test]
		public void EveryPathThatDropsUnsavedChangesLeavesANotice()
		{
			// 1. a background save finds another window's change
			var core = OpenWithRobin();
			var other = NewCore();
			other.Unlock(Pass);
			other.QuickAdd("Alex Sample", "");
			other.SaveNow();
			core.QuickAdd("Sam Testcase", "");
			core.SaveNow(waitIfBusy: false);
			Assert.That(core.StorageNotice, Does.Contain("Another window changed the members list, so 1 change(s) made here could not be kept."));

			// 2. a reload while changes are not saved (the save keeps failing)
			core.DismissNotice();
			core.Reload(Pass);
			core.Vault.FaultPoint = step =>
			{
				if (step == "main-write")
				{
					throw new IOException("simulated");
				}
			};
			core.QuickAdd("Jo Placeholder", "");
			core.SaveNow();
			Assert.That(core.Dirty, Is.True);
			core.Reload(Pass);
			Assert.That(core.StorageNotice, Does.Contain("1 change(s) made here were not saved and were not kept."));

			// 3. a restore while changes are not saved
			core.DismissNotice();
			core.QuickAdd("Kim Example", "");
			core.SaveNow();
			Assert.That(core.Dirty, Is.True);
			Assert.That(core.RestorePrevious(Pass, ""), Is.Null);
			Assert.That(core.StorageNotice, Does.Contain("1 change(s) made here were not saved and were not kept."));
			core.Vault.FaultPoint = null;

			// 4. the PIN change path (above) and the lock (held, and told in the lock report) are covered elsewhere;
			// a lock never drops them silently either
			core.QuickAdd("Lee Sample", "");
			using (HoldLock())
			{
				Assert.That(core.Lock().NotSaved, Is.True);
			}
		}

		[Test]
		public void AnUnexpectedFailureWhileOpeningShowsTheNotOpenView()
		{
			FileWithRobin();
			var core = NewCore();
			core.Vault.FaultPoint = step =>
			{
				if (step.StartsWith("open", StringComparison.Ordinal))
				{
					throw new InvalidOperationException("simulated: text that must never be shown");
				}
			};

			Assert.That(core.Unlock(Pass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked), "the not-open view with Try again");
			Assert.That(core.StorageNotice, Does.Contain(MembersSessionText.CouldNotOpen));
			Assert.That(core.StorageNotice.Any(l => l.Contains("simulated")), Is.False);

			// the app's own catch does the same if anything escapes
			core.OpenFailed();
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Locked));

			core.Vault.FaultPoint = null;
			Assert.That(core.Unlock(Pass), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
		}

		[Test]
		public void AfterAReloadDraftsOfMembersNoLongerThereAreDroppedWithANotice()
		{
			var core = OpenWithRobin();
			var robin = core.Drafts.SelectedMemberId;
			var sam = core.QuickAdd("Sam Testcase", "").Value.Id;
			core.SaveNow();
			core.Drafts.SetField(sam, MemberField.Name, "");
			core.Drafts.SetField(sam, MemberField.NoteText, "asked about the laser");
			core.CommitDetailDrafts();

			// another window deletes Sam
			var other = NewCore();
			other.Unlock(Pass);
			Assert.That(other.Delete(sam, other.LockGeneration), Is.True);
			other.SaveNow();

			core.Reload(Pass);
			Assert.That(core.Store.FindMember(sam), Is.Null);
			Assert.That(core.Drafts.SelectedMemberId, Is.Null, "a selection of a member no longer there is cleared");
			Assert.That(core.Drafts.MembersWithDrafts, Does.Not.Contain(sam));
			Assert.That(core.StorageNotice, Does.Contain("1 typed entries for members no longer in the list were not kept."));
			Assert.That(core.Store.FindMember(robin), Is.Not.Null);
		}
	}
}
