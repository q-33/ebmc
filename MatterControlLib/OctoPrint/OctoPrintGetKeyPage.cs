using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// Runs OctoPrint's Application Keys workflow: sends the request, waits (up to 2 minutes) for the user to
	/// approve it in the OctoPrint web interface, and stores the key in UserSettings. Cancel or closing the window
	/// stops the polling.
	/// </summary>
	public class OctoPrintGetKeyPage : DialogPage
	{
		private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

		private readonly Uri baseUri;

		private readonly Action onSaved;

		private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

		private readonly WrappedTextWidget statusText;

		private readonly GuiWidget openButton;

		private volatile string authDialogUrl;

		private bool started;

		public OctoPrintGetKeyPage(Uri baseUri, Action onSaved)
			: base("Cancel".Localize())
		{
			this.baseUri = baseUri;
			this.onSaved = onSaved;

			this.WindowTitle = ApplicationController.Instance.ProductName + " - " + "Get OctoPrint API Key".Localize();
			this.HeaderText = "Get an API Key from OctoPrint".Localize();
			this.WindowSize = new Vector2(500 * GuiWidget.DeviceScale, 300 * GuiWidget.DeviceScale);

			contentRow.AddChild(new WrappedTextWidget(
				"A request for access has been sent to OctoPrint at {0}. Open OctoPrint in your browser, log in if asked, and approve the request from {1}. This window waits up to 2 minutes.".Localize().FormatWith(baseUri.Authority, ApplicationController.Instance.ProductName),
				pointSize: theme.DefaultFontSize,
				textColor: theme.TextColor));

			statusText = new WrappedTextWidget("Sending the request...".Localize(), pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
			{
				Margin = new BorderDouble(top: 10),
			};
			contentRow.AddChild(statusText);

			openButton = theme.CreateDialogButton("Open OctoPrint".Localize());
			openButton.Name = "Open OctoPrint Button";
			openButton.Click += (s, e) =>
			{
				Uri target = OctoPrintMessages.BrowserUri(authDialogUrl, baseUri);
				ApplicationController.LaunchBrowser(target.AbsoluteUri);
			};
			this.AddPageAction(openButton);
		}

		public override void OnLoad(EventArgs args)
		{
			base.OnLoad(args);

			if (!started)
			{
				started = true;
				CancellationToken token = cancellation.Token;
				Task.Run(() => AcquireAsync(token));
			}
		}

		public override void OnClosed(EventArgs e)
		{
			// Stops the polling whether the window was closed with Cancel, Escape or the close box
			cancellation.Cancel();
			base.OnClosed(e);
		}

		private async Task AcquireAsync(CancellationToken token)
		{
			var stopwatch = Stopwatch.StartNew();
			try
			{
				string key;
				using (var client = new OctoPrintClient(baseUri, null))
				{
					key = await client.AcquireAppKeyAsync(
						ApplicationController.Instance.ProductName,
						new StatusProgress(this),
						MaxWait,
						token,
						url => authDialogUrl = url);
				}

				UiThread.RunOnIdle(() =>
				{
					if (token.IsCancellationRequested)
					{
						return;
					}

					try
					{
						OctoPrintSettings.SetApiKey(baseUri, key);
					}
					catch (ArgumentException)
					{
						ShowFailure("OctoPrint sent a key with characters that are not allowed. Create a key in OctoPrint and paste it with Edit.".Localize());
						return;
					}

					onSaved?.Invoke();
					this.DialogWindow?.CloseOnIdle();
				});
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
			}
			catch (OctoPrintException e) when (e.Kind == OctoPrintError.Timeout && stopwatch.Elapsed >= MaxWait)
			{
				UiThread.RunOnIdle(() => ShowFailure("The request wasn't approved within 2 minutes. Use Get Key to try again.".Localize()));
			}
			catch (Exception e)
			{
				string message = OctoPrintErrorText.Describe(e, baseUri);
				UiThread.RunOnIdle(() => ShowFailure(message));
			}
		}

		private void ShowFailure(string message)
		{
			if (this.HasBeenClosed)
			{
				return;
			}

			statusText.TextColor = Color.Red;
			statusText.Text = message;
			openButton.Enabled = false;
			this.SetCancelButtonText("Close".Localize());
		}

		private void SetStatus(string message)
		{
			UiThread.RunOnIdle(() =>
			{
				if (!this.HasBeenClosed)
				{
					statusText.Text = message;
				}
			});
		}

		/// <summary>
		/// The client reports its steps in English; this shows localized text for them instead.
		/// </summary>
		private sealed class StatusProgress : IProgress<string>
		{
			private readonly OctoPrintGetKeyPage page;

			private int step;

			public StatusProgress(OctoPrintGetKeyPage page)
			{
				this.page = page;
			}

			public void Report(string value)
			{
				switch (step++)
				{
					case 0:
						page.SetStatus("Sending the request...".Localize());
						break;

					case 1:
						page.SetStatus("Waiting for you to approve the request in OctoPrint...".Localize());
						break;

					default:
						page.SetStatus("Approved.".Localize());
						break;
				}
			}
		}
	}
}
