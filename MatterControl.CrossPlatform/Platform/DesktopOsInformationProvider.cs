using System;
using MatterHackers.Agg;
using MatterHackers.Agg.Platform;

namespace MatterHackers.MatterControl.CrossPlatform
{
	public class DesktopOsInformationProvider : IOsInformationProvider
	{
		/// <summary>
		/// Usable size of the primary display in framebuffer pixels. Set once at
		/// startup from the main thread, because the windowing calls that produce it
		/// may only be made there while this property can be read from any thread.
		/// </summary>
		public static Point2D PrimaryDesktopSize { get; set; } = new Point2D(1920, 1080);

		public OSType OperatingSystem { get; } = DetectOperatingSystem();

		public Point2D DesktopSize => PrimaryDesktopSize;

		public long PhysicalMemory { get; } = DetectPhysicalMemory();

		private static OSType DetectOperatingSystem()
		{
			if (System.OperatingSystem.IsMacOS())
			{
				return OSType.Mac;
			}

			if (System.OperatingSystem.IsWindows())
			{
				return OSType.Windows;
			}

			if (System.OperatingSystem.IsLinux() || System.OperatingSystem.IsFreeBSD())
			{
				return OSType.X11;
			}

			return OSType.Other;
		}

		private static long DetectPhysicalMemory()
		{
			try
			{
				long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
				if (bytes > 0)
				{
					return bytes;
				}
			}
			catch (Exception)
			{
			}

			// A conservative guess keeps memory-based decisions on the safe side
			return 4L * 1024 * 1024 * 1024;
		}
	}
}
