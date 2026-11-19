using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// Which projects to list.
	/// </summary>
	public sealed class ProjectFilter
	{
		/// <summary>Null or empty means every status.</summary>
		public ISet<ProjectStatus> Statuses { get; set; }

		public string Query { get; set; } = "";

		/// <summary>Only the projects of this member, when set.</summary>
		public string MemberId { get; set; }

		public bool HelpWantedOnly { get; set; }
	}

	/// <summary>
	/// EBMC Edition: search over members and projects. Case and accents are ignored (<see cref="MembersRules.Fold"/>);
	/// every word of the query must match somewhere.
	/// </summary>
	public static class MembersSearch
	{
		/// <summary>
		/// Members matching every word of <paramref name="query"/> in their name, role, contact, interests, machines,
		/// notes, log notes or the titles of their projects. Higher importance always comes first; within the same
		/// importance, name-prefix matches come first, then name matches, then the rest; ties go to the most recent
		/// activity. An empty query lists everyone, by importance, then most recent activity first. Archived members are
		/// left out unless <paramref name="includeArchived"/> (then mixed in by the same order). Only members with at
		/// least <paramref name="minImportance"/> are listed (0: everyone). Builds a fresh index; for repeated searches
		/// keep a <see cref="MembersSearchIndex"/> (the store does).
		/// </summary>
		public static IReadOnlyList<Member> SearchMembers(MembersData d, string query, bool includeArchived, int minImportance = 0)
		{
			return new MembersSearchIndex(d).SearchMembers(query, includeArchived, minImportance);
		}

		/// <summary>
		/// Projects by status, query (title, needs, notes and member names), member and help wanted. Asking for
		/// "Stuck" alone lists the project stuck longest first; otherwise the most recently changed come first.
		/// </summary>
		public static IReadOnlyList<Project> FilterProjects(MembersData d, ProjectFilter f)
		{
			return new MembersSearchIndex(d).FilterProjects(f);
		}

		/// <summary>
		/// The day of the member's latest activity: the later of their latest log note's date and the day the record
		/// last changed (both compared as dates). The change time is stored in UTC and turned into a day in
		/// <paramref name="zone"/> (the computer's own time zone when null), the same way the session's "today" is.
		/// </summary>
		public static DateTime LastActivity(Member member, TimeZoneInfo zone = null)
		{
			if (member == null)
			{
				return DateTime.MinValue;
			}

			DateTime latest = LocalDay(member.UpdatedUtc, zone);
			foreach (var entry in member.Log ?? new List<LogEntry>())
			{
				if (entry != null
					&& MembersRules.TryParseIso(entry.Date, out var date)
					&& date > latest)
				{
					latest = date;
				}
			}

			return latest;
		}

		/// <summary>The day in <paramref name="zone"/> (local when null) of a stored UTC time.</summary>
		private static DateTime LocalDay(DateTime utc, TimeZoneInfo zone)
		{
			// stored times are UTC; one read back without a kind is UTC too
			var asUtc = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
			return TimeZoneInfo.ConvertTimeFromUtc(asUtc, zone ?? TimeZoneInfo.Local).Date;
		}
	}

	/// <summary>
	/// The folded text of every member and project, built once and kept up to date entry by entry
	/// (<see cref="Refresh"/>), so a change costs only the entries it touches.
	/// </summary>
	public sealed class MembersSearchIndex
	{
		// separates fields so a word cannot match across two of them
		private const char Separator = '\u0001';

		private readonly Dictionary<string, MemberEntry> members = new Dictionary<string, MemberEntry>(StringComparer.Ordinal);
		private readonly Dictionary<string, ProjectEntry> projects = new Dictionary<string, ProjectEntry>(StringComparer.Ordinal);

		public MembersSearchIndex(MembersData d)
		{
			var memberList = (d?.Members ?? new List<Member>()).Where(m => m != null).ToList();
			var projectList = (d?.Projects ?? new List<Project>()).Where(p => p != null).ToList();

			var titlesByMember = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
			foreach (var project in projectList)
			{
				var title = MembersRules.Fold(project.Title);
				foreach (var id in project.MemberIds ?? new List<string>())
				{
					if (!titlesByMember.TryGetValue(id ?? "", out var builder))
					{
						titlesByMember[id ?? ""] = builder = new StringBuilder();
					}

					builder.Append(Separator).Append(title);
				}
			}

			foreach (var member in memberList)
			{
				titlesByMember.TryGetValue(member.Id ?? "", out var titles);
				members[UniqueKey(members, member.Id)] = BuildMember(member, titles?.ToString() ?? "");
			}

			foreach (var project in projectList)
			{
				projects[UniqueKey(projects, project.Id)] = BuildProject(project);
			}
		}

		/// <summary>
		/// Rebuilds the entries of the given members and projects from <paramref name="d"/> (removing those that no
		/// longer exist). Ids are unique in the store's data.
		/// </summary>
		public void Refresh(MembersData d, IEnumerable<string> memberIds, IEnumerable<string> projectIds)
		{
			foreach (var id in (memberIds ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal))
			{
				var member = d.Members.FirstOrDefault(m => m.Id == id);
				if (member == null)
				{
					members.Remove(id);
					continue;
				}

				var titles = new StringBuilder();
				foreach (var project in d.Projects)
				{
					if (project.MemberIds.Contains(id))
					{
						titles.Append(Separator).Append(MembersRules.Fold(project.Title));
					}
				}

				members[id] = BuildMember(member, titles.ToString());
			}

			foreach (var id in (projectIds ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal))
			{
				var project = d.Projects.FirstOrDefault(p => p.Id == id);
				if (project == null)
				{
					projects.Remove(id);
				}
				else
				{
					projects[id] = BuildProject(project);
				}
			}
		}

		/// <inheritdoc cref="MembersSearch.SearchMembers"/>
		public IReadOnlyList<Member> SearchMembers(string query, bool includeArchived, int minImportance = 0)
		{
			var foldedQuery = MembersRules.Fold(MembersRules.CollapseWhitespace(query));
			var terms = foldedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

			var matches = new List<(MemberEntry entry, int rank)>();
			foreach (var entry in members.Values)
			{
				if (entry.Member.Archived && !includeArchived)
				{
					continue;
				}

				// the same clamped level the list shows (a hand-edited 9 is three stars, -1 none)
				if (MembersRules.ClampImportance(entry.Member.Importance) < minImportance)
				{
					continue;
				}

				int rank = 0;
				if (terms.Length > 0)
				{
					bool all = true;
					foreach (var term in terms)
					{
						if (entry.All.IndexOf(term, StringComparison.Ordinal) < 0)
						{
							all = false;
							break;
						}
					}

					if (!all)
					{
						continue;
					}

					if (entry.Name.StartsWith(foldedQuery, StringComparison.Ordinal))
					{
						rank = 0;
					}
					else if (entry.Name.Contains(foldedQuery, StringComparison.Ordinal)
						|| terms.All(term => entry.Name.Contains(term, StringComparison.Ordinal)))
					{
						rank = 1;
					}
					else
					{
						rank = 2;
					}
				}

				matches.Add((entry, rank));
			}

			return matches
				.OrderByDescending(m => MembersRules.ClampImportance(m.entry.Member.Importance))
				.ThenBy(m => m.rank)
				.ThenByDescending(m => m.entry.Activity)
				.ThenByDescending(m => m.entry.Member.UpdatedUtc)
				.ThenBy(m => m.entry.Name, StringComparer.Ordinal)
				.ThenBy(m => m.entry.Member.Id ?? "", StringComparer.Ordinal)
				.Select(m => m.entry.Member)
				.ToList();
		}

		/// <inheritdoc cref="MembersSearch.FilterProjects"/>
		public IReadOnlyList<Project> FilterProjects(ProjectFilter f)
		{
			f ??= new ProjectFilter();
			var terms = MembersRules.Fold(MembersRules.CollapseWhitespace(f.Query)).Split(' ', StringSplitOptions.RemoveEmptyEntries);

			var list = projects.Values.Where(entry =>
			{
				var p = entry.Project;
				if (f.Statuses != null && f.Statuses.Count > 0 && !f.Statuses.Contains(p.Status))
				{
					return false;
				}

				if (f.HelpWantedOnly && !p.HelpWanted)
				{
					return false;
				}

				if (!string.IsNullOrEmpty(f.MemberId) && (p.MemberIds == null || !p.MemberIds.Contains(f.MemberId)))
				{
					return false;
				}

				return terms.All(term => entry.All.IndexOf(term, StringComparison.Ordinal) >= 0);
			});

			bool stuckOnly = f.Statuses != null && f.Statuses.Count == 1 && f.Statuses.Contains(ProjectStatus.Stuck);
			IOrderedEnumerable<ProjectEntry> ordered = stuckOnly
				? list.OrderBy(e => e.StatusSince)
				: list.OrderByDescending(e => e.Project.UpdatedUtc);

			return ordered
				.ThenBy(e => e.Project.Title ?? "", StringComparer.OrdinalIgnoreCase)
				.ThenBy(e => e.Project.Id ?? "", StringComparer.Ordinal)
				.Select(e => e.Project)
				.ToList();
		}

		private static string UniqueKey<T>(Dictionary<string, T> map, string id)
		{
			var key = id ?? "";
			for (int n = 2; map.ContainsKey(key); n++)
			{
				// only for hand-made data with repeated ids; the store's ids are unique
				key = (id ?? "") + "\u0000" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
			}

			return key;
		}

		private static MemberEntry BuildMember(Member member, string titles)
		{
			var builder = new StringBuilder();
			var name = MembersRules.Fold(MembersRules.CollapseWhitespace(member.Name));
			builder.Append(name)
				.Append(Separator).Append(MembersRules.Fold(member.Role))
				.Append(Separator).Append(MembersRules.Fold(member.Contact))
				.Append(Separator).Append(MembersRules.Fold(member.Notes));
			foreach (var tag in (member.Interests ?? new List<string>()).Concat(member.Machines ?? new List<string>()))
			{
				builder.Append(Separator).Append(MembersRules.Fold(tag));
			}

			foreach (var entry in member.Log ?? new List<LogEntry>())
			{
				builder.Append(Separator).Append(MembersRules.Fold(entry?.Text));
			}

			builder.Append(titles);
			return new MemberEntry
			{
				Member = member,
				Name = name,
				All = builder.ToString(),
				Activity = MembersSearch.LastActivity(member),
			};
		}

		private ProjectEntry BuildProject(Project project)
		{
			var builder = new StringBuilder();
			builder.Append(MembersRules.Fold(project.Title))
				.Append(Separator).Append(MembersRules.Fold(project.Needs))
				.Append(Separator).Append(MembersRules.Fold(project.Notes));
			foreach (var id in project.MemberIds ?? new List<string>())
			{
				if (id != null && members.TryGetValue(id, out var member))
				{
					builder.Append(Separator).Append(member.Name);
				}
			}

			return new ProjectEntry
			{
				Project = project,
				All = builder.ToString(),
				StatusSince = MembersRules.TryParseIso(project.StatusSince, out var since) ? since : DateTime.MaxValue,
			};
		}

		private sealed class MemberEntry
		{
			public Member Member;
			public string Name;
			public string All;
			public DateTime Activity;
		}

		private sealed class ProjectEntry
		{
			public Project Project;
			public string All;
			public DateTime StatusSince;
		}
	}
}
