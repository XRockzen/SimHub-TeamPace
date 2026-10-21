using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace User.TeamPace
{
    // ── Converter : "" → Collapsed, non-empty → Visible ─────────────────────
    public class VisibleWhenNotEmptyConverter : IValueConverter
    {
        public static readonly VisibleWhenNotEmptyConverter Instance = new VisibleWhenNotEmptyConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        { throw new NotSupportedException(); }
    }

    /// <summary>
    /// Main screen: three tabs (career face to face, history, strategy), a circuit page with
    /// progression, telemetry and work plan, and the session analysis, with a back stack, the
    /// team notifications bell and a folding help per screen.
    /// </summary>
    public partial class TeamPaceControl : UserControl
    {
        private enum Screen { Career, History, Strategy, Circuit, Analysis, Overlay }
        private enum CircuitTab { Progress, Telemetry, Work }

        private class NavState
        {
            public Screen Screen;
            public CircuitTab Tab;
            public string Circuit;
            public string ClassLabel;
            public Dictionary<string, LmuSessionRecord> Sessions;   // analysis: driver key → session
            public string Title;      // analysis: circuit name
            public string Subtitle;   // analysis: "Course du 20/09/26 · 18:50"
            public string Crumb;      // short label in the breadcrumb
            public string BadgeClass; // raw car class for the title badge
            public List<TelemetryView.Preset> Presets;   // telemetry: laps shown, kept up to date by the view
            public double ZoomFrom = double.NaN, ZoomTo = double.NaN;
            public bool RaceTab;      // session analysis: the "Course" tab
        }

        private ProgressionChart _progressChart;

        // Kept across control re-creations (SimHub rebuilds the settings page)
        private static readonly List<string> SelectedKeys = new List<string> { "" };

        private readonly TeamPacePlugin _plugin;
        private readonly DispatcherTimer _loadTimer;
        private bool _lmuSettingsDirty;
        private bool _helpOpen;
        private static bool _progressChartOn;   // the progression chart is folded until asked for

        private List<DriverView> _drivers = new List<DriverView>();
        private NavState _state;
        private readonly Stack<NavState> _back = new Stack<NavState>();

        private readonly HashSet<string> _activeClasses = new HashSet<string>();

        // Content currently shown
        private readonly List<SessionListColumn> _listCols     = new List<SessionListColumn>();
        private readonly List<AnalysisColumn>    _analysisCols = new List<AnalysisColumn>();
        private CareerFaceView _career;
        private bool _populatingDrivers;

        public TeamPaceControl(TeamPacePlugin plugin)
        {
            _plugin = plugin;
            InitializeComponent();

            TxtLmuPlayer.Text = LmuSessionStore.PlayerName  ?? "";
            TxtLmuPath.Text   = LmuSessionStore.ResultsPath ?? "";
            if (string.IsNullOrEmpty(TxtLmuPath.Text))
                TxtLmuPath.Text = LmuXmlParser.AutoDiscoverResultsPath();
            TxtTeamFolder.Text = LmuSessionStore.TeamFolder;
            _lmuSettingsDirty = false;

            // Settings stay open until the player name is known, then fold away
            SetSettingsOpen(string.IsNullOrWhiteSpace(LmuSessionStore.PlayerName));

            // Mouse "back" button and Alt+Left go back, like in a browser
            PreviewMouseDown += (s, e) => { if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; } };
            PreviewKeyDown   += (s, e) =>
            {
                if (e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
                { GoBack(); e.Handled = true; }
            };

            RebuildDrivers();
            Navigate(new NavState { Screen = Screen.Career, Crumb = "Carrière" }, false);

            TxtVersion.Text = "TeamPace " + PluginUpdater.Format(PluginUpdater.CurrentVersion);
            Loaded += (s, e) =>
            {
                PluginUpdater.Changed += OnUpdateChanged;
                TelemetryService.StatusChanged += OnTelemetryStatus;
                NotificationService.Arrived += OnNotificationsArrived;
                NotificationService.Changed += OnNotificationsChanged;
                RefreshUpdateBanner();
                RefreshBell();
                ShowPendingToasts();
                CheckTeamFolder();
            };
            Unloaded += (s, e) =>
            {
                PluginUpdater.Changed -= OnUpdateChanged;
                TelemetryService.StatusChanged -= OnTelemetryStatus;
                NotificationService.Arrived -= OnNotificationsArrived;
                NotificationService.Changed -= OnNotificationsChanged;
            };

            _loadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _loadTimer.Tick += LoadTimer_Tick;
            if (_plugin.IsImporting) _loadTimer.Start();
        }

        private void LoadTimer_Tick(object sender, EventArgs e)
        {
            if (_plugin.IsImporting) return;
            _loadTimer.Stop();
            ReloadData();
        }

        private void ReloadData()
        {
            RebuildDrivers();
            Render();
        }

        // ═══ Drivers ══════════════════════════════════════════════════════════

        private void RebuildDrivers()
        {
            var available = LmuTeamSync.GetDrivers();
            var byKey = new Dictionary<string, TeamDriver>();
            foreach (var d in available) byKey[d.DbPath ?? ""] = d;

            SelectedKeys.RemoveAll(k => !byKey.ContainsKey(k));
            if (SelectedKeys.Count == 0) SelectedKeys.Add("");

            _drivers = new List<DriverView>();
            for (int i = 0; i < SelectedKeys.Count; i++)
                _drivers.Add(new DriverView(byKey[SelectedKeys[i]], DriverView.Palette[i % DriverView.Palette.Length]));

            DriverChips.Children.Clear();
            foreach (var d in _drivers) DriverChips.Children.Add(BuildChip(d));

            _populatingDrivers = true;
            var choices = new List<object>();
            choices.Add(SelectedKeys.Count >= DriverView.MaxDrivers ? "4 pilotes maximum" : "+ Ajouter un pilote…");
            foreach (var d in available)
                if (!SelectedKeys.Contains(d.DbPath ?? "")) choices.Add(d);
            CmbAddDriver.ItemsSource = choices;
            CmbAddDriver.SelectedIndex = 0;
            CmbAddDriver.IsEnabled = choices.Count > 1 && SelectedKeys.Count < DriverView.MaxDrivers;
            _populatingDrivers = false;
        }

        private UIElement BuildChip(DriverView d)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = d.Brush, Margin = new Thickness(0, 0, 5, 0),
                                             VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = d.DbPath == null ? d.Name + " (moi)" : d.Name,
                                               FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            if (_drivers.Count > 1)
            {
                var close = new TextBlock { Text = "  ✕", FontSize = 11, Opacity = 0.6, Cursor = Cursors.Hand,
                                            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Retirer ce pilote" };
                close.MouseLeftButtonDown += (s, e) =>
                {
                    SelectedKeys.Remove(d.Key);
                    RebuildDrivers();
                    RefreshForDriverChange();
                    e.Handled = true;
                };
                panel.Children.Add(close);
            }
            return new Border
            {
                Child = panel,
                BorderBrush = d.Brush, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(30, d.Brush.Color.R, d.Brush.Color.G, d.Brush.Color.B)),
                CornerRadius = new CornerRadius(11), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0),
            };
        }

        private void CmbAddDriver_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_populatingDrivers) return;
            var d = CmbAddDriver.SelectedItem as TeamDriver;
            if (d == null || SelectedKeys.Count >= DriverView.MaxDrivers) return;
            SelectedKeys.Add(d.DbPath ?? "");
            RebuildDrivers();
            RefreshForDriverChange();
        }

        /// <summary>In an analysis, newly added drivers get their matching session if they have one.</summary>
        private void RefreshForDriverChange()
        {
            if (_state != null && _state.Screen == Screen.Analysis)
            {
                LmuSessionRecord any = null;
                foreach (var s in _state.Sessions.Values) { any = s; break; }
                if (any != null)
                    foreach (var d in _drivers)
                        if (!_state.Sessions.ContainsKey(d.Key))
                        {
                            var m = d.FindMatch(any);
                            if (m != null) _state.Sessions[d.Key] = m;
                        }
            }
            else if (_state != null && _state.Presets != null)
            {
                // Removed drivers lose their laps, added ones get their best lap on the circuit
                _state.Presets.RemoveAll(p => !_drivers.Exists(d => d.Key == p.DriverKey));
                foreach (var d in _drivers)
                    if (!_state.Presets.Exists(p => p.DriverKey == d.Key) && _state.Presets.Count < TelemetryView.MaxSlots)
                        _state.Presets.Add(new TelemetryView.Preset { DriverKey = d.Key });
            }
            Render();
        }

        // ═══ Navigation ═══════════════════════════════════════════════════════

        private void Navigate(NavState state, bool push)
        {
            if (push && _state != null) _back.Push(_state);
            _state = state;
            Render();
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) { GoBack(); }

        private void GoBack()
        {
            if (_back.Count == 0) return;
            _state = _back.Pop();
            Render();
        }

        /// <summary>Jumps back to the i-th breadcrumb level (0 = root of the stack).</summary>
        private void GoToCrumb(int index)
        {
            var path = new List<NavState>(_back);
            path.Reverse();                       // oldest first
            if (index < 0 || index >= path.Count) return;
            _back.Clear();
            for (int i = 0; i < index; i++) _back.Push(path[i]);
            _state = path[index];
            Render();
        }

        private void OpenTab(Screen screen, string crumb)
        {
            _back.Clear();
            Navigate(new NavState { Screen = screen, Crumb = crumb }, false);
        }

        private void BtnTabCareer_Click(object sender, RoutedEventArgs e)   { OpenTab(Screen.Career, "Carrière"); }
        private void BtnTabHistory_Click(object sender, RoutedEventArgs e)  { OpenTab(Screen.History, "Historique"); }
        private void BtnTabStrategy_Click(object sender, RoutedEventArgs e) { OpenTab(Screen.Strategy, "Stratégie"); }
        private void BtnTabOverlay_Click(object sender, RoutedEventArgs e)  { OpenTab(Screen.Overlay, "Overlay"); }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SetSettingsOpen(SettingsPanel.Visibility != Visibility.Visible);
        }

        private void SetSettingsOpen(bool open)
        {
            SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            BtnSettings.Content = open ? "⚙ Masquer les réglages" : "⚙ Réglages";
        }

        /// <summary>Page title, subtitle, optional category badge and clickable breadcrumb.</summary>
        private void SetHeader(string title, string subtitle, string carClass)
        {
            TxtPageTitle.Text    = title;
            TxtPageSubtitle.Text = subtitle ?? "";
            if (string.IsNullOrEmpty(carClass))
                PageBadge.Visibility = Visibility.Collapsed;
            else
            {
                PageBadge.Visibility = Visibility.Visible;
                PageBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(LmuCategoryStyle.GetBg(carClass)));
                TxtPageBadge.Text    = LmuCategoryStyle.GetShortName(carClass);
            }

            BreadcrumbPanel.Children.Clear();
            var path = new List<NavState>(_back);
            path.Reverse();
            BreadcrumbPanel.Visibility = path.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < path.Count; i++)
            {
                int index = i;
                var link = new TextBlock { Text = path[i].Crumb ?? "…", Style = (Style)FindResource("CrumbLink") };
                link.MouseLeftButtonDown += (s, e) => { GoToCrumb(index); e.Handled = true; };
                BreadcrumbPanel.Children.Add(link);
                BreadcrumbPanel.Children.Add(new TextBlock { Text = "  ›  ", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)) });
            }
            BreadcrumbPanel.Children.Add(new TextBlock { Text = _state.Crumb ?? title, FontSize = 13,
                                                         Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)) });
        }

        private void SetTopStatus(string text)
        {
            TxtTopStatus.Text = text ?? "";
            TxtTopStatus.Foreground = TxtTopStatus.Text.StartsWith("⚠") ? Ui.Amber : Ui.Frozen(Color.FromArgb(0x99, 255, 255, 255));
        }

        private void OpenAnalysis(DriverView driver, LmuSessionRecord session)
        {
            var map = new Dictionary<string, LmuSessionRecord>();
            map[driver.Key] = session;
            foreach (var d in _drivers)
            {
                if (d.Key == driver.Key) continue;
                var m = d.FindMatch(session);
                if (m != null) map[d.Key] = m;
            }
            var when = session.SessionDate != DateTime.MinValue ? session.SessionDate.ToString("dd/MM/yy · HH:mm") : "?";
            Navigate(new NavState
            {
                Screen     = Screen.Analysis,
                Sessions   = map,
                Circuit    = session.TrackDisplayName,
                ClassLabel = LmuCategoryStyle.GetShortName(session.CarClass),
                Title      = session.TrackDisplayName,
                Subtitle   = string.Format("{0} du {1}{2}", session.SessionTypeLong, when,
                                           map.Count > 1 ? string.Format("  ·  session commune à {0} pilotes", map.Count) : ""),
                Crumb      = string.Format("{0} {1}", session.SessionTypeLong,
                                           session.SessionDate != DateTime.MinValue ? session.SessionDate.ToString("dd/MM") : ""),
                BadgeClass = session.CarClass,
            }, true);
        }

        private void OpenCircuit(string circuit, string classLabel, string rawClass, CircuitTab tab, bool push)
        {
            Navigate(new NavState
            {
                Screen = Screen.Circuit, Tab = tab, Circuit = circuit, ClassLabel = classLabel, BadgeClass = rawClass,
                Crumb = ShortCircuit(circuit) + " · " + classLabel,
            }, push);
        }

        // ═══ Rendering ════════════════════════════════════════════════════════

        private void Render()
        {
            if (_state == null) return;
            foreach (var d in _drivers) d.Invalidate();

            ColumnsHost.Children.Clear();
            ColumnsHost.ColumnDefinitions.Clear();
            _listCols.Clear();
            _analysisCols.Clear();
            _career = null;
            HeaderActions.Children.Clear();
            if (_progressChart != null) { _progressChart.Detach(); _progressChart = null; }

            BtnBack.Visibility = _back.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // The tab the current path started from stays highlighted
            var rootScreen = _back.Count > 0 ? new List<NavState>(_back)[_back.Count - 1].Screen : _state.Screen;
            SetTabActive(BtnTabCareer,   rootScreen == Screen.Career || rootScreen == Screen.Circuit);
            SetTabActive(BtnTabHistory,  rootScreen == Screen.History || rootScreen == Screen.Analysis);
            SetTabActive(BtnTabStrategy, rootScreen == Screen.Strategy);
            SetTabActive(BtnTabOverlay,  rootScreen == Screen.Overlay);

            ClassFilterBar.Visibility     = _state.Screen == Screen.Career ? Visibility.Visible : Visibility.Collapsed;
            CircuitTabs.Visibility        = _state.Screen == Screen.Circuit ? Visibility.Visible : Visibility.Collapsed;
            AnalysisTabs.Visibility       = Visibility.Collapsed;
            BtnChart.Visibility           = _state.Screen == Screen.Circuit && _state.Tab == CircuitTab.Progress ? Visibility.Visible : Visibility.Collapsed;
            Ui.SetOn(BtnChart, _progressChartOn);
            ProgressChartPanel.Visibility = _state.Screen == Screen.Circuit && _state.Tab == CircuitTab.Progress && _progressChartOn ? Visibility.Visible : Visibility.Collapsed;
            TxtLegendTeam.Visibility      = _drivers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            TxtStatus.Text = "";

            switch (_state.Screen)
            {
                case Screen.Career:   RenderCareer();   break;
                case Screen.History:  RenderHistory();  break;
                case Screen.Strategy: RenderStrategy(); break;
                case Screen.Overlay:
                    SetHeader("Overlay", null, null);
                    Fill(new OverlaySettingsView(_drivers));
                    break;
                case Screen.Circuit:  RenderCircuit();  break;
                case Screen.Analysis: RenderAnalysis(); break;
            }
            UpdateHelp();
        }

        private static void SetTabActive(Button b, bool active)
        {
            b.BorderBrush = active
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF2196F3"))
                : Brushes.Transparent;
            b.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromArgb(166, 255, 255, 255));
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void Fill(UIElement content)
        {
            ColumnsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnsHost.Children.Add(content);
        }

        /// <summary>Adds an equal-width column with the driver's colour bar and name above the content.</summary>
        private void AddColumn(DriverView d, UIElement content, string subtitle)
        {
            int index = ColumnsHost.ColumnDefinitions.Count;
            if (index > 0)
                ColumnsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            ColumnsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            header.Children.Add(new TextBlock { Text = d.DbPath == null ? d.Name + "  (moi)" : d.Name,
                                                FontSize = 18, FontWeight = FontWeights.Bold, Foreground = d.Brush });
            if (!string.IsNullOrEmpty(subtitle))
                header.Children.Add(new TextBlock { Text = "   " + subtitle, FontSize = 13, Opacity = 0.6,
                                                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });

            var dock = new DockPanel();
            var bar = new Border { Height = 3, Background = d.Brush, CornerRadius = new CornerRadius(1.5) };
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(header);
            dock.Children.Add(content);

            Grid.SetColumn(dock, ColumnsHost.ColumnDefinitions.Count - 1);
            ColumnsHost.Children.Add(dock);
        }

        // ─── Career ───────────────────────────────────────────────────────────

        private void RenderCareer()
        {
            SetHeader("Carrière", null, null);
            _career = new CareerFaceView(_drivers, _activeClasses);
            _career.CircuitOpened += (circuit, label, raw) => OpenCircuit(circuit, label, raw, CircuitTab.Progress, true);
            Fill(_career);
            PopulateClassFilter(_career.ClassSamples());
        }

        private void PopulateClassFilter(List<LmuCircuitBestRow> seen)
        {
            ClassFilterBar.Children.Clear();
            var labels = new HashSet<string>();
            foreach (var r in seen) labels.Add(r.CarClassLabel);
            _activeClasses.IntersectWith(labels);

            if (seen.Count <= 1) return;
            ClassFilterBar.Children.Add(new TextBlock { Text = "Catégories :", FontSize = 11.5, Opacity = 0.6,
                                                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
            foreach (var r in seen)
            {
                var label = r.CarClassLabel;
                var tag = new Border
                {
                    CornerRadius = new CornerRadius(3), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 5, 4),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(r.CarClassBg)),
                    BorderBrush = r.CarClassThick == "1"
                        ? (Brush)new SolidColorBrush((Color)ColorConverter.ConvertFromString(r.CarClassBorder))
                        : Brushes.Transparent,
                    BorderThickness = r.CarClassThick == "1" ? new Thickness(1) : new Thickness(0),
                    Cursor = Cursors.Hand, ToolTip = "Afficher ou masquer cette catégorie",
                    Opacity = _activeClasses.Count == 0 || _activeClasses.Contains(label) ? 1.0 : 0.28,
                    Child = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
                };
                tag.MouseLeftButtonDown += (s, e) =>
                {
                    if (!_activeClasses.Remove(label)) _activeClasses.Add(label);
                    foreach (UIElement child in ClassFilterBar.Children)
                    {
                        var b = child as Border;
                        if (b == null) continue;
                        var l = ((TextBlock)b.Child).Text;
                        b.Opacity = _activeClasses.Count == 0 || _activeClasses.Contains(l) ? 1.0 : 0.28;
                    }
                    if (_career != null) _career.ApplyClassFilter();
                };
                ClassFilterBar.Children.Add(tag);
            }
        }

        // ─── History ──────────────────────────────────────────────────────────

        private void RenderHistory()
        {
            SetHeader("Historique", null, null);
            var view = new HistoryView(_drivers);
            view.SessionOpened += OpenAnalysis;
            Fill(view);
            if (string.IsNullOrEmpty(LmuSessionStore.PlayerName))
                TxtStatus.Text = "Renseignez votre nom de joueur dans les réglages, puis cliquez sur ⟳ Tout mettre à jour";
        }

        // ─── Strategy ─────────────────────────────────────────────────────────

        private void RenderStrategy()
        {
            SetHeader("Stratégie", null, null);
            Fill(new StrategyView(_drivers, _state.Circuit, _state.ClassLabel));
        }

        // ─── Circuit page ─────────────────────────────────────────────────────

        private void BtnChart_Click(object sender, RoutedEventArgs e)
        {
            _progressChartOn = !_progressChartOn;
            Render();
        }

        private void AnalysisTab_Click(object sender, RoutedEventArgs e)
        {
            bool race = (string)((Button)sender).Tag == "Race";
            if (race == _state.RaceTab) return;
            _state.RaceTab = race;
            Render();
        }

        private void CircuitTab_Click(object sender, RoutedEventArgs e)
        {
            var tab = (CircuitTab)Enum.Parse(typeof(CircuitTab), (string)((Button)sender).Tag);
            if (tab == _state.Tab) return;
            _state.Tab = tab;
            Render();
        }

        private void RenderCircuit()
        {
            if (_state.BadgeClass == null)
                foreach (var d in _drivers)
                    foreach (var s in d.Sessions)
                        if (s.TrackDisplayName == _state.Circuit && LmuCategoryStyle.GetShortName(s.CarClass) == _state.ClassLabel) { _state.BadgeClass = s.CarClass; break; }
            SetHeader(_state.Circuit, null, _state.BadgeClass);
            SetTabActive(BtnCtProgress,  _state.Tab == CircuitTab.Progress);
            SetTabActive(BtnCtTelemetry, _state.Tab == CircuitTab.Telemetry);
            SetTabActive(BtnCtWork,      _state.Tab == CircuitTab.Work);

            switch (_state.Tab)
            {
                case CircuitTab.Progress:  RenderProgress(); break;
                case CircuitTab.Telemetry: RenderTelemetry(); break;
                case CircuitTab.Work:      RenderWork(); break;
            }
        }

        private void RenderTelemetry()
        {
            if (_state.Presets == null)
            {
                _state.Presets = new List<TelemetryView.Preset>();
                foreach (var d in _drivers) _state.Presets.Add(new TelemetryView.Preset { DriverKey = d.Key });
            }
            var view = new TelemetryView(_drivers, _state.Circuit, _state.Presets, _state.ZoomFrom, _state.ZoomTo);
            _state.ZoomFrom = _state.ZoomTo = double.NaN;   // only on arrival
            Fill(view);
        }

        private void RenderWork()
        {
            var view = new CoachingView(_drivers, _state.Circuit, _state.ClassLabel);
            view.TelemetryRequested += (presets, from, to) => Navigate(new NavState
            {
                Screen = Screen.Circuit, Tab = CircuitTab.Telemetry, Circuit = _state.Circuit, ClassLabel = _state.ClassLabel,
                BadgeClass = _state.BadgeClass, Presets = presets, ZoomFrom = from, ZoomTo = to, Crumb = "Télémétrie",
            }, true);
            Fill(view);
        }

        private void AddListColumn(DriverView d, List<LmuSessionRow> rows, bool progressMode, string subtitle)
        {
            var col = new SessionListColumn(d);
            col.Load(rows, progressMode);
            col.SessionSelected += (drv, rec) =>
            {
                if (rec == null) return;
                foreach (var c in _listCols) if (c != col) c.SelectMatching(rec);
                if (_progressChart != null) _progressChart.Highlight(rec);
            };
            col.SessionOpened += OpenAnalysis;
            _listCols.Add(col);
            AddColumn(d, col, subtitle);
        }

        private List<Brush> CommonColorsFor(DriverView owner, LmuSessionRecord s)
        {
            var list = new List<Brush>();
            foreach (var other in _drivers)
                if (other != owner && other.FindMatch(s) != null) list.Add(other.Brush);
            return list;
        }

        private void RenderProgress()
        {
            // Sessions of each driver on this circuit × class, oldest first
            var perDriver = new List<List<LmuSessionRecord>>();
            foreach (var d in _drivers)
            {
                var matching = new List<LmuSessionRecord>();
                foreach (var s in d.Sessions)
                {
                    if (s.BestLapTime.Ticks <= 0) continue;
                    if (!string.Equals(s.TrackDisplayName, _state.Circuit, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(LmuCategoryStyle.GetShortName(s.CarClass), _state.ClassLabel, StringComparison.OrdinalIgnoreCase)) continue;
                    matching.Add(s);
                }
                matching.Sort((a, b) => a.SessionDate.CompareTo(b.SessionDate));
                perDriver.Add(matching);
            }

            long teamBest = long.MaxValue;
            foreach (var list in perDriver)
                foreach (var s in list) if (s.BestLapTime.Ticks < teamBest) teamBest = s.BestLapTime.Ticks;

            var series = new List<ProgressionChart.Series>();
            for (int i = 0; i < _drivers.Count; i++)
            {
                var d = _drivers[i];
                var matching = perDriver[i];
                var common = new HashSet<LmuSessionRecord>();

                long best = long.MaxValue;
                foreach (var s in matching) if (s.BestLapTime.Ticks < best) best = s.BestLapTime.Ticks;

                var rows = new List<LmuSessionRow>();
                LmuSessionRecord prev = null;
                foreach (var s in matching)
                {
                    string delta = "—", fg = "#DDFFFFFF";
                    if (prev != null)
                    {
                        var diff = s.BestLapTime - prev.BestLapTime;
                        if (Math.Abs(diff.TotalSeconds) < 0.001) delta = "=";
                        else { delta = Fmt.Signed(diff); fg = diff.Ticks < 0 ? "#FF4CAF50" : "#FFEF5350"; }
                    }
                    var row = new LmuSessionRow
                    {
                        Record = s, IsPersonalBest = s.BestLapTime.Ticks == best,
                        Delta = delta, DeltaFg = fg, CommonColors = CommonColorsFor(d, s),
                    };
                    if (row.IsCommon) common.Add(s);
                    rows.Add(row);
                    prev = s;
                }
                rows.Reverse();   // newest first, delta still against the previous outing

                AddListColumn(d, rows, true, null);
                _listCols[_listCols.Count - 1].SetSummary(BuildProgressSummary(matching, best, teamBest));
                series.Add(new ProgressionChart.Series { Driver = d, Sessions = matching, IsCommon = common.Contains });
            }

            _progressChart = new ProgressionChart();
            _progressChart.PointSelected += (drv, rec) =>
            {
                foreach (var c in _listCols)
                {
                    if (c.Driver == drv) c.SelectSession(rec);
                    else c.SelectMatching(rec);
                }
                _progressChart.Highlight(rec);
            };
            _progressChart.PointOpened += OpenAnalysis;
            _progressChart.Attach(ProgressCanvas, series);
        }

        private List<SessionListColumn.SummaryItem> BuildProgressSummary(List<LmuSessionRecord> sessions, long best, long teamBest)
        {
            var items = new List<SessionListColumn.SummaryItem>();
            if (sessions.Count == 0)
            {
                items.Add(new SessionListColumn.SummaryItem { Label = "SESSIONS", Value = "0", Detail = "jamais roulé ici", Color = "#F2FFFFFF" });
                return items;
            }
            var driving = TimeSpan.Zero; int laps = 0;
            foreach (var s in sessions) { driving = driving + CareerColumn.SessionDuration(s); laps += s.TotalLaps; }
            items.Add(new SessionListColumn.SummaryItem
            {
                Label = "RECORD", Value = (_drivers.Count > 1 && best == teamBest ? "★ " : "") + Fmt.Lap(TimeSpan.FromTicks(best)), Color = "#FF66BB6A", Detail = "",
            });
            items.Add(new SessionListColumn.SummaryItem { Label = "ROULAGE", Value = Fmt.TotalTime(driving), Color = "#FF80DEEA", Detail = "" });
            items.Add(new SessionListColumn.SummaryItem { Label = "SESSIONS", Value = sessions.Count.ToString(), Color = "#F2FFFFFF", Detail = "" });
            items.Add(new SessionListColumn.SummaryItem { Label = "TOURS", Value = laps.ToString(), Color = "#F2FFFFFF", Detail = "" });
            return items;
        }

        private static string ShortCircuit(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Circuit";
            int dash = name.IndexOf(" – ", StringComparison.Ordinal);
            var s = dash > 0 ? name.Substring(0, dash) : name;
            return s.Length > 28 ? s.Substring(0, 27) + "…" : s;
        }

        // ─── Analysis ─────────────────────────────────────────────────────────

        private void RenderAnalysis()
        {
            SetHeader(_state.Title, _state.Subtitle, _state.BadgeClass);
            LmuSessionRecord any = null;
            foreach (var x in _state.Sessions.Values) { any = x; break; }
            bool isRace = any != null && any.IsRace;
            AnalysisTabs.Visibility = isRace ? Visibility.Visible : Visibility.Collapsed;
            if (!isRace) _state.RaceTab = false;
            SetTabActive(BtnAtLaps, !_state.RaceTab);
            SetTabActive(BtnAtRace, _state.RaceTab);

            if (_state.RaceTab)
            {
                // The race as the car lived it: against the team's plan, then every car
                var plan = RaceReviewView.PlanFor(_state.Circuit, _state.ClassLabel);
                var review = new RaceReviewView(_drivers, any, plan);
                Fill(new ScrollViewer { Content = review, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) });
                return;
            }

            var tele = Ui.Button("Télémétrie →", "Les tours de la séance superposés : vitesse, freinage, accélérateur, trajectoires");
            tele.Click += (s, e) => OpenTelemetryFromAnalysis(null, 0);
            HeaderActions.Children.Add(tele);

            foreach (var d in _drivers)
            {
                LmuSessionRecord s;
                if (_state.Sessions.TryGetValue(d.Key, out s))
                {
                    var col = new AnalysisColumn(d, s);
                    col.StatsChanged += c => UpdateAverageReferences();
                    col.LapOpened    += (c, lapNum) => OpenTelemetryFromAnalysis(c.Driver.Key, lapNum);
                    _analysisCols.Add(col);
                    // The column scrolls as a whole, so the lap table stays reachable under tall blocks
                    var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = col };
                    AddColumn(d, scroll, null);
                }
                else
                {
                    AddColumn(d, new TextBlock
                    {
                        Text = "Ce pilote n'a pas participé à cette session.",
                        Opacity = 0.5, FontStyle = FontStyles.Italic, FontSize = 13, Margin = new Thickness(0, 30, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    }, null);
                }
            }

            bool fuel = false, ve = false, tyres = false, pits = false;
            foreach (var c in _analysisCols)
            {
                fuel |= c.HasFuel; ve |= c.HasVe; tyres |= c.HasTyres; pits |= c.HasPits;
            }
            foreach (var c in _analysisCols) c.SetOptionalBlocks(fuel, ve, tyres, pits);
            int maxStops = 0;
            foreach (var c in _analysisCols) maxStops = Math.Max(maxStops, c.PitCount);
            foreach (var c in _analysisCols) c.SetPitBlockStops(maxStops);

            if (_analysisCols.Count > 1)
            {
                var bestByLap = new Dictionary<int, long>();
                foreach (var c in _analysisCols)
                    foreach (var r in c.Rows)
                    {
                        if (!r.IsValid || r.Lap.LapTimeTicks <= 0) continue;
                        long b;
                        if (!bestByLap.TryGetValue(r.LapNum, out b) || r.Lap.LapTimeTicks < b) bestByLap[r.LapNum] = r.Lap.LapTimeTicks;
                    }
                foreach (var c in _analysisCols) c.SetTeamDeltas(bestByLap);
            }
            else foreach (var c in _analysisCols) c.SetTeamDeltas(null);

            UpdateAverageReferences();
        }

        /// <summary>Every car of the race, from whichever displayed driver's database has it.</summary>
        private RaceField FieldOf(Dictionary<string, LmuSessionRecord> sessions)
        {
            foreach (var d in _drivers)
            {
                LmuSessionRecord s;
                if (!sessions.TryGetValue(d.Key, out s) || !s.IsRace) continue;
                var f = LmuSessionStore.GetField(s.FileId, d.DbPath);
                if (f != null && f.Cars.Count > 1) return f;
            }
            // Shared by a crew member in the team folder
            foreach (var s in sessions.Values)
            {
                var f = TeamRaceStore.FieldFor(s);
                if (f != null && f.Cars.Count > 1) return f;
            }
            return null;
        }

        private void UpdateAverageReferences()
        {
            AnalysisColumn best = null;
            if (_analysisCols.Count > 1)
                foreach (var c in _analysisCols)
                    if (c.AverageTicks > 0 && (best == null || c.AverageTicks < best.AverageTicks)) best = c;
            foreach (var c in _analysisCols)
                c.SetAverageReference(best != null ? best.AverageTicks : 0, best != null ? best.Driver.Name : null);
        }

        /// <summary>From a session: each driver's best lap of that session, or the given lap for one driver.</summary>
        private void OpenTelemetryFromAnalysis(string driverKey, int lapNum)
        {
            var presets = new List<TelemetryView.Preset>();
            foreach (var d in _drivers)
            {
                LmuSessionRecord s;
                if (!_state.Sessions.TryGetValue(d.Key, out s)) { presets.Add(new TelemetryView.Preset { DriverKey = d.Key }); continue; }
                var p = new TelemetryView.Preset { DriverKey = d.Key, SessionFileId = s.FileId, LapNum = d.Key == driverKey ? lapNum : 0 };
                if (d.Key == driverKey) presets.Insert(0, p);   // the double-clicked lap is the reference
                else presets.Add(p);
            }
            Navigate(new NavState
            {
                Screen = Screen.Circuit, Tab = CircuitTab.Telemetry, Circuit = _state.Circuit, ClassLabel = _state.ClassLabel,
                BadgeClass = _state.BadgeClass, Presets = presets, Crumb = "Télémétrie",
            }, true);
        }

        private void OnTelemetryStatus()
        {
            Dispatcher.BeginInvoke(new Action(() => SetTopStatus(TelemetryService.Status)));
        }

        // ═══ Aide ═════════════════════════════════════════════════════════════

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            _helpOpen = !_helpOpen;
            UpdateHelp();
        }

        private void UpdateHelp()
        {
            Ui.SetOn(BtnHelp, _helpOpen);
            HelpPanel.Visibility = _helpOpen ? Visibility.Visible : Visibility.Collapsed;
            AnalysisLegend.Visibility = _helpOpen && _state.Screen == Screen.Analysis && !_state.RaceTab ? Visibility.Visible : Visibility.Collapsed;
            TxtHelp.Text = HelpText();
        }

        private string HelpText()
        {
            switch (_state.Screen)
            {
                case Screen.Career:
                    return "Une ligne par circuit et catégorie : le record de chaque pilote ; le plus rapide en vert (★), l'écart des autres en rouge. Les étiquettes de catégorie filtrent le tableau.\n" +
                           "Clic : le circuit ressort dans l'activité. Double-clic : la page du circuit (progression, télémétrie, plan de travail).";
                case Screen.History:
                    return "Une ligne par session, groupées par jour. Une session courue par plusieurs pilotes n'occupe qu'une ligne, avec le résultat de chacun. Mode : hors ligne, en ligne (seul dans la voiture) ou équipage (voiture partagée).\n" +
                           "Double-clic : l'analyse de la session. Les sessions sans tour chronométré sont masquées.";
                case Screen.Strategy:
                    return "Préparation : l'énergie d'abord, le plein à chaque arrêt et le complément au dernier. Glissez un arrêt (carré jaune) pour le déplacer ; glissez un pilote sur un relais, ou cliquez le relais. Chaque arrêt se règle sur sa carte : le niveau d'énergie voulu à la sortie des stands et les pneus.\n" +
                           "Analyse de course : une course réelle face au plan, puis toutes les voitures. Données mesurées : une valeur saisie passe en jaune ; effacez-la pour revenir à la mesure.";
                case Screen.Overlay:
                    return "Un seul overlay, discret, au-dessus du jeu : quatre pages (Télémétrie, Coaching, Course, Plan de travail) qu'on fait défiler avec les boutons du volant, associés dans la section « Boutons du volant » (Cliquer pour configurer).\n" +
                           "« Placer l'overlay » le montre à l'écran : glissez-le, la molette change sa taille. Le jeu doit être en fenêtré ou en plein écran fenêtré.";
                case Screen.Analysis:
                    if (_state.RaceTab)
                        return "Prévu / réel : le plan de l'équipe pour ce circuit face à la course ; un arrêt réel est aussi large que le temps qu'il a coûté. Les arrêts des autres voitures sont repérés au temps perdu (40 s ou plus) : LMU n'écrit ni leur énergie ni leurs pneus.\n" +
                               "Toutes les voitures : clic sur une ligne pour en faire la référence de « Où le temps est parti » ; les cases choisissent les voitures de la courbe d'écarts (plus bas = plus loin du premier) ; survol ou clic sur un nom à droite : sa courbe en évidence.";
                    return "Cochez ou décochez des tours : les statistiques se recalculent. « Rythme » coche les tours représentatifs. ▾ replie les statistiques ou le tableau.\n" +
                           "Double-clic sur un tour : sa télémétrie. Au survol du théorique, des invalides et des arrêts : le détail. Repères et couleurs ci-dessous.";
                default:
                    if (_state.Tab == CircuitTab.Progress)
                        return "Les séances de chaque pilote sur ce circuit, la plus récente en haut ; Δ : écart avec sa séance précédente. Clic : la séance ressort chez tous ; double-clic : son analyse.\n" +
                               "« Courbe » affiche le meilleur tour de chaque séance (plus bas = plus rapide, ★ = record, anneau doré = séance commune).";
                    if (_state.Tab == CircuitTab.Telemetry)
                        return "Jusqu'à 4 tours superposés ; le premier sert de référence pour l'écart (une courbe qui monte = du temps perdu). Les valeurs sous la souris s'affichent au-dessus des courbes.\n" +
                               "Courbes : molette pour zoomer, glisser pour se déplacer, double-clic pour revoir le tour. Carte : clic sur un virage pour zoomer, ⤢ pour l'agrandir. Anneau rouge : début de freinage, anneau vert : remise des gaz.";
                    return "Le tour idéal met bout à bout le meilleur passage de l'équipe dans chaque virage ; il est le même pour tous. Le passage habituel du pilote choisi (40 tours récents et 10 plus rapides) y est comparé virage par virage.\n" +
                           "Chaque virage est découpé en freinage, corde et sortie : la phase qui coûte le plus est expliquée par les mesures qui diffèrent. Un virage irrégulier devient une priorité de régularité.";
            }
        }

        // ═══ Notifications ════════════════════════════════════════════════════

        private void OnNotificationsChanged()
        {
            Dispatcher.BeginInvoke(new Action(RefreshBell));
        }

        private void OnNotificationsArrived(List<TeamNotification> items)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowToasts(items);
                var ids = new HashSet<int>();
                foreach (var x in items) ids.Add(x.Id);
                Task.Run(() => NotificationService.MarkToasted(ids));
                RefreshBell();
            }));
        }

        /// <summary>News that arrived while the plugin page was closed pop up once on opening.</summary>
        private void ShowPendingToasts()
        {
            var pending = NotificationService.All().FindAll(x => !x.Read && !x.Toasted);
            if (pending.Count == 0) return;
            ShowToasts(pending);
            var ids = new HashSet<int>();
            foreach (var x in pending) ids.Add(x.Id);
            Task.Run(() => NotificationService.MarkToasted(ids));
        }

        private void RefreshBell()
        {
            int n = NotificationService.UnreadCount();
            BellBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtBellCount.Text = n > 99 ? "99+" : n.ToString();
            BtnBell.ToolTip = n > 0 ? string.Format("{0} nouveauté(s) de l'équipe", n) : "Nouveautés de l'équipe";
        }

        private void BtnBell_Click(object sender, RoutedEventArgs e)
        {
            BuildBellList();
            BellPopup.IsOpen = true;
        }

        private void BellPopup_Closed(object sender, EventArgs e)
        {
            Task.Run(() => NotificationService.MarkAllRead());
        }

        private void LnkReadAll_Click(object sender, MouseButtonEventArgs e)
        {
            NotificationService.MarkAllRead();
            BuildBellList();
        }

        private void BuildBellList()
        {
            BellList.Children.Clear();
            var all = NotificationService.All();
            int unread = all.FindAll(x => !x.Read).Count;
            TxtBellTitle.Text = unread > 0 ? string.Format("Nouveautés  ·  {0} non lue(s)", unread) : "Nouveautés";
            if (all.Count == 0)
            {
                BellList.Children.Add(new TextBlock { Text = "Rien de neuf pour l'instant.", Margin = new Thickness(14, 4, 14, 14), Foreground = Ui.Muted, FontSize = 13 });
                return;
            }
            foreach (var n in all) BellList.Children.Add(NotificationRow(n, true));
        }

        private static string Icon(string kind)
        {
            switch (kind)
            {
                case "record":    return "★";
                case "retaken":   return "★";
                case "improved":  return "↗";
                case "win":       return "1";
                case "podium":    return "3";
                case "common":    return "⇄";
                default:          return "∿";
            }
        }

        private static Color IconColor(string kind)
        {
            switch (kind)
            {
                case "record":   return Color.FromRgb(0xEF, 0x8A, 0x63);
                case "retaken":  return Color.FromRgb(0x5A, 0xA6, 0xEE);
                case "win":
                case "podium":   return Color.FromRgb(0x66, 0xBB, 0x6A);
                case "common":   return Color.FromRgb(0xFF, 0xC8, 0x57);
                default:         return Color.FromRgb(0x9F, 0x8F, 0xEF);
            }
        }

        private UIElement NotificationRow(TeamNotification n, bool inList)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            var icon = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Ui.Frozen(IconColor(n.Kind)), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = Icon(n.Kind), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = Ui.Frozen(Color.FromRgb(0x22, 0x23, 0x28)),
                                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            g.Children.Add(icon);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = n.Title, FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = (n.Detail ?? "") + "  ·  " + Ago(n.CreatedUtc), FontSize = 11.5, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var close = new TextBlock { Text = "✕", FontSize = 12, Foreground = Ui.Faint, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right,
                                        ToolTip = inList ? "Supprimer" : "Fermer" };
            Grid.SetColumn(close, 2);
            g.Children.Add(close);

            var row = new Border
            {
                Child = g, Padding = new Thickness(14, 9, 12, 9), Cursor = Cursors.Hand,
                Background = inList && !n.Read ? Ui.Frozen(Color.FromArgb(18, 0x21, 0x96, 0xF3)) : Brushes.Transparent,
                BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            };
            close.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (inList) { NotificationService.Delete(n.Id); BuildBellList(); }
                else ToastHost.Children.Remove(ToastFor(row));
            };
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.Handled) return;
                BellPopup.IsOpen = false;
                OpenNotification(n);
            };
            return row;
        }

        private static FrameworkElement ToastFor(FrameworkElement row)
        {
            FrameworkElement e = row;
            while (e.Parent is FrameworkElement && !(e.Parent is StackPanel && ((StackPanel)e.Parent).Name == "ToastHost")) e = (FrameworkElement)e.Parent;
            return e;
        }

        /// <summary>A notification opens what it is about: the session, or the circuit.</summary>
        private void OpenNotification(TeamNotification n)
        {
            if (!string.IsNullOrEmpty(n.SessionFileId))
            {
                var key = n.DriverKey ?? "";
                if (!SelectedKeys.Contains(key) && SelectedKeys.Count < DriverView.MaxDrivers) { SelectedKeys.Add(key); RebuildDrivers(); }
                var d = _drivers.Find(x => x.Key == key);
                var s = d != null ? d.Sessions.Find(x => x.FileId == n.SessionFileId) : null;
                if (s != null) { OpenAnalysis(d, s); return; }
            }
            if (!string.IsNullOrEmpty(n.Circuit))
                OpenCircuit(n.Circuit, n.ClassLabel, null, CircuitTab.Progress, true);
        }

        /// <summary>Pops the news up at the top right: three at most, the last one sums up the rest.</summary>
        private void ShowToasts(List<TeamNotification> items)
        {
            if (items.Count == 0) return;
            int shown = 0;
            foreach (var n in items)
            {
                if (ToastHost.Children.Count >= 3 || shown == 2 && items.Count > 3) break;
                AddToast(NotificationRow(n, false), IconColor(n.Kind));
                shown++;
            }
            int rest = items.Count - shown;
            if (rest > 0)
            {
                var more = new TextBlock { Text = string.Format("+ {0} autre(s) nouveauté(s) · ouvrir la liste", rest), FontSize = 13, Foreground = Ui.Link,
                                           Margin = new Thickness(14, 10, 14, 10), Cursor = Cursors.Hand };
                more.MouseLeftButtonDown += (s, e) => { ToastHost.Children.Clear(); BuildBellList(); BellPopup.IsOpen = true; };
                AddToast(more, Color.FromRgb(0x5A, 0xA6, 0xEE));
            }
        }

        private void AddToast(UIElement content, Color accent)
        {
            var toast = new Border
            {
                Child = content, Background = Ui.Frozen(Color.FromRgb(0x2C, 0x2D, 0x33)),
                BorderBrush = Ui.Frozen(accent), BorderThickness = new Thickness(4, 1, 1, 1), CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 },
            };
            ToastHost.Children.Add(toast);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            timer.Tick += (s, e) => { timer.Stop(); ToastHost.Children.Remove(toast); };
            timer.Start();
        }

        private static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 1) return "à l'instant";
            if (d.TotalHours < 1) return string.Format("il y a {0} min", (int)d.TotalMinutes);
            if (d.TotalDays < 1) return string.Format("il y a {0} h", (int)d.TotalHours);
            return utc.ToLocalTime().ToString("dd/MM HH:mm");
        }

        // ═══ Mise à jour ══════════════════════════════════════════════════════

        private bool _manualCheck;

        private void OnUpdateChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshUpdateBanner();
                if (!_manualCheck) return;
                _manualCheck = false;
                if (PluginUpdater.LastError != null)
                    TxtStatus.Text = "Vérification impossible : " + PluginUpdater.LastError;
                else if (PluginUpdater.Available == null)
                    TxtStatus.Text = "Vous avez la dernière version (" + PluginUpdater.Format(PluginUpdater.CurrentVersion) + ")";
            }));
        }

        private void RefreshUpdateBanner()
        {
            if (PluginUpdater.PendingRestartVersion != null)
            {
                UpdateBanner.Visibility = Visibility.Visible;
                UpdateButtons.Visibility = Visibility.Collapsed;
                TxtUpdateTitle.Text = "✔ Mise à jour " + PluginUpdater.Format(PluginUpdater.PendingRestartVersion) + " installée";
                TxtUpdateNotes.Text = "Redémarrez SimHub pour l'activer.";
                return;
            }
            var info = PluginUpdater.Available;
            if (info == null || PluginUpdater.Dismissed)
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }
            UpdateBanner.Visibility  = Visibility.Visible;
            UpdateButtons.Visibility = Visibility.Visible;
            BtnUpdateInstall.IsEnabled = true;
            TxtUpdateTitle.Text = string.Format("⬆ Mise à jour {0} disponible  (installée : {1})",
                PluginUpdater.Format(info.Version), PluginUpdater.Format(PluginUpdater.CurrentVersion));
            TxtUpdateNotes.Text = string.IsNullOrEmpty(info.Notes) ? "" : info.Notes.Replace("\r", "");
        }

        private void BtnUpdateInstall_Click(object sender, RoutedEventArgs e)
        {
            BtnUpdateInstall.IsEnabled = false;
            TxtUpdateTitle.Text = "Téléchargement et vérification…";
            PluginUpdater.InstallInBackground(error => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (error == null) { RefreshUpdateBanner(); return; }
                BtnUpdateInstall.IsEnabled = true;
                TxtUpdateTitle.Text = "Échec de la mise à jour";
                TxtUpdateNotes.Text = error;
            })));
        }

        private void BtnUpdateNotes_Click(object sender, RoutedEventArgs e)
        {
            var info = PluginUpdater.Available;
            if (info != null && !string.IsNullOrEmpty(info.HtmlUrl))
                try { System.Diagnostics.Process.Start(info.HtmlUrl); } catch { }
        }

        private void BtnUpdateLater_Click(object sender, RoutedEventArgs e)
        {
            PluginUpdater.Dismissed = true;
            RefreshUpdateBanner();
        }

        private void LnkCheckUpdate_Click(object sender, MouseButtonEventArgs e)
        {
            if (!PluginUpdater.IsConfigured)
            {
                TxtStatus.Text = "Mises à jour automatiques non configurées dans cette version";
                return;
            }
            _manualCheck = true;
            PluginUpdater.Dismissed = false;
            TxtStatus.Text = "Recherche de mise à jour…";
            PluginUpdater.CheckInBackground();
        }

        // ═══ LMU config ═══════════════════════════════════════════════════════

        private void TxtLmuPlayer_TextChanged(object sender, TextChangedEventArgs e) { _lmuSettingsDirty = true; }
        private void TxtLmuPath_TextChanged(object sender, TextChangedEventArgs e)   { _lmuSettingsDirty = true; }

        /// <summary>
        /// The one refresh button: new LMU results, telemetry extraction, then team sharing
        /// (own data out, teammates' data in) and the teammates' news.
        /// </summary>
        private void BtnUpdateAll_Click(object sender, RoutedEventArgs e) { UpdateAll(); }

        private void UpdateAll()
        {
            if (_lmuSettingsDirty)
            {
                LmuSessionStore.SaveSettings(TxtLmuPlayer.Text.Trim(), TxtLmuPath.Text.Trim());
                _lmuSettingsDirty = false;
            }
            if (string.IsNullOrWhiteSpace(LmuSessionStore.PlayerName))
            {
                SetSettingsOpen(true);
                SetTopStatus("Renseignez d'abord votre nom de joueur LMU");
                return;
            }

            BtnUpdateAll.IsEnabled = false;
            Action<string> report = text => Dispatcher.BeginInvoke(new Action(() => SetTopStatus(text)));
            Task.Run(() =>
            {
                string teamText = null;
                TelemetryService.Result tele = null;
                try
                {
                    report("Import des résultats LMU…");
                    _plugin.RunLmuImport();

                    tele = TelemetryService.Extract();

                    if (LmuTeamSync.IsConfigured)
                    {
                        report("Synchronisation avec l'équipe…");
                        var error   = LmuTeamSync.Export();
                        int updated = LmuTeamSync.PullTeammates();
                        int traces  = TelemetryService.SyncTeam();
                        int mates   = LmuTeamSync.GetDrivers().Count - 1;
                        teamText = error != null
                            ? "Échec de l'envoi : " + error
                            : string.Format("Envoyé à {0:HH:mm}  ·  {1} coéquipier(s){2}{3}", DateTime.Now, mates,
                                updated > 0 ? string.Format(", {0} mis à jour", updated) : "",
                                traces > 0 ? string.Format(", {0} relais de télémétrie reçus", traces) : "");
                    }
                    else if (LmuTeamSync.Problem != null) teamText = "⚠ " + LmuTeamSync.Problem;
                    TelemetryService.Invalidate();
                    NotificationService.Check();
                }
                catch (Exception ex)
                {
                    teamText = "Erreur : " + ex.Message;
                    SimHub.Logging.Current.Warn("[TeamPace] Update all: " + ex);
                }

                Dispatcher.Invoke(() =>
                {
                    TxtLmuStatus.Text = string.Format("{0} session(s)", LmuSessionStore.GetSessionCount());
                    if (teamText != null) TxtTeamStatus.Text = teamText;
                    var parts = new List<string> { TxtLmuStatus.Text };
                    if (tele != null)
                        parts.Add(tele.Error != null ? "télémétrie : " + tele.Error
                                : tele.Laps > 0 ? string.Format("{0} tour(s) de télémétrie extrait(s)", tele.Laps)
                                : "télémétrie à jour");
                    if (LmuTeamSync.IsConfigured) parts.Add("équipe synchronisée");
                    else if (LmuTeamSync.Problem != null) parts.Add("⚠ équipe non synchronisée : dossier introuvable");
                    TxtTeamFolder.Text = LmuSessionStore.TeamFolder;
                    SetTopStatus("À jour : " + string.Join("  ·  ", parts));
                    BtnUpdateAll.IsEnabled = true;
                    ReloadData();
                });
            });
        }

        // ═══ Équipe (synchro dossier partagé) ═════════════════════════════════

        /// <summary>Says so when the team folder cannot be reached instead of silently not sharing.</summary>
        private void CheckTeamFolder()
        {
            bool ok = LmuTeamSync.IsConfigured;   // also follows a drive letter change
            TxtTeamFolder.Text = LmuSessionStore.TeamFolder;
            if (ok || LmuTeamSync.Problem == null) return;
            TxtTeamStatus.Text = "⚠ " + LmuTeamSync.Problem;
            TxtTeamStatus.Foreground = Ui.Amber;
            SetTopStatus("⚠ " + LmuTeamSync.Problem);
        }

        private void BtnTeamBrowse_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description  = "Dossier Google Drive partagé avec l'équipe";
                dlg.SelectedPath = LmuSessionStore.TeamFolder;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                LmuSessionStore.SaveTeamFolder(dlg.SelectedPath);
                TxtTeamFolder.Text = dlg.SelectedPath;
            }
            UpdateAll();
        }
    }
}
