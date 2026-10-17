using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>A driver shown as one column in the UI: identity, colour and lazily loaded sessions.</summary>
    public class DriverView
    {
        public static readonly string[] Palette = { "#FF378ADD", "#FFD85A30", "#FF1D9E75", "#FF9F8FEF" };
        public const int MaxDrivers = 4;

        public TeamDriver Driver   { get; private set; }
        public string     ColorHex { get; private set; }
        public SolidColorBrush Brush { get; private set; }

        public string Name   { get { return Driver.Name; } }
        public string DbPath { get { return Driver.DbPath; } }   // null = own database
        public string Key    { get { return Driver.DbPath ?? ""; } }

        private List<LmuSessionRecord> _sessions;

        public DriverView(TeamDriver driver, string colorHex)
        {
            Driver   = driver;
            ColorHex = colorHex;
            Brush    = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            Brush.Freeze();
        }

        public List<LmuSessionRecord> Sessions
        {
            get
            {
                if (_sessions == null)
                {
                    // Team races this driver drove in, as the crew shared them
                    _sessions = TeamRaceStore.Complete(Name, LmuSessionStore.GetAllSessions(DbPath));
                    _sessions.Sort((a, b) => b.SessionDate.CompareTo(a.SessionDate));
                }
                return _sessions;
            }
        }

        public void Invalidate() { _sessions = null; }

        public LmuSessionRecord FindMatch(LmuSessionRecord other)
        {
            foreach (var s in Sessions)
                if (SessionMatch.Same(s, other)) return s;
            return null;
        }
    }

    public static class Fmt
    {
        public static string TotalTime(TimeSpan t)
        {
            if (t <= TimeSpan.Zero) return "—";
            int h = (int)t.TotalHours, min = t.Minutes;
            if (h == 0) return string.Format("{0}min", min > 0 ? min : 1);
            if (min == 0) return string.Format("{0}h", h);
            return string.Format("{0}h {1:00}min", h, min);
        }

        public static string Lap(TimeSpan t)
        {
            return string.Format("{0}:{1:00}.{2:000}", (int)t.TotalMinutes, t.Seconds, t.Milliseconds);
        }

        public static string LapSec(double sec) { return Lap(TimeSpan.FromSeconds(sec)); }

        /// <summary>Unsigned gap: "0.312" under a minute, "1:02.312" above.</summary>
        public static string Seconds(TimeSpan t)
        {
            t = t.Duration();
            return t.TotalMinutes >= 1 ? Lap(t) : string.Format("{0}.{1:000}", t.Seconds, t.Milliseconds);
        }

        public static string Signed(TimeSpan t)
        {
            return (t.Ticks < 0 ? "-" : "+") + Seconds(t);
        }
    }

    public static class SessionMatch
    {
        private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(3);

        /// <summary>When LMU wrote the results file ("2026_10_17_13_58_35-74P1").</summary>
        private static bool FileEnd(string fileId, out DateTime end)
        {
            end = DateTime.MinValue;
            return fileId != null && fileId.Length >= 19 &&
                   DateTime.TryParseExact(fileId.Substring(0, 19), "yyyy_MM_dd_HH_mm_ss", System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.None, out end);
        }

        /// <summary>Two drivers' results describe the same event (same server session).</summary>
        public static bool Same(LmuSessionRecord a, LmuSessionRecord b)
        {
            if (a == null || b == null) return false;
            if (!string.Equals(a.TrackDisplayName, b.TrackDisplayName, StringComparison.OrdinalIgnoreCase)) return false;
            if (a.IsRace != b.IsRace || a.IsQuali != b.IsQuali) return false;
            if (a.IsRace && TeamRaceStore.SameRace(a, b)) return true;
            // Both results files written at the end of the same session (a joined practice does
            // not always tell when the session started, its end is the same for everyone)
            DateTime ea, eb;
            if (FileEnd(a.FileId, out ea) && FileEnd(b.FileId, out eb) && (ea - eb).Duration() <= TimeSpan.FromMinutes(2) &&
                (a.SessionDate - b.SessionDate).Duration() <= TimeSpan.FromMinutes(30)) return true;
            DateTime ta, tb;
            if (a.BlockStartUtc != DateTime.MinValue && b.BlockStartUtc != DateTime.MinValue)
            {
                ta = a.BlockStartUtc; tb = b.BlockStartUtc;
            }
            else
            {
                if (a.SessionDate == DateTime.MinValue || b.SessionDate == DateTime.MinValue) return false;
                ta = a.SessionDate.ToUniversalTime(); tb = b.SessionDate.ToUniversalTime();
            }
            return (ta - tb).Duration() <= Tolerance;
        }
    }
}
