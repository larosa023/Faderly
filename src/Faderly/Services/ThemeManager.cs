using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Faderly.Services
{
    /// <summary>A named colour palette. Keys match the DynamicResource brushes used in the UI.</summary>
    public sealed class ThemeDefinition
    {
        public ThemeDefinition(string key, string name, bool isLight, Dictionary<string, string> colors)
        {
            Key = key;
            Name = name;
            IsLight = isLight;
            Colors = colors;
        }

        public string Key { get; }
        public string Name { get; }
        public bool IsLight { get; }
        public Dictionary<string, string> Colors { get; }
    }

    /// <summary>Theme catalog plus live switching. "System" follows the Windows light/dark setting.</summary>
    public static class ThemeManager
    {
        public const string SystemKey = "System";

        private static ResourceDictionary? _resources;
        private static bool _listening;

        public static string CurrentKey { get; private set; } = SystemKey;

        public static event EventHandler? ThemeChanged;

        public static IReadOnlyList<ThemeDefinition> Themes { get; } = new List<ThemeDefinition>
        {
            new("Dark", "Dark", false, new()
            {
                ["WindowBrush"] = "#17181C", ["SurfaceBrush"] = "#1F2026", ["Surface2Brush"] = "#272930",
                ["HoverBrush"] = "#2E3038", ["BorderBrush"] = "#2A2C33", ["BorderStrongBrush"] = "#3A3D46",
                ["TextBrush"] = "#EDEEF2", ["SubtextBrush"] = "#9396A1", ["TrackBrush"] = "#34363F",
                ["AccentBrush"] = "#7B8CFF", ["AccentSoftBrush"] = "#267B8CFF", ["MutedFillBrush"] = "#5A5D68",
                ["MeterBrush"] = "#3DDC97", ["ThumbBrush"] = "#F4F5F8", ["ThumbBorderBrush"] = "#00000000",
                ["ScrollThumbBrush"] = "#40FFFFFF", ["DangerBrush"] = "#FF6B6B",
            }),
            new("Light", "Light", true, new()
            {
                ["WindowBrush"] = "#F6F7F9", ["SurfaceBrush"] = "#FFFFFF", ["Surface2Brush"] = "#EEF0F4",
                ["HoverBrush"] = "#E6E9EF", ["BorderBrush"] = "#E1E4EA", ["BorderStrongBrush"] = "#C9CDD6",
                ["TextBrush"] = "#16181D", ["SubtextBrush"] = "#6A6F7B", ["TrackBrush"] = "#DDE1E8",
                ["AccentBrush"] = "#4F6BF5", ["AccentSoftBrush"] = "#1F4F6BF5", ["MutedFillBrush"] = "#A9AEB9",
                ["MeterBrush"] = "#22B07D", ["ThumbBrush"] = "#FFFFFF", ["ThumbBorderBrush"] = "#C9CDD6",
                ["ScrollThumbBrush"] = "#33000000", ["DangerBrush"] = "#D64545",
            }),
            new("Midnight", "Midnight", false, new()
            {
                ["WindowBrush"] = "#000000", ["SurfaceBrush"] = "#0E0E10", ["Surface2Brush"] = "#17171A",
                ["HoverBrush"] = "#1F1F23", ["BorderBrush"] = "#1C1C20", ["BorderStrongBrush"] = "#2C2C32",
                ["TextBrush"] = "#F2F2F5", ["SubtextBrush"] = "#8C8C96", ["TrackBrush"] = "#26262B",
                ["AccentBrush"] = "#A78BFA", ["AccentSoftBrush"] = "#26A78BFA", ["MutedFillBrush"] = "#4A4A52",
                ["MeterBrush"] = "#34D399", ["ThumbBrush"] = "#F2F2F5", ["ThumbBorderBrush"] = "#00000000",
                ["ScrollThumbBrush"] = "#40FFFFFF", ["DangerBrush"] = "#FF6B6B",
            }),
            new("Nord", "Nord", false, new()
            {
                ["WindowBrush"] = "#2E3440", ["SurfaceBrush"] = "#353B48", ["Surface2Brush"] = "#3B4252",
                ["HoverBrush"] = "#434C5E", ["BorderBrush"] = "#3B4252", ["BorderStrongBrush"] = "#4C566A",
                ["TextBrush"] = "#ECEFF4", ["SubtextBrush"] = "#A3ACBD", ["TrackBrush"] = "#4C566A",
                ["AccentBrush"] = "#88C0D0", ["AccentSoftBrush"] = "#2688C0D0", ["MutedFillBrush"] = "#616E88",
                ["MeterBrush"] = "#A3BE8C", ["ThumbBrush"] = "#ECEFF4", ["ThumbBorderBrush"] = "#00000000",
                ["ScrollThumbBrush"] = "#40FFFFFF", ["DangerBrush"] = "#BF616A",
            }),
            new("Ocean", "Ocean", false, new()
            {
                ["WindowBrush"] = "#0F1B26", ["SurfaceBrush"] = "#142433", ["Surface2Brush"] = "#1A2E40",
                ["HoverBrush"] = "#213851", ["BorderBrush"] = "#1C3043", ["BorderStrongBrush"] = "#2A4560",
                ["TextBrush"] = "#E6F1FA", ["SubtextBrush"] = "#8BA6BD", ["TrackBrush"] = "#24405A",
                ["AccentBrush"] = "#38BDF8", ["AccentSoftBrush"] = "#2638BDF8", ["MutedFillBrush"] = "#4A6580",
                ["MeterBrush"] = "#2DD4BF", ["ThumbBrush"] = "#E6F1FA", ["ThumbBorderBrush"] = "#00000000",
                ["ScrollThumbBrush"] = "#40FFFFFF", ["DangerBrush"] = "#F87171",
            }),
            new("Forest", "Forest", false, new()
            {
                ["WindowBrush"] = "#131A15", ["SurfaceBrush"] = "#1A221D", ["Surface2Brush"] = "#212B25",
                ["HoverBrush"] = "#28342D", ["BorderBrush"] = "#222C26", ["BorderStrongBrush"] = "#33433A",
                ["TextBrush"] = "#E8F0EA", ["SubtextBrush"] = "#93A59A", ["TrackBrush"] = "#2C3A32",
                ["AccentBrush"] = "#6FCF97", ["AccentSoftBrush"] = "#266FCF97", ["MutedFillBrush"] = "#4E6156",
                ["MeterBrush"] = "#C5E478", ["ThumbBrush"] = "#E8F0EA", ["ThumbBorderBrush"] = "#00000000",
                ["ScrollThumbBrush"] = "#40FFFFFF", ["DangerBrush"] = "#F28B82",
            }),
            new("Sunset", "Sunset", false, new()
            {
                ["WindowBrush"] = "#1C1517", ["SurfaceBrush"] = "#241B1E", ["Surface2Brush"] = "#2D2226",
                ["HoverBrush"] = "#36292E", ["BorderBrush"] = "#2E2327", ["BorderStrongBrush"] = "#45343A",
                ["TextBrush"] = "#F7ECEE", ["SubtextBrush"] = "#B39AA1", ["TrackBrush"] = "#3D2E33",
                ["AccentBrush"] = "#FF8A65", ["AccentSoftBrush"] = "#26FF8A65", ["MutedFillBrush"] = "#6B5359",
                ["MeterBrush"] = "#FFD166", ["ThumbBrush"] = "#F7ECEE", ["ThumbBorderBrush"] = "#00000000",
                ["ScrollThumbBrush"] = "#40FFFFFF", ["DangerBrush"] = "#FF6B6B",
            }),
            new("Rose", "Rosé", true, new()
            {
                ["WindowBrush"] = "#FBF6F4", ["SurfaceBrush"] = "#FFFFFF", ["Surface2Brush"] = "#F4ECE9",
                ["HoverBrush"] = "#EEE3DF", ["BorderBrush"] = "#EADFDB", ["BorderStrongBrush"] = "#D9C9C4",
                ["TextBrush"] = "#2A1F22", ["SubtextBrush"] = "#7D6B70", ["TrackBrush"] = "#E8DCD8",
                ["AccentBrush"] = "#D9607A", ["AccentSoftBrush"] = "#1FD9607A", ["MutedFillBrush"] = "#B8A7AB",
                ["MeterBrush"] = "#3BAF8A", ["ThumbBrush"] = "#FFFFFF", ["ThumbBorderBrush"] = "#D9C9C4",
                ["ScrollThumbBrush"] = "#33000000", ["DangerBrush"] = "#D64545",
            }),
        };

        public static ThemeDefinition Find(string key) =>
            Themes.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? Themes[0];

        /// <summary>The palette actually shown for a key ("System" resolves to Light or Dark).</summary>
        public static ThemeDefinition Resolve(string key) =>
            string.Equals(key, SystemKey, StringComparison.OrdinalIgnoreCase)
                ? Find(ReadSystemLightTheme() ? "Light" : "Dark")
                : Find(key);

        public static void Apply(ResourceDictionary resources, string key)
        {
            _resources = resources;
            bool known = string.Equals(key, SystemKey, StringComparison.OrdinalIgnoreCase) ||
                         Themes.Any(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
            CurrentKey = known ? key : SystemKey;

            var theme = Resolve(CurrentKey);
            foreach (var (name, hex) in theme.Colors)
                resources[name] = MakeBrush(hex);

            if (!_listening)
            {
                _listening = true;
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        public static SolidColorBrush MakeBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General) return;
            if (!string.Equals(CurrentKey, SystemKey, StringComparison.OrdinalIgnoreCase)) return;

            var app = Application.Current;
            var res = _resources;
            if (app == null || res == null) return;
            app.Dispatcher.BeginInvoke(new Action(() => Apply(res, SystemKey)));
        }

        private static bool ReadSystemLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int value)
                    return value == 1;
            }
            catch
            {
            }
            return false;
        }
    }
}
