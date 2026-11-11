using System;
using System.IO;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.SettingsManagement;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: a translation map that shows the OEM product name wherever a localized
	/// string says "MatterControl". The lookup itself always uses the original English string,
	/// so the translation files (and, in DEBUG builds, the strings recorded as new) are unchanged;
	/// only the returned text is rebranded.
	/// </summary>
	public class BrandedTranslationMap : TranslationMap
	{
		private const string UpstreamName = "MatterControl";

		// Read once when the map is built (LoadTranslationMap), never from inside Translate,
		// so a Localize() call made while the OEM settings load cannot recurse.
		private readonly string productName = GetProductName();

		public BrandedTranslationMap(string twoLetterIsoLanguageName)
			: base(twoLetterIsoLanguageName)
		{
		}

		public BrandedTranslationMap(StreamReader machineTranslation, StreamReader humanTranslation, string twoLetterIsoLanguageName)
			: base(machineTranslation, humanTranslation, twoLetterIsoLanguageName)
		{
		}

		public override string Translate(string englishString)
		{
			var translated = base.Translate(englishString);

			if (productName == null
				|| string.IsNullOrEmpty(translated))
			{
				return translated;
			}

			return translated.Replace(UpstreamName, productName, StringComparison.Ordinal);
		}

		/// <summary>
		/// The product name to show, or null when no replacement should happen: no OEM settings,
		/// an empty name, or a name that already contains "MatterControl" (upstream's own settings).
		/// </summary>
		private static string GetProductName()
		{
			try
			{
				var name = OemSettings.Instance?.UnregisteredProductName;
				if (string.IsNullOrEmpty(name)
					|| name.Contains(UpstreamName, StringComparison.Ordinal))
				{
					return null;
				}

				return name;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
