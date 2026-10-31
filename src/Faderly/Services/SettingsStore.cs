using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Faderly.Services
{
    public sealed class AppSettings
    {
        public bool StartWithWindows { get; set; } = true;
        public bool AutoCheckUpdates { get; set; } = true;
        public bool ShowSystemSounds { get; set; } = true;
        public bool ShowMicrophone { get; set; } = true;

        /// <summary>Mouse wheel over the tray icon changes master volume.</summary>
        public bool TrayScrollVolume { get; set; } = true;

        /// <summary>Theme key from ThemeManager.Themes, or "System".</summary>
        public string Theme { get; set; } = "System";

        /// <summary>false = tray popup (default), true = regular app window.</summary>
        public bool WindowMode { get; set; }

        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }

        // ----- Hotkeys -----

        public HotkeyBinding? ToggleHotkey { get; set; } = HotkeyBinding.Create(HotkeyBinding.Ctrl | HotkeyBinding.Alt, 0x4D); // M
        public HotkeyBinding? MicMuteHotkey { get; set; } = HotkeyBinding.Create(HotkeyBinding.Ctrl | HotkeyBinding.Alt, 0x4E); // N

        /// <summary>Holding a modifier while using the volume keys / knob controls the app you're in.</summary>
        public bool ActiveAppVolumeKeys { get; set; } = true;

        /// <summary>"Ctrl", "Shift" or "Alt".</summary>
        public string ActiveAppModifier { get; set; } = "Ctrl";

        public List<AppHotkeySettings> AppHotkeys { get; set; } = new();

        // ----- Hidden apps -----

        public List<HiddenApp> HiddenApps { get; set; } = new();

        /// <summary>Repairs anything a hand-edited or older settings file left null.</summary>
        internal void Normalize()
        {
            AppHotkeys ??= new List<AppHotkeySettings>();
            HiddenApps ??= new List<HiddenApp>();
            AppHotkeys.RemoveAll(a => a == null || string.IsNullOrEmpty(a.Key) || a.IsEmpty);
            HiddenApps.RemoveAll(h => h == null || string.IsNullOrEmpty(h.Key));
            Theme ??= "System";
            if (ActiveAppModifier is not ("Ctrl" or "Shift" or "Alt")) ActiveAppModifier = "Ctrl";
            if (ToggleHotkey != null && !ToggleHotkey.IsValid) ToggleHotkey = null;
            if (MicMuteHotkey != null && !MicMuteHotkey.IsValid) MicMuteHotkey = null;
        }
    }

    /// <summary>A key plus modifiers. Modifier bits match both WPF ModifierKeys and RegisterHotKey.</summary>
    public sealed class HotkeyBinding
    {
        public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

        public int Modifiers { get; set; }

        /// <summary>Windows virtual-key code.</summary>
        public int Key { get; set; }

        public bool IsValid => Key > 0 && Key < 256;

        public static HotkeyBinding Create(int modifiers, int key) => new() { Modifiers = modifiers, Key = key };

        public bool SameAs(HotkeyBinding? other) =>
            other != null && other.Modifiers == Modifiers && other.Key == Key;
    }

    public sealed class AppHotkeySettings
    {
        /// <summary>Lower-case exe path, same key the mixer rows use.</summary>
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public HotkeyBinding? Up { get; set; }
        public HotkeyBinding? Down { get; set; }
        public HotkeyBinding? Mute { get; set; }

        public bool IsEmpty => Up == null && Down == null && Mute == null;
    }

    public sealed class HiddenApp
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
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
            AppSettings? settings = null;
            try
            {
                if (File.Exists(FilePath))
                    settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
            }
            catch (Exception ex)
            {
                // Corrupt file: fall back to defaults.
                Log.Error("Reading settings", ex);
            }
            settings ??= new AppSettings();
            settings.Normalize();
            return settings;
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
