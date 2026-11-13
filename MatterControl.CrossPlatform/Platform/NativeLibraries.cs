using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// Finds the native libraries the application needs: GLFW (window and input) and SQLite (local database), and the
	/// optional solid modelling kernel (libebmc_occt).
	/// </summary>
	/// <remarks>
	/// The bindings name these libraries differently depending on the machine they were compiled on
	/// ("glfw3", "libglfw.3", "glfw", "sqlite3"), and the files themselves are named differently on each
	/// operating system. Resolving them here means the same build output runs everywhere and a missing
	/// library produces a message that says what to install.
	/// </remarks>
	public static class NativeLibraries
	{
		private static readonly Dictionary<string, IntPtr> Loaded = new Dictionary<string, IntPtr>();

		private static bool registered;

		/// <summary>
		/// Gets the path GLFW was loaded from, for diagnostics
		/// </summary>
		public static string GlfwPath { get; private set; }

		/// <summary>
		/// Gets the path SQLite was loaded from, for diagnostics
		/// </summary>
		public static string SqlitePath { get; private set; }

		/// <summary>
		/// Gets the path the solid modelling kernel (libebmc_occt) was loaded from, for diagnostics; null if it was not
		/// </summary>
		public static string SolidKernelPath { get; private set; }

		/// <summary>
		/// Must be called before anything touches GLFW or the database
		/// </summary>
		public static void Register()
		{
			if (registered)
			{
				return;
			}

			registered = true;

			// typeof() does not run the static constructor, so GLFW is not initialized by this line
			NativeLibrary.SetDllImportResolver(typeof(GLFW.Glfw).Assembly, Resolve);

			// The SQLite bindings are compiled into this assembly
			NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);

			// EBMC: the solid modelling kernel's bindings are in MatterControlLib (DesignTools/Solid/EbmcOcct.cs)
			NativeLibrary.SetDllImportResolver(typeof(MatterHackers.MatterControl.DesignTools.Solid.SolidKernel).Assembly, Resolve);
		}

		private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
		{
			lock (Loaded)
			{
				if (Loaded.TryGetValue(libraryName, out IntPtr cached))
				{
					return cached;
				}

				IntPtr handle = IntPtr.Zero;

				if (IsGlfw(libraryName))
				{
					handle = LoadFirst(GlfwCandidates(), out string path);
					GlfwPath = path;

					if (handle == IntPtr.Zero)
					{
						throw new DllNotFoundException(GlfwHelp());
					}
				}
				else if (libraryName == "sqlite3")
				{
					handle = LoadFirst(SqliteCandidates(), out string path);
					SqlitePath = path;

					if (handle == IntPtr.Zero)
					{
						throw new DllNotFoundException(
							Program.AppName + " could not load the SQLite library (libsqlite3). "
							+ "It is part of macOS; on Linux install it with: sudo apt install libsqlite3-0");
					}
				}

				else if (libraryName == "ebmc_occt")
				{
					// Optional: when it is missing the app runs without solid modelling (SolidKernel reports why)
					handle = LoadFirst(SolidKernelCandidates(), out string path);
					SolidKernelPath = path;
				}

				if (handle != IntPtr.Zero)
				{
					Loaded[libraryName] = handle;
				}

				// IntPtr.Zero falls back to the runtime's normal search
				return handle;
			}
		}

		private static bool IsGlfw(string libraryName)
		{
			return libraryName == "glfw"
				|| libraryName == "glfw3"
				|| libraryName == "libglfw.3"
				|| libraryName == "libglfw";
		}

		private static IntPtr LoadFirst(IEnumerable<string> candidates, out string loadedFrom)
		{
			foreach (var candidate in candidates)
			{
				// Note: no File.Exists test. On current macOS the system libraries are not files on disk,
				// they live in the dyld shared cache and can only be found by asking the loader.
				if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
				{
					loadedFrom = candidate;
					return handle;
				}
			}

			loadedFrom = null;
			return IntPtr.Zero;
		}

		private static string RuntimeFolder
		{
			get
			{
				string os = OperatingSystem.IsMacOS() ? "osx"
					: OperatingSystem.IsWindows() ? "win"
					: "linux";

				string architecture = RuntimeInformation.ProcessArchitecture switch
				{
					Architecture.Arm64 => "arm64",
					Architecture.X64 => "x64",
					Architecture.X86 => "x86",
					Architecture.Arm => "arm",
					_ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
				};

				return $"{os}-{architecture}";
			}
		}

		private static IEnumerable<string> GlfwCandidates()
		{
			string appDirectory = System.AppContext.BaseDirectory;

			// An explicit override always wins
			string overridePath = Environment.GetEnvironmentVariable("MATTERCONTROL_GLFW");
			if (!string.IsNullOrEmpty(overridePath))
			{
				yield return overridePath;
			}

			string fileName = OperatingSystem.IsMacOS() ? "libglfw.3.dylib"
				: OperatingSystem.IsWindows() ? "glfw3.dll"
				: "libglfw.so.3";

			// The copy this project ships for exactly this operating system and processor
			// (from the OpenTK.redist.glfw package, see the project file)
			yield return Path.Combine(appDirectory, "native", RuntimeFolder, fileName);
			yield return Path.Combine(appDirectory, "runtimes", RuntimeFolder, "native", fileName);

			// Next to the application. On a Mac this can be the Intel-only library that
			// agg-sharp bundles; the loader refuses it on Apple silicon and the search goes on.
			yield return Path.Combine(appDirectory, fileName);

			if (OperatingSystem.IsMacOS())
			{
				// Homebrew on Apple Silicon, Homebrew on Intel, MacPorts
				yield return "/opt/homebrew/lib/libglfw.3.dylib";
				yield return "/usr/local/lib/libglfw.3.dylib";
				yield return "/opt/local/lib/libglfw.3.dylib";
			}

			// Whatever the system loader can find on its own
			yield return fileName;

			if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
			{
				yield return "libglfw.so";
			}
		}

		/// <summary>True in DEBUG builds, or when the app is not running from inside a macOS .app bundle (dotnet run).</summary>
		private static bool AllowDeveloperOverrides
		{
			get
			{
#if DEBUG
				return true;
#else
				return !System.AppContext.BaseDirectory.Replace('\\', '/').Contains(".app/Contents/", StringComparison.Ordinal);
#endif
			}
		}

		private static IEnumerable<string> SolidKernelCandidates()
		{
			// A developer override, never honoured by a packaged app: whoever can set the user's environment could
			// otherwise load their own native code into it
			string overridePath = Environment.GetEnvironmentVariable("EBMC_OCCT_LIBRARY");
			if (!string.IsNullOrEmpty(overridePath)
				&& AllowDeveloperOverrides)
			{
				yield return overridePath;
			}

			string fileName = OperatingSystem.IsMacOS() ? "libebmc_occt.dylib"
				: OperatingSystem.IsWindows() ? "ebmc_occt.dll"
				: "libebmc_occt.so";

			string appDirectory = System.AppContext.BaseDirectory;

			// The copy this project ships for exactly this operating system and processor (see the project file)
			yield return Path.Combine(appDirectory, "native", RuntimeFolder, fileName);

			// Next to the application (a user who rebuilt the library with native/occt/build.sh may put it here)
			yield return Path.Combine(appDirectory, fileName);
		}

		private static IEnumerable<string> SqliteCandidates()
		{
			string overridePath = Environment.GetEnvironmentVariable("MATTERCONTROL_SQLITE");
			if (!string.IsNullOrEmpty(overridePath))
			{
				yield return overridePath;
			}

			if (OperatingSystem.IsMacOS())
			{
				// Ships with macOS
				yield return "/usr/lib/libsqlite3.dylib";
				yield return "libsqlite3.dylib";
				yield return "/opt/homebrew/opt/sqlite/lib/libsqlite3.dylib";
				yield return "/usr/local/opt/sqlite/lib/libsqlite3.dylib";
			}
			else if (OperatingSystem.IsWindows())
			{
				yield return Path.Combine(System.AppContext.BaseDirectory, "sqlite3.dll");
				yield return "sqlite3.dll";
			}
			else
			{
				yield return "libsqlite3.so.0";
				yield return "libsqlite3.so";
			}
		}

		private static string GlfwHelp()
		{
			string message = Program.AppName + " could not load the GLFW library, which it needs to open its window.";

			if (OperatingSystem.IsMacOS())
			{
				message += $" A copy for {RuntimeFolder} should be in the 'native/{RuntimeFolder}' folder next to the application."
					+ " If it is missing, install GLFW with Homebrew (brew install glfw) or set MATTERCONTROL_GLFW to the full path of libglfw.3.dylib.";
			}
			else if (!OperatingSystem.IsWindows())
			{
				message += " Install it with your package manager, for example: sudo apt install libglfw3";
			}

			return message;
		}
	}
}
