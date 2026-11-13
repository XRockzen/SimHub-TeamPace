using System;

namespace User.TeamPace
{
    public class LmuLapRecord
    {
        public string SessionId          { get; set; }
        public int    LapNum             { get; set; }
        public long   LapTimeTicks       { get; set; }   // TimeSpan.Ticks
        public bool   IsValid            { get; set; }
        public int    Position           { get; set; }   // race position at end of lap

        // ─── Sectors (seconds; -1 = not recorded / invalid) ──────────────────
        public float  S1                 { get; set; }
        public float  S2                 { get; set; }
        public float  S3                 { get; set; }

        // ─── Physics ─────────────────────────────────────────────────────────
        public float  TopSpeedKph        { get; set; }
        public float  FuelRemaining      { get; set; }   // fraction of tank 0–1
        public float  FuelUsed           { get; set; }   // fraction of tank, this lap
        public float  VirtualEnergy      { get; set; }   // fraction 0–1 remaining
        public float  VirtualEnergyUsed  { get; set; }   // fraction, this lap
        public float  TireWearFL         { get; set; }   // fraction 0–1 remaining
        public float  TireWearFR         { get; set; }
        public float  TireWearRL         { get; set; }
        public float  TireWearRR         { get; set; }
        public string FrontCompound      { get; set; }   // e.g. "1,Wet"
        public string RearCompound       { get; set; }

        // ─── Session context ─────────────────────────────────────────────────
        public bool   IsPitLap           { get; set; }
        public float  ElapsedTime        { get; set; }   // seconds from block start to lap start

        // ─── Race-control events during the lap (from the XML stream) ────────
        public int    LimitsInvalid      { get; set; }   // "Invalid Lap Cut Track / Off Track"
        public int    LimitsWarning      { get; set; }   // "Warning"
        public int    LimitsNoAction     { get; set; }   // "No Further Action"
        public int    Contacts           { get; set; }   // "reported contact"
        public string Penalty            { get; set; }   // penalty received during the lap, if any

        // Invalid laps: duration rebuilt from the next lap's start (display only, never in stats)
        public long   ReconstructedTicks { get; set; }

        // ─── Live capture, merged at display time (not persisted with XML laps) ──
        public bool   HasLive            { get; set; }
        public float  LiveFuelUsedL      { get; set; }   // litres burnt this lap
        public float  LiveFuelLevelL     { get; set; }   // litres at lap end
        public float  LiveWearFL         { get; set; }   // % wear this lap
        public float  LiveWearFR         { get; set; }
        public float  LiveWearRL         { get; set; }
        public float  LiveWearRR         { get; set; }
        public LmuLivePit LivePit        { get; set; }   // pit stop entered during this lap
        public LmuLiveLap Live           { get; set; }   // what was recorded live (heart rate among it)

        // ─── Computed helpers ─────────────────────────────────────────────────
        public TimeSpan LapTime  { get { return TimeSpan.FromTicks(LapTimeTicks); } }
        public bool HasS1 { get { return S1 > 0; } }
        public bool HasS2 { get { return S2 > 0; } }
        public bool HasS3 { get { return S3 > 0; } }
        public bool HasAllSectors { get { return HasS1 && HasS2 && HasS3; } }

        public float Theoretical
        {
            get { return HasAllSectors ? S1 + S2 + S3 : -1f; }
        }

        public string LapTimeFormatted
        {
            get { return LapTimeTicks <= 0 ? "—" : BestLapRecord.FormatLapTime(LapTime); }
        }

        public string S1Formatted { get { return S1 > 0 ? S1.ToString("F3") : "—"; } }
        public string S2Formatted { get { return S2 > 0 ? S2.ToString("F3") : "—"; } }
        public string S3Formatted { get { return S3 > 0 ? S3.ToString("F3") : "—"; } }
    }
}
