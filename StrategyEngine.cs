using LiteDB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace User.TeamPace
{
    /// <summary>What the team enters for a race; saved as a named plan.</summary>
    public class StrategyPlan
    {
        public string Id           { get; set; }   // plan name
        public string Circuit      { get; set; }
        public string ClassLabel   { get; set; }
        public int    DurationMin  { get; set; }
        public string StartClock   { get; set; }   // "20:00", optional
        public int    TyreSets     { get; set; }
        public double TankL        { get; set; }
        public int    MaxStintMin  { get; set; }   // 0 = no limit
        public int    MinDriveMin  { get; set; }   // per driver, 0 = no rule
        // Drivers by name: the same on every teammate's computer, so a plan can be shared
        public List<string> Drivers     { get; set; }   // drivers taking part
        public List<string> StintDrivers { get; set; }  // driver of each stint, chosen by hand
        public List<int>    StopTyres   { get; set; }   // tyres changed at each stop, -1 = automatic
        public List<double> StopFuel    { get; set; }   // refuel at each stop: virtual energy % (litres without it), -1 = automatic, Full = fill up
        public List<int>    StopLaps    { get; set; }   // lap of each stop moved by hand, -1 = where the tank says
        public List<double> StopLevel   { get; set; }   // level asked for when leaving each stop: virtual energy % (litres without it), -1 = automatic
        public List<int>    StopWheels  { get; set; }   // wheels changed at each stop (FL 1, FR 2, RL 4, RR 8), -1 = automatic
        public int          TyresAvailable { get; set; } // single tyres for the race, the starting set included
        // Qualifying before the race: its tyres are mounted at stop 0, the race tyres at stop 1
        public int    QualiMin     { get; set; }   // time spent on track in qualifying, 0 = none
        public string QualiDriver  { get; set; }
        public int?   QualiWheels  { get; set; }   // stop 0, null = the 4 tyres
        public int?   GridWheels   { get; set; }   // stop 1, null = automatic
        public List<StrategyOverride> Overrides { get; set; }
        public string   Author     { get; set; }   // last saved by
        public DateTime UpdatedUtc { get; set; }

        public const double Full = 9999;

        public static StrategyPlan Default()
        {
            return new StrategyPlan
            {
                DurationMin = 360, StartClock = "", TyreSets = 6, TankL = 100, MaxStintMin = 0, MinDriveMin = 0,
                Drivers = new List<string>(), StintDrivers = new List<string>(), StopTyres = new List<int>(), StopFuel = new List<double>(), StopLaps = new List<int>(), StopLevel = new List<double>(), TyresAvailable = 24,
                Overrides = new List<StrategyOverride>(),
            };
        }

        /// <summary>Lists missing from a plan saved by an older version.</summary>
        public void Normalize()
        {
            if (Drivers == null) Drivers = new List<string>();
            if (StintDrivers == null) StintDrivers = new List<string>();
            if (StopTyres == null) StopTyres = new List<int>();
            if (StopWheels == null)
            {
                // Older plans counted the tyres: 2 meant the fronts
                StopWheels = new List<int>();
                foreach (var n in StopTyres) StopWheels.Add(n < 0 ? -1 : StrategyEngine.WheelsOf(n));
            }
            if (StopFuel == null) StopFuel = new List<double>();
            if (StopLaps == null) StopLaps = new List<int>();
            if (StopLevel == null)
            {
                // Older plans asked for an amount put in; only "full" keeps its meaning
                StopLevel = new List<double>();
                foreach (var f in StopFuel) StopLevel.Add(f >= Full - 1 ? 100 : -1);
            }
            if (TyresAvailable <= 0) TyresAvailable = TyreSets >= 10 ? TyreSets : Math.Max(1, TyreSets) * 4;   // a count typed in the old "sets" box
            if (Overrides == null) Overrides = new List<StrategyOverride>();
        }
    }

    public class StrategyOverride
    {
        public string Param     { get; set; }
        public string DriverName { get; set; }   // "" for team-wide values
        public double Value     { get; set; }
    }

    /// <summary>A value measured on the team's sessions, possibly replaced by hand.</summary>
    public class StrategyValue
    {
        public double Value;
        public string Source;
        public bool   Measured;     // false: default value
        public bool   Overridden;
    }

    /// <summary>
    /// Endurance planner: measures pace, degradation, consumption, wear and pit-stop times on the
    /// team's sessions for a circuit and category, then simulates the race stint by stint.
    /// </summary>
    internal static class StrategyEngine
    {
        public const string Pace = "pace", Degradation = "deg", Fuel = "fuel", Energy = "ve", Wear = "wear";
        public const string LaneLoss = "lane", FuelRate = "rate", Tyres2 = "tyres2", Tyres4 = "tyres4", TyreLife = "life", StopReal = "stopreal";
        public static readonly string[] DriverParams = { Pace, Degradation, Fuel, Energy, Wear };
        public static readonly string[] TeamParams = { StopReal, LaneLoss, FuelRate, Tyres2, Tyres4, TyreLife };

        public static string Label(string p)
        {
            switch (p)
            {
                case Pace:        return "Rythme de course";
                case Degradation: return "Dégradation des pneus";
                case Fuel:        return "Consommation";
                case Energy:      return "Énergie virtuelle";
                case Wear:        return "Usure pneus (le plus usé)";
                case StopReal:    return "Arrêt complet en course";
                case LaneLoss:    return "Voie des stands";
                case FuelRate:    return "Débit de ravitaillement";
                case Tyres2:      return "Changement de 2 pneus";
                case Tyres4:      return "Changement des 4 pneus";
                default:          return "Durée d'un train de pneus";
            }
        }

        public static string Format(string p, double v)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (p)
            {
                case Pace:        return Fmt.LapSec(v);
                case Degradation: return string.Format(inv, "+{0:0.000} s/tour", v);
                case Fuel:        return string.Format(inv, "{0:0.00} L/tour", v);
                case Energy:      return v > 0 ? string.Format(inv, "{0:0.0} %/tour", v) : "—";
                case Wear:        return string.Format(inv, "{0:0.00} %/tour", v);
                case LaneLoss:
                case StopReal:    return string.Format(inv, "{0:0} s perdues", v);
                case FuelRate:    return string.Format(inv, "{0:0.0} L/s", v);
                case Tyres2:
                case Tyres4:      return string.Format(inv, "+{0:0} s", v);
                default:          return string.Format(inv, "{0:0} tours", v);
            }
        }

        // ─── Measures ─────────────────────────────────────────────────────────

        public class DriverData
        {
            public DriverView Driver;
            public Dictionary<string, StrategyValue> Values = new Dictionary<string, StrategyValue>();
        }

        public class Measures
        {
            public List<DriverData> Drivers = new List<DriverData>();
            public Dictionary<string, StrategyValue> Team = new Dictionary<string, StrategyValue>();
            /// <summary>Wear of each tyre (FL, FR, RL, RR) against the most worn one, measured here.</summary>
            public double[] CornerShare = { 1, 1, 1, 1 };
        }

        private class Stop { public double Stopped, Fuel, Lane, Loss; public int Tyres; public bool Swap; }

        public static Measures Measure(List<DriverView> drivers, string circuit, string classLabel, double tankL)
        {
            var m = new Measures();
            var stops = new List<Stop>();
            var corners = new[] { new List<double>(), new List<double>(), new List<double>(), new List<double>() };
            var raceStops = new HashSet<string>();   // a race's stop counted once, whichever driver's data it comes from
            foreach (var d in drivers)
            {
                var dd = new DriverData { Driver = d };
                var times = new List<double>(); var slopes = new List<double>(); var fuel = new List<double>();
                var ve = new List<double>(); var wear = new List<double>();
                int sessions = 0;

                var list = d.Sessions.FindAll(s => s.TrackDisplayName == circuit && LmuCategoryStyle.GetShortName(s.CarClass) == classLabel);
                list.Sort((a, b) => b.SessionDate.CompareTo(a.SessionDate));
                foreach (var s in list)
                {
                    if (sessions >= 8) break;
                    var laps = LmuSessionStore.GetLapsForSession(s.FileId, d.DbPath);
                    if (laps.Count < 3) continue;
                    sessions++;
                    laps.Sort((a, b) => a.LapNum.CompareTo(b.LapNum));
                    try { AnalysisColumn.MergeLiveData(s, laps, d.DbPath); } catch { }

                    long best = long.MaxValue;
                    foreach (var l in laps) if (l.IsValid && l.LapTimeTicks > 0) best = Math.Min(best, l.LapTimeTicks);
                    var run = new List<double>();
                    for (int i = 0; i < laps.Count; i++)
                    {
                        var l = laps[i];
                        bool clean = l.IsValid && !l.IsPitLap && l.LapNum > 1 && l.LapTimeTicks > 0 && l.LapTimeTicks < best * 1.04
                                     && (i == 0 || !laps[i - 1].IsPitLap);
                        if (clean) { times.Add(l.LapTime.TotalSeconds); run.Add(l.LapTime.TotalSeconds); }
                        if (l.IsPitLap || i == laps.Count - 1)
                        {
                            if (run.Count >= 12) slopes.Add(Slope(run));
                            run.Clear();
                        }
                        if (l.IsPitLap) continue;
                        if (l.HasLive && l.LiveFuelUsedL > 0.3) fuel.Add(l.LiveFuelUsedL);
                        else if (l.FuelUsed > 0.002) fuel.Add(l.FuelUsed * tankL);
                        if (l.VirtualEnergyUsed > 0.002) ve.Add(l.VirtualEnergyUsed * 100);
                        double w = Math.Max(Math.Max(l.LiveWearFL, l.LiveWearFR), Math.Max(l.LiveWearRL, l.LiveWearRR));
                        if (l.HasLive && w > 0.05)
                        {
                            wear.Add(w);
                            corners[0].Add(l.LiveWearFL); corners[1].Add(l.LiveWearFR); corners[2].Add(l.LiveWearRL); corners[3].Add(l.LiveWearRR);
                        }
                        else if (i > 0 && !laps[i - 1].IsPitLap)
                        {
                            var p = laps[i - 1];
                            double fl = (p.TireWearFL - l.TireWearFL) * 100, fr = (p.TireWearFR - l.TireWearFR) * 100;
                            double rl = (p.TireWearRL - l.TireWearRL) * 100, rr = (p.TireWearRR - l.TireWearRR) * 100;
                            double xw = Math.Max(Math.Max(fl, fr), Math.Max(rl, rr));
                            if (xw > 0.05 && xw < 5)
                            {
                                wear.Add(xw);
                                corners[0].Add(fl); corners[1].Add(fr); corners[2].Add(rl); corners[3].Add(rr);
                            }
                        }
                    }

                    // Pit stops. In a race, the time each stop cost is the car's (found from LMU's
                    // results, as in the race analysis): it holds the lane, the service and, at a
                    // driver change, the swap. What SimHub captured at the stop gives the service.
                    var field = s.IsRace ? (LmuSessionStore.GetField(s.FileId, d.DbPath) ?? TeamRaceStore.FieldFor(s)) : null;
                    var car = field != null ? field.Cars.Find(c => c.IsUs) : null;
                    if (car != null)
                    {
                        foreach (var st in RaceFieldAnalysis.Run(car).Stops)
                        {
                            if (!raceStops.Add(s.BlockStartUtc.ToString("yyyyMMddHHmm") + "|" + st.Lap)) continue;
                            var lap = laps.Find(l => l.LapNum == st.Lap);
                            var p = lap != null ? lap.LivePit : null;
                            stops.Add(new Stop
                            {
                                Loss = st.Loss, Swap = st.Swap != null, Stopped = p != null ? p.StoppedSec : double.NaN,
                                Fuel = p != null ? p.FuelAddedL : 0, Tyres = p != null ? p.TyresCount : -1, Lane = p != null ? p.LaneSec : double.NaN,
                            });
                        }
                    }
                    else
                    {
                        // Practice: stops captured live, the out-lap only when it really follows
                        double pace = Median(times);
                        for (int i = 1; i + 1 < laps.Count; i++)
                        {
                            var p = laps[i].LivePit;
                            if (p == null || p.FromGarage) continue;
                            double inLap = Dur(laps[i]), outLap = Dur(laps[i + 1]);
                            var stop = new Stop { Stopped = p.StoppedSec, Fuel = p.FuelAddedL, Tyres = p.TyresCount, Lane = p.LaneSec, Loss = double.NaN };
                            if (!double.IsNaN(pace) && inLap > 0 && laps[i + 1].LapNum == laps[i].LapNum + 1 && outLap > pace * 0.85 && !laps[i + 1].IsPitLap)
                                stop.Loss = inLap + outLap - 2 * pace;
                            stops.Add(stop);
                        }
                    }
                }

                string basis = string.Format("{0} séance(s)", sessions);
                dd.Values[Pace] = Value(Median(times), double.NaN, string.Format("médiane de {0} tours propres · {1}", times.Count, basis));
                dd.Values[Degradation] = Value(slopes.Count > 0 ? Math.Max(0, Math.Min(0.3, Median(slopes))) : double.NaN, 0.03,
                    slopes.Count > 0 ? string.Format("pente du rythme sur {0} relais de 12 tours ou plus", slopes.Count) : "pas de relais assez long : valeur par défaut");
                dd.Values[Fuel] = Value(Median(fuel), double.NaN, string.Format("{0} tours mesurés", fuel.Count));
                // LMU writes each lap's energy in steps (3.1, 3.5 %…): the median lands on a step,
                // a trimmed mean gives the true average
                dd.Values[Energy] = Value(TrimmedMean(ve), 0, ve.Count > 0 ? string.Format("résultats LMU · {0} tours", ve.Count) : "pas d'énergie virtuelle sur cette voiture");
                dd.Values[Wear] = Value(Median(wear), double.NaN, string.Format("{0} tours mesurés", wear.Count));
                m.Drivers.Add(dd);
            }

            // Team-wide pit values. LMU refuels while the tyres are changed: the service lasts as
            // long as the longer of the two. A refuel without tyres gives the rate; a stop with
            // tyres gives an upper bound of the rate and the tyre change time.
            var rates = new List<double>(); var tyres4 = new List<double>(); var tyres2 = new List<double>(); var lanes = new List<double>();
            // At a driver change the leaving driver's SimHub stops seeing the car: what it captured
            // there (fuel, time stopped) is cut short. Only stops without a swap measure the service.
            var real = new List<double>();
            foreach (var s in stops) if (!s.Swap && s.Fuel >= 20 && s.Tyres == 0 && s.Stopped > 3) rates.Add(s.Fuel / s.Stopped);
            if (rates.Count == 0) foreach (var s in stops) if (!s.Swap && s.Fuel >= 20 && s.Tyres > 0 && s.Stopped > 3) rates.Add(s.Fuel / s.Stopped);
            double rate = rates.Count > 0 ? Median(rates) : double.NaN;
            foreach (var s in stops)
            {
                if (!s.Swap && (s.Tyres == 4 || s.Tyres == 2) && s.Stopped > 3) (s.Tyres == 4 ? tyres4 : tyres2).Add(s.Stopped);
                if (!s.Swap && !double.IsNaN(s.Loss) && !double.IsNaN(s.Stopped)) lanes.Add(s.Loss - s.Stopped);
                // The whole cost of a stop in a race, from LMU's results: lane, service, swap
                if (!double.IsNaN(s.Loss) && s.Loss > 20 && s.Tyres != -2) real.Add(s.Loss);
            }
            m.Team[FuelRate] = Value(rate, 2.1, rates.Count > 0 ? string.Format("{0} ravitaillement(s) mesuré(s)", rates.Count) : "valeur par défaut");
            m.Team[Tyres2] = Value(Median(tyres2), 5, tyres2.Count > 0 ? string.Format("{0} changement(s) mesuré(s)", tyres2.Count) : "valeur par défaut");
            m.Team[Tyres4] = Value(Median(tyres4), 12, tyres4.Count > 0 ? string.Format("{0} changement(s) mesuré(s)", tyres4.Count) : "valeur par défaut");
            m.Team[LaneLoss] = Value(Median(lanes), 25, lanes.Count > 0 ? string.Format("{0} arrêt(s) sans relais mesuré(s)", lanes.Count) : "valeur par défaut");
            m.Team[StopReal] = Value(real.Count >= 2 ? Median(real) : double.NaN, double.NaN,
                real.Count >= 2 ? string.Format("médiane de {0} arrêts de vos courses ici (voie, service, relais) : le plan s'y cale", real.Count) : "pas encore de course ici");
            if (corners[0].Count >= 5)
            {
                var med = new double[4];
                for (int c = 0; c < 4; c++) med[c] = Math.Max(0, Median(corners[c]));
                double top = Math.Max(Math.Max(med[0], med[1]), Math.Max(med[2], med[3]));
                if (top > 0) for (int c = 0; c < 4; c++) m.CornerShare[c] = Math.Max(0.2, med[c] / top);
            }
            double maxWear = 0;
            foreach (var dd in m.Drivers) if (dd.Values[Wear].Measured) maxWear = Math.Max(maxWear, dd.Values[Wear].Value);
            m.Team[TyreLife] = Value(maxWear > 0 ? Math.Floor(60 / maxWear) : double.NaN, 40,
                maxWear > 0 ? "jusqu'à 60 % d'usure au rythme d'usure le plus élevé" : "valeur par défaut");
            return m;
        }

        private static StrategyValue Value(double measured, double fallback, string source)
        {
            bool ok = !double.IsNaN(measured) && measured > 0;
            return new StrategyValue { Value = ok ? measured : fallback, Measured = ok, Source = ok ? source : (double.IsNaN(fallback) ? "pas de mesure" : source) };
        }

        /// <summary>Applies hand-entered values on top of the measures.</summary>
        public static void ApplyOverrides(Measures m, StrategyPlan plan)
        {
            foreach (var o in plan.Overrides)
            {
                StrategyValue v = null;
                if (string.IsNullOrEmpty(o.DriverName)) m.Team.TryGetValue(o.Param, out v);
                else
                {
                    var dd = m.Drivers.Find(x => x.Driver.Name == o.DriverName);
                    if (dd != null) dd.Values.TryGetValue(o.Param, out v);
                }
                if (v != null) { v.Value = o.Value; v.Overridden = true; }
            }
        }

        private static double Dur(LmuLapRecord l)
        {
            if (l.LapTimeTicks > 0) return l.LapTime.TotalSeconds;
            return l.ReconstructedTicks > 0 ? TimeSpan.FromTicks(l.ReconstructedTicks).TotalSeconds : 0;
        }

        private static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            var c = new List<double>(v); c.Sort();
            return c[c.Count / 2];
        }

