using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members: importance (0 to 3 stars) and role. File compatibility with the version 1 format, the rules, the order
	/// and filter of the list, search, the drafts and the export. Every name here is invented.
	/// </summary>
	[TestFixture]
	public class MembersImportanceTests
	{
		private static readonly DateTime Today = new DateTime(2026, 11, 16);

		/// <summary>
		/// The decrypted members data as the version 1 code wrote it. Produced once, before Importance and Role were added,
		/// by a throwaway test that built this sample MembersData by hand (invented names) and wrote the output of the
		/// then-current MembersExport.ToJsonBytes to a file; that output is pasted here unchanged, split into string
		/// pieces. It is not read from any real members file. A real file in this format exists; it must keep opening,
		/// losing nothing.
		/// </summary>
		private const string VersionOnePayload =
			"{\"SchemaVersion\":1,\"Members\":[{\"Id\":\"000000000000000000000000000000a1\",\"Name\":\"Morgan Fixture\","
			+ "\"Contact\":\"morgan@example.invalid\",\"MetOn\":\"2026-10-03\",\"Interests\":[\"nylon\"],\"Machines\":[\"Pulse XE\"],"
			+ "\"Notes\":\"two\\nlines\",\"Log\":[{\"Id\":\"000000000000000000000000000000b1\",\"Date\":\"2026-11-02\",\"Text\":\"first visit\","
			+ "\"CreatedUtc\":\"2026-11-02T19:00:00Z\"}],\"Archived\":false,\"CreatedUtc\":\"2026-10-03T18:00:00Z\",\"UpdatedUtc\":\"2026-11-02T19:00:00Z\"},"
			+ "{\"Id\":\"000000000000000000000000000000a2\",\"Name\":\"Quinn Placeholder\",\"Contact\":\"\",\"MetOn\":\"\",\"Interests\":[],"
			+ "\"Machines\":[],\"Notes\":\"\",\"Log\":[],\"Archived\":true,\"CreatedUtc\":\"2026-09-01T18:00:00Z\",\"UpdatedUtc\":\"2026-09-05T18:00:00Z\"}],"
			+ "\"Projects\":[{\"Id\":\"000000000000000000000000000000c1\",\"Title\":\"Cable clip\",\"Status\":\"InProgress\",\"StatusSince\":\"2026-11-01\","
			+ "\"MemberIds\":[\"000000000000000000000000000000a1\"],\"Needs\":\"\",\"Machines\":[],\"HelpWanted\":false,\"Notes\":\"\",\"Started\":\"\","
			+ "\"Log\":[],\"Link\":null,\"CreatedUtc\":\"2026-11-01T18:00:00Z\",\"UpdatedUtc\":\"2026-11-01T18:00:00Z\"}],"
			+ "\"DeletedMembers\":[{\"Id\":\"000000000000000000000000000000d1\",\"DeletedUtc\":\"2026-08-01T00:00:00Z\"}],\"DeletedProjects\":[]}";

		private const string Pin = "amber-lantern-42";

		private static string Id(int n) => n.ToString("x32");

		private string root;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "ebmc-members-importance-test-" + Guid.NewGuid().ToString("N"));
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}

		// --- the file format ---

		[Test]
		public void TodaysVersionOnePayloadOpensWithNoImportanceAndNoRoleAndLosesNothing()
		{
			var data = MembersExport.ParseJson(VersionOnePayload);
			Assert.That(data.Members.Select(m => m.Importance), Is.EqualTo(new[] { 0, 0 }));
			Assert.That(data.Members.Select(m => m.Role), Is.EqualTo(new[] { "", "" }));
			Assert.That(data.SchemaVersion, Is.EqualTo(MembersData.CurrentSchemaVersion));

			// written back: the same text, apart from the version number and the two new fields (with their defaults)
			var written = Encoding.UTF8.GetString(MembersExport.ToJsonBytes(data));
			Assert.That(written, Does.StartWith("{\"SchemaVersion\":2,"));
			Assert.That(written, Does.Contain("\"Name\":\"Morgan Fixture\",\"Importance\":0,\"Role\":\"\","));
			var withoutNewFields = written
				.Replace("{\"SchemaVersion\":2,", "{\"SchemaVersion\":1,")
				.Replace("\"Importance\":0,\"Role\":\"\",", "");
			Assert.That(withoutNewFields, Is.EqualTo(VersionOnePayload), "every version 1 field is kept as it was");

			// and that opens again to the same data
			Assert.That(Encoding.UTF8.GetString(MembersExport.ToJsonBytes(MembersExport.ParseJson(written))), Is.EqualTo(written));
		}

		[Test]
		public void AVersionOneMembersFileOpensSavesAndOpensAgain()
		{
			// the real file's envelope, with today's payload inside, under a passphrase
			Directory.CreateDirectory(root);
			var key = MembersCrypto.NewDataKey();
			var file = Path.Combine(root, MembersVault.FileName);
			File.WriteAllBytes(file, MembersCrypto.SealFile(Encoding.UTF8.GetBytes(VersionOnePayload), key, MembersCrypto.WrapKey(key, Pin)));

			var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
			var vault = new MembersVault(root, () => now);
			var opened = vault.Open(Pin);
			Assert.That(opened.Status, Is.EqualTo(OpenStatus.Opened));
			Assert.That(opened.Data.Members.Select(m => m.Name), Is.EqualTo(new[] { "Morgan Fixture", "Quinn Placeholder" }));
			Assert.That(opened.Data.Members.All(m => m.Importance == 0 && m.Role == ""), Is.True);

			// an edit through the store, saved and opened by a fresh vault
			var store = new MembersStore(opened.Data, () => now, null, () => Today);
			Assert.That(store.UpdateMember(Id(0xa1), m =>
			{
				m.Importance = 3;
				m.Role = "Founder";
			}).IsValid, Is.True);
			Assert.That(vault.Save(store.Snapshot()).Status, Is.EqualTo(SaveStatus.Saved));

			var again = new MembersVault(root, () => now).Open(Pin);
			Assert.That(again.Status, Is.EqualTo(OpenStatus.Opened));
			var morgan = again.Data.Members.Single(m => m.Id == Id(0xa1));
			Assert.That(morgan.Importance, Is.EqualTo(3));
			Assert.That(morgan.Role, Is.EqualTo("Founder"));
			Assert.That(morgan.Notes, Is.EqualTo("two\nlines"));
			Assert.That(morgan.Log.Single().Text, Is.EqualTo("first visit"));
			Assert.That(again.Data.Members.Single(m => m.Id == Id(0xa2)).Archived, Is.True);
			Assert.That(again.Data.Projects.Single().MemberIds, Is.EqualTo(new[] { Id(0xa1) }));
			Assert.That(again.Data.DeletedMembers.Single().Id, Is.EqualTo(Id(0xd1)));
		}

		[Test]
		public void AFileWithTheNewFieldsIsMarkedSoAnOlderBuildRefusesIt()
		{
			// a version 1 build ignores fields it does not know and would drop them on its next save; the version number
			// makes it stop at NewerVersion instead (it then changes nothing)
			var data = MembersExport.ParseJson(VersionOnePayload);
			data.Members[0].Importance = 2;
			var json = MembersExport.ToJson(data);
			Assert.That(MembersData.CurrentSchemaVersion, Is.GreaterThan(1));
			Assert.That(MembersExport.ReadSchemaVersion(Encoding.UTF8.GetBytes(json)), Is.EqualTo(MembersData.CurrentSchemaVersion));

			// and this build refuses a file from a build after it, for the same reason
			var newer = json.Replace("\"SchemaVersion\": " + MembersData.CurrentSchemaVersion, "\"SchemaVersion\": " + (MembersData.CurrentSchemaVersion + 1));
			Assert.That(Assert.Throws<MembersFileException>(() => MembersExport.ParseJson(newer)).Problem, Is.EqualTo(MembersFileProblem.NewerVersion));
		}

		// --- the rules ---

		[Test]
		public void ImportanceIsZeroToThree()
		{
			foreach (var good in new[] { 0, 1, 2, 3 })
			{
				Assert.That(MembersRules.ValidateImportance(good).IsValid, Is.True, good.ToString());
			}

			foreach (var bad in new[] { -1, 4, int.MaxValue, int.MinValue })
			{
				Assert.That(MembersRules.ValidateImportance(bad).Problems, Is.EqualTo(new[] { MembersRules.ImportanceInvalid }), bad.ToString());
			}

			var member = new Member { Id = Id(1), Name = "Robin Example", Importance = 4 };
			Assert.That(MembersRules.ValidateMember(member, Today).Problems, Is.EqualTo(new[] { MembersRules.ImportanceInvalid }));
			Assert.That(MembersRules.MaxImportance, Is.EqualTo(3));
		}

		[Test]
		public void TheRoleIsTrimmedOneShortLineWithoutControlCharacters()
		{
			var member = new Member { Id = Id(1), Name = "Robin Example", Role = "  Founder \t" };
			MembersRules.CleanMember(member);
			Assert.That(member.Role, Is.EqualTo("Founder"));
			Assert.That(MembersRules.ValidateMember(member, Today).IsValid, Is.True);

			Assert.That(MembersRules.RoleMaxLength, Is.EqualTo(40));
			Assert.That(MembersRules.ValidateRole("").IsValid, Is.True, "empty is allowed");
			Assert.That(MembersRules.ValidateRole(null).IsValid, Is.True);
			Assert.That(MembersRules.ValidateRole(new string('r', 40)).IsValid, Is.True);
			Assert.That(MembersRules.ValidateRole(new string('r', 41)).Problems, Is.EqualTo(new[] { MembersRules.RoleTooLong }));

			// counted in characters a person sees: 40 accented letters typed as letter + combining mark are 40
			Assert.That(MembersRules.ValidateRole(string.Concat(Enumerable.Repeat("é", 40))).IsValid, Is.True);

			foreach (var bad in new[] { "Board\nmember", "Board\tmember", "Board\rmember", "Board\u0007", "‮Board", "Board\uD800" })
			{
				Assert.That(MembersRules.ValidateRole(bad).Problems, Is.EqualTo(new[] { MembersRules.RoleInvalidCharacters }), bad);
			}

			var twoLines = new Member { Id = Id(1), Name = "Robin Example", Role = "Board\nmember" };
			MembersRules.CleanMember(twoLines);
			Assert.That(MembersRules.ValidateMember(twoLines, Today).Problems, Does.Contain(MembersRules.RoleInvalidCharacters));
		}

		[Test]
		public void ChangingImportanceOrRoleIsAnOrdinaryEdit()
		{
			var now = new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc);
			int next = 0;
			var store = new MembersStore(new MembersData(), () => now, () => Id(++next), () => Today);
			var robin = store.QuickAdd("Robin Example", "", Today).Value.Id;
			int changes = 0;
			store.Changed += (s, e) => changes++;

			now = now.AddMinutes(5);
			Assert.That(store.UpdateMember(robin, m =>
			{
				m.Importance = 3;
				m.Role = "  Founder ";
			}).IsValid, Is.True);
			var stored = store.FindMember(robin);
			Assert.That(stored.Importance, Is.EqualTo(3));
			Assert.That(stored.Role, Is.EqualTo("Founder"));
			Assert.That(stored.UpdatedUtc, Is.EqualTo(now));
			Assert.That(changes, Is.EqualTo(1));

			// refused edits leave the member as it was
			now = now.AddMinutes(5);
			Assert.That(store.UpdateMember(robin, m => m.Importance = 4).Problems, Is.EqualTo(new[] { MembersRules.ImportanceInvalid }));
			Assert.That(store.UpdateMember(robin, m => m.Role = new string('r', 41)).Problems, Is.EqualTo(new[] { MembersRules.RoleTooLong }));
			Assert.That(store.FindMember(robin).Importance, Is.EqualTo(3));
			Assert.That(store.FindMember(robin).Role, Is.EqualTo("Founder"));
			Assert.That(store.FindMember(robin).UpdatedUtc, Is.EqualTo(now.AddMinutes(-5)));
			Assert.That(changes, Is.EqualTo(1));
		}

		[Test]
		public void AnOutOfRangeValueFromAFileDoesNotBlockOtherEdits()
		{
			// only possible in a hand-edited file; like the date met, a value an edit does not touch is not checked again
			var data = new MembersData { Members = { new Member { Id = Id(1), Name = "Robin Example", Importance = 7, Role = new string('r', 60) } } };
			var store = new MembersStore(data, () => new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc), null, () => Today);
			Assert.That(store.UpdateMember(Id(1), m => m.Contact = "robin@example.invalid").IsValid, Is.True);
			Assert.That(store.FindMember(Id(1)).Importance, Is.EqualTo(7), "kept as it is, never silently changed");
			Assert.That(store.UpdateMember(Id(1), m => m.Importance = 2).IsValid, Is.True);
			Assert.That(store.FindMember(Id(1)).Importance, Is.EqualTo(2));
		}

		// --- order, filter and search ---

		private static MembersData People()
		{
			DateTime On(int day) => new DateTime(2026, 11, day, 12, 0, 0, DateTimeKind.Utc);
			return new MembersData
			{
				Members =
				{
					new Member { Id = "recent", Name = "Robin Example", UpdatedUtc = On(14) },
					new Member { Id = "founder", Name = "Sam Testcase", Importance = 3, Role = "Founder", UpdatedUtc = On(1) },
					new Member { Id = "board-old", Name = "Alex Sample", Importance = 2, Role = "Board", UpdatedUtc = On(3) },
					new Member { Id = "board-new", Name = "Jordan Example", Importance = 2, Notes = "came with robin", UpdatedUtc = On(10) },
					new Member { Id = "helper", Name = "Casey Placeholder", Importance = 1, UpdatedUtc = On(5) },
					new Member { Id = "older", Name = "Drew Fixture", UpdatedUtc = On(2) },
					new Member { Id = "left-star", Name = "Avery Testcase", Importance = 2, Archived = true, UpdatedUtc = On(12) },
					new Member { Id = "left", Name = "Blake Sample", Archived = true, UpdatedUtc = On(15) },
				},
			};
		}

		private static IEnumerable<string> Ids(IEnumerable<Member> members) => members.Select(m => m.Id);

		[Test]
		public void HigherImportanceComesFirstThenTheMostRecentActivity()
		{
			Assert.That(
				Ids(MembersSearch.SearchMembers(People(), "", false)),
				Is.EqualTo(new[] { "founder", "board-new", "board-old", "helper", "recent", "older" }));
		}

		[Test]
		public void WithNoImportanceAnywhereTheOrderIsExactlyAsBefore()
		{
			var data = People();
			foreach (var member in data.Members)
			{
				member.Importance = 0;
			}

			// most recent activity first, as before importance existed
			Assert.That(
				Ids(MembersSearch.SearchMembers(data, "", true)),
				Is.EqualTo(new[] { "left", "recent", "left-star", "board-new", "helper", "board-old", "older", "founder" }));
		}

		[Test]
		public void ArchivedMembersAreMixedInAsBeforeWithImportanceAppliedWithin()
		{
			// archived members are only listed with Show archived, and then take their place by the same order
			Assert.That(
				Ids(MembersSearch.SearchMembers(People(), "", true)),
				Is.EqualTo(new[] { "founder", "left-star", "board-new", "board-old", "helper", "left", "recent", "older" }));
		}

		[Test]
		public void ImportanceComesFirstWhenSearchingToo()
		{
			// "example" is in Robin's and Jordan's names (Robin's activity is more recent); Jordan has two stars
			Assert.That(Ids(MembersSearch.SearchMembers(People(), "example", false)), Is.EqualTo(new[] { "board-new", "recent" }));

			// higher importance even beats a better match: Robin's name starts with "robin", Jordan's notes mention robin
			Assert.That(Ids(MembersSearch.SearchMembers(People(), "robin", false)), Is.EqualTo(new[] { "board-new", "recent" }));

			// within one level the match order is as before: a name match before a notes match
			var data = People();
			data.Members.Single(m => m.Id == "board-new").Importance = 0;
			Assert.That(Ids(MembersSearch.SearchMembers(data, "robin", false)), Is.EqualTo(new[] { "recent", "board-new" }));
		}

		[Test]
		public void TheFilterCombinesWithSearchAndShowArchived()
		{
			var data = People();
			Assert.That(Ids(MembersSearch.SearchMembers(data, "", false, 1)), Is.EqualTo(new[] { "founder", "board-new", "board-old", "helper" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "", false, 2)), Is.EqualTo(new[] { "founder", "board-new", "board-old" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "", false, 3)), Is.EqualTo(new[] { "founder" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "", true, 2)), Is.EqualTo(new[] { "founder", "left-star", "board-new", "board-old" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "testcase", true, 2)), Is.EqualTo(new[] { "founder", "left-star" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "testcase", false, 2)), Is.EqualTo(new[] { "founder" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "testcase", false, 0)), Is.EqualTo(new[] { "founder" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "example", false, 3)), Is.Empty);
		}

		[Test]
		public void TheRoleIsSearchable()
		{
			Assert.That(Ids(MembersSearch.SearchMembers(People(), "founder", false)), Is.EqualTo(new[] { "founder" }));
			Assert.That(Ids(MembersSearch.SearchMembers(People(), "BOARD", false)), Is.EqualTo(new[] { "board-old" }));

			// through the store's kept index, after an edit
			var now = new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc);
			var store = new MembersStore(new MembersData { Members = { new Member { Id = Id(1), Name = "Robin Example" } } }, () => now, null, () => Today);
			Assert.That(store.SearchMemberIds("instructor", false), Is.Empty);
			store.UpdateMember(Id(1), m => m.Role = "Instructor");
			Assert.That(store.SearchMemberIds("instructor", false), Is.EqualTo(new[] { Id(1) }));
			store.UpdateMember(Id(1), m => m.Importance = 1);
			Assert.That(store.SearchMemberIds("", false, 2), Is.Empty);
			Assert.That(store.SearchMemberIds("", false, 1), Is.EqualTo(new[] { Id(1) }));
		}

		// --- the session: drafts, the picker and the filter ---

		private MembersSessionCore OpenWithRobin(out string robin)
		{
			var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
			int ids = 0;
			var core = new MembersSessionCore(Path.Combine(root, "Members"), () => now, wait: ms => { }, newId: () => Id(++ids), today: () => now.Date);
			Assert.That(core.CreateList(Pin, Pin), Is.Null);
			var added = core.QuickAdd("Robin Example", "");
			Assert.That(added.Ok, Is.True);
			robin = added.Value.Id;
			core.SaveNow();
			return core;
		}

		[Test]
		public void TheTypedRoleSurvivesARebuildAndIsStoredWhenCommitted()
		{
			var core = OpenWithRobin(out var robin);
			core.Drafts.SetField(robin, MemberField.Role, "  Founder ");

			// a rebuilt page reads the typed text back
			Assert.That(core.FieldText(core.Store.FindMember(robin), MemberField.Role), Is.EqualTo("  Founder "));
			Assert.That(core.Store.FindMember(robin).Role, Is.Empty);

			core.SaveNow();
			Assert.That(core.Store.FindMember(robin).Role, Is.EqualTo("Founder"));
			Assert.That(core.Drafts.HasField(robin, MemberField.Role), Is.False);
			Assert.That(core.Dirty, Is.False, "saved");

			// the same text again (spaces around it) is no change
			core.Drafts.SetField(robin, MemberField.Role, "Founder ");
			core.CommitDetailDrafts();
			Assert.That(core.Dirty, Is.False);
			Assert.That(core.Drafts.HasField(robin, MemberField.Role), Is.False);
		}

		[Test]
		public void AnInvalidRoleStaysTypedWithItsProblem()
		{
			var core = OpenWithRobin(out var robin);
			core.Drafts.SetField(robin, MemberField.Role, new string('r', 41));
			core.Drafts.SetField(robin, MemberField.Contact, "robin@example.invalid");

			var problems = core.CommitDetailDrafts();
			Assert.That(problems.Problems, Is.EqualTo(new[] { MembersRules.RoleTooLong }));
			Assert.That(core.Store.FindMember(robin).Role, Is.Empty);
			Assert.That(core.Store.FindMember(robin).Contact, Is.EqualTo("robin@example.invalid"), "the other fields are stored");
			Assert.That(core.FieldText(core.Store.FindMember(robin), MemberField.Role), Is.EqualTo(new string('r', 41)));

			core.Drafts.SetField(robin, MemberField.Role, "Board\nmember");
			Assert.That(core.CommitDetailDrafts().Problems, Is.EqualTo(new[] { MembersRules.RoleInvalidCharacters }));

			core.Drafts.SetField(robin, MemberField.Role, "Board");
			Assert.That(core.CommitDetailDrafts().IsValid, Is.True);
			Assert.That(core.Store.FindMember(robin).Role, Is.EqualTo("Board"));
		}

		[Test]
		public void PickingAnImportanceIsOneEditAndIsSaved()
		{
			var core = OpenWithRobin(out var robin);
			core.Drafts.SetField(robin, MemberField.Role, "Founder");

			Assert.That(core.SetImportance(3).IsValid, Is.True);
			Assert.That(core.Store.FindMember(robin).Importance, Is.EqualTo(3));
			Assert.That(core.Store.FindMember(robin).Role, Is.EqualTo("Founder"), "the typed role is stored first");
			Assert.That(core.Dirty, Is.True);

			core.SaveNow();
			Assert.That(core.Dirty, Is.False);
			Assert.That(core.SetImportance(3).IsValid, Is.True);
			Assert.That(core.Dirty, Is.False, "the level already set changes nothing");
			Assert.That(core.SetImportance(4).Problems, Is.EqualTo(new[] { MembersRules.ImportanceInvalid }));
			Assert.That(core.SetImportance(-1).IsValid, Is.False);
			Assert.That(core.Store.FindMember(robin).Importance, Is.EqualTo(3));

			// locked and opened again: kept in the file
			core.Lock();
			Assert.That(core.Unlock(Pin), Is.Null);
			Assert.That(core.Store.FindMember(robin).Importance, Is.EqualTo(3));
			Assert.That(core.Store.FindMember(robin).Role, Is.EqualTo("Founder"));

			// the selection is kept over the lock: back to none
			Assert.That(core.SetImportance(0).IsValid, Is.True);
			Assert.That(core.Store.FindMember(robin).Importance, Is.EqualTo(0));
		}

		[Test]
		public void TheListFilterIsAViewSettingKeptForTheSession()
		{
			var core = OpenWithRobin(out var robin);
			var sam = core.QuickAdd("Sam Testcase", "").Value.Id;
			Assert.That(core.SetImportance(2).IsValid, Is.True, "Sam is selected after the quick add");
			core.SaveNow();

			Assert.That(core.ListMembers(out _).Select(m => m.Id), Is.EqualTo(new[] { sam, robin }));
			core.Drafts.MinImportance = 1;
			Assert.That(core.ListMembers(out int total).Select(m => m.Id), Is.EqualTo(new[] { sam }));
			Assert.That(total, Is.EqualTo(1));
			core.Drafts.SearchText = "robin";
			Assert.That(core.ListMembers(out _), Is.Empty);
			core.Drafts.SearchText = "";

			// not a change to the data, and never in the file
			Assert.That(core.Dirty, Is.False);

			// kept over a lock (like Show archived); cleared when the app closes
			core.Lock();
			Assert.That(core.Drafts.MinImportance, Is.EqualTo(1));
			core.Unlock(Pin);
			Assert.That(core.ListMembers(out _).Select(m => m.Id), Is.EqualTo(new[] { sam }));
			core.CloseForExit();
			Assert.That(core.Drafts.MinImportance, Is.EqualTo(0));
		}

		// --- version 1 files next to version 2 ones: previous copy, backups, restore, passphrase change ---

		private const string NewPin = "violet-harbor-77";

		private string MembersFolder => Path.Combine(root, "Members");

		/// <summary>A members file holding <see cref="VersionOnePayload"/> under <see cref="Pin"/>, opened by a session.</summary>
		private MembersSessionCore SessionOnVersionOneFile()
		{
			Directory.CreateDirectory(MembersFolder);
			var key = MembersCrypto.NewDataKey();
			File.WriteAllBytes(Path.Combine(MembersFolder, MembersVault.FileName), MembersCrypto.SealFile(Encoding.UTF8.GetBytes(VersionOnePayload), key, MembersCrypto.WrapKey(key, Pin)));

			var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
			int ids = 0;
			var core = new MembersSessionCore(MembersFolder, () => now, wait: ms => { }, newId: () => Id(0x100 + ++ids), today: () => now.Date);
			Assert.That(core.Unlock(Pin), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			return core;
		}

		/// <summary>The data in an encrypted members file and its schema version as written, or null when
		/// <paramref name="pin"/> does not open it.</summary>
		private static MembersData Read(string path, string pin, out int version)
		{
			version = -1;
			if (MembersCrypto.OpenFile(File.ReadAllBytes(path), pin, out var plain, out _, out _) != EnvelopeOpenStatus.Ok)
			{
				return null;
			}

			version = MembersExport.ReadSchemaVersion(plain);
			return MembersExport.ParseJson(plain);
		}

		private static void AssertVersionOneMembers(MembersData data)
		{
			Assert.That(data, Is.Not.Null);
			Assert.That(data.Members.Select(m => m.Name), Is.EqualTo(new[] { "Morgan Fixture", "Quinn Placeholder" }));
			var morgan = data.Members[0];
			Assert.That(morgan.Notes, Is.EqualTo("two\nlines"));
			Assert.That(morgan.Log.Single().Text, Is.EqualTo("first visit"));
			Assert.That(morgan.Interests, Is.EqualTo(new[] { "nylon" }));
			Assert.That(data.Members[1].Archived, Is.True);
			Assert.That(data.Projects.Single().MemberIds, Is.EqualTo(new[] { Id(0xa1) }));
			Assert.That(data.DeletedMembers.Single().Id, Is.EqualTo(Id(0xd1)));
		}

		/// <summary>Opens a version 1 file and saves one change: the main file becomes version 2.</summary>
		private MembersSessionCore FirstSaveOfAVersionOneFile()
		{
			var core = SessionOnVersionOneFile();
			var main = core.Vault.FilePath;

			// opening alone does not rewrite the file
			Read(main, Pin, out int beforeSave);
			Assert.That(beforeSave, Is.EqualTo(1));
			Assert.That(core.Dirty, Is.False);

			core.Select(Id(0xa1));
			Assert.That(core.SetImportance(3).IsValid, Is.True);
			core.SaveNow();
			Assert.That(core.Dirty, Is.False);
			Read(main, Pin, out int afterSave);
			Assert.That(afterSave, Is.EqualTo(2));
			return core;
		}

		[Test]
		public void AfterTheFirstSaveThePreviousCopyAndTheDaysBackupStillHoldTheVersionOneFile()
		{
			var core = FirstSaveOfAVersionOneFile();

			var previous = Read(core.Vault.PreviousPath, Pin, out int previousVersion);
			Assert.That(previousVersion, Is.EqualTo(1));
			AssertVersionOneMembers(previous);

			var backup = core.Vault.ListBackups().Single();
			var backedUp = Read(backup, Pin, out int backupVersion);
			Assert.That(backupVersion, Is.EqualTo(1));
			AssertVersionOneMembers(backedUp);
			Assert.That(backedUp.Members.All(m => m.Importance == 0), Is.True, "the copy from before the change");
		}

		[Test]
		public void RestoringAVersionOneBackupOverAVersionTwoFileLosesNothingAndTheNextSaveIsVersionTwo()
		{
			var core = FirstSaveOfAVersionOneFile();
			Assert.That(core.RestoreNewestBackup(Pin, ""), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			var restored = core.Store.Snapshot();
			AssertVersionOneMembers(restored);
			Assert.That(restored.Members.All(m => m.Importance == 0 && m.Role == ""), Is.True);

			core.Select(Id(0xa2));
			Assert.That(core.SetImportance(1).IsValid, Is.True);
			core.SaveNow();
			var saved = Read(core.Vault.FilePath, Pin, out int version);
			Assert.That(version, Is.EqualTo(2));
			AssertVersionOneMembers(saved);
			Assert.That(saved.Members[1].Importance, Is.EqualTo(1));
		}

		[Test]
		public void RestoringAVersionOnePreviousCopyOverAVersionTwoFileLosesNothingAndTheNextSaveIsVersionTwo()
		{
			var core = FirstSaveOfAVersionOneFile();
			Assert.That(core.RestorePrevious(Pin, ""), Is.Null);
			Assert.That(core.State, Is.EqualTo(MembersSessionState.Open));
			AssertVersionOneMembers(core.Store.Snapshot());

			core.Select(Id(0xa1));
			core.Drafts.SetField(Id(0xa1), MemberField.Role, "Founder");
			core.SaveNow();
			var saved = Read(core.Vault.FilePath, Pin, out int version);
			Assert.That(version, Is.EqualTo(2));
			AssertVersionOneMembers(saved);
			Assert.That(saved.Members[0].Role, Is.EqualTo("Founder"));
		}

		[Test]
		public void ChangingThePassphraseKeepsAVersionTwoMainFileAndAVersionOneBackupOpenable()
		{
			var core = FirstSaveOfAVersionOneFile();
			var backup = core.Vault.ListBackups().Single();
			Assert.That(core.ChangePassphrase(Pin, NewPin, NewPin), Is.Null);

			var main = Read(core.Vault.FilePath, NewPin, out int mainVersion);
			Assert.That(mainVersion, Is.EqualTo(2));
			AssertVersionOneMembers(main);
			Assert.That(main.Members[0].Importance, Is.EqualTo(3));

			var backedUp = Read(backup, NewPin, out int backupVersion);
			Assert.That(backupVersion, Is.EqualTo(1));
			AssertVersionOneMembers(backedUp);

			Assert.That(Read(core.Vault.FilePath, Pin, out _), Is.Null, "the old passphrase no longer opens it");
			Assert.That(Read(backup, Pin, out _), Is.Null);
		}

		[Test]
		public void TheLockReportCountsAnInvalidRoleThatWasNotStored()
		{
			var core = OpenWithRobin(out var robin);
			core.Drafts.SetField(robin, MemberField.Role, new string('r', 41));
			var report = core.Lock();
			Assert.That(report.NotSaved, Is.False);
			Assert.That(report.DiscardedEntries, Is.EqualTo(1));
			Assert.That(core.Unlock(Pin), Is.Null);
			Assert.That(core.Store.FindMember(robin).Role, Is.Empty);
		}

		// --- follow-ups: role characters, clamped levels, the empty list ---

		[Test]
		public void LineAndParagraphSeparatorsAreRefusedInARole()
		{
			Assert.That(char.IsControl('\u2028'), Is.False, "why they need their own check");
			foreach (var bad in new[] { "Board\u2028member", "Board\u2029member" })
			{
				Assert.That(MembersRules.ValidateRole(bad).Problems, Is.EqualTo(new[] { MembersRules.RoleInvalidCharacters }));
				var member = new Member { Id = Id(1), Name = "Robin Example", Role = bad };
				MembersRules.CleanMember(member);
				Assert.That(MembersRules.ValidateMember(member, Today).Problems, Does.Contain(MembersRules.RoleInvalidCharacters));
			}
		}

		[Test]
		public void ARoleCannotPileCombiningMarksOnOneCharacter()
		{
			// one text element with a hundred marks would count as one character of the 40
			var piled = "B" + new string('\u0301', 100) + "oard";
			Assert.That(MembersRules.ValidateRole(piled).Problems, Is.EqualTo(new[] { MembersRules.RoleInvalidCharacters }));
			Assert.That(MembersRules.ValidateRole(string.Concat(Enumerable.Repeat("x" + new string('\u0301', 50), 40))).IsValid, Is.False);

			// the same limit as names: up to four marks on a character is fine (Vietnamese needs two)
			Assert.That(MembersRules.ValidateRole("Tru\u031B\u0300o\u031B\u0309ng ban").IsValid, Is.True);
			Assert.That(MembersRules.ValidateRole("B" + new string('\u0301', MembersRules.MaxMarksPerCharacter)).IsValid, Is.True);
			Assert.That(MembersRules.ValidateRole("B" + new string('\u0301', MembersRules.MaxMarksPerCharacter + 1)).IsValid, Is.False);
		}

		[Test]
		public void AnOutOfRangeImportanceSortsAndFiltersAsTheLevelItIsShownAs()
		{
			DateTime On(int day) => new DateTime(2026, 11, day, 12, 0, 0, DateTimeKind.Utc);
			var data = new MembersData
			{
				Members =
				{
					new Member { Id = "nine", Name = "Robin Example", Importance = 9, UpdatedUtc = On(1) },
					new Member { Id = "three", Name = "Sam Testcase", Importance = 3, UpdatedUtc = On(5) },
					new Member { Id = "minus", Name = "Alex Sample", Importance = -1, UpdatedUtc = On(9) },
					new Member { Id = "zero", Name = "Jordan Example", Importance = 0, UpdatedUtc = On(3) },
				},
			};

			Assert.That(MembersRules.ClampImportance(9), Is.EqualTo(3));
			Assert.That(MembersRules.ClampImportance(-1), Is.EqualTo(0));

			// 9 counts as three stars (then the most recent first), -1 as none
			Assert.That(Ids(MembersSearch.SearchMembers(data, "", false)), Is.EqualTo(new[] { "three", "nine", "minus", "zero" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "", false, 3)), Is.EqualTo(new[] { "three", "nine" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "", false, 1)), Is.EqualTo(new[] { "three", "nine" }));

			// the stored values are not changed
			Assert.That(data.Members.Select(m => m.Importance), Is.EqualTo(new[] { 9, 3, -1, 0 }));
			var store = new MembersStore(data, () => On(16), null, () => Today);
			Assert.That(store.SearchMemberIds("", false, 3), Is.EqualTo(new[] { "three", "nine" }));
			Assert.That(store.Snapshot().Members.Select(m => m.Importance), Is.EqualTo(new[] { 9, 3, -1, 0 }));
		}

		[Test]
		public void TheEmptyListSaysWhatHidesTheMatches()
		{
			var core = OpenWithRobin(out var robin);
			var sam = core.QuickAdd("Sam Testcase", "").Value.Id;
			Assert.That(core.SetImportance(2).IsValid, Is.True, "Sam");
			var alex = core.QuickAdd("Alex Sample", "").Value.Id;
			Assert.That(core.SetImportance(3).IsValid, Is.True, "Alex");
			Assert.That(core.SetArchived(true), Is.True, "Alex has left");

			// a search and the star filter: removing the filter would show Robin
			core.Drafts.SearchText = "robin";
			core.Drafts.MinImportance = 1;
			Assert.That(core.ListMembers(out _), Is.Empty);
			Assert.That(core.EmptyListMessage(), Is.EqualTo(MembersSessionText.ListEmptyMatchesWithFewerStars));

			// the filter alone: the only member with three stars is archived
			core.Drafts.SearchText = "";
			core.Drafts.MinImportance = 3;
			Assert.That(core.ListMembers(out _), Is.Empty);
			Assert.That(core.EmptyListMessage(), Is.EqualTo(MembersSessionText.ListEmptyOnlyArchived), "only archived Alex has three stars");
			core.Drafts.ShowArchived = true;
			Assert.That(core.ListMembers(out _).Single().Id, Is.EqualTo(alex));
			core.Drafts.ShowArchived = false;

			// filter on, a search only an archived member matches
			core.Drafts.SearchText = "alex";
			core.Drafts.MinImportance = 1;
			Assert.That(core.ListMembers(out _), Is.Empty);
			Assert.That(core.EmptyListMessage(), Is.EqualTo(MembersSessionText.ListEmptyOnlyArchived));

			// a search nothing matches, with or without the filter
			core.Drafts.SearchText = "nobody";
			Assert.That(core.EmptyListMessage(), Is.EqualTo(MembersSessionText.ListEmptyNoMatch));
			core.Drafts.MinImportance = 0;
			Assert.That(core.EmptyListMessage(), Is.EqualTo(MembersSessionText.ListEmptyNoMatch));

			// no one with that many stars at all (Alex deleted)
			core.Drafts.SearchText = "";
			core.Drafts.MinImportance = 3;
			Assert.That(core.Delete(alex, core.LockGeneration), Is.True);
			Assert.That(core.EmptyListMessage(), Is.EqualTo(MembersSessionText.ListEmptyTooFewStars));
			Assert.That(sam, Is.Not.EqualTo(robin));
		}

		// --- export and import ---

		[Test]
		public void TheJsonExportKeepsTheNewFieldsAndTheCsvShowsThem()
		{
			var data = new MembersData
			{
				Members = { new Member { Id = Id(1), Name = "Robin Example", Importance = 3, Role = "Founder, Board" } },
			};

			var back = MembersExport.ParseJson(MembersExport.ToJson(data));
			Assert.That(back.Members[0].Importance, Is.EqualTo(3));
			Assert.That(back.Members[0].Role, Is.EqualTo("Founder, Board"));

			var lines = MembersExport.MembersCsv(data).Split("\r\n");
			Assert.That(lines[0], Does.EndWith(",Notes,Importance,Role"));
			Assert.That(lines[1], Does.EndWith(",3,\"Founder, Board\""));
		}

		[Test]
		public void ImportAcceptsFilesWithoutTheNewFieldsAndChecksThemWhenPresent()
		{
			var into = new MembersData();
			var result = MembersExport.ImportJson(VersionOnePayload, into, ImportMode.Merge, Today);
			Assert.That(result.Status, Is.EqualTo(ImportStatus.Imported));
			Assert.That(result.MembersAdded, Is.EqualTo(2));
			Assert.That(into.Members.All(m => m.Importance == 0 && m.Role == ""), Is.True);

			var bad = "{\"SchemaVersion\":2,\"Members\":[{\"Id\":\"" + Id(5) + "\",\"Name\":\"Sam Testcase\",\"Importance\":9},"
				+ "{\"Id\":\"" + Id(6) + "\",\"Name\":\"Alex Sample\",\"Importance\":2,\"Role\":\" Board \"}]}";
			var checkedResult = MembersExport.ImportJson(bad, into, ImportMode.Merge, Today);
			Assert.That(checkedResult.MembersAdded, Is.EqualTo(1));
			Assert.That(checkedResult.SkippedInvalid[MembersRules.ImportanceInvalid], Is.EqualTo(1));
			var alex = into.Members.Single(m => m.Id == Id(6));
			Assert.That(alex.Importance, Is.EqualTo(2));
			Assert.That(alex.Role, Is.EqualTo("Board"));
		}
	}
}
