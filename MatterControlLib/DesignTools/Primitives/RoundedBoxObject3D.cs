using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.VectorMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>
	/// EBMC Edition (club shapes): a box with rounded vertical edges and optionally rounded or bevelled top edges. The
	/// bottom edges stay sharp so it prints flat.
	/// </summary>
	public class RoundedBoxObject3D : ClubShapeObject3D, IPropertyGridModifier, IObject3DControlsProvider
	{
		public RoundedBoxObject3D()
		{
			Name = "Rounded Box".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Cube"];
		}

		protected override string BuildTitle => "Rounded Box".Localize();

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Width { get; set; } = 20;

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Depth { get; set; } = 20;

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 20;

		[DisplayName("Corner Radius")]
		[Description("Rounds the four upright edges.")]
		[MaxDecimalPlaces(2)]
		[Slider(0, 50, Easing.EaseType.Quadratic, snapDistance: .5)]
		public DoubleOrExpression CornerRadius { get; set; } = 3;

		[DisplayName("Top Edge Radius")]
		[Description("Rounds or bevels the top edges only; the bottom stays flat for printing. 0 for none.")]
		[MaxDecimalPlaces(2)]
		[Slider(0, 20, Easing.EaseType.Quadratic, snapDistance: .5)]
		public DoubleOrExpression TopEdgeRadius { get; set; } = 0;

		[DisplayName("Top Edge")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		[JsonConverter(typeof(StringEnumConverter))]
		public TopEdgeStyle TopEdge { get; set; } = TopEdgeStyle.Rounded;

		public static async Task<RoundedBoxObject3D> Create()
		{
			var item = new RoundedBoxObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var width = Width.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			var depth = Depth.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			var height = Height.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			var corner = CornerRadius.ClampFinite(this, 0, ShapeLimits.BoxCornerRadius(1e6, width, depth), ref valuesChanged);
			var top = TopEdgeRadius.ClampFinite(this, 0, ShapeLimits.BoxTopRadius(1e6, width, depth, height, corner), ref valuesChanged);
			var style = TopEdge;
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(ShapeBuilders.RoundedBox(width, depth, height, corner, top, style));
				return build;
			};
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
			change.SetRowVisible(nameof(TopEdge), () => TopEdgeRadius.Value(this) > 0);
		}
	}
}
