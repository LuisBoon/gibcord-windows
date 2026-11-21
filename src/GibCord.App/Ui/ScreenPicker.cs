using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GibCord.App.Ui;

/// <summary>
/// Własne okienko „Udostępnij ekran” (zamiast systemowego): zakładki Ekrany / Okna z podglądami, jakość, płynność,
/// dźwięk z komputera i „Na żywo”. Podwójne kliknięcie podglądu = od razu na żywo. Systemowe okienko zostaje jako zapasowe.
/// </summary>
static class ScreenPicker
{
    public static void Show(Shell sh)
    {
        var p = App.Prefs;
        var cid = sh.C.VoiceChannelId;
        var max = sh.S.ChannelById(cid)?.MaxStream ?? "1440p";
        var levels = new[] { "720p", "1080p", "1440p" };
        var allowed = levels.Take(Array.IndexOf(levels, max) is var mi and >= 0 ? mi + 1 : 3).ToArray();
        var quality = allowed.Contains(p.StreamQuality) ? p.StreamQuality : allowed[^1] == "1440p" ? "1080p" : allowed[^1];
        var fps = p.StreamFps >= 60 ? 60 : 30;
        List<ScreenSource> screens = [], apps = [];
        ScreenSource? chosen = null;
        var onScreens = true;

        var tiles = new WrapPanel { Spacing = 12, Fill = true };
        var status = U.T("Szukam ekranów i okien…", 13, null, Theme.Mu);
        // lista podglądów dopasowana do okna: przyciski na dole zawsze widoczne (okienko zaczyna się 70 px od góry)
        var listArea = new Grid { Height = Math.Clamp(sh.ActualHeight - 400, 150, 360) };
        listArea.Children.Add(U.Scroll(U.V(8, status, tiles)));
        var tabs = U.H(6);
        var go = U.Primary("Na żywo", () => { }, 40).With(b => { b.IsEnabled = false; b.HorizontalAlignment = HorizontalAlignment.Right; b.Width = 150; });
        var tileMap = new Dictionary<ScreenSource, (Border Frame, Image Img)>();

        void Pick(ScreenSource s)
        {
            chosen = s;
            foreach (var (src, t) in tileMap) t.Frame.BorderBrush = ReferenceEquals(src, s) ? Theme.Accent : U.W(.08);
            go.IsEnabled = true;
        }

        async void SetThumb(Image img, byte[]? png)
        {
            if (png is null) return;
            try
            {
                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await ms.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(png));
                ms.Seek(0);
                var bi = new BitmapImage();
                await bi.SetSourceAsync(ms);
                img.Source = bi;
            }
            catch { }
        }

