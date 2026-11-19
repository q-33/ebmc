using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members & Projects: the encrypted members file (AES-GCM, key wrapped with the maintainer PIN), atomic saves,
	/// the previous copy, daily backups, restores, PIN changes, the outside-change check, failures at every write
	/// step and tampering. Every test works in a fresh temporary folder that is deleted afterwards. Every name here is
	/// invented.
	/// </summary>
	[TestFixture]
	public class MembersVaultTests
	{
		private const string Pin = "135792";
		private const string OtherPin = "246801";
		private const string ThirdPin = "975310";

		// ids in the app's format
		private static readonly string RobinId = 1.ToString("x32");
		private static readonly string ArmId = 2.ToString("x32");

		private string root;
		private string folder;
		private DateTime now;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "ebmc-members-test-" + Guid.NewGuid().ToString("N"));
			folder = Path.Combine(root, "vault");
			Directory.CreateDirectory(folder);
			now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
			{
				if (!OperatingSystem.IsWindows())
				{
					foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Append(root))
					{
						File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
					}
				}

				Directory.Delete(root, recursive: true);
			}
		}

		private string Main => Path.Combine(folder, MembersVault.FileName);

		private string Previous => Main + ".prev";

		private MembersVault NewVault(string at = null) => new MembersVault(at ?? folder, () => now);

		private static MembersData Sample(string note = "met at open night")
		{
			// invented
			return new MembersData
			{
				Members =
				{
					new Member
					{
						Id = RobinId, Name = "Robin Example", Contact = "robin@example.invalid", Interests = { "nylon" },
						Log = { new LogEntry { Id = "l1", Date = "2026-11-16", Text = note } },
					},
				},
				Projects = { new Project { Id = ArmId, Title = "Robot arm", MemberIds = { RobinId } } },
			};
		}

		private static string Note(OpenResult result) => result.Data.Members[0].Log[0].Text;

		/// <summary>Every file under the test folder with its hash, to prove nothing was written.</summary>
		private Dictionary<string, string> Snapshot(string at = null)
		{
			return Directory.GetFiles(at ?? folder, "*", SearchOption.AllDirectories)
				.Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
				.ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
		}

		private MembersVault SavedVault(MembersData data = null, string pin = Pin)
		{
			var vault = NewVault();
			Assert.That(vault.Open(pin).Status, Is.EqualTo(OpenStatus.NewEmpty));
			Assert.That(vault.Save(data ?? Sample()).Status, Is.EqualTo(SaveStatus.Saved));
			return vault;
		}

		private static bool OpensWith(string file, string pin)
		{
			return MembersCrypto.OpenFile(File.ReadAllBytes(file), pin, out _, out _, out _) == EnvelopeOpenStatus.Ok;
		}

		/// <summary>A members file with the same data under its own key and PIN, made in another folder.</summary>
		private byte[] ForeignFile(string pin, string note)
		{
			var other = Path.Combine(root, "other-" + Guid.NewGuid().ToString("N"));
			var vault = NewVault(other);
			vault.Open(pin);
			vault.Save(Sample(note));
			return File.ReadAllBytes(Path.Combine(other, MembersVault.FileName));
		}

		private static void CopyDirectory(string from, string to)
		{
			Directory.CreateDirectory(to);
			foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal)))
			{
				var target = Path.Combine(to, Path.GetRelativePath(from, file));
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				File.Copy(file, target);
			}
		}

		private IEnumerable<string> TempFiles() => Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories);

		[Test]
		public void AesGcmIsSupportedOnThisMachine()
		{
			Assert.That(AesGcm.IsSupported, Is.True);
		}

		[Test]
		public void ANewFolderOpensEmptyAndWritesNothing()
		{
			var vault = NewVault();
			var result = vault.Open(Pin);

			Assert.That(result.Status, Is.EqualTo(OpenStatus.NewEmpty));
			Assert.That(result.Data.Members, Is.Empty);
			Assert.That(vault.IsOpen, Is.True);
			Assert.That(Snapshot(), Is.Empty);
		}

		[Test]
		public void ARelativeFolderIsMadeAbsoluteOnce()
		{
			var vault = new MembersVault(Path.Combine("some", "relative", "folder"), () => now);
			Assert.That(Path.IsPathRooted(vault.Folder), Is.True);
			Assert.That(Path.IsPathRooted(vault.FilePath), Is.True);
		}

		[Test]
		public void SaveThenOpenWithTheRightPinRoundTrips()
		{
			SavedVault();

			var reopened = NewVault().Open(Pin);
			Assert.That(reopened.Status, Is.EqualTo(OpenStatus.Opened));
			Assert.That(MembersExport.ToJson(reopened.Data), Is.EqualTo(MembersExport.ToJson(Sample())));
		}

		[Test]
		public void TheFileContainsNoPlainText()
		{
			SavedVault();
			var bytes = File.ReadAllBytes(Main);

			foreach (var secret in new[] { "Robin", "Example", "example.invalid", "nylon", "open night", "Robot arm", "Members" })
			{
				Assert.That(Contains(bytes, Encoding.UTF8.GetBytes(secret)), Is.False, secret);
				Assert.That(Contains(bytes, Encoding.Unicode.GetBytes(secret)), Is.False, secret);
			}

			Assert.That(Encoding.UTF8.GetString(bytes), Does.StartWith("{\"format\":\"ebmc-members\",\"v\":1,"));
		}

		[Test]
		public void AWrongPinIsAResultAndChangesNothing()
		{
			SavedVault();
			var before = Snapshot();

			var vault = NewVault();
			OpenResult result = null;
			Assert.DoesNotThrow(() => result = vault.Open(OtherPin));

			Assert.That(result.Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(result.Data, Is.Null);
			Assert.That(vault.IsOpen, Is.False);
			Assert.That(vault.Save(new MembersData()).Status, Is.EqualTo(SaveStatus.NotOpen));
			Assert.That(vault.Rewrap(Pin).Status, Is.EqualTo(SaveStatus.NotOpen));
			Assert.That(NewVault().Open("").Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(Snapshot(), Is.EqualTo(before));

			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.Opened));
		}

		[Test]
		public void ADamagedFileFallsBackToThePreviousCopy()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));

			var bytes = File.ReadAllBytes(Main);
			File.WriteAllBytes(Main, bytes.Take(bytes.Length / 2).ToArray());

			var truncated = NewVault().Open(Pin);
			Assert.That(truncated.Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
			Assert.That(Note(truncated), Is.EqualTo("first"));

			File.WriteAllText(Main, "Robin Example was here");
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));

			File.Delete(Main);
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
		}

		[TestCase("salt")]
		[TestCase("wrap-nonce")]
		[TestCase("wrap-tag")]
		[TestCase("wrap-ct")]
		public void ADamagedKeyPartFallsBackToThePreviousCopy(string part)
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));
			File.WriteAllBytes(Main, DamagePart(File.ReadAllBytes(Main), part));

			var result = NewVault().Open(Pin);
			Assert.That(result.Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
			Assert.That(Note(result), Is.EqualTo("first"));

			// both copies refuse the PIN: wrong PIN
			File.WriteAllBytes(Previous, DamagePart(File.ReadAllBytes(Previous), part));
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.WrongPin));
		}

		[Test]
		public void SavingAfterAFallbackSetsTheMainFileAsideAndKeepsThePreviousCopy()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));
			var previous = File.ReadAllBytes(Previous);
			File.WriteAllText(Main, "damaged");

			var reopened = NewVault();
			Assert.That(reopened.Open(Pin).Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
			var saved = reopened.Save(Sample("third"));
			Assert.That(saved.Status, Is.EqualTo(SaveStatus.Saved));

			Assert.That(File.ReadAllBytes(Previous), Is.EqualTo(previous));
			Assert.That(saved.SetAside, Has.Count.EqualTo(1));
			Assert.That(Path.GetFileName(saved.SetAside[0]), Does.StartWith("members.ebmc.replaced-20261116T120000Z"));
			Assert.That(File.ReadAllText(saved.SetAside[0]), Is.EqualTo("damaged"));
			Assert.That(reopened.ListSetAside(), Is.EqualTo(saved.SetAside));
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("third"));
		}

		[Test]
		public void AMainFileUnderAnotherKeyIsNeverReplacedByASaveFromThePreviousCopy()
		{
			// the main file holds newer data under another PIN; the previous copy opens with this one
			var vault = SavedVault(Sample("old"));
			vault.Save(Sample("old again"));
			var newer = ForeignFile(OtherPin, "newer, other PIN");
			File.WriteAllBytes(Main, newer);
			var before = Snapshot();

			var reopened = NewVault();
			Assert.That(reopened.Open(Pin).Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
			Assert.That(reopened.Save(Sample("edited")).Status, Is.EqualTo(SaveStatus.ChangedElsewhere));
			Assert.That(Snapshot(), Is.EqualTo(before));

			// the explicit way on: restore the previous copy, which keeps the newer file aside
			var restored = reopened.Restore(Previous, Pin);
			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));
			var aside = restored.SetAside.Single(f => File.ReadAllBytes(f).AsSpan().SequenceEqual(newer));
			Assert.That(OpensWith(aside, OtherPin), Is.True);
			Assert.That(reopened.Save(Sample("edited")).Status, Is.EqualTo(SaveStatus.Saved));
		}

		[Test]
		public void BackupsAreNeverOpenedAutomatically()
		{
			var vault = SavedVault(Sample("day one"));
			now = now.AddDays(1);
			vault.Save(Sample("day two"));
			File.WriteAllText(Main, "damaged");
			File.WriteAllText(Previous, "damaged");
			var before = Snapshot();

			var result = NewVault().Open(Pin);
			Assert.That(result.Status, Is.EqualTo(OpenStatus.Unreadable));
			Assert.That(result.BackupsAvailable, Is.True);
			Assert.That(Snapshot(), Is.EqualTo(before));

			File.Delete(Main);
			File.Delete(Previous);
			var gone = NewVault().Open(Pin);
			Assert.That(gone.Status, Is.EqualTo(OpenStatus.Unreadable), "not a new empty list while backups exist");
			Assert.That(gone.BackupsAvailable, Is.True);
		}

		[Test]
		public void AnOldPinNeverOpensOldDataThatCouldThenOverwriteNewerData()
		{
			// the reviewed scenario: PIN changed, main damaged, the old PIN typed
			var vault = SavedVault(Sample("day one"));
			now = now.AddDays(1);
			vault.Save(Sample("day two"));
			vault.Save(Sample("day two later"));
			Assert.That(vault.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			File.WriteAllText(Main, "damaged");
			var before = Snapshot();

			var old = NewVault();
			var result = old.Open(Pin);
			Assert.That(result.Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(old.IsOpen, Is.False);
			Assert.That(old.Save(Sample("from the old PIN")).Status, Is.EqualTo(SaveStatus.NotOpen));
			Assert.That(Snapshot(), Is.EqualTo(before));

			// the new PIN still opens the previous copy
			var current = NewVault().Open(OtherPin);
			Assert.That(current.Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
			Assert.That(Note(current), Is.EqualTo("day two"));
		}

		[Test]
		public void NothingReadableIsUnreadableAndChangesNothing()
		{
			SavedVault();
			File.WriteAllText(Main, "damaged");
			var before = Snapshot();

			var vault = NewVault();
			OpenResult result = null;
			Assert.DoesNotThrow(() => result = vault.Open(Pin));
			Assert.That(result.Status, Is.EqualTo(OpenStatus.Unreadable));
			Assert.That(result.BackupsAvailable, Is.False);
			Assert.That(vault.IsOpen, Is.False);
			Assert.That(vault.Save(new MembersData()).Status, Is.EqualTo(SaveStatus.NotOpen));
			Assert.That(Snapshot(), Is.EqualTo(before));
		}

		[Test]
		public void DamagedJsonInsideAValidEnvelopeIsUnreadableWithoutThrowing()
		{
			var key = MembersCrypto.NewDataKey();
			var wrapped = MembersCrypto.WrapKey(key, Pin);
			var plain = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"Members\":[{\"Name\":\"Robin Example\",\"CreatedUtc\":\"Robin Example\"}]}");
			File.WriteAllBytes(Main, MembersCrypto.SealFile(plain, key, wrapped));

			OpenResult result = null;
			Assert.DoesNotThrow(() => result = NewVault().Open(Pin));
			Assert.That(result.Status, Is.EqualTo(OpenStatus.Unreadable));
		}

		[Test]
		public void AFileFromANewerVersionIsReportedAndNotReplaced()
		{
			File.WriteAllBytes(Main, NewerFile(MembersCrypto.NewDataKey(), Pin, out _));
			var before = Snapshot();

			var vault = NewVault();
			Assert.That(vault.Open(Pin).Status, Is.EqualTo(OpenStatus.NewerVersion));
			Assert.That(vault.Save(new MembersData()).Status, Is.EqualTo(SaveStatus.NotOpen));
			Assert.That(Snapshot(), Is.EqualTo(before));
		}

		[Test]
		public void ADamagedMainAndANewerPreviousCopyReportNewerVersion()
		{
			File.WriteAllText(Main, "damaged");
			File.WriteAllBytes(Previous, NewerFile(MembersCrypto.NewDataKey(), Pin, out _));
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.NewerVersion));
		}

		[Test]
		public void ANewerPreviousCopyIsSetAsideNotOverwritten()
		{
			var vault = SavedVault(Sample("first"));
			OpenKey(Main, Pin, out var key, out var wrapped);
			var newer = MembersCrypto.SealFile(Encoding.UTF8.GetBytes("{\"SchemaVersion\":" + Newer + "}"), key, wrapped);
			File.WriteAllBytes(Previous, newer);

			var saved = vault.Save(Sample("second"));
			Assert.That(saved.Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(File.ReadAllBytes(saved.SetAside.Single()), Is.EqualTo(newer));
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("second"));
		}

		[Test]
		public void PruningKeepsABackupFromANewerVersion()
		{
			var vault = SavedVault();
			OpenKey(Main, Pin, out var key, out var wrapped);
			var backups = Path.Combine(folder, "Backups");
			Directory.CreateDirectory(backups);
			var newer = Path.Combine(backups, "members-2020-01-01.ebmc");
			File.WriteAllBytes(newer, MembersCrypto.SealFile(Encoding.UTF8.GetBytes("{\"SchemaVersion\":" + Newer + "}"), key, wrapped));
			var ordinary = Path.Combine(backups, "members-2020-01-02.ebmc");
			File.Copy(Main, ordinary);
			for (int day = 1; day <= 14; day++)
			{
				File.Copy(Main, Path.Combine(backups, $"members-2025-01-{day:00}.ebmc"));
			}

			vault.Save(Sample("again"));
			Assert.That(File.Exists(newer), Is.True);
			Assert.That(File.Exists(ordinary), Is.False);
		}

		[Test]
		public void RewrapChangesThePin()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));

			Assert.That(vault.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.WrongPin), "the old PIN no longer opens the file");

			var reopened = NewVault().Open(OtherPin);
			Assert.That(reopened.Status, Is.EqualTo(OpenStatus.Opened));
			Assert.That(Note(reopened), Is.EqualTo("second"));

			// the previous copy is re-encrypted too, so it is still a usable fallback with the new PIN
			File.WriteAllText(Main, "damaged");
			var fallback = NewVault().Open(OtherPin);
			Assert.That(fallback.Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
			Assert.That(Note(fallback), Is.EqualTo("first"));
		}

		[Test]
		public void AfterRewrapNoManagedFileOpensWithTheOldPin()
		{
			var vault = SavedVault(Sample("day one"));
			for (int day = 1; day <= 3; day++)
			{
				now = now.AddDays(1);
				vault.Save(Sample("day " + day));
			}

			// a backup saved with an even older PIN, which this vault cannot re-encrypt
			var older = Path.Combine(folder, "Backups", "members-2026-01-01.ebmc");
			File.WriteAllBytes(older, ForeignFile(ThirdPin, "older PIN"));
			var olderBytes = File.ReadAllBytes(older);
			int backupCount = vault.ListBackups().Count;

			now = now.AddDays(1); // a backup would be due today; Rewrap must not take one
			var result = vault.Rewrap(OtherPin);

			Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(result.LeftUnderOtherPins, Is.EqualTo(new[] { older }));
			Assert.That(vault.ListBackups(), Has.Count.EqualTo(backupCount), "no backup taken");
			Assert.That(File.ReadAllBytes(older), Is.EqualTo(olderBytes), "left untouched");
			Assert.That(TempFiles(), Is.Empty);

			foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(f => f != older && !f.EndsWith(".lock", StringComparison.Ordinal)))
			{
				Assert.That(OpensWith(file, Pin), Is.False, file);
				Assert.That(OpensWith(file, OtherPin), Is.True, file);
			}
		}

		[Test]
		public void RewrapAfterOpeningFromThePreviousCopyAsksToSaveFirst()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));
			File.WriteAllText(Main, "damaged");
			var reopened = NewVault();
			reopened.Open(Pin);
			var before = Snapshot();

			Assert.That(reopened.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.SaveFirst));
			Assert.That(Snapshot(), Is.EqualTo(before));
			Assert.That(reopened.IsOpen, Is.True);

			reopened.Save(Sample("third"));
			Assert.That(reopened.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("third"));
			Assert.That(OpensWith(Previous, Pin), Is.False);
		}

		[Test]
		public void AfterRewrapTheVaultKeepsSavingWithTheNewPin()
		{
			var vault = SavedVault();
			Assert.That(vault.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(vault.Save(Sample("after the change")).Status, Is.EqualTo(SaveStatus.Saved));

			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("after the change"));
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.WrongPin));
		}

		[Test]
		public void RewrapBeforeTheFirstSaveUsesTheNewPin()
		{
			var vault = NewVault();
			vault.Open(Pin);
			Assert.That(vault.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(Snapshot(), Is.Empty);
			vault.Save(Sample());
			Assert.That(NewVault().Open(OtherPin).Status, Is.EqualTo(OpenStatus.Opened));
		}

		/// <summary>A pristine folder: main "day 2", previous copy "day 1", backups "day 0" and "day 1".</summary>
		private string PristineThreeDays()
		{
			var vault = SavedVault(Sample("day 0"));
			for (int day = 1; day <= 2; day++)
			{
				now = now.AddDays(1);
				vault.Save(Sample("day " + day));
			}

			var pristine = Path.Combine(root, "pristine");
			CopyDirectory(folder, pristine);
			return pristine;
		}

		private void ResetFolder(string from)
		{
			Directory.Delete(folder, recursive: true);
			CopyDirectory(from, folder);
		}

		[Test]
		public void ARewrapFailureBeforeTheMainFileKeepsTheOldPinAndAfterItTheNewOne()
		{
			var pristine = PristineThreeDays();

			var steps = new List<string>();
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step => steps.Add(step);
			Assert.That(counting.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(steps.IndexOf("rewrap-switch-main"), Is.GreaterThan(steps.IndexOf("rewrap-journal-rename")));
			Assert.That(steps.IndexOf("rewrap-switch"), Is.GreaterThan(steps.IndexOf("rewrap-switch-main")), "the main file first");
			Assert.That(steps.Last(), Is.EqualTo("rewrap-journal-delete"));
			int mainSwitch = steps.IndexOf("rewrap-switch-main");

			for (int failAt = 0; failAt < steps.Count; failAt++)
			{
				ResetFolder(pristine);
				var subject = NewVault();
				subject.Open(Pin);
				int calls = 0;
				subject.FaultPoint = step =>
				{
					if (calls++ == failAt)
					{
						throw new IOException("injected");
					}
				};

				string step = steps[failAt];
				if (failAt <= mainSwitch)
				{
					var e = Assert.Throws<MembersFileException>(() => subject.Rewrap(OtherPin), step);
					Assert.That(e.Problem, Is.EqualTo(MembersFileProblem.SaveFailed));
					Assert.That(TempFiles(), Is.Empty, step);
					Assert.That(File.Exists(subject.JournalPath), Is.False, step);
					Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("day 2"), step + ": the old PIN is still in force");
					Assert.That(NewVault().Open(OtherPin).Status, Is.EqualTo(OpenStatus.WrongPin), step);
				}
				else
				{
					var result = subject.Rewrap(OtherPin);
					Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved), step);
					Assert.That(result.PinChangePending, Is.True, step);
					var old = NewVault().Open(Pin);
					Assert.That(old.Status, Is.EqualTo(OpenStatus.WrongPin), step);
					Assert.That(old.PinChangeInterrupted, Is.True, step);
				}

				// the vault goes on: its next write finishes or no longer needs the change
				subject.FaultPoint = null;
				Assert.That(subject.Save(Sample("after the failure")).Status, Is.EqualTo(SaveStatus.Saved), step);
				Assert.That(File.Exists(subject.JournalPath), Is.False, step);
				Assert.That(subject.Rewrap(ThirdPin).Status, Is.EqualTo(SaveStatus.Saved), step);
				Assert.That(Note(NewVault().Open(ThirdPin)), Is.EqualTo("after the failure"), step);
				AssertNoFileOpensWith(step, Pin, OtherPin);
				Assert.That(TempFiles(), Is.Empty, step);
			}
		}

		[Test]
		public void ACrashAtAnyPointOfAPinChangeLeavesTheNewestDataUnderExactlyOnePin()
		{
			var pristine = PristineThreeDays();

			// the folder as it is just before each write, rename or delete, and at the end
			var crashes = new List<(string step, string at)>();
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step =>
			{
				var at = Path.Combine(root, "crash-" + crashes.Count);
				CopyDirectory(folder, at);
				crashes.Add((step, at));
			};
			Assert.That(counting.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			var end = Path.Combine(root, "crash-end");
			CopyDirectory(folder, end);
			crashes.Add(("end", end));

			// staging main, .prev and two backups; the journal; main; the three others; deleting the journal; the end
			Assert.That(crashes.Select(c => c.step), Is.EqualTo(new[]
			{
				"rewrap-stage-main", "rewrap-stage", "rewrap-stage", "rewrap-stage", "rewrap-journal-write", "rewrap-journal-rename",
				"rewrap-switch-main", "rewrap-switch", "rewrap-switch", "rewrap-switch", "rewrap-journal-delete", "end",
			}));

			foreach (var (step, at) in crashes)
			{
				string label = step + " (" + Path.GetFileName(at) + ")";

				// opening with each PIN: exactly one opens, with the newest data, never an older copy
				ResetFolder(at);
				var old = NewVault().Open(Pin);
				ResetFolder(at);
				var renewed = NewVault().Open(OtherPin);
				Assert.That(new[] { old.Status, renewed.Status }.Count(s => s == OpenStatus.Opened), Is.EqualTo(1), label);
				Assert.That(new[] { old.Status, renewed.Status }, Has.None.EqualTo(OpenStatus.OpenedFromPrevious), label);
				Assert.That(new[] { old.Status, renewed.Status }.Where(s => s != OpenStatus.Opened), Is.All.EqualTo(OpenStatus.WrongPin), label);
				Assert.That(Note(old.IsOpen ? old : renewed), Is.EqualTo("day 2"), label);
				string inForce = old.IsOpen ? Pin : OtherPin;

				// retry with the same new PIN, and with a different one
				foreach (var retry in new[] { OtherPin, ThirdPin })
				{
					ResetFolder(at);
					var vault = NewVault();
					Assert.That(vault.Open(inForce).Status, Is.EqualTo(OpenStatus.Opened), label);
					var result = vault.Rewrap(retry);
					Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved), label);
					Assert.That(result.PinChangePending, Is.False, label);
					Assert.That(result.LeftUnderOtherPins, Is.Empty, label);
					Assert.That(File.Exists(vault.JournalPath), Is.False, label);
					Assert.That(TempFiles(), Is.Empty, label);
					Assert.That(Note(NewVault().Open(retry)), Is.EqualTo("day 2"), label);

					var abandoned = new[] { Pin, OtherPin, ThirdPin }.Where(p => p != retry).ToArray();
					AssertNoFileOpensWith(label + " retry " + retry, abandoned);
				}
			}
		}

		/// <summary>No file in the folder (any depth) opens with any of <paramref name="pins"/>.</summary>
		private void AssertNoFileOpensWith(string label, params string[] pins)
		{
			var keks = new Dictionary<string, byte[]>();
			foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
			{
				if (!MembersCrypto.TryParseEnvelope(File.ReadAllBytes(file), out var w, out _))
				{
					continue;
				}

				foreach (var pin in pins)
				{
					var cacheKey = pin + "|" + Convert.ToBase64String(w.Salt);
					if (!keks.TryGetValue(cacheKey, out var kek))
					{
						keks[cacheKey] = kek = MembersCrypto.DeriveKek(pin, w.Salt, w.Iterations);
					}

					Assert.That(MembersCrypto.UnwrapKeyWithKek(w, kek), Is.Null, label + ": " + Path.GetFileName(file) + " opens with " + pin);
				}
			}
		}

		[Test]
		public void SetAsideFilesCanBeListedAndRestored()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));
			File.WriteAllBytes(Main, ForeignFile(OtherPin, "newest, other PIN"));

			var reopened = NewVault();
			Assert.That(reopened.Open(Pin).Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
			var fromPrevious = reopened.Restore(Previous, Pin);
			Assert.That(fromPrevious.Status, Is.EqualTo(OpenStatus.Restored));
			var aside = fromPrevious.SetAside.Single(f => OpensWith(f, OtherPin));
			Assert.That(reopened.ListSetAside(), Does.Contain(aside));

			// bring the set-aside data back, under this vault's PIN
			var restored = reopened.Restore(aside, OtherPin);
			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));
			Assert.That(Note(restored), Is.EqualTo("newest, other PIN"));
			Assert.That(OpensWith(Main, Pin), Is.True);
			Assert.That(OpensWith(Main, OtherPin), Is.False);
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("newest, other PIN"));
		}

		[Test]
		public void RestoreChecksTheCurrentPinAndNeverAssumesIt()
		{
			var vault = SavedVault(Sample("current"));
			now = now.AddDays(1);
			vault.Save(Sample("current, later"));
			var backup = vault.ListBackups().Single();
			var before = Snapshot();

			// not open: a typo in the current PIN is refused, nothing changes
			var restorer = NewVault();
			var typo = restorer.Restore(backup, Pin, "135793");
			Assert.That(typo.Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(restorer.IsOpen, Is.False);
			Assert.That(Snapshot(), Is.EqualTo(before));
			Assert.That(restorer.Restore(backup, Pin, null).Status, Is.EqualTo(OpenStatus.WrongPin));

			// open: the vault's own checked key is used, whatever is passed
			var open = NewVault();
			open.Open(Pin);
			var restored = open.Restore(backup, null, "000000");
			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));
			Assert.That(Note(restored), Is.EqualTo("current"));
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("current"));
		}

		[Test]
		public void RestoreWithTheVaultOpenHonoursTheAppsPinCheck()
		{
			var vault = SavedVault(Sample("current"));
			now = now.AddDays(1);
			vault.Save(Sample("current, later"));
			var backup = vault.ListBackups().Single();
			var before = Snapshot();
			Func<string, bool> isCurrent = p => p == Pin;

			var open = NewVault();
			Assert.That(open.Open(Pin).Status, Is.EqualTo(OpenStatus.Opened));

			// a PIN the app's check rejects, no PIN, and a check that throws: WrongPin, nothing written, still open
			Assert.That(open.Restore(backup, Pin, "000000", isCurrent).Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(open.Restore(backup, Pin, null, isCurrent).Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(open.Restore(backup, Pin, Pin, p => throw new InvalidOperationException()).Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(Snapshot(), Is.EqualTo(before));
			Assert.That(open.IsOpen, Is.True);

			// the check accepts a PIN, but the open vault's key is not under it: refused too
			Assert.That(open.Restore(backup, Pin, OtherPin, p => p == OtherPin).Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(Snapshot(), Is.EqualTo(before));

			// the vault still saves after the refusals
			Assert.That(open.Save(Sample("current, later")).Status, Is.EqualTo(SaveStatus.Saved));

			// the right PIN restores
			var restored = open.Restore(backup, Pin, Pin, isCurrent);
			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));
			Assert.That(Note(restored), Is.EqualTo("current"));
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("current"));
		}

		[Test]
		public void WithNothingOnDiskToCheckRestoreNeedsTheAppsPinCheck()
		{
			var vault = SavedVault(Sample("current"));
			now = now.AddDays(1);
			vault.Save(Sample("current, later"));
			var backup = vault.ListBackups().Single();
			File.Delete(Main);
			File.Delete(Previous);
			var before = Snapshot();

			// no check given: only the restored file's own PIN is accepted
			Assert.That(NewVault().Restore(backup, Pin, OtherPin).Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(Snapshot(), Is.EqualTo(before));

			// the app's check refuses a typo, and an exception from it counts as "no"
			Func<string, bool> isCurrent = p => p == OtherPin;
			Assert.That(NewVault().Restore(backup, Pin, "246802", isCurrent).Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(NewVault().Restore(backup, Pin, OtherPin, p => throw new InvalidOperationException()).Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(Snapshot(), Is.EqualTo(before));

			var restored = NewVault().Restore(backup, Pin, OtherPin, isCurrent);
			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));
			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("current"));
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.WrongPin));

			// a main file whose envelope does not even read is no evidence; the file's own PIN still works
			File.WriteAllText(Main, "damaged");
			File.Delete(Previous);
			Assert.That(NewVault().Restore(backup, Pin, Pin).Status, Is.EqualTo(OpenStatus.Restored));
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("current"));
		}

		[Test]
		public void TheAppsPinCheckIsAppliedEvenWhenTheFilesCouldCheck()
		{
			var vault = SavedVault(Sample("current"));
			now = now.AddDays(1);
			vault.Save(Sample("later"));
			var backup = vault.ListBackups().Single();
			Assert.That(NewVault().Restore(backup, Pin, Pin, p => false).Status, Is.EqualTo(OpenStatus.WrongPin));
			Assert.That(NewVault().Restore(backup, Pin, Pin, p => p == Pin).Status, Is.EqualTo(OpenStatus.Restored));
		}

		[Test]
		public void RestoreReportsTheFilesItCouldNotReKeyAndCapsTheSlowDerivations()
		{
			// no main file, no previous copy; a backup from another PIN is restored under this PIN with a new key
			var backups = Path.Combine(folder, "Backups");
			Directory.CreateDirectory(backups);
			var chosen = Path.Combine(backups, "members-2026-01-01.ebmc");
			File.WriteAllBytes(chosen, ForeignFile(OtherPin, "chosen"));

			// files under this PIN, each with its own salt: more than the cap
			int underPin = MembersVault.MaxSaltsPerOperation + 5;
			for (int i = 0; i < underPin; i++)
			{
				File.WriteAllBytes(Path.Combine(backups, $"members-2025-01-{i + 1:00}.ebmc"), ForeignFile(Pin, "old " + i));
			}

			var underOther = Path.Combine(backups, "members-2024-01-01.ebmc");
			File.WriteAllBytes(underOther, ForeignFile(ThirdPin, "third"));

			var watch = System.Diagnostics.Stopwatch.StartNew();
			var restored = NewVault().Restore(chosen, OtherPin, Pin, p => p == Pin);
			watch.Stop();

			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));

			// one derivation checked the chosen file against this PIN; the rest of the cap re-keys files under it
			var mainKey = MainKey(Pin);
			int reKeyed = Directory.GetFiles(backups).Count(f => MembersCrypto.OpenFileWithKey(File.ReadAllBytes(f), mainKey) != null);
			Assert.That(reKeyed, Is.EqualTo(MembersVault.MaxSaltsPerOperation - 1));
			Assert.That(restored.NotReKeyed, Does.Contain(underOther).And.Contain(chosen));
			Assert.That(restored.NotReKeyed, Has.Count.EqualTo(underPin + 2 - reKeyed));
			Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(10));
		}

		private byte[] MainKey(string pin)
		{
			OpenKey(Main, pin, out var key, out _);
			return key;
		}

		[Test]
		public void TemporaryFilesLeftByACrashAreDeletedOnOpen()
		{
			var vault = SavedVault(Sample("first"));
			now = now.AddDays(1);
			vault.Save(Sample("second"));
			var oldCopy = File.ReadAllBytes(Main);
			File.WriteAllBytes(Main + ".tmp", oldCopy);
			File.WriteAllBytes(Main + ".new.tmp", oldCopy);
			File.WriteAllBytes(Main + ".prev.tmp", oldCopy);
			File.WriteAllBytes(Path.Combine(folder, "Backups", "members-2026-11-17.ebmc.tmp"), oldCopy);
			File.WriteAllText(Path.Combine(folder, "unrelated.tmp"), "not ours");

			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.Opened));
			Assert.That(TempFiles().Select(Path.GetFileName), Is.EqualTo(new[] { "unrelated.tmp" }));
		}

		[Test]
		public void RewrapDeletesStrayTemporaryFilesSoTheOldPinReadsNothing()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));
			File.WriteAllBytes(Main + ".tmp", File.ReadAllBytes(Main)); // left by a crash after this vault opened

			Assert.That(vault.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(TempFiles(), Is.Empty);
			AssertNoFileOpensWith("after rewrap", Pin);
		}

		private static bool RunsWithin(Action action, int seconds = 10)
		{
			var task = System.Threading.Tasks.Task.Run(action);
			return task.Wait(TimeSpan.FromSeconds(seconds));
		}

		private static void MakeFifo(string path)
		{
			using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", "\"" + path + "\"") { UseShellExecute = false });
			process.WaitForExit();
			Assert.That(process.ExitCode, Is.EqualTo(0), "mkfifo");
		}

		[Test]
		public void PipesLinksAndHugeFilesAreNeverRead()
		{
			if (OperatingSystem.IsWindows())
			{
				Assert.Ignore("Unix pipes");
			}

			var vault = SavedVault(Sample("first"));
			now = now.AddDays(1);
			vault.Save(Sample("second"));
			var backups = Path.Combine(folder, "Backups");
			var fifo = Path.Combine(backups, "members-2026-01-01.ebmc");
			MakeFifo(fifo);
			var link = Path.Combine(backups, "members-2026-01-02.ebmc");
			var outside = Path.Combine(root, "outside.ebmc");
			File.Copy(Main, outside);
			File.CreateSymbolicLink(link, outside);
			var huge = Path.Combine(backups, "members-2026-01-03.ebmc");
			using (var stream = new FileStream(huge, FileMode.CreateNew))
			{
				stream.SetLength(MembersVault.MaxFileBytes + 1); // sparse: nothing is written
			}

			SaveResult result = null;
			Assert.That(RunsWithin(() => result = vault.Rewrap(OtherPin)), Is.True, "a pipe would block a read");
			Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(result.Skipped, Is.EquivalentTo(new[] { fifo, link, huge }));
			Assert.That(OpensWith(outside, Pin), Is.True, "nothing was written through the link");

			// a pipe in place of the main file: not opened; the previous copy is used and the pipe moved aside unread
			File.Delete(Main);
			MakeFifo(Main);
			OpenResult opened = null;
			var reopened = NewVault();
			Assert.That(RunsWithin(() => opened = reopened.Open(OtherPin)), Is.True);
			Assert.That(opened.Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));
			SaveResult saved = null;
			Assert.That(RunsWithin(() => saved = reopened.Save(Sample("third"))), Is.True);
			Assert.That(saved.SetAside, Has.Count.EqualTo(1));
			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("third"));

			// a link or a huge file in place of the main file: unreadable, never replaced
			File.Delete(Main);
			File.CreateSymbolicLink(Main, outside);
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.Unreadable));
			File.Delete(Main);
			using (var stream = new FileStream(Main, FileMode.CreateNew))
			{
				stream.SetLength(MembersVault.MaxFileBytes + 1);
			}

			Assert.That(NewVault().Open(OtherPin).Status, Is.EqualTo(OpenStatus.Unreadable));
		}

		[Test]
		public void ASaveFailureAtAnyStepNeverDamagesTheFile()
		{
			var vault = SavedVault(Sample("day one"));
			now = now.AddDays(1);
			vault.Save(Sample("day one later")); // a previous copy and one backup exist now
			now = now.AddDays(1);                 // the next save is due a backup

			var pristine = Path.Combine(root, "pristine");
			CopyDirectory(folder, pristine);

			var steps = new List<string>();
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step => steps.Add(step);
			counting.Save(Sample("new"));
			Assert.That(steps, Is.EqualTo(new[] { "backup-write", "backup-rename", "prev-write", "prev-rename", "main-write", "main-rename" }));

			for (int failAt = 0; failAt < steps.Count; failAt++)
			{
				Directory.Delete(folder, recursive: true);
				CopyDirectory(pristine, folder);

				var subject = NewVault();
				subject.Open(Pin);
				int calls = 0;
				subject.FaultPoint = step =>
				{
					if (calls++ == failAt)
					{
						throw new IOException("injected");
					}
				};

				string step = steps[failAt];
				if (step.StartsWith("main", StringComparison.Ordinal))
				{
					var e = Assert.Throws<MembersFileException>(() => subject.Save(Sample("new")), step);
					Assert.That(e.Problem, Is.EqualTo(MembersFileProblem.SaveFailed));
					Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("day one later"), step);
				}
				else
				{
					var result = subject.Save(Sample("new"));
					Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved), step);
					Assert.That(step.StartsWith("backup", StringComparison.Ordinal) ? result.BackupSkipped : result.PreviousSkipped, Is.True, step);
					Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("new"), step);
				}

				Assert.That(TempFiles(), Is.Empty, step);
				subject.FaultPoint = null;
				Assert.That(subject.Save(Sample("retry")).Status, Is.EqualTo(SaveStatus.Saved), step);
				Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("retry"), step);
			}
		}

		[Test]
		public void ABackupsPathThatIsAFileOnlySkipsTheBackup()
		{
			var vault = SavedVault();
			File.WriteAllText(Path.Combine(folder, "Backups"), "not a folder");

			var result = vault.Save(Sample("again"));
			Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(result.BackupSkipped, Is.True);
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("again"));
			Assert.That(vault.ListBackups(), Is.Empty);
		}

		[Test]
		public void APreviousCopyThatIsAFolderOnlySkipsThePreviousCopy()
		{
			var vault = SavedVault();
			Directory.CreateDirectory(Previous);

			var result = vault.Save(Sample("again"));
			Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(result.PreviousSkipped, Is.True);
			Assert.That(Directory.Exists(Previous), Is.True);
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("again"));

			File.WriteAllText(Main, "damaged");
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.Unreadable));
		}

		[Test]
		public void AMainFileThatIsAFolderIsUnreadableAndNeverReplaced()
		{
			Directory.CreateDirectory(Main);
			var vault = NewVault();
			Assert.That(vault.Open(Pin).Status, Is.EqualTo(OpenStatus.Unreadable));
			Assert.That(vault.IsOpen, Is.False);
			Assert.That(Directory.Exists(Main), Is.True);
		}

		[Test]
		public void OneBackupADayAndOnlyTheNewestFourteenAreKept()
		{
			var vault = SavedVault();
			Assert.That(vault.ListBackups(), Is.Empty, "the first save has nothing to back up");

			vault.Save(Sample("again"));
			vault.Save(Sample("and again"));
			Assert.That(vault.ListBackups().Select(Path.GetFileName), Is.EqualTo(new[] { "members-2026-11-16.ebmc" }));

			for (int day = 1; day <= 20; day++)
			{
				now = now.AddDays(1);
				vault.Save(Sample("day " + day));
				vault.Save(Sample("day " + day + " later"));
			}

			var backups = vault.ListBackups().Select(Path.GetFileName).ToList();
			Assert.That(backups, Has.Count.EqualTo(MembersVault.BackupsKept));
			Assert.That(backups.First(), Is.EqualTo("members-2026-12-06.ebmc"));
			Assert.That(backups.Last(), Is.EqualTo("members-2026-11-23.ebmc"));
			Assert.That(backups, Is.Ordered.Descending);

			// a backup holds the file as it was before the first save of its day
			var restored = NewVault().Restore(vault.ListBackups()[0], Pin, Pin);
			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));
			Assert.That(Note(restored), Is.EqualTo("day 19 later"));
		}

		[Test]
		public void BackupNamesUseAsciiDigitsOnly()
		{
			var vault = SavedVault();
			var backups = Path.Combine(folder, "Backups");
			Directory.CreateDirectory(backups);
			File.Copy(Main, Path.Combine(backups, "members-٢٠٢٦-١١-١٦.ebmc"));
			Assert.That(vault.ListBackups(), Is.Empty);
		}

		[Test]
		public void RestoringABackupSetsTheCurrentFilesAsideAndUsesTheCurrentPin()
		{
			var vault = SavedVault(Sample("current"));
			vault.Save(Sample("current, later"));
			var mainBefore = File.ReadAllBytes(Main);

			// a backup saved long ago with another PIN
			var backup = Path.Combine(folder, "Backups", "members-2026-01-01.ebmc");
			Directory.CreateDirectory(Path.GetDirectoryName(backup));
			File.WriteAllBytes(backup, ForeignFile(OtherPin, "from the backup"));

			var restorer = NewVault();
			Assert.That(restorer.Restore(backup, Pin, Pin).Status, Is.EqualTo(OpenStatus.WrongPin), "the backup's own PIN is needed");
			Assert.That(File.ReadAllBytes(Main), Is.EqualTo(mainBefore));

			var restored = restorer.Restore(backup, OtherPin, Pin);
			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));
			Assert.That(Note(restored), Is.EqualTo("from the backup"));
			Assert.That(restorer.IsOpen, Is.True);
			Assert.That(restored.SetAside, Has.Count.EqualTo(2));
			Assert.That(File.Exists(Previous), Is.False);
			Assert.That(TempFiles(), Is.Empty);

			// the main file is under the current PIN only, and the set-aside files keep the newer data
			Assert.That(OpensWith(Main, Pin), Is.True);
			Assert.That(OpensWith(Main, OtherPin), Is.False);
			var setAsideNotes = restored.SetAside.Select(f =>
			{
				Assert.That(OpensWith(f, Pin), Is.True, f);
				var vaultAt = Path.Combine(root, "peek-" + Path.GetFileName(f));
				Directory.CreateDirectory(vaultAt);
				File.Copy(f, Path.Combine(vaultAt, MembersVault.FileName));
				return Note(NewVault(vaultAt).Open(Pin));
			});
			Assert.That(setAsideNotes, Is.EquivalentTo(new[] { "current, later", "current" }));

			// saving goes on normally, and a later PIN change covers the set-aside files too
			Assert.That(restorer.Save(restored.Data).Status, Is.EqualTo(SaveStatus.Saved));
			var rewrap = restorer.Rewrap(ThirdPin);
			Assert.That(rewrap.Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(rewrap.LeftUnderOtherPins, Is.EqualTo(new[] { backup }));
			foreach (var file in restored.SetAside)
			{
				Assert.That(OpensWith(file, Pin), Is.False, file);
				Assert.That(OpensWith(file, ThirdPin), Is.True, file);
			}
		}

		[Test]
		public void ARestoreFailureLeavesTheMainFileAsItWas()
		{
			var vault = SavedVault(Sample("first"));
			now = now.AddDays(1);
			vault.Save(Sample("current"));
			var backup = vault.ListBackups().Single();
			var before = Snapshot();

			foreach (var failOn in new[] { "restore-set-aside-write", "restore-set-aside-rename", "restore-main-write", "restore-main-rename" })
			{
				var restorer = NewVault();
				restorer.FaultPoint = step =>
				{
					if (step == failOn)
					{
						throw new IOException("injected");
					}
				};

				Assert.Throws<MembersFileException>(() => restorer.Restore(backup, Pin, Pin), failOn);
				Assert.That(restorer.IsOpen, Is.False, failOn);
				Assert.That(Snapshot(), Is.EqualTo(before), failOn);
			}
		}

		[Test]
		public void OnlyListedBackupsCanBeRestored()
		{
			SavedVault();
			var outside = Path.Combine(folder, "elsewhere.ebmc");
			File.Copy(Main, outside);

			Assert.That(NewVault().Restore(outside, Pin, Pin).Status, Is.EqualTo(OpenStatus.Unreadable));
			Assert.That(NewVault().Restore(null, Pin, Pin).Status, Is.EqualTo(OpenStatus.Unreadable));
		}

		[Test]
		public void AnOutsideChangeBetweenOpenAndSaveIsRefused()
		{
			SavedVault();
			var first = NewVault();
			var second = NewVault();
			first.Open(Pin);
			second.Open(Pin);

			Assert.That(second.Save(Sample("from the second window")).Status, Is.EqualTo(SaveStatus.Saved));
			var before = Snapshot();

			Assert.That(first.Save(Sample("from the first window")).Status, Is.EqualTo(SaveStatus.ChangedElsewhere));
			Assert.That(first.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.ChangedElsewhere));
			Assert.That(Snapshot(), Is.EqualTo(before));
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("from the second window"));
		}

		[Test]
		public void AFileCreatedElsewhereAfterOpeningEmptyIsNotOverwritten()
		{
			var first = NewVault();
			Assert.That(first.Open(Pin).Status, Is.EqualTo(OpenStatus.NewEmpty));
			SavedVault(Sample("other window"));

			Assert.That(first.Save(Sample("first window")).Status, Is.EqualTo(SaveStatus.ChangedElsewhere));
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("other window"));
		}

		[Test]
		public void AFailedWriteThrowsAFixedMessageAndKeepsTheOldFile()
		{
			if (OperatingSystem.IsWindows())
			{
				Assert.Ignore("Unix file modes");
			}

			var vault = SavedVault(Sample("kept"));
			var before = Snapshot();
			File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try
			{
				var e = Assert.Throws<MembersFileException>(() => vault.Save(Sample("Robin Example")));
				Assert.That(e.Problem, Is.EqualTo(MembersFileProblem.SaveFailed));
				Assert.That(e.InnerException, Is.Null);
				Assert.That(e.ToString(), Does.Not.Contain("Robin"));
			}
			finally
			{
				File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}

			Assert.That(Snapshot(), Is.EqualTo(before));
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("kept"));
		}

		[Test]
		public void CloseForgetsTheKey()
		{
			var vault = SavedVault();
			vault.Close();
			Assert.That(vault.IsOpen, Is.False);
			Assert.That(vault.Save(Sample()).Status, Is.EqualTo(SaveStatus.NotOpen));
		}

		[Test]
		public void KeyOpensWithChecksTheOpenVaultsKeyAndWritesNothing()
		{
			var vault = SavedVault();
			var before = Snapshot();
			Assert.That(vault.KeyOpensWith(Pin), Is.True);
			Assert.That(vault.KeyOpensWith(OtherPin), Is.False);
			Assert.That(vault.KeyOpensWith(Pin + " "), Is.False, "never trimmed");
			Assert.That(vault.KeyOpensWith(""), Is.False);
			Assert.That(vault.KeyOpensWith(null), Is.False);
			Assert.That(Snapshot(), Is.EqualTo(before));
			Assert.That(vault.IsOpen, Is.True);

			// after a key change, only the new one
			Assert.That(vault.Rewrap(OtherPin).Ok, Is.True);
			Assert.That(vault.KeyOpensWith(OtherPin), Is.True);
			Assert.That(vault.KeyOpensWith(Pin), Is.False);

			// closed: nothing to check against
			vault.Close();
			Assert.That(vault.KeyOpensWith(OtherPin), Is.False);
		}

		// --- the file format ---

		/// <summary>
		/// Written by hand from a file made once by this version, with PIN 135792. It must keep opening in every later
		/// version: the format does not depend on how a JSON writer spaces or escapes.
		/// </summary>
		private const string GoldenFile =
			"{\"format\":\"ebmc-members\",\"v\":1,\"kdf\":{\"alg\":\"pbkdf2-sha256\",\"iter\":100000,"
			+ "\"salt\":\"Ev+vHNTosnnJscyFGmFEJQ==\"},\"wrap\":{\"nonce\":\"ATaMNYaq8qmQ+/dW\","
			+ "\"ct\":\"zeSzZmNTEcnWgtD1fhWPpVzyDGYXbc5NjlaCeFB5dhI=\",\"tag\":\"IosaW8sFLfI1ax9pVy0LDg==\"},\"data\":{"
			+ "\"nonce\":\"SnKfn0EUUHyDRhWN\","
			+ "\"ct\":\"+IedvW3dZPbf8sxrF91LYa9ZmUBX0pXQvp3JKWyvfDjJ9Jwi1J4GBZuoGulbZMKBD5/rU0CGegqqqy1Pultv"
			+ "65Aq/a1fsL2Zvrt1Rw3M/zrCoAI6cS4o42LUnVTry5Qi/E4RwlHAU/L8EsewoC73db48CLMkuKAjHzD45emHyAoibV"
			+ "6aOfirRx8H7l5qVStkwBRkeMFfafDK+R7jlo0MXwnHq2BejRP7TIqATV61HfsIcu70CEIi6VP5WYlxK50fuxjprnNV"
			+ "T4vuQaXZGf8hKUbQw0LOjSnyJy3403B0RnEtG/xDESp7LaQ12QEnzetBZWS5AFEgViFbr1JL1rALOrAZN8bm7xCzRw"
			+ "fwGp9POwLIHbMwA8qbADpliq4ZdDw5\","
			+ "\"tag\":\"j40+tLHoTQmDv94VqQR56w==\"}}";

		[Test]
		public void AGoldenFileKeepsOpening()
		{
			File.WriteAllText(Main, GoldenFile);
			var result = NewVault().Open(Pin);
			Assert.That(result.Status, Is.EqualTo(OpenStatus.Opened));
			Assert.That(result.Data.Members.Single().Name, Is.EqualTo("Golden Example"));
			Assert.That(result.IdsMigrated, Is.True, "the golden file's id is from before ids were checked");
			Assert.That(MembersRules.IsWellFormedId(result.Data.Members.Single().Id), Is.True);
			Assert.That(NewVault().Open(OtherPin).Status, Is.EqualTo(OpenStatus.WrongPin));
		}

		[Test]
		public void TheEnvelopeIsReadFieldByFieldNotByteForByte()
		{
			// the same values, re-ordered and spaced differently, still open
			using var document = System.Text.Json.JsonDocument.Parse(GoldenFile);
			var r = document.RootElement;
			string S(string a, string b) => r.GetProperty(a).GetProperty(b).GetString();
			var reordered =
				"{\n  \"data\" : { \"tag\": \"" + S("data", "tag") + "\", \"ct\": \"" + S("data", "ct") + "\", \"nonce\": \"" + S("data", "nonce") + "\" },\n" +
				"  \"wrap\" : { \"ct\": \"" + S("wrap", "ct") + "\", \"tag\": \"" + S("wrap", "tag") + "\", \"nonce\": \"" + S("wrap", "nonce") + "\" },\n" +
				"  \"kdf\" : { \"salt\": \"" + S("kdf", "salt") + "\", \"iter\": 100000, \"alg\": \"pbkdf2-sha256\" },\n" +
				"  \"v\" : 1, \"format\" : \"ebmc-members\"\n}\n";
			File.WriteAllText(Main, reordered);
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.Opened));

			// but not with an extra or a repeated member, an escaped string, or another iteration count
			foreach (var bad in new[]
			{
				reordered.Replace("\"v\" : 1,", "\"v\" : 1, \"extra\": 0,"),
				reordered.Replace("\"v\" : 1,", "\"v\" : 1, \"v\" : 1,"),
				reordered.Replace("\"ebmc-members\"", "\"ebmc\\u002Dmembers\""),
				reordered.Replace("100000", "1e5"),
				reordered.Replace("100000", "99999"),
			})
			{
				Assert.That(MembersCrypto.TryParseEnvelope(Encoding.UTF8.GetBytes(bad), out _, out _), Is.False, bad);
			}
		}

		[Test]
		public void FlippingAnyByteOfTheFileFailsWithTheRightStatusForItsRegion()
		{
			SavedVault();
			var original = File.ReadAllBytes(Main);
			OpenKey(Main, Pin, out var key, out var wrapped);
			var kek = MembersCrypto.DeriveKek(Pin, wrapped.Salt, wrapped.Iterations);
			var text = Encoding.ASCII.GetString(original);
			int wrapStart = text.IndexOf("\"wrap\"", StringComparison.Ordinal);
			int dataStart = text.IndexOf("\"data\"", StringComparison.Ordinal);

			var regions = new HashSet<string>();
			int kdfRuns = 0;
			foreach (byte mask in new byte[] { 0x01, 0x20, 0x80 })
			{
				for (int i = 0; i < original.Length; i++)
				{
					var tampered = (byte[])original.Clone();
					tampered[i] ^= mask;
					string region = i < wrapStart ? "header" : i < dataStart ? "wrap" : "data";
					regions.Add(region);

					if (!MembersCrypto.TryParseEnvelope(tampered, out var w, out _))
					{
						continue; // the vault reports Unreadable without running the KDF
					}

					bool sameSalt = w.Salt.AsSpan().SequenceEqual(wrapped.Salt);
					bool sameWrap = sameSalt
						&& w.Nonce.AsSpan().SequenceEqual(wrapped.Nonce)
						&& w.Ciphertext.AsSpan().SequenceEqual(wrapped.Ciphertext)
						&& w.Tag.AsSpan().SequenceEqual(wrapped.Tag);

					if (sameWrap)
					{
						// only the data part changed: the key unwraps, the data does not open -> Unreadable
						Assert.That(region, Is.EqualTo("data"), $"byte {i}");
						Assert.That(MembersCrypto.OpenFileWithKey(tampered, key), Is.Null, $"byte {i}");
					}
					else if (sameSalt)
					{
						// the wrapped key changed: it does not unwrap -> WrongPin
						Assert.That(region, Is.EqualTo("wrap"), $"byte {i}");
						Assert.That(MembersCrypto.UnwrapKeyWithKek(w, kek), Is.Null, $"byte {i}");
					}
					else
					{
						// the salt changed: another key-encryption key -> WrongPin
						Assert.That(region, Is.EqualTo("header"), $"byte {i}");
						kdfRuns++;
						Assert.That(MembersCrypto.UnwrapKey(w, Pin), Is.Null, $"byte {i}");
					}
				}
			}

			Assert.That(regions, Is.EquivalentTo(new[] { "header", "wrap", "data" }));
			Assert.That(kdfRuns, Is.GreaterThan(0).And.LessThan(100));
		}

		[Test]
		public void TheVaultReportsTamperingByRegionAndUsesThePreviousCopy()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));
			var original = File.ReadAllBytes(Main);
			var previous = File.ReadAllBytes(Previous);
			var text = Encoding.ASCII.GetString(original);

			var cases = new[]
			{
				(at: text.IndexOf("\"format\"", StringComparison.Ordinal) + 3, expected: OpenStatus.Unreadable),
				(at: text.IndexOf("\"salt\":\"", StringComparison.Ordinal) + 9, expected: OpenStatus.WrongPin),
				(at: text.IndexOf("\"wrap\"", StringComparison.Ordinal) + 20, expected: OpenStatus.WrongPin),
				(at: text.IndexOf("\"data\"", StringComparison.Ordinal) + 30, expected: OpenStatus.Unreadable),
				(at: original.Length - 5, expected: OpenStatus.Unreadable),
			};

			foreach (var (at, expected) in cases)
			{
				var tampered = (byte[])original.Clone();
				tampered[at] = (byte)(tampered[at] == (byte)'A' ? 'B' : 'A');

				// alone: the status of its region, nothing written
				File.Delete(Previous);
				File.WriteAllBytes(Main, tampered);
				var alone = NewVault();
				var result = alone.Open(Pin);
				Assert.That(result.Status, Is.EqualTo(expected), $"byte {at}");
				Assert.That(alone.IsOpen, Is.False);
				Assert.That(File.ReadAllBytes(Main), Is.EqualTo(tampered));

				// with the previous copy: it opens, with the previous copy's data, never the tampered file's
				File.WriteAllBytes(Previous, previous);
				var fallback = NewVault().Open(Pin);
				Assert.That(fallback.Status, Is.EqualTo(OpenStatus.OpenedFromPrevious), $"byte {at}");
				Assert.That(Note(fallback), Is.EqualTo("first"));
			}
		}

		[Test]
		public void ChangingAnyByteOfTheEncryptedPartsIsCaughtByTheCipher()
		{
			// below the envelope parser: the authenticated encryption alone catches each change
			var key = MembersCrypto.NewDataKey();
			var aad = Encoding.UTF8.GetBytes("header");
			var sealedBytes = MembersCrypto.Seal(Encoding.UTF8.GetBytes("Robin Example"), key, aad);
			Assert.That(Encoding.UTF8.GetString(MembersCrypto.Open(sealedBytes, key, aad)), Is.EqualTo("Robin Example"));

			for (int i = 0; i < sealedBytes.Length; i++)
			{
				var tampered = (byte[])sealedBytes.Clone();
				tampered[i] ^= 0x01;
				Assert.That(MembersCrypto.Open(tampered, key, aad), Is.Null, $"byte {i}");
			}

			Assert.That(MembersCrypto.Open(sealedBytes, key, Encoding.UTF8.GetBytes("headex")), Is.Null, "associated data");
			Assert.That(MembersCrypto.Open(sealedBytes, MembersCrypto.NewDataKey(), aad), Is.Null, "other key");

			var wrapped = MembersCrypto.WrapKey(key, Pin);
			Assert.That(MembersCrypto.UnwrapKey(wrapped, Pin), Is.EqualTo(key));
			Assert.That(MembersCrypto.UnwrapKey(wrapped, OtherPin), Is.Null);
			wrapped.Salt[0] ^= 1;
			Assert.That(MembersCrypto.UnwrapKey(wrapped, Pin), Is.Null, "salt is authenticated");
		}

		[Test]
		public void TheAuthenticatedHeaderHasAFixedLayout()
		{
			var wrapped = new WrappedKey { Iterations = 100000, Salt = Enumerable.Range(1, 16).Select(b => (byte)b).ToArray() };
			var expected = Encoding.ASCII.GetBytes("EBMC-MEMBERS-AAD")
				.Concat(new byte[] { 1, 0, 0, 0, 1, 1, 0, 1, 0x86, 0xA0, 16 })
				.Concat(wrapped.Salt)
				.ToArray();
			Assert.That(MembersCrypto.HeaderAad(wrapped), Is.EqualTo(expected));
		}

		// --- two copies of the app ---

		private FileStream HoldLock() => new FileStream(Path.Combine(folder, MembersVault.FileName + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

		[Test]
		public void EveryWriteIsBusyWhileAnotherCopyHoldsTheLock()
		{
			var vault = SavedVault(Sample("first"));
			now = now.AddDays(1);
			vault.Save(Sample("second"));
			var backup = vault.ListBackups().Single();
			File.WriteAllBytes(Main + ".tmp", File.ReadAllBytes(Main)); // a temporary file another copy may be writing
			var before = Snapshot();

			using (HoldLock())
			{
				Assert.That(vault.Save(Sample("blocked")).Status, Is.EqualTo(SaveStatus.Busy));
				Assert.That(vault.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Busy));
				Assert.That(vault.Restore(backup, Pin).Status, Is.EqualTo(OpenStatus.Busy));
				Assert.That(NewVault().Restore(backup, Pin, Pin).Status, Is.EqualTo(OpenStatus.Busy));

				// reading still works, but nothing is cleaned while the lock is held
				Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("second"));
				Assert.That(Snapshot(), Is.EqualTo(before));
			}

			// once free, the same vault goes on; the temporary file is cleaned on the next open
			Assert.That(vault.Save(Sample("third")).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.Opened));
			Assert.That(TempFiles(), Is.Empty);
		}

		[Test]
		public void ALockFileLeftBehindDoesNotBlock()
		{
			File.WriteAllText(Path.Combine(folder, MembersVault.FileName + ".lock"), "left by a crashed copy");
			var vault = NewVault();
			vault.Open(Pin);
			Assert.That(vault.Save(Sample()).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(vault.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
		}

		[Test]
		public void AnOpenWithAnUnfinishedPinChangeIsBusyWhileTheLockIsHeld()
		{
			var pristine = PristineThreeDays();
			var crash = Path.Combine(root, "after-main-switch");
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step =>
			{
				if (step == "rewrap-switch" && !Directory.Exists(crash))
				{
					CopyDirectory(folder, crash);
				}
			};
			counting.Rewrap(OtherPin);
			ResetFolder(crash);
			var before = Snapshot();

			using (HoldLock())
			{
				Assert.That(NewVault().Open(OtherPin).Status, Is.EqualTo(OpenStatus.Busy));
				Assert.That(Snapshot(), Is.EqualTo(before));
			}

			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("day 2"));
			Assert.That(File.Exists(Main + ".rewrap"), Is.False);
		}

		[Test]
		public void TwoCopiesInterleavedAtEachStepOfASave()
		{
			var vault = SavedVault(Sample("day 0"));
			now = now.AddDays(1);
			vault.Save(Sample("day 1"));
			now = now.AddDays(1);
			var pristine = Path.Combine(root, "pristine");
			CopyDirectory(folder, pristine);

			var steps = new List<string>();
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step => steps.Add(step);
			counting.Save(Sample("from A"));
			Assert.That(steps, Has.Count.EqualTo(6));

			for (int at = 0; at < steps.Count; at++)
			{
				ResetFolder(pristine);
				var a = NewVault();
				a.Open(Pin);
				var b = NewVault();
				b.Open(Pin);
				int calls = 0;
				string label = steps[at];
				a.FaultPoint = step =>
				{
					if (calls++ != at)
					{
						return;
					}

					// another copy opens (and would clean temporary files), saves and changes the PIN mid-save
					var c = NewVault().Open(Pin);
					Assert.That(c.Status, Is.EqualTo(OpenStatus.Opened), label);
					Assert.That(Note(c), Is.EqualTo("day 1"), label);
					Assert.That(b.Save(Sample("from B")).Status, Is.EqualTo(SaveStatus.Busy), label);
					Assert.That(b.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Busy), label);
				};

				Assert.That(a.Save(Sample("from A")).Status, Is.EqualTo(SaveStatus.Saved), label);
				Assert.That(TempFiles(), Is.Empty, label);
				Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("from A"), label);
				Assert.That(b.Save(Sample("from B")).Status, Is.EqualTo(SaveStatus.ChangedElsewhere), label);
			}
		}

		[Test]
		public void TwoCopiesInterleavedAtEachStepOfAPinChange()
		{
			var pristine = PristineThreeDays();
			var steps = new List<string>();
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step => steps.Add(step);
			counting.Rewrap(OtherPin);

			for (int at = 0; at < steps.Count; at++)
			{
				ResetFolder(pristine);
				var a = NewVault();
				a.Open(Pin);
				var b = NewVault();
				b.Open(Pin);
				int calls = 0;
				string label = steps[at];
				a.FaultPoint = step =>
				{
					if (calls++ != at)
					{
						return;
					}

					// another copy opens with the old PIN: it may see the old data, or be told to wait or that the PIN
					// changed, but never an older copy; it deletes none of the staged files
					var c = NewVault().Open(Pin);
					Assert.That(c.Status, Is.AnyOf(OpenStatus.Opened, OpenStatus.Busy, OpenStatus.WrongPin), label);
					if (c.IsOpen)
					{
						Assert.That(Note(c), Is.EqualTo("day 2"), label);
					}

					Assert.That(b.Save(Sample("from B")).Status, Is.EqualTo(SaveStatus.Busy), label);
					Assert.That(b.Rewrap(ThirdPin).Status, Is.EqualTo(SaveStatus.Busy), label);
				};

				var result = a.Rewrap(OtherPin);
				Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved), label);
				Assert.That(result.PinChangePending, Is.False, label);
				Assert.That(File.Exists(a.JournalPath), Is.False, label);
				Assert.That(TempFiles(), Is.Empty, label);
				Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("day 2"), label);
				AssertNoFileOpensWith(label, Pin);
			}
		}

		[Test]
		public void APinChangeIsReportedOnlyWhenTheMainFileReallySwitched()
		{
			PristineThreeDays();
			var vault = NewVault();
			vault.Open(Pin);

			// something removes the staged main file just before it is renamed
			vault.FaultPoint = step =>
			{
				if (step == "rewrap-switch-main")
				{
					File.Delete(Main + ".new.tmp");
				}
			};

			var e = Assert.Throws<MembersFileException>(() => vault.Rewrap(OtherPin));
			Assert.That(e.Problem, Is.EqualTo(MembersFileProblem.SaveFailed));
			Assert.That(File.Exists(vault.JournalPath), Is.False);
			Assert.That(TempFiles(), Is.Empty);
			Assert.That(Note(NewVault().Open(Pin)), Is.EqualTo("day 2"), "the old PIN is still in force");
			AssertNoFileOpensWith("after the failed change", OtherPin);

			vault.FaultPoint = null;
			Assert.That(vault.Save(Sample("still saving")).Status, Is.EqualTo(SaveStatus.Saved));
		}

		[Test]
		public void AnUnreadableMainFileDuringAPinChangeCanStillBeRestored()
		{
			var pristine = PristineThreeDays();
			var crash = Path.Combine(root, "after-main-switch");
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step =>
			{
				if (step == "rewrap-switch" && !Directory.Exists(crash))
				{
					CopyDirectory(folder, crash);
				}
			};
			counting.Rewrap(OtherPin);
			ResetFolder(crash);
			File.WriteAllText(Main, "damaged");

			var open = NewVault().Open(OtherPin);
			Assert.That(open.Status, Is.EqualTo(OpenStatus.Unreadable));
			Assert.That(open.PinChangeInterrupted, Is.True);

			// the previous copy is still under the old PIN here, so the old PIN is the one the files can check
			var backup = NewVault().ListBackups().First();
			var restored = NewVault().Restore(backup, Pin, Pin, p => p == Pin);
			Assert.That(restored.Status, Is.EqualTo(OpenStatus.Restored));
			Assert.That(File.Exists(Main + ".rewrap"), Is.False);
			Assert.That(Directory.GetFiles(folder, "*.new.tmp", SearchOption.AllDirectories), Is.Empty);
			Assert.That(restored.SetAside.Any(f => f.Contains(".rewrap.replaced-", StringComparison.Ordinal)), Is.True);
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.Opened));
		}

		[Test]
		public void APendingPinChangeDoesNotBlockSaving()
		{
			PristineThreeDays();
			var vault = NewVault();
			vault.Open(Pin);
			bool failRenames = true;
			vault.FaultPoint = step =>
			{
				if (failRenames && step == "rewrap-switch")
				{
					throw new IOException("injected");
				}
			};

			var rewrap = vault.Rewrap(OtherPin);
			Assert.That(rewrap.Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(rewrap.PinChangePending, Is.True);

			for (int i = 0; i < 3; i++)
			{
				var saved = vault.Save(Sample("while pending " + i));
				Assert.That(saved.Status, Is.EqualTo(SaveStatus.Saved));
				Assert.That(saved.PinChangePending, Is.True);

				// read without opening a vault (an Open with the new PIN would finish the change itself)
				Assert.That(MembersCrypto.OpenFile(File.ReadAllBytes(Main), OtherPin, out var plain, out _, out _), Is.EqualTo(EnvelopeOpenStatus.Ok));
				Assert.That(MembersExport.ParseJson(plain).Members[0].Log[0].Text, Is.EqualTo("while pending " + i));
			}

			failRenames = false;
			var finished = vault.Save(Sample("finished"));
			Assert.That(finished.PinChangePending, Is.False);
			Assert.That(File.Exists(vault.JournalPath), Is.False);
			Assert.That(TempFiles(), Is.Empty);
			AssertNoFileOpensWith("after the pending change finished", Pin);

			// the previous copy was not wound back to the older staged version
			File.WriteAllText(Main, "damaged");
			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("while pending 2"));
		}

		[Test]
		public void TheJournalOnlyEverMovesManagedFiles()
		{
			var pristine = PristineThreeDays();
			var crash = Path.Combine(root, "after-main-switch");
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step =>
			{
				if (step == "rewrap-switch" && !Directory.Exists(crash))
				{
					CopyDirectory(folder, crash);
				}
			};
			counting.Rewrap(OtherPin);
			ResetFolder(crash);

			// hostile entries: outside the folder, a file that is not managed, a repeated line, and padding
			var outside = Path.Combine(root, "outside.ebmc");
			File.WriteAllText(outside, "keep me");
			File.Copy(Main, outside + ".new.tmp");
			var journal = File.ReadAllText(Main + ".rewrap");
			var extra = "../outside.ebmc\t../outside.ebmc.new.tmp\nnotes.txt\tnotes.txt.new.tmp\n" + journal.Split('\n')[1] + "\n";
			File.WriteAllText(Main + ".rewrap", journal + extra);

			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("day 2"));
			Assert.That(File.ReadAllText(outside), Is.EqualTo("keep me"));
			Assert.That(File.Exists(outside + ".new.tmp"), Is.True);
			AssertNoFileOpensWith("after recovery", Pin);

			// a journal far larger than any real one is ignored; the naming rule still finishes the change
			ResetFolder(crash);
			File.WriteAllText(Main + ".rewrap", "ebmc-members-rewrap 1\n" + new string('x', 300 * 1024));
			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("day 2"));
			AssertNoFileOpensWith("after recovery with a padded journal", Pin);
		}

		[Test]
		public void BackupsDatedInTheFutureAreNotListedPrunedOrCountedButAreReEncrypted()
		{
			var vault = SavedVault(Sample("first"));
			var backups = Path.Combine(folder, "Backups");
			Directory.CreateDirectory(backups);
			var future = Path.Combine(backups, "members-2099-01-01.ebmc");
			File.Copy(Main, future);
			var badDate = Path.Combine(backups, "members-2026-13-45.ebmc");
			File.Copy(Main, badDate);

			for (int day = 1; day <= 20; day++)
			{
				now = now.AddDays(1);
				vault.Save(Sample("day " + day));
			}

			Assert.That(vault.ListBackups(), Has.Count.EqualTo(MembersVault.BackupsKept), "the future one takes no slot");
			Assert.That(vault.ListBackups(), Does.Not.Contain(future).And.Not.Contain(badDate));
			Assert.That(File.Exists(future), Is.True, "never pruned");

			Assert.That(vault.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved));
			Assert.That(OpensWith(future, OtherPin), Is.True);
			AssertNoFileOpensWith("after the PIN change", Pin);
		}

		[Test]
		public void IdsFromAnEarlyVersionAreReplacedConsistentlyAndStored()
		{
			var key = MembersCrypto.NewDataKey();
			var wrapped = MembersCrypto.WrapKey(key, Pin);
			var json = "{\"SchemaVersion\":1,\"Members\":[{\"Id\":\"robin\",\"Name\":\"Robin Example\"},{\"Id\":\"" + 5.ToString("x32") + "\",\"Name\":\"Sam Testcase\"}],"
				+ "\"Projects\":[{\"Id\":\"arm\",\"Title\":\"Robot arm\",\"MemberIds\":[\"robin\",\"" + 5.ToString("x32") + "\"]}]}";
			File.WriteAllBytes(Main, MembersCrypto.SealFile(Encoding.UTF8.GetBytes(json), key, wrapped));

			var vault = NewVault();
			var opened = vault.Open(Pin);
			Assert.That(opened.IdsMigrated, Is.True);
			var robin = opened.Data.Members.Single(m => m.Name == "Robin Example");
			Assert.That(MembersRules.IsWellFormedId(robin.Id), Is.True);
			Assert.That(opened.Data.Members.Single(m => m.Name == "Sam Testcase").Id, Is.EqualTo(5.ToString("x32")), "well-formed ids are kept");
			var project = opened.Data.Projects.Single();
			Assert.That(MembersRules.IsWellFormedId(project.Id), Is.True);
			Assert.That(project.MemberIds, Is.EqualTo(new[] { robin.Id, 5.ToString("x32") }));

			// stored with the next save; then deleting leaves a deletion record
			vault.Save(opened.Data);
			var again = NewVault().Open(Pin);
			Assert.That(again.IdsMigrated, Is.False);
			Assert.That(again.Data.Members.Select(m => m.Id), Does.Contain(robin.Id));
			var store = new MembersStore(again.Data, () => now, () => Guid.NewGuid().ToString("N"));
			store.DeleteMember(robin.Id);
			Assert.That(store.Snapshot().DeletedMembers.Single().Id, Is.EqualTo(robin.Id));
		}

		[Test]
		public void AnOpenDuringAPinChangeNeverFallsBackAndItsSaveNeverUndoesTheChange()
		{
			var pristine = PristineThreeDays();
			var steps = new List<string>();
			var counting = NewVault();
			counting.Open(Pin);
			counting.FaultPoint = step => steps.Add(step);
			counting.Rewrap(OtherPin);

			foreach (var openPin in new[] { Pin, OtherPin })
			{
				for (int at = 0; at < steps.Count; at++)
				{
					ResetFolder(pristine);
					var a = NewVault();
					a.Open(Pin);
					var b = NewVault();
					OpenResult seen = null;
					int calls = 0;
					string label = openPin + " at " + steps[at];
					a.FaultPoint = step =>
					{
						if (calls++ != at)
						{
							return;
						}

						// another window unlocks in the middle of the PIN change, then saves
						seen = b.Open(openPin);
						Assert.That(seen.Status, Is.Not.EqualTo(OpenStatus.OpenedFromPrevious), label);
						if (seen.IsOpen)
						{
							Assert.That(Note(seen), Is.EqualTo("day 2"), label);
						}

						Assert.That(b.Save(Sample("from the other window")).Status, Is.AnyOf(SaveStatus.Busy, SaveStatus.NotOpen), label);
					};

					Assert.That(a.Rewrap(OtherPin).Status, Is.EqualTo(SaveStatus.Saved), label);

					// after the change: the other window's save is refused, the change stands, nothing older is current
					Assert.That(b.Save(Sample("from the other window")).Status, Is.AnyOf(SaveStatus.ChangedElsewhere, SaveStatus.NotOpen), label);
					Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("day 2"), label);
					Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.WrongPin), label);
					AssertNoFileOpensWith(label, Pin);
				}
			}
		}

		[Test]
		public void TheReportedRaceAnOldPinOpenWhoseJournalCheckGoesStale()
		{
			// window B checks for a journal (none yet); window A then changes the PIN as far as the main file (the
			// other renames fail, so the journal stays and .prev is still under the old PIN); B goes on with the old PIN
			PristineThreeDays();
			var a = NewVault();
			a.Open(Pin);
			a.FaultPoint = step =>
			{
				if (step == "rewrap-switch")
				{
					throw new IOException("injected");
				}
			};

			var b = NewVault();
			bool fired = false;
			b.FaultPoint = step =>
			{
				if (step == "open-journal-checked" && !fired)
				{
					fired = true;
					var change = a.Rewrap(OtherPin);
					Assert.That(change.PinChangePending, Is.True);
				}
			};

			var seen = b.Open(Pin);
			Assert.That(fired, Is.True);
			Assert.That(seen.Status, Is.EqualTo(OpenStatus.WrongPin), "decided again under the lock: the change is seen");
			Assert.That(seen.PinChangeInterrupted, Is.True);
			Assert.That(b.Save(Sample("stale")).Status, Is.EqualTo(SaveStatus.NotOpen));

			a.FaultPoint = null;
			Assert.That(a.Save(Sample("day 2")).PinChangePending, Is.False);
			Assert.That(Note(NewVault().Open(OtherPin)), Is.EqualTo("day 2"));
			AssertNoFileOpensWith("after the race", Pin);
		}

		[Test]
		public void ASaveFromThePreviousCopyIsRefusedWhileAPinChangeIsUnfinished()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));
			File.WriteAllText(Main, "damaged");
			var fromPrevious = NewVault();
			Assert.That(fromPrevious.Open(Pin).Status, Is.EqualTo(OpenStatus.OpenedFromPrevious));

			File.WriteAllText(Main + ".rewrap", "ebmc-members-rewrap 1\n"); // another copy started a PIN change since
			var before = Snapshot();
			Assert.That(fromPrevious.Save(Sample("stale")).Status, Is.EqualTo(SaveStatus.ChangedElsewhere));
			Assert.That(Snapshot(), Is.EqualTo(before));
		}

		[Test]
		public void ConcurrentSavesAndOpensNeverFallBackToThePreviousCopy()
		{
			var writer = SavedVault(Sample("0"));
			writer.Save(Sample("1"));
			var statuses = new System.Collections.Concurrent.ConcurrentBag<OpenStatus>();
			var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
			int saved = 0;

			var saving = System.Threading.Tasks.Task.Run(() =>
			{
				for (int i = 2; i < 150; i++)
				{
					var result = writer.Save(Sample(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
					if (result.Status == SaveStatus.Saved)
					{
						saved++;
					}
					else if (result.Status != SaveStatus.Busy)
					{
						errors.Add("save " + result.Status);
					}
				}
			});

			var opening = System.Threading.Tasks.Task.Run(() =>
			{
				while (!saving.IsCompleted)
				{
					var result = NewVault().Open(Pin);
					statuses.Add(result.Status);
					if (result.IsOpen && !int.TryParse(Note(result), out _))
					{
						errors.Add("odd data");
					}
				}
			});

			Assert.That(System.Threading.Tasks.Task.WaitAll(new[] { saving, opening }, TimeSpan.FromSeconds(60)), Is.True);
			Assert.That(errors, Is.Empty);
			Assert.That(statuses, Is.Not.Empty);
			Assert.That(statuses, Has.None.EqualTo(OpenStatus.OpenedFromPrevious));
			Assert.That(statuses.Distinct(), Is.SubsetOf(new[] { OpenStatus.Opened, OpenStatus.Busy }));
			Assert.That(saved, Is.GreaterThan(0));
		}

		[Test]
		public void ALockFileThatCannotBeOpenedIsAFailureNotBusy()
		{
			var vault = SavedVault(Sample("first"));
			vault.Save(Sample("second"));
			File.Delete(Path.Combine(folder, MembersVault.FileName + ".lock"));
			Directory.CreateDirectory(Path.Combine(folder, MembersVault.FileName + ".lock"));

			var e = Assert.Throws<MembersFileException>(() => vault.Save(Sample("third")));
			Assert.That(e.Problem, Is.EqualTo(MembersFileProblem.SaveFailed));

			// a fallback open needs the lock: it fails as unreadable, not as busy
			File.WriteAllText(Main, "damaged");
			Assert.That(NewVault().Open(Pin).Status, Is.EqualTo(OpenStatus.Unreadable));
		}

		[Test]
		public void MigratedIdsAreTheSameOnEveryOpen()
		{
			var key = MembersCrypto.NewDataKey();
			var wrapped = MembersCrypto.WrapKey(key, Pin);
			var json = "{\"SchemaVersion\":1,\"Members\":[{\"Id\":\"robin\",\"Name\":\"Robin Example\"},{\"Id\":\"sam\",\"Name\":\"Sam Testcase\"}],"
				+ "\"Projects\":[{\"Id\":\"arm\",\"Title\":\"Robot arm\",\"MemberIds\":[\"robin\",\"sam\"]}]}";
			File.WriteAllBytes(Main, MembersCrypto.SealFile(Encoding.UTF8.GetBytes(json), key, wrapped));

			var first = NewVault().Open(Pin).Data;
			var second = NewVault().Open(Pin).Data;
			Assert.That(second.Members.Select(m => m.Id), Is.EqualTo(first.Members.Select(m => m.Id)));
			Assert.That(second.Projects.Single().Id, Is.EqualTo(first.Projects.Single().Id));
			Assert.That(first.Members.Select(m => m.Id), Is.All.Matches<string>(MembersRules.IsWellFormedId));

			// an export taken before the first save merges onto the saved data without duplicating anyone
			var export = MembersExport.ToJson(first);
			var vault = NewVault();
			var opened = vault.Open(Pin);
			vault.Save(opened.Data);
			var saved = NewVault().Open(Pin).Data;
			MembersExport.ImportJson(export, saved, ImportMode.Merge, new DateTime(2026, 11, 16));
			Assert.That(saved.Members, Has.Count.EqualTo(2));
		}

		[Test]
		public void AMigratedIdThatCollidesIsDerivedAgainTheSameWay()
		{
			var used = new HashSet<string>();
			var plain = MembersRules.DerivedId("member", "robin", new HashSet<string>());
			used.Add(plain); // a record already has that id

			var first = MembersRules.DerivedId("member", "robin", new HashSet<string>(used));
			var second = MembersRules.DerivedId("member", "robin", new HashSet<string>(used));
			Assert.That(first, Is.Not.EqualTo(plain));
			Assert.That(second, Is.EqualTo(first));
			Assert.That(MembersRules.IsWellFormedId(first), Is.True);

			var data = new MembersData
			{
				Members =
				{
					new Member { Id = plain, Name = "Sam Testcase" },
					new Member { Id = "robin", Name = "Robin Example" },
				},
			};
			MembersRules.AssignWellFormedIds(data);
			Assert.That(data.Members[0].Id, Is.EqualTo(plain));
			Assert.That(data.Members[1].Id, Is.EqualTo(first));
		}

		private static void OpenKey(string file, string pin, out byte[] key, out WrappedKey wrapped)
		{
			Assert.That(MembersCrypto.OpenFile(File.ReadAllBytes(file), pin, out _, out key, out wrapped), Is.EqualTo(EnvelopeOpenStatus.Ok));
		}

		/// <summary>A schema version this build does not know yet.</summary>
		private static readonly string Newer = (MembersData.CurrentSchemaVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

		private static byte[] NewerFile(byte[] key, string pin, out WrappedKey wrapped)
		{
			wrapped = MembersCrypto.WrapKey(key, pin);
			return MembersCrypto.SealFile(Encoding.UTF8.GetBytes("{\"SchemaVersion\":" + Newer + ",\"Members\":[]}"), key, wrapped);
		}

		/// <summary>Changes one base64 character inside a part of the key, keeping the file well formed.</summary>
		private static byte[] DamagePart(byte[] file, string part)
		{
			var text = Encoding.ASCII.GetString(file);
			string marker = part switch
			{
				"salt" => "\"salt\":\"",
				"wrap-nonce" => "\"wrap\":{\"nonce\":\"",
				"wrap-ct" => "\"ct\":\"",
				_ => "\"wrap\":{",
			};

			int at = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
			if (part == "wrap-tag")
			{
				at = text.IndexOf("\"tag\":\"", at, StringComparison.Ordinal) + 7;
			}

			var bytes = (byte[])file.Clone();
			bytes[at] = (byte)(bytes[at] == (byte)'A' ? 'B' : 'A');
			return bytes;
		}

		private static bool Contains(byte[] haystack, byte[] needle)
		{
			for (int i = 0; i + needle.Length <= haystack.Length; i++)
			{
				if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
				{
					return true;
				}
			}

			return false;
		}
	}
}
