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
        private const int HotkeyStep = 2;

        /// <summary>An audio session we've already identified, so it isn't re-resolved every refresh.</summary>
        private sealed class SessionEntry
        {
            public SessionEntry(AudioSessionControl control, string deviceId, string groupKey, string name,
                                ImageSource? icon, bool isSystem, uint pid, bool own)
            {
                Control = control;
                DeviceId = deviceId;
                GroupKey = groupKey;
                Name = name;
                Icon = icon;
                IsSystem = isSystem;
                Pid = pid;
                Own = own;
            }

            public AudioSessionControl Control { get; }
            public string DeviceId { get; }
            public string GroupKey { get; }
            public string Name { get; }
            public ImageSource? Icon { get; }
            public bool IsSystem { get; }
            public uint Pid { get; }
            /// <summary>Faderly's own session (never shown).</summary>
            public bool Own { get; }
        }

        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _slowTimer;
        private readonly DispatcherTimer _meterTimer;
        private readonly DispatcherTimer _routeRefreshTimer;
        private readonly AppSettings _settings;
        private readonly uint _ownPid = (uint)Environment.ProcessId;

        // Every active output device stays open so sessions on all of them can be shown.
        private readonly Dictionary<string, MMDevice> _render = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SessionEntry> _sessionCache = new(StringComparer.Ordinal);
        // All app rows, including hidden ones (so hotkeys still work for hidden apps).
        private readonly Dictionary<string, AppSessionGroup> _rows = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

        private MMDevice? _device;
        private string? _defaultId;
        private AudioEndpointVolume? _endpointVolume;
        private string _deviceName = "No output device";
        private string _deviceGlyph = "";
        private double _masterVolume;
        private bool _masterMuted;
        private double _masterPeak;
        private bool _syncingMaster;
        private int _masterSyncQueued;

        private MMDevice? _mic;
        private string? _micId;
        private AudioEndpointVolume? _micVolumeEndpoint;
        private string _micName = "No microphone";
        private string _micGlyph = "";
        private double _micVolume;
        private bool _micMuted;
        private double _micPeak;
        private bool _syncingMic;
        private int _micSyncQueued;
        private string _micStatus = "";

        private bool _isDeviceListOpen;
        private bool _isMicListOpen;
        private bool _isSettingsOpen;
        private volatile bool _isWindowVisible;
        private bool _hasApps;
        private string _hotkeyWarning = "";
        private DateTime _lastSessionRefreshUtc = DateTime.MinValue;
        private DateTime _lastDeviceRefreshUtc = DateTime.MinValue;
        private string? _lastMissingKey;
        private DateTime _lastMissingUtc = DateTime.MinValue;

        private AppSessionGroup? _expandedRow;
        private bool _expandedCanRoute;
        private string _expandedRouteError = "";

        public event EventHandler? QuitRequested;

        /// <summary>Raised when switching between tray popup and app window.</summary>
        public event EventHandler? WindowModeChanged;

        /// <summary>Raised whenever any hotkey setting changes, so they can be re-registered.</summary>
        public event EventHandler? HotkeysChanged;

        public MixerViewModel(AppSettings settings, UpdateService updater)
        {
            _settings = settings;
            _dispatcher = Dispatcher.CurrentDispatcher;
            Updater = updater;
            foreach (var h in settings.HiddenApps) _hidden.Add(h.Key);

            ToggleMasterMuteCommand = new RelayCommand(() => MasterMuted = !MasterMuted);
            ToggleDeviceListCommand = new RelayCommand(() => { IsMicListOpen = false; IsDeviceListOpen = !IsDeviceListOpen; });
            SelectDeviceCommand = new RelayCommand(p => SelectDevice(p as DeviceItem));
            ToggleMicMuteCommand = new RelayCommand(() => MicMuted = !MicMuted);
            ToggleMicListCommand = new RelayCommand(() => { IsDeviceListOpen = false; IsMicListOpen = !IsMicListOpen; });
            SelectMicCommand = new RelayCommand(p => SelectMic(p as DeviceItem));
            OpenSettingsCommand = new RelayCommand(() => { IsDeviceListOpen = false; IsMicListOpen = false; IsSettingsOpen = true; });
            CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
            CheckUpdatesCommand = new RelayCommand(async () => await Updater.CheckAsync(userInitiated: true));
            RestartToUpdateCommand = new RelayCommand(() => Updater.RestartToUpdate());
            OpenGitHubCommand = new RelayCommand(() => OpenUri(UpdateService.RepoUrl));
            OpenSoundSettingsCommand = new RelayCommand(() => OpenUri("ms-settings:sound"));
            QuitCommand = new RelayCommand(() => QuitRequested?.Invoke(this, EventArgs.Empty));
            ToggleWindowModeCommand = new RelayCommand(() => IsWindowMode = !IsWindowMode);
            SelectThemeCommand = new RelayCommand(p => SelectTheme(p as ThemeOption));
            ToggleExpandCommand = new RelayCommand(p => ToggleExpanded(p as AppSessionGroup));
            HideAppCommand = new RelayCommand(p => HideApp(p as AppSessionGroup));
            UnhideAppCommand = new RelayCommand(p => UnhideApp(p as HiddenAppItem));
            SelectAppDeviceCommand = new RelayCommand(p => SelectAppDevice(p as DeviceOption));
            RemoveAppHotkeysCommand = new RelayCommand(p => RemoveAppHotkeys(p as AppHotkeyItem));
            SetActiveModifierCommand = new RelayCommand(p => ActiveAppModifier = p as string ?? "Ctrl");

            Themes.Add(new ThemeOption(ThemeManager.SystemKey, "System"));
            foreach (var t in ThemeManager.Themes)
                Themes.Add(new ThemeOption(t.Key, t.Name));
            MarkSelectedTheme();
            RebuildHiddenList();
            RebuildAppHotkeyList();

            // Both timers only run while the mixer is on screen (see SetWindowVisible).
            _slowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _slowTimer.Tick += (_, _) => Refresh();

            _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _meterTimer.Tick += (_, _) => UpdateMeters();

            // After moving an app to another device, its session reappears there a moment later.
            _routeRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _routeRefreshTimer.Tick += (_, _) =>
            {
                _routeRefreshTimer.Stop();
                RefreshSessions();
                RefreshExpandedRoute();
            };

            // Warm up once at startup (devices, sessions, icons) so the first open is instant.
            Refresh();
        }

        // ================= Bindable state =================

        public UpdateService Updater { get; }
        public ObservableCollection<AppSessionGroup> Apps { get; } = new();
        public ObservableCollection<DeviceItem> Devices { get; } = new();
        public ObservableCollection<DeviceItem> MicDevices { get; } = new();
        public ObservableCollection<ThemeOption> Themes { get; } = new();
        public ObservableCollection<HiddenAppItem> HiddenApps { get; } = new();
        public ObservableCollection<AppHotkeyItem> AppHotkeyList { get; } = new();
        public ObservableCollection<DeviceOption> ExpandedDeviceOptions { get; } = new();

        public AppSettings Settings => _settings;

        public bool HasApps
        {
            get => _hasApps;
            private set => SetProperty(ref _hasApps, value);
        }

        public bool HasHiddenApps => HiddenApps.Count > 0;
        public bool HasAppHotkeys => AppHotkeyList.Count > 0;

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

        // ----- Output -----

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
        public string MasterGlyph => SpeakerGlyph(_masterVolume, _masterMuted);

        private static string SpeakerGlyph(double volume, bool muted)
        {
            if (muted || volume <= 0) return "";
            if (volume < 34) return "";
            if (volume < 67) return "";
            return "";
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

        // ----- Microphone -----

        public bool ShowMicrophone
        {
            get => _settings.ShowMicrophone;
            set
            {
                if (_settings.ShowMicrophone == value) return;
                _settings.ShowMicrophone = value;
                SettingsStore.Save(_settings);
                OnPropertyChanged();
                OnPropertyChanged(nameof(MicVisible));
                if (!value) IsMicListOpen = false;
            }
        }

        public bool HasMic => _mic != null;
        public bool MicVisible => ShowMicrophone && HasMic;

        public string MicName
        {
            get => _micName;
            private set => SetProperty(ref _micName, value);
        }

        public string MicDeviceGlyph
        {
            get => _micGlyph;
            private set => SetProperty(ref _micGlyph, value);
        }

        public double MicVolume
        {
            get => _micVolume;
            set
            {
                value = Math.Clamp(Math.Round(value), 0, 100);
                if (!SetProperty(ref _micVolume, value)) return;
                OnPropertyChanged(nameof(MicVolumeText));
                if (_syncingMic || _micVolumeEndpoint == null) return;
                try
                {
                    if (_micMuted) MicMuted = false;
                    _micVolumeEndpoint.MasterVolumeLevelScalar = (float)(value / 100.0);
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("Set mic volume", ex);
                }
            }
        }

        public string MicVolumeText => $"{_micVolume:0}";

        public bool MicMuted
        {
            get => _micMuted;
            set
            {
                if (!SetProperty(ref _micMuted, value)) return;
                OnPropertyChanged(nameof(MicGlyph));
                if (_syncingMic || _micVolumeEndpoint == null) return;
                try { _micVolumeEndpoint.Mute = value; }
                catch (Exception ex) { Log.WarnOnce("Set mic mute", ex); }
            }
        }

        public string MicGlyph => _micMuted ? "" : "";

        public double MicPeak
        {
            get => _micPeak;
            private set => SetProperty(ref _micPeak, value);
        }

        /// <summary>"In use by Discord" / "Not in use".</summary>
        public string MicStatus
        {
            get => _micStatus;
            private set
            {
                if (SetProperty(ref _micStatus, value)) OnPropertyChanged(nameof(MicSubtitle));
            }
        }

        public string MicSubtitle => _micStatus.Length == 0 ? "Input" : "Input  ·  " + _micStatus;

        public bool IsMicListOpen
        {
            get => _isMicListOpen;
            set => SetProperty(ref _isMicListOpen, value);
        }

        // ----- Pages / settings -----

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
                UpdateVisibleRows();
            }
        }

        public bool TrayScrollVolume
        {
            get => _settings.TrayScrollVolume;
            set
            {
                if (_settings.TrayScrollVolume == value) return;
                _settings.TrayScrollVolume = value;
                SettingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }

        // ----- Hotkey settings -----

        public HotkeyBinding? ToggleHotkey
        {
            get => _settings.ToggleHotkey;
            set
            {
                _settings.ToggleHotkey = value;
                HotkeySettingChanged();
            }
        }

        public HotkeyBinding? MicMuteHotkey
        {
            get => _settings.MicMuteHotkey;
            set
            {
                _settings.MicMuteHotkey = value;
                HotkeySettingChanged();
            }
        }

        public bool ActiveAppVolumeKeys
        {
            get => _settings.ActiveAppVolumeKeys;
            set
            {
                if (_settings.ActiveAppVolumeKeys == value) return;
                _settings.ActiveAppVolumeKeys = value;
                HotkeySettingChanged();
            }
        }

        public string ActiveAppModifier
        {
            get => _settings.ActiveAppModifier;
            set
            {
                if (value is not ("Ctrl" or "Shift" or "Alt") || _settings.ActiveAppModifier == value) return;
                _settings.ActiveAppModifier = value;
                HotkeySettingChanged();
            }
        }

        public bool IsModCtrl => ActiveAppModifier == "Ctrl";
        public bool IsModShift => ActiveAppModifier == "Shift";
        public bool IsModAlt => ActiveAppModifier == "Alt";

        public int ActiveAppModifierBits => ActiveAppModifier switch
        {
            "Shift" => HotkeyBinding.Shift,
            "Alt" => HotkeyBinding.Alt,
            _ => HotkeyBinding.Ctrl
        };

        public string HotkeyWarning
        {
            get => _hotkeyWarning;
            private set
            {
                if (SetProperty(ref _hotkeyWarning, value)) OnPropertyChanged(nameof(HasHotkeyWarning));
            }
        }

        public bool HasHotkeyWarning => _hotkeyWarning.Length > 0;

        public void SetHotkeyFailures(IReadOnlyList<string> failed, IReadOnlyList<string> duplicates)
        {
            var parts = new List<string>();
            if (duplicates.Count > 0) parts.Add("Used twice in Faderly: " + string.Join(", ", duplicates.Distinct()));
            if (failed.Count > 0) parts.Add("Already used by another app: " + string.Join(", ", failed.Distinct()));
            HotkeyWarning = string.Join("\n", parts);
        }

        private void HotkeySettingChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            SettingsStore.Save(_settings);
            OnPropertyChanged(name);
            OnPropertyChanged(nameof(IsModCtrl));
            OnPropertyChanged(nameof(IsModShift));
            OnPropertyChanged(nameof(IsModAlt));
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }

        // ----- Expanded app panel -----

        public AppSessionGroup? ExpandedRow
        {
            get => _expandedRow;
            private set => SetProperty(ref _expandedRow, value);
        }

        public bool ExpandedCanRoute
        {
            get => _expandedCanRoute;
            private set => SetProperty(ref _expandedCanRoute, value);
        }

        public string ExpandedRouteError
        {
            get => _expandedRouteError;
            private set => SetProperty(ref _expandedRouteError, value);
        }

        // ================= Commands =================

        public ICommand ToggleMasterMuteCommand { get; }
        public ICommand ToggleDeviceListCommand { get; }
        public ICommand SelectDeviceCommand { get; }
        public ICommand ToggleMicMuteCommand { get; }
        public ICommand ToggleMicListCommand { get; }
        public ICommand SelectMicCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand CloseSettingsCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand RestartToUpdateCommand { get; }
        public ICommand OpenGitHubCommand { get; }
        public ICommand OpenSoundSettingsCommand { get; }
        public ICommand QuitCommand { get; }
        public ICommand ToggleWindowModeCommand { get; }
        public ICommand SelectThemeCommand { get; }
        public ICommand ToggleExpandCommand { get; }
        public ICommand HideAppCommand { get; }
        public ICommand UnhideAppCommand { get; }
        public ICommand SelectAppDeviceCommand { get; }
        public ICommand RemoveAppHotkeysCommand { get; }
        public ICommand SetActiveModifierCommand { get; }

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

        // ================= Visibility drives all background work =================

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
                IsMicListOpen = false;
                if (!IsWindowMode)
                {
                    IsSettingsOpen = false;
                    Collapse();
                }

                // Let meters rest at zero so they don't flash old levels on the next open.
                MasterPeak = 0;
                MicPeak = 0;
                foreach (var app in Apps) app.ResetPeak();
            }
        }

        // ================= Devices =================

        private void Refresh()
        {
            RefreshDevices();
            SyncMaster();
            RefreshMic();
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

            // Keep one open MMDevice per active output; drop ones that went away.
            var ids = new List<string>();
            foreach (var d in active)
            {
                string id;
                try { id = d.ID; }
                catch { d.Dispose(); continue; }

                ids.Add(id);
                if (_render.ContainsKey(id)) d.Dispose();
                else _render[id] = d;
            }
            foreach (var gone in _render.Keys.Where(k => !ids.Contains(k)).ToList())
                DropDevice(gone);

            if (!ids.SequenceEqual(Devices.Select(d => d.Id)))
            {
                Devices.Clear();
                foreach (var id in ids)
                {
                    string name;
                    try { name = _render[id].FriendlyName; } catch { name = "Audio device"; }
                    Devices.Add(new DeviceItem(id, name));
                }
            }
            foreach (var item in Devices)
                item.IsDefault = item.Id == defaultId;

            if (defaultId != _defaultId || (defaultId != null && _device == null))
                SetDefaultDevice(defaultId);

            RefreshMicDevice();
        }

        private void SetDefaultDevice(string? id)
        {
            if (_endpointVolume != null)
            {
                try { _endpointVolume.OnVolumeNotification -= OnEndpointVolumeChanged; } catch { }
            }
            _endpointVolume = null;
            _device = null;
            _defaultId = id;

            if (id != null && _render.TryGetValue(id, out var device))
            {
                try
                {
                    _endpointVolume = device.AudioEndpointVolume;
                    // Instant updates when the volume changes elsewhere (keyboard keys, knobs, Windows flyout).
                    _endpointVolume.OnVolumeNotification += OnEndpointVolumeChanged;
                    _device = device;
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("Opening device", ex);
                    _endpointVolume = null;
                }
            }

            var item = Devices.FirstOrDefault(d => d.Id == id);
            DeviceName = _device != null ? item?.Name ?? "Audio device" : "No output device";
            DeviceGlyph = item?.Glyph ?? "";
            OnPropertyChanged(nameof(HasDevice));
            SyncMaster();
        }

        /// <summary>An output device disappeared: forget its sessions and release it.</summary>
        private void DropDevice(string id)
        {
            foreach (var key in _sessionCache.Where(kv => kv.Value.DeviceId == id).Select(kv => kv.Key).ToList())
            {
                try { _sessionCache[key].Control.Dispose(); } catch { }
                _sessionCache.Remove(key);
            }
            if (_render.TryGetValue(id, out var device))
            {
                if (ReferenceEquals(device, _device)) SetDefaultDevice(null);
                try { device.Dispose(); } catch { }
                _render.Remove(id);
            }
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
            {
                RefreshDevices();
                RefreshSessions();
            }
            else
            {
                Log.Info("Couldn't switch default device to " + item.Name);
            }
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

        // ================= Microphone =================

        private void RefreshMicDevice()
        {
            var active = new List<MMDevice>();
            string? defaultId = null;
            try
            {
                foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    active.Add(d);
                if (active.Count > 0)
                {
                    using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                    defaultId = def.ID;
                }

                var ids = active.Select(d => d.ID).ToList();
                if (!ids.SequenceEqual(MicDevices.Select(d => d.Id)))
                {
                    MicDevices.Clear();
                    foreach (var d in active)
                    {
                        string name;
                        try { name = d.FriendlyName; } catch { name = "Microphone"; }
                        MicDevices.Add(new DeviceItem(d.ID, name, isCapture: true));
                    }
                }
                foreach (var item in MicDevices)
                    item.IsDefault = item.Id == defaultId;
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Enumerating microphones", ex);
                return;
            }
            finally
            {
                foreach (var d in active) d.Dispose();
            }

            if (defaultId != _micId || (defaultId != null && _mic == null))
                SetMic(defaultId);
        }

        private void SetMic(string? id)
        {
            if (_micVolumeEndpoint != null)
            {
                try { _micVolumeEndpoint.OnVolumeNotification -= OnMicVolumeChanged; } catch { }
            }
            _micVolumeEndpoint = null;
            try { _mic?.Dispose(); } catch { }
            _mic = null;
            _micId = id;

            if (id != null)
            {
                try
                {
                    _mic = _enumerator.GetDevice(id);
                    _micVolumeEndpoint = _mic.AudioEndpointVolume;
                    _micVolumeEndpoint.OnVolumeNotification += OnMicVolumeChanged;
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("Opening microphone", ex);
                    try { _mic?.Dispose(); } catch { }
                    _mic = null;
                    _micVolumeEndpoint = null;
                }
            }

            var item = MicDevices.FirstOrDefault(d => d.Id == id);
            MicName = _mic != null ? item?.Name ?? "Microphone" : "No microphone";
            MicDeviceGlyph = item?.Glyph ?? "";
            OnPropertyChanged(nameof(HasMic));
            OnPropertyChanged(nameof(MicVisible));
            SyncMic();
        }

        private void OnMicVolumeChanged(AudioVolumeNotificationData data)
        {
            if (!_isWindowVisible) return;
            if (Interlocked.Exchange(ref _micSyncQueued, 1) == 1) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                Interlocked.Exchange(ref _micSyncQueued, 0);
                if (_isWindowVisible) SyncMic();
            }));
        }

        private void SyncMic()
        {
            if (_micVolumeEndpoint == null) return;
            try
            {
                _syncingMic = true;
                MicVolume = _micVolumeEndpoint.MasterVolumeLevelScalar * 100.0;
                MicMuted = _micVolumeEndpoint.Mute;
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Reading mic volume", ex);
            }
            finally
            {
                _syncingMic = false;
            }
        }

        /// <summary>Mic volume plus which apps are capturing right now (only while visible).</summary>
        private void RefreshMic()
        {
            SyncMic();
            if (!_isWindowVisible || !ShowMicrophone || _mic == null)
            {
                MicStatus = "";
                return;
            }

            var users = new List<string>();
            try
            {
                var manager = _mic.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                int count = sessions?.Count ?? 0;
                for (int i = 0; i < count; i++)
                {
                    AudioSessionControl? s = null;
                    try
                    {
                        s = sessions![i];
                        if (s.State != AudioSessionState.AudioSessionStateActive || s.IsSystemSoundsSession) continue;
                        uint pid = s.GetProcessID;
                        if (pid == 0 || pid == _ownPid) continue;
                        string name = AppInfoResolver.Resolve(pid, s.DisplayName).Name;
                        if (!users.Contains(name)) users.Add(name);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { s?.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Reading mic sessions", ex);
            }

            MicStatus = users.Count == 0 ? "Not in use" : "In use by " + string.Join(", ", users);
        }

        private void SelectMic(DeviceItem? item)
        {
            IsMicListOpen = false;
            if (item == null || item.IsDefault) return;
            if (Native.SetDefaultAudioDevice(item.Id))
            {
                RefreshMicDevice();
                RefreshMic();
            }
            else
            {
                Log.Info("Couldn't switch default microphone to " + item.Name);
            }
        }

        // ================= App sessions =================

        private void RefreshSessions()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var groups = new Dictionary<string, List<SessionEntry>>(StringComparer.Ordinal);
            var order = new List<string>();
            bool partial = false;

            foreach (var (deviceId, device) in _render)
            {
                try
                {
                    var manager = device.AudioSessionManager;
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
                                entry = Identify(fresh, deviceId);
                                if (id.Length > 0) _sessionCache[id] = entry;
                            }
                            fresh = null;
                            if (id.Length > 0) seen.Add(id);

                            if (entry.Own) continue;
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
                    // Device busy or going away: keep what we had for it this round.
                    Log.WarnOnce("Reading sessions", ex);
                    partial = true;
                }
            }

            if (!partial)
            {
                // Forget sessions that no longer exist.
                foreach (var stale in _sessionCache.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    try { _sessionCache[stale].Control.Dispose(); } catch { }
                    _sessionCache.Remove(stale);
                }

                foreach (var gone in _rows.Keys.Where(k => !groups.ContainsKey(k)).ToList())
                    _rows.Remove(gone);
            }

            foreach (var key in order)
            {
                var entries = groups[key];
                // Prefer the session that's actually playing, then the one on the default device,
                // so the row reads/writes the right volume after an app moved between devices.
                entries.Sort((a, b) => Rank(b).CompareTo(Rank(a)));
                var controls = entries.Select(e => e.Control).ToList();
                var pids = entries.Select(e => e.Pid).Where(p => p != 0).Distinct().ToList();

                if (!_rows.TryGetValue(key, out var row))
                {
                    var first = entries[0];
                    row = new AppSessionGroup(this, key, first.Name, first.Icon, first.IsSystem);
                    _rows[key] = row;
                }
                row.SetSessions(controls, pids);
                row.SyncFromSystem();

                // Show which device the app plays through when none of its audio is on the default one.
                string deviceId = entries[0].DeviceId;
                row.DeviceLabel = row.IsSystem || entries.Any(e => e.DeviceId == _defaultId)
                    ? ""
                    : Devices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? "";
            }

            UpdateVisibleRows();
            _lastSessionRefreshUtc = DateTime.UtcNow;
        }

        private int Rank(SessionEntry e)
        {
            int rank = e.DeviceId == _defaultId ? 1 : 0;
            try
            {
                if (e.Control.State == AudioSessionState.AudioSessionStateActive) rank += 2;
            }
            catch
            {
            }
            return rank;
        }

        /// <summary>Sync the on-screen list with the rows, leaving out hidden apps.</summary>
        private void UpdateVisibleRows()
        {
            for (int i = Apps.Count - 1; i >= 0; i--)
            {
                var row = Apps[i];
                if (!_rows.TryGetValue(row.Key, out var current) || !ReferenceEquals(current, row) || IsHidden(row))
                    Apps.RemoveAt(i);
            }

            foreach (var row in _rows.Values)
            {
                if (!IsHidden(row) && !Apps.Contains(row))
                    Apps.Insert(SortedIndex(row), row);
            }

            HasApps = Apps.Count > 0;
            if (_expandedRow != null && !Apps.Contains(_expandedRow)) Collapse();
        }

        private bool IsHidden(AppSessionGroup row) =>
            row.IsSystem ? !_settings.ShowSystemSounds || _hidden.Contains(row.Key) : _hidden.Contains(row.Key);

        private SessionEntry Identify(AudioSessionControl s, string deviceId)
        {
            bool isSystem = s.IsSystemSoundsSession;
            uint pid = s.GetProcessID;

            if (isSystem)
                return new SessionEntry(s, deviceId, SystemKey, "System sounds", null, true, pid, own: false);
            if (pid == _ownPid)
                return new SessionEntry(s, deviceId, "", "", null, false, pid, own: true);

            var info = AppInfoResolver.Resolve(pid, s.DisplayName);
            return new SessionEntry(s, deviceId, info.Key, info.Name, info.Icon, false, pid, own: false);
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
                MasterPeak = Decay(_masterPeak, p);
            }

            var mic = _mic;
            if (mic != null && ShowMicrophone)
            {
                double p = 0;
                if (!_micMuted)
                {
                    try { p = mic.AudioMeterInformation.MasterPeakValue; }
                    catch { }
                }
                MicPeak = Decay(_micPeak, p);
            }

            foreach (var app in Apps)
                app.UpdatePeak();
        }

        /// <summary>Fast attack, smooth release; tiny values snap to 0 so idle meters stop redrawing.</summary>
        private static double Decay(double current, double target)
        {
            double next = target >= current ? target : Math.Max(target, current * 0.82);
            return next < 0.004 ? 0 : next;
        }

        // ================= Hide / unhide =================

        private void HideApp(AppSessionGroup? row)
        {
            if (row == null) return;
            if (_hidden.Add(row.Key))
            {
                _settings.HiddenApps.Add(new HiddenApp { Key = row.Key, Name = row.Name });
                SettingsStore.Save(_settings);
                RebuildHiddenList();
            }
            UpdateVisibleRows();
        }

        private void UnhideApp(HiddenAppItem? item)
        {
            if (item == null) return;
            _hidden.Remove(item.Key);
            _settings.HiddenApps.RemoveAll(h => string.Equals(h.Key, item.Key, StringComparison.OrdinalIgnoreCase));
            SettingsStore.Save(_settings);
            RebuildHiddenList();
            UpdateVisibleRows();
        }

        private void RebuildHiddenList()
        {
            HiddenApps.Clear();
            foreach (var h in _settings.HiddenApps.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase))
                HiddenApps.Add(new HiddenAppItem(h.Key, string.IsNullOrEmpty(h.Name) ? h.Key : h.Name));
            OnPropertyChanged(nameof(HasHiddenApps));
        }

        // ================= Expanded app panel (device + hotkeys + hide) =================

        private void ToggleExpanded(AppSessionGroup? row)
        {
            if (row == null) return;
            if (ReferenceEquals(_expandedRow, row))
            {
                Collapse();
                return;
            }
            Collapse();
            row.IsExpanded = true;
            ExpandedRow = row;
            ExpandedRouteError = "";
            RefreshExpandedRoute();
        }

        public void Collapse()
        {
            if (_expandedRow != null) _expandedRow.IsExpanded = false;
            ExpandedRow = null;
            ExpandedDeviceOptions.Clear();
            ExpandedCanRoute = false;
        }

        private void RefreshExpandedRoute()
        {
            var row = _expandedRow;
            ExpandedDeviceOptions.Clear();
            ExpandedCanRoute = row != null && row.CanCustomize && row.Pids.Count > 0 && AudioPolicy.IsSupported;
            if (!ExpandedCanRoute || row == null) return;

            string? current = AudioPolicy.GetAppDevice(row.Pids[0]);
            ExpandedDeviceOptions.Add(new DeviceOption(null, "Default (" + DeviceName + ")", "")
            {
                IsSelected = current == null
            });
            foreach (var d in Devices)
            {
                ExpandedDeviceOptions.Add(new DeviceOption(d.Id, d.Name, d.Glyph)
                {
                    IsSelected = current != null && string.Equals(current, d.Id, StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        private void SelectAppDevice(DeviceOption? option)
        {
            var row = _expandedRow;
            if (option == null || row == null) return;

            if (AudioPolicy.SetAppDevice(row.Pids, option.Id))
            {
                foreach (var o in ExpandedDeviceOptions) o.IsSelected = ReferenceEquals(o, option);
                ExpandedRouteError = "";
                _routeRefreshTimer.Stop();
                _routeRefreshTimer.Start();
            }
            else
            {
                ExpandedRouteError = "Windows didn't accept that. Try again while the app is playing.";
            }
        }

        // ================= Per-app hotkeys =================

        internal HotkeyBinding? GetAppHotkey(string key, AppHotkeyKind kind)
        {
            var entry = _settings.AppHotkeys.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
            return entry == null ? null : kind switch
            {
                AppHotkeyKind.Up => entry.Up,
                AppHotkeyKind.Down => entry.Down,
                _ => entry.Mute
            };
        }

        internal void SetAppHotkey(AppSessionGroup row, AppHotkeyKind kind, HotkeyBinding? value)
        {
            var entry = _settings.AppHotkeys.FirstOrDefault(a => string.Equals(a.Key, row.Key, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                if (value == null) return;
                entry = new AppHotkeySettings { Key = row.Key, Name = row.Name };
                _settings.AppHotkeys.Add(entry);
            }

            switch (kind)
            {
                case AppHotkeyKind.Up: entry.Up = value; break;
                case AppHotkeyKind.Down: entry.Down = value; break;
                default: entry.Mute = value; break;
            }
            if (entry.IsEmpty) _settings.AppHotkeys.Remove(entry);

            SettingsStore.Save(_settings);
            row.NotifyHotkeysChanged();
            RebuildAppHotkeyList();
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RemoveAppHotkeys(AppHotkeyItem? item)
        {
            if (item == null) return;
            _settings.AppHotkeys.RemoveAll(a => string.Equals(a.Key, item.Key, StringComparison.OrdinalIgnoreCase));
            SettingsStore.Save(_settings);
            if (_rows.TryGetValue(item.Key, out var row)) row.NotifyHotkeysChanged();
            RebuildAppHotkeyList();
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RebuildAppHotkeyList()
        {
            AppHotkeyList.Clear();
            foreach (var a in _settings.AppHotkeys.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var parts = new List<string>();
                if (a.Up != null) parts.Add("Up: " + HotkeyManager.Format(a.Up));
                if (a.Down != null) parts.Add("Down: " + HotkeyManager.Format(a.Down));
                if (a.Mute != null) parts.Add("Mute: " + HotkeyManager.Format(a.Mute));
                AppHotkeyList.Add(new AppHotkeyItem(a.Key, a.Name, string.Join("   ", parts)));
            }
            OnPropertyChanged(nameof(HasAppHotkeys));
        }

        // ================= Hotkey / knob / tray-wheel actions =================
        // Each returns what the on-screen indicator should show (or null for nothing).

        /// <summary>
        /// Make sure the session list is recent enough to act on. Throttled, so holding a hotkey
        /// (30 repeats a second) doesn't re-scan audio devices on every repeat.
        /// </summary>
        private void EnsureFreshSessions(string? key = null)
        {
            var now = DateTime.UtcNow;
            bool stale = (now - _lastSessionRefreshUtc).TotalMilliseconds > 1500;
            bool missing = key != null && !_rows.ContainsKey(key);
            if (missing && key == _lastMissingKey && (now - _lastMissingUtc).TotalMilliseconds < 1500)
                missing = false; // just looked for it; it's still not playing
            if (stale || missing)
            {
                EnsureFreshDevices(force: true);
                RefreshSessions();
            }
            if (key != null && !_rows.ContainsKey(key))
            {
                _lastMissingKey = key;
                _lastMissingUtc = now;
            }
        }

        /// <summary>Pick up a default output/mic changed in Windows while the mixer was hidden.</summary>
        private void EnsureFreshDevices(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && !_isWindowVisible && (now - _lastDeviceRefreshUtc).TotalMilliseconds < 1500) return;
            if (_isWindowVisible && !force) return; // the 1s refresh timer is already keeping up
            _lastDeviceRefreshUtc = now;
            RefreshDevices();
        }

        public VolumeFeedback? AdjustMaster(int notches)
        {
            if (notches == 0) return null;
            EnsureFreshDevices();
            if (_endpointVolume == null) return null;
            SyncMaster();
            bool wasMuted = _masterMuted;
            MasterVolume = _masterVolume + notches * HotkeyStep;
            if (wasMuted && notches < 0) MasterMuted = true; // turning down shouldn't unmute
            return new VolumeFeedback(DeviceName, SpeakerGlyph(_masterVolume, _masterMuted), null, _masterVolume, _masterMuted);
        }

        public VolumeFeedback? ToggleMicMute()
        {
            EnsureFreshDevices();
            if (_micVolumeEndpoint == null)
                return new VolumeFeedback("No microphone", "", null, -1, false, "");
            SyncMic();
            MicMuted = !_micMuted;
            return new VolumeFeedback(_micMuted ? "Microphone muted" : "Microphone on", MicGlyph, null, -1, _micMuted, "");
        }

        public VolumeFeedback? AdjustApp(string key, int delta) => ActOnApp(key, null, row => row.Nudge(delta));

        public VolumeFeedback? ToggleAppMute(string key) => ActOnApp(key, null, row => row.ToggleMuteFromHotkey());

        public VolumeFeedback? AdjustActiveApp(int delta) => ActOnActiveApp(row => row.Nudge(delta));

        public VolumeFeedback? ToggleActiveAppMute() => ActOnActiveApp(row => row.ToggleMuteFromHotkey());

        private VolumeFeedback? ActOnActiveApp(Action<AppSessionGroup> action)
        {
            uint pid = Native.GetForegroundProcessId();
            if (pid == 0 || pid == _ownPid) return null;
            string? path = Native.GetProcessPath(pid);
            if (path == null) return null;
            return ActOnApp(path.ToLowerInvariant(), pid, action);
        }

        private VolumeFeedback? ActOnApp(string key, uint? pidForName, Action<AppSessionGroup> action)
        {
            EnsureFreshSessions(key);
            if (!_rows.TryGetValue(key, out var row))
            {
                string name = pidForName is uint pid
                    ? AppInfoResolver.Resolve(pid, null).Name
                    : _settings.AppHotkeys.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))?.Name ?? "This app";
                return new VolumeFeedback(name, "", null, -1, false, "Not playing audio");
            }

            action(row);
            return new VolumeFeedback(row.Name, SpeakerGlyph(row.Volume, row.IsMuted), row.Icon, row.Volume, row.IsMuted);
        }

        // ================= Misc =================

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
            _routeRefreshTimer.Stop();

            if (_endpointVolume != null)
            {
                try { _endpointVolume.OnVolumeNotification -= OnEndpointVolumeChanged; } catch { }
            }
            if (_micVolumeEndpoint != null)
            {
                try { _micVolumeEndpoint.OnVolumeNotification -= OnMicVolumeChanged; } catch { }
            }
            foreach (var entry in _sessionCache.Values)
            {
                try { entry.Control.Dispose(); } catch { }
            }
            _sessionCache.Clear();
            foreach (var d in _render.Values)
            {
                try { d.Dispose(); } catch { }
            }
            _render.Clear();
            try { _mic?.Dispose(); } catch { }
            try { _enumerator.Dispose(); } catch { }
        }
    }
}
