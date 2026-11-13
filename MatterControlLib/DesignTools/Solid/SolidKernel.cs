using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>
	/// EBMC Edition: whether the exact-solid kernel (libebmc_occt, Open CASCADE) can be used in this process. Loading
	/// is tried once, on first use; nothing here throws. App code asks SolidModeling.IsEnabled, which checks the OEM
	/// switch first so a stock build never loads the library. When the library is missing or too old every solid feature
	/// is hidden, and solid objects in opened files keep the mesh that was saved with them.
	/// </summary>
	public static class SolidKernel
	{
		/// <summary>Oldest shim version with everything the app calls (edge geometry, transform, compound).</summary>
		public static readonly Version RequiredVersion = new Version(0, 2);

		private static readonly object Gate = new object();

		// written once under Gate, then only read; volatile so a reader on another thread sees the finished values
		private static volatile bool probed;

		private static volatile bool available;

		private static volatile string version = "";

		private static volatile string unavailableReason = "";

		/// <summary>True only if the native library loads and reports a compatible version.</summary>
		public static bool IsAvailable
		{
			get
			{
				Probe();
				return available;
			}
		}

		/// <summary>The library's version string, e.g. "ebmc_occt 0.2; OCCT 8.0.1", or "" when not available.</summary>
		public static string Version
		{
			get
			{
				Probe();
				return version;
			}
		}

		/// <summary>Why the kernel is not available ("" when it is).</summary>
		public static string UnavailableReason
		{
			get
			{
				Probe();
				return unavailableReason;
			}
		}

		/// <summary>Parses "ebmc_occt 0.2; OCCT 8.0.1" into 0.2; null when the text has no shim version.</summary>
		public static Version ParseShimVersion(string versionText)
		{
			if (string.IsNullOrEmpty(versionText))
			{
				return null;
			}

			var match = Regex.Match(versionText, @"ebmc_occt\s+(\d+)\.(\d+)");
			if (!match.Success)
			{
				return null;
			}

			return new Version(
				int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
				int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
		}

		/// <summary>Same major version as <see cref="RequiredVersion"/> (a new major may change the ABI) and at least its minor.</summary>
		public static bool IsCompatible(Version shimVersion)
		{
			return shimVersion != null
				&& shimVersion.Major == RequiredVersion.Major
				&& shimVersion.Minor >= RequiredVersion.Minor;
		}

		private static void Probe()
		{
			if (probed)
			{
				return;
			}

			lock (Gate)
			{
				if (probed)
				{
					return;
				}

				try
				{
					version = EbmcOcct.Version;
					var shimVersion = ParseShimVersion(version);
					if (shimVersion == null)
					{
						unavailableReason = "the solid modelling library reported an unknown version '" + version + "'";
					}
					else if (!IsCompatible(shimVersion))
					{
						unavailableReason = "the solid modelling library is version " + shimVersion + "; " + RequiredVersion.Major + ".x with x >= " + RequiredVersion.Minor + " is needed";
					}
					else
					{
						available = true;
					}
				}
				catch (Exception ex)
				{
					// DllNotFoundException, EntryPointNotFoundException, BadImageFormatException, ...
					unavailableReason = ex.GetType().Name + ": " + ex.Message;
				}

				if (!available)
				{
					version = "";
				}

				Trace.WriteLine(available
					? "Solid modelling kernel loaded: " + version
					: "Solid modelling is off: " + unavailableReason);

				probed = true;
			}
		}
	}
}
