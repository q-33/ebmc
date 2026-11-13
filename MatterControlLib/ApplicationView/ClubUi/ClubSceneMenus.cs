using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterControlLib.Library.OpenInto;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.DesignTools.Operations;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.Library.Export;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.RenderOpenGl;

namespace MatterHackers.MatterControl.ClubUi
{
	/// <summary>
	/// EBMC Edition (ClubDialogsAndMenus): the 3D view's right-click menus in the club style. Every item runs code that
	/// already ran from the stock menus, the toolbar or the properties panel, with the same enable rule; nothing here
	/// slices, sends or prints (Print keeps its own action and place).
	/// </summary>
	public static class ClubSceneMenus
	{
		/// <summary>The right-click menu on a part (stock: ApplicationController.GetActionMenuForSceneItem).</summary>
		public static void BuildPartMenu(PopupMenu popupMenu, View3DWidget view3DWidget, Dictionary<string, NamedAction> workspaceActions, NamedAction saveAs, NamedAction export, bool editable)
		{
			var sceneContext = view3DWidget.sceneContext;
			var scene = sceneContext.Scene;
			var selectedItem = scene.SelectedItem;
			var menuTheme = popupMenu.Theme;
			bool designTab = sceneContext.Printer == null;

			var sources = new Dictionary<string, ClubMenuSource>();

			// scene operations offered where the toolbar offers them (printer-tab trim, Simple mode)
			void AddOperation(string id, string operationId)
			{
				var operation = FindOperation(operationId);
				if (operation == null
					|| (!designTab && SettingsManagement.OemSettings.Instance.TrimPrinterTabToolbar && !operation.ShowOnPrinterTabs)
					|| (UiModes.IsSimple && !SceneOperations.ShowInSimple(operation)))
				{
					return;
				}

				sources[id] = new ClubMenuSource(
					new NamedAction()
					{
						Title = operation.Title,
						Action = () => UiThread.RunOnIdle(() => operation.Action?.Invoke(sceneContext)),
						IsEnabled = () => operation.IsEnabled?.Invoke(sceneContext) == true,
					},
					() => operation.HelpText ?? "");
			}

			if (editable)
			{
				AddOperation(ClubMenus.Duplicate, "Duplicate");

				// Mirror Copy: Advanced mode on design tabs, as in the Transform group (OemSettings.EnableAdvancedTools)
				if (designTab
					&& AdvancedTools.InAdvancedMode)
				{
					AddOperation(ClubMenus.MirrorCopy, "MirrorCopy");
				}

				// the properties panel's Solid / Hole switch, on design tabs for parts that show it
				if (designTab
					&& !(selectedItem.GetType().GetCustomAttributes(typeof(HideMeterialAndColor), true).FirstOrDefault() is HideMeterialAndColor))
				{
					sources[ClubMenus.HoleOrSolid] = HoleOrSolid(sceneContext, selectedItem);
				}

				AddOperation(ClubMenus.LayFlat, "Lay Flat");
			}

			if (workspaceActions["Edit"] is NamedActionGroup edit)
			{
				foreach (var action in edit.Group)
				{
					switch (action.ID)
					{
						case "Cut":
							sources[ClubMenus.Cut] = new ClubMenuSource(action);
							break;
						case "Copy":
							sources[ClubMenus.Copy] = new ClubMenuSource(action);
							break;
						case "Paste":
							sources[ClubMenus.Paste] = new ClubMenuSource(action);
							break;
					}
				}
			}

			sources[ClubMenus.PasteInto] = new ClubMenuSource(workspaceActions["PasteInto"]);

			if (editable)
			{
				// the stock Rename item (ApplicationController.AddActionMenuItems)
				sources[ClubMenus.Rename] = new ClubMenuSource(new NamedAction()
				{
					Title = "Rename".Localize(),
					Action = () =>
					{
						var current = scene.SelectedItem;
						if (current != null)
						{
							current.ShowRenameDialog(scene.UndoBuffer);
						}
					},
				});

				sources[ClubMenus.MoreTools] = new ClubMenuSource((menu, title) =>
				{
					menu.CreateSubMenu(title, menuTheme, (modifyMenu) => SceneOperations.AddModifyItems(modifyMenu, menuTheme, sceneContext));
					return ClubMenuBuilder.LastItem(menu);
				});

				if (OpenIntoExecutable.FoundInstalledExecutable)
				{
					sources[ClubMenus.OpenWith] = new ClubMenuSource((menu, title) =>
					{
						menu.CreateSubMenu(title, menuTheme, (modifyMenu) => OpenIntoExecutable.AddOption(modifyMenu, menuTheme, sceneContext));
						return ClubMenuBuilder.LastItem(menu);
					});
				}
			}

			sources[ClubMenus.SaveAs] = new ClubMenuSource(saveAs);
			sources[ClubMenus.ExportPart] = new ClubMenuSource(export);
			sources[ClubMenus.Delete] = new ClubMenuSource(workspaceActions["Delete"]);

			ClubMenuBuilder.Build(popupMenu, ClubMenus.PartContext, sources);
		}

