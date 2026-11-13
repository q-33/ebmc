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

		/// <summary>
		/// Writes the STEP file. The export dialog calls this on a worker thread: the scene is only read here (each
		/// solid's values are read without writing anything back), and the kernel work follows on the same thread.
		/// Never throws; every failure, and a cancel, comes back as a message for the dialog to show.
		/// </summary>
		public async Task<List<ValidationError>> Generate(IEnumerable<ILibraryItem> libraryItems, string outputPath, Action<double, string> progress, CancellationToken cancellationToken)
		{
			try
			{
				var roots = Roots(libraryItems);
				var parts = roots?.Select(Collect).ToList();
				if (!Enabled || parts == null || parts.Count == 0 || parts.Any(p => p == null))
				{
					return Error("Only exact solids (Solid Box, Solid Cylinder, Plate with Hole, STEP parts) can be saved as STEP.".Localize(), "");
				}

				var all = parts.SelectMany(p => p).ToList();
				var builds = new List<(Part part, Func<OcctShape> make, double[] matrix)>();
				var missing = new List<string>();
				foreach (var part in all)
				{
					var make = part.Source.ExportShape(out var problemMessage);
					if (make == null)
					{
						// the same plain message the part itself shows
						missing.Add(part.Name + ": " + problemMessage);
					}
					else
					{
						builds.Add((part, make, SolidMesher.ToKernelMatrix(part.Matrix)));
					}
				}

				if (missing.Count > 0)
				{
					return Error("These parts could not be made as exact solids, so nothing was saved.".Localize(), string.Join("\n", missing));
				}

				return await Task.Run(() => Write(builds, outputPath, progress, cancellationToken));
			}
			catch (OperationCanceledException)
			{
				return Cancelled();
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("STEP export: " + ex);
				return Error(SolidMessages.For(SolidProblem.StepNotWritten, SolidShapeKind.Box, s => s.Localize(), Path.GetFileName(outputPath ?? "")), "");
			}
		}

		private static List<ValidationError> Write(List<(Part part, Func<OcctShape> make, double[] matrix)> builds, string outputPath, Action<double, string> progress, CancellationToken cancellationToken)
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

				cancellationToken.ThrowIfCancellationRequested();
				using (var assembled = SolidBuilders.Assemble(solids, holes))
				{
					SolidBuilders.WriteStep(assembled, outputPath);
				}

				progress?.Invoke(1, "Exporting".Localize());
				return null;
			}
			catch (OperationCanceledException)
			{
				return Cancelled();
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("STEP export: " + ex.Message);
				return Error(SolidMessages.For(SolidProblem.StepNotWritten, SolidShapeKind.Box, s => s.Localize(), Path.GetFileName(outputPath ?? "")), "");
			}
			finally
			{
				foreach (var shape in solids.Concat(holes))
				{
					shape.Dispose();
				}
			}
		}

		/// <summary>Not an error, but not a success either: the dialog says no file was written.</summary>
		private static List<ValidationError> Cancelled()
		{
			return new List<ValidationError>
			{
				new ValidationError(StepExportRules.CancelledId)
				{
					Error = "STEP export cancelled. No file was written.".Localize(),
					Details = "",
					ErrorLevel = ValidationErrorLevel.Warning,
				}
			};
		}

		private static List<ValidationError> Error(string message, string details)
		{
			return new List<ValidationError>
			{
				new ValidationError(StepExportRules.FailedId)
				{
					Error = message,
					Details = details,
				}
			};
		}
	}
}
