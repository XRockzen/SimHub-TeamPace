using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace User.TeamPace
{
    /// <summary>
    /// A race as the whole car lived it: every lap with the driver who drove it, the result and
    /// every car of the race. Written by whichever crew member imports it, in the local data
    /// folder and in the team folder (<c>races\</c>), so each crew member sees the race with his
    /// own laps even when his own TeamPace has not imported it (or an older version took the
    /// whole car for his), and the race analysis stays available to the team.
    /// </summary>
    public class TeamRace
    {
        public int Version = 1;
        public LmuSessionRecord Session;                          // the car's result; TotalLaps = every lap
        public List<LmuLapRecord> Laps = new List<LmuLapRecord>(); // every lap of the car
        public List<string> Owners = new List<string>();           // who drove each of them
        public RaceField Field;
        public double LogFrom = double.MaxValue;   // session time the writer's logs start from (earlier = better informed)
        public string Author;
        public DateTime UpdatedUtc;

        public bool IsShared { get { return Owners.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1; } }

        public List<string> Crew { get { return Owners.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); } }

        public int LapsOf(string driver) { return Owners.Count(o => SameDriver(o, driver)); }

        public static bool SameDriver(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class TeamRaceStore
    {
        public const string Prefix = "TR|";   // file id of a race rebuilt for a crew member
        private const string Folder = "races";
        private static readonly object Sync = new object();
        private static List<TeamRace> _cache;
        private static DateTime _cacheAt;
        private static readonly Dictionary<string, KeyValuePair<DateTime, TeamRace>> Files =
            new Dictionary<string, KeyValuePair<DateTime, TeamRace>>(StringComparer.OrdinalIgnoreCase);

        private static string LocalDir
        {
            get { return LmuSessionStore.OwnDbPath == null ? null : Path.Combine(Path.GetDirectoryName(LmuSessionStore.OwnDbPath), Folder); }
        }

        private static string TeamDir
        {
            get { return LmuTeamSync.IsConfigured ? Path.Combine(LmuSessionStore.TeamFolder, Folder) : null; }
        }

        // ─── Writing ──────────────────────────────────────────────────────────

        public static void Save(TeamRace race)
        {
            // A solo race is already shared by its driver's database (with every car of the race)
            if (race == null || race.Session == null || race.Laps.Count == 0 || !race.IsShared) return;
            race.Author = LmuSessionStore.PlayerName;
            race.UpdatedUtc = DateTime.UtcNow;
            var s = race.Session;
            var name = string.Format("{0:yyyyMMdd_HHmm}_{4}_{1}_{2}_{3}.json", s.BlockStartUtc,
                LmuTeamSync.Sanitize(Short(s.TrackVenue, 16)), s.CarNumber, LmuTeamSync.Sanitize(Short(LmuSessionStore.PlayerName, 16)), s.SessionType);
            string json;
            try { json = Newtonsoft.Json.JsonConvert.SerializeObject(race); }
            catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Team race: " + ex.Message); return; }
            foreach (var dir in new[] { LocalDir, TeamDir })
            {
                if (dir == null) continue;
                try
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, name);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Team race " + name + ": " + ex.Message); }
            }
            lock (Sync) _cache = null;
        }

        private static string Short(string s, int n)
        {
            s = s ?? "";
            return s.Length > n ? s.Substring(0, n) : s;
        }

        // ─── Reading ──────────────────────────────────────────────────────────

        /// <summary>Every race known locally or in the team folder, one per car and race (the most complete).</summary>
        public static List<TeamRace> All()
        {
            lock (Sync)
            {
                if (_cache != null && (DateTime.UtcNow - _cacheAt).TotalSeconds < 60) return _cache;
                var list = new List<TeamRace>();
                bool changed = _cache == null;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in new[] { LocalDir, TeamDir })
                {
                    if (dir == null || !Directory.Exists(dir)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.json"); } catch { continue; }
                    foreach (var f in files)
                    {
                        seen.Add(f);
                        TeamRace r = null;
                        DateTime stamp;
                        try { stamp = File.GetLastWriteTimeUtc(f); } catch { continue; }
                        KeyValuePair<DateTime, TeamRace> known;
                        if (Files.TryGetValue(f, out known) && known.Key == stamp) r = known.Value;   // unchanged: no parsing
                        else
                        {
                            try { r = Newtonsoft.Json.JsonConvert.DeserializeObject<TeamRace>(File.ReadAllText(f)); } catch { }
                            Files[f] = new KeyValuePair<DateTime, TeamRace>(stamp, r);
                            changed = true;
                        }
                        if (r == null || r.Session == null || r.Laps.Count != r.Owners.Count) continue;
                        // One per session: the writer whose logs start earliest knew who drove each lap
                        var same = list.FindIndex(x => SameRace(x.Session, r.Session));
                        if (same < 0) list.Add(r);
                        else if (r.LogFrom < list[same].LogFrom - 1 || (Math.Abs(r.LogFrom - list[same].LogFrom) <= 1 && r.Laps.Count > list[same].Laps.Count)) list[same] = r;
                    }
                }
                foreach (var gone in Files.Keys.Where(k => !seen.Contains(k)).ToList()) { Files.Remove(gone); changed = true; }
                if (changed) LmuSessionStore.Touch();   // drivers' sessions are rebuilt from these
                _cache = list;
                _cacheAt = DateTime.UtcNow;
                return list;
            }
        }

        public static void Invalidate() { lock (Sync) _cache = null; }

        /// <summary>The same car in the same race: same circuit and car, started together.</summary>
        public static bool SameRace(LmuSessionRecord a, LmuSessionRecord b)
        {
            if (a == null || b == null || a.IsRace != b.IsRace || a.IsQuali != b.IsQuali) return false;
            if (!string.Equals(a.TrackDisplayName, b.TrackDisplayName, StringComparison.OrdinalIgnoreCase)) return false;
            if (a.BlockStartUtc == DateTime.MinValue || b.BlockStartUtc == DateTime.MinValue) return false;
            var gap = (a.BlockStartUtc - b.BlockStartUtc).Duration();
            if (gap <= TimeSpan.FromMinutes(3)) return true;
            if (!a.IsRace) return false;
            // An older TeamPace dated a joined race at the join: same car, within the race
            bool sameCar = a.CarNumber == b.CarNumber && a.CarNumber > 0 &&
                           string.Equals((a.TeamName ?? "").Trim(), (b.TeamName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            double window = Math.Max(a.RaceTimeMinutes, b.RaceTimeMinutes) + 30;
            return sameCar && gap.TotalMinutes <= window;
        }

        /// <summary>
        /// A driver's sessions completed with the team races he drove in: a race missing from his
        /// database, or stored as the whole car by an older TeamPace, is rebuilt with his laps.
        /// </summary>
        public static List<LmuSessionRecord> Complete(string driver, List<LmuSessionRecord> sessions)
        {
            List<TeamRace> races;
            try { races = All(); } catch { return sessions; }
            foreach (var race in races)
            {
                if (!race.IsShared) continue;
                int mine = race.LapsOf(driver);
                if (mine == 0) continue;
                int i = sessions.FindIndex(s => SameRace(s, race.Session));
                // Kept when his own TeamPace split it the same way; otherwise the best-informed
                // version wins (a driver who joined late does not see who drove before)
                if (i >= 0 && !string.IsNullOrEmpty(sessions[i].TeamDrivers) && sessions[i].TotalLaps == mine) continue;
                var rebuilt = Rebuild(race, driver);
                if (i >= 0) sessions[i] = rebuilt; else sessions.Add(rebuilt);
            }
            return sessions;
        }

        private static LmuSessionRecord Rebuild(TeamRace race, string driver)
        {
            var src = race.Session;
            var s = (LmuSessionRecord)Newtonsoft.Json.JsonConvert.DeserializeObject(Newtonsoft.Json.JsonConvert.SerializeObject(src), typeof(LmuSessionRecord));
            s.FileId = string.Format("{0}{1}|{2}|{3}", Prefix, src.BlockStartUtc.Ticks, src.CarNumber, driver);
            s.PlayerName = driver;
            s.CarLaps = race.Laps.Count;
            var own = OwnLaps(race, driver, s.FileId);
            s.TotalLaps = own.Count;
            long best = 0;
            foreach (var l in own) if (l.IsValid && l.LapTimeTicks > 0 && (best == 0 || l.LapTimeTicks < best)) best = l.LapTimeTicks;
            s.BestLapTime = TimeSpan.FromTicks(best);
            s.TeamDrivers = string.Join(" · ", race.Crew.Select(c => c + " " + race.LapsOf(c)));
            s.IncidentCount = own.Sum(l => l.Contacts);
            s.PenaltyCount = own.Count(l => !string.IsNullOrEmpty(l.Penalty));
            s.Field = null;
            if (s.SessionDate.Kind == DateTimeKind.Utc) s.SessionDate = s.SessionDate.ToLocalTime();
            return s;
        }

        private static List<LmuLapRecord> OwnLaps(TeamRace race, string driver, string fileId)
        {
            var picked = new List<LmuLapRecord>();
            for (int i = 0; i < race.Laps.Count; i++)
                if (TeamRace.SameDriver(race.Owners[i], driver)) picked.Add(race.Laps[i]);
            // Copies: the analysis merges live data into the laps it is given
            var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<LmuLapRecord>>(Newtonsoft.Json.JsonConvert.SerializeObject(picked));
            foreach (var l in list) l.SessionId = fileId;
            return list;
        }

        /// <summary>Laps of a rebuilt race (file id starting with <see cref="Prefix"/>).</summary>
        public static List<LmuLapRecord> LapsFor(string fileId)
        {
            var race = RaceOf(fileId);
            if (race == null) return new List<LmuLapRecord>();
            var driver = fileId.Substring(fileId.LastIndexOf('|') + 1);
            return OwnLaps(race, driver, fileId);
        }

        private static TeamRace RaceOf(string fileId)
        {
            if (fileId == null || !fileId.StartsWith(Prefix)) return null;
            var parts = fileId.Split('|');
            long ticks; int car;
            if (parts.Length < 4 || !long.TryParse(parts[1], out ticks) || !int.TryParse(parts[2], out car)) return null;
            var start = new DateTime(ticks, DateTimeKind.Utc);
            foreach (var r in All())
                if (r.Session.CarNumber == car && (r.Session.BlockStartUtc - start).Duration() <= TimeSpan.FromMinutes(3)) return r;
            return null;
        }

        /// <summary>Every car of the race a session belongs to, from the shared races.</summary>
        // (only races carry every car; a shared practice or qualifying has its laps only)
        public static RaceField FieldFor(LmuSessionRecord session)
        {
            if (session == null || !session.IsRace) return null;
            var direct = RaceOf(session.FileId);
            if (direct != null) return direct.Field;
            foreach (var r in All()) if (r.Field != null && SameRace(r.Session, session)) return r.Field;
            return null;
        }
    }
}
