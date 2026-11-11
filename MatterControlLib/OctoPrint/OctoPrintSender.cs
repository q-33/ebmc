using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DataStorage;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.Library.Export;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// Sends what is on the printer's bed to OctoPrint: produces the G-code exactly as Export G-Code does (same
	/// GCodeExport pipeline, including the write-regex, baby-step and leveling streams), uploads it under the part
	/// name plus a date-time suffix, and optionally starts the print. Runs as a task in the app's task list, with
	/// the task's stop button cancelling it.
	/// </summary>
	public static class OctoPrintSender
	{
		private static int busy;

		private static int busyMessageShowing;

		public static void SendBed(PrinterConfig printer, bool startPrint)
		{
			if (printer == null)
			{
				return;
			}

			// 1. Feature switch, address and key (read here, on the UI thread)
			OctoPrintClient client = OctoPrintSettings.CreateClient(printer, out Uri baseUri, out string error);
			if (client == null)
			{
				ShowMessage(error);
				return;
			}

			// What Export G-Code would export for this bed: the G-code file itself, or the scene
			// The remote name comes from the G-code file's name, or from the printable parts on the bed (not from the
			// bed's plating file, whose name is the printer name and a date)
			ILibraryItem item;
			string baseName;
			if (printer.Bed.EditContext.IsGGCodeSource)
			{
				item = printer.Bed.EditContext.SourceItem;
				baseName = OctoPrintMessages.RemoteBaseName(item.Name);
			}
			else if (printer.PrintableItems(printer.Bed.Scene).Any())
			{
				item = new InMemoryLibraryItem(printer.Bed.Scene);
				baseName = OctoPrintMessages.ChooseBaseName(
					printer.Bed.Scene.Children
						.Where(child => printer.PrintableItems(child).Any())
						.Select(child => child.Name)
						.ToList());
			}
			else
			{
				client.Dispose();
				ShowMessage("There is nothing on the bed to send.".Localize());
				return;
			}

			var exporter = new GCodeExport();
			exporter.Initialize(printer);
			if (!exporter.Enabled)
			{
				client.Dispose();
				string reason = exporter.DisabledReason;
				ShowMessage(string.IsNullOrWhiteSpace(reason) ? "G-code can't be produced for this printer right now.".Localize() : reason);
				return;
			}

			if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
			{
				client.Dispose();

				// Only one of these at a time, so a repeating Ctrl+P can't stack them
				if (Interlocked.CompareExchange(ref busyMessageShowing, 1, 0) == 0)
				{
					UiThread.RunOnIdle(() =>
					{
						StyledMessageBox.ShowMessageBox(
							(response) => Interlocked.Exchange(ref busyMessageShowing, 0),
							"A job is already being sent to OctoPrint. Wait for it to finish, or cancel it in the task list.".Localize(),
							"OctoPrint".Localize());
					});
				}

				return;
			}

			string title = startPrint ? "Sending to OctoPrint".Localize() : "Uploading to OctoPrint".Localize();

			ApplicationController.Instance.Tasks.Execute(
				title,
				printer,
				async (reporter, cancellationTokenSource) =>
				{
					var cancellationToken = cancellationTokenSource.Token;
					try
					{
						await RunAsync(client, baseUri, exporter, item, baseName, startPrint, reporter, cancellationToken);
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
						// Cancelled from the task's stop button: nothing to say
					}
					catch (Exception e)
					{
						// The task runner swallows exceptions, so report here
						ShowMessage(OctoPrintErrorText.Describe(e, baseUri));
					}
					finally
					{
						client.Dispose();
						Interlocked.Exchange(ref busy, 0);
					}
				});
		}

		private static async Task RunAsync(OctoPrintClient client, Uri baseUri, GCodeExport exporter, ILibraryItem item, string baseName, bool startPrint, Action<double, string> reporter, CancellationToken cancellationToken)
		{
			// 2. Pre-flight: is OctoPrint there, does it accept the key, and can it print?
			reporter?.Invoke(0, "Checking OctoPrint".Localize());
			await client.GetVersionAsync(cancellationToken);
			PrinterState state = await client.GetPrinterStateAsync(cancellationToken);

			if (startPrint)
			{
				string problem = null;
				if (state != null && (state.Printing || state.Paused))
				{
					problem = "A print is already running.".Localize();
				}
				else if (state == null || !state.Operational || state.Error)
				{
					problem = "The printer isn't connected in OctoPrint.".Localize();
				}

				if (problem != null)
				{
					bool uploadOnly = await AskAsync(
						problem + "\n\n" + "You can upload the file now and start it from OctoPrint later.".Localize(),
						"OctoPrint".Localize(),
						"Upload Only".Localize(),
						"Cancel".Localize(),
						cancellationToken);

					if (!uploadOnly)
					{
						return;
					}

					startPrint = false;
				}
			}

			cancellationToken.ThrowIfCancellationRequested();

			// 3. The G-code, exactly as Export G-Code produces it, in a temporary file
			string tempPath = ApplicationDataStorage.Instance.GetTempFileName("gcode");
			try
			{
				string preparing = "Preparing G-code".Localize();
				reporter?.Invoke(0, preparing);

				List<ValidationError> errors = await exporter.Generate(
					new[] { item },
					tempPath,
					(ratio, message) => reporter?.Invoke(Clamp(ratio) * 0.5, string.IsNullOrEmpty(message) ? preparing : message),
					cancellationToken);

				cancellationToken.ThrowIfCancellationRequested();

				if (errors?.Any(e => e.ErrorLevel == ValidationErrorLevel.Error) == true)
				{
					UiThread.RunOnIdle(() => ApplicationController.Instance.ShowValidationErrors("Nothing was sent to OctoPrint".Localize(), errors));
					return;
				}

				// GCodeExport reports most failures only in a message box, so check its explicit result and the file
				if (!exporter.LastExportSucceeded
					|| !File.Exists(tempPath)
					|| new FileInfo(tempPath).Length == 0)
				{
					ShowMessage("The G-code couldn't be produced, so nothing was sent to OctoPrint. Try Export G-Code to see what went wrong.".Localize());
					return;
				}

				// 4. Remote name: the part name, plus the date and time so members don't overwrite each other
				string remoteName = OctoPrintClient.AddTimestamp(
					OctoPrintClient.SanitizeRemoteName(baseName),
					DateTime.Now);

				// 5. Upload into the second half of the progress bar
				string uploading = "Uploading {0}".Localize().FormatWith(remoteName);
				reporter?.Invoke(0.5, uploading);

				double sent = 0;
				UploadResult result;
				try
				{
					result = await client.UploadAsync(
						tempPath,
						remoteName,
						select: false,
						print: startPrint,
						new ReporterProgress(fraction =>
						{
							sent = fraction;
							reporter?.Invoke(0.5 + Clamp(fraction) * 0.5, uploading);
						}),
						cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && sent >= 0.99)
				{
					// Cancelled after the whole file had been handed over: OctoPrint may already have stored it (and, if
					// printing was requested, started the print). Cancel is otherwise silent, but not here.
					ShowOutcome(
						(startPrint
							? "Cancelled, but the whole file had already been sent. OctoPrint may have stored it as {0} and started printing; check OctoPrint."
							: "Cancelled, but the whole file had already been sent. OctoPrint may have stored it as {0}; check OctoPrint.").Localize().FormatWith(remoteName),
						baseUri);
					throw;
				}

				// 6. Outcome
				ShowOutcome(OctoPrintMessages.UploadOutcome(startPrint, result, s => s.Localize()), baseUri);
			}
			finally
			{
				TryDelete(tempPath);
			}
		}

		/// <summary>
		/// A yes/no question that also ends, as "no", when the task is stopped: the box is then closed.
		/// </summary>
		private static async Task<bool> AskAsync(string message, string caption, string yes, string no, CancellationToken cancellationToken)
		{
			var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			DialogWindow window = null;

			UiThread.RunOnIdle(() =>
			{
				if (cancellationToken.IsCancellationRequested)
				{
					answer.TrySetResult(false);
					return;
				}

				// The same page and size StyledMessageBox.ShowMessageBox uses, kept so it can be closed on Stop
				window = DialogWindow.Show(new StyledMessageBox.MessageBoxPage(
					(response) => answer.TrySetResult(response),
					message,
					caption,
					StyledMessageBox.MessageType.YES_NO,
					null,
					400,
					300,
					yes,
					no,
					ApplicationController.Instance.Theme));
			});

			using (cancellationToken.Register(() =>
			{
				answer.TrySetResult(false);
				UiThread.RunOnIdle(() => window?.CloseOnIdle());
			}))
			{
				return await answer.Task;
			}
		}

		private static void ShowMessage(string message)
		{
			UiThread.RunOnIdle(() =>
			{
				StyledMessageBox.ShowMessageBox(message, "OctoPrint".Localize());
			});
		}

		private static void ShowOutcome(string message, Uri baseUri)
		{
			UiThread.RunOnIdle(() =>
			{
				var theme = ApplicationController.Instance.Theme;
				var openButton = theme.CreateDialogButton("Open OctoPrint".Localize());
				openButton.HAnchor = HAnchor.Left;
				openButton.Margin = new BorderDouble(top: 10);
				openButton.Click += (s, e) => ApplicationController.LaunchBrowser(baseUri.AbsoluteUri);

				StyledMessageBox.ShowMessageBox(
					null,
					message,
					"OctoPrint".Localize(),
					new GuiWidget[] { openButton },
					StyledMessageBox.MessageType.OK);
			});
		}

		private static double Clamp(double value)
		{
			if (double.IsNaN(value))
			{
				return 0;
			}

			return Math.Max(0, Math.Min(1, value));
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch
			{
			}
		}

		/// <summary>
		/// Passes upload progress straight to the task reporter (Progress&lt;T&gt; would post to a synchronization
		/// context, which the task's thread does not have).
		/// </summary>
		private sealed class ReporterProgress : IProgress<double>
		{
			private readonly Action<double> report;

			public ReporterProgress(Action<double> report)
			{
				this.report = report;
			}

			public void Report(double value)
			{
				report(value);
			}
		}
	}
}
