using System;
using System.Collections.Generic;
using System.Globalization;

namespace User.TeamPace
{
    /// <summary>
    /// The work plan of a circuit: where the time goes, corner by corner, without drowning the
    /// driver in numbers. The reference is the team's ideal lap: the best passage of any driver
    /// in each corner. A driver's usual passage is compared with it; each corner is split into
    /// three phases (braking, apex, exit), and the three biggest losses become priorities, each
    /// explained by the measure that differs most in its worst phase.
    /// </summary>
    internal static class CoachingEngine
    {
        public class Lap
        {
            public DriverView Driver;
            public TeleLapRef Ref;
            public TeleTrace  Trace;
        }

        /// <summary>One lap through one corner.</summary>
        public class Pass
        {
            public Lap Lap;
            public double Sector, Brake, Mid, Exit;   // s
            public double BrakePoint = double.NaN;    // lap distance where braking starts
            public double EntrySpeed, MinSpeed, MinSpeedAt, ExitSpeed;
            public double GasPoint = double.NaN;      // lap distance where the throttle comes back
            public double ApexLateral = double.NaN;   // m, + = right
            public double GLatMax = double.NaN;
            public double AbsShare = double.NaN, TcShare = double.NaN;   // % of the time active
        }

        public class Evidence
        {
            public string Text;
            public int Verdict;   // -1 worse, 0 neutral, +1 same / better
        }

        public class Priority
        {
            public int Rank;
            public TrackModel.Corner Corner;
            public string Against;          // "face à ton meilleur passage", "face à PASCAL", "régularité"
            public double Gain;             // s
            public double[] PhaseLoss = new double[3];
            public string Text;
            public List<Evidence> Evidence = new List<Evidence>();
            public string Basis;
            public Lap Mine, Reference;
        }

        public class CornerRow
        {
            public TrackModel.Corner Corner;
            public double MyBest, MyMedian, Reference, Gain;
            public string ReferenceBy;      // who holds the team's best passage
            public string Phase;
        }

        public class Result
        {
            public string Error;
            public string SubjectName;
            public double SubjectRecord = double.NaN, TeamRecord = double.NaN, Ideal = double.NaN;
            public string TeamRecordBy;
            public int SubjectLaps, TeamLaps;
            public List<Priority> Priorities = new List<Priority>();
            public List<CornerRow> Corners = new List<CornerRow>();
        }

