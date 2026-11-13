using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>
	/// EBMC Edition: wording for design-side notices (failed booleans, files that could not be opened, model size
	/// prompts). Free of GUI and translation code so it can be unit tested: every method takes a <c>localize</c>
	/// delegate applied to the English text; the app passes <c>s => s.Localize()</c>, tests pass <c>s => s</c>.
	/// </summary>
	public static class DesignMessages
	{
		/// <summary>How many file names a load-failure message lists before "and N more".</summary>
		public const int MaxNamesListed = 3;

		/// <summary>Shown when Combine, Subtract, Intersection or Subtract &amp; Replace fails.</summary>
		public static string OperationFailed(string operation, Func<string, string> localize)
		{
			return string.Format(
				L(localize, "{0} could not be completed. One of the parts may have gaps or overlapping surfaces; try Repair on it."),
				operation);
		}

		/// <summary>
		/// Shown when one or more files could not be added to the bed. Lists up to <see cref="MaxNamesListed"/> names,
		/// then "and N more". Returns null when there is nothing to report.
		/// </summary>
		public static string CouldNotOpen(IEnumerable<string> fileNames, Func<string, string> localize)
		{
			var names = (fileNames ?? Enumerable.Empty<string>())
				.Where(n => !string.IsNullOrWhiteSpace(n))
				.ToList();

			if (names.Count == 0)
			{
				return null;
			}

			var listed = string.Join(", ", names.Take(MaxNamesListed));
			int more = names.Count - MaxNamesListed;
			if (more > 0)
			{
				listed += " " + string.Format(L(localize, "and {0} more"), more);
			}

			return names.Count == 1
				? string.Format(L(localize, "Couldn't open {0}: the file is damaged or is not a 3D model."), listed)
				: string.Format(L(localize, "Couldn't open {0}: the files are damaged or are not 3D models."), listed);
		}

		/// <summary>Prompt for a model whose largest size is under <see cref="ModelFit.TinyLimitMm"/>.</summary>
		public static string ModelTiny(double largestSizeMm, Func<string, string> localize)
		{
			return string.Format(L(localize, "This model is only {0} mm across. Was it made in inches?"), Mm(largestSizeMm));
		}

		/// <summary>
		/// Prompt for a model that does not fit the printer's bed and build height. Sizes of 0 or less (not set in the
		/// profile) are left out; a round bed is given as its diameter.
		/// </summary>
		public static string ModelTooBigForBed(double bedWidthMm, double bedDepthMm, double buildHeightMm, Func<string, string> localize, bool roundBed = false)
		{
			var parts = new List<string>();
			if (roundBed)
			{
				if (bedWidthMm > 0)
				{
					parts.Add("Ø" + Mm(bedWidthMm));
				}
			}
			else
			{
				if (bedWidthMm > 0)
				{
					parts.Add(Mm(bedWidthMm));
				}

				if (bedDepthMm > 0)
				{
					parts.Add(Mm(bedDepthMm));
				}
			}

			if (buildHeightMm > 0)
			{
				parts.Add(Mm(buildHeightMm));
			}

			if (parts.Count == 0)
			{
				return L(localize, "This model is bigger than the printer's bed.");
			}

			return string.Format(L(localize, "This model is bigger than the printer's bed ({0} mm)."), string.Join(" × ", parts));
		}

		/// <summary>Prompt for a model over <see cref="ModelFit.DesignLimitMm"/> on a design tab.</summary>
		public static string ModelHuge(double largestSizeMm, Func<string, string> localize)
		{
			return string.Format(L(localize, "This model is {0} mm across, which is very large. Was it made in a different unit?"), Mm(largestSizeMm));
		}

		/// <summary>Button text for a scale choice: "Make 25.4× bigger", "Make 10× smaller" or "Shrink to fit".</summary>
		public static string ScaleChoice(ModelFitChoice choice, Func<string, string> localize)
		{
			if (choice.IsShrinkToFit)
			{
				return L(localize, "Shrink to fit");
			}

			if (choice.Scale >= 1)
			{
				return string.Format(L(localize, "Make {0}× bigger"), Factor(choice.Scale));
			}

			return string.Format(L(localize, "Make {0}× smaller"), Factor(1 / choice.Scale));
		}

		public static string LeaveIt(Func<string, string> localize) => L(localize, "Leave it");

		public static string ModelSizeTitle(Func<string, string> localize) => L(localize, "Model Size");

		/// <summary>Sizes in prompts: up to 2 decimals under 10 mm, otherwise whole millimetres.</summary>
		public static string Mm(double value)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				return "?";
			}

			return Math.Abs(value) < 10
				? value.ToString("0.##", CultureInfo.InvariantCulture)
				: Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
		}

		private static string Factor(double value)
		{
			return Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
		}

		private static string L(Func<string, string> localize, string text)
		{
			return (localize ?? (s => s))(text);
		}
	}
}
