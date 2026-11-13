using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.PartPreviewWindow;
using NUnit.Framework;

namespace EBMC.Tests
{
	/// <summary>
	/// The Advanced tools (OemSettings.EnableAdvancedTools): command search ranking, synonyms and the no-printing rule
	/// (CommandSearchRules), the Transform section's maths (TransformRules), Mirror Copy placement (MirrorCopyRules) and
	/// the keyboard shortcuts table against the handlers (ShortcutTable).
	/// </summary>
	[TestFixture]
	public class AdvancedToolsTests
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

		// ------------------------------------------------------------------------------------------------------------
		// command search

		private static CommandEntry Op(string id, string title = null, string where = "Modify", bool enabled = true, string help = null)
		{
			return new CommandEntry(CommandKind.Operation, "op:" + id, id, title ?? id, where) { Enabled = enabled, HelpText = help, DisabledReason = enabled ? null : help };
		}

		private static CommandEntry Shape(string name, string where = "Shapes")
		{
			return new CommandEntry(CommandKind.Shape, "shape:" + name, name, name, where);
		}

		private static CommandEntry Ws(string id, string title, string where = "File")
		{
			return new CommandEntry(CommandKind.Workspace, "ws:" + id, id, title, where);
		}

		/// <summary>A realistic set: the design tab's operations, palette shapes and the workspace whitelist.</summary>
		private static List<CommandEntry> Catalog(bool selectionOk = true)
		{
			return new List<CommandEntry>
			{
				Op("ArrangeAllParts", "Arrange All Parts"),
				Op("Lay Flat"),
				Op("Group"),
				Op("Duplicate"),
				Op("Remove"),
				Op("Translate", where: "Transform"),
				Op("Rotate", where: "Transform"),
				Op("Scale", where: "Transform"),
				Op("Mirror", where: "Transform"),
				Op("MirrorCopy", "Mirror Copy", "Transform"),
				Op("Align", where: "Placement"),
				Op("Plane Cut", where: "Reshape", enabled: selectionOk, help: "**At least 1 part must be selected**"),
				Op("Hollow Out", where: "Reshape"),
				Op("LinearExtrude", "Linear Extrude", "Path"),
				Op("Revolve", where: "Path"),
				Op("Combine", where: "Merge", enabled: selectionOk, help: "**At least 2 parts must be selected**"),
				Op("Subtract", where: "Merge", enabled: selectionOk, help: "**At least 2 parts must be selected**"),
				Op("Intersect", where: "Merge"),
				Op("Linear Array", where: "Duplication"),
				Op("Radial Array", where: "Duplication"),
				Op("Advanced Array", where: "Duplication"),
				Op("Reduce", where: "Mesh"),
				Op("Repair", where: "Mesh"),
				Shape("Cube"),
				Shape("Cylinder"),
				Shape("Rounded Box"),
				Shape("Text"),
				Shape("Gear"),
				Shape("Measure Tool"),
				Shape("Solid Box", "Solids"),
				Shape("Solid Cylinder", "Solids"),
				Shape("Plate with Hole", "Solids"),
				Shape("Name Tag", "Starters"),
				Shape("Cable Clip", "Starters"),
				Ws("Save", "Save"),
				Ws("SaveAs", "Save As"),
				Ws("OpenFile", "Open file…"),
				Ws("Undo", "Undo", "Edit"),
				Ws("Shortcuts", "Keyboard shortcuts", "App"),
			};
		}

		private static List<string> Titles(string query, List<CommandEntry> entries, IReadOnlyList<string> recents = null)
		{
			return CommandSearchRules.Rank(query, entries, recents).Select(e => e.Title).ToList();
		}

		[TestCase("fillet")]
		[TestCase("round")]
		public void FilletFindsTheRoundedShapesFirst(string query)
		{
			var titles = Titles(query, Catalog());
			Assert.That(titles.Take(2), Is.EquivalentTo(new[] { "Rounded Box", "Solid Box" }));
		}

