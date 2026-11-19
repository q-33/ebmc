using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.Laser;

namespace MatterHackers.MatterControl.Library.Export
{
	/// <summary>
	/// EBMC Edition: the laser data files (StaticData/Laser, written by Tools/EBMC/generate_ebmc_files.py). Read once; a
	/// missing or broken file gives an empty list.
	/// </summary>
	internal static class LaserDataFiles
	{
		private static List<LaserMachineProfile> machines;

		private static List<LaserMaterialPreset> materials;

		private static string Read(string name)
		{
			try
			{
				var path = Path.Combine("Laser", name);
				return StaticData.Instance.FileExists(path) ? StaticData.Instance.ReadAllText(path) : null;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("Laser data: " + ex.Message);
				return null;
			}
		}

		/// <summary>What went wrong reading the files (a repeated id, an entry that could not be read), for the page to show.</summary>
		public static List<string> LoadNotes { get; } = new List<string>();

		public static List<LaserMachineProfile> Machines => machines ?? (machines = LaserData.LoadMachines(Read("machines.json"), LoadNotes));

		public static List<LaserMaterialPreset> Materials => materials ?? (materials = LaserData.LoadMaterials(Read("materials.json"), LoadNotes));
	}

	/// <summary>
	/// EBMC Edition: the laser G-code values typed this session, per operation (Cut and Score each remember their own). They
	/// start empty: nothing is filled in that could be taken for a recommended setting.
	/// </summary>
	internal static class LaserGcodeSession
	{
		public static string MachineId;

		public static string Operation = "Cut";

		public static string MaterialNote = "";

		/// <summary>The text typed in power, speed and passes, by operation.</summary>
		public static readonly Dictionary<string, (string power, string speed, string passes)> Typed = new Dictionary<string, (string, string, string)>();
	}

	/// <summary>
	/// EBMC Edition: "G-code for laser (.nc)": the outline as a G-code file for a laser cutter (OemSettings.EnableLaserGcode,
	/// maintainers only). Only the Export for laser page uses it: it needs the machine, power, speed and passes typed there,
	/// so it is never listed in the general Export dialog (Enabled is false). The file is .nc, not the 3D printer's G-code file type, so it cannot be
	/// taken for a 3D-printer job; nothing here slices, sends or uses the 3D printer at all. A concrete class on purpose (see
	/// LaserSvgExport).
	/// </summary>
	public class LaserGcodeExport : IExportPlugin, IExportItemFilter
	{
		internal LaserExportChoice Choice { get; } = new LaserExportChoice();

		/// <summary>The machine and job values, set by the page before the export.</summary>
		internal LaserMachineProfile Machine { get; set; }

		internal LaserJobSettings Job { get; set; }

		public int Priority => 5;

		public string ButtonText => "G-code for laser (.nc)".Localize();

		public string FileExtension => ".nc";

		public string ExtensionFilter => "Save as laser G-code|*.nc";

		public ImageBuffer Icon { get; } = LaserExportCore.LoadIcon();

		public void Initialize(PrinterConfig printer)
		{
		}

		// never in the general Export dialog: only the Export for laser page, which has the job fields, uses this
		public bool Enabled => false;

		public string DisabledReason => "";

		public bool ExportPossible(ILibraryAsset libraryItem) => false;

		public bool CanExport(IEnumerable<ILibraryItem> libraryItems) => false;

		public Task<List<ValidationError>> Generate(IEnumerable<ILibraryItem> libraryItems, string outputPath, Action<double, string> progress, CancellationToken cancellationToken)
		{
			var machine = Machine;
			var job = Job;
			if (!LaserTools.GcodeAvailable
				|| machine == null
				|| job == null)
			{
				return Task.FromResult(new List<ValidationError>
				{
					new ValidationError(LaserExportRules.FailedId)
					{
						Error = "Laser G-code can only be saved from the Export for laser page, by a maintainer.".Localize(),
						Details = "",
					}
				});
			}

			return LaserExportCore.Generate(libraryItems, outputPath, progress, cancellationToken, Choice, (outline, title) =>
			{
				var result = LaserGcodeWriter.Write(outline, job, machine, cancellationToken);
				var output = new LaserWriteOutput { Text = result.Text };
				output.Errors.AddRange(result.Problems.Where(p => p.Level == LaserProblemLevel.Error).Select(p => p.Message));
				output.Notes.AddRange(result.Problems.Where(p => p.Level == LaserProblemLevel.Warning).Select(p => p.Message));
				if (result.Ok)
				{
					output.SummarySuffix = ", " + LaserGcodeWriter.Duration(result.EstimatedSeconds);
				}

				return output;
			});
		}
	}
}
