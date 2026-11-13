/*
Copyright (c) 2022, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.ImageProcessing;
using MatterHackers.MatterControl.Library.Widgets;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	public class OverflowBar : Toolbar
	{
		protected static HashSet<Type> ignoredTypes = new HashSet<Type> { typeof(HorizontalLine), typeof(TextEditWithInlineCancel) };
		protected static HashSet<Type> ignoredInMenuTypes = new HashSet<Type> { typeof(VerticalLine), typeof(HorizontalLine), typeof(TextEditWithInlineCancel), typeof(HorizontalSpacer) };

		public OverflowBar(ThemeConfig theme, string text = null)
			: this(null, theme, text)
		{ }

		public OverflowBar(ImageBuffer icon, ThemeConfig theme, string text = null)
			: base(theme.TabbarPadding)
		{
			this.theme = theme;

			if (icon == null)
			{
				if (text != null)
				{
					this.OverflowButton = new PopupMenuButton(text, theme)
					{
						AlignToRightEdge = true,
						DrawArrow = true,
					};
				}
				else
				{
					this.OverflowButton = new OverflowMenuButton(this, theme)
					{
						AlignToRightEdge = true,
					};
				}
			}
			else
			{
				this.OverflowButton = new OverflowMenuButton(this, icon, theme)
				{
					AlignToRightEdge = true,
				};
			}
			AddMenuItemsFunction(OverflowButton);

			// We want to set right margin to overflow button width but width is already scaled - need to inflate value by amount needed to hit width when rescaled in Margin setter
			this.ActionArea.Margin = new BorderDouble(right: Math.Ceiling(this.OverflowButton.Width / GuiWidget.DeviceScale));
			this.SetRightAnchorItem(this.OverflowButton);
		}

		private ThemeConfig theme;

		public PopupMenuButton OverflowButton { get; }

		public Action<PopupMenu> ExtendOverflowMenu { get; set; }

		protected virtual void OnExtendPopupMenu(PopupMenu popupMenu)
		{
			this.ExtendOverflowMenu(popupMenu);
		}

		/// <summary>
		/// EBMC Edition: when true (the default), the items after the first stretched spacer (the right-aligned group, such
		/// as the search button of the slice settings tabs) keep their room and the items before the spacer overflow first.
		/// When false, items overflow strictly from the end of the row, as in stock MatterControl.
		/// </summary>
		public bool ReserveTrailingItems { get; set; } = true;

		public override void OnBoundsChanged(EventArgs e)
		{
			base.OnBoundsChanged(e);

			if (this.RightAnchorItem == null)
			{
				return;
			}

			// EBMC Edition: an item is shown only when all of it fits (stock showed it when half of it fitted, so the last
			// tab was clipped and never offered in the overflow menu), measured against the room the action area really
			// has: the bar less its padding, the action area's margins and padding, and the overflow button with its margins
			var actionArea = this.ActionArea;
			double reservedRight = Math.Max(
				actionArea.DeviceMarginAndBorder.Right,
				this.RightAnchorItem.Width + this.RightAnchorItem.DeviceMarginAndBorder.Width);
			double available = this.Width
				- this.DevicePadding.Width
				- actionArea.DeviceMarginAndBorder.Left
				- reservedRight
				- actionArea.DevicePadding.Width;

			var items = actionArea.Children.Where(c => !ignoredTypes.Contains(c.GetType())).ToList();

			// Stretched widgets take what is left, so only their minimum size counts
			double ItemWidth(GuiWidget widget)
			{
				var width = widget.HAnchor.HasFlag(HAnchor.Stretch) ? widget.MinimumSize.X : widget.Width;
				return width + widget.DeviceMarginAndBorder.Width;
			}

			var leading = items;
			var trailing = new List<GuiWidget>();
			if (this.ReserveTrailingItems)
			{
				int spacerIndex = items.FindIndex(c => c is HorizontalSpacer);
				if (spacerIndex >= 0)
				{
					// the spacer itself stays visible so the trailing group stays right-aligned
					items[spacerIndex].Visible = true;
					leading = items.Take(spacerIndex).ToList();
					trailing = items.Skip(spacerIndex + 1).ToList();
				}
			}

			// Reserve the right-aligned group first; if even that does not fit, its last items overflow
			double trailingWidth = 0;
			bool withinLimits = true;
			foreach (var widget in trailing)
			{
				var width = ItemWidth(widget);
				withinLimits &= trailingWidth + width <= available;
				widget.Visible = withinLimits;
				if (withinLimits)
				{
					trailingWidth += width;
				}
			}

			// Then the leading items, left to right; once one does not fit, it and all after it overflow
			double accumulatedX = 0;
			double room = available - trailingWidth;
			withinLimits = true;
			foreach (var widget in leading)
			{
				var width = ItemWidth(widget);
				withinLimits &= accumulatedX + width <= room;
				widget.Visible = withinLimits;
				accumulatedX += width;
			}
		}

		private void AddMenuItemsFunction(PopupMenuButton menuButton)
		{
			menuButton.DynamicPopupContent = () =>
			{
				var menuTheme = ApplicationController.Instance.MenuTheme;
				var popupMenu = new PopupMenu(menuTheme);

				// Perform overflow
				bool hasOverflowItems = false;
				foreach (var widget in this.ActionArea.Children.Where(c => !c.Visible && !ignoredInMenuTypes.Contains(c.GetType())))
				{
					if (widget is ToolbarSeparator)
					{
						popupMenu.CreateSeparator();
						continue;
					}

					hasOverflowItems = true;

					PopupMenu.MenuItem menuItem;

					var iconButton = widget as ThemedIconButton;

					var iconImage = iconButton?.IconImage;

					// EBMC Edition: tabs have no tooltip, so fall back to their label when the tooltip is empty as well as null
					menuItem = popupMenu.CreateMenuItem(
						string.IsNullOrEmpty(widget.ToolTipText) ? widget.Text : widget.ToolTipText,
						iconImage);

					menuItem.Enabled = widget.Enabled;

					menuItem.Click += (s, e) =>
					{
						widget.InvokeClick();
					};
				}

				if (hasOverflowItems)
				{
					popupMenu.CreateSeparator();
				}

				// Extend menu with non-overflow/standard items
				this.OnExtendPopupMenu(popupMenu);

				return popupMenu;
			};
		}

		/// <summary>
		/// A PopupMenuButton with the standard overflow icon
		/// </summary>
		public class OverflowMenuButton : PopupMenuButton
		{
			public OverflowMenuButton(ThemeConfig theme)
				: base(CreateOverflowIcon(theme), theme)
			{
			}

			public OverflowMenuButton(string text, ThemeConfig theme)
				: base(text, CreateOverflowIcon(theme), theme)
			{
			}

			public OverflowMenuButton(OverflowBar overflowBar, ThemeConfig theme)
				: this(overflowBar, CreateOverflowIcon(theme), theme)
			{
			}

			public OverflowMenuButton(OverflowBar overflowBar, ImageBuffer icon, ThemeConfig theme)
				: base(icon, theme)
            {
            }

            private static ImageBuffer CreateOverflowIcon(ThemeConfig theme)
			{
				return StaticData.Instance.LoadIcon(Path.Combine("ViewTransformControls", "overflow.png"), 32, 32).GrayToColor(theme.TextColor);
			}
		}
	}
}