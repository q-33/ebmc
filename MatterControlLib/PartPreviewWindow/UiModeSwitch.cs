using System;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;
using MatterHackers.Localizations;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: the "Simple · Advanced" switch at the right of the top bar (frame 5 of
	/// tasks/design/ux-modes-mockups.html). A dark track holding two segments; the selected one is raised like the active
	/// club tab (raised fill, line border, accent underline) and its label uses the tab font. Clicking the other segment
	/// calls <see cref="UiModes.Set"/>, which rebuilds the window; when that is refused (a slice or send is running)
	/// nothing changes and the status bar says why.
	/// </summary>
	public class UiModeSwitch : FlowLayoutWidget
	{
		/// <summary>Track height, corner radius and inner padding (mockup: 26, 7, 2; 28 so each segment is a 24 unit target).</summary>
		private const double TrackHeight = 28;

		private const double TrackRadius = 7;

		private const double TrackPadding = 2;

		/// <summary>Segment corner radius, horizontal padding and the gap between the two (mockup: 5, 10, 2).</summary>
		private const double SegmentRadius = 5;

		private const double SegmentPaddingX = 10;

		private const double SegmentGap = 2;

		/// <summary>The accent underline under the selected label: height and inset from the segment's ends.</summary>
		private const double UnderlineHeight = 2;

		private const double UnderlineInset = 6;

		/// <summary>Label size (mockup 11.5 px at 1x; the club tabs use 11 pt).</summary>
		private const double LabelPointSize = 10.5;

		/// <summary>Space between the switch and the search button.</summary>
		public const double MarginRight = 8;

		private readonly ThemeConfig theme;

		public UiModeSwitch(ThemeConfig theme)
		{
			this.theme = theme;
			this.Name = "Ui Mode Switch";
			// Center AND Fit: assigning VAnchor.Center alone replaces FlowLayoutWidget's default Fit, which left the switch
			// 0 units tall, so its segments were clipped away and nothing showed in the top bar
			this.VAnchor = VAnchor.Center | VAnchor.Fit;
			this.HAnchor = HAnchor.Fit;
			this.Padding = new BorderDouble(TrackPadding);
			this.Margin = new BorderDouble(right: MarginRight);
			this.ToolTipText = "Simple shows the essentials. Advanced shows every tool.".Localize();

			var current = UiModes.Current;

			this.AddChild(new Segment(theme, UiMode.Simple, "Simple".Localize(), current == UiMode.Simple)
			{
				Margin = new BorderDouble(right: SegmentGap),
				ToolTipText = this.ToolTipText,
			});

			this.AddChild(new Segment(theme, UiMode.Advanced, "Advanced".Localize(), current == UiMode.Advanced)
			{
				ToolTipText = this.ToolTipText,
			});
		}

		public override void OnDraw(Graphics2D graphics2D)
		{
			var scale = GuiWidget.DeviceScale;
			var bounds = this.LocalBounds;

			// The track: darker than the tab bar, with a hairline in the club line colour
			var line = ClubTabStyle.Px(ClubTabStyle.LineWidth, scale);
			graphics2D.Render(new RoundedRect(bounds, TrackRadius * scale), ClubTabStyle.LineColor(theme));
			var inner = bounds;
			inner.Inflate(-line);
			graphics2D.Render(new RoundedRect(inner, Math.Max(0, TrackRadius * scale - line)), TrackColor(theme));

			base.OnDraw(graphics2D);
		}

		private static Color TrackColor(ThemeConfig theme) => theme.TabBarBackground.Blend(Color.Black, .45);

		private class Segment : GuiWidget
		{
			private readonly ThemeConfig theme;

			private readonly UiMode mode;

			private readonly bool selected;

			private readonly TextWidget label;

			public Segment(ThemeConfig theme, UiMode mode, string text, bool selected)
			{
				this.theme = theme;
				this.mode = mode;
				this.selected = selected;
				this.Name = text + " Mode Segment";
				this.HAnchor = HAnchor.Fit;
				this.VAnchor = VAnchor.Absolute;
				this.Height = (TrackHeight - 2 * TrackPadding) * GuiWidget.DeviceScale;
				this.Padding = new BorderDouble(SegmentPaddingX, 0);
				this.Cursor = selected ? Cursors.Default : Cursors.Hand;

				label = new TextWidget(text, pointSize: LabelPointSize, typeFace: ApplicationController.Instance.GetTypeFace(ClubTabStyle.LabelFontName))
				{
					VAnchor = VAnchor.Center,
					HAnchor = HAnchor.Center,
					Selectable = false,
					TextColor = selected ? theme.TextColor : ClubTabStyle.MutedTextColor(theme),
				};

				this.AddChild(label);
			}

			private bool IsHovered => this.UnderMouseState != UnderMouseState.NotUnderMouse;

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

			protected override void OnClick(MouseEventArgs mouseEvent)
			{
				base.OnClick(mouseEvent);

				if (!selected)
				{
					// Changing the mode rebuilds the main window (this widget included), so run it outside this click
					UiThread.RunOnIdle(() => UiModes.Set(mode));
				}
			}

			public override void OnDraw(Graphics2D graphics2D)
			{
				var scale = GuiWidget.DeviceScale;
				var bounds = this.LocalBounds;
				var radius = SegmentRadius * scale;

				if (selected)
				{
					// Raised like the active tab: line border, raised fill, accent underline under the label
					var line = ClubTabStyle.Px(ClubTabStyle.LineWidth, scale);
					graphics2D.Render(new RoundedRect(bounds, radius), ClubTabStyle.LineColor(theme));
					var inner = bounds;
					inner.Inflate(-line);
					graphics2D.Render(new RoundedRect(inner, Math.Max(0, radius - line)), theme.InactiveTabColor);

					var inset = ClubTabStyle.Px(UnderlineInset, scale);
					var height = ClubTabStyle.Px(UnderlineHeight, scale);
					graphics2D.FillRectangle(
						new RectangleDouble(Math.Round(bounds.Left + inset), Math.Round(bounds.Bottom + line), Math.Round(bounds.Right - inset), Math.Round(bounds.Bottom + line) + height),
						theme.PrimaryAccentColor);
				}
				else if (IsHovered)
				{
					graphics2D.Render(new RoundedRect(bounds, radius), ClubTabStyle.HoverFillColor(theme).WithAlpha(110));
				}

				label.TextColor = (selected || IsHovered) ? theme.TextColor : ClubTabStyle.MutedTextColor(theme);

				base.OnDraw(graphics2D);
			}
		}
	}
}
