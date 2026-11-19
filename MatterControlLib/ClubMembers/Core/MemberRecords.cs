using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// EBMC Edition: the club's members and their projects. Plain data, saved as JSON inside the encrypted members
	/// file (see <see cref="MembersVault"/>). Dates entered by people are ISO "yyyy-MM-dd" strings, because
	/// net6 has no date-only type that System.Text.Json can store; <see cref="MembersRules"/> checks them.
	/// </summary>
	public sealed class MembersData
	{
		/// <summary>
		/// 1: the first format. 2: members have <see cref="Member.Importance"/> and <see cref="Member.Role"/> (absent in
		/// a version 1 file, read as 0 and ""). Raised so that a build that does not know a field refuses the file
		/// (NewerVersion, nothing changed) instead of reading it and dropping the field on its next save.
		/// </summary>
		public const int CurrentSchemaVersion = 2;

		public int SchemaVersion { get; set; } = CurrentSchemaVersion;

		public List<Member> Members { get; set; } = new List<Member>();

		public List<Project> Projects { get; set; } = new List<Project>();

		/// <summary>
		/// Members deleted here: only the id and when, never a name. A merge import never brings them back.
		/// Optional in the file, so version 1 files written before it existed still open.
		/// </summary>
		public List<Tombstone> DeletedMembers { get; set; } = new List<Tombstone>();

		/// <summary>Projects deleted here (id and time only).</summary>
		public List<Tombstone> DeletedProjects { get; set; } = new List<Tombstone>();

		public MembersData Clone()
		{
			return new MembersData
			{
				SchemaVersion = SchemaVersion,
				Members = (Members ?? new List<Member>()).Select(m => m?.Clone()).ToList(),
				Projects = (Projects ?? new List<Project>()).Select(p => p?.Clone()).ToList(),
				DeletedMembers = (DeletedMembers ?? new List<Tombstone>()).Select(t => t?.Clone()).ToList(),
				DeletedProjects = (DeletedProjects ?? new List<Tombstone>()).Select(t => t?.Clone()).ToList(),
			};
		}

		/// <summary>
		/// Removes project member ids that name no member (never removes a record).
		/// </summary>
		public void RemoveDanglingMemberIds()
		{
			var ids = new HashSet<string>((Members ?? new List<Member>()).Where(m => m != null).Select(m => m.Id));
			foreach (var project in Projects ?? new List<Project>())
			{
				project?.MemberIds?.RemoveAll(id => !ids.Contains(id));
			}
		}

		/// <summary>
		/// Replaces missing lists with empty ones, so the rest of the code never sees null.
		/// </summary>
		public void Normalize()
		{
			Members = (Members ?? new List<Member>()).Where(m => m != null).ToList();
			Projects = (Projects ?? new List<Project>()).Where(p => p != null).ToList();
			// only ids this app makes: a deleted list never holds free text such as a name
			DeletedMembers = MembersRules.UnionTombstones(DeletedMembers, null, null);
			DeletedProjects = MembersRules.UnionTombstones(DeletedProjects, null, null);
			foreach (var member in Members)
			{
				member.Normalize();
			}

			foreach (var project in Projects)
			{
				project.Normalize();
			}
		}
	}

	/// <summary>
	/// A deleted record: its id and when it was deleted, nothing else.
	/// </summary>
	public sealed class Tombstone
	{
		public string Id { get; set; } = "";

		public DateTime DeletedUtc { get; set; }

		public Tombstone Clone() => new Tombstone { Id = Id, DeletedUtc = DeletedUtc };
	}

	public sealed class Member
	{
		public string Id { get; set; } = "";

		public string Name { get; set; } = "";

		/// <summary>How important the member is to the club: 0 (none, the default), 1, 2 or 3 (shown as stars). Higher
		/// comes first in the list. Schema version 2; absent in older files, read as 0.</summary>
		public int Importance { get; set; }

		/// <summary>One short line typed by the owner, for example "Founder" or "Board", shown as a badge; "" for none.
		/// Schema version 2; absent in older files, read as "".</summary>
		public string Role { get; set; } = "";

		/// <summary>Free format: email, phone, Discord name.</summary>
		public string Contact { get; set; } = "";

		/// <summary>"yyyy-MM-dd" or empty.</summary>
		public string MetOn { get; set; } = "";

		public List<string> Interests { get; set; } = new List<string>();

		/// <summary>Free-text tags, for example "Pulse XE" or "laser".</summary>
		public List<string> Machines { get; set; } = new List<string>();

		public string Notes { get; set; } = "";

		public List<LogEntry> Log { get; set; } = new List<LogEntry>();

		/// <summary>Someone who has left. Archiving is the normal action; deleting is separate and explicit.</summary>
		public bool Archived { get; set; }

		public DateTime CreatedUtc { get; set; }

		public DateTime UpdatedUtc { get; set; }

		public Member Clone()
		{
			return new Member
			{
				Id = Id,
				Name = Name,
				Importance = Importance,
				Role = Role,
				Contact = Contact,
				MetOn = MetOn,
				Interests = new List<string>(Interests ?? new List<string>()),
				Machines = new List<string>(Machines ?? new List<string>()),
				Notes = Notes,
				Log = (Log ?? new List<LogEntry>()).Select(l => l?.Clone()).ToList(),
				Archived = Archived,
				CreatedUtc = CreatedUtc,
				UpdatedUtc = UpdatedUtc,
			};
		}

		internal void Normalize()
		{
			Id ??= "";
			Name ??= "";
			Role ??= "";
			Contact ??= "";
			MetOn ??= "";
			Notes ??= "";
			Interests = (Interests ?? new List<string>()).Where(t => t != null).ToList();
			Machines = (Machines ?? new List<string>()).Where(t => t != null).ToList();
			Log = (Log ?? new List<LogEntry>()).Where(l => l != null).ToList();
			foreach (var entry in Log)
			{
				entry.Normalize();
			}
		}
	}

	public sealed class LogEntry
	{
		public string Id { get; set; } = "";

		/// <summary>"yyyy-MM-dd".</summary>
		public string Date { get; set; } = "";

		public string Text { get; set; } = "";

		public DateTime CreatedUtc { get; set; }

		public LogEntry Clone()
		{
			return new LogEntry
			{
				Id = Id,
				Date = Date,
				Text = Text,
				CreatedUtc = CreatedUtc,
			};
		}

		internal void Normalize()
		{
			Id ??= "";
			Date ??= "";
			Text ??= "";
		}
	}

	[JsonConverter(typeof(JsonStringEnumConverter))]
	public enum ProjectStatus
	{
		Idea,
		InProgress,
		Stuck,
		Done,
	}

	public sealed class Project
	{
		public string Id { get; set; } = "";

		public string Title { get; set; } = "";

		public ProjectStatus Status { get; set; } = ProjectStatus.Idea;

		/// <summary>"yyyy-MM-dd": the day the status last changed.</summary>
		public string StatusSince { get; set; } = "";

		public List<string> MemberIds { get; set; } = new List<string>();

		/// <summary>What the project needs, for example "help with CAD".</summary>
		public string Needs { get; set; } = "";

		public List<string> Machines { get; set; } = new List<string>();

		public bool HelpWanted { get; set; }

		public string Notes { get; set; } = "";

		/// <summary>"yyyy-MM-dd" or empty.</summary>
		public string Started { get; set; } = "";

		public List<LogEntry> Log { get; set; } = new List<LogEntry>();

		/// <summary>The design file, if one is linked. Never a print file.</summary>
		public DesignLink Link { get; set; }

		public DateTime CreatedUtc { get; set; }

		public DateTime UpdatedUtc { get; set; }

		public Project Clone()
		{
			return new Project
			{
				Id = Id,
				Title = Title,
				Status = Status,
				StatusSince = StatusSince,
				MemberIds = new List<string>(MemberIds ?? new List<string>()),
				Needs = Needs,
				Machines = new List<string>(Machines ?? new List<string>()),
				HelpWanted = HelpWanted,
				Notes = Notes,
				Started = Started,
				Log = (Log ?? new List<LogEntry>()).Select(l => l?.Clone()).ToList(),
				Link = Link?.Clone(),
				CreatedUtc = CreatedUtc,
				UpdatedUtc = UpdatedUtc,
			};
		}

		internal void Normalize()
		{
			Id ??= "";
			Title ??= "";
			StatusSince ??= "";
			Needs ??= "";
			Notes ??= "";
			Started ??= "";
			MemberIds = (MemberIds ?? new List<string>()).Where(t => !string.IsNullOrEmpty(t)).ToList();
			Machines = (Machines ?? new List<string>()).Where(t => t != null).ToList();
			Log = (Log ?? new List<LogEntry>()).Where(l => l != null).ToList();
			foreach (var entry in Log)
			{
				entry.Normalize();
			}
		}
	}

	/// <summary>
	/// A link from a project to a design file. The path is the key; the file name, size and SHA-256 are only used to
	/// find the file again if it moves (see <see cref="DesignLinkResolver"/>).
	/// </summary>
	public sealed class DesignLink
	{
		public string Path { get; set; } = "";

		public string FileName { get; set; } = "";

		public long SizeBytes { get; set; }

		/// <summary>Lower-case hex.</summary>
		public string Sha256 { get; set; } = "";

		public DateTime LinkedUtc { get; set; }

		public DesignLink Clone()
		{
			return new DesignLink
			{
				Path = Path,
				FileName = FileName,
				SizeBytes = SizeBytes,
				Sha256 = Sha256,
				LinkedUtc = LinkedUtc,
			};
		}
	}
}
