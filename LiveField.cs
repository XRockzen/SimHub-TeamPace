using GameReaderCommon;
using System;
using System.Collections.Generic;

namespace User.TeamPace
{
    /// <summary>
    /// Every car of the race as it runs, from SimHub's opponents: the time each lap starts,
    /// the laps through the pits, positions and drivers. The same model the race analysis
    /// reads from LMU's results after the race, so the pitwall shows that analysis live.
    /// Read 10 times a second, nothing written to disk.
    /// </summary>
    internal static class LiveField
    {
        private class Car
        {
            public FieldCar Field = new FieldCar();
            public int Lap = -1;
            public bool InLane;
        }

        private static readonly Dictionary<string, Car> Cars = new Dictionary<string, Car>();
        private static readonly object Lock = new object();
        private static double _t0 = double.NaN;
        private static int _frame;

        /// <summary>Changes when a car starts a lap; the most laps any car has started.</summary>
        public static volatile int Version;
        public static int LeaderLaps;
        internal static double TestNow = double.NaN;   // the clock, when a test replays a race

        public static void Reset()
        {
            lock (Lock) { Cars.Clear(); _t0 = double.NaN; LeaderLaps = 0; Version++; }
        }

        public static void Update(List<Opponent> list)
        {
            if (++_frame % 6 != 0) return;
            if (list == null) return;
            double now = double.IsNaN(TestNow) ? OverlayService.Now : TestNow;
            lock (Lock)
            {
                if (double.IsNaN(_t0)) _t0 = now;
                foreach (var o in list)
                {
                    if (o == null) continue;
                    string key = !string.IsNullOrEmpty(o.Id) ? o.Id : o.CarNumber + "|" + o.TeamName;
                    Car c;
                    if (!Cars.TryGetValue(key, out c)) { c = new Car(); Cars[key] = c; }
                    var f = c.Field;
                    f.Team = o.TeamName ?? ""; f.Number = o.CarNumber ?? ""; f.Vehicle = o.CarName ?? ""; f.CarClass = o.CarClass ?? "";
                    f.Position = o.Position; f.ClassPosition = o.PositionInClass; f.IsUs = o.IsPlayer;
                    if (!string.IsNullOrEmpty(o.Name) && !f.Drivers.Contains(o.Name)) f.Drivers.Add(o.Name);   // a driver change adds a name

                    int lap = o.CurrentLap.HasValue ? o.CurrentLap.Value : -1;
                    if (lap > 0 && lap != c.Lap)
                    {
                        // When the lap started: now, less the time already spent in it (we look 10 times a second)
                        double inLap = o.CurrentLapTime.HasValue ? o.CurrentLapTime.Value.TotalSeconds : 0;
                        double start = now - _t0 - Math.Max(0, Math.Min(inLap, 5));
                        if (f.Starts.Count == 0 && lap > 1)
                        {
                            // Seen after the start (a spectator joining): earlier laps at its last lap time, to keep laps aligned
                            double lt = o.LastLapTime.TotalSeconds > 20 ? o.LastLapTime.TotalSeconds : 120;
                            for (int k = 1; k < lap; k++) f.Starts.Add(Math.Round(start - (lap - k) * lt, 2));
                        }
                        if (lap > c.Lap || f.Starts.Count < lap) f.Starts.Add(Math.Round(start, 2));
                        c.Lap = lap;
                        if (f.Starts.Count > LeaderLaps) LeaderLaps = f.Starts.Count;
                        Version++;
                    }
                    if (o.IsCarInPitLane && !c.InLane && lap > 0 && !f.PitLaps.Contains(lap)) f.PitLaps.Add(lap);
                    c.InLane = o.IsCarInPitLane;
                }
            }
        }

        /// <summary>A copy of the race so far, for the analysis (the lap under way has no end yet).</summary>
        public static RaceField Snapshot()
        {
            var field = new RaceField();
            lock (Lock)
                foreach (var c in Cars.Values)
                {
                    var f = c.Field;
                    if (f.Starts.Count == 0) continue;
                    field.Cars.Add(new FieldCar
                    {
                        Team = f.Team, Number = f.Number, Vehicle = f.Vehicle, CarClass = f.CarClass,
                        Position = f.Position, ClassPosition = f.ClassPosition, IsUs = f.IsUs, Finish = "",
                        Drivers = new List<string>(f.Drivers), Starts = new List<double>(f.Starts),
                        PitLaps = new List<int>(f.PitLaps), LastLap = 0,
                    });
                }
            return field.Cars.Count > 1 && field.Cars.Exists(x => x.Starts.Count >= 2) ? field : null;
        }
    }
}
