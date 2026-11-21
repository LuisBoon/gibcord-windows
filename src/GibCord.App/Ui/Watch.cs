using System.Text.Json;
using System.Text.RegularExpressions;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;

namespace GibCord.App.Ui;

/// <summary>
/// Wspólne oglądanie w rozmowie: odtwarzacz (WebView2 z YouTube albo filmem z czatu) zsynchronizowany ze stanem z serwera,
/// własne przyciski (gra/pauza, przewijanie, następny, kolejka, stop) i latające reakcje.
/// </summary>
public sealed partial class WatchPanel : Grid, IDisposable
{
    readonly Shell _sh;
    readonly long _cid;
    readonly WebView2 _web = new();
    readonly Grid _reactLayer = new() { IsHitTestVisible = false };
    readonly TextBlock _title = U.T("", 14, U.Bold);
    readonly TextBlock _time = U.T("0:00", 12, U.Semi, Theme.Mu);
    readonly SliderField _seek = new() { Minimum = 0, Maximum = 1, StepFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _play;
    readonly StackPanel _queue = new() { Spacing = 4 };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    bool _ready, _disposed, _seeking;
    string? _loaded;   // co jest wczytane w odtwarzaczu (Ident)
    double _duration, _local;
    bool _localPlaying;
    string? _endedKey;

    GibCordClient C => _sh.C;
    WatchState? W => _sh.S.Watches.GetValueOrDefault(_cid);

    public WatchPanel(Shell sh, long cid)
    {
        _sh = sh;
        _cid = cid;
        this.Rows("*", "auto");
        _play = U.IconButton("", "Graj / pauza", TogglePlay, 16, 40);
        _play.Background = U.W(.1);
        _play.CornerRadius = new CornerRadius(20);
        _seek.ValueChanged += (_, e) => { if (_seeking) _time.Text = $"{Fmt(e.NewValue)} / {Fmt(_duration)}"; };
        _seek.PointerCaptureLost += (_, _) => Seek();
        _seek.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _seeking = true), true);
        _seek.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Seek()), true);

        var player = new Grid { Background = U.B("#ff000000") }.Add(_web, _reactLayer);
        var playerBox = new Border { CornerRadius = new CornerRadius(14), Child = player, Margin = U.Th(16, 16, 16, 8) };
        var reacts = U.H(0);
        foreach (var r in GibCordClient.WatchReactions)
        {
            var emo = r;
            var b = new Button { Content = U.T(r, 16), Background = Theme.Transparent, BorderThickness = U.Th(0), Width = 32, Height = 34, Padding = U.Th(0), CornerRadius = new CornerRadius(16) };
            b.Click += (_, _) => C.Watch(new { t = "watch_react", emoji = emo });
            reacts.Add(b);
        }
        var queueBtn = U.IconButton("", "Kolejka filmów", () => { }, 15, 36);
        queueBtn.Flyout = new Flyout { Content = new ScrollViewer { Content = _queue, MaxHeight = 360, Width = 360 } };
        // dwa rzędy: tytuł i pasek czasu na całą szerokość, pod spodem przyciski (mieści się też w wąskim oknie)
        var bar = new Grid { ColumnSpacing = 10 }.Cols("auto", "*", "auto").Add(_play.At(0, 0), _seek.At(0, 1), _time.At(0, 2));
        var buttons = new Grid().Cols("*", "auto").Add(U.Scroll2(reacts).At(0, 0), U.H(2, queueBtn,
            U.IconButton("\uE893", "Następny film", () => C.Watch(new { t = "watch_skip" }), 15, 36),
            U.IconButton("\uE71A", "Zakończ wspólne oglądanie", () => C.Watch(new { t = "watch_stop" }), 15, 36, Theme.Red)).At(0, 1));
        var controls = U.V(4, _title, bar, buttons);
        controls.Padding = U.Th(16, 0, 16, 8);
        this.Add(playerBox.At(0), controls.At(1));

        C.WatchReacted += OnReact;
        _sh.S.Changed += OnStore;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        DrawControls(W);
        _ = Init();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        C.WatchReacted -= OnReact;
        _sh.S.Changed -= OnStore;
        try { _web.Close(); } catch { }
    }

    static string Fmt(double s) { if (double.IsNaN(s) || s < 0) s = 0; var t = TimeSpan.FromSeconds(s); return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}"; }

    void OnStore(StoreChange c) { if (c.Kind == ChangeKind.Watch && c.Id == _cid) Sync(); }

    // ───────────────────────── WebView2 ─────────────────────────

    async Task Init()
    {
        try
        {
            var dir = Path.Combine(App.DataDir, "odtwarzacz");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "player.html"), PlayerHtml);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(App.DataDir, "webview"),
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" });
            await _web.EnsureCoreWebView2Async(env);
            if (_disposed || _web.CoreWebView2 is not { } core) return; // panel zamknięty w trakcie startu odtwarzacza
            core.SetVirtualHostNameToFolderMapping("player.gibcord.local", dir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // nowe okna (np. „Obejrzyj na YouTube”): tylko https, w przeglądarce — żadnych innych protokołów (search-ms:, ms-… itd.)
            core.NewWindowRequested += (s2, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == "https") _ = Windows.System.Launcher.LaunchUriAsync(u);
            };
            // strona główna odtwarzacza zostaje na player.gibcord.local (ramki YouTube nawigują same w sobie)
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("https://player.gibcord.local/", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true;
            };
            core.WebMessageReceived += (_, e) =>
            {
                if (!e.Source.StartsWith("https://player.gibcord.local/", StringComparison.OrdinalIgnoreCase)) return; // tylko nasz odtwarzacz, nie ramki
                OnMessage(e.TryGetWebMessageAsString());
            };
            core.Navigate("https://player.gibcord.local/player.html");
        }
        catch (Exception ex)
        {
            App.Log("Wspólne oglądanie: " + ex);
            if (_disposed) return;
            _sh.Info("Odtwarzacz nie działa — brakuje składnika Microsoft Edge WebView2 w Windowsie.", true);
        }
    }

    void OnMessage(string? json)
    {
        if (json is null || _disposed) return;
        JsonElement m;
        try { m = JsonDocument.Parse(json).RootElement; } catch { return; }
        switch (m.Str("t"))
        {
            case "ready": _ready = true; _loaded = null; Sync(); break;
            case "time":
                _local = m.TryGetProperty("pos", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : _local;
                if (m.TryGetProperty("dur", out var d) && d.ValueKind == JsonValueKind.Number && d.GetDouble() > 0) _duration = d.GetDouble();
                _localPlaying = m.Bool("playing") == true;
                break;
            case "ended":
                if (W is { Key: { } k } w && _endedKey != k) { _endedKey = k; C.Watch(new { t = "watch_ended", key = k }); }
                break;
            case "error":
                _sh.Info("Tego filmu nie da się odtworzyć (twórca mógł zablokować osadzanie). Pomiń go przyciskiem „Następny”.", true);
                break;
        }
    }

    Task Js(string code) => _ready && !_disposed ? _web.ExecuteScriptAsync(code).AsTask() : Task.CompletedTask;

    /// <summary>Doprowadza odtwarzacz do stanu z serwera: film, gra/pauza, pozycja (tolerancja 2 s).</summary>
    void Sync()
    {
        if (_disposed) return;
        var w = W;
        DrawControls(w);
        if (w is null || !_ready) return;
        var target = w.Now(Format.NowMs());
        if (_loaded != w.Ident)
        {
            _loaded = w.Ident;
            _duration = 0;
            var src = w.Kind == "file" ? C.Api.Abs(w.Url)?.AbsoluteUri ?? "" : w.VideoId ?? "";
            _ = Js($"load({JsonSerializer.Serialize(w.Kind)}, {JsonSerializer.Serialize(src)}, {target.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(w.Playing ? "true" : "false")})");
            return;
        }
        if (Math.Abs(_local - target) > 2) _ = Js($"seek({target.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        if (w.Playing && !_localPlaying) _ = Js("play()");
        else if (!w.Playing && _localPlaying) _ = Js("pause()");
    }

    void Tick()
    {
        if (_disposed) return;
        var w = W;
        if (w is null) return;
        if (!_seeking)
        {
            var pos = w.Now(Format.NowMs());
            _seek.Maximum = Math.Max(1, _duration);
            _seek.Value = _duration > 0 ? Math.Min(_seek.Maximum, pos) : 0; // długość jeszcze nieznana — pasek pusty
            _time.Text = _duration > 0 ? $"{Fmt(pos)} / {Fmt(_duration)}" : Fmt(pos);
        }
        // co ~2 s sprawdzamy, czy nie odjechaliśmy od reszty
        if (Environment.TickCount64 % 2000 < 500) Sync();
    }

    void TogglePlay()
    {
        var w = W;
        if (w is null) return;
        var pos = w.Now(Format.NowMs());
        if (w.Ended) pos = 0;
        C.Watch(new { t = "watch_ctl", action = w.Playing ? "pause" : "play", position = Math.Round(pos, 2) });
    }

    void Seek()
    {
        if (!_seeking) return;
        _seeking = false;
        C.Watch(new { t = "watch_ctl", action = "seek", position = Math.Round(_seek.Value, 2) });
    }

    void DrawControls(WatchState? w)
    {
        if (w is null) return;
        ((FontIcon)_play.Content).Glyph = w.Playing ? "" : "";
        var by = _sh.S.NameOf(w.AddedBy, _sh.S.ServerOfChannel(_cid));
        _title.Text = (w.Ended ? "Koniec kolejki · " : "") + (w.Title ?? (w.Kind == "file" ? "Film z czatu" : "Film z YouTube")) + " · dodał(a) " + by
            + (w.Skip.Count > 0 ? $" · pominąć chce {w.Skip.Count}" : "");
        _queue.Children.Clear();
        _queue.Add(U.T(w.Queue.Count == 0 ? "Kolejka jest pusta." : $"Następne ({w.Queue.Count})", 12.5, U.Bold, Theme.Mu));
        foreach (var q in w.Queue)
        {
            var qid = q.Qid;
            var g = new Grid().Cols("*", "auto").Add(U.V(0, U.T(q.Title ?? q.VideoId ?? "Film", 13, U.Semi, wrap: true), U.T("dodał(a) " + _sh.S.NameOf(q.By), 11, null, Theme.Mu2)).At(0, 0));
            if (q.By == _sh.S.Me.Id || _sh.S.Can(_cid, Perm.VoiceModerate))
                g.Add(U.IconButton(G.Close, "Usuń z kolejki", () => C.Watch(new { t = "watch_unqueue", qid }), 10, 26).At(0, 1));
            _queue.Add(g);
        }
        _queue.Add(U.Ghost("+ Dodaj film z YouTube", () => WatchUi.AskVideo(_sh, queue: true), 30).With(b => b.HorizontalAlignment = HorizontalAlignment.Stretch));
    }

    void OnReact(long cid, long uid, string emoji)
    {
        if (cid != _cid || _disposed) return;
        var t = U.T(emoji, 34);
        t.HorizontalAlignment = HorizontalAlignment.Left;
        t.VerticalAlignment = VerticalAlignment.Bottom;
        var x = 40 + Random.Shared.NextDouble() * Math.Max(100, ActualWidth - 140);
        var tr = new TranslateTransform { X = x, Y = 0 };
        t.RenderTransform = tr;
        _reactLayer.Children.Add(t);
        var sb = new Storyboard();
        var up = new DoubleAnimation { From = 0, To = -Math.Max(160, ActualHeight * .6), Duration = TimeSpan.FromSeconds(2.4) };
        Storyboard.SetTarget(up, tr); Storyboard.SetTargetProperty(up, "Y");
        var fade = new DoubleAnimation { From = 1, To = 0, BeginTime = TimeSpan.FromSeconds(1.4), Duration = TimeSpan.FromSeconds(1) };
        Storyboard.SetTarget(fade, t); Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(up); sb.Children.Add(fade);
        sb.Completed += (_, _) => _reactLayer.Children.Remove(t);
        sb.Begin();
    }

    // Strona odtwarzacza: YouTube IFrame API albo <video>. Komunikaty do aplikacji przez chrome.webview.postMessage.
    const string PlayerHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="strict-origin-when-cross-origin">
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}#yt,#vid{position:absolute;inset:0;width:100%;height:100%;border:0;display:none}video{object-fit:contain;background:#000}</style>
</head><body><div id="yt"></div><video id="vid" playsinline></video>
<script>
const post = o => window.chrome.webview.postMessage(JSON.stringify(o));
let kind = null, yt = null, ytReady = false, pending = null;
const vid = document.getElementById('vid');
function tag(){ const s=document.createElement('script'); s.src='https://www.youtube.com/iframe_api'; document.head.appendChild(s); }
window.onYouTubeIframeAPIReady = () => { ytReady = true; if (pending) { const p = pending; pending = null; load(...p); } };
function state(){ if (kind==='yt' && yt && yt.getPlayerState) return { pos: yt.getCurrentTime()||0, dur: yt.getDuration()||0, playing: yt.getPlayerState()===1 }; if (kind==='file') return { pos: vid.currentTime||0, dur: vid.duration||0, playing: !vid.paused }; return { pos:0, dur:0, playing:false }; }
function load(k, src, start, playing){
  kind = k;
  if (k==='yt'){
    vid.pause(); vid.style.display='none'; document.getElementById('yt').style.display='block';
    if (!ytReady){ pending=[k,src,start,playing]; return; }
    if (!yt){ yt = new YT.Player('yt',{ videoId: src, playerVars:{ autoplay: playing?1:0, start: Math.floor(start), controls:0, disablekb:1, modestbranding:1, rel:0, playsinline:1, iv_load_policy:3 },
      events:{ onReady: e => { e.target.seekTo(start,true); if (playing) e.target.playVideo(); else e.target.pauseVideo(); },
               onStateChange: e => { if (e.data===0) post({t:'ended'}); },
               onError: () => post({t:'error'}) } }); }
    else { if (playing) yt.loadVideoById({videoId:src,startSeconds:start}); else yt.cueVideoById({videoId:src,startSeconds:start}); }
  } else {
    if (yt && yt.stopVideo) yt.stopVideo(); document.getElementById('yt').style.display='none'; vid.style.display='block';
    vid.src = src; vid.currentTime = start; if (playing) vid.play().catch(()=>{}); else vid.pause();
  }
}
function play(){ if (kind==='yt' && yt && yt.playVideo) yt.playVideo(); else if (kind==='file') vid.play().catch(()=>{}); }
function pause(){ if (kind==='yt' && yt && yt.pauseVideo) yt.pauseVideo(); else if (kind==='file') vid.pause(); }
function seek(t){ if (kind==='yt' && yt && yt.seekTo) yt.seekTo(t,true); else if (kind==='file') vid.currentTime=t; }
vid.addEventListener('ended', () => post({t:'ended'}));
vid.addEventListener('error', () => post({t:'error'}));
setInterval(() => post(Object.assign({t:'time'}, state())), 500);
tag(); post({t:'ready'});
</script></body></html>
""";
}

/// <summary>Start wspólnego oglądania: link z YouTube (albo film z czatu), od razu albo do kolejki.</summary>
static partial class WatchUi
{
    [GeneratedRegex(@"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|live/|embed/)|youtu\.be/)([A-Za-z0-9_-]{11})")]
    private static partial Regex YtRe();

    public static string? VideoIdOf(string text)
    {
        text = text.Trim();
        if (Regex.IsMatch(text, "^[A-Za-z0-9_-]{11}$")) return text;
        var m = YtRe().Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static async void AskVideo(Shell sh, bool queue)
    {
        if (sh.C.VoiceChannelId == 0) { sh.Info("Wspólne oglądanie działa w rozmowie — najpierw wejdź na kanał głosowy.", true); return; }
        var box = new TextBox { PlaceholderText = "Wklej link z YouTube" };
        var title = new TextBox { PlaceholderText = "Tytuł (opcjonalnie)", MaxLength = 200 };
        var d = new AppDialog
        {
            Title = queue ? "Dodaj do kolejki" : "Oglądajcie razem", Content = U.V(8, U.T("Wszyscy w rozmowie zobaczą ten sam film w tym samym momencie.", 13, null, Theme.Mu, wrap: true), box, title).With(p => p.Width = 380),
            PrimaryButtonText = queue ? "Dodaj" : "Oglądaj", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var id = VideoIdOf(box.Text);
        if (id is null) { sh.Info("To nie wygląda na link do filmu z YouTube.", true); return; }
        Start(sh, id, string.IsNullOrWhiteSpace(title.Text) ? null : title.Text.Trim(), queue || sh.S.Watches.ContainsKey(sh.C.VoiceChannelId));
    }

    public static void Start(Shell sh, string videoId, string? title, bool queue) =>
        sh.C.Watch(new { t = queue ? "watch_queue" : "watch_set", videoId, title });

    public static void StartFile(Shell sh, long uploadId, bool queue) =>
        sh.C.Watch(new { t = queue ? "watch_queue" : "watch_set", uploadId });
}
