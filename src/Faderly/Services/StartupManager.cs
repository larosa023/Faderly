using System;
using Microsoft.Win32;

namespace Faderly.Services
{
    /// <summary>Adds/removes Faderly from "start with Windows" (per-user Run key).</summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Faderly";

        public static void Enable()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                key?.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            catch
            {
                // Registry access can fail under unusual policies; not fatal.
            }
        }

        public static void Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                key?.DeleteValue(ValueName, false);
            }
            catch
            {
            }
        }

        public static void Apply(bool enabled)
        {
            if (enabled) Enable(); else Disable();
        }
    }
}
