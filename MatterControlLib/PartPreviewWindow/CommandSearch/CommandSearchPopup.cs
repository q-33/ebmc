using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.ImageProcessing;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: one runnable command search row: the pure <see cref="CommandEntry"/> plus how to run it and its icon.
	/// </summary>
	public sealed class CommandSearchItem
	{
		public CommandSearchItem(CommandEntry entry, Action run, Func<ThemeConfig, ImageBuffer> icon)
		{
			Entry = entry;
			Run = run;
			Icon = icon;
		}

		public CommandEntry Entry { get; }

		public Action Run { get; }

		public Func<ThemeConfig, ImageBuffer> Icon { get; }
	}

	/// <summary>
	/// EBMC Edition: where command search finds its rows for a tab (OemSettings.EnableAdvancedTools): the scene operations
	/// the toolbar and Modify menu offer for this tab type and mode, the palette's shapes, solids and starters, and a
	/// whitelist of workspace actions (CommandSearchRules.WorkspaceWhitelist). Nothing here slices, sends or prints, and
	/// CommandSearchRules drops anything named like that as a second guard.
	/// </summary>
	public static class CommandSearchSources
	{
		public static List<CommandSearchItem> Build(View3DWidget view3D)
		{
			var items = new List<CommandSearchItem>();
			if (view3D?.sceneContext == null)
			{
				return items;
			}

			AddOperations(view3D, items);
			AddShapes(view3D, items);
			AddWorkspaceActions(view3D, items);

			// the safety net: never a printing action
			return items.Where(i => CommandSearchRules.IsAllowed(i.Entry)).ToList();
		}

		private static bool Safe(Func<bool> check)
		{
			try
			{
				return check();
			}
			catch
			{
				// some rules assume a selection; treat a rule that throws as "no"
				return false;
			}
		}

		private static void AddOperations(View3DWidget view3D, List<CommandSearchItem> items)
		{
			var sceneContext = view3D.sceneContext;
			bool printerTab = sceneContext.Printer != null;
			bool trim = printerTab && OemSettings.Instance.TrimPrinterTabToolbar;
			bool simple = UiModes.IsSimple;

			void Add(SceneOperation operation, string where)
			{
				if (operation == null
					|| operation is SceneSelectionSeparator
					|| operation.Action == null
					|| string.IsNullOrEmpty(operation.Title)
					|| (trim && !operation.ShowOnPrinterTabs)
					|| Safe(() => operation.ShowInModifyMenu?.Invoke(sceneContext) == false))
				{
					return;
				}

				bool enabled = Safe(() => operation.IsEnabled?.Invoke(sceneContext) == true);
				var entry = new CommandEntry(CommandKind.Operation, "op:" + operation.Id, operation.Id, operation.Title, where)
				{
					HelpText = CommandSearchRules.PlainHelp(operation.HelpText),
					Enabled = enabled,
					DisabledReason = enabled ? null : CommandSearchRules.PlainHelp(operation.HelpText),
					Shortcut = OperationShortcut(operation.Id),
				};

				var op = operation;
				items.Add(new CommandSearchItem(
					entry,
					() =>
					{
						// IsEnabled exactly as the toolbar: re-checked when it runs
						if (Safe(() => op.IsEnabled?.Invoke(sceneContext) == true))
						{
							op.Action(sceneContext);
						}
					},
					op.Icon));
			}

			var modify = "Modify".Localize();
			foreach (var operation in SceneOperations.All)
			{
				if (operation is OperationGroup group)
				{
					if ((trim && !group.ShowOnPrinterTabs)
						|| !CommandSearchRules.SearchGroup(printerTab, group.Visible, group.ShowOnPrinterTabs))
					{
						continue;
					}

					var operations = simple
						? SceneOperations.SimpleGroupedOperations(group, printerTab)
						: group.Operations;

					// on a design tab a group hidden on the toolbar (Tools options) is still offered, as in the Modify menu;
					// on a printer tab only the groups its toolbar shows (CommandSearchRules.SearchGroup)
					foreach (var child in operations)
					{
						Add(child, group.Title);
					}
				}
				else if (!simple || SceneOperations.ShowInSimple(operation))
				{
					Add(operation, modify);
				}
			}
		}

		private static string OperationShortcut(string id)
		{
			switch (id)
			{
				case "Duplicate":
					return AdvancedTools.IsEnabled ? KeyHints.ForThisComputer("Ctrl + D") : null;

				case "Remove":
					return "Delete".Localize();

				default:
					return null;
			}
		}

		private static void AddShapes(View3DWidget view3D, List<CommandSearchItem> items)
		{
			var sceneContext = view3D.sceneContext;
			var tab = sceneContext.Printer != null ? PaletteTab.Printer : PaletteTab.Design;
			var seen = new HashSet<string>();

			var sections = PaletteCatalog.For(tab, DesignTools.Solid.SolidModeling.IsEnabled, UiModes.Current, DesignTools.ClubShapes.ClubShapesFeature.IsEnabled);
			foreach (var section in sections)
			{
				foreach (var family in section.Families)
				{
					foreach (var member in family.Members)
					{
						if (member.Source != PaletteSource.Primitives
							&& member.Source != PaletteSource.Starters
							&& member.Source != PaletteSource.CalibrationParts)
						{
							// operations are listed from the registry; palette commands (Add model, Supports) are not
							continue;
						}

						if (!seen.Add(member.Source + ":" + member.Name)
							|| ShapesPalette.ResolveItem(member) == null)
						{
							continue;
						}

						string where = member.Source == PaletteSource.Starters ? "Starters".Localize()
							: section.Label == "Solids" ? "Solids".Localize()
							: "Shapes".Localize();

						var entry = new CommandEntry(CommandKind.Shape, "shape:" + member.Name, member.Name, member.Title.Localize(), where)
						{
							HelpText = member.Hint?.Localize() ?? member.SizeText(),
							IconKey = member.Glyph,
						};

						var chosen = member;
						items.Add(new CommandSearchItem(
							entry,
							() => InsertShape(view3D, chosen),
							theme => PaletteIcons.Load(chosen.Glyph, 16, theme.TextColor)));
					}
				}
			}
		}

		/// <summary>Adds a palette member the way a click on its palette button does, else as the classic rail adds it.</summary>
		private static void InsertShape(View3DWidget view3D, PaletteMember member)
		{
			var sceneContext = view3D.sceneContext;
			var palette = view3D.Parents<DesignTabPage>().FirstOrDefault()?.Descendants<ShapesPalette>().FirstOrDefault()
				?? AppContext.RootSystemWindow?.Descendants<ShapesPalette>().FirstOrDefault(p => p.ActuallyVisibleOnScreen());

			if (palette != null
				&& palette.RunFromSearch(sceneContext, member))
			{
				return;
			}

			if (ShapesPalette.ResolveItem(member) is Library.ILibraryItem item)
			{
				sceneContext.AddToPlate(new[] { item });
			}
		}

		private static void AddWorkspaceActions(View3DWidget view3D, List<CommandSearchItem> items)
		{
			var sceneContext = view3D.sceneContext;
			var scene = sceneContext.Scene;
			Dictionary<string, NamedAction> workspace = null;
			try
			{
				workspace = ApplicationController.Instance.GetWorkspaceActions(view3D);
			}
			catch
			{
			}

			var file = "File".Localize();
			var edit = "Edit".Localize();
			var view = "View".Localize();
			var app = "App".Localize();

			void Add(string id, string title, string where, Action run, Func<bool> enabled = null, string shortcut = null, string help = null, Func<ThemeConfig, ImageBuffer> icon = null)
			{
				if (!CommandSearchRules.WorkspaceWhitelist.Contains(id))
				{
					return;
				}

				bool isEnabled = enabled == null || Safe(enabled);
				items.Add(new CommandSearchItem(
					new CommandEntry(CommandKind.Workspace, "ws:" + id, id, title, where)
					{
						Enabled = isEnabled,
						Shortcut = shortcut,
						HelpText = help,
						DisabledReason = isEnabled ? null : help,
					},
					() =>
					{
						if (enabled == null || Safe(enabled))
						{
							run();
						}
					},
					icon));
			}

			void AddNamed(string id, string where, string shortcut = null, Func<bool> extraCondition = null)
			{
				if (workspace != null
					&& workspace.TryGetValue(id, out var action)
					&& action?.Action != null
					&& (extraCondition == null || extraCondition()))
				{
					var icon = action.Icon;
					Add(
						id,
						action.Title,
						where,
						action.Action,
						() => action.IsEnabled?.Invoke() != false,
						shortcut ?? (string.IsNullOrEmpty(action.Shortcut) ? null : action.Shortcut),
						icon: icon == null ? (Func<ThemeConfig, ImageBuffer>)null : (theme => icon));
				}
			}

			AddNamed("Save", file);
			AddNamed("SaveAs", file);

			// no Export: its dialog can export G-code (a slice) whenever a printer is attached, and on a design tab
			// the export call attaches the only active printer itself

			Add("Undo", "Undo".Localize(), edit, () => scene.Undo(), () => scene.UndoBuffer.UndoCount > 0, KeyHints.ForThisComputer("Ctrl + Z"));
			Add("Redo", "Redo".Localize(), edit, () => scene.Redo(), () => scene.UndoBuffer.RedoCount > 0, KeyHints.ForThisComputer("Ctrl + Y"));
			Add("SelectAll", "Select All".Localize(), edit, () => view3D.SelectAll(), () => scene.Children.Any(), KeyHints.ForThisComputer("Ctrl + A"));

			Add("NewDesign", "New design tab".Localize(), file, () => UiThread.RunOnIdle(() => ApplicationController.Instance.MainView?.CreateNewDesignTab(true)));

			Add(
				"OpenFile",
				"Open file…".Localize(),
				file,
				() => ApplicationController.OpenFileWithSystemDialog(fileNames =>
				{
					if (fileNames != null && fileNames.Any())
					{
						ApplicationController.Instance.MainView.OpenFile(fileNames.FirstOrDefault());
					}
				}),
				help: "STL, 3MF, OBJ, STEP and design files".Localize());

			if (UiModes.SwitchEnabled)
			{
				var other = UiModes.IsSimple ? UiMode.Advanced : UiMode.Simple;
				Add(
					"SwitchMode",
					other == UiMode.Advanced ? "Switch to Advanced".Localize() : "Switch to Simple".Localize(),
					view,
					() => UiModes.Set(other),
					() => UiModes.BusyReason() == null,
					help: "Simple shows fewer tools; Advanced shows every tool".Localize());
			}

			if (AdvancedTools.InAdvancedMode)
			{
				Add("Shortcuts", "Keyboard shortcuts".Localize(), app, () => ShortcutsPage.Show(), shortcut: "?");
			}

			if (Maintainer.LockEnabled)
			{
				if (Maintainer.IsUnlocked)
				{
					Add("MaintainerLock", "Lock maintainer settings".Localize(), app, Maintainer.Lock);
				}
				else
				{
					Add(
						"MaintainerLock",
						Maintainer.HasPin ? "Unlock maintainer settings".Localize() : "Set maintainer PIN".Localize(),
						app,
						() => DialogWindow.Show(new MaintainerPinPage(Maintainer.HasPin ? MaintainerPinMode.Unlock : MaintainerPinMode.SetPin, null)));
				}
			}
		}
	}

	/// <summary>
	/// EBMC Edition: the command search popup ("Find a tool"): a text field and up to eight results, centred in the
	/// window. Up and Down choose, Enter runs, Esc closes, a click runs. Disabled rows are dimmed with the reason and do
	/// not run. Recently used commands (this session) come first while the field is empty.
	/// </summary>
	public class CommandSearchPopup : FlowLayoutWidget
	{
		private static List<string> recentIds = new List<string>();

		private static CommandSearchPopup openPopup;

		private readonly ThemeConfig theme;

		private readonly List<CommandSearchItem> items;

		private readonly Dictionary<string, CommandSearchItem> itemsById;

		private readonly FlowLayoutWidget resultsColumn;

		private readonly ThemedTextEditWidget searchField;

		private readonly TextWidget emptyText;

		private readonly View3DWidget view3D;

		private IReadOnlyList<CommandEntry> shown = new CommandEntry[0];

		private int selectedIndex;

		private CommandSearchPopup(View3DWidget view3D, ThemeConfig theme)
			: base(FlowDirection.TopToBottom)
		{
			this.view3D = view3D;
			this.theme = theme;
			this.Name = "Command Search";
			// Absolute before Width: a FlowLayoutWidget starts as HAnchor.Fit, and a Width set while it fits is laid out
			// back to its (empty) content, 0 wide, so the popup was invisible (the earlier order: Width, then Absolute)
			this.HAnchor = HAnchor.Absolute;
			this.VAnchor = VAnchor.Fit;
			this.Width = 480 * DeviceScale;
			this.BackgroundColor = theme.BackgroundColor;
			this.Padding = new BorderDouble(8);

			items = CommandSearchSources.Build(view3D);
			itemsById = items.GroupBy(i => i.Entry.Id).ToDictionary(g => g.Key, g => g.First());

			searchField = new ThemedTextEditWidget("", theme, messageWhenEmptyAndNotSelected: "Find a tool".Localize())
			{
				HAnchor = HAnchor.Stretch,
				Name = "Command Search Field",
			};
			this.AddChild(searchField);

			resultsColumn = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(top: 6),
			};
			this.AddChild(resultsColumn);

			emptyText = new TextWidget("No tool matches. Try another word.".Localize(), pointSize: theme.DefaultFontSize, textColor: theme.TextColor.WithAlpha(150))
			{
				Margin = new BorderDouble(6, 8),
				Visible = false,
			};
			this.AddChild(emptyText);

			this.AddChild(new TextWidget(KeyHints.ForThisComputer("Up and Down choose · Enter runs · Esc closes".Localize()), pointSize: theme.FontSize8, textColor: theme.TextColor.WithAlpha(140))
			{
				Margin = new BorderDouble(6, 2, 6, 6),
			});

			var internalEdit = searchField.ActualTextEditWidget.InternalTextEditWidget;
			internalEdit.KeyDown += SearchField_KeyDown;
			searchField.ActualTextEditWidget.TextChanged += (s, e) =>
			{
				selectedIndex = 0;
				Refresh();
			};

			Refresh();
		}

		/// <summary>
		/// Opens the popup for the visible 3D view (or closes it when it is already open). Nothing happens when the tools
		/// are off or no 3D view is showing.
		/// </summary>
		public static void Toggle()
		{
			if (openPopup != null
				&& !openPopup.HasBeenClosed)
			{
				openPopup.Close();
				openPopup = null;
				return;
			}

			if (!AdvancedTools.IsEnabled)
			{
				return;
			}

			var systemWindow = AppContext.RootSystemWindow;
			var view3D = systemWindow?.Descendants<View3DWidget>().FirstOrDefault(v => v.ActuallyVisibleOnScreen());
			var anchor = ApplicationController.Instance.MainView;
			if (view3D == null
				|| anchor == null
				|| anchor.Parent == null)
			{
				return;
			}

			CommandSearchPopup popup;
			try
			{
				popup = new CommandSearchPopup(view3D, AppContext.MenuTheme);
			}
			catch (Exception ex)
			{
				// a key handler must not swallow this silently: say so in the status bar and the log
				System.Diagnostics.Trace.WriteLine("Find a tool could not open: " + ex);
				ApplicationController.Instance.SetUiHint("Find a tool could not open.".Localize());
				return;
			}

			openPopup = popup;
			popup.Closed += (s, e) =>
			{
				if (openPopup == popup)
				{
					openPopup = null;
				}
			};

			systemWindow.ShowPopup(
				AppContext.MenuTheme,
				new MatePoint(anchor),
				new MatePoint(popup),
				default(RectangleDouble),
				1,
				PlaceNearTop);

			UiThread.RunOnIdle(() =>
			{
				if (!popup.HasBeenClosed)
				{
					popup.searchField.Focus();
				}
			});
		}

		/// <summary>Centred horizontally over the window, its top a fifth of the way down.</summary>
		private static void PlaceNearTop(SystemWindow systemWindow, MatePoint anchor, MatePoint popup, RectangleDouble altBounds)
		{
			var widget = popup.Widget;
			var x = (systemWindow.Width - widget.Width) / 2;
			var top = systemWindow.Height - systemWindow.Height / 5;
			var y = top - widget.Height;
			widget.Position = new Vector2(Math.Round(Math.Max(0, x)), Math.Round(Math.Max(0, y)));
		}

		public override void OnBoundsChanged(EventArgs e)
		{
			base.OnBoundsChanged(e);

			// keep the top edge where it was as the list grows or shrinks
			if (this.Parent is SystemWindow systemWindow)
			{
				var top = systemWindow.Height - systemWindow.Height / 5;
				var y = Math.Round(Math.Max(0, top - this.Height));
				if (Math.Abs(this.Position.Y - y) > 0.5)
				{
					this.Position = new Vector2(this.Position.X, y);
				}
			}
		}

		private void SearchField_KeyDown(object sender, KeyEventArgs keyEvent)
		{
			switch (keyEvent.KeyCode)
			{
				case Keys.Down:
					Move(1);
					keyEvent.Handled = true;
					keyEvent.SuppressKeyPress = true;
					break;

				case Keys.Up:
					Move(-1);
					keyEvent.Handled = true;
					keyEvent.SuppressKeyPress = true;
					break;

				case Keys.Enter:
					keyEvent.Handled = true;
					keyEvent.SuppressKeyPress = true;
					RunSelected();
					break;

				case Keys.Escape:
					keyEvent.Handled = true;
					keyEvent.SuppressKeyPress = true;

					// close, then give the keys back to the 3D view (as running a command does)
					var target = view3D;
					UiThread.RunOnIdle(() =>
					{
						this.Close();
						if (target?.HasBeenClosed == false)
						{
							target.Object3DControlLayer.Focus();
						}
					});
					break;
			}
		}

		private void Move(int delta)
		{
			if (shown.Count == 0)
			{
				return;
			}

			selectedIndex = (selectedIndex + delta + shown.Count) % shown.Count;
			UpdateHighlight();
		}

		private void RunSelected()
		{
			if (selectedIndex >= 0 && selectedIndex < shown.Count)
			{
				RunEntry(shown[selectedIndex]);
			}
		}

		private void RunEntry(CommandEntry entry)
		{
			if (entry == null
				|| !entry.Enabled
				|| !itemsById.TryGetValue(entry.Id, out var item))
			{
				return;
			}

			recentIds = CommandSearchRules.AddRecent(recentIds, entry.Id);

			var target = view3D;
			this.Close();

			// run after the popup has gone, with the 3D view focused (as a toolbar click leaves it)
			UiThread.RunOnIdle(() =>
			{
				if (target?.HasBeenClosed == false)
				{
					target.Object3DControlLayer.Focus();
				}

				item.Run();
			});
		}

		private void Refresh()
		{
			shown = CommandSearchRules.Rank(searchField.Text, items.Select(i => i.Entry), recentIds);
			if (selectedIndex >= shown.Count)
			{
				selectedIndex = 0;
			}

			resultsColumn.CloseChildren();
			for (int i = 0; i < shown.Count; i++)
			{
				resultsColumn.AddChild(CreateRow(shown[i], i));
			}

			emptyText.Visible = shown.Count == 0;
			UpdateHighlight();
		}

		private void UpdateHighlight()
		{
			int index = 0;
			foreach (var row in resultsColumn.Children)
			{
				row.BackgroundColor = index == selectedIndex ? theme.AccentMimimalOverlay : Color.Transparent;
				index++;
			}
		}

		private GuiWidget CreateRow(CommandEntry entry, int index)
		{
			var textColor = entry.Enabled ? theme.TextColor : theme.TextColor.WithAlpha(110);
			var mutedColor = theme.TextColor.WithAlpha(entry.Enabled ? 150 : 90);

			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Padding = new BorderDouble(6, 5),
				Cursor = entry.Enabled ? Cursors.Hand : Cursors.Default,
				Name = entry.Title + " Command Row",
				ToolTipText = entry.Enabled ? entry.HelpText ?? "" : entry.DisabledReason ?? "",
			};

			ImageBuffer icon = null;
			if (itemsById.TryGetValue(entry.Id, out var item)
				&& item.Icon != null)
			{
				try
				{
					icon = item.Icon(theme);
				}
				catch
				{
				}
			}

			var iconSize = (int)(16 * DeviceScale);
			if (icon == null)
			{
				icon = new ImageBuffer(iconSize, iconSize);
			}
			else if (!entry.Enabled)
			{
				icon = new ImageBuffer(icon).AjustAlpha(0.45);
			}

			row.AddChild(new ImageWidget(icon)
			{
				VAnchor = VAnchor.Center,
				Margin = new BorderDouble(right: 8),
				Selectable = false,
			});

			var texts = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Center | VAnchor.Fit,
				Selectable = false,
			};
			row.AddChild(texts);

			texts.AddChild(new TextWidget(entry.Title, pointSize: theme.DefaultFontSize, textColor: textColor)
			{
				Selectable = false,
			});

			var detail = entry.Where;
			if (!entry.Enabled && !string.IsNullOrEmpty(entry.DisabledReason))
			{
				detail = entry.Where + " · " + entry.DisabledReason;
			}

			if (!string.IsNullOrEmpty(detail))
			{
				texts.AddChild(new TextWidget(detail, pointSize: theme.FontSize8, textColor: mutedColor)
				{
					Selectable = false,
				});
			}

			if (!string.IsNullOrEmpty(entry.Shortcut))
			{
				row.AddChild(new TextWidget(entry.Shortcut, pointSize: theme.FontSize8, textColor: mutedColor)
				{
					VAnchor = VAnchor.Center,
					Margin = new BorderDouble(left: 8),
					Selectable = false,
				});
			}

			row.MouseEnterBounds += (s, e) =>
			{
				selectedIndex = index;
				UpdateHighlight();
			};

			row.Click += (s, e) => RunEntry(entry);

			return row;
		}
	}
}