		[TestCase("chamfer", "Solid Box")]
		[TestCase("bevel", "Solid Box")]
		[TestCase("cut", "Subtract")]
		[TestCase("difference", "Subtract")]
		[TestCase("boolean", "Subtract")]
		[TestCase("union", "Combine")]
		[TestCase("join", "Combine")]
		[TestCase("merge", "Combine")]
		[TestCase("pattern", "Linear Array")]
		[TestCase("extrude", "Linear Extrude")]
		[TestCase("revolve", "Revolve")]
		[TestCase("lathe", "Revolve")]
		[TestCase("shell", "Hollow Out")]
		[TestCase("split", "Plane Cut")]
		[TestCase("slice plane", "Plane Cut")]
		[TestCase("move", "Translate")]
		[TestCase("copy", "Duplicate")]
		[TestCase("flip", "Mirror")]
		[TestCase("measure", "Measure Tool")]
		[TestCase("ruler", "Measure Tool")]
		[TestCase("hole", "Plate with Hole")]
		[TestCase("text", "Text")]
		[TestCase("label", "Text")]
		[TestCase("gear", "Gear")]
		[TestCase("cog", "Gear")]
		[TestCase("step", "Open file…")]
		[TestCase("cad file", "Open file…")]
		[TestCase("simplify", "Reduce")]
		[TestCase("decimate", "Reduce")]
		[TestCase("fix", "Repair")]
		[TestCase("heal", "Repair")]
		[TestCase("FILLET", "Rounded Box")]
		public void SynonymsFindOurTools(string query, string expectedFirst)
		{
			Assert.That(Titles(query, Catalog()).FirstOrDefault(), Is.EqualTo(expectedFirst));
		}

		[Test]
		public void CutAlsoListsPlaneCutAfterSubtract()
		{
			var titles = Titles("cut", Catalog());
			Assert.That(titles.IndexOf("Subtract"), Is.LessThan(titles.IndexOf("Plane Cut")));
			Assert.That(titles, Does.Contain("Plane Cut"));
		}

		[Test]
		public void MatchingIsPrefixThenWordPrefixThenSubstringThenFuzzy()
		{
			var entries = new List<CommandEntry>
			{
				Op("Xsubarray", "Xarray thing"),       // substring "array"
				Op("Linear Array"),                   // word prefix "Array"
				Op("Array Builder"),                  // prefix
				Op("Advanced Ray", "A r r a y"),      // nothing but fuzzy letters
			};

			Assert.That(Titles("array", entries), Is.EqualTo(new[] { "Array Builder", "Linear Array", "Xarray thing", "A r r a y" }));
		}

		[Test]
		public void MatchingIsCaseInsensitive()
		{
			Assert.That(Titles("LINEAR", Catalog()).Take(2), Is.EquivalentTo(new[] { "Linear Array", "Linear Extrude" }));
			Assert.That(Titles("plane cut", Catalog()).First(), Is.EqualTo("Plane Cut"));
		}

		[Test]
		public void FuzzySubsequenceFindsAbbreviations()
		{
			Assert.That(Titles("lnar", Catalog()), Does.Contain("Linear Array"));
			Assert.That(Titles("zzzq", Catalog()), Is.Empty);
		}

		[Test]
		public void SeveralWordsMustAllMatch()
		{
			var titles = Titles("radial array", Catalog());
			Assert.That(titles.First(), Is.EqualTo("Radial Array"));
			Assert.That(titles, Does.Not.Contain("Linear Array"));
		}

		[Test]
		public void HelpTextAndWhereAreSearchedToo()
		{
			Assert.That(Titles("starters", Catalog()), Does.Contain("Name Tag"));
		}

		[Test]
		public void DisabledCommandsRankAfterEnabledOnes()
		{
			// with nothing selected, Subtract and Plane Cut are disabled; a weaker enabled match still comes first
			var entries = Catalog(selectionOk: false);
			entries.Add(Op("Cutter Thing"));
			var ranked = CommandSearchRules.Rank("cut", entries);
			Assert.That(ranked.First().Title, Is.EqualTo("Cutter Thing"));

			int lastEnabled = ranked.ToList().FindLastIndex(e => e.Enabled);
			int firstDisabled = ranked.ToList().FindIndex(e => !e.Enabled);
			Assert.That(firstDisabled, Is.GreaterThan(lastEnabled));
			Assert.That(ranked.Where(e => !e.Enabled).Select(e => e.Title), Does.Contain("Subtract"));
		}

