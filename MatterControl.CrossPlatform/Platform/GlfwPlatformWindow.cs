//----------------------------------------------------------------------------
// Anti-Grain Geometry - Version 2.4
// Copyright (C) 2002-2005 Maxim Shemanarev (http://www.antigrain.com)
//
// C# port by: Lars Brubaker
//                  larsbrubaker@gmail.com
// Copyright (C) 2007
//
// Permission to copy, use, modify, sell and distribute this software
// is granted provided this copyright notice appears in all copies.
// This software is provided "as is" without express or implied
// warranty, and with no claim as to its suitability for any purpose.
//
//----------------------------------------------------------------------------
// Contact: mcseem@antigrain.com
//          mcseemagg@yahoo.com
//          http://www.antigrain.com
//----------------------------------------------------------------------------
//
// This file is a modified version of agg-sharp's Glfw/GlfwPlatformWindow.cs.
// Changes for the macOS / Linux host: high-DPI (Retina) framebuffers, a full
// key map, modifier and focus tracking, cached cursors, a non-throwing GLFW
// error handler, a complete close sequence and an event loop that sleeps
// while the window is idle.
//
//----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using GLFW;
using Exception = System.Exception;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.RenderOpenGl;
using MatterHackers.RenderOpenGl.OpenGl;
using MatterHackers.VectorMath;
using AggKeys = MatterHackers.Agg.UI.Keys;
using GlfwKeys = GLFW.Keys;
using KeyEventArgs = MatterHackers.Agg.UI.KeyEventArgs;

namespace MatterHackers.MatterControl.CrossPlatform
{
	public class GlfwPlatformWindow : IPlatformWindow
	{
		// Standard cursor shapes added in GLFW 3.4 that the bundled binding has no names for
		private const int ResizeNwseCursor = 0x00036007;
		private const int ResizeNeswCursor = 0x00036008;
		private const int ResizeAllCursor = 0x00036009;
		private const int NotAllowedCursor = 0x0003600A;

		private const int DoubleClickMs = 500;

		// How long the event loop may sleep when nothing needs drawing. Short enough that
		// timers, animations and work queued from other threads still feel immediate.
		private const double IdleWaitSeconds = 0.005;

		private static readonly bool IsMac = OperatingSystem.IsMacOS();

		private static ErrorCallback errorCallback;

		private readonly Dictionary<MouseButton, int> clickCount = new Dictionary<MouseButton, int>();
		private readonly Dictionary<MouseButton, long> lastMouseDownTime = new Dictionary<MouseButton, long>();
		private readonly Dictionary<int, Cursor> cursors = new Dictionary<int, Cursor>();
		private readonly GlfwWindowProvider windowProvider;

		// GLFW keeps only a native pointer to each callback, so the delegates have to be
		// held here for as long as the window lives or the garbage collector may free them.
		private SizeCallback framebufferSizeCallback;
		private WindowCallback refreshCallback;
		private WindowCallback closeCallback;
		private WindowMaximizedCallback maximizeCallback;
		private IconifyCallback iconifyCallback;
		private FocusCallback focusCallback;
		private WindowContentsScaleCallback contentScaleCallback;
		private KeyCallback keyCallback;
		private CharCallback charCallback;
		private MouseCallback cursorPositionCallback;
		private MouseButtonCallback mouseButtonCallback;
		private MouseCallback scrollCallback;
		private FileDropCallback dropCallback;

		private static Window preCreatedWindow = Window.None;

		private Window glfwWindow = Window.None;
		private SystemWindow rootWindow;
		private string title = "";
		private bool iconified;
		private bool drawing;
		private bool suppressNextChar;
		private bool hasStandardCursors34;
		private int currentCursorShape = -1;
		private MouseButtons buttonsDown = MouseButtons.None;
		private double mouseX;
		private double mouseY;
		private double wheelRemainder;

		// Framebuffer pixels per window coordinate: 2 on a Retina display, 1 elsewhere
		private double pixelRatioX = 1;
		private double pixelRatioY = 1;
		private double lastReportedScale;

		public GlfwPlatformWindow(GlfwWindowProvider windowProvider)
		{
			this.windowProvider = windowProvider;
		}

		/// <summary>
		/// Raised on the UI thread when the window moves to a display with a different
		/// scale factor, for example from a Retina panel to an external monitor.
		/// </summary>
		public static event Action<double> ContentScaleChanged;

		/// <summary>
		/// The most recent GLFW error, kept so a failed start can explain itself.
		/// </summary>
		public static string LastError { get; private set; } = "";

		/// <summary>
		/// Set while a native file dialog is open. The dialog is shown by a helper
		/// process, so the main window keeps running; ignoring input here makes it
		/// behave like the modal dialog it stands in for.
		/// </summary>
		public static bool InputBlocked { get; set; }

		public static GlfwPlatformWindow Current { get; private set; }

		public Window NativeWindow => glfwWindow;

		public string Caption
		{
			get => title;

			set
			{
				title = value ?? "";
				if (glfwWindow != Window.None)
				{
					Glfw.SetWindowTitle(glfwWindow, title);
				}
			}
		}

