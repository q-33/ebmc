using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// The user-facing wording for the OctoPrint feature, kept free of any GUI or translation code so that it can
	/// be unit tested. Every method takes a <c>localize</c> delegate that is applied to each English template
	/// before it is formatted; the app passes <c>s => s.Localize()</c>, tests pass <c>s => s</c>.
	/// Nothing here ever receives or returns the API key, except <see cref="MaskKey"/>, which returns only a masked
	/// summary of it.
	/// </summary>
	public static class OctoPrintMessages
	{
		private const string Dot = " · ";

		/// <summary>
		/// An actionable sentence (or two) for a failed OctoPrint call. Never null.
		/// </summary>
		public static string DescribeError(OctoPrintException exception, Uri host, Func<string, string> localize)
		{
			if (exception == null)
			{
				throw new ArgumentNullException(nameof(exception));
			}

			return DescribeError(exception.Kind, exception.ServerMessage, host, localize)
				?? DescribeError(OctoPrintError.Unexpected, exception.ServerMessage, host, localize);
		}

		/// <summary>
		/// The text for one error kind, or null for a kind that has no text yet (a unit test makes sure every
		/// <see cref="OctoPrintError"/> value has one).
		/// </summary>
		public static string DescribeError(OctoPrintError kind, string serverMessage, Uri host, Func<string, string> localize)
		{
			localize = localize ?? (s => s);
			string hostLabel = HostLabel(host, localize);

			switch (kind)
			{
				case OctoPrintError.HostNotFound:
					{
						string text = Format(localize("Couldn't find {0}. Check that the Raspberry Pi is powered on and on the same network as this computer."), hostLabel);
						if (host != null && host.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
						{
							text += " " + localize("If names ending in .local don't work on this network, enter the Pi's IP address instead.");
						}

						return text;
					}

				case OctoPrintError.ConnectionRefused:
					return Format(localize("{0} answered, but OctoPrint isn't running there (or the port is wrong)."), hostLabel);

				case OctoPrintError.Timeout:
					return localize("OctoPrint didn't respond in time.");

				case OctoPrintError.TlsFailure:
					return Format(localize("The connection to {0} couldn't be secured because its certificate isn't trusted. OctoPi uses a self-signed certificate; on the club network use an address starting with http://"), hostLabel);

				case OctoPrintError.Forbidden:
					return localize("OctoPrint rejected the API key. Use Get Key, or paste a new key.");

				case OctoPrintError.NotOperational:
					return localize("OctoPrint is running, but the printer isn't connected to it. Connect the printer in OctoPrint and try again.");

				case OctoPrintError.FileInUse:
					return localize("A file with this name is being printed right now, so it can't be replaced.");

				case OctoPrintError.UnsupportedFile:
					return localize("OctoPrint refused the file type.");

				case OctoPrintError.TooLarge:
					return localize("The file is larger than OctoPrint's upload limit.");

				case OctoPrintError.BadRequest:
					return WithServerMessage(localize("OctoPrint rejected the request."), serverMessage, localize);

				case OctoPrintError.NotFound:
					return WithServerMessage(localize("OctoPrint couldn't find what was asked for. Check that the address points at OctoPrint."), serverMessage, localize);

				case OctoPrintError.ServerError:
					return WithServerMessage(localize("OctoPrint reported an internal error."), serverMessage, localize);

				case OctoPrintError.AppKeysUnavailable:
					return WithServerMessage(localize("This OctoPrint server can't hand out API keys automatically. In OctoPrint, open Settings > Application Keys, create a key, and paste it here."), serverMessage, localize);

				case OctoPrintError.AppKeyDenied:
					return WithServerMessage(localize("The key request was denied in OctoPrint, or it expired. Use Get Key to try again."), serverMessage, localize);

				case OctoPrintError.Unexpected:
					return WithServerMessage(localize("Something unexpected happened while talking to OctoPrint."), serverMessage, localize);

				default:
					return null;
			}
		}

		/// <summary>
		/// One line describing the server and printer, for the Send menu: "OctoPrint 1.11.8 · Printer ready".
		/// <paramref name="state"/> null means OctoPrint is not connected to the printer.
		/// </summary>
		public static string StatusLine(VersionInfo version, PrinterState state, JobInfo job, Func<string, string> localize)
		{
			localize = localize ?? (s => s);
			string server = Format(localize("OctoPrint {0}"), string.IsNullOrWhiteSpace(version?.Server) ? "?" : version.Server.Trim());

			if (state == null)
			{
				return server + Dot + localize("Printer not connected");
			}

			if (state.Printing)
			{
				if (job?.Completion is double completion)
				{
					int percent = (int)Math.Floor(Math.Max(0, Math.Min(100, completion)));
					return server + Dot + Format(localize("Printing {0}%"), percent);
				}

				return server + Dot + localize("Printing");
			}

			if (state.Paused)
			{
				return server + Dot + localize("Print paused");
			}

			if (state.Error)
			{
				return server + Dot + localize("Printer error");
			}

			if (state.Operational)
			{
				return server + Dot + localize("Printer ready");
			}

			return server + Dot + localize("Printer busy");
		}

		/// <summary>
		/// What to tell the user after a successful upload.
		/// </summary>
		public static string UploadOutcome(bool printRequested, UploadResult result, Func<string, string> localize)
		{
			localize = localize ?? (s => s);
			string name = result?.Name ?? string.Empty;

			if (printRequested)
			{
				return result?.EffectivePrint == true
					? Format(localize("Printing {0} on OctoPrint."), name)
					: Format(localize("Uploaded as {0}, but the print did not start because the printer wasn't ready. Start it from OctoPrint."), name);
			}

			return Format(localize("Uploaded as {0}."), name);
		}

		/// <summary>
		/// A masked summary of a key for display: "••••1a2b". Shows the last four characters
		/// only when the key is at least 16 characters long, so a short key is never mostly revealed. Empty when
		/// there is no key.
		/// </summary>
		public static string MaskKey(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
			{
				return string.Empty;
			}

			key = key.Trim();
			const string Mask = "••••";
			return key.Length >= 16 ? Mask + key.Substring(key.Length - 4) : Mask;
		}

		/// <summary>
		/// Trims a pasted key and checks it has only the characters an HTTP header can carry (printable ASCII,
		/// no spaces). False for an empty or invalid key.
		/// </summary>
		public static bool TryCleanApiKey(string input, out string key)
		{
			key = null;
			string text = input?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}

			foreach (char c in text)
			{
				if (c <= 0x20 || c > 0x7e)
				{
					return false;
				}
			}

			key = text;
			return true;
		}

		/// <summary>
		/// The base of the remote file name for a bed or part name: drops a 3D-model extension (".stl", ".mcx",
		/// ...) so "bracket.stl" becomes "bracket" rather than "bracket.stl.gcode"; keeps other dots
		/// ("v1.2 bracket"). "print" when the name is empty.
		/// </summary>
		public static string RemoteBaseName(string itemName)
		{
			string name = itemName?.Trim();
			if (string.IsNullOrEmpty(name))
			{
				return "print";
			}

			string extension = Path.GetExtension(name).ToLowerInvariant();
			switch (extension)
			{
				case ".stl":
				case ".obj":
				case ".3mf":
				case ".amf":
				case ".mcx":
					name = name.Substring(0, name.Length - extension.Length).Trim();
					break;
			}

			return name.Length == 0 ? "print" : name;
		}

		/// <summary>
		/// The base of the remote file name for a bed, from the names of its top-level printable parts: the name when
		/// there is one part or all parts share it, "&lt;first&gt;_and_&lt;n-1&gt;_more" for several different names,
		/// and "print" when no part has a usable name. Parts without a usable name are left out. Each name goes
		/// through <see cref="RemoteBaseName"/> first (so "bracket.stl" counts as "bracket"). The result is not
		/// cleaned or shortened; <see cref="OctoPrintClient.SanitizeRemoteName"/> does that.
		/// </summary>
		public static string ChooseBaseName(IList<string> partNames)
		{
			var names = new List<string>();
			foreach (string partName in partNames ?? Array.Empty<string>())
			{
				if (!string.IsNullOrWhiteSpace(partName))
				{
					string name = RemoteBaseName(partName);
					if (name != "print" || partName.Trim() == "print")
					{
						names.Add(name);
					}
				}
			}

			if (names.Count == 0)
			{
				return "print";
			}

			string first = names[0];
			bool allSame = names.TrueForAll(n => string.Equals(n, first, StringComparison.Ordinal));
			return allSame ? first : string.Format(CultureInfo.InvariantCulture, "{0}_and_{1}_more", first, names.Count - 1);
		}

		/// <summary>
		/// The page to open for "Open OctoPrint": the approval page the server returned, if it is an absolute
		/// http or https address (anything else, such as file: or a custom scheme, is ignored), otherwise the
		/// OctoPrint address itself.
		/// </summary>
		public static Uri BrowserUri(string authDialogUrl, Uri baseUri)
		{
			if (!string.IsNullOrWhiteSpace(authDialogUrl)
				&& Uri.TryCreate(authDialogUrl.Trim(), UriKind.Absolute, out Uri auth)
				&& (auth.Scheme == Uri.UriSchemeHttp || auth.Scheme == Uri.UriSchemeHttps)
				&& string.IsNullOrEmpty(auth.UserInfo))
			{
				return auth;
			}

			return baseUri;
		}

		private static string HostLabel(Uri host, Func<string, string> localize)
		{
			if (host == null || !host.IsAbsoluteUri || string.IsNullOrEmpty(host.Host))
			{
				return localize("the OctoPrint server");
			}

			// Authority never includes a user name or password
			return host.Authority;
		}

		private static string WithServerMessage(string text, string serverMessage, Func<string, string> localize)
		{
			if (string.IsNullOrWhiteSpace(serverMessage))
			{
				return text;
			}

			return text + " " + Format(localize("OctoPrint said: {0}"), serverMessage.Trim());
		}

		private static string Format(string template, params object[] args)
		{
			return string.Format(CultureInfo.CurrentCulture, template, args);
		}
	}
}
