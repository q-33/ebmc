using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.ClubUi
{
	/// <summary>
	/// EBMC Edition (ClubDialogsAndMenus): how a dialog footer button looks and where it sits. The style never changes
	/// what a button does.
	/// </summary>
	public enum PageActionStyle
	{
		/// <summary>Stock behaviour: the first highlighted action becomes Primary, the others Secondary.</summary>
		Default,

		/// <summary>Accent fill; the Enter default unless the dialog asks for a safe default.</summary>
		Primary,

		/// <summary>Outlined.</summary>
		Secondary,

		/// <summary>Danger fill. Never the Enter default.</summary>
		Destructive,

		/// <summary>The page's Cancel (or No / OK when it only closes) button: plain text.</summary>
		Cancel,

		/// <summary>The far-left slot: a dismissive or helper action (for example "Export print log").</summary>
		Aside,
	}

	/// <summary>
	/// EBMC Edition: the tile shown before a dialog title. Auto lets the message box pick (a question for a yes/no box,
	/// info for an OK box).
	/// </summary>
	public enum DialogSeverity
	{
		Auto,
		None,
		Info,
		Warning,
		Danger,
		Question,
	}

	/// <summary>
	/// EBMC Edition: the club dialog template's rules, with no UI dependencies so Tests/EBMC.Tests can check them.
	/// </summary>
	public static class ClubDialogRules
	{
		/// <summary>
		/// Turns the stock "first highlighted action is primary" rule into explicit styles: a Default style becomes
		/// Primary for the first action added with highlighting (the stock AddPageAction counts only highlighted calls,
		/// so that is the one it makes the accept button), Secondary otherwise. Explicit styles are kept, and an explicit
		/// Primary means no Default action is promoted.
		/// </summary>
		public static IReadOnlyList<PageActionStyle> ResolveStyles(IReadOnlyList<PageActionStyle> styles, IReadOnlyList<bool> highlighted)
		{
			var resolved = new List<PageActionStyle>();
			bool primaryTaken = styles.Contains(PageActionStyle.Primary);
			for (int i = 0; i < styles.Count; i++)
			{
				var style = styles[i];
				if (style == PageActionStyle.Default)
				{
					if (highlighted[i] && !primaryTaken)
					{
						style = PageActionStyle.Primary;
						primaryTaken = true;
					}
					else
					{
						style = PageActionStyle.Secondary;
					}
				}

				resolved.Add(style);
			}

			return resolved;
		}

		/// <summary>
		/// Which footer button Enter presses, as an index into <paramref name="styles"/> (the visible footer buttons, the
		/// page's Cancel button included with style Cancel), or -1 for none. Escape always cancels and is not decided
		/// here. When the dialog asks for a safe default, or any button is Destructive, Enter goes to the Cancel button,
		/// or nowhere when there is no visible Cancel (another button could be the affirmative one, for example a safe-
		/// default message box's styled-down "Resume"); a destructive button is never the default. Otherwise Enter goes
		/// to the first Primary button, or nowhere.
		/// </summary>
		public static int DefaultButton(IReadOnlyList<PageActionStyle> styles, bool safeDefault)
		{
			if (safeDefault
				|| styles.Contains(PageActionStyle.Destructive))
			{
				return IndexOf(styles, PageActionStyle.Cancel);
			}

			return IndexOf(styles, PageActionStyle.Primary);
		}

		/// <summary>
		/// The left-to-right order of the footer buttons, as indexes into <paramref name="styles"/>: Aside buttons first
		/// (the far-left slot), then Secondary and Cancel buttons in the order they were added, then the Primary and
		/// Destructive buttons, so the main action is at the right edge.
		/// </summary>
		public static IReadOnlyList<int> FooterOrder(IReadOnlyList<PageActionStyle> styles)
		{
			var indexes = Enumerable.Range(0, styles.Count);
			return indexes.Where(i => styles[i] == PageActionStyle.Aside)
				.Concat(indexes.Where(i => styles[i] == PageActionStyle.Secondary || styles[i] == PageActionStyle.Cancel || styles[i] == PageActionStyle.Default))
				.Concat(indexes.Where(i => styles[i] == PageActionStyle.Primary || styles[i] == PageActionStyle.Destructive))
				.ToList();
		}

		/// <summary>How many of the ordered footer buttons sit in the far-left slot (before the spacer).</summary>
		public static int AsideCount(IReadOnlyList<PageActionStyle> styles) => styles.Count(s => s == PageActionStyle.Aside);

		/// <summary>The tile a message box shows when its caller asked for Auto.</summary>
		public static DialogSeverity MessageBoxSeverity(DialogSeverity requested, bool isQuestion, bool destructive)
		{
			if (requested != DialogSeverity.Auto)
			{
				return requested;
			}

			if (destructive)
			{
				return DialogSeverity.Danger;
			}

			return isQuestion ? DialogSeverity.Question : DialogSeverity.Info;
		}

		/// <summary>
		/// The window title with the template on: the dialog's own title when it has one, otherwise the product name
		/// (instead of "{Product} - Please Confirm" or "{Product} - Alert").
		/// </summary>
		public static string WindowTitle(string caption, string productName)
		{
			return string.IsNullOrWhiteSpace(caption) ? productName : caption.Trim();
		}

		private static int IndexOf(IReadOnlyList<PageActionStyle> styles, PageActionStyle style)
		{
			for (int i = 0; i < styles.Count; i++)
			{
				if (styles[i] == style)
				{
					return i;
				}
			}

			return -1;
		}
	}

	/// <summary>
	/// EBMC Edition: the toast rules (how long one stays, how many stack).
	/// </summary>
	public static class ClubToastRules
	{
		/// <summary>At most this many toasts are shown at once; a new one pushes out the oldest.</summary>
		public const int MaxShown = 3;

		/// <summary>ShowNotification's stock duration; a caller that does not choose gets the toast's own.</summary>
		public const double StockDurationSeconds = 10;

		public const double DefaultSeconds = 6;

		/// <summary>
		/// Seconds a toast stays: about 6 when the caller kept ShowNotification's default, otherwise what it asked for,
		/// kept between 4 and 12 seconds.
		/// </summary>
		public static double DisplaySeconds(double requestedSeconds)
		{
			if (requestedSeconds <= 0
				|| requestedSeconds == StockDurationSeconds)
			{
				return DefaultSeconds;
			}

			return System.Math.Max(4, System.Math.Min(12, requestedSeconds));
		}

		/// <summary>How many of the oldest toasts to remove before one more is shown.</summary>
		public static int ToRemoveBeforeAdding(int shownCount)
		{
			return System.Math.Max(0, shownCount + 1 - MaxShown);
		}
	}
}
