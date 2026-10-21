using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace User.TeamPace
{
    /// <summary>
    /// Endurance planner, in three tabs. Préparation: the race, then the plan as a timeline of
    /// stints (drag a stop to move it, drop a driver on a stint, click a stop to set it up) with
    /// one card per stop. Analyse de course: a race that was run, against the plan, stop by stop,
    /// and against the other teams. Données mesurées: pace, consumption, wear and pit times
    /// measured on the team's sessions, each replaceable by hand.
    /// </summary>
    internal class StrategyView : UserControl
    {
        private static StrategyPlan _plan;   // the plan being edited, kept while browsing
        private static string _tab = "prep";
        private static string _raceId;       // race shown in the analysis tab
        private static int _selectedStop = -1;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Brush StopBrush = Ui.Frozen(Color.FromRgb(0xFF, 0xC8, 0x57));
        private static readonly Brush Late = Ui.Frozen(Color.FromRgb(0xEF, 0x53, 0x50));

        private readonly List<DriverView> _drivers;
        private StrategyEngine.Measures _measures;
        private readonly StackPanel _header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        private readonly StackPanel _tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        private readonly StackPanel _body = new StackPanel();
        private bool _building;

        /// <summary>Opens on the analysis of a race (from a session's analysis).</summary>
        public static void ShowRace(LmuSessionRecord race)
        {
            _tab = "race";
            _raceId = race != null ? race.FileId : null;
        }

        public StrategyView(List<DriverView> drivers, string circuit, string classLabel)
        {
            _drivers = drivers;
            if (_plan == null) _plan = StrategyPlan.Default();
            _plan.Normalize();
            if (circuit != null && (circuit != _plan.Circuit || classLabel != _plan.ClassLabel))
            {
                // Another circuit: the team's latest plan for it, else a fresh one
                var saved = StrategyEngine.SavedPlans().Where(p => p.Circuit == circuit && p.ClassLabel == classLabel).OrderByDescending(p => p.UpdatedUtc).FirstOrDefault();
                if (saved != null) { _plan = saved; _plan.Normalize(); }
                else { _plan = StrategyPlan.Default(); _plan.Circuit = circuit; _plan.ClassLabel = classLabel; }
            }
            if (_plan.Circuit == null) PickDefaultCircuit();
            if (_plan.Drivers.Count == 0) foreach (var d in drivers) _plan.Drivers.Add(d.Name);

            var root = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            root.Children.Add(_header);
            DockPanel.SetDock(_tabs, Dock.Top);
            root.Children.Add(_tabs);
            root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) });
            Content = root;

            BuildHeader();
            Remeasure();
        }

        private void PickDefaultCircuit()
        {
            LmuSessionRecord last = null;
            foreach (var d in _drivers) foreach (var s in d.Sessions) if (last == null || s.SessionDate > last.SessionDate) last = s;
            if (last != null) { _plan.Circuit = last.TrackDisplayName; _plan.ClassLabel = LmuCategoryStyle.GetShortName(last.CarClass); }
        }

        private void Remeasure()
        {
            _measures = _plan.Circuit == null ? null : StrategyEngine.Measure(_drivers, _plan.Circuit, _plan.ClassLabel, _plan.TankL);
            Rebuild();
        }

        private void Rebuild()
        {
            _building = true;
            BuildTabs();
            _body.Children.Clear();
            if (_measures == null) _body.Children.Add(Ui.T("Choisissez un circuit.", 13, Ui.Muted));
            else
            {
                StrategyEngine.ApplyOverrides(_measures, _plan);
                if (_tab == "race") BuildRace();
                else if (_tab == "data") _body.Children.Add(Card(MeasuresTable()));
                else BuildPreparation();
            }
            _building = false;
        }

        private void BuildTabs()
        {
            _tabs.Children.Clear();
            foreach (var t in new[] { new[] { "prep", "Préparation" }, new[] { "race", "Analyse de course" }, new[] { "data", "Données mesurées" } })
            {
                var key = t[0];
                var b = new Button { Content = t[1], FontSize = 14.5, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand };
                b.SetResourceReference(StyleProperty, "TabBtn");
                b.BorderThickness = new Thickness(0, 0, 0, 2);
                b.BorderBrush = _tab == key ? Ui.Frozen(Color.FromRgb(0x21, 0x96, 0xF3)) : Brushes.Transparent;
                b.Foreground = _tab == key ? Brushes.White : Ui.Muted;
                b.FontWeight = _tab == key ? FontWeights.SemiBold : FontWeights.Normal;
                b.Background = Brushes.Transparent;
                b.Click += (s, e) => { _tab = key; Rebuild(); };
                _tabs.Children.Add(b);
            }
        }

        // ─── Header: saved plans ──────────────────────────────────────────────

        private void BuildHeader()
        {
            _header.Children.Clear();
            var plans = StrategyEngine.SavedPlans();
            var cb = new ComboBox { Width = 340, Height = 26, FontSize = 12.5, Margin = new Thickness(0, 0, 8, 0), MaxDropDownHeight = 420,
                                    ToolTip = "Plans de l'équipe, enregistrés dans le dossier d'équipe" };
            cb.Items.Add(plans.Count > 0 ? string.Format("Plans de l'équipe ({0})…", plans.Count) : "Aucun plan enregistré");
            foreach (var p in plans)
                cb.Items.Add(new ComboBoxItem
                {
                    Tag = p,
                    Content = string.Format("{0}   ·   {1} le {2:dd/MM HH:mm}", p.Id,
                        string.IsNullOrEmpty(p.Author) ? "?" : p.Author, p.UpdatedUtc == DateTime.MinValue ? DateTime.Now : p.UpdatedUtc.ToLocalTime()),
                });
            cb.SelectedIndex = 0;
            cb.SelectionChanged += (s, e) =>
            {
                var item = cb.SelectedItem as ComboBoxItem;
                if (item == null) return;
                _plan = (StrategyPlan)item.Tag;
                _plan.Normalize();
                _selectedStop = -1;
                BuildHeader();
                Remeasure();
            };
            _header.Children.Add(cb);

            var name = new TextBox { Width = 240, Height = 26, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, Text = _plan.Id ?? "", ToolTip = "Nom du plan" };
            _header.Children.Add(name);
            var save = Ui.Button("Enregistrer", "Enregistre le plan dans le dossier d'équipe : chaque coéquipier le retrouve dans la liste");
            save.Margin = new Thickness(8, 0, 0, 0);
            save.Click += (s, e) =>
            {
                var id = name.Text.Trim();
                if (id.Length == 0) id = string.Format("{0} · {1} h", _plan.Circuit, _plan.DurationMin / 60.0);
                _plan.Id = id;
                bool shared = StrategyEngine.Save(_plan);
                BuildHeader();
                var t = Ui.T(shared ? "✔ enregistré et partagé avec l'équipe" : "⚠ enregistré sur ce PC seulement : dossier d'équipe introuvable", 12, shared ? Ui.Green : Ui.Amber);
                t.Margin = new Thickness(10, 0, 0, 0);
                _header.Children.Add(t);
            };
            _header.Children.Add(save);
            if (!string.IsNullOrEmpty(_plan.Id) && plans.Exists(p => p.Id == _plan.Id))
            {
                var del = Ui.Button("Supprimer", "Supprime ce plan pour toute l'équipe");
                del.Margin = new Thickness(6, 0, 0, 0);
                del.Click += (s, e) => { StrategyEngine.Delete(_plan.Id); _plan.Id = null; BuildHeader(); };
                _header.Children.Add(del);
            }
            var fresh = Ui.Button("Nouveau plan");
            fresh.Margin = new Thickness(6, 0, 0, 0);
            fresh.Click += (s, e) =>
            {
                var c = _plan.Circuit; var k = _plan.ClassLabel;
                _plan = StrategyPlan.Default();
                _plan.Circuit = c; _plan.ClassLabel = k;
                foreach (var d in _drivers) _plan.Drivers.Add(d.Name);
                _selectedStop = -1;
                BuildHeader();
                Remeasure();
            };
            _header.Children.Add(fresh);
            if (!string.IsNullOrEmpty(_plan.Author) && _plan.Author != LmuSessionStore.PlayerName)
            {
                var by = Ui.T(string.Format("plan de {0} : vos changements l'enregistrent à votre nom pour toute l'équipe", _plan.Author), 12, Ui.Muted);
                by.Margin = new Thickness(12, 0, 0, 0);
                _header.Children.Add(by);
            }
        }

        // ═══ Préparation ══════════════════════════════════════════════════════

        private void BuildPreparation()
        {
            _body.Children.Add(RaceInputs());
            foreach (var name in _plan.Drivers)
                if (!_drivers.Exists(d => d.Name == name))
                    _body.Children.Add(Warning(string.Format("{0} fait partie de ce plan mais n'est pas affiché : ajoutez-le avec « + Ajouter un pilote… » pour l'inclure.", name)));

            var sim = StrategyEngine.Simulate(_plan, _measures, false);
            if (sim.Stints.Count == 0)
            {
                foreach (var x in sim.Warnings) _body.Children.Add(Warning(x));
                return;
            }
            if (_selectedStop >= sim.Stops.Count || (_selectedStop < -1 && sim.Quali == null)) _selectedStop = -1;

            _body.Children.Add(Kpis(sim));
            _body.Children.Add(Timeline(sim));
            foreach (var x in sim.Warnings) _body.Children.Add(Warning(x));
            _body.Children.Add(StopCards(sim));
        }

        private UIElement RaceInputs()
        {
            var wrap = new WrapPanel();
            var circuits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _drivers)
                foreach (var s in d.Sessions) circuits.Add(s.TrackDisplayName + "  ·  " + LmuCategoryStyle.GetShortName(s.CarClass));
            var cb = new ComboBox { Height = 26, FontSize = 12.5, MaxDropDownHeight = 420, Width = 330 };
            foreach (var c in circuits) cb.Items.Add(c);
            cb.SelectedItem = _plan.Circuit + "  ·  " + _plan.ClassLabel;
            cb.SelectionChanged += (s, e) =>
            {
                if (_building || cb.SelectedItem == null) return;
                var parts = ((string)cb.SelectedItem).Split(new[] { "  ·  " }, StringSplitOptions.None);
                _plan.Circuit = parts[0]; _plan.ClassLabel = parts[1];
                _plan.Overrides.Clear();
                Dispatcher.BeginInvoke(new Action(Remeasure));
            };
            wrap.Children.Add(Field("Circuit · catégorie", cb));
            wrap.Children.Add(Field("Durée (h:mm)", Box(string.Format("{0}:{1:00}", _plan.DurationMin / 60, _plan.DurationMin % 60), v =>
            {
                var p = v.Split(':');
                int h, m = 0;
                if (!int.TryParse(p[0], out h) || (p.Length > 1 && !int.TryParse(p[1], out m))) return false;
                _plan.DurationMin = h * 60 + m;
                return _plan.DurationMin > 0;
            }, null, 70)));
            wrap.Children.Add(Field("Qualif roulée (min)", Box(_plan.QualiMin > 0 ? _plan.QualiMin.ToString() : "", v =>
            {
                int n = 0;
                if (v.Trim().Length > 0 && (!int.TryParse(v.Trim(), out n) || n < 0)) return false;
                _plan.QualiMin = n;
                return true;
            }, "Temps passé en piste en qualif. Ses pneus (arrêt 0) s'usent pendant la qualif ;\nl'arrêt 1 met les pneus du départ de la course. Vide = pas de qualif.", 60)));
            wrap.Children.Add(Field("Départ", Box(_plan.StartClock ?? "", v => { _plan.StartClock = v.Trim(); return true; }, "ex. 20:00, pour afficher l'heure de chaque arrêt", 70)));
            wrap.Children.Add(Field("Pneus disponibles", Box(_plan.TyresAvailable.ToString(), v => { int n; if (!int.TryParse(v, out n) || n < 4) return false; _plan.TyresAvailable = n; return true; }, "Nombre de pneus pour la qualif et la course, ceux du départ compris (20 = 5 trains)", 60)));
            wrap.Children.Add(Field("Réservoir (L)", Box(_plan.TankL.ToString(Inv), v => { double n; if (!double.TryParse(v.Replace(',', '.'), NumberStyles.Float, Inv, out n) || n < 1) return false; _plan.TankL = n; return true; }, null, 60)));
            wrap.Children.Add(Field("Relais maxi (min)", Box(_plan.MaxStintMin > 0 ? _plan.MaxStintMin.ToString() : "", v => { int n = 0; if (v.Trim().Length > 0 && !int.TryParse(v, out n)) return false; _plan.MaxStintMin = n; return true; }, "vide = selon le réservoir et l'énergie", 60)));
            wrap.Children.Add(Field("Conduite mini / pilote (min)", Box(_plan.MinDriveMin > 0 ? _plan.MinDriveMin.ToString() : "", v => { int n = 0; if (v.Trim().Length > 0 && !int.TryParse(v, out n)) return false; _plan.MinDriveMin = n; return true; }, "vide = pas de règle", 60)));

            var team = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 6) };
            foreach (var d in _drivers)
            {
                var key = d.Name;
                var check = new CheckBox { IsChecked = _plan.Drivers.Contains(key), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Pilote de la course" };
                check.Checked += (s, e) => { if (_building) return; if (!_plan.Drivers.Contains(key)) _plan.Drivers.Add(key); Rebuild(); };
                check.Unchecked += (s, e) => { if (_building) return; _plan.Drivers.Remove(key); Rebuild(); };
                team.Children.Add(check);
                team.Children.Add(Ui.T(d.Name + "   ", 13, Ui.Light(d.Brush), FontWeights.SemiBold));
            }
            wrap.Children.Add(Field("Pilotes", team));
            var c2 = Card(wrap);
            c2.Margin = new Thickness(0, 0, 0, 10);
            return c2;
        }

        private UIElement Kpis(StrategyEngine.Simulation sim)
        {
            var kpis = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 10) };
            kpis.Children.Add(Kpi("TOURS PRÉVUS", sim.Laps.ToString()));
            kpis.Children.Add(Kpi("ARRÊTS", sim.Stops.Count.ToString()));
            kpis.Children.Add(Kpi("TEMPS AUX STANDS", Duration(sim.PitLoss)));
            kpis.Children.Add(Kpi("PNEUS UTILISÉS", string.Format("{0} / {1}", sim.TyresUsed, _plan.TyresAvailable)));
            var driving = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var k in _plan.Drivers)
            {
                var d = _drivers.Find(x => x.Name == k);
                double t; sim.Driving.TryGetValue(k, out t);
                if (d == null) continue;
                var v = Ui.T(Fmt.TotalTime(TimeSpan.FromSeconds(t)) + "  ", 17, Ui.Light(d.Brush), FontWeights.SemiBold);
                v.ToolTip = d.Name;
                driving.Children.Add(v);
            }
            var dk = new StackPanel();
            dk.Children.Add(Ui.T("CONDUITE", 11, Ui.Muted));
            dk.Children.Add(driving);
            var dkc = Ui.Card(dk); dkc.Margin = new Thickness(0, 0, 8, 0);
            kpis.Children.Add(dkc);
            int longest = sim.Stints.Max(s => s.Laps);
            kpis.Children.Add(Kpi("RELAIS LE PLUS LONG", longest + " tours"));
            return kpis;
        }

        // ─── Timeline: stints and stops, edited in place ──────────────────────

        private class Block { public Border Box; public StrategyEngine.StintPlan Stint; public int FirstLap; }

        private UIElement Timeline(StrategyEngine.Simulation sim)
        {
            var panel = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            chips.Children.Add(Ui.T("Glisser sur un relais : ", 12, Ui.Muted));
            foreach (var k in _plan.Drivers)
            {
                var d = _drivers.Find(x => x.Name == k);
                if (d == null) continue;
                var chip = new Border
                {
                    BorderBrush = d.Brush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(0, 0, 6, 0), Background = Ui.Tint(d.Brush, 40), Cursor = Cursors.SizeAll,
                    Child = Ui.T(d.Name, 12.5, Brushes.White, FontWeights.SemiBold), ToolTip = "Glisser ce pilote sur un relais pour qu'il le prenne",
                };
                var driverName = d.Name;
                chip.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragDrop.DoDragDrop(chip, driverName, DragDropEffects.Copy); };
                chips.Children.Add(chip);
            }
            DockPanel.SetDock(chips, Dock.Right);
            head.Children.Add(chips);
            head.Children.Add(Title("Déroulé de la course", null));
            panel.Children.Add(head);

            // Hour ticks
            var ticks = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            double total = Math.Max(1, sim.Finish);
            int hours = (int)Math.Floor(total / 3600);
            double used = 0;
            for (int h = 0; h <= hours; h++)
            {
                double w = h < hours ? 3600 : total - hours * 3600;
                ticks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, w), GridUnitType.Star) });
                var t = Ui.T(h + " h", 11.5, Ui.Faint);
                Grid.SetColumn(t, h);
                ticks.Children.Add(t);
                used += w;
            }
            panel.Children.Add(ticks);

            var line = new Grid { Height = 66 };
            var blocks = new List<Block>();
            int firstLap = 1;
            if (sim.Quali != null)
            {
                // Stop 0, the qualifying, stop 1: before the hours of the race
                ticks.Margin = new Thickness(QualiWidth + 60, 0, 0, 3);
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                line.Children.Add(PreMarker(sim, 0, 0));
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(QualiWidth) });
                line.Children.Add(QualiBlock(sim, 1));
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                line.Children.Add(PreMarker(sim, 1, 2));
            }
            for (int i = 0; i < sim.Stints.Count; i++)
            {
                var st = sim.Stints[i];
                var d = _drivers.Find(x => x.Name == st.Driver);
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, st.End - st.Start), GridUnitType.Star) });
                var col = line.ColumnDefinitions.Count - 1;
                var before = i > 0 ? sim.Stops[i - 1] : sim.Quali != null ? sim.Quali.Stop1 : null;
                string tyres = before == null ? "pneus neufs"
                             : before.Wheels == 0 ? (i == 0 ? "pneus de la qualif" : "mêmes pneus")
                             : StrategyEngine.WheelsLabel(before.Wheels) + (before.Tyres > 1 ? " neufs" : " neuf");
                var text = new StackPanel { Margin = new Thickness(9, 6, 6, 6) };
                text.Children.Add(Ui.T(d != null ? d.Name : st.Driver, 13.5, Brushes.White, FontWeights.Bold));
                text.Children.Add(Ui.T(string.Format("T{0}–T{1} · {2}", firstLap, firstLap + st.Laps - 1, Fmt.TotalTime(TimeSpan.FromSeconds(st.End - st.Start))), 12, Brushes.White));
                text.Children.Add(Ui.T(tyres, 11.5, Ui.Frozen(Color.FromArgb(220, 255, 255, 255))));
                var box = new Border
                {
                    Background = d != null ? d.Brush : Brushes.Gray, Child = text, AllowDrop = true, ClipToBounds = true,
                    CornerRadius = new CornerRadius(i == 0 && sim.Quali == null ? 6 : 0, i == sim.Stints.Count - 1 ? 6 : 0, i == sim.Stints.Count - 1 ? 6 : 0, i == 0 && sim.Quali == null ? 6 : 0),
                    ToolTip = string.Format("Relais {0} : {1} tours, de {2} à {3}\nClic : choisir le pilote · ou glisser un pilote dessus", i + 1, st.Laps, Clock(st.Start), Clock(st.End)),
                    Cursor = Cursors.Hand,
                };
                int stint = i;
                box.Drop += (s, e) =>
                {
                    var name = e.Data.GetData(typeof(string)) as string;
                    if (name != null) SetDriver(sim, stint, name);
                };
                box.MouseLeftButtonUp += (s, e) =>
                {
                    var menu = new ContextMenu();
                    foreach (var k in _plan.Drivers)
                    {
                        var dv = _drivers.Find(x => x.Name == k);
                        if (dv == null) continue;
                        var item = new MenuItem { Header = dv.Name, IsChecked = dv.Name == st.Driver };
                        var nm = dv.Name;
                        item.Click += (a, b) => SetDriver(sim, stint, nm);
                        menu.Items.Add(item);
                    }
                    menu.PlacementTarget = box;
                    menu.IsOpen = true;
                };
                Grid.SetColumn(box, col);
                line.Children.Add(box);
                blocks.Add(new Block { Box = box, Stint = st, FirstLap = firstLap });
                firstLap += st.Laps;

                if (i < sim.Stops.Count)
                {
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                    line.Children.Add(StopMarker(sim, i, blocks, line, line.ColumnDefinitions.Count - 1));
                }
            }
            panel.Children.Add(line);
            var c = Card(panel);
            c.Margin = new Thickness(0, 0, 0, 10);
            return c;
        }

        private const double QualiWidth = 150;

        /// <summary>The qualifying on the timeline: its driver (click or drop one on it) and its laps.</summary>
        private UIElement QualiBlock(StrategyEngine.Simulation sim, int col)
        {
            var q = sim.Quali;
            var d = _drivers.Find(x => x.Name == q.Driver);
            var text = new StackPanel { Margin = new Thickness(9, 6, 6, 6) };
            text.Children.Add(Ui.T(d != null ? d.Name : q.Driver, 13.5, Brushes.White, FontWeights.Bold));
            text.Children.Add(Ui.T(string.Format("Qualif · ≈ {0} tours", q.Laps), 12, Brushes.White));
            text.Children.Add(Ui.T(q.Stop0.Wheels == StrategyEngine.AllWheels ? "pneus neufs" : StrategyEngine.WheelsLabel(q.Stop0.Wheels) + " neufs", 11.5, Ui.Frozen(Color.FromArgb(220, 255, 255, 255))));
            var brush = d != null ? d.Brush : (SolidColorBrush)Brushes.Gray;
            var box = new Border
            {
                Background = Ui.Tint(brush, 110),
                Child = text, AllowDrop = true, ClipToBounds = true, CornerRadius = new CornerRadius(6, 0, 0, 6), Cursor = Cursors.Hand,
                BorderBrush = brush, BorderThickness = new Thickness(1),
                ToolTip = string.Format("Qualif : {0} min en piste, ≈ {1} tours\nL'usure de ces pneus est reportée sur le départ de la course (arrêt 1)\nClic : choisir le pilote · ou glisser un pilote dessus", _plan.QualiMin, q.Laps),
            };
            box.Drop += (s, e) =>
            {
                var name = e.Data.GetData(typeof(string)) as string;
                if (name != null) { _plan.QualiDriver = name; Dispatcher.BeginInvoke(new Action(Rebuild)); }
            };
            box.MouseLeftButtonUp += (s, e) =>
            {
                var menu = new ContextMenu();
                foreach (var k in _plan.Drivers)
                {
                    var dv = _drivers.Find(x => x.Name == k);
                    if (dv == null) continue;
                    var item = new MenuItem { Header = dv.Name, IsChecked = dv.Name == q.Driver };
                    var nm = dv.Name;
                    item.Click += (a, b) => { _plan.QualiDriver = nm; Dispatcher.BeginInvoke(new Action(Rebuild)); };
                    menu.Items.Add(item);
                }
                menu.PlacementTarget = box;
                menu.IsOpen = true;
            };
            Grid.SetColumn(box, col);
            return box;
        }

        /// <summary>Stop 0 (before the qualifying) or 1 (before the race): click to set its tyres.</summary>
        private UIElement PreMarker(StrategyEngine.Simulation sim, int number, int col)
        {
            bool selected = _selectedStop == -10 - number;
            var marker = new Border
            {
                Background = selected ? StopBrush : Ui.Frozen(Color.FromRgb(0x1B, 0x1C, 0x20)), BorderBrush = StopBrush,
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = number.ToString(), FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                        Foreground = selected ? Ui.Frozen(Color.FromRgb(0x22, 0x23, 0x28)) : StopBrush },
                ToolTip = number == 0 ? "Arrêt 0 · pneus de la qualif\nClic pour le régler" : "Arrêt 1 · pneus du départ de la course\nClic pour le régler",
            };
            marker.MouseLeftButtonUp += (s, e) => { _selectedStop = -10 - number; Dispatcher.BeginInvoke(new Action(Rebuild)); e.Handled = true; };
            Grid.SetColumn(marker, col);
            return marker;
        }

        private void SetDriver(StrategyEngine.Simulation sim, int stint, string name)
        {
            var list = new List<string>();
            for (int k = 0; k < sim.Stints.Count; k++) list.Add(sim.Stints[k].Driver);
            list[stint] = name;
            _plan.StintDrivers = list;
            Dispatcher.BeginInvoke(new Action(Rebuild));
        }

        /// <summary>A stop between two stints: click to select it, drag it to move it to another lap.</summary>
        private UIElement StopMarker(StrategyEngine.Simulation sim, int index, List<Block> blocks, Grid line, int col)
        {
            var stop = sim.Stops[index];
            bool selected = index == _selectedStop;
            var label = new TextBlock { Text = (index + sim.FirstStop).ToString(), FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                        Foreground = selected ? Ui.Frozen(Color.FromRgb(0x22, 0x23, 0x28)) : StopBrush };
            var marker = new Border
            {
                Background = selected ? StopBrush : Ui.Frozen(Color.FromRgb(0x1B, 0x1C, 0x20)), BorderBrush = StopBrush,
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Child = label, Cursor = Cursors.SizeWE,
                ToolTip = string.Format(Inv, "Arrêt {0} · T{1} · {2}{3}\nGlisser pour le déplacer · clic pour le régler", index + sim.FirstStop, stop.Lap, Clock(stop.At), stop.LapChosen ? " · déplacé à la main" : ""),
            };
            Grid.SetColumn(marker, col);

            Point origin = new Point();
            bool dragging = false;
            int target = stop.Lap;
            marker.MouseLeftButtonDown += (s, e) => { origin = e.GetPosition(line); dragging = false; marker.CaptureMouse(); e.Handled = true; };
            marker.MouseMove += (s, e) =>
            {
                if (!marker.IsMouseCaptured) return;
                var pos = e.GetPosition(line);
                if (!dragging && Math.Abs(pos.X - origin.X) < 4) return;
                dragging = true;
                target = LapAt(blocks, line, pos.X);
                label.Text = "T" + target;
                label.FontSize = 10;
            };
            marker.MouseLeftButtonUp += (s, e) =>
            {
                if (!marker.IsMouseCaptured) return;
                marker.ReleaseMouseCapture();
                if (dragging && target > 0 && target != stop.Lap)
                {
                    while (_plan.StopLaps.Count <= index) _plan.StopLaps.Add(-1);
                    _plan.StopLaps[index] = target;
                    // The following stops find their place again from there
                    for (int k = index + 1; k < _plan.StopLaps.Count; k++) if (_plan.StopLaps[k] > 0 && _plan.StopLaps[k] <= target) _plan.StopLaps[k] = -1;
                }
                _selectedStop = index;
                Dispatcher.BeginInvoke(new Action(Rebuild));
                e.Handled = true;
            };
            return marker;
        }

        /// <summary>The lap under a horizontal position of the timeline.</summary>
        private static int LapAt(List<Block> blocks, Grid line, double x)
        {
            Block last = null;
            foreach (var b in blocks)
            {
                var left = b.Box.TranslatePoint(new Point(0, 0), line).X;
                if (x < left && last != null) return last.FirstLap + last.Stint.Laps - 1;
                if (x >= left && x <= left + b.Box.ActualWidth)
                {
                    double frac = (x - left) / Math.Max(1, b.Box.ActualWidth);
                    return Math.Max(1, b.FirstLap + (int)Math.Round(frac * b.Stint.Laps) - 1);
                }
                last = b;
            }
            return last != null ? last.FirstLap + last.Stint.Laps - 1 : 0;
        }

        // ─── One card per stop ────────────────────────────────────────────────

        private UIElement StopCards(StrategyEngine.Simulation sim)
        {
            int pre = sim.Quali != null ? 2 : 0;
            if (sim.Stops.Count + pre == 0) return Ui.T("Aucun arrêt nécessaire.", 13, Ui.Muted);
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = Math.Min(4, sim.Stops.Count + pre) };
            for (int n = 0; n < pre; n++) grid.Children.Add(PreCard(sim, n));
            for (int i = 0; i < sim.Stops.Count; i++) grid.Children.Add(StopCard(sim, i));
            return grid;
        }

        /// <summary>Stop 0 (the qualifying tyres) or stop 1 (the tyres the race starts on).</summary>
        private UIElement PreCard(StrategyEngine.Simulation sim, int n)
        {
            var q = sim.Quali;
            var s = n == 0 ? q.Stop0 : q.Stop1;
            var p = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var when = Ui.T(n == 0 ? "avant la qualif" : "avant la course", 12.5, Ui.Muted);
            DockPanel.SetDock(when, Dock.Right);
            head.Children.Add(when);
            head.Children.Add(Ui.T("Arrêt " + n, 15, Ui.Text, FontWeights.Bold));
            p.Children.Add(head);
            p.Children.Add(Line("Pneus", TyrePicker(s, n == 1, w =>
            {
                if (n == 0) _plan.QualiWheels = w; else _plan.GridWheels = w;
                Dispatcher.BeginInvoke(new Action(Rebuild));
            })));
            var d = _drivers.Find(x => x.Name == s.NextDriver);
            var who = Ui.T(n == 0 ? string.Format("{0} · ≈ {1} tours", s.NextDriver, q.Laps) : s.NextDriver, 13, d != null ? Ui.Light(d.Brush) : Ui.Text, FontWeights.SemiBold);
            p.Children.Add(Line(n == 0 ? "Qualif" : "Départ", who));
            if (n == 1) p.Children.Add(Line(sim.EnergyMode ? "Énergie" : "Essence", Ui.T(sim.EnergyMode ? "100 % au départ" : "plein au départ", 13, Ui.Text)));
            var foot = Ui.T(n == 0 ? (s.Tyres > 0 ? string.Format("{0} pneu{1} du stock", s.Tyres, s.Tyres > 1 ? "s" : "") : "aucun pneu du stock")
                                   : string.Format(Inv, "usure en fin de qualif : {0:0} % au plus", s.WearIn.Max()), 12.5, Ui.Muted);
            p.Children.Add(new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = foot, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(0, 6, 0, 0) });
            var card = new Border
            {
                Background = Ui.Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 10), Child = p,
                BorderBrush = _selectedStop == -10 - n ? StopBrush : Brushes.Transparent, BorderThickness = new Thickness(1),
            };
            card.MouseLeftButtonDown += (snd, e) => { if (_selectedStop != -10 - n) { _selectedStop = -10 - n; Dispatcher.BeginInvoke(new Action(Rebuild)); } };
            return card;
        }

        private UIElement StopCard(StrategyEngine.Simulation sim, int i)
        {
            var s = sim.Stops[i];
            bool last = i == sim.Stops.Count - 1;
            var p = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var when = Ui.T(Clock(s.At), 12.5, Ui.Muted);
            DockPanel.SetDock(when, Dock.Right);
            head.Children.Add(when);
            head.Children.Add(Ui.T(string.Format("Arrêt {0} · T{1}", i + sim.FirstStop, s.Lap), 15, Ui.Text, FontWeights.Bold));
            p.Children.Add(head);

            p.Children.Add(Line(sim.EnergyMode ? "Énergie" : "Essence", LevelBox(i, s, last, sim.EnergyMode)));
            p.Children.Add(Line("Pneus", TyrePicker(s, true, w =>
            {
                while (_plan.StopWheels.Count <= i) _plan.StopWheels.Add(-1);
                while (_plan.StopTyres.Count <= i) _plan.StopTyres.Add(-1);
                _plan.StopWheels[i] = w;
                _plan.StopTyres[i] = w < 0 ? -1 : StrategyEngine.Count(w);   // read by older versions
                Dispatcher.BeginInvoke(new Action(Rebuild));
            })));
            var prev = i < sim.Stints.Count ? sim.Stints[i].Driver : "";
            var next = _drivers.Find(x => x.Name == s.NextDriver);
            var relay = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            if (prev == s.NextDriver) relay.Inlines.Add(new System.Windows.Documents.Run("même pilote (" + prev + ")") { Foreground = Ui.Muted });
            else
            {
                relay.Inlines.Add(new System.Windows.Documents.Run(prev + " → ") { Foreground = Ui.Text });
                relay.Inlines.Add(new System.Windows.Documents.Run(s.NextDriver) { Foreground = next != null ? Ui.Light(next.Brush) : Ui.Text, FontWeight = FontWeights.Bold });
            }
            p.Children.Add(Line("Relais", relay));
            if (s.LapChosen)
            {
                var reset = Ui.T("déplacé à la main · remettre en automatique", 11.5, Ui.Link);
                reset.Cursor = Cursors.Hand;
                reset.MouseLeftButtonDown += (snd, e) =>
                {
                    if (i < _plan.StopLaps.Count) _plan.StopLaps[i] = -1;
                    Dispatcher.BeginInvoke(new Action(Rebuild));
                    e.Handled = true;
                };
                p.Children.Add(reset);
            }
            var foot = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var lost = Ui.T(string.Format(Inv, "perdu +{0:0} s", s.Lost), 13, Ui.Red);
            lost.ToolTip = "Voie des stands comprise";
            DockPanel.SetDock(lost, Dock.Right);
            foot.Children.Add(lost);
            foot.Children.Add(Ui.T(string.Format(Inv, "arrêt {0:0} s", s.Stopped), 13, Ui.Text));
            p.Children.Add(new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = foot, Margin = new Thickness(0, 4, 0, 0) });

            var card = new Border
            {
                Background = Ui.Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 10), Child = p,
                BorderBrush = i == _selectedStop ? StopBrush : Brushes.Transparent, BorderThickness = new Thickness(1),
            };
            card.MouseLeftButtonDown += (snd, e) => { if (_selectedStop != i) { _selectedStop = i; Dispatcher.BeginInvoke(new Action(Rebuild)); } };
            return card;
        }

        private static UIElement Line(string label, FrameworkElement value)
        {
            var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2), MinHeight = 26 };
            var l = Ui.T(label, 12.5, Ui.Muted);
            l.Width = 76;
            DockPanel.SetDock(l, Dock.Left);
            d.Children.Add(l);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            d.Children.Add(value);
            return d;
        }

        private static readonly string[] WheelNames = { "Avant gauche", "Avant droit", "Arrière gauche", "Arrière droit" };
        private static readonly Brush NewTyre = Ui.Frozen(Color.FromRgb(0x42, 0xA5, 0xF5));

        /// <summary>
        /// The car seen from above: each tyre coloured by what is left of it when the car comes
        /// in; a click changes it or keeps it (blue = new tyre). "auto" goes back to the automatic
        /// choice: an axle is changed when one of its tyres would pass the limit in the next stint.
        /// </summary>
        private FrameworkElement TyrePicker(StrategyEngine.StopPlan s, bool showWear, Action<int> set)
        {
            var panel = new StackPanel();
            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var w in new[] { 40.0, 8, 24, 8, 40 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            foreach (var h in new[] { 22.0, 8, 22 }) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
            var body = new Border { CornerRadius = new CornerRadius(8, 8, 5, 5), Background = Ui.Frozen(Color.FromArgb(20, 255, 255, 255)),
                                    BorderBrush = Ui.Frozen(Color.FromArgb(51, 255, 255, 255)), BorderThickness = new Thickness(1), Margin = new Thickness(0, -2, 0, -2) };
            Grid.SetColumn(body, 2); Grid.SetRowSpan(body, 3);
            g.Children.Add(body);
            foreach (var row in new[] { 0, 2 })
            {
                var axle = new Border { Height = 2, Background = Ui.Frozen(Color.FromArgb(51, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(axle, 1); Grid.SetColumnSpan(axle, 3); Grid.SetRow(axle, row);
                g.Children.Add(axle);
            }
            for (int c = 0; c < 4; c++)
            {
                int bit = 1 << c;
                bool on = (s.Wheels & bit) != 0;
                double worn = s.WearIn != null ? s.WearIn[c] : 0;
                var color = WearColor(worn);
                var tyre = new Border
                {
                    CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Margin = new Thickness(on ? 0 : 1),
                    Background = on ? NewTyre : new SolidColorBrush(Color.FromArgb(showWear ? (byte)230 : (byte)60, color.R, color.G, color.B)),
                    BorderBrush = on ? Brushes.White : Brushes.Transparent, BorderThickness = new Thickness(on ? 1.5 : 0),
                    ToolTip = string.Format(Inv, "{0}{1}\nClic : {2}", WheelNames[c],
                        showWear ? string.Format(Inv, " · reste {0:0} % à l'arrivée (usé à {1:0} %)", 100 - worn, worn) : "",
                        on ? "garder ce pneu" : "le changer"),
                };
                tyre.MouseLeftButtonDown += (snd, e) => { set(s.Wheels ^ bit); e.Handled = true; };
                Grid.SetRow(tyre, c < 2 ? 0 : 2);
                Grid.SetColumn(tyre, c % 2 == 0 ? 1 : 3);
                g.Children.Add(tyre);
                var label = Ui.T(on ? "neuf" : showWear ? string.Format(Inv, "{0:0} %", 100 - worn) : "—", 12, on ? NewTyre : showWear ? new SolidColorBrush(color) : Ui.Faint, on ? FontWeights.SemiBold : FontWeights.Normal);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.HorizontalAlignment = c % 2 == 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                label.Margin = c % 2 == 0 ? new Thickness(0, 0, 6, 0) : new Thickness(6, 0, 0, 0);
                Grid.SetRow(label, c < 2 ? 0 : 2);
                Grid.SetColumn(label, c % 2 == 0 ? 0 : 4);
                g.Children.Add(label);
            }
            panel.Children.Add(g);
            var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            foot.Children.Add(Ui.T(StrategyEngine.WheelsLabel(s.Wheels), 12, Ui.Text));
            if (s.TyresChosen)
            {
                var reset = Ui.T("  · auto", 12, Ui.Link);
                reset.Cursor = Cursors.Hand;
                reset.ToolTip = "Revenir au choix automatique";
                reset.MouseLeftButtonDown += (snd, e) => { set(-1); e.Handled = true; };
                foot.Children.Add(reset);
            }
            else foot.Children.Add(Ui.T("  · auto", 12, Ui.Faint));
            panel.Children.Add(foot);
            return panel;
        }

        /// <summary>Green while the tyre is fresh, amber halfway to the limit, coral past it.</summary>
        private static Color WearColor(double worn)
        {
            double k = Math.Max(0, Math.Min(1, worn / StrategyEngine.WearLimit));
            var green = Color.FromRgb(0x4C, 0xAF, 0x7D); var amber = Color.FromRgb(0xF2, 0xB1, 0x3C); var coral = Color.FromRgb(0xEF, 0x6C, 0x5A);
            Func<Color, Color, double, Color> mix = (a, b, t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
            return k < 0.5 ? mix(green, amber, k * 2) : mix(amber, coral, (k - 0.5) * 2);
        }

        /// <summary>
        /// The level asked for when leaving the pits (virtual energy %, litres without it), as in
        /// LMU's pit menu. Empty = automatic: full, or just what the last stint needs.
        /// </summary>
        private FrameworkElement LevelBox(int stop, StrategyEngine.StopPlan s, bool last, bool energy)
        {
            string unit = energy ? "%" : "L";
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var added = Ui.T(string.Format(Inv, "+{0:0} {1}  ", energy ? s.EnergyPct : s.FuelL, unit), 12, Ui.Faint);
            added.ToolTip = energy ? string.Format(Inv, "Remis à l'arrêt : +{0:0} % d'énergie, environ {1:0} L d'essence", s.EnergyPct, s.FuelL) : "Remis à l'arrêt";
            row.Children.Add(added);
            var box = new TextBox
            {
                Width = 52, Height = 24, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right,
                Text = string.Format(Inv, "{0:0}", s.Level), FontWeight = s.FuelChosen ? FontWeights.Bold : FontWeights.Normal,
                BorderBrush = s.FuelChosen ? Ui.Amber : Ui.Frozen(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(s.FuelChosen ? 2 : 1),
                ToolTip = string.Format("Niveau à la sortie des stands, comme dans le menu des stands de LMU.\nVide = automatique : {0}.", last ? "juste ce qu'il faut jusqu'au drapeau" : "le plein"),
            };
            Action apply = () =>
            {
                if (_building) return;
                var text = (box.Text ?? "").Replace("%", "").Replace("L", "").Replace(',', '.').Trim();
                double v = -1;
                if (text.Length > 0 && (!double.TryParse(text, NumberStyles.Float, Inv, out v) || v < 0)) { box.BorderBrush = Ui.Red; return; }
                if (text.Length > 0 && !s.FuelChosen && Math.Abs(v - s.Level) < 0.5) return;   // the automatic value, untouched
                while (_plan.StopLevel.Count <= stop) _plan.StopLevel.Add(-1);
                _plan.StopLevel[stop] = v;
                Dispatcher.BeginInvoke(new Action(Rebuild));
            };
            box.LostFocus += (snd, e) => apply();
            box.KeyDown += (snd, e) => { if (e.Key == Key.Enter) { apply(); Keyboard.ClearFocus(); } };
            row.Children.Add(box);
            row.Children.Add(Ui.T(" " + unit, 13, Ui.Muted));
            if (s.FuelChosen)
            {
                var reset = Ui.T("  auto", 12, Ui.Link);
                reset.Cursor = Cursors.Hand;
                reset.ToolTip = "Revenir au niveau automatique";
                reset.MouseLeftButtonDown += (snd, e) =>
                {
                    if (stop < _plan.StopLevel.Count) _plan.StopLevel[stop] = -1;
                    Dispatcher.BeginInvoke(new Action(Rebuild));
                    e.Handled = true;
                };
                row.Children.Add(reset);
            }
            return row;
        }


        /// <summary>Races run on this circuit and category by the displayed drivers, one per race.</summary>
        private List<LmuSessionRecord> Races()
        {
            var list = new List<LmuSessionRecord>();
            foreach (var d in _drivers)
                foreach (var s in d.Sessions)
                {
                    if (!s.IsRace || s.TrackDisplayName != _plan.Circuit || LmuCategoryStyle.GetShortName(s.CarClass) != _plan.ClassLabel) continue;
                    if (s.TotalLaps == 0 && s.CarLaps == 0) continue;   // joined but never drove
                    if (list.Exists(x => SessionMatch.Same(x, s))) continue;
                    list.Add(s);
                }
            list.Sort((a, b) => b.SessionDate.CompareTo(a.SessionDate));
            return list;
        }

        private static string RaceLabel(LmuSessionRecord r)
        {
            var row = new LmuSessionRow { Record = r };
            return string.Format("{0:dd/MM · HH:mm} · {1} · {2} tours", r.SessionDate, row.Position, r.CarLaps > 0 ? r.CarLaps : r.TotalLaps);
        }

        // ═══ Analyse de course ════════════════════════════════════════════════

        private void BuildRace()
        {
            var races = Races();
            if (races.Count == 0)
            {
                _body.Children.Add(Ui.T("Aucune course sur ce circuit dans cette catégorie pour les pilotes affichés.", 13, Ui.Muted));
                return;
            }
            var race = races.FirstOrDefault(r => r.FileId == _raceId) ?? races.FirstOrDefault(r => _drivers.Exists(d => { var m = d.FindMatch(r); return m != null && m.FileId == _raceId; })) ?? races[0];
            _raceId = race.FileId;

            var pick = new ComboBox { Width = 380, Height = 26, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var r in races) pick.Items.Add(new ComboBoxItem { Content = "Course du " + RaceLabel(r), Tag = r });
            foreach (ComboBoxItem it in pick.Items) if (it.Tag == race) pick.SelectedItem = it;
            pick.SelectionChanged += (s, e) =>
            {
                if (_building) return;
                _raceId = ((LmuSessionRecord)((ComboBoxItem)pick.SelectedItem).Tag).FileId;
                Dispatcher.BeginInvoke(new Action(Rebuild));
            };
            _body.Children.Add(pick);

            _body.Children.Add(new RaceReviewView(_drivers, race, _plan));
        }

        // ═══ Données mesurées ═════════════════════════════════════════════════

        private UIElement MeasuresTable()
        {
            var panel = new StackPanel();
            panel.Children.Add(Title("Données de l'équipe", null));
            var drivers = _measures.Drivers.FindAll(d => _plan.Drivers.Contains(d.Driver.Name));

            Func<Grid> newRow = () =>
            {
                var g = new Grid { MinHeight = 30 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                foreach (var d in drivers) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                return g;
            };
            var header = newRow();
            header.Children.Add(Ui.T("Mesure", 12, Ui.Faint));
            for (int i = 0; i < drivers.Count; i++)
            {
                var t = Ui.T(drivers[i].Driver.Name, 12, Ui.Light(drivers[i].Driver.Brush), FontWeights.SemiBold);
                Grid.SetColumn(t, i + 1);
                header.Children.Add(t);
            }
            var src = Ui.T("Source", 12, Ui.Faint);
            Grid.SetColumn(src, drivers.Count + 1);
            header.Children.Add(src);
            panel.Children.Add(header);

            foreach (var p in StrategyEngine.DriverParams)
            {
                var g = newRow();
                g.Children.Add(Ui.T(StrategyEngine.Label(p), 13));
                string source = null;
                for (int i = 0; i < drivers.Count; i++)
                {
                    var v = drivers[i].Values[p];
                    var cell = ValueBox(p, drivers[i].Driver.Name, v);
                    Grid.SetColumn(cell, i + 1);
                    g.Children.Add(cell);
                    if (source == null) source = v.Overridden ? "modifié à la main" : v.Source;
                }
                var s = Ui.T(source ?? "", 11.5, Ui.Faint);
                s.ToolTip = source;
                Grid.SetColumn(s, drivers.Count + 1);
                g.Children.Add(s);
                panel.Children.Add(new Border { Child = g, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) });
            }
            foreach (var p in StrategyEngine.TeamParams)
            {
                var g = newRow();
                g.Children.Add(Ui.T(StrategyEngine.Label(p), 13));
                var v = _measures.Team[p];
                var cell = ValueBox(p, "", v);
                Grid.SetColumn(cell, 1);
                Grid.SetColumnSpan(cell, Math.Max(1, drivers.Count));
                g.Children.Add(cell);
                var source = v.Overridden ? "modifié à la main" : "équipe · " + v.Source;
                if (p == StrategyEngine.FuelRate && drivers.Count > 0)
                {
                    var dv = drivers[0].Values;
                    double ve = dv[StrategyEngine.Energy].Value, fuel = dv[StrategyEngine.Fuel].Value;
                    if (ve > 0 && fuel > 0)
                        source = string.Format(Inv, "≈ {0:0.0} % d'énergie par seconde ({1} : {2:0.00} L par %)  ·  ", v.Value / (fuel / ve), drivers[0].Driver.Name, fuel / ve) + source;
                }
                var s = Ui.T(source, 11.5, Ui.Faint);
                Grid.SetColumn(s, drivers.Count + 1);
                g.Children.Add(s);
                panel.Children.Add(new Border { Child = g, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) });
            }
            return panel;
        }

        /// <summary>A measured value shown as text; typing replaces it, emptying the box restores the measure.</summary>
        private FrameworkElement ValueBox(string param, string driverKey, StrategyValue v)
        {
            var box = new TextBox
            {
                Text = Editable(param, v.Value), Width = 110, Height = 24, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center, Background = Ui.Frozen(Color.FromArgb(40, 0, 0, 0)),
                Foreground = v.Overridden ? Ui.Amber : (v.Measured ? Ui.Text : Ui.Muted),
                BorderBrush = v.Overridden ? Ui.Amber : Ui.Frozen(Color.FromArgb(50, 255, 255, 255)),
                ToolTip = (v.Measured ? "Mesuré : " : "Valeur par défaut : ") + StrategyEngine.Format(param, v.Value) + "\nEffacez pour revenir à la mesure.",
            };
            box.LostFocus += (s, e) =>
            {
                var text = box.Text.Trim();
                _plan.Overrides.RemoveAll(o => o.Param == param && o.DriverName == driverKey);
                if (text.Length > 0 && text != Editable(param, v.Value))
                {
                    double value;
                    if (TryParse(param, text, out value)) _plan.Overrides.Add(new StrategyOverride { Param = param, DriverName = driverKey, Value = value });
                }
                else if (text.Length > 0 && v.Overridden)
                    _plan.Overrides.Add(new StrategyOverride { Param = param, DriverName = driverKey, Value = v.Value });
                Remeasure();
            };
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
            return box;
        }

        private static string Editable(string param, double v)
        {
            if (param == StrategyEngine.Pace) return Fmt.LapSec(v);
            if (double.IsNaN(v)) return "";
            if (param == StrategyEngine.TyreLife || param == StrategyEngine.LaneLoss || param == StrategyEngine.Tyres4 || param == StrategyEngine.StopReal) return v.ToString("0", Inv);
            return v.ToString(param == StrategyEngine.Degradation ? "0.000" : "0.00", Inv);
        }

        private static bool TryParse(string param, string text, out double v)
        {
            text = text.Replace(',', '.');
            if (param == StrategyEngine.Pace && text.Contains(":"))
            {
                var p = text.Split(':');
                double m, s;
                v = 0;
                if (!double.TryParse(p[0], NumberStyles.Float, Inv, out m) || !double.TryParse(p[1], NumberStyles.Float, Inv, out s)) return false;
                v = m * 60 + s;
                return v > 0;
            }
            return double.TryParse(text, NumberStyles.Float, Inv, out v) && v >= 0;
        }

        // ─── Small widgets ────────────────────────────────────────────────────

        /// <summary>Race time, or the clock time when a start time is given.</summary>
        private string Clock(double raceSec)
        {
            TimeSpan start;
            if (!string.IsNullOrEmpty(_plan.StartClock) && TimeSpan.TryParse(_plan.StartClock, Inv, out start))
                return (start + TimeSpan.FromSeconds(raceSec)).ToString(@"hh\:mm");
            var t = TimeSpan.FromSeconds(Math.Max(0, raceSec));
            return string.Format("{0}h{1:00}", (int)t.TotalHours, t.Minutes);
        }

        private static string Duration(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Abs(sec));
            return t.TotalMinutes >= 1 ? string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds) : string.Format("{0} s", t.Seconds);
        }

        private static UIElement Title(string title, string sub)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            p.Children.Add(Ui.T(title, 14.5, Ui.Text, FontWeights.SemiBold));
            if (!string.IsNullOrEmpty(sub)) p.Children.Add(Ui.T("   " + sub, 12, Ui.Muted));
            return p;
        }

        private static UIElement Warning(string text)
        {
            return new TextBlock { Text = "⚠ " + text, Foreground = Ui.Amber, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        }

        private static Border Card(UIElement child) { return Ui.Card(child, new Thickness(14, 12, 14, 12)); }

        private static UIElement Kpi(string label, string value)
        {
            var p = new StackPanel();
            p.Children.Add(Ui.T(label, 11, Ui.Muted));
            p.Children.Add(Ui.T(value, 20, Ui.Text, FontWeights.SemiBold));
            var c = Ui.Card(p); c.Margin = new Thickness(0, 0, 8, 0);
            return c;
        }

        private static UIElement Field(string label, FrameworkElement input)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 18, 6) };
            p.Children.Add(Ui.T(label, 11.5, Ui.Muted));
            input.Margin = new Thickness(0, 3, 0, 0);
            p.Children.Add(input);
            return p;
        }

        private TextBox Box(string text, Func<string, bool> apply, string tooltip, double width)
        {
            var b = new TextBox { Text = text, Height = 26, Width = width, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = tooltip };
            b.LostFocus += (s, e) =>
            {
                if (apply(b.Text)) { b.BorderBrush = Ui.Frozen(Color.FromArgb(50, 255, 255, 255)); Dispatcher.BeginInvoke(new Action(Rebuild)); }
                else b.BorderBrush = Ui.Red;
            };
            b.KeyDown += (s, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
            return b;
        }
    }
}
