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
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.ClubUi;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl
{
	public class DialogPage : FlowLayoutWidget
	{
		protected GuiWidget headerRow;
		protected FlowLayoutWidget contentRow;
		protected FlowLayoutWidget footerRow;

		private TextWidget headerLabel;

		public GuiWidget CancelButton { get; private set; }

		public Vector2 WindowSize { get; set; }

		protected double labelFontSize = 12 * GuiWidget.DeviceScale;

		protected double errorFontSize = 10 * GuiWidget.DeviceScale;

		protected ThemeConfig theme;
		private int actionCount = 0;
		private SystemWindow systemWindow;
		private GuiWidget _acceptButton;

		// EBMC Edition (ClubDialogsAndMenus): the footer actions in the order they were added, for the club template
		private readonly System.Collections.Generic.List<(GuiWidget button, PageActionStyle style, bool highlighted)> clubActions
			= new System.Collections.Generic.List<(GuiWidget button, PageActionStyle style, bool highlighted)>();

		private GuiWidget severityTile;

		private DialogSeverity severity = DialogSeverity.None;

		public DialogPage(string cancelButtonText = null, bool useOverflowBar = false)
			: base(FlowDirection.TopToBottom)
		{
			theme = ApplicationController.Instance.Theme;

			this.HAnchor = HAnchor.Stretch;
			this.VAnchor = VAnchor.Stretch;

			if (cancelButtonText == null)
			{
				cancelButtonText = "Cancel".Localize();
			}

			CancelButton = theme.CreateDialogButton(cancelButtonText);
			CancelButton.Margin = new BorderDouble(left: 3);
			CancelButton.Name = "Cancel Wizard Button";

			// Create the header row for the widget
			if (useOverflowBar)
			{
				headerRow = new OverflowBar(theme)
				{
					Name = "HeaderRow",
					Margin = new BorderDouble(0, 3, 0, 0),
					Padding = new BorderDouble(0, 12),
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Fit
				};
			}
			else
			{
				headerRow = new FlowLayoutWidget(FlowDirection.LeftToRight)
				{
					Name = "HeaderRow",
					Margin = new BorderDouble(0, 3, 0, 0),
					Padding = new BorderDouble(0, 12),
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Fit
				};
			}

			this.AddChild(headerRow);

			headerLabel = new TextWidget("Setup Wizard".Localize(), pointSize: 24, textColor: theme.PrimaryAccentColor)
			{
				AutoExpandBoundsToText = true,
				EllipsisIfClipped = true,
				HAnchor = HAnchor.Stretch
			};
			headerRow.AddChild(headerLabel);

			// Create the main control container
			contentRow = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				Padding = new BorderDouble(10),
				BackgroundColor = theme.SectionBackgroundColor,
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch
			};
			this.AddChild(contentRow);

			// Create the footer (button) container
			footerRow = new FlowLayoutWidget(FlowDirection.LeftToRight)
			{
				Name = "FooterRow",
				HAnchor = HAnchor.Fit | HAnchor.Right,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(0, 6),
				Padding = new BorderDouble(top: 4, bottom: 2)
			};
			this.AddChild(footerRow);

#if !__ANDROID__
			headerRow.Padding = new BorderDouble(0, 3, 0, 3);

			headerLabel.PointSize = 14;
			headerLabel.TextColor = theme.TextColor;
			contentRow.Padding = new BorderDouble(5);

			footerRow.Padding = 0;
			footerRow.Margin = new BorderDouble(top: theme.DefaultContainerPadding);
#endif

			// EBMC Edition: the club dialog template (OemSettings.ClubDialogsAndMenus): a 15 pt bold title, the content on
			// the raised background with more room, the footer laid out in OnLoad
			if (ClubStyle.On)
			{
				// the label's own parent (an overflow header bar keeps its children in an inner row)
				var titleParent = headerLabel.Parent;
				int titleIndex = titleParent.Children.IndexOf(headerLabel);
				var titleText = headerLabel.Text;
				titleParent.RemoveChild(headerLabel);
				headerLabel.Close();
				headerLabel = ClubStyle.CreateTitle(theme, titleText);
				titleParent.AddChild(headerLabel, titleIndex);
				headerRow.Padding = new BorderDouble(0, 8, 0, 6);
				contentRow.Padding = new BorderDouble(14, 12);
			}
		}

		/// <summary>
		/// EBMC Edition (ClubDialogsAndMenus): when true, Enter presses the safe button (Cancel) instead of the primary
		/// action. A page with a Destructive action gets this automatically. Ignored with the switch off.
		/// </summary>
		public bool SafeDefault { get; set; }

		/// <summary>
		/// EBMC Edition (ClubDialogsAndMenus): the tile before the title. Ignored with the switch off.
		/// </summary>
		public DialogSeverity Severity
		{
			get => severity;
			set
			{
				severity = value;
				if (!ClubStyle.On)
				{
					return;
				}

				if (severityTile != null)
				{
					severityTile.Parent?.RemoveChild(severityTile);
					severityTile.Close();
					severityTile = null;
				}

				severityTile = ClubStyle.CreateSeverityTile(value);
				var titleParent = headerLabel.Parent;
				if (severityTile != null
					&& titleParent != null)
				{
					titleParent.AddChild(severityTile, titleParent.Children.IndexOf(headerLabel));
				}
			}
		}

		public GuiWidget AcceptButton
		{
			get => _acceptButton;
			set
			{
				_acceptButton = value;
				theme.ApplyPrimaryActionStyle(_acceptButton);
			}
		}

		// Add public accessor for content panel
		public FlowLayoutWidget ContentRow => contentRow;

		public DialogWindow DialogWindow { get; set; }

		public string WindowTitle { get; set; }

		public bool AlwaysOnTopOfMain { get; set; } = true;

		public string HeaderText
		{
			get => headerLabel.Text;
			set => headerLabel.Text = value;
		}

		public void AddPageAction(GuiWidget button, bool highlightFirstAction = true)
		{
			if (highlightFirstAction
				&& actionCount++ == 0)
			{
				this.AcceptButton = button;
			}

			button.Margin = theme.ButtonSpacing;
			footerRow.AddChild(button);

			clubActions.Add((button, PageActionStyle.Default, highlightFirstAction));
		}

		/// <summary>
		/// EBMC Edition: adds a footer action with a club style. With ClubDialogsAndMenus off this is the stock
		/// AddPageAction (Primary, Default and Destructive highlight as the first action did; the others do not). With it
		/// on, a Destructive action is never the Enter default (see ClubDialogRules.DefaultButton).
		/// </summary>
		public void AddPageAction(GuiWidget button, PageActionStyle style)
		{
			bool highlight = style == PageActionStyle.Default
				|| style == PageActionStyle.Primary
				|| style == PageActionStyle.Destructive;

			if (!ClubStyle.On)
			{
				this.AddPageAction(button, highlight);
				return;
			}

			if (style == PageActionStyle.Default)
			{
				this.AddPageAction(button, true);
				return;
			}

			if (style == PageActionStyle.Primary
				&& actionCount++ == 0)
			{
				this.AcceptButton = button;
			}

			button.Margin = theme.ButtonSpacing;
			footerRow.AddChild(button);

			clubActions.Add((button, style, highlight));
		}

		protected void SetCancelButtonName(string newName)
		{
			CancelButton.Name = newName;
		}

		protected void SetCancelButtonText(string text)
		{
			CancelButton.Text = text;
		}

		protected void HideCancelButton()
		{
			CancelButton.Visible = false;
		}

		public override void OnLoad(EventArgs args)
		{
			// Add 'Close' event listener after derived types have had a chance to register event listeners
			CancelButton.Click += (s, e) =>
			{
				if (this.AllowCancel())
				{
					this.DialogWindow?.ClosePage();
				}
			};

			// Register listeners
			systemWindow = this.Parents<SystemWindow>().FirstOrDefault();
			systemWindow.KeyDown += SystemWindow_KeyDown;

			footerRow.AddChild(CancelButton);

			if (ClubStyle.On)
			{
				this.ApplyClubFooter();
			}

			base.OnLoad(args);
		}

		/// <summary>
		/// EBMC Edition: styles the footer buttons, picks the Enter default (ClubDialogRules.DefaultButton) and, when the
		/// footer holds only the page's actions and Cancel, lays it out as [aside] … [secondary, cancel] [primary] above a
		/// hairline. Button actions are not touched.
		/// </summary>
		private void ApplyClubFooter()
		{
			var buttons = new System.Collections.Generic.List<GuiWidget>();
			var styles = new System.Collections.Generic.List<PageActionStyle>();
			var highlighted = new System.Collections.Generic.List<bool>();

			foreach (var (button, style, highlight) in clubActions)
			{
				if (button.Parent != footerRow)
				{
					continue;
				}

				buttons.Add(button);
				styles.Add(style);

				// a page that set AcceptButton itself keeps it as the highlighted action
				highlighted.Add(style == PageActionStyle.Default
					&& (_acceptButton != null && clubActions.Any(a => a.button == _acceptButton) ? button == _acceptButton : highlight));
			}

			if (CancelButton.Visible)
			{
				buttons.Add(CancelButton);
				styles.Add(PageActionStyle.Cancel);
				highlighted.Add(false);
			}

			var resolved = ClubDialogRules.ResolveStyles(styles, highlighted);

			if (this.SafeDefault
				|| resolved.Contains(PageActionStyle.Destructive))
			{
				int index = ClubDialogRules.DefaultButton(resolved, safeDefault: true);
				_acceptButton = index >= 0 ? buttons[index] : null;
			}

			for (int i = 0; i < buttons.Count; i++)
			{
				ClubStyle.ApplyButtonStyle(theme, buttons[i], resolved[i], isDefault: buttons[i] == _acceptButton);
			}

			// lay out only a footer this page did not arrange itself
			if (footerRow.Children.Count != buttons.Count
				|| footerRow.Children.Any(c => !buttons.Contains(c)))
			{
				return;
			}

			var order = ClubDialogRules.FooterOrder(resolved);
			int asides = ClubDialogRules.AsideCount(resolved);

			footerRow.RemoveChildren();
			for (int i = 0; i < order.Count; i++)
			{
				if (i == asides)
				{
					footerRow.AddChild(new HorizontalSpacer());
				}

				var button = buttons[order[i]];
				button.Margin = new BorderDouble(left: 8);

				// re-added after RemoveChildren (Debug builds refuse a removed widget otherwise)
				button.ClearRemovedFlag();
				footerRow.AddChild(button);
			}

			footerRow.HAnchor = HAnchor.Stretch;
			footerRow.Border = new BorderDouble(top: 1);
			footerRow.BorderColor = ClubStyle.Line(theme);
			footerRow.Padding = new BorderDouble(0, 2, 0, 10);
		}

		public bool AllowCancel()
		{
			bool windowAbortCancel = false;

			this.OnCancel(out bool pageAbortCancel);

			if (!pageAbortCancel)
			{
				this.DialogWindow.OnCancel(out windowAbortCancel);
			}

			// Allow cancel if page and DialogWindow do not abort
			return !pageAbortCancel
				&& !windowAbortCancel;
		}

		protected virtual void OnCancel(out bool abortCancel)
		{
			abortCancel = false;
		}

		public override void OnClosed(EventArgs e)
		{
			// Unregister listeners
			if (systemWindow != null)
			{
				systemWindow.KeyDown -= SystemWindow_KeyDown;
			}

			base.OnClosed(e);
		}

		private void SystemWindow_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Escape
				&& !e.Handled)
			{
				this.OnCancel(out bool abortCancel);

				if (!abortCancel)
				{
					this.DialogWindow?.ClosePage();
				}
			}
			else if (e.KeyCode == Keys.Enter
				&& this.AcceptButton != null
				&& this.AcceptButton.Visible
				&& this.AcceptButton.Enabled)
			{
				var tabStops = this.ActiveTabStops();

				// If no tab stop child is actively focused, fire click on the AcceptButton
				if (!tabStops.Any(w => w.Focused))
				{
					this.AcceptButton.InvokeClick();
				}
			}
		}
	}
}