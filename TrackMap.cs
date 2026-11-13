using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>
    /// Map of the circuit following the chart's distance window: the whole lap, or a close-up of
    /// the zoomed part with the track surface, each lap's line, where each driver starts braking
    /// and gets back on the throttle, and the cursor.
    /// </summary>
    internal class TrackMap : FrameworkElement
    {
        public class Line
        {
            public Color     Color;
            public TeleTrace Trace;
            public double[]  X, Y;        // position of each trace point
            public List<int> BrakeIdx;    // braking starts
            public List<int> GasIdx;      // throttle back on
            public float[]   Heat;        // heart rate at each point: the line coloured by it (null: its colour)
            public double    HeatLo, HeatHi;
        }

        public event Action<double> DistanceClicked;            // corner close-up
        public event Action<double> HoverDistance;              // NaN when away from the lines
        public event Action<double, double> ZoomRequested;      // distance under the mouse, factor

        private TrackModel _model;
        private List<Line> _lines = new List<Line>();
        private double _length, _x0, _x1, _cursor = double.NaN;

        // Current transform
        private double _scale = 1, _ox, _oy, _minX, _maxY;

        public bool IsLarge { get; set; }

        /// <summary>Numbered markers on the centre line (work plan priorities).</summary>
        public List<KeyValuePair<double, string>> Highlights { get; set; }

        /// <summary>No lap lines: just the track (work plan overview).</summary>
        public bool ShowLegend { get; set; }

        public TrackMap()
        {
            ShowLegend = true;
            ClipToBounds = true;
            Cursor = Cursors.Hand;
        }

        public void SetData(TrackModel model, List<Line> lines, double length)
        {
            _model = model; _lines = lines ?? new List<Line>(); _length = length;
            InvalidateVisual();
        }

        public void SetView(double x0, double x1) { _x0 = x0; _x1 = x1; InvalidateVisual(); }
        public void SetCursor(double d)          { _cursor = d; InvalidateVisual(); }

        private bool Zoomed { get { return _length > 0 && _x1 - _x0 < _length * 0.95; } }

        // ─── Markers ──────────────────────────────────────────────────────────

        /// <summary>Braking starts (brake over 10 % after 30 m without) and throttle pick-ups (over 20 % after a 20 m lift).</summary>
        public static void FindMarkers(TeleTrace t, out List<int> brake, out List<int> gas)
        {
            brake = new List<int>(); gas = new List<int>();
            int lastBrake = -1000, liftStart = -1;
            for (int i = 1; i < t.N; i++)
            {
                if (t.Brake[i] >= 10 && t.Brake[i - 1] < 10 && i - lastBrake > 15) brake.Add(i);
                if (t.Brake[i] >= 10) lastBrake = i;

                if (t.Throttle[i] < 20) { if (liftStart < 0) liftStart = i; }
                else
                {
                    if (liftStart >= 0 && i - liftStart >= 10) gas.Add(i);
                    liftStart = -1;
                }
            }
        }

        // ─── Drawing ──────────────────────────────────────────────────────────

        private static readonly Brush TrackFill = Frozen(Color.FromArgb(34, 255, 255, 255));
        private static readonly Pen   EdgePen   = FrozenPen(Color.FromArgb(110, 255, 255, 255), 1.2);
        private static readonly Pen   OutlinePen = FrozenPen(Color.FromArgb(70, 255, 255, 255), 6);
        private static readonly Color BrakeRing = Color.FromRgb(0xFF, 0x52, 0x52);
        private static readonly Color GasRing   = Color.FromRgb(0x69, 0xF0, 0xAE);
        private static readonly Brush LabelBrush = Frozen(Color.FromArgb(220, 255, 200, 87));
        private static readonly Brush TextBrush  = Frozen(Color.FromArgb(170, 255, 255, 255));
        private static readonly Typeface Face = new Typeface("Segoe UI");

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (_lines.Count == 0 && _model == null) return;
            if (!FitView()) return;

            double lo = Zoomed ? _x0 - 15 : 0, hi = Zoomed ? _x1 + 15 : _length;

            // Track surface, or a plain outline without a model
            if (_model != null) DrawTrack(dc, lo, hi);
            else if (_lines.Count > 0) dc.DrawGeometry(null, OutlinePen, LineGeometry(_lines[0], 0, _lines[0].Trace.N - 1));

            // Corner names, outside each corner
            if (_model != null)
                foreach (var c in _model.Corners)
                {
                    if (c.Apex < lo || c.Apex > hi) continue;
                    int k = _model.Index(c.Apex);
                    double off = (c.Left ? -_model.EdgeRight[k] : _model.EdgeLeft[k]) + (c.Left ? -1 : 1) * 14 / Math.Max(_scale, 0.01);
                    var p = Map(_model.Cx[k] + off * _model.Nx[k], _model.Cy[k] + off * _model.Ny[k]);
                    Text(dc, c.Name, p, LabelBrush, IsLarge ? 13 : 10.5, true);
                }

            // Lines, the reference last so it stays on top
            double width = Zoomed ? (IsLarge ? 3 : 2.4) : 1.6;
            for (int li = _lines.Count - 1; li >= 0; li--)
            {
                var l = _lines[li];
                int a = l.Trace.IndexAt(lo), b = l.Trace.IndexAt(hi);
                if (b - a < 2) continue;
                if (l.Heat != null) DrawHeat(dc, l, a, b, width * 1.8);
                else dc.DrawGeometry(null, FrozenPen(l.Color, width), LineGeometry(l, a, b));
            }

            // Braking and throttle points
            double r = Zoomed ? (IsLarge ? 6.5 : 5) : 3;
            foreach (var l in _lines)
            {
                var fill = Frozen(l.Color);
                foreach (var i in l.BrakeIdx) if (InView(l.Trace, i, lo, hi)) dc.DrawEllipse(fill, FrozenPen(BrakeRing, Zoomed ? 2.2 : 1.4), Pt(l, i), r, r);
                foreach (var i in l.GasIdx)   if (InView(l.Trace, i, lo, hi)) dc.DrawEllipse(fill, FrozenPen(GasRing,   Zoomed ? 2.2 : 1.4), Pt(l, i), r, r);
            }

            // Start / finish
            if (_model != null && (!Zoomed || _x0 <= 20))
            {
                double lx, ly, rx, ry;
                _model.Edges(0, out lx, out ly, out rx, out ry);
                dc.DrawLine(FrozenPen(Colors.White, 2), Map(lx, ly), Map(rx, ry));
            }

            // Cursor: where each lap is at that distance
            if (!double.IsNaN(_cursor))
                foreach (var l in _lines)
                {
                    if (_cursor < l.Trace.FirstDist || _cursor > l.Trace.DistAt(l.Trace.N - 1)) continue;
                    dc.DrawEllipse(Frozen(l.Color), FrozenPen(Colors.White, 2), Pt(l, l.Trace.IndexAt(_cursor)), Zoomed ? 6 : 5, Zoomed ? 6 : 5);
                }

            if (Highlights != null && _model != null)
                foreach (var h in Highlights)
                {
                    int k = _model.Index(h.Key);
                    var p = Map(_model.Cx[k], _model.Cy[k]);
                    dc.DrawEllipse(LabelBrush, FrozenPen(Color.FromRgb(0x22, 0x23, 0x28), 2), p, 11, 11);
                    var ft = new FormattedText(h.Value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12.5,
                                               Frozen(Color.FromRgb(0x22, 0x23, 0x28)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    ft.SetFontWeight(FontWeights.Bold);
                    dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
                }

            if (ShowLegend) DrawLegend(dc);
            foreach (var l in _lines) if (l.Heat != null) { DrawHeatLegend(dc, l); break; }
            if (Zoomed) DrawScale(dc);
        }

        private void DrawTrack(DrawingContext dc, double lo, double hi)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool first = true;
                for (double d = lo; d <= hi; d += TrackModel.Step)
                {
                    double lx, ly, rx, ry;
                    _model.Edges(Wrap(d), out lx, out ly, out rx, out ry);
                    if (first) { g.BeginFigure(Map(lx, ly), true, true); first = false; }
                    else g.LineTo(Map(lx, ly), true, true);
                }
                for (double d = hi; d >= lo; d -= TrackModel.Step)
                {
                    double lx, ly, rx, ry;
                    _model.Edges(Wrap(d), out lx, out ly, out rx, out ry);
                    g.LineTo(Map(rx, ry), true, true);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(TrackFill, null, geo);

            // Edges as two separate lines (the polygon's closing seams are not edges)
            foreach (var left in new[] { true, false })
            {
                var e = new StreamGeometry();
                using (var g = e.Open())
                {
                    bool first = true;
                    for (double d = lo; d <= hi; d += TrackModel.Step)
                    {
                        double lx, ly, rx, ry;
                        _model.Edges(Wrap(d), out lx, out ly, out rx, out ry);
                        var p = left ? Map(lx, ly) : Map(rx, ry);
                        if (first) { g.BeginFigure(p, false, false); first = false; } else g.LineTo(p, true, true);
                    }
                }
                e.Freeze();
                dc.DrawGeometry(null, EdgePen, e);
            }
        }

        private void DrawLegend(DrawingContext dc)
        {
            double size = IsLarge ? 12 : 10.5, x = 8, y = 10;
            dc.DrawEllipse(Brushes.Transparent, FrozenPen(BrakeRing, 2), new Point(x + 4, y), 4, 4);
            x += 12;
            x += Text(dc, "freinage", new Point(x, y), TextBrush, size, false) + 12;
            dc.DrawEllipse(Brushes.Transparent, FrozenPen(GasRing, 2), new Point(x + 4, y), 4, 4);
            x += 12;
            Text(dc, "remise des gaz", new Point(x, y), TextBrush, size, false);
        }

        /// <summary>The line in pieces of the same colour, calm (green) to tense (red).</summary>
        private void DrawHeat(DrawingContext dc, Line l, int a, int b, double width)
        {
            const int Buckets = 16;
            int stride = Zoomed ? 1 : 2;
            int start = a, bucket = -1;
            for (int i = a; i <= b; i += stride)
            {
                float v = l.Heat[i];
                int k = float.IsNaN(v) ? -1 : (int)Math.Round((Buckets - 1) * Math.Max(0, Math.Min(1, (v - l.HeatLo) / Math.Max(1, l.HeatHi - l.HeatLo))));
                if (k != bucket || i + stride > b)
                {
                    int end = Math.Min(b, i);
                    if (end > start)
                    {
                        if (bucket >= 0) dc.DrawGeometry(null, FrozenPen(Heart.Heat(bucket, 0, Buckets - 1), width), LineGeometry(l, start, end));
                        else dc.DrawGeometry(null, FrozenPen(Color.FromArgb(120, 160, 160, 160), width * 0.6), LineGeometry(l, start, end));
                    }
                    start = end; bucket = k;
                }
            }
        }

        private void DrawHeatLegend(DrawingContext dc, Line l)
        {
            double size = IsLarge ? 12 : 10.5, x = 8, y = ActualHeight - (Zoomed ? 34 : 14), w = 90;
            for (int i = 0; i < 18; i++)
                dc.DrawRectangle(Frozen(Heart.Heat(i, 0, 17)), null, new Rect(x + w * i / 18, y - 3, w / 18 + 0.5, 6));
            Text(dc, string.Format(CultureInfo.InvariantCulture, "♥ {0:0} → {1:0} bpm", l.HeatLo, l.HeatHi), new Point(x + w + 8, y), TextBrush, size, false);
        }

        private void DrawScale(DrawingContext dc)
        {
            double target = 90 / _scale;   // ~90 px
            double p = Math.Pow(10, Math.Floor(Math.Log10(target)));
            double len = target / p >= 5 ? 5 * p : target / p >= 2 ? 2 * p : p;
            double px = len * _scale, x = 10, y = ActualHeight - 12;
            var pen = FrozenPen(Color.FromArgb(180, 255, 255, 255), 1.5);
            dc.DrawLine(pen, new Point(x, y), new Point(x + px, y));
            dc.DrawLine(pen, new Point(x, y - 4), new Point(x, y + 1));
            dc.DrawLine(pen, new Point(x + px, y - 4), new Point(x + px, y + 1));
            Text(dc, len.ToString("0", CultureInfo.InvariantCulture) + " m", new Point(x + px + 6, y - 1), TextBrush, IsLarge ? 12 : 10, false);
        }

        // ─── Geometry ─────────────────────────────────────────────────────────

        /// <summary>Fits the visible part (whole lap, or the zoomed window) into the control.</summary>
        private bool FitView()
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            Action<double, double> add = (x, y) =>
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            };
            double lo = Zoomed ? _x0 : 0, hi = Zoomed ? _x1 : _length;
            foreach (var l in _lines)
            {
                int a = l.Trace.IndexAt(lo), b = l.Trace.IndexAt(hi);
                for (int i = a; i <= b; i++) add(l.X[i], l.Y[i]);
            }
            if (_model != null)
                for (double d = lo; d <= hi; d += TrackModel.Step * 2)
                {
                    double lx, ly, rx, ry;
                    _model.Edges(Wrap(d), out lx, out ly, out rx, out ry);
                    add(lx, ly); add(rx, ry);
                }
            if (minX > maxX) return false;

            double pad = IsLarge ? 36 : 22;
            double w = Math.Max(1, ActualWidth - 2 * pad), h = Math.Max(1, ActualHeight - 2 * pad - 14);
            _scale = Math.Min(w / Math.Max(10, maxX - minX), h / Math.Max(10, maxY - minY));
            _ox = pad + (w - (maxX - minX) * _scale) / 2;
            _oy = pad + 14 + (h - (maxY - minY) * _scale) / 2;
            _minX = minX; _maxY = maxY;
            return true;
        }

        private Point Map(double x, double y) { return new Point(_ox + (x - _minX) * _scale, _oy + (_maxY - y) * _scale); }

        private Point Pt(Line l, int i) { return Map(l.X[i], l.Y[i]); }

        private double Wrap(double d) { return _length > 0 ? ((d % _length) + _length) % _length : d; }

        private static bool InView(TeleTrace t, int i, double lo, double hi)
        {
            double d = t.DistAt(i);
            return d >= lo && d <= hi;
        }

        private Geometry LineGeometry(Line l, int a, int b)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Pt(l, a), false, false);
                int stride = Zoomed ? 1 : 2;
                for (int i = a + 1; i <= b; i += stride) g.LineTo(Pt(l, i), true, true);
                g.LineTo(Pt(l, b), true, true);
            }
            geo.Freeze();
            return geo;
        }

        private double Text(DrawingContext dc, string s, Point p, Brush brush, double size, bool centred)
        {
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush,
                                       VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(centred ? p.X - ft.Width / 2 : p.X, p.Y - ft.Height / 2));
            return ft.Width;
        }

        // ─── Mouse ────────────────────────────────────────────────────────────

        /// <summary>Lap distance of the reference line point nearest to the mouse (NaN if farther than maxPx).</summary>
        private double Nearest(Point p, double maxPx)
        {
            if (_lines.Count == 0) return double.NaN;
            var l = _lines[0];
            double lo = Zoomed ? _x0 : 0, hi = Zoomed ? _x1 : _length;
            int a = l.Trace.IndexAt(lo), b = l.Trace.IndexAt(hi), best = -1;
            double bestD = maxPx * maxPx;
            for (int i = a; i <= b; i++)
            {
                var q = Pt(l, i);
                double d = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best < 0 ? double.NaN : l.Trace.DistAt(best);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = HoverDistance;
            if (h != null) h(Nearest(e.GetPosition(this), 40));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            var h = HoverDistance;
            if (h != null) h(double.NaN);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            double d = Nearest(e.GetPosition(this), 40);
            var h = DistanceClicked;
            if (!double.IsNaN(d) && h != null) h(d);
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            double d = Nearest(e.GetPosition(this), 10000);
            var h = ZoomRequested;
            if (!double.IsNaN(d) && h != null) h(d, e.Delta > 0 ? 0.7 : 1 / 0.7);
            e.Handled = true;
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static Pen FrozenPen(Color c, double w)
        {
            var p = new Pen(Frozen(c), w) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }
    }
}
