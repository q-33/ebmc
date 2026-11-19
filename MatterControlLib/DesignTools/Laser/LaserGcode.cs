using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using MatterHackers.MatterControl.DesignTools.ClubShapes;

namespace MatterHackers.MatterControl.DesignTools.Laser
{
	// EBMC Edition: "G-code for laser (.nc)": the outline as a G-code file for a laser cutter. Pure (System and ClubShapes
	// only) so Tests/EBMC.Tests checks it. It only writes text; nothing here talks to a machine. It has nothing to do with
	// the 3D printer's G-code: different file type (.nc), different code, never sliced, never sent.

	/// <summary>
	/// A laser machine as data (StaticData/Laser/machines.json, written by Tools/EBMC/generate_ebmc_files.py). Verified false
	/// means nobody has checked the values on the machine.
	/// </summary>
	public sealed class LaserMachineProfile
	{
		public string Id { get; set; } = "";

		public string Name { get; set; } = "";

		public bool Verified { get; set; }

		public string Note { get; set; } = "";

		/// <summary>Work area in mm; null when not known (the size is then not checked, and the file says so).</summary>
		public double? WorkAreaXMm { get; set; }

		public double? WorkAreaYMm { get; set; }

		public bool WorkAreaVerified { get; set; }

		public double PowerSMin { get; set; } = 0;

		public double PowerSMax { get; set; } = 1000;

		/// <summary>The highest power percent this machine profile allows (at most 100).</summary>
		public double MaxPowerPercent { get; set; } = 100;

		public double MaxCutSpeedMmS { get; set; }

		/// <summary>The slowest cutting speed allowed: slower than this at power is a burn risk.</summary>
		public double MinCutSpeedMmS { get; set; } = 1;

		public double TravelSpeedMmS { get; set; }

		/// <summary>M4 (dynamic power) or M3 (constant power).</summary>
		public string LaserOnCommand { get; set; } = "M4";

		public string LaserOffCommand { get; set; } = "M5";

		public List<string> Header { get; set; } = new List<string>();

		public List<string> Footer { get; set; } = new List<string>();

		/// <summary>True when the machine's Y origin is at the back (Y grows towards the front).</summary>
		public bool OriginAtBack { get; set; }
	}

	/// <summary>One laser job: one operation over the whole outline.</summary>
	public sealed class LaserJobSettings
	{
		/// <summary>"Cut" or "Score": only a label (and which remembered values the page uses).</summary>
		public string Operation = "Cut";

		/// <summary>Percent of the machine's power range; NaN until someone types it.</summary>
		public double PowerPercent = double.NaN;

		/// <summary>mm/s; NaN until someone types it.</summary>
		public double SpeedMmS = double.NaN;

		public int Passes = 1;

		/// <summary>Free text; written into a comment as printable ASCII only.</summary>
		public string MaterialNote = "";
	}

	/// <summary>Values for one operation of an approved material preset.</summary>
	public sealed class LaserOperationValues
	{
		public double PowerPercent { get; set; }

		public double SpeedMmS { get; set; }

		public int Passes { get; set; }
	}

	/// <summary>A material preset (StaticData/Laser/materials.json). Only approved ones are ever offered.</summary>
	public sealed class LaserMaterialPreset
	{
		public string Id { get; set; } = "";

		public string Name { get; set; } = "";

		public double ThicknessMm { get; set; }

		public bool Approved { get; set; }

		public string ApprovedBy { get; set; } = "";

		public string ApprovedOn { get; set; } = "";

		public string Source { get; set; } = "";

		/// <summary>By operation: "cut", "score".</summary>
		public Dictionary<string, LaserOperationValues> Operations { get; set; } = new Dictionary<string, LaserOperationValues>();
	}

	/// <summary>
	/// Reads the laser data files; never throws (bad data gives an empty list, a bad entry is skipped). A repeated id keeps
	/// the first entry (the order in the file is the order the generator wrote) and is reported as a load note.
	/// </summary>
	public static class LaserData
	{
		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			ReadCommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
		};