		[Test]
		public void EmptyQueryListsRecentsFirstMostRecentFirst()
		{
			var recents = CommandSearchRules.AddRecent(null, "op:Combine");
			recents = CommandSearchRules.AddRecent(recents, "shape:Gear");
			recents = CommandSearchRules.AddRecent(recents, "op:Combine");

			Assert.That(recents, Is.EqualTo(new[] { "op:Combine", "shape:Gear" }));

			var titles = Titles("", Catalog(), recents);
			Assert.That(titles.Take(2), Is.EqualTo(new[] { "Combine", "Gear" }));
			Assert.That(titles.Count, Is.EqualTo(CommandSearchRules.MaxRows));
			Assert.That(Titles("   ", Catalog(), recents).First(), Is.EqualTo("Combine"));
		}

		[Test]
		public void RecentsAreKeptToEightAndNoDuplicates()
		{
			List<string> recents = null;
			for (int i = 0; i < 20; i++)
			{
				recents = CommandSearchRules.AddRecent(recents, "op:" + (i % 11));
			}

			Assert.That(recents.Count, Is.EqualTo(CommandSearchRules.MaxRows));
			Assert.That(recents.Distinct().Count(), Is.EqualTo(recents.Count));
		}

		[Test]
		public void AtMostEightRows()
		{
			Assert.That(CommandSearchRules.Rank("a", Catalog()).Count, Is.LessThanOrEqualTo(CommandSearchRules.MaxRows));
		}

		[TestCase("Print")]
		[TestCase("Upload and Print")]
		[TestCase("Send")]
		[TestCase("Slice")]
		[TestCase("Export G-code and slice")]
		[TestCase("PRINT")]
		public void NoPrintingCommandIsEverListed(string title)
		{
			var forbiddenById = new CommandEntry(CommandKind.Workspace, "ws:" + title.Replace(" ", ""), title, "Harmless", "File");
			var forbiddenByTitle = new CommandEntry(CommandKind.Workspace, "ws:x", "x", title, "File");
			var forbiddenByKey = new CommandEntry(CommandKind.Operation, "op:y", title, "Something", "Modify");

			var entries = Catalog();
			entries.AddRange(new[] { forbiddenById, forbiddenByTitle, forbiddenByKey });

			foreach (var query in new[] { "", title, "print", "send", "upload", "slice", "x", "harmless", "something" })
			{
				var ranked = CommandSearchRules.Rank(query, entries, new[] { forbiddenById.Id, forbiddenByTitle.Id, forbiddenByKey.Id }, 100);
				Assert.That(ranked, Does.Not.Contain(forbiddenById));
				Assert.That(ranked, Does.Not.Contain(forbiddenByTitle));
				Assert.That(ranked, Does.Not.Contain(forbiddenByKey));
				foreach (var entry in ranked)
				{
					foreach (var word in CommandSearchRules.ForbiddenWords)
					{
						Assert.That((entry.Id + " " + entry.Key + " " + entry.Title).ToLowerInvariant(), Does.Not.Contain(word));
					}
				}
			}
		}

		[Test]
		public void TheWorkspaceWhitelistHasNoPrintingAction()
		{
			foreach (var id in CommandSearchRules.WorkspaceWhitelist)
			{
				Assert.That(CommandSearchRules.IsForbidden(id), Is.False, id);
			}

			Assert.That(CommandSearchRules.WorkspaceWhitelist, Does.Not.Contain("Print"));
			Assert.That(CommandSearchRules.WorkspaceWhitelist, Does.Not.Contain("ClearBed"));
		}

		[Test]
		public void TheSearchSourcesNeverReachAPrintPath()
		{
			var source = Read("MatterControlLib/PartPreviewWindow/CommandSearch/CommandSearchPopup.cs");

			foreach (var banned in new[] { "PushToPrinterAndPrint", "OctoPrint", "PrintPart", "SliceItem", "Slicer.", "\"Print\"", "StartPrint", "SendBed", "GCodeExport" })
			{
				Assert.That(source, Does.Not.Contain(banned), banned);
			}

			// every row passes the safety net, and every workspace row is checked against the whitelist
			Assert.That(source, Does.Contain("items.Where(i => CommandSearchRules.IsAllowed(i.Entry))"));
			Assert.That(source, Does.Contain("if (!CommandSearchRules.WorkspaceWhitelist.Contains(id))"));

			// every whitelisted action has a source
			foreach (var id in CommandSearchRules.WorkspaceWhitelist)
			{
				Assert.That(source, Does.Match("(Add|AddNamed)\\(\\s*\"" + id + "\""), id);
			}

			// Export is never offered: its dialog can export G-code (a slice) whenever a printer is attached, and on a
			// design tab ExportLibraryItems attaches the only active printer itself
			Assert.That(CommandSearchRules.WorkspaceWhitelist, Does.Not.Contain("Export"));
			Assert.That(source, Does.Not.Contain("AddNamed(\"Export\""));
			Assert.That(source, Does.Not.Contain("ExportLibraryItems"));

			// on a printer tab a group the toolbar hides (Printing: support, wipe tower, fuzzy) is not searchable
			Assert.That(source, Does.Contain("CommandSearchRules.SearchGroup(printerTab, group.Visible, group.ShowOnPrinterTabs)"));
		}

