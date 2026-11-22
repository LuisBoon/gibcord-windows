using System.Globalization;
using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace GibCord.App.Ui;

/// <summary>
/// 6.2 Radio na kanale: ta sama sesja co wspólne oglądanie (watch_*), tylko z mode="radio". Odtwarzacz (WebView2 z YouTube
/// albo plikiem z czatu) trzyma okno główne, poza ekranem — muzyka gra dalej, gdy przechodzisz po kanałach i serwerach.
/// Wszyscy w rozmowie słuchają od razu; „Nie słucham” wyłącza radio tylko u mnie (do końca tej sesji radia).
/// </summary>
public sealed class RadioHost
{
    public static RadioHost? Current { get; private set; }
    readonly Shell _sh;
    readonly WebView2 _web = new() { Width = 240, Height = 240, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    readonly HashSet<string> _off = [];
    bool _initStarted, _ready;
    string? _loaded, _endedKey;
    double _vol = -1;
    public double Position { get; private set; }
    public double Duration { get; private set; }
    bool _localPlaying;
    /// <summary>Coś się zmieniło (tytuł, pozycja co ~1 s) — dla paska radia.</summary>
    public event Action? Changed;

    GibCordClient C => _sh.C;
    /// <summary>Radio w mojej rozmowie (null, gdy nie gra albo to zwykłe oglądanie).</summary>
    public WatchState? W => C.VoiceChannelId != 0 && _sh.S.Watches.TryGetValue(C.VoiceChannelId, out var w) && w.Radio ? w : null;
    public bool Listening => W is { } w && !_off.Contains(w.Session ?? "");

    readonly Action<StoreChange> _onStore;
    readonly Action _onVoice;
    bool _dead;

    public RadioHost(Shell sh, Panel root)
    {
        _sh = sh;
        Current?.Kill(); // okno przebudowane (motyw, inne konto) — stary odtwarzacz milknie
        Current = this;
        // poza oknem (przesunięty w lewo w górę), ale „widoczny” dla przeglądarki — YouTube gra tylko w widocznym odtwarzaczu
        _web.RenderTransform = new TranslateTransform { X = -2000, Y = -2000 };
        Canvas.SetZIndex(_web, -10);
        root.Children.Add(_web);
        _timer = sh.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _onStore = c => { if (c.Kind == ChangeKind.Watch) Sync(); };
        _onVoice = Sync;
        sh.S.Changed += _onStore;
        C.VoiceChanged += _onVoice;
    }

    void Kill()
    {
        if (_dead) return;
        _dead = true;
        _timer.Stop();
        _sh.S.Changed -= _onStore;
        C.VoiceChanged -= _onVoice;
        try { _web.Close(); } catch { }
        if (_web.Parent is Panel p) p.Children.Remove(_web);
    }

    public void SetListening(bool on)
    {
        if (W is not { } w) return;
        if (on) _off.Remove(w.Session ?? ""); else _off.Add(w.Session ?? "");
        Sync();
        _sh.RefreshVoice();
    }

    public static double Volume { get => Math.Clamp(App.Prefs.RadioVolume, 0, 1); set { App.Prefs.RadioVolume = Math.Clamp(value, 0, 1); App.Prefs.Save(); Current?.ApplyVolume(); } }

    async Task Init()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            var dir = Path.Combine(App.DataDir, "odtwarzacz");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "radio.html"), RadioHtml);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(App.DataDir, "webview"),
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" });
            await _web.EnsureCoreWebView2Async(env);
            if (_web.CoreWebView2 is not { } core) return;
            core.SetVirtualHostNameToFolderMapping("player.gibcord.local", dir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://player.gibcord.local/", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true; };
            core.WebMessageReceived += (_, e) => { if (e.Source.StartsWith("https://player.gibcord.local/", StringComparison.OrdinalIgnoreCase)) OnMessage(e.TryGetWebMessageAsString()); };
            core.Navigate("https://player.gibcord.local/radio.html");
        }
        catch (Exception ex)
        {
            App.Log("Radio: " + ex);
            _sh.Info("Radio nie działa — brakuje składnika Microsoft Edge WebView2 w Windowsie.", true);
        }
    }

    void OnMessage(string? json)
    {
        if (json is null) return;
        JsonElement m;
        try { m = JsonDocument.Parse(json).RootElement; } catch { return; }
        switch (m.Str("t"))
        {
            case "ready": _ready = true; _loaded = null; _vol = -1; Sync(); break;
            case "time":
                if (m.TryGetProperty("pos", out var p) && p.ValueKind == JsonValueKind.Number) Position = p.GetDouble();
                if (m.TryGetProperty("dur", out var d) && d.ValueKind == JsonValueKind.Number && d.GetDouble() > 0) Duration = d.GetDouble();
                _localPlaying = m.Bool("playing") == true;
                break;
            case "ended":
            case "error":
                if (W is { Key: { } k } && _endedKey != k)
                {
                    _endedKey = k;
                    if (m.Str("t") == "error") _sh.Info("YouTube nie pozwala odtworzyć tego utworu — przeskakuję dalej.", true);
                    C.Watch(new { t = "watch_ended", key = k }); // serwer przełącza tylko raz, nawet gdy wyśle kilka osób
                }
                break;
        }
    }

    Task Js(string code) => _ready && _web.CoreWebView2 is not null ? _web.ExecuteScriptAsync(code).AsTask() : Task.CompletedTask;
    static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

    void ApplyVolume()
    {
        var v = C.Voice.Options.Deafened ? 0 : Volume * Math.Clamp(C.Voice.Options.OutputVolume, 0, 1);
        if (Math.Abs(v - _vol) < .001) return;
        _vol = v;
        _ = Js($"vol({Num(v)})");
    }

    /// <summary>Doprowadza odtwarzacz do stanu z serwera (utwór, gra/pauza, pozycja z tolerancją 2 s).</summary>
    public void Sync()
    {
        if (_dead) return;
        var w = Listening ? W : null;
        if (w is null)
        {
            if (_loaded is not null) { _loaded = null; Position = Duration = 0; _ = Js("stop()"); }
            Changed?.Invoke();
            return;
        }
        if (!_initStarted) { _ = Init(); return; }
        if (!_ready) return;
        ApplyVolume();
        var target = w.Now(Format.NowMs());
        if (_loaded != w.Ident)
        {
            _loaded = w.Ident;
            Duration = 0;
            var src = w.Kind == "file" ? C.Api.Abs(w.Url)?.AbsoluteUri ?? "" : w.VideoId ?? "";
            _ = Js($"load({JsonSerializer.Serialize(w.Kind)}, {JsonSerializer.Serialize(src)}, {Num(target)}, {(w.Playing ? "true" : "false")})");
            Changed?.Invoke();
            return;
        }
        if (Math.Abs(Position - target) > 2 && w.Playing) _ = Js($"seek({Num(target)})");
        if (w.Playing && !_localPlaying) _ = Js("play()");
        else if (!w.Playing && _localPlaying) _ = Js("pause()");
        Changed?.Invoke();
    }

    void Tick()
    {
        if (_dead || W is null) return;
        if (Environment.TickCount64 % 2000 < 500) Sync(); // co ~2 s: czy nie odjechaliśmy od reszty
        else { ApplyVolume(); Changed?.Invoke(); }
    }

    const string RadioHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="strict-origin-when-cross-origin">
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}#yt,#vid{position:absolute;inset:0;width:100%;height:100%;border:0}</style>
</head><body><div id="yt"></div><video id="vid" playsinline></video>
<script>
const post = o => window.chrome.webview.postMessage(JSON.stringify(o));
let kind = null, yt = null, ytReady = false, pending = null, volume = 0.5;
const vid = document.getElementById('vid');
function tag(){ const s=document.createElement('script'); s.src='https://www.youtube.com/iframe_api'; document.head.appendChild(s); }
window.onYouTubeIframeAPIReady = () => { ytReady = true; if (pending) { const p = pending; pending = null; load(...p); } };
function state(){ if (kind==='yt' && yt && yt.getPlayerState) return { pos: yt.getCurrentTime()||0, dur: yt.getDuration()||0, playing: yt.getPlayerState()===1 }; if (kind==='file') return { pos: vid.currentTime||0, dur: vid.duration||0, playing: !vid.paused }; return { pos:0, dur:0, playing:false }; }
function vol(v){ volume = v; if (yt && yt.setVolume) yt.setVolume(Math.round(v*100)); vid.volume = Math.max(0, Math.min(1, v)); }
function load(k, src, start, playing){
  kind = k;
  if (k==='yt'){
    vid.pause();
    if (!ytReady){ pending=[k,src,start,playing]; return; }
    if (!yt){ yt = new YT.Player('yt',{ videoId: src, playerVars:{ autoplay: playing?1:0, start: Math.floor(start), controls:0, disablekb:1, rel:0, playsinline:1, iv_load_policy:3 },
      events:{ onReady: e => { e.target.setVolume(Math.round(volume*100)); e.target.seekTo(start,true); if (playing) e.target.playVideo(); else e.target.pauseVideo(); },
               onStateChange: e => { if (e.data===0) post({t:'ended'}); },
               onError: () => post({t:'error'}) } }); }
    else { if (playing) yt.loadVideoById({videoId:src,startSeconds:start}); else yt.cueVideoById({videoId:src,startSeconds:start}); yt.setVolume(Math.round(volume*100)); }
  } else {
    if (yt && yt.stopVideo) yt.stopVideo();
    vid.src = src; vid.currentTime = start; vid.volume = volume; if (playing) vid.play().catch(()=>{}); else vid.pause();
  }
}
function stop(){ kind = null; if (yt && yt.stopVideo) yt.stopVideo(); vid.pause(); vid.removeAttribute('src'); vid.load(); }
function play(){ if (kind==='yt' && yt && yt.playVideo) yt.playVideo(); else if (kind==='file') vid.play().catch(()=>{}); }
function pause(){ if (kind==='yt' && yt && yt.pauseVideo) yt.pauseVideo(); else if (kind==='file') vid.pause(); }
function seek(t){ if (kind==='yt' && yt && yt.seekTo) yt.seekTo(t,true); else if (kind==='file') vid.currentTime=t; }
vid.addEventListener('ended', () => post({t:'ended'}));
vid.addEventListener('error', () => { if (kind==='file') post({t:'error'}); });
setInterval(() => post(Object.assign({t:'time'}, state())), 500);
tag(); post({t:'ready'});
</script></body></html>
""";
}

/// <summary>Pasek radia w rozmowie, mały pasek w panelu rozmowy po lewej i okienko „dodaj utwory”.</summary>
static class RadioUi
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    static string Title(WatchState w) => w.Title ?? (w.Kind == "file" ? (w.Audio ? "Muzyka z czatu" : "Film z czatu") : "Utwór z YouTube");
    static string Title(WatchItem w) => w.Title ?? (w.Kind == "file" ? (w.Audio ? "Muzyka z czatu" : "Film z czatu") : "Utwór z YouTube");
    static string Fmt(double s) { if (double.IsNaN(s) || s < 0) s = 0; var t = TimeSpan.FromSeconds(s); return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}"; }

    /// <summary>Pasek radia nad przyciskami rozmowy (null, gdy radio nie gra).</summary>
    public static FrameworkElement? Bar(Shell sh, long cid)
    {
        var host = RadioHost.Current;
        if (host?.W is not { } w || sh.C.VoiceChannelId != cid) return null;
        var srv = sh.S.ServerOfChannel(cid);
        if (!host.Listening)
        {
            var row0 = new Grid { ColumnSpacing = 10 }.Cols("auto", "*", "auto").Add(
                U.T("📻", 18).With(t => t.VerticalAlignment = VerticalAlignment.Center).At(0, 0),
                U.V(0, U.T("Radio gra na kanale", 11.5, null, Theme.Mu), U.T(Title(w), 13, U.Semi).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis)).At(0, 1),
                U.Primary("Słuchaj", () => host.SetListening(true), 32).At(0, 2));
            return U.Box(row0, U.W(.06), 16, U.Th(12, 8), U.W(.08), 1).With(b => { b.MaxWidth = 520; b.HorizontalAlignment = HorizontalAlignment.Center; b.Margin = U.Th(16, 0, 16, 8); });
        }
        var title = U.T(w.Ended ? "Koniec kolejki — dodaj utwór" : Title(w), 13.5, U.Semi).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis);
        var by = sh.S.NameOf(w.AddedBy, srv);
        var sub = U.T($"dodał(a) {by}{(w.Queue.Count > 0 ? $" · dalej {w.Queue.Count}" : "")}", 11.5, null, Theme.Mu);
        var prog = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 4, MinHeight = 4, CornerRadius = new CornerRadius(2), Foreground = Theme.Accent, Background = U.W(.1) };
        var time = U.T("", 11, null, Theme.Mu2);
        void Upd()
        {
            var pos = w.Now(Format.NowMs()); var dur = host.Duration;
            prog.Value = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
            time.Text = dur > 0 ? $"{Fmt(pos)} / {Fmt(dur)}" : Fmt(pos);
        }
        Upd();
        Action upd = () => Upd();
        host.Changed += upd;
        var pp = U.IconButton(w.Playing ? "" : "", w.Playing ? "Pauza dla wszystkich" : "Graj dla wszystkich", () =>
        {
            var pos = w.Ended ? 0 : w.Now(Format.NowMs());
            sh.C.Watch(new { t = "watch_ctl", action = w.Playing ? "pause" : "play", position = Math.Round(pos, 2) });
        }, 15, 34);
        var mod = sh.S.Can(cid, Perm.VoiceModerate);
        var n = Math.Max(1, (int)Math.Ceiling((sh.S.Voice.GetValueOrDefault(cid)?.Count ?? 1) / 2.0));
        var mine = w.Skip.Contains(sh.S.Me.Id);
        var skip = U.IconButton("", w.AddedBy == sh.S.Me.Id || mod ? "Następny utwór" : mine ? $"Cofnij głos za pominięciem ({w.Skip.Count} z {n})" : $"Pomiń ({w.Skip.Count} z {n} głosów)", () => sh.C.Watch(new { t = "watch_skip" }), 15, 34, mine ? Theme.Accent : null);
        var vol = new SliderField { Minimum = 0, Maximum = 100, StepFrequency = 5, Width = 110, Value = Math.Round(RadioHost.Volume * 100), VerticalAlignment = VerticalAlignment.Center };
        vol.ValueChanged += (_, e) => RadioHost.Volume = e.NewValue / 100;
        ToolTipService.SetToolTip(vol, "Głośność radia (tylko u Ciebie)");
        var queue = U.IconButton("", "Kolejka", () => { }, 15, 34);
        queue.Flyout = new Flyout { Content = Queue(sh, cid, w) };
        var more = U.IconButton("", "Więcej", () => { }, 15, 34);
        var mf = new MenuFlyout();
        mf.Items.Add(M.Item("Dodaj utwory", "", () => Ask(sh)));
        mf.Items.Add(M.Item("Nie słucham (tylko u mnie)", "", () => { host.SetListening(false); sh.Info("Radio gra dalej u innych. Wrócisz przyciskiem „Słuchaj”."); }));
        mf.Items.Add(M.Item("Wyłącz radio dla wszystkich", "", () => sh.C.Watch(new { t = "watch_stop" })));
        more.Flyout = mf;
        var top = new Grid { ColumnSpacing = 10 }.Cols("auto", "*", "auto").Add(
            new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = U.C("#e4668f"), Offset = 0 }, new GradientStop { Color = U.C("#5a3b8e"), Offset = .6 }, new GradientStop { Color = U.C("#1f7680"), Offset = 1 } } }, Child = U.T("📻", 18).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }) }.At(0, 0),
            U.V(1, title, sub).With(p => p.VerticalAlignment = VerticalAlignment.Center).At(0, 1),
            time.With(t => t.VerticalAlignment = VerticalAlignment.Center).At(0, 2));
        var ctl = U.H(2, pp, skip, U.I(G.Speaker, 13, Theme.Mu).With(i => i.Margin = U.Th(8, 0, 4, 0)), vol, queue, more);
        var box = U.Box(U.V(6, top, prog, ctl), U.W(.06), 16, U.Th(12, 10), U.W(.08), 1);
        box.MaxWidth = 520; box.HorizontalAlignment = HorizontalAlignment.Center; box.Margin = U.Th(16, 0, 16, 8);
        box.Unloaded += (_, _) => host.Changed -= upd;
        return box;
    }

    static FrameworkElement Queue(Shell sh, long cid, WatchState w)
    {
        var p = new StackPanel { Spacing = 4, Width = 340 };
        p.Add(U.T(w.Queue.Count == 0 ? "Kolejka jest pusta." : $"Dalej ({w.Queue.Count})", 12.5, U.Bold, Theme.Mu));
        var mod = sh.S.Can(cid, Perm.VoiceModerate);
        var i = 0;
        foreach (var q in w.Queue)
        {
            var qid = q.Qid;
            var g = new Grid { ColumnSpacing = 8 }.Cols("auto", "*", "auto").Add(U.T($"{++i}", 12, null, Theme.Mu2).With(t => t.VerticalAlignment = VerticalAlignment.Center).At(0, 0),
                U.V(0, U.T(Title(q), 13, U.Semi).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis), U.T(sh.S.NameOf(q.By), 11, null, Theme.Mu2)).At(0, 1));
            if (q.By == sh.S.Me.Id || mod) g.Add(U.IconButton(G.Close, "Usuń z kolejki", () => sh.C.Watch(new { t = "watch_unqueue", qid }), 10, 26).At(0, 2));
            p.Add(g);
        }
        p.Add(U.Ghost("+ Dodaj utwory", () => Ask(sh), 30).With(b => b.HorizontalAlignment = HorizontalAlignment.Stretch));
        return new ScrollViewer { Content = p, MaxHeight = 380 };
    }

    /// <summary>Mały pasek w panelu rozmowy po lewej (gdy radio gra i słucham).</summary>
    public static FrameworkElement? Mini(Shell sh)
    {
        var host = RadioHost.Current;
        if (host?.W is not { } w || !host.Listening) return null;
        var title = U.T("📻 " + (w.Ended ? "Radio: koniec kolejki" : Title(w)), 12, U.Semi).With(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.VerticalAlignment = VerticalAlignment.Center; });
        var pp = U.IconButton(w.Playing ? "" : "", w.Playing ? "Pauza radia (dla wszystkich)" : "Graj", () => sh.C.Watch(new { t = "watch_ctl", action = w.Playing ? "pause" : "play", position = Math.Round(w.Ended ? 0 : w.Now(Format.NowMs()), 2) }), 12, 26);
        var sk = U.IconButton("", "Następny utwór / głos za pominięciem", () => sh.C.Watch(new { t = "watch_skip" }), 12, 26);
        var g = new Grid { ColumnSpacing = 2 }.Cols("*", "auto", "auto").Add(title.At(0, 0), pp.At(0, 1), sk.At(0, 2));
        title.Tip(Title(w));
        return new Border { Child = g, Padding = U.Th(12, 2, 6, 2), Background = U.Alpha("#e4668f", .1), BorderBrush = Theme.Line, BorderThickness = U.Th(0, 1, 0, 0) };
    }

    static async Task<string?> YtTitle(string id)
    {
        try
        {
            var j = await Http.GetStringAsync($"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString("https://www.youtube.com/watch?v=" + id)}");
            using var d = JsonDocument.Parse(j);
            return d.RootElement.Str("title");
        }
        catch { return null; }
    }

    /// <summary>Okienko: linki z YouTube (każdy w nowej linii) → radio (pierwszy rusza od razu, reszta do kolejki).</summary>
    public static async void Ask(Shell sh)
    {
        if (sh.C.VoiceChannelId == 0) { sh.Info("Radio działa w rozmowie — najpierw wejdź na kanał głosowy.", true); return; }
        var cur = sh.S.Watches.GetValueOrDefault(sh.C.VoiceChannelId);
        var radio = cur?.Radio == true;
        var box = new TextBox { PlaceholderText = "https://www.youtube.com/watch?v=…\nhttps://youtu.be/…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
        var text = radio ? "Wklej linki z YouTube, każdy w nowej linii. Trafią na koniec kolejki."
            : "Muzyka zagra wszystkim na tym kanale naraz, bez obrazu. Każdy ustawia sobie głośność radia, a utwory lecą po kolei." + (cur is not null ? " Teraz oglądacie film — radio go zastąpi." : "");
        var d = new AppDialog
        {
            Title = radio ? "Dodaj do radia" : "Radio na kanale",
            Content = U.V(8, U.T(text, 13, null, Theme.Mu, wrap: true), box, U.T("Muzykę wrzuconą na czat (MP3, OGG, M4A, WAV, FLAC) puścisz przyciskiem „Puść w radiu” pod plikiem.", 11.5, null, Theme.Mu2, wrap: true)).With(p => p.Width = 400),
            PrimaryButtonText = radio ? "Do kolejki" : "Włącz radio", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var ids = box.Text.Split(['\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(WatchUi.VideoIdOf).Where(x => x is not null).Select(x => x!).Distinct().Take(20).ToList();
        if (ids.Count == 0) { sh.Info("Nie widzę tu linku do YouTube.", true); return; }
        var titles = await Task.WhenAll(ids.Select(YtTitle));
        for (var i = 0; i < ids.Count; i++)
            sh.C.Watch(new { t = i == 0 && !radio ? "watch_set" : "watch_queue", mode = "radio", videoId = ids[i], title = titles[i] });
        sh.Info(radio ? $"Dodano do radia: {ids.Count} {Format.Plural(ids.Count, "utwór", "utwory", "utworów")}" : "Radio gra dla wszystkich na kanale");
    }

    /// <summary>„Puść w radiu” pod plikiem muzycznym (albo filmem) z czatu.</summary>
    public static void PlayFile(Shell sh, long uploadId)
    {
        var radio = sh.S.Watches.GetValueOrDefault(sh.C.VoiceChannelId)?.Radio == true;
        sh.C.Watch(new { t = radio ? "watch_queue" : "watch_set", mode = "radio", uploadId });
        sh.Info(radio ? "Dodano do radia" : "Radio gra dla wszystkich na kanale");
    }

    /// <summary>„Do radia” pod linkiem z YouTube.</summary>
    public static async void PlayYt(Shell sh, string id)
    {
        var radio = sh.S.Watches.GetValueOrDefault(sh.C.VoiceChannelId)?.Radio == true;
        var title = await YtTitle(id);
        sh.C.Watch(new { t = radio ? "watch_queue" : "watch_set", mode = "radio", videoId = id, title });
        sh.Info(radio ? "Dodano do radia" : "Radio gra dla wszystkich na kanale");
    }
}
