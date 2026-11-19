using System.Linq;

namespace MatterHackers.MatterControl.SlicerConfiguration
{
	/// <summary>
	/// EBMC Edition: the pre-print summary's view of a real <see cref="PrinterSettings"/>, through its own
	/// <see cref="PrinterSettings.GetValueAndLayerName"/> on <see cref="PrinterSettings.DefaultLayerCascade"/> (with and
	/// without the User layer). Kept out of <see cref="PrintSummary"/> so the summary can be tested without this class.
	/// </summary>
	public sealed class PrinterSettingsSummarySource : IPrintSummarySource
	{
		private readonly PrinterSettings settings;

		public PrinterSettingsSummarySource(PrinterSettings settings)
		{
			this.settings = settings;
		}

		public string MaterialName => settings.MaterialLayer?.Name;

		public string QualityName => settings.QualityLayer?.Name;

		/// <summary>The key the G-code heats the bed to: SettingsHelpers' own selection.</summary>
		public string BedTemperatureKey => settings.Helpers.ActiveBedTemperatureSetting;

		public (string currentValue, string layerName) GetValueAndLayerName(string key)
		{
			return Normalize(settings.GetValueAndLayerName(key));
		}

		public (string currentValue, string layerName) GetValueAndLayerNameBelowUser(string key)
		{
			var userLayer = settings.UserLayer;
			return Normalize(settings.GetValueAndLayerName(key, settings.DefaultLayerCascade.Where(layer => layer != userLayer)));
		}

		private static (string currentValue, string layerName) Normalize((string currentValue, string layerName) found)
		{
			// GetValueAndLayerName returns ("", "") when no layer has the key
			return string.IsNullOrEmpty(found.layerName) ? ("", null) : found;
		}
	}
}
