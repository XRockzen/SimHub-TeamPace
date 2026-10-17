using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace User.TeamPace
{
    /// <summary>A lap with telemetry, tied to its result-file session and lap when they exist.</summary>
    public class TeleLapRef
    {
        public DriverView       Driver;
        public TeleFileHeader   File;
        public TeleLapInfo      Lap;
        public LmuSessionRecord Session;   // null when no result file matches the recording
        public LmuLapRecord     XmlLap;    // null when the results have no such lap

        public int  LapNum     { get { return Lap.LapNum; } }
        public bool IsOfficial { get { return XmlLap != null && XmlLap.LapTimeTicks > 0; } }

        /// <summary>Official time when LMU timed the lap, measured from the recording otherwise.</summary>
        public double LapSec { get { return IsOfficial ? XmlLap.LapTime.TotalSeconds : Lap.LapSec; } }

        /// <summary>Clean comparison lap: complete, not a pit lap, and valid when results are known.</summary>
        public bool IsClean
        {
            get
            {
                if (!Lap.IsComplete || Lap.IsInLap) return false;
                return XmlLap == null || (XmlLap.IsValid && XmlLap.LapTimeTicks > 0 && !XmlLap.IsPitLap);
            }
        }

        public DateTime Date
        {
            get
            {
                if (Session != null && Session.SessionDate != DateTime.MinValue) return Session.SessionDate;
                var utc = File.RecordingUtc;
                return utc == DateTime.MinValue ? DateTime.MinValue : utc.ToLocalTime();
            }
        }

        public string SessionKey { get { return Session != null ? "S|" + Session.FileId : "F|" + File.Path; } }

        public string SessionLabel
        {
            get
            {
                string type = Session != null ? Session.SessionTypeLong
                            : File.SessionType.StartsWith("Race", StringComparison.OrdinalIgnoreCase) ? "Course"
                            : File.SessionType.StartsWith("Qual", StringComparison.OrdinalIgnoreCase) ? "Qualif." : "Essais";
                return string.Format("{0} du {1:dd/MM/yy · HH:mm}", type, Date);
            }
        }

        /// <summary>Why the lap is not a clean one, or null.</summary>
        public string Remark
        {
            get
            {
                if (Lap.IsOutLap) return "sortie";
                if (Lap.IsInLap) return "stand";
                if (!Lap.IsComplete) return "incomplet";
                if (XmlLap != null && (!XmlLap.IsValid || XmlLap.LapTimeTicks <= 0)) return "invalide";
                return null;
            }
        }
    }

    /// <summary>
    /// Telemetry: extraction of LMU's recordings by TeamPace.Extractor.exe (64-bit, embedded in
    /// the plugin), sharing of the extracted laps through the team folder, and lookup of the laps
    /// of a driver on a circuit.
    /// </summary>
    public static class TelemetryService
    {
        private const string ExtractorName = "TeamPace.Extractor.exe";
        private const string FolderName    = "telemetry";
        private const string Ext           = ".tpt";

        private static string _dataDir;
        private static readonly object RunLock = new object();
        private static int _afterSessionPending;

        public static event Action StatusChanged;
        public static string Status { get; private set; }

        public static void Init(string dataDir)
        {
            _dataDir = dataDir;
        }

        private static void SetStatus(string s)
        {
            Status = s;
            var h = StatusChanged;
            if (h != null) h();
        }

        // ─── Paths ────────────────────────────────────────────────────────────

        /// <summary>LMU install folder, from the results folder (…\UserData\Log\Results).</summary>
        public static string LmuRoot
        {
            get
            {
                var path = LmuSessionStore.ResultsPath;
                if (string.IsNullOrEmpty(path)) path = LmuXmlParser.AutoDiscoverResultsPath();
                if (string.IsNullOrEmpty(path)) return null;
                var dir = new DirectoryInfo(path);
                while (dir != null && !string.Equals(dir.Name, "UserData", StringComparison.OrdinalIgnoreCase))
                    dir = dir.Parent;
                return dir != null && dir.Parent != null ? dir.Parent.FullName : null;
            }
        }

        private static string OwnFolderName { get { return LmuTeamSync.Sanitize(LmuSessionStore.PlayerName); } }
        private static string OwnDir        { get { return Path.Combine(_dataDir, FolderName, OwnFolderName); } }
        private static string TeamLocalRoot { get { return Path.Combine(_dataDir, "team", FolderName); } }
        private static string SharedRoot    { get { return Path.Combine(LmuSessionStore.TeamFolder ?? "", FolderName); } }

        public static string DirFor(TeamDriver d)
        {
            return d.DbPath == null ? OwnDir : Path.Combine(TeamLocalRoot, LmuTeamSync.Sanitize(d.Name));
        }

        // ─── Extraction ───────────────────────────────────────────────────────

        public class Result
        {
            public int    Extracted, Laps, Locked, Failed;
            public string Error;
        }

        /// <summary>
        /// Extracts every LMU recording not processed yet. Blocking; one run at a time.
        /// Recordings still open in the game are reported as locked and picked up next time.
        /// </summary>
        public static Result Extract()
        {
            var r = new Result();
            if (_dataDir == null || string.IsNullOrWhiteSpace(LmuSessionStore.PlayerName))
            {
                r.Error = "nom de joueur non configuré";
                return r;
            }
            var root = LmuRoot;
            var dll  = root != null ? Path.Combine(root, "duckdb.dll") : null;
            var src  = root != null ? Path.Combine(root, "UserData", "Telemetry") : null;
            if (dll == null || !File.Exists(dll) || !Directory.Exists(src))
            {
                r.Error = "télémétrie LMU introuvable (dossier UserData\\Telemetry)";
                SetStatus("Télémétrie : " + r.Error);
                return r;
            }
            if (!Environment.Is64BitOperatingSystem)
            {
                r.Error = "Windows 64 bits requis";
                return r;
            }

            lock (RunLock)
            {
                try
                {
                    var exe = EnsureExtractor();
                    Directory.CreateDirectory(OwnDir);
                    SetStatus("Télémétrie : recherche des nouveaux relais…");

                    var psi = new ProcessStartInfo(exe,
                        string.Format("\"{0}\" \"{1}\" \"{2}\"", dll, src.TrimEnd('\\'), OwnDir.TrimEnd('\\')))
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        // Below the game and SimHub: it may run while the player drives
                        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                        var errors = new System.Text.StringBuilder();
                        p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                        p.BeginErrorReadLine();
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                        {
                            if (line.StartsWith("progress ")) SetStatus("Télémétrie : extraction " + line.Substring(9) + "…");
                            else if (line.StartsWith("done ")) ParseDone(line, r);
                        }
                        if (!p.WaitForExit(30 * 60 * 1000)) { try { p.Kill(); } catch { } r.Error = "délai dépassé"; }
                        else if (p.ExitCode == 1) r.Error = errors.ToString().Trim();
                        if (errors.Length > 0)
                            SimHub.Logging.Current.Warn("[TeamPace] Extractor: " + errors.ToString().Trim());
                    }
                }
                catch (Exception ex)
                {
                    r.Error = ex.Message;
                    SimHub.Logging.Current.Warn("[TeamPace] Telemetry extraction failed: " + ex);
                }
            }

            if (r.Error != null) SetStatus("Télémétrie : échec, " + r.Error);
            else SetStatus(r.Extracted > 0
                ? string.Format("Télémétrie : {0} tour(s) extrait(s)", r.Laps)
                : "Télémétrie à jour");
            return r;
        }

        private static void ParseDone(string line, Result r)
        {
            foreach (var part in line.Substring(5).Split(' '))
            {
                var kv = part.Split('=');
                int v;
                if (kv.Length != 2 || !int.TryParse(kv[1], out v)) continue;
                switch (kv[0])
                {
                    case "extracted": r.Extracted = v; break;
                    case "laps":      r.Laps = v;      break;
                    case "locked":    r.Locked = v;    break;
                    case "failed":    r.Failed = v;    break;
                }
            }
        }

        /// <summary>Writes the embedded extractor next to the plugin data when missing or outdated.</summary>
        private static string EnsureExtractor()
        {
            var dir = Path.Combine(_dataDir, "bin");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ExtractorName);
            byte[] bytes;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExtractorName))
            {
                if (s == null) throw new FileNotFoundException("extracteur absent de la DLL");
                bytes = new byte[s.Length];
                int read = 0;
                while (read < bytes.Length) read += s.Read(bytes, read, bytes.Length - read);
            }
            bool same = File.Exists(path) && new FileInfo(path).Length == bytes.Length;
            if (same)
            {
                var existing = File.ReadAllBytes(path);
                for (int i = 0; i < bytes.Length && same; i++) same = existing[i] == bytes[i];
            }
            if (!same) File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>
        /// End of a session: the last stint's recording is still open in the game for a while,
        /// so retry until it can be read (up to half an hour).
        /// </summary>
        public static void RequestAfterSession()
        {
            if (Interlocked.Exchange(ref _afterSessionPending, 1) == 1) return;
            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(30000);
                    for (int attempt = 0; attempt < 15; attempt++)
                    {
                        var r = Extract();
                        if (r.Laps > 0) ExportOwn();
                        if (r.Error != null || r.Locked == 0) break;
                        Thread.Sleep(120000);
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn("[TeamPace] After-session telemetry: " + ex.Message);
                }
                finally { Interlocked.Exchange(ref _afterSessionPending, 0); }
            });
        }

        // ─── Team sharing ─────────────────────────────────────────────────────

        /// <summary>Publishes and fetches extracted laps. Returns the number of files received.</summary>
        public static int SyncTeam()
        {
            ExportOwn();
            return PullTeam();
        }

        /// <summary>Copies own recordings that are new or changed to the team folder.</summary>
        public static void ExportOwn()
        {
            if (!LmuTeamSync.IsConfigured || !Directory.Exists(OwnDir)) return;
            try
            {
                var dest = Path.Combine(SharedRoot, OwnFolderName);
                Directory.CreateDirectory(dest);
                foreach (var file in Directory.GetFiles(OwnDir, "*" + Ext))
                {
                    var target = Path.Combine(dest, Path.GetFileName(file));
                    if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length) continue;
                    if (!HasLaps(file)) continue;
                    CopyAtomic(file, target);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TeamPace] Telemetry export: " + ex.Message);
            }
        }

        public static int PullTeam()
        {
            if (!LmuTeamSync.IsConfigured || !Directory.Exists(SharedRoot)) return 0;
            int count = 0;
            try
            {
                foreach (var driverDir in Directory.GetDirectories(SharedRoot))
                {
                    var name = Path.GetFileName(driverDir);
                    if (string.Equals(name, OwnFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    var local = Path.Combine(TeamLocalRoot, name);
                    Directory.CreateDirectory(local);
                    int received = 0;
                    foreach (var file in Directory.GetFiles(driverDir, "*" + Ext))
                    {
                        var target = Path.Combine(local, Path.GetFileName(file));
                        try
                        {
                            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length) continue;
                            CopyAtomic(file, target);
                            if (!HasLaps(target)) { File.Delete(target); continue; }   // partial download
                            count++;
                            received++;
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn("[TeamPace] Telemetry pull " + name + ": " + ex.Message);
                        }
                    }
                    NotificationService.AddTelemetry(name.Replace('_', ' '), received);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TeamPace] Telemetry pull: " + ex.Message);
            }
            return count;
        }

        private static void CopyAtomic(string src, string dest)
        {
            var tmp = dest + ".tmp";
            File.Copy(src, tmp, true);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
        }

        private static bool HasLaps(string file)
        {
            try { return TeleFormat.ReadHeader(file).Laps.Count > 0; }
            catch { return false; }
        }

        // ─── Lookup ───────────────────────────────────────────────────────────

        private class CachedHeader { public DateTime Stamp; public long Size; public TeleFileHeader Header; }

        private static readonly Dictionary<string, CachedHeader> Headers = new Dictionary<string, CachedHeader>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<LmuLapRecord>> SessionLaps = new Dictionary<string, List<LmuLapRecord>>();

        /// <summary>Every lap with telemetry of a driver on a circuit (result-file track name).</summary>
        public static List<TeleLapRef> GetLaps(DriverView d, string circuit)
        {
            var list = new List<TeleLapRef>();
            var dir = DirFor(d.Driver);
            if (_dataDir == null || !Directory.Exists(dir)) return list;

            foreach (var file in Directory.GetFiles(dir, "*" + Ext))
            {
                var h = Header(file);
                if (h == null || h.Laps.Count == 0) continue;

                LmuSessionRecord session;
                List<LmuLapRecord> xmlLaps;
                MatchSession(d, h, out session, out xmlLaps);
                if (session != null
                        ? !string.Equals(session.TrackDisplayName, circuit, StringComparison.OrdinalIgnoreCase)
                        : !CircuitMatches(h, circuit))
                    continue;

                foreach (var lap in h.Laps)
                {
                    var r = new TeleLapRef { Driver = d, File = h, Lap = lap, Session = session };
                    if (xmlLaps != null)
                        foreach (var x in xmlLaps)
                            if (SameLap(x, lap)) { r.XmlLap = x; break; }
                    list.Add(r);
                }
            }
            return list;
        }

        public static bool HasAny(DriverView d)
        {
            var dir = DirFor(d.Driver);
            return _dataDir != null && Directory.Exists(dir) && Directory.GetFiles(dir, "*" + Ext).Length > 0;
        }

        public static TeleTrace LoadTrace(TeleLapRef r)
        {
            return TeleFormat.ReadTrace(r.File, r.Lap);
        }

        private static TeleFileHeader Header(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                CachedHeader c;
                lock (Headers)
                    if (Headers.TryGetValue(file, out c) && c.Stamp == fi.LastWriteTimeUtc && c.Size == fi.Length) return c.Header;
                var h = TeleFormat.ReadHeader(file);
                lock (Headers) Headers[file] = new CachedHeader { Stamp = fi.LastWriteTimeUtc, Size = fi.Length, Header = h };
                return h;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TeamPace] Unreadable telemetry " + Path.GetFileName(file) + ": " + ex.Message);
                return null;
            }
        }

        private static bool SameLap(LmuLapRecord x, TeleLapInfo lap)
        {
            return x.LapNum == lap.LapNum && Math.Abs(x.ElapsedTime - lap.StartEt) < 1.0;
        }

        /// <summary>
        /// The recording and the results share the session clock: the session whose laps start
        /// at the same session times as the recorded laps is the one.
        /// </summary>
        private static void MatchSession(DriverView d, TeleFileHeader h, out LmuSessionRecord best, out List<LmuLapRecord> bestLaps)
        {
            best = null; bestLaps = null;
            int bestCount = 0;
            var rec = h.RecordingUtc;
            foreach (var s in d.Sessions)
            {
                var start = s.BlockStartUtc != DateTime.MinValue ? s.BlockStartUtc
                          : s.SessionDate != DateTime.MinValue ? s.SessionDate.ToUniversalTime() : DateTime.MinValue;
                if (rec != DateTime.MinValue && start != DateTime.MinValue &&
                    (rec < start.AddHours(-1) || rec > start.AddHours(26))) continue;

                var laps = LapsOf(d, s);
                int count = 0;
                foreach (var lap in h.Laps)
                    foreach (var x in laps)
                        if (SameLap(x, lap)) { count++; break; }
                if (count > bestCount) { bestCount = count; best = s; bestLaps = laps; }
            }
        }

        private static List<LmuLapRecord> LapsOf(DriverView d, LmuSessionRecord s)
        {
            var key = (d.DbPath ?? "") + "|" + s.FileId;
            List<LmuLapRecord> laps;
            lock (SessionLaps)
                if (SessionLaps.TryGetValue(key, out laps)) return laps;
            laps = LmuSessionStore.GetLapsForSession(s.FileId, d.DbPath);
            lock (SessionLaps) SessionLaps[key] = laps;
            return laps;
        }

        /// <summary>Recording not tied to a result file: compare the track names.</summary>
        private static bool CircuitMatches(TeleFileHeader h, string circuit)
        {
            if (string.IsNullOrEmpty(circuit)) return false;
            foreach (var name in new[] { h.Layout, h.Track })
                if (!string.IsNullOrEmpty(name) &&
                    (string.Equals(name, circuit, StringComparison.OrdinalIgnoreCase) ||
                     circuit.EndsWith(" – " + name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            return false;
        }

        // ─── Track model ──────────────────────────────────────────────────────

        private static readonly Dictionary<string, TrackModel> Models = new Dictionary<string, TrackModel>();

        /// <summary>
        /// Centre line, edges and corners of a circuit, rebuilt from the fastest clean laps of the
        /// whole team (displayed or not, up to 40), cached until the laps change.
        /// </summary>
        public static TrackModel TrackModelFor(string circuit, IEnumerable<TeleLapRef> known)
        {
            var laps = new List<TeleLapRef>();
            var seen = new HashSet<string>();
            Action<TeleLapRef> add = l =>
            {
                if (l.IsClean && l.File.Version >= 2 && seen.Add(l.File.Path + "#" + l.LapNum + "#" + l.Lap.StartEt)) laps.Add(l);
            };
            var shown = new HashSet<string>();
            foreach (var l in known) { add(l); shown.Add(l.Driver.Key); }
            foreach (var mate in LmuTeamSync.GetDrivers())
            {
                if (shown.Contains(mate.DbPath ?? "")) continue;
                foreach (var l in GetLaps(new DriverView(mate, DriverView.Palette[0]), circuit)) add(l);
            }
            if (laps.Count == 0) return null;
            laps.Sort((a, b) => a.LapSec.CompareTo(b.LapSec));
            if (laps.Count > 40) laps.RemoveRange(40, laps.Count - 40);

            var key = circuit + "|" + laps.Count + "|" + laps[0].File.Path + "|" + laps[laps.Count - 1].File.Path;
            TrackModel model;
            lock (Models) if (Models.TryGetValue(key, out model)) return model;

            var traces = new List<TeleTrace>();
            double length = 0;
            foreach (var l in laps)
            {
                try
                {
                    var t = LoadTrace(l);
                    // A recording whose lap distance jumped would bend the track
                    if (t.FirstDist > 10 || Math.Abs(t.Time[t.N - 1] - l.LapSec) > 0.6) continue;
                    traces.Add(t);
                    length = Math.Max(length, l.File.TrackLength);
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Track model: " + ex.Message); }
            }
            try { model = TrackModel.Build(traces, length); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TeamPace] Track model failed: " + ex);
                model = null;
            }
            lock (Models) Models[key] = model;
            return model;
        }

        /// <summary>Forget cached session laps (after an import or a team sync).</summary>
        public static void Invalidate()
        {
            lock (SessionLaps) SessionLaps.Clear();
        }
    }
}
