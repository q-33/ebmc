using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	public enum RingHolePosition
	{
		None,
		Left,
		Top
	}

	public enum TextStyle
	{
		Raised,
		Engraved
	}

	public enum KnobGrip
	{
		Smooth,
		Fluted
	}

	public enum KnobShaft
	{
		Round,
		DShaft
	}

	public enum LidStyle
	{
		Lip,
		None
	}

	/// <summary>A run of a design that does not fit: what was changed, in plain words (shown as a notice).</summary>
	public sealed class StarterProblems
	{
		private readonly List<string> messages = new List<string>();

		public IReadOnlyList<string> Messages => messages;

		public void Add(string message)
		{
			if (!messages.Contains(message))
			{
				messages.Add(message);
			}
		}
	}

	/// <summary>Countersink sizes for a flat-head screw (90° head).</summary>
	public static class Countersink
	{
		/// <summary>How deep a 90° countersink is from the head diameter to the shaft diameter.</summary>
		public static double Depth(double shaftDiameter, double headDiameter) => Math.Max(0, (headDiameter - shaftDiameter) / 2);

		/// <summary>
		/// The countersink depth that fits a plate: at most the plate thickness minus <paramref name="minStraight"/> of
		/// straight hole. Returns the depth and whether it had to be made shallower.
		/// </summary>
		public static (double depth, bool shallower) Fit(double shaftDiameter, double headDiameter, double plateThickness, double minStraight = 1)
		{
			var full = Depth(shaftDiameter, headDiameter);
			var max = Math.Max(0, plateThickness - minStraight);
			return full > max ? (max, true) : (full, false);
		}

		/// <summary>A round hole with a countersink at the top, as rings for BodyBuilder (from z0 to z1).</summary>
		public static Contour Hole(double cx, double cy, double shaftDiameter, double depth, double z0, double z1, int sides = 32)
		{
			var shaft = Profiles.Circle(shaftDiameter / 2, sides, cx, cy);
			if (depth <= 0.01)
			{
				return Contour.Prism(shaft, z0, z1);
			}

			var head = Profiles.Circle(shaftDiameter / 2 + depth, sides, cx, cy);
			return new Contour(new Ring(z0, shaft), new Ring(z1 - depth, shaft), new Ring(z1, head));
		}
	}

	/// <summary>
	/// EBMC Edition: the layout of a Name Tag. Text sizes are given as the text's width per millimetre of capital-letter
	/// height (measured from the font by the app), so the layout itself stays pure.
	/// </summary>
	public sealed class NameTagLayout
	{
		public const double RingWall = 2.5;

		/// <summary>The widest tag, fixed or fitted to the name.</summary>
		public const double MaxWidth = 400;

		public double PlateWidth { get; private set; }

		public double PlateHeight { get; private set; }

		public double CornerRadius { get; private set; }

		public double CapHeight { get; private set; }

		public double TextCenterX { get; private set; }

		public double TextCenterY { get; private set; }

		/// <summary>Width available for the text.</summary>
		public double TextWidth { get; private set; }

		public double HoleDiameter { get; private set; }

		public double HoleX { get; private set; }

		public double HoleY { get; private set; }

		public bool HasHole { get; private set; }

		/// <summary>True when a fixed width made the letters smaller than the height allows.</summary>
		public bool Shrunk { get; private set; }

		public static double Margin(double height) => Math.Max(2, 0.15 * height);

		/// <param name="widthPerCap">Text width (mm) per mm of capital height; 0 for no text.</param>
		/// <param name="fixedWidth">The plate width, or null to fit the plate to the text.</param>
		public static NameTagLayout Compute(double widthPerCap, double? fixedWidth, double height, double cornerRadius, RingHolePosition hole, double holeDiameter, StarterProblems problems = null)
		{
			var layout = new NameTagLayout();
			height = ShapeLimits.Clamp(height, 8, 200);
			var margin = Margin(height);
			var cap = 0.45 * height;
			layout.HasHole = hole != RingHolePosition.None;

			var maxHole = hole == RingHolePosition.Left ? height - 2 * RingWall : height;
			layout.HoleDiameter = ShapeLimits.Clamp(holeDiameter, 1.5, Math.Max(1.5, maxHole));
			var zone = layout.HasHole ? layout.HoleDiameter + 2 * RingWall : 0;

			var leftZone = hole == RingHolePosition.Left ? zone : margin;
			double width;
			if (fixedWidth.HasValue)
			{
				width = ShapeLimits.Clamp(fixedWidth.Value, leftZone + margin + 5, MaxWidth);
				var available = width - leftZone - margin;
				if (widthPerCap > 0 && widthPerCap * cap > available)
				{
					cap = available / widthPerCap;
					layout.Shrunk = true;
					problems?.Add("The name is longer than the tag, so the letters were made smaller. Make the tag wider or set Width to Auto.");
				}
			}
			else
			{
				width = leftZone + margin + Math.Max(10, widthPerCap * cap);
				if (width > MaxWidth)
				{
					// as long as the widest tag allows: the letters get smaller, as with a fixed width
					width = MaxWidth;
					cap = (width - leftZone - margin) / widthPerCap;
					layout.Shrunk = true;
					problems?.Add("The name is too long for the widest tag (400 mm), so the letters were made smaller.");
				}
			}

			if (hole == RingHolePosition.Top)
			{
				// the hole sits across the top: it must leave a ring wall each side within the tag's width
				var maxTop = Math.Max(1.5, width - 2 * RingWall);
				if (layout.HoleDiameter > maxTop)
				{
					layout.HoleDiameter = maxTop;
					zone = layout.HoleDiameter + 2 * RingWall;
					problems?.Add("The ring hole was made smaller so it fits inside the tag.");
				}
			}

			var plateHeight = hole == RingHolePosition.Top ? height + zone - RingWall : height;
			layout.PlateWidth = width;
			layout.PlateHeight = plateHeight;
			layout.CapHeight = cap;
			layout.CornerRadius = ShapeLimits.Clamp(cornerRadius, 0, Math.Min(width, plateHeight) / 2 * 0.99);
			layout.TextWidth = width - leftZone - margin;
			layout.TextCenterX = -width / 2 + leftZone + layout.TextWidth / 2;
			layout.TextCenterY = hole == RingHolePosition.Top ? -plateHeight / 2 + height / 2 : 0;
			if (hole == RingHolePosition.Left)
			{
				layout.HoleX = -width / 2 + RingWall + layout.HoleDiameter / 2;
				layout.HoleY = 0;
			}
			else if (hole == RingHolePosition.Top)
			{
				layout.HoleX = 0;
				layout.HoleY = plateHeight / 2 - RingWall - layout.HoleDiameter / 2;
			}

			return layout;
		}

		/// <summary>The plate outline (counter-clockwise, centred).</summary>
		public List<Pt> Outline() => Profiles.RoundedRectangle(PlateWidth, PlateHeight, CornerRadius, Profiles.ArcSegments(CornerRadius));

		public List<Pt> HoleOutline() => HasHole ? Profiles.Circle(HoleDiameter / 2, 32, HoleX, HoleY) : null;
	}

	/// <summary>EBMC Edition: the layout of a Sign: up to three centred lines, an optional raised border and two mounting holes.</summary>
	public sealed class SignLayout
	{
		public const double BorderWidth = 3;

		public const double HoleDiameter = 4;

		public const double HoleInset = 8;

		public const double LinePitch = 1.6;

		public double Width { get; private set; }

		public double Height { get; private set; }

		public double CornerRadius { get; private set; }

		public double CapHeight { get; private set; }

		public double TextWidth { get; private set; }

		public IReadOnlyList<double> LineCenters { get; private set; }

		public IReadOnlyList<(double x, double y)> Holes { get; private set; }

		public static SignLayout Compute(double width, double height, bool border, bool holes, IReadOnlyList<double> widthPerCapPerLine)
		{
			var layout = new SignLayout
			{
				Width = ShapeLimits.Clamp(width, 40, 400),
				Height = ShapeLimits.Clamp(height, 25, 300)
			};
			layout.CornerRadius = Math.Min(4, Math.Min(layout.Width, layout.Height) / 8);
			var edge = (border ? BorderWidth : 0) + 4;
			var top = layout.Height / 2 - (holes ? HoleInset + HoleDiameter / 2 + 3 : edge);
			var bottom = -layout.Height / 2 + edge;
			var availableHeight = Math.Max(2, top - bottom);
			layout.TextWidth = Math.Max(2, layout.Width - 2 * edge);

			var lines = widthPerCapPerLine.Count;
			var cap = Math.Min(25, availableHeight / (LinePitch * Math.Max(0, lines - 1) + 1.3));
			var widest = widthPerCapPerLine.Count == 0 ? 0 : widthPerCapPerLine.Max();
			if (widest > 0)
			{
				cap = Math.Min(cap, layout.TextWidth / widest);
			}

			layout.CapHeight = cap;
			var middle = (top + bottom) / 2;
			var centers = new List<double>();
			for (int i = 0; i < lines; i++)
			{
				centers.Add(middle + (lines - 1) / 2.0 * LinePitch * cap - i * LinePitch * cap);
			}

			layout.LineCenters = centers;
			layout.Holes = holes
				? new[] { (-layout.Width / 2 + HoleInset, layout.Height / 2 - HoleInset), (layout.Width / 2 - HoleInset, layout.Height / 2 - HoleInset) }
				: new (double, double)[0];
			return layout;
		}

		public List<Pt> Outline() => Profiles.RoundedRectangle(Width, Height, CornerRadius, Profiles.ArcSegments(CornerRadius));

		/// <summary>The inside edge of the border.</summary>
		public List<Pt> BorderInside() => Profiles.RoundedRectangle(Width - 2 * BorderWidth, Height - 2 * BorderWidth, Math.Max(0, CornerRadius - BorderWidth), Profiles.ArcSegments(CornerRadius));
	}

	/// <summary>EBMC Edition: the sizes of a Box with Lid, from the inside size.</summary>
	public sealed class BoxLidRules
	{
		public const double Gap = 6;

		public double InnerWidth { get; private set; }

		public double InnerDepth { get; private set; }

		public double InnerHeight { get; private set; }

		public double Wall { get; private set; }

		public double Floor { get; private set; }

		public double Clearance { get; private set; }

		public double OuterWidth => InnerWidth + 2 * Wall;

		public double OuterDepth => InnerDepth + 2 * Wall;

		public double OuterHeight => InnerHeight + Floor;

		public double CornerRadius { get; private set; }

		public double InnerCornerRadius => Math.Max(0, CornerRadius - Wall);

		/// <summary>The lip fits inside the box with the clearance all round: inner size minus twice the clearance.</summary>
		public double LipOuterWidth => InnerWidth - 2 * Clearance;

		public double LipOuterDepth => InnerDepth - 2 * Clearance;

		public double LipCornerRadius => Math.Max(0, InnerCornerRadius - Clearance);

		public double LipWall { get; private set; }

		public double LipInnerWidth => LipOuterWidth - 2 * LipWall;

		public double LipInnerDepth => LipOuterDepth - 2 * LipWall;

		public double LipInnerCornerRadius => Math.Max(0, LipCornerRadius - LipWall);

		public double LipHeight { get; private set; }

		public static BoxLidRules Compute(double innerWidth, double innerDepth, double innerHeight, double wall, double floor, double cornerRadius, double clearance, StarterProblems problems = null)
		{
			var r = new BoxLidRules
			{
				Wall = ShapeLimits.Clamp(wall, ShapeLimits.MinWall, 10),
				Floor = ShapeLimits.Clamp(floor, 0.6, 10),
				Clearance = ShapeLimits.Clamp(clearance, 0, 1),
				InnerWidth = ShapeLimits.Clamp(innerWidth, 8, 300),
				InnerDepth = ShapeLimits.Clamp(innerDepth, 8, 300),
				InnerHeight = ShapeLimits.Clamp(innerHeight, 3, 300),
			};
			r.CornerRadius = ShapeLimits.Clamp(cornerRadius, 0, Math.Min(r.OuterWidth, r.OuterDepth) / 2 * 0.99);
			var smallest = Math.Min(r.LipOuterWidth, r.LipOuterDepth);
			r.LipWall = ShapeLimits.Clamp(r.Wall, ShapeLimits.MinWall, Math.Max(ShapeLimits.MinWall, (smallest - 2) / 2));
			r.LipHeight = Math.Max(1, Math.Min(4, r.InnerHeight * 0.5));
			if (r.Wall != wall || r.Floor != floor || r.Clearance != clearance)
			{
				problems?.Add("Wall, floor and clearance were kept within what prints well.");
			}

			return r;
		}

		/// <summary>X centres of the box and the lid, side by side with a gap.</summary>
		public (double box, double lid) Centers(bool withLid) => withLid ? (-(OuterWidth + Gap) / 2, (OuterWidth + Gap) / 2) : (0, 0);

		public ClubMesh Box()
		{
			var segs = Profiles.ArcSegments(CornerRadius);
			var outer = Profiles.RoundedRectangle(OuterWidth, OuterDepth, CornerRadius, segs);
			var inner = Profiles.RoundedRectangle(InnerWidth, InnerDepth, InnerCornerRadius, segs);
			return BodyBuilder.Build(Contour.Prism(outer, 0, OuterHeight), Contour.Prism(inner, Floor, OuterHeight));
		}

		/// <summary>The lid, printed upside down: a plate the size of the box with the lip standing on it.</summary>
		public ClubMesh Lid()
		{
			var segs = Profiles.ArcSegments(CornerRadius);
			var plate = Profiles.RoundedRectangle(OuterWidth, OuterDepth, CornerRadius, segs);
			var lip = Profiles.RoundedRectangle(LipOuterWidth, LipOuterDepth, LipCornerRadius, segs);
			var lipInside = Profiles.RoundedRectangle(LipInnerWidth, LipInnerDepth, LipInnerCornerRadius, segs);
			var top = Floor + LipHeight;
			var outer = new Contour(new Ring(0, plate), new Ring(Floor, plate), new Ring(Floor, lip), new Ring(top, lip));
			return BodyBuilder.Build(outer, Contour.Prism(lipInside, Floor, top));
		}
	}

	/// <summary>
	/// EBMC Edition: a wall hook, printed on its side: the back plate stands on one long edge (its width is the print
	/// height) and the hook's L-shaped arm sticks out from the bottom of the plate's front with an upturned tip.
	/// </summary>
	public sealed class WallHookRules
	{
		public double PlateWidth { get; private set; }

		public double PlateHeight { get; private set; }

		public double PlateThickness { get; private set; }

		public double Reach { get; private set; }

		public double HookHeight { get; private set; }

		public double HookThickness { get; private set; }

		public double ScrewDiameter { get; private set; }

		public double HeadDiameter { get; private set; }

		public double CountersinkDepth { get; private set; }

		/// <summary>Clearance between the hook's tip and the lower screw head, so a screwdriver reaches past the tip.</summary>
		public const double TipClearance = 5;

		/// <summary>Space between the two screw heads, and from the upper head to the top of the plate.</summary>
		public const double HeadGap = 3;

		/// <summary>The tallest plate.</summary>
		public const double MaxPlateHeight = 400;

		/// <summary>
		/// Heights of the two screw holes up the plate (mounted orientation). The lower one is above the upturned tip with
		/// <see cref="TipClearance"/> to spare, so a screwdriver held square to the wall passes the tip.
		/// </summary>
		public (double lower, double upper) HoleHeights => (HookHeight + HeadDiameter / 2 + TipClearance, PlateHeight - HeadDiameter / 2 - HeadGap);

		/// <summary>The plate height both screw holes need above the tip.</summary>
		public static double MinPlateHeight(double hookHeight, double headDiameter) => hookHeight + 2 * headDiameter + TipClearance + 2 * HeadGap;

		public static WallHookRules Compute(double plateWidth, double plateHeight, double plateThickness, double reach, double hookHeight, double hookThickness, double screwDiameter, double headDiameter, StarterProblems problems = null)
		{
			var r = new WallHookRules
			{
				PlateWidth = ShapeLimits.Clamp(plateWidth, 10, 200),
				PlateThickness = ShapeLimits.Clamp(plateThickness, 2, 20),
				HookThickness = ShapeLimits.Clamp(hookThickness, 2, 30),
				ScrewDiameter = ShapeLimits.Clamp(screwDiameter, 1.5, 10),
			};
			// the screw first (at most the plate width less 2 mm a side), then the head between the screw and that width,
			// so a countersink never reaches the plate's edge
			r.ScrewDiameter = Math.Min(r.ScrewDiameter, r.PlateWidth - 4);
			r.HeadDiameter = ShapeLimits.Clamp(headDiameter, r.ScrewDiameter, Math.Max(r.ScrewDiameter, r.PlateWidth - 4));
			r.Reach = ShapeLimits.Clamp(reach, r.HookThickness + 3, 200);

			// the tip can rise at most as far as the tallest plate still leaves room for both screws above it
			var maxHook = Math.Max(r.HookThickness, MaxPlateHeight - (MinPlateHeight(0, r.HeadDiameter)));
			r.HookHeight = ShapeLimits.Clamp(hookHeight, r.HookThickness, Math.Min(200, maxHook));
			if (r.HookHeight < hookHeight - 1e-9)
			{
				problems?.Add("The hook's tip was made lower so both screws still fit on the plate above it.");
			}

			// room for two screw heads above the tip
			var minHeight = MinPlateHeight(r.HookHeight, r.HeadDiameter);
			r.PlateHeight = ShapeLimits.Clamp(plateHeight, minHeight, Math.Max(minHeight, MaxPlateHeight));
			if (r.PlateHeight > plateHeight + 1e-9)
			{
				problems?.Add("The plate was made taller so both screw holes fit above the hook's tip.");
			}

			var (depth, shallower) = Countersink.Fit(r.ScrewDiameter, r.HeadDiameter, r.PlateThickness);
			r.CountersinkDepth = depth;
			if (shallower)
			{
				problems?.Add("The plate is too thin for the full countersink, so the screw heads will stand a little proud.");
			}

			return r;
		}

		public ClubMesh Mesh()
		{
			var mesh = new ClubMesh();

			// the plate, drawn flat (X = width, Y = height) and extruded through its thickness, then turned so the
			// thickness runs along X, the width along Z (print height) and the countersinks face the hook (+X)
			var corner = Math.Min(2, Math.Min(PlateWidth, PlateHeight) / 4);
			var outline = Profiles.Translate(Profiles.RoundedRectangle(PlateWidth, PlateHeight, corner, Profiles.ArcSegments(corner)), PlateWidth / 2, PlateHeight / 2);
			var (lower, upper) = HoleHeights;
			var plate = BodyBuilder.Build(
				Contour.Prism(outline, 0, PlateThickness),
				Countersink.Hole(PlateWidth / 2, lower, ScrewDiameter, CountersinkDepth, 0, PlateThickness),
				Countersink.Hole(PlateWidth / 2, upper, ScrewDiameter, CountersinkDepth, 0, PlateThickness));
			plate.Transform(ClubMesh.XyToZy);
			mesh.Add(plate);

			// the arm: an L in XY (out from the plate, then up), extruded through the plate width along Z; it starts half
			// a millimetre inside the plate so the two bodies overlap rather than just touch
			var t = PlateThickness;
			var x0 = t - Math.Min(0.5, t / 2);
			var tip = t + Reach;
			// a tip no taller than the arm is a straight peg: two of the L's corners would coincide (a zero-length edge)
			var arm = HookHeight - HookThickness < 0.01
				? new List<Pt>
				{
					new Pt(x0, 0),
					new Pt(tip, 0),
					new Pt(tip, HookThickness),
					new Pt(x0, HookThickness),
				}
				: new List<Pt>
				{
					new Pt(x0, 0),
					new Pt(tip, 0),
					new Pt(tip, HookHeight),
					new Pt(tip - HookThickness, HookHeight),
					new Pt(tip - HookThickness, HookThickness),
					new Pt(x0, HookThickness),
				};
			mesh.Add(BodyBuilder.Extrude(arm, 0, PlateWidth));
			return mesh;
		}
	}

	/// <summary>EBMC Edition: a phone stand, printed on its side profile (the width runs along Y).</summary>
	public sealed class PhoneStandRules
	{
		public const double Wall = 4;

		public const double BackHeight = 60;

		public const double CableGapWidth = 14;

		public double PhoneThickness { get; private set; }

		public double AngleDegrees { get; private set; }

		public double Width { get; private set; }

		public double LipHeight { get; private set; }

		public bool CableGap { get; private set; }

		public static PhoneStandRules Compute(double phoneThickness, double angleDegrees, double width, double lipHeight, bool cableGap)
		{
			return new PhoneStandRules
			{
				PhoneThickness = ShapeLimits.Clamp(phoneThickness, 5, 30),
				AngleDegrees = ShapeLimits.Clamp(angleDegrees, 60, 80),
				Width = ShapeLimits.Clamp(width, 30, 200),
				LipHeight = ShapeLimits.Clamp(lipHeight, 3, 30),
				CableGap = cableGap && width >= CableGapWidth + 20,
			};
		}

		private Pt SlotBack => new Pt(Wall + PhoneThickness, Wall);

		private Pt RestTopFront
		{
			get
			{
				var a = AngleDegrees * Math.PI / 180;
				return new Pt(SlotBack.X + BackHeight / Math.Tan(a), Wall + BackHeight);
			}
		}

		private Pt RestTopBack
		{
			get
			{
				var a = AngleDegrees * Math.PI / 180;
				return RestTopFront + new Pt(Math.Sin(a), -Math.Cos(a)) * Wall;
			}
		}

		public double Depth => RestTopBack.X + 2;

		/// <summary>The back rest and the base behind the phone, as a side profile (X forward-back, Y up).</summary>
		public List<Pt> RestProfile() => new List<Pt>
		{
			new Pt(SlotBack.X, 0),
			new Pt(Depth, 0),
			new Pt(Depth, Wall),
			RestTopBack,
			RestTopFront,
			SlotBack,
		};

		/// <summary>The front lip and the base under the phone, overlapping the rest by half a millimetre.</summary>
		public List<Pt> LipProfile() => new List<Pt>
		{
			new Pt(0, 0),
			new Pt(SlotBack.X + 0.5, 0),
			new Pt(SlotBack.X + 0.5, Wall),
			new Pt(Wall, Wall),
			new Pt(Wall, Wall + LipHeight),
			new Pt(0, Wall + LipHeight),
		};

		public ClubMesh Mesh()
		{
			var mesh = new ClubMesh();
			mesh.Add(BodyBuilder.Extrude(RestProfile(), 0, Width).Transform(ClubMesh.XyToXz));
			if (CableGap)
			{
				var side = (Width - CableGapWidth) / 2;
				mesh.Add(BodyBuilder.Extrude(LipProfile(), 0, side).Transform(ClubMesh.XyToXz));
				mesh.Add(BodyBuilder.Extrude(LipProfile(), Width - side, Width).Transform(ClubMesh.XyToXz));
			}
			else
			{
				mesh.Add(BodyBuilder.Extrude(LipProfile(), 0, Width).Transform(ClubMesh.XyToXz));
			}

			return mesh;
		}
	}

	/// <summary>EBMC Edition: a knob with a blind shaft hole from the bottom (printed with the hole on the bed).</summary>
	public sealed class KnobRules
	{
		public double Diameter { get; private set; }

		public double Height { get; private set; }

		public KnobGrip Grip { get; private set; }

		public int Flutes { get; private set; }

		public KnobShaft Shaft { get; private set; }

		public double ShaftDiameter { get; private set; }

		public double ShaftDepth { get; private set; }

		public double Clearance { get; private set; }

		/// <summary>Most flutes that keep each at least 3 mm of rim.</summary>
		public static int MaxFlutes(double diameter) => Math.Max(3, (int)Math.Floor(Math.PI * diameter / 3));

		public static int ClampFlutes(int flutes, double diameter) => ShapeLimits.Clamp(flutes, 3, MaxFlutes(diameter));

		public double FluteDepth => Math.Min(1.5, Diameter * 0.06);

		/// <summary>Radius of the shaft hole: the shaft plus the clearance.</summary>
		public double HoleRadius => ShaftDiameter / 2 + Clearance;

		/// <summary>Distance from the centre to the D-shaft's flat (a 6 mm shaft is 4.5 mm across the flat), plus the clearance.</summary>
		public double FlatDistance => ShaftDiameter * 0.25 + Clearance;

		public static KnobRules Compute(double diameter, double height, KnobGrip grip, int flutes, KnobShaft shaft, double shaftDiameter, double shaftDepth, double clearance, StarterProblems problems = null)
		{
			var r = new KnobRules
			{
				Diameter = ShapeLimits.Clamp(diameter, 8, 150),
				Height = ShapeLimits.Clamp(height, 4, 100),
				Grip = grip,
				Shaft = shaft,
				Clearance = ShapeLimits.Clamp(clearance, 0, 1),
			};
			r.Flutes = ClampFlutes(flutes, r.Diameter);
			r.ShaftDiameter = ShapeLimits.Clamp(shaftDiameter, 1, Math.Max(1, r.Diameter - 6 - 2 * r.Clearance));
			r.ShaftDepth = ShapeLimits.Clamp(shaftDepth, 1, r.Height - 1.5);
			if (r.ShaftDiameter < shaftDiameter - 1e-9 || r.ShaftDepth < shaftDepth - 1e-9)
			{
				problems?.Add("The shaft hole was made to fit inside the knob (at least 3 mm of wall and 1.5 mm on top).");
			}

			return r;
		}

		public List<Pt> Outline()
		{
			var radius = Diameter / 2;
			if (Grip == KnobGrip.Smooth)
			{
				return Profiles.Circle(radius, 96);
			}

			var samples = Flutes * 12;
			var points = new List<Pt>(samples);
			for (int i = 0; i < samples; i++)
			{
				var a = Profiles.Tau * i / samples;
				var r = radius - FluteDepth * (1 - Math.Cos(Flutes * a)) / 2;
				points.Add(new Pt(r * Math.Cos(a), r * Math.Sin(a)));
			}

			return points;
		}

		public List<Pt> ShaftOutline()
		{
			var r = HoleRadius;
			if (Shaft == KnobShaft.Round)
			{
				return Profiles.Circle(r, 40);
			}

			// a circle cut by a flat on the +X side
			var d = Math.Min(FlatDistance, r * 0.95);
			var start = Math.Acos(d / r);
			var points = new List<Pt>();
			const int steps = 36;
			for (int i = 0; i <= steps; i++)
			{
				var a = start + (Profiles.Tau - 2 * start) * i / steps;
				points.Add(new Pt(r * Math.Cos(a), r * Math.Sin(a)));
			}

			return points;
		}

		public ClubMesh Mesh() => BodyBuilder.Build(Contour.Prism(Outline(), 0, Height), Contour.Prism(ShaftOutline(), 0, ShaftDepth));
	}

	/// <summary>EBMC Edition: a cable clip: a C-shaped ring standing on a base, with an optional screw tab, printed as it mounts.</summary>
	public sealed class CableClipRules
	{
		public double CableDiameter { get; private set; }

		public double Opening { get; private set; }

		public double Width { get; private set; }

		public double Wall { get; private set; }

		public bool Tab { get; private set; }

		public double HoleDiameter { get; private set; }

		public double InnerRadius => CableDiameter / 2 + 0.2;

		public double OuterRadius => InnerRadius + Wall;

		public double TabLength => HoleDiameter + 6;

		/// <summary>
		/// The widest opening that still holds the cable: under nine tenths of the inside and at most eight tenths of the
		/// cable itself (the inside is 0.4 mm wider than the cable, which matters for thin cables).
		/// </summary>
		public static double MaxOpening(double cableDiameter)
		{
			var inner = cableDiameter + 0.4;
			return Math.Max(1, Math.Min(0.9 * inner, 0.8 * cableDiameter));
		}

		public static CableClipRules Compute(double cableDiameter, double opening, double width, double wall, bool tab, double holeDiameter, StarterProblems problems = null)
		{
			var r = new CableClipRules
			{
				CableDiameter = ShapeLimits.Clamp(cableDiameter, 2, 40),
				Width = ShapeLimits.Clamp(width, 4, 100),
				Wall = ShapeLimits.Clamp(wall, 1.2, 6),
				Tab = tab,
				HoleDiameter = ShapeLimits.Clamp(holeDiameter, 1.5, 8),
			};
			r.HoleDiameter = Math.Min(r.HoleDiameter, r.Width - 3);
			r.Opening = ShapeLimits.Clamp(opening, 1, MaxOpening(r.CableDiameter));
			if (r.Opening < opening - 1e-9)
			{
				problems?.Add("The opening must be narrower than the cable, or the clip will not hold it.");
			}

			return r;
		}

		/// <summary>The C profile (X across, Y up), centred over the base.</summary>
		public List<Pt> ClipProfile()
		{
			var ri = InnerRadius;
			var ro = OuterRadius;
			var cy = Wall + ri;
			var ao = Math.Asin(Math.Min(0.99, Opening / 2 / ro));
			var ai = Math.Asin(Math.Min(0.99, Opening / 2 / ri));
			var points = new List<Pt>();
			const int outerSteps = 48;
			for (int i = 0; i <= outerSteps; i++)
			{
				var a = Math.PI / 2 + ao + (Profiles.Tau - 2 * ao) * i / outerSteps;
				points.Add(new Pt(ro * Math.Cos(a), cy + ro * Math.Sin(a)));
			}

			const int innerSteps = 40;
			for (int i = 0; i <= innerSteps; i++)
			{
				var a = Math.PI / 2 - ai - (Profiles.Tau - 2 * ai) * i / innerSteps;
				points.Add(new Pt(ri * Math.Cos(a), cy + ri * Math.Sin(a)));
			}

			return points;
		}

		public ClubMesh Mesh()
		{
			var mesh = new ClubMesh();
			mesh.Add(BodyBuilder.Extrude(ClipProfile(), 0, Width).Transform(ClubMesh.XyToXz));

			// the base under the ring (the ring's bottom sits inside it) and the screw tab, flat on the bed
			var ro = OuterRadius;
			var right = Tab ? ro + TabLength : ro;
			var outline = new List<Pt> { new Pt(-ro, 0), new Pt(right, 0), new Pt(right, Width), new Pt(-ro, Width) };
			mesh.Add(Tab
				? BodyBuilder.Extrude(outline, 0, Wall, Profiles.Circle(HoleDiameter / 2, 32, ro + TabLength / 2, Width / 2))
				: BodyBuilder.Extrude(outline, 0, Wall));
			return mesh;
		}
	}

	/// <summary>EBMC Edition: a plant label lying flat: a blade for the text and a stake, optionally pointed.</summary>
	public sealed class PlantLabelRules
	{
		public const double Margin = 2.5;

		public double BladeLength { get; private set; }

		public double BladeWidth { get; private set; }

		public double StakeLength { get; private set; }

		public double StakeWidth { get; private set; }

		public bool Pointed { get; private set; }

		public double TipLength => Pointed ? Math.Min(StakeWidth * 1.2, StakeLength * 0.5) : 0;

		public double TotalLength => BladeLength + StakeLength;

		public static PlantLabelRules Compute(double bladeLength, double bladeWidth, double stakeLength, double stakeWidth, bool pointed)
		{
			var r = new PlantLabelRules
			{
				BladeLength = ShapeLimits.Clamp(bladeLength, 15, 250),
				BladeWidth = ShapeLimits.Clamp(bladeWidth, 8, 100),
				StakeLength = ShapeLimits.Clamp(stakeLength, 5, 300),
				Pointed = pointed,
			};
			r.StakeWidth = ShapeLimits.Clamp(stakeWidth, 3, r.BladeWidth);
			return r;
		}

		/// <summary>Capital height of the text on the blade for a text width per mm of capital height.</summary>
		public double CapHeight(double widthPerCap)
		{
			var cap = 0.5 * BladeWidth;
			var available = BladeLength - 2 * Margin;
			return widthPerCap > 0 ? Math.Min(cap, available / widthPerCap) : cap;
		}

		/// <summary>Centre of the text area (the blade), with the outline starting at X = 0.</summary>
		public (double x, double y) TextCenter => (BladeLength / 2, 0);

		public List<Pt> Outline()
		{
			var r = Math.Min(3, BladeWidth / 4);
			var segs = Profiles.ArcSegments(r);
			var hb = BladeWidth / 2;
			var hs = StakeWidth / 2;
			var end = TotalLength;
			var points = new List<Pt>();

			// the blade's back-left corner round to its front-left corner (rounded), then along the front edge
			for (int i = 0; i <= segs; i++)
			{
				var a = Math.PI + Math.PI / 2 * i / segs;
				points.Add(new Pt(r + r * Math.Cos(a), -hb + r + r * Math.Sin(a)));
			}

			if (hs < hb - 1e-6)
			{
				points.Add(new Pt(BladeLength, -hb));
				points.Add(new Pt(BladeLength, -hs));
			}

			if (Pointed)
			{
				points.Add(new Pt(end - TipLength, -hs));
				points.Add(new Pt(end, 0));
				points.Add(new Pt(end - TipLength, hs));
			}
			else
			{
				points.Add(new Pt(end, -hs));
				points.Add(new Pt(end, hs));
			}

			if (hs < hb - 1e-6)
			{
				points.Add(new Pt(BladeLength, hs));
				points.Add(new Pt(BladeLength, hb));
			}

			for (int i = 0; i <= segs; i++)
			{
				var a = Math.PI / 2 + Math.PI / 2 * i / segs;
				points.Add(new Pt(r + r * Math.Cos(a), hb - r + r * Math.Sin(a)));
			}

			return points;
		}
	}

	/// <summary>EBMC Edition: a QR tag: the code's dark modules raised on a plate (the code's quiet zone is the border).</summary>
	public sealed class QrTagRules
	{
		public const double RingWall = 2.5;

		public double Size { get; private set; }

		public int Modules { get; private set; }

		public double ModuleSize => Size / Modules;

		public bool RingHole { get; private set; }

		public double HoleDiameter { get; private set; }

		public double PlateWidth => Size;

		/// <summary>The plate: the code, then a ring wall, the hole and another ring wall above it.</summary>
		public double PlateHeight => RingHole ? Size + HoleDiameter + 2 * RingWall : Size;

		/// <summary>The largest ring hole for a tag size: a ring wall each side within the width, and at most 15 mm.</summary>
		public static double MaxHoleDiameter(double size) => Math.Max(2, Math.Min(15, size - 2 * RingWall));

		public static QrTagRules Compute(double size, int modules, bool ringHole, double holeDiameter, StarterProblems problems = null)
		{
			var r = new QrTagRules
			{
				Size = ShapeLimits.Clamp(size, 15, 200),
				Modules = Math.Max(1, modules),
				RingHole = ringHole,
			};
			r.HoleDiameter = ShapeLimits.Clamp(holeDiameter, 2, MaxHoleDiameter(r.Size));
			if (ringHole && r.HoleDiameter < holeDiameter - 1e-9)
			{
				problems?.Add("The ring hole was made smaller so it fits inside the tag.");
			}

			if (r.ModuleSize < 0.6)
			{
				problems?.Add("The code's squares are smaller than 0.6 mm and may not print cleanly. Make the tag bigger or the text shorter.");
			}

			return r;
		}

		/// <summary>Horizontal runs of dark modules: (row from the top, first column, last column).</summary>
		public static List<(int row, int start, int end)> Runs(IReadOnlyList<IReadOnlyList<bool>> modules)
		{
			var runs = new List<(int, int, int)>();
			for (int row = 0; row < modules.Count; row++)
			{
				var line = modules[row];
				int col = 0;
				while (col < line.Count)
				{
					if (!line[col])
					{
						col++;
						continue;
					}

					int start = col;
					while (col < line.Count && line[col])
					{
						col++;
					}

					runs.Add((row, start, col - 1));
				}
			}

			return runs;
		}

		public List<Pt> Outline() => Profiles.RoundedRectangle(PlateWidth, PlateHeight, 2, 3);

		/// <summary>Centre of the code within the plate (the ring hole sits above it).</summary>
		public double CodeCenterY => RingHole ? -(HoleDiameter + 2 * RingWall) / 2 : 0;

		/// <summary>Centre of the ring hole: a ring wall below the top edge (and a ring wall above the code).</summary>
		public double HoleCenterY => PlateHeight / 2 - RingWall - HoleDiameter / 2;

		public List<Pt> HoleOutline() => RingHole ? Profiles.Circle(HoleDiameter / 2, 32, 0, HoleCenterY) : null;

		/// <summary>The plate, with the ring hole when there is one.</summary>
		public ClubMesh Plate(double thickness)
		{
			var hole = HoleOutline();
			return hole == null ? BodyBuilder.Extrude(Outline(), 0, thickness) : BodyBuilder.Extrude(Outline(), 0, thickness, hole);
		}

		/// <summary>
		/// One rectangle per run of dark modules, in plate coordinates. Neighbouring rectangles share edges, so the app
		/// joins them (a 2D union) before raising them into one body.
		/// </summary>
		public List<List<Pt>> ModuleRectangles(IReadOnlyList<IReadOnlyList<bool>> modules)
		{
			var m = ModuleSize;
			var left = -Size / 2;
			var top = CodeCenterY + Size / 2;
			return Runs(modules).Select(r => new List<Pt>
			{
				new Pt(left + r.start * m, top - (r.row + 1) * m),
				new Pt(left + (r.end + 1) * m, top - (r.row + 1) * m),
				new Pt(left + (r.end + 1) * m, top - r.row * m),
				new Pt(left + r.start * m, top - r.row * m),
			}).ToList();
		}
	}
}
