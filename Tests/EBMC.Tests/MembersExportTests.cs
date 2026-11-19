using System;
using System.Linq;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members & Projects: JSON (round trip, import merge/replace, versions), CSV escaping and the damaged-file
	/// exception. Every name here is invented.
	/// </summary>
	[TestFixture]
	public class MembersExportTests
	{
		private static readonly DateTime Older = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Newer = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Today = new DateTime(2026, 11, 16);

		// ids in the app's format (32 lower-case hex digits)
		private static readonly string Robin = Id(1);
		private static readonly string Sam = Id(2);
		private static readonly string Arm = Id(3);
		private static readonly string Alex = Id(4);
		private static readonly string Long = Id(5);
		private static readonly string Empty = Id(6);
		private static readonly string Date = Id(7);
		private static readonly string Date2 = Id(8);
		private static readonly string Untitled = Id(9);
		private static readonly string R = Id(10);
		private static readonly string Gone = Id(11);

		private static string Id(int n) => n.ToString("x32");

		private static MembersData Sample()
		{
			// invented
			return new MembersData
			{
				Members =
				{
					new Member
					{
						Id = Robin, Name = "Robin Example", Contact = "robin@example.invalid", MetOn = "2026-11-01",
						Interests = { "nylon" }, Machines = { "Pulse XE" }, Notes = "line one\nline two",
						Log = { new LogEntry { Id = "l1", Date = "2026-11-02", Text = "met at open night", CreatedUtc = Older } },
						CreatedUtc = Older, UpdatedUtc = Older,
					},
					new Member { Id = Sam, Name = "Sam Testcase", Archived = true, CreatedUtc = Older, UpdatedUtc = Older },
				},
				Projects =
				{
					new Project
					{
						Id = Arm, Title = "Robot arm", Status = ProjectStatus.InProgress, StatusSince = "2026-11-03",
						MemberIds = { Robin, Sam }, HelpWanted = true,
						Link = new DesignLink { Path = "/club/designs/arm.mcx", FileName = "arm.mcx", SizeBytes = 10, Sha256 = "ab" },
						CreatedUtc = Older, UpdatedUtc = Older,
					},
				},
			};
		}

		[Test]
		public void JsonRoundTripKeepsEverything()
		{
			var json = MembersExport.ToJson(Sample());
			Assert.That(json, Does.Contain("\"InProgress\""), "status stored as text");
			Assert.That(json, Does.Contain("\"SchemaVersion\": " + MembersData.CurrentSchemaVersion));

			var back = MembersExport.ParseJson(json);
			Assert.That(MembersExport.ToJson(back), Is.EqualTo(json));
			Assert.That(back.Members[0].Notes, Is.EqualTo("line one\nline two"));
			Assert.That(back.Projects[0].Link.FileName, Is.EqualTo("arm.mcx"));
			Assert.That(back.Members[0].UpdatedUtc, Is.EqualTo(Older));
		}

		[Test]
		public void ADamagedFileGivesAFixedMessageThatNeverQuotesTheData()
		{
			var damaged = new[]
			{
				"{\"SchemaVersion\":1,\"Members\":[{\"Name\":\"Robin Example\",\"CreatedUtc\":\"Robin Example\"}]}",
				"{\"SchemaVersion\":1,\"Members\":[{\"Name\":\"Robin Example\"",
				"{\"SchemaVersion\":1,\"Members\":\"Robin Example\"}",
				"{\"SchemaVersion\":\"Robin Example\"}",
				"Robin Example",
				"",
			};

			foreach (var text in damaged)
			{
				var e = Assert.Throws<MembersFileException>(() => MembersExport.ParseJson(text), text);
				Assert.That(e.Problem, Is.EqualTo(MembersFileProblem.Unreadable));
				Assert.That(e.InnerException, Is.Null);
				Assert.That(e.ToString(), Does.Not.Contain("Robin"));
				Assert.That(e.ToString(), Does.Not.Contain("Example"));
				Assert.That(e.Message, Is.EqualTo(MembersFileException.FixedMessage(MembersFileProblem.Unreadable)));
			}
		}

		[Test]
		public void RecordsWithoutIdsOrWithRepeatedIdsAreRefused()
		{
			Assert.Throws<MembersFileException>(() => MembersExport.ParseJson("{\"SchemaVersion\":1,\"Members\":[{\"Name\":\"Robin Example\"}]}"));
			Assert.Throws<MembersFileException>(() => MembersExport.ParseJson("{\"SchemaVersion\":1,\"Members\":[{\"Id\":\"a\"},{\"Id\":\"a\"}]}"));
		}

		[Test]
		public void VersionOneOpensAndMissingListsBecomeEmpty()
		{
			// a version 1 file opens and is brought up to the current version (it is saved as such)
			var data = MembersExport.ParseJson("{\"SchemaVersion\":1}");
			Assert.That(data.SchemaVersion, Is.EqualTo(MembersData.CurrentSchemaVersion));
			Assert.That(data.Members, Is.Empty);
			Assert.That(data.Projects, Is.Empty);

			var withNulls = MembersExport.ParseJson("{\"SchemaVersion\":1,\"Members\":[{\"Id\":\"a\",\"Name\":\"Sam Testcase\",\"Interests\":null,\"Log\":null}],\"Projects\":null}");
			Assert.That(withNulls.Members[0].Interests, Is.Empty);
			Assert.That(withNulls.Members[0].Log, Is.Empty);
			Assert.That(withNulls.Projects, Is.Empty);
		}

		[Test]
		public void ANewerOrMissingSchemaVersionIsRefused()
		{
			var newer = Assert.Throws<MembersFileException>(() => MembersExport.ParseJson("{\"SchemaVersion\":" + (MembersData.CurrentSchemaVersion + 1) + ",\"Members\":[]}"));
			Assert.That(newer.Problem, Is.EqualTo(MembersFileProblem.NewerVersion));

			Assert.That(Assert.Throws<MembersFileException>(() => MembersExport.ParseJson("{\"Members\":[]}")).Problem, Is.EqualTo(MembersFileProblem.Unreadable));
			Assert.That(Assert.Throws<MembersFileException>(() => MembersExport.ParseJson("{\"SchemaVersion\":0}")).Problem, Is.EqualTo(MembersFileProblem.Unreadable));
		}

		[Test]
		public void ImportMergeKeepsTheNewerRecordAndAddsNewOnes()
		{
			var local = Sample();
			local.Members[1].Name = "Sam Testcase (local, newer)";
			local.Members[1].UpdatedUtc = Newer;

			var incoming = Sample();
			incoming.Members[0].Contact = "new contact";
			incoming.Members[0].UpdatedUtc = Newer;              // newer than local: replaces it
			incoming.Members[1].Name = "Sam Testcase (file, older)"; // older than local: ignored
			incoming.Members.Add(new Member { Id = Alex, Name = "Alex Sample", UpdatedUtc = Older });

			var result = MembersExport.ImportJson(MembersExport.ToJson(incoming), local, ImportMode.Merge, Today);

			Assert.That(result.Status, Is.EqualTo(ImportStatus.Imported));
			Assert.That(result.MembersAdded, Is.EqualTo(1));
			Assert.That(result.MembersUpdated, Is.EqualTo(1));
			Assert.That(result.ProjectsAdded, Is.EqualTo(0));
			Assert.That(result.ProjectsUpdated, Is.EqualTo(0));
			Assert.That(local.Members.Single(m => m.Id == Robin).Contact, Is.EqualTo("new contact"));
			Assert.That(local.Members.Single(m => m.Id == Sam).Name, Is.EqualTo("Sam Testcase (local, newer)"));
			Assert.That(local.Members.Select(m => m.Id), Is.EquivalentTo(new[] { Robin, Sam, Alex }));
		}

		[Test]
		public void ImportMergeNeverBringsBackADeletedRecord()
		{
			var store = new MembersStore(Sample(), () => Newer, () => Guid.NewGuid().ToString("N"));
			var export = MembersExport.ToJson(store.Snapshot());
			Assert.That(store.DeleteMember(Sam), Is.True);
			Assert.That(store.DeleteProject(Arm), Is.True);

			var result = store.Import(export, ImportMode.Merge, Today);

			Assert.That(result.Status, Is.EqualTo(ImportStatus.Imported));
			Assert.That(result.SkippedDeleted, Is.EqualTo(2));
			Assert.That(result.MembersAdded, Is.EqualTo(0));
			Assert.That(store.FindMember(Sam), Is.Null);
			Assert.That(store.FindProject(Arm), Is.Null);

			// the deleted list holds ids and times only, never a name
			var json = MembersExport.ToJson(store.Snapshot());
			Assert.That(json, Does.Contain("\"DeletedMembers\""));
			Assert.That(json, Does.Not.Contain("Sam Testcase"));
		}

		[Test]
		public void ReplaceKeepsTheDeletedListsSoALaterMergeCannotBringRecordsBack()
		{
			var store = new MembersStore(Sample(), () => Newer, () => Guid.NewGuid().ToString("N"));
			var oldExport = MembersExport.ToJson(store.Snapshot());   // holds Sam
			store.DeleteMember(Sam);

			// a replace with a file that has neither Sam nor a deleted list
			var other = new MembersData { Members = { new Member { Id = Alex, Name = "Alex Sample", UpdatedUtc = Older } } };
			store.Import(MembersExport.ToJson(other), ImportMode.Replace, Today);
			Assert.That(store.Snapshot().DeletedMembers.Select(t => t.Id), Is.EqualTo(new[] { Sam }));

			var merged = store.Import(oldExport, ImportMode.Merge, Today);
			Assert.That(merged.SkippedDeleted, Is.EqualTo(1));
			Assert.That(store.FindMember(Sam), Is.Null);
			Assert.That(store.FindMember(Robin), Is.Not.Null);
		}

		[Test]
		public void ReplaceDoesNotMarkRecordsTheFileHoldsAsDeleted()
		{
			var store = new MembersStore(Sample(), () => Newer, () => Guid.NewGuid().ToString("N"));
			var export = MembersExport.ToJson(store.Snapshot());
			store.DeleteMember(Sam);

			store.Import(export, ImportMode.Replace, Today);
			Assert.That(store.FindMember(Sam), Is.Not.Null);
			Assert.That(store.Snapshot().DeletedMembers, Is.Empty);
		}

		[Test]
		public void RecordsWhoseIdIsNotTheAppsFormatAreSkippedAndNeverRemembered()
		{
			// an id that is a person's name must not end up in the "id and time only" deleted list
			var incoming = Sample();
			incoming.Members.Add(new Member { Id = "Jo Placeholder", Name = "Jo Placeholder", UpdatedUtc = Older });
			incoming.Members.Add(new Member { Id = Id(12).ToUpperInvariant(), Name = "Upper Case", UpdatedUtc = Older });
			incoming.Projects.Add(new Project { Id = "lamp", Title = "Lamp shade", MemberIds = { Robin }, UpdatedUtc = Older });
			incoming.DeletedMembers.Add(new Tombstone { Id = "Robin Example", DeletedUtc = Older });
			incoming.DeletedMembers.Add(new Tombstone { Id = Id(13), DeletedUtc = Older });

			var local = new MembersData();
			var result = MembersExport.ImportJson(MembersExport.ToJson(incoming), local, ImportMode.Replace, Today);

			Assert.That(result.SkippedInvalid[MembersRules.IdInvalid], Is.EqualTo(3));
			Assert.That(local.Members.Select(m => m.Id), Is.EquivalentTo(new[] { Robin, Sam }));
			Assert.That(local.DeletedMembers.Select(t => t.Id), Is.EqualTo(new[] { Id(13) }));
			Assert.That(MembersExport.ToJson(local), Does.Not.Contain("Jo Placeholder"));

			// reading a file drops such entries from the deleted lists too (they are not records)
			var read = MembersExport.ParseJson(MembersExport.ToJson(incoming));
			Assert.That(read.DeletedMembers.Select(t => t.Id), Is.EqualTo(new[] { Id(13) }));
		}

		[Test]
		public void TheDeletedListsAreCapped()
		{
			var list = new System.Collections.Generic.List<Tombstone>();
			for (int i = 0; i < MembersRules.MaxTombstones + 5; i++)
			{
				MembersRules.AddTombstone(list, Id(100 + i), Older.AddMinutes(i));
			}

			Assert.That(list, Has.Count.EqualTo(MembersRules.MaxTombstones));
			Assert.That(list.Any(t => t.Id == Id(100)), Is.False, "the oldest is dropped");
			Assert.That(list.Any(t => t.Id == Id(100 + MembersRules.MaxTombstones + 4)), Is.True);

			MembersRules.AddTombstone(list, "Robin Example", Newer);
			Assert.That(list.Any(t => t.Id == "Robin Example"), Is.False);
		}

		[Test]
		public void AnImportAboveTheRecordLimitIsRefused()
		{
			var huge = new MembersData();
			for (int i = 0; i <= MembersExport.MaxImportRecords; i++)
			{
				huge.Members.Add(new Member { Id = Id(1000 + i), Name = "Person " + i });
			}

			var local = Sample();
			var before = MembersExport.ToJson(local);
			var result = MembersExport.ImportJson(MembersExport.ToJson(huge), local, ImportMode.Merge, Today);
			Assert.That(result.Status, Is.EqualTo(ImportStatus.TooLarge));
			Assert.That(MembersExport.ToJson(local), Is.EqualTo(before));
		}

		[Test]
		public void ALargeMergeTakesLinearTime()
		{
			var local = new MembersData();
			var incoming = new MembersData();
			for (int i = 0; i < 20000; i++)
			{
				local.Members.Add(new Member { Id = Id(1000 + i), Name = "Person " + i, UpdatedUtc = Older });
				incoming.Members.Add(new Member { Id = Id(1000 + i), Name = "Person " + i, UpdatedUtc = Newer });
			}

			var json = MembersExport.ToJson(incoming);
			var watch = System.Diagnostics.Stopwatch.StartNew();
			var result = MembersExport.ImportJson(json, local, ImportMode.Merge, Today);
			watch.Stop();

			Assert.That(result.MembersUpdated, Is.EqualTo(20000));
			Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(5), "a quadratic merge of 20,000 takes far longer");
		}

		[Test]
		public void InvalidImportedRecordsAreSkippedAndCountedPerReason()
		{
			var incoming = Sample();
			incoming.Members.Add(new Member { Id = Long, Name = new string('n', 5000), UpdatedUtc = Older });
			incoming.Members.Add(new Member { Id = Empty, Name = "   ", UpdatedUtc = Older });
			incoming.Members.Add(new Member { Id = Date, Name = "Alex Sample", MetOn = "not a date", UpdatedUtc = Older });
			incoming.Members.Add(new Member { Id = Date2, Name = "Jo Placeholder", MetOn = "2099-01-01", UpdatedUtc = Older });
			incoming.Projects.Add(new Project { Id = Untitled, Title = "", MemberIds = { Robin }, UpdatedUtc = Older });

			var local = new MembersData();
			var result = MembersExport.ImportJson(MembersExport.ToJson(incoming), local, ImportMode.Merge, Today);

			Assert.That(result.Status, Is.EqualTo(ImportStatus.Imported));
			Assert.That(local.Members.Select(m => m.Id), Is.EquivalentTo(new[] { Robin, Sam }));
			Assert.That(local.Projects.Select(p => p.Id), Is.EqualTo(new[] { Arm }));
			Assert.That(result.SkippedInvalid[MembersRules.NameTooLong], Is.EqualTo(1));
			Assert.That(result.SkippedInvalid[MembersRules.NameRequired], Is.EqualTo(1));
			Assert.That(result.SkippedInvalid[MembersRules.DateInvalid], Is.EqualTo(2));
			Assert.That(result.SkippedInvalid[MembersRules.TitleRequired], Is.EqualTo(1));
			Assert.That(result.SkippedInvalidCount, Is.EqualTo(5));
			Assert.That(string.Join(" ", result.SkippedInvalid.Keys), Does.Not.Contain("not a date"));

			// the same rules apply to Replace
			var replaced = new MembersData();
			Assert.That(MembersExport.ImportJson(MembersExport.ToJson(incoming), replaced, ImportMode.Replace, Today).SkippedInvalidCount, Is.EqualTo(5));
			Assert.That(replaced.Members, Has.Count.EqualTo(2));
		}

		[Test]
		public void ImportCleansRecordsTheWayTheStoreDoes()
		{
			var incoming = new MembersData { Members = { new Member { Id = R, Name = "  Robin   Example ", Interests = { "nylon", "Nylon" } } } };
			var local = new MembersData();
			MembersExport.ImportJson(MembersExport.ToJson(incoming), local, ImportMode.Merge, Today);
			Assert.That(local.Members[0].Name, Is.EqualTo("Robin Example"));
			Assert.That(local.Members[0].Interests, Is.EqualTo(new[] { "nylon" }));
		}

		[Test]
		public void ReplaceAndReadingTheFileDropLinksToMissingMembers()
		{
			var json = MembersExport.ToJson(Sample()).Replace("\"" + Sam + "\"", "\"" + Gone + "\"");
			json = json.Replace("\"Id\": \"" + Gone + "\"", "\"Id\": \"" + Sam + "\""); // the member keeps its id; only the project link dangles

			Assert.That(MembersExport.ParseJson(json).Projects.Single().MemberIds, Is.EqualTo(new[] { Robin }), "reading");

			var local = new MembersData();
			MembersExport.ImportJson(json, local, ImportMode.Replace, Today);
			Assert.That(local.Projects.Single().MemberIds, Is.EqualTo(new[] { Robin }), "replace");
		}

		[Test]
		public void ReadingTheFileNeverDropsRecordsThatBreakARule()
		{
			// the vault reads its own file with ParseJson: an over-long name from an older version still loads
			var json = "{\"SchemaVersion\":1,\"Members\":[{\"Id\":\"a\",\"Name\":\"" + new string('n', 5000) + "\",\"MetOn\":\"not a date\"}]}";
			var data = MembersExport.ParseJson(json);
			Assert.That(data.Members.Single().Name, Has.Length.EqualTo(5000));
		}

		[Test]
		public void TextWithALoneSurrogateIsStillWritten()
		{
			var data = new MembersData { Members = { new Member { Id = "a", Name = "Robin \uD800 Example" } } };
			string json = null;
			Assert.DoesNotThrow(() => json = MembersExport.ToJson(data));
			Assert.That(MembersExport.ParseJson(json).Members[0].Name, Is.EqualTo("Robin \uFFFD Example"));
			Assert.That(data.Members[0].Name, Is.EqualTo("Robin \uD800 Example"), "the caller's data is not changed");
		}

		[Test]
		public void ImportMergeDropsMembershipOfPeopleWhoDoNotExist()
		{
			var local = new MembersData();
			var incoming = Sample();
			incoming.Members.RemoveAll(m => m.Id == Sam);
			var json = MembersExport.ToJson(incoming).Replace("\"" + Sam + "\"", "\"" + Gone + "\"");

			MembersExport.ImportJson(json, local, ImportMode.Merge, Today);
			Assert.That(local.Projects.Single().MemberIds, Is.EqualTo(new[] { Robin }));
		}

		[Test]
		public void ImportReplaceSwapsEverything()
		{
			var local = Sample();
			var incoming = new MembersData { Members = { new Member { Id = Alex, Name = "Alex Sample", UpdatedUtc = Older } } };

			var result = MembersExport.ImportJson(MembersExport.ToJson(incoming), local, ImportMode.Replace, Today);

			Assert.That(result.Status, Is.EqualTo(ImportStatus.Imported));
			Assert.That(local.Members.Select(m => m.Id), Is.EqualTo(new[] { Alex }));
			Assert.That(local.Projects, Is.Empty);
		}

		[Test]
		public void ImportRefusesANewerOrDamagedFileAndChangesNothing()
		{
			var local = Sample();
			var before = MembersExport.ToJson(local);

			var newer = MembersExport.ImportJson("{\"SchemaVersion\":99,\"Members\":[]}", local, ImportMode.Replace, Today);
			Assert.That(newer.Status, Is.EqualTo(ImportStatus.NewerVersion));

			var damaged = MembersExport.ImportJson("{\"SchemaVersion\":1,\"Members\":[{\"Name\":\"Robin", local, ImportMode.Replace, Today);
			Assert.That(damaged.Status, Is.EqualTo(ImportStatus.Unreadable));

			Assert.That(MembersExport.ToJson(local), Is.EqualTo(before));
		}

		[Test]
		public void CsvEscapesQuotesCommasAndNewlines()
		{
			Assert.That(MembersExport.CsvCell("plain"), Is.EqualTo("plain"));
			Assert.That(MembersExport.CsvCell("Example, Robin"), Is.EqualTo("\"Example, Robin\""));
			Assert.That(MembersExport.CsvCell("the \"lamp\" one"), Is.EqualTo("\"the \"\"lamp\"\" one\""));
			Assert.That(MembersExport.CsvCell("line one\nline two"), Is.EqualTo("\"line one\nline two\""));
			Assert.That(MembersExport.CsvCell("a\r\nb"), Is.EqualTo("\"a\r\nb\""));
			Assert.That(MembersExport.CsvCell(null), Is.EqualTo(""));
		}

		[TestCase("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
		[TestCase("+1 555", "'+1 555")]
		[TestCase("-2", "'-2")]
		[TestCase("@sam", "'@sam")]
		[TestCase("\tcmd", "'\tcmd")]
		[TestCase("a=b", "a=b")]
		[TestCase(" =1+1", "' =1+1")]
		[TestCase("\u00A0=1+1", "'\u00A0=1+1")]
		[TestCase("\n=1+1", "\"'\n=1+1\"")]
		[TestCase("\r=1+1", "\"'\r=1+1\"")]
		[TestCase("\uFF1D1+1", "'\uFF1D1+1")]
		[TestCase("\uFF0B1", "'\uFF0B1")]
		[TestCase("\uFF0D1", "'\uFF0D1")]
		[TestCase("\uFF20sam", "'\uFF20sam")]
		[TestCase("   ", "   ")]
		public void CsvGuardsAgainstSpreadsheetFormulas(string value, string expected)
		{
			Assert.That(MembersExport.CsvCell(value), Is.EqualTo(expected));
		}

		[Test]
		public void MembersCsvHasAHeaderAndOneRowPerMember()
		{
			var csv = MembersExport.MembersCsv(Sample());
			var lines = csv.Split("\r\n");

			Assert.That(lines[0], Is.EqualTo("Id,Name,Contact,Met on,Interests,Machines,Archived,Last note,Projects,Notes,Importance,Role"));
			Assert.That(lines[1], Does.StartWith(Robin + ",Robin Example,robin@example.invalid,2026-11-01,nylon,Pulse XE,no,2026-11-02,Robot arm,\"line one\nline two\""));
			Assert.That(lines[2], Does.StartWith(Sam + ",Sam Testcase,,,,,yes,,Robot arm,"));
			Assert.That(lines[3], Is.EqualTo(""));
			Assert.That(lines, Has.Length.EqualTo(4));
		}

		[Test]
		public void ProjectsCsvNamesMembersAndOnlyTheDesignFileName()
		{
			var csv = MembersExport.ProjectsCsv(Sample());
			var lines = csv.Split("\r\n");

			Assert.That(lines[0], Is.EqualTo("Id,Title,Status,Status since,Members,Needs,Machines,Help wanted,Started,Design file,Notes"));
			Assert.That(lines[1], Is.EqualTo(Arm + ",Robot arm,In progress,2026-11-03,Robin Example; Sam Testcase,,,yes,,arm.mcx,"));
			Assert.That(csv, Does.Not.Contain("/club/designs"));
		}
	}
}
