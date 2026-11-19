using System;
using System.Linq;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members & Projects: the changes people make (quick add, edit, archive, delete, notes, projects). Every name here
	/// is invented.
	/// </summary>
	[TestFixture]
	public class MembersStoreTests
	{
		private static readonly DateTime Today = new DateTime(2026, 11, 16);

		/// <summary>An id in the app's format (32 lower-case hex digits).</summary>
		private static readonly string Fixed = 7.ToString("x32");
		private static readonly string A = 0xa.ToString("x32");
		private static readonly string B = 0xb.ToString("x32");
		private static readonly string C = 0xc.ToString("x32");

		private DateTime now;
		private int nextId;
		private int changes;
		private MembersStore store;

		[SetUp]
		public void SetUp()
		{
			now = new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc);
			nextId = 0;
			changes = 0;
			store = new MembersStore(new MembersData(), () => now, () => (++nextId).ToString("x32"), () => Today);
			store.Changed += (s, e) => changes++;
		}

		[Test]
		public void QuickAddTrimsTheNameAndDatesTheFirstNoteToday()
		{
			var result = store.QuickAdd("  Robin  Example ", "met at open night", Today);

			Assert.That(result.Ok, Is.True);
			Assert.That(result.Value.Name, Is.EqualTo("Robin Example"));
			Assert.That(result.Value.Id, Is.Not.Empty);
			Assert.That(result.Value.Log, Has.Count.EqualTo(1));
			Assert.That(result.Value.Log[0].Date, Is.EqualTo("2026-11-16"));
			Assert.That(result.Value.Log[0].Text, Is.EqualTo("met at open night"));
			Assert.That(result.Value.CreatedUtc, Is.EqualTo(now));
			Assert.That(store.Snapshot().Members, Has.Count.EqualTo(1));
			Assert.That(changes, Is.EqualTo(1));
		}

		[Test]
		public void QuickAddWithoutANoteAddsNoLog()
		{
			var result = store.QuickAdd("Sam Testcase", "   ", Today);
			Assert.That(result.Ok, Is.True);
			Assert.That(result.Value.Log, Is.Empty);
		}

		[Test]
		public void QuickAddRejectsAnEmptyName()
		{
			var result = store.QuickAdd("   ", "met at open night", Today);

			Assert.That(result.Ok, Is.False);
			Assert.That(result.Validation.Problems, Does.Contain(MembersRules.NameRequired));
			Assert.That(store.Snapshot().Members, Is.Empty);
			Assert.That(changes, Is.EqualTo(0));
		}

		[Test]
		public void AddMemberCopiesTheDraftAndRefusesADuplicateId()
		{
			var draft = new Member { Id = Fixed, Name = "Robin Example", Interests = { "nylon", "Nylon", " CAD " } };
			var added = store.AddMember(draft);
			Assert.That(added.Ok, Is.True);
			Assert.That(added.Value, Is.Not.SameAs(draft));
			Assert.That(added.Value.Interests, Is.EqualTo(new[] { "nylon", "CAD" }));

			var again = store.AddMember(new Member { Id = Fixed, Name = "Sam Testcase" });
			Assert.That(again.Ok, Is.False);
			Assert.That(store.Snapshot().Members, Has.Count.EqualTo(1));
		}

		[Test]
		public void UpdateMemberWithAnInvalidEditLeavesTheStoredMemberUnchanged()
		{
			var id = store.QuickAdd("Robin Example", "", Today).Value.Id;
			changes = 0;

			var result = store.UpdateMember(id, m =>
			{
				m.Name = "";
				m.Contact = "robin@example.invalid";
				m.Interests.Add("lathe");
			});

			Assert.That(result.IsValid, Is.False);
			var stored = store.FindMember(id);
			Assert.That(stored.Name, Is.EqualTo("Robin Example"));
			Assert.That(stored.Contact, Is.EqualTo(""));
			Assert.That(stored.Interests, Is.Empty);
			Assert.That(changes, Is.EqualTo(0));
		}

		[Test]
		public void UpdateMemberCommitsAValidEditButKeepsTheId()
		{
			var created = store.QuickAdd("Robin Example", "", Today).Value;
			now = now.AddHours(1);

			var result = store.UpdateMember(created.Id, m =>
			{
				m.Id = "changed";
				m.Contact = " robin@example.invalid ";
				m.Machines.Add("Pulse XE");
			});

			Assert.That(result.IsValid, Is.True);
			var stored = store.FindMember(created.Id);
			Assert.That(stored.Contact, Is.EqualTo("robin@example.invalid"));
			Assert.That(stored.Machines, Is.EqualTo(new[] { "Pulse XE" }));
			Assert.That(stored.UpdatedUtc, Is.EqualTo(now));
			Assert.That(stored.CreatedUtc, Is.EqualTo(created.CreatedUtc));
			Assert.That(store.FindMember("changed"), Is.Null);
		}

		[Test]
		public void UpdateOfAMissingMemberIsRefused()
		{
			Assert.That(store.UpdateMember("nobody", m => m.Name = "Sam Testcase").Problems, Does.Contain(MembersRules.NotFound));
		}

		[Test]
		public void ArchiveIsReversibleAndKeepsTheRecord()
		{
			var id = store.QuickAdd("Robin Example", "", Today).Value.Id;
			Assert.That(store.ArchiveMember(id, true), Is.True);
			Assert.That(store.FindMember(id).Archived, Is.True);
			Assert.That(store.ArchiveMember(id, false), Is.True);
			Assert.That(store.FindMember(id).Archived, Is.False);
			Assert.That(store.ArchiveMember("nobody", true), Is.False);
		}

		[Test]
		public void DeleteMemberRemovesThemFromProjectsAndKeepsOrphanedProjects()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			var sam = store.QuickAdd("Sam Testcase", "", Today).Value.Id;
			var shared = store.AddProject(new Project { Title = "Robot arm", MemberIds = { robin, sam } }).Value.Id;
			var solo = store.AddProject(new Project { Title = "Lamp shade", MemberIds = { robin } }).Value.Id;

			Assert.That(store.DeleteMember(robin), Is.True);

			Assert.That(store.FindMember(robin), Is.Null);
			Assert.That(store.FindProject(shared).MemberIds, Is.EqualTo(new[] { sam }));
			Assert.That(store.FindProject(solo), Is.Not.Null);
			Assert.That(store.FindProject(solo).MemberIds, Is.Empty);
			Assert.That(store.ProjectsWithoutMembers().Select(p => p.Id), Is.EqualTo(new[] { solo }));
			Assert.That(store.DeleteMember(robin), Is.False);
		}

		[Test]
		public void AddProjectNeedsAnExistingMember()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;

			var unknown = store.AddProject(new Project { Title = "Robot arm", MemberIds = { "nobody" } });
			Assert.That(unknown.Ok, Is.False);
			Assert.That(unknown.Validation.Problems, Does.Contain(MembersRules.UnknownMember));

			var none = store.AddProject(new Project { Title = "Robot arm" });
			Assert.That(none.Ok, Is.False);
			Assert.That(none.Validation.Problems, Does.Contain(MembersRules.ProjectNeedsMember));

			var ok = store.AddProject(new Project { Title = "  Robot   arm ", MemberIds = { robin }, Status = ProjectStatus.Stuck });
			Assert.That(ok.Ok, Is.True);
			Assert.That(ok.Value.Title, Is.EqualTo("Robot arm"));
			Assert.That(ok.Value.StatusSince, Is.EqualTo("2026-11-16"));
			Assert.That(store.Snapshot().Projects, Has.Count.EqualTo(1));
			Assert.That(store.ProjectsOf(robin).Select(p => p.Id), Is.EqualTo(new[] { ok.Value.Id }));
		}

		[Test]
		public void AStatusChangeSetsStatusSinceToToday()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			var id = store.AddProject(new Project { Title = "Robot arm", MemberIds = { robin }, StatusSince = "2026-10-01" }).Value.Id;
			Assert.That(store.FindProject(id).StatusSince, Is.EqualTo("2026-10-01"));

			Assert.That(store.UpdateProject(id, p => p.Needs = "help with CAD").IsValid, Is.True);
			Assert.That(store.FindProject(id).StatusSince, Is.EqualTo("2026-10-01"));

			Assert.That(store.UpdateProject(id, p => p.Status = ProjectStatus.Stuck).IsValid, Is.True);
			Assert.That(store.FindProject(id).StatusSince, Is.EqualTo("2026-11-16"));
		}

		[Test]
		public void UpdateProjectRefusesAnUnknownMember()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			var id = store.AddProject(new Project { Title = "Robot arm", MemberIds = { robin } }).Value.Id;

			Assert.That(store.UpdateProject(id, p => p.MemberIds.Add("nobody")).IsValid, Is.False);
			Assert.That(store.FindProject(id).MemberIds, Is.EqualTo(new[] { robin }));
		}

		[Test]
		public void AddLogChecksTheDateAndText()
		{
			var id = store.QuickAdd("Robin Example", "", Today).Value.Id;

			var yesterday = store.AddLog(id, "yesterday", "  asked about nylon  ", Today);
			Assert.That(yesterday.Ok, Is.True);
			Assert.That(yesterday.Value.Date, Is.EqualTo("2026-11-15"));
			Assert.That(yesterday.Value.Text, Is.EqualTo("asked about nylon"));

			Assert.That(store.AddLog(id, "", "printed a bracket", Today).Value.Date, Is.EqualTo("2026-11-16"));
			Assert.That(store.AddLog(id, "16/11/2026", "x", Today).Ok, Is.False);
			Assert.That(store.AddLog(id, "today", "   ", Today).Ok, Is.False);
			Assert.That(store.AddLog(id, "today", new string('x', 501), Today).Ok, Is.False);
			Assert.That(store.AddLog("nobody", "today", "x", Today).Ok, Is.False);
			Assert.That(store.FindMember(id).Log, Has.Count.EqualTo(2));
		}

		[Test]
		public void AddProjectLogAddsADatedNote()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			var id = store.AddProject(new Project { Title = "Robot arm", MemberIds = { robin } }).Value.Id;

			var entry = store.AddProjectLog(id, "2026-11-01", "gripper printed", Today);
			Assert.That(entry.Ok, Is.True);
			Assert.That(store.FindProject(id).Log.Single().Date, Is.EqualTo("2026-11-01"));
		}

		[Test]
		public void DeleteProject()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			var id = store.AddProject(new Project { Title = "Robot arm", MemberIds = { robin } }).Value.Id;
			Assert.That(store.DeleteProject(id), Is.True);
			Assert.That(store.Snapshot().Projects, Is.Empty);
			Assert.That(store.DeleteProject(id), Is.False);
		}

		[Test]
		public void EverythingHandedOutIsACopy()
		{
			var added = store.QuickAdd("Robin Example", "first note", Today).Value;
			added.Name = "";
			added.Log.Clear();

			var found = store.FindMember(added.Id);
			Assert.That(found.Name, Is.EqualTo("Robin Example"));
			Assert.That(found.Log, Has.Count.EqualTo(1));

			found.Name = "changed without a check";
			store.FindMember(added.Id).Interests.Add("lathe");
			Assert.That(store.FindMember(added.Id).Name, Is.EqualTo("Robin Example"));
			Assert.That(store.FindMember(added.Id).Interests, Is.Empty);

			var project = store.AddProject(new Project { Title = "Robot arm", MemberIds = { added.Id } }).Value;
			project.Title = "";
			store.ProjectsOf(added.Id)[0].Title = "";
			Assert.That(store.FindProject(project.Id).Title, Is.EqualTo("Robot arm"));

			var snapshot = store.Snapshot();
			snapshot.Members.Clear();
			Assert.That(store.MemberCount, Is.EqualTo(1));
		}

		[Test]
		public void TheStoreCopiesTheDataItIsGiven()
		{
			var data = new MembersData { Members = { new Member { Id = "r", Name = "Robin Example" } } };
			var own = new MembersStore(data, () => now, () => "x");
			data.Members[0].Name = "";
			Assert.That(own.FindMember("r").Name, Is.EqualTo("Robin Example"));
		}

		[Test]
		public void AnIdCollisionIsRetriedThenFailsCleanly()
		{
			var ids = new System.Collections.Generic.Queue<string>(new[] { A, A, B });
			var own = new MembersStore(new MembersData(), () => now, () => ids.Count > 0 ? ids.Dequeue() : A, () => Today);

			Assert.That(own.QuickAdd("Robin Example", "", Today).Value.Id, Is.EqualTo(A));
			Assert.That(own.QuickAdd("Sam Testcase", "", Today).Value.Id, Is.EqualTo(B), "retried past the used id");

			var third = own.QuickAdd("Alex Sample", "", Today);
			Assert.That(third.Ok, Is.False);
			Assert.That(third.Validation.Problems, Does.Contain(MembersRules.DuplicateId));
			Assert.That(own.MemberCount, Is.EqualTo(2));
		}

		[Test]
		public void ADeletedIdIsNeverUsedAgain()
		{
			var ids = new System.Collections.Generic.Queue<string>(new[] { A, A, C });
			var own = new MembersStore(new MembersData(), () => now, () => ids.Dequeue(), () => Today);
			own.QuickAdd("Robin Example", "", Today);
			own.DeleteMember(A);

			Assert.That(own.QuickAdd("Sam Testcase", "", Today).Value.Id, Is.EqualTo(C));
			Assert.That(own.AddMember(new Member { Id = A, Name = "Alex Sample" }).Ok, Is.False);
		}

		[Test]
		public void DeletingRemembersOnlyTheIdAndTime()
		{
			var id = store.QuickAdd("Robin Example", "", Today).Value.Id;
			store.DeleteMember(id);
			var tombstone = store.Snapshot().DeletedMembers.Single();
			Assert.That(tombstone.Id, Is.EqualTo(id));
			Assert.That(tombstone.DeletedUtc, Is.EqualTo(now));
		}

		[Test]
		public void AnEditStillWorksAfterTheClockMovedBack()
		{
			var id = store.QuickAdd("Robin Example", "note for today", Today).Value.Id;
			Assert.That(store.UpdateMember(id, m => m.MetOn = "2026-11-16").IsValid, Is.True);

			// the computer's clock is set back a week: the stored dates are now "in the future"
			now = now.AddDays(-7);
			Assert.That(store.UpdateMember(id, m => m.Contact = "robin@example.invalid").IsValid, Is.True);

			// a date the edit adds or changes is still checked
			Assert.That(store.UpdateMember(id, m => m.MetOn = "2026-11-15").IsValid, Is.False);
			Assert.That(store.UpdateMember(id, m => m.Log.Add(new LogEntry { Date = "2026-11-16", Text = "new" })).IsValid, Is.False);
			Assert.That(store.UpdateMember(id, m => m.Log[0].Text = "changed").IsValid, Is.False);
			Assert.That(store.FindMember(id).Log[0].Text, Is.EqualTo("note for today"));
		}

		[Test]
		public void AProjectEditStillWorksAfterTheClockMovedBack()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			var id = store.AddProject(new Project { Title = "Robot arm", MemberIds = { robin }, Started = "2026-11-16" }).Value.Id;
			now = now.AddDays(-7);
			Assert.That(store.UpdateProject(id, p => p.Needs = "a gearbox").IsValid, Is.True);
		}

		[Test]
		public void SearchThroughTheStoreUsesAnIndexThatFollowsChanges()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			Assert.That(store.SearchMemberIds("robin", false), Is.EqualTo(new[] { robin }));

			store.UpdateMember(robin, m => m.Interests.Add("nylon"));
			Assert.That(store.SearchMemberIds("nylon", false), Is.EqualTo(new[] { robin }));

			store.ArchiveMember(robin, true);
			Assert.That(store.SearchMemberIds("nylon", false), Is.Empty);

			var sam = store.QuickAdd("Sam Testcase", "", Today).Value.Id;
			var project = store.AddProject(new Project { Title = "Lamp shade", MemberIds = { sam }, HelpWanted = true }).Value.Id;
			Assert.That(store.FilterProjectIds(new ProjectFilter { HelpWantedOnly = true }), Is.EqualTo(new[] { project }));
			Assert.That(store.SearchMemberIds("lamp", false), Is.EqualTo(new[] { sam }));
		}

		[Test]
		public void AnIdNotInTheAppsFormatIsRefused()
		{
			Assert.That(store.AddMember(new Member { Id = "robin", Name = "Robin Example" }).Validation.Problems, Does.Contain(MembersRules.IdInvalid));
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			Assert.That(store.AddProject(new Project { Id = "Robot arm", Title = "Robot arm", MemberIds = { robin } }).Validation.Problems, Does.Contain(MembersRules.IdInvalid));

			// a generator that makes free text is retried, then refused cleanly
			var own = new MembersStore(new MembersData(), () => now, () => "Robin Example", () => Today);
			Assert.That(own.QuickAdd("Sam Testcase", "", Today).Validation.Problems, Does.Contain(MembersRules.DuplicateId));
		}

		[Test]
		public void DeletingAHandMadeIdLeavesNoTraceOfIt()
		{
			// data made by hand (not by the store) may hold a free-text id; deleting it writes no deleted entry
			var own = new MembersStore(new MembersData { Members = { new Member { Id = "Robin Example", Name = "Robin Example" } } }, () => now, () => Fixed);
			Assert.That(own.DeleteMember("Robin Example"), Is.True);
			Assert.That(own.Snapshot().DeletedMembers, Is.Empty);
		}

		[Test]
		public void TheIndexFollowsRenamesIntoProjectSearch()
		{
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			var arm = store.AddProject(new Project { Title = "Robot arm", MemberIds = { robin } }).Value.Id;
			Assert.That(store.FilterProjectIds(new ProjectFilter { Query = "robin" }), Is.EqualTo(new[] { arm }));

			store.UpdateMember(robin, m => m.Name = "Sam Testcase");
			Assert.That(store.FilterProjectIds(new ProjectFilter { Query = "robin" }), Is.Empty);
			Assert.That(store.FilterProjectIds(new ProjectFilter { Query = "testcase" }), Is.EqualTo(new[] { arm }));

			store.UpdateProject(arm, p => p.Title = "Lamp shade");
			Assert.That(store.SearchMemberIds("lamp", false), Is.EqualTo(new[] { robin }));
			Assert.That(store.SearchMemberIds("robot", false), Is.Empty);

			store.DeleteProject(arm);
			Assert.That(store.SearchMemberIds("lamp", false), Is.Empty);
			store.DeleteMember(robin);
			Assert.That(store.SearchMemberIds("", true), Is.Empty);
		}
	}
}
