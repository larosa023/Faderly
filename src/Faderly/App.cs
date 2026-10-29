using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Faderly.Services;
using Faderly.ViewModels;

namespace Faderly
{
    public sealed class App : Application
    {
        private readonly bool _startHidden;
        private readonly bool _justUpdated;
        private TrayIcon? _tray;
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
