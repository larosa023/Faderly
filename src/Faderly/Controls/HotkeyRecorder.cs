using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Faderly.Services;

namespace Faderly.Controls
{
    /// <summary>
    /// A box that records a key combination: click it, press the keys. Backspace clears, Esc cancels.
    /// While recording, global hotkeys are released so the combination reaches this box.
    /// </summary>
    public sealed class HotkeyRecorder : Button
    {
        public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
            nameof(Hotkey), typeof(HotkeyBinding), typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, _) => ((HotkeyRecorder)d).UpdateText()));

        private static readonly DependencyPropertyKey IsRecordingKey = DependencyProperty.RegisterReadOnly(
            nameof(IsRecording), typeof(bool), typeof(HotkeyRecorder), new PropertyMetadata(false));

        public static readonly DependencyProperty IsRecordingProperty = IsRecordingKey.DependencyProperty;

        private static readonly DependencyPropertyKey DisplayTextKey = DependencyProperty.RegisterReadOnly(
            nameof(DisplayText), typeof(string), typeof(HotkeyRecorder), new PropertyMetadata("Not set"));

        public static readonly DependencyProperty DisplayTextProperty = DisplayTextKey.DependencyProperty;

        private static readonly DependencyPropertyKey IsSetKey = DependencyProperty.RegisterReadOnly(
            nameof(IsSet), typeof(bool), typeof(HotkeyRecorder), new PropertyMetadata(false));

        public static readonly DependencyProperty IsSetProperty = IsSetKey.DependencyProperty;

        /// <summary>Raised when any recorder starts / stops listening (global hotkeys are paused in between).</summary>
        public static event EventHandler? RecordingStarted;
        public static event EventHandler? RecordingStopped;

        public HotkeyRecorder()
        {
            Focusable = true;
            ToolTip = "Click, then press a key combination.\nBackspace clears it, Esc cancels.";
            UpdateText();
        }

        public HotkeyBinding? Hotkey
        {
            get => (HotkeyBinding?)GetValue(HotkeyProperty);
            set => SetValue(HotkeyProperty, value);
        }

        public bool IsRecording => (bool)GetValue(IsRecordingProperty);
        public string DisplayText => (string)GetValue(DisplayTextProperty);
        public bool IsSet => (bool)GetValue(IsSetProperty);

        protected override void OnClick()
        {
            base.OnClick();
            if (IsRecording) Stop();
            else Start();
        }

        private void Start()
        {
            SetValue(IsRecordingKey, true);
            SetValue(DisplayTextKey, "Press keys…");
            Keyboard.Focus(this);
            RecordingStarted?.Invoke(this, EventArgs.Empty);
        }

        private void Stop()
        {
            if (!IsRecording) return;
            SetValue(IsRecordingKey, false);
            UpdateText();
            RecordingStopped?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateText()
        {
            SetValue(IsSetKey, Hotkey != null && Hotkey.IsValid);
            if (!IsRecording) SetValue(DisplayTextKey, HotkeyManager.Format(Hotkey));
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!IsRecording)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            e.Handled = true;
            Key key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                Key.DeadCharProcessed => e.DeadCharProcessedKey,
                _ => e.Key
            };
            ModifierKeys mods = Keyboard.Modifiers;

            if (IsModifierKey(key))
            {
                // Show what's held so far, e.g. "Ctrl + Alt + …"
                SetValue(DisplayTextKey, HotkeyManager.FormatModifiersOnly(mods) + "…");
                return;
            }

            if (mods == ModifierKeys.None && key == Key.Escape)
            {
                Stop();
                return;
            }

            if (mods == ModifierKeys.None && (key == Key.Back || key == Key.Delete))
            {
                Hotkey = null;
                Stop();
                return;
            }

            if (mods == ModifierKeys.None && !WorksAlone(key))
            {
                SetValue(DisplayTextKey, "Add Ctrl, Alt or Shift");
                return;
            }

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0 || vk >= 256) return;

            Hotkey = HotkeyBinding.Create((int)mods, vk);
            Stop();
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if (IsRecording) e.Handled = true; // e.g. stop a lone Alt from doing anything
            else base.OnPreviewKeyUp(e);
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            Stop();
        }

        protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnIsKeyboardFocusWithinChanged(e);
            if (!(bool)e.NewValue) Stop();
        }

        private static bool IsModifierKey(Key key) => key is
            Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

        /// <summary>
        /// Keys that make sense as a hotkey with no modifier. Volume keys aren't included: binding a bare
        /// Volume Up would take over the master volume keys everywhere.
        /// </summary>
        private static bool WorksAlone(Key key) =>
            (key >= Key.F13 && key <= Key.F24) ||
            key is Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop or
                   Key.Pause or Key.Scroll;
    }
}
