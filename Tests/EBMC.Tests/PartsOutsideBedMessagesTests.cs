using System;
using MatterHackers.MatterControl;
using NUnit.Framework;

namespace EBMC.Tests
{
	[TestFixture]
	public class PartsOutsideBedMessagesTests
	{
		private static readonly Func<string, string> Identity = s => s;

		// Marks every template that went through the localize delegate
		private static readonly Func<string, string> Bracket = s => "[" + s + "]";

		[TestCase(0, false, PartsOutsideBedLevel.None)]
		[TestCase(0, true, PartsOutsideBedLevel.None)]
		[TestCase(1, true, PartsOutsideBedLevel.Warning)]
		[TestCase(5, true, PartsOutsideBedLevel.Warning)]
		[TestCase(1, false, PartsOutsideBedLevel.Error)]
		[TestCase(5, false, PartsOutsideBedLevel.Error)]
		public void Classify(int partsOutside, bool anyPartInside, PartsOutsideBedLevel expected)
		{
			Assert.That(PartsOutsideBedMessages.Classify(partsOutside, anyPartInside), Is.EqualTo(expected));
		}

		[Test]
		public void NoTextWhenNothingIsOutside()
		{
			Assert.That(PartsOutsideBedMessages.Title(PartsOutsideBedLevel.None, Identity), Is.Null);
			Assert.That(PartsOutsideBedMessages.Details(PartsOutsideBedLevel.None, new[] { "Cube" }, false, Identity), Is.Null);
		}

		[Test]
		public void WarningText()
		{
			Assert.That(PartsOutsideBedMessages.Title(PartsOutsideBedLevel.Warning, Identity), Is.EqualTo("Parts Outside the Bed"));
			Assert.That(
				PartsOutsideBedMessages.Details(PartsOutsideBedLevel.Warning, new[] { "Cube", "Bracket" }, false, Identity),
				Is.EqualTo("These parts are partly or fully outside the printable area; anything outside will not be printed: Cube, Bracket."));

			// The warning gives no extra advice either way
			Assert.That(
				PartsOutsideBedMessages.Details(PartsOutsideBedLevel.Warning, new[] { "Cube" }, true, Identity),
				Is.EqualTo(PartsOutsideBedMessages.Details(PartsOutsideBedLevel.Warning, new[] { "Cube" }, false, Identity)));
		}

		[Test]
		public void ErrorText()
		{
			Assert.That(PartsOutsideBedMessages.Title(PartsOutsideBedLevel.Error, Identity), Is.EqualTo("Nothing Is Inside the Printable Area"));

			Assert.That(
				PartsOutsideBedMessages.Details(PartsOutsideBedLevel.Error, new[] { "Cube" }, false, Identity),
				Is.EqualTo("Every part on the bed is outside the printable area, so nothing would be printed: Cube. Move them onto the bed."));
		}

		[Test]
		public void ErrorTextForPartsTooLargeForTheBed()
		{
			Assert.That(
				PartsOutsideBedMessages.Details(PartsOutsideBedLevel.Error, new[] { "Tower" }, true, Identity),
				Is.EqualTo("Every part on the bed is outside the printable area, so nothing would be printed: Tower. Move them onto the bed, or make them smaller if they are larger than the printable area."));
		}

		[TestCase(PartsOutsideBedLevel.None, 0, false)]
		[TestCase(PartsOutsideBedLevel.None, 2, false)]
		[TestCase(PartsOutsideBedLevel.Warning, 0, false)]
		[TestCase(PartsOutsideBedLevel.Warning, 1, true)]
		[TestCase(PartsOutsideBedLevel.Error, 0, false)]
		[TestCase(PartsOutsideBedLevel.Error, 3, true)]
		public void ArrangeIsOfferedOnlyWhenAnOutsidePartFits(PartsOutsideBedLevel level, int outsidePartsThatFit, bool expected)
		{
			Assert.That(PartsOutsideBedMessages.OfferArrange(level, outsidePartsThatFit), Is.EqualTo(expected));
		}

		[Test]
		public void NamesUpToTheCapAreAllListed()
		{
			Assert.That(PartsOutsideBedMessages.NameList(new[] { "A" }, Identity), Is.EqualTo("A"));
			Assert.That(PartsOutsideBedMessages.NameList(new[] { "A", "B", "C" }, Identity), Is.EqualTo("A, B, C"));
		}

		[Test]
		public void NamesPastTheCapAreCounted()
		{
			Assert.That(PartsOutsideBedMessages.MaxNamesListed, Is.EqualTo(3));
			Assert.That(PartsOutsideBedMessages.NameList(new[] { "A", "B", "C", "D" }, Identity), Is.EqualTo("A, B, C (and 1 more)"));
			Assert.That(
				PartsOutsideBedMessages.NameList(new[] { "Cube", "Bracket", "Clip", "Hook", "Peg", "Lid" }, Identity),
				Is.EqualTo("Cube, Bracket, Clip (and 3 more)"));
		}

		[Test]
		public void MissingNamesBecomeUnnamedPart()
		{
			Assert.That(PartsOutsideBedMessages.NameList(new[] { null, "", "  ", " Cube " }, Identity), Is.EqualTo("Unnamed part, Unnamed part, Unnamed part (and 1 more)"));
			Assert.That(PartsOutsideBedMessages.NameList(new[] { " Cube " }, Identity), Is.EqualTo("Cube"));
		}

		[Test]
		public void NoNamesGivesEmptyList()
		{
			Assert.That(PartsOutsideBedMessages.NameList(null, Identity), Is.EqualTo(""));
			Assert.That(PartsOutsideBedMessages.NameList(new string[0], Identity), Is.EqualTo(""));
		}

		[Test]
		public void AllTextGoesThroughLocalize()
		{
			foreach (var level in new[] { PartsOutsideBedLevel.Warning, PartsOutsideBedLevel.Error })
			{
				Assert.That(PartsOutsideBedMessages.Title(level, Bracket), Does.StartWith("[").And.EndWith("]"), level.ToString());
				foreach (bool tooLarge in new[] { false, true })
				{
					string details = PartsOutsideBedMessages.Details(level, new[] { "Cube" }, tooLarge, Bracket);
					Assert.That(details, Does.StartWith("[").And.EndWith("]"), level.ToString());
				}
			}

			Assert.That(PartsOutsideBedMessages.NameList(new[] { null, "B", "C", "D" }, Bracket), Is.EqualTo("[Unnamed part], B, C [(and 1 more)]"));
		}

		[Test]
		public void NullLocalizeIsTreatedAsIdentity()
		{
			Assert.That(PartsOutsideBedMessages.Title(PartsOutsideBedLevel.Warning, null), Is.EqualTo("Parts Outside the Bed"));
			Assert.That(PartsOutsideBedMessages.NameList(new[] { "" }, null), Is.EqualTo("Unnamed part"));
		}

		[Test]
		public void UserTextNeverNamesTheUpstreamProduct()
		{
			foreach (var level in new[] { PartsOutsideBedLevel.Warning, PartsOutsideBedLevel.Error })
			{
				Assert.That(PartsOutsideBedMessages.Title(level, Identity), Does.Not.Contain("MatterControl"));
				Assert.That(PartsOutsideBedMessages.Details(level, new[] { "Cube" }, true, Identity), Does.Not.Contain("MatterControl"));
			}
		}
	}
}
