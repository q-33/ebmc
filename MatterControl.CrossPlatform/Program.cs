using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using GLFW;
using Exception = System.Exception;
using MatterHackers.Agg;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.MatterControl.DataStorage;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.MatterControl.SlicerConfiguration;
using MatterHackers.SerialPortCommunication.FrostedSerial;
using Microsoft.Extensions.Configuration;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// Entry point of MatterControl on macOS (Apple silicon and Intel) and Linux.
	///
	/// It performs the same startup steps as the Windows Program.cs at the root of
	/// the repository, with the WinForms pieces replaced by the classes in the
	/// Platform folder.
	/// </summary>
	public static class Program
	{
		private const string RepositoryRootKey = "MatterControl.RepositoryRoot";

		public static int Main(string[] args)
		{
			AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportFatal(e.ExceptionObject as Exception);

			if (args.Any(a => a == "--version" || a == "--info"))
			{
				PrintInfo();
				return 0;
			}

			try
			{
				Run(args);
				return 0;
			}
			catch (Exception e)
			{
				ReportFatal(e);
				return 1;
			}
			finally
			{
				KeepAwake.Set(false);
			}
		}

		private static void Run(string[] args)
		{
			// GLFW and SQLite are found by name at first use, so this has to come first
			NativeLibraries.Register();

			LocateStaticData();

			// Set the global culture for the app, current thread and all new threads.
			// G-code and settings files always use '.' as the decimal separator.
			CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
			CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
			Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
			Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

			// Printers are reached through System.IO.Ports
			FrostedSerialPortFactory.GetPlatformSerialPort = (serialPortName) => new CSharpSerialPortWrapper(serialPortName);

			// Replace the WinForms defaults with the classes in this assembly
			var providers = AggContext.Config.ProviderTypes;
			providers.OsInformationProvider = typeof(DesktopOsInformationProvider).AssemblyQualifiedName;
			providers.DialogProvider = typeof(DesktopFileDialogProvider).AssemblyQualifiedName;
			providers.SystemWindowProvider = typeof(GlfwWindowProvider).AssemblyQualifiedName;

			// If files were passed on the command line, open them once the application has loaded
			var shellFiles = args
				.Where(f => File.Exists(f) && ApplicationController.ShellFileExtensions.Contains(Path.GetExtension(f).ToLower()))
				.Select(Path.GetFullPath)
				.ToArray();

			if (shellFiles.Any())
			{
				ApplicationController.StartupActions.Add(new ApplicationController.StartupAction()
				{
					Title = "Shell Files",
					Priority = 0,
					Action = () =>
					{
						foreach (string file in shellFiles)
						{
							ApplicationController.Instance.ShellOpenFile(file);
						}
					}
				});
			}

			// Load optional user configuration
			string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			IConfiguration config = new ConfigurationBuilder()
				.AddJsonFile(Path.Combine(System.AppContext.BaseDirectory, "appsettings.json"), optional: true)
				.AddJsonFile(Path.Combine(userProfilePath, "MatterControl.json"), optional: true)
				.Build();

			config.Bind("Agg:GraphicsMode", AggContext.Config.GraphicsMode);

			// There is no separate slicer executable in this build: MatterSlice runs inside
			// the application, on a background thread.
			Slicer.RunInProcess = true;

			Application.EnableF5Collect = config.GetValue<bool>("MatterControl:Application:EnableF5Collect");
			Application.EnableNetworkTraffic = config.GetValue("MatterControl:Application:EnableNetworkTraffic", true);

			// Make sure we have the right working directory as we assume everything relative to the executable.
			Directory.SetCurrentDirectory(System.AppContext.BaseDirectory);

			Datastore.Instance.Initialize(new SQLiteUnix.SQLiteConnection(ApplicationDataStorage.Instance.DatastorePath));

			double displayScale = InitializeDisplay();

			// Inside an application bundle GLFW changes the working directory to the bundle's
			// Resources folder when it starts, so put it back.
			Directory.SetCurrentDirectory(System.AppContext.BaseDirectory);
			AppContext.DisplayScale = displayScale;
			GuiWidget.DeviceScale = UserTextSize() * displayScale;

			// Init the platform features before ShowAsSystemWindow
			AppContext.Platform = new DesktopPlatformFeatures();
			AppContext.Platform.InitPluginFinder();
			AppContext.Platform.ProcessCommandline();

			config.Bind("MatterControl", AppContext.Options);

			// Window sizes are stored in pixels. A first start has no stored size, so
			// scale the default up on a high resolution display.
			var desktopSize = AggContext.DesktopSize;
			var (width, height) = RootSystemWindow.GetStartupBounds(
				(int)Math.Min(1280 * displayScale, desktopSize.x),
				(int)Math.Min(720 * displayScale, desktopSize.y));

			var rootSystemWindow = Application.LoadRootWindow(width, height);
			if (displayScale != 1)
			{
				rootSystemWindow.MinimumSize = rootSystemWindow.MinimumSize * displayScale;
			}

			SingleWindowProvider.SetWindowTheme(ApplicationController.Instance.Theme);

			ApplicationController.Instance.KeepAwake = KeepAwake.Set;

			GlfwPlatformWindow.ContentScaleChanged += OnContentScaleChanged;

			// Runs the event loop until the window is closed
			rootSystemWindow.ShowAsSystemWindow();
		}

		/// <summary>
		/// StaticData holds the profiles, themes, icons and translations. A published
		/// application carries its own copy; a development build reads the one in the
		/// repository it was built from, so a 40+ MB copy is not made on every build.
		/// </summary>
		private static void LocateStaticData()
		{
			string beside = Path.Combine(System.AppContext.BaseDirectory, "StaticData");
			if (Directory.Exists(beside))
			{
				StaticData.OverrideRootPath(beside);
				return;
			}

			string fromEnvironment = Environment.GetEnvironmentVariable("MATTERCONTROL_STATICDATA");
			if (!string.IsNullOrEmpty(fromEnvironment) && Directory.Exists(fromEnvironment))
			{
				StaticData.OverrideRootPath(fromEnvironment);
				return;
			}

			// Inside a macOS application bundle: MatterControl.app/Contents/Resources/StaticData
			string inBundle = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "..", "Resources", "StaticData"));
			if (Directory.Exists(inBundle))
			{
				StaticData.OverrideRootPath(inBundle);
				return;
			}

			string repositoryRoot = typeof(Program).Assembly
				.GetCustomAttributes<AssemblyMetadataAttribute>()
				.FirstOrDefault(a => a.Key == RepositoryRootKey)?.Value;

			if (!string.IsNullOrEmpty(repositoryRoot))
			{
				string inRepository = Path.Combine(repositoryRoot, "StaticData");
				if (Directory.Exists(inRepository))
				{
					StaticData.OverrideRootPath(inRepository);
					return;
				}
			}

			throw new DirectoryNotFoundException(
				"MatterControl could not find its StaticData folder. Looked next to the application ("
				+ beside + ") and in the source tree it was built from. "
				+ "Set MATTERCONTROL_STATICDATA to the folder's full path to point at it directly.");
		}

		/// <summary>
		/// Starts GLFW, creates the (still hidden) application window and works out the
		/// display scale the interface should be built for: 2 on a Retina panel, 1 on a
		/// standard one.
		/// </summary>
		private static double InitializeDisplay()
		{
			// First use of GLFW: loads the library and initializes it on this (the main) thread
			GlfwPlatformWindow.InstallErrorHandler();

			var monitor = Glfw.PrimaryMonitor;
			if (monitor == GLFW.Monitor.None)
			{
				throw new InvalidOperationException(
					"No display was found, so MatterControl cannot open its window. " + GlfwPlatformWindow.LastError);
			}

			// Measured from a real window rather than assumed from the monitor
			double pixelsPerUnit = NormalizeScale(GlfwPlatformWindow.CreateNativeWindow());

			// macOS reports sizes in points and hands out a larger framebuffer, so the
			// measurement is the scale. X11 and Windows always give one pixel per unit and
			// publish the desktop's scaling separately.
			double scale = OperatingSystem.IsMacOS()
				? pixelsPerUnit
				: Math.Max(pixelsPerUnit, NormalizeScale(monitor.ContentScale.X));

			var workArea = monitor.WorkArea;
			if (workArea.Width > 0 && workArea.Height > 0)
			{
				// The rest of the application counts pixels
				DesktopOsInformationProvider.PrimaryDesktopSize = new Point2D(
					(int)(workArea.Width * pixelsPerUnit),
					(int)(workArea.Height * pixelsPerUnit));
			}

			return scale;
		}

		private static double NormalizeScale(double scale)
		{
			if (double.IsNaN(scale) || scale < 1)
			{
				return 1;
			}

			return Math.Min(scale, 4);
		}

		private static double UserTextSize()
		{
			string textSizeMode = UserSettings.Instance.get(UserSettingsKey.ApplicationTextSize);
			if (!string.IsNullOrEmpty(textSizeMode)
				&& double.TryParse(textSizeMode, NumberStyles.Float, CultureInfo.InvariantCulture, out double textSize)
				&& textSize > 0)
			{
				return textSize;
			}

			return 1;
		}

		private static double pendingScale;

		/// <summary>
		/// The window was dragged to a display with a different scale factor, for
		/// example from the built-in Retina panel to an external monitor. Widget
		/// sizes are computed when they are created, so the interface is rebuilt.
		/// </summary>
		private static void OnContentScaleChanged(double newScale)
		{
			pendingScale = NormalizeScale(newScale);

			// Several notifications arrive while the window crosses between displays;
			// wait for it to settle and rebuild once.
			UiThread.RunOnIdle(() =>
			{
				if (pendingScale == AppContext.DisplayScale || ApplicationController.Instance == null)
				{
					return;
				}

				AppContext.DisplayScale = pendingScale;
				GuiWidget.DeviceScale = UserTextSize() * pendingScale;
				ApplicationController.Instance.ReloadAll().ConfigureAwait(false);
			}, 0.5);
		}

		private static void PrintInfo()
		{
			Console.WriteLine("MatterControl (GLFW / OpenGL host)");
			Console.WriteLine("  Runtime:      " + RuntimeInformation.FrameworkDescription);
			Console.WriteLine("  System:       " + RuntimeInformation.OSDescription);
			Console.WriteLine("  Architecture: " + RuntimeInformation.ProcessArchitecture);
			Console.WriteLine("  Application:  " + System.AppContext.BaseDirectory);
			Console.WriteLine("  User data:    " + ApplicationDataStorage.ApplicationUserDataPath);
		}

		private static void ReportFatal(Exception exception)
		{
			if (exception == null)
			{
				return;
			}

			string report = $"{DateTime.Now:u} MatterControl stopped because of an error:{Environment.NewLine}{exception}{Environment.NewLine}";
			Console.Error.WriteLine(report);

			try
			{
				// Started from Finder there is no terminal to read, so keep a copy on disk
				string logPath = Path.Combine(ApplicationDataStorage.ApplicationUserDataPath, "crash.log");
				File.AppendAllText(logPath, report + Environment.NewLine);
				Console.Error.WriteLine("A copy of this report was written to " + logPath);
			}
			catch (Exception)
			{
			}
		}
	}
}
