using GameReaderCommon;
using System;
using System.Threading.Tasks;

namespace User.TeamPace
{
    public class LmuLiveLap
    {
        public int    Id             { get; set; }
        public long   StartUtcTicks  { get; set; }
        public long   EndUtcTicks    { get; set; }
        public int    LapNum         { get; set; }
        public string Track          { get; set; }
        public double FuelUsedL      { get; set; }
        public double FuelLevelL     { get; set; }
        public double WearFL         { get; set; }   // % of tyre worn during this lap
        public double WearFR         { get; set; }
        public double WearRL         { get; set; }
        public double WearRR         { get; set; }
        public bool   InPitLane      { get; set; }

        // Heart rate ("Montre Da Fit" plugin), time-weighted on the valid measures; absent in older records
        public double   HrAvg        { get; set; }   // BPM, 0 = no heart rate this lap
        public int      HrMin        { get; set; }
        public int      HrMax        { get; set; }
        public double   HrValidPct   { get; set; }   // share of the lap with a valid measure, %
        public double[] HrZoneSec    { get; set; }   // seconds in zones 0 to 5
        public double[] HrSectorAvg  { get; set; }   // sectors 1 to 3, 0 = not measured
        public int[]    HrSectorMax  { get; set; }
        public byte[]   HrProfile    { get; set; }   // BPM every 50 m from the line, 0 = not measured
        public int      HrStartPeak  { get; set; }   // peak of the first minute of the session (green flag, or leaving the pits)

        [LiteDB.BsonIgnore] public bool HasHeart { get { return HrAvg > 0; } }
    }

    public class LmuLivePit
    {
        public int    Id             { get; set; }
        public long   EntryUtcTicks  { get; set; }
        public long   ExitUtcTicks   { get; set; }
        public int    LapNum         { get; set; }   // lap being driven at pit entry
        public string Track          { get; set; }
        public double LaneSec        { get; set; }   // pit entry → pit exit
        public double StoppedSec     { get; set; }   // speed ~0 inside the pit lane
        public double LimiterSec     { get; set; }   // pit limiter engaged inside the lane
        public double FuelAddedL     { get; set; }
        public bool   TyresChanged   { get; set; }
        public int    TyresCount     { get; set; }   // wheels changed (0 when unknown, older captures)
        public bool   FromGarage     { get; set; }   // lane passage started in the garage, not a stop
    }

    /// <summary>
    /// Records per-lap fuel/tyre usage and pit stop timings: from LMU's memory when the car is
    /// driven on this PC (its pit state says when the car stands in its box), else from SimHub.
    /// </summary>
    public class LmuLiveTracker
    {
        private const double MaxTickSec     = 1.0;   // ignore gaps (pause, alt-tab)
        private const double MaxWearStepPct = 2.0;   // a bigger jump in one tick = new tyres
        private const double PitWearStepPct = 0.2;   // same, while stopped in the lane (fresh tyres barely differ)
        private const double StoppedKmh     = 1.0;
        private const double GarageSpawnKmh = 5.0;   // entering the lane from the track is never this slow

        private string   _track;
        private int      _lastCompleted = -1;
        private DateTime _lastTick;

        // Current lap accumulators
        private DateTime _lapStart;
        private bool     _lapStarted;
        private double   _fuelUsed;
        private double[] _wear = new double[4];
        private bool     _lapInPit;

        private double   _prevFuel = -1;
        private double[] _prevTyre;
        private double[] _tyreBuf = new double[4];

        // Current pit stop
        private bool     _inLane;
        private DateTime _pitEntry;
        private int      _pitLap;
        private double   _pitStopped, _pitLimiter, _pitFuelAdded;
        private bool[]   _pitTyres = new bool[4];
        private bool     _pitFromGarage;

        // Heart rate of the lap under way, and the peak of the session's first minute
        private readonly HeartLap _heart = new HeartLap();
        private bool     _started;
        private DateTime _startUntil;
        private int      _startPeak, _lastPhase;

        public void Update(GameData data)
        {
            var now = DateTime.UtcNow;
            var us  = LmuLive.Us;
            var tyre = _tyreBuf;   // two buffers swapped every tick: no allocation per frame
            if (LmuLive.Driving)
            {
                // LMU's own values: fuel, wear (0..1, here in %), pit state and limiter
                for (int i = 0; i < 4; i++) tyre[i] = us.Wear[i] * 100;
                // The race starts at the green flag
                int phase = LmuLive.State.Phase;
                if (LmuLive.State.IsRace && phase == 5 && _lastPhase > 0 && _lastPhase < 5) StartWindow(now);
                _lastPhase = phase;
                Tick(now, LmuLive.State.Track, us.Laps, us.Fuel, tyre, us.Speed, us.InPits, us.Limiter, us.PitState == LmuCar.PitStopped,
                     us.LapDist, us.Sector);
                return;
            }
            var d = data.NewData;
            if (d == null || data.GamePaused || data.GameReplay || data.GameInMenu)
            {
                _lastTick = DateTime.MinValue;
                return;
            }
            tyre[0] = d.TyreWearFrontLeft; tyre[1] = d.TyreWearFrontRight; tyre[2] = d.TyreWearRearLeft; tyre[3] = d.TyreWearRearRight;
            Tick(now, d.TrackName ?? "", d.CompletedLaps, d.Fuel, tyre, d.SpeedKmh, d.IsInPitLane != 0, d.PitLimiterOn != 0, d.SpeedKmh < StoppedKmh,
                 d.TrackPositionMeters, Math.Max(-1, Math.Min(2, d.CurrentSectorIndex - 1)));
        }

