using GibCord.App.Ui;
using GibCord.Core;
using Microsoft.UI.Dispatching;

namespace GibCord.App;

/// <summary>
/// 6.2 klipy: kiedy nagrywać w tle (ustawienie: wyłączone / tylko w grze / zawsze cały ekran), skrót Alt+F9 i co po zapisie
/// (dźwięk, dymek w nakładce gry, powiadomienie Windows z „Przytnij i wyślij”).
/// </summary>
public sealed class ClipCtl : IDisposable
{
    public static ClipCtl? Instance { get; private set; }
    readonly MainWindow _w;
    readonly GibCordClient _c;
    readonly Presence _p;
    readonly DispatcherQueueTimer _t;
    ClipRecorder? _rec;
    string _key = "";
    long _failedAt;

    public ClipRecorder? Recorder => _rec;
    public event Action? Changed;

    public ClipCtl(MainWindow w, GibCordClient c, Presence p)
    {
        _w = w; _c = c; _p = p;
        Instance = this;
        p.Changed += () => w.Dq.TryEnqueue(Sync);
        _t = w.Dq.CreateTimer();
        _t.Interval = TimeSpan.FromSeconds(5); // okno gry pojawia się czasem później niż proces
        _t.Tick += (_, _) => Sync();
        _t.Start();
    }

    public static bool Supported => ScreenShare.Supported;

    public void Sync()
    {
        try
        {
            var mode = App.Prefs.ClipMode;
            var g = _p.Current;
            var key = mode switch { "game" when g is not null => "g:" + g.Exe, "always" => "screen", _ => "" };
            if (key == _key && (key.Length == 0 || _rec?.Running == true)) return;
            if (key == _key && Environment.TickCount64 - _failedAt < 60_000) return; // nie próbujemy w kółko po błędzie
            Stop();
            _key = key;
            if (key.Length == 0 || !Supported) return;
            Windows.Graphics.Capture.GraphicsCaptureItem? item;
            string label;
            if (key == "screen")
            {
                var s = ScreenSources.Screens().FirstOrDefault();
                item = s is null ? null : ScreenSources.ItemFor(s);
                label = "Pulpit";
            }
            else
            {
                var h = GameScan.MainWindowOf(g!.Exe);
                if (h == 0) { _key = ""; return; } // spróbujemy za 5 s
                item = ScreenSources.ItemFor(new ScreenSource(false, h, g.Name, ""));
                label = g.Name;
            }
            if (item is null) { _failedAt = Environment.TickCount64; return; }
            _rec = ClipRecorder.Start(label, item, App.Prefs.ClipQuality, App.Prefs.ClipAudio, _c);
            if (_rec is null) _failedAt = Environment.TickCount64;
            _w.Keys?.SetClipKey(_rec is not null);
            Changed?.Invoke();
        }
        catch (Exception ex) { App.Log("Klipy (start): " + ex.Message); _failedAt = Environment.TickCount64; }
    }

    void Stop()
    {
        if (_rec is null) return;
        _rec.Dispose();
        _rec = null;
        _w.Keys?.SetClipKey(false);
        Changed?.Invoke();
    }

    bool _busy;
    /// <summary>Alt+F9: zapisuje ostatnie N sekund.</summary>
    public async void SaveNow()
    {
        if (_busy) return;
        var rec = _rec;
        if (rec is null) { _w.CurrentShell?.Info("Klipy z gry są wyłączone — włączysz je w Ustawienia → Aplikacja → Klipy z gry.", true); return; }
        _busy = true;
        try
        {
            Tones.Play("clip");
            var path = await rec.Save(App.Prefs.ClipSeconds);
            if (path is null)
            {
                _w.Overlay?.Notify("Klip nie zapisany", rec.LastError ?? "");
                Toasts.ShowClip(null, rec.LastError ?? "Nie udało się zapisać klipu.");
                return;
            }
            LastClip = path;
            _w.Overlay?.Notify("Klip zapisany", $"{rec.Label} · ostatnie {App.Prefs.ClipSeconds} s");
            Toasts.ShowClip(path, $"{rec.Label} · ostatnie {App.Prefs.ClipSeconds} s. Kliknij, żeby przyciąć i wysłać.");
            _w.CurrentShell?.Info("Klip zapisany w Wideo\\GibCord", false);
        }
        finally { _busy = false; }
    }

    public static string? LastClip { get; private set; }

    public void Dispose()
    {
        _t.Stop();
        Stop();
        if (Instance == this) Instance = null;
    }
}
