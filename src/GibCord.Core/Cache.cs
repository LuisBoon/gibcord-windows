using System.Collections.Concurrent;
using System.Text.Json;

namespace GibCord.Core;

/// <summary>
/// Pamięć podręczna na dysku: ostatni stan (/api/state) i ostatnie wiadomości każdego kanału.
/// Dzięki temu okno pokazuje czat od razu po starcie, zanim serwer odpowie.
/// </summary>
public sealed class DiskCache
{
    public const int MessagesPerChannel = 60;
    readonly string _dir;
    readonly string _msgDir;
    readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

    public DiskCache(string dir)
    {
        _dir = dir;
        _msgDir = Path.Combine(dir, "wiadomosci");
        Directory.CreateDirectory(_msgDir);
    }

    public string Dir => _dir;

    public StateResponse? LoadState()
    {
        try
        {
            var p = Path.Combine(_dir, "stan.json");
            return File.Exists(p) ? Json.Deserialize<StateResponse>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public void SaveState(string rawJson) => WriteSoon("stan.json", () => rawJson, TimeSpan.Zero);

    public List<Message>? LoadMessages(long channelId)
    {
        try
        {
            var p = Path.Combine(_msgDir, channelId + ".json");
            return File.Exists(p) ? Json.Deserialize<List<Message>>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    /// <summary>Zapisuje ostatnie wiadomości kanału (z opóźnieniem — kolejne zmiany sklejają się w jeden zapis).</summary>
    public void SaveMessages(long channelId, IEnumerable<Message> items)
    {
        var snapshot = items.Where(m => m.Send == SendState.Sent && m.Id > 0).TakeLast(MessagesPerChannel).ToList();
        WriteSoon(Path.Combine("wiadomosci", channelId + ".json"), () => Json.Serialize(snapshot), TimeSpan.FromSeconds(2));
    }

    void WriteSoon(string rel, Func<string> content, TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        if (_pending.TryGetValue(rel, out var old)) old.Cancel();
        _pending[rel] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
                var path = Path.Combine(_dir, rel);
                var tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, content(), cts.Token);
                File.Move(tmp, path, true);
            }
            catch { /* pamięć podręczna nie jest krytyczna */ }
            finally { _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(rel, cts)); }
        });
    }

    /// <summary>Po wylogowaniu: nic z poprzedniego konta nie zostaje na dysku.</summary>
    public void Clear()
    {
        foreach (var c in _pending.Values) c.Cancel();
        try { foreach (var f in Directory.GetFiles(_msgDir)) File.Delete(f); } catch { }
        try { File.Delete(Path.Combine(_dir, "stan.json")); } catch { }
    }
}

/// <summary>Drobne ustawienia aplikacji (ostatni kanał, rozmiar okna…) w jednym pliku JSON.</summary>
public sealed class Prefs
{
    public Dictionary<long, long> LastChannel { get; set; } = [];
    public long LastServer { get; set; } = -1;    // -1 = jeszcze nic, 0 = rozmowy prywatne
    public long LastDm { get; set; }
    public bool Notifications { get; set; } = true;
    public bool Sounds { get; set; } = true;
    /// <summary>Krótki dźwięk przy nowej wiadomości (gdy przychodzi powiadomienie).</summary>
    public bool MessageSound { get; set; } = true;
    /// <summary>Pokazuj innym, w co grasz (wykrywanie po procesach) i czego słuchasz w Spotify.</summary>
    public bool GameDetection { get; set; } = true;
    public bool MusicDetection { get; set; } = true;
    /// <summary>Nakładka w grze: "off", "game" (tylko gdy wykryto grę), "always" (zawsze w rozmowie); róg: tl, tr, bl, br.</summary>
    public string OverlayMode { get; set; } = "game";
    public string OverlayCorner { get; set; } = "tl";
    public bool MembersPanel { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public int[]? Window { get; set; }   // x, y, szer., wys., zmaksymalizowane (0/1)
    /// <summary>Ustawienia głosu: urządzenia, czułość, „naciśnij, aby mówić”, głośność osób.</summary>
    public Voice.VoiceOptions Voice { get; set; } = new();
    /// <summary>Ostatnio użyte emoji (najnowsze pierwsze; zwykłe albo &lt;:nazwa:id&gt;).</summary>
    public List<string> RecentEmoji { get; set; } = [];
    /// <summary>Ogłoszenie adminów zamknięte krzyżykiem (jego czas „at”).</summary>
    public long AnnouncementSeen { get; set; }
    /// <summary>Szkice wiadomości na kanałach (klucz listy → tekst).</summary>
    public Dictionary<long, string> Drafts { get; set; } = [];

    public void UseEmoji(string e)
    {
        RecentEmoji.Remove(e);
        RecentEmoji.Insert(0, e);
        if (RecentEmoji.Count > 24) RecentEmoji.RemoveRange(24, RecentEmoji.Count - 24);
        Save();
    }

    static string? _path;
    public static Prefs Load(string dir)
    {
        _path = Path.Combine(dir, "ustawienia.json");
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(_path), Json.Options) ?? new() : new(); }
        catch { return new(); }
    }
    public void Save()
    {
        if (_path is null) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(this, Json.Options)); } catch { }
    }
}
