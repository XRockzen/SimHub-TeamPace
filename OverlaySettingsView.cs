using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace User.TeamPace
{
    /// <summary>
    /// The "Overlay" tab: a live preview of the overlay and everything it can be set to
    /// (display, telemetry, reference, coaching, race plan), plus the wheel buttons to map.
    /// </summary>
    internal class OverlaySettingsView : UserControl
    {
        private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
        private readonly List<DriverView> _drivers;
        private readonly OverlayView _preview = new OverlayView();
        private readonly DispatcherTimer _timer;
        private readonly TextBlock _status = Ui.T("", 12.5, Ui.Muted);
        private readonly StackPanel _root = new StackPanel();
        private bool _building;

        public OverlaySettingsView(List<DriverView> drivers)
        {
            _drivers = drivers;
            Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) =>
            {
                if (!OverlayService.Live) OverlayService.DemoTick();
                _preview.InvalidateVisual();
            };
            Action status = () => Dispatcher.BeginInvoke(new Action(() => _status.Text = "Référence : " + OverlayService.Status));
            Loaded += (s, e) => { _timer.Start(); OverlayService.StatusChanged += status; status(); };
            Unloaded += (s, e) =>
            {
                _timer.Stop();
                OverlayService.StatusChanged -= status;
                if (OverlayService.EditMode) OverlayService.SetEditMode(false);
            };
            Build();
        }

        private OverlaySettings S { get { return OverlaySettings.Current; } }

        private void Changed(bool reference)
        {
            if (_building) return;
            OverlaySettings.Save();
            if (reference) OverlayService.RequestReference();
        }

        private void Build()
        {
            _building = true;
            _root.Children.Clear();

            // Preview, at its real size
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var frame = new Border { Child = _preview, Background = Ui.Frozen(Color.FromRgb(0x4A, 0x5A, 0x6C)), Padding = new Thickness(16), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(frame, Dock.Left);
            top.Children.Add(frame);
            var side = new StackPanel { Margin = new Thickness(18, 4, 0, 0) };
            var enable = Check("Afficher l'overlay en roulant", S.Enabled, v => { S.Enabled = v; Changed(false); });
            enable.FontSize = 14;
            side.Children.Add(enable);
            side.Children.Add(Note("Il apparaît dès que LMU roule (fenêtré ou plein écran fenêtré), au-dessus du jeu, sans prendre la souris."));
            var place = Ui.Button(OverlayService.EditMode ? "Terminé" : "Placer l'overlay à l'écran");
            place.Margin = new Thickness(0, 10, 0, 0);
            place.HorizontalAlignment = HorizontalAlignment.Left;
            place.Click += (s, e) =>
            {
                OverlayService.SetEditMode(!OverlayService.EditMode);
                place.Content = OverlayService.EditMode ? "Terminé" : "Placer l'overlay à l'écran";
            };
            side.Children.Add(place);
            side.Children.Add(Note("Glissez-le où vous voulez (par exemple à la place du rétroviseur), la molette change sa taille."));
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 12, 0, 0);
            side.Children.Add(_status);
            top.Children.Add(side);
            _root.Children.Add(top);

            var wrap = new WrapPanel();
            wrap.Children.Add(Section("Affichage", Display()));
            wrap.Children.Add(Section("Télémétrie", Telemetry()));
            wrap.Children.Add(Section("Référence", Reference()));
            wrap.Children.Add(Section("Coaching", Coaching()));
            wrap.Children.Add(Section("Course", Race()));
            wrap.Children.Add(Section("Boutons du volant", Buttons()));
            _root.Children.Add(wrap);
            _building = false;
        }

        // ─── Sections ─────────────────────────────────────────────────────────

        private UIElement Display()
        {
            var p = new StackPanel();
            p.Children.Add(SliderRow("Taille", 40, 300, S.Scale * 100, v => string.Format("{0:0} %", v), v => { S.Scale = v / 100; Changed(false); }));
            p.Children.Add(SliderRow("Fond", 0, 100, S.Opacity * 100, v => string.Format("{0:0} %", v), v => { S.Opacity = v / 100; Changed(false); }));
            p.Children.Add(Label("Pages (A / C pour passer de l'une à l'autre)"));
            var pages = new WrapPanel();
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                var c = Check(OverlaySettings.PageName((OverlayPage)i), S.Pages[i], v =>
                {
                    S.Pages[k] = v;
                    if (!S.Pages.Any(x => x)) S.Pages[0] = true;
                    if (!S.Pages[S.Page]) OverlayService.NextPage(1);
                    Changed(false);
                });
                c.Margin = new Thickness(0, 2, 14, 2);
                pages.Children.Add(c);
            }
            p.Children.Add(pages);
            p.Children.Add(Label("Page affichée"));
            var cb = new ComboBox { Height = 26, FontSize = 12.5, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            for (int i = 0; i < 4; i++) cb.Items.Add(OverlaySettings.PageName((OverlayPage)i));
            cb.SelectedIndex = S.Page;
            cb.SelectionChanged += (s, e) => { if (_building || cb.SelectedIndex < 0) return; S.Page = cb.SelectedIndex; S.Pages[S.Page] = true; Changed(false); };
            p.Children.Add(cb);
            return p;
        }

        private UIElement Telemetry()
        {
            var p = new StackPanel();
            p.Children.Add(SliderRow("Durée affichée", 3, 20, S.WindowSec, v => string.Format("{0:0} s", v), v => { S.WindowSec = Math.Round(v); Changed(false); }));
            p.Children.Add(Note("Plus la durée est courte, plus les courbes défilent vite."));
            p.Children.Add(Check("Fantôme de la référence (bouton B)", S.Ghost, v => { S.Ghost = v; Changed(false); }));
            return p;
        }

        private UIElement Reference()
        {
            var p = new StackPanel();
            p.Children.Add(Label("Tour de référence"));
            var cb = new ComboBox { Height = 26, FontSize = 12.5, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            var options = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("ideal|", "Idéal de l'équipe (meilleur passage de chaque virage)"),
                new KeyValuePair<string, string>("mine|", "Mon meilleur tour"),
                new KeyValuePair<string, string>("team|", "Meilleur tour de l'équipe"),
            };
            foreach (var td in LmuTeamSync.GetDrivers().Skip(1))
                options.Add(new KeyValuePair<string, string>("driver|" + td.Name, "Meilleur tour de " + td.Name));
            string current = S.Reference + "|" + (S.Reference == "driver" ? S.ReferenceDriver : "");
            foreach (var o in options)
            {
                var item = new ComboBoxItem { Content = o.Value, Tag = o.Key };
                cb.Items.Add(item);
                if (o.Key == current) cb.SelectedItem = item;
            }
            if (cb.SelectedItem == null) cb.SelectedIndex = 0;
            cb.SelectionChanged += (s, e) =>
            {
                if (_building) return;
                var parts = ((string)((ComboBoxItem)cb.SelectedItem).Tag).Split('|');
                S.Reference = parts[0]; S.ReferenceDriver = parts[1].Length > 0 ? parts[1] : null;
                Changed(true);
            };
            p.Children.Add(cb);

            p.Children.Add(Label("Circuit"));
            var circuits = new ComboBox { Height = 26, FontSize = 12.5, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 400 };
            circuits.Items.Add(new ComboBoxItem { Content = "Automatique : le circuit où vous roulez", Tag = null });
            var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _drivers)
                foreach (var s in d.Sessions) seen.Add(s.TrackDisplayName + "|" + LmuCategoryStyle.GetShortName(s.CarClass));
            foreach (var key in seen)
            {
                var parts = key.Split('|');
                var item = new ComboBoxItem { Content = parts[0] + "  ·  " + parts[1], Tag = key };
                circuits.Items.Add(item);
                if (S.Circuit == parts[0] && S.ClassLabel == parts[1]) circuits.SelectedItem = item;
            }
            if (circuits.SelectedItem == null) circuits.SelectedIndex = 0;
            circuits.SelectionChanged += (s, e) =>
            {
                if (_building) return;
                var tag = (string)((ComboBoxItem)circuits.SelectedItem).Tag;
                if (tag == null) { S.Circuit = null; S.ClassLabel = null; }
                else { var parts = tag.Split('|'); S.Circuit = parts[0]; S.ClassLabel = parts[1]; }
                Changed(true);
            };
            p.Children.Add(circuits);
            p.Children.Add(Note("Choisir un circuit permet de préparer la référence avant de rouler."));
            var reload = Ui.SmallButton("Recharger la référence");
            reload.HorizontalAlignment = HorizontalAlignment.Left;
            reload.Margin = new Thickness(0, 8, 0, 0);
            reload.Click += (s, e) => OverlayService.RequestReference();
            p.Children.Add(reload);
            return p;
        }

        private UIElement Coaching()
        {
            var p = new StackPanel();
            var voice = new StackPanel { Orientation = Orientation.Horizontal };
            voice.Children.Add(Check("Annonces vocales (bouton B)", S.Voice, v => { S.Voice = v; Changed(false); }));
            var test = Ui.SmallButton("Tester");
            test.Margin = new Thickness(10, 0, 0, 0);
            test.Click += (s, e) => OverlayService.Say("Virage 5 : freine 15 mètres plus tard");
            voice.Children.Add(test);
            p.Children.Add(voice);
            p.Children.Add(Note("La consigne est lue dans la ligne droite d'avant, seulement sur la page Coaching."));
            p.Children.Add(SliderRow("Virages travaillés", 1, 3, S.CoachCorners, v => string.Format("{0:0}", v), v => { S.CoachCorners = (int)Math.Round(v); Changed(true); }));
            p.Children.Add(SliderRow("Consigne affichée", 150, 600, S.AnnounceMeters, v => string.Format("{0:0} m avant", v), v => { S.AnnounceMeters = Math.Round(v / 10) * 10; Changed(false); }));
            p.Children.Add(Note("Les virages viennent du Plan de travail : les plus coûteux face à la référence."));
            return p;
        }

        private UIElement Race()
        {
            var p = new StackPanel();
            p.Children.Add(Label("Plan de stratégie suivi"));
            var cb = new ComboBox { Height = 26, FontSize = 12.5, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            cb.Items.Add(new ComboBoxItem { Content = "Automatique : le dernier plan du circuit", Tag = null });
            foreach (var plan in StrategyEngine.SavedPlans())
            {
                var item = new ComboBoxItem { Content = plan.Id + "  ·  " + plan.Circuit, Tag = plan.Id };
                cb.Items.Add(item);
                if (plan.Id == S.StrategyPlan) cb.SelectedItem = item;
            }
            if (cb.SelectedItem == null) cb.SelectedIndex = 0;
            cb.SelectionChanged += (s, e) => { if (_building) return; S.StrategyPlan = (string)((ComboBoxItem)cb.SelectedItem).Tag; Changed(true); };
            p.Children.Add(cb);
            p.Children.Add(Note("La page Course suit ce plan : tours avant l'arrêt, marge d'essence, écart au plan, pilote et pneus de l'arrêt."));
            return p;
        }

        /// <summary>SimHub's own "click to configure" control, one per wheel button, as everywhere in SimHub.</summary>
        private UIElement Buttons()
        {
            var p = new StackPanel();
            string prefix = typeof(TeamPacePlugin).Name + ".";   // SimHub names a plugin's actions after its class
            foreach (var row in new[]
            {
                new[] { "A", "Overlay.PageSuivante", "Page suivante" },
                new[] { "B", "Overlay.Action", "Action de la page" },
                new[] { "C", "Overlay.PagePrecedente", "Page précédente" },
                new[] { "D", "Overlay.Afficher", "Masquer / afficher" },
            })
            {
                var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0), MinHeight = 30 };
                var key = Ui.T(row[0], 13, Ui.Amber, FontWeights.Bold);
                key.Width = 20;
                DockPanel.SetDock(key, Dock.Left);
                line.Children.Add(key);
                FrameworkElement editor = null;
                try
                {
                    editor = new SimHub.Plugins.UI.ControlsEditor { ActionName = prefix + row[1], FriendlyName = row[2] };
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn("[TeamPace] Controls editor: " + ex.Message); }
                if (editor == null)
                {
                    var txt = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text };
                    txt.Text = row[2] + " : action « " + prefix + row[1] + " » dans Contrôles et événements";
                    editor = txt;
                }
                line.Children.Add(editor);
                p.Children.Add(line);
            }
            p.Children.Add(Note("Cliquez puis appuyez sur le bouton du volant à associer, comme partout dans SimHub. B dépend de la page : fantôme, voix, arrêt suivant, référence."));
            return p;
        }

        // ─── Widgets ──────────────────────────────────────────────────────────

        private static UIElement Section(string title, UIElement body)
        {
            var p = new StackPanel();
            p.Children.Add(Ui.T(title, 14.5, Ui.Text, FontWeights.SemiBold));
            var inner = body as FrameworkElement;
            if (inner != null) inner.Margin = new Thickness(0, 6, 0, 0);
            p.Children.Add(body);
            var c = Ui.Card(p, new Thickness(14, 12, 14, 12));
            c.Width = 380;
            c.Margin = new Thickness(0, 0, 12, 12);
            c.VerticalAlignment = VerticalAlignment.Top;
            return c;
        }

        private static TextBlock Label(string text)
        {
            var t = Ui.T(text, 12, Ui.Muted);
            t.Margin = new Thickness(0, 8, 0, 3);
            return t;
        }

        private static TextBlock Note(string text)
        {
            return new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        }

        private CheckBox Check(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = value, Foreground = Ui.Text, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            c.Checked += (s, e) => set(true);
            c.Unchecked += (s, e) => set(false);
            return c;
        }

        private UIElement SliderRow(string label, double min, double max, double value, Func<double, string> format, Action<double> set)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var l = Ui.T(label, 12.5, Ui.Muted);
            l.Width = 120;
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            var v = Ui.T(format(value), 12.5, Ui.Text);
            v.Width = 70;
            v.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (s, e) => { v.Text = format(slider.Value); };
            // Saved when the slider is released (or a key moves it), not at every pixel
            slider.PreviewMouseUp += (s, e) => set(slider.Value);
            slider.KeyUp += (s, e) => set(slider.Value);
            row.Children.Add(slider);
            return row;
        }
    }
}
