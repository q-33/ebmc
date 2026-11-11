using System;
using System.Linq;
using MatterHackers.MatterControl.OctoPrint;
using NUnit.Framework;

namespace EBMC.Tests
{
	[TestFixture]
	public class OctoPrintMessagesTests
	{
		private static readonly Uri Pi = new Uri("http://octopi.local/");

		private static readonly Func<string, string> Identity = s => s;

		// Marks every template that went through the localize delegate
		private static readonly Func<string, string> Bracket = s => "[" + s + "]";

		private static OctoPrintError[] AllErrors => Enum.GetValues(typeof(OctoPrintError)).Cast<OctoPrintError>().ToArray();

		[Test]
		public void EveryErrorKindHasText()
		{
			foreach (OctoPrintError kind in AllErrors)
			{
				string text = OctoPrintMessages.DescribeError(kind, null, Pi, Identity);
				Assert.That(text, Is.Not.Null.And.Not.Empty, "No text for OctoPrintError." + kind + "; add it to OctoPrintMessages.DescribeError");
			}
		}

		[Test]
		public void EveryErrorTextGoesThroughLocalize()
		{
			foreach (OctoPrintError kind in AllErrors)
			{
				string text = OctoPrintMessages.DescribeError(kind, "server text", Pi, Bracket);
				Assert.That(text, Does.StartWith("["), kind.ToString());
			}
		}

		[TestCaseSource(nameof(AllErrorsSource))]
		public void ExceptionOverloadIsNeverNull(OctoPrintError kind)
		{
			var exception = new OctoPrintException(kind, 500, "detail");
			Assert.That(OctoPrintMessages.DescribeError(exception, Pi, Identity), Is.Not.Null.And.Not.Empty);
			Assert.That(OctoPrintMessages.DescribeError(exception, null, null), Is.Not.Null.And.Not.Empty);
		}

		public static OctoPrintError[] AllErrorsSource() => AllErrors;

		[Test]
		public void HostNotFoundSuggestsTheIpAddressOnlyForDotLocal()
		{
			string local = OctoPrintMessages.DescribeError(OctoPrintError.HostNotFound, null, new Uri("http://octopi.local/"), Identity);
			string ip = OctoPrintMessages.DescribeError(OctoPrintError.HostNotFound, null, new Uri("http://192.168.1.50/"), Identity);

			Assert.That(local, Is.EqualTo("Couldn't find octopi.local. Check that the Raspberry Pi is powered on and on the same network as this computer. If names ending in .local don't work on this network, enter the Pi's IP address instead."));
			Assert.That(ip, Is.EqualTo("Couldn't find 192.168.1.50. Check that the Raspberry Pi is powered on and on the same network as this computer."));
		}

		[Test]
		public void HostLabelKeepsThePortAndDropsCredentials()
		{
			string text = OctoPrintMessages.DescribeError(OctoPrintError.ConnectionRefused, null, new Uri("http://user:secret@pi:5000/octoprint/"), Identity);
			Assert.That(text, Is.EqualTo("pi:5000 answered, but OctoPrint isn't running there (or the port is wrong)."));
			Assert.That(text, Does.Not.Contain("secret"));
		}

		[Test]
		public void MissingHostIsDescribedGenerically()
		{
			string text = OctoPrintMessages.DescribeError(OctoPrintError.TlsFailure, null, null, Identity);
			Assert.That(text, Does.StartWith("The connection to the OctoPrint server couldn't be secured"));
		}

		[TestCase(OctoPrintError.BadRequest)]
		[TestCase(OctoPrintError.NotFound)]
		[TestCase(OctoPrintError.ServerError)]
		[TestCase(OctoPrintError.AppKeysUnavailable)]
		[TestCase(OctoPrintError.AppKeyDenied)]
		[TestCase(OctoPrintError.Unexpected)]
		public void ServerMessageIsAppendedForGenericKinds(OctoPrintError kind)
		{
			string text = OctoPrintMessages.DescribeError(kind, "  Bad thing  ", Pi, Identity);
			Assert.That(text, Does.EndWith(" OctoPrint said: Bad thing"));

			string without = OctoPrintMessages.DescribeError(kind, "   ", Pi, Identity);
			Assert.That(without, Does.Not.Contain("OctoPrint said"));
		}

