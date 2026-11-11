using System;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// A small dialog for pasting an API key by hand. The key is saved to UserSettings only.
	/// </summary>
	public class OctoPrintApiKeyPage : DialogPage
	{
		private readonly Uri baseUri;

		private readonly Action onSaved;

		private readonly ThemedTextEditWidget keyEditor;

		private readonly WrappedTextWidget errorText;

		public OctoPrintApiKeyPage(Uri baseUri, Action onSaved)
			: base("Cancel".Localize())
		{
			this.baseUri = baseUri;
			this.onSaved = onSaved;

			this.WindowTitle = ApplicationController.Instance.ProductName + " - " + "OctoPrint API Key".Localize();
			this.HeaderText = "OctoPrint API Key".Localize();
			this.WindowSize = new Vector2(500 * GuiWidget.DeviceScale, 260 * GuiWidget.DeviceScale);

			contentRow.AddChild(new WrappedTextWidget(
				"Paste the API key for {0}. You can create one in OctoPrint under Settings > Application Keys. The key is kept in this computer's user settings only, not in the printer profile.".Localize().FormatWith(baseUri.Authority),
				pointSize: theme.DefaultFontSize,
				textColor: theme.TextColor));

			keyEditor = new ThemedTextEditWidget(string.Empty, theme, pixelWidth: 360 * GuiWidget.DeviceScale)
			{
				HAnchor = HAnchor.Stretch,
				Margin = new BorderDouble(top: 10),
				Name = "OctoPrint API Key Editor",
			};
			contentRow.AddChild(keyEditor);

			errorText = new WrappedTextWidget(string.Empty, pointSize: theme.DefaultFontSize, textColor: Color.Red)
			{
				Margin = new BorderDouble(top: 10),
			};
			contentRow.AddChild(errorText);

			var saveButton = theme.CreateDialogButton("Save".Localize());
			saveButton.Name = "Save OctoPrint API Key Button";
			saveButton.Click += (s, e) => UiThread.RunOnIdle(Save);
			this.AddPageAction(saveButton);
		}

		public override void OnLoad(EventArgs args)
		{
			base.OnLoad(args);
			keyEditor.Focus();
		}

		private void Save()
		{
			if (!OctoPrintMessages.TryCleanApiKey(keyEditor.Text, out string key))
			{
				errorText.Text = "That doesn't look like an API key. A key has no spaces and only letters, digits and punctuation.".Localize();
				return;
			}

			OctoPrintSettings.SetApiKey(baseUri, key);
			keyEditor.Text = string.Empty;

			onSaved?.Invoke();
			this.DialogWindow?.CloseOnIdle();
		}
	}
}
