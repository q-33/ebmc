using MatterHackers.MatterControl.SettingsManagement;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>
	/// EBMC Edition: the one switch every solid-modelling feature checks (palette Solids family, Primitives list, STEP
	/// open and export): the OEM setting EnableSolidModeling is on and the kernel library loaded. Stock settings leave
	/// it off without touching the native library.
	/// </summary>
	public static class SolidModeling
	{
		public static bool IsEnabled => OemSettings.Instance.EnableSolidModeling && SolidKernel.IsAvailable;
	}
}
