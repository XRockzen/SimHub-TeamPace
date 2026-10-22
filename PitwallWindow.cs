using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace User.TeamPace
{
    /// <summary>A window the overlay service shows and hides.</summary>
    internal interface IOverlayHost
    {
        void Apply(OverlaySettings s, bool edit);
    }

    /// <summary>
    /// The pitwall: for the teammate following the race in the session. On top, the stint, the
    /// fuel, the next stop and what to set for it; below, the race tab's analysis of every car,
    /// live (where the time went against the leader, the cars and their stops, the gap to the
    /// leader lap by lap). A normal window above the game: it can be moved by its top band,
    /// resized by its corner, scrolled and clicked.
    /// </summary>
    internal class PitwallWindow : Window, IOverlayHost
    {
        private readonly PitwallView _head = new PitwallView();
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly ScrollViewer _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
        private readonly Border _body;
        private readonly DispatcherTimer _frame, _slow;
        private int _builtAt = -1;
        private bool _dirty, _applying;

        public PitwallWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Title = "TeamPace pitwall";
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/User.TeamPace;component/Styles.xaml", UriKind.Relative) });

            var root = new DockPanel { LayoutTransform = _scale };
            DockPanel.SetDock(_head, Dock.Top);
            _head.HorizontalAlignment = HorizontalAlignment.Left;
            _head.Cursor = System.Windows.Input.Cursors.SizeAll;
            _head.ToolTip = "Glisser pour déplacer · coin en bas à droite pour redimensionner";
            _head.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } Save(); };
            root.Children.Add(_head);
            _body = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 0x1B, 0x1D, 0x22)), CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(14, 10, 8, 10), Child = _scroll,
            };
            root.Children.Add(_body);
            Content = root;
            Placeholder("L'analyse de toutes les voitures s'affiche ici pendant la course : où le temps est parti face au premier, les voitures et leurs arrêts, l'écart au premier tour par tour.");

            SizeChanged += (s, e) => { if (!_applying) _dirty = true; };
            LocationChanged += (s, e) => { if (!_applying) _dirty = true; };

            _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
            _frame.Tick += (s, e) =>
            {
                if (!OverlayService.Live) OverlayService.DemoTick();
                _head.InvalidateVisual();
            };
            _slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _slow.Tick += (s, e) => { Refresh(); if (_dirty) Save(); };
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { _frame.Start(); _slow.Start(); Refresh(); }
                else { _frame.Stop(); _slow.Stop(); }
            };
        }

        public void Apply(OverlaySettings s, bool edit)
        {
            _applying = true;
            _scale.ScaleX = _scale.ScaleY = s.PitScale;
            if (double.IsNaN(s.PitLeft)) s.PitLeft = Math.Max(0, (SystemParameters.PrimaryScreenWidth - s.PitW) / 2);
            if (Math.Abs(Left - s.PitLeft) > 0.5) Left = s.PitLeft;
            if (Math.Abs(Top - s.PitTop) > 0.5) Top = s.PitTop;
            if (Math.Abs(Width - s.PitW) > 0.5) Width = s.PitW;
            if (Math.Abs(Height - s.PitH) > 0.5) Height = s.PitH;
            Topmost = false; Topmost = true;
            _applying = false;
        }

        private void Save()
        {
            _dirty = false;
            var s = OverlaySettings.Current;
            s.PitLeft = Left; s.PitTop = Top; s.PitW = ActualWidth; s.PitH = ActualHeight;
            OverlaySettings.Save();
        }

        /// <summary>The analysis again each time the leader starts a lap (it resets the cars ticked on the chart).</summary>
        private void Refresh()
        {
            int laps = LiveField.LeaderLaps;
            if (laps == _builtAt) return;
            var field = LiveField.Snapshot();
            if (field == null) return;
            _builtAt = laps;
            double offset = _scroll.VerticalOffset;
            try { _scroll.Content = new RaceFieldView(field); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TeamPace] Pitwall analysis: " + ex.Message);
                Placeholder("Analyse indisponible pour l'instant : " + ex.Message);
            }
            Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
        }

        private void Placeholder(string text)
        {
            _scroll.Content = new TextBlock { Text = text, Foreground = Ui.Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 10, 4, 10) };
        }
    }
}
