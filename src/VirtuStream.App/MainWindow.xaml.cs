using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;

namespace VirtuStream
{
    public partial class MainWindow : Window
    {
        sealed class Tab
        {
            public Border Header; public TextBlock Title; public Image Icon; public TextBlock Glyph; public WebView2 View;
        }
        sealed class Fav { public string Title, Url; }

        // Palettes : sombre (par défaut, façon Firefox) et clair. L'accent lavande n'est utilisé que pour le focus et les états actifs.
        static readonly Dictionary<string, string> Dark = new Dictionary<string, string> {
            {"AppBg","#1C1B22"},{"ToolbarBg","#2B2A33"},{"TabActiveBg","#42414D"},{"TabHoverBg","#35343D"},{"OmniboxBg","#1C1B22"},
            {"PanelBg","#23222B"},{"Fg","#FBFBFE"},{"FgMuted","#BFBFC9"},{"Accent","#A99CFF"},{"AccentSoft","#3A3568"},{"Hover","#3E3D48"},{"Line","#3A3944"} };
        static readonly Dictionary<string, string> Light = new Dictionary<string, string> {
            {"AppBg","#E9E9EE"},{"ToolbarBg","#F9F9FB"},{"TabActiveBg","#FFFFFF"},{"TabHoverBg","#DCDCE3"},{"OmniboxBg","#EDEDF2"},
            {"PanelBg","#F4F4F8"},{"Fg","#15141A"},{"FgMuted","#5B5B66"},{"Accent","#6E5BE0"},{"AccentSoft","#E6E2FF"},{"Hover","#DCDCE3"},{"Line","#CFCFD8"} };

        readonly List<Tab> tabs = new List<Tab>(); Tab active;
        CoreWebView2Environment env; readonly MediaEngine engine = new MediaEngine();
        readonly string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtuStream");
        List<Fav> favs = new List<Fav>(); List<Fav> history = new List<Fav>();
        bool dark = true, level1, seeking; volatile bool previewBusy;
        PerformanceCounter cpu; readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        string ThemePath => Path.Combine(data, "theme.txt");

        public MainWindow()
        {
            InitializeComponent();
            WindowChrome.SetWindowChrome(this, new WindowChrome {
                CaptionHeight = 40, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            foreach (UIElement el in new UIElement[] { BtnSidebar, TabArea, Caption }) WindowChrome.SetIsHitTestVisibleInChrome(el, true);
            StateChanged += (s, e) => { Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0); BtnMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; };
            SizeChanged += (s, e) => RelayoutTabs();

            Directory.CreateDirectory(data);
            try { dark = File.ReadAllText(ThemePath).Trim() != "light"; } catch { }
            SetTheme(dark);
            favs = Load("favorites.json"); history = Load("history.json"); RebuildBookmarks();
            try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpu.NextValue(); } catch { }

            engine.Error += m => Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(this, m, "VirtuStream", MessageBoxButton.OK, MessageBoxImage.Warning)));
            engine.Ended += () => Dispatcher.BeginInvoke(new Action(() => BtnPlay.Content = "\uE768"));
            engine.Preview += OnPreview;
            timer.Tick += (s, e) => Tick(); timer.Start();

