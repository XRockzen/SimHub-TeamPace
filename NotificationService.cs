using LiteDB;
using System;
using System.Collections.Generic;
using System.IO;

namespace User.TeamPace
{
    public class TeamNotification
    {
        public int      Id            { get; set; }
        public DateTime CreatedUtc    { get; set; }
        public string   Kind          { get; set; }   // record, improved, win, podium, common, telemetry, retaken
        public string   Title         { get; set; }
        public string   Detail        { get; set; }
        public string   Circuit       { get; set; }   // open the circuit page
        public string   ClassLabel    { get; set; }
        public string   SessionFileId { get; set; }   // open the session analysis
        public string   DriverKey     { get; set; }   // whose session (null = own)
        public bool     Read          { get; set; }
        public bool     Toasted       { get; set; }
    }

    /// <summary>What was known of a driver at the last check, to spot what is new.</summary>
    public class NotificationState
    {
        public string                   Id       { get; set; }   // driver file name, "me" for the own database
        public List<BestEntry>          Bests    { get; set; }   // best lap per circuit|class
        public List<string>             Sessions { get; set; }   // session ids already seen

        // Stored as a list: LiteDB field names cannot hold the spaces of circuit names
        public Dictionary<string, long> BestMap()
        {
            var map = new Dictionary<string, long>();
            if (Bests != null) foreach (var b in Bests) map[b.Key] = b.Ticks;
            return map;
        }
    }

    public class BestEntry
    {
        public string Key   { get; set; }
        public long   Ticks { get; set; }
    }

    /// <summary>
    /// Teammates' news (records beaten, wins, common sessions, telemetry received), stacked until
    /// read. Kept in a local file, never shared with the team.
    /// </summary>
    public static class NotificationService
    {
        private static readonly object Lock = new object();
        private static string _path;

        /// <summary>New notifications, raised on a background thread.</summary>
        public static event Action<List<TeamNotification>> Arrived;
        public static event Action Changed;

        public static void Init(string dataDir)
        {
            _path = Path.Combine(dataDir, "local.db");
        }

        private static LiteDatabase Open() { return new LiteDatabase(string.Format("Filename={0}", _path)); }

        // ─── Read / update ────────────────────────────────────────────────────

        public static List<TeamNotification> All()
        {
            var list = new List<TeamNotification>();
            if (_path == null) return list;
            lock (Lock)
            {
                try
                {
                    using (var db = Open()) list.AddRange(db.GetCollection<TeamNotification>("notifications").FindAll());
                }
                catch (Exception ex) { Warn("read", ex); }
            }
            list.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
            return list;
        }

        public static int UnreadCount()
        {
            int n = 0;
            foreach (var x in All()) if (!x.Read) n++;
            return n;
        }

        public static void MarkAllRead() { Update(x => { if (x.Read) return false; x.Read = true; return true; }); }

        public static void MarkToasted(ICollection<int> ids) { Update(x => { if (x.Toasted || !ids.Contains(x.Id)) return false; x.Toasted = true; return true; }); }

        public static void Delete(int id)
        {
            lock (Lock)
            {
                try { using (var db = Open()) db.GetCollection<TeamNotification>("notifications").Delete(id); }
                catch (Exception ex) { Warn("delete", ex); }
            }
            RaiseChanged();
        }

        private static void Update(Func<TeamNotification, bool> change)
        {
            if (_path == null) return;
            bool any = false;
            lock (Lock)
            {
                try
                {
                    using (var db = Open())
                    {
                        var col = db.GetCollection<TeamNotification>("notifications");
                        foreach (var x in col.FindAll()) if (change(x)) { col.Update(x); any = true; }
                    }
                }
                catch (Exception ex) { Warn("update", ex); }
            }
            if (any) RaiseChanged();
        }

        private static void Add(List<TeamNotification> items)
        {
            if (items.Count == 0 || _path == null) return;
            lock (Lock)
            {
                try
                {
                    using (var db = Open())
                    {
                        var col = db.GetCollection<TeamNotification>("notifications");
                        foreach (var x in items) { x.CreatedUtc = DateTime.UtcNow; col.Insert(x); }
                        // Keep the last 200
                        var all = new List<TeamNotification>(col.FindAll());
                        all.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
                        for (int i = 200; i < all.Count; i++) col.Delete(all[i].Id);
                    }
                }
                catch (Exception ex) { Warn("add", ex); return; }
            }
            var h = Arrived;
            if (h != null) h(items);
            RaiseChanged();
        }

        private static void RaiseChanged() { var h = Changed; if (h != null) h(); }

