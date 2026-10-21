using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace User.TeamPace
{
    /// <summary>
    /// Lap telemetry comparison on one circuit: up to four laps (any driver, any session),
    /// overlaid on lap distance, with the time gap to the first lap, a track map and the values
    /// under the cursor.
    /// </summary>
    internal class TelemetryView : UserControl
    {
        public const int MaxSlots = 4;

        /// <summary>What a slot shows: a driver, optionally a session, optionally a lap (0 = best).</summary>
        public class Preset
        {
            public string DriverKey;
            public string SessionFileId;   // null = every session on the circuit
            public int    LapNum;
        }

        private class Slot
        {
            public DriverView Driver;
            public string     SessionKey;   // null = all sessions
            public TeleLapRef Lap;
            public Color      Color;
            public TeleTrace  Trace;
            public string     Note;
        }

        private class Item
        {
            public object Value;
            public string Text;
            public override string ToString() { return Text; }
        }

        private static readonly string[] ExtraColors = { "#FFFFC857", "#FFEC6FAD", "#FF4DD0E1", "#FFB0BEC5" };
        private static readonly HashSet<TeleChannel> VisibleNormal = new HashSet<TeleChannel>(TelemetryChart.Channels);
        // Under the large map there is room for fewer curves: the pedals, speed and gap first
        private static readonly HashSet<TeleChannel> VisibleLarge = new HashSet<TeleChannel>
            { TeleChannel.Delta, TeleChannel.Speed, TeleChannel.Throttle, TeleChannel.Brake, TeleChannel.Gear };
        private static HashSet<TeleChannel> Visible { get { return _large ? VisibleLarge : VisibleNormal; } }
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly List<DriverView> _drivers;
        private readonly string _circuit;
        private readonly List<Preset> _presets;   // kept in sync with the slots (owned by the nav state)
        private readonly Dictionary<string, List<TeleLapRef>> _laps = new Dictionary<string, List<TeleLapRef>>();
        private readonly List<Slot> _slots = new List<Slot>();

        private readonly StackPanel _slotPanel = new StackPanel();
        private readonly WrapPanel _toggles = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        private readonly TelemetryChart _chart = new TelemetryChart();
        private readonly TrackMap _map = new TrackMap();
        private readonly Border _mapHost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
        };
        private readonly TextBlock _mapHint = new TextBlock
        {
            FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly Grid _center = new Grid();
        private readonly StackPanel _side = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        private readonly ScrollViewer _sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly StackPanel _cornerPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        private readonly Grid _readout = new Grid { Margin = new Thickness(0, 0, 0, 6) };   // values under the cursor, above the curves
        private readonly DockPanel _chartHost = new DockPanel();
        private readonly Button _btnLarge = new Button { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private static bool _large;   // kept while browsing laps
        private TrackModel _model;
        private readonly List<Border> _cornerRows = new List<Border>();
        private readonly TextBlock _empty = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 560,
            TextAlignment = TextAlignment.Center,
        };
        private bool _building;

        public TelemetryView(List<DriverView> drivers, string circuit, List<Preset> presets,
                             double zoomFrom = double.NaN, double zoomTo = double.NaN)
        {
            _drivers = drivers;
            _circuit = circuit;
            _presets = presets;

            foreach (var d in drivers) _laps[d.Key] = TelemetryService.GetLaps(d, circuit);
            foreach (var p in presets)
            {
                if (_slots.Count >= MaxSlots) break;
                var d = drivers.Find(x => x.Key == p.DriverKey);
                if (d != null) _slots.Add(CreateSlot(d, p));
            }
            if (_slots.Count == 0 && drivers.Count > 0) _slots.Add(CreateSlot(drivers[0], new Preset()));

            _model = BuildModel();
            Content = BuildLayout();
            _chart.CursorMoved += d => { _map.SetCursor(d); UpdateReadout(d); HighlightCorner(d); };
            _chart.ViewChanged += () => _map.SetView(_chart.ViewStart, _chart.ViewEnd);
            _map.DistanceClicked += ZoomOnCorner;
            _map.HoverDistance   += d => _chart.SetCursor(d);
            _map.ZoomRequested   += (d, f) => _chart.ZoomAt(d, f);
            Reload(false);
            if (!double.IsNaN(zoomFrom) && !double.IsNaN(zoomTo))
                Dispatcher.BeginInvoke(new Action(() => _chart.SetView(zoomFrom, zoomTo)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ─── Slots ────────────────────────────────────────────────────────────

        private Slot CreateSlot(DriverView d, Preset p)
        {
            var slot = new Slot { Driver = d };
            var laps = _laps[d.Key];
            if (!string.IsNullOrEmpty(p.SessionFileId))
            {
                slot.SessionKey = "S|" + p.SessionFileId;
                if (!laps.Exists(l => l.SessionKey == slot.SessionKey))
                {
                    slot.SessionKey = null;
                    slot.Note = "séance sans télémétrie : meilleur tour du circuit";
                }
            }
            slot.Lap = p.LapNum > 0 && slot.SessionKey != null
                ? laps.Find(l => l.SessionKey == slot.SessionKey && l.LapNum == p.LapNum)
                : null;
            if (slot.Lap == null) slot.Lap = BestLap(laps, slot.SessionKey);
            return slot;
        }

        private static TeleLapRef BestLap(List<TeleLapRef> laps, string sessionKey)
        {
            TeleLapRef best = null, fallback = null;
            foreach (var l in laps)
            {
                if (sessionKey != null && l.SessionKey != sessionKey) continue;
                if (l.IsClean && (best == null || l.LapSec < best.LapSec)) best = l;
                if (l.Lap.IsComplete && (fallback == null || l.LapSec < fallback.LapSec)) fallback = l;
            }
            return best ?? fallback;
        }

        private void AssignColors()
        {
            var used = new HashSet<string>();
            foreach (var s in _slots)
            {
                string hex = s.Driver.ColorHex;
                if (used.Contains(hex))
                    foreach (var c in ExtraColors) if (!used.Contains(c)) { hex = c; break; }
                used.Add(hex);
                s.Color = (Color)ColorConverter.ConvertFromString(hex);
            }
        }

        private void SyncPresets()
        {
            _presets.Clear();
            foreach (var s in _slots)
                _presets.Add(new Preset
                {
                    DriverKey     = s.Driver.Key,
                    SessionFileId = s.Lap != null && s.SessionKey != null && s.Lap.Session != null ? s.Lap.Session.FileId : null,
                    LapNum        = s.Lap != null && s.SessionKey != null ? s.Lap.LapNum : 0,
                });
        }

        // ─── Track model ──────────────────────────────────────────────────────

        private TrackModel BuildModel()
        {
            var known = new List<TeleLapRef>();
            foreach (var kv in _laps) known.AddRange(kv.Value);
            return TelemetryService.TrackModelFor(_circuit, known);
        }

        // ─── Layout ───────────────────────────────────────────────────────────

        private UIElement BuildLayout()
        {
            var root = new DockPanel();

            var top = new StackPanel();
            DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(_slotPanel);
            top.Children.Add(_toggles);
            root.Children.Add(top);

            // Map header: what the map does and the enlarge button, right on top of it
            var mapHeader = new DockPanel { Margin = new Thickness(4, 2, 2, 4) };
            DockPanel.SetDock(_btnLarge, Dock.Right);
            mapHeader.Children.Add(_btnLarge);
            mapHeader.Children.Add(_mapHint);
            var mapDock = new DockPanel();
            DockPanel.SetDock(mapHeader, Dock.Top);
            mapDock.Children.Add(mapHeader);
            mapDock.Children.Add(_map);
            _mapHost.Child = mapDock;
            _side.Children.Add(_cornerPanel);
            DockPanel.SetDock(_readout, Dock.Top);
            _chartHost.Children.Add(_readout);
            _chartHost.Children.Add(_chart);
            _sideScroll.Content = _side;
            root.Children.Add(_center);

            _btnLarge.SetResourceReference(StyleProperty, "SmallBtn");
            _btnLarge.Click += (s, e) => { _large = !_large; ApplyLayout(); };

            BuildToggles();
            ApplyLayout();
            return root;
        }

        /// <summary>
        /// Normal: curves with the map in the side column. Large map: the map takes the top of the
        /// screen, the curves stay below it, still synchronised.
        /// </summary>
        private void ApplyLayout()
        {
            _center.Children.Clear();
            _center.RowDefinitions.Clear();
            _center.ColumnDefinitions.Clear();
            _side.Children.Remove(_mapHost);

            _center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
            var scroll = _sideScroll;
            Grid.SetColumn(scroll, 1);
            Grid.SetRowSpan(scroll, 1);

            if (_large)
            {
                _center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
                _center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 200 });
                _mapHost.Height = double.NaN;
                _mapHost.Margin = new Thickness(0, 0, 0, 8);
                _center.Children.Add(_mapHost);
                Grid.SetRow(_chartHost, 1); Grid.SetRow(_empty, 1);
                Grid.SetRowSpan(scroll, 2);
            }
            else
            {
                _mapHost.Height = 290;
                _mapHost.Margin = new Thickness(0);
                _side.Children.Insert(0, _mapHost);
                Grid.SetRow(_chartHost, 0); Grid.SetRow(_empty, 0);
            }
            _center.Children.Add(_chartHost);
            _center.Children.Add(_empty);
            _center.Children.Add(scroll);

            _map.IsLarge = _large;
            _map.InvalidateVisual();
            if (_toggles.Children.Count > 0) { BuildToggles(); _chart.SetVisible(VisibleList()); }
            _btnLarge.Content = _large ? "⤡ Réduire" : "⤢ Agrandir";
            _mapHint.Text = "Carte";
        }

        private void ZoomOnCorner(double dist)
        {
            var c = _model != null ? _model.CornerAt(dist) : null;
            if (c != null) _chart.SetView(c.Start - 120, c.End + 60);
            else _chart.SetView(dist - 150, dist + 150);
        }

        private void BuildToggles()
        {
            _toggles.Children.Clear();
            _toggles.Children.Add(new TextBlock { Text = "Courbes", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(166, 255, 255, 255)),
                                                  VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            foreach (var c in TelemetryChart.Channels)
            {
                var channel = c;
                var cb = new CheckBox
                {
                    Content = TelemetryChart.Title(c).Split(' ')[0], IsChecked = Visible.Contains(c), FontSize = 12,
                    Foreground = Brushes.White, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                cb.Checked   += (s, e) => { Visible.Add(channel);    _chart.SetVisible(VisibleList()); };
                cb.Unchecked += (s, e) => { Visible.Remove(channel); _chart.SetVisible(VisibleList()); };
                _toggles.Children.Add(cb);
            }
        }

        private static List<TeleChannel> VisibleList()
        {
            var list = new List<TeleChannel>();
            foreach (var c in TelemetryChart.Channels) if (Visible.Contains(c)) list.Add(c);
            return list;
        }

        private void BuildSlotRows()
        {
            _building = true;
            _slotPanel.Children.Clear();
            for (int i = 0; i < _slots.Count; i++) _slotPanel.Children.Add(BuildSlotRow(_slots[i], i));

            if (_slots.Count < MaxSlots)
            {
                var add = new Button { Content = "+ Ajouter un tour", HorizontalAlignment = HorizontalAlignment.Left,
                                       Margin = new Thickness(0, 2, 0, 0) };
                add.SetResourceReference(StyleProperty, "SmallBtn");   // resolved once in the page
                add.Click += (s, e) =>
                {
                    var d = _slots.Count > 0 ? _slots[0].Driver : _drivers[0];
                    _slots.Add(CreateSlot(d, new Preset()));
                    Reload(true);
                };
                _slotPanel.Children.Add(add);
            }
            _building = false;
        }

        private UIElement BuildSlotRow(Slot slot, int index)
        {
            var brush = new SolidColorBrush(slot.Color);
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            foreach (var w in new[] { 5.0, 170, 300, 250, -1, 30 })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            var bar = new Border { Background = brush, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 0, 1) };
            grid.Children.Add(bar);

            // Driver
            var cbDriver = Combo(1);
            foreach (var d in _drivers) cbDriver.Items.Add(new Item { Value = d, Text = d.DbPath == null ? d.Name + " (moi)" : d.Name });
            cbDriver.SelectedIndex = _drivers.IndexOf(slot.Driver);
            cbDriver.SelectionChanged += (s, e) =>
            {
                if (_building) return;
                slot.Driver = (DriverView)((Item)cbDriver.SelectedItem).Value;
                slot.SessionKey = null; slot.Note = null;
                slot.Lap = BestLap(_laps[slot.Driver.Key], null);
                Reload(true);
            };
            grid.Children.Add(cbDriver);

            var laps = _laps[slot.Driver.Key];

            // Session
            var cbSession = Combo(2);
            cbSession.Items.Add(new Item { Value = null, Text = "Toutes les séances  ·  meilleurs tours" });
            var sessions = new List<TeleLapRef>();
            var seen = new HashSet<string>();
            foreach (var l in laps) if (seen.Add(l.SessionKey)) sessions.Add(l);
            sessions.Sort((a, b) => b.Date.CompareTo(a.Date));
            int selSession = 0;
            foreach (var s in sessions)
            {
                var best = BestLap(laps, s.SessionKey);
                int count = laps.FindAll(l => l.SessionKey == s.SessionKey).Count;
                cbSession.Items.Add(new Item
                {
                    Value = s.SessionKey,
                    Text = string.Format("{0}  ·  {1} tours{2}", s.SessionLabel, count,
                                         best != null ? "  ·  " + Fmt.LapSec(best.LapSec) : ""),
                });
                if (s.SessionKey == slot.SessionKey) selSession = cbSession.Items.Count - 1;
            }
            cbSession.SelectedIndex = selSession;
            cbSession.SelectionChanged += (s, e) =>
            {
                if (_building || cbSession.SelectedItem == null) return;
                slot.SessionKey = (string)((Item)cbSession.SelectedItem).Value;
                slot.Note = null;
                slot.Lap = BestLap(_laps[slot.Driver.Key], slot.SessionKey);
                Reload(true);
            };
            grid.Children.Add(cbSession);

            // Lap
            var cbLap = Combo(3);
            var lapItems = LapItems(laps, slot.SessionKey);
            int selLap = -1;
            foreach (var it in lapItems)
            {
                cbLap.Items.Add(it);
                if (it.Value == slot.Lap) selLap = cbLap.Items.Count - 1;
            }
            cbLap.SelectedIndex = selLap;
            cbLap.IsEnabled = lapItems.Count > 0;
            cbLap.SelectionChanged += (s, e) =>
            {
                if (_building || cbLap.SelectedItem == null) return;
                slot.Lap = (TeleLapRef)((Item)cbLap.SelectedItem).Value;
                slot.Note = null;
                Reload(true);
            };
            grid.Children.Add(cbLap);

            // Summary
            var info = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                                       TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(info, 4);
            if (slot.Lap == null)
                info.Inlines.Add(new System.Windows.Documents.Run(laps.Count == 0
                    ? "aucune télémétrie sur ce circuit" : "aucun tour") { Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), FontStyle = FontStyles.Italic });
            else
            {
                info.Inlines.Add(new System.Windows.Documents.Run(Fmt.LapSec(slot.Lap.LapSec)) { Foreground = brush, FontWeight = FontWeights.SemiBold });
                if (index == 0)
                    info.Inlines.Add(new System.Windows.Documents.Run("   référence") { Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)) });
                else if (_slots[0].Lap != null)
                {
                    double gap = slot.Lap.LapSec - _slots[0].Lap.LapSec;
                    info.Inlines.Add(new System.Windows.Documents.Run(string.Format(Inv, "   {0}{1:0.000} s", gap >= 0 ? "+" : "−", Math.Abs(gap)))
                    {
                        Foreground = new SolidColorBrush(gap <= 0 ? Color.FromRgb(0x66, 0xBB, 0x6A) : Color.FromRgb(0xEF, 0x9A, 0x9A)),
                    });
                    // Read from this lap's side: minus = faster than the reference
                    info.Inlines.Add(new System.Windows.Documents.Run(Math.Abs(gap) < 0.0005 ? "  même temps" : gap < 0 ? "  plus rapide" : "  plus lent")
                    {
                        Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                    });
                    info.ToolTip = string.Format(Inv, "Ce tour est {0:0.000} s {1} que la référence (1re ligne).\nVert = plus rapide qu'elle, rouge = plus lent.", Math.Abs(gap), gap < 0 ? "plus rapide" : "plus lent");
                }
                if (!string.IsNullOrEmpty(slot.Note))
                    info.Inlines.Add(new System.Windows.Documents.Run("   (" + slot.Note + ")") { Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), FontStyle = FontStyles.Italic });
            }
            grid.Children.Add(info);

            if (_slots.Count > 1)
            {
                var remove = new TextBlock { Text = "✕", FontSize = 13, Opacity = 0.6, Cursor = Cursors.Hand, ToolTip = "Retirer ce tour",
                                             VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                remove.MouseLeftButtonDown += (s, e) => { _slots.Remove(slot); Reload(true); e.Handled = true; };
                Grid.SetColumn(remove, 5);
                grid.Children.Add(remove);
            }
            return grid;
        }

        private static ComboBox Combo(int column)
        {
            var cb = new ComboBox { Height = 24, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                    MaxDropDownHeight = 420 };
            Grid.SetColumn(cb, column);
            return cb;
        }

        /// <summary>
        /// One session: every lap in order, flagged. All sessions: clean laps from the fastest
        /// (a full history would be thousands of laps).
        /// </summary>
        private static List<Item> LapItems(List<TeleLapRef> laps, string sessionKey)
        {
            var items = new List<Item>();
            if (sessionKey == null)
            {
                var clean = laps.FindAll(l => l.IsClean);
                clean.Sort((a, b) => a.LapSec.CompareTo(b.LapSec));
                for (int i = 0; i < clean.Count && i < 300; i++)
                {
                    var l = clean[i];
                    items.Add(new Item
                    {
                        Value = l,
                        Text = string.Format("{0}{1}   T{2} · {3}", Fmt.LapSec(l.LapSec), i == 0 ? " ★" : "", l.LapNum, l.SessionLabel),
                    });
                }
                return items;
            }
            var list = laps.FindAll(l => l.SessionKey == sessionKey);
            list.Sort((a, b) => a.LapNum.CompareTo(b.LapNum));
            var best = BestLap(laps, sessionKey);
            foreach (var l in list)
            {
                var remark = l.Remark;
                string time = remark == "invalide" ? "(" + Fmt.LapSec(l.LapSec) + ")" : Fmt.LapSec(l.LapSec);
                items.Add(new Item
                {
                    Value = l,
                    Text = string.Format("T{0,-3}  {1}{2}{3}", l.LapNum, time, l == best ? "  ★" : "",
                                         remark != null ? "   " + remark : ""),
                });
            }
            return items;
        }

        // ─── Data ─────────────────────────────────────────────────────────────

        private void Reload(bool keepView)
        {
            AssignColors();
            SyncPresets();
            BuildSlotRows();

            var traces = new List<TelemetryChart.Trace>();
            foreach (var s in _slots)
            {
                s.Trace = null;
                if (s.Lap == null) continue;
                try { s.Trace = TelemetryService.LoadTrace(s.Lap); }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn("[TeamPace] Trace load: " + ex.Message);
                    continue;
                }
                traces.Add(new TelemetryChart.Trace { Data = s.Trace, Color = s.Color });
            }

            // Time gap to the first lap, at each of its points
            if (traces.Count > 0)
            {
                var reference = traces[0].Data;
                for (int k = 1; k < traces.Count; k++)
                {
                    var other = traces[k].Data;
                    var delta = new float[reference.N];
                    for (int i = 0; i < reference.N; i++)
                    {
                        double t = other.TimeAt(reference.DistAt(i));
                        delta[i] = double.IsNaN(t) ? float.NaN : (float)(t - reference.Time[i]);
                    }
                    traces[k].Delta = delta;
                }
            }

            bool any = false;
            foreach (var kv in _laps) any |= kv.Value.Count > 0;
            _empty.Visibility = traces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = any
                ? "Choisissez un tour ci-dessus."
                : "Aucune télémétrie sur ce circuit pour les pilotes affichés.\n\nLMU enregistre la télémétrie de chaque relais : cliquez sur « ⟳ Tout mettre à jour » " +
                  "pour l'extraire et récupérer celle de vos coéquipiers. Elle est aussi extraite automatiquement à la fin de chaque session.";
            _chart.Visibility = traces.Count == 0 ? Visibility.Hidden : Visibility.Visible;

            _chart.SetData(traces, VisibleList(), keepView);
            _chart.SetTrack(_model);

            var lines = new List<TrackMap.Line>();
            double length = _model != null ? _model.Length : 0;
            foreach (var s in _slots)
            {
                if (s.Trace == null) continue;
                lines.Add(MapLine(s));
                if (_model == null) length = Math.Max(length, s.Trace.DistAt(s.Trace.N - 1));
            }
            _map.SetData(_model, lines, length);
            _map.SetView(_chart.ViewStart, _chart.ViewEnd);
            UpdateCorners();
            UpdateReadout(double.NaN);
        }

        /// <summary>Lap line on the map: centre line + lateral offset when the track model allows it.</summary>
        private TrackMap.Line MapLine(Slot s)
        {
            var t = s.Trace;
            var line = new TrackMap.Line { Color = s.Color, Trace = t, X = new double[t.N], Y = new double[t.N] };
            for (int i = 0; i < t.N; i++)
            {
                if (_model != null && t.HasLateral) _model.Position(t.DistAt(i), t.Lateral[i], out line.X[i], out line.Y[i]);
                else { line.X[i] = t.X[i]; line.Y[i] = t.Y[i]; }
            }
            TrackMap.FindMarkers(t, out line.BrakeIdx, out line.GasIdx);
            return line;
        }

        // ─── Corners ──────────────────────────────────────────────────────────

        /// <summary>
        /// Time gained or lost on the reference in each corner: the lap is split halfway between
        /// corners, so the corner times add up to the lap gap.
        /// </summary>
        private void UpdateCorners()
        {
            _cornerPanel.Children.Clear();
            _cornerRows.Clear();
            var refSlot = _slots.Count > 0 && _slots[0].Trace != null ? _slots[0] : null;
            if (_model == null || _model.Corners.Count == 0 || refSlot == null) return;

            var others = _slots.FindAll(x => x != refSlot && x.Trace != null);
            _cornerPanel.Children.Add(new TextBlock
            {
                Text = others.Count > 0 ? "Virages  ·  écart sur la référence  (vert : plus rapide qu'elle)" : "Virages  ·  temps de passage",
                FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(166, 255, 255, 255)), Margin = new Thickness(0, 0, 0, 4),
            });

            var sectors = _model.CornerSectors();
            for (int i = 0; i < _model.Corners.Count; i++)
            {
                var c = _model.Corners[i];
                double a = sectors[i].Key, b = Math.Min(sectors[i].Value, refSlot.Trace.DistAt(refSlot.Trace.N - 1));
                foreach (var o in others) b = Math.Min(b, o.Trace.DistAt(o.Trace.N - 1));
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                foreach (var o in (others.Count > 0 ? others : new List<Slot> { refSlot }))
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                grid.Children.Add(new TextBlock
                {
                    Text = string.Format(Inv, "{0}  {1}  {2:0}°", c.Name, c.Left ? "gauche" : "droite", c.Angle),
                    FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 200, 87)),
                });
                int col = 1;
                double tr = refSlot.Trace.TimeAt(b) - refSlot.Trace.TimeAt(a);
                foreach (var o in (others.Count > 0 ? others : new List<Slot> { refSlot }))
                {
                    var cell = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
                    if (o == refSlot)
                    {
                        cell.Text = double.IsNaN(tr) ? "—" : string.Format(Inv, "{0:0.00} s", tr);
                        cell.Foreground = new SolidColorBrush(o.Color);
                    }
                    else
                    {
                        double to = o.Trace.TimeAt(b) - o.Trace.TimeAt(a);
                        double gap = to - tr;
                        cell.Text = double.IsNaN(gap) ? "—" : string.Format(Inv, "{0}{1:0.00}", gap >= 0 ? "+" : "−", Math.Abs(gap));
                        cell.Foreground = new SolidColorBrush(double.IsNaN(gap) ? Color.FromArgb(120, 255, 255, 255)
                                          : gap > 0.02 ? Color.FromRgb(0xEF, 0x9A, 0x9A)
                                          : gap < -0.02 ? Color.FromRgb(0x66, 0xBB, 0x6A) : Color.FromArgb(200, 255, 255, 255));
                        cell.ToolTip = o.Driver.Name + " · T" + o.Lap.LapNum;
                    }
                    Grid.SetColumn(cell, col++);
                    grid.Children.Add(cell);
                }

                var row = new Border
                {
                    Child = grid, Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand,
                    Background = Brushes.Transparent, Tag = c,
                    ToolTip = string.Format(Inv, "{0} : de {1:0} à {2:0} m, rayon mini ≈ {3:0} m  ·  clic : zoom", c.Name, c.Start, c.End, c.MinRadius),
                };
                var corner = c;
                row.MouseLeftButtonDown += (sender, e) => { _chart.SetView(corner.Start - 120, corner.End + 60); e.Handled = true; };
                _cornerRows.Add(row);
                _cornerPanel.Children.Add(row);
            }
        }

        private void HighlightCorner(double dist)
        {
            var current = double.IsNaN(dist) || _model == null ? null : _model.CornerAt(dist);
            foreach (var row in _cornerRows)
                row.Background = row.Tag == current ? new SolidColorBrush(Color.FromArgb(36, 255, 200, 87)) : Brushes.Transparent;
        }

        private static readonly string[] ReadoutHeads = { "Δ", "Vitesse", "Rapport", "Accél.", "Frein", "Volant", "Régime", "Placement" };

        /// <summary>
        /// One line per lap above the curves: the values under the cursor, big enough to read
        /// while sweeping, or each lap's summary when the mouse is elsewhere.
        /// </summary>
        private void UpdateReadout(double dist)
        {
            _readout.Children.Clear();
            _readout.RowDefinitions.Clear();
            if (_readout.ColumnDefinitions.Count == 0)
            {
                _readout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
                foreach (var w in new[] { 82.0, 86, 64, 64, 64, 68, 82, 96 }) _readout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            }
            var live = _slots.Where(x => x.Trace != null).ToList();
            _readout.Visibility = live.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (live.Count == 0) return;
            bool cursor = !double.IsNaN(dist);
            var muted = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));

            _readout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bg = new Border { Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)), CornerRadius = new CornerRadius(6) };
            Grid.SetColumnSpan(bg, 9); Grid.SetRowSpan(bg, live.Count + 1);
            _readout.Children.Add(bg);
            AddCell(new TextBlock { Text = cursor ? string.Format(Inv, "À {0:0} m", dist) : "Sous la souris",
                                    FontSize = 12, Foreground = cursor ? Brushes.White : muted, FontWeight = cursor ? FontWeights.SemiBold : FontWeights.Normal }, 0, 0);
            for (int c = 0; c < ReadoutHeads.Length; c++)
                AddCell(new TextBlock { Text = ReadoutHeads[c], FontSize = 11.5, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Right }, 0, c + 1);

            Slot refSlot = live[0];
            int row = 1;
            foreach (var s in live)
            {
                var t = s.Trace;
                var brush = new SolidColorBrush(s.Color);
                _readout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = new Border
                {
                    BorderBrush = brush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(7, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = string.Format("{0}  ·  T{1}  ·  {2}", s.Driver.Name, s.Lap.LapNum, Fmt.LapSec(s.Lap.LapSec)),
                        FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = s.Lap.SessionLabel + (s.Lap.Remark != null ? "  ·  " + s.Lap.Remark : ""),
                    },
                };
                AddCell(name, row, 0);

                var vals = new string[8];
                for (int k = 0; k < 8; k++) vals[k] = "—";
                Brush gapBrush = Brushes.White;
                if (cursor && dist >= t.FirstDist && dist <= t.DistAt(t.N - 1))
                {
                    int i = t.IndexAt(dist);
                    if (s != refSlot)
                    {
                        double tr = refSlot.Trace.TimeAt(dist), ts = t.TimeAt(dist);
                        if (!double.IsNaN(tr) && !double.IsNaN(ts))
                        {
                            vals[0] = string.Format(Inv, "{0}{1:0.000}", ts - tr >= 0 ? "+" : "−", Math.Abs(ts - tr));
                            gapBrush = ts - tr > 0 ? Ui.Red : Ui.Green;
                        }
                    }
                    else vals[0] = "réf.";
                    vals[1] = string.Format(Inv, "{0:0} km/h", t.Speed[i]);
                    vals[2] = t.Gear[i] < 0 ? "R" : t.Gear[i] == 0 ? "N" : t.Gear[i].ToString(Inv);
                    vals[3] = string.Format(Inv, "{0:0} %", t.Throttle[i]);
                    vals[4] = string.Format(Inv, "{0:0} %", t.Brake[i]);
                    vals[5] = string.Format(Inv, "{0:+0;-0;0} %", t.Steer[i]);
                    vals[6] = string.Format(Inv, "{0:0}", t.Rpm[i]);
                    if (t.HasLateral) vals[7] = string.Format(Inv, "{0:0.0} m {1}", Math.Abs(t.Lateral[i]), t.Lateral[i] > 0 ? "dr." : "g.");
                }
                else if (!cursor)
                {
                    float top = 0;
                    for (int i = 0; i < t.N; i++) top = Math.Max(top, t.Speed[i]);
                    vals[1] = string.Format(Inv, "max {0:0}", top);
                }
                for (int k = 0; k < 8; k++)
                    AddCell(new TextBlock
                    {
                        Text = vals[k], FontSize = 15, FontWeight = FontWeights.SemiBold,
                        Foreground = k == 0 ? gapBrush : vals[k] == "—" ? muted : Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Right,
                    }, row, k + 1);
                row++;
            }
        }

        private void AddCell(FrameworkElement e, int row, int col)
        {
            e.Margin = new Thickness(col == 0 ? 8 : 4, row == 0 ? 5 : 2, col == 8 ? 10 : 4, 3);
            e.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(e, row); Grid.SetColumn(e, col);
            _readout.Children.Add(e);
        }
    }
}
