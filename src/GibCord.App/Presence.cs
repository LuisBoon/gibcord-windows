using System.Diagnostics;
using GibCord.Core;
using Windows.Media.Control;

namespace GibCord.App;

/// <summary>
/// „Gra w …” i „Słucha Spotify” przy nicku — jak w starej aplikacji, ale natywnie:
/// gry po nazwach procesów (lista z serwera), muzyka z Windowsa (sterowanie multimediami, SMTC) — bez PowerShella i bez logowania do Spotify.
/// </summary>
public sealed class Presence
{
    readonly GibCordClient _c;
    List<DetectableGame> _games = [];
    DateTime _gamesAt;
    DetectableGame? _game;
    long _gameSince;
    string? _musicKey;
    GlobalSystemMediaTransportControlsSessionManager? _smtc;
    readonly CancellationTokenSource _cts = new();

    public Presence(GibCordClient c)
    {
        _c = c;
        _ = Loop(_cts.Token);
    }

    public void Stop() => _cts.Cancel();

    async Task Loop(CancellationToken ct)
    {
        await Task.Delay(8000, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { if (_c.Status == GatewayStatus.Connected) { await ScanGames(); await ScanMusic(); } }
            catch (Exception ex) { App.Log("Obecność: " + ex.Message); }
            try { await Task.Delay(15000, ct).ConfigureAwait(false); } catch { return; }
        }
    }

    async Task ScanGames()
    {
        if (!App.Prefs.GameDetection) { if (_game is not null) { _game = null; _c.SendGame(null, 0); } return; }
        if (_games.Count == 0 || DateTime.UtcNow - _gamesAt > TimeSpan.FromHours(6))
        {
            try { _games = await _c.DetectableGames(); _gamesAt = DateTime.UtcNow; } catch { if (_games.Count == 0) return; }
        }
        var procs = Process.GetProcesses();
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byExe = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            var exe = p.ProcessName.ToLowerInvariant() + ".exe";
            running.Add(exe);
            if (!byExe.TryGetValue(exe, out var l)) byExe[exe] = l = [];
            l.Add(p);
        }
        IEnumerable<string> Titles(string exe) => byExe.TryGetValue(exe, out var l) ? l.Select(p => { try { return p.MainWindowTitle; } catch { return ""; } }) : [];
        var pick = GibCordClient.PickGame(_games, running, Titles, _game);
        foreach (var p in procs) p.Dispose();
        if (pick?.Name == _game?.Name) return;
        _game = pick;
        _gameSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _c.SendGame(pick, _gameSince);
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
        if (key == _musicKey) return;
        _musicKey = key;
        _c.SendMusic(now);
    }
}
