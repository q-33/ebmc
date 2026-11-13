using System;
using System.IO;
using System.Threading.Tasks;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.DesignTools.Solid;
using MatterHackers.MatterControl.Library;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: opens STEP files (.step, .stp) as <see cref="SolidImportObject3D"/> parts. Registered by
	/// ApplicationController only when solid modelling is on and the kernel loaded (SolidModeling.IsEnabled), so a
	/// build without the kernel does not offer STEP at all. On a printer tab the part is sliced as its mesh, like an STL.
	/// </summary>
	public class StepContentProvider : ISceneContentProvider
	{
		public static readonly string[] Extensions = { "step", "stp" };

		public Task<IObject3D> CreateItem(ILibraryItem item, Action<double, string> reporter)
		{
			return Task.Run<IObject3D>(async () =>
			{
				if (!(item is ILibraryAssetStream streamInterface))
				{
					return null;
				}

				var fileName = item is FileSystemFileItem fileItem ? Path.GetFileName(fileItem.FilePath) : streamInterface.FileName;

				// exactly one message per failed file: this one, not also the generic "Couldn't open"
				void Fail(SolidProblem problem)
				{
					DesignNotices.MarkReported(item);
					DesignNotices.SolidProblem(SolidMessages.For(problem, SolidShapeKind.Box, s => s.Localize(), fileName));
				}

				try
				{
					if (streamInterface.FileSize > SolidTolerance.MaxStepFileBytes)
					{
						Fail(SolidProblem.StepTooLarge);
						return null;
					}

					using (var memory = new MemoryStream())
					{
						using (var contentStream = await streamInterface.GetStream(reporter))
						{
							if (contentStream?.Stream == null)
							{
								Fail(SolidProblem.StepUnreadable);
								return null;
							}

							// the asset store hashes then copies the stream, so give it a seekable copy, but never an
							// unbounded one (the reported size can be unknown or wrong)
							if (!await CopyAtMost(contentStream.Stream, memory, SolidTolerance.MaxStepFileBytes))
							{
								Fail(SolidProblem.StepTooLarge);
								return null;
							}
						}

						memory.Position = 0;
						reporter?.Invoke(0.3, null);

						// the kernel work runs as a task the member can cancel from the running-tasks bar
						SolidImportObject3D loaded = null;
						var problem = SolidProblem.None;
						await ApplicationController.Instance.Tasks.Execute(
							"Opening STEP file".Localize(),
							null,
							async (taskReporter, cancellation) =>
							{
								(loaded, problem) = await SolidImportObject3D.Load(memory, fileName, cancellation.Token);
							});

						reporter?.Invoke(1, null);
						if (loaded == null)
						{
							if (problem == SolidProblem.Cancelled)
							{
								DesignNotices.MarkReported(item);
							}
							else
							{
								Fail(problem == SolidProblem.None ? SolidProblem.StepUnreadable : problem);
							}
						}

						return loaded;
					}
				}
				catch (Exception ex)
				{
					System.Diagnostics.Trace.WriteLine("STEP import: " + ex.Message);
					Fail(SolidProblem.StepUnreadable);
					return null;
				}
			});
		}

		private static async Task<bool> CopyAtMost(Stream source, Stream destination, long maxBytes)
		{
			var buffer = new byte[81920];
			long total = 0;
			int read;
			while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
			{
				total += read;
				if (total > maxBytes)
				{
					return false;
				}

				await destination.WriteAsync(buffer, 0, read);
			}

			return true;
		}

		/// <summary>
		/// Library thumbnails are always the plain icon: drawing one would mean importing and tessellating the whole
		/// STEP file (and copying it into the asset store) just because a folder was opened.
		/// </summary>
		public Task<ImageBuffer> GetThumbnail(ILibraryItem item, int width, int height)
		{
			return Task.FromResult(DefaultImage);
		}

		public ImageBuffer DefaultImage => StaticData.Instance.LoadIcon(Path.Combine("filetypes", "stl.png"));
	}
}
