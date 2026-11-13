using System;
using System.ComponentModel;
using System.Threading.Tasks;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>
	/// EBMC Edition: a plate with one clean round through-hole, cut by an exact boolean in the solid kernel (not by
	/// mesh subtraction), optionally countersunk. The size handles change Width, Depth and Height; the hole keeps its
	/// diameter and its offset from the plate's centre.
	/// </summary>
	public class SolidPlateObject3D : SolidObject3D, IPropertyGridModifier, IObject3DControlsProvider
	{
		public SolidPlateObject3D()
		{
			Name = "Plate with Hole".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Cube"];
		}

		internal override SolidShapeKind Kind => SolidShapeKind.Plate;

		protected override string BuildTitle => "Plate with Hole".Localize();

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Width { get; set; } = 40;

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Depth { get; set; } = 40;

		[MaxDecimalPlaces(2)]
		[Slider(1, 100, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 5;

		[DisplayName("Hole Diameter")]
		[MaxDecimalPlaces(2)]
		[Slider(0.5, 100, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression HoleDiameter { get; set; } = 8;

		[DisplayName("Hole X")]
		[Description("How far the hole's centre is from the plate's centre, left (-) or right (+), in mm.")]
		[MaxDecimalPlaces(2)]
		public DoubleOrExpression HoleX { get; set; } = 0;

		[DisplayName("Hole Y")]
		[Description("How far the hole's centre is from the plate's centre, front (-) or back (+), in mm.")]
		[MaxDecimalPlaces(2)]
		public DoubleOrExpression HoleY { get; set; } = 0;

		[DisplayName("Rim")]
		[Description("Countersink bevels the top of the hole at 45° so a flat-head screw sits flush.")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		public SolidHoleRim Rim { get; set; } = SolidHoleRim.Plain;

		[DisplayName("Countersink Size")]
		[Description("How far the countersink reaches out from the hole and down into it, in mm.")]
		[MaxDecimalPlaces(2)]
		[Slider(0, 10, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression RimSize { get; set; } = 1;

		public static async Task<SolidPlateObject3D> Create()
		{
			var item = new SolidPlateObject3D();
			await item.BuildDetached();
			return item;
		}

		private protected override Func<OcctShape> PrepareBuild(bool clampStored, ref bool valuesChanged, out SolidProblem problem)
		{
			var spec = new SolidPlateSpec
			{
				Width = Read(Width, SolidLimits.MinSize, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				Depth = Read(Depth, SolidLimits.MinSize, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				Height = Read(Height, SolidLimits.MinSize, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				HoleDiameter = Read(HoleDiameter, SolidLimits.MinSize, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				HoleX = Read(HoleX, -SolidLimits.MaxSize, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				HoleY = Read(HoleY, -SolidLimits.MaxSize, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				Rim = Rim,
				RimSize = Read(RimSize, 0, SolidLimits.MaxSize, clampStored, ref valuesChanged),
			};

			problem = spec.Check();
			return problem == SolidProblem.None ? () => SolidBuilders.Plate(spec) : (Func<OcctShape>)null;
		}

		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			if (!CanEditSize)
			{
				// solid modelling off: the shape cannot be rebuilt, so no size handles (move and rotate still work)
				object3DControlsLayer.AddControls(ControlTypes.MoveInZ);
				object3DControlsLayer.AddControls(ControlTypes.RotateXYZ);
				return;
			}

			object3DControlsLayer.AddHeightControl(this, Width, Depth, Height);
			object3DControlsLayer.AddWidthDepthControls(this, Width, Depth, Height);

			object3DControlsLayer.AddControls(ControlTypes.MoveInZ);
			object3DControlsLayer.AddControls(ControlTypes.RotateXYZ);
		}

		public void UpdateControls(PublicPropertyChange change)
		{
			LockRowsWhenUnavailable(change);
			change.SetRowVisible(nameof(RimSize), () => Rim == SolidHoleRim.Countersink);
		}
	}
}
