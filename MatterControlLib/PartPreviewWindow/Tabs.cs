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
using MatterHackers.Agg.Font;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;
using MatterHackers.DataConverters3D;
using MatterHackers.ImageProcessing;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.Library;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	public interface ITab
	{
		GuiWidget TabContent { get; }

		string Key { get; }

		string Text { get; }
	}

	/// <summary>
	/// A toolbar and associated tab body
	/// </summary>
	public class SimpleTabs : FlowLayoutWidget
	{
		public SimpleTabs(ThemeConfig theme, string overflowText, GuiWidget rightAnchorItem = null)
			: base(FlowDirection.TopToBottom)
		{
			this.TabContainer = this;

			if (rightAnchorItem == null)
			{
				TabBar = new OverflowBar(null, theme, overflowText)
				{
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Fit
				};
			}
			else
			{
				TabBar = new Toolbar(theme.TabbarPadding, rightAnchorItem)
				{
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Fit
				};
			}

			this.AddChild(this.TabBar);
		}

		public Toolbar TabBar { get; }

		public GuiWidget TabContainer { get; protected set; }

		public event EventHandler ActiveTabChanged;

		public IEnumerable<ITab> AllTabs
		{
			get
			{
				foreach (var child in this.TabBar.ActionArea.Children)
				{
					if (child is ITab iTab)
					{
						yield return iTab;
					}
				}
			}
		}

		public int TabCount => AllTabs.Count();

		public virtual void AddTab(GuiWidget tabWidget, int position = -1)
		{
			var iTab = tabWidget as ITab;

			tabWidget.Click += TabWidget_Click;

			this.TabBar.ActionArea.AddChild(tabWidget, position);

			this.TabContainer.AddChild(iTab.TabContent);
		}

		public virtual void AddTab(GuiWidget tabWidget, int tabPosition, int widgetPosition)
		{
			var iTab = tabWidget as ITab;

			tabWidget.Click += TabWidget_Click;

			this.TabBar.ActionArea.AddChild(tabWidget, widgetPosition);

			this.TabContainer.AddChild(iTab.TabContent);
		}

		private void TabWidget_Click(object sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				var tab = sender as ITab;
				this.ActiveTab = tab;

				// Push focus to tab content on tab pill selection
				tab.TabContent.Focus();
			}
		}

		public string SelectedTabKey
		{
			get
			{
				return this.ActiveTab?.Key;
			}
			set
			{
				var foundTab = AllTabs.First();
				foreach (var tab in AllTabs)
				{
					if (tab.Key == value)
					{
						foundTab = tab;
					}
				}

				this.ActiveTab = foundTab;
			}
		}

		public int SelectedTabIndex
		{
			get => AllTabs.IndexOf(this.ActiveTab);
			set
			{
				int index = 0;
				foreach (var tab in AllTabs)
				{
					if (index == value)
					{
						this.ActiveTab = tab;
					}
				}
			}
		}

		internal virtual void CloseTab(ITab tab)
		{
			// Close Tab and TabContent widgets
			tab.TabContent.Close();
			(tab as GuiWidget)?.Close();

			if (tab is ChromeTab chromeTab)
			{
				// Activate next or last tab
				ActiveTab = chromeTab.NextTab ?? AllTabs.LastOrDefault();
			}
			else
			{
				// Activate last tab
				ActiveTab = AllTabs.LastOrDefault();
			}
		}

		private ITab _activeTab;

		public ITab ActiveTab
		{
			get => _activeTab;
			set
			{
				if (_activeTab != value)
				{
					_activeTab = value;

					var clickedWidget = value as GuiWidget;

					foreach (var tab in AllTabs)
					{
						tab.TabContent.Visible = tab == clickedWidget;
					}

					this.OnActiveTabChanged();
				}
			}
		}

		// default to no movable tabs
		public int FirstMovableTab { get; set; } = int.MaxValue;

		public override GuiWidget AddChild(GuiWidget childToAdd, int indexInChildrenList = -1)
		{
			if (this.TabContainer == this)
			{
				return base.AddChild(childToAdd, indexInChildrenList);
			}
			else
			{
				return this.TabContainer.AddChild(childToAdd, indexInChildrenList);
			}
		}

		protected virtual void OnActiveTabChanged()
		{
			this.ActiveTabChanged?.Invoke(this, null);
		}

		internal int GetTabIndex(SimpleTab tab)
		{
			return AllTabs.IndexOf(tab);
		}
	}

	public class ChromeTabs : SimpleTabs
	{
		private TabTrailer tabTrailer;

		private GuiWidget leadingTabAdornment;

		public event EventHandler PlusClicked;

		public ChromeTabs(GuiWidget rightAnchorItem, ThemeConfig theme)
			: base(theme, null, rightAnchorItem)
		{
			// EBMC Edition: the space before the first tab holds the vertical rule after the brand button
			leadingTabAdornment = new GuiWidget()
			{
				MinimumSize = new Vector2((ClubTabStyle.RuleSpace * 2 + ClubTabStyle.LineWidth) * GuiWidget.DeviceScale, ClubTabStyle.TabHeight * GuiWidget.DeviceScale),
				VAnchor = VAnchor.Stretch
			};
			leadingTabAdornment.AfterDraw += (s, e) =>
			{
				e.Graphics2D.FillRectangle(ClubTabStyle.RuleRect(leadingTabAdornment.LocalBounds, GuiWidget.DeviceScale), ClubTabStyle.LineColor(theme));
			};
			this.TabBar.ActionArea.AddChild(leadingTabAdornment);

			this.TabBar.MouseMove += (s, e) =>
			{
				try
				{
					if (e?.DragFiles?.Count > 0
						&& e.DragFiles.Where(f => ApplicationController.ShellFileExtensions.Contains(Path.GetExtension(f).ToLower())).Any())
					{
						e.AcceptDrop = true;
					}
				}
				catch
                {
                }
			};

			TabBar.MouseEnterBounds += (s, e) =>
			{
                TabBar.SetActiveUiHint("You can drag and drop .mcx files here to open them");
			};

			this.TabBar.MouseUp += (s, e) =>
			{
				if (e?.DragFiles?.Count > 0
					&& e.DragFiles.Where(f => ApplicationController.ShellFileExtensions.Contains(Path.GetExtension(f).ToLower())).Any())
				{
					foreach (var file in e.DragFiles)
					{
						ApplicationController.Instance.MainView.OpenFile(file);
					}
				}
			};

			tabTrailer = new TabTrailer()
			{
				VAnchor = VAnchor.Bottom,
				MinimumSize = new Vector2(ClubTabStyle.NewTabLeadingSpace * GuiWidget.DeviceScale, ClubTabStyle.TabHeight * GuiWidget.DeviceScale),
			};

			this.TabBar.ActionArea.AddChild(tabTrailer);

			var plusTabButton = new NewTabButton(theme)
			{
				Height = ClubTabStyle.SquareButtonSize * GuiWidget.DeviceScale,
			};

			plusTabButton.IconButton.Click += (s, e) =>
			{
				this.PlusClicked?.Invoke(this, null);
			};

			this.TabBar.ActionArea.AddChild(plusTabButton);
		}

		public override void AddTab(GuiWidget tabWidget, int tabIndex = -1)
		{
			// Default position if tabIndex == -1 is just before the tabTrailer
			var widgetPosition = this.TabBar.ActionArea.Children.IndexOf(tabTrailer);
			var firstTabPosition = this.TabBar.ActionArea.Children.IndexOf(leadingTabAdornment) + 1;

			if (tabIndex != -1)
			{
				// Adjust position to be the head of the list + the tabIndex offset
				widgetPosition = firstTabPosition + tabIndex;
			}

			if (tabWidget is ChromeTab newTab)
			{
				newTab.TabContent.Visible = false;

				// Call AddTab(widget, int) in base explicitly
				base.AddTab(tabWidget, widgetPosition - firstTabPosition, widgetPosition);
			}
		}

		public void MoveTabRight(ITab tab)
		{
			var index = AllTabs.IndexOf(tab);
			var tabWidget = tab as GuiWidget;

			if (index >= FirstMovableTab
				&& index < AllTabs.Count() - 1)
			{
				TabBar.ActionArea.Children.Modify(list =>
				{
					var tabIndex = list.IndexOf(tabWidget);
					list.Remove(tabWidget);
					list.Insert(tabIndex + 1, tabWidget);
				});

				var savedIndex = index - 3;
				var moving = ApplicationController.Instance.Workspaces[savedIndex];
				ApplicationController.Instance.Workspaces.RemoveAt(savedIndex);
				ApplicationController.Instance.Workspaces.Insert(savedIndex + 1, moving);

				TabBar.ActionArea.PerformLayout();

				ActiveTab = tab;
			}
		}

		public void MoveTabLeft(ITab tab)
		{
			var index = AllTabs.IndexOf(tab);
			var tabWidget = tab as GuiWidget;

			if (index >= FirstMovableTab + 1
				&& index < AllTabs.Count())
			{
				TabBar.ActionArea.Children.Modify(list =>
				{
					var tabIndex = list.IndexOf(tabWidget);
					list.Remove(tabWidget);
					list.Insert(tabIndex - 1, tabWidget);
				});

				var savedIndex = index - 3;
				var moving = ApplicationController.Instance.Workspaces[savedIndex];
				ApplicationController.Instance.Workspaces.RemoveAt(savedIndex);
				ApplicationController.Instance.Workspaces.Insert(savedIndex - 1, moving);

				TabBar.ActionArea.PerformLayout();

				ActiveTab = tab;
			}
		}

		public Func<GuiWidget> NewTabPage { get; set; }
	}

	public class SimpleTab : GuiWidget, ITab
	{
		public event EventHandler CloseClicked;

		protected SimpleTabs parentTabControl;

		protected ThemeConfig theme;

		protected TabPill tabPill;

		public GuiWidget TabContent { get; protected set; }

		public string Key { get; set; }

		private bool hasClose = false;

		public SimpleTab(string tabKey, string tabLabel, SimpleTabs parentTabControl, GuiWidget tabContent, ThemeConfig theme, string tabImageUrl = null, bool hasClose = true, double pointSize = 12, ImageBuffer iconImage = null, TypeFace typeFace = null)
		{
			this.Key = tabKey;
			this.HAnchor = HAnchor.Fit;
			this.VAnchor = VAnchor.Fit | VAnchor.Bottom;
			this.Padding = 0;
			this.Margin = 0;
			this.theme = theme;
			this.hasClose = hasClose;

			this.TabContent = tabContent;
			this.parentTabControl = parentTabControl;

			if (iconImage != null)
			{
				tabPill = new TabPill(tabLabel, theme.TextColor, iconImage, pointSize, typeFace);
			}
			else
			{
				tabPill = new TabPill(tabLabel, theme.TextColor, tabImageUrl, pointSize, typeFace);
			}

			tabPill.Margin = hasClose ? new BorderDouble(right: 16) : 0;

			this.AddChild(tabPill);

			if (hasClose)
			{
				// var fadeRegion = new LeftClipFlowLayoutWidget();
				// fadeRegion.HAnchor |= HAnchor.Right;
				// this.AddChild(fadeRegion);

				var closeButton = this.CreateCloseButton();
				closeButton.Name = "Close Tab Button";
				closeButton.ToolTipText = "Close".Localize();
				closeButton.Click += (s, e) => ConditionallyCloseTab();
				closeButton.HAnchor |= HAnchor.Right;
				closeButton.VAnchor = VAnchor.Center;
				// fadeRegion.AddChild(closeButton);
				this.AddChild(closeButton);
			}
		}

		/// <summary>
		/// Creates the close button widget. Called from the constructor, after the theme is set.
		/// </summary>
		protected virtual GuiWidget CreateCloseButton()
		{
			var closeButton = theme.CreateSmallResetButton();
			closeButton.Margin = new BorderDouble(right: 7, top: 1);
			return closeButton;
		}

		public override void OnMouseDown(MouseEventArgs mouseEvent)
		{
			if(mouseEvent.Button == MouseButtons.Middle
				&& hasClose)
			{
				ConditionallyCloseTab();
			}

			base.OnMouseDown(mouseEvent);
		}

		public override void OnClosed(EventArgs e)
		{
			base.OnClosed(e);

			// Clear all listeners
			this.CloseClicked = null;
		}

		private void ConditionallyCloseTab()
		{
			UiThread.RunOnIdle(() =>
			{
				if (this.TabContent is PrinterTabPage printerTab
					&& printerTab.Printer.Connection.Printing)
				{
					StyledMessageBox.ShowMessageBox(
						(bool response) =>
						{
							if (response)
							{
								UiThread.RunOnIdle(() =>
								{
									this.parentTabControl.CloseTab(this);
									this.CloseClicked?.Invoke(this, null);
								});
							}
						},
						"Cancel the current print?".Localize(),
						"Cancel Print?".Localize(),
						StyledMessageBox.MessageType.YES_NO,
						"Cancel Print".Localize(),
						"Continue Printing".Localize());
				}
				else if (this.TabContent is DesignTabPage partTab // we are a part tap
					&& partTab?.Workspace?.SceneContext is ISceneContext sceneContext // we have a context
                    && sceneContext.Scene is InteractiveScene scene // we have a scene
                    && scene.HasUnsavedChanges // we have unsaved changes
                    && (sceneContext.EditContext.ContentStore != null || scene.Descendants().Count() > 0)) // we are not an unsaved empty tab (new with only deleting phil)
				{
					StyledMessageBox.ShowYNCMessageBox(
						(response) =>
						{
							switch (response)
							{
								case StyledMessageBox.ResponseType.YES:
									UiThread.RunOnIdle(async () =>
									{
										if (sceneContext.EditContext.ContentStore == null)
										{
											// If we are about to close a tab that has never been saved it will need a name before it can actually save
											// Open up the save as dialog rather than continue with saving and closing
											DialogWindow.Show(
												new SaveAsPage(
													(container, newName) =>
													{
														sceneContext.SaveAs(container, newName);
														// If we succeed at saveing the file go ahead and finish closing this tab
														this.CloseClicked?.Invoke(this, null);
														// Must be called after CloseClicked otherwise listeners are cleared before event is invoked
														this.parentTabControl.CloseTab(this);
													}));
										}
										else
										{
											await ApplicationController.Instance.Tasks.Execute("Saving Changes".Localize(), this, partTab.Workspace.SceneContext.SaveChanges);

											this.CloseClicked?.Invoke(this, null);
											// Must be called after CloseClicked otherwise listeners are cleared before event is invoked
											this.parentTabControl.CloseTab(this);
										}
									});
									break;

								case StyledMessageBox.ResponseType.NO:
									UiThread.RunOnIdle(() =>
									{
										this.CloseClicked?.Invoke(this, null);
										// Must be called after CloseClicked otherwise listeners are cleared before event is invoked
										this.parentTabControl?.CloseTab(this);
									});
									break;
							}
						},
						"Wolud you like to save changes before closing?".Localize(),
						"Save Changes?".Localize(),
						"Save Changes".Localize(),
						"Discard Changes".Localize(),
						"Cancel".Localize());
				}
				else
				{
					this.CloseClicked?.Invoke(this, null);
					// Must be called after CloseClicked otherwise listeners are cleared before event is invoked
					this.parentTabControl?.CloseTab(this);
				}
			});
		}

		public class TabPill : FlowLayoutWidget
		{
			private TextWidget label;
			private ImageWidget imageWidget;

			public TabPill(string tabTitle, Color textColor, string imageUrl = null, double pointSize = 12, TypeFace typeFace = null)
				: this(tabTitle, textColor, string.IsNullOrEmpty(imageUrl) ? null : new ImageBuffer(16, 16).CreateScaledImage(GuiWidget.DeviceScale), pointSize, typeFace)
			{
				if (imageWidget != null
					&& !string.IsNullOrEmpty(imageUrl))
				{
					try
					{
						// TODO: Use caching
						// Attempt to load image
						WebCache.RetrieveImageAsync(imageWidget.Image, imageUrl, true);
					}
					catch { }
				}
			}

			public TabPill(string tabTitle, Color textColor, ImageBuffer imageBuffer = null, double pointSize = 12, TypeFace typeFace = null)
			{
				this.Selectable = false;
				this.Padding = new BorderDouble(10, 5, 10, 4);

				if (imageBuffer != null)
				{
					imageWidget = new ImageWidget(imageBuffer)
					{
						Margin = new BorderDouble(right: 6, bottom: 2),
						VAnchor = VAnchor.Center
					};
					this.AddChild(imageWidget);
				}

				label = new TextWidget(tabTitle, pointSize: pointSize, typeFace: typeFace)
				{
					TextColor = textColor,
					VAnchor = VAnchor.Center,
					AutoExpandBoundsToText = true
				};
				this.AddChild(label);
			}

			public Color TextColor
			{
				get => label.TextColor;
				set => label.TextColor = value;
			}

			public override string Text
			{
				get => label.Text;
				set => label.Text = value;
			}
		}
	}

	public class ToolTab : SimpleTab
	{
		public Color InactiveTabColor { get; set; }

		public Color ActiveTabColor { get; set; }

		public override Color BorderColor
		{
			get =>  (this.IsActiveTab) ? theme.PrimaryAccentColor : base.BorderColor;
			set => base.BorderColor = value;
		}

		public ToolTab(string tabKey, string tabLabel, SimpleTabs parentTabControl, GuiWidget tabContent, ThemeConfig theme, string tabImageUrl = null, bool hasClose = true, int pointSize = -1)
			: base(tabKey, tabLabel, parentTabControl, tabContent, theme, tabImageUrl, hasClose, pointSize: (pointSize == -1) ? theme.FontSize10 : pointSize)
		{
			this.Border = new BorderDouble(top: 1);
			this.InactiveTabColor = Color.Transparent;
			this.ActiveTabColor = theme.BackgroundColor;

			tabPill.Padding = tabPill.Padding.Clone(top: 10, bottom: 10);
		}

		private bool IsActiveTab => this == parentTabControl.ActiveTab;

		public override string Text { get => tabPill.Text; set => tabPill.Text = value; }

		public override void OnDraw(Graphics2D graphics2D)
		{
			graphics2D.Render(
				new RoundedRect(this.LocalBounds, 0),
				(this.IsActiveTab) ? this.ActiveTabColor : this.InactiveTabColor);

			base.OnDraw(graphics2D);
		}
	}

	public class ChromeTab : SimpleTab
	{
		public ChromeTab(string tabKey, string tabLabel, SimpleTabs parentTabControl, GuiWidget tabContent, ThemeConfig theme, string tabImageUrl = null, bool hasClose = true)
			: base(tabKey, tabLabel, parentTabControl, tabContent, theme, tabImageUrl, hasClose, ClubTabStyle.LabelPointSize, typeFace: LabelTypeFace)
		{
			this.ApplyClubLayout(hasClose);
		}

		public ChromeTab(string tabKey, string tabLabel, SimpleTabs parentTabControl, GuiWidget tabContent, ThemeConfig theme, ImageBuffer imageBuffer, bool hasClose = true)
			: base(tabKey, tabLabel, parentTabControl, tabContent, theme, pointSize: ClubTabStyle.LabelPointSize, iconImage: imageBuffer, hasClose: hasClose, typeFace: LabelTypeFace)
		{
			this.Text = tabLabel;
			this.ApplyClubLayout(hasClose);
		}

		// EBMC Edition: "A. Club label" tabs - see ClubTabStyle for the design values
		private static TypeFace LabelTypeFace => ApplicationController.Instance.GetTypeFace(ClubTabStyle.LabelFontName);

		private void ApplyClubLayout(bool hasClose)
		{
			this.Padding = new BorderDouble(ClubTabStyle.TabPaddingX, 0, hasClose ? ClubTabStyle.ClosablePaddingRight : ClubTabStyle.TabPaddingX, 0);
			this.Margin = new BorderDouble(right: ClubTabStyle.TabGap);
			this.MinimumSize = new Vector2(0, ClubTabStyle.TabHeight * GuiWidget.DeviceScale);

			tabPill.Padding = 0;
			tabPill.VAnchor = VAnchor.Center | VAnchor.Fit;

			// Reserve the close button's space (label gap + circle) so the label never moves when it shows or hides
			tabPill.Margin = hasClose ? new BorderDouble(right: ClubTabStyle.CloseGap + ClubTabStyle.CloseButtonSize) : 0;
		}

		protected override GuiWidget CreateCloseButton()
		{
			// Shown on the active tab, and on an inactive tab while the pointer is over it
			return new ClubTabCloseButton(theme, () => this.IsActiveTab || this.IsHovered);
		}

		private bool IsActiveTab => parentTabControl != null && this == parentTabControl.ActiveTab;

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

        internal ChromeTab NextTab
		{
			get
			{
				var owner = this.Parent;
				if (owner != null)
				{
					var found = false;
					foreach(var item in owner.Children)
					{
						if (item == this)
						{
							found = true;
						}
						else if (found && item is ChromeTab chromeTab)
						{
							return chromeTab;
						}
					}

				}

				return null;
			}
		}

		public override void OnDraw(Graphics2D graphics2D)
		{
			if (this.Parent == null)
			{
				return;
			}

			bool isActive = this.IsActiveTab;
			bool isHovered = this.IsHovered;

			if (isActive)
			{
				// Same colour as the workspace below, with a line border on left/top/right and the accent along the top.
				// It is drawn over the bar's bottom line, so the line is interrupted under the active tab.
				ClubTabStyle.DrawActiveTab(graphics2D, this.LocalBounds, GuiWidget.DeviceScale, theme.BackgroundColor, ClubTabStyle.LineColor(theme), theme.PrimaryAccentColor);
			}
			else if (isHovered)
			{
				ClubTabStyle.DrawHoverTab(graphics2D, this.LocalBounds, GuiWidget.DeviceScale, ClubTabStyle.HoverFillColor(theme));
			}

			// Set before the children draw; TextColor only invalidates when it changes
			tabPill.TextColor = (isActive || isHovered) ? theme.TextColor : ClubTabStyle.MutedTextColor(theme);

			base.OnDraw(graphics2D);
		}

        public void HookupNameChange(PartWorkspace workspace)
        {
            throw new NotImplementedException();
        }

        public override void OnClosed(EventArgs e)
		{
			this.parentTabControl = null;
			this.TabContent = null;

			base.OnClosed(e);
		}

        public override string Text
		{
			get => tabPill?.Text;
			set
			{
				if (tabPill != null)
				{
					tabPill.Text = value;
				}
			}
		}

        public override string ToolTipText
		{
			get => tabPill?.ToolTipText;
			set
			{
				if (tabPill != null)
				{
					tabPill.ToolTipText = value;
				}
			}
		}

	}

	public static class TabsExtensions
	{
		public static int IndexOf<ITab>(this IEnumerable<ITab> source, ITab value)
		{
			int index = 0;
			var comparer = EqualityComparer<ITab>.Default; // or pass in as a parameter
			foreach (ITab item in source)
			{
				if (comparer.Equals(item, value)) return index;
				index++;
			}
			return -1;
		}
	}
}
