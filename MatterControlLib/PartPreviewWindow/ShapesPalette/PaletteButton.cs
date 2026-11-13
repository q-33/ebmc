using System;
using System.Collections.Generic;
using System.IO;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;
using MatterHackers.ImageProcessing;
using MatterHackers.MatterControl.Library;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: loads the shapes palette glyphs (StaticData/Icons/Shapes, black on alpha, 4x masters).
	/// StaticData.LoadIcon first resamples the whole master up by the device scale (bilinear) and then halves it, which
	/// softens 2x; this loader box-halves the master straight to the device size, so 1x (80 -> 40 -> 20) and
	/// 2x (80 -> 40) are exact box reductions.
	/// </summary>
	public static class PaletteIcons
	{
		private static readonly Dictionary<(string, int), ImageBuffer> Cache = new Dictionary<(string, int), ImageBuffer>();

		public static ImageBuffer Load(string glyph, int logicalSize, Color color)
		{
			int deviceSize = Math.Max(1, (int)Math.Round(logicalSize * GuiWidget.DeviceScale));

			ImageBuffer scaled;
			lock (Cache)
			{
				if (!Cache.TryGetValue((glyph, deviceSize), out scaled))
				{
					var path = Path.Combine("Icons", "Shapes", glyph + ".png");
					if (!StaticData.Instance.FileExists(path))
					{
						// A missing glyph must not take the palette down; the button stays usable with its tooltip
						return new ImageBuffer(deviceSize, deviceSize);
					}

					var master = StaticData.Instance.LoadImage(path);
					master.SetRecieveBlender(new BlenderPreMultBGRA());
					scaled = master.Width == deviceSize ? master : master.CreateScaledImage(deviceSize, deviceSize);
					Cache[(glyph, deviceSize)] = scaled;
				}
			}

			var tinted = new ImageBuffer(scaled).GrayToColor(color);
			if (color.alpha < 255)
			{
				tinted = tinted.AjustAlpha(color.alpha / 255.0);
			}

			tinted.SetRecieveBlender(new BlenderPreMultBGRA());
			return tinted;
		}
	}

	/// <summary>
	/// EBMC Edition: a shapes palette button. Flat at rest, the theme's raised colour on hover, accent tint while pressed,
	/// and a 2 unit accent bar on its left edge while its flyout is open (the club tab style). The button spans the whole
	/// palette width so the bar can sit at the palette's edge; the visible 36 unit square (or row, with labels) is inset.
	/// Families with more members carry a corner mark (compact) or chevron (labels); clicking there, right-clicking or
	/// pressing and holding opens the flyout. Buttons that add something can be dragged onto the bed.
	/// </summary>
	public class PaletteButton : ThemedButton
	{
		public const double ButtonSize = 36;

		public const double Inset = 6;

		private const double CornerHit = 14;

		private const double ChevronHit = 26;

		private readonly bool hasFlyout;

		private readonly bool expanded;

		private readonly ImageWidget imageWidget;

		private readonly TextWidget labelWidget;

		private readonly ImageWidget chevronWidget;

		private Vector2 mouseDownAt;

		private bool mouseDownInFlyoutArea;

		private bool suppressClick;

		private bool leftDown;

		private int holdToken;

		public PaletteButton(ThemeConfig theme, string label, bool hasFlyout, bool expanded, double width)
			: base(theme)
		{
			this.hasFlyout = hasFlyout;
			this.expanded = expanded;

			var scale = DeviceScale;
			this.HAnchor = HAnchor.Absolute;
			this.VAnchor = VAnchor.Absolute;
			this.Width = width;
			this.Height = ButtonSize * scale;
			this.Margin = new BorderDouble(bottom: 2);
			this.Padding = 0;
			this.BackgroundColor = Color.Transparent;
			this.HoverColor = ClubTabStyle.HoverFillColor(theme);
			this.MouseDownColor = theme.PrimaryAccentColor.WithAlpha(41);
			this.BackgroundRadius = new RadiusCorners(7 * scale);
			this.Name = label + " Palette Button";

			var row = new FlowLayoutWidget()
			{
				HAnchor = expanded ? HAnchor.Stretch : HAnchor.Center | HAnchor.Fit,
				VAnchor = VAnchor.Center | VAnchor.Fit,
				Padding = expanded ? new BorderDouble(left: Inset + 8, right: Inset + 6) : 0,
				Selectable = false
			};
			this.AddChild(row);

			imageWidget = new ImageWidget(new ImageBuffer(1, 1), listenForImageChanged: false)
			{
				VAnchor = VAnchor.Center,
				Selectable = false
			};
			row.AddChild(imageWidget);

			if (expanded)
			{
				labelWidget = new TextWidget(label, pointSize: 10, textColor: theme.TextColor)
				{
					VAnchor = VAnchor.Center,
					Margin = new BorderDouble(left: 9),
					AutoExpandBoundsToText = true,
					Selectable = false
				};
				row.AddChild(labelWidget);
				row.AddChild(new HorizontalSpacer());

				if (hasFlyout)
				{
					chevronWidget = new ImageWidget(ClubChevron(theme), listenForImageChanged: false)
					{
						VAnchor = VAnchor.Center,
						Selectable = false
					};
					row.AddChild(chevronWidget);
				}
			}
		}

		/// <summary>Raised when the user asks for the flyout (corner mark, chevron, right click or press and hold).</summary>
		public event EventHandler FlyoutRequested;

		/// <summary>Raised for a plain click on the button (not on its corner mark).</summary>
		public event EventHandler Activated;

		/// <summary>True while this button's flyout is open: raised fill and accent bar.</summary>
		public bool FlyoutOpen { get; set; }

		/// <summary>True for the chosen side of the Solid/Hole switch: <see cref="SelectedFill"/> and the accent bar.</summary>
		public bool Selected { get; set; }

		/// <summary>Fill of a selected button; the raised (hover) colour when not set.</summary>
		public Color SelectedFill { get; set; }

		/// <summary>The items a drag from this button places on the bed; null when the button cannot be dragged.</summary>
		public Func<IEnumerable<ILibraryItem>> DragItems { get; set; }

		/// <summary>The 3D view a drag drops into.</summary>
		public View3DWidget View3DWidget { get; set; }

		public void SetGlyph(ImageBuffer glyph)
		{
			imageWidget.Image = glyph;
			imageWidget.Invalidate();
		}

		private static ImageBuffer ClubChevron(ThemeConfig theme)
		{
			// chevron-right, the same glyph the mockup uses beside expandable rows
			var size = (int)Math.Round(12 * DeviceScale);
			var image = new ImageBuffer(size, size);
			var graphics = image.NewGraphics2D();
			var s = size / 24.0;
			var path = new VertexStorage();
			path.MoveTo(9 * s, size - 6 * s);
			path.LineTo(15 * s, size - 12 * s);
			path.LineTo(9 * s, size - 18 * s);
			graphics.Render(new Stroke(path, 2 * s) { LineCap = LineCap.Round, LineJoin = LineJoin.Round }, ClubTabStyle.MutedTextColor(theme));
			return image;
		}

		private RectangleDouble VisibleBounds
		{
			get
			{
				var b = this.LocalBounds;
				var inset = Math.Round(Inset * DeviceScale);
				return new RectangleDouble(b.Left + inset, b.Bottom, b.Right - inset, b.Top);
			}
		}

		private bool InFlyoutArea(Vector2 position)
		{
			if (!hasFlyout)
			{
				return false;
			}

			var visible = VisibleBounds;
			if (expanded)
			{
				return position.X >= visible.Right - ChevronHit * DeviceScale;
			}

			var hit = CornerHit * DeviceScale;
			return position.X >= visible.Right - hit && position.Y <= visible.Bottom + hit;
		}

		public override void OnDrawBackground(Graphics2D graphics2D)
		{
			var visible = VisibleBounds;
			var fill = FlyoutOpen ? HoverColor : BackgroundColor;
			if (Selected && !ContainsFirstUnderMouseRecursive())
			{
				fill = SelectedFill.alpha > 0 ? SelectedFill : HoverColor;
			}

			if (fill.alpha > 0)
			{
				var rect = new RoundedRect(visible, 7 * DeviceScale);
				graphics2D.Render(rect, fill);
			}

			if (FlyoutOpen || Selected)
			{
				// accent bar at the palette's left edge, like the active tab's bar
				var bar = Math.Max(1, Math.Round(ClubTabStyle.AccentHeight * DeviceScale));
				var inset = Math.Round(8 * DeviceScale);
				graphics2D.FillRectangle(new RectangleDouble(0, visible.Bottom + inset, bar, visible.Top - inset), theme.PrimaryAccentColor);
			}
		}

		public override void OnDraw(Graphics2D graphics2D)
		{
			base.OnDraw(graphics2D);

			if (hasFlyout && !expanded)
			{
				// corner mark: a small right triangle in the bottom-right corner
				var visible = VisibleBounds;
				var size = 5 * DeviceScale;
				var gap = 3 * DeviceScale;
				var right = visible.Right - gap;
				var bottom = visible.Bottom + gap;
				var mark = new VertexStorage();
				mark.MoveTo(right, bottom);
				mark.LineTo(right, bottom + size);
				mark.LineTo(right - size, bottom);
				mark.ClosePolygon();

				var color = (FlyoutOpen || ContainsFirstUnderMouseRecursive()) && Enabled ? theme.TextColor : ClubTabStyle.MutedTextColor(theme);
				graphics2D.Render(mark, color);
			}
		}

		public override void OnMouseDown(MouseEventArgs mouseEvent)
		{
			// a click on the button while its flyout is open only closes the flyout (as DropButton does)
			suppressClick = FlyoutOpen;
			leftDown = mouseEvent.Button == MouseButtons.Left;
			mouseDownAt = mouseEvent.Position;
			mouseDownInFlyoutArea = InFlyoutArea(mouseEvent.Position);

			if (mouseEvent.Button == MouseButtons.Right && hasFlyout)
			{
				suppressClick = true;
				UiThread.RunOnIdle(() => FlyoutRequested?.Invoke(this, null));
			}
			else if (leftDown && hasFlyout && !mouseDownInFlyoutArea)
			{
				// press and hold opens the flyout too
				int token = ++holdToken;
				UiThread.RunOnIdle(
					() =>
					{
						if (token == holdToken
							&& leftDown
							&& !suppressClick
							&& !HasBeenClosed
							&& View3DWidget?.DragOperationActive != true)
						{
							suppressClick = true;
							FlyoutRequested?.Invoke(this, null);
						}
					},
					.5);
			}

			base.OnMouseDown(mouseEvent);
		}

		public override void OnMouseMove(MouseEventArgs mouseEvent)
		{
			if (leftDown
				&& MouseCaptured
				&& DragItems != null
				&& View3DWidget != null
				&& !mouseDownInFlyoutArea
				&& (mouseEvent.Position - mouseDownAt).Length > 6 * DeviceScale
				&& !this.LocalBounds.Contains(mouseEvent.Position))
			{
				// past the drag threshold and off the button: hand the drag to the 3D view, which starts it once the
				// pointer is over the bed (the same path the library list uses)
				holdToken++;
				View3DWidget.ExternalDragOver(this.TransformToScreenSpace(mouseEvent.Position), DragItems);
			}

			base.OnMouseMove(mouseEvent);
		}

		public override void OnMouseUp(MouseEventArgs mouseEvent)
		{
			leftDown = false;
			holdToken++;

			if (View3DWidget?.DragOperationActive == true)
			{
				suppressClick = true;
				var screenPosition = this.TransformToScreenSpace(mouseEvent.Position);
				var layer = View3DWidget.Object3DControlLayer;
				var bedScreenBounds = layer.TransformToScreenSpace(layer.LocalBounds);
				View3DWidget.FinishDrop(mouseUpInBounds: bedScreenBounds.Contains(screenPosition));
			}

			base.OnMouseUp(mouseEvent);
		}

		protected override void OnClick(MouseEventArgs mouseEvent)
		{
			if (mouseEvent.Button != MouseButtons.Left
				|| suppressClick)
			{
				suppressClick = false;
				return;
			}

			if (mouseDownInFlyoutArea || hasFlyout && Activated == null)
			{
				FlyoutRequested?.Invoke(this, null);
			}
			else
			{
				Activated?.Invoke(this, null);
			}

			base.OnClick(mouseEvent);
		}
	}
}
