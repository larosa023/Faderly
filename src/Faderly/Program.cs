using System;
using System.Linq;
using System.Threading;
using Faderly.Services;
using Velopack;

namespace Faderly
{
    public static class Program
    {
        public const string ShowSignalName = "Faderly.ShowWindow.Signal.v1";
        private const string MutexName = "Faderly.SingleInstance.v1";

        public static bool IsFirstRun { get; private set; }
        public static bool JustUpdated { get; private set; }

        [STAThread]
        public static void Main(string[] args)
        {
            // Velopack must run first: it handles install/update/uninstall hooks
            // and applies a pending update on startup.
            VelopackApp.Build()
                .OnFirstRun(_ => IsFirstRun = true)
                .OnRestarted(_ => JustUpdated = true)
                .OnBeforeUninstallFastCallback(_ => StartupManager.Disable())
                .Run();

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

            bool startHidden = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            var app = new App(startHidden && !IsFirstRun && !JustUpdated);
            app.Run();

            GC.KeepAlive(mutex);
        }
    }
}
