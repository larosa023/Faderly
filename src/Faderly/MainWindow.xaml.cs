using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Faderly.Services;
using Faderly.ViewModels;

namespace Faderly
{
    /// <summary>
    /// The mixer. Runs in one of two modes:
    ///  - Popup (default): tray flyout anchored bottom-right, hides when you click away.
    ///  - Window: a regular app window with a taskbar button that you can drag,
    ///    minimize and leave open. Its position is remembered.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int HotkeyId = 0xF4D1;
        private const uint VK_M = 0x4D;

        private readonly MixerViewModel _vm;
        private DateTime _lastHiddenUtc = DateTime.MinValue;
        private HwndSource? _source;
        private IntPtr _hwnd;

        public MainWindow(MixerViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            Deactivated += (_, _) =>
            {
                if (!_vm.IsWindowMode) HideMixer();
            };
            SizeChanged += (_, _) =>
            {
                if (!_vm.IsWindowMode) PositionPopup();
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
                return;
            }

            Opacity = 0;
            if (IsWindowMode) PositionWindow(); else PositionPopup();
            WindowState = WindowState.Normal;
            Show();
            Activate();
            if (_hwnd != IntPtr.Zero) Native.SetForegroundWindow(_hwnd);
            _vm.SetWindowVisible(true);

            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            ShellTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(IsWindowMode ? 6 : 14, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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

        /// <summary>Tray click / hotkey.</summary>
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

        // ---------- Modes ----------

        private void OnModeChanged()
        {
            bool wasVisible = IsVisible;
            ApplyModeChrome();

            if (IsWindowMode)
            {
                // Popup -> window: pop it out a little left of the tray corner so it feels "detached".
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
        }

        /// <summary>Popup: anchor to the bottom-right of the work area, above the taskbar.</summary>
        private void PositionPopup()
        {
            var area = SystemParameters.WorkArea;
            double height = ActualHeight > 0 ? ActualHeight : 420;
            Left = area.Right - Width - 4;
            Top = area.Bottom - height - 4;
        }

        /// <summary>Window: restore the saved position, kept on-screen.</summary>
        private void PositionWindow()
        {
            var screen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

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

        private void RememberPosition(bool force = false)
        {
            if (!IsWindowMode || (!IsVisible && !force) || WindowState != WindowState.Normal) return;
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            if (_vm.Settings.WindowLeft == Left && _vm.Settings.WindowTop == Top) return;
            _vm.Settings.WindowLeft = Left;
            _vm.Settings.WindowTop = Top;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // Debounce settings writes while the window is being dragged.
        private readonly System.Windows.Threading.DispatcherTimer _saveTimer = CreateSaveTimer();

        private static System.Windows.Threading.DispatcherTimer CreateSaveTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            return timer;
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SettingsStore.Save(_vm.Settings);
            };
        }

        // ---------- Title bar buttons (window mode) ----------

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsWindowMode || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void CloseToTray_Click(object sender, RoutedEventArgs e) => HideMixer();

        // ---------- Global hotkey (Ctrl + Alt + M) ----------

        public void InitializeHotkey()
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
            Native.RegisterHotKey(_hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_M);
        }

        public void DisposeHotkey()
        {
            if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, HotkeyId);
            _source?.RemoveHook(WndProc);
            RememberPosition();
            SettingsStore.Save(_vm.Settings);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                // Hotkey doesn't steal focus, so "active" means the user is looking at it.
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
