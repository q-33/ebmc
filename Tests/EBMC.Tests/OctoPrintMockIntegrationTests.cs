using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.MatterControl.OctoPrint;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Drives the real client against Tools/EBMC/octoprint_mock.py. Not run by default. To run:
	///   python3 Tools/EBMC/octoprint_mock.py --port 5050 --key testkey &amp;
	///   EBMC_OCTOPRINT_MOCK=http://127.0.0.1:5050 dotnet test Tests/EBMC.Tests --filter "Category=Mock"
	/// The key defaults to "testkey"; set EBMC_OCTOPRINT_MOCK_KEY to change it.
	/// </summary>
	[TestFixture]
	[Category("Mock")]
	[Explicit("Needs Tools/EBMC/octoprint_mock.py running; see the class comment")]
	public class OctoPrintMockIntegrationTests
	{
		[Test]
		public async Task UploadAndPrintAgainstMock()
		{
			string address = Environment.GetEnvironmentVariable("EBMC_OCTOPRINT_MOCK");
			if (string.IsNullOrEmpty(address))
			{
				Assert.Ignore("EBMC_OCTOPRINT_MOCK is not set");
			}

			string key = Environment.GetEnvironmentVariable("EBMC_OCTOPRINT_MOCK_KEY") ?? "testkey";
			Assert.That(OctoPrintEndpoint.TryNormalize(address, out Uri baseUri, out string error), Is.True, error);

			// 200 KB of G-code-like text with a unique marker so the hash is specific to this run
			var builder = new StringBuilder("; EBMC mock test " + Guid.NewGuid() + "\n");
			for (int i = 0; builder.Length < 200_000; i++)
			{
				builder.Append("G1 X").Append(i % 200).Append(" Y").Append(i % 150).Append(" E0.05\n");
			}

			string path = Path.Combine(Path.GetTempPath(), "ebmc-mock-" + Guid.NewGuid().ToString("N") + ".gcode");
			File.WriteAllText(path, builder.ToString());
			string localHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

			try
			{
				using (var control = new HttpClient { BaseAddress = baseUri })
				using (var client = new OctoPrintClient(baseUri, key))
				{
					VersionInfo version = await client.GetVersionAsync(CancellationToken.None);
					Assert.That(version.Server, Is.EqualTo("1.11.8"));

					// Operational and idle: the print starts
					(await control.PostAsync("mock/state?printer=operational", null)).EnsureSuccessStatusCode();
					var progress = new Progress();
					UploadResult printed = await client.UploadAsync(path, "EBMC Mock Part.gcode", false, true, progress, CancellationToken.None);
					Assert.That(printed.Name, Is.EqualTo("EBMC_Mock_Part.gcode"));
					Assert.That(printed.EffectivePrint, Is.True);
					Assert.That(printed.EffectiveSelect, Is.True);
					Assert.That(progress.Values, Is.Ordered);
					Assert.That(progress.Values.Last(), Is.EqualTo(1.0));

					PrinterState state = await client.GetPrinterStateAsync(CancellationToken.None);
					Assert.That(state.Printing, Is.True);
					JobInfo job = await client.GetJobAsync(CancellationToken.None);
					Assert.That(job.FileName, Is.EqualTo("EBMC_Mock_Part.gcode"));

					// Re-uploading the file being printed is refused
					var inUse = Assert.ThrowsAsync<OctoPrintException>(() => client.UploadAsync(path, "EBMC Mock Part.gcode", false, false, null, CancellationToken.None));
					Assert.That(inUse.Kind, Is.EqualTo(OctoPrintError.FileInUse));

					// Printer not connected: still 201, but nothing prints
					(await control.PostAsync("mock/state?printer=closed", null)).EnsureSuccessStatusCode();
					UploadResult notPrinted = await client.UploadAsync(path, "closed.gcode", true, true, null, CancellationToken.None);
					Assert.That(notPrinted.EffectivePrint, Is.False);
					Assert.That(notPrinted.EffectiveSelect, Is.False);
					Assert.That(await client.GetPrinterStateAsync(CancellationToken.None), Is.Null);

					// The server stored exactly the bytes we sent
					using (JsonDocument files = JsonDocument.Parse(await control.GetStringAsync("mock/files")))
					{
						foreach (string name in new[] { "EBMC_Mock_Part.gcode", "closed.gcode" })
						{
							JsonElement entry = files.RootElement.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
							Assert.That(entry.GetProperty("sha256").GetString(), Is.EqualTo(localHash), name);
							Assert.That(entry.GetProperty("size").GetInt64(), Is.EqualTo(new FileInfo(path).Length), name);
						}
					}
				}

				using (var wrongKey = new OctoPrintClient(baseUri, "not-the-key"))
				{
					var ex = Assert.ThrowsAsync<OctoPrintException>(() => wrongKey.GetVersionAsync(CancellationToken.None));
					Assert.That(ex.Kind, Is.EqualTo(OctoPrintError.Forbidden));
				}
			}
			finally
			{
				File.Delete(path);
			}
		}

		private class Progress : IProgress<double>
		{
			public System.Collections.Generic.List<double> Values { get; } = new System.Collections.Generic.List<double>();

			public void Report(double value)
			{
				lock (Values)
				{
					Values.Add(value);
				}
			}
		}
	}
}
