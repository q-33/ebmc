using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.GuiAutomation;
using MatterHackers.MatterControl.CrossPlatform;

namespace EBMC.UiSmoke
{
	/// <summary>A failed check: its message is the one-line reason printed at the end.</summary>
	public class SmokeFailure : Exception
	{
		public SmokeFailure(string message)
			: base(message)
		{
		}
	}

	/// <summary>
	/// A dialog that does not fit (its text would scroll): the step fails and the run fails at the end, but the
	/// following steps still run, so one run shows everything else too.
	/// </summary>
	public class FitFailure : SmokeFailure
	{
		public FitFailure(string message)
			: base(message)
		{
		}
	}

	/// <summary>
	/// The helpers every flow uses: named steps with a screenshot each, waits on widget state (never fixed delays
	/// for correctness), clicks by name, typing, and reading widget state on the UI thread.
	/// </summary>
	public static class Smoke
	{
		public static AutomationRunner Runner { get; set; }

		public static string OutDir { get; set; }

		public static string DataDir { get; set; }

		public static int StepCount { get; private set; }

		/// <summary>Failed steps after which the flow went on (see <see cref="FitFailure"/>).</summary>
		public static List<string> DeferredFailures { get; } = new List<string>();

		/// <summary>Run once when the process ends, pass or fail.</summary>
		public static List<Action> OnExit { get; } = new List<Action>();

		private static readonly Stopwatch FlowTimer = new Stopwatch();

		public static void StartTimer() => FlowTimer.Start();

		public static double ElapsedSeconds => FlowTimer.Elapsed.TotalSeconds;

		// --- steps and screenshots ---

		public static void Step(string name, Action body)
		{
			StepCount++;
			string label = $"{StepCount:00}-{name}";
			var timer = Stopwatch.StartNew();
			Console.WriteLine($"[{label}] start");
			try
			{
				body();
				NetworkWatch.ThrowIfAny();
			}
			catch (FitFailure e)
			{
				TryCapture(label + "-FAILED");
				Console.WriteLine($"[{label}] FAIL ({timer.Elapsed.TotalSeconds:0.0} s), the run goes on: {e.Message}");
				DeferredFailures.Add($"step {label}: {e.Message}");
				return;
			}
			catch (Exception e)
			{
				TryCapture(label + "-FAILED");
				throw new SmokeFailure($"step {label}: {SmokeProgram.Describe(e)}");
			}

			string file = Capture(label);
			Console.WriteLine($"[{label}] PASS ({timer.Elapsed.TotalSeconds:0.0} s) screenshot {Path.GetFileName(file)}");
		}

		public static void Log(string message) => Console.WriteLine("    " + message);

		public static void TryCaptureNamed(string name) => TryCapture($"{StepCount + 1:00}-{name}");

		private static void TryCapture(string label)
		{
			try
			{
				Capture(label);
			}
			catch (Exception e)
			{
				Console.WriteLine($"[{label}] (no screenshot: {e.Message})");
			}
		}

		/// <summary>
		/// Saves the next frame as a PNG. The read happens on the UI thread (which owns the GL context), in the
		/// AfterDraw of the top-most window: the last thing drawn in a frame, before the buffers are swapped.
		/// </summary>
		public static string Capture(string label)
		{
			var done = new ManualResetEventSlim();
			ImageBuffer image = null;
			Exception error = null;

			UiThread.RunOnIdle(() =>
			{
				var top = SystemWindow.AllOpenSystemWindows.Last();
				EventHandler<DrawEventArgs> handler = null;
				handler = (s, e) =>
				{
					top.AfterDraw -= handler;
					try
					{
						image = FramebufferCapture.ReadBackBuffer();
					}
					catch (Exception ex)
					{
						error = ex;
					}

					done.Set();
				};
				top.AfterDraw += handler;
				top.Invalidate();
			});

			if (!done.Wait(TimeSpan.FromSeconds(15)))
			{
				throw new SmokeFailure($"no frame was drawn within 15 s for screenshot {label}");
			}

			if (error != null)
			{
				throw new SmokeFailure($"screenshot {label}: {error.Message}");
			}

			if (image == null)
			{
				throw new SmokeFailure($"screenshot {label}: no window to read");
			}

			string path = Path.Combine(OutDir, label + ".png");
			if (!ImageIO.SaveImageData(path, image))
			{
				throw new SmokeFailure($"screenshot {label}: could not write {path}");
			}

			return path;
		}

