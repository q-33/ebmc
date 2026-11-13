/*
Copyright (c) 2018, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ClipperLib;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.Operations;
using MatterHackers.MatterControl.PartPreviewWindow.View3D;
using MatterHackers.PolygonMesh;
using MatterHackers.PolygonMesh.Csg;
using MatterHackers.PolygonMesh.Processors;
using MatterHackers.VectorMath;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Polygons = System.Collections.Generic.List<System.Collections.Generic.List<ClipperLib.IntPoint>>;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>EBMC Edition: which side of a Plane Cut is kept.</summary>
	public enum PlaneCutKeep
	{
		/// <summary>Everything below the cut (the stock behaviour and the default).</summary>
		Bottom,

		/// <summary>Everything above the cut.</summary>
		Top,

		/// <summary>Both sides, as two separate parts.</summary>
		Both,
	}

	public class PlaneCutObject3D : OperationSourceContainerObject3D, IPropertyGridModifier
	{
		public PlaneCutObject3D()
		{
			Name = "Plane Cut".Localize();
		}


		[Slider(1, 200, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression CutHeight { get; set; } = 10;

		// EBMC: files saved before this existed have no value and load as Bottom, the old behaviour
		[EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
		[Description("Which part to keep. Both makes two separate parts; use Apply then Ungroup to move them apart.")]
		[JsonConverter(typeof(StringEnumConverter))]
		public PlaneCutKeep Keep { get; set; } = PlaneCutKeep.Bottom;

		/// <summary>
		/// EBMC: a new cut starts at half the height of what it cuts instead of a fixed 10 mm (which can be above or
		/// below the whole part). Only on first applying; saved cuts keep their height.
		/// </summary>
		public override void WrapSelectedItemAndSelect(InteractiveScene scene)
		{
			var selectedItem = scene?.SelectedItem;
			if (selectedItem != null)
			{
				var bounds = selectedItem.GetAxisAlignedBoundingBox();
				if (bounds.ZSize > 0
					&& !double.IsInfinity(bounds.ZSize)
					&& !double.IsNaN(bounds.ZSize))
				{
					CutHeight = Math.Round((bounds.MinXYZ.Z + bounds.MaxXYZ.Z) / 2, 2);
				}
			}

			base.WrapSelectedItemAndSelect(scene);
		}

		private double cutMargin = .01;

		public Mesh Cut(IObject3D item)
		{
			return Cut(item, keepTop: false);
		}

		public Mesh Cut(IObject3D item, bool keepTop)
		{
			// EBMC: copy the faces so cutting never changes the source mesh (both sides are cut from the same source)
			var mesh = new Mesh(item.Mesh.Vertices, new FaceList(item.Mesh.Faces.Select(f => new Face(f.v0, f.v1, f.v2, f.normal)).ToList()));

			var itemMatrix = item.WorldMatrix(SourceContainer);
			mesh.Transform(itemMatrix);

			var cutHeight = CutHeight.Value(this);

			// calculate and add the PWN face from the loops
			var cutPlane = new Plane(Vector3.UnitZ, new Vector3(0, 0, cutHeight));
			var slice = SliceLayer.CreateSlice(mesh, cutPlane);

			// copy every face that is on or below the cut plane
			// cut the faces at the cut plane
			mesh.Split(new Plane(Vector3.UnitZ, cutHeight), cutMargin, cleanAndMerge: false);

			if (keepTop)
			{
				// remove every face below the cut plane
				RemoveFacesBeyondCut(mesh, keepTop: true);

				// the cap faces down on the top part
				var cap = slice.AsVertices().TriangulateFaces(null, null, cutHeight);
				cap.ReverseFaces();
				mesh.CopyFaces(cap);
			}
			else
			{
				// remove every face above the cut plane
				RemoveFacesBeyondCut(mesh, keepTop: false);

				slice.AsVertices().TriangulateFaces(null, mesh, cutHeight);
			}

			mesh.Transform(itemMatrix.Inverted);

			return mesh;
		}

		private void RemoveFacesBeyondCut(Mesh mesh, bool keepTop)
		{
			var newVertices = new List<Vector3Float>();
			var newFaces = new List<Face>();
			var facesToRemove = new HashSet<int>();

			var cutRemove = CutHeight.Value(this) - cutMargin;
			var cutRemoveBelow = CutHeight.Value(this) + cutMargin;
			for (int i = 0; i < mesh.Faces.Count; i++)
			{
				var face = mesh.Faces[i];

				if (!keepTop
					&& mesh.Vertices[face.v0].Z >= cutRemove
					&& mesh.Vertices[face.v1].Z >= cutRemove
					&& mesh.Vertices[face.v2].Z >= cutRemove)
				{
					// record the face for removal
					facesToRemove.Add(i);
				}
				else if (keepTop
					&& mesh.Vertices[face.v0].Z <= cutRemoveBelow
					&& mesh.Vertices[face.v1].Z <= cutRemoveBelow
					&& mesh.Vertices[face.v2].Z <= cutRemoveBelow)
				{
					facesToRemove.Add(i);
				}
			}

			// make a new list of all the faces we are keeping
			var keptFaces = new List<Face>();
			for (int i = 0; i < mesh.Faces.Count; i++)
			{
				if (!facesToRemove.Contains(i))
				{
					keptFaces.Add(mesh.Faces[i]);
				}
			}

			var vertexCount = mesh.Vertices.Count;

			// add the new vertices
			mesh.Vertices.AddRange(newVertices);

			// add the new faces (have to make the vertex indices to the new vertices
			foreach (var newFace in newFaces)
			{
				Face faceNewIndices = newFace;
				faceNewIndices.v0 += vertexCount;
				faceNewIndices.v1 += vertexCount;
				faceNewIndices.v2 += vertexCount;
				keptFaces.Add(faceNewIndices);
			}

			mesh.Faces = new FaceList(keptFaces);

			mesh.CleanAndMerge();
		}

        public override AxisAlignedBoundingBox GetAxisAlignedBoundingBox(Matrix4X4 matrix)
        {
            var aabb = base.GetAxisAlignedBoundingBox(matrix);
			return aabb;
        }

        public override Task Rebuild()
		{
			this.DebugDepth("Rebuild");

			var rebuildLocks = this.RebuilLockAll();

			return TaskBuilder(
				"Plane Cut".Localize(),
				(reporter, cancellationToken) =>
				{
					// EBMC: an empty source used to throw here (First() on nothing) and the rebuild locks were never
					// released, leaving the object stuck; always release them
					try
					{
						var newChildren = new List<Object3D>();
						var root = SourceContainer.Children.FirstOrDefault();
						root = root == null ? SourceContainer : root;
						foreach (var sourceItem in SourceContainer.VisibleMeshes())
						{
							void AddPart(bool keepTop)
							{
								var newMesh = new Object3D()
								{
									Mesh = Cut(sourceItem, keepTop),
									OwnerID = sourceItem.ID
								};
								newMesh.CopyWorldProperties(sourceItem, root, Object3DPropertyFlags.All);
								newChildren.Add(newMesh);
							}

							if (Keep != PlaneCutKeep.Top)
							{
								AddPart(keepTop: false);
							}

							if (Keep != PlaneCutKeep.Bottom)
							{
								AddPart(keepTop: true);
							}
						}

						var sourceContainer = SourceContainer;
						this.Children.Modify(list =>
						{
							list.Clear();
							list.Add(sourceContainer);
							foreach (var child in newChildren)
							{
								list.Add(child);
							}
							sourceContainer.Visible = newChildren.Count == 0;
						});
					}
					catch (Exception e)
					{
						SubtractObject3D_2.ShowSourceAfterFailure(this);
						if (!cancellationToken.IsCancellationRequested)
						{
							DesignNotices.OperationFailed("Plane Cut".Localize(), e);
						}
					}
					finally
					{
						UiThread.RunOnIdle(() =>
						{
							rebuildLocks.Dispose();
							Invalidate(InvalidateType.DisplayValues);
							this.CancelAllParentBuilding();
							Parent?.Invalidate(new InvalidateArgs(this, InvalidateType.Children));
						});
					}

					return Task.CompletedTask;
				});
		}

		public void UpdateControls(PublicPropertyChange change)
		{
		}
	}

	public static class CutExtensionMethods
	{
		public static Polygons CreateUnion(this Polygons polygons, Polygons other)
		{
			Clipper clipper = new Clipper();
			clipper.AddPaths(polygons, PolyType.ptSubject, true);
			clipper.AddPaths(other, PolyType.ptSubject, true);

			Polygons ret = new Polygons();
			clipper.Execute(ClipType.ctUnion, ret, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
			return ret;
		}
	}
}