using System;
using System.IO;
using System.Linq;
using System.Text;
using MatterHackers.MatterControl.OctoPrint;
using NUnit.Framework;

namespace EBMC.Tests
{
	[TestFixture]
	public class SanitizeRemoteNameTests
	{
		[TestCase("a/b.gcode", "b.gcode")]
		[TestCase("C:\\prints\\part.gcode", "part.gcode")]
		[TestCase(";rm *.gcode", "rm_.gcode")]
		[TestCase(".hidden", "hidden.gcode")]
		[TestCase("...", "print.gcode")]
		[TestCase("My Part (v2).stl", "My_Part_(v2).stl.gcode")]
		[TestCase("part.gcode", "part.gcode")]
		[TestCase("part.GCO", "part.GCO")]
		[TestCase("part.g", "part.g")]
		[TestCase("a|b&c$d?e<f>g:h\"i", "abcdefghi.gcode")]
		[TestCase("tab\there  and   there.gcode", "tab_here_and_there.gcode")]
		[TestCase("\u0001ctl\u007f.gcode", "ctl.gcode")]
		[TestCase("Würfel 日本.gcode", "Würfel_日本.gcode")]
		[TestCase("Ünïcödé", "Ünïcödé.gcode")]
		[TestCase("", "print.gcode")]
		[TestCase("   ", "print.gcode")]
		[TestCase(null, "print.gcode")]
		[TestCase("dir/", "print.gcode")]
		public void Sanitizes(string input, string expected)
		{
			Assert.That(OctoPrintClient.SanitizeRemoteName(input), Is.EqualTo(expected));
		}

		[TestCase("a.", "a.gcode")]
		[TestCase("a...", "a.gcode")]
		[TestCase("a..gcode", "a.gcode")]
		[TestCase("a. ", "a.gcode")]
		public void TrailingDotsBeforeTheExtensionAreRemoved(string input, string expected)
		{
			Assert.That(OctoPrintClient.SanitizeRemoteName(input), Is.EqualTo(expected));
		}

		[Test]
		public void LongNameIsCappedKeepingTheExtension()
		{
			string result = OctoPrintClient.SanitizeRemoteName(new string('a', 250) + ".gcode");
			Assert.That(Encoding.UTF8.GetByteCount(result), Is.EqualTo(OctoPrintClient.MaxRemoteNameBytes));
			Assert.That(result, Is.EqualTo(new string('a', 194) + ".gcode"));
		}

		[Test]
		public void LongNameWithoutExtensionIsCappedAndGetsGcode()
		{
			string result = OctoPrintClient.SanitizeRemoteName(new string('b', 250));
			Assert.That(Encoding.UTF8.GetByteCount(result), Is.EqualTo(OctoPrintClient.MaxRemoteNameBytes));
			Assert.That(result, Does.EndWith(".gcode"));
		}

		[Test]
		public void CjkNameIsCappedByUtf8Bytes()
		{
			// 150 characters of 3 bytes each = 450 bytes, well over the cap although only 156 UTF-16 units long
			string result = OctoPrintClient.SanitizeRemoteName(new string('日', 150) + ".gcode");
			byte[] bytes = Encoding.UTF8.GetBytes(result);
			Assert.That(bytes.Length, Is.LessThanOrEqualTo(200));
			Assert.That(result, Does.EndWith(".gcode"));
			Assert.That(result, Is.EqualTo(new string('日', 64) + ".gcode"), "64 * 3 + 6 = 198 bytes; a 65th character would make 201");
			Assert.That(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), Is.EqualTo(result), "valid UTF-8");
		}

		[Test]
		public void EmojiNameIsCutOnACodePointBoundary()
		{
			// Each emoji is a surrogate pair (2 UTF-16 units, 4 UTF-8 bytes); 194 bytes are left for the stem
			string result = OctoPrintClient.SanitizeRemoteName(string.Concat(Enumerable.Repeat("😀", 100)) + ".gcode");
			Assert.That(result, Does.EndWith(".gcode"));
			string stem = result.Substring(0, result.Length - ".gcode".Length);
			Assert.That(stem, Is.EqualTo(string.Concat(Enumerable.Repeat("😀", 48))));
			Assert.That(char.IsHighSurrogate(stem[stem.Length - 1]), Is.False);
			Assert.That(Encoding.UTF8.GetByteCount(result), Is.LessThanOrEqualTo(200));
		}

		[Test]
		public void CapDoesNotSplitSurrogatePairs()
		{
			// 193 letters then emoji (4 bytes each) straddling the cut point
			string result = OctoPrintClient.SanitizeRemoteName(new string('c', 193) + "😀😀😀.gcode");
			Assert.That(result, Is.EqualTo(new string('c', 193) + ".gcode"));
		}

		[TestCase("part.gcode", "part_20261111-143005.gcode")]
		[TestCase("My Part.GCO", "My_Part_20261111-143005.GCO")]
		[TestCase("model.stl", "model.stl_20261111-143005.gcode")]
		[TestCase(null, "print_20261111-143005.gcode")]
		[TestCase("a.", "a_20261111-143005.gcode")]
		public void AddTimestampInsertsDateAndTimeBeforeTheExtension(string input, string expected)
		{
			Assert.That(OctoPrintClient.AddTimestamp(input, new DateTime(2026, 11, 11, 14, 30, 5)), Is.EqualTo(expected));
		}

		[Test]
		public void AddTimestampKeepsTheByteCap()
		{
			var now = new DateTime(2026, 11, 11, 14, 30, 5);
			foreach (string input in new[] { new string('日', 150) + ".gcode", string.Concat(Enumerable.Repeat("😀", 100)), new string('a', 300) + ".g" })
			{
				string result = OctoPrintClient.AddTimestamp(input, now);
				Assert.That(Encoding.UTF8.GetByteCount(result), Is.LessThanOrEqualTo(OctoPrintClient.MaxRemoteNameBytes), input);
				Assert.That(Encoding.UTF8.GetByteCount(result), Is.LessThanOrEqualTo(255), input);
				Assert.That(Path.GetFileNameWithoutExtension(result), Does.EndWith("_20261111-143005"), input);
				Assert.That(OctoPrintClient.SanitizeRemoteName(result), Is.EqualTo(result), "cleaning again (as UploadAsync does) must keep the suffix");
			}
		}
	}
}
