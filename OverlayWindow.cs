using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace User.TeamPace
{
    /// <summary>
    /// The overlay's own window: borderless, transparent, above the game, and ignoring the mouse
    /// (clicks go through to the game) except while it is being placed. Works with the game in
    /// a window or in borderless full screen, like SimHub's overlays.
    /// </summary>
    internal class OverlayWindow : Window
    {
        private readonly OverlayCanvas _view;
        private readonly bool _pit;
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly DispatcherTimer _frame;
        private bool _edit, _clickThrough;

        public OverlayWindow(bool pitwall)
        {
            _pit = pitwall;
            _view = pitwall ? (OverlayCanvas)new PitwallView() : new OverlayView();
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = pitwall ? "TeamPace pitwall" : "TeamPace overlay";
            _view.LayoutTransform = _scale;
            Content = _view;

            SourceInitialized += (s, e) => SetClickThrough(!_edit);
            MouseLeftButtonDown += (s, e) => { if (_edit) { try { DragMove(); } catch { } Save(); } };
            MouseWheel += (s, e) =>
            {
                if (!_edit) return;
                var st = OverlaySettings.Current;
                double step = e.Delta > 0 ? 0.05 : -0.05;
                if (_pit) st.PitScale = Math.Max(0.4, Math.Min(3, st.PitScale + step));
                else st.Scale = Math.Max(0.4, Math.Min(3, st.Scale + step));
                OverlaySettings.Save();
            };

            // 30 frames a second while shown, nothing while hidden
            _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _frame.Tick += (s, e) =>
            {
                if (!OverlayService.Live) OverlayService.DemoTick();
                _view.InvalidateVisual();
            };
            IsVisibleChanged += (s, e) => { if (IsVisible) _frame.Start(); else _frame.Stop(); };
        }

        public void Apply(OverlaySettings s, bool edit)
        {
            _edit = edit;
            _view.EditMode = edit;
            double scale = _pit ? s.PitScale : s.Scale;
            _scale.ScaleX = _scale.ScaleY = scale;
            if (_pit && double.IsNaN(s.PitLeft)) s.PitLeft = (SystemParameters.PrimaryScreenWidth - OverlaySettings.PitWidth * scale) / 2;
            if (!_pit && double.IsNaN(s.Left)) s.Left = (SystemParameters.PrimaryScreenWidth - OverlaySettings.BaseWidth * scale) / 2;
            double left = _pit ? s.PitLeft : s.Left, top = _pit ? s.PitTop : s.Top;
            if (Math.Abs(Left - left) > 0.5) Left = left;
            if (Math.Abs(Top - top) > 0.5) Top = top;
            Cursor = edit ? Cursors.SizeAll : null;
            SetClickThrough(!edit);
            Topmost = false; Topmost = true;   // back above the game if it took the front
        }

        private void Save()
        {
            var s = OverlaySettings.Current;
            if (_pit) { s.PitLeft = Left; s.PitTop = Top; }
            else { s.Left = Left; s.Top = Top; }
            OverlaySettings.Save();
        }

        // ─── Win32: the mouse goes through the window ─────────────────────────

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private void SetClickThrough(bool on)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || (on == _clickThrough && _styled)) return;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            style = on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
            _clickThrough = on;
            _styled = true;
        }

        private bool _styled;
    }
}
