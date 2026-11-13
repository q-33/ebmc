using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.PolygonMesh.Processors;
using MatterHackers.VectorMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>
	/// EBMC Edition (club shapes): a spur gear with involute teeth (20° pressure angle) and a round bore, drawn by the
	/// existing Gear2D outline (which includes its own small backlash and clearance) and extruded. Two gears mesh when they
	/// have the same module; their centres are then (teeth1 + teeth2) × module / 2 apart.
	/// </summary>
	public class GearObject3D : ClubShapeObject3D, IObject3DControlsProvider
	{
		public GearObject3D()
		{
			Name = "Gear".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Torus"];
		}

		protected override string BuildTitle => "Gear".Localize();

		[Slider(6, 100, snapDistance: 1)]
		public IntOrExpression Teeth { get; set; } = 20;

		[Description("Tooth size in mm. Gears that mesh need the same module. The pitch diameter is teeth × module; the outside diameter is (teeth + 2) × module.")]
		[MaxDecimalPlaces(2)]
		[Slider(0.5, 5, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression Module { get; set; } = 1;

		[MaxDecimalPlaces(2)]
		[Slider(0.5, 100, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 5;

		[DisplayName("Bore Diameter")]
		[Description("The round hole in the middle; 0 for none.")]
		[MaxDecimalPlaces(2)]
		[Slider(0, 50, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression BoreDiameter { get; set; } = 5;

		public static async Task<GearObject3D> Create()
		{
			var item = new GearObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var teeth = Teeth.ClampFinite(this, GearProfile.MinTeeth, GearProfile.MaxTeeth, ref valuesChanged);
			var module = Module.ClampFinite(this, 0.3, 20, ref valuesChanged);
			var height = Height.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			var bore = BoreDiameter.ClampFinite(this, 0, ShapeLimits.GearBore(teeth, module, 1e6), ref valuesChanged);
			return token =>
			{
				var build = new ClubBuild();
				// the tooth outline (and bore) from Gear2D, extruded like LinearExtrude does
				var outline = new Gear2D
				{
					CircularPitch = GearProfile.CircularPitch(module),
					ToothCount = teeth,
					PressureAngle = GearProfile.PressureAngleDegrees,
					CenterHoleDiameter = bore,
				};
				build.Extra.Add(VertexSourceToMesh.Extrude(outline, height));
				return build;
			};
		}

		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			ClubControls.AddHeightOnly(object3DControlsLayer, () => Height.Value(this), h => Height = h);
		}
	}
}