		[Test]
		public void EverySynonymPointsAtSomethingThatExists()
		{
			var operations = Read("MatterControlLib/ApplicationView/SceneOperations.cs");
			var operationIds = new HashSet<string>(Regex.Matches(operations, "new SceneOperation\\(\"([^\"]+)\"\\)").Select(m => m.Groups[1].Value));
			var operationTitles = new HashSet<string>(Regex.Matches(operations, "TitleGetter = \\(\\) => \"([^\"]+)\"").Select(m => m.Groups[1].Value));

			var paletteNames = new HashSet<string>(
				new[] { PaletteTab.Design, PaletteTab.Printer }
					.SelectMany(tab => PaletteCatalog.For(tab, true, MatterHackers.MatterControl.UiMode.Advanced, true))
					.SelectMany(s => s.Families)
					.SelectMany(f => f.Members)
					.Select(m => m.Name));

			foreach (var pair in CommandSearchRules.Synonyms)
			{
				foreach (var target in pair.Value)
				{
					bool exists = operationIds.Contains(target)
						|| operationTitles.Contains(target)
						|| paletteNames.Contains(target)
						|| CommandSearchRules.WorkspaceWhitelist.Contains(target);
					Assert.That(exists, Is.True, $"{pair.Key} -> {target}");
				}
			}
		}

		[Test]
		public void MirrorCopyIsRegisteredBesideMirror()
		{
			var operations = Read("MatterControlLib/ApplicationView/SceneOperations.cs");
			Assert.That(operations, Does.Contain("new SceneOperation(\"MirrorCopy\")"));

			var mirror = operations.IndexOf("MirrorOperation(),", StringComparison.Ordinal);
			var copy = operations.IndexOf("DesignOnly(MirrorCopyOperation())", StringComparison.Ordinal);
			Assert.That(mirror, Is.GreaterThan(0));
			Assert.That(copy, Is.GreaterThan(mirror));
			Assert.That(operations.Substring(mirror, copy - mirror), Does.Not.Contain("new OperationGroup("), "Mirror Copy is in the Transform group with Mirror");
		}

		[Test]
		public void HelpStarsAreRemoved()
		{
			Assert.That(CommandSearchRules.PlainHelp("**At least 1 part must be selected**"), Is.EqualTo("At least 1 part must be selected"));
		}

		// ------------------------------------------------------------------------------------------------------------
		// Transform section maths

		private static readonly TransformBounds Box = new TransformBounds(10, 20, 0, 30, 60, 5); // 20 × 40 × 5, centre (20, 40)

		private static void AssertPoint((double X, double Y, double Z) actual, double x, double y, double z)
		{
			Assert.That(actual.X, Is.EqualTo(x).Within(1e-9));
			Assert.That(actual.Y, Is.EqualTo(y).Within(1e-9));
			Assert.That(actual.Z, Is.EqualTo(z).Within(1e-9));
		}

		[Test]
		public void TheFieldsShowCentreBottomAndSize()
		{
			Assert.That(Box.Field(TransformField.CenterX), Is.EqualTo(20));
			Assert.That(Box.Field(TransformField.CenterY), Is.EqualTo(40));
			Assert.That(Box.Field(TransformField.BottomZ), Is.EqualTo(0));
			Assert.That(Box.Field(TransformField.SizeX), Is.EqualTo(20));
			Assert.That(Box.Field(TransformField.SizeY), Is.EqualTo(40));
			Assert.That(Box.Field(TransformField.SizeZ), Is.EqualTo(5));
		}

