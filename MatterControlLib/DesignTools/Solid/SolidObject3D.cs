using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.PolygonMesh;
using Newtonsoft.Json;

namespace MatterHackers.MatterControl.DesignTools.Solid
{
	/// <summary>
	/// EBMC Edition: base of the exact-solid objects. Each one is an ordinary Object3D whose Mesh comes from the solid
	/// kernel; everything downstream (selection, handles, groups and mesh booleans, save, slicing) only ever sees that
	/// mesh. The parameters are saved in the .mcx file and the mesh is saved as an asset like any other part's, so a
	/// copy of the app without the kernel, or with the EnableSolidModeling switch off, still opens the file and shows
	/// (and prints) the last saved mesh. Every kernel use checks SolidModeling.IsEnabled, which reads the OEM switch
	/// before it ever loads the native library.
	/// </summary>
	/// <remarks>
	/// Rebuilds run on a background task (with the usual progress row and Cancel), one at a time per object: a change
	/// that arrives while a build is running is coalesced into one follow-up build with the newest values. The new
	/// mesh is put in place on the UI thread. When the kernel refuses, the previous mesh stays and a plain message is
	/// shown. The Task returned by Rebuild completes after the mesh is in place, which the size handles rely on to
	/// keep the opposite side still.
	/// </remarks>
	public abstract class SolidObject3D : Object3D, ISolidShapeSource
	{
		private readonly object buildGate = new object();

		private Func<OcctShape> pendingBuild;

		private List<TaskCompletionSource<bool>> waiting = new List<TaskCompletionSource<bool>>();

		private bool workerRunning;

		private string lastNotice;

		private DateTime lastNoticeTime;

		/// <summary>What the messages call this shape.</summary>
		[JsonIgnore]
		internal abstract SolidShapeKind Kind { get; }

		/// <summary>Title of the running-task row while the kernel works.</summary>
		[JsonIgnore]
		protected abstract string BuildTitle { get; }

		/// <summary>
		/// Reads and clamps the parameters on the calling thread and returns the build to run on the worker (which must
		/// only use the captured values), or null when the parameters cannot be built (after reporting why).
		/// </summary>
		private protected abstract Func<OcctShape> PrepareBuild(ref bool valuesChanged, out SolidProblem problem);

		/// <summary>
		/// The exact shape for STEP export, made from the current values, in this object's own coordinates (the same
		/// ones as its mesh). Null when the values cannot be built. Run the returned function off the UI thread.
		/// </summary>
		Func<OcctShape> ISolidShapeSource.ExportShape()
		{
			bool valuesChanged = false;
			return PrepareBuild(ref valuesChanged, out _);
		}

		/// <summary>The face id per triangle and edge polylines of the current mesh (memory only; null until built here).</summary>
		[JsonIgnore]
		public SolidMeshResult SolidMesh { get; private set; }

		/// <summary>True while a kernel build for this object is queued or running.</summary>
		[JsonIgnore]
		public bool IsBuilding
		{
			get
			{
				lock (buildGate)
				{
					return workerRunning;
				}
			}
		}

		/// <summary>
		/// After this object is read from a file or copied: once loading has finished, a solid whose saved mesh could not
		/// be found is rebuilt from its parameters (on the UI thread, only when solid modelling is on). Reading Mesh
		/// itself never starts a build; a reader of a missing mesh just gets null.
		/// </summary>
		[OnDeserialized]
		internal void SolidOnDeserialized(StreamingContext context)
		{
			UiThread.RunOnIdle(() => RestoreMissingMesh(10), 1);
		}

		private void RestoreMissingMesh(int triesLeft)
		{
			var mesh = base.Mesh;
			if (mesh != null && mesh != Object3D.FileMissingMesh)
			{
				return;
			}

			if (mesh == null && !string.IsNullOrEmpty(MeshPath) && triesLeft > 0)
			{
				// the saved mesh is probably still loading; look again shortly
				UiThread.RunOnIdle(() => RestoreMissingMesh(triesLeft - 1), 1);
				return;
			}

			if (SolidModeling.IsEnabled && !IsBuilding)
			{
				Rebuild();
			}
		}

		public override async void OnInvalidate(InvalidateArgs invalidateArgs)
		{
			if (invalidateArgs.InvalidateType.HasFlag(InvalidateType.Properties) && invalidateArgs.Source == this)
			{
				await Rebuild();
			}
			else if (Expressions.NeedRebuild(this, invalidateArgs))
			{
				await Rebuild();
			}
			else
			{
				base.OnInvalidate(invalidateArgs);
			}
		}

		public override Task Rebuild()
		{
			this.DebugDepth("Rebuild");

			bool valuesChanged = false;
			var build = PrepareBuild(ref valuesChanged, out var problem);
			Invalidate(InvalidateType.DisplayValues);

			// One gate for everything (OEM switch first, so a stock build never loads the native library): without it a
			// solid is a plain saved mesh that nothing can change
			if (!SolidModeling.IsEnabled)
			{
				return base.Rebuild();
			}

			if (build == null)
			{
				Report(problem);
				return base.Rebuild();
			}

			// completed on the UI thread after the mesh is in place; continuations never run inline on a worker
			var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			bool startWorker;
			lock (buildGate)
			{
				pendingBuild = build;
				waiting.Add(done);
				startWorker = !workerRunning;
				workerRunning = true;
			}

			if (startWorker)
			{
				StartWorker();
			}

			return done.Task;
		}

