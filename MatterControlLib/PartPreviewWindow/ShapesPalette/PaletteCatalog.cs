using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// Which kind of tab a shapes palette sits on.
	/// </summary>
	public enum PaletteTab
	{
		Design,
		Printer
	}

	/// <summary>
	/// Where the palette finds the thing a member stands for.
	/// </summary>
	public enum PaletteSource
	{
		/// <summary>A GeneratorItem registered in the library root by Primitives3DContainer, found by its (English) name.</summary>
		Primitives,

		/// <summary>An item of CalibrationPartsContainer, found by its name (a generator name or a sample part's file name).</summary>
		CalibrationParts,

		/// <summary>A SceneOperation in the SceneOperations registry, found by its Id.</summary>
		SceneOperation,

		/// <summary>A palette command (see PaletteCatalog.AddModelCommand and SupportsCommand).</summary>
		Command
	}

	/// <summary>
	/// One thing a palette button or flyout row adds or runs.
	/// </summary>
	public sealed class PaletteMember
	{
		public PaletteMember(PaletteSource source, string name, string glyph, string title = null)
		{
			Source = source;
			Name = name;
			Glyph = glyph;
			Title = title ?? name;
		}

		public PaletteSource Source { get; }

		/// <summary>Registered name (generator item name, calibration item name, operation Id or command name).</summary>
		public string Name { get; }

		/// <summary>English label shown to the user (localized by the UI).</summary>
		public string Title { get; }

		/// <summary>File name (without .png) under StaticData/Icons/Shapes.</summary>
		public string Glyph { get; }

		/// <summary>Class the generator creates; set for shapes whose size the size chips change.</summary>
		public string ObjectType { get; private set; }

		/// <summary>The shape's default dimensions in mm, in the order SizeFormat uses them.</summary>
		public double[] Dimensions { get; private set; } = new double[0];

		/// <summary>Composite format for the default size, e.g. "Ø{0} × {1}"; null when there is no size to show.</summary>
		public string SizeFormat { get; private set; }

		/// <summary>Plain size text for members whose size is not a set of dimensions (e.g. text).</summary>
		public string SizeNote { get; private set; }

		/// <summary>DoubleOrExpression properties of ObjectType that the size chips scale; empty when chips do not apply.</summary>
		public string[] ScaledProperties { get; private set; } = new string[0];

		/// <summary>True when the item only exists in some builds (e.g. DEBUG-only); the palette leaves it out when missing.</summary>
		public bool Optional { get; private set; }

		/// <summary>True when the size chips apply to this member.</summary>
		public bool Scalable => ScaledProperties.Length > 0;

		internal PaletteMember Sized(string objectType, string format, double[] dimensions, params string[] scaledProperties)
		{
			ObjectType = objectType;
			SizeFormat = format;
			Dimensions = dimensions;
			ScaledProperties = scaledProperties;
			return this;
		}

		internal PaletteMember Note(string sizeNote)
		{
			SizeNote = sizeNote;
			return this;
		}

		internal PaletteMember AsOptional()
		{
			Optional = true;
			return this;
		}

		/// <summary>
		/// The size shown for this member when the size chips give <paramref name="scale"/> (1 = the default size).
		/// </summary>
		public string SizeText(double scale = 1)
		{
			if (SizeFormat == null)
			{
				return SizeNote ?? "";
			}

			var values = Dimensions.Select(d => (object)(d * scale).ToString("0.#", CultureInfo.InvariantCulture)).ToArray();
			return string.Format(CultureInfo.InvariantCulture, SizeFormat, values);
		}
	}

	/// <summary>
	/// One palette button: a single member, or a family whose button adds the current member and whose flyout lists them all.
	/// </summary>
	public sealed class PaletteFamily
	{
		public PaletteFamily(string id, string label, params PaletteMember[] members)
		{
			Id = id;
			Label = label;
			Members = members;
		}

		public string Id { get; }

		/// <summary>English name of the family (button label and flyout title).</summary>
		public string Label { get; }

		public IReadOnlyList<PaletteMember> Members { get; }

		/// <summary>True when shapes of this family can be inserted as holes (Solid/Hole switch).</summary>
		public bool CanBeHole { get; private set; }

		/// <summary>True when a click opens the flyout instead of running a member: support generation needs its settings, and
		/// calibration parts are chosen deliberately rather than repeated.</summary>
		public bool ClickOpensFlyout { get; private set; }

		/// <summary>Glyph of the family button when it differs from the current member's (e.g. Calibration).</summary>
		public string FamilyGlyph { get; private set; }

		/// <summary>True when the button carries a corner mark and opens a flyout.</summary>
		public bool HasFlyout => Members.Count > 1 || ClickOpensFlyout;

		/// <summary>True when the flyout shows the 10 / 20 / 40 mm size chips.</summary>
		public bool HasSizeChips => Members.Any(m => m.Scalable);

		internal PaletteFamily Holes()
		{
			CanBeHole = true;
			return this;
		}

		internal PaletteFamily FlyoutOnClick()
		{
			ClickOpensFlyout = true;
			return this;
		}

		internal PaletteFamily WithGlyph(string glyph)
		{
			FamilyGlyph = glyph;
			return this;
		}
	}

	/// <summary>
	/// A run of buttons, preceded by a divider (or, with labels on, a section label) when it has a label.
	/// </summary>
	public sealed class PaletteSection
	{
		public PaletteSection(string label, params PaletteFamily[] families)
		{
			Label = label;
			Families = families;
		}

		/// <summary>English section label; null for the first section, which has no divider.</summary>
		public string Label { get; }

		public IReadOnlyList<PaletteFamily> Families { get; }
	}

	/// <summary>
	/// EBMC Edition: what the shapes palette (direction A of tasks/design/shapes-rail-mockups.html) shows on design and
	/// printer tabs. Pure data with no UI or app dependencies, so Tests/EBMC.Tests can check it against the code it names.
	/// </summary>
	public static class PaletteCatalog
	{
		/// <summary>Command: open files with the system dialog and add them to the bed (the toolbar's Add System File to Bed).</summary>
		public const string AddModelCommand = "AddModel";

		/// <summary>Command: the toolbar's Generate Support panel.</summary>
		public const string SupportsCommand = "Supports";

		/// <summary>The size chips, in mm. A chip scales the member's default dimensions by chip / BaseSize.</summary>
		public static readonly int[] SizeChips = { 10, 20, 40 };

		/// <summary>The size the primitives are made at by default, in mm (the "20" chip).</summary>
		public const int BaseSize = 20;

		public static double ScaleFor(int chip) => chip / (double)BaseSize;

		private static PaletteMember Primitive(string name, string glyph) => new PaletteMember(PaletteSource.Primitives, name, glyph);

		private static PaletteMember Operation(string id, string glyph, string title) => new PaletteMember(PaletteSource.SceneOperation, id, glyph, title);

		private static PaletteMember Calibration(string name, string glyph, string title = null) => new PaletteMember(PaletteSource.CalibrationParts, name, glyph, title);

		private static readonly double[] D20 = { 20, 20, 20 };

		private static PaletteMember Cube() => Primitive("Cube", "cube").Sized("CubeObject3D", "{0} × {1} × {2}", D20, "Width", "Depth", "Height");

		private static PaletteMember Cylinder() => Primitive("Cylinder", "cylinder").Sized("CylinderObject3D", "Ø{0} × {1}", new double[] { 20, 20 }, "Diameter", "DiameterTop", "Height");

		private static PaletteMember Text() => Primitive("Text", "text").Note("24 pt × 3 mm");

		private static PaletteMember Measure() => Primitive("Measure Tool", "measure");

		/// <summary>The design tab: Solid/Hole switch, shape families, then the modelling tools.</summary>
		public static IReadOnlyList<PaletteSection> Design { get; } = new[]
		{
			new PaletteSection(
				"Shapes",
				new PaletteFamily(
					"box",
					"Box shapes",
					Cube(),
					Primitive("Pyramid", "pyramid").Sized("PyramidObject3D", "{0} × {1} × {2}", D20, "Width", "Depth", "Height"),
					Primitive("Wedge", "wedge").Sized("WedgeObject3D_2", "{0} × {1} × {2}", D20, "Width", "Depth", "Height"),
					Primitive("Half Wedge", "half-wedge").Sized("HalfWedgeObject3D", "{0} × {1} × {2}", new double[] { 20, 20, 10 }, "Width", "Depth", "Height")).Holes(),
				new PaletteFamily(
					"round",
					"Round shapes",
					Cylinder(),
					Primitive("Cone", "cone").Sized("ConeObject3D", "Ø{0} × {1}", new double[] { 20, 20 }, "Diameter", "Height"),
					Primitive("Half Cylinder", "half-cylinder").Sized("HalfCylinderObject3D", "{0} × {1} × {2}", new double[] { 20, 20, 10 }, "Width", "Depth"),
					Primitive("Sphere", "sphere").Sized("SphereObject3D", "Ø{0}", new double[] { 20 }, "Diameter"),
					Primitive("Half Sphere", "half-sphere").Sized("HalfSphereObject3D", "Ø{0} × {1}", new double[] { 20, 10 }, "Diameter"),
					Primitive("Torus", "torus").Sized("TorusObject3D", "Ø{0} / Ø{1}", new double[] { 20, 10 }, "OuterDiameter", "InnerDiameter"),
					Primitive("Ring", "ring").Sized("RingObject3D", "Ø{0} / Ø{1} × {2}", new double[] { 20, 15, 5 }, "OuterDiameter", "InnerDiameter", "Height")).Holes(),
				new PaletteFamily("text", "Text", Text()).Holes(),
				new PaletteFamily(
					"image",
					"Image & QR",
					Primitive("Image Converter", "image").Note("club badge"),
					Primitive("QR Code", "qr-code").Note("club website")).Holes(),
				new PaletteFamily(
					"marks",
					"Measure & notes",
					Measure(),
					Primitive("Description", "description"),
					Primitive("Variable Sheet", "variable-sheet"))),
			new PaletteSection(
				"Tools",
				new PaletteFamily(
					"combine",
					"Combine",
					Operation("Combine", "combine", "Combine"),
					Operation("Subtract", "subtract", "Subtract"),
					Operation("Intersect", "intersect", "Intersect"),
					Operation("Subtract & Replace", "subtract-replace", "Subtract & Replace")),
				new PaletteFamily("align", "Align", Operation("Align", "align", "Align")),
				new PaletteFamily("mirror", "Mirror", Operation("Mirror", "mirror", "Mirror")),
				new PaletteFamily(
					"array",
					"Array",
					Operation("Linear Array", "array-linear", "Linear Array"),
					Operation("Radial Array", "array-radial", "Radial Array"),
					Operation("Advanced Array", "array-advanced", "Advanced Array")),
				new PaletteFamily("planecut", "Plane Cut", Operation("Plane Cut", "plane-cut", "Plane Cut"))),
		};

		/// <summary>The printer tab: add a model, quick test shapes and calibration parts, then the print preparation tools.</summary>
		/// <remarks>
		/// No temperature towers: they are made of Set Temperature parts that write hotend temperatures into the print,
		/// past the maintainer lock, so a member could print the wrong temperatures for the loaded material.
		/// </remarks>
		public static IReadOnlyList<PaletteSection> Printer { get; } = new[]
		{
			new PaletteSection(
				null,
				new PaletteFamily("add", "Add model", new PaletteMember(PaletteSource.Command, AddModelCommand, "add-model", "Add model"))),
			new PaletteSection(
				"Test shapes",
				new PaletteFamily("pcube", "Cube", Cube()),
				new PaletteFamily("pcylinder", "Cylinder", Cylinder()),
				new PaletteFamily("ptext", "Text", Text()),
				new PaletteFamily(
					"calibration",
					"Calibration",
					Calibration("XY Calibration", "xy-calibration").Note("dual extruder").AsOptional(),
					Calibration("Calibration - Box.stl", "cube", "Calibration box"),
					Calibration("Calibration - Circle.stl", "cylinder", "Calibration circle"),
					Calibration("Retraction Cubes.mcx", "retraction-cubes", "Retraction cubes")).WithGlyph("xy-calibration").FlyoutOnClick()),
			new PaletteSection(
				"Prepare",
				new PaletteFamily("arrange", "Arrange All", Operation("ArrangeAllParts", "arrange", "Arrange All Parts")),
				new PaletteFamily("layflat", "Lay Flat", Operation("Lay Flat", "lay-flat", "Lay Flat")),
				new PaletteFamily("supports", "Supports", new PaletteMember(PaletteSource.Command, SupportsCommand, "supports", "Generate Support")).FlyoutOnClick(),
				new PaletteFamily("pmeasure", "Measure Tool", Measure())),
		};

		public static IReadOnlyList<PaletteSection> For(PaletteTab tab) => tab == PaletteTab.Printer ? Printer : Design;

		/// <summary>True when the tab shows the Solid/Hole switch (design tabs only).</summary>
		public static bool HasHoleSwitch(PaletteTab tab) => tab == PaletteTab.Design;

		public static IEnumerable<PaletteFamily> Families(PaletteTab tab) => For(tab).SelectMany(s => s.Families);

		/// <summary>Every glyph the palette loads for a tab, including the switch and labels toggle.</summary>
		public static IEnumerable<string> Glyphs(PaletteTab tab)
		{
			var glyphs = Families(tab).SelectMany(f => f.Members.Select(m => m.Glyph).Concat(f.FamilyGlyph != null ? new[] { f.FamilyGlyph } : new string[0]))
				.Concat(new[] { LabelsShowGlyph, LabelsHideGlyph });

			if (HasHoleSwitch(tab))
			{
				glyphs = glyphs.Concat(new[] { SolidSwitchGlyph, HoleSwitchGlyph });
			}

			return glyphs.Distinct();
		}

		public const string LabelsShowGlyph = "labels-show";

		public const string LabelsHideGlyph = "labels-hide";

		/// <summary>Glyphs of the Solid/Hole switch buttons.</summary>
		public const string SolidSwitchGlyph = "cube";

		public const string HoleSwitchGlyph = "hole";

		/// <summary>Logical size of every palette glyph (80 px masters).</summary>
		public const int GlyphSize = 20;

		/// <summary>The member a family button starts with before the user picks another one.</summary>
		public static PaletteMember DefaultMember(PaletteFamily family) => family.Members[0];
	}
}
