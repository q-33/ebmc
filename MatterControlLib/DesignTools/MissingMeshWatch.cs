using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>
	/// EBMC Edition: a part (club shape or exact solid) whose saved mesh is missing and that will rebuild itself once it
	/// is in an open tab's scene.
	/// </summary>
	internal interface IRestoresMissingMesh
	{
		/// <summary>
		/// Called on the UI thread when an open scene may have gained parts. Returns true when the part needs no more
		/// looks (it has a mesh, it started a rebuild, or its rule says not to); false while it is not in an open scene yet.
		/// </summary>
		bool TryRestoreMissingMesh();
	}

	/// <summary>
	/// EBMC Edition: waits for meshless parts to appear in an open scene, without polling. A part that is not in an open
	/// scene after loading is remembered (weakly, so thumbnails and off-scene copies are simply collected) and looked at
	/// again whenever a workspace is added or restored, a workspace's scene loads, or its parts change. Bounded work: one
	/// pass over the remembered parts per such event, coalesced to one per idle.
	/// </summary>
	internal static class MissingMeshWatch
	{
		private static readonly List<WeakReference<IRestoresMissingMesh>> Pending = new List<WeakReference<IRestoresMissingMesh>>();

		private static readonly ConditionalWeakTable<object, object> HookedScenes = new ConditionalWeakTable<object, object>();

		private static bool hooked;

		private static bool checkQueued;

		/// <summary>Remembers <paramref name="item"/> until it is in an open scene (UI thread).</summary>
		public static void Add(IRestoresMissingMesh item)
		{
			if (!Pending.Any(r => r.TryGetTarget(out var existing) && ReferenceEquals(existing, item)))
			{
				Pending.Add(new WeakReference<IRestoresMissingMesh>(item));
			}

			EnsureHooked();
		}

		private static void EnsureHooked()
		{
			var controller = ApplicationController.Instance;
			if (controller == null)
			{
				return;
			}

			if (!hooked)
			{
				hooked = true;
				controller.WorkspacesChanged += (s, e) =>
				{
					HookWorkspace(e.Workspace);
					QueueCheck();
				};
			}

			foreach (var workspace in controller.Workspaces.ToList())
			{
				HookWorkspace(workspace);
			}
		}

		private static void HookWorkspace(PartWorkspace workspace)
		{
			var sceneContext = workspace?.SceneContext;
			var scene = sceneContext?.Scene;
			if (sceneContext == null
				|| scene == null
				|| HookedScenes.TryGetValue(sceneContext, out _))
			{
				return;
			}

			HookedScenes.Add(sceneContext, null);

			sceneContext.SceneLoaded += (s, e) => QueueCheck();
			scene.Invalidated += (s, e) =>
			{
				if (Pending.Count > 0
					&& e.InvalidateType.HasFlag(InvalidateType.Children))
				{
					QueueCheck();
				}
			};
		}

		private static void QueueCheck()
		{
			if (checkQueued
				|| Pending.Count == 0)
			{
				return;
			}

			checkQueued = true;
			UiThread.RunOnIdle(() =>
			{
				checkQueued = false;
				foreach (var reference in Pending.ToList())
				{
					if (!reference.TryGetTarget(out var item)
						|| item.TryRestoreMissingMesh())
					{
						Pending.Remove(reference);
					}
				}
			});
		}
	}
}
