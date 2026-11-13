using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.CustomWidgets;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.Library.Export;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.MatterControl.SlicerConfiguration;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// The "Send" button that replaces Connect and Print on the printer bar when the printer sends its jobs to
	/// OctoPrint. Modelled on ExportSlaPopupMenu: a popup with the quick settings from the Print menu, the
	/// validation panel, a live OctoPrint status line, Upload / Upload and Print, and Export G-Code.
	/// </summary>
	public class OctoPrintSendPopupMenu : PopupMenuButton
	{
		private static readonly TimeSpan StatusBudget = TimeSpan.FromSeconds(5);

		private readonly PrinterConfig printer;

		private readonly SettingsContext settingsContext;

		public OctoPrintSendPopupMenu(PrinterConfig printer, ThemeConfig theme)
			: base(theme)
		{
			this.printer = printer;
			this.DrawArrow = true;
			this.BackgroundColor = theme.ToolbarButtonBackground;
			this.HoverColor = theme.ToolbarButtonHover;
			this.MouseDownColor = theme.ToolbarButtonDown;
			this.Name = "OctoPrintSendPopupMenu";
			this.HAnchor = HAnchor.Fit;
			this.VAnchor = VAnchor.Fit;

			settingsContext = new SettingsContext(printer, null, NamedSettingsLayers.All);

			this.PopupHAnchor = HAnchor.Fit;
			this.PopupVAnchor = VAnchor.Fit;
			this.MakeScrollable = false;

			this.DynamicPopupContent = () => this.CreatePopupContent(theme);

			this.AddChild(new ThemedTextButton("Send".Localize(), theme)
			{
				Selectable = false,
				Padding = theme.TextButtonPadding.Clone(right: 5)
			});
		}

		private GuiWidget CreatePopupContent(ThemeConfig theme)
		{
			var menuTheme = ApplicationController.Instance.MenuTheme;

			int tabIndex = 0;

			var sendPanel = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				Padding = theme.DefaultContainerPadding,
				BackgroundColor = menuTheme.BackgroundColor
			};

			sendPanel.AddChild(new TextWidget("Options".Localize(), textColor: menuTheme.TextColor, pointSize: theme.DefaultFontSize)
			{
				HAnchor = HAnchor.Left
			});

			var optionsPanel = new IgnoredFlowLayout()
			{
				Name = "OctoPrintSendPopupMenu Panel",
				HAnchor = HAnchor.Fit | HAnchor.Left,
				VAnchor = VAnchor.Fit,
				Padding = 5,
				MinimumSize = new Vector2(400 * GuiWidget.DeviceScale, 65 * GuiWidget.DeviceScale),
			};
			sendPanel.AddChild(optionsPanel);

			// The same quick settings as the Print menu (PrintPopupMenu), built the same way
			var settingsToAdd = new[]
			{
				SettingsKey.layer_height,
				SettingsKey.fill_density,
				SettingsKey.create_raft,
				SettingsKey.create_per_layer_support
			};

			foreach (var key in settingsToAdd)
			{
				var settingsData = PrinterSettings.SettingsData[key];
				var row = SliceSettingsTabView.CreateItemRow(settingsData, settingsContext, printer, menuTheme, ref tabIndex);

				if (row is SliceSettingsRow settingsRow)
				{
					settingsRow.ArrowDirection = ArrowDirection.Left;
				}

				optionsPanel.AddChild(row);
			}

			// OctoPrint status, filled in when the popup opens
			var statusText = new WrappedTextWidget("Checking OctoPrint...".Localize(), pointSize: theme.DefaultFontSize, textColor: menuTheme.TextColor)
			{
				Name = "OctoPrint Status",
				Margin = new BorderDouble(5, 10, 5, 5),
			};
			sendPanel.AddChild(statusText);
			this.UpdateStatus(statusText);

			// Perform validation before popup (without the USB-connection checks, which do not apply here)
			var errors = OctoPrintValidation.Validate(printer);

			var hasErrors = errors.Any(e => e.ErrorLevel == ValidationErrorLevel.Error);
			var hasWarnings = errors.Any(e => e.ErrorLevel == ValidationErrorLevel.Warning
				&& !PartsOutsideBedValidation.IsDismissed(e));

			var hasErrorsOrWarnings = hasErrors || hasWarnings;
			if (hasErrorsOrWarnings)
			{
				string label = hasErrors ? "Action Required".Localize() : "Action Recommended".Localize();

				// On its own line, so the button rows below never have to share their width with it
				sendPanel.AddChild(new TextWidget(label, textColor: hasErrors ? Color.Red : theme.PrimaryAccentColor, pointSize: theme.DefaultFontSize)
				{
					HAnchor = HAnchor.Left,
					AutoExpandBoundsToText = true,
					Margin = new BorderDouble(5, 5, 5, 0),
				});
			}

			// Two rows, each narrower than the options panel's minimum width (400 * DeviceScale, which grows with
			// the text size like the buttons do): Export on the left, then Upload and Upload and Print on the right
			var exportRow = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch
			};
			sendPanel.AddChild(exportRow);

			var setupRow = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch
			};

			setupRow.AddChild(new HorizontalSpacer());

			var exportPlugin = new GCodeExport();
			exportPlugin.Initialize(printer);

			// Local export stays reachable: the same call as the Print menu's Export button
			var exportButton = menuTheme.CreateDialogButton("Export G-Code...".Localize());
			exportButton.Name = "OctoPrint Export GCode Button";
			exportButton.Enabled = exportPlugin.Enabled;
			exportButton.ToolTipText = exportPlugin.Enabled ? "Export G-Code".Localize() : exportPlugin.DisabledReason;
			exportButton.Click += (s, e) =>
			{
				this.CloseMenu();
				ExportPrintItemPage.DoExport(
					new[] { new InMemoryLibraryItem(printer.Bed.Scene) },
					printer,
					exportPlugin);
			};
			exportRow.AddChild(exportButton);

			bool canSend = !hasErrors && exportPlugin.Enabled;
			string disabledReason = exportPlugin.Enabled ? "Fix the problems listed below first".Localize() : exportPlugin.DisabledReason;

			var uploadButton = menuTheme.CreateDialogButton("Upload".Localize());
			uploadButton.Name = "OctoPrint Upload Button";
			uploadButton.Enabled = canSend;
			uploadButton.ToolTipText = canSend ? "Upload the G-code to OctoPrint without starting it".Localize() : disabledReason;
			uploadButton.Click += (s, e) =>
			{
				this.CloseMenu();
				UiThread.RunOnIdle(() => OctoPrintSender.SendBed(printer, startPrint: false));
			};
			setupRow.AddChild(uploadButton);

			var printButton = menuTheme.CreateDialogButton("Upload and Print".Localize());
			printButton.Name = "OctoPrint Upload And Print Button";
			printButton.Enabled = canSend;
			printButton.ToolTipText = canSend ? "Upload the G-code to OctoPrint and start printing".Localize() : disabledReason;
			printButton.Click += (s, e) =>
			{
				this.CloseMenu();
				UiThread.RunOnIdle(() => OctoPrintSender.SendBed(printer, startPrint: true));
			};
			printButton.MouseEnterBounds += (s, e) => printButton.SetActiveUiHint(KeyHints.ForThisComputer("Ctrl + P".Localize()));
			setupRow.AddChild(printButton);

			if (canSend)
			{
				theme.ApplyPrimaryActionStyle(printButton);
			}

			sendPanel.AddChild(setupRow);

			if (hasErrorsOrWarnings)
			{
				var errorsPanel = new ValidationErrorsPanel(errors, menuTheme)
				{
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Fit,
					Margin = 3
				};

				// Wrap and append the validation errors panel, as the Print menu does
				var errorsContainer = new FlowLayoutWidget(FlowDirection.TopToBottom)
				{
					HAnchor = HAnchor.Fit,
					VAnchor = VAnchor.Fit,
					BackgroundColor = sendPanel.BackgroundColor
				};

				// Clear bottom padding
				sendPanel.Padding = sendPanel.Padding.Clone(bottom: 2);

				errorsContainer.AddChild(sendPanel);
				errorsContainer.AddChild(errorsPanel);

				return errorsContainer;
			}

			return sendPanel;
		}

		private void UpdateStatus(WrappedTextWidget statusText)
		{
			// Settings are read here, on the UI thread; only the network calls run in the background
			OctoPrintClient client = OctoPrintSettings.CreateClient(printer, out Uri baseUri, out string error);
			if (client == null)
			{
				statusText.Text = error;
				return;
			}

			Task.Run(async () =>
			{
				string text;
				using (client)
				using (var budget = new CancellationTokenSource(StatusBudget))
				{
					try
					{
						VersionInfo version = await client.GetVersionAsync(budget.Token);
						PrinterState state = await client.GetPrinterStateAsync(budget.Token);
						JobInfo job = state != null && (state.Printing || state.Paused) ? await client.GetJobAsync(budget.Token) : null;
						text = OctoPrintMessages.StatusLine(version, state, job, s => s.Localize());
					}
					catch (OperationCanceledException)
					{
						// The 5 second budget ran out
						text = OctoPrintErrorText.Describe(new OctoPrintException(OctoPrintError.Timeout, null, null), baseUri);
					}
					catch (Exception e)
					{
						text = OctoPrintErrorText.Describe(e, baseUri);
					}
				}

				UiThread.RunOnIdle(() =>
				{
					if (!statusText.HasBeenClosed)
					{
						statusText.Text = text;
					}
				});
			});
		}

		private class IgnoredFlowLayout : FlowLayoutWidget, IIgnoredPopupChild
		{
			public IgnoredFlowLayout()
				: base(FlowDirection.TopToBottom)
			{
			}

			public bool KeepMenuOpen => false;
		}
	}
}
