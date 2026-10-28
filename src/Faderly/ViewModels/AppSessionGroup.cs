using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace Faderly.ViewModels
{
    /// <summary>
    /// One row in the mixer. An app can own several audio sessions (browsers, games);
    /// they're grouped so one slider controls them all.
    /// </summary>
    public sealed class AppSessionGroup : ObservableObject
    {
        private double _volume;
        private bool _isMuted;
        private double _peak;
        private bool _syncing;

        public AppSessionGroup(string key, string name, ImageSource? icon, bool isSystem)
        {
            Key = key;
            Name = name;
            Icon = icon;
            IsSystem = isSystem;
            ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        }

        public string Key { get; }
        public string Name { get; }
        public ImageSource? Icon { get; }
        public bool IsSystem { get; }
        public bool HasIcon => Icon != null;
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

        public ICommand ToggleMuteCommand { get; }

        internal List<AudioSessionControl> Sessions { get; } = new();

        public double Volume
        {
            get => _volume;
            set
            {
                value = Math.Clamp(Math.Round(value), 0, 100);
                if (!SetProperty(ref _volume, value)) return;
                OnPropertyChanged(nameof(VolumeText));
                if (_syncing) return;

                // Dragging the slider of a muted app unmutes it, like Windows does.
                if (_isMuted) IsMuted = false;
                Apply(s => s.SimpleAudioVolume.Volume = (float)(value / 100.0));
            }
        }

        public string VolumeText => $"{_volume:0}";

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (!SetProperty(ref _isMuted, value)) return;
                if (_syncing) return;
                Apply(s => s.SimpleAudioVolume.Mute = value);
            }
        }

        public double Peak
        {
            get => _peak;
            private set => SetProperty(ref _peak, value);
        }

        /// <summary>Pull current volume/mute from Windows (picks up changes made elsewhere).</summary>
        internal void SyncFromSystem()
        {
            if (Sessions.Count == 0) return;
            try
            {
                var simple = Sessions[0].SimpleAudioVolume;
                _syncing = true;
                Volume = simple.Volume * 100.0;
                IsMuted = simple.Mute;
            }
            catch
            {
            }
            finally
            {
                _syncing = false;
            }
        }

        internal void UpdatePeak()
        {
            double max = 0;
            foreach (var s in Sessions)
            {
                try
                {
                    double p = s.AudioMeterInformation.MasterPeakValue;
                    if (p > max) max = p;
                }
                catch
                {
                }
            }
            if (_isMuted) max = 0;
            // Fast attack, smooth release.
            double next = max >= _peak ? max : Math.Max(max, _peak * 0.82);
            Peak = next < 0.004 ? 0 : next;
        }

        private void Apply(Action<AudioSessionControl> action)
        {
            foreach (var s in Sessions)
            {
                try { action(s); }
                catch { /* session went away mid-change */ }
            }
        }
    }
}