		[TestCase(OctoPrintError.Forbidden)]
		[TestCase(OctoPrintError.Timeout)]
		[TestCase(OctoPrintError.FileInUse)]
		public void ServerMessageIsNotAppendedForSpecificKinds(OctoPrintError kind)
		{
			Assert.That(OctoPrintMessages.DescribeError(kind, "detail", Pi, Identity), Does.Not.Contain("detail"));
		}

		[Test]
		public void SpecifiedWording()
		{
			Assert.That(OctoPrintMessages.DescribeError(OctoPrintError.Timeout, null, Pi, Identity), Is.EqualTo("OctoPrint didn't respond in time."));
			Assert.That(OctoPrintMessages.DescribeError(OctoPrintError.TlsFailure, null, new Uri("https://octopi.local/"), Identity),
				Is.EqualTo("The connection to octopi.local couldn't be secured because its certificate isn't trusted. OctoPi uses a self-signed certificate; on the club network use an address starting with http://"));
			Assert.That(OctoPrintMessages.DescribeError(OctoPrintError.Forbidden, null, Pi, Identity), Is.EqualTo("OctoPrint rejected the API key. Use Get Key, or paste a new key."));
			Assert.That(OctoPrintMessages.DescribeError(OctoPrintError.NotOperational, null, Pi, Identity),
				Is.EqualTo("OctoPrint is running, but the printer isn't connected to it. Connect the printer in OctoPrint and try again."));
			Assert.That(OctoPrintMessages.DescribeError(OctoPrintError.FileInUse, null, Pi, Identity), Is.EqualTo("A file with this name is being printed right now, so it can't be replaced."));
			Assert.That(OctoPrintMessages.DescribeError(OctoPrintError.UnsupportedFile, null, Pi, Identity), Is.EqualTo("OctoPrint refused the file type."));
			Assert.That(OctoPrintMessages.DescribeError(OctoPrintError.TooLarge, null, Pi, Identity), Is.EqualTo("The file is larger than OctoPrint's upload limit."));
		}

		[Test]
		public void StatusLineStates()
		{
			var version = new VersionInfo { Server = "1.11.8" };
			Assert.That(OctoPrintMessages.StatusLine(version, null, null, Identity), Is.EqualTo("OctoPrint 1.11.8 · Printer not connected"));
			Assert.That(OctoPrintMessages.StatusLine(version, new PrinterState { Operational = true, Ready = true }, null, Identity), Is.EqualTo("OctoPrint 1.11.8 · Printer ready"));
			Assert.That(OctoPrintMessages.StatusLine(version, new PrinterState { Operational = true, Printing = true }, new JobInfo { Completion = 42.7 }, Identity), Is.EqualTo("OctoPrint 1.11.8 · Printing 42%"));
			Assert.That(OctoPrintMessages.StatusLine(version, new PrinterState { Operational = true, Printing = true }, new JobInfo(), Identity), Is.EqualTo("OctoPrint 1.11.8 · Printing"));
			Assert.That(OctoPrintMessages.StatusLine(version, new PrinterState { Operational = true, Printing = true }, null, Identity), Is.EqualTo("OctoPrint 1.11.8 · Printing"));
			Assert.That(OctoPrintMessages.StatusLine(version, new PrinterState { Operational = true, Paused = true }, null, Identity), Is.EqualTo("OctoPrint 1.11.8 · Print paused"));
			Assert.That(OctoPrintMessages.StatusLine(version, new PrinterState { Error = true }, null, Identity), Is.EqualTo("OctoPrint 1.11.8 · Printer error"));
			Assert.That(OctoPrintMessages.StatusLine(version, new PrinterState(), null, Identity), Is.EqualTo("OctoPrint 1.11.8 · Printer busy"));
			Assert.That(OctoPrintMessages.StatusLine(null, null, null, Identity), Is.EqualTo("OctoPrint ? · Printer not connected"));
		}

