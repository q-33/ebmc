using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.PolygonMesh;
using MatterHackers.MatterControl.DesignTools.ClubShapes;
using MatterHackers.MatterControl.DesignTools.Laser;
using MatterHackers.MatterControl.DesignTools.Solid;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.Library.Export
{
	/// <summary>
	/// EBMC Edition: the outline choice for a laser export. Method and height are the one shared session setting
	/// (<see cref="LaserExportSettings"/>), so the SVG and DXF formats and the laser page always agree; only the
	/// after-save callback belongs to this export.
	/// </summary>
	internal sealed class LaserExportChoice
	{
		public LaserOutlineMethod Method
		{
			get => LaserExportSettings.Method;
			set => LaserExportSettings.Set(value, LaserExportSettings.SliceHeight);
		}

		public double SliceHeight
		{
			get => LaserExportSettings.SliceHeight;
			set => LaserExportSettings.Set(LaserExportSettings.Method, value);
		}

		/// <summary>Called after a file is saved, with the plain summary ("Saved name.svg: 20 × 20 mm, 1 shape").</summary>
		public Action<string> Saved;
	}

	/// <summary>
	/// EBMC Edition: the outline choice (method and Slice at height value), one setting for this session shared by the
	/// export dialog's SVG and DXF formats and the laser page. Changed tells every open chooser to show the new value.
	/// </summary>
	internal static class LaserExportSettings
	{
		public static LaserOutlineMethod Method { get; private set; } = LaserOutlineMethod.Footprint;

		public static double SliceHeight { get; private set; } = 0.2;

		public static event Action Changed;

		public static void Set(LaserOutlineMethod method, double sliceHeight)
		{
			sliceHeight = LaserExportRules.CleanSliceHeight(sliceHeight);
			if (method == Method
				&& sliceHeight == SliceHeight)
			{
				return;
			}

			Method = method;
			SliceHeight = sliceHeight;
			Changed?.Invoke();
		}
	}

	/// <summary>
	/// EBMC Edition: reads the scene for the laser export. Only objects already in memory (a selection or the bed), never
	/// a library file or a generator; the scene is only read, never changed.
	/// </summary>
	internal static class LaserGeometrySource
	{
		/// <summary>agg's curve flattening scale for about 0.01 mm accuracy (tolerance is 0.5 / scale).</summary>
		private const double CurveScale = 50;

		/// <summary>The objects behind the items, or null if any item is not an object already in memory.</summary>
		public static List<IObject3D> Roots(IEnumerable<ILibraryItem> libraryItems)
		{
			var roots = new List<IObject3D>();
			foreach (var item in libraryItems ?? Enumerable.Empty<ILibraryItem>())
			{
				// as STEP export: never load a library file on the UI thread, never ask a generator for a new object
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

		/// <summary>
		/// The parts under root as builder inputs, in bed coordinates. Only reads the scene. It runs where Generate runs: on a
		/// worker thread (RunningTasksConfig.Execute wraps the export in Task.Run), while the scene may still be edited on
		/// the UI thread. Stock STL export reads the scene from the same worker thread and has the same exposure.
		/// </summary>
		public static void Collect(IObject3D root, List<LaserMeshInput> meshes, List<LaserPathInput> paths)
		{
			// a pure 2D drawing (MeshIsSolidObject false) lying flat gives its path; GetRawPath is never asked of a solid
			// path provider (an extrusion or image base), which goes through its mesh or its children instead
			var drawingLoops = new Dictionary<IObject3D, List<List<Pt>>>();
			bool IsFlatDrawing(IObject3D item)
			{
				if (!(item is IPathProvider provider)
					|| provider.MeshIsSolidObject
					|| !LaserExportRules.PathIsFlat(ToAffine(item.WorldMatrix())))
				{
					return false;
				}

				var raw = provider.GetRawPath();
				var loops = raw == null ? null : Loops(raw);
				if (loops == null || loops.Count == 0)
				{
					// no loop to cut: the walk falls back to the part's mesh, or its children
					return false;
				}

				drawingLoops[item] = loops;
				return true;
			}

			var found = LaserExportRules.Collect<IObject3D>(
				root,
				item => item.Mesh is Mesh mesh && mesh.Faces.Count > 0,
				IsFlatDrawing,
				item => item.Children.Where(c => c.Visible),
				RoleOf);

			foreach (var (item, kind, role) in found)
			{
				var affine = ToAffine(item.WorldMatrix());
				if (kind == LaserSourceKind.Path)
				{
					var bounds = WorldBounds(item);
					paths.Add(new LaserPathInput
					{
						Name = item.Name,
						Loops = drawingLoops[item],
						Affine = affine,
						Role = role,
						MinZ = bounds.MinXYZ.Z,
						MaxZ = bounds.MaxXYZ.Z,
						IsDrawing = true,
					});
					continue;
				}

				var m = item.Mesh;
				if (m == null)
				{
					continue;
				}

				var positions = new double[m.Vertices.Count * 3];
				for (int i = 0; i < m.Vertices.Count; i++)
				{
					positions[i * 3] = m.Vertices[i].X;
					positions[i * 3 + 1] = m.Vertices[i].Y;
					positions[i * 3 + 2] = m.Vertices[i].Z;
				}

				var triangles = new int[m.Faces.Count * 3];
				for (int i = 0; i < m.Faces.Count; i++)
				{
					triangles[i * 3] = m.Faces[i].v0;
					triangles[i * 3 + 1] = m.Faces[i].v1;
					triangles[i * 3 + 2] = m.Faces[i].v2;
				}

				meshes.Add(new LaserMeshInput
				{
					Name = item.Name,
					Positions = positions,
					Triangles = triangles,
					Affine = affine,
					Role = role,
				});
			}
		}

		/// <summary>Solid or Hole from the part and its parents; null (left out) for support, wipe tower, fuzzy and parts that are not saved.</summary>
		private static LaserRole? RoleOf(IObject3D item)
		{
			if (!item.WorldPersistable())
			{
				return null;
			}

			switch (item.WorldOutputType())
			{
				case PrintOutputTypes.Default:
				case PrintOutputTypes.Solid:
					return LaserRole.Solid;
				case PrintOutputTypes.Hole:
					return LaserRole.Hole;
				default:
					return null;
			}
		}

		public static double[] ToAffine(Matrix4X4 matrix) => SolidMesher.ToKernelMatrix(matrix);

		/// <summary>The part's box on the bed.</summary>
		public static AxisAlignedBoundingBox WorldBounds(IObject3D item)
		{
			return item.GetAxisAlignedBoundingBox(item.Parent == null ? Matrix4X4.Identity : item.Parent.WorldMatrix());
		}

		/// <summary>
		/// The loops of a path, curves flattened (as VertexSourceToClipperPolygons.CreatePolygons). Every run of three or
		/// more points is treated as closed, whether or not it ends with a close flag. Checked against the sources: the
		/// Clipper-built paths (CreateVertexStorage, PolygonToPathStorage), font glyphs, RoundedRect and Ellipse do emit
		/// close flags, but a path typed or pasted as an SVG string (CustomPathObject3D) need not; and MatterControl's own
		/// Extrude (CreatePolygons) ignores close flags and closes every run, so this matches the part's on-screen mesh.
		/// </summary>
		private static List<List<Pt>> Loops(IVertexSource path)
		{
			var loops = new List<List<Pt>>();
			List<Pt> current = null;
			foreach (var vertex in new FlattenCurves(path) { ResolutionScale = CurveScale }.Vertices())
			{
				if (vertex.Command == FlagsAndCommand.MoveTo
					|| vertex.IsLineTo)
				{
					if (vertex.Command == FlagsAndCommand.MoveTo)
					{
						current = null;
					}

					if (current == null)
					{
						current = new List<Pt>();
						loops.Add(current);
					}

					current.Add(new Pt(vertex.Position.X, vertex.Position.Y));
				}
				else if (vertex.Command != FlagsAndCommand.FlagNone)
				{
					current = null;
				}
			}

			return loops.Where(l => l.Count >= 3).ToList();
		}
	}

	/// <summary>EBMC Edition: what a laser file writer gives back: the text, or the reasons it refused, and notes.</summary>
	internal sealed class LaserWriteOutput
	{
		public string Text;

		public List<string> Errors = new List<string>();

		public List<string> Notes = new List<string>();

		/// <summary>Added to the saved message (", about 2 min").</summary>
		public string SummarySuffix = "";

		public static LaserWriteOutput Of(string text) => new LaserWriteOutput { Text = text };
	}

	/// <summary>EBMC Edition: the work shared by the laser exporters (SVG, DXF, laser G-code): a static helper, not a base class.</summary>
	internal static class LaserExportCore
	{
		public static ImageBuffer LoadIcon() => StaticData.Instance.LoadIcon(Path.Combine("filetypes", "stl.png"));

		public static bool CanExport(IEnumerable<ILibraryItem> libraryItems)
		{
			// called while the export dialog lists every format: never let anything here break the dialog
			try
			{
				return LaserTools.IsEnabled
					&& LaserExportRules.Applies(LaserGeometrySource.Roots(libraryItems));
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("Laser export check: " + ex.Message);
				return false;
			}
		}

		/// <summary>The outline choice under the format's button in the export dialog.</summary>
		public static GuiWidget OptionsPanel(RadioButton radioButton)
		{
			var panel = LaserOutlineChooser.Create(AppContext.Theme, () => radioButton.InvokeClick());
			panel.Margin = new BorderDouble(left: 40, bottom: 10);

			// any click on the panel picks this format, even on an option that is already chosen (MouseDown is raised on
			// the panel for every press inside it, after its children have had it)
			panel.MouseDown += (s, e) =>
			{
				if (!radioButton.Checked)
				{
					UiThread.RunOnIdle(() => radioButton.InvokeClick());
				}
			};

			return panel;
		}

		/// <summary>
		/// Writes the file. DoExport calls this inside RunningTasksConfig.Execute, which runs it on a worker thread, so the
		/// scene is read from that worker (as stock STL export does) and the outline is then worked out in Task.Run. Never
		/// throws; a failure, an empty outline (no file written) and a cancel come back as messages for the dialog.
		/// </summary>
		public static async Task<List<ValidationError>> Generate(
			IEnumerable<ILibraryItem> libraryItems,
			string outputPath,
			Action<double, string> progress,
			CancellationToken cancellationToken,
			LaserExportChoice choice,
			Func<LaserOutline, string, LaserWriteOutput> write)
		{
			try
			{
				var roots = LaserGeometrySource.Roots(libraryItems);
				if (!LaserTools.IsEnabled
					|| !LaserExportRules.Applies(roots))
				{
					return Error(LaserExportRules.FailedId, LaserExportRules.NotInMemoryMessage.Localize());
				}

				var meshes = new List<LaserMeshInput>();
				var paths = new List<LaserPathInput>();
				foreach (var root in roots)
				{
					LaserGeometrySource.Collect(root, meshes, paths);
				}

				var options = new LaserOutlineOptions
				{
					Method = choice.Method,
					SliceHeightAboveBottom = LaserExportRules.CleanSliceHeight(choice.SliceHeight),
				};

				progress?.Invoke(0.1, "Exporting".Localize());
				var outline = await Task.Run(() => LaserOutlineBuilder.Build(meshes, paths, options, cancellationToken), cancellationToken);
				cancellationToken.ThrowIfCancellationRequested();

				if (outline.IsEmpty)
				{
					return Error(LaserExportRules.EmptyId, LaserExportRules.EmptyMessage(options.Method, outline.Report).Localize());
				}

				var name = libraryItems?.FirstOrDefault()?.Name;
				var output = write(outline, string.IsNullOrEmpty(name) ? null : name);
				if (output.Errors.Count > 0)
				{
					// refused (laser G-code: power, speed, passes, size): nothing is written
					return output.Errors.Select(e => Error(LaserExportRules.FailedId, e.Localize())[0]).ToList();
				}

				File.WriteAllText(outputPath, output.Text, new UTF8Encoding(false));
				progress?.Invoke(1, "Exporting".Localize());

				choice.Saved?.Invoke(LaserExportRules.SavedMessage(Path.GetFileName(outputPath), outline) + output.SummarySuffix);

				var notes = LaserExportRules.Notes(outline.Report);
				notes.AddRange(output.Notes);
				return notes.Count == 0
					? null
					: notes.Select(note => new ValidationError(LaserExportRules.NoteId)
					{
						Error = note,
						Details = "",
						ErrorLevel = ValidationErrorLevel.Warning,
					}).ToList();
			}
			catch (OperationCanceledException)
			{
				return new List<ValidationError>
				{
					new ValidationError(LaserExportRules.CancelledId)
					{
						Error = LaserExportRules.CancelledMessage.Localize(),
						Details = "",
						ErrorLevel = ValidationErrorLevel.Warning,
					}
				};
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("Laser export: " + ex);
				return Error(LaserExportRules.FailedId, LaserExportRules.FailedMessage.Localize());
			}
		}

		private static List<ValidationError> Error(string id, string message)
		{
			return new List<ValidationError>
			{
				new ValidationError(id)
				{
					Error = message,
					Details = "",
				}
			};
		}
	}

	/// <summary>
	/// EBMC Edition: the "Outline" choice: seen from above, or slice at a height in mm. Shows and sets the one shared
	/// session setting (LaserExportSettings); every open chooser follows a change made in another.
	/// </summary>
	internal static class LaserOutlineChooser
	{
		public static GuiWidget Create(ThemeConfig theme, Action changed)
		{
			var column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};

			var siblings = new List<GuiWidget>();
			var fromAbove = new RadioButton(new RadioButtonViewText("Outline seen from above".Localize(), theme.TextColor, fontSize: theme.DefaultFontSize))
			{
				HAnchor = HAnchor.Left,
				Cursor = Cursors.Hand,
				Margin = new BorderDouble(0, 3),
				Checked = LaserExportSettings.Method == LaserOutlineMethod.Footprint,
				SiblingRadioButtonList = siblings,
				ToolTipText = "The shape of the parts as seen from directly above, holes that go all the way through included".Localize(),
			};
			siblings.Add(fromAbove);
			column.AddChild(fromAbove);

			var sliceRow = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Left | HAnchor.Fit,
				VAnchor = VAnchor.Fit,
			};
			column.AddChild(sliceRow);

			var slice = new RadioButton(new RadioButtonViewText("Slice at height".Localize(), theme.TextColor, fontSize: theme.DefaultFontSize))
			{
				VAnchor = VAnchor.Center,
				Cursor = Cursors.Hand,
				Margin = new BorderDouble(0, 3, 6, 3),
				Checked = LaserExportSettings.Method == LaserOutlineMethod.Section,
				SiblingRadioButtonList = siblings,
				ToolTipText = "The outline where a flat cut at this height above the bottom meets the parts".Localize(),
			};
			siblings.Add(slice);
			sliceRow.AddChild(slice);

			var height = new ThemedNumberEdit(LaserExportSettings.SliceHeight, theme, pixelWidth: 60 * GuiWidget.DeviceScale, allowDecimals: true, minValue: 0, increment: 0.1)
			{
				VAnchor = VAnchor.Center,
				SelectAllOnFocus = true,
			};
			sliceRow.AddChild(height);
			sliceRow.AddChild(new TextWidget("mm".Localize(), pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
			{
				VAnchor = VAnchor.Center,
				Margin = new BorderDouble(left: 4),
			});

			// true while this chooser is showing the shared value, so that does not count as a person's choice
			bool showing = false;

			fromAbove.CheckedStateChanged += (s, e) =>
			{
				if (fromAbove.Checked && !showing)
				{
					LaserExportSettings.Set(LaserOutlineMethod.Footprint, LaserExportSettings.SliceHeight);
					changed?.Invoke();
				}
			};

			slice.CheckedStateChanged += (s, e) =>
			{
				if (slice.Checked && !showing)
				{
					LaserExportSettings.Set(LaserOutlineMethod.Section, LaserExportSettings.SliceHeight);
					changed?.Invoke();
				}
			};

			height.ActuallNumberEdit.EditComplete += (s, e) =>
			{
				if (showing)
				{
					return;
				}

				LaserExportSettings.Set(LaserOutlineMethod.Section, height.ActuallNumberEdit.Value);
				changed?.Invoke();
			};

			// show the shared value whenever it changes elsewhere (the other format's panel, the laser page)
			void Show()
			{
				showing = true;
				try
				{
					fromAbove.Checked = LaserExportSettings.Method == LaserOutlineMethod.Footprint;
					slice.Checked = LaserExportSettings.Method == LaserOutlineMethod.Section;
					if (height.ActuallNumberEdit.Value != LaserExportSettings.SliceHeight)
					{
						height.ActuallNumberEdit.Value = LaserExportSettings.SliceHeight;
					}
				}
				finally
				{
					showing = false;
				}
			}

			Action onChanged = () => UiThread.RunOnIdle(() =>
			{
				if (!column.HasBeenClosed)
				{
					Show();
				}
			});
			LaserExportSettings.Changed += onChanged;
			column.Closed += (s, e) => LaserExportSettings.Changed -= onChanged;

			return column;
		}
	}

	/// <summary>
	/// EBMC Edition: the 2D outline seen from above (or sliced at a height) as an SVG in mm, for a laser cutter's own
	/// software. Offered for objects already in memory when OemSettings.EnableLaserExport is on. A file only: no printer,
	/// no slicing. A concrete class on purpose: the export dialog creates every public IExportPlugin by reflection, and a
	/// public abstract one would stop the plugins after it from loading.
	/// </summary>
	public class LaserSvgExport : IExportPlugin, IExportItemFilter, IExportWithOptions
	{
		internal LaserExportChoice Choice { get; } = new LaserExportChoice();

		public int Priority => 5;

		public string ButtonText => "SVG for laser".Localize();

		public string FileExtension => ".svg";

		public string ExtensionFilter => "Save as SVG|*.svg";

		public ImageBuffer Icon { get; } = LaserExportCore.LoadIcon();

		public void Initialize(PrinterConfig printer)
		{
		}

		public bool Enabled => LaserTools.IsEnabled;

		// empty: the dialog leaves the button out instead of showing it disabled
		public string DisabledReason => "";

		public bool ExportPossible(ILibraryAsset libraryItem) => Enabled;

		public bool CanExport(IEnumerable<ILibraryItem> libraryItems) => LaserExportCore.CanExport(libraryItems);

		public GuiWidget GetOptionsPanel(IEnumerable<ILibraryItem> libraryItems, RadioButton radioButton) => LaserExportCore.OptionsPanel(radioButton);

		public Task<List<ValidationError>> Generate(IEnumerable<ILibraryItem> libraryItems, string outputPath, Action<double, string> progress, CancellationToken cancellationToken)
		{
			return LaserExportCore.Generate(libraryItems, outputPath, progress, cancellationToken, Choice, (o, title) => LaserWriteOutput.Of(LaserSvgWriter.Write(o, new LaserFileOptions { Title = title })));
		}
	}

	/// <summary>
	/// EBMC Edition: the same outline as <see cref="LaserSvgExport"/> as an R12 DXF in mm (closed polylines on layer CUT).
	/// A concrete class on purpose (see LaserSvgExport).
	/// </summary>
	public class LaserDxfExport : IExportPlugin, IExportItemFilter, IExportWithOptions
	{
		internal LaserExportChoice Choice { get; } = new LaserExportChoice();

		public int Priority => 5;

		public string ButtonText => "DXF for laser".Localize();

		public string FileExtension => ".dxf";

		public string ExtensionFilter => "Save as DXF|*.dxf";

		public ImageBuffer Icon { get; } = LaserExportCore.LoadIcon();

		public void Initialize(PrinterConfig printer)
		{
		}

		public bool Enabled => LaserTools.IsEnabled;

		// empty: the dialog leaves the button out instead of showing it disabled
		public string DisabledReason => "";

		public bool ExportPossible(ILibraryAsset libraryItem) => Enabled;

		public bool CanExport(IEnumerable<ILibraryItem> libraryItems) => LaserExportCore.CanExport(libraryItems);

		public GuiWidget GetOptionsPanel(IEnumerable<ILibraryItem> libraryItems, RadioButton radioButton) => LaserExportCore.OptionsPanel(radioButton);

		public Task<List<ValidationError>> Generate(IEnumerable<ILibraryItem> libraryItems, string outputPath, Action<double, string> progress, CancellationToken cancellationToken)
		{
			return LaserExportCore.Generate(libraryItems, outputPath, progress, cancellationToken, Choice, (o, title) => LaserWriteOutput.Of(LaserDxfWriter.Write(o, new LaserFileOptions { Title = title })));
		}
	}
}
