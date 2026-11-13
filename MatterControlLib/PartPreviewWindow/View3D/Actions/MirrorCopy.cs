using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: Mirror Copy (OemSettings.EnableAdvancedTools). Adds a mirrored copy of the selection next to it, as
	/// one undo step, and selects the copy. The copy is an ordinary Mirror operation (MirrorObject3D_2) holding a clone
	/// of the selection, so it bakes the mirrored mesh with reversed faces and recalculated normals exactly as Mirror does,
	/// and can be edited (axis) or applied like any Mirror. Placement: MirrorCopyRules.
	/// </summary>
	public static class MirrorCopy
	{
		private const string AxisSettingKey = "ebmc_mirror_copy_axis";

		/// <summary>The axis used last (user settings), X before the first use.</summary>
		public static MirrorCopyAxis LastAxis
		{
			get => MirrorCopyRules.Parse(UserSettings.Instance.get(AxisSettingKey));
			set => UserSettings.Instance.set(AxisSettingKey, value.ToString());
		}

		public static string AxisTitle(MirrorCopyAxis axis)
		{
			switch (axis)
			{
				case MirrorCopyAxis.Y:
					return "Mirror Copy across Y (behind)".Localize();

				case MirrorCopyAxis.Z:
					return "Mirror Copy across Z (on top)".Localize();

				default:
					return "Mirror Copy across X (to the right)".Localize();
			}
		}

		/// <summary>
		/// Shows the X / Y / Z choice over the middle of the 3D view of <paramref name="sceneContext"/>, the last-used axis
		/// first. Without a visible 3D view (or a window) it uses the last-used axis.
		/// </summary>
		public static void ChooseAxisAndRun(ISceneContext sceneContext)
		{
			var view3D = AppContext.RootSystemWindow?.Descendants<View3DWidget>()
				.FirstOrDefault(v => v.sceneContext == sceneContext && v.ActuallyVisibleOnScreen());
			var systemWindow = view3D?.Parents<SystemWindow>().FirstOrDefault();

			if (view3D == null
				|| systemWindow == null)
			{
				Run(sceneContext, LastAxis);
				return;
			}

			var menuTheme = ApplicationController.Instance.MenuTheme;
			var popupMenu = new PopupMenu(menuTheme);

			var last = LastAxis;
			var axes = new[] { last }.Concat(new[] { MirrorCopyAxis.X, MirrorCopyAxis.Y, MirrorCopyAxis.Z }.Where(a => a != last));
			// EBMC Edition: the club menu style (OemSettings.ClubDialogsAndMenus); the same three items
			bool club = ClubUi.ClubStyle.On;
			if (club)
			{
				ClubUi.ClubMenuBuilder.StyleMenu(popupMenu);
			}

			foreach (var axis in axes)
			{
				var chosen = axis;
				var menuItem = club
					? ClubUi.ClubMenuBuilder.CreateItem(popupMenu, AxisTitle(chosen), null, null, false)
					: popupMenu.CreateMenuItem(AxisTitle(chosen));
				menuItem.Click += (s, e) => UiThread.RunOnIdle(() => Run(sceneContext, chosen));
			}

			systemWindow.ShowPopup(
				menuTheme,
				new MatePoint(view3D),
				new MatePoint(popupMenu),
				default(RectangleDouble),
				1,
				CenterOver);
		}

		/// <summary>Places a popup in the middle of its anchor.</summary>
		public static void CenterOver(SystemWindow systemWindow, MatePoint anchor, MatePoint popup, RectangleDouble altBounds)
		{
			var anchorWidget = anchor.Widget;
			var origin = anchorWidget.Parent.TransformToScreenSpace(anchorWidget.Position);
			var x = origin.X + (anchorWidget.Width - popup.Widget.Width) / 2;
			var y = origin.Y + (anchorWidget.Height - popup.Widget.Height) / 2;

			x = Math.Max(0, Math.Min(x, systemWindow.Width - popup.Widget.Width));
			y = Math.Max(0, Math.Min(y, systemWindow.Height - popup.Widget.Height));

			popup.Widget.Position = new Vector2(Math.Round(x), Math.Round(y));
		}

		/// <summary>
		/// Adds the mirrored copy across <paramref name="axis"/> as one undo step (an insert) and selects it. Does nothing
		/// without a selection; says so in the status bar when the selection is flat along the axis.
		/// </summary>
		public static async void Run(ISceneContext sceneContext, MirrorCopyAxis axis)
		{
			var scene = sceneContext?.Scene;
			var selected = scene?.SelectedItem;
			if (selected == null)
			{
				return;
			}

			// the copy is placed from the selection's own matrix and added at the top of the scene, which is only right
			// for a top-level selection (a part, a group or a multi-selection)
			if (!scene.Children.Contains(selected))
			{
				ApplicationController.Instance.SetUiHint(TransformRules.NestedSelectionMessage.Localize());
				return;
			}

			LastAxis = axis;

			var bounds = selected.GetAxisAlignedBoundingBox();
			int index = MirrorCopyRules.Index(axis);
			var offset = MirrorCopyRules.Offset(bounds.MinXYZ[index], bounds.MaxXYZ[index]);
			if (offset <= 0)
			{
				ApplicationController.Instance.SetUiHint("This part is flat along that axis, so a mirrored copy would land on it.".Localize());
				return;
			}

			var move = Vector3.Zero;
			move[index] = offset;
			var moveMatrix = Matrix4X4.CreateTranslation(move);

			// the selected parts in world space (a multi-selection's own transform is folded into each copy)
			var sources = selected is SelectionGroupObject3D selectionGroup
				? selectionGroup.Children.Select(child => (item: child, world: child.Matrix * selectionGroup.Matrix)).ToList()
				: new List<(IObject3D item, Matrix4X4 world)> { (selected, selected.Matrix) };

			var mirror = new MirrorObject3D_2()
			{
				MirrorOn = axis == MirrorCopyAxis.X ? MirrorObject3D_2.MirrorAxis.X_Axis
					: axis == MirrorCopyAxis.Y ? MirrorObject3D_2.MirrorAxis.Y_Axis
					: MirrorObject3D_2.MirrorAxis.Z_Axis,
			};

			using (mirror.RebuildLock())
			{
				var copies = sources.Select(source =>
				{
					var copy = source.item.Clone();
					copy.Matrix = source.world * moveMatrix;
					return copy;
				}).ToList();

				mirror.Children.Modify(list =>
				{
					list.Clear();
					list.AddRange(copies);
				});
			}

			// one undo step: inserting the finished Mirror (its rebuild below is not an undo step)
			scene.SelectedItem = null;
			scene.UndoBuffer.AddAndDo(new InsertCommand(scene, mirror));

			await mirror.Rebuild();

			mirror.Name = "Mirror Copy".Localize() + " " + mirror.NameFromChildren();

			if (scene.Children.Contains(mirror))
			{
				scene.SelectedItem = mirror;
			}
		}
	}
}
