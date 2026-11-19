using System;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// EBMC Edition (OemSettings.EnableClubMembers): the Members tab. When the list is open: a header with Lock and
	/// Change passphrase, a quick-add bar, a search box, Show archived and an importance filter (All, one star and up, two
	/// stars and up, three stars) with the list of people (higher importance first, then most recent activity) and the
	/// detail of the chosen member. When it is not: with no members files, a view to set the members passphrase and
	/// create the list; otherwise a view to enter the passphrase, or a plain view of why the list is not open and what
	/// can be done. Passphrase fields are masked, read once, passed on and cleared; they are never kept in the drafts.
	/// Everything else typed is kept in <see cref="MembersSession"/>, so the page can be rebuilt at any time (Lock,
	/// Simple/Advanced, theme) without losing it. The quick-add and search fields are never rebuilt while typing: only
	/// the list and the detail are.
	/// </summary>
	public class MembersTabPage : FlowLayoutWidget
	{
		private static readonly Func<string, string> Localize = s => s.Localize();

		private readonly ThemeConfig theme;

		private readonly FlowLayoutWidget noticeArea;

		private readonly GuiWidget body;

		private MembersSessionState? shownState;

		private MembersStore shownStore;

		/// <summary>Whether members files existed when the Locked view was built (Set view or Enter view).</summary>
		private bool shownHasFiles;

		/// <summary>An error from the view just replaced, shown in the new view's error line (for example "a list
		/// already exists" when another window created one while the Set view was showing).</summary>
		private string carriedError;

		private FlowLayoutWidget listColumn;

		private TextWidget countLine;

		private GuiWidget detailHost;

		private ThemedTextEditWidget quickName;

		private ThemedTextEditWidget quickNote;

		private TextWidget quickHint;

		private int searchGeneration;

		private int shownRevision;

		private MemberDetailPanel detailPanel;

		/// <summary>
		/// The tab's content. A failure while building it never stops the rest of the window (where Lock lives) from
		/// being built: the tab then shows a fixed sentence (never the error's text, which could quote data).
		/// </summary>
		public static GuiWidget Create(ThemeConfig theme)
		{
			try
			{
				return new MembersTabPage(theme);
			}
			catch (Exception)
			{
				return CouldNotShow(theme, "Members could not be shown. Restart the app to try again.");
			}
		}

		private static GuiWidget CouldNotShow(ThemeConfig theme, string sentence)
		{
			return new WrappedTextWidget(sentence.Localize(), pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Top | VAnchor.Fit,
				Margin = new BorderDouble(16, 12),
				BackgroundColor = theme.BackgroundColor,
				Name = "Members Not Shown",
			};
		}

		public MembersTabPage(ThemeConfig theme)
			: base(FlowDirection.TopToBottom)
		{
			this.theme = theme;
			this.Name = "Members Tab Page";
			this.HAnchor = HAnchor.Stretch;
			this.VAnchor = VAnchor.Stretch;
			this.BackgroundColor = theme.BackgroundColor;

			noticeArea = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			this.AddChild(noticeArea);

			body = new GuiWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch,
			};
			this.AddChild(body);

			Refresh();

			// subscribed only once the page is built: a page whose first build failed is never left listening
			EventHandler onChanged = (s, e) => UiThread.RunOnIdle(Refresh);
			MembersSession.Changed += onChanged;
			this.Closed += (s, e) => MembersSession.Changed -= onChanged;
		}

		private static MembersSessionCore Core => MembersSession.Core;

		private double Scale => GuiWidget.DeviceScale;

		private void Refresh()
		{
			if (this.HasBeenClosed || !MembersSession.Enabled)
			{
				return;
			}

			RefreshNotices();

			var state = Core.State;
			bool hasFiles = state == MembersSessionState.Locked && Core.HasMembersFiles;
			if (state != shownState
				|| Core.Store != shownStore
				|| (state == MembersSessionState.Locked && hasFiles != shownHasFiles))
			{
				shownState = state;
				shownStore = Core.Store;
				shownHasFiles = hasFiles;
				shownRevision = Core.Revision;
				body.CloseChildren();
				GuiWidget content;
				try
				{
					content = state == MembersSessionState.Open ? BuildOpen() : BuildStateView(state);
				}
				catch (Exception)
				{
					content = CouldNotShow(theme, "Members could not be shown. Restart the app to try again.");
				}

				carriedError = null;
				body.AddChild(content);
				return;
			}

			if (Core.Revision != shownRevision)
			{
				// changed from outside this page's own actions (a member deleted from a confirmation box)
				shownRevision = Core.Revision;
				RebuildList();
				RebuildDetail();
				return;
			}

			// typed fields saved after a pause: their problems are shown without rebuilding the field being typed in
			if (detailPanel != null && !detailPanel.HasBeenClosed)
			{
				detailPanel.ShowProblem(Core.ProblemsFor(Core.Drafts.SelectedMemberId));
			}
		}

		// --- notices ---

		private void RefreshNotices()
		{
			noticeArea.CloseChildren();

			// each fixed sentence is translated first; counts and file names are put in afterwards
			var lines = Core.Notices.Select(n => n.Render(Localize)).ToList();
			if (Core.SaveProblem != null)
			{
				lines.Insert(0, Core.SaveProblem.Localize());
			}

			if (lines.Count == 0)
			{
				return;
			}

			var panel = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				BackgroundColor = theme.MinimalShade,
				Padding = new BorderDouble(10, 6),
				Margin = new BorderDouble(6, 4, 6, 0),
				Name = "Members Storage Notice",
			};

			var text = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Center | VAnchor.Fit,
			};
			foreach (var line in lines)
			{
				text.AddChild(new WrappedTextWidget(line, pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Stretch,
				});
			}

			panel.AddChild(text);

			if (Core.Notices.Count > 0)
			{
				var dismiss = new ThemedTextButton("Dismiss".Localize(), theme)
				{
					VAnchor = VAnchor.Center,
					BackgroundColor = theme.SlightShade,
					Margin = new BorderDouble(left: 8),
					Name = "Members Notice Dismiss",
				};
				dismiss.Click += (s, e) => UiThread.RunOnIdle(Core.DismissNotice);
				panel.AddChild(dismiss);
			}

			noticeArea.AddChild(panel);
		}

		// --- the list and detail ---

		private GuiWidget BuildOpen()
		{
			var column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch,
			};

			column.AddChild(BuildHeader(column));
			column.AddChild(BuildQuickAdd());

			var splitter = new Splitter()
			{
				SplitterDistance = 300 * Scale,
				SplitterSize = theme.SplitterWidth,
				SplitterBackground = theme.SplitterBackground,
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch,
			};
			column.AddChild(splitter);

			var left = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch,
				Padding = new BorderDouble(6, 4),
			};
			splitter.Panel1.AddChild(left);

			var search = new ThemedTextEditWidget(Core.Drafts.SearchText ?? "", theme, messageWhenEmptyAndNotSelected: "Search members".Localize())
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Name = "Members Search Field",
			};
			search.TextChanged += (s, e) =>
			{
				// the field stays as it is; only the list is rebuilt, after a short pause
				Core.Drafts.SearchText = search.Text;
				int generation = ++searchGeneration;
				UiThread.RunOnIdle(
					() =>
					{
						if (generation == searchGeneration)
						{
							RebuildList();
						}
					},
					0.15);
			};
			left.AddChild(search);

			var archived = new CheckBox("Show archived".Localize(), theme.TextColor, theme.DefaultFontSize)
			{
				Checked = Core.Drafts.ShowArchived,
				Margin = new BorderDouble(0, 4),
				HAnchor = HAnchor.Left,
				Name = "Members Show Archived",
			};
			archived.CheckedStateChanged += (s, e) =>
			{
				Core.Drafts.ShowArchived = archived.Checked;
				RebuildList();
			};
			left.AddChild(archived);
			left.AddChild(BuildImportanceFilter());

			var scroll = new ScrollableWidget(true)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch,
			};
			scroll.ScrollArea.HAnchor = HAnchor.Stretch;
			left.AddChild(scroll);

			listColumn = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			scroll.AddChild(listColumn);

			countLine = new TextWidget("", pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor)
			{
				HAnchor = HAnchor.Left,
				Margin = new BorderDouble(0, 4),
			};
			left.AddChild(countLine);

			detailHost = splitter.Panel2;

			RebuildList();
			RebuildDetail();
			return column;
		}

		/// <summary>
		/// The open list's header: Lock and Change passphrase on the right. The change panel opens under it (page-local:
		/// three masked fields, read and cleared on every attempt, never put in the drafts).
		/// </summary>
		private GuiWidget BuildHeader(FlowLayoutWidget column)
		{
			var header = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Padding = new BorderDouble(6, 4, 6, 0),
				Name = "Members Header",
			};

			// Fit only, like every row in a top-to-bottom column
			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			header.AddChild(row);
			row.AddChild(new HorizontalSpacer());

			var changeButton = new ThemedTextButton("Change passphrase…".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(left: 6),
				Name = "Members Change Passphrase Button",
			};
			row.AddChild(changeButton);

			var lockButton = new ThemedTextButton("Lock".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(left: 6),
				ToolTipText = "Save the list and close it until the passphrase is entered again".Localize(),
				Name = "Members Lock Button",
			};
			row.AddChild(lockButton);

			var panel = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Padding = new BorderDouble(10, 8),
				Margin = new BorderDouble(0, 4, 0, 2),
				BackgroundColor = theme.MinimalShade,
				Visible = false,
				Name = "Members Change Passphrase Panel",
			};
			header.AddChild(panel);

			var currentField = AddPassphraseRow(panel, "Current passphrase".Localize(), "Members Current Passphrase Field");
			var newField = AddPassphraseRow(panel, "New passphrase".Localize(), "Members Change New Passphrase Field");
			var repeatField = AddPassphraseRow(panel, "Repeat the new passphrase".Localize(), "Members Change Repeat Passphrase Field");

			var error = NewErrorLine("Members Change Passphrase Error");

			var buttons = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Left | HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(top: 8),
			};
			panel.AddChild(buttons);
			panel.AddChild(error);

			var save = new ThemedTextButton("Save".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(right: 6),
				Name = "Members Change Passphrase Save Button",
			};
			buttons.AddChild(save);

			var cancel = new ThemedTextButton("Cancel".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(right: 6),
				Name = "Members Change Passphrase Cancel Button",
			};
			buttons.AddChild(cancel);

			void ClearFields()
			{
				foreach (var field in new[] { currentField, newField, repeatField })
				{
					ClearPassphrase(field);
				}
			}

			void ClosePanel()
			{
				ClearFields();
				SetErrorText(error, "");
				panel.Visible = false;
			}

			void Submit()
			{
				if (panel.HasBeenClosed || !panel.Visible)
				{
					return;
				}

				// read once, passed on and cleared: never kept
				var current = currentField.Text;
				var next = newField.Text;
				var repeat = repeatField.Text;
				ClearFields();

				var problem = MembersSession.ChangePassphrase(current, next, repeat);
				if (panel.HasBeenClosed)
				{
					return;
				}

				if (problem == null)
				{
					// the notice area says it was changed
					ClosePanel();
					return;
				}

				// refused because another window changed the list: the page moves to that view, which shows why
				if (ShownInAnotherView(error, problem))
				{
					return;
				}

				SetErrorText(error, problem);
				currentField.Focus();
			}

			changeButton.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				if (panel.HasBeenClosed)
				{
					return;
				}

				if (panel.Visible)
				{
					ClosePanel();
					return;
				}

				SetErrorText(error, "");
				panel.Visible = true;
				currentField.Focus();
			});

			save.Click += (s, e) => UiThread.RunOnIdle(Submit);
			cancel.Click += (s, e) => UiThread.RunOnIdle(ClosePanel);

			// Enter moves to the next field, and saves from the last one
			currentField.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(() =>
			{
				if (!newField.HasBeenClosed)
				{
					newField.Focus();
				}
			});
			newField.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(() =>
			{
				if (!repeatField.HasBeenClosed)
				{
					repeatField.Focus();
				}
			});
			repeatField.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(Submit);

			lockButton.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				if (this.HasBeenClosed || Core.State != MembersSessionState.Open)
				{
					return;
				}

				// nothing typed into the passphrase fields outlives the list view; the session's change event then shows
				// the Enter view in place of this one (the window is not rebuilt)
				ClearFields();
				MembersSession.Lock();
			});

			return header;
		}

		private GuiWidget BuildQuickAdd()
		{
			var bar = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Padding = new BorderDouble(6, 6, 6, 2),
			};

			// Fit only: with a Center anchor too, agg-sharp leaves the row out when the bar fits its height to its children
			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			bar.AddChild(row);

			quickName = new ThemedTextEditWidget(Core.Drafts.QuickAddName ?? "", theme, pixelWidth: 200 * Scale, messageWhenEmptyAndNotSelected: "Name".Localize())
			{
				VAnchor = VAnchor.Center,
				Name = "Members Quick Add Name",
			};
			row.AddChild(quickName);

			quickNote = new ThemedTextEditWidget(Core.Drafts.QuickAddNote ?? "", theme, pixelWidth: 300 * Scale, messageWhenEmptyAndNotSelected: "First note (optional)".Localize())
			{
				VAnchor = VAnchor.Center,
				Margin = new BorderDouble(left: 6),
				Name = "Members Quick Add Note",
			};
			row.AddChild(quickNote);

			var add = new ThemedTextButton("Add member".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(left: 6),
				Name = "Members Quick Add Button",
			};
			add.Click += (s, e) => UiThread.RunOnIdle(QuickAdd);
			row.AddChild(add);

			quickHint = new TextWidget("", pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor)
			{
				HAnchor = HAnchor.Left,
				Margin = new BorderDouble(2, 0, 0, 4),
				Name = "Members Quick Add Hint",
			};
			bar.AddChild(quickHint);

			// typing is mirrored into the session at once, so a rebuild of the window keeps it
			quickName.TextChanged += (s, e) =>
			{
				Core.Drafts.QuickAddName = quickName.Text;
				UpdateDuplicateHint();
			};
			quickNote.TextChanged += (s, e) => Core.Drafts.QuickAddNote = quickNote.Text;

			// Enter in either field adds
			quickName.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(QuickAdd);
			quickNote.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(QuickAdd);

			UpdateDuplicateHint();
			return bar;
		}

		private void UpdateDuplicateHint()
		{
			if (quickHint == null || quickHint.HasBeenClosed)
			{
				return;
			}

			var names = Core.PossibleDuplicateNames(quickName.Text);
			quickHint.Text = names.Count == 0
				? ""
				: "Possibly already in the list: {0}".Localize().FormatWith(string.Join(", ", names));
		}

		private void QuickAdd()
		{
			if (quickName == null || quickName.HasBeenClosed || Core.State != MembersSessionState.Open)
			{
				return;
			}

			// read the fields directly: a field only reports its text when it loses focus
			var result = Core.QuickAdd(quickName.Text, quickNote.Text);
			if (!result.Ok)
			{
				quickHint.Text = result.Validation.Message(Localize);
				quickName.Focus();
				return;
			}

			quickName.Text = "";
			quickNote.Text = "";
			quickHint.Text = "Added.".Localize();
			RebuildList();
			RebuildDetail();
			UiThread.RunOnIdle(() =>
			{
				if (!quickName.HasBeenClosed)
				{
					quickName.Focus();
				}
			});
		}

		/// <summary>
		/// The importance filter above the list: All, one star and up, two stars and up, three stars. It combines with the
		/// search and Show archived, and is a view setting (kept in the drafts for the session, never in the file).
		/// </summary>
		private GuiWidget BuildImportanceFilter()
		{
			// Fit only, like every row in a top-to-bottom column
			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Left | HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(0, 0, 0, 4),
				Name = "Members Importance Filter",
			};

			var buttons = new[]
			{
				MemberImportanceStyle.LevelButton(theme, 0, false, "All".Localize(), false, "Members Filter All"),
				MemberImportanceStyle.LevelButton(theme, 1, true, null, false, "Members Filter 1 Star And Up"),
				MemberImportanceStyle.LevelButton(theme, 2, true, null, false, "Members Filter 2 Stars And Up"),
				MemberImportanceStyle.LevelButton(theme, 3, false, null, false, "Members Filter 3 Stars"),
			};
			buttons[0].ToolTipText = "Show every member".Localize();
			buttons[1].ToolTipText = "Show members with one star or more".Localize();
			buttons[2].ToolTipText = "Show members with two stars or more".Localize();
			buttons[3].ToolTipText = "Show members with three stars".Localize();

			void ShowCurrent()
			{
				for (int level = 0; level < buttons.Length; level++)
				{
					MemberImportanceStyle.SetCurrent(theme, buttons[level], level == Core.Drafts.MinImportance);
				}
			}

			for (int level = 0; level < buttons.Length; level++)
			{
				int chosen = level;
				buttons[level].Click += (s, e) => UiThread.RunOnIdle(() =>
				{
					if (this.HasBeenClosed || Core.State != MembersSessionState.Open)
					{
						return;
					}

					Core.Drafts.MinImportance = chosen;
					ShowCurrent();
					RebuildList();
				});
				row.AddChild(buttons[level]);
			}

			ShowCurrent();
			return row;
		}

		private void RebuildList()
		{
			if (listColumn == null || listColumn.HasBeenClosed || Core.State != MembersSessionState.Open)
			{
				return;
			}

			listColumn.CloseChildren();
			var members = Core.ListMembers(out int total);
			if (members.Count == 0)
			{
				listColumn.AddChild(new WrappedTextWidget(
					Core.EmptyListMessage().Localize(),
					pointSize: theme.DefaultFontSize - 1,
					textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Stretch,
					Margin = new BorderDouble(4, 8),
				});
			}

			foreach (var member in members)
			{
				listColumn.AddChild(BuildRow(member));
			}

			countLine.Text = total > members.Count
				? "Showing {0} of {1}: narrow the search to see the rest.".Localize().FormatWith(members.Count, total)
				: "";
		}

		private GuiWidget BuildRow(Member member)
		{
			bool selected = member.Id == Core.Drafts.SelectedMemberId;
			var row = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Padding = new BorderDouble(8, 5),
				Margin = new BorderDouble(0, 1),
				Cursor = Cursors.Hand,
				BackgroundColor = selected ? theme.AccentMimimalOverlay : Color.Transparent,
				Name = "Members Row",
			};

			int level = MemberImportanceStyle.Level(member.Importance);
			var nameText = new TextWidget(member.Name, pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
			{
				HAnchor = HAnchor.Left,
				Selectable = false,
			};

			if (level == 0 && string.IsNullOrEmpty(member.Role))
			{
				// exactly as before importance existed
				row.AddChild(nameText);
			}
			else
			{
				// the name, its stars and the role badge on one line (Fit only, like every row in a top-to-bottom column)
				var title = new FlowLayoutWidget()
				{
					HAnchor = HAnchor.Left | HAnchor.Fit,
					VAnchor = VAnchor.Fit,
					Selectable = false,
					Name = "Members Row Title",
				};
				nameText.HAnchor = HAnchor.Absolute;
				nameText.VAnchor = VAnchor.Center;
				title.AddChild(nameText);
				if (level > 0)
				{
					title.AddChild(new ImportanceStars(theme, level, false, theme.DefaultFontSize - 2)
					{
						VAnchor = VAnchor.Center,
						Margin = new BorderDouble(left: 6),
						OnAccent = selected,
						Name = "Members Row Stars",
					});
				}

				if (!string.IsNullOrEmpty(member.Role))
				{
					title.AddChild(MemberImportanceStyle.MakeRoleBadge(theme, member.Role, level, "Members Row Role"));
				}

				row.AddChild(title);
			}

			if (level > 0)
			{
				// a narrow stripe in the level colour at the left edge (inside the row's padding)
				var stripe = MemberImportanceStyle.LevelColor(theme, level);
				row.AfterDraw += (s, e) =>
				{
					var bounds = row.LocalBounds;
					e.Graphics2D.FillRectangle(new RectangleDouble(bounds.Left, bounds.Bottom, bounds.Left + MemberImportanceStyle.StripeWidth * Scale, bounds.Top), stripe);
				};
			}

			var activity = MembersRules.ToIso(MembersSearch.LastActivity(member));
			var second = member.Archived ? "Archived · {0}".Localize().FormatWith(activity) : activity;
			row.AddChild(new TextWidget(second, pointSize: theme.DefaultFontSize - 2, textColor: theme.TextColor.WithAlpha(165))
			{
				HAnchor = HAnchor.Left,
				Selectable = false,
			});

			var id = member.Id;
			row.Click += (s, e) => UiThread.RunOnIdle(() => SelectMember(id));
			return row;
		}

		private void SelectMember(string id)
		{
			if (this.HasBeenClosed || Core.State != MembersSessionState.Open)
			{
				return;
			}

			Core.Select(id);
			RebuildList();
			RebuildDetail();
		}

		private void RebuildDetail()
		{
			if (detailHost == null || detailHost.HasBeenClosed || Core.State != MembersSessionState.Open)
			{
				return;
			}

			detailHost.CloseChildren();
			detailPanel = null;
			var member = Core.Drafts.SelectedMemberId == null ? null : Core.Store.FindMember(Core.Drafts.SelectedMemberId);
			if (member == null)
			{
				detailHost.AddChild(new WrappedTextWidget("Choose a member on the left, or add one above.".Localize(), pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Top | VAnchor.Fit,
					Margin = new BorderDouble(12),
				});
				return;
			}

			try
			{
				detailPanel = new MemberDetailPanel(theme, member, OnDetailChanged)
				{
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Stretch,
				};
				detailHost.AddChild(detailPanel);
			}
			catch (Exception)
			{
				detailPanel = null;
				detailHost.CloseChildren();
				detailHost.AddChild(CouldNotShow(theme, "This member could not be shown."));
			}
		}

		/// <summary>The detail changed something: the list always, the detail itself when asked (log, tags).</summary>
		private void OnDetailChanged(bool rebuildDetail)
		{
			if (this.HasBeenClosed)
			{
				return;
			}

			RebuildList();
			if (rebuildDetail)
			{
				RebuildDetail();
			}
		}

		// --- when the list is not open ---

		private GuiWidget BuildStateView(MembersSessionState state)
		{
			var column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit | VAnchor.Top,
				Padding = new BorderDouble(16, 12),
				Name = "Members State View",
			};

			column.AddChild(new TextWidget("Members".Localize(), pointSize: theme.DefaultFontSize + 2, textColor: theme.TextColor, bold: true)
			{
				HAnchor = HAnchor.Left,
				Margin = new BorderDouble(bottom: 8),
			});

			void Say(string text)
			{
				column.AddChild(new WrappedTextWidget(text.Localize(), pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Stretch,
					Margin = new BorderDouble(bottom: 6),
				});
			}

			if (state == MembersSessionState.Locked && !Core.HasMembersFiles)
			{
				return BuildSetView(column, Say);
			}

			bool offerRestore = false;
			bool offerPrevious = false;
			string retryLabel = "Try again".Localize();
			string retryName = "Members Retry Button";
			switch (state)
			{
				case MembersSessionState.Unreadable:
					Say(MembersSessionText.StateUnreadable);

					// also when only daily backups are left (the main file and the previous copy are gone)
					offerRestore = Core.NewestBackup != null;
					offerPrevious = Core.HasPreviousCopy;
					if (offerRestore || offerPrevious)
					{
						Say(MembersSessionText.RestoreHelp);
					}

					break;

				case MembersSessionState.NewerVersion:
					Say(MembersSessionText.StateNewerVersion);
					break;

				case MembersSessionState.Busy:
					Say(MembersSessionText.StateBusy);
					break;

				case MembersSessionState.ChangedElsewhere:
					Say(MembersSessionText.StateChangedElsewhere);
					retryLabel = "Load again".Localize();
					if (Core.OfferRestorePrevious && Core.HasPreviousCopy)
					{
						Say(MembersSessionText.RestorePreviousOffer);
						offerPrevious = true;
					}

					break;

				default:
					// Locked with a members list on disk: the Enter view
					Say(MembersSessionText.StateNotOpen);
					retryLabel = "Open".Localize();
					retryName = "Members Open Button";
					break;
			}

			var passField = AddPassphraseRow(column, "Passphrase".Localize(), "Members Passphrase Field");
			ThemedPasswordTextEditWidget filePassField = null;
			if (offerRestore || offerPrevious)
			{
				filePassField = AddPassphraseRow(column, "Passphrase the copy was saved with, if different".Localize(), "Members File Passphrase Field");
			}

			var error = NewErrorLine("Members State Error");
			SetErrorText(error, carriedError);

			// Fit only, like every row in a top-to-bottom column
			var buttons = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Left | HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(top: 8),
			};
			column.AddChild(buttons);
			column.AddChild(error);

			void Run(Func<string, string, string> action)
			{
				if (passField.HasBeenClosed)
				{
					return;
				}

				// the passphrases are read from the fields, passed on and cleared; they are not kept
				var pass = passField.Text;
				var filePass = filePassField?.Text ?? "";
				ClearPassphrase(passField);
				ClearPassphrase(filePassField);

				var before = Core.State;
				var problem = action(pass, filePass);
				if (problem == null && Core.State == before && before != MembersSessionState.Open)
				{
					// the passphrase was taken but the list is still not open: say so (the view stays as it is)
					problem = MembersSessionText.StillNotOpen.Localize();
				}

				if (ShownInAnotherView(error, problem))
				{
					return;
				}

				SetErrorText(error, problem);
				if (problem != null)
				{
					passField.Focus();
				}
			}

			void AddButton(string label, string name, Func<string, string, string> action)
			{
				var button = new ThemedTextButton(label, theme)
				{
					VAnchor = VAnchor.Center,
					BackgroundColor = theme.SlightShade,
					Margin = new BorderDouble(right: 6),
					Name = name,
				};
				button.Click += (s, e) => UiThread.RunOnIdle(() => Run(action));
				buttons.AddChild(button);
			}

			AddButton(retryLabel, retryName, (pass, filePass) => MembersSession.Open(pass));
			if (offerRestore)
			{
				AddButton("Restore newest backup".Localize(), "Members Restore Backup Button", (pass, filePass) => MembersSession.RestoreNewestBackup(pass, filePass));
			}

			if (offerPrevious)
			{
				AddButton("Restore previous copy".Localize(), "Members Restore Previous Button", (pass, filePass) => MembersSession.RestorePrevious(pass, filePass));
			}

			// Enter in either field does what the first button does (never a restore)
			passField.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(() => Run((pass, filePass) => MembersSession.Open(pass)));
			if (filePassField != null)
			{
				filePassField.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(() => Run((pass, filePass) => MembersSession.Open(pass)));
			}

			return column;
		}

		/// <summary>No members files: choose the members passphrase (typed twice) and create the list.</summary>
		private GuiWidget BuildSetView(FlowLayoutWidget column, Action<string> say)
		{
			say(MembersSessionText.NoListYet);
			say(MembersSessionText.ForgottenPassphraseWarning);

			var newField = AddPassphraseRow(column, "New passphrase".Localize(), "Members New Passphrase Field");
			var repeatField = AddPassphraseRow(column, "Repeat the passphrase".Localize(), "Members Repeat Passphrase Field");
			column.AddChild(new TextWidget("At least {0} characters.".Localize().FormatWith(MembersRules.PassphraseMinLength), pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor.WithAlpha(165))
			{
				HAnchor = HAnchor.Left,
				Margin = new BorderDouble(0, 2, 0, 0),
			});

			var error = NewErrorLine("Members Create Error");
			SetErrorText(error, carriedError);

			var buttons = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Left | HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(top: 8),
			};
			column.AddChild(buttons);
			column.AddChild(error);

			var create = new ThemedTextButton("Create members list".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Name = "Members Create List Button",
			};
			buttons.AddChild(create);

			void Create()
			{
				if (newField.HasBeenClosed)
				{
					return;
				}

				// read once, passed on and cleared: never kept
				var pass = newField.Text;
				var repeat = repeatField.Text;
				ClearPassphrase(newField);
				ClearPassphrase(repeatField);

				var problem = MembersSession.CreateList(pass, repeat);
				if (ShownInAnotherView(error, problem))
				{
					return;
				}

				SetErrorText(error, problem);
				if (problem != null)
				{
					newField.Focus();
				}
			}

			create.Click += (s, e) => UiThread.RunOnIdle(Create);
			newField.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(() =>
			{
				if (!repeatField.HasBeenClosed)
				{
					repeatField.Focus();
				}
			});
			repeatField.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(Create);

			return column;
		}

		/// <summary>
		/// After an action on a state view: rebuilds the page now if the view no longer fits (the state changed, or
		/// members files appeared or went away, which raises no session event), carrying <paramref name="problem"/> into
		/// the new view's error line. True when the view was replaced (<paramref name="error"/> is closed).
		/// </summary>
		private bool ShownInAnotherView(WrappedTextWidget error, string problem)
		{
			carriedError = problem;
			Refresh();
			carriedError = null;
			return error.HasBeenClosed;
		}

		private WrappedTextWidget NewErrorLine(string name)
		{
			return new WrappedTextWidget("", pointSize: theme.DefaultFontSize, textColor: Color.Red)
			{
				HAnchor = HAnchor.Stretch,
				Margin = new BorderDouble(top: 6),
				Name = name,
			};
		}

		/// <summary>
		/// Sets or clears a red error line (every error line on the tab and in the member detail goes through here).
		/// Workaround: WrappedTextWidget keeps showing its old text when given an empty one (agg-sharp AdjustTextWrap
		/// returns early on empty text), so the inner text is cleared too. The real fix is in WrappedTextWidget itself.
		/// </summary>
		internal static void SetErrorText(WrappedTextWidget line, string text)
		{
			if (line == null || line.HasBeenClosed)
			{
				return;
			}

			line.Text = text ?? "";
			if (string.IsNullOrEmpty(text))
			{
				line.TextWidget.Text = "";
			}
		}

		/// <summary>
		/// Empties a passphrase field and its undo history (otherwise Cmd+Z in the field brings the typed text back).
		/// Every passphrase field on the tab is cleared through here.
		/// </summary>
		private static void ClearPassphrase(ThemedPasswordTextEditWidget field)
		{
			if (field == null || field.HasBeenClosed)
			{
				return;
			}

			field.Text = "";
			field.ActualTextEditWidget.ClearUndoHistory();
		}

		/// <summary>A masked field with its label, as a Fit-only row of <paramref name="parent"/>. Copy and cut from
		/// the keyboard are refused in it (the text behind the dots never reaches the clipboard that way).</summary>
		private ThemedPasswordTextEditWidget AddPassphraseRow(GuiWidget parent, string label, string name)
		{
			// Fit only: a single Top/Center/Bottom anchor makes agg-sharp leave the row out when a Fit column measures
			// its children
			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(0, 3),
			};
			row.AddChild(new TextWidget(label, pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
			{
				VAnchor = VAnchor.Center,
				MinimumSize = new Vector2(260 * Scale, 0),
			});
			var field = new ThemedPasswordTextEditWidget("", theme, pixelWidth: 240 * Scale)
			{
				VAnchor = VAnchor.Center,
				Name = name,
			};

			// raised before the editor acts on the key; marked handled, the editor skips its copy and cut
			field.ActualTextEditWidget.InternalTextEditWidget.KeyDown += (s, e) =>
			{
				if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.X))
				{
					e.Handled = true;
					e.SuppressKeyPress = true;
				}
			};
			row.AddChild(field);
			parent.AddChild(row);
			return field;
		}
	}
}
