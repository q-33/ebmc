using MatterHackers.MatterControl;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>The shortcut wording rule (MatterControlLib/ApplicationView/KeyHintRules.cs).</summary>
	[TestFixture]
	public class KeyHintTests
	{
		[TestCase("Zoom: Mouse Wheel, Ctrl + Alt + Left Mouse Button", false, "Zoom: Mouse Wheel, Ctrl + Alt + Left Mouse Button")]
		[TestCase("Zoom: Mouse Wheel, Ctrl + Alt + Left Mouse Button", true, "Zoom: Mouse Wheel, Cmd + Option + Left Mouse Button")]
		[TestCase("Ctrl+P", false, "Ctrl + P")]
		[TestCase("Ctrl+S", true, "Cmd + S")]
		[TestCase("Ctrl + P", true, "Cmd + P")]
		[TestCase("Redo: Ctrl + Y, Ctrl + Shift + Z", true, "Redo: Cmd + Y, Cmd + Shift + Z")]
		[TestCase("Ctrl + '+' & Ctrl + '-'", true, "Cmd + '+' & Cmd + '-'")]
		[TestCase("Up Arrow = +1, Down Arrow = -1, (Shift * 10, Ctrl / 10)", true, "Up Arrow = +1, Down Arrow = -1, (Shift * 10, Cmd / 10)")]
		[TestCase("Alternate", true, "Alternate")]
		public void Wording(string hint, bool mac, string expected)
		{
			Assert.That(KeyHintRules.ForSystem(hint, mac), Is.EqualTo(expected));
		}
	}
}
