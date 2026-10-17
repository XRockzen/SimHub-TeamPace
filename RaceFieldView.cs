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
    /// Race analysis: every car of a category, its stops, stints and pace, what our car lost
    /// against a reference car, and what worked or not compared with the top five.
    /// </summary>
    internal class RaceFieldView : StackPanel
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Brush Blue = Ui.Frozen(Color.FromRgb(0x5A, 0xA9, 0xF0));
        private static readonly Brush UsRow = Ui.Frozen(Color.FromArgb(30, 255, 200, 87));
        private static readonly Brush RefRow = Ui.Frozen(Color.FromArgb(26, 0x5A, 0xA9, 0xF0));

        private readonly List<CarRun> _all;
        private readonly CarRun _us;
        private readonly double _t0, _t1;   // session time of the start and of the last finisher
        private string _class;
        private CarRun _ref;
        private readonly StackPanel _body = new StackPanel();
        private readonly HashSet<CarRun> _shown = new HashSet<CarRun>();   // cars drawn on the gap chart

        public RaceFieldView(RaceField field)
        {
            Children.Add(_body);

            _all = RaceFieldAnalysis.RunAll(field);
            _us = _all.FirstOrDefault(r => r.Car.IsUs);
            _t0 = _all.Where(r => r.Laps > 0).Select(r => r.Start).DefaultIfEmpty(0).Min();
            _t1 = _all.Select(r => r.End).DefaultIfEmpty(1).Max();
            _class = _us != null ? _us.Car.CarClass : _all[0].Car.CarClass;
            ResetReference();
            Build();
        }

        private List<CarRun> ClassRuns
        {
            get { return _all.Where(r => r.Car.CarClass == _class).OrderBy(r => r.ClassPosition <= 0 ? 999 : r.ClassPosition).ToList(); }
        }

        /// <summary>The class winner, or the runner-up when we won.</summary>
        private void ResetReference()
        {
            var runs = ClassRuns;
            _ref = runs.FirstOrDefault(r => r != _us);
            ShowAll();
        }

        /// <summary>Every car that ran most of the race (a car out early would crush the scale).</summary>
        private void ShowAll()
        {
            var runs = ClassRuns;
            int maxLaps = runs.Count > 0 ? runs.Max(r => r.Laps) : 0;
            _shown.Clear();
            foreach (var r in runs) if (r.Laps >= maxLaps * 0.8) _shown.Add(r);
        }

        /// <summary>The three cars ahead of us and the three behind, in the class.</summary>
        private void ShowAround()
        {
            var runs = ClassRuns;
            _shown.Clear();
            int i = runs.IndexOf(_us);
            if (i < 0) { foreach (var r in runs.Take(7)) _shown.Add(r); return; }
            for (int k = Math.Max(0, i - 3); k <= Math.Min(runs.Count - 1, i + 3); k++) _shown.Add(runs[k]);
            if (runs.Count > 0) _shown.Add(runs[0]);
        }

        /// <summary>The first five of the class (us excluded), among cars that ran most of the race.</summary>
        private List<CarRun> Top5()
        {
            var runs = ClassRuns;
            int maxLaps = runs.Count > 0 ? runs.Max(r => r.Laps) : 0;
            return runs.Where(r => r != _us && r.Laps >= maxLaps * 0.8).Take(5).ToList();
        }

        // ═══ Layout ═══════════════════════════════════════════════════════════

        private void Build()
        {
            _body.Children.Clear();
            _body.Children.Add(ClassBar());

            bool usHere = _us != null && _us.Car.CarClass == _class;
            if (usHere && _ref != null)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
                var a = Breakdown();
                var b = Findings();
                Grid.SetColumn(b, 2);
                row.Children.Add(a);
                row.Children.Add(b);
                _body.Children.Add(row);
            }
            else if (_us == null)
                _body.Children.Add(Note("Votre voiture n'est pas dans ce résultat : seul le classement est affiché."));

            var head = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            legend.Children.Add(Swatch(StopLine.SwapBrush, "arrêt avec changement de pilote"));
            legend.Children.Add(Swatch(Blue, "arrêt"));
            legend.Children.Add(Swatch(StopLine.LostBrush, "temps perdu en piste"));
            legend.Children.Add(Ui.T("   largeur = temps perdu", 12, Ui.Faint));
            DockPanel.SetDock(legend, Dock.Right);
            head.Children.Add(legend);
            head.Children.Add(Ui.T("Toutes les voitures", 15, Ui.Text, FontWeights.SemiBold));
            _body.Children.Add(head);
            _body.Children.Add(Table());

            var chartHead = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var picks = new StackPanel { Orientation = Orientation.Horizontal };
            Action<string, string, Action> pick = (text, tip, act) =>
            {
                var b = Ui.SmallButton(text, tip);
                b.Margin = new Thickness(6, 0, 0, 0);
                b.Click += (s, e) => { act(); Build(); };
                picks.Children.Add(b);
            };
            pick("Toutes", "Toutes les voitures qui ont couru l'essentiel de la course", ShowAll);
            pick("Autour de nous", "Le premier, et les trois voitures devant et derrière nous", ShowAround);
            pick("Aucune", "Seulement notre voiture", () => _shown.Clear());
            DockPanel.SetDock(picks, Dock.Right);
            chartHead.Children.Add(picks);
            chartHead.Children.Add(Ui.T("Écart au premier, tour par tour", 15, Ui.Text, FontWeights.SemiBold));
            _body.Children.Add(chartHead);
            var cars = GapCars();
            _body.Children.Add(Ui.Card(new GapChart(cars, ClassRuns.FirstOrDefault(), _us) { Height = Math.Max(280, cars.Count * 16 + 40) }, new Thickness(10)));
        }

        private UIElement ClassBar()
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            bar.Children.Add(new TextBlock { Text = "Catégorie :", FontSize = 12.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var classes = _all.GroupBy(r => r.Car.CarClass).OrderBy(g => g.Min(r => r.Car.Position <= 0 ? 999 : r.Car.Position));
            foreach (var g in classes)
            {
                string cls = g.Key;
                var badge = Ui.Badge(string.Format("{0}  ·  {1}", LmuCategoryStyle.GetShortName(cls), g.Count()), LmuCategoryStyle.GetBg(cls), 12);
                badge.Margin = new Thickness(0, 0, 6, 0);
                badge.Cursor = Cursors.Hand;
                badge.Opacity = cls == _class ? 1 : 0.35;
                badge.ToolTip = "Afficher cette catégorie";
                badge.MouseLeftButtonDown += (s, e) => { _class = cls; ResetReference(); Build(); };
                bar.Children.Add(badge);
            }
            return bar;
        }

        private static UIElement Swatch(Brush color, string text)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
            s.Children.Add(new Border { Width = 12, Height = 10, Background = color, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            s.Children.Add(Ui.T(text, 12, Ui.Muted));
            return s;
        }

        private static UIElement Note(string text)
        {
            return new TextBlock { Text = text, FontSize = 13, Foreground = Ui.Muted, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        }

        // ═══ Where the time went, against the reference ══════════════════════

        private UIElement Breakdown()
        {
            var p = new StackPanel();
            p.Children.Add(Ui.T("Où le temps est parti", 15, Ui.Text, FontWeights.SemiBold));
            var vs = Ui.T("face à " + _ref.Label + "  (P" + _ref.ClassPosition + ")", 12.5, Blue);
            vs.Margin = new Thickness(0, 2, 0, 10);
            p.Children.Add(vs);

            double pace = (_us.Pace - _ref.Pace) * _us.Laps;
            double pit = _us.PitLoss - _ref.PitLoss;
            double other = _us.OtherLoss - _ref.OtherLoss;
            var items = new[]
            {
                Tuple.Create("Rythme", pace, string.Format(Inv, "{0:+0.00;-0.00} s au tour × {1} tours", _us.Pace - _ref.Pace, _us.Laps)),
                Tuple.Create("Arrêts", pit, string.Format("{0} arrêts contre {1}", _us.Stops.Count, _ref.Stops.Count)),
                Tuple.Create("Hors arrêts", other, "sorties, contacts, trafic, tours lents"),
            };
            double max = Math.Max(30, items.Max(i => Math.Abs(i.Item2)));

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            int row = 0;
            foreach (var it in items)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Ui.T(it.Item1, 13.5);
                label.ToolTip = it.Item3;
                Grid.SetRow(label, row);
                g.Children.Add(label);

                var track = new Grid { Height = 16, Margin = new Thickness(6, 5, 6, 5), ToolTip = it.Item3 };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Abs(it.Item2) / max, GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Abs(it.Item2) / max, GridUnitType.Star) });
                track.Children.Add(new Border { Background = it.Item2 > 0 ? Ui.Red : Ui.Green, CornerRadius = new CornerRadius(3), Opacity = 0.85 });
                Grid.SetRow(track, row); Grid.SetColumn(track, 1);
                g.Children.Add(track);

                var v = Ui.T(Signed(it.Item2), 13.5, it.Item2 > 0 ? Ui.Red : Ui.Green, FontWeights.SemiBold);
                v.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(v, row); Grid.SetColumn(v, 2);
                g.Children.Add(v);

                var sub = Ui.T(it.Item3, 11.5, Ui.Faint);
                sub.Margin = new Thickness(0, -3, 0, 6);
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(sub, row + 1); Grid.SetColumn(sub, 0); Grid.SetColumnSpan(sub, 3);
                g.Children.Add(sub);
                row += 2;
            }
            p.Children.Add(g);

            double total = pace + pit + other;
            int lapsDown = _ref.Laps - _us.Laps;
            string real = lapsDown > 0 ? string.Format("à l'arrivée : {0} tour{1} de retard", lapsDown, lapsDown > 1 ? "s" : "")
                        : lapsDown < 0 ? string.Format("à l'arrivée : {0} tour{1} d'avance", -lapsDown, lapsDown < -1 ? "s" : "")
                        : string.Format(Inv, "à l'arrivée : {0:+0;-0} s", _us.End - _ref.End);
            var tot = Ui.T(string.Format(Inv, "Total {0}  ≈ {1:0.0} tours  ·  {2}", Signed(total), Math.Abs(total) / Math.Max(1, _us.Pace), real), 13, Ui.Text, FontWeights.SemiBold);
            tot.Margin = new Thickness(0, 6, 0, 0);
            tot.TextWrapping = TextWrapping.Wrap;
            p.Children.Add(tot);
            return Ui.Card(p, new Thickness(14, 12, 14, 12));
        }

        // ═══ What worked, what did not ═══════════════════════════════════════

        private UIElement Findings()
        {
            var top = Top5();
            var p = new StackPanel();
            p.Children.Add(Ui.T("Ce qui a marché, ce qui est à revoir", 15, Ui.Text, FontWeights.SemiBold));
            var sub = Ui.T(top.Count > 0 ? "comparé aux " + top.Count + " premiers de la catégorie" : "pas assez de voitures pour comparer", 12.5, Ui.Muted);
            sub.Margin = new Thickness(0, 2, 0, 8);
            p.Children.Add(sub);
            if (top.Count == 0) return Ui.Card(p, new Thickness(14, 12, 14, 12));

            var good = new List<string>();
            var bad = new List<string>();
            string names = string.Join(", ", top.Select(r => r.Label.Length > 18 ? r.Label.Substring(0, 17) + "…" : r.Label));

            // Number of stops
            int usual = top.GroupBy(r => r.Stops.Count).OrderByDescending(gr => gr.Count()).ThenBy(gr => gr.Key).First().Key;
            if (_us.Stops.Count > usual)
                bad.Add(string.Format("{0} arrêts, contre {1} pour la plupart des premiers : {2} de trop.", _us.Stops.Count, usual, _us.Stops.Count - usual));
            else
                good.Add(string.Format("{0} arrêts, comme la plupart des premiers ({1}).", _us.Stops.Count, usual));

            // Slow stops
            var topStops = top.SelectMany(r => r.Stops).Select(s => s.Loss).ToList();
            double typical = RaceFieldAnalysis.Median(topStops);
            var slow = _us.Stops.Where(s => s.Loss > typical + 30).ToList();
            foreach (var s in slow)
                bad.Add(string.Format(Inv, "Arrêt du tour {0} : {1:0} s perdues, contre {2:0} s pour un arrêt habituel des premiers (+{3:0} s){4}.",
                    s.Lap, s.Loss, typical, s.Loss - typical, s.Swap != null ? ", avec changement de pilote" : ""));
            var normal = _us.Stops.Where(s => s.Loss <= typical + 30).ToList();
            if (normal.Count > 0)
                good.Add(string.Format(Inv, "{0} arrêt{1} dans les temps : {2:0} s en moyenne, contre {3:0} s pour les premiers.",
                    normal.Count, normal.Count > 1 ? "s" : "", normal.Average(s => s.Loss), typical));

            // Stints
            double stint = _us.AverageStint, topStint = top.Average(r => r.AverageStint);
            if (_us.Stops.Count > 0 && stint < topStint - 2)
                bad.Add(string.Format(Inv, "Relais plus courts : {0:0} tours en moyenne, contre {1:0} pour les premiers.", stint, topStint));
            else if (_us.Stops.Count > 0)
                good.Add(string.Format(Inv, "Relais de {0:0} tours en moyenne, comme les premiers ({1:0}).", stint, topStint));

            // Pace
            double topPace = RaceFieldAnalysis.Median(top.Select(r => r.Pace).ToList());
            var ranked = ClassRuns.Where(r => r.Pace > 0 && r.Laps >= _us.Laps * 0.5).OrderBy(r => r.Pace).ToList();
            int rank = ranked.IndexOf(_us) + 1;
            double dp = _us.Pace - topPace;
            string rankText = rank > 0 ? string.Format(" ({0}{1} rythme de la catégorie)", rank, rank == 1 ? "er" : "e") : "";
            if (dp > 0.2)
                bad.Add(string.Format(Inv, "Rythme : {0:+0.00} s au tour par rapport aux premiers, soit ≈ {1:0} s sur la course{2}.", dp, dp * _us.Laps, rankText));
            else
                good.Add(string.Format(Inv, "Rythme au niveau des premiers ({0:+0.00;-0.00} s au tour){1}.", dp, rankText));

            // Time lost on track
            double topOther = top.Average(r => r.OtherLoss);
            if (_us.OtherLoss > topOther + 30)
            {
                var worst = _us.Incidents.OrderByDescending(s => s.Loss).Take(3).Select(s => string.Format(Inv, "tour {0} ({1:0} s)", s.Lap, s.Loss));
                bad.Add(string.Format(Inv, "Temps perdu en piste (sorties, contacts, trafic) : {0:0} s, contre {1:0} s pour les premiers. Les pires : {2}.",
                    _us.OtherLoss, topOther, string.Join(", ", worst)));
            }
            else
                good.Add(string.Format(Inv, "Peu de temps perdu en piste : {0:0} s, contre {1:0} s pour les premiers.", _us.OtherLoss, topOther));

            foreach (var t in bad) p.Children.Add(Finding(false, t));
            foreach (var t in good) p.Children.Add(Finding(true, t));
            sub.ToolTip = "Premiers : " + names;
            return Ui.Card(p, new Thickness(14, 12, 14, 12));
        }

        private static UIElement Finding(bool good, string text)
        {
            var d = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var icon = Ui.T(good ? "✓" : "✗", 14, good ? Ui.Green : Ui.Red, FontWeights.Bold);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Width = 20;
            DockPanel.SetDock(icon, Dock.Left);
            d.Children.Add(icon);
            d.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap });
            return d;
        }

        // ═══ Every car ════════════════════════════════════════════════════════

        private static readonly string[] Heads = { "", "Pos", "Équipe", "Tours", "Rythme", "Δ rythme", "Arrêts", "Aux stands", "Relais", "En piste", "Déroulé de la course" };
        private static readonly string[] HeadTips =
        {
            "Voitures tracées sur la courbe d'écarts", "Position dans la catégorie", "Équipe et numéro ; au survol : les pilotes", "Tours bouclés",
            "Tour médian de la course, arrêts compris exclus", "Écart de rythme avec notre voiture (+ = plus lent que nous)",
            "Arrêts aux stands repérés au temps perdu (les pénalités de type stop and go en font partie)",
            "Temps perdu à tous les arrêts, entrée et sortie des stands comprises", "Relais moyen, en tours",
            "Temps perdu hors arrêts : sorties, contacts, trafic, tours lents", "Chaque barre : un arrêt (largeur = temps perdu). Au survol : le détail",
        };

        private UIElement Table()
        {
            var g = new Grid();
            var widths = new[] { 30.0, 42, 230, 54, 78, 76, 56, 84, 58, 76, -1 };
            foreach (var w in widths)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w), MinWidth = w < 0 ? 200 : 0 });

            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var headBg = new Border { Background = Ui.HeaderBg, CornerRadius = new CornerRadius(5, 5, 0, 0) };
            Grid.SetColumnSpan(headBg, widths.Length);
            g.Children.Add(headBg);
            for (int c = 0; c < Heads.Length; c++)
            {
                UIElement cell;
                if (c == Heads.Length - 1) cell = new TimeAxis(_t0, _t1) { Height = 30 };
                else
                {
                    var t = Ui.T(Heads[c], 12, Ui.Muted, FontWeights.SemiBold);
                    t.Margin = new Thickness(6, 7, 6, 7);
                    if (c >= 3) t.HorizontalAlignment = HorizontalAlignment.Right;
                    cell = t;
                }
                ((FrameworkElement)cell).ToolTip = HeadTips[c];
                Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }

            int row = 1;
            foreach (var r in ClassRuns)
            {
                var run = r;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                bool us = r == _us, isRef = r == _ref;
                var bg = new Border
                {
                    Background = us ? UsRow : isRef ? RefRow : (row % 2 == 0 ? Ui.Panel : Brushes.Transparent),
                    Cursor = us ? null : Cursors.Hand,
                    ToolTip = us ? "Notre voiture" : "Clic : prendre cette voiture comme référence",
                };
                if (!us) bg.MouseLeftButtonDown += (s, e) => { _ref = run; Build(); };
                Grid.SetRow(bg, row);
                Grid.SetColumnSpan(bg, widths.Length);
                g.Children.Add(bg);

                var weight = us ? FontWeights.SemiBold : FontWeights.Normal;
                double dPace = _us != null && r.Pace > 0 ? r.Pace - _us.Pace : double.NaN;
                string drivers = string.Join(", ", r.Car.Drivers);
                var shown = new CheckBox { IsChecked = _shown.Contains(run) || us, IsEnabled = !us, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Tracer sur la courbe d'écarts" };
                shown.Checked += (s, e) => { _shown.Add(run); Build(); };
                shown.Unchecked += (s, e) => { _shown.Remove(run); Build(); };
                Grid.SetRow(shown, row);
                g.Children.Add(shown);

                var cells = new[]
                {
                    r.ClassPosition > 0 ? r.ClassPosition.ToString() : "—",
                    (us ? "★ " : isRef ? "◆ " : "") + r.Label + (string.IsNullOrEmpty(r.Car.Number) ? "" : "  #" + r.Car.Number),
                    r.Laps.ToString(),
                    r.Pace > 0 ? Fmt.LapSec(r.Pace) : "—",
                    us || double.IsNaN(dPace) ? "" : string.Format(Inv, "{0:+0.00;-0.00}", dPace),
                    r.Stops.Count.ToString(),
                    string.Format(Inv, "{0:0} s", r.PitLoss),
                    r.Stops.Count > 0 ? string.Format(Inv, "{0:0}", r.AverageStint) : "—",
                    string.Format(Inv, "{0:0} s", r.OtherLoss),
                };
                for (int c = 0; c < cells.Length; c++)
                {
                    Brush fg = Ui.Text;
                    if (c == 4 && !double.IsNaN(dPace) && !us) fg = dPace < 0 ? Ui.Red : Ui.Green;   // faster than us: red
                    var t = Ui.T(cells[c], 13, us && c == 1 ? Ui.Amber : fg, weight);
                    t.Margin = new Thickness(6, 5, 6, 5);
                    t.IsHitTestVisible = c <= 1;
                    int gc = c + 1;
                    if (c == 0 && !string.IsNullOrEmpty(r.Car.Finish) && r.Car.Finish != "Finished Normally")
                        t.ToolTip = "Non classé à l'arrivée (" + r.Car.Finish + ")";
                    if (c == 1) t.ToolTip = "Pilotes : " + drivers + (string.IsNullOrEmpty(r.Car.Vehicle) ? "" : "\n" + r.Car.Vehicle);
                    if (c >= 2) t.HorizontalAlignment = HorizontalAlignment.Right;
                    if (c == 0 && !string.IsNullOrEmpty(r.Car.Finish) && r.Car.Finish != "Finished Normally") t.Text += " *";
                    Grid.SetRow(t, row); Grid.SetColumn(t, gc);
                    g.Children.Add(t);
                }
                var line = new StopLine(run, _t0, _t1, us) { Height = 26, Margin = new Thickness(4, 0, 6, 0), ToolTip = StopsTip(run) };
                Grid.SetRow(line, row); Grid.SetColumn(line, widths.Length - 1);
                g.Children.Add(line);
                row++;
            }

            var host = new Border { Background = Ui.Panel, CornerRadius = new CornerRadius(7), Child = g, Margin = new Thickness(0, 0, 0, 16) };
            return host;
        }

        private static string StopsTip(CarRun r)
        {
            if (r.Stops.Count == 0) return "Aucun arrêt repéré";
            var lines = new List<string>();
            foreach (var s in r.Stops)
                lines.Add(string.Format(Inv, "Tour {0} : {1:0} s{2}", s.Lap, s.Loss,
                    s.Swap != null ? "  ·  " + s.Swap.From + " → " + s.Swap.To : ""));
            var stints = r.Stints;
            lines.Add("Relais : " + string.Join(" · ", stints.Select(x => x + " t")));
            return string.Join("\n", lines);
        }

        private List<CarRun> GapCars()
        {
            var list = ClassRuns.Where(r => _shown.Contains(r)).ToList();
            if (_us != null && _us.Car.CarClass == _class && !list.Contains(_us)) list.Add(_us);
            return list;
        }

        private static string Signed(double s)
        {
            var a = Math.Abs(s);
            string body = a >= 120 ? string.Format(Inv, "{0} min {1:00} s", (int)(a / 60), Math.Round(a % 60)) : string.Format(Inv, "{0:0} s", a);
            return (s > 0.5 ? "+" : s < -0.5 ? "−" : "") + body;
        }

        // ═══ Drawings ═════════════════════════════════════════════════════════

        /// <summary>Hours of race above the timeline column.</summary>
        private class TimeAxis : FrameworkElement
        {
            private readonly double _t0, _t1;
            public TimeAxis(double t0, double t1) { _t0 = t0; _t1 = t1; Margin = new Thickness(4, 0, 6, 0); }

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
                double span = Math.Max(1, _t1 - _t0);
                double step = span > 3 * 3600 ? 3600 : span > 3600 ? 1800 : 600;
                for (double t = 0; t <= span + 1; t += step)
                {
                    double x = t / span * ActualWidth;
                    string label = step >= 1800 ? (t / 3600).ToString("0.#", Inv) + " h" : (t / 60).ToString("0") + " min";
                    var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11.5, Ui.Muted, 1.0);
                    dc.DrawText(ft, new Point(Math.Min(ActualWidth - ft.Width, Math.Max(0, x - ft.Width / 2)), 8));
                }
            }
        }

        /// <summary>One car over the race: its run, its stops (width = time lost) and time lost on track.</summary>
        private class StopLine : FrameworkElement
        {
            private static readonly Pen Run = new Pen(Ui.Frozen(Color.FromArgb(70, 255, 255, 255)), 2);
            public static readonly Brush SwapBrush = Ui.Frozen(Color.FromRgb(0xFF, 0xA7, 0x26));
            public static readonly Brush LostBrush = Ui.Frozen(Color.FromArgb(200, 0xEF, 0x53, 0x50));
            private readonly CarRun _r;
            private readonly double _t0, _t1;

            public StopLine(CarRun r, double t0, double t1, bool us) { _r = r; _t0 = t0; _t1 = t1; }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight, span = Math.Max(1, _t1 - _t0);
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
                Func<double, double> X = t => (t - _t0) / span * w;
                double mid = h / 2;
                dc.DrawLine(Run, new Point(X(_r.Start), mid), new Point(X(_r.End), mid));
                foreach (var s in _r.Incidents)
                    if (s.Loss >= 8)
                        dc.DrawRectangle(LostBrush, null, new Rect(X(s.At), mid - 4, Math.Max(2, s.Loss / span * w), 8));
                foreach (var s in _r.Stops)
                    dc.DrawRoundedRectangle(s.Swap != null ? SwapBrush : Blue, null, new Rect(X(s.At), mid - 7, Math.Max(3, s.Loss / span * w), 14), 2, 2);
            }
        }

        /// <summary>Gap to the class leader at the end of each lap.</summary>
        private class GapChart : FrameworkElement
        {
            private static readonly Brush[] Colors =
            {
                Ui.Frozen(Color.FromRgb(0x9E, 0xA3, 0xAD)), Ui.Frozen(Color.FromRgb(0x81, 0xC7, 0x84)),
                Ui.Frozen(Color.FromRgb(0xBA, 0x9C, 0xF0)), Ui.Frozen(Color.FromRgb(0x4D, 0xD0, 0xE1)),
                Ui.Frozen(Color.FromRgb(0xF4, 0x8F, 0xB1)), Ui.Frozen(Color.FromRgb(0x5A, 0xA9, 0xF0)),
            };
            private readonly List<CarRun> _cars;
            private readonly CarRun _leader, _us;
            // A car brought forward: hovered on its name, or pinned by a click (kept while the chart is rebuilt)
            private static string _pinned;
            private string _hover;
            private readonly List<KeyValuePair<Rect, string>> _labelHits = new List<KeyValuePair<Rect, string>>();

            public GapChart(List<CarRun> cars, CarRun leader, CarRun us)
            {
                _cars = cars; _leader = leader; _us = us;
                ToolTip = null;
            }

            private string LabelAt(Point p)
            {
                foreach (var kv in _labelHits) if (kv.Key.Contains(p)) return kv.Value;
                return null;
            }

            protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
            {
                var hit = LabelAt(e.GetPosition(this));
                Cursor = hit != null ? Cursors.Hand : null;
                if (hit != _hover) { _hover = hit; InvalidateVisual(); }
            }

            protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
            {
                if (_hover != null) { _hover = null; InvalidateVisual(); }
            }

            protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
            {
                var hit = LabelAt(e.GetPosition(this));
                _pinned = hit == null || hit == _pinned ? null : hit;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
                if (_leader == null || _cars.Count == 0) return;
                const double left = 64, right = 190, top = 8, bottom = 24;
                double w = ActualWidth - left - right, h = ActualHeight - top - bottom;
                if (w < 50 || h < 50) return;

                int laps = _leader.LapEnds.Count;
                var series = new List<KeyValuePair<CarRun, List<Point>>>();
                double maxGap = 10;
                foreach (var c in _cars)
                {
                    var pts = new List<Point>();
                    for (int i = 0; i < Math.Min(laps, c.LapEnds.Count); i++)
                    {
                        double a = c.LapEnds[i], b = _leader.LapEnds[i];
                        if (double.IsNaN(a) || double.IsNaN(b)) continue;
                        pts.Add(new Point(i + 1, a - b));
                        maxGap = Math.Max(maxGap, a - b);
                    }
                    series.Add(new KeyValuePair<CarRun, List<Point>>(c, pts));
                }
                double step = maxGap > 1200 ? 300 : maxGap > 400 ? 120 : maxGap > 120 ? 60 : 15;
                maxGap = Math.Ceiling(maxGap / step) * step;
                Func<double, double> X = lap => left + (lap - 1) / Math.Max(1, laps - 1) * w;
                Func<double, double> Y = gap => top + Math.Max(0, gap) / maxGap * h;

                var grid = new Pen(Ui.Line, 1);
                var tf = new Typeface("Segoe UI");
                for (double g = 0; g <= maxGap + 0.1; g += step)
                {
                    dc.DrawLine(grid, new Point(left, Y(g)), new Point(left + w, Y(g)));
                    string label = g >= 120 ? string.Format(Inv, "{0} min", g / 60) : string.Format(Inv, "{0} s", g);
                    var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 11, Ui.Muted, 1.0);
                    dc.DrawText(ft, new Point(left - ft.Width - 6, Y(g) - ft.Height / 2));
                }
                int lapStep = laps > 150 ? 25 : laps > 60 ? 10 : 5;
                for (int l = lapStep; l <= laps; l += lapStep)
                {
                    var ft = new FormattedText("T" + l, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 11, Ui.Muted, 1.0);
                    dc.DrawText(ft, new Point(X(l) - ft.Width / 2, top + h + 5));
                }

                // Legend on the right, at each line's end, nudged apart. The car brought forward
                // (hovered or pinned) is drawn last and thick, the others fade.
                string focus = _hover ?? _pinned;
                if (focus != null && !series.Exists(x => x.Key.Label == focus)) focus = null;
                var labels = new List<Tuple<double, string, Brush, string>>();
                var colors = new Dictionary<CarRun, Brush>();
                int k = 0;
                foreach (var s in series) colors[s.Key] = s.Key == _us ? Ui.Amber : Colors[k++ % Colors.Length];
                var ordered = new List<KeyValuePair<CarRun, List<Point>>>(series);
                ordered.Sort((a, b) => (a.Key.Label == focus).CompareTo(b.Key.Label == focus));
                foreach (var s in ordered)
                {
                    if (s.Value.Count < 2) continue;
                    var baseColor = ((SolidColorBrush)colors[s.Key]).Color;
                    bool on = focus == null || s.Key.Label == focus;
                    // Our car stays half visible, to compare with the one brought forward
                    Brush color = on ? colors[s.Key] : Ui.Frozen(Color.FromArgb((byte)(s.Key == _us ? 150 : 55), baseColor.R, baseColor.G, baseColor.B));
                    var pen = new Pen(color, focus != null && on ? 3.2 : s.Key == _us ? 2.6 : 1.5);
                    pen.Freeze();
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(new Point(X(s.Value[0].X), Y(s.Value[0].Y)), false, false);
                        for (int i = 1; i < s.Value.Count; i++) ctx.LineTo(new Point(X(s.Value[i].X), Y(s.Value[i].Y)), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, pen, geo);
                    var last = s.Value[s.Value.Count - 1];
                    string name = s.Key.Label.Length > 22 ? s.Key.Label.Substring(0, 21) + "…" : s.Key.Label;
                    labels.Add(Tuple.Create(Y(last.Y), "P" + s.Key.ClassPosition + " " + name, on ? colors[s.Key] : Ui.Faint, s.Key.Label));
                }
                labels.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                double prev = double.MinValue;
                _labelHits.Clear();
                foreach (var l in labels)
                {
                    double y = Math.Max(l.Item1, prev + 15);
                    prev = y;
                    bool focused = l.Item4 == focus;
                    var ft = new FormattedText((focused && l.Item4 == _pinned ? "● " : "") + l.Item2, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                               new Typeface(tf.FontFamily, FontStyles.Normal, focused ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), 11.5, l.Item3, 1.0);
                    var at = new Point(left + w + 8, y - ft.Height / 2);
                    dc.DrawText(ft, at);
                    _labelHits.Add(new KeyValuePair<Rect, string>(new Rect(at.X - 2, at.Y, ft.Width + 4, ft.Height), l.Item4));
                }
            }
        }
    }
}
