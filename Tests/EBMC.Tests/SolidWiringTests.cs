using System.IO;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Source checks for wiring that cannot run without the app: the solid gate on handles and rows, the bundled-only
	/// kernel lookup, and where the Subtract Hole default is applied.
	/// </summary>
	[TestFixture]
	public class SolidWiringTests
	{
		private static string root;

		[OneTimeSetUp]
		public void FindRoot()
		{
			var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "EBMC.md")))
			{
				directory = directory.Parent;
			}

			Assert.That(directory, Is.Not.Null);
			root = directory.FullName;
		}

		private static string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));

		[TestCase("SolidBoxObject3D")]
		[TestCase("SolidCylinderObject3D")]
		[TestCase("SolidPlateObject3D")]
		public void WithSolidsOffTheParametersAreLockedAndTheSizeHandlesGone(string type)
		{
			var source = Read($"MatterControlLib/DesignTools/Solid/{type}.cs");
			Assert.That(source, Does.Contain("if (!CanEditSize)"));
			Assert.That(source, Does.Contain("LockRowsWhenUnavailable(change);"));
			Assert.That(source, Does.Not.Contain(".ClampIfNotCalculated("), "values are read through Read(), which only writes back when asked");
		}

		[Test]
		public void KernelIsOnlyLoadedFromTheApp()
		{
			var source = Read("MatterControl.CrossPlatform/Platform/NativeLibraries.cs");
			Assert.That(source, Does.Contain("throw new DllNotFoundException(\"The solid modelling library (libebmc_occt)"));
			Assert.That(source, Does.Contain("\".app/Contents/\", StringComparison.OrdinalIgnoreCase"));
		}

		[TestCase("SubtractObject3D_2")]
		[TestCase("SubtractAndReplaceObject3D_2")]
		public void HoleDefaultIsAppliedBeforeTheBuildTaskStarts(string type)
		{
			var source = Read($"MatterControlLib/PartPreviewWindow/View3D/Actions/{type}.cs");
			var rebuild = source.IndexOf("public override Task Rebuild()");
			var apply = source.IndexOf("if (applyDefaultPartsToRemove)");
			var task = source.IndexOf("Tasks.Execute(", rebuild);
			Assert.That(rebuild, Is.GreaterThan(0));
			Assert.That(apply, Is.GreaterThan(rebuild).And.LessThan(task), "applied in Rebuild, on the calling (UI) thread, before the task");
			Assert.That(source.IndexOf("if (applyDefaultPartsToRemove)", apply + 1), Is.EqualTo(-1), "and nowhere else (not on the build thread)");

			// the tidy-up that removes IDs also runs before the task, not inside the build
			var build = source.IndexOf(type == "SubtractObject3D_2" ? "private void Subtract(CancellationToken" : "private void SubtractAndReplace(CancellationToken");
			Assert.That(build, Is.GreaterThan(0));
			var buildEnd = source.IndexOf("\n        }\n", build);
			var buildBody = source.Substring(build, buildEnd - build);
			Assert.That(buildBody, Does.Not.Contain("CleanUpSelectedChildrenIDs(this)").And.Not.Contain("TidySelectedChildren()").And.Not.Contain("SelectedChildren.Add").And.Not.Contain("SelectedChildren.Remove"));
		}
	}
}
