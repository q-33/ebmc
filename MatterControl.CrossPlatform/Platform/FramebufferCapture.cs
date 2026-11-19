using System;
using GLFW;
using MatterHackers.Agg.Image;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// Reads the window's back buffer into an image, for the on-screen smoke test (Tests/EBMC.UiSmoke).
	/// The application itself never calls this.
	///
	/// Call it on the thread that owns the GL context (the UI thread), after a frame has been drawn and before it
	/// is swapped: for example from the AfterDraw event of the top-most SystemWindow, which is the last thing drawn
	/// in a frame. The image is at framebuffer resolution (twice the window size on a Retina display).
	/// </summary>
	public static unsafe class FramebufferCapture
	{
		private const uint GL_BACK = 0x0405;
		private const uint GL_BGRA = 0x80E1;
		private const uint GL_UNSIGNED_BYTE = 0x1401;
		private const uint GL_PACK_ALIGNMENT = 0x0D05;

		private static delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void> glReadPixels;
		private static delegate* unmanaged[Cdecl]<uint, void> glReadBuffer;
		private static delegate* unmanaged[Cdecl]<uint, int, void> glPixelStorei;

		/// <summary>Returns the back buffer of the application window, or null when there is no window.</summary>
		public static ImageBuffer ReadBackBuffer()
		{
			var platformWindow = GlfwPlatformWindow.Current;
			if (platformWindow == null || platformWindow.NativeWindow == Window.None)
			{
				return null;
			}

			if (Glfw.CurrentContext != platformWindow.NativeWindow)
			{
				throw new InvalidOperationException("FramebufferCapture must run on the thread that owns the window's OpenGL context.");
			}

			if (glReadPixels == null)
			{
				glReadPixels = (delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void>)Lookup("glReadPixels");
				glReadBuffer = (delegate* unmanaged[Cdecl]<uint, void>)Lookup("glReadBuffer");
				glPixelStorei = (delegate* unmanaged[Cdecl]<uint, int, void>)Lookup("glPixelStorei");
			}

			Glfw.GetFramebufferSize(platformWindow.NativeWindow, out int width, out int height);
			if (width < 1 || height < 1)
			{
				return null;
			}

			// glReadPixels returns BGRA rows bottom row first. ImageBuffer is 32-bit BGRA and also counts y from the
			// bottom, so row y goes to row y; ImageIO.SaveImageData turns the image top-down when it writes the file.
			int rowBytes = width * 4;
			var pixels = new byte[rowBytes * height];

			glReadBuffer(GL_BACK);
			glPixelStorei(GL_PACK_ALIGNMENT, 4);
			fixed (byte* destination = pixels)
			{
				glReadPixels(0, 0, width, height, GL_BGRA, GL_UNSIGNED_BYTE, destination);
			}

			var image = new ImageBuffer(width, height);
			byte[] buffer = image.GetBuffer();
			for (int y = 0; y < height; y++)
			{
				Buffer.BlockCopy(pixels, y * rowBytes, buffer, image.GetBufferOffsetY(y), rowBytes);
			}

			// The window is opaque; the framebuffer's alpha channel is not meaningful
			for (int y = 0; y < height; y++)
			{
				int offset = image.GetBufferOffsetY(y);
				for (int x = 0; x < width; x++)
				{
					buffer[offset + x * 4 + 3] = 255;
				}
			}

			return image;
		}

		private static void* Lookup(string name)
		{
			var address = Glfw.GetProcAddress(name);
			if (address == IntPtr.Zero)
			{
				throw new NotSupportedException("The OpenGL driver does not provide " + name + ".");
			}

			return (void*)address;
		}
	}
}
