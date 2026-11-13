using System;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the app-wide "Simple · Advanced" switch. Simple shows the essentials; Advanced shows every tool.
	/// A mode only changes what is shown, never a setting value, the bed or how a print is sliced or sent.
	/// </summary>
	public enum UiMode
	{
		Simple,
		Advanced
	}

	/// <summary>
	/// EBMC Edition: the rules of the Simple · Advanced switch with no app or UI dependencies, so Tests/EBMC.Tests can
	/// check them: parsing, the default, and how the mode maps to and from the Slice Settings detail level
	/// (UserSettingsKey.SliceSettingsViewDetail, which only chooses which settings rows are listed).
	/// The app uses them through MatterControlLib/ApplicationView/UiModes.cs.
	/// </summary>
	public static class UiModeRules
	{
		/// <summary>The Slice Settings detail levels, as stored in SliceSettingsViewDetail.</summary>
		public const string DetailSimple = "Simple";

		public const string DetailIntermediate = "Intermediate";

		public const string DetailAdvanced = "Advanced";

		/// <summary>The mode when nothing is stored and the OEM default is missing or unreadable.</summary>
		public const UiMode Fallback = UiMode.Advanced;

		/// <summary>Reads a stored or OEM value ("Simple" or "Advanced", any case, surrounding spaces ignored).</summary>
		public static bool TryParse(string text, out UiMode mode)
		{
			mode = Fallback;
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}

			switch (text.Trim().ToLowerInvariant())
			{
				case "simple":
					mode = UiMode.Simple;
					return true;

				case "advanced":
					mode = UiMode.Advanced;
					return true;
			}

			return false;
		}

		/// <summary>The text stored for a mode.</summary>
		public static string Format(UiMode mode) => mode == UiMode.Simple ? "Simple" : "Advanced";

		/// <summary>
		/// The mode to use: the user's stored value, else the OEM default, else <see cref="Fallback"/> (Advanced).
		/// </summary>
		public static UiMode Resolve(string storedValue, string oemDefault)
		{
			if (TryParse(storedValue, out UiMode stored))
			{
				return stored;
			}

			if (TryParse(oemDefault, out UiMode fromOem))
			{
				return fromOem;
			}

			return Fallback;
		}

		/// <summary>True for the two detail levels Advanced mode can use (Intermediate and Advanced).</summary>
		public static bool IsNonSimpleDetail(string detail) => detail == DetailIntermediate || detail == DetailAdvanced;

		/// <summary>
		/// The Slice Settings detail level for a mode: Simple → "Simple"; Advanced → the user's last Intermediate or
		/// Advanced choice (<paramref name="lastNonSimpleDetail"/>), or "Intermediate" when there is none.
		/// </summary>
		public static string DetailFor(UiMode mode, string lastNonSimpleDetail)
		{
			if (mode == UiMode.Simple)
			{
				return DetailSimple;
			}

			return IsNonSimpleDetail(lastNonSimpleDetail) ? lastNonSimpleDetail : DetailIntermediate;
		}

		/// <summary>
		/// The reverse rule, for a level picked in the Slice Settings "View" menu: "Simple" → Simple mode; "Intermediate"
		/// or "Advanced" → Advanced mode; null for anything else.
		/// </summary>
		public static UiMode? ModeForDetail(string detail)
		{
			if (detail == DetailSimple)
			{
				return UiMode.Simple;
			}

			if (IsNonSimpleDetail(detail))
			{
				return UiMode.Advanced;
			}

			return null;
		}

		/// <summary>
		/// What to store when the mode and the Slice Settings detail level are brought into line (at startup and on every
		/// switch): the level the user had in Advanced (Intermediate or Advanced) is kept as the level Advanced returns
		/// to, and the detail becomes the mode's level unless it already agrees. Returns the detail and the remembered
		/// Advanced level; each equals its stored value when nothing needs writing.
		/// </summary>
		public static (string detail, string lastAdvanced) Sync(UiMode mode, string storedDetail, string storedLastAdvancedDetail)
		{
			var lastAdvanced = IsNonSimpleDetail(storedDetail) ? storedDetail : storedLastAdvancedDetail;
			var detail = DetailAgrees(mode, storedDetail) ? storedDetail : DetailFor(mode, lastAdvanced);
			return (detail, lastAdvanced);
		}

		/// <summary>
		/// Whether the stored detail level already agrees with <paramref name="mode"/>. An unset level counts as "Simple"
		/// (the Slice Settings panel shows the Simple list when nothing is stored).
		/// </summary>
		public static bool DetailAgrees(UiMode mode, string storedDetail)
		{
			var shown = IsNonSimpleDetail(storedDetail) ? storedDetail : DetailSimple;
			return ModeForDetail(shown) == mode;
		}
	}
}