        // ─── Detection ────────────────────────────────────────────────────────

        public static void AddTelemetry(string driverName, int files)
        {
            if (files <= 0) return;
            Add(new List<TeamNotification>
            {
                new TeamNotification
                {
                    Kind = "telemetry",
                    Title = string.Format("Télémétrie de {0} reçue", driverName),
                    Detail = string.Format("{0} relais de plus à comparer", files),
                },
            });
        }

        /// <summary>
        /// Compares every teammate's results with what was known at the last check. The first
        /// check of a driver only records the current state: no flood of old news.
        /// </summary>
        public static void Check()
        {
            if (_path == null || string.IsNullOrEmpty(LmuSessionStore.OwnDbPath)) return;
            var drivers = LmuTeamSync.GetDrivers();
            if (drivers.Count == 0) return;
            var me = drivers[0];
            var mySessions = TeamRaceStore.Complete(me.Name, LmuSessionStore.GetAllSessions(null));
            var myBests = Bests(mySessions);
            var news = new List<TeamNotification>();

            lock (Lock)
            {
                try
                {
                    using (var db = Open())
                    {
                        var states = db.GetCollection<NotificationState>("notification_state");
                        var mates = new List<KeyValuePair<TeamDriver, Dictionary<string, LmuCircuitBestRow>>>();

                        for (int i = 1; i < drivers.Count; i++)
                        {
                            var mate = drivers[i];
                            var id = Path.GetFileName(mate.DbPath);
                            var sessions = TeamRaceStore.Complete(mate.Name, LmuSessionStore.GetAllSessions(mate.DbPath));
                            var bests = Bests(sessions);
                            mates.Add(new KeyValuePair<TeamDriver, Dictionary<string, LmuCircuitBestRow>>(mate, bests));

                            var state = states.FindById(id);
                            if (state != null) Compare(mate, state, bests, sessions, myBests, mySessions, news);
                            states.Upsert(Snapshot(id, bests, sessions));
                        }

                        // Own records taken back from a teammate
                        var mine = states.FindById("me");
                        var mineBests = mine != null ? mine.BestMap() : null;
                        if (mine != null)
                            foreach (var kv in myBests)
                            {
                                long prev;
                                if (!mineBests.TryGetValue(kv.Key, out prev) || kv.Value.BestTicks >= prev) continue;
                                foreach (var m in mates)
                                {
                                    LmuCircuitBestRow theirs;
                                    if (!m.Value.TryGetValue(kv.Key, out theirs)) continue;
                                    if (kv.Value.BestTicks < theirs.BestTicks && prev >= theirs.BestTicks)
                                        news.Add(new TeamNotification
                                        {
                                            Kind = "retaken", Circuit = kv.Value.Circuit, ClassLabel = kv.Value.CarClassLabel,
                                            Title = string.Format("Tu as repris le record à {0} · {1}", kv.Value.Circuit, kv.Value.CarClassLabel),
                                            Detail = string.Format("{0}, {1} devant {2}", kv.Value.BestLap, Gap(theirs.BestTicks - kv.Value.BestTicks), m.Key.Name),
                                        });
                                }
                            }
                        states.Upsert(Snapshot("me", myBests, mySessions));
                    }
                }
                catch (Exception ex) { Warn("check", ex); return; }
            }
            Add(news);
        }