		private static List<T> Load<T>(string json, string listName, Func<T, string> idOf, string kind, List<string> notes)
		{
			var list = new List<T>();
			var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			try
			{
				using (var doc = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
				{
					if (doc.RootElement.ValueKind == JsonValueKind.Object
						&& doc.RootElement.TryGetProperty(listName, out var items)
						&& items.ValueKind == JsonValueKind.Array)
					{
						foreach (var item in items.EnumerateArray())
						{
							T value;
							try
							{
								value = JsonSerializer.Deserialize<T>(item.GetRawText(), Options);
							}
							catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
							{
								notes?.Add($"A {kind} entry could not be read and was left out.");
								continue;
							}

							if (value == null)
							{
								continue;
							}

							var id = idOf(value) ?? "";
							if (!ids.Add(id))
							{
								notes?.Add($"The {kind} id '{LaserGcodeWriter.Ascii(id)}' appears more than once; only the first one is used.");
								continue;
							}

							list.Add(value);
						}
					}
				}
			}
			catch (JsonException)
			{
				notes?.Add($"The {kind} file could not be read.");
			}

			return list;
		}

		public static List<LaserMachineProfile> LoadMachines(string json) => LoadMachines(json, null);

		public static List<LaserMachineProfile> LoadMachines(string json, List<string> notes) => Load<LaserMachineProfile>(json, "machines", m => m.Id, "machine profile", notes);

		public static List<LaserMaterialPreset> LoadMaterials(string json) => LoadMaterials(json, null);

		public static List<LaserMaterialPreset> LoadMaterials(string json, List<string> notes)
		{
			var presets = Load<LaserMaterialPreset>(json, "materials", p => p.Id, "material preset", notes);
			foreach (var preset in presets)
			{
				// operation names match whatever their case ("Cut", "cut")
				var operations = new Dictionary<string, LaserOperationValues>(StringComparer.OrdinalIgnoreCase);
				foreach (var pair in preset.Operations ?? new Dictionary<string, LaserOperationValues>())
				{
					if (pair.Key != null && !operations.ContainsKey(pair.Key))
					{
						operations[pair.Key] = pair.Value;
					}
				}

				preset.Operations = operations;
			}

			return presets;
		}

		/// <summary>
		/// The presets that may be offered for an operation: approved, with a name in approvedBy, and with values for that
		/// operation (its name in any case). Nothing else is ever offered.
		/// </summary>
		public static List<LaserMaterialPreset> OfferedPresets(IEnumerable<LaserMaterialPreset> presets, string operation)
		{
			var key = operation ?? "";
			return (presets ?? Enumerable.Empty<LaserMaterialPreset>())
				.Where(p => p != null
					&& p.Approved
					&& !string.IsNullOrWhiteSpace(p.ApprovedBy)
					&& p.Operations != null
					&& p.Operations.Any(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase) && o.Value != null))
				.ToList();
		}
	}

	/// <summary>
	/// EBMC Edition: reads the numbers people type for power, speed and passes. Strict on purpose, so a thousands separator
	/// can never be misread: digits, optionally one '.' or ',' followed by one or two digits ("12", "12.5", "12,5"). "1,000",
	/// "1.000", "0.125", signs, exponents, spaces inside, "Infinity" and "NaN" are refused.
	/// </summary>
	public static class LaserNumberText
	{
		private static readonly Regex Decimal = new Regex(@"^[0-9]{1,6}(?:[.,][0-9]{1,2})?$", RegexOptions.CultureInvariant);

		private static readonly Regex Whole = new Regex(@"^[0-9]{1,6}$", RegexOptions.CultureInvariant);

		public static bool TryParse(string text, out double value)
		{
			value = double.NaN;
			var t = (text ?? "").Trim();
			if (!Decimal.IsMatch(t))
			{
				return false;
			}

			value = double.Parse(t.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
			return true;
		}

		public static bool TryParseWhole(string text, out int value)
		{
			value = 0;
			var t = (text ?? "").Trim();
			return Whole.IsMatch(t) && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
		}

		/// <summary>
		/// The typed power, speed and passes as a job (operation and note as given), and a plain message for each field that
		/// is empty or not a number in the allowed form. Range checks are LaserGcodeRules.ValidateJob.
		/// </summary>
		public static List<LaserGcodeProblem> ReadJob(string power, string speed, string passes, LaserJobSettings job)
		{
			var problems = new List<LaserGcodeProblem>();
			const string Form = "a number like 12 or 12.5 (at most 2 decimals, no thousands separator)";
			if (string.IsNullOrWhiteSpace(power))
			{
				problems.Add(new LaserGcodeProblem { Id = LaserGcodeRules.PowerId, Message = "Enter the power in %." });
			}
			else if (TryParse(power, out var p))
			{
				job.PowerPercent = p;
			}
			else
			{
				problems.Add(new LaserGcodeProblem { Id = LaserGcodeRules.PowerId, Message = $"Power: type {Form}." });
			}

			if (string.IsNullOrWhiteSpace(speed))
			{
				problems.Add(new LaserGcodeProblem { Id = LaserGcodeRules.SpeedId, Message = "Enter the speed in mm/s." });
			}
			else if (TryParse(speed, out var s))
			{
				job.SpeedMmS = s;
			}
			else
			{
				problems.Add(new LaserGcodeProblem { Id = LaserGcodeRules.SpeedId, Message = $"Speed: type {Form}." });
			}

			if (string.IsNullOrWhiteSpace(passes))
			{
				problems.Add(new LaserGcodeProblem { Id = LaserGcodeRules.PassesId, Message = "Enter the number of passes." });
			}
			else if (TryParseWhole(passes, out var n))
			{
				job.Passes = n;
			}
			else
			{
				problems.Add(new LaserGcodeProblem { Id = LaserGcodeRules.PassesId, Message = "Passes: type a whole number." });
			}

			return problems;
		}
	}

	public enum LaserProblemLevel
	{
		Error,
		Warning,
	}

	public sealed class LaserGcodeProblem
	{
		public string Id;

		public string Message;

		public LaserProblemLevel Level = LaserProblemLevel.Error;
	}

	/// <summary>
	/// EBMC Edition: the checks before a laser G-code file is written, with fixed ids and plain wording. Out-of-range values
	/// are refused (never quietly changed), and the message says what the limit is and which profile it comes from.
	/// </summary>
	public static class LaserGcodeRules
	{
		public const string MachineId = "LaserGcodeMachine";

		public const string PowerId = "LaserGcodePower";

		public const string SpeedId = "LaserGcodeSpeed";

		public const string PassesId = "LaserGcodePasses";

		public const string TooBigId = "LaserGcodeTooBig";

		public const string EmptyId = "LaserGcodeEmpty";

		public const string SizeNotCheckedId = "LaserGcodeSizeNotChecked";

		public const string NotVerifiedId = "LaserGcodeNotVerified";

		public const string SkippedLoopsId = "LaserGcodeSkippedLoops";

		public const int MinPasses = 1;

		public const int MaxPasses = 20;

		/// <summary>Sane bounds for a profile's numbers (anything outside means the data is wrong).</summary>
		public const double MaxS = 100000;

		public const double MaxSpeedMmS = 2000;

		public const double MaxWorkAreaMm = 5000;

		/// <summary>Longest start or end line a profile may hold.</summary>
		public const int MaxStartEndLineLength = 70;

		private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

		/// <summary>"in the profile 'Name'", saying so when the profile is only a placeholder.</summary>
		public static string InProfile(LaserMachineProfile m) =>
			m.Verified ? $"in the profile '{m.Name}'" : $"in the profile '{m.Name}' (a placeholder, not checked on the machine)";

		/// <summary>The highest power percent allowed on this machine: its own clamp, never above 100.</summary>
		public static double PowerLimit(LaserMachineProfile machine) => Math.Min(100, machine?.MaxPowerPercent ?? 0);

		private static bool InRange(double v, double min, double max, bool minExclusive = false) =>
			!double.IsNaN(v) && !double.IsInfinity(v) && (minExclusive ? v > min : v >= min) && v <= max;

		// ---- start and end lines: an allow-list ----

		/// <summary>
		/// The only commands a profile's start or end lines may hold: units (mm), absolute, feed per minute, plane, work
		/// offset, laser off, air assist on/off, dwell, program end.
		/// </summary>
		private static readonly HashSet<string> AllowedCommands = new HashSet<string> { "G17", "G21", "G54", "G90", "G94", "M5", "M8", "M9", "G4", "M2", "M30" };

		/// <summary>
		/// Null when a start or end line from a machine profile is harmless; otherwise why not. The line is split into words
		/// (a letter and a number: leading zeros, lower case, N line numbers, "( )" and ";" comments are understood) and every
		/// word must be on a short allow-list (G17 G21 G54 G90 G94 M2 M30 M5 M8 M9, and G4 with a P dwell of at most 60), each letter
		/// once per line, and the line at most 70 characters. Anything else is
		/// refused, as are "$" settings and any control character (a line break would start a new line in the file).
		/// </summary>
		public static string StartEndLineProblem(string line)
		{
			if (line == null)
			{
				return null;
			}

			if (line.Length > MaxStartEndLineLength)
			{
				return $"it is longer than {MaxStartEndLineLength} characters";
			}

			if (line.Any(c => c < 0x20 || c > 0x7E))
			{
				return "it contains a line break or another control character";
			}

			if (line.Contains('$') || line.Contains('%'))
			{
				return "machine settings ($ or %) are not allowed";
			}

			// comments: "( ... )" and everything after ';'
			var code = new StringBuilder();
			bool inParen = false;
			foreach (var c in line)
			{
				if (inParen)
				{
					if (c == ')')
					{
						inParen = false;
					}

					continue;
				}

				if (c == ';')
				{
					break;
				}

				if (c == '(')
				{
					inParen = true;
					continue;
				}

				if (c == ')')
				{
					return "it has a ')' without a '('";
				}

				code.Append(c);
			}

			if (inParen)
			{
				return "it has a '(' without a ')'";
			}

			var words = new List<(char letter, double number)>();
			var text = code.ToString();
			int i = 0;
			while (i < text.Length)
			{
				if (text[i] == ' ' || text[i] == '\t')
				{
					i++;
					continue;
				}

				char letter = char.ToUpperInvariant(text[i]);
				if (letter < 'A' || letter > 'Z')
				{
					return $"'{text[i]}' is not allowed";
				}

				i++;
				int start = i;
				while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
				{
					i++;
				}

				var digits = text.Substring(start, i - start);
				if (digits.Length == 0
					|| digits.Count(c => c == '.') > 1
					|| !double.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
				{
					return $"'{letter}' must be followed by a number";
				}

				words.Add((letter, number));
			}

			// one word per letter on a line (no "G4 P1 P2", no two commands on one line)
			var repeated = words.GroupBy(w => w.letter).FirstOrDefault(g => g.Count() > 1);
			if (repeated != null)
			{
				return $"'{repeated.Key}' appears more than once";
			}

			bool dwell = false;
			foreach (var (letter, number) in words)
			{
				if (letter == 'N')
				{
					continue;
				}

				if (letter == 'G' || letter == 'M')
				{
					if (number != Math.Floor(number))
					{
						return $"{letter}{N(number)} is not allowed";
					}

					var command = letter.ToString() + ((int)number).ToString(CultureInfo.InvariantCulture);
					if (!AllowedCommands.Contains(command))
					{
						return $"{command} is not allowed";
					}

					dwell |= command == "G4";
					continue;
				}

				// a dwell's time is P only: S would set laser power
				if (letter == 'P' && dwell && number <= 60)
				{
					continue;
				}

				return $"{letter}{N(number)} is not allowed";
			}

			if (dwell && !words.Any(w => w.letter == 'P'))
			{
				return "G4 needs a P dwell time";
			}

			return null;
		}

		/// <summary>The machine profile's own problems (it comes from a data file).</summary>
		public static List<LaserGcodeProblem> ValidateMachine(LaserMachineProfile m)
		{
			var problems = new List<LaserGcodeProblem>();
			void Add(string message) => problems.Add(new LaserGcodeProblem { Id = MachineId, Message = message });

			if (m == null)
			{
				Add("Choose a laser machine.");
				return problems;
			}

			var name = LaserGcodeWriter.Ascii(m.Name);
			if (!InRange(m.PowerSMin, 0, MaxS)
				|| !InRange(m.PowerSMax, 0, MaxS, minExclusive: true)
				|| !(m.PowerSMax > m.PowerSMin)
				|| m.PowerSMax - m.PowerSMin < 1)
			{
				Add($"The machine profile '{name}' has no usable power range (S from 0 to at most {N(MaxS)}).");
			}

			if (!InRange(m.MaxPowerPercent, 0, 100, minExclusive: true))
			{
				Add($"The machine profile '{name}' has a power limit outside 0-100 %.");
			}

			if (!InRange(m.MinCutSpeedMmS, 0, MaxSpeedMmS, minExclusive: true)
				|| !(m.MinCutSpeedMmS < m.MaxCutSpeedMmS))
			{
				Add($"The machine profile '{name}' has an unusable slowest cutting speed (more than 0 and below its fastest).");
			}

			if (!InRange(m.MaxCutSpeedMmS, 0, MaxSpeedMmS, minExclusive: true)
				|| !InRange(m.TravelSpeedMmS, 0, MaxSpeedMmS, minExclusive: true))
			{
				Add($"The machine profile '{name}' has no usable speeds (more than 0 and at most {N(MaxSpeedMmS)} mm/s).");
			}

			if (m.WorkAreaXMm.HasValue != m.WorkAreaYMm.HasValue
				|| (m.WorkAreaXMm.HasValue && (!InRange(m.WorkAreaXMm.Value, 0, MaxWorkAreaMm, minExclusive: true) || !InRange(m.WorkAreaYMm.Value, 0, MaxWorkAreaMm, minExclusive: true))))
			{
				Add($"The machine profile '{name}' has an unusable work area (both sides, more than 0 and at most {N(MaxWorkAreaMm)} mm).");
			}

			if (m.LaserOnCommand != "M3" && m.LaserOnCommand != "M4")
			{
				Add($"The machine profile '{name}' must turn the laser on with M3 or M4.");
			}

			if (m.LaserOffCommand != "M5")
			{
				Add($"The machine profile '{name}' must turn the laser off with M5.");
			}

			foreach (var line in (m.Header ?? new List<string>()).Concat(m.Footer ?? new List<string>()))
			{
				var problem = StartEndLineProblem(line);
				if (problem != null)
				{
					Add($"The machine profile '{name}' has a start or end line that is not allowed: '{LaserGcodeWriter.Ascii(line)}' ({problem}).");
				}
			}

			return problems;
		}

		/// <summary>The typed job values against the machine (the page checks these before asking where to save).</summary>
		public static List<LaserGcodeProblem> ValidateJob(LaserJobSettings job, LaserMachineProfile machine)
		{
			var problems = ValidateMachine(machine);
			if (problems.Count > 0 || job == null)
			{
				return problems;
			}

			var limit = PowerLimit(machine);
			if (double.IsNaN(job.PowerPercent))
			{
				problems.Add(new LaserGcodeProblem { Id = PowerId, Message = "Enter the power in %." });
			}
			else if (!(job.PowerPercent > 0) || job.PowerPercent > limit)
			{
				problems.Add(new LaserGcodeProblem { Id = PowerId, Message = $"Power must be more than 0 % and no more than {N(limit)} % {InProfile(machine)}." });
			}
			else if (SValue(job.PowerPercent, machine) <= machine.PowerSMin)
			{
				problems.Add(new LaserGcodeProblem { Id = PowerId, Message = $"Power {N(job.PowerPercent)} % is too small to have any effect {InProfile(machine)}: it comes out as S{N(machine.PowerSMin)}." });
			}

			if (double.IsNaN(job.SpeedMmS))
			{
				problems.Add(new LaserGcodeProblem { Id = SpeedId, Message = "Enter the speed in mm/s." });
			}
			else if (job.SpeedMmS > 0 && job.SpeedMmS < machine.MinCutSpeedMmS && job.SpeedMmS <= machine.MaxCutSpeedMmS)
			{
				problems.Add(new LaserGcodeProblem { Id = SpeedId, Message = $"Speed must be at least {N(machine.MinCutSpeedMmS)} mm/s {InProfile(machine)}: a very slow speed with the laser on can start a fire." });
			}
			else if (!(job.SpeedMmS > 0) || job.SpeedMmS > machine.MaxCutSpeedMmS)
			{
				problems.Add(new LaserGcodeProblem { Id = SpeedId, Message = $"Speed must be more than 0 and no more than {N(machine.MaxCutSpeedMmS)} mm/s {InProfile(machine)}." });
			}
			else if (FeedText(job.SpeedMmS) == "0")
			{
				problems.Add(new LaserGcodeProblem { Id = SpeedId, Message = $"Speed {job.SpeedMmS.ToString("G3", CultureInfo.InvariantCulture)} mm/s is too small to have any effect: it comes out as F0." });
			}

			if (job.Passes < MinPasses || job.Passes > MaxPasses)
			{
				problems.Add(new LaserGcodeProblem { Id = PassesId, Message = $"Passes must be from {MinPasses} to {MaxPasses}." });
			}

			return problems;
		}

		/// <summary>Everything: machine, job values, and the outline (not empty, fits the work area when it is known).</summary>
		public static List<LaserGcodeProblem> Validate(LaserOutline outline, LaserJobSettings job, LaserMachineProfile machine)
		{
			var problems = ValidateJob(job, machine);
			if (outline == null || outline.IsEmpty)
			{
				problems.Add(new LaserGcodeProblem { Id = EmptyId, Message = "Nothing to cut: the outline is empty." });
				return problems;
			}

			if (machine == null || problems.Any(p => p.Id == MachineId))
			{
				return problems;
			}

			if (machine.WorkAreaXMm.HasValue && machine.WorkAreaYMm.HasValue)
			{
				var tooWide = outline.Width - machine.WorkAreaXMm.Value;
				var tooDeep = outline.Height - machine.WorkAreaYMm.Value;
				if (tooWide > 1e-6 || tooDeep > 1e-6)
				{
					var parts = new List<string>();
					if (tooWide > 1e-6)
					{
						parts.Add($"{N(tooWide)} mm too wide");
					}

					if (tooDeep > 1e-6)
					{
						parts.Add($"{N(tooDeep)} mm too deep");
					}

					problems.Add(new LaserGcodeProblem
					{
						Id = TooBigId,
						Message = $"The outline ({N(outline.Width)} × {N(outline.Height)} mm) does not fit the work area ({N(machine.WorkAreaXMm.Value)} × {N(machine.WorkAreaYMm.Value)} mm) {InProfile(machine)}: {string.Join(" and ", parts)}.",
					});
				}
			}
			else
			{
				problems.Add(new LaserGcodeProblem
				{
					Id = SizeNotCheckedId,
					Level = LaserProblemLevel.Warning,
					Message = $"The profile '{machine.Name}' has no work area, so the size was not checked.",
				});
			}

			if (!machine.Verified)
			{
				problems.Add(new LaserGcodeProblem
				{
					Id = NotVerifiedId,
					Level = LaserProblemLevel.Warning,
					Message = $"The machine profile '{machine.Name}' has not been checked on the machine. Check the file in the laser's own software before cutting.",
				});
			}

			return problems;
		}

		/// <summary>The S value for a power percent on this machine (rounded to a whole number).</summary>
		public static int SValue(double powerPercent, LaserMachineProfile machine)
		{
			var s = Math.Round(machine.PowerSMin + (machine.PowerSMax - machine.PowerSMin) * powerPercent / 100, MidpointRounding.AwayFromZero);
			return (int)Math.Max(0, Math.Min(MaxS, s));
		}

		/// <summary>F (mm/min) for a speed in mm/s.</summary>
		public static double Feed(double speedMmS) => speedMmS * 60;

		/// <summary>F as written in the file.</summary>
		public static string FeedText(double speedMmS) => LaserNumbers.Format(Feed(speedMmS), 3);
	}

	public sealed class LaserGcodeResult
	{
		/// <summary>The file text, or null when an error stopped it (see Problems).</summary>
		public string Text;

		public List<LaserGcodeProblem> Problems = new List<LaserGcodeProblem>();

		public double CutLengthMm;

		public double TravelLengthMm;

		public double EstimatedSeconds;

		public int LoopsPerPass;

		/// <summary>Loops left out because they have fewer than 3 distinct points or no length (the laser is never fired for them).</summary>
		public int SkippedLoops;

		public bool Ok => Text != null;
	}

	/// <summary>
	/// EBMC Edition: writes the laser G-code. Invariant numbers, "\n" line endings. The laser is turned off before every
	/// travel move and is never on during a G0. Per pass: shapes nearest-first from where the head is, innermost islands
	/// before the shapes around them, and within a shape its holes before its outer edge (so the part stays in place).
	/// The outline's lower-left corner is put at the machine origin (Y flipped when the machine's origin is at the back).
	/// </summary>
	public static class LaserGcodeWriter
	{
		private const int Decimals = 3;

		private static string N(double v) => LaserNumbers.Format(v, Decimals);

		/// <summary>Printable ASCII only (for comments), at most 240 characters.</summary>
		public static string Ascii(string text)
		{
			var clean = new string((text ?? "").Where(c => c >= 0x20 && c <= 0x7E).ToArray()).Trim();
			return clean.Length > 240 ? clean.Substring(0, 240) : clean;
		}

		public static LaserGcodeResult Write(LaserOutline outline, LaserJobSettings job, LaserMachineProfile machine, CancellationToken ct = default)
		{
			var result = new LaserGcodeResult();
			result.Problems.AddRange(LaserGcodeRules.Validate(outline, job, machine));
			if (result.Problems.Any(p => p.Level == LaserProblemLevel.Error))
			{
				return result;
			}

			var order = Order(outline, machine, ct, out int skipped);
			result.LoopsPerPass = order.Count;
			result.SkippedLoops = skipped;
			if (skipped > 0)
			{
				result.Problems.Add(new LaserGcodeProblem
				{
					Id = LaserGcodeRules.SkippedLoopsId,
					Level = LaserProblemLevel.Warning,
					Message = skipped == 1 ? "1 loop too small to cut was left out." : $"{skipped} loops too small to cut were left out.",
				});
			}

			if (order.Count == 0)
			{
				result.Problems.Add(new LaserGcodeProblem { Id = LaserGcodeRules.EmptyId, Message = "Nothing to cut: every loop is too small." });
				return result;
			}

			int s = LaserGcodeRules.SValue(job.PowerPercent, machine);
			string feed = LaserGcodeRules.FeedText(job.SpeedMmS);
			string on = machine.LaserOnCommand;
			string off = machine.LaserOffCommand;

			// lengths for the estimate (one pass)
			double cut = 0, travel = 0;
			var head = new Pt(0, 0);
			foreach (var loop in order)
			{
				travel += (loop[0] - head).Length;
				for (int i = 0; i < loop.Count; i++)
				{
					cut += (loop[(i + 1) % loop.Count] - loop[i]).Length;
				}

				head = loop[0];
			}

			// pass 1 starts from the origin, later passes from where the last one ended; then back to the origin
			int passes = job.Passes;
			double firstFromOrigin = order[0][0].Length;
			double between = travel - firstFromOrigin;
			double backToFirst = (order[0][0] - head).Length;
			travel = firstFromOrigin + passes * between + (passes - 1) * backToFirst + head.Length;
			cut *= passes;
			result.CutLengthMm = cut;
			result.TravelLengthMm = travel;
			result.EstimatedSeconds = cut / job.SpeedMmS + travel / machine.TravelSpeedMmS;

			var sb = new StringBuilder();
			void L(string line) => sb.Append(line).Append('\n');
			void C(string comment) => L("; " + Ascii(comment));

			C("G-code for a laser cutter, made by EBMC (Export for laser).");
			C("This is NOT 3D-printer G-code. Do not send it to a 3D printer.");
			C($"Units: mm. Size: {N(outline.Width)} x {N(outline.Height)} mm, lower-left corner at the machine origin.");
			C($"Operation: {job.Operation}");
			C($"Power: {N(job.PowerPercent)} % (S{s} in S{N(machine.PowerSMin)}-{N(machine.PowerSMax)}), speed: {N(job.SpeedMmS)} mm/s (F{feed}), passes: {passes}");
			if (!string.IsNullOrWhiteSpace(Ascii(job.MaterialNote)))
			{
				C("Material: " + job.MaterialNote);
			}

			C($"Machine profile: {machine.Name} ({(machine.Verified ? "verified" : "NOT verified on the machine")})");
			if (!string.IsNullOrWhiteSpace(machine.Note))
			{
				C("Profile note: " + machine.Note);
			}

			if (machine.WorkAreaXMm.HasValue && machine.WorkAreaYMm.HasValue)
			{
				C($"Work area: {N(machine.WorkAreaXMm.Value)} x {N(machine.WorkAreaYMm.Value)} mm{(machine.WorkAreaVerified ? "" : " (NOT verified)")}");
			}
			else
			{
				C("Work area: not known; the size was not checked.");
			}

			C($"Y origin: {(machine.OriginAtBack ? "back (Y flipped)" : "front")}");
			C($"Estimated cutting length: {N(cut)} mm; estimated time: {Duration(result.EstimatedSeconds)}");
			if (skipped > 0)
			{
				C($"Left out: {skipped} loop(s) too small to cut.");
			}

			C("Outline only: no engraving or fill, no kerf allowance, no lead-ins, no tabs.");
			C("Before starting: lid closed, air assist on, focus set, stay with the machine, extinguisher to hand.");
			L("G21");
			L("G90");
			L(off);

			// start and end lines passed the allow-list (LaserGcodeRules.StartEndLineProblem), so they are written as they are
			foreach (var line in machine.Header ?? new List<string>())
			{
				L(line);
			}

			for (int pass = 1; pass <= passes; pass++)
			{
				ct.ThrowIfCancellationRequested();
				C($"Pass {pass} of {passes}");
				foreach (var loop in order)
				{
					var start = loop[0];
					L($"G0 X{N(start.X)} Y{N(start.Y)} S0");
					L($"{on} S{s}");
					for (int i = 1; i < loop.Count; i++)
					{
						L($"G1 X{N(loop[i].X)} Y{N(loop[i].Y)}" + (i == 1 ? $" F{feed}" : ""));
					}

					// close the loop, then off before any travel
					L($"G1 X{N(start.X)} Y{N(start.Y)}");
					L(off);
				}
			}

			L(off);
			L("G0 X0 Y0 S0");
			foreach (var line in machine.Footer ?? new List<string>())
			{
				L(line);
			}

			result.Text = sb.ToString();
			return result;
		}

		/// <summary>"about 3 min" / "about 40 s".</summary>
		public static string Duration(double seconds)
		{
			if (double.IsNaN(seconds) || double.IsInfinity(seconds))
			{
				return "unknown";
			}

			if (seconds < 60)
			{
				return $"about {Math.Max(1, (int)Math.Ceiling(seconds))} s";
			}

			return $"about {(int)Math.Ceiling(seconds / 60)} min";
		}

		/// <summary>Where a point of the outline goes on the machine, rounded as it is written.</summary>
		internal static Pt ToMachine(Pt p, LaserOutline o, LaserMachineProfile m)
		{
			var x = Math.Round(p.X - o.MinX, Decimals);
			var y = Math.Round(m.OriginAtBack ? o.MaxY - p.Y : p.Y - o.MinY, Decimals);
			return new Pt(x, y);
		}

		/// <summary>
		/// The loop as written: consecutive repeats (after rounding) removed, and the closing repeat of the first point.
		/// Null when it has fewer than 3 distinct points or no length: the laser is never switched on for it.
		/// </summary>
		internal static List<Pt> Cuttable(IEnumerable<Pt> points)
		{
			var loop = new List<Pt>();
			foreach (var p in points)
			{
				if (loop.Count == 0 || loop[loop.Count - 1].X != p.X || loop[loop.Count - 1].Y != p.Y)
				{
					loop.Add(p);
				}
			}

			while (loop.Count > 1 && loop[0].X == loop[loop.Count - 1].X && loop[0].Y == loop[loop.Count - 1].Y)
			{
				loop.RemoveAt(loop.Count - 1);
			}

			var distinct = loop.Select(p => (p.X, p.Y)).Distinct().Count();
			double length = 0;
			for (int i = 0; i < loop.Count; i++)
			{
				length += (loop[(i + 1) % loop.Count] - loop[i]).Length;
			}

			return distinct >= 3 && length > 0 ? loop : null;
		}

		private sealed class Shape
		{
			public List<Pt> Outer;

			public List<List<Pt>> Holes;

			public double MinX, MinY, MaxX, MaxY;

			public int Depth;
		}

		/// <summary>
		/// The loops in cutting order, in machine coordinates, each starting at the vertex nearest the head (first point
		/// not repeated). Deepest islands first; within a depth, nearest shape first; within a shape, holes then outer.
		/// Nesting uses a bounding-box test before the point-in-polygon test; nearest-next uses a grid of the loops'
		/// points, so thousands of shapes stay quick.
		/// </summary>
		internal static List<List<Pt>> Order(LaserOutline outline, LaserMachineProfile machine, CancellationToken ct, out int skipped)
		{
			skipped = 0;
			var shapes = new List<Shape>();
			foreach (var sh in outline.Layers.SelectMany(l => l.Shapes ?? Array.Empty<LaserShape>()))
			{
				ct.ThrowIfCancellationRequested();
				var outer = sh?.Outer?.Points == null ? null : Cuttable(sh.Outer.Points.Select(p => ToMachine(p, outline, machine)));
				var holes = new List<List<Pt>>();
				foreach (var h in sh?.Holes ?? Array.Empty<LaserLoop>())
				{
					var hole = h?.Points == null ? null : Cuttable(h.Points.Select(p => ToMachine(p, outline, machine)));
					if (hole == null)
					{
						skipped++;
					}
					else
					{
						holes.Add(hole);
					}
				}

				if (outer == null)
				{
					// its holes cannot be cut without it either
					skipped += 1 + holes.Count;
					continue;
				}

				shapes.Add(new Shape
				{
					Outer = outer,
					Holes = holes,
					MinX = outer.Min(p => p.X),
					MinY = outer.Min(p => p.Y),
					MaxX = outer.Max(p => p.X),
					MaxY = outer.Max(p => p.Y),
				});
			}

			// nesting depth: how many other shapes contain this one (an island in a hole sits inside the shape with the
			// hole); the box test rules out nearly every pair before the polygon test
			var byMinX = Enumerable.Range(0, shapes.Count).OrderBy(i => shapes[i].MinX).ToArray();
			var sortedMinX = byMinX.Select(i => shapes[i].MinX).ToArray();
			for (int i = 0; i < shapes.Count; i++)
			{
				if ((i & 0xFF) == 0)
				{
					ct.ThrowIfCancellationRequested();
				}

				var p = shapes[i].Outer[0];
				int end = UpperBound(sortedMinX, p.X);
				for (int k = 0; k < end; k++)
				{
					var j = byMinX[k];
					var other = shapes[j];
					if (j != i
						&& p.X <= other.MaxX && p.Y >= other.MinY && p.Y <= other.MaxY
						&& Profiles.Contains(other.Outer, p))
					{
						shapes[i].Depth++;
					}
				}
			}

			var order = new List<List<Pt>>();
			var head = new Pt(0, 0);
			foreach (var group in shapes.GroupBy(sh => sh.Depth).OrderByDescending(g => g.Key))
			{
				var members = group.ToList();
				var finder = new NearestFinder(members.Select(m => m.Outer).ToList());
				for (int n = 0; n < members.Count; n++)
				{
					if ((n & 0xFF) == 0)
					{
						ct.ThrowIfCancellationRequested();
					}

					var shape = members[finder.Take(head)];
					if (shape.Holes.Count > 0)
					{
						var holeFinder = new NearestFinder(shape.Holes);
						for (int h = 0; h < shape.Holes.Count; h++)
						{
							var hole = StartNear(shape.Holes[holeFinder.Take(head)], head);
							order.Add(hole);
							head = hole[0];
						}
					}

					var outer = StartNear(shape.Outer, head);
					order.Add(outer);
					head = outer[0];
				}
			}

			return order;
		}

		/// <summary>The number of values in a sorted array that are at most x.</summary>
		private static int UpperBound(double[] sorted, double x)
		{
			int lo = 0, hi = sorted.Length;
			while (lo < hi)
			{
				int mid = (lo + hi) / 2;
				if (sorted[mid] <= x)
				{
					lo = mid + 1;
				}
				else
				{
					hi = mid;
				}
			}

			return lo;
		}

		/// <summary>Finds, and takes, the loop with a point nearest a position: a grid of every loop's points, searched in rings.</summary>
		private sealed class NearestFinder
		{
			private readonly Dictionary<(long, long), List<(int loop, Pt point)>> grid = new Dictionary<(long, long), List<(int, Pt)>>();

			private readonly bool[] taken;

			private readonly double cell;

			private readonly long minCx, minCy, maxCx, maxCy;

			private int left;

			public NearestFinder(List<List<Pt>> loops)
			{
				taken = new bool[loops.Count];
				left = loops.Count;
				var all = loops.SelectMany(l => l).ToList();
				double width = all.Count == 0 ? 1 : all.Max(p => p.X) - all.Min(p => p.X);
				double height = all.Count == 0 ? 1 : all.Max(p => p.Y) - all.Min(p => p.Y);
				cell = Math.Max(0.5, Math.Max(width, height) / Math.Max(1, Math.Sqrt(all.Count)));
				minCx = minCy = long.MaxValue;
				maxCx = maxCy = long.MinValue;
				for (int i = 0; i < loops.Count; i++)
				{
					foreach (var p in loops[i])
					{
						var key = Cell(p);
						if (!grid.TryGetValue(key, out var list))
						{
							grid[key] = list = new List<(int, Pt)>();
						}

						list.Add((i, p));
						minCx = Math.Min(minCx, key.Item1);
						maxCx = Math.Max(maxCx, key.Item1);
						minCy = Math.Min(minCy, key.Item2);
						maxCy = Math.Max(maxCy, key.Item2);
					}
				}
			}

			private (long, long) Cell(Pt p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));

			/// <summary>The index of the not-yet-taken loop nearest p (ties: lower index), marked taken.</summary>
			public int Take(Pt p)
			{
				if (left <= 0)
				{
					throw new InvalidOperationException("no loop left");
				}

				var (cx, cy) = Cell(p);
				int best = -1;
				double bestDistance = double.MaxValue;
				long reach = Math.Max(Math.Max(Math.Abs(cx - minCx), Math.Abs(maxCx - cx)), Math.Max(Math.Abs(cy - minCy), Math.Abs(maxCy - cy)));
				for (long r = 0; r <= reach; r++)
				{
					for (long gx = cx - r; gx <= cx + r; gx++)
					{
						for (long gy = cy - r; gy <= cy + r; gy++)
						{
							if (Math.Max(Math.Abs(gx - cx), Math.Abs(gy - cy)) != r
								|| !grid.TryGetValue((gx, gy), out var list))
							{
								continue;
							}

							foreach (var (loop, point) in list)
							{
								if (taken[loop])
								{
									continue;
								}

								var d = (point - p).Length;
								if (d < bestDistance || (d == bestDistance && loop < best))
								{
									bestDistance = d;
									best = loop;
								}
							}
						}
					}

					// anything in a further ring is at least r cells away
					if (best >= 0 && bestDistance <= r * cell)
					{
						break;
					}
				}

				taken[best] = true;
				left--;
				return best;
			}
		}

		private static List<Pt> StartNear(List<Pt> loop, Pt head)
		{
			int best = 0;
			double bestDistance = double.MaxValue;
			for (int i = 0; i < loop.Count; i++)
			{
				var d = (loop[i] - head).Length;
				if (d < bestDistance)
				{
					bestDistance = d;
					best = i;
				}
			}

			return loop.Skip(best).Concat(loop.Take(best)).ToList();
		}
	}
}
