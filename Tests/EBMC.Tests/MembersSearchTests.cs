using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members & Projects: search and project filters. Every name here is invented.
	/// </summary>
	[TestFixture]
	public class MembersSearchTests
	{
		private static MembersData Fixture()
		{
			// invented people
			return new MembersData
			{
				Members =
				{
					new Member
					{
						Id = "robin", Name = "Robin Example", Interests = { "nylon", "CAD" }, Machines = { "Pulse XE" },
						UpdatedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
						Log = { new LogEntry { Date = "2026-11-10", Text = "asked about carbon fibre" } },
					},
					new Member
					{
						Id = "sam", Name = "Sam Testcase", Notes = "friend of robin, wants to learn the laser",
						UpdatedUtc = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc),
					},
					new Member
					{
						Id = "jose", Name = "José Placeholder", Contact = "jose@example.invalid",
						UpdatedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
					},
					new Member
					{
						Id = "alex", Name = "Alex Sample", Archived = true, Interests = { "nylon" },
						UpdatedUtc = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc),
					},
				},
				Projects =
				{
					new Project
					{
						Id = "arm", Title = "Robot arm", Status = ProjectStatus.Stuck, StatusSince = "2026-10-20",
						MemberIds = { "robin" }, HelpWanted = true, Needs = "gearbox design",
						UpdatedUtc = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc),
					},
					new Project
					{
						Id = "lamp", Title = "Lamp shade", Status = ProjectStatus.Stuck, StatusSince = "2026-09-01",
						MemberIds = { "sam" },
						UpdatedUtc = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc),
					},
					new Project
					{
						Id = "sign", Title = "Club sign", Status = ProjectStatus.InProgress, StatusSince = "2026-11-01",
						MemberIds = { "sam", "jose" }, HelpWanted = true,
						UpdatedUtc = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc),
					},
					new Project
					{
						Id = "box", Title = "Tool box", Status = ProjectStatus.Done, StatusSince = "2026-08-01",
						UpdatedUtc = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc),
					},
				},
			};
		}

		private static IEnumerable<string> Ids(IEnumerable<Member> members) => members.Select(m => m.Id);

		private static IEnumerable<string> Ids(IEnumerable<Project> projects) => projects.Select(p => p.Id);

		[Test]
		public void EveryWordMustMatchSomewhere()
		{
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "rob nyl", false)), Is.EqualTo(new[] { "robin" }));
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "rob zzz", false)), Is.Empty);
		}

		[Test]
		public void AccentsAndCaseAreIgnoredBothWays()
		{
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "José", false)), Is.EqualTo(new[] { "jose" }));
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "JOSE", false)), Is.EqualTo(new[] { "jose" }));

			var plain = new MembersData { Members = { new Member { Id = "j", Name = "Jose Placeholder" } } };
			Assert.That(Ids(MembersSearch.SearchMembers(plain, "josé", false)), Is.EqualTo(new[] { "j" }));
		}

		[Test]
		public void ANamePrefixMatchRanksAboveANotesMatch()
		{
			// Sam is more recent, but only mentions "robin" in the notes
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "robin", false)), Is.EqualTo(new[] { "robin", "sam" }));
		}

		[Test]
		public void ANameContainsMatchRanksAboveOtherFields()
		{
			var data = new MembersData
			{
				Members =
				{
					new Member { Id = "notes", Name = "Sam Testcase", Notes = "ample time", UpdatedUtc = new DateTime(2026, 11, 15) },
					new Member { Id = "name", Name = "Alex Sample", UpdatedUtc = new DateTime(2026, 1, 1) },
				},
			};

			Assert.That(Ids(MembersSearch.SearchMembers(data, "ample", false)), Is.EqualTo(new[] { "name", "notes" }));
		}

		[Test]
		public void SearchCoversContactMachinesLogAndProjectTitles()
		{
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "example.invalid", false)), Is.EqualTo(new[] { "jose" }));
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "pulse", false)), Is.EqualTo(new[] { "robin" }));
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "carbon", false)), Is.EqualTo(new[] { "robin" }));
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "club sign", false)), Is.EquivalentTo(new[] { "sam", "jose" }));
		}

		[Test]
		public void ArchivedMembersAreHiddenUnlessAskedFor()
		{
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "nylon", false)), Is.EqualTo(new[] { "robin" }));
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "nylon", true)), Is.EquivalentTo(new[] { "robin", "alex" }));
		}

		[Test]
		public void AnEmptyQueryListsEveryoneByMostRecentActivity()
		{
			// Robin's latest note (2026-11-10) counts, not the older UpdatedUtc
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), "  ", false)), Is.EqualTo(new[] { "sam", "robin", "jose" }));
			Assert.That(Ids(MembersSearch.SearchMembers(Fixture(), null, true)), Is.EqualTo(new[] { "alex", "sam", "robin", "jose" }));
		}

		[Test]
		public void StuckListsTheProjectStuckLongestFirst()
		{
			var stuck = MembersSearch.FilterProjects(Fixture(), new ProjectFilter { Statuses = new HashSet<ProjectStatus> { ProjectStatus.Stuck } });
			Assert.That(Ids(stuck), Is.EqualTo(new[] { "lamp", "arm" }));
		}

		[Test]
		public void OtherFiltersListTheMostRecentlyChangedFirst()
		{
			Assert.That(Ids(MembersSearch.FilterProjects(Fixture(), new ProjectFilter())), Is.EqualTo(new[] { "box", "sign", "lamp", "arm" }));
			Assert.That(MembersSearch.FilterProjects(Fixture(), null), Has.Count.EqualTo(4));
		}

		[Test]
		public void HelpWantedOnly()
		{
			Assert.That(Ids(MembersSearch.FilterProjects(Fixture(), new ProjectFilter { HelpWantedOnly = true })), Is.EquivalentTo(new[] { "arm", "sign" }));

			var stuckHelp = new ProjectFilter { HelpWantedOnly = true, Statuses = new HashSet<ProjectStatus> { ProjectStatus.Stuck } };
			Assert.That(Ids(MembersSearch.FilterProjects(Fixture(), stuckHelp)), Is.EqualTo(new[] { "arm" }));
		}

		[Test]
		public void ProjectQueryCoversTitleNeedsAndMemberNames()
		{
			Assert.That(Ids(MembersSearch.FilterProjects(Fixture(), new ProjectFilter { Query = "gearbox" })), Is.EqualTo(new[] { "arm" }));
			Assert.That(Ids(MembersSearch.FilterProjects(Fixture(), new ProjectFilter { Query = "jose" })), Is.EqualTo(new[] { "sign" }));
			Assert.That(Ids(MembersSearch.FilterProjects(Fixture(), new ProjectFilter { Query = "sam shade" })), Is.EqualTo(new[] { "lamp" }));
			Assert.That(Ids(MembersSearch.FilterProjects(Fixture(), new ProjectFilter { MemberId = "sam" })), Is.EquivalentTo(new[] { "lamp", "sign" }));
		}

		[Test]
		public void ActivityComparesDaysNotTimesOfDay()
		{
			// a note dated the 12th and a record changed late on the 12th count as the same day; the change time breaks the tie
			var data = new MembersData
			{
				Members =
				{
					new Member { Id = "note", Name = "Robin Example", UpdatedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), Log = { new LogEntry { Date = "2026-11-12", Text = "x" } } },
					new Member { Id = "edit", Name = "Sam Testcase", UpdatedUtc = new DateTime(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc) },
					new Member { Id = "newer", Name = "Alex Sample", UpdatedUtc = new DateTime(2026, 11, 13, 1, 0, 0, DateTimeKind.Utc) },
				},
			};

			Assert.That(Ids(MembersSearch.SearchMembers(data, "", false)), Is.EqualTo(new[] { "newer", "edit", "note" }));
			Assert.That(MembersSearch.LastActivity(data.Members[0], TimeZoneInfo.Utc), Is.EqualTo(new DateTime(2026, 11, 12)));
			Assert.That(MembersSearch.LastActivity(data.Members[1], TimeZoneInfo.Utc), Is.EqualTo(new DateTime(2026, 11, 12)));
		}

		[Test]
		public void ActivityIsTheLocalDayOfTheUtcChangeTime()
		{
			// 17:07 on 16 November at UTC-8 is already the 17th in UTC: the row shows the 16th, like the "Today" button
			var pacific = TimeZoneInfo.CreateCustomTimeZone("Test UTC-8", TimeSpan.FromHours(-8), "Test UTC-8", "Test UTC-8");
			var east = TimeZoneInfo.CreateCustomTimeZone("Test UTC+10", TimeSpan.FromHours(10), "Test UTC+10", "Test UTC+10");
			var changed = new DateTime(2026, 11, 17, 1, 7, 0, DateTimeKind.Utc);
			var member = new Member { Id = "robin", Name = "Robin Example", UpdatedUtc = changed };

			Assert.That(MembersSearch.LastActivity(member, pacific), Is.EqualTo(new DateTime(2026, 11, 16)));
			Assert.That(MembersSearch.LastActivity(member, TimeZoneInfo.Utc), Is.EqualTo(new DateTime(2026, 11, 17)));
			Assert.That(MembersRules.ToIso(MembersSearch.LastActivity(member, pacific)), Is.EqualTo("2026-11-16"));

			// a time read back without a kind is still UTC
			member.UpdatedUtc = DateTime.SpecifyKind(changed, DateTimeKind.Unspecified);
			Assert.That(MembersSearch.LastActivity(member, pacific), Is.EqualTo(new DateTime(2026, 11, 16)));

			// the evening before in UTC is already the next day further east
			member.UpdatedUtc = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);
			Assert.That(MembersSearch.LastActivity(member, east), Is.EqualTo(new DateTime(2026, 11, 17)));

			// a later note date still wins over the change day
			member.Log.Add(new LogEntry { Date = "2026-11-20", Text = "x" });
			Assert.That(MembersSearch.LastActivity(member, pacific), Is.EqualTo(new DateTime(2026, 11, 20)));

			// a member never stamped does not throw west of UTC
			Assert.That(() => MembersSearch.LastActivity(new Member { Id = "sam", Name = "Sam Testcase" }, pacific), Throws.Nothing);
		}

		[Test]
		public void SearchFoldsLigaturesAndSpecialLetters()
		{
			var data = new MembersData
			{
				Members =
				{
					new Member { Id = "a", Name = "Robin Straße", Interests = { "ﬁlament" } },
					new Member { Id = "b", Name = "Sam Işık" },
				},
			};

			Assert.That(Ids(MembersSearch.SearchMembers(data, "strasse filament", false)), Is.EqualTo(new[] { "a" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "isik", false)), Is.EqualTo(new[] { "b" }));
			Assert.That(Ids(MembersSearch.SearchMembers(data, "IŞIK", false)), Is.EqualTo(new[] { "b" }));
		}

		[Test]
		public void AWarmSearchOverAFullClubIsFast()
		{
			// invented, generated people
			var random = new Random(7);
			var words = new[] { "nylon", "laser", "cad", "resin", "lathe", "pulse", "arduino", "wood", "cnc", "carbon" };
			var data = new MembersData();
			for (int i = 0; i < 5000; i++)
			{
				var member = new Member
				{
					Id = "m" + i,
					Name = "Person" + i + " Example",
					Contact = "p" + i + "@example.invalid",
					Interests = { words[random.Next(words.Length)], words[random.Next(words.Length)] },
					Notes = "likes " + words[random.Next(words.Length)],
					UpdatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
				};

				for (int j = 0; j < 4; j++)
				{
					member.Log.Add(new LogEntry { Id = i + "-" + j, Date = "2026-0" + (1 + j) + "-1" + (i % 10), Text = "talked about " + words[random.Next(words.Length)] });
				}

				data.Members.Add(member);
			}

			for (int i = 0; i < 3000; i++)
			{
				data.Projects.Add(new Project { Id = "p" + i, Title = "Project " + words[i % words.Length] + " " + i, MemberIds = { "m" + random.Next(5000) }, StatusSince = "2026-01-01" });
			}

			var store = new MembersStore(data, () => new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc), () => Guid.NewGuid().ToString("N"));
			store.SearchMemberIds("warm up", false);

			var watch = System.Diagnostics.Stopwatch.StartNew();
			const int Runs = 10;
			int found = 0;
			for (int run = 0; run < Runs; run++)
			{
				found = store.SearchMemberIds("laser cad", false).Count;
				store.SearchMemberIds("person42", false);
				store.SearchMemberIds("", false);
				store.FilterProjectIds(new ProjectFilter { Query = "nylon" });
			}

			watch.Stop();
			Assert.That(found, Is.GreaterThan(0));

			// the target is well under 20 ms for one query; this is a generous limit for a busy test machine
			double perRound = watch.Elapsed.TotalMilliseconds / Runs;
			Assert.That(perRound, Is.LessThan(100), $"{perRound:F1} ms for four queries");
		}

		[Test]
		public void AddingANoteDoesNotRebuildTheWholeIndex()
		{
			var data = new MembersData();
			for (int i = 0; i < 5000; i++)
			{
				data.Members.Add(new Member { Id = i.ToString("x32"), Name = "Person" + i + " Example", Interests = { "nylon" }, Notes = "invented" });
			}

			for (int i = 0; i < 3000; i++)
			{
				data.Projects.Add(new Project { Id = (100000 + i).ToString("x32"), Title = "Project " + i, MemberIds = { (i % 5000).ToString("x32") } });
			}

			var clock = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
			var store = new MembersStore(data, () => clock, () => Guid.NewGuid().ToString("N"), () => new DateTime(2026, 11, 16));
			store.SearchMemberIds("warm", false);
			var target = 42.ToString("x32");

			const int Runs = 20;
			var watch = System.Diagnostics.Stopwatch.StartNew();
			for (int run = 0; run < Runs; run++)
			{
				Assert.That(store.AddLog(target, "today", "quick note " + run, new DateTime(2026, 11, 16)).Ok, Is.True);
				Assert.That(store.SearchMemberIds("quick note " + run, false), Is.EqualTo(new[] { target }));
			}

			watch.Stop();

			// a full rebuild costs tens of milliseconds here; a note plus a search must cost far less
			double perRound = watch.Elapsed.TotalMilliseconds / Runs;
			Assert.That(perRound, Is.LessThan(15), $"{perRound:F1} ms per note and search");
		}
	}
}
