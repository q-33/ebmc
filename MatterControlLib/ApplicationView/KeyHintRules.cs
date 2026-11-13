using System.Text.RegularExpressions;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the shortcut wording rule used by KeyHints, with no app dependencies so Tests/EBMC.Tests can check it.
	/// </summary>
	public static class KeyHintRules
	{
		/// <summary>"Ctrl + P" notation everywhere; on a Mac "Ctrl" reads "Cmd" and "Alt" reads "Option".</summary>
		public static string ForSystem(string hint, bool mac)
		{
			if (string.IsNullOrEmpty(hint))
			{
				return hint;
			}

			// one notation: spaces round a + that joins two keys ("Ctrl+P" -> "Ctrl + P"); a quoted '+' key is left alone
			var text = Regex.Replace(hint, @"(?<=\w)\s*\+\s*(?=\w)", " + ");
			if (!mac)
			{
				return text;
			}

			text = Regex.Replace(text, @"\bCtrl\b", "Cmd");
			return Regex.Replace(text, @"\bAlt\b", "Option");
		}
	}
}
