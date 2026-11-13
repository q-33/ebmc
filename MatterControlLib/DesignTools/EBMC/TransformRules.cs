using System;
using System.Linq;
using System.Globalization;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>
	/// EBMC Edition: a world axis-aligned bounding box in mm (the selection's bounds as the Transform section shows them).
	/// </summary>
	public readonly struct TransformBounds
	{
		public TransformBounds(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
		{
			MinX = minX;
			MinY = minY;
			MinZ = minZ;
			MaxX = maxX;
			MaxY = maxY;
			MaxZ = maxZ;
		}

		public double MinX { get; }

		public double MinY { get; }

		public double MinZ { get; }

		public double MaxX { get; }

		public double MaxY { get; }

		public double MaxZ { get; }

		public double CenterX => (MinX + MaxX) / 2;

		public double CenterY => (MinY + MaxY) / 2;

		public double CenterZ => (MinZ + MaxZ) / 2;

		public double Bottom => MinZ;

		public double SizeX => MaxX - MinX;

		public double SizeY => MaxY - MinY;

		public double SizeZ => MaxZ - MinZ;

		/// <summary>False for an empty or broken box (nothing selected that has a mesh yet).</summary>
		public bool IsValid => IsFinite(MinX) && IsFinite(MaxX) && IsFinite(MinY) && IsFinite(MaxY) && IsFinite(MinZ) && IsFinite(MaxZ)
			&& SizeX >= 0 && SizeY >= 0 && SizeZ >= 0;

		/// <summary>The value the Transform section shows in a row: 0..2 = Centre X, Centre Y, Bottom Z; 3..5 = Size X, Y, Z.</summary>
		public double Field(TransformField field)
		{
			switch (field)
			{
				case TransformField.CenterX: return CenterX;
				case TransformField.CenterY: return CenterY;
				case TransformField.BottomZ: return Bottom;
				case TransformField.SizeX: return SizeX;
				case TransformField.SizeY: return SizeY;
				case TransformField.SizeZ: return SizeZ;
				default: return 0;
			}
		}

		private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}

	/// <summary>EBMC Edition: the typed fields of the Transform section.</summary>
	public enum TransformField
	{
		CenterX,
		CenterY,
		BottomZ,
		SizeX,
		SizeY,
		SizeZ,
		RotateX,
		RotateY,
		RotateZ
	}

	/// <summary>
	/// EBMC Edition: one edit of the Transform section as a world-space change, applied to a point p as
	/// <c>rotate(p - Pivot) scaled, + Pivot + Move</c>: first scale about the pivot, then rotate about it, then move. The
	/// app turns it into one matrix (T(-pivot) · S · R · T(pivot) · T(move), row-vector order as agg-sharp's Matrix4X4)
	/// and applies it through one undoable transform command.
	/// </summary>
	public sealed class TransformStep
	{
		public double PivotX { get; set; }

		public double PivotY { get; set; }

		public double PivotZ { get; set; }

		public double ScaleX { get; set; } = 1;

		public double ScaleY { get; set; } = 1;

		public double ScaleZ { get; set; } = 1;

		/// <summary>0 = X, 1 = Y, 2 = Z; -1 for no rotation.</summary>
		public int RotateAxis { get; set; } = -1;

		public double RotateDegrees { get; set; }

		public double MoveX { get; set; }

		public double MoveY { get; set; }

		public double MoveZ { get; set; }

		/// <summary>True when the step changes nothing.</summary>
		public bool IsIdentity => ScaleX == 1 && ScaleY == 1 && ScaleZ == 1
			&& (RotateAxis < 0 || RotateDegrees == 0)
			&& MoveX == 0 && MoveY == 0 && MoveZ == 0;

		/// <summary>Where the step takes a world point (used by the tests and to check the app's matrix).</summary>
		public (double X, double Y, double Z) Apply(double x, double y, double z)
		{
			// scale about the pivot
			x = (x - PivotX) * ScaleX;
			y = (y - PivotY) * ScaleY;
			z = (z - PivotZ) * ScaleZ;

			// rotate about the pivot (right-handed, counter-clockwise looking down the axis)
			if (RotateAxis >= 0 && RotateDegrees != 0)
			{
				var radians = RotateDegrees * Math.PI / 180;
				var cos = Math.Cos(radians);
				var sin = Math.Sin(radians);
				double a, b;
				switch (RotateAxis)
				{
					case 0:
						a = y * cos - z * sin;
						b = y * sin + z * cos;
						y = a;
						z = b;
						break;

					case 1:
						a = z * cos - x * sin;
						b = z * sin + x * cos;
						z = a;
						x = b;
						break;

					default:
						a = x * cos - y * sin;
						b = x * sin + y * cos;
						x = a;
						y = b;
						break;
				}
			}

			return (x + PivotX + MoveX, y + PivotY + MoveY, z + PivotZ + MoveZ);
		}
	}

	/// <summary>
	/// EBMC Edition: the maths of the Transform section (typed position, size and rotation for the selection), with no
	/// app dependencies so Tests/EBMC.Tests can check it. All values are world mm and degrees.
	/// </summary>
	public static class TransformRules
	{
		/// <summary>Smallest size the section accepts, in mm.</summary>
		public const double MinimumSize = 0.001;

		/// <summary>
		/// A typed value this close to the current one changes nothing (half the fields' 0.01 display step), so
		/// re-entering the shown value, or a field losing focus after Enter, never adds a transform or an undo step.
		/// </summary>
		public const double NoChangeTolerance = 0.005;

		/// <summary>Largest position (either side of 0) and size the section accepts, in mm.</summary>
		public const double MaximumMillimetres = 10000;

		/// <summary>Why the section and Mirror Copy do nothing for a part inside a group (their maths is in scene space).</summary>
		public const string NestedSelectionMessage = "Select the whole group to move or copy it.";

		/// <summary>Shown when Keep proportions is off but the selection is turned, so sizes change together.</summary>
		public const string RotatedScalesEvenlyMessage = "This part is rotated, so it scales evenly.";

		/// <summary>
		/// True when <paramref name="value"/> typed in <paramref name="field"/> would not change the selection: a position
		/// or size within <see cref="NoChangeTolerance"/> of the current one, or a turn of 0 (or a whole turn).
		/// </summary>
		public static bool IsNoChange(TransformBounds bounds, TransformField field, double value)
		{
			if (!IsFinite(value))
			{
				return true;
			}

			if (field >= TransformField.RotateX)
			{
				return Math.Abs(value % 360) < 1e-9;
			}

			return bounds.IsValid && Math.Abs(bounds.Field(field) - value) <= NoChangeTolerance;
		}

		/// <summary>
		/// Whether a typed value is in range: a position within ±<see cref="MaximumMillimetres"/>, a size up to it, a
		/// turn of any angle. Out of range, the field reverts.
		/// </summary>
		public static bool IsWithinLimits(TransformField field, double value)
		{
			if (!IsFinite(value))
			{
				return false;
			}

			if (field >= TransformField.RotateX)
			{
				return true;
			}

			if (field >= TransformField.SizeX)
			{
				return value <= MaximumMillimetres;
			}

			return Math.Abs(value) <= MaximumMillimetres;
		}

		/// <summary>
		/// Whether a 3 x 3 linear part (row major) only scales and swaps or flips axes, so scaling it along a world axis
		/// keeps right angles. A turned part (any rotation that is not a multiple of 90°) is not axis aligned.
		/// </summary>
		public static bool IsAxisAligned(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22, double tolerance = 1e-6)
		{
			var rows = new[] { new[] { m00, m01, m02 }, new[] { m10, m11, m12 }, new[] { m20, m21, m22 } };
			foreach (var row in rows)
			{
				var length = Math.Sqrt(row[0] * row[0] + row[1] * row[1] + row[2] * row[2]);
				if (!IsFinite(length) || length == 0)
				{
					return false;
				}

				int significant = row.Count(v => Math.Abs(v) / length > tolerance);
				if (significant != 1)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Whether a size entry scales all three axes together: when Keep proportions is on, and also when the selection
		/// is turned (scaling one world axis of a turned part would shear it into a parallelogram).
		/// </summary>
		public static bool ScaleProportionally(bool keepProportions, bool axisAligned) => keepProportions || !axisAligned;

		/// <summary>
		/// The step that puts the selection's centre X, centre Y or bottom Z at <paramref name="value"/>, or null when it
		/// is already there or the box is not valid.
		/// </summary>
		public static TransformStep Move(TransformBounds bounds, TransformField field, double value)
		{
			if (!bounds.IsValid || !IsFinite(value))
			{
				return null;
			}

			var step = new TransformStep();
			switch (field)
			{
				case TransformField.CenterX:
					step.MoveX = value - bounds.CenterX;
					break;

				case TransformField.CenterY:
					step.MoveY = value - bounds.CenterY;
					break;

				case TransformField.BottomZ:
					step.MoveZ = value - bounds.Bottom;
					break;

				default:
					return null;
			}

			return step.IsIdentity ? null : step;
		}

		/// <summary>
		/// The step that makes the selection <paramref name="value"/> mm along a size field's axis, scaling about the
		/// centre in X and Y and the bottom in Z (so the part stays where it is and on the bed). With
		/// <paramref name="proportional"/> every axis scales by the same factor. Null when the size is zero, negative or
		/// not a number, when the selection is flat along that axis (it cannot be scaled to a size), or when nothing
		/// changes.
		/// </summary>
		public static TransformStep Resize(TransformBounds bounds, TransformField field, double value, bool proportional)
		{
			if (!bounds.IsValid
				|| !IsFinite(value)
				|| value < MinimumSize)
			{
				return null;
			}

			double current;
			switch (field)
			{
				case TransformField.SizeX:
					current = bounds.SizeX;
					break;

				case TransformField.SizeY:
					current = bounds.SizeY;
					break;

				case TransformField.SizeZ:
					current = bounds.SizeZ;
					break;

				default:
					return null;
			}

			if (current < MinimumSize)
			{
				return null;
			}

			var factor = value / current;
			if (factor == 1)
			{
				return null;
			}

			var step = new TransformStep
			{
				PivotX = bounds.CenterX,
				PivotY = bounds.CenterY,
				PivotZ = bounds.Bottom,
			};

			if (proportional)
			{
				step.ScaleX = step.ScaleY = step.ScaleZ = factor;
			}
			else if (field == TransformField.SizeX)
			{
				step.ScaleX = factor;
			}
			else if (field == TransformField.SizeY)
			{
				step.ScaleY = factor;
			}
			else
			{
				step.ScaleZ = factor;
			}

			return step;
		}

		/// <summary>
		/// The step that turns the selection by <paramref name="degrees"/> about a rotate field's world axis through the
		/// centre of its bounds. Null for 0, a whole turn or not a number. (The app then keeps the bottom where it was, so
		/// a turned part does not sink into the bed: see <see cref="KeepBottom"/>.)
		/// </summary>
		public static TransformStep Rotate(TransformBounds bounds, TransformField field, double degrees)
		{
			if (!bounds.IsValid || !IsFinite(degrees))
			{
				return null;
			}

			var normalized = degrees % 360;
			if (normalized == 0)
			{
				return null;
			}

			int axis;
			switch (field)
			{
				case TransformField.RotateX:
					axis = 0;
					break;

				case TransformField.RotateY:
					axis = 1;
					break;

				case TransformField.RotateZ:
					axis = 2;
					break;

				default:
					return null;
			}

			return new TransformStep
			{
				PivotX = bounds.CenterX,
				PivotY = bounds.CenterY,
				PivotZ = bounds.CenterZ,
				RotateAxis = axis,
				RotateDegrees = normalized,
			};
		}

		/// <summary>
		/// The Z move that puts the bottom of the turned selection (<paramref name="bottomAfter"/>) back at
		/// <paramref name="bottomBefore"/>.
		/// </summary>
		public static double KeepBottom(double bottomBefore, double bottomAfter)
		{
			if (!IsFinite(bottomBefore) || !IsFinite(bottomAfter))
			{
				return 0;
			}

			return bottomBefore - bottomAfter;
		}

		/// <summary>
		/// The number typed in a field, or null for empty or invalid input (the field then reverts). Accepts a plain
		/// number ("20", "-3.5"), an optional unit ("20 mm", "90°"), a leading "=", and, when <paramref name="evaluate"/>
		/// is given, arithmetic such as "20+5" or "40/3" (the app passes its expression parser; it returns NaN for text it
		/// cannot read).
		/// </summary>
		public static double? ParseNumber(string text, Func<string, double> evaluate = null)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}

			var trimmed = text.Trim();
			if (trimmed.StartsWith("="))
			{
				trimmed = trimmed.Substring(1).Trim();
			}

			foreach (var unit in new[] { "mm", "°", "deg" })
			{
				if (trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
				{
					trimmed = trimmed.Substring(0, trimmed.Length - unit.Length).Trim();
					break;
				}
			}

			if (trimmed.Length == 0)
			{
				return null;
			}

			// a plain number: digits with at most one decimal point, an optional sign; no exponent ("1e2"), no thousands
			// or decimal comma ("100,126"), so nothing typed is read as a different number
			if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[+-]?(\d+(\.\d*)?|\.\d+)$"))
			{
				if (double.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var plain))
				{
					return IsFinite(plain) ? plain : (double?)null;
				}

				return null;
			}

			if (evaluate == null
				|| !IsPlainArithmetic(trimmed))
			{
				return null;
			}

			double value;
			try
			{
				value = evaluate(trimmed);
			}
			catch
			{
				return null;
			}

			return IsFinite(value) ? value : (double?)null;
		}

		/// <summary>
		/// Only arithmetic on plain numbers: digits, + - * /, brackets and spaces (no names, so no sheet variables or
		/// functions), each number with at most one decimal point, and an operator between any two numbers. The
		/// expression parser reads "79.7100.126" (new digits typed after the old value) as 79.71 × 0.126; that is refused
		/// here, as are "2 3" and "2(3)".
		/// </summary>
		public static bool IsPlainArithmetic(string text)
		{
			// the previous token: 'n' number, ')' close, 'o' operator or open bracket, ' ' start
			char previous = ' ';
			int i = 0;
			while (i < text.Length)
			{
				var c = text[i];
				if (c == ' ')
				{
					i++;
					continue;
				}

				if (char.IsDigit(c) || c == '.')
				{
					int start = i;
					while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
					{
						i++;
					}

					var number = text.Substring(start, i - start);
					if (number.Count(ch => ch == '.') > 1
						|| number == "."
						|| previous == 'n'
						|| previous == ')')
					{
						return false;
					}

					previous = 'n';
					continue;
				}

				if (c == '(')
				{
					if (previous == 'n' || previous == ')')
					{
						return false;
					}

					previous = 'o';
				}
				else if (c == ')')
				{
					previous = ')';
				}
				else if (c == '+' || c == '-' || c == '*' || c == '/')
				{
					previous = 'o';
				}
				else
				{
					return false;
				}

				i++;
			}

			return true;
		}

		/// <summary>A value as the fields show it: at most two decimals, no trailing zeros, "0" rather than "-0".</summary>
		public static string Format(double value)
		{
			if (!IsFinite(value))
			{
				return "";
			}

			var rounded = Math.Round(value, 2);
			if (rounded == 0)
			{
				rounded = 0;
			}

			return rounded.ToString("0.##", CultureInfo.InvariantCulture);
		}

		private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}
}
