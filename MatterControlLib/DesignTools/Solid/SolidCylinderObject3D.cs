using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.Plugins.EditorTools;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>
	/// EBMC Edition: an exact cylinder (a true circle, not a polygon) with an optional rounded or bevelled rim. The
	/// diameter and height handles change Diameter and Height.
	/// </summary>
	public class SolidCylinderObject3D : SolidObject3D, IPropertyGridModifier, IObject3DControlsProvider
	{
		public SolidCylinderObject3D()
		{
			Name = "Solid Cylinder".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Cylinder"];
		}

		internal override SolidShapeKind Kind => SolidShapeKind.Cylinder;

		protected override string BuildTitle => "Solid Cylinder".Localize();

		[MaxDecimalPlaces(2)]
		[Description("The width from one side to the opposite side.")]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Diameter { get; set; } = 20;

		[MaxDecimalPlaces(2)]
		[Slider(1, 400, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Height { get; set; } = 20;

		[DisplayName("Edges")]
		[Description("Round (fillet) or bevel (chamfer) the rim.")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		public SolidEdgeTreatment EdgeTreatment { get; set; } = SolidEdgeTreatment.Chamfer;

		[DisplayName("Size")]
		[Description("The fillet radius or the chamfer width, in mm.")]
		[MaxDecimalPlaces(2)]
		[Slider(0, 20, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression EdgeSize { get; set; } = 1;

		[DisplayName("Which Rims")]
		[Description("The rims that are rounded or bevelled.")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		public SolidCylinderEdges Rims { get; set; } = SolidCylinderEdges.Top;

		public static async Task<SolidCylinderObject3D> Create()
		{
			var item = new SolidCylinderObject3D();
			await item.BuildDetached();
			return item;
		}

		private protected override Func<OcctShape> PrepareBuild(bool clampStored, ref bool valuesChanged, out SolidProblem problem)
		{
			var spec = new SolidCylinderSpec
			{
				Diameter = Read(Diameter, SolidLimits.MinSize, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				Height = Read(Height, SolidLimits.MinSize, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				Treatment = EdgeTreatment,
				EdgeSize = Read(EdgeSize, 0, SolidLimits.MaxSize, clampStored, ref valuesChanged),
				Edges = Rims,
			};

			problem = spec.Check();
			return problem == SolidProblem.None ? () => SolidBuilders.Cylinder(spec) : (Func<OcctShape>)null;
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

			double getHeight() => Height.Value(this);
			void setHeight(double height) => Height = height;
			var getDiameters = new List<Func<double>>() { () => Diameter.Value(this) };
			var setDiameters = new List<Action<double>>() { (diameter) => Diameter = diameter };
			object3DControlsLayer.Object3DControls.Add(new ScaleDiameterControl(object3DControlsLayer,
				getHeight,
				setHeight,
				getDiameters,
				setDiameters,
				0,
				controlVisible: () => true));
			object3DControlsLayer.Object3DControls.Add(new ScaleHeightControl(object3DControlsLayer,
				null,
				null,
				null,
				null,
				getHeight,
				setHeight,
				getDiameters,
				setDiameters));
			object3DControlsLayer.AddControls(ControlTypes.MoveInZ);
			object3DControlsLayer.AddControls(ControlTypes.RotateXYZ);
		}

		public void UpdateControls(PublicPropertyChange change)
		{
			LockRowsWhenUnavailable(change);
			change.SetRowVisible(nameof(EdgeSize), () => EdgeTreatment != SolidEdgeTreatment.None);
			change.SetRowVisible(nameof(Rims), () => EdgeTreatment != SolidEdgeTreatment.None);
		}
	}
}