		public Point2D DesktopPosition
		{
			get
			{
				if (glfwWindow == Window.None)
				{
					return rootWindow?.InitialDesktopPosition ?? new Point2D(-1, -1);
				}

				Glfw.GetWindowPosition(glfwWindow, out int x, out int y);
				return new Point2D(x, y);
			}

			set
			{
				if (glfwWindow != Window.None)
				{
					Glfw.SetWindowPosition(glfwWindow, value.x, value.y);
				}
			}
		}

		public bool Invalidated { get; set; } = true;

		public Vector2 MinimumSize
		{
			get => rootWindow?.MinimumSize ?? Vector2.Zero;

			set => ApplyMinimumSize(value);
		}

		public AggKeys ModifierKeys { get; private set; } = AggKeys.None;

		public int TitleBarHeight => 0;

		private static bool InputEnabled => IPlatformWindow.EnablePlatformWindowInput && !InputBlocked;

		private SystemWindow TopWindow => windowProvider.TopWindow ?? rootWindow;

		/// <summary>
		/// The binding installs an error handler that throws. An exception cannot unwind
		/// through GLFW's native frames, so any GLFW error (even a harmless one such as
		/// "the clipboard holds no text") would end the process. Replace it with one that
		/// records the message instead.
		/// </summary>
		public static void InstallErrorHandler()
		{
			errorCallback = (code, message) =>
			{
				LastError = $"{code}: {Marshal.PtrToStringUTF8(message)}";

				// An empty clipboard is reported as an error; it is not worth a log line
				if (code != GLFW.ErrorCode.FormatUnavailable)
				{
					Console.Error.WriteLine("GLFW " + LastError);
				}
			};

			Glfw.SetErrorCallback(errorCallback);
		}

		public void BringToFront()
		{
			if (glfwWindow != Window.None)
			{
				Glfw.ShowWindow(glfwWindow);
			}
		}

		public void Activate()
		{
			if (glfwWindow != Window.None)
			{
				Glfw.ShowWindow(glfwWindow);
				Glfw.FocusWindow(glfwWindow);
			}
		}

		public void Close()
		{
			if (glfwWindow != Window.None)
			{
				Glfw.SetWindowShouldClose(glfwWindow, true);
			}
		}

		public void CloseSystemWindow(SystemWindow systemWindow)
		{
			if (systemWindow == rootWindow)
			{
				// The application window itself is going away: let the event loop end
				this.Close();
				return;
			}

			// One of the dialogs drawn inside the window closed
			this.Invalidated = true;
		}

		public void Invalidate(RectangleDouble rectToInvalidate)
		{
			this.Invalidated = true;
		}

		public Graphics2D NewGraphics2D()
		{
			var graphics2D = new Graphics2DOpenGL((int)rootWindow.Width, (int)rootWindow.Height, GuiWidget.DeviceScale);
			graphics2D.PushTransform();

			return graphics2D;
		}

		public void SetCursor(Cursors cursorToSet)
		{
			if (glfwWindow == Window.None)
			{
				return;
			}

			int shape = MapCursor(cursorToSet);
			if (shape == currentCursorShape)
			{
				return;
			}

			currentCursorShape = shape;
			Glfw.SetCursor(glfwWindow, GetCursor(shape));
		}

		public void ShowSystemWindow(SystemWindow systemWindow)
		{
			systemWindow.PlatformWindow = this;
			systemWindow.AnchorAll();

			if (rootWindow == null)
			{
				rootWindow = systemWindow;

				// Runs the event loop and only returns when the application window closes
				this.Run();
			}
			else
			{
				// Dialogs are drawn as overlays that fill the application window
				systemWindow.Size = new Vector2(rootWindow.Width, rootWindow.Height);
				this.Invalidated = true;
			}
		}

		private void ApplyMinimumSize(Vector2 minimumSize)
		{
			if (glfwWindow == Window.None)
			{
				return;
			}

			// MinimumSize is in framebuffer pixels, GLFW wants window coordinates
			int minWidth = (int)Math.Ceiling(minimumSize.X / pixelRatioX);
			int minHeight = (int)Math.Ceiling(minimumSize.Y / pixelRatioY);

			Glfw.SetWindowSizeLimits(glfwWindow,
				minWidth > 0 ? minWidth : -1,
				minHeight > 0 ? minHeight : -1,
				-1,
				-1);
		}

		/// <summary>
		/// Creates the native window, still hidden, before any of the interface is built.
		/// The interface sizes itself from the display scale, and the only dependable way
		/// to learn that scale is to measure a real window: how many framebuffer pixels
		/// it got for each unit of its size.
		/// </summary>
		/// <returns>Framebuffer pixels per window unit: 2 on a Retina display, otherwise 1</returns>
		public static double CreateNativeWindow()
		{
			InstallErrorHandler();

			if (preCreatedWindow == Window.None)
			{
				preCreatedWindow = CreateHiddenWindow(800, 600, Program.AppName);
			}

			Glfw.GetWindowSize(preCreatedWindow, out int windowWidth, out _);
			Glfw.GetFramebufferSize(preCreatedWindow, out int framebufferWidth, out _);

			return windowWidth > 0 && framebufferWidth > 0
				? (double)framebufferWidth / windowWidth
				: 1;
		}

