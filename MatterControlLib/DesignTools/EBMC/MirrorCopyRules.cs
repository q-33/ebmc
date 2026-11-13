using System;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>EBMC Edition: the world axis Mirror Copy reflects across.</summary>
	public enum MirrorCopyAxis
	{
		X,
		Y,
		Z
	}

	/// <summary>
	/// EBMC Edition: where Mirror Copy puts the mirrored copy, with no app dependencies so Tests/EBMC.Tests can check it.
	/// The copy is the selection reflected in the plane of its own far face on the chosen axis: it touches the original
	/// along that face, on the + side (right of it for X, behind it for Y, on top of it for Z, so it never goes under the
	/// bed). The app moves the copy by <see cref="Offset"/> and then reflects it about its own bounds centre, which leaves
	/// its bounds where they are, so both together are the reflection in that face.
	/// </summary>
	public static class MirrorCopyRules
	{
		/// <summary>The default axis before the user has picked one.</summary>
		public const MirrorCopyAxis DefaultAxis = MirrorCopyAxis.X;

		/// <summary>
		/// How far the copy moves along the axis before it is reflected: the selection's size on that axis, so the copy
		/// occupies [max, max + size] next to the original's [min, max]. 0 for a flat or broken box.
		/// </summary>
		public static double Offset(double min, double max)
		{
			var size = max - min;
			if (double.IsNaN(size) || double.IsInfinity(size) || size <= 0)
			{
				return 0;
			}

			return size;
		}

		/// <summary>The copy's range on the axis: (min, max) of the reflection of [min, max] in the plane at max.</summary>
		public static (double Min, double Max) CopyRange(double min, double max)
		{
			// reflection in the plane x = max: x -> 2 * max - x
			return (2 * max - max, 2 * max - min);
		}

		/// <summary>The axis stored in user settings ("X", "Y" or "Z"); anything else is the default.</summary>
		public static MirrorCopyAxis Parse(string stored)
		{
			if (!string.IsNullOrEmpty(stored)
				&& Enum.TryParse(stored.Trim(), true, out MirrorCopyAxis axis)
				&& Enum.IsDefined(typeof(MirrorCopyAxis), axis))
			{
				return axis;
			}

			return DefaultAxis;
		}

		/// <summary>0, 1 or 2 for X, Y or Z.</summary>
		public static int Index(MirrorCopyAxis axis) => (int)axis;
	}
}
