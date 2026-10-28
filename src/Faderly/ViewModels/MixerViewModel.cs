using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Faderly.Services;
using NAudio.CoreAudioApi;

namespace Faderly.ViewModels
{
    public sealed class MixerViewModel : ObservableObject, IDisposable
    {
        private const string SystemKey = "__system__";

        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly DispatcherTimer _slowTimer;
        private readonly DispatcherTimer _meterTimer;
        private readonly AppSettings _settings;
        private readonly uint _ownPid = (uint)Environment.ProcessId;

        private MMDevice? _device;
        private string _deviceName = "No output device";
        private string _deviceGlyph = "";
        private double _masterVolume;
        private bool _masterMuted;
        private double _masterPeak;
        private bool _syncingMaster;
        private bool _isDeviceListOpen;
        private bool _isSettingsOpen;
        private bool _isWindowVisible;

        public event EventHandler? QuitRequested;

        public MixerViewModel(AppSettings settings, UpdateService updater)
        {
            _settings = settings;
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

            _slowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
            _slowTimer.Tick += (_, _) => Refresh();

            _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _meterTimer.Tick += (_, _) => UpdateMeters();

            Refresh();
            _slowTimer.Start();
        }

        // ---------- Bindable state ----------

        public UpdateService Updater { get; }
        public ObservableCollection<AppSessionGroup> Apps { get; } = new();
        public ObservableCollection<DeviceItem> Devices { get; } = new();

        public bool HasApps => Apps.Count > 0;
        public string HotkeyText => "Ctrl + Alt + M";

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
                if (_syncingMaster || _device == null) return;
                try
                {
                    if (_masterMuted) MasterMuted = false;
                    _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(value / 100.0);
                }
                catch
                {
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
                if (_syncingMaster || _device == null) return;
                try { _device.AudioEndpointVolume.Mute = value; }
                catch { }
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

        // ---------- Window visibility drives refresh rate ----------

        public void SetWindowVisible(bool visible)
        {
            _isWindowVisible = visible;
            if (visible)
            {
                Refresh();
                _slowTimer.Interval = TimeSpan.FromMilliseconds(900);
                _meterTimer.Start();
            }
            else
            {
                _meterTimer.Stop();
                _slowTimer.Interval = TimeSpan.FromSeconds(4);
                IsDeviceListOpen = false;
                IsSettingsOpen = false;
            }
        }

        // ---------- Audio plumbing ----------

        private void Refresh()
        {
            RefreshDevices();
            if (_isWindowVisible || Apps.Count == 0)
            {
                SyncMaster();
                RefreshSessions();
            }
        }

        private void RefreshDevices()
        {
            List<MMDevice> active;
            string? defaultId = null;
            try
            {
                active = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                if (active.Count > 0)
                    defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch
            {
                active = new List<MMDevice>();
            }

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

            foreach (var d in active) d.Dispose();

            string? currentId = null;
            try { currentId = _device?.ID; } catch { }

            if (defaultId != currentId)
                SwitchToDevice(defaultId);
        }

        private void SwitchToDevice(string? id)
        {
            try { _device?.Dispose(); } catch { }
            _device = null;
            Apps.Clear();
            OnPropertyChanged(nameof(HasApps));

            if (id != null)
            {
                try { _device = _enumerator.GetDevice(id); }
                catch { _device = null; }
            }

            var item = Devices.FirstOrDefault(d => d.Id == id);
            DeviceName = item?.Name ?? "No output device";
            DeviceGlyph = item?.Glyph ?? "";
            OnPropertyChanged(nameof(HasDevice));

            SyncMaster();
            RefreshSessions();
        }

        private void SelectDevice(DeviceItem? item)
        {
            IsDeviceListOpen = false;
            if (item == null || item.IsDefault) return;
            if (Native.SetDefaultAudioDevice(item.Id))
                RefreshDevices();
        }

        private void SyncMaster()
        {
            if (_device == null) return;
            try
            {
                var ep = _device.AudioEndpointVolume;
                _syncingMaster = true;
                MasterVolume = ep.MasterVolumeLevelScalar * 100.0;
                MasterMuted = ep.Mute;
            }
            catch
            {
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
                if (Apps.Count > 0) { Apps.Clear(); OnPropertyChanged(nameof(HasApps)); }
                return;
            }

            var found = new Dictionary<string, (AppInfo info, bool isSystem, List<AudioSessionControl> sessions)>();

            try
            {
                var manager = _device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;

                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl s;
                    try { s = sessions[i]; } catch { continue; }

                    try
                    {
                        if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                        bool isSystem = s.IsSystemSoundsSession;
                        uint pid = s.GetProcessID;

                        if (isSystem)
                        {
                            if (!_settings.ShowSystemSounds) continue;
                            var sysInfo = new AppInfo(SystemKey, "System sounds", null, null);
                            Add(found, sysInfo, true, s);
                            continue;
                        }

                        if (pid == _ownPid || !Native.IsProcessAlive(pid)) continue;

                        var info = AppInfoResolver.Resolve(pid, s.DisplayName);
                        Add(found, info, false, s);
                    }
                    catch
                    {
                        // Session vanished while we were reading it.
                    }
                }
            }
            catch
            {
                // Device unplugged mid-refresh; RefreshDevices will catch up next tick.
                return;
            }

            // Remove rows whose app is gone.
            for (int i = Apps.Count - 1; i >= 0; i--)
                if (!found.ContainsKey(Apps[i].Key))
                    Apps.RemoveAt(i);

            // Update existing rows and insert new ones in sorted order.
            foreach (var (key, entry) in found)
            {
                var group = Apps.FirstOrDefault(a => a.Key == key);
                if (group == null)
                {
                    group = new AppSessionGroup(key, entry.info.Name, entry.info.Icon, entry.isSystem);
                    group.Sessions.AddRange(entry.sessions);
                    group.SyncFromSystem();
                    Apps.Insert(SortedIndex(group), group);
                }
                else
                {
                    group.Sessions.Clear();
                    group.Sessions.AddRange(entry.sessions);
                    group.SyncFromSystem();
                }
            }

            OnPropertyChanged(nameof(HasApps));
        }

        private static void Add(
            Dictionary<string, (AppInfo info, bool isSystem, List<AudioSessionControl> sessions)> found,
            AppInfo info, bool isSystem, AudioSessionControl session)
        {
            if (!found.TryGetValue(info.Key, out var entry))
            {
                entry = (info, isSystem, new List<AudioSessionControl>());
                found[info.Key] = entry;
            }
            entry.sessions.Add(session);
        }

        /// <summary>Apps alphabetical, System sounds pinned to the bottom.</summary>
        private int SortedIndex(AppSessionGroup group)
        {
            for (int i = 0; i < Apps.Count; i++)
            {
                var other = Apps[i];
                if (group.IsSystem) continue;
                if (other.IsSystem) return i;
                if (string.Compare(group.Name, other.Name, StringComparison.CurrentCultureIgnoreCase) < 0)
                    return i;
            }
            return Apps.Count;
        }

        private void UpdateMeters()
        {
            if (_device != null)
            {
                try
                {
                    double p = _masterMuted ? 0 : _device.AudioMeterInformation.MasterPeakValue;
                    double next = p >= _masterPeak ? p : Math.Max(p, _masterPeak * 0.82);
                    MasterPeak = next < 0.004 ? 0 : next;
                }
                catch
                {
                }
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
            catch
            {
            }
        }

        public void Dispose()
        {
            _slowTimer.Stop();
            _meterTimer.Stop();
            try { _device?.Dispose(); } catch { }
            try { _enumerator.Dispose(); } catch { }
        }
    }
}
