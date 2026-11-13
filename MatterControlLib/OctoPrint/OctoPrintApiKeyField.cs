using System;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.SlicerConfiguration;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// The settings row for the OctoPrint API key. It never shows, reads or writes the key through the printer
	/// profile: the base UIField value stays empty, and the key is read from and written to UserSettings via
	/// <see cref="OctoPrintSettings"/>. The row shows a masked summary ("••••1a2b") with Edit,
	/// Get Key, Test and Forget buttons.
	/// </summary>
	public class OctoPrintApiKeyField : UIField
	{
		private readonly PrinterConfig printer;

		private readonly ThemeConfig theme;

		private TextWidget summary;

		private GuiWidget editButton;

		private GuiWidget getKeyButton;

		private GuiWidget testButton;

		private GuiWidget forgetButton;

		private Uri baseUri;

		public OctoPrintApiKeyField(PrinterConfig printer, ThemeConfig theme)
		{
			this.printer = printer;
			this.theme = theme;
		}

		public override void Initialize(ref int tabIndex)
		{
			// The summary on its own line and the four buttons in two rows of two, so no button is ever cut off by a
			// long summary or a narrow settings panel (its minimum is 400 * (int)DeviceScale, so at text sizes
			// between 1 and 2 the buttons grow but the minimum does not)
			var column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};

			summary = new TextWidget(string.Empty, textColor: theme.TextColor, pointSize: theme.DefaultFontSize)
			{
				AutoExpandBoundsToText = true,
				HAnchor = HAnchor.Left,
				Margin = new BorderDouble(bottom: 5),
				Name = "OctoPrint API Key Summary",
			};
			column.AddChild(summary);

			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Left | HAnchor.Fit,
				VAnchor = VAnchor.Fit,
			};
			column.AddChild(row);

			var secondRow = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Left | HAnchor.Fit,
				VAnchor = VAnchor.Fit,
			};
			column.AddChild(secondRow);

			editButton = AddButton(row, "Edit".Localize(), "Paste or type the API key".Localize(), ref tabIndex);
			editButton.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				if (baseUri != null)
				{
					DialogWindow.Show(new OctoPrintApiKeyPage(baseUri, Refresh));
				}
			});

			getKeyButton = AddButton(row, "Get Key".Localize(), "Ask OctoPrint for a key; you approve the request in the OctoPrint web interface".Localize(), ref tabIndex);
			getKeyButton.Click += (s, e) => GetKey();

			testButton = AddButton(secondRow, "Test".Localize(), "Check the address and key".Localize(), ref tabIndex);
			testButton.Click += (s, e) => Test();

			forgetButton = AddButton(secondRow, "Forget".Localize(), "Delete the key stored on this computer".Localize(), ref tabIndex);
			forgetButton.Click += (s, e) => Forget();

			// EBMC Edition: say why the key buttons are off while the maintainer lock is on (rebuilt when the lock changes)
			if (!Maintainer.CanEdit(printer.Settings, SettingsKey.octoprint_api_key))
			{
				editButton.ToolTipText = getKeyButton.ToolTipText = forgetButton.ToolTipText = Maintainer.LockedText;
			}

			this.Content = column;

			Refresh();
		}

		protected override string ConvertValue(string newValue)
		{
			// Whatever the profile holds for this setting is ignored; the key lives in UserSettings only
			return string.Empty;
		}

		private GuiWidget AddButton(GuiWidget row, string text, string toolTip, ref int tabIndex)
		{
			var button = new ThemedTextButton(text, theme)
			{
				BackgroundColor = theme.MinimalShade,
				VAnchor = VAnchor.Center,
				Margin = theme.ButtonSpacing,
				ToolTipText = toolTip,
				TabIndex = tabIndex++,
				Name = "OctoPrint " + text + " Button",
			};
			row.AddChild(button);
			return button;
		}

		private void Refresh()
		{
			if (summary == null || summary.HasBeenClosed)
			{
				return;
			}

			bool haveAddress = OctoPrintSettings.TryGetBaseUri(printer, out baseUri, out _);
			string key = haveAddress ? OctoPrintSettings.GetApiKey(baseUri) : null;

			if (!haveAddress)
			{
				summary.Text = "Enter the address first".Localize();
			}
			else if (key == null)
			{
				summary.Text = "Not set".Localize();
			}
			else
			{
				summary.Text = OctoPrintMessages.MaskKey(key);
			}

			// EBMC Edition: changing or removing the key needs a club maintainer while the lock is on; testing it does not
			bool canChange = Maintainer.CanEdit(printer.Settings, SettingsKey.octoprint_api_key);

			editButton.Enabled = haveAddress && canChange;
			getKeyButton.Enabled = haveAddress && canChange;
			testButton.Enabled = key != null;
			forgetButton.Enabled = key != null && canChange;
		}

		private void Test()
		{
			Uri address = baseUri;
			string key = address == null ? null : OctoPrintSettings.GetApiKey(address);
			if (key == null)
			{
				return;
			}

			testButton.Enabled = false;

			Task.Run(async () =>
			{
				string message;
				try
				{
					using (var client = new OctoPrintClient(address, key))
					{
						VersionInfo version = await client.GetVersionAsync(CancellationToken.None);
						PrinterState state = await client.GetPrinterStateAsync(CancellationToken.None);

						message = "Connected to OctoPrint {0}.".Localize().FormatWith(string.IsNullOrWhiteSpace(version.Server) ? "?" : version.Server.Trim())
							+ " "
							+ (state == null ? "The printer is not connected in OctoPrint.".Localize() : "The printer is connected in OctoPrint.".Localize());
					}
				}
				catch (Exception e)
				{
					message = OctoPrintErrorText.Describe(e, address);
				}

				UiThread.RunOnIdle(() =>
				{
					Refresh();
					StyledMessageBox.ShowMessageBox(message, "OctoPrint".Localize());
				});
			});
		}

		private void GetKey()
		{
			Uri address = baseUri;
			if (address == null)
			{
				return;
			}

			getKeyButton.Enabled = false;

			Task.Run(async () =>
			{
				bool supported = false;
				string error = null;
				try
				{
					// The Application Keys endpoints need no key
					using (var client = new OctoPrintClient(address, null))
					{
						supported = await client.ProbeAppKeysAsync(CancellationToken.None);
					}
				}
				catch (Exception e)
				{
					error = OctoPrintErrorText.Describe(e, address);
				}

				UiThread.RunOnIdle(() =>
				{
					Refresh();

					if (error != null)
					{
						StyledMessageBox.ShowMessageBox(error, "OctoPrint".Localize());
					}
					else if (!supported)
					{
						StyledMessageBox.ShowMessageBox(
							OctoPrintErrorText.Describe(new OctoPrintException(OctoPrintError.AppKeysUnavailable, null, null), address),
							"OctoPrint".Localize());
					}
					else
					{
						DialogWindow.Show(new OctoPrintGetKeyPage(address, Refresh));
					}
				});
			});
		}

		private void Forget()
		{
			Uri address = baseUri;
			if (address == null)
			{
				return;
			}

			StyledMessageBox.ShowMessageBox(
				(forget) =>
				{
					if (forget)
					{
						OctoPrintSettings.SetApiKey(address, null);
						Refresh();
					}
				},
				"Delete the OctoPrint API key stored on this computer for {0}?".Localize().FormatWith(address.Authority),
				"Forget API Key".Localize(),
				StyledMessageBox.MessageType.YES_NO,
				"Forget".Localize(),
				"Cancel".Localize(),
				// EBMC Edition: club template labels; Enter keeps the key (ClubDialogsAndMenus)
				new ClubMessageOptions()
				{
					YesLabel = "Forget key".Localize(),
					NoLabel = "Keep key".Localize(),
					Destructive = true,
				});
		}
	}
}
