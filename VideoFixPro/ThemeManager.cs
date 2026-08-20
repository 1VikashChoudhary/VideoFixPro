using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace VideoFixPro
{
    public static class ThemeManager
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoFixPro"
        );
        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "theme.json");

        public static bool IsDarkTheme { get; private set; } = true;

        public static event Action? ThemeChanged;

        public static void Initialize()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("IsDarkTheme", out var prop))
                    {
                        IsDarkTheme = prop.GetBoolean();
                    }
                }
            }
            catch
            {
                IsDarkTheme = true;
            }

            ApplyTheme(IsDarkTheme);
        }

        public static void ToggleTheme()
        {
            SetTheme(!IsDarkTheme);
        }

        public static void SetTheme(bool isDark)
        {
            IsDarkTheme = isDark;
            ApplyTheme(isDark);
            SavePreference();
            ThemeChanged?.Invoke();
        }

        private static void ApplyTheme(bool isDark)
        {
            if (Application.Current == null) return;

            var res = Application.Current.Resources;

            if (isDark)
            {
                // Dark Theme Palette (GitHub Dark inspired)
                SetBrush(res, "BgBrush", "#0D1117");
                SetBrush(res, "SurfaceBrush", "#161B22");
                SetBrush(res, "CardBrush", "#21262D");
                SetBrush(res, "CardHoverBrush", "#2D333B");
                SetBrush(res, "BorderBrush", "#30363D");
                SetBrush(res, "Border2Brush", "#21262D");
                SetBrush(res, "AccentBrush", "#388BFD");
                SetBrush(res, "AccentHoverBrush", "#1F6FEB");
                SetBrush(res, "SuccessBrush", "#3FB950");
                SetBrush(res, "ErrorBrush", "#F85149");
                SetBrush(res, "WarningBrush", "#D29922");
                SetBrush(res, "TextBrush", "#E6EDF3");
                SetBrush(res, "Text2Brush", "#8B949E");
                SetBrush(res, "MutedBrush", "#6E7681");
                SetBrush(res, "TitleBarBrush", "#010409");
                SetBrush(res, "LogBgBrush", "#090C10");
                SetBrush(res, "LogTextBrush", "#3FB950");
                SetBrush(res, "InputBgBrush", "#161B22");
                SetBrush(res, "BadgeBgBrush", "#1F3152");
                SetBrush(res, "BadgeTextBrush", "#79C0FF");
            }
            else
            {
                // Light Theme Palette (GitHub Light / Modern Pro inspired)
                SetBrush(res, "BgBrush", "#F6F8FA");
                SetBrush(res, "SurfaceBrush", "#FFFFFF");
                SetBrush(res, "CardBrush", "#F0F2F5");
                SetBrush(res, "CardHoverBrush", "#E1E4E8");
                SetBrush(res, "BorderBrush", "#D0D7DE");
                SetBrush(res, "Border2Brush", "#E1E4E8");
                SetBrush(res, "AccentBrush", "#0969DA");
                SetBrush(res, "AccentHoverBrush", "#0550AE");
                SetBrush(res, "SuccessBrush", "#1A7F37");
                SetBrush(res, "ErrorBrush", "#CF222E");
                SetBrush(res, "WarningBrush", "#9A6700");
                SetBrush(res, "TextBrush", "#1F2328");
                SetBrush(res, "Text2Brush", "#57606A");
                SetBrush(res, "MutedBrush", "#6E7781");
                SetBrush(res, "TitleBarBrush", "#EAEEF2");
                SetBrush(res, "LogBgBrush", "#FFFFFF");
                SetBrush(res, "LogTextBrush", "#1A7F37");
                SetBrush(res, "InputBgBrush", "#FFFFFF");
                SetBrush(res, "BadgeBgBrush", "#DDF4FF");
                SetBrush(res, "BadgeTextBrush", "#0969DA");
            }
        }

        private static void SetBrush(ResourceDictionary res, string key, string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                res[key] = brush;
            }
            catch { }
        }

        private static void SavePreference()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);

                string json = JsonSerializer.Serialize(new { IsDarkTheme });
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }
    }
}
