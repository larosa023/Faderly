using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Faderly.Services
{
    /// <summary>Puts the colour palette into Application.Resources, following the Windows app theme.</summary>
    public static class ThemeManager
    {
        public static bool IsLight { get; private set; }

        public static void Apply(ResourceDictionary resources)
        {
            IsLight = ReadSystemLightTheme();

            if (IsLight)
            {
                Set(resources, "WindowBrush", "#F6F7F9");
                Set(resources, "SurfaceBrush", "#FFFFFF");
                Set(resources, "Surface2Brush", "#EEF0F4");
                Set(resources, "HoverBrush", "#E6E9EF");
                Set(resources, "BorderBrush", "#E1E4EA");
                Set(resources, "BorderStrongBrush", "#C9CDD6");
                Set(resources, "TextBrush", "#16181D");
                Set(resources, "SubtextBrush", "#6A6F7B");
                Set(resources, "TrackBrush", "#DDE1E8");
                Set(resources, "AccentBrush", "#4F6BF5");
                Set(resources, "AccentSoftBrush", "#1F4F6BF5");
                Set(resources, "MutedFillBrush", "#A9AEB9");
                Set(resources, "MeterBrush", "#22B07D");
                Set(resources, "ThumbBrush", "#FFFFFF");
                Set(resources, "ThumbBorderBrush", "#C9CDD6");
                Set(resources, "ScrollThumbBrush", "#33000000");
                Set(resources, "DangerBrush", "#D64545");
            }
            else
            {
                Set(resources, "WindowBrush", "#17181C");
                Set(resources, "SurfaceBrush", "#1F2026");
                Set(resources, "Surface2Brush", "#272930");
                Set(resources, "HoverBrush", "#2E3038");
                Set(resources, "BorderBrush", "#2A2C33");
                Set(resources, "BorderStrongBrush", "#3A3D46");
                Set(resources, "TextBrush", "#EDEEF2");
                Set(resources, "SubtextBrush", "#9396A1");
                Set(resources, "TrackBrush", "#34363F");
                Set(resources, "AccentBrush", "#7B8CFF");
                Set(resources, "AccentSoftBrush", "#267B8CFF");
                Set(resources, "MutedFillBrush", "#5A5D68");
                Set(resources, "MeterBrush", "#3DDC97");
                Set(resources, "ThumbBrush", "#F4F5F8");
                Set(resources, "ThumbBorderBrush", "#00000000");
                Set(resources, "ScrollThumbBrush", "#40FFFFFF");
                Set(resources, "DangerBrush", "#FF6B6B");
            }
        }

        private static void Set(ResourceDictionary resources, string key, string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
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