		[Test]
		public void StatusLinePercentIsClamped()
		{
			var printing = new PrinterState { Operational = true, Printing = true };
			Assert.That(OctoPrintMessages.StatusLine(null, printing, new JobInfo { Completion = 150 }, Identity), Does.EndWith("Printing 100%"));
			Assert.That(OctoPrintMessages.StatusLine(null, printing, new JobInfo { Completion = -3 }, Identity), Does.EndWith("Printing 0%"));
		}

		[Test]
		public void UploadOutcomes()
		{
			var started = new UploadResult { Name = "part_20261111-143005.gcode", EffectivePrint = true };
			var notStarted = new UploadResult { Name = "part_20261111-143005.gcode", EffectivePrint = false };

			Assert.That(OctoPrintMessages.UploadOutcome(true, started, Identity), Is.EqualTo("Printing part_20261111-143005.gcode on OctoPrint."));
			Assert.That(OctoPrintMessages.UploadOutcome(true, notStarted, Identity),
				Is.EqualTo("Uploaded as part_20261111-143005.gcode, but the print did not start because the printer wasn't ready. Start it from OctoPrint."));
			Assert.That(OctoPrintMessages.UploadOutcome(false, notStarted, Identity), Is.EqualTo("Uploaded as part_20261111-143005.gcode."));
			Assert.That(OctoPrintMessages.UploadOutcome(false, started, Bracket), Is.EqualTo("[Uploaded as {0}.]".Replace("{0}", "part_20261111-143005.gcode")));
		}

		[TestCase(null, "")]
		[TestCase("   ", "")]
		[TestCase("short", "••••")]
		[TestCase("0123456789abcde", "••••")]
		[TestCase("0123456789ABCDEF0123456789ab1a2b", "••••1a2b")]
		[TestCase("  0123456789ABCDEF0123456789ab1a2b  ", "••••1a2b")]
		public void MaskKey(string key, string expected)
		{
			Assert.That(OctoPrintMessages.MaskKey(key), Is.EqualTo(expected));
		}

		[Test]
		public void MaskNeverShowsMoreThanFourCharacters()
		{
			string key = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345";
			string masked = OctoPrintMessages.MaskKey(key);
			Assert.That(masked, Does.Not.Contain("ABCD"));
			Assert.That(masked.Count(c => c != '•'), Is.EqualTo(4));
		}

		[TestCase("  ABCdef123-_.~  ", true, "ABCdef123-_.~")]
		[TestCase("abc def", false, null)]
		[TestCase("abc\tdef", false, null)]
		[TestCase("abcé", false, null)]
		[TestCase("", false, null)]
		[TestCase(null, false, null)]
		public void TryCleanApiKey(string input, bool ok, string expected)
		{
			Assert.That(OctoPrintMessages.TryCleanApiKey(input, out string key), Is.EqualTo(ok));
			Assert.That(key, Is.EqualTo(expected));
		}

		[TestCase("bracket.stl", "bracket")]
		[TestCase("Bracket.STL", "Bracket")]
		[TestCase("v1.2 bracket", "v1.2 bracket")]
		[TestCase("Workspace.mcx", "Workspace")]
		[TestCase("cube.gcode", "cube.gcode")]
		[TestCase(".stl", "print")]
		[TestCase("  ", "print")]
		[TestCase(null, "print")]
		public void RemoteBaseName(string itemName, string expected)
		{
			Assert.That(OctoPrintMessages.RemoteBaseName(itemName), Is.EqualTo(expected));
		}

		[TestCase("bracket.stl", "bracket_20261111-143005.gcode")]
		[TestCase("v1.2 bracket", "v1.2_bracket_20261111-143005.gcode")]
		[TestCase("cube.gcode", "cube_20261111-143005.gcode")]
		[TestCase(null, "print_20261111-143005.gcode")]
		public void RemoteNameAsTheSenderBuildsIt(string itemName, string expected)
		{
			string name = OctoPrintClient.AddTimestamp(
				OctoPrintClient.SanitizeRemoteName(OctoPrintMessages.RemoteBaseName(itemName)),
				new DateTime(2026, 11, 11, 14, 30, 5));
			Assert.That(name, Is.EqualTo(expected));
		}

