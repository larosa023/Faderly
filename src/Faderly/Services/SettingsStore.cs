using System;
using System.IO;
using System.Text.Json;

namespace Faderly.Services
{
    public sealed class AppSettings
    {
        public bool StartWithWindows { get; set; } = true;
        public bool AutoCheckUpdates { get; set; } = true;
        public bool ShowSystemSounds { get; set; } = true;

        /// <summary>Theme key from ThemeManager.Themes, or "System".</summary>
        public string Theme { get; set; } = "System";

        /// <summary>false = tray popup (default), true = regular app window.</summary>
        public bool WindowMode { get; set; }

        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
    }

    /// <summary>Loads and saves settings as JSON in %AppData%\Faderly.</summary>
    public static class SettingsStore
    {
        private static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Faderly");

        private static readonly string FilePath = Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
                }
            }
            catch
            {
                // Corrupt file: fall back to defaults.
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                // Write to a temp file and swap it in, so a crash mid-write can't corrupt settings.
                Directory.CreateDirectory(Folder);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Saving settings", ex);
            }
        }
    }
}
