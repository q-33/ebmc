using System;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// EBMC Edition: how a member's importance (0 to 3 stars) and role look on the Members tab. The only place the
	/// level colours are defined. Level 0 has no colour and no stars: such a row looks as it did before importance
	/// existed.
	/// <para>Stars are drawn as vector shapes (<see cref="ImportanceStars"/>), not as the black star character (U+2605): the interface
	/// font may not have that glyph.</para>
	/// </summary>
	public static class MemberImportanceStyle
	{
		// bronze, silver, gold: light enough for the dark theme's near-black background and the accent overlay of a
		// selected row there, and darker variants for a light theme's white background and its (pale) selected row
		private static readonly Color[] DarkThemeLevels = { Color.Transparent, new Color("#D9A06E"), new Color("#C9CED6"), new Color("#F2C14E") };

		private static readonly Color[] LightThemeLevels = { Color.Transparent, new Color("#8C5A2B"), new Color("#5F6670"), new Color("#8A6500") };

		/// <summary>The width of the coloured stripe at the left edge of a list row (before the device scale).</summary>
		public const double StripeWidth = 3;

		/// <summary>An importance as shown: out-of-range values (only possible in a hand-edited file) are clamped.</summary>
		public static int Level(int importance) => MembersRules.ClampImportance(importance);

		/// <summary>The colour of a level (transparent for 0).</summary>
		public static Color LevelColor(ThemeConfig theme, int importance)
		{
			var levels = theme.IsDarkTheme ? DarkThemeLevels : LightThemeLevels;
			return levels[Level(importance)];
		}

		/// <summary>
		/// The member's role as a small pill-shaped badge (the text in the theme's text colour, so it stays legible on a
		/// selected row; a tint and outline in the level colour, or a neutral one for level 0).
		/// </summary>
		public static RoleBadge MakeRoleBadge(ThemeConfig theme, string role, int importance, string name)
		{
			var badge = new RoleBadge(theme)
			{
				VAnchor = VAnchor.Center,
				Margin = new BorderDouble(left: 6),
				Name = name,
			};
			badge.Show(role, importance);
			return badge;
		}

		/// <summary>
		/// A small button showing <paramref name="stars"/> stars (with a "+" when <paramref name="andUp"/>), or the text
		/// <paramref name="text"/> when it is given. The current choice has an accent outline and tint.
		/// </summary>
		public static ThemedButton LevelButton(ThemeConfig theme, int stars, bool andUp, string text, bool current, string name)
		{
			var button = new ThemedButton(theme)
			{
				HAnchor = HAnchor.Fit,
				VAnchor = VAnchor.Center,
				Height = theme.ButtonHeight * 0.8,
				Padding = new BorderDouble(8, 0),
				Margin = new BorderDouble(right: 4),
				BackgroundRadius = theme.ButtonRadius * GuiWidget.DeviceScale,
				Name = name,
			};

			if (text != null)
			{
				button.AddChild(new TextWidget(text, pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Center,
					VAnchor = VAnchor.Center,
					Selectable = false,
				});
			}
			else
			{
				button.AddChild(new ImportanceStars(theme, stars, andUp, theme.DefaultFontSize)
				{
					HAnchor = HAnchor.Center,
					VAnchor = VAnchor.Center,
				});
			}

			SetCurrent(theme, button, current);
			return button;
		}

		/// <summary>Marks a level button as the current choice or not.</summary>
		public static void SetCurrent(ThemeConfig theme, ThemedButton button, bool current)
		{
			button.BackgroundColor = current ? theme.AccentMimimalOverlay : theme.SlightShade;
			button.BorderColor = current ? theme.PrimaryAccentColor : Color.Transparent;
			button.BackgroundOutlineWidth = current ? 1 : 0;
			foreach (var child in button.Children)
			{
				if (child is ImportanceStars starsWidget)
				{
					starsWidget.OnAccent = current;
				}
			}

			button.Invalidate();
		}
	}

	/// <summary>
	/// A role as a pill: a fixed-size widget (so it can be centred in a left-to-right row) around one line of text.
	/// </summary>
	public class RoleBadge : GuiWidget
	{
		private readonly ThemeConfig theme;

		private readonly TextWidget text;

		public RoleBadge(ThemeConfig theme)
		{
			this.theme = theme;
			this.Selectable = false;
			this.BackgroundOutlineWidth = 1;
			text = new TextWidget("", pointSize: theme.DefaultFontSize - 2, textColor: theme.TextColor)
			{
				HAnchor = HAnchor.Center,
				VAnchor = VAnchor.Center,
				AutoExpandBoundsToText = true,
				Selectable = false,
			};
			this.AddChild(text);
		}

		/// <summary>Shows <paramref name="role"/> at <paramref name="importance"/>; hidden when the role is empty.</summary>
		public void Show(string role, int importance)
		{
			text.Text = role ?? "";
			this.Visible = !string.IsNullOrEmpty(role);
			this.Width = Math.Ceiling(text.Width + 12 * DeviceScale);
			this.Height = Math.Ceiling(text.Height + 2 * DeviceScale);

			int level = MemberImportanceStyle.Level(importance);
			var color = level == 0 ? theme.TextColor : MemberImportanceStyle.LevelColor(theme, level);
			this.BackgroundColor = color.WithAlpha(level == 0 ? 20 : 40);
			this.BorderColor = color.WithAlpha(level == 0 ? 110 : 200);
			this.BackgroundRadius = Math.Max(1, this.Height / 2);
			this.Invalidate();
		}
	}

	/// <summary>
	/// One to three five-pointed stars in the level colour, drawn as vector shapes (no font glyph needed). On an
	/// accent-coloured background (<see cref="OnAccent"/>) each star gets a thin outline in the theme background colour
	/// so it stays clear of the accent.
	/// </summary>
	public class ImportanceStars : GuiWidget
	{
		private readonly ThemeConfig theme;

		private readonly int count;

		private readonly bool plus;

		private readonly double starSize;

		private readonly double gap;

		public ImportanceStars(ThemeConfig theme, int importance, bool plus, double pointSize)
		{
			this.theme = theme;
			this.count = MemberImportanceStyle.Level(importance);
			this.plus = plus;
			this.starSize = Math.Round(pointSize * 1.05 * DeviceScale);
			this.gap = Math.Max(1, Math.Round(1.5 * DeviceScale));
			this.Selectable = false;

			double width = count * starSize + Math.Max(0, count - 1) * gap;
			if (plus)
			{
				width += gap * 2 + starSize * 0.7;
			}

			this.Width = Math.Max(1, width);
			this.Height = starSize;
		}

		/// <summary>Drawn on an accent-coloured background (a selected row or the current button).</summary>
		public bool OnAccent { get; set; }

		public override void OnDraw(Graphics2D graphics2D)
		{
			var color = MemberImportanceStyle.LevelColor(theme, count);
			double cell = starSize / 2;
			double outline = Math.Max(1, 1.5 * DeviceScale);

			// inset by half the outline plus half a pixel for anti-aliasing, so the outline drawn on an accent background
			// stays inside the widget (the top point would otherwise touch its top edge); the widget's size is unchanged.
			// Always inset, so a star looks the same selected or not
			double outer = Math.Max(1, cell - outline / 2 - 0.5);
			double centerY = Height / 2;
			for (int i = 0; i < count; i++)
			{
				var star = Star(cell + i * (starSize + gap), centerY, outer, outer * 0.45);
				if (OnAccent)
				{
					graphics2D.Render(new Stroke(star, outline), theme.BackgroundColor);
				}

				graphics2D.Render(star, color);
			}

			if (plus)
			{
				// a "+" after the stars: two bars in the text colour
				double size = starSize * 0.7;
				double left = count * starSize + Math.Max(0, count - 1) * gap + gap * 2;
				double bar = Math.Max(1, Math.Round(1.5 * DeviceScale));
				var textColor = theme.TextColor;
				graphics2D.FillRectangle(new RectangleDouble(left, centerY - bar / 2, left + size, centerY + bar / 2), textColor);
				graphics2D.FillRectangle(new RectangleDouble(left + size / 2 - bar / 2, centerY - size / 2, left + size / 2 + bar / 2, centerY + size / 2), textColor);
			}

			base.OnDraw(graphics2D);
		}

		private static VertexStorage Star(double centerX, double centerY, double outer, double inner)
		{
			var star = new VertexStorage();
			for (int k = 0; k < 10; k++)
			{
				// the first point straight up (y grows upwards), then alternating outer and inner points
				double angle = Math.PI / 2 + k * Math.PI / 5;
				double radius = k % 2 == 0 ? outer : inner;
				double x = centerX + Math.Cos(angle) * radius;
				double y = centerY + Math.Sin(angle) * radius;
				if (k == 0)
				{
					star.MoveTo(x, y);
				}
				else
				{
					star.LineTo(x, y);
				}
			}

			star.ClosePolygon();
			return star;
		}
	}
}
