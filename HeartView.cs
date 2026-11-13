using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>
    /// The small heart-rate overlay: a heart beating at the driver's rate and the BPM under it,
    /// on a dark pill so it reads over any part of the game. Grey and "—" while the watch has
    /// no valid measure.
    /// </summary>
    internal class HeartView : OverlayCanvas
    {
        public const double W = 78, H = 96;

        private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
        private static readonly Typeface Bold = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.SemiCondensed);
        private static readonly Typeface Reg = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.SemiCondensed);
        private static readonly Brush Pill = Frozen(Color.FromArgb(200, 0x10, 0x12, 0x18)), White = Frozen(Colors.White),
                                      Muted = Frozen(Color.FromArgb(170, 255, 255, 255)), Shadow = Frozen(Color.FromArgb(200, 0, 0, 0)),
                                      Red = Frozen(Color.FromRgb(0xFF, 0x3B, 0x4E)), Grey = Frozen(Color.FromRgb(0x70, 0x76, 0x80));
        private static readonly Pen Border = FrozenPen(Color.FromArgb(40, 255, 255, 255), 1), HeartEdge = FrozenPen(Color.FromArgb(220, 255, 255, 255), 1.4),
                                    EditPen = DashPen();
        private static readonly Geometry Shape = Geometry.Parse("M 0,-6 C -4,-14 -16,-12 -16,-2 C -16,6 -6,12 0,18 C 6,12 16,6 16,-2 C 16,-12 4,-14 0,-6 Z");
        private static readonly DateTime Origin = DateTime.UtcNow;

        private double _ppd = 1, _phase;
        private DateTime _last = DateTime.UtcNow;

        /// <summary>Made-up values while placing it with no watch.</summary>
        public static bool Demo;

        public HeartView()
        {
            Width = W; Height = H;
            Loaded += (s, e) => { try { _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { } };
        }

        protected override void OnRender(DrawingContext dc)
        {
            int bpm = Heart.Bpm;
            if (bpm <= 0 && (EditMode || Demo)) bpm = 128;
            dc.DrawRoundedRectangle(Pill, Border, new Rect(0.5, 0.5, W - 1, H - 1), 14, 14);
            if (EditMode) dc.DrawRoundedRectangle(null, EditPen, new Rect(1.5, 1.5, W - 3, H - 3), 13, 13);

            // The beat: a quick swell at each heartbeat, then back
            var now = DateTime.UtcNow;
            double dt = Math.Min(0.2, (now - _last).TotalSeconds);
            _last = now;
            if (bpm > 0) _phase = (_phase + dt * bpm / 60.0) % 1.0;
            double beat = bpm > 0 ? 1 + 0.16 * Math.Exp(-_phase * 9) + 0.07 * Math.Exp(-Math.Max(0, _phase - 0.22) * 14) * (_phase > 0.22 ? 1 : 0) : 1;

            dc.PushTransform(new TranslateTransform(W / 2, 32));
            dc.PushTransform(new ScaleTransform(beat * 1.05, beat * 1.05));
            dc.DrawGeometry(bpm > 0 ? Red : Grey, HeartEdge, Shape);
            dc.Pop(); dc.Pop();

            string value = bpm > 0 ? bpm.ToString(Fr) : "—";
            var ft = Text(value, 26, White, Bold);
            var sh = Text(value, 26, Shadow, Bold);
            dc.DrawText(sh, new Point((W - ft.Width) / 2 + 1, 52 + 1));
            dc.DrawText(ft, new Point((W - ft.Width) / 2, 52));
            var unit = Text(bpm > 0 || !Heart.Present ? (Heart.Present || EditMode || Demo ? "bpm" : "montre ?") : "mesure…", 10.5, Muted, Reg);
            dc.DrawText(unit, new Point((W - unit.Width) / 2, 80));
        }

        private FormattedText Text(string s, double size, Brush b, Typeface face)
        {
            return new FormattedText(s, Fr, FlowDirection.LeftToRight, face, size, b, _ppd);
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static Pen FrozenPen(Color c, double w) { var p = new Pen(Frozen(c), w); p.Freeze(); return p; }
        private static Pen DashPen()
        {
            var p = new Pen(Frozen(Color.FromRgb(0xFF, 0xC8, 0x57)), 2) { DashStyle = new DashStyle(new double[] { 2, 1.5 }, 0) };
            p.Freeze();
            return p;
        }
    }
}