		/// <summary>
		/// After the part's own items (IRightClickMenuProvider) and the reorder items were appended, moves the danger
		/// group (its separator and items) back to the end so danger stays last.
		/// </summary>
		public static void KeepDangerLast(PopupMenu popupMenu, int childCountAfterBuild)
		{
			if (popupMenu.Children.Count <= childCountAfterBuild)
			{
				return;
			}

			// the danger group is the last row of the club build: one separator then the Delete item
			var deleteItem = popupMenu.Children[childCountAfterBuild - 1];
			var separator = childCountAfterBuild >= 2 ? popupMenu.Children[childCountAfterBuild - 2] : null;
			popupMenu.RemoveChild(deleteItem);
			if (separator is HorizontalLine)
			{
				popupMenu.RemoveChild(separator);
			}

			if (!(popupMenu.Children.LastOrDefault() is HorizontalLine))
			{
				ClubMenuBuilder.AddSeparator(popupMenu);
			}

			deleteItem.ClearRemovedFlag();
			popupMenu.AddChild(deleteItem);
		}

		/// <summary>The right-click menu on the empty bed (stock: View3DWidget.ShowBedContextMenu).</summary>
		public static void BuildBedMenu(PopupMenu popupMenu, View3DWidget view3DWidget, Dictionary<string, NamedAction> workspaceActions, NamedAction paste)
		{
			var sceneContext = view3DWidget.sceneContext;

			var sources = new Dictionary<string, ClubMenuSource>()
			{
				[ClubMenus.Paste] = new ClubMenuSource(paste),
				// the Cmd/Ctrl + A handler's call
				[ClubMenus.SelectAll] = new ClubMenuSource(new NamedAction()
				{
					Title = "Select All".Localize(),
					Action = () => view3DWidget.SelectAll(),
				}),
				[ClubMenus.Save] = new ClubMenuSource(workspaceActions["Save"]),
				[ClubMenus.SaveAs] = new ClubMenuSource(workspaceActions["SaveAs"]),
				[ClubMenus.Export] = new ClubMenuSource(workspaceActions["Export"]),
				[ClubMenus.Print] = new ClubMenuSource(workspaceActions["Print"]),
				[ClubMenus.ArrangeAll] = new ClubMenuSource(workspaceActions["ArrangeAll"]),
				// the Add button's system file action
				[ClubMenus.AddFile] = new ClubMenuSource(new NamedAction()
				{
					Title = "Add System File to Bed".Localize(),
					Action = () => ApplicationController.OpenFileWithSystemDialog((fileNames) =>
					{
						sceneContext.AddToPlate(fileNames);
					}),
				}),
				[ClubMenus.ClearBed] = new ClubMenuSource(workspaceActions["ClearBed"]),
			};

			ClubMenuBuilder.Build(popupMenu, ClubMenus.BedContext, sources);
		}

		private static SceneOperation FindOperation(string id)
		{
			try
			{
				return SceneOperations.ById(id);
			}
			catch (KeyNotFoundException)
			{
				return null;
			}
		}

		/// <summary>
		/// "Make it a hole" or "Make it solid": the two buttons of the properties panel's Solid / Hole switch
		/// (SelectedObjectPanel SetToHole and SetToSolid), as one undo step each.
		/// </summary>
		private static ClubMenuSource HoleOrSolid(ISceneContext sceneContext, IObject3D selectedItem)
		{
			var undoBuffer = sceneContext.Scene.UndoBuffer;
			if (selectedItem.OutputType != PrintOutputTypes.Hole)
			{
				return new ClubMenuSource(new NamedAction()
				{
					Title = "Make it a hole".Localize(),
					Action = () =>
					{
						if (selectedItem.OutputType != PrintOutputTypes.Hole)
						{
							undoBuffer.AddAndDo(new MakeHole(selectedItem));
						}
					},
				});
			}

			return new ClubMenuSource(new NamedAction()
			{
				Title = "Make it solid".Localize(),
				Action = () =>
				{
					// make sure the render mode is set to shaded or outline
					switch (sceneContext.ViewState.RenderType)
					{
						case RenderTypes.Shaded:
						case RenderTypes.Outlines:
						case RenderTypes.Polygons:
							break;

						default:
							sceneContext.ViewState.RenderType = RenderTypes.Outlines;
							break;
					}

					// the colour the properties panel's colour field starts with
					var worldColor = selectedItem.VisibleMeshes()?.FirstOrDefault()?.WorldColor() ?? Color.White;

					var currentOutputType = selectedItem.OutputType;
					if (currentOutputType != PrintOutputTypes.Solid && currentOutputType != PrintOutputTypes.Default)
					{
						undoBuffer.AddAndDo(new ChangeColor(selectedItem, worldColor, PrintOutputTypes.Solid));
					}
				},
			});
		}
	}
}
