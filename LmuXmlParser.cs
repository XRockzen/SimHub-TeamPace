using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace User.TeamPace
{
    public static class LmuXmlParser
    {
        private static readonly string[] _candidatePaths = new[]
        {
            @"F:\Program Files (x86)\Steam\steamapps\common\Le Mans Ultimate\UserData\Log\Results",
            @"C:\Program Files (x86)\Steam\steamapps\common\Le Mans Ultimate\UserData\Log\Results",
            @"D:\Steam\steamapps\common\Le Mans Ultimate\UserData\Log\Results",
            @"E:\Steam\steamapps\common\Le Mans Ultimate\UserData\Log\Results",
            @"G:\Steam\steamapps\common\Le Mans Ultimate\UserData\Log\Results",
        };

        // FileSystemWatcher for live session detection
        private static FileSystemWatcher _watcher;
        private static string            _watchPlayerName;

        // ─── Discovery ─────────────────────────────────────────────────────────

        public static string AutoDiscoverResultsPath()
        {
            foreach (var path in _candidatePaths)
                if (Directory.Exists(path)) return path;
            return "";
        }

        // ─── Bulk import ───────────────────────────────────────────────────────

        /// <summary>Scans the directory, imports all XML files not yet parsed.</summary>
        /// <returns>Number of sessions imported (player found).</returns>
        public static int ScanAndImport(string resultsDir, string playerName,
                                        Action<string> log = null)
        {
            if (!Directory.Exists(resultsDir))
            {
                Log(log, "[LmuXml] Results directory not found: {0}", resultsDir);
                return 0;
            }
            if (string.IsNullOrWhiteSpace(playerName))
            {
                Log(log, "[LmuXml] Player name is empty — cannot import sessions");
                return 0;
            }

            string[] files;
            try { files = Directory.GetFiles(resultsDir, "*.xml", SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                Log(log, "[LmuXml] Cannot list files: {0}", ex.Message);
                return 0;
            }

            int imported = 0;
            int skipped  = 0;

            foreach (var file in files)
            {
                var fileId = Path.GetFileNameWithoutExtension(file);
                if (LmuSessionStore.IsFileParsed(fileId)) { skipped++; continue; }

                try
                {
                    LmuSessionRecord session;
                    List<LmuLapRecord> laps;
                    if (ParseFile(file, playerName, out session, out laps))
                    {
                        LmuSessionStore.SaveSession(session, laps);
                        imported++;
                    }
                    else
                    {
                        // Player not found — mark as parsed so we don't re-read
                        LmuSessionStore.MarkFileParsed(fileId);
                    }
                }
                catch (Exception ex)
                {
                    Log(log, "[LmuXml] Error parsing {0}: {1}", fileId, ex.Message);
                    LmuSessionStore.MarkFileParsed(fileId); // avoid infinite retry
                }
            }

            Log(log,
                "[LmuXml] Scan done — {0} imported, {1} skipped (total files: {2})",
                imported, skipped, files.Length);
            return imported;
        }

        // ─── Live watcher ──────────────────────────────────────────────────────

        public static void StartWatcher(string resultsDir, string playerName)
        {
            StopWatcher();
            if (!Directory.Exists(resultsDir) || string.IsNullOrWhiteSpace(playerName)) return;

            _watchPlayerName = playerName;
            _watcher = new FileSystemWatcher(resultsDir, "*.xml")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnFileCreated;
            _watcher.Changed += OnFileChanged;
        }

        public static void StopWatcher()
        {
            if (_watcher == null) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileCreated;
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        private static void OnFileCreated(object sender, FileSystemEventArgs e)
            { ImportSingleFile(e.FullPath); }

        private static void OnFileChanged(object sender, FileSystemEventArgs e)
            { ImportSingleFile(e.FullPath); }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Importing =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static void ImportSingleFile(string filePath)
        {
            var fileId = Path.GetFileNameWithoutExtension(filePath);
            if (LmuSessionStore.IsFileParsed(fileId)) return;
            if (!Importing.TryAdd(fileId, true)) return;   // the other event of the same write
            try { ImportOnce(filePath, fileId); }
            finally { bool x; Importing.TryRemove(fileId, out x); }
        }

        private static void ImportOnce(string filePath, string fileId)
        {

            // Small delay so the game finishes writing the file
            System.Threading.Thread.Sleep(2000);
            try
            {
                LmuSessionRecord session;
                List<LmuLapRecord> laps;
                if (ParseFile(filePath, _watchPlayerName, out session, out laps))
                {
                    LmuSessionStore.SaveSession(session, laps);
                    LmuTeamSync.ExportAsync();
                    // A results file is written when a session ends: its telemetry can be extracted
                    TelemetryService.RequestAfterSession();
                    System.Threading.Tasks.Task.Run(() => NotificationService.Check());
                }
                else
                    LmuSessionStore.MarkFileParsed(fileId);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    string.Format("[LmuXml] Watcher error on {0}: {1}", fileId, ex.Message));
                LmuSessionStore.MarkFileParsed(fileId);
            }
        }

        // ─── Single file parse ─────────────────────────────────────────────────

        public static bool ParseFile(string filePath, string playerName,
                                     out LmuSessionRecord session,
                                     out List<LmuLapRecord> laps)
        {
            session = null;
            laps    = new List<LmuLapRecord>();

            var fileId = Path.GetFileNameWithoutExtension(filePath);

            XDocument xdoc;
            try { xdoc = XDocument.Load(filePath); }
            catch { return false; }

            // Root is <rFactorXML>; header fields live in <RaceResults> child
            var root   = xdoc.Root;
            var header = root.Element("RaceResults") ?? root;

            // ── Session date from filename + XML override ──────────────────────
            var sessionDate = ExtractDateFromFilename(fileId);
            var dtElem      = header.Element("DateTime");
            if (dtElem != null)
            {
                double unix;
                if (double.TryParse(dtElem.Value, NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out unix) && unix > 0)
                    sessionDate = DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime;
            }

            session = new LmuSessionRecord
            {
                FileId          = fileId,
                SessionType     = ExtractSessionType(fileId),
                SessionDate     = sessionDate,
                TrackVenue      = GetText(header, "TrackVenue"),
                TrackCourse     = GetText(header, "TrackCourse"),
                TrackEvent      = GetText(header, "TrackEvent"),
                TrackLengthM    = GetFloat(header, "TrackLength"),
                Setting         = GetText(header, "Setting"),
                ServerName      = GetText(header, "ServerName"),
                RaceTimeMinutes = GetFloat(header, "RaceTime"),
                RaceLaps        = GetInt(header,   "RaceLaps"),
                DamageMult      = GetFloatDef(header, "DamageMult",   1f),
                FuelMult        = GetFloatDef(header, "FuelMult",     1f),
                TireMult        = GetFloatDef(header, "TireMult",     1f),
                MechFailRate    = GetFloat(header, "MechFailRate"),
                PlayerName      = playerName,
            };

            // ── Locate the session block — LMU numbers practice blocks ("Practice1") ──
            XElement block = null;
            foreach (var child in header.Elements())
            {
                var n = child.Name.LocalName;
                if ((n.StartsWith("Race")    || n.StartsWith("Qualify") ||
                     n.StartsWith("Practice") || n.StartsWith("Warmup")) &&
                    child.Element("Driver") != null)
                { block = child; break; }
            }
            if (block == null) block = header;

            double blockUnix;
            var blockDt = block.Element("DateTime");
            if (blockDt != null && double.TryParse(blockDt.Value, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out blockUnix) && blockUnix > 0)
                session.BlockStartUtc = DateTimeOffset.FromUnixTimeSeconds((long)blockUnix).UtcDateTime;

            // ── Count player incidents and penalties from Stream ───────────────
            var stream = block.Element("Stream") ?? header.Element("Stream");

            // A results file written after joining a session already running (after a driver
            // swap, a reconnection) has its <DateTime> at the join, while lap times ("et") count
            // from the session start: move the start back so both share the session clock.
            double firstEt = FirstEventTime(stream);
            if (session.BlockStartUtc != DateTime.MinValue && firstEt > 60)
            {
                session.BlockStartUtc = session.BlockStartUtc.AddSeconds(-firstEt);
                // A race is shown at its start, the same for every driver of the car
                if (session.IsRace) session.SessionDate = session.BlockStartUtc;
            }
            if (stream != null)
            {
                foreach (var ev in stream.Elements("Incident"))
                    if (StartsWithName(ev.Value, playerName)) session.IncidentCount++;
                foreach (var ev in stream.Elements("Penalty"))
                    if (StartsWithName(ev.Value, playerName)) session.PenaltyCount++;
            }

            // ── Find the player's <Driver> element ────────────────────────────
            XElement playerDriver = null;
            foreach (var drv in block.Elements("Driver"))
            {
                var nameEl = drv.Element("Name");
                if (nameEl == null) continue;
                var drvName = nameEl.Value.Trim();
                // Exact match first, then contains (clan tag / server suffix)
                if (string.Equals(drvName, playerName, StringComparison.OrdinalIgnoreCase) ||
                    drvName.IndexOf(playerName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    playerName.IndexOf(drvName,  StringComparison.OrdinalIgnoreCase) >= 0)
                { playerDriver = drv; break; }
            }

            // Shared car (endurance): LMU writes one result per car, under the name of the driver
            // at the finish. The player's car is the one whose slot they drove.
            var carNames = CarNames(stream, playerName);
            if (playerDriver == null && carNames.Count > 0)
                foreach (var drv in block.Elements("Driver"))
                {
                    var drvName = (GetText(drv, "Name") ?? "").Trim();
                    bool swapped = false;
                    foreach (var sw in drv.Elements("Swap")) if (NameMatches(sw.Value.Trim(), playerName)) swapped = true;
                    if (swapped || carNames.Contains(drvName)) { playerDriver = drv; break; }
                }

            if (playerDriver == null)
            {
                session = null;
                return false;
            }
            carNames.Add(playerDriver.Element("Name").Value.Trim());
            foreach (var sw in playerDriver.Elements("Swap"))
                if (sw.Value.Trim().Length > 0 && sw.Value.Trim() != "???") carNames.Add(sw.Value.Trim());

            // ── Parse driver result ────────────────────────────────────────────
            session.CarType       = GetText(playerDriver, "CarType");
            session.CarClass      = GetText(playerDriver, "CarClass");
            session.TeamName      = GetText(playerDriver, "TeamName");
            session.GridPos       = GetInt(playerDriver,  "GridPos");
            session.ClassGridPos  = GetInt(playerDriver,  "ClassGridPos");
            session.Position      = GetInt(playerDriver,  "Position");
            session.ClassPosition = GetInt(playerDriver,  "ClassPosition");
            session.TotalLaps     = GetInt(playerDriver,  "Laps");
            session.Pitstops      = GetInt(playerDriver,  "Pitstops");
            session.FinishStatus  = GetText(playerDriver, "FinishStatus");
            session.DNFReason     = GetText(playerDriver, "DNFReason");
            session.ControlAndAids= GetText(playerDriver, "ControlAndAids");

            int carNum;
            int.TryParse(GetText(playerDriver, "CarNumber"), out carNum);
            session.CarNumber = carNum;

            var bestLapSec = GetFloat(playerDriver, "BestLapTime");
            session.BestLapTime = bestLapSec > 0
                ? TimeSpan.FromSeconds(bestLapSec) : TimeSpan.Zero;

            var finishSec = GetFloat(playerDriver, "FinishTime");
            session.FinishTime = finishSec > 0
                ? TimeSpan.FromSeconds(finishSec) : TimeSpan.Zero;

            // ── Parse per-lap data ─────────────────────────────────────────────
            foreach (var lapEl in playerDriver.Elements("Lap"))
                laps.Add(ParseLapElement(lapEl, fileId));
            session.CarLaps = laps.Count;
            // A lap without official time lasted until the car's next lap started (kept, since
            // a shared car's driver may not have the next lap)
            for (int i = 0; i + 1 < laps.Count; i++)
            {
                var l = laps[i];
                if (l.IsValid || l.LapTimeTicks > 0 || l.ElapsedTime < 1f) continue;
                double sec = laps[i + 1].ElapsedTime - l.ElapsedTime;
                if (sec > 10 && sec < 3600) l.ReconstructedTicks = TimeSpan.FromSeconds(sec).Ticks;
            }

            // ── Shared car: keep the player's own laps ─────────────────────────
            string driverName = playerDriver.Element("Name").Value.Trim();
            List<XElement> streams = null;
            if (carNames.Count > 1 || session.IsRace)
                streams = SessionStreams(filePath, session, stream);

            // ── Race: every car, to compare strategies ────────────────────────
            if (session.IsRace)
            {
                try { session.Field = RaceFieldBuilder.Build(block, streams, playerDriver); }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[LmuXml] Race field: " + ex.Message); }
            }

            var allLaps = laps;
            Dictionary<int, string> owners = null;
            if (carNames.Count > 1)
            {
                owners = LapOwners(streams, carNames, laps);
                if (owners != null)
                {
                    var counts = new Dictionary<string, int>();
                    var own = new List<LmuLapRecord>();
                    string ownName = null;
                    foreach (var l in laps)
                    {
                        string o;
                        if (!owners.TryGetValue(l.LapNum, out o)) continue;
                        int c; counts.TryGetValue(o, out c); counts[o] = c + 1;
                        if (NameMatches(o, playerName)) { own.Add(l); ownName = o; }
                    }
                    if (counts.Count > 1)
                    {
                        var parts = new List<string>();
                        foreach (var kv in counts) parts.Add(string.Format("{0} {1}", kv.Key, kv.Value));
                        session.TeamDrivers = string.Join(" · ", parts);
                        laps = own;
                        session.TotalLaps = own.Count;
                        long best = 0;
                        foreach (var l in own) if (l.IsValid && l.LapTimeTicks > 0 && (best == 0 || l.LapTimeTicks < best)) best = l.LapTimeTicks;
                        session.BestLapTime = TimeSpan.FromTicks(best);
                        if (ownName != null) driverName = ownName;
                        if (own.Count == 0) { session = null; return false; }
                    }
                }
            }

            if (stream != null)
                AttachStreamEvents(stream, driverName, laps);

            // ── Race: the whole car, shared with the crew ──────────────────────
            // Every session the car was shared in (practice, qualifying, race), and every race
            if ((session.IsRace || !string.IsNullOrEmpty(session.TeamDrivers)) && allLaps.Count > 0)
            {
                try
                {
                    session.Race = BuildTeamRace(session, allLaps, owners, driverName, stream);
                    // How early this player's logs start: the earliest knows who drove each lap best
                    double from = double.MaxValue;
                    foreach (var st in streams ?? new List<XElement> { stream }) if (st != null) from = Math.Min(from, FirstEventTime(st));
                    session.Race.LogFrom = from == double.MaxValue ? 0 : from;
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[LmuXml] Team race: " + ex.Message); }
            }

            return true;
        }

        private static TeamRace BuildTeamRace(LmuSessionRecord session, List<LmuLapRecord> allLaps,
                                              Dictionary<int, string> owners, string driverName, XElement stream)
        {
            var race = new TeamRace { Field = session.Field };
            var byOwner = new Dictionary<string, List<LmuLapRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in allLaps)
            {
                string o;
                if (owners == null || !owners.TryGetValue(l.LapNum, out o)) o = driverName;
                race.Laps.Add(l);
                race.Owners.Add(o);
                List<LmuLapRecord> list;
                if (!byOwner.TryGetValue(o, out list)) byOwner[o] = list = new List<LmuLapRecord>();
                list.Add(l);
            }
            // The teammates' race-control events (the player's are already attached)
            if (stream != null)
                foreach (var kv in byOwner)
                    if (!NameMatches(kv.Key, driverName)) AttachStreamEvents(stream, kv.Key, kv.Value);

            var field = session.Field;
            session.Field = null;
            try { race.Session = Newtonsoft.Json.JsonConvert.DeserializeObject<LmuSessionRecord>(Newtonsoft.Json.JsonConvert.SerializeObject(session)); }
            finally { session.Field = field; }
            race.Session.Race = null;
            race.Session.TotalLaps = allLaps.Count;
            race.Session.CarLaps = allLaps.Count;
            race.Session.PlayerName = null;
            return race;
        }

        // ─── Shared cars ───────────────────────────────────────────────────────

        private static readonly Regex ScoreRx  = new Regex(@"^\s*(.+?)\((\d+)\) lap=", RegexOptions.Compiled);
        private static readonly Regex ChangeRx = new Regex("Slot=(\\d+) Vehicle=\"[^\"]*\" Old=\"([^\"]*)\" New=\"([^\"]*)\"", RegexOptions.Compiled);

        private static bool NameMatches(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                   a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Session time of the first logged event. A few events can carry a bogus time (2 s in a
        /// log starting at 7486 s): the first time followed by a steady run of events counts.
        /// </summary>
        private static double FirstEventTime(XElement stream)
        {
            if (stream == null) return 0;
            var times = new List<double>();
            foreach (var ev in stream.Elements())
            {
                float et = AttrFloat(ev, "et", -1f);
                if (et >= 0) times.Add(et);
            }
            if (times.Count == 0) return 0;
            times.Sort();
            for (int i = 0; i < times.Count; i++)
            {
                int j = Math.Min(times.Count - 1, i + 5);
                if (j == i || times[j] - times[i] < 120) return times[i];
            }
            return times[0];
        }

        /// <summary>Everyone who drove the player's car (same slot) in this file's log.</summary>
        private static HashSet<string> CarNames(XElement stream, string playerName)
        {
            var bySlot = new Dictionary<string, HashSet<string>>();
            Action<string, string> add = (slot, name) =>
            {
                HashSet<string> set;
                if (!bySlot.TryGetValue(slot, out set)) bySlot[slot] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(name.Trim());
            };
            if (stream != null)
                foreach (var ev in stream.Elements())
                {
                    var n = ev.Name.LocalName;
                    if (n == "Score")
                    {
                        var m = ScoreRx.Match(ev.Value ?? "");
                        if (m.Success) add(m.Groups[2].Value, m.Groups[1].Value);
                    }
                    else if (n == "DriverChange")
                    {
                        var m = ChangeRx.Match(ev.Value ?? "");
                        if (m.Success) { add(m.Groups[1].Value, m.Groups[2].Value); add(m.Groups[1].Value, m.Groups[3].Value); }
                    }
                }
            foreach (var kv in bySlot)
                foreach (var name in kv.Value)
                    if (NameMatches(name, playerName)) return kv.Value;
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The session's logs: this file's, then those of the other results files of the same
        /// session. A file only logs what happened after the player joined, and LMU writes one at
        /// each swap or reconnection.
        /// </summary>
        private static List<XElement> SessionStreams(string filePath, LmuSessionRecord session, XElement stream)
        {
            var streams = new List<XElement>();
            if (stream != null) streams.Add(stream);
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                var mine = File.GetLastWriteTimeUtc(filePath);
                var suffix = session.SessionType.Substring(0, 1);
                foreach (var other in Directory.GetFiles(dir, "*.xml"))
                {
                    if (string.Equals(other, filePath, StringComparison.OrdinalIgnoreCase)) continue;
                    var id = Path.GetFileNameWithoutExtension(other);
                    if (id.Length < 2 || id.Substring(id.Length - 2, 1) != suffix) continue;
                    if (Math.Abs((File.GetLastWriteTimeUtc(other) - mine).TotalHours) > session.RaceTimeMinutes / 60 + 2) continue;
                    XDocument doc;
                    try { doc = XDocument.Load(other); } catch { continue; }
                    var h = doc.Root.Element("RaceResults") ?? doc.Root;
                    if (GetText(h, "TrackVenue") != session.TrackVenue || GetText(h, "TrackCourse") != session.TrackCourse) continue;
                    XElement b = null;
                    foreach (var child in h.Elements())
                        if (child.Name.LocalName.StartsWith(session.IsRace ? "Race" : session.IsQuali ? "Qualify" : "Practice")
                            && child.Element("DateTime") != null && (child.Element("Stream") != null || child.Element("Driver") != null))
                        { b = child; break; }
                    if (b == null) continue;
                    var st = b.Element("Stream") ?? h.Element("Stream");
                    double unix;
                    var dt = b.Element("DateTime");
                    if (st == null || dt == null || !double.TryParse(dt.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out unix)) continue;
                    var start = DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime.AddSeconds(-FirstEventTime(st));
                    if (Math.Abs((start - session.BlockStartUtc).TotalMinutes) > 3) continue;
                    streams.Add(st);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[LmuXml] Session files: " + ex.Message);
            }
            return streams;
        }

        /// <summary>
        /// Who drove each lap of a shared car, from the session's logs: lap crossings ("Name(slot)
        /// lap=N") and driver swaps. Null when nothing tells.
        /// </summary>
        private static Dictionary<int, string> LapOwners(List<XElement> streams, HashSet<string> names, List<LmuLapRecord> laps)
        {
            var events = new List<KeyValuePair<double, string>>();   // from this time on, this driver
            var before = new List<KeyValuePair<double, string>>();   // until this time, this driver (swap "Old")
            foreach (var st in streams)
            {
                foreach (var ev in st.Elements())
                {
                    float et = AttrFloat(ev, "et", -1f);
                    if (et < 0) continue;
                    var n = ev.Name.LocalName;
                    if (n == "Score")
                    {
                        var m = ScoreRx.Match(ev.Value ?? "");
                        if (m.Success && names.Contains(m.Groups[1].Value.Trim()))
                            events.Add(new KeyValuePair<double, string>(et, m.Groups[1].Value.Trim()));
                    }
                    else if (n == "DriverChange")
                    {
                        var m = ChangeRx.Match(ev.Value ?? "");
                        if (!m.Success) continue;
                        string oldName = m.Groups[2].Value.Trim(), newName = m.Groups[3].Value.Trim();
                        if (!names.Contains(oldName) && !names.Contains(newName)) continue;
                        events.Add(new KeyValuePair<double, string>(et, newName));
                        before.Add(new KeyValuePair<double, string>(et, oldName));
                    }
                }
            }

            if (events.Count == 0) return null;
            events.Sort((a, b) => a.Key.CompareTo(b.Key));
            before.Sort((a, b) => a.Key.CompareTo(b.Key));

            var owners = new Dictionary<int, string>();
            foreach (var l in laps)
            {
                double at = l.ElapsedTime + 1;   // a lap belongs to whoever was driving as it began
                string owner = null;
                foreach (var e in events) { if (e.Key <= at) owner = e.Value; else break; }
                if (owner == null)
                {
                    // Before the first logged event: the driver handing over at the first swap,
                    // or the first driver seen crossing the line
                    foreach (var bf in before) if (bf.Key > at) { owner = bf.Value; break; }
                    if (owner == null) owner = events[0].Value;
                }
                owners[l.LapNum] = owner;
            }
            return owners;
        }

        /// <summary>
        /// Assigns the player's race-control events to the lap during which they happened
        /// (track limits with their outcome, contacts, penalties), using the stream timestamps.
        /// </summary>
        private static void AttachStreamEvents(XElement stream, string driverName, List<LmuLapRecord> laps)
        {
            if (laps.Count == 0) return;
            var ordered = new List<LmuLapRecord>(laps);
            ordered.Sort((a, b) => a.LapNum.CompareTo(b.LapNum));

            Func<XElement, LmuLapRecord> lapAt = ev =>
            {
                float et = AttrFloat(ev, "et", -1f);
                if (et < 0) return null;
                LmuLapRecord hit = ordered[0];
                foreach (var l in ordered) if (l.ElapsedTime > 0 && l.ElapsedTime <= et) hit = l;
                return hit;
            };

            foreach (var ev in stream.Elements())
            {
                var name = ev.Name.LocalName;
                if (name == "TrackLimits")
                {
                    if (!string.Equals(Attr(ev, "Driver"), driverName, StringComparison.OrdinalIgnoreCase)) continue;
                    var lap = lapAt(ev);
                    if (lap == null) continue;
                    var outcome = (ev.Value ?? "").Trim();
                    if (outcome.StartsWith("Invalid Lap", StringComparison.OrdinalIgnoreCase)) lap.LimitsInvalid++;
                    else if (outcome.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) lap.LimitsWarning++;
                    else if (outcome.IndexOf("Penalty", StringComparison.OrdinalIgnoreCase) >= 0)
                        lap.Penalty = lap.Penalty ?? "Pénalité (limites de piste)";
                    else lap.LimitsNoAction++;
                }
                else if (name == "Incident")
                {
                    var text = ev.Value ?? "";
                    if (!text.StartsWith(driverName + "(", StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.IndexOf("reported contact", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var lap = lapAt(ev);
                    if (lap != null) lap.Contacts++;
                }
                else if (name == "Penalty")
                {
                    var text = (ev.Value ?? "").Trim();
                    if (!text.StartsWith(driverName + " received", StringComparison.OrdinalIgnoreCase)) continue;
                    var lap = lapAt(ev);
                    if (lap == null) continue;
                    // "X received Drive Thru penalty, 0s, 0laps for "Track Limits". Result: …"
                    int start = driverName.Length + " received ".Length;
                    int cut = text.IndexOf(". Result", StringComparison.Ordinal);
                    lap.Penalty = cut > start ? text.Substring(start, cut - start) : text.Substring(start);
                }
            }
        }

        // ─── <Lap> parsing ─────────────────────────────────────────────────────

        private static LmuLapRecord ParseLapElement(XElement el, string sessionId)
        {
            var lap = new LmuLapRecord { SessionId = sessionId };

            int lapNum; int.TryParse(Attr(el, "num"), out lapNum);
            lap.LapNum = lapNum;

            int pos; int.TryParse(Attr(el, "p"), out pos);
            lap.Position = pos;

            float et; float.TryParse(Attr(el, "et"), NumberStyles.Any,
                                     CultureInfo.InvariantCulture, out et);
            lap.ElapsedTime = et;

            lap.S1          = AttrFloat(el, "s1",        -1f);
            lap.S2          = AttrFloat(el, "s2",        -1f);
            lap.S3          = AttrFloat(el, "s3",        -1f);
            lap.TopSpeedKph = AttrFloat(el, "topspeed",   0f);
            lap.FuelRemaining     = AttrFloat(el, "fuel",    0f);
            lap.FuelUsed          = AttrFloat(el, "fuelUsed",0f);
            lap.VirtualEnergy     = AttrFloat(el, "ve",      0f);
            lap.VirtualEnergyUsed = AttrFloat(el, "veUsed",  0f);
            lap.TireWearFL = AttrFloat(el, "twfl", 0f);
            lap.TireWearFR = AttrFloat(el, "twfr", 0f);
            lap.TireWearRL = AttrFloat(el, "twrl", 0f);
            lap.TireWearRR = AttrFloat(el, "twrr", 0f);
            lap.FrontCompound = Attr(el, "fcompound") ?? "";
            lap.RearCompound  = Attr(el, "rcompound") ?? "";
            lap.IsPitLap      = Attr(el, "pit") == "1";

            // Lap time from inner text: "157.1803" or "--.----"
            var timeText = (el.Value ?? "").Trim();
            if (timeText != "--.----" && timeText != "--.-" && timeText.Length > 0)
            {
                float sec;
                if (float.TryParse(timeText, NumberStyles.Any,
                                   CultureInfo.InvariantCulture, out sec) && sec > 0)
                {
                    lap.LapTimeTicks = TimeSpan.FromSeconds(sec).Ticks;
                    lap.IsValid      = true;
                }
            }

            return lap;
        }

        // ─── Filename helpers ──────────────────────────────────────────────────

        private static string ExtractSessionType(string fileId)
        {
            if (fileId.EndsWith("R1", StringComparison.OrdinalIgnoreCase)) return "R1";
            if (fileId.EndsWith("R2", StringComparison.OrdinalIgnoreCase)) return "R2";
            if (fileId.EndsWith("Q1", StringComparison.OrdinalIgnoreCase)) return "Q1";
            if (fileId.EndsWith("Q2", StringComparison.OrdinalIgnoreCase)) return "Q2";
            if (fileId.EndsWith("P1", StringComparison.OrdinalIgnoreCase)) return "P1";
            if (fileId.EndsWith("P2", StringComparison.OrdinalIgnoreCase)) return "P2";
            return "P1";
        }

        private static DateTime ExtractDateFromFilename(string fileId)
        {
            // Pattern: 2025_03_15_15_53_17-03R1
            if (fileId.Length >= 19)
            {
                DateTime dt;
                if (DateTime.TryParseExact(fileId.Substring(0, 19),
                    "yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt))
                    return dt;
            }
            return DateTime.MinValue;
        }

        // ─── XML helpers ───────────────────────────────────────────────────────

        private static string GetText(XElement parent, string name)
        {
            var el = parent == null ? null : parent.Element(name);
            return el == null ? "" : (el.Value ?? "").Trim();
        }

        private static float GetFloat(XElement parent, string name)
        {
            float v; float.TryParse(GetText(parent, name), NumberStyles.Any,
                                     CultureInfo.InvariantCulture, out v); return v;
        }

        private static float GetFloatDef(XElement parent, string name, float def)
        {
            var s = GetText(parent, name);
            if (string.IsNullOrEmpty(s)) return def;
            float v;
            return float.TryParse(s, NumberStyles.Any,
                                  CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static int GetInt(XElement parent, string name)
        {
            int v; int.TryParse(GetText(parent, name), out v); return v;
        }

        private static string Attr(XElement el, string name)
        {
            var a = el.Attribute(name); return a == null ? null : a.Value;
        }

        private static float AttrFloat(XElement el, string name, float def)
        {
            var s = Attr(el, name);
            if (s == null) return def;
            float v;
            return float.TryParse(s, NumberStyles.Any,
                                  CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static bool StartsWithName(string text, string name)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name)) return false;
            return text.StartsWith(name, StringComparison.OrdinalIgnoreCase);
        }

        private static void Log(Action<string> log, string format, params object[] args)
        {
            var msg = string.Format(format, args);
            SimHub.Logging.Current.Info(msg);
            if (log != null) log(msg);
        }
    }
}