		[Test]
		public void MovePutsTheCentreOrBottomWhereTyped()
		{
			var step = TransformRules.Move(Box, TransformField.CenterX, 100);
			AssertPoint(step.Apply(20, 40, 0), 100, 40, 0);

			step = TransformRules.Move(Box, TransformField.CenterY, -5);
			AssertPoint(step.Apply(20, 40, 0), 20, -5, 0);

			step = TransformRules.Move(Box, TransformField.BottomZ, 2.5);
			AssertPoint(step.Apply(10, 20, 0), 10, 20, 2.5);

			Assert.That(TransformRules.Move(Box, TransformField.CenterX, 20), Is.Null, "already there");
		}

		[Test]
		public void ResizeKeepsTheCentreAndBottom()
		{
			var step = TransformRules.Resize(Box, TransformField.SizeX, 40, proportional: false);
			AssertPoint(step.Apply(10, 20, 0), 0, 20, 0);
			AssertPoint(step.Apply(30, 60, 5), 40, 60, 5);
			AssertPoint(step.Apply(20, 40, 0), 20, 40, 0);

			step = TransformRules.Resize(Box, TransformField.SizeZ, 10, proportional: false);
			AssertPoint(step.Apply(30, 60, 5), 30, 60, 10);
			AssertPoint(step.Apply(10, 20, 0), 10, 20, 0);
		}

		[Test]
		public void ProportionalResizeScalesEveryAxis()
		{
			var step = TransformRules.Resize(Box, TransformField.SizeY, 80, proportional: true);
			Assert.That(step.ScaleX, Is.EqualTo(2));
			Assert.That(step.ScaleY, Is.EqualTo(2));
			Assert.That(step.ScaleZ, Is.EqualTo(2));
			AssertPoint(step.Apply(30, 60, 5), 40, 80, 10);
		}

		[TestCase(0)]
		[TestCase(-5)]
		[TestCase(0.0001)]
		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		public void ZeroNegativeOrBrokenSizesAreRefused(double size)
		{
			Assert.That(TransformRules.Resize(Box, TransformField.SizeX, size, true), Is.Null);
		}

		[Test]
		public void AFlatSelectionCannotBeScaledAlongItsFlatAxis()
		{
			var flat = new TransformBounds(0, 0, 0, 10, 10, 0);
			Assert.That(TransformRules.Resize(flat, TransformField.SizeZ, 5, false), Is.Null);
			Assert.That(TransformRules.Resize(flat, TransformField.SizeX, 20, false), Is.Not.Null);
		}

		[Test]
		public void RotateTurnsAboutTheCentre()
		{
			var step = TransformRules.Rotate(Box, TransformField.RotateZ, 90);
			Assert.That(step.PivotX, Is.EqualTo(20));
			Assert.That(step.PivotY, Is.EqualTo(40));
			Assert.That(step.PivotZ, Is.EqualTo(2.5));

			// the centre stays; +X of the centre turns to +Y (counter-clockwise seen from above)
			AssertPoint(step.Apply(20, 40, 2.5), 20, 40, 2.5);
			AssertPoint(step.Apply(30, 40, 2.5), 20, 50, 2.5);

			step = TransformRules.Rotate(Box, TransformField.RotateX, 90);
			AssertPoint(step.Apply(20, 50, 2.5), 20, 40, 12.5);

			step = TransformRules.Rotate(Box, TransformField.RotateY, 90);
			AssertPoint(step.Apply(20, 40, 12.5), 30, 40, 2.5);
		}

		[TestCase(0)]
		[TestCase(360)]
		[TestCase(-720)]
		[TestCase(double.NaN)]
		public void NoTurnIsNoStep(double degrees)
		{
			Assert.That(TransformRules.Rotate(Box, TransformField.RotateZ, degrees), Is.Null);
		}

		[Test]
		public void KeepBottomLiftsBackToTheOldBottom()
		{
			Assert.That(TransformRules.KeepBottom(0, -7.5), Is.EqualTo(7.5));
			Assert.That(TransformRules.KeepBottom(2, 2), Is.EqualTo(0));
		}

		[Test]
		public void InvalidBoundsGiveNoStep()
		{
			var broken = new TransformBounds(double.PositiveInfinity, 0, 0, double.NegativeInfinity, 1, 1);
			Assert.That(broken.IsValid, Is.False);
			Assert.That(TransformRules.Move(broken, TransformField.CenterX, 1), Is.Null);
			Assert.That(TransformRules.Resize(broken, TransformField.SizeX, 1, true), Is.Null);
			Assert.That(TransformRules.Rotate(broken, TransformField.RotateX, 1), Is.Null);
		}

