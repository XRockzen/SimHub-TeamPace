using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>
    /// A race, as the car lived it: against the team's plan when there is one (key figures,
    /// planned and real stints, stop by stop, what the laps were lost on), then every car of the
    /// category. The same block in a session's "Course" tab and in the strategy's analysis.
    /// </summary>
    internal class RaceReviewView : StackPanel
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Brush StopBrush = Ui.Frozen(Color.FromRgb(0xFF, 0xC8, 0x57));
        private static readonly Brush Late = Ui.Frozen(Color.FromRgb(0xEF, 0x53, 0x50));

        private readonly List<DriverView> _drivers;
        private readonly StrategyPlan _plan;
        private bool _energy = true;

        /// <summary>The team's plan for a circuit and category: the most recently saved one.</summary>
        public static StrategyPlan PlanFor(string circuit, string classLabel)
        {
            try
            {
                var p = StrategyEngine.SavedPlans().Where(x => x.Circuit == circuit && x.ClassLabel == classLabel)
                                      .OrderByDescending(x => x.UpdatedUtc).FirstOrDefault();
                if (p != null) p.Normalize();
                return p;
            }
            catch { return null; }
        }

        /// <summary>Every car of the race, and what SimHub captured at our stops.</summary>
        public static RaceField FieldFor(List<DriverView> drivers, LmuSessionRecord race, Dictionary<int, LmuLivePit> pits)
        {
            RaceField field = null;
            foreach (var d in drivers)
            {
                var mine = d.FindMatch(race);
                if (mine == null) continue;
                if (field == null) field = LmuSessionStore.GetField(mine.FileId, d.DbPath) ?? TeamRaceStore.FieldFor(mine);
                if (pits == null) continue;
                try
                {
                    var laps = LmuSessionStore.GetLapsForSession(mine.FileId, d.DbPath);
                    AnalysisColumn.MergeLiveData(mine, laps, d.DbPath);
                    foreach (var l in laps) if (l.LivePit != null && !l.LivePit.FromGarage && !pits.ContainsKey(l.LapNum)) pits[l.LapNum] = l.LivePit;
                }
                catch { }
            }
            return field != null && field.Cars.Count > 1 ? field : null;
        }

        public RaceReviewView(List<DriverView> drivers, LmuSessionRecord race, StrategyPlan plan)
        {
            _drivers = drivers;
            _plan = plan;
            Children.Add(Ui.T("Chargement de la course…", 13, Ui.Faint));
            // Laps of every driver, live captures, measures and simulation: off the interface thread
            System.Threading.Tasks.Task.Run(() =>
            {
                RaceField field = null; StrategyEngine.Simulation sim = null; StrategyReview review = null;
                try
                {
                    var pits = new Dictionary<int, LmuLivePit>();
                    field = FieldFor(drivers, race, pits);
                    if (field != null)
                    {
                        if (plan != null && plan.Drivers.Count > 0)
                        {
                            try
                            {
                                var measures = StrategyEngine.Measure(drivers, plan.Circuit, plan.ClassLabel, plan.TankL);
                                StrategyEngine.ApplyOverrides(measures, plan);
                                sim = StrategyEngine.Simulate(plan, measures, false);
                                if (sim.Stints.Count == 0) sim = null;
                                else _energy = sim.EnergyMode;
                            }
                            catch { sim = null; }
                        }
                        review = StrategyReview.Build(field, sim, pits);
                    }
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Race review: " + ex.Message); }
                Dispatcher.BeginInvoke(new Action(() => Show(field, sim, review)));
            });
        }

        private void Show(RaceField field, StrategyEngine.Simulation sim, StrategyReview review)
        {
            Children.Clear();
            if (field == null)
            {
                Children.Add(Ui.T("Les données de toutes les voitures manquent pour cette course : « ⟳ Tout mettre à jour ».", 13, Ui.Muted));
                return;
            }
            if (review != null)
            {
                Children.Add(Kpis(sim, review));
                Children.Add(PlannedVsReal(sim, review));
                if (sim != null)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(StopsReview(review));
                    var verdict = Verdict(sim, review);
                    Grid.SetColumn(verdict, 2);
                    row.Children.Add(verdict);
                    Children.Add(row);
                }
            }
            Children.Add(new RaceFieldView(field));
        }

        // ─── Key figures ──────────────────────────────────────────────────────

        private UIElement Kpis(StrategyEngine.Simulation sim, StrategyReview r)
        {
            var kpis = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 10) };
            // delta = real − planned; worse when it grows, except for the laps
            Func<string, string, string, double, string, UIElement> pair = (label, planned, real, delta, tip) =>
            {
                var p = new StackPanel { ToolTip = tip };
                p.Children.Add(Ui.T(label, 11, Ui.Muted));
                var v = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text };
                v.Inlines.Add(new System.Windows.Documents.Run(sim != null ? planned + " / " + real : real));
                bool worse = label == "TOURS" ? delta < 0 : delta > 0;
                if (sim != null && Math.Abs(delta) > 0.001)
                    v.Inlines.Add(new System.Windows.Documents.Run("  " + (delta > 0 ? "+" : "−") + DeltaText(label, Math.Abs(delta))) { FontSize = 13, Foreground = worse ? Ui.Red : Ui.Green });
                p.Children.Add(v);
                var c = Ui.Card(p); c.Margin = new Thickness(0, 0, 8, 0);
                return c;
            };
            string pr = sim != null ? "prévu / réel" : null;
            int planLaps = sim != null ? sim.Laps : 0;
            kpis.Children.Add(pair("TOURS", planLaps.ToString(), r.Run.Laps.ToString(), sim != null ? r.Run.Laps - planLaps : 0, pr));
            kpis.Children.Add(pair("ARRÊTS", sim != null ? sim.Stops.Count.ToString() : "", r.Stops.Count.ToString(), sim != null ? r.Stops.Count - sim.Stops.Count : 0, pr));
            kpis.Children.Add(pair("AUX STANDS", sim != null ? Duration(sim.PitLoss) : "", Duration(r.Run.PitLoss), sim != null ? r.Run.PitLoss - sim.PitLoss : 0, pr));
            double planPace = sim != null && sim.Laps > 0 ? (sim.Finish - sim.PitLoss) / sim.Laps : 0;
            kpis.Children.Add(pair("RYTHME", sim != null ? Fmt.LapSec(planPace) : "", Fmt.LapSec(r.Run.Pace), sim != null ? r.Run.Pace - planPace : 0, pr));

            var drv = new StackPanel();
            drv.Children.Add(Ui.T("TOURS PAR PILOTE", 11, Ui.Muted));
            var line = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0) };
            foreach (var kv in r.LapsByDriver)
            {
                var d = DriverByName(kv.Key);
                line.Inlines.Add(new System.Windows.Documents.Run(First(kv.Key) + " " + kv.Value + "   ") { Foreground = d != null ? Ui.Light(d.Brush) : Ui.Text });
            }
            drv.Children.Add(line);
            if (sim != null)
            {
                var planned = new Dictionary<string, int>();
                foreach (var st in sim.Stints) { int c; planned.TryGetValue(st.Driver, out c); planned[st.Driver] = c + st.Laps; }
                drv.ToolTip = "Prévu : " + string.Join(" · ", planned.Select(kv => kv.Key + " " + kv.Value));
            }
            var dc = Ui.Card(drv); dc.Margin = new Thickness(0, 0, 8, 0);
            kpis.Children.Add(dc);
            return kpis;
        }

        private static string DeltaText(string label, double v)
        {
            if (label == "AUX STANDS") return Duration(v);
            if (label == "RYTHME") return v.ToString("0.00", Inv);
            return v.ToString("0", Inv);
        }

        // ─── Planned and real stints ──────────────────────────────────────────

        private UIElement PlannedVsReal(StrategyEngine.Simulation sim, StrategyReview r)
        {
            var p = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var legend = new StackPanel { Orientation = Orientation.Horizontal };
            legend.Children.Add(Swatch(StopBrush, sim != null ? "arrêt dans le plan" : "arrêt"));
            if (sim != null) legend.Children.Add(Swatch(Late, "hors plan ou trop long"));
            DockPanel.SetDock(legend, Dock.Right);
            head.Children.Add(legend);
            head.Children.Add(Ui.T(sim != null ? "Prévu / réel" + (_plan != null && !string.IsNullOrEmpty(_plan.Id) ? "  ·  " + _plan.Id : "") : "Relais et arrêts", 14.5, Ui.Text, FontWeights.SemiBold));
            p.Children.Add(head);
            double total = Math.Max(r.Finish, sim != null ? sim.Finish : 0);

            Func<string, Grid, UIElement> row = (label, bar) =>
            {
                var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var l = Ui.T(label, 12.5, Ui.Muted);
                l.Width = 56;
                DockPanel.SetDock(l, Dock.Left);
                d.Children.Add(l);
                d.Children.Add(bar);
                return d;
            };
            Action<Grid, double, Brush, string, string> seg = (g, width, brush, text, tip) =>
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, width), GridUnitType.Star) });
                var b = new Border { Background = brush, ToolTip = tip, ClipToBounds = true };
                if (text != null) { var t = Ui.T(text, 12, Brushes.White, FontWeights.SemiBold); t.Margin = new Thickness(7, 0, 4, 0); b.Child = t; }
                Grid.SetColumn(b, g.ColumnDefinitions.Count - 1);
                g.Children.Add(b);
            };

            if (sim != null)
            {
                var g = new Grid { Height = 32 };
                double t = 0;
                for (int i = 0; i < sim.Stints.Count; i++)
                {
                    var st = sim.Stints[i];
                    var d = DriverByName(st.Driver);
                    seg(g, st.End - t, d != null ? d.Brush : Brushes.Gray, First(st.Driver) + " · " + st.Laps + " t", string.Format("Relais prévu {0} : {1} tours", i + 1, st.Laps));
                    t = st.End;
                    if (i < sim.Stops.Count)
                    {
                        seg(g, sim.Stops[i].Lost, StopBrush, null, string.Format(Inv, "Arrêt prévu {0} · T{1} · +{2:0} s", i + 1, sim.Stops[i].Lap, sim.Stops[i].Lost));
                        t += sim.Stops[i].Lost;
                    }
                }
                if (total > t) seg(g, total - t, Brushes.Transparent, null, null);
                p.Children.Add(row("Prévu", g));
            }
            {
                var g = new Grid { Height = 32 };
                double t = 0;
                var bad = new HashSet<StrategyReview.RealStop>(r.Rows.Where(x => x.Mood < 0 && x.Real != null).Select(x => x.Real));
                for (int i = 0; i < r.Stints.Count; i++)
                {
                    var st = r.Stints[i];
                    var d = DriverByName(st.Driver);
                    seg(g, st.End - t, d != null ? d.Brush : Brushes.Gray, First(st.Driver) + " · " + st.Laps + " t", string.Format("Relais {0} : {1}, T{2}–T{3}", i + 1, st.Driver, st.FromLap, st.ToLap));
                    t = st.End;
                    if (i < r.Stops.Count)
                    {
                        var stop = r.Stops[i];
                        seg(g, stop.Loss, bad.Contains(stop) ? Late : StopBrush, null, StrategyReview.Describe(stop, Clock));
                        t += stop.Loss;
                    }
                }
                if (total > t) seg(g, total - t, Brushes.Transparent, null, null);
                p.Children.Add(row(sim != null ? "Réel" : "", g));
            }
            var c = Card(p);
            c.Margin = new Thickness(0, 0, 0, 10);
            return c;
        }

        private static UIElement Swatch(Brush color, string text)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
            s.Children.Add(new Border { Width = 10, Height = 10, Background = color, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            s.Children.Add(Ui.T(text, 12, Ui.Muted));
            return s;
        }

        // ─── Stop by stop ─────────────────────────────────────────────────────

        private UIElement StopsReview(StrategyReview r)
        {
            var table = new StackPanel();
            var widths = new[] { 58.0, 2.1, 2.6, 70, 2.3 };
            Func<bool, Grid> grid = head =>
            {
                var g = new Grid { MinHeight = head ? 26 : 30 };
                foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 10 ? new GridLength(w) : new GridLength(w, GridUnitType.Star) });
                return g;
            };
            var h = grid(true);
            var names = new[] { "Arrêt", "Prévu", "Réel", "Écart", "Constat" };
            for (int i = 0; i < names.Length; i++)
            {
                var t = Ui.T(names[i], 12, Ui.Faint);
                t.Margin = new Thickness(10, 0, 10, 0);
                if (i == 3) t.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(t, i);
                h.Children.Add(t);
            }
            table.Children.Add(new Border { Child = h, Background = Ui.HeaderBg });
            foreach (var row in r.Rows)
            {
                var g = grid(false);
                var planned = row.Planned != null
                    ? string.Format(Inv, "T{0} · {1} · {2} · {3} · +{4:0} s", row.Planned.Lap, Clock(row.Planned.At),
                        string.Format(Inv, "{0:0} {1}", row.Planned.Level, _energy ? "%" : "L"),
                        row.Planned.Tyres == 0 ? "0 pneu" : row.Planned.Tyres + " pneus", row.Planned.Lost)
                    : "—";
                var real = row.Real != null ? StrategyReview.Describe(row.Real, Clock) : "—";
                if (row.Real != null && row.Real.Pit != null && row.Real.Pit.LaneSec > 0)
                    real += string.Format(Inv, " · voie {0:0} s", row.Real.Pit.LaneSec);
                Brush mood = row.Mood > 0 ? Ui.Green : row.Mood < 0 ? Ui.Red : Ui.Muted;
                var cells = new[]
                {
                    row.PlannedIndex >= 0 ? (row.PlannedIndex + 1).ToString() : "+",
                    planned, real,
                    string.Format(Inv, "{0}{1:0} s", row.Delta >= 0 ? "+" : "−", Math.Abs(row.Delta)),
                    row.Verdict,
                };
                for (int i = 0; i < cells.Length; i++)
                {
                    var t = Ui.T(cells[i], 13, i == 1 && row.Planned == null ? Ui.Faint : i >= 3 ? mood : Ui.Text, i == 0 ? FontWeights.Bold : FontWeights.Normal);
                    t.Margin = new Thickness(10, 0, 10, 0);
                    t.ToolTip = cells[i];
                    if (i == 3) t.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(t, i);
                    g.Children.Add(t);
                }
                table.Children.Add(new Border
                {
                    Child = g, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                    Background = row.Mood < 0 ? Ui.Frozen(Color.FromArgb(26, 0xEF, 0x53, 0x50)) : Brushes.Transparent,
                });
            }
            return new Border { Background = Ui.Panel, CornerRadius = new CornerRadius(8), Child = table, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top };
        }

        private UIElement Verdict(StrategyEngine.Simulation sim, StrategyReview r)
        {
            var p = new StackPanel();
            p.Children.Add(Ui.T("Bilan", 14.5, Ui.Text, FontWeights.SemiBold));
            Action<string, Brush> say = (text, color) => p.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            double pace = r.Run.Pace > 0 ? r.Run.Pace : 100;
            int missing = sim.Laps - r.Run.Laps;
            var causes = new List<string>();
            foreach (var u in r.Rows.Where(x => x.PlannedIndex < 0 && x.Real != null))
                causes.Add(string.Format(Inv, "arrêt imprévu au tour {0} (≈ {1:0.0} t)", u.Real.Lap, u.Real.Loss / pace));
            foreach (var o in r.Rows.Where(x => x.PlannedIndex >= 0 && x.Real != null && x.Delta > 20))
                causes.Add(string.Format(Inv, "arrêt {0} trop long (≈ {1:0.0} t)", o.PlannedIndex + 1, o.Delta / pace));
            if (missing > 0) say(string.Format("−{0} tour{1} sur le plan{2}", missing, missing > 1 ? "s" : "", causes.Count > 0 ? " : " + string.Join(", ", causes) + "." : "."), Ui.Text);
            else if (missing < 0) say(string.Format("+{0} tour{1} sur le plan.", -missing, missing < -1 ? "s" : ""), Ui.Green);
            else say("Le nombre de tours prévu est tenu.", Ui.Green);
            var ok = r.Rows.Where(x => x.PlannedIndex >= 0 && x.Mood > 0).Select(x => (x.PlannedIndex + 1).ToString()).ToList();
            if (ok.Count > 0) say(string.Format("Arrêt{0} {1} dans le plan.", ok.Count > 1 ? "s" : "", string.Join(", ", ok)), Ui.Green);
            double planPace = sim.Laps > 0 ? (sim.Finish - sim.PitLoss) / sim.Laps : 0;
            if (planPace > 0)
            {
                double d = r.Run.Pace - planPace;
                say(string.Format(Inv, "Rythme {0}{1:0.00} s au tour sur le prévu (≈ {2:0} s).", d >= 0 ? "+" : "−", Math.Abs(d), Math.Abs(d) * r.Run.Laps), d > 0.1 ? Ui.Red : Ui.Green);
            }
            var c = Card(p);
            c.VerticalAlignment = VerticalAlignment.Top;
            return c;
        }

        // ─── Small helpers ────────────────────────────────────────────────────

        private DriverView DriverByName(string name) { return _drivers.Find(d => TeamRace.SameDriver(d.Name, name)); }

        private static string First(string name) { return string.IsNullOrEmpty(name) ? "?" : name.Trim().Split(' ')[0]; }

        private string Clock(double raceSec)
        {
            TimeSpan start;
            if (_plan != null && !string.IsNullOrEmpty(_plan.StartClock) && TimeSpan.TryParse(_plan.StartClock, Inv, out start))
                return (start + TimeSpan.FromSeconds(raceSec)).ToString(@"hh\:mm");
            var t = TimeSpan.FromSeconds(Math.Max(0, raceSec));
            return string.Format("{0}h{1:00}", (int)t.TotalHours, t.Minutes);
        }

        public static string Duration(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Abs(sec));
            return t.TotalMinutes >= 1 ? string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds) : string.Format("{0} s", t.Seconds);
        }

        private static Border Card(UIElement child) { return Ui.Card(child, new Thickness(14, 12, 14, 12)); }
    }
}
