using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Faderly.Services;
using Faderly.ViewModels;

namespace Faderly
{
    /// <summary>
    /// Small on-screen indicator shown above the taskbar after a hotkey, knob or tray-wheel change.
    /// It never takes focus and clicks pass straight through it.
    /// </summary>
    public partial class OsdWindow : Window
    {
        private readonly DispatcherTimer _hideTimer;

        public OsdWindow()
        {
            InitializeComponent();
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
                fade.Completed += (_, _) =>
                {
                    // Only hide if nothing re-showed it during the fade.
                    if (!_hideTimer.IsEnabled) Hide();
                };
                BeginAnimation(OpacityProperty, fade);
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.AddWindowExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
        }

        public void ShowFeedback(VolumeFeedback feedback)
        {
            TitleText.Text = feedback.Title;
            RightText.Text = feedback.RightText;
            GlyphText.Text = feedback.Glyph;
            IconImage.Source = feedback.Icon;
            IconImage.Visibility = feedback.HasIcon ? Visibility.Visible : Visibility.Collapsed;
            GlyphText.Visibility = feedback.HasIcon ? Visibility.Collapsed : Visibility.Visible;
            Bar.Visibility = feedback.HasLevel ? Visibility.Visible : Visibility.Collapsed;
            BarScale.ScaleX = feedback.LevelFraction;
            BarFill.Opacity = feedback.Muted ? 0.35 : 1;

            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (!IsVisible) Show();
            UpdateLayout();
            Position();

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        /// <summary>Centered just above the taskbar on the primary screen.</summary>
        private void Position()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 48;
        }
    }
}
