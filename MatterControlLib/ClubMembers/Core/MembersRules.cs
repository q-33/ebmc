using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// The outcome of a check. Problems are fixed English sentences (never the data that failed), so they can be
	/// translated by the app and shown as they are.
	/// </summary>
	public sealed class ValidationResult
	{
		private readonly List<string> problems = new List<string>();

		public static ValidationResult Valid => new ValidationResult();

		public bool IsValid => problems.Count == 0;

		public IReadOnlyList<string> Problems => problems;

		public static ValidationResult Invalid(string problem)
		{
			var result = new ValidationResult();
			result.Add(problem);
			return result;
		}

		public void Add(string problem)
		{
			if (!problems.Contains(problem))
			{
				problems.Add(problem);
			}
		}

		public void AddRange(ValidationResult other)
		{
			foreach (var problem in other.problems)
			{
				Add(problem);
			}
		}

		/// <summary>
		/// The problems as one text, each passed through <paramref name="localize"/>.
		/// </summary>
		public string Message(Func<string, string> localize)
		{
			localize ??= s => s;
			return string.Join(" ", problems.Select(localize));
		}
	}

	/// <summary>
	/// A value or the reason it could not be made.
	/// </summary>
	public sealed class Result<T>
	{
		private Result(T value, ValidationResult validation)
		{
			Value = value;
			Validation = validation;
		}

		public T Value { get; }

		public ValidationResult Validation { get; }

		public bool Ok => Validation.IsValid;

		public static Result<T> Success(T value) => new Result<T>(value, ValidationResult.Valid);

		public static Result<T> Failure(ValidationResult validation) => new Result<T>(default, validation);

		public static Result<T> Failure(string problem) => new Result<T>(default, ValidationResult.Invalid(problem));
	}


	/// <summary>
	/// EBMC Edition: the rules for members and projects (new passphrases, what is valid, tags, dates, folding for
	/// search, possible duplicates). Only System.*, so it is unit tested.
	/// </summary>
	public static class MembersRules
	{
		/// <summary>In text elements (what a person sees as one character), not UTF-16 units.</summary>
		public const int NameMaxLength = 100;
		public const int ContactMaxLength = 200;
		public const int NotesMaxLength = 10000;
		public const int NeedsMaxLength = 1000;
		public const int LogTextMaxLength = 500;
		public const int TagMaxLength = 40;
		public const int MaxTags = 30;
		public const int TitleMaxLength = 120;
		public const int RoleMaxLength = 40;

		/// <summary>The highest importance (three stars); 0 is none.</summary>
		public const int MaxImportance = 3;

		/// <summary>The most combining marks allowed on one base character in a name (Vietnamese needs two).</summary>
		public const int MaxMarksPerCharacter = 4;

		public const string NameRequired = "Enter a name.";
		public const string NameTooLong = "The name is too long (at most 100 characters).";
		public const string NameNeedsLetter = "The name needs at least one letter or digit.";
		public const string NameInvalidCharacters = "The name contains characters that are not allowed.";
		public const string TextInvalidCharacters = "Some text contains characters that cannot be stored.";
		public const string ContactTooLong = "The contact is too long (at most 200 characters).";
		public const string NotesTooLong = "The notes are too long (at most 10,000 characters).";
		public const string NeedsTooLong = "The needs text is too long (at most 1,000 characters).";
		public const string DateInvalid = "Enter a date as yyyy-mm-dd, \"today\" or \"yesterday\" (not in the future).";
		public const string TagTooLong = "Each tag can have at most 40 characters.";
		public const string TooManyTags = "At most 30 tags.";
		public const string LogTextRequired = "Enter the note text.";
		public const string LogTextTooLong = "The note is too long (at most 500 characters).";
		public const string ImportanceInvalid = "Choose an importance of none, one, two or three stars.";
		public const string RoleTooLong = "The role is too long (at most 40 characters).";
		public const string RoleInvalidCharacters = "The role must be one line, without tabs or other special characters.";
		public const string TitleRequired = "Enter a project title.";
		public const string TitleTooLong = "The title is too long (at most 120 characters).";
		public const string ProjectNeedsMember = "Choose at least one member for the project.";
		public const string UnknownMember = "The project names a member who is not in the list.";
		public const string NotFound = "That entry no longer exists.";
		public const string DuplicateId = "An entry with the same id already exists.";
		public const string MissingId = "An entry has no id.";
		public const string Deleted = "The entry was deleted here.";
		public const string IdInvalid = "An entry has an id this app does not make.";

		/// <summary>The most deleted ids remembered per list; the oldest are dropped.</summary>
		public const int MaxTombstones = 10000;

		private static readonly DateTime Earliest = new DateTime(1900, 1, 1);

		/// <summary>The fewest characters (text elements, after <see cref="NormalizePassphrase"/>) a new members
		/// passphrase may have. Only checked when a passphrase is set or changed, never when one is entered.</summary>
		public const int PassphraseMinLength = 8;

		/// <summary>The most characters (text elements) a new members passphrase may have.</summary>
		public const int PassphraseMaxLength = 256;

		public const string PassphraseEmpty = "Enter a passphrase.";
		public const string PassphraseOnlySpaces = "The passphrase cannot be only spaces.";
		public const string PassphraseTooShort = "The passphrase needs at least 8 characters.";
		public const string PassphraseTooLong = "The passphrase is too long (at most 256 characters).";
		public const string PassphraseInvalidCharacters = "The passphrase contains characters that are not allowed (such as tabs or line breaks).";
		public const string PassphraseMismatch = "The two passphrases are not the same.";

		/// <summary>
		/// The form of a typed members passphrase that is used as the file's key: Unicode NFC, so the same visible
		/// text typed different ways opens the same file. Digits and plain ASCII are unchanged (a file saved with an
		/// old maintainer PIN opens with that PIN). Never trimmed. Text that cannot be normalized (a lone surrogate)
		/// is returned as it is; <see cref="CheckNewPassphrase"/> refuses such text as a new passphrase. Null stays null.
		/// </summary>
		public static string NormalizePassphrase(string passphrase)
		{
			if (string.IsNullOrEmpty(passphrase))
			{
				return passphrase;
			}

			try
			{
				return passphrase.Normalize(NormalizationForm.FormC);
			}
			catch (ArgumentException)
			{
				return passphrase;
			}
		}

		/// <summary>
		/// The rules for a new members passphrase (setting it the first time, or changing it): not empty, not only
		/// white space, at least
		/// <see cref="PassphraseMinLength"/> and at most <see cref="PassphraseMaxLength"/> characters, no control
		/// characters or broken characters, and the same as <paramref name="repeat"/>. Both are compared after
		/// <see cref="NormalizePassphrase"/>; neither is trimmed. Entering an existing passphrase has no rule beyond
		/// non-empty.
		/// </summary>
		public static ValidationResult CheckNewPassphrase(string passphrase, string repeat)
		{
			if (string.IsNullOrEmpty(passphrase))
			{
				return ValidationResult.Invalid(PassphraseEmpty);
			}

			var result = new ValidationResult();
			if (string.IsNullOrWhiteSpace(passphrase))
			{
				// never trimmed: spaces inside or around a passphrase are fine, a passphrase of spaces only is not
				result.Add(PassphraseOnlySpaces);
			}

			if (!IsStorableText(passphrase) || passphrase.Any(char.IsControl))
			{
				result.Add(PassphraseInvalidCharacters);
			}

			var normalized = NormalizePassphrase(passphrase);
			int length = new StringInfo(normalized).LengthInTextElements;
			if (length < PassphraseMinLength)
			{
				result.Add(PassphraseTooShort);
			}
			else if (length > PassphraseMaxLength)
			{
				result.Add(PassphraseTooLong);
			}

			if (!string.Equals(normalized, NormalizePassphrase(repeat ?? ""), StringComparison.Ordinal))
			{
				result.Add(PassphraseMismatch);
			}

			return result;
		}

		/// <summary>
		/// Trims and collapses runs of white space into one space. Null becomes "".
		/// </summary>
		public static string CollapseWhitespace(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}

			var builder = new StringBuilder(text.Length);
			bool pendingSpace = false;
			foreach (char c in text.Trim())
			{
				if (char.IsWhiteSpace(c))
				{
					pendingSpace = true;
					continue;
				}

				if (pendingSpace)
				{
					builder.Append(' ');
					pendingSpace = false;
				}

				builder.Append(c);
			}

			return builder.ToString();
		}

		/// <summary>
		/// Splits on commas, trims and collapses white space, drops empty parts and removes duplicates ignoring case
		/// (the first spelling is kept). Lengths are not checked here; see <see cref="TryParseTags"/>.
		/// </summary>
		public static List<string> ParseTags(string commaText)
		{
			var tags = new List<string>();
			if (string.IsNullOrWhiteSpace(commaText))
			{
				return tags;
			}

			foreach (var part in commaText.Split(','))
			{
				AddTag(tags, part);
			}

			return tags;
		}

		/// <summary>
		/// <see cref="ParseTags"/> plus the length and count limits.
		/// </summary>
		public static bool TryParseTags(string commaText, out List<string> tags, out ValidationResult validation)
		{
			tags = ParseTags(commaText);
			validation = ValidateTags(tags);
			return validation.IsValid;
		}

		/// <summary>
		/// Cleans a tag list the same way as <see cref="ParseTags"/> (trim, collapse, de-duplicate).
		/// </summary>
		public static List<string> NormalizeTags(IEnumerable<string> tags)
		{
			var result = new List<string>();
			foreach (var tag in tags ?? Enumerable.Empty<string>())
			{
				AddTag(result, tag);
			}

			return result;
		}

		public static ValidationResult ValidateTags(IEnumerable<string> tags)
		{
			var result = new ValidationResult();
			var list = (tags ?? Enumerable.Empty<string>()).ToList();
			if (list.Count > MaxTags)
			{
				result.Add(TooManyTags);
			}

			if (list.Any(t => t == null || t.Length < 1 || t.Length > TagMaxLength))
			{
				result.Add(TagTooLong);
			}

			if (list.Any(t => t != null && !IsStorableText(t)))
			{
				result.Add(TextInvalidCharacters);
			}

			return result;
		}

		/// <summary>
		/// Accepts "today", "yesterday" (any case) and ISO "yyyy-MM-dd", from 1900-01-01 to one day after
		/// <paramref name="today"/> (time-zone slack). Day/month orders such as 16/11/2026 are refused: they are
		/// ambiguous in a US club. The Gregorian calendar is used whatever the current culture.
		/// </summary>
		public static bool TryParseDate(string text, DateTime today, out string iso)
		{
			iso = "";
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}

			var trimmed = text.Trim();
			DateTime date;
			if (string.Equals(trimmed, "today", StringComparison.OrdinalIgnoreCase))
			{
				date = today.Date;
			}
			else if (string.Equals(trimmed, "yesterday", StringComparison.OrdinalIgnoreCase))
			{
				date = today.Date.AddDays(-1);
			}
			else if (!TryParseIso(trimmed, out date))
			{
				return false;
			}

			if (date < Earliest || date > today.Date.AddDays(1))
			{
				return false;
			}

			iso = ToIso(date);
			return true;
		}

		public static string ToIso(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		/// <summary>
		/// Exactly "yyyy-MM-dd" with ASCII digits, Gregorian.
		/// </summary>
		public static bool TryParseIso(string text, out DateTime date)
		{
			date = default;
			if (text == null || text.Length != 10 || text[4] != '-' || text[7] != '-')
			{
				return false;
			}

			for (int i = 0; i < 10; i++)
			{
				if (i != 4 && i != 7 && (text[i] < '0' || text[i] > '9'))
				{
					return false;
				}
			}

			return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
		}

		/// <summary>
		/// Whether <paramref name="iso"/> is a stored date in the allowed range. Empty is allowed when
		/// <paramref name="allowEmpty"/>.
		/// </summary>
		public static bool IsValidStoredDate(string iso, DateTime today, bool allowEmpty)
		{
			if (string.IsNullOrEmpty(iso))
			{
				return allowEmpty;
			}

			// only the exact ISO form, not "today"
			return TryParseIso(iso, out _)
				&& TryParseDate(iso, today, out var normalized)
				&& normalized == iso;
		}

		/// <summary>
		/// Lower case without accents, for search: "José" and "jose" fold the same, so do "ﬁ" and "fi", "ß" and "ss",
		/// "ı" and "i", "Æ" and "ae". Culture-independent (the same in Turkish, Thai or Arabic settings).
		/// </summary>
		public static string Fold(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return "";
			}

			// compatibility decomposition: ligatures and full-width forms become plain letters, accents separate
			string decomposed;
			try
			{
				decomposed = s.Normalize(NormalizationForm.FormKD);
			}
			catch (ArgumentException)
			{
				// a lone surrogate cannot be normalised; fold what can be
				decomposed = ReplaceLoneSurrogates(s);
			}

			var builder = new StringBuilder(decomposed.Length);
			foreach (char c in decomposed)
			{
				var category = CharUnicodeInfo.GetUnicodeCategory(c);
				if (category == UnicodeCategory.NonSpacingMark
					|| category == UnicodeCategory.EnclosingMark)
				{
					continue;
				}

				switch (c)
				{
					case 'ß':
					case 'ẞ':
						builder.Append("ss");
						break;
					case 'ı':
						builder.Append('i');
						break;
					case 'æ':
					case 'Æ':
						builder.Append("ae");
						break;
					case 'œ':
					case 'Œ':
						builder.Append("oe");
						break;
					case 'ø':
					case 'Ø':
						builder.Append('o');
						break;
					case 'ł':
					case 'Ł':
						builder.Append('l');
						break;
					case 'đ':
					case 'Đ':
						builder.Append('d');
						break;
					default:
						builder.Append(char.ToLowerInvariant(c));
						break;
				}
			}

			return builder.ToString();
		}

		/// <summary>
		/// Members whose name may be the same person as <paramref name="name"/>: the folded names are equal, or one
		/// starts with the other. Duplicates are allowed; this is only a hint for the screen.
		/// </summary>
		public static IEnumerable<Member> PossibleDuplicates(MembersData d, string name)
		{
			var folded = Fold(CollapseWhitespace(name));
			if (folded.Length == 0 || d?.Members == null)
			{
				return Enumerable.Empty<Member>();
			}

			return d.Members.Where(m =>
			{
				var other = Fold(CollapseWhitespace(m?.Name));
				return other.Length > 0
					&& (other.StartsWith(folded, StringComparison.Ordinal) || folded.StartsWith(other, StringComparison.Ordinal));
			}).ToList();
		}

		/// <summary>
		/// Problems with a name: required; at most 100 text elements; at least one letter or digit; no control
		/// characters, no bidirectional override or isolate characters, no lone surrogates, and at most
		/// <see cref="MaxMarksPerCharacter"/> combining marks on one character.
		/// </summary>
		public static ValidationResult ValidateName(string rawName)
		{
			var result = new ValidationResult();
			var name = CollapseWhitespace(rawName);
			if (name.Length == 0)
			{
				result.Add(NameRequired);
				return result;
			}

			if (!IsStorableText(name) || HasForbiddenNameCharacters(name) || TooManyMarks(name))
			{
				result.Add(NameInvalidCharacters);
				return result;
			}

			if (new StringInfo(name).LengthInTextElements > NameMaxLength)
			{
				result.Add(NameTooLong);
			}

			bool hasVisibleLetterOrDigit = false;
			foreach (var rune in name.EnumerateRunes())
			{
				if (Rune.IsLetterOrDigit(rune) && !IsInvisible(rune.Value))
				{
					hasVisibleLetterOrDigit = true;
					break;
				}
			}

			if (!hasVisibleLetterOrDigit)
			{
				result.Add(NameNeedsLetter);
			}

			return result;
		}

		/// <summary>
		/// An importance as the list uses it (display, order and filter): a stored value outside 0 to
		/// <see cref="MaxImportance"/> (only possible in a hand-edited file) counts as the nearest end. The stored value
		/// is never changed by this.
		/// </summary>
		public static int ClampImportance(int importance) => Math.Max(0, Math.Min(MaxImportance, importance));

		/// <summary>
		/// An importance is 0 (none) to <see cref="MaxImportance"/>.
		/// </summary>
		public static ValidationResult ValidateImportance(int importance)
		{
			return importance >= 0 && importance <= MaxImportance ? ValidationResult.Valid : ValidationResult.Invalid(ImportanceInvalid);
		}

		/// <summary>
		/// Problems with a role (checked as given; <see cref="CleanMember"/> trims it first): empty is allowed; at most
		/// <see cref="RoleMaxLength"/> text elements; one line, with no control characters (tabs, line breaks) and no
		/// line or paragraph separators (U+2028, U+2029), no bidirectional override or isolate characters, no lone
		/// surrogates, and at most <see cref="MaxMarksPerCharacter"/> combining marks on one character (as for names, so
		/// 40 text elements cannot hide thousands of marks).
		/// </summary>
		public static ValidationResult ValidateRole(string role)
		{
			var result = new ValidationResult();
			if (string.IsNullOrEmpty(role))
			{
				return result;
			}

			if (!IsStorableText(role)
				|| HasForbiddenNameCharacters(role)
				|| role.IndexOf('\u2028') >= 0
				|| role.IndexOf('\u2029') >= 0
				|| TooManyMarks(role))
			{
				result.Add(RoleInvalidCharacters);
				return result;
			}

			if (new StringInfo(role).LengthInTextElements > RoleMaxLength)
			{
				result.Add(RoleTooLong);
			}

			return result;
		}

		/// <summary>
		/// False when <paramref name="text"/> has a lone (unpaired) surrogate, which cannot be stored as UTF-8.
		/// Such text is refused explicitly, never silently changed.
		/// </summary>
		public static bool IsStorableText(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return true;
			}

			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (char.IsHighSurrogate(c))
				{
					if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
					{
						return false;
					}

					i++;
				}
				else if (char.IsLowSurrogate(c))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Replaces each lone surrogate with U+FFFD. Used only when writing data that was already stored (from an
		/// older file), so a save can never fail on it; new text with a lone surrogate is refused by validation.
		/// </summary>
		public static string ReplaceLoneSurrogates(string text)
		{
			if (IsStorableText(text))
			{
				return text;
			}

			var builder = new StringBuilder(text.Length);
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
				{
					builder.Append(c).Append(text[i + 1]);
					i++;
				}
				else if (char.IsSurrogate(c))
				{
					builder.Append('�');
				}
				else
				{
					builder.Append(c);
				}
			}

			return builder.ToString();
		}

		/// <summary>
		/// Cleans a member's text the way the store does before checking it (trims, collapses the name, cleans tags).
		/// Ids and times are not touched.
		/// </summary>
		public static void CleanMember(Member member)
		{
			member.Normalize();
			member.Name = CollapseWhitespace(member.Name);
			member.Role = member.Role.Trim();
			member.Contact = member.Contact.Trim();
			member.MetOn = member.MetOn.Trim();
			member.Interests = NormalizeTags(member.Interests);
			member.Machines = NormalizeTags(member.Machines);
			foreach (var entry in member.Log)
			{
				entry.Text = entry.Text.Trim();
			}
		}

		/// <inheritdoc cref="CleanMember"/>
		public static void CleanProject(Project project)
		{
			project.Normalize();
			project.Title = CollapseWhitespace(project.Title);
			project.Needs = project.Needs.Trim();
			project.StatusSince = project.StatusSince.Trim();
			project.Started = project.Started.Trim();
			project.MemberIds = project.MemberIds.Distinct(StringComparer.Ordinal).ToList();
			project.Machines = NormalizeTags(project.Machines);
			foreach (var entry in project.Log)
			{
				entry.Text = entry.Text.Trim();
			}
		}

		/// <summary>
		/// Checks a member. <paramref name="today"/> bounds the dates (one day of slack is added). When
		/// <paramref name="original"/> is given (an edit), dates and notes the edit did not add or change are not
		/// checked again, so an edit still works after the clock moved back.
		/// </summary>
		public static ValidationResult ValidateMember(Member m, DateTime today, Member original = null)
		{
			var result = new ValidationResult();
			if (m == null)
			{
				result.Add(NotFound);
				return result;
			}

			result.AddRange(ValidateName(m.Name));

			// like the date met: a value the edit did not change is not checked again
			if (!(original != null && original.Importance == m.Importance))
			{
				result.AddRange(ValidateImportance(m.Importance));
			}

			if (!(original != null && original.Role == m.Role))
			{
				result.AddRange(ValidateRole(m.Role));
			}

			if ((m.Contact ?? "").Length > ContactMaxLength)
			{
				result.Add(ContactTooLong);
			}

			bool metOnUnchanged = original != null && original.MetOn == m.MetOn;
			if (!metOnUnchanged && !IsValidStoredDate(m.MetOn, today, allowEmpty: true))
			{
				result.Add(DateInvalid);
			}

			result.AddRange(ValidateTags(m.Interests));
			result.AddRange(ValidateTags(m.Machines));

			if ((m.Notes ?? "").Length > NotesMaxLength)
			{
				result.Add(NotesTooLong);
			}

			if (!IsStorableText(m.Contact) || !IsStorableText(m.Notes))
			{
				result.Add(TextInvalidCharacters);
			}

			foreach (var entry in m.Log ?? new List<LogEntry>())
			{
				if (!IsUnchangedEntry(entry, original?.Log))
				{
					result.AddRange(ValidateLogEntry(entry, today));
				}
			}

			return result;
		}

		/// <summary>
		/// Checks a project. A new project needs at least one member; every named member must exist. An existing
		/// project may be left with no members (when they were deleted); it is then listed under "No member".
		/// With <paramref name="original"/>, unchanged dates and notes are not checked again.
		/// </summary>
		public static ValidationResult ValidateProject(Project p, ISet<string> memberIds, bool isNew, DateTime today, Project original = null)
		{
			var result = new ValidationResult();
			if (p == null)
			{
				result.Add(NotFound);
				return result;
			}

			var title = CollapseWhitespace(p.Title);
			if (title.Length == 0)
			{
				result.Add(TitleRequired);
			}
			else if (new StringInfo(title).LengthInTextElements > TitleMaxLength)
			{
				result.Add(TitleTooLong);
			}

			var ids = p.MemberIds ?? new List<string>();
			if (isNew && ids.Count == 0)
			{
				result.Add(ProjectNeedsMember);
			}

			if (ids.Any(id => memberIds == null || !memberIds.Contains(id)))
			{
				result.Add(UnknownMember);
			}

			if ((p.Needs ?? "").Length > NeedsMaxLength)
			{
				result.Add(NeedsTooLong);
			}

			if ((p.Notes ?? "").Length > NotesMaxLength)
			{
				result.Add(NotesTooLong);
			}

			if (!IsStorableText(p.Title) || !IsStorableText(p.Needs) || !IsStorableText(p.Notes))
			{
				result.Add(TextInvalidCharacters);
			}

			result.AddRange(ValidateTags(p.Machines));

			bool sinceUnchanged = original != null && original.StatusSince == p.StatusSince;
			bool startedUnchanged = original != null && original.Started == p.Started;
			if ((!sinceUnchanged && !IsValidStoredDate(p.StatusSince, today, allowEmpty: true))
				|| (!startedUnchanged && !IsValidStoredDate(p.Started, today, allowEmpty: true)))
			{
				result.Add(DateInvalid);
			}

			foreach (var entry in p.Log ?? new List<LogEntry>())
			{
				if (!IsUnchangedEntry(entry, original?.Log))
				{
					result.AddRange(ValidateLogEntry(entry, today));
				}
			}

			return result;
		}

		public static ValidationResult ValidateLogEntry(LogEntry entry, DateTime today)
		{
			var result = new ValidationResult();
			if (entry == null)
			{
				result.Add(NotFound);
				return result;
			}

			var text = (entry.Text ?? "").Trim();
			if (text.Length == 0)
			{
				result.Add(LogTextRequired);
			}
			else if (text.Length > LogTextMaxLength)
			{
				result.Add(LogTextTooLong);
			}

			if (!IsStorableText(text))
			{
				result.Add(TextInvalidCharacters);
			}

			if (!IsValidStoredDate(entry.Date, today, allowEmpty: false))
			{
				result.Add(DateInvalid);
			}

			return result;
		}

		private static bool IsUnchangedEntry(LogEntry entry, List<LogEntry> originalLog)
		{
			return entry != null
				&& originalLog != null
				&& !string.IsNullOrEmpty(entry.Id)
				&& originalLog.Any(o => o != null && o.Id == entry.Id && o.Date == entry.Date && o.Text == entry.Text);
		}

		/// <summary>
		/// An id this app makes: 32 lower-case hexadecimal digits (a GUID without dashes). Anything else (a name,
		/// free text) is refused for new records and imports, so the deleted lists can never hold a name.
		/// </summary>
		public static bool IsWellFormedId(string id)
		{
			if (id == null || id.Length != 32)
			{
				return false;
			}

			foreach (char c in id)
			{
				if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Gives every member and project whose id is not one this app makes (from an early version) a new
		/// well-formed id, and updates the projects' member lists to match. Returns how many ids changed.
		/// <para>The new id is derived from the old one (the first 32 hex digits of SHA-256 over
		/// "ebmc-members-id-v1", the record kind and the old id), so every open before the first save gives the same
		/// ids and an export taken meanwhile still matches. If it collides with an id in use, a counter is added to
		/// the hashed text (1, 2, ...) in the order the records appear, which is also the same on every open.</para>
		/// </summary>
		public static int AssignWellFormedIds(MembersData d)
		{
			if (d == null)
			{
				return 0;
			}

			int changed = 0;
			var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
			var usedMembers = new HashSet<string>((d.Members ?? new List<Member>()).Where(m => m != null && IsWellFormedId(m.Id)).Select(m => m.Id), StringComparer.Ordinal);
			foreach (var member in d.Members ?? new List<Member>())
			{
				if (member != null && !IsWellFormedId(member.Id))
				{
					var fresh = DerivedId("member", member.Id, usedMembers);
					renamed[member.Id ?? ""] = fresh;
					member.Id = fresh;
					changed++;
				}
			}

			var usedProjects = new HashSet<string>((d.Projects ?? new List<Project>()).Where(p => p != null && IsWellFormedId(p.Id)).Select(p => p.Id), StringComparer.Ordinal);
			foreach (var project in d.Projects ?? new List<Project>())
			{
				if (project == null)
				{
					continue;
				}

				if (!IsWellFormedId(project.Id))
				{
					project.Id = DerivedId("project", project.Id, usedProjects);
					changed++;
				}

				if (renamed.Count > 0 && project.MemberIds != null)
				{
					project.MemberIds = project.MemberIds.Select(id => id != null && renamed.TryGetValue(id, out var fresh) ? fresh : id).ToList();
				}
			}

			return changed;
		}

		/// <summary>
		/// The well-formed id derived from a legacy id (see <see cref="AssignWellFormedIds"/>); added to
		/// <paramref name="used"/>.
		/// </summary>
		internal static string DerivedId(string kind, string legacyId, HashSet<string> used)
		{
			for (int attempt = 0; ; attempt++)
			{
				var text = "ebmc-members-id-v1\n" + kind + "\n" + (legacyId ?? "")
					+ (attempt == 0 ? "" : "\n" + attempt.ToString(CultureInfo.InvariantCulture));
				var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ReplaceLoneSurrogates(text)));
				var id = Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant();
				if (used.Add(id))
				{
					return id;
				}
			}
		}

		/// <summary>
		/// Adds a deletion record for a well-formed id (others are not remembered) and keeps at most
		/// <see cref="MaxTombstones"/>, dropping the oldest.
		/// </summary>
		public static void AddTombstone(List<Tombstone> list, string id, DateTime deletedUtc)
		{
			if (list == null || !IsWellFormedId(id))
			{
				return;
			}

			list.RemoveAll(t => t.Id == id);
			list.Add(new Tombstone { Id = id, DeletedUtc = deletedUtc });
			TrimTombstones(list);
		}

		/// <summary>
		/// Both lists of deleted ids together (well-formed ids only, the later time kept), without the ids in
		/// <paramref name="present"/>, at most <see cref="MaxTombstones"/> (oldest dropped).
		/// </summary>
		public static List<Tombstone> UnionTombstones(IEnumerable<Tombstone> a, IEnumerable<Tombstone> b, IEnumerable<string> present)
		{
			var keep = new HashSet<string>(present ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
			var byId = new Dictionary<string, Tombstone>(StringComparer.Ordinal);
			foreach (var t in (a ?? Enumerable.Empty<Tombstone>()).Concat(b ?? Enumerable.Empty<Tombstone>()))
			{
				if (t == null || !IsWellFormedId(t.Id) || keep.Contains(t.Id))
				{
					continue;
				}

				if (!byId.TryGetValue(t.Id, out var existing) || t.DeletedUtc > existing.DeletedUtc)
				{
					byId[t.Id] = t.Clone();
				}
			}

			var list = byId.Values.ToList();
			TrimTombstones(list);
			return list;
		}

		private static void TrimTombstones(List<Tombstone> list)
		{
			if (list.Count > MaxTombstones)
			{
				var newest = list.OrderByDescending(t => t.DeletedUtc).ThenBy(t => t.Id, StringComparer.Ordinal).Take(MaxTombstones).ToList();
				list.Clear();
				list.AddRange(newest);
			}
		}

		/// <summary>
		/// Code points that show nothing: Hangul fillers, zero-width and joiner characters, variation selectors,
		/// the byte order mark, tags and the other default-ignorable code points of Unicode.
		/// </summary>
		private static bool IsInvisible(int c)
		{
			return c == 0x00AD || c == 0x034F || c == 0x061C || c == 0x115F || c == 0x1160
				|| (c >= 0x17B4 && c <= 0x17B5) || (c >= 0x180B && c <= 0x180F)
				|| (c >= 0x200B && c <= 0x200F) || (c >= 0x202A && c <= 0x202E) || (c >= 0x2060 && c <= 0x206F)
				|| c == 0x3164 || (c >= 0xFE00 && c <= 0xFE0F) || c == 0xFEFF || c == 0xFFA0 || (c >= 0xFFF0 && c <= 0xFFF8)
				|| (c >= 0x1BCA0 && c <= 0x1BCA3) || (c >= 0x1D173 && c <= 0x1D17A) || (c >= 0xE0000 && c <= 0xE0FFF);
		}

		private static bool HasForbiddenNameCharacters(string name)
		{
			foreach (char c in name)
			{
				if (char.IsControl(c)
					|| (c >= '‪' && c <= '‮') // embeddings and overrides
					|| (c >= '⁦' && c <= '⁩') // isolates
					|| c == '‎' || c == '‏' || c == '؜') // direction marks
				{
					return true;
				}
			}

			return false;
		}

		private static bool TooManyMarks(string name)
		{
			int run = 0;
			foreach (char c in name)
			{
				var category = CharUnicodeInfo.GetUnicodeCategory(c);
				if (category == UnicodeCategory.NonSpacingMark
					|| category == UnicodeCategory.EnclosingMark
					|| category == UnicodeCategory.SpacingCombiningMark)
				{
					if (++run > MaxMarksPerCharacter)
					{
						return true;
					}
				}
				else
				{
					run = 0;
				}
			}

			return false;
		}

		private static void AddTag(List<string> tags, string raw)
		{
			var tag = CollapseWhitespace(raw);
			if (tag.Length > 0
				&& !tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
			{
				tags.Add(tag);
			}
		}
	}
}
