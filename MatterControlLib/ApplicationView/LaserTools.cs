using MatterHackers.MatterControl.SettingsManagement;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: "Export for laser" (OemSettings.EnableLaserExport): the 2D outline of parts saved as SVG or DXF for a
	/// laser cutter's own software, and (OemSettings.EnableLaserGcode, maintainers only) as a laser G-code file (.nc).
	/// It writes files only; it never controls a machine and never changes 3D printing, slicing or the printer. With the
	/// switches off nothing here changes the stock interface.
	/// </summary>
	public static class LaserTools
	{
		/// <summary>Whether this build has the laser export at all.</summary>
		public static bool IsEnabled => OemSettings.Instance.EnableLaserExport;

		/// <summary>
		/// Whether "G-code for laser (.nc)" is offered now: both switches on, and the maintainer lock in use and unlocked.
		/// Absent (not greyed) otherwise.
		/// </summary>
		public static bool GcodeAvailable => IsEnabled
			&& OemSettings.Instance.EnableLaserGcode
			&& Maintainer.LockEnabled
			&& Maintainer.IsUnlocked;
	}
}
