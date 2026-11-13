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
	/// <summary>How a Polygon Prism's diameter is measured.</summary>
	public enum PolygonMeasure
	{
		[EnumName("Across corners")]
		AcrossCorners,
		[EnumName("Across flats")]
		AcrossFlats
	}

	/// <summary>EBMC Edition (club shapes): a prism on a regular polygon (3 to 12 sides; 6 makes a hexagon), a flat side to the front.</summary>
	public class PolygonPrismObject3D : ClubShapeObject3D, IObject3DControlsProvider
	{
		public PolygonPrismObject3D()
		{
			Name = "Polygon Prism".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Cylinder"];
		}

		protected override string BuildTitle => "Polygon Prism".Localize();

		[Description("3 makes a triangle, 6 a hexagon.")]
		[Slider(3, 12, snapDistance: 1)]
		public IntOrExpression Sides { get; set; } = 6;

		[Description("Measured across the corners or across the flat sides, as chosen below.")]
		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Diameter { get; set; } = 20;

		[DisplayName("Diameter Is")]
		[Description("Across corners: the circle the corners sit on. Across flats: the distance between two opposite flat sides (a spanner size for a hexagon).")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		[JsonConverter(typeof(StringEnumConverter))]
		public PolygonMeasure Measure { get; set; } = PolygonMeasure.AcrossCorners;

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 20;

		public static async Task<PolygonPrismObject3D> Create()
		{
			var item = new PolygonPrismObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var sides = Sides.ClampFinite(this, 3, 12, ref valuesChanged);
			var diameter = Diameter.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			var height = Height.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			var cornerRadius = Measure == PolygonMeasure.AcrossFlats ? Profiles.CornerRadiusFromFlats(sides, diameter) : diameter / 2;
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(ShapeBuilders.PolygonPrism(sides, cornerRadius, height));
				return build;
			};
		}

		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			ClubControls.AddHeightOnly(object3DControlsLayer, () => Height.Value(this), h => Height = h);
		}
	}
}
