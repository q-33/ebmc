using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the keyboard shortcuts sheet (brand menu "Keyboard shortcuts", or "?" in the 3D view), grouped File,
	/// Edit, View, Tools, built from ShortcutTable. Shortcut names follow this computer (Cmd and Option on a Mac).
	/// </summary>
	public class ShortcutsPage : DialogPage
	{
		public ShortcutsPage()
			: base("Close".Localize())
		{
			var title = "Keyboard shortcuts".Localize();
			this.WindowTitle = ApplicationController.Instance.ProductName + " - " + title;
			this.HeaderText = title;
			this.WindowSize = new Vector2(560 * GuiWidget.DeviceScale, 620 * GuiWidget.DeviceScale);

			var scrollable = new ScrollableWidget(true)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch,
			};
			scrollable.ScrollArea.HAnchor = HAnchor.Stretch;
			contentRow.AddChild(scrollable);

			var column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			scrollable.AddChild(column);

			var rows = ShortcutTable.Shown(AdvancedTools.IsEnabled).ToList();
			foreach (var group in ShortcutTable.Groups)
			{
				var groupRows = rows.Where(r => r.Group == group).ToList();
				if (groupRows.Count == 0)
				{
					continue;
				}

				column.AddChild(new TextWidget(group.Localize(), pointSize: theme.DefaultFontSize + 1, textColor: theme.PrimaryAccentColor, bold: true)
				{
					Margin = new BorderDouble(0, 4, 0, 10),
				});

				foreach (var entry in groupRows)
				{
					var row = new FlowLayoutWidget()
					{
						HAnchor = HAnchor.Stretch,
						VAnchor = VAnchor.Fit,
						Padding = new BorderDouble(0, 3),
					};

					row.AddChild(new TextWidget(KeyHints.ForThisComputer(ShortcutTable.KeysForSystem(entry.Keys, AggContext.OperatingSystem == OSType.Mac).Localize()), pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
					{
						MinimumSize = new Vector2(230 * GuiWidget.DeviceScale, 0),
						VAnchor = VAnchor.Center,
					});

					row.AddChild(new WrappedTextWidget(entry.Action.Localize(), pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
					{
						HAnchor = HAnchor.Stretch,
						VAnchor = VAnchor.Center | VAnchor.Fit,
					});

					column.AddChild(row);
				}
			}
		}

		/// <summary>Shows the sheet (one at a time).</summary>
		public static void Show()
		{
			UiThread.RunOnIdle(() => DialogWindow.Show<ShortcutsPage>());
		}
	}
}
