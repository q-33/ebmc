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
	/// <summary>EBMC Edition (club shapes): a plate with fully rounded ends (a stadium), optionally with a centred slot through it.</summary>
	public class SlotPlateObject3D : ClubShapeObject3D, IPropertyGridModifier, IObject3DControlsProvider
	{
		public SlotPlateObject3D()
		{
			Name = "Slot Plate".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Wedge"];
		}

		protected override string BuildTitle => "Slot Plate".Localize();

		[Description("End to end, including the rounded ends.")]
		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Length { get; set; } = 40;

		[Description("Side to side; the ends are half circles of this diameter.")]
		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Width { get; set; } = 20;

		[MaxDecimalPlaces(2)]
		[Slider(0.5, 100, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 5;

		[DisplayName("Slot Hole")]
		[Description("A slot through the middle, also with rounded ends.")]
		public bool Slot { get; set; } = true;

		[DisplayName("Slot Length")]
		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression SlotLength { get; set; } = 20;

		[DisplayName("Slot Width")]
		[MaxDecimalPlaces(2)]
		[Slider(0.5, 100, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression SlotWidth { get; set; } = 6;

		public static async Task<SlotPlateObject3D> Create()
		{
			var item = new SlotPlateObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var width = Width.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			var length = Length.ClampFinite(this, width, ShapeLimits.MaxSize, ref valuesChanged);
			var height = Height.ClampFinite(this, ShapeLimits.MinSize, ShapeLimits.MaxSize, ref valuesChanged);
			var slot = Slot && width > 2 * ShapeLimits.MinWall + ShapeLimits.MinSize;
			double slotLength = 0, slotWidth = 0;
			if (slot)
			{
				var (maxLength, maxWidth) = ShapeLimits.SlotHole(length, width, 1e6, 1e6);
				slotWidth = SlotWidth.ClampFinite(this, ShapeLimits.MinSize, maxWidth, ref valuesChanged);
				slotLength = SlotLength.ClampFinite(this, slotWidth, Math.Max(slotWidth, maxLength), ref valuesChanged);
			}

			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(ShapeBuilders.SlotPlate(length, width, height, slot, slotLength, slotWidth));
				return build;
			};
		}

		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			object3DControlsLayer.AddHeightControl(this, Length, Width, Height);
			object3DControlsLayer.AddWidthDepthControls(this, Length, Width, Height);
			object3DControlsLayer.AddControls(ControlTypes.MoveInZ);
			object3DControlsLayer.AddControls(ControlTypes.RotateXYZ);
		}

		public void UpdateControls(PublicPropertyChange change)
		{
			change.SetRowVisible(nameof(SlotLength), () => Slot);
			change.SetRowVisible(nameof(SlotWidth), () => Slot);
		}
	}
}