		// --- checks ---

		public static void Require(bool condition, string message)
		{
			if (!condition)
			{
				throw new SmokeFailure(message);
			}
		}

		public static bool WaitFor(Func<bool> condition, double seconds)
		{
			var timer = Stopwatch.StartNew();
			while (timer.Elapsed.TotalSeconds < seconds)
			{
				if (condition())
				{
					return true;
				}

				Thread.Sleep(100);
			}

			return condition();
		}

		public static void WaitUntil(Func<bool> condition, double seconds, string failure)
		{
			Require(WaitFor(condition, seconds), failure + $" (waited {seconds:0} s)");
		}

		/// <summary>Runs <paramref name="read"/> on the UI thread and returns its result (widget state is only
		/// consistent there).</summary>
		public static T Ui<T>(Func<T> read, double seconds = 20)
		{
			if (UiThread.IsUiThread)
			{
				// already there (a helper called from inside another read): queuing would wait for itself
				return read();
			}

			var done = new ManualResetEventSlim();
			T result = default;
			Exception error = null;
			UiThread.RunOnIdle(() =>
			{
				try
				{
					result = read();
				}
				catch (Exception e)
				{
					error = e;
				}

				done.Set();
			});

			if (!done.Wait(TimeSpan.FromSeconds(seconds)))
			{
				throw new SmokeFailure($"the UI thread did not answer within {seconds:0} s");
			}

			if (error != null)
			{
				throw new SmokeFailure("reading the UI failed: " + error.GetType().Name + ": " + error.Message);
			}

			return result;
		}

		public static void Ui(Action action) => Ui(() =>
		{
			action();
			return true;
		});

		/// <summary>The visible widgets with this name, top-most window first. Call on the UI thread.</summary>
		private static List<GuiWidget> VisibleNamedOnUi(string name)
		{
			var found = new List<GuiWidget>();
			foreach (var window in SystemWindow.AllOpenSystemWindows.Reverse())
			{
				found.AddRange(window.FindDescendants(name)
					.Select(w => w.Widget)
					.Where(w => w != null && w.ActuallyVisibleOnScreen()));
			}

			return found;
		}

		public static List<GuiWidget> Visible(string name) => Ui(() => VisibleNamedOnUi(name));

		public static bool IsShown(string name) => Visible(name).Count > 0;

		/// <summary>Waits for a visible widget with this name (and the condition, read on the UI thread).</summary>
		public static GuiWidget WaitForWidget(string name, double seconds = 10, Func<GuiWidget, bool> condition = null, string what = null)
		{
			GuiWidget widget = null;
			bool ok = WaitFor(() =>
			{
				widget = Ui(() => VisibleNamedOnUi(name).FirstOrDefault(w => condition == null || condition(w)));
				return widget != null;
			}, seconds);

			Require(ok, $"\"{name}\"{(what == null ? "" : " " + what)} not shown within {seconds:0} s");
			return widget;
		}

		public static void WaitForGone(string name, double seconds = 10)
		{
			WaitUntil(() => !IsShown(name), seconds, $"\"{name}\" still shown");
		}

		/// <summary>
		/// The widget is drawn where it can be seen: a non-empty rectangle on screen, inside the window and inside
		/// every parent (so a row with no height, or one pushed outside its column, fails here).
		/// </summary>
		public static void RequireOnScreen(GuiWidget widget, string label)
		{
			string problem = Ui(() =>
			{
				var window = widget.Parents<SystemWindow>().FirstOrDefault();
				if (window == null)
				{
					return "is not in a window";
				}

				var bounds = widget.TransformToParentSpace(window, widget.LocalBounds);
				if (bounds.Width < 1 || bounds.Height < 1)
				{
					return $"has an empty area on screen ({bounds.Width:0} x {bounds.Height:0})";
				}

				foreach (var parent in widget.Parents<GuiWidget>())
				{
					var parentBounds = parent.TransformToParentSpace(window, parent.LocalBounds);
					if (bounds.Left < parentBounds.Left - 1 || bounds.Right > parentBounds.Right + 1
						|| bounds.Bottom < parentBounds.Bottom - 1 || bounds.Top > parentBounds.Top + 1)
					{
						// clipped by a scroll area is allowed only partly: at least half must show
						var visible = bounds;
						visible.IntersectWithRectangle(parentBounds);
						if (visible.Width < bounds.Width / 2 || visible.Height < bounds.Height / 2)
						{
							return $"is mostly outside its parent \"{parent.Name}\" ({parent.GetType().Name})";
						}
					}

					if (parent == window)
					{
						break;
					}
				}

				return null;
			});

			Require(problem == null, $"{label} {problem}");
		}

