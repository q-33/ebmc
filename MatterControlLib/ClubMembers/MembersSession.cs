using System;
using System.IO;
using System.Linq;
using MatterHackers.Agg.Font;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DataStorage;
using MatterHackers.MatterControl.SettingsManagement;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// EBMC Edition (OemSettings.EnableClubMembers): the Members tab's session for this run of the app. It holds a
	/// <see cref="MembersSessionCore"/> on the per-user data folder. The list has its own members passphrase, set when
	/// the list is created and entered on the tab; it has nothing to do with the maintainer lock. The tab's Lock button
	/// saves and forgets the key; the list is also saved and closed when the app closes. Changes and typed fields are
	/// saved a moment after the last one, without waiting on the UI thread when another window is writing (tried again
	/// later, less and less often). Problems at lock and exit are told in a box that does not need the Members tab. The
	/// wrappers here catch every exception and return translated fixed sentences. Use from the UI thread only. No
	/// network, no logging.
	/// </summary>
	public static class MembersSession
	{
		/// <summary>The pause after the last change before it is saved.</summary>
		public const double SaveDelaySeconds = 1.0;

		/// <summary>The longest pause between tries while another window keeps the file busy.</summary>
		public const double MaxRetrySeconds = 16;

		/// <summary>How often changes held since a lock are tried again before giving up until the next open or exit.</summary>
		public const int HeldTries = 6;

		private static readonly Func<string, string> Localize = s => s.Localize();

		private static MembersSessionCore core;

		private static int saveGeneration;

		/// <summary>Raised (on the UI thread) when the tab should show something else: opened, a state view, a notice.</summary>
		public static event EventHandler Changed;

		/// <summary>Whether this build has the Members tab at all.</summary>
		public static bool Enabled => OemSettings.Instance.EnableClubMembers;

		/// <summary>Whether the Members tab is shown: whenever the club switch is on (the tab asks for the passphrase).</summary>
		public static bool ShowTab => Enabled;

		/// <summary>The members folder: in the app's per-user data folder, never in the app or the repository.</summary>
		public static string Folder => Path.Combine(ApplicationDataStorage.ApplicationUserDataPath, "Members");

		/// <summary>The session, made on first use (only when the feature is on).</summary>
		public static MembersSessionCore Core
		{
			get
			{
				if (core == null)
				{
					core = new MembersSessionCore(Folder, () => DateTime.UtcNow);
					core.SaveWanted += (s, e) => ScheduleSave();
					core.StateChanged += (s, e) => Changed?.Invoke(null, EventArgs.Empty);
				}

				return core;
			}
		}

		/// <summary>
		/// Creates the members list under <paramref name="passphrase"/> (see <see cref="MembersSessionCore.CreateList"/>).
		/// Null when it is open; otherwise why not (translated).
		/// </summary>
		public static string CreateList(string passphrase, string repeat)
		{
			if (!Enabled)
			{
				return null;
			}

			try
			{
				return Core.CreateList(passphrase, repeat)?.Localize();
			}
			catch (Exception)
			{
				return MembersSessionText.CouldNotOpen.Localize();
			}
		}

		/// <summary>
		/// Opens the members list with <paramref name="passphrase"/> (see <see cref="MembersSessionCore.Unlock"/>): the
		/// Enter view's Open and the state views' Load again / Try again. Null when the passphrase was taken (the tab
		/// then shows whatever state the file gave); otherwise why not (translated). An unexpected failure shows the
		/// "not open" view with its notice.
		/// </summary>
		public static string Open(string passphrase)
		{
			if (!Enabled)
			{
				return null;
			}

			try
			{
				return Core.Unlock(passphrase)?.Localize();
			}
			catch (Exception)
			{
				try
				{
					Core.OpenFailed();
				}
				catch (Exception)
				{
				}

				return null;
			}
		}

		/// <summary>
		/// Brings back the newest daily backup (see <see cref="MembersSessionCore.RestoreNewestBackup"/>). Null when
		/// restored; otherwise why not (translated).
		/// </summary>
		public static string RestoreNewestBackup(string passphrase, string filePassphrase)
		{
			try
			{
				return core?.RestoreNewestBackup(passphrase, filePassphrase)?.Localize();
			}
			catch (Exception)
			{
				return MembersSessionText.RestoreFailed.Localize();
			}
		}

		/// <summary>Brings back the previous copy (see <see cref="MembersSessionCore.RestorePrevious"/>); as
		/// <see cref="RestoreNewestBackup"/>.</summary>
		public static string RestorePrevious(string passphrase, string filePassphrase)
		{
			try
			{
				return core?.RestorePrevious(passphrase, filePassphrase)?.Localize();
			}
			catch (Exception)
			{
				return MembersSessionText.RestoreFailed.Localize();
			}
		}

		/// <summary>
		/// Changes the members passphrase while the list is open (see <see cref="MembersSessionCore.ChangePassphrase"/>).
		/// Null when changed; otherwise why not (translated).
		/// </summary>
		public static string ChangePassphrase(string current, string next, string repeat)
		{
			if (core == null)
			{
				return MembersSessionText.PassphraseRefusedNotOpen.Localize();
			}

			try
			{
				return core.ChangePassphrase(current, next, repeat)?.Localize();
			}
			catch (Exception)
			{
				return MembersSessionText.PassphraseRefusedFailed.Localize();
			}
		}

		/// <summary>
		/// The tab's Lock button: save now (a short, capped wait if another window is writing), then forget the data,
		/// the typed text and the key. The lock always happens; what could not be saved is told in a box at once.
		/// </summary>
		public static void Lock()
		{
			saveGeneration++;
			if (core == null)
			{
				return;
			}

			LockReport report;
			try
			{
				report = core.Lock();
			}
			catch (Exception)
			{
				// the lock always happens
				report = new LockReport() { NotSaved = core.HoldsChangesFromLock };
			}

			if (report.HasNews)
			{
				Tell(report.Render(Localize));
			}

			if (core.HoldsChangesFromLock)
			{
				RetryHeld(1);
			}
		}

		/// <summary>
		/// Before the app closes (the close can still be stopped): saves once more. True when something would still be
		/// lost and the person has not already said to close anyway: the app asks first.
		/// </summary>
		public static bool ExitWouldLoseChanges()
		{
			if (core == null || exitConfirmed)
			{
				return false;
			}

			try
			{
				return core.SaveBeforeExit();
			}
			catch (Exception)
			{
				return core.HasUnsavedWork;
			}
		}

		/// <summary>The person chose to close anyway.</summary>
		public static void ConfirmExit() => exitConfirmed = true;

		private static bool exitConfirmed;

		/// <summary>When the app closes: save now, then forget the key, the data and the drafts.</summary>
		public static void FlushAndClose()
		{
			saveGeneration++;
			try
			{
				core?.CloseForExit();
			}
			catch (Exception)
			{
			}
		}

		/// <summary>Saves a moment after the last change (each change restarts the pause).</summary>
		public static void ScheduleSave() => ScheduleSave(SaveDelaySeconds, 0);

		private static void ScheduleSave(double delay, int busyTries)
		{
			int generation = ++saveGeneration;
			UiThread.RunOnIdle(
				() =>
				{
					if (generation != saveGeneration || core == null)
					{
						return;
					}

					try
					{
						// never waits here: if another window is writing, try again later, less and less often
						core.SaveNow(waitIfBusy: false);
						if (core.LastSaveBusy && core.Dirty)
						{
							ScheduleSave(Math.Min(MaxRetrySeconds, SaveDelaySeconds * Math.Pow(2, busyTries + 1)), busyTries + 1);
						}
					}
					catch (Exception)
					{
					}
				},
				delay);
		}

		/// <summary>While locked: tries the changes held since the lock again, less and less often.</summary>
		private static void RetryHeld(int attempt)
		{
			if (attempt > HeldTries)
			{
				return;
			}

			UiThread.RunOnIdle(
				() =>
				{
					if (core == null || !core.HoldsChangesFromLock || core.State == MembersSessionState.Open)
					{
						return;
					}

					HeldChanges result;
					try
					{
						result = core.TrySaveHeld(waitIfBusy: false);
					}
					catch (Exception)
					{
						result = HeldChanges.StillHeld;
					}

					switch (result)
					{
						case HeldChanges.Saved:
							Tell(MembersSessionText.HeldSavedNow.Localize());
							break;
						case HeldChanges.Lost:
							Tell(MembersSessionText.LostBeforeLock.Localize());
							break;
						case HeldChanges.StillHeld:
							RetryHeld(attempt + 1);
							break;
					}
				},
				Math.Min(MaxRetrySeconds, Math.Pow(2, attempt)));
		}

		/// <summary>A box that does not need the Members tab (it stays when the window is rebuilt). Fixed wording only.</summary>
		private static void Tell(string message)
		{
			// the stock OK box, shown the way the two-argument ShowMessageBox shows it, but wrapped to the width the text
			// really has (see NoticePage)
			UiThread.RunOnIdle(() => DialogWindow.Show(new NoticePage(message, "Members".Localize()), 0));
		}

		/// <summary>
		/// The stock OK box with its text wrapped to the visible width. Workaround: MessageBoxPage wraps to the content
		/// width minus the content padding in logical units (StyledMessageBoxWindow.cs, AdjustTextWrap), not in device
		/// pixels, so with the display scale above 1 a line can be up to (scale - 1) x 28 px wider than what is shown and
		/// its end is cut off. That box is shared by the whole app, so it is left as it is here; the real fix is
		/// DevicePadding there, after which this class can go and Tell can call ShowMessageBox again.
		/// </summary>
		private sealed class NoticePage : StyledMessageBox.MessageBoxPage
		{
			private readonly string message;

			public NoticePage(string message, string caption)
				: base(null, message, caption, StyledMessageBox.MessageType.OK, null, 400, 300, "", "", ApplicationController.Instance.Theme)
			{
				this.message = message;
				Rewrap();
			}

			public override void OnBoundsChanged(EventArgs e)
			{
				// the box wraps first (too wide), then this wraps again from the unwrapped text
				base.OnBoundsChanged(e);
				Rewrap();
			}

			private void Rewrap()
			{
				// the message is the only TextWidget the box puts in its scroll area
				var scroll = this.ContentRow?.Children.OfType<ScrollableWidget>().FirstOrDefault();
				var text = scroll?.ScrollArea.Children.OfType<TextWidget>().FirstOrDefault();
				if (message == null || text == null)
				{
					return;
				}

				// and the scroll bar's room when it shows
				double width = this.ContentRow.Width - (this.ContentRow.DevicePadding.Width + scroll.ScrollArea.DeviceMarginAndBorder.Width + text.DeviceMarginAndBorder.Width);
				if (width > 0)
				{
					var wrapper = new EnglishTextWrapping(text.PointSize * GuiWidget.DeviceScale);
					text.Text = wrapper.InsertCRs(message, width);
				}
			}
		}

		/// <summary>Saves now (what was typed in the detail fields too).</summary>
		public static void SaveNow()
		{
			try
			{
				core?.SaveNow();
			}
			catch (Exception)
			{
			}
		}
	}
}