		private static double Arithmetic(string text) => Convert.ToDouble(new DataTable().Compute(text, null), CultureInfo.InvariantCulture);

		[TestCase("20", 20.0)]
		[TestCase(" -3.5 ", -3.5)]
		[TestCase("12 mm", 12.0)]
		[TestCase("90°", 90.0)]
		[TestCase("=7", 7.0)]
		[TestCase("20+5", 25.0)]
		[TestCase("40/4", 10.0)]
		[TestCase("=(2+3)*4", 20.0)]
		public void NumbersAndArithmeticAreAccepted(string text, double expected)
		{
			Assert.That(TransformRules.ParseNumber(text, Arithmetic), Is.EqualTo(expected).Within(1e-9));
		}

		[TestCase("")]
		[TestCase("   ")]
		[TestCase("abc")]
		[TestCase("width")]
		[TestCase("NaN")]
		[TestCase("Infinity")]
		[TestCase("1/0")]
		[TestCase("mm")]
		public void EmptyOrInvalidInputIsIgnored(string text)
		{
			Assert.That(TransformRules.ParseNumber(text, s => s == "1/0" ? double.PositiveInfinity : Arithmetic(s)), Is.Null);
		}

		[Test]
		public void WithoutAnEvaluatorOnlyPlainNumbersAreAccepted()
		{
			Assert.That(TransformRules.ParseNumber("20+5"), Is.Null);
			Assert.That(TransformRules.ParseNumber("25"), Is.EqualTo(25));
		}

		[TestCase(12.346, "12.35")]
		[TestCase(-0.001, "0")]
		[TestCase(20.0, "20")]
		[TestCase(double.NaN, "")]
		public void ValuesAreShownWithAtMostTwoDecimals(double value, string expected)
		{
			Assert.That(TransformRules.Format(value), Is.EqualTo(expected));
		}

		[Test]
		public void TheSectionUsesOneTransformCommandAndTheAppsExpressionParser()
		{
			var source = Read("MatterControlLib/PartPreviewWindow/TransformSection.cs");
			Assert.That(Regex.Matches(source, "UndoBuffer\\.AddAndDo\\(").Count, Is.EqualTo(1));
			Assert.That(source, Does.Contain("new TransformCommand(transformData)"));
			Assert.That(source, Does.Contain("new Expression(text).calculate()"));
			Assert.That(source, Does.Contain("serializationKey: ExpandedKey"));
		}

		// ------------------------------------------------------------------------------------------------------------
		// Mirror Copy

		[Test]
		public void TheCopyTouchesTheOriginalOnThePlusSide()
		{
			Assert.That(MirrorCopyRules.Offset(10, 30), Is.EqualTo(20));
			var (min, max) = MirrorCopyRules.CopyRange(10, 30);
			Assert.That(min, Is.EqualTo(30), "touches the original's far face");
			Assert.That(max, Is.EqualTo(50));

			// moving by the offset and reflecting about the moved box's own centre is the reflection in the far face
			double movedMin = 10 + MirrorCopyRules.Offset(10, 30), movedMax = 30 + MirrorCopyRules.Offset(10, 30);
			double centre = (movedMin + movedMax) / 2;
			Assert.That(Math.Min(2 * centre - movedMin, 2 * centre - movedMax), Is.EqualTo(min));
			Assert.That(Math.Max(2 * centre - movedMin, 2 * centre - movedMax), Is.EqualTo(max));

			// a point 2 mm inside the far face lands 2 mm beyond it
			Assert.That(2 * centre - (28 + MirrorCopyRules.Offset(10, 30)), Is.EqualTo(32));
		}

		[Test]
		public void ZCopiesGoOnTopNeverUnderTheBed()
		{
			var (min, _) = MirrorCopyRules.CopyRange(0, 12);
			Assert.That(min, Is.EqualTo(12));
		}

		[TestCase(5, 5)]
		[TestCase(5, 4)]
		[TestCase(double.NaN, 1)]
		public void AFlatSelectionHasNoOffset(double min, double max)
		{
			Assert.That(MirrorCopyRules.Offset(min, max), Is.EqualTo(0));
		}

