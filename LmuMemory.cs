using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace User.TeamPace
{
    /// <summary>One car as LMU publishes it, live.</summary>
    internal class LmuCar
    {
        public int Id, Place, Laps, Pitstops, Gear;
        public string Driver = "", Vehicle = "", Class = "";
        public bool InPits;
        public double LapStartET, LastLap, BestLap, LapDist;
        public double Throttle, Brake, Speed;   // 0..1, 0..1, km/h
        internal int ScoreAt = -1, TeleAt = -1;  // where it was found, for the quick reads
    }

    /// <summary>The session and every car, as read in LMU's shared memory.</summary>
    internal class LmuState
    {
        public double ET, EndET;
        public int Session;            // 10 and above: race; 5 to 8: qualifying
        public string Track = "", PlayerName = "";
        public bool PlayerHasVehicle;
        public int PlayerIndex;
        public readonly List<LmuCar> Cars = new List<LmuCar>();

        public bool IsRace { get { return Session >= 10; } }
        public bool IsQualifying { get { return Session >= 5 && Session <= 8; } }
    }

    /// <summary>
    /// LMU's own shared memory ("LMU_Data", always there), or the rFactor 2 shared memory plugin's
    /// maps when present. SimHub reads nothing while one watches a teammate drive; LMU still
    /// publishes the session and every car (positions, laps, the time each lap started, stops,
    /// pedals and speed). The fuel, energy and tyres of other cars are placeholders, ours too
    /// when we only watch.
    /// </summary>
    internal static class LmuMemory
    {
        // LMU_Data: scoring info, then 128 vehicles of 584 bytes, then the telemetry (3 bytes of
        // header: active vehicles, player's index, player has a vehicle) and 1888 bytes a vehicle
        private const int LmuScoring = 1632, LmuVehicles = 2192, LmuTeleHeader = 128464, LmuTele = 128468;
        // rFactor 2 plugin maps: 12 bytes of header before the scoring info, 16 before the telemetry
        private const int RfScoring = 12, RfVehicles = 12 + 548, RfTeleCount = 12, RfTele = 16;
        private const int VehSize = 584, TeleSize = 1888, MaxCars = 128;

        private static MemoryMappedFile _lmuFile, _rfScoreFile, _rfTeleFile;
        private static MemoryMappedViewAccessor _lmu, _rfScore, _rfTele;
        private static DateTime _retry = DateTime.MinValue;
        private static readonly byte[] Text = new byte[64];
        private static double _lastET = double.NaN;
        private static DateTime _lastChange = DateTime.MinValue;

        /// <summary>
        /// The session and every car, or null while LMU publishes nothing or the session stands
        /// still (menu, pause) for more than 3 seconds. Reuses the given state's objects.
        /// </summary>
        public static LmuState Read(LmuState into)
        {
            if (!Open()) return null;
            try
            {
                var st = into ?? new LmuState();
                bool lmu = _lmu != null;
                var sc = lmu ? _lmu : _rfScore;
                int info = lmu ? LmuScoring : RfScoring, veh = lmu ? LmuVehicles : RfVehicles;
                int n = sc.ReadInt32(info + 104);
                if (n < 0 || n > MaxCars) { Close(); return null; }
                if (!Header(st)) return null;
                st.Track = Str(sc, info, 64);
                st.PlayerName = Str(sc, info + 116, 32);

                var tele = lmu ? _lmu : _rfTele;
                int teleBase = lmu ? LmuTele : RfTele;
                st.PlayerHasVehicle = lmu && _lmu.ReadByte(LmuTeleHeader + 2) != 0;
                st.PlayerIndex = lmu ? _lmu.ReadByte(LmuTeleHeader + 1) : -1;
                int teleCount = Math.Min(MaxCars, lmu ? _lmu.ReadByte(LmuTeleHeader) : _rfTele.ReadInt32(RfTeleCount));

                while (st.Cars.Count < n) st.Cars.Add(new LmuCar());
                if (st.Cars.Count > n) st.Cars.RemoveRange(n, st.Cars.Count - n);
                for (int k = 0; k < n; k++)
                {
                    var c = st.Cars[k];
                    int o = veh + k * VehSize;
                    c.Id = sc.ReadInt32(o);
                    c.ScoreAt = o;
                    c.Driver = Str(sc, o + 4, 32);
                    c.Vehicle = Str(sc, o + 36, 64);
                    c.Class = Str(sc, o + 200, 32);
                    // Its telemetry: the same index in LMU's map; looked up by id in the plugin's
                    c.TeleAt = -1;
                    if (lmu && k < teleCount && tele.ReadInt32(teleBase + k * TeleSize) == c.Id) c.TeleAt = teleBase + k * TeleSize;
                    else
                        for (int j = 0; j < teleCount; j++)
                            if (tele.ReadInt32(teleBase + j * TeleSize) == c.Id) { c.TeleAt = teleBase + j * TeleSize; break; }
                    Fill(c);
                }
                return st;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TeamPace] LMU memory: " + ex.Message);
                Close();
                return null;
            }
        }

        /// <summary>The session clock and one car again, between two full reads (nothing allocated). False: read everything again.</summary>
        public static bool Refresh(LmuState st, LmuCar c)
        {
            if (_lmu == null && _rfScore == null) return false;
            try
            {
                var sc = _lmu ?? _rfScore;
                if (c.ScoreAt < 0 || sc.ReadInt32(c.ScoreAt) != c.Id || !Header(st)) return false;
                Fill(c);
                return true;
            }
            catch { Close(); return false; }
        }

        /// <summary>Session type and clock; false when the clock stands still (menu, pause, LMU closed).</summary>
        private static bool Header(LmuState st)
        {
            var sc = _lmu ?? _rfScore;
            int info = _lmu != null ? LmuScoring : RfScoring;
            st.ET = sc.ReadDouble(info + 68);
            st.EndET = sc.ReadDouble(info + 76);
            st.Session = sc.ReadInt32(info + 64);
            var now = DateTime.UtcNow;
            if (st.ET != _lastET) { _lastET = st.ET; _lastChange = now; }
            return (now - _lastChange).TotalSeconds <= 3;
        }

        private static void Fill(LmuCar c)
        {
            var sc = _lmu ?? _rfScore;
            int o = c.ScoreAt;
            c.Laps = sc.ReadInt16(o + 100);
            c.LapDist = sc.ReadDouble(o + 104);
            c.BestLap = sc.ReadDouble(o + 144);
            c.LastLap = sc.ReadDouble(o + 168);
            c.Pitstops = sc.ReadInt16(o + 192);
            c.InPits = sc.ReadByte(o + 198) != 0;
            c.Place = sc.ReadByte(o + 199);
            c.LapStartET = sc.ReadDouble(o + 256);
            if (c.TeleAt < 0) return;
            var tele = _lmu ?? _rfTele;
            int t = c.TeleAt;
            c.Gear = tele.ReadInt32(t + 352);
            c.Throttle = tele.ReadDouble(t + 420);
            c.Brake = tele.ReadDouble(t + 428);
            double vx = tele.ReadDouble(t + 184), vy = tele.ReadDouble(t + 192), vz = tele.ReadDouble(t + 200);
            c.Speed = Math.Sqrt(vx * vx + vy * vy + vz * vz) * 3.6;
        }

        private static bool Open()
        {
            if (_lmu != null || (_rfScore != null && _rfTele != null)) return true;
            if (DateTime.UtcNow < _retry) return false;
            _retry = DateTime.UtcNow.AddSeconds(5);
            try
            {
                _lmuFile = MemoryMappedFile.OpenExisting("LMU_Data", MemoryMappedFileRights.Read);
                _lmu = _lmuFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                // The layout this reader knows: the track name in place and the telemetry count matching
                int n = _lmu.ReadInt32(LmuScoring + 104);
                if (n >= 0 && n <= MaxCars && (n == 0 || _lmu.ReadByte(LmuTeleHeader) == n)) return true;
                SimHub.Logging.Current.Warn("[TeamPace] LMU_Data: unknown layout, trying the rF2 plugin maps");
                Close();
            }
            catch { Close(); }
            try
            {
                _rfScoreFile = MemoryMappedFile.OpenExisting("$rFactor2SMMP_Scoring$", MemoryMappedFileRights.Read);
                _rfScore = _rfScoreFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _rfTeleFile = MemoryMappedFile.OpenExisting("$rFactor2SMMP_Telemetry$", MemoryMappedFileRights.Read);
                _rfTele = _rfTeleFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                return true;
            }
            catch { Close(); return false; }
        }

        private static void Close()
        {
            foreach (var d in new IDisposable[] { _lmu, _lmuFile, _rfScore, _rfScoreFile, _rfTele, _rfTeleFile })
                if (d != null) try { d.Dispose(); } catch { }
            _lmu = null; _lmuFile = null; _rfScore = null; _rfScoreFile = null; _rfTele = null; _rfTeleFile = null;
        }

        private static string Str(MemoryMappedViewAccessor a, int offset, int length)
        {
            int n = a.ReadArray(offset, Text, 0, Math.Min(length, Text.Length));
            int e = 0;
            while (e < n && Text[e] != 0) e++;
            return Encoding.UTF8.GetString(Text, 0, e);
        }
    }
}
