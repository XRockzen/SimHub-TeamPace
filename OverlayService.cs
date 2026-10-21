using GameReaderCommon;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace User.TeamPace
{
    /// <summary>The strategy plan followed on the race page, simulated once per circuit.</summary>
    internal class OverlayRace
    {
        public StrategyPlan Plan;
        public StrategyEngine.Simulation Sim;
        public StrategyEngine.Measures Measures;
        public double FuelPerLap = double.NaN;
        public string Error;

        public static OverlayRace Build(string circuit, string classLabel, OverlaySettings s)
        {
            var r = new OverlayRace();
            var plans = StrategyEngine.SavedPlans();   // newest first
            if (!string.IsNullOrEmpty(s.StrategyPlan)) r.Plan = plans.Find(p => p.Id == s.StrategyPlan);
            if (r.Plan == null) r.Plan = plans.Find(p => p.Circuit == circuit && (classLabel == null || p.ClassLabel == classLabel));
            if (r.Plan == null) { r.Error = "Aucun plan de stratégie pour ce circuit"; return r; }
            var drivers = new List<DriverView>();
            int k = 0;
            foreach (var td in LmuTeamSync.GetDrivers()) drivers.Add(new DriverView(td, DriverView.Palette[k++ % DriverView.Palette.Length]));
            var m = StrategyEngine.Measure(drivers, r.Plan.Circuit, r.Plan.ClassLabel, r.Plan.TankL);
            StrategyEngine.ApplyOverrides(m, r.Plan);
            r.Measures = m;
            r.Sim = StrategyEngine.Simulate(r.Plan, m, false);
            var me = m.Drivers.Find(d => d.Driver.DbPath == null);
            if (me != null && me.Values[StrategyEngine.Fuel].Value > 0) r.FuelPerLap = me.Values[StrategyEngine.Fuel].Value;
            if (r.Sim.Stints.Count == 0) r.Error = r.Sim.Warnings.Count > 0 ? r.Sim.Warnings[0] : "Plan incomplet";
            return r;
        }

        /// <summary>The plan's driver of a stint.</summary>
        public string DriverOf(int stint)
        {
            if (Plan == null || Measures == null) return null;
            var keys = Plan.Drivers.FindAll(k => Measures.Drivers.Exists(d => d.Driver.Name == k));
            return keys.Count > 0 ? StrategyEngine.DriverOf(Plan, keys, stint) : null;
        }

        /// <summary>The plan's pace of a driver, s.</summary>
        public double PaceOf(string driver)
        {
            var d = Measures != null ? Measures.Drivers.Find(x => x.Driver.Name == driver) : null;
            return d != null ? d.Values[StrategyEngine.Pace].Value : double.NaN;
        }

        /// <summary>Tyres used by the plan once a number of race stops are made (the qualifying's included).</summary>
        public int TyresUsedAfter(int stops)
        {
            int n = Sim.Quali != null ? Sim.Quali.Stop0.Tyres + Sim.Quali.Stop1.Tyres : 4;
            for (int i = 0; i < stops && i < Sim.Stops.Count; i++) n += Sim.Stops[i].Tyres;
            return n;
        }

        /// <summary>The measures with what the race shows so far for the driver in the car: pace and consumption.</summary>
        public StrategyEngine.Measures LiveMeasures(double pace, double fuel, int stint)
        {
            var m = new StrategyEngine.Measures { Team = Measures.Team, CornerShare = Measures.CornerShare };
            string current = DriverOf(stint);
            foreach (var d in Measures.Drivers)
            {
                var dd = new StrategyEngine.DriverData { Driver = d.Driver };
                foreach (var kv in d.Values)
                    dd.Values[kv.Key] = new StrategyValue { Value = kv.Value.Value, Measured = kv.Value.Measured, Overridden = kv.Value.Overridden, Source = kv.Value.Source };
                if (d.Driver.Name == current)
                {
                    if (!double.IsNaN(pace)) dd.Values[StrategyEngine.Pace].Value = pace;
                    if (!double.IsNaN(fuel))
                    {
                        // The virtual energy goes with the fuel actually burnt
                        double old = dd.Values[StrategyEngine.Fuel].Value;
                        dd.Values[StrategyEngine.Fuel].Value = fuel;
                        if (old > 0 && dd.Values[StrategyEngine.Energy].Value > 0) dd.Values[StrategyEngine.Energy].Value *= fuel / old;
                    }
                }
                m.Drivers.Add(dd);
            }
            return m;
        }

        /// <summary>Race time at the end of a lap, according to the plan (stops included).</summary>
        public double PlanTimeAt(int laps)
        {
            if (Sim == null) return double.NaN;
            int first = 0;
            foreach (var st in Sim.Stints)
            {
                if (laps <= first + st.Laps && st.Laps > 0) return st.Start + (st.End - st.Start) * (laps - first) / st.Laps;
                first += st.Laps;
            }
            return double.NaN;
        }
    }

    /// <summary>
    /// The in-race overlay: fed by SimHub's game loop (no allocation, no disk), it keeps the
    /// last seconds of inputs, the current lap measured against the reference lap, and the
    /// passages through the corners being worked on. The window reads these values 30 times
    /// a second.
    /// </summary>
    internal static class OverlayService
    {
        // ─── Live values (written by the game loop, read by the window) ───────
        public static double Throttle, Brake, Speed, Dist;
        public static string Gear = "N";
        public static double Delta = double.NaN, CornerDelta = double.NaN, RefSpeed = double.NaN;
        public static int RefGear, CornerNumber, Lap;
        public static double Fuel, SessionLeft;
        public static bool IsRace;
        public static OverlayFocus Approach, Bilan, Next;
        public static double ApproachLeft, NextIn;
        public static double SessionFirst = double.NaN, SessionBest = double.NaN, PlanGap = double.NaN;
        public static int StopView;

        // The race as it goes: the plan recalculated at the end of each lap from where the race stands
        public static volatile StrategyEngine.Simulation LiveSim;
        public static int LiveLap = -1, RaceStops, StintLaps;
        public static double LivePace = double.NaN, LiveFuel = double.NaN, StintStart;
        public static double[] RecentLaps = new double[0];   // the last laps of the race, s (a fresh array each lap: safe to read)
        private static readonly List<double> RaceLaps = new List<double>(), RaceFuel = new List<double>();
        private static bool _inLane, _pitThisLap, _wasRace;
        private static double _lapStartFuel = -1;
        private static int _tyreLap;

        /// <summary>Race time from the time left in a timed race; NaN when unknown.</summary>
        public static double RaceTime
        {
            get
            {
                var race = _race;
                if (race == null || race.Plan == null || !(SessionLeft > 0)) return double.NaN;
                double t = race.Plan.DurationMin * 60.0 - SessionLeft;
                return t >= 0 ? t : double.NaN;
            }
        }
        public static bool Demo;   // preview with made-up values (game not running)

        // The last seconds of inputs: yours and the reference's at the same place on track
        public const int Cap = 4096;
        public static readonly float[] RingT = new float[Cap], RingThr = new float[Cap], RingBrk = new float[Cap], RingGThr = new float[Cap], RingGBrk = new float[Cap];
        public static volatile int Head;
        public static int Count;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        public static double Now { get { return Clock.Elapsed.TotalSeconds; } }

        private static volatile OverlayReference _ref;
        private static volatile OverlayRace _race;
        public static OverlayReference Reference { get { return _ref; } }
        public static OverlayRace Race { get { return _race; } }

        public static string Status = "Pas encore de référence : elle se charge quand vous roulez.";
        public static string TrackLive = "", Circuit, ClassLabel;
        public static event Action StatusChanged;

        private static volatile bool _running;
        private static double _lastFrame = -10;
        public static bool Live { get { return _running && Now - _lastFrame < 2; } }

        // ─── Game loop ────────────────────────────────────────────────────────

        private static string _track, _cls;
        private static int _completed = -1;
        private static TeleTrace _cur, _last;
        private static int _curFirst = -1, _curLast = -1, _lastFirst = -1, _lastLast = -1;
        private static bool _fromLine;
        private static double _prevDist, _prevT;
        private static int _sector = -1;
        private static OverlayFocus _pending;
        private static readonly List<CoachingEngine.Lap> LivePass = new List<CoachingEngine.Lap> { new CoachingEngine.Lap() };

        public static void Update(GameData data)
        {
            var d = data.NewData;
            if (!data.GameRunning || d == null || data.GameInMenu || data.GameReplay) { _running = false; return; }
            _running = true;
            _lastFrame = Now;
            Demo = false;
            var s = OverlaySettings.Current;
            if (!s.Enabled && !s.PitwallEnabled && !EditMode && !PitEditMode) return;   // nothing computed while both are off

            var track = d.TrackName ?? "";
            var cls = d.CarClass ?? "";
            if (!string.Equals(track, _track, StringComparison.Ordinal) || !string.Equals(cls, _cls, StringComparison.Ordinal))
            {
                _track = track; _cls = cls; TrackLive = track;
                ResetSession();
                RequestReference();
            }

            Fuel = d.Fuel;
            SessionLeft = d.SessionTimeLeft.TotalSeconds;
            IsRace = d.SessionTypeName != null && d.SessionTypeName.IndexOf("race", StringComparison.OrdinalIgnoreCase) >= 0;
            if (IsRace != _wasRace) { _wasRace = IsRace; ResetRace(); }
            // Pit stops of the race: counted at the lane entry, the stint restarts at the exit
            bool lane = d.IsInPitLane != 0;
            if (IsRace && lane && !_inLane && d.SpeedKmh > 5)
            {
                var live = LiveSim;
                var race = _race;
                int wheels = live != null && live.Stops.Count > 0 ? live.Stops[0].Wheels
                           : race != null && race.Sim != null && RaceStops < race.Sim.Stops.Count ? race.Sim.Stops[RaceStops].Wheels : 0;
                RaceStops++;
                _pitThisLap = true;
                if (wheels != 0) _tyreLap = d.CompletedLaps;
            }
            if (IsRace && !lane && _inLane) { StintStart = RaceTime; StintLaps = 0; }
            _inLane = lane;
            Feed(d.TrackPositionMeters, d.TrackPositionPercent, d.CurrentLapTime.TotalSeconds, d.LastLapTime.TotalSeconds, d.CompletedLaps,
                 d.Throttle, d.Brake, d.SpeedKmh, d.Gear ?? "", s);
        }

        /// <summary>One frame of the game (also fed by the tests with a recorded lap).</summary>
        internal static void Feed(double meters, double percent, double lapT, double lastLapSec, int completed,
                                  double throttle, double brake, double speed, string gear, OverlaySettings s)
        {
            Throttle = throttle; Brake = brake; Speed = speed; Gear = gear;

            if (completed != _completed)
            {
                if (_completed >= 0 && completed == _completed + 1) LapDone(lastLapSec, completed, s);
                // The lap may already have restarted when the distance went back to the line
                if (!(_curLast >= 0 && _curLast < 50)) NewLap();
                _completed = completed;
            }
            Lap = completed + 1;

            float gt = 0, gb = 0;
            var r = _ref;
            if (r != null && r.Trace != null)
            {
                var t = r.Trace;
                double dist = meters;
                if (!(dist > 0)) dist = percent * r.Length;
                Dist = dist;
                int i = t.IndexAt(dist);
                gt = t.Throttle[i]; gb = t.Brake[i]; RefSpeed = t.Speed[i]; RefGear = t.Gear[i];
                Record(t, dist, lapT);
                double tr = t.TimeAt(dist);
                Delta = _fromLine && !double.IsNaN(tr) ? lapT - tr : double.NaN;
                Corners(r, dist, lapT, s);
            }
            else { Delta = CornerDelta = RefSpeed = double.NaN; }

            Push((float)Throttle, (float)Brake, gt, gb);
        }

        private static void Push(float thr, float brk, float gt, float gb)
        {
            int h = Head;
            RingT[h] = (float)Now; RingThr[h] = thr; RingBrk[h] = brk; RingGThr[h] = gt; RingGBrk[h] = gb;
            Head = (h + 1) % Cap;
            if (Count < Cap) Count++;
        }

        private static void ResetRace()
        {
            RaceLaps.Clear(); RaceFuel.Clear(); RecentLaps = new double[0];
            RaceStops = 0; StintLaps = 0; StintStart = 0; _tyreLap = 0; _lapStartFuel = -1; _pitThisLap = false;
            LiveSim = null; LiveLap = -1; LivePace = LiveFuel = double.NaN;
        }

        private static void ResetSession()
        {
            ResetRace();
            _completed = -1;
            _curFirst = _curLast = _lastFirst = _lastLast = -1;
            _fromLine = false;
            _sector = -1;
            _pending = null;
            SessionFirst = SessionBest = PlanGap = double.NaN;
            Approach = Bilan = Next = null;
        }

        private static void NewLap()
        {
            var tmp = _last; _last = _cur; _cur = tmp;
            _lastFirst = _curFirst; _lastLast = _curLast;
            _curFirst = _curLast = -1;
            _fromLine = false;
            if (_cur != null) { Array.Clear(_cur.Time, 0, _cur.N); Array.Clear(_cur.Speed, 0, _cur.N); Array.Clear(_cur.Throttle, 0, _cur.N); Array.Clear(_cur.Brake, 0, _cur.N); }
        }

        /// <summary>The lap being driven, resampled at the reference's step as the car goes.</summary>
        private static void Record(TeleTrace reference, double dist, double lapT)
        {
            if (_cur == null || _cur.N != reference.N)
            {
                _cur = new TeleTrace(reference.N) { Step = reference.Step, FirstDist = 0 };
                _last = new TeleTrace(reference.N) { Step = reference.Step, FirstDist = 0 };
                _curFirst = _curLast = _lastFirst = _lastLast = -1;
            }
            var c = _cur;
            int i = (int)(dist / c.Step);
            if (i < 0) return;
            if (i >= c.N) i = c.N - 1;
            if (_curLast < 0)
            {
                if (i > c.N - 50) return;   // still at the end of the previous lap
                _curFirst = _curLast = i;
                _fromLine = i <= 5;
                Set(c, i, lapT);
            }
            else if (i > _curLast)
            {
                if (i - _curLast > 150) _fromLine = false;   // the car jumped (back to the pits…)
                double span = dist - _prevDist;
                for (int k = _curLast + 1; k <= i; k++)
                {
                    double f = span > 0 ? (k * c.Step - _prevDist) / span : 1;
                    f = f < 0 ? 0 : (f > 1 ? 1 : f);
                    Set(c, k, _prevT + (lapT - _prevT) * f);
                }
                _curLast = i;
            }
            else if (i < _curLast - 50)
            {
                // Back at the line before the lap counter moved, or put back in the pits
                NewLap();
                _prevDist = dist; _prevT = lapT;
                Record(reference, dist, lapT);
                return;
            }
            _prevDist = dist; _prevT = lapT;
        }

        private static void Set(TeleTrace c, int k, double time)
        {
            c.Time[k] = (float)time; c.Speed[k] = (float)Speed; c.Throttle[k] = (float)Throttle; c.Brake[k] = (float)Brake;
        }

        private static void Corners(OverlayReference r, double dist, double lapT, OverlaySettings s)
        {
            var sec = r.Sectors;
            if (sec == null || sec.Count == 0) { CornerDelta = double.NaN; return; }
            int n = _sector;
            if (n < 0 || n >= sec.Count || dist < sec[n].Key || dist >= sec[n].Value)
            {
                n = sec.Count - 1;
                for (int k = 0; k < sec.Count; k++) if (dist < sec[k].Value) { n = k; break; }
            }
            if (n != _sector)
            {
                int prev = _sector;
                _sector = n;
                if (prev >= 0 && n == (prev + 1) % sec.Count)
                    foreach (var f in r.Focus) if (f.Sector == prev && !Evaluate(f)) _pending = f;
            }
            if (_pending != null && Evaluate(_pending)) _pending = null;
            CornerNumber = r.Model.Corners[n].Number;
            int ia = (int)(sec[n].Key / _cur.Step);
            CornerDelta = _curFirst >= 0 && _curFirst <= ia
                ? (lapT - _cur.Time[ia]) - (r.Trace.TimeAt(dist) - r.Trace.TimeAt(sec[n].Key))
                : double.NaN;

            // The next corner worked on, and its instruction once close enough
            OverlayFocus approach = null, next = null;
            double approachLeft = 0, nextIn = double.MaxValue, length = r.Length;
            foreach (var f in r.Focus)
            {
                double target = double.IsNaN(f.RefBrake) ? f.Corner.Start : f.RefBrake;
                double left = target - dist;
                if (left < -length / 2) left += length;
                if (left > 0 && left < nextIn) { next = f; nextIn = left; }
                if (left > -40 && left <= s.AnnounceMeters) { approach = f; approachLeft = left; }
            }
            Next = next; NextIn = nextIn;
            Approach = approach; ApproachLeft = approachLeft;
            if (approach != null && approach.SpokenLap != Lap && approachLeft > 40)
            {
                approach.SpokenLap = Lap;
                if (s.Voice && s.Page == (int)OverlayPage.Coaching && !_hidden) Say(approach.Spoken);
            }
        }

        /// <summary>
        /// A passage through a corner worked on, measured as the work plan measures it, once the
        /// lap is recorded past the corner's exit. False: not recorded far enough yet.
        /// </summary>
        private static bool Evaluate(OverlayFocus f)
        {
            int ia = (int)(f.A / _cur.Step);
            int need = Math.Min(_cur.N - 1, (int)(Math.Max(f.B, f.Corner.End + 70) / _cur.Step));
            TeleTrace t = null;
            if (_curFirst >= 0 && _curFirst <= ia && _curLast >= need) t = _cur;
            else if (_lastFirst >= 0 && _lastFirst <= ia && _lastLast >= need - 1) t = _last;
            if (t == null)
            {
                // Still in the corner's exit: wait; the lap was cut (pits, reset): give up
                return !(_curFirst >= 0 && _curFirst <= ia && _curLast < need);
            }
            LivePass[0].Trace = t;
            var passes = CoachingEngine.Passes(LivePass, f.Corner, f.A, Math.Min(f.B, t.DistAt(t.N - 1)));
            if (passes.Count == 0) return true;
            var p = passes[0];
            if (!(p.Sector > 0) || p.Sector > f.RefSector * 1.5) return true;   // a spin, a recording gap
            f.Gaps.Add(p.Sector - f.RefSector);
            f.LastBrakePoint = p.BrakePoint;
            f.LastMinSpeed = p.MinSpeed;
            Bilan = f;
            return true;
        }

        private static void LapDone(double lapSec, int completed, OverlaySettings s)
        {
            if (lapSec > 0)
            {
                if (double.IsNaN(SessionFirst)) SessionFirst = lapSec;
                if (double.IsNaN(SessionBest) || lapSec < SessionBest) SessionBest = lapSec;
            }
            if (IsRace)
            {
                // What the race shows: lap times and fuel burnt, without the laps through the pits
                if (lapSec > 0 && !_pitThisLap) RaceLaps.Add(lapSec);
                if (_lapStartFuel >= 0 && !_pitThisLap && _lapStartFuel - Fuel > 0.05) RaceFuel.Add(_lapStartFuel - Fuel);
                _lapStartFuel = Fuel;
                _pitThisLap = false;
                StintLaps++;
                int k = Math.Min(8, RaceLaps.Count);
                var recent = new double[k];
                for (int i = 0; i < k; i++) recent[i] = RaceLaps[RaceLaps.Count - k + i];
                RecentLaps = recent;
                Replan(completed);
            }
            var race = _race;
            if (IsRace && race != null && race.Plan != null)
            {
                // Race time from the time left (a timed race); unknown: no gap rather than a wrong one
                double elapsed = race.Plan.DurationMin * 60.0 - SessionLeft;
                double planned = race.PlanTimeAt(completed);
                PlanGap = SessionLeft > 0 && elapsed > 0 && !double.IsNaN(planned) ? elapsed - planned : double.NaN;
            }
        }

        /// <summary>The plan for the rest of the race, from where it stands (in the background).</summary>
        private static void Replan(int completed)
        {
            var race = _race;
            double time = RaceTime;
            if (race == null || race.Sim == null || race.Measures == null || race.Sim.Stints.Count == 0 || double.IsNaN(time)) return;
            var state = new StrategyEngine.RaceState
            {
                Time = time, Lap = completed, StopsDone = RaceStops, Fuel = Fuel,
                StintTime = Math.Max(0, time - StintStart), LapsOnTyres = Math.Max(0, completed - _tyreLap),
                TyresUsed = race.TyresUsedAfter(RaceStops),
            };
            double pace = Recent(RaceLaps, 10, 3), fuel = Recent(RaceFuel, 10, 2);
            int stops = RaceStops;
            Task.Run(() =>
            {
                try
                {
                    var sim = StrategyEngine.Resume(race.Plan, race.LiveMeasures(pace, fuel, stops), state);
                    if (sim.Stints.Count == 0) return;
                    LivePace = pace; LiveFuel = fuel; LiveLap = completed;
                    LiveSim = sim;
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Live strategy: " + ex.Message); }
            });
        }

        /// <summary>Median of the last values (a lap behind a slower car or a yellow flag weighs nothing); NaN when too few.</summary>
        private static double Recent(List<double> v, int last, int min)
        {
            int n = Math.Min(last, v.Count);
            if (n < min) return double.NaN;
            var c = v.GetRange(v.Count - n, n);
            c.Sort();
            return c[n / 2];
        }

        // ─── Reference ────────────────────────────────────────────────────────

        private static int _refVersion;

        /// <summary>Builds the reference and the race plan again (circuit, settings or data changed).</summary>
        public static void RequestReference()
        {
            int v = Interlocked.Increment(ref _refVersion);
            var s = OverlaySettings.Current;
            string track = _track, cls = _cls;
            SetStatus("Chargement de la référence…");
            Task.Run(() =>
            {
                try
                {
                    string circuit = s.Circuit, klass = s.ClassLabel;
                    if (string.IsNullOrEmpty(circuit)) Detect(track, cls, out circuit, out klass);
                    if (circuit == null)
                    {
                        if (v != _refVersion) return;
                        _ref = null; _race = null;
                        SetStatus(string.IsNullOrEmpty(track) ? "Circuit inconnu : roulez, ou choisissez-le ci-dessous."
                                                              : "Circuit « " + track + " » introuvable dans vos sessions : choisissez-le ci-dessous.");
                        return;
                    }
                    var r = OverlayReference.Build(circuit, klass, s);
                    OverlayRace race = null;
                    try { race = OverlayRace.Build(circuit, klass, s); }
                    catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Overlay race plan: " + ex.Message); }
                    if (v != _refVersion) return;
                    Circuit = circuit; ClassLabel = klass;
                    _race = race;
                    _ref = r.Trace != null ? r : null;
                    Approach = Bilan = Next = null;
                    _sector = -1;
                    SetStatus(r.Error ?? string.Format("{0} · {1} · {2}{3} · {4} virage{5} travaillé{5}", r.Label, Fmt.LapSec(r.LapSec), circuit,
                        klass != null ? " · " + klass : "", r.Focus.Count, r.Focus.Count > 1 ? "s" : ""));
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn("[TeamPace] Overlay reference: " + ex);
                    if (v == _refVersion) SetStatus("Référence impossible : " + ex.Message);
                }
            });
        }

        private static void SetStatus(string text)
        {
            Status = text;
            var h = StatusChanged;
            if (h != null) h();
        }

        /// <summary>The results-file circuit and category matching what SimHub reports, the most recent session first.</summary>
        private static void Detect(string track, string carClass, out string circuit, out string classLabel)
        {
            circuit = null; classLabel = null;
            var nt = Norm(track);
            if (nt.Length < 3) return;
            string wanted = string.IsNullOrEmpty(carClass) ? null : LmuCategoryStyle.GetShortName(carClass);
            LmuSessionRecord best = null;
            bool bestClass = false;
            foreach (var td in LmuTeamSync.GetDrivers())
                foreach (var s in new DriverView(td, DriverView.Palette[0]).Sessions)
                {
                    if (!Matches(nt, s.TrackVenue) && !Matches(nt, s.TrackCourse) && !Matches(nt, s.TrackDisplayName)) continue;
                    bool sameClass = wanted != null && LmuCategoryStyle.GetShortName(s.CarClass) == wanted;
                    if (best == null || (sameClass && !bestClass) || (sameClass == bestClass && s.SessionDate > best.SessionDate))
                    {
                        best = s; bestClass = sameClass;
                    }
                }
            if (best == null) return;
            circuit = best.TrackDisplayName;
            classLabel = LmuCategoryStyle.GetShortName(best.CarClass);
        }

        private static bool Matches(string norm, string name)
        {
            var n = Norm(name);
            return n.Length >= 3 && (n == norm || n.Contains(norm) || norm.Contains(n));
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var b = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) b.Append(ch);
            return b.ToString();
        }

        // ─── Wheel buttons ────────────────────────────────────────────────────

        private static bool _hidden, _pitHidden;
        public static bool EditMode, PitEditMode;

        public static void NextPage(int dir)
        {
            var s = OverlaySettings.Current;
            for (int k = 1; k <= 4; k++)
            {
                int p = ((s.Page + dir * k) % 4 + 4) % 4;
                if (s.Pages[p]) { s.Page = p; break; }
            }
            OverlaySettings.Save();
        }

        /// <summary>Button B: what it does depends on the page.</summary>
        public static void PageAction()
        {
            var s = OverlaySettings.Current;
            switch ((OverlayPage)s.Page)
            {
                case OverlayPage.Telemetry: s.Ghost = !s.Ghost; break;
                case OverlayPage.Coaching:  s.Voice = !s.Voice; break;
                case OverlayPage.Race:      StopView = (StopView + 1) % 3; return;
                default:
                    s.Reference = s.Reference == "ideal" ? "mine" : s.Reference == "mine" ? "team" : "ideal";
                    OverlaySettings.Save();
                    RequestReference();
                    return;
            }
            OverlaySettings.Save();
        }

        public static void ToggleVisible()
        {
            _hidden = !_hidden;
            Dispatch(Sync);
        }

        public static void TogglePitwall()
        {
            _pitHidden = !_pitHidden;
            Dispatch(Sync);
        }

        // ─── Window ───────────────────────────────────────────────────────────

        private static OverlayWindow _window, _pitWindow;
        private static DispatcherTimer _watch;

        public static void Init(string dataDir)
        {
            OverlaySettings.Load(dataDir);
            OverlaySettings.Changed += () => Dispatch(Sync);
            Dispatch(() =>
            {
                _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _watch.Tick += (a, b) => Sync();
                _watch.Start();
            });
        }

        private static void Dispatch(Action a)
        {
            var app = Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(a);
        }

        /// <summary>Shows the window while driving with the overlay on (or while placing it), hides it otherwise.</summary>
        private static void Sync()
        {
            var s = OverlaySettings.Current;
            try
            {
                Show(ref _window, false, EditMode || (s.Enabled && !_hidden && Live), EditMode, s);
                Show(ref _pitWindow, true, PitEditMode || (s.PitwallEnabled && !_pitHidden && Live), PitEditMode, s);
            }
            catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Overlay window: " + ex.Message); }
        }

        private static void Show(ref OverlayWindow w, bool pitwall, bool show, bool edit, OverlaySettings s)
        {
            if (show)
            {
                if (w == null) w = new OverlayWindow(pitwall);
                w.Apply(s, edit);
                if (!w.IsVisible) w.Show();
            }
            else if (w != null && w.IsVisible) w.Hide();
        }

        public static void SetEditMode(bool on)
        {
            EditMode = on;
            if (on && !Live) Demo = true;
            Sync();
        }

        public static void SetPitEditMode(bool on)
        {
            PitEditMode = on;
            if (on && !Live) Demo = true;
            Sync();
        }

        public static void Shutdown()
        {
            Dispatch(() =>
            {
                if (_window != null) _window.Close();
                if (_pitWindow != null) _pitWindow.Close();
                _window = _pitWindow = null;
            });
        }

        // ─── Voice ────────────────────────────────────────────────────────────

        private static System.Speech.Synthesis.SpeechSynthesizer _synth;
        private static readonly object SpeechLock = new object();

        public static void Say(string text)
        {
            Task.Run(() =>
            {
                try
                {
                    lock (SpeechLock)
                    {
                        if (_synth == null)
                        {
                            _synth = new System.Speech.Synthesis.SpeechSynthesizer();
                            foreach (var v in _synth.GetInstalledVoices())
                                if (v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "fr") { _synth.SelectVoice(v.VoiceInfo.Name); break; }
                            _synth.SetOutputToDefaultAudioDevice();
                        }
                        _synth.SpeakAsyncCancelAll();
                        _synth.SpeakAsync(text);
                    }
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Voice: " + ex.Message); }
            });
        }

        // ─── Preview ──────────────────────────────────────────────────────────

        private static readonly List<OverlayFocus> DemoFocus = new List<OverlayFocus>();

        /// <summary>The corners shown: the reference's, or made-up ones in the preview.</summary>
        public static List<OverlayFocus> Focus
        {
            get
            {
                var r = _ref;
                if (r != null && r.Focus.Count > 0) return r.Focus;
                if (!Demo) return r != null ? r.Focus : DemoFocus;
                if (DemoFocus.Count == 0)
                {
                    DemoFocus.Add(DemoCorner(1, "FREINAGE", "Freine 8 m plus tard", new[] { 0.09, 0.06, 0.04, 0.02, 0.02 }));
                    DemoFocus.Add(DemoCorner(5, "FREINAGE", "Freine 15 m plus tard", new[] { 0.15, 0.12, 0.09, 0.05, 0.04 }));
                    DemoFocus.Add(DemoCorner(9, "SORTIE", "Gaz 10 m plus tôt", new[] { 0.22, 0.21 }));
                }
                return DemoFocus;
            }
        }

        private static OverlayFocus DemoCorner(int n, string phase, string message, double[] gaps)
        {
            var f = new OverlayFocus { Rank = n, Corner = new TrackModel.Corner { Number = n }, Phase = phase, Message = message, RefBrake = 100, RefMinSpeed = 89 };
            f.Gaps.AddRange(gaps);
            f.LastBrakePoint = 86; f.LastMinSpeed = 87;
            return f;
        }

        /// <summary>Made-up inputs for the preview while the game is not running.</summary>
        public static void DemoTick()
        {
            if (Live) { Demo = false; return; }
            Demo = true;
            double t = Now, ph = t % 9.0;
            double thr = ph < 4 ? 100 : ph < 4.3 ? 100 - (ph - 4) / 0.3 * 100 : ph < 5.8 ? 0 : ph < 7 ? (ph - 5.8) / 1.2 * 100 : 100;
            double brk = ph < 4.1 ? 0 : ph < 4.4 ? (ph - 4.1) / 0.3 * 95 : ph < 5.8 ? 95 * (5.8 - ph) / 1.4 : 0;
            double gph = (t + 0.25) % 9.0;
            double gthr = gph < 4 ? 100 : gph < 4.3 ? 100 - (gph - 4) / 0.3 * 100 : gph < 5.5 ? 0 : gph < 6.6 ? (gph - 5.5) / 1.1 * 100 : 100;
            double gbrk = gph < 4.1 ? 0 : gph < 4.4 ? (gph - 4.1) / 0.3 * 92 : gph < 5.5 ? 92 * (5.5 - gph) / 1.1 : 0;
            Throttle = thr; Brake = brk; Speed = 120 + thr; Gear = thr > 50 ? "5" : "3";
            RefSpeed = 124 + gthr; RefGear = gthr > 50 ? 5 : 3;
            Delta = 0.41 + 0.05 * Math.Sin(t / 3); CornerDelta = 0.08; CornerNumber = 5;
            Lap = 14; Fuel = 46; IsRace = true; PlanGap = 4;
            var focus = Focus;
            bool approach = (t % 12) < 6;
            Approach = approach ? focus[1] : null; ApproachLeft = 300 - (t % 6) / 6 * 300;
            Bilan = approach ? null : focus[1];
            Next = focus[2]; NextIn = 820;
            SessionFirst = 107.92; SessionBest = 107.74;
            Push((float)thr, (float)brk, (float)gthr, (float)gbrk);
        }
    }
}
