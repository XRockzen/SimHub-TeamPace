using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>
    /// History, one row per session: the same session run by several displayed drivers is one
    /// row with each driver's result side by side, grouped by day, newest first.
    /// </summary>
    internal class HistoryView : UserControl
    {
        public event Action<DriverView, LmuSessionRecord> SessionOpened;

        private class Event
        {
            public LmuSessionRecord[] Sessions;   // per driver
            public LmuSessionRecord Any;
            public DateTime Date;
            public int Count;
            public bool IsEmpty;
        }

        // Filters kept while browsing
        private static string _class, _circuit;
        private static readonly HashSet<string> _types = new HashSet<string>();   // "R", "Q", "P"; none = all
        private static readonly HashSet<string> _modes = new HashSet<string>();   // "Hors ligne", "En ligne", "Équipage"; none = all
        private static bool _commonOnly, _simOnly, _showEmpty;
        private const int PageSize = 200;

        private readonly List<DriverView> _drivers;
        private readonly List<Event> _events = new List<Event>();
        private readonly StackPanel _rows = new StackPanel();
        private readonly StackPanel _filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        private int _shown = PageSize;
        private Border _selected;

        public HistoryView(List<DriverView> drivers)
        {
            _drivers = drivers;
            Collect();
            var root = new DockPanel();
            DockPanel.SetDock(_filters, Dock.Top);
            root.Children.Add(_filters);
            var table = new DockPanel();
            var head = BuildHeader();
            DockPanel.SetDock(head, Dock.Top);
            table.Children.Add(head);
            table.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            root.Children.Add(new Border { Background = Ui.Frozen(Color.FromArgb(10, 255, 255, 255)), CornerRadius = new CornerRadius(8), Child = table });
            Content = root;
            Refresh();
        }

        // ─── Data ─────────────────────────────────────────────────────────────

        /// <summary>Every driver's sessions merged: a session matching another driver's joins its row.</summary>
        private void Collect()
        {
            var all = new List<KeyValuePair<int, LmuSessionRecord>>();
            for (int i = 0; i < _drivers.Count; i++)
                foreach (var s in _drivers[i].Sessions) all.Add(new KeyValuePair<int, LmuSessionRecord>(i, s));
            all.Sort((a, b) => b.Value.SessionDate.CompareTo(a.Value.SessionDate));

            foreach (var kv in all)
            {
                Event target = null;
                // Only recent rows can match (sessions within a few minutes)
                for (int k = _events.Count - 1; k >= 0 && k >= _events.Count - 40; k--)
                {
                    var e = _events[k];
                    if (e.Sessions[kv.Key] == null && SessionMatch.Same(e.Any, kv.Value)) { target = e; break; }
                }
                if (target == null)
                {
                    target = new Event { Sessions = new LmuSessionRecord[_drivers.Count], Any = kv.Value, Date = kv.Value.SessionDate };
                    _events.Add(target);
                }
                target.Sessions[kv.Key] = kv.Value;
                target.Count++;
            }
            foreach (var e in _events)
            {
                e.IsEmpty = true;
                foreach (var s in e.Sessions)
                    if (s != null && s.TotalLaps > 0 && s.BestLapTime.Ticks > 0) e.IsEmpty = false;
            }
        }

        private bool Passes(Event e)
        {
            var s = e.Any;
            if (_types.Count > 0 && !_types.Contains(s.IsRace ? "R" : s.IsQuali ? "Q" : "P")) return false;
            if (_modes.Count > 0 && !_modes.Contains(Mode(e))) return false;
            if (_class != null && LmuCategoryStyle.GetShortName(s.CarClass) != _class) return false;
            if (_circuit != null && s.TrackDisplayName != _circuit) return false;
            if (_commonOnly && e.Count < 2) return false;
            if (_simOnly && !s.IsFullSim) return false;
            return true;
        }

        // ─── Filters ──────────────────────────────────────────────────────────

        private void BuildFilters(int emptyCount)
        {
            _filters.Children.Clear();
            // Clickable tags, like the categories of the career: none lit = everything
            Action<string, HashSet<string>, string, string, string> tag = (key, set, text, bg, tip) =>
            {
                var b = Ui.Badge(text, bg, 11.5);
                b.Padding = new Thickness(9, 3, 9, 3);
                b.Margin = new Thickness(0, 0, 5, 0);
                b.Cursor = Cursors.Hand;
                b.ToolTip = tip;
                b.Opacity = set.Count == 0 || set.Contains(key) ? 1 : 0.3;
                b.MouseLeftButtonDown += (snd, ev) => { if (!set.Remove(key)) set.Add(key); _shown = PageSize; Refresh(); };
                _filters.Children.Add(b);
            };
            _filters.Children.Add(Ui.T("Type ", 12, Ui.Muted));
            tag("R", _types, "Courses", "#CC1565B0", "Afficher ou masquer les courses");
            tag("Q", _types, "Qualifs", "#CC6A1E8E", "Afficher ou masquer les qualifications");
            tag("P", _types, "Essais", "#CC2E7D32", "Afficher ou masquer les essais");
            _filters.Children.Add(new Border { Width = 14 });
            _filters.Children.Add(Ui.T("Mode ", 12, Ui.Muted));
            tag("Hors ligne", _modes, "Hors ligne", "#FF55585F", "Sessions hors ligne");
            tag("En ligne", _modes, "En ligne", "#FF1F6FB5", "En ligne, seul dans la voiture");
            tag("Équipage", _modes, "Équipage", "#FF5E4B8B", "En ligne, voiture partagée avec des coéquipiers");
            _filters.Children.Add(new Border { Width = 14 });

            var classes = new SortedSet<string>();
            var circuits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _events) { classes.Add(LmuCategoryStyle.GetShortName(e.Any.CarClass)); circuits.Add(e.Any.TrackDisplayName); }
            _filters.Children.Add(Combo("Catégorie : toutes", classes, _class, v => { _class = v; _shown = PageSize; Refresh(); }, 170));
            _filters.Children.Add(Combo("Circuit : tous", circuits, _circuit, v => { _circuit = v; _shown = PageSize; Refresh(); }, 300));

            if (_drivers.Count > 1)
            {
                var common = Ui.Button("⇄ Communes uniquement", "Seulement les sessions courues par au moins deux des pilotes affichés");
                common.Margin = new Thickness(0, 0, 6, 0);
                Ui.SetOn(common, _commonOnly);
                common.Click += (s, e) => { _commonOnly = !_commonOnly; _shown = PageSize; Refresh(); };
                _filters.Children.Add(common);
            }
            // Rare filters in a small menu
            var more = Ui.Button(_simOnly ? "⋯ sim complète" : "⋯", "Autres filtres");
            Ui.SetOn(more, _simOnly);
            more.Click += (s, e) =>
            {
                var menu = new ContextMenu();
                var item = new MenuItem { Header = "Seulement les sessions en simulation complète", IsCheckable = true, IsChecked = _simOnly,
                                          ToolTip = "Dégâts, carburant et usure réalistes" };
                item.Click += (a2, b2) => { _simOnly = !_simOnly; _shown = PageSize; Refresh(); };
                menu.Items.Add(item);
                menu.PlacementTarget = more;
                menu.IsOpen = true;
            };
            _filters.Children.Add(more);

            if (emptyCount > 0 || _showEmpty)
            {
                var link = Ui.T(_showEmpty ? "  ·  masquer les sessions vides" : string.Format("  ·  {0} session(s) vide(s) masquée(s), afficher", emptyCount), 12, Ui.Link);
                link.Cursor = Cursors.Hand;
                link.Margin = new Thickness(12, 0, 0, 0);
                link.ToolTip = "Sessions sans aucun tour chronométré";
                link.MouseLeftButtonDown += (s, e) => { _showEmpty = !_showEmpty; Refresh(); };
                _filters.Children.Add(link);
            }
        }

        private static ComboBox Combo(string all, IEnumerable<string> values, string current, Action<string> set, double width)
        {
            var cb = new ComboBox { Width = width, Height = 26, FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            cb.Items.Add(all);
            int sel = 0;
            foreach (var v in values) { cb.Items.Add(v); if (v == current) sel = cb.Items.Count - 1; }
            cb.SelectedIndex = sel;
            cb.SelectionChanged += (s, e) => set(cb.SelectedIndex <= 0 ? null : (string)cb.SelectedItem);
            return cb;
        }

        // ─── Rows ─────────────────────────────────────────────────────────────

        private bool TwoDrivers { get { return _drivers.Count == 2; } }
        private const double TimeColW = 64, CircuitMin = 280, TypeW = 52, CatW = 84, BestW = 104, PosW = 90, LapsW = 56, GapW = 108;

        private Grid NewGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeColW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = CircuitMin });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TypeW) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CatW) });
            for (int i = 0; i < _drivers.Count; i++)
            {
                if (TwoDrivers && i == 1) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GapW) });
                foreach (var w in new[] { BestW, PosW, LapsW }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            }
            return g;
        }

        private int DriverCol(int i) { return 4 + i * 3 + (TwoDrivers && i == 1 ? 1 : 0); }

        private UIElement BuildHeader()
        {
            var outer = new StackPanel();
            var top = NewGrid(); var bottom = NewGrid();
            top.Background = bottom.Background = Ui.HeaderBg;
            Place(top, Ui.T("Session", 12.5, Ui.Muted, FontWeights.SemiBold), 0, 4, new Thickness(10, 6, 6, 2));
            for (int i = 0; i < _drivers.Count; i++)
            {
                var d = _drivers[i];
                var band = new Border
                {
                    Background = Ui.Tint(d.Brush, 34), BorderBrush = d.Brush, BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 5, 6, 3),
                    Child = Ui.T(d.DbPath == null ? d.Name + "  (moi)" : d.Name, 13.5, Ui.Light(d.Brush), FontWeights.Bold),
                };
                Grid.SetColumn(band, DriverCol(i)); Grid.SetColumnSpan(band, 3);
                top.Children.Add(band);
            }
            if (TwoDrivers) Place(top, Ui.T("Écart", 12.5, Ui.Muted, FontWeights.SemiBold), DriverCol(1) - 1, 1, new Thickness(18, 6, 0, 2));

            var names = new[] { "Heure", "Circuit", "Type", "Cat." };
            for (int k = 0; k < 4; k++) Place(bottom, Ui.T(names[k], 12, Ui.Faint), k, 1, new Thickness(k == 0 ? 10 : 6, 2, 6, 5));
            for (int i = 0; i < _drivers.Count; i++)
            {
                var sub = new[] { "Meilleur", "Pos.", "Tours" };
                for (int k = 0; k < 3; k++)
                {
                    var cell = new Border
                    {
                        Background = Ui.Tint(_drivers[i].Brush, 18), BorderBrush = _drivers[i].Brush,
                        BorderThickness = new Thickness(k == 0 ? 3 : 0, 0, 0, 0), Padding = new Thickness(8, 2, 6, 5),
                        Child = Ui.T(sub[k], 12, Ui.Faint),
                    };
                    Grid.SetColumn(cell, DriverCol(i) + k);
                    bottom.Children.Add(cell);
                }
            }
            if (TwoDrivers) Place(bottom, Ui.T("meilleur tour", 12, Ui.Faint), DriverCol(1) - 1, 1, new Thickness(18, 2, 0, 5));
            outer.Children.Add(top);
            outer.Children.Add(bottom);
            return outer;
        }

        private void Refresh()
        {
            int empty = 0;
            var visible = new List<Event>();
            foreach (var e in _events)
            {
                if (!Passes(e)) continue;
                if (e.IsEmpty && !_showEmpty) { empty++; continue; }
                visible.Add(e);
            }
            BuildFilters(empty);

            _rows.Children.Clear();
            DateTime day = DateTime.MinValue;
            var culture = new CultureInfo("fr-FR");
            for (int i = 0; i < visible.Count && i < _shown; i++)
            {
                var e = visible[i];
                if (e.Date.Date != day)
                {
                    day = e.Date.Date;
                    var label = culture.TextInfo.ToTitleCase(e.Date.ToString("dddd d MMMM yyyy", culture));
                    _rows.Children.Add(new Border
                    {
                        Background = Ui.Frozen(Color.FromArgb(16, 255, 255, 255)), Padding = new Thickness(10, 5, 10, 5),
                        BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                        Child = Ui.T(e.Date == DateTime.MinValue ? "Date inconnue" : label, 12, Ui.Muted, FontWeights.SemiBold),
                    });
                }
                _rows.Children.Add(BuildRow(e));
            }
            if (visible.Count > _shown)
            {
                var more = Ui.Button(string.Format("Afficher plus  ({0} sessions restantes)", visible.Count - _shown));
                more.Margin = new Thickness(10);
                more.HorizontalAlignment = HorizontalAlignment.Left;
                more.Click += (s, ev) => { _shown += PageSize; Refresh(); };
                _rows.Children.Add(more);
            }
            if (visible.Count == 0)
                _rows.Children.Add(new TextBlock { Text = "Aucune session pour ces filtres.", Margin = new Thickness(12, 20, 0, 0), Foreground = Ui.Faint, FontStyle = FontStyles.Italic, FontSize = 13 });
        }

        /// <summary>The crew when the car was shared ("Amaury Marin 75 · Pascal Marin 51"), else null.</summary>
        private static string Crew(Event e)
        {
            foreach (var s in e.Sessions) if (s != null && !string.IsNullOrEmpty(s.TeamDrivers)) return s.TeamDrivers;
            return null;
        }

        private static string Mode(Event e)
        {
            if (Crew(e) != null) return "Équipage";
            return e.Any.IsOnline ? "En ligne" : "Hors ligne";
        }

        private Border BuildRow(Event e)
        {
            var g = NewGrid();
            g.MinHeight = 29;
            var s0 = e.Any;
            Place(g, Ui.T(e.Date == DateTime.MinValue ? "" : e.Date.ToString("HH:mm"), 12.5, Ui.Muted), 0, 1, new Thickness(10, 0, 4, 0));
            var circuit = Ui.T(s0.TrackDisplayName, 13);
            circuit.ToolTip = s0.TrackDisplayName;
            // Offline, online, or online with the car shared by the crew
            var crew = Crew(e);
            var mode = Mode(e);
            var badge = Ui.Badge(mode.ToUpperInvariant(), crew != null ? "#FF5E4B8B" : s0.IsOnline ? "#FF1F6FB5" : "#FF55585F", 10);
            badge.Margin = new Thickness(8, 0, 0, 0);
            badge.Opacity = crew != null ? 1 : 0.8;
            badge.ToolTip = crew != null ? "Voiture partagée · " + crew + " tours" : s0.IsOnline ? "En ligne, seul dans la voiture" : "Hors ligne";
            var dock = new DockPanel();
            DockPanel.SetDock(badge, Dock.Right);
            dock.Children.Add(badge);
            dock.Children.Add(circuit);
            Place(g, dock, 1, 1, new Thickness(6, 0, 6, 0));
            var typeRow = new LmuSessionRow { Record = s0 };
            Place(g, Ui.Badge(typeRow.TypeLabel, typeRow.TypeBg), 2, 1, new Thickness(6, 0, 0, 0));
            Place(g, Ui.Badge(typeRow.CarClassLabel, typeRow.CarClassBg), 3, 1, new Thickness(6, 0, 0, 0));

            long best = long.MaxValue; int timed = 0;
            foreach (var s in e.Sessions) if (s != null && s.BestLapTime.Ticks > 0) { timed++; best = Math.Min(best, s.BestLapTime.Ticks); }

            for (int i = 0; i < _drivers.Count; i++)
            {
                var d = _drivers[i];
                int col = DriverCol(i);
                for (int k = 0; k < 3; k++)
                {
                    var bg = new Border { Background = Ui.Tint(d.Brush, 14), BorderBrush = d.Brush, BorderThickness = new Thickness(k == 0 ? 3 : 0, 0, 0, 0) };
                    Grid.SetColumn(bg, col + k);
                    g.Children.Add(bg);
                }
                var s = e.Sessions[i];
                if (s == null) { Place(g, Ui.T("—", 13, Ui.Faint), col, 1, new Thickness(10, 0, 4, 0)); continue; }
                var row = new LmuSessionRow { Record = s };
                bool isBest = timed > 1 && s.BestLapTime.Ticks == best;
                Place(g, Ui.T((isBest ? "★ " : "") + row.BestLap, 13, isBest ? Ui.Green : Ui.Text, isBest ? FontWeights.Bold : FontWeights.Normal), col, 1, new Thickness(10, 0, 4, 0));
                Place(g, Ui.T(row.Position, 12.5, Ui.Muted), col + 1, 1, new Thickness(8, 0, 4, 0));
                Place(g, Ui.T(s.TotalLaps.ToString(), 12.5, Ui.Muted), col + 2, 1, new Thickness(8, 0, 4, 0));
            }
            if (TwoDrivers)
            {
                var a = e.Sessions[0]; var b = e.Sessions[1];
                TextBlock gap;
                if (a == null || b == null || a.BestLapTime.Ticks <= 0 || b.BestLapTime.Ticks <= 0) gap = Ui.T(a != null && b != null ? "—" : "seul", 12, Ui.Faint);
                else
                {
                    var diff = Fmt.Seconds(TimeSpan.FromTicks(Math.Abs(a.BestLapTime.Ticks - b.BestLapTime.Ticks)));
                    bool left = a.BestLapTime.Ticks <= b.BestLapTime.Ticks;
                    gap = Ui.T(left ? "◀ " + diff : diff + " ▶", 12.5, Ui.Light(left ? _drivers[0].Brush : _drivers[1].Brush), FontWeights.Bold);
                }
                gap.HorizontalAlignment = HorizontalAlignment.Center;
                Place(g, gap, DriverCol(1) - 1, 1, new Thickness(0));
            }

            var border = new Border
            {
                Child = g, Background = e.Count > 1 ? Ui.Frozen(Color.FromArgb(14, 255, 200, 87)) : Brushes.Transparent,
                BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand,
                ToolTip = e.Count > 1 ? "Session commune · double-clic : analyse côte à côte" : "Double-clic : analyse de la session",
            };
            var normal = border.Background;
            border.MouseLeftButtonDown += (snd, ev) =>
            {
                if (ev.ClickCount == 2)
                {
                    int idx = 0;
                    for (int i = 0; i < e.Sessions.Length; i++) if (e.Sessions[i] != null) { idx = i; break; }
                    var h = SessionOpened;
                    if (h != null) h(_drivers[idx], e.Sessions[idx]);
                }
                else
                {
                    if (_selected != null) _selected.Background = (Brush)_selected.Tag;
                    _selected = border;
                    border.Tag = normal;
                    border.Background = Ui.Selected;
                }
                ev.Handled = true;
            };
            return border;
        }

        private static void Place(Grid g, FrameworkElement e, int col, int span, Thickness margin)
        {
            e.Margin = margin;
            Grid.SetColumn(e, col);
            Grid.SetColumnSpan(e, span);
            g.Children.Add(e);
        }
    }
}
