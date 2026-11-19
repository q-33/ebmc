using System;
using System.Collections.Generic;
using System.Globalization;

namespace MatterHackers.MatterControl.SlicerConfiguration
{
	/// <summary>One line of the pre-print summary.</summary>
	public sealed class PrintSummaryLine
	{
		/// <summary>The settings key the line is about (the brim line uses <see cref="SettingsKey.create_brim"/>).</summary>
		public string Key { get; set; }

		/// <summary>The line as shown, e.g. "Bed: 80 °C  (changed by hand; NylonG preset says 75 °C)".</summary>
		public string Text { get; set; }

		/// <summary>The name of the layer the sliced value comes from (User, Scene, Material, Quality, Oem, Base).</summary>
		public string LayerName { get; set; }

		/// <summary>True when a hand override (User layer) differs from what the presets and profile beneath it say.</summary>
		public bool ChangedByHand { get; set; }

		public override string ToString() => Text;
	}

	/// <summary>
	/// EBMC Edition: the "what will be sliced" lines shown before a print: material, nozzle and bed temperature (bed only
	/// with a heated bed), layer height, first layer and brim, each with where its value comes from and, when it was
	/// changed by hand, what the preset or profile beneath says. Free of GUI and translation code: every method takes a
	/// <c>localize</c> delegate applied to each English template before it is formatted (the app passes
	/// <c>s => s.Localize()</c>, tests pass <c>s => s</c>).
	/// </summary>
	public static class PrintSummary
	{
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
				lines.Add(Line(source, SettingsKey.bed_temperature, localize("Bed: {0}"), Temperature, localize));
			}

			lines.Add(Line(source, SettingsKey.layer_height, localize("Layer height: {0}"), Millimetres, localize));
			lines.Add(Line(source, SettingsKey.first_layer_height, localize("First layer: {0}"), Millimetres, localize));
			lines.Add(BrimLine(source, localize));

			return lines;
		}

		/// <summary>The lines joined for a message box, one per row.</summary>
		public static string Text(IEnumerable<PrintSummaryLine> lines)
		{
			return string.Join("\n", System.Linq.Enumerable.Select(lines, l => l.Text));
		}

		private static PrintSummaryLine Line(IPrintSummarySource source, string key, string template, Func<string, string> show, Func<string, string> localize)
		{
			var (value, layer) = source.GetValueAndLayerName(key);
			var (below, belowLayer) = source.GetValueAndLayerNameBelowUser(key);

			return Compose(source, key, Format(template, show(value)), layer, value, below, belowLayer, show(below), localize);
		}

		private static PrintSummaryLine BrimLine(IPrintSummarySource source, Func<string, string> localize)
		{
			var (on, onLayer) = source.GetValueAndLayerName(SettingsKey.create_brim);
			var (loops, loopsLayer) = source.GetValueAndLayerName(SettingsKey.brims);
			var (onBelow, onBelowLayer) = source.GetValueAndLayerNameBelowUser(SettingsKey.create_brim);
			var (loopsBelow, loopsBelowLayer) = source.GetValueAndLayerNameBelowUser(SettingsKey.brims);

			string now = Brim(on, loops, localize);
			string before = Brim(onBelow, loopsBelow, localize);

			// The brim is two settings; it counts as changed by hand when either hand override changes what it says
			bool fromUser = onLayer == SettingsCascade.User || (IsOn(on) && loopsLayer == SettingsCascade.User);
			string layer = fromUser ? SettingsCascade.User : onLayer;
			string belowLayer = onLayer == SettingsCascade.User || !IsOn(on) ? onBelowLayer : loopsBelowLayer;

			return Compose(source, SettingsKey.create_brim, Format(localize("Brim: {0}"), now), layer, now, before, belowLayer, before, localize);
		}

		private static PrintSummaryLine Compose(IPrintSummarySource source, string key, string shown, string layer, string value, string below, string belowLayer, string belowShown, Func<string, string> localize)
		{
			string note;
			bool changedByHand = false;

			if (layer == SettingsCascade.User)
			{
				if (belowLayer == null)
				{
					note = localize("set by hand");
					changedByHand = true;
				}
				else if (SameValue(value, below))
				{
					// a hand override that says the same as the preset: the preset is what counts
					note = LayerLabel(source, belowLayer, localize);
				}
				else
				{
					note = Format(localize("changed by hand; {0} says {1}"), LayerLabel(source, belowLayer, localize), belowShown);
					changedByHand = true;
				}
			}
			else
			{
				note = LayerLabel(source, layer, localize);
			}

			return new PrintSummaryLine()
			{
				Key = key,
				Text = note == null ? shown : shown + "  (" + note + ")",
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
			if (!IsOn(on))
			{
				return localize("off");
			}

			string trimmed = (loops ?? "").Trim();
			if (TryNumber(trimmed, out double count))
			{
				return count == 1
					? localize("on, 1 loop")
					: Format(localize("on, {0} loops"), Number(count));
			}

			if (trimmed.EndsWith("mm", StringComparison.OrdinalIgnoreCase)
				&& TryNumber(trimmed.Substring(0, trimmed.Length - 2).Trim(), out double width))
			{
				return Format(localize("on, {0} mm wide"), Number(width));
			}

			return trimmed.Length == 0 ? localize("on") : Format(localize("on, {0}"), trimmed);
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

		private static bool SameValue(string a, string b)
		{
			if (TryNumber(a, out double x) && TryNumber(b, out double y))
			{
				return x == y;
			}

			return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);
		}

		private static bool TryNumber(string value, out double number)
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
