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
using MatterHackers.DataConverters3D;
using MatterHackers.ImageProcessing;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.ClubUi;
using MatterHackers.MatterControl.CustomWidgets;
using MatterHackers.MatterControl.DataStorage;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.Library.Widgets;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.MatterControl.SlicerConfiguration;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	public enum ViewControls3DButtons
	{
		Rotate,
		Scale,
		Translate,
		PartSelect
	}

	public enum PartViewMode
	{
		Layers2D,
		Layers3D,
		Model
	}

	public class ViewModeChangedEventArgs : EventArgs
	{
		public PartViewMode ViewMode { get; set; }

		public PartViewMode PreviousMode { get; set; }
	}

	public class TransformStateChangedEventArgs : EventArgs
	{
		public ViewControls3DButtons TransformMode { get; set; }
	}

	public class ViewToolBarControls : FlowLeftRightWithWrapping
	{
		public event EventHandler ResetView;

		public event EventHandler<TransformStateChangedEventArgs> TransformStateChanged;

		private View3DWidget view3DWidget;
		private readonly ISceneContext sceneContext;
		private readonly PartWorkspace workspace;
		private ViewControls3DButtons activeTransformState = ViewControls3DButtons.PartSelect;
		private readonly Dictionary<GuiWidget, SceneOperation> operationButtons;
		private MainViewWidget mainViewWidget = null;
		private readonly PopupMenuButton bedMenuButton;
		private readonly UndoBuffer undoBuffer;
		private readonly ThemedIconButton undoButton;
		private readonly ThemedIconButton redoButton;

		public ViewToolBarControls(PartWorkspace workspace, ThemeConfig theme, UndoBuffer undoBuffer, bool isPrinterType, bool showPrintButton)
		{
			this.undoBuffer = undoBuffer;
			this.IsPrinterMode = isPrinterType;
			this.sceneContext = workspace.SceneContext;
			this.workspace = workspace;

			this.RowPadding = 0;
			this.RowBorder = new BorderDouble(0, 0, 0, 1);
			this.RowBorderColor = theme.GetBorderColor(50);

			var openLibraryButton = CreateOpenLibraryButton(sceneContext, theme);

			this.AddChild(CreateOpenFileButton(openLibraryButton, theme));

			this.AddChild(CreateSaveButton(theme));

			this.AddChild(new ToolbarSeparator(theme.GetBorderColor(50), theme.SeparatorMargin));

			// EBMC Edition: the Bed menu is about the build plate, so it uses the plate glyph (as the view controls' bed
			// toggle does); bed.png, the heated bed, stays for temperature
			bedMenuButton = new PopupMenuButton("Bed".Localize(), StaticData.Instance.LoadIcon(Path.Combine("Shapes", "view-bed.png"), 16, 16).GrayToColor(theme.TextColor), theme)
			{
				Name = "Bed Options Menu",
				Enabled = true,
				Margin = theme.ButtonSpacing,
				VAnchor = VAnchor.Center,
				DrawArrow = true
			};

			this.AddChild(bedMenuButton);

			this.AddChild(new ToolbarSeparator(theme.GetBorderColor(50), theme.SeparatorMargin));

			undoButton = new ThemedIconButton(StaticData.Instance.LoadIcon("undo.png", 16, 16).GrayToColor(theme.TextColor), theme)
			{
				Name = "3D View Undo",
				ToolTipText = "Undo".Localize(),
				Enabled = false,
				Margin = theme.ButtonSpacing,
				VAnchor = VAnchor.Center
			};
			undoButton.MouseEnterBounds += (s, e) => undoButton.SetActiveUiHint(KeyHints.ForThisComputer("Undo: Ctrl + Z".Localize()));
			undoButton.Click += (sender, e) =>
			{
				sceneContext.Scene.Undo();
				view3DWidget.Object3DControlLayer.Focus();
			};
			this.AddChild(undoButton);

			redoButton = new ThemedIconButton(StaticData.Instance.LoadIcon("redo.png", 16, 16).GrayToColor(theme.TextColor), theme)
			{
				Name = "3D View Redo",
				Margin = theme.ButtonSpacing,
				ToolTipText = "Redo".Localize(),
				Enabled = false,
				VAnchor = VAnchor.Center
			};
			redoButton.MouseEnterBounds += (s, e) => redoButton.SetActiveUiHint(KeyHints.ForThisComputer("Redo: Ctrl + Y, Ctrl + Shift + Z".Localize()));
			redoButton.Click += (sender, e) =>
			{
				sceneContext.Scene.Redo();
				view3DWidget.Object3DControlLayer.Focus();
			};
			this.AddChild(redoButton);

			if (showPrintButton)
			{
				var printButton = new ThemedTextButton("Print".Localize(), theme)
				{
					Name = "Print Button",
					BackgroundColor = theme.AccentMimimalOverlay
				};
				printButton.Click += (s, e) =>
				{
					view3DWidget.PushToPrinterAndPrint();
				};
				this.AddChild(printButton);
			}

			this.AddChild(new ToolbarSeparator(theme.GetBorderColor(50), theme.SeparatorMargin));

			undoButton.Enabled = undoBuffer.UndoCount > 0;
			redoButton.Enabled = undoBuffer.RedoCount > 0;

			operationButtons = new Dictionary<GuiWidget, SceneOperation>();

			// EBMC Edition: Simple mode (UiModes) shows only the operations marked ShowInSimpleMode, no operation groups,
			// no support button and no Tools options menu. Advanced and the stock build show everything as before.
			bool simple = UiModes.IsSimple;

			// In Simple a separator is only drawn between two shown buttons (never doubled or trailing)
			bool simpleButtonShown = false;
			bool simpleSeparatorPending = false;

			void AddSimpleButton(SceneOperation operation)
			{
				if (simpleSeparatorPending)
				{
					this.AddChild(new ToolbarSeparator(theme.GetBorderColor(50), theme.SeparatorMargin));
					simpleSeparatorPending = false;
				}

				AddOperationButton(theme, operation);
				simpleButtonShown = true;
			}

			// Add Selected IObject3D -> Operations to toolbar
			foreach (var namedAction in SceneOperations.All)
			{
				// EBMC Edition: printer tabs can leave modelling tools off the toolbar
				if (!ShowOnThisToolbar(namedAction))
				{
					continue;
				}

				if (simple)
				{
					if (namedAction is SceneSelectionSeparator)
					{
						simpleSeparatorPending = simpleButtonShown;
					}
					else if (namedAction is OperationGroup simpleGroup)
					{
						// The group is hidden; its Simple operations (Align on design tabs) are shown on their own
						foreach (var operation in SceneOperations.SimpleGroupedOperations(simpleGroup, IsPrinterMode)
							.Where(operation => !TrimForPrinter || operation.ShowOnPrinterTabs))
						{
							AddSimpleButton(operation);
						}
					}
					else if (SceneOperations.ShowInSimple(namedAction))
					{
						AddSimpleButton(namedAction);
					}

					continue;
				}

				if (namedAction is SceneSelectionSeparator)
				{
					this.AddChild(new ToolbarSeparator(theme.GetBorderColor(50), theme.SeparatorMargin));
					continue;
				}

				// add the create support before the align
				if (namedAction is OperationGroup group
					&& group.Id == "Transform")
				{
					this.AddChild(CreateSupportButton(theme));
					this.AddChild(new ToolbarSeparator(theme.GetBorderColor(50), theme.SeparatorMargin));
				}

				if (namedAction is OperationGroup operationGroup)
				{
					var expandoSet = new FlowLayoutWidget();

					var actionDropDown = CreateActionDropDown(theme, operationGroup);
					var operationButtonGroup = CreateOperationButtonGroup(theme, operationGroup);

					void UpdateVisability(object s, EventArgs e)
					{
						if (operationGroup.Visible)
						{
							if (operationGroup.Collapse)
							{
								actionDropDown.Visible = true;
								operationButtonGroup.Visible = false;

								DoWrappingLayout();
							}
							else
							{
								actionDropDown.Visible = false;
								operationButtonGroup.Visible = true;

								DoWrappingLayout();
							}
						}
						else
						{
							actionDropDown.Visible = false;
							operationButtonGroup.Visible = false;

							DoWrappingLayout();
						}
					}

					UserSettings.Instance.SettingChanged += UpdateVisability;
					operationGroup.CollapseChanged += UpdateVisability;
					operationGroup.VisibleChanged += UpdateVisability;
					this.Closed += (s, e) =>
					{
						UserSettings.Instance.SettingChanged -= UpdateVisability;
						operationGroup.CollapseChanged -= UpdateVisability;
						operationGroup.VisibleChanged -= UpdateVisability;
					};

					UpdateVisability(operationGroup, null);

					expandoSet.AddChild(actionDropDown);
					expandoSet.AddChild(operationButtonGroup);

					this.AddChild(expandoSet);
				}
				else
				{
					AddOperationButton(theme, namedAction);
				}
			}

			// add the options menu
			this.AddChild(new HorizontalSpacer());

			optionsButton = new PopupMenuButton("Tools".Localize(), theme)
			{
				AlignToRightEdge = true,
				DrawArrow = true,
				// EBMC Edition: no Tools options menu in Simple mode (it only shows, hides and expands groups)
				Visible = !simple,
			};
			this.AddChild(optionsButton);
			optionsButton.Name = "ToolBar Overflow Menu";
			optionsButton.ToolTipText = "Tool Bar Options".Localize();
			optionsButton.DynamicPopupContent = () => GenerateToolBarOptionsMenu(theme);

			// Register listeners
			undoBuffer.Changed += UndoBuffer_Changed;
			sceneContext.Scene.SelectionChanged += UpdateToolbarButtons;
			sceneContext.Scene.ItemsModified += UpdateToolbarButtons;

			// Run on load
			UpdateToolbarButtons(null, null);

			this.MouseDown += (s, mouseEvent) =>
			{
				if (mouseEvent.Button == MouseButtons.Right
					&& !simple)
				{
					UiThread.RunOnIdle(() =>
					{
						var popupMenu = GenerateToolBarOptionsMenu(theme);
						popupMenu.ShowMenu(this, mouseEvent);
					});
				}
			};
		}

		/// <summary>
		/// A toolbar button for a single operation.
		/// </summary>
		private void AddOperationButton(ThemeConfig theme, SceneOperation namedAction)
		{
			GuiWidget button;

			if (namedAction.Icon != null)
			{
				button = new ThemedIconButton(namedAction.Icon(theme), theme)
				{
					Name = namedAction.Title + " Button",
					ToolTipText = namedAction.Title,
					Margin = theme.ButtonSpacing,
					BackgroundColor = theme.ToolbarButtonBackground,
					HoverColor = theme.ToolbarButtonHover,
					MouseDownColor = theme.ToolbarButtonDown,
				};

				if (!string.IsNullOrEmpty(namedAction.UiHint))
				{
					button.MouseEnterBounds += (s1, e1) => button.SetActiveUiHint(namedAction.UiHint);
				}
			}
			else
			{
				button = new ThemedTextButton(namedAction.Title, theme)
				{
					Name = namedAction.Title + " Button",
					Margin = theme.ButtonSpacing,
					BackgroundColor = theme.ToolbarButtonBackground,
					HoverColor = theme.ToolbarButtonHover,
					MouseDownColor = theme.ToolbarButtonDown,
				};
			}

			operationButtons.Add(button, namedAction);

			button.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				namedAction.Action.Invoke(sceneContext);
				var partTab = button.Parents<DesignTabPage>().FirstOrDefault();
				var view3D = partTab.Descendants<View3DWidget>().FirstOrDefault();
				view3D.Object3DControlLayer.Focus();
			});

			this.AddChild(button);
		}

        private string Collapsed;
		private string Expanded;
		private string Hidden;

		private string Default;
		private string Hide_All;
		private string Expand_All;
        private PopupMenuButton optionsButton;

        private IEnumerable<OperationGroup> GroupOperations

		{
			get
			{
				foreach (var namedAction in SceneOperations.All)
				{
					if (namedAction is OperationGroup operationGroup
						&& ShowOnThisToolbar(operationGroup))
					{
						yield return operationGroup;
					}
				}
			}
		}

		private PopupMenu GenerateToolBarOptionsMenu(ThemeConfig theme)
		{
			var popupMenu = new PopupMenu(theme)
			{
				Padding = new BorderDouble(0, 7),
			};

#if true
			void DefaultClicked(object e, MouseEventArgs mouseEvent)
			{
				foreach (var operationGroup in GroupOperations)
				{
					switch(operationGroup.Id)
                    {
						case "Path":
						case "Printing":
						case "Design Apps":
							operationGroup.Visible = false;
							break;

						default:
							operationGroup.Collapse = true;
							operationGroup.Visible = true;
							break;
                    }
				}

				optionsButton.InvokeClick();
				UiThread.RunOnIdle(optionsButton.InvokeClick);
			}

			void HideAll(object e, MouseEventArgs mouseEvent)
			{
				foreach (var operationGroup in GroupOperations)
				{
					operationGroup.Visible = false;
				}

				optionsButton.InvokeClick();
				UiThread.RunOnIdle(optionsButton.InvokeClick);
			}

			void ExpandAll(object e, MouseEventArgs mouseEvent)
			{
				foreach (var operationGroup in GroupOperations)
				{
					operationGroup.Collapse = false;
					operationGroup.Visible = true;
				}

				optionsButton.InvokeClick();
				UiThread.RunOnIdle(optionsButton.InvokeClick);
			}

			// buttons for the control of defaults
			var topButtonData = new (string, string, EventHandler<MouseEventArgs>)[]
			{
				(nameof(Default), nameof(Default).Replace("_", " ").Localize(), DefaultClicked),
				(nameof(Expand_All), nameof(Expand_All).Replace("_", " ").Localize(), ExpandAll),
				(nameof(Hide_All), nameof(Hide_All).Replace("_", " ").Localize(), HideAll),
			};

			popupMenu.CreateButtonMenuItem("Design Tools".Localize(), topButtonData, 40 * GuiWidget.DeviceScale, true);

			popupMenu.CreateSeparator(5);
#endif

			// the buttons per setting
			var buttonData = new (string, string)[]
			{
				(nameof(Collapsed), "Collapse".Localize()),
				(nameof(Expanded), "Expand".Localize()),
				(nameof(Hidden), "Hide".Localize()),
			};

			foreach (var operationGroup in GroupOperations)
			{
				var startingValue = operationGroup.Collapse ? nameof(Collapsed) : nameof(Expanded);
				if (!operationGroup.Visible)
				{
					startingValue = nameof(Hidden);
				}

				popupMenu.CreateButtonSelectMenuItem(operationGroup.Title, buttonData, startingValue, (value) =>
				{
					switch (value)
					{
						case nameof(Expanded):
							operationGroup.Collapse = false;
							operationGroup.Visible = true;
							break;

						case nameof(Collapsed):
							operationGroup.Collapse = true;
							operationGroup.Visible = true;
							break;

						case nameof(Hidden):
							operationGroup.Visible = false;
							break;
					}
				}, 40 * GuiWidget.DeviceScale);

				popupMenu.CreateSeparator();
			};

			return popupMenu;
		}

		private FlowLayoutWidget CreateOperationButtonGroup(ThemeConfig theme, OperationGroup operationGroup)
		{
			var buttonGroup = new FlowLayoutWidget();

			foreach (var operation in ToolbarOperations(operationGroup))
			{
				var operationButton = new OperationIconButton(operation, sceneContext, theme);
				if (!string.IsNullOrEmpty(operation.UiHint))
				{
					operationButton.MouseEnterBounds += (s1, e1) => operationButton.SetActiveUiHint(operation.UiHint);
				}
				operationButtons.Add(operationButton, operation);

				buttonGroup.AddChild(operationButton);
			}

			var collapseButton = buttonGroup.AddChild(new ThemedIconButton(StaticData.Instance.LoadIcon("collapse_single.png", 8, 16).GrayToColor(theme.TextColor), theme));
			collapseButton.Width = 16 * DeviceScale;
			collapseButton.ToolTipText = "Collapse".Localize();

			collapseButton.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				operationGroup.Collapse = true;
				DoWrappingLayout();
			});

			buttonGroup.AddChild(new ToolbarSeparator(theme.GetBorderColor(50), theme.SeparatorMargin));

			return buttonGroup;
		}

		private GuiWidget CreateActionDropDown(ThemeConfig theme, OperationGroup operationGroup)
		{
			var buttonGroup = new FlowLayoutWidget();

			var groupOperations = ToolbarOperations(operationGroup);
			var defaultOperation = ToolbarDefaultOperation(operationGroup);

			PopupMenuButton actionAndDropDown = null;
			actionAndDropDown = theme.CreateSplitButton(
				new SplitButtonParams()
				{
					Icon = defaultOperation.Icon(theme),
					ButtonAction = (menuButton) =>
					{
						defaultOperation.Action.Invoke(sceneContext);
					},
					ButtonTooltip = defaultOperation.HelpText ?? defaultOperation.Title,
					ButtonName = defaultOperation.Title,
					ExtendPopupMenu = (PopupMenu popupMenu) =>
					{
						foreach (var operation in groupOperations)
						{
							var operationMenu = popupMenu.CreateMenuItem(operation.Title, operation.Icon?.Invoke(theme));

							operationMenu.Enabled = operation.IsEnabled(sceneContext);
							operationMenu.ToolTipText = operation.Title;

							if (!operationMenu.Enabled
								&& !string.IsNullOrEmpty(operation.HelpText))
							{
								operationMenu.ToolTipText += "\n\n" + operation.HelpText;
							}

							operationMenu.Click += (s, e) => UiThread.RunOnIdle(() =>
							{
								if (defaultOperation != operation)
								{
									// Update button
									var iconButton = actionAndDropDown.Children.OfType<ThemedIconButton>().First();
									iconButton.SetIcon(operation.Icon(theme));
									iconButton.ToolTipText = operation.HelpText ?? operation.Title;

									if (!string.IsNullOrEmpty(operation.UiHint))
									{
										iconButton.MouseEnterBounds += (s1, e1) => iconButton.SetActiveUiHint(operation.UiHint);
									}

									UserSettings.Instance.set(operationGroup.GroupRecordId, operationGroup.Operations.IndexOf(operation).ToString());

									defaultOperation = operation;

									iconButton.Invalidate();
								}

								operation.Action?.Invoke(sceneContext);
							});
						}

						var expandMenuItem = popupMenu.CreateMenuItem("Expand".Localize(), StaticData.Instance.LoadIcon("expand_single.png", 8, 16).GrayToColor(theme.TextColor));

						expandMenuItem.Click += (s, e) => UiThread.RunOnIdle(() =>
						{
							operationGroup.Collapse = false;
							DoWrappingLayout();
						});
					}
				},
				operationGroup);

			operationButtons.Add(actionAndDropDown, operationGroup);

			buttonGroup.AddChild(actionAndDropDown);
			buttonGroup.AddChild(new ToolbarSeparator(theme.GetBorderColor(50), theme.SeparatorMargin));

			return buttonGroup;
		}

		public void NotifyResetView()
		{
			this.ResetView.Invoke(this, null);
		}

		public bool IsPrinterMode { get; }

		/// <summary>
		/// EBMC Edition: true on a printer tab when the OEM settings trim its toolbar to what printing needs.
		/// </summary>
		private bool TrimForPrinter => IsPrinterMode && OemSettings.Instance.TrimPrinterTabToolbar;

		/// <summary>
		/// The operations of a group that this toolbar shows.
		/// </summary>
		private List<SceneOperation> ToolbarOperations(OperationGroup operationGroup)
		{
			return operationGroup.Operations
				.Where(operation => operation != null && (!TrimForPrinter || operation.ShowOnPrinterTabs))
				.ToList();
		}

		/// <summary>
		/// The group's remembered operation, or its first shown one when this toolbar leaves the remembered one out.
		/// </summary>
		private SceneOperation ToolbarDefaultOperation(OperationGroup operationGroup)
		{
			var defaultOperation = operationGroup.GetDefaultOperation();
			var shown = ToolbarOperations(operationGroup);

			return shown.Contains(defaultOperation) ? defaultOperation : shown.First();
		}

		/// <summary>
		/// Whether an operation or group belongs on this toolbar: everything on design tabs; on a trimmed printer tab only
		/// the ones marked for printer tabs, and a group only if it keeps at least one operation.
		/// </summary>
		private bool ShowOnThisToolbar(SceneOperation operation)
		{
			if (!TrimForPrinter)
			{
				return true;
			}

			if (!operation.ShowOnPrinterTabs)
			{
				return false;
			}

			return !(operation is OperationGroup group)
				|| ToolbarOperations(group).Any();
		}

		public ViewControls3DButtons ActiveButton
		{
			get => activeTransformState;
			set
			{
				this.activeTransformState = value;
				view3DWidget?.UpdateControlButtons(activeTransformState);
				TransformStateChanged?.Invoke(this, new TransformStateChangedEventArgs()
				{
					TransformMode = activeTransformState
				});
			}
		}

		internal void SetView3DWidget(View3DWidget view3DWidget)
		{
			this.view3DWidget = view3DWidget;

			bedMenuButton.DynamicPopupContent = () =>
			{
				var workspaceActions = ApplicationController.Instance.GetWorkspaceActions(view3DWidget);
				var menuTheme = ApplicationController.Instance.MenuTheme;
				var popupMenu = new PopupMenu(menuTheme);

				int thumbWidth = 45;
				var gutterWidth = thumbWidth + 7;

				Action<PopupMenu> populateRecent = (subMenu) =>
				{
					int maxItemWidth = 0;

					var recentFiles = new DirectoryInfo(ApplicationDataStorage.Instance.PlatingDirectory).GetFiles("*.mcx").OrderByDescending(f => f.LastWriteTime);
					foreach (var item in recentFiles.Where(f => f.Length > 215).Select(f => new SceneReplacementFileItem(f.FullName)).Take(12))
					{
						var imageBuffer = new ImageBuffer(thumbWidth, thumbWidth);

						var title = new FileInfo(item.FilePath).LastWriteTime.ToString("MMMM d h:mm tt");

						var bedHistory = subMenu.CreateMenuItem(title, imageBuffer);
						bedHistory.GutterWidth = gutterWidth;
						bedHistory.HAnchor = HAnchor.Fit;
						bedHistory.VAnchor = VAnchor.Absolute;
						bedHistory.Padding = new BorderDouble(gutterWidth + 3, 2, 12, 2);
						bedHistory.Height = thumbWidth + 3;
						bedHistory.Click += (s, e) =>
						{
							UiThread.RunOnIdle(async () =>
							{
								await ApplicationController.Instance.Tasks.Execute("Saving changes".Localize() + "...", sceneContext.Printer, sceneContext.SaveChanges);

								await sceneContext.LoadLibraryContent(item, null);

								if (sceneContext.Printer != null)
								{
									sceneContext.Printer.ViewState.ViewMode = PartViewMode.Model;
								}
							});
						};

						maxItemWidth = (int)Math.Max(maxItemWidth, bedHistory.Width);

						void UpdateImageBuffer(ImageBuffer thumbnail)
						{
							// Copy updated thumbnail into original image
							imageBuffer.CopyFrom(thumbnail);
							bedHistory.Invalidate();
						}

						ApplicationController.Instance.Library.LoadItemThumbnail(
							UpdateImageBuffer,
							(contentProvider) =>
							{
								if (contentProvider is MeshContentProvider meshContentProvider)
								{
									ApplicationController.Instance.Thumbnails.QueueForGeneration(async () =>
									{
										// Ask the MeshContentProvider to RayTrace the image
										var thumbnail = await meshContentProvider.GetThumbnail(item, thumbWidth, thumbWidth);
										if (thumbnail != null)
										{
											UpdateImageBuffer(thumbnail);
										}
									});
								}
							},
							item,
							ApplicationController.Instance.Library.PlatingHistory,
							thumbWidth,
							thumbWidth,
							menuTheme).ConfigureAwait(false);
					}

					// Resize menu items to max item width
					foreach (var menuItem in subMenu.Children)
					{
						menuItem.HAnchor = HAnchor.Left | HAnchor.Absolute;
						menuItem.Width = maxItemWidth;
					}
				};

				// EBMC Edition: the club Bed menu (OemSettings.ClubDialogsAndMenus): the same items with the same actions
				// and enable rules, regrouped by ClubMenus.Bed, plus the existing Add a file, Save, Save as and Arrange
				// all actions. Print keeps its title, shortcut and enable rule and stays between Edit and Export.
				if (ClubStyle.On)
				{
					var sources = new Dictionary<string, ClubMenuSource>()
					{
						[ClubMenus.OpenRecent] = new ClubMenuSource((menu, title) =>
						{
							menu.CreateSubMenu(title, menuTheme, populateRecent);
							return ClubMenuBuilder.LastItem(menu);
						}),
						[ClubMenus.AddFile] = new ClubMenuSource(new NamedAction()
						{
							Title = "Add System File to Bed".Localize(),
							// the Add button's system file action (CreateOpenFileButton)
							Action = () => ApplicationController.OpenFileWithSystemDialog((fileNames) =>
							{
								sceneContext.AddToPlate(fileNames);
							}),
						}),
						[ClubMenus.Edit] = new ClubMenuSource(workspaceActions["Edit"]),
						[ClubMenus.Print] = new ClubMenuSource(workspaceActions["Print"]),
						[ClubMenus.Save] = new ClubMenuSource(workspaceActions["Save"]),
						[ClubMenus.SaveAs] = new ClubMenuSource(workspaceActions["SaveAs"]),
						// the Bed menu's own Export (below), unchanged
						[ClubMenus.Export] = new ClubMenuSource(new NamedAction()
						{
							ID = "Export",
							Title = "Export".Localize(),
							Action = () =>
							{
								ApplicationController.Instance.ExportLibraryItems(
									new[] { new InMemoryLibraryItem(sceneContext.Scene) },
									centerOnBed: false,
									printer: view3DWidget.Printer);
							},
							IsEnabled = () => sceneContext.EditableScene
								|| (sceneContext.EditContext.SourceItem is ILibraryAsset libraryAsset
									&& string.Equals(Path.GetExtension(libraryAsset.FileName), ".gcode", StringComparison.OrdinalIgnoreCase))
						}),
						[ClubMenus.ArrangeAll] = new ClubMenuSource(workspaceActions["ArrangeAll"]),
						[ClubMenus.ClearBed] = new ClubMenuSource(workspaceActions["ClearBed"]),
					};

					ClubMenuBuilder.Build(popupMenu, ClubMenus.Bed, sources);
					return popupMenu;
				}

				popupMenu.CreateSubMenu("Open Recent".Localize(), menuTheme, populateRecent);

				var actions = new NamedAction[]
				{
					new ActionSeparator(),
					workspaceActions["Edit"],
					new ActionSeparator(),
					workspaceActions["Print"],
					new ActionSeparator(),
					new NamedAction()
					{
						ID = "Export",
						Title = "Export".Localize(),
						Icon = StaticData.Instance.LoadIcon("cube_export.png", 16, 16).GrayToColor(menuTheme.TextColor),
						Action = () =>
						{
							ApplicationController.Instance.ExportLibraryItems(
								new[] { new InMemoryLibraryItem(sceneContext.Scene) },
								centerOnBed: false,
								printer: view3DWidget.Printer);
						},
						IsEnabled = () => sceneContext.EditableScene
							|| (sceneContext.EditContext.SourceItem is ILibraryAsset libraryAsset
								&& string.Equals(Path.GetExtension(libraryAsset.FileName), ".gcode", StringComparison.OrdinalIgnoreCase))
					},
					new ActionSeparator(),
					new NamedAction()
					{
						ID = "ClearBed",
						Title = "Clear Bed".Localize(),
						Action = () =>
						{
							UiThread.RunOnIdle(() =>
							{
								view3DWidget.ClearPlate();
							});
						}
					}
				};

				menuTheme.CreateMenuItems(popupMenu, actions);

				return popupMenu;
			};
		}

		private void UndoBuffer_Changed(object sender, EventArgs e)
		{
			undoButton.Enabled = undoBuffer.UndoCount > 0;
			redoButton.Enabled = undoBuffer.RedoCount > 0;
		}

		private GuiWidget CreateOpenFileButton(GuiWidget openLibraryButton, ThemeConfig theme)
		{
			var popupMenu = new PopupMenuButton(openLibraryButton, theme)
			{
				Name = "Open File Button"
			};
			popupMenu.DistinctPopupButton = true;
			popupMenu.DrawArrow = true;
			openLibraryButton.Selectable = true;

			if (popupMenu.Children<ThemedButton>().FirstOrDefault() is ThemedButton simpleButton)
			{
				simpleButton.Padding = 0;
			};

			var openMenuItems = new PopupMenu(ApplicationController.Instance.MenuTheme);
			popupMenu.PopupContent = openMenuItems;

			// EBMC Edition: the club Add split (OemSettings.ClubDialogsAndMenus): the button opens the system file dialog
			// (ClubAddButton) and the arrow lists both sources; the same two actions as before
			if (ClubStyle.On
				&& openLibraryButton is ClubAddButton clubAddButton)
			{
				ClubMenuBuilder.StyleMenu(openMenuItems);
				var menuTheme = ApplicationController.Instance.MenuTheme;

				var fromLibrary = ClubMenuBuilder.CreateItem(openMenuItems, "From the Library".Localize() + ClubMenuText.Ellipsis, ClubStyle.MenuIcon("cube.png", ClubStyle.Muted(menuTheme)), null, false);
				fromLibrary.Name = "Add From Library Menu Item";
				fromLibrary.Click += (s, e) => UiThread.RunOnIdle(clubAddButton.ShowPopup);

				var fromFile = ClubMenuBuilder.CreateItem(openMenuItems, "From a file".Localize() + ClubMenuText.Ellipsis, ClubStyle.MenuIcon("fa-folder-open_16.png", ClubStyle.Muted(menuTheme)), null, false);
				fromFile.Name = "Add System File to Bed Menu Item";
				fromFile.Click += (s, e) => clubAddButton.OpenSystemFile();

				return popupMenu;
			}

			var openFileButton = openMenuItems.CreateMenuItem("Add System File to Bed".Localize(), StaticData.Instance.LoadIcon("fa-folder-open_16.png", 16, 16).GrayToColor(theme.TextColor));

			openFileButton.Click += (s, e) =>
            {
                ApplicationController.OpenFileWithSystemDialog((fileNames) =>
                {
					sceneContext.AddToPlate(fileNames);
				});
            };

			return popupMenu;
		}

        private void UpdateToolbarButtons(object sender, EventArgs e)
		{
			// Set enabled level based on operation rules
			foreach (var (button, operation) in operationButtons.Select(kvp => (kvp.Key, kvp.Value)))
			{
				button.Enabled = operation.IsEnabled?.Invoke(sceneContext) ?? false;
                button.MouseEnterBounds += (s, e) => button.SetActiveUiHint(operation.Title);
				button.ToolTipText = operation.Title;
				if (!button.Enabled
					&& !string.IsNullOrEmpty(operation.HelpText))
				{
					button.ToolTipText += "\n\n" + operation.HelpText;
				}

				if (operation is OperationGroup operationGroup
					&& button is PopupMenuButton splitButton
					&& button.Descendants<ThemedIconButton>().FirstOrDefault() is ThemedIconButton iconButton)
				{
					var defaultOperation = ToolbarDefaultOperation(operationGroup);
					iconButton.Enabled = defaultOperation.IsEnabled(sceneContext);
					iconButton.ToolTipText = defaultOperation.Title;

					if (!iconButton.Enabled
						&& !string.IsNullOrEmpty(defaultOperation.HelpText))
					{
						iconButton.ToolTipText += "\n\n" + defaultOperation.HelpText;
					}
				}
			}
		}

		private GuiWidget CreateOpenLibraryButton(ISceneContext sceneContext, ThemeConfig theme)
		{
			var openColor = theme.ResolveColor(theme.BackgroundColor, theme.SlightShade);

			PopupMenuButton libraryPopup = null;

			// EBMC Edition: with ClubDialogsAndMenus the button reads "Add" and opens the system file dialog; the Library
			// panel below opens from the arrow menu instead (ClubAddButton.ShowPopup). Off, ClubAddButton is a plain
			// PopupMenuButton.
			libraryPopup = new ClubAddButton(
				ClubStyle.On ? "Add".Localize() : "Add to Bed".Localize(),
				StaticData.Instance.LoadIcon("fa-folder-open_16.png", 16, 16).GrayToColor(theme.TextColor),
				theme,
				ClubStyle.On ? (fileNames) => sceneContext.AddToPlate(fileNames) : (Action<string[]>)null)
			{
				MakeScrollable = false,
				Name = "Add Content Menu",
				AlwaysKeepOpen = true,
				DynamicPopupContent = () =>
				{
					if (mainViewWidget == null)
					{
						mainViewWidget = this.Parents<MainViewWidget>().FirstOrDefault();
					}

					var verticalResizeContainer = new VerticalResizeContainer(theme, GrabBarSide.Right)
					{
						BackgroundColor = openColor,
						MinimumSize = new Vector2(120, 50),
						Height = libraryPopup.TransformToScreenSpace(libraryPopup.Position).Y,
						SplitterBarColor = theme.SlightShade,
					};

					double.TryParse(UserSettings.Instance.get(UserSettingsKey.PopupLibraryWidth), out double controlWidth);
					if (controlWidth == 0)
					{
						controlWidth = 400;
					}

					verticalResizeContainer.Width = controlWidth;

					verticalResizeContainer.BoundsChanged += (s2, e2) =>
					{
						UserSettings.Instance.set(UserSettingsKey.PopupLibraryWidth, verticalResizeContainer.Width.ToString());
					};

					var systemWindow = this.Parents<SystemWindow>().FirstOrDefault();

					// Compute slight highlight of openColor for use as listView background color
					var slightHighlight = theme.ResolveColor(openColor, Color.White.WithAlpha(theme.IsDarkTheme ? 10 : 50));

					var popupLibraryWidget = new PopupLibraryWidget(mainViewWidget, workspace, theme, slightHighlight, libraryPopup)
					{
						HAnchor = HAnchor.Stretch,
						VAnchor = VAnchor.Absolute,
						Height = libraryPopup.TransformToScreenSpace(libraryPopup.Position).Y,
						Margin = new BorderDouble(left: verticalResizeContainer.SplitterWidth)
					};

					systemWindow.SizeChanged += (s, e) =>
					{
						popupLibraryWidget.Height = libraryPopup.TransformToScreenSpace(libraryPopup.Position).Y;
					};

					verticalResizeContainer.AddChild(popupLibraryWidget);

					systemWindow.MouseDown += SystemWindownMouseDown;

					void SystemWindownMouseDown(object s2, MouseEventArgs mouseEvent)
					{
						if (verticalResizeContainer.Parent != null)
						{
							// MouseUp on our SystemWindow outside of our bounds should call close
							var resizeContainerMousePosition = verticalResizeContainer.TransformFromScreenSpace(mouseEvent.Position);
							bool mouseUpOnWidget = resizeContainerMousePosition.X >= 0 && resizeContainerMousePosition.X <= verticalResizeContainer.Width
								&& resizeContainerMousePosition.Y >= 0 && resizeContainerMousePosition.Y <= verticalResizeContainer.Height;

							if (!mouseUpOnWidget)
							{
								libraryPopup.CloseMenu();
								systemWindow.MouseDown -= SystemWindownMouseDown;
							}
						}
						else
						{
							systemWindow.MouseDown -= SystemWindownMouseDown;
						}
					}

					return verticalResizeContainer;
				},
				BackgroundColor = theme.ToolbarButtonBackground,
				HoverColor = theme.ToolbarButtonHover,
				MouseDownColor = theme.ToolbarButtonDown,
				OpenColor = openColor,
				// DrawArrow = true,
				Margin = 0,
				PopupBorderColor = Color.Transparent,
				PopupHAnchor = HAnchor.Fit,
				PopupVAnchor = VAnchor.Fit
			};

			if (ClubStyle.On)
			{
				libraryPopup.ToolTipText = "Add a file to the bed".Localize();
			}

			return libraryPopup;
		}

		/// <summary>
		/// EBMC Edition: the Add button. With an <c>addFiles</c> action (ClubDialogsAndMenus on) a click opens the system
		/// file dialog (the action of the stock "Add System File to Bed" item) and the Library panel opens only through
		/// <see cref="PopupButton.ShowPopup"/> from the arrow menu. Without one it is a plain PopupMenuButton.
		/// </summary>
		private class ClubAddButton : PopupMenuButton
		{
			private readonly Action<string[]> addFiles;

			public ClubAddButton(string text, ImageBuffer icon, ThemeConfig theme, Action<string[]> addFiles)
				: base(text, icon, theme)
			{
				this.addFiles = addFiles;
			}

			public void OpenSystemFile()
			{
				ApplicationController.OpenFileWithSystemDialog((fileNames) =>
				{
					addFiles?.Invoke(fileNames);
				});
			}

			protected override void OnClick(MouseEventArgs mouseEvent)
			{
				if (addFiles == null)
				{
					base.OnClick(mouseEvent);
					return;
				}

				// a click that closed the open Library panel (it closes when the mouse goes down) does not also open the
				// file dialog; otherwise open the file dialog
				if (!panelOpenAtMouseDown
					&& mouseEvent.Button == MouseButtons.Left)
				{
					this.OpenSystemFile();
				}

				panelOpenAtMouseDown = false;

				// still raise the Click event (PopupButton.OnClick then GuiWidget.OnClick), with the Library panel's
				// content factory swapped for one that returns nothing, so PopupButton.ShowPopup opens no panel
				var libraryContent = this.DynamicPopupContent;
				this.DynamicPopupContent = () => null;
				try
				{
					base.OnClick(mouseEvent);
				}
				finally
				{
					this.DynamicPopupContent = libraryContent;
				}
			}

			private bool panelOpenAtMouseDown;

			public override void OnMouseDown(MouseEventArgs mouseEvent)
			{
				panelOpenAtMouseDown = this.MenuVisible;
				base.OnMouseDown(mouseEvent);
			}
		}

		private GuiWidget CreateSupportButton(ThemeConfig theme)
		{
			PopupMenuButton toggleSupportButton = null;

			toggleSupportButton = new PopupMenuButton(StaticData.Instance.LoadIcon("edit_support.png", 16, 16).GrayToColor(theme.TextColor), theme)
			{
				Name = "Support SplitButton",
				ToolTipText = "Generate Support".Localize(),
				DynamicPopupContent = () => CreateSupportPanel(sceneContext),
				PopupHAnchor = HAnchor.Fit,
				PopupVAnchor = VAnchor.Fit,
				MakeScrollable = false,
				BackgroundColor = theme.ToolbarButtonBackground,
				HoverColor = theme.ToolbarButtonHover,
				MouseDownColor = theme.ToolbarButtonDown,
				DrawArrow = true,
				Margin = theme.ButtonSpacing,
			};

			return toggleSupportButton;
		}

		/// <summary>
		/// The Generate Support panel the toolbar's support button opens (also opened by the shapes palette).
		/// </summary>
		internal static GuiWidget CreateSupportPanel(ISceneContext sceneContext)
		{
			var minimumSupportHeight = .05;
			if (sceneContext.Printer != null)
			{
				minimumSupportHeight = sceneContext.Printer.Settings.GetValue<double>(SettingsKey.layer_height) / 2;
			}

			return new GenerateSupportPanel(AppContext.MenuTheme, sceneContext.Scene, minimumSupportHeight);
		}

		private GuiWidget CreateSaveButton(ThemeConfig theme)
		{
			return theme.CreateSplitButton(new SplitButtonParams()
			{
				ButtonText = "Save".Localize(),
				ButtonName = "Save",
				// EBMC Edition: the shortcut in the tooltip (ClubDialogsAndMenus)
				ButtonTooltip = ClubStyle.On ? "Save".Localize() + " (" + KeyHints.ForThisComputer("Ctrl + S") + ")" : null,
				Icon = StaticData.Instance.LoadIcon("save_grey_16x.png", 16, 16).GrayToColor(theme.TextColor),
				ButtonAction = (menuButton) =>
				{
					ApplicationController.Instance.Tasks.Execute("Saving".Localize(), sceneContext.Printer, async (progress, cancellationToken) =>
					{
						menuButton.Enabled = false;

						if (sceneContext.EditContext.ContentStore == null)
						{
							UiThread.RunOnIdle(() =>
							{
								// we need to ask for a destination
								DialogWindow.Show(
									new SaveAsPage(
										(container, newName) =>
										{
											sceneContext.SaveAs(container, newName);
										}));
							});
						}
						else
						{
							try
							{
								await sceneContext.SaveChanges(progress, cancellationToken);
							}
							catch (Exception ex)
							{
								ApplicationController.Instance.LogError("Error saving file".Localize() + ": " + ex.Message);
							}
						}

						menuButton.Enabled = true;
					}).ConfigureAwait(false);
				},
				ExtendPopupMenu = (PopupMenu popupMenu) =>
				{
					// EBMC Edition: club menu style, same two actions (ClubDialogsAndMenus)
					if (ClubStyle.On)
					{
						ClubMenuBuilder.StyleMenu(popupMenu);
						var clubSaveAs = ClubMenuBuilder.CreateItem(popupMenu, ClubMenuText.WithEllipsis("Save as".Localize(), true), null, null, false);
						clubSaveAs.Name = "Save As Menu Item";
						clubSaveAs.Click += (s, e) => UiThread.RunOnIdle(() =>
						{
							DialogWindow.Show(
								new SaveAsPage(
									(container, newName) =>
									{
										sceneContext.SaveAs(container, newName);
									}));
						});
						var clubExport = ClubMenuBuilder.CreateItem(popupMenu, ClubMenuText.WithEllipsis("Export".Localize(), true), ClubStyle.MenuIcon("cube_export.png", ClubStyle.Muted(popupMenu.Theme)), null, false);
						clubExport.Name = "Export Menu Item";
						clubExport.Click += (s, e) => UiThread.RunOnIdle(() =>
						{
							ApplicationController.Instance.ExportLibraryItems(
								new[] { new InMemoryLibraryItem(sceneContext.Scene) },
								centerOnBed: false,
								printer: view3DWidget.Printer);
						});
						return;
					}

					var saveAs = popupMenu.CreateMenuItem("Save As".Localize());
					saveAs.Click += (s, e) => UiThread.RunOnIdle(() =>
					{
						DialogWindow.Show(
							new SaveAsPage(
								(container, newName) =>
								{
									sceneContext.SaveAs(container, newName);
								}));
					});
					var export = popupMenu.CreateMenuItem("Export".Localize(), StaticData.Instance.LoadIcon("cube_export.png", 16, 16).GrayToColor(theme.TextColor));
					export.Click += (s, e) => UiThread.RunOnIdle(() =>
					{
						ApplicationController.Instance.ExportLibraryItems(
							new[] { new InMemoryLibraryItem(sceneContext.Scene) },
							centerOnBed: false,
							printer: view3DWidget.Printer);
					});
				}
			});
		}

		public override void OnClosed(EventArgs e)
		{
			// Unregister listeners
			undoBuffer.Changed -= UndoBuffer_Changed;
			sceneContext.Scene.SelectionChanged -= UpdateToolbarButtons;
			sceneContext.Scene.Children.ItemsModified -= UpdateToolbarButtons;

			base.OnClosed(e);
		}
	}
}