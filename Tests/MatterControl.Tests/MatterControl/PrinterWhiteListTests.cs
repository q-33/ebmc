using System.IO;
using System.Linq;
using MatterHackers.MatterControl.Tests.Automation;
using NUnit.Framework;

namespace MatterControl.Tests.MatterControl
{
	[TestFixture]
	class PrinterWhiteListTests
	{
		[Test, Category("PrinterWhiteListTests")]
		public void DesktopCalibrationPartsInSettings()
		{
			string settingsJsonPath = Path.Combine(MatterControlUtilities.StaticDataPath, "OEMSettings", "Settings.json");

			if (File.Exists(settingsJsonPath))
			{
				string[] lines = File.ReadAllLines(settingsJsonPath);
				bool hasCoin = lines.Where(l => l.Contains("\"MatterControl - Coin.stl\",")).Any();
				bool hasTabletStand = lines.Where(l => l.Contains("\"MatterControl - Stand.stl\",")).Any();

				// EBMC Edition: the MatterControl-branded sample parts are no longer preloaded
				Assert.IsFalse(hasCoin, "Unexpected coin file found");
				Assert.IsFalse(hasTabletStand, "Unexpected stand file found");
			}
		}

		[Test, Category("SamplePartsTests")]
		public void DesktopCalibrationPartsExist()
		{
			string samplePartsPath = Path.Combine(MatterControlUtilities.StaticDataPath, "OEMSettings", "SampleParts");
			string[] files = Directory.GetFiles(samplePartsPath);
			bool hasBadge = files.Where(l => l.Contains("EBMC Badge.stl")).Any();
			Assert.IsTrue(hasBadge, "Expected EBMC Badge file not found");
		}
	}
}
