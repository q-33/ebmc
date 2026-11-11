using System;
using MatterHackers.Agg;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.SlicerConfiguration;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// Where the OctoPrint settings live. The switch and the address are ordinary printer settings
	/// (octoprint_enabled, octoprint_host). The API key is a secret: it is kept only in this computer's
	/// <see cref="UserSettings"/> (the SQLite store in the user's data folder) under
	/// "OctoPrintApiKey|" + <see cref="OctoPrintEndpoint.Key"/>, so it follows the server rather than the printer
	/// profile and is never part of a profile export, a print-history archive or the G-code.
	/// </summary>
	public static class OctoPrintSettings
	{
		private const string ApiKeyPrefix = "OctoPrintApiKey|";

		public static bool Enabled(PrinterConfig printer)
		{
			// FFF printers only, so a hand-edited SLA profile cannot be routed to OctoPrint
			return printer?.Settings != null
				&& printer.Settings.Slicer.PrinterType == PrinterType.FFF
				&& printer.Settings.GetValue<bool>(SettingsKey.octoprint_enabled);
		}

		/// <summary>
		/// The normalized OctoPrint address from the printer settings. On failure <paramref name="error"/> is a
		/// localized sentence saying what to fix.
		/// </summary>
		public static bool TryGetBaseUri(PrinterConfig printer, out Uri baseUri, out string error)
		{
			baseUri = null;
			string host = printer?.Settings?.GetValue(SettingsKey.octoprint_host);
			if (string.IsNullOrWhiteSpace(host))
			{
				error = "Enter the OctoPrint address in the printer settings (Printer > General > OctoPrint Address).".Localize();
				return false;
			}

			if (!OctoPrintEndpoint.TryNormalize(host, out baseUri, out string normalizeError))
			{
				error = normalizeError.Localize();
				return false;
			}

			error = null;
			return true;
		}

		/// <summary>
		/// The API key stored on this computer for the server, or null when there is none.
		/// </summary>
		public static string GetApiKey(Uri baseUri)
		{
			if (baseUri == null)
			{
				return null;
			}

			string key = UserSettings.Instance.get(SettingName(baseUri));
			return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
		}

		/// <summary>
		/// Stores the API key for the server on this computer. Null or empty removes it (UserSettings has no delete,
		/// so the stored value is overwritten with an empty string). Throws ArgumentException, without the key in
		/// the message, when the key has characters an HTTP header cannot carry.
		/// </summary>
		public static void SetApiKey(Uri baseUri, string apiKey)
		{
			if (baseUri == null)
			{
				throw new ArgumentNullException(nameof(baseUri));
			}

			if (string.IsNullOrWhiteSpace(apiKey))
			{
				UserSettings.Instance.set(SettingName(baseUri), string.Empty);
				return;
			}

			if (!OctoPrintMessages.TryCleanApiKey(apiKey, out string cleaned))
			{
				throw new ArgumentException("The API key contains characters that are not allowed.", nameof(apiKey));
			}

			UserSettings.Instance.set(SettingName(baseUri), cleaned);
		}

		/// <summary>
		/// A client for the printer's OctoPrint server using the key stored on this computer. Returns null, with a
		/// localized <paramref name="error"/>, when the feature is off or the address or key is missing or invalid;
		/// it never throws for those cases. The caller disposes the client.
		/// </summary>
		public static OctoPrintClient CreateClient(PrinterConfig printer, out Uri baseUri, out string error)
		{
			baseUri = null;
			if (!Enabled(printer))
			{
				error = "Turn on \"Send jobs to OctoPrint\" in the printer settings (Printer > General).".Localize();
				return null;
			}

			if (!TryGetBaseUri(printer, out baseUri, out error))
			{
				return null;
			}

			string apiKey = GetApiKey(baseUri);
			if (apiKey == null)
			{
				error = "There is no OctoPrint API key on this computer for {0}. Add one in the printer settings (Printer > General > OctoPrint API Key).".Localize().FormatWith(baseUri.Authority);
				return null;
			}

			try
			{
				return new OctoPrintClient(baseUri, apiKey);
			}
			catch (ArgumentException)
			{
				// The client's message never contains the key
				error = "The stored OctoPrint API key contains characters that are not allowed. Paste the key again in the printer settings.".Localize();
				return null;
			}
		}

		private static string SettingName(Uri baseUri)
		{
			return ApiKeyPrefix + OctoPrintEndpoint.Key(baseUri);
		}
	}
}