		[TestCase(null, MirrorCopyAxis.X)]
		[TestCase("", MirrorCopyAxis.X)]
		[TestCase("y", MirrorCopyAxis.Y)]
		[TestCase("Z", MirrorCopyAxis.Z)]
		[TestCase("Q", MirrorCopyAxis.X)]
		[TestCase("7", MirrorCopyAxis.X)]
		public void TheLastAxisIsReadBack(string stored, MirrorCopyAxis expected)
		{
			Assert.That(MirrorCopyRules.Parse(stored), Is.EqualTo(expected));
		}

		[Test]
		public void MirrorCopyIsOneInsertOfAMirrorOperation()
		{
			var source = Read("MatterControlLib/PartPreviewWindow/View3D/Actions/MirrorCopy.cs");
			Assert.That(Regex.Matches(source, "UndoBuffer\\.AddAndDo\\(").Count, Is.EqualTo(1));
			Assert.That(source, Does.Contain("new InsertCommand(scene, mirror)"));
			Assert.That(source, Does.Contain("new MirrorObject3D_2()"));
			Assert.That(source, Does.Contain("scene.SelectedItem = mirror;"));
		}

		// ------------------------------------------------------------------------------------------------------------
		// keyboard shortcuts

		[Test]
		public void EveryListedShortcutIsHandled()
		{
			var files = new Dictionary<string, string>
			{
				[ShortcutTable.ApplicationFile] = Read("MatterControlLib/ApplicationView/Application.cs"),
				[ShortcutTable.View3DFile] = Read("MatterControlLib/PartPreviewWindow/View3D/View3DWidget.cs"),
			};

			foreach (var entry in ShortcutTable.All)
			{
				Assert.That(files.ContainsKey(entry.SourceFile), Is.True, entry.Keys);
				Assert.That(files[entry.SourceFile], Does.Contain(entry.Token), $"{entry.Keys}: {entry.Action}");
				Assert.That(ShortcutTable.Groups, Does.Contain(entry.Group), entry.Keys);
			}
		}

		[Test]
		public void TheNewShortcutsAreGatedByTheAdvancedTools()
		{
			var application = Read("MatterControlLib/ApplicationView/Application.cs");

			// Cmd/Ctrl + K, Cmd/Ctrl + D: EnableAdvancedTools; S and ?: Advanced mode as well
			Assert.That(application, Does.Contain("&& keyEvent.KeyCode == Keys.K\n\t\t\t\t\t&& AdvancedTools.IsEnabled").Or.Contain("&& keyEvent.KeyCode == Keys.K\r\n\t\t\t\t\t&& AdvancedTools.IsEnabled"));
			var d = application.IndexOf("case Keys.D:", StringComparison.Ordinal);
			Assert.That(application.Substring(d, 400), Does.Contain("AdvancedTools.IsEnabled"));
			var s = application.IndexOf("case 's':", StringComparison.Ordinal);
			Assert.That(application.Substring(s, 400), Does.Contain("AdvancedTools.InAdvancedMode"));
			var q = application.IndexOf("case '?':", StringComparison.Ordinal);
			Assert.That(application.Substring(q - 300, 300), Does.Contain("AdvancedTools.InAdvancedMode"));

			Assert.That(ShortcutTable.Shown(false).Any(e => e.AdvancedTools), Is.False);
			Assert.That(ShortcutTable.Shown(true).Count(), Is.EqualTo(ShortcutTable.All.Count));
		}

		[Test]
		public void TheNewKeysWereFreeBefore()
		{
			// each new key appears once as a case label: nothing else in Application.cs already used it
			var application = Read("MatterControlLib/ApplicationView/Application.cs");
			Assert.That(Regex.Matches(application, "case Keys\\.K:").Count, Is.EqualTo(1));
			Assert.That(Regex.Matches(application, "case Keys\\.D:").Count, Is.EqualTo(1));
			Assert.That(Regex.Matches(application, "case 's':").Count, Is.EqualTo(1));
			Assert.That(Regex.Matches(application, "case '\\?':").Count, Is.EqualTo(1));
		}

		[Test]
		public void ShortcutsUseTheOneNotation()
		{
			foreach (var entry in ShortcutTable.All)
			{
				Assert.That(KeyHintRules.ForSystem(entry.Keys, false), Is.EqualTo(entry.Keys), entry.Keys);
			}

			Assert.That(KeyHintRules.ForSystem(ShortcutTable.KeysFor("Duplicate the selection"), true), Is.EqualTo("Cmd + D"));
		}
	}
}
