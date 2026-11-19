using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MatterHackers.MatterControl.ClubMembers;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Members & Projects: finding a project's linked design file again, with a fake file system.
	/// </summary>
	[TestFixture]
	public class DesignLinkTests
	{
		private const string OldFolder = "/club/designs";
		private const string Desktop = "/home/maker/Desktop";
		private const string Documents = "/home/maker/Documents";

		/// <summary>path -> SHA-256</summary>
		private Dictionary<string, string> files;

		private List<string> listed;

		/// <summary>Folder entries listed by the fake but that are folders, not files.</summary>
		private List<string> folders;

		private Func<string, bool> existsOverride;
		private Func<string, string> hashOverride;

		[SetUp]
		public void SetUp()
		{
			files = new Dictionary<string, string>(StringComparer.Ordinal);
			listed = new List<string>();
			folders = new List<string>();
			existsOverride = null;
			hashOverride = null;
		}

		private static DesignLink Link(string path = OldFolder + "/arm.mcx", string sha = "aaa")
		{
			return new DesignLink { Path = path, FileName = Path.GetFileName(path), SizeBytes = 100, Sha256 = sha };
		}

		private LinkResolveResult Resolve(DesignLink link, params string[] folders)
		{
			return DesignLinkResolver.Resolve(
				link,
				folders,
				path => existsOverride != null ? existsOverride(path) : files.ContainsKey(path),
				folder =>
				{
					listed.Add(folder);
					if (folder == "/missing")
					{
						throw new DirectoryNotFoundException();
					}

					// direct children only
					return files.Keys.Concat(folders).Where(f => Path.GetDirectoryName(f) == folder).ToList();
				},
				path => hashOverride != null ? hashOverride(path) : files.TryGetValue(path, out var sha) ? sha : throw new FileNotFoundException());
		}

		[Test]
		public void AFileStillInPlaceIsFound()
		{
			files[OldFolder + "/arm.mcx"] = "aaa";
			var result = Resolve(Link(), Desktop);

			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.Found));
			Assert.That(result.Path, Is.EqualTo(OldFolder + "/arm.mcx"));
			Assert.That(result.NewSha256, Is.Null);
			Assert.That(listed, Is.Empty, "no search when the file is in place");
		}

		[Test]
		public void AChangedFileInPlaceIsFoundWithItsNewFingerprint()
		{
			files[OldFolder + "/arm.mcx"] = "bbb";
			var result = Resolve(Link(), Desktop);

			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.Found));
			Assert.That(result.NewSha256, Is.EqualTo("bbb"));
		}

		[Test]
		public void AMovedFileWithTheSameContentsIsRelinked()
		{
			files[Desktop + "/arm.mcx"] = "aaa";
			files[Documents + "/other.mcx"] = "aaa";
			var result = Resolve(Link(), Desktop, Documents);

			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.Relinked));
			Assert.That(result.Path, Is.EqualTo(Desktop + "/arm.mcx"));
		}

		[Test]
		public void OneIdenticalCopyWinsOverSameNamedFilesWithOtherContents()
		{
			files[Desktop + "/arm.mcx"] = "zzz";
			files[Documents + "/arm.mcx"] = "aaa";
			var result = Resolve(Link(), Desktop, Documents);

			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.Relinked));
			Assert.That(result.Path, Is.EqualTo(Documents + "/arm.mcx"));
		}

		[Test]
		public void TheSameNameWithOtherContentsAsks()
		{
			files[Desktop + "/arm.mcx"] = "zzz";
			var result = Resolve(Link(), Desktop);

			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.AskUseThis));
			Assert.That(result.Candidates, Is.EqualTo(new[] { Desktop + "/arm.mcx" }));
			Assert.That(result.Path, Is.Null);
		}

		[Test]
		public void TwoIdenticalCopiesAsk()
		{
			files[Desktop + "/arm.mcx"] = "aaa";
			files[Documents + "/arm.mcx"] = "aaa";
			var result = Resolve(Link(), Desktop, Documents);

			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.AskUseThis));
			Assert.That(result.Candidates, Is.EquivalentTo(new[] { Desktop + "/arm.mcx", Documents + "/arm.mcx" }));
		}

		[Test]
		public void TheOldFolderIsSearchedFirstAndFoldersOnlyOnce()
		{
			files[OldFolder + "/renamed-then-back/arm.mcx"] = "aaa";
			var result = Resolve(Link(), Desktop, OldFolder, Desktop + "/");

			Assert.That(listed, Is.EqualTo(new[] { OldFolder, Desktop }));
			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.NotFound), "sub-folders are not searched");
		}

		[Test]
		public void NothingFoundAndUnreadableFoldersAreSkipped()
		{
			files[Desktop + "/bracket.mcx"] = "aaa";
			var result = Resolve(Link(), "/missing", Desktop, null, "");

			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.NotFound));
			Assert.That(result.Candidates, Is.Empty);
		}

		[Test]
		public void PrintFilesAreNeverLinkedOrFound()
		{
			Assert.That(DesignLinkResolver.IsLinkableDesign("/club/arm.gcode"), Is.False);
			Assert.That(DesignLinkResolver.IsLinkableDesign("/club/arm.GCODE"), Is.False);
			Assert.That(DesignLinkResolver.IsLinkableDesign("/club/arm"), Is.False);
			Assert.That(DesignLinkResolver.IsLinkableDesign(null), Is.False);
			foreach (var extension in new[] { ".mcx", ".stl", ".3mf", ".obj", ".amf", ".step", ".stp", ".svg", ".STL" })
			{
				Assert.That(DesignLinkResolver.IsLinkableDesign("/club/arm" + extension), Is.True, extension);
			}

			files[OldFolder + "/arm.gcode"] = "aaa";
			Assert.That(Resolve(Link(OldFolder + "/arm.gcode")).Resolution, Is.EqualTo(LinkResolution.NotFound));

			// a link whose stored file name is a print file is not searched for either
			var odd = Link();
			odd.FileName = "arm.gcode";
			files[Desktop + "/arm.gcode"] = "aaa";
			Assert.That(Resolve(odd, Desktop).Resolution, Is.EqualTo(LinkResolution.NotFound));
		}

		[Test]
		public void ALinkWithoutAFingerprintAsksInsteadOfRelinking()
		{
			files[Desktop + "/arm.mcx"] = "aaa";
			Assert.That(Resolve(Link(sha: ""), Desktop).Resolution, Is.EqualTo(LinkResolution.AskUseThis));
		}

		[Test]
		public void AnyExceptionFromTheFileSystemCountsAsNotFound()
		{
			files[Desktop + "/arm.mcx"] = "aaa";
			existsOverride = path => throw new InvalidOperationException("injected");
			LinkResolveResult result = null;
			Assert.DoesNotThrow(() => result = Resolve(Link(), Desktop));
			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.NotFound));

			existsOverride = null;
			hashOverride = path => throw new NotSupportedException("injected");
			Assert.DoesNotThrow(() => result = Resolve(Link(), Desktop));
			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.AskUseThis), "a file whose contents cannot be read is offered, not relinked");

			var throwing = DesignLinkResolver.Resolve(Link(), new[] { Desktop }, p => false, d => throw new InvalidOperationException("injected"), p => "aaa");
			Assert.That(throwing.Resolution, Is.EqualTo(LinkResolution.NotFound));
		}

		[Test]
		public void FoldersAreNeverCandidates()
		{
			folders.Add(Desktop + "/arm.mcx");
			Assert.That(Resolve(Link(), Desktop).Resolution, Is.EqualTo(LinkResolution.NotFound));
		}

		[Test]
		public void NamesThatDifferOnlyInCaseStaySeparate()
		{
			files[Desktop + "/arm.mcx"] = "zzz";
			files[Desktop + "/ARM.mcx"] = "yyy";
			files["/home/maker/desktop/arm.mcx"] = "xxx";
			var result = Resolve(Link(), Desktop, "/home/maker/desktop");

			Assert.That(result.Resolution, Is.EqualTo(LinkResolution.AskUseThis));
			Assert.That(result.Candidates, Is.EquivalentTo(new[] { Desktop + "/arm.mcx", Desktop + "/ARM.mcx", "/home/maker/desktop/arm.mcx" }));
		}
	}
}
