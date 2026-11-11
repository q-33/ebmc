using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// A small client for the parts of the OctoPrint REST API that EBMC needs: checking the server, getting an
	/// API key through the Application Keys plugin, uploading (and optionally printing) G-code, and reading the
	/// printer, job and connection state. See https://docs.octoprint.org/en/master/api/.
	/// </summary>
	public sealed class OctoPrintClient : IDisposable
	{
		/// <summary>
		/// The longest name <see cref="SanitizeRemoteName"/> and <see cref="AddTimestamp"/> return, in UTF-8 bytes.
		/// The Pi's ext4 file system allows 255 bytes per name; 200 keeps well clear of that, and a name with the
		/// 16-byte date-time suffix from <see cref="AddTimestamp"/> still fits in 200.
		/// </summary>
		public const int MaxRemoteNameBytes = 200;

		private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

		private readonly Uri baseUri;

		private readonly string apiKey;

		private readonly HttpClient http;

		public OctoPrintClient(Uri baseUri, string apiKey, HttpMessageHandler handler = null)
		{
			if (baseUri == null)
			{
				throw new ArgumentNullException(nameof(baseUri));
			}

			if (!baseUri.IsAbsoluteUri)
			{
				throw new ArgumentException("The OctoPrint address must be an absolute URI.", nameof(baseUri));
			}

			// A user name and password in the address must never end up in ToString() or error text
			if (!string.IsNullOrEmpty(baseUri.UserInfo))
			{
				baseUri = new UriBuilder(baseUri) { UserName = string.Empty, Password = string.Empty }.Uri;
			}

			if (!baseUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
			{
				baseUri = new Uri(baseUri.GetLeftPart(UriPartial.Path) + "/");
			}

			this.baseUri = baseUri;
			this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

			if (this.apiKey != null)
			{
				foreach (char c in this.apiKey)
				{
					if (char.IsControl(c) || c > 0x7e)
					{
						// Deliberately does not include the key in the message
						throw new ArgumentException("The API key contains characters that are not allowed.", nameof(apiKey));
					}
				}
			}

			http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler == null)
			{
				Timeout = System.Threading.Timeout.InfiniteTimeSpan
			};
			http.DefaultRequestHeaders.UserAgent.ParseAdd("EBMC");

			if (this.apiKey != null)
			{
				http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", this.apiKey);
			}
		}

		public Uri BaseUri => baseUri;

		/// <summary>
		/// Time allowed for each ordinary (non-upload) request, including reading the response.
		/// </summary>
		internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

		/// <summary>
		/// How often <see cref="AcquireAppKeyAsync"/> polls for the user's decision. OctoPrint discards a
		/// request that has not been polled for more than 5 seconds.
		/// </summary>
		internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

		/// <summary>
		/// Time allowed for an upload after the whole file has been read and handed to the HTTP stack: for the
		/// last buffered data to drain to the server and for OctoPrint to store the file and answer.
		/// </summary>
		internal TimeSpan UploadResponseTimeout { get; set; } = TimeSpan.FromSeconds(120);

		public void Dispose()
		{
			http.Dispose();
		}

		public override string ToString()
		{
			return "OctoPrintClient " + baseUri.AbsoluteUri;
		}

		public async Task<VersionInfo> GetVersionAsync(CancellationToken cancellationToken)
		{
			RawResponse response = await SendAsync(HttpMethod.Get, "api/version", null, cancellationToken).ConfigureAwait(false);
			EnsureSuccess(response, OctoPrintError.NotOperational);

			using (JsonDocument doc = ParseJson(response))
			{
				JsonElement root = doc.RootElement;
				return new VersionInfo
				{
					Api = GetString(root, "api"),
					Server = GetString(root, "server"),
					Text = GetString(root, "text")
				};
			}
		}

		/// <summary>
		/// True when the server supports the Application Keys workflow (the probe answers 204).
		/// </summary>
		public async Task<bool> ProbeAppKeysAsync(CancellationToken cancellationToken)
		{
			RawResponse response = await SendAsync(HttpMethod.Get, "plugin/appkeys/probe", null, cancellationToken).ConfigureAwait(false);
			return response.Status == 204;
		}

		public async Task<AppKeyRequest> RequestAppKeyAsync(string app, string user, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(app))
			{
				throw new ArgumentException("An application name is required.", nameof(app));
			}

			var body = new Dictionary<string, string> { ["app"] = app };
			if (!string.IsNullOrWhiteSpace(user))
			{
				body["user"] = user;
			}

			RawResponse response = await SendAsync(HttpMethod.Post, "plugin/appkeys/request", JsonContent(body), cancellationToken).ConfigureAwait(false);
			EnsureSuccess(response, OctoPrintError.NotOperational);

			using (JsonDocument doc = ParseJson(response))
			{
				string token = GetString(doc.RootElement, "app_token");
				if (string.IsNullOrEmpty(token))
				{
					throw new OctoPrintException(OctoPrintError.Unexpected, response.Status, "The response did not contain an app_token.");
				}

				return new AppKeyRequest
				{
					AppToken = token,
					AuthDialogUrl = GetString(doc.RootElement, "auth_dialog")
				};
			}
		}

		public async Task<(AppKeyStatus status, string apiKey)> PollAppKeyAsync(string appToken, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(appToken))
			{
				throw new ArgumentException("An app token is required.", nameof(appToken));
			}

			RawResponse response = await SendAsync(HttpMethod.Get, "plugin/appkeys/request/" + Uri.EscapeDataString(appToken), null, cancellationToken).ConfigureAwait(false);
			switch (response.Status)
			{
				case 202:
					return (AppKeyStatus.Pending, null);

				case 404:
					return (AppKeyStatus.Denied, null);

				case 200:
					using (JsonDocument doc = ParseJson(response))
					{
						string key = GetString(doc.RootElement, "api_key");
						if (string.IsNullOrEmpty(key))
						{
							throw new OctoPrintException(OctoPrintError.Unexpected, response.Status, "The response did not contain an api_key.");
						}

						return (AppKeyStatus.Granted, key);
					}

				default:
					throw MapError(null, response.Status, response.Body, OctoPrintError.NotOperational);
			}
		}

		/// <summary>
		/// Runs the whole Application Keys workflow: probe, request, then poll about once a second until the user
		/// approves or denies the request in OctoPrint, or <paramref name="maxWait"/> elapses.
		/// </summary>
		/// <returns>The new API key.</returns>
		public async Task<string> AcquireAppKeyAsync(string app, IProgress<string> status, TimeSpan maxWait, CancellationToken cancellationToken, Action<string> onAuthDialogUrl = null)
		{
			status?.Report("Checking that OctoPrint can issue an API key");
			if (!await ProbeAppKeysAsync(cancellationToken).ConfigureAwait(false))
			{
				throw new OctoPrintException(OctoPrintError.AppKeysUnavailable, null, null);
			}

			AppKeyRequest request = await RequestAppKeyAsync(app, null, cancellationToken).ConfigureAwait(false);
			if (!string.IsNullOrEmpty(request.AuthDialogUrl))
			{
				onAuthDialogUrl?.Invoke(request.AuthDialogUrl);
			}

			status?.Report("Waiting for the request to be approved in OctoPrint");

			var stopwatch = Stopwatch.StartNew();
			while (true)
			{
				(AppKeyStatus decision, string key) = await PollAppKeyAsync(request.AppToken, cancellationToken).ConfigureAwait(false);
				if (decision == AppKeyStatus.Granted)
				{
					status?.Report("Access granted");
					return key;
				}

				if (decision == AppKeyStatus.Denied)
				{
					throw new OctoPrintException(OctoPrintError.AppKeyDenied, 404, null);
				}

				if (stopwatch.Elapsed >= maxWait)
				{
					throw new OctoPrintException(OctoPrintError.Timeout, null, "No decision was made in time.");
				}

				await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Uploads a file to OctoPrint's local storage. A file with the same name is overwritten.
		/// </summary>
		/// <param name="localPath">The file to send.</param>
		/// <param name="remoteName">The name to store it under; it is cleaned with <see cref="SanitizeRemoteName"/>.
		/// Null uses the local file name.</param>
		/// <param name="select">Select the file for printing after the upload.</param>
		/// <param name="print">Start printing after the upload (implies select). Check
		/// <see cref="UploadResult.EffectivePrint"/>: OctoPrint does not treat a printer that cannot print as an error.</param>
		/// <param name="progress">Receives 0..1, never decreasing, and exactly 1.0 when the upload succeeded.</param>
		/// <param name="idleTimeout">While the file is being sent, give up when no data is read from it for this long
		/// (default 60 s). Once all of it has been read, the server has <see cref="UploadResponseTimeout"/> (default
		/// 120 s) to answer instead. There is no total time limit.</param>
		public async Task<UploadResult> UploadAsync(string localPath, string remoteName, bool select, bool print, IProgress<double> progress, CancellationToken cancellationToken, TimeSpan? idleTimeout = null)
		{
			if (string.IsNullOrEmpty(localPath))
			{
				throw new ArgumentException("A file to upload is required.", nameof(localPath));
			}

			string name = SanitizeRemoteName(string.IsNullOrWhiteSpace(remoteName) ? Path.GetFileName(localPath) : remoteName);
			TimeSpan idle = idleTimeout ?? DefaultIdleTimeout;
			TimeSpan responseWait = UploadResponseTimeout;

			var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

			using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
			using (var content = new MultipartFormDataContent("----EBMCBoundary" + Guid.NewGuid().ToString("N")))
			{
				var progressStream = new ProgressReadStream(fileStream, progress, allRead => ResetTimer(idleCts, allRead ? responseWait : idle));

				var fileContent = new StreamContent(progressStream, 81920);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
				fileContent.Headers.ContentDisposition = FormDataDisposition("file", name);
				content.Add(fileContent);

				content.Add(TextPart("select", select || print ? "true" : "false"));
				content.Add(TextPart("print", print ? "true" : "false"));

				// Makes sure the length is known up front, so the request is sent with Content-Length
				// rather than chunked (OctoPrint requires Content-Length for uploads)
				if (content.Headers.ContentLength == null)
				{
					throw new InvalidOperationException("Could not determine the upload size.");
				}

				ResetTimer(idleCts, idle);
				RawResponse response = await SendCoreAsync(HttpMethod.Post, "api/files/local", content, idleCts.Token, cancellationToken).ConfigureAwait(false);
				EnsureSuccess(response, OctoPrintError.FileInUse);

				UploadResult result;
				using (JsonDocument doc = ParseJson(response))
				{
					JsonElement root = doc.RootElement;
					result = new UploadResult
					{
						Name = GetString(root, "files", "local", "name") ?? name,
						Origin = GetString(root, "files", "local", "origin") ?? "local",
						Done = GetBool(root, "done") ?? false,
						EffectiveSelect = GetBool(root, "effectiveSelect") ?? false,
						EffectivePrint = GetBool(root, "effectivePrint") ?? false
					};
				}

				progressStream.ReportComplete();
				return result;
			}
		}

		/// <summary>
		/// The printer state, or null when OctoPrint is not connected to the printer (HTTP 409).
		/// </summary>
		public async Task<PrinterState> GetPrinterStateAsync(CancellationToken cancellationToken)
		{
			RawResponse response = await SendAsync(HttpMethod.Get, "api/printer?exclude=sd", null, cancellationToken).ConfigureAwait(false);
			if (response.Status == 409)
			{
				return null;
			}

			EnsureSuccess(response, OctoPrintError.NotOperational);

			using (JsonDocument doc = ParseJson(response))
			{
				JsonElement root = doc.RootElement;
				return new PrinterState
				{
					Text = GetString(root, "state", "text"),
					Operational = GetBool(root, "state", "flags", "operational") ?? false,
					Printing = GetBool(root, "state", "flags", "printing") ?? false,
					Paused = GetBool(root, "state", "flags", "paused") ?? false,
					Error = GetBool(root, "state", "flags", "error") ?? false,
					Ready = GetBool(root, "state", "flags", "ready") ?? false,
					Tool0Actual = GetDouble(root, "temperature", "tool0", "actual"),
					Tool0Target = GetDouble(root, "temperature", "tool0", "target"),
					BedActual = GetDouble(root, "temperature", "bed", "actual"),
					BedTarget = GetDouble(root, "temperature", "bed", "target")
				};
			}
		}

		public async Task<JobInfo> GetJobAsync(CancellationToken cancellationToken)
		{
			RawResponse response = await SendAsync(HttpMethod.Get, "api/job", null, cancellationToken).ConfigureAwait(false);
			EnsureSuccess(response, OctoPrintError.NotOperational);

			using (JsonDocument doc = ParseJson(response))
			{
				JsonElement root = doc.RootElement;
				return new JobInfo
				{
					FileName = GetString(root, "job", "file", "name"),
					Completion = GetDouble(root, "progress", "completion"),
					PrintTimeLeft = GetDouble(root, "progress", "printTimeLeft"),
					State = GetString(root, "state")
				};
			}
		}

		public async Task<ConnectionInfo> GetConnectionAsync(CancellationToken cancellationToken)
		{
			RawResponse response = await SendAsync(HttpMethod.Get, "api/connection", null, cancellationToken).ConfigureAwait(false);
			EnsureSuccess(response, OctoPrintError.NotOperational);

			using (JsonDocument doc = ParseJson(response))
			{
				JsonElement root = doc.RootElement;
				var info = new ConnectionInfo
				{
					State = GetString(root, "current", "state"),
					Port = GetString(root, "current", "port"),
				};

				double? baud = GetDouble(root, "current", "baudrate");
				if (baud.HasValue)
				{
					info.Baudrate = (int)baud.Value;
				}

				if (TryGetPath(root, out JsonElement ports, "options", "ports") && ports.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement port in ports.EnumerateArray())
					{
						if (port.ValueKind == JsonValueKind.String)
						{
							info.Ports.Add(port.GetString());
						}
					}
				}

				return info;
			}
		}

		/// <summary>
		/// Asks OctoPrint to connect to the printer. Null port or baud rate lets OctoPrint use its defaults.
		/// </summary>
		public Task ConnectAsync(string port = null, int? baudrate = null, CancellationToken cancellationToken = default)
		{
			var body = new Dictionary<string, object> { ["command"] = "connect" };
			if (!string.IsNullOrEmpty(port))
			{
				body["port"] = port;
			}

			if (baudrate.HasValue)
			{
				body["baudrate"] = baudrate.Value;
			}

			return PostCommandAsync("api/connection", body, cancellationToken);
		}

		public Task DisconnectAsync(CancellationToken cancellationToken)
		{
			return PostCommandAsync("api/connection", new Dictionary<string, object> { ["command"] = "disconnect" }, cancellationToken);
		}

		public Task StartJobAsync(CancellationToken cancellationToken)
		{
			return PostCommandAsync("api/job", new Dictionary<string, object> { ["command"] = "start" }, cancellationToken);
		}

		public Task CancelJobAsync(CancellationToken cancellationToken)
		{
			return PostCommandAsync("api/job", new Dictionary<string, object> { ["command"] = "cancel" }, cancellationToken);
		}

		/// <summary>
		/// Makes a safe OctoPrint file name: keeps only the part after the last / or \, turns runs of whitespace into
		/// "_", removes control characters and the characters / \ ; | &amp; ? $ * &lt; &gt; : and ", removes leading
		/// dots and any dots or spaces at the end of the name before its extension, makes sure the name ends in
		/// .gcode, .gco or .g (adding .gcode otherwise) and cuts the part before the extension so the whole name is
		/// at most <see cref="MaxRemoteNameBytes"/> bytes in UTF-8, never inside a character. Returns "print.gcode"
		/// when nothing is left.
		/// </summary>
		public static string SanitizeRemoteName(string name)
		{
			const string DefaultName = "print.gcode";
			const string Removed = "/\\;|&?$*<>:\"";

			if (string.IsNullOrWhiteSpace(name))
			{
				return DefaultName;
			}

			int lastSeparator = name.LastIndexOfAny(new[] { '/', '\\' });
			if (lastSeparator >= 0)
			{
				name = name.Substring(lastSeparator + 1);
			}

			var builder = new StringBuilder(name.Length);
			bool inWhitespace = false;
			foreach (char c in name.Trim())
			{
				if (char.IsWhiteSpace(c))
				{
					if (!inWhitespace)
					{
						builder.Append('_');
						inWhitespace = true;
					}

					continue;
				}

				inWhitespace = false;
				if (char.IsControl(c) || Removed.IndexOf(c) >= 0)
				{
					continue;
				}

				builder.Append(c);
			}

			string result = builder.ToString().TrimStart('.');
			if (result.Length == 0)
			{
				return DefaultName;
			}

			string extension = Path.GetExtension(result);
			string lowerExtension = extension.ToLowerInvariant();
			string stem = result;
			if (lowerExtension == ".gcode" || lowerExtension == ".gco" || lowerExtension == ".g")
			{
				stem = result.Substring(0, result.Length - extension.Length);
			}
			else
			{
				extension = ".gcode";
			}

			return CapStem(stem, extension, string.Empty);
		}

		/// <summary>
		/// Adds the local date and time before the extension so that members' uploads do not overwrite each other:
		/// "part.gcode" at 2026-11-11 14:30:05 becomes "part_20261111-143005.gcode". The name is cleaned with
		/// <see cref="SanitizeRemoteName"/> and the part before the suffix is cut so the result, suffix (16 bytes)
		/// included, is still at most <see cref="MaxRemoteNameBytes"/> (200) bytes in UTF-8, under ext4's 255.
		/// Cleaning the result again leaves it unchanged.
		/// </summary>
		public static string AddTimestamp(string remoteName, DateTime localNow)
		{
			string name = SanitizeRemoteName(remoteName);
			string extension = Path.GetExtension(name);
			string suffix = localNow.ToString("'_'yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture);
			return CapStem(name.Substring(0, name.Length - extension.Length), extension, suffix);
		}

		/// <summary>
		/// stem + suffix + extension, with the stem cut (on a character boundary) so the whole is at most
		/// <see cref="MaxRemoteNameBytes"/> UTF-8 bytes, and without dots or spaces at the end of the stem.
		/// </summary>
		private static string CapStem(string stem, string extension, string suffix)
		{
			int maxStemBytes = MaxRemoteNameBytes - Encoding.UTF8.GetByteCount(extension) - Encoding.UTF8.GetByteCount(suffix);
			stem = TruncateUtf8(stem.TrimEnd('.', ' '), maxStemBytes).TrimEnd('.', ' ');
			if (stem.Length == 0)
			{
				stem = "print";
			}

			return stem + suffix + extension;
		}

		/// <summary>
		/// The longest start of <paramref name="text"/> that is at most <paramref name="maxBytes"/> bytes in UTF-8,
		/// never splitting a surrogate pair.
		/// </summary>
		private static string TruncateUtf8(string text, int maxBytes)
		{
			int bytes = 0;
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				int units = 1;
				int size;
				if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
				{
					units = 2;
					size = 4;
				}
				else if (c < 0x80)
				{
					size = 1;
				}
				else if (c < 0x800)
				{
					size = 2;
				}
				else
				{
					// Includes an unpaired surrogate, which UTF-8 encodes as the 3-byte replacement character
					size = 3;
				}

				if (bytes + size > maxBytes)
				{
					return text.Substring(0, i);
				}

				bytes += size;
				i += units - 1;
			}

			return text;
		}

		private static void ResetTimer(CancellationTokenSource cts, TimeSpan timeout)
		{
			try
			{
				cts.CancelAfter(timeout);
			}
			catch (ObjectDisposedException)
			{
			}
		}

		private static ContentDispositionHeaderValue FormDataDisposition(string name, string fileName = null)
		{
			var disposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"" };
			if (fileName != null)
			{
				bool ascii = true;
				var fallback = new StringBuilder(fileName.Length);
				foreach (char c in fileName)
				{
					if (c > 0x7e || c < 0x20)
					{
						ascii = false;
						fallback.Append('_');
					}
					else
					{
						fallback.Append(c);
					}
				}

				disposition.FileName = "\"" + fallback + "\"";
				if (!ascii)
				{
					// RFC 5987 encoded form, for servers that understand it
					disposition.FileNameStar = fileName;
				}
			}

			return disposition;
		}

		private static HttpContent TextPart(string name, string value)
		{
			var part = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
			part.Headers.ContentDisposition = FormDataDisposition(name);
			return part;
		}

		private static HttpContent JsonContent<T>(T body)
		{
			return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

		private async Task PostCommandAsync(string relativeUri, Dictionary<string, object> body, CancellationToken cancellationToken)
		{
			RawResponse response = await SendAsync(HttpMethod.Post, relativeUri, JsonContent(body), cancellationToken).ConfigureAwait(false);
			EnsureSuccess(response, OctoPrintError.NotOperational);
		}

		private async Task<RawResponse> SendAsync(HttpMethod method, string relativeUri, HttpContent content, CancellationToken cancellationToken)
		{
			using (content)
			using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
			{
				timeoutCts.CancelAfter(RequestTimeout);
				return await SendCoreAsync(method, relativeUri, content, timeoutCts.Token, cancellationToken).ConfigureAwait(false);
			}
		}

		private async Task<RawResponse> SendCoreAsync(HttpMethod method, string relativeUri, HttpContent content, CancellationToken effectiveToken, CancellationToken callerToken)
		{
			try
			{
				using (var request = new HttpRequestMessage(method, new Uri(baseUri, relativeUri)) { Content = content })
				using (HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveToken).ConfigureAwait(false))
				{
					string body = await response.Content.ReadAsStringAsync(effectiveToken).ConfigureAwait(false);
					return new RawResponse((int)response.StatusCode, body);
				}
			}
			catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex) when (!(ex is OctoPrintException))
			{
				throw MapError(ex, null, null, OctoPrintError.Unexpected);
			}
		}

		private void EnsureSuccess(RawResponse response, OctoPrintError conflictKind)
		{
			if (response.Status < 200 || response.Status > 299)
			{
				throw MapError(null, response.Status, response.Body, conflictKind);
			}
		}

		/// <summary>
		/// The one place that turns transport failures and HTTP error statuses into OctoPrintExceptions.
		/// Cancellation by the caller never reaches here; it propagates as OperationCanceledException.
		/// </summary>
		/// <param name="error">A transport exception, or null for an HTTP error status.</param>
		/// <param name="statusCode">The HTTP status when the server answered.</param>
		/// <param name="body">The response body when the server answered.</param>
		/// <param name="conflictKind">What a 409 means for this call.</param>
		private OctoPrintException MapError(Exception error, int? statusCode, string body, OctoPrintError conflictKind)
		{
			if (error != null)
			{
				if (error is OperationCanceledException)
				{
					return new OctoPrintException(OctoPrintError.Timeout, null, null, error);
				}

				for (Exception inner = error; inner != null; inner = inner.InnerException)
				{
					if (inner is AuthenticationException)
					{
						return new OctoPrintException(OctoPrintError.TlsFailure, null, null, error);
					}
				}

				for (Exception inner = error; inner != null; inner = inner.InnerException)
				{
					if (inner is SocketException socketException)
					{
						switch (socketException.SocketErrorCode)
						{
							case SocketError.HostNotFound:
							case SocketError.TryAgain:
							case SocketError.NoData:
								return new OctoPrintException(OctoPrintError.HostNotFound, null, null, error);

							case SocketError.ConnectionRefused:
							case SocketError.NetworkUnreachable:
							case SocketError.HostUnreachable:
							case SocketError.HostDown:
								return new OctoPrintException(OctoPrintError.ConnectionRefused, null, null, error);

							case SocketError.TimedOut:
								return new OctoPrintException(OctoPrintError.Timeout, null, null, error);
						}
					}
				}

				return new OctoPrintException(OctoPrintError.Unexpected, null, null, error);
			}

			int status = statusCode ?? 0;
			OctoPrintError kind;
			if (status == 401 || status == 403)
			{
				kind = OctoPrintError.Forbidden;
			}
			else if (status == 409)
			{
				kind = conflictKind;
			}
			else if (status == 415)
			{
				kind = OctoPrintError.UnsupportedFile;
			}
			else if (status == 400)
			{
				kind = OctoPrintError.BadRequest;
			}
			else if (status == 413)
			{
				kind = OctoPrintError.TooLarge;
			}
			else if (status == 404)
			{
				kind = OctoPrintError.NotFound;
			}
			else if (status >= 500 && status <= 599)
			{
				kind = OctoPrintError.ServerError;
			}
			else
			{
				kind = OctoPrintError.Unexpected;
			}

			// The raw body is scrubbed too, so nothing the parser decodes or quotes can carry the key
			return ServerException(kind, statusCode, ExtractServerMessage(Scrub(body)));
		}

		/// <summary>
		/// Builds an exception that carries text from the server: removes the API key from the whole text first and
		/// only then cuts it to <see cref="OctoPrintException.MaxServerMessageLength"/>, so the cut can never leave
		/// part of the key behind.
		/// </summary>
		private OctoPrintException ServerException(OctoPrintError kind, int? statusCode, string serverMessage)
		{
			string text = Scrub(serverMessage)?.Trim();
			if (text != null && text.Length > OctoPrintException.MaxServerMessageLength)
			{
				text = text.Substring(0, OctoPrintException.MaxServerMessageLength);
			}

			return new OctoPrintException(kind, statusCode, text);
		}

		/// <summary>
		/// The server's error text: the JSON "error" value, or else the whole body. Not truncated; see
		/// <see cref="ServerException"/>.
		/// </summary>
		private static string ExtractServerMessage(string body)
		{
			if (string.IsNullOrWhiteSpace(body))
			{
				return null;
			}

			try
			{
				using (JsonDocument doc = JsonDocument.Parse(body))
				{
					string error = GetString(doc.RootElement, "error");
					if (!string.IsNullOrEmpty(error))
					{
						return error;
					}
				}
			}
			catch (JsonException)
			{
			}

			return body.Trim();
		}

		/// <summary>
		/// Makes sure the API key cannot leak through text the server sends back: removes the key as it is and
		/// URL-encoded (either case of hex digits). Must run on the full text, before any truncation.
		/// </summary>
		private string Scrub(string text)
		{
			if (text == null || apiKey == null)
			{
				return text;
			}

			text = text.Replace(apiKey, "***", StringComparison.Ordinal);

			string escaped = Uri.EscapeDataString(apiKey);
			if (escaped != apiKey)
			{
				text = text.Replace(escaped, "***", StringComparison.OrdinalIgnoreCase);
			}

			return text;
		}

		private static JsonDocument ParseJson(RawResponse response)
		{
			try
			{
				return JsonDocument.Parse(string.IsNullOrWhiteSpace(response.Body) ? "{}" : response.Body);
			}
			catch (JsonException)
			{
				// The JsonException is not kept as the inner exception: its message quotes the response body
				throw new OctoPrintException(OctoPrintError.Unexpected, response.Status, "The response is not valid JSON.");
			}
		}

		private static bool TryGetPath(JsonElement element, out JsonElement result, params string[] path)
		{
			result = element;
			foreach (string name in path)
			{
				if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out result))
				{
					result = default;
					return false;
				}
			}

			return true;
		}

		private static string GetString(JsonElement element, params string[] path)
		{
			if (!TryGetPath(element, out JsonElement value, path))
			{
				return null;
			}

			switch (value.ValueKind)
			{
				case JsonValueKind.String:
					return value.GetString();
				case JsonValueKind.Number:
					return value.GetRawText();
				default:
					return null;
			}
		}

		private static double? GetDouble(JsonElement element, params string[] path)
		{
			if (!TryGetPath(element, out JsonElement value, path))
			{
				return null;
			}

			if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
			{
				return number;
			}

			if (value.ValueKind == JsonValueKind.String
				&& double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
			{
				return number;
			}

			return null;
		}

		private static bool? GetBool(JsonElement element, params string[] path)
		{
			if (!TryGetPath(element, out JsonElement value, path))
			{
				return null;
			}

			switch (value.ValueKind)
			{
				case JsonValueKind.True:
					return true;
				case JsonValueKind.False:
					return false;
				default:
					return null;
			}
		}

		private sealed class RawResponse
		{
			public RawResponse(int status, string body)
			{
				Status = status;
				Body = body;
			}

			public int Status { get; }

			public string Body { get; }
		}

		/// <summary>
		/// A read-only, seekable wrapper that reports how much of the file has been read and calls
		/// <c>onActivity</c> on every read (used to restart the idle timer), passing true once the whole file has
		/// been read. Being seekable lets HttpClient compute Content-Length, so the upload is not chunked.
		/// </summary>
		private sealed class ProgressReadStream : Stream
		{
			// Reading the last byte is not the same as the server accepting the file, so stop short of 1.0
			// until the response has arrived.
			private const double MaxWhileSending = 0.99;

			private readonly Stream inner;

			private readonly IProgress<double> progress;

			private readonly Action<bool> onActivity;

			private readonly long length;

			private double lastReported = -1;

			public ProgressReadStream(Stream inner, IProgress<double> progress, Action<bool> onActivity)
			{
				this.inner = inner;
				this.progress = progress;
				this.onActivity = onActivity;
				length = inner.Length;
			}

			public override bool CanRead => true;

			public override bool CanSeek => inner.CanSeek;

			public override bool CanWrite => false;

			public override long Length => length;

			public override long Position
			{
				get => inner.Position;
				set => inner.Position = value;
			}

			public void ReportComplete()
			{
				Report(1.0);
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				int read = inner.Read(buffer, offset, count);
				OnRead(read);
				return read;
			}

			public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			{
				int read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
				OnRead(read);
				return read;
			}

			public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
			{
				int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
				OnRead(read);
				return read;
			}

			public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

			public override void Flush()
			{
			}

			public override void SetLength(long value) => throw new NotSupportedException();

			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			protected override void Dispose(bool disposing)
			{
				if (disposing)
				{
					inner.Dispose();
				}

				base.Dispose(disposing);
			}

			private void OnRead(int read)
			{
				onActivity?.Invoke(read == 0 || inner.Position >= length);
				double fraction = length == 0 ? 1.0 : (double)inner.Position / length;
				Report(Math.Min(fraction, MaxWhileSending));
			}

			private void Report(double value)
			{
				// Never goes backwards, even if HttpClient rewinds the stream to resend it
				if (value > lastReported)
				{
					lastReported = value;
					progress?.Report(value);
				}
			}
		}
	}
}
