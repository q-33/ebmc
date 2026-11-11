using System;
using System.Collections.Specialized;
using GLFW;
using Exception = System.Exception;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.UI;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// System clipboard access through GLFW, which shares text with other
	/// applications. GLFW has no image or file-list formats, so an image that is
	/// copied stays available for pasting inside MatterControl only.
	/// </summary>
	public class GlfwClipboard : ISystemClipboard
	{
		private ImageBuffer copiedImage;

		// What the system clipboard held right after the image was copied. If it
		// changes, something else was copied since and the image is stale.
		private string textWhenImageCopied;

		public bool ContainsFileDropList => false;

		public bool ContainsImage => copiedImage != null && ReadText() == textWhenImageCopied;

		public bool ContainsText => !string.IsNullOrEmpty(ReadText());

		public StringCollection GetFileDropList() => new StringCollection();

		public ImageBuffer GetImage() => this.ContainsImage ? copiedImage : null;

		public string GetText() => ReadText();

		public void SetText(string text)
		{
			copiedImage = null;

			var window = GlfwPlatformWindow.Current?.NativeWindow ?? Window.None;
			if (window == Window.None)
			{
				return;
			}

			try
			{
				Glfw.SetClipboardString(window, text ?? "");
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("Could not write to the clipboard: " + e.Message);
			}
		}

		public void SetImage(ImageBuffer imageBuffer)
		{
			copiedImage = imageBuffer;
			textWhenImageCopied = ReadText();
		}

		private static string ReadText()
		{
			var window = GlfwPlatformWindow.Current?.NativeWindow ?? Window.None;
			if (window == Window.None)
			{
				return "";
			}

			try
			{
				return Glfw.GetClipboardString(window) ?? "";
			}
			catch (Exception)
			{
				return "";
			}
		}
	}
}
