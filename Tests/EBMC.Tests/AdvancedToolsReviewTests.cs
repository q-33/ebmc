using System;
using System.IO;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// Fixes from the review of the Advanced tools and club shapes: a Transform field never applies an edit that changes
	/// nothing (the second commit after Enter), typed limits, turned parts scale evenly, the search's printer-tab group
	/// rule, the Mac wording of the shortcuts sheet and the club shapes' automatic rebuild rule.
	/// </summary>
	[TestFixture]
	public class AdvancedToolsReviewTests
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

			root = directory.FullName;
		}

		private static string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));

		private static readonly TransformBounds Box = new TransformBounds(-6.1725, -5, 0, 6.1725, 5, 20);

		[Test]
		public void RoundedRecommitChangesNothing()
		{
			// 12.345 typed and applied; the field then shows 12.35. Committing 12.35 again must be a no-op
			Assert.That(Box.SizeX, Is.EqualTo(12.345).Within(1e-9));
			Assert.That(TransformRules.IsNoChange(Box, TransformField.SizeX, 12.35), Is.True);
			Assert.That(TransformRules.IsNoChange(Box, TransformField.SizeX, 12.345), Is.True);
			Assert.That(TransformRules.IsNoChange(Box, TransformField.SizeX, 12.36), Is.False);
		}

		[TestCase(TransformField.CenterX, 0, true)]
		[TestCase(TransformField.CenterX, 0.004, true)]
		[TestCase(TransformField.CenterX, 0.01, false)]
		[TestCase(TransformField.BottomZ, 0, true)]
		[TestCase(TransformField.SizeZ, 20, true)]
		[TestCase(TransformField.SizeZ, 21, false)]
		[TestCase(TransformField.RotateZ, 0, true)]
		[TestCase(TransformField.RotateZ, 360, true)]
		[TestCase(TransformField.RotateZ, 90, false)]
		[TestCase(TransformField.RotateX, double.NaN, true)]
		public void NoChange(TransformField field, double value, bool expected)
		{
			Assert.That(TransformRules.IsNoChange(Box, field, value), Is.EqualTo(expected));
		}

		[TestCase(TransformField.CenterX, 10000, true)]
		[TestCase(TransformField.CenterX, -10000, true)]
		[TestCase(TransformField.CenterY, 10000.5, false)]
		[TestCase(TransformField.BottomZ, -20000, false)]
		[TestCase(TransformField.SizeX, 10000, true)]
		[TestCase(TransformField.SizeY, 10001, false)]
		[TestCase(TransformField.RotateZ, 720, true)]
		[TestCase(TransformField.SizeZ, double.PositiveInfinity, false)]
		public void Limits(TransformField field, double value, bool expected)
		{
			Assert.That(TransformRules.IsWithinLimits(field, value), Is.EqualTo(expected));
		}

		[Test]
		public void AxisAlignedMatrices()
		{
			// identity, non-uniform scale, a quarter turn about Z and a mirror are axis aligned
			Assert.That(TransformRules.IsAxisAligned(1, 0, 0, 0, 1, 0, 0, 0, 1), Is.True);
			Assert.That(TransformRules.IsAxisAligned(2, 0, 0, 0, 0.5, 0, 0, 0, 3), Is.True);
			Assert.That(TransformRules.IsAxisAligned(0, 1, 0, -1, 0, 0, 0, 0, 1), Is.True);
			Assert.That(TransformRules.IsAxisAligned(-1, 0, 0, 0, 1, 0, 0, 0, 1), Is.True);

			// 30 degrees about Z is not; nor is a flat (zero) axis
			var c = Math.Cos(Math.PI / 6);
			var s = Math.Sin(Math.PI / 6);
			Assert.That(TransformRules.IsAxisAligned(c, s, 0, -s, c, 0, 0, 0, 1), Is.False);
			Assert.That(TransformRules.IsAxisAligned(1, 0, 0, 0, 0, 0, 0, 0, 1), Is.False);

			// a rounding-error rotation still counts as aligned
			Assert.That(TransformRules.IsAxisAligned(1, 1e-9, 0, -1e-9, 1, 0, 0, 0, 1, 1e-6), Is.True);
		}

		[Test]
		public void TurnedPartsScaleEvenly()
		{
			Assert.That(TransformRules.ScaleProportionally(keepProportions: true, axisAligned: true), Is.True);
			Assert.That(TransformRules.ScaleProportionally(keepProportions: false, axisAligned: true), Is.False);
			Assert.That(TransformRules.ScaleProportionally(keepProportions: false, axisAligned: false), Is.True);

			var step = TransformRules.Resize(Box, TransformField.SizeX, 24.69, TransformRules.ScaleProportionally(false, false));
			Assert.That(step.ScaleX, Is.EqualTo(step.ScaleY).Within(1e-12));
			Assert.That(step.ScaleZ, Is.EqualTo(step.ScaleX).Within(1e-12));
		}

		[Test]
		public void TransformSectionMarksProgrammaticTextAsStartingState()
		{
			var source = Read("MatterControlLib/PartPreviewWindow/TransformSection.cs");
			Assert.That(source, Does.Contain("InternalTextEditWidget.MarkAsStartingState()"));
			// every programmatic assignment goes through SetFieldText (the only "edit.Text =" left is inside it)
			Assert.That(System.Text.RegularExpressions.Regex.Matches(source, @"\.Text = ").Count, Is.EqualTo(1));
			Assert.That(source, Does.Contain("!TransformRules.IsNoChange(bounds, field, value.Value)"));
			Assert.That(source, Does.Contain("TransformRules.IsWithinLimits(field, value.Value)"));
			Assert.That(source, Does.Contain("TransformRules.NestedSelectionMessage"));
		}

		[Test]
		public void SearchGroupsOnPrinterTabsFollowTheToolbar()
		{
			// design tabs: every group (as the Modify menu)
			Assert.That(CommandSearchRules.SearchGroup(printerTab: false, groupVisibleOnToolbar: false, groupShownOnPrinterTabs: false), Is.True);

			// printer tabs: only groups the toolbar shows there (Printing is hidden by default)
			Assert.That(CommandSearchRules.SearchGroup(true, groupVisibleOnToolbar: false, groupShownOnPrinterTabs: true), Is.False);
			Assert.That(CommandSearchRules.SearchGroup(true, true, false), Is.False);
			Assert.That(CommandSearchRules.SearchGroup(true, true, true), Is.True);
		}

		[Test]
		public void ShortcutKeysOnAMac()
		{
			Assert.That(ShortcutTable.KeysForSystem("Delete, Backspace", mac: true), Is.EqualTo("Delete"));
			Assert.That(ShortcutTable.KeysForSystem("Ctrl + V, Shift + Insert", mac: true), Is.EqualTo("Ctrl + V"));
			Assert.That(ShortcutTable.KeysForSystem("Delete, Backspace", mac: false), Is.EqualTo("Delete, Backspace"));
			Assert.That(ShortcutTable.KeysForSystem("Ctrl + V, Shift + Insert", mac: false), Is.EqualTo("Ctrl + V, Shift + Insert"));
			Assert.That(ShortcutTable.KeysForSystem("Ctrl + Y, Ctrl + Shift + Z", mac: true), Is.EqualTo("Ctrl + Y, Ctrl + Shift + Z"));
		}

		[Test]
		public void CtrlShortcutsIgnoreShiftAndTextFields()
		{
			var source = Read("MatterControlLib/ApplicationView/Application.cs");
			Assert.That(source, Does.Match(@"keyEvent\.Control\s+&& !keyEvent\.Shift\s+&& keyEvent\.KeyCode == Keys\.K"));
			Assert.That(source, Does.Match(@"keyEvent\.Control\s+&& !keyEvent\.Shift\s+&& AdvancedTools\.IsEnabled"));
			Assert.That(source, Does.Contain("bool textFieldFocused = TextFieldHasFocus(systemWindow);"));
		}

		[Test]
		public void ClubShapesDoNotRebuildForEverAfterAFailure()
		{
			Assert.That(ClubBuildRules.AutoRebuild(hasMesh: false, lastBuildFailed: false, isBuilding: false, inOpenScene: true), Is.True);
			Assert.That(ClubBuildRules.AutoRebuild(false, lastBuildFailed: true, false, true), Is.False);
			Assert.That(ClubBuildRules.AutoRebuild(false, false, isBuilding: true, true), Is.False);
			Assert.That(ClubBuildRules.AutoRebuild(false, false, false, inOpenScene: false), Is.False);
			Assert.That(ClubBuildRules.AutoRebuild(hasMesh: true, false, false, true), Is.False);

			// the Mesh getter has no side effect, and the solids restart for a queued build even after a failure
			var shape = Read("MatterControlLib/DesignTools/ClubShapes/ClubShapeObject3D.cs");
			Assert.That(shape, Does.Contain("get => base.Mesh;"));
			var solid = Read("MatterControlLib/DesignTools/Solid/SolidObject3D.cs");
			Assert.That(solid, Does.Contain("restart = ClubShapes.ClubBuildRules.Restart(failed, pendingBuild != null);"));
		}

		[Test]
		public void NestedSelectionsAreNotMirrorCopied()
		{
			var operations = Read("MatterControlLib/ApplicationView/SceneOperations.cs");
			Assert.That(operations, Does.Contain("&& sceneContext.Scene.Children.Contains(sceneContext.Scene.SelectedItem)"));
			var mirror = Read("MatterControlLib/PartPreviewWindow/View3D/Actions/MirrorCopy.cs");
			Assert.That(mirror, Does.Contain("if (!scene.Children.Contains(selected))"));
		}

		[Test]
		public void MeshlessPartsWaitForAnOpenSceneWithoutATimeLimit()
		{
			// a part not yet in an open scene is handed to MissingMeshWatch (event driven, weak references) instead of
			// being dropped after ten one-second looks
			foreach (var file in new[] { "MatterControlLib/DesignTools/ClubShapes/ClubShapeObject3D.cs", "MatterControlLib/DesignTools/Solid/SolidObject3D.cs" })
			{
				var source = Read(file);
				Assert.That(source, Does.Contain("MissingMeshWatch.Add(this);"), file);
				Assert.That(source, Does.Contain("IRestoresMissingMesh.TryRestoreMissingMesh()"), file);
			}

			var watch = Read("MatterControlLib/DesignTools/MissingMeshWatch.cs");
			Assert.That(watch, Does.Contain("WeakReference<IRestoresMissingMesh>"));
			Assert.That(watch, Does.Contain("WorkspacesChanged"));
			Assert.That(watch, Does.Contain("SceneLoaded"));
		}

		[Test]
		public void ReAddedWidgetsClearTheirRemovedFlag()
		{
			// agg-sharp's Debug AddChild throws for a widget that was removed and not cleared
			Assert.That(Read("MatterControlLib/SetupWizard/DialogPage.cs"), Does.Contain("button.ClearRemovedFlag();"));
			var toast = Read("MatterControlLib/ApplicationView/ClubUi/ClubToast.cs");
			Assert.That(toast, Does.Contain("stack.ClearRemovedFlag();"));
			Assert.That(toast, Does.Contain("icon.ClearRemovedFlag();"));
			Assert.That(Read("MatterControlLib/ApplicationView/ClubUi/ClubSceneMenus.cs"), Does.Contain("deleteItem.ClearRemovedFlag();"));
		}

		[Test]
		public void LibraryMenusMatchOnStableIds()
		{
			var widget = Read("MatterControlLib/Library/Widgets/LibraryWidget.cs");
			foreach (var entry in ClubUiSpec.LibraryEntriesWithActions)
			{
				Assert.That(widget, Does.Contain("Id = ClubUi.ClubMenus." + entry + ","), entry);
			}

			Assert.That(Read("MatterControlLib/ApplicationView/ClubUi/ClubLibraryMenus.cs"), Does.Not.Contain("StockTitles"));
		}

		private static class ClubUiSpec
		{
			public static readonly string[] LibraryEntriesWithActions =
			{
				"LibAdd", "LibCreateFolder", "LibShareCode", "LibPrint", "LibOpen", "LibOpenCopy", "LibAddToBed",
				"LibExport", "LibShare", "LibRename", "LibMove", "LibShowOnDisk", "LibRemove",
			};
		}

		private static double Arithmetic(string text) => System.Convert.ToDouble(new System.Data.DataTable().Compute(text, null), System.Globalization.CultureInfo.InvariantCulture);

		[TestCase("100.126", 100.126)]
		[TestCase(" 100.126 ", 100.126)]
		[TestCase("+100.126", 100.126)]
		[TestCase(".5", 0.5)]
		[TestCase("100.", 100.0)]
		[TestCase("79.7+20.3", 100.0)]
		[TestCase("(2+3)*4", 20.0)]
		public void TypedValuesReadAsTyped(string text, double expected)
		{
			Assert.That(TransformRules.ParseNumber(text, Arithmetic), Is.EqualTo(expected).Within(1e-9));
		}

		// "79.7100.126" is the old value with 100.126 typed after it: the expression parser read it as 79.71 x 0.126 =
		// 10.04, which scaled a 79.7 mm part to 10.04 mm. Every such text is refused now and the field reverts.
		[TestCase("79.7100.126")]
		[TestCase("79.100.126")]
		[TestCase("100,126")]
		[TestCase("1e2")]
		[TestCase("1E2")]
		[TestCase("2 3")]
		[TestCase("2(3)")]
		[TestCase("(2)3")]
		[TestCase(".")]
		[TestCase("1..2")]
		public void AmbiguousTextIsRefused(string text)
		{
			// the evaluator would give a number for some of these; the rule must refuse them before it is asked
			Assert.That(TransformRules.ParseNumber(text, s => 10.04), Is.Null, text);
		}

		[Test]
		public void PlainArithmeticRule()
		{
			Assert.That(TransformRules.IsPlainArithmetic("20+5"), Is.True);
			Assert.That(TransformRules.IsPlainArithmetic("-(3.5*2)/7"), Is.True);
			Assert.That(TransformRules.IsPlainArithmetic("79.7100.126"), Is.False);
			Assert.That(TransformRules.IsPlainArithmetic("2x3"), Is.False);
		}

		[Test]
		public void TransformFieldsSelectOnClickAndScrollBack()
		{
			var source = Read("MatterControlLib/PartPreviewWindow/TransformSection.cs");
			Assert.That(source, Does.Contain("InternalTextEditWidget.SelectAllOnFocus = true;"));
			Assert.That(source, Does.Contain("mouseEvent.Clicks >= 3"));
			Assert.That(source, Does.Contain("edit.ActualTextEditWidget.TopLeftOffset = Vector2.Zero;"));
		}

		[Test]
		public void CommandSearchPopupIsWideBeforeItIsShown()
		{
			// a FlowLayoutWidget starts as HAnchor.Fit; a Width set before HAnchor.Absolute is laid out back to 0 (checked
			// headless with agg-sharp: 0 x 76 in the old order, 480 x 76 in the new one)
			var source = Read("MatterControlLib/PartPreviewWindow/CommandSearch/CommandSearchPopup.cs");
			int anchor = source.IndexOf("this.HAnchor = HAnchor.Absolute;", System.StringComparison.Ordinal);
			int width = source.IndexOf("this.Width = 480 * DeviceScale;", System.StringComparison.Ordinal);
			Assert.That(anchor, Is.GreaterThan(0));
			Assert.That(width, Is.GreaterThan(anchor));
		}
	}
}
