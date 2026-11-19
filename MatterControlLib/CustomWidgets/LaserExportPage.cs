using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.ClubUi;
using MatterHackers.MatterControl.DesignTools.Laser;
using MatterHackers.MatterControl.Library;
using MatterHackers.MatterControl.Library.Export;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: "Export for laser" (OemSettings.EnableLaserExport): saves the 2D outline of the selection or the whole
	/// design as SVG or DXF in mm for a laser cutter's own software, or (OemSettings.EnableLaserGcode, maintainer unlocked)
	/// as a laser G-code file (.nc). It has no printer: the export runs with no printer attached, so it can never slice,
	/// send or print, and the laser file is never sent anywhere.
	/// </summary>
	public class LaserExportPage : DialogPage
	{
		private readonly ISceneContext sceneContext;

		private readonly FlowLayoutWidget column;

		private RadioButton selectedOption;

		private RadioButton dxfOption;

		private RadioButton gcodeOption;

		private TextWidget sizeLine;

		// laser G-code fields (only when it is offered)
		private FlowLayoutWidget gcodePanel;

		private MHDropDownList machineList;

		private WrappedTextWidget machineNote;

		private RadioButton cutOption;

		private ThemedTextEditWidget powerEdit;

		private ThemedTextEditWidget speedEdit;

		private ThemedTextEditWidget passesEdit;

		private ThemedTextEditWidget materialEdit;

		private TextWidget powerLimit;

		private TextWidget speedLimit;

		private WrappedTextWidget problemsText;

		private string shownOperation;

		public LaserExportPage(ISceneContext sceneContext)
			: base("Cancel".Localize())
		{
			this.sceneContext = sceneContext;
			var title = "Export for laser".Localize();
			this.WindowTitle = ApplicationController.Instance.ProductName + " - " + title;
			this.HeaderText = title;
			this.Name = "Laser Export Window";
			bool gcode = LaserTools.GcodeAvailable;
			this.WindowSize = new Vector2(520 * GuiWidget.DeviceScale, (gcode ? 640 : 460) * GuiWidget.DeviceScale);

			var scrollable = new ScrollableWidget(true)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Stretch,
			};
			scrollable.ScrollArea.HAnchor = HAnchor.Stretch;
			contentRow.AddChild(scrollable);

			column = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			scrollable.AddChild(column);

			var scene = sceneContext.Scene;
			bool hasSelection = scene.SelectedItem != null;

			// What
			AddHeading("What".Localize());
			var whatSiblings = new List<GuiWidget>();
			selectedOption = AddOption("Selected parts".Localize(), whatSiblings, hasSelection);
			selectedOption.Enabled = hasSelection;
			var wholeOption = AddOption("Whole design".Localize(), whatSiblings, !hasSelection);
			selectedOption.CheckedStateChanged += (s, e) => UpdateSize();
			wholeOption.CheckedStateChanged += (s, e) => UpdateSize();

			sizeLine = new TextWidget("", pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor)
			{
				Margin = new BorderDouble(left: 4, top: 4, bottom: 4),
				HAnchor = HAnchor.Left,
			};
			column.AddChild(sizeLine);

			// Format
			AddHeading("Format".Localize());
			var formatSiblings = new List<GuiWidget>();
			AddOption("SVG (xTool, LightBurn, Inkscape)".Localize(), formatSiblings, true);
			dxfOption = AddOption("DXF (LightBurn, CAD)".Localize(), formatSiblings, false);
			if (gcode)
			{
				// maintainers only (the lock is unlocked); absent otherwise
				gcodeOption = AddOption("G-code for laser (.nc)".Localize(), formatSiblings, false);
			}

			// Outline
			AddHeading("Outline".Localize());
			var chooser = LaserOutlineChooser.Create(theme, null);
			chooser.Margin = new BorderDouble(left: 4);
			column.AddChild(chooser);

			if (gcode)
			{
				AddGcodePanel();
				gcodeOption.CheckedStateChanged += (s, e) => gcodePanel.Visible = gcodeOption.Checked;
				gcodePanel.Visible = false;
			}

			UpdateSize();

			var exportButton = theme.CreateDialogButton("Export".Localize());
			exportButton.Name = "Laser Export Button";
			exportButton.Click += (s, e) => Export();
			this.AddPageAction(exportButton);
		}

		/// <summary>Shows the page for this design tab.</summary>
		public static void Show(ISceneContext sceneContext)
		{
			UiThread.RunOnIdle(() => DialogWindow.Show(new LaserExportPage(sceneContext)));
		}

		private void AddHeading(string text, GuiWidget parent = null)
		{
			(parent ?? column).AddChild(new TextWidget(text, pointSize: theme.DefaultFontSize, textColor: theme.PrimaryAccentColor, bold: true)
			{
				Margin = new BorderDouble(0, 2, 0, 10),
				HAnchor = HAnchor.Left,
			});
		}

		private RadioButton AddOption(string text, List<GuiWidget> siblings, bool isChecked, GuiWidget parent = null)
		{
			var option = new RadioButton(new RadioButtonViewText(text, theme.TextColor, fontSize: theme.DefaultFontSize))
			{
				HAnchor = HAnchor.Left,
				Cursor = Cursors.Hand,
				Margin = new BorderDouble(4, 3),
				Checked = isChecked,
				SiblingRadioButtonList = siblings,
			};
			siblings.Add(option);
			(parent ?? column).AddChild(option);
			return option;
		}

		private WrappedTextWidget AddText(GuiWidget parent, string text, double pointSizeDelta = -1)
		{
			var widget = new WrappedTextWidget(text, pointSize: theme.DefaultFontSize + pointSizeDelta, textColor: theme.TextColor)
			{
				HAnchor = HAnchor.Stretch,
				Margin = new BorderDouble(4, 2),
			};
			parent.AddChild(widget);
			return widget;
		}

		/// <summary>A label, a text field and an optional hint on one row.</summary>
		private ThemedTextEditWidget AddField(GuiWidget parent, string label, string text, double width, out TextWidget hint)
		{
			var row = new FlowLayoutWidget()
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
				Margin = new BorderDouble(4, 3),
			};
			row.AddChild(new TextWidget(label, pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
			{
				VAnchor = VAnchor.Center,
				MinimumSize = new Vector2(130 * GuiWidget.DeviceScale, 0),
			});
			var edit = new ThemedTextEditWidget(text ?? "", theme, pixelWidth: width * GuiWidget.DeviceScale)
			{
				VAnchor = VAnchor.Center,
			};
			row.AddChild(edit);
			hint = new TextWidget("", pointSize: theme.DefaultFontSize - 1, textColor: theme.TextColor)
			{
				VAnchor = VAnchor.Center,
				Margin = new BorderDouble(left: 6),
			};
			row.AddChild(hint);
			parent.AddChild(row);
			return edit;
		}

		private void AddGcodePanel()
		{
			gcodePanel = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				VAnchor = VAnchor.Fit,
			};
			column.AddChild(gcodePanel);

			AddHeading("Laser G-code".Localize(), gcodePanel);
			AddText(gcodePanel, "Outlines only: no engraving or fill, no kerf allowance, no lead-ins and no tabs. The file is for a laser cutter, not the 3D printer.".Localize());

			// machine
			var machines = LaserDataFiles.Machines;
			machineList = new MHDropDownList("Choose a machine".Localize(), theme, maxHeight: 200 * GuiWidget.DeviceScale)
			{
				HAnchor = HAnchor.Left,
				Margin = new BorderDouble(4, 3),
			};
			foreach (var machine in machines)
			{
				machineList.AddItem(machine.Name, machine.Id);
			}

			gcodePanel.AddChild(machineList);
			machineNote = AddText(gcodePanel, "");
			var startMachine = machines.FirstOrDefault(m => m.Id == LaserGcodeSession.MachineId) ?? machines.FirstOrDefault();
			if (startMachine != null)
			{
				machineList.SelectedValue = startMachine.Id;
			}

			machineList.SelectionChanged += (s, e) =>
			{
				LaserGcodeSession.MachineId = machineList.SelectedValue;
				UpdateMachine();
			};

			// operation: Cut or Score (a label, and each remembers its own values)
			var operationSiblings = new List<GuiWidget>();
			cutOption = AddOption("Cut".Localize(), operationSiblings, LaserGcodeSession.Operation != "Score", gcodePanel);
			var scoreOption = AddOption("Score".Localize(), operationSiblings, LaserGcodeSession.Operation == "Score", gcodePanel);
			shownOperation = Operation();

			// approved presets only (none until the owner approves values)
			var offered = LaserData.OfferedPresets(LaserDataFiles.Materials, "cut").Concat(LaserData.OfferedPresets(LaserDataFiles.Materials, "score")).Distinct().ToList();
			if (offered.Count > 0)
			{
				var presetList = new MHDropDownList("Approved material settings".Localize(), theme, maxHeight: 200 * GuiWidget.DeviceScale)
				{
					HAnchor = HAnchor.Left,
					Margin = new BorderDouble(4, 3),
				};
				foreach (var preset in offered)
				{
					presetList.AddItem(preset.Name, preset.Id);
				}

				presetList.SelectionChanged += (s, e) =>
				{
					var preset = LaserData.OfferedPresets(LaserDataFiles.Materials, Operation()).FirstOrDefault(p => p.Id == presetList.SelectedValue);
					if (preset != null
						&& preset.Operations.TryGetValue(Operation().ToLowerInvariant(), out var values))
					{
						powerEdit.Text = values.PowerPercent.ToString(CultureInfo.InvariantCulture);
						speedEdit.Text = values.SpeedMmS.ToString(CultureInfo.InvariantCulture);
						passesEdit.Text = values.Passes.ToString(CultureInfo.InvariantCulture);
						materialEdit.Text = preset.Name;
					}
				};
				gcodePanel.AddChild(presetList);
			}

			LaserGcodeSession.Typed.TryGetValue(shownOperation, out var typed);
			powerEdit = AddField(gcodePanel, "Power (%)".Localize(), typed.power, 70, out powerLimit);
			speedEdit = AddField(gcodePanel, "Speed (mm/s)".Localize(), typed.speed, 70, out speedLimit);
			passesEdit = AddField(gcodePanel, "Passes".Localize(), typed.passes, 70, out var passesHint);
			passesHint.Text = $"{LaserGcodeRules.MinPasses}–{LaserGcodeRules.MaxPasses}";
			materialEdit = AddField(gcodePanel, "Material note".Localize(), LaserGcodeSession.MaterialNote, 220, out _);

			cutOption.CheckedStateChanged += (s, e) => SwitchOperation();
			scoreOption.CheckedStateChanged += (s, e) => SwitchOperation();

			AddHeading("Before cutting".Localize(), gcodePanel);
			foreach (var line in new[] { "Lid closed", "Air assist on", "Focus set", "Stay with the machine while it runs", "Extinguisher to hand" })
			{
				AddText(gcodePanel, "•  " + line.Localize(), 0);
			}

			problemsText = new WrappedTextWidget("", pointSize: theme.DefaultFontSize, textColor: ClubStyle.DangerText)
			{
				HAnchor = HAnchor.Stretch,
				Margin = new BorderDouble(4, 6),
			};
			gcodePanel.AddChild(problemsText);

			UpdateMachine();
		}

		private string Operation() => cutOption == null || cutOption.Checked ? "Cut" : "Score";

		private LaserMachineProfile SelectedMachine() => LaserDataFiles.Machines.FirstOrDefault(m => m.Id == machineList?.SelectedValue);

		private void UpdateMachine()
		{
			var machine = SelectedMachine();
			if (machine == null)
			{
				machineNote.Text = "";
				powerLimit.Text = "";
				speedLimit.Text = "";
				return;
			}

			var note = machine.Verified
				? ""
				: "Not checked on the machine: check the file in the laser's own software before cutting.".Localize();
			if (!string.IsNullOrWhiteSpace(machine.Note))
			{
				note = (note + " " + machine.Note).Trim();
			}

			if (LaserDataFiles.LoadNotes.Count > 0)
			{
				note = (note + " " + string.Join(" ", LaserDataFiles.LoadNotes)).Trim();
			}

			machineNote.Text = note;

			// a limit is shown only for a profile checked on the machine; a placeholder's numbers are not facts about it
			// (its limits still apply, and a refusal names the profile as a placeholder)
			string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
			powerLimit.Text = machine.Verified ? $"up to {N(LaserGcodeRules.PowerLimit(machine))}" : "";
			speedLimit.Text = machine.Verified ? $"up to {N(machine.MaxCutSpeedMmS)}" : "";
		}

		private void RememberTyped(string operation)
		{
			LaserGcodeSession.Typed[operation] = (powerEdit.Text, speedEdit.Text, passesEdit.Text);
			LaserGcodeSession.MaterialNote = materialEdit.Text;
		}

		private void SwitchOperation()
		{
			var now = Operation();
			if (now == shownOperation)
			{
				return;
			}

			RememberTyped(shownOperation);
			LaserGcodeSession.Typed.TryGetValue(now, out var typed);
			powerEdit.Text = typed.power ?? "";
			speedEdit.Text = typed.speed ?? "";
			passesEdit.Text = typed.passes ?? "";
			shownOperation = now;
			LaserGcodeSession.Operation = now;
		}

		/// <summary>The selection when that option is picked (and something is selected), otherwise the whole design.</summary>
		private IObject3D Chosen()
		{
			var scene = sceneContext.Scene;
			return selectedOption.Checked && scene.SelectedItem != null
				? scene.SelectedItem
				: scene;
		}

		private void UpdateSize()
		{
			try
			{
				// the box on the bed only: working out the real outline here could take a while on a big part
				var bounds = LaserGeometrySource.WorldBounds(Chosen());
				sizeLine.Text = bounds.XSize > 0 && bounds.YSize > 0 && !double.IsInfinity(bounds.XSize)
					? LaserExportRules.SizeLine(bounds.XSize, bounds.YSize).Localize()
					: "";
			}
			catch
			{
				sizeLine.Text = "";
			}
		}

		private void Export()
		{
			var item = new InMemoryLibraryItem(Chosen());

			// the outline method and height are the shared session setting the chooser above sets
			IExportPlugin plugin;
			LaserExportChoice exportChoice;
			if (gcodeOption?.Checked == true)
			{
				if (!LaserTools.GcodeAvailable)
				{
					problemsText.Text = "Laser G-code needs a maintainer: unlock the maintainer settings first.".Localize();
					return;
				}

				RememberTyped(Operation());
				var job = new LaserJobSettings
				{
					Operation = Operation(),
					MaterialNote = materialEdit.Text,
					Passes = 0,
				};
				var machine = SelectedMachine();

				// strict reading of the typed numbers (no thousands separators), then the range checks; refuse here,
				// before asking where to save. Size and emptiness are checked when the outline is made
				var problems = LaserNumberText.ReadJob(powerEdit.Text, speedEdit.Text, passesEdit.Text, job);
				if (problems.Count == 0)
				{
					problems = LaserGcodeRules.ValidateJob(job, machine);
				}

				if (problems.Count > 0)
				{
					problemsText.Text = string.Join("\n", problems.Select(p => p.Message.Localize()));
					return;
				}

				problemsText.Text = "";
				var gcodeExport = new LaserGcodeExport { Machine = machine, Job = job };
				exportChoice = gcodeExport.Choice;
				plugin = gcodeExport;
			}
			else if (dxfOption.Checked)
			{
				var dxf = new LaserDxfExport();
				exportChoice = dxf.Choice;
				plugin = dxf;
			}
			else
			{
				var svg = new LaserSvgExport();
				exportChoice = svg.Choice;
				plugin = svg;
			}

			exportChoice.Saved = message =>
			{
				if (ClubStyle.On)
				{
					UiThread.RunOnIdle(() => ClubToast.Show(message, 6));
				}
			};

			// no printer: the export can never offer or make 3D-printer G-code
			ExportPrintItemPage.DoExport(new[] { item }, null, plugin);

			this.DialogWindow?.CloseOnIdle();
		}
	}
}
