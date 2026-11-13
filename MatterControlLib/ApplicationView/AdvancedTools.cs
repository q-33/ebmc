using MatterHackers.MatterControl.SettingsManagement;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the Advanced-mode design tools (OemSettings.EnableAdvancedTools): command search, the typed
	/// Transform section, Mirror Copy and the keyboard shortcuts sheet. They only change the design through the undo
	/// buffer, the same way the existing tools do; none of them slices, sends or prints. With the switch off nothing here
	/// changes the stock interface.
	/// </summary>
	public static class AdvancedTools
	{
		/// <summary>Whether this build has the tools at all.</summary>
		public static bool IsEnabled => OemSettings.Instance.EnableAdvancedTools;

		/// <summary>
		/// Whether the Advanced-only parts are shown now: the tools are on and the app is in Advanced mode (always the
		/// case when the OEM settings leave the mode switch off).
		/// </summary>
		public static bool InAdvancedMode => IsEnabled && !UiModes.IsSimple;
	}
}