		// --- input ---

		/// <summary>Clicks a named widget once it is shown and enabled (waits for a rebuilt panel first).</summary>
		public static void Click(string name, double seconds = 10)
		{
			var widget = WaitForWidget(name, seconds, w => w.Enabled, "(enabled)");
			Runner.ClickWidget(widget);
		}

		public static void ClickWidget(GuiWidget widget) => Runner.ClickWidget(widget);

		/// <summary>The visible widget showing exactly this text (for the few controls that have no name).</summary>
		public static GuiWidget WaitForText(string text, double seconds = 10)
		{
			GuiWidget widget = null;
			bool ok = WaitFor(() =>
			{
				widget = Ui(() =>
				{
					foreach (var window in SystemWindow.AllOpenSystemWindows.Reverse())
					{
						var match = window.Descendants<GuiWidget>()
							.FirstOrDefault(w => w.Text == text && !(w is TextEditWidget) && w.ActuallyVisibleOnScreen());
						if (match != null)
						{
							return match;
						}
					}

					return null;
				});
				return widget != null;
			}, seconds);

			Require(ok, $"no widget showing \"{text}\" within {seconds:0} s");
			return widget;
		}

		/// <summary>Clicks a named text field, checks it took the focus, and types into it.</summary>
		public static void TypeInto(string fieldName, string text, bool secret = false)
		{
			var field = WaitForWidget(fieldName, 10, w => w.Enabled, "(enabled)");
			Runner.ClickWidget(field);
			WaitUntil(() => Ui(() => field.ContainsFocus), 5, $"\"{fieldName}\" did not take the focus when clicked");
			SelectAllAndDelete();
			Type(text);
			string typed = Ui(() => field.Text);
			Require(typed == text, secret
				? $"\"{fieldName}\" does not hold the typed text"
				: $"\"{fieldName}\" holds \"{typed}\" after typing \"{text}\"");
		}

		/// <summary>
		/// Types like the GLFW host does: a key-down with the key and modifiers, then the character unless the key
		/// press was suppressed, then the key-up. Covers letters (either case), digits, space and - . : /.
		/// </summary>
		public static void Type(string text)
		{
			Ui(() =>
			{
				foreach (char c in text)
				{
					var key = KeyFor(c, out bool shift);
					var keys = key | (shift ? Keys.Shift : Keys.None);
					var top = TopWindow();
					var down = new KeyEventArgs(keys);
					top.OnKeyDown(down);
					if (!down.SuppressKeyPress)
					{
						top.OnKeyPress(new KeyPressEventArgs(c));
					}

					top.OnKeyUp(new KeyEventArgs(keys));
				}
			});
		}

		private static Keys KeyFor(char c, out bool shift)
		{
			shift = false;
			if (c >= 'a' && c <= 'z')
			{
				return (Keys)char.ToUpperInvariant(c);
			}

			if (c >= 'A' && c <= 'Z')
			{
				shift = true;
				return (Keys)c;
			}

			if (c >= '0' && c <= '9')
			{
				return (Keys)c;
			}

			switch (c)
			{
				case ' ':
					return Keys.Space;
				case '-':
					return Keys.OemMinus;
				case '.':
					return Keys.OemPeriod;
				case '/':
					return Keys.OemQuestion;
				case ':':
					shift = true;
					return Keys.OemSemicolon;
				default:
					throw new SmokeFailure($"the test cannot type '{c}'");
			}
		}

