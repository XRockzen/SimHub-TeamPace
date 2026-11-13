using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace User.TeamPace
{
    /// <summary>
    /// The driver's heart rate, published by the "Montre Da Fit" plugin (User.DafitWatch) as
    /// SimHub properties. Optional: without that plugin, or with the watch away, nothing is read
    /// and TeamPace works as before. Read ten times a second (the watch sends two measures a
    /// second); a measure the watch flags as stale, out of range, or jumping too far from the
    /// last one (an optical sensor shaken by the wheel) is ignored, never counted as 0.
    /// </summary>
    internal static class Heart
    {
        private const string Rate = "DafitWatchPlugin.HeartRate", RateValid = "DafitWatchPlugin.HeartRateValid",
                             RateZone = "DafitWatchPlugin.HeartRateZone", WatchState = "DafitWatchPlugin.State";
        public const int MinBpm = 40, MaxBpm = 220, MaxJump = 40;

        public static volatile bool Present;   // the watch plugin publishes its properties
        public static volatile int Bpm;        // last accepted measure, 0 = none valid now
        public static volatile int Zone;       // 0 to 5, as the watch plugin computes it
        public static volatile string State = "";

        private static int _frame, _lastOk;
        private static DateTime _lastOkAt = DateTime.MinValue;

        /// <summary>Called at every SimHub frame; reads one frame in six.</summary>
        public static void Read(PluginManager pm)
        {
            if (++_frame % 6 != 0) return;
            try
            {
                var hr = pm.GetPropertyValue(Rate);
                if (hr == null) { Ingest(null, null, null, null, DateTime.UtcNow); return; }
                Ingest(hr, pm.GetPropertyValue(RateValid), pm.GetPropertyValue(RateZone), pm.GetPropertyValue(WatchState), DateTime.UtcNow);
            }
            catch { Present = false; Bpm = 0; }
        }

        /// <summary>One reading of the watch plugin's properties (null rate: the plugin is not there).</summary>
        internal static void Ingest(object rate, object valid, object zone, object state, DateTime now)
        {
            if (rate == null) { Present = false; Bpm = 0; State = ""; return; }
            Present = true;
            State = state != null ? state.ToString() : "";
            int bpm = Convert.ToInt32(rate, CultureInfo.InvariantCulture);
            bool ok = bpm >= MinBpm && bpm <= MaxBpm && valid != null && Convert.ToBoolean(valid, CultureInfo.InvariantCulture);
            // A jump the heart cannot make in a few seconds: a shaken sensor
            if (ok && _lastOk > 0 && Math.Abs(bpm - _lastOk) > MaxJump && (now - _lastOkAt).TotalSeconds < 3) ok = false;
            if (ok)
            {
                _lastOk = bpm; _lastOkAt = now;
                Zone = zone != null ? Math.Max(0, Math.Min(5, Convert.ToInt32(zone, CultureInfo.InvariantCulture))) : 0;
            }
            Bpm = ok ? bpm : 0;
        }

        /// <summary>Zone colours, 0 (rest) to 5 (≥ 90 % of the maximum).</summary>
        public static readonly System.Windows.Media.Color[] ZoneColors =
        {
            System.Windows.Media.Color.FromRgb(0x78, 0x90, 0x9C), System.Windows.Media.Color.FromRgb(0x42, 0xA5, 0xF5),
            System.Windows.Media.Color.FromRgb(0x66, 0xBB, 0x6A), System.Windows.Media.Color.FromRgb(0xFF, 0xCA, 0x28),
            System.Windows.Media.Color.FromRgb(0xFF, 0x8A, 0x3D), System.Windows.Media.Color.FromRgb(0xEF, 0x53, 0x50),
        };

        public static readonly string[] ZoneNames = { "repos", "< 60 %", "60-70 %", "70-80 %", "80-90 %", "≥ 90 %" };

        /// <summary>A colour from calm (green) to tense (red), for a value between lo and hi.</summary>
        public static System.Windows.Media.Color Heat(double v, double lo, double hi)
        {
            double f = hi > lo ? Math.Max(0, Math.Min(1, (v - lo) / (hi - lo))) : 0.5;
            // green → yellow → red
            byte r = (byte)(f < 0.5 ? 0x4C + (0xFF - 0x4C) * f * 2 : 0xFF);
            byte g = (byte)(f < 0.5 ? 0xC0 + (0xD5 - 0xC0) * f * 2 : 0xD5 - (0xD5 - 0x45) * (f - 0.5) * 2);
            byte b = (byte)(f < 0.5 ? 0x60 - 0x30 * f * 2 : 0x30);
            return System.Windows.Media.Color.FromRgb(r, g, b);
        }
    }

    /// <summary>
    /// The heart rate over one lap, time-weighted on the valid measures only: average, min, max,
    /// share of the lap measured, time in each zone, each sector, and the rate every 50 m.
    /// </summary>
    internal class HeartLap
    {
        public const double Step = 50;   // metres between two points of the profile

        private double _time, _valid, _sum;
        private int _min, _max;
        private readonly double[] _zone = new double[6], _secSum = new double[3], _secT = new double[3];
        private readonly int[] _secMax = new int[3];
        private readonly List<double> _binSum = new List<double>(), _binT = new List<double>();

        public void Reset()
        {
            _time = _valid = _sum = 0; _min = int.MaxValue; _max = 0;
            Array.Clear(_zone, 0, 6); Array.Clear(_secSum, 0, 3); Array.Clear(_secT, 0, 3); Array.Clear(_secMax, 0, 3);
            _binSum.Clear(); _binT.Clear();
        }

        public HeartLap() { Reset(); }

        /// <summary>dt seconds at this place of the lap (sector 0-2, -1 unknown), with the heart rate known now (0: none).</summary>
        public void Add(double dt, int bpm, int zone, double dist, int sector)
        {
            if (dt <= 0) return;
            _time += dt;
            if (bpm <= 0) return;
            _valid += dt; _sum += bpm * dt;
            if (bpm < _min) _min = bpm;
            if (bpm > _max) _max = bpm;
            _zone[Math.Max(0, Math.Min(5, zone))] += dt;
            if (sector >= 0 && sector < 3)
            {
                _secSum[sector] += bpm * dt; _secT[sector] += dt;
                if (bpm > _secMax[sector]) _secMax[sector] = bpm;
            }
            if (dist >= 0 && dist < 50000)
            {
                int bin = (int)(dist / Step);
                while (_binSum.Count <= bin) { _binSum.Add(0); _binT.Add(0); }
                _binSum[bin] += bpm * dt; _binT[bin] += dt;
            }
        }

        /// <summary>Writes the lap's figures; nothing when no valid measure was seen.</summary>
        public void Fill(LmuLiveLap lap)
        {
            if (_valid < 1 || _time <= 0) return;
            lap.HrAvg = Math.Round(_sum / _valid, 1);
            lap.HrMin = _min; lap.HrMax = _max;
            lap.HrValidPct = Math.Round(100 * Math.Min(1, _valid / _time), 1);
            lap.HrZoneSec = _zone.Select(z => Math.Round(z, 1)).ToArray();
            lap.HrSectorAvg = new double[3]; lap.HrSectorMax = new int[3];
            for (int s = 0; s < 3; s++)
            {
                lap.HrSectorAvg[s] = _secT[s] > 0 ? Math.Round(_secSum[s] / _secT[s], 1) : 0;
                lap.HrSectorMax[s] = _secMax[s];
            }
            var profile = new byte[_binSum.Count];
            for (int i = 0; i < profile.Length; i++)
                profile[i] = _binT[i] > 0 ? (byte)Math.Min(255, Math.Round(_binSum[i] / _binT[i])) : (byte)0;
            lap.HrProfile = profile;
        }
    }

    /// <summary>The heart rate recorded with the laps, found again for the analysis and the telemetry.</summary>
    internal static class HeartData
    {
        private static readonly Dictionary<string, List<LmuLapRecord>> Sessions = new Dictionary<string, List<LmuLapRecord>>();

        /// <summary>The heart rate of a telemetry lap (its result-file lap matched with what was recorded live), or null.</summary>
        public static LmuLiveLap ForLap(TeleLapRef r)
        {
            if (r == null || r.Session == null) return null;
            var key = (r.Driver.DbPath ?? "") + "|" + r.Session.FileId;
            List<LmuLapRecord> laps;
            lock (Sessions)
                if (!Sessions.TryGetValue(key, out laps))
                {
                    laps = LmuSessionStore.GetLapsForSession(r.Session.FileId, r.Driver.DbPath);
                    try { AnalysisColumn.MergeLiveData(r.Session, laps, r.Driver.DbPath); }
                    catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Heart rate lookup: " + ex.Message); }
                    Sessions[key] = laps;
                }
            foreach (var l in laps)
                if (l.LapNum == r.LapNum) return l.Live != null && l.Live.HasHeart ? l.Live : null;
            return null;
        }

        /// <summary>Recorded again (new laps, data pulled): looked up afresh.</summary>
        public static void Forget() { lock (Sessions) Sessions.Clear(); }

        /// <summary>The rate at each point of a trace, from the 50 m profile (NaN where it was not measured).</summary>
        public static float[] AlongTrace(LmuLiveLap live, TeleTrace t)
        {
            var p = live != null ? live.HrProfile : null;
            if (p == null || p.Length == 0) return null;
            var v = new float[t.N];
            bool any = false;
            for (int i = 0; i < t.N; i++)
            {
                double pos = t.DistAt(i) / HeartLap.Step - 0.5;   // bins are centred on their middle
                int a = (int)Math.Floor(pos);
                double f = pos - a;
                float va = a >= 0 && a < p.Length ? p[a] : (byte)0, vb = a + 1 >= 0 && a + 1 < p.Length ? p[a + 1] : (byte)0;
                if (a < 0) va = vb;
                if (a + 1 >= p.Length) vb = va;
                v[i] = va > 0 && vb > 0 ? (float)(va + (vb - va) * f) : va > 0 ? va : vb > 0 ? vb : float.NaN;
                any |= !float.IsNaN(v[i]);
            }
            return any ? v : null;
        }

        /// <summary>The heart rate figures of several laps: time-weighted average, max, peak at the start, time per zone.</summary>
        public static bool Summary(IEnumerable<LmuLiveLap> laps, out double avg, out int max, out int start, out double[] zones)
        {
            double sum = 0, time = 0;
            max = 0; start = 0; zones = new double[6];
            foreach (var l in laps)
            {
                if (l == null || !l.HasHeart) continue;
                double t = l.HrZoneSec != null ? l.HrZoneSec.Sum() : 0;
                if (t <= 0) continue;
                sum += l.HrAvg * t; time += t;
                if (l.HrMax > max) max = l.HrMax;
                if (l.HrStartPeak > start) start = l.HrStartPeak;
                for (int z = 0; z < 6 && z < l.HrZoneSec.Length; z++) zones[z] += l.HrZoneSec[z];
            }
            avg = time > 0 ? sum / time : 0;
            if (start > max) max = start;
            return time > 0;
        }
    }
}
