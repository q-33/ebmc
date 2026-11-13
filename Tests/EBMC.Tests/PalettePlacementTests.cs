using System.Collections.Generic;
using System.Linq;
using MatterHackers.MatterControl.PartPreviewWindow;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Where a click on the shapes palette puts a part: the bed centre, or the nearest free spot on the bed.
	/// </summary>
	[TestFixture]
	public class PalettePlacementTests
	{
		// A printer whose origin is the bed's front-left corner (like the club's Pulse): print_center is mid-bed
		private static readonly BedRect Bed = new BedRect(0, 0, 220, 220);

		private const double CenterX = 110;

		private const double CenterY = 110;

		[Test]
		public void EmptyBedUsesTheBedCenter()
		{
			var (x, y, found) = PalettePlacement.FindOpenCenter(CenterX, CenterY, 20, 20, Enumerable.Empty<BedRect>(), Bed);
			Assert.That((x, y, found), Is.EqualTo((CenterX, CenterY, true)));
		}

		[Test]
		public void DesignTabsCenterOnTheOrigin()
		{
			var (x, y, found) = PalettePlacement.FindOpenCenter(0, 0, 20, 20, Enumerable.Empty<BedRect>(), null);
			Assert.That((x, y, found), Is.EqualTo((0.0, 0.0, true)));
		}

		[Test]
		public void TakenCenterMovesToTheNearestFreeSpot()
		{
			var cube = BedRect.Around(CenterX, CenterY, 20, 20);
			var (x, y, _) = PalettePlacement.FindOpenCenter(CenterX, CenterY, 20, 20, new[] { cube }, Bed);

			// 20 mm part, 20 mm cube, 2 mm gap: 22 mm apart at least, so the first 5 mm step that clears is 25 mm
			Assert.That((x, y), Is.EqualTo((CenterX + 25, CenterY)));
		}

		[Test]
		public void ConsecutiveInsertsDoNotOverlapAndStayOnTheBed()
		{
			var placed = new List<BedRect>();
			for (int i = 0; i < 12; i++)
			{
				var (x, y, found) = PalettePlacement.FindOpenCenter(CenterX, CenterY, 20, 20, placed, Bed);
				Assert.That(found, Is.True, $"part {i} found a free spot");
				var footprint = BedRect.Around(x, y, 20, 20);

				Assert.That(Bed.Contains(footprint), Is.True, $"part {i} at {footprint} is on the bed");
				Assert.That(placed.Any(p => p.Overlaps(footprint)), Is.False, $"part {i} at {footprint} overlaps an earlier part");
				Assert.That(placed.Any(p => p.Overlaps(BedRect.Around(x, y, 20 + 2 * PalettePlacement.GapMm, 20 + 2 * PalettePlacement.GapMm))), Is.False, $"part {i} keeps the gap");

				placed.Add(footprint);
			}
		}

		[Test]
		public void NeverLeavesTheBed()
		{
			// a small bed nearly filled by one part: the only free spots are near the edges
			var bed = new BedRect(0, 0, 60, 60);
			var big = BedRect.Around(30, 30, 30, 30);
			var (x, y, _) = PalettePlacement.FindOpenCenter(30, 30, 10, 10, new[] { big }, bed);

			Assert.That(bed.Contains(BedRect.Around(x, y, 10, 10)), Is.True);
			Assert.That(big.Overlaps(BedRect.Around(x, y, 14, 14)), Is.False);
		}

		[Test]
		public void FullBedFallsBackToTheCenter()
		{
			var bed = new BedRect(0, 0, 40, 40);
			var (x, y, found) = PalettePlacement.FindOpenCenter(20, 20, 30, 30, new[] { bed }, bed);
			Assert.That((x, y, found), Is.EqualTo((20.0, 20.0, false)));
		}

		[TestCase(220, 220, true)]
		[TestCase(100, 50, true)]
		[TestCase(221, 10, false)]
		[TestCase(10, 230, false)]
		public void FitsOnBedChecksTheFootprintAgainstTheBed(double width, double depth, bool expected)
		{
			Assert.That(PalettePlacement.FitsOnBed(width, depth, Bed), Is.EqualTo(expected));
		}

		[Test]
		public void AnythingFitsWithNoBed()
		{
			Assert.That(PalettePlacement.FitsOnBed(5000, 5000, null), Is.True);
		}

		[Test]
		public void PartBiggerThanTheBedIsNotFound()
		{
			// it can never be placed, so the caller must say "bigger than the bed", not "the bed is full"
			var (_, _, found) = PalettePlacement.FindOpenCenter(CenterX, CenterY, 300, 20, Enumerable.Empty<BedRect>(), Bed);
			Assert.That(found, Is.False);
			Assert.That(PalettePlacement.FitsOnBed(300, 20, Bed), Is.False);
		}
	}
}