		private static Window CreateHiddenWindow(int width, int height, string title)
		{
			var graphicsMode = AggContext.Config.GraphicsMode;
			Glfw.WindowHint(Hint.Samples, graphicsMode.FSAASamples);
			Glfw.WindowHint(Hint.Visible, false);

			// Ask macOS for a full resolution framebuffer on Retina displays. No context
			// version or profile is requested on purpose: that yields the legacy OpenGL 2.1
			// context on macOS, which is the one that still has the fixed-function
			// pipeline MatterControl draws with.
			Glfw.WindowHint(Hint.CocoaRetinaFrameBuffer, true);
			Glfw.WindowHint(Hint.ScaleToMonitor, false);

			var window = Glfw.CreateWindow(width, height, title, GLFW.Monitor.None, Window.None);
			if (window == Window.None && graphicsMode.FSAASamples > 0)
			{
				// Some drivers have no multisampled pixel format at all
				Glfw.WindowHint(Hint.Samples, 0);
				window = Glfw.CreateWindow(width, height, title, GLFW.Monitor.None, Window.None);
			}

			if (window == Window.None)
			{
				throw new InvalidOperationException("Could not create an OpenGL window. " + LastError);
			}

			return window;
		}

		private void Run()
		{
			InstallErrorHandler();

			// Same moment WinForms installs its context: when the application window is created
			UiThreadSynchronizationContext.Install();

			glfwWindow = preCreatedWindow != Window.None
				? preCreatedWindow
				: CreateHiddenWindow(800, 600, Program.AppName);
			preCreatedWindow = Window.None;

			this.UpdatePixelRatio();

			var workArea = Glfw.PrimaryMonitor.WorkArea;

			// rootWindow is sized in framebuffer pixels, the native window in its own units
			int windowWidth = Math.Max(100, (int)Math.Round(rootWindow.Width / pixelRatioX));
			int windowHeight = Math.Max(100, (int)Math.Round(rootWindow.Height / pixelRatioY));
			if (workArea.Width > 0 && workArea.Height > 0)
			{
				windowWidth = Math.Min(windowWidth, workArea.Width);
				windowHeight = Math.Min(windowHeight, workArea.Height);
			}

			title = string.IsNullOrEmpty(rootWindow.Title) ? title : rootWindow.Title;
			Glfw.SetWindowTitle(glfwWindow, title);
			Glfw.SetWindowSize(glfwWindow, windowWidth, windowHeight);

			Current = this;

			Glfw.MakeContextCurrent(glfwWindow);

			// Wait for the display's vertical refresh so drawing never outruns the screen
			Glfw.SwapInterval(1);

			// Entry points can only be looked up once a context is current
			GL.Instance = new NativeGL();

			Glfw.GetVersion(out int major, out int minor, out _);
			hasStandardCursors34 = major > 3 || (major == 3 && minor >= 4);

			this.UpdatePixelRatio();
			this.ReportDisplayScale(Glfw.PrimaryMonitor.ContentScale.X);
			this.SyncSizeFromFramebuffer();
			this.ApplyMinimumSize(rootWindow.MinimumSize);
			this.PlaceWindow(workArea, windowWidth, windowHeight);
			this.HookCallbacks();
			this.SetWindowIcon();

			if (rootWindow.Maximized)
			{
				Glfw.MaximizeWindow(glfwWindow);
			}

			Glfw.ShowWindow(glfwWindow);
			Glfw.FocusWindow(glfwWindow);

			while (!Glfw.WindowShouldClose(glfwWindow))
			{
				if (this.Invalidated)
				{
					Glfw.PollEvents();
				}
				else
				{
					// Sleeps until input arrives or the timeout passes, instead of spinning
					Glfw.WaitEventsTimeout(IdleWaitSeconds);
				}

				// Timers, animations and work marshalled from background threads
				UiThread.InvokePendingActions();

				this.DrawIfNeeded();

				if (!iconified)
				{
					// macOS does not always report zoom changes through the callback
					bool maximized = Glfw.GetWindowAttribute(glfwWindow, WindowAttribute.Maximized);
					if (maximized != rootWindow.Maximized)
					{
						rootWindow.Maximized = maximized;
					}
				}
			}

			// Several shapes can share one fallback cursor, so free each handle once
			foreach (var cursor in cursors.Values.Distinct().ToList())
			{
				if (cursor != Cursor.None)
				{
					Glfw.DestroyCursor(cursor);
				}
			}

			cursors.Clear();

			var closingWindow = glfwWindow;
			glfwWindow = Window.None;
			Current = null;
			Glfw.DestroyWindow(closingWindow);
			Glfw.Terminate();
		}

