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
        private readonly List<AudioSessionControl> _sessions = new();
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
                float level = (float)(value / 100.0);
                Apply(v => v.Volume = level);
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
                Apply(v => v.Mute = value);
            }
        }

        public double Peak
        {
            get => _peak;
            private set => SetProperty(ref _peak, value);
        }

        /// <summary>Replace the sessions this row controls (only touches the list if it actually changed).</summary>
        internal void SetSessions(List<AudioSessionControl> sessions)
        {
            if (sessions.Count == _sessions.Count)
            {
                bool same = true;
                for (int i = 0; i < sessions.Count; i++)
                {
                    if (!ReferenceEquals(sessions[i], _sessions[i])) { same = false; break; }
                }
                if (same) return;
            }
            _sessions.Clear();
            _sessions.AddRange(sessions);
        }

        /// <summary>Pull current volume/mute from Windows (picks up changes made elsewhere).</summary>
        internal void SyncFromSystem()
        {
            if (_sessions.Count == 0) return;
            var simple = _sessions[0].SimpleAudioVolume;
            if (simple == null) return;
            try
            {
                _syncing = true;
                Volume = simple.Volume * 100.0;
                IsMuted = simple.Mute;
            }
            catch
            {
                // Session went away; the next refresh removes the row.
            }
            finally
            {
                _syncing = false;
            }
        }

        internal void UpdatePeak()
        {
            double max = 0;
            if (!_isMuted)
            {
                foreach (var s in _sessions)
                {
                    var meter = s.AudioMeterInformation;
                    if (meter == null) continue;
                    try
                    {
                        double p = meter.MasterPeakValue;
                        if (p > max) max = p;
                    }
                    catch
                    {
                    }
                }
            }
            // Fast attack, smooth release. Snapping tiny values to 0 means a silent app stops re-rendering.
            double next = max >= _peak ? max : Math.Max(max, _peak * 0.82);
            Peak = next < 0.004 ? 0 : next;
        }

        internal void ResetPeak() => Peak = 0;

        private void Apply(Action<SimpleAudioVolume> action)
        {
            foreach (var s in _sessions)
            {
                var simple = s.SimpleAudioVolume;
                if (simple == null) continue;
                try { action(simple); }
                catch { /* session went away mid-change */ }
            }
        }
    }
}
