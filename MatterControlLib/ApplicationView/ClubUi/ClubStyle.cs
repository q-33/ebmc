using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.ImageProcessing;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.ClubUi
{
	/// <summary>
	/// EBMC Edition: the ClubDialogsAndMenus switch and the club dialog template's look (frame 7 of the UX mock-ups).
	/// Colours come from the theme except the two danger colours, which the theme has no key for.
	/// </summary>
	public static class ClubStyle
	{
		/// <summary>True when OEMSettings turns on the club dialog template, menu style and toasts.</summary>
		public static bool On => OemSettings.Instance.ClubDialogsAndMenus;

		/// <summary>Fill of a destructive button: a deep red that keeps white text readable (5.6:1).</summary>
		public static Color DangerFill { get; } = new Color("#B8321A");

		public static Color DangerFillHover { get; } = new Color("#CC3B20");

		/// <summary>Danger text and icons on the dark ground (the same red as the error icon).</summary>
		public static Color DangerText { get; } = new Color("#FF7A5C");

		public static Color Info { get; } = new Color("#6C9BFF");

		public static Color Warning { get; } = new Color("#FFDF69");

		public const double ButtonHeight = 28;

		public const double TitlePointSize = 15;

		public const double TileSize = 32;

		public static Color Line(ThemeConfig theme) => theme.BorderColor20;

		/// <summary>Muted text: the menu theme's light text, or the text colour faded when a theme has none.</summary>
		public static Color Muted(ThemeConfig theme) => theme.LightTextColor.Alpha0To255 > 0 ? theme.LightTextColor : theme.TextColor.WithAlpha(165);

		public static Color Disabled(ThemeConfig theme) => theme.DisabledColor.Alpha0To255 > 0 ? theme.DisabledColor : theme.TextColor.WithAlpha(110);

		public static bool IsMac => AggContext.OperatingSystem == OSType.Mac;

		/// <summary>
		/// A dialog button in a club style. With the switch off this is the stock dialog button.
		/// </summary>
		public static ThemedTextButton CreateClubDialogButton(this ThemeConfig theme, string text, PageActionStyle style)
		{
			var button = theme.CreateDialogButton(text);
			if (On)
			{
				ApplyButtonStyle(theme, button, style, isDefault: false);
			}

			return button;
		}

		/// <summary>Paints a footer button for its style; <paramref name="isDefault"/> adds the focus ring Enter goes to.</summary>
		public static void ApplyButtonStyle(ThemeConfig theme, GuiWidget widget, PageActionStyle style, bool isDefault)
		{
			if (!(widget is ThemedButton button))
			{
				return;
			}

			var text = theme.TextColor;
			var wash = theme.TextColor.WithAlpha(24);
			Color background, hover, foreground;
			double outline = 0;
			Color border = Color.Transparent;

			switch (style)
			{
				case PageActionStyle.Primary:
					background = theme.PrimaryAccentColor;
					hover = theme.ResolveColor(theme.PrimaryAccentColor, Color.White.WithAlpha(38));
					foreground = Color.White;
					break;

				case PageActionStyle.Destructive:
					background = DangerFill;
					hover = DangerFillHover;
					foreground = Color.White;
					break;

				case PageActionStyle.Cancel:
					background = Color.Transparent;
					hover = wash;
					foreground = text;
					break;

				case PageActionStyle.Aside:
					background = Color.Transparent;
					hover = wash;
					foreground = Muted(theme);
					break;

				default:
					background = Color.Transparent;
					hover = wash;
					foreground = text;
					outline = 1;
					border = Line(theme);
					break;
			}

			if (isDefault
				&& style != PageActionStyle.Primary)
			{
				// the focus ring of the safe default (Enter); a filled primary is its own cue
				outline = 2;
				border = theme.PrimaryAccentColor.WithAlpha(140);
			}

			button.BackgroundColor = background;
			button.HoverColor = hover;
			button.MouseDownColor = hover;
			button.BackgroundRadius = new RadiusCorners(theme.ButtonRadius * GuiWidget.DeviceScale);
			button.BackgroundOutlineWidth = outline;
			button.BorderColor = border;

			if (button is ThemedTextButton textButton)
			{
				textButton.TextColor = foreground;
				textButton.Height = ButtonHeight * GuiWidget.DeviceScale;
				textButton.Padding = new BorderDouble(12, 0);
			}
		}

		/// <summary>The 32 px tinted tile before a dialog title, or null for no tile.</summary>
		public static GuiWidget CreateSeverityTile(DialogSeverity severity)
		{
			string icon;
			Color tint;
			switch (severity)
			{
				case DialogSeverity.Info:
					icon = "StatusInfoTip_16x.png";
					tint = Info;
					break;
				case DialogSeverity.Warning:
					icon = "SettingsGroupWarning_16x.png";
					tint = Warning;
					break;
				case DialogSeverity.Danger:
					icon = "SettingsGroupError_16x.png";
					tint = DangerText;
					break;
				case DialogSeverity.Question:
					icon = System.IO.Path.Combine("Shapes", "dialog-question.png");
					tint = ApplicationController.Instance.Theme.PrimaryAccentColor;
					break;
				default:
					return null;
			}

			var size = TileSize * GuiWidget.DeviceScale;
			var tile = new GuiWidget(size, size)
			{
				HAnchor = HAnchor.Absolute,
				VAnchor = VAnchor.Absolute | VAnchor.Center,
				BackgroundColor = tint.WithAlpha(36),
				BackgroundRadius = new RadiusCorners(8 * GuiWidget.DeviceScale),
				Margin = new BorderDouble(right: 10),
				Selectable = false,
				Name = "Dialog Severity Tile",
			};

			// the status icons carry their colour in the pixels (callers do not tint them)
			ImageBuffer image = StaticData.Instance.LoadIcon(icon, 16, 16);
			tile.AddChild(new ImageWidget(image)
			{
				HAnchor = HAnchor.Center,
				VAnchor = VAnchor.Center,
				Selectable = false,
			});

			return tile;
		}

		/// <summary>The dialog title in the template: Nunito Bold, 15 pt.</summary>
		public static TextWidget CreateTitle(ThemeConfig theme, string text)
		{
			return new TextWidget(text, pointSize: TitlePointSize, textColor: theme.TextColor, typeFace: ApplicationController.Instance.GetTypeFace("Nunito_Bold"))
			{
				AutoExpandBoundsToText = true,
				EllipsisIfClipped = true,
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Center,
			};
		}

		/// <summary>A tinted icon for a menu row (muted, or the danger colour).</summary>
		public static ImageBuffer MenuIcon(string file, Color color)
		{
			return StaticData.Instance.LoadIcon(file, 16, 16).GrayToColor(color);
		}

		internal static Vector2 Scaled(double x, double y) => new Vector2(x * GuiWidget.DeviceScale, y * GuiWidget.DeviceScale);
	}
}
