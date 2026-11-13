using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: when the Simple · Advanced switch must wait, and the hint that says what it waits for. No app
	/// dependencies, so Tests/EBMC.Tests can check it. The switch rebuilds the main window, so it waits for work that a
	/// rebuild could disturb: slicing, the previous switch's rebuild, and every running task that is not marked
	/// RunningTaskOptions.AllowsWindowRebuild (saves, exports, file loads, part rebuilds, sends). Tasks marked with it (a
	/// print in progress, the heater hold after it, heating waits, timed notices) do not hold the switch up.
	/// </summary>
	public static class UiModeBusyRules
	{
		/// <summary>
		/// Null when the mode can change now; otherwise the hint. <paramref name="tasks"/> are the running tasks' titles and
		/// whether each allows a window rebuild.
		/// </summary>
		public static string Reason(bool slicing, bool rebuilding, IEnumerable<(string title, bool allowsRebuild)> tasks, Func<string, string> localize)
		{
			localize = localize ?? (s => s);

			if (rebuilding)
			{
				return localize("Switch modes in a moment, when the window has been rebuilt.");
			}

			if (slicing)
			{
				return localize("Switch modes after slicing has finished.");
			}

			var blocking = (tasks ?? Enumerable.Empty<(string, bool)>()).Where(t => !t.allowsRebuild).ToList();
			if (blocking.Count == 0)
			{
				return null;
			}

			var name = TaskName(blocking[0].title);
			if (string.IsNullOrEmpty(name))
			{
				return localize("Switch modes after the running task has finished.");
			}

			return localize("Switch modes after {0} has finished.").Replace("{0}", name);
		}

		/// <summary>A task title as a phrase inside a sentence: "Saving \"Box\" ..." becomes "saving \"Box\"".</summary>
		public static string TaskName(string title)
		{
			if (string.IsNullOrWhiteSpace(title))
			{
				return null;
			}

			var name = title.Trim().TrimEnd('.', '…', ' ');
			if (name.Length == 0)
			{
				return null;
			}

			// lower-case the first letter of an ordinary word ("Saving" -> "saving"), not of a name in capitals ("STEP")
			if (name.Length > 1 && char.IsUpper(name[0]) && !char.IsUpper(name[1]))
			{
				name = char.ToLowerInvariant(name[0]) + name.Substring(1);
			}

			return name;
		}
	}
}
