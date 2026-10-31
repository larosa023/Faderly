using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace Faderly.Services
{
    public sealed record HotkeyRegistration(string Name, HotkeyBinding Binding, bool Repeat, Action Action);

    /// <summary>
    /// System-wide hotkeys via RegisterHotKey. No keyboard hook: Windows only wakes us up when one of
    /// our exact key combinations is pressed, so this costs nothing the rest of the time.
    /// </summary>
    internal sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _window;
        private readonly Dictionary<int, HotkeyRegistration> _registered = new();
        private List<HotkeyRegistration> _wanted = new();
        private bool _suspended;
        private bool _disposed;

        /// <summary>Names of hotkeys Windows refused (usually because another app already uses the combination).</summary>
        public IReadOnlyList<string> Failed { get; private set; } = Array.Empty<string>();

        public event EventHandler? FailuresChanged;

        public HotkeyManager()
        {
            var parameters = new HwndSourceParameters("FaderlyHotkeys")
            {
                WindowStyle = 0,
                ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW
                Width = 0,
                Height = 0
            };
            _window = new HwndSource(parameters);
            _window.AddHook(WndProc);
        }

        public void Set(IEnumerable<HotkeyRegistration> registrations)
        {
            if (_disposed) return;
            _wanted = new List<HotkeyRegistration>(registrations);
            UnregisterAll();
            if (!_suspended) RegisterAll();
        }

        /// <summary>Temporarily release every hotkey (while the user is recording a new one).</summary>
        public void Suspend()
        {
            if (_suspended) return;
            _suspended = true;
            UnregisterAll();
        }

        public void Resume()
        {
            if (!_suspended) return;
            _suspended = false;
            RegisterAll();
        }

        private void RegisterAll()
        {
            var failed = new List<string>();
            int id = 1;
            foreach (var reg in _wanted)
            {
                if (!reg.Binding.IsValid) continue;
                uint mods = (uint)reg.Binding.Modifiers;
                if (!reg.Repeat) mods |= Native.MOD_NOREPEAT;
                if (Native.RegisterHotKey(_window.Handle, id, mods, (uint)reg.Binding.Key))
                {
                    _registered[id] = reg;
                }
                else
                {
                    failed.Add(reg.Name);
                    Log.Info($"Hotkey {Format(reg.Binding)} for \"{reg.Name}\" is already in use.");
                }
                id++;
            }
            Failed = failed;
            FailuresChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UnregisterAll()
        {
            foreach (var id in _registered.Keys)
                Native.UnregisterHotKey(_window.Handle, id);
            _registered.Clear();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _registered.TryGetValue((int)wParam.ToInt64(), out var reg))
            {
                handled = true;
                try
                {
                    reg.Action();
                }
                catch (Exception ex)
                {
                    Log.Error("Hotkey " + reg.Name, ex);
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterAll();
            _window.RemoveHook(WndProc);
            _window.Dispose();
        }

        // ---------- Display ----------

        public static string Format(HotkeyBinding? binding)
        {
            if (binding == null || !binding.IsValid) return "Not set";
            var sb = new StringBuilder();
            AppendModifiers(sb, binding.Modifiers);
            sb.Append(KeyName(binding.Key));
            return sb.ToString();
        }

        public static string FormatModifiersOnly(ModifierKeys modifiers)
        {
            var sb = new StringBuilder();
            AppendModifiers(sb, (int)modifiers);
            return sb.ToString();
        }

        private static void AppendModifiers(StringBuilder sb, int mods)
        {
            if ((mods & HotkeyBinding.Ctrl) != 0) sb.Append("Ctrl + ");
            if ((mods & HotkeyBinding.Alt) != 0) sb.Append("Alt + ");
            if ((mods & HotkeyBinding.Shift) != 0) sb.Append("Shift + ");
            if ((mods & HotkeyBinding.Win) != 0) sb.Append("Win + ");
        }

        public static string KeyName(int vk)
        {
            Key key = KeyInterop.KeyFromVirtualKey(vk);
            return key switch
            {
                Key.VolumeUp => "Volume Up",
                Key.VolumeDown => "Volume Down",
                Key.VolumeMute => "Volume Mute",
                Key.MediaPlayPause => "Play/Pause",
                Key.MediaNextTrack => "Next Track",
                Key.MediaPreviousTrack => "Previous Track",
                Key.MediaStop => "Stop",
                Key.Return => "Enter",
                Key.Prior => "Page Up",
                Key.Next => "Page Down",
                Key.Up => "Up",
                Key.Down => "Down",
                Key.Left => "Left",
                Key.Right => "Right",
                Key.OemPlus => "=",
                Key.OemMinus => "-",
                Key.OemComma => ",",
                Key.OemPeriod => ".",
                Key.Oem1 => ";",
                Key.Oem2 => "/",
                Key.Oem3 => "`",
                Key.Oem4 => "[",
                Key.Oem5 => "\\",
                Key.Oem6 => "]",
                Key.Oem7 => "'",
                Key.Add => "Num +",
                Key.Subtract => "Num -",
                Key.Multiply => "Num *",
                Key.Divide => "Num /",
                >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
                Key.None => $"Key {vk}",
                _ => key.ToString()
            };
        }
    }
}
