using System.IO;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The Simple · Advanced switch in the top bar. agg-sharp's FlowLayoutWidget starts as VAnchor.Fit; assigning
	/// VAnchor.Center alone drops Fit, the switch is laid out 0 units tall and its segments are clipped away (it only
	/// reserved its width, the empty space left of search). Checked against the source because the widget needs the
	/// running app to build.
	/// </summary>
	[TestFixture]
	public class TopBarLayoutTests
	{
		[Test]
		public void ModeSwitchFitsItsHeight()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "EBMC.md")))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null);
			var source = File.ReadAllText(Path.Combine(directory.FullName, "MatterControlLib", "PartPreviewWindow", "UiModeSwitch.cs"));
			Assert.That(source, Does.Contain("this.VAnchor = VAnchor.Center | VAnchor.Fit;"));
			Assert.That(source, Does.Not.Contain("this.VAnchor = VAnchor.Center;"));
			Assert.That(source, Does.Contain("private const double TrackHeight = 28;"), "segments are 24 units: a comfortable click target");
		}
	}
}
