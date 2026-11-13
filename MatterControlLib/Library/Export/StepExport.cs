using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.Solid;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.Library.Export
{
	/// <summary>
	/// EBMC Edition: lets the export dialog (ExportPrintItemPage) hide a plugin that cannot export the chosen items.
	/// </summary>
	public interface IExportItemFilter
	{
		bool CanExport(IEnumerable<ILibraryItem> libraryItems);
	}

	/// <summary>
	/// EBMC Edition: saves exact solids as a STEP file (AP214, mm). Offered only when solid modelling is on, the kernel
	/// loaded, and everything being exported is an exact solid (Solid Box, Solid Cylinder, Plate with Hole, STEP part)
	/// or a plain group of them. Several solids are written as one compound with the bodies kept separate; if any of
	/// them is set to Hole, the solids are fused and the holes cut from them, as Combine would. This is a design file
	/// format only; it has nothing to do with G-code.
	/// </summary>
	public class StepExport : IExportPlugin, IExportItemFilter
	{
		public int Priority => 4;

		public string ButtonText => "STEP File".Localize();

		public string FileExtension => ".step";

		public string ExtensionFilter => "Save as STEP|*.step";

		public ImageBuffer Icon { get; } = StaticData.Instance.LoadIcon(Path.Combine("filetypes", "stl.png"));

		public void Initialize(PrinterConfig printer)
		{
		}

		public bool Enabled => SolidModeling.IsEnabled;

		// empty: the dialog leaves the button out instead of showing it disabled
		public string DisabledReason => "";

		public bool ExportPossible(ILibraryAsset libraryItem) => Enabled;

		public bool CanExport(IEnumerable<ILibraryItem> libraryItems)
		{
			// called while the export dialog lists every format: never let anything here break the dialog
			try
			{
				if (!Enabled)
				{
					return false;
				}

				var roots = Roots(libraryItems);
				return StepExportRules.Applies(roots?.Select(root => Collect(root) != null));
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("STEP export check: " + ex.Message);
				return false;
			}
		}

		/// <summary>The objects behind the items, or null if any item is not an object already in memory.</summary>
		private static List<IObject3D> Roots(IEnumerable<ILibraryItem> libraryItems)
		{
			var roots = new List<IObject3D>();
			foreach (var item in libraryItems ?? Enumerable.Empty<ILibraryItem>())
			{
				// only objects already in memory (a selection or the bed): never load a library file on the UI thread, and
				// never ask a generator (it would make a new object, and run a kernel build, each time the dialog opens)
				if (!(item is ILibraryObject3D objectItem)
					|| item is GeneratorItem)
				{
					return null;
				}

				var task = objectItem.GetObject3D(null);
				if (task.Status != TaskStatus.RanToCompletion || task.Result == null)
				{
					return null;
				}

				roots.Add(task.Result);
			}

			return roots;
		}

		/// <summary>One solid to export: where it is (relative to the export root) and whether it is a hole.</summary>
		private sealed class Part
		{
			public ISolidShapeSource Source;
			public Matrix4X4 Matrix;
			public bool IsHole;
			public string Name;
		}

		/// <summary>
		/// Every solid under root, or null when root holds anything else that would be exported (a mesh part, or an
		/// operation such as Combine whose visible result is a mesh). The decision is StepExportRules.Collect.
		/// </summary>
		private static List<Part> Collect(IObject3D root)
		{
			var found = StepExportRules.Collect<IObject3D>(
				root,
				item => item is ISolidShapeSource,
				item => item.Mesh != null && item.Mesh.Faces.Count > 0,
				item => item.Children.Where(c => c.Visible));

			return found?.Select(f =>
			{
				// world matrix relative to the export root (root included), and Hole if the part or any parent is a hole
				var matrix = f.item.Matrix;
				var isHole = f.item.OutputType == PrintOutputTypes.Hole;
				for (int i = f.ancestors.Count - 1; i >= 0; i--)
				{
					matrix *= f.ancestors[i].Matrix;
					isHole |= f.ancestors[i].OutputType == PrintOutputTypes.Hole;
				}

				return new Part { Source = (ISolidShapeSource)f.item, Matrix = matrix, IsHole = isHole, Name = f.item.Name };
			}).ToList();
		}

		public Task<List<ValidationError>> Generate(IEnumerable<ILibraryItem> libraryItems, string outputPath, Action<double, string> progress, CancellationToken cancellationToken)
		{
			// gather on the calling thread (reads the scene), then do the kernel work off it
			var roots = Roots(libraryItems);
			var parts = roots?.Select(Collect).ToList();
			if (!Enabled || parts == null || parts.Count == 0 || parts.Any(p => p == null))
			{
				return Task.FromResult(Error("Only exact solids (Solid Box, Solid Cylinder, Plate with Hole, STEP parts) can be saved as STEP.".Localize(), ""));
			}

			var all = parts.SelectMany(p => p).ToList();
			var builds = all.Select(p => (part: p, make: p.Source.ExportShape(), matrix: SolidMesher.ToKernelMatrix(p.Matrix))).ToList();
			var missing = builds.Where(b => b.make == null).Select(b => b.part.Name).ToList();
			if (missing.Count > 0)
			{
				return Task.FromResult(Error("These parts could not be made as exact solids. Check their sizes, or for a STEP part whether its original file is on this computer.".Localize(), string.Join("\n", missing)));
			}

			return Task.Run(() =>
			{
				var solids = new List<OcctShape>();
				var holes = new List<OcctShape>();
				try
				{
					int done = 0;
					foreach (var (part, make, kernelMatrix) in builds)
					{
						cancellationToken.ThrowIfCancellationRequested();
						using (var local = make())
						{
							(part.IsHole ? holes : solids).Add(SolidBuilders.Transform(local, kernelMatrix));
						}

						progress?.Invoke(0.8 * ++done / builds.Count, "Exporting".Localize());
					}

					if (solids.Count == 0)
					{
						return Error("There is no solid to save: every part is set to Hole.".Localize(), "");
					}

					using (var assembled = SolidBuilders.Assemble(solids, holes))
					{
						SolidBuilders.WriteStep(assembled, outputPath);
					}

					progress?.Invoke(1, "Exporting".Localize());
					return null;
				}
				catch (OperationCanceledException)
				{
					return null;
				}
				catch (SolidBuildException ex)
				{
					System.Diagnostics.Trace.WriteLine("STEP export: " + ex.Message);
					return Error(SolidMessages.For(ex.Problem == SolidProblem.StepNotWritten ? ex.Problem : SolidProblem.StepNotWritten, SolidShapeKind.Box, s => s.Localize(), Path.GetFileName(outputPath)), "");
				}
				catch (Exception ex)
				{
					System.Diagnostics.Trace.WriteLine("STEP export: " + ex.Message);
					return Error(SolidMessages.For(SolidProblem.StepNotWritten, SolidShapeKind.Box, s => s.Localize(), Path.GetFileName(outputPath)), "");
				}
				finally
				{
					foreach (var shape in solids.Concat(holes))
					{
						shape.Dispose();
					}
				}
			});
		}

		private static List<ValidationError> Error(string message, string details)
		{
			return new List<ValidationError>
			{
				new ValidationError("ItemToSTEPExportInvalid")
				{
					Error = message,
					Details = details,
				}
			};
		}
	}
}
