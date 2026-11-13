using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace User.TeamPace
{
    public class TeamDriver
    {
        public string   Name       { get; set; }
        public string   DbPath     { get; set; }   // null = own database
        public DateTime LastUpdate { get; set; }

        public override string ToString()
        {
            if (DbPath == null) return Name + "  (moi)";
            return LastUpdate == DateTime.MinValue
                ? Name
                : string.Format("{0}  (maj {1:dd/MM HH:mm})", Name, LastUpdate);
        }
    }

    /// <summary>
    /// Shares session databases through a synced folder (Google Drive, OneDrive…):
    /// each plugin publishes its own database as "&lt;player&gt;.lmu.db" and keeps a local,
    /// read-only copy of every teammate's file.
    /// </summary>
    public static class LmuTeamSync
    {
        private const string Extension = ".lmu.db";
        private static int _exporting;

        private static string LocalTeamDir
        {
            get
            {
                var dir = Path.Combine(Path.GetDirectoryName(LmuSessionStore.OwnDbPath), "team");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string OwnFileName
        {
            get { return Sanitize(LmuSessionStore.PlayerName) + Extension; }
        }

        /// <summary>Why the team folder cannot be used, or null.</summary>
        public static string Problem { get; private set; }

        public static bool IsConfigured
        {
            get
            {
                var folder = LmuSessionStore.TeamFolder;
                if (string.IsNullOrEmpty(folder) || string.IsNullOrWhiteSpace(LmuSessionStore.PlayerName)) { Problem = null; return false; }
                if (Directory.Exists(folder)) { Problem = null; return true; }

                // Google Drive for desktop may mount on another letter after a restart (J: → I:)
                var moved = Relocate(folder);
                if (moved != null)
                {
                    SimHub.Logging.Current.Info(string.Format("[LmuTeamSync] Team folder moved: {0} → {1}", folder, moved));
                    LmuSessionStore.SaveTeamFolder(moved);
                    Problem = null;
                    return true;
                }
                Problem = string.Format("Dossier d'équipe introuvable ({0}) : Google Drive est-il lancé ?", folder);
                return false;
            }
        }

        /// <summary>The same folder on another drive letter, or null.</summary>
        private static string Relocate(string folder)
        {
            try
            {
                var root = Path.GetPathRoot(folder);
                if (string.IsNullOrEmpty(root)) return null;
                var rest = folder.Substring(root.Length);
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || string.Equals(drive.RootDirectory.FullName, root, StringComparison.OrdinalIgnoreCase)) continue;
                    var candidate = Path.Combine(drive.RootDirectory.FullName, rest);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[LmuTeamSync] Relocate: " + ex.Message);
            }
            return null;
        }

        // ─── Export ───────────────────────────────────────────────────────────

        public static void ExportAsync()
        {
            if (!IsConfigured) return;
            Task.Run(() => Export());
        }

        /// <summary>Publishes the own database. Returns an error message, or null on success.</summary>
        public static string Export()
        {
            if (!IsConfigured) return "Dossier d'équipe ou nom de joueur non configuré";
            if (Interlocked.Exchange(ref _exporting, 1) == 1) return null;
            try
            {
                var dest = Path.Combine(LmuSessionStore.TeamFolder, OwnFileName);
                // Write next to the target then swap, so the sync client never uploads a half file
                var tmp = dest + ".tmp";
                LmuSessionStore.CopyOwnDatabase(tmp);
                if (!OverlaySettings.Current.ShareHeart) LmuSessionStore.StripHeartRate(tmp);   // kept on this PC only
                if (!File.Exists(dest)) { File.Move(tmp, dest); return null; }
                try { File.Replace(tmp, dest, null); }
                catch (IOException)
                {
                    // Some virtual drives (Drive for desktop) refuse Replace
                    File.Copy(tmp, dest, true);
                    File.Delete(tmp);
                }
                return null;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(string.Format("[LmuTeamSync] Export error: {0}", ex.Message));
                return ex.Message;
            }
            finally { Interlocked.Exchange(ref _exporting, 0); }
        }

        // ─── Import ───────────────────────────────────────────────────────────

        /// <summary>Refreshes local copies of teammates' files that changed in the team folder.</summary>
        public static int PullTeammates()
        {
            if (!IsConfigured) return 0;
            int updated = 0;
            string[] files;
            try { files = Directory.GetFiles(LmuSessionStore.TeamFolder, "*" + Extension); }
            catch { return 0; }

            foreach (var src in files)
            {
                var name = Path.GetFileName(src);
                if (string.Equals(name, OwnFileName, StringComparison.OrdinalIgnoreCase)) continue;

                var local = Path.Combine(LocalTeamDir, name);
                try
                {
                    if (File.Exists(local) &&
                        File.GetLastWriteTimeUtc(local) >= File.GetLastWriteTimeUtc(src)) continue;

                    // Copy to a temp file first: a file still being downloaded must not
                    // replace a good local copy.
                    var tmp = local + ".tmp";
                    File.Copy(src, tmp, true);
                    if (string.IsNullOrEmpty(LmuSessionStore.ReadPlayerName(tmp)))
                    {
                        File.Delete(tmp);
                        continue;
                    }
                    if (File.Exists(local)) File.Delete(local);
                    File.Move(tmp, local);
                    File.SetLastWriteTimeUtc(local, File.GetLastWriteTimeUtc(src));
                    updated++;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        string.Format("[LmuTeamSync] Pull error on {0}: {1}", name, ex.Message));
                }
            }
            return updated;
        }

        /// <summary>Own database first, then every teammate copy available locally.</summary>
        public static List<TeamDriver> GetDrivers()
        {
            var list = new List<TeamDriver>
            {
                new TeamDriver { Name = string.IsNullOrEmpty(LmuSessionStore.PlayerName)
                                        ? "Moi" : LmuSessionStore.PlayerName }
            };
            if (string.IsNullOrEmpty(LmuSessionStore.OwnDbPath)) return list;

            foreach (var file in Directory.GetFiles(LocalTeamDir, "*" + Extension))
            {
                if (string.Equals(Path.GetFileName(file), OwnFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = LmuSessionStore.ReadPlayerName(file);
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new TeamDriver
                {
                    Name       = name,
                    DbPath     = file,
                    LastUpdate = File.GetLastWriteTime(file),
                });
            }
            return list;
        }

        internal static string Sanitize(string name)
        {
            var s = string.IsNullOrWhiteSpace(name) ? "pilote" : name.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}
