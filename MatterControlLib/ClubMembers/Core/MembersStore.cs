using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// EBMC Edition: the changes people make to the members data. Every change is checked first and made only when
	/// valid; <see cref="Changed"/> is raised after each change so the caller can save. Everything handed out is a
	/// copy, so the data can only change through these methods. No files here: saving is <see cref="MembersVault"/>'s
	/// job (give it <see cref="Snapshot"/>).
	/// </summary>
	public sealed class MembersStore
	{
		private const int IdAttempts = 10;

		private readonly Func<DateTime> clockUtc;
		private readonly Func<string> newId;
		private readonly Func<DateTime> today;

		private readonly HashSet<string> dirtyMembers = new HashSet<string>(StringComparer.Ordinal);
		private readonly HashSet<string> dirtyProjects = new HashSet<string>(StringComparer.Ordinal);

		private MembersSearchIndex index;

		/// <param name="data">The data to start from (copied).</param>
		/// <param name="clockUtc">The current UTC time, for the record time stamps.</param>
		/// <param name="newId">Makes a new unique id.</param>
		/// <param name="today">The local date for automatic dates (a status change). Defaults to the local date of
		/// <paramref name="clockUtc"/>.</param>
		public MembersStore(MembersData data, Func<DateTime> clockUtc, Func<string> newId, Func<DateTime> today = null)
		{
			Data = (data ?? new MembersData()).Clone();
			Data.Normalize();
			this.clockUtc = clockUtc ?? (() => DateTime.UtcNow);
			this.newId = newId ?? (() => Guid.NewGuid().ToString("N"));
			this.today = today ?? (() => this.clockUtc().ToLocalTime().Date);
		}

		public event EventHandler Changed;

		/// <summary>
		/// The live data. Private: everyone else gets copies (<see cref="Snapshot"/>).
		/// </summary>
		private MembersData Data { get; }

		/// <summary>
		/// The latest date a stored date may have: one day past the UTC date, which covers every time zone.
		/// </summary>
		private DateTime ValidationToday => clockUtc().Date;

		/// <summary>
		/// The search index: built on the first search, then only the entries changed since are rebuilt, on the next
		/// search (a change itself does no index work).
		/// </summary>
		private MembersSearchIndex Index
		{
			get
			{
				if (index == null)
				{
					index = new MembersSearchIndex(Data);
				}
				else if (dirtyMembers.Count > 0 || dirtyProjects.Count > 0)
				{
					index.Refresh(Data, dirtyMembers, dirtyProjects);
				}

				dirtyMembers.Clear();
				dirtyProjects.Clear();
				return index;
			}
		}

		/// <summary>A copy of all the data, for saving or export.</summary>
		public MembersData Snapshot() => Data.Clone();

		public int MemberCount => Data.Members.Count;

		public int ProjectCount => Data.Projects.Count;

		/// <summary>A copy of the member, or null.</summary>
		public Member FindMember(string id) => LiveMember(id)?.Clone();

		/// <summary>A copy of the project, or null.</summary>
		public Project FindProject(string id) => LiveProject(id)?.Clone();

		/// <summary>
		/// Adds a member from just a name and, when given, a first note dated <paramref name="today"/>.
		/// </summary>
		public Result<Member> QuickAdd(string name, string firstNote, DateTime today)
		{
			var draft = new Member { Name = name };
			var note = (firstNote ?? "").Trim();
			if (note.Length > 0)
			{
				draft.Log.Add(new LogEntry { Date = MembersRules.ToIso(today), Text = note });
			}

			return AddMember(draft);
		}

		/// <summary>
		/// Adds a copy of <paramref name="draft"/>, with a new id when it has none. Returns a copy.
		/// </summary>
		public Result<Member> AddMember(Member draft)
		{
			if (draft == null)
			{
				return Result<Member>.Failure(MembersRules.NameRequired);
			}

			var member = draft.Clone();
			member.Normalize();
			if (string.IsNullOrEmpty(member.Id))
			{
				member.Id = UniqueId(MemberIdTaken);
				if (member.Id == null)
				{
					return Result<Member>.Failure(MembersRules.DuplicateId);
				}
			}
			else if (!MembersRules.IsWellFormedId(member.Id))
			{
				return Result<Member>.Failure(MembersRules.IdInvalid);
			}
			else if (MemberIdTaken(member.Id))
			{
				return Result<Member>.Failure(MembersRules.DuplicateId);
			}

			var now = clockUtc();
			MembersRules.CleanMember(member);
			StampLog(member.Log, now);
			member.CreatedUtc = now;
			member.UpdatedUtc = now;

			var validation = MembersRules.ValidateMember(member, ValidationToday);
			if (!validation.IsValid)
			{
				return Result<Member>.Failure(validation);
			}

			Data.Members.Add(member);
			OnChanged(member.Id);
			return Result<Member>.Success(member.Clone());
		}

		/// <summary>
		/// Applies <paramref name="edit"/> to a copy of the member, checks the copy, and stores it only if valid.
		/// The id and creation time cannot be changed. Dates and notes the edit did not touch are not checked again.
		/// </summary>
		public ValidationResult UpdateMember(string id, Action<Member> edit)
		{
			int index = Data.Members.FindIndex(m => m.Id == id);
			if (index < 0 || edit == null)
			{
				return ValidationResult.Invalid(MembersRules.NotFound);
			}

			var original = Data.Members[index];
			var copy = original.Clone();
			edit(copy);
			copy.Normalize();
			copy.Id = original.Id;
			copy.CreatedUtc = original.CreatedUtc;

			var now = clockUtc();
			MembersRules.CleanMember(copy);
			StampLog(copy.Log, now);

			var validation = MembersRules.ValidateMember(copy, ValidationToday, original);
			if (!validation.IsValid)
			{
				return validation;
			}

			copy.UpdatedUtc = now;
			Data.Members[index] = copy;
			OnChanged(copy.Id, ProjectIdsOf(copy.Id));
			return validation;
		}

		/// <summary>
		/// Archives (or brings back) a member. This is the normal action for someone who leaves.
		/// </summary>
		public bool ArchiveMember(string id, bool archived)
		{
			var member = LiveMember(id);
			if (member == null)
			{
				return false;
			}

			if (member.Archived != archived)
			{
				member.Archived = archived;
				member.UpdatedUtc = clockUtc();
				OnChanged(member.Id);
			}

			return true;
		}

		/// <summary>
		/// Deletes a member for good and removes them from their projects. Projects left with no member are kept.
		/// The id is remembered (without the name) so a merge import cannot bring them back. The screen must ask
		/// first.
		/// </summary>
		public bool DeleteMember(string id)
		{
			int removed = Data.Members.RemoveAll(m => m.Id == id);
			if (removed == 0)
			{
				return false;
			}

			var now = clockUtc();
			var affected = ProjectIdsOf(id);
			MembersRules.AddTombstone(Data.DeletedMembers, id, now);
			foreach (var project in Data.Projects)
			{
				if (project.MemberIds.RemoveAll(m => m == id) > 0)
				{
					project.UpdatedUtc = now;
				}
			}

			OnChanged(id, affected);
			return true;
		}

		/// <summary>
		/// Adds a dated note to a member. An empty <paramref name="dateText"/> means today.
		/// </summary>
		public Result<LogEntry> AddLog(string memberId, string dateText, string text, DateTime today)
		{
			var member = LiveMember(memberId);
			if (member == null)
			{
				return Result<LogEntry>.Failure(MembersRules.NotFound);
			}

			var entry = MakeLogEntry(dateText, text, today, out var validation);
			if (entry == null)
			{
				return Result<LogEntry>.Failure(validation);
			}

			member.Log.Add(entry);
			member.UpdatedUtc = entry.CreatedUtc;
			OnChanged(member.Id);
			return Result<LogEntry>.Success(entry.Clone());
		}

		/// <summary>
		/// Adds a copy of <paramref name="draft"/>. It needs a title and at least one existing member. Returns a copy.
		/// </summary>
		public Result<Project> AddProject(Project draft)
		{
			if (draft == null)
			{
				return Result<Project>.Failure(MembersRules.TitleRequired);
			}

			var project = draft.Clone();
			project.Normalize();
			if (string.IsNullOrEmpty(project.Id))
			{
				project.Id = UniqueId(ProjectIdTaken);
				if (project.Id == null)
				{
					return Result<Project>.Failure(MembersRules.DuplicateId);
				}
			}
			else if (!MembersRules.IsWellFormedId(project.Id))
			{
				return Result<Project>.Failure(MembersRules.IdInvalid);
			}
			else if (ProjectIdTaken(project.Id))
			{
				return Result<Project>.Failure(MembersRules.DuplicateId);
			}

			var now = clockUtc();
			MembersRules.CleanProject(project);
			StampLog(project.Log, now);
			if (project.StatusSince.Length == 0)
			{
				project.StatusSince = MembersRules.ToIso(today());
			}

			project.CreatedUtc = now;
			project.UpdatedUtc = now;

			var validation = MembersRules.ValidateProject(project, MemberIdSet(), isNew: true, ValidationToday);
			if (!validation.IsValid)
			{
				return Result<Project>.Failure(validation);
			}

			Data.Projects.Add(project);
			OnProjectChanged(project.Id, project.MemberIds);
			return Result<Project>.Success(project.Clone());
		}

		/// <summary>
		/// Applies <paramref name="edit"/> to a copy of the project and stores it only if valid. A status change sets
		/// <see cref="Project.StatusSince"/> to today.
		/// </summary>
		public ValidationResult UpdateProject(string id, Action<Project> edit)
		{
			int index = Data.Projects.FindIndex(p => p.Id == id);
			if (index < 0 || edit == null)
			{
				return ValidationResult.Invalid(MembersRules.NotFound);
			}

			var original = Data.Projects[index];
			var copy = original.Clone();
			edit(copy);
			copy.Normalize();
			copy.Id = original.Id;
			copy.CreatedUtc = original.CreatedUtc;

			var now = clockUtc();
			MembersRules.CleanProject(copy);
			StampLog(copy.Log, now);
			if (copy.Status != original.Status)
			{
				copy.StatusSince = MembersRules.ToIso(today());
			}

			var validation = MembersRules.ValidateProject(copy, MemberIdSet(), isNew: false, ValidationToday, original);
			if (!validation.IsValid)
			{
				return validation;
			}

			copy.UpdatedUtc = now;
			Data.Projects[index] = copy;
			OnProjectChanged(copy.Id, original.MemberIds.Concat(copy.MemberIds));
			return validation;
		}

		public bool DeleteProject(string id)
		{
			var project = LiveProject(id);
			if (project == null)
			{
				return false;
			}

			Data.Projects.RemoveAll(p => p.Id == id);
			MembersRules.AddTombstone(Data.DeletedProjects, id, clockUtc());
			OnProjectChanged(id, project.MemberIds);
			return true;
		}

		/// <summary>
		/// Adds a dated note to a project. An empty <paramref name="dateText"/> means today.
		/// </summary>
		public Result<LogEntry> AddProjectLog(string projectId, string dateText, string text, DateTime today)
		{
			var project = LiveProject(projectId);
			if (project == null)
			{
				return Result<LogEntry>.Failure(MembersRules.NotFound);
			}

			var entry = MakeLogEntry(dateText, text, today, out var validation);
			if (entry == null)
			{
				return Result<LogEntry>.Failure(validation);
			}

			project.Log.Add(entry);
			project.UpdatedUtc = entry.CreatedUtc;
			OnProjectChanged(project.Id, Enumerable.Empty<string>());
			return Result<LogEntry>.Success(entry.Clone());
		}

		/// <summary>
		/// Copies of the projects <paramref name="memberId"/> is part of.
		/// </summary>
		public IReadOnlyList<Project> ProjectsOf(string memberId)
		{
			return Data.Projects.Where(p => p.MemberIds.Contains(memberId)).Select(p => p.Clone()).ToList();
		}

		/// <summary>
		/// Copies of the projects left with no member (listed under "No member").
		/// </summary>
		public IReadOnlyList<Project> ProjectsWithoutMembers()
		{
			return Data.Projects.Where(p => p.MemberIds.Count == 0).Select(p => p.Clone()).ToList();
		}

		/// <summary>
		/// The ids of the members matching <paramref name="query"/> with at least <paramref name="minImportance"/>, best
		/// first (see <see cref="MembersSearch.SearchMembers"/>). Uses an index kept until the next change.
		/// </summary>
		public IReadOnlyList<string> SearchMemberIds(string query, bool includeArchived, int minImportance = 0)
		{
			return Index.SearchMembers(query, includeArchived, minImportance).Select(m => m.Id).ToList();
		}

		/// <summary>
		/// The ids of the projects matching <paramref name="filter"/> (see <see cref="MembersSearch.FilterProjects"/>).
		/// </summary>
		public IReadOnlyList<string> FilterProjectIds(ProjectFilter filter)
		{
			return Index.FilterProjects(filter).Select(p => p.Id).ToList();
		}

		/// <summary>
		/// Imports an exported JSON file into the data (see <see cref="MembersExport.ImportJson"/>).
		/// </summary>
		public ImportResult Import(string json, ImportMode mode, DateTime today)
		{
			var result = MembersExport.ImportJson(json, Data, mode, today);
			if (result.Status == ImportStatus.Imported)
			{
				// many records may have changed: the index is rebuilt on the next search
				index = null;
				OnChanged(null);
			}

			return result;
		}

		private Member LiveMember(string id) => Data.Members.FirstOrDefault(m => m.Id == id);

		private Project LiveProject(string id) => Data.Projects.FirstOrDefault(p => p.Id == id);

		private bool MemberIdTaken(string id) => Data.Members.Any(m => m.Id == id) || Data.DeletedMembers.Any(t => t.Id == id);

		private bool ProjectIdTaken(string id) => Data.Projects.Any(p => p.Id == id) || Data.DeletedProjects.Any(t => t.Id == id);

		/// <summary>
		/// A new id not in use (nor deleted). Null if <see cref="newId"/> keeps returning used ids.
		/// </summary>
		private string UniqueId(Func<string, bool> taken)
		{
			for (int attempt = 0; attempt < IdAttempts; attempt++)
			{
				var id = newId();
				if (MembersRules.IsWellFormedId(id) && !taken(id))
				{
					return id;
				}
			}

			return null;
		}

		private LogEntry MakeLogEntry(string dateText, string text, DateTime today, out ValidationResult validation)
		{
			string iso;
			if (string.IsNullOrWhiteSpace(dateText))
			{
				iso = MembersRules.ToIso(today);
			}
			else if (!MembersRules.TryParseDate(dateText, today, out iso))
			{
				validation = ValidationResult.Invalid(MembersRules.DateInvalid);
				return null;
			}

			var now = clockUtc();
			var entry = new LogEntry
			{
				Id = newId(),
				Date = iso,
				Text = (text ?? "").Trim(),
				CreatedUtc = now,
			};

			validation = MembersRules.ValidateLogEntry(entry, ValidationToday.Date > today.Date ? ValidationToday : today);
			return validation.IsValid ? entry : null;
		}

		private void StampLog(List<LogEntry> log, DateTime now)
		{
			foreach (var entry in log)
			{
				if (string.IsNullOrEmpty(entry.Id))
				{
					entry.Id = newId();
				}

				if (entry.CreatedUtc == default)
				{
					entry.CreatedUtc = now;
				}
			}
		}

		private HashSet<string> MemberIdSet() => new HashSet<string>(Data.Members.Select(m => m.Id));

		private List<string> ProjectIdsOf(string memberId) => Data.Projects.Where(p => p.MemberIds.Contains(memberId)).Select(p => p.Id).ToList();

		/// <summary>
		/// Records what changed for the index (nothing is rebuilt now) and raises <see cref="Changed"/>.
		/// </summary>
		private void OnChanged(string memberId, IEnumerable<string> projectIds = null)
		{
			if (memberId != null)
			{
				dirtyMembers.Add(memberId);
			}

			foreach (var id in projectIds ?? Enumerable.Empty<string>())
			{
				dirtyProjects.Add(id);
			}

			Changed?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>A project changed: its entry, and the members whose project titles it touches.</summary>
		private void OnProjectChanged(string projectId, IEnumerable<string> memberIds)
		{
			dirtyProjects.Add(projectId);
			foreach (var id in memberIds)
			{
				dirtyMembers.Add(id);
			}

			Changed?.Invoke(this, EventArgs.Empty);
		}
	}
}