		/// <summary>A key with Command held (the host maps Command to Control), like Cmd+C.</summary>
		public static void PressCommand(Keys key)
		{
			Ui(() =>
			{
				var top = TopWindow();
				Keyboard.SetKeyDownState(Keys.ControlKey, true);
				try
				{
					var down = new KeyEventArgs(key | Keys.Control);
					top.OnKeyDown(down);
					top.OnKeyUp(new KeyEventArgs(key | Keys.Control));
				}
				finally
				{
					Keyboard.SetKeyDownState(Keys.ControlKey, false);
				}
			});
		}

		public static void PressEnter()
		{
			Ui(() =>
			{
				var top = TopWindow();
				var down = new KeyEventArgs(Keys.Enter);
				top.OnKeyDown(down);
				if (!down.SuppressKeyPress)
				{
					top.OnKeyPress(new KeyPressEventArgs('\r'));
				}

				top.OnKeyUp(new KeyEventArgs(Keys.Enter));
			});
		}

		private static void SelectAllAndDelete()
		{
			PressCommand(Keys.A);
			Ui(() =>
			{
				var top = TopWindow();
				top.OnKeyDown(new KeyEventArgs(Keys.Back));
				top.OnKeyUp(new KeyEventArgs(Keys.Back));
			});
		}

		private static SystemWindow TopWindow() => SystemWindow.AllOpenSystemWindows.Last();
	}

	/// <summary>What a shown message box holds and where its parts are on screen (device pixels, y up).</summary>
	public sealed class MessageBoxReport
	{
		public string Title { get; set; }

		/// <summary>The text as given to the box.</summary>
		public string Message { get; set; }

		/// <summary>The text as drawn: the box inserts line breaks to wrap it.</summary>
		public string[] DisplayedRows { get; set; }

		public RectangleDouble Window { get; set; }

		public RectangleDouble Page { get; set; }

		public RectangleDouble ScrollArea { get; set; }

		public RectangleDouble Text { get; set; }

		public Dictionary<string, RectangleDouble> Buttons { get; } = new Dictionary<string, RectangleDouble>();

		public double DeviceScale { get; set; }

		/// <summary>What ClubDialogRules.MessageBoxSize returned for the text (units before the device scale).</summary>
		public (int width, int height) RuleSize { get; set; }

		public double WrapWidth { get; set; }

		public string Sizes()
		{
			string Size(RectangleDouble r) => $"{r.Width:0}x{r.Height:0} px ({r.Width / DeviceScale:0}x{r.Height / DeviceScale:0} units)";
			int messageRows = Message.Replace("\r", "").Split('\n').Length;
			return $"box {Size(Page)}; rule asked {RuleSize.width}x{RuleSize.height} units; text area {Size(ScrollArea)}; "
				+ $"text {Size(Text)}; {messageRows} rows given, {DisplayedRows.Length} rows drawn; wrap width {WrapWidth:0} px; scale {DeviceScale}";
		}
	}

