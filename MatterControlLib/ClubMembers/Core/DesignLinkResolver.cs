using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MatterHackers.MatterControl.ClubMembers
{
	public enum LinkResolution
	{
		/// <summary>The file is where it was linked. <see cref="LinkResolveResult.NewSha256"/> is set when its
		/// contents changed (normal for a design still being worked on): store it silently.</summary>
		Found,

		/// <summary>The file had moved; exactly one file with the same name and the same contents was found.
		/// Relink to <see cref="LinkResolveResult.Path"/> without asking.</summary>
		Relinked,

		/// <summary>Files with the same name were found, but not one with exactly the same contents: ask
		/// "Use this file?" for one of <see cref="LinkResolveResult.Candidates"/>.</summary>
		AskUseThis,

		/// <summary>Nothing found: offer "Find…" (a file dialog).</summary>
		NotFound,
	}

	public sealed class LinkResolveResult
	{
		public LinkResolution Resolution { get; set; }

		/// <summary>The file to open, for <see cref="LinkResolution.Found"/> and <see cref="LinkResolution.Relinked"/>.</summary>
		public string Path { get; set; }

		/// <summary>The new fingerprint when it differs from the stored one; otherwise null.</summary>
		public string NewSha256 { get; set; }

		public IReadOnlyList<string> Candidates { get; set; } = new List<string>();
	}

	/// <summary>
	/// EBMC Edition: finds a project's linked design file again. Pure: the file system is passed in as functions, so
	/// it is unit tested with a fake one. Print files are never linked or opened, so a link cannot lead toward a
	/// printer.
	/// </summary>
	public static class DesignLinkResolver
	{
		/// <summary>The design file types a project may link to. Print files (.gcode) are not among them.</summary>
		public static readonly IReadOnlyList<string> DesignExtensions = new[] { ".mcx", ".stl", ".3mf", ".obj", ".amf", ".step", ".stp", ".svg" };

		public static bool IsLinkableDesign(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return false;
			}

			var extension = System.IO.Path.GetExtension(path);
			return DesignExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Where the linked file is now.
		/// </summary>
		/// <param name="link">The stored link.</param>
		/// <param name="searchFolders">Folders to look in, without recursing, after the file's old folder: the
		/// library folders, Desktop, Documents and Downloads.</param>
		/// <param name="exists">Whether a file (not a folder) exists. Only such files are ever candidates.</param>
		/// <param name="listDir">The files directly in a folder (full paths).</param>
		/// <param name="sha256">A file's SHA-256 as lower-case hex.</param>
		public static LinkResolveResult Resolve(
			DesignLink link,
			IEnumerable<string> searchFolders,
			Func<string, bool> exists,
			Func<string, IEnumerable<string>> listDir,
			Func<string, string> sha256)
		{
			if (link == null
				|| !IsLinkableDesign(link.Path)
				|| exists == null
				|| listDir == null
				|| sha256 == null)
			{
				return new LinkResolveResult { Resolution = LinkResolution.NotFound };
			}

			if (SafeExists(exists, link.Path))
			{
				var hash = SafeHash(sha256, link.Path);
				return new LinkResolveResult
				{
					Resolution = LinkResolution.Found,
					Path = link.Path,
					NewSha256 = hash != null && !SameHash(hash, link.Sha256) ? hash : null,
				};
			}

			var fileName = string.IsNullOrEmpty(link.FileName) ? System.IO.Path.GetFileName(link.Path) : link.FileName;
			if (!IsLinkableDesign(fileName))
			{
				return new LinkResolveResult { Resolution = LinkResolution.NotFound };
			}

			var folders = new List<string>();
			var oldFolder = SafeDirectoryName(link.Path);
			foreach (var folder in new[] { oldFolder }.Concat(searchFolders ?? Enumerable.Empty<string>()))
			{
				if (!string.IsNullOrWhiteSpace(folder)
					&& !folders.Any(f => string.Equals(Trim(f), Trim(folder), StringComparison.Ordinal)))
				{
					folders.Add(folder);
				}
			}

			var candidates = new List<string>();
			foreach (var folder in folders)
			{
				foreach (var file in SafeList(listDir, folder))
				{
					// a case-insensitive name match (macOS and Windows), but files whose names differ only in case
					// stay separate candidates; folders are never candidates
					if (string.Equals(SafeFileName(file), fileName, StringComparison.OrdinalIgnoreCase)
						&& !candidates.Contains(file, StringComparer.Ordinal)
						&& SafeExists(exists, file))
					{
						candidates.Add(file);
					}
				}
			}

			if (candidates.Count == 0)
			{
				return new LinkResolveResult { Resolution = LinkResolution.NotFound };
			}

			var sameContents = string.IsNullOrEmpty(link.Sha256)
				? new List<string>()
				: candidates.Where(c => SameHash(SafeHash(sha256, c), link.Sha256)).ToList();

			if (sameContents.Count == 1)
			{
				return new LinkResolveResult
				{
					Resolution = LinkResolution.Relinked,
					Path = sameContents[0],
					Candidates = sameContents,
				};
			}

			// several identical copies, or same name with other contents: the person chooses
			return new LinkResolveResult
			{
				Resolution = LinkResolution.AskUseThis,
				Candidates = sameContents.Count > 1 ? sameContents : candidates,
			};
		}

		private static bool SameHash(string a, string b)
		{
			return !string.IsNullOrEmpty(a)
				&& !string.IsNullOrEmpty(b)
				&& string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}

		private static string Trim(string folder) => folder.TrimEnd('/', '\\');

		private static string SafeFileName(string path)
		{
			try
			{
				return System.IO.Path.GetFileName(path);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		private static string SafeDirectoryName(string path)
		{
			try
			{
				return System.IO.Path.GetDirectoryName(path);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		private static bool SafeExists(Func<string, bool> exists, string path)
		{
			try
			{
				return exists(path);
			}
			catch (Exception)
			{
				// whatever the injected function throws counts as "not found"
				return false;
			}
		}

		private static string SafeHash(Func<string, string> sha256, string path)
		{
			try
			{
				return sha256(path);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static List<string> SafeList(Func<string, IEnumerable<string>> listDir, string folder)
		{
			try
			{
				return (listDir(folder) ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrEmpty(f)).ToList();
			}
			catch (Exception)
			{
				return new List<string>();
			}
		}
	}
}
