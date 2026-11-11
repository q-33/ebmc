using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// Native open / save / folder dialogs without a UI toolkit dependency.
	///
	/// macOS: the system's own panels, driven through /usr/bin/osascript
	///        ("choose file", "choose file name", "choose folder").
	/// Linux: zenity, which ships with most desktops.
	///
	/// The dialog runs in a helper process, so it is awaited on a worker thread
	/// while the application window keeps drawing; input to the window is ignored
	/// until the dialog is answered so that it still behaves like a modal dialog.
	/// </summary>
	public class DesktopFileDialogProvider : IFileDialogProvider
	{
		private static readonly bool IsMac = OperatingSystem.IsMacOS();

		public string LastDirectoryUsed { get; private set; } = "";

		// Only the old Xamarin.Mac build needed to resolve file reference URLs
		public string ResolveFilePath(string path) => path;

		public bool OpenFileDialog(OpenFileDialogParams openParams, Action<OpenFileDialogParams> callback)
		{
			openParams.FileName = "";
			openParams.FileNames = null;

			var filters = ParseFilter(openParams.Filter);
			string directory = ExistingDirectory(openParams.InitialDirectory, this.LastDirectoryUsed);
			string title = openParams.Title;
			bool multiSelect = openParams.MultiSelect;

			RunDialog(
				() => IsMac
					? RunOsaScript(MacOpenScript(title, directory, filters, multiSelect))
					: RunZenity(ZenityOpenArguments(title, directory, filters, multiSelect)),
				(paths) =>
				{
					if (paths.Length > 0)
					{
						openParams.FileNames = paths;
						openParams.FileName = paths[0];
						this.LastDirectoryUsed = Path.GetDirectoryName(paths[0]) ?? "";
					}

					callback?.Invoke(openParams);
				});

			return true;
		}

		public bool SaveFileDialog(SaveFileDialogParams saveParams, Action<SaveFileDialogParams> callback)
		{
			var filters = ParseFilter(saveParams.Filter);
			string directory = ExistingDirectory(saveParams.InitialDirectory, this.LastDirectoryUsed);
			string title = saveParams.Title;
			string defaultExtension = DefaultExtension(filters, saveParams.FilterIndex);

			string suggestedName = Path.GetFileName(saveParams.FileName ?? "");
			if (!string.IsNullOrEmpty(suggestedName)
				&& !Path.HasExtension(suggestedName)
				&& defaultExtension != null)
			{
				suggestedName += "." + defaultExtension;
			}

			RunDialog(
				() => IsMac
					? RunOsaScript(MacSaveScript(title, directory, suggestedName))
					: RunZenity(ZenitySaveArguments(title, directory, suggestedName, filters)),
				(paths) =>
				{
					if (paths.Length > 0)
					{
						string fileName = paths[0];
						if (!Path.HasExtension(fileName) && defaultExtension != null)
						{
							fileName += "." + defaultExtension;
						}

						saveParams.FileName = fileName;
						this.LastDirectoryUsed = Path.GetDirectoryName(fileName) ?? "";
					}
					else
					{
						saveParams.FileName = null;
					}

					callback?.Invoke(saveParams);
				});

			return true;
		}

		public bool SelectFolderDialog(SelectFolderDialogParams folderParams, Action<SelectFolderDialogParams> callback)
		{
			string directory = "";
			if (folderParams.RootFolder == SelectFolderDialogParams.RootFolderTypes.Specify)
			{
				// Walk up to the first folder that exists
				string candidate = folderParams.FolderPath;
				while (!string.IsNullOrEmpty(candidate) && !Directory.Exists(candidate))
				{
					candidate = Path.GetDirectoryName(candidate);
				}

				directory = candidate ?? "";
			}

			string prompt = string.IsNullOrEmpty(folderParams.Title) ? folderParams.Description : folderParams.Title;

			RunDialog(
				() => IsMac
					? RunOsaScript(MacFolderScript(prompt, directory))
					: RunZenity(ZenityFolderArguments(prompt, directory)),
				(paths) =>
				{
					// Matches the Windows dialog, which reports an empty path when cancelled
					folderParams.FolderPath = paths.Length > 0 ? TrimTrailingSeparator(paths[0]) : "";
					callback?.Invoke(folderParams);
				});

			return true;
		}

		public void ShowFileInFolder(string fileName)
		{
			try
			{
				string fullPath = Path.GetFullPath(fileName);
				var startInfo = new ProcessStartInfo { UseShellExecute = false };

				if (IsMac)
				{
					// Reveals and selects the file in Finder
					startInfo.FileName = "/usr/bin/open";
					startInfo.ArgumentList.Add("-R");
					startInfo.ArgumentList.Add(fullPath);
				}
				else
				{
					startInfo.FileName = "xdg-open";
					startInfo.ArgumentList.Add(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? "/");
				}

				Process.Start(startInfo)?.Dispose();
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("Could not show the file in its folder: " + e.Message);
			}
		}

		private static void RunDialog(Func<string[]> showDialog, Action<string[]> completed)
		{
			GlfwPlatformWindow.InputBlocked = true;

			Task.Run(() =>
			{
				string[] paths;
				try
				{
					paths = showDialog();
				}
				catch (Exception e)
				{
					Console.Error.WriteLine("The file dialog could not be shown: " + e.Message);
					paths = Array.Empty<string>();
				}

				UiThread.RunOnIdle(() =>
				{
					GlfwPlatformWindow.InputBlocked = false;
					completed(paths);
				});
			});
		}

		/// <summary>
		/// Parses a Windows style filter: "Description|*.a;*.b|Other|*.c"
		/// </summary>
		internal static List<(string description, List<string> extensions)> ParseFilter(string filter)
		{
			var result = new List<(string, List<string>)>();
			if (string.IsNullOrWhiteSpace(filter))
			{
				return result;
			}

			string[] parts = filter.Split('|');
			for (int i = 0; i + 1 < parts.Length; i += 2)
			{
				var extensions = parts[i + 1]
					.Split(';', StringSplitOptions.RemoveEmptyEntries)
					.Select(pattern => pattern.Trim())
					.Select(pattern => pattern.StartsWith("*.") ? pattern.Substring(2) : pattern.TrimStart('*', '.'))
					.Where(extension => extension.Length > 0)
					.ToList();

				result.Add((parts[i].Trim(), extensions));
			}

			return result;
		}

		private static bool IsWildcard(string extension) => extension == "*" || extension.Contains('*') || extension.Contains('?');

		/// <summary>
		/// The extension list a dialog should allow, or null when everything is allowed.
		/// </summary>
		private static List<string> AllowedExtensions(List<(string description, List<string> extensions)> filters)
		{
			var all = filters.SelectMany(f => f.extensions).ToList();
			if (all.Count == 0 || all.Any(IsWildcard))
			{
				return null;
			}

			return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}

		private static string DefaultExtension(List<(string description, List<string> extensions)> filters, int filterIndex)
		{
			if (filters.Count == 0)
			{
				return null;
			}

			// FilterIndex is 1 based, with 0 meaning "not set"
			int index = Math.Clamp(filterIndex - 1, 0, filters.Count - 1);
			return filters[index].extensions.FirstOrDefault(extension => !IsWildcard(extension));
		}

		private static string ExistingDirectory(params string[] candidates)
		{
			foreach (string candidate in candidates)
			{
				if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
				{
					return candidate;
				}
			}

			return "";
		}

		private static string TrimTrailingSeparator(string path)
		{
			return path.Length > 1 ? path.TrimEnd('/') : path;
		}

		private static string[] SplitLines(string output)
		{
			return output
				.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(line => line.Trim())
				.Where(line => line.Length > 0)
				.ToArray();
		}

		#region macOS

		private static string AppleScriptString(string text)
		{
			var builder = new StringBuilder("\"");
			foreach (char character in text ?? "")
			{
				switch (character)
				{
					case '\\':
						builder.Append("\\\\");
						break;

					case '"':
						builder.Append("\\\"");
						break;

					case '\r':
					case '\n':
						builder.Append(' ');
						break;

					default:
						builder.Append(character);
						break;
				}
			}

			return builder.Append('"').ToString();
		}

		private static string MacCommonOptions(string prompt, string directory)
		{
			var options = new StringBuilder();
			if (!string.IsNullOrWhiteSpace(prompt))
			{
				options.Append(" with prompt ").Append(AppleScriptString(prompt));
			}

			if (!string.IsNullOrEmpty(directory))
			{
				// The parameter wants a file object, not a path string
				options.Append(" default location (POSIX file ").Append(AppleScriptString(directory)).Append(')');
			}

			return options.ToString();
		}

		internal static List<string> MacOpenScript(string title, string directory, List<(string description, List<string> extensions)> filters, bool multiSelect)
		{
			var command = new StringBuilder("set chosen to choose file");
			command.Append(MacCommonOptions(title, directory));

			var extensions = AllowedExtensions(filters);
			if (extensions != null)
			{
				command.Append(" of type {").Append(string.Join(", ", extensions.Select(AppleScriptString))).Append('}');
			}

			if (multiSelect)
			{
				command.Append(" multiple selections allowed true");
			}

			return new List<string>
			{
				"try",
				command.ToString(),
				// One file comes back as a single alias, several as a list
				"if class of chosen is not list then set chosen to {chosen}",
				"set output to \"\"",
				"repeat with i from 1 to (count of chosen)",
				"set output to output & (POSIX path of (item i of chosen)) & linefeed",
				"end repeat",
				"return output",
				// -128 is "user cancelled"
				"on error number -128",
				"return \"\"",
				"end try",
			};
		}

		internal static List<string> MacSaveScript(string title, string directory, string suggestedName)
		{
			var command = new StringBuilder("return POSIX path of (choose file name");
			command.Append(MacCommonOptions(title, directory));
			if (!string.IsNullOrEmpty(suggestedName))
			{
				command.Append(" default name ").Append(AppleScriptString(suggestedName));
			}

			command.Append(')');

			return new List<string>
			{
				"try",
				command.ToString(),
				"on error number -128",
				"return \"\"",
				"end try",
			};
		}

		internal static List<string> MacFolderScript(string prompt, string directory)
		{
			return new List<string>
			{
				"try",
				"return POSIX path of (choose folder" + MacCommonOptions(prompt, directory) + ")",
				"on error number -128",
				"return \"\"",
				"end try",
			};
		}

		private static string[] RunOsaScript(List<string> scriptLines)
		{
			var startInfo = new ProcessStartInfo("/usr/bin/osascript");
			foreach (string line in scriptLines)
			{
				startInfo.ArgumentList.Add("-e");
				startInfo.ArgumentList.Add(line);
			}

			return RunAndReadLines(startInfo, cancelExitCode: -1);
		}

		#endregion

		#region Linux

		private static List<string> ZenityCommon(string title, List<(string description, List<string> extensions)> filters)
		{
			var arguments = new List<string> { "--file-selection" };
			if (!string.IsNullOrWhiteSpace(title))
			{
				arguments.Add("--title=" + title);
			}

			if (filters != null)
			{
				foreach (var (description, extensions) in filters)
				{
					if (extensions.Count == 0)
					{
						continue;
					}

					// Offer both cases, file names on Linux are case sensitive
					var patterns = extensions
						.SelectMany(e => IsWildcard(e) ? new[] { "*" } : new[] { "*." + e.ToLowerInvariant(), "*." + e.ToUpperInvariant() })
						.Distinct();

					arguments.Add($"--file-filter={description} | {string.Join(" ", patterns)}");
				}
			}

			return arguments;
		}

		private static List<string> ZenityOpenArguments(string title, string directory, List<(string description, List<string> extensions)> filters, bool multiSelect)
		{
			var arguments = ZenityCommon(title, filters);
			if (multiSelect)
			{
				arguments.Add("--multiple");
				arguments.Add("--separator=\n");
			}

			if (!string.IsNullOrEmpty(directory))
			{
				arguments.Add("--filename=" + directory.TrimEnd('/') + "/");
			}

			return arguments;
		}

		private static List<string> ZenitySaveArguments(string title, string directory, string suggestedName, List<(string description, List<string> extensions)> filters)
		{
			var arguments = ZenityCommon(title, filters);
			arguments.Add("--save");
			arguments.Add("--confirm-overwrite");

			string start = string.IsNullOrEmpty(directory) ? "" : directory.TrimEnd('/') + "/";
			if (!string.IsNullOrEmpty(start + suggestedName))
			{
				arguments.Add("--filename=" + start + suggestedName);
			}

			return arguments;
		}

		private static List<string> ZenityFolderArguments(string title, string directory)
		{
			var arguments = ZenityCommon(title, null);
			arguments.Add("--directory");
			if (!string.IsNullOrEmpty(directory))
			{
				arguments.Add("--filename=" + directory.TrimEnd('/') + "/");
			}

			return arguments;
		}

		private static string[] RunZenity(List<string> arguments)
		{
			var startInfo = new ProcessStartInfo("zenity");
			foreach (string argument in arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}

			try
			{
				// zenity exits with 1 when the dialog is cancelled
				return RunAndReadLines(startInfo, cancelExitCode: 1);
			}
			catch (System.ComponentModel.Win32Exception)
			{
				Console.Error.WriteLine("File dialogs need the 'zenity' program. Install it with your package manager, for example: sudo apt install zenity");
				return Array.Empty<string>();
			}
		}

		#endregion

		private static string[] RunAndReadLines(ProcessStartInfo startInfo, int cancelExitCode)
		{
			startInfo.UseShellExecute = false;
			startInfo.RedirectStandardOutput = true;
			startInfo.RedirectStandardError = true;
			startInfo.StandardOutputEncoding = Encoding.UTF8;
			startInfo.StandardErrorEncoding = Encoding.UTF8;

			using (var process = Process.Start(startInfo))
			{
				if (process == null)
				{
					return Array.Empty<string>();
				}

				// Read both streams before waiting so a full pipe cannot stall the helper
				var errorTask = process.StandardError.ReadToEndAsync();
				string output = process.StandardOutput.ReadToEnd();
				process.WaitForExit();
				string error = errorTask.Result;

				if (process.ExitCode != 0 && process.ExitCode != cancelExitCode)
				{
					Console.Error.WriteLine($"{startInfo.FileName} exited with code {process.ExitCode}: {error.Trim()}");
					return Array.Empty<string>();
				}

				return SplitLines(output);
			}
		}
	}
}
