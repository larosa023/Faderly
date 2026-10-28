namespace Faderly.ViewModels
{
    public sealed class DeviceItem : ObservableObject
    {
        private bool _isDefault;

        public DeviceItem(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }

        public bool IsDefault
        {
            get => _isDefault;
            set => SetProperty(ref _isDefault, value);
        }

        /// <summary>Segoe Fluent/MDL2 glyph: headphones for headsets, speaker otherwise.</summary>
        public string Glyph
        {
            get
            {
                string n = Name.ToLowerInvariant();
                if (n.Contains("headphone") || n.Contains("headset") || n.Contains("earbud") ||
                    n.Contains("airpods") || n.Contains("buds") || n.Contains("hands-free"))
                    return "";
                if (n.Contains("hdmi") || n.Contains("display") || n.Contains("monitor") || n.Contains("tv"))
                    return "";
                return "";
            }
        }
    }
}
