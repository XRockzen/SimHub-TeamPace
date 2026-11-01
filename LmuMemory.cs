using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace User.TeamPace
{
    /// <summary>One car as LMU publishes it, live.</summary>
    internal class LmuCar
    {
        public int Id, Place, Laps, Pitstops, Penalties, Gear, Control, PitState, FinishStatus, LapsBehindLeader;
        public string Driver = "", Vehicle = "", Class = "", Team = "";
        public bool InPits, IsPlayer;
        public ulong SteamId;
        public double LapStartET, LastLap, BestLap, LapDist, TimeBehindLeader;
        public double FuelFraction;   // 0..1, fuel or battery left: published for every car

        // Telemetry: complete for the car driven on this PC, partly filled by LMU for the others
        public bool HasTelemetry;
        public double Throttle, Brake, Speed;           // the driver's pedals 0..1 (unfiltered, as LMU records them), km/h
        public double Fuel, FuelCapacity, Energy, Battery;   // litres, litres, virtual energy 0..1 (0: none), 0..1
        public bool LapInvalid, Limiter;
        public readonly double[] Wear = new double[4];  // FL, FR, RL, RR: LMU's 0..1
        public string FrontCompound = "", RearCompound = "";
        internal int ScoreAt = -1, TeleAt = -1;          // slots in LMU's arrays, for the quick reads

        public const int PitNone = 0, PitRequest = 1, PitEntering = 2, PitStopped = 3, PitExiting = 4;
    }

    /// <summary>The session and every car, as read in LMU's shared memory.</summary>
    internal class LmuState
    {
        public double ET, EndET, TimeLeft;
        public int Session;            // 0 test day, 1-4 practice, 5-8 qualifying, 9 warmup, 10-13 race
        public int Phase;              // 0 before, 1-4 formation and start, 5 green, 6 full course yellow, 7 stopped, 8 over, 9 paused
        public int YellowState;        // full course yellow: -1 invalid, 0 none, 1 pending, 2 pits closed, 3 pit lead lap, 4 pits open, 5 last lap, 6 resume
        public string Track = "", PlayerName = "";
        public bool PlayerHasVehicle, InRealtime;
        public int PlayerIndex = -1;   // in the telemetry array
        public double Raining, TrackTemp, AmbientTemp, Wetness;
        public readonly List<LmuCar> Cars = new List<LmuCar>();

        public bool IsRace { get { return Session >= 10; } }
        public bool IsQualifying { get { return Session >= 5 && Session <= 8; } }

        /// <summary>The car driven on this PC, or null (watching, at the monitor).</summary>
        public LmuCar Player
        {
            get
            {
                if (!PlayerHasVehicle) return null;
                foreach (var c in Cars) if (c.IsPlayer) return c;
                foreach (var c in Cars) if (c.TeleAt == PlayerIndex) return c;
                return null;
            }
        }
    }

    /// <summary>
    /// LMU's own shared memory ("LMU_Data"), read as Studio 397's SDK documents it
    /// (Support\SharedMemoryInterface): copied under LMU's lock in one go, then decoded. LMU
    /// publishes the session and every car, and the complete telemetry of the car driven on
    /// this PC; SimHub reads nothing while one watches a teammate.
    /// </summary>
    internal static unsafe class LmuMemory
    {
        // SharedMemoryObjectOut: generic, paths (5 × MAX_PATH), scoring (info, 104 vehicles,
        // results stream), telemetry (3 bytes of header, 104 vehicles)
        private const int GameVersionAt = 64, PathsAt = 332, PathSize = 260;
        private const int InfoAt = 1632, InfoSize = 548, VehiclesAt = 2192, VehSize = 584;
        private const int TeleHeaderAt = 128464, TeleAt = 128468, TeleSize = 1888, MaxCars = 104;
        private const int MapSize = TeleAt + MaxCars * TeleSize;

        private static MemoryMappedFile _file;
        private static MemoryMappedViewAccessor _view;
        private static byte* _base;
        private static MemoryMappedFile _lockFile;
        private static MemoryMappedViewAccessor _lockView;
        private static int* _lock;   // { waiters, busy }
        private static EventWaitHandle _lockEvent;
        private static DateTime _retry = DateTime.MinValue;

        private static readonly byte[] Info = new byte[InfoSize], Head = new byte[3];
        private static readonly byte[] Veh = new byte[MaxCars * VehSize], Tele = new byte[MaxCars * TeleSize];
        private static double _lastET = double.NaN;
        private static DateTime _lastChange = DateTime.MinValue;

        public static long GameVersion { get; private set; }
        public static int LockMissed;   // reads made without the lock (LMU held it too long)

        /// <summary>
        /// The session and every car, or null while LMU publishes nothing or the session stands
        /// still (menu, pause) for more than 3 seconds. Reuses the given state's objects.
        /// </summary>
        public static LmuState Read(LmuState into)
        {
            if (!Open()) return null;
            try
            {
                int n, tn;
                bool locked = Lock();
                try
                {
                    Copy(InfoAt, Info, InfoSize);
                    n = I32(Info, 104);
                    if (n < 0 || n > MaxCars) n = -1;
                    else
                    {
                        Copy(VehiclesAt, Veh, n * VehSize);
                        Copy(TeleHeaderAt, Head, 3);
                        tn = Math.Min(MaxCars, (int)Head[0]);
                        Copy(TeleAt, Tele, tn * TeleSize);
                    }
                }
                finally { if (locked) Unlock(); }
                if (n < 0) { Close(); return null; }
                tn = Math.Min(MaxCars, (int)Head[0]);

                var st = into ?? new LmuState();
                if (!Header(st)) return null;
                st.Track = Str(Info, 0, 64);
                st.PlayerName = Str(Info, 116, 32);
                st.PlayerIndex = Head[1];
                st.PlayerHasVehicle = Head[2] != 0;

                while (st.Cars.Count < n) st.Cars.Add(new LmuCar());
                if (st.Cars.Count > n) st.Cars.RemoveRange(n, st.Cars.Count - n);
                for (int k = 0; k < n; k++)
                {
                    var c = st.Cars[k];
                    int o = k * VehSize;
                    c.Id = I32(Veh, o);
                    c.ScoreAt = k;
                    c.Driver = Str(Veh, o + 4, 32);
                    c.Vehicle = Str(Veh, o + 36, 64);
                    c.Class = Str(Veh, o + 200, 32);
                    c.Team = Str(Veh, o + 480, 24);
                    // Its telemetry: the same slot as a rule, else looked up by id
                    c.TeleAt = -1;
                    if (k < tn && I32(Tele, k * TeleSize) == c.Id) c.TeleAt = k;
                    else
                        for (int j = 0; j < tn; j++)
                            if (I32(Tele, j * TeleSize) == c.Id) { c.TeleAt = j; break; }
                    Score(c, o);
                    if (c.TeleAt >= 0) Telemetry(c, c.TeleAt * TeleSize);
                    c.HasTelemetry = c.TeleAt >= 0;
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

        /// <summary>The session clock and one car again, between two full reads. False: read everything again.</summary>
        public static bool Refresh(LmuState st, LmuCar c)
        {
            if (_base == null || st == null || c == null || c.ScoreAt < 0) return false;
            try
            {
                int o = c.ScoreAt * VehSize, t = c.TeleAt * TeleSize;
                bool locked = Lock();
                try
                {
                    Copy(InfoAt, Info, InfoSize);
                    Copy(VehiclesAt + o, Veh, o, VehSize);
                    if (c.TeleAt >= 0) Copy(TeleAt + t, Tele, t, TeleSize);
                }
                finally { if (locked) Unlock(); }
                if (I32(Veh, o) != c.Id || !Header(st)) return false;
                Score(c, o);
                if (c.TeleAt >= 0)
                {
                    if (I32(Tele, t) != c.Id) return false;
                    Telemetry(c, t);
                }
                return true;
            }
            catch { Close(); return false; }
        }

        /// <summary>LMU's UserData folder as LMU publishes it (kept once LMU has been seen), or null.</summary>
        public static volatile string UserData;

        private static string ReadPath(int index)
        {
            var b = new byte[PathSize];
            Copy(PathsAt + index * PathSize, b, PathSize);
            int e = 0;
            while (e < b.Length && b[e] != 0) e++;
            return e > 0 ? Encoding.Default.GetString(b, 0, e) : null;
        }

        /// <summary>Session type, phase and clock; false when the clock stands still (menu, pause, LMU closed).</summary>
        private static bool Header(LmuState st)
        {
            st.Session = I32(Info, 64);
            st.ET = F64(Info, 68);
            st.EndET = F64(Info, 76);
            st.Phase = Info[108];
            st.YellowState = (sbyte)Info[109];
            st.InRealtime = Info[115] != 0;
            st.Raining = F64(Info, 220);
            st.AmbientTemp = F64(Info, 228);
            st.TrackTemp = F64(Info, 236);
            st.Wetness = F64(Info, 332);
            double left = BitConverter.ToSingle(Info, 340);
            st.TimeLeft = left > 0 ? left : st.EndET > 0 ? Math.Max(0, st.EndET - st.ET) : 0;
            var now = DateTime.UtcNow;
            if (st.ET != _lastET) { _lastET = st.ET; _lastChange = now; }
            return (now - _lastChange).TotalSeconds <= 3;
        }

        private static void Score(LmuCar c, int o)
        {
            c.Laps = BitConverter.ToInt16(Veh, o + 100);
            c.FinishStatus = (sbyte)Veh[o + 103];
            c.LapDist = F64(Veh, o + 104);
            c.BestLap = F64(Veh, o + 144);
            c.LastLap = F64(Veh, o + 168);
            c.Pitstops = BitConverter.ToInt16(Veh, o + 192);
            c.Penalties = BitConverter.ToInt16(Veh, o + 194);
            c.IsPlayer = Veh[o + 196] != 0;
            c.Control = (sbyte)Veh[o + 197];
            c.InPits = Veh[o + 198] != 0;
            c.Place = Veh[o + 199];
            c.TimeBehindLeader = F64(Veh, o + 244);
            c.LapsBehindLeader = I32(Veh, o + 252);
            c.LapStartET = F64(Veh, o + 256);
            c.PitState = Veh[o + 457];
            c.SteamId = BitConverter.ToUInt64(Veh, o + 536);
            c.FuelFraction = Veh[o + 578] / 255.0;
        }

        private static void Telemetry(LmuCar c, int t)
        {
            c.Gear = I32(Tele, t + 352);
            c.Throttle = F64(Tele, t + 388);
            c.Brake = F64(Tele, t + 396);
            double vx = F64(Tele, t + 184), vy = F64(Tele, t + 192), vz = F64(Tele, t + 200);
            c.Speed = Math.Sqrt(vx * vx + vy * vy + vz * vz) * 3.6;
            c.Fuel = F64(Tele, t + 524);
            c.Limiter = Tele[t + 604] != 0;
            c.FuelCapacity = F64(Tele, t + 608);
            c.Battery = F64(Tele, t + 704);
            c.LapInvalid = Tele[t + 745] != 0;
            double ve = BitConverter.ToSingle(Tele, t + 776);
            c.Energy = ve > 1.001 ? ve / 100 : ve > 0 ? ve : 0;   // a fraction; read as a percentage if above 1
            for (int i = 0; i < 4; i++) c.Wear[i] = F64(Tele, t + 848 + i * 260 + 152);
            c.FrontCompound = Str(Tele, t + 620, 18);
            c.RearCompound = Str(Tele, t + 638, 18);
        }

        // ─── LMU's lock: a spin on { waiters, busy }, never long enough to hold the game up ──

        private static bool Lock()
        {
            if (_lock == null) return false;
            for (int i = 0; i < 1000; i++)   // the SDK's TryLockSpin
            {
                if (Interlocked.CompareExchange(ref *(_lock + 1), 1, 0) == 0) return true;
                Thread.SpinWait(4);
            }
            LockMissed++;
            return false;
        }

        private static void Unlock()
        {
            Interlocked.Exchange(ref *(_lock + 1), 0);
            if (Thread.VolatileRead(ref *_lock) > 0 && _lockEvent != null) _lockEvent.Set();
        }

        private static bool Open()
        {
            if (_base != null) return true;
            if (DateTime.UtcNow < _retry) return false;
            _retry = DateTime.UtcNow.AddSeconds(5);
            try
            {
                _file = MemoryMappedFile.OpenExisting("LMU_Data", MemoryMappedFileRights.Read);
                _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (_view.Capacity < MapSize) { Close(); return false; }
                byte* p = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _base = p + _view.PointerOffset;
                // The layout of the SDK: a plausible car count, the telemetry count matching it
                int n = *(int*)(_base + InfoAt + 104);
                if (n < 0 || n > MaxCars || (n > 0 && _base[TeleHeaderAt] != n))
                {
                    SimHub.Logging.Current.Warn("[TeamPace] LMU_Data: layout not recognised (LMU update?)");
                    Close();
                    _retry = DateTime.UtcNow.AddSeconds(30);
                    return false;
                }
                GameVersion = *(int*)(_base + GameVersionAt);
                var user = ReadPath(0);
                if (!string.IsNullOrEmpty(user))
                {
                    // Relative ("UserData\") or full: made full from LMU's process folder when relative
                    if (!System.IO.Path.IsPathRooted(user))
                        try
                        {
                            var lmu = System.Diagnostics.Process.GetProcessesByName("Le Mans Ultimate");
                            if (lmu.Length > 0) user = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(lmu[0].MainModule.FileName), user);
                        }
                        catch { user = null; }
                    if (user != null && System.IO.Path.IsPathRooted(user)) UserData = user;
                }
                OpenLock();
                SimHub.Logging.Current.Info(string.Format("[TeamPace] LMU_Data open: game version {0}, lock {1}", GameVersion, _lock != null ? "shared" : "unavailable"));
                return true;
            }
            catch { Close(); return false; }
        }

        private static void OpenLock()
        {
            try
            {
                _lockFile = MemoryMappedFile.OpenExisting("LMU_SharedMemoryLockData", MemoryMappedFileRights.ReadWrite);
                _lockView = _lockFile.CreateViewAccessor(0, 8, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                _lockView.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _lock = (int*)(p + _lockView.PointerOffset);
                _lockEvent = EventWaitHandle.OpenExisting("LMU_SharedMemoryLockEvent", EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify);
            }
            catch
            {
                // Without the event the lock cannot wake LMU up: read without it
                CloseLock();
            }
        }

        private static void CloseLock()
        {
            if (_lock != null) { try { _lockView.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { } _lock = null; }
            foreach (var d in new IDisposable[] { _lockEvent, _lockView, _lockFile })
                if (d != null) try { d.Dispose(); } catch { }
            _lockEvent = null; _lockView = null; _lockFile = null;
        }

        private static void Close()
        {
            CloseLock();
            if (_base != null) { try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { } _base = null; }
            foreach (var d in new IDisposable[] { _view, _file })
                if (d != null) try { d.Dispose(); } catch { }
            _view = null; _file = null;
        }

        private static void Copy(int from, byte[] to, int length) { Copy(from, to, 0, length); }

        private static void Copy(int from, byte[] to, int at, int length)
        {
            if (length > 0) Marshal.Copy((IntPtr)(_base + from), to, at, length);
        }

        private static int I32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }
        private static double F64(byte[] b, int o) { return BitConverter.ToDouble(b, o); }

        private static string Str(byte[] b, int o, int length)
        {
            int e = 0;
            while (e < length && b[o + e] != 0) e++;
            return e == 0 ? "" : Encoding.UTF8.GetString(b, o, e);
        }
    }

    /// <summary>
    /// LMU's memory read once per SimHub frame, shared by the overlay, the pitwall, the lap
    /// recorder and the invalid laps: every car ten times a second, ours at every frame.
    /// </summary>
    internal static class LmuLive
    {
        public static LmuState State;   // null: LMU publishes nothing (closed, menu, pause)
        public static LmuCar Us;        // our car: the one driven here, else our team's
        public static bool Driving;     // Us is driven on this PC: its telemetry is complete
        public static bool FullFrame;   // every car was read this frame
        private static int _frame;
        private static string _ourTeam, _ourVehicle;
        private static volatile HashSet<string> _teamNames;
        private static bool _namesLoading;

        public static void Tick()
        {
            FullFrame = State == null || Us == null || ++_frame % 6 == 0;
            if (FullFrame)
            {
                State = LmuMemory.Read(State);
                Us = State != null ? OurCar(State) : null;
            }
            else if (!LmuMemory.Refresh(State, Us)) { State = null; Us = null; }
            Driving = State != null && Us != null && State.PlayerHasVehicle && Us.IsPlayer;
        }

        /// <summary>The team names changed (team folder pulled): looked up again.</summary>
        public static void ForgetNames() { _teamNames = null; _namesLoading = false; }

        /// <summary>
        /// Our car: the one driven on this PC; else the car last driven here (its team and
        /// vehicle as LMU names them); else the one a driver of the team is driving.
        /// </summary>
        private static LmuCar OurCar(LmuState st)
        {
            var p = st.Player;
            if (p != null) { _ourTeam = p.Team; _ourVehicle = p.Vehicle; return p; }
            if (_ourVehicle != null)
                foreach (var c in st.Cars)
                    if (c.Vehicle == _ourVehicle && c.Team == _ourTeam) return c;
            var names = _teamNames;
            if (names == null && !_namesLoading)
            {
                _namesLoading = true;
                Task.Run(() =>
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try { foreach (var td in LmuTeamSync.GetDrivers()) set.Add(td.Name.Trim()); }
                    catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Team names: " + ex.Message); }
                    _teamNames = set;
                });
            }
            foreach (var c in st.Cars)
                if (string.Equals(c.Driver.Trim(), st.PlayerName.Trim(), StringComparison.OrdinalIgnoreCase) || (names != null && names.Contains(c.Driver.Trim())))
                {
                    _ourTeam = c.Team; _ourVehicle = c.Vehicle;   // kept through the driver changes
                    return c;
                }
            return null;
        }
    }
}
