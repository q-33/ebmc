using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.Font;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;

namespace MatterHackers.MatterControl.ClubUi
{
	/// <summary>
	/// EBMC Edition (ClubDialogsAndMenus): a short notice as a toast at the bottom-left of the main window: a small
	/// rounded panel on the raised background with a hairline border and a severity icon. It closes by itself
	/// (ClubToastRules.DisplaySeconds) or when clicked; at most ClubToastRules.MaxShown stack, the oldest leaving first.
	/// It is added without taking the keyboard focus.
	/// </summary>
	public static class ClubToast
	{
		private const double Width = 330;

		private static FlowLayoutWidget stack;

		/// <summary>Shows the toast; false when there is no main window to show it on (the caller falls back).</summary>
		public static bool Show(string message, double requestedSeconds, DialogSeverity severity = DialogSeverity.Info)
		{
			var window = ApplicationController.Instance.MainView?.Parents<SystemWindow>().FirstOrDefault();
			if (window == null
				|| string.IsNullOrWhiteSpace(message))
			{
				return false;
			}

			var theme = ApplicationController.Instance.Theme;

			if (stack == null
				|| stack.HasBeenClosed
				|| stack.Parent != window)
			{
				stack = new FlowLayoutWidget(FlowDirection.BottomToTop)
				{
					Name = "Club Toasts",
					HAnchor = HAnchor.Left | HAnchor.Fit,
					VAnchor = VAnchor.Bottom | VAnchor.Fit,
					Margin = new BorderDouble(16, 36, 0, 0),
				};
				window.AddChild(stack);
			}
			else
			{
				// keep the toasts above anything added to the window since
				window.RemoveChild(stack);
				stack.ClearRemovedFlag();
				window.AddChild(stack);
			}

			for (int i = ClubToastRules.ToRemoveBeforeAdding(stack.Children.Count); i > 0 && stack.Children.Count > 0; i--)
			{
				stack.Children.First().Close();
			}

			var toast = new FlowLayoutWidget()
			{
				Name = "Club Toast",
				HAnchor = HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				BackgroundColor = theme.SectionBackgroundColor,
				BorderColor = ClubStyle.Line(theme),
				BackgroundOutlineWidth = 1,
				BackgroundRadius = new RadiusCorners(9 * GuiWidget.DeviceScale),
				Padding = new BorderDouble(12, 9),
				Margin = new BorderDouble(0, 8, 0, 0),
				Cursor = Cursors.Hand,
				ToolTipText = "Click to close".Localize(),
			};

			var tile = ClubStyle.CreateSeverityTile(severity);
			if (tile != null)
			{
				// a small icon, not the dialog's 32 px tile
				var icon = tile.Children.First();
				tile.RemoveChild(icon);
				tile.Close();
				icon.Margin = new BorderDouble(right: 10);
				icon.VAnchor = VAnchor.Center;
				icon.ClearRemovedFlag();
				toast.AddChild(icon);
			}

			var pointSize = 11;
			var wrapped = new EnglishTextWrapping(pointSize * GuiWidget.DeviceScale).InsertCRs(message, (Width - 60) * GuiWidget.DeviceScale);
			toast.AddChild(new TextWidget(wrapped, pointSize: pointSize, textColor: theme.TextColor)
			{
				VAnchor = VAnchor.Center,
				Selectable = false,
			});

			foreach (var child in toast.Children)
			{
				child.Selectable = false;
			}

			toast.Click += (s, e) => toast.Close();

			stack.AddChild(toast);

			UiThread.RunOnIdle(() =>
			{
				if (!toast.HasBeenClosed)
				{
					toast.Close();
				}
			}, ClubToastRules.DisplaySeconds(requestedSeconds));

			return true;
		}
	}
}
