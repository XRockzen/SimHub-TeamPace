using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace User.TeamPace
{
    public enum TeleChannel { Delta, Speed, Throttle, Brake, Gear, Steer, Rpm, Heart }

    /// <summary>
    /// Stacked telemetry panels sharing a lap-distance axis. Traces are drawn on one layer
    /// (redrawn on zoom or data change), the hover cursor on another (redrawn on mouse move).
    /// Wheel: zoom · drag: pan · double-click: whole lap.
    /// </summary>
    internal class TelemetryChart : Grid
    {
        public class Trace
        {
            public TeleTrace Data;
            public Color     Color;
            public float[]   Delta;   // time gap to the reference at each reference point (null = reference)
            public float[]   Heart;   // heart rate at each point, BPM (null: no watch on that lap)
        }

        public static readonly TeleChannel[] Channels =
            { TeleChannel.Delta, TeleChannel.Speed, TeleChannel.Throttle, TeleChannel.Brake,
              TeleChannel.Gear, TeleChannel.Steer, TeleChannel.Rpm, TeleChannel.Heart };

        public static string Title(TeleChannel c)
        {
            switch (c)
            {
                case TeleChannel.Delta:    return "Écart (s)";
                case TeleChannel.Speed:    return "Vitesse (km/h)";
                case TeleChannel.Throttle: return "Accélérateur (%)";
                case TeleChannel.Brake:    return "Frein (%)";
                case TeleChannel.Gear:     return "Rapport";
                case TeleChannel.Steer:    return "Volant (%)";
                case TeleChannel.Heart:    return "Cardio (BPM)";
                default:                   return "Régime (tr/min)";
            }
        }

        private static double Weight(TeleChannel c)
        {
            switch (c)
            {
                case TeleChannel.Speed: return 2.2;
                case TeleChannel.Delta: return 1.3;
                default:                return 1.0;
            }
        }

        public const double ML = 58, MR = 10, MT = 16, MB = 22, Gap = 8;   // MT: corner names strip

        public event Action<double> CursorMoved;   // lap distance, NaN when the mouse leaves
        public event Action ViewChanged;

        private List<Trace> _traces = new List<Trace>();
        private TrackModel _track;
        private readonly List<TeleChannel> _visible = new List<TeleChannel>();
        private double _len, _x0, _x1;
        private double _cursor = double.NaN;
        private bool   _dragging;
        private double _dragX, _dragX0, _dragX1;

        private readonly Layer _tracesLayer, _cursorLayer;

        public TelemetryChart()
        {
            Background   = Brushes.Transparent;   // hit-testable everywhere
            ClipToBounds = true;
            _tracesLayer = new Layer(DrawTraces);
            _cursorLayer = new Layer(DrawCursor) { IsHitTestVisible = false };
            Children.Add(_tracesLayer);
            Children.Add(_cursorLayer);
            SizeChanged += (s, e) => { _tracesLayer.InvalidateVisual(); _cursorLayer.InvalidateVisual(); };
        }

        public double ViewStart { get { return _x0; } }
        public double ViewEnd   { get { return _x1; } }

        public void SetData(List<Trace> traces, IEnumerable<TeleChannel> visible, bool keepView)
        {
            _traces = traces ?? new List<Trace>();
            _visible.Clear();
            _visible.AddRange(visible);
            double len = 0;
            foreach (var t in _traces) if (t.Data.N > 0) len = Math.Max(len, t.Data.DistAt(t.Data.N - 1));
            bool sameTrack = Math.Abs(len - _len) < 50;
            _len = len;
            if (!keepView || !sameTrack || _x1 <= _x0) { _x0 = 0; _x1 = len; }
            Redraw();
        }

        /// <summary>Corner names and spans above the panels.</summary>
        public void SetTrack(TrackModel track)
        {
            _track = track;
            Redraw();
        }

        public void SetVisible(IEnumerable<TeleChannel> visible)
        {
            _visible.Clear();
            _visible.AddRange(visible);
            Redraw();
        }

        private void Redraw()
        {
            _tracesLayer.InvalidateVisual();
            _cursorLayer.InvalidateVisual();
            var h = ViewChanged;
            if (h != null) h();
        }

        // ─── Geometry ─────────────────────────────────────────────────────────

        private double PlotW { get { return Math.Max(10, ActualWidth - ML - MR); } }

        private double X(double dist) { return ML + (dist - _x0) / (_x1 - _x0) * PlotW; }

        private double Dist(double x) { return _x0 + (x - ML) / PlotW * (_x1 - _x0); }

        /// <summary>Top and height of each visible panel.</summary>
        private List<KeyValuePair<TeleChannel, Rect>> Panels()
        {
            var list = new List<KeyValuePair<TeleChannel, Rect>>();
            double total = 0;
            foreach (var c in _visible) total += Weight(c);
            double avail = ActualHeight - MT - MB - Gap * Math.Max(0, _visible.Count - 1);
            if (total <= 0 || avail < 20) return list;
            double y = MT;
            foreach (var c in _visible)
            {
                double h = avail * Weight(c) / total;
                list.Add(new KeyValuePair<TeleChannel, Rect>(c, new Rect(ML, y, PlotW, h)));
                y += h + Gap;
            }
            return list;
        }

        private static double Value(Trace t, TeleChannel c, int i)
        {
            var d = t.Data;
            switch (c)
            {
                case TeleChannel.Delta:    return t.Delta != null ? t.Delta[i] : 0;
                case TeleChannel.Speed:    return d.Speed[i];
                case TeleChannel.Throttle: return d.Throttle[i];
                case TeleChannel.Brake:    return d.Brake[i];
                case TeleChannel.Gear:     return d.Gear[i];
                case TeleChannel.Steer:    return d.Steer[i];
                case TeleChannel.Heart:    return t.Heart != null ? t.Heart[i] : double.NaN;
                default:                   return d.Rpm[i];
            }
        }

        /// <summary>Y range of a panel over the visible distance.</summary>
        private void Range(TeleChannel c, out double lo, out double hi)
        {
            switch (c)
            {
                case TeleChannel.Throttle:
                case TeleChannel.Brake: lo = 0; hi = 100; return;
            }
            double min = double.MaxValue, max = double.MinValue;
            foreach (var t in _traces)
            {
                // Delta values are indexed on the reference trace
                var basis = c == TeleChannel.Delta && t.Delta != null ? _traces[0].Data : t.Data;
                if (c == TeleChannel.Delta && t.Delta == null) continue;
                int a = basis.IndexAt(_x0), b = basis.IndexAt(_x1);
                for (int i = a; i <= b; i++)
                {
                    double v = Value(t, c, i);
                    if (double.IsNaN(v)) continue;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            if (min > max) { min = 0; max = 1; }
            switch (c)
            {
                case TeleChannel.Delta:
                    double m = Math.Max(0.05, Math.Max(Math.Abs(min), Math.Abs(max)) * 1.1);
                    lo = -m; hi = m; return;
                case TeleChannel.Steer:
                    double s = Math.Max(5, Math.Max(Math.Abs(min), Math.Abs(max)) * 1.1);
                    lo = -s; hi = s; return;
                case TeleChannel.Gear:
                    lo = 0; hi = Math.Max(2, max) + 0.5; return;
                case TeleChannel.Speed:
                    lo = 0; hi = Math.Ceiling(max * 1.05 / 50) * 50; return;
                case TeleChannel.Heart:
                    lo = Math.Floor(min / 10) * 10 - 5; hi = Math.Ceiling(max / 10) * 10 + 5; return;
                default:   // rpm
                    lo = Math.Floor(min / 1000) * 1000; hi = Math.Ceiling(max / 1000) * 1000;
                    if (hi <= lo) hi = lo + 1000;
                    return;
            }
        }

        private static double Y(Rect r, double v, double lo, double hi)
        {
            return r.Bottom - (v - lo) / (hi - lo) * r.Height;
        }

        // ─── Drawing ──────────────────────────────────────────────────────────

        private static readonly Brush PanelBg   = Frozen(Color.FromArgb(10, 255, 255, 255));
        private static readonly Brush TextBrush = Frozen(Color.FromArgb(150, 255, 255, 255));
        private static readonly Brush TitleBrush = Frozen(Color.FromArgb(200, 255, 255, 255));
        private static readonly Pen   GridPen   = FrozenPen(Color.FromArgb(26, 255, 255, 255), 1);
        private static readonly Pen   ZeroPen   = FrozenPen(Color.FromArgb(70, 255, 255, 255), 1);
        private static readonly Pen   CursorPen = FrozenPen(Color.FromArgb(200, 255, 255, 255), 1);
        private static readonly Typeface Face   = new Typeface("Segoe UI");
        private static readonly Brush CornerBg    = Frozen(Color.FromArgb(9, 255, 255, 255));
        private static readonly Brush CornerBrush = Frozen(Color.FromArgb(170, 255, 200, 87));

        private void DrawTraces(DrawingContext dc)
        {
            if (_traces.Count == 0 || _x1 <= _x0) return;
            var panels = Panels();
            foreach (var kv in panels)
            {
                var c = kv.Key;
                var r = kv.Value;
                dc.DrawRectangle(PanelBg, null, r);
                double lo, hi;
                Range(c, out lo, out hi);

                // Y labels: bottom, middle, top (zero line for signed channels)
                // Small panels: bounds only, or no labels at all, so they never overlap
                var ticks = r.Height >= 60 ? new[] { lo, (lo + hi) / 2, hi } : r.Height >= 34 ? new[] { lo, hi } : new double[0];
                foreach (var v in ticks)
                {
                    double y = Y(r, v, lo, hi);
                    dc.DrawLine(GridPen, new Point(r.Left, y), new Point(r.Right, y));
                    // Kept inside the panel so labels of neighbouring panels never overlap
                    double ty = Math.Max(r.Top + 6, Math.Min(r.Bottom - 6, y));
                    Text(dc, Label(c, v), r.Left - 6, ty, TextAlignment.Right, TextBrush, 11);
                }
                if (lo < 0 && hi > 0)
                {
                    double y0 = Y(r, 0, lo, hi);
                    dc.DrawLine(ZeroPen, new Point(r.Left, y0), new Point(r.Right, y0));
                }

                dc.PushClip(new RectangleGeometry(r));
                // Reference last, so it stays readable on top of the others
                for (int k = _traces.Count - 1; k >= 0; k--) DrawSeries(dc, _traces[k], c, r, lo, hi);
                dc.Pop();

                Text(dc, Title(c), r.Left + 6, r.Top + 8, TextAlignment.Left, TitleBrush, 11.5);
            }

            // Corner names above the panels, corner spans shaded lightly
            if (panels.Count > 0 && _track != null)
            {
                var last = panels[panels.Count - 1].Value;
                foreach (var corner in _track.Corners)
                {
                    if (corner.End < _x0 || corner.Start > _x1) continue;
                    double a = Math.Max(ML, X(corner.Start)), b = Math.Min(ML + PlotW, X(corner.End));
                    dc.DrawRectangle(CornerBg, null, new Rect(a, MT, Math.Max(1, b - a), last.Bottom - MT));
                    double xa = X(corner.Apex);
                    if (xa >= ML && xa <= ML + PlotW)
                        Text(dc, corner.Name, xa, MT / 2, TextAlignment.Center, CornerBrush, 11);
                }
            }

            // Distance axis under the last panel
            if (panels.Count > 0)
            {
                var last = panels[panels.Count - 1].Value;
                double step = NiceStep((_x1 - _x0) / Math.Max(2, PlotW / 90));
                for (double d = Math.Ceiling(_x0 / step) * step; d <= _x1 - step * 0.35; d += step)
                {
                    double x = X(d);
                    dc.DrawLine(GridPen, new Point(x, MT), new Point(x, last.Bottom));
                    Text(dc, d >= 1000 ? (d / 1000).ToString(step < 1000 ? "0.0#" : "0", CultureInfo.InvariantCulture) + " km"
                                       : d.ToString("0", CultureInfo.InvariantCulture) + " m",
                         x, last.Bottom + 10, TextAlignment.Center, TextBrush, 11);
                }
            }
        }

        private void DrawSeries(DrawingContext dc, Trace t, TeleChannel c, Rect r, double lo, double hi)
        {
            if (c == TeleChannel.Delta && t.Delta == null)
            {
                // Reference: the zero line, in its colour
                double y0 = Y(r, 0, lo, hi);
                dc.DrawLine(FrozenPen(Color.FromArgb(160, t.Color.R, t.Color.G, t.Color.B), 1.2),
                            new Point(r.Left, y0), new Point(r.Right, y0));
                return;
            }
            var basis = c == TeleChannel.Delta ? _traces[0].Data : t.Data;
            int a = Math.Max(0, basis.IndexAt(_x0) - 1), b = Math.Min(basis.N - 1, basis.IndexAt(_x1) + 1);
            if (b <= a) return;
            int stride = Math.Max(1, (b - a) / (int)Math.Max(1, PlotW * 1.5));

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool open = false;
                double prevY = 0;
                for (int i = a; i <= b; i += stride)
                {
                    double v = Value(t, c, i);
                    if (double.IsNaN(v)) { open = false; continue; }
                    double x = X(basis.DistAt(i)), y = Y(r, v, lo, hi);
                    if (!open) { g.BeginFigure(new Point(x, y), false, false); open = true; }
                    else
                    {
                        if (c == TeleChannel.Gear && y != prevY) g.LineTo(new Point(x, prevY), true, false);
                        g.LineTo(new Point(x, y), true, false);
                    }
                    prevY = y;
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, FrozenPen(t.Color, c == TeleChannel.Speed ? 1.6 : 1.3), geo);
        }

        private void DrawCursor(DrawingContext dc)
        {
            if (double.IsNaN(_cursor) || _traces.Count == 0) return;
            double x = X(_cursor);
            var panels = Panels();
            if (panels.Count == 0) return;
            dc.DrawLine(CursorPen, new Point(x, MT), new Point(x, panels[panels.Count - 1].Value.Bottom));
            foreach (var kv in panels)
            {
                double lo, hi;
                Range(kv.Key, out lo, out hi);
                foreach (var t in _traces)
                {
                    var basis = kv.Key == TeleChannel.Delta ? _traces[0].Data : t.Data;
                    if (kv.Key == TeleChannel.Delta && t.Delta == null) continue;
                    if (_cursor < basis.FirstDist || _cursor > basis.DistAt(basis.N - 1)) continue;
                    double v = Value(t, kv.Key, basis.IndexAt(_cursor));
                    if (double.IsNaN(v)) continue;
                    dc.DrawEllipse(Frozen(t.Color), null, new Point(x, Y(kv.Value, v, lo, hi)), 3, 3);
                }
            }
        }

        private static string Label(TeleChannel c, double v)
        {
            switch (c)
            {
                case TeleChannel.Delta: return (v > 0 ? "+" : "") + v.ToString("0.00", CultureInfo.InvariantCulture);
                case TeleChannel.Gear:  return Math.Round(v).ToString(CultureInfo.InvariantCulture);
                case TeleChannel.Rpm:   return (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k";
                default:                return Math.Round(v).ToString(CultureInfo.InvariantCulture);
            }
        }

        private void Text(DrawingContext dc, string s, double x, double y, TextAlignment align, Brush brush, double size)
        {
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush,
                                       VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double left = align == TextAlignment.Right ? x - ft.Width : align == TextAlignment.Center ? x - ft.Width / 2 : x;
            dc.DrawText(ft, new Point(left, y - ft.Height / 2));
        }

        private static double NiceStep(double raw)
        {
            double p = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
            double n = raw / p;
            return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p;
        }

        // ─── Mouse ────────────────────────────────────────────────────────────

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_len <= 0) return;
            double xc = Clamp(Dist(e.GetPosition(this).X), _x0, _x1);
            double f = e.Delta > 0 ? 0.75 : 1 / 0.75;
            SetView(xc - (xc - _x0) * f, xc + (_x1 - xc) * f);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ClickCount == 2) { SetView(0, _len); e.Handled = true; return; }
            _dragging = true;
            _dragX = e.GetPosition(this).X; _dragX0 = _x0; _dragX1 = _x1;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _dragging = false;
            ReleaseMouseCapture();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            if (_dragging)
            {
                double shift = -(p.X - _dragX) / PlotW * (_dragX1 - _dragX0);
                shift = Clamp(shift, -_dragX0, _len - _dragX1);
                _x0 = _dragX0 + shift; _x1 = _dragX1 + shift;
                Redraw();
            }
            SetCursor(p.X >= ML && p.X <= ML + PlotW ? Dist(p.X) : double.NaN);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_dragging) SetCursor(double.NaN);
        }

        public void SetCursor(double dist)
        {
            _cursor = dist;
            _cursorLayer.InvalidateVisual();
            var h = CursorMoved;
            if (h != null) h(dist);
        }

        /// <summary>Zooms on a distance window (used by the track map).</summary>
        public void SetView(double x0, double x1)
        {
            double span = Clamp(x1 - x0, Math.Min(60, _len), _len);
            double mid = (x0 + x1) / 2;
            _x0 = Clamp(mid - span / 2, 0, _len - span);
            _x1 = _x0 + span;
            Redraw();
        }

        /// <summary>Zooms in or out around a lap distance (used by the track map's wheel).</summary>
        public void ZoomAt(double dist, double factor)
        {
            if (_len <= 0) return;
            double xc = Clamp(dist, _x0, _x1);
            SetView(xc - (xc - _x0) * factor, xc + (_x1 - xc) * factor);
        }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static Pen FrozenPen(Color c, double w)
        {
            var p = new Pen(Frozen(c), w) { LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        private sealed class Layer : FrameworkElement
        {
            private readonly Action<DrawingContext> _draw;
            public Layer(Action<DrawingContext> draw) { _draw = draw; }
            protected override void OnRender(DrawingContext dc) { _draw(dc); }
        }
    }
}