	public static class MessageBoxes
	{
		/// <summary>Reads the top-most message box (StyledMessageBox.MessageBoxPage) on the UI thread.</summary>
		public static MessageBoxReport Read() => Smoke.Ui(() =>
		{
			var window = SystemWindow.AllOpenSystemWindows.Last();
			var page = window.Descendants<MatterHackers.MatterControl.StyledMessageBox.MessageBoxPage>().FirstOrDefault();
			if (page == null)
			{
				return null;
			}

			var scrollable = page.Descendants<ScrollableWidget>().First();
			var text = scrollable.Descendants<TextWidget>().First();
			string message = (string)typeof(MatterHackers.MatterControl.StyledMessageBox.MessageBoxPage)
				.GetField("unwrappedMessage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
				.GetValue(page);

			var contentRow = (GuiWidget)typeof(MatterHackers.MatterControl.DialogPage)
				.GetField("contentRow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
				.GetValue(page);

			var report = new MessageBoxReport()
			{
				Title = page.HeaderText,
				Message = message,
				DisplayedRows = text.Text.Replace("\r", "").Split('\n'),
				Window = window.LocalBounds,
				Page = page.TransformToParentSpace(window, page.LocalBounds),
				ScrollArea = scrollable.TransformToParentSpace(window, scrollable.LocalBounds),
				Text = text.TransformToParentSpace(window, text.LocalBounds),
				DeviceScale = GuiWidget.DeviceScale,
				RuleSize = MatterHackers.MatterControl.ClubUi.ClubDialogRules.MessageBoxSize(message),
				WrapWidth = contentRow.Width - (contentRow.Padding.Width + text.Margin.Width),
			};

			foreach (var name in new[] { "Yes Button", "No Button", "Ok Button" })
			{
				var button = page.Descendants<GuiWidget>().FirstOrDefault(w => w.Name == name && w.Visible);
				if (button != null)
				{
					report.Buttons[name + " (" + button.Text + ")"] = button.TransformToParentSpace(window, button.LocalBounds);
				}
			}

			return report;
		});

		/// <summary>Waits for a message box with this title.</summary>
		public static MessageBoxReport WaitFor(string title, double seconds)
		{
			MessageBoxReport report = null;
			Smoke.WaitUntil(() =>
			{
				report = Read();
				return report != null && report.Title == title;
			}, seconds, $"no \"{title}\" box" + (report != null ? $" (the box shown is \"{report.Title}\")" : ""));

			// let it lay out (the wrap is applied when its size is known)
			Thread.Sleep(500);
			return Read();
		}

		/// <summary>
		/// The whole text and every button can be seen without scrolling: the text inside its (scrolling) area, the
		/// buttons and the box inside the window. Fails with the measured sizes.
		/// </summary>
		public static void RequireFits(MessageBoxReport box)
		{
			Smoke.Log("sizes: " + box.Sizes());
			Smoke.Log("rows as drawn:");
			foreach (var row in box.DisplayedRows)
			{
				Smoke.Log("  | " + row);
			}

			var problems = new List<string>();
			bool Inside(RectangleDouble inner, RectangleDouble outer) =>
				inner.Left >= outer.Left - 1 && inner.Right <= outer.Right + 1 && inner.Bottom >= outer.Bottom - 1 && inner.Top <= outer.Top + 1;

			if (!Inside(box.Text, box.ScrollArea))
			{
				problems.Add($"the text ({box.Text.Width:0}x{box.Text.Height:0} px) does not fit its area ({box.ScrollArea.Width:0}x{box.ScrollArea.Height:0} px), so it scrolls");
			}

			if (!Inside(box.Page, box.Window))
			{
				problems.Add("the box is larger than the window");
			}

			if (box.Buttons.Count == 0)
			{
				problems.Add("no buttons found");
			}

			foreach (var button in box.Buttons)
			{
				if (button.Value.Width < 1 || button.Value.Height < 1 || !Inside(button.Value, box.Page) || !Inside(button.Value, box.Window))
				{
					problems.Add($"{button.Key} is not fully inside the box");
				}
			}

			Smoke.Log("buttons: " + string.Join(", ", box.Buttons.Keys));
			if (problems.Count > 0)
			{
				throw new FitFailure(string.Join("; ", problems) + $" [{box.Sizes()}]");
			}
		}
	}

	/// <summary>
	/// Tools/EBMC/octoprint_mock.py on a free 127.0.0.1 port, with its one-line-per-request log (stderr) kept.
	/// </summary>
	public sealed class OctoPrintMock : IDisposable
	{
		private readonly Process process;
		private readonly List<string> log = new List<string>();

		public OctoPrintMock(string key, string state)
		{
			string script = FindScript();
			RequireRealPython();

			var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			Port = ((IPEndPoint)listener.LocalEndpoint).Port;
			listener.Stop();

			// --exit-with-stdin and --parent-pid: the mock ends by itself when this process ends, even when it is killed
			process = Process.Start(new ProcessStartInfo(
				"/usr/bin/python3",
				$"-I \"{script}\" --port {Port} --key {key} --state {state} --exit-with-stdin --parent-pid {Environment.ProcessId}")
			{
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
			});
			process.ErrorDataReceived += (s, e) =>
			{
				if (e.Data != null)
				{
					lock (log)
					{
						log.Add(e.Data);
					}
				}
			};
			process.BeginErrorReadLine();

			Smoke.WaitUntil(() => Log.Any(l => l.StartsWith("OctoPrint mock on")), 10, "the OctoPrint mock did not start");
			string first = Log.First(l => l.StartsWith("OctoPrint mock on"));
			var match = System.Text.RegularExpressions.Regex.Match(first, @"uploads in (.*octoprint-mock-[^)]*)\)");
			UploadsFolder = match.Success ? match.Groups[1].Value : null;
			Console.WriteLine("    mock: " + first);
		}

		/// <summary>The temporary folder the mock made for uploads (it removes it when it stops cleanly).</summary>
		public string UploadsFolder { get; }

		/// <summary>
		/// On a Mac without the Command Line Tools, /usr/bin/python3 is a stub that only offers to install them.
		/// </summary>
		private static void RequireRealPython()
		{
			var check = Process.Start(new ProcessStartInfo("/usr/bin/python3", "-I -c \"import sys; print(sys.version_info[0])\"")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			});
			string output = check.StandardOutput.ReadToEnd();
			string error = check.StandardError.ReadToEnd();
			if (!check.WaitForExit(30000))
			{
				check.Kill();
			}

			if (!check.HasExited || check.ExitCode != 0 || output.Trim() != "3")
			{
				throw new SmokeFailure("/usr/bin/python3 does not run Python 3 (on a Mac without the Command Line Tools it is only a stub; "
					+ "install them with xcode-select --install): " + (error + output).Trim().Replace('\n', ' '));
			}
		}

		public int Port { get; }

		public string BaseUrl => $"http://127.0.0.1:{Port}";

		public IReadOnlyList<string> Log
		{
			get
			{
				lock (log)
				{
					return log.ToList();
				}
			}
		}

		/// <summary>The request lines (without the start-up line), e.g. "12:00:01 GET /api/version -> 200 key=ok".</summary>
		public IReadOnlyList<string> Requests => Log.Where(l => !l.StartsWith("OctoPrint mock on")).ToList();

		public string Files()
		{
			using (var client = new System.Net.Http.HttpClient())
			{
				return client.GetStringAsync(BaseUrl + "/mock/files").GetAwaiter().GetResult();
			}
		}

		/// <summary>
		/// Stops the mock gracefully so it removes its uploads folder: closes its input (it then shuts down), waits,
		/// then SIGTERM, then SIGKILL. A folder left after a forced kill is removed here (only that folder, which the
		/// mock reported making).
		/// </summary>
		public void Dispose()
		{
			try
			{
				if (!process.HasExited)
				{
					process.StandardInput.Close();
					if (!process.WaitForExit(5000))
					{
						Process.Start("/bin/kill", $"-TERM {process.Id}")?.WaitForExit(2000);
						if (!process.WaitForExit(3000))
						{
							process.Kill();
							process.WaitForExit(3000);
						}
					}
				}

				var stopped = Log.LastOrDefault(l => l.StartsWith("OctoPrint mock stopped"));
				Console.WriteLine("    mock: " + (stopped ?? "killed (no clean stop)"));
				if (UploadsFolder != null
					&& Path.GetFileName(UploadsFolder).StartsWith("octoprint-mock-")
					&& Directory.Exists(UploadsFolder))
				{
					Directory.Delete(UploadsFolder, true);
					Console.WriteLine("    mock: removed its uploads folder after a forced stop");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("    mock: stopping it failed: " + e.Message);
			}
		}

		private static string FindScript()
		{
			var folder = new DirectoryInfo(AppContext.BaseDirectory);
			while (folder != null)
			{
				string candidate = Path.Combine(folder.FullName, "Tools", "EBMC", "octoprint_mock.py");
				if (File.Exists(candidate))
				{
					return candidate;
				}

				folder = folder.Parent;
			}

			throw new SmokeFailure("Tools/EBMC/octoprint_mock.py not found above " + AppContext.BaseDirectory);
		}
	}

	/// <summary>
	/// An in-memory clipboard installed before the app starts, so the app never reaches the real system clipboard
	/// during a run (the host only installs its own when none is set).
	/// </summary>
	public class FakeClipboard : ISystemClipboard
	{
		public string Text { get; private set; } = "";

		public int SetCount { get; private set; }

		public bool ContainsFileDropList => false;

		public bool ContainsImage => false;

		public bool ContainsText => !string.IsNullOrEmpty(Text);

		public StringCollection GetFileDropList() => new StringCollection();

		public ImageBuffer GetImage() => null;

		public string GetText() => Text;

		public void SetText(string text)
		{
			Text = text ?? "";
			SetCount++;
		}

		public void SetImage(ImageBuffer imageBuffer)
		{
			SetCount++;
		}
	}

	/// <summary>
	/// Test-only file dialogs: a save dialog answers at once with a path in the scratch folder; an open dialog is
	/// cancelled. Installed through AggContext.FileDialogs after start-up, so no system panel is ever shown.
	/// </summary>
	public class ScratchFileDialogs : IFileDialogProvider
	{
		public string SaveFolder { get; set; }

		public List<string> SavedPaths { get; } = new List<string>();

		public string LastDirectoryUsed => SaveFolder;

		public string ResolveFilePath(string path) => path;

		public bool OpenFileDialog(OpenFileDialogParams openParams, Action<OpenFileDialogParams> callback)
		{
			Smoke.Log($"open dialog requested (\"{openParams.Title}\"): cancelled by the test");
			return false;
		}

		public bool SelectFolderDialog(SelectFolderDialogParams folderParams, Action<SelectFolderDialogParams> callback)
		{
			Smoke.Log("folder dialog requested: cancelled by the test");
			return false;
		}

		public bool SaveFileDialog(SaveFileDialogParams saveParams, Action<SaveFileDialogParams> callback)
		{
			Directory.CreateDirectory(SaveFolder);
			string name = string.IsNullOrWhiteSpace(saveParams.FileName) ? "export" : Path.GetFileName(saveParams.FileName);
			string path = Path.Combine(SaveFolder, name);
			saveParams.FileName = path;
			saveParams.FileNames = new[] { path };
			SavedPaths.Add(path);
			Smoke.Log($"save dialog (\"{saveParams.Title}\", filter \"{saveParams.Filter}\") answered with {path}");
			UiThread.RunOnIdle(() => callback?.Invoke(saveParams));
			return true;
		}

		public void ShowFileInFolder(string fileName)
		{
		}
	}

	/// <summary>
	/// Records every HTTP request and host-name lookup the process starts (the runtime's own System.Net.Http and
	/// System.Net.NameResolution event sources). Anything that is not loopback fails the step it happened in. It
	/// observes, it cannot block: the slice flow also points the OctoPrint host at a closed loopback port.
	/// </summary>
	public sealed class NetworkWatch : EventListener
	{
		private static readonly object Locker = new object();
		private static readonly List<(string Description, bool Loopback)> All = new List<(string, bool)>();
		private static NetworkWatch instance;

		public static void Install() => instance = new NetworkWatch();

		/// <summary>
		/// Proves the watch sees traffic before trusting "none seen": an HTTP request to a closed loopback port, a lookup
		/// of "localhost" and a raw TCP connect (no HTTP) to a loopback listener must all be recorded, as loopback. They
		/// are then forgotten.
		/// </summary>
		public static void SelfTest()
		{
			using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) })
			{
				try
				{
					client.GetAsync("http://127.0.0.1:9/ebmc-ui-smoke-self-test").GetAwaiter().GetResult();
				}
				catch (Exception)
				{
					// refused, as intended
				}
			}

