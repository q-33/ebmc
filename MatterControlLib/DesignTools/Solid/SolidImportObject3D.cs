using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>
	/// The exact shape a solid object stands for, for STEP export. Implemented by the parametric solids and by imported
	/// STEP parts.
	/// </summary>
	internal interface ISolidShapeSource
	{
		/// <summary>
		/// Makes the shape in the object's own coordinates (those of its mesh); null when it cannot be made, with the
		/// plain message saying why.
		/// </summary>
		Func<OcctShape> ExportShape(out string problemMessage);
	}

	/// <summary>
	/// EBMC Edition: a part opened from a STEP file. The STEP file itself is kept in the app's asset store under a name
	/// made from its content (SHA-1), the part shows the kernel's tessellation of it, and the read-only values give its
	/// size, volume and face/edge counts as read from the file ("Original": scaling the part does not change them). The mesh is saved with the design like any part's, so the part still opens
	/// (and prints) without the kernel.
	/// </summary>
	public class SolidImportObject3D : Object3D, ISolidShapeSource
	{
		public SolidImportObject3D()
		{
			Name = "STEP Part".Localize();
		}

		/// <summary>The STEP file's name in the asset store ("sha1.step").</summary>
		[HideFromEditor]
		public string StepAsset { get; set; }

		/// <summary>The name of the file it was opened from (for messages).</summary>
		[HideFromEditor]
		public string SourceFileName { get; set; }

		[ReadOnly(true)]
		[DisplayName("Original Width (mm)")]
		public double SizeX { get; set; }

		[ReadOnly(true)]
		[DisplayName("Original Depth (mm)")]
		public double SizeY { get; set; }

		[ReadOnly(true)]
		[DisplayName("Original Height (mm)")]
		public double SizeZ { get; set; }

		[ReadOnly(true)]
		[DisplayName("Original Volume (mm³)")]
		public double Volume { get; set; }

		[ReadOnly(true)]
		[DisplayName("Faces")]
		public int FaceCount { get; set; }

		[ReadOnly(true)]
		[DisplayName("Edges")]
		public int EdgeCount { get; set; }

		/// <summary>Full path of the stored STEP file, or null if it is not on this computer.</summary>
		internal string StepAssetPath
		{
			get
			{
				if (string.IsNullOrEmpty(StepAsset) || string.IsNullOrEmpty(Object3D.AssetsPath))
				{
					return null;
				}

				var path = Path.Combine(Object3D.AssetsPath, Path.GetFileName(StepAsset));
				return File.Exists(path) ? path : null;
			}
		}

		Func<OcctShape> ISolidShapeSource.ExportShape(out string problemMessage)
		{
			var path = StepAssetPath;
			problemMessage = path == null ? "The original STEP file of this part is not on this computer.".Localize() : null;
			return path == null ? (Func<OcctShape>)null : () => SolidBuilders.ReadStep(path);
		}

		/// <summary>
		/// Reads a STEP stream: stores it in the asset store, then reads and tessellates it with the kernel. Call off the
		/// UI thread. Shows no message: on failure it returns the problem and the caller reports it once.
		/// </summary>
		public static async Task<(SolidImportObject3D item, SolidProblem problem)> Load(Stream stream, string fileName, CancellationToken cancellationToken)
		{
			if (!SolidModeling.IsEnabled)
			{
				return (null, SolidProblem.KernelUnavailable);
			}

			string assetName;
			try
			{
				Directory.CreateDirectory(Object3D.AssetsPath);
				assetName = await AssetObject3D.AssetManager.StoreStream(stream, ".step", false, cancellationToken, null);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("STEP import: could not store the file: " + ex.Message);
				return (null, SolidProblem.StepUnreadable);
			}

			var item = new SolidImportObject3D
			{
				StepAsset = assetName,
				SourceFileName = fileName,
			};

			var path = item.StepAssetPath;
			var result = path == null
				? SolidBuildResult.Failed(SolidProblem.StepUnreadable)
				: SolidBuilders.Build(() => SolidBuilders.ReadStep(path), cancellationToken, withProperties: true);

			if (!result.Succeeded)
			{
				System.Diagnostics.Trace.WriteLine("STEP import failed: " + result.Problem + " " + result.Detail);
				var problem = result.Problem == SolidProblem.Cancelled || result.Problem == SolidProblem.TooDetailed
					? result.Problem
					: SolidProblem.StepUnreadable;
				return (null, problem);
			}

			var solidMesh = SolidMesher.ToMesh(result.Tessellation);
			if (solidMesh.Mesh.Faces.Count == 0)
			{
				return (null, SolidProblem.StepUnreadable);
			}

			using (item.RebuildLock())
			{
				item.Mesh = solidMesh.Mesh;
			}

			if (!string.IsNullOrEmpty(fileName))
			{
				item.Name = Path.GetFileNameWithoutExtension(fileName);
			}

			var bounds = result.Bounds;
			item.SizeX = Math.Round(bounds[3] - bounds[0], 3);
			item.SizeY = Math.Round(bounds[4] - bounds[1], 3);
			item.SizeZ = Math.Round(bounds[5] - bounds[2], 3);
			item.Volume = Math.Round(result.Volume, 2);
			item.FaceCount = result.FaceCount;
			item.EdgeCount = result.EdgeCount;

			return (item, SolidProblem.None);
		}
	}
}
