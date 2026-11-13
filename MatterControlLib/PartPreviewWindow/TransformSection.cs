using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.CustomWidgets;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.VectorMath;
using org.mariuszgromada.math.mxparser;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: the typed "Transform" section at the top of the properties panel (Advanced mode,
	/// OemSettings.EnableAdvancedTools): Centre X, Centre Y and Bottom Z, Size X/Y/Z with Keep proportions, and Rotate by
	/// X/Y/Z, in world mm and degrees for the whole selection. Each entry is one undo step through TransformCommand (the
	/// same command the handles and the model size prompt use); the maths is TransformRules. The fields follow the part
	/// while it is dragged. Fields accept arithmetic ("20+5"); empty or invalid input, and a zero or negative size, are
	/// ignored and the field shows the current value again.
	/// </summary>
	public class TransformSection : SectionWidget
	{
		public const string ExpandedKey = "ebmc_transform_section_expanded";

		private readonly ISceneContext sceneContext;

		private readonly IObject3D item;

		private readonly Dictionary<TransformField, ThemedTextEditWidget> fields = new Dictionary<TransformField, ThemedTextEditWidget>();

		private bool refreshQueued;

		/// <summary>Narrowest a field gets: "1234.56" at the default font with the field's padding and border.</summary>
		private const double MinimumFieldWidth = 62;

		private CheckBox keepProportions;

		// EBMC review fixes: the reason the fields are off (a part inside a group) and the "scales evenly" note
		private TextWidget nestedNote;

		private TextWidget rotatedNote;

		private TransformSection(ISceneContext sceneContext, IObject3D item, ThemeConfig theme, FlowLayoutWidget content)
			: base("Transform".Localize(), content, theme, serializationKey: ExpandedKey, defaultExpansion: true)
		{
			this.sceneContext = sceneContext;
			this.item = item;
			this.Name = "Transform Section";

			content.HAnchor = HAnchor.Stretch;
			content.Padding = new BorderDouble(theme.DefaultContainerPadding, 4);

			content.AddChild(CreateRow(theme, "Position".Localize(), "mm", new[]
			{
				(TransformField.CenterX, "Centre X".Localize()),
				(TransformField.CenterY, "Centre Y".Localize()),
				(TransformField.BottomZ, "Bottom Z".Localize()),
			}));

			content.AddChild(CreateRow(theme, "Size".Localize(), "mm", new[]
			{
				(TransformField.SizeX, "X"),
				(TransformField.SizeY, "Y"),
				(TransformField.SizeZ, "Z"),
			}));

			keepProportions = new CheckBox("Keep proportions".Localize(), theme.TextColor, theme.FontSize9)
			{
				Checked = true,
				Margin = new BorderDouble(left: 4, bottom: 4),
				ToolTipText = "Change all three sizes together".Localize(),
				Name = "Transform Keep Proportions",
			};
			content.AddChild(keepProportions);

			rotatedNote = new TextWidget(TransformRules.RotatedScalesEvenlyMessage.Localize(), pointSize: theme.FontSize8, textColor: theme.TextColor.WithAlpha(150))
			{
				Margin = new BorderDouble(4, 2),
				Visible = false,
				Name = "Transform Rotated Note",
			};
			content.AddChild(rotatedNote);
			keepProportions.CheckedStateChanged += (s, e) => Refresh();

			content.AddChild(CreateRow(theme, "Rotate by".Localize(), "°", new[]
			{
				(TransformField.RotateX, "X"),
				(TransformField.RotateY, "Y"),
				(TransformField.RotateZ, "Z"),
			}));

			if (IsParametric(item))
			{
				content.AddChild(new WrappedTextWidget(
					"Size scales this part as a whole; its own size fields below keep their values. The corner handles change those fields instead.".Localize(),
					pointSize: theme.FontSize8,
					textColor: theme.TextColor.WithAlpha(150))
				{
					HAnchor = HAnchor.Stretch,
					Margin = new BorderDouble(4, 4, 4, 2),
				});
			}

			nestedNote = new TextWidget(TransformRules.NestedSelectionMessage.Localize(), pointSize: theme.FontSize8, textColor: theme.TextColor.WithAlpha(150))
			{
				Margin = new BorderDouble(4, 4, 4, 2),
				Visible = false,
				Name = "Transform Nested Note",
			};
			content.AddChild(nestedNote);

			sceneContext.Scene.Invalidated += Scene_Invalidated;
			sceneContext.Scene.SelectionChanged += Scene_Invalidated;

			Refresh();
		}

		/// <summary>
		/// The selection is a direct child of the scene (a part, a group or a multi-selection). The maths works in scene
		/// space, so a part inside a group is not edited here.
		/// </summary>
		private bool AtSceneRoot => sceneContext.Scene.Children.Contains(item);

		/// <summary>Every mesh of the selection is axis aligned in the world (no turn other than multiples of 90°).</summary>
		private bool IsAxisAligned()
		{
			foreach (var meshItem in item.DescendantsAndSelf().Where(d => d.Mesh != null))
			{
				var m = meshItem.WorldMatrix();
				if (!TransformRules.IsAxisAligned(
					m.Row0.X, m.Row0.Y, m.Row0.Z,
					m.Row1.X, m.Row1.Y, m.Row1.Z,
					m.Row2.X, m.Row2.Y, m.Row2.Z,
					1e-4))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Sets a field's text and makes it the field's starting text, so losing focus does not commit it again.</summary>
		private static void SetFieldText(ThemedTextEditWidget edit, string text)
		{
			if (edit.Text != text)
			{
				edit.Text = text;

				// agg-sharp scrolls the field only when the caret moves; a shorter text set from code kept the old scroll
				// and showed only its last digits ("04" for 10.04), so scroll back to the start
				edit.ActualTextEditWidget.TopLeftOffset = Vector2.Zero;
			}

			edit.ActualTextEditWidget.InternalTextEditWidget.MarkAsStartingState();
		}

		/// <summary>The section for <paramref name="item"/>, or null when the Advanced tools are off or not in Advanced mode.</summary>
		public static TransformSection Create(ISceneContext sceneContext, IObject3D item, ThemeConfig theme)
		{
			if (!AdvancedTools.InAdvancedMode
				|| sceneContext?.Scene == null
				|| item == null)
			{
				return null;
			}

			var content = new FlowLayoutWidget(FlowDirection.TopToBottom);
			return new TransformSection(sceneContext, item, theme, content);
		}

		public override void OnClosed(EventArgs e)
		{
			sceneContext.Scene.Invalidated -= Scene_Invalidated;
			sceneContext.Scene.SelectionChanged -= Scene_Invalidated;
			base.OnClosed(e);
		}

		/// <summary>
		/// The object's own parameters set its size (primitives, solids, club shapes): their corner handles edit those
		/// parameters, while this section scales the matrix.
		/// </summary>
		private static bool IsParametric(IObject3D item)
		{
			return item is IObject3DControlsProvider
				&& (item is PrimitiveObject3D
					|| item is DesignTools.Solid.SolidObject3D
					|| item is DesignTools.ClubShapes.ClubShapeObject3D);
		}

		private GuiWidget CreateRow(ThemeConfig theme, string label, string unit, (TransformField field, string label)[] cells)
		{
			var row = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				Margin = new BorderDouble(0, 3),
			};

			row.AddChild(new TextWidget(label + " (" + unit + ")", pointSize: theme.FontSize9, textColor: theme.TextColor.WithAlpha(180))
			{
				Margin = new BorderDouble(4, 2),
			});

			var cellsRow = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
			};
			row.AddChild(cellsRow);

			foreach (var (field, cellLabel) in cells)
			{
				var cell = new FlowLayoutWidget(FlowDirection.TopToBottom)
				{
					HAnchor = HAnchor.Stretch,
					Margin = new BorderDouble(4, 0),
				};

				cell.AddChild(new TextWidget(cellLabel, pointSize: theme.FontSize8, textColor: theme.TextColor.WithAlpha(150)));

				var edit = new ThemedTextEditWidget("", theme, pixelWidth: 60 * DeviceScale)
				{
					HAnchor = HAnchor.Stretch,
					Name = "Transform " + field,
					ToolTipText = Tooltip(field),
				};

				// a click into the field selects its value, so typing replaces it (without this a click left the caret at
				// the end and the typed digits were appended to the old value)
				edit.ActualTextEditWidget.InternalTextEditWidget.SelectAllOnFocus = true;

				// a triple click selects the whole value too (agg-sharp's double click selects only the digits between dots)
				var internalEdit = edit.ActualTextEditWidget.InternalTextEditWidget;
				internalEdit.MouseDown += (s, mouseEvent) =>
				{
					if (mouseEvent.Clicks >= 3)
					{
						internalEdit.SelectAll();
					}
				};

				// the text area uses the whole cell (wide enough for "1234.56")
				edit.ActualTextEditWidget.HAnchor = HAnchor.Stretch;
				edit.MinimumSize = new Vector2(Math.Ceiling(MinimumFieldWidth * DeviceScale), 0);

				edit.ActualTextEditWidget.EditComplete += (s, e) => Commit(field, edit);
				edit.ActualTextEditWidget.InternalTextEditWidget.KeyDown += (s, keyEvent) =>
				{
					if (keyEvent.KeyCode == Keys.Escape)
					{
						// put the current value back
						keyEvent.Handled = true;
						keyEvent.SuppressKeyPress = true;
						SetFieldText(edit, CurrentText(field, CurrentBounds()));
					}
				};

				fields[field] = edit;
				cell.AddChild(edit);
				cellsRow.AddChild(cell);
			}

			return row;
		}

		private static string Tooltip(TransformField field)
		{
			switch (field)
			{
				case TransformField.CenterX:
				case TransformField.CenterY:
					return "The centre of the selection on the bed, in mm".Localize();

				case TransformField.BottomZ:
					return "The height of the bottom of the selection; 0 is on the bed".Localize();

				case TransformField.RotateX:
				case TransformField.RotateY:
				case TransformField.RotateZ:
					return "Type an angle to turn the selection about its centre; the field then goes back to 0".Localize();

				default:
					return "The size of the selection along this axis, in mm".Localize();
			}
		}

		private void Scene_Invalidated(object sender, EventArgs e)
		{
			if (refreshQueued || this.HasBeenClosed)
			{
				return;
			}

			refreshQueued = true;
			UiThread.RunOnIdle(() =>
			{
				refreshQueued = false;
				if (!this.HasBeenClosed)
				{
					Refresh();
				}
			});
		}

		private TransformBounds CurrentBounds()
		{
			var aabb = item.GetAxisAlignedBoundingBox();
			return new TransformBounds(aabb.MinXYZ.X, aabb.MinXYZ.Y, aabb.MinXYZ.Z, aabb.MaxXYZ.X, aabb.MaxXYZ.Y, aabb.MaxXYZ.Z);
		}

		private static string CurrentText(TransformField field, TransformBounds bounds)
		{
			if (field >= TransformField.RotateX)
			{
				return "0";
			}

			return bounds.IsValid ? TransformRules.Format(bounds.Field(field)) : "";
		}

		/// <summary>Shows the current values (the bounds measured once); a field being typed in keeps its text.</summary>
		private void Refresh()
		{
			var bounds = CurrentBounds();
			bool atRoot = AtSceneRoot;

			foreach (var pair in fields)
			{
				pair.Value.Enabled = atRoot;
				if (!pair.Value.ContainsFocus)
				{
					SetFieldText(pair.Value, CurrentText(pair.Key, bounds));
				}
			}

			keepProportions.Enabled = atRoot;
			nestedNote.Visible = !atRoot;
			rotatedNote.Visible = atRoot
				&& !keepProportions.Checked
				&& !IsAxisAligned();
		}

		private static double Evaluate(string text)
		{
			// the app's expression parser (mXparser, also behind the Variable Sheet's "=" expressions)
			return new Expression(text).calculate();
		}

		private void Commit(TransformField field, ThemedTextEditWidget edit)
		{
			var value = TransformRules.ParseNumber(edit.Text, Evaluate);
			if (!value.HasValue
				&& !string.IsNullOrWhiteSpace(edit.Text))
			{
				// the field goes back to the current value below
				ApplicationController.Instance.SetUiHint("Type a number, such as 100.5 or 20+5.".Localize());
			}
			var bounds = CurrentBounds();
			var scene = sceneContext.Scene;

			if (value.HasValue
				&& !TransformRules.IsWithinLimits(field, value.Value))
			{
				ApplicationController.Instance.SetUiHint("That is more than 10,000 mm.".Localize());
				value = null;
			}

			if (!AtSceneRoot)
			{
				ApplicationController.Instance.SetUiHint(TransformRules.NestedSelectionMessage.Localize());
			}

			TransformStep step = null;
			if (value.HasValue
				&& bounds.IsValid
				&& scene.SelectedItem == item
				&& AtSceneRoot
				// nothing to do for the value already there (also stops a second commit of the same entry)
				&& !TransformRules.IsNoChange(bounds, field, value.Value))
			{
				if (field <= TransformField.BottomZ)
				{
					step = TransformRules.Move(bounds, field, value.Value);
				}
				else if (field <= TransformField.SizeZ)
				{
					// a turned part scales evenly: one world axis alone would shear it
					step = TransformRules.Resize(bounds, field, value.Value, TransformRules.ScaleProportionally(keepProportions?.Checked != false, IsAxisAligned()));
					if (step == null && value.Value < TransformRules.MinimumSize)
					{
						ApplicationController.Instance.SetUiHint("A size must be more than 0 mm.".Localize());
					}
				}
				else
				{
					step = TransformRules.Rotate(bounds, field, value.Value);
				}
			}

			if (step != null)
			{
				Apply(step, field >= TransformField.RotateX, bounds.Bottom);
			}

			// rotate fields go back to 0; the others show the (new) current value
			UiThread.RunOnIdle(() =>
			{
				if (!this.HasBeenClosed)
				{
					SetFieldText(edit, CurrentText(field, CurrentBounds()));
				}
			});
		}

		private static Matrix4X4 ToMatrix(TransformStep step)
		{
			var pivot = new Vector3(step.PivotX, step.PivotY, step.PivotZ);
			var matrix = Matrix4X4.CreateTranslation(-pivot)
				* Matrix4X4.CreateScale(step.ScaleX, step.ScaleY, step.ScaleZ);

			if (step.RotateAxis >= 0 && step.RotateDegrees != 0)
			{
				var radians = MathHelper.DegreesToRadians(step.RotateDegrees);
				matrix *= step.RotateAxis == 0 ? Matrix4X4.CreateRotationX(radians)
					: step.RotateAxis == 1 ? Matrix4X4.CreateRotationY(radians)
					: Matrix4X4.CreateRotationZ(radians);
			}

			return matrix
				* Matrix4X4.CreateTranslation(pivot)
				* Matrix4X4.CreateTranslation(step.MoveX, step.MoveY, step.MoveZ);
		}

		/// <summary>
		/// Applies a world-space step to the selection as one undo step. A multi-selection changes each selected part (the
		/// selection's own transform is kept). A rotation then moves the selection so its bottom stays where it was.
		/// </summary>
		private void Apply(TransformStep step, bool keepBottom, double bottomBefore)
		{
			var scene = sceneContext.Scene;
			var delta = ToMatrix(step);

			var targets = item is SelectionGroupObject3D group
				? group.Children.ToList()
				: new List<IObject3D> { item };

			// world delta in each target's parent space (a multi-selection's children sit under the selection's transform)
			var parentMatrix = item is SelectionGroupObject3D ? item.Matrix : Matrix4X4.Identity;
			var localDelta = parentMatrix * delta * parentMatrix.Inverted;

			var undo = targets.Select(t => t.Matrix).ToList();
			var redo = undo.Select(m => m * localDelta).ToList();

			if (keepBottom)
			{
				// measure the turned selection, then lift or lower it back to its old bottom
				for (int i = 0; i < targets.Count; i++)
				{
					targets[i].Matrix = redo[i];
				}

				var bottomAfter = item.GetAxisAlignedBoundingBox().MinXYZ.Z;

				for (int i = 0; i < targets.Count; i++)
				{
					targets[i].Matrix = undo[i];
				}

				var lift = TransformRules.KeepBottom(bottomBefore, bottomAfter);
				if (lift != 0)
				{
					var liftLocal = parentMatrix * Matrix4X4.CreateTranslation(0, 0, lift) * parentMatrix.Inverted;
					redo = redo.Select(m => m * liftLocal).ToList();
				}
			}

			var transformData = targets.Select((t, i) => new TransformData()
			{
				TransformedObject = t,
				UndoTransform = undo[i],
				RedoTransform = redo[i],
			}).ToList();

			scene.UndoBuffer.AddAndDo(new TransformCommand(transformData));
			scene.Invalidate(new InvalidateArgs(item, InvalidateType.Matrix));
		}
	}
}
