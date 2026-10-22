using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>
    /// The overlay, drawn by hand at 760 × 150 (scaled by its host): one page at a time, chosen
    /// with the wheel buttons. Everything comes from <see cref="OverlayService"/>.
    /// </summary>
    /// <summary>What the overlay window shows: the overlay or the pitwall.</summary>
    internal abstract class OverlayCanvas : FrameworkElement
    {
        public bool EditMode;
    }

    internal class OverlayView : OverlayCanvas
    {
        public const double W = OverlaySettings.BaseWidth, H = OverlaySettings.BaseHeight;

        private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
        private static readonly Typeface Reg = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.SemiCondensed);
        private static readonly Typeface Bold = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.SemiCondensed);

        private static Brush B(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
        private static Pen P(Brush b, double w, bool dashed = false)
        {
            var p = new Pen(b, w) { LineJoin = PenLineJoin.Round };
            if (dashed) p.DashStyle = new DashStyle(new double[] { 2, 1.5 }, 0);
            p.Freeze();
            return p;
        }

        private static readonly Brush Text = B(255, 0xE8, 0xE9, 0xEC), Muted = B(150, 255, 255, 255), Faint = B(90, 255, 255, 255);
        private static readonly Brush Line = B(20, 255, 255, 255), Track = B(20, 255, 255, 255), Sep = B(22, 255, 255, 255);
        private static readonly Brush Green = B(255, 0x66, 0xBB, 0x6A), Red = B(255, 0xEF, 0x9A, 0x9A), Amber = B(255, 0xFF, 0xC8, 0x57);
        private static readonly Brush Gas = B(255, 0x4C, 0xAF, 0x7D), Brk = B(255, 0xEF, 0x6C, 0x5A), BrkFill = B(62, 0xEF, 0x6C, 0x5A);
        private static readonly Brush Blue = B(255, 0x42, 0xA5, 0xF5), AmberSoft = B(36, 0xFF, 0xC8, 0x57);
        private static readonly Pen GasPen = P(Gas, 2), BrkPen = P(Brk, 2), GhostGas = P(B(140, 0x4C, 0xAF, 0x7D), 2, true), GhostBrk = P(B(140, 0xEF, 0x6C, 0x5A), 2, true);
        private static readonly Pen GridPen = P(Line, 1), NowPen = P(B(90, 255, 255, 255), 1), BorderPen = P(B(24, 255, 255, 255), 1), EditPen = P(Amber, 2, true);

        private readonly List<Point> _a = new List<Point>(), _b = new List<Point>(), _c = new List<Point>(), _d = new List<Point>();
        private double _ppd = 1;

        public OverlayView()
        {
            Width = W; Height = H;
            SnapsToDevicePixels = true;
            Loaded += (s, e) => { try { _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { } };
        }

        protected override void OnRender(DrawingContext dc)
        {
            var s = OverlaySettings.Current;
            var bg = new SolidColorBrush(Color.FromArgb((byte)(Math.Max(0.05, s.Opacity) * 255), 0x11, 0x14, 0x1A));
            dc.DrawRoundedRectangle(bg, BorderPen, new Rect(0.5, 0.5, W - 1, H - 1), 10, 10);
            if (EditMode) dc.DrawRoundedRectangle(null, EditPen, new Rect(1, 1, W - 2, H - 2), 10, 10);

            var page = (OverlayPage)Math.Max(0, OverlayService.CurrentPage);
            Header(dc, s, page);
            switch (page)
            {
                case OverlayPage.Telemetry: Telemetry(dc, s); break;
                case OverlayPage.Coaching:  Coaching(dc, s); break;
                case OverlayPage.Race:      Race(dc, s); break;
                default:                    Plan(dc, s); break;
            }
        }

        // ─── Common ───────────────────────────────────────────────────────────

        private double T(DrawingContext dc, string s, double x, double y, double size, Brush b, bool bold = false, int align = 0)
        {
            var ft = new FormattedText(s ?? "", Fr, FlowDirection.LeftToRight, bold ? Bold : Reg, size, b, _ppd);
            if (align == 1) x -= ft.Width; else if (align == 2) x -= ft.Width / 2;
            dc.DrawText(ft, new Point(x, y));
            return ft.Width;
        }

        private static string Signed(double v, string format)
        {
            return (v < 0 ? "−" : "+") + Math.Abs(v).ToString(format, Fr);
        }

        private static Brush GapBrush(double v) { return double.IsNaN(v) ? Muted : v <= 0 ? Green : Red; }

        private void Header(DrawingContext dc, OverlaySettings s, OverlayPage page)
        {
            string title;
            var race = OverlayService.Race;
            var r = OverlayService.Reference;
            switch (page)
            {
                case OverlayPage.Telemetry: title = "TÉLÉMÉTRIE"; break;
                case OverlayPage.Coaching:  title = "COACHING"; break;
                case OverlayPage.Race:      title = race != null && race.Plan != null ? "COURSE · PLAN « " + race.Plan.Id.ToUpperInvariant() + " »" : "COURSE"; break;
                default:                    title = r != null ? "PLAN DE TRAVAIL · " + r.Label.ToUpperInvariant() + " " + Fmt.LapSec(r.LapSec) : "PLAN DE TRAVAIL"; break;
            }
            double w = T(dc, title, 12, 7, 11, Muted, true);
            if (OverlayService.Demo) T(dc, "APERÇU", 12 + w + 10, 7, 11, Amber, true);
            if (EditMode) T(dc, "Glissez pour placer · molette : taille", W / 2, 7, 11, Amber, false, 2);

            // Page marks, then what button B does here
            double x = W - 12;
            for (int p = 3; p >= 0; p--)
            {
                if (!s.Shows(OverlayService.Session, (OverlayPage)p)) continue;
                bool on = p == (int)page;
                double pw = on ? 14 : 4;
                dc.DrawRoundedRectangle(on ? Text : Faint, null, new Rect(x - pw, 13, pw, 4), 2, 2);
                x -= pw + 4;
            }
            string hint = page == OverlayPage.Telemetry ? (s.Ghost ? "B · fantôme affiché" : "B · fantôme masqué")
                        : page == OverlayPage.Coaching ? (s.Voice ? "B · voix active" : "B · voix coupée")
                        : page == OverlayPage.Race ? "B · arrêt suivant" : "B · changer de référence";
            T(dc, hint, x - 8, 7, 11, Muted, false, 1);
        }

        private void Center(DrawingContext dc, string text, Brush b)
        {
            T(dc, text, W / 2, 70, 14, b, false, 2);
        }

        // ─── 1. Telemetry ─────────────────────────────────────────────────────

        private void Telemetry(DrawingContext dc, OverlaySettings s)
        {
            const double x0 = 12, y0 = 30, cw = 480, ch = 104;
            Func<double, double> yv = v => y0 + 6 + (100 - Math.Max(0, Math.Min(100, v))) * 0.9;
            foreach (var v in new[] { 100.0, 50, 0 }) dc.DrawLine(GridPen, new Point(x0, yv(v)), new Point(x0 + cw, yv(v)));

            // The last seconds, newest on the right; one point per pixel at most
            _a.Clear(); _b.Clear(); _c.Clear(); _d.Clear();
            double now = OverlayService.Now, win = s.WindowSec, lastX = double.MaxValue;
            int head = OverlayService.Head, count = OverlayService.Count;
            for (int k = 0; k < count; k++)
            {
                int i = (head - 1 - k + OverlayService.Cap) % OverlayService.Cap;
                double age = now - OverlayService.RingT[i];
                if (age > win || age < -1) break;
                double x = x0 + cw * (1 - Math.Max(0, age) / win);
                if (lastX - x < 1 && k > 0) continue;
                lastX = x;
                _a.Add(new Point(x, yv(OverlayService.RingThr[i])));
                _b.Add(new Point(x, yv(OverlayService.RingBrk[i])));
                _c.Add(new Point(x, yv(OverlayService.RingGThr[i])));
                _d.Add(new Point(x, yv(OverlayService.RingGBrk[i])));
            }
            bool ghost = s.Ghost && (OverlayService.Reference != null || OverlayService.Demo);
            if (ghost)
            {
                Poly(dc, _d, GhostBrk, false, 0);
                Poly(dc, _c, GhostGas, false, 0);
            }
            Poly(dc, _b, BrkPen, true, yv(0));
            Poly(dc, _a, GasPen, false, 0);
            dc.DrawLine(NowPen, new Point(x0 + cw - 0.5, y0 + 2), new Point(x0 + cw - 0.5, y0 + ch - 4));

            // Right: pedals, gear, speed. Numbers calm down (smoothed, shown 4 times a second), as on a dashboard
            Smooth();
            dc.DrawRectangle(Sep, null, new Rect(506, 30, 1, 104));
            Bar(dc, 522, 42, 90, OverlayService.Brake, Brk);
            Bar(dc, 538, 42, 90, OverlayService.Throttle, Gas);
            if (!ghost)
            {
                // Without the ghost: the car only, nothing compared
                T(dc, OverlayService.Gear, 606, 34, 64, Text, true, 2);
                T(dc, double.IsNaN(_sSpeed) ? "—" : _sSpeed.ToString("0", Fr), W - 14, 44, 40, Text, true, 1);
                T(dc, "km/h", W - 14, 94, 13, Faint, false, 1);
                return;
            }
            double gw = T(dc, OverlayService.Gear, 560, 34, 50, Text, true);
            if (OverlayService.RefGear > 0) T(dc, "/ " + OverlayService.RefGear, 564 + gw, 60, 17, Faint);
            double sw = T(dc, double.IsNaN(_sSpeed) ? "—" : _sSpeed.ToString("0", Fr), 560, 102, 18, Text);
            T(dc, !double.IsNaN(_sRefSpeed) ? "/ " + _sRefSpeed.ToString("0", Fr) + " km/h" : "km/h", 564 + sw, 106, 12, Faint);

            var r = OverlayService.Reference;
            string refName = r != null ? r.Label : OverlayService.Demo ? "Idéal de l'équipe" : "pas de référence";
            T(dc, "Δ " + refName.ToUpperInvariant(), W - 12, 36, 11, Muted, false, 1);
            T(dc, double.IsNaN(_sDelta) ? "—" : Signed(_sDelta, "0.00"), W - 12, 48, 30, GapBrush(_sDelta), true, 1);
            if (!double.IsNaN(_sCorner))
            {
                double vw = T(dc, Signed(_sCorner, "0.00"), W - 12, 92, 24, GapBrush(_sCorner), true, 1);
                T(dc, "V" + _shownCorner, W - 18 - vw, 100, 14, Muted, false, 1);
            }
            else if (r == null && !OverlayService.Demo)
                T(dc, "voir l'onglet Overlay", W - 12, 98, 12, Faint, false, 1);
        }

        // ─── Calm numbers: smoothed over about half a second, redrawn 4 times a second ─────

        private double _last = double.NaN, _nextShow;
        private double _fDelta = double.NaN, _fCorner = double.NaN, _fSpeed = double.NaN, _fRefSpeed = double.NaN;
        private double _sDelta = double.NaN, _sCorner = double.NaN, _sSpeed = double.NaN, _sRefSpeed = double.NaN;
        private int _corner = -1, _shownCorner;

        private void Smooth()
        {
            double now = OverlayService.Now;
            double dt = double.IsNaN(_last) ? 1 : Math.Max(0, Math.Min(1, now - _last));
            _last = now;
            _fDelta = Ema(_fDelta, OverlayService.Delta, dt, 0.6);
            if (OverlayService.CornerNumber != _corner) { _corner = OverlayService.CornerNumber; _fCorner = double.NaN; }   // a new corner starts from its own gap
            _fCorner = Ema(_fCorner, OverlayService.CornerDelta, dt, 0.6);
            _fSpeed = Ema(_fSpeed, OverlayService.Speed, dt, 0.2);
            _fRefSpeed = Ema(_fRefSpeed, OverlayService.RefSpeed, dt, 0.2);
            if (now < _nextShow) return;
            _nextShow = now + 0.25;
            _sDelta = _fDelta; _sCorner = _fCorner; _sSpeed = _fSpeed; _sRefSpeed = _fRefSpeed; _shownCorner = _corner;
        }

        private static double Ema(double filtered, double value, double dt, double tau)
        {
            if (double.IsNaN(value)) return double.NaN;
            if (double.IsNaN(filtered)) return value;
            return filtered + (value - filtered) * (1 - Math.Exp(-dt / tau));
        }

        private static void Poly(DrawingContext dc, List<Point> pts, Pen pen, bool fill, double baseY)
        {
            if (pts.Count < 2) return;
            if (fill)
            {
                var area = new StreamGeometry();
                using (var c = area.Open())
                {
                    c.BeginFigure(new Point(pts[0].X, baseY), true, true);
                    c.PolyLineTo(pts, false, false);
                    c.LineTo(new Point(pts[pts.Count - 1].X, baseY), false, false);
                }
                area.Freeze();
                dc.DrawGeometry(BrkFill, null, area);
            }
            var line = new StreamGeometry();
            using (var c = line.Open())
            {
                c.BeginFigure(pts[0], false, false);
                c.PolyLineTo(pts, true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, pen, line);
        }

        private static void Bar(DrawingContext dc, double x, double y, double h, double v, Brush b)
        {
            dc.DrawRoundedRectangle(Track, null, new Rect(x, y, 9, h), 3, 3);
            double f = Math.Max(0, Math.Min(100, v)) / 100 * h;
            if (f > 0.5) dc.DrawRoundedRectangle(b, null, new Rect(x, y + h - f, 9, f), 3, 3);
        }

        // ─── 2. Coaching ──────────────────────────────────────────────────────

        private void Coaching(DrawingContext dc, OverlaySettings s)
        {
            var focus = OverlayService.Focus;
            if (focus.Count == 0)
            {
                Center(dc, OverlayService.Reference == null ? OverlayService.Status : "Pas de virage à travailler : il faut 3 tours propres avec télémétrie ici.", Muted);
                return;
            }
            dc.DrawRectangle(Sep, null, new Rect(546, 30, 1, 104));
            var ap = OverlayService.Approach;
            var bi = OverlayService.Bilan;
            if (ap != null)
            {
                T(dc, string.Format("V{0} · {1} · PRIORITÉ {2}", ap.Corner.Number, ap.Phase, ap.Rank), 12, 36, 12, Amber, true);
                T(dc, ap.Message, 12, 52, 30, Text, true);
                // Countdown to the reference's braking point, your last one in white
                const double bx = 12, by = 98, bw = 520;
                double ann = s.AnnounceMeters, left = OverlayService.ApproachLeft;
                double frac = Math.Max(0, Math.Min(1, 1 - left / ann));
                dc.DrawRoundedRectangle(Track, null, new Rect(bx, by, bw, 10), 5, 5);
                dc.DrawRoundedRectangle(B(140, 0xFF, 0xC8, 0x57), null, new Rect(bx, by, Math.Max(10, bw * frac), 10), 5, 5);
                dc.DrawRoundedRectangle(Amber, null, new Rect(bx + bw - 4, by - 5, 4, 20), 2, 2);
                bool mark = !double.IsNaN(ap.LastBrakePoint) && !double.IsNaN(ap.RefBrake);
                if (mark)
                {
                    double m = Math.Max(0, Math.Min(1.0, 1 - (ap.RefBrake - ap.LastBrakePoint) / ann));
                    dc.DrawRectangle(B(150, 255, 255, 255), null, new Rect(bx + bw * m - 1, by - 4, 2, 18));
                }
                T(dc, left > 0 ? string.Format(Fr, "freinage de la référence dans {0:0} m", left) : "freinage de la référence : maintenant", bx, 114, 12.5, Muted);
                if (mark) T(dc, "trait blanc : ton dernier freinage", bx + bw, 114, 12.5, Faint, false, 1);
                List(dc, focus, ap);
            }
            else if (bi != null)
            {
                T(dc, string.Format("V{0} · {1} · BILAN DU PASSAGE", bi.Corner.Number, bi.Phase), 12, 36, 12, Muted, true);
                double gap = bi.LastGap;
                bool ok = gap <= 0.02;
                double hw = T(dc, ok ? "✓ dans la référence" : Signed(gap, "0.00") + " s sur la référence", 12, 52, 28, ok ? Green : gap < 0.08 ? Amber : Red, true);
                if (bi.Gaps.Count >= 2)
                {
                    double change = gap - bi.Gaps[bi.Gaps.Count - 2];
                    T(dc, Signed(change, "0.00") + " s sur le passage d'avant", 12 + hw + 14, 62, 16, change <= 0 ? Green : Red, true);
                }
                var parts = new List<string>();
                if (!double.IsNaN(bi.LastBrakePoint) && !double.IsNaN(bi.RefBrake))
                {
                    double db = bi.LastBrakePoint - bi.RefBrake;
                    parts.Add(Math.Abs(db) < 3 ? "freinage au même endroit" : string.Format(Fr, "freiné {0:0} m {1}", Math.Abs(db), db > 0 ? "après la référence" : "avant la référence"));
                }
                if (!double.IsNaN(bi.LastMinSpeed)) parts.Add(string.Format(Fr, "vitesse mini {0:0} / {1:0} km/h", bi.LastMinSpeed, bi.RefMinSpeed));
                T(dc, string.Join("  ·  ", parts), 12, 100, 13.5, Muted);
                Trend(dc, bi);
            }
            else
            {
                T(dc, "PROCHAIN VIRAGE TRAVAILLÉ", 12, 36, 12, Muted, true);
                var nx = OverlayService.Next;
                if (nx != null)
                {
                    T(dc, string.Format(Fr, "V{0} dans {1:0} m", nx.Corner.Number, OverlayService.NextIn), 12, 52, 28, Text, true);
                    T(dc, nx.Message, 12, 94, 16, Amber);
                }
                else T(dc, "—", 12, 52, 28, Faint, true);
                List(dc, focus, null);
            }
        }

        private void List(DrawingContext dc, List<OverlayFocus> focus, OverlayFocus current)
        {
            double y = 40;
            foreach (var f in focus)
            {
                bool cur = f == current;
                double g = f.LastGap;
                bool done = !double.IsNaN(g) && g <= 0.02;
                if (cur) dc.DrawRoundedRectangle(AmberSoft, null, new Rect(556, y - 2, 192, 22), 4, 4);
                Brush b = cur ? Amber : done ? Green : Muted;
                T(dc, (done ? "✓ " : cur ? "● " : "○ ") + "V" + f.Corner.Number + " " + f.Phase.ToLowerInvariant(), 562, y, 13.5, b, cur);
                T(dc, double.IsNaN(g) ? "—" : Signed(g, "0.00"), 742, y, 13.5, b, cur, 1);
                y += 28;
            }
        }

        private void Trend(DrawingContext dc, OverlayFocus f)
        {
            T(dc, "5 DERNIERS PASSAGES", 556, 36, 11, Muted);
            int n = Math.Min(5, f.Gaps.Count);
            double max = 0.05;
            for (int k = 0; k < n; k++) max = Math.Max(max, Math.Abs(f.Gaps[f.Gaps.Count - n + k]));
            for (int k = 0; k < n; k++)
            {
                double g = f.Gaps[f.Gaps.Count - n + k];
                double h = Math.Max(3, Math.Abs(g) / max * 56);
                Brush b = g <= 0.02 ? Green : g < 0.08 ? Amber : Red;
                dc.DrawRoundedRectangle(b, null, new Rect(556 + k * 30, 112 - h, 22, h), 3, 3);
            }
            if (n >= 2) T(dc, string.Format(Fr, "écart : {0:0.00} → {1:0.00} s", f.Gaps[f.Gaps.Count - n], f.Gaps[f.Gaps.Count - 1]), 556, 118, 12, Muted);
        }

        // ─── 3. Race ──────────────────────────────────────────────────────────

        private void Race(DrawingContext dc, OverlaySettings s)
        {
            var race = OverlayService.Race;
            bool demo = OverlayService.Demo && (race == null || race.Sim == null);
            if (!demo && (race == null || race.Sim == null || race.Error != null))
            {
                Center(dc, race != null && race.Error != null ? race.Error : "Pas de plan de stratégie chargé : voir l'onglet Overlay.", Muted);
                return;
            }
            int done = OverlayService.Lap - 1;
            StrategyEngine.StopPlan stop = null;
            int number = 0, planLap = 0;
            if (!demo)
            {
                var sim = OverlayService.LiveSim ?? race.Sim;   // recalculated at each lap once the race runs
                int j = sim.Stops.FindIndex(st => st.Lap > done);
                if (j >= 0)
                {
                    j = Math.Min(sim.Stops.Count - 1, j + OverlayService.StopView);
                    stop = sim.Stops[j];
                    number = j + sim.FirstStop;
                    int b = number - race.Sim.FirstStop;
                    if (sim != race.Sim && b >= 0 && b < race.Sim.Stops.Count) planLap = race.Sim.Stops[b].Lap;
                }
            }
            double colW = (W - 24) / 4;
            for (int c = 1; c < 4; c++) dc.DrawRectangle(Sep, null, new Rect(12 + c * colW, 30, 1, 104));

            // 1. Laps to the stop
            int toGo = demo ? 15 : stop != null ? stop.Lap - done : 0;
            if (demo || stop != null)
            {
                T(dc, "ARRÊT " + (demo ? 3 : number) + " DANS", 12, 40, 11, Muted);
                double w = T(dc, toGo.ToString(), 12, 54, 38, Text, true);
                T(dc, toGo > 1 ? "tours" : "tour", 18 + w, 72, 16, Muted);
                T(dc, demo ? "au T29 · vers 1h23" : planLap > 0 && planLap != stop.Lap ? string.Format("au T{0} · plan T{1}", stop.Lap, planLap)
                                                                                       : string.Format("au T{0} · vers {1}", stop.Lap, Clock(stop.At)), 12, 104, 12.5, planLap > 0 && planLap != stop.Lap ? Amber : Muted);
            }
            else
            {
                T(dc, "DERNIER RELAIS", 12, 40, 11, Muted);
                T(dc, "jusqu'au drapeau", 12, 60, 18, Text, true);
            }

            // 2. Fuel and its margin
            double x = 12 + colW + 12;
            double tank = demo ? 100 : Math.Max(1, race.Plan.TankL);
            double perLap = demo ? 3.9 : !double.IsNaN(OverlayService.LiveFuel) ? OverlayService.LiveFuel : race.FuelPerLap;
            T(dc, "ESSENCE", x, 40, 11, Muted);
            T(dc, OverlayService.Fuel.ToString("0", Fr) + " L", x, 54, 24, Text, true);
            double bw = colW - 24;
            dc.DrawRoundedRectangle(Track, null, new Rect(x, 88, bw, 8), 4, 4);
            dc.DrawRoundedRectangle(Blue, null, new Rect(x, 88, bw * Math.Max(0, Math.Min(1, OverlayService.Fuel / tank)), 8), 4, 4);
            if (!double.IsNaN(perLap) && perLap > 0 && toGo > 0)
            {
                double need = toGo * perLap;
                dc.DrawRectangle(Amber, null, new Rect(x + bw * Math.Min(1, need / tank) - 1, 85, 2, 14));
                double margin = OverlayService.Fuel / perLap - toGo;
                T(dc, string.Format(Fr, "marge {0} tour", Signed(margin, "0.0")), x, 104, 12.5, margin >= 0.3 ? Green : margin >= 0 ? Amber : Red);
            }

            // 3. Against the plan
            x = 12 + 2 * colW + 12;
            T(dc, "SUR LE PLAN", x, 40, 11, Muted);
            double pg = OverlayService.PlanGap;
            T(dc, double.IsNaN(pg) ? "—" : Signed(pg, "0") + " s", x, 54, 28, double.IsNaN(pg) ? Faint : pg <= 0 ? Green : pg < 15 ? Amber : Red, true);
            T(dc, !OverlayService.IsRace ? "hors course" : double.IsNaN(pg) ? "au prochain tour" : "fin du tour " + done, x, 104, 12.5, Muted);

            // 4. The stop: who takes over, the level and the tyres
            x = 12 + 3 * colW + 12;
            if (!demo && stop == null) return;
            T(dc, "À L'ARRÊT " + (demo ? 3 : number), x, 40, 11, Muted);
            T(dc, "→ " + (demo ? "Sam" : stop.NextDriver), x, 56, 15, Amber, true);
            int wheels = demo ? StrategyEngine.FL | StrategyEngine.FR : stop.Wheels;
            string level = demo ? "100 %" : race.Sim.EnergyMode ? stop.Level.ToString("0", Fr) + " %" : stop.Level.ToString("0", Fr) + " L";
            T(dc, level + " · " + StrategyEngine.WheelsLabel(wheels), x, 80, 12.5, Muted);
            Car(dc, x + colW - 46, 92, wheels);
        }

        private void Car(DrawingContext dc, double x, double y, int wheels)
        {
            dc.DrawRoundedRectangle(B(26, 255, 255, 255), P(B(50, 255, 255, 255), 1), new Rect(x + 10, y, 14, 34), 4, 4);
            for (int c = 0; c < 4; c++)
            {
                bool on = (wheels & (1 << c)) != 0;
                double wx = c % 2 == 0 ? x : x + 27, wy = c < 2 ? y : y + 20;
                dc.DrawRoundedRectangle(on ? Blue : B(200, 0xF2, 0xB1, 0x3C), null, new Rect(wx, wy, 7, 14), 2, 2);
            }
        }

        private static string Clock(double raceSec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, raceSec));
            return string.Format("{0}h{1:00}", (int)t.TotalHours, t.Minutes);
        }

        // ─── 4. Work plan ─────────────────────────────────────────────────────

        private void Plan(DrawingContext dc, OverlaySettings s)
        {
            var focus = OverlayService.Focus;
            var r = OverlayService.Reference;
            if (r == null && !OverlayService.Demo) { Center(dc, OverlayService.Status, Muted); return; }
            dc.DrawRectangle(Sep, null, new Rect(546, 30, 1, 104));
            double y = 42;
            if (focus.Count == 0) T(dc, "Pas de virage à travailler ici (3 tours propres avec télémétrie nécessaires).", 12, 70, 13, Muted);
            foreach (var f in focus)
            {
                double first = f.FirstGap, last = f.LastGap;
                bool done = !double.IsNaN(last) && last <= 0.02;
                bool next = f == OverlayService.Next;
                Brush b = done ? Green : next ? Amber : Muted;
                T(dc, (done ? "✓ " : next ? "● " : "○ ") + "V" + f.Corner.Number + " · " + f.Phase.ToLowerInvariant(), 12, y, 13.5, b, next);
                double frac = f.Gaps.Count >= 2 && first > 0 ? Math.Max(0.04, Math.Min(1, (first - last) / first)) : done ? 1 : 0.04;
                dc.DrawRoundedRectangle(Track, null, new Rect(178, y + 6, 240, 6), 3, 3);
                dc.DrawRoundedRectangle(done ? Green : next ? Amber : B(110, 255, 255, 255), null, new Rect(178, y + 6, 240 * frac, 6), 3, 3);
                string txt = f.Gaps.Count == 0 ? "pas encore passé" : f.Gaps.Count == 1 ? string.Format(Fr, "{0:0.00} s à prendre", Math.Max(0, last))
                           : string.Format(Fr, "{0:0.00} → {1:0.00} s", first, last);
                T(dc, txt, 534, y, 13, b, false, 1);
                y += 30;
            }
            double refLap = r != null ? r.LapSec : 107.32;
            T(dc, "TON MEILLEUR TOUR", 560, 40, 11, Muted);
            double best = OverlayService.SessionBest, start = OverlayService.SessionFirst;
            T(dc, double.IsNaN(best) ? "—" : Signed(best - refLap, "0.00") + " s", 560, 54, 28, double.IsNaN(best) ? Faint : GapBrush(best - refLap), true);
            T(dc, "sur la référence", 560, 90, 12.5, Muted);
            if (!double.IsNaN(start)) T(dc, "1er tour : " + Signed(start - refLap, "0.00") + " s", 560, 108, 12.5, Muted);
        }
    }
}
