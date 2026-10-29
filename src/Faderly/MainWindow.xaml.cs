using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Faderly.Services;
using Faderly.ViewModels;

namespace Faderly
{
    /// <summary>
    /// The mixer. Runs in one of two modes:
    ///  - Popup (default): tray flyout anchored next to the taskbar, hides when you click away.
    ///  - Window: a regular app window with a taskbar button that you can drag,
    ///    minimize and leave open. Its position is remembered.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int HotkeyId = 0xF4D1;
        private const uint VK_M = 0x4D;
        private const double ScreenGap = 4;

        private readonly MixerViewModel _vm;
        private readonly DispatcherTimer _saveTimer;
        private DateTime _lastHiddenUtc = DateTime.MinValue;
        private DateTime _modeSwitchedUtc = DateTime.MinValue;
        private HwndSource? _source;
        private IntPtr _hwnd;
        private bool _hotkeyRegistered;
        private double _slideFrom = 14;

        public MainWindow(MixerViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SettingsStore.Save(_vm.Settings);
            };

            Deactivated += (_, _) =>
            {
                if (IsWindowMode) return;
                // Switching modes briefly re-parents the window, which can fire a spurious deactivate.
                if ((DateTime.UtcNow - _modeSwitchedUtc).TotalMilliseconds < 400) return;
                HideMixer();
            };
            SizeChanged += (_, _) =>
            {
                if (IsWindowMode) KeepWindowOnScreen();
                else PositionPopup();
            };
            StateChanged += (_, _) =>
            {
                if (IsVisible) _vm.SetWindowVisible(WindowState != WindowState.Minimized);
            };
            LocationChanged += (_, _) => RememberPosition();

