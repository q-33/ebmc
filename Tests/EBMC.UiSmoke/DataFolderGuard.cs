using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace EBMC.UiSmoke
{
	/// <summary>
	/// Whether a folder may be used as the smoke test's EBMC_DATA_DIR. Pure apart from reading the file system: the
	/// real data folder is a parameter, so the rules can be tested against a fake one.
	///
	/// Paths are compared as the operating system will resolve them: every existing component is resolved with
	/// realpath(3) (so chains of symbolic links count), ".." is applied to the resolved parent as the kernel does,
	/// and missing trailing components are appended as written. Anything that resolves to, inside, or around the
	/// real folder is refused.
	/// </summary>
	public static class DataFolderGuard
	{
		public const string MarkerName = "ebmc-ui-smoke.marker";

		/// <summary>Null when <paramref name="value"/> may be used; otherwise why not.</summary>
		public static string Check(string value, string realFolder, bool reuse)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "EBMC_DATA_DIR is not set; this test only runs on a throwaway data folder";
			}

			if (!Path.IsPathRooted(value))
			{
				return "EBMC_DATA_DIR must be an absolute path";
			}

			string candidate, real;
			try
			{
				candidate = Resolve(value);
				real = Resolve(realFolder);
			}
			catch (IOException e)
			{
				return "EBMC_DATA_DIR cannot be resolved: " + e.Message;
			}

			if (Same(candidate, real))
			{
				return $"EBMC_DATA_DIR resolves to the real data folder {realFolder}";
			}

			if (Inside(candidate, real))
			{
				return $"EBMC_DATA_DIR resolves inside the real data folder {realFolder} ({candidate})";
			}

			if (Inside(real, candidate))
			{
				return $"EBMC_DATA_DIR contains the real data folder {realFolder} ({candidate})";
			}

			if (File.Exists(candidate))
			{
				return "EBMC_DATA_DIR is a file: " + candidate;
			}

			bool hasEntries = Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any();
			if (reuse)
			{
				return File.Exists(Path.Combine(candidate, MarkerName))
					? null
					: $"this flow reuses a scratch folder a previous smoke run made ({MarkerName} missing): {candidate}";
			}

			return hasEntries ? "EBMC_DATA_DIR is not empty; give a fresh folder: " + candidate : null;
		}

		/// <summary>The path as the system will resolve it (see the class comment). Throws IOException for a dangling link or a loop.</summary>
		public static string Resolve(string path)
		{
			if (!Path.IsPathRooted(path))
			{
				throw new IOException("not an absolute path: " + path);
			}

			string current = "/";
			bool missing = false;
			foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
			{
				if (part == ".")
				{
					continue;
				}

				if (part == "..")
				{
					// the parent of what the previous component resolved to, as the kernel does it
					current = Path.GetDirectoryName(current.TrimEnd('/')) ?? "/";
					if (current == "")
					{
						current = "/";
					}

					continue;
				}

				string next = current == "/" ? "/" + part : current + "/" + part;
				if (missing)
				{
					current = next;
					continue;
				}

				if (Directory.Exists(next) || File.Exists(next))
				{
					// follows every link in the chain; throws for a loop
					current = RealPath(next);
				}
				else if (IsDanglingLink(next))
				{
					throw new IOException("dangling symbolic link: " + next);
				}
				else
				{
					missing = true;
					current = next;
				}
			}

			return current;
		}

		private static bool IsDanglingLink(string path)
		{
			try
			{
				return new FileInfo(path).LinkTarget != null;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool Same(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

		private static bool Inside(string path, string folder)
		{
			string f = folder.TrimEnd('/');
			return path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase) || (f == "" && path != "/");
		}

		[DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
		private static extern IntPtr realpath(byte[] path, IntPtr resolved);

		[DllImport("libc", EntryPoint = "free")]
		private static extern void free(IntPtr pointer);

		private static string RealPath(string path)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(path + "\0");
			IntPtr result = realpath(bytes, IntPtr.Zero);
			if (result == IntPtr.Zero)
			{
				throw new IOException($"cannot resolve {path} (a dangling or looping symbolic link?), errno {Marshal.GetLastWin32Error()}");
			}

			try
			{
				return Marshal.PtrToStringUTF8(result);
			}
			finally
			{
				free(result);
			}
		}

		/// <summary>
		/// The guard against a FAKE real folder made under <paramref name="scratch"/>: every case, expected accept or
		/// refuse. Returns the number of wrong answers. Never looks at the real data folder.
		/// </summary>
		public static int SelfTest(string scratch)
		{
			string root = Path.Combine(scratch, "guard-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
			string home = Path.Combine(root, "home");
			string real = Path.Combine(home, "Library", "Application Support", "MatterControl");
			Directory.CreateDirectory(Path.Combine(real, "Profiles"));
			File.WriteAllText(Path.Combine(real, "MatterControl.db"), "fake");
			Directory.CreateDirectory(Path.Combine(real, "emptysub"));
			string area = Path.Combine(root, "area");
			Directory.CreateDirectory(area);
			Directory.CreateDirectory(Path.Combine(area, "fresh"));
			Directory.CreateDirectory(Path.Combine(area, "full"));
			File.WriteAllText(Path.Combine(area, "full", "x"), "x");
			File.WriteAllText(Path.Combine(area, "afile"), "x");
			Directory.CreateDirectory(Path.Combine(area, "made"));
			File.WriteAllText(Path.Combine(area, "made", MarkerName), "m");
			File.WriteAllText(Path.Combine(real, "emptysub-marker"), "");

			void Link(string name, string target) => File.CreateSymbolicLink(Path.Combine(area, name), target);
			Link("link-to-real", real);
			Link("link-to-empty-inside-real", Path.Combine(real, "emptysub"));
			Link("link-to-parent", Path.GetDirectoryName(real));
			Link("link-chain-1", Path.Combine(area, "link-chain-2"));
			Link("link-chain-2", Path.Combine(area, "link-to-real"));
			Link("link-dangling", Path.Combine(area, "nowhere"));
			Link("link-to-fresh", Path.Combine(area, "fresh"));
			Link("link-to-home", home);
			Directory.CreateDirectory(Path.Combine(real, "made-inside"));
			File.WriteAllText(Path.Combine(real, "made-inside", MarkerName), "m");
			Link("link-to-marker-inside-real", Path.Combine(real, "made-inside"));
			Link("dotdot-trap", Path.Combine(real, "Profiles"));

			var cases = new List<(string name, string value, bool reuse, bool accept)>
			{
				("unset", "", false, false),
				("relative", "scratch/data", false, false),
				("the real folder", real, false, false),
				("the real folder, trailing slash", real + "/", false, false),
				("the real folder, other case", real.Replace("MatterControl", "mattercontrol"), false, false),
				("an existing folder inside it", Path.Combine(real, "Profiles"), false, false),
				("a missing folder inside it", Path.Combine(real, "newsub"), false, false),
				("its parent (contains it)", Path.GetDirectoryName(real), false, false),
				("the fake home (contains it)", home, false, false),
				("/ (contains it)", "/", false, false),
				("lexical .. into it", Path.Combine(area, "..", "home", "Library", "Application Support", "MatterControl", "x"), false, false),
				("symlink to the real folder", Path.Combine(area, "link-to-real"), false, false),
				("symlink to an empty folder inside it", Path.Combine(area, "link-to-empty-inside-real"), false, false),
				("link-to-real/newsub", Path.Combine(area, "link-to-real", "newsub"), false, false),
				("link-to-parent/MatterControl/newsub", Path.Combine(area, "link-to-parent", "MatterControl", "newsub"), false, false),
				("chain of two links to it", Path.Combine(area, "link-chain-1", "newsub"), false, false),
				("link-to-home/... (inside via a link)", Path.Combine(area, "link-to-home", "Library", "Application Support", "MatterControl"), false, false),
				("physical .. through a link (Profiles/../newsub)", Path.Combine(area, "dotdot-trap", "..", "newsub"), false, false),
				("dangling link", Path.Combine(area, "link-dangling"), false, false),
				("a file", Path.Combine(area, "afile"), false, false),
				("a non-empty folder", Path.Combine(area, "full"), false, false),
				("reuse: a symlink to a marked folder inside it", Path.Combine(area, "link-to-marker-inside-real"), true, false),
				("reuse: a folder without the marker", Path.Combine(area, "full"), true, false),
				("a fresh empty folder", Path.Combine(area, "fresh"), false, true),
				("a missing folder in the scratch area", Path.Combine(area, "new", "deeper"), false, true),
				("a symlink to a fresh empty folder in the scratch area", Path.Combine(area, "link-to-fresh"), false, true),
				("lexical .. that stays in the scratch area", Path.Combine(area, "full", "..", "fresh"), false, true),
				("reuse: a folder with the marker", Path.Combine(area, "made"), true, true),
			};

			int wrong = 0;
			Console.WriteLine($"guard self-test, fake real folder: {real}");
			foreach (var c in cases)
			{
				string refusal = Check(c.value, real, c.reuse);
				bool accepted = refusal == null;
				bool ok = accepted == c.accept;
				wrong += ok ? 0 : 1;
				Console.WriteLine($"  {(ok ? "ok   " : "WRONG")} {(accepted ? "accept" : "refuse")}  {c.name}{(refusal == null ? "" : ": " + refusal.Replace(root, "<scratch>"))}");
			}

			// the test folder holds only what this test made; links are removed as links (never followed)
			foreach (var link in Directory.GetFileSystemEntries(area).Where(p => new FileInfo(p).LinkTarget != null))
			{
				File.Delete(link);
			}

			Directory.Delete(root, true);
			Console.WriteLine(wrong == 0 ? $"guard self-test: all {cases.Count} cases right" : $"guard self-test: {wrong} of {cases.Count} WRONG");
			return wrong;
		}
	}
}
