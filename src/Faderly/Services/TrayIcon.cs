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
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out Native.RECT location);

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

        // Wheel support: raw mouse input is only switched on while the cursor is over the icon.
        private readonly DispatcherTimer _hoverTimer;
        private bool _wheelTracking;
        private int _wheelRemainder;
        private bool _scrollEnabled = true;

        public event EventHandler? LeftButtonDown;
        public event EventHandler? LeftClick;
        public event EventHandler? RightClick;
        public event EventHandler? BalloonClick;

        /// <summary>Mouse wheel over the icon, in notches (positive = up). Supports fine-grained wheels.</summary>
        public event EventHandler<int>? Scroll;

        public TrayIcon(string tooltip)
        {
            _tooltip = tooltip;

            // Created first so no window message can ever find it missing.
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _hoverTimer.Tick += (_, _) =>
            {
                if (!IsCursorOverIcon()) StopWheelTracking();
            };

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

        public bool ScrollEnabled
        {
            get => _scrollEnabled;
            set
            {
                _scrollEnabled = value;
                if (!value) StopWheelTracking();
            }
        }

        /// <summary>The hidden window that receives tray messages.</summary>
        public IntPtr Handle => _window.Handle;

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
                int evt = (int)(lParam.ToInt64() & 0xFFFF);

                if (evt == WM_MOUSEMOVE)
                {
                    StartWheelTracking();
                    handled = true;
                    return IntPtr.Zero;
                }

                // Button-down is handled immediately (not queued) so it's recorded before the click.
                if (evt == WM_LBUTTONDOWN)
                {
                    LeftButtonDown?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    return IntPtr.Zero;
                }

                EventHandler? handler = evt switch
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
            else if (msg == Native.WM_INPUT && _wheelTracking)
            {
                // Not marked handled: Windows needs DefWindowProc to clean up WM_INPUT.
                int notches = ReadWheel(lParam);
                if (notches != 0 && IsCursorOverIcon())
                {
                    var scroll = Scroll;
                    if (scroll != null)
                        _window.Dispatcher.BeginInvoke(new Action(() => scroll(this, notches)));
                }
            }
            else if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                // Explorer restarted: our icon is gone, add it back.
                _added = false;
                if (!TryAdd()) _retryTimer.Start();
            }
            return IntPtr.Zero;
        }

        // ---------- Wheel ----------

        private void StartWheelTracking()
        {
            if (_wheelTracking || !_scrollEnabled || _disposed) return;
            var device = new[]
            {
                new Native.RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = Native.RIDEV_INPUTSINK, Target = _window.Handle }
            };
            if (Native.RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            {
                _wheelTracking = true;
                _wheelRemainder = 0;
                _hoverTimer.Start();
            }
        }

        private void StopWheelTracking()
        {
            _hoverTimer.Stop();
            if (!_wheelTracking) return;
            _wheelTracking = false;
            var device = new[]
            {
                new Native.RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = Native.RIDEV_REMOVE, Target = IntPtr.Zero }
            };
            Native.RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
        }

        /// <summary>Whole wheel notches in this raw input packet (0 if it isn't a wheel event).</summary>
        private int ReadWheel(IntPtr rawInput)
        {
            uint headerSize = (uint)(8 + 2 * IntPtr.Size); // RAWINPUTHEADER
            uint size = 0;
            Native.GetRawInputData(rawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size < headerSize + 8 || size > 1024) return 0;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(rawInput, Native.RID_INPUT, buffer, ref size, headerSize) != size) return 0;
                if ((uint)Marshal.ReadInt32(buffer, 0) != Native.RIM_TYPEMOUSE) return 0;

                // RAWMOUSE: usFlags (2) + padding (2), then usButtonFlags (2), usButtonData (2).
                ushort flags = (ushort)Marshal.ReadInt16(buffer, (int)headerSize + 4);
                if ((flags & Native.RI_MOUSE_WHEEL) == 0) return 0;
                short delta = Marshal.ReadInt16(buffer, (int)headerSize + 6);

                _wheelRemainder += delta;
                int notches = _wheelRemainder / 120;
                _wheelRemainder -= notches * 120;
                return notches;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private bool IsCursorOverIcon()
        {
            if (!_added || !Native.GetCursorPos(out var pt)) return false;
            var id = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = _window.Handle,
                uID = 1
            };
            if (Shell_NotifyIconGetRect(ref id, out var r) != 0) return false;
            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _retryTimer.Stop();
            StopWheelTracking();
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
