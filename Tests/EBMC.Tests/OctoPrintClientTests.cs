using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.MatterControl.OctoPrint;
using NUnit.Framework;

namespace EBMC.Tests
{
	[TestFixture]
	public class OctoPrintClientTests
	{
		internal const string Key = "SECRET-KEY-0123456789abcdef";

		private static readonly Uri BaseUri = new Uri("http://octopi.local/");

		private readonly List<string> tempFiles = new List<string>();

		[TearDown]
		public void DeleteTempFiles()
		{
			foreach (string file in tempFiles)
			{
				File.Delete(file);
			}

			tempFiles.Clear();
		}

		private static OctoPrintClient Client(FakeHandler handler, string key = Key, Uri baseUri = null)
		{
			return new OctoPrintClient(baseUri ?? BaseUri, key, handler);
		}

		private string TempFile(int size, string extension = ".gcode")
		{
			string path = Path.Combine(Path.GetTempPath(), "ebmc-test-" + Guid.NewGuid().ToString("N") + extension);
			var data = new byte[size];
			new Random(size).NextBytes(data);
			File.WriteAllBytes(path, data);
			tempFiles.Add(path);
			return path;
		}

		// ---------- version, auth and transport errors ----------

		[Test]
		public async Task VersionSendsApiKeyHeaderAndNoQueryKey()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, "{\"api\":\"0.1\",\"server\":\"1.11.8\",\"text\":\"OctoPrint 1.11.8\",\"extra\":{\"a\":[1,2]}}");
			using (var client = Client(handler))
			{
				VersionInfo version = await client.GetVersionAsync(CancellationToken.None);
				Assert.That(version.Api, Is.EqualTo("0.1"));
				Assert.That(version.Server, Is.EqualTo("1.11.8"));
				Assert.That(version.Text, Is.EqualTo("OctoPrint 1.11.8"));
			}

