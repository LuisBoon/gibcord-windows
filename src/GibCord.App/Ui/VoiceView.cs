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
        var text = U.V(0, state, where);
        var go = new Border { Child = new Grid().Cols("auto", "*").Add(U.I(G.Signal, 16, StateBrush(c)).With(i => i.Margin = U.Th(2, 0, 8, 0)).At(0, 0), text.At(0, 1)), Background = Theme.Transparent, CornerRadius = new CornerRadius(8), Padding = U.Th(4, 4) };
        go.Tip("Przejdź do rozmowy");
        go.PointerEntered += (_, _) => go.Background = Theme.Hover;
        go.PointerExited += (_, _) => go.Background = Theme.Transparent;
        go.Tapped += (_, _) => sh.OpenChannel(c.VoiceChannelId);
        var leave = U.IconButton(G.HangUp, "Rozłącz", () => _ = c.LeaveVoice(), 15, 32, Theme.Red);
        var right = U.H(0);
        if (!c.Offline && sh.W.Screen is not null)
            right.Add(U.IconButton(G.Screen, c.Voice.ScreenSharing ? "Zakończ transmisję" : "Udostępnij ekran", () => ToggleScreen(sh), 15, 32, c.Voice.ScreenSharing ? Theme.Red : null));
        right.Add(leave);
        var row = new Grid().Cols("*", "auto").Add(go.At(0, 0), right.At(0, 1));
        var box = new Border { Child = row, Padding = U.Th(8, 6, 6, 6), Background = Theme.Rail, BorderBrush = Theme.Line, BorderThickness = U.Th(0, 1, 0, 0) };
        if (c.Voice.ScreenSharing)
            return U.V(0, box, new Border { Background = U.Alpha("#ff4d6a", .18), Padding = U.Th(12, 4), Child = U.T("Nadajesz ekran", 11.5, U.Bold, Theme.Red) });
        return box;
    }

    /// <summary>Start / koniec transmisji ekranu (systemowe okienko wyboru ekranu albo okna).</summary>
    public static async void ToggleScreen(Shell sh)
    {
        var ss = sh.W.Screen;
        if (ss is null) return;
        if (ss.Active || sh.C.Voice.ScreenSharing) { ss.Stop(); sh.RefreshVoice(); return; }
        var err = await ss.Start(sh.W.Hwnd);
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
        var slider = new Slider { Minimum = 0, Maximum = 200, StepFrequency = 5, Value = Math.Round(o.VolumeOf(uid) * 100), Width = 220 };
        slider.ValueChanged += (_, e) =>
        {
            o.UserVolume[uid] = e.NewValue / 100.0;
            if (Math.Abs(e.NewValue - 100) < 1) o.UserVolume.Remove(uid);
            label.Text = $"Głośność: {Math.Round(e.NewValue)}%";
            App.Prefs.Save();
        };
        var mute = new ToggleSwitch { Header = "Wycisz tylko u mnie", IsOn = o.UserMuted.Contains(uid) };
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
        return new Border { Child = card, Background = U.B("#FA2a2147"), CornerRadius = new CornerRadius(18), Padding = U.Th(36, 28), BorderBrush = U.W(.12), BorderThickness = U.Th(1), Width = 360 };
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
        Av = new Avatar(80).Set(sh.S, sh.C.Api, vm.UserId, srv);
        // awatar skaluje się razem z kafelkiem (kafelki dopasowują się do szerokości okna)
        var avBox = new Viewbox { Child = Av, Stretch = Stretch.Uniform, Height = Math.Min(80, h * .45), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        SizeChanged += (_, e) => avBox.Height = Math.Max(28, Math.Min(80, e.NewSize.Height * .45));
        _frame = new Border { CornerRadius = new CornerRadius(14), Background = U.W(.06), BorderBrush = U.W(.08), BorderThickness = U.Th(2) };
        var icons = U.H(4);
        if (vm.Muted || vm.ServerMuted || vm.Suppressed) icons.Add(U.I(G.MicOff, 13, Theme.Red));
        if (vm.Deafened || vm.ServerDeafened) icons.Add(U.I(G.SpeakerOff, 13, Theme.Red));
        var name = U.T(sh.S.NameOf(vm.UserId, srv) + (me ? " (Ty)" : ""), 12.5, U.Semi);
        var label = new Border { Background = U.B("#99120c22"), CornerRadius = new CornerRadius(8), Padding = U.Th(8, 3), Margin = U.Th(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = U.H(6, name, icons) };
        var center = new Grid().Add(avBox);
        this.Add(_frame, center, new Border { CornerRadius = new CornerRadius(12), Margin = U.Th(2), Child = Video }, label);
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

    public VoiceView(Shell sh, long cid)
    {
        _sh = sh;
        _cid = cid;
        this.Rows("48", "*", "auto");
        this.Add(_header.At(0), _stage.At(1), _controls.At(2));
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(66);
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        _stage.SizeChanged += (_, e) => Fit(e.NewSize.Width, e.NewSize.Height);
        Refresh();
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
        _tick.Stop();
    }

    public void Refresh()
    {
        if (_disposed) return;
        var s = _sh.S;
        var srv = s.ServerOfChannel(_cid);
        var ch = s.ChannelById(_cid);
        var members = s.Voice.GetValueOrDefault(_cid) ?? [];

        var head = new Grid().Cols("auto", "auto", "*", "auto");
        head.Add(U.I(G.Voice, 17, Theme.Mu).With(i => i.Margin = U.Th(0, 0, 10, 0)).At(0, 0), U.T(ch?.Name ?? "Rozmowa", 15, U.Bold).At(0, 1),
            U.T(members.Count == 0 ? "nikogo tu nie ma" : $"{members.Count} {Format.Plural(members.Count, "osoba", "osoby", "osób")}", 12.5, null, Theme.Mu).With(t => t.Margin = U.Th(12, 0, 0, 0)).At(0, 2));
        if (Here) head.Add(U.T(VoiceUi.StateText(C), 12.5, U.Bold, VoiceUi.StateBrush(C)).At(0, 3));
        _header.Child = head;

        // scena: oglądana transmisja na dużo + kafelki
        _stage.Children.Clear();
        _stage.RowDefinitions.Clear();
        _tiles.Clear();
        _gridTiles.Clear();
        var watched = Here ? C.Voice.Feeds.FirstOrDefault(f => f.Screen) : null;
        var me = s.Me.Id;
        if (watched is not null)
        {
            _stage.Rows("*", "auto");
            var img = VideoImage("screen:" + watched.TrackSid);
            var big = new Grid { Margin = U.Th(16, 16, 16, 8) };
            big.Add(new Border { Background = U.B("#cc08040f"), CornerRadius = new CornerRadius(14), Child = img });
            var who = U.T($"{s.NameOf(watched.UserId, srv)} nadaje ekran", 12.5, U.Semi);
            var stop = U.Ghost("Przestań oglądać", () => { C.Voice.Unwatch(watched.UserId); Refresh(); }, 30);
            big.Add(new Border { Background = U.B("#99120c22"), CornerRadius = new CornerRadius(8), Padding = U.Th(10, 4), Margin = U.Th(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = who });
            big.Add(stop.With(b => { b.HorizontalAlignment = HorizontalAlignment.Right; b.VerticalAlignment = VerticalAlignment.Top; b.Margin = U.Th(10); }));
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
        else
        {
            var n = members.Count;
            var (w, h) = n <= 2 ? (340d, 220d) : n <= 6 ? (260d, 170d) : (200d, 130d);
            var wrap = new WrapPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = U.Th(16) };
            foreach (var vm in members) { var t = Tile(vm, srv, w, h, vm.UserId == me); _gridTiles.Add(t); wrap.Children.Add(t); }
            _stage.Add(U.Scroll(wrap));
            Fit(_stage.ActualWidth, _stage.ActualHeight);
        }

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
            _controls.Child = U.H(12, mic, deaf, screen, settings, leave).With(p => p.HorizontalAlignment = HorizontalAlignment.Center);
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
            if (!_video.ContainsKey(key)) _video[key] = (t.Video, null, 0);
            t.Video.Visibility = Visibility.Visible;
            Draw(key, cam);
        }
        foreach (var f in C.Voice.Feeds.Where(f => f.Screen)) if (_video.ContainsKey("screen:" + f.TrackSid)) Draw("screen:" + f.TrackSid, f);
    }

    void Draw(string key, VideoFeed feed)
    {
        var (img, bmp, seen) = _video[key];
        var b = bmp;
        feed.TryCopy(ref seen, (px, w, h) =>
        {
            if (w <= 0 || h <= 0) return;
            if (b is null || b.PixelWidth != w || b.PixelHeight != h) { b = new WriteableBitmap(w, h); img.Source = b; }
            using (var st = b.PixelBuffer.AsStream()) st.Write(px[..Math.Min(px.Length, w * h * 4)]);
            b.Invalidate();
        });
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
            var av = new Avatar(40, ring: "#2a2140").Set(_sh.S, c.Api, m.UserId, null);
            _avs.Add((m.UserId, av));
            avs.Add(new Border { Child = av, CornerRadius = new CornerRadius(22), BorderBrush = U.B("#2a2140"), BorderThickness = U.Th(2) });
        }
        var text = here ? U.T(VoiceUi.StateText(c), 13, U.Bold, VoiceUi.StateBrush(c)) : U.T("Rozmowa trwa — możesz dołączyć", 13, U.Bold);
        var right = U.H(8);
        if (here)
        {
            var (mic, deaf) = VoiceUi.MuteButtons(_sh);
            var leave = new Button { Content = U.I(G.HangUp, 16, U.B("#ffffff")), Width = 48, Height = 34, CornerRadius = new CornerRadius(17), Background = Theme.Red2, BorderThickness = U.Th(0) };
            leave.Click += (_, _) => _ = c.LeaveVoice();
            right.Add(mic, deaf, leave);
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
        ComboBox Combo(List<AudioDevice> list, string cur, Action<string> set)
        {
            var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var d in list) cb.Items.Add(d.Name);
            var i = list.FindIndex(d => d.Id == cur);
            cb.SelectedIndex = i < 0 ? 0 : i;
            cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { set(list[cb.SelectedIndex].Id); App.Prefs.Save(); host?.Restart(); } };
            return cb;
        }
        Slider Vol(double v, Action<double> set)
        {
            var s = new Slider { Minimum = 0, Maximum = 200, StepFrequency = 5, Value = Math.Round(v * 100) };
            s.ValueChanged += (_, e) => { set(e.NewValue / 100); App.Prefs.Save(); };
            return s;
        }
        var micBox = Combo(inputs, o.InputDevice, id => o.InputDevice = id);
        var outBox = Combo(outputs, o.OutputDevice, id => o.OutputDevice = id);
        var inVol = Vol(o.InputGain, v => o.InputGain = v);
        var outVol = Vol(o.OutputVolume, v => o.OutputVolume = v);

        var meter = new ProgressBar { Maximum = 100, Value = 0, Height = 8, CornerRadius = new CornerRadius(4), Foreground = Theme.Green };
        var thr = U.T("", 11.5, null, Theme.Mu);
        var mode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add("Wykrywanie głosu (mówisz — słychać)");
        mode.Items.Add("Naciśnij, aby mówić");
        mode.SelectedIndex = o.Mode == "ptt" ? 1 : 0;
        var keyBtn = U.Ghost(o.PttKey > 0 ? "Klawisz: " + KeyName(o.PttKey) : "Ustaw klawisz", () => { }, 34);
        var waiting = false;
        keyBtn.Click += (_, _) => { waiting = true; ((TextBlock)keyBtn.Content).Text = "Naciśnij klawisz…"; };
        var pttRow = U.V(6, keyBtn, U.T("Klawisz działa także wtedy, gdy grasz w innym oknie.", 11.5, null, Theme.Mu2, wrap: true));
        pttRow.Visibility = o.Mode == "ptt" ? Visibility.Visible : Visibility.Collapsed;
        mode.SelectionChanged += (_, _) => { o.Mode = mode.SelectedIndex == 1 ? "ptt" : "vad"; pttRow.Visibility = o.Mode == "ptt" ? Visibility.Visible : Visibility.Collapsed; App.Prefs.Save(); };

        var auto = new ToggleSwitch { Header = "Automatyczna czułość", IsOn = o.AutoSens };
        var sens = new Slider { Minimum = -80, Maximum = -10, StepFrequency = 1, Value = o.Sens, IsEnabled = !o.AutoSens };
        auto.Toggled += (_, _) => { o.AutoSens = auto.IsOn; sens.IsEnabled = !auto.IsOn; App.Prefs.Save(); };
        sens.ValueChanged += (_, e) => { o.Sens = e.NewValue; App.Prefs.Save(); };
        ToggleSwitch Sw(string header, bool on, Action<bool> set) { var t = new ToggleSwitch { Header = header, IsOn = on }; t.Toggled += (_, _) => { set(t.IsOn); App.Prefs.Save(); }; return t; }

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
        var dlg = new ContentDialog
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
