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
	/// <summary>EBMC Edition (club shapes): a flat heart.</summary>
	public class HeartObject3D : ClubShapeObject3D, IObject3DControlsProvider
	{
		public HeartObject3D()
		{
			Name = "Heart".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Cube"];
		}

		protected override string BuildTitle => "Heart".Localize();

		[Description("Side to side; the heart is a little shorter front to back.")]
		[MaxDecimalPlaces(2)]
		[Slider(2, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Width { get; set; } = 20;

		[Description("How thick (tall) the heart is.")]
		[MaxDecimalPlaces(2)]
		[Slider(0.5, 100, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 5;

		public static async Task<HeartObject3D> Create()
		{
			var item = new HeartObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var width = Width.ClampFinite(this, 2, ShapeLimits.MaxSize, ref valuesChanged);
			var height = Height.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(ShapeBuilders.Heart(width, height));
				return build;
			};
		}

		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			ClubControls.AddHeightOnly(object3DControlsLayer, () => Height.Value(this), h => Height = h);
		}
	}
}