			RecordedRequest request = handler.Requests.Single();
			Assert.That(request.Method, Is.EqualTo("GET"));
			Assert.That(request.Uri.AbsoluteUri, Is.EqualTo("http://octopi.local/api/version"));
			Assert.That(request.Headers["X-Api-Key"], Is.EqualTo(Key));
			Assert.That(request.Headers["User-Agent"], Does.Contain("EBMC"));
			Assert.That(request.Uri.Query, Does.Not.Contain("apikey"));
			Assert.That(request.Uri.AbsoluteUri, Does.Not.Contain(Key));
		}

		[Test]
		public async Task RequestsKeepTheBasePath()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, "{}");
			using (var client = Client(handler, baseUri: new Uri("https://pi:8443/octoprint")))
			{
				await client.GetVersionAsync(CancellationToken.None);
			}

			Assert.That(handler.Requests.Single().Uri.AbsoluteUri, Is.EqualTo("https://pi:8443/octoprint/api/version"));
		}

		[Test]
		public async Task NoKeyMeansNoKeyHeader()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, "{}");
			using (var client = Client(handler, key: null))
			{
				await client.GetVersionAsync(CancellationToken.None);
			}

			Assert.That(handler.Requests.Single().Headers.ContainsKey("X-Api-Key"), Is.False);
		}

		[TestCase(HttpStatusCode.Forbidden)]
		[TestCase(HttpStatusCode.Unauthorized)]
		public void ForbiddenStatusMapsToForbidden(HttpStatusCode status)
		{
			var handler = new FakeHandler().Respond(status, "{\"error\":\"You don't have the permission to access the requested resource.\"}");
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Forbidden));
				Assert.That(ex.StatusCode, Is.EqualTo((int)status));
				Assert.That(ex.ServerMessage, Does.StartWith("You don't have the permission"));
			}
		}

		[TestCase(SocketError.HostNotFound, OctoPrintError.HostNotFound)]
		[TestCase(SocketError.TryAgain, OctoPrintError.HostNotFound)]
		[TestCase(SocketError.NoData, OctoPrintError.HostNotFound)]
		[TestCase(SocketError.ConnectionRefused, OctoPrintError.ConnectionRefused)]
		[TestCase(SocketError.NetworkUnreachable, OctoPrintError.ConnectionRefused)]
		[TestCase(SocketError.HostUnreachable, OctoPrintError.ConnectionRefused)]
		[TestCase(SocketError.HostDown, OctoPrintError.ConnectionRefused)]
		public void SocketErrorsAreMapped(SocketError socketError, OctoPrintError expected)
		{
			var handler = new FakeHandler().Throw(new HttpRequestException("failed", new SocketException((int)socketError)));
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(expected));
				Assert.That(ex.StatusCode, Is.Null);
				Assert.That(ex.InnerException, Is.InstanceOf<HttpRequestException>());
			}
		}

		[Test]
		public void TlsFailureIsMapped()
		{
			var handler = new FakeHandler().Throw(new HttpRequestException("ssl", new IOException("io", new AuthenticationException("bad certificate"))));
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.TlsFailure));
			}
		}

		[Test]
		public void SlowServerTimesOut()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, "{}", delay: TimeSpan.FromSeconds(10));
			using (var client = Client(handler))
			{
				client.RequestTimeout = TimeSpan.FromMilliseconds(200);
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Timeout));
			}
		}

		[Test]
		public void CallerCancellationPropagatesAsOperationCanceled()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, "{}", delay: TimeSpan.FromSeconds(10));
			using (var client = Client(handler))
			using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
			{
				var ex = Assert.CatchAsync<OperationCanceledException>(() => client.GetVersionAsync(cts.Token));
				Assert.That(ex, Is.Not.Null);
			}
		}

		[Test]
		public void UnknownStatusIsUnexpectedAndInvalidJsonIsUnexpected()
		{
			var handler = new FakeHandler().Respond((HttpStatusCode)418, "teapot").Respond(HttpStatusCode.OK, "<html>not json</html>", contentType: "text/html");
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Unexpected));
				Assert.That(ex.ServerMessage, Is.EqualTo("teapot"));

				ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Unexpected));
			}
		}

		[Test]
		public async Task UserInfoInTheBaseAddressIsDropped()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.InternalServerError, "boom", contentType: "text/plain");
			using (var client = Client(handler, baseUri: new Uri("http://member:hunter2pw@octopi.local:5000/octo/")))
			{
				Assert.That(client.BaseUri.UserInfo, Is.Empty);
				Assert.That(client.BaseUri.AbsoluteUri, Is.EqualTo("http://octopi.local:5000/octo/"));
				Assert.That(client.ToString(), Does.Not.Contain("hunter2pw").And.Not.Contain("member"));
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.ToString(), Does.Not.Contain("hunter2pw"));
			}

			Assert.That(handler.Requests.Single().Uri.AbsoluteUri, Is.EqualTo("http://octopi.local:5000/octo/api/version"));

			using (var client = Client(new FakeHandler().Respond(HttpStatusCode.OK, "{}"), baseUri: new Uri("http://member:hunter2pw@octopi.local/octo")))
			{
				Assert.That(client.ToString(), Does.Not.Contain("hunter2pw"));
				await client.GetVersionAsync(CancellationToken.None);
			}
		}

		[Test]
		public void InvalidKeyCharactersAreRejectedWithoutEchoingTheKey()
		{
			var ex = Assert.Throws<ArgumentException>(() => new OctoPrintClient(BaseUri, "abc\r\nX-Evil: 1" + Key, new FakeHandler()));
			Assert.That(ex.ToString(), Does.Not.Contain(Key));
		}

		// ---------- upload ----------

		private const string UploadResponse = "{\"files\":{\"local\":{\"name\":\"my_part_renamed.gcode\",\"origin\":\"local\",\"refs\":{\"resource\":\"http://octopi.local/api/files/local/my_part_renamed.gcode\",\"download\":\"http://octopi.local/downloads/files/local/my_part_renamed.gcode\"}}},\"done\":true,\"effectiveSelect\":true,\"effectivePrint\":false}";

		[Test]
		public async Task UploadSendsMultipartWithContentLength()
		{
			string path = TempFile(300_000);
			var progress = new RecordingProgress();
			var handler = new FakeHandler().Respond(HttpStatusCode.Created, UploadResponse);

			UploadResult result;
			using (var client = Client(handler))
			{
				result = await client.UploadAsync(path, "My Part.gcode", select: false, print: true, progress, CancellationToken.None);
			}

			RecordedRequest request = handler.Requests.Single();
			Assert.That(request.Method, Is.EqualTo("POST"));
			Assert.That(request.Uri.AbsoluteUri, Is.EqualTo("http://octopi.local/api/files/local"));
			Assert.That(request.Headers["X-Api-Key"], Is.EqualTo(Key));
			Assert.That(request.ContentLength, Is.Not.Null);
			Assert.That(request.ContentLength, Is.EqualTo(request.Body.LongLength));
			Assert.That(request.TransferEncodingChunked, Is.False);
			Assert.That(request.ContentType, Does.StartWith("multipart/form-data"));

			Dictionary<string, Part> parts = Multipart.Parse(request);
			Assert.That(parts.Keys, Is.EquivalentTo(new[] { "file", "select", "print" }));
			Assert.That(parts["file"].FileName, Is.EqualTo("My_Part.gcode"));
			Assert.That(parts["file"].Data, Is.EqualTo(File.ReadAllBytes(path)));
			Assert.That(parts["print"].Text, Is.EqualTo("true"));
			Assert.That(parts["select"].Text, Is.EqualTo("true"), "print implies select");

			Assert.That(result.Name, Is.EqualTo("my_part_renamed.gcode"));
			Assert.That(result.Origin, Is.EqualTo("local"));
			Assert.That(result.Done, Is.True);
			Assert.That(result.EffectiveSelect, Is.True);
			Assert.That(result.EffectivePrint, Is.False);

			Assert.That(progress.Values.Count, Is.GreaterThan(2));
			Assert.That(progress.Values, Is.Ordered);
			Assert.That(progress.Values.All(v => v >= 0 && v <= 1), Is.True);
			Assert.That(progress.Values.Last(), Is.EqualTo(1.0));
			Assert.That(progress.Values.Count(v => v == 1.0), Is.EqualTo(1));
		}

		[Test]
		public async Task UploadWithoutSelectOrPrintAndDefaultName()
		{
			string path = TempFile(10);
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, "{\"files\":{\"local\":{\"name\":\"x.gcode\",\"origin\":\"local\"}},\"done\":true}");
			using (var client = Client(handler))
			{
				UploadResult result = await client.UploadAsync(path, null, false, false, null, CancellationToken.None);
				Assert.That(result.Name, Is.EqualTo("x.gcode"));
				Assert.That(result.EffectivePrint, Is.False);
			}

			Dictionary<string, Part> parts = Multipart.Parse(handler.Requests.Single());
			Assert.That(parts["file"].FileName, Is.EqualTo(Path.GetFileName(path)));
			Assert.That(parts["select"].Text, Is.EqualTo("false"));
			Assert.That(parts["print"].Text, Is.EqualTo("false"));
		}

		[Test]
		public async Task UploadOfUnicodeNameSendsEncodedFileName()
		{
			string path = TempFile(10);
			var handler = new FakeHandler().Respond(HttpStatusCode.Created, UploadResponse);
			using (var client = Client(handler))
			{
				await client.UploadAsync(path, "Würfel.gcode", false, false, null, CancellationToken.None);
			}

			Assert.That(handler.Requests.Single().BodyText, Does.Contain("filename*=utf-8''W%C3%BCrfel.gcode").IgnoreCase);
		}

		[TestCase(HttpStatusCode.Conflict, OctoPrintError.FileInUse)]
		[TestCase(HttpStatusCode.UnsupportedMediaType, OctoPrintError.UnsupportedFile)]
		[TestCase(HttpStatusCode.RequestEntityTooLarge, OctoPrintError.TooLarge)]
		[TestCase(HttpStatusCode.BadRequest, OctoPrintError.BadRequest)]
		[TestCase(HttpStatusCode.NotFound, OctoPrintError.NotFound)]
		[TestCase(HttpStatusCode.BadGateway, OctoPrintError.ServerError)]
		public void UploadErrorsAreMapped(HttpStatusCode status, OctoPrintError expected)
		{
			string path = TempFile(1000);
			var progress = new RecordingProgress();
			var handler = new FakeHandler().Respond(status, "<html><body>nginx error page</body></html>", contentType: "text/html");
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.UploadAsync(path, "a.gcode", false, true, progress, CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(expected));
				Assert.That(ex.StatusCode, Is.EqualTo((int)status));
			}

			Assert.That(progress.Values.Max(), Is.LessThan(1.0), "1.0 is only reported on success");
		}

		[Test]
		public void UploadServerErrorTextIsSurfacedAndTruncated()
		{
			string path = TempFile(10);
			string longError = "Disk full. " + new string('x', 500);
			var handler = new FakeHandler().Respond(HttpStatusCode.InternalServerError, JsonSerializer.Serialize(new { error = longError }));
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.UploadAsync(path, "a.gcode", false, false, null, CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.ServerError));
				Assert.That(ex.ServerMessage, Does.StartWith("Disk full."));
				Assert.That(ex.ServerMessage.Length, Is.EqualTo(300));
				Assert.That(ex.Message, Does.Contain("Disk full."));
			}
		}

		[Test]
		public void UploadIdleTimeout()
		{
			string path = TempFile(10);
			var handler = new FakeHandler().Respond(HttpStatusCode.Created, UploadResponse, delay: TimeSpan.FromSeconds(10));
			using (var client = Client(handler))
			{
				// The handler reads the whole body before stalling, so the response window applies (see the tests below)
				client.UploadResponseTimeout = TimeSpan.FromMilliseconds(200);
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.UploadAsync(path, "a.gcode", false, false, null, CancellationToken.None, TimeSpan.FromMilliseconds(200)));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Timeout));
			}
		}

		[Test]
		public async Task SlowButProgressingUploadIsNotTimedOut()
		{
			// 20 file chunks of 80 KiB, each taking 60 ms to "send": well over 1 s in total, but never 400 ms without a read
			string path = TempFile(20 * 81920);
			var handler = new FakeHandler { BodyWriteDelay = TimeSpan.FromMilliseconds(60) }.Respond(HttpStatusCode.Created, UploadResponse);
			var started = DateTime.UtcNow;
			using (var client = Client(handler))
			{
				client.UploadResponseTimeout = TimeSpan.FromMilliseconds(2000);
				UploadResult result = await client.UploadAsync(path, "a.gcode", false, false, null, CancellationToken.None, TimeSpan.FromMilliseconds(400));
				Assert.That(result.Done, Is.True);
			}

			Assert.That(DateTime.UtcNow - started, Is.GreaterThan(TimeSpan.FromMilliseconds(1000)), "the upload took longer than the idle timeout");
			Assert.That(Multipart.Parse(handler.Requests.Single())["file"].Data, Is.EqualTo(File.ReadAllBytes(path)));
		}

		[Test]
		public async Task ServerMayTakeLongerThanTheIdleTimeoutToAnswerAfterTheBody()
		{
			string path = TempFile(1000);
			var handler = new FakeHandler().Respond(HttpStatusCode.Created, UploadResponse, delay: TimeSpan.FromMilliseconds(500));
			using (var client = Client(handler))
			{
				client.UploadResponseTimeout = TimeSpan.FromMilliseconds(2000);
				UploadResult result = await client.UploadAsync(path, "a.gcode", false, false, null, CancellationToken.None, TimeSpan.FromMilliseconds(150));
				Assert.That(result.Done, Is.True);
			}
		}

		[Test]
		public void ServerAnsweringAfterTheResponseWindowTimesOut()
		{
			string path = TempFile(1000);
			var handler = new FakeHandler().Respond(HttpStatusCode.Created, UploadResponse, delay: TimeSpan.FromSeconds(10));
			using (var client = Client(handler))
			{
				client.UploadResponseTimeout = TimeSpan.FromMilliseconds(300);
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.UploadAsync(path, "a.gcode", false, false, null, CancellationToken.None, TimeSpan.FromSeconds(30)));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Timeout));
			}
		}

		[Test]
		public void StalledUploadStillTimesOutAfterTheIdleWindow()
		{
			// The handler never reads the body, so the client sees no progress at all
			string path = TempFile(1000);
			var handler = new StallingHandler();
			using (var client = new OctoPrintClient(BaseUri, Key, handler))
			{
				client.UploadResponseTimeout = TimeSpan.FromSeconds(30);
				var started = DateTime.UtcNow;
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.UploadAsync(path, "a.gcode", false, false, null, CancellationToken.None, TimeSpan.FromMilliseconds(200)));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Timeout));
				Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(10)));
			}
		}

		[Test]
		public void UploadCallerCancellation()
		{
			string path = TempFile(10);
			var handler = new FakeHandler().Respond(HttpStatusCode.Created, UploadResponse, delay: TimeSpan.FromSeconds(10));
			using (var client = Client(handler))
			using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
			{
				Assert.CatchAsync<OperationCanceledException>(() => client.UploadAsync(path, "a.gcode", false, false, null, cts.Token));
			}
		}

		// ---------- application keys ----------

		[TestCase(HttpStatusCode.NoContent, true)]
		[TestCase(HttpStatusCode.NotFound, false)]
		[TestCase(HttpStatusCode.OK, false)]
		public async Task ProbeAppKeys(HttpStatusCode status, bool expected)
		{
			var handler = new FakeHandler().Respond(status);
			using (var client = Client(handler, key: null))
			{
				Assert.That(await client.ProbeAppKeysAsync(CancellationToken.None), Is.EqualTo(expected));
			}

			Assert.That(handler.Requests.Single().Uri.AbsolutePath, Is.EqualTo("/plugin/appkeys/probe"));
		}

		[Test]
		public async Task RequestAppKey()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.Created, "{\"app_token\":\"tok123\",\"auth_dialog\":\"http://octopi.local/plugin/appkeys/auth/tok123\"}");
			AppKeyRequest result;
			using (var client = Client(handler, key: null))
			{
				result = await client.RequestAppKeyAsync("EBMC", "kenneth", CancellationToken.None);
			}

			Assert.That(result.AppToken, Is.EqualTo("tok123"));
			Assert.That(result.AuthDialogUrl, Is.EqualTo("http://octopi.local/plugin/appkeys/auth/tok123"));

			RecordedRequest request = handler.Requests.Single();
			Assert.That(request.Method, Is.EqualTo("POST"));
			Assert.That(request.Uri.AbsolutePath, Is.EqualTo("/plugin/appkeys/request"));
			Assert.That(request.ContentType, Does.StartWith("application/json"));
			using (JsonDocument doc = JsonDocument.Parse(request.BodyText))
			{
				Assert.That(doc.RootElement.GetProperty("app").GetString(), Is.EqualTo("EBMC"));
				Assert.That(doc.RootElement.GetProperty("user").GetString(), Is.EqualTo("kenneth"));
			}
		}

		[Test]
		public async Task RequestAppKeyWithoutAuthDialog()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.Created, "{\"app_token\":\"tok123\"}");
			using (var client = Client(handler, key: null))
			{
				AppKeyRequest result = await client.RequestAppKeyAsync("EBMC", null, CancellationToken.None);
				Assert.That(result.AuthDialogUrl, Is.Null);
			}

			Assert.That(handler.Requests.Single().BodyText, Is.EqualTo("{\"app\":\"EBMC\"}"));
		}

		[Test]
		public async Task PollAppKey()
		{
			var handler = new FakeHandler()
				.Respond(HttpStatusCode.Accepted)
				.Respond(HttpStatusCode.OK, "{\"api_key\":\"NEWKEY\"}")
				.Respond(HttpStatusCode.NotFound);
			using (var client = Client(handler, key: null))
			{
				Assert.That(await client.PollAppKeyAsync("tok/1", CancellationToken.None), Is.EqualTo((AppKeyStatus.Pending, (string)null)));
				Assert.That(await client.PollAppKeyAsync("tok/1", CancellationToken.None), Is.EqualTo((AppKeyStatus.Granted, "NEWKEY")));
				Assert.That(await client.PollAppKeyAsync("tok/1", CancellationToken.None), Is.EqualTo((AppKeyStatus.Denied, (string)null)));
			}

			Assert.That(handler.Requests[0].Uri.AbsoluteUri, Is.EqualTo("http://octopi.local/plugin/appkeys/request/tok%2F1"));
		}

		[Test]
		public async Task AcquireAppKeyPollsUntilGranted()
		{
			var handler = new FakeHandler()
				.Respond(HttpStatusCode.NoContent)
				.Respond(HttpStatusCode.Created, "{\"app_token\":\"tok\",\"auth_dialog\":\"http://octopi.local/auth\"}")
				.Respond(HttpStatusCode.Accepted)
				.Respond(HttpStatusCode.Accepted)
				.Respond(HttpStatusCode.OK, "{\"api_key\":\"NEWKEY\"}");
			var status = new List<string>();
			string dialog = null;

			using (var client = Client(handler, key: null))
			{
				client.PollInterval = TimeSpan.FromMilliseconds(10);
				string key = await client.AcquireAppKeyAsync("EBMC", new SyncProgress<string>(status.Add), TimeSpan.FromSeconds(30), CancellationToken.None, url => dialog = url);
				Assert.That(key, Is.EqualTo("NEWKEY"));
			}

			Assert.That(handler.Requests.Select(r => r.Uri.AbsolutePath), Is.EqualTo(new[]
			{
				"/plugin/appkeys/probe",
				"/plugin/appkeys/request",
				"/plugin/appkeys/request/tok",
				"/plugin/appkeys/request/tok",
				"/plugin/appkeys/request/tok",
			}));
			Assert.That(dialog, Is.EqualTo("http://octopi.local/auth"));
			Assert.That(status, Is.Not.Empty);
		}

		[Test]
		public void AcquireAppKeyDenied()
		{
			var handler = new FakeHandler()
				.Respond(HttpStatusCode.NoContent)
				.Respond(HttpStatusCode.Created, "{\"app_token\":\"tok\"}")
				.Respond(HttpStatusCode.Accepted)
				.Respond(HttpStatusCode.NotFound);
			using (var client = Client(handler, key: null))
			{
				client.PollInterval = TimeSpan.FromMilliseconds(10);
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.AcquireAppKeyAsync("EBMC", null, TimeSpan.FromSeconds(30), CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.AppKeyDenied));
			}
		}

		[Test]
		public void AcquireAppKeyUnsupported()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.NotFound);
			using (var client = Client(handler, key: null))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.AcquireAppKeyAsync("EBMC", null, TimeSpan.FromSeconds(30), CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.AppKeysUnavailable));
			}

			Assert.That(handler.Requests.Count, Is.EqualTo(1));
		}

		[Test]
		public void AcquireAppKeyTimesOutAfterMaxWait()
		{
			var handler = new FakeHandler()
				.Respond(HttpStatusCode.NoContent)
				.Respond(HttpStatusCode.Created, "{\"app_token\":\"tok\"}")
				.Respond(HttpStatusCode.Accepted);
			using (var client = Client(handler, key: null))
			{
				client.PollInterval = TimeSpan.FromMilliseconds(10);
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.AcquireAppKeyAsync("EBMC", null, TimeSpan.FromMilliseconds(150), CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Timeout));
			}

			Assert.That(handler.Requests.Count, Is.GreaterThan(3));
		}

		[Test]
		public async Task AcquireAppKeyCancellationStopsPolling()
		{
			var handler = new FakeHandler()
				.Respond(HttpStatusCode.NoContent)
				.Respond(HttpStatusCode.Created, "{\"app_token\":\"tok\"}")
				.Respond(HttpStatusCode.Accepted);
			using (var client = Client(handler, key: null))
			using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
			{
				client.PollInterval = TimeSpan.FromMilliseconds(10);
				Assert.CatchAsync<OperationCanceledException>(() => client.AcquireAppKeyAsync("EBMC", null, TimeSpan.FromMinutes(5), cts.Token));

				int count = handler.Requests.Count;
				await Task.Delay(200);
				Assert.That(handler.Requests.Count, Is.EqualTo(count));
			}
		}

		// ---------- printer, job, connection ----------

		[Test]
		public async Task PrinterStateIsNullWhenNotOperational()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.Conflict, "{\"error\":\"Printer is not operational\"}");
			using (var client = Client(handler))
			{
				Assert.That(await client.GetPrinterStateAsync(CancellationToken.None), Is.Null);
			}

			Assert.That(handler.Requests.Single().Uri.PathAndQuery, Is.EqualTo("/api/printer?exclude=sd"));
		}

		[Test]
		public async Task PrinterStateParsesFlagsAndTemperatures()
		{
			const string Body = "{\"state\":{\"text\":\"Printing\",\"flags\":{\"operational\":true,\"paused\":false,\"printing\":true,\"cancelling\":false,\"pausing\":false,\"sdReady\":false,\"error\":false,\"ready\":false,\"closedOrError\":false}},"
				+ "\"temperature\":{\"tool0\":{\"actual\":214.8,\"target\":215.0,\"offset\":0},\"bed\":{\"actual\":59.9,\"target\":60,\"offset\":0}}}";
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, Body);
			using (var client = Client(handler))
			{
				PrinterState state = await client.GetPrinterStateAsync(CancellationToken.None);
				Assert.That(state.Text, Is.EqualTo("Printing"));
				Assert.That(state.Operational, Is.True);
				Assert.That(state.Printing, Is.True);
				Assert.That(state.Paused, Is.False);
				Assert.That(state.Error, Is.False);
				Assert.That(state.Ready, Is.False);
				Assert.That(state.Tool0Actual, Is.EqualTo(214.8));
				Assert.That(state.Tool0Target, Is.EqualTo(215.0));
				Assert.That(state.BedActual, Is.EqualTo(59.9));
				Assert.That(state.BedTarget, Is.EqualTo(60.0));
			}
		}

		[Test]
		public async Task PrinterStateToleratesMissingBedAndNullTargets()
		{
			const string Body = "{\"state\":{\"text\":\"Operational\",\"flags\":{\"operational\":true,\"ready\":true}},\"temperature\":{\"tool0\":{\"actual\":24.5,\"target\":null}}}";
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, Body);
			using (var client = Client(handler))
			{
				PrinterState state = await client.GetPrinterStateAsync(CancellationToken.None);
				Assert.That(state.Operational, Is.True);
				Assert.That(state.Ready, Is.True);
				Assert.That(state.Printing, Is.False);
				Assert.That(state.Tool0Actual, Is.EqualTo(24.5));
				Assert.That(state.Tool0Target, Is.Null);
				Assert.That(state.BedActual, Is.Null);
				Assert.That(state.BedTarget, Is.Null);
			}
		}

		[Test]
		public void PrinterStateOtherErrorsThrow()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.Forbidden);
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetPrinterStateAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Forbidden));
			}
		}

		[Test]
		public async Task JobWithNulls()
		{
			const string Body = "{\"job\":{\"file\":{\"name\":null,\"origin\":null,\"size\":null,\"date\":null},\"estimatedPrintTime\":null,\"filament\":null},\"progress\":{\"completion\":null,\"filepos\":null,\"printTime\":null,\"printTimeLeft\":null},\"state\":\"Operational\"}";
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, Body);
			using (var client = Client(handler))
			{
				JobInfo job = await client.GetJobAsync(CancellationToken.None);
				Assert.That(job.FileName, Is.Null);
				Assert.That(job.Completion, Is.Null);
				Assert.That(job.PrintTimeLeft, Is.Null);
				Assert.That(job.State, Is.EqualTo("Operational"));
			}
		}

		[Test]
		public async Task JobWhilePrinting()
		{
			const string Body = "{\"job\":{\"file\":{\"name\":\"part.gcode\",\"origin\":\"local\",\"size\":1234,\"date\":1700000000},\"estimatedPrintTime\":3600},\"progress\":{\"completion\":42.5,\"filepos\":524,\"printTime\":1500,\"printTimeLeft\":2100},\"state\":\"Printing\"}";
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, Body);
			using (var client = Client(handler))
			{
				JobInfo job = await client.GetJobAsync(CancellationToken.None);
				Assert.That(job.FileName, Is.EqualTo("part.gcode"));
				Assert.That(job.Completion, Is.EqualTo(42.5));
				Assert.That(job.PrintTimeLeft, Is.EqualTo(2100));
				Assert.That(job.State, Is.EqualTo("Printing"));
			}
		}

		[Test]
		public async Task JobCommands()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.NoContent).Respond(HttpStatusCode.NoContent).Respond(HttpStatusCode.Conflict, "{\"error\":\"Printer is not operational\"}");
			using (var client = Client(handler))
			{
				await client.StartJobAsync(CancellationToken.None);
				await client.CancelJobAsync(CancellationToken.None);
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.StartJobAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.NotOperational));
				Assert.That(ex.ServerMessage, Is.EqualTo("Printer is not operational"));
			}

			Assert.That(handler.Requests[0].Method, Is.EqualTo("POST"));
			Assert.That(handler.Requests[0].Uri.AbsolutePath, Is.EqualTo("/api/job"));
			Assert.That(handler.Requests[0].BodyText, Is.EqualTo("{\"command\":\"start\"}"));
			Assert.That(handler.Requests[1].BodyText, Is.EqualTo("{\"command\":\"cancel\"}"));
		}

		[Test]
		public async Task ConnectionParses()
		{
			const string Body = "{\"current\":{\"state\":\"Operational\",\"port\":\"/dev/ttyACM0\",\"baudrate\":250000,\"printerProfile\":\"_default\"},"
				+ "\"options\":{\"ports\":[\"/dev/ttyACM0\",\"/dev/ttyUSB0\"],\"baudrates\":[250000,115200],\"printerProfiles\":[{\"name\":\"Default\",\"id\":\"_default\"}],\"portPreference\":null}}";
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, Body);
			using (var client = Client(handler))
			{
				ConnectionInfo info = await client.GetConnectionAsync(CancellationToken.None);
				Assert.That(info.State, Is.EqualTo("Operational"));
				Assert.That(info.Port, Is.EqualTo("/dev/ttyACM0"));
				Assert.That(info.Baudrate, Is.EqualTo(250000));
				Assert.That(info.Ports, Is.EqualTo(new[] { "/dev/ttyACM0", "/dev/ttyUSB0" }));
			}
		}

		[Test]
		public async Task ConnectionParsesClosedState()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.OK, "{\"current\":{\"state\":\"Closed\",\"port\":null,\"baudrate\":null},\"options\":{}}");
			using (var client = Client(handler))
			{
				ConnectionInfo info = await client.GetConnectionAsync(CancellationToken.None);
				Assert.That(info.State, Is.EqualTo("Closed"));
				Assert.That(info.Port, Is.Null);
				Assert.That(info.Baudrate, Is.Null);
				Assert.That(info.Ports, Is.Empty);
			}
		}

		[Test]
		public async Task ConnectAndDisconnectPostTheRightJson()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.NoContent);
			using (var client = Client(handler))
			{
				await client.ConnectAsync("/dev/ttyACM0", 250000, CancellationToken.None);
				await client.ConnectAsync();
				await client.DisconnectAsync(CancellationToken.None);
			}

			Assert.That(handler.Requests.All(r => r.Method == "POST" && r.Uri.AbsolutePath == "/api/connection"), Is.True);
			Assert.That(handler.Requests.All(r => r.ContentType.StartsWith("application/json")), Is.True);
			Assert.That(handler.Requests[0].BodyText, Is.EqualTo("{\"command\":\"connect\",\"port\":\"/dev/ttyACM0\",\"baudrate\":250000}"));
			Assert.That(handler.Requests[1].BodyText, Is.EqualTo("{\"command\":\"connect\"}"));
			Assert.That(handler.Requests[2].BodyText, Is.EqualTo("{\"command\":\"disconnect\"}"));
		}

		[Test]
		public void ConnectWithBadPortIsBadRequest()
		{
			var handler = new FakeHandler().Respond(HttpStatusCode.BadRequest, "{\"error\":\"Invalid port: /dev/nope\"}");
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.ConnectAsync("/dev/nope"));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.BadRequest));
				Assert.That(ex.ServerMessage, Is.EqualTo("Invalid port: /dev/nope"));
			}
		}

		// ---------- secrets ----------

		[Test]
		public void ApiKeyNeverAppearsInExceptions()
		{
			string path = TempFile(100);
			var scenarios = new List<Func<OctoPrintClient, Task>>();
			var handlers = new List<FakeHandler>();
			var keys = new List<string>();

			void Add(FakeHandler handler, Func<OctoPrintClient, Task> call, string key = Key)
			{
				handlers.Add(handler);
				scenarios.Add(call);
				keys.Add(key);
			}

			// The server echoing the key back must not leak it either
			Add(new FakeHandler().Respond(HttpStatusCode.Forbidden, "{\"error\":\"Invalid API key " + Key + "\"}"), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.InternalServerError, "Traceback: X-Api-Key: " + Key), c => c.GetJobAsync(CancellationToken.None));
			Add(new FakeHandler().Throw(new HttpRequestException("failed", new SocketException((int)SocketError.HostNotFound))), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Throw(new HttpRequestException("failed", new SocketException((int)SocketError.ConnectionRefused))), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Throw(new HttpRequestException("ssl", new AuthenticationException("bad"))), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.OK, "{}", delay: TimeSpan.FromSeconds(5)), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.Conflict, "{\"error\":\"in use\"}"), c => c.UploadAsync(path, "a.gcode", true, true, null, CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.UnsupportedMediaType), c => c.UploadAsync(path, "a.gcode", true, true, null, CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.RequestEntityTooLarge), c => c.UploadAsync(path, "a.gcode", true, true, null, CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.Conflict), c => c.StartJobAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.BadRequest, Key), c => c.ConnectAsync("/dev/x", 1));
			Add(new FakeHandler().Respond(HttpStatusCode.OK, "not json " + Key), c => c.GetConnectionAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.NotFound), c => c.AcquireAppKeyAsync("EBMC", null, TimeSpan.FromSeconds(1), CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.NoContent).Respond(HttpStatusCode.Created, "{\"app_token\":\"t\"}").Respond(HttpStatusCode.NotFound), c => c.AcquireAppKeyAsync("EBMC", null, TimeSpan.FromSeconds(1), CancellationToken.None));

			// The key must be removed before the 300-character cut, or the cut can leave part of it behind
			int max = OctoPrintException.MaxServerMessageLength;
			Add(new FakeHandler().Respond(HttpStatusCode.InternalServerError, new string('x', max - 10) + Key, contentType: "text/plain"), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.InternalServerError, new string('x', max - Key.Length) + Key, contentType: "text/plain"), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.InternalServerError, new string('x', 1000) + Key, contentType: "text/plain"), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.InternalServerError, JsonSerializer.Serialize(new { error = new string('x', max - 10) + Key + new string('y', 100) })), c => c.GetVersionAsync(CancellationToken.None));
			Add(new FakeHandler().Respond(HttpStatusCode.BadRequest, "{\"error\":\"bad key \\u0053ECRET-KEY-0123456789abcdef\"}"), c => c.GetVersionAsync(CancellationToken.None));

			// A key that changes when URL-encoded, echoed back in a URL (upper- and lower-case escapes)
			const string SymbolKey = "SECRET/KEY+0123456789=abc";
			string escaped = Uri.EscapeDataString(SymbolKey);
			Add(new FakeHandler().Respond(HttpStatusCode.BadRequest, "Bad request: /api/version?apikey=" + escaped, contentType: "text/plain"), c => c.GetVersionAsync(CancellationToken.None), SymbolKey);
			Add(new FakeHandler().Respond(HttpStatusCode.BadRequest, "Bad request: /api/version?apikey=" + escaped.ToLowerInvariant(), contentType: "text/plain"), c => c.GetVersionAsync(CancellationToken.None), SymbolKey);
			Add(new FakeHandler().Respond(HttpStatusCode.BadRequest, new string('x', max - 8) + escaped, contentType: "text/plain"), c => c.GetVersionAsync(CancellationToken.None), SymbolKey);

			for (int i = 0; i < scenarios.Count; i++)
			{
				using (var client = Client(handlers[i], key: keys[i]))
				{
					client.RequestTimeout = TimeSpan.FromMilliseconds(200);
					client.PollInterval = TimeSpan.FromMilliseconds(10);
					var ex = Assert.CatchAsync<OctoPrintException>(() => scenarios[i](client), "scenario " + i);
					Assert.That(ex.Message, Does.Not.Contain(keys[i]), "scenario " + i);
					Assert.That(ex.ToString(), Does.Not.Contain(keys[i]), "scenario " + i);
					Assert.That(ex.ServerMessage ?? string.Empty, Does.Not.Contain(keys[i]), "scenario " + i);
					AssertNoKeyFragment(ex.Message, keys[i], "scenario " + i);
					AssertNoKeyFragment(ex.ToString(), keys[i], "scenario " + i);
					AssertNoKeyFragment(ex.ServerMessage ?? string.Empty, keys[i], "scenario " + i);
				}
			}

			using (var client = Client(new FakeHandler()))
			{
				Assert.That(client.ToString(), Does.Not.Contain(Key));
			}
		}

		[Test]
		public void KeyStraddlingTheTruncationPointDoesNotLeakPartially()
		{
			// Reproduces the reported leak: 290 characters of text, then the key, in a non-JSON 500 response
			var handler = new FakeHandler().Respond(HttpStatusCode.InternalServerError, new string('x', 290) + Key, contentType: "text/plain");
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.ServerError));
				Assert.That(ex.ServerMessage, Does.StartWith(new string('x', 290)));
				Assert.That(ex.ServerMessage.Length, Is.LessThanOrEqualTo(OctoPrintException.MaxServerMessageLength));
				AssertNoKeyFragment(ex.Message, Key, "message");
				AssertNoKeyFragment(ex.ServerMessage, Key, "server message");
			}
		}

		[Test]
		public void KeyInsideLongJsonErrorIsScrubbedBeforeTruncation()
		{
			string error = new string('x', 295) + Key + new string('y', 100);
			var handler = new FakeHandler().Respond(HttpStatusCode.InternalServerError, JsonSerializer.Serialize(new { error }));
			using (var client = Client(handler))
			{
				var ex = Assert.ThrowsAsync<OctoPrintException>(() => client.GetVersionAsync(CancellationToken.None));
				Assert.That(ex.ServerMessage.Length, Is.EqualTo(OctoPrintException.MaxServerMessageLength));
				Assert.That(ex.ServerMessage, Does.StartWith(new string('x', 295)));
				AssertNoKeyFragment(ex.ToString(), Key, "ToString");
			}
		}

		/// <summary>
		/// No run of 6 or more characters of the key (and so no longer one) may appear in the text.
		/// </summary>
		private static void AssertNoKeyFragment(string text, string key, string label)
		{
			const int Fragment = 6;
			for (int i = 0; i + Fragment <= key.Length; i++)
			{
				Assert.That(text, Does.Not.Contain(key.Substring(i, Fragment)), label + ": contains part of the key");
			}
		}

		// ---------- helpers ----------

		/// <summary>
		/// Accepts the request but never reads its body or answers, like a server that stopped reading.
		/// </summary>
		private class StallingHandler : HttpMessageHandler
		{
			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
				throw new InvalidOperationException("not reached");
			}
		}

		private class RecordingProgress : IProgress<double>
		{
			public List<double> Values { get; } = new List<double>();

			public void Report(double value) => Values.Add(value);
		}

		private class SyncProgress<T> : IProgress<T>
		{
			private readonly Action<T> action;

			public SyncProgress(Action<T> action) => this.action = action;

			public void Report(T value) => action(value);
		}
	}
}
