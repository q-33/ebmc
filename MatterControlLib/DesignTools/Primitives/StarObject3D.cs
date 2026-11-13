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
	/// <summary>EBMC Edition (club shapes): a flat star with straight sides, one point to the back.</summary>
	public class StarObject3D : ClubShapeObject3D, IObject3DControlsProvider
	{
		public StarObject3D()
		{
			Name = "Star".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Text"];
		}

		protected override string BuildTitle => "Star".Localize();

		[Slider(3, 20, snapDistance: 1)]
		public IntOrExpression Points { get; set; } = 5;

		[DisplayName("Outer Diameter")]
		[Description("Across the tips.")]
		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression OuterDiameter { get; set; } = 20;

		[DisplayName("Inner Diameter")]
		[Description("Across the inside corners between the tips; smaller makes thinner points.")]
		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression InnerDiameter { get; set; } = 10;

		[MaxDecimalPlaces(2)]
		[Slider(0.5, 100, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 5;

		public static async Task<StarObject3D> Create()
		{
			var item = new StarObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var points = Points.ClampFinite(this, 3, 20, ref valuesChanged);
			var outer = OuterDiameter.ClampFinite(this, 2, ShapeLimits.MaxSize, ref valuesChanged);
			var inner = InnerDiameter.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.StarInner(outer, 1e6), ref valuesChanged);
			var height = Height.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(ShapeBuilders.Star(points, outer, inner, height));
				return build;
			};
		}

		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			ClubControls.AddHeightOnly(object3DControlsLayer, () => Height.Value(this), h => Height = h);
		}
	}
}
