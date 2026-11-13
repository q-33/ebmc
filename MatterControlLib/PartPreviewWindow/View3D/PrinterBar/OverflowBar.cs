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

		/// <summary>
		/// EBMC Edition: items that never overflow (the printer row's Print/Send popups, Slice and Connect, which make no
		/// sense as plain menu rows). Their room is reserved first, so the other items overflow before them.
		/// </summary>
		private readonly HashSet<GuiWidget> neverOverflow = new HashSet<GuiWidget>();

		/// <summary>
		/// EBMC Edition: true for the item that is currently selected (the active tab of a tab strip). It is kept visible
		/// like a <see cref="NeverOverflow"/> item, and marked as checked if it ever has to go in the overflow menu.
		/// </summary>
		public Func<GuiWidget, bool> IsActiveItem { get; set; }

		/// <summary>
		/// EBMC Edition: action-area children hidden by their owner (for example the library breadcrumb while searching),
		/// as opposed to hidden here because they did not fit. The fit leaves them hidden and the menu leaves them out.
		/// </summary>
		private readonly HashSet<GuiWidget> hiddenByOwner = new HashSet<GuiWidget>();

		/// <summary>Children whose size and visibility changes are watched, with what removes the handlers again.</summary>
		private readonly Dictionary<GuiWidget, Action> watched = new Dictionary<GuiWidget, Action>();

		/// <summary>Loop guard: true while the fit itself changes visibility.</summary>
		private bool fitting;

		/// <summary>True while a re-fit is queued for the next idle.</summary>
		private bool fitQueued;

		/// <summary>
		/// EBMC Edition: keeps <paramref name="widget"/> (a child of the action area) out of the overflow menu; other items
		/// overflow first.
		/// </summary>
		public T NeverOverflow<T>(T widget)
			where T : GuiWidget
		{
			neverOverflow.Add(widget);
			return widget;
		}

		/// <summary>
		/// EBMC Edition: runs the fit again on the next idle (once, however many times it is asked for), for changes the
		/// bar's own bounds do not show: a child getting wider, a child shown or hidden by its owner, the active tab.
		/// </summary>
		public void RequestFit()
		{
			if (fitQueued)
			{
				return;
			}

			fitQueued = true;
			UiThread.RunOnIdle(() =>
			{
				fitQueued = false;
				FitItems();
			});
		}

		/// <summary>
		/// EBMC Edition: the owner of an action-area child shows or hides it (for example the library breadcrumb while
		/// searching). Use this rather than setting Visible: an item the fit had already hidden raises no VisibleChanged
		/// when its owner hides it too, so only this records the owner's intent reliably. Shown items still overflow when
		/// they do not fit.
		/// </summary>
		public void SetOwnerVisible(GuiWidget widget, bool visible)
		{
			if (visible)
			{
				hiddenByOwner.Remove(widget);
			}
			else
			{
				hiddenByOwner.Add(widget);
			}

			// the owner's decision is recorded above; the VisibleChanged this causes must not be read as one again
			var wasFitting = fitting;
			fitting = true;
			try
			{
				widget.Visible = visible;
			}
			finally
			{
				fitting = wasFitting;
			}

			RequestFit();
		}

		private void Watch(GuiWidget widget)
		{
			if (watched.ContainsKey(widget))
			{
				return;
			}

			// A child that is already hidden when it is first seen was hidden by its owner
			if (!widget.Visible)
			{
				hiddenByOwner.Add(widget);
			}

			double lastWidth = widget.Width;
			void BoundsChangedHandler(object s, EventArgs e)
			{
				// only a change of size matters (a label widening); moves caused by the fit do not
				if (!fitting
					&& widget.Width != lastWidth)
				{
					lastWidth = widget.Width;
					RequestFit();
				}
			}

			void VisibleChangedHandler(object s, EventArgs e)
			{
				if (fitting)
				{
					return;
				}

				// Not the fit: the owner showed or hid it (owners that do not use SetOwnerVisible)
				if (widget.Visible)
				{
					hiddenByOwner.Remove(widget);
				}
				else
				{
					hiddenByOwner.Add(widget);
				}

				RequestFit();
			}

			void ClosedHandler(object s, EventArgs e) => Forget(widget);

			widget.BoundsChanged += BoundsChangedHandler;
			widget.VisibleChanged += VisibleChangedHandler;
			widget.Closed += ClosedHandler;
			watched[widget] = () =>
			{
				widget.BoundsChanged -= BoundsChangedHandler;
				widget.VisibleChanged -= VisibleChangedHandler;
				widget.Closed -= ClosedHandler;
			};
		}

		/// <summary>Stops watching a child (closed, or removed from the action area without being closed).</summary>
		private void Forget(GuiWidget widget)
		{
			if (watched.TryGetValue(widget, out var unhook))
			{
				unhook();
				watched.Remove(widget);
			}

			hiddenByOwner.Remove(widget);
			neverOverflow.Remove(widget);
		}

		public override void OnClosed(EventArgs e)
		{
			foreach (var widget in watched.Keys.ToList())
			{
				Forget(widget);
			}

			base.OnClosed(e);
		}

		private bool KeepVisible(GuiWidget widget) => neverOverflow.Contains(widget) || IsActiveItem?.Invoke(widget) == true;

		public override void OnBoundsChanged(EventArgs e)
		{
			base.OnBoundsChanged(e);

			FitItems();
		}

		private void FitItems()
		{
			if (this.RightAnchorItem == null
				|| fitting)
			{
				return;
			}

			fitting = true;
			try
			{
				Fit();
			}
			finally
			{
				fitting = false;
			}
		}

		private void Fit()
		{
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

			var children = actionArea.Children.Where(c => !ignoredTypes.Contains(c.GetType())).ToList();

			// children removed from the action area without being closed: drop their handlers (no leak)
			foreach (var gone in watched.Keys.Where(w => !actionArea.Children.Contains(w)).ToList())
			{
				Forget(gone);
			}

			foreach (var child in children)
			{
				Watch(child);
			}

			// Items their owner has hidden stay hidden and take no room
			var items = children.Where(c => !hiddenByOwner.Contains(c)).ToList();

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

			// Items that must stay (the active tab, the print and slice buttons) are reserved first
			double keptWidth = leading.Concat(trailing).Where(KeepVisible).Sum(ItemWidth);
			double room = available - keptWidth;

			// Then the right-aligned group; if even that does not fit, its last items overflow
			double trailingWidth = 0;
			bool withinLimits = true;
			foreach (var widget in trailing)
			{
				if (KeepVisible(widget))
				{
					widget.Visible = true;
					continue;
				}

				var width = ItemWidth(widget);
				withinLimits &= trailingWidth + width <= room;
				widget.Visible = withinLimits;
				if (withinLimits)
				{
					trailingWidth += width;
				}
			}

			// Then the leading items, left to right; once one does not fit, it and all after it overflow (the kept items
			// stay, so it is the last items that are not kept that go to the menu)
			double accumulatedX = 0;
			room -= trailingWidth;
			withinLimits = true;
			foreach (var widget in leading)
			{
				if (KeepVisible(widget))
				{
					widget.Visible = true;
					continue;
				}

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
				// EBMC Edition: only what the fit hid; never what the owner hid on purpose
				foreach (var widget in this.ActionArea.Children.Where(c => !c.Visible && !hiddenByOwner.Contains(c) && !ignoredInMenuTypes.Contains(c.GetType())))
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
					var menuText = string.IsNullOrEmpty(widget.ToolTipText) ? widget.Text : widget.ToolTipText;
					if (IsActiveItem?.Invoke(widget) == true)
					{
						// the selected tab, if even it had to overflow: shown checked
						menuItem = popupMenu.CreateBoolMenuItem(menuText, () => true, (value) => widget.InvokeClick());
						menuItem.Enabled = widget.Enabled;
						continue;
					}

					menuItem = popupMenu.CreateMenuItem(
						menuText,
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