using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClipperLib;
using MatterHackers.Agg.Font;
using MatterHackers.Agg.Transform;
using MatterHackers.Agg.VertexSource;
using MatterHackers.DataConverters2D;
using MatterHackers.PolygonMesh;
using MatterHackers.PolygonMesh.Processors;
using MatterHackers.VectorMath;
using Polygon = System.Collections.Generic.List<ClipperLib.IntPoint>;
using Polygons = System.Collections.Generic.List<System.Collections.Generic.List<ClipperLib.IntPoint>>;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	/// <summary>
	/// EBMC Edition: text for the starters, made the way TextObject3D makes it (the named font through
	/// ApplicationController.GetTypeFace, TypeFacePrinter per letter, sized by capital height with TextSize, extruded with
	/// VertexSourceToMesh.Extrude), as one set of 2D outlines in millimetres so a starter can raise them or cut them into a
	/// plate. Outlines use Clipper's integer points at 1000 per mm.
	/// </summary>
	public static class ClubText
	{
		public const double Scale = 1000;

		/// <summary>The starters' default font: a rounded display face, not the interface font.</summary>
		public const string DefaultFont = "Fredoka";

		private const double MmPerPoint = 25.4 / 72;

		/// <summary>Finds a font by name; the app's own font lookup (StaticData/Fonts and the user's Fonts folder). Replaceable for headless checks.</summary>
		public static Func<string, TypeFace> FontLoader { get; set; } = name => ApplicationController.Instance.GetTypeFace(name);

		/// <summary>
		/// The face to use for <paramref name="font"/>, looked up ONCE per build (in PrepareBuild) and then passed to both
		/// the measuring and the drawing: the app's lookup can return null for a name it has not loaded yet and a different
		/// face on the next call, so two lookups could measure with one font and draw with another. An empty, unknown or
		/// unloadable name gives the starters' default font explicitly.
		/// </summary>
		public static TypeFace Resolve(string font)
		{
			return ClubTextRules.ResolveFont(font, DefaultFont, FontLoader);
		}

		/// <summary>The outlines of one line of text with capital letters <paramref name="capHeight"/> mm tall, baseline at Y = 0, starting at X = 0.</summary>
		public static Polygons Line(string text, string font, double capHeight, CancellationToken cancellationToken = default)
		{
			return Line(text, Resolve(font), capHeight, cancellationToken);
		}

		/// <summary>As <see cref="Line(string, string, double, CancellationToken)"/> with an already resolved face (see <see cref="Resolve"/>).</summary>
		public static Polygons Line(string text, TypeFace typeFace, double capHeight, CancellationToken cancellationToken = default)
		{
			var result = new Polygons();
			text = ClubTextRules.OneLine(text);
			if (string.IsNullOrWhiteSpace(text))
			{
				return result;
			}

			if (typeFace == null)
			{
				return result;
			}

			var capHeightPerEm = TextSize.CapHeightPerEm(typeFace.Cap_height, typeFace.UnitsPerEm);
			var pointSize = TextSize.PointSizeForCapHeight(capHeight, capHeightPerEm);
			var style = new StyledTypeFace(typeFace, pointSize)
			{
				FlattenCurves = false
			};

			var all = new Polygons();
			double x = 0;
			foreach (var letter in text)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var printer = new TypeFacePrinter(letter.ToString(), style)
				{
					ResolutionScale = 10,
				};

				if (!char.IsWhiteSpace(letter))
				{
					var scaled = new VertexSourceApplyTransform(printer, Affine.NewScaling(MmPerPoint) * Affine.NewTranslation(x, 0));
					var polygons = scaled.CreatePolygons(Scale).GetCorrectedWinding();
					all.AddRange(polygons);
				}

				x += printer.GetSize(letter.ToString()).X * MmPerPoint;
			}

			// letters of joined or script fonts overlap; union them so the overlaps stay solid
			var clipper = new Clipper();
			clipper.AddPaths(all, PolyType.ptSubject, true);
			clipper.Execute(ClipType.ctUnion, result, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
			return result;
		}

		/// <summary>The width of a line per millimetre of capital height (0 for no text).</summary>
		public static double WidthPerCap(string text, string font) => WidthPerCap(text, Resolve(font));

		/// <summary>As <see cref="WidthPerCap(string, string)"/> with an already resolved face (see <see cref="Resolve"/>).</summary>
		public static double WidthPerCap(string text, TypeFace typeFace)
		{
			const double probe = 10;
			var line = Line(text, typeFace, probe);
			if (line.Count == 0)
			{
				return 0;
			}

			var bounds = Clipper.GetBounds(line);
			return (bounds.right - bounds.left) / Scale / probe;
		}

		/// <summary>Moves outlines so their bounding box is centred on (<paramref name="x"/>, <paramref name="y"/>) mm.</summary>
		public static Polygons CenterOn(Polygons polygons, double x, double y)
		{
			if (polygons.Count == 0)
			{
				return polygons;
			}

			var b = Clipper.GetBounds(polygons);
			var dx = (long)Math.Round(x * Scale - (b.left + b.right) / 2.0);
			var dy = (long)Math.Round(y * Scale - (b.top + b.bottom) / 2.0);
			return polygons.Select(p => p.Select(q => new IntPoint(q.X + dx, q.Y + dy)).ToList()).ToList();
		}

		public static Polygon ToClipper(IEnumerable<Pt> outline) => outline.Select(p => new IntPoint(Math.Round(p.X * Scale), Math.Round(p.Y * Scale))).ToList();

		/// <summary>Outline minus the given outlines (e.g. a plate with text and a ring hole cut out).</summary>
		public static Polygons Subtract(Polygons outline, Polygons cut)
		{
			var clipper = new Clipper();
			clipper.AddPaths(outline, PolyType.ptSubject, true);
			clipper.AddPaths(cut, PolyType.ptClip, true);
			var result = new Polygons();
			clipper.Execute(ClipType.ctDifference, result, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
			return result;
		}

		/// <summary>
		/// The union of outlines (non-zero fill), grown by <paramref name="grow"/> mm so shapes that only touch at a corner
		/// (QR squares on a diagonal) overlap instead of meeting at a single point, which would leave a non-manifold edge.
		/// </summary>
		public static Polygons Union(Polygons polygons, double grow = 0)
		{
			var clipper = new Clipper();
			clipper.AddPaths(polygons, PolyType.ptSubject, true);
			var result = new Polygons();
			clipper.Execute(ClipType.ctUnion, result, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
			if (grow > 0)
			{
				var offset = new ClipperOffset();
				offset.AddPaths(result, JoinType.jtMiter, EndType.etClosedPolygon);
				var grown = new Polygons();
				offset.Execute(ref grown, grow * Scale);
				result = grown;
			}

			return result;
		}

		/// <summary>Extrudes outlines from <paramref name="z0"/> to <paramref name="z1"/> (null for nothing).</summary>
		public static Mesh Extrude(Polygons polygons, double z0, double z1)
		{
			if (polygons == null || polygons.Count == 0 || z1 <= z0)
			{
				return null;
			}

			var mesh = VertexSourceToMesh.Extrude(polygons.CreateVertexStorage(Scale), z1 - z0);
			mesh.Translate(new Vector3(0, 0, z0));
			return mesh;
		}
	}
}
