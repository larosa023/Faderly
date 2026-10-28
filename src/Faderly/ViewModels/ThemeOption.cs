using System.Windows;
using System.Windows.Media;
using Faderly.Services;

namespace Faderly.ViewModels
{
    /// <summary>One swatch in the theme picker.</summary>
    public sealed class ThemeOption : ObservableObject
    {
        private bool _isSelected;

        public ThemeOption(string key, string name)
        {
            Key = key;
            Name = name;

            if (key == ThemeManager.SystemKey)
            {
                // Half light, half dark.
                var light = ThemeManager.Find("Light").Colors;
                var dark = ThemeManager.Find("Dark").Colors;
                var split = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                split.GradientStops.Add(new GradientStop(Parse(light["WindowBrush"]), 0.5));
                split.GradientStops.Add(new GradientStop(Parse(dark["WindowBrush"]), 0.5));
                split.Freeze();
                Background = split;
                Surface = ThemeManager.MakeBrush("#80888B94");
                Accent = ThemeManager.MakeBrush(dark["AccentBrush"]);
                Meter = ThemeManager.MakeBrush(dark["MeterBrush"]);
            }
            else
            {
                var c = ThemeManager.Find(key).Colors;
                Background = ThemeManager.MakeBrush(c["WindowBrush"]);
                Surface = ThemeManager.MakeBrush(c["Surface2Brush"]);
                Accent = ThemeManager.MakeBrush(c["AccentBrush"]);
                Meter = ThemeManager.MakeBrush(c["MeterBrush"]);
            }
        }

        public string Key { get; }
        public string Name { get; }
        public Brush Background { get; }
        public Brush Surface { get; }
        public Brush Accent { get; }
        public Brush Meter { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    }
}
