using System;
using System.Collections.Generic;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.Plugins.EditorTools;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	/// <summary>EBMC Edition: the size handles the club shapes use, built the way CylinderObject3D builds its own.</summary>
	public static class ClubControls
	{
		/// <summary>A diameter handle on the bottom edge and a height handle, then move and rotate.</summary>
		public static void AddDiameterAndHeight(Object3DControlsLayer layer, Func<double> getDiameter, Action<double> setDiameter, Func<double> getHeight, Action<double> setHeight)
		{
			var getDiameters = new List<Func<double>>() { getDiameter };
			var setDiameters = new List<Action<double>>() { setDiameter };
			layer.Object3DControls.Add(new ScaleDiameterControl(layer, getHeight, setHeight, getDiameters, setDiameters, 0, controlVisible: () => true));
			layer.Object3DControls.Add(new ScaleHeightControl(layer, null, null, null, null, getHeight, setHeight, getDiameters, setDiameters));
			layer.AddControls(ControlTypes.MoveInZ);
			layer.AddControls(ControlTypes.RotateXYZ);
		}

		/// <summary>A height handle only (the other sizes are typed in), then move and rotate.</summary>
		public static void AddHeightOnly(Object3DControlsLayer layer, Func<double> getHeight, Action<double> setHeight)
		{
			layer.Object3DControls.Add(new ScaleHeightControl(layer, null, null, null, null, getHeight, setHeight));
			layer.AddControls(ControlTypes.MoveInZ);
			layer.AddControls(ControlTypes.RotateXYZ);
		}

		/// <summary>Move and rotate only (starters: their sizes come from their own fields).</summary>
		public static void AddMoveRotate(Object3DControlsLayer layer)
		{
			layer.AddControls(ControlTypes.MoveInZ);
			layer.AddControls(ControlTypes.RotateXYZ);
		}
	}
}
