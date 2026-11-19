using System.Collections.Generic;
using MatterHackers.MatterControl.DesignTools.ClubShapes;

namespace MatterHackers.MatterControl.DesignTools.Laser
{
	// EBMC Edition: the 2D outline model for "Export for laser". Everything in this folder is pure geometry (System,
	// ClipperLib and ClubShapes.Pt only, no agg-sharp and no GUI) so Tests/EBMC.Tests can check it. It writes files only;
	// nothing here talks to a machine, and it has nothing to do with slicing or printing.

	/// <summary>How the outline is found.</summary>
	public enum LaserOutlineMethod
	{
		/// <summary>The outline seen from above: every triangle projected onto the bed and joined.</summary>
		Footprint,

		/// <summary>A cut through the part at a height above its bottom.</summary>
		Section,
	}

	/// <summary>Whether an input adds material or takes it away.</summary>
	public enum LaserRole
	{
		Solid,
		Hole,
	}

	/// <summary>
	/// One mesh to outline: positions (x, y, z per vertex, in the part's own mm), triangles (three vertex indices each,
	/// counter-clockwise seen from outside) and where it is on the bed.
	/// </summary>
	public sealed class LaserMeshInput
	{
		public string Name;

		public double[] Positions;

		public int[] Triangles;

		/// <summary>Row-major 3x4 affine (p' = A p + t), the layout of SolidMesher.ToKernelMatrix; null is identity.</summary>
		public double[] Affine;

		public LaserRole Role;
	}

	/// <summary>One flat 2D path to outline (a drawing, text in 2D, an image trace or the path an extrusion is made from).</summary>
	public sealed class LaserPathInput
	{
		public string Name;

		/// <summary>Closed loops in the item's own mm (already flattened, first point not repeated).</summary>
		public List<List<Pt>> Loops;

		/// <summary>Row-major 3x4 affine (p' = A p + t), the layout of SolidMesher.ToKernelMatrix; null is identity.</summary>
		public double[] Affine;

		public LaserRole Role;

		/// <summary>Bottom and top of the item on the bed (mm), for the hole rule and Slice at height.</summary>
		public double MinZ;

		public double MaxZ;

		/// <summary>True for a drawing (its mesh only shows the path); false when the path is the profile of a solid part.</summary>
		public bool IsDrawing;
	}

	/// <summary>A closed loop in mm, Y up, first point not repeated.</summary>
	public sealed class LaserLoop
	{
		public IReadOnlyList<Pt> Points;
	}

	/// <summary>One island: its outer loop (counter-clockwise) and its holes (clockwise).</summary>
	public sealed class LaserShape
	{
		public LaserLoop Outer;

		public IReadOnlyList<LaserLoop> Holes;
	}

	/// <summary>A named group of shapes. Stage A writes one "Cut" layer; later stages may add Score and Engrave.</summary>
	public sealed class LaserLayer
	{
		public string Name;

		public IReadOnlyList<LaserShape> Shapes;
	}

	/// <summary>The finished outline in bed coordinates (mm, Y up) with its bounds.</summary>
	public sealed class LaserOutline
	{
		public const string CutLayer = "Cut";

		public IReadOnlyList<LaserLayer> Layers;

		public double MinX;

		public double MinY;

		public double MaxX;

		public double MaxY;

		public LaserOutlineReport Report;

		public bool IsEmpty;

		public double Width => IsEmpty ? 0 : MaxX - MinX;

		public double Height => IsEmpty ? 0 : MaxY - MinY;
	}

	/// <summary>What the builder did, for the messages after an export.</summary>
	public sealed class LaserOutlineReport
	{
		public int Shapes;

		public int Holes;

		public int OpenLoopsClosed;

		public int OpenLoopsDropped;

		public int TinyLoopsDropped;

		/// <summary>Names of parts set to Hole that were left out because they do not go all the way through.</summary>
		public List<string> HolesNotThrough = new List<string>();

		/// <summary>How many (hole, solid part) pairs were left out seen from above; only the first few are worded.</summary>
		public int HoleWarnings;

		/// <summary>Names of parts whose slice lost lines because the surface has gaps wider than LaserOutlineBuilder.SectionGapMm.</summary>
		public List<string> PartsWithGaps = new List<string>();

		public List<string> Warnings = new List<string>();
	}

	public sealed class LaserOutlineOptions
	{
		public LaserOutlineMethod Method = LaserOutlineMethod.Footprint;

		/// <summary>Slice at height: mm above the bottom of everything being exported.</summary>
		public double SliceHeightAboveBottom = 0.2;

		/// <summary>Loops smaller than this (mm²) are dropped and counted.</summary>
		public double MinLoopAreaMm2 = 1e-4;

		/// <summary>Cracks narrower than twice this (mm) between joined triangles are sealed.</summary>
		public double CloseGapMm = 0.002;

		/// <summary>Triangles per union while finding the outline seen from above.</summary>
		public int ChunkSize = 2000;
	}
}