		private void PlaceWindow(System.Drawing.Rectangle workArea, int windowWidth, int windowHeight)
		{
			var requested = rootWindow.InitialDesktopPosition;
			if (requested == new Point2D(-1, -1) || !IsOnAnyMonitor(requested.x, requested.y, windowWidth))
			{
				if (workArea.Width <= 0 || workArea.Height <= 0)
				{
					return;
				}

				// Center on the primary display
				int x = workArea.X + Math.Max(0, (workArea.Width - windowWidth) / 2);
				int y = workArea.Y + Math.Max(0, (workArea.Height - windowHeight) / 2);
				Glfw.SetWindowPosition(glfwWindow, x, y);
			}
			else
			{
				Glfw.SetWindowPosition(glfwWindow, requested.x, requested.y);
			}
		}

		/// <summary>
		/// A position saved while an external monitor was attached can end up off screen
		/// once it is unplugged. Only restore a position whose title bar is reachable.
		/// </summary>
		private static bool IsOnAnyMonitor(int x, int y, int windowWidth)
		{
			try
			{
				foreach (var monitor in Glfw.Monitors)
				{
					var area = monitor.WorkArea;
					if (area.Width <= 0 || area.Height <= 0)
					{
						continue;
					}

					bool titleBarVisible = y >= area.Y - 10
						&& y < area.Y + area.Height - 40
						&& x + windowWidth > area.X + 80
						&& x < area.X + area.Width - 80;

					if (titleBarVisible)
					{
						return true;
					}
				}
			}
			catch (Exception)
			{
				// If the monitors cannot be listed, trust the saved position
				return true;
			}

			return false;
		}

		private void HookCallbacks()
		{
			framebufferSizeCallback = this.OnFramebufferSize;
			refreshCallback = this.OnRefresh;
			closeCallback = this.OnCloseRequested;
			maximizeCallback = this.OnMaximize;
			iconifyCallback = this.OnIconify;
			focusCallback = this.OnFocus;
			contentScaleCallback = this.OnContentScale;
			keyCallback = this.OnKey;
			charCallback = this.OnChar;
			cursorPositionCallback = this.OnCursorPosition;
			mouseButtonCallback = this.OnMouseButton;
			scrollCallback = this.OnScroll;
			dropCallback = this.OnDrop;

			Glfw.SetFramebufferSizeCallback(glfwWindow, framebufferSizeCallback);
			Glfw.SetWindowRefreshCallback(glfwWindow, refreshCallback);
			Glfw.SetCloseCallback(glfwWindow, closeCallback);
			Glfw.SetWindowMaximizeCallback(glfwWindow, maximizeCallback);
			Glfw.SetWindowIconifyCallback(glfwWindow, iconifyCallback);
			Glfw.SetWindowFocusCallback(glfwWindow, focusCallback);
			Glfw.SetWindowContentScaleCallback(glfwWindow, contentScaleCallback);
			Glfw.SetKeyCallback(glfwWindow, keyCallback);
			Glfw.SetCharCallback(glfwWindow, charCallback);
			Glfw.SetCursorPositionCallback(glfwWindow, cursorPositionCallback);
			Glfw.SetMouseButtonCallback(glfwWindow, mouseButtonCallback);
			Glfw.SetScrollCallback(glfwWindow, scrollCallback);
			Glfw.SetDropCallback(glfwWindow, dropCallback);
		}

