using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatterHackers.MatterControl.ClubMembers
{
	public enum ImportMode
	{
		/// <summary>Records are matched by id; the newer one (by UpdatedUtc) wins; new ones are added.</summary>
		Merge,

		/// <summary>Everything is replaced by the file's contents.</summary>
		Replace,
	}

	public enum ImportStatus
	{
		Imported,
		Unreadable,
		NewerVersion,

		/// <summary>More than <see cref="MembersExport.MaxImportRecords"/> records: refused, nothing changed.</summary>
		TooLarge,
	}

	public sealed class ImportResult
	{
		public ImportStatus Status { get; set; }

		public int MembersAdded { get; set; }

		public int MembersUpdated { get; set; }

		public int ProjectsAdded { get; set; }

		public int ProjectsUpdated { get; set; }

		/// <summary>Records not imported because they were deleted here (merge only).</summary>
		public int SkippedDeleted { get; set; }

		/// <summary>Records not imported because they break a rule, counted per problem. The keys are the fixed
		/// problem sentences of <see cref="MembersRules"/>, never text from the file.</summary>
		public Dictionary<string, int> SkippedInvalid { get; } = new Dictionary<string, int>();

		public int SkippedInvalidCount => SkippedInvalid.Values.Sum();

		internal void CountInvalid(ValidationResult validation)
		{
			foreach (var problem in validation.Problems)
			{
				SkippedInvalid[problem] = SkippedInvalid.TryGetValue(problem, out int n) ? n + 1 : 1;
			}
		}
	}

	/// <summary>
	/// EBMC Edition: the members data as JSON (inside the encrypted file and as an unencrypted export the owner keeps
	/// as a recovery copy) and as CSV for spreadsheets. Exports are NOT encrypted; the screen must say so.
	/// </summary>
	public static class MembersExport
	{
		/// <summary>The most members and projects together an import accepts.</summary>
		public const int MaxImportRecords = 50000;

		private static readonly JsonSerializerOptions CompactOptions = MakeOptions(indented: false);

		private static readonly JsonSerializerOptions IndentedOptions = MakeOptions(indented: true);

		/// <summary>
		/// The data as indented JSON, stamped with the current schema version.
		/// </summary>
		public static string ToJson(MembersData d) => Serialize(d, IndentedOptions);

		/// <summary>
		/// The data as compact UTF-8 JSON, for the encrypted file.
		/// </summary>
		public static byte[] ToJsonBytes(MembersData d) => Encoding.UTF8.GetBytes(Serialize(d, CompactOptions));

		/// <summary>
		/// Reads JSON written by <see cref="ToJson"/> (by this or an older version). Throws
		/// <see cref="MembersFileException"/> (fixed message, no inner exception) when it is damaged or newer.
		/// </summary>
		public static MembersData ParseJson(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
			{
				throw new MembersFileException(MembersFileProblem.Unreadable);
			}

			return ParseJson(Encoding.UTF8.GetBytes(json));
		}

		/// <inheritdoc cref="ParseJson(string)"/>
		public static MembersData ParseJson(byte[] utf8Json)
		{
			MembersData data;
			try
			{
				int version;
				using (var document = JsonDocument.Parse(utf8Json))
				{
					if (document.RootElement.ValueKind != JsonValueKind.Object
						|| !document.RootElement.TryGetProperty(nameof(MembersData.SchemaVersion), out var versionElement)
						|| !versionElement.TryGetInt32(out version))
					{
						throw new MembersFileException(MembersFileProblem.Unreadable);
					}
				}

				if (version > MembersData.CurrentSchemaVersion)
				{
					throw new MembersFileException(MembersFileProblem.NewerVersion);
				}

				if (version < 1)
				{
					throw new MembersFileException(MembersFileProblem.Unreadable);
				}

				data = JsonSerializer.Deserialize<MembersData>(utf8Json, CompactOptions);
				if (data == null)
				{
					throw new MembersFileException(MembersFileProblem.Unreadable);
				}

				data = Migrate(data, version);
			}
			catch (JsonException)
			{
				// never wrap or quote it: the message can contain the text that failed to parse
				throw new MembersFileException(MembersFileProblem.Unreadable);
			}
			catch (ArgumentException)
			{
				throw new MembersFileException(MembersFileProblem.Unreadable);
			}
			catch (InvalidOperationException)
			{
				throw new MembersFileException(MembersFileProblem.Unreadable);
			}
			catch (NotSupportedException)
			{
				throw new MembersFileException(MembersFileProblem.Unreadable);
			}
			catch (FormatException)
			{
				throw new MembersFileException(MembersFileProblem.Unreadable);
			}

			data.Normalize();
			if (!HasUniqueIds(data))
			{
				throw new MembersFileException(MembersFileProblem.Unreadable);
			}

			// records are never dropped here (this also reads the members file itself); only project links to
			// members that do not exist are removed
			data.RemoveDanglingMemberIds();
			return data;
		}

		/// <summary>
		/// Reads <paramref name="json"/> and merges it into, or replaces, <paramref name="into"/> (changed in place).
		/// <list type="bullet">
		/// <item>Every incoming record is cleaned and checked with the same rules as the store; records that break a
		/// rule, or whose id is not one this app makes, are skipped and counted per fixed reason.</item>
		/// <item>Merge matches records by id and keeps the newer one (by UpdatedUtc); it never brings back a record
		/// deleted here, and never deletes anything.</item>
		/// <item>Replace takes the file's records; the deleted lists become the union of both (minus records the file
		/// holds), so a later merge cannot bring back what was deleted here.</item>
		/// <item>More than <see cref="MaxImportRecords"/> records: refused.</item>
		/// </list>
		/// Nothing is changed unless the whole file reads. Never throws for a bad file.
		/// </summary>
		public static ImportResult ImportJson(string json, MembersData into, ImportMode mode, DateTime today)
		{
			if (into == null)
			{
				throw new ArgumentNullException(nameof(into));
			}

			MembersData incoming;
			try
			{
				incoming = ParseJson(json);
			}
			catch (MembersFileException e)
			{
				return new ImportResult
				{
					Status = e.Problem == MembersFileProblem.NewerVersion ? ImportStatus.NewerVersion : ImportStatus.Unreadable,
				};
			}

			if (incoming.Members.Count + incoming.Projects.Count > MaxImportRecords)
			{
				return new ImportResult { Status = ImportStatus.TooLarge };
			}

			into.Normalize();
			var result = new ImportResult { Status = ImportStatus.Imported };
			var validationToday = today.Date;
			bool merge = mode == ImportMode.Merge;

			var deletedMembers = new HashSet<string>(into.DeletedMembers.Select(t => t.Id), StringComparer.Ordinal);
			var deletedProjects = new HashSet<string>(into.DeletedProjects.Select(t => t.Id), StringComparer.Ordinal);

			// members first: projects are checked against the members that end up in the data
			var finalMembers = merge ? new List<Member>(into.Members) : new List<Member>();
			var memberIndex = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int i = 0; i < finalMembers.Count; i++)
			{
				memberIndex[finalMembers[i].Id] = i;
			}

			foreach (var member in incoming.Members)
			{
				if (!MembersRules.IsWellFormedId(member.Id))
				{
					result.CountInvalid(ValidationResult.Invalid(MembersRules.IdInvalid));
					continue;
				}

				if (merge && deletedMembers.Contains(member.Id))
				{
					result.SkippedDeleted++;
					continue;
				}

				MembersRules.CleanMember(member);
				var validation = MembersRules.ValidateMember(member, validationToday);
				if (!validation.IsValid)
				{
					result.CountInvalid(validation);
					continue;
				}

				if (!memberIndex.TryGetValue(member.Id, out int index))
				{
					memberIndex[member.Id] = finalMembers.Count;
					finalMembers.Add(member);
					result.MembersAdded++;
				}
				else if (member.UpdatedUtc > finalMembers[index].UpdatedUtc)
				{
					finalMembers[index] = member;
					result.MembersUpdated++;
				}
			}

			var memberIds = new HashSet<string>(memberIndex.Keys, StringComparer.Ordinal);
			var finalProjects = merge ? new List<Project>(into.Projects) : new List<Project>();
			var projectIndex = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int i = 0; i < finalProjects.Count; i++)
			{
				projectIndex[finalProjects[i].Id] = i;
			}

			foreach (var project in incoming.Projects)
			{
				if (!MembersRules.IsWellFormedId(project.Id))
				{
					result.CountInvalid(ValidationResult.Invalid(MembersRules.IdInvalid));
					continue;
				}

				if (merge && deletedProjects.Contains(project.Id))
				{
					result.SkippedDeleted++;
					continue;
				}

				MembersRules.CleanProject(project);
				project.MemberIds.RemoveAll(id => !memberIds.Contains(id));
				var validation = MembersRules.ValidateProject(project, memberIds, isNew: false, validationToday);
				if (!validation.IsValid)
				{
					result.CountInvalid(validation);
					continue;
				}

				if (!projectIndex.TryGetValue(project.Id, out int index))
				{
					projectIndex[project.Id] = finalProjects.Count;
					finalProjects.Add(project);
					result.ProjectsAdded++;
				}
				else if (project.UpdatedUtc > finalProjects[index].UpdatedUtc)
				{
					finalProjects[index] = project;
					result.ProjectsUpdated++;
				}
			}

			into.SchemaVersion = MembersData.CurrentSchemaVersion;
			into.Members = finalMembers;
			into.Projects = finalProjects;
			if (!merge)
			{
				// the union, so a later merge cannot bring back what was deleted on either side; records the file
				// holds are not marked deleted
				into.DeletedMembers = MembersRules.UnionTombstones(into.DeletedMembers, incoming.DeletedMembers, memberIndex.Keys);
				into.DeletedProjects = MembersRules.UnionTombstones(into.DeletedProjects, incoming.DeletedProjects, projectIndex.Keys);
			}

			into.RemoveDanglingMemberIds();
			return result;
		}

		/// <summary>
		/// The SchemaVersion of decrypted members JSON, or -1 when it cannot be read. No exception.
		/// </summary>
		public static int ReadSchemaVersion(byte[] utf8Json)
		{
			try
			{
				using (var document = JsonDocument.Parse(utf8Json))
				{
					return document.RootElement.ValueKind == JsonValueKind.Object
						&& document.RootElement.TryGetProperty(nameof(MembersData.SchemaVersion), out var element)
						&& element.TryGetInt32(out int version)
						? version
						: -1;
				}
			}
			catch (JsonException)
			{
				return -1;
			}
			catch (ArgumentException)
			{
				return -1;
			}
		}

		/// <summary>
		/// One row per member, for a spreadsheet.
		/// </summary>
		public static string MembersCsv(MembersData d)
		{
			var builder = new StringBuilder();
			// Importance and Role (schema version 2) come last, so the earlier columns keep their places
			AppendRow(builder, "Id", "Name", "Contact", "Met on", "Interests", "Machines", "Archived", "Last note", "Projects", "Notes", "Importance", "Role");
			foreach (var member in d?.Members ?? new List<Member>())
			{
				var projects = (d.Projects ?? new List<Project>())
					.Where(p => p.MemberIds != null && p.MemberIds.Contains(member.Id))
					.Select(p => p.Title);
				var lastNote = (member.Log ?? new List<LogEntry>())
					.Select(l => l.Date)
					.OrderByDescending(date => date, StringComparer.Ordinal)
					.FirstOrDefault() ?? "";

				AppendRow(
					builder,
					member.Id,
					member.Name,
					member.Contact,
					member.MetOn,
					string.Join("; ", member.Interests ?? new List<string>()),
					string.Join("; ", member.Machines ?? new List<string>()),
					member.Archived ? "yes" : "no",
					lastNote,
					string.Join("; ", projects),
					member.Notes,
					member.Importance.ToString(CultureInfo.InvariantCulture),
					member.Role);
			}

			return builder.ToString();
		}

		/// <summary>
		/// One row per project, for a spreadsheet. Only the design file's name is included, not its folder.
		/// </summary>
		public static string ProjectsCsv(MembersData d)
		{
			var names = (d?.Members ?? new List<Member>())
				.GroupBy(m => m.Id)
				.ToDictionary(g => g.Key, g => g.First().Name);

			var builder = new StringBuilder();
			AppendRow(builder, "Id", "Title", "Status", "Status since", "Members", "Needs", "Machines", "Help wanted", "Started", "Design file", "Notes");
			foreach (var project in d?.Projects ?? new List<Project>())
			{
				var members = (project.MemberIds ?? new List<string>())
					.Select(id => names.TryGetValue(id, out var name) ? name : "")
					.Where(name => name.Length > 0);

				AppendRow(
					builder,
					project.Id,
					project.Title,
					StatusText(project.Status),
					project.StatusSince,
					string.Join("; ", members),
					project.Needs,
					string.Join("; ", project.Machines ?? new List<string>()),
					project.HelpWanted ? "yes" : "no",
					project.Started,
					project.Link?.FileName ?? "",
					project.Notes);
			}

			return builder.ToString();
		}

		/// <summary>
		/// One CSV cell (RFC 4180). A cell that starts with a tab or carriage return, or whose first character other
		/// than white space (including no-break spaces and newlines) is =, +, -, @ or their full-width forms, gets a
		/// leading ' so a spreadsheet shows it as text instead of running it as a formula.
		/// </summary>
		public static string CsvCell(string value)
		{
			value ??= "";
			if (NeedsFormulaGuard(value))
			{
				value = "'" + value;
			}

			if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
			{
				value = "\"" + value.Replace("\"", "\"\"") + "\"";
			}

			return value;
		}

		private static bool NeedsFormulaGuard(string value)
		{
			if (value.Length == 0)
			{
				return false;
			}

			if (value[0] == '\t' || value[0] == '\r')
			{
				return true;
			}

			foreach (char c in value)
			{
				if (char.IsWhiteSpace(c) || c == '\u200B' || c == '\uFEFF')
				{
					continue;
				}

				return "=+-@\uFF1D\uFF0B\uFF0D\uFF20\uFE62\uFE63\uFE66\u2212".IndexOf(c) >= 0;
			}

			return false;
		}

		public static string StatusText(ProjectStatus status)
		{
			switch (status)
			{
				case ProjectStatus.InProgress:
					return "In progress";
				default:
					return status.ToString();
			}
		}

		/// <summary>
		/// Brings data of an older schema version up to the current one. 1 to 2: nothing to change, a version 1 member has
		/// no Importance or Role and reads as 0 and "" (the defaults). Later versions add their steps here.
		/// </summary>
		private static MembersData Migrate(MembersData data, int fromVersion)
		{
			data.SchemaVersion = MembersData.CurrentSchemaVersion;
			return data;
		}

		private static bool HasUniqueIds(MembersData data)
		{
			bool Unique(IEnumerable<string> ids)
			{
				var list = ids.ToList();
				return list.All(id => !string.IsNullOrEmpty(id)) && list.Distinct(StringComparer.Ordinal).Count() == list.Count;
			}

			return Unique(data.Members.Select(m => m.Id)) && Unique(data.Projects.Select(p => p.Id));
		}

		private static void AppendRow(StringBuilder builder, params string[] cells)
		{
			builder.Append(string.Join(",", cells.Select(CsvCell)));
			builder.Append("\r\n");
		}

		private static string Serialize(MembersData d, JsonSerializerOptions options)
		{
			var copy = (d ?? new MembersData()).Clone();
			copy.Normalize();
			copy.SchemaVersion = MembersData.CurrentSchemaVersion;
			ReplaceLoneSurrogates(copy);
			return JsonSerializer.Serialize(copy, options);
		}

		/// <summary>
		/// New text with a lone surrogate is refused by the rules; this only covers text already stored, so writing
		/// can never fail on it. Each lone surrogate becomes U+FFFD.
		/// </summary>
		private static void ReplaceLoneSurrogates(MembersData d)
		{
			string F(string s) => MembersRules.ReplaceLoneSurrogates(s);
			List<string> L(List<string> list) => list.Select(F).ToList();
			void Log(List<LogEntry> log)
			{
				foreach (var entry in log)
				{
					entry.Id = F(entry.Id);
					entry.Date = F(entry.Date);
					entry.Text = F(entry.Text);
				}
			}

			foreach (var m in d.Members)
			{
				m.Id = F(m.Id);
				m.Name = F(m.Name);
				m.Role = F(m.Role);
				m.Contact = F(m.Contact);
				m.MetOn = F(m.MetOn);
				m.Notes = F(m.Notes);
				m.Interests = L(m.Interests);
				m.Machines = L(m.Machines);
				Log(m.Log);
			}

			foreach (var p in d.Projects)
			{
				p.Id = F(p.Id);
				p.Title = F(p.Title);
				p.StatusSince = F(p.StatusSince);
				p.Needs = F(p.Needs);
				p.Notes = F(p.Notes);
				p.Started = F(p.Started);
				p.MemberIds = L(p.MemberIds);
				p.Machines = L(p.Machines);
				Log(p.Log);
				if (p.Link != null)
				{
					p.Link.Path = F(p.Link.Path);
					p.Link.FileName = F(p.Link.FileName);
					p.Link.Sha256 = F(p.Link.Sha256);
				}
			}

			foreach (var t in d.DeletedMembers.Concat(d.DeletedProjects))
			{
				t.Id = F(t.Id);
			}
		}

		private static JsonSerializerOptions MakeOptions(bool indented)
		{
			var options = new JsonSerializerOptions
			{
				WriteIndented = indented,
				DefaultIgnoreCondition = JsonIgnoreCondition.Never,
			};
			options.Converters.Add(new JsonStringEnumConverter());
			return options;
		}
	}
}
