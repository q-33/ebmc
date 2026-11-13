using System;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.OctoPrint;
using MatterHackers.MatterControl.SlicerConfiguration;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The "Ready to print?" question asked before a print is started through OctoPrint, and the rule that refuses a
	/// G-code file from elsewhere while the maintainer lock is on.
	/// </summary>
	[TestFixture]
	[SetCulture("en-US")]
	public class PrintSafetyTests
	{
		private static readonly Func<string, string> Identity = s => s;

		private static readonly Func<string, string> Bracket = s => "[" + s + "]";

		[Test]
		public void ConfirmBodyWithMaterialAndHeatedBed()
		{
			Assert.That(
				OctoPrintMessages.ConfirmPrintBody("NylonX", 265, 60, Identity),
				Is.EqualTo("Is the bed empty and clean?\nIs NylonX loaded?\n\nThe printer will heat to 265 °C (bed 60 °C), home, and probe the bed before it prints."));
		}

		[Test]
		public void ConfirmBodyWithoutHeatedBed()
		{
			Assert.That(
				OctoPrintMessages.ConfirmPrintBody("PLA", 210, null, Identity),
				Is.EqualTo("Is the bed empty and clean?\nIs PLA loaded?\n\nThe printer will heat to 210 °C, home, and probe the bed before it prints."));
		}

		[TestCase(null)]
		[TestCase("")]
		[TestCase("   ")]
		public void ConfirmBodyWithoutMaterial(string material)
		{
			string text = OctoPrintMessages.ConfirmPrintBody(material, 265, 60, Identity);
			Assert.That(text, Does.Contain("\nIs the right material loaded?\n"));
		}

		[Test]
		public void ConfirmBodyTrimsMaterialAndKeepsHalfDegrees()
		{
			string text = OctoPrintMessages.ConfirmPrintBody("  NylonX ", 212.5, 60.27, Identity);
			Assert.That(text, Does.Contain("Is NylonX loaded?"));
			Assert.That(text, Does.Contain("heat to 212.5 °C (bed 60.3 °C)"));
		}

		[Test]
		public void ConfirmWordingIsLocalized()
		{
			Assert.That(OctoPrintMessages.ConfirmPrintTitle(Bracket), Is.EqualTo("[Ready to print?]"));
			Assert.That(OctoPrintMessages.ConfirmPrintStart(Bracket), Is.EqualTo("[Start print]"));

			string body = OctoPrintMessages.ConfirmPrintBody(null, 265, 60, Bracket);
			Assert.That(body, Does.StartWith("[Is the bed empty and clean?]\n"));
			Assert.That(body, Does.Contain("[Is [the right material] loaded?]"));
			Assert.That(body, Does.Contain("[The printer will heat to 265 °C (bed 60 °C), home, and probe the bed before it prints.]"));

			string noBed = OctoPrintMessages.ConfirmPrintBody("PLA", 210, null, Bracket);
			Assert.That(noBed, Does.Contain("[The printer will heat to 210 °C, home, and probe the bed before it prints.]"));
		}

		[Test]
		public void ConfirmWordingNeverNamesTheUpstreamProduct()
		{
			foreach (string text in new[]
			{
				OctoPrintMessages.ConfirmPrintTitle(Identity),
				OctoPrintMessages.ConfirmPrintStart(Identity),
				OctoPrintMessages.ConfirmPrintBody(null, 265, 60, Identity),
				OctoPrintMessages.ConfirmPrintBody("NylonX", 265, null, Identity),
				MaintainerMessages.ForeignGCode(Identity),
				MaintainerMessages.ForeignGCodeTitle(Identity),
			})
			{
				Assert.That(text, Does.Not.Contain("MatterControl"));
			}
		}

		// isGCodeSource, lockEnabled, unlocked -> refused
		[TestCase(true, true, false, true)]
		[TestCase(true, true, true, false)]
		[TestCase(true, false, false, false)]
		[TestCase(true, false, true, false)]
		[TestCase(false, true, false, false)]
		[TestCase(false, true, true, false)]
		[TestCase(false, false, false, false)]
		[TestCase(false, false, true, false)]
		public void GCodeSourceRefusedOnlyForLockedMembers(bool isGCodeSource, bool lockEnabled, bool unlocked, bool refused)
		{
			Assert.That(SettingsLock.RefuseGCodeSource(isGCodeSource, lockEnabled, unlocked), Is.EqualTo(refused));
		}

		[Test]
		public void ForeignGCodeWording()
		{
			Assert.That(
				MaintainerMessages.ForeignGCode(Identity),
				Is.EqualTo("This is a ready-made G-code file, so the app cannot check that it suits this printer. Open the model file (STL, 3MF or OBJ) instead, or ask a club maintainer."));
			Assert.That(MaintainerMessages.ForeignGCode(Bracket), Does.StartWith("[").And.EndWith("]"));
			Assert.That(MaintainerMessages.ForeignGCodeTitle(Bracket), Is.EqualTo("[G-Code File Not Allowed]"));
		}

		// hasCommandParts, lockEnabled, unlocked -> refused
		[TestCase(true, true, false, true)]
		[TestCase(true, true, true, false)]
		[TestCase(true, false, false, false)]
		[TestCase(true, false, true, false)]
		[TestCase(false, true, false, false)]
		[TestCase(false, true, true, false)]
		[TestCase(false, false, false, false)]
		[TestCase(false, false, true, false)]
		public void CommandPartsRefusedOnlyForLockedMembers(bool hasCommandParts, bool lockEnabled, bool unlocked, bool refused)
		{
			Assert.That(SettingsLock.RefuseCommandParts(hasCommandParts, lockEnabled, unlocked), Is.EqualTo(refused));
		}

		[Test]
		public void CommandPartsWording()
		{
			Assert.That(
				MaintainerMessages.CommandParts(Identity),
				Is.EqualTo("This plate has parts that change the printer's temperature or send it commands. Ask a club maintainer to print it."));
			Assert.That(MaintainerMessages.CommandPartsTitle(Bracket), Does.StartWith("[").And.EndWith("]"));
		}

		// isGCodeSource, hasCommandParts -> generic question (no material or temperatures)
		[TestCase(false, false, false)]
		[TestCase(true, false, true)]
		[TestCase(false, true, true)]
		[TestCase(true, true, true)]
		public void GenericConfirmationWhenTemperaturesAreNotTheMaterials(bool isGCodeSource, bool hasCommandParts, bool generic)
		{
			Assert.That(OctoPrintMessages.UseGenericConfirmation(isGCodeSource, hasCommandParts), Is.EqualTo(generic));
		}

		[Test]
		public void GenericConfirmationStatesNoNumbers()
		{
			string text = OctoPrintMessages.ConfirmPrintBodyGeneric(Identity);
			Assert.That(text, Is.EqualTo("Is the bed empty and clean?\nIs the right material loaded?\n\nThe printer will heat, home, and probe the bed before it prints."));
			Assert.That(text, Does.Not.Match("[0-9]"));
			Assert.That(text, Does.Not.Contain("°"));
		}
	}
}
