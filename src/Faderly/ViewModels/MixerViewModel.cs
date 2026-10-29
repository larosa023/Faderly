using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Faderly.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Faderly.ViewModels
{
    /// <summary>
    /// Everything the mixer shows. Designed to cost nothing while hidden: no timers run until the
    /// window is on screen, and audio sessions are identified once and then reused.
    /// </summary>
    public sealed class MixerViewModel : ObservableObject, IDisposable
    {
        private const string SystemKey = "__system__";

        /// <summary>An audio session we've already identified, so it isn't re-resolved every refresh.</summary>
        private sealed class SessionEntry
        {
            public SessionEntry(AudioSessionControl control, string groupKey, string name,
                                ImageSource? icon, bool isSystem, uint pid, bool hidden)
            {
                Control = control;
                GroupKey = groupKey;
                Name = name;
                Icon = icon;
                IsSystem = isSystem;
                Pid = pid;
                Hidden = hidden;
            }

            public AudioSessionControl Control { get; }
            public string GroupKey { get; }
            public string Name { get; }
            public ImageSource? Icon { get; }
            public bool IsSystem { get; }
            public uint Pid { get; }
            /// <summary>Never shown (e.g. Faderly's own session).</summary>
            public bool Hidden { get; }
        }

        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _slowTimer;
        private readonly DispatcherTimer _meterTimer;
        private readonly AppSettings _settings;
        private readonly uint _ownPid = (uint)Environment.ProcessId;
        private readonly Dictionary<string, SessionEntry> _sessionCache = new(StringComparer.Ordinal);

        private MMDevice? _device;
        private AudioEndpointVolume? _endpointVolume;
        private string _deviceName = "No output device";
        private string _deviceGlyph = "";
        private double _masterVolume;
        private bool _masterMuted;
        private double _masterPeak;
        private bool _syncingMaster;
        private bool _isDeviceListOpen;
        private bool _isSettingsOpen;
        private volatile bool _isWindowVisible;
        private int _masterSyncQueued;
        private bool _hotkeyAvailable = true;
        private bool _hasApps;

        public event EventHandler? QuitRequested;

        /// <summary>Raised when switching between tray popup and app window.</summary>
        public event EventHandler? WindowModeChanged;

        public MixerViewModel(AppSettings settings, UpdateService updater)
        {
            _settings = settings;
            _dispatcher = Dispatcher.CurrentDispatcher;
            Updater = updater;

            ToggleMasterMuteCommand = new RelayCommand(() => MasterMuted = !MasterMuted);
            ToggleDeviceListCommand = new RelayCommand(() => IsDeviceListOpen = !IsDeviceListOpen);
            SelectDeviceCommand = new RelayCommand(p => SelectDevice(p as DeviceItem));
            OpenSettingsCommand = new RelayCommand(() => { IsDeviceListOpen = false; IsSettingsOpen = true; });
            CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
            CheckUpdatesCommand = new RelayCommand(async () => await Updater.CheckAsync(userInitiated: true));
            RestartToUpdateCommand = new RelayCommand(() => Updater.RestartToUpdate());
            OpenGitHubCommand = new RelayCommand(() => OpenUri(UpdateService.RepoUrl));
            OpenSoundSettingsCommand = new RelayCommand(() => OpenUri("ms-settings:sound"));
            QuitCommand = new RelayCommand(() => QuitRequested?.Invoke(this, EventArgs.Empty));
            ToggleWindowModeCommand = new RelayCommand(() => IsWindowMode = !IsWindowMode);
            SelectThemeCommand = new RelayCommand(p => SelectTheme(p as ThemeOption));

            Themes.Add(new ThemeOption(ThemeManager.SystemKey, "System"));
            foreach (var t in ThemeManager.Themes)
                Themes.Add(new ThemeOption(t.Key, t.Name));
            MarkSelectedTheme();

            // Both timers only run while the mixer is on screen (see SetWindowVisible).
            _slowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _slowTimer.Tick += (_, _) => Refresh();

            _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _meterTimer.Tick += (_, _) => UpdateMeters();

            // Warm up once at startup (devices, sessions, icons) so the first open is instant.
            Refresh();
        }

        // ---------- Bindable state ----------

        public UpdateService Updater { get; }
        public ObservableCollection<AppSessionGroup> Apps { get; } = new();
        public ObservableCollection<DeviceItem> Devices { get; } = new();
        public ObservableCollection<ThemeOption> Themes { get; } = new();

        public bool HasApps
        {
            get => _hasApps;
            private set => SetProperty(ref _hasApps, value);
        }

        public AppSettings Settings => _settings;

        /// <summary>true = regular app window with a taskbar button; false = tray popup.</summary>
        public bool IsWindowMode
        {
            get => _settings.WindowMode;
            set
            {
                if (_settings.WindowMode == value) return;
                _settings.WindowMode = value;
                SettingsStore.Save(_settings);
                OnPropertyChanged();
                OnPropertyChanged(nameof(WindowModeGlyph));
                OnPropertyChanged(nameof(WindowModeTip));
                WindowModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string WindowModeGlyph => IsWindowMode ? "" : "";
        public string WindowModeTip => IsWindowMode ? "Switch to tray popup" : "Open as a window";

        public bool HotkeyAvailable
        {
            get => _hotkeyAvailable;
            set
            {
                if (SetProperty(ref _hotkeyAvailable, value)) OnPropertyChanged(nameof(HotkeyText));
            }
        }

        public string HotkeyText => _hotkeyAvailable ? "Ctrl + Alt + M" : "Ctrl + Alt + M (used by another app)";

        public string DeviceName
        {
            get => _deviceName;
            private set => SetProperty(ref _deviceName, value);
        }

        public string DeviceGlyph
        {
            get => _deviceGlyph;
            private set => SetProperty(ref _deviceGlyph, value);
        }

        public bool HasDevice => _device != null;

        public double MasterVolume
        {
            get => _masterVolume;
            set
            {
                value = Math.Clamp(Math.Round(value), 0, 100);
                if (!SetProperty(ref _masterVolume, value)) return;
                OnPropertyChanged(nameof(MasterVolumeText));
                OnPropertyChanged(nameof(MasterGlyph));
                if (_syncingMaster || _endpointVolume == null) return;
                try
                {
                    if (_masterMuted) MasterMuted = false;
                    _endpointVolume.MasterVolumeLevelScalar = (float)(value / 100.0);
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("Set master volume", ex);
                }
            }
        }

        public string MasterVolumeText => $"{_masterVolume:0}";

        public bool MasterMuted
        {
            get => _masterMuted;
            set
            {
                if (!SetProperty(ref _masterMuted, value)) return;
                OnPropertyChanged(nameof(MasterGlyph));
                if (_syncingMaster || _endpointVolume == null) return;
                try { _endpointVolume.Mute = value; }
                catch (Exception ex) { Log.WarnOnce("Set master mute", ex); }
            }
        }

        /// <summary>Speaker glyph that reflects the level, like the Windows flyout.</summary>
        public string MasterGlyph
        {
            get
            {
                if (_masterMuted || _masterVolume <= 0) return "";
                if (_masterVolume < 34) return "";
                if (_masterVolume < 67) return "";
                return "";
            }
        }

        public double MasterPeak
        {
            get => _masterPeak;
            private set => SetProperty(ref _masterPeak, value);
        }

        public bool IsDeviceListOpen
        {
            get => _isDeviceListOpen;
            set => SetProperty(ref _isDeviceListOpen, value);
        }

        public bool IsSettingsOpen
        {
            get => _isSettingsOpen;
            set => SetProperty(ref _isSettingsOpen, value);
        }

        public bool StartWithWindows
        {
            get => _settings.StartWithWindows;
            set
            {
                if (_settings.StartWithWindows == value) return;
                _settings.StartWithWindows = value;
                StartupManager.Apply(value);
                SettingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }

        public bool AutoCheckUpdates
        {
            get => _settings.AutoCheckUpdates;
            set
            {
                if (_settings.AutoCheckUpdates == value) return;
                _settings.AutoCheckUpdates = value;
                Updater.StartAutomaticChecks(value);
                SettingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }

        public bool ShowSystemSounds
        {
            get => _settings.ShowSystemSounds;
            set
            {
                if (_settings.ShowSystemSounds == value) return;
                _settings.ShowSystemSounds = value;
                SettingsStore.Save(_settings);
                OnPropertyChanged();
                RefreshSessions();
            }
        }

        // ---------- Commands ----------

        public ICommand ToggleMasterMuteCommand { get; }
        public ICommand ToggleDeviceListCommand { get; }
        public ICommand SelectDeviceCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand CloseSettingsCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand RestartToUpdateCommand { get; }
        public ICommand OpenGitHubCommand { get; }
        public ICommand OpenSoundSettingsCommand { get; }
        public ICommand QuitCommand { get; }
        public ICommand ToggleWindowModeCommand { get; }
        public ICommand SelectThemeCommand { get; }

        private void SelectTheme(ThemeOption? option)
        {
            if (option == null) return;
            _settings.Theme = option.Key;
            SettingsStore.Save(_settings);
            var app = System.Windows.Application.Current;
            if (app != null) ThemeManager.Apply(app.Resources, option.Key);
            MarkSelectedTheme();
        }

        private void MarkSelectedTheme()
        {
            foreach (var t in Themes)
                t.IsSelected = string.Equals(t.Key, _settings.Theme, StringComparison.OrdinalIgnoreCase);
            if (!Themes.Any(t => t.IsSelected) && Themes.Count > 0)
                Themes[0].IsSelected = true;
        }

        // ---------- Visibility drives all background work ----------

        public void SetWindowVisible(bool visible)
        {
            if (visible)
            {
                bool wasVisible = _isWindowVisible;
                _isWindowVisible = true;
                if (!wasVisible) Refresh();
                _slowTimer.Start();
                _meterTimer.Start();
            }
            else
            {
                _isWindowVisible = false;
                _slowTimer.Stop();
                _meterTimer.Stop();
                IsDeviceListOpen = false;
                if (!IsWindowMode) IsSettingsOpen = false;

                // Let meters rest at zero so they don't flash old levels on the next open.
                MasterPeak = 0;
                foreach (var app in Apps) app.ResetPeak();
            }
        }

        // ---------- Audio plumbing ----------

        private void Refresh()
        {
            RefreshDevices();
            SyncMaster();
            RefreshSessions();
        }

        private void RefreshDevices()
        {
            var active = new List<MMDevice>();
            string? defaultId = null;
            try
            {
                foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    active.Add(d);
                if (active.Count > 0)
                {
                    using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def.ID;
                }
            }
            catch (Exception ex)
            {
                // Usually a device mid-plug; keep the current state and try again next tick.
                Log.WarnOnce("Enumerating devices", ex);
                foreach (var d in active) d.Dispose();
                return;
            }

            try
            {
                // Rebuild the device list only if the set of devices changed.
                var ids = active.Select(d => d.ID).ToList();
                if (!ids.SequenceEqual(Devices.Select(d => d.Id)))
                {
                    Devices.Clear();
                    foreach (var d in active)
                    {
                        string name;
                        try { name = d.FriendlyName; } catch { name = "Audio device"; }
                        Devices.Add(new DeviceItem(d.ID, name));
                    }
                }
                foreach (var item in Devices)
                    item.IsDefault = item.Id == defaultId;
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Reading devices", ex);
            }
            finally
            {
                foreach (var d in active) d.Dispose();
            }

            string? currentId = null;
            try { currentId = _device?.ID; } catch { }

            if (defaultId != currentId)
                SwitchToDevice(defaultId);
        }

        private void SwitchToDevice(string? id)
        {
            DetachDevice();

            if (id != null)
            {
                try
                {
                    _device = _enumerator.GetDevice(id);
                    _endpointVolume = _device.AudioEndpointVolume;
                    // Instant updates when the volume changes elsewhere (keyboard keys, knobs, Windows flyout).
                    _endpointVolume.OnVolumeNotification += OnEndpointVolumeChanged;
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("Opening device", ex);
                    DetachDevice();
                }
            }

            var item = Devices.FirstOrDefault(d => d.Id == id);
            DeviceName = _device != null ? item?.Name ?? "Audio device" : "No output device";
            DeviceGlyph = item?.Glyph ?? "";
            OnPropertyChanged(nameof(HasDevice));

            SyncMaster();
            RefreshSessions();
        }

        private void DetachDevice()
        {
            if (_endpointVolume != null)
            {
                try { _endpointVolume.OnVolumeNotification -= OnEndpointVolumeChanged; } catch { }
            }
            _endpointVolume = null;

            foreach (var entry in _sessionCache.Values)
            {
                try { entry.Control.Dispose(); } catch { }
            }
            _sessionCache.Clear();
            Apps.Clear();
            HasApps = false;

            try { _device?.Dispose(); } catch { }
            _device = null;
        }

        /// <summary>Called on a Windows audio thread; hop to the UI thread, coalescing bursts.</summary>
        private void OnEndpointVolumeChanged(AudioVolumeNotificationData data)
        {
            if (!_isWindowVisible) return;
            if (Interlocked.Exchange(ref _masterSyncQueued, 1) == 1) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                Interlocked.Exchange(ref _masterSyncQueued, 0);
                if (_isWindowVisible) SyncMaster();
            }));
        }

        private void SelectDevice(DeviceItem? item)
        {
            IsDeviceListOpen = false;
            if (item == null || item.IsDefault) return;
            if (Native.SetDefaultAudioDevice(item.Id))
                RefreshDevices();
            else
                Log.Info("Couldn't switch default device to " + item.Name);
        }

        private void SyncMaster()
        {
            if (_endpointVolume == null) return;
            try
            {
                _syncingMaster = true;
                // Always re-read the live value (never trust an event payload), so a burst of
                // notifications while dragging can't snap the slider back to an older level.
                MasterVolume = _endpointVolume.MasterVolumeLevelScalar * 100.0;
                MasterMuted = _endpointVolume.Mute;
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Reading master volume", ex);
            }
            finally
            {
                _syncingMaster = false;
            }
        }

        private void RefreshSessions()
        {
            if (_device == null)
            {
                Apps.Clear();
                HasApps = false;
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var groups = new Dictionary<string, List<SessionEntry>>(StringComparer.Ordinal);
            var order = new List<string>();

            try
            {
                var manager = _device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                int count = sessions?.Count ?? 0;

                for (int i = 0; i < count; i++)
                {
                    AudioSessionControl? fresh = null;
                    try
                    {
                        fresh = sessions![i];
                        string id = fresh.GetSessionInstanceIdentifier ?? "";

                        if (fresh.State == AudioSessionState.AudioSessionStateExpired)
                        {
                            fresh.Dispose();
                            continue;
                        }

                        SessionEntry? entry;
                        if (id.Length > 0 && _sessionCache.TryGetValue(id, out entry))
                        {
                            // Same session as last time: keep the wrapper we already have.
                            fresh.Dispose();
                        }
                        else
                        {
                            entry = Identify(fresh);
                            if (id.Length > 0) _sessionCache[id] = entry;
                        }
                        fresh = null;
                        if (id.Length > 0) seen.Add(id);

                        if (entry.Hidden) continue;
                        if (entry.IsSystem && !_settings.ShowSystemSounds) continue;
                        if (!entry.IsSystem && !Native.IsProcessAlive(entry.Pid)) continue;

                        if (!groups.TryGetValue(entry.GroupKey, out var list))
                        {
                            list = new List<SessionEntry>();
                            groups[entry.GroupKey] = list;
                            order.Add(entry.GroupKey);
                        }
                        list.Add(entry);
                    }
                    catch
                    {
                        // Session vanished while we were reading it.
                        try { fresh?.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                // Device unplugged mid-refresh; the next device refresh catches up.
                Log.WarnOnce("Reading sessions", ex);
                return;
            }

            // Forget sessions that no longer exist.
            if (_sessionCache.Count > seen.Count)
            {
                foreach (var stale in _sessionCache.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    try { _sessionCache[stale].Control.Dispose(); } catch { }
                    _sessionCache.Remove(stale);
                }
            }

            // Remove rows whose app is gone.
            for (int i = Apps.Count - 1; i >= 0; i--)
                if (!groups.ContainsKey(Apps[i].Key))
                    Apps.RemoveAt(i);

            // Update existing rows and insert new ones in sorted order.
            foreach (var key in order)
            {
                var entries = groups[key];
                var controls = entries.Select(e => e.Control).ToList();
                var row = Apps.FirstOrDefault(a => a.Key == key);
                if (row == null)
                {
                    var first = entries[0];
                    row = new AppSessionGroup(key, first.Name, first.Icon, first.IsSystem);
                    row.SetSessions(controls);
                    row.SyncFromSystem();
                    Apps.Insert(SortedIndex(row), row);
                }
                else
                {
                    row.SetSessions(controls);
                    row.SyncFromSystem();
                }
            }

            HasApps = Apps.Count > 0;
        }

        private SessionEntry Identify(AudioSessionControl s)
        {
            bool isSystem = s.IsSystemSoundsSession;
            uint pid = s.GetProcessID;

            if (isSystem)
                return new SessionEntry(s, SystemKey, "System sounds", null, true, pid, hidden: false);
            if (pid == _ownPid)
                return new SessionEntry(s, "", "", null, false, pid, hidden: true);

            var info = AppInfoResolver.Resolve(pid, s.DisplayName);
            return new SessionEntry(s, info.Key, info.Name, info.Icon, false, pid, hidden: false);
        }

        /// <summary>Apps alphabetical, System sounds pinned to the bottom.</summary>
        private int SortedIndex(AppSessionGroup row)
        {
            if (row.IsSystem) return Apps.Count;
            for (int i = 0; i < Apps.Count; i++)
            {
                var other = Apps[i];
                if (other.IsSystem) return i;
                if (string.Compare(row.Name, other.Name, StringComparison.CurrentCultureIgnoreCase) < 0)
                    return i;
            }
            return Apps.Count;
        }

        private void UpdateMeters()
        {
            var device = _device;
            if (device != null)
            {
                double p = 0;
                if (!_masterMuted)
                {
                    try { p = device.AudioMeterInformation.MasterPeakValue; }
                    catch { }
                }
                double next = p >= _masterPeak ? p : Math.Max(p, _masterPeak * 0.82);
                MasterPeak = next < 0.004 ? 0 : next;
            }

            foreach (var app in Apps)
                app.UpdatePeak();
        }

        private static void OpenUri(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Opening " + uri, ex);
            }
        }

        public void Dispose()
        {
            _isWindowVisible = false;
            _slowTimer.Stop();
            _meterTimer.Stop();
            DetachDevice();
            try { _enumerator.Dispose(); } catch { }
        }
    }
}
