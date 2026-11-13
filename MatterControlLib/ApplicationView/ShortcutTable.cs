using System.Collections.Generic;
using System.Linq;

namespace MatterHackers.MatterControl
{
	/// <summary>EBMC Edition: one row of the keyboard shortcuts sheet.</summary>
	public sealed class ShortcutEntry
	{
		public ShortcutEntry(string group, string keys, string action, string sourceFile, string token, bool advancedTools = false)
		{
			Group = group;
			Keys = keys;
			Action = action;
			SourceFile = sourceFile;
			Token = token;
			AdvancedTools = advancedTools;
		}

		/// <summary>"File", "Edit", "View" or "Tools".</summary>
		public string Group { get; }

		/// <summary>The keys in the "Ctrl + S" notation (KeyHints turns Ctrl into Cmd on a Mac).</summary>
		public string Keys { get; }

		/// <summary>What it does, in plain words (localized by the app).</summary>
		public string Action { get; }

		/// <summary>The file whose handler implements it: "Application.cs" or "View3DWidget.cs".</summary>
		public string SourceFile { get; }

		/// <summary>
		/// Text that the handler in <see cref="SourceFile"/> contains (a <c>case</c> label or call); Tests/EBMC.Tests
		/// checks it is there, so the sheet cannot list a shortcut nobody handles.
		/// </summary>
		public string Token { get; }

		/// <summary>True for the shortcuts added with the Advanced tools (OemSettings.EnableAdvancedTools).</summary>
		public bool AdvancedTools { get; }
	}

	/// <summary>
	/// EBMC Edition: every keyboard and mouse shortcut of the main window, as the keyboard shortcuts sheet lists them. A
	/// hand-maintained list kept next to the handlers in Application.cs (keys) and View3DWidget.cs (mouse modifiers);
	/// Tests/EBMC.Tests checks each entry's token is in its handler file. Edit it when a shortcut is added or changed.
	/// </summary>
	public static class ShortcutTable
	{
		public const string ApplicationFile = "Application.cs";

		public const string View3DFile = "View3DWidget.cs";

		public static IReadOnlyList<string> Groups { get; } = new[] { "File", "Edit", "View", "Tools" };

