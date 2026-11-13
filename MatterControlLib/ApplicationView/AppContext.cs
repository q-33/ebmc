/*
Copyright (c) 2022, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Markdig.Agg;
using Markdig.Renderers.Agg;
using MatterHackers.Agg;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.MatterControl.SlicerConfiguration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: InternalsVisibleTo("MatterControl.Tests")]
[assembly: InternalsVisibleTo("MatterControl.AutomationTests")]
[assembly: InternalsVisibleTo("CloudServices.Tests")]

namespace MatterHackers.MatterControl
{

	public static class AppContext
	{
		/// <summary>
		/// Gets or sets native platform features
		/// </summary>
		public static INativePlatformFeatures Platform { get; set; }

		public static MatterControlOptions Options { get; set; } = new MatterControlOptions();

		/// <summary>
		/// Gets or sets the scale the platform asks for on high density displays (2 on a Retina Mac).
		/// The user's text size setting is multiplied by this to produce GuiWidget.DeviceScale.
		/// Hosts that do not report a display scale, such as the Windows WinForms host, leave this at 1.
		/// </summary>
		public static double DisplayScale { get; set; } = 1;

		public static bool IsLoading { get; internal set; } = true;

		/// <summary>
		/// Gets the root SystemWindow
		/// </summary>
		public static SystemWindow RootSystemWindow { get; internal set; }

		public static ThemeConfig Theme => ThemeSet.Theme;

		public static ThemeConfig MenuTheme => ThemeSet.MenuTheme;

		private static ThemeSet _themeset;
		public static ThemeSet ThemeSet => _themeset;

		public static Dictionary<string, IColorTheme> ThemeProviders { get; }

		private static readonly Dictionary<string, string> themes = new();

		static AppContext()
		{
			ThemeProviders = new Dictionary<string, IColorTheme>();

			string themesPath = Path.Combine("Themes", "System");

			var staticData = StaticData.Instance;

			// Load available themes from StaticData
			if (staticData.DirectoryExists(themesPath))
			{
				var themeFiles = staticData.GetDirectories(themesPath).SelectMany(d => staticData.GetFiles(d).Where(p => Path.GetExtension(p) == ".json"));
				foreach (var themeFile in themeFiles)
				{
					themes[Path.GetFileNameWithoutExtension(themeFile)] = themeFile;
				}

				foreach (var directoryTheme in StaticData.Instance.GetDirectories(themesPath).Where(d => Path.GetFileName(d) != "Menus").Select(d => new DirectoryTheme(d)))
				{
					ThemeProviders.Add(directoryTheme.Name, directoryTheme);
				}
			}

			// EBMC Edition: a build with a locked theme always starts with it (default accent colour), whatever theme the
			// user stored; the stored file is left as it is. Stock loading below if the themeset is missing.
			string lockedProvider = ThemeLock.ProviderFor(
				OemSettings.Instance.LockedThemeSet,
				ThemeProviders.Values.Select(p => (p.Name, p.ThemeNames)));
			if (lockedProvider != null)
			{
				try
				{
					_themeset = ThemeProviders[lockedProvider].GetTheme(OemSettings.Instance.LockedThemeSet);
				}
				catch
				{
					_themeset = null;
				}
			}

			// Load theme
			try
			{
				if (_themeset == null
					&& File.Exists(ProfileManager.Instance.ProfileThemeSetPath))
				{
					_themeset = JsonConvert.DeserializeObject<ThemeSet>(File.ReadAllText(ProfileManager.Instance.ProfileThemeSetPath));
					ThemeSet.Theme.EnsureDefaults();
                    ThemeConfigExtensions.RebuildTheme(ThemeSet.Theme);

                    // If the serialized format is older than the current format, null and fall back to latest default below
                    if (ThemeSet.SchemeVersion != ThemeSet.LatestSchemeVersion)
					{
						_themeset = null;
					}
				}
			}
			catch { }

			if (ThemeSet == null)
			{
				// EBMC Edition: default to the club theme, falling back to the stock default if it is missing
				if (ThemeProviders.TryGetValue("EBMC", out IColorTheme clubThemeProvider))
				{
					_themeset = clubThemeProvider.GetTheme("EBMC-Dark");
				}
				else
				{
					var themeProvider = ThemeProviders["Modern"];
					_themeset = themeProvider.GetTheme("Modern-Dark");
				}
			}

			ToolTipManager.CreateToolTip = MatterControlToolTipWidget;
		}

		private static GuiWidget MatterControlToolTipWidget(string toolTipText)
		{
			var toolTipPopover = new ClickablePopover(ArrowDirection.Up, new BorderDouble(0, 0), 7, 0);

            // EBMC Edition: a raised panel (the menu background) with a rounded line-coloured outline instead of a
            // square black border, a little inner padding, and body text at the theme's default size (Markdown body
            // text is 10 pt in agg-sharp)
            var markdownWidegt = new MarkdownWidget(Theme, false)
            {
                HAnchor = HAnchor.Absolute,
                VAnchor = VAnchor.Fit,
                Width = 350 * GuiWidget.DeviceScale,
                BackgroundColor = MenuTheme.BackgroundColor,
                BackgroundRadius = new RadiusCorners(6 * GuiWidget.DeviceScale),
                BackgroundOutlineWidth = 1,
                BorderColor = Theme.BorderColor20,
                Padding = new BorderDouble(6, 4),
                Markdown = toolTipText
            };

            foreach (var word in markdownWidegt.Descendants<TextWidget>().Where(t => t.PointSize == 10))
            {
                word.PointSize = Theme.DefaultFontSize;
            }

            markdownWidegt.Width = 350 * GuiWidget.DeviceScale;
			var maxLineWidth = 0.0;
			if (markdownWidegt.Descendants<ParagraphX>().Any())
			{
				maxLineWidth = markdownWidegt.Descendants<ParagraphX>().Max(i => i.MaxLineWidth);
			}

			markdownWidegt.Width = maxLineWidth + 15 + markdownWidegt.DevicePadding.Width;

			return markdownWidegt;
		}

		public static ThemeConfig LoadTheme(string themeName)
		{
			try
			{
				if (themes.TryGetValue(themeName, out string themePath))
				{
					string json = StaticData.Instance.ReadAllText(themePath);

					var themeConfig = JsonConvert.DeserializeObject<ThemeConfig>(json);
					themeConfig.EnsureDefaults();
					ThemeConfigExtensions.RebuildTheme(themeConfig);
                    
					return themeConfig;
				}
			}
			catch
			{
				Console.WriteLine("Error loading theme: " + themeName);
			}

			return new ThemeConfig();
		}

		public static void SetThemeAccentColor(Color accentColor)
		{
			ThemeSet.SetAccentColor(accentColor);
			AppContext.SetTheme(ThemeSet);
		}

		public static void SetTheme(ThemeSet themeSet)
		{
			// EBMC Edition: no theme changes when the build locks its theme (the pickers are not shown then)
			if (OemSettings.Instance.IsThemeLocked)
			{
				return;
			}

			_themeset = themeSet;

			File.WriteAllText(
				ProfileManager.Instance.ProfileThemeSetPath,
				JsonConvert.SerializeObject(
					ThemeSet,
					Formatting.Indented,
					new JsonSerializerSettings
					{
						ContractResolver = new ThemeContractResolver()
					}));

			UiThread.RunOnIdle(() =>
			{
				UserSettings.Instance.set(UserSettingsKey.ActiveThemeName, ThemeSet.Name);

				// Explicitly fire ReloadAll in response to user interaction
				ApplicationController.Instance.ReloadAll().ConfigureAwait(false);
			});
		}

		public class MatterControlOptions
		{
			public bool McwsTestEnvironment { get; set; }
		}
	}
}
