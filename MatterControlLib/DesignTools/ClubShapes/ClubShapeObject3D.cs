using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.DataConverters3D.UndoCommands;
using MatterHackers.Localizations;
using MatterHackers.PolygonMesh;
using MatterHackers.VectorMath;
using Newtonsoft.Json;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	/// <summary>
	/// EBMC Edition: the OEM switch for the club shapes and starters (EnableClubShapes; stock builds leave it off).
	/// </summary>
	public static class ClubShapesFeature
	{
		public static bool IsEnabled => SettingsManagement.OemSettings.Instance.EnableClubShapes;
	}

	/// <summary>
	/// What a club shape's build makes: the pure bodies (ClubMesh) plus, for starters with text, agg-sharp meshes made from
	/// the text outlines. Built on a worker; the object never has children.
	/// </summary>
	public sealed class ClubBuild
	{
		public ClubMesh Bodies { get; } = new ClubMesh();

		public List<Mesh> Extra { get; } = new List<Mesh>();

		/// <summary>One closed agg-sharp mesh, centred on X and Y with its bottom at Z = 0.</summary>
		public Mesh ToMesh()
		{
			var mesh = new Mesh(Bodies.Positions.ToArray(), Bodies.Triangles.ToArray());
			foreach (var extra in Extra.Where(e => e != null && e.Faces.Count > 0))
			{
				mesh.CopyFaces(extra);
			}

			var bounds = mesh.GetAxisAlignedBoundingBox();
			if (bounds.XSize >= 0)
			{
				mesh.Translate(new Vector3(-bounds.Center.X, -bounds.Center.Y, -bounds.MinXYZ.Z));
			}

			return mesh;
		}
	}

	/// <summary>
	/// EBMC Edition: base of the club shapes and starters. Each is an ordinary Object3D whose mesh is rebuilt from its
	/// parameters (saved in the .mcx; the mesh is saved as an asset like any part's), so a file reopens with the same
	/// part, and the slicer only ever sees a normal closed mesh. The object has no children; Apply turns it into a plain
	/// mesh part.
	/// </summary>
	/// <remarks>
	/// Rebuilds run on a background task (with the running-task row and its Cancel), one at a time per object; a change
	/// that arrives during a build is coalesced into one follow-up build with the newest values. The new mesh is put in
	/// place on the UI thread and the Task returned by Rebuild completes after that, which the size handles rely on.
	/// Values that cannot be built are clamped and a plain notice says what changed.
	/// </remarks>
	public abstract class ClubShapeObject3D : Object3D, IRestoresMissingMesh
	{
		private readonly object buildGate = new object();

		private Func<CancellationToken, ClubBuild> pendingBuild;

		private List<TaskCompletionSource<bool>> waiting = new List<TaskCompletionSource<bool>>();

		private bool workerRunning;

		// set when the last build failed and left no mesh; automatic rebuilds wait for a property change
		private bool buildFailed;

		private string lastNotice;

		private DateTime lastNoticeTime;

		/// <summary>Title of the running-task row while it builds.</summary>
		[JsonIgnore]
		protected abstract string BuildTitle { get; }

		/// <summary>
		/// Reads and clamps the parameters (UI thread; clamped values are stored back, as the other primitives do) and
		/// returns the build to run on the worker, which must only use the captured values.
		/// </summary>
		protected abstract Func<CancellationToken, ClubBuild> PrepareBuild(ref bool valuesChanged, StarterProblems problems);

		public override bool CanApply => true;

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
		/// Reading the mesh never starts a build (it used to, which made a shape whose first build failed rebuild on every
		/// read: every frame, and from slicing). A part read from a file whose mesh is missing is rebuilt once loading has
		/// finished (<see cref="ClubOnDeserialized"/>); a reader of a missing mesh just gets null.
		/// </summary>
		public override Mesh Mesh
		{
			get => base.Mesh;

			set => base.Mesh = value;
		}

		/// <summary>
		/// After this object is read from a file or copied: once loading has finished, a shape whose saved mesh could not
		/// be found is rebuilt from its values, only for a part in an open tab's scene, and not again after a failed
		/// build until a value changes (ClubBuildRules.AutoRebuild). The same rule as SolidObject3D.
		/// </summary>
		[OnDeserialized]
		internal void ClubOnDeserialized(StreamingContext context)
		{
			UiThread.RunOnIdle(() => RestoreMissingMesh(10), 1);
		}

		private void RestoreMissingMesh(int triesLeft)
		{
			var mesh = base.Mesh;
			bool hasMesh = mesh != null && mesh != Object3D.FileMissingMesh;
			if (hasMesh)
			{
				return;
			}

			if (mesh == null && !string.IsNullOrEmpty(MeshPath) && triesLeft > 0)
			{
				// the saved mesh is probably still loading; look again shortly
				UiThread.RunOnIdle(() => RestoreMissingMesh(triesLeft - 1), 1);
				return;
			}

			if (!IsInOpenScene())
			{
				// not in an open tab yet (a slow start restoring several tabs, or a thumbnail / off-scene copy): look
				// again when a scene gains parts, with no time limit and no polling (MissingMeshWatch)
				MissingMeshWatch.Add(this);
				return;
			}

			RebuildIfAllowed();
		}

		bool IRestoresMissingMesh.TryRestoreMissingMesh()
		{
			var mesh = base.Mesh;
			if (mesh != null && mesh != Object3D.FileMissingMesh)
			{
				return true;
			}

			if (!IsInOpenScene())
			{
				return false;
			}

			RebuildIfAllowed();
			return true;
		}

		private void RebuildIfAllowed()
		{
			var mesh = base.Mesh;
			bool hasMesh = mesh != null && mesh != Object3D.FileMissingMesh;
			bool inOpenScene = true;

			bool failedBefore;
			lock (buildGate)
			{
				failedBefore = buildFailed;
			}

			if (ClubBuildRules.AutoRebuild(hasMesh, failedBefore, IsBuilding, inOpenScene))
			{
				Rebuild();
			}
		}

		/// <summary>True when this object's root is the scene of an open design or printer tab.</summary>
		private bool IsInOpenScene()
		{
			var chain = this.AncestorsAndSelf().ToList();
			if (chain.Count < 2)
			{
				return false;
			}

			var topLevel = chain[chain.Count - 2];
			return ApplicationController.Instance?.Workspaces?.Any(w => w.SceneContext?.Scene?.Children.Contains(topLevel) == true) == true;
		}

		/// <summary>
		/// Replaces this object with a plain mesh part (the parameters are dropped). While a build is running, or before
		/// there is a mesh, it changes nothing and says why (it never removes the part).
		/// </summary>
		public override void Apply(UndoBuffer undoBuffer)
		{
			switch (ClubBuildRules.ForApply(IsBuilding, base.Mesh != null))
			{
				case ClubApplyDecision.WaitForBuild:
					Report("The shape is still being made. Apply it again in a moment.".Localize());
					return;

				case ClubApplyDecision.NothingToApply:
					Report("The shape has not been made yet, so there is nothing to apply.".Localize());
					return;
			}

			using (RebuildLock())
			{
				var meshOnly = new Object3D()
				{
					Mesh = this.Mesh.Copy(CancellationToken.None)
				};

				meshOnly.CopyProperties(this, Object3DPropertyFlags.All);
				undoBuffer.AddAndDo(new ReplaceCommand(new[] { this }, new[] { meshOnly }));
			}

			Invalidate(InvalidateType.Children);
		}

		public override async void OnInvalidate(InvalidateArgs invalidateArgs)
		{
			if (invalidateArgs.InvalidateType.HasFlag(InvalidateType.Properties) && invalidateArgs.Source == this)
			{
				// a value changed: a failed build may succeed now
				lock (buildGate)
				{
					buildFailed = false;
				}

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

		/// <summary>
		/// Builds the mesh for an object that is not in a scene yet (Create), off the calling thread; does not need the
		/// UI thread to finish.
		/// </summary>
		protected async Task BuildDetached()
		{
			bool valuesChanged = false;
			var problems = new StarterProblems();
			var build = PrepareBuild(ref valuesChanged, problems);
			var mesh = await Task.Run(() => build(CancellationToken.None).ToMesh());
			using (RebuildLock())
			{
				Mesh = mesh;
			}
		}

		/// <summary>Builds synchronously on the calling thread (for checks and tools; the app uses Rebuild).</summary>
		public Mesh BuildNow(StarterProblems problems = null)
		{
			bool valuesChanged = false;
			var build = PrepareBuild(ref valuesChanged, problems ?? new StarterProblems());
			return build(CancellationToken.None).ToMesh();
		}

		public override Task Rebuild()
		{
			this.DebugDepth("Rebuild");

			bool valuesChanged = false;
			var problems = new StarterProblems();
			notANumberReplaced = false;
			var build = PrepareBuild(ref valuesChanged, problems);
			if (notANumberReplaced)
			{
				problems.Add(ClubValueRules.NotANumberNotice);
			}

			Invalidate(InvalidateType.DisplayValues);
			foreach (var message in problems.Messages)
			{
				Report(message.Localize());
			}

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
			try
			{
				ApplicationController.Instance.Tasks.Execute(BuildTitle, this, (reporter, cancellation) => Work(reporter, cancellation));
			}
			catch (Exception ex)
			{
				Trace.WriteLine(BuildTitle + ": could not start a build: " + ex.Message);
				List<TaskCompletionSource<bool>> leftover;
				lock (buildGate)
				{
					leftover = waiting;
					waiting = new List<TaskCompletionSource<bool>>();
					pendingBuild = null;
					workerRunning = false;
				}

				UiThread.RunOnIdle(() => Complete(leftover));
			}
		}

		/// <summary>
		/// One build at a time, always with the newest values; ends with every waiting Rebuild completed. The builds take
		/// milliseconds and are not cancellable (ClubBuildRules.Cancellable): the task row's Cancel is ignored, so the
		/// mesh always ends up matching the values.
		/// </summary>
		private async Task Work(Action<double, string> reporter, CancellationTokenSource cancellation)
		{
			List<TaskCompletionSource<bool>> current = null;
			bool failed = false;
			var token = ClubBuildRules.Cancellable ? cancellation.Token : CancellationToken.None;
			try
			{
				while (!token.IsCancellationRequested)
				{
					Func<CancellationToken, ClubBuild> build;
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
					var watch = Stopwatch.StartNew();
					Mesh mesh;
					try
					{
						mesh = build(token).ToMesh();
					}
					catch (OperationCanceledException)
					{
						break;
					}

					Trace.WriteLine($"{BuildTitle}: {mesh.Faces.Count} triangles, {watch.Elapsed.TotalMilliseconds:F1} ms");

					lock (buildGate)
					{
						if (pendingBuild != null)
						{
							// newer values arrived while this one was building; build those instead
							waiting.InsertRange(0, current);
							current = null;
							continue;
						}
					}

					reporter?.Invoke(1, null);

					var batch = current;
					var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
					UiThread.RunOnIdle(() =>
					{
						try
						{
							Apply(mesh);
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
					// a newer build queued during this one (also after a failure) still has to be made
					restart = ClubBuildRules.Restart(failed, pendingBuild != null);
					buildFailed = failed && base.Mesh == null;
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
					Report("This shape could not be made with these values. Try other values.".Localize());
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

		/// <summary>
		/// Runs on the UI thread. The new mesh always goes onto the part, as SolidObject3D does: a part deleted while it was
		/// building can come back with Undo (the same object, re-added; agg-sharp never clears Parent) and must then show
		/// the shape that matches its values. Only telling the parents is skipped while it is out of the scene.
		/// </summary>
		private void Apply(Mesh mesh)
		{
			var (assignMesh, notifyParents) = Solid.SolidApplyRule.ForFinishedBuild(
				succeeded: mesh != null,
				hasParent: Parent != null,
				inParentsChildren: Parent?.Children.Contains(this) == true);

			if (assignMesh)
			{
				using (RebuildLock())
				{
					using (new CenterAndHeightMaintainer(this))
					{
						Mesh = mesh;
					}
				}
			}

			if (notifyParents)
			{
				this.CancelAllParentBuilding();
				Parent?.Invalidate(new InvalidateArgs(this, InvalidateType.Mesh));
			}
		}

		/// <summary>Set by <see cref="ClubInput"/> when a field held NaN or ∞ and was put back to its default.</summary>
		[JsonIgnore]
		internal bool notANumberReplaced;

		/// <summary>A plain notice; the same one is not repeated within a few seconds (dragging a slider).</summary>
		private void Report(string message)
		{
			UiThread.RunOnIdle(() =>
			{
				var now = DateTime.UtcNow;
				if (message == lastNotice && (now - lastNoticeTime).TotalSeconds < 5)
				{
					return;
				}

				lastNotice = message;
				lastNoticeTime = now;
				DesignNotices.ShapeNotice(message);
			});
		}
	}

	/// <summary>
	/// EBMC Edition: the club shapes' field reads. Like ClampIfNotCalculated, plus: a value that is not a number ("NaN")
	/// or infinite becomes the field's default (the value a new part of that kind has) and the shape shows a notice.
	/// </summary>
	public static class ClubInput
	{
		private static readonly Dictionary<(Type, string), double> Defaults = new Dictionary<(Type, string), double>();

		public static double ClampFinite(this DoubleOrExpression field, ClubShapeObject3D item, double min, double max, ref bool valuesChanged)
		{
			var (value, replaced) = ClubValueRules.Finite(field.Value(item), DefaultFor(item, field, min));
			if (replaced)
			{
				item.notANumberReplaced = true;
				valuesChanged = true;
				if (!field.IsEquation)
				{
					field.Expression = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
				}
				else
				{
					// an equation that evaluates to NaN keeps its text; build with the default
					return Math.Max(min, Math.Min(max, value));
				}
			}

			return field.ClampIfNotCalculated(item, min, max, ref valuesChanged);
		}

		/// <summary>The int fields cannot hold NaN; this keeps one call shape for every club field.</summary>
		public static int ClampFinite(this IntOrExpression field, ClubShapeObject3D item, int min, int max, ref bool valuesChanged)
		{
			return field.ClampIfNotCalculated(item, min, max, ref valuesChanged);
		}

		/// <summary>The default of the property holding <paramref name="field"/>, read from a new object of the same kind.</summary>
		private static double DefaultFor(ClubShapeObject3D item, DoubleOrExpression field, double fallback)
		{
			var type = item.GetType();
			var property = type.GetProperties().FirstOrDefault(p => p.PropertyType == typeof(DoubleOrExpression) && ReferenceEquals(p.GetValue(item), field));
			if (property == null)
			{
				return fallback;
			}

			lock (Defaults)
			{
				if (!Defaults.TryGetValue((type, property.Name), out var value))
				{
					value = fallback;
					try
					{
						if (Activator.CreateInstance(type) is ClubShapeObject3D fresh
							&& property.GetValue(fresh) is DoubleOrExpression freshField
							&& double.TryParse(freshField.Expression, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
							&& !double.IsNaN(parsed)
							&& !double.IsInfinity(parsed))
						{
							value = parsed;
						}
					}
					catch
					{
					}

					Defaults[(type, property.Name)] = value;
				}

				return value;
			}
		}
	}
}
