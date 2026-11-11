using System;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// Turns what a person types as an OctoPrint address ("octopi.local", "https://pi:8443/octoprint")
	/// into a clean base URI that endpoint paths can be resolved against.
	/// </summary>
	public static class OctoPrintEndpoint
	{
		/// <summary>
		/// Normalizes an OctoPrint address. Adds http:// when no scheme is given, allows only http and https,
		/// drops any user name, password, query and fragment, lower-cases the host, keeps the port and any base
		/// path (for OctoPrint behind a reverse proxy) and makes sure the path ends with a slash, so that
		/// <c>new Uri(baseUri, "api/version")</c> stays under the base path. Rejects a scheme without "//"
		/// ("http:/pi"), an IPv6 link-local address with a zone ("[fe80::1%25en0]", which a URL cannot carry
		/// through to the connection) and a base path with "." or ".." segments, plain or percent-encoded.
		/// </summary>
		public static bool TryNormalize(string input, out Uri baseUri, out string error)
		{
			baseUri = null;
			error = null;

			string text = input?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				error = "Enter the address of the OctoPrint server.";
				return false;
			}

			// "http:/pi" or "http:pi" would otherwise become a server named "http"
			foreach (string scheme in new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps })
			{
				if (text.StartsWith(scheme + ":", StringComparison.OrdinalIgnoreCase)
					&& !text.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase))
				{
					error = "The OctoPrint address is malformed: write it as " + scheme + "://host.";
					return false;
				}
			}

			if (!text.Contains("://"))
			{
				text = "http://" + text;
			}

			if (HasDotSegment(text))
			{
				error = "The OctoPrint address path must not contain \".\" or \"..\" segments.";
				return false;
			}

			if (!Uri.TryCreate(text, UriKind.Absolute, out Uri parsed))
			{
				error = "The OctoPrint address is not a valid URL.";
				return false;
			}

			if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
			{
				error = "The OctoPrint address must start with http:// or https://.";
				return false;
			}

			string host = parsed.Host.ToLowerInvariant();
			if (string.IsNullOrEmpty(host))
			{
				error = "The OctoPrint address has no host name.";
				return false;
			}

			if (parsed.HostNameType == UriHostNameType.IPv6 && parsed.DnsSafeHost.Contains("%"))
			{
				error = "Link-local IPv6 addresses with a zone (such as %en0) are not supported. Use the Pi's host name, or its IPv4 or global IPv6 address.";
				return false;
			}

			string path = parsed.AbsolutePath;
			if (!path.EndsWith("/", StringComparison.Ordinal))
			{
				path += "/";
			}

			string port = parsed.IsDefaultPort ? string.Empty : ":" + parsed.Port;

			if (!Uri.TryCreate(parsed.Scheme + "://" + host + port + path, UriKind.Absolute, out Uri result))
			{
				error = "The OctoPrint address is not a valid URL.";
				return false;
			}

			baseUri = result;
			return true;
		}

		/// <summary>
		/// True when the path of an absolute address (text after "scheme://") has a "." or ".." segment,
		/// including percent-encoded forms such as "%2e%2e". Uri would silently collapse these.
		/// </summary>
		private static bool HasDotSegment(string text)
		{
			string rest = text.Substring(text.IndexOf("://", StringComparison.Ordinal) + 3);
			int pathStart = rest.IndexOfAny(new[] { '/', '\\', '?', '#' });
			if (pathStart < 0 || rest[pathStart] == '?' || rest[pathStart] == '#')
			{
				return false;
			}

			string path = rest.Substring(pathStart);
			int pathEnd = path.IndexOfAny(new[] { '?', '#' });
			if (pathEnd >= 0)
			{
				path = path.Substring(0, pathEnd);
			}

			foreach (string segment in path.Split('/', '\\'))
			{
				string decoded = Uri.UnescapeDataString(segment);
				if (decoded == "." || decoded == "..")
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// A stable key for storing settings (such as the API key) per OctoPrint server.
		/// Expects a URI produced by <see cref="TryNormalize"/>.
		/// </summary>
		public static string Key(Uri baseUri)
		{
			if (baseUri == null)
			{
				throw new ArgumentNullException(nameof(baseUri));
			}

			return baseUri.AbsoluteUri;
		}
	}
}
