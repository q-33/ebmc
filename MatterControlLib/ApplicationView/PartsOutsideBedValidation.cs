using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.MatterControl.SlicerConfiguration;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// The "Parts Outside the Bed" warning, or "Nothing Is Inside the Printable Area" error, with the names of the
	/// parts it is about.
	/// </summary>
	public class PartsOutsideBedValidationError : ValidationError
	{
		public PartsOutsideBedValidationError(string id, IReadOnlyList<string> partNames)
			: base(id)
		{
			this.PartNames = partNames;
		}

		/// <summary>
		/// The names of all outside parts (not only the ones listed in the details), in scene order.
		/// </summary>
		public IReadOnlyList<string> PartNames { get; }
	}

	/// <summary>
	/// Reports parts that the slicer will leave out because they are outside the printable area, instead of
	/// leaving them out silently. "Outside" is exactly what <see cref="PrinterExtensionMethods.PrintableItems"/>
	/// leaves out for position (<see cref="PrinterExtensionMethods.InsidePrintableArea"/>), so this report and the
	/// sliced G-code always agree. Wording and the warning/error decision are in <see cref="PartsOutsideBedMessages"/>.
	/// Only real parts count, on both sides: meshes whose output type is Default or Solid, the same types the slicer
	/// prints as solids (Slicer.GetSolidsForExtruder). Holes, supports, wipe towers and fuzzy-skin regions are
	/// ignored, so they are never named and never make the bed count as "something is inside".
	/// </summary>
	public static class PartsOutsideBedValidation
	{
		/// <summary>
		/// The entry for the printer's bed, or null when no real part is outside:
		/// - a warning (<see cref="ValidationErrors.PartsOutsideBed"/>) when some top-level parts are (partly) outside
		///   the printable area and a real part is inside;
		/// - an error when every real part is outside, so nothing would be printed. It uses the
		///   <see cref="ValidationErrors.NoPrintableParts"/> ID because it is a more specific "Empty Bed": any
		///   check that adds "Empty Bed" only when that ID is missing (OctoPrintValidation) does not add a second one.
		/// Cost: one walk of the scene's visible meshes using each mesh's cached world bounding box, the same work
		/// as one full PrintableItems enumeration, plus one bounding box per outside part; no mesh vertices are
		/// read once the bounds are cached (the 3D view asks for the same bounds every frame).
		/// </summary>
		public static PartsOutsideBedValidationError Check(PrinterConfig printer)
		{
			var scene = printer.Bed.Scene;

			bool anyInside = false;
			var outsideParts = new HashSet<IObject3D>();

			// The same items, in the same walk, as PrintableItems(scene)
			foreach (var item in scene.VisibleMeshes())
			{
				if (!PrinterExtensionMethods.IsPrintableCandidate(item))
				{
					continue;
				}

				var outputType = item.WorldOutputType();
				if (outputType != PrintOutputTypes.Default
					&& outputType != PrintOutputTypes.Solid)
				{
					continue;
				}

				if (printer.InsidePrintableArea(item))
				{
					anyInside = true;
				}
				else
				{
					// Report the part the user placed (a top-level child of the scene), not the mesh inside it
					outsideParts.Add(item.AncestorsAndSelf().FirstOrDefault(i => i.Parent == scene) ?? item);
				}
			}

			var level = PartsOutsideBedMessages.Classify(outsideParts.Count, anyInside);
			if (level == PartsOutsideBedLevel.None)
			{
				return null;
			}

			// List the parts in scene order
			var parts = scene.Children.Where(child => outsideParts.Contains(child)).ToList();
			if (parts.Count < outsideParts.Count)
			{
				parts = outsideParts.ToList();
			}

			// Advisory only (it chooses the advice and whether Arrange is offered, never what is printed)
			int partsThatFit = parts.Count(part => FitsOnBed(printer, part));

			var names = parts.Select(part => part.Name).ToList();

			var entry = new PartsOutsideBedValidationError(level == PartsOutsideBedLevel.Error ? ValidationErrors.NoPrintableParts : ValidationErrors.PartsOutsideBed, names)
			{
				Error = PartsOutsideBedMessages.Title(level, s => s.Localize()),
				Details = PartsOutsideBedMessages.Details(level, names, partsThatFit < parts.Count, s => s.Localize()),
				ErrorLevel = level == PartsOutsideBedLevel.Error ? ValidationErrorLevel.Error : ValidationErrorLevel.Warning,
			};

			if (PartsOutsideBedMessages.OfferArrange(level, partsThatFit))
			{
				entry.FixAction = new NamedAction()
				{
					// The same call as the bed menu's "Arrange All Parts"
					Title = "Arrange All Parts".Localize(),
					Action = () => UiThread.RunOnIdle(async () =>
					{
						await printer.Bed.Scene.AutoArrangeChildren(new Vector3(printer.Bed.BedCenter)).ConfigureAwait(false);
					}),
					IsEnabled = () => printer.Bed.EditableScene,
				};
			}

			return entry;
		}

		/// <summary>
		/// Adds the <see cref="Check"/> entry, if any, unless an entry with either of its IDs is already in the list.
		/// </summary>
		public static void AddValidationErrors(PrinterConfig printer, List<ValidationError> errors)
		{
			if (errors.Any(e => e.ID == ValidationErrors.PartsOutsideBed || e.ID == ValidationErrors.NoPrintableParts))
			{
				return;
			}

			if (Check(printer) is ValidationError entry)
			{
				errors.Add(entry);
			}
		}

		/// <summary>
		/// The "Parts Outside the Bed" warning cannot be dismissed: dismissing a warning is stored for good and for
		/// every printer, which would switch this protection off. Every reader and writer of the "Ignore_" settings
		/// goes through these two methods.
		/// </summary>
		public static bool CanDismiss(ValidationError error)
		{
			return error.ErrorLevel == ValidationErrorLevel.Warning
				&& error.ID != ValidationErrors.PartsOutsideBed;
		}

		/// <summary>
		/// True when the user dismissed this warning earlier. Always false for one that cannot be dismissed, even if
		/// an "Ignore_" setting was stored for it before.
		/// </summary>
		public static bool IsDismissed(ValidationError error)
		{
			return CanDismiss(error)
				&& UserSettings.Instance.get($"Ignore_{error.ID}") == "true";
		}

		/// <summary>
		/// True when exporting these items slices the printer's bed: GCodeExport slices Printer.Bed.Scene for an
		/// "mcx" source (and does not slice at all for a G-code source).
		/// </summary>
		public static bool IsBedSource(IEnumerable<ILibraryItem> libraryItems)
		{
			return libraryItems?.OfType<ILibraryAsset>().FirstOrDefault() is ILibraryAssetStream assetStream
				&& assetStream.ContentType == "mcx";
		}

		/// <summary>
		/// The "Nothing Is Inside the Printable Area" error for exporting a bed, or null. For export only the error
		/// is checked here; the warning is shown by the page or menu the export starts from.
		/// </summary>
		public static ValidationError ExportError(PrinterConfig printer, IEnumerable<ILibraryItem> libraryItems)
		{
			if (printer?.Bed?.Scene == null
				|| !IsBedSource(libraryItems))
			{
				return null;
			}

			var entry = Check(printer);
			return entry?.ErrorLevel == ValidationErrorLevel.Error ? entry : null;
		}

		private static bool FitsOnBed(PrinterConfig printer, IObject3D part)
		{
			var size = part.WorldAxisAlignedBoundingBox().Size;
			var bed = printer.Bed;

			// The same bed dimensions InsideBuildVolume compares against
			double width = bed.BedShape == BedShape.Rectangular ? printer.Settings.MeshAllowedBounds.Width : bed.ViewerVolume.X;
			double depth = bed.BedShape == BedShape.Rectangular ? printer.Settings.MeshAllowedBounds.Height : bed.ViewerVolume.Y;

			return size.X <= width
				&& size.Y <= depth
				&& (bed.BuildHeight <= 0 || size.Z < bed.BuildHeight);
		}

		/// <summary>
		/// Shows the "Parts Outside the Bed" warning after a slice, for the Slice and Re-Slice buttons. One instance
		/// per button: it is shown only after a successful slice, and only when the set of outside parts differs
		/// from the one this button showed last, so slicing again without moving anything does not repeat it.
		/// </summary>
		public class SliceNotice
		{
			private string lastShown;

			/// <param name="errors">The validation list the slice was started with.</param>
			/// <param name="sliceSucceeded">Whether the slice completed.</param>
			public void AfterSlice(IEnumerable<ValidationError> errors, bool sliceSucceeded)
			{
				if (!sliceSucceeded)
				{
					return;
				}

				var warning = errors?.OfType<PartsOutsideBedValidationError>().FirstOrDefault(e => e.ID == ValidationErrors.PartsOutsideBed);
				if (warning == null)
				{
					lastShown = null;
					return;
				}

				string partSet = string.Join("\n", warning.PartNames.OrderBy(name => name ?? ""));
				if (partSet == lastShown)
				{
					return;
				}

				lastShown = partSet;
				ApplicationController.Instance.ShowValidationErrors(
					"Slicing Warning".Localize(),
					new List<ValidationError> { warning },
					"Action Recommended".Localize());
			}
		}
	}
}
