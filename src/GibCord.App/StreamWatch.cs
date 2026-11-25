using GibCord.Core;
using GibCord.Core.Voice;
using Microsoft.UI.Dispatching;

namespace GibCord.App;

/// <summary>
/// 25.11.2026 czujnik cięcia transmisji w GibCordzie 6 (tak samo jak na stronie): co 2 s statystyki ekranu, który nadaję,
/// i ekranów, które oglądam. Gdy mój obraz zwalnia przez procesor albo łącze — podpowiedź z przyciskiem (720p / 30 kl/s).
/// Co 30 s krótkie podsumowanie na serwer (POST /api/diag/stream), żeby przy „tnie” było widać przyczynę.
/// Działa na wątku w tle (statystyki z biblioteki głosu), okno tylko pokazuje podpowiedź.
/// </summary>
static class StreamWatch
{
    static GibCordClient? _c;
    static ScreenShare? _screen;
    static DispatcherQueue? _dq;
    static Timer? _timer;
    static int _busy;
    static long _hintAt;
    static readonly Dictionary<string, StreamProbe> Probes = [];

    public static void Init(GibCordClient c, ScreenShare? screen, DispatcherQueue dq)
    {
        _c = c; _screen = screen; _dq = dq;
        _timer ??= new Timer(_ => _ = Tick(), null, 2000, 2000);
    }

    static async Task Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var c = _c; if (c is null) return;
            var v = c.Voice;
            var live = new HashSet<string>();
            if (v.State == VoiceState.Connected)
            {
                if (v.ScreenSharing) { live.Add("send"); await One(c, "send", null).ConfigureAwait(false); }
                foreach (var uid in v.Watching) { var k = "recv:" + uid; live.Add(k); await One(c, k, uid).ConfigureAwait(false); }
            }
            foreach (var k in Probes.Keys.Where(k => !live.Contains(k)).ToList())
            {
                Report(c, Probes[k].Flush(true, Extra));
                Probes.Remove(k);
            }
        }
        catch (Exception ex) { App.Log("Czujnik transmisji: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    static async Task One(GibCordClient c, string key, long? uid)
    {
        var stats = await c.Voice.ScreenStats(uid).ConfigureAwait(false);
        if (stats is null) return;
        var send = uid is null;
        var s = StreamProbe.Read(stats, send);
        if (s is null) return;
        var now = Environment.TickCount64;
        if (!Probes.TryGetValue(key, out var pr)) Probes[key] = pr = new StreamProbe(send, uid, now);
        var d = pr.Add(s, now, Extra, out var rep);
        if (rep is not null) Report(c, rep);
        if (send && d is not null) Hint(pr, now);
    }

    static Dictionary<string, object?> Extra()
    {
        var sc = _screen;
        return new()
        {
            ["app"] = "windows", ["version"] = Updater.Version, ["e2ee"] = _c?.Voice.Encrypted,
            ["res"] = sc?.Active == true ? sc.TargetHeight + "p" : null, ["target"] = sc?.Active == true ? sc.Fps : null,
        };
    }

    static void Report(GibCordClient c, Dictionary<string, object?>? d)
    {
        if (d is null || Options.Demo) return;
        _ = Task.Run(async () => { try { await c.Api.PostJson("diag/stream", d).ConfigureAwait(false); } catch { } });
    }

    static void Hint(StreamProbe pr, long now)
    {
        var sc = _screen;
        if (sc is null || !sc.Active) return;
        if (now - pr.Since < 15_000 || now - _hintAt < 120_000) return; // bez pierwszych 15 s (łącze się rozpędza), najwyżej co 2 min
        var tr = StreamProbe.Trouble(pr.Window, sc.Fps, sc.TargetHeight);
        if (tr is null) return;
        _hintAt = now;
        pr.MarkHint(tr.Reason);
        var lower = sc.LowerOption;
        var cpu = tr.Reason == "cpu";
        var title = cpu ? "Obraz zwalnia: procesor nie nadąża" : "Obraz zwalnia: łącze nie nadąża";
        var sub = tr.Slow ? $"Oglądający widzą ok. {tr.Fps} kl/s zamiast {sc.Fps}." : $"Obraz u oglądających jest mniej ostry ({tr.H}p).";
        if (lower is null) sub += cpu ? " Zamknij programy, których teraz nie potrzebujesz." : " Sprawdź, czy nikt w domu nie pobiera dużych plików.";
        var action = lower switch { "720p" => "Przełącz na 720p", "30fps" => "Przełącz na 30 kl/s", _ => null };
        App.Log($"Czujnik transmisji: {tr.Reason}, {tr.Fps} kl/s, {tr.H}p");
        _dq?.TryEnqueue(() => App.Window?.CurrentShell?.StreamHint(title, sub, action, action is null ? null : () =>
        {
            var ok = sc.Lower();
            App.Window?.CurrentShell?.Info(ok ? (lower == "720p" ? "Transmisja: 720p" : "Transmisja: 30 kl/s") : "Nie udało się zmienić jakości transmisji", !ok);
        }));
    }
}
