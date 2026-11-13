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
	/// EBMC Edition: a box made by the solid kernel, with exact rounded (fillet) or bevelled (chamfer) edges. The size
	/// handles change Width, Depth and Height (like the Cube's), so the edges keep their shape when it is resized.
	/// </summary>
	public class SolidBoxObject3D : SolidObject3D, IPropertyGridModifier, IObject3DControlsProvider
	{
		public SolidBoxObject3D()
		{
			Name = "Solid Box".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Cube"];
		}

		internal override SolidShapeKind Kind => SolidShapeKind.Box;

		protected override string BuildTitle => "Solid Box".Localize();

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Width { get; set; } = 20;

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Depth { get; set; } = 20;

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 20;

		[DisplayName("Edges")]
		[Description("Round (fillet) or bevel (chamfer) the edges.")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		public SolidEdgeTreatment EdgeTreatment { get; set; } = SolidEdgeTreatment.Fillet;

		[DisplayName("Size")]
		[Description("The fillet radius or the chamfer width, in mm.")]
		[MaxDecimalPlaces(2)]
		[Slider(0, 20, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression EdgeSize { get; set; } = 2;

		[DisplayName("Which Edges")]
		[Description("The edges that are rounded or bevelled.")]
		public SolidBoxEdges Edges { get; set; } = SolidBoxEdges.All;

		public static async Task<SolidBoxObject3D> Create()
		{
			var item = new SolidBoxObject3D();
			await item.BuildDetached();
			return item;
		}

		private protected override Func<OcctShape> PrepareBuild(ref bool valuesChanged, out SolidProblem problem)
		{
			var spec = new SolidBoxSpec
			{
				Width = Width.ClampIfNotCalculated(this, SolidLimits.MinSize, SolidLimits.MaxSize, ref valuesChanged),
				Depth = Depth.ClampIfNotCalculated(this, SolidLimits.MinSize, SolidLimits.MaxSize, ref valuesChanged),
				Height = Height.ClampIfNotCalculated(this, SolidLimits.MinSize, SolidLimits.MaxSize, ref valuesChanged),
				Treatment = EdgeTreatment,
				EdgeSize = EdgeSize.ClampIfNotCalculated(this, 0, SolidLimits.MaxSize, ref valuesChanged),
				Edges = Edges,
			};

			problem = spec.Check();
			return problem == SolidProblem.None ? () => SolidBuilders.Box(spec) : (Func<OcctShape>)null;
		}

		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			object3DControlsLayer.AddHeightControl(this, Width, Depth, Height);
			object3DControlsLayer.AddWidthDepthControls(this, Width, Depth, Height);

			object3DControlsLayer.AddControls(ControlTypes.MoveInZ);
			object3DControlsLayer.AddControls(ControlTypes.RotateXYZ);
		}

		public void UpdateControls(PublicPropertyChange change)
		{
			change.SetRowVisible(nameof(EdgeSize), () => EdgeTreatment != SolidEdgeTreatment.None);
			change.SetRowVisible(nameof(Edges), () => EdgeTreatment != SolidEdgeTreatment.None);
		}
	}
}