            Loaded += async (s, e) => { try { if (!await InitEnv()) return; await NewTab(null); } catch (Exception ex) { App.Report(ex); } };
            Closing += (s, e) => { Save("history.json", history.Skip(Math.Max(0, history.Count - 500)).ToList()); engine.Dispose(); };
            PreviewKeyDown += (s, e) => {
                if (Keyboard.Modifiers == ModifierKeys.Control) {
                    if (e.Key == Key.T) { _ = NewTab(null); e.Handled = true; }
                    else if (e.Key == Key.W && active != null) { CloseTab(active); e.Handled = true; }
                    else if (e.Key == Key.L) { Omnibox.Focus(); Omnibox.SelectAll(); e.Handled = true; }
                    else if (e.Key == Key.R) { active?.View.Reload(); e.Handled = true; } }
            };
        }

        // ---------- Fenêtre ----------
        void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object s, RoutedEventArgs e) => Close();
        void SidebarToggle_Click(object s, RoutedEventArgs e) => Sidebar.Visibility = Sidebar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        // ---------- Persistance ----------
        List<Fav> Load(string n) { try { return JsonConvert.DeserializeObject<List<Fav>>(File.ReadAllText(Path.Combine(data, n))) ?? new List<Fav>(); } catch { return new List<Fav>(); } }
        void Save(string n, List<Fav> l) { try { File.WriteAllText(Path.Combine(data, n), JsonConvert.SerializeObject(l)); } catch { } }

        // ---------- Thème ----------
        void SetTheme(bool d)
        {
            dark = d; var r = Application.Current.Resources;
            foreach (var kv in (d ? Dark : Light)) r[kv.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
            foreach (var t in tabs) { var c = t.View.CoreWebView2; if (c != null) c.Profile.PreferredColorScheme = d ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light; }
            if (active != null) Select(active);
            try { File.WriteAllText(ThemePath, d ? "dark" : "light"); } catch { }
        }

        // ---------- Navigateur ----------
        async Task<bool> InitEnv()
        {   // Le composant Microsoft WebView2 doit être présent : message clair s'il manque (cause fréquente de fermeture immédiate).
            try { var v = CoreWebView2Environment.GetAvailableBrowserVersionString(); if (string.IsNullOrEmpty(v)) throw new InvalidOperationException(); }
            catch (Exception)
            {
                var r = MessageBox.Show(this, "Le composant Microsoft WebView2 est requis et n'est pas installé.\n\nOuvrir la page de téléchargement ?", "VirtuStream", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes) { try { Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703"); } catch { } }
                return false;
            }
            env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "profile"),
                new CoreWebView2EnvironmentOptions("--renderer-process-limit=3 --disable-background-networking"));
            return true;
        }

        static bool IsInternal(string s) => string.IsNullOrEmpty(s) || s.StartsWith("about:") || s.StartsWith("data:");

        async Task NewTab(string url)
        {
            if (env == null) return;
            var wv = new WebView2 { Visibility = Visibility.Collapsed }; ViewHost.Children.Add(wv);
            await wv.EnsureCoreWebView2Async(env);
            var c = wv.CoreWebView2; c.Settings.IsWebMessageEnabled = false; c.Settings.AreHostObjectsAllowed = false; // aucun canal privilégié page -> application
            c.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;

            var t = new Tab { View = wv };
            t.Icon = new Image { Width = 16, Height = 16 };
            t.Glyph = new TextBlock { Text = "\uE774", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            t.Glyph.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            var iconHost = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) }; iconHost.Children.Add(t.Glyph); iconHost.Children.Add(t.Icon);
            t.Title = new TextBlock { Text = "Nouvel onglet", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var close = new Button { Content = "\uE711", Style = (Style)FindResource("IconBtn"), Width = 22, Height = 22, FontSize = 9 };
            close.Click += (s, e) => CloseTab(t);
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(t.Title, 1); Grid.SetColumn(close, 2); g.Children.Add(iconHost); g.Children.Add(t.Title); g.Children.Add(close);
            t.Header = new Border { Child = g, Height = 32, Width = 200, Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                                    Padding = new Thickness(10, 0, 4, 0), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            t.Header.MouseLeftButtonDown += (s, e) => Select(t);
            t.Header.MouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(t); };
            t.Header.MouseEnter += (s, e) => { if (t != active) t.Header.SetResourceReference(Border.BackgroundProperty, "TabHoverBg"); };
            t.Header.MouseLeave += (s, e) => { if (t != active) t.Header.Background = Brushes.Transparent; };
            TabStrip.Children.Add(t.Header); tabs.Add(t); RelayoutTabs();

            c.DocumentTitleChanged += (s, e) => t.Title.Text = string.IsNullOrEmpty(c.DocumentTitle) ? "Nouvel onglet" : c.DocumentTitle;
            c.SourceChanged += (s, e) => { if (t == active) { Omnibox.Text = IsInternal(c.Source) ? "" : c.Source; UpdateStar(); } };
            c.HistoryChanged += (s, e) => { if (t == active) UpdateNav(); };
            c.NavigationCompleted += (s, e) => { if (e.IsSuccess && !IsInternal(c.Source)) history.Add(new Fav { Title = c.DocumentTitle, Url = c.Source }); };
            c.NewWindowRequested += (s, e) => { e.Handled = true; _ = NewTab(e.Uri); };
            c.PermissionRequested += (s, e) => OnPermission(e);
            c.FaviconChanged += async (s, e) => {
                try {
                    using (var st = await c.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png)) {
                        if (st == null) { t.Icon.Source = null; t.Glyph.Visibility = Visibility.Visible; return; }
                        var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = st; bi.EndInit(); bi.Freeze();
                        t.Icon.Source = bi; t.Glyph.Visibility = Visibility.Collapsed; }
                } catch { } };
            c.ContainsFullScreenElementChanged += (s, e) => {
                bool fs = c.ContainsFullScreenElement; WindowState = fs ? WindowState.Maximized : WindowState.Normal; };
            await InstallLevel1(c);
            Select(t);
            if (url != null) c.Navigate(url); else { c.NavigateToString(NewTabHtml()); Omnibox.Focus(); }
        }

        void RelayoutTabs()
        {
            if (tabs.Count == 0) return;
            double avail = ActualWidth - 46 - 46 * 3 - 80; double w = Math.Max(90, Math.Min(220, avail / tabs.Count));
            foreach (var t in tabs) t.Header.Width = w;
        }

        void OnPermission(CoreWebView2PermissionRequestedEventArgs e)
        {
            var kind = e.PermissionKind;
            string name = kind == CoreWebView2PermissionKind.Camera ? "la caméra" : kind == CoreWebView2PermissionKind.Microphone ? "le microphone" :
                          kind == CoreWebView2PermissionKind.Geolocation ? "votre position" : kind == CoreWebView2PermissionKind.Notifications ? "les notifications" : null;
            if (name == null) { e.State = CoreWebView2PermissionState.Default; return; }
            var d = e.GetDeferral();
            Dispatcher.BeginInvoke(new Action(() => {
                string host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u.Host : e.Uri;
                var r = MessageBox.Show(this, host + " souhaite utiliser " + name + ". Autoriser ?", "Autorisation", MessageBoxButton.YesNo, MessageBoxImage.Question);
                e.State = r == MessageBoxResult.Yes ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny; d.Complete();
            }));
        }

        void Select(Tab t)
        {
            active = t;
            foreach (var x in tabs)
            {
                x.View.Visibility = x == t ? Visibility.Visible : Visibility.Collapsed;
                if (x == t) { x.Header.SetResourceReference(Border.BackgroundProperty, "TabActiveBg"); x.Header.SetResourceReference(Border.BorderBrushProperty, "Accent"); }
                else { x.Header.Background = Brushes.Transparent; x.Header.BorderBrush = Brushes.Transparent; }
                x.Title.SetResourceReference(TextBlock.ForegroundProperty, x == t ? "Fg" : "FgMuted");
            }
            var src = t.View.CoreWebView2?.Source; Omnibox.Text = IsInternal(src) ? "" : src; UpdateNav(); UpdateStar();
        }

        void CloseTab(Tab t)
        {
            int i = tabs.IndexOf(t); tabs.Remove(t); TabStrip.Children.Remove(t.Header); ViewHost.Children.Remove(t.View); t.View.Dispose();
            if (tabs.Count == 0) { Close(); return; }
            RelayoutTabs(); if (t == active) Select(tabs[Math.Min(i, tabs.Count - 1)]);
        }

        void UpdateNav() { var c = active?.View.CoreWebView2; BtnBack.IsEnabled = c?.CanGoBack ?? false; BtnFwd.IsEnabled = c?.CanGoForward ?? false; }
        void UpdateStar()
        {
            var u = active?.View.CoreWebView2?.Source; bool on = !IsInternal(u) && favs.Any(f => f.Url == u);
            BtnStar.Content = on ? "\uE735" : "\uE734"; BtnStar.SetResourceReference(Control.ForegroundProperty, on ? "Accent" : "FgMuted");
        }

        static string Normalize(string s)
        {
            s = s.Trim(); if (s.Length == 0) return null;
            if (Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) return s;
            if (!s.Contains(" ") && s.Contains(".")) return "https://" + s;
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(s);
        }
        void Omnibox_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { var u = Normalize(Omnibox.Text); if (u != null) active?.View.CoreWebView2.Navigate(u); active?.View.Focus(); } }
        void Omnibox_GotFocus(object s, KeyboardFocusChangedEventArgs e) => Omnibox.SelectAll();
        void NewTab_Click(object s, RoutedEventArgs e) => _ = NewTab(null);
        void Back_Click(object s, RoutedEventArgs e) { if (active?.View.CanGoBack == true) active.View.GoBack(); }
        void Fwd_Click(object s, RoutedEventArgs e) { if (active?.View.CanGoForward == true) active.View.GoForward(); }
        void Reload_Click(object s, RoutedEventArgs e) => active?.View.Reload();

        void Bookmark_Click(object s, RoutedEventArgs e)
        {
            var c = active?.View.CoreWebView2; if (c == null || IsInternal(c.Source)) return;
            var ex = favs.FirstOrDefault(f => f.Url == c.Source);
            if (ex != null) favs.Remove(ex); else favs.Add(new Fav { Title = c.DocumentTitle, Url = c.Source });
            Save("favorites.json", favs); RebuildBookmarks(); UpdateStar();
        }

        static string Short(string title, string url) { var s = string.IsNullOrWhiteSpace(title) ? url : title; return s.Length > 40 ? s.Substring(0, 39) + "…" : s; }

        void RebuildBookmarks()
        {
            BookmarkItems.Children.Clear();
            foreach (var f in favs.Take(10))
            {
                var u = f.Url; var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = "\uE774", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = Short(f.Title, f.Url), MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var b = new Button { Style = (Style)FindResource("MenuItemBtn"), Padding = new Thickness(8, 3, 8, 3), Content = sp };
                b.Click += (s, e) => active?.View.CoreWebView2.Navigate(u);
                BookmarkItems.Children.Add(b);
            }
            BookmarksBar.Visibility = favs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- Menus ----------
        void ShowMenu(FrameworkElement anchor, string kind, PlacementMode mode)
        {
            MenuItems.Children.Clear();
            void Header(string text) { var tb = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 11.5, Margin = new Thickness(12, 8, 12, 4) }; tb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted"); MenuItems.Children.Add(tb); }
            void Item(string text, Action act) { var b = new Button { Style = (Style)FindResource("MenuItemBtn"), Content = text }; b.Click += (s, e) => { MenuPopup.IsOpen = false; act(); }; MenuItems.Children.Add(b); }
            void Sep() { var r = new Border { Height = 1, Margin = new Thickness(6, 4, 6, 4) }; r.SetResourceReference(Border.BackgroundProperty, "Line"); MenuItems.Children.Add(r); }
            void Favs() { Header("Favoris"); if (favs.Count == 0) Item("(aucun favori)", () => { }); foreach (var f in favs.Take(15)) { var u = f.Url; Item(Short(f.Title, f.Url), () => active?.View.CoreWebView2.Navigate(u)); } }
            void Hist() { Header("Historique récent"); if (history.Count == 0) Item("(vide)", () => { }); foreach (var f in Enumerable.Reverse(history).Take(15)) { var u = f.Url; Item(Short(f.Title, f.Url), () => active?.View.CoreWebView2.Navigate(u)); } }
            void Common()
            {
                Item("Téléchargements", () => active?.View.CoreWebView2?.OpenDefaultDownloadDialog());
                Item(dark ? "Passer au thème clair" : "Passer au thème sombre", () => SetTheme(!dark));
                Item("Lecteur et caméra virtuelle", () => ToggleMedia_Click(null, null));
            }
            if (kind == "main") { Item("Nouvel onglet", () => _ = NewTab(null)); Common(); Sep(); Favs(); Sep(); Hist(); }
            else if (kind == "fav") Favs();
            else if (kind == "hist") Hist();
            else Common();
            MenuPopup.PlacementTarget = anchor; MenuPopup.Placement = mode; MenuPopup.IsOpen = true;
        }
        void Menu_Click(object s, RoutedEventArgs e) => ShowMenu((FrameworkElement)s, "main", PlacementMode.Bottom);
        void OtherBookmarks_Click(object s, RoutedEventArgs e) => ShowMenu((FrameworkElement)s, "fav", PlacementMode.Bottom);
        void SideStar_Click(object s, RoutedEventArgs e) => ShowMenu((FrameworkElement)s, "fav", PlacementMode.Right);
        void SideHistory_Click(object s, RoutedEventArgs e) => ShowMenu((FrameworkElement)s, "hist", PlacementMode.Right);
        void SideSettings_Click(object s, RoutedEventArgs e) => ShowMenu((FrameworkElement)s, "settings", PlacementMode.Right);

        // ---------- Page d'accueil ----------
        string NewTabHtml()
        {
            var p = dark ? Dark : Light;
            var list = favs.Take(8).Select(f => new { Title = f.Title, Url = f.Url }).ToList();
            if (list.Count == 0) list = new[] {
                new { Title = "Google", Url = "https://www.google.com" }, new { Title = "YouTube", Url = "https://www.youtube.com" },
                new { Title = "Google Meet", Url = "https://meet.google.com" }, new { Title = "WhatsApp Web", Url = "https://web.whatsapp.com" },
                new { Title = "Messenger", Url = "https://www.messenger.com" }, new { Title = "Zoom", Url = "https://app.zoom.us" } }.ToList();
            var tiles = new StringBuilder();
            foreach (var f in list)
            {
                string host = Uri.TryCreate(f.Url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "?";
                string label = string.IsNullOrWhiteSpace(f.Title) ? host : f.Title; if (label.Length > 16) label = label.Substring(0, 15) + "…";
                tiles.Append("<a href='" + WebUtility.HtmlEncode(f.Url) + "'><div class='ic'>" + WebUtility.HtmlEncode(host.Substring(0, 1).ToUpperInvariant()) + "</div>" + WebUtility.HtmlEncode(label) + "</a>");
            }
            string html = @"<!doctype html><html><head><meta charset='utf-8'><title>Nouvel onglet</title><style>
html,body{margin:0;height:100%;background:%BG%;color:%FG%;font-family:'Segoe UI',sans-serif}
.wrap{display:flex;flex-direction:column;align-items:center;padding-top:14vh}
h1{font-weight:600;font-size:34px;margin:0 0 28px}
h1 span{color:%AC%}
form{width:min(640px,86vw)}
input{width:100%;box-sizing:border-box;height:52px;border-radius:26px;border:2px solid %LN%;background:%CARD%;color:%FG%;font-size:16px;padding:0 24px;outline:none}
input:focus{border-color:%AC%}
.tiles{display:grid;grid-template-columns:repeat(4,92px);gap:22px;margin-top:48px}
@media(min-width:900px){.tiles{grid-template-columns:repeat(8,92px)}}
a{color:%FG%;text-decoration:none;text-align:center;font-size:12px}
.ic{width:72px;height:72px;margin:0 auto 8px;border-radius:16px;background:%CARD%;display:flex;align-items:center;justify-content:center;font-size:26px;font-weight:600;color:%AC%}
a:hover .ic{background:%LN%}
</style></head><body><div class='wrap'><h1>Virtu<span>Stream</span></h1>
<form action='https://www.google.com/search' method='get'><input name='q' placeholder='Rechercher avec Google ou saisir une adresse'></form>
<div class='tiles'>%TILES%</div></div></body></html>";
            return html.Replace("%BG%", p["AppBg"]).Replace("%FG%", p["Fg"]).Replace("%AC%", p["Accent"]).Replace("%LN%", p["Line"]).Replace("%CARD%", p["ToolbarBg"]).Replace("%TILES%", tiles.ToString());
        }

        // ---------- Niveau 1 : caméra interne au navigateur ----------
        string l1Script;
        async Task InstallLevel1(CoreWebView2 c)
        {
            try { l1Script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "virtualcam.js")); } catch { return; }
            await c.AddScriptToExecuteOnDocumentCreatedAsync(l1Script);
            await c.AddScriptToExecuteOnDocumentCreatedAsync(CfgScript());
            ApplyMapping(c);
        }
        string CfgScript() => level1 && engine.File != null
            ? "window.__VS_CFG={enabled:true,audio:true,fps:" + engine.Fps + ",url:'https://vs.invalid/" + Uri.EscapeDataString(Path.GetFileName(engine.File)) + "'};"
            : "window.__VS_CFG={enabled:false};";
        void ApplyMapping(CoreWebView2 c)
        { if (engine.File != null) c.SetVirtualHostNameToFolderMapping("vs.invalid", Path.GetDirectoryName(engine.File), CoreWebView2HostResourceAccessKind.Allow); }
        void L1_Click(object s, RoutedEventArgs e)
        {
            level1 = ChkL1.IsChecked == true;
            foreach (var t in tabs) { ApplyMapping(t.View.CoreWebView2); _ = t.View.CoreWebView2.ExecuteScriptAsync(CfgScript()); }
        }

        // ---------- Panneau multimédia ----------
        void ToggleMedia_Click(object s, RoutedEventArgs e) => MediaPanel.Visibility = MediaPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        void OpenFile_Click(object s, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Vidéos|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.m4v" };
            if (d.ShowDialog(this) != true) return;
            if (engine.Load(d.FileName))
            {   // changement à chaud : la caméra reste active, la dernière image est répétée
                FileLabel.Text = Path.GetFileName(d.FileName); engine.Loop = ChkLoop.IsChecked == true;
                foreach (var t in tabs) { ApplyMapping(t.View.CoreWebView2); _ = t.View.CoreWebView2.ExecuteScriptAsync(CfgScript()); }
                engine.Play(); BtnPlay.Content = "\uE769";
            }
        }
        void Play_Click(object s, RoutedEventArgs e)
        { engine.Loop = ChkLoop.IsChecked == true; if (engine.Playing) { engine.Pause(); BtnPlay.Content = "\uE768"; } else { engine.Play(); BtnPlay.Content = "\uE769"; } }
        void Stop_Click(object s, RoutedEventArgs e) { engine.Stop(); BtnPlay.Content = "\uE768"; }
        void Seek_Pressed(object s, MouseButtonEventArgs e) => seeking = true;
        void Seek_Released(object s, MouseButtonEventArgs e) { seeking = false; if (engine.Duration > 0) engine.Seek(Seek.Value * engine.Duration); }

        void Cam_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (ChkCam.IsChecked == true)
                {
                    if (!SoftCam.IsAvailable()) throw new InvalidOperationException("Composant caméra virtuelle non installé ou non enregistré. Relancez l'installateur (droits administrateur requis).");
                    engine.EnableCamera();
                }
                else engine.DisableCamera();
            }
            catch (Exception ex) { ChkCam.IsChecked = false; MessageBox.Show(this, ex.Message, "Caméra virtuelle", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        void Mic_Click(object s, RoutedEventArgs e)
        {
            try { if (ChkMic.IsChecked == true) { engine.EnableMic(); engine.SetMicVolume((float)Vol.Value); if (engine.Playing) engine.Seek(engine.Position); } else engine.DisableMic(); }
            catch (Exception ex) { ChkMic.IsChecked = false; MessageBox.Show(this, ex.Message, "Microphone virtuel", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e) => engine.SetMicVolume((float)e.NewValue);
        void Settings_Changed(object s, RoutedEventArgs e)
        {
            if (!IsLoaded || Res720 == null || Fps24 == null) return;
            bool hd = Res720.IsChecked == true;
            engine.Reconfigure(hd ? 1280 : 1920, hd ? 720 : 1080, Fps24.IsChecked == true ? 24 : 30, ChkHw.IsChecked == true);
        }

        void OnPreview(byte[] f, int w, int h)
        {
            if (previewBusy || MediaPanel.Visibility != Visibility.Visible) return; previewBusy = true;
            var copy = (byte[])f.Clone();
            Dispatcher.BeginInvoke(new Action(() => { try { var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, copy, w * 3); b.Freeze(); Preview.Source = b; } finally { previewBusy = false; } }), DispatcherPriority.Background);
        }

        static string T(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
        void Tick()
        {
            if (MediaPanel.Visibility != Visibility.Visible) return;
            if (engine.Duration > 0 && !seeking) Seek.Value = Math.Min(1, engine.Position / engine.Duration);
            TimeLabel.Text = T(engine.Position) + " / " + T(engine.Duration);
            Level.Value = engine.MicPeak;
            long total = Process.GetCurrentProcess().WorkingSet64;
            try { foreach (var p in Process.GetProcessesByName("ffmpeg")) total += p.WorkingSet64; foreach (var p in Process.GetProcessesByName("msedgewebview2")) total += p.WorkingSet64; } catch { }
            Stats.Text = string.Format(
                "Source        : {0}×{1}\nSortie        : {2}×{3}\nFPS demandés  : {4}\nFPS mesurés   : {5:0.0}\nImages perdues: {6}\nDécodage HW   : {7}\nCaméra        : {8}\nMicro virtuel : {9}\nCPU système   : {10}\nRAM (app+ffmpeg+webview): {11} Mo",
                engine.SrcW, engine.SrcH, engine.Width, engine.Height, engine.Fps, engine.MeasuredFps, engine.Dropped, engine.DecodeInfo,
                !engine.CameraOn ? "inactive" : (engine.CameraHasConsumer ? "active, lue par une application" : "active, aucune application connectée"),
                engine.MicOn ? "actif" : "inactif", cpu != null ? cpu.NextValue().ToString("0") + " %" : "n/d", total / 1048576);
        }
    }
}
