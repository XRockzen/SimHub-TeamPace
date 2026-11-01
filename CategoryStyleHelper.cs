using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace User.TeamPace
{
    public class CategoryBadge
    {
        public string    ShortName      { get; set; }
        public Brush     Background     { get; set; }
        public Brush     BorderBrush    { get; set; }
        public Thickness BorderThickness { get; set; }
    }

    public static class CategoryStyleHelper
    {
        // ─── Row alternation brushes ──────────────────────────────────────────

        public static readonly Brush RowBrushA;   // even circuit group  — subtle blue tint
        public static readonly Brush RowBrushB;   // odd  circuit group  — near-transparent

        public static readonly Brush HoverBrush;
        public static readonly Brush SelectedBrush;

        // ─── Category badge map ───────────────────────────────────────────────

        private static readonly Dictionary<string, CategoryBadge> Badges;

        // ─── Static constructor ───────────────────────────────────────────────

        static CategoryStyleHelper()
        {
            RowBrushA     = Frozen(Color.FromArgb(22, 100, 160, 255));   // 9 % blue
            RowBrushB     = Frozen(Color.FromArgb(6,  255, 255, 255));   // 2 % white
            HoverBrush    = Frozen(Color.FromArgb(40, 150, 200, 255));   // 16 % blue
            SelectedBrush = Frozen(Color.FromArgb(65, 100, 170, 255));   // 26 % blue

            Badges = new Dictionary<string, CategoryBadge>(StringComparer.OrdinalIgnoreCase);

            // ── LMU categories ────────────────────────────────────────────────
            Badges.Add("HYPERCAR",  Badge("HY",   "#D32F2F", null, 0));
            Badges.Add("Hyper",     Badge("HY",   "#D32F2F", null, 0));
            Badges.Add("LMP2_ELMS", Badge("P2",   "#1565C0", "#FB8C00", 2));
            Badges.Add("LMGT3",     Badge("GT3",  "#2E7D32", null, 0));
            Badges.Add("GTE",       Badge("GTE",  "#E65100", null, 0));
            Badges.Add("LMP2",      Badge("P2",   "#1565C0", null, 0));
            Badges.Add("LMP2 ELMS", Badge("P2",   "#1565C0", "#FB8C00", 2));
            Badges.Add("LMP3",      Badge("P3",   "#6A1B9A", null, 0));

            // ── ACC categories ────────────────────────────────────────────────
            Badges.Add("GT3",   Badge("GT3",  "#2E7D32", null, 0));
            Badges.Add("GT4",   Badge("GT4",  "#00695C", null, 0));

            // ── Generic fallback ──────────────────────────────────────────────
            Badges.Add("",      Badge("?",    "#37474F", null, 0));
        }

        // ─── Public API ───────────────────────────────────────────────────────

        public static CategoryBadge GetBadge(string category)
        {
            if (string.IsNullOrEmpty(category)) return Badges[""];
            CategoryBadge badge;
            if (Badges.TryGetValue(category, out badge)) return badge;
            // Fallback: dark grey badge with first letters
            return Badge(Abbrev(category), "#37474F", null, 0);
        }

        /// <summary>Returns the WPF Color for a category (from its badge background).</summary>
        public static Color GetColor(string category)
        {
            var bg = GetBadge(category).Background as SolidColorBrush;
            return bg != null ? bg.Color : Color.FromRgb(55, 71, 79);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static CategoryBadge Badge(string shortName, string bgHex, string borderHex, double borderWidth)
        {
            Brush bg     = Frozen(ParseHex(bgHex));
            Brush border = borderHex != null ? Frozen(ParseHex(borderHex)) : Brushes.Transparent;
            return new CategoryBadge
            {
                ShortName       = shortName,
                Background      = bg,
                BorderBrush     = border,
                BorderThickness = borderWidth > 0 ? new Thickness(borderWidth) : new Thickness(0)
            };
        }

        private static Color ParseHex(string hex)
        {
            hex = hex.TrimStart('#');
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return Color.FromRgb(r, g, b);
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static string Abbrev(string s)
        {
            if (s.Length <= 4) return s.ToUpper();
            // First letters of each word
            var parts = s.Split(' ');
            var result = "";
            foreach (var p in parts) if (p.Length > 0) result += p[0];
            return result.ToUpper();
        }
    }
}
