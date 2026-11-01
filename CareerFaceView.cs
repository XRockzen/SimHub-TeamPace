using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>
    /// Career, face to face: one row per circuit × category with every displayed driver's record
    /// on the same line (one tinted block per driver), the key figures side by side, and each
    /// driver's weekly activity below. Selecting a row highlights its circuit in the activity
    /// charts; clicking a bar selects its row.
    /// </summary>
    internal class CareerFaceView : UserControl
    {
        public event Action<string, string, string> CircuitOpened;   // circuit, class label, raw class
        public event Action<string> FocusChanged;                     // row key, null when cleared

        private class Line
        {
            public string Key, Circuit, ClassLabel, RawClass, ClassBg;
            public LmuCircuitBestRow[] Cells;   // per driver, null when never driven
            public long Best;
            public Border Row;
        }

        private static string _focusKey;   // kept while browsing

        private readonly List<DriverView> _drivers;
        private readonly ICollection<string> _classes;
        private readonly List<Line> _lines = new List<Line>();
        private readonly List<GlobalActivityChartRenderer> _charts = new List<GlobalActivityChartRenderer>();
        private readonly StackPanel _rows = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        private const double CircuitMin = 260, CatW = 84, TimeW = 128, CarW = 160, DateW = 80, RunW = 72, GapW = 112;

        public CareerFaceView(List<DriverView> drivers, ICollection<string> activeClasses)
        {
            _drivers = drivers;
            _classes = activeClasses;
            Build();
            Unloaded += (s, e) => { foreach (var c in _charts) c.Detach(); };
        }

        /// <summary>Category labels present, for the filter chips.</summary>
        public List<LmuCircuitBestRow> ClassSamples()
        {
            var seen = new HashSet<string>();
            var list = new List<LmuCircuitBestRow>();
            foreach (var l in _lines)
                foreach (var c in l.Cells)
                    if (c != null && seen.Add(c.CarClassLabel)) list.Add(c);
            list.Sort((a, b) => string.Compare(a.CarClassLabel, b.CarClassLabel, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public void ApplyClassFilter()
        {
            foreach (var l in _lines)
                l.Row.Visibility = _classes.Count == 0 || _classes.Contains(l.ClassLabel) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─── Build ────────────────────────────────────────────────────────────

        private void Build()
        {
            var perDriver = new List<Dictionary<string, LmuCircuitBestRow>>();
            var rawClass = new Dictionary<string, string>();
            foreach (var d in _drivers)
            {
                var map = new Dictionary<string, LmuCircuitBestRow>();
                foreach (var r in CareerColumn.BuildCircuitRows(d.Sessions)) map[r.Key] = r;
                perDriver.Add(map);
                foreach (var s in d.Sessions)
                {
                    var key = s.TrackDisplayName + "|" + LmuCategoryStyle.GetShortName(s.CarClass);
                    if (!rawClass.ContainsKey(key)) rawClass[key] = s.CarClass;
                }
            }

            var keySet = new HashSet<string>();
            foreach (var m in perDriver) foreach (var k in m.Keys) keySet.Add(k);
            // By circuit, then category ("Monza" before "Monza – Curva Grande")
            var keys = new List<string>(keySet);
            keys.Sort((x, y) =>
            {
                var a = x.Split('|'); var b = y.Split('|');
                int c = string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a[1], b[1], StringComparison.OrdinalIgnoreCase);
            });
            foreach (var key in keys)
            {
                var line = new Line { Key = key, Cells = new LmuCircuitBestRow[_drivers.Count], Best = long.MaxValue };
                for (int i = 0; i < _drivers.Count; i++)
                {
                    LmuCircuitBestRow r;
                    if (!perDriver[i].TryGetValue(key, out r)) continue;
                    line.Cells[i] = r;
                    line.Circuit = r.Circuit; line.ClassLabel = r.CarClassLabel; line.ClassBg = r.CarClassBg;
                    if (r.BestTicks > 0 && r.BestTicks < line.Best) line.Best = r.BestTicks;
                }
                string raw; rawClass.TryGetValue(key, out raw);
                line.RawClass = raw;
                _lines.Add(line);
            }

            var root = new DockPanel();
            var kpis = BuildKpis(perDriver);
            DockPanel.SetDock(kpis, Dock.Top);
            root.Children.Add(kpis);

            var activity = BuildActivity();
            DockPanel.SetDock(activity, Dock.Bottom);
            root.Children.Add(activity);

            var table = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
            var head = BuildHeader();
            DockPanel.SetDock(head, Dock.Top);
            table.Children.Add(head);
            // The rows' scroll bar narrows them: the header follows, so the columns stay aligned
            _scroll.LayoutUpdated += (s, e) =>
            {
                double bar = Math.Max(0, _scroll.ActualWidth - _scroll.ViewportWidth);
                if (Math.Abs(((FrameworkElement)head).Margin.Right - bar) > 0.5) ((FrameworkElement)head).Margin = new Thickness(0, 0, bar, 0);
            };
            foreach (var l in _lines) { l.Row = BuildRow(l); _rows.Children.Add(l.Row); }
            _scroll.Content = _rows;
            table.Children.Add(_scroll);
            root.Children.Add(new Border { Background = Ui.Frozen(Color.FromArgb(10, 255, 255, 255)), CornerRadius = new CornerRadius(8), Child = table, Margin = new Thickness(0, 10, 0, 10) });

            Content = root;
            ApplyClassFilter();
            if (_focusKey != null) Dispatcher.BeginInvoke(new Action(() => Select(_focusKey, true)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private bool TwoDrivers { get { return _drivers.Count == 2; } }

        private Grid NewRowGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = CircuitMin });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CatW) });
            for (int i = 0; i < _drivers.Count; i++)
            {
                if (TwoDrivers && i == 1) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GapW) });
                foreach (var w in new[] { TimeW, CarW, DateW, RunW }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            }
            return g;
        }

        /// <summary>Grid column of a driver block's first cell.</summary>
        private int DriverCol(int i) { return 2 + i * 4 + (TwoDrivers && i == 1 ? 1 : 0); }

        private UIElement BuildHeader()
        {
            var outer = new StackPanel();
            var top = NewRowGrid();
            var bottom = NewRowGrid();
            top.Background = bottom.Background = Ui.HeaderBg;

            Place(top, Ui.T("Circuit · catégorie", 12.5, Ui.Muted, FontWeights.SemiBold), 0, 2, new Thickness(10, 6, 6, 2));
            for (int i = 0; i < _drivers.Count; i++)
            {
                var d = _drivers[i];
                var band = new Border
                {
                    Background = Ui.Tint(d.Brush, 34), BorderBrush = d.Brush, BorderThickness = new Thickness(3, 0, 0, 0),
                    Child = Ui.T(d.DbPath == null ? d.Name + "  (moi)" : d.Name, 13.5, Ui.Light(d.Brush), FontWeights.Bold),
                    Padding = new Thickness(8, 5, 6, 3),
                };
                Grid.SetColumn(band, DriverCol(i)); Grid.SetColumnSpan(band, 4);
                top.Children.Add(band);
            }
            if (TwoDrivers) Place(top, Center(Ui.T("Écart", 12.5, Ui.Muted, FontWeights.SemiBold)), DriverCol(1) - 1, 1, new Thickness(0, 6, 0, 2));

            Place(bottom, Ui.T("Circuit", 12, Ui.Faint), 0, 1, new Thickness(10, 2, 6, 5));
            Place(bottom, Ui.T("Cat.", 12, Ui.Faint), 1, 1, new Thickness(6, 2, 6, 5));
            for (int i = 0; i < _drivers.Count; i++)
            {
                int c = DriverCol(i);
                var names = new[] { "Record", "Voiture", "Date", "Roulage" };
                for (int k = 0; k < 4; k++)
                {
                    var cell = new Border
                    {
                        Background = Ui.Tint(_drivers[i].Brush, 18),
                        BorderBrush = _drivers[i].Brush, BorderThickness = new Thickness(k == 0 ? 3 : 0, 0, 0, 0),
                        Padding = new Thickness(8, 2, 6, 5), Child = Ui.T(names[k], 12, Ui.Faint),
                    };
                    Grid.SetColumn(cell, c + k);
                    bottom.Children.Add(cell);
                }
            }
            if (TwoDrivers) Place(bottom, Center(Ui.T("qui mène", 12, Ui.Faint)), DriverCol(1) - 1, 1, new Thickness(0, 2, 0, 5));

            outer.Children.Add(top);
            outer.Children.Add(bottom);
            return outer;
        }

        private Border BuildRow(Line l)
        {
            var g = NewRowGrid();
            g.MinHeight = 30;
            Place(g, Ui.T(l.Circuit, 13), 0, 1, new Thickness(10, 0, 6, 0));
            var badge = Ui.Badge(l.ClassLabel, l.ClassBg ?? "#555555");
            Place(g, badge, 1, 1, new Thickness(6, 0, 0, 0));

            int count = 0;
            foreach (var c in l.Cells) if (c != null && c.BestTicks > 0) count++;

            for (int i = 0; i < _drivers.Count; i++)
            {
                var d = _drivers[i];
                var r = l.Cells[i];
                int col = DriverCol(i);
                for (int k = 0; k < 4; k++)
                {
                    var bg = new Border
                    {
                        Background = Ui.Tint(d.Brush, 14), BorderBrush = d.Brush,
                        BorderThickness = new Thickness(k == 0 ? 3 : 0, 0, 0, 0),
                    };
                    Grid.SetColumn(bg, col + k);
                    g.Children.Add(bg);
                }
                if (r == null)
                {
                    Place(g, Ui.T("—", 13, Ui.Faint), col, 1, new Thickness(10, 0, 4, 0));
                    continue;
                }
                bool best = count > 1 && r.BestTicks == l.Best;
                var time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
                if (best) time.Inlines.Add(new System.Windows.Documents.Run("★ " + r.BestLap) { Foreground = Ui.Green, FontWeight = FontWeights.Bold });
                else
                {
                    time.Inlines.Add(new System.Windows.Documents.Run(r.BestLap) { Foreground = Ui.Text });
                    if (count > 1 && r.BestTicks > 0)
                        time.Inlines.Add(new System.Windows.Documents.Run("  +" + Fmt.Seconds(TimeSpan.FromTicks(r.BestTicks - l.Best))) { Foreground = Ui.Red, FontSize = 12 });
                }
                Place(g, time, col, 1, new Thickness(10, 0, 4, 0));
                var car = Ui.T(r.Voiture, 12.5);
                car.ToolTip = r.Voiture;
                Place(g, car, col + 1, 1, new Thickness(8, 0, 4, 0));
                Place(g, Ui.T(r.Date, 12.5, Ui.Muted), col + 2, 1, new Thickness(8, 0, 4, 0));
                Place(g, Ui.T(r.TotalTime, 12.5, Ui.Muted), col + 3, 1, new Thickness(8, 0, 4, 0));
            }

            if (TwoDrivers)
            {
                var a = l.Cells[0]; var b = l.Cells[1];
                TextBlock gap;
                if (a == null || b == null || a.BestTicks <= 0 || b.BestTicks <= 0) gap = Ui.T("seul", 12, Ui.Faint);
                else
                {
                    var diff = Fmt.Seconds(TimeSpan.FromTicks(Math.Abs(a.BestTicks - b.BestTicks)));
                    bool left = a.BestTicks <= b.BestTicks;
                    gap = Ui.T(left ? "◀ " + diff : diff + " ▶", 12.5, Ui.Light(left ? _drivers[0].Brush : _drivers[1].Brush), FontWeights.Bold);
                }
                Place(g, Center(gap), DriverCol(1) - 1, 1, new Thickness(0));
            }

            var row = new Border
            {
                Child = g, Background = Brushes.Transparent, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                Cursor = Cursors.Hand, ToolTip = "Clic : sélectionner ce circuit · double-clic : ouvrir sa page (progression, télémétrie, plan de travail)",
            };
            row.MouseEnter += (s, e) => { if (_focusKey != l.Key) row.Background = Ui.Hover; };
            row.MouseLeave += (s, e) => { if (_focusKey != l.Key) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    var h = CircuitOpened;
                    if (h != null) h(l.Circuit, l.ClassLabel, l.RawClass);
                }
                else Select(_focusKey == l.Key ? null : l.Key, false);
                e.Handled = true;
            };
            return row;
        }

        /// <summary>Highlights a row (null clears) and its circuit in every activity chart.</summary>
        private void Select(string key, bool scroll)
        {
            _focusKey = key;
            Line focused = null;
            foreach (var l in _lines)
            {
                bool on = l.Key == key;
                l.Row.Background = on ? Ui.Selected : Brushes.Transparent;
                if (on) focused = l;
            }
            foreach (var c in _charts) c.SelectTrack(focused != null ? focused.Circuit : null);
            if (scroll && focused != null) focused.Row.BringIntoView();
            var h = FocusChanged;
            if (h != null) h(key);
        }

        // ─── Key figures ──────────────────────────────────────────────────────

        private UIElement BuildKpis(List<Dictionary<string, LmuCircuitBestRow>> perDriver)
        {
            var names = new[] { "COURSES", "VICTOIRES", "PODIUMS", "TOP 5", "DNF", "RECORDS DÉTENUS", "TOURS", "ROULAGE" };
            var values = new List<string[]>();
            for (int i = 0; i < _drivers.Count; i++)
            {
                int races = 0, wins = 0, podiums = 0, top5 = 0, dnf = 0, laps = 0;
                var driving = TimeSpan.Zero;
                foreach (var s in _drivers[i].Sessions)
                {
                    laps += s.TotalLaps;
                    driving = driving + CareerColumn.SessionDuration(s);
                    if (!s.IsRace) continue;
                    races++;
                    if (s.IsFinished)
                    {
                        int p = s.ClassPosition > 0 ? s.ClassPosition : s.Position;
                        if (p == 1) wins++;
                        if (p > 0 && p <= 3) podiums++;
                        if (p > 0 && p <= 5) top5++;
                    }
                    else if (s.TotalLaps > 0) dnf++;
                }
                int records = 0;
                foreach (var l in _lines)
                {
                    int count = 0; foreach (var c in l.Cells) if (c != null && c.BestTicks > 0) count++;
                    if (count > 1 && l.Cells[i] != null && l.Cells[i].BestTicks == l.Best) records++;
                }
                values.Add(new[] { races.ToString(), wins.ToString(), podiums.ToString(), top5.ToString(), dnf.ToString(),
                                   _drivers.Count > 1 ? records.ToString() : "—", laps.ToString(), Hours(driving) });
            }

            var grid = new UniformGrid { Rows = 1 };
            for (int k = 0; k < names.Length; k++)
            {
                if (_drivers.Count < 2 && k == 5) continue;
                var panel = new StackPanel();
                panel.Children.Add(Ui.T(names[k], 11, Ui.Muted));
                var vals = new StackPanel { Orientation = _drivers.Count <= 2 ? Orientation.Horizontal : Orientation.Vertical, Margin = new Thickness(0, 3, 0, 0) };
                for (int i = 0; i < _drivers.Count; i++)
                {
                    var t = Ui.T(values[i][k], _drivers.Count <= 2 ? 19 : 14, Ui.Light(_drivers[i].Brush), FontWeights.SemiBold);
                    t.Margin = new Thickness(0, 0, 18, 0);
                    t.ToolTip = _drivers[i].Name;
                    vals.Children.Add(t);
                }
                panel.Children.Add(vals);
                var card = Ui.Card(panel);
                card.Margin = new Thickness(0, 0, 8, 0);
                grid.Children.Add(card);
            }
            return grid;
        }

        /// <summary>Driving time in whole hours ("97 h"), minutes under an hour.</summary>
        private static string Hours(TimeSpan t)
        {
            if (t <= TimeSpan.Zero) return "—";
            return t.TotalHours < 1 ? string.Format("{0} min", Math.Max(1, (int)Math.Round(t.TotalMinutes))) : string.Format("{0} h", (int)Math.Round(t.TotalHours));
        }

        // ─── Activity ─────────────────────────────────────────────────────────

        private UIElement BuildActivity()
        {
            var panel = new StackPanel();
            var title = Ui.T("ACTIVITÉ  ·  heures par semaine", 11, Ui.Muted);
            title.ToolTip = "Une couleur par circuit. Clic sur une barre : sélectionne le circuit dans le tableau. "
                          + "Les poignées sous le graphique choisissent la période ; double-clic sur la barre : tout l'historique.";
            panel.Children.Add(title);

            DateTime? scaleMin = null;
            double scaleMax = 0;
            var acts = new List<List<ActivityLap>>();
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _drivers)
            {
                var act = CareerColumn.BuildActivity(d.Sessions);
                acts.Add(act);
                foreach (var a in act)
                {
                    if (!scaleMin.HasValue || a.Date < scaleMin.Value) scaleMin = a.Date;
                    double t; totals.TryGetValue(a.TrackName, out t); totals[a.TrackName] = t + 1;
                }
                scaleMax = Math.Max(scaleMax, GlobalActivityChartRenderer.MaxWeeklyValue(act, true));
            }
            // Same colour for a circuit in every driver's chart
            var tracks = new List<string>(totals.Keys);
            tracks.Sort((a, b) => totals[b].CompareTo(totals[a]));

            for (int i = 0; i < _drivers.Count; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var name = Ui.T(_drivers[i].Name, 12, Ui.Light(_drivers[i].Brush), FontWeights.SemiBold);
                name.Width = 120;
                DockPanel.SetDock(name, Dock.Left);
                row.Children.Add(name);
                bool last = i == _drivers.Count - 1;   // the period handles under the last chart, for every driver
                var canvas = new Canvas { Height = (_drivers.Count > 2 ? 54 : 70) + (last ? 18 : 0), Background = Brushes.Transparent, ClipToBounds = true };
                row.Children.Add(canvas);
                panel.Children.Add(row);

                var chart = new GlobalActivityChartRenderer
                {
                    ScaleMinDate = scaleMin, ScaleMaxValue = scaleMax > 0 ? (double?)scaleMax : null,
                    RangeHandles = last, ViewFrom = _from, ViewTo = _to,
                };
                if (last) { chart.RangeChanged += () => ApplyPeriod(chart, acts); chart.RangeMoving += () => ApplyPeriod(chart, acts); }
                chart.Attach(canvas, acts[i], tracks);
                chart.SetMode(true);
                chart.TrackClicked += track =>
                {
                    Line match = null;
                    if (track != null)
                        foreach (var l in _lines)
                            if (l.Row.Visibility == Visibility.Visible && string.Equals(l.Circuit, track, StringComparison.OrdinalIgnoreCase)) { match = l; break; }
                    Select(match != null ? match.Key : null, true);
                };
                _charts.Add(chart);
                if (i == _drivers.Count - 1) ApplyPeriod(chart, acts);
            }
            return Ui.Card(panel);
        }

        // The period of the activity, kept while the plugin runs
        private static DateTime? _from, _to;

        /// <summary>The period chosen with the handles, on every driver's chart, with a scale common to them.</summary>
        private void ApplyPeriod(GlobalActivityChartRenderer source, List<List<ActivityLap>> acts)
        {
            _from = source.ViewFrom; _to = source.ViewTo;
            double max = 0;
            foreach (var a in acts) max = Math.Max(max, GlobalActivityChartRenderer.MaxWeeklyValue(a, true, _from, _to));
            foreach (var c in _charts)
            {
                c.ViewFrom = _from; c.ViewTo = _to;
                c.PeriodScaleMax = max > 0 ? (double?)max : null;
                c.SetMode(c.UseHours);   // the dragged chart too: the common scale
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static void Place(Grid g, FrameworkElement e, int col, int span, Thickness margin)
        {
            e.Margin = margin;
            Grid.SetColumn(e, col);
            Grid.SetColumnSpan(e, span);
            g.Children.Add(e);
        }

        private static FrameworkElement Center(TextBlock t)
        {
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return t;
        }
    }
}
