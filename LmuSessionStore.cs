using LiteDB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace User.TeamPace
{
    public static class LmuSessionStore
    {
        private static string _dbPath;       // own database — the only one ever written
        private static readonly object _lock = new object();

        public static string OwnDbPath  { get { return _dbPath; } }
        public static string TeamFolder { get; private set; }

        // Read accessors take a driver's database path; null means the own database.
        private static string ReadConn(string dbPath)
        {
            return string.Format("Filename={0};ReadOnly=true", string.IsNullOrEmpty(dbPath) ? _dbPath : dbPath);
        }

        // Bump when the parser starts accepting files it used to skip: clears
        // the "already parsed" flags once so those files get re-imported.
        private const int ParserVersion = 9;   // 5: shared cars split by driver, session clock origin; 6: race field; 7: race start time; 8: team races shared; 9: every shared session

        /// <summary>Bumped whenever stored sessions change: views reload only then.</summary>
        public static int DataVersion { get { return _version; } }
        private static int _version;
        public static void Touch() { System.Threading.Interlocked.Increment(ref _version); }

        public static string PlayerName  { get; private set; }
        public static string ResultsPath { get; private set; }

        // ─── Init ──────────────────────────────────────────────────────────────

        public static void Init(string commonPath)
        {
            _dbPath = Path.Combine(PluginPaths.DataDir(commonPath), "lmu_sessions.db");

            try
            {
                using (var db = new LiteDatabase(string.Format("Filename={0}", _dbPath)))
                {
                    // Ensure indexes for fast lookups
                    db.GetCollection("sessions").EnsureIndex("FileId");
                    db.GetCollection("laps").EnsureIndex("SessionId");
                    db.GetCollection("parsed_files").EnsureIndex("FileId");
                    db.GetCollection("fields").EnsureIndex("FileId");

                    // Load settings
                    var row = db.GetCollection("settings")
                                .FindOne(Query.EQ("_id", new BsonValue("config")));
                    if (row != null)
                    {
                        PlayerName  = row.ContainsKey("PlayerName")  ? row["PlayerName"].AsString  : "";
                        ResultsPath = row.ContainsKey("ResultsPath") ? row["ResultsPath"].AsString : "";
                        TeamFolder  = row.ContainsKey("TeamFolder")  ? row["TeamFolder"].AsString  : "";
                    }

                    var settings = db.GetCollection("settings");
                    var meta = settings.FindOne(Query.EQ("_id", new BsonValue("meta")));
                    int storedVersion = meta != null && meta.ContainsKey("ParserVersion")
                        ? meta["ParserVersion"].AsInt32 : 1;
                    if (storedVersion < ParserVersion)
                    {
                        db.GetCollection("parsed_files").Delete(Query.All());
                        var m = new BsonDocument();
                        m["_id"]           = new BsonValue("meta");
                        m["ParserVersion"] = new BsonValue(ParserVersion);
                        settings.Upsert(m);
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    string.Format("[LmuSessionStore] Init error: {0}", ex.Message));
            }

            if (PlayerName  == null) PlayerName  = "";
            if (ResultsPath == null) ResultsPath = "";
            if (TeamFolder  == null) TeamFolder  = "";
        }

        // ─── Settings ──────────────────────────────────────────────────────────

        public static void SaveSettings(string playerName, string resultsPath)
        {
            if (string.IsNullOrEmpty(_dbPath)) return;
            lock (_lock)
            {
                try
                {
                    using (var db = new LiteDatabase(string.Format("Filename={0}", _dbPath)))
                    {
                        var doc = new BsonDocument();
                        doc["_id"]         = new BsonValue("config");
                        doc["PlayerName"]  = new BsonValue(playerName  ?? "");
                        doc["ResultsPath"] = new BsonValue(resultsPath ?? "");
                        doc["TeamFolder"]  = new BsonValue(TeamFolder  ?? "");
                        db.GetCollection("settings").Upsert(doc);
                    }
                    PlayerName  = playerName  ?? "";
                    ResultsPath = resultsPath ?? "";
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        string.Format("[LmuSessionStore] SaveSettings error: {0}", ex.Message));
                }
            }
        }

        public static void SaveTeamFolder(string folder)
        {
            TeamFolder = folder ?? "";
            SaveSettings(PlayerName, ResultsPath);
        }

        /// <summary>Copies the own database under the write lock, so the copy is consistent.</summary>
        public static void CopyOwnDatabase(string destPath)
        {
            lock (_lock) File.Copy(_dbPath, destPath, true);
        }

        /// <summary>Reads the player name stored in another plugin's database copy.</summary>
        public static string ReadPlayerName(string dbPath)
        {
            try
            {
                using (var db = new LiteDatabase(string.Format("Filename={0};ReadOnly=true", dbPath)))
                {
                    var row = db.GetCollection("settings").FindOne(Query.EQ("_id", new BsonValue("config")));
                    return row != null && row.ContainsKey("PlayerName") ? row["PlayerName"].AsString : "";
                }
            }
            catch { return ""; }
        }

        // ─── File tracking ─────────────────────────────────────────────────────

        public static bool IsFileParsed(string fileId)
        {
            if (string.IsNullOrEmpty(_dbPath)) return false;
            try
            {
                using (var db = new LiteDatabase(string.Format("Filename={0};ReadOnly=true", _dbPath)))
                    return db.GetCollection("parsed_files")
                             .FindOne(Query.EQ("FileId", new BsonValue(fileId))) != null;
            }
            catch { return false; }
        }

        public static void MarkFileParsed(string fileId)
        {
            if (string.IsNullOrEmpty(_dbPath) || string.IsNullOrEmpty(fileId)) return;
            lock (_lock)
            {
                try
                {
                    using (var db = new LiteDatabase(string.Format("Filename={0}", _dbPath)))
                    {
                        var col = db.GetCollection("parsed_files");
                        if (col.FindOne(Query.EQ("FileId", new BsonValue(fileId))) == null)
                        {
                            var doc = new BsonDocument();
                            doc["FileId"] = new BsonValue(fileId);
                            doc["Date"]   = new BsonValue(DateTime.UtcNow);
                            col.Insert(doc);
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>Supprime les flags "déjà parsé" pour forcer un re-scan complet.</summary>
        public static void ClearParsedFlags()
        {
            if (string.IsNullOrEmpty(_dbPath)) return;
            lock (_lock)
            {
                try
                {
                    using (var db = new LiteDatabase(string.Format("Filename={0}", _dbPath)))
                        db.GetCollection("parsed_files").Delete(Query.All());
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        string.Format("[LmuSessionStore] ClearParsedFlags error: {0}", ex.Message));
                }
            }
        }

        // ─── Write ─────────────────────────────────────────────────────────────

        public static void SaveSession(LmuSessionRecord session, List<LmuLapRecord> laps)
        {
            if (string.IsNullOrEmpty(_dbPath) || session == null) return;
            lock (_lock)
            {
                try
                {
                    using (var db = new LiteDatabase(string.Format("Filename={0}", _dbPath)))
                    {
                        // LMU writes a results file each time you join a running session (after a
                        // driver swap, a disconnection): keep only the most complete one
                        if (!KeepAgainstDuplicates(db, session))
                        {
                            MarkParsed(db, session.FileId);
                            return;
                        }
                        db.GetCollection("sessions").Upsert(SessionToDoc(session));

                        if (laps != null && laps.Count > 0)
                        {
                            var col = db.GetCollection("laps");
                            col.Delete(Query.EQ("SessionId", new BsonValue(session.FileId)));
                            foreach (var lap in laps)
                                col.Insert(LapToDoc(lap));
                        }

                        var fields = db.GetCollection("fields");
                        fields.Delete(Query.EQ("FileId", new BsonValue(session.FileId)));
                        if (session.Field != null)
                        {
                            var f = new BsonDocument();
                            f["FileId"] = new BsonValue(session.FileId);
                            f["Json"]   = new BsonValue(Newtonsoft.Json.JsonConvert.SerializeObject(session.Field));
                            fields.Insert(f);
                        }

                        MarkParsed(db, session.FileId);
                    }
                    if (session.Race != null) TeamRaceStore.Save(session.Race);
                    Touch();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        string.Format("[LmuSessionStore] SaveSession error: {0}", ex.Message));
                }
            }
        }

        private static void MarkParsed(LiteDatabase db, string fileId)
        {
            var pf = db.GetCollection("parsed_files");
            if (pf.FindOne(Query.EQ("FileId", new BsonValue(fileId))) != null) return;
            var doc = new BsonDocument();
            doc["FileId"] = new BsonValue(fileId);
            doc["Date"]   = new BsonValue(DateTime.UtcNow);
            pf.Insert(doc);
        }

        /// <summary>
        /// Same session already stored from another results file (same track, same type, same
        /// session start): keeps whichever covers more laps. Returns false when the new one loses.
        /// </summary>
        private static bool KeepAgainstDuplicates(LiteDatabase db, LmuSessionRecord session)
        {
            if (session.BlockStartUtc == DateTime.MinValue) return true;
            var sessions = db.GetCollection("sessions");
            var laps = db.GetCollection("laps");
            var tolerance = TimeSpan.FromMinutes(3).Ticks;
            foreach (var d in sessions.Find(Query.EQ("TrackVenue", new BsonValue(session.TrackVenue ?? ""))))
            {
                var other = DocToSession(d);
                if (other.FileId == session.FileId || other.SessionType != session.SessionType) continue;
                if (other.TrackCourse != session.TrackCourse || other.BlockStartUtc == DateTime.MinValue) continue;
                if (Math.Abs(other.BlockStartUtc.Ticks - session.BlockStartUtc.Ticks) > tolerance) continue;
                if (Math.Max(other.CarLaps, other.TotalLaps) > Math.Max(session.CarLaps, session.TotalLaps)) return false;
                sessions.Delete(d["_id"]);
                laps.Delete(Query.EQ("SessionId", new BsonValue(other.FileId)));
                db.GetCollection("fields").Delete(Query.EQ("FileId", new BsonValue(other.FileId)));
            }
            return true;
        }

        // ─── Read ──────────────────────────────────────────────────────────────

        public static List<LmuSessionRecord> GetAllSessions(string dbPath = null)
        {
            var result = new List<LmuSessionRecord>();
            if (string.IsNullOrEmpty(_dbPath)) return result;
            try
            {
                using (var db = new LiteDatabase(ReadConn(dbPath)))
                    foreach (var doc in db.GetCollection("sessions").FindAll())
                        result.Add(DocToSession(doc));
            }
            catch { }
            return result.OrderByDescending(s => s.SessionDate).ToList();
        }

        public static List<LmuLapRecord> GetLapsForSession(string fileId, string dbPath = null)
        {
            var result = new List<LmuLapRecord>();
            if (string.IsNullOrEmpty(_dbPath) || string.IsNullOrEmpty(fileId)) return result;
            if (fileId.StartsWith(TeamRaceStore.Prefix)) return TeamRaceStore.LapsFor(fileId);
            try
            {
                using (var db = new LiteDatabase(ReadConn(dbPath)))
                    foreach (var doc in db.GetCollection("laps")
                                         .Find(Query.EQ("SessionId", new BsonValue(fileId))))
                        result.Add(DocToLap(doc));
            }
            catch { }
            return result.OrderBy(l => l.LapNum).ToList();
        }

        /// <summary>Every car of a race (null when the race was imported before 3.5 or is not a race).</summary>
        public static RaceField GetField(string fileId, string dbPath = null)
        {
            if (string.IsNullOrEmpty(_dbPath) || string.IsNullOrEmpty(fileId)) return null;
            if (fileId.StartsWith(TeamRaceStore.Prefix)) return null;
            try
            {
                using (var db = new LiteDatabase(ReadConn(dbPath)))
                {
                    var doc = db.GetCollection("fields").FindOne(Query.EQ("FileId", new BsonValue(fileId)));
                    if (doc != null) return Newtonsoft.Json.JsonConvert.DeserializeObject<RaceField>(doc["Json"].AsString);
                }
            }
            catch { }
            return null;
        }

        public static int GetSessionCount()
        {
            if (string.IsNullOrEmpty(_dbPath)) return 0;
            try
            {
                using (var db = new LiteDatabase(ReadConn(null)))
                    return db.GetCollection("sessions").Count();
            }
            catch { return 0; }
        }

        public static int GetParsedFileCount()
        {
            if (string.IsNullOrEmpty(_dbPath)) return 0;
            try
            {
                using (var db = new LiteDatabase(string.Format("Filename={0};ReadOnly=true", _dbPath)))
                    return db.GetCollection("parsed_files").Count();
            }
            catch { return 0; }
        }

        // ─── Live capture ──────────────────────────────────────────────────────

        public static void SaveLiveLap(LmuLiveLap lap)
        {
            if (string.IsNullOrEmpty(_dbPath) || lap == null) return;
            lock (_lock)
            {
                try
                {
                    using (var db = new LiteDatabase(string.Format("Filename={0}", _dbPath)))
                    {
                        var col = db.GetCollection<LmuLiveLap>("live_laps");
                        col.EnsureIndex(x => x.StartUtcTicks);
                        col.Insert(lap);
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        string.Format("[LmuSessionStore] SaveLiveLap error: {0}", ex.Message));
                }
            }
        }

        public static void SaveLivePit(LmuLivePit pit)
        {
            if (string.IsNullOrEmpty(_dbPath) || pit == null) return;
            lock (_lock)
            {
                try
                {
                    using (var db = new LiteDatabase(string.Format("Filename={0}", _dbPath)))
                    {
                        var col = db.GetCollection<LmuLivePit>("live_pits");
                        col.EnsureIndex(x => x.EntryUtcTicks);
                        col.Insert(pit);
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        string.Format("[LmuSessionStore] SaveLivePit error: {0}", ex.Message));
                }
            }
        }

        public static List<LmuLiveLap> GetLiveLaps(DateTime fromUtc, DateTime toUtc, string dbPath = null)
        {
            var result = new List<LmuLiveLap>();
            if (string.IsNullOrEmpty(_dbPath)) return result;
            try
            {
                using (var db = new LiteDatabase(ReadConn(dbPath)))
                    result.AddRange(db.GetCollection<LmuLiveLap>("live_laps").Find(
                        Query.Between("StartUtcTicks", new BsonValue(fromUtc.Ticks), new BsonValue(toUtc.Ticks))));
            }
            catch { }
            return result;
        }

        public static List<LmuLivePit> GetLivePits(DateTime fromUtc, DateTime toUtc, string dbPath = null)
        {
            var result = new List<LmuLivePit>();
            if (string.IsNullOrEmpty(_dbPath)) return result;
            try
            {
                using (var db = new LiteDatabase(ReadConn(dbPath)))
                    result.AddRange(db.GetCollection<LmuLivePit>("live_pits").Find(
                        Query.Between("EntryUtcTicks", new BsonValue(fromUtc.Ticks), new BsonValue(toUtc.Ticks))));
            }
            catch { }
            return result;
        }

        // ─── Serialization ─────────────────────────────────────────────────────

        private static BsonDocument SessionToDoc(LmuSessionRecord s)
        {
            var d = new BsonDocument();
            d["_id"]             = new BsonValue(s.FileId);
            d["FileId"]          = new BsonValue(s.FileId         ?? "");
            d["SessionType"]     = new BsonValue(s.SessionType    ?? "");
            d["SessionDate"]     = new BsonValue(s.SessionDate);
            d["BlockStartTicks"] = new BsonValue(s.BlockStartUtc.Ticks);
            d["TrackVenue"]      = new BsonValue(s.TrackVenue     ?? "");
            d["TrackCourse"]     = new BsonValue(s.TrackCourse    ?? "");
            d["TrackEvent"]      = new BsonValue(s.TrackEvent     ?? "");
            d["TrackLengthM"]    = new BsonValue((double)s.TrackLengthM);
            d["Setting"]         = new BsonValue(s.Setting        ?? "");
            d["ServerName"]      = new BsonValue(s.ServerName     ?? "");
            d["RaceTimeMinutes"] = new BsonValue((double)s.RaceTimeMinutes);
            d["RaceLaps"]        = new BsonValue(s.RaceLaps);
            d["DamageMult"]      = new BsonValue((double)s.DamageMult);
            d["FuelMult"]        = new BsonValue((double)s.FuelMult);
            d["TireMult"]        = new BsonValue((double)s.TireMult);
            d["MechFailRate"]    = new BsonValue((double)s.MechFailRate);
            d["PlayerName"]      = new BsonValue(s.PlayerName     ?? "");
            d["CarType"]         = new BsonValue(s.CarType        ?? "");
            d["CarClass"]        = new BsonValue(s.CarClass       ?? "");
            d["CarNumber"]       = new BsonValue(s.CarNumber);
            d["TeamName"]        = new BsonValue(s.TeamName       ?? "");
            d["GridPos"]         = new BsonValue(s.GridPos);
            d["ClassGridPos"]    = new BsonValue(s.ClassGridPos);
            d["Position"]        = new BsonValue(s.Position);
            d["ClassPosition"]   = new BsonValue(s.ClassPosition);
            d["BestLapTicks"]    = new BsonValue(s.BestLapTime.Ticks);
            d["FinishTimeTicks"] = new BsonValue(s.FinishTime.Ticks);
            d["TotalLaps"]       = new BsonValue(s.TotalLaps);
            d["Pitstops"]        = new BsonValue(s.Pitstops);
            d["FinishStatus"]    = new BsonValue(s.FinishStatus   ?? "");
            d["DNFReason"]       = new BsonValue(s.DNFReason      ?? "");
            d["ControlAndAids"]  = new BsonValue(s.ControlAndAids ?? "");
            d["IncidentCount"]   = new BsonValue(s.IncidentCount);
            d["PenaltyCount"]    = new BsonValue(s.PenaltyCount);
            d["CarLaps"]         = new BsonValue(s.CarLaps);
            d["TeamDrivers"]     = new BsonValue(s.TeamDrivers ?? "");
            return d;
        }

        private static LmuSessionRecord DocToSession(BsonDocument d)
        {
            return new LmuSessionRecord
            {
                FileId          = Str(d, "FileId"),
                SessionType     = Str(d, "SessionType"),
                SessionDate     = d.ContainsKey("SessionDate")     ? d["SessionDate"].AsDateTime           : DateTime.MinValue,
                BlockStartUtc   = d.ContainsKey("BlockStartTicks")
                                    ? new DateTime(d["BlockStartTicks"].AsInt64, DateTimeKind.Utc) : DateTime.MinValue,
                TrackVenue      = Str(d, "TrackVenue"),
                TrackCourse     = Str(d, "TrackCourse"),
                TrackEvent      = Str(d, "TrackEvent"),
                TrackLengthM    = Flt(d, "TrackLengthM"),
                Setting         = Str(d, "Setting"),
                ServerName      = Str(d, "ServerName"),
                RaceTimeMinutes = Flt(d, "RaceTimeMinutes"),
                RaceLaps        = Int(d, "RaceLaps"),
                DamageMult      = d.ContainsKey("DamageMult")      ? (float)d["DamageMult"].AsDouble       : 1f,
                FuelMult        = d.ContainsKey("FuelMult")        ? (float)d["FuelMult"].AsDouble         : 1f,
                TireMult        = d.ContainsKey("TireMult")        ? (float)d["TireMult"].AsDouble         : 1f,
                MechFailRate    = Flt(d, "MechFailRate"),
                PlayerName      = Str(d, "PlayerName"),
                CarType         = Str(d, "CarType"),
                CarClass        = Str(d, "CarClass"),
                CarNumber       = Int(d, "CarNumber"),
                TeamName        = Str(d, "TeamName"),
                GridPos         = Int(d, "GridPos"),
                ClassGridPos    = Int(d, "ClassGridPos"),
                Position        = Int(d, "Position"),
                ClassPosition   = Int(d, "ClassPosition"),
                BestLapTime     = d.ContainsKey("BestLapTicks")    ? TimeSpan.FromTicks(d["BestLapTicks"].AsInt64)    : TimeSpan.Zero,
                FinishTime      = d.ContainsKey("FinishTimeTicks") ? TimeSpan.FromTicks(d["FinishTimeTicks"].AsInt64) : TimeSpan.Zero,
                TotalLaps       = Int(d, "TotalLaps"),
                Pitstops        = Int(d, "Pitstops"),
                FinishStatus    = Str(d, "FinishStatus"),
                DNFReason       = Str(d, "DNFReason"),
                ControlAndAids  = Str(d, "ControlAndAids"),
                IncidentCount   = Int(d, "IncidentCount"),
                PenaltyCount    = Int(d, "PenaltyCount"),
                CarLaps         = Int(d, "CarLaps"),
                TeamDrivers     = Str(d, "TeamDrivers"),
            };
        }

        private static BsonDocument LapToDoc(LmuLapRecord l)
        {
            var d = new BsonDocument();
            d["SessionId"]         = new BsonValue(l.SessionId ?? "");
            d["LapNum"]            = new BsonValue(l.LapNum);
            d["LapTimeTicks"]      = new BsonValue(l.LapTimeTicks);
            d["IsValid"]           = new BsonValue(l.IsValid);
            d["Position"]          = new BsonValue(l.Position);
            d["S1"]                = new BsonValue((double)l.S1);
            d["S2"]                = new BsonValue((double)l.S2);
            d["S3"]                = new BsonValue((double)l.S3);
            d["TopSpeedKph"]       = new BsonValue((double)l.TopSpeedKph);
            d["FuelRemaining"]     = new BsonValue((double)l.FuelRemaining);
            d["FuelUsed"]          = new BsonValue((double)l.FuelUsed);
            d["VirtualEnergy"]     = new BsonValue((double)l.VirtualEnergy);
            d["VirtualEnergyUsed"] = new BsonValue((double)l.VirtualEnergyUsed);
            d["TireWearFL"]        = new BsonValue((double)l.TireWearFL);
            d["TireWearFR"]        = new BsonValue((double)l.TireWearFR);
            d["TireWearRL"]        = new BsonValue((double)l.TireWearRL);
            d["TireWearRR"]        = new BsonValue((double)l.TireWearRR);
            d["FrontCompound"]     = new BsonValue(l.FrontCompound ?? "");
            d["RearCompound"]      = new BsonValue(l.RearCompound  ?? "");
            d["IsPitLap"]          = new BsonValue(l.IsPitLap);
            d["ElapsedTime"]       = new BsonValue((double)l.ElapsedTime);
            d["LimitsInvalid"]     = new BsonValue(l.LimitsInvalid);
            d["LimitsWarning"]     = new BsonValue(l.LimitsWarning);
            d["LimitsNoAction"]    = new BsonValue(l.LimitsNoAction);
            d["Contacts"]          = new BsonValue(l.Contacts);
            d["Penalty"]           = new BsonValue(l.Penalty ?? "");
            d["ReconstructedTicks"] = new BsonValue(l.ReconstructedTicks);
            return d;
        }

        private static LmuLapRecord DocToLap(BsonDocument d)
        {
            return new LmuLapRecord
            {
                SessionId         = Str(d, "SessionId"),
                LapNum            = Int(d, "LapNum"),
                LapTimeTicks      = d.ContainsKey("LapTimeTicks")      ? d["LapTimeTicks"].AsInt64       : 0L,
                IsValid           = d.ContainsKey("IsValid")           ? d["IsValid"].AsBoolean          : false,
                Position          = Int(d, "Position"),
                S1                = d.ContainsKey("S1")                ? (float)d["S1"].AsDouble         : -1f,
                S2                = d.ContainsKey("S2")                ? (float)d["S2"].AsDouble         : -1f,
                S3                = d.ContainsKey("S3")                ? (float)d["S3"].AsDouble         : -1f,
                TopSpeedKph       = Flt(d, "TopSpeedKph"),
                FuelRemaining     = Flt(d, "FuelRemaining"),
                FuelUsed          = Flt(d, "FuelUsed"),
                VirtualEnergy     = Flt(d, "VirtualEnergy"),
                VirtualEnergyUsed = Flt(d, "VirtualEnergyUsed"),
                TireWearFL        = Flt(d, "TireWearFL"),
                TireWearFR        = Flt(d, "TireWearFR"),
                TireWearRL        = Flt(d, "TireWearRL"),
                TireWearRR        = Flt(d, "TireWearRR"),
                FrontCompound     = Str(d, "FrontCompound"),
                RearCompound      = Str(d, "RearCompound"),
                IsPitLap          = d.ContainsKey("IsPitLap") ? d["IsPitLap"].AsBoolean : false,
                ElapsedTime       = Flt(d, "ElapsedTime"),
                LimitsInvalid     = Int(d, "LimitsInvalid"),
                LimitsWarning     = Int(d, "LimitsWarning"),
                LimitsNoAction    = Int(d, "LimitsNoAction"),
                Contacts          = Int(d, "Contacts"),
                Penalty           = Str(d, "Penalty"),
                ReconstructedTicks = d.ContainsKey("ReconstructedTicks") ? d["ReconstructedTicks"].AsInt64 : 0,
            };
        }

        // ─── BsonDocument accessors ────────────────────────────────────────────

        private static string Str(BsonDocument d, string key)
            { return d.ContainsKey(key) ? d[key].AsString : ""; }

        private static int Int(BsonDocument d, string key)
            { return d.ContainsKey(key) ? d[key].AsInt32 : 0; }

        private static float Flt(BsonDocument d, string key)
            { return d.ContainsKey(key) ? (float)d[key].AsDouble : 0f; }
    }
}
