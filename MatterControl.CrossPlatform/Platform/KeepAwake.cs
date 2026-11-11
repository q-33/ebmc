using System;
using System.Diagnostics;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// Stops the computer from going to sleep in the middle of a print. A USB
	/// print is streamed line by line from this machine, so a sleeping computer
	/// means a stopped (and ruined) print.
	///
	/// Both platforms use a small helper process that holds a sleep assertion
	/// and exits by itself when MatterControl goes away, however that happens.
	/// </summary>
	internal static class KeepAwake
	{
		private static readonly object Locker = new object();
		private static Process helper;

		public static void Set(bool keepAwake)
		{
			lock (Locker)
			{
				if (keepAwake)
				{
					if (helper == null || HasExited(helper))
					{
						helper?.Dispose();
						helper = StartHelper();
					}
				}
				else if (helper != null)
				{
					try
					{
						if (!helper.HasExited)
						{
							helper.Kill();
						}
					}
					catch (Exception)
					{
					}

					helper.Dispose();
					helper = null;
				}
			}
		}

		private static bool HasExited(Process process)
		{
			try
			{
				return process.HasExited;
			}
			catch (Exception)
			{
				return true;
			}
		}

		private static Process StartHelper()
		{
			try
			{
				string processId = Environment.ProcessId.ToString();
				var startInfo = new ProcessStartInfo { UseShellExecute = false };

				if (OperatingSystem.IsMacOS())
				{
					// -i: no idle sleep, -w: only for as long as this process exists
					startInfo.FileName = "/usr/bin/caffeinate";
					startInfo.ArgumentList.Add("-i");
					startInfo.ArgumentList.Add("-w");
					startInfo.ArgumentList.Add(processId);
				}
				else if (OperatingSystem.IsLinux())
				{
					// Holds the inhibitor for as long as tail runs, and tail ends with this process
					startInfo.FileName = "systemd-inhibit";
					startInfo.ArgumentList.Add("--what=idle:sleep");
					startInfo.ArgumentList.Add("--who=" + Program.AppName);
					startInfo.ArgumentList.Add("--why=A print is running");
					startInfo.ArgumentList.Add("tail");
					startInfo.ArgumentList.Add("--pid=" + processId);
					startInfo.ArgumentList.Add("-f");
					startInfo.ArgumentList.Add("/dev/null");
				}
				else
				{
					return null;
				}

				return Process.Start(startInfo);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("Could not prevent the computer from sleeping during the print: " + e.Message);
				return null;
			}
		}
	}
}
