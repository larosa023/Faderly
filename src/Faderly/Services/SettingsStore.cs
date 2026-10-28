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
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            }
            catch
            {
            }
        }
    }
}