        private void Tick(DateTime now, string track, int completed, double fuel, double[] tyre, double speed, bool inLane, bool limiter, bool stopped,
                          double dist, int sector)
        {
            double dt = _lastTick == DateTime.MinValue ? 0 : (now - _lastTick).TotalSeconds;
            if (dt < 0 || dt > MaxTickSec) dt = 0;
            _lastTick = now;

            if (track != _track || completed < _lastCompleted)
                ResetSession(track);

            // Heart rate: the session's first minute (once the car moves, or from the green flag), and the lap
            int bpm = Heart.Bpm;
            if (!_started && speed > 10) StartWindow(now);
            if (now < _startUntil && bpm > _startPeak) _startPeak = bpm;
            _heart.Add(dt, bpm, Heart.Zone, dist, sector);

            AccumulateFuel(fuel, _inLane);
            AccumulateWear(tyre, _inLane && stopped);
            TrackPit(inLane, speed, limiter, stopped, now, dt, completed);

            if (completed < 0) return;

            if (_lastCompleted >= 0 && completed > _lastCompleted && _lapStarted)
                SaveLap(now, completed, fuel);

            if (_lastCompleted < 0 || completed > _lastCompleted)
                StartLap(now);

            _lastCompleted = completed;
        }

        private void ResetSession(string track)
        {
            _track         = track;
            _lastCompleted = -1;
            _lapStarted    = false;
            _inLane        = false;
            _prevFuel      = -1;
            _prevTyre      = null;
            _started       = false;
            _startPeak     = 0;
            _startUntil    = DateTime.MinValue;
            _heart.Reset();
        }

        private void StartWindow(DateTime now)
        {
            _started = true;
            _startPeak = 0;
            _startUntil = now.AddSeconds(60);
        }

        private void StartLap(DateTime now)
        {
            _lapStart   = now;
            _lapStarted = true;
            _fuelUsed   = 0;
            _wear       = new double[4];
            _lapInPit   = _inLane;
            _heart.Reset();
        }

        private void AccumulateFuel(double fuel, bool inLane)
        {
            if (_prevFuel >= 0)
            {
                double delta = _prevFuel - fuel;
                if (delta > 0) _fuelUsed += delta;
                else if (delta < 0 && inLane) _pitFuelAdded += -delta;
            }
            _prevFuel = fuel;
        }

        // Worn or remaining, only the magnitude of the change is used; large steps are tyre changes.
        private void AccumulateWear(double[] tyre, bool stoppedInLane)
        {
            if (_prevTyre != null)
            {
                double limit = stoppedInLane ? PitWearStepPct : MaxWearStepPct;
                for (int i = 0; i < 4; i++)
                {
                    double step = Math.Abs(tyre[i] - _prevTyre[i]);
                    if (step > limit) { if (_inLane) _pitTyres[i] = true; }
                    else _wear[i] += step;
                }
            }
            _tyreBuf = _prevTyre ?? new double[4];
            _prevTyre = tyre;
        }

        private void TrackPit(bool inLane, double speed, bool limiter, bool stopped, DateTime now, double dt, int completed)
        {
            if (inLane && !_inLane)
            {
                _pitEntry     = now;
                _pitLap       = completed + 1;
                _pitStopped   = 0;
                _pitLimiter   = 0;
                _pitFuelAdded = 0;
                _pitTyres     = new bool[4];
                _lapInPit     = true;
                // Escape → garage puts the car back in the lane at a standstill: driving out
                // of the garage afterwards is a lane passage, not a pit stop.
                _pitFromGarage = dt == 0 || speed < GarageSpawnKmh;
            }
            else if (inLane)
            {
                if (stopped) _pitStopped += dt;
                if (limiter) _pitLimiter += dt;
            }
            else if (_inLane)
            {
                int tyres = 0;
                foreach (var t in _pitTyres) if (t) tyres++;
                var pit = new LmuLivePit
                {
                    EntryUtcTicks = _pitEntry.Ticks,
                    ExitUtcTicks  = now.Ticks,
                    LapNum        = _pitLap,
                    Track         = _track,
                    LaneSec       = (now - _pitEntry).TotalSeconds,
                    StoppedSec    = _pitStopped,
                    LimiterSec    = _pitLimiter,
                    FuelAddedL    = _pitFuelAdded,
                    TyresChanged  = tyres > 0,
                    TyresCount    = tyres,
                    FromGarage    = _pitFromGarage,
                };
                Task.Run(() => LmuSessionStore.SaveLivePit(pit));
            }

            _inLane = inLane;
        }

        private void SaveLap(DateTime now, int lapNum, double fuel)
        {
            var lap = new LmuLiveLap
            {
                StartUtcTicks = _lapStart.Ticks,
                EndUtcTicks   = now.Ticks,
                LapNum        = lapNum,
                Track         = _track,
                FuelUsedL     = _fuelUsed,
                FuelLevelL    = fuel,
                WearFL        = _wear[0],
                WearFR        = _wear[1],
                WearRL        = _wear[2],
                WearRR        = _wear[3],
                InPitLane     = _lapInPit,
            };
            _heart.Fill(lap);
            // The start's peak goes with the lap during which its minute ended
            if (_startPeak > 0 && now >= _startUntil && lap.HasHeart) { lap.HrStartPeak = _startPeak; _startPeak = 0; }
            Task.Run(() => LmuSessionStore.SaveLiveLap(lap));
        }
    }
}
