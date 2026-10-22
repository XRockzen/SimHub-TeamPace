using System;
using System.IO;

namespace User.TeamPace
{
    public enum OverlayPage { Telemetry = 0, Coaching = 1, Race = 2, Plan = 3 }

    /// <summary>The kind of session being driven: each one shows its own pages.</summary>
    public enum SessionKind { Practice = 0, Qualifying = 1, Race = 2 }

    /// <summary>Everything the in-race overlay can be set to, saved in the plugin data folder (overlay.json).</summary>
    public class OverlaySettings
    {
        public const double BaseWidth = 760, BaseHeight = 150;
        public const double PitWidth = 900, PitHeight = 250;

        // Display
        public bool   Enabled  { get; set; }
        public double Left     { get; set; }
        public double Top      { get; set; }
        public double Scale    { get; set; }   // 1 = 760 × 150 px
        public double Opacity  { get; set; }   // background, 0..1
        public bool[] SessionPages { get; set; }   // pages shown in each kind of session: [kind * 4 + page]
        public int    Page     { get; set; }

        public bool Shows(SessionKind kind, OverlayPage page) { return SessionPages[(int)kind * 4 + (int)page]; }

        /// <summary>Practice: telemetry, coaching, work plan. Qualifying: telemetry. Race: telemetry and race plan.</summary>
        public static bool[] DefaultSessionPages()
        {
            return new[]
            {
                true, true,  false, true,    // practice
                true, false, false, false,   // qualifying
                true, false, true,  false,   // race
            };
        }

        // Telemetry
        public double WindowSec { get; set; }  // seconds shown by the scrolling curves
        public bool   Ghost     { get; set; }

        // Reference: "ideal" (team ideal lap), "mine" (my best lap), "team" (the team's best lap), "driver" (one driver's best)
        public string Reference       { get; set; }
        public string ReferenceDriver { get; set; }
        public string Circuit         { get; set; }   // null = the circuit being driven
        public string ClassLabel      { get; set; }

        // Coaching
        public bool   Voice          { get; set; }
        public int    CoachCorners   { get; set; }
        public double AnnounceMeters { get; set; }   // the instruction shows this far before the ideal braking point

        // Race
        public string StrategyPlan { get; set; }      // null = the team's latest plan for the circuit

        // Pitwall: for the teammate following the race in the session, on his own computer
        public bool   PitwallEnabled { get; set; }
        public double PitLeft        { get; set; }
        public double PitTop         { get; set; }
        public double PitScale       { get; set; }
        public double PitW           { get; set; }   // window size, px
        public double PitH           { get; set; }

        public static OverlaySettings Default()
        {
            return new OverlaySettings
            {
                Enabled = false, Left = double.NaN, Top = 24, Scale = 1, Opacity = 0.88,
                SessionPages = DefaultSessionPages(), Page = 0,
                WindowSec = 8, Ghost = true,
                Reference = "ideal", Voice = false, CoachCorners = 3, AnnounceMeters = 300,
                PitwallEnabled = false, PitLeft = double.NaN, PitTop = 200, PitScale = 1, PitW = 1300, PitH = 900,
            };
        }

        /// <summary>Values missing or out of range in a file saved by an older version.</summary>
        public void Normalize()
        {
            if (SessionPages == null || SessionPages.Length != 12) SessionPages = DefaultSessionPages();
            if (!(Scale >= 0.4 && Scale <= 3)) Scale = 1;
            if (!(PitScale >= 0.4 && PitScale <= 3)) PitScale = 1;
            if (!(PitW >= 500)) PitW = 1300;
            if (!(PitH >= 300)) PitH = 900;
            if (!(Opacity >= 0 && Opacity <= 1)) Opacity = 0.88;
            if (!(WindowSec >= 2 && WindowSec <= 30)) WindowSec = 8;
            if (string.IsNullOrEmpty(Reference)) Reference = "ideal";
            if (CoachCorners < 1 || CoachCorners > 3) CoachCorners = 3;
            if (!(AnnounceMeters >= 100 && AnnounceMeters <= 800)) AnnounceMeters = 300;
            if (Page < 0 || Page > 3) Page = 0;
        }

        private static string _path;
        private static OverlaySettings _current;
        public static event Action Changed;

        public static OverlaySettings Current
        {
            get
            {
                if (_current == null) _current = Default();
                return _current;
            }
        }

        public static void Load(string dataDir)
        {
            _path = Path.Combine(dataDir, "overlay.json");
            OverlaySettings s = null;
            try { if (File.Exists(_path)) s = Newtonsoft.Json.JsonConvert.DeserializeObject<OverlaySettings>(File.ReadAllText(_path)); }
            catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Overlay settings: " + ex.Message); }
            _current = s ?? Default();
            _current.Normalize();
        }

        /// <summary>Writes the settings and tells the overlay.</summary>
        public static void Save()
        {
            var s = Current;
            s.Normalize();
            if (_path != null)
                try { File.WriteAllText(_path, Newtonsoft.Json.JsonConvert.SerializeObject(s, Newtonsoft.Json.Formatting.Indented)); }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Overlay settings save: " + ex.Message); }
            var h = Changed;
            if (h != null) h();
        }

        public static string PageName(OverlayPage p)
        {
            switch (p)
            {
                case OverlayPage.Telemetry: return "Télémétrie";
                case OverlayPage.Coaching:  return "Coaching";
                case OverlayPage.Race:      return "Course";
                default:                    return "Plan de travail";
            }
        }

        public static string SessionName(SessionKind k)
        {
            return k == SessionKind.Practice ? "Essais" : k == SessionKind.Qualifying ? "Qualif" : "Course";
        }

        public static string ReferenceName(string kind, string driver)
        {
            switch (kind)
            {
                case "mine":   return "Mon meilleur tour";
                case "team":   return "Meilleur tour de l'équipe";
                case "driver": return "Meilleur tour de " + (driver ?? "?");
                default:       return "Idéal de l'équipe";
            }
        }
    }
}
