using System.Runtime.InteropServices.WindowsRuntime;
using GibCord.Core;
using GibCord.Core.Voice;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace GibCord.App.Ui;

/// <summary>Ikony rozmów (Segoe Fluent Icons).</summary>
static class G
{
    public const string Mic = "", MicOff = "", Headphones = "", Speaker = "", SpeakerOff = "",
        Phone = "", HangUp = "", Screen = "", Camera = "", Signal = "", Eye = "",
        Settings = "", Close = "", Keyboard = "", Voice = "";
}

/// <summary>Kawałki interfejsu rozmów używane w kilku miejscach (panel w kolumnie, pasek rozmowy w DM, dzwonek).</summary>
static class VoiceUi
{
    public static string StateText(GibCordClient c) => c.VoiceStatus switch
    {
        VoiceState.Connected => c.Voice.Quality <= 1 && !c.Offline ? "Połączono · słaby internet" : "Połączono z głosem",
        VoiceState.Reconnecting => "Ponowne łączenie…",
        _ => "Łączenie…",
    };

    public static Brush StateBrush(GibCordClient c) => c.VoiceStatus switch
    {
        VoiceState.Connected => c.Voice.Quality <= 1 && !c.Offline ? Theme.Yellow : Theme.Green,
        _ => Theme.Yellow,
    };

    /// <summary>Nazwa pokoju: „#kanał / serwer” albo nazwa rozmowy prywatnej.</summary>
    public static (string Title, string Sub) RoomName(Store s, long cid)
    {
        if (s.ChannelById(cid) is { } ch) return (ch.Name, s.ServerOfChannel(cid)?.Name ?? "");
        if (s.DmById(cid) is { } d) return (s.DmTitle(d), d.IsGroup ? "Rozmowa w grupie" : "Rozmowa prywatna");
        return ("Rozmowa", "");
    }

