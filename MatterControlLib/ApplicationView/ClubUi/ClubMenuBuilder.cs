using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.PrintQueue;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.ClubUi
{
	/// <summary>
	/// EBMC Edition (ClubDialogsAndMenus): what a menu description's entry stands for. Exactly one of the members is
	/// set: an existing NamedAction (built with the same enable rule and click as ThemeConfig.CreateMenuItems), or a
	/// callback that adds the item itself (sub-menus, rows the old code built by hand).
	/// </summary>
	public sealed class ClubMenuSource
	{
		public ClubMenuSource(NamedAction action, Func<string> disabledReason = null)
		{
			Action = action;
			DisabledReason = disabledReason;
		}

		public ClubMenuSource(Func<PopupMenu, string, GuiWidget> addItem)
		{
			AddItem = addItem;
		}

		public NamedAction Action { get; }

		/// <summary>Adds the item to the menu (given the menu and the entry's title) and returns it.</summary>
		public Func<PopupMenu, string, GuiWidget> AddItem { get; }

		/// <summary>The tooltip of the item when it is disabled for a reason other than the maintainer lock.</summary>
		public Func<string> DisabledReason { get; }
	}

	/// <summary>
	/// EBMC Edition (ClubDialogsAndMenus): builds a menu from a <see cref="ClubMenuSpec"/> in the club menu style
	/// (frame 8): 28 px rows, a 16 px icon column, right-aligned muted shortcut hints, separators that render, small
	/// caps section headers, danger items last in the danger colour, disabled items visible with the reason as tooltip.
	/// </summary>
	public static class ClubMenuBuilder
	{
		public const double ItemHeight = 28;

		/// <summary>Icon column: 8 px inset + 16 px icon + 9 px gap.</summary>
		public const double Gutter = 33;

		private static readonly ConditionalWeakTable<LibraryAction, object> DangerLibraryActions = new ConditionalWeakTable<LibraryAction, object>();

		public static void Build(PopupMenu popupMenu, ClubMenuSpec spec, IDictionary<string, ClubMenuSource> sources)
		{
			StyleMenu(popupMenu);

			foreach (var row in ClubMenuLayout.Arrange(spec, id => sources.TryGetValue(id, out var source) && source != null))
			{
				switch (row.Kind)
				{
					case ClubMenuRowKind.Separator:
						AddSeparator(popupMenu);
						break;

					case ClubMenuRowKind.Header:
						AddHeader(popupMenu, row.Text.Localize());
						break;

					default:
						AddEntry(popupMenu, row.Entry, sources[row.Entry.Id]);
						break;
				}
			}
		}

		/// <summary>The panel: 5 px padding on the menu theme's raised background.</summary>
		public static void StyleMenu(PopupMenu popupMenu)
		{
			popupMenu.Padding = new BorderDouble(5);
		}

		public static void AddSeparator(PopupMenu popupMenu)
		{
			var line = popupMenu.CreateSeparator();
			line.Margin = new BorderDouble(6, 4);
			line.BackgroundColor = ClubStyle.Line(popupMenu.Theme);
		}

		public static void AddHeader(PopupMenu popupMenu, string text)
		{
			popupMenu.AddChild(new TextWidget(text.ToUpperInvariant(), pointSize: 8, textColor: ClubStyle.Muted(popupMenu.Theme), bold: true)
			{
				Margin = new BorderDouble(8, 3, 8, 6),
				Selectable = false,
			});
		}

		private static string TitleFor(ClubMenuEntry entry, string fallback)
		{
			var title = entry.Title != null ? entry.Title.Localize() : fallback;
			return entry.Title != null ? ClubMenuText.WithEllipsis(title, entry.OpensDialog) : title;
		}

		private static void AddEntry(PopupMenu popupMenu, ClubMenuEntry entry, ClubMenuSource source)
		{
			var theme = popupMenu.Theme;
			var iconColor = entry.Danger ? ClubStyle.DangerText : ClubStyle.Muted(theme);

			if (source.AddItem != null)
			{
				var widget = source.AddItem(popupMenu, TitleFor(entry, entry.Id));
				if (widget is PopupMenu.MenuItem custom)
				{
					ApplyMetrics(custom);
					if (entry.Icon != null && custom.Image == null)
					{
						custom.Image = ClubStyle.MenuIcon(entry.Icon, iconColor);
					}
				}

				return;
			}

			var action = source.Action;
			string title = TitleFor(entry, action.Title);

			if (action is NamedActionGroup)
			{
				// the Edit row (Cut, Copy, Paste buttons): the stock builder, unchanged, then the row height
				theme.CreateMenuItems(popupMenu, new[] { action });
				if (popupMenu.Children.LastOrDefault() is PopupMenu.MenuItem groupItem)
				{
					groupItem.MinimumSize = new Vector2(groupItem.MinimumSize.X, ItemHeight * GuiWidget.DeviceScale);
				}

				return;
			}

			PopupMenu.MenuItem menuItem;
			if (action is NamedBoolAction boolAction)
			{
				// same calls as ThemeConfigExtensions.CreateMenuItems
				menuItem = popupMenu.CreateBoolMenuItem(title, boolAction.GetIsActive, boolAction.SetIsActive);
				ApplyMetrics(menuItem);
			}
			else
			{
				ImageBuffer icon = entry.Icon != null ? ClubStyle.MenuIcon(entry.Icon, iconColor) : action.Icon;
				string shortcut = entry.Shortcut != null ? KeyHints.ForThisComputer(entry.Shortcut) : action.Shortcut;
				menuItem = CreateItem(popupMenu, title, icon, shortcut, entry.Danger);
			}

			menuItem.Name = $"{action.Title} Menu Item";

			// the enable rule and click of ThemeConfigExtensions.CreateMenuItems
			menuItem.Enabled = action.Action != null && action.IsEnabled?.Invoke() != false;

			menuItem.ClearRemovedFlag();

			if (menuItem.Enabled)
			{
				menuItem.Click += (s, e) =>
				{
					action.Action();
				};
			}
			else
			{
				SetDisabled(menuItem, theme);
				menuItem.ToolTipText = action.IsBlockedByLock?.Invoke() == true
					? Maintainer.LockedText
					: source.DisabledReason?.Invoke() ?? "";
			}
		}

		/// <summary>
		/// A plain club menu row: icon column, title, right-aligned muted shortcut. Clicking closes the menu, like
		/// PopupMenu.CreateMenuItem; the caller adds the action.
		/// </summary>
		public static PopupMenu.MenuItem CreateItem(PopupMenu popupMenu, string title, ImageBuffer icon, string shortcut, bool danger)
		{
			var theme = popupMenu.Theme;
			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.MaxFitOrStretch,
				VAnchor = VAnchor.Center | VAnchor.Fit,
				Selectable = false,
			};

			row.AddChild(new TextWidget(title, pointSize: theme.DefaultFontSize, textColor: danger ? ClubStyle.DangerText : theme.TextColor)
			{
				VAnchor = VAnchor.Center,
				Selectable = false,
				Name = "Club Menu Title",
			});

			row.AddChild(new HorizontalSpacer());

			if (!string.IsNullOrEmpty(shortcut))
			{
				row.AddChild(new TextWidget(shortcut, pointSize: theme.DefaultFontSize - 1, textColor: ClubStyle.Muted(theme))
				{
					VAnchor = VAnchor.Center,
					Margin = new BorderDouble(left: 18),
					Selectable = false,
					Name = "Club Menu Shortcut",
				});
			}

			var menuItem = popupMenu.CreateMenuItem(row, title, icon);
			menuItem.Click += (s, e) => popupMenu.Unfocus();
			ApplyMetrics(menuItem);

			return menuItem;
		}

		/// <summary>Row height, icon column, padding and the rounded hover of the club style for any menu item.</summary>
		public static void ApplyMetrics(PopupMenu.MenuItem menuItem)
		{
			menuItem.MinimumSize = new Vector2(menuItem.MinimumSize.X, ItemHeight * GuiWidget.DeviceScale);
			menuItem.GutterWidth = (Gutter - 1) * GuiWidget.DeviceScale;
			menuItem.Padding = new BorderDouble(Gutter, 0, 12, 0);
			menuItem.BackgroundRadius = new RadiusCorners(6 * GuiWidget.DeviceScale);

			// the stock items pad their text with PopupMenu.MenuPadding (40, 8, 20, 8); the row pads it here instead
			foreach (var text in menuItem.Children.OfType<TextWidget>())
			{
				text.Padding = new BorderDouble(0, 0, 8, 0);
			}
		}

		/// <summary>Dims a disabled club row (its title and shortcut) to the disabled colour; it stays visible.</summary>
		public static void SetDisabled(PopupMenu.MenuItem menuItem, ThemeConfig theme)
		{
			foreach (var text in menuItem.Descendants<TextWidget>())
			{
				text.TextColor = ClubStyle.Disabled(theme);
			}
		}

		/// <summary>Restyles a sub-menu item PopupMenu.CreateSubMenu just added (it returns nothing).</summary>
		public static GuiWidget LastItem(PopupMenu popupMenu)
		{
			return popupMenu.Children.LastOrDefault();
		}

		// ---- Library menus (LibraryAction lists) ----

		/// <summary>Marks a library action as a danger item (shown last-styled, in the danger colour).</summary>
		public static void MarkDanger(LibraryAction action)
		{
			DangerLibraryActions.AddOrUpdate(action, null);
		}

		public static bool IsDanger(LibraryAction action) => DangerLibraryActions.TryGetValue(action, out _);

		/// <summary>
		/// A library menu in the club style. Separators render (the stock menus filtered them out by scope), collapse
		/// around items that are left out, and never start or end the menu. With <paramref name="hideDisabled"/> the
		/// disabled items are left out, as the stock item menu does; otherwise they show dimmed.
		/// </summary>
		public static void BuildLibraryMenu(
			PopupMenu popupMenu,
			IReadOnlyList<LibraryAction> actions,
			Func<LibraryAction, bool> inScope,
			Func<LibraryAction, bool> isEnabled,
			bool hideDisabled,
			Action<LibraryAction> invoke)
		{
			StyleMenu(popupMenu);

			var rows = actions.Where(a => a is MenuSeparator || inScope(a)).ToList();
			var enabled = rows.Select(a => !(a is MenuSeparator) && isEnabled(a)).ToList();
			var keep = ClubMenuLayout.CollapseSeparators(
				rows.Select(a => a is MenuSeparator).ToList(),
				rows.Select((a, i) => !(a is MenuSeparator) && (!hideDisabled || enabled[i])).ToList());

			for (int i = 0; i < rows.Count; i++)
			{
				if (!keep[i])
				{
					continue;
				}

				var action = rows[i];
				if (action is MenuSeparator)
				{
					AddSeparator(popupMenu);
					continue;
				}

				bool danger = IsDanger(action);
				var menuItem = CreateItem(popupMenu, action.Title, action.Icon, null, danger);
				menuItem.Name = $"{action.Title} Menu Item";
				menuItem.Enabled = enabled[i];
				if (enabled[i])
				{
					menuItem.Click += (s, e) => invoke(action);
				}
				else
				{
					SetDisabled(menuItem, popupMenu.Theme);
				}
			}
		}
	}
}
