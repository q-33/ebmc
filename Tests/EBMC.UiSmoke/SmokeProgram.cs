using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.GuiAutomation;
using MatterHackers.MatterControl.DataStorage;
using Host = MatterHackers.MatterControl.CrossPlatform.Program;

namespace EBMC.UiSmoke
{
	/// <summary>
	/// On-screen smoke test (Plan C). Usage: EBMC.UiSmoke --flow &lt;name&gt; --out &lt;screenshot folder&gt;, with
	/// EBMC_DATA_DIR set to a fresh, empty folder outside the real data folder (members-restore: the folder the
	/// members flow left). One flow per process: the app cannot be started twice in one process.
	/// Exit codes: 0 every step passed, 1 a step failed, 2 refused to start, 3 unhandled or unobserved exception,
	/// 4 overall timeout. The last line printed on failure is "UI SMOKE FAILED: reason".
	/// </summary>
	public static class SmokeProgram
	{
		private const double OverallTimeoutSeconds = 420;

		/// <summary>Written into the scratch folder at the start of a run; a reusing flow requires it.</summary>
		private const string MarkerName = "ebmc-ui-smoke.marker";

		private static int finished;
		private static string closeProblem;

		/// <summary>The folder the app uses when EBMC_DATA_DIR is not set (ApplicationDataStorage: LocalApplicationData/MatterControl).</summary>
		public static string RealDataFolder => Path.GetFullPath(Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MatterControl"));

		public static int Main(string[] args)
		{
			// Nothing in here touches an application type: ApplicationDataStorage reads EBMC_DATA_DIR once, when the
			// type is first used, so the checks come first.
			string flowName = Argument(args, "--flow");
			string outDir = Argument(args, "--out");
			if (args.Contains("--list"))
			{
				foreach (var f in Flows.All)
				{
					Console.WriteLine($"{f.Name}\t{f.Summary}");
				}

				return 0;
			}

			var flow = Flows.All.FirstOrDefault(f => f.Name == flowName);
			if (flow == null)
			{
				return Refuse("give a flow as --flow <name>: " + string.Join(", ", Flows.All.Select(f => f.Name)));
			}

			if (outDir == null || !Path.IsPathRooted(outDir))
			{
				return Refuse("give the screenshot folder as --out <absolute path>");
			}

			string refusal = CheckDataFolder(Environment.GetEnvironmentVariable("EBMC_DATA_DIR"), flow.ReusesData);
			if (refusal != null)
			{
				return Refuse(refusal);
			}

			Smoke.DataDir = Path.GetFullPath(Environment.GetEnvironmentVariable("EBMC_DATA_DIR")).Replace("//", "/").TrimEnd('/');
			Smoke.OutDir = outDir;
			Directory.CreateDirectory(outDir);
			Directory.CreateDirectory(Smoke.DataDir);
			File.WriteAllText(Path.Combine(Smoke.DataDir, MarkerName), "scratch data folder of the EBMC UI smoke test\n");

			AppDomain.CurrentDomain.UnhandledException += (s, e) =>
				Finish(3, "unhandled exception" + (e.ExceptionObject is Exception ex ? ": " + Describe(ex) : ""));
			TaskScheduler.UnobservedTaskException += (s, e) =>
				Finish(3, "unobserved task exception: " + Describe(e.Exception));

			var watchdog = new Thread(() =>
			{
				Thread.Sleep(TimeSpan.FromSeconds(OverallTimeoutSeconds));
				Finish(4, $"the run did not finish within {OverallTimeoutSeconds} s");
			})
			{ IsBackground = true };
			watchdog.Start();

			try
			{
				RunApplication(flow);
			}
			catch (Exception e)
			{
				Finish(1, Describe(e));
			}

			Finish(0, null);
			return 0;
		}

		private static string Argument(string[] args, string name)
		{
			int index = Array.IndexOf(args, name);
			return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
		}

		private static string CheckDataFolder(string value, bool reuse)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "EBMC_DATA_DIR is not set; this test only runs on a throwaway data folder";
			}

			if (!Path.IsPathRooted(value))
			{
				return "EBMC_DATA_DIR must be an absolute path";
			}

			string real = RealDataFolder;
			string requested = Path.GetFullPath(value);