        FrameworkElement Tile(ScreenSource s)
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            var placeholder = U.I(s.IsScreen ? "" : "", 26, Theme.Mu2).With(i => i.HorizontalAlignment = HorizontalAlignment.Center);
            var frame = new Border
            {
                Width = 208, Height = 117, CornerRadius = new CornerRadius(10), Background = U.W(.06), BorderThickness = U.Th(2), BorderBrush = U.W(.08),
                Child = new Grid().Add(placeholder, img),
            };
            tileMap[s] = (frame, img);
            SetThumb(img, s.Thumb);
            var box = U.V(6, frame,
                U.T(s.Title, 12.5, U.Semi).With(t => { t.MaxWidth = 208; t.HorizontalAlignment = HorizontalAlignment.Left; }),
                U.T(s.Sub, 11, null, Theme.Mu2).With(t => t.MaxWidth = 208));
            var hit = new Border { Child = box, Background = Theme.Transparent, Width = 208 };
            hit.Tapped += (_, _) => Pick(s);
            hit.DoubleTapped += (_, _) => { Pick(s); Go(); };
            hit.PointerEntered += (_, _) => { if (!ReferenceEquals(chosen, s)) frame.BorderBrush = U.W(.25); };
            hit.PointerExited += (_, _) => { if (!ReferenceEquals(chosen, s)) frame.BorderBrush = U.W(.08); };
            ToolTipService.SetToolTip(hit, s.Title);
            return hit;
        }

        void Draw()
        {
            tabs.Children.Clear();
            Button Tab(string label, bool on, Action a)
            {
                var b = new Button { Content = U.T(label, 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(14, 5) };
                b.Click += (_, _) => a();
                return b;
            }
            tabs.Add(Tab($"Ekrany ({screens.Count})", onScreens, () => { onScreens = true; Draw(); }),
                Tab($"Okna ({apps.Count})", !onScreens, () => { onScreens = false; Draw(); }));
            tiles.Children.Clear();
            tileMap.Clear();
            var list = onScreens ? screens : apps;
            status.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count == 0 && status.Text != "Szukam ekranów i okien…") status.Text = onScreens ? "Nie widać żadnego ekranu." : "Nie widać okien do udostępnienia.";
            foreach (var s in list) tiles.Children.Add(Tile(s));
            if (chosen is not null && tileMap.ContainsKey(chosen)) Pick(chosen);
        }

        // jakość, płynność, dźwięk
        var qRow = U.H(6);
        var fRow = U.H(6);
        void DrawOptions()
        {
            qRow.Children.Clear();
            foreach (var q in allowed)
            {
                var on = q == quality; var qq = q;
                var b = new Button { Content = U.T(q, 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(10), Padding = U.Th(12, 4) };
                b.Click += (_, _) => { quality = qq; DrawOptions(); };
                qRow.Children.Add(b);
            }
            fRow.Children.Clear();
            foreach (var f in new[] { 30, 60 })
            {
                var on = f == fps; var ff = f;
                var b = new Button { Content = U.T(f == 60 ? "60 kl/s (gry)" : "30 kl/s", 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(10), Padding = U.Th(12, 4) };
                b.Click += (_, _) => { fps = ff; DrawOptions(); };
                fRow.Children.Add(b);
            }
        }
        DrawOptions();
        var audio = new CheckBox { Content = U.T("Dźwięk z komputera", 12.5, U.Semi), IsChecked = p.StreamAudio, MinWidth = 0 };
        ToolTipService.SetToolTip(audio, "Gra, film, muzyka — bez dźwięków rozmowy");
        var options = new WrapPanel { Spacing = 18 }.Add(
            U.V(4, U.T("JAKOŚĆ", 10.5, U.Bold, Theme.Mu2), qRow),
            U.V(4, U.T("PŁYNNOŚĆ", 10.5, U.Bold, Theme.Mu2), fRow),
            U.V(4, U.T("DŹWIĘK", 10.5, U.Bold, Theme.Mu2), audio));

        var system = new HyperlinkButton { Content = U.T("Systemowe okienko wyboru…", 12, U.Semi, Theme.Mu), Padding = U.Th(0) };
        system.Click += async (_, _) =>
        {
            Save();
            sh.CloseOverlay();
            if (sh.W.Screen is not { } ss) return;
            var err = await ss.Start(sh.W.Hwnd, fps, quality, audio.IsChecked == true);
            if (err is not null) sh.Info(err, true);
            sh.RefreshVoice();
        };
        var cancel = U.Ghost("Anuluj", sh.CloseOverlay, 40);
        var footer = new Grid { ColumnSpacing = 10 }.Cols("*", "auto", "auto").Add(system.With(s => s.VerticalAlignment = VerticalAlignment.Center).At(0, 0), cancel.At(0, 1), go.At(0, 2));
        var head = new Grid().Cols("*", "auto").Add(
            U.V(2, U.T("Udostępnij ekran", 19, U.Bold), U.T(max != "1440p" ? $"Na tym kanale najwyżej {max}." : "Wybierz ekran albo okno — inni zobaczą je na żywo.", 12, null, Theme.Mu)).At(0, 0),
            U.IconButton(G.Close, "Zamknij (Esc)", sh.CloseOverlay, 14, 32).At(0, 1));
        var card = new Border
        {
            Width = 736, Background = Theme.Surface, CornerRadius = new CornerRadius(16), Padding = U.Th(22, 18), BorderBrush = U.W(.12), BorderThickness = U.Th(1),
            Child = U.V(14, head, tabs, listArea, U.HLine(.08), options, footer),
        };

        void Save()
        {
            p.StreamQuality = quality; p.StreamFps = fps; p.StreamAudio = audio.IsChecked == true; p.Save();
        }
        async void Go()
        {
            if (chosen is not { } s || sh.W.Screen is not { } ss) return;
            Save();
            sh.CloseOverlay();
            Windows.Graphics.Capture.GraphicsCaptureItem? item = null;
            try { item = ScreenSources.ItemFor(s); }
            catch (Exception ex) { App.Log("Wybór ekranu: " + ex.Message); }
            if (item is null) { sh.Info("Nie da się udostępnić tego " + (s.IsScreen ? "ekranu" : "okna") + " — spróbuj „Systemowe okienko wyboru…”.", true); return; }
            var err = await ss.Start(item, fps, quality, audio.IsChecked == true);
            if (err is not null) sh.Info(err, true);
            sh.RefreshVoice();
        }
        go.Click += (_, _) => Go();

        Draw();
        sh.ShowOverlay(card);
        _ = Load();

        async Task Load()
        {
            try
            {
                var (sc, ap) = await Task.Run(() => (ScreenSources.Screens(), ScreenSources.Apps()));
                screens = sc; apps = ap;
                status.Text = "";
                if (screens.Count > 0) chosen ??= screens[0];
                sh.DispatcherQueue.TryEnqueue(Draw);
                // podglądy okien w tle, po kilka naraz — kafelki uzupełniają się, gdy są gotowe
                await Task.Run(() => Parallel.ForEach(apps, new ParallelOptions { MaxDegreeOfParallelism = 3 }, s =>
                {
                    ScreenSources.ThumbOf(s);
                    if (s.Thumb is { } png) sh.DispatcherQueue.TryEnqueue(() => { if (tileMap.TryGetValue(s, out var t)) SetThumb(t.Img, png); });
                }));
            }
            catch (Exception ex)
            {
                App.Log("Wybór ekranu: " + ex.Message);
                sh.DispatcherQueue.TryEnqueue(() => { status.Text = "Nie udało się pobrać listy — użyj „Systemowe okienko wyboru…”."; status.Visibility = Visibility.Visible; });
            }
        }
    }
}
