using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// EBMC Edition (OemSettings.EnableClubMembers): one member on the Members tab: name, importance (None to three
	/// stars, one click each), role (one short line, with its badge), contact, date met, interests
	/// and machines as removable tags, free notes, the dated notes with an "Add note" row, Archive and Delete. Built
	/// again whenever it changes (never re-adds a widget). Text fields show what was typed (kept in the session) and are
	/// stored when the field is left; the buttons read the fields directly.
	/// </summary>
	public class MemberDetailPanel : ScrollableWidget
	{
		private static readonly Func<string, string> Localize = s => s.Localize();

		private readonly ThemeConfig theme;

		private readonly Member member;

		/// <summary>Called after a change: true when this panel should be built again (tags, notes, archive).</summary>
		private readonly Action<bool> changed;

		private readonly FlowLayoutWidget column;

		private readonly WrappedTextWidget errorText;

		/// <summary>The role badge beside the Role field, updated in place when the role is stored.</summary>
		private RoleBadge roleBadge;

		public MemberDetailPanel(ThemeConfig theme, Member member, Action<bool> changed)
			: base(true)
		{
			this.theme = theme;
			this.member = member;
			this.changed = changed;
			this.Name = "Member Detail Panel";
			this.ScrollArea.HAnchor = HAnchor.Stretch;

			column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Padding = new BorderDouble(12, 8),
			};
			this.AddChild(column);

			errorText = new WrappedTextWidget("", pointSize: theme.DefaultFontSize, textColor: Color.Red)
			{
				HAnchor = HAnchor.Stretch,
				Name = "Member Detail Error",
			};
			column.AddChild(errorText);

			AddTextRow("Name".Localize(), MemberField.Name, 280, "Member Name Field");
			AddImportanceRow();
			AddRoleRow();
			AddTextRow("Contact".Localize(), MemberField.Contact, 280, "Member Contact Field");
			AddMetOnRow();
			AddTags("Interests".Localize(), MemberField.InterestText, member.Interests, "Member Interests");
			AddTags("Machines".Localize(), MemberField.MachineText, member.Machines, "Member Machines");
			AddNotes();
			AddLog();
			AddActions();

			// a field typed earlier that did not pass the checks is shown again with its problem
			ShowProblem(Core.ProblemsFor(member.Id));
		}

		private static MembersSessionCore Core => MembersSession.Core;

		private double Scale => GuiWidget.DeviceScale;

		/// <summary>Shows the problems of the typed fields (or clears them). Never rebuilds a field.</summary>
		public void ShowProblem(ValidationResult result)
		{
			MembersTabPage.SetErrorText(errorText, result == null || result.IsValid ? "" : result.Message(Localize));
		}

		private void AddHeading(string text)
		{
			column.AddChild(new TextWidget(text, pointSize: theme.DefaultFontSize, textColor: theme.PrimaryAccentColor, bold: true)
			{
				HAnchor = HAnchor.Left,
				Margin = new BorderDouble(0, 2, 0, 10),
			});
		}

		private FlowLayoutWidget AddRow(string label)
		{
			// rows in the column are Fit only: a single Top/Center/Bottom anchor makes agg-sharp leave a child out when a
			// Fit parent measures its children, so the column (and the scroll area) would end above the last rows
			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(0, 3),
			};
			row.AddChild(new TextWidget(label, pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
			{
				VAnchor = VAnchor.Center,
				MinimumSize = new Vector2(90 * Scale, 0),
			});
			column.AddChild(row);
			return row;
		}

		/// <summary>A field that shows the typed draft (or the stored value), mirrors typing into the session at once
		/// and stores the value when it is left or Enter is pressed.</summary>
		private ThemedTextEditWidget MakeDraftField(MemberField field, double width, string name, bool multiLine = false, string empty = "", double pixelHeight = 0)
		{
			// pixelHeight: the editor's own height (a multi-line box is otherwise one line tall inside its frame)
			var edit = new ThemedTextEditWidget(Core.FieldText(member, field), theme, pixelWidth: width * Scale, pixelHeight: pixelHeight, multiLine: multiLine, messageWhenEmptyAndNotSelected: empty)
			{
				VAnchor = VAnchor.Center,
				Name = name,
			};

			var id = member.Id;
			edit.TextChanged += (s, e) => Core.Drafts.SetField(id, field, edit.Text);
			edit.ActualTextEditWidget.EditComplete += (s, e) => UiThread.RunOnIdle(Commit);
			return edit;
		}

		private void Commit()
		{
			if (this.HasBeenClosed || Core.State != MembersSessionState.Open)
			{
				return;
			}

			var result = Core.CommitDetailDrafts();
			ShowProblem(result);
			ShowStoredRole();
			changed(false);
		}

		/// <summary>One click per level: None, one, two or three stars; the current one is highlighted. A change is an
		/// ordinary edit, and the panel is built again to show it.</summary>
		private void AddImportanceRow()
		{
			var row = AddRow("Importance".Localize());
			int current = MemberImportanceStyle.Level(member.Importance);
			var buttons = new[]
			{
				MemberImportanceStyle.LevelButton(theme, 0, false, "None".Localize(), current == 0, "Member Importance None"),
				MemberImportanceStyle.LevelButton(theme, 1, false, null, current == 1, "Member Importance 1 Star"),
				MemberImportanceStyle.LevelButton(theme, 2, false, null, current == 2, "Member Importance 2 Stars"),
				MemberImportanceStyle.LevelButton(theme, 3, false, null, current == 3, "Member Importance 3 Stars"),
			};
			buttons[0].ToolTipText = "No importance".Localize();
			buttons[1].ToolTipText = "One star".Localize();
			buttons[2].ToolTipText = "Two stars".Localize();
			buttons[3].ToolTipText = "Three stars".Localize();

			for (int level = 0; level < buttons.Length; level++)
			{
				int chosen = level;
				buttons[level].Click += (s, e) => UiThread.RunOnIdle(() =>
				{
					if (this.HasBeenClosed || Core.State != MembersSessionState.Open)
					{
						return;
					}

					ShowProblem(Core.SetImportance(chosen));
					changed(true);
				});
				row.AddChild(buttons[level]);
			}
		}

		/// <summary>The role: one short line, typed like the other fields, with its badge as it shows in the list.</summary>
		private void AddRoleRow()
		{
			var row = AddRow("Role".Localize());
			row.AddChild(MakeDraftField(MemberField.Role, 200, "Member Role Field", empty: "for example Founder".Localize()));
			roleBadge = MemberImportanceStyle.MakeRoleBadge(theme, member.Role, member.Importance, "Member Role Badge");
			row.AddChild(roleBadge);
		}

		/// <summary>The badge shows the stored role (not the typed text): updated after the typed fields are stored.</summary>
		private void ShowStoredRole()
		{
			var stored = Core.Store?.FindMember(member.Id);
			if (stored != null && roleBadge != null && !roleBadge.HasBeenClosed)
			{
				roleBadge.Show(stored.Role, stored.Importance);
			}
		}

		private void AddTextRow(string label, MemberField field, double width, string name)
		{
			AddRow(label).AddChild(MakeDraftField(field, width, name));
		}

		private void AddMetOnRow()
		{
			var row = AddRow("Met on".Localize());
			var edit = MakeDraftField(MemberField.MetOn, 120, "Member Met On Field", empty: "yyyy-mm-dd");
			row.AddChild(edit);

			var today = new ThemedTextButton("Today".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(left: 6),
				Name = "Member Met On Today",
			};
			today.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				if (edit.HasBeenClosed)
				{
					return;
				}

				edit.Text = MembersRules.ToIso(Core.Today);
				Commit();
			});
			row.AddChild(today);
		}

		private void AddTags(string label, MemberField field, List<string> tags, string name)
		{
			AddHeading(label);

			if (tags.Count > 0)
			{
				var chips = new FlowLeftRightWithWrapping()
				{
					HAnchor = HAnchor.Stretch,
					Name = name + " Tags",
				};

				foreach (var tag in tags)
				{
					var chip = new ThemedTextButton(tag + "  ×", theme)
					{
						BackgroundColor = theme.SlightShade,
						Margin = new BorderDouble(0, 2, 6, 2),
						ToolTipText = "Remove".Localize(),
						Name = "Member Tag Chip",
					};
					var value = tag;
					chip.Click += (s, e) => UiThread.RunOnIdle(() =>
					{
						if (this.HasBeenClosed)
						{
							return;
						}

						ShowProblem(Core.RemoveTag(field, value));
						changed(true);
					});
					chips.AddChild(chip);
				}

				column.AddChild(chips);
			}

			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(0, 3),
			};
			column.AddChild(row);

			var id = member.Id;
			var edit = new ThemedTextEditWidget(Core.Drafts.Field(id, field) ?? "", theme, pixelWidth: 220 * Scale, messageWhenEmptyAndNotSelected: "Add, separated by commas".Localize())
			{
				VAnchor = VAnchor.Center,
				Name = name + " Add Field",
			};
			edit.TextChanged += (s, e) => Core.Drafts.SetField(id, field, edit.Text);
			row.AddChild(edit);

			var add = new ThemedTextButton("Add".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(left: 6),
				Name = name + " Add Button",
			};
			row.AddChild(add);

			void AddNow()
			{
				if (this.HasBeenClosed || edit.HasBeenClosed)
				{
					return;
				}

				var result = Core.AddTags(field, edit.Text);
				ShowProblem(result);
				if (result.IsValid)
				{
					changed(true);
				}
			}

			add.Click += (s, e) => UiThread.RunOnIdle(AddNow);
			edit.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(AddNow);
		}

		private void AddNotes()
		{
			AddHeading("Notes".Localize());
			var notes = MakeDraftField(MemberField.Notes, 320, "Member Notes Field", multiLine: true, pixelHeight: 80 * Scale);
			notes.HAnchor = HAnchor.Stretch;
			notes.VAnchor = VAnchor.Fit;
			notes.MinimumSize = new Vector2(0, 80 * Scale);
			column.AddChild(notes);
		}

		private void AddLog()
		{
			AddHeading("Log".Localize());

			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(0, 3),
			};
			column.AddChild(row);

			var id = member.Id;
			string Draft(MemberField field) => Core.Drafts.Field(id, field) ?? "";

			var date = new ThemedTextEditWidget(Draft(MemberField.NoteDate), theme, pixelWidth: 100 * Scale, messageWhenEmptyAndNotSelected: "today".Localize())
			{
				VAnchor = VAnchor.Center,
				Name = "Member Note Date Field",
			};
			date.TextChanged += (s, e) => Core.Drafts.SetField(id, MemberField.NoteDate, date.Text);
			row.AddChild(date);

			var text = new ThemedTextEditWidget(Draft(MemberField.NoteText), theme, pixelWidth: 260 * Scale, messageWhenEmptyAndNotSelected: "Note".Localize())
			{
				VAnchor = VAnchor.Center,
				Margin = new BorderDouble(left: 6),
				Name = "Member Note Text Field",
			};
			text.TextChanged += (s, e) => Core.Drafts.SetField(id, MemberField.NoteText, text.Text);
			row.AddChild(text);

			var add = new ThemedTextButton("Add note".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(left: 6),
				Name = "Member Add Note Button",
			};
			row.AddChild(add);

			void AddNow()
			{
				if (this.HasBeenClosed || text.HasBeenClosed)
				{
					return;
				}

				// read the fields directly: a field only reports its text when it loses focus
				var result = Core.AddNote(date.Text, text.Text);
				if (!result.Ok)
				{
					ShowProblem(result.Validation);
					return;
				}

				changed(true);
			}

			add.Click += (s, e) => UiThread.RunOnIdle(AddNow);
			text.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(AddNow);

			var entries = member.Log
				.OrderByDescending(entry => entry.Date, StringComparer.Ordinal)
				.ThenByDescending(entry => entry.CreatedUtc)
				.ToList();
			if (entries.Count == 0)
			{
				column.AddChild(new TextWidget("No notes yet.".Localize(), pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Left,
					Margin = new BorderDouble(0, 4),
				});
			}

			foreach (var entry in entries)
			{
				var line = new FlowLayoutWidget()
				{
					HAnchor = HAnchor.Stretch,
					VAnchor = VAnchor.Fit,
					Margin = new BorderDouble(0, 2),
				};
				line.AddChild(new TextWidget(entry.Date, pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor.WithAlpha(165))
				{
					VAnchor = VAnchor.Top,
					MinimumSize = new Vector2(90 * Scale, 0),
				});
				line.AddChild(new WrappedTextWidget(entry.Text, pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Stretch,
				});
				column.AddChild(line);
			}
		}

		private void AddActions()
		{
			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Left | HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(0, 6, 0, 14),
			};
			column.AddChild(row);

			var archive = new ThemedTextButton(member.Archived ? "Unarchive".Localize() : "Archive".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Name = "Member Archive Button",
			};
			archive.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				if (this.HasBeenClosed)
				{
					return;
				}

				Core.SetArchived(!member.Archived);
				changed(true);
			});
			row.AddChild(archive);

			var delete = new ThemedTextButton("Delete...".Localize(), theme)
			{
				VAnchor = VAnchor.Center,
				BackgroundColor = theme.SlightShade,
				Margin = new BorderDouble(left: 8),
				Name = "Member Delete Button",
			};
			var id = member.Id;
			delete.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				// the question names no one: the box is a separate window and may outlive this panel (a rebuild, or the
				// tab's Lock, which closes the list view). The delete is refused if the list was locked meanwhile; whichever
				// page is showing then hears of it through the session's change event, not through this panel
				int askedInLockGeneration = Core.LockGeneration;
				StyledMessageBox.ShowMessageBox(
					(confirmed) =>
					{
						if (confirmed
							&& MembersSession.ShowTab)
						{
							Core.Delete(id, askedInLockGeneration);
						}
					},
					"Delete this member and all their notes for good? This cannot be undone. Archive keeps them but hides them from the list.".Localize(),
					"Delete member?".Localize(),
					StyledMessageBox.MessageType.YES_NO,
					"Delete member".Localize(),
					"Keep".Localize(),
					new ClubMessageOptions()
					{
						YesLabel = "Delete member".Localize(),
						NoLabel = "Keep".Localize(),
						Destructive = true,
					});
			});
			row.AddChild(delete);
		}
	}
}
