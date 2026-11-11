using MatterHackers.Agg.UI;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// Hosts the application in one GLFW window. Dialogs are drawn inside that
	/// window as movable panels, which is what SingleWindowProvider implements.
	/// </summary>
	public class GlfwWindowProvider : SingleWindowProvider
	{
		public override void ShowSystemWindow(SystemWindow systemWindow)
		{
			if (platformWindow == null)
			{
				platformWindow = new GlfwPlatformWindow(this);
			}

			// For the application window this call runs the event loop and only
			// returns once that window has closed.
			base.ShowSystemWindow(systemWindow);

			// A dialog is wrapped in an overlay window by the base class. The dialog
			// itself still needs the platform window for cursors, modifier keys and
			// invalidation.
			if (!systemWindow.HasBeenClosed)
			{
				systemWindow.PlatformWindow = platformWindow;
			}
		}
	}
}
