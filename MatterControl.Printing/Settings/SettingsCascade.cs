using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.SlicerConfiguration
{
	/// <summary>
	/// What the pre-print summary needs to know about a printer's settings: the value that will be sliced, the layer it
	/// comes from, and the value the cascade gives when the User layer (the hand overrides) is left out. The app reads
	/// these from <c>PrinterSettings</c> (<see cref="PrinterSettingsSummarySource"/>); tests use <see cref="SettingsCascade"/>.
	/// Layer names are the ones <c>PrinterSettings.GetValueAndLayerName</c> returns: User, Scene, Material, Quality, Oem, Base.
	/// </summary>
	public interface IPrintSummarySource
	{
		/// <summary>The active material preset's name, or null or blank when no material preset is chosen.</summary>
		string MaterialName { get; }

		/// <summary>The active quality preset's name, or null or blank when no quality preset is chosen.</summary>
		string QualityName { get; }

		/// <summary>The bed temperature key the G-code heats to (see <see cref="PrintSummary.ActiveBedTemperatureKey"/>).</summary>
		string BedTemperatureKey { get; }

		/// <summary>The value that wins and the name of its layer; layerName is null when no layer has the key.</summary>
		(string currentValue, string layerName) GetValueAndLayerName(string key);

		/// <summary>The same, with the User layer left out: what the value would be without the hand override.</summary>
		(string currentValue, string layerName) GetValueAndLayerNameBelowUser(string key);
	}

	/// <summary>
	/// A small, GUI-free model of the settings cascade: named layers searched in the fixed order <see cref="Order"/>
	/// (the order of <c>PrinterSettings.DefaultLayerCascade</c>; a test checks the two agree). The first layer that has a
	/// key wins.
	/// </summary>
	public sealed class SettingsCascade : IPrintSummarySource
	{
		public const string User = "User";
		public const string Scene = "Scene";
		public const string Material = "Material";
		public const string Quality = "Quality";
		public const string Oem = "Oem";
		public const string Base = "Base";

		/// <summary>Highest precedence first.</summary>
		public static IReadOnlyList<string> Order { get; } = new[] { User, Scene, Material, Quality, Oem, Base };

		private readonly Dictionary<string, IReadOnlyDictionary<string, string>> layers = new Dictionary<string, IReadOnlyDictionary<string, string>>();

		public string MaterialName { get; set; }

		public string QualityName { get; set; }

		/// <summary>Sets (or, with null, removes) the layer of the given name; the name must be one of <see cref="Order"/>.</summary>
		public SettingsCascade With(string layerName, IReadOnlyDictionary<string, string> values)
		{
			if (Array.IndexOf((string[])Order, layerName) < 0)
			{
				throw new ArgumentException("Unknown settings layer: " + layerName, nameof(layerName));
			}

			if (values == null)
			{
				layers.Remove(layerName);
			}
			else
			{
				layers[layerName] = values;
			}

			return this;
		}

		/// <summary>
		/// The same selection as the app's (SettingsHelpers.ActiveBedTemperatureSetting): a per-surface temperature counts
		/// when the Material, Oem or Base layer sets it above 0 (PrinterSettings.MaterialLayerCascade).
		/// </summary>
		public string BedTemperatureKey
		{
			get
			{
				bool materialHasAny = PrintSummary.SurfaceBedTemperatureKeys.Any(key =>
				{
					foreach (string name in new[] { Material, Oem, Base })
					{
						if (layers.TryGetValue(name, out var values) && values.TryGetValue(key, out string value))
						{
							return PrintSummary.TryNumber(value, out double degrees) && degrees > 0;
						}
					}

					return false;
				});

				return PrintSummary.ActiveBedTemperatureKey(
					IsOn(GetValueAndLayerName(SettingsKey.has_swappable_bed).currentValue),
					materialHasAny,
					GetValueAndLayerName(SettingsKey.bed_surface).currentValue);
			}
		}

		private static bool IsOn(string value) => value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

		public (string currentValue, string layerName) GetValueAndLayerName(string key) => Find(key, skipUser: false);

		public (string currentValue, string layerName) GetValueAndLayerNameBelowUser(string key) => Find(key, skipUser: true);

		private (string currentValue, string layerName) Find(string key, bool skipUser)
		{
			foreach (string name in Order)
			{
				if (skipUser && name == User)
				{
					continue;
				}

				if (layers.TryGetValue(name, out var values)
					&& values.TryGetValue(key, out string value))
				{
					return (value, name);
				}
			}

			return ("", null);
		}
	}
}
