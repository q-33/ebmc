using System;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: a shapes palette flyout: a titled list of a family's members (glyph, name, size) with, for resizable
	/// shapes, the size chips. Opens to the right of its palette button with SystemWindow.ShowPopup and closes when a
	/// member is chosen or when it loses focus.
	/// </summary>
	public class PaletteFlyout : FlowLayoutWidget
	{
		private readonly ThemeConfig menuTheme;

		private readonly PaletteFamily family;

		private readonly Func<PaletteMember, bool> isEnabled;

		private readonly Func<PaletteMember, string> disabledHelp;

		private readonly FlowLayoutWidget rows;

		private readonly FlowLayoutWidget chips;

		public PaletteFlyout(
			ThemeConfig menuTheme,
			PaletteFamily family,
			Func<PaletteMember, bool> isAvailable,
			Func<PaletteMember, bool> isEnabled,
			Func<PaletteMember, string> disabledHelp,
			bool holeMode,
			string hint)
			: base(FlowDirection.TopToBottom)
		{
			this.menuTheme = menuTheme;
			this.family = family;
			this.isEnabled = isEnabled;
			this.disabledHelp = disabledHelp;
			this.IsAvailable = isAvailable;
			this.HoleMode = holeMode;

			var scale = GuiWidget.DeviceScale;
			this.HAnchor = HAnchor.Absolute;
			this.VAnchor = VAnchor.Fit;
			this.Width = 236 * scale;
			this.Padding = new BorderDouble(6);
			this.BackgroundColor = menuTheme.BackgroundColor;
			this.Name = family.Label + " Palette Flyout";

			// header: family name and, for shapes that can be holes, the current Solid/Hole mode
			var header = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				Padding = new BorderDouble(6, 6, 6, 4),
			};
			header.AddChild(new TextWidget(family.Label.Localize().ToUpper(), pointSize: 8, textColor: ClubTabStyle.MutedTextColor(menuTheme), bold: true)
			{
				VAnchor = VAnchor.Center
			});
			header.AddChild(new HorizontalSpacer());
			if (family.CanBeHole)
			{
				header.AddChild(new TextWidget(holeMode ? "Hole".Localize() : "Solid".Localize(), pointSize: 8, textColor: holeMode ? menuTheme.PrimaryAccentColor : menuTheme.TextColor)
				{
					VAnchor = VAnchor.Center
				});
			}

			this.AddChild(header);

			rows = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch
			};
			this.AddChild(rows);

			if (family.HasSizeChips)
			{
				chips = new FlowLayoutWidget()
				{
					HAnchor = HAnchor.Stretch,
					Margin = new BorderDouble(top: 5),
					Padding = new BorderDouble(6, 3, 6, 7),
					Border = new BorderDouble(top: 1),
					BorderColor = ClubTabStyle.LineColor(menuTheme),
				};
				this.AddChild(chips);
				AddSizeChips();
			}

			if (!string.IsNullOrEmpty(hint))
			{
				var hintWidget = this.AddChild(new TextWidget(hint, pointSize: 7.5, textColor: ClubTabStyle.MutedTextColor(menuTheme))
				{
					Margin = new BorderDouble(6, 2),
					AutoExpandBoundsToText = true,
				});
				contentWidth = Math.Max(contentWidth, hintWidget.Width + hintWidget.DeviceMarginAndBorder.Width);
			}

			Rebuild();
		}

		/// <summary>Raised with the member the user picked; the flyout closes itself afterwards.</summary>
		public event EventHandler<PaletteMember> MemberChosen;

		/// <summary>True for members the app has (DEBUG-only parts are missing from release builds).</summary>
		public Func<PaletteMember, bool> IsAvailable { get; }

		public bool HoleMode { get; }

		/// <summary>Widest content seen so far (rows, hint) in device pixels, without the flyout's own padding; the flyout only grows.</summary>
		private double contentWidth;

		/// <summary>Space kept between a row's name and its size.</summary>
		private const double ColumnGap = 16;

		private void Rebuild()
		{
			var scale = GuiWidget.DeviceScale;
			var factor = PaletteCatalog.ScaleFor(PaletteSession.SizeChip(family.Id));
			var current = PaletteSession.CurrentMember(family);

			rows.CloseChildren();
			foreach (var member in family.Members.Where(m => IsAvailable(m)))
			{
				var enabled = isEnabled(member);
				var tintHole = HoleMode && family.CanBeHole;
				var glyphColor = !enabled ? menuTheme.DisabledColor : tintHole ? menuTheme.PrimaryAccentColor : menuTheme.TextColor;

				var row = new ThemedButton(menuTheme)
				{
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Absolute,
					Height = 32 * scale,
					Margin = 0,
					Padding = new BorderDouble(7, 0),
					BackgroundColor = Color.Transparent,
					HoverColor = menuTheme.PrimaryAccentColor.WithAlpha(41),
					MouseDownColor = menuTheme.PrimaryAccentColor.WithAlpha(70),
					BackgroundRadius = new RadiusCorners(6 * scale),
					Enabled = enabled,
					Name = member.Title + " Palette Row",
				};

				var line = new FlowLayoutWidget()
				{
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Center | VAnchor.Fit,
					Selectable = false
				};
				row.AddChild(line);

				line.AddChild(new ImageWidget(PaletteIcons.Load(member.Glyph, PaletteCatalog.GlyphSize, glyphColor), listenForImageChanged: false)
				{
					VAnchor = VAnchor.Center,
					Selectable = false
				});

				var name = member.Title.Localize() + (member == current && family.Members.Count > 1 ? " •" : "");
				var nameWidget = line.AddChild(new TextWidget(name, pointSize: 9, textColor: enabled ? menuTheme.TextColor : menuTheme.DisabledColor)
				{
					VAnchor = VAnchor.Center,
					Margin = new BorderDouble(left: 9),
					Selectable = false
				});
				line.AddChild(new HorizontalSpacer());

				var rowWidth = row.DevicePadding.Width + PaletteCatalog.GlyphSize * scale + nameWidget.Width + nameWidget.DeviceMarginAndBorder.Width;

				var size = member.Scalable ? member.SizeText(factor) : member.SizeText();
				if (!string.IsNullOrEmpty(size))
				{
					var sizeWidget = line.AddChild(new TextWidget(member.Scalable ? size : size.Localize(), pointSize: 8, textColor: enabled ? ClubTabStyle.MutedTextColor(menuTheme) : menuTheme.DisabledColor)
					{
						VAnchor = VAnchor.Center,
						Margin = new BorderDouble(left: ColumnGap),
						Selectable = false
					});
					rowWidth += sizeWidget.Width + sizeWidget.DeviceMarginAndBorder.Width;
				}

				contentWidth = Math.Max(contentWidth, rowWidth);

				row.ToolTipText = member.Title.Localize();
				if (!enabled)
				{
					var help = disabledHelp(member);
					if (!string.IsNullOrEmpty(help))
					{
						row.ToolTipText += "\n\n" + help;
					}
				}

				row.Click += (s, e) => UiThread.RunOnIdle(() =>
				{
					MemberChosen?.Invoke(this, member);
					this.Unfocus();
				});

				rows.AddChild(row);
			}

			// size the flyout to its widest row or hint, never narrower than the mockup's 236
			// all in device pixels: text widths, device padding and margins
			this.Width = Math.Max(this.Width, Math.Ceiling(contentWidth + this.DevicePadding.Width + 2 * scale));
		}

		/// <summary>
		/// The 10 / 20 / 40 mm chips. Built once and restyled in place, so the focused chip is never closed (closing it
		/// would move focus out of the popup and close the flyout).
		/// </summary>
		private void AddSizeChips()
		{
			var scale = GuiWidget.DeviceScale;
			chips.AddChild(new TextWidget("Size".Localize(), pointSize: 8, textColor: ClubTabStyle.MutedTextColor(menuTheme))
			{
				VAnchor = VAnchor.Center,
				Margin = new BorderDouble(right: 5)
			});

			var chipButtons = new System.Collections.Generic.List<(int size, ThemedButton button, TextWidget text)>();

			void Restyle()
			{
				var selected = PaletteSession.SizeChip(family.Id);
				foreach (var (size, button, text) in chipButtons)
				{
					var on = size == selected;
					button.BackgroundColor = on ? menuTheme.PrimaryAccentColor.WithAlpha(41) : Color.Transparent;
					button.BorderColor = on ? menuTheme.PrimaryAccentColor : ClubTabStyle.LineColor(menuTheme);
					text.TextColor = on ? menuTheme.PrimaryAccentColor : menuTheme.TextColor;
					button.Invalidate();
				}
			}

			foreach (var size in PaletteCatalog.SizeChips)
			{
				var chip = new ThemedButton(menuTheme)
				{
					HAnchor = HAnchor.Fit,
					VAnchor = VAnchor.Fit | VAnchor.Center,
					Margin = new BorderDouble(right: 5),
					Padding = new BorderDouble(6, 1),
					HoverColor = ClubTabStyle.HoverFillColor(menuTheme),
					MouseDownColor = menuTheme.PrimaryAccentColor.WithAlpha(70),
					BackgroundRadius = new RadiusCorners(5 * scale),
					BackgroundOutlineWidth = 1,
					ToolTipText = "Size of the next shape you add from this list".Localize(),
					Name = size + " mm Size Chip",
				};
				var text = new TextWidget(size.ToString(), pointSize: 8, textColor: menuTheme.TextColor)
				{
					Selectable = false
				};
				chip.AddChild(text);
				chip.Click += (s, e) => UiThread.RunOnIdle(() =>
				{
					PaletteSession.SetSizeChip(family.Id, size);
					Restyle();
					Rebuild();
				});
				chipButtons.Add((size, chip, text));
				chips.AddChild(chip);
			}

			chips.AddChild(new TextWidget("mm", pointSize: 8, textColor: ClubTabStyle.MutedTextColor(menuTheme))
			{
				VAnchor = VAnchor.Center
			});

			Restyle();
		}
	}
}