        private static void Compare(TeamDriver mate, NotificationState state, Dictionary<string, LmuCircuitBestRow> bests,
                                    List<LmuSessionRecord> sessions, Dictionary<string, LmuCircuitBestRow> myBests,
                                    List<LmuSessionRecord> mySessions, List<TeamNotification> news)
        {
            var stateBests = state.BestMap();
            foreach (var kv in bests)
            {
                var r = kv.Value;
                long prev;
                bool known = stateBests.TryGetValue(kv.Key, out prev);
                if (known && r.BestTicks >= prev) continue;
                LmuCircuitBestRow mine;
                bool iHave = myBests.TryGetValue(kv.Key, out mine);
                if (iHave && r.BestTicks < mine.BestTicks && (!known || prev >= mine.BestTicks))
                    news.Add(new TeamNotification
                    {
                        Kind = "record", Circuit = r.Circuit, ClassLabel = r.CarClassLabel,
                        Title = string.Format("{0} a battu ton record à {1} · {2}", mate.Name, r.Circuit, r.CarClassLabel),
                        Detail = string.Format("{0}, {1} de mieux que toi", r.BestLap, Gap(mine.BestTicks - r.BestTicks)),
                    });
                else if (known)
                    news.Add(new TeamNotification
                    {
                        Kind = "improved", Circuit = r.Circuit, ClassLabel = r.CarClassLabel,
                        Title = string.Format("{0} a amélioré son record à {1} · {2}", mate.Name, r.Circuit, r.CarClassLabel),
                        Detail = string.Format("{0} (−{1}){2}", r.BestLap, Gap(prev - r.BestTicks),
                            !iHave ? "" : r.BestTicks < mine.BestTicks
                                ? string.Format(", {0} devant toi", Gap(mine.BestTicks - r.BestTicks))
                                : string.Format(", encore {0} derrière toi", Gap(r.BestTicks - mine.BestTicks))),
                    });
            }

            var seen = new HashSet<string>(state.Sessions ?? new List<string>());
            foreach (var s in sessions)
            {
                if (seen.Contains(s.FileId)) continue;
                int pos = s.ClassPosition > 0 ? s.ClassPosition : s.Position;
                if (s.IsRace && s.IsFinished && pos == 1)
                    news.Add(SessionNews("win", mate, s, string.Format("{0} a gagné une course à {1}", mate.Name, s.TrackDisplayName),
                                         string.Format("{0} · {1:dd/MM HH:mm}", new LmuSessionRow { Record = s }.Position, s.SessionDate)));
                else if (s.IsRace && s.IsFinished && pos > 1 && pos <= 3)
                    news.Add(SessionNews("podium", mate, s, string.Format("{0} est monté sur le podium à {1}", mate.Name, s.TrackDisplayName),
                                         string.Format("{0} · {1:dd/MM HH:mm}", new LmuSessionRow { Record = s }.Position, s.SessionDate)));

                foreach (var m in mySessions)
                {
                    if (!SessionMatch.Same(m, s)) continue;
                    string detail;
                    if (s.IsRace && m.IsFinished && s.IsFinished && m.Position > 0 && s.Position > 0)
                        detail = string.Format("{0} : tu termines {1}, {2} contre {3}", s.SessionTypeLong,
                            m.Position < s.Position ? "devant lui" : "derrière lui", new LmuSessionRow { Record = m }.Position, new LmuSessionRow { Record = s }.Position);
                    else if (m.BestLapTime.Ticks > 0 && s.BestLapTime.Ticks > 0)
                        detail = string.Format("{0} : meilleur tour {1} contre {2}", s.SessionTypeLong, m.BestLapFormatted, s.BestLapFormatted);
                    else detail = s.SessionTypeLong;
                    news.Add(new TeamNotification
                    {
                        Kind = "common", SessionFileId = m.FileId, DriverKey = null, Circuit = s.TrackDisplayName,
                        ClassLabel = LmuCategoryStyle.GetShortName(s.CarClass),
                        Title = string.Format("Session commune avec {0} à {1} du {2:dd/MM}", mate.Name, s.TrackDisplayName, s.SessionDate),
                        Detail = detail,
                    });
                    break;
                }
            }
        }

        private static TeamNotification SessionNews(string kind, TeamDriver mate, LmuSessionRecord s, string title, string detail)
        {
            return new TeamNotification
            {
                Kind = kind, Title = title, Detail = detail, SessionFileId = s.FileId, DriverKey = mate.DbPath,
                Circuit = s.TrackDisplayName, ClassLabel = LmuCategoryStyle.GetShortName(s.CarClass),
            };
        }

        private static NotificationState Snapshot(string id, Dictionary<string, LmuCircuitBestRow> bests, List<LmuSessionRecord> sessions)
        {
            var state = new NotificationState { Id = id, Bests = new List<BestEntry>(), Sessions = new List<string>() };
            foreach (var kv in bests) state.Bests.Add(new BestEntry { Key = kv.Key, Ticks = kv.Value.BestTicks });
            foreach (var s in sessions) state.Sessions.Add(s.FileId);
            return state;
        }

        private static Dictionary<string, LmuCircuitBestRow> Bests(List<LmuSessionRecord> sessions)
        {
            var map = new Dictionary<string, LmuCircuitBestRow>();
            foreach (var r in CareerColumn.BuildCircuitRows(sessions)) if (r.BestTicks > 0) map[r.Key] = r;
            return map;
        }

        private static string Gap(long ticks) { return Fmt.Seconds(TimeSpan.FromTicks(ticks)); }

        private static void Warn(string what, Exception ex)
        {
            SimHub.Logging.Current.Warn("[TeamPace] Notifications " + what + ": " + ex.Message);
        }
    }
}