            _vm.WindowModeChanged += (_, _) => OnModeChanged();
            ApplyModeChrome();
        }

        private bool IsWindowMode => _vm.IsWindowMode;

        // ---------- Show / hide ----------

        public void ShowMixer()
        {
            if (IsVisible)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (_hwnd != IntPtr.Zero) Native.SetForegroundWindow(_hwnd);
                return;
            }

            // Refresh first so the very first frame already has the right apps and size.
            _vm.SetWindowVisible(true);

            if (IsWindowMode) PositionWindow(); else PositionPopup();
            WindowState = WindowState.Normal;
            Show();
            Activate();
            if (_hwnd != IntPtr.Zero) Native.SetForegroundWindow(_hwnd);

            // FillBehavior.Stop hands the property back to its base value (1 / 0) when done,
            // so no animation clock keeps running in the background.
            Opacity = 1;
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
            ShellTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(IsWindowMode ? 6 : _slideFrom, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }

        public void HideMixer()
        {
            if (!IsVisible) return;
            RememberPosition();
            Hide();
            _lastHiddenUtc = DateTime.UtcNow;
            _vm.SetWindowVisible(false);
        }

        /// <summary>Tray click.</summary>
        public void ToggleMixer()
        {
            if (IsWindowMode)
            {
                // A tray click always takes focus away first, so just bring the window up.
                ShowMixer();
                return;
            }

            if (IsVisible)
            {
                HideMixer();
                return;
            }

            // Clicking the tray icon deactivates (and hides) the popup a moment before the
            // click arrives; don't immediately reopen it in that case.
            if ((DateTime.UtcNow - _lastHiddenUtc).TotalMilliseconds < 350) return;
            ShowMixer();
        }

        // ---------- Tray menu ----------

        public void ShowTrayMenu()
        {
            if (FindResource("TrayMenu") is not ContextMenu menu) return;
            if (menu.IsOpen) menu.IsOpen = false;

            menu.DataContext = _vm;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;

            // Without this the menu won't close when you click elsewhere on the desktop.
            if (PresentationSource.FromVisual(menu) is HwndSource src)
                Native.SetForegroundWindow(src.Handle);
        }

        private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowMixer();

        private void TraySound_Click(object sender, RoutedEventArgs e) =>
            _vm.OpenSoundSettingsCommand.Execute(null);

        // IsChecked is bound two-way, so the mode has already flipped by the time Click fires.
        private void TrayWindowMode_Click(object sender, RoutedEventArgs e) => ShowMixer();

        private void TrayUpdates_Click(object sender, RoutedEventArgs e)
        {
            ShowMixer();
            _vm.IsSettingsOpen = true;
            _vm.CheckUpdatesCommand.Execute(null);
        }

        private void TrayQuit_Click(object sender, RoutedEventArgs e) => _vm.QuitCommand.Execute(null);

        // ---------- Modes ----------

        private void OnModeChanged()
        {
            bool wasVisible = IsVisible;
            _modeSwitchedUtc = DateTime.UtcNow;
            ApplyModeChrome();

            if (IsWindowMode)
            {
                // Popup -> window: pop it out a little away from the tray corner so it feels "detached".
                if (_vm.Settings.WindowLeft == null || _vm.Settings.WindowTop == null)
                {
                    var area = SystemParameters.WorkArea;
                    Left = area.Right - Width - 80;
                    Top = Math.Max(area.Top + 40, area.Bottom - ActualHeight - 80);
                    RememberPosition(force: true);
                }
                else
                {
                    PositionWindow();
                }
            }
            else
            {
                PositionPopup();
            }

            if (wasVisible)
            {
                if (!IsVisible) Show();
                Activate();
                _vm.SetWindowVisible(true);
            }
        }

        private void ApplyModeChrome()
        {
            if (IsWindowMode)
            {
                Topmost = false;
                ShowInTaskbar = true;
            }
            else
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                ShowInTaskbar = false;
                Topmost = true;
            }
            AllowTaskbarMinimize();
        }

        /// <summary>
        /// Popup: anchor next to the taskbar on the primary screen (bottom-right by default,
        /// but follows a taskbar moved to the top or left).
        /// </summary>
        private void PositionPopup()
        {
            var area = SystemParameters.WorkArea;
            double screenW = SystemParameters.PrimaryScreenWidth;

            MaxHeight = Math.Max(320, area.Height - ScreenGap);
            double height = ActualHeight > 0 ? ActualHeight : 420;

            bool taskbarTop = area.Top > 0.5;
            bool taskbarLeft = area.Left > 0.5 && area.Right >= screenW - 0.5;

            Left = taskbarLeft ? area.Left + ScreenGap : area.Right - Width - ScreenGap;
            Top = taskbarTop ? area.Top + ScreenGap : area.Bottom - height - ScreenGap;
            _slideFrom = taskbarTop ? -14 : 14;
        }

        /// <summary>Window: restore the saved position, kept on-screen.</summary>
        private void PositionWindow()
        {
            MaxHeight = Math.Max(320, SystemParameters.VirtualScreenHeight);

            var screen = VirtualScreen;
            double? left = _vm.Settings.WindowLeft;
            double? top = _vm.Settings.WindowTop;

            if (left is double l && top is double t &&
                l > screen.Left - Width + 80 && l < screen.Right - 80 &&
                t > screen.Top - 10 && t < screen.Bottom - 80)
            {
                Left = l;
                Top = t;
            }
            else
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + Math.Max(40, (area.Height - (ActualHeight > 0 ? ActualHeight : 520)) / 2);
            }
        }

        /// <summary>If the window grows (more apps), nudge it up rather than letting it run off-screen.</summary>
        private void KeepWindowOnScreen()
        {
            if (!IsVisible || WindowState != WindowState.Normal) return;
            var screen = VirtualScreen;
            double overflow = Top + ActualHeight - screen.Bottom;
            if (overflow > 0) Top = Math.Max(screen.Top, Top - overflow);
        }

        private static Rect VirtualScreen => new(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        private void RememberPosition(bool force = false)
        {
            if (!IsWindowMode || (!IsVisible && !force) || WindowState != WindowState.Normal) return;
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            if (_vm.Settings.WindowLeft == Left && _vm.Settings.WindowTop == Top) return;
            _vm.Settings.WindowLeft = Left;
            _vm.Settings.WindowTop = Top;
            // Debounced so dragging doesn't write the settings file dozens of times a second.
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Borderless windows lack WS_MINIMIZEBOX, so clicking the taskbar button wouldn't minimize them.</summary>
        private void AllowTaskbarMinimize()
        {
            if (_hwnd == IntPtr.Zero) return;
            Native.AddWindowStyle(_hwnd, Native.WS_MINIMIZEBOX);
        }

        // ---------- Title bar buttons (window mode) ----------

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsWindowMode || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void CloseToTray_Click(object sender, RoutedEventArgs e) => HideMixer();

        // ---------- Native handle: global hotkey (Ctrl + Alt + M) ----------

        public void InitializeNative()
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
            _hotkeyRegistered = Native.RegisterHotKey(_hwnd, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_M);
            _vm.HotkeyAvailable = _hotkeyRegistered;
            if (!_hotkeyRegistered) Log.Info("Ctrl+Alt+M is already used by another app; hotkey disabled.");
            AllowTaskbarMinimize();
        }

        public void DisposeNative()
        {
            if (_hwnd != IntPtr.Zero && _hotkeyRegistered) Native.UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyRegistered = false;
            _source?.RemoveHook(WndProc);
            _saveTimer.Stop();
            RememberPosition();
            SettingsStore.Save(_vm.Settings);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt64() == HotkeyId)
            {
                if (IsVisible && WindowState != WindowState.Minimized && IsActive) HideMixer();
                else ShowMixer();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---------- Input ----------

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (_vm.IsSettingsOpen) _vm.IsSettingsOpen = false;
            else if (_vm.IsDeviceListOpen) _vm.IsDeviceListOpen = false;
            else if (!IsWindowMode) HideMixer();
            e.Handled = true;
        }

        /// <summary>Scroll over any fader to nudge it by 2% (Shift = 10%).</summary>
        private void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not Slider slider || !slider.IsEnabled) return;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 2;
            slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? step : -step), slider.Minimum, slider.Maximum);
            e.Handled = true;
        }
    }
}
