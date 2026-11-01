namespace User.TeamPace
{
    public static class LmuCategoryStyle
    {
        // ── Category color palette ────────────────────────────────────────────
        // HYPERCAR  → rouge
        // LMP2      → bleu
        // LMPE ELMS → bleu + bordure orange
        // LMP3      → violet
        // LMGT3     → vert
        // GTE       → orange
        // inconnu   → gris

        public static string GetBg(string carClass)
        {
            switch (Normalize(carClass))
            {
                case "HYPERCAR": return "#CCB71C1C";
                case "LMP2":     return "#CC1565B0";
                case "LMPEELMS": return "#CC1565B0";
                case "LMP3":     return "#CC6A1E8E";
                case "LMGT3":    return "#CC2E7D32";
                case "GTE":      return "#CCE65100";
                default:         return "#CC424242";
            }
        }

        public static string GetBorder(string carClass)
        {
            if (Normalize(carClass) == "LMPEELMS") return "#FFFF9800";
            return "Transparent";
        }

        public static string GetBorderThickness(string carClass)
        {
            if (Normalize(carClass) == "LMPEELMS") return "1";
            return "0";
        }

        // Text label : use a short canonical name
        public static string GetShortName(string carClass)
        {
            if (string.IsNullOrEmpty(carClass)) return "?";
            switch (Normalize(carClass))
            {
                case "HYPERCAR": return "Hyper";   // LMU's own name, kept in the saved plans
                case "LMP2":     return "LMP2";
                case "LMPEELMS": return "LMP2 ELMS";
                case "LMP3":     return "LMP3";
                case "LMGT3":    return "GT3";
                case "GTE":      return "GTE";
                default:
                    // Return first word, max 6 chars
                    var s = carClass.Split(' ')[0];
                    return s.Length > 6 ? s.Substring(0, 6) : s;
            }
        }

        private static string Normalize(string carClass)
        {
            if (string.IsNullOrEmpty(carClass)) return "";
            var u = carClass.ToUpperInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            // LMU writes "Hyper" in its results and its live memory
            if (u.Contains("HYPER") || u == "H" || u == "LMH" || u == "LMDH") return "HYPERCAR";
            if (u.Contains("LMPEELMS") || u.Contains("ELMS")) return "LMPEELMS";
            if (u.Contains("LMP2"))   return "LMP2";
            if (u.Contains("LMP3"))   return "LMP3";
            if (u.Contains("LMGT3") || u.Contains("GT3")) return "LMGT3";
            if (u.Contains("GTE"))    return "GTE";
            return u;
        }
    }
}