/// <summary>Mean without the lowest and highest tenth (a refuelled or a lost lap).</summary>
        private static double TrimmedMean(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            var c = new List<double>(v); c.Sort();
            int cut = c.Count / 10;
            double sum = 0; int n = 0;
            for (int i = cut; i < c.Count - cut; i++) { sum += c[i]; n++; }
            return n > 0 ? sum / n : double.NaN;
        }

        private static double Slope(List<double> y)
        {
            int n = y.Count;
            double mx = (n - 1) / 2.0, my = 0;
            foreach (var v in y) my += v;
            my /= n;
            double num = 0, den = 0;
            for (int i = 0; i < n; i++) { num += (i - mx) * (y[i] - my); den += (i - mx) * (i - mx); }
            return den > 0 ? num / den : 0;
        }

        // ─── Simulation ───────────────────────────────────────────────────────

        public class StintPlan
        {
            public string Driver;   // name
            public int Laps;
            public double Start, End;   // race time, s
        }

        public class StopPlan
        {
            public int Lap;
            public bool LapChosen;     // moved by hand
            public double At;           // race time when the car enters the pits
            public double FuelL;
            public double EnergyPct;   // virtual energy put back, %
            public int Tyres;          // tyres changed
            public int Wheels;         // which ones (FL 1, FR 2, RL 4, RR 8)
            public double[] WearIn;    // wear of each tyre when the car comes in, %
            public bool TyresChosen;   // set by hand
            public bool FuelChosen;    // level asked for by hand
            public double Level;       // level when leaving the pits: % of virtual energy, or litres
            public bool Full;          // filled up
            public double Stopped, Lost;
            public string NextDriver;
        }

        public class Simulation
        {
            public List<StintPlan> Stints = new List<StintPlan>();
            public List<StopPlan> Stops = new List<StopPlan>();
            public PreRace Quali;         // null without a qualifying
            public double[] WearPerLap;   // each tyre, % per lap
            public bool HasQuali;         // the plan has a qualifying (stops 0 and 1)
            public int StopsDone;         // race stops already made (live strategy)
            /// <summary>Number shown for the first stop of the list: 2 after a qualifying (0 and 1 come before the race), more once stops are made.</summary>
            public int FirstStop { get { return (HasQuali ? 2 : 1) + StopsDone; } }
            public int Laps;
            public double Finish;         // race time when the last lap is completed
            public double PitLoss;
            public int SetsUsed;
            public int TyresUsed;      // single tyres, the starting set included
            public bool EnergyMode;    // every driver uses virtual energy: refuels are counted in %
            public Dictionary<string, double> Driving = new Dictionary<string, double>();
            public List<string> Warnings = new List<string>();
        }

        /// <summary>Qualifying: tyres mounted at stop 0, run on it, then the race tyres at stop 1.</summary>
        public class PreRace
        {
            public string Driver;
            public int Laps;
            public StopPlan Stop0 = new StopPlan(), Stop1 = new StopPlan();
        }

        /// <summary>Where a race stands, to plan the rest of it while it runs.</summary>
        public class RaceState
        {
            public double Time;                    // race time, s
            public int    Lap;                     // laps completed
            public int    StopsDone;               // race stops made
            public double Fuel;                    // litres in the tank
            public double Energy = double.NaN;     // virtual energy %, NaN = from the fuel and the measured ratio
            public double StintTime;               // time driven in the current stint, s
            public int    LapsOnTyres;             // laps since the tyres were changed
            public int    TyresUsed;
        }

        // ─── Wheels ───────────────────────────────────────────────────────────

        public const int FL = 1, FR = 2, RL = 4, RR = 8, AllWheels = 15;
        /// <summary>Wear at which a tyre is due (the set's life is counted up to there).</summary>
        public const double WearLimit = 60;

        public static int Count(int wheels)
        {
            int n = 0;
            for (int b = 0; b < 4; b++) if ((wheels & (1 << b)) != 0) n++;
            return n;
        }

        /// <summary>Wheels for a number of tyres (plans saved before the choice per wheel).</summary>
        public static int WheelsOf(int count)
        {
            return count <= 0 ? 0 : count == 1 ? FL : count == 2 ? FL | FR : count == 3 ? FL | FR | RL : AllWheels;
        }

        public static string WheelsLabel(int wheels)
        {
            switch (wheels & AllWheels)
            {
                case 0:              return "aucun pneu";
                case AllWheels:      return "4 pneus";
                case FL | FR:        return "les 2 avant";
                case RL | RR:        return "les 2 arrière";
                case FL | RL:        return "les 2 gauches";
                case FR | RR:        return "les 2 droits";
                default:             return Count(wheels) + (Count(wheels) > 1 ? " pneus" : " pneu");
            }
        }

        /// <summary>Automatic choice: an axle is changed when one of its tyres would pass the limit in the next stint.</summary>
        private static int AutoWheels(double[] wear, double[] perLap, int laps)
        {
            Func<int, bool> due = c => wear[c] + laps * perLap[c] > WearLimit;
            return (due(0) || due(1) ? FL | FR : 0) | (due(2) || due(3) ? RL | RR : 0);
        }

        /// <summary>Fewer tyres left than asked: the axle that needs it most.</summary>
        private static int Afford(int wheels, double[] wear, int left)
        {
            if (Count(wheels) <= left) return wheels;
            if (left < 2) return 0;
            return Math.Max(wear[0], wear[1]) >= Math.Max(wear[2], wear[3]) ? FL | FR : RL | RR;
        }

        /// <summary>Changes the given wheels: the tyres used, the wear and age reset.</summary>
        private static void Mount(Simulation sim, int wheels, double[] wear, double[] age)
        {
            for (int c = 0; c < 4; c++)
                if ((wheels & (1 << c)) != 0) { wear[c] = 0; age[c] = 0; }
            sim.TyresUsed += Count(wheels);
        }

        /// <summary>
        /// Timed race, stint by stint: a stint lasts as long as the tank (fuel or virtual energy)
        /// or the maximum stint time allows; tyres are changed when the next stint would take the
        /// set past its life (or at every stop when <paramref name="tyresEveryStop"/>).
        /// </summary>
        public static Simulation Simulate(StrategyPlan plan, Measures m, bool tyresEveryStop)
        {
            // Energy first: every stint as long as the tank allows, a full refill at each stop
            // and only what the last stint needs at the last one; tyres last as long as they can.
            plan.Normalize();
            return Run(plan, m, tyresEveryStop, 0, null);
        }

        /// <summary>The rest of a race from where it stands: same rules, stops and settings of the plan still to come.</summary>
        public static Simulation Resume(StrategyPlan plan, Measures m, RaceState from)
        {
            plan.Normalize();
            return Run(plan, m, false, 0, from);
        }

        private static Simulation Run(StrategyPlan plan, Measures m, bool tyresEveryStop, int capLaps, RaceState from)
        {
            var sim = new Simulation();
            var keys = plan.Drivers.FindAll(k => m.Drivers.Exists(d => d.Driver.Name == k));
            if (keys.Count == 0) { sim.Warnings.Add("Choisissez au moins un pilote."); return sim; }
            foreach (var k in keys)
            {
                var dd = m.Drivers.Find(d => d.Driver.Name == k);
                if (!dd.Values[Pace].Measured && !dd.Values[Pace].Overridden) sim.Warnings.Add(dd.Driver.Name + " : pas de rythme mesuré ici, saisissez-le.");
                if (!(dd.Values[Fuel].Value > 0)) sim.Warnings.Add(dd.Driver.Name + " : pas de consommation mesurée ici, saisissez-la.");
            }
            if (sim.Warnings.Count > 0) return sim;

            // LMU counts the refuel in virtual energy on cars that have it; the fuel follows
            // (litres per % measured for each driver)
            sim.EnergyMode = keys.TrueForAll(k => m.Drivers.Find(d => d.Driver.Name == k).Values[Energy].Value > 0);
            double duration = plan.DurationMin * 60.0, tank = Math.Max(1, plan.TankL);
            double rate = m.Team[FuelRate].Value, lane = m.Team[LaneLoss].Value;
            double t2 = m.Team[Tyres2].Value, t4 = m.Team[Tyres4].Value;
            // 0 → 0, 2 → t2, 4 → t4, in between on a straight line
            Func<int, double> tyreTime = n => n <= 0 ? 0 : n <= 2 ? t2 * n / 2 : t2 + (t4 - t2) * (n - 2) / 2;
            // Measured in real races: a full stop (full refill and 4 tyres) costs that; the lane is
            // what remains once its service is taken off, so lighter stops cost less
            var stopReal = m.Team[StopReal];
            if (stopReal.Measured || stopReal.Overridden)
            {
                var first = m.Drivers.Find(d => d.Driver.Name == keys[0]).Values;
                double fullLitres = sim.EnergyMode && first[Energy].Value > 0 ? 100 * first[Fuel].Value / first[Energy].Value : tank;
                double fullService = Math.Max(Math.Min(fullLitres, tank) / rate, tyreTime(4));
                lane = Math.Max(10, stopReal.Value - fullService);
            }
            int life = (int)Math.Max(1, m.Team[TyreLife].Value);
            double t = 0, fuelInTank = tank, energy = 100;
            // Each tyre: the most worn one reaches the limit after a set's life, the others in proportion
            sim.WearPerLap = new double[4];
            for (int c = 0; c < 4; c++) sim.WearPerLap[c] = WearLimit / life * m.CornerShare[c];
            var wear = new double[4]; var age = new double[4];
            int lap = 0, stint = 0;
            sim.TyresUsed = 4;
            sim.HasQuali = plan.QualiMin > 0;

            if (plan.QualiMin > 0 && from == null)
            {
                // Stop 0: the qualifying tyres. They run the qualifying, then stop 1 puts the race tyres
                var q = new PreRace { Driver = keys.Contains(plan.QualiDriver ?? "") ? plan.QualiDriver : keys[0] };
                var qp = m.Drivers.Find(d => d.Driver.Name == q.Driver).Values;
                q.Laps = Math.Max(1, (int)Math.Floor(plan.QualiMin * 60.0 / qp[Pace].Value));
                sim.TyresUsed = 0;
                int w0 = plan.QualiWheels.HasValue && plan.QualiWheels.Value >= 0 ? plan.QualiWheels.Value & AllWheels : AllWheels;
                q.Stop0 = new StopPlan { Wheels = w0, Tyres = Count(w0), TyresChosen = plan.QualiWheels.HasValue && plan.QualiWheels.Value >= 0, WearIn = new double[4], NextDriver = q.Driver };
                Mount(sim, w0, wear, age);
                for (int c = 0; c < 4; c++) { wear[c] += q.Laps * sim.WearPerLap[c]; age[c] += q.Laps; }

                var k0 = DriverOf(plan, keys, 0);
                var p0 = m.Drivers.Find(d => d.Driver.Name == k0).Values;
                int forced0 = ForcedStop(plan, 0, 0);
                int firstStint = forced0 > 0 ? forced0 : Limit(tank, 100, p0[Fuel].Value, p0[Energy].Value, p0[Pace].Value, plan.MaxStintMin);
                bool chosen1 = plan.GridWheels.HasValue && plan.GridWheels.Value >= 0;
                int w1 = chosen1 ? plan.GridWheels.Value & AllWheels : AutoWheels(wear, sim.WearPerLap, firstStint);
                if (sim.TyresUsed + Count(w1) > plan.TyresAvailable)
                {
                    sim.Warnings.Add(string.Format("Arrêt 1 : {0} demandé(s) mais plus assez de pneus neufs ({1} pour la qualif et la course).", WheelsLabel(w1), plan.TyresAvailable));
                    if (!chosen1) w1 = Afford(w1, wear, plan.TyresAvailable - sim.TyresUsed);
                }
                q.Stop1 = new StopPlan { Wheels = w1, Tyres = Count(w1), TyresChosen = chosen1, WearIn = (double[])wear.Clone(), NextDriver = k0, Full = true, Level = sim.EnergyMode ? 100 : tank };
                Mount(sim, w1, wear, age);
                sim.Quali = q;
            }

            double stintDone = 0;
            if (from != null)
            {
                // Live: the race so far is done; the tank, the tyres and the stint as they are
                t = from.Time; lap = from.Lap; stint = from.StopsDone; sim.StopsDone = from.StopsDone;
                fuelInTank = Math.Max(0, Math.Min(tank, from.Fuel));
                var cur = m.Drivers.Find(d => d.Driver.Name == DriverOf(plan, keys, stint)).Values;
                energy = !double.IsNaN(from.Energy) ? from.Energy
                       : sim.EnergyMode && cur[Fuel].Value > 0 ? Math.Min(100, fuelInTank * cur[Energy].Value / cur[Fuel].Value) : 100;
                for (int c = 0; c < 4; c++) { age[c] = from.LapsOnTyres; wear[c] = from.LapsOnTyres * sim.WearPerLap[c]; }
                sim.TyresUsed = Math.Max(4, from.TyresUsed);
                stintDone = from.StintTime;
            }

            while (t < duration && stint < 200)
            {
                var key = DriverOf(plan, keys, stint);
                var p = m.Drivers.Find(d => d.Driver.Name == key).Values;
                double pace = p[Pace].Value, fuel = p[Fuel].Value, ve = p[Energy].Value, deg = p[Degradation].Value;
                int n = Limit(fuelInTank, energy, fuel, ve, pace, plan.MaxStintMin);
                // The stint under way has already used part of the maximum stint time
                if (stintDone > 0 && plan.MaxStintMin > 0) n = Math.Max(1, Math.Min(n, (int)Math.Floor((plan.MaxStintMin * 60 - stintDone) / pace)));
                stintDone = 0;
                int forced = ForcedStop(plan, stint, lap);
                if (forced > 0 && from != null && forced - lap > n) forced = 0;   // live: a stop the tank cannot reach comes earlier
                if (forced > 0)
                {
                    // Stop moved by hand: the stint lasts until then, even past the tank
                    if (forced - lap > n)
                        sim.Warnings.Add(string.Format("Relais {0} : {1} tours, plus que l'autonomie ({2} tours).", stint + 1, forced - lap, n));
                    n = forced - lap;
                }
                else if (capLaps > 0) n = Math.Min(n, capLaps);
                var s = new StintPlan { Driver = key, Start = t };
                for (int k = 0; k < n && t < duration; k++)
                {
                    t += pace + deg * (age[0] + age[1] + age[2] + age[3]) / 4;
                    lap++; s.Laps++;
                    for (int c = 0; c < 4; c++) { age[c]++; wear[c] += sim.WearPerLap[c]; }
                    fuelInTank -= fuel; energy -= ve;
                }
                s.End = t;
                sim.Stints.Add(s);
                double dr; sim.Driving.TryGetValue(key, out dr); sim.Driving[key] = dr + (s.End - s.Start);
                if (t >= duration) break;

                // Stop: fuel for the next stint (or to the flag), tyres when the set is worn out
                var nextKey = DriverOf(plan, keys, stint + 1);
                var np = m.Drivers.Find(d => d.Driver.Name == nextKey).Values;
                double nPace = np[Pace].Value, nFuel = np[Fuel].Value, nVe = np[Energy].Value;
                int toFlag = (int)Math.Ceiling((duration - t - lane) / nPace) + 1;
                int next = Math.Min(Limit(tank, 100, nFuel, nVe, nPace, plan.MaxStintMin), Math.Max(1, toFlag));
                if (capLaps > 0) next = Math.Min(next, capLaps + 1);   // fuel for the balanced stint, one lap spare
                int nextForced = ForcedStop(plan, stint + 1, lap);
                if (nextForced > 0) next = Math.Min(nextForced - lap, Math.Max(1, toFlag));
                int stopIndex = sim.Stops.Count + sim.StopsDone;   // the plan's settings for this stop
                double ratio = sim.EnergyMode ? nFuel / nVe : 0;   // litres per % of virtual energy
                int fullStint = Limit(tank, 100, nFuel, nVe, nPace, plan.MaxStintMin);
                double asked = stopIndex < plan.StopLevel.Count ? plan.StopLevel[stopIndex] : -1;
                bool fuelChosen = asked >= 0;
                double targetVe, targetFuel;
                if (fuelChosen) { targetVe = Math.Min(100, asked); targetFuel = Math.Min(tank, asked); }
                else if (nextForced > 0 || toFlag <= fullStint)
                {
                    // The last stint (or one cut short by hand): what it needs, with a lap spare
                    int need = nextForced > 0 ? Math.Min(nextForced - lap, Math.Max(1, toFlag)) : Math.Max(1, toFlag);
                    targetVe = Math.Min(100, need * nVe + 0.5 * nVe);
                    targetFuel = Math.Min(tank, need * nFuel + 0.5 * nFuel);
                }
                else { targetVe = 100; targetFuel = tank; }
                double veAdd = sim.EnergyMode ? Math.Max(0, targetVe - energy) : 0;
                double add = sim.EnergyMode ? Math.Max(0, Math.Min(tank - fuelInTank, veAdd * ratio)) : Math.Max(0, targetFuel - fuelInTank);
                next = Math.Min(Limit(fuelInTank + add, energy + veAdd, nFuel, nVe, nPace, plan.MaxStintMin), Math.Max(1, toFlag));
                if (nextForced > 0) next = Math.Min(nextForced - lap, Math.Max(1, toFlag));
                bool chosen = !tyresEveryStop && stopIndex < plan.StopWheels.Count && plan.StopWheels[stopIndex] >= 0;
                int wheels = chosen ? plan.StopWheels[stopIndex] & AllWheels : tyresEveryStop ? AllWheels : AutoWheels(wear, sim.WearPerLap, next);
                if (wheels != 0 && sim.TyresUsed + Count(wheels) > plan.TyresAvailable)
                {
                    sim.Warnings.Add(string.Format("Arrêt {0} : {1} demandé(s) mais plus assez de pneus neufs ({2} {3}).", sim.Stops.Count + sim.FirstStop, WheelsLabel(wheels),
                        plan.TyresAvailable, sim.Quali != null ? "pour la qualif et la course" : "pour la course"));
                    if (!chosen) wheels = Afford(wheels, wear, plan.TyresAvailable - sim.TyresUsed);
                }
                int tyres = Count(wheels);
                var wearIn = (double[])wear.Clone();
                Mount(sim, wheels, wear, age);
                double refuel = add / rate;   // the fuel that goes in sets the time
                double stopped = Math.Max(refuel, tyreTime(tyres));   // refuel and tyres at the same time
                double lost = lane + stopped;
                sim.Stops.Add(new StopPlan
                {
                    Lap = lap, LapChosen = forced > 0, At = t, FuelL = add, EnergyPct = veAdd, FuelChosen = fuelChosen,
                    Level = sim.EnergyMode ? energy + veAdd : fuelInTank + add,
                    Full = sim.EnergyMode ? energy + veAdd >= 99.5 : fuelInTank + add >= tank - 0.5,
                    Tyres = tyres, Wheels = wheels, WearIn = wearIn, TyresChosen = chosen, Stopped = stopped, Lost = lost, NextDriver = nextKey,
                });
                sim.PitLoss += lost;
                t += lost;
                fuelInTank += add; energy += veAdd;
                stint++;
            }
            sim.Laps = lap;
            sim.Finish = t;
            sim.SetsUsed = (int)Math.Ceiling(sim.TyresUsed / 4.0);

            if (plan.MinDriveMin > 0)
                foreach (var k in keys)
                {
                    double dr; sim.Driving.TryGetValue(k, out dr);
                    if (dr < plan.MinDriveMin * 60)
                        sim.Warnings.Add(string.Format("{0} ne roule que {1} : moins que le minimum de {2}.",
                            m.Drivers.Find(d => d.Driver.Name == k).Driver.Name, Fmt.TotalTime(TimeSpan.FromSeconds(dr)), Fmt.TotalTime(TimeSpan.FromMinutes(plan.MinDriveMin))));
                }
            return sim;
        }

        /// <summary>The lap a stop was moved to, when it lies after the given lap; else 0.</summary>
        private static int ForcedStop(StrategyPlan plan, int stint, int lap)
        {
            if (plan.StopLaps == null || stint >= plan.StopLaps.Count) return 0;
            return plan.StopLaps[stint] > lap ? plan.StopLaps[stint] : 0;
        }

        private static int Limit(double fuelInTank, double energy, double fuel, double ve, double pace, int maxStintMin)
        {
            int n = (int)Math.Floor((fuelInTank - 0.3 * fuel) / fuel);
            if (ve > 0) n = Math.Min(n, (int)Math.Floor((energy - 0.3 * ve) / ve));
            if (maxStintMin > 0) n = Math.Min(n, (int)Math.Floor(maxStintMin * 60 / pace));
            return Math.Max(1, n);
        }

        /// <summary>Driver of a stint: the one chosen by hand, otherwise the drivers in turn.</summary>
        public static string DriverOf(StrategyPlan plan, List<string> keys, int stint)
        {
            if (plan.StintDrivers != null && stint < plan.StintDrivers.Count && keys.Contains(plan.StintDrivers[stint]))
                return plan.StintDrivers[stint];
            return keys[stint % keys.Count];
        }

        // ─── Saved plans ──────────────────────────────────────────────────────
        //
        // Plans live in the team folder ("strategy\<name>.json"), so every teammate sees and edits
        // the same plans; a local copy keeps them when the team folder is unreachable.

        private static string _path;
        private static readonly object Lock = new object();

        public static void Init(string dataDir) { _path = Path.Combine(dataDir, "local.db"); }

        private static string SharedDir
        {
            get { return LmuTeamSync.IsConfigured ? Path.Combine(LmuSessionStore.TeamFolder, "strategy") : null; }
        }

        private static string FileName(string id)
        {
            var s = id.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s + ".json";
        }

        /// <summary>The team's plans (shared folder) and the local ones, newest version of each.</summary>
        public static List<StrategyPlan> SavedPlans()
        {
            var byId = new Dictionary<string, StrategyPlan>(StringComparer.OrdinalIgnoreCase);
            Action<StrategyPlan> keep = p =>
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Id)) return;
                p.Normalize();
                StrategyPlan cur;
                if (!byId.TryGetValue(p.Id, out cur) || p.UpdatedUtc > cur.UpdatedUtc) byId[p.Id] = p;
            };
            if (_path != null)
                lock (Lock)
                {
                    try { using (var db = new LiteDatabase(string.Format("Filename={0}", _path))) foreach (var p in db.GetCollection<StrategyPlan>("strategy_plans").FindAll()) keep(p); }
                    catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Plans: " + ex.Message); }
                }
            var dir = SharedDir;
            if (dir != null && Directory.Exists(dir))
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try { keep(Newtonsoft.Json.JsonConvert.DeserializeObject<StrategyPlan>(File.ReadAllText(file))); }
                    catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Plan " + Path.GetFileName(file) + ": " + ex.Message); }
                }
            var list = new List<StrategyPlan>(byId.Values);
            list.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
            return list;
        }

        /// <summary>Saves locally and to the team folder. Returns false when only the local copy was written.</summary>
        public static bool Save(StrategyPlan plan)
        {
            if (string.IsNullOrWhiteSpace(plan.Id)) return false;
            plan.Author = LmuSessionStore.PlayerName;
            plan.UpdatedUtc = DateTime.UtcNow;
            if (_path != null)
                lock (Lock)
                {
                    try { using (var db = new LiteDatabase(string.Format("Filename={0}", _path))) db.GetCollection<StrategyPlan>("strategy_plans").Upsert(plan); }
                    catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Plan save: " + ex.Message); }
                }
            var dir = SharedDir;
            if (dir == null) return false;
            try
            {
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, FileName(plan.Id));
                var tmp = target + ".tmp";
                File.WriteAllText(tmp, Newtonsoft.Json.JsonConvert.SerializeObject(plan, Newtonsoft.Json.Formatting.Indented));
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TeamPace] Plan share: " + ex.Message);
                return false;
            }
        }

        /// <summary>Deletes the plan for the whole team.</summary>
        public static void Delete(string id)
        {
            if (_path != null)
                lock (Lock)
                {
                    try { using (var db = new LiteDatabase(string.Format("Filename={0}", _path))) db.GetCollection<StrategyPlan>("strategy_plans").Delete(id); }
                    catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Plan delete: " + ex.Message); }
                }
            var dir = SharedDir;
            if (dir == null) return;
            try
            {
                var file = Path.Combine(dir, FileName(id));
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Plan delete: " + ex.Message); }
        }
    }
}
