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
		private const string MarkerName = DataFolderGuard.MarkerName;

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
			string guardScratch = Argument(args, "--guard-selftest");
			if (guardScratch != null)
			{
				// the folder guard against a fake real folder made in the given scratch folder (never the real one)
				return DataFolderGuard.SelfTest(guardScratch) == 0 ? 0 : 1;
			}

			if (args.Contains("--network-selftest"))
			{
				return NetworkSelfTest();
			}

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

			// the real path of both, links resolved (see DataFolderGuard)
			string refusal = DataFolderGuard.Check(Environment.GetEnvironmentVariable("EBMC_DATA_DIR"), RealDataFolder, flow.ReusesData);
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

			// Ctrl-C: end the way a failure does, so the OctoPrint mock is stopped and the result line is printed
			Console.CancelKeyPress += (s, e) =>
			{
				e.Cancel = true;
				Finish(5, "interrupted (Ctrl-C)");
			};

			try
			{
				RunApplication(flow);
			}
			catch (Exception e)
			{
				// a hard failure after steps that did not fit: report all of them
				string reason = Describe(e);
				var earlier = Smoke.DeferredFailures.Where(d => !reason.Contains(d)).ToList();
				Finish(1, earlier.Count == 0 ? reason : string.Join(" | ", earlier) + " | then: " + reason);
			}

			Finish(0, null);
			return 0;
		}

		/// <summary>
		/// The watch's self-test, then a raw TCP connect to this computer's own non-loopback address (a closed port: the
		/// connection is refused by this computer, nothing leaves it), which the watch must report as outside.
		/// </summary>
		private static int NetworkSelfTest()
		{
			NetworkWatch.Install();
			NetworkWatch.SelfTest();

			var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
				.Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
				.SelectMany(n => n.GetIPProperties().UnicastAddresses)
				.Select(u => u.Address)
				.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
			if (own == null)
			{
				Console.WriteLine("no non-loopback IPv4 address on this computer; the outside check cannot be shown");
				return 1;
			}

			using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp))
			{
				try
				{
					socket.Connect(own, 9);
				}
				catch (System.Net.Sockets.SocketException e)
				{
					Console.WriteLine($"connect to this computer's own address {own}:9: {e.SocketErrorCode} (expected)");
				}
			}

			var outside = NetworkWatch.OutsideSeen;
			Console.WriteLine("reported as outside: " + (outside.Count == 0 ? "nothing" : string.Join("; ", outside)));
			try
			{
				NetworkWatch.ThrowIfAny();
				Console.WriteLine("WRONG: the watch did not fail on a non-loopback connect");
				return 1;
			}
			catch (SmokeFailure e)
			{
				Console.WriteLine("the watch fails the step as it should: " + e.Message);
				return 0;
			}
		}

		private static string Argument(string[] args, string name)
		{
			int index = Array.IndexOf(args, name);
			return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void RunApplication(Flow flow)
		{
			Console.WriteLine($"EBMC UI smoke: flow {flow.Name} ({flow.Summary})");
			Console.WriteLine("EBMC UI smoke: data folder " + Smoke.DataDir);
			Console.WriteLine("EBMC UI smoke: screenshots " + Smoke.OutDir);

			// every exception thrown anywhere, even one the app catches and drops, is written to first-chance.log in the
			// screenshot folder (a step that fails on a swallowed error can then say which one)
			var firstChance = new StreamWriter(Path.Combine(Smoke.OutDir, "first-chance.log")) { AutoFlush = true };
			AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
			{
				try
				{
					lock (firstChance)
					{
						firstChance.WriteLine($"{DateTime.Now:HH:mm:ss.fff} step {Smoke.StepCount}: {e.Exception.GetType().Name}: {e.Exception.Message.Replace('\n', ' ')}");
						firstChance.WriteLine("    " + new StackTrace(1, false).ToString().Replace("\n", "\n    ").Trim());
					}
				}
				catch (Exception)
				{
				}
			};

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

			if (Smoke.DeferredFailures.Count > 0)
			{
				throw new SmokeFailure(string.Join(" | ", Smoke.DeferredFailures));
			}

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

			// things a flow started outside the app (the OctoPrint mock) are stopped whatever the outcome
			foreach (var cleanup in Smoke.OnExit)
			{
				try
				{
					cleanup();
				}
				catch (Exception)
				{
				}
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
