using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members & Projects: the pure rules (new passphrases, dates, tags, folding, validation) and source checks on
	/// the core folder. Every name here is invented.
	/// </summary>
	[TestFixture]
	public class MembersRulesTests
	{
		private static readonly DateTime Today = new DateTime(2026, 11, 16);

		[Test]
		public void ANewPassphraseNeedsEightCharactersAndTheSameRepeat()
		{
			Assert.That(MembersRules.CheckNewPassphrase("amber-lantern-42", "amber-lantern-42").IsValid, Is.True);
			Assert.That(MembersRules.CheckNewPassphrase("12345678", "12345678").IsValid, Is.True, "digits only is allowed");
			Assert.That(MembersRules.CheckNewPassphrase("        ", "        ").Problems, Is.EqualTo(new[] { MembersRules.PassphraseOnlySpaces }), "spaces only");
			Assert.That(MembersRules.CheckNewPassphrase("   \u3000    ", "   \u3000    ").Problems, Does.Contain(MembersRules.PassphraseOnlySpaces), "any white space only");
			Assert.That(MembersRules.CheckNewPassphrase(" amber lantern ", " amber lantern ").IsValid, Is.True, "spaces around and inside are kept and fine");
			Assert.That(MembersRules.CheckNewPassphrase("  ab  cd", "  ab  cd").IsValid, Is.True, "never trimmed: eight characters with the spaces");
			Assert.That(MembersRules.CheckNewPassphrase("  ab  c", "  ab  c").Problems, Is.EqualTo(new[] { MembersRules.PassphraseTooShort }));

			Assert.That(MembersRules.CheckNewPassphrase("1234567", "1234567").Problems, Is.EqualTo(new[] { MembersRules.PassphraseTooShort }));
			Assert.That(MembersRules.CheckNewPassphrase("", "").Problems, Is.EqualTo(new[] { MembersRules.PassphraseEmpty }));
			Assert.That(MembersRules.CheckNewPassphrase(null, null).Problems, Is.EqualTo(new[] { MembersRules.PassphraseEmpty }));
			Assert.That(MembersRules.CheckNewPassphrase("amber-lantern-42", "amber-lantern-43").Problems, Is.EqualTo(new[] { MembersRules.PassphraseMismatch }));
			Assert.That(MembersRules.CheckNewPassphrase("amber-lantern-42", null).Problems, Is.EqualTo(new[] { MembersRules.PassphraseMismatch }));
			Assert.That(MembersRules.CheckNewPassphrase("amber-lantern-42", "amber-lantern-42 ").Problems, Is.EqualTo(new[] { MembersRules.PassphraseMismatch }), "a trailing space is part of it");

			Assert.That(MembersRules.CheckNewPassphrase(new string('x', MembersRules.PassphraseMaxLength), new string('x', MembersRules.PassphraseMaxLength)).IsValid, Is.True);
			var tooLong = new string('x', MembersRules.PassphraseMaxLength + 1);
			Assert.That(MembersRules.CheckNewPassphrase(tooLong, tooLong).Problems, Is.EqualTo(new[] { MembersRules.PassphraseTooLong }));
		}

		[Test]
		public void ANewPassphraseHasNoControlOrBrokenCharacters()
		{
			foreach (var bad in new[] { "amber\tlantern42", "amber-lantern-42\n", "amber\u0000lantern42", "amber-lantern\u007F42", "amber-lantern-\uD800x" })
			{
				Assert.That(MembersRules.CheckNewPassphrase(bad, bad).Problems, Does.Contain(MembersRules.PassphraseInvalidCharacters));
			}

			// letters with marks, other scripts and emoji are fine
			Assert.That(MembersRules.CheckNewPassphrase("Crème brûlée ☕", "Crème brûlée ☕").IsValid, Is.True);
			Assert.That(MembersRules.CheckNewPassphrase("сова-над-рекой", "сова-над-рекой").IsValid, Is.True);
		}

		[Test]
		public void PassphrasesAreComparedAndCountedInNfc()
		{
			const string composed = "café-terrace";
			const string decomposed = "café-terrace";
			Assert.That(composed, Is.Not.EqualTo(decomposed));
			Assert.That(MembersRules.NormalizePassphrase(decomposed), Is.EqualTo(composed));
			Assert.That(MembersRules.NormalizePassphrase(composed), Is.EqualTo(composed));
			Assert.That(MembersRules.CheckNewPassphrase(composed, decomposed).IsValid, Is.True, "the same text typed two ways");

			// eight UTF-16 units but seven characters: too short
			const string seven = "café-xy";
			Assert.That(seven.Length, Is.EqualTo(8));
			Assert.That(MembersRules.CheckNewPassphrase(seven, seven).Problems, Is.EqualTo(new[] { MembersRules.PassphraseTooShort }));

			// digits, spaces and plain ASCII are left as they are; nothing is trimmed
			Assert.That(MembersRules.NormalizePassphrase(" 4821 "), Is.EqualTo(" 4821 "));
			Assert.That(MembersRules.NormalizePassphrase(""), Is.EqualTo(""));
			Assert.That(MembersRules.NormalizePassphrase(null), Is.Null);

			// a lone surrogate cannot be normalized: left as it is (entering), refused when setting (above)
			const string broken = "abc\uD800defgh";
			Assert.That(MembersRules.NormalizePassphrase(broken), Is.EqualTo(broken));
		}

		[TestCase("today", "2026-11-16")]
		[TestCase(" Today ", "2026-11-16")]
		[TestCase("yesterday", "2026-11-15")]
		[TestCase("2026-11-16", "2026-11-16")]
		[TestCase("2026-11-17", "2026-11-17")] // one day of time-zone slack
		[TestCase("1900-01-01", "1900-01-01")]
		[TestCase("2024-02-29", "2024-02-29")]
		public void TryParseDateAccepts(string text, string expected)
		{
			Assert.That(MembersRules.TryParseDate(text, Today, out var iso), Is.True);
			Assert.That(iso, Is.EqualTo(expected));
		}

		[TestCase("2026-02-30")]
		[TestCase("16/11/2026")]
		[TestCase("11/16/2026")]
		[TestCase("1800-01-01")]
		[TestCase("1899-12-31")]
		[TestCase("2026-11-18")] // tomorrow + 1
		[TestCase("2026-11-19")]
		[TestCase("2026-1-5")]
		[TestCase("tomorrow")]
		[TestCase("")]
		[TestCase("   ")]
		[TestCase(null)]
		public void TryParseDateRejects(string text)
		{
			Assert.That(MembersRules.TryParseDate(text, Today, out var iso), Is.False);
			Assert.That(iso, Is.EqualTo(""));
		}

		[Test]
		public void ParseTagsTrimsDeduplicatesAndKeepsFirstSpelling()
		{
			Assert.That(MembersRules.ParseTags("nylon, Nylon ,  CAD,,"), Is.EqualTo(new[] { "nylon", "CAD" }));
			Assert.That(MembersRules.ParseTags("  Pulse   XE , laser"), Is.EqualTo(new[] { "Pulse XE", "laser" }));
			Assert.That(MembersRules.ParseTags(""), Is.Empty);
			Assert.That(MembersRules.ParseTags(null), Is.Empty);
		}

		[Test]
		public void TagLengthAndCountLimits()
		{
			Assert.That(MembersRules.TryParseTags(new string('a', 40), out var ok, out var okValidation), Is.True);
			Assert.That(ok, Has.Count.EqualTo(1));
			Assert.That(okValidation.IsValid, Is.True);

			Assert.That(MembersRules.TryParseTags("cad, " + new string('a', 41), out _, out var tooLong), Is.False);
			Assert.That(tooLong.Problems, Does.Contain(MembersRules.TagTooLong));

			var many = string.Join(",", Enumerable.Range(1, 31).Select(i => "tag" + i));
			Assert.That(MembersRules.TryParseTags(many, out _, out var tooMany), Is.False);
			Assert.That(tooMany.Problems, Does.Contain(MembersRules.TooManyTags));

			var thirty = string.Join(",", Enumerable.Range(1, 30).Select(i => "tag" + i));
			Assert.That(MembersRules.TryParseTags(thirty, out _, out _), Is.True);
		}

		[Test]
		public void FoldIgnoresCaseAndAccents()
		{
			Assert.That(MembersRules.Fold("José"), Is.EqualTo("jose"));
			Assert.That(MembersRules.Fold("ÅNGSTRÖM Ñame"), Is.EqualTo("angstrom name"));
			Assert.That(MembersRules.Fold(null), Is.EqualTo(""));
		}

		[Test]
		public void PossibleDuplicatesByFoldedNameOrPrefix()
		{
			// invented
			var data = new MembersData
			{
				Members =
				{
					new Member { Id = "1", Name = "Robin Example" },
					new Member { Id = "2", Name = "Sam Testcase" },
					new Member { Id = "3", Name = "Rôbin" },
				},
			};

			Assert.That(MembersRules.PossibleDuplicates(data, "robin example").Select(m => m.Id), Is.EquivalentTo(new[] { "1", "3" }));
			Assert.That(MembersRules.PossibleDuplicates(data, "Robin").Select(m => m.Id), Is.EquivalentTo(new[] { "1", "3" }));
			Assert.That(MembersRules.PossibleDuplicates(data, "Sam  Testcase Jr").Select(m => m.Id), Is.EquivalentTo(new[] { "2" }));
			Assert.That(MembersRules.PossibleDuplicates(data, "Alex Placeholder"), Is.Empty);
			Assert.That(MembersRules.PossibleDuplicates(data, "  "), Is.Empty);
		}

		[Test]
		public void ValidateMemberLimits()
		{
			Assert.That(MembersRules.ValidateMember(new Member { Name = "Robin Example" }, Today).IsValid, Is.True);
			Assert.That(MembersRules.ValidateMember(new Member { Name = "   " }, Today).Problems, Does.Contain(MembersRules.NameRequired));
			Assert.That(MembersRules.ValidateMember(new Member { Name = new string('n', 100) }, Today).IsValid, Is.True);
			Assert.That(MembersRules.ValidateMember(new Member { Name = new string('n', 101) }, Today).Problems, Does.Contain(MembersRules.NameTooLong));
			Assert.That(MembersRules.ValidateMember(new Member { Name = "Robin Example", Contact = new string('c', 201) }, Today).Problems, Does.Contain(MembersRules.ContactTooLong));
			Assert.That(MembersRules.ValidateMember(new Member { Name = "Robin Example", Notes = new string('x', 10001) }, Today).Problems, Does.Contain(MembersRules.NotesTooLong));
			Assert.That(MembersRules.ValidateMember(new Member { Name = "Robin Example", MetOn = "16/11/2026" }, Today).Problems, Does.Contain(MembersRules.DateInvalid));
			Assert.That(MembersRules.ValidateMember(new Member { Name = "Robin Example", MetOn = "today" }, Today).Problems, Does.Contain(MembersRules.DateInvalid));
			Assert.That(MembersRules.ValidateMember(new Member { Name = "Robin Example", MetOn = "2026-11-01" }, Today).IsValid, Is.True);
			Assert.That(MembersRules.ValidateMember(new Member { Name = "Robin Example", Machines = { new string('m', 41) } }, Today).Problems, Does.Contain(MembersRules.TagTooLong));

			var badLog = new Member { Name = "Robin Example", Log = { new LogEntry { Date = "2026-11-16", Text = new string('l', 501) } } };
			Assert.That(MembersRules.ValidateMember(badLog, Today).Problems, Does.Contain(MembersRules.LogTextTooLong));
		}

		[Test]
		public void ValidateProjectNeedsTitleAndExistingMembers()
		{
			var ids = new HashSet<string> { "m1" };
			Assert.That(MembersRules.ValidateProject(new Project { Title = "Robot arm", MemberIds = { "m1" } }, ids, isNew: true, Today).IsValid, Is.True);
			Assert.That(MembersRules.ValidateProject(new Project { Title = " ", MemberIds = { "m1" } }, ids, true, Today).Problems, Does.Contain(MembersRules.TitleRequired));
			Assert.That(MembersRules.ValidateProject(new Project { Title = new string('t', 121), MemberIds = { "m1" } }, ids, true, Today).Problems, Does.Contain(MembersRules.TitleTooLong));
			Assert.That(MembersRules.ValidateProject(new Project { Title = "Robot arm" }, ids, true, Today).Problems, Does.Contain(MembersRules.ProjectNeedsMember));
			Assert.That(MembersRules.ValidateProject(new Project { Title = "Robot arm", MemberIds = { "nobody" } }, ids, true, Today).Problems, Does.Contain(MembersRules.UnknownMember));

			// an existing project may be left with no member
			Assert.That(MembersRules.ValidateProject(new Project { Title = "Robot arm" }, ids, isNew: false, Today).IsValid, Is.True);
		}

		[Test]
		public void MessagesPassThroughLocalize()
		{
			var result = MembersRules.ValidateMember(new Member { Name = "" }, Today);
			Assert.That(result.Message(s => "[" + s + "]"), Is.EqualTo("[" + MembersRules.NameRequired + "]"));
			Assert.That(result.Message(null), Is.EqualTo(MembersRules.NameRequired));
		}

		[TestCase("Robin Example")]
		[TestCase("José Placeholder")]
		[TestCase("Nguyễn Văn Mẫu")] // invented, two marks on one letter
		[TestCase("สมชาย ตัวอย่าง")] // invented, Thai
		[TestCase("سامي مثال")] // invented, Arabic
		[TestCase("R2")]
		[TestCase("👩🏽‍🔧 Robin")]
		public void NamesThatAreAccepted(string name)
		{
			Assert.That(MembersRules.ValidateName(name).IsValid, Is.True, name);
		}

		[TestCase("Robin\u0000Example", MembersRules.NameInvalidCharacters)]
		[TestCase("Robin\u0007", MembersRules.NameInvalidCharacters)]
		[TestCase("Robin \u202EelpmaxE", MembersRules.NameInvalidCharacters)]
		[TestCase("Robin \u2067Example\u2069", MembersRules.NameInvalidCharacters)]
		[TestCase("Robin \u200FExample", MembersRules.NameInvalidCharacters)]
		[TestCase("Ro\u0301\u0301\u0301\u0301\u0301bin", MembersRules.NameInvalidCharacters)]
		[TestCase("---", MembersRules.NameNeedsLetter)]
		[TestCase("\u3164\u3164", MembersRules.NameNeedsLetter)] // Hangul filler: a letter that shows nothing
		[TestCase("\u115F\u1160", MembersRules.NameNeedsLetter)]
		[TestCase("\uFFA0 \uFFA0", MembersRules.NameNeedsLetter)]
		[TestCase("\u3164\u200B\uFEFF", MembersRules.NameNeedsLetter)]
		[TestCase("🙂🙂", MembersRules.NameNeedsLetter)]
		[TestCase(" \t\n ", MembersRules.NameRequired)]
		public void NamesThatAreRefused(string name, string problem)
		{
			Assert.That(MembersRules.ValidateName(name).Problems, Does.Contain(problem));
		}

		[Test]
		public void NamesWithALoneSurrogateAreRefused()
		{
			// not as [TestCase]: attribute strings are stored as UTF-8, which turns a lone surrogate into U+FFFD
			Assert.That(MembersRules.ValidateName("Robin \uD800").Problems, Does.Contain(MembersRules.NameInvalidCharacters));
			Assert.That(MembersRules.ValidateName("Robin \uDC00x").Problems, Does.Contain(MembersRules.NameInvalidCharacters));
			Assert.That(MembersRules.ValidateName("Robin \uD83D\uDE42").IsValid, Is.True, "a proper pair is fine");
		}

		[Test]
		public void IdsMustBeInTheAppsFormat()
		{
			Assert.That(MembersRules.IsWellFormedId(Guid.NewGuid().ToString("N")), Is.True);
			Assert.That(MembersRules.IsWellFormedId(new string('0', 32)), Is.True);
			foreach (var bad in new[] { null, "", "robin", "Robin Example", Guid.NewGuid().ToString(), Guid.NewGuid().ToString("N").ToUpperInvariant(), new string('0', 31), new string('0', 33), new string('g', 32), new string('٣', 32) })
			{
				Assert.That(MembersRules.IsWellFormedId(bad), Is.False, bad);
			}
		}

		[Test]
		public void NameLengthCountsWhatAPersonSeesAsOneCharacter()
		{
			// 100 letters each with one accent: 200 UTF-16 units, 100 text elements
			var accented = string.Concat(Enumerable.Repeat("e\u0301", 100));
			Assert.That(MembersRules.ValidateName(accented).IsValid, Is.True);
			Assert.That(MembersRules.ValidateName(accented + "e").Problems, Does.Contain(MembersRules.NameTooLong));

			var emoji = string.Concat(Enumerable.Repeat("👩🏽‍🔧", 99)) + "R";
			Assert.That(MembersRules.ValidateName(emoji).IsValid, Is.True);
		}

		[Test]
		public void OtherTextWithALoneSurrogateIsRefusedNotChanged()
		{
			var member = new Member { Name = "Robin Example", Notes = "half \uD83D" };
			Assert.That(MembersRules.ValidateMember(member, Today).Problems, Does.Contain(MembersRules.TextInvalidCharacters));
			Assert.That(member.Notes, Is.EqualTo("half \uD83D"));
			Assert.That(MembersRules.TryParseTags("ny\uDC00lon", out _, out var tags), Is.False);
			Assert.That(tags.Problems, Does.Contain(MembersRules.TextInvalidCharacters));
		}

		[TestCase("ﬁlament", "filament")]
		[TestCase("Straße", "strasse")]
		[TestCase("ıI", "ii")]
		[TestCase("İstanbul", "istanbul")]
		[TestCase("Æsir Œuvre", "aesir oeuvre")]
		[TestCase("Øster Łódź Đặng", "oster lodz dang")]
		[TestCase("ＲＯＢＩＮ", "robin")]
		public void FoldHandlesLigaturesAndSpecialLetters(string text, string expected)
		{
			Assert.That(MembersRules.Fold(text), Is.EqualTo(expected));
		}

		[TestCase("th-TH")]
		[TestCase("ar-SA")]
		[TestCase("tr-TR")]
		public void RulesDoNotDependOnTheCurrentCulture(string culture)
		{
			var saved = System.Globalization.CultureInfo.CurrentCulture;
			var savedUi = System.Globalization.CultureInfo.CurrentUICulture;
			try
			{
				System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
				System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

				Assert.That(MembersRules.TryParseDate("2026-11-16", Today, out var iso), Is.True);
				Assert.That(iso, Is.EqualTo("2026-11-16"));
				Assert.That(MembersRules.TryParseDate("today", Today, out iso), Is.True);
				Assert.That(iso, Is.EqualTo("2026-11-16"));
				Assert.That(MembersRules.TryParseDate("٢٠٢٦-١١-١٦", Today, out _), Is.False, "Arabic-Indic digits");
				Assert.That(MembersRules.TryParseDate("๒๐๒๖-๑๑-๑๖", Today, out _), Is.False, "Thai digits");
				Assert.That(MembersRules.ToIso(Today), Is.EqualTo("2026-11-16"));
				Assert.That(MembersRules.Fold("ROBIN ISIK"), Is.EqualTo("robin isik"));
				Assert.That(MembersRules.ParseTags("Nylon, NYLON, nylon, ınox"), Is.EqualTo(new[] { "Nylon", "ınox" }));

				var data = new MembersData { Members = { new Member { Id = "i", Name = "IRIS Example", Interests = { "TIG" }, UpdatedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) } } };
				Assert.That(MembersSearch.SearchMembers(data, "iris tig", false), Has.Count.EqualTo(1));

				var json = MembersExport.ToJson(data);
				Assert.That(MembersExport.ParseJson(json).Members[0].UpdatedUtc, Is.EqualTo(data.Members[0].UpdatedUtc));
				Assert.That(json, Does.Contain("2026-11-01T00:00:00Z"));
				Assert.That(MembersExport.MembersCsv(data), Does.Contain("i,IRIS Example"));
			}
			finally
			{
				System.Globalization.CultureInfo.CurrentCulture = saved;
				System.Globalization.CultureInfo.CurrentUICulture = savedUi;
			}
		}

		[Test]
		public void CoreIsPlainSystemCodeWithNoNetworkLoggingOrPrintPath()
		{
			var folder = Path.Combine(RepositoryRoot(), "MatterControlLib", "ClubMembers", "Core");
			var files = Directory.GetFiles(folder, "*.cs");
			Assert.That(files.Select(Path.GetFileName), Is.EquivalentTo(new[]
			{
				"MemberRecords.cs", "MembersRules.cs", "MembersSearch.cs", "MembersStore.cs", "MembersCrypto.cs",
				"MembersVault.cs", "MembersExport.cs", "MembersFileException.cs", "DesignLinkResolver.cs",
			}));

			var banned = new[]
			{
				"HttpClient", "WebClient", "Socket", "Console.", "Debug.", "Trace.", "Logger", "StaticData", "OctoPrint",
				"Slice", "GCode", "PrinterConfig", "Localize", "DateOnly", "SetUnixFileMode", "MatterHackers.Agg",
				"ApplicationController", "UserSettings",
			};

			foreach (var file in files)
			{
				var source = File.ReadAllText(file);
				foreach (var word in banned)
				{
					Assert.That(source, Does.Not.Contain(word), Path.GetFileName(file) + ": " + word);
				}

				foreach (Match match in Regex.Matches(source, @"^using\s+([\w.]+)\s*;", RegexOptions.Multiline))
				{
					Assert.That(match.Groups[1].Value, Does.StartWith("System"), Path.GetFileName(file));
				}

				// the exception only ever carries a fixed problem, never text or an inner exception
				foreach (Match match in Regex.Matches(source, @"new MembersFileException\(([^)]*)\)"))
				{
					Assert.That(match.Groups[1].Value, Does.Match(@"^MembersFileProblem\.\w+$|^MembersFileProblem problem$"), Path.GetFileName(file));
				}
			}
		}

		internal static string RepositoryRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "EBMC.md")))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null, "repository root");
			return directory.FullName;
		}
	}
}
