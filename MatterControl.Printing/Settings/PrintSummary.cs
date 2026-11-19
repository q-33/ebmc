using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MatterHackers.MatterControl.SlicerConfiguration
{
	/// <summary>One entry of the pre-print summary.</summary>
	public sealed class PrintSummaryLine
	{
		/// <summary>The settings key the line is about (the brim line uses <see cref="SettingsKey.create_brim"/>).</summary>
		public string Key { get; set; }

		/// <summary>
		/// The entry as shown: "Nozzle: 265 °C  (NylonX preset)", or, when changed by hand, two rows:
		/// "Bed: 60 °C\n    changed by hand; NylonX preset says 80 °C".
		/// </summary>
		public string Text { get; set; }

		/// <summary>The name of the layer the sliced value comes from (User, Scene, Material, Quality, Oem, Base).</summary>
		public string LayerName { get; set; }

		/// <summary>True when a hand override (User layer) differs from what the presets and profile beneath it say.</summary>
		public bool ChangedByHand { get; set; }

		public override string ToString() => Text;
	}

	/// <summary>
	/// EBMC Edition: the "what will be sliced" lines shown before a print: material, nozzle and bed temperature (bed only
	/// with a heated bed, read from the same key the G-code heats to), layer height, first layer (in mm, as sliced) and
	/// brim (off under a raft, as sliced), each with where its value comes from and, when it was changed by hand, what
	/// the preset or profile beneath says. Free of GUI and translation code: every method takes a <c>localize</c>
	/// delegate applied to each English template before it is formatted (the app passes <c>s => s.Localize()</c>, tests
	/// pass <c>s => s</c>).
	/// </summary>
	public static class PrintSummary
	{
		/// <summary>The indent of the second row of an entry that was changed by hand.</summary>
		public const string NoteIndent = "    ";

		/// <summary>The per-surface bed temperature keys, as listed by SettingsHelpers.ActiveMaterialHasAnyBedTemperatures.</summary>
		public static IReadOnlyList<string> SurfaceBedTemperatureKeys { get; } = new[]
		{
			SettingsKey.bed_temperature_blue_tape,
			SettingsKey.bed_temperature_buildtak,
			SettingsKey.bed_temperature_garolite,
			SettingsKey.bed_temperature_glass,
			SettingsKey.bed_temperature_kapton,
			SettingsKey.bed_temperature_pei,
			SettingsKey.bed_temperature_pp,
		};

		/// <summary>
		/// The bed temperature key the G-code heats to (SettingsHelpers.ActiveBedTemperatureSetting calls this): the
		/// surface's own key when the printer has a swappable bed and the material sets any per-surface temperature,
		/// otherwise <see cref="SettingsKey.bed_temperature"/>.
		/// </summary>
		public static string ActiveBedTemperatureKey(bool hasSwappableBed, bool materialHasAnySurfaceTemperature, string bedSurface)
		{
			if (!hasSwappableBed || !materialHasAnySurfaceTemperature)
			{
				return SettingsKey.bed_temperature;
			}

			switch (bedSurface)
			{
				case "Blue Tape":
					return SettingsKey.bed_temperature_blue_tape;

				case "BuildTak":
					return SettingsKey.bed_temperature_buildtak;

				case "Garolite":
					return SettingsKey.bed_temperature_garolite;

				case "Glass":
					return SettingsKey.bed_temperature_glass;

				case "Kapton":
					return SettingsKey.bed_temperature_kapton;

				case "PEI":
					return SettingsKey.bed_temperature_pei;

				case "Polypropylene":
					return SettingsKey.bed_temperature_pp;

				default:
					return SettingsKey.bed_temperature;
			}
		}

		public static List<PrintSummaryLine> Build(IPrintSummarySource source, Func<string, string> localize)
		{
			if (source == null)
			{
				throw new ArgumentNullException(nameof(source));
			}

			localize = localize ?? (s => s);

			string materialName = string.IsNullOrWhiteSpace(source.MaterialName) ? null : source.MaterialName.Trim();
			var lines = new List<PrintSummaryLine>
			{
				new PrintSummaryLine()
				{
					Key = SettingsKey.active_material_key,
					Text = Format(localize("Material: {0}"), materialName ?? localize("none chosen")),
					LayerName = null,
				},
				Line(source, SettingsKey.temperature, localize("Nozzle: {0}"), Temperature, localize),
			};

			if (IsOn(source.GetValueAndLayerName(SettingsKey.has_heated_bed).currentValue))
			{
				string bedKey = source.BedTemperatureKey ?? SettingsKey.bed_temperature;
				string template = bedKey == SettingsKey.bed_temperature
					? localize("Bed: {0}")
					: Format(localize("Bed ({0}): {{0}}"), source.GetValueAndLayerName(SettingsKey.bed_surface).currentValue.Trim());
				lines.Add(Line(source, bedKey, template, Temperature, localize));
			}

			string layerHeight = source.GetValueAndLayerName(SettingsKey.layer_height).currentValue;
			lines.Add(Line(source, SettingsKey.layer_height, localize("Layer height: {0}"), Millimetres, localize));
			lines.Add(Line(source, SettingsKey.first_layer_height, localize("First layer: {0}"), v => FirstLayer(v, layerHeight), localize));
			lines.Add(BrimLine(source, localize));

			return lines;
		}

		/// <summary>The entries joined for a message box, one row per line.</summary>
		public static string Text(IEnumerable<PrintSummaryLine> lines)
		{
			return string.Join("\n", lines.Select(l => l.Text));
		}

		/// <summary>
		/// A value in the summary's words for the keys the summary shows ("Bed", "80 °C"); other keys get
		/// <paramref name="keyLabel"/> and the value as stored.
		/// </summary>
		public static (string label, string value) Describe(string key, string value, Func<string, string> keyLabel, Func<string, string> localize)
		{
			localize = localize ?? (s => s);

			if (key == SettingsKey.temperature)
			{
				return (localize("Nozzle"), Temperature(value));
			}

			if (key == SettingsKey.bed_temperature || SurfaceBedTemperatureKeys.Contains(key))
			{
				return (localize("Bed"), Temperature(value));
			}

			if (key == SettingsKey.layer_height)
			{
				return (localize("Layer height"), Millimetres(value));
			}

			return ((keyLabel ?? (k => k))(key), Shown(value));
		}

		private static PrintSummaryLine Line(IPrintSummarySource source, string key, string template, Func<string, string> show, Func<string, string> localize)
		{
			var (value, layer) = source.GetValueAndLayerName(key);
			var (below, belowLayer) = source.GetValueAndLayerNameBelowUser(key);

			string shown = Format(template, show(value));

			if (layer == SettingsCascade.User)
			{
				if (belowLayer == null)
				{
					return Entry(key, shown, layer, localize("set by hand"), changedByHand: true);
				}

				if (!SameValue(value, below))
				{
					return Entry(key, shown, layer, Format(localize("changed by hand; {0} says {1}"), LayerLabel(source, belowLayer, localize), show(below)), changedByHand: true);
				}

				// a hand override that says the same as the preset: the preset is what counts
				layer = belowLayer;
			}

			return Entry(key, shown, layer, LayerLabel(source, layer, localize), changedByHand: false);
		}

		private static PrintSummaryLine BrimLine(IPrintSummarySource source, Func<string, string> localize)
		{
			var (raft, raftLayer) = source.GetValueAndLayerName(SettingsKey.create_raft);
			var (on, onLayer) = source.GetValueAndLayerName(SettingsKey.create_brim);
			var (loops, loopsLayer) = source.GetValueAndLayerName(SettingsKey.brims);
			var (onBelow, onBelowLayer) = source.GetValueAndLayerNameBelowUser(SettingsKey.create_brim);
			var (loopsBelow, loopsBelowLayer) = source.GetValueAndLayerNameBelowUser(SettingsKey.brims);

			string template = localize("Brim: {0}");

			// the slicer draws no brim under a raft (and the printable area is worked out the same way)
			if (IsOn(raft))
			{
				return Entry(SettingsKey.create_brim, Format(template, localize("off")), raftLayer, localize("a raft is on"), changedByHand: false);
			}

			// a hand override equal to what is beneath it does not count
			if (onLayer == SettingsCascade.User && onBelowLayer != null && IsOn(on) == IsOn(onBelow))
			{
				onLayer = onBelowLayer;
			}

			if (loopsLayer == SettingsCascade.User && loopsBelowLayer != null && SameValue(loops, loopsBelow))
			{
				loopsLayer = loopsBelowLayer;
			}

			string shown = Format(template, Brim(on, loops, localize));

			if (onLayer == SettingsCascade.User)
			{
				string note = onBelowLayer == null
					? localize("set by hand")
					: Format(localize("changed by hand; {0} says {1}"), LayerLabel(source, onBelowLayer, localize), Brim(onBelow, loopsBelow, localize));
				return Entry(SettingsKey.create_brim, shown, SettingsCascade.User, note, changedByHand: true);
			}

			if (IsOn(on) && loopsLayer == SettingsCascade.User)
			{
				string note = loopsBelowLayer == null
					? localize("loops set by hand")
					: Format(localize("loops changed by hand; {0} says {1}"), LayerLabel(source, loopsBelowLayer, localize), Loops(loopsBelow, localize));
				return Entry(SettingsKey.create_brim, shown, SettingsCascade.User, note, changedByHand: true);
			}

			string label = LayerLabel(source, onLayer, localize);
			if (IsOn(on) && loopsLayer != null && loopsLayer != onLayer)
			{
				label = Format(localize("{0}; loops: {1}"), label, LayerLabel(source, loopsLayer, localize));
			}

			return Entry(SettingsKey.create_brim, shown, onLayer, label, changedByHand: false);
		}

		private static PrintSummaryLine Entry(string key, string shown, string layer, string note, bool changedByHand)
		{
			string text = note == null
				? shown
				: changedByHand
					? shown + "\n" + NoteIndent + note
					: shown + "  (" + note + ")";

			return new PrintSummaryLine()
			{
				Key = key,
				Text = text,
				LayerName = layer,
				ChangedByHand = changedByHand,
			};
		}

		/// <summary>Where a value comes from, in words: "NylonX preset", "Standard quality", "printer profile"...</summary>
		public static string LayerLabel(IPrintSummarySource source, string layerName, Func<string, string> localize)
		{
			localize = localize ?? (s => s);

			switch (layerName)
			{
				case SettingsCascade.User:
					return localize("set by hand");

				case SettingsCascade.Scene:
					return localize("part settings");

				case SettingsCascade.Material:
					return string.IsNullOrWhiteSpace(source?.MaterialName)
						? localize("material preset")
						: Format(localize("{0} preset"), source.MaterialName.Trim());

				case SettingsCascade.Quality:
					return string.IsNullOrWhiteSpace(source?.QualityName)
						? localize("quality preset")
						: Format(localize("{0} quality"), source.QualityName.Trim());

				case SettingsCascade.Oem:
					return localize("printer profile");

				case SettingsCascade.Base:
					return localize("default");

				default:
					return null;
			}
		}

		private static string Brim(string on, string loops, Func<string, string> localize)
		{
			return IsOn(on) ? localize("on") + ", " + Loops(loops, localize) : localize("off");
		}

		private static string Loops(string loops, Func<string, string> localize)
		{
			string trimmed = (loops ?? "").Trim();
			if (TryNumber(trimmed, out double count))
			{
				return count == 1 ? localize("1 loop") : Format(localize("{0} loops"), Number(count));
			}

			if (trimmed.EndsWith("mm", StringComparison.OrdinalIgnoreCase)
				&& TryNumber(trimmed.Substring(0, trimmed.Length - 2).Trim(), out double width))
			{
				return Format(localize("{0} mm wide"), Number(width));
			}

			return trimmed.Length == 0 ? "?" : trimmed;
		}

		/// <summary>
		/// The first layer as the slicer takes it (AsPercentOfReferenceOrDirect on layer_height): a percentage of the
		/// layer height, and 0 meaning the layer height. Shown in mm, with the percentage in brackets.
		/// </summary>
		private static string FirstLayer(string value, string layerHeight)
		{
			string trimmed = (value ?? "").Trim();
			TryNumber(layerHeight, out double reference);

			if (trimmed.Contains("%"))
			{
				if (TryNumber(trimmed.Replace("%", ""), out double percent))
				{
					double mm = reference * percent / 100.0;
					return Number(mm == 0 ? reference : mm) + " mm (" + Number(percent) + "%)";
				}

				return Shown(value);
			}

			if (TryNumber(trimmed, out double direct))
			{
				return Number(direct == 0 ? reference : direct) + " mm";
			}

			return Shown(value);
		}

		private static string Temperature(string value)
		{
			return TryNumber(value, out double degrees) ? Number(degrees) + " °C" : Shown(value);
		}

		private static string Millimetres(string value)
		{
			return TryNumber(value, out double mm) ? Number(mm) + " mm" : Shown(value);
		}

		private static string Shown(string value)
		{
			return string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();
		}

		private static bool IsOn(string value)
		{
			string v = (value ?? "").Trim();
			return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Equal as numbers when both are numbers, otherwise as trimmed text.</summary>
		public static bool SameValue(string a, string b)
		{
			if (TryNumber(a, out double x) && TryNumber(b, out double y))
			{
				return x == y;
			}

			return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);
		}

		internal static bool TryNumber(string value, out double number)
		{
			return double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
		}

		private static string Number(double value)
		{
			return value.ToString("0.###", CultureInfo.CurrentCulture);
		}

		private static string Format(string template, params object[] args)
		{
			return string.Format(CultureInfo.CurrentCulture, template, args);
		}
	}
}
