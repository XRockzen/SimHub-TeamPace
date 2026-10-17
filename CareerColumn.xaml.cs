using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace User.TeamPace
{
    public partial class CareerColumn : UserControl
    {
        public event Action<string>            CircuitSelected;   // row key, null when cleared
        public event Action<LmuCircuitBestRow> CircuitOpened;

        private readonly DriverView _driver;
        private readonly GlobalActivityChartRenderer _activity = new GlobalActivityChartRenderer();
        private List<LmuCircuitBestRow> _allRows = new List<LmuCircuitBestRow>();
        private bool _syncing;

        public List<LmuCircuitBestRow> AllRows { get { return _allRows; } }

        public CareerColumn(DriverView driver)
        {
            _driver = driver;
            InitializeComponent();
            Unloaded += (s, e) => _activity.Detach();
        }

        // ─── Data ─────────────────────────────────────────────────────────────

        public void Load(DateTime? scaleMin, double? scaleMax)
        {
            var sessions = _driver.Sessions;

            int races = 0, wins = 0, podiums = 0, top5 = 0, dnf = 0, laps = 0;
            var driving = TimeSpan.Zero;
            foreach (var s in sessions)
            {
                laps   += s.TotalLaps;
                driving = driving + SessionDuration(s);
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

            TxtRaces.Text   = races.ToString();
            TxtWins.Text    = wins.ToString();
            TxtPodiums.Text = podiums.ToString();
            TxtTop5.Text    = top5.ToString();
            TxtDnf.Text     = dnf.ToString();
            TxtLaps.Text    = laps.ToString();
            TxtTime.Text    = Fmt.TotalTime(driving);

            _allRows = BuildCircuitRows(sessions);
            CircuitGrid.ItemsSource = _allRows;

            _activity.ScaleMinDate  = scaleMin;
            _activity.ScaleMaxValue = scaleMax;
            _activity.Attach(ActivityCanvas, BuildActivity(sessions));
            _activity.SetMode(true);
            _activity.RenderLegend(ActivityLegend);
        }

        public void ApplyClassFilter(ICollection<string> activeClasses)
        {
            if (activeClasses == null || activeClasses.Count == 0)
            {
                CircuitGrid.ItemsSource = _allRows;
                return;
            }
            var filtered = new List<LmuCircuitBestRow>();
            foreach (var r in _allRows)
                if (activeClasses.Contains(r.CarClassLabel)) filtered.Add(r);
            CircuitGrid.ItemsSource = filtered;
        }

        /// <summary>Shows each record's gap to the best displayed driver (multi-driver mode).</summary>
        public void SetGaps(Dictionary<string, long> bestByKey, Dictionary<string, int> driversByKey)
        {
            bool show = bestByKey != null;
            ColGap.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            foreach (var r in _allRows)
            {
                long best; int count;
                bestByKey.TryGetValue(r.Key, out best);
                driversByKey.TryGetValue(r.Key, out count);
                if (count < 2 || best <= 0)
                {
                    r.Gap = "seul"; r.GapFg = "#66FFFFFF"; r.GapTicks = long.MaxValue;
                }
                else if (r.BestTicks == best)
                {
                    r.Gap = "★ best"; r.GapFg = "#FF4CAF50"; r.GapTicks = 0;
                }
                else
                {
                    r.GapTicks = r.BestTicks - best;
                    r.Gap   = "+" + Fmt.Seconds(TimeSpan.FromTicks(r.GapTicks));
                    r.GapFg = "#FFEF5350";
                }
            }
            CircuitGrid.Items.Refresh();
        }

        /// <summary>Focus requested by another driver's column: same circuit × class, or nothing.</summary>
        public void SelectCircuit(string key)
        {
            _syncing = true;
            LmuCircuitBestRow match = null;
            if (key != null)
                foreach (LmuCircuitBestRow r in CircuitGrid.Items)
                    if (r.Key == key) { match = r; break; }
            CircuitGrid.SelectedItem = match;
            if (match != null) CircuitGrid.ScrollIntoView(match);
            Focus(key);
            _syncing = false;
        }

        /// <summary>
        /// Greys out every row except the focused circuit × class, and highlights that circuit in
        /// the activity chart. A driver who never drove it there ends up with every row greyed.
        /// </summary>
        private void Focus(string key)
        {
            foreach (var r in _allRows) r.IsDimmed = key != null && r.Key != key;
            _activity.SelectTrack(key != null ? key.Split('|')[0] : null);
        }

        // ─── Events ───────────────────────────────────────────────────────────

        private void CircuitGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = CircuitGrid.SelectedItem as LmuCircuitBestRow;
            Focus(row != null ? row.Key : null);
            if (!_syncing && CircuitSelected != null) CircuitSelected(row != null ? row.Key : null);
        }

        // Clicking the focused row again clears the focus
        private void CircuitGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 1) return;
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is DataGridRow)) dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            var row = dep as DataGridRow;
            if (row != null && row.IsSelected)
            {
                CircuitGrid.SelectedItem = null;
                e.Handled = true;
            }
        }

        private void CircuitGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = CircuitGrid.SelectedItem as LmuCircuitBestRow;
            if (row != null && CircuitOpened != null) CircuitOpened(row);
        }

        // ─── Shared computations ──────────────────────────────────────────────

        /// <summary>Driving time of a session: finish time, or best lap × laps as a proxy.</summary>
        public static TimeSpan SessionDuration(LmuSessionRecord s)
        {
            // A shared car's race time is the whole car's: each driver drove his share of the laps
            if (s.FinishTime.Ticks > 0 && s.IsCrew && s.CarLaps > 0)
                return TimeSpan.FromTicks(s.FinishTime.Ticks * Math.Min(s.TotalLaps, s.CarLaps) / s.CarLaps);
            if (s.FinishTime.Ticks > 0) return s.FinishTime;
            if (s.BestLapTime.Ticks > 0 && s.TotalLaps > 0)
                return TimeSpan.FromTicks(s.BestLapTime.Ticks * s.TotalLaps);
            return TimeSpan.Zero;
        }

        public static List<ActivityLap> BuildActivity(List<LmuSessionRecord> sessions)
        {
            var list = new List<ActivityLap>();
            foreach (var s in sessions)
            {
                if (s.SessionDate == DateTime.MinValue) continue;
                var dur = SessionDuration(s);
                if (dur <= TimeSpan.Zero) continue;
                list.Add(new ActivityLap { TrackName = s.TrackDisplayName, Date = s.SessionDate, LapTime = dur });
            }
            return list;
        }

        public static List<LmuCircuitBestRow> BuildCircuitRows(List<LmuSessionRecord> sessions)
        {
            var bests = new Dictionary<string, LmuSessionRecord>();
            var time  = new Dictionary<string, TimeSpan>();
            foreach (var s in sessions)
            {
                var key = s.TrackDisplayName + "|" + LmuCategoryStyle.GetShortName(s.CarClass);
                var dur = SessionDuration(s);
                if (dur > TimeSpan.Zero)
                {
                    TimeSpan t; time.TryGetValue(key, out t);
                    time[key] = t + dur;
                }
                if (s.BestLapTime.Ticks <= 0) continue;
                LmuSessionRecord cur;
                if (!bests.TryGetValue(key, out cur) || s.BestLapTime.Ticks < cur.BestLapTime.Ticks)
                    bests[key] = s;
            }

            var rows = new List<LmuCircuitBestRow>();
            foreach (var kv in bests)
            {
                var s = kv.Value;
                var car = s.CarType ?? "";
                TimeSpan total; time.TryGetValue(kv.Key, out total);
                rows.Add(new LmuCircuitBestRow
                {
                    Circuit        = s.TrackDisplayName,
                    CarClassLabel  = LmuCategoryStyle.GetShortName(s.CarClass),
                    CarClassBg     = LmuCategoryStyle.GetBg(s.CarClass),
                    CarClassBorder = LmuCategoryStyle.GetBorder(s.CarClass),
                    CarClassThick  = LmuCategoryStyle.GetBorderThickness(s.CarClass),
                    BestLap        = s.BestLapFormatted,
                    BestTicks      = s.BestLapTime.Ticks,
                    Voiture        = car.Length > 18 ? car.Substring(0, 17) + "…" : car,
                    Date           = s.SessionDate != DateTime.MinValue ? s.SessionDate.ToString("dd/MM/yy") : "",
                    TotalTime      = Fmt.TotalTime(total),
                    TotalTimeTicks = total.Ticks,
                });
            }
            rows.Sort((a, b) =>
            {
                int c = string.Compare(a.Circuit, b.Circuit, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.CarClassLabel, b.CarClassLabel, StringComparison.OrdinalIgnoreCase);
            });
            return rows;
        }
    }
}
