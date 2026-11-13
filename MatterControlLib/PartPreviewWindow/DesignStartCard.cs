using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.ImageProcessing;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	/// <summary>
	/// EBMC Edition: the "Start a design" card a new, empty design tab shows over its 3D view (OEM switch
	/// NewDesignStartsEmpty; frame 6 of tasks/design/ux-modes-mockups.html). Three choices (add a shape, use a starter,
	/// open a file), a starters gallery with the club badge as one more starter, and "Don't show this again". It goes away
	/// for good as soon as the scene has anything in it; the palette, toolbar and the rest of the view stay usable.
	/// </summary>
	public class DesignStartCard : GuiWidget
	{
		private const double CardWidth = 600;

		private readonly ThemeConfig theme;

		private readonly ISceneContext sceneContext;

		private readonly DesignTabPage page;

		private readonly FlowLayoutWidget gallery;

		private readonly TextWidget galleryNote;

		private string defaultNote;

		/// <summary>
		/// Puts the card on a new design tab when the club switch asks for an empty start and the user has not hidden it.
		/// Returns true when the tab should stay empty (no sample part), whether or not the card is shown.
		/// </summary>
		public static bool StartEmpty(ChromeTab tab)
		{
			if (!OemSettings.Instance.NewDesignStartsEmpty)
			{
				return false;
			}

			if (UserSettings.Instance.get(UserSettingsKey.HideDesignStartCard) != "true"
				&& tab?.TabContent is DesignTabPage page
				&& !(page is PrinterTabPage)
				&& page.view3DWidget != null
				&& page.sceneContext.Scene.Children.Count == 0)
			{
				// in the 3D viewport itself (the layer that holds the view controls), so the card centres in the view and
				// clears the right-hand panel
				page.view3DWidget.Object3DControlLayer.AddChild(new DesignStartCard(AppContext.Theme, page));
				CardScenes.AddOrUpdate(page.sceneContext, page.sceneContext);
			}

			return true;
		}

		/// <summary>The scenes whose tab showed the card and that are still empty (the card has not gone for good).</summary>
		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISceneContext, object> CardScenes = new System.Runtime.CompilerServices.ConditionalWeakTable<ISceneContext, object>();

		/// <summary>
		/// After a window rebuild (Simple · Advanced switch, maintainer lock) a restored design tab whose card had not gone
		/// for good, and whose scene is still empty, shows the card again (StartCardRules.ShowAgain).
		/// </summary>
		public static void RestoreAfterRebuild(ChromeTab tab)
		{
			if (tab?.TabContent is DesignTabPage page
				&& !(page is PrinterTabPage)
				&& page.view3DWidget != null
				&& page.view3DWidget.Object3DControlLayer.Children.OfType<DesignStartCard>().Any() == false
				&& DesignTools.ClubShapes.StartCardRules.ShowAgain(
					hadCard: CardScenes.TryGetValue(page.sceneContext, out _),
					sceneEmpty: page.sceneContext.Scene.Children.Count == 0,
					hidden: UserSettings.Instance.get(UserSettingsKey.HideDesignStartCard) == "true",
					startsEmpty: OemSettings.Instance.NewDesignStartsEmpty))
			{
				page.view3DWidget.Object3DControlLayer.AddChild(new DesignStartCard(AppContext.Theme, page));
			}
		}

		private DesignStartCard(ThemeConfig theme, DesignTabPage page)
		{
			this.theme = theme;
			this.page = page;
			this.sceneContext = page.sceneContext;

			var scale = DeviceScale;
			this.Name = "Design Start Card";
			// centred in the viewport (it was at the top, over the view controls); narrower when the view is
			this.HAnchor = HAnchor.Center | HAnchor.Absolute;
			this.VAnchor = VAnchor.Center | VAnchor.Fit;
			this.Width = CardWidth * scale;
			this.Padding = new BorderDouble(18, 14, 18, 16);
			this.BackgroundColor = theme.BackgroundColor;
			this.BackgroundRadius = new RadiusCorners(10 * scale);
			this.BackgroundOutlineWidth = Math.Max(1, Math.Round(scale));
			this.BorderColor = ClubTabStyle.LineColor(theme);

			var column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			this.AddChild(column);

			column.AddChild(new TextWidget("Start a design".Localize(), pointSize: 14, textColor: theme.TextColor, bold: true)
			{
				AutoExpandBoundsToText = true,
			});
			column.AddChild(new TextWidget("Start with a shape, a starter design, or a file you downloaded.".Localize(), pointSize: 9, textColor: ClubTabStyle.MutedTextColor(theme))
			{
				AutoExpandBoundsToText = true,
				Margin = new BorderDouble(top: 2, bottom: 12),
			});

			var choices = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(bottom: 12),
			};
			column.AddChild(choices);

			var withStarters = StarterTiles().Any();
			// short details that wrap inside the tile instead of being cut off
			choices.AddChild(Choice("cube", "Add a shape".Localize(), "Box, round, text, picture".Localize(), AddShape));
			choices.AddChild(Choice(PaletteCatalog.StartersGlyph, "Use a starter".Localize(), withStarters ? "Name tag, sign, box and more".Localize() : "The club badge".Localize(), ToggleGallery));
			choices.AddChild(Choice("add-model", "Open a file…".Localize(), "STL, 3MF or OBJ, or drop it here".Localize(), OpenFile));

			// the starters gallery, shown by "Use a starter"
			gallery = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Visible = false,
				Margin = new BorderDouble(bottom: 6),
			};
			column.AddChild(gallery);
			BuildGallery();

			var footer = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			column.AddChild(footer);

			defaultNote = "Starters are ordinary parts: every number can be changed on the right.".Localize();
			galleryNote = new TextWidget(defaultNote, pointSize: 8.5, textColor: ClubTabStyle.MutedTextColor(theme))
			{
				VAnchor = VAnchor.Center,
				AutoExpandBoundsToText = true,
			};
			footer.AddChild(galleryNote);
			footer.AddChild(new HorizontalSpacer());

			var hide = new ThemedButton(theme)
			{
				HAnchor = HAnchor.Fit,
				VAnchor = VAnchor.Fit,
				Padding = new BorderDouble(8, 4),
				HoverColor = ClubTabStyle.HoverFillColor(theme),
				BackgroundRadius = new RadiusCorners(5 * scale),
				Name = "Hide Design Start Card Button",
				ToolTipText = "New design tabs will open empty, without this card.".Localize(),
			};
			hide.AddChild(new TextWidget("Don't show this again".Localize(), pointSize: 8.5, textColor: ClubTabStyle.MutedTextColor(theme))
			{
				Selectable = false,
			});
			hide.Click += (s, e) => UiThread.RunOnIdle(() =>
			{
				UserSettings.Instance.set(UserSettingsKey.HideDesignStartCard, "true");
				CardScenes.Remove(sceneContext);
				this.Close();
			});
			footer.AddChild(hide);

			sceneContext.Scene.Children.ItemsModified += Scene_Changed;
			sceneContext.Scene.Invalidated += Scene_Changed;
		}

		public override void OnParentChanged(EventArgs e)
		{
			base.OnParentChanged(e);
			if (this.Parent != null)
			{
				this.Parent.BoundsChanged += Parent_BoundsChanged;
				FitWidth();
			}
		}

		private void Parent_BoundsChanged(object sender, EventArgs e) => FitWidth();

		/// <summary>
		/// The card's width: 600 units, less in a narrow view so it keeps clear of the view controls (about 130 units at
		/// each side), never under 420.
		/// </summary>
		private void FitWidth()
		{
			if (this.Parent == null
				|| this.Parent.Width <= 0)
			{
				return;
			}

			var scale = DeviceScale;
			var width = Math.Max(420 * scale, Math.Min(CardWidth * scale, this.Parent.Width - 2 * 130 * scale));
			if (Math.Abs(this.Width - width) > 0.5)
			{
				this.Width = width;
			}
		}

		public override void OnClosed(EventArgs e)
		{
			if (this.Parent != null)
			{
				this.Parent.BoundsChanged -= Parent_BoundsChanged;
			}

			sceneContext.Scene.Children.ItemsModified -= Scene_Changed;
			sceneContext.Scene.Invalidated -= Scene_Changed;
			base.OnClosed(e);
		}

		private void Scene_Changed(object sender, EventArgs e)
		{
			UiThread.RunOnIdle(() =>
			{
				// the first thing added (a shape, a starter, a dropped or opened file) removes the card for good
				if (!this.HasBeenClosed && sceneContext.Scene.Children.Count > 0)
				{
					CardScenes.Remove(sceneContext);
					this.Close();
				}
			});
		}

		private GuiWidget Choice(string glyph, string title, string detail, Action action)
		{
			var scale = DeviceScale;
			var button = new ThemedButton(theme)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				MinimumSize = new Vector2(0, 58 * scale),
				Margin = new BorderDouble(right: 8),
				Padding = new BorderDouble(10, 8),
				BackgroundColor = ClubTabStyle.HoverFillColor(theme),
				HoverColor = theme.PrimaryAccentColor.WithAlpha(41),
				MouseDownColor = theme.PrimaryAccentColor.WithAlpha(70),
				BackgroundRadius = new RadiusCorners(9 * scale),
				Name = title + " Start Button",
				ToolTipText = title,
			};

			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Center | VAnchor.Fit,
				Selectable = false,
			};
			button.AddChild(row);
			row.AddChild(new ImageWidget(PaletteIcons.Load(glyph, 24, theme.PrimaryAccentColor), listenForImageChanged: false)
			{
				VAnchor = VAnchor.Center,
				Selectable = false,
			});

			var text = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Center | VAnchor.Fit,
				Margin = new BorderDouble(left: 10),
				Selectable = false,
			};
			row.AddChild(text);
			text.AddChild(new TextWidget(title, pointSize: 10, textColor: theme.TextColor, bold: true) { Selectable = false });

			// the detail wraps to a second line rather than ending in "…"
			text.AddChild(new WrappedTextWidget(detail, pointSize: 8, textColor: ClubTabStyle.MutedTextColor(theme))
			{
				HAnchor = HAnchor.Stretch,
				Selectable = false,
			});

			button.Click += (s, e) => UiThread.RunOnIdle(action);
			return button;
		}

		/// <summary>The starters this build offers (registered generators), then the club badge.</summary>
		private IEnumerable<(string name, ImageBuffer image, string hint, Action add)> StarterTiles()
		{
			foreach (var member in PaletteCatalog.StarterMembers)
			{
				if (ShapesPalette.ResolveItem(member) is ILibraryItem item)
				{
					yield return (member.Title.Localize(), PaletteIcons.Load(member.Glyph, 40, theme.TextColor), member.Hint.Localize(), () => sceneContext.AddToPlate(new[] { item }));
				}
			}

			// the badge artwork is black on transparent: tint it like the starter glyphs so it reads on the dark tile
			yield return ("Club badge".Localize(), StaticData.Instance.LoadIcon("mh.png", 40, 40).GrayToColor(theme.TextColor), "The club's EBMC badge, ready to print.".Localize(), () => sceneContext.AddPhilToBed());
		}

		private void BuildGallery()
		{
			var scale = DeviceScale;
			gallery.AddChild(new TextWidget("CLUB STARTERS".Localize(), pointSize: 7.5, textColor: ClubTabStyle.MutedTextColor(theme), bold: true)
			{
				Margin = new BorderDouble(bottom: 6),
				AutoExpandBoundsToText = true,
			});

			FlowLayoutWidget row = null;
			int count = 0;
			foreach (var (name, image, hint, add) in StarterTiles())
			{
				if (count % 5 == 0)
				{
					row = new FlowLayoutWidget()
					{
						HAnchor = HAnchor.Stretch,
						VAnchor = VAnchor.Fit,
						Margin = new BorderDouble(bottom: 8),
					};
					gallery.AddChild(row);
				}

				count++;
				var tile = new ThemedButton(theme)
				{
					HAnchor = HAnchor.Absolute,
					VAnchor = VAnchor.Absolute,
					Width = 104 * scale,
					Height = 78 * scale,
					Margin = new BorderDouble(right: 8),
					BackgroundColor = ClubTabStyle.HoverFillColor(theme),
					HoverColor = theme.PrimaryAccentColor.WithAlpha(41),
					MouseDownColor = theme.PrimaryAccentColor.WithAlpha(70),
					BackgroundRadius = new RadiusCorners(8 * scale),
					Name = name + " Starter Tile",
					ToolTipText = name + "\n\n" + hint,
				};

				var stack = new FlowLayoutWidget(FlowDirection.TopToBottom)
				{
					HAnchor = HAnchor.Center | HAnchor.Fit,
					VAnchor = VAnchor.Center | VAnchor.Fit,
					Selectable = false,
				};
				tile.AddChild(stack);
				stack.AddChild(new ImageWidget(image, listenForImageChanged: false)
				{
					HAnchor = HAnchor.Center,
					Selectable = false,
				});
				stack.AddChild(new TextWidget(name, pointSize: 8.5, textColor: theme.TextColor)
				{
					HAnchor = HAnchor.Center,
					Margin = new BorderDouble(top: 5),
					Selectable = false,
				});

				tile.MouseEnterBounds += (s, e) => galleryNote.Text = hint;
				tile.MouseLeaveBounds += (s, e) => galleryNote.Text = defaultNote;
				tile.Click += (s, e) => UiThread.RunOnIdle(() =>
				{
					// adding the starter puts it in the scene, which closes this card
					add();
				});
				row.AddChild(tile);
			}
		}

		private void ToggleGallery()
		{
			gallery.Visible = !gallery.Visible;
		}

		/// <summary>Opens the palette's Box shapes flyout so the user picks the shape; without the palette, adds a cube.</summary>
		private void AddShape()
		{
			var palette = page.Descendants<ShapesPalette>().FirstOrDefault();
			if (palette != null && palette.OpenFlyout("box"))
			{
				return;
			}

			var cube = new PaletteMember(PaletteSource.Primitives, "Cube", "cube");
			if (ShapesPalette.ResolveItem(cube) is ILibraryItem item)
			{
				sceneContext.AddToPlate(new[] { item });
			}
		}

		private void OpenFile()
		{
			// the toolbar's Add to Bed > Add System File to Bed (also the palette's Add model)
			ApplicationController.OpenFileWithSystemDialog(fileNames => sceneContext.AddToPlate(fileNames));
		}
	}
}
