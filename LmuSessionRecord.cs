using System;

namespace User.TeamPace
{
    public class LmuSessionRecord
    {
        // ─── Identity ─────────────────────────────────────────────────────────
        public string   FileId          { get; set; }  // filename without ext → DB _id
        public string   SessionType     { get; set; }  // "P1", "Q1", "R1"
        public DateTime SessionDate     { get; set; }
        public DateTime BlockStartUtc   { get; set; }  // lap "et" values are relative to this

        // ─── Track / event ────────────────────────────────────────────────────
        public string   TrackVenue      { get; set; }
        public string   TrackCourse     { get; set; }
        public string   TrackEvent      { get; set; }
        public float    TrackLengthM    { get; set; }

        // ─── Session conditions ───────────────────────────────────────────────
        public string   Setting         { get; set; }  // "Multiplayer" / "Solo"
        public string   ServerName      { get; set; }
        public float    RaceTimeMinutes { get; set; }
        public int      RaceLaps        { get; set; }
        public float    DamageMult      { get; set; }
        public float    FuelMult        { get; set; }
        public float    TireMult        { get; set; }
        public float    MechFailRate    { get; set; }

        // ─── Player car / team ────────────────────────────────────────────────
        public string   PlayerName      { get; set; }
        public string   CarType         { get; set; }
        public string   CarClass        { get; set; }
        public int      CarNumber       { get; set; }
        public string   TeamName        { get; set; }

        // ─── Player result ────────────────────────────────────────────────────
        public int      GridPos         { get; set; }
        public int      ClassGridPos    { get; set; }
        public int      Position        { get; set; }
        public int      ClassPosition   { get; set; }
        public TimeSpan BestLapTime     { get; set; }
        public TimeSpan FinishTime      { get; set; }
        public int      TotalLaps       { get; set; }
        public int      Pitstops        { get; set; }
        public int      CarLaps         { get; set; }  // laps of the whole car (team races: every driver)
        public string   TeamDrivers     { get; set; }  // "Amaury Marin 74 · Pascal Marin 52" when the car was shared
        public RaceField Field          { get; set; }  // races: every car, stored apart (LmuSessionStore.GetField)
        [Newtonsoft.Json.JsonIgnore]
        public TeamRace  Race           { get; set; }  // races: the whole car, shared with the crew (TeamRaceStore)
        public string   FinishStatus    { get; set; }
        public string   DNFReason       { get; set; }
        public string   ControlAndAids  { get; set; }

        // ─── Behaviour ────────────────────────────────────────────────────────
        public int      IncidentCount   { get; set; }
        public int      PenaltyCount    { get; set; }

        // ─── Computed helpers (not stored) ────────────────────────────────────
        public bool IsRace     { get { return SessionType != null && SessionType.StartsWith("R"); } }
        public bool IsQuali    { get { return SessionType != null && SessionType.StartsWith("Q"); } }
        public bool IsPractice { get { return !IsRace && !IsQuali; } }
        public bool IsOnline   { get { return string.Equals(Setting, "Multiplayer", StringComparison.OrdinalIgnoreCase); } }
        public bool IsCrew     { get { return !string.IsNullOrEmpty(TeamDrivers); } }
        /// <summary>"Hors ligne", "En ligne" or "Équipage" (online, the car shared with teammates).</summary>
        public string Mode     { get { return IsCrew ? "Équipage" : IsOnline ? "En ligne" : "Hors ligne"; } }
        public string ModeBg   { get { return IsCrew ? "#FF5E4B8B" : IsOnline ? "#FF1F6FB5" : "#FF55585F"; } }
        public bool IsFullSim  { get { return DamageMult >= 1.0f && FuelMult >= 1.0f && TireMult >= 1.0f; } }
        public bool IsFinished { get { return string.Equals(FinishStatus, "Finished Normally", StringComparison.OrdinalIgnoreCase); } }
        public int  PositionsGained { get { return GridPos > 0 && Position > 0 ? GridPos - Position : 0; } }

        public string SessionTypeLong
        {
            get
            {
                if (IsRace)     return "Course";
                if (IsQuali)    return "Qualif.";
                return "Essais";
            }
        }

        public string BestLapFormatted
        {
            get { return BestLapTime.Ticks <= 0 ? "—" : BestLapRecord.FormatLapTime(BestLapTime); }
        }

        public string TrackDisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(TrackCourse) && TrackCourse != TrackVenue)
                    return TrackVenue + " – " + TrackCourse;
                return string.IsNullOrEmpty(TrackVenue) ? "—" : TrackVenue;
            }
        }
    }
}
