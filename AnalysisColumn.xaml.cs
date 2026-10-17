using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>Lap-by-lap analysis of one driver's session, with stats on the checked laps.</summary>
    public partial class AnalysisColumn : UserControl
    {
        // Decimal point everywhere, like lap times (1:58.335), whatever the Windows locale
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Raised when the checked laps change (average lap may have moved).</summary>
        public event Action<AnalysisColumn> StatsChanged;

        /// <summary>Raised on a lap double-click: open that lap's telemetry.</summary>
        public event Action<AnalysisColumn, int> LapOpened;

        public DriverView       Driver  { get; private set; }
        public LmuSessionRecord Session { get; private set; }
        public long AverageTicks { get; private set; }   // average of checked valid laps, 0 if none

        private readonly ObservableCollection<AnalysisLapRow> _rows = new ObservableCollection<AnalysisLapRow>();
        private long _referenceAvg;
        private string _referenceName;

        public IList<AnalysisLapRow> Rows { get { return _rows; } }

        // Folded groups, the same for every column and kept while browsing
        private static bool _statsOpen = true, _lapsOpen = true;
        private static event Action Folded;

        public AnalysisColumn(DriverView driver, LmuSessionRecord session)
        {
            Driver  = driver;
            Session = session;
            InitializeComponent();
            LapGrid.MouseDoubleClick += LapGrid_MouseDoubleClick;
            // The table shows every lap; the page scrolls, the wheel over the table included
            LapGrid.PreviewMouseWheel += (s, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = this });
            };
            Loaded   += (s, e) => { Folded += ApplyFolds; ApplyFolds(); };
            Unloaded += (s, e) => Folded -= ApplyFolds;
            Build();
        }

        private void StatsHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _statsOpen = !_statsOpen;
            var h = Folded; if (h != null) h();
        }

        private void LapsHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _lapsOpen = !_lapsOpen;
            var h = Folded; if (h != null) h();
        }

        private void ApplyFolds()
        {
            StatsBody.Visibility = _statsOpen ? Visibility.Visible : Visibility.Collapsed;
            TxtStatsTitle.Text = (_statsOpen ? "▾" : "▸") + "  Statistiques";
            LapGrid.Visibility = _lapsOpen ? Visibility.Visible : Visibility.Collapsed;
            TxtLapsTitle.Text = (_lapsOpen ? "▾" : "▸") + "  Tour par tour";
        }

        private void LapGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var row = e.OriginalSource as DependencyObject;
            while (row != null && !(row is DataGridRow))
            {
                if (row is CheckBox) return;   // double-click on the selection box
                row = System.Windows.Media.VisualTreeHelper.GetParent(row);
            }
            var lap = row != null ? ((DataGridRow)row).Item as AnalysisLapRow : null;
            var h = LapOpened;
            if (lap != null && h != null) h(this, lap.LapNum);
        }

        // ─── Build ────────────────────────────────────────────────────────────

        private void Build()
        {
            var r = Session;
            var cls = r.CarClass ?? "";
            BadgeClass.Background  = Brush(LmuCategoryStyle.GetBg(cls));
            BadgeClass.BorderBrush = Brush(LmuCategoryStyle.GetBorder(cls));
            BadgeClass.BorderThickness = LmuCategoryStyle.GetBorderThickness(cls) == "1" ? new Thickness(1) : new Thickness(0);
            TxtClass.Text = LmuCategoryStyle.GetShortName(cls);
            TxtCar.Text   = (r.CarType ?? "") + (string.IsNullOrEmpty(r.TeamName) ? "" : "  ·  " + r.TeamName.Trim());
            if (!string.IsNullOrEmpty(r.TeamDrivers))
                // Shared car: this column holds this driver's laps, the result is the car's
                TxtCar.ToolTip = string.Format("Équipage : {0} tours\nLa voiture a fait {1} tours ; cette colonne ne montre que ceux de ce pilote, la position est celle de la voiture.", r.TeamDrivers, r.CarLaps);
            TxtDate.Text  = r.SessionDate != DateTime.MinValue
                ? r.SessionDate.ToString("ddd d MMM yyyy · HH:mm", CultureInfo.GetCultureInfo("fr-FR")) : "";

            KpiFinishBox.Visibility = r.IsRace ? Visibility.Visible : Visibility.Collapsed;
            KpiGridBox.Visibility   = r.IsRace ? Visibility.Visible : Visibility.Collapsed;
            KpiFinish.Text = r.IsFinished ? Pos(r.ClassPosition, r.Position) : (r.TotalLaps > 0 ? "Abandon" : "—");
            KpiGrid.Text   = Pos(r.ClassGridPos, r.GridPos);
            KpiBest.Text   = r.BestLapFormatted;
            KpiLaps.Text   = r.TotalLaps.ToString();
            KpiPits.Text   = r.Pitstops.ToString();
            KpiIncidents.Text = r.PenaltyCount > 0
                ? string.Format(Inv, "{0} · {1} pén.", r.IncidentCount, r.PenaltyCount)
                : r.IncidentCount.ToString();

            SimBadges.Children.Clear();
            AddBadge(r.IsFullSim ? "SIM" : "ARCADE", r.IsFullSim ? "#991565B0" : "#99666666",
                     r.IsFullSim ? "Simulation complète : dégâts, carburant et usure réalistes" : "Dégâts, carburant ou usure réduits");
            AddBadge(r.Mode.ToUpperInvariant(), r.ModeBg,
                     r.IsCrew ? "En ligne, voiture partagée : " + r.TeamDrivers + " tours" : r.IsOnline ? "En ligne, seul dans la voiture" : "Hors ligne");

            var laps = LmuSessionStore.GetLapsForSession(r.FileId, Driver.DbPath);
            int liveMatched = MergeLiveData(r, laps, Driver.DbPath);
            ReconstructInvalidTimes(laps);

            // Theoretical best = best S1 + best S2 + best S3 of valid laps, each with its lap
            float theo = 0f, top = 0f;
            float b1 = float.MaxValue, b2 = float.MaxValue, b3 = float.MaxValue;
            int l1 = 0, l2 = 0, l3 = 0;
            foreach (var l in laps)
            {
                if (l.IsValid && l.S1 > 0 && l.S1 < b1) { b1 = l.S1; l1 = l.LapNum; }
                if (l.IsValid && l.S2 > 0 && l.S2 < b2) { b2 = l.S2; l2 = l.LapNum; }
                if (l.IsValid && l.S3 > 0 && l.S3 < b3) { b3 = l.S3; l3 = l.LapNum; }
                if (l.TopSpeedKph > top) top = l.TopSpeedKph;
            }
            bool hasTheo = b1 < float.MaxValue && b2 < float.MaxValue && b3 < float.MaxValue;
            if (hasTheo) theo = b1 + b2 + b3;
            KpiTheo.Text = theo > 0 ? Fmt.LapSec(theo) : "—";
            KpiTheoBox.ToolTip = hasTheo
                ? string.Format(Inv,
                    "Meilleurs secteurs des tours valides\n\nS1   {0}   tour {1}\nS2   {2}   tour {3}\nS3   {4}   tour {5}\n\nThéorique   {6}",
                    Fmt.LapSec(b1), l1, Fmt.LapSec(b2), l2, Fmt.LapSec(b3), l3, Fmt.LapSec(theo))
                : "Pas assez de secteurs valides pour calculer un temps théorique";

            bool hasVe = false, hasTire = false;
            string prevCompound = null;
            LmuLapRecord prevLap = null;
            foreach (var l in laps)
            {
                bool tireChanged = (prevCompound != null && !string.IsNullOrEmpty(l.FrontCompound) &&
                                    l.FrontCompound != prevCompound) ||
                                   (l.LivePit != null && l.LivePit.TyresChanged);
                bool rhythm = l.IsValid && !l.IsPitLap && l.LapNum > 1 &&
                              (prevLap == null || !prevLap.IsPitLap);
                // Out lap: after a pit stop, or the first lap when leaving the garage
                bool outLap = !l.IsPitLap &&
                              ((prevLap != null && prevLap.IsPitLap) ||
                               (prevLap == null && !l.IsValid && l.ElapsedTime < 1f));
                var row = new AnalysisLapRow
                {
                    Lap         = l,
                    TireChanged = tireChanged,
                    IsOutLap    = outLap,
                    IsRhythmLap = rhythm,
                    IsChecked   = rhythm,
                };
                if (l.HasLive)
                {
                    row.WearLapFL = l.LiveWearFL; row.WearLapFR = l.LiveWearFR;
                    row.WearLapRL = l.LiveWearRL; row.WearLapRR = l.LiveWearRR;
                }
                else if (prevLap != null)
                {
                    // XML stores remaining fraction at lap end: wear = drop vs. previous lap
                    row.WearLapFL = WearDrop(prevLap.TireWearFL, l.TireWearFL);
                    row.WearLapFR = WearDrop(prevLap.TireWearFR, l.TireWearFR);
                    row.WearLapRL = WearDrop(prevLap.TireWearRL, l.TireWearRL);
                    row.WearLapRR = WearDrop(prevLap.TireWearRR, l.TireWearRR);
                }
                prevLap = l;
                if (row.HasVe) hasVe = true;
                if (row.HasTireData) hasTire = true;
                row.PropertyChanged += (s, ev) => { if (ev.PropertyName == "IsChecked") Recalc(); };
                _rows.Add(row);
                if (!string.IsNullOrEmpty(l.FrontCompound)) prevCompound = l.FrontCompound;
            }

            LapGrid.ItemsSource = _rows;
            HasVe    = hasVe;
            HasTyres = hasTire;
            foreach (var row in _rows)
            {
                if (row.FuelUsed != "—") HasFuel = true;
                if (row.IsPitLap) { HasPits = true; PitCount++; }
            }
            ShowRaceControlSummary();
            // A shared car's result counts the car's stops: this column shows the driver's own
            if (!string.IsNullOrEmpty(r.TeamDrivers)) KpiPits.Text = PitCount.ToString();
            TxtDate.ToolTip = liveMatched > 0
                ? string.Format(Inv, "Capture directe SimHub : {0} tour(s) (carburant, usure, arrêts)", liveMatched)
                : "Pas de capture directe : SimHub n'était pas ouvert pendant la session";
            SetOptionalBlocks(HasFuel, HasVe, HasTyres, HasPits);

            Recalc();
        }

        public int PitCount { get; private set; }

        /// <summary>Same height for every driver's pit block, sized on the longest list.</summary>
        /// <summary>The lap table fills the visible height; the blocks above scroll away.</summary>
        public void SetLapTableHeight(double height) { }

        public void SetPitBlockStops(int stops) { }

        /// <summary>Invalidated-lap counter, with the race-control breakdown on hover.</summary>
        private void ShowRaceControlSummary()
        {
            int invalid = 0, warnings = 0, noAction = 0, contacts = 0;
            var penalties = new List<string>();
            foreach (var row in _rows)
            {
                if (row.IsTrackInvalidated) invalid++;
                warnings += row.Lap.LimitsWarning;
                noAction += row.Lap.LimitsNoAction;
                contacts += row.Lap.Contacts;
                if (!string.IsNullOrEmpty(row.Lap.Penalty))
                    penalties.Add(string.Format(Inv, "tour {0} : {1}", row.LapNum, row.Lap.Penalty));
            }
            KpiInvalid.Text = invalid.ToString();
            var tip = new List<string>
            {
                string.Format(Inv, "{0} tour(s) invalidé(s) : coupure ou sortie de piste", invalid),
                string.Format(Inv, "{0} avertissement(s) limites de piste", warnings),
                string.Format(Inv, "{0} dépassement(s) des limites sans conséquence", noAction),
                string.Format(Inv, "{0} contact(s)", contacts),
            };
            if (penalties.Count > 0) { tip.Add(""); tip.Add("Pénalités :"); tip.AddRange(penalties); }
            tip.Add("");
            tip.Add("Les tours de stand et de sortie du garage ne sont pas comptés.");
            KpiInvalidBox.ToolTip = string.Join("\n", tip);
        }

        // One row of key figures, two when the column is too narrow (3-4 drivers)
        private void KpiGridPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            int visible = 0;
            foreach (UIElement c in KpiGridPanel.Children) if (c.Visibility == Visibility.Visible) visible++;
            if (visible == 0) return;
            int rows = e.NewSize.Width / visible < 74 ? 2 : 1;
            if (KpiGridPanel.Rows != rows) KpiGridPanel.Rows = rows;
        }

        public bool HasFuel  { get; private set; }
        public bool HasVe    { get; private set; }
        public bool HasTyres { get; private set; }
        public bool HasPits  { get; private set; }

        /// <summary>
        /// Shows the optional blocks and columns. In a comparison the host passes the union over
        /// all drivers, so every column keeps the same layout and the lap tables stay aligned.
        /// </summary>
        public void SetOptionalBlocks(bool fuel, bool ve, bool tyres, bool pits)
        {
            StatsFuelPanel.Visibility = fuel  ? Visibility.Visible : Visibility.Collapsed;
            StatsVePanel.Visibility   = ve    ? Visibility.Visible : Visibility.Collapsed;
            StatsTirePanel.Visibility = tyres ? Visibility.Visible : Visibility.Collapsed;
            StatsPitPanel.Visibility  = pits  ? Visibility.Visible : Visibility.Collapsed;
            ColFuel.Visibility        = fuel  ? Visibility.Visible : Visibility.Collapsed;
            ColFuelLeft.Visibility    = fuel  ? Visibility.Visible : Visibility.Collapsed;
            ColVe.Visibility          = ve    ? Visibility.Visible : Visibility.Collapsed;
            ColWear.Visibility        = tyres ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string Pos(int cls, int overall)
        {
            if (cls > 0 && overall > 0 && cls != overall) return string.Format(Inv, "P{0} (P{1})", cls, overall);
            if (cls > 0) return "P" + cls;
            if (overall > 0) return "P" + overall;
            return "—";
        }

        private void AddBadge(string text, string bg, string tip)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding      = new Thickness(5, 1, 5, 1),
                Margin       = new Thickness(0, 0, 4, 0),
                Background   = Brush(bg),
            };
            b.Child = new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            b.ToolTip = tip;
            SimBadges.Children.Add(b);
        }

        private static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        // ─── Multi-driver comparison ──────────────────────────────────────────

        /// <summary>Gap of each lap to the fastest displayed driver on the same lap number.</summary>
        public void SetTeamDeltas(Dictionary<int, long> bestByLap)
        {
            ColTeamDelta.Visibility = bestByLap != null ? Visibility.Visible : Visibility.Collapsed;
            if (bestByLap == null) return;
            foreach (var row in _rows)
            {
                long best;
                if (!row.IsValid || row.Lap.LapTimeTicks <= 0 || !bestByLap.TryGetValue(row.LapNum, out best))
                {
                    row.TeamDelta = ""; continue;
                }
                long gap = row.Lap.LapTimeTicks - best;
                row.TeamDelta   = gap == 0 ? "★" : "+" + Fmt.Seconds(TimeSpan.FromTicks(gap));
                row.TeamDeltaFg = gap == 0 ? "#FF4CAF50" : (gap < TimeSpan.TicksPerSecond / 2 ? "#FFFFB74D" : "#FFEF5350");
            }
            LapGrid.Items.Refresh();
        }

        /// <summary>Best average among the displayed drivers, to show this driver's gap to it.</summary>
        public void SetAverageReference(long bestAvgTicks, string bestName)
        {
            _referenceAvg  = bestAvgTicks;
            _referenceName = bestName;
            UpdateVsBest();
        }

        private void UpdateVsBest()
        {
            if (_referenceAvg <= 0 || AverageTicks <= 0) { TxtStVsBest.Text = ""; return; }
            long gap = AverageTicks - _referenceAvg;
            if (gap <= 0)
            {
                TxtStVsBest.Text = "★ meilleure moyenne";
                TxtStVsBest.Foreground = Brushes.LightGreen;
            }
            else
            {
                TxtStVsBest.Text = string.Format(Inv, "+{0} s vs {1}", Fmt.Seconds(TimeSpan.FromTicks(gap)), _referenceName);
                TxtStVsBest.Foreground = Brush("#FFEF9A9A");
            }
        }

        // ─── Selection ────────────────────────────────────────────────────────

        private void BtnValid_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.IsChecked = row.IsRhythmLap;
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.IsChecked = true;
        }

        private void BtnNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.IsChecked = false;
        }

        // ─── Stats ────────────────────────────────────────────────────────────

        private void Recalc()
        {
            var sel = new List<AnalysisLapRow>();
            foreach (var row in _rows) if (row.IsChecked) sel.Add(row);

            TxtSelSummary.Text = string.Format(Inv, "{0} tour(s) coché(s) sur {1}", sel.Count, _rows.Count);
            UpdateHighlights(sel);

            // Lap times
            long best = long.MaxValue, worst = 0, sum = 0; int cnt = 0;
            var secs = new List<double>();
            foreach (var row in sel)
            {
                if (!row.IsValid) continue;
                var t = row.Lap.LapTimeTicks;
                if (t <= 0) continue;
                sum += t; cnt++;
                secs.Add(TimeSpan.FromTicks(t).TotalSeconds);
                if (t < best) best = t;
                if (t > worst) worst = t;
            }
            AverageTicks = cnt > 0 ? sum / cnt : 0;
            if (cnt > 0)
            {
                double mean = TimeSpan.FromTicks(AverageTicks).TotalSeconds, sq = 0;
                foreach (var s in secs) sq += (s - mean) * (s - mean);
                double sigma = cnt > 1 ? Math.Sqrt(sq / (cnt - 1)) : 0;

                TxtStAvg.Text       = Fmt.Lap(TimeSpan.FromTicks(AverageTicks));
                TxtStBestWorst.Text = string.Format(Inv, "{0}  ·  {1}",
                    Fmt.Lap(TimeSpan.FromTicks(best)), Fmt.Lap(TimeSpan.FromTicks(worst)));
                TxtStSigma.Text  = cnt > 1 ? string.Format(Inv, "± {0:0.000} s", sigma) : "—";
                TxtStSpread.Text = "meilleur / pire : " + Fmt.Seconds(TimeSpan.FromTicks(worst - best));
            }
            else
            {
                TxtStAvg.Text = TxtStSigma.Text = "—";
                TxtStBestWorst.Text = TxtStSpread.Text = "";
            }
            UpdateVsBest();

            UpdateFuel(sel);
            UpdateEnergy(sel);
            Tyre(SqFL, TxtStTireFL, WearPerLap(sel, x => x.WearLapFL));
            Tyre(SqFR, TxtStTireFR, WearPerLap(sel, x => x.WearLapFR));
            Tyre(SqRL, TxtStTireRL, WearPerLap(sel, x => x.WearLapRL));
            Tyre(SqRR, TxtStTireRR, WearPerLap(sel, x => x.WearLapRR));
            UpdatePits(sel);

            if (StatsChanged != null) StatsChanged(this);
        }

        private void UpdateHighlights(List<AnalysisLapRow> sel)
        {
            long best = long.MaxValue, worst = 0;
            float b1 = float.MaxValue, b2 = float.MaxValue, b3 = float.MaxValue;
            int valid = 0;
            foreach (var row in sel)
            {
                if (!row.IsValid) continue;
                var l = row.Lap;
                if (l.LapTimeTicks > 0)
                {
                    valid++;
                    if (l.LapTimeTicks < best)  best  = l.LapTimeTicks;
                    if (l.LapTimeTicks > worst) worst = l.LapTimeTicks;
                }
                if (l.S1 > 0 && l.S1 < b1) b1 = l.S1;
                if (l.S2 > 0 && l.S2 < b2) b2 = l.S2;
                if (l.S3 > 0 && l.S3 < b3) b3 = l.S3;
            }
            foreach (var row in _rows)
            {
                var l = row.Lap;
                bool on = row.IsChecked && row.IsValid;
                row.SetHighlights(
                    on && l.LapTimeTicks == best,
                    on && valid > 1 && l.LapTimeTicks == worst,
                    on && l.S1 > 0 && l.S1 == b1,
                    on && l.S2 > 0 && l.S2 == b2,
                    on && l.S3 > 0 && l.S3 == b3);
            }
        }

        // Litres when live data exists, otherwise % of tank from the XML.
        // Pit laps are excluded: refuelling makes their consumption meaningless.
        private void UpdateFuel(List<AnalysisLapRow> sel)
        {
            bool litres = false;
            foreach (var row in sel) if (row.HasLiveFuel) { litres = true; break; }

            float tot = 0f, left = 0f; int cnt = 0;
            foreach (var row in sel)
            {
                if (row.IsPitLap) continue;
                float used = litres ? row.Lap.LiveFuelUsedL : row.Lap.FuelUsed * 100f;
                if (used <= 0) continue;
                tot += used; cnt++;
                float l = litres ? row.Lap.LiveFuelLevelL : row.Lap.FuelRemaining * 100f;
                if (l > 0) left = l;
            }
            string unit = litres ? "L" : "% rés.";
            if (cnt == 0) { TxtStFuelAvg.Text = "—"; return; }
            TxtStFuelAvg.Text = string.Format(Inv, "{0:F2} {1}/tour", tot / cnt, unit);
        }

        private void UpdateEnergy(List<AnalysisLapRow> sel)
        {
            float tot = 0f; int cnt = 0;
            foreach (var row in sel)
                if (!row.IsPitLap && row.Lap.VirtualEnergyUsed > 0) { tot += row.Lap.VirtualEnergyUsed * 100f; cnt++; }
            if (cnt == 0) { TxtStVeAvg.Text = "—"; TxtStVeTot.Text = ""; return; }
            float avg = tot / cnt;
            TxtStVeAvg.Text = string.Format(Inv, "{0:F2} %/tour", avg);
            TxtStVeTot.Text = string.Format(Inv, "100 % = {0:F1} tours", 100f / avg);
        }

        private static double WearPerLap(List<AnalysisLapRow> rows, Func<AnalysisLapRow, float> wear)
        {
            float sum = 0f; int cnt = 0;
            foreach (var row in rows) { float w = wear(row); if (w > 0) { sum += w; cnt++; } }
            return cnt > 0 ? sum / cnt : double.NaN;
        }

        /// <summary>A tyre of the top view: coloured by its wear per lap (green light, amber, coral heavy), the value beside it.</summary>
        private static void Tyre(Border tyre, TextBlock text, double wear)
        {
            if (double.IsNaN(wear)) { text.Text = "—"; tyre.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)); return; }
            text.Text = string.Format(Inv, "{0:F2} %", wear);
            double k = Math.Max(0, Math.Min(1, (wear - 0.3) / 0.9));   // 0.3 %/lap → green, 1.2 %/lap → coral
            var green = Color.FromRgb(0x4C, 0xAF, 0x7D); var amber = Color.FromRgb(0xF2, 0xB1, 0x3C); var coral = Color.FromRgb(0xEF, 0x6C, 0x5A);
            Func<Color, Color, double, Color> mix = (a, b, t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
            var c = k < 0.5 ? mix(green, amber, k * 2) : mix(amber, coral, (k - 0.5) * 2);
            tyre.Background = new LinearGradientBrush(c, Color.FromArgb(200, c.R, c.G, c.B), 90);
        }

        // Measured when captured live, otherwise estimated from lap times:
        // (in-lap + out-lap) minus twice the average green-flag lap.
        private void UpdatePits(List<AnalysisLapRow> sel)
        {
            long refSum = 0; int refCnt = 0;
            foreach (var row in sel)
                if (row.IsValid && !row.IsPitLap && row.Lap.LapTimeTicks > 0) { refSum += row.Lap.LapTimeTicks; refCnt++; }
            double refSec = refCnt > 0 ? TimeSpan.FromTicks(refSum / refCnt).TotalSeconds : 0;

            var items = new List<PitItem>();
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (!row.IsPitLap) continue;
                double loss = double.NaN;
                double inLap = Duration(row.Lap);
                if (refSec > 0 && inLap > 0)
                {
                    loss = inLap - refSec;
                    // The out-lap counts only when it is a real lap that follows (a return to the
                    // garage cuts it short; on a shared car the next lap may be another stint)
                    bool next = i + 1 < _rows.Count && !_rows[i + 1].IsPitLap && _rows[i + 1].LapNum == row.LapNum + 1;
                    double outLap = next ? Duration(_rows[i + 1].Lap) : 0;
                    if (outLap > refSec * 0.85) loss += outLap - refSec;
                }
                var parts = new List<string> { "Tour " + row.LapNum + (double.IsNaN(loss) ? "" : string.Format(Inv, "  ·  temps perdu ≈ {0:0.0} s", loss)) };
                var p = row.Lap.LivePit;
                if (p != null)
                {
                    parts.Add(string.Format(Inv, "voie {0:F1} s  ·  arrêt {1:F1} s", p.LaneSec, p.StoppedSec));
                    if (p.FuelAddedL > 0.5) parts.Add(string.Format(Inv, "+{0:F1} L", p.FuelAddedL));
                    if (p.TyresCount > 0) parts.Add(p.TyresCount == 1 ? "1 pneu" : p.TyresCount + " pneus");
                    else if (p.TyresChanged) parts.Add("pneus changés");
                }
                else parts.Add("détail non capturé (SimHub fermé)");
                items.Add(new PitItem { Lap = row.LapNum, Loss = loss, Tyres = p != null ? (p.TyresCount > 0 ? p.TyresCount : p.TyresChanged ? 4 : 0) : -1, Detail = string.Join("\n", parts) });
            }
            _pits = items;
            double total = items.Where(x => !double.IsNaN(x.Loss)).Sum(x => x.Loss);
            TxtPitSummary.Text = items.Count == 0 ? "aucun" : string.Format(Inv, "{0}  ·  {1:0} s perdues", items.Count, total);
            DrawPits();
        }

        private List<PitItem> _pits = new List<PitItem>();

        private void PitStrip_SizeChanged(object sender, SizeChangedEventArgs e) { DrawPits(); }

        /// <summary>Laps on a line, one mark per stop (width = time lost), its lap under it; details on hover.</summary>
        private void DrawPits()
        {
            PitStrip.Children.Clear();
            double w = PitStrip.ActualWidth;
            if (w < 20 || _rows.Count == 0) return;
            int first = _rows[0].LapNum, last = _rows[_rows.Count - 1].LapNum;
            double span = Math.Max(1, last - first + 1);
            Func<double, double> X = lap => (lap - first) / span * w;
            var axis = new System.Windows.Shapes.Rectangle { Width = w, Height = 3, Fill = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), RadiusX = 1.5, RadiusY = 1.5 };
            Canvas.SetTop(axis, 13);
            PitStrip.Children.Add(axis);
            double refSec = 0;
            foreach (var r in _rows) if (r.IsValid && !r.IsPitLap && r.Lap.LapTimeTicks > 0) { refSec = r.Lap.LapTime.TotalSeconds; break; }
            foreach (var p in _pits)
            {
                // A lap is about refSec long on this scale: the mark is as wide as the time lost
                double width = refSec > 0 && !double.IsNaN(p.Loss) ? Math.Max(6, p.Loss / refSec * w / span) : 6;
                var mark = new Border
                {
                    Width = width, Height = 20, CornerRadius = new CornerRadius(3), ToolTip = p.Detail,
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)),
                };
                Canvas.SetLeft(mark, Math.Min(w - width, X(p.Lap)));
                Canvas.SetTop(mark, 4);
                PitStrip.Children.Add(mark);
                var label = new TextBlock { Text = "T" + p.Lap, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) };
                Canvas.SetLeft(label, Math.Min(w - 26, Math.Max(0, X(p.Lap) - 4)));
                Canvas.SetTop(label, 27);
                PitStrip.Children.Add(label);
            }
        }

        public class PitItem
        {
            public int Lap;
            public double Loss;
            public int Tyres;      // tyres changed, -1 unknown
            public string Detail;
        }

        /// <summary>Official time, or the rebuilt one for laps LMU did not time (pit laps).</summary>
        private static double Duration(LmuLapRecord l)
        {
            if (l.LapTimeTicks > 0) return l.LapTime.TotalSeconds;
            return l.ReconstructedTicks > 0 ? TimeSpan.FromTicks(l.ReconstructedTicks).TotalSeconds : 0;
        }

        // ─── Live data merge ──────────────────────────────────────────────────

        private static float WearDrop(float prev, float cur)
        {
            if (prev <= 0 || cur <= 0 || cur > prev) return 0f;
            return (prev - cur) * 100f;
        }

        // The session start written by LMU is not the real green flag: it is off by a few
        // minutes, differently for each driver. Laps are therefore matched on lap number, and
        // when several captured laps share a number (another session in the window), the one
        // closest to the session's typical clock offset wins.
        internal static int MergeLiveData(LmuSessionRecord r, List<LmuLapRecord> laps, string dbPath)
        {
            if (r.BlockStartUtc == DateTime.MinValue || laps.Count == 0) return 0;

            float lastEt = 0f;
            foreach (var l in laps) if (l.ElapsedTime > lastEt) lastEt = l.ElapsedTime;
            var from = r.BlockStartUtc.AddMinutes(-15);
            var to   = r.BlockStartUtc.AddSeconds(lastEt).AddMinutes(15);

            var live = LmuSessionStore.GetLiveLaps(from, to, dbPath);
            var pits = LmuSessionStore.GetLivePits(from, to, dbPath);
            if (live.Count == 0 && pits.Count == 0) return 0;

            Func<LmuLapRecord, long> xmlStart = l => r.BlockStartUtc.AddSeconds(l.ElapsedTime).Ticks;

            var offsets = new List<long>();
            foreach (var l in laps)
                foreach (var s in live)
                    if (s.LapNum == l.LapNum) offsets.Add(s.StartUtcTicks - xmlStart(l));
            if (offsets.Count == 0) return 0;
            offsets.Sort();
            long median = offsets[offsets.Count / 2];

            int matched = 0;
            foreach (var l in laps)
            {
                LmuLiveLap best = null;
                long bestGap = long.MaxValue;
                foreach (var s in live)
                {
                    if (s.LapNum != l.LapNum) continue;
                    long gap = Math.Abs(s.StartUtcTicks - xmlStart(l) - median);
                    if (gap < bestGap) { bestGap = gap; best = s; }
                }
                if (best != null) { ApplyLive(l, best); matched++; }
            }

            // A pit entry makes its lap an in-lap. Lap 1 is never one: in practice and qualifying
            // you start in the garage, and leaving it is recorded as a pit-lane passage.
            long maxPitGap = TimeSpan.FromMinutes(10).Ticks;
            foreach (var p in pits)
            {
                if (IsGarageExit(p, live)) continue;
                foreach (var l in laps)
                {
                    if (l.LapNum != p.LapNum || l.LapNum <= 1 || l.LivePit != null) continue;
                    if (Math.Abs(p.EntryUtcTicks - xmlStart(l) - median) > maxPitGap) continue;
                    l.LivePit = p;
                    l.IsPitLap = true;
                    break;
                }
            }
            return matched;
        }

        /// <summary>
        /// Escape → garage ends the lap and puts the car back in the lane; driving out is a lane
        /// passage, not a stop. Captures made before v2.6 have no flag: such a passage starts at
        /// the very instant a new lap starts, with nothing serviced.
        /// </summary>
        private static bool IsGarageExit(LmuLivePit p, List<LmuLiveLap> live)
        {
            if (p.FromGarage) return true;
            if (p.FuelAddedL > 0.5 || p.TyresChanged) return false;
            long tolerance = TimeSpan.FromSeconds(2).Ticks;
            foreach (var s in live)
                if (Math.Abs(s.StartUtcTicks - p.EntryUtcTicks) < tolerance) return true;
            return false;
        }

        /// <summary>
        /// LMU gives no time for invalidated laps, but each lap carries its start time:
        /// an invalid lap lasted until the next lap started.
        /// </summary>
        private static void ReconstructInvalidTimes(List<LmuLapRecord> laps)
        {
            for (int i = 0; i + 1 < laps.Count; i++)
            {
                var l = laps[i];
                if (l.IsValid || l.ReconstructedTicks > 0) continue;
                // A lap starting at session opening includes the time spent in the garage
                if (l.ElapsedTime < 1f) continue;
                // Shared car: the next lap in the list may be the next stint of this driver
                if (laps[i + 1].LapNum != l.LapNum + 1) continue;
                double sec = laps[i + 1].ElapsedTime - l.ElapsedTime;
                if (sec > 10 && sec < 3600) l.ReconstructedTicks = TimeSpan.FromSeconds(sec).Ticks;
            }
        }

        private static void ApplyLive(LmuLapRecord l, LmuLiveLap s)
        {
            l.HasLive        = true;
            l.LiveFuelUsedL  = (float)s.FuelUsedL;
            l.LiveFuelLevelL = (float)s.FuelLevelL;
            l.LiveWearFL     = (float)s.WearFL;
            l.LiveWearFR     = (float)s.WearFR;
            l.LiveWearRL     = (float)s.WearRL;
            l.LiveWearRR     = (float)s.WearRR;
            // InPitLane is also true for out-laps (lap started in the lane): pit laps come
            // from the XML flag and from captured pit entries, not from this.
        }
    }
}