		private void SetWindowIcon()
		{
			// On macOS the icon belongs to the application bundle, not to a window, and
			// GLFW reports an error if one is set.
			if (IsMac)
			{
				return;
			}

			try
			{
				var applicationIcon = StaticData.Instance.LoadIcon("application.png");
				if (applicationIcon == null)
				{
					return;
				}

				var large = ToRgbaTopDown(applicationIcon);
				var small = ToRgbaTopDown(applicationIcon.CreateScaledImage(16, 16));
				var largeHandle = GCHandle.Alloc(large.pixels, GCHandleType.Pinned);
				var smallHandle = GCHandle.Alloc(small.pixels, GCHandleType.Pinned);
				try
				{
					// GLFW copies the pixels before returning
					Glfw.SetWindowIcon(glfwWindow,
						2,
						new[]
						{
							new Image(large.width, large.height, largeHandle.AddrOfPinnedObject()),
							new Image(small.width, small.height, smallHandle.AddrOfPinnedObject()),
						});
				}
				finally
				{
					largeHandle.Free();
					smallHandle.Free();
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("Could not set the window icon: " + e.Message);
			}
		}

		private static (int width, int height, byte[] pixels) ToRgbaTopDown(ImageBuffer source)
		{
			var pixels = new byte[source.Width * source.Height * 4];
			int index = 0;
			for (int y = source.Height - 1; y >= 0; y--)
			{
				for (int x = 0; x < source.Width; x++)
				{
					var pixel = source.GetPixel(x, y);
					pixels[index++] = pixel.red;
					pixels[index++] = pixel.green;
					pixels[index++] = pixel.blue;
					pixels[index++] = pixel.alpha;
				}
			}

			return (source.Width, source.Height, pixels);
		}

		private void UpdatePixelRatio()
		{
			Glfw.GetWindowSize(glfwWindow, out int windowWidth, out int windowHeight);
			Glfw.GetFramebufferSize(glfwWindow, out int framebufferWidth, out int framebufferHeight);

			if (windowWidth > 0 && windowHeight > 0 && framebufferWidth > 0 && framebufferHeight > 0)
			{
				pixelRatioX = (double)framebufferWidth / windowWidth;
				pixelRatioY = (double)framebufferHeight / windowHeight;
			}
		}

		private void SyncSizeFromFramebuffer()
		{
			Glfw.GetFramebufferSize(glfwWindow, out int width, out int height);
			this.ResizeTo(width, height);
		}

		private void ResizeTo(int width, int height)
		{
			if (width <= 0 || height <= 0 || rootWindow == null)
			{
				// Minimized
				return;
			}

			var size = new Vector2(width, height);
			if (rootWindow.Size != size)
			{
				rootWindow.Size = size;
			}

			foreach (var window in windowProvider.OpenWindows)
			{
				if (window != rootWindow && window.Size != size)
				{
					window.Size = size;
				}
			}

			this.Invalidated = true;
		}

		private void ResetViewport()
		{
			Glfw.MakeContextCurrent(glfwWindow);

			int width = (int)rootWindow.Width;
			int height = (int)rootWindow.Height;

			GL.MatrixMode(MatrixMode.Modelview);
			GL.LoadIdentity();
			GL.Scissor(0, 0, width, height);

			GL.MatrixMode(MatrixMode.Projection);
			GL.LoadIdentity();

			// Bottom-left corner pixel has coordinate (0, 0)
			GL.Ortho(0, width, 0, height, -1, 1);
			GL.Viewport(0, 0, width, height);
		}

		private void DrawIfNeeded()
		{
			if (!this.Invalidated
				|| iconified
				|| drawing
				|| glfwWindow == Window.None
				|| rootWindow == null
				|| rootWindow.Width < 1
				|| rootWindow.Height < 1)
			{
				return;
			}

			drawing = true;
			try
			{
				this.Invalidated = false;
				this.ResetViewport();

				Graphics2D graphics2D = this.NewGraphics2D();
				var openWindows = windowProvider.OpenWindows;
				for (int i = 0; i < openWindows.Count; i++)
				{
					var window = openWindows[i];
					if (window.PlatformWindow == null)
					{
						// Closed while this frame was being prepared
						continue;
					}

					if (i > 0)
					{
						if (window.Size != rootWindow.Size)
						{
							window.Size = rootWindow.Size;
						}

						// Dim what is underneath a dialog
						this.ResetViewport();
						graphics2D.FillRectangle(rootWindow.LocalBounds, new Color(Color.Black, 160));
					}

					window.OnDraw(graphics2D);
				}

				Glfw.SwapBuffers(glfwWindow);
			}
			finally
			{
				drawing = false;
			}
		}

		private void OnFramebufferSize(Window window, int width, int height)
		{
			this.UpdatePixelRatio();
			if (IsMac)
			{
				// Moving between a Retina and a standard display changes the framebuffer
				this.ReportDisplayScale(pixelRatioX);
			}

			this.ResizeTo(width, height);

			// The event loop is blocked while the user drags a window edge, so draw from
			// here to keep the content following the resize.
			this.DrawIfNeeded();
		}

		private void OnRefresh(Window window)
		{
			this.Invalidated = true;
			this.DrawIfNeeded();
		}

		private void OnMaximize(Window window, bool maximized)
		{
			if (rootWindow != null && rootWindow.Maximized != maximized)
			{
				rootWindow.Maximized = maximized;
			}
		}

		private void OnIconify(IntPtr window, bool isIconified)
		{
			iconified = isIconified;
			if (!isIconified)
			{
				this.Invalidated = true;
			}
		}

		private void OnFocus(Window window, bool focused)
		{
			// Key releases that happen while another application has focus never arrive
			// (Command+Tab is the usual case), so forget every held key on both edges.
			Keyboard.Clear();
			this.ModifierKeys = AggKeys.None;
			suppressNextChar = false;

			if (!focused && buttonsDown != MouseButtons.None)
			{
				buttonsDown = MouseButtons.None;
			}

			this.Invalidated = true;
		}

		private void OnContentScale(Window window, float xScale, float yScale)
		{
			this.UpdatePixelRatio();
			this.ApplyMinimumSize(rootWindow.MinimumSize);
			this.ReportDisplayScale(xScale);
		}

		private void ReportDisplayScale(double contentScale)
		{
			// On macOS the measured framebuffer ratio is the scale (see CreateNativeWindow)
			double scale = IsMac ? pixelRatioX : contentScale;
			if (scale > 0 && scale != lastReportedScale)
			{
				bool first = lastReportedScale == 0;
				lastReportedScale = scale;
				if (!first)
				{
					ContentScaleChanged?.Invoke(scale);
				}
			}
		}

		private void OnCloseRequested(Window window)
		{
			if (rootWindow == null || rootWindow.HasBeenClosed)
			{
				return;
			}

			if (InputBlocked)
			{
				// A file dialog is waiting for an answer
				Glfw.SetWindowShouldClose(glfwWindow, false);
				return;
			}

			// Give the application the chance to ask about unsaved work or a running print
			var shouldClose = new ShouldCloseEventArgs();
			rootWindow.OnShouldClose(shouldClose);

			if (shouldClose.Cancel)
			{
				Glfw.SetWindowShouldClose(glfwWindow, false);
			}
			else if (!rootWindow.HasBeenClosed)
			{
				// Runs the application's shutdown code, which ends in CloseSystemWindow
				rootWindow.Close();
			}
		}

		private void OnCursorPosition(Window window, double x, double y)
		{
			mouseX = x * pixelRatioX;
			mouseY = rootWindow.Height - (y * pixelRatioY);

			if (!InputEnabled)
			{
				return;
			}

			TopWindow.OnMouseMove(new MouseEventArgs(buttonsDown, 0, mouseX, mouseY, 0));
		}

		private void OnMouseButton(Window window, MouseButton button, InputState state, ModifierKeys modifiers)
		{
			MouseButtons aggButton;
			switch (button)
			{
				case MouseButton.Left:
					aggButton = MouseButtons.Left;
					break;

				case MouseButton.Right:
					aggButton = MouseButtons.Right;
					break;

				case MouseButton.Middle:
					aggButton = MouseButtons.Middle;
					break;

				default:
					return;
			}

			if (state == InputState.Press)
			{
				buttonsDown |= aggButton;
			}
			else if (state == InputState.Release)
			{
				buttonsDown &= ~aggButton;
			}

			if (!InputEnabled)
			{
				return;
			}

			this.SyncModifiers(modifiers);

			if (state == InputState.Press)
			{
				long now = UiThread.CurrentTimerMs;
				int clicks = 1;
				if (lastMouseDownTime.TryGetValue(button, out long lastDown)
					&& now - lastDown < DoubleClickMs
					&& clickCount.TryGetValue(button, out int previousClicks)
					&& previousClicks == 1)
				{
					clicks = 2;
				}

				clickCount[button] = clicks;
				lastMouseDownTime[button] = now;

				TopWindow.OnMouseDown(new MouseEventArgs(aggButton, clicks, mouseX, mouseY, 0));
			}
			else if (state == InputState.Release)
			{
				clickCount.TryGetValue(button, out int clicks);
				TopWindow.OnMouseUp(new MouseEventArgs(aggButton, Math.Max(1, clicks), mouseX, mouseY, 0));
			}
		}

		private void OnScroll(Window window, double x, double y)
		{
			if (!InputEnabled)
			{
				return;
			}

			// One wheel notch is 120 units. Trackpads send many small fractional steps,
			// so carry the remainder forward rather than rounding each one away.
			double delta = (y * 120) + wheelRemainder;
			int wholeDelta = (int)delta;
			wheelRemainder = delta - wholeDelta;

			if (wholeDelta != 0)
			{
				TopWindow.OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, mouseX, mouseY, wholeDelta));
			}
		}

