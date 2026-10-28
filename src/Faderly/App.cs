using System;
using System.Threading;
using System.Windows;
using Faderly.Services;
using Faderly.ViewModels;
using WinForms = System.Windows.Forms;

namespace Faderly
{
    public sealed class App : Application
    {
        private readonly bool _startHidden;
        private WinForms.NotifyIcon? _tray;
        private WinForms.ToolStripMenuItem? _startupItem;
        private MainWindow? _window;
        private MixerViewModel? _vm;
        private UpdateService? _updater;
        private EventWaitHandle? _showSignal;
        private RegisteredWaitHandle? _showWait;

        public App(bool startHidden)
        {
            _startHidden = startHidden;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                // Keep the tray app alive on unexpected UI errors.
                args.Handled = true;
            };

            ThemeManager.Apply(Resources);

            var settings = SettingsStore.Load();
            StartupManager.Apply(settings.StartWithWindows);

            _updater = new UpdateService();
            _vm = new MixerViewModel(settings, _updater);
            _vm.QuitRequested += (_, _) => Quit();

            _window = new MainWindow(_vm);
            _window.InitializeHotkey();

            CreateTray(settings);

            _updater.UpdateDownloaded += (_, _) =>
            {
                _tray?.ShowBalloonTip(
                    4000,
                    "Faderly update ready",
                    $"Version {_updater.AvailableVersion} is downloaded. Open Faderly and click Restart to update.",
                    WinForms.ToolTipIcon.Info);
            };
            _updater.Restarting += (_, _) => DisposeTray();
            _updater.StartAutomaticChecks(settings.AutoCheckUpdates);

            // A second launch signals us to show the window.
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSignalName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => _window?.ShowMixer())), null, Timeout.Infinite, false);

            if (!_startHidden)
                _window.ShowMixer();
        }

        private void CreateTray(AppSettings settings)
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open Faderly", null, (_, _) => _window?.ShowMixer());
            menu.Items.Add("Sound settings", null, (_, _) => _vm?.OpenSoundSettingsCommand.Execute(null));
            menu.Items.Add(new WinForms.ToolStripSeparator());

            _startupItem = new WinForms.ToolStripMenuItem("Start with Windows")
            {
                Checked = settings.StartWithWindows,
                CheckOnClick = true
            };
            _startupItem.CheckedChanged += (_, _) =>
            {
                if (_vm != null) _vm.StartWithWindows = _startupItem.Checked;
            };
            menu.Items.Add(_startupItem);
            menu.Items.Add("Check for updates", null, async (_, _) =>
            {
                if (_updater == null) return;
                _window?.ShowMixer();
                if (_vm != null) _vm.IsSettingsOpen = true;
                await _updater.CheckAsync(userInitiated: true);
            });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Quit());

            menu.Opening += (_, _) =>
            {
                if (_vm != null && _startupItem != null) _startupItem.Checked = _vm.StartWithWindows;
            };

            _tray = new WinForms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Faderly",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == WinForms.MouseButtons.Left)
                    _window?.ToggleMixer();
            };
            _tray.BalloonTipClicked += (_, _) => _window?.ShowMixer();
        }

        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var info = GetResourceStream(new Uri("pack://application:,,,/Assets/Faderly.ico"));
                if (info != null)
                {
                    using var stream = info.Stream;
                    // Ask for a small size so Windows picks the crisp 16/20/24px frame.
                    int size = WinForms.SystemInformation.SmallIconSize.Width;
                    return new System.Drawing.Icon(stream, size, size);
                }
            }
            catch
            {
            }
            return System.Drawing.SystemIcons.Application;
        }

        private void DisposeTray()
        {
            if (_tray == null) return;
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        private void Quit()
        {
            DisposeTray();
            _showWait?.Unregister(null);
            _window?.DisposeHotkey();
            _vm?.Dispose();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DisposeTray();
            base.OnExit(e);
        }
    }
}
