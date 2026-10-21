using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;

namespace User.TeamPace
{
    [PluginDescription("Le Mans Ultimate : comparez votre rythme à celui de vos coéquipiers, session par session et tour par tour")]
    [PluginAuthor("XRockzen")]
    [PluginName("TeamPace")]
    public class TeamPacePlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        internal List<BestLapRecord> AllRecords;
        internal bool   IsLoading;
        internal bool   IsImporting;
        internal string LoadError;
        internal string PluginsDataPath;
        internal string LastGame;
        internal int    LmuSessionImportedCount;

        private BestLapRecord _currentRecord;
        private string        _lastKey;
        private string        _lastTrack, _lastCar, _lastGame;

        // ─── Invalid lap tracking ─────────────────────────────────────────────
        private int    _lastCompletedLaps  = -1;
        private string _lastTrackForSession = null;
        private string _sessionId           = null;   // our own session GUID

        private readonly LmuLiveTracker _liveTracker = new LmuLiveTracker();
        private bool _overlayFailed;

        public TeamPacePlugin()
        {
            AllRecords = new List<BestLapRecord>();
            IsLoading  = true;
        }

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon  { get { return PluginIcon.Image; } }
        public string      LeftMenuTitle{ get { return "TeamPace"; } }

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[TeamPace] Starting plugin");

            this.AttachDelegate("CurrentBestLap",    () => _currentRecord != null ? _currentRecord.BestLapFormatted    : "-");
            this.AttachDelegate("CurrentBestRhythm", () => _currentRecord != null ? _currentRecord.BestRhythmFormatted : "-");
            this.AttachDelegate("CurrentTotalLaps",  () => _currentRecord != null ? _currentRecord.TotalLaps : 0);
            this.AttachDelegate("IsLoading",         () => IsLoading);

            var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            PluginsDataPath = Path.Combine(exeDir, "PluginsData");

            var commonPath = Path.Combine(PluginsDataPath, "Common");
            InvalidLapStore.Init(commonPath);
            LmuSessionStore.Init(commonPath);
            var dataDir = PluginPaths.DataDir(commonPath);
            TelemetryService.Init(dataDir);
            NotificationService.Init(dataDir);
            StrategyEngine.Init(dataDir);
            OverlayService.Init(dataDir);

            // Wheel buttons for the overlay (SimHub › Controls and events)
            this.AddAction("Overlay.PageSuivante", (a, b) => OverlayService.NextPage(1), (a, b) => { });
            this.AddAction("Overlay.PagePrecedente", (a, b) => OverlayService.NextPage(-1), (a, b) => { });
            this.AddAction("Overlay.Action", (a, b) => OverlayService.PageAction(), (a, b) => { });
            this.AddAction("Overlay.Afficher", (a, b) => OverlayService.ToggleVisible(), (a, b) => { });
            this.AddAction("Pitwall.Afficher", (a, b) => OverlayService.TogglePitwall(), (a, b) => { });
            this.AttachDelegate("Overlay.Delta", () => OverlayService.Delta);
            this.AttachDelegate("Overlay.Page", () => OverlaySettings.PageName((OverlayPage)OverlaySettings.Current.Page));

            var dataPath = PluginsDataPath;
            Task.Run(() =>
            {
                try
                {
                    AllRecords = RecordsService.LoadAllRecords(dataPath);
                    var newest = AllRecords.OrderByDescending(r => r.RecordDate).FirstOrDefault();
                    LastGame = newest != null ? newest.Game : null;
                    SimHub.Logging.Current.Info(
                        string.Format("[TeamPace] Loaded {0} records — last game: {1}", AllRecords.Count, LastGame));
                }
                catch (Exception ex)
                {
                    LoadError = ex.Message;
                    SimHub.Logging.Current.Error(string.Format("[TeamPace] Load error: {0}", ex));
                }
                finally { IsLoading = false; }
            });

            // LMU XML import (background, does not block plugin startup)
            Task.Run(() => StartLmuImport());

            PluginUpdater.CleanupPrevious();
            PluginUpdater.CheckInBackground();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (data.GameRunning && data.GameName == "LMU")
                _liveTracker.Update(data);
            if (data.GameName == "LMU")
            {
                // The overlay must never disturb SimHub's loop: an error is logged once
                try { OverlayService.Update(data); }
                catch (Exception ex) { if (!_overlayFailed) { _overlayFailed = true; SimHub.Logging.Current.Error("[TeamPace] Overlay: " + ex); } }
            }

            if (IsLoading || data.NewData == null) return;

            // ── Current-record tracking (sidebar delegate) ────────────────────
            var track = data.NewData.TrackName;
            var car   = data.NewData.CarModel;
            if (!string.IsNullOrEmpty(track) && !string.IsNullOrEmpty(car) &&
                (track != _lastTrack || car != _lastCar || data.GameName != _lastGame))   // no string built every frame
            {
                _lastTrack = track; _lastCar = car; _lastGame = data.GameName;
                {
                    _currentRecord = AllRecords.FirstOrDefault(r =>
                        string.Equals(r.Track, track, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(r.CarId,          car, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(r.CarDisplayName, car, StringComparison.OrdinalIgnoreCase)));
                }
            }

            // ── Invalid lap capture ───────────────────────────────────────────
            TrackInvalidLaps(data);
        }

