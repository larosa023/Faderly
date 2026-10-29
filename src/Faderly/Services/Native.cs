using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Faderly.Services
{
    internal static class Native
    {
        // ---------- Process info ----------

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        private const uint STILL_ACTIVE = 259;

        /// <summary>Full exe path of a process, or null if it can't be read.</summary>
        public static string? GetProcessPath(uint pid)
        {
            if (pid == 0) return null;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>True if the process exists and has not exited (or we can't tell).</summary>
        public static bool IsProcessAlive(uint pid)
        {
            if (pid == 0) return true;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                // Access denied means it exists; invalid parameter means it's gone.
                int err = Marshal.GetLastWin32Error();
                return err == 5; // ERROR_ACCESS_DENIED
            }
            try
            {
                return !GetExitCodeProcess(handle, out uint code) || code == STILL_ACTIVE;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // ---------- Icons ----------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIconW(string iconFile, int index, uint flags,
            out IntPtr largeIcon, out IntPtr smallIcon, uint iconSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

        public static readonly IntPtr IDI_APPLICATION = new(32512);

        /// <summary>
        /// First icon of an exe at (roughly) the requested pixel size, or IntPtr.Zero.
        /// The caller owns the handle and must DestroyIcon it.
        /// </summary>
        public static IntPtr ExtractIcon(string path, int size)
        {
            try
            {
                uint sizes = (uint)(size & 0xFFFF) | ((uint)(size & 0xFFFF) << 16);
                int hr = SHDefExtractIconW(path, 0, 0, out IntPtr large, out IntPtr small, sizes);
                if (small != IntPtr.Zero) DestroyIcon(small);
                if (hr != 0)
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    return IntPtr.Zero;
                }
                return large;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        // ---------- Misc window helpers ----------

        public const int SM_CXSMICON = 49;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegisterWindowMessage(string message);

        private const int GWL_STYLE = -16;
        public const int WS_MINIMIZEBOX = 0x00020000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        public static void AddWindowStyle(IntPtr hwnd, int style)
        {
            try
            {
                long current = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                if ((current & style) == style) return;
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(current | (long)style));
            }
            catch
            {
            }
        }

        // ---------- Global hotkey ----------

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        // ---------- Default audio device (undocumented but stable IPolicyConfig) ----------

        [ComImport]
        [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
        }

        [ComImport]
        [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class CPolicyConfigClient
        {
        }

        /// <summary>Makes a playback device the Windows default for all roles.</summary>
        public static bool SetDefaultAudioDevice(string deviceId)
        {
            object? client = null;
            try
            {
                client = new CPolicyConfigClient();
                var policy = (IPolicyConfig)client;
                // 0 = Console, 1 = Multimedia, 2 = Communications
                int hr = 0;
                for (int role = 0; role <= 2; role++)
                    hr |= policy.SetDefaultEndpoint(deviceId, role);
                return hr == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (client != null) Marshal.ReleaseComObject(client);
            }
        }
    }
}