		public static IReadOnlyList<ShortcutEntry> All { get; } = new[]
		{
			// File
			new ShortcutEntry("File", "Ctrl + S", "Save", ApplicationFile, "case Keys.S:"),
			new ShortcutEntry("File", "Ctrl + P", "Print (the same as the printer tab's Print or Send button)", ApplicationFile, "view3D.PushToPrinterAndPrint();"),

			// Edit
			new ShortcutEntry("Edit", "Ctrl + Z", "Undo", ApplicationFile, "view3D.Scene.Undo();"),
			new ShortcutEntry("Edit", "Ctrl + Y, Ctrl + Shift + Z", "Redo", ApplicationFile, "view3D.Scene.UndoBuffer.Redo();"),
			new ShortcutEntry("Edit", "Ctrl + X", "Cut", ApplicationFile, "view3D.Scene.Cut();"),
			new ShortcutEntry("Edit", "Ctrl + C", "Copy", ApplicationFile, "view3D.Scene.Copy();"),
			new ShortcutEntry("Edit", "Ctrl + V, Shift + Insert", "Paste", ApplicationFile, "case Keys.Insert:"),
			new ShortcutEntry("Edit", "Ctrl + D", "Duplicate the selection", ApplicationFile, "case Keys.D:", advancedTools: true),
			new ShortcutEntry("Edit", "Ctrl + A", "Select all", ApplicationFile, "view3D.SelectAll();"),
			new ShortcutEntry("Edit", "Space", "Clear the selection", ApplicationFile, "case ' ':"),
			new ShortcutEntry("Edit", "Delete, Backspace", "Remove the selection", ApplicationFile, "view3D.Scene.DeleteSelection();"),
			new ShortcutEntry("Edit", "G", "Group the selected parts", ApplicationFile, "case 'g':"),
			new ShortcutEntry("Edit", "Shift + G", "Ungroup", ApplicationFile, "scene.UngroupSelection();"),
			new ShortcutEntry("Edit", "F2", "Rename the selected part", ApplicationFile, "case Keys.F2:"),
			new ShortcutEntry("Edit", "Esc", "Cancel a drag", ApplicationFile, "case Keys.Escape:"),
			new ShortcutEntry("Edit", "Arrow keys", "Nudge the selected part (Shift: 5 times as far, Ctrl: a fifth)", ApplicationFile, "NudgeItem(view3D, object3D, ArrowDirection.Left, keyEvent);"),

			// View
			new ShortcutEntry("View", "W", "Reset the view", ApplicationFile, "view3D.ResetView();"),
			new ShortcutEntry("View", "Z", "Zoom to the selection", ApplicationFile, "view3D.ZoomToSelection();"),
			new ShortcutEntry("View", "Ctrl + '+', Ctrl + '-'", "Zoom in and out", ApplicationFile, "case Keys.Oemplus:"),
			new ShortcutEntry("View", "Arrow keys", "Turn the view (nothing selected)", ApplicationFile, "TrackBallTransformType.Rotation"),
			new ShortcutEntry("View", "Shift + Arrow keys", "Move the view (nothing selected)", ApplicationFile, "TrackBallTransformType.Translation"),
			new ShortcutEntry("View", "Up, Down", "Step through the layers (layer view)", ApplicationFile, "printerTabPage.LayerScrollbar.Value += 1;"),
			new ShortcutEntry("View", "Ctrl + Left, Ctrl + Right", "Step through a layer (layer view)", ApplicationFile, "printerTabPage.LayerFeaturesIndex -= 1;"),
			new ShortcutEntry("View", "Right mouse button, Ctrl + Left mouse button", "Turn the view", View3DFile, "viewControls3D.ActiveButton = ViewControls3DButtons.Rotate;"),
			new ShortcutEntry("View", "Middle mouse button, Ctrl + Shift + Left mouse button", "Move the view", View3DFile, "viewControls3D.ActiveButton = ViewControls3DButtons.Translate;"),
			new ShortcutEntry("View", "Mouse wheel, Ctrl + Alt + Left mouse button", "Zoom", View3DFile, "viewControls3D.ActiveButton = ViewControls3DButtons.Scale;"),
			new ShortcutEntry("View", "F1", "Help", ApplicationFile, "keyEvent.KeyCode == Keys.F1"),

			// Tools
			new ShortcutEntry("Tools", "Ctrl + K", "Find a tool", ApplicationFile, "case Keys.K:", advancedTools: true),
			new ShortcutEntry("Tools", "S", "Find a tool (in the 3D view, Advanced)", ApplicationFile, "case 's':", advancedTools: true),
			new ShortcutEntry("Tools", "?", "Keyboard shortcuts (Advanced)", ApplicationFile, "case '?':", advancedTools: true),
		};

		/// <summary>The rows to show: every row, or without the Advanced tools' rows when they are off.</summary>
		public static IEnumerable<ShortcutEntry> Shown(bool advancedTools) => All.Where(entry => advancedTools || !entry.AdvancedTools);

		/// <summary>The keys of the row for <paramref name="action"/> (for hints and command search), or null.</summary>
		public static string KeysFor(string action) => All.FirstOrDefault(entry => entry.Action == action)?.Keys;

		/// <summary>
		/// The keys as a computer's keyboard names them: on a Mac the key labelled Delete is the one sent as Backspace, so
		/// "Delete, Backspace" reads "Delete", and there is no Insert key, so "Shift + Insert" is left out. Other systems
		/// keep the text. (KeyHints then turns Ctrl into Cmd.)
		/// </summary>
		public static string KeysForSystem(string keys, bool mac)
		{
			if (!mac || string.IsNullOrEmpty(keys))
			{
				return keys;
			}

			var parts = keys.Split(',')
				.Select(part => part.Trim())
				.Where(part => part != "Shift + Insert")
				.Select(part => part == "Backspace" ? "Delete" : part)
				.Distinct()
				.ToList();

			return string.Join(", ", parts);
		}
	}
}
