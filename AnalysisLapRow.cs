using System;
using System.ComponentModel;
using System.Globalization;

namespace User.TeamPace
{
    /// <summary>Row model for the analysis lap DataGrid (view 7). Supports INotifyPropertyChanged for live stats recalc.</summary>
    public class AnalysisLapRow : INotifyPropertyChanged
    {
        public LmuLapRecord Lap { get; set; }

        // ── Checkbox ──────────────────────────────────────────────────────────

        private bool _isChecked;
        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    OnPropertyChanged("IsChecked");
                }
            }
        }

        // ── Lap identity ──────────────────────────────────────────────────────

        public int  LapNum   { get { return Lap.LapNum; } }
        public bool IsValid  { get { return Lap.IsValid; } }
        public bool IsPitLap { get { return Lap.IsPitLap; } }

        // ── Time ──────────────────────────────────────────────────────────────

        public string LapTime
        {
            get
            {
                if (!Lap.IsValid)
                    return Lap.ReconstructedTicks > 0
                        ? "(" + Fmt.Lap(TimeSpan.FromTicks(Lap.ReconstructedTicks)) + ")"
                        : "--:--.---";
                var t = Lap.LapTime;
                return string.Format("{0}:{1:00}.{2:000}",
                    (int)t.TotalMinutes, t.Seconds, t.Milliseconds);
            }
        }

        private static string Sec(float s)
        {
            if (s <= 0) return "—";
            // Same as everywhere else: "28.569" under a minute, "1:02.312" above
            return Fmt.Seconds(TimeSpan.FromSeconds(s));
        }

        public string S1 { get { return Sec(Lap.S1); } }
        public string S2 { get { return Sec(Lap.S2); } }
        public string S3 { get { return Sec(Lap.S3); } }

        // ── Fuel ──────────────────────────────────────────────────────────────

        // Live capture gives litres; LMU's XML only gives fractions of the tank.
        public bool HasLiveFuel { get { return Lap.HasLive && Lap.LiveFuelUsedL > 0; } }

        public string FuelUsed
        {
            get
            {
                if (HasLiveFuel)      return Lap.LiveFuelUsedL.ToString("F2", CultureInfo.InvariantCulture) + " L";
                if (Lap.FuelUsed > 0) return (Lap.FuelUsed * 100).ToString("F1", CultureInfo.InvariantCulture) + " %";
                return "—";
            }
        }

        public string FuelRemaining
        {
            get
            {
                if (Lap.HasLive && Lap.LiveFuelLevelL > 0) return Lap.LiveFuelLevelL.ToString("F1", CultureInfo.InvariantCulture) + " L";
                if (Lap.FuelRemaining > 0)                 return (Lap.FuelRemaining * 100).ToString("F0", CultureInfo.InvariantCulture) + " %";
                return "—";
            }
        }

        public bool   HasVe  { get { return Lap.VirtualEnergyUsed > 0; } }
        public string VeUsed { get { return HasVe ? (Lap.VirtualEnergyUsed * 100).ToString("F1", CultureInfo.InvariantCulture) + " %" : "—"; } }

        // ── Tyre wear during this lap (% of tyre, set by the builder) ─────────

        public float WearLapFL { get; set; }
        public float WearLapFR { get; set; }
        public float WearLapRL { get; set; }
        public float WearLapRR { get; set; }

        public bool HasTireData { get { return WearLapFL > 0 || WearLapFR > 0 || WearLapRL > 0 || WearLapRR > 0; } }

        public string TireWearAvg
        {
            get
            {
                if (!HasTireData) return "—";
                return ((WearLapFL + WearLapFR + WearLapRL + WearLapRR) / 4f).ToString("F2", CultureInfo.InvariantCulture) + " %";
            }
        }

        // ── Heart rate (watch, recorded live) ─────────────────────────────────

        private LmuLiveLap Hr { get { return Lap.Live != null && Lap.Live.HasHeart ? Lap.Live : null; } }
        public bool   HasHeart { get { return Hr != null; } }
        public string HeartAvg { get { return HasHeart ? Math.Round(Hr.HrAvg).ToString(CultureInfo.InvariantCulture) : "—"; } }
        public string HeartMax { get { return HasHeart ? Hr.HrMax.ToString(CultureInfo.InvariantCulture) : "—"; } }

        /// <summary>The lap's heart rate in detail, on hover.</summary>
        public string HeartTip
        {
            get
            {
                var h = Hr;
                if (h == null) return "Pas de cardio sur ce tour";
                var inv = CultureInfo.InvariantCulture;
                var lines = new System.Collections.Generic.List<string>
                {
                    string.Format(inv, "BPM moyen {0:0} · mini {1} · maxi {2}", h.HrAvg, h.HrMin, h.HrMax),
                    string.Format(inv, "Mesuré sur {0:0} % du tour", h.HrValidPct),
                };
                if (h.HrSectorAvg != null && h.HrSectorMax != null && h.HrSectorAvg.Length == 3)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    for (int s = 0; s < 3; s++)
                        parts.Add(h.HrSectorAvg[s] > 0 ? string.Format(inv, "S{0} {1:0} (maxi {2})", s + 1, h.HrSectorAvg[s], h.HrSectorMax[s]) : string.Format(inv, "S{0} —", s + 1));
                    lines.Add(string.Join(" · ", parts));
                }
                if (h.HrZoneSec != null)
                {
                    var zones = new System.Collections.Generic.List<string>();
                    for (int z = 5; z >= 0 && z < h.HrZoneSec.Length; z--)
                        if (h.HrZoneSec[z] >= 1) zones.Add(string.Format(inv, "zone {0} ({1}) {2:0} s", z, Heart.ZoneNames[z], h.HrZoneSec[z]));
                    if (zones.Count > 0) lines.Add(string.Join("\n", zones));
                }
                if (h.HrStartPeak > 0) lines.Add(string.Format(inv, "Pic au départ : {0} bpm", h.HrStartPeak));
                return string.Join("\n", lines);
            }
        }

        // ── Compound ─────────────────────────────────────────────────────────

        public string Compound { get { return Lap.FrontCompound ?? ""; } }

        // ── Visual markers ────────────────────────────────────────────────────

        public bool TireChanged { get; set; }  // compound changed vs. previous lap

        // Representative of race pace: valid, not lap 1, not a pit lap, not the lap after one
        public bool IsRhythmLap { get; set; }

        // Highlights are recomputed over the checked laps, so they need notifications.
        private bool _isBest, _isWorst, _isS1Best, _isS2Best, _isS3Best;

        public void SetHighlights(bool best, bool worst, bool s1, bool s2, bool s3)
        {
            if (best != _isBest || worst != _isWorst)
            {
                _isBest = best; _isWorst = worst;
                OnPropertyChanged("LapTimeFg");
            }
            if (s1 != _isS1Best) { _isS1Best = s1; OnPropertyChanged("S1Fg"); }
            if (s2 != _isS2Best) { _isS2Best = s2; OnPropertyChanged("S2Fg"); }
            if (s3 != _isS3Best) { _isS3Best = s3; OnPropertyChanged("S3Fg"); }
        }

        // ── Colors ───────────────────────────────────────────────────────────

        private const string ColNormal = "#DDFFFFFF";
        private const string ColBest   = "#FF4CAF50";
        private const string ColWorst  = "#FFEF5350";
        private const string ColSector = "#FFBA68C8";

        public string LapTimeFg
        {
            get
            {
                if (!IsValid) return "#66FFFFFF";
                if (_isBest)  return ColBest;
                if (_isWorst) return ColWorst;
                return ColNormal;
            }
        }

        public string S1Fg { get { return _isS1Best ? ColSector : ColNormal; } }
        public string S2Fg { get { return _isS2Best ? ColSector : ColNormal; } }
        public string S3Fg { get { return _isS3Best ? ColSector : ColNormal; } }

        public string RowBg
        {
            get
            {
                if (IsTrackInvalidated) return "#14FF4444";
                if (IsPitLap || IsOutLap) return "#0A1565B0";
                return "Transparent";
            }
        }

        // ── Gap to the fastest displayed driver on the same lap number ───────

        public string TeamDelta   { get; set; }
        public string TeamDeltaFg { get; set; }

        // ── Markers ───────────────────────────────────────────────────────────

        /// <summary>Lap leaving the garage or the pits (set by the builder).</summary>
        public bool IsOutLap { get; set; }

        /// <summary>
        /// Invalidated by race control (cut / off track). A lap without a time that is only a pit
        /// or garage lap is not counted; one without any explanation still is.
        /// </summary>
        public bool IsTrackInvalidated
        {
            get { return Lap.LimitsInvalid > 0 || (!IsValid && !IsPitLap && !IsOutLap); }
        }

        public string FlagPit     { get { return IsPitLap ? "PIT" : (IsOutLap ? "SORTIE" : ""); } }
        public string FlagTire    { get { return TireChanged ? "PNEU" : ""; } }
        public string FlagInvalid { get { return IsTrackInvalidated ? "INV" : ""; } }
        public string FlagWarning
        {
            get
            {
                if (!string.IsNullOrEmpty(Lap.Penalty)) return "PÉN";
                if (Lap.LimitsWarning > 0) return "AVERT";
                if (Lap.LimitsNoAction > 0 && Lap.LimitsInvalid == 0) return "LIM";
                return "";
            }
        }
        public string FlagContact { get { return Lap.Contacts > 0 ? "CHOC" : ""; } }
        public string FlagWarningBg { get { return FlagWarning == "PÉN" ? "#CC8E24AA" : (FlagWarning == "AVERT" ? "#CCEF6C00" : "#66777777"); } }

        /// <summary>Plain-language summary shown when hovering the Info cell.</summary>
        public string InfoTooltip
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (IsPitLap) parts.Add("Passage aux stands");
                if (IsOutLap) parts.Add(Lap.LapNum == 1 ? "Sortie du garage" : "Sortie des stands");
                if (TireChanged) parts.Add("Changement de pneus");
                if (Lap.LimitsInvalid > 0) parts.Add(string.Format("Tour invalidé : {0} coupure(s) / sortie(s) de piste", Lap.LimitsInvalid));
                else if (!IsValid && IsTrackInvalidated) parts.Add("Tour sans chrono (invalidé par LMU)");
                if (Lap.LimitsWarning > 0) parts.Add(string.Format("{0} avertissement(s) limites de piste", Lap.LimitsWarning));
                if (Lap.LimitsNoAction > 0) parts.Add(string.Format("{0} dépassement(s) des limites sans conséquence", Lap.LimitsNoAction));
                if (Lap.Contacts > 0) parts.Add(string.Format("{0} contact(s)", Lap.Contacts));
                if (!string.IsNullOrEmpty(Lap.Penalty)) parts.Add("Pénalité : " + Lap.Penalty);
                if (!IsValid && Lap.ReconstructedTicks > 0) parts.Add("Chrono reconstitué, hors statistiques");
                return parts.Count > 0 ? string.Join("\n", parts) : null;
            }
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