			bool Inside(string path, string folder) =>
				path.TrimEnd('/').Equals(folder.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
				|| path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

			if (Inside(requested, real))
			{
				return "EBMC_DATA_DIR points inside the real data folder " + real;
			}

			if (File.Exists(requested))
			{
				return "EBMC_DATA_DIR is a file: " + requested;
			}

			bool hasEntries = Directory.Exists(requested) && Directory.EnumerateFileSystemEntries(requested).Any();
			if (reuse)
			{
				// only a folder this test made itself (a symbolic link to the real folder has no marker)
				if (!File.Exists(Path.Combine(requested, MarkerName)) || new FileInfo(requested).LinkTarget != null)
				{
					return "this flow reuses a scratch folder a previous smoke run made (" + MarkerName + " missing): " + requested;
				}

				return null;
			}

			// Fresh means empty: this also refuses a symbolic link to the real (non-empty) folder
			if (hasEntries)
			{
				return "EBMC_DATA_DIR is not empty; give a fresh folder: " + requested;
			}

			return null;
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void RunApplication(Flow flow)
		{
			Console.WriteLine($"EBMC UI smoke: flow {flow.Name} ({flow.Summary})");
			Console.WriteLine("EBMC UI smoke: data folder " + Smoke.DataDir);
			Console.WriteLine("EBMC UI smoke: screenshots " + Smoke.OutDir);

			// observers first: the app must never see the real clipboard, and every request is recorded
			NetworkWatch.Install();
			NetworkWatch.SelfTest();
			Flows.Clipboard = new FakeClipboard();
			Clipboard.SetSystemClipboard(Flows.Clipboard);

			flow.BeforeStart?.Invoke();

			// The real start-up, shared with the app (MatterControl.CrossPlatform/Program.cs)
			SystemWindow rootWindow = Host.Start(Array.Empty<string>());

			// no system file panel is ever shown: saves go to the scratch folder
			Flows.Dialogs = new ScratchFileDialogs { SaveFolder = Path.Combine(Smoke.DataDir, "exports") };
			AggContext.FileDialogs = Flows.Dialogs;

			Console.WriteLine("EBMC UI smoke: app reports data folder " + ApplicationDataStorage.ApplicationUserDataPath);

			AutomationRunner.TimeToMoveMouse = 0.15;
			Smoke.StartTimer();

			// Blocks on this (the main) thread running the event loop; the flow runs on a worker thread. A UI thread
			// exception (Debug build) leaves the event loop and comes out of here.
			Task run = AutomationRunner.ShowWindowAndExecuteTests(
				rootWindow,
				runner =>
				{
					Smoke.Runner = runner;
					flow.Run();
					return Task.CompletedTask;
				},
				OverallTimeoutSeconds - 30,
				closeWindow: runner => CloseApp(rootWindow));
			run.GetAwaiter().GetResult();

			if (closeProblem != null)
			{
				throw new SmokeFailure(closeProblem);
			}

			if (!rootWindow.HasBeenClosed)
			{
				throw new SmokeFailure("the application window did not close");
			}

			NetworkWatch.ThrowIfAny();

			// unobserved task exceptions are only raised when the task is collected
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();

			Smoke.Log($"closed cleanly; {Smoke.StepCount} steps in {Smoke.ElapsedSeconds:0.0} s");
		}

		/// <summary>
		/// Closes the app and answers the one prompt a flow can expect: "Save Changes?" for a design tab, answered
		/// with Discard (the No button). Any other prompt is a failure.
		/// </summary>
		private static void CloseApp(SystemWindow rootWindow)
		{
			rootWindow.CloseOnIdle();
			var timer = Stopwatch.StartNew();
			while (!rootWindow.HasBeenClosed && timer.Elapsed.TotalSeconds < 30)
			{
				Thread.Sleep(250);
				if (rootWindow.HasBeenClosed)
				{
					break;
				}

				try
				{
					if (Smoke.IsShown("No Button"))
					{
						bool saveChanges = Smoke.Ui(() => SystemWindow.AllOpenSystemWindows.Last().Descendants<GuiWidget>().Any(w => w.Text == "Save Changes?"));
						if (saveChanges)
						{
							Smoke.Log("close: \"Save Changes?\" asked, answering Discard Changes");
							Smoke.Runner.ClickWidget(Smoke.Visible("No Button").First());
							continue;
						}
					}

					if (Smoke.IsShown("Yes Button") || Smoke.IsShown("No Button") || Smoke.IsShown("Ok Button"))
					{
						string text = Smoke.Ui(() => string.Join(" / ", SystemWindow.AllOpenSystemWindows.Last().Descendants<TextWidget>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Take(6)));
						closeProblem = "an unexpected prompt appeared on close: " + text;
						Smoke.TryCaptureNamed("close-prompt-FAILED");
						Finish(1, closeProblem);
					}
				}
				catch (Exception)
				{
					// the window may close while it is being read
				}
			}

			if (!rootWindow.HasBeenClosed)
			{
				closeProblem = "the application did not close within 30 s";
				Smoke.TryCaptureNamed("close-FAILED");
				Finish(1, closeProblem);
			}
		}

		/// <summary>One line for the result; an unexpected exception is printed in full first.</summary>
		public static string Describe(Exception e)
		{
			while ((e is AggregateException || e is System.Reflection.TargetInvocationException) && e.InnerException != null)
			{
				e = e.InnerException;
			}

			if (e is SmokeFailure)
			{
				return e.Message;
			}

			Console.Error.WriteLine(e);
			return (e.GetType().Name + ": " + e.Message).Replace('\n', ' ');
		}

		private static int Refuse(string reason)
		{
			Console.Error.WriteLine("UI SMOKE REFUSED: " + reason);
			return 2;
		}

		/// <summary>Prints the result once and ends the process (the event loop or the flow may still be running).</summary>
		private static void Finish(int code, string reason)
		{
			if (Interlocked.Exchange(ref finished, 1) != 0)
			{
				return;
			}

			Console.Out.Flush();
			Console.WriteLine(code == 0
				? $"UI SMOKE PASSED: {Smoke.StepCount} steps in {Smoke.ElapsedSeconds:0.0} s, screenshots in {Smoke.OutDir}"
				: "UI SMOKE FAILED: " + reason);
			Console.Out.Flush();
			Environment.Exit(code);
		}
	}
}
