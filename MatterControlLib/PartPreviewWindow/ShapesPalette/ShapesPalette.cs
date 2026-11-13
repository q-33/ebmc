using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.Library;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: what the shapes palettes remember for the rest of the session (not saved): each family's last used
	/// member and size chip. Shared by every tab's palette.
	/// </summary>
	public static class PaletteSession
	{
		private static readonly Dictionary<string, string> LastMember = new Dictionary<string, string>();

		private static readonly Dictionary<string, int> Chips = new Dictionary<string, int>();

		/// <summary>Raised when a family's current member changes, so every palette can update its button glyph.</summary>
		public static event EventHandler Changed;

		public static PaletteMember CurrentMember(PaletteFamily family)
		{
			if (LastMember.TryGetValue(family.Id, out string name)
				&& family.Members.FirstOrDefault(m => m.Name == name) is PaletteMember member)
			{
				return member;
			}

			return PaletteCatalog.DefaultMember(family);
		}

		public static void SetCurrentMember(PaletteFamily family, PaletteMember member)
		{
			if (CurrentMember(family) != member)
			{
				LastMember[family.Id] = member.Name;
				Changed?.Invoke(null, null);
			}
		}

		public static int SizeChip(string familyId)
		{
			return Chips.TryGetValue(familyId, out int size) ? size : PaletteCatalog.BaseSize;
		}

		public static void SetSizeChip(string familyId, int size)
		{
			Chips[familyId] = size;
		}
	}

	/// <summary>
	/// EBMC Edition: wraps a palette shape so it is created at the chosen size and/or as a hole. Everything else (ID,
	/// name, content type) is the original generator's, so insertion behaves as it does for the library item itself.
	/// </summary>
	public class PaletteInsertItem : ILibraryObject3D
	{
		private readonly ILibraryObject3D source;

		private readonly PaletteMember member;

		private readonly double scale;

		private readonly bool hole;

		public PaletteInsertItem(ILibraryObject3D source, PaletteMember member, double scale, bool hole)
		{
			this.source = source;
			this.member = member;
			this.scale = scale;
			this.hole = hole;
		}

		public string ID => source.ID;

		public string Name
		{
			get => source.Name;
			set { }
		}

		public event EventHandler NameChanged
		{
			add => source.NameChanged += value;
			remove => source.NameChanged -= value;
		}

		public bool IsProtected => true;

		public bool IsVisible => source.IsVisible;

		public DateTime DateModified => source.DateModified;

		public DateTime DateCreated => source.DateCreated;

		public string ContentType => source.ContentType;

		public string Category => source.Category;

		public string FileName => source.FileName;

		public string AssetPath => source.AssetPath;

		public async Task<IObject3D> GetObject3D(Action<double, string> reportProgress)
		{
			var item = await source.GetObject3D(reportProgress);
			if (item == null)
			{
				return null;
			}

			if (scale != 1 && member.Scalable)
			{
				foreach (var propertyName in member.ScaledProperties)
				{
					var property = item.GetType().GetProperty(propertyName);
					if (property?.GetValue(item) is DoubleOrExpression value
						&& property.CanWrite)
					{
						property.SetValue(item, new DoubleOrExpression(value.Value(item) * scale));
					}
				}

				await item.Rebuild();
			}

			if (hole)
			{
				item.OutputType = PrintOutputTypes.Hole;
			}

			return item;
		}
	}

	/// <summary>
	/// EBMC Edition: the shapes and tools palette that replaces the shapes rail when OemSettings.ShapesPaletteStyle is
	/// "palette" (direction A of tasks/design/shapes-rail-mockups.html). A 48 unit column of 36 unit icon buttons, 164
	/// units with labels (the FavoritesBarExpansion user setting). What it shows comes from PaletteCatalog; shapes are the
	/// library's own generator items and tools are the toolbar's own SceneOperations.
	/// </summary>
	public class ShapesPalette : GuiWidget
	{
		public const double CompactWidth = 48;

		public const double ExpandedWidth = 164;

		private static CalibrationPartsContainer calibrationParts;

		private readonly ThemeConfig theme;

		private readonly ISceneContext sceneContext;

		private readonly View3DWidget view3DWidget;

		private readonly PaletteTab tab;

		private readonly List<(PaletteButton button, PaletteFamily family)> familyButtons = new List<(PaletteButton, PaletteFamily)>();

		private bool holeMode;

		private bool expanded;

		/// <summary>EBMC Edition: the Simple · Advanced mode this palette was built for (the window is rebuilt on a change).</summary>
		private readonly UiMode mode;

		public ShapesPalette(ThemeConfig theme, ISceneContext sceneContext, View3DWidget view3DWidget, bool isPrinterTab)
		{
			this.theme = theme;
			this.sceneContext = sceneContext;
			this.view3DWidget = view3DWidget;
			this.tab = isPrinterTab ? PaletteTab.Printer : PaletteTab.Design;

			// The interface tour's "Shapes" step (ProductTour.json) points at the widget named LibraryView
			this.Name = "LibraryView";
			this.HAnchor = HAnchor.Absolute;
			this.VAnchor = VAnchor.Stretch;
			this.BackgroundColor = theme.BackgroundColor;
			this.Border = new BorderDouble(top: 1, right: 1);
			this.BorderColor = ClubTabStyle.LineColor(theme);

			// EBMC Edition: the Simple · Advanced mode (UiModes) picks the lists; design tabs in Simple always show labels
			mode = UiModes.Current;
			expanded = PaletteCatalog.LabelsAlwaysShown(tab, mode)
				|| UserSettings.Instance.get(UserSettingsKey.FavoritesBarExpansion) == "1";

			Build();

			sceneContext.Scene.SelectionChanged += Scene_Changed;
			sceneContext.Scene.ItemsModified += Scene_Changed;
			PaletteSession.Changed += PaletteSession_Changed;
		}

		public override void OnClosed(EventArgs e)
		{
			sceneContext.Scene.SelectionChanged -= Scene_Changed;
			sceneContext.Scene.ItemsModified -= Scene_Changed;
			PaletteSession.Changed -= PaletteSession_Changed;

			base.OnClosed(e);
		}

		private void Scene_Changed(object sender, EventArgs e)
		{
			UiThread.RunOnIdle(UpdateButtons);
		}

		private void PaletteSession_Changed(object sender, EventArgs e)
		{
			UiThread.RunOnIdle(UpdateButtons);
		}

		private void Build()
		{
			this.CloseChildren();
			familyButtons.Clear();

			var scale = DeviceScale;
			this.Width = (expanded ? ExpandedWidth : CompactWidth) * scale;
			// minus the right border in device pixels, scaled and rounded the way GuiWidget sizes its device border
			var borderRight = this.Border.Right * scale;
			if (this.EnforceIntegerBounds)
			{
				borderRight = Math.Round(borderRight);
			}

			var buttonWidth = this.Width - borderRight;

			var top = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Top | VAnchor.Fit,
				Padding = new BorderDouble(top: PaletteButton.Inset),
			};
			this.AddChild(top);

			if (PaletteCatalog.HasHoleSwitch(tab))
			{
				top.AddChild(CreateHoleSwitch(buttonWidth));
			}

			// EBMC Edition: the Solids family only when solid modelling is on and the kernel loaded (never in Simple); the club
			// shapes and starters only when EnableClubShapes is on (design tabs)
			foreach (var section in PaletteCatalog.For(tab, DesignTools.Solid.SolidModeling.IsEnabled, mode, DesignTools.ClubShapes.ClubShapesFeature.IsEnabled))
			{
				if (section.Label != null)
				{
					top.AddChild(CreateDivider(section.Label));
				}

				foreach (var family in section.Families)
				{
					if (!family.Members.Any(IsAvailable))
					{
						continue;
					}

					var button = new PaletteButton(theme, family.Label.Localize(), family.HasFlyout, expanded, buttonWidth)
					{
						View3DWidget = view3DWidget
					};

					if (!family.ClickOpensFlyout)
					{
						button.Activated += (s, e) => Run(family, PaletteSession.CurrentMember(family));
					}

					button.FlyoutRequested += (s, e) => ShowFlyout(button, family);

					if (!family.ClickOpensFlyout
						&& family.Members.All(m => m.Source == PaletteSource.Primitives || m.Source == PaletteSource.CalibrationParts))
					{
						button.DragItems = () =>
						{
							var item = PrepareItem(family, PaletteSession.CurrentMember(family));
							return item == null ? Enumerable.Empty<ILibraryItem>() : new[] { item };
						};
					}

					familyButtons.Add((button, family));
					top.AddChild(button);
				}
			}

			if (PaletteCatalog.LabelsAlwaysShown(tab, mode))
			{
				// no labels toggle: the labels stay on and the user's labels setting is left as it is
				UpdateButtons();
				return;
			}

			var labelsButton = new PaletteButton(theme, expanded ? "Hide labels".Localize() : "Show labels".Localize(), false, expanded, buttonWidth)
			{
				VAnchor = VAnchor.Absolute | VAnchor.Bottom,
				Margin = new BorderDouble(bottom: PaletteButton.Inset),
				ToolTipText = expanded ? "Hide labels".Localize() : "Show labels".Localize(),
				Name = "Palette Labels Button",
			};
			labelsButton.SetGlyph(PaletteIcons.Load(expanded ? PaletteCatalog.LabelsHideGlyph : PaletteCatalog.LabelsShowGlyph, PaletteCatalog.GlyphSize, theme.TextColor));
			labelsButton.Activated += (s, e) => UiThread.RunOnIdle(() =>
			{
				expanded = !expanded;
				UserSettings.Instance.set(UserSettingsKey.FavoritesBarExpansion, expanded ? "1" : "0");
				Build();
			});
			this.AddChild(labelsButton);

			UpdateButtons();
		}

		private GuiWidget CreateDivider(string label)
		{
			var scale = DeviceScale;
			if (expanded)
			{
				return new TextWidget(label.Localize().ToUpper(), pointSize: 7, textColor: ClubTabStyle.MutedTextColor(theme), bold: true)
				{
					Margin = new BorderDouble(PaletteButton.Inset + 8, 3, 0, 8),
					AutoExpandBoundsToText = true
				};
			}

			return new GuiWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Absolute,
				Height = Math.Max(1, Math.Round(ClubTabStyle.LineWidth * scale)),
				Margin = new BorderDouble(PaletteButton.Inset + 6, 4),
				BackgroundColor = ClubTabStyle.LineColor(theme)
			};
		}

		/// <summary>
		/// The Solid/Hole switch: two full-size palette buttons, stacked; the chosen one has the raised fill (accent tint
		/// for Hole) and the accent bar. Hole makes the shapes you add holes. Not saved: every tab starts on Solid.
		/// </summary>
		private GuiWidget CreateHoleSwitch(double width)
		{
			var column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				Name = "Solid Hole Switch",
			};

			PaletteButton Side(string label, string glyph, bool isHole, string tooltip)
			{
				var on = isHole == holeMode;
				var button = new PaletteButton(theme, label, false, expanded, width)
				{
					Selected = on,
					SelectedFill = isHole ? theme.PrimaryAccentColor.WithAlpha(41) : ClubTabStyle.HoverFillColor(theme),
					ToolTipText = tooltip,
					Name = label + " Switch Button",
				};

				var color = !on ? ClubTabStyle.MutedTextColor(theme) : isHole ? theme.PrimaryAccentColor : theme.TextColor;
				button.SetGlyph(PaletteIcons.Load(glyph, PaletteCatalog.GlyphSize, color));
				button.Activated += (s, e) => UiThread.RunOnIdle(() =>
				{
					if (holeMode != isHole)
					{
						holeMode = isHole;
						Build();
					}
				});

				return button;
			}

			column.AddChild(Side("Solid".Localize(), PaletteCatalog.SolidSwitchGlyph, false, "Solid: shapes you add are solid".Localize()));
			column.AddChild(Side("Hole".Localize(), PaletteCatalog.HoleSwitchGlyph, true, "Hole: shapes you add cut away from solids when grouped".Localize()));

			return column;
		}

		private void UpdateButtons()
		{
			if (this.HasBeenClosed)
			{
				return;
			}

			foreach (var (button, family) in familyButtons)
			{
				var member = PaletteSession.CurrentMember(family);
				var memberEnabled = IsEnabled(member);

				// a family button stays usable while any member is, so its flyout can still be opened
				button.Enabled = family.HasFlyout ? family.Members.Where(IsAvailable).Any(IsEnabled) : memberEnabled;

				var glyphColor = !memberEnabled
					? theme.DisabledColor
					: holeMode && family.CanBeHole ? theme.PrimaryAccentColor : theme.TextColor;
				button.SetGlyph(PaletteIcons.Load(family.FamilyGlyph ?? member.Glyph, PaletteCatalog.GlyphSize, glyphColor));
				// no tooltip while the button's flyout is open: it would cover the flyout's title and first rows
				button.ToolTipText = button.FlyoutOpen ? "" : Tooltip(family, member, memberEnabled);
			}
		}

		private string Tooltip(PaletteFamily family, PaletteMember member, bool enabled)
		{
			if (family.ClickOpensFlyout)
			{
				return family.Label.Localize() + "\n\n" + "Click to choose.".Localize();
			}

			var size = member.Scalable
				? member.SizeText(PaletteCatalog.ScaleFor(PaletteSession.SizeChip(family.Id))) + " mm"
				: member.SizeText().Localize();
			var lines = new List<string>
			{
				member.Title.Localize() + (string.IsNullOrEmpty(size) ? "" : "  (" + size + ")")
			};

			if (!string.IsNullOrEmpty(member.Hint))
			{
				lines.Add(member.Hint.Localize());
			}

			if (member.Source == PaletteSource.Primitives || member.Source == PaletteSource.CalibrationParts || member.Source == PaletteSource.Starters)
			{
				if (holeMode && family.CanBeHole)
				{
					lines.Add("Added as a hole.".Localize());
				}

				lines.Add("Click to add, drag to place.".Localize());
			}

			if (family.HasFlyout)
			{
				var others = family.Members.Where(m => m != member && IsAvailable(m)).Select(m => m.Title.Localize());
				lines.Add("Click the corner for {0}.".Localize().FormatWith(string.Join(", ", others)));
			}

			if (!enabled
				&& Operation(member)?.HelpText is string help
				&& !string.IsNullOrEmpty(help))
			{
				lines.Add("");
				lines.Add(help);
			}

			// the app's tooltips are Markdown: a blank line starts a new paragraph, a single line break does not
			return string.Join("\n\n", lines.Where(l => l != ""));
		}

		/// <summary>
		/// EBMC Edition: opens a family's flyout as if its corner were clicked (the start card's "Add a shape"). False when
		/// this palette has no such family or is not in a window.
		/// </summary>
		public bool OpenFlyout(string familyId)
		{
			foreach (var (button, family) in familyButtons)
			{
				if (family.Id == familyId && family.HasFlyout && button.Enabled)
				{
					ShowFlyout(button, family);
					return button.FlyoutOpen;
				}
			}

			return false;
		}

		/// <summary>EBMC Edition: the library item a palette member adds (null when it is not available in this build).</summary>
		public static ILibraryItem ResolveItem(PaletteMember member) => Resolve(member);

		/// <summary>
		/// EBMC Edition: runs a member chosen in command search exactly as a click on it would (the family's size chip and
		/// the Solid/Hole switch apply; inserts queue at the bed centre). False when this palette is not for
		/// <paramref name="forScene"/> or does not show the member.
		/// </summary>
		public bool RunFromSearch(ISceneContext forScene, PaletteMember member)
		{
			if (forScene != sceneContext
				|| this.HasBeenClosed)
			{
				return false;
			}

			var family = familyButtons.Select(f => f.family).FirstOrDefault(f => f.Members.Contains(member));
			if (family == null
				|| !IsEnabled(member))
			{
				return false;
			}

			PaletteSession.SetCurrentMember(family, member);
			Run(family, member);
			return true;
		}

		private void ShowFlyout(PaletteButton button, PaletteFamily family)
		{
			if (button.FlyoutOpen)
			{
				return;
			}

			GuiWidget content;
			if (family.ClickOpensFlyout
				&& family.Members[0].Name == PaletteCatalog.SupportsCommand)
			{
				// the toolbar's Generate Support panel
				content = ViewToolBarControls.CreateSupportPanel(sceneContext);
			}
			else
			{
				string hint = null;
				if (family.Members.All(m => m.Source != PaletteSource.SceneOperation))
				{
					hint = holeMode && family.CanBeHole
						? "Holes cut into the parts they are grouped or combined with".Localize()
						: "Click adds near the bed centre; drag the button to place".Localize();
				}

				var flyout = new PaletteFlyout(AppContext.MenuTheme, family, IsAvailable, IsEnabled, m => Operation(m)?.HelpText, holeMode && family.CanBeHole, hint);
				flyout.MemberChosen += (s, member) =>
				{
					PaletteSession.SetCurrentMember(family, member);
					Run(family, member);
				};
				content = flyout;
			}

			button.FlyoutOpen = true;
			button.ToolTipText = "";
			button.Invalidate();
			content.Closed += (s, e) =>
			{
				button.FlyoutOpen = false;
				button.Invalidate();

				// the tooltip comes back a moment after the flyout closes
				UiThread.RunOnIdle(UpdateButtons, .6);
			};

			if (!ShowBeside(button, content))
			{
				// not in a window (yet): nothing opened, so nothing will close it
				button.FlyoutOpen = false;
				button.Invalidate();
				UpdateButtons();
			}
		}

		/// <summary>
		/// Shows <paramref name="content"/> to the right of <paramref name="anchor"/>, top aligned, or bottom aligned when
		/// it would run off the bottom of the window. False when the anchor is not in a window.
		/// </summary>
		private static bool ShowBeside(PaletteButton anchor, GuiWidget content)
		{
			if (anchor.Parents<SystemWindow>().FirstOrDefault() is SystemWindow systemWindow)
			{
				systemWindow.ToolTipManager.Clear();

				// leave the mockup's gap between the button and the flyout
				var gap = 7 * DeviceScale;
				systemWindow.ShowPopup(
					AppContext.MenuTheme,
					new MatePoint(anchor)
					{
						Mate = new MateOptions(MateEdge.Right, MateEdge.Top),
						AltMate = new MateOptions(MateEdge.Right, MateEdge.Bottom)
					},
					new MatePoint(content)
					{
						Mate = new MateOptions(MateEdge.Left, MateEdge.Top),
						AltMate = new MateOptions(MateEdge.Left, MateEdge.Bottom)
					},
					new RectangleDouble(0, 0, anchor.Width + gap, anchor.Height));
				return true;
			}

			return false;
		}

		/// <summary>Runs a member: adds a shape or part, runs an operation or a command.</summary>
		private void Run(PaletteFamily family, PaletteMember member)
		{
			switch (member.Source)
			{
				case PaletteSource.Primitives:
				case PaletteSource.CalibrationParts:
				case PaletteSource.Starters:
					if (PrepareItem(family, member) is ILibraryItem item)
					{
						// the bed centre, or the nearest free spot on the bed
						QueueInsert(item);
						FocusView();
					}

					break;

				case PaletteSource.SceneOperation:
					if (Operation(member) is SceneOperation operation
						&& operation.IsEnabled(sceneContext))
					{
						UiThread.RunOnIdle(() =>
						{
							operation.Action?.Invoke(sceneContext);
							FocusView();
						});
					}

					break;

				case PaletteSource.Command:
					if (member.Name == PaletteCatalog.AddModelCommand)
					{
						// the toolbar's Add to Bed > Add System File to Bed
						ApplicationController.OpenFileWithSystemDialog((fileNames) =>
						{
							sceneContext.AddToPlate(fileNames);
						});
					}

					break;
			}
		}

		/// <summary>
		/// Click inserts run one after another: each starts when the previous one has loaded and been placed, so two quick
		/// clicks cannot both take the bed centre. (The layout runs inside InsertionGroupObject3D's loading task, off the UI
		/// thread, and only sees parts already on the bed.)
		/// </summary>
		private Task insertChain = Task.CompletedTask;

		private void QueueInsert(ILibraryItem item)
		{
			var previous = insertChain;
			var done = new TaskCompletionSource<bool>();
			insertChain = done.Task;

			previous.ContinueWith(_ => UiThread.RunOnIdle(() =>
			{
				// EBMC: 'done' must always complete, or the queue jams and every later click does nothing
				bool waitingForLoad = false;
				try
				{
					if (this.HasBeenClosed)
					{
						return;
					}

					// AddToPlate inserts into the ACTIVE tab's scene (DragDropData), so after a tab switch a queued insert
					// would land on another tab: skip it
					if (ApplicationController.Instance.DragDropData.SceneContext != sceneContext)
					{
						return;
					}

					// Read on the UI thread; the layout callback then only uses these values and the loading task's own
					// snapshot of the bed's parts
					var center = sceneContext.BedCenter;
					BedRect? bed = null;
					if (sceneContext.Printer?.Settings.BedBounds is RectangleDouble bedBounds)
					{
						bed = new BedRect(bedBounds.Left, bedBounds.Bottom, bedBounds.Right, bedBounds.Top);
					}

					var insertion = sceneContext.AddToPlate(new[] { item }, center, (loaded, itemsOnBed) => PlaceNearBedCenter(loaded, itemsOnBed, center, bed));
					if (insertion?.LoadingItemsTask is Task loading)
					{
						waitingForLoad = true;
						loading.ContinueWith(__ => done.TrySetResult(true));
					}
				}
				catch
				{
					ShowHint("The part could not be added.".Localize());
				}
				finally
				{
					if (!waitingForLoad)
					{
						done.TrySetResult(true);
					}
				}
			}));
		}

		/// <summary>
		/// Layout for a click-added part: on the bed, centred on the bed centre or the nearest free spot (PalettePlacement).
		/// Runs on the insertion's loading task; <paramref name="itemsOnBed"/> is that task's snapshot of the bed.
		/// </summary>
		private static void PlaceNearBedCenter(IObject3D item, IEnumerable<IObject3D> itemsOnBed, Vector2 center, BedRect? bed)
		{
			PlatingHelper.PlaceOnBed(item);

			var bounds = item.GetAxisAlignedBoundingBox();
			if (bounds.XSize < 0 || bounds.YSize < 0)
			{
				return;
			}

			var occupied = itemsOnBed
				.Where(other => other != item)
				.Select(other => other.GetAxisAlignedBoundingBox())
				.Where(b => b.XSize >= 0 && b.YSize >= 0 && !double.IsInfinity(b.XSize) && !double.IsInfinity(b.YSize))
				.Select(b => new BedRect(b.MinXYZ.X, b.MinXYZ.Y, b.MaxXYZ.X, b.MaxXYZ.Y))
				.ToList();

			var (x, y, found) = PalettePlacement.FindOpenCenter(center.X, center.Y, bounds.XSize, bounds.YSize, occupied, bed);
			item.Matrix *= Matrix4X4.CreateTranslation(x - bounds.Center.X, y - bounds.Center.Y, 0);

			if (!found)
			{
				// a part larger than the bed can never find a free spot; say that rather than "the bed is full"
				var text = PalettePlacement.FitsOnBed(bounds.XSize, bounds.YSize, bed)
					? "The bed is full; the part was placed at the centre.".Localize()
					: "This part is bigger than the bed.".Localize();
				UiThread.RunOnIdle(() => ShowHint(text));
			}
		}

		/// <summary>A message in the status bar for a few seconds.</summary>
		private static void ShowHint(string text)
		{
			ApplicationController.Instance.SetUiHint(text);
			UiThread.RunOnIdle(
				() =>
				{
					if (ApplicationController.Instance.GetUiHint() == text)
					{
						ApplicationController.Instance.SetUiHint("");
					}
				},
				6);
		}

		private void FocusView()
		{
			if (view3DWidget?.HasBeenClosed == false)
			{
				view3DWidget.Object3DControlLayer.Focus();
			}
		}

		/// <summary>
		/// The library item to add for a member: the registered generator itself, or a wrapper that makes it at the chosen
		/// size and/or as a hole.
		/// </summary>
		private ILibraryItem PrepareItem(PaletteFamily family, PaletteMember member)
		{
			var item = Resolve(member);
			var scale = member.Scalable ? PaletteCatalog.ScaleFor(PaletteSession.SizeChip(family.Id)) : 1;
			var hole = holeMode && family.CanBeHole;

			if (item is ILibraryObject3D object3DItem
				&& (scale != 1 || hole))
			{
				return new PaletteInsertItem(object3DItem, member, scale, hole);
			}

			return item;
		}

		private bool IsAvailable(PaletteMember member)
		{
			switch (member.Source)
			{
				case PaletteSource.Primitives:
				case PaletteSource.CalibrationParts:
				case PaletteSource.Starters:
					return Resolve(member) != null;

				case PaletteSource.SceneOperation:
					return Operation(member) != null;

				default:
					return true;
			}
		}

		private bool IsEnabled(PaletteMember member)
		{
			if (member.Source == PaletteSource.SceneOperation)
			{
				return Operation(member)?.IsEnabled(sceneContext) == true;
			}

			return IsAvailable(member);
		}

		private static SceneOperation Operation(PaletteMember member)
		{
			if (member.Source != PaletteSource.SceneOperation
				|| SceneOperations.All == null)
			{
				return null;
			}

			// The registry's operations, including those inside groups (SceneOperations.ById throws for unknown ids)
			IEnumerable<SceneOperation> Flatten(IEnumerable<SceneOperation> operations)
			{
				foreach (var operation in operations.Where(o => o != null))
				{
					yield return operation;
					if (operation is OperationGroup group)
					{
						foreach (var child in Flatten(group.Operations))
						{
							yield return child;
						}
					}
				}
			}

			return Flatten(SceneOperations.All).FirstOrDefault(o => o.Id == member.Name);
		}

		/// <summary>
		/// Finds the item a member names: the generator registered in the library root by Primitives3DContainer, or the
		/// item of the calibration parts container. Missing items (e.g. DEBUG-only ones in a release build) give null.
		/// </summary>
		private static ILibraryItem Resolve(PaletteMember member)
		{
			IEnumerable<ILibraryItem> items;
			string name;

			if (member.Source == PaletteSource.Primitives || member.Source == PaletteSource.Starters)
			{
				// EBMC Edition: the Club Starters are registered in the library root too (EditorToolsPlugin)
				items = ApplicationController.Instance.Library.RootLibaryContainer.Items.OfType<GeneratorItem>();
				name = member.Name.Localize();
			}
			else if (member.Source == PaletteSource.CalibrationParts)
			{
				if (calibrationParts == null)
				{
					calibrationParts = new CalibrationPartsContainer();
					calibrationParts.Load();
				}

				items = calibrationParts.Items;

				// generator names are localized; sample parts are named by their file name
				name = member.Name.Contains('.') ? member.Name : member.Name.Localize();
			}
			else
			{
				return null;
			}

			return items.FirstOrDefault(i => i.Name == name);
		}
	}
}
