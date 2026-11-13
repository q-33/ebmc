using System;
using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.MatterControl.SlicerConfiguration;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.Library
{
	/// <summary>
	/// EBMC Edition: after model files are added to a bed, offers to scale a model that is a suspicious size (made in
	/// inches, or bigger than the printer's bed). Off unless <see cref="OemSettings.OfferModelSizeFix"/> is set. The
	/// decision is <see cref="ModelFit"/>, the wording <see cref="DesignMessages"/>. It only moves and scales the model
	/// in the scene, as one undoable step; it has nothing to do with slicing.
	/// </summary>
	public static class ModelSizeCheck
	{
		/// <summary>
		/// Checks the newly added models (call on the UI thread, after they are in the scene) and shows one prompt for
		/// the first model with a size problem.
		/// </summary>
		public static void Offer(ISceneContext sceneContext, IEnumerable<IObject3D> addedModels)
		{
			if (!OemSettings.Instance.OfferModelSizeFix
				|| sceneContext?.Scene == null
				|| addedModels == null)
			{
				return;
			}

			var scene = sceneContext.Scene;
			bool onPrinter = sceneContext.Printer != null;

			double bedWidth = 0, bedDepth = 0, buildHeight = 0;
			bool roundBed = false;
			if (onPrinter)
			{
				bedWidth = sceneContext.ViewerVolume.X;
				bedDepth = sceneContext.ViewerVolume.Y;
				buildHeight = sceneContext.BuildHeight;
				roundBed = sceneContext.BedShape == BedShape.Circular;
			}

			foreach (var item in addedModels)
			{
				if (item == null
					|| !scene.Children.Contains(item))
				{
					continue;
				}

				var bounds = item.GetAxisAlignedBoundingBox();

				// on a round bed the footprint itself must fit the circle (a 100 mm disc fits a 100 mm bed even though its
				// bounding box's diagonal does not)
				double footprint = roundBed ? FootprintDiameter(item, bounds.Center.X, bounds.Center.Y) : 0;
				var advice = ModelFit.Advise(bounds.XSize, bounds.YSize, bounds.ZSize, onPrinter, bedWidth, bedDepth, buildHeight, roundBed, footprint);
				if (advice.Issue == ModelFitIssue.None)
				{
					continue;
				}

				string message;
				switch (advice.Issue)
				{
					case ModelFitIssue.Tiny:
						message = DesignMessages.ModelTiny(advice.LargestSizeMm, s => s.Localize());
						break;

					case ModelFitIssue.TooBigForBed:
						message = DesignMessages.ModelTooBigForBed(bedWidth, bedDepth, buildHeight, s => s.Localize(), roundBed);
						break;

					default:
						message = DesignMessages.ModelHuge(advice.LargestSizeMm, s => s.Localize());
						break;
				}

				DialogWindow.Show(new ModelSizePage(scene, item, message, advice.Choices));

				// one prompt at a time
				return;
			}
		}

		/// <summary>The diameter, about (centerX, centerY), of the circle holding every vertex of the item's visible meshes.</summary>
		private static double FootprintDiameter(IObject3D item, double centerX, double centerY)
		{
			IEnumerable<(double, double)> Points()
			{
				foreach (var meshItem in item.VisibleMeshes())
				{
					var matrix = meshItem.WorldMatrix();
					foreach (var vertex in meshItem.Mesh.Vertices)
					{
						var world = new Vector3(vertex).Transform(matrix);
						yield return (world.X, world.Y);
					}
				}
			}

			return ModelFit.FootprintDiameter(Points(), centerX, centerY);
		}

		/// <summary>
		/// Scales the model uniformly about its centre, keeping its bottom where it was (on the bed), as one undoable
		/// transform.
		/// </summary>
		public static void ApplyScale(InteractiveScene scene, IObject3D item, double scale)
		{
			if (scene == null
				|| item == null
				|| scale <= 0
				|| double.IsNaN(scale)
				|| double.IsInfinity(scale)
				|| !scene.Children.Contains(item))
			{
				// removed (or undone) while the prompt was open
				return;
			}

			var before = item.Matrix;
			var bounds = item.GetAxisAlignedBoundingBox();
			var center = bounds.Center;
			var bottom = bounds.MinXYZ.Z;

			var after = before
				* Matrix4X4.CreateTranslation(-center.X, -center.Y, -bottom)
				* Matrix4X4.CreateScale(scale)
				* Matrix4X4.CreateTranslation(center.X, center.Y, bottom);

			scene.UndoBuffer.AddAndDo(new TransformCommand(item, before, after));
			scene.Invalidate(new InvalidateArgs(item, InvalidateType.Matrix));
		}

		private class ModelSizePage : DialogPage
		{
			public ModelSizePage(InteractiveScene scene, IObject3D item, string message, IReadOnlyList<ModelFitChoice> choices)
				: base(DesignMessages.LeaveIt(s => s.Localize()))
			{
				this.WindowTitle = ApplicationController.Instance.ProductName + " - " + DesignMessages.ModelSizeTitle(s => s.Localize());
				this.HeaderText = item.Name;
				this.WindowSize = new Vector2(480 * GuiWidget.DeviceScale, 240 * GuiWidget.DeviceScale);

				contentRow.AddChild(new WrappedTextWidget(message, pointSize: 12, textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Stretch,
				});

				bool first = true;
				foreach (var choice in choices)
				{
					var button = theme.CreateDialogButton(DesignMessages.ScaleChoice(choice, s => s.Localize()));
					var scale = choice.Scale;
					button.Click += (s, e) =>
					{
						UiThread.RunOnIdle(() =>
						{
							ApplyScale(scene, item, scale);
							this.DialogWindow?.Close();
						});
					};

					this.AddPageAction(button, first);
					first = false;
				}
			}
		}
	}
}
