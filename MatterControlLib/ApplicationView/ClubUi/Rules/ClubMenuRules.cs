using System;
using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl.ClubUi
{
	/// <summary>
	/// EBMC Edition (ClubDialogsAndMenus): one entry of a declarative menu description. The UI maps the Id to the
	/// existing action (same action, same enable condition); the entry only says where it goes and how it reads.
	/// </summary>
	public sealed class ClubMenuEntry
	{
		public ClubMenuEntry(string id, string title, string section, bool danger = false, bool opensDialog = false, string icon = null, string shortcut = null)
		{
			Id = id;
			Title = title;
			Section = section;
			Danger = danger;
			OpensDialog = opensDialog;
			Icon = icon;
			Shortcut = shortcut;
		}

		public string Id { get; }

		/// <summary>English, sentence case; the UI localizes it. Null keeps the action's own title unchanged.</summary>
		public string Title { get; }

		public string Section { get; }

		/// <summary>Shown last, in the danger text colour.</summary>
		public bool Danger { get; }

		/// <summary>The item opens a dialog or asks first, so its title ends with an ellipsis.</summary>
		public bool OpensDialog { get; }

		/// <summary>Optional icon file under StaticData/Icons (tinted by the UI); null keeps the action's own icon.</summary>
		public string Icon { get; }

		/// <summary>Optional shortcut in the "Ctrl + S" notation; null keeps the action's own.</summary>
		public string Shortcut { get; }
	}

	/// <summary>A section of a menu: an optional small-caps header and its entries.</summary>
	public sealed class ClubMenuSection
	{
		public ClubMenuSection(string id, string header = null)
		{
			Id = id;
			Header = header;
		}

		public string Id { get; }

		/// <summary>English header text (shown in small caps), or null for none.</summary>
		public string Header { get; }
	}

	public sealed class ClubMenuSpec
	{
		public ClubMenuSpec(string name, IReadOnlyList<ClubMenuSection> sections, IReadOnlyList<ClubMenuEntry> entries)
		{
			Name = name;
			Sections = sections;
			Entries = entries;
		}

		public string Name { get; }

		public IReadOnlyList<ClubMenuSection> Sections { get; }

		public IReadOnlyList<ClubMenuEntry> Entries { get; }

		public ClubMenuEntry Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
	}

	public enum ClubMenuRowKind
	{
		Header,
		Item,
		Separator,
	}

	public sealed class ClubMenuRow
	{
		public ClubMenuRow(ClubMenuRowKind kind, string text = null, ClubMenuEntry entry = null)
		{
			Kind = kind;
			Text = text;
			Entry = entry;
		}

		public ClubMenuRowKind Kind { get; }

		/// <summary>The header text for a Header row.</summary>
		public string Text { get; }

		public ClubMenuEntry Entry { get; }

		public override string ToString()
		{
			switch (Kind)
			{
				case ClubMenuRowKind.Header:
					return "#" + Text;
				case ClubMenuRowKind.Separator:
					return "-";
				default:
					return Entry.Id;
			}
		}
	}

	/// <summary>
	/// EBMC Edition: lays out a menu description. Pure, so Tests/EBMC.Tests check the same order the UI builds.
	/// </summary>
	public static class ClubMenuLayout
	{
		/// <summary>
		/// The rows of the menu: each section with at least one shown entry, in description order, starting with its
		/// header (if it has one); then every shown danger entry, last, whatever its section. One separator between
		/// non-empty groups; none at the ends.
		/// </summary>
		public static IReadOnlyList<ClubMenuRow> Arrange(ClubMenuSpec spec, Func<string, bool> isShown)
		{
			var groups = new List<(string header, List<ClubMenuEntry> entries)>();
			foreach (var section in spec.Sections)
			{
				var entries = spec.Entries
					.Where(e => e.Section == section.Id && !e.Danger && isShown(e.Id))
					.ToList();
				groups.Add((section.Header, entries));
			}

			groups.Add((null, spec.Entries.Where(e => e.Danger && isShown(e.Id)).ToList()));

			var rows = new List<ClubMenuRow>();
			foreach (var (header, entries) in groups.Where(g => g.entries.Count > 0))
			{
				if (rows.Count > 0)
				{
					rows.Add(new ClubMenuRow(ClubMenuRowKind.Separator));
				}

				if (header != null)
				{
					rows.Add(new ClubMenuRow(ClubMenuRowKind.Header, header));
				}

				rows.AddRange(entries.Select(e => new ClubMenuRow(ClubMenuRowKind.Item, entry: e)));
			}

			return rows;
		}

		/// <summary>
		/// For a menu built from a flat list where items can be left out (a library menu hides what does not apply):
		/// which rows to keep, given which are separators and which items are shown. Separators are kept only between
		/// two shown items, never two in a row, and never first or last.
		/// </summary>
		public static IReadOnlyList<bool> CollapseSeparators(IReadOnlyList<bool> isSeparator, IReadOnlyList<bool> isShown)
		{
			var keep = new bool[isSeparator.Count];
			bool itemSinceSeparator = false;
			int pendingSeparator = -1;
			for (int i = 0; i < isSeparator.Count; i++)
			{
				if (isSeparator[i])
				{
					if (itemSinceSeparator)
					{
						pendingSeparator = i;
						itemSinceSeparator = false;
					}

					continue;
				}

				if (!isShown[i])
				{
					continue;
				}

				if (pendingSeparator >= 0)
				{
					keep[pendingSeparator] = true;
					pendingSeparator = -1;
				}

				keep[i] = true;
				itemSinceSeparator = true;
			}

			return keep;
		}
	}

	/// <summary>EBMC Edition: menu wording rules.</summary>
	public static class ClubMenuText
	{
		public const string Ellipsis = "…";

		/// <summary>
		/// An item that opens a dialog ends with one ellipsis character; three dots become the character. Other titles
		/// lose a trailing ellipsis.
		/// </summary>
		public static string WithEllipsis(string title, bool opensDialog)
		{
			if (string.IsNullOrEmpty(title))
			{
				return title;
			}

			var bare = title.TrimEnd();
			if (bare.EndsWith("..."))
			{
				bare = bare.Substring(0, bare.Length - 3).TrimEnd();
			}
			else if (bare.EndsWith(Ellipsis))
			{
				bare = bare.Substring(0, bare.Length - 1).TrimEnd();
			}

			return opensDialog ? bare + Ellipsis : bare;
		}

		/// <summary>
		/// <paramref name="text"/> if it is no wider than <paramref name="maxWidth"/> (as <paramref name="measure"/> says),
		/// otherwise the longest start of it that fits with "…" after it (at least the ellipsis).
		/// </summary>
		public static string Ellipsize(string text, double maxWidth, Func<string, double> measure)
		{
			if (string.IsNullOrEmpty(text)
				|| measure(text) <= maxWidth)
			{
				return text;
			}

			for (int length = text.Length - 1; length > 0; length--)
			{
				var candidate = text.Substring(0, length).TrimEnd() + Ellipsis;
				if (measure(candidate) <= maxWidth)
				{
					return candidate;
				}
			}

			return Ellipsis;
		}

		/// <summary>The shortcut hint as shown in a menu: Cmd and Option on a Mac, "Ctrl + S" spacing elsewhere.</summary>
		public static string ShortcutText(string hint, bool mac) => KeyHintRules.ForSystem(hint, mac);

		/// <summary>The "show the file on disk" wording for the computer's own file manager.</summary>
		public static string ShowOnDiskTitle(bool mac, bool windows)
		{
			if (mac)
			{
				return "Show in Finder";
			}

			return windows ? "Show in Explorer" : "Show in Files";
		}
	}

	/// <summary>
	/// EBMC Edition: the club menu descriptions. The UI builds these menus from them, mapping each Id to the action that
	/// already existed (see ClubMenuBuilder and the menu files named in each description).
	/// </summary>
	public static class ClubMenus
	{
		// Printer Options ("⋯" on the printer bar, PrinterActionsBar.GeneratePrinterOverflowMenu)
		public const string ShowPrinter = "ShowPrinter";
		public const string ShowControls = "ShowControls";
		public const string ShowTerminal = "ShowTerminal";
		public const string Calibrate = "Calibrate";
		public const string UpdateSettings = "UpdateSettings";
		public const string RestoreSettings = "RestoreSettings";
		public const string ImportSettings = "ImportSettings";
		public const string ExportSettings = "ExportSettings";
		public const string Eeprom = "Eeprom";
		public const string MaintainerLock = "MaintainerLock";
		public const string ResetSettings = "ResetSettings";
		public const string DeletePrinter = "DeletePrinter";

		public static ClubMenuSpec PrinterOptions { get; } = new ClubMenuSpec(
			"Printer options",
			new[]
			{
				new ClubMenuSection("show", "Show"),
				new ClubMenuSection("maintenance", "Maintenance"),
				new ClubMenuSection("lock"),
			},
			new[]
			{
				new ClubMenuEntry(ShowPrinter, "Printer settings panel", "show"),
				new ClubMenuEntry(ShowControls, "Controls", "show"),
				new ClubMenuEntry(ShowTerminal, "Terminal", "show"),
				new ClubMenuEntry(Calibrate, "Calibrate", "maintenance", opensDialog: true, icon: "compass.png"),
				new ClubMenuEntry(UpdateSettings, "Update settings", "maintenance", opensDialog: true, icon: "fa-refresh_14.png"),
				new ClubMenuEntry(RestoreSettings, "Restore settings", "maintenance", opensDialog: true),
				new ClubMenuEntry(ImportSettings, "Import settings", "maintenance", opensDialog: true),
				new ClubMenuEntry(ExportSettings, "Export settings", "maintenance", opensDialog: true, icon: "cube_export.png"),
				new ClubMenuEntry(Eeprom, "Firmware settings (EEPROM)", "maintenance", opensDialog: true, icon: "memory_16x16.png"),
				// title comes from the action: "Unlock maintainer settings…" or "Lock maintainer settings"
				new ClubMenuEntry(MaintainerLock, null, "lock"),
				new ClubMenuEntry(ResetSettings, "Reset to club defaults", "danger", danger: true, opensDialog: true, icon: "undo.png"),
				new ClubMenuEntry(DeletePrinter, "Delete printer", "danger", danger: true, opensDialog: true, icon: "remove.png"),
			});

		// Bed menu (3D view toolbar, ViewToolBarControls.SetView3DWidget) and the bed right-click (View3DWidget)
		public const string OpenRecent = "OpenRecent";
		public const string AddFile = "AddFile";
		public const string Edit = "Edit";
		public const string Print = "Print";
		public const string Save = "Save";
		public const string SaveAs = "SaveAs";
		public const string Export = "Export";
		public const string ArrangeAll = "ArrangeAll";
		public const string ClearBed = "ClearBed";
		public const string Paste = "Paste";
		public const string SelectAll = "SelectAll";

		/// <summary>
		/// The Bed menu. The Edit row and Print stay (they are not dead) and keep their relative order to the other
		/// existing items (Open recent, Edit, Print, Export, Clear bed); Print keeps its own title, shortcut and
		/// enable rule.
		/// </summary>
		public static ClubMenuSpec Bed { get; } = new ClubMenuSpec(
			"Bed",
			new[]
			{
				new ClubMenuSection("open"),
				new ClubMenuSection("edit"),
				new ClubMenuSection("print"),
				new ClubMenuSection("file"),
				new ClubMenuSection("arrange"),
			},
			new[]
			{
				new ClubMenuEntry(OpenRecent, "Open recent", "open"),
				new ClubMenuEntry(AddFile, "Add a file", "open", opensDialog: true, icon: "fa-folder-open_16.png"),
				new ClubMenuEntry(Edit, null, "edit"),
				new ClubMenuEntry(Print, null, "print"),
				new ClubMenuEntry(Save, "Save", "file", icon: "save_grey_16x.png", shortcut: "Ctrl + S"),
				new ClubMenuEntry(SaveAs, "Save as", "file", opensDialog: true),
				new ClubMenuEntry(Export, "Export", "file", opensDialog: true, icon: "cube_export.png"),
				new ClubMenuEntry(ArrangeAll, "Arrange all", "arrange", icon: "arrange_all.png"),
				// Clear bed does not ask first, so it has no ellipsis
				new ClubMenuEntry(ClearBed, "Clear bed", "danger", danger: true, icon: "remove.png"),
			});

		/// <summary>Right-click on the empty bed. Save, Save as, Export and Print keep their place.</summary>
		public static ClubMenuSpec BedContext { get; } = new ClubMenuSpec(
			"Bed right-click",
			new[]
			{
				new ClubMenuSection("edit"),
				new ClubMenuSection("file"),
				new ClubMenuSection("print"),
				new ClubMenuSection("arrange"),
			},
			new[]
			{
				new ClubMenuEntry(Paste, "Paste", "edit", shortcut: "Ctrl + V"),
				new ClubMenuEntry(SelectAll, "Select all", "edit", shortcut: "Ctrl + A"),
				new ClubMenuEntry(Save, "Save", "file", icon: "save_grey_16x.png", shortcut: "Ctrl + S"),
				new ClubMenuEntry(SaveAs, "Save as", "file", opensDialog: true),
				new ClubMenuEntry(Export, "Export", "file", opensDialog: true, icon: "cube_export.png"),
				new ClubMenuEntry(Print, null, "print"),
				new ClubMenuEntry(ArrangeAll, "Arrange all", "arrange", icon: "arrange_all.png"),
				new ClubMenuEntry(AddFile, "Add a file", "arrange", opensDialog: true, icon: "fa-folder-open_16.png"),
				new ClubMenuEntry(ClearBed, "Clear bed", "danger", danger: true, icon: "remove.png"),
			});

		// Right-click on a part (ApplicationController.GetActionMenuForSceneItem)
		public const string Duplicate = "Duplicate";
		public const string MirrorCopy = "MirrorCopy";
		public const string HoleOrSolid = "HoleOrSolid";
		public const string LayFlat = "LayFlat";
		public const string Cut = "Cut";
		public const string Copy = "Copy";
		public const string PasteInto = "PasteInto";
		public const string Rename = "Rename";
		public const string MoreTools = "MoreTools";
		public const string OpenWith = "OpenWith";
		public const string ExportPart = "ExportPart";
		public const string Delete = "Delete";

		/// <summary>
		/// The part right-click. Save as (an existing item, it saves the whole bed) stays beside Export this part.
		/// Items added by the part itself or the reorder items follow the description, as before.
		/// </summary>
		public static ClubMenuSpec PartContext { get; } = new ClubMenuSpec(
			"Part right-click",
			new[]
			{
				new ClubMenuSection("quick"),
				new ClubMenuSection("clipboard"),
				new ClubMenuSection("more"),
			},
			new[]
			{
				new ClubMenuEntry(Duplicate, "Duplicate", "quick", icon: "duplicate.png", shortcut: "Ctrl + D"),
				new ClubMenuEntry(MirrorCopy, "Mirror copy", "quick", opensDialog: true, icon: "mirror_32x32.png"),
				// title from the item's output type: "Make it a hole" or "Make it solid"
				new ClubMenuEntry(HoleOrSolid, null, "quick"),
				new ClubMenuEntry(LayFlat, "Lay flat", "quick", icon: "lay_flat.png"),
				new ClubMenuEntry(Cut, "Cut", "clipboard", shortcut: "Ctrl + X"),
				new ClubMenuEntry(Copy, "Copy", "clipboard", shortcut: "Ctrl + C"),
				new ClubMenuEntry(Paste, "Paste", "clipboard", shortcut: "Ctrl + V"),
				new ClubMenuEntry(PasteInto, "Paste into group", "clipboard"),
				new ClubMenuEntry(Rename, "Rename", "more", opensDialog: true, icon: "icon_edit.png", shortcut: "F2"),
				new ClubMenuEntry(MoreTools, "More tools", "more"),
				new ClubMenuEntry(OpenWith, "Open with", "more"),
				new ClubMenuEntry(SaveAs, "Save as", "more", opensDialog: true),
				new ClubMenuEntry(ExportPart, "Export this part", "more", opensDialog: true, icon: "cube_export.png"),
				new ClubMenuEntry(Delete, "Delete", "danger", danger: true, icon: "remove.png", shortcut: "Delete"),
			});

		// Library item and file menus (LibraryWidget.CreateMenuActions)
		public const string LibPrint = "LibPrint";
		public const string LibOpen = "LibOpen";
		public const string LibOpenCopy = "LibOpenCopy";
		public const string LibAddToBed = "LibAddToBed";
		public const string LibRename = "LibRename";
		public const string LibMove = "LibMove";
		public const string LibShowOnDisk = "LibShowOnDisk";
		public const string LibExport = "LibExport";
		public const string LibShare = "LibShare";
		public const string LibOther = "LibOther";
		public const string LibRemove = "LibRemove";
		public const string LibAdd = "LibAdd";
		public const string LibCreateFolder = "LibCreateFolder";
		public const string LibShareCode = "LibShareCode";

		/// <summary>
		/// The library's right-click and overflow menus. Print stays first (where it is today). Items registered by other
		/// code (LibOther) go before the danger item. The two cloud-sharing items are vendor-only and only listed when
		/// the vendor services are on.
		/// </summary>
		public static ClubMenuSpec Library { get; } = new ClubMenuSpec(
			"Library",
			new[]
			{
				new ClubMenuSection("folder"),
				new ClubMenuSection("print"),
				new ClubMenuSection("open"),
				new ClubMenuSection("file"),
				new ClubMenuSection("other"),
			},
			new[]
			{
				new ClubMenuEntry(LibAdd, "Add files", "folder", opensDialog: true),
				new ClubMenuEntry(LibCreateFolder, "Create folder", "folder", opensDialog: true),
				new ClubMenuEntry(LibShareCode, "Enter share code", "folder", opensDialog: true),
				new ClubMenuEntry(LibPrint, null, "print"),
				new ClubMenuEntry(LibOpen, "Open", "open"),
				new ClubMenuEntry(LibOpenCopy, "Open a copy", "open"),
				new ClubMenuEntry(LibAddToBed, "Add to bed", "open"),
				new ClubMenuEntry(LibRename, "Rename", "file", opensDialog: true),
				new ClubMenuEntry(LibMove, "Move", "file", opensDialog: true),
				new ClubMenuEntry(LibShowOnDisk, null, "file"),
				new ClubMenuEntry(LibExport, "Export", "file", opensDialog: true),
				new ClubMenuEntry(LibShare, "Share", "file", opensDialog: true),
				new ClubMenuEntry(LibOther, null, "other"),
				new ClubMenuEntry(LibRemove, "Remove", "danger", danger: true, opensDialog: true),
			});

		// The brand (EBMC) menu (BrandMenuButton)
		public const string OpenFile = "OpenFile";
		public const string FindTool = "FindTool";
		public const string Help = "Help";
		public const string Shortcuts = "Shortcuts";
		public const string Tour = "Tour";
		public const string CheckForUpdate = "CheckForUpdate";
		public const string Settings = "Settings";
		public const string Community = "Community";
		public const string About = "About";

		public static ClubMenuSpec Brand { get; } = new ClubMenuSpec(
			"Brand",
			new[]
			{
				new ClubMenuSection("file"),
				new ClubMenuSection("help"),
				new ClubMenuSection("vendor"),
				new ClubMenuSection("settings"),
				new ClubMenuSection("about"),
			},
			new[]
			{
				new ClubMenuEntry(OpenFile, "Open file", "file", opensDialog: true, icon: "fa-folder-open_16.png"),
				new ClubMenuEntry(FindTool, "Find a tool", "file", opensDialog: true, icon: "icon_search_24x24.png", shortcut: "Ctrl + K"),
				new ClubMenuEntry(Help, "Help", "help", icon: "help_page.png", shortcut: "F1"),
				new ClubMenuEntry(Shortcuts, "Keyboard shortcuts", "help", opensDialog: true, shortcut: "?"),
				new ClubMenuEntry(Tour, "Interface tour", "help", opensDialog: true, icon: "tour.png"),
				new ClubMenuEntry(CheckForUpdate, "Check for update", "vendor", opensDialog: true, icon: "update.png"),
				new ClubMenuEntry(Community, "Community", "vendor", icon: "feedback.png"),
				new ClubMenuEntry(Settings, "Settings", "settings", opensDialog: true, icon: "fa-cog_16.png"),
				// title from the product name: "About EBMC"
				new ClubMenuEntry(About, null, "about", opensDialog: true),
			});
	}
}
