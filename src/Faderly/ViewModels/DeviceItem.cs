namespace Faderly.ViewModels
{
    public sealed class DeviceItem : ObservableObject
    {
        private bool _isDefault;

        public DeviceItem(string id, string name, bool isCapture = false)
        {
            Id = id;
            Name = name;
            IsCapture = isCapture;
        }

        public string Id { get; }
        public string Name { get; }
        public bool IsCapture { get; }

        public bool IsDefault
        {
            get => _isDefault;
            set => SetProperty(ref _isDefault, value);
        }

        /// <summary>Segoe Fluent/MDL2 glyph that matches the kind of device.</summary>
        public string Glyph => GlyphFor(Name, IsCapture);

        public static string GlyphFor(string name, bool isCapture)
        {
            string n = name.ToLowerInvariant();
            bool headset = n.Contains("headphone") || n.Contains("headset") || n.Contains("earbud") ||
                           n.Contains("airpods") || n.Contains("buds") || n.Contains("hands-free");
            if (headset) return "";
            if (isCapture) return "";
            if (n.Contains("hdmi") || n.Contains("display") || n.Contains("monitor") || n.Contains("tv"))
                return "";
            return "";
        }
    }
}
