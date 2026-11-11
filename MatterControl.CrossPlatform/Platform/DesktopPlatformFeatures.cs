using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.MatterControl.PrinterControls.PrinterConnections;
using MatterHackers.MatterControl.SlicerConfiguration;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// The macOS / Linux counterpart of MatterControl.Winforms' WindowsPlatformsFeatures.
	/// </summary>
	public class DesktopPlatformFeatures : INativePlatformFeatures
	{
		// Assemblies that ship with .NET itself can never contain MatterControl plugins
		private static readonly string[] FrameworkPrefixes =
		{
			"System.",
			"Microsoft.",
			"mscorlib",
			"netstandard",
			"WindowsBase",
			"createdump",
		};

		public event EventHandler PictureTaken;

		public bool CameraInUseByExternalProcess { get; set; } = false;

		public void TakePhoto(string imageFileName)
		{
			var noCameraImage = new ImageBuffer(640, 480);
			Graphics2D graphics = noCameraImage.NewGraphics2D();
			graphics.Clear(Color.White);
			graphics.DrawString("No Camera Detected", 320, 240, pointSize: 24, justification: Agg.Font.Justification.Center);
			graphics.DrawString(DateTime.Now.ToString(), 320, 200, pointSize: 12, justification: Agg.Font.Justification.Center);
			ImageIO.SaveImageData(imageFileName, noCameraImage);

			PictureTaken?.Invoke(null, null);
		}

		public void OpenCameraPreview()
		{
		}

		public void PlaySound(string fileName)
		{
			if (!OperatingSystem.IsMacOS())
			{
				return;
			}

			try
			{
				// afplay is part of macOS. It needs a file, so copy the sound out of StaticData.
				string tempFile = Path.Combine(Path.GetTempPath(), "MatterControl-" + Path.GetFileName(fileName));
				if (!File.Exists(tempFile))
				{
					using (var source = StaticData.Instance.OpenStream(Path.Combine("Sounds", fileName)))
					using (var destination = File.Create(tempFile))
					{
						source.CopyTo(destination);
					}
				}

				var startInfo = new ProcessStartInfo("/usr/bin/afplay") { UseShellExecute = false };
				startInfo.ArgumentList.Add(tempFile);
				Process.Start(startInfo)?.Dispose();
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("Could not play sound: " + e.Message);
			}
		}

		public bool IsNetworkConnected()
		{
			return true;
		}

		public void ConfigureWifi()
		{
		}

		public void ProcessCommandline()
		{
			foreach (string command in Environment.GetCommandLineArgs())
			{
				switch (command.ToUpper())
				{
					case "CLEAR_CACHE":
						CacheDirectory.DeleteCacheData();
						break;

					case "SHOW_MEMORY":
						RootSystemWindow.ShowMemoryUsed = true;
						break;
				}
			}
		}

		public void PlatformInit(Action<string> reporter)
		{
			if (Clipboard.Instance == null)
			{
				Clipboard.SetSystemClipboard(new GlfwClipboard());
			}

			ApplicationSettings.Instance.set("HardwareHasCamera", "false");
		}

		public void GenerateLocalizationValidationFile()
		{
			// A developer tool of the Windows build; nothing in the application depends on it
		}

		public void InitPluginFinder()
		{
			string searchPath = System.AppContext.BaseDirectory;

			// Load plugins from the dlls in the application directory
			foreach (string file in Directory.GetFiles(searchPath, "*.dll"))
			{
				string name = Path.GetFileName(file);
				if (FrameworkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}

				try
				{
					// Be sure not to load a DLL more than once!
					// https://github.com/dotnet/runtime/issues/39783
					PluginFinder.LoadTypesFromAssembly(AssemblyLoadContext.Default.LoadFromAssemblyPath(file));
				}
				catch (Exception e)
				{
					Debug.WriteLine("Error loading assembly: " + e.Message);
				}
			}
		}

		public GuiWidget GetConnectDevicePage(object printer)
		{
			return new SetupStepComPortOne(printer as PrinterConfig);
		}

		// Primarily required for Android, return true on other platforms
		public bool HasPermissionToDevice(object printer) => true;
	}
}
