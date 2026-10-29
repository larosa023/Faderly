using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Faderly.Services
{
    /// <summary>
    /// Notification-area icon built straight on Shell_NotifyIcon, so the app doesn't need to
    /// load Windows Forms just for a tray icon. Re-adds itself if Explorer restarts.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private const int CallbackMessage = 0x8000 + 0x10; // WM_APP + 0x10
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONUP = 0x0205;
        private const int NIN_BALLOONUSERCLICK = 0x0405;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const uint NIM_ADD = 0x0;
        private const uint NIM_MODIFY = 0x1;
        private const uint NIM_DELETE = 0x2;
        private const uint NIF_MESSAGE = 0x1;
        private const uint NIF_ICON = 0x2;
        private const uint NIF_TIP = 0x4;
        private const uint NIF_INFO = 0x10;
        private const uint NIIF_INFO = 0x1;
        private const uint NIIF_NOSOUND = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

        private readonly HwndSource _window;
        private readonly int _taskbarCreatedMessage;
        private readonly DispatcherTimer _retryTimer;
        private readonly string _tooltip;
        private readonly IntPtr _icon;
        private readonly bool _ownsIcon;
        private bool _added;
        private bool _disposed;
        private int _retries;

        public event EventHandler? LeftClick;
        public event EventHandler? RightClick;
        public event EventHandler? BalloonClick;

        public TrayIcon(string tooltip)
        {
            _tooltip = tooltip;

            int size = Native.GetSystemMetrics(Native.SM_CXSMICON);
            if (size <= 0) size = 16;
            string? exe = Environment.ProcessPath;
            _icon = exe != null ? Native.ExtractIcon(exe, size) : IntPtr.Zero;
            _ownsIcon = _icon != IntPtr.Zero;
            if (_icon == IntPtr.Zero) _icon = Native.LoadIcon(IntPtr.Zero, Native.IDI_APPLICATION);

            // A hidden top-level window (not message-only, so it still hears "TaskbarCreated").
            var parameters = new HwndSourceParameters("FaderlyTray")
            {
                WindowStyle = 0,
                ExtendedWindowStyle = WS_EX_TOOLWINDOW,
                Width = 0,
                Height = 0
            };
            _window = new HwndSource(parameters);
            _window.AddHook(WndProc);
            _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");

            // At sign-in the taskbar may not exist yet; keep trying for a couple of minutes.
            _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _retryTimer.Tick += (_, _) =>
            {
                if (TryAdd() || ++_retries >= 60) _retryTimer.Stop();
            };
            if (!TryAdd()) _retryTimer.Start();
        }

        public void ShowBalloon(string title, string text)
        {
            if (_disposed || !_added) return;
            var data = NewData(NIF_INFO);
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(text, 255);
            data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        private bool TryAdd()
        {
            if (_disposed || _added) return true;
            var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            data.uCallbackMessage = CallbackMessage;
            data.hIcon = _icon;
            data.szTip = Truncate(_tooltip, 127);
            _added = Shell_NotifyIcon(NIM_ADD, ref data) || Shell_NotifyIcon(NIM_MODIFY, ref data);
            return _added;
        }

        private NOTIFYICONDATAW NewData(uint flags) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = flags,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == CallbackMessage)
            {
                EventHandler? handler = (int)(lParam.ToInt64() & 0xFFFF) switch
                {
                    WM_LBUTTONUP => LeftClick,
                    WM_RBUTTONUP => RightClick,
                    NIN_BALLOONUSERCLICK => BalloonClick,
                    _ => null
                };
                // Return to the shell right away; do the UI work on the next dispatcher turn.
                if (handler != null)
                    _window.Dispatcher.BeginInvoke(new Action(() => handler(this, EventArgs.Empty)));
                handled = true;
            }
            else if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                // Explorer restarted: our icon is gone, add it back.
                _added = false;
                if (!TryAdd()) _retryTimer.Start();
            }
            return IntPtr.Zero;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _retryTimer.Stop();
            try
            {
                if (_added)
                {
                    var data = NewData(0);
                    Shell_NotifyIcon(NIM_DELETE, ref data);
                }
            }
            catch
            {
            }
            _added = false;
            if (_ownsIcon) Native.DestroyIcon(_icon);
            _window.RemoveHook(WndProc);
            _window.Dispose();
        }
    }
}
