using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.Library.Export;
using MatterHackers.MatterControl.SlicerConfiguration;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// The printer validation used for sending to OctoPrint: the Print menu's validation
	/// (<see cref="SettingsValidation.Validate"/>) adjusted for a printer that OctoPrint drives.
	/// </summary>
	public static class OctoPrintValidation
	{
		public const string GCodeCannotBeProduced = "OctoPrintGCodeCannotBeProduced";

		public const string StillConnectedOverUsb = "OctoPrintStillConnectedOverUsb";

		public const string ForeignGCode = "OctoPrintForeignGCode";

		public const string CommandParts = "OctoPrintCommandParts";

		/// <summary>
		/// The validation list for the Send menu and the Print actions when the printer sends to OctoPrint:
		/// - drops the USB-connection checks ("Printer Disconnected" and the emulator warning on the serial port);
		/// - adds "Empty Bed", which the shared validator only reports while a USB connection is open;
		/// - adds an error when G-code can't be produced (GCodeExport is disabled, for example because printer setup
		///   is required), so the reason is visible rather than only a tooltip;
		/// - adds an error for a G-code file from elsewhere while the maintainer lock is on (EBMC Edition);
		/// - adds a warning, with a Disconnect action, when a USB connection is still open.
		/// Model and settings errors are kept and still block.
		/// </summary>
		public static List<ValidationError> Validate(PrinterConfig printer)
		{
			var errors = printer.Validate()
				.Where(e => e.ID != ValidationErrors.PrinterDisconnected
					&& !(e is SettingsValidationError settingsError && settingsError.CanonicalSettingsName == SettingsKey.com_port))
				.ToList();

			if (!printer.Bed.EditContext.IsGGCodeSource
				&& !printer.PrintableItems(printer.Bed.Scene).Any()
				&& !errors.Any(e => e.ID == ValidationErrors.NoPrintableParts))
			{
				// Same text as the shared validator's check
				errors.Add(new ValidationError(ValidationErrors.NoPrintableParts)
				{
					Error = "Empty Bed".Localize(),
					Details = "No printable parts exists within the bounds of the printer bed. Add content to continue".Localize(),
					ErrorLevel = ValidationErrorLevel.Error,
				});
			}

			// EBMC Edition: while the maintainer lock is on, a G-code file from elsewhere is not sent
			if (Maintainer.RefuseGCodeSource(printer.Bed.EditContext.IsGGCodeSource))
			{
				errors.Add(new ValidationError(ForeignGCode)
				{
					Error = MaintainerMessages.ForeignGCodeTitle(s => s.Localize()),
					Details = Maintainer.ForeignGCodeText,
					ErrorLevel = ValidationErrorLevel.Error,
				});
			}

			// ... nor a plate whose parts change temperatures or send commands
			if (Maintainer.RefuseCommandParts(printer))
			{
				errors.Add(new ValidationError(CommandParts)
				{
					Error = MaintainerMessages.CommandPartsTitle(s => s.Localize()),
					Details = Maintainer.CommandPartsText,
					ErrorLevel = ValidationErrorLevel.Error,
				});
			}

			var exporter = new GCodeExport();
			exporter.Initialize(printer);
			if (!exporter.Enabled)
			{
				string reason = exporter.DisabledReason;
				errors.Add(new ValidationError(GCodeCannotBeProduced)
				{
					Error = "G-Code Can't Be Produced".Localize(),
					Details = string.IsNullOrWhiteSpace(reason)
						? "G-code can't be produced for this printer right now.".Localize()
						: (Maintainer.CanManagePrinter
							? "{0}. Open Printer Options > Update Settings to take the latest printer settings, or run printer setup while connected over USB.".Localize()
							: "{0}. Ask a club maintainer to update the printer settings or run printer setup.".Localize()).FormatWith(reason.TrimEnd('.')),
					ErrorLevel = ValidationErrorLevel.Error,
				});
			}

			if (printer.Connection.IsConnected)
			{
				errors.Add(new ValidationError(StillConnectedOverUsb)
				{
					Error = "Still Connected Over USB".Localize(),
					Details = "This printer is set to send jobs to OctoPrint but is connected directly. Disconnect so OctoPrint can use the printer.".Localize(),
					ErrorLevel = ValidationErrorLevel.Warning,
					FixAction = new NamedAction()
					{
						Title = "Disconnect".Localize(),
						Action = () => UiThread.RunOnIdle(() => Disconnect(printer)),
					}
				});
			}

			return errors;
		}

		/// <summary>
		/// True when nothing in <see cref="Validate"/> blocks sending.
		/// </summary>
		public static bool CanSend(PrinterConfig printer)
		{
			return !Validate(printer).Any(e => e.ErrorLevel == ValidationErrorLevel.Error);
		}

		/// <summary>
		/// The same disconnect as the printer bar's Disconnect button (PrinterConnectButton).
		/// </summary>
		private static void Disconnect(PrinterConfig printer)
		{
			if (printer.Connection.Printing || printer.Connection.Paused)
			{
				StyledMessageBox.ShowMessageBox(
					(bool disconnectCancel) =>
					{
						if (disconnectCancel)
						{
							printer.Connection.Stop(false);
							printer.Connection.Disable();
						}
					},
					"WARNING: Disconnecting will stop the current print.\n\nAre you sure you want to disconnect?".Localize(),
					"Disconnect and stop the current print?".Localize(),
					StyledMessageBox.MessageType.YES_NO,
					"Disconnect".Localize(),
					"Stay Connected".Localize());
			}
			else
			{
				printer.Connection.Disable();
			}
		}
	}
}