        private static readonly string[] PhaseNames = { "au freinage", "à la corde", "en sortie" };
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <param name="team">clean laps of every driver shown (the subject's included)</param>
        /// <param name="subject">the driver whose plan it is</param>
        public static Result Analyze(TrackModel model, List<Lap> team, DriverView subject)
        {
            var mine = team.FindAll(l => l.Driver == subject);
            var r = new Result { SubjectName = subject.Name, SubjectLaps = mine.Count, TeamLaps = team.Count };
            if (model == null || model.Corners.Count == 0) { r.Error = "Pas encore de tracé pour ce circuit : il faut au moins un tour propre avec la télémétrie."; return r; }
            if (mine.Count < 3) { r.Error = "Il faut au moins 3 tours propres de " + subject.Name + " avec la télémétrie sur ce circuit pour établir son plan de travail."; return r; }

            foreach (var l in mine) if (double.IsNaN(r.SubjectRecord) || l.Ref.LapSec < r.SubjectRecord) r.SubjectRecord = l.Ref.LapSec;
            foreach (var l in team)
                if (double.IsNaN(r.TeamRecord) || l.Ref.LapSec < r.TeamRecord) { r.TeamRecord = l.Ref.LapSec; r.TeamRecordBy = l.Driver.Name; }

            var sectors = model.CornerSectors();
            var candidates = new List<Priority>();
            double ideal = 0;
            bool complete = true;
            for (int i = 0; i < model.Corners.Count; i++)
            {
                var c = model.Corners[i];
                double a = sectors[i].Key, b = sectors[i].Value;
                var all = Typical(Passes(team, c, a, b));
                var my = all.FindAll(p => p.Lap.Driver == subject);
                if (all.Count == 0) { complete = false; continue; }
                all.Sort((x, y) => x.Sector.CompareTo(y.Sector));
                var best = all[0];   // the team's best passage: one corner of the ideal lap
                ideal += best.Sector;
                if (my.Count < 3) continue;
                my.Sort((x, y) => x.Sector.CompareTo(y.Sector));
                var myMedian = my[my.Count / 2];

                var row = new CornerRow
                {
                    Corner = c, MyBest = my[0].Sector, MyMedian = myMedian.Sector, Reference = best.Sector,
                    ReferenceBy = best.Lap.Driver.Name, Gain = myMedian.Sector - best.Sector,
                };
                if (row.Gain > 0.03)
                {
                    var pr = Explain(c, myMedian, best, subject);
                    row.Phase = PhaseNames[Worst(pr.PhaseLoss)];
                    candidates.Add(pr);
                }
                r.Corners.Add(row);

                // Regularity: a corner whose time varies a lot from lap to lap
                double q1 = my[my.Count / 4].Sector, q3 = my[(my.Count * 3) / 4].Sector;
                if (q3 - q1 > 0.12) candidates.Add(Regularity(c, my, q3 - q1));
            }
            if (complete && ideal > 0) r.Ideal = ideal;

            // One card per corner, the biggest gains first
            candidates.Sort((x, y) => y.Gain.CompareTo(x.Gain));
            var used = new HashSet<int>();
            foreach (var p in candidates)
            {
                if (r.Priorities.Count == 3) break;
                if (!used.Add(p.Corner.Number)) continue;
                p.Rank = r.Priorities.Count + 1;
                r.Priorities.Add(p);
            }
            return r;
        }

        // ─── Measures ─────────────────────────────────────────────────────────

        internal static List<Pass> Passes(List<Lap> laps, TrackModel.Corner c, double a, double b)
        {
            var list = new List<Pass>();
            foreach (var l in laps)
            {
                var t = l.Trace;
                if (t.FirstDist > a || t.DistAt(t.N - 1) < b) continue;
                double ta = t.TimeAt(a), tb = t.TimeAt(b);
                double s = Math.Max(a, Math.Min(b, c.Start)), e = Math.Max(s, Math.Min(b, c.End));
                double ts = t.TimeAt(s), te = t.TimeAt(e);
                if (double.IsNaN(ta) || double.IsNaN(tb)) continue;
                var p = new Pass { Lap = l, Sector = tb - ta, Brake = ts - ta, Mid = te - ts, Exit = tb - te };

                int ia = t.IndexAt(a), iApex = t.IndexAt(c.Apex), ib = t.IndexAt(b);
                for (int i = ia; i <= iApex; i++) if (t.Brake[i] >= 10) { p.BrakePoint = t.DistAt(i); break; }
                p.EntrySpeed = t.Speed[t.IndexAt(c.Start)];
                int lo = t.IndexAt(c.Start - 30), hi = t.IndexAt(c.End + 30), imin = lo;
                for (int i = lo; i <= hi; i++) if (t.Speed[i] < t.Speed[imin]) imin = i;
                p.MinSpeed = t.Speed[imin]; p.MinSpeedAt = t.DistAt(imin);
                for (int i = imin; i <= ib; i++)
                {
                    if (t.Throttle[i] < 20) continue;
                    double sum = 0; int n = 0;
                    for (int k = i; k < Math.Min(t.N, i + 10); k++) { sum += t.Throttle[k]; n++; }
                    if (n > 0 && sum / n >= 20) { p.GasPoint = t.DistAt(i); break; }
                }
                p.ExitSpeed = t.Speed[t.IndexAt(Math.Min(b, c.End + 60))];
                if (t.Lateral != null) p.ApexLateral = t.Lateral[iApex];
                if (t.GLat != null)
                {
                    double g = 0;
                    for (int i = t.IndexAt(c.Start); i <= t.IndexAt(c.End); i++) g = Math.Max(g, Math.Abs(t.GLat[i]));
                    p.GLatMax = g;
                }
                if (t.Abs != null)
                {
                    p.AbsShare = Share(t.Abs, ia, iApex);
                    p.TcShare = Share(t.Tc, iApex, ib);
                }
                list.Add(p);
            }
            return list;
        }

        /// <summary>Drops passes far from the usual time (a spin, a recording glitch).</summary>
        internal static List<Pass> Typical(List<Pass> passes)
        {
            if (passes.Count < 3) return passes;
            var times = new List<double>();
            foreach (var p in passes) times.Add(p.Sector);
            times.Sort();
            double median = times[times.Count / 2];
            return passes.FindAll(p => p.Sector > median * 0.9 && p.Sector < median * 1.25);
        }

        private static double Share(byte[] v, int a, int b)
        {
            if (b <= a) return 0;
            double s = 0;
            for (int i = a; i <= b; i++) s += v[i];
            return s / (b - a + 1);
        }

        internal static int Worst(double[] loss)
        {
            int w = 0;
            for (int i = 1; i < 3; i++) if (loss[i] > loss[w]) w = i;
            return w;
        }

        // ─── Explanations ─────────────────────────────────────────────────────

        private static Priority Explain(TrackModel.Corner c, Pass me, Pass reference, DriverView subject)
        {
            bool self = reference.Lap.Driver == subject;
            string against = self ? "face au tour idéal : ton meilleur passage" : "face au tour idéal : passage de " + reference.Lap.Driver.Name;
            var p = new Priority { Corner = c, Against = against, Gain = me.Sector - reference.Sector, Mine = me.Lap, Reference = reference.Lap };
            p.PhaseLoss[0] = me.Brake - reference.Brake;
            p.PhaseLoss[1] = me.Mid - reference.Mid;
            p.PhaseLoss[2] = me.Exit - reference.Exit;
            int phase = Worst(p.PhaseLoss);
            string who = self ? null : reference.Lap.Driver.Name;

            // Every measure, then the sentence from the ones of the worst phase
            double brake = Diff(reference.BrakePoint, me.BrakePoint);               // + : reference brakes later
            double entry = reference.EntrySpeed - me.EntrySpeed;                     // + : reference faster
            double vmin  = reference.MinSpeed - me.MinSpeed;
            double gas   = Diff(me.GasPoint, reference.GasPoint);                    // + : reference earlier
            double exit  = reference.ExitSpeed - me.ExitSpeed;
            double line  = Diff(me.ApexLateral, reference.ApexLateral);
            double glat  = Diff(reference.GLatMax, me.GLatMax);
            double abs   = Diff(me.AbsShare, reference.AbsShare);
            double tc    = Diff(me.TcShare, reference.TcShare);

            var clauses = new List<string>();
            if (phase == 0)
            {
                if (brake >= 4) clauses.Add(self ? string.Format(Inv, "tu freines {0:0} m plus tôt que dans ton meilleur passage", brake)
                                                 : string.Format(Inv, "{0} freine {1:0} m plus tard", who, brake));
                if (entry >= 2) clauses.Add(self ? string.Format(Inv, "tu entres à {0:0} km/h au lieu de {1:0}", me.EntrySpeed, reference.EntrySpeed)
                                                 : string.Format(Inv, "entre {0:0} km/h plus vite", entry));
                if (abs >= 8) clauses.Add(string.Format(Inv, "ton ABS travaille {0:0} % du freinage contre {1:0} %", me.AbsShare, reference.AbsShare));
            }
            else if (phase == 1)
            {
                if (vmin >= 2) clauses.Add(self ? string.Format(Inv, "tu passes la corde à {0:0} km/h au lieu de {1:0}", me.MinSpeed, reference.MinSpeed)
                                                : string.Format(Inv, "{0} garde {1:0} km/h de plus à la corde", who, vmin));
                if (Math.Abs(line) >= 0.8) clauses.Add(string.Format(Inv, "ta trajectoire passe {0:0.0} m plus {1} à la corde", Math.Abs(line), line > 0 ? "à droite" : "à gauche"));
                if (glat >= 0.06) clauses.Add(string.Format(Inv, "{0} {1:0.00} g de latéral contre {2:0.00}", self ? "ton meilleur passage tient" : who + " tient", reference.GLatMax, me.GLatMax));
            }
            else
            {
                if (gas >= 5) clauses.Add(self ? string.Format(Inv, "tu remets les gaz {0:0} m plus tard que dans ton meilleur passage", gas)
                                               : string.Format(Inv, "{0} remet les gaz {1:0} m plus tôt", who, gas));
                if (exit >= 2) clauses.Add(self ? string.Format(Inv, "tu sors à {0:0} km/h au lieu de {1:0}", me.ExitSpeed, reference.ExitSpeed)
                                                : string.Format(Inv, "sort à {0:0} km/h contre {1:0}", reference.ExitSpeed, me.ExitSpeed));
                if (tc >= 8) clauses.Add(string.Format(Inv, "ton antipatinage coupe {0:0} % de la sortie contre {1:0} %", me.TcShare, reference.TcShare));
            }
            var head = string.Format(Inv, "La perte est {0} ({1:0.00} s)", PhaseNames[phase], p.PhaseLoss[phase]);
            p.Text = clauses.Count > 0 ? head + " : " + string.Join(", ", clauses) + "." : head + ", sans différence nette sur une seule mesure : compare les courbes.";

            // Evidence chips: every measure, coloured
            Add(p, brake, 4, v => string.Format(Inv, "tu freines {0}", Signed(v, "m", "plus tôt", "plus tard")), true);
            Add(p, vmin, 2, v => string.Format(Inv, "vitesse mini {0:0} / {1:0} km/h", me.MinSpeed, reference.MinSpeed), true);
            Add(p, gas, 5, v => string.Format(Inv, "tu remets les gaz {0}", Signed(v, "m", "plus tard", "plus tôt")), true);
            Add(p, exit, 2, v => string.Format(Inv, "sortie {0:0} / {1:0} km/h", me.ExitSpeed, reference.ExitSpeed), true);
            Add(p, glat, 0.06, v => string.Format(Inv, "G latéral max {0:0.00} / {1:0.00}", me.GLatMax, reference.GLatMax), true);
            Add(p, abs, 8, v => string.Format(Inv, "ABS {0:0} % / {1:0} %", me.AbsShare, reference.AbsShare), true);
            Add(p, tc, 8, v => string.Format(Inv, "antipatinage {0:0} % / {1:0} %", me.TcShare, reference.TcShare), true);

            p.Basis = self
                ? string.Format("Ton meilleur passage : {0} · ton passage habituel : {1}", LapName(reference.Lap), LapName(me.Lap))
                : string.Format("Meilleur passage de l'équipe : {0}, {1} · ton passage habituel : {2}", who, LapName(reference.Lap), LapName(me.Lap));
            return p;
        }

        private static Priority Regularity(TrackModel.Corner c, List<Pass> my, double spread)
        {
            var p = new Priority { Corner = c, Against = "régularité", Gain = spread / 2, Mine = my[my.Count / 2].Lap, Reference = my[0].Lap };
            double minB = double.MaxValue, maxB = double.MinValue;
            int absLaps = 0;
            foreach (var x in my)
            {
                if (!double.IsNaN(x.BrakePoint)) { minB = Math.Min(minB, x.BrakePoint); maxB = Math.Max(maxB, x.BrakePoint); }
                if (!double.IsNaN(x.AbsShare) && x.AbsShare >= 10) absLaps++;
            }
            p.Text = string.Format(Inv, "Ton passage varie de {0:0.00} s d'un tour à l'autre", spread);
            if (maxB > minB && maxB - minB >= 6)
            {
                p.Text += string.Format(Inv, " : ton point de freinage bouge de {0:0} m.", maxB - minB);
                p.Evidence.Add(new Evidence { Text = string.Format(Inv, "freinage : {0:0} m d'écart d'un tour à l'autre", maxB - minB), Verdict = -1 });
                p.PhaseLoss[0] = spread;
            }
            else
            {
                p.Text += " : vise le même point de freinage et la même corde à chaque tour.";
                p.PhaseLoss[1] = spread;
            }
            if (absLaps > 0) p.Evidence.Add(new Evidence { Text = string.Format("ABS marqué {0} tour(s) sur {1}", absLaps, my.Count), Verdict = -1 });
            p.Basis = string.Format("Mesuré sur tes {0} tours propres", my.Count);
            return p;
        }

        private static void Add(Priority p, double diff, double threshold, Func<double, string> text, bool worseWhenPositive)
        {
            if (double.IsNaN(diff)) return;
            int verdict = Math.Abs(diff) < threshold ? 1 : (diff > 0) == worseWhenPositive ? -1 : 0;
            p.Evidence.Add(new Evidence { Text = text(diff), Verdict = verdict });
        }

        private static string Signed(double v, string unit, string positive, string negative)
        {
            if (Math.Abs(v) < 0.5) return "au même endroit";
            return string.Format(Inv, "{0:0} {1} {2}", Math.Abs(v), unit, v > 0 ? positive : negative);
        }

        private static double Diff(double a, double b) { return double.IsNaN(a) || double.IsNaN(b) ? double.NaN : a - b; }

        public static string LapName(Lap l)
        {
            var d = l.Ref.Date;
            return string.Format("T{0} du {1:dd/MM}", l.Ref.LapNum, d);
        }
    }
}
