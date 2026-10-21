using System;
using System.Collections.Generic;
using System.Globalization;

namespace User.TeamPace
{
    /// <summary>A corner worked on while driving: what to do, where the reference brakes, and how each passage went.</summary>
    internal class OverlayFocus
    {
        public int Rank;
        public TrackModel.Corner Corner;
        public int Sector;               // index in the track model's corner sectors
        public double A, B;             // sector bounds, lap distance
        public string Phase;            // "FREINAGE", "CORDE", "SORTIE", "RÉGULARITÉ"
        public string Message;          // "Freine 15 m plus tard"
        public string Spoken;           // the same, written to be read aloud
        public double RefSector, RefBrake = double.NaN, RefMinSpeed;

        // This session, filled while driving
        public readonly List<double> Gaps = new List<double>();   // time lost on the reference at each passage
        public double LastBrakePoint = double.NaN, LastMinSpeed = double.NaN;
        public int SpokenLap = -1;

        public double LastGap { get { return Gaps.Count > 0 ? Gaps[Gaps.Count - 1] : double.NaN; } }
        public double FirstGap { get { return Gaps.Count > 0 ? Gaps[0] : double.NaN; } }
    }

    /// <summary>The lap the overlay compares with, and the corners of the work plan.</summary>
    internal class OverlayReference
    {
        public string Circuit, ClassLabel, Kind, Label;
        public TeleTrace Trace;
        public double LapSec = double.NaN;
        public TrackModel Model;
        public List<KeyValuePair<double, double>> Sectors;
        public List<OverlayFocus> Focus = new List<OverlayFocus>();
        public string Error;

        public double Length { get { return Model != null ? Model.Length : Trace != null ? Trace.DistAt(Trace.N - 1) : 0; } }

        private static readonly CultureInfo Inv = CultureInfo.GetCultureInfo("fr-FR");   // instructions are read in French

