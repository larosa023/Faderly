using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using Faderly.ViewModels;
using Velopack;
using Velopack.Sources;

namespace Faderly.Services
{
    /// <summary>
    /// Built-in updater. Checks GitHub Releases for a newer version, downloads it in the
    /// background, then offers "Restart to update". Powered by Velopack.
    /// </summary>
    public sealed class UpdateService : ObservableObject
    {
        public const string RepoUrl = "https://github.com/larosa023/Faderly";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

        private readonly UpdateManager? _manager;
        private readonly DispatcherTimer _timer;
        private UpdateInfo? _ready;
        private bool _busy;
        private string _status = "";
        private bool _updateReady;
        private string? _availableVersion;
        private int _progress;

        public event EventHandler? UpdateDownloaded;

        /// <summary>Raised right before the app exits to install an update.</summary>
        public event EventHandler? Restarting;

        public UpdateService()
        {
            try
            {
                _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            }
            catch
            {
                _manager = null;
            }

            _timer = new DispatcherTimer { Interval = CheckInterval };
            _timer.Tick += async (_, _) => await CheckAsync(userInitiated: false);

            Status = IsInstalled ? "" : "Updates work in the installed version.";
        }

        public bool IsInstalled
        {
            get
            {
                try { return _manager?.IsInstalled == true; }
                catch { return false; }
            }
        }

        public string CurrentVersion
        {
            get
            {
                try
                {
                    var v = _manager?.CurrentVersion;
                    if (v != null) return v.ToString();
                }
                catch
                {
                }
                var asm = Assembly.GetExecutingAssembly().GetName().Version;
                return asm == null ? "dev" : $"{asm.Major}.{asm.Minor}.{asm.Build}";
            }
        }

        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public bool UpdateReady
        {
            get => _updateReady;
            private set => SetProperty(ref _updateReady, value);
        }

        public string? AvailableVersion
        {
            get => _availableVersion;
            private set => SetProperty(ref _availableVersion, value);
        }

        public int Progress
        {
            get => _progress;
            private set => SetProperty(ref _progress, value);
        }

        public bool IsBusy
        {
            get => _busy;
            private set => SetProperty(ref _busy, value);
        }

        public void StartAutomaticChecks(bool enabled)
        {
            if (enabled && IsInstalled)
            {
                _timer.Start();
                // First check shortly after startup so launch stays snappy.
                var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                first.Tick += async (_, _) =>
                {
                    first.Stop();
                    await CheckAsync(userInitiated: false);
                };
                first.Start();
            }
            else
            {
                _timer.Stop();
            }
        }

        public async Task CheckAsync(bool userInitiated)
        {
            if (IsBusy || UpdateReady) return;

            if (_manager == null || !IsInstalled)
            {
                if (userInitiated) Status = "Updates work in the installed version.";
                return;
            }

            IsBusy = true;
            try
            {
                if (userInitiated) Status = "Checking for updates…";

                UpdateInfo? info = await _manager.CheckForUpdatesAsync();
                if (info == null)
                {
                    Status = userInitiated ? "You're on the latest version." : "";
                    return;
                }

                AvailableVersion = info.TargetFullRelease.Version.ToString();
                Status = $"Downloading v{AvailableVersion}…";
                Progress = 0;

                var dispatcher = Dispatcher.CurrentDispatcher;
                await _manager.DownloadUpdatesAsync(info, p => dispatcher.InvokeAsync(() =>
                {
                    Progress = p;
                    Status = $"Downloading v{AvailableVersion}… {p}%";
                }));

                _ready = info;
                UpdateReady = true;
                Status = $"Version {AvailableVersion} is ready to install.";
                UpdateDownloaded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Status = userInitiated ? $"Couldn't check for updates: {ex.Message}" : "";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void RestartToUpdate()
        {
            if (_manager == null || _ready == null) return;
            try
            {
                Restarting?.Invoke(this, EventArgs.Empty);
                _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
            }
            catch (Exception ex)
            {
                Status = $"Update failed: {ex.Message}";
            }
        }
    }
}