		private void OnDrop(Window window, int count, IntPtr paths)
		{
			if (!InputEnabled || count <= 0)
			{
				return;
			}

			var files = new List<string>(count);
			for (int i = 0; i < count; i++)
			{
				string path = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(paths, i * IntPtr.Size));
				if (!string.IsNullOrEmpty(path))
				{
					files.Add(path);
				}
			}

			// No cursor movement is reported while something is dragged in from another
			// application, so ask where the pointer is now.
			Glfw.GetCursorPosition(glfwWindow, out double x, out double y);
			mouseX = x * pixelRatioX;
			mouseY = rootWindow.Height - (y * pixelRatioY);
			double dropX = mouseX;
			double dropY = mouseY;

			UiThread.RunOnIdle(() =>
			{
				var target = TopWindow;
				if (target == null || files.Count == 0)
				{
					return;
				}

				target.OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, dropX, dropY, 0, files));
				target.OnMouseUp(new MouseEventArgs(MouseButtons.None, 0, dropX, dropY, 0, files));
			});
		}

		private void OnChar(Window window, uint codePoint)
		{
			if (!InputEnabled || suppressNextChar)
			{
				return;
			}

			// Shortcuts are handled as key presses, never as text
			bool control = (this.ModifierKeys & AggKeys.Control) == AggKeys.Control;
			bool alt = (this.ModifierKeys & AggKeys.Alt) == AggKeys.Alt;
			if (control && !alt)
			{
				return;
			}

			if (codePoint < 32 || codePoint == 127 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
			{
				return;
			}

			foreach (char character in char.ConvertFromUtf32((int)codePoint))
			{
				TopWindow.OnKeyPress(new KeyPressEventArgs(character));
			}
		}

		private void OnKey(Window window, GlfwKeys key, int scanCode, InputState state, ModifierKeys mods)
		{
			if (!InputEnabled)
			{
				return;
			}

			AggKeys keyCode = MapKey(key, scanCode);

			// X11 reports the modifier state from before the event, so a modifier key's own
			// press does not include itself and its release still does. Make it consistent.
			ModifierKeys ownModifier = ModifierOf(key);
			if (state == InputState.Release)
			{
				mods &= ~ownModifier;
			}
			else
			{
				mods |= ownModifier;
			}

			this.SyncModifiers(mods);

			if (keyCode == AggKeys.None)
			{
				return;
			}

			if (state == InputState.Press || state == InputState.Repeat)
			{
				var keyEvent = new KeyEventArgs(keyCode | this.ModifierKeys);
				TopWindow.OnKeyDown(keyEvent);

				SetKeyDown(keyCode, true);

				suppressNextChar = keyEvent.SuppressKeyPress;

				if (!keyEvent.SuppressKeyPress
					&& (key == GlfwKeys.Enter || key == GlfwKeys.NumpadEnter))
				{
					// GLFW reports Enter as a key only; text boxes expect the character too
					TopWindow.OnKeyPress(new KeyPressEventArgs('\r'));
				}
			}
			else if (state == InputState.Release)
			{
				suppressNextChar = false;

				// Only report a release for a press this window saw
				if (Keyboard.IsKeyDown(keyCode))
				{
					TopWindow.OnKeyUp(new KeyEventArgs(keyCode | this.ModifierKeys));
					SetKeyDown(keyCode, false);
				}
			}
		}

		private void SyncModifiers(ModifierKeys mods)
		{
			bool shift = mods.HasFlag(GLFW.ModifierKeys.Shift);
			bool alt = mods.HasFlag(GLFW.ModifierKeys.Alt);

			// Command is the shortcut key on a Mac, so Command+C / V / Z / S drive the
			// shortcuts written for Ctrl. Elsewhere Super is the system's own key.
			bool control = mods.HasFlag(GLFW.ModifierKeys.Control)
				|| (IsMac && mods.HasFlag(GLFW.ModifierKeys.Super));

			SetKeyDown(AggKeys.ShiftKey, shift);
			SetKeyDown(AggKeys.ControlKey, control);
			SetKeyDown(AggKeys.Menu, alt);

			var modifiers = AggKeys.None;
			if (shift)
			{
				modifiers |= AggKeys.Shift;
			}

			if (control)
			{
				modifiers |= AggKeys.Control;
			}

			if (alt)
			{
				modifiers |= AggKeys.Alt;
			}

			this.ModifierKeys = modifiers;
		}

		private static void SetKeyDown(AggKeys key, bool down)
		{
			// Keyboard raises StateChanged on every call, so only report real changes
			if (Keyboard.IsKeyDown(key) != down)
			{
				Keyboard.SetKeyDownState(key, down);
			}
		}

		private static ModifierKeys ModifierOf(GlfwKeys key)
		{
			switch (key)
			{
				case GlfwKeys.LeftShift:
				case GlfwKeys.RightShift:
					return GLFW.ModifierKeys.Shift;

				case GlfwKeys.LeftControl:
				case GlfwKeys.RightControl:
					return GLFW.ModifierKeys.Control;

				case GlfwKeys.LeftAlt:
				case GlfwKeys.RightAlt:
					return GLFW.ModifierKeys.Alt;

				case GlfwKeys.LeftSuper:
				case GlfwKeys.RightSuper:
					return GLFW.ModifierKeys.Super;
			}

			return 0;
		}

		private static AggKeys MapKey(GlfwKeys key, int scanCode)
		{
			if (key >= GlfwKeys.Space && key <= GlfwKeys.GraveAccent)
			{
				// GLFW key codes name physical positions on a US keyboard. Shortcuts should
				// follow what is printed on the key, so ask which character this key
				// produces in the active layout (Z and Y swap on a German keyboard).
				string name = Glfw.GetKeyName(key, scanCode);
				if (!string.IsNullOrEmpty(name) && name.Length == 1)
				{
					char character = char.ToUpperInvariant(name[0]);
					if (character >= 'A' && character <= 'Z')
					{
						return AggKeys.A + (character - 'A');
					}
				}
			}

			if (key >= GlfwKeys.A && key <= GlfwKeys.Z)
			{
				return AggKeys.A + (key - GlfwKeys.A);
			}

			if (key >= GlfwKeys.Alpha0 && key <= GlfwKeys.Alpha9)
			{
				return AggKeys.D0 + (key - GlfwKeys.Alpha0);
			}

			if (key >= GlfwKeys.Numpad0 && key <= GlfwKeys.Numpad9)
			{
				return AggKeys.NumPad0 + (key - GlfwKeys.Numpad0);
			}

			if (key >= GlfwKeys.F1 && key <= GlfwKeys.F12)
			{
				return AggKeys.F1 + (key - GlfwKeys.F1);
			}

			switch (key)
			{
				case GlfwKeys.Space: return AggKeys.Space;
				case GlfwKeys.Apostrophe: return AggKeys.OemQuotes;
				case GlfwKeys.Comma: return AggKeys.Oemcomma;
				case GlfwKeys.Minus: return AggKeys.OemMinus;
				case GlfwKeys.Period: return AggKeys.OemPeriod;
				case GlfwKeys.Slash: return AggKeys.OemQuestion;
				case GlfwKeys.SemiColon: return AggKeys.OemSemicolon;
				case GlfwKeys.Equal: return AggKeys.Oemplus;
				case GlfwKeys.LeftBracket: return AggKeys.OemOpenBrackets;
				case GlfwKeys.Backslash: return AggKeys.OemPipe;
				case GlfwKeys.RightBracket: return AggKeys.OemCloseBrackets;
				case GlfwKeys.GraveAccent: return AggKeys.Oemtilde;

				case GlfwKeys.Escape: return AggKeys.Escape;
				case GlfwKeys.Enter: return AggKeys.Return;
				case GlfwKeys.Tab: return AggKeys.Tab;
				case GlfwKeys.Backspace: return AggKeys.Back;
				case GlfwKeys.Insert: return AggKeys.Insert;
				case GlfwKeys.Delete: return AggKeys.Delete;
				case GlfwKeys.Right: return AggKeys.Right;
				case GlfwKeys.Left: return AggKeys.Left;
				case GlfwKeys.Down: return AggKeys.Down;
				case GlfwKeys.Up: return AggKeys.Up;
				case GlfwKeys.PageUp: return AggKeys.PageUp;
				case GlfwKeys.PageDown: return AggKeys.PageDown;
				case GlfwKeys.Home: return AggKeys.Home;
				case GlfwKeys.End: return AggKeys.End;
				case GlfwKeys.CapsLock: return AggKeys.CapsLock;
				case GlfwKeys.ScrollLock: return AggKeys.Scroll;
				case GlfwKeys.NumLock: return AggKeys.NumLock;
				case GlfwKeys.PrintScreen: return AggKeys.PrintScreen;
				case GlfwKeys.Pause: return AggKeys.Pause;

				case GlfwKeys.NumpadDecimal: return AggKeys.Decimal;
				case GlfwKeys.NumpadDivide: return AggKeys.Divide;
				case GlfwKeys.NumpadMultiply: return AggKeys.Multiply;
				case GlfwKeys.NumpadSubtract: return AggKeys.Subtract;
				case GlfwKeys.NumpadAdd: return AggKeys.Add;
				case GlfwKeys.NumpadEnter: return AggKeys.Enter;
				case GlfwKeys.NumpadEqual: return AggKeys.Oemplus;

				case GlfwKeys.LeftShift:
				case GlfwKeys.RightShift:
					return AggKeys.ShiftKey;

				case GlfwKeys.LeftControl:
				case GlfwKeys.RightControl:
					return AggKeys.ControlKey;

				case GlfwKeys.LeftAlt:
				case GlfwKeys.RightAlt:
					return AggKeys.Menu;

				case GlfwKeys.LeftSuper:
				case GlfwKeys.RightSuper:
					return IsMac ? AggKeys.ControlKey : AggKeys.LWin;

				case GlfwKeys.Menu: return AggKeys.Apps;
			}

			return AggKeys.None;
		}

		private int MapCursor(Cursors cursor)
		{
			switch (cursor)
			{
				case Cursors.Cross:
					return (int)CursorType.Crosshair;

				case Cursors.Hand:
					return (int)CursorType.Hand;

				case Cursors.IBeam:
					return (int)CursorType.Beam;

				case Cursors.HSplit:
				case Cursors.SizeNS:
				case Cursors.NoMoveVert:
				case Cursors.PanNorth:
				case Cursors.PanSouth:
					return (int)CursorType.ResizeVertical;

				case Cursors.VSplit:
				case Cursors.SizeWE:
				case Cursors.NoMoveHoriz:
				case Cursors.PanEast:
				case Cursors.PanWest:
					return (int)CursorType.ResizeHorizontal;

				case Cursors.SizeNWSE:
				case Cursors.PanNW:
				case Cursors.PanSE:
					return hasStandardCursors34 ? ResizeNwseCursor : (int)CursorType.Crosshair;

				case Cursors.SizeNESW:
				case Cursors.PanNE:
				case Cursors.PanSW:
					return hasStandardCursors34 ? ResizeNeswCursor : (int)CursorType.Crosshair;

				case Cursors.SizeAll:
				case Cursors.NoMove2D:
					return hasStandardCursors34 ? ResizeAllCursor : (int)CursorType.Crosshair;

				case Cursors.No:
					return hasStandardCursors34 ? NotAllowedCursor : (int)CursorType.Arrow;
			}

			return (int)CursorType.Arrow;
		}

		private Cursor GetCursor(int shape)
		{
			if (cursors.TryGetValue(shape, out Cursor cursor))
			{
				return cursor;
			}

			cursor = Glfw.CreateStandardCursor((CursorType)shape);
			if (cursor == Cursor.None && shape != (int)CursorType.Arrow)
			{
				// Not every platform has every shape; the arrow always exists
				cursor = GetCursor((int)CursorType.Arrow);
			}

			cursors[shape] = cursor;
			return cursor;
		}
	}
}
