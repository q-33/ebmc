using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// How serious it is that some parts on the bed are outside the printable area.
	/// </summary>
	public enum PartsOutsideBedLevel
	{
		/// <summary>No part is outside (or there are no parts at all; "Empty Bed" covers that).</summary>
		None,

		/// <summary>Some parts are outside and at least one part is inside: the print goes ahead without them.</summary>
		Warning,

		/// <summary>Every part is outside: nothing would be printed.</summary>
		Error,
	}

	/// <summary>
	/// The decision and the user-facing wording for parts that are outside the printable area, kept free of any
	/// GUI, scene or translation code so that it can be unit tested. Deciding which parts are outside is done by the
	/// caller with the same test the slicer uses (PrinterExtensionMethods); this only turns the result into text.
	/// Every method takes a <c>localize</c> delegate that is applied to each English template before it is
	/// formatted; the app passes <c>s => s.Localize()</c>, tests pass <c>s => s</c>.
	/// </summary>
	public static class PartsOutsideBedMessages
	{
		/// <summary>
		/// The most part names listed before the rest are summarised as "(and N more)".
		/// </summary>
		public const int MaxNamesListed = 3;

		public static PartsOutsideBedLevel Classify(int partsOutside, bool anyPartInside)
		{
			if (partsOutside <= 0)
			{
				return PartsOutsideBedLevel.None;
			}

			return anyPartInside ? PartsOutsideBedLevel.Warning : PartsOutsideBedLevel.Error;
		}

		/// <summary>
		/// The heading for the validation entry, or null for <see cref="PartsOutsideBedLevel.None"/>.
		/// </summary>
		public static string Title(PartsOutsideBedLevel level, Func<string, string> localize)
		{
			localize = localize ?? (s => s);

			switch (level)
			{
				case PartsOutsideBedLevel.Warning:
					return localize("Parts Outside the Bed");

				case PartsOutsideBedLevel.Error:
					return localize("Nothing Is Inside the Printable Area");

				default:
					return null;
			}
		}

		/// <summary>
		/// The details for the validation entry, naming the parts, or null for <see cref="PartsOutsideBedLevel.None"/>.
		/// <paramref name="anyPartTooLarge"/> is true when at least one outside part is taller than the build height or
		/// wider/deeper than the bed (advisory only: it chooses the advice in the error text).
		/// </summary>
		public static string Details(PartsOutsideBedLevel level, IEnumerable<string> outsideNames, bool anyPartTooLarge, Func<string, string> localize)
		{
			localize = localize ?? (s => s);

			switch (level)
			{
				case PartsOutsideBedLevel.Warning:
					return string.Format(
						CultureInfo.CurrentCulture,
						localize("These parts are partly or fully outside the printable area; anything outside will not be printed: {0}."),
						NameList(outsideNames, localize));

				case PartsOutsideBedLevel.Error:
					return string.Format(
						CultureInfo.CurrentCulture,
						localize("Every part on the bed is outside the printable area, so nothing would be printed: {0}."),
						NameList(outsideNames, localize))
						+ " "
						+ (anyPartTooLarge
							? localize("Move them onto the bed, or make them smaller if they are larger than the printable area.")
							: localize("Move them onto the bed."));

				default:
					return null;
			}
		}

		/// <summary>
		/// Whether to offer "Arrange All Parts": only when at least one outside part would fit on the bed (its width
		/// and depth within the bed and its height within the build height), so arranging can help.
		/// </summary>
		public static bool OfferArrange(PartsOutsideBedLevel level, int outsidePartsThatFit)
		{
			return level != PartsOutsideBedLevel.None
				&& outsidePartsThatFit > 0;
		}

		/// <summary>
		/// "Cube, Bracket, Clip (and 2 more)": at most <see cref="MaxNamesListed"/> names, in the given order, with
		/// "Unnamed part" for a missing or blank name.
		/// </summary>
		public static string NameList(IEnumerable<string> names, Func<string, string> localize)
		{
			localize = localize ?? (s => s);

			var allNames = (names ?? Enumerable.Empty<string>())
				.Select(name => string.IsNullOrWhiteSpace(name) ? localize("Unnamed part") : name.Trim())
				.ToList();

			string text = string.Join(", ", allNames.Take(MaxNamesListed));

			int remaining = allNames.Count - MaxNamesListed;
			if (remaining > 0)
			{
				text += " " + string.Format(CultureInfo.CurrentCulture, localize("(and {0} more)"), remaining);
			}

			return text;
		}
	}
}
