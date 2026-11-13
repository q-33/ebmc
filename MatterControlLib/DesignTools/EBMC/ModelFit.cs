using System;
using System.Collections.Generic;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>EBMC Edition: what, if anything, is wrong with the size of a newly loaded model.</summary>
	public enum ModelFitIssue
	{
		/// <summary>The size looks plausible; say nothing.</summary>
		None,

		/// <summary>Largest size under <see cref="ModelFit.TinyLimitMm"/>: probably made in inches or centimetres.</summary>
		Tiny,

		/// <summary>Printer tab: does not fit the bed and build height.</summary>
		TooBigForBed,

		/// <summary>Design tab: largest size over <see cref="ModelFit.DesignLimitMm"/>.</summary>
		Huge,
	}

	/// <summary>One button of the size prompt: a uniform scale factor.</summary>
	public struct ModelFitChoice
	{
		public ModelFitChoice(double scale, bool isShrinkToFit = false)
		{
			Scale = scale;
			IsShrinkToFit = isShrinkToFit;
		}

		/// <summary>Uniform scale to apply (above 1 makes the model bigger).</summary>
		public double Scale { get; }

		/// <summary>True for the computed "Shrink to fit" factor.</summary>
		public bool IsShrinkToFit { get; }
	}

	/// <summary>The decision for one model: the issue and the scale choices to offer (besides "Leave it").</summary>
	public sealed class ModelFitAdvice
	{
		public static readonly ModelFitAdvice NoIssue = new ModelFitAdvice(ModelFitIssue.None, 0, new ModelFitChoice[0]);

		public ModelFitAdvice(ModelFitIssue issue, double largestSizeMm, IReadOnlyList<ModelFitChoice> choices)
		{
			Issue = issue;
			LargestSizeMm = largestSizeMm;
			Choices = choices;
		}

		public ModelFitIssue Issue { get; }

		public double LargestSizeMm { get; }

		public IReadOnlyList<ModelFitChoice> Choices { get; }
	}

	/// <summary>
	/// EBMC Edition: decides whether a model that was just loaded is a suspicious size and which uniform scales to
	/// offer. Pure logic with no GUI or geometry types so it can be unit tested. It only helps the user place a model;
	/// it has nothing to do with slicing.
	/// </summary>
	public static class ModelFit
	{
		/// <summary>A model whose largest size is under this (mm) is treated as tiny.</summary>
		public const double TinyLimitMm = 2;

		/// <summary>On a design tab, a model whose largest size is over this (mm) is treated as huge.</summary>
		public const double DesignLimitMm = 1000;

		/// <summary>Shrink to fit leaves this fraction of the bed free (2%).</summary>
		public const double ShrinkMargin = 0.02;

		public const double InchesToMm = 25.4;

		public const double CentimetresToMm = 10;

		/// <summary>
		/// Advice for a model of the given size (mm, along X, Y and Z of the bed).
		/// </summary>
		/// <param name="onPrinter">True on a printer tab: the bed limits apply instead of <see cref="DesignLimitMm"/>.</param>
		/// <param name="bedWidth">Bed size along X (the diameter of a round bed). 0 or less: no limit.</param>
		/// <param name="bedDepth">Bed size along Y (ignored for a round bed). 0 or less: no limit.</param>
		/// <param name="buildHeight">Build height. 0 or less: no limit (some profiles leave it unset).</param>
		/// <param name="roundBed">True for a circular bed, where the model's footprint must fit inside the diameter.</param>
		/// <param name="footprintDiameter">
		/// Round beds: the diameter of a circle around the bounding box's centre that holds the model's whole footprint
		/// (twice the largest distance of any point from that centre). 0 or less: unknown, the X-Y diagonal is used,
		/// which is too strict for round parts.
		/// </param>
		public static ModelFitAdvice Advise(double sizeX, double sizeY, double sizeZ,
			bool onPrinter,
			double bedWidth = 0, double bedDepth = 0, double buildHeight = 0,
			bool roundBed = false,
			double footprintDiameter = 0)
		{
			sizeX = Clean(sizeX);
			sizeY = Clean(sizeY);
			sizeZ = Clean(sizeZ);
			footprintDiameter = Clean(footprintDiameter);

			double largest = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
			if (largest <= 0)
			{
				// nothing measurable (an empty or failed load); nothing useful to offer
				return ModelFitAdvice.NoIssue;
			}

			if (largest < TinyLimitMm)
			{
				// only offer a scale whose result still fits (the bed on a printer tab, the design limit otherwise)
				var choices = new List<ModelFitChoice>();
				foreach (var scale in new[] { InchesToMm, CentimetresToMm })
				{
					bool fits = onPrinter
						? Fits(sizeX * scale, sizeY * scale, sizeZ * scale, bedWidth, bedDepth, buildHeight, roundBed, footprintDiameter * scale)
						: largest * scale <= DesignLimitMm;
					if (fits)
					{
						choices.Add(new ModelFitChoice(scale));
					}
				}

				return new ModelFitAdvice(ModelFitIssue.Tiny, largest, choices);
			}

			if (onPrinter)
			{
				if (!Fits(sizeX, sizeY, sizeZ, bedWidth, bedDepth, buildHeight, roundBed, footprintDiameter))
				{
					double shrink = ShrinkToFitScale(sizeX, sizeY, sizeZ, bedWidth, bedDepth, buildHeight, roundBed, footprintDiameter);
					if (shrink > 0 && shrink < 1)
					{
						return new ModelFitAdvice(ModelFitIssue.TooBigForBed, largest, new[] { new ModelFitChoice(shrink, isShrinkToFit: true) });
					}
				}

				return ModelFitAdvice.NoIssue;
			}

			if (largest > DesignLimitMm)
			{
				return new ModelFitAdvice(ModelFitIssue.Huge, largest, new[]
				{
					new ModelFitChoice(1 / CentimetresToMm),
					new ModelFitChoice(1 / InchesToMm),
				});
			}

			return ModelFitAdvice.NoIssue;
		}

		/// <summary>True when the model fits the bed and build height exactly (no margin). Unset limits always fit.</summary>
		public static bool Fits(double sizeX, double sizeY, double sizeZ,
			double bedWidth, double bedDepth, double buildHeight, bool roundBed, double footprintDiameter = 0)
		{
			if (roundBed)
			{
				// 1 µm of slack: a disc of exactly the bed's diameter measures a hair over it in floating point
				if (bedWidth > 0 && RoundFootprint(sizeX, sizeY, footprintDiameter) > bedWidth + 1e-3)
				{
					return false;
				}
			}
			else
			{
				if (bedWidth > 0 && sizeX > bedWidth)
				{
					return false;
				}

				if (bedDepth > 0 && sizeY > bedDepth)
				{
					return false;
				}
			}

			return !(buildHeight > 0 && sizeZ > buildHeight);
		}

		/// <summary>
		/// The largest uniform scale that fits the model inside the bed and build height with a
		/// <see cref="ShrinkMargin"/> margin. Above 1 when the model already fits. 0 when no limit applies.
		/// </summary>
		public static double ShrinkToFitScale(double sizeX, double sizeY, double sizeZ,
			double bedWidth, double bedDepth, double buildHeight, bool roundBed, double footprintDiameter = 0)
		{
			sizeX = Clean(sizeX);
			sizeY = Clean(sizeY);
			sizeZ = Clean(sizeZ);

			double keep = 1 - ShrinkMargin;
			double scale = double.PositiveInfinity;

			void Limit(double size, double limit)
			{
				if (size > 0 && limit > 0)
				{
					scale = Math.Min(scale, limit * keep / size);
				}
			}

			if (roundBed)
			{
				Limit(RoundFootprint(sizeX, sizeY, Clean(footprintDiameter)), bedWidth);
			}
			else
			{
				Limit(sizeX, bedWidth);
				Limit(sizeY, bedDepth);
			}

			Limit(sizeZ, buildHeight);

			return double.IsPositiveInfinity(scale) ? 0 : scale;
		}

		/// <summary>
		/// The diameter the model needs on a round bed: the measured footprint when known (never more than the box's
		/// diagonal, never less than its larger side), else the diagonal.
		/// </summary>
		public static double RoundFootprint(double sizeX, double sizeY, double footprintDiameter)
		{
			double diagonal = Math.Sqrt(sizeX * sizeX + sizeY * sizeY);
			if (footprintDiameter <= 0)
			{
				return diagonal;
			}

			return Math.Min(diagonal, Math.Max(footprintDiameter, Math.Max(sizeX, sizeY)));
		}

		/// <summary>
		/// Twice the largest distance of any of the points (x, y pairs) from (centerX, centerY): the diameter of the
		/// circle about that centre that holds them all. 0 for no points.
		/// </summary>
		public static double FootprintDiameter(IEnumerable<(double x, double y)> points, double centerX, double centerY)
		{
			double maxSquared = 0;
			foreach (var (x, y) in points)
			{
				var dx = x - centerX;
				var dy = y - centerY;
				maxSquared = Math.Max(maxSquared, dx * dx + dy * dy);
			}

			return 2 * Math.Sqrt(maxSquared);
		}

		private static double Clean(double value)
		{
			return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
		}
	}
}
