using System.Text.RegularExpressions;
using MatterHackers.Agg.Platform;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the shortcut modifier named in hints and menus. The Mac host maps Command to Control
	/// (MatterControl.CrossPlatform GlfwPlatformWindow.SyncModifiers), so on a Mac the shortcuts are Cmd + key, and some
	/// Ctrl combinations are taken by the system (Ctrl + Up/Down is Mission Control). Elsewhere it stays Ctrl.
	/// </summary>
	public static class KeyHints
	{
		/// <summary>"Cmd" on macOS, "Ctrl" elsewhere.</summary>
		public static string Modifier => AggContext.OperatingSystem == OSType.Mac ? "Cmd" : "Ctrl";

		/// <summary>"Option" on macOS, "Alt" elsewhere.</summary>
		public static string AltName => AggContext.OperatingSystem == OSType.Mac ? "Option" : "Alt";

		/// <summary>
		/// <paramref name="hint"/> in the one notation used for shortcuts in hints and menus ("Ctrl + P", with spaces
		/// round every +), with the modifier names of this computer: on a Mac "Ctrl" becomes "Cmd" and "Alt" becomes
		/// "Option". Call it on the localized text: "Undo: Ctrl + Z".Localize() becomes "Undo: Cmd + Z" on a Mac, and
		/// a menu's "Ctrl+P" becomes "Ctrl + P" (or "Cmd + P").
		/// </summary>
		public static string ForThisComputer(string hint)
		{
			return KeyHintRules.ForSystem(hint, AggContext.OperatingSystem == OSType.Mac);
		}
	}
}
