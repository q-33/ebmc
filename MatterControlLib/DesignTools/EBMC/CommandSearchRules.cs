using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>
	/// EBMC Edition: what kind of thing a command search result runs.
	/// </summary>
	public enum CommandKind
	{
		/// <summary>A scene operation of the toolbar registry (SceneOperations).</summary>
		Operation,

		/// <summary>A shapes palette member (a shape, solid or Club Starter), added through the palette's insert path.</summary>
		Shape,

		/// <summary>A whitelisted workspace action (see <see cref="CommandSearchRules.WorkspaceWhitelist"/>).</summary>
		Workspace
	}

	/// <summary>
	/// EBMC Edition: one row the command search ("Find a tool") can list. Plain data; the app builds the rows from the
	/// toolbar registry, the palette catalog and the workspace whitelist and keeps its own map from <see cref="Id"/> to
	/// the action.
	/// </summary>
	public sealed class CommandEntry
	{
		public CommandEntry(CommandKind kind, string id, string key, string title, string where)
		{
			Kind = kind;
			Id = id;
			Key = key ?? title;
			Title = title ?? key;
			Where = where;
		}

		public CommandKind Kind { get; }

		/// <summary>Unique id, e.g. "op:Combine", "shape:Rounded Box", "ws:Save".</summary>
		public string Id { get; }

		/// <summary>English name the synonyms table refers to (an operation id, a palette member name, a workspace id); the
		/// table also matches the title.</summary>
		public string Key { get; }

		/// <summary>The title shown (localized by the app).</summary>
		public string Title { get; }

		/// <summary>Where it lives: "Modify", "Shapes", "File"…, or an operation group's title.</summary>
		public string Where { get; }

		/// <summary>The operation's help text, a palette hint, or null.</summary>
		public string HelpText { get; set; }

		/// <summary>The shortcut shown at the right of the row ("Ctrl + D"), or null.</summary>
		public string Shortcut { get; set; }

		/// <summary>False lists the row dimmed, with <see cref="DisabledReason"/>, and it cannot run.</summary>
		public bool Enabled { get; set; } = true;

		/// <summary>Why it can't run now (the operation's help text), shown when <see cref="Enabled"/> is false.</summary>
		public string DisabledReason { get; set; }

		/// <summary>The palette glyph or operation id the app uses to find the row's icon.</summary>
		public string IconKey { get; set; }

		public override string ToString() => Id;
	}

	/// <summary>
	/// EBMC Edition: the matching, ranking and safety rules of the command search, with no UI or app dependencies so
	/// Tests/EBMC.Tests can check them. Nothing the search lists may start a slice, a send/upload or a print.
	/// </summary>
	public static class CommandSearchRules
	{
		/// <summary>Most rows the popup lists.</summary>
		public const int MaxRows = 8;

		/// <summary>
		/// Words that mark a printing action. No row whose id, key or title contains one of these is ever listed
		/// (<see cref="IsForbidden"/>); this is a safety net on top of the sources, which never offer them.
		/// </summary>
		public static IReadOnlyList<string> ForbiddenWords { get; } = new[] { "print", "send", "upload", "slice" };

		/// <summary>
		/// The only workspace actions the search offers, by id. Print, Send, Upload and Slice, and everything on the
		/// printer action row, are deliberately absent.
		/// </summary>
		public static IReadOnlyList<string> WorkspaceWhitelist { get; } = new[]
		{
			"Save",
			"SaveAs",
			// no "Export": the export dialog offers G-code (a slice) whenever a printer is attached, and on a design tab
			// ExportLibraryItems attaches the only active printer by itself
			"Undo",
			"Redo",
			"SelectAll",
			"NewDesign",
			"OpenFile",
			"SwitchMode",
			"Shortcuts",
			"MaintainerLock",
		};

		/// <summary>
		/// Words people bring from Fusion 360, Tinkercad and friends, and the tools (by <see cref="CommandEntry.Key"/>)
		/// they should find, best first. A phrase (two words) matches only the whole query.
		/// </summary>
		public static IReadOnlyDictionary<string, string[]> Synonyms { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
		{
			["fillet"] = new[] { "Rounded Box", "Solid Box", "Solid Cylinder" },
			["round"] = new[] { "Rounded Box", "Solid Box", "Solid Cylinder" },
			["chamfer"] = new[] { "Solid Box", "Solid Cylinder" },
			["bevel"] = new[] { "Solid Box", "Solid Cylinder" },
			["union"] = new[] { "Combine" },
			["join"] = new[] { "Combine" },
			["merge"] = new[] { "Combine" },
			["cut"] = new[] { "Subtract", "Plane Cut" },
			["difference"] = new[] { "Subtract" },
			["boolean"] = new[] { "Subtract", "Combine", "Intersect" },
			["intersection"] = new[] { "Intersect" },
			["common"] = new[] { "Intersect" },
			["pattern"] = new[] { "Linear Array", "Radial Array", "Advanced Array" },
			["repeat"] = new[] { "Linear Array", "Radial Array", "Advanced Array" },
			["circular"] = new[] { "Radial Array" },
			["extrude"] = new[] { "Linear Extrude" },
			["revolve"] = new[] { "Revolve" },
			["lathe"] = new[] { "Revolve" },
			["shell"] = new[] { "Hollow Out" },
			["hollow"] = new[] { "Hollow Out" },
			["split"] = new[] { "Plane Cut" },
			["slice plane"] = new[] { "Plane Cut" },
			["move"] = new[] { "Translate", "Align" },
			["position"] = new[] { "Translate", "Align" },
			["copy"] = new[] { "Duplicate", "MirrorCopy" },
			["clone"] = new[] { "Duplicate" },
			["flip"] = new[] { "Mirror", "MirrorCopy" },
			["symmetry"] = new[] { "MirrorCopy", "Mirror" },
			["symmetric"] = new[] { "MirrorCopy", "Mirror" },
			["measure"] = new[] { "Measure Tool" },
			["ruler"] = new[] { "Measure Tool" },
			["dimension"] = new[] { "Measure Tool" },
			["hole"] = new[] { "Plate with Hole", "Subtract" },
			["text"] = new[] { "Text", "Name Tag", "Sign" },
			["label"] = new[] { "Text", "Name Tag", "Plant Label" },
			["gear"] = new[] { "Gear" },
			["cog"] = new[] { "Gear" },
			["step"] = new[] { "OpenFile" },
			["stp"] = new[] { "OpenFile" },
			["cad file"] = new[] { "OpenFile" },
			["import"] = new[] { "OpenFile" },
			["stl"] = new[] { "OpenFile" },
			["load"] = new[] { "OpenFile" },
			["simplify"] = new[] { "Reduce" },
			["decimate"] = new[] { "Reduce" },
			["fix"] = new[] { "Repair" },
			["heal"] = new[] { "Repair" },
			["resize"] = new[] { "Scale" },
			["size"] = new[] { "Scale" },
			["turn"] = new[] { "Rotate" },
			["delete"] = new[] { "Remove" },
			["erase"] = new[] { "Remove" },
			["box"] = new[] { "Cube", "Rounded Box", "Solid Box" },
			["block"] = new[] { "Cube", "Rounded Box", "Solid Box" },
			["tube"] = new[] { "Tube" },
			["pipe"] = new[] { "Tube" },
			["hexagon"] = new[] { "Polygon Prism" },
			["keys"] = new[] { "Shortcuts" },
			["hotkeys"] = new[] { "Shortcuts" },
			["keyboard"] = new[] { "Shortcuts" },
			["new"] = new[] { "NewDesign" },
			["tab"] = new[] { "NewDesign" },
			["mode"] = new[] { "SwitchMode" },
			["simple"] = new[] { "SwitchMode" },
			["advanced"] = new[] { "SwitchMode" },
			["pin"] = new[] { "MaintainerLock" },
			["maintainer"] = new[] { "MaintainerLock" },
		};

		// score tiers (higher is better)
		private const int ExactScore = 1000;
		private const int PrefixScore = 900;
		private const int SynonymScore = 850;
		private const int WordPrefixScore = 800;
		private const int SubstringScore = 600;
		private const int OtherFieldWordPrefixScore = 400;
		private const int OtherFieldSubstringScore = 300;
		private const int FuzzyScore = 100;

		/// <summary>
		/// Whether an operation group's tools are offered in the search. On a printer tab only a group the toolbar shows
		/// there (Tools options turned on, kept on printer tabs) is searchable, so a hidden group such as Printing (Convert
		/// to Support, Wipe Tower, Fuzzy Region) cannot be reached from the search; design tabs offer every group, as the
		/// Modify menu does.
		/// </summary>
		public static bool SearchGroup(bool printerTab, bool groupVisibleOnToolbar, bool groupShownOnPrinterTabs)
		{
			return !printerTab || (groupVisibleOnToolbar && groupShownOnPrinterTabs);
		}

		/// <summary>
		/// True when <paramref name="text"/> names a printing action (contains print, send, upload or slice, any case).
		/// </summary>
		public static bool IsForbidden(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}

			return ForbiddenWords.Any(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		/// <summary>True when the entry may be listed: none of its id, key or title is a printing action.</summary>
		public static bool IsAllowed(CommandEntry entry)
		{
			return entry != null
				&& !IsForbidden(entry.Id)
				&& !IsForbidden(entry.Key)
				&& !IsForbidden(entry.Title);
		}

		/// <summary>
		/// How well <paramref name="query"/> matches <paramref name="entry"/>; 0 for no match. Case-insensitive. On the
		/// title: exact > prefix > synonym > word prefix > substring; then a word prefix or substring of where it lives or
		/// its help text; then the query's letters in order in the title (fuzzy subsequence). A query of several words
		/// matches when every word matches; it scores as its weakest word.
		/// </summary>
		public static int Score(string query, CommandEntry entry)
		{
			var q = Normalize(query);
			if (q.Length == 0 || entry == null)
			{
				return 0;
			}

			// the whole query first (also catches two-word synonyms such as "slice plane")
			int whole = ScoreWord(q, entry);
			var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (words.Length < 2)
			{
				return whole;
			}

			int weakest = int.MaxValue;
			foreach (var word in words)
			{
				var score = ScoreWord(word, entry);
				if (score == 0)
				{
					weakest = 0;
					break;
				}

				weakest = Math.Min(weakest, score);
			}

			return Math.Max(whole, weakest);
		}

		/// <summary>
		/// The rows to list for <paramref name="query"/>: only allowed entries; enabled before disabled, then by score,
		/// then recently used first, then in the given order. An empty query lists recently used commands first (most
		/// recent first), then the rest in the given order. At most <paramref name="max"/> rows.
		/// </summary>
		public static IReadOnlyList<CommandEntry> Rank(string query, IEnumerable<CommandEntry> entries, IReadOnlyList<string> recentIds = null, int max = MaxRows)
		{
			var allowed = (entries ?? Enumerable.Empty<CommandEntry>()).Where(IsAllowed).ToList();
			recentIds = recentIds ?? new string[0];

			int RecentRank(CommandEntry entry)
			{
				// most recent first: recentIds is most recent first
				for (int i = 0; i < recentIds.Count; i++)
				{
					if (recentIds[i] == entry.Id)
					{
						return i;
					}
				}

				return int.MaxValue;
			}

			if (Normalize(query).Length == 0)
			{
				return allowed
					.Select((entry, index) => (entry, index))
					.OrderBy(x => RecentRank(x.entry))
					.ThenBy(x => x.entry.Enabled ? 0 : 1)
					.ThenBy(x => x.index)
					.Select(x => x.entry)
					.Take(max)
					.ToList();
			}

			return allowed
				.Select((entry, index) => (entry, index, score: Score(query, entry)))
				.Where(x => x.score > 0)
				.OrderBy(x => x.entry.Enabled ? 0 : 1)
				.ThenByDescending(x => x.score)
				.ThenBy(x => RecentRank(x.entry))
				.ThenBy(x => x.index)
				.Select(x => x.entry)
				.Take(max)
				.ToList();
		}

		/// <summary>
		/// The session's recently used list after running <paramref name="id"/>: it moves to the front; at most
		/// <paramref name="max"/> are kept.
		/// </summary>
		public static List<string> AddRecent(IEnumerable<string> recentIds, string id, int max = MaxRows)
		{
			var list = new List<string> { id };
			list.AddRange((recentIds ?? Enumerable.Empty<string>()).Where(r => r != id));
			return list.Take(max).ToList();
		}

		/// <summary>Help text as plain words: the operations' "**...**" emphasis removed.</summary>
		public static string PlainHelp(string helpText)
		{
			if (string.IsNullOrEmpty(helpText))
			{
				return helpText;
			}

			return helpText.Replace("*", "").Trim();
		}

		private static int ScoreWord(string word, CommandEntry entry)
		{
			var title = Normalize(entry.Title);
			var key = Normalize(entry.Key);

			int best = 0;

			foreach (var name in new[] { title, key }.Distinct())
			{
				if (name.Length == 0)
				{
					continue;
				}

				if (name == word)
				{
					best = Math.Max(best, ExactScore);
				}
				else if (name.StartsWith(word, StringComparison.Ordinal))
				{
					best = Math.Max(best, PrefixScore);
				}
				else if (WordPrefix(name, word))
				{
					best = Math.Max(best, WordPrefixScore);
				}
				else if (name.IndexOf(word, StringComparison.Ordinal) >= 0)
				{
					best = Math.Max(best, SubstringScore);
				}
			}

			if (Synonyms.TryGetValue(word, out var targets))
			{
				for (int i = 0; i < targets.Length; i++)
				{
					if (string.Equals(targets[i], entry.Key, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(targets[i], entry.Title, StringComparison.OrdinalIgnoreCase))
					{
						// the first target ranks highest
						best = Math.Max(best, SynonymScore - i);
					}
				}
			}

			if (best > 0)
			{
				return best;
			}

			foreach (var other in new[] { Normalize(entry.Where), Normalize(PlainHelp(entry.HelpText)) })
			{
				if (other.Length == 0)
				{
					continue;
				}

				if (other.StartsWith(word, StringComparison.Ordinal) || WordPrefix(other, word))
				{
					best = Math.Max(best, OtherFieldWordPrefixScore);
				}
				else if (word.Length >= 3 && other.IndexOf(word, StringComparison.Ordinal) >= 0)
				{
					best = Math.Max(best, OtherFieldSubstringScore);
				}
			}

			if (best > 0)
			{
				return best;
			}

			return Fuzzy(word, title);
		}

		/// <summary>The query's letters in order in the name: 1..99 above <see cref="FuzzyScore"/>, tighter is better.</summary>
		private static int Fuzzy(string word, string name)
		{
			var letters = word.Replace(" ", "");
			if (letters.Length < 2 || name.Length == 0)
			{
				return 0;
			}

			int position = -1;
			int first = -1;
			foreach (var letter in letters)
			{
				position = name.IndexOf(letter, position + 1);
				if (position < 0)
				{
					return 0;
				}

				if (first < 0)
				{
					first = position;
				}
			}

			int span = position - first + 1;
			int gaps = span - letters.Length;
			return FuzzyScore + Math.Max(1, 99 - gaps * 5 - first);
		}

		private static bool WordPrefix(string name, string word)
		{
			int index = 0;
			while (index < name.Length)
			{
				int found = name.IndexOf(word, index, StringComparison.Ordinal);
				if (found < 0)
				{
					return false;
				}

				if (found == 0 || !char.IsLetterOrDigit(name[found - 1]))
				{
					return true;
				}

				index = found + 1;
			}

			return false;
		}

		private static string Normalize(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}

			var lower = text.Trim().ToLowerInvariant();

			// collapse runs of white space
			return string.Join(" ", lower.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
		}
	}
}
