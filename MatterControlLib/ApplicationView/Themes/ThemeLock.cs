using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the rule for a build that fixes its theme (OemSettings.LockedThemeSet). Pure, so it is unit tested.
	/// </summary>
	public static class ThemeLock
	{
		/// <summary>
		/// The theme provider (StaticData/Themes/System folder) holding the locked themeset, or null when the theme is not
		/// locked or the themeset does not exist (the app then loads the theme as stock does).
		/// </summary>
		/// <param name="lockedThemeSet">OemSettings.LockedThemeSet: a themeset name such as "EBMC-Dark", or empty.</param>
		/// <param name="providers">Each provider's name and the themeset names it holds.</param>
		public static string ProviderFor(string lockedThemeSet, IEnumerable<(string provider, IEnumerable<string> themeNames)> providers)
		{
			if (string.IsNullOrWhiteSpace(lockedThemeSet))
			{
				return null;
			}

			return providers
				.Where(p => p.themeNames != null && p.themeNames.Contains(lockedThemeSet, StringComparer.Ordinal))
				.Select(p => p.provider)
				.FirstOrDefault();
		}
	}
}
