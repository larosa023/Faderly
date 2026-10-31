using System.Windows.Media;

namespace Faderly.ViewModels
{
    /// <summary>One choice in an app's "Play through" list. Id null = follow the Windows default.</summary>
    public sealed class DeviceOption : ObservableObject
    {
        private bool _isSelected;

        public DeviceOption(string? id, string name, string glyph)
        {
            Id = id;
            Name = name;
            Glyph = glyph;
        }

        public string? Id { get; }
        public string Name { get; }
        public string Glyph { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    public sealed class HiddenAppItem
    {
        public HiddenAppItem(string key, string name)
        {
            Key = key;
            Name = name;
        }

        public string Key { get; }
        public string Name { get; }
    }

    public sealed class AppHotkeyItem
    {
        public AppHotkeyItem(string key, string name, string summary)
        {
            Key = key;
            Name = name;
            Summary = summary;
        }

        public string Key { get; }
        public string Name { get; }
        public string Summary { get; }
    }

    /// <summary>What the on-screen indicator shows after a hotkey / knob / tray-wheel change.</summary>
    public sealed class VolumeFeedback
    {
        public VolumeFeedback(string title, string glyph, ImageSource? icon, double level, bool muted, string? detail = null)
        {
            Title = title;
            Glyph = glyph;
            Icon = icon;
            Level = level;
            Muted = muted;
            Detail = detail;
        }

        public string Title { get; }
        public string Glyph { get; }
        public ImageSource? Icon { get; }

        /// <summary>0–100, or negative to hide the bar.</summary>
        public double Level { get; }

        public bool Muted { get; }

        /// <summary>Right-hand text; defaults to the percentage (or "Muted").</summary>
        public string? Detail { get; }

        public bool HasIcon => Icon != null;
        public bool HasLevel => Level >= 0;
        public double LevelFraction => Level < 0 ? 0 : Level / 100.0;

        public string RightText => Detail ?? (Muted ? "Muted" : Level < 0 ? "" : $"{Level:0}%");
    }

    public enum AppHotkeyKind
    {
        Up,
        Down,
        Mute
    }
}
