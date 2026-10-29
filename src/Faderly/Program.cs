using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faderly.Services;
using Velopack;

namespace Faderly
{
    public static class Program
    {
        public const string ShowSignalName = "Faderly.ShowWindow.Signal.v1";
        private const string MutexName = "Faderly.SingleInstance.v1";

        private static bool _isFirstRun;
        private static bool _justUpdated;

        [STAThread]
        public static void Main(string[] args)
        {
            // Velopack must run first: it handles install/update/uninstall hooks
            // and applies a pending update on startup.
            VelopackApp.Build()
                .OnFirstRun(_ => _isFirstRun = true)
                .OnRestarted(_ => _justUpdated = true)
                .OnBeforeUninstallFastCallback(_ => StartupManager.Disable())
                .Run();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) Log.Error("Fatal unhandled exception", ex);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error("Unobserved task exception", e.Exception);
                e.SetObserved();
            };

            using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // Already running: ask the existing instance to show itself.
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                    signal.Set();
                }
                catch
                {
                    // Existing instance is still starting; nothing to do.
                }
                return;
            }

            bool trayArg = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            // Sign-in launches (--tray) stay in the tray; the very first run after install opens the window.
            var app = new App(startHidden: trayArg && !_isFirstRun, justUpdated: _justUpdated);
            app.Run();

            GC.KeepAlive(mutex);
        }
    }
}
