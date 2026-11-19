using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>What the Members tab shows instead of (or with) the list.</summary>
	public enum MembersSessionState
	{
		/// <summary>Not opened in this run of the app, or locked again. No member data and no typed text is shown
		/// or kept, except changes that could not be saved before locking (kept in memory, with the file's key while
		/// it is held, until they are saved, the next open, or the app closes after a warning) and the id of the
		/// selected member. With no members files (<see cref="MembersSessionCore.HasMembersFiles"/> false) the tab
		/// offers to set a passphrase and create the list; otherwise to enter the passphrase. A wrong passphrase
		/// stays here.</summary>
		Locked,

		/// <summary>The members file is open: the list is shown.</summary>
		Open,

		/// <summary>The file (and the previous copy) cannot be read.</summary>
		Unreadable,

		/// <summary>The file was saved by a newer version of the app.</summary>
		NewerVersion,

		/// <summary>Another copy of the app kept the members files busy for longer than a short wait.</summary>
		Busy,

		/// <summary>Another copy of the app changed the file since it was opened here: it must be loaded again.</summary>
		ChangedElsewhere,
	}

	/// <summary>The text fields of the member detail.</summary>
	public enum MemberField
	{
		Name,
		Contact,
		MetOn,
		Notes,

		/// <summary>The "Add note" text (added with the button, never saved by itself).</summary>
		NoteText,

		/// <summary>The "Add note" date (empty means today).</summary>
		NoteDate,

		/// <summary>The "add interest" field.</summary>
		InterestText,

		/// <summary>The "add machine" field.</summary>
		MachineText,

		/// <summary>The role (one short line, such as "Founder"), stored like the name when the field is left.</summary>
		Role,
	}

	/// <summary>
	/// Everything half typed on the Members tab, kept in memory (never on disk) so a rebuild of the window (Simple/
	/// Advanced, theme, a printer setting) loses nothing. The detail-field drafts belong to their member, so a draft
	/// that did not pass the checks stays with that member until it is fixed (shown again when the member is chosen).
	/// Locking clears every draft with typed text (the caller is told how many were not saved); closing clears all.
	/// </summary>
	public sealed class MembersDrafts
	{
		private readonly Dictionary<string, Dictionary<MemberField, string>> fields = new Dictionary<string, Dictionary<MemberField, string>>(StringComparer.Ordinal);

		/// <summary>Raised when a detail field draft is typed into (the app saves after a pause).</summary>
		public event EventHandler FieldChanged;

		public string QuickAddName { get; set; } = "";

		public string QuickAddNote { get; set; } = "";

		public string SearchText { get; set; } = "";

		public bool ShowArchived { get; set; }

		/// <summary>The list's importance filter: only members with at least this many stars (0: everyone). A view
		/// setting like <see cref="ShowArchived"/>: never stored in the members file, kept until the app closes.</summary>
		public int MinImportance { get; set; }

		/// <summary>The member shown in the detail panel, or null.</summary>
		public string SelectedMemberId { get; private set; }

		/// <summary>The members that have detail drafts.</summary>
		public IReadOnlyList<string> MembersWithDrafts => fields.Keys.ToList();

		public bool HasField(MemberField field) => HasField(SelectedMemberId, field);

		public bool HasField(string memberId, MemberField field) => memberId != null && fields.TryGetValue(memberId, out var map) && map.ContainsKey(field);

		/// <summary>The selected member's draft of <paramref name="field"/>, or null.</summary>
		public string Field(MemberField field) => Field(SelectedMemberId, field);

		public string Field(string memberId, MemberField field) => memberId != null && fields.TryGetValue(memberId, out var map) && map.TryGetValue(field, out var text) ? text : null;

		/// <summary>Records typing into a field of the selected member (other members are never typed into).</summary>
		public void SetField(string memberId, MemberField field, string text)
		{
			if (memberId == null || memberId != SelectedMemberId)
			{
				return;
			}

			if (!fields.TryGetValue(memberId, out var map))
			{
				fields[memberId] = map = new Dictionary<MemberField, string>();
			}

			map[field] = text ?? "";
			FieldChanged?.Invoke(this, EventArgs.Empty);
		}

		public void ClearField(MemberField field) => ClearField(SelectedMemberId, field);

		public void ClearField(string memberId, MemberField field)
		{
			if (memberId != null && fields.TryGetValue(memberId, out var map))
			{
				map.Remove(field);
				if (map.Count == 0)
				{
					fields.Remove(memberId);
				}
			}
		}

		/// <summary>Drops every draft of a member (the member was deleted on purpose).</summary>
		internal void ForgetMember(string memberId)
		{
			if (memberId != null)
			{
				fields.Remove(memberId);
			}
		}

		internal void Select(string memberId)
		{
			SelectedMemberId = memberId;
		}

		/// <summary>How many drafts of one member hold typed text.</summary>
		public int EnteredCountFor(string memberId) => memberId != null && fields.TryGetValue(memberId, out var map) ? map.Values.Count(v => !string.IsNullOrWhiteSpace(v)) : 0;

		/// <summary>How many drafts hold typed text that was not saved (the search text is not counted).</summary>
		public int EnteredCount => fields.Values.Sum(map => map.Values.Count(v => !string.IsNullOrWhiteSpace(v)))
			+ (string.IsNullOrWhiteSpace(QuickAddName) ? 0 : 1)
			+ (string.IsNullOrWhiteSpace(QuickAddNote) ? 0 : 1);

		/// <summary>On lock: clears every draft with typed text (and the search); keeps the selected id. Returns how
		/// many held typed text that was not saved.</summary>
		internal int ClearEntered()
		{
			int count = EnteredCount;
			QuickAddName = "";
			QuickAddNote = "";
			SearchText = "";
			fields.Clear();
			return count;
		}

		internal void Clear()
		{
			ClearEntered();
			ShowArchived = false;
			MinImportance = 0;
			SelectedMemberId = null;
		}
	}

	/// <summary>A line of the Storage notice: a fixed sentence, and for some a count ({0}) and file names ({1}).</summary>
	public sealed class MembersNotice
	{
		public MembersNotice(string sentence, int count = 0, string names = null)
		{
			Sentence = sentence;
			Count = count;
			Names = names ?? "";
		}

		/// <summary>The fixed sentence (what goes through the translation).</summary>
		public string Sentence { get; }

		public int Count { get; }

		/// <summary>File names only, never folders or contents.</summary>
		public string Names { get; }

		/// <summary>The sentence translated first, then the count and names put in: nothing variable is translated.</summary>
		public string Render(Func<string, string> localize)
		{
			var sentence = (localize ?? (s => s))(Sentence);
			return Count > 0 ? string.Format(sentence, Count, Names) : sentence;
		}

		public override bool Equals(object obj) => obj is MembersNotice other && other.Sentence == Sentence && other.Count == Count && other.Names == Names;

		public override int GetHashCode() => (Sentence ?? "").GetHashCode() ^ Count ^ Names.GetHashCode();
	}

	/// <summary>What happened at lock, for a notice that does not need the Members tab.</summary>
	public sealed class LockReport
	{
		/// <summary>The changes could not be saved before locking (they are held in memory).</summary>
		public bool NotSaved { get; set; }

		/// <summary>Drafts with typed text that were not saved and were cleared.</summary>
		public int DiscardedEntries { get; set; }

		public bool HasNews => NotSaved || DiscardedEntries > 0;

		public string Render(Func<string, string> localize)
		{
			localize ??= s => s;
			var lines = new List<string>();
			if (NotSaved)
			{
				lines.Add(localize(MembersSessionText.LockNotSaved));
			}

			if (DiscardedEntries > 0)
			{
				lines.Add(string.Format(localize(MembersSessionText.LockDiscardedEntries), DiscardedEntries));
			}

			return string.Join("\n\n", lines);
		}
	}

	/// <summary>The outcome of trying to save changes held since a lock.</summary>
	public enum HeldChanges
	{
		/// <summary>Nothing is held.</summary>
		None,

		/// <summary>They were saved now.</summary>
		Saved,

		/// <summary>Still held (busy or a write failure): try again later.</summary>
		StillHeld,

		/// <summary>The file changed elsewhere meanwhile, so they could not be saved and were dropped (with a notice).</summary>
		Lost,
	}

	/// <summary>
	/// EBMC Edition: the Members tab's state, without any GUI: it owns the vault and the store, creates the members
	/// list under a passphrase chosen for it (<see cref="CreateList"/>), opens it with that passphrase
	/// (<see cref="Unlock"/>), saves, changes the passphrase (<see cref="ChangePassphrase"/>), flushes and forgets
	/// the key on <see cref="Lock"/>, and keeps what was half typed. The members passphrase has nothing to do with
	/// the maintainer PIN. Nothing about it is stored: no hash, no verifier; the authenticated decryption of the file
	/// is the only check. Only the vault holds the file's key, until locking (or, when changes could not be saved
	/// before locking, until they are saved or the app closes).
	/// <para>Typed passphrases are used in Unicode NFC (<see cref="MembersRules.NormalizePassphrase"/>) and never
	/// trimmed. Entering one has no rule beyond non-empty; the rules of <see cref="MembersRules.CheckNewPassphrase"/>
	/// apply only when one is set or changed.</para>
	/// <para>Methods that act for the person return null when it worked (or when the outcome is shown by
	/// <see cref="State"/> and <see cref="Notices"/>), otherwise one fixed English sentence from
	/// <see cref="MembersSessionText"/> or <see cref="MembersRules"/> for the app to translate and show.</para>
	/// <para>The folder and clock are parameters, so all of this runs in a temporary folder in tests. No network, no
	/// logging, and no member text in any message: messages are the fixed sentences of <see cref="MembersSessionText"/>
	/// with counts and file names put in after translation.</para>
	/// </summary>
	public sealed class MembersSessionCore
	{
		/// <summary>How often a Busy answer is tried again where the caller must wait (open, lock, passphrase change,
		/// restore), and the wait between tries: at most about 0.6 s in all.</summary>
		public const int BusyAttempts = 5;

		public const int BusyWaitMs = 150;

		/// <summary>Background saves that found the file busy in a row before the busy notice is shown.</summary>
		public const int BusySavesBeforeNotice = 3;

		/// <summary>The most rows the list shows; a search narrows it.</summary>
		public const int MaxListRows = 200;

		private static readonly MemberField[] StoredFields = { MemberField.Name, MemberField.Role, MemberField.Contact, MemberField.MetOn, MemberField.Notes };

		private readonly Func<DateTime> clockUtc;
		private readonly Func<DateTime> today;
		private readonly Action<int> wait;
		private readonly Func<string> newId;
		private readonly List<MembersNotice> notices = new List<MembersNotice>();
		private readonly Dictionary<string, ValidationResult> fieldProblems = new Dictionary<string, ValidationResult>(StringComparer.Ordinal);

		/// <summary>The SHA-256 of the data as last opened or saved (to tell whether the file still holds it).</summary>
		private byte[] lastSavedHash;

		/// <summary>Changes made here since the last save (counted for the notices; never shown as text).</summary>
		private int unsavedChanges;

		/// <summary>Changes that could not be saved before locking (memory only), and what they were based on.</summary>
		private MembersData heldAtLock;
		private int heldAtLockChanges;
		private byte[] heldAtLockBase;

		/// <param name="folder">The members folder (the app passes its per-user data folder).</param>
		/// <param name="clockUtc">The current UTC time.</param>
		/// <param name="wait">Waits the given milliseconds between Busy retries (tests pass a no-op).</param>
		/// <param name="newId">Makes ids (tests pass a counter); the store's default otherwise.</param>
		/// <param name="today">The local date; the local date of <paramref name="clockUtc"/> otherwise.</param>
		public MembersSessionCore(string folder, Func<DateTime> clockUtc, Action<int> wait = null, Func<string> newId = null, Func<DateTime> today = null)
		{
			this.clockUtc = clockUtc ?? (() => DateTime.UtcNow);
			this.today = today ?? (() => this.clockUtc().ToLocalTime().Date);
			this.wait = wait ?? (ms => System.Threading.Thread.Sleep(ms));
			this.newId = newId;
			Vault = new MembersVault(folder, this.clockUtc);
			Drafts.FieldChanged += (s, e) =>
			{
				if (State == MembersSessionState.Open)
				{
					SaveWanted?.Invoke(this, EventArgs.Empty);
				}
			};
		}

		/// <summary>Raised after anything shown on the tab changed state (opened, locked, a state view, a notice, a
		/// member deleted).</summary>
		public event EventHandler StateChanged;

		/// <summary>Raised when there are changes (or typed fields) to save: the app saves after a short pause.</summary>
		public event EventHandler SaveWanted;

		public MembersVault Vault { get; }

		public MembersSessionState State { get; private set; } = MembersSessionState.Locked;

		/// <summary>The data while <see cref="State"/> is Open, otherwise null.</summary>
		public MembersStore Store { get; private set; }

		public MembersDrafts Drafts { get; } = new MembersDrafts();

		/// <summary>True from an open with the passphrase (<see cref="Unlock"/> or <see cref="CreateList"/> that got
		/// past the passphrase: open, or a state view such as Unreadable or Busy) until <see cref="Lock"/>.</summary>
		public bool Unlocked { get; private set; }

		/// <summary>Counts the locks (<see cref="Lock"/>): a question asked before a lock is not acted on after it.</summary>
		public int LockGeneration { get; private set; }

		/// <summary>Changes when the list or the detail must be shown again for a reason other than the page's own
		/// action (a member deleted from a confirmation box).</summary>
		public int Revision { get; private set; }

		/// <summary>There are changes not yet saved.</summary>
		public bool Dirty { get; private set; }

		/// <summary>Changes from before the last lock are held in memory, not yet saved.</summary>
		public bool HoldsChangesFromLock => heldAtLock != null;

		/// <summary>Anything that would be lost if the app closed now: unsaved changes, changes held since a lock, or
		/// typed text not saved.</summary>
		public bool HasUnsavedWork => (State == MembersSessionState.Open && Dirty) || heldAtLock != null || Drafts.EnteredCount > 0;

		/// <summary>The last open found daily backups that could be restored.</summary>
		public bool BackupsAvailable { get; private set; }

		/// <summary>The last open stopped at an unfinished passphrase (or older PIN) change: opening with the new
		/// passphrase finishes it.</summary>
		public bool PinChangeInterrupted { get; private set; }

		/// <summary>The file was opened from the previous copy and has not been saved since.</summary>
		public bool OpenedFromPrevious { get; private set; }

		/// <summary>A save after opening from the previous copy was refused: offer to restore the previous copy.</summary>
		public bool OfferRestorePrevious { get; private set; }

		/// <summary>The last save problem (a fixed sentence), or null.</summary>
		public string SaveProblem { get; private set; }

		/// <summary>The last save was refused because another copy of the app was writing.</summary>
		public bool LastSaveBusy { get; private set; }

		public int ConsecutiveBusySaves { get; private set; }

		/// <summary>The Storage notice, newest last.</summary>
		public IReadOnlyList<MembersNotice> Notices => notices;

		/// <summary>The Storage notice in English (as <see cref="MembersNotice.Render"/> without translation).</summary>
		public IReadOnlyList<string> StorageNotice => notices.Select(n => n.Render(null)).ToList();

		public bool HasPreviousCopy => File.Exists(Vault.PreviousPath);

		public string NewestBackup => Vault.ListBackups().FirstOrDefault();

		public DateTime Today => today();

		/// <summary>The problems of the typed fields of <paramref name="memberId"/> that did not pass the checks.</summary>
		public ValidationResult ProblemsFor(string memberId) => memberId != null && fieldProblems.TryGetValue(memberId, out var problems) ? problems : ValidationResult.Valid;

		// --- create, open, lock, passphrase change ---

		/// <summary>
		/// Whether any members file exists that an open or a restore could read: the main file, the previous copy, an
		/// unfinished passphrase change or a daily backup. False: the tab offers <see cref="CreateList"/>; true: it
		/// asks for the passphrase (<see cref="Unlock"/>). Files moved aside are not counted (the vault never opens
		/// them by itself).
		/// </summary>
		public bool HasMembersFiles => File.Exists(Vault.FilePath) || Directory.Exists(Vault.FilePath)
			|| File.Exists(Vault.PreviousPath) || Directory.Exists(Vault.PreviousPath)
			|| File.Exists(Vault.JournalPath) || Directory.Exists(Vault.JournalPath)
			|| Vault.ListBackups().Count > 0;

		/// <summary>
		/// Creates a new, empty members list under <paramref name="passphrase"/> and opens it; the file is written at
		/// once. The passphrase must pass <see cref="MembersRules.CheckNewPassphrase"/> with <paramref name="repeat"/>.
		/// The only way a list is ever created. Refused (nothing written) while any members file exists, or the list
		/// is open.
		/// <para>Returns null when the list is open (state Open), otherwise a fixed sentence: a
		/// <see cref="MembersRules"/> passphrase problem (the first one), <see cref="MembersSessionText.ListAlreadyExists"/>,
		/// <see cref="MembersSessionText.StateBusy"/> or <see cref="MembersSessionText.CouldNotOpen"/>; the state stays
		/// Locked then.</para>
		/// </summary>
		public string CreateList(string passphrase, string repeat)
		{
			var check = MembersRules.CheckNewPassphrase(passphrase, repeat);
			if (!check.IsValid)
			{
				return check.Problems[0];
			}

			if (State == MembersSessionState.Open || HasMembersFiles)
			{
				return MembersSessionText.ListAlreadyExists;
			}

			var result = OpenVault(MembersRules.NormalizePassphrase(passphrase));
			if (result == null)
			{
				return MembersSessionText.CouldNotOpen;
			}

			if (!result.IsOpen)
			{
				// another window made a list meanwhile under another passphrase, or the folder is busy or unusable
				Vault.Close();
				State = MembersSessionState.Locked;
				Changed();
				switch (result.Status)
				{
					case OpenStatus.WrongPin:
						return MembersSessionText.ListAlreadyExists;
					case OpenStatus.Busy:
						return MembersSessionText.StateBusy;
					default:
						return MembersSessionText.CouldNotOpen;
				}
			}

			Unlocked = true;
			Apply(result);
			if (result.Status == OpenStatus.NewEmpty)
			{
				// written now, so the file exists (and the passphrase is in force) before anything is typed
				Dirty = true;
				SaveNow();
			}

			return null;
		}

		/// <summary>
		/// Opens the existing members list with <paramref name="passphrase"/> (as typed; no rule beyond non-empty).
		/// Called from the Enter passphrase view and the state views (Load again, Try again). Does nothing while open.
		/// Never creates a list: with no members files it refuses with <see cref="MembersSessionText.NoListYet"/>.
		/// <para>A wrong passphrase changes nothing on disk and leaves <see cref="State"/> as it was (Locked from the
		/// Enter view); it returns <see cref="MembersSessionText.WrongPassphrase"/>, or
		/// <see cref="MembersSessionText.WrongPassphraseInterrupted"/> when a passphrase change did not finish. Empty:
		/// <see cref="MembersSessionText.PassphraseRequired"/>. Otherwise null, with the state the file gave: Open, or
		/// a state view (Unreadable, NewerVersion, Busy, ChangedElsewhere), or Locked with a CouldNotOpen notice after
		/// an unexpected failure.</para>
		/// </summary>
		public string Unlock(string passphrase)
		{
			if (State == MembersSessionState.Open)
			{
				return null;
			}

			if (string.IsNullOrEmpty(passphrase))
			{
				return MembersSessionText.PassphraseRequired;
			}

			if (!HasMembersFiles)
			{
				return MembersSessionText.NoListYet;
			}

			return OpenExisting(MembersRules.NormalizePassphrase(passphrase));
		}

		/// <summary>
		/// Opens the file again from disk with <paramref name="passphrase"/> (after another copy of the app changed it,
		/// or to retry), also while open. Unsaved changes here are dropped (with a notice). Only after a successful
		/// open (<see cref="Unlocked"/>); returns as <see cref="Unlock"/>. While open, a passphrase that does not open
		/// the file's key changes nothing.
		/// </summary>
		public string Reload(string passphrase)
		{
			if (!Unlocked)
			{
				return null;
			}

			if (string.IsNullOrEmpty(passphrase))
			{
				return MembersSessionText.PassphraseRequired;
			}

			var pass = MembersRules.NormalizePassphrase(passphrase);
			if (State == MembersSessionState.Open && Vault.IsOpen && !Vault.KeyOpensWith(pass))
			{
				return MembersSessionText.WrongPassphrase;
			}

			return OpenExisting(pass);
		}

		private string OpenExisting(string pass)
		{
			if (heldAtLock != null && Vault.IsOpen && !Vault.KeyOpensWith(pass))
			{
				// changes held since the lock, under a key this passphrase does not open: refused before the vault is
				// asked, so the key stays held (TrySaveHeld and SaveBeforeExit can still save them) and nothing is written
				return MembersSessionText.WrongPassphrase;
			}

			var result = OpenVault(pass);
			if (result == null)
			{
				return null;
			}

			if (result.Status == OpenStatus.NewEmpty)
			{
				// the files went away meanwhile: only CreateList makes a list
				Vault.Close();
				DropData();
				State = MembersSessionState.Locked;
				Unlocked = false;
				Changed();
				return MembersSessionText.NoListYet;
			}

			if (result.Status == OpenStatus.WrongPin)
			{
				// the vault is closed and nothing was written; the state view stays as it was
				BackupsAvailable = result.BackupsAvailable;
				PinChangeInterrupted = result.PinChangeInterrupted;
				AddNotices(result);
				Changed();
				return PinChangeInterrupted ? MembersSessionText.WrongPassphraseInterrupted : MembersSessionText.WrongPassphrase;
			}

			Unlocked = true;
			Apply(result);
			return null;
		}

		/// <summary>
		/// Changes the members passphrase while the list is open: <paramref name="next"/> must pass
		/// <see cref="MembersRules.CheckNewPassphrase"/> with <paramref name="repeat"/>, and <paramref name="current"/>
		/// must open the file's key. What is pending is saved first, then the file (and the previous copy and backups
		/// under the same key) move to the new passphrase. The list stays open.
		/// <para>Returns null when changed (a <see cref="MembersSessionText.PassphraseChanged"/> notice is added),
		/// otherwise a fixed sentence: a <see cref="MembersRules"/> passphrase problem (the first one),
		/// <see cref="MembersSessionText.PassphraseRefusedNotOpen"/>, <see cref="MembersSessionText.PassphraseRequired"/>,
		/// <see cref="MembersSessionText.CurrentPassphraseWrong"/>, or one of the PassphraseRefused sentences (Busy,
		/// ChangedElsewhere, which also moves the state to ChangedElsewhere, Unsaved, Failed). When refused, the old
		/// passphrase still opens the file.</para>
		/// </summary>
		public string ChangePassphrase(string current, string next, string repeat)
		{
			if (State != MembersSessionState.Open || Store == null || !Vault.IsOpen)
			{
				return MembersSessionText.PassphraseRefusedNotOpen;
			}

			if (string.IsNullOrEmpty(current))
			{
				return MembersSessionText.PassphraseRequired;
			}

			var check = MembersRules.CheckNewPassphrase(next, repeat);
			if (!check.IsValid)
			{
				return check.Problems[0];
			}

			if (!Vault.KeyOpensWith(MembersRules.NormalizePassphrase(current)))
			{
				return MembersSessionText.CurrentPassphraseWrong;
			}

			var problem = MoveToNewPin(MembersRules.NormalizePassphrase(next));
			if (problem == null)
			{
				AddNotice(new MembersNotice(MembersSessionText.PassphraseChanged));
				Changed();
			}

			return problem;
		}

		/// <summary>The open list: saves what is pending, then moves the file to the new passphrase. A failed save or
		/// move is tried once more. Null when moved, otherwise a fixed PassphraseRefused sentence.</summary>
		private string MoveToNewPin(string newPin)
		{
			SaveNow();
			if (State == MembersSessionState.Open && Dirty && !LastSaveBusy)
			{
				// a single failure may be passing: once more
				SaveNow();
			}

			if (State != MembersSessionState.Open)
			{
				return MembersSessionText.PassphraseRefusedChangedElsewhere;
			}

			if (Dirty)
			{
				return LastSaveBusy ? MembersSessionText.PassphraseRefusedBusy : MembersSessionText.PassphraseRefusedUnsaved;
			}

			for (int attempt = 0; ; attempt++)
			{
				try
				{
					var result = Retry(() => Vault.Rewrap(newPin), r => r.Status == SaveStatus.Busy);
					if (result.Status == SaveStatus.SaveFirst)
					{
						// opened from the previous copy and not saved yet: save it as the main file, then change the key
						var saved = Retry(() => Vault.Save(Store.Snapshot()), r => r.Status == SaveStatus.Busy);
						if (!saved.Ok)
						{
							return RefusalFor(saved.Status);
						}

						AfterSaved(saved);
						result = Retry(() => Vault.Rewrap(newPin), r => r.Status == SaveStatus.Busy);
					}

					if (!result.Ok)
					{
						return RefusalFor(result.Status);
					}

					AddNotices(result);
					if (result.PinChangePending)
					{
						AddNotice(new MembersNotice(MembersSessionText.PinChangePending));
					}

					Changed();
					return null;
				}
				catch (MembersFileException)
				{
					if (attempt >= 1)
					{
						return MembersSessionText.PassphraseRefusedFailed;
					}
				}
			}
		}

		/// <summary>
		/// Saves what was typed and changed (waiting at most about 0.6 s if another window is writing), then forgets
		/// the data and every draft with typed text, and the file's key. The lock always happens: the state is Locked
		/// and <see cref="LockGeneration"/> goes up. If the save fails, the changes are held in memory with the file's
		/// key (never written anywhere else) and are saved by <see cref="TrySaveHeld"/>, at the next open, or before
		/// the app closes; the report says so.
		/// </summary>
		public LockReport Lock()
		{
			var report = new LockReport();
			bool keepKey = false;
			if (State == MembersSessionState.Open)
			{
				SaveNow();
				if (Dirty && Store != null)
				{
					heldAtLock = Store.Snapshot();
					heldAtLockBase = lastSavedHash;
					heldAtLockChanges = Math.Max(1, unsavedChanges);
					keepKey = true;
					report.NotSaved = true;
				}
			}

			keepKey |= heldAtLock != null && Vault.IsOpen;
			report.DiscardedEntries = Drafts.ClearEntered();
			fieldProblems.Clear();

			// held above when not saved, and told in the lock report
			DropData(lossAccountedFor: true);
			if (!keepKey)
			{
				Vault.Close();
			}

			ResetFlags();
			State = MembersSessionState.Locked;
			Unlocked = false;
			LockGeneration++;
			Changed();
			return report;
		}

		/// <summary>
		/// While locked: tries to save the changes held since the lock (once, or for a moment when
		/// <paramref name="waitIfBusy"/>). The file's key is forgotten once they are saved or lost.
		/// </summary>
		public HeldChanges TrySaveHeld(bool waitIfBusy)
		{
			if (heldAtLock == null)
			{
				return HeldChanges.None;
			}

			if (!Vault.IsOpen || State == MembersSessionState.Open)
			{
				// after a new open the held changes are dealt with by the open
				return HeldChanges.StillHeld;
			}

			SaveResult result;
			try
			{
				result = waitIfBusy ? Retry(() => Vault.Save(heldAtLock), r => r.Status == SaveStatus.Busy) : Vault.Save(heldAtLock);
			}
			catch (MembersFileException)
			{
				return HeldChanges.StillHeld;
			}

			switch (result.Status)
			{
				case SaveStatus.Saved:
					AddNotices(result);
					ForgetHeld();
					Changed();
					return HeldChanges.Saved;

				case SaveStatus.Busy:
					return HeldChanges.StillHeld;

				default:
					// another window changed the file meanwhile: nothing was overwritten
					ForgetHeld();
					AddNotice(new MembersNotice(MembersSessionText.LostBeforeLock));
					Changed();
					return HeldChanges.Lost;
			}
		}

		private void ForgetHeld()
		{
			heldAtLock = null;
			heldAtLockBase = null;
			if (State != MembersSessionState.Open)
			{
				Vault.Close();
			}
		}

		/// <summary>
		/// Before the app closes: commits and saves once more (open list, or changes held since a lock). True when
		/// something would still be lost (see <see cref="HasUnsavedWork"/>): the app warns first.
		/// </summary>
		public bool SaveBeforeExit()
		{
			if (State == MembersSessionState.Open)
			{
				SaveNow();
			}

			TrySaveHeld(waitIfBusy: true);
			return HasUnsavedWork;
		}

		/// <summary>When the app closes: saves, forgets the key, the data and every draft.</summary>
		public void CloseForExit()
		{
			SaveBeforeExit();

			// the app asked before closing when anything would be lost
			DropData(lossAccountedFor: true);
			heldAtLock = null;
			heldAtLockBase = null;
			Vault.Close();
			ResetFlags();
			State = MembersSessionState.Locked;
			Unlocked = false;
			Drafts.Clear();
			fieldProblems.Clear();
			notices.Clear();
		}

		// --- restore ---

		/// <summary>
		/// Brings back the newest daily backup. <paramref name="pin"/> is the members passphrase (as typed, not empty);
		/// <paramref name="filePin"/> the passphrase (or older PIN) the copy was saved with when different (may be
		/// empty). The restored list ends up under <paramref name="pin"/>, which must open the file's key when the list
		/// is open, or the main file or previous copy when either can be read. Returns null when restored (state
		/// Open), otherwise a fixed sentence: <see cref="MembersSessionText.NothingToRestore"/>,
		/// <see cref="MembersSessionText.PassphraseRequired"/>, <see cref="MembersSessionText.WrongPassphrase"/> (open
		/// list, nothing changed), <see cref="MembersSessionText.RestoreWrongPin"/>, RestoreFailed, StateBusy,
		/// RestoreChangedElsewhere, StateNewerVersion or RestoreUnreadable; the state is then unchanged.
		/// </summary>
		public string RestoreNewestBackup(string pin, string filePin)
		{
			var backup = NewestBackup;
			return backup == null ? MembersSessionText.NothingToRestore : RestoreFile(backup, pin, filePin);
		}

		/// <summary>Brings back the previous copy (members.ebmc.prev); as <see cref="RestoreNewestBackup"/>.</summary>
		public string RestorePrevious(string pin, string filePin)
		{
			return HasPreviousCopy ? RestoreFile(Vault.PreviousPath, pin, filePin) : MembersSessionText.NothingToRestore;
		}

		private string RestoreFile(string file, string pin, string filePin)
		{
			if (!Unlocked)
			{
				return null;
			}

			if (string.IsNullOrEmpty(pin))
			{
				return MembersSessionText.PassphraseRequired;
			}

			var pass = MembersRules.NormalizePassphrase(pin);
			var filePass = string.IsNullOrEmpty(filePin) ? pass : MembersRules.NormalizePassphrase(filePin);

			// with the vault open, the typed passphrase must be the one in force: checked before anything is written
			if (Vault.IsOpen && !Vault.KeyOpensWith(pass))
			{
				return MembersSessionText.WrongPassphrase;
			}

			if (State == MembersSessionState.Open)
			{
				SaveNow();
			}

			OpenResult result;
			try
			{
				result = Retry(
					() => Vault.Restore(file, filePass, pass, null),
					r => r.Status == OpenStatus.Busy);
			}
			catch (MembersFileException)
			{
				return MembersSessionText.RestoreFailed;
			}

			if (result.Status == OpenStatus.Restored)
			{
				Apply(result);
				return null;
			}

			BackupsAvailable = result.BackupsAvailable || BackupsAvailable;
			AddNotices(result);
			Changed();
			switch (result.Status)
			{
				case OpenStatus.WrongPin:
					return MembersSessionText.RestoreWrongPin;
				case OpenStatus.Busy:
					return MembersSessionText.StateBusy;
				case OpenStatus.ChangedElsewhere:
					return MembersSessionText.RestoreChangedElsewhere;
				case OpenStatus.NewerVersion:
					return MembersSessionText.StateNewerVersion;
				default:
					return MembersSessionText.RestoreUnreadable;
			}
		}

		// --- saving ---

		/// <summary>
		/// Commits the typed fields and saves now if anything changed. With <paramref name="waitIfBusy"/> (lock, passphrase
		/// change, closing) a busy file is tried again for at most about 0.6 s; without it (the save after a pause) it
		/// is tried once and the caller tries again later (<see cref="LastSaveBusy"/>); the busy notice shows after
		/// <see cref="BusySavesBeforeNotice"/> such tries. A file changed elsewhere switches to the
		/// <see cref="MembersSessionState.ChangedElsewhere"/> view.
		/// </summary>
		public void SaveNow(bool waitIfBusy = true)
		{
			LastSaveBusy = false;
			if (State != MembersSessionState.Open || Store == null)
			{
				return;
			}

			var problemsBefore = ProblemsFor(Drafts.SelectedMemberId).Message(null);
			CommitDetailDrafts();
			if (!Dirty)
			{
				if (ProblemsFor(Drafts.SelectedMemberId).Message(null) != problemsBefore)
				{
					// nothing to save, but the typed fields' problems changed: the page shows them
					Changed();
				}

				return;
			}

			SaveResult result;
			try
			{
				result = waitIfBusy
					? Retry(() => Vault.Save(Store.Snapshot()), r => r.Status == SaveStatus.Busy)
					: Vault.Save(Store.Snapshot());
			}
			catch (MembersFileException)
			{
				SetSaveProblem(MembersSessionText.SaveFailed);
				return;
			}

			switch (result.Status)
			{
				case SaveStatus.Saved:
					AfterSaved(result);
					Changed();
					break;

				case SaveStatus.Busy:
					// kept; the caller tries again later
					LastSaveBusy = true;
					ConsecutiveBusySaves++;
					if (waitIfBusy || ConsecutiveBusySaves >= BusySavesBeforeNotice)
					{
						SetSaveProblem(MembersSessionText.SaveBusy);
					}

					break;

				case SaveStatus.ChangedElsewhere:
					EnterChangedElsewhere();
					break;

				default:
					// not open: nothing to save into
					SetSaveProblem(MembersSessionText.SaveFailed);
					break;
			}
		}

		private void AfterSaved(SaveResult result)
		{
			Dirty = false;
			unsavedChanges = 0;
			SaveProblem = null;
			ConsecutiveBusySaves = 0;
			OpenedFromPrevious = false;
			lastSavedHash = Hash(Store.Snapshot());
			AddNotices(result);
			if (result.PinChangePending)
			{
				AddNotice(new MembersNotice(MembersSessionText.PinChangePending));
			}
		}

		private void SetSaveProblem(string problem)
		{
			SaveProblem = problem;
			Changed();
		}

		// --- the detail fields ---

		/// <summary>Shows <paramref name="memberId"/> in the detail panel. What was typed for the previous member is
		/// stored first where it passes the checks; what does not stays with that member (shown again when chosen).</summary>
		public void Select(string memberId)
		{
			if (memberId == Drafts.SelectedMemberId)
			{
				return;
			}

			CommitDetailDrafts();
			Drafts.Select(memberId);
		}

		/// <summary>The text a detail field shows: what was typed (not yet stored), else the stored value.</summary>
		public string FieldText(Member member, MemberField field)
		{
			if (member != null && Drafts.HasField(member.Id, field))
			{
				return Drafts.Field(member.Id, field);
			}

			return member == null ? "" : StoredValue(member, field);
		}

		/// <summary>
		/// Stores the typed name, role, contact, date met and notes of every member with drafts, each field on its own and
		/// only if it differs from what is stored. A field that does not pass the checks stays typed (not stored) with
		/// its problem (<see cref="ProblemsFor"/>); the others are stored. Returns the selected member's problems.
		/// </summary>
		public ValidationResult CommitDetailDrafts()
		{
			if (Store == null)
			{
				return ValidationResult.Valid;
			}

			foreach (var id in Drafts.MembersWithDrafts)
			{
				var problems = new ValidationResult();
				if (Store.FindMember(id) != null)
				{
					foreach (var field in StoredFields)
					{
						problems.AddRange(CommitField(id, field));
					}
				}

				if (problems.IsValid)
				{
					fieldProblems.Remove(id);
				}
				else
				{
					fieldProblems[id] = problems;
				}
			}

			return ProblemsFor(Drafts.SelectedMemberId);
		}

		private ValidationResult CommitField(string id, MemberField field)
		{
			if (!Drafts.HasField(id, field))
			{
				return ValidationResult.Valid;
			}

			var member = Store.FindMember(id);
			var typed = Drafts.Field(id, field);
			string value = typed;
			if (field == MemberField.MetOn)
			{
				var trimmed = typed.Trim();
				if (trimmed.Length == 0)
				{
					value = "";
				}
				else if (!MembersRules.TryParseDate(trimmed, today(), out value))
				{
					return ValidationResult.Invalid(MembersRules.DateInvalid);
				}
			}

			var stored = StoredValue(member, field);
			bool same = field == MemberField.Notes || field == MemberField.MetOn
				? value == stored
				: field == MemberField.Role
					? value.Trim() == stored
					: MembersRules.CollapseWhitespace(value) == MembersRules.CollapseWhitespace(stored);
			if (same)
			{
				Drafts.ClearField(id, field);
				return ValidationResult.Valid;
			}

			var result = Store.UpdateMember(id, m =>
			{
				switch (field)
				{
					case MemberField.Name:
						m.Name = value;
						break;
					case MemberField.Role:
						m.Role = value;
						break;
					case MemberField.Contact:
						m.Contact = value;
						break;
					case MemberField.MetOn:
						m.MetOn = value;
						break;
					case MemberField.Notes:
						m.Notes = value;
						break;
				}
			});

			if (result.IsValid)
			{
				Drafts.ClearField(id, field);
			}

			return result;
		}

		private static string StoredValue(Member member, MemberField field)
		{
			switch (field)
			{
				case MemberField.Name:
					return member.Name;
				case MemberField.Role:
					return member.Role;
				case MemberField.Contact:
					return member.Contact;
				case MemberField.MetOn:
					return member.MetOn;
				case MemberField.Notes:
					return member.Notes;
				default:
					return "";
			}
		}

		// --- quick add, notes, tags ---

		/// <summary>Adds a member from the quick-add bar. On success the bar's drafts are cleared and the new member
		/// is selected.</summary>
		public Result<Member> QuickAdd(string name, string firstNote)
		{
			if (Store == null)
			{
				return Result<Member>.Failure(MembersRules.NotFound);
			}

			var result = Store.QuickAdd(name, firstNote, today());
			if (result.Ok)
			{
				Drafts.QuickAddName = "";
				Drafts.QuickAddNote = "";
				Select(result.Value.Id);
			}

			return result;
		}

		/// <summary>Members already in the list who may be the same person (names only, for an on-screen hint).</summary>
		public IReadOnlyList<string> PossibleDuplicateNames(string name, int max = 2)
		{
			if (Store == null || string.IsNullOrWhiteSpace(name))
			{
				return Array.Empty<string>();
			}

			return MembersRules.PossibleDuplicates(Store.Snapshot(), name).Select(m => m.Name).Take(max).ToList();
		}

		/// <summary>Adds the typed dated note to the selected member; the note drafts are cleared on success.</summary>
		public Result<LogEntry> AddNote(string dateText, string text)
		{
			var id = Drafts.SelectedMemberId;
			if (Store == null || id == null)
			{
				return Result<LogEntry>.Failure(MembersRules.NotFound);
			}

			var result = Store.AddLog(id, dateText, text, today());
			if (result.Ok)
			{
				Drafts.ClearField(id, MemberField.NoteText);
				Drafts.ClearField(id, MemberField.NoteDate);
			}

			return result;
		}

		/// <summary>Adds the comma-separated tags to the selected member's interests or machines.</summary>
		public ValidationResult AddTags(MemberField field, string commaText)
		{
			var id = Drafts.SelectedMemberId;
			if (Store == null || id == null || (field != MemberField.InterestText && field != MemberField.MachineText))
			{
				return ValidationResult.Invalid(MembersRules.NotFound);
			}

			if (!MembersRules.TryParseTags(commaText, out var tags, out var validation))
			{
				return validation;
			}

			var member = Store.FindMember(id);
			var current = field == MemberField.InterestText ? member?.Interests : member?.Machines;
			if (current == null)
			{
				return ValidationResult.Invalid(MembersRules.NotFound);
			}

			var merged = MembersRules.NormalizeTags(current.Concat(tags));
			if (merged.SequenceEqual(current))
			{
				Drafts.ClearField(id, field);
				return ValidationResult.Valid;
			}

			var result = Store.UpdateMember(id, m =>
			{
				if (field == MemberField.InterestText)
				{
					m.Interests = merged;
				}
				else
				{
					m.Machines = merged;
				}
			});

			if (result.IsValid)
			{
				Drafts.ClearField(id, field);
			}

			return result;
		}

		/// <summary>Removes one tag from the selected member's interests or machines.</summary>
		public ValidationResult RemoveTag(MemberField field, string tag)
		{
			var id = Drafts.SelectedMemberId;
			if (Store == null || id == null)
			{
				return ValidationResult.Invalid(MembersRules.NotFound);
			}

			return Store.UpdateMember(id, m =>
			{
				if (field == MemberField.InterestText)
				{
					m.Interests.Remove(tag);
				}
				else if (field == MemberField.MachineText)
				{
					m.Machines.Remove(tag);
				}
			});
		}

		/// <summary>
		/// Sets the selected member's importance (0 to <see cref="MembersRules.MaxImportance"/>), an ordinary edit (the
		/// change time moves). Typed fields are stored first. Picking the level already set changes nothing.
		/// </summary>
		public ValidationResult SetImportance(int importance)
		{
			var id = Drafts.SelectedMemberId;
			if (Store == null || id == null)
			{
				return ValidationResult.Invalid(MembersRules.NotFound);
			}

			var check = MembersRules.ValidateImportance(importance);
			if (!check.IsValid)
			{
				return check;
			}

			CommitDetailDrafts();
			var member = Store.FindMember(id);
			if (member == null)
			{
				return ValidationResult.Invalid(MembersRules.NotFound);
			}

			if (member.Importance == importance)
			{
				return ValidationResult.Valid;
			}

			return Store.UpdateMember(id, m => m.Importance = importance);
		}

		public bool SetArchived(bool archived)
		{
			var id = Drafts.SelectedMemberId;
			if (Store == null || id == null)
			{
				return false;
			}

			CommitDetailDrafts();
			return Store.ArchiveMember(id, archived);
		}

		/// <summary>
		/// Deletes <paramref name="memberId"/> for good after the screen asked. Refused when the lock was cycled since
		/// the question was asked (<paramref name="askedInLockGeneration"/>) or the list is not open. The member's
		/// drafts go too; the change event tells whichever page is showing.
		/// </summary>
		public bool Delete(string memberId, int askedInLockGeneration)
		{
			if (Store == null
				|| memberId == null
				|| !Unlocked
				|| State != MembersSessionState.Open
				|| askedInLockGeneration != LockGeneration)
			{
				return false;
			}

			if (Drafts.SelectedMemberId == memberId)
			{
				Drafts.Select(null);
			}

			Drafts.ForgetMember(memberId);
			fieldProblems.Remove(memberId);
			var deleted = Store.DeleteMember(memberId);
			Revision++;
			Changed();
			return deleted;
		}

		/// <summary>The members to list for the search text, Show archived and the importance filter: best first, at most
		/// <see cref="MaxListRows"/>.</summary>
		public IReadOnlyList<Member> ListMembers(out int total)
		{
			total = 0;
			if (Store == null)
			{
				return Array.Empty<Member>();
			}

			var ids = Store.SearchMemberIds(Drafts.SearchText ?? "", Drafts.ShowArchived, Drafts.MinImportance);
			total = ids.Count;
			return ids.Take(MaxListRows).Select(Store.FindMember).Where(m => m != null).ToList();
		}

		/// <summary>
		/// Why the list is empty, as a fixed sentence from <see cref="MembersSessionText"/>: no members at all; only
		/// archived members match the search and filter while Show archived is off; the star filter hides the matches
		/// (with or without a search); or nothing matches the search.
		/// </summary>
		public string EmptyListMessage()
		{
			if (Store == null || Store.MemberCount == 0)
			{
				return MembersSessionText.ListEmptyNoMembers;
			}

			var search = Drafts.SearchText ?? "";
			bool searching = !string.IsNullOrWhiteSpace(search);
			int minImportance = Drafts.MinImportance;
			// archived members that meet both the search and the filter: Show archived is what hides them
			if ((searching || minImportance > 0) && !Drafts.ShowArchived && Store.SearchMemberIds(search, true, minImportance).Count > 0)
			{
				return MembersSessionText.ListEmptyOnlyArchived;
			}

			if (minImportance > 0 && Store.SearchMemberIds(search, Drafts.ShowArchived, 0).Count > 0)
			{
				return searching ? MembersSessionText.ListEmptyMatchesWithFewerStars : MembersSessionText.ListEmptyTooFewStars;
			}

			if (searching)
			{
				return MembersSessionText.ListEmptyNoMatch;
			}

			return minImportance > 0 ? MembersSessionText.ListEmptyTooFewStars : MembersSessionText.ListEmptyNoMembers;
		}

		public void DismissNotice()
		{
			notices.Clear();
			Changed();
		}

		/// <summary>
		/// The Storage notice lines for a result: counts and file names only (never paths, never contents).
		/// </summary>
		public static List<MembersNotice> StorageNotices(IEnumerable<string> setAside, IEnumerable<string> notReKeyed, IEnumerable<string> leftUnderOtherPins, IEnumerable<string> skipped, bool backupSkipped, bool previousSkipped)
		{
			var lines = new List<MembersNotice>();
			void Add(string sentence, IEnumerable<string> files)
			{
				var names = (files ?? Enumerable.Empty<string>()).Select(f => Path.GetFileName(f ?? "")).Where(n => n.Length > 0).Distinct().ToList();
				if (names.Count == 0)
				{
					return;
				}

				var shown = string.Join(", ", names.Take(4));
				if (names.Count > 4)
				{
					shown += ", +" + (names.Count - 4);
				}

				lines.Add(new MembersNotice(sentence, names.Count, shown));
			}

			Add(MembersSessionText.StorageSetAside, setAside);
			Add(MembersSessionText.StorageNotReKeyed, notReKeyed);
			Add(MembersSessionText.StorageOtherPins, leftUnderOtherPins);
			Add(MembersSessionText.StorageSkipped, skipped);
			if (backupSkipped)
			{
				lines.Add(new MembersNotice(MembersSessionText.StorageBackupSkipped));
			}

			if (previousSkipped)
			{
				lines.Add(new MembersNotice(MembersSessionText.StoragePreviousSkipped));
			}

			return lines;
		}

		/// <summary>The same lines in English (tests and anything that does not translate).</summary>
		public static List<string> StorageLines(IEnumerable<string> setAside, IEnumerable<string> notReKeyed, IEnumerable<string> leftUnderOtherPins, IEnumerable<string> skipped, bool backupSkipped, bool previousSkipped)
		{
			return StorageNotices(setAside, notReKeyed, leftUnderOtherPins, skipped, backupSkipped, previousSkipped).Select(n => n.Render(null)).ToList();
		}

		// --- inside ---

		/// <summary>
		/// One open of the vault with <paramref name="pass"/> (normalized). Changes held since a lock are saved first,
		/// with the key still held, so they are never put over newer data, but only when that key is under
		/// <paramref name="pass"/>: a wrong passphrase writes nothing (<see cref="OpenExisting"/> refuses it before this,
		/// keeping the held key). Null after an unexpected failure (shown by
		/// <see cref="OpenFailed"/>).
		/// </summary>
		private OpenResult OpenVault(string pass)
		{
			if (heldAtLock != null && Vault.IsOpen && Vault.KeyOpensWith(pass))
			{
				TrySaveHeld(waitIfBusy: true);
			}

			DropData();
			try
			{
				// a wrong passphrase can also come back as Busy while another copy of the app writes: tried again for a
				// moment
				return Retry(() => Vault.Open(pass), r => r.Status == OpenStatus.Busy);
			}
			catch (MembersFileException)
			{
				return new OpenResult(OpenStatus.Unreadable, null);
			}
			catch (Exception)
			{
				// anything else: the "not open" view with its Try again, and a fixed sentence (never the error's text)
				OpenFailed();
				return null;
			}
		}

		/// <summary>
		/// The open failed in a way the vault does not report: shows the "not open" view (Locked), which offers to try
		/// again, with a fixed notice.
		/// </summary>
		public void OpenFailed()
		{
			DropData();
			State = MembersSessionState.Locked;
			AddNotice(new MembersNotice(MembersSessionText.CouldNotOpen));
			Changed();
		}

		private void Apply(OpenResult result)
		{
			BackupsAvailable = result.BackupsAvailable;
			PinChangeInterrupted = result.PinChangeInterrupted;
			AddNotices(result);
			if (!result.IsOpen)
			{
				DropData();
				switch (result.Status)
				{
					case OpenStatus.NewerVersion:
						State = MembersSessionState.NewerVersion;
						break;
					case OpenStatus.Busy:
						State = MembersSessionState.Busy;
						break;
					case OpenStatus.ChangedElsewhere:
						State = MembersSessionState.ChangedElsewhere;
						break;
					default:
						State = MembersSessionState.Unreadable;
						break;
				}

				Changed();
				return;
			}

			AttachStore(result.Data);
			State = MembersSessionState.Open;
			Dirty = false;
			SaveProblem = null;
			ConsecutiveBusySaves = 0;
			OfferRestorePrevious = false;
			OpenedFromPrevious = result.Status == OpenStatus.OpenedFromPrevious;
			lastSavedHash = Hash(Store.Snapshot());
			if (OpenedFromPrevious)
			{
				AddNotice(new MembersNotice(MembersSessionText.OpenedFromPrevious));
			}

			if (PinChangeInterrupted)
			{
				// the vault finishes the renames before its next write: save soon
				PinChangeInterrupted = false;
				AddNotice(new MembersNotice(MembersSessionText.PinChangePending));
				Dirty = true;
			}

			if (heldAtLock != null)
			{
				// still held (the file was busy): only onto the same data they were made on
				if (heldAtLockBase != null && lastSavedHash.AsSpan().SequenceEqual(heldAtLockBase))
				{
					AttachStore(heldAtLock);
					Dirty = true;
					unsavedChanges = heldAtLockChanges;
				}
				else
				{
					AddNotice(new MembersNotice(MembersSessionText.LostBeforeLock));
				}

				heldAtLock = null;
				heldAtLockBase = null;
			}

			if (result.IdsMigrated)
			{
				// the new ids are stored with the next save: save soon
				Dirty = true;
			}

			ForgetDraftsOfMissingMembers();
			Changed();
			if (Dirty)
			{
				SaveWanted?.Invoke(this, EventArgs.Empty);
			}
		}

		/// <summary>After a load: what was typed for members no longer in the list is dropped (counted in a notice),
		/// and a selection of such a member is cleared.</summary>
		private void ForgetDraftsOfMissingMembers()
		{
			int gone = 0;
			foreach (var id in Drafts.MembersWithDrafts)
			{
				if (Store.FindMember(id) == null)
				{
					gone += Drafts.EnteredCountFor(id);
					Drafts.ForgetMember(id);
					fieldProblems.Remove(id);
				}
			}

			if (Drafts.SelectedMemberId != null && Store.FindMember(Drafts.SelectedMemberId) == null)
			{
				Drafts.Select(null);
			}

			if (gone > 0)
			{
				AddNotice(new MembersNotice(MembersSessionText.DraftsOfMissingMembers, gone));
			}
		}

		private void AttachStore(MembersData data)
		{
			if (Store != null && unsavedChanges > 0)
			{
				// replaced (a restore) while changes here were not saved: told, never silent
				AddNotice(new MembersNotice(MembersSessionText.LostUnsaved, unsavedChanges));
			}

			unsavedChanges = 0;
			Store = new MembersStore(data, clockUtc, newId, today);
			Store.Changed += (s, e) =>
			{
				Dirty = true;
				unsavedChanges++;
				SaveWanted?.Invoke(this, EventArgs.Empty);
			};
		}

		private void ResetFlags()
		{
			OfferRestorePrevious = false;
			OpenedFromPrevious = false;
			PinChangeInterrupted = false;
			SaveProblem = null;
			LastSaveBusy = false;
			ConsecutiveBusySaves = 0;
		}

		/// <summary>
		/// Forgets the data. Changes made here and not saved are never dropped without a notice: unless the caller has
		/// already accounted for them (held at lock, counted for another window's change, asked about at exit), a
		/// notice with their number is added.
		/// </summary>
		private void DropData(bool lossAccountedFor = false)
		{
			if (Store != null && unsavedChanges > 0 && !lossAccountedFor)
			{
				AddNotice(new MembersNotice(MembersSessionText.LostUnsaved, unsavedChanges));
			}

			Store = null;
			Dirty = false;
			lastSavedHash = null;
			unsavedChanges = 0;
		}

		private T Retry<T>(Func<T> action, Func<T, bool> busy)
		{
			var result = action();
			for (int attempt = 1; attempt < BusyAttempts && busy(result); attempt++)
			{
				wait(BusyWaitMs);
				result = action();
			}

			return result;
		}

		/// <summary>
		/// Another window changed the file: this window's data goes, and the changes made here since the last save
		/// are counted in a notice.
		/// </summary>
		private void EnterChangedElsewhere()
		{
			OfferRestorePrevious = OpenedFromPrevious;
			if (Store != null && unsavedChanges > 0)
			{
				AddNotice(new MembersNotice(MembersSessionText.LostChangedElsewhere, unsavedChanges));
			}

			DropData(lossAccountedFor: true);
			State = MembersSessionState.ChangedElsewhere;
			Changed();
		}

		/// <summary>The passphrase change refusal for a save or move that did not go through.</summary>
		private string RefusalFor(SaveStatus status)
		{
			switch (status)
			{
				case SaveStatus.Busy:
					return MembersSessionText.PassphraseRefusedBusy;
				case SaveStatus.ChangedElsewhere:
					EnterChangedElsewhere();
					return MembersSessionText.PassphraseRefusedChangedElsewhere;
				default:
					return MembersSessionText.PassphraseRefusedFailed;
			}
		}

		private void AddNotices(SaveResult result)
		{
			foreach (var line in StorageNotices(result.SetAside, null, result.LeftUnderOtherPins, result.Skipped, result.BackupSkipped, result.PreviousSkipped))
			{
				AddNotice(line);
			}
		}

		private void AddNotices(OpenResult result)
		{
			foreach (var line in StorageNotices(result.SetAside, result.NotReKeyed, null, result.Skipped, false, false))
			{
				AddNotice(line);
			}
		}

		private void AddNotice(MembersNotice line)
		{
			if (!notices.Contains(line))
			{
				notices.Add(line);
			}

			// a long-running session keeps only the latest few
			while (notices.Count > 8)
			{
				notices.RemoveAt(0);
			}
		}

		private static byte[] Hash(MembersData data)
		{
			var bytes = MembersExport.ToJsonBytes(data);
			try
			{
				return SHA256.HashData(bytes);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(bytes);
			}
		}

		private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// The Members tab's fixed sentences (English; the app passes them through its translation BEFORE any count or
	/// file name is put in). Formats take a count ({0}) and, for Storage lines, file names ({1}) only.
	/// </summary>
	public static class MembersSessionText
	{
		public const string PassphraseRequired = "Enter the members passphrase.";
		public const string WrongPassphrase = "That passphrase does not open the members list. Nothing was changed.";
		public const string WrongPassphraseInterrupted = "That passphrase does not open the members list. A passphrase change did not finish: enter the new passphrase, or the one from before. Nothing was changed.";
		public const string NoListYet = "There is no members list yet. Choose a passphrase to create one.";
		public const string ListAlreadyExists = "A members list already exists (another window may have just made one). Enter its passphrase to open it.";
		public const string ForgottenPassphraseWarning = "A forgotten passphrase cannot be recovered: the members list and its backups would be unreadable.";
		public const string CurrentPassphraseWrong = "The current passphrase is not right, so the passphrase was not changed.";
		public const string PassphraseChanged = "The members passphrase was changed.";
		public const string PassphraseRefusedNotOpen = "The members list is not open, so the passphrase was not changed. Open it first.";
		public const string PassphraseRefusedBusy = "Another window is saving the members list, so the passphrase was not changed. Wait a moment and try again.";
		public const string PassphraseRefusedChangedElsewhere = "Another window changed the members list, so the passphrase was not changed. Load the list again, then change the passphrase.";
		public const string PassphraseRefusedUnsaved = "The latest member changes could not be saved, so the passphrase was not changed.";
		public const string PassphraseRefusedFailed = "The members list could not be moved to the new passphrase, so the passphrase was not changed. The old passphrase still opens it.";
		public const string StillNotOpen = "The members list still did not open.";
		public const string StateNotOpen = "The members list is not open. Enter the members passphrase to open the list.";
		public const string StateUnreadable = "The members list cannot be read. Nothing was changed; the file is kept as it is.";
		public const string RestoreHelp = "You can bring back a saved copy. The current file is moved aside, never deleted.";
		public const string StateNewerVersion = "The members list was saved by a newer version of the app. Update the app to open it. Nothing was changed.";
		public const string StateBusy = "Another window of the app is saving the members list. Try again in a moment.";
		public const string StateChangedElsewhere = "Another window of the app changed the members list. Enter the passphrase to load the latest version; if it was changed in the other window, enter the new one. Changes made here since the last save were not kept.";
		public const string RestorePreviousOffer = "This window had opened the previous copy, and the main file has changed since. You can make the previous copy the main file instead (the main file is moved aside, never deleted).";
		public const string OpenedFromPrevious = "The members file could not be read, so the previous copy was opened. The next change saves it as the main file.";
		public const string SaveFailed = "The members list could not be saved. Your changes are kept in this window and saved again with the next change.";
		public const string SaveBusy = "Another window is saving the members list. Your changes are kept and saved again shortly.";
		public const string PinChangePending = "Storage: the passphrase change is not quite finished on disk. The app finishes it with the next save.";
		public const string LostBeforeLock = "Changes made just before locking could not be saved, and the list changed elsewhere since, so they were not kept.";
		public const string LostChangedElsewhere = "Another window changed the members list, so {0} change(s) made here could not be kept.";
		public const string LostUnsaved = "{0} change(s) made here were not saved and were not kept.";
		public const string DraftsOfMissingMembers = "{0} typed entries for members no longer in the list were not kept.";
		public const string CouldNotOpen = "The members list could not be opened. Enter the passphrase to try again.";
		public const string LockNotSaved = "The members list could not be saved before locking. The changes are kept in memory and saved as soon as possible; if the app closes first, you are asked before anything is lost.";
		public const string LockDiscardedEntries = "{0} typed entries on the Members tab were not saved (incomplete, or they did not pass the checks) and were cleared.";
		public const string HeldSavedNow = "The members changes from before the lock are saved now.";
		public const string ExitWouldLose = "Some members changes or typed text could not be saved and will be lost if the app closes now.";
		public const string NothingToRestore = "There is no saved copy to bring back.";
		public const string RestoreFailed = "The copy could not be brought back. Nothing was changed.";
		public const string RestoreWrongPin = "That copy did not open. If it was saved with another passphrase, enter that one in the second field; if the list itself cannot be read, enter it in the first field instead and change it afterwards with Change passphrase.";
		public const string RestoreChangedElsewhere = "Another window changed the members list meanwhile. Load the list again first.";
		public const string RestoreUnreadable = "That copy cannot be read.";
		public const string StorageSetAside = "Storage: {0} file(s) moved aside, never deleted: {1}";
		public const string StorageNotReKeyed = "Storage: {0} file(s) still under another passphrase: {1}";
		public const string StorageOtherPins = "Storage: {0} file(s) saved with another passphrase were not moved to the new passphrase: {1}";
		public const string StorageSkipped = "Storage: {0} entry(ies) that are not plain files were left alone: {1}";
		public const string StorageBackupSkipped = "Storage: today's backup could not be made.";
		public const string ListEmptyNoMembers = "No members yet. Add one above.";
		public const string ListEmptyNoMatch = "No member matches the search.";
		public const string ListEmptyTooFewStars = "No member has that many stars. Choose All to see everyone.";
		public const string ListEmptyMatchesWithFewerStars = "No member with that many stars matches the search. Choose All to see the other matches.";
		public const string ListEmptyOnlyArchived = "Only archived members match. Tick Show archived to see them.";
		public const string StoragePreviousSkipped = "Storage: the previous copy could not be written.";
	}
}