			Dns.GetHostAddresses("localhost");

			var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;
			using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp))
			{
				socket.Connect(IPAddress.Loopback, port);
			}

			listener.Stop();

			lock (Locker)
			{
				bool sawRequest = All.Any(a => a.Description.Contains("127.0.0.1:9/ebmc-ui-smoke-self-test") && a.Loopback);
				bool sawLookup = All.Any(a => a.Description == "name lookup localhost" && a.Loopback);
				bool sawConnect = All.Any(a => a.Description == $"socket connect 127.0.0.1:{port}" && a.Loopback);
				if (!sawRequest || !sawLookup || !sawConnect)
				{
					throw new SmokeFailure($"the network watch does not work (HTTP request seen: {sawRequest}, lookup seen: {sawLookup}, "
						+ $"raw connect seen: {sawConnect}): {string.Join("; ", All.Select(a => a.Description))}");
				}

				All.Clear();
			}

			Console.WriteLine("EBMC UI smoke: network watch self-test passed (a loopback HTTP request, a lookup and a raw socket connect were all seen)");
		}

		public static IReadOnlyList<string> AllSeen
		{
			get
			{
				lock (Locker)
				{
					return All.Select(a => a.Description).ToList();
				}
			}
		}

		/// <summary>What was seen that was not loopback.</summary>
		public static IReadOnlyList<string> OutsideSeen
		{
			get
			{
				lock (Locker)
				{
					return All.Where(a => !a.Loopback).Select(a => a.Description).Distinct().ToList();
				}
			}
		}

		public static void ThrowIfAny()
		{
			var outside = OutsideSeen;
			if (outside.Count > 0)
			{
				throw new SmokeFailure("network traffic outside this computer was attempted: " + string.Join("; ", outside));
			}
		}

		protected override void OnEventSourceCreated(EventSource eventSource)
		{
			if (eventSource.Name == "System.Net.Http"
				|| eventSource.Name == "System.Net.NameResolution"
				|| eventSource.Name == "System.Net.Sockets")
			{
				EnableEvents(eventSource, EventLevel.Informational);
			}
		}

		protected override void OnEventWritten(EventWrittenEventArgs e)
		{
			string description = null;
			bool loopback = false;
			if (e.EventSource.Name == "System.Net.Http" && e.EventName == "RequestStart" && e.Payload?.Count >= 3)
			{
				string host = e.Payload[1]?.ToString() ?? "";
				description = $"HTTP {e.Payload[0]}://{host}:{e.Payload[2]}{(e.Payload.Count > 3 ? e.Payload[3] : "")}";
				loopback = IsLoopback(host);
			}
			else if (e.EventSource.Name == "System.Net.NameResolution" && e.EventName == "ResolutionStart" && e.Payload?.Count >= 1)
			{
				string host = e.Payload[0]?.ToString() ?? "";
				description = "name lookup " + host;
				loopback = IsLoopback(host);
			}
			else if (e.EventSource.Name == "System.Net.Sockets" && e.EventName == "ConnectStart" && e.Payload?.Count >= 1)
			{
				string address = e.Payload[0]?.ToString() ?? "";
				var endpoint = ParseSocketAddress(address);
				if (endpoint == null && address.StartsWith("Unix", StringComparison.Ordinal))
				{
					// a Unix domain socket never leaves the computer
					description = "socket connect (Unix domain) " + address;
					loopback = true;
				}
				else
				{
					description = endpoint == null ? "socket connect (unreadable address) " + address : "socket connect " + endpoint;
					loopback = endpoint != null && IPAddress.IsLoopback(endpoint.Address);
				}
			}

			if (description == null)
			{
				return;
			}

			lock (Locker)
			{
				All.Add((description, loopback));
			}
		}

		/// <summary>
		/// The runtime writes the address as SocketAddress.ToString(): "InterNetwork:16:{p,p,a,b,c,d,...}" (port in the
		/// first two bytes, big-endian, then the IPv4 bytes) or "InterNetworkV6:28:{p,p,flow x4,16 address bytes,...}".
		/// </summary>
		public static IPEndPoint ParseSocketAddress(string text)
		{
			int open = text.IndexOf('{');
			int close = text.LastIndexOf('}');
			if (open < 0 || close < open)
			{
				return null;
			}

			var bytes = new List<byte>();
			foreach (string part in text.Substring(open + 1, close - open - 1).Split(','))
			{
				if (!byte.TryParse(part.Trim(), out byte b))
				{
					return null;
				}

				bytes.Add(b);
			}

			if (text.StartsWith("InterNetworkV6", StringComparison.Ordinal) && bytes.Count >= 22)
			{
				return new IPEndPoint(new IPAddress(bytes.Skip(6).Take(16).ToArray()), bytes[0] << 8 | bytes[1]);
			}

			if (text.StartsWith("InterNetwork", StringComparison.Ordinal) && bytes.Count >= 6)
			{
				return new IPEndPoint(new IPAddress(bytes.Skip(2).Take(4).ToArray()), bytes[0] << 8 | bytes[1]);
			}

			return null;
		}

		/// <summary>True for "localhost" and any loopback address (127.0.0.0/8, ::1).</summary>
		public static bool IsLoopback(string host)
		{
			host = (host ?? "").Trim('[', ']');
			return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
				|| (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
		}
	}
}
