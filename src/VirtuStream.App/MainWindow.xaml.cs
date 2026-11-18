using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;

namespace VirtuStream
{
    public partial class MainWindow : Window
    {
        sealed class Tab { public Border Header; public TextBlock Title; public WebView2 View; }
        sealed class Fav { public string Title, Url; }

        readonly List<Tab> tabs = new List<Tab>(); Tab active;
        CoreWebView2Environment env; readonly MediaEngine engine = new MediaEngine();
        readonly string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtuStream");
        List<Fav> favs = new List<Fav>(); List<Fav> history = new List<Fav>();
        bool dark, level1; bool seeking; volatile bool previewBusy;
        PerformanceCounter cpu; readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        public MainWindow()
        {
            InitializeComponent(); Directory.CreateDirectory(data);
            favs = Load("favorites.json"); history = Load("history.json");
            try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpu.NextValue(); } catch { }
            engine.Error += m => Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(this, m, "VirtuStream", MessageBoxButton.OK, MessageBoxImage.Warning)));
            engine.Ended += () => Dispatcher.BeginInvoke(new Action(() => BtnPlay.Content = "\uE768"));
            engine.Preview += OnPreview;
            timer.Tick += (s, e) => Tick(); timer.Start();
            Loaded += async (s, e) => { await InitEnv(); await NewTab("https://www.google.com"); };
            Closing += (s, e) => { Save("history.json", history.Skip(Math.Max(0, history.Count - 500)).ToList()); engine.Dispose(); };
            PreviewKeyDown += (s, e) => {
                if (e.KeyboardDevice.Modifiers == ModifierKeys.Control) {
                    if (e.Key == Key.T) { _ = NewTab(null); e.Handled = true; }
                    else if (e.Key == Key.W && active != null) { CloseTab(active); e.Handled = true; }
                    else if (e.Key == Key.L) { Omnibox.Focus(); Omnibox.SelectAll(); e.Handled = true; } }
            };
        }

        // ---------- Persistance ----------
        List<Fav> Load(string n) { try { return JsonConvert.DeserializeObject<List<Fav>>(File.ReadAllText(Path.Combine(data, n))) ?? new List<Fav>(); } catch { return new List<Fav>(); } }
        void Save(string n, List<Fav> l) { try { File.WriteAllText(Path.Combine(data, n), JsonConvert.SerializeObject(l)); } catch { } }

        // ---------- Navigateur ----------
        async Task InitEnv()
        {   // Un seul environnement WebView2 (runtime Edge partagé) ; nombre de processus limité pour 4 Go de RAM.
            env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "profile"),
                new CoreWebView2EnvironmentOptions("--renderer-process-limit=3 --disable-background-networking --enable-features=msWebView2EnableDraggableRegions"));
        }

        async Task NewTab(string url)
        {
            var wv = new WebView2 { Visibility = Visibility.Collapsed }; ViewHost.Children.Add(wv);
            await wv.EnsureCoreWebView2Async(env);
            var c = wv.CoreWebView2; c.Settings.AreDevToolsEnabled = true; c.Settings.IsWebMessageEnabled = false; // aucun canal privilégié page -> hôte
            c.Settings.AreHostObjectsAllowed = false;
            var t = new Tab { View = wv };
            t.Title = new TextBlock { Text = "Nouvel onglet", MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var close = new Button { Content = "\uE711", Style = (Style)FindResource("IconBtn"), Width = 20, Height = 20, FontSize = 9 };
            close.Click += (s, e) => CloseTab(t);
            var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(t.Title); sp.Children.Add(close);
            t.Header = new Border { Child = sp, Padding = new Thickness(12, 6, 6, 6), Margin = new Thickness(0, 0, 1, 0), CornerRadius = new CornerRadius(8, 8, 0, 0), MinWidth = 120, Height = 30 };
            t.Header.MouseLeftButtonDown += (s, e) => Select(t);
            TabStrip.Children.Add(t.Header); tabs.Add(t);

            c.DocumentTitleChanged += (s, e) => t.Title.Text = string.IsNullOrEmpty(c.DocumentTitle) ? "Nouvel onglet" : c.DocumentTitle;
            c.SourceChanged += (s, e) => { if (t == active) Omnibox.Text = c.Source; };
            c.HistoryChanged += (s, e) => { if (t == active) UpdateNav(); };
            c.NavigationCompleted += (s, e) => { if (e.IsSuccess && !c.Source.StartsWith("about:")) history.Add(new Fav { Title = c.DocumentTitle, Url = c.Source }); };
            c.NewWindowRequested += (s, e) => { e.Handled = true; _ = NewTab(e.Uri); };
            c.PermissionRequested += (s, e) => OnPermission(e);
            c.ContainsFullScreenElementChanged += (s, e) => { WindowStyle = c.ContainsFullScreenElement ? WindowStyle.None : WindowStyle.SingleBorderWindow; WindowState = c.ContainsFullScreenElement ? WindowState.Maximized : WindowState.Normal; };
            await InstallLevel1(c);
            Select(t);
            if (url != null) c.Navigate(url); else c.NavigateToString("<html><body style='font-family:Segoe UI;color:#5f6368;display:flex;align-items:center;justify-content:center;height:100vh'>Nouvel onglet</body></html>");
        }

        void OnPermission(CoreWebView2PermissionRequestedEventArgs e)
        {
            var kind = e.PermissionKind;
            string name = kind == CoreWebView2PermissionKind.Camera ? "la caméra" : kind == CoreWebView2PermissionKind.Microphone ? "le microphone" :
                          kind == CoreWebView2PermissionKind.Geolocation ? "votre position" : kind == CoreWebView2PermissionKind.Notifications ? "les notifications" : null;
            if (name == null) { e.State = CoreWebView2PermissionState.Default; return; }
            var d = e.GetDeferral();
            Dispatcher.BeginInvoke(new Action(() => {
                var r = MessageBox.Show(this, new Uri(e.Uri).Host + " souhaite utiliser " + name + ". Autoriser ?", "Autorisation", MessageBoxButton.YesNo, MessageBoxImage.Question);
                e.State = r == MessageBoxResult.Yes ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny; d.Complete();
            }));
        }

        void Select(Tab t)
        {
            active = t;
            foreach (var x in tabs) { x.View.Visibility = x == t ? Visibility.Visible : Visibility.Collapsed;
                x.Header.SetResourceReference(Border.BackgroundProperty, x == t ? "TabActiveBg" : "TabBarBg");
                x.Title.SetResourceReference(TextBlock.ForegroundProperty, x == t ? "Fg" : "FgMuted"); }
            Omnibox.Text = t.View.CoreWebView2?.Source ?? ""; UpdateNav();
        }
        void CloseTab(Tab t)
        {
            int i = tabs.IndexOf(t); tabs.Remove(t); TabStrip.Children.Remove(t.Header); ViewHost.Children.Remove(t.View); t.View.Dispose();
            if (tabs.Count == 0) { Close(); return; }
            if (t == active) Select(tabs[Math.Min(i, tabs.Count - 1)]);
        }
        void UpdateNav() { var c = active?.View.CoreWebView2; BtnBack.IsEnabled = c?.CanGoBack ?? false; BtnFwd.IsEnabled = c?.CanGoForward ?? false; }

        static string Normalize(string s)
        {
            s = s.Trim(); if (s.Length == 0) return null;
            if (Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "edge")) return s;
            if (!s.Contains(" ") && s.Contains(".")) return "https://" + s;
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(s);
        }
        void Omnibox_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { var u = Normalize(Omnibox.Text); if (u != null) active?.View.CoreWebView2.Navigate(u); active?.View.Focus(); } }
        void Omnibox_GotFocus(object s, RoutedEventArgs e) => Omnibox.SelectAll();
        void NewTab_Click(object s, RoutedEventArgs e) => _ = NewTab(null);
        void Back_Click(object s, RoutedEventArgs e) { if (active?.View.CanGoBack == true) active.View.GoBack(); }
        void Fwd_Click(object s, RoutedEventArgs e) { if (active?.View.CanGoForward == true) active.View.GoForward(); }
        void Reload_Click(object s, RoutedEventArgs e) => active?.View.Reload();
        void Bookmark_Click(object s, RoutedEventArgs e)
        { var c = active?.View.CoreWebView2; if (c == null || c.Source.StartsWith("about:")) return;
          if (!favs.Any(f => f.Url == c.Source)) { favs.Add(new Fav { Title = c.DocumentTitle, Url = c.Source }); Save("favorites.json", favs); } }

        void Menu_Click(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            var fm = new MenuItem { Header = "Favoris" }; foreach (var f in favs) { var u = f.Url; var mi = new MenuItem { Header = string.IsNullOrEmpty(f.Title) ? f.Url : f.Title }; mi.Click += (a, b) => _ = NewTab(u); fm.Items.Add(mi); }
            if (favs.Count == 0) fm.Items.Add(new MenuItem { Header = "(vide)", IsEnabled = false });
            var hm = new MenuItem { Header = "Historique" }; foreach (var f in Enumerable.Reverse(history).Take(15)) { var u = f.Url; var mi = new MenuItem { Header = string.IsNullOrEmpty(f.Title) ? f.Url : f.Title }; mi.Click += (a, b) => _ = NewTab(u); hm.Items.Add(mi); }
            var th = new MenuItem { Header = dark ? "Thème clair" : "Thème sombre" }; th.Click += (a, b) => SetTheme(!dark);
            var dl = new MenuItem { Header = "Téléchargements" }; dl.Click += (a, b) => active?.View.CoreWebView2.OpenDefaultDownloadDialog();
            m.Items.Add(fm); m.Items.Add(hm); m.Items.Add(dl); m.Items.Add(new Separator()); m.Items.Add(th);
            m.PlacementTarget = (UIElement)s; m.IsOpen = true;
        }

        void SetTheme(bool d)
        {
            dark = d; var r = Application.Current.Resources;
            void S(string k, string c) => r[k] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
            if (d) { S("TabBarBg", "#202124"); S("ToolbarBg", "#35363A"); S("TabActiveBg", "#35363A"); S("OmniboxBg", "#202124"); S("PanelBg", "#292A2D");
                     S("Fg", "#E8EAED"); S("FgMuted", "#9AA0A6"); S("Accent", "#8AB4F8"); S("Hover", "#4A4C50"); S("Line", "#3C4043"); }
            else   { S("TabBarBg", "#DEE1E6"); S("ToolbarBg", "#FFFFFF"); S("TabActiveBg", "#FFFFFF"); S("OmniboxBg", "#F1F3F4"); S("PanelBg", "#F8F9FA");
                     S("Fg", "#202124"); S("FgMuted", "#5F6368"); S("Accent", "#1A73E8"); S("Hover", "#E8EAED"); S("Line", "#DADCE0"); }
            foreach (var t in tabs) t.View.CoreWebView2.Profile.PreferredColorScheme = d ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            if (active != null) Select(active);
        }

        // ---------- Niveau 1 : caméra interne ----------
        string l1Script; string l1Id;
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
            bool was = engine.Playing;
            if (engine.Load(d.FileName)) {   // changement à chaud : la caméra reste active, la dernière image est répétée
                FileLabel.Text = Path.GetFileName(d.FileName); engine.Loop = ChkLoop.IsChecked == true;
                foreach (var t in tabs) { ApplyMapping(t.View.CoreWebView2); _ = t.View.CoreWebView2.ExecuteScriptAsync(CfgScript()); }
                engine.Play(); BtnPlay.Content = "\uE769"; }
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
                if (ChkCam.IsChecked == true) {
                    if (!SoftCam.IsAvailable()) throw new InvalidOperationException("Composant caméra virtuelle non installé. Relancez l'installateur (droits administrateur requis).");
                    engine.EnableCamera(); }
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
            if (!IsLoaded || CmbRes == null || CmbFps == null) return;
            int w = CmbRes.SelectedIndex == 0 ? 1280 : 1920, h = CmbRes.SelectedIndex == 0 ? 720 : 1080, f = CmbFps.SelectedIndex == 0 ? 24 : 30;
            engine.Reconfigure(w, h, f, ChkHw.IsChecked == true);
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
            var me = Process.GetCurrentProcess();
            long total = me.WorkingSet64; try { foreach (var p in Process.GetProcessesByName("ffmpeg")) total += p.WorkingSet64; foreach (var p in Process.GetProcessesByName("msedgewebview2")) total += p.WorkingSet64; } catch { }
            Stats.Text = string.Format(
                "Source        : {0}×{1}\nSortie        : {2}×{3}\nFPS demandés  : {4}\nFPS mesurés   : {5:0.0}\nImages perdues: {6}\nDécodage HW   : {7}\nCaméra        : {8}\nMicro virtuel : {9}\nCPU système   : {10}\nRAM (app+ffmpeg+webview): {11} Mo",
                engine.SrcW, engine.SrcH, engine.Width, engine.Height, engine.Fps, engine.MeasuredFps, engine.Dropped, engine.DecodeInfo,
                !engine.CameraOn ? "inactive" : (engine.CameraHasConsumer ? "active, lue par une application" : "active, aucune application connectée"),
                engine.MicOn ? "actif" : "inactif", cpu != null ? cpu.NextValue().ToString("0") + " %" : "n/d", total / 1048576);
        }
    }
}
