using System.Text.Json;

namespace GibCord.Core;

/// <summary>Gra z listy serwera (GET /api/games/detectable).</summary>
public sealed record DetectableGame(long? Id, string Name, string[] Executables, string? TitleHint);

/// <summary>Utwór grający teraz na tym komputerze (Spotify).</summary>
public sealed record MusicNow(string Title, string Artists, string Album, long DurationMs, long? StartedAt);

/// <summary>„W co gram” i „czego słucham” — wysyłane przez WebSocket jak w starej aplikacji (game_set, music_set).</summary>
public sealed partial class GibCordClient
{
    public void SendGame(DetectableGame? g, long startedAt)
    {
        if (Offline || _gw is null) return;
        _ = g is null ? _gw.Send(new { t = "game_clear" }) : _gw.Send(new { t = "game_set", name = g.Name, appId = g.Id, startedAt });
    }

    /// <summary>Gra spoza listy serwera (sklep, pełny ekran, dodana ręcznie) — serwer przyjmuje samą nazwę; z listy — także jej numer.</summary>
    public void SendGame(GameHit? g, long startedAt)
    {
        if (Offline || _gw is null) return;
        _ = g is null ? _gw.Send(new { t = "game_clear" }) : _gw.Send(new { t = "game_set", name = g.Name, appId = g.AppId, startedAt });
    }

    public void SendMusic(MusicNow? m)
    {
        if (Offline || _gw is null) return;
        _ = m is null ? _gw.Send(new { t = "music_clear" })
            : _gw.Send(new { t = "music_set", title = m.Title, artists = m.Artists, album = m.Album, durationMs = m.DurationMs, startedAt = m.StartedAt });
    }

    public async Task<List<DetectableGame>> DetectableGames()
    {
        var e = await Api.GetJson("games/detectable");
        var list = new List<DetectableGame>();
        if (e.ValueKind != JsonValueKind.Array) return list;
        foreach (var g in e.EnumerateArray())
        {
            var name = g.Str("name");
            if (string.IsNullOrEmpty(name) || !g.TryGetProperty("executables", out var ex) || ex.ValueKind != JsonValueKind.Array) continue;
            var exes = ex.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.ToLowerInvariant()).Where(x => x.Length is > 0 and <= 128).ToArray();
            if (exes.Length == 0) continue;
            list.Add(new DetectableGame(g.Long("id"), name, exes, g.Str("titleHint")?.ToLowerInvariant()));
        }
        return list;
    }

    /// <summary>Wybór gry jak w starej aplikacji: zostajemy przy obecnej, jeśli dalej działa (bez przeskakiwania).</summary>
    public static DetectableGame? PickGame(IReadOnlyList<DetectableGame> games, ISet<string> running, Func<string, IEnumerable<string>> titlesOf, DetectableGame? current)
    {
        bool Ok(DetectableGame g) => g.Executables.Any(e => running.Contains(e) && (g.TitleHint is null || titlesOf(e).Any(t => t.Contains(g.TitleHint, StringComparison.OrdinalIgnoreCase))));
        if (current is not null && games.FirstOrDefault(g => g.Name == current.Name && Ok(g)) is { } still) return still;
        return games.FirstOrDefault(Ok);
    }
}
