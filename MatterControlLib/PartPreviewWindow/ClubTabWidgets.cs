using System;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: the tab close button of the club tab strip. A circular hit area with an "x"
	/// drawn in code: muted at rest, a line coloured circle with a full colour "x" on hover, no red.
	/// It always takes up its space so labels do not move; <paramref name="isShown"/> decides
	/// whether it is drawn (the owning tab shows it while active or hovered).
	/// </summary>
	public class ClubTabCloseButton : GuiWidget
	{
		private readonly ThemeConfig theme;
		private readonly Func<bool> isShown;

		public ClubTabCloseButton(ThemeConfig theme, Func<bool> isShown)
		{
			this.theme = theme;
			this.isShown = isShown;

			var size = ClubTabStyle.CloseButtonSize * DeviceScale;
			this.HAnchor = HAnchor.Absolute;
			this.VAnchor = VAnchor.Absolute;
			this.Width = size;
			this.Height = size;
			this.Cursor = Cursors.Hand;
		}

		public override void OnMouseEnterBounds(MouseEventArgs mouseEvent)
		{
			Invalidate();
			base.OnMouseEnterBounds(mouseEvent);
		}

		public override void OnMouseLeaveBounds(MouseEventArgs mouseEvent)
		{
			Invalidate();
			base.OnMouseLeaveBounds(mouseEvent);
		}

		public override void OnDraw(Graphics2D graphics2D)
		{
			if (isShown == null || isShown())
			{
				var scale = DeviceScale;
				var center = LocalBounds.Center;
				var hovered = UnderMouseState != UnderMouseState.NotUnderMouse;

				if (hovered)
				{
					graphics2D.Circle(center, Math.Min(Width, Height) / 2, ClubTabStyle.LineColor(theme));
				}

				ClubTabStyle.DrawGlyph(
					graphics2D,
					ClubGlyph.Close,
					center,
					ClubTabStyle.CloseGlyphSize,
					scale,
					hovered ? theme.TextColor : ClubTabStyle.MutedTextColor(theme));
			}

			base.OnDraw(graphics2D);
		}
	}

	/// <summary>
	/// EBMC Edition: a square, rounded, borderless button showing a glyph drawn in code (used for "+").
	/// Muted glyph at rest; hover fill and full colour glyph under the pointer.
	/// </summary>
	public class ClubGlyphButton : ThemedButton
	{
		private readonly ClubGlyph glyph;
		private readonly double glyphSize;

		public ClubGlyphButton(ClubGlyph glyph, double glyphSize, ThemeConfig theme)
			: base(theme)
		{
			this.glyph = glyph;
			this.glyphSize = glyphSize;

			this.HAnchor = HAnchor.Absolute;
			this.VAnchor = VAnchor.Absolute | VAnchor.Center;
			this.Margin = 0;

			ClubTabStyle.ApplySquareButtonStyle(this, theme);
		}

		public override void OnDraw(Graphics2D graphics2D)
		{
			base.OnDraw(graphics2D);

			var hovered = Enabled && ContainsFirstUnderMouseRecursive();
			ClubTabStyle.DrawGlyph(
				graphics2D,
				glyph,
				LocalBounds.Center,
				glyphSize,
				DeviceScale,
				hovered ? theme.TextColor : ClubTabStyle.MutedTextColor(theme));
		}
	}

	/// <summary>
	/// EBMC Edition: a small non-interactive glyph (the brand button's chevron).
	/// </summary>
	public class ClubGlyphWidget : GuiWidget
	{
		private readonly ClubGlyph glyph;
		private readonly double glyphSize;
		private readonly Color color;

		public ClubGlyphWidget(ClubGlyph glyph, double glyphSize, Color color)
		{
			this.glyph = glyph;
			this.glyphSize = glyphSize;
			this.color = color;
			this.Selectable = false;
			this.HAnchor = HAnchor.Absolute;
			this.VAnchor = VAnchor.Absolute;
			this.Width = glyphSize * DeviceScale;
			this.Height = glyphSize * DeviceScale;
		}

		public override void OnDraw(Graphics2D graphics2D)
		{
			ClubTabStyle.DrawGlyph(graphics2D, glyph, LocalBounds.Center, glyphSize, DeviceScale, color);
			base.OnDraw(graphics2D);
		}
	}
}