        /// <summary>Builds the reference of a circuit and category (slow: reads the team's telemetry; run it in the background).</summary>
        public static OverlayReference Build(string circuit, string classLabel, OverlaySettings s)
        {
            var r = new OverlayReference { Circuit = circuit, ClassLabel = classLabel, Kind = s.Reference, Label = OverlaySettings.ReferenceName(s.Reference, s.ReferenceDriver) };
            var drivers = new List<DriverView>();
            int k = 0;
            foreach (var td in LmuTeamSync.GetDrivers()) drivers.Add(new DriverView(td, DriverView.Palette[k++ % DriverView.Palette.Length]));
            var me = drivers[0];

            var team = new List<CoachingEngine.Lap>();
            var known = new List<TeleLapRef>();
            foreach (var d in drivers)
            {
                var all = TelemetryService.GetLaps(d, circuit);
                known.AddRange(all);
                team.AddRange(Laps(d, all, classLabel));
            }
            r.Model = TelemetryService.TrackModelFor(circuit, known);
            if (r.Model != null)
            {
                var model = r.Model;
                team = team.FindAll(l => l.Trace.FirstDist < 10 && Math.Abs(l.Trace.DistAt(l.Trace.N - 1) - model.Length) < 40
                                         && Math.Abs(l.Trace.Time[l.Trace.N - 1] - l.Ref.LapSec) < 0.6);
                r.Sectors = model.CornerSectors();
            }
            if (team.Count == 0)
            {
                r.Error = "Pas de télémétrie de l'équipe ici : roulez un tour propre puis « Tout mettre à jour ».";
                return r;
            }

            // The reference lap
            CoachingEngine.Lap best = null;
            Func<CoachingEngine.Lap, bool> keep = l => true;
            if (r.Kind == "mine") keep = l => l.Driver == me;
            else if (r.Kind == "driver") keep = l => l.Driver.Name == s.ReferenceDriver;
            foreach (var l in team) if (keep(l) && (best == null || l.Ref.LapSec < best.Ref.LapSec)) best = l;
            if (r.Kind == "ideal" && r.Model != null && r.Model.Corners.Count > 0)
            {
                r.Trace = Stitch(r.Model, r.Sectors, team);
                if (r.Trace != null) r.LapSec = r.Trace.Time[r.Trace.N - 1];
            }
            if (r.Trace == null)
            {
                if (best == null)
                {
                    r.Error = "Aucun tour propre avec télémétrie pour « " + r.Label + " » ici.";
                    return r;
                }
                r.Trace = best.Trace;
                r.LapSec = best.Ref.LapSec;
                if (r.Kind == "ideal") r.Label = "Meilleur tour de l'équipe";   // no track model yet: no ideal lap
                r.Label += " (" + best.Driver.Name + ")";
            }

            // The corners to work on: the work plan's priorities, measured against this reference
            if (r.Model != null && r.Model.Corners.Count > 0)
            {
                try
                {
                    var plan = CoachingEngine.Analyze(r.Model, team, me);
                    foreach (var p in plan.Priorities)
                    {
                        if (r.Focus.Count >= s.CoachCorners) break;
                        var f = MakeFocus(r, p, team, me);
                        if (f != null) r.Focus.Add(f);
                    }
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Overlay work plan: " + ex.Message); }
            }
            return r;
        }

        /// <summary>Clean laps tied to a results file: the 40 most recent and the 10 fastest (as in the work plan).</summary>
        private static List<CoachingEngine.Lap> Laps(DriverView d, List<TeleLapRef> all, string classLabel)
        {
            var refs = all.FindAll(l => l.IsClean && l.File.Version >= 2 && l.Session != null && l.XmlLap != null &&
                                        (classLabel == null || LmuCategoryStyle.GetShortName(l.Session.CarClass) == classLabel));
            var fastest = new List<TeleLapRef>(refs);
            fastest.Sort((a, b) => a.LapSec.CompareTo(b.LapSec));
            refs.Sort((a, b) => b.Date.CompareTo(a.Date));
            if (refs.Count > 40) refs.RemoveRange(40, refs.Count - 40);
            for (int i = 0; i < Math.Min(10, fastest.Count); i++) if (!refs.Contains(fastest[i])) refs.Add(fastest[i]);
            var laps = new List<CoachingEngine.Lap>();
            foreach (var r in refs)
            {
                try { laps.Add(new CoachingEngine.Lap { Driver = d, Ref = r, Trace = TelemetryService.LoadTrace(r) }); }
                catch { }
            }
            return laps;
        }

        /// <summary>
        /// The team's ideal lap as one trace: in each corner sector, the inputs and the time of the
        /// best passage, end to end. Its time is the sum of the best sector times.
        /// </summary>
        private static TeleTrace Stitch(TrackModel model, List<KeyValuePair<double, double>> sectors, List<CoachingEngine.Lap> team)
        {
            double step = TrackModel.Step;
            int n = (int)Math.Floor(model.Length / step) + 1;
            var t = new TeleTrace(n) { Step = (float)step, FirstDist = 0 };
            double offset = 0;
            for (int s = 0; s < sectors.Count; s++)
            {
                double a = sectors[s].Key, b = sectors[s].Value;
                var passes = CoachingEngine.Typical(CoachingEngine.Passes(team, model.Corners[s], a, b));
                if (passes.Count == 0) return null;
                CoachingEngine.Pass best = passes[0];
                foreach (var p in passes) if (p.Sector < best.Sector) best = p;
                var L = best.Lap.Trace;
                double ta = L.TimeAt(a);
                int i0 = (int)Math.Ceiling(a / step), i1 = s == sectors.Count - 1 ? n - 1 : (int)Math.Floor(b / step);
                for (int i = Math.Max(0, i0); i <= Math.Min(n - 1, i1); i++)
                {
                    double d = i * step;
                    double tl = L.TimeAt(Math.Min(d, L.DistAt(L.N - 1)));
                    t.Time[i] = (float)(offset + (double.IsNaN(tl) ? best.Sector : tl - ta));
                    int j = L.IndexAt(d);
                    t.Speed[i] = L.Speed[j]; t.Throttle[i] = L.Throttle[j]; t.Brake[i] = L.Brake[j];
                    t.Steer[i] = L.Steer[j]; t.Rpm[i] = L.Rpm[j]; t.Gear[i] = L.Gear[j];
                    t.X[i] = L.X[j]; t.Y[i] = L.Y[j];
                    if (L.Lateral != null) { t.Lateral[i] = L.Lateral[j]; t.Edge[i] = L.Edge[j]; }
                }
                offset += best.Sector;
            }
            return t;
        }

        /// <summary>One corner of the work plan, with the instruction measured against the reference lap.</summary>
        private static OverlayFocus MakeFocus(OverlayReference r, CoachingEngine.Priority p, List<CoachingEngine.Lap> team, DriverView me)
        {
            int s = r.Model.Corners.IndexOf(p.Corner);
            if (s < 0) return null;
            double a = r.Sectors[s].Key, b = r.Sectors[s].Value;
            var refPasses = CoachingEngine.Passes(new List<CoachingEngine.Lap> { new CoachingEngine.Lap { Trace = r.Trace } }, p.Corner, a, b);
            var mine = CoachingEngine.Typical(CoachingEngine.Passes(team.FindAll(l => l.Driver == me), p.Corner, a, b));
            if (refPasses.Count == 0 || mine.Count == 0) return null;
            var rp = refPasses[0];
            mine.Sort((x, y) => x.Sector.CompareTo(y.Sector));
            var mp = mine[mine.Count / 2];   // the usual passage

            var f = new OverlayFocus
            {
                Rank = p.Rank, Corner = p.Corner, Sector = s, A = a, B = b,
                RefSector = rp.Sector, RefBrake = rp.BrakePoint, RefMinSpeed = rp.MinSpeed,
            };
            double brake = Diff(rp.BrakePoint, mp.BrakePoint);   // + : the reference brakes later
            double entry = rp.EntrySpeed - mp.EntrySpeed, vmin = rp.MinSpeed - mp.MinSpeed;
            double gas = Diff(mp.GasPoint, rp.GasPoint), exit = rp.ExitSpeed - mp.ExitSpeed;
            double line = Diff(rp.ApexLateral, mp.ApexLateral);   // + : the reference is further right
            if (p.Against == "régularité")
            {
                f.Phase = "RÉGULARITÉ";
                f.Message = "Même freinage à chaque tour";
            }
            else
            {
                double[] loss = { mp.Brake - rp.Brake, mp.Mid - rp.Mid, mp.Exit - rp.Exit };
                int phase = CoachingEngine.Worst(loss);
                f.Phase = phase == 0 ? "FREINAGE" : phase == 1 ? "CORDE" : "SORTIE";
                // The clearest difference, those of the costly phase first: one instruction, never a vague one
                var said = new List<KeyValuePair<double, string>>();
                Action<double, double, int, string> add = (v, unit, ph, text) =>
                {
                    if (double.IsNaN(v) || Math.Abs(v) < unit) return;
                    said.Add(new KeyValuePair<double, string>(Math.Abs(v) / unit * (ph == phase ? 2 : 1), text));
                };
                add(brake, 4, 0, string.Format(Inv, "Freine {0:0} m plus {1}", Math.Abs(brake), brake > 0 ? "tard" : "tôt"));
                if (entry > 0) add(entry, 2, 0, string.Format(Inv, "Entre {0:0} km/h plus vite", entry));
                if (vmin > 0) add(vmin, 2, 1, string.Format(Inv, "{0:0} km/h de plus à la corde", vmin));
                add(line, 0.8, 1, string.Format(Inv, "Corde {0:0.0} m plus à {1}", Math.Abs(line), line > 0 ? "droite" : "gauche"));
                if (gas > 0) add(gas, 5, 2, string.Format(Inv, "Gaz {0:0} m plus tôt", gas));
                if (exit > 0) add(exit, 2, 2, string.Format(Inv, "Sors {0:0} km/h plus vite", exit));
                said.Sort((x, y) => y.Key.CompareTo(x.Key));
                f.Message = said.Count > 0 ? said[0].Value : "Suis le fantôme";
                // What the driver controls comes first: the braking point, the throttle point
                if (phase == 0 && Math.Abs(brake) >= 4) f.Message = string.Format(Inv, "Freine {0:0} m plus {1}", Math.Abs(brake), brake > 0 ? "tard" : "tôt");
                else if (phase == 2 && gas >= 5) f.Message = string.Format(Inv, "Gaz {0:0} m plus tôt", gas);
            }
            f.Spoken = "Virage " + p.Corner.Number + " : " + f.Message.Replace(" km/h", " kilomètres heure").Replace(" m ", " mètres ");
            return f;
        }

        private static double Diff(double a, double b) { return double.IsNaN(a) || double.IsNaN(b) ? double.NaN : a - b; }
    }
}
