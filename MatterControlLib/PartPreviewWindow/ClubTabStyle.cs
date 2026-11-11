using System;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	public enum ClubGlyph
	{
		Close,
		Plus,
		ChevronDown
	}

	/// <summary>
	/// EBMC Edition: design values and drawing geometry for the "A. Club label" top bar and tab strip.
	/// Lengths are in device independent units: multiply by GuiWidget.DeviceScale before use
	/// (the helpers below take the scale and do it for you). Point sizes are passed straight to
	/// TextWidget, which applies DeviceScale itself. Colours always come from the theme so the
	/// dark and the light club themes both work.
	/// This class depends only on agg-sharp so the geometry can be rendered offline.
	/// </summary>
	public static class ClubTabStyle
	{
		// ---- Bar ----

		/// <summary>Height of the whole top bar (was about 36).</summary>
		public const double BarHeight = 40;

		/// <summary>Space between the window's left edge and the brand button.</summary>
		public const double BarPaddingLeft = 6;

		/// <summary>Width of every hairline: the bar's bottom line, the active tab border and the rule after the brand.</summary>
		public const double LineWidth = 1;

		// ---- Tabs ----

		/// <summary>Tab height; tabs are bottom aligned in the bar so the active one meets the workspace.</summary>
		public const double TabHeight = 32;

		/// <summary>Radius of the two top corners of a tab. The bottom corners are square.</summary>
		public const double TabCornerRadius = 6;

		/// <summary>Horizontal padding inside a tab, each side.</summary>
		public const double TabPaddingX = 14;

		/// <summary>Gap between neighbouring tabs.</summary>
		public const double TabGap = 2;

		/// <summary>Narrowest a tab may get when many tabs share the bar.</summary>
		public const double TabMinimumWidth = 40;

		/// <summary>Height of the accent bar along the top of the active tab.</summary>
		public const double AccentHeight = 2;

		/// <summary>The accent bar stops this far from each end of the tab, so it sits between the rounded corners.</summary>
		public const double AccentInset = 6;

		/// <summary>Tab label font (a TTF in StaticData/Fonts, loaded with ApplicationController.GetTypeFace).</summary>
		public const string LabelFontName = "Nunito_SemiBold";

		public const double LabelPointSize = 11;

		/// <summary>Alpha applied to theme.TextColor for muted labels and glyphs (no theme key exists for muted text).</summary>
		public const int MutedTextAlpha = 165;

		// ---- Tab close button ----

		/// <summary>Diameter of the circular hit area / hover circle.</summary>
		public const double CloseButtonSize = 18;

		/// <summary>Size of the "x" glyph box (Lucide proportions: the strokes span half of it).</summary>
		public const double CloseGlyphSize = 12;

		/// <summary>Gap between the label and the close circle.</summary>
		public const double CloseGap = 8;

		/// <summary>Right padding of a closable tab: the circle sits 6 units into the normal 14 unit padding.</summary>
		public const double ClosablePaddingRight = TabPaddingX - 6;

		// ---- Glyphs drawn in code ----

		/// <summary>Stroke width of drawn glyphs (2 units on a 24 grid at 16 units).</summary>
		public const double GlyphStroke = 1.3;

		public const double PlusGlyphSize = 16;

		public const double ChevronGlyphSize = 10;

		// ---- "+" (new tab) and search buttons ----

		public const double SquareButtonSize = 28;

		public const double ButtonRadius = 6;

		/// <summary>Space between the last tab's gap and the "+" button.</summary>
		public const double NewTabLeadingSpace = 4;

		/// <summary>Space between the search button and the window's right edge.</summary>
		public const double SearchMarginRight = 8;

		// ---- Brand button ----

		public const double BrandHeight = 28;

		public const double BrandPaddingX = 8;

		/// <summary>Gap between badge, product name and chevron.</summary>
		public const double BrandItemGap = 8;

		public const int BrandBadgeSize = 20;

		public const string BrandFontName = "Nunito_Bold";

		public const double BrandPointSize = 12;

		/// <summary>Height of the vertical rule between the brand button and the tabs.</summary>
		public const double RuleHeight = 20;

		/// <summary>Space either side of the vertical rule.</summary>
		public const double RuleSpace = 8;

		// ---- Colours (all from the theme) ----

		/// <summary>Subtle line colour: #3D3731 on EBMC-Dark, #D9CFBF on EBMC-Paper.</summary>
		public static Color LineColor(ThemeConfig theme) => theme.BorderColor20;

		public static Color MutedTextColor(ThemeConfig theme) => theme.TextColor.WithAlpha(MutedTextAlpha);

		public static Color HoverFillColor(ThemeConfig theme) => theme.InactiveTabColor;

		/// <summary>
		/// Square, rounded, borderless bar button ("+" and search): no fill at rest, hover fill,
		/// line colour while pressed. Keeps the button's icon, name, tooltip and click behaviour.
		/// </summary>
		public static void ApplySquareButtonStyle(ThemedButton button, ThemeConfig theme)
		{
			var size = SquareButtonSize * GuiWidget.DeviceScale;
			button.Width = size;
			button.Height = size;
			button.BackgroundRadius = new RadiusCorners(ButtonRadius * GuiWidget.DeviceScale);
			button.BackgroundColor = Color.Transparent;
			button.HoverColor = HoverFillColor(theme);
			button.MouseDownColor = LineColor(theme);
		}

		// ---- Pixel helpers ----

		/// <summary>
		/// Converts units to a whole number of device pixels (at least <paramref name="minimum"/>),
		/// so hairlines and edges land on pixel boundaries at any DeviceScale, including non-integer ones.
		/// </summary>
		public static double Px(double units, double scale, double minimum = 1)
		{
			return Math.Max(minimum, Math.Round(units * scale));
		}

		private static RectangleDouble Snap(RectangleDouble r)
		{
			return new RectangleDouble(Math.Round(r.Left), Math.Round(r.Bottom), Math.Round(r.Right), Math.Round(r.Top));
		}

		// ---- Geometry (pure functions of bounds and scale) ----

		/// <summary>
		/// A rectangle whose two top corners are rounded and whose bottom corners are square.
		/// </summary>
		public static VertexStorage TopRoundedRect(double left, double bottom, double right, double top, double radius)
		{
			radius = Math.Max(0, Math.Min(radius, Math.Min((right - left) / 2, top - bottom)));

			var path = new VertexStorage();
			path.MoveTo(left, bottom);
			AddArc(path, left + radius, top - radius, radius, Math.PI, Math.PI / 2);
			AddArc(path, right - radius, top - radius, radius, Math.PI / 2, 0);
			path.LineTo(right, bottom);
			path.ClosePolygon();

			return path;
		}

		private static void AddArc(VertexStorage path, double cx, double cy, double radius, double startAngle, double endAngle)
		{
			// About one segment per device pixel of arc length, enough for a smooth curve at any scale
			int segments = Math.Max(2, Math.Min(32, (int)Math.Ceiling(radius * Math.PI / 2)));
			for (int i = 0; i <= segments; i++)
			{
				double angle = startAngle + (endAngle - startAngle) * i / segments;
				path.LineTo(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
			}
		}

		/// <summary>The full tab silhouette; filled in the line colour it becomes the active tab's border.</summary>
		public static VertexStorage TabOuterPath(RectangleDouble bounds, double scale)
		{
			var r = Snap(bounds);
			return TopRoundedRect(r.Left, r.Bottom, r.Right, r.Top, Px(TabCornerRadius, scale));
		}

		/// <summary>The tab silhouette inset by one line width on the left, top and right (not the bottom).</summary>
		public static VertexStorage TabInnerPath(RectangleDouble bounds, double scale)
		{
			var r = Snap(bounds);
			var line = Px(LineWidth, scale);
			return TopRoundedRect(r.Left + line, r.Bottom, r.Right - line, r.Top - line, Math.Max(0, Px(TabCornerRadius, scale) - line));
		}

		/// <summary>Hover fill: the tab silhouette stopping one line width above the bottom, so the bar's line stays visible.</summary>
		public static VertexStorage TabHoverPath(RectangleDouble bounds, double scale)
		{
			var r = Snap(bounds);
			return TopRoundedRect(r.Left, r.Bottom + Px(LineWidth, scale), r.Right, r.Top, Px(TabCornerRadius, scale));
		}

		/// <summary>The accent bar along the top edge of the active tab, inset from both ends.</summary>
		public static RectangleDouble AccentBarRect(RectangleDouble bounds, double scale)
		{
			var r = Snap(bounds);
			var inset = Px(AccentInset, scale);
			return new RectangleDouble(r.Left + inset, r.Top - Px(AccentHeight, scale), r.Right - inset, r.Top);
		}

		/// <summary>The hairline along the bottom edge of the bar.</summary>
		public static RectangleDouble BarLineRect(RectangleDouble bounds, double scale)
		{
			var r = Snap(bounds);
			return new RectangleDouble(r.Left, r.Bottom, r.Right, r.Bottom + Px(LineWidth, scale));
		}

		/// <summary>The vertical rule between the brand button and the tabs, centred vertically in <paramref name="bounds"/>.</summary>
		public static RectangleDouble RuleRect(RectangleDouble bounds, double scale)
		{
			var r = Snap(bounds);
			var left = r.Left + Px(RuleSpace, scale);
			var height = Px(RuleHeight, scale);
			var bottom = Math.Round((r.Bottom + r.Top - height) / 2);
			return new RectangleDouble(left, bottom, left + Px(LineWidth, scale), bottom + height);
		}

		/// <summary>
		/// The stroked outline of a glyph centred on <paramref name="center"/>, using Lucide's proportions
		/// on a 24 unit grid (x: 6..18, plus: 5..19, chevron-down: 6,9 to 12,15 to 18,9).
		/// </summary>
		public static IVertexSource GlyphPath(ClubGlyph glyph, Vector2 center, double sizeUnits, double scale)
		{
			var size = sizeUnits * scale;
			var path = new VertexStorage();
			double stroke;

			switch (glyph)
			{
				case ClubGlyph.Plus:
					{
						// Horizontal and vertical strokes: use a whole pixel width centred so the edges are crisp
						stroke = Px(GlyphStroke, scale);
						var offset = (stroke % 2 == 1) ? 0.5 : 0;
						var cx = Math.Floor(center.X) + offset;
						var cy = Math.Floor(center.Y) + offset;
						var half = Math.Round(size * 7 / 24);
						path.MoveTo(cx - half, cy);
						path.LineTo(cx + half, cy);
						path.MoveTo(cx, cy - half);
						path.LineTo(cx, cy + half);
						break;
					}

				case ClubGlyph.ChevronDown:
					{
						stroke = GlyphStroke * scale;
						var halfWidth = size * 6 / 24;
						var halfHeight = size * 3 / 24;
						path.MoveTo(center.X - halfWidth, center.Y + halfHeight);
						path.LineTo(center.X, center.Y - halfHeight);
						path.LineTo(center.X + halfWidth, center.Y + halfHeight);
						break;
					}

				default:
					{
						stroke = GlyphStroke * scale;
						var half = size * 6 / 24;
						path.MoveTo(center.X - half, center.Y - half);
						path.LineTo(center.X + half, center.Y + half);
						path.MoveTo(center.X - half, center.Y + half);
						path.LineTo(center.X + half, center.Y - half);
						break;
					}
			}

			return new Stroke(path, stroke)
			{
				LineCap = LineCap.Round,
				LineJoin = LineJoin.Round
			};
		}

		// ---- Drawing ----

		/// <summary>Active tab: workspace coloured fill, line coloured border on left/top/right, accent bar on top.</summary>
		public static void DrawActiveTab(Graphics2D graphics2D, RectangleDouble bounds, double scale, Color fill, Color line, Color accent)
		{
			graphics2D.Render(TabOuterPath(bounds, scale), line);
			graphics2D.Render(TabInnerPath(bounds, scale), fill);
			graphics2D.FillRectangle(AccentBarRect(bounds, scale), accent);
		}

		/// <summary>An inactive tab under the pointer: a plain fill with the rounded top.</summary>
		public static void DrawHoverTab(Graphics2D graphics2D, RectangleDouble bounds, double scale, Color fill)
		{
			graphics2D.Render(TabHoverPath(bounds, scale), fill);
		}

		public static void DrawGlyph(Graphics2D graphics2D, ClubGlyph glyph, Vector2 center, double sizeUnits, double scale, Color color)
		{
			graphics2D.Render(GlyphPath(glyph, center, sizeUnits, scale), color);
		}
	}
}
