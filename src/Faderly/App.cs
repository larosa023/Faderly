using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Faderly.Controls;
using Faderly.Services;
using Faderly.ViewModels;

namespace Faderly
{
    public sealed class App : Application
    {
        private readonly bool _startHidden;
        private readonly bool _justUpdated;
        private TrayIcon? _tray;
        private HotkeyManager? _hotkeys;
        private OsdWindow? _osd;
        private MainWindow? _window;
        private MixerViewModel? _vm;
        private UpdateService? _updater;
        private EventWaitHandle? _showSignal;
        private RegisteredWaitHandle? _showWait;
        private bool _cleanedUp;

        public App(bool startHidden, bool justUpdated)
        {
            _startHidden = startHidden;
            _justUpdated = justUpdated;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                // Keep the tray app alive on unexpected UI errors, but record them.
                Log.Error("Unhandled UI exception", args.Exception);
                args.Handled = true;
            };

            try
            {
                Start();
            }
            catch (Exception ex)
            {
                Log.Error("Startup failed", ex);
                Cleanup();
                _ = RescueAsync(ex);
            }
        }

        private void Start()
        {
            var settings = SettingsStore.Load();
            ThemeManager.Apply(Resources, settings.Theme);
            StartupManager.Apply(settings.StartWithWindows);

            _updater = new UpdateService();
            _vm = new MixerViewModel(settings, _updater);
            _vm.QuitRequested += (_, _) => Quit();

            _window = new MainWindow(_vm);
            _window.InitializeNative();

            var window = _window;
            CreateTray();

            var vm = _vm;
            _hotkeys = new HotkeyManager();
            _hotkeys.FailuresChanged += (_, _) => vm.SetHotkeyFailures(
                _hotkeys?.Failed ?? Array.Empty<string>(), _hotkeys?.Duplicates ?? Array.Empty<string>());
            vm.HotkeysChanged += (_, _) => ApplyHotkeys();
            // While a hotkey box is listening, release global hotkeys so the keys reach it.
            HotkeyRecorder.RecordingStarted += OnRecordingStarted;
            HotkeyRecorder.RecordingStopped += OnRecordingStopped;
            ApplyHotkeys();

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MixerViewModel.TrayScrollVolume) && _tray != null)
                    _tray.ScrollEnabled = vm.TrayScrollVolume;
            };

            var updater = _updater;
            updater.UpdateDownloaded += (_, _) => _tray?.ShowBalloon(
                "Faderly update ready",
                $"Version {updater.AvailableVersion} is downloaded. Open Faderly and click Restart to update.");
            // Remove the tray icon before the updater exits the process (no ghost icon left behind);
            // put it back if the update couldn't be applied.
            updater.Restarting += (_, _) =>
            {
                try { _window?.SaveState(); } catch { }
                _tray?.Dispose();
                _tray = null;
            };
            updater.RestartFailed += (_, _) => { if (_tray == null) CreateTray(); };
            updater.StartAutomaticChecks(settings.AutoCheckUpdates);

            // A second launch signals us to show the window.
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSignalName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => _window?.ShowMixer())), null, Timeout.Infinite, false);

            if (_justUpdated)
                _tray?.ShowBalloon("Faderly updated", $"You're now on version {updater.CurrentVersion}.");

            if (!_startHidden)
                window.ShowMixer();

            Log.Info($"Started v{updater.CurrentVersion} (hidden: {_startHidden}, window mode: {settings.WindowMode})");
        }

        private void CreateTray()
        {
            var window = _window;
            if (window == null) return;
            _tray = new TrayIcon("Faderly");
            var tray = _tray;
            tray.LeftButtonDown += (_, _) => window.OnTrayButtonDown();
            tray.LeftClick += (_, _) => window.ToggleMixer();
            tray.RightClick += (_, _) => window.ShowTrayMenu(tray.Handle);
            _tray.BalloonClick += (_, _) => window.ShowMixer();
            tray.ScrollEnabled = _vm?.TrayScrollVolume ?? true;
            tray.Scroll += (_, notches) =>
            {
                if (_vm is { TrayScrollVolume: true } vm) ShowFeedback(vm.AdjustMaster(notches));
            };
        }

        private void OnRecordingStarted(object? sender, EventArgs e) => _hotkeys?.Suspend();

        private void OnRecordingStopped(object? sender, EventArgs e) => _hotkeys?.Resume();

        /// <summary>(Re)register every global hotkey from the current settings.</summary>
        private void ApplyHotkeys()
        {
            var vm = _vm;
            var window = _window;
            if (vm == null || window == null || _hotkeys == null) return;
            var s = vm.Settings;
            var list = new List<HotkeyRegistration>();

            if (s.ToggleHotkey != null)
                list.Add(new HotkeyRegistration("Open Faderly", s.ToggleHotkey, false, window.ToggleFromHotkey));

            if (s.MicMuteHotkey != null)
                list.Add(new HotkeyRegistration("Mute microphone", s.MicMuteHotkey, false,
                    () => ShowFeedback(vm.ToggleMicMute(), always: true)));

            if (s.ActiveAppVolumeKeys)
            {
                int mod = vm.ActiveAppModifierBits;
                const int VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF;
                list.Add(new HotkeyRegistration($"{s.ActiveAppModifier} + Volume Up", HotkeyBinding.Create(mod, VolumeUp), true,
                    () => ShowFeedback(vm.AdjustActiveApp(+2))));
                list.Add(new HotkeyRegistration($"{s.ActiveAppModifier} + Volume Down", HotkeyBinding.Create(mod, VolumeDown), true,
                    () => ShowFeedback(vm.AdjustActiveApp(-2))));
                list.Add(new HotkeyRegistration($"{s.ActiveAppModifier} + Volume Mute", HotkeyBinding.Create(mod, VolumeMute), false,
                    () => ShowFeedback(vm.ToggleActiveAppMute())));
            }

            foreach (var app in s.AppHotkeys)
            {
                string key = app.Key;
                if (app.Up != null)
                    list.Add(new HotkeyRegistration(app.Name + " up", app.Up, true, () => ShowFeedback(vm.AdjustApp(key, +2))));
                if (app.Down != null)
                    list.Add(new HotkeyRegistration(app.Name + " down", app.Down, true, () => ShowFeedback(vm.AdjustApp(key, -2))));
                if (app.Mute != null)
                    list.Add(new HotkeyRegistration(app.Name + " mute", app.Mute, false, () => ShowFeedback(vm.ToggleAppMute(key))));
            }

            _hotkeys.Set(list);
        }

        /// <summary>Show the on-screen indicator, unless the mixer itself is in front (it already shows the change).</summary>
        private void ShowFeedback(VolumeFeedback? feedback, bool always = false)
        {
            if (feedback == null) return;
            if (!always && _window != null && _window.IsInFront) return;
            try
            {
                _osd ??= new OsdWindow();
                _osd.ShowFeedback(feedback);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("On-screen indicator", ex);
            }
        }

        /// <summary>
        /// Last line of defence: if this version can't start, try to pull a newer (fixed) release
        /// before giving up, so a bad build never needs a manual reinstall.
        /// </summary>
        private async Task RescueAsync(Exception error)
        {
            try
            {
                var manager = UpdateService.CreateManager();
                if (manager != null && manager.IsInstalled)
                {
                    var info = await manager.CheckForUpdatesAsync();
                    if (info != null)
                    {
                        Log.Info($"Startup rescue: installing v{info.TargetFullRelease.Version}");
                        await manager.DownloadUpdatesAsync(info);
                        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Startup rescue failed", ex);
            }

            _window?.AllowClose();
            MessageBox.Show(
                $"Faderly couldn't start.\n\n{error.Message}\n\nDetails were saved to:\n{Log.FilePath}",
                "Faderly", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }

        private void Quit()
        {
            _window?.AllowClose();
            Cleanup();
            Shutdown();
        }

        /// <summary>Releases the tray icon, hotkey and audio objects. Safe to call more than once.</summary>
        private void Cleanup()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;

            try { _tray?.Dispose(); } catch { }
            _tray = null;
            HotkeyRecorder.RecordingStarted -= OnRecordingStarted;
            HotkeyRecorder.RecordingStopped -= OnRecordingStopped;
            try { _hotkeys?.Dispose(); } catch { }
            _hotkeys = null;
            try { _osd?.Close(); } catch { }
            _osd = null;
            try { _showWait?.Unregister(null); } catch { }
            try { _showSignal?.Dispose(); } catch { }
            try { _window?.DisposeNative(); } catch { }
            try { _vm?.Dispose(); } catch { }
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            // Signing out / shutting down: let the window close normally.
            _window?.AllowClose();
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Cleanup();
            base.OnExit(e);
        }
    }
}
