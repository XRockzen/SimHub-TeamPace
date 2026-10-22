using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>
    /// The pitwall, for the teammate who follows the race in the session while another drives:
    /// the stint under way, the fuel, the next stop as recalculated at each lap and what to set
    /// in the pit menu for it (driver, level, tyres), the stops after, the last laps.
    /// His own SimHub reads the car he is watching: no link between the computers is needed.
    /// </summary>
    internal class PitwallView : OverlayCanvas
    {
        public const double W = OverlaySettings.PitWidth, H = OverlaySettings.PitHeight;

        private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
        private static readonly Typeface Reg = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.SemiCondensed);
        private static readonly Typeface Bold = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.SemiCondensed);
        private static Brush B(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
        private static readonly Brush Text = B(255, 0xE8, 0xE9, 0xEC), Muted = B(150, 255, 255, 255), Faint = B(90, 255, 255, 255);
        private static readonly Brush Track = B(20, 255, 255, 255), Sep = B(22, 255, 255, 255);
        private static readonly Brush Green = B(255, 0x66, 0xBB, 0x6A), Red = B(255, 0xEF, 0x9A, 0x9A), Amber = B(255, 0xFF, 0xC8, 0x57), Blue = B(255, 0x42, 0xA5, 0xF5);
        private static readonly Pen BorderPen = MakePen(B(24, 255, 255, 255), 1), EditPen = MakePen(Amber, 2, true);
        private double _ppd = 1;

        private static Pen MakePen(Brush b, double w, bool dashed = false)
        {
            var p = new Pen(b, w);
            if (dashed) p.DashStyle = new DashStyle(new double[] { 2, 1.5 }, 0);
            p.Freeze();
            return p;
        }

        public PitwallView()
        {
            Width = W; Height = H;
            Loaded += (s, e) => { try { _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { } };
        }

        private double T(DrawingContext dc, string s, double x, double y, double size, Brush b, bool bold = false, int align = 0)
        {
            var ft = new FormattedText(s ?? "", Fr, FlowDirection.LeftToRight, bold ? Bold : Reg, size, b, _ppd);
            if (align == 1) x -= ft.Width; else if (align == 2) x -= ft.Width / 2;
            dc.DrawText(ft, new Point(x, y));
            return ft.Width;
        }

        private static string Signed(double v, string f) { return (v < 0 ? "−" : "+") + Math.Abs(v).ToString(f, Fr); }
        private static string Lap(double s) { return double.IsNaN(s) || s <= 0 ? "—" : Fmt.LapSec(s); }
        private static string Clock(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return string.Format("{0}h{1:00}", (int)t.TotalHours, t.Minutes);
        }

        /// <summary>Everything drawn, from the race or made up for the preview.</summary>
        private class Data
        {
            public string Plan, Driver, Error;
            public int Done, StintLaps, LiveLap = -1;
            public double TimeLeft = double.NaN, StintTime = double.NaN, LastLap = double.NaN, Pace = double.NaN, PlannedPace = double.NaN;
            public double Fuel, PerLap = double.NaN, Tank = 100;
            public bool PerLapMeasured, Energy, FuelEstimated;
            public List<StrategyEngine.StopPlan> Stops = new List<StrategyEngine.StopPlan>();
            public int FirstNumber;
            public int PlanLap;   // lap of the next stop in the original plan, when the live plan moved it
            public double[] Recent = new double[0];
        }

        private static Data Collect()
        {
            var d = new Data();
            var race = OverlayService.Race;
            if (OverlayService.Demo && (race == null || race.Sim == null)) return Demo();
            if (race == null || race.Sim == null) { d.Error = "Pas de plan de stratégie pour ce circuit : voir l'onglet Overlay."; return d; }
            if (race.Error != null) { d.Error = race.Error; return d; }
            var sim = OverlayService.LiveSim ?? race.Sim;
            d.Plan = race.Plan.Id;
            d.Done = OverlayService.Lap - 1;
            d.LiveLap = OverlayService.LiveSim != null ? OverlayService.LiveLap : -1;
            d.TimeLeft = OverlayService.SessionLeft > 0 ? OverlayService.SessionLeft : double.NaN;
            d.Driver = OverlayService.Spectating && OverlayService.CarDriver.Length > 0 ? OverlayService.CarDriver
                     : OverlayService.LiveSim != null && OverlayService.LiveSim.Stints.Count > 0 ? OverlayService.LiveSim.Stints[0].Driver : race.DriverOf(OverlayService.RaceStops);
            d.FuelEstimated = OverlayService.FuelEstimated;
            d.PlannedPace = race.PaceOf(d.Driver);
            d.StintLaps = OverlayService.StintLaps;
            double rt = OverlayService.RaceTime;
            d.StintTime = double.IsNaN(rt) ? double.NaN : rt - OverlayService.StintStart;
            d.Recent = OverlayService.RecentLaps;
            d.LastLap = d.Recent.Length > 0 ? d.Recent[d.Recent.Length - 1] : OverlayService.Spectating ? OverlayService.CarLastLap : double.NaN;
            d.Pace = OverlayService.LivePace;
            d.Fuel = OverlayService.Fuel;
            d.Tank = Math.Max(1, race.Plan.TankL);
            d.PerLapMeasured = !double.IsNaN(OverlayService.LiveFuel);
            d.PerLap = d.PerLapMeasured ? OverlayService.LiveFuel : race.FuelPerLap;
            d.Energy = sim.EnergyMode;
            int j = sim.Stops.FindIndex(st => st.Lap > d.Done);
            // The original plan, not recalculated yet: the next stop is the one after those made
            if (sim == race.Sim && OverlayService.RaceStops > 0 && OverlayService.IsRace) j = OverlayService.RaceStops < sim.Stops.Count ? Math.Max(j, OverlayService.RaceStops) : -1;
            if (j >= 0)
            {
                for (int k = j; k < sim.Stops.Count; k++) d.Stops.Add(sim.Stops[k]);
                d.FirstNumber = j + sim.FirstStop;
                int b = d.FirstNumber - race.Sim.FirstStop;
                if (sim != race.Sim && b >= 0 && b < race.Sim.Stops.Count && race.Sim.Stops[b].Lap != d.Stops[0].Lap) d.PlanLap = race.Sim.Stops[b].Lap;
            }
            return d;
        }

        private static Data Demo()
        {
            var d = new Data
            {
                Plan = "6 h de São Paulo", Driver = "Alex", Done = 41, StintLaps = 12, LiveLap = 41, TimeLeft = 3 * 3600 + 22 * 60,
                StintTime = 19 * 60 + 12, LastLap = 96.21, Pace = 96.05, PlannedPace = 95.9, Fuel = 52, PerLap = 3.12, PerLapMeasured = true, Tank = 100,
                Energy = true, FirstNumber = 4, PlanLap = 58, Recent = new[] { 96.4, 95.98, 96.1, 96.02, 96.31, 95.87, 96.05, 96.21 },
            };
            d.Stops.Add(new StrategyEngine.StopPlan { Lap = 57, At = 2 * 3600 + 32 * 60, Level = 100, EnergyPct = 93, FuelL = 89, Wheels = StrategyEngine.FL | StrategyEngine.FR, Tyres = 2, NextDriver = "Sam" });
            d.Stops.Add(new StrategyEngine.StopPlan { Lap = 86, At = 3 * 3600 + 20 * 60, Level = 100, EnergyPct = 95, FuelL = 91, Wheels = StrategyEngine.AllWheels, Tyres = 4, NextDriver = "Alex" });
            d.Stops.Add(new StrategyEngine.StopPlan { Lap = 115, At = 4 * 3600 + 8 * 60, Level = 64, EnergyPct = 58, FuelL = 55, Wheels = 0, Tyres = 0, NextDriver = "Sam" });
            return d;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var s = OverlaySettings.Current;
            var bg = new SolidColorBrush(Color.FromArgb((byte)(Math.Max(0.05, s.Opacity) * 255), 0x11, 0x14, 0x1A));
            dc.DrawRoundedRectangle(bg, BorderPen, new Rect(0.5, 0.5, W - 1, H - 1), 10, 10);
            if (EditMode) dc.DrawRoundedRectangle(null, EditPen, new Rect(1, 1, W - 2, H - 2), 10, 10);

            var d = Collect();
            double hw = T(dc, d.Plan != null ? "PITWALL · PLAN « " + d.Plan.ToUpperInvariant() + " »" : "PITWALL", 12, 8, 12, Muted, true);
            if (OverlayService.Demo) T(dc, "APERÇU", 12 + hw + 10, 8, 12, Amber, true);
            if (EditMode) T(dc, "Glissez pour placer · molette : taille", W / 2, 8, 12, Amber, false, 2);
            if (d.Error != null) { T(dc, d.Error, W / 2, 110, 15, Muted, false, 2); return; }
            T(dc, string.Format("TOUR {0}{1}{2}", d.Done, double.IsNaN(d.TimeLeft) ? "" : " · RESTE " + Clock(d.TimeLeft),
                d.LiveLap >= 0 ? " · RECALCULÉ AU TOUR " + d.LiveLap : " · PLAN D'ORIGINE"), W - 12, 8, 12, Muted, false, 1);

            double cw = (W - 24) / 4;
            for (int c = 1; c < 4; c++) dc.DrawRectangle(Sep, null, new Rect(12 + c * cw, 34, 1, 108));
            dc.DrawRectangle(Sep, null, new Rect(12, 152, W - 24, 1));

            // 1. The stint under way
            double x = 12;
            T(dc, "RELAIS EN COURS", x, 36, 11, Muted);
            T(dc, d.Driver ?? "—", x, 50, 22, Text, true);
            T(dc, d.StintLaps < 0 || double.IsNaN(d.StintTime) ? "relais commencé avant votre arrivée"
                  : string.Format("{0} tour{1} · {2}", d.StintLaps, d.StintLaps > 1 ? "s" : "", Fmt.TotalTime(TimeSpan.FromSeconds(d.StintTime))), x, 80, 13, Muted);
            T(dc, "dernier : " + Lap(d.LastLap), x, 100, 13, double.IsNaN(d.LastLap) || double.IsNaN(d.PlannedPace) ? Text : d.LastLap <= d.PlannedPace + 0.2 ? Green : Red);
            T(dc, "moyenne " + Lap(d.Pace) + " · prévu " + Lap(d.PlannedPace), x, 120, 12, Faint);

            // 2. Fuel
            x = 12 + cw + 12;
            var next = d.Stops.Count > 0 ? d.Stops[0] : null;
            int toGo = next != null ? Math.Max(0, next.Lap - d.Done) : 0;
            T(dc, d.FuelEstimated ? "ESSENCE · ESTIMÉE" : "ESSENCE", x, 36, 11, d.FuelEstimated ? Amber : Muted);
            T(dc, double.IsNaN(d.Fuel) ? "—" : (d.FuelEstimated ? "≈ " : "") + d.Fuel.ToString("0", Fr) + " L", x, 50, 22, Text, true);
            double bw = cw - 24;
            dc.DrawRoundedRectangle(Track, null, new Rect(x, 82, bw, 8), 4, 4);
            if (!double.IsNaN(d.Fuel)) dc.DrawRoundedRectangle(Blue, null, new Rect(x, 82, bw * Math.Max(0, Math.Min(1, d.Fuel / d.Tank)), 8), 4, 4);
            if (!double.IsNaN(d.PerLap) && d.PerLap > 0)
            {
                if (toGo > 0) dc.DrawRectangle(Amber, null, new Rect(x + bw * Math.Min(1, toGo * d.PerLap / d.Tank) - 1, 79, 2, 14));
                T(dc, double.IsNaN(d.Fuel) ? "inconnue jusqu'au prochain arrêt" : string.Format(Fr, "{0:0.00} L/tour {1}", d.PerLap, d.PerLapMeasured ? "mesurés" : "prévus"), x, 98, 12.5, Muted);
                if (toGo > 0 && !double.IsNaN(d.Fuel))
                {
                    double margin = d.Fuel / d.PerLap - toGo;
                    T(dc, "marge " + Signed(margin, "0.0") + " tour", x, 118, 13, margin >= 0.3 ? Green : margin >= 0 ? Amber : Red, true);
                }
            }

            // 3. The next stop, as recalculated
            x = 12 + 2 * cw + 12;
            if (next != null)
            {
                T(dc, "PROCHAIN ARRÊT · " + d.FirstNumber, x, 36, 11, Muted);
                double w = T(dc, toGo.ToString(), x, 48, 34, Text, true);
                T(dc, toGo > 1 ? "tours" : "tour", x + w + 6, 64, 15, Muted);
                T(dc, string.Format("au T{0} · vers {1}", next.Lap, Clock(next.At)), x, 98, 13, Muted);
                if (d.PlanLap > 0) T(dc, "plan d'origine : T" + d.PlanLap, x, 118, 12.5, Amber);
            }
            else
            {
                T(dc, "PROCHAIN ARRÊT", x, 36, 11, Muted);
                T(dc, "jusqu'au drapeau", x, 54, 18, Text, true);
            }

            // 4. What to set in the pit menu for it
            x = 12 + 3 * cw + 12;
            if (next != null)
            {
                T(dc, "À PRÉPARER", x, 36, 11, Muted);
                T(dc, "→ " + next.NextDriver, x, 50, 18, Amber, true);
                T(dc, d.Energy ? string.Format(Fr, "énergie {0:0} %  (+{1:0} %)", next.Level, next.EnergyPct) : string.Format(Fr, "essence : +{0:0} L", next.FuelL), x, 80, 13, Text);
                if (d.Energy) T(dc, string.Format(Fr, "≈ {0:0} L d'essence", next.FuelL), x, 98, 12, Faint);
                T(dc, "pneus : " + StrategyEngine.WheelsLabel(next.Wheels), x, 118, 13, Text);
                Car(dc, x + cw - 50, 84, next.Wheels);
            }

            // Stops after the next one, and the last laps
            T(dc, "ARRÊTS SUIVANTS", 12, 160, 11, Muted);
            double y = 178;
            for (int k = 1; k < d.Stops.Count && k <= 3; k++)
            {
                var st = d.Stops[k];
                string level = d.Energy ? st.Level.ToString("0", Fr) + " %" : "+" + st.FuelL.ToString("0", Fr) + " L";
                T(dc, string.Format("{0} · T{1} · {2} · {3} · {4}", d.FirstNumber + k, st.Lap, st.NextDriver, level, StrategyEngine.WheelsLabel(st.Wheels)), 12, y, 13, Text);
                y += 21;
            }
            if (d.Stops.Count <= 1) T(dc, d.Stops.Count == 1 ? "dernier arrêt, puis jusqu'au drapeau" : "—", 12, y, 13, Faint);

            T(dc, "DERNIERS TOURS", 560, 160, 11, Muted);
            for (int k = 0; k < d.Recent.Length; k++)
            {
                double lap = d.Recent[k];
                Brush b = double.IsNaN(d.PlannedPace) ? Text : lap <= d.PlannedPace + 0.2 ? Green : lap <= d.PlannedPace + 1 ? Amber : Red;
                T(dc, Lap(lap), 560 + (k % 4) * 82, 178 + (k / 4) * 22, 13, b);
            }
            if (d.Recent.Length == 0) T(dc, "après le premier tour de course", 560, 178, 13, Faint);
        }

        private static void Car(DrawingContext dc, double x, double y, int wheels)
        {
            dc.DrawRoundedRectangle(B(26, 255, 255, 255), MakePen(B(50, 255, 255, 255), 1), new Rect(x + 12, y, 16, 40), 4, 4);
            for (int c = 0; c < 4; c++)
            {
                bool on = (wheels & (1 << c)) != 0;
                double wx = c % 2 == 0 ? x : x + 32, wy = c < 2 ? y : y + 24;
                dc.DrawRoundedRectangle(on ? Blue : B(200, 0xF2, 0xB1, 0x3C), null, new Rect(wx, wy, 8, 16), 2, 2);
            }
        }
    }
}