    /// <summary>Panel nad „ja” w kolumnie kanałów, gdy jesteśmy w rozmowie.</summary>
    public static FrameworkElement? Panel(Shell sh)
    {
        var c = sh.C;
        if (c.VoiceChannelId == 0) return null;
        var (title, sub) = RoomName(sh.S, c.VoiceChannelId);
        var state = U.T(StateText(c), 12.5, U.Bold, StateBrush(c));
        var where = U.T(string.IsNullOrEmpty(sub) ? title : $"{title} / {sub}", 11.5, null, Theme.Mu);
        // ping na żywo: mały wykres ostatnich pomiarów + liczba (co 3 s, w rozmowie serwer pingowany częściej)
        var spark = new Border { Width = 26, Height = 12, Margin = U.Th(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        var ms = U.T("", 11.5, U.Bold, Theme.Mu);
        void Ping()
        {
            var rtt = c.Rtt?.TotalMilliseconds;
            ms.Text = rtt is double r ? $"{Math.Round(r)} ms · " : "";
            ms.Foreground = U.PingBrush(rtt);
            spark.Child = U.Sparkline(c.RttHistory.TakeLast(20).ToList(), 26, 12, U.PingBrush(rtt));
        }
        Ping();
        var line2 = U.HG(0, spark, ms, where);
        var lockIcon = E2eeUi.Icon(sh, c.VoiceChannelId); // 6.4: szyfrowana rozmowa prywatna
        var text = U.V(0, lockIcon is null ? state : U.HG(0, lockIcon, state), line2);
        var go = new Border { Child = new Grid().Cols("auto", "*").Add(U.I(G.Signal, 16, StateBrush(c)).With(i => i.Margin = U.Th(2, 0, 8, 0)).At(0, 0), text.At(0, 1)), Background = Theme.Transparent, CornerRadius = new CornerRadius(8), Padding = U.Th(4, 4) };
        go.Tip("Przejdź do rozmowy (prawy przycisk: stan połączenia i wykres pingu)");
        go.RightTapped += (_, e) => { e.Handled = true; ShowStats(sh, go); };
        go.PointerEntered += (_, _) => go.Background = Theme.Hover;
        go.PointerExited += (_, _) => go.Background = Theme.Transparent;
        go.Tapped += (_, _) => sh.OpenChannel(c.VoiceChannelId);
        var leave = U.IconButton(G.HangUp, "Rozłącz", () => _ = c.LeaveVoice(), 15, 32, Theme.Red);
        var right = U.H(0);
        if (!c.Offline && sh.W.Screen is not null)
            right.Add(U.IconButton(G.Screen, c.Voice.ScreenSharing ? "Zakończ transmisję" : "Udostępnij ekran", () => ToggleScreen(sh), 15, 32, c.Voice.ScreenSharing ? Theme.Red : null));
        right.Add(SoundboardButton(sh, 32));
        right.Add(leave);
        var row = new Grid().Cols("*", "auto").Add(go.At(0, 0), right.At(0, 1));
        var box = new Border { Child = row, Padding = U.Th(8, 6, 6, 6), Background = Theme.Rail, BorderBrush = Theme.Line, BorderThickness = U.Th(0, 1, 0, 0) };
        var pingTimer = sh.DispatcherQueue.CreateTimer();
        pingTimer.Interval = TimeSpan.FromSeconds(3);
        pingTimer.Tick += (_, _) => Ping();
        box.Loaded += (_, _) => pingTimer.Start();
        box.Unloaded += (_, _) => pingTimer.Stop();
        var radio = RadioUi.Mini(sh); // 6.2
        if (c.Voice.ScreenSharing)
            return U.V(0, radio, box, new Border { Background = U.Alpha("#ff4d6a", .18), Padding = U.Th(12, 4), Child = U.T("Nadajesz ekran", 11.5, U.Bold, Theme.Red) });
        return radio is null ? box : U.V(0, radio, box);
    }

    /// <summary>Stan połączenia: ping do serwera, jakość głosu, ustawienia pokoju, odświeżane co sekundę.</summary>
    public static void ShowStats(Shell sh, FrameworkElement anchor)
    {
        var c = sh.C;
        var panel = new StackPanel { Spacing = 8, Width = 300 };
        var fly = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        var t = sh.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        void Draw()
        {
            panel.Children.Clear();
            panel.Add(U.T("Stan połączenia", 15, U.Bold));
            FrameworkElement Row(string k, string v, Brush? fg = null) => new Grid().Cols("*", "auto").Add(U.T(k, 13, null, Theme.Mu).At(0, 0), U.T(v, 13, U.Bold, fg ?? Theme.Fg).At(0, 1));
            var rtt = c.Rtt?.TotalMilliseconds;
            panel.Add(Row("Ping do serwera", rtt is double r ? $"{Math.Round(r)} ms" : "—", U.PingBrush(rtt)));
            // wykres pingu z ostatnich ~3 minut (pomiar co 3 s w rozmowie)
            var hist = c.RttHistory.TakeLast(60).ToList();
            if (hist.Count >= 2)
            {
                var chart = new Border { Background = U.W(.04), CornerRadius = new CornerRadius(8), Padding = U.Th(8, 6), Child = U.Sparkline(hist, 284, 52, U.PingBrush(rtt), 100, fill: true) };
                panel.Add(chart);
                panel.Add(U.T($"najniższy {Math.Round(hist.Min())} ms · średni {Math.Round(hist.Average())} ms · najwyższy {Math.Round(hist.Max())} ms", 11, null, Theme.Mu2));
            }
            var q = c.Voice.Quality;
            panel.Add(Row("Jakość głosu", q switch { >= 3 => "świetna", 2 => "dobra", 1 => "słaba", _ => "bardzo słaba" }, q >= 3 ? Theme.Green : q == 2 ? Theme.Teal : q == 1 ? Theme.Yellow : Theme.Red));
            panel.Add(Row("Stan", StateText(c)));
            if (sh.S.ChannelById(c.VoiceChannelId) is { } ch)
            {
                panel.Add(Row("Dźwięk kanału", $"{(ch.Bitrate > 0 ? ch.Bitrate : 64)} kb/s"));
                panel.Add(Row("Transmisje do", ch.MaxStream ?? "1080p"));
            }
            panel.Add(Row("Osób w rozmowie", (sh.S.Voice.GetValueOrDefault(c.VoiceChannelId)?.Count ?? 0).ToString()));
            if (c.Voice.ScreenSharing) panel.Add(Row("Nadajesz ekran", App.Prefs.StreamQuality + " · " + App.Prefs.StreamFps + " kl/s" + (sh.W.Screen?.AudioActive == true ? " · z dźwiękiem" : ""), Theme.Red));
            panel.Add(U.T("Głos idzie przez serwer GibCorda (LiveKit), szyfrowany w drodze.", 11, null, Theme.Mu2, wrap: true));
        }
        t.Tick += (_, _) => Draw();
        fly.Opened += (_, _) => t.Start();
        fly.Closed += (_, _) => t.Stop();
        Draw();
        fly.ShowAt(anchor);
    }

    /// <summary>Przycisk soundboardu: dźwięki wbudowane i serwera, słyszą je wszyscy w rozmowie.</summary>
    public static Button SoundboardButton(Shell sh, double box)
    {
        var b = U.IconButton("\uE7F5", "Soundboard — puść dźwięk wszystkim w rozmowie", () => { }, box > 40 ? 16 : 15, box);
        b.Click += (_, _) => ShowSoundboard(sh, b);
        return b;
    }

    public static void ShowSoundboard(Shell sh, FrameworkElement anchor)
    {
        var c = sh.C;
        var srv = sh.S.ServerOfChannel(c.VoiceChannelId);
        var panel = new StackPanel { Spacing = 6, Width = 340 };
        var fly = new Flyout { Content = U.Scroll(panel).With(s => s.MaxHeight = 420), Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        if (srv is not null && !sh.S.Can(c.VoiceChannelId, Perm.UseSoundboard))
        {
            panel.Add(U.T("Nie masz uprawnień do dźwięków na tym kanale.", 13, null, Theme.Mu, wrap: true));
            fly.ShowAt(anchor);
            return;
        }
        Button Snd(string emoji, string name, Action play)
        {
            var btn = new Button
            {
                Content = U.H(6, U.T(emoji, 16), U.T(name, 12.5, U.Semi).With(t => t.MaxWidth = 110)), Background = U.W(.08), BorderThickness = U.Th(0),
                CornerRadius = new CornerRadius(10), Padding = U.Th(10, 6), Width = 162, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            btn.Click += (_, _) => play();
            return btn;
        }
        panel.Add(U.T("WBUDOWANE", 10.5, U.Bold, Theme.Mu2));
        var wrap = new WrapPanel { Spacing = 6 };
        var keys = c.Info?.BuiltinSounds.Select(x => x.Key).ToList() is { Count: > 0 } k ? k : GibCordClient.BuiltinSounds.Keys.ToList();
        foreach (var key in keys)
        {
            var (emo, name) = GibCordClient.BuiltinSounds.TryGetValue(key, out var bi) ? bi : ("🔊", key);
            wrap.Children.Add(Snd(emo, name, () => c.PlaySound(null, key)));
        }
        panel.Add(wrap);
        if (srv is { Sounds.Count: > 0 })
        {
            panel.Add(U.T(srv.Name.ToUpperInvariant(), 10.5, U.Bold, Theme.Mu2).With(t => t.Margin = U.Th(0, 6, 0, 0)));
            var w2 = new WrapPanel { Spacing = 6 };
            foreach (var snd in srv.Sounds) { var id = snd.Id; w2.Children.Add(Snd(string.IsNullOrEmpty(snd.Emoji) ? "🔊" : snd.Emoji!, snd.Name, () => c.PlaySound(id, null))); }
            panel.Add(w2);
        }
        panel.Add(U.T("Jeden dźwięk na 2 sekundy. Słyszą go wszyscy w rozmowie (poza wygłuszonymi).", 11, null, Theme.Mu2, wrap: true).With(t => t.Margin = U.Th(0, 6, 0, 0)));
        fly.ShowAt(anchor);
    }

    /// <summary>Start / koniec transmisji ekranu (systemowe okienko wyboru ekranu albo okna).</summary>
    public static async void ToggleScreen(Shell sh)
    {
        var ss = sh.W.Screen;
        if (ss is null) return;
        if (ss.Active || sh.C.Voice.ScreenSharing) { ss.Stop(); sh.RefreshVoice(); return; }
        var cid = sh.C.VoiceChannelId;
        if (sh.S.ServerOfChannel(cid) is not null && !sh.S.Can(cid, Perm.Stream)) { sh.Info("Nie masz uprawnień do transmisji na tym kanale.", true); return; }
        if (!ScreenShare.Supported) { sh.Info("Ten Windows nie pozwala nagrywać ekranu (potrzebny Windows 10 1903 albo nowszy).", true); return; }
        // własne okienko wyboru: ekrany i okna z podglądami, jakość, płynność, dźwięk (systemowe zostaje jako zapasowe)
        ScreenPicker.Show(sh);
    }

    /// <summary>Kamera w rozmowie: włącz / wyłącz.</summary>
    public static async void ToggleCamera(Shell sh)
    {
        var cam = sh.W.Cam;
        if (cam is null) return;
        if (cam.Active) { cam.Stop(); sh.RefreshVoice(); return; }
        var err = await cam.Start();
        if (err is not null) sh.Info(err, true);
        sh.RefreshVoice();
    }

    /// <summary>Przyciski mikrofon / słuchawki w panelu „ja” — działają też poza rozmową (jak na stronie).</summary>
    public static (Button Mic, Button Deaf) MuteButtons(Shell sh)
    {
        var o = sh.C.Voice.Options;
        var micOff = o.Muted || o.Deafened;
        var mic = U.IconButton(micOff ? G.MicOff : G.Mic, micOff ? "Włącz mikrofon" : "Wycisz mikrofon", () => { sh.C.ToggleMute(); Tones.Play(sh.C.Voice.Options.Muted ? "mute" : "unmute"); App.Prefs.Save(); }, 15, 30, micOff ? Theme.Red : null);
        var deaf = U.IconButton(o.Deafened ? G.SpeakerOff : G.Headphones, o.Deafened ? "Włącz dźwięk" : "Wyłącz dźwięk (wygłuszenie)", () => { sh.C.ToggleDeafen(); Tones.Play(sh.C.Voice.Options.Deafened ? "mute" : "unmute"); App.Prefs.Save(); }, 15, 30, o.Deafened ? Theme.Red : null);
        return (mic, deaf);
    }

    /// <summary>Głośność osoby (0–200%) i wyciszenie tylko u mnie — dymek pod prawym przyciskiem.</summary>
    public static Flyout VolumeFlyout(Shell sh, long uid, Server? srv)
    {
        var o = sh.C.Voice.Options;
        var label = U.T($"Głośność: {Math.Round(o.VolumeOf(uid) * 100)}%", 12.5, U.Semi, Theme.Mu);
        var slider = new SliderField { Minimum = 0, Maximum = 200, StepFrequency = 5, Value = Math.Round(o.VolumeOf(uid) * 100), Width = 220 };
        slider.ValueChanged += (_, e) =>
        {
            o.UserVolume[uid] = e.NewValue / 100.0;
            if (Math.Abs(e.NewValue - 100) < 1) o.UserVolume.Remove(uid);
            label.Text = $"Głośność: {Math.Round(e.NewValue)}%";
            App.Prefs.Save();
        };
        var mute = new SwitchField { Header = "Wycisz tylko u mnie", IsOn = o.UserMuted.Contains(uid) };
        mute.Toggled += (_, _) => { if (mute.IsOn) o.UserMuted.Add(uid); else o.UserMuted.Remove(uid); App.Prefs.Save(); };
        var panel = U.V(8, U.T(sh.S.NameOf(uid, srv), 14, U.Bold), label, slider, mute);
        panel.Padding = U.Th(4);
        return new Flyout { Content = panel };
    }

    /// <summary>Okienko „X dzwoni do Ciebie” na środku okna.</summary>
    public static FrameworkElement RingCard(Shell sh, long cid, long from)
    {
        var s = sh.S;
        var av = new Avatar(72).Set(s, sh.C.Api, from, null);
        av.Talking = true;
        var dm = s.DmById(cid);
        var title = dm is { IsGroup: true } ? $"{s.NameOf(from, null)} · {s.DmTitle(dm)}" : s.NameOf(from, null);
        var accept = new Button { Content = U.H(8, U.I(G.Phone, 16, U.B("#ffffff")), U.T("Odbierz", 14, U.Bold, U.B("#ffffff"))), Background = U.B("#2fa66a"), CornerRadius = new CornerRadius(12), Padding = U.Th(18, 9), BorderThickness = U.Th(0) };
        var decline = new Button { Content = U.H(8, U.I(G.HangUp, 16, U.B("#ffffff")), U.T("Odrzuć", 14, U.Bold, U.B("#ffffff"))), Background = Theme.Red2, CornerRadius = new CornerRadius(12), Padding = U.Th(18, 9), BorderThickness = U.Th(0) };
        accept.Click += (_, _) => { Tones.StopRing(); sh.CloseOverlay(); _ = sh.C.JoinVoice(cid); sh.OpenChannel(cid); };
        decline.Click += (_, _) => { Tones.StopRing(); sh.CloseOverlay(); sh.C.DeclineCall(cid); };
        var card = U.V(14, av.With(a => a.HorizontalAlignment = HorizontalAlignment.Center),
            U.T(title, 18, U.Bold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            U.T("dzwoni do Ciebie…", 13.5, null, Theme.Mu).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            U.H(12, decline, accept).With(p => p.HorizontalAlignment = HorizontalAlignment.Center));
        return new Border { Child = card, Background = Theme.Surface, CornerRadius = new CornerRadius(18), Padding = U.Th(36, 28), BorderBrush = U.W(.12), BorderThickness = U.Th(1), Width = 360 };
    }
}

/// <summary>Kafelek osoby w rozmowie: awatar (zielona obwódka, gdy mówi) albo obraz z kamery, nazwa, ikonki.</summary>
sealed class VoiceTile : Grid
{
    public long Uid { get; }
    public Avatar Av { get; }
    public Image Video { get; } = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    readonly Border _frame;

    public VoiceTile(Shell sh, VoiceMember vm, Server? srv, double w, double h, bool me)
    {
        Uid = vm.UserId;
        Width = w; Height = h;
        Av = new Avatar(80) { ShowDecor = true }.Set(sh.S, sh.C.Api, vm.UserId, srv);
        // awatar skaluje się razem z kafelkiem (kafelki dopasowują się do szerokości okna)
        var avBox = new Viewbox { Child = Av, Stretch = Stretch.Uniform, Height = Math.Min(80, h * .45), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        SizeChanged += (_, e) => avBox.Height = Math.Max(28, Math.Min(80, e.NewSize.Height * .45));
        _frame = new Border { CornerRadius = new CornerRadius(14), Background = U.W(.06), BorderBrush = U.W(.08), BorderThickness = U.Th(2) };
        var icons = U.H(4);
        if (vm.Muted || vm.ServerMuted || vm.Suppressed) icons.Add(U.I(G.MicOff, 13, Theme.Red));
        if (vm.Deafened || vm.ServerDeafened) icons.Add(U.I(G.SpeakerOff, 13, Theme.Red));
        if (VoiceFxUi.Badge(vm.Fx) is { } fxb) icons.Add(fxb); // 6.2
        var name = U.T(sh.S.NameOf(vm.UserId, srv) + (me ? " (Ty)" : ""), 12.5, U.Semi);
        var label = new Border { Background = U.B("#99120c22"), CornerRadius = new CornerRadius(8), Padding = U.Th(8, 3), Margin = U.Th(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = U.H(6, name, icons) };
        var center = new Grid().Add(avBox);
        this.Add(_frame, center, new Border { CornerRadius = new CornerRadius(12), Margin = U.Th(2), Child = Video }, label);
        if (vm.Streaming && !me && !sh.C.Voice.IsWatching(vm.UserId) && sh.C.StreamPreviews.ContainsKey(vm.UserId))
        {
            // podgląd transmisji (kadr co ~10 s) zamiast awatara, dopóki ktoś nie kliknie „Oglądaj”
            var pv = new Image { Stretch = Stretch.UniformToFill, Opacity = .85 };
            this.Children.Insert(2, new Border { CornerRadius = new CornerRadius(12), Margin = U.Th(2), Child = pv });
            _ = LoadPreview(sh, vm.UserId, pv);
        }
        if (vm.Streaming)
        {
            var live = U.Box(U.T("NA ŻYWO", 9.5, U.Bold, U.B("#ffffff")), Theme.Red2, 5, U.Th(6, 2));
            live.HorizontalAlignment = HorizontalAlignment.Right; live.VerticalAlignment = VerticalAlignment.Top; live.Margin = U.Th(8);
            this.Add(live);
            if (!me)
            {
                var watching = sh.C.Voice.IsWatching(vm.UserId);
                var btn = new Button
                {
                    Content = U.H(6, U.I(G.Eye, 14, Theme.OnAccent), U.T(watching ? "Przestań oglądać" : "Oglądaj transmisję", 12.5, U.Bold, Theme.OnAccent)),
                    Background = Theme.AccentGradient(), CornerRadius = new CornerRadius(10), Padding = U.Th(12, 6), BorderThickness = U.Th(0),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = U.Th(0, 0, 0, 40),
                };
                btn.Click += (_, _) => { if (sh.C.Voice.IsWatching(vm.UserId)) sh.C.Voice.Unwatch(vm.UserId); else sh.C.Voice.Watch(vm.UserId); sh.RefreshVoice(); };
                this.Add(btn);
            }
        }
    }

    public bool Talking
    {
        set { Av.Talking = value; _frame.BorderBrush = value ? Theme.Green : U.W(.08); }
    }

    static async Task LoadPreview(Shell sh, long uid, Image img)
    {
        try
        {
            var bytes = await sh.C.Api.GetBytes($"voice/{sh.C.VoiceChannelId}/preview/{uid}");
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            img.Source = bmp;
        }
        catch { }
    }
}

/// <summary>Widok kanału głosowego (makieta 1): kafelki osób, oglądana transmisja na dużo, przyciski rozmowy na dole.</summary>
public sealed class VoiceView : Grid, IDisposable
{
    readonly Shell _sh;
    readonly long _cid;
    readonly Border _header = new() { Height = 48, BorderBrush = Theme.Line, BorderThickness = U.Th(0, 0, 0, 1), Padding = U.Th(16, 0, 10, 0) };
    readonly Grid _stage = new();
    readonly Border _controls = new() { Padding = U.Th(0, 10, 0, 16) };
    readonly DispatcherQueueTimer _tick;
    readonly List<VoiceTile> _tiles = [];
    readonly List<VoiceTile> _gridTiles = [];
    readonly Dictionary<string, (Image Img, WriteableBitmap? Bmp, long Seen)> _video = [];
    bool _disposed;
    WatchPanel? _watch;
    BoardPanel? _board;
    /// <summary>6.3: na którym kanale głosowym masz otwartą tablicę (0 = żadnym).</summary>
    public static long BoardOpenFor;

    /// <summary>Kompaktowy: bez nagłówka — scena rozmowy nad czatem w rozmowie prywatnej (kamery, transmisje).</summary>
    public VoiceView(Shell sh, long cid, bool compact = false)
    {
        _sh = sh;
        _cid = cid;
        this.Rows(compact ? "0" : "48", "*", "auto");
        if (compact) { _controls.Padding = U.Th(0, 6, 0, 10); Background = U.B("#40080414"); }
        this.Add(_header.At(0), _stage.At(1), _controls.At(2));
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(66);
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        _stage.SizeChanged += (_, e) => Fit(e.NewSize.Width, e.NewSize.Height);
        Refresh();
        Saver.Changed += OnSaver;
        OnSaver();
    }

    /// <summary>6.4.2: okna nie widać — kafelki (mówienie, obraz z kamer) nie odświeżają się.</summary>
    void OnSaver()
    {
        if (_disposed) return;
        if (Saver.Asleep) _tick.Stop();
        else if (!_tick.IsRunning) _tick.Start();
    }

    /// <summary>Kafelki tak duże, jak się da, żeby wszystkie zmieściły się na ekranie (jak w makiecie).</summary>
    void Fit(double width, double height)
    {
        var n = _gridTiles.Count;
        if (n == 0 || width <= 0 || height <= 0) return;
        const double gap = 12, pad = 32, ratio = .62;
        double bestW = 0;
        for (var cols = 1; cols <= n; cols++)
        {
            var w = (width - pad - (cols - 1) * gap) / cols;
            var rows = (int)Math.Ceiling(n / (double)cols);
            var h = w * ratio;
            if (rows * h + (rows - 1) * gap + pad > height) h = (height - pad - (rows - 1) * gap) / rows;
            w = Math.Min(w, h / ratio);
            if (w > bestW) bestW = w;
        }
        bestW = Math.Clamp(bestW, 150, 440);
        foreach (var t in _gridTiles) { t.Width = bestW; t.Height = Math.Round(bestW * ratio); }
    }

    GibCordClient C => _sh.C;
    bool Here => C.VoiceChannelId == _cid;

    public void Dispose()
    {
        _disposed = true;
        Saver.Changed -= OnSaver;
        _tick.Stop();
        _watch?.Dispose();
        _watch = null;
        _board?.Dispose();
        _board = null;
    }

    public void Refresh()
    {
        if (_disposed) return;
        var s = _sh.S;
        var srv = s.ServerOfChannel(_cid);
        var ch = s.ChannelById(_cid);
        var members = s.Voice.GetValueOrDefault(_cid) ?? [];

        var head = new Grid().Cols("auto", "auto", "*", "auto");
        head.Add(U.I(ch?.IsStage == true ? "\uE789" : G.Voice, 17, Theme.Mu).With(i => i.Margin = U.Th(0, 0, 10, 0)).At(0, 0), U.T(ch?.Name ?? "Rozmowa", 15, U.Bold).At(0, 1),
            U.T(members.Count == 0 ? "nikogo tu nie ma" : $"{members.Count} {Format.Plural(members.Count, "osoba", "osoby", "osób")}", 12.5, null, Theme.Mu).With(t => t.Margin = U.Th(12, 0, 0, 0)).At(0, 2));
        if (Here)
        {
            var st = U.T(VoiceUi.StateText(C), 12.5, U.Bold, VoiceUi.StateBrush(C));
            st.Tip("Kliknij: ping i jakość połączenia");
            st.Tapped += (_, _) => VoiceUi.ShowStats(_sh, st);
            head.Add(st.At(0, 3));
        }
        _header.Child = head;

        // scena: oglądana transmisja na dużo + kafelki
        _stage.Children.Clear();
        _stage.RowDefinitions.Clear();
        _tiles.Clear();
        _gridTiles.Clear();
        var watched = Here ? C.Voice.Feeds.FirstOrDefault(f => f.Screen) : null;
        var me = s.Me.Id;
        if (!Here && BoardOpenFor == _cid) BoardOpenFor = 0;
        // 6.3: tablica na dużo + kafelki pod spodem
        var boardShown = Here && BoardOpenFor == _cid;
        if (boardShown)
        {
            if (_board is null)
            {
                _board = new BoardPanel(_sh, _cid);
                if (Options.Demo && Options.View == "board") _board.Demo(GibCord.App.Demo.OlaId);
                if (Options.Demo && Options.View is "kalambury" or "kalambury2" or "kalkoniec") _board.DemoCastle(Options.View == "kalambury2" ? GibCord.App.Demo.MeId : GibCord.App.Demo.OlaId); // 6.2
            }
            if (_board.Parent is Panel bp) bp.Children.Remove(_board);
            _stage.Rows("*", "auto");
            _stage.Add(_board.At(0));
            var stripB = U.H(10);
            stripB.Padding = U.Th(16, 0, 16, 8);
            foreach (var vm in members) stripB.Add(Tile(vm, srv, 120, 74, vm.UserId == me));
            _stage.Add(new ScrollViewer { Content = stripB, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled }.At(1));
        }
        else if (_board is not null) { _board.Dispose(); _board = null; }
        if (!boardShown) {
        // wspólne oglądanie: odtwarzacz na dużo (jeden na cały czas — bez przeładowania przy odświeżeniu); radio (6.2) gra w tle bez obrazu
        var videoWatch = s.Watches.GetValueOrDefault(_cid) is { Radio: false };
        if (Here && videoWatch)
        {
            _watch ??= new WatchPanel(_sh, _cid);
            if (_watch.Parent is Panel wp) wp.Children.Remove(_watch);
            _stage.Rows("*", "auto");
            _stage.Add(_watch.At(0));
            var strip0 = U.H(10);
            strip0.Padding = U.Th(16, 0, 16, 8);
            foreach (var vm in members) strip0.Add(Tile(vm, srv, 140, 86, vm.UserId == me));
            _stage.Add(new ScrollViewer { Content = strip0, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled }.At(1));
        }
        else if (_watch is not null) { _watch.Dispose(); _watch = null; }
        if (Here && videoWatch) { }
        else if (watched is not null)
        {
            _stage.Rows("*", "auto");
            var img = VideoImage("screen:" + watched.TrackSid);
            var big = new Grid { Margin = U.Th(16, 16, 16, 8) };
            big.Add(new Border { Background = U.B("#cc08040f"), CornerRadius = new CornerRadius(14), Child = img });
            var who = U.T($"{s.NameOf(watched.UserId, srv)} nadaje ekran", 12.5, U.Semi);
            var stop = U.Ghost("Przestań oglądać", () => { if (_sh.FocusMode) _sh.ToggleFocus(); C.Voice.Unwatch(watched.UserId); Refresh(); }, 30);
            var vol = new SliderField { Minimum = 0, Maximum = 200, StepFrequency = 5, Width = 120, Value = Math.Round(C.Voice.Options.StreamVolume * 100), VerticalAlignment = VerticalAlignment.Center };
            vol.ValueChanged += (_, e) => { C.Voice.Options.StreamVolume = e.NewValue / 100; App.Prefs.Save(); };
            ToolTipService.SetToolTip(vol, "Głośność transmisji");
            var full = U.IconButton(_sh.FocusMode ? "\uE73F" : "\uE740", _sh.FocusMode ? "Wyjdź z pełnego ekranu (Esc)" : "Pełny ekran", () => { _sh.ToggleFocus(); Refresh(); }, 15, 30);
            var pip = U.IconButton("\uE8A7", PipWindow.IsOpenFor(watched.UserId) ? "Zamknij okienko na wierzchu" : "Okienko na wierzchu (oglądaj nad innymi programami)", () => { if (_sh.FocusMode) _sh.ToggleFocus(); PipWindow.Toggle(_sh, watched.UserId); Refresh(); }, 15, 30);
            var tools = U.H(8, U.I(G.Speaker, 14, Theme.Mu), vol, pip, full, stop);
            tools.Padding = U.Th(10, 2, 6, 2);
            big.Add(new Border { Background = U.B("#99120c22"), CornerRadius = new CornerRadius(8), Padding = U.Th(10, 4), Margin = U.Th(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = who });
            big.Add(new Border { Background = U.B("#99120c22"), CornerRadius = new CornerRadius(10), Child = tools, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = U.Th(10) });
            _stage.Add(big.At(0));
            var strip = U.H(10);
            strip.Padding = U.Th(16, 0, 16, 8);
            foreach (var vm in members) strip.Add(Tile(vm, srv, 168, 104, vm.UserId == me));
            _stage.Add(new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled }.At(1));
        }
        else if (members.Count == 0)
        {
            _stage.Add(U.V(10, U.I(G.Voice, 44, Theme.Mu2), U.T("Na kanale nikogo nie ma. Wejdź pierwszy — reszta usłyszy dzwonek.", 14, null, Theme.Mu, wrap: true))
                .With(p => { p.HorizontalAlignment = HorizontalAlignment.Center; p.VerticalAlignment = VerticalAlignment.Center; p.MaxWidth = 360; }));
        }
        else if (ch?.IsStage == true) // 6.3: scena — mówcy, ręce w górze, widownia
            _stage.Add(StageView.Build(_sh, _cid, members, srv, (vm, tw, th) => Tile(vm, srv, tw, th, vm.UserId == me)));
        else
        {
            var n = members.Count;
            var (w, h) = n <= 2 ? (340d, 220d) : n <= 6 ? (260d, 170d) : (200d, 130d);
            var wrap = new WrapPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = U.Th(16) };
            foreach (var vm in members) { var t = Tile(vm, srv, w, h, vm.UserId == me); _gridTiles.Add(t); wrap.Children.Add(t); }
            _stage.Add(U.Scroll(wrap));
            Fit(_stage.ActualWidth, _stage.ActualHeight);
        }

        } // !boardShown

        // przyciski na dole
        if (Here)
        {
            var (mic, deaf) = VoiceUi.MuteButtons(_sh);
            foreach (var b in new[] { mic, deaf }) { b.Width = 44; b.Height = 44; b.Background = U.W(.1); b.CornerRadius = new CornerRadius(22); }
            var leave = new Button { Content = U.I(G.HangUp, 18, U.B("#ffffff")), Width = 56, Height = 44, CornerRadius = new CornerRadius(22), Background = Theme.Red2, BorderThickness = U.Th(0) };
            leave.Click += (_, _) => _ = C.LeaveVoice();
            ToolTipService.SetToolTip(leave, "Rozłącz");
            var settings = U.IconButton(G.Settings, "Ustawienia głosu", () => VoiceSettings.Show(_sh), 16, 44);
            settings.Background = U.W(.1); settings.CornerRadius = new CornerRadius(22);
            var sharing = C.Voice.ScreenSharing;
            var screen = U.IconButton(G.Screen, sharing ? "Zakończ transmisję" : "Udostępnij ekran", () => VoiceUi.ToggleScreen(_sh), 16, 44, sharing ? U.B("#ffffff") : null);
            screen.Background = sharing ? Theme.Red2 : U.W(.1); screen.CornerRadius = new CornerRadius(22);
            if (C.Offline || _sh.W.Screen is null) screen.Visibility = Visibility.Collapsed;
            var camOn = _sh.W.Cam?.Active == true;
            var cam = U.IconButton(G.Camera, camOn ? "Wyłącz kamerę" : "Włącz kamerę", () => VoiceUi.ToggleCamera(_sh), 16, 44, camOn ? U.B("#ffffff") : null);
            cam.Background = camOn ? U.B("#2fa66a") : U.W(.1); cam.CornerRadius = new CornerRadius(22);
            if (C.Offline || _sh.W.Cam is null) cam.Visibility = Visibility.Collapsed;
            var sb = VoiceUi.SoundboardButton(_sh, 44);
            sb.Background = U.W(.1); sb.CornerRadius = new CornerRadius(22);
            var watching = s.Watches.GetValueOrDefault(_cid) is { Radio: false };
            var tv = U.IconButton("\uE8B2", watching ? "Dodaj film do kolejki" : "Oglądajcie razem (YouTube)", () => WatchUi.AskVideo(_sh, watching), 16, 44, watching ? U.B("#ffffff") : null);
            tv.Background = watching ? U.B("#6b2d7c") : U.W(.1); tv.CornerRadius = new CornerRadius(22);
            if (C.Offline) tv.Visibility = Visibility.Collapsed;
            // 6.3: tablica (podświetlona, gdy ktoś ją ogląda) i przycisk sceny
            var bv = BoardPanel.Viewers.GetValueOrDefault(_cid)?.Count ?? 0;
            if (BoardPanel.Games.GetValueOrDefault(_cid) is { Phase: not "end" } && !boardShown) bv = Math.Max(bv, 1); // 6.2: trwają Kalambury
            var board = U.IconButton("\uEE56", boardShown ? "Zamknij tablicę" : bv > 0 ? $"Tablica — otwarta u {bv} {Format.Plural(bv, "osoby", "osób", "osób")}" : "Tablica — rysujcie razem", () => { BoardOpenFor = boardShown ? 0 : _cid; Refresh(); }, 16, 44, boardShown || bv > 0 ? U.B("#ffffff") : null);
            board.Background = boardShown ? U.B("#2f7f86") : bv > 0 ? U.B("#24585e") : U.W(.1); board.CornerRadius = new CornerRadius(22);
            if (C.Offline && !Options.Demo) board.Visibility = Visibility.Collapsed;
            var stageBtn = ch?.IsStage == true ? StageView.Button(_sh, _cid, members.FirstOrDefault(m => m.UserId == me)) : null;
            // 6.2: zmieniacz głosu i radio
            var fxBtn = VoiceFxUi.Button(_sh, 44);
            var radioOn = s.Watches.GetValueOrDefault(_cid) is { Radio: true };
            var radio = U.IconButton("\uE8D6", radioOn ? "Radio gra — dodaj utwory" : "Radio: muzyka dla wszystkich na kanale", () => RadioUi.Ask(_sh), 16, 44, radioOn ? U.B("#ffffff") : null);
            radio.Background = radioOn ? U.B("#8a3a5c") : U.W(.1); radio.CornerRadius = new CornerRadius(22);
            if (C.Offline && !Options.Demo) radio.Visibility = Visibility.Collapsed;
            var row = U.H(12, stageBtn, mic, deaf, cam, screen, fxBtn, sb, radio, tv, board, settings, leave).With(p => p.HorizontalAlignment = HorizontalAlignment.Center);
            _controls.Child = RadioUi.Bar(_sh, _cid) is { } rbar ? U.V(4, rbar, row) : row;
        }
        else
        {
            var join = U.Primary(C.VoiceChannelId != 0 ? "Przejdź na ten kanał" : "Dołącz do kanału", () => _ = C.JoinVoice(_cid), 42);
            join.HorizontalAlignment = HorizontalAlignment.Center;
            join.Width = 240;
            _controls.Child = join;
        }
        C.Voice.SetCamerasVisible(true);
    }

    VoiceTile Tile(VoiceMember vm, Server? srv, double w, double h, bool me)
    {
        var t = new VoiceTile(_sh, vm, srv, w, h, me);
        t.ContextFlyout = me ? null : VoiceUi.VolumeFlyout(_sh, vm.UserId, srv);
        t.Tapped += (_, _) => _sh.ShowProfile(vm.UserId, t);
        _tiles.Add(t);
        return t;
    }

    Image VideoImage(string key)
    {
        var img = new Image { Stretch = Stretch.Uniform };
        _video[key] = (img, null, 0);
        return img;
    }

    void Tick()
    {
        if (_disposed) return;
        foreach (var t in _tiles) t.Talking = C.IsSpeaking(t.Uid);
        if (!Here || C.Offline) return;
        // kamery w kafelkach
        foreach (var t in _tiles)
        {
            var cam = C.Voice.Feeds.FirstOrDefault(f => !f.Screen && f.UserId == t.Uid);
            var key = "cam:" + t.Uid;
            if (cam is null) { t.Video.Visibility = Visibility.Collapsed; _video.Remove(key); continue; }
            if (!_video.TryGetValue(key, out var ve) || !ReferenceEquals(ve.Img, t.Video)) _video[key] = (t.Video, null, 0); // po odświeżeniu kafelek jest nowy
            t.Video.Visibility = Visibility.Visible;
            Draw(key, cam);
        }
        foreach (var f in C.Voice.Feeds.Where(f => f.Screen)) if (_video.ContainsKey("screen:" + f.TrackSid)) Draw("screen:" + f.TrackSid, f);
    }

    void Draw(string key, VideoFeed feed)
    {
        var (img, bmp, seen) = _video[key];
        var b = bmp;
        var (mw, mh) = U.PixelSize(img);
        feed.TryCopy(ref seen, (px, w, h) =>
        {
            if (w <= 0 || h <= 0) return;
            if (b is null || b.PixelWidth != w || b.PixelHeight != h) { b = new WriteableBitmap(w, h); img.Source = b; }
            using (var st = b.PixelBuffer.AsStream()) st.Write(px[..Math.Min(px.Length, w * h * 4)]);
            b.Invalidate();
        }, mw, mh);
        _video[key] = (img, b, seen);
    }
}

/// <summary>Pasek rozmowy w DM / grupie: kto jest w rozmowie, dołącz / rozłącz.</summary>
public sealed class CallBar : Grid
{
    readonly Shell _sh;
    readonly long _cid;
    readonly List<(long Uid, Avatar Av)> _avs = [];
    readonly Border _b = new() { Background = U.B("#33120c22"), BorderBrush = Theme.Line, BorderThickness = U.Th(0, 0, 0, 1) };

    public CallBar(Shell sh, long cid)
    {
        _sh = sh; _cid = cid;
        Children.Add(_b);
        Refresh();
    }

    public void Refresh()
    {
        var c = _sh.C;
        var members = _sh.S.Voice.GetValueOrDefault(_cid) ?? [];
        var here = c.VoiceChannelId == _cid;
        _avs.Clear();
        if (members.Count == 0 && !here) { _b.Child = null; Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;
        var avs = U.H(4);
        foreach (var m in members.Take(8))
        {
            var av = new Avatar(40, ring: Theme.RingHex) { ShowDecor = true }.Set(_sh.S, c.Api, m.UserId, null);
            _avs.Add((m.UserId, av));
            avs.Add(new Border { Child = av, CornerRadius = new CornerRadius(22), BorderBrush = U.B(Theme.RingHex), BorderThickness = U.Th(2) });
        }
        var stateText = here ? U.T(VoiceUi.StateText(c), 13, U.Bold, VoiceUi.StateBrush(c)) : U.T("Rozmowa trwa — możesz dołączyć", 13, U.Bold);
        // 6.4: kłódka „Szyfrowane od końca do końca” (klik — kod bezpieczeństwa)
        var text = E2eeUi.Badge(_sh, _cid) is { } lk ? (FrameworkElement)U.V(2, stateText, lk.With(x => x.Margin = U.Th(-4, 0, 0, 0))) : stateText;
        text.VerticalAlignment = VerticalAlignment.Center;
        var right = U.H(8);
        if (here)
        {
            var (mic, deaf) = VoiceUi.MuteButtons(_sh);
            var leave = new Button { Content = U.I(G.HangUp, 16, U.B("#ffffff")), Width = 48, Height = 34, CornerRadius = new CornerRadius(17), Background = Theme.Red2, BorderThickness = U.Th(0) };
            leave.Click += (_, _) => _ = c.LeaveVoice();
            var camOn = _sh.W.Cam?.Active == true;
            var cam = U.IconButton(G.Camera, camOn ? "Wyłącz kamerę" : "Włącz kamerę", () => VoiceUi.ToggleCamera(_sh), 15, 32, camOn ? Theme.Green : null);
            var sharing = c.Voice.ScreenSharing;
            var screen = U.IconButton(G.Screen, sharing ? "Zakończ transmisję" : "Udostępnij ekran", () => VoiceUi.ToggleScreen(_sh), 15, 32, sharing ? Theme.Red : null);
            if (c.Offline || _sh.W.Cam is null) cam.Visibility = Visibility.Collapsed;
            if (c.Offline || _sh.W.Screen is null) screen.Visibility = Visibility.Collapsed;
            right.Add(mic, deaf, cam, screen, VoiceUi.SoundboardButton(_sh, 32), leave);
        }
        else
        {
            var join = new Button { Content = U.H(6, U.I(G.Phone, 15, U.B("#ffffff")), U.T("Dołącz", 13, U.Bold, U.B("#ffffff"))), Background = U.B("#2fa66a"), CornerRadius = new CornerRadius(17), Padding = U.Th(14, 6), BorderThickness = U.Th(0) };
            join.Click += (_, _) => _ = c.JoinVoice(_cid);
            right.Add(join);
        }
        _b.Padding = U.Th(16, 10);
        _b.Child = new Grid().Cols("auto", "*", "auto").Add(avs.At(0, 0), text.With(t => t.Margin = U.Th(16, 0, 8, 0)).At(0, 1), right.At(0, 2));
    }

    public void Tick() { foreach (var (uid, av) in _avs) av.Talking = _sh.C.IsSpeaking(uid); }
}

/// <summary>Ustawienia głosu: mikrofon, głośniki, czułość, „naciśnij, aby mówić”, redukcja szumów.</summary>
static class VoiceSettings
{
    public static async void Show(Shell sh)
    {
        var host = sh.W.Voice;
        var o = sh.C.Voice.Options;
        var inputs = AudioDevices.Inputs();
        var outputs = AudioDevices.Outputs();
        DropField Combo(List<AudioDevice> list, string cur, Action<string> set)
        {
            var cb = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var d in list) cb.Items.Add(d.Name);
            var i = list.FindIndex(d => d.Id == cur);
            cb.SelectedIndex = i < 0 ? 0 : i;
            cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { set(list[cb.SelectedIndex].Id); App.Prefs.Save(); host?.Restart(); } };
            return cb;
        }
        SliderField Vol(double v, Action<double> set)
        {
            var s = new SliderField { Minimum = 0, Maximum = 200, StepFrequency = 5, Value = Math.Round(v * 100) };
            s.ValueChanged += (_, e) => { set(e.NewValue / 100); App.Prefs.Save(); };
            return s;
        }
        var micBox = Combo(inputs, o.InputDevice, id => o.InputDevice = id);
        var outBox = Combo(outputs, o.OutputDevice, id => o.OutputDevice = id);
        var inVol = Vol(o.InputGain, v => o.InputGain = v);
        var outVol = Vol(o.OutputVolume, v => o.OutputVolume = v);

        var meter = new ProgressBar { Maximum = 100, Value = 0, Height = 8, CornerRadius = new CornerRadius(4), Foreground = Theme.Green };
        var thr = U.T("", 11.5, null, Theme.Mu);
        var mode = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add("Wykrywanie głosu (mówisz — słychać)");
        mode.Items.Add("Naciśnij, aby mówić");
        mode.SelectedIndex = o.Mode == "ptt" ? 1 : 0;
        var keyBtn = U.Ghost(o.PttKey > 0 ? "Klawisz: " + KeyName(o.PttKey) : "Ustaw klawisz", () => { }, 34);
        var waiting = false;
        keyBtn.Click += (_, _) => { waiting = true; ((TextBlock)keyBtn.Content).Text = "Naciśnij klawisz…"; };
        var pttRow = U.V(6, keyBtn, U.T("Klawisz działa także wtedy, gdy grasz w innym oknie.", 11.5, null, Theme.Mu2, wrap: true));
        pttRow.Visibility = o.Mode == "ptt" ? Visibility.Visible : Visibility.Collapsed;
        mode.SelectionChanged += (_, _) => { o.Mode = mode.SelectedIndex == 1 ? "ptt" : "vad"; pttRow.Visibility = o.Mode == "ptt" ? Visibility.Visible : Visibility.Collapsed; App.Prefs.Save(); };

        var auto = new SwitchField { Header = "Automatyczna czułość", IsOn = o.AutoSens };
        var sens = new SliderField { Minimum = -80, Maximum = -10, StepFrequency = 1, Value = o.Sens, IsEnabled = !o.AutoSens };
        auto.Toggled += (_, _) => { o.AutoSens = auto.IsOn; sens.IsEnabled = !auto.IsOn; App.Prefs.Save(); };
        sens.ValueChanged += (_, e) => { o.Sens = e.NewValue; App.Prefs.Save(); };
        SwitchField Sw(string header, bool on, Action<bool> set) { var t = new SwitchField { Header = header, IsOn = on }; t.Toggled += (_, _) => { set(t.IsOn); App.Prefs.Save(); }; return t; }

        var panel = U.V(10,
            U.T("Mikrofon", 12, U.Bold, Theme.Mu), micBox, U.T("Głośność mikrofonu", 11.5, null, Theme.Mu2), inVol,
            U.T("Głośniki / słuchawki", 12, U.Bold, Theme.Mu), outBox, U.T("Głośność rozmowy", 11.5, null, Theme.Mu2), outVol,
            U.HLine(.1),
            U.T("Tryb mikrofonu", 12, U.Bold, Theme.Mu), mode, pttRow,
            U.T("Test mikrofonu — powiedz coś", 12, U.Bold, Theme.Mu), meter, thr,
            auto, sens,
            U.HLine(.1),
            Sw("Redukcja szumów", o.NoiseSuppression, v => o.NoiseSuppression = v),
            Sw("Usuwanie echa", o.EchoCancellation, v => o.EchoCancellation = v),
            Sw("Automatyczna głośność mikrofonu", o.AutoGain, v => o.AutoGain = v));
        panel.Width = 420;
        var dlg = new AppDialog
        {
            Title = "Głos i dźwięk", Content = new ScrollViewer { Content = panel, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Gotowe", XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        dlg.PreviewKeyDown += (_, e) =>
        {
            if (!waiting) return;
            waiting = false;
            e.Handled = true;
            o.PttKey = (int)e.Key;
            ((TextBlock)keyBtn.Content).Text = "Klawisz: " + KeyName(o.PttKey);
            App.Prefs.Save();
        };
        var timer = sh.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(60);
        timer.Tick += (_, _) =>
        {
            var v = sh.C.Voice;
            meter.Value = Pcm.Meter(v.MicDb) * 100;
            meter.Foreground = v.MicDb > v.Threshold ? Theme.Green : Theme.Gray;
            thr.Text = o.Mode == "ptt" ? "W trybie „naciśnij, aby mówić” słychać Cię tylko z wciśniętym klawiszem." : $"Próg: {Math.Round(v.Threshold)} dB — zielony pasek = słychać Cię";
            if (host?.AudioError is { } err) thr.Text = err;
        };
        host?.SetTesting(true);
        timer.Start();
        await dlg.ShowAsync();
        timer.Stop();
        host?.SetTesting(false);
        App.Prefs.Save();
    }

    static string KeyName(int vk) => vk switch
    {
        0x20 => "Spacja", 0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt", 0x14 => "Caps Lock", 0x09 => "Tab", 0xC0 => "`",
        0x05 => "Boczny przycisk myszy 1", 0x06 => "Boczny przycisk myszy 2",
        _ => ((VirtualKey)vk).ToString(),
    };
}
