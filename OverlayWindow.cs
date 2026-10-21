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
        private readonly OverlayView _view = new OverlayView();
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly DispatcherTimer _frame;
        private bool _edit, _clickThrough;

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "TeamPace overlay";
            _view.LayoutTransform = _scale;
            Content = _view;

            SourceInitialized += (s, e) => SetClickThrough(!_edit);
            MouseLeftButtonDown += (s, e) => { if (_edit) { try { DragMove(); } catch { } Save(); } };
            MouseWheel += (s, e) =>
            {
                if (!_edit) return;
                var st = OverlaySettings.Current;
                st.Scale = Math.Max(0.4, Math.Min(3, st.Scale + (e.Delta > 0 ? 0.05 : -0.05)));
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
            _scale.ScaleX = _scale.ScaleY = s.Scale;
            if (double.IsNaN(s.Left)) s.Left = (SystemParameters.PrimaryScreenWidth - OverlaySettings.BaseWidth * s.Scale) / 2;
            if (Math.Abs(Left - s.Left) > 0.5) Left = s.Left;
            if (Math.Abs(Top - s.Top) > 0.5) Top = s.Top;
            Cursor = edit ? Cursors.SizeAll : null;
            SetClickThrough(!edit);
            Topmost = false; Topmost = true;   // back above the game if it took the front
        }

        private void Save()
        {
            var s = OverlaySettings.Current;
            s.Left = Left; s.Top = Top;
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
