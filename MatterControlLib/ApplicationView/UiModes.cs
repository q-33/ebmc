using System;
using System.Linq;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.SettingsManagement;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the app-wide "Simple · Advanced" mode. Simple shows the essentials and Advanced shows every tool.
	/// A mode only decides what is shown: it never changes a setting value, what is on the bed, or how a print is sliced,
	/// sent or started. When the OEM settings leave the switch off, <see cref="Current"/> is always Advanced and nothing
	/// here changes the stock interface. The rules are in MatterControl.Printing/Settings/UiMode.cs (UiModeRules).
	/// Independent of the maintainer lock (Maintainer), which works the same in both modes. Use from the UI thread only.
	/// </summary>
	public static class UiModes
	{
		/// <summary>How long the status-bar hints of this switch stay up, in seconds.</summary>
		private const double HintSeconds = 6;

		private static bool initialized;

		/// <summary>
		/// True from a successful <see cref="Set"/> until its window rebuild has finished. ReloadAll only sets
		/// IsReloading once the queued rebuild starts, so without this a fast second click could queue a second rebuild.
		/// </summary>
		private static bool reloadPending;

		/// <summary>
		/// Raised after the mode has changed (before the main window is rebuilt).
		/// </summary>
		public static event EventHandler Changed;

		/// <summary>
		/// Whether this build shows the switch at all (OEM ShowUiModeSwitch).
		/// </summary>
		public static bool SwitchEnabled => OemSettings.Instance.ShowUiModeSwitch;

		/// <summary>
		/// The mode in use: always Advanced when the switch is off; otherwise the user's stored choice, else the OEM
		/// default, else Advanced.
		/// </summary>
		public static UiMode Current
		{
			get
			{
				if (!SwitchEnabled)
				{
					return UiMode.Advanced;
				}

				return UiModeRules.Resolve(UserSettings.Instance.get(UserSettingsKey.UiMode), OemSettings.Instance.DefaultUiMode);
			}
		}

		/// <summary>
		/// Called once at startup (Application.Initialize, before the main view is built): makes the Slice Settings
		/// detail level agree with the mode (a new user, or a level picked before the switch existed). Only which rows are
		/// listed changes; no setting value is touched. Does nothing with the switch off.
		/// </summary>
		public static void InitializeAtStartup()
		{
			if (initialized || !SwitchEnabled)
			{
				return;
			}

			initialized = true;
			SyncSliceSettingsDetail(Current);
		}

		/// <summary>
		/// True when the switch is on and set to Simple.
		/// </summary>
		public static bool IsSimple => Current == UiMode.Simple;

		/// <summary>
		/// Why the mode can't change right now, or null when it can, naming what it waits for (UiModeBusyRules): a slice,
		/// the last switch's window rebuild, or a running task that a rebuild could disturb (a save, an export, a file load,
		/// a part rebuild, a send). Tasks marked RunningTaskOptions.AllowsWindowRebuild (a print in progress, the heater
		/// hold after it, heating waits, timed notices) do not hold it up.
		/// </summary>
		public static string BusyReason()
		{
			var controller = ApplicationController.Instance;

			bool slicing = controller.ActivePrinters.Any(p => p?.ViewState?.SlicingItem == true);
			var tasks = controller.Tasks.RunningTasks.Select(t => (t.Title, t.Options?.AllowsWindowRebuild == true));

			return UiModeBusyRules.Reason(slicing, controller.IsReloading || reloadPending, tasks, s => s.Localize());
		}

		/// <summary>
		/// Changes the mode: stores it, sets the Slice Settings detail level to match, and rebuilds the main window.
		/// Returns false, changing nothing, when the switch is off, the mode is already <paramref name="mode"/>, or a
		/// printer task is running (a status-bar hint then says why).
		/// </summary>
		public static bool Set(UiMode mode)
		{
			if (!SwitchEnabled
				|| Current == mode)
			{
				return false;
			}

			var busy = BusyReason();
			if (busy != null)
			{
				ShowHint(busy);
				return false;
			}

			reloadPending = true;
			UserSettings.Instance.set(UserSettingsKey.UiMode, UiModeRules.Format(mode));
			SyncSliceSettingsDetail(mode);

			Changed?.Invoke(null, EventArgs.Empty);

			bool firstAdvanced = mode == UiMode.Advanced
				&& UserSettings.Instance.get(UserSettingsKey.UiModeAdvancedHintShown) != "true";

			UiThread.RunOnIdle(async () =>
			{
				// Rebuild the main window (the same call the maintainer lock uses). Workspaces, beds, selections and
				// printer settings live outside the window and are kept; dialogs are separate windows and stay open.
				try
				{
					await ApplicationController.Instance.ReloadAll();
				}
				finally
				{
					reloadPending = false;
				}

				if (firstAdvanced)
				{
					UserSettings.Instance.set(UserSettingsKey.UiModeAdvancedHintShown, "true");
					ShowHint("Advanced shows every tool. Switch back to Simple at any time.".Localize());
				}
			});

			return true;
		}

		/// <summary>
		/// The Slice Settings "View" menu picked <paramref name="level"/> (Simple, Intermediate or Advanced). With the switch
		/// on, the app mode follows it, so the menu and the mode never disagree: picking Intermediate or Advanced while in
		/// Simple switches the app to Advanced, picking Simple while in Advanced switches it to Simple. Returns true when
		/// the pick was handled here by a mode change (or refused while a printer task runs); false when the caller should
		/// apply the level itself as before (switch off, or a level within the current mode).
		/// </summary>
		public static bool PickSliceSettingsDetail(string level)
		{
			if (!SwitchEnabled)
			{
				return false;
			}

			var mode = UiModeRules.ModeForDetail(level);
			bool modeChange = mode != null && mode.Value != Current;

			// A switch that has to wait changes nothing at all: the level is not remembered either, so the menu shows the
			// old level again when it next opens (true: the caller must not apply the level itself)
			if (modeChange
				&& BusyReason() is string busy)
			{
				ShowHint(busy);
				return true;
			}

			if (UiModeRules.IsNonSimpleDetail(level))
			{
				// Remember it as the level Advanced mode returns to
				UserSettings.Instance.set(UserSettingsKey.UiModeLastAdvancedDetail, level);
			}

			if (!modeChange)
			{
				return false;
			}

			// Switching the mode sets the level and rebuilds everything, the Slice Settings panel included
			Set(mode.Value);
			return true;
		}

		/// <summary>
		/// Writes UserSettingsKey.SliceSettingsViewDetail for <paramref name="mode"/> when it does not already agree. The
		/// level only chooses which settings rows the Slice Settings and printer settings panels list.
		/// </summary>
		private static void SyncSliceSettingsDetail(UiMode mode)
		{
			var stored = UserSettings.Instance.get(UserSettingsKey.SliceSettingsViewDetail);
			var storedLast = UserSettings.Instance.get(UserSettingsKey.UiModeLastAdvancedDetail);
			var (detail, lastAdvanced) = UiModeRules.Sync(mode, stored, storedLast);

			if (lastAdvanced != storedLast)
			{
				UserSettings.Instance.set(UserSettingsKey.UiModeLastAdvancedDetail, lastAdvanced);
			}

			if (detail != stored)
			{
				UserSettings.Instance.set(UserSettingsKey.SliceSettingsViewDetail, detail);
			}
		}

		private static void ShowHint(string text)
		{
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
				HintSeconds);
		}
	}
}
