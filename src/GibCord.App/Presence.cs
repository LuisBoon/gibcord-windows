using System.Diagnostics;
using GibCord.Core;
using Windows.Media.Control;

namespace GibCord.App;

/// <summary>
/// „Gra w …” i „Słucha Spotify” przy nicku — natywnie, bez PowerShella i bez logowania do Spotify.
/// Gry (od 6.0.5): Twoja lista, lista z serwera, gry ze sklepów po folderze instalacji (Steam, Epic, GOG, Xbox, EA, Ubisoft, Riot,
/// Rockstar, Amazon, itch, wpisy w rejestrze) i programy na pełnym ekranie — logika w <see cref="GameDetect"/>.
/// Muzyka z Windowsa (sterowanie multimediami, SMTC).
/// </summary>
public sealed class Presence
{
    readonly GibCordClient _c;
    List<DetectableGame> _games = [];
    DateTime _gamesAt;
    volatile GameHit? _hit;
    string? _fsBefore;
    long _gameSince;
    GameLibrary? _lib;
    DateTime _libAt;
    string? _musicKey;
    GlobalSystemMediaTransportControlsSessionManager? _smtc;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _wake = new(0, 1);

    // serwer zapomina grę i muzykę po rozłączeniu (i po każdym wdrożeniu) — po ponownym połączeniu wysyłamy je jeszcze raz
    volatile bool _resendGame, _resendMusic;
    GatewayStatus _last;

    public Presence(GibCordClient c)
    {
        _c = c;
        _c.StatusChanged += OnStatus;
        _ = Loop(_cts.Token);
    }

    void OnStatus(GatewayStatus st, TimeSpan? _)
    {
        if (st == GatewayStatus.Connected && _last != GatewayStatus.Connected) { _resendGame = true; _resendMusic = true; }
        _last = st;
    }

    public void Stop() { _c.StatusChanged -= OnStatus; _cts.Cancel(); }

    /// <summary>Czy właśnie wykryliśmy grę (nakładka w trybie „tylko w grze”).</summary>
    public bool InGame => _hit is not null;

    /// <summary>Wykryta teraz gra (ustawienia pokazują ją z przyciskiem „To nie gra”).</summary>
    public GameHit? Current => _hit;

    /// <summary>Zmiana wykrytej gry (wołane z wątku w tle).</summary>
    public event Action? Changed;

    /// <summary>Skan od razu (po zmianie listy gier w ustawieniach), bez czekania 15 s.</summary>
    public void RescanSoon() { try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { } }

    async Task Loop(CancellationToken ct)
    {
        await Task.Delay(8000, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { if (_c.Status == GatewayStatus.Connected) { await ScanGames().ConfigureAwait(false); await ScanMusic().ConfigureAwait(false); } }
            catch (Exception ex) { App.Log("Obecność: " + ex.Message); }
            try { await _wake.WaitAsync(15000, ct).ConfigureAwait(false); } catch { return; }
        }
    }

    async Task ScanGames()
    {
        if (!App.Prefs.GameDetection) { if (_hit is not null) { _hit = null; _c.SendGame((GameHit?)null, 0); Changed?.Invoke(); } return; }
        if (_games.Count == 0 || DateTime.UtcNow - _gamesAt > TimeSpan.FromHours(6))
        {
            try { _games = await _c.DetectableGames().ConfigureAwait(false); _gamesAt = DateTime.UtcNow; } catch { }
        }
        if (_lib is null || DateTime.UtcNow - _libAt > TimeSpan.FromMinutes(30)) { _lib = GameScan.Library(); _libAt = DateTime.UtcNow; }

        var procs = GameScan.Processes();
        Dictionary<string, string> mine;
        HashSet<string> not;
        try { mine = new(App.Prefs.MyGames, StringComparer.OrdinalIgnoreCase); not = new(App.Prefs.NotGames, StringComparer.OrdinalIgnoreCase); }
        catch { mine = new(StringComparer.OrdinalIgnoreCase); not = new(StringComparer.OrdinalIgnoreCase); }
        var pick = GameDetect.Pick(procs, _games, _lib, mine, not, _hit, _fsBefore);
        // pełny ekran liczy się dopiero przy drugim skanie z rzędu — zapamiętujemy dzisiejszego kandydata
        _fsBefore = procs.FirstOrDefault(p => p.Fullscreen && GameDetect.IsFullscreenGameCandidate(p) && !not.Contains(p.Exe))?.Exe;

        var resend = _resendGame;
        _resendGame = false;
        if (pick?.Name == _hit?.Name && pick?.Exe == _hit?.Exe)
        {
            if (resend && pick is not null) _c.SendGame(pick, _gameSince);
            return;
        }
        if (pick is not null && pick.Name != _hit?.Name) App.Log($"Gra: {pick.Name} ({pick.Source}, {pick.Exe})");
        var sameName = pick is not null && pick.Name == _hit?.Name;
        _hit = pick;
        if (!sameName) _gameSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _c.SendGame(pick, _gameSince);
        Changed?.Invoke();
    }

    async Task ScanMusic()
    {
        if (!App.Prefs.MusicDetection) { if (_musicKey is not null) { _musicKey = null; _c.SendMusic(null); } return; }
        _smtc ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var s = _smtc.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true);
        MusicNow? now = null;
        if (s is not null && s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (!string.IsNullOrWhiteSpace(props?.Title) && !string.IsNullOrWhiteSpace(props?.Artist))
            {
                var tl = s.GetTimelineProperties();
                var pos = tl.Position + (tl.LastUpdatedTime == default ? TimeSpan.Zero : DateTimeOffset.Now - tl.LastUpdatedTime);
                var dur = (long)(tl.EndTime - tl.StartTime).TotalMilliseconds;
                long? started = pos > TimeSpan.Zero && pos < TimeSpan.FromHours(3) ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)pos.TotalMilliseconds : null;
                now = new MusicNow(props!.Title, props.Artist, props.AlbumTitle ?? "", dur > 0 ? dur : 0, started);
            }
        }
        var key = now is null ? null : $"{now.Title}|{now.Artists}|{(now.StartedAt ?? 0) / 10000}";
        var resend = _resendMusic;
        _resendMusic = false;
        if (key == _musicKey && !(resend && now is not null)) return;
        _musicKey = key;
        _c.SendMusic(now);
    }
}
