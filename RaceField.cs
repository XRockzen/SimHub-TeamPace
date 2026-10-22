using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace User.TeamPace
{
    // ═══ Stored data: every car of a race, as LMU wrote it ════════════════════

    /// <summary>
    /// Every car of a race, kept with the player's session so the whole team can compare
    /// strategies. LMU writes real lap times, positions and driver swaps for all cars; their
    /// fuel, energy and tyres are placeholders, so stops are found by the time they cost.
    /// </summary>
    public class RaceField
    {
        public int Version = 1;
        public List<FieldCar> Cars = new List<FieldCar>();
    }

    public class FieldCar
    {
        public string Team;
        public string Number;
        public string Vehicle;
        public string CarClass;
        public int Position;
        public int ClassPosition;
        public string Finish;
        public bool IsUs;
        public List<string> Drivers = new List<string>();
        public List<double> Starts = new List<double>();   // session time at the start of each lap
        public double LastLap;                             // time of the last lap (0 = unknown)
        public List<int> PitLaps = new List<int>();        // laps LMU flagged as pit laps
        public List<FieldSwap> Swaps = new List<FieldSwap>();
        public int KnownFrom;                              // live, seen after the start: laps before this one are estimated
    }

    public class FieldSwap
    {
        public double Et;
        public string From;
        public string To;
    }

    // ═══ Built from the results XML ═══════════════════════════════════════════

    internal static class RaceFieldBuilder
    {
        private static readonly Regex ScoreRx  = new Regex(@"^\s*(.+?)\((\d+)\) lap=", RegexOptions.Compiled);
        private static readonly Regex ChangeRx = new Regex("Slot=(\\d+) Vehicle=\"[^\"]*\" Old=\"([^\"]*)\" New=\"([^\"]*)\"", RegexOptions.Compiled);

        /// <param name="streams">the race logs of this file and of the other files of the same race</param>
        public static RaceField Build(XElement block, List<XElement> streams, XElement playerDriver)
        {
            // Slots → names (lap crossings, swaps) and slot → swaps, across every log
            var slotNames = new Dictionary<string, HashSet<string>>();
            var slotSwaps = new Dictionary<string, List<FieldSwap>>();
            var seenSwaps = new HashSet<string>();
            Action<string, string> addName = (slot, name) =>
            {
                name = name.Trim();
                if (name.Length == 0 || name == "???") return;
                HashSet<string> set;
                if (!slotNames.TryGetValue(slot, out set)) slotNames[slot] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(name);
            };
            foreach (var st in streams)
            {
                if (st == null) continue;
                foreach (var ev in st.Elements())
                {
                    var n = ev.Name.LocalName;
                    if (n == "Score")
                    {
                        var m = ScoreRx.Match(ev.Value ?? "");
                        if (m.Success) addName(m.Groups[2].Value, m.Groups[1].Value);
                    }
                    else if (n == "DriverChange")
                    {
                        var m = ChangeRx.Match(ev.Value ?? "");
                        if (!m.Success) continue;
                        double et = Attr(ev, "et");
                        string slot = m.Groups[1].Value;
                        addName(slot, m.Groups[2].Value);
                        addName(slot, m.Groups[3].Value);
                        if (et <= 0 || !seenSwaps.Add(slot + "|" + Math.Round(et))) continue;   // same swap in several files
                        List<FieldSwap> list;
                        if (!slotSwaps.TryGetValue(slot, out list)) slotSwaps[slot] = list = new List<FieldSwap>();
                        list.Add(new FieldSwap { Et = et, From = m.Groups[2].Value.Trim(), To = m.Groups[3].Value.Trim() });
                    }
                }
            }

            var field = new RaceField();
            foreach (var drv in block.Elements("Driver"))
            {
                var car = new FieldCar
                {
                    Team          = Text(drv, "TeamName"),
                    Number        = Text(drv, "CarNumber"),
                    Vehicle       = Text(drv, "VehName"),
                    CarClass      = Text(drv, "CarClass"),
                    Position      = Int(drv, "Position"),
                    ClassPosition = Int(drv, "ClassPosition"),
                    Finish        = Text(drv, "FinishStatus"),
                    IsUs          = drv == playerDriver,
                };
                string name = Text(drv, "Name").Trim();

                // The car's slot: the one its finishing driver drove
                string slot = null;
                foreach (var kv in slotNames) if (kv.Value.Contains(name)) { slot = kv.Key; break; }
                if (slot != null)
                {
                    car.Drivers.AddRange(slotNames[slot]);
                    List<FieldSwap> sw;
                    if (slotSwaps.TryGetValue(slot, out sw)) { car.Swaps.AddRange(sw); car.Swaps.Sort((a, b) => a.Et.CompareTo(b.Et)); }
                }
                if (!car.Drivers.Contains(name)) car.Drivers.Insert(0, name);

                foreach (var lap in drv.Elements("Lap"))
                {
                    double et = Attr(lap, "et");
                    if (et <= 0) continue;
                    car.Starts.Add(Math.Round(et, 2));
                    int num;
                    if ((string)lap.Attribute("pit") == "1" && int.TryParse((string)lap.Attribute("num"), out num)) car.PitLaps.Add(num);
                    double t;
                    car.LastLap = double.TryParse(lap.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out t) && t > 0 ? Math.Round(t, 3) : 0;
                }
                if (car.Starts.Count > 0) field.Cars.Add(car);
            }
            return field.Cars.Count > 1 ? field : null;
        }

        private static string Text(XElement e, string name) { var x = e.Element(name); return x != null ? x.Value : ""; }
        private static int Int(XElement e, string name) { int v; int.TryParse(Text(e, name), out v); return v; }
        private static double Attr(XElement e, string name)
        {
            double v;
            var a = e.Attribute(name);
            return a != null && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : -1;
        }
    }

    // ═══ What each car did: stints, stops, pace ═══════════════════════════════

    public class CarStop
    {
        public int Lap;            // in-lap
        public int Laps;           // laps the stop spread over (in-lap, out-lap…)
        public double At;          // session time at the start of the in-lap
        public double Loss;        // seconds lost against the car's own pace
        public bool PitFlag;
        public FieldSwap Swap;     // driver change during the stop
    }

    public class CarRun
    {
        public FieldCar Car;
        public int Laps;
        public double Pace;              // median lap, seconds
        public double End;               // session time at the finish line
        public double Start;             // session time at the start of lap 1
        public List<CarStop> Stops = new List<CarStop>();
        public List<CarStop> Incidents = new List<CarStop>();   // slow laps that were not stops
        public double PitLoss;
        public double OtherLoss;
        public List<double> LapEnds = new List<double>();        // session time at the end of each lap

        public string Label { get { return string.IsNullOrEmpty(Car.Team) ? Car.Drivers[0] : Car.Team.Trim(); } }
        public int ClassPosition { get { return Car.ClassPosition; } }

        /// <summary>Laps driven between stops (the last one up to the flag).</summary>
        public List<int> Stints
        {
            get
            {
                var list = new List<int>();
                int from = 0;
                foreach (var s in Stops) { list.Add(s.Lap - from); from = s.Lap; }
                list.Add(Laps - from);
                return list;
            }
        }

        public double AverageStint
        {
            get
            {
                // The last stint is cut by the flag: only full stints count, when there are some
                var st = Stints;
                if (st.Count > 1) st.RemoveAt(st.Count - 1);
                double sum = 0;
                foreach (var x in st) sum += x;
                return st.Count > 0 ? sum / st.Count : 0;
            }
        }
    }

    internal static class RaceFieldAnalysis
    {
        public const double SlowMargin = 5;    // a lap this much slower than the pace counts as lost time
        public const double StopLoss   = 40;   // a slow spell costing this much is a stop
        public const double FlaggedStopLoss = 15;   // …or less, when LMU flagged a pit lap or a driver swap

        public static CarRun Run(FieldCar car)
        {
            var run = new CarRun { Car = car, Laps = car.Starts.Count };
            int n = car.Starts.Count;
            var dur = new double[n];
            for (int i = 0; i < n; i++)
                dur[i] = i + 1 < n ? car.Starts[i + 1] - car.Starts[i] : car.LastLap;
            for (int i = 0; i < n; i++) run.LapEnds.Add(dur[i] > 0 ? car.Starts[i] + dur[i] : double.NaN);
            run.Start = n > 0 ? car.Starts[0] : 0;
            run.End = n > 0 ? (dur[n - 1] > 0 ? car.Starts[n - 1] + dur[n - 1] : car.Starts[n - 1]) : 0;

            // Laps estimated before the car was seen (live) say nothing of its pace or its stops
            int first = Math.Max(1, car.KnownFrom);
            var sample = new List<double>();
            for (int i = first; i < n; i++) if (dur[i] > 0) sample.Add(dur[i]);
            if (sample.Count == 0) return run;
            sample.Sort();
            run.Pace = sample[sample.Count / 2];

            var pits = new HashSet<int>(car.PitLaps);
            int k = first;   // lap 1 (standing start) is never compared
            while (k < n)
            {
                if (!(dur[k] > run.Pace + SlowMargin)) { k++; continue; }
                int j = k;
                double loss = 0;
                bool flag = false;
                while (j < n && dur[j] > run.Pace + SlowMargin) { loss += dur[j] - run.Pace; flag |= pits.Contains(j + 1); j++; }
                double from = car.Starts[k] - 5, to = j < n ? car.Starts[j] : double.MaxValue;
                FieldSwap swap = null;
                foreach (var s in car.Swaps) if (s.Et >= from && s.Et <= to) { swap = s; break; }

                var ev = new CarStop { Lap = k + 1, Laps = j - k, At = car.Starts[k], Loss = loss, PitFlag = flag, Swap = swap };
                if (loss >= StopLoss || ((flag || swap != null) && loss >= FlaggedStopLoss)) { run.Stops.Add(ev); run.PitLoss += loss; }
                else { run.Incidents.Add(ev); run.OtherLoss += loss; }
                k = j;
            }
            return run;
        }

        public static List<CarRun> RunAll(RaceField field)
        {
            var list = new List<CarRun>();
            foreach (var c in field.Cars) list.Add(Run(c));
            return list;
        }

        public static double Median(List<double> v)
        {
            if (v.Count == 0) return 0;
            var s = new List<double>(v);
            s.Sort();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }
    }
}
