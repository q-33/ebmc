using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MatterHackers.MatterControl.ClubMembers
{
	public enum OpenStatus
	{
		/// <summary>There are no members files yet: an empty list, saved with this PIN on the first save.</summary>
		NewEmpty,

		Opened,

		/// <summary>The main file could not be opened with this PIN (damaged, or its key part damaged); the copy
		/// kept by the previous save opened with the same PIN. Never used while a PIN change is unfinished.</summary>
		OpenedFromPrevious,

		/// <summary>A backup or set-aside file was restored explicitly (<see cref="MembersVault.Restore"/>).</summary>
		Restored,

		/// <summary>Neither the file nor the previous copy opens with this PIN. Nothing was changed.</summary>
		WrongPin,

		/// <summary>Neither the file nor the previous copy can be read. Nothing was changed.</summary>
		Unreadable,

		/// <summary>The file was saved by a newer version of the app. Nothing was changed.</summary>
		NewerVersion,

		/// <summary><see cref="MembersVault.Restore"/> only: the main file changed since this vault opened it.
		/// Nothing was changed.</summary>
		ChangedElsewhere,

		/// <summary>Another copy of the app is writing the members files right now. Nothing was changed; try
		/// again.</summary>
		Busy,
	}

	public sealed class OpenResult
	{
		public OpenResult(OpenStatus status, MembersData data, bool backupsAvailable = false)
		{
			Status = status;
			Data = data;
			BackupsAvailable = backupsAvailable;
		}

		public OpenStatus Status { get; }

		/// <summary>The data when it opened (or an empty list for <see cref="OpenStatus.NewEmpty"/>); otherwise null.</summary>
		public MembersData Data { get; }

		public bool IsOpen => Data != null;

		/// <summary>When it did not open: whether daily backups exist that could be restored explicitly.</summary>
		public bool BackupsAvailable { get; }

		/// <summary>A PIN change was interrupted. If the file did not open, it opens only with the PIN that was in
		/// use when the change stopped (the old one, or the new one if the change had already reached the main
		/// file); the older copies are not offered meanwhile.</summary>
		public bool PinChangeInterrupted { get; internal set; }

		/// <summary>Some records of the file had ids this app does not make (from an early version); they were given
		/// new ids, which are stored with the next save.</summary>
		public bool IdsMigrated { get; internal set; }

		/// <summary>Restore: files moved aside (never deleted).</summary>
		public List<string> SetAside { get; } = new List<string>();

		/// <summary>Restore: backups and set-aside files that are not under the vault's key afterwards (they could
		/// not be re-encrypted, or were saved with another PIN). They may still open with an older PIN.</summary>
		public List<string> NotReKeyed { get; } = new List<string>();

		/// <summary>Entries named like members files that are not plain files (folders, links, pipes, devices,
		/// empty or over 64 MB). They were not read.</summary>
		public List<string> Skipped { get; } = new List<string>();
	}

	public enum SaveStatus
	{
		Saved,

		/// <summary>Another copy of the app changed the file since it was opened here. Nothing was written.</summary>
		ChangedElsewhere,

		/// <summary>The vault is not open (locked, or the last open failed). Nothing was written.</summary>
		NotOpen,

		/// <summary><see cref="MembersVault.Rewrap"/> only: the vault was opened from the previous copy and has not
		/// saved yet, so there is no good main file to re-encrypt. Save first, then change the PIN. Nothing was
		/// written.</summary>
		SaveFirst,

		/// <summary>Another copy of the app is writing the members files right now. Nothing was written; try
		/// again.</summary>
		Busy,
	}

	public sealed class SaveResult
	{
		public SaveResult(SaveStatus status)
		{
			Status = status;
		}

		public SaveStatus Status { get; }

		public bool Ok => Status == SaveStatus.Saved;

		/// <summary>The day's backup could not be made (for example "Backups" is not a folder). The save itself
		/// succeeded.</summary>
		public bool BackupSkipped { get; internal set; }

		/// <summary>The previous copy could not be written (for example ".prev" is a folder). The save itself
		/// succeeded.</summary>
		public bool PreviousSkipped { get; internal set; }

		/// <summary>Files moved aside instead of being overwritten. They are never deleted automatically.</summary>
		public List<string> SetAside { get; } = new List<string>();

		/// <summary><see cref="MembersVault.Rewrap"/> only: managed files that do not open with the vault's current
		/// key and so were not re-encrypted. They were saved with another key: possibly with an older PIN, and
		/// possibly still with the PIN being replaced (for example a file from another computer). The screen can
		/// offer to delete them.</summary>
		public List<string> LeftUnderOtherPins { get; } = new List<string>();

		/// <summary><see cref="MembersVault.Rewrap"/> only: the new PIN is in force (the main file is under it) but
		/// renaming the other files did not finish. The vault finishes it before its next write, and
		/// <see cref="MembersVault.Open"/> with the new PIN finishes it too.</summary>
		public bool PinChangePending { get; internal set; }

		/// <summary>Entries named like members files that are not plain files; they were not read.</summary>
		public List<string> Skipped { get; } = new List<string>();
	}

	/// <summary>
	/// EBMC Edition: the encrypted members file in a folder (the app passes its per-user data folder). It holds:
	/// <list type="bullet">
	/// <item><c>members.ebmc</c>, the file;</item>
	/// <item><c>members.ebmc.prev</c>, the file as it was before the last save;</item>
	/// <item><c>Backups/members-yyyy-MM-dd.ebmc</c>, one copy a day (UTC date) taken before the first save of the day,
	/// the newest 14 kept;</item>
	/// <item><c>members.ebmc[.prev].replaced-&lt;UTC time&gt;</c>, files moved aside instead of being overwritten
	/// (never deleted automatically; <see cref="ListSetAside"/> and <see cref="Restore"/>);</item>
	/// <item><c>members.ebmc.rewrap</c>, the journal of an unfinished PIN change (file names only).</item>
	/// </list>
	/// Rules:
	/// <list type="bullet">
	/// <item>Saving writes a temporary file, flushes it to disk and renames it over the old file.</item>
	/// <item>Opening tries the main file, then <c>.prev</c> with the same PIN (unless a PIN change is unfinished).
	/// Backups and set-aside files are never opened automatically.</item>
	/// <item>A file is overwritten only when it is the one this vault opened or wrote, or older data under the
	/// current key; anything else (damaged, another key, newer app version) is moved aside first.</item>
	/// <item>Only plain files up to 64 MB are ever read; folders, links, pipes and devices are skipped.</item>
	/// <item>A wrong PIN is a result, never an exception, and never changes a file.</item>
	/// </list>
	/// No network, no logging.
	/// </summary>
	public sealed class MembersVault
	{
		public const string FileName = "members.ebmc";
		public const int BackupsKept = 14;

		/// <summary>No members file is anywhere near this; anything larger is not read.</summary>
		public const long MaxFileBytes = 64L * 1024 * 1024;

		/// <summary>The most different salts (slow key derivations) one restore tries when re-encrypting files.</summary>
		public const int MaxSaltsPerOperation = 20;

		private const string ReplacedMarker = ".replaced-";
		private const string NewSuffix = ".new.tmp";
		private const string JournalHeader = "ebmc-members-rewrap 1";
		private const int MaxJournalBytes = 256 * 1024;
		private const int MaxJournalEntries = 1000;

		private static readonly Regex BackupName = new Regex(@"^members-[0-9]{4}-[0-9]{2}-[0-9]{2}\.ebmc$", RegexOptions.CultureInvariant);

		private readonly Func<DateTime> clockUtc;

		private byte[] dataKey;
		private WrappedKey wrapped;

		/// <summary>What the main file was when opened or last saved here (null: no file).</summary>
		private string knownFingerprint;

		/// <summary>Whether the main file opened (or was written) here with the current key.</summary>
		private bool mainIsGood;

		/// <summary>Whether the last file opened needed new ids (see <see cref="OpenResult.IdsMigrated"/>).</summary>
		private bool lastOpenMigratedIds;

		/// <summary>When opened from the previous copy: its wrapped key, until the first save succeeds.</summary>
		private WrappedKey previousWrapAtOpen;

		public MembersVault(string folder, Func<DateTime> clockUtc)
		{
			if (string.IsNullOrWhiteSpace(folder))
			{
				throw new ArgumentException("A folder is required.", nameof(folder));
			}

			Folder = Path.GetFullPath(folder);
			this.clockUtc = clockUtc ?? (() => DateTime.UtcNow);
		}

		private enum EntryKind
		{
			Missing,
			Regular,

			/// <summary>Length 0: an empty file, or a pipe, device or socket (which report no length). Never opened.</summary>
			Empty,
			Link,
			Folder,
			TooLarge,
			Error,
		}

		private enum JournalOutcome
		{
			None,
			RolledBack,
			RolledForward,
			Pending,
			NotOurs,
		}

		public string Folder { get; }

		public string FilePath => Path.Combine(Folder, FileName);

		public string PreviousPath => FilePath + ".prev";

		public string BackupFolder => Path.Combine(Folder, "Backups");

		public string JournalPath => FilePath + ".rewrap";

		/// <summary>
		/// Held open with no sharing while any write runs (save, PIN change, restore, finishing a PIN change,
		/// clearing temporary files). The file itself means nothing: a copy of the app that crashed leaves it
		/// behind, but the system releases its lock with the process, so it never blocks.
		/// </summary>
		public string LockPath => FilePath + ".lock";

		public bool IsOpen => dataKey != null;

		/// <summary>
		/// Test seam: called with a step name just before each write, rename or delete of a managed file; a test
		/// throws an IOException here to simulate a failure, or copies the folder to simulate a crash.
		/// </summary>
		internal Action<string> FaultPoint { get; set; }

		private string MainNewPath => FilePath + NewSuffix;

		/// <summary>
		/// Opens the members file with <paramref name="pin"/>. If the main file does not open with it (damaged, or a
		/// damaged key part), the previous copy is tried with the same PIN, except while a PIN change is unfinished:
		/// then only the main file counts, and opening it finishes or undoes the change. Backups are never opened
		/// here. After a successful open, temporary files left by a crash are deleted.
		/// <para>Only a clean open of the main file is decided without the write lock. Everything else (falling back
		/// to the previous copy, starting empty, finishing a PIN change) is decided again from scratch while holding
		/// the lock, so it never sees another copy of the app half way through a save or a PIN change; if the lock is
		/// taken the result is Busy.</para>
		/// </summary>
		public OpenResult Open(string pin)
		{
			Close();
			bool backups = ListBackups().Count > 0;
			if (string.IsNullOrEmpty(pin))
			{
				ResetOpenState();
				return new OpenResult(OpenStatus.WrongPin, null, backups);
			}

			var quick = OpenCore(pin, backups, locked: false);
			if (quick != null)
			{
				return quick;
			}

			FileStream held;
			try
			{
				held = TryLock();
			}
			catch (MembersFileException)
			{
				Close();
				return new OpenResult(OpenStatus.Unreadable, null, backups);
			}

			if (held == null)
			{
				Close();
				return new OpenResult(OpenStatus.Busy, null, backups);
			}

			using (held)
			{
				return OpenCore(pin, ListBackups().Count > 0, locked: true);
			}
		}

		private void ResetOpenState()
		{
			knownFingerprint = null;
			mainIsGood = false;
			previousWrapAtOpen = null;
			lastOpenMigratedIds = false;
		}

		/// <summary>
		/// One attempt at opening, from scratch. Without the lock it returns null for anything but a clean open of the
		/// main file (or a final answer that changes nothing), so the caller takes the lock and asks again.
		/// </summary>
		private OpenResult OpenCore(string pin, bool backups, bool locked)
		{
			Close();
			ResetOpenState();
			bool journal = File.Exists(JournalPath) || Directory.Exists(JournalPath);
			Fault(locked ? "open-locked-journal-checked" : "open-journal-checked");
			var mainKind = Inspect(FilePath);
			knownFingerprint = Fingerprint(FilePath);

			var mainStatus = OpenStatus.NewEmpty; // meaning: no main file
			switch (mainKind)
			{
				case EntryKind.Regular:
					if (!TryReadRegular(FilePath, out var mainBytes))
					{
						if (!locked)
						{
							return null;
						}

						return new OpenResult(OpenStatus.Unreadable, null, backups);
					}

					mainStatus = TryOpenBytes(mainBytes, pin, out var data);
					if (mainStatus == OpenStatus.Opened)
					{
						mainIsGood = true;
						var opened = new OpenResult(OpenStatus.Opened, data) { IdsMigrated = lastOpenMigratedIds };
						if (journal)
						{
							if (locked)
							{
								opened.PinChangeInterrupted = ResolveJournal(dataKey) == JournalOutcome.Pending;
							}
							else
							{
								// finishing or undoing the PIN change writes, so it needs the lock
								FileStream held;
								try
								{
									held = TryLock();
								}
								catch (MembersFileException)
								{
									Close();
									return new OpenResult(OpenStatus.Unreadable, null, backups);
								}

								if (held == null)
								{
									Close();
									return new OpenResult(OpenStatus.Busy, null, backups);
								}

								using (held)
								{
									opened.PinChangeInterrupted = ResolveJournal(dataKey) == JournalOutcome.Pending;
								}
							}
						}

						if (locked)
						{
							if (!File.Exists(JournalPath))
							{
								DeleteStrayTemps();
							}
						}
						else
						{
							CleanStrayTempsIfFree();
						}

						return opened;
					}

					if (mainStatus == OpenStatus.NewerVersion)
					{
						Close();
						return new OpenResult(OpenStatus.NewerVersion, null, backups);
					}

					break;

				case EntryKind.Empty:
					// damaged (or a pipe, never opened): the previous copy may help
					mainStatus = OpenStatus.Unreadable;
					break;

				case EntryKind.Missing:
					break;

				default:
					// a folder, a link, too large: never read and never replaced
					return new OpenResult(OpenStatus.Unreadable, null, backups);
			}

			if (!locked)
			{
				// anything but a clean open of the main file is decided under the lock
				Close();
				return null;
			}

			if (journal)
			{
				// an unfinished PIN change: the previous copy may hold older data under either PIN, so it is not offered
				Close();
				return new OpenResult(mainStatus == OpenStatus.WrongPin ? OpenStatus.WrongPin : OpenStatus.Unreadable, null, backups)
				{
					PinChangeInterrupted = true,
				};
			}

			var previousStatus = OpenStatus.NewEmpty;
			var previousKind = Inspect(PreviousPath);
			if (previousKind == EntryKind.Regular && TryReadRegular(PreviousPath, out var previousBytes))
			{
				previousStatus = TryOpenBytes(previousBytes, pin, out var data);
				if (previousStatus == OpenStatus.Opened)
				{
					// remembered, so the first save can refuse if the main file turns out to be under another key
					previousWrapAtOpen = wrapped;
					DeleteStrayTemps();
					return new OpenResult(OpenStatus.OpenedFromPrevious, data) { IdsMigrated = lastOpenMigratedIds };
				}

				if (previousStatus == OpenStatus.NewerVersion)
				{
					Close();
					return new OpenResult(OpenStatus.NewerVersion, null, backups);
				}
			}
			else if (previousKind != EntryKind.Missing)
			{
				previousStatus = OpenStatus.Unreadable;
			}

			Close();
			if (mainKind == EntryKind.Missing && previousKind == EntryKind.Missing)
			{
				if (backups)
				{
					// the files are gone but backups exist: restoring is the person's choice, never automatic
					return new OpenResult(OpenStatus.Unreadable, null, true);
				}

				dataKey = MembersCrypto.NewDataKey();
				wrapped = MembersCrypto.WrapKey(dataKey, pin);
				DeleteStrayTemps();
				return new OpenResult(OpenStatus.NewEmpty, new MembersData());
			}

			bool wrongPin = mainStatus == OpenStatus.WrongPin || previousStatus == OpenStatus.WrongPin;
			return new OpenResult(wrongPin ? OpenStatus.WrongPin : OpenStatus.Unreadable, null, backups);
		}

		/// <summary>
		/// The daily backups, newest first.
		/// </summary>
		public IReadOnlyList<string> ListBackups()
		{
			try
			{
				if (!Directory.Exists(BackupFolder))
				{
					return new List<string>();
				}

				// a name dated more than a day ahead (or not a real date) is not listed, pruned or counted, so it
				// cannot push the real backups out
				var latest = clockUtc().Date.AddDays(1);
				return AllBackupFiles()
					.Where(f => MembersRules.TryParseIso(Path.GetFileName(f).Substring(8, 10), out var date) && date <= latest)
					.OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
					.ToList();
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return new List<string>();
			}
		}

		/// <summary>
		/// Every file in Backups named like a daily backup, whatever its date (a PIN change re-encrypts them all).
		/// </summary>
		private IReadOnlyList<string> AllBackupFiles()
		{
			try
			{
				if (!Directory.Exists(BackupFolder))
				{
					return new List<string>();
				}

				return Directory.GetFiles(BackupFolder)
					.Where(f => BackupName.IsMatch(Path.GetFileName(f)))
					.OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
					.ToList();
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return new List<string>();
			}
		}

		/// <summary>
		/// The files moved aside instead of being overwritten (never deleted automatically). Each can be brought back
		/// with <see cref="Restore"/>.
		/// </summary>
		public IReadOnlyList<string> ListSetAside()
		{
			try
			{
				return Directory.GetFiles(Folder)
					.Where(f => Path.GetFileName(f).StartsWith(FileName, StringComparison.Ordinal)
						&& Path.GetFileName(f).Contains(ReplacedMarker, StringComparison.Ordinal)
						&& !f.EndsWith(".tmp", StringComparison.Ordinal))
					.OrderBy(f => f, StringComparer.Ordinal)
					.ToList();
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return new List<string>();
			}
		}

		/// <summary>
		/// Brings back one of <see cref="ListBackups"/> or <see cref="ListSetAside"/>, or the previous copy
		/// (<see cref="PreviousPath"/>, the way to go on when a save after opening from it is refused because the
		/// main file is under another key), which may have been saved with an older key string
		/// (<paramref name="filePin"/>: an older passphrase or PIN; not needed when it is under the vault's key). The
		/// key strings here (named "pin" for history) are the members passphrase.
		/// <para>The key of the result is always checked, never assumed:</para>
		/// <list type="bullet">
		/// <item>With the vault open, its own (checked) key is used. Without <paramref name="isCurrentPin"/>,
		/// <paramref name="currentPin"/> is ignored; with it, <paramref name="currentPin"/> must pass it and open the
		/// vault's own key part, or the result is WrongPin and nothing changes.</item>
		/// <item>Closed: <paramref name="currentPin"/> must pass <paramref name="isCurrentPin"/> when given, and must
		/// open the key part of the main file or the previous copy when one of them is readable.</item>
		/// <item>Closed with nothing on disk to check against and no <paramref name="isCurrentPin"/>: refused unless
		/// <paramref name="currentPin"/> is the PIN that opens the file being restored.</item>
		/// </list>
		/// <para>The members session passes no <paramref name="isCurrentPin"/> (nothing about the passphrase is stored);
		/// it checks <see cref="KeyOpensWith"/> first while the vault is open, and otherwise relies on the rules above, so
		/// a typo can never leave the members file under a passphrase nobody knows.</para>
		/// <para>The current main file and previous copy (and, if the main file is unreadable, an unfinished PIN
		/// change's journal and staged files) are moved aside first, never deleted; then the restored data becomes the
		/// main file under the checked key, in one atomic rename. The vault stays open on it. Busy if another copy of
		/// the app is writing. Throws <see cref="MembersFileException"/> (fixed message) if a write fails; the main
		/// file is then unchanged.</para>
		/// </summary>
		public OpenResult Restore(string file, string filePin, string currentPin = null, Func<string, bool> isCurrentPin = null)
		{
			using (var held = TryLock())
			{
				if (held == null)
				{
					return new OpenResult(OpenStatus.Busy, null, ListBackups().Count > 0);
				}

				return RestoreLocked(file, filePin, currentPin, isCurrentPin);
			}
		}

		private OpenResult RestoreLocked(string file, string filePin, string currentPin, Func<string, bool> isCurrentPin)
		{
			var full = SafeFullPath(file);
			if (file == null
				|| !(ListBackups().Contains(full, StringComparer.Ordinal)
					|| ListSetAside().Contains(full, StringComparer.Ordinal)
					|| full == PreviousPath))
			{
				return new OpenResult(OpenStatus.Unreadable, null, ListBackups().Count > 0);
			}

			if (!TryReadRegular(full, out var fileBytes))
			{
				var skipped = new OpenResult(OpenStatus.Unreadable, null, true);
				skipped.Skipped.Add(full);
				return skipped;
			}

			byte[] key;
			WrappedKey keyWrap;
			bool freshKey = false;
			bool journalAside;
			var kekCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
			if (IsOpen)
			{
				// with the app's check given, the vault's key must be under a PIN it accepts: checked before anything
				// is written (a rejected PIN changes nothing)
				if (isCurrentPin != null && !KeyIsUnderCheckedPin(currentPin, isCurrentPin))
				{
					return new OpenResult(OpenStatus.WrongPin, null, ListBackups().Count > 0);
				}

				var journalOutcome = ResolvePendingWithKey();
				if (journalOutcome == JournalOutcome.NotOurs || !MainUnchanged())
				{
					return new OpenResult(OpenStatus.ChangedElsewhere, null);
				}

				if (journalOutcome == JournalOutcome.Pending)
				{
					throw new MembersFileException(MembersFileProblem.SaveFailed);
				}

				journalAside = false;
				key = (byte[])dataKey.Clone();
				keyWrap = wrapped;
			}
			else
			{
				journalAside = File.Exists(JournalPath);
				if (journalAside
					&& TryReadRegular(FilePath, out var journalMain)
					&& MembersCrypto.TryParseEnvelope(journalMain, out _, out _))
				{
					// the main file can be opened: open it with the right PIN, which finishes or undoes the PIN change
					return new OpenResult(OpenStatus.Unreadable, null, true) { PinChangeInterrupted = true };
				}

				if (string.IsNullOrEmpty(currentPin) || !Passes(isCurrentPin, currentPin))
				{
					return new OpenResult(OpenStatus.WrongPin, null, true);
				}

				key = null;
				keyWrap = null;
				bool evidence = false;
				foreach (var candidate in new[] { FilePath, PreviousPath })
				{
					if (TryReadRegular(candidate, out var candidateBytes)
						&& MembersCrypto.TryParseEnvelope(candidateBytes, out var candidateWrap, out _))
					{
						evidence = true;
						key = UnwrapCached(candidateWrap, currentPin, kekCache);
						if (key != null)
						{
							keyWrap = candidateWrap;
							break;
						}
					}
				}

				if (evidence && key == null)
				{
					ZeroAll(kekCache);
					return new OpenResult(OpenStatus.WrongPin, null, true);
				}

				if (key == null)
				{
					// nothing on disk to check the PIN against: only the app's check, or the restored file's own PIN
					if (isCurrentPin == null && currentPin != filePin)
					{
						ZeroAll(kekCache);
						return new OpenResult(OpenStatus.WrongPin, null, true);
					}

					// keep the file's own key if it is under this PIN
					if (MembersCrypto.TryParseEnvelope(fileBytes, out var fileWrap, out _)
						&& (key = UnwrapCached(fileWrap, currentPin, kekCache)) != null)
					{
						keyWrap = fileWrap;
					}
					else
					{
						key = MembersCrypto.NewDataKey();
						keyWrap = MembersCrypto.WrapKey(key, currentPin);
						freshKey = true;
					}
				}

				knownFingerprint = Fingerprint(FilePath);
				mainIsGood = false;
			}

			// the chosen file: under the vault's key, or with its own PIN
			byte[] plain = MembersCrypto.OpenFileWithKey(fileBytes, key);
			if (plain == null)
			{
				var status = EnvelopeOpenStatus.WrongPin;
				if (!string.IsNullOrEmpty(filePin))
				{
					status = MembersCrypto.OpenFile(fileBytes, filePin, out plain, out var fileKey, out _);
					if (fileKey != null)
					{
						CryptographicOperations.ZeroMemory(fileKey);
					}
				}

				if (status != EnvelopeOpenStatus.Ok)
				{
					RestoreRefused(key);
					ZeroAll(kekCache);
					return new OpenResult(status == EnvelopeOpenStatus.WrongPin ? OpenStatus.WrongPin : OpenStatus.Unreadable, null, true);
				}
			}

			var result = new OpenResult(OpenStatus.Restored, null, true);
			MembersData data;
			byte[] mainBytes;
			try
			{
				try
				{
					data = MembersExport.ParseJson(plain);
					result.IdsMigrated = MembersRules.AssignWellFormedIds(data) > 0;
				}
				catch (MembersFileException e)
				{
					RestoreRefused(key);
					ZeroAll(kekCache);
					return new OpenResult(e.Problem == MembersFileProblem.NewerVersion ? OpenStatus.NewerVersion : OpenStatus.Unreadable, null, true);
				}

				try
				{
					Directory.CreateDirectory(Folder);
					if (journalAside)
					{
						// an unfinished PIN change whose main file is unreadable: its journal and staged files go aside
						foreach (var (_, temp) in JournalEntries())
						{
							if (File.Exists(temp))
							{
								var aside = ReplacedName(Path.Combine(Folder, FileName + "." + Path.GetFileName(temp)));
								Fault("restore-journal-aside");
								File.Move(temp, aside);
								result.SetAside.Add(aside);
							}
						}

						var journalTarget = ReplacedName(JournalPath);
						Fault("restore-journal-aside");
						File.Move(JournalPath, journalTarget);
						result.SetAside.Add(journalTarget);
					}

					foreach (var source in new[] { FilePath, PreviousPath })
					{
						var kind = Inspect(source);
						if (kind == EntryKind.Missing)
						{
							continue;
						}

						var target = ReplacedName(source);
						if (kind == EntryKind.Regular && TryReadRegular(source, out var bytes))
						{
							WriteAtomic(target, bytes, "restore-set-aside");
						}
						else
						{
							// not a plain file: moved aside unread
							Fault("restore-set-aside-rename");
							File.Move(source, target);
						}

						result.SetAside.Add(target);
					}

					mainBytes = MembersCrypto.SealFile(plain, key, keyWrap);
					WriteAtomic(FilePath, mainBytes, "restore-main");
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					// copies are removed; anything moved aside unread stays aside (never deleted)
					foreach (var copy in result.SetAside.Where(File.Exists).Where(f => Inspect(f) == EntryKind.Regular))
					{
						TryDelete(copy);
					}

					RestoreRefused(key);
					ZeroAll(kekCache);
					throw new MembersFileException(MembersFileProblem.SaveFailed);
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plain);
			}

			// the old previous copy is kept aside above; without it a later fallback cannot open older data
			Fault("restore-delete-prev");
			TryDelete(PreviousPath);

			// the backups and set-aside files under the restored key are covered by a later PIN change; with a new key
			// the files under the current PIN are moved to it (at most MaxSaltsPerOperation slow derivations)
			foreach (var other in AllBackupFiles().Concat(ListSetAside()).Where(f => !result.SetAside.Contains(f)))
			{
				if (!TryReadRegular(other, out var otherBytes))
				{
					result.Skipped.Add(other);
					continue;
				}

				var otherPlain = MembersCrypto.OpenFileWithKey(otherBytes, key);
				if (otherPlain != null)
				{
					CryptographicOperations.ZeroMemory(otherPlain);
					continue;
				}

				if (!freshKey)
				{
					result.NotReKeyed.Add(other);
					continue;
				}

				try
				{
					otherPlain = PlainUnderPin(otherBytes, currentPin, kekCache);
					if (otherPlain == null)
					{
						result.NotReKeyed.Add(other);
						continue;
					}

					WriteAtomic(other, MembersCrypto.SealFile(otherPlain, key, keyWrap), "restore-reencrypt");
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					result.NotReKeyed.Add(other);
				}
				finally
				{
					if (otherPlain != null)
					{
						CryptographicOperations.ZeroMemory(otherPlain);
					}
				}
			}

			ZeroAll(kekCache);
			if (dataKey != null && !ReferenceEquals(dataKey, key))
			{
				CryptographicOperations.ZeroMemory(dataKey);
			}

			dataKey = key;
			wrapped = keyWrap;
			knownFingerprint = Sha(mainBytes);
			mainIsGood = true;
			previousWrapAtOpen = null;
			var restoredResult = WithData(result, data);
			restoredResult.IdsMigrated = result.IdsMigrated;
			return restoredResult;
		}

		/// <summary>
		/// Saves <paramref name="d"/>. Refused when the vault is not open, or when the file changed since it was
		/// opened or saved here (another copy of the app). Finishes an unfinished PIN change first. Throws
		/// <see cref="MembersFileException"/> (fixed message) when writing the main file fails; the main file is then
		/// unchanged and the vault stays usable.
		/// </summary>
		public SaveResult Save(MembersData d)
		{
			if (!IsOpen)
			{
				return new SaveResult(SaveStatus.NotOpen);
			}

			using (var held = TryLock())
			{
				if (held == null)
				{
					return new SaveResult(SaveStatus.Busy);
				}

				if (previousWrapAtOpen != null && !MainStillReplaceable())
				{
					return new SaveResult(SaveStatus.ChangedElsewhere);
				}

				var journalOutcome = ResolvePendingWithKey();
				if (journalOutcome == JournalOutcome.NotOurs)
				{
					return new SaveResult(SaveStatus.ChangedElsewhere);
				}

				// a PIN change whose renames keep failing does not block saving: the main file is already under the new
				// key; the renames are retried on every write
				var plain = MembersExport.ToJsonBytes(d ?? new MembersData());
				try
				{
					var result = WriteMain(MembersCrypto.SealFile(plain, dataKey, wrapped));
					result.PinChangePending = File.Exists(JournalPath);
					return result;
				}
				finally
				{
					CryptographicOperations.ZeroMemory(plain);
				}
			}
		}

		/// <summary>
		/// Changes the PIN. A new data key is made, and every file this vault manages that opens with the current
		/// key (main file, previous copy, daily backups, set-aside files) is re-encrypted with it under
		/// <paramref name="newPin"/>; no backup is taken. Files that do not open with the current key are listed in
		/// <see cref="SaveResult.LeftUnderOtherPins"/>; entries that are not plain files in
		/// <see cref="SaveResult.Skipped"/>.
		/// <para>Steps: (1) every new version is written under a temporary name; (2) a journal listing them is
		/// written; (3) the main file is renamed into place: from here the new PIN is in force; (4) the other files are
		/// renamed; (5) the journal is deleted. A failure in 1-3 deletes the temporary files and the journal and
		/// throws <see cref="MembersFileException"/>: the old PIN stays in force everywhere. A failure in 4-5 returns
		/// Saved with <see cref="SaveResult.PinChangePending"/>: the vault is on the new key and finishes the renames
		/// before its next write. After a crash the journal decides: <see cref="Open"/> with the PIN of the main file
		/// finishes (new PIN) or undoes (old PIN) the change, and no older copy is offered meanwhile.</para>
		/// </summary>
		public SaveResult Rewrap(string newPin)
		{
			if (!IsOpen)
			{
				return new SaveResult(SaveStatus.NotOpen);
			}

			if (string.IsNullOrEmpty(newPin))
			{
				throw new ArgumentException("A PIN is required.", nameof(newPin));
			}

			using (var held = TryLock())
			{
				return held == null ? new SaveResult(SaveStatus.Busy) : RewrapLocked(newPin);
			}
		}

		private SaveResult RewrapLocked(string newPin)
		{
			var journalOutcome = ResolvePendingWithKey();
			if (journalOutcome == JournalOutcome.NotOurs)
			{
				return new SaveResult(SaveStatus.ChangedElsewhere);
			}

			if (journalOutcome == JournalOutcome.Pending)
			{
				throw new MembersFileException(MembersFileProblem.SaveFailed);
			}

			var others = new List<string>();
			if (Inspect(PreviousPath) != EntryKind.Missing)
			{
				others.Add(PreviousPath);
			}

			others.AddRange(AllBackupFiles());
			others.AddRange(ListSetAside());

			if (!mainIsGood)
			{
				if (Inspect(FilePath) == EntryKind.Missing && others.Count == 0)
				{
					// nothing on disk yet: the first save will use the new PIN
					var fresh = MembersCrypto.NewDataKey();
					CryptographicOperations.ZeroMemory(dataKey);
					dataKey = fresh;
					wrapped = MembersCrypto.WrapKey(fresh, newPin);
					return new SaveResult(SaveStatus.Saved);
				}

				return new SaveResult(SaveStatus.SaveFirst);
			}

			if (!MainUnchanged())
			{
				return new SaveResult(SaveStatus.ChangedElsewhere);
			}

			// copies left by a crash would otherwise keep the old PIN
			DeleteStrayTemps();

			var result = new SaveResult(SaveStatus.Saved);
			var newKey = MembersCrypto.NewDataKey();
			var newWrapped = MembersCrypto.WrapKey(newKey, newPin);
			var staged = new List<(string target, string temp)>();
			byte[] newMain;
			try
			{
				if (!TryReadRegular(FilePath, out var mainBytes))
				{
					throw new IOException();
				}

				var mainPlain = MembersCrypto.OpenFileWithKey(mainBytes, dataKey);
				if (mainPlain == null)
				{
					throw new IOException();
				}

				try
				{
					newMain = MembersCrypto.SealFile(mainPlain, newKey, newWrapped);
				}
				finally
				{
					CryptographicOperations.ZeroMemory(mainPlain);
				}

				Fault("rewrap-stage-main");
				staged.Add((FilePath, Stage(MainNewPath, newMain)));

				foreach (var file in others)
				{
					if (!TryReadRegular(file, out var bytes))
					{
						result.Skipped.Add(file);
						continue;
					}

					var plain = MembersCrypto.OpenFileWithKey(bytes, dataKey);
					if (plain == null)
					{
						result.LeftUnderOtherPins.Add(file);
						continue;
					}

					try
					{
						var sealedBytes = MembersCrypto.SealFile(plain, newKey, newWrapped);
						Fault("rewrap-stage");
						staged.Add((file, Stage(file + NewSuffix, sealedBytes)));
					}
					finally
					{
						CryptographicOperations.ZeroMemory(plain);
					}
				}

				WriteAtomic(JournalPath, JournalBytes(staged), "rewrap-journal");
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Abandon(staged);
				CryptographicOperations.ZeroMemory(newKey);
				throw new MembersFileException(MembersFileProblem.SaveFailed);
			}

			// the main file first: from here on the new PIN is in force
			try
			{
				Fault("rewrap-switch-main");
				File.Move(MainNewPath, FilePath, overwrite: true);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// whether it happened is checked below, never assumed
			}

			// success only if the main file now really opens with the new key
			bool switched = false;
			if (TryReadRegular(FilePath, out var nowMain))
			{
				var check = MembersCrypto.OpenFileWithKey(nowMain, newKey);
				if (check != null)
				{
					CryptographicOperations.ZeroMemory(check);
					switched = true;
				}
			}

			if (!switched)
			{
				Abandon(staged);
				CryptographicOperations.ZeroMemory(newKey);
				throw new MembersFileException(MembersFileProblem.SaveFailed);
			}

			CryptographicOperations.ZeroMemory(dataKey);
			dataKey = newKey;
			wrapped = newWrapped;
			knownFingerprint = Sha(newMain);

			result.PinChangePending = ResolveJournal(dataKey) == JournalOutcome.Pending;
			return result;
		}

		/// <summary>
		/// Forgets the key (cleared from memory). The caller drops its copy of the data.
		/// </summary>
		public void Close()
		{
			if (dataKey != null)
			{
				CryptographicOperations.ZeroMemory(dataKey);
			}

			dataKey = null;
			wrapped = null;
		}

		private static OpenResult WithData(OpenResult from, MembersData data)
		{
			var result = new OpenResult(from.Status, data, from.BackupsAvailable) { PinChangeInterrupted = from.PinChangeInterrupted };
			result.SetAside.AddRange(from.SetAside);
			result.NotReKeyed.AddRange(from.NotReKeyed);
			result.Skipped.AddRange(from.Skipped);
			return result;
		}

		private static EntryKind Inspect(string path)
		{
			try
			{
				var info = new FileInfo(path);
				if (info.LinkTarget != null)
				{
					return EntryKind.Link;
				}

				if (Directory.Exists(path))
				{
					return EntryKind.Folder;
				}

				if (!info.Exists)
				{
					return EntryKind.Missing;
				}

				// pipes, devices and sockets report no length, so they are never opened (opening a pipe would wait)
				if (info.Length == 0)
				{
					return EntryKind.Empty;
				}

				return info.Length > MaxFileBytes ? EntryKind.TooLarge : EntryKind.Regular;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
			{
				return EntryKind.Error;
			}
		}

		/// <summary>
		/// Reads a plain file of at most <see cref="MaxFileBytes"/>; false for anything else. No exception.
		/// <para>The type is checked before opening because opening a pipe waits for a writer. Between the check and
		/// the open the entry could be swapped for a pipe; net6 offers no way to open without waiting (no O_NONBLOCK,
		/// no file-type query on a handle) short of platform calls, which this core avoids. What bounds it: the swap
		/// needs write access to the person's own data folder; the worst outcome is that this one call waits (no data
		/// is changed, as every write happens after a successful read); and after opening, a stream that cannot seek
		/// (a pipe) or reports more than the cap is refused, and at most the cap is read.</para>
		/// </summary>
		private static bool TryReadRegular(string path, out byte[] bytes)
		{
			bytes = null;
			if (Inspect(path) != EntryKind.Regular)
			{
				return false;
			}

			try
			{
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					if (!stream.CanSeek || stream.Length > MaxFileBytes)
					{
						return false;
					}

					var buffer = new byte[stream.Length];
					int read = 0;
					while (read < buffer.Length)
					{
						int n = stream.Read(buffer, read, buffer.Length - read);
						if (n == 0)
						{
							break;
						}

						read += n;
					}

					if (read != buffer.Length)
					{
						return false;
					}

					bytes = buffer;
					return true;
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return false;
			}
		}

		private static string Sha(byte[] bytes) => "sha:" + Convert.ToHexString(SHA256.HashData(bytes));

		/// <summary>
		/// What an entry is now: null when missing, the SHA-256 of a plain file, or its kind, size and time otherwise.
		/// </summary>
		private static string Fingerprint(string path)
		{
			var kind = Inspect(path);
			if (kind == EntryKind.Missing)
			{
				return null;
			}

			if (kind == EntryKind.Regular && TryReadRegular(path, out var bytes))
			{
				return Sha(bytes);
			}

			try
			{
				var info = new FileInfo(path);
				return "other:" + kind + ":" + (info.Exists ? info.Length : -1) + ":" + info.LastWriteTimeUtc.Ticks;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return "other:" + kind;
			}
		}

		private static string SafeFullPath(string path)
		{
			try
			{
				return path == null ? "" : Path.GetFullPath(path);
			}
			catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException)
			{
				return "";
			}
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
			}
		}

		private static void ZeroAll(Dictionary<string, byte[]> keks)
		{
			foreach (var kek in keks.Values)
			{
				CryptographicOperations.ZeroMemory(kek);
			}

			keks.Clear();
		}

		/// <summary>
		/// The data key of a wrapped key under <paramref name="pin"/>, deriving at most
		/// <see cref="MaxSaltsPerOperation"/> different salts per operation. Null when it does not open (or the cap
		/// is reached).
		/// </summary>
		private static byte[] UnwrapCached(WrappedKey fileWrap, string pin, Dictionary<string, byte[]> kekCache)
		{
			var saltKey = Convert.ToBase64String(fileWrap.Salt);
			if (!kekCache.TryGetValue(saltKey, out var kek))
			{
				if (kekCache.Count >= MaxSaltsPerOperation)
				{
					return null;
				}

				kekCache[saltKey] = kek = MembersCrypto.DeriveKek(pin, fileWrap.Salt, fileWrap.Iterations);
			}

			return MembersCrypto.UnwrapKeyWithKek(fileWrap, kek);
		}

		private static byte[] PlainUnderPin(byte[] fileBytes, string pin, Dictionary<string, byte[]> kekCache)
		{
			if (string.IsNullOrEmpty(pin) || !MembersCrypto.TryParseEnvelope(fileBytes, out var fileWrap, out _))
			{
				return null;
			}

			var key = UnwrapCached(fileWrap, pin, kekCache);
			if (key == null)
			{
				return null;
			}

			try
			{
				return MembersCrypto.OpenFileWithKey(fileBytes, key);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(key);
			}
		}

		private void RestoreRefused(byte[] key)
		{
			// the vault's own key stays; a copy or a key made for the restore is cleared
			if (key != null && !ReferenceEquals(key, dataKey))
			{
				CryptographicOperations.ZeroMemory(key);
			}
		}

		private void Fault(string step) => FaultPoint?.Invoke(step);

		/// <summary>
		/// The write lock (see <see cref="LockPath"/>), or null when another copy of the app holds it. The lock is an
		/// exclusive open of the lock file; the system lets go of it when the holder exits, even after a crash.
		/// </summary>
		private FileStream TryLock()
		{
			try
			{
				Directory.CreateDirectory(Folder);
				return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException e) when (IsLockViolation(e))
			{
				return null;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// any other failure (no such folder, not a file, no permission) is a failure, not "busy"
				throw new MembersFileException(MembersFileProblem.SaveFailed);
			}
		}

		/// <summary>
		/// Whether opening failed only because another holder has the file: on Windows a sharing or lock violation
		/// (0x80070020, 0x80070021); on macOS and Linux .NET reports the refused lock as a plain IOException carrying
		/// EWOULDBLOCK (35 on macOS and BSD, 11 on Linux).
		/// </summary>
		private static bool IsLockViolation(IOException e)
		{
			if (e.GetType() != typeof(IOException))
			{
				return false;
			}

			int code = e.HResult;
			if (OperatingSystem.IsWindows())
			{
				return code == unchecked((int)0x80070020) || code == unchecked((int)0x80070021);
			}

			return code == 35 || code == 11;
		}

		/// <summary>
		/// For a vault opened from the previous copy: the main file may be replaced only if no PIN change is
		/// unfinished and the main file is not a readable file under another key than the previous copy's (a newer
		/// file under a new PIN must never be replaced by older data under the old one).
		/// </summary>
		private bool MainStillReplaceable()
		{
			if (File.Exists(JournalPath) || Directory.Exists(JournalPath))
			{
				return false;
			}

			if (TryReadRegular(FilePath, out var mainBytes) && MembersCrypto.TryParseEnvelope(mainBytes, out var mainWrap, out _))
			{
				return SameWrap(mainWrap, previousWrapAtOpen);
			}

			return true;
		}

		private static bool SameWrap(WrappedKey a, WrappedKey b)
		{
			return a.Iterations == b.Iterations
				&& a.Salt.AsSpan().SequenceEqual(b.Salt)
				&& a.Nonce.AsSpan().SequenceEqual(b.Nonce)
				&& a.Ciphertext.AsSpan().SequenceEqual(b.Ciphertext)
				&& a.Tag.AsSpan().SequenceEqual(b.Tag);
		}

		/// <summary>
		/// Deletes temporary files left by a crash, only when there are some, no PIN change is unfinished, and the
		/// lock is free (otherwise another copy of the app may be writing them right now).
		/// </summary>
		private void CleanStrayTempsIfFree()
		{
			if (File.Exists(JournalPath) || !StrayTemps().Any())
			{
				return;
			}

			try
			{
				using (var held = TryLock())
				{
					if (held != null && !File.Exists(JournalPath))
					{
						DeleteStrayTemps();
					}
				}
			}
			catch (MembersFileException)
			{
				// no write access: nothing can be cleaned, and opening still succeeds
			}
		}

		private static bool Passes(Func<string, bool> isCurrentPin, string pin)
		{
			if (isCurrentPin == null)
			{
				return true;
			}

			try
			{
				return isCurrentPin(pin);
			}
			catch (Exception)
			{
				// whatever the app's check throws counts as "not the current PIN"
				return false;
			}
		}

		/// <summary>
		/// With the vault open: whether <paramref name="pass"/> opens the vault's own key part to the key in use (one
		/// slow derivation; read only). False when the vault is closed or <paramref name="pass"/> is empty.
		/// </summary>
		public bool KeyOpensWith(string pass) => KeyIsUnderCheckedPin(pass, null);

		/// <summary>
		/// With the vault open: whether <paramref name="pin"/> passes the app's check and opens the vault's own key
		/// part to the key in use (one slow derivation).
		/// </summary>
		private bool KeyIsUnderCheckedPin(string pin, Func<string, bool> isCurrentPin)
		{
			if (string.IsNullOrEmpty(pin) || wrapped == null || !Passes(isCurrentPin, pin))
			{
				return false;
			}

			var kekCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
			var key = UnwrapCached(wrapped, pin, kekCache);
			ZeroAll(kekCache);
			if (key == null)
			{
				return false;
			}

			try
			{
				return CryptographicOperations.FixedTimeEquals(key, dataKey);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(key);
			}
		}

		private static bool OpensWithKey(string path, byte[] key)
		{
			if (!TryReadRegular(path, out var bytes))
			{
				return false;
			}

			var plain = MembersCrypto.OpenFileWithKey(bytes, key);
			if (plain == null)
			{
				return false;
			}

			CryptographicOperations.ZeroMemory(plain);
			return true;
		}

		/// <summary>
		/// Opens file bytes with the PIN; on success the vault holds the file's key.
		/// </summary>
		private OpenStatus TryOpenBytes(byte[] fileBytes, string pin, out MembersData data)
		{
			data = null;
			var status = MembersCrypto.OpenFile(fileBytes, pin, out var plain, out var key, out var keyWrap);
			if (status == EnvelopeOpenStatus.WrongPin)
			{
				return OpenStatus.WrongPin;
			}

			if (status != EnvelopeOpenStatus.Ok)
			{
				return OpenStatus.Unreadable;
			}

			try
			{
				data = MembersExport.ParseJson(plain);

				// records from an early version may have ids this app no longer makes: give them new ones, kept
				// consistent in the projects; the next save stores them
				lastOpenMigratedIds = MembersRules.AssignWellFormedIds(data) > 0;
			}
			catch (MembersFileException e)
			{
				CryptographicOperations.ZeroMemory(key);
				return e.Problem == MembersFileProblem.NewerVersion ? OpenStatus.NewerVersion : OpenStatus.Unreadable;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plain);
			}

			Close();
			dataKey = key;
			wrapped = keyWrap;
			return OpenStatus.Opened;
		}

		/// <summary>The main file is exactly as it was opened or last saved here (or still absent).</summary>
		private bool MainUnchanged() => Fingerprint(FilePath) == knownFingerprint;

		/// <summary>
		/// Whether <paramref name="path"/> holds data this vault may overwrite: it opens with the current key and is
		/// not from a newer app version.
		/// </summary>
		private bool IsOursAndNotNewer(string path)
		{
			if (!TryReadRegular(path, out var bytes))
			{
				return false;
			}

			var plain = MembersCrypto.OpenFileWithKey(bytes, dataKey);
			if (plain == null)
			{
				return false;
			}

			int version = MembersExport.ReadSchemaVersion(plain);
			CryptographicOperations.ZeroMemory(plain);
			return version >= 1 && version <= MembersData.CurrentSchemaVersion;
		}

		// --- the PIN-change journal ---

		private static byte[] JournalText(IEnumerable<string> lines) => Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");

		private byte[] JournalBytes(List<(string target, string temp)> staged)
		{
			// file names relative to the folder: no secrets, no member data
			return JournalText(new[] { JournalHeader }.Concat(staged.Select(s => Relative(s.target) + "\t" + Relative(s.temp))));
		}

		private string Relative(string path) => Path.GetRelativePath(Folder, path).Replace('\\', '/');

		/// <summary>
		/// The staged files of an unfinished PIN change: from the journal when it reads, and in any case every
		/// <c>*.new.tmp</c> next to a managed file (the naming rule), so a damaged journal loses nothing.
		/// </summary>
		private List<(string target, string temp)> JournalEntries()
		{
			var entries = new List<(string target, string temp)>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			void Add(string target, string temp)
			{
				if (entries.Count < MaxJournalEntries && seen.Add(temp))
				{
					entries.Add((target, temp));
				}
			}

			// a journal larger than any real one is ignored; the naming rule below still finds the staged files
			if (new FileInfo(JournalPath) is var journalInfo && journalInfo.Exists && journalInfo.Length <= MaxJournalBytes
				&& TryReadRegular(JournalPath, out var bytes))
			{
				var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
				if (lines.Length > 0 && lines[0] == JournalHeader)
				{
					foreach (var line in lines.Skip(1))
					{
						var parts = line.Split('\t');
						if (parts.Length == 2
							&& parts[1] == parts[0] + NewSuffix
							&& IsManagedRelative(parts[0]))
						{
							Add(Path.Combine(Folder, parts[0]), Path.Combine(Folder, parts[1]));
						}
					}
				}
			}

			foreach (var directory in new[] { Folder, BackupFolder })
			{
				try
				{
					if (!Directory.Exists(directory))
					{
						continue;
					}

					foreach (var temp in Directory.GetFiles(directory, "*" + NewSuffix).Take(MaxJournalEntries))
					{
						var target = temp.Substring(0, temp.Length - NewSuffix.Length);
						if (IsManagedRelative(Relative(target)))
						{
							Add(target, temp);
						}
					}
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
				}
			}

			return entries;
		}

		/// <summary>Only the main file, the previous copy, set-aside files and dated backups; never "..".</summary>
		private static bool IsManagedRelative(string relative)
		{
			if (string.IsNullOrEmpty(relative) || relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
			{
				return false;
			}

			if (relative.StartsWith("Backups/", StringComparison.Ordinal))
			{
				return BackupName.IsMatch(relative.Substring("Backups/".Length));
			}

			return relative == FileName
				|| relative == FileName + ".prev"
				|| (relative.StartsWith(FileName, StringComparison.Ordinal) && relative.Contains(ReplacedMarker, StringComparison.Ordinal) && !relative.Contains('/'));
		}

		/// <summary>
		/// Finishes or undoes an unfinished PIN change. The caller has checked that the main file opens with
		/// <paramref name="keyOfMain"/>. If the main file's new version is still waiting, the change never reached
		/// it: the staged files are deleted (undo). Otherwise each staged file that opens with the main file's key is
		/// renamed into place (finish). Never throws.
		/// </summary>
		private JournalOutcome ResolveJournal(byte[] keyOfMain)
		{
			if (!File.Exists(JournalPath))
			{
				return JournalOutcome.None;
			}

			var entries = JournalEntries();
			try
			{
				if (File.Exists(MainNewPath))
				{
					foreach (var (_, temp) in entries)
					{
						Fault("journal-undo");
						if (File.Exists(temp))
						{
							File.Delete(temp);
						}
					}

					Fault("rewrap-journal-delete");
					File.Delete(JournalPath);
					return JournalOutcome.RolledBack;
				}

				foreach (var (target, temp) in entries.Where(e => e.target != FilePath))
				{
					if (!File.Exists(temp))
					{
						continue;
					}

					byte[] plain = null;
					try
					{
						plain = TryReadRegular(temp, out var bytes) ? MembersCrypto.OpenFileWithKey(bytes, keyOfMain) : null;
					}
					finally
					{
						if (plain != null)
						{
							CryptographicOperations.ZeroMemory(plain);
						}
					}

					Fault("rewrap-switch");
					if (plain != null && !OpensWithKey(target, keyOfMain))
					{
						// the target is still the old version (old key): replace it with its re-encrypted copy
						File.Move(temp, target, overwrite: true);
					}
					else
					{
						// the target is already under the new key (written since, for example a newer .prev from a
						// save), or the staged copy is not ours: the staged copy goes
						File.Delete(temp);
					}
				}

				Fault("rewrap-journal-delete");
				File.Delete(JournalPath);
				return JournalOutcome.RolledForward;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return JournalOutcome.Pending;
			}
		}

		/// <summary>
		/// With the vault open: resolves a journal if the main file opens with the vault's key; NotOurs otherwise.
		/// </summary>
		private JournalOutcome ResolvePendingWithKey()
		{
			if (!File.Exists(JournalPath))
			{
				return JournalOutcome.None;
			}

			if (!TryReadRegular(FilePath, out var mainBytes))
			{
				return JournalOutcome.NotOurs;
			}

			var plain = MembersCrypto.OpenFileWithKey(mainBytes, dataKey);
			if (plain == null)
			{
				return JournalOutcome.NotOurs;
			}

			CryptographicOperations.ZeroMemory(plain);
			return ResolveJournal(dataKey);
		}

		/// <summary>Deletes the staged files and the journal of a PIN change that did not reach the main file.</summary>
		private void Abandon(List<(string target, string temp)> staged)
		{
			foreach (var (_, temp) in staged)
			{
				TryDelete(temp);
			}

			TryDelete(JournalPath);
		}

		/// <summary>
		/// Deletes temporary files left by a crash (ours only: names starting with "members" and ending ".tmp"), in
		/// the folder and in Backups. Only called when no PIN change is unfinished.
		/// </summary>
		private void DeleteStrayTemps()
		{
			foreach (var file in StrayTemps())
			{
				TryDelete(file);
			}
		}

		/// <summary>Our temporary files: names starting with "members" and ending ".tmp", in the folder and Backups.</summary>
		private List<string> StrayTemps()
		{
			var found = new List<string>();
			foreach (var directory in new[] { Folder, BackupFolder })
			{
				try
				{
					if (!Directory.Exists(directory))
					{
						continue;
					}

					foreach (var file in Directory.GetFiles(directory))
					{
						var name = Path.GetFileName(file);
						if (name.StartsWith("members", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal))
						{
							found.Add(file);
						}
					}
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
				}
			}

			return found;
		}

		// --- saving ---

		private SaveResult WriteMain(byte[] fileBytes)
		{
			var result = new SaveResult(SaveStatus.Saved);
			try
			{
				Directory.CreateDirectory(Folder);
				if (!MainUnchanged())
				{
					return new SaveResult(SaveStatus.ChangedElsewhere);
				}

				var mainKind = Inspect(FilePath);
				if (mainKind != EntryKind.Missing)
				{
					if (mainIsGood)
					{
						result.BackupSkipped = !TryMakeBackupIfDue();
						TryRotateMainToPrevious(result);
					}
					else
					{
						// opened from the previous copy: the main file (damaged, or under another PIN) may hold newer
						// data, so it is kept aside, and the previous copy is left as it is
						var target = ReplacedName(FilePath);
						if (mainKind == EntryKind.Regular && TryReadRegular(FilePath, out var oldMain))
						{
							WriteAtomic(target, oldMain, "set-aside");
						}
						else
						{
							Fault("set-aside-rename");
							File.Move(FilePath, target);
						}

						result.SetAside.Add(target);
					}
				}

				WriteAtomic(FilePath, fileBytes, "main");
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				throw new MembersFileException(MembersFileProblem.SaveFailed);
			}

			knownFingerprint = Sha(fileBytes);
			mainIsGood = true;
			previousWrapAtOpen = null;
			PruneBackups();
			return result;
		}

		/// <summary>
		/// Copies the main file to <c>.prev</c>. A <c>.prev</c> that is not ours (another key, damaged, newer app
		/// version, not a plain file) is moved aside first, unread. A failure here does not stop the save.
		/// </summary>
		private void TryRotateMainToPrevious(SaveResult result)
		{
			try
			{
				var kind = Inspect(PreviousPath);
				if (kind == EntryKind.Folder)
				{
					result.PreviousSkipped = true;
					return;
				}

				// with a PIN change pending, the old .prev has a re-encrypted copy waiting: it is ours, and the copy is stale
				var stagedPrevious = PreviousPath + NewSuffix;
				if (File.Exists(JournalPath) && kind == EntryKind.Regular && OpensWithKey(stagedPrevious, dataKey))
				{
					Fault("prev-staged-delete");
					File.Delete(stagedPrevious);
				}
				else if (kind != EntryKind.Missing && !IsOursAndNotNewer(PreviousPath))
				{
					var target = ReplacedName(PreviousPath);
					Fault("set-aside-prev");
					File.Move(PreviousPath, target);
					result.SetAside.Add(target);
				}

				if (!TryReadRegular(FilePath, out var mainBytes))
				{
					result.PreviousSkipped = true;
					return;
				}

				WriteAtomic(PreviousPath, mainBytes, "prev");
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				result.PreviousSkipped = true;
			}
		}

		/// <summary>
		/// The day's backup (a copy of the main file before the first save of the UTC day). Never overwrites a
		/// backup. False when it was due but could not be made.
		/// </summary>
		private bool TryMakeBackupIfDue()
		{
			try
			{
				var name = "members-" + clockUtc().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".ebmc";
				var target = Path.Combine(BackupFolder, name);
				var kind = Inspect(target);
				if (kind == EntryKind.Regular)
				{
					return true;
				}

				if (kind != EntryKind.Missing || Inspect(BackupFolder) != EntryKind.Folder && Inspect(BackupFolder) != EntryKind.Missing)
				{
					return false;
				}

				if (!TryReadRegular(FilePath, out var mainBytes))
				{
					return false;
				}

				Directory.CreateDirectory(BackupFolder);
				WriteAtomic(target, mainBytes, "backup");
				return true;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return false;
			}
		}

		/// <summary>
		/// Deletes the backups beyond the newest 14, except one from a newer app version.
		/// </summary>
		private void PruneBackups()
		{
			foreach (var old in ListBackups().Skip(BackupsKept))
			{
				if (TryReadRegular(old, out var bytes))
				{
					var plain = MembersCrypto.OpenFileWithKey(bytes, dataKey);
					if (plain != null)
					{
						int version = MembersExport.ReadSchemaVersion(plain);
						CryptographicOperations.ZeroMemory(plain);
						if (version > MembersData.CurrentSchemaVersion)
						{
							continue;
						}
					}
				}

				TryDelete(old);
			}
		}

		/// <summary>
		/// Writes a temporary file next to <paramref name="path"/>, flushes it to disk, then renames it over
		/// <paramref name="path"/> (atomic on APFS and NTFS): a crash leaves either the old file or the new one.
		/// </summary>
		private void WriteAtomic(string path, byte[] bytes, string step)
		{
			Fault(step + "-write");
			var temp = Stage(path + ".tmp", bytes);
			try
			{
				Fault(step + "-rename");
				File.Move(temp, path, overwrite: true);
			}
			catch
			{
				TryDelete(temp);
				throw;
			}
		}

		/// <summary>
		/// Writes and flushes <paramref name="temp"/>; returns its name. Deletes it again if writing fails.
		/// </summary>
		private string Stage(string temp, byte[] bytes)
		{
			try
			{
				using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					stream.Write(bytes, 0, bytes.Length);
					stream.Flush(flushToDisk: true);
				}
			}
			catch
			{
				TryDelete(temp);
				throw;
			}

			return temp;
		}

		/// <summary>
		/// A name for moving <paramref name="path"/> aside that does not exist yet.
		/// </summary>
		private string ReplacedName(string path)
		{
			var stamp = clockUtc().ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
			var name = path + ReplacedMarker + stamp;
			for (int n = 2; Inspect(name) != EntryKind.Missing || File.Exists(name + ".tmp"); n++)
			{
				name = path + ReplacedMarker + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
			}

			return name;
		}
	}
}
