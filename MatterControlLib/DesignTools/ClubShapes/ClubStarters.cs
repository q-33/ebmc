using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.VectorMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Polygons = System.Collections.Generic.List<System.Collections.Generic.List<ClipperLib.IntPoint>>;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	/// <summary>
	/// EBMC Edition: base of the Club Starters: a fill-in design with a few plainly named fields that makes one printable
	/// part (the Box with Lid makes its two parts side by side). Only move and rotate handles: the sizes are the fields.
	/// </summary>
	public abstract class ClubStarterObject3D : ClubShapeObject3D, IObject3DControlsProvider
	{
		public void AddObject3DControls(Object3DControlsLayer object3DControlsLayer)
		{
			ClubControls.AddMoveRotate(object3DControlsLayer);
		}

		/// <summary>The plate (with any holes) as one body from z0 to z1.</summary>
		protected static ClubMesh Plate(List<Pt> outline, double z0, double z1, params IReadOnlyList<Pt>[] holes)
		{
			return BodyBuilder.Extrude(outline, z0, z1, holes.Where(h => h != null).ToArray());
		}

		/// <summary>The plate's top <paramref name="depth"/> mm with the text (and holes) cut out, for engraved text.</summary>
		protected static PolygonMesh.Mesh EngravedLayer(List<Pt> outline, Polygons text, double z0, double z1, params IReadOnlyList<Pt>[] holes)
		{
			var cut = new Polygons(text);
			cut.AddRange(holes.Where(h => h != null).Select(h => ClubText.ToClipper(h)));
			return ClubText.Extrude(ClubText.Subtract(new Polygons { ClubText.ToClipper(outline) }, cut), z0, z1);
		}
	}

	/// <summary>EBMC Edition (Club Starter): a name tag with raised or engraved letters and a key-ring hole.</summary>
	public class NameTagObject3D : ClubStarterObject3D, IPropertyGridModifier
	{
		public NameTagObject3D()
		{
			Name = "Name Tag".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Text"];
		}

		protected override string BuildTitle => "Name Tag".Localize();

		[DisplayName("Name")]
		public StringOrExpression Text { get; set; } = "Your name";

		[FontSelector]
		public string Font { get; set; } = ClubText.DefaultFont;

		[DisplayName("Fit Width to Name")]
		[Description("On: the tag is as wide as the name needs. Off: use the Width below (a long name gets smaller letters).")]
		public bool AutoWidth { get; set; } = true;

		[MaxDecimalPlaces(1)]
		[Slider(20, 200, Easing.EaseType.Quadratic, snapDistance: 1)]
		public DoubleOrExpression Width { get; set; } = 70;

		[Description("Top to bottom; the letters are a little under half of this.")]
		[MaxDecimalPlaces(1)]
		[Slider(8, 60, Easing.EaseType.Quadratic, snapDistance: .5)]
		public DoubleOrExpression Height { get; set; } = 20;

		[MaxDecimalPlaces(2)]
		[Slider(1, 10, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression Thickness { get; set; } = 3;

		[DisplayName("Letters")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		[JsonConverter(typeof(StringEnumConverter))]
		public TextStyle Letters { get; set; } = TextStyle.Raised;

		[DisplayName("Letter Depth")]
		[Description("How far the letters stand up (raised) or go in (engraved), in mm.")]
		[MaxDecimalPlaces(2)]
		[Slider(0.2, 5, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression LetterDepth { get; set; } = 1;

		[DisplayName("Corner Radius")]
		[MaxDecimalPlaces(1)]
		[Slider(0, 20, Easing.EaseType.Quadratic, snapDistance: .5)]
		public DoubleOrExpression CornerRadius { get; set; } = 4;

		[DisplayName("Ring Hole")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		[JsonConverter(typeof(StringEnumConverter))]
		public RingHolePosition RingHole { get; set; } = RingHolePosition.Left;

		[DisplayName("Hole Diameter")]
		[MaxDecimalPlaces(1)]
		[Slider(1.5, 15, Easing.EaseType.Quadratic, snapDistance: .5)]
		public DoubleOrExpression HoleDiameter { get; set; } = 5;

		public static async Task<NameTagObject3D> Create()
		{
			var item = new NameTagObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var text = Text.Value(this) ?? "";
			// the face is looked up once, so the letters are measured and drawn with the same font
			var font = ClubText.Resolve(Font);
			double? fixedWidth = AutoWidth ? (double?)null : Width.ClampFinite(this, 20, 400, ref valuesChanged);
			var height = Height.ClampFinite(this, 8, 200, ref valuesChanged);
			var thickness = Thickness.ClampFinite(this, 1, 20, ref valuesChanged);
			var engraved = Letters == TextStyle.Engraved;
			var depth = LetterDepth.ClampFinite(this, 0.2, engraved ? Math.Max(0.2, thickness - 0.6) : 10, ref valuesChanged);
			var corner = CornerRadius.ClampFinite(this, 0, 100, ref valuesChanged);
			var hole = RingHole;
			var holeDiameter = HoleDiameter.ClampFinite(this, 1.5, 50, ref valuesChanged);

			// measured on the UI thread so a too-long name can be reported straight away
			var widthPerCap = ClubText.WidthPerCap(text, font);
			NameTagLayout.Compute(widthPerCap, fixedWidth, height, corner, hole, holeDiameter, problems);

			return token =>
			{
				var layout = NameTagLayout.Compute(widthPerCap, fixedWidth, height, corner, hole, holeDiameter);
				var outline = layout.Outline();
				var holeOutline = layout.HoleOutline();
				var letters = ClubText.CenterOn(ClubText.Line(text, font, layout.CapHeight, token), layout.TextCenterX, layout.TextCenterY);
				var build = new ClubBuild();
				if (engraved && letters.Count > 0)
				{
					build.Bodies.Add(Plate(outline, 0, thickness - depth, holeOutline));
					build.Extra.Add(EngravedLayer(outline, letters, thickness - depth - 0.01, thickness, holeOutline));
				}
				else
				{
					build.Bodies.Add(Plate(outline, 0, thickness, holeOutline));
					build.Extra.Add(ClubText.Extrude(letters, thickness - 0.02, thickness + depth));
				}

				return build;
			};
		}

		public void UpdateControls(PublicPropertyChange change)
		{
			change.SetRowVisible(nameof(Width), () => !AutoWidth);
			change.SetRowVisible(nameof(HoleDiameter), () => RingHole != RingHolePosition.None);
		}
	}

	/// <summary>EBMC Edition (Club Starter): a sign with up to three lines, an optional raised border and two mounting holes.</summary>
	public class SignObject3D : ClubStarterObject3D
	{
		private const double LetterHeight = 1.2;

		public SignObject3D()
		{
			Name = "Sign".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Pyramid"];
		}

		protected override string BuildTitle => "Sign".Localize();

		[DisplayName("Line 1")]
		public StringOrExpression Line1 { get; set; } = "Makers";

		[DisplayName("Line 2")]
		public StringOrExpression Line2 { get; set; } = "at work";

		[DisplayName("Line 3")]
		[Description("Leave a line empty to leave it out; the letters grow to fill the sign.")]
		public StringOrExpression Line3 { get; set; } = "";

		[FontSelector]
		public string Font { get; set; } = ClubText.DefaultFont;

		[MaxDecimalPlaces(1)]
		[Slider(40, 250, Easing.EaseType.Quadratic, snapDistance: 1)]
		public DoubleOrExpression Width { get; set; } = 120;

		[MaxDecimalPlaces(1)]
		[Slider(25, 200, Easing.EaseType.Quadratic, snapDistance: 1)]
		public DoubleOrExpression Height { get; set; } = 60;

		[MaxDecimalPlaces(2)]
		[Slider(1.5, 10, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression Thickness { get; set; } = 4;

		[DisplayName("Raised Border")]
		public bool Border { get; set; } = true;

		[DisplayName("Mounting Holes")]
		[Description("Two 4 mm holes near the top corners.")]
		public bool MountingHoles { get; set; } = true;

		public static async Task<SignObject3D> Create()
		{
			var item = new SignObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var lines = new[] { Line1.Value(this), Line2.Value(this), Line3.Value(this) }.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
			// the face is looked up once, so the letters are measured and drawn with the same font
			var font = ClubText.Resolve(Font);
			var width = Width.ClampFinite(this, 40, 400, ref valuesChanged);
			var height = Height.ClampFinite(this, 25, 300, ref valuesChanged);
			var thickness = Thickness.ClampFinite(this, 1.5, 20, ref valuesChanged);
			var border = Border;
			var holes = MountingHoles;
			var widths = lines.Select(l => ClubText.WidthPerCap(l, font)).ToList();

			return token =>
			{
				var layout = SignLayout.Compute(width, height, border, holes, widths);
				var outline = layout.Outline();
				var holeOutlines = layout.Holes.Select(h => (IReadOnlyList<Pt>)Profiles.Circle(SignLayout.HoleDiameter / 2, 24, h.x, h.y)).ToArray();
				var build = new ClubBuild();
				build.Bodies.Add(Plate(outline, 0, thickness, holeOutlines));
				if (border)
				{
					build.Bodies.Add(BodyBuilder.Extrude(outline, thickness - 0.02, thickness + LetterHeight, layout.BorderInside()));
				}

				for (int i = 0; i < lines.Count; i++)
				{
					var letters = ClubText.CenterOn(ClubText.Line(lines[i], font, layout.CapHeight, token), 0, layout.LineCenters[i]);
					build.Extra.Add(ClubText.Extrude(letters, thickness - 0.02, thickness + LetterHeight));
				}

				return build;
			};
		}
	}

	/// <summary>
	/// EBMC Edition (Club Starter): a box with a lid that fits inside it on a lip. Both parts are made in this one object,
	/// side by side, so they move, save and print together; Apply makes them a plain mesh.
	/// </summary>
	public class BoxWithLidObject3D : ClubStarterObject3D
	{
		public BoxWithLidObject3D()
		{
			Name = "Box with Lid".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Cube"];
		}

		protected override string BuildTitle => "Box with Lid".Localize();

		[DisplayName("Inside Width")]
		[Description("The space inside the box; the walls are added outside.")]
		[MaxDecimalPlaces(1)]
		[Slider(8, 200, Easing.EaseType.Quadratic, snapDistance: 1)]
		public DoubleOrExpression InnerWidth { get; set; } = 40;

		[DisplayName("Inside Depth")]
		[MaxDecimalPlaces(1)]
		[Slider(8, 200, Easing.EaseType.Quadratic, snapDistance: 1)]
		public DoubleOrExpression InnerDepth { get; set; } = 30;

		[DisplayName("Inside Height")]
		[MaxDecimalPlaces(1)]
		[Slider(3, 200, Easing.EaseType.Quadratic, snapDistance: 1)]
		public DoubleOrExpression InnerHeight { get; set; } = 20;

		[MaxDecimalPlaces(2)]
		[Slider(0.8, 6, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression Wall { get; set; } = 2;

		[MaxDecimalPlaces(2)]
		[Slider(0.6, 6, Easing.EaseType.Quadratic, snapDistance: .1)]
		public DoubleOrExpression Floor { get; set; } = 2;

		[DisplayName("Corner Radius")]
		[Description("Of the outside corners.")]
		[MaxDecimalPlaces(1)]
		[Slider(0, 20, Easing.EaseType.Quadratic, snapDistance: .5)]
		public DoubleOrExpression CornerRadius { get; set; } = 4;

		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		[JsonConverter(typeof(StringEnumConverter))]
		public LidStyle Lid { get; set; } = LidStyle.Lip;

		[Description("The gap between the lid's lip and the box's inside wall, all round. More makes a looser fit.")]
		[MaxDecimalPlaces(2)]
		[Slider(0, 1, snapDistance: .05)]
		public DoubleOrExpression Clearance { get; set; } = 0.3;

		public static async Task<BoxWithLidObject3D> Create()
		{
			var item = new BoxWithLidObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var rules = BoxLidRules.Compute(
				InnerWidth.ClampFinite(this, 8, 300, ref valuesChanged),
				InnerDepth.ClampFinite(this, 8, 300, ref valuesChanged),
				InnerHeight.ClampFinite(this, 3, 300, ref valuesChanged),
				Wall.ClampFinite(this, ShapeLimits.MinWall, 10, ref valuesChanged),
				Floor.ClampFinite(this, 0.6, 10, ref valuesChanged),
				CornerRadius.ClampFinite(this, 0, 100, ref valuesChanged),
				Clearance.ClampFinite(this, 0, 1, ref valuesChanged),
				problems);
			var withLid = Lid == LidStyle.Lip;
			return token =>
			{
				var build = new ClubBuild();
				var (boxX, lidX) = rules.Centers(withLid);
				build.Bodies.Add(rules.Box().Translate(boxX, 0, 0));
				if (withLid)
				{
					build.Bodies.Add(rules.Lid().Translate(lidX, 0, 0));
				}

				return build;
			};
		}
	}

	/// <summary>EBMC Edition (Club Starter): a wall hook with two countersunk screw holes, printed on its side.</summary>
	public class WallHookObject3D : ClubStarterObject3D
	{
		public WallHookObject3D()
		{
			Name = "Wall Hook".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Wedge"];
		}

		protected override string BuildTitle => "Wall Hook".Localize();

		[DisplayName("Plate Width")]
		[Description("Side to side on the wall (it prints standing on this edge).")]
		[MaxDecimalPlaces(1)]
		[Slider(10, 100, snapDistance: 1)]
		public DoubleOrExpression PlateWidth { get; set; } = 30;

		[DisplayName("Plate Height")]
		[MaxDecimalPlaces(1)]
		[Slider(20, 200, snapDistance: 1)]
		public DoubleOrExpression PlateHeight { get; set; } = 60;

		[DisplayName("Plate Thickness")]
		[MaxDecimalPlaces(1)]
		[Slider(2, 15, snapDistance: .5)]
		public DoubleOrExpression PlateThickness { get; set; } = 5;

		[DisplayName("Hook Reach")]
		[Description("How far the hook sticks out from the plate.")]
		[MaxDecimalPlaces(1)]
		[Slider(8, 120, snapDistance: 1)]
		public DoubleOrExpression Reach { get; set; } = 25;

		[DisplayName("Hook Tip Height")]
		[Description("How high the turned-up tip rises.")]
		[MaxDecimalPlaces(1)]
		[Slider(3, 60, snapDistance: 1)]
		public DoubleOrExpression HookHeight { get; set; } = 12;

		[DisplayName("Hook Thickness")]
		[MaxDecimalPlaces(1)]
		[Slider(2, 20, snapDistance: .5)]
		public DoubleOrExpression HookThickness { get; set; } = 6;

		[DisplayName("Screw Diameter")]
		[MaxDecimalPlaces(1)]
		[Slider(1.5, 8, snapDistance: .1)]
		public DoubleOrExpression ScrewDiameter { get; set; } = 4;

		[DisplayName("Screw Head Diameter")]
		[Description("For flat-head (countersunk) screws.")]
		[MaxDecimalPlaces(1)]
		[Slider(2, 16, snapDistance: .1)]
		public DoubleOrExpression HeadDiameter { get; set; } = 8;

		public static async Task<WallHookObject3D> Create()
		{
			var item = new WallHookObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var rules = WallHookRules.Compute(
				PlateWidth.ClampFinite(this, 10, 200, ref valuesChanged),
				PlateHeight.ClampFinite(this, 10, 400, ref valuesChanged),
				PlateThickness.ClampFinite(this, 2, 20, ref valuesChanged),
				Reach.ClampFinite(this, 5, 200, ref valuesChanged),
				HookHeight.ClampFinite(this, 2, 200, ref valuesChanged),
				HookThickness.ClampFinite(this, 2, 30, ref valuesChanged),
				ScrewDiameter.ClampFinite(this, 1.5, 10, ref valuesChanged),
				HeadDiameter.ClampFinite(this, 1.5, 30, ref valuesChanged),
				problems);
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(rules.Mesh());
				return build;
			};
		}
	}

	/// <summary>EBMC Edition (Club Starter): a phone stand, printed on its side.</summary>
	public class PhoneStandObject3D : ClubStarterObject3D
	{
		public PhoneStandObject3D()
		{
			Name = "Phone Stand".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Cylinder"];
		}

		protected override string BuildTitle => "Phone Stand".Localize();

		[DisplayName("Phone Thickness")]
		[Description("The slot the phone stands in; measure the phone with its case.")]
		[MaxDecimalPlaces(1)]
		[Slider(5, 30, snapDistance: .5)]
		public DoubleOrExpression PhoneThickness { get; set; } = 12;

		[DisplayName("Lean Angle")]
		[Description("From the table: 60 leans the phone well back, 80 holds it nearly upright.")]
		[MaxDecimalPlaces(0)]
		[Slider(60, 80, snapDistance: 1)]
		public DoubleOrExpression LeanAngle { get; set; } = 70;

		[MaxDecimalPlaces(1)]
		[Slider(30, 150, snapDistance: 1)]
		public DoubleOrExpression Width { get; set; } = 70;

		[DisplayName("Lip Height")]
		[Description("The front lip that stops the phone sliding off.")]
		[MaxDecimalPlaces(1)]
		[Slider(3, 30, snapDistance: .5)]
		public DoubleOrExpression LipHeight { get; set; } = 8;

		[DisplayName("Cable Gap")]
		[Description("A 14 mm gap in the middle of the lip and base for a charging cable.")]
		public bool CableGap { get; set; } = true;

		public static async Task<PhoneStandObject3D> Create()
		{
			var item = new PhoneStandObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var rules = PhoneStandRules.Compute(
				PhoneThickness.ClampFinite(this, 5, 30, ref valuesChanged),
				LeanAngle.ClampFinite(this, 60, 80, ref valuesChanged),
				Width.ClampFinite(this, 30, 200, ref valuesChanged),
				LipHeight.ClampFinite(this, 3, 30, ref valuesChanged),
				CableGap);
			if (CableGap && !rules.CableGap)
			{
				problems.Add("The stand is too narrow for a cable gap, so it was left out.");
			}

			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(rules.Mesh());
				return build;
			};
		}
	}

	/// <summary>EBMC Edition (Club Starter): a knob for a round or D-shaped shaft, smooth or fluted; printed with the shaft hole down.</summary>
	public class KnobObject3D : ClubStarterObject3D, IPropertyGridModifier
	{
		public KnobObject3D()
		{
			Name = "Knob".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["Torus"];
		}

		protected override string BuildTitle => "Knob".Localize();

		[MaxDecimalPlaces(1)]
		[Slider(8, 80, snapDistance: .5)]
		public DoubleOrExpression Diameter { get; set; } = 30;

		[MaxDecimalPlaces(1)]
		[Slider(4, 50, snapDistance: .5)]
		public DoubleOrExpression Height { get; set; } = 15;

		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		[JsonConverter(typeof(StringEnumConverter))]
		public KnobGrip Grip { get; set; } = KnobGrip.Fluted;

		[Description("Grooves round the edge for grip.")]
		[Slider(3, 40, snapDistance: 1)]
		public IntOrExpression Flutes { get; set; } = 12;

		[DisplayName("Shaft")]
		[Description("D-shaft: a round shaft with one flat side (common on knobs for volume controls).")]
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		[JsonConverter(typeof(StringEnumConverter))]
		public KnobShaft Shaft { get; set; } = KnobShaft.DShaft;

		[DisplayName("Shaft Diameter")]
		[MaxDecimalPlaces(2)]
		[Slider(2, 12, snapDistance: .1)]
		public DoubleOrExpression ShaftDiameter { get; set; } = 6;

		[DisplayName("Shaft Depth")]
		[MaxDecimalPlaces(1)]
		[Slider(2, 40, snapDistance: .5)]
		public DoubleOrExpression ShaftDepth { get; set; } = 10;

		[Description("Added to the shaft hole all round so the knob slides on.")]
		[MaxDecimalPlaces(2)]
		[Slider(0, 0.6, snapDistance: .05)]
		public DoubleOrExpression Clearance { get; set; } = 0.2;

		public static async Task<KnobObject3D> Create()
		{
			var item = new KnobObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var diameter = Diameter.ClampFinite(this, 8, 150, ref valuesChanged);
			var rules = KnobRules.Compute(
				diameter,
				Height.ClampFinite(this, 4, 100, ref valuesChanged),
				Grip,
				Flutes.ClampFinite(this, 3, KnobRules.MaxFlutes(diameter), ref valuesChanged),
				Shaft,
				ShaftDiameter.ClampFinite(this, 1, 100, ref valuesChanged),
				ShaftDepth.ClampFinite(this, 1, 100, ref valuesChanged),
				Clearance.ClampFinite(this, 0, 1, ref valuesChanged),
				problems);
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(rules.Mesh());
				return build;
			};
		}

		public void UpdateControls(PublicPropertyChange change)
		{
			change.SetRowVisible(nameof(Flutes), () => Grip == KnobGrip.Fluted);
		}
	}

	/// <summary>EBMC Edition (Club Starter): a snap-in cable clip with an optional screw tab.</summary>
	public class CableClipObject3D : ClubStarterObject3D, IPropertyGridModifier
	{
		public CableClipObject3D()
		{
			Name = "Cable Clip".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["HalfCylinder"];
		}

		protected override string BuildTitle => "Cable Clip".Localize();

		[DisplayName("Cable Diameter")]
		[MaxDecimalPlaces(1)]
		[Slider(2, 20, snapDistance: .5)]
		public DoubleOrExpression CableDiameter { get; set; } = 6;

		[DisplayName("Opening Width")]
		[Description("The gap the cable is pushed through; a little narrower than the cable so it snaps in.")]
		[MaxDecimalPlaces(1)]
		[Slider(1, 20, snapDistance: .1)]
		public DoubleOrExpression Opening { get; set; } = 4.5;

		[Description("Along the cable.")]
		[MaxDecimalPlaces(1)]
		[Slider(4, 40, snapDistance: .5)]
		public DoubleOrExpression Width { get; set; } = 10;

		[MaxDecimalPlaces(1)]
		[Slider(1.2, 5, snapDistance: .1)]
		public DoubleOrExpression Wall { get; set; } = 2;

		[DisplayName("Screw Tab")]
		public bool ScrewTab { get; set; } = true;

		[DisplayName("Hole Diameter")]
		[MaxDecimalPlaces(1)]
		[Slider(1.5, 8, snapDistance: .1)]
		public DoubleOrExpression HoleDiameter { get; set; } = 3.5;

		public static async Task<CableClipObject3D> Create()
		{
			var item = new CableClipObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var rules = CableClipRules.Compute(
				CableDiameter.ClampFinite(this, 2, 40, ref valuesChanged),
				Opening.ClampFinite(this, 0.5, 80, ref valuesChanged),
				Width.ClampFinite(this, 4, 100, ref valuesChanged),
				Wall.ClampFinite(this, 1.2, 6, ref valuesChanged),
				ScrewTab,
				HoleDiameter.ClampFinite(this, 1.5, 8, ref valuesChanged),
				problems);
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(rules.Mesh());
				return build;
			};
		}

		public void UpdateControls(PublicPropertyChange change)
		{
			change.SetRowVisible(nameof(HoleDiameter), () => ScrewTab);
		}
	}

	/// <summary>EBMC Edition (Club Starter): a plant label lying flat, with raised letters on the blade and a stake.</summary>
	public class PlantLabelObject3D : ClubStarterObject3D
	{
		private const double Thickness = 2;

		private const double LetterHeight = 1;

		public PlantLabelObject3D()
		{
			Name = "Plant Label".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["HalfWedge"];
		}

		protected override string BuildTitle => "Plant Label".Localize();

		[DisplayName("Plant Name")]
		public StringOrExpression Text { get; set; } = "Basil";

		[FontSelector]
		public string Font { get; set; } = ClubText.DefaultFont;

		[DisplayName("Blade Length")]
		[Description("The part above the soil that carries the name.")]
		[MaxDecimalPlaces(1)]
		[Slider(15, 150, snapDistance: 1)]
		public DoubleOrExpression BladeLength { get; set; } = 60;

		[DisplayName("Blade Width")]
		[MaxDecimalPlaces(1)]
		[Slider(8, 60, snapDistance: .5)]
		public DoubleOrExpression BladeWidth { get; set; } = 20;

		[DisplayName("Stake Length")]
		[Description("The part pushed into the soil.")]
		[MaxDecimalPlaces(1)]
		[Slider(5, 200, snapDistance: 1)]
		public DoubleOrExpression StakeLength { get; set; } = 80;

		[DisplayName("Stake Width")]
		[MaxDecimalPlaces(1)]
		[Slider(3, 40, snapDistance: .5)]
		public DoubleOrExpression StakeWidth { get; set; } = 10;

		[DisplayName("Pointed Tip")]
		public bool Pointed { get; set; } = true;

		public static async Task<PlantLabelObject3D> Create()
		{
			var item = new PlantLabelObject3D();
			await item.BuildDetached();
			return item;
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var text = Text.Value(this) ?? "";
			// the face is looked up once, so the letters are measured and drawn with the same font
			var font = ClubText.Resolve(Font);
			var rules = PlantLabelRules.Compute(
				BladeLength.ClampFinite(this, 15, 250, ref valuesChanged),
				BladeWidth.ClampFinite(this, 8, 100, ref valuesChanged),
				StakeLength.ClampFinite(this, 5, 300, ref valuesChanged),
				StakeWidth.ClampFinite(this, 3, 100, ref valuesChanged),
				Pointed);
			var widthPerCap = ClubText.WidthPerCap(text, font);
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(Plate(rules.Outline(), 0, Thickness));
				var (x, y) = rules.TextCenter;
				var letters = ClubText.CenterOn(ClubText.Line(text, font, rules.CapHeight(widthPerCap), token), x, y);
				build.Extra.Add(ClubText.Extrude(letters, Thickness - 0.02, Thickness + LetterHeight));
				return build;
			};
		}
	}

	/// <summary>
	/// EBMC Edition (Club Starter): a QR code tag: the code's dark squares raised 1 mm on a plate, the code's own margin as
	/// the border, with an optional ring hole. Uses the same QR generator and default text as the QR Code part. Print the
	/// raised squares in a second colour (pause at the layer change) for a code a phone can read.
	/// </summary>
	public class QrTagObject3D : ClubStarterObject3D, IPropertyGridModifier
	{
		private const double Raise = 1;

		public QrTagObject3D()
		{
			Name = "QR Tag".Localize();
			Color = Operations.Object3DExtensions.PrimitiveColors["HalfSphere"];
		}

		protected override string BuildTitle => "QR Tag".Localize();

		[Description("The web address or text the code holds.")]
		public StringOrExpression Text { get; set; } = string.IsNullOrEmpty(SettingsManagement.OemSettings.Instance.QrCodeDefaultText)
			? "https://www.matterhackers.com"
			: SettingsManagement.OemSettings.Instance.QrCodeDefaultText;

		[Description("Width of the tag; the code fills it with its own white margin.")]
		[MaxDecimalPlaces(1)]
		[Slider(15, 150, snapDistance: 1)]
		public DoubleOrExpression Size { get; set; } = 40;

		[MaxDecimalPlaces(2)]
		[Slider(1, 6, snapDistance: .1)]
		public DoubleOrExpression Thickness { get; set; } = 2;

		[DisplayName("Ring Hole")]
		public bool RingHole { get; set; } = true;

		[DisplayName("Hole Diameter")]
		[MaxDecimalPlaces(1)]
		[Slider(2, 15, snapDistance: .5)]
		public DoubleOrExpression HoleDiameter { get; set; } = 5;

		public static async Task<QrTagObject3D> Create()
		{
			var item = new QrTagObject3D();
			await item.BuildDetached();
			return item;
		}

		/// <summary>The QR modules (rows from the top, including the quiet zone), as the QR Code part makes them.</summary>
		public static List<IReadOnlyList<bool>> Modules(string text)
		{
			var generator = new QRCoder.QRCodeGenerator();
			var data = generator.CreateQrCode(string.IsNullOrEmpty(text) ? " " : text, QRCoder.QRCodeGenerator.ECCLevel.Q);
			return data.ModuleMatrix.Select(row => (IReadOnlyList<bool>)Enumerable.Range(0, row.Length).Select(i => row[i]).ToList()).ToList();
		}

		protected override Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems)
		{
			var text = Text.Value(this) ?? "";
			var size = Size.ClampFinite(this, 15, 200, ref valuesChanged);
			var thickness = Thickness.ClampFinite(this, 1, 10, ref valuesChanged);
			var holeDiameter = HoleDiameter.ClampFinite(this, 2, 15, ref valuesChanged);
			var ringHole = RingHole;
			List<IReadOnlyList<bool>> modules;
			try
			{
				modules = Modules(text);
			}
			catch (Exception)
			{
				problems.Add("This text is too long for a QR code.");
				modules = Modules(" ");
			}

			var rules = QrTagRules.Compute(size, modules.Count, ringHole, holeDiameter, problems);
			return token =>
			{
				var build = new ClubBuild();
				build.Bodies.Add(rules.Plate(thickness));

				// the dark squares joined into outlines (neighbours share edges, diagonal ones are joined by a 0.01 mm
				// overlap) and raised as one body
				var squares = rules.ModuleRectangles(modules).Select(ClubText.ToClipper).ToList();
				build.Extra.Add(ClubText.Extrude(ClubText.Union(squares, 0.01), thickness - 0.02, thickness + Raise));
				return build;
			};
		}

		public void UpdateControls(PublicPropertyChange change)
		{
			change.SetRowVisible(nameof(HoleDiameter), () => RingHole);
		}
	}
}
