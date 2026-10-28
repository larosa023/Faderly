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

            Deactivated += (_, _) => HideMixer();
            SizeChanged += (_, _) => PositionWindow();
        }

        // ---------- Show / hide ----------

        public void ShowMixer()
        {
            if (IsVisible)
            {
                Activate();
                return;
            }

            Opacity = 0;
            PositionWindow();
            Show();
            Activate();
            if (_hwnd != IntPtr.Zero) Native.SetForegroundWindow(_hwnd);
            _vm.SetWindowVisible(true);

            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            ShellTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        public void HideMixer()
        {
            if (!IsVisible) return;
            Hide();
            _lastHiddenUtc = DateTime.UtcNow;
            _vm.SetWindowVisible(false);
        }

        public void ToggleMixer()
        {
            if (IsVisible)
            {
                HideMixer();
                return;
            }

            // Clicking the tray icon deactivates (and hides) the window a moment before the
            // click arrives; don't immediately reopen it in that case.
            if ((DateTime.UtcNow - _lastHiddenUtc).TotalMilliseconds < 350) return;
            ShowMixer();
        }

        /// <summary>Anchor to the bottom-right of the work area, above the taskbar.</summary>
        private void PositionWindow()
        {
            var area = SystemParameters.WorkArea;
            double height = ActualHeight > 0 ? ActualHeight : 420;
            Left = area.Right - Width - 4;
            Top = area.Bottom - height - 4;
        }

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
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                if (IsVisible) HideMixer();
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
            else HideMixer();
            e.Handled = true;
        }

        /// <summary>Scroll over any fader to nudge it by 2%.</summary>
        private void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not Slider slider || !slider.IsEnabled) return;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 2;
            slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? step : -step), slider.Minimum, slider.Maximum);
            e.Handled = true;
        }
    }
}
