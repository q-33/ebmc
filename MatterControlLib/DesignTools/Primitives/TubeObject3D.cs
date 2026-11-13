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
	/// <summary>EBMC Edition (club shapes): a round tube (a cylinder with a round hole through it).</summary>
	public class TubeObject3D : ClubShapeObject3D, IObject3DControlsProvider
	{
		public TubeObject3D()
		{
			Name = "Tube".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Ring"];
		}

		protected override string BuildTitle => "Tube".Localize();

		[DisplayName("Outer Diameter")]
		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression OuterDiameter { get; set; } = 20;

		[DisplayName("Wall Thickness")]
		[Description("The inside diameter is the outer diameter minus twice this.")]
		[MaxDecimalPlaces(2)]
		[Slider(0.8, 50, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression WallThickness { get; set; } = 2;

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 20;

		[Slider(3, 180, Easing.EaseType.Quadratic, snapDistance: 1)]
		public IntOrExpression Sides { get; set; } = 64;

		public static async Task<TubeObject3D> Create()
		{
			var item = new TubeObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			// the side count first: with few sides the wall is thinnest across the flats, so the minimum wall grows
			var sides = Sides.ClampFinite(this, 3, 360, ref valuesChanged);
			var outer = OuterDiameter.ClampFinite(this, ShapeLimits.TubeMinOuter(sides), ShapeLimits.MaxSize, ref valuesChanged);
			var wallLimit = ShapeLimits.TubeWall(0, outer, sides);
			var wall = WallThickness.ClampFinite(this, wallLimit, Math.Max(wallLimit, (outer - ShapeLimits.MinSize) / 2), ref valuesChanged);
			var height = Height.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(ShapeBuilders.Tube(outer, outer - 2 * wall, height, sides));
				return build;
			};
		}

		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			ClubControls.AddDiameterAndHeight(object3DControlsLayer, () => OuterDiameter.Value(this), d => OuterDiameter = d, () => Height.Value(this), h => Height = h);
		}
	}
}