		private void StartWorker()
		{
			ApplicationController.Instance.Tasks.Execute(BuildTitle, this, (reporter, cancellationTokenSource) => Work(reporter, cancellationTokenSource));
		}

		/// <summary>
		/// Builds the mesh for an object that is not in a scene yet (Create), off the calling thread. Unlike Rebuild it
		/// does not need the UI thread to finish.
		/// </summary>
		protected async Task BuildDetached()
		{
			bool valuesChanged = false;
			var build = PrepareBuild(ref valuesChanged, out _);
			if (build == null || !SolidModeling.IsEnabled)
			{
				return;
			}

			var result = await Task.Run(() => SolidBuilders.Build(build, default));
			if (result.Succeeded)
			{
				var solidMesh = SolidMesher.ToMesh(result.Tessellation);
				using (RebuildLock())
				{
					Mesh = solidMesh.Mesh;
				}

				SolidMesh = solidMesh;
			}
			else
			{
				Report(result.Problem);
			}
		}

		/// <summary>
		/// The build loop: one build at a time for this object, always with the newest values. Whatever happens (cancel,
		/// an exception anywhere), the loop ends with workerRunning reset and every waiting Rebuild task completed; values
		/// that arrived during a cancelled build get a fresh worker.
		/// </summary>
		private async Task Work(Action<double, string> reporter, CancellationTokenSource cancellation)
		{
			List<TaskCompletionSource<bool>> current = null;
			bool failed = false;
			try
			{
				while (!cancellation.IsCancellationRequested)
				{
					Func<OcctShape> build;
					lock (buildGate)
					{
						build = pendingBuild;
						pendingBuild = null;
						if (build == null)
						{
							break;
						}

						current = waiting;
						waiting = new List<TaskCompletionSource<bool>>();
					}

					reporter?.Invoke(0, null);
					var result = SolidBuilders.Build(build, cancellation.Token);
					Trace.WriteLine($"{BuildTitle}: {result.Problem}, {result.Tessellation?.TriangleCount ?? 0} triangles, {result.ShapeMilliseconds + result.TessellateMilliseconds:F1} ms");

					if (result.Problem == SolidProblem.Cancelled)
					{
						break;
					}

					lock (buildGate)
					{
						if (pendingBuild != null)
						{
							// newer values arrived while this one was building; build those instead of showing this one
							waiting.InsertRange(0, current);
							current = null;
							continue;
						}
					}

					var solidMesh = result.Succeeded ? SolidMesher.ToMesh(result.Tessellation) : null;
					reporter?.Invoke(1, null);

					var batch = current;
					var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
					UiThread.RunOnIdle(() =>
					{
						try
						{
							Apply(result, solidMesh);
						}
						catch (Exception ex)
						{
							Trace.WriteLine(BuildTitle + ": could not show the new shape: " + ex.Message);
						}

						Complete(batch);
						applied.TrySetResult(true);
					});

					current = null;
					await applied.Task;
				}
			}
			catch (Exception ex)
			{
				failed = true;
				Trace.WriteLine(BuildTitle + ": build failed: " + ex);
			}
			finally
			{
				List<TaskCompletionSource<bool>> leftover;
				bool restart;
				lock (buildGate)
				{
					// after a cancel, values set during the cancelled build still need a build; after a failure, stop
					restart = !failed && pendingBuild != null;
					if (restart)
					{
						leftover = current ?? new List<TaskCompletionSource<bool>>();
					}
					else
					{
						leftover = waiting;
						if (current != null)
						{
							leftover.AddRange(current);
						}

						waiting = new List<TaskCompletionSource<bool>>();
						pendingBuild = null;
						workerRunning = false;
					}
				}

				UiThread.RunOnIdle(() => Complete(leftover));

				if (failed)
				{
					Report(SolidProblem.KernelRefused);
				}

				if (restart)
				{
					UiThread.RunOnIdle(StartWorker);
				}
			}
		}

		private static void Complete(List<TaskCompletionSource<bool>> batch)
		{
			foreach (var done in batch)
			{
				done.TrySetResult(true);
			}
		}

		/// <summary>Runs on the UI thread.</summary>
		private void Apply(SolidBuildResult result, SolidMeshResult solidMesh)
		{
			if (solidMesh == null)
			{
				Report(result.Problem);
				return;
			}

			using (RebuildLock())
			{
				using (new CenterAndHeightMaintainer(this))
				{
					Mesh = solidMesh.Mesh;
				}
			}

			SolidMesh = solidMesh;
			lastNotice = null;

			// if any of our parents are rebuilding cancel it as we just changed and they need to start over
			this.CancelAllParentBuilding();
			Parent?.Invalidate(new InvalidateArgs(this, InvalidateType.Mesh));
		}

		/// <summary>Shows the plain message for a problem; the same message is not repeated within a few seconds (dragging a handle).</summary>
		private void Report(SolidProblem problem)
		{
			var message = SolidMessages.For(problem, Kind, s => s.Localize());
			if (message == null)
			{
				return;
			}

			var now = DateTime.UtcNow;
			if (message == lastNotice && (now - lastNoticeTime).TotalSeconds < 5)
			{
				return;
			}

			lastNotice = message;
			lastNoticeTime = now;
			DesignNotices.SolidProblem(message);
		}
	}
}