		[TestCase(new[] { "Cube" }, "Cube")]
		[TestCase(new[] { "Cube", "Cube", "Cube" }, "Cube")]
		[TestCase(new[] { "bracket.stl", "bracket" }, "bracket")]
		[TestCase(new[] { "Cube", "Cylinder", "Cube" }, "Cube_and_2_more")]
		[TestCase(new[] { "Cube", "Cylinder" }, "Cube_and_1_more")]
		[TestCase(new[] { "", "  ", "Cube" }, "Cube")]
		[TestCase(new[] { "", "  ", null }, "print")]
		[TestCase(new[] { ".stl" }, "print")]
		[TestCase(new string[0], "print")]
		[TestCase(new[] { "Badge.STL", "Gear.3mf" }, "Badge_and_1_more")]
		[TestCase(new[] { "print" }, "print")]
		public void ChooseBaseName(string[] names, string expected)
		{
			Assert.That(OctoPrintMessages.ChooseBaseName(names), Is.EqualTo(expected));
		}

		[Test]
		public void ChooseBaseNameNullList()
		{
			Assert.That(OctoPrintMessages.ChooseBaseName(null), Is.EqualTo("print"));
		}

		[Test]
		public void SingleCubeGivesTheExpectedRemoteName()
		{
			string name = OctoPrintClient.AddTimestamp(
				OctoPrintClient.SanitizeRemoteName(OctoPrintMessages.ChooseBaseName(new[] { "Cube" })),
				new DateTime(2026, 11, 11, 10, 38, 11));
			Assert.That(name, Is.EqualTo("Cube_20261111-103811.gcode"));
		}

		[Test]
		public void VeryLongPartNamesStayWithinTheRemoteNameLimit()
		{
			string longName = new string('x', 500);
			string chosen = OctoPrintMessages.ChooseBaseName(new[] { longName, "other" });
			Assert.That(chosen, Does.StartWith(longName).And.EndWith("_and_1_more"));

			string remote = OctoPrintClient.AddTimestamp(OctoPrintClient.SanitizeRemoteName(chosen), new DateTime(2026, 11, 11, 10, 38, 11));
			Assert.That(System.Text.Encoding.UTF8.GetByteCount(remote), Is.LessThanOrEqualTo(OctoPrintClient.MaxRemoteNameBytes));
			Assert.That(remote, Does.EndWith("_20261111-103811.gcode"));
		}

		[Test]
		public void BrowserUriPrefersAnHttpApprovalPage()
		{
			var baseUri = new Uri("http://octopi.local/");
			Assert.That(OctoPrintMessages.BrowserUri("http://octopi.local/plugin/appkeys/auth/abc", baseUri).AbsoluteUri, Is.EqualTo("http://octopi.local/plugin/appkeys/auth/abc"));
			Assert.That(OctoPrintMessages.BrowserUri(null, baseUri), Is.SameAs(baseUri));
			Assert.That(OctoPrintMessages.BrowserUri("", baseUri), Is.SameAs(baseUri));
			Assert.That(OctoPrintMessages.BrowserUri("not a url", baseUri), Is.SameAs(baseUri));
			Assert.That(OctoPrintMessages.BrowserUri("/relative/path", baseUri), Is.SameAs(baseUri));
		}

		[TestCase("file:///Applications/Calculator.app")]
		[TestCase("javascript:alert(1)")]
		[TestCase("smb://server/share")]
		[TestCase("http://user:pw@octopi.local/plugin/appkeys/auth/abc")]
		public void BrowserUriRejectsOtherSchemesAndCredentials(string authDialogUrl)
		{
			var baseUri = new Uri("http://octopi.local/");
			Assert.That(OctoPrintMessages.BrowserUri(authDialogUrl, baseUri), Is.SameAs(baseUri));
		}
	}
}
