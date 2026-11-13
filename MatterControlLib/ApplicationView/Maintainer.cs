using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.MatterControl.SetupWizard;
using MatterHackers.MatterControl.SlicerConfiguration;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the club maintainer lock. When the OEM settings turn it on, printer and material settings need a
	/// maintainer, who unlocks them with a PIN until the app closes. The unlocked state is never saved, so every start
	/// is locked. Only a hash of the PIN is kept, in this computer's user settings. Use from the UI thread only.
	/// </summary>
	public static class Maintainer
	{
		/// <summary>
		/// The shortest time between two "Locked" notifications, so one action that is refused several writes shows
		/// one message.
		/// </summary>
		private const long NoticeIntervalMs = 4000;

		private static long lastNoticeMs = long.MinValue;

		/// <summary>
		/// Raised after <see cref="TryUnlock"/> succeeds or <see cref="Lock"/> locks again.
		/// </summary>
		public static event EventHandler Changed;

		/// <summary>
		/// Whether this build uses the maintainer lock at all.
		/// </summary>
		public static bool LockEnabled => OemSettings.Instance.RequireMaintainerForPrinterSettings;

		/// <summary>
		/// True after a maintainer has entered the PIN in this session.
		/// </summary>
		public static bool IsUnlocked { get; private set; }

		/// <summary>
		/// Whether printer-wide actions (calibration, presets, import/export, add/delete/rename printers, controls,
		/// terminal) are allowed now: always when the lock is off, otherwise only when unlocked.
		/// </summary>
		public static bool CanManagePrinter => !LockEnabled || IsUnlocked;

		/// <summary>
		/// Whether a printer may be added (or imported) now: when <see cref="CanManagePrinter"/>, and always while no
		/// printer exists yet, so a fresh install can be set up.
		/// </summary>
		public static bool CanAddPrinter => CanManagePrinter || !ProfileManager.Instance.ActiveProfiles.Any();

		/// <summary>
		/// Whether a maintainer PIN has been set on this computer.
		/// </summary>
		public static bool HasPin => !string.IsNullOrEmpty(UserSettings.Instance.get(UserSettingsKey.MaintainerPin));

		/// <summary>
		/// The text for a locked setting or action.
		/// </summary>
		public static string LockedText => MaintainerMessages.Locked(s => s.Localize());

		/// <summary>
		/// Whether a G-code file opened from elsewhere must be refused for sending or printing now (see
		/// <see cref="SettingsLock.RefuseGCodeSource"/>).
		/// </summary>
		public static bool RefuseGCodeSource(bool isGCodeSource) => SettingsLock.RefuseGCodeSource(isGCodeSource, LockEnabled, IsUnlocked);

		/// <summary>
		/// Why a G-code file is refused while locked.
		/// </summary>
		public static string ForeignGCodeText => MaintainerMessages.ForeignGCode(s => s.Localize());

		/// <summary>
		/// Whether the bed holds parts that write their own G-code into the print: every scene G-code transformer, the
		/// same set RunSceneGCodeProcesorsStream applies when the print is exported or sent.
		/// </summary>
		public static bool HasCommandParts(PrinterConfig printer)
		{
			return printer?.Bed?.Scene?.Descendants().Any(item => item is DesignTools.IGCodeTransformer) == true;
		}

		/// <summary>
		/// Whether the printer's bed must be refused for sending or printing now because of such parts (see
		/// <see cref="SettingsLock.RefuseCommandParts"/>).
		/// </summary>
		public static bool RefuseCommandParts(PrinterConfig printer)
		{
			// only walk the scene when the lock could refuse (same result as the rule, without the walk when unlocked)
			if (!(LockEnabled && !IsUnlocked))
			{
				return false;
			}

			return SettingsLock.RefuseCommandParts(HasCommandParts(printer), LockEnabled, IsUnlocked);
		}

		/// <summary>
		/// Why a bed with temperature or command parts is refused while locked.
		/// </summary>
		public static string CommandPartsText => MaintainerMessages.CommandParts(s => s.Localize());

		/// <summary>
		/// Stores a new PIN. Returns false, storing nothing, if it is not 4 to 8 digits, or if a PIN is already set and
		/// the session is not unlocked (only a maintainer may replace it).
		/// </summary>
		public static bool SetPin(string pin)
		{
			if (!MaintainerPin.IsValidPin(pin)
				|| (HasPin && !IsUnlocked))
			{
				return false;
			}

			UserSettings.Instance.set(UserSettingsKey.MaintainerPin, MaintainerPin.Hash(pin));
			return true;
		}

		/// <summary>
		/// Whether <paramref name="pin"/> is the stored PIN. Does not change the lock.
		/// </summary>
		public static bool CheckPin(string pin)
		{
			return MaintainerPin.Verify(pin, UserSettings.Instance.get(UserSettingsKey.MaintainerPin));
		}

		/// <summary>
		/// Unlocks for the rest of the session if <paramref name="pin"/> is the stored PIN.
		/// </summary>
		public static bool TryUnlock(string pin)
		{
			if (!CheckPin(pin))
			{
				return false;
			}

			if (!IsUnlocked)
			{
				IsUnlocked = true;
				OnChanged();
			}

			return true;
		}

		public static void Lock()
		{
			if (IsUnlocked)
			{
				IsUnlocked = false;
				OnChanged();
			}
		}

		/// <summary>
		/// Whether <paramref name="key"/> of <paramref name="settings"/> may be changed now.
		/// </summary>
		public static bool CanEdit(PrinterSettings settings, string key)
		{
			return SettingsLock.CanEdit(LockEnabled, IsUnlocked, key, ActiveMaterialKeys(settings));
		}

		/// <summary>
		/// The keys the active material preset sets, or null when there is none.
		/// </summary>
		public static IEnumerable<string> ActiveMaterialKeys(PrinterSettings settings)
		{
			return settings?.MaterialLayer?.Keys;
		}

		/// <summary>
		/// Shows "Locked: a club maintainer can change this" in the status bar at the bottom of the main window for a
		/// few seconds (the same place as the toolbar hints), at most once every few seconds.
		/// </summary>
		public static void NotifyLocked()
		{
			long now = UiThread.CurrentTimerMs;
			if (lastNoticeMs != long.MinValue
				&& now - lastNoticeMs < NoticeIntervalMs)
			{
				return;
			}

			lastNoticeMs = now;

			string text = LockedText;
			ApplicationController.Instance.SetUiHint(text);
			UiThread.RunOnIdle(
				() =>
				{
					// Clear it unless something else has put its own hint there since
					if (ApplicationController.Instance.GetUiHint() == text)
					{
						ApplicationController.Instance.SetUiHint("");
					}
				},
				NoticeIntervalMs / 1000.0);
		}

		/// <summary>
		/// Dialog pages that change presets or printer defaults, closed when the lock comes back on. The calibration
		/// wizards and firmware (EEPROM) pages are left open: closing them part way could leave the printer half set up.
		/// </summary>
		private static readonly Type[] MaintainerOnlyPages =
		{
			typeof(SlicePresetsPage),
			typeof(ImportSettingsPage),
			typeof(UpdateSettingsPage),
			typeof(PrinterProfileHistoryPage),
		};

		private static void OnChanged()
		{
			Changed?.Invoke(null, EventArgs.Empty);

			UiThread.RunOnIdle(() =>
			{
				if (LockEnabled && !IsUnlocked)
				{
					// Locked again: close the maintainer-only windows that may still be open
					foreach (var pageType in MaintainerOnlyPages)
					{
						DialogWindow.Close(pageType);
					}
				}

				// Part settings in the scene may hold locked keys that now apply or no longer apply
				foreach (var printer in ApplicationController.Instance.ActivePrinters.Distinct().ToList())
				{
					printer?.ForceSceneSettingsUpdate();
				}

				// Rebuild the main window so every locked control, menu and tab is up to date. Dialogs (such as the
				// Settings dialog) are separate windows and stay open.
				ApplicationController.Instance.ReloadAll().ConfigureAwait(false);
			});
		}
	}
}