        private void TrackInvalidLaps(GameData data)
        {
            var newData = data.NewData;
            if (newData == null) return;

            string lapTrack = newData.TrackName ?? "";
            int completedLaps = newData.CompletedLaps;

            // Detect session change: new track or lap counter reset
            bool sessionChanged = (lapTrack != _lastTrackForSession)
                               || (completedLaps >= 0 && _lastCompletedLaps >= 0 && completedLaps < _lastCompletedLaps);

            if (sessionChanged)
            {
                _sessionId           = Guid.NewGuid().ToString("N");
                _lastCompletedLaps   = -1;
                _lastTrackForSession = lapTrack;
            }

            // Negative means game not active / no data yet
            if (completedLaps < 0)
            {
                _lastCompletedLaps = -1;
                return;
            }

            // Detect lap completion (CompletedLaps increased by at least 1)
            if (_lastCompletedLaps >= 0 && completedLaps > _lastCompletedLaps)
            {
                TimeSpan lastLapTime = newData.LastLapTime;
                bool isInvalid = lastLapTime.Ticks <= 0;

                if (isInvalid)
                {
                    string carId = newData.CarId ?? newData.CarModel ?? "";
                    string game  = data.GameName ?? "";

                    if (!string.IsNullOrEmpty(lapTrack) && !string.IsNullOrEmpty(carId) && !string.IsNullOrEmpty(game))
                    {
                        // Written off the game loop: a database write here would stutter the frame
                        int lapNo = completedLaps; string sid = _sessionId ?? ""; var at = DateTime.UtcNow;
                        Task.Run(() => InvalidLapStore.AddLap(game, lapTrack, carId, lapNo, sid, at));

                        SimHub.Logging.Current.Info(
                            string.Format("[TeamPace] Captured invalid lap #{0} — {1} / {2}",
                                completedLaps, lapTrack, carId));
                    }
                }
            }

            _lastCompletedLaps = completedLaps;
        }

        public void End(PluginManager pluginManager)
        {
            LmuXmlParser.StopWatcher();
            OverlayService.Shutdown();
            if (_teamWatch != null) _teamWatch.Dispose();
            LmuTeamSync.Export();
        }

        // ─── LMU XML import ───────────────────────────────────────────────────

        private void StartLmuImport()
        {
            IsImporting = true;
            try
            {
                var resultsPath = LmuSessionStore.ResultsPath;
                var playerName  = LmuSessionStore.PlayerName;

                // Auto-discover path if not configured
                if (string.IsNullOrEmpty(resultsPath))
                    resultsPath = LmuXmlParser.AutoDiscoverResultsPath();

                if (string.IsNullOrEmpty(resultsPath))
                {
                    SimHub.Logging.Current.Info(
                        "[TeamPace] LMU results path not found — set it in plugin settings");
                    return;
                }

                if (string.IsNullOrEmpty(playerName))
                {
                    SimHub.Logging.Current.Info(
                        "[TeamPace] LMU player name not set — configure it in plugin settings");
                    return;
                }

                int imported = LmuXmlParser.ScanAndImport(resultsPath, playerName);
                LmuSessionImportedCount = LmuSessionStore.GetSessionCount();

                // Watch for new sessions after a race
                LmuXmlParser.StartWatcher(resultsPath, playerName);

                LmuTeamSync.PullTeammates();
                LmuTeamSync.Export();
                TelemetryService.PullTeam();
                NotificationService.Check();
                StartTeamWatch();

                if (imported > 0)
                    SimHub.Logging.Current.Info(
                        string.Format("[TeamPace] LMU import done: {0} new sessions", imported));
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    string.Format("[TeamPace] LMU import error: {0}", ex.Message));
            }
            finally
            {
                IsImporting = false;
            }
        }

        private System.Threading.Timer _teamWatch;

        /// <summary>
        /// While SimHub runs, fetch the teammates' news every 5 minutes so a record beaten by a
        /// teammate pops up without waiting for the next start.
        /// </summary>
        private void StartTeamWatch()
        {
            if (_teamWatch != null) return;
            _teamWatch = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (!LmuTeamSync.IsConfigured) return;
                    int pulled = LmuTeamSync.PullTeammates();
                    if (pulled > 0 | TelemetryService.PullTeam() > 0) TelemetryService.Invalidate();
                    if (pulled > 0) { LmuSessionStore.Touch(); NotificationService.Check(); }   // nothing new: nothing to read
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Team watch: " + ex.Message); }
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>Imports new LMU results now (blocking).</summary>
        internal void RunLmuImport()
        {
            StartLmuImport();
        }

        internal void TriggerLmuRescan()
        {
            Task.Run(() => StartLmuImport());
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new TeamPaceControl(this);
        }

        internal void Reload()
        {
            IsLoading = true;
            LoadError = null;
            AllRecords = new List<BestLapRecord>();
            LastGame   = null;
            _lastKey   = null;
            _lastTrack = _lastCar = _lastGame = null;
            _currentRecord = null;

            var dataPath = PluginsDataPath;
            Task.Run(() =>
            {
                try
                {
                    AllRecords = RecordsService.LoadAllRecords(dataPath);
                    var newest = AllRecords.OrderByDescending(r => r.RecordDate).FirstOrDefault();
                    LastGame = newest != null ? newest.Game : null;
                }
                catch (Exception ex) { LoadError = ex.Message; }
                finally { IsLoading = false; }
            });
        }
    }
}
