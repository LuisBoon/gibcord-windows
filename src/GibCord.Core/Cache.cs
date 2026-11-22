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
/// <summary>Rzeczy zależne od konta (przy kilku kontach każde ma swoje).</summary>
public sealed class AccountPrefs
{
    public Dictionary<long, string> Drafts { get; set; } = [];
    public Dictionary<long, long> LastChannel { get; set; } = [];
    public long LastServer { get; set; } = -1;
    public long LastDm { get; set; }
    public long StatusClearAt { get; set; }
    public string StatusClearText { get; set; } = "";
    public List<long> ServerOrder { get; set; } = [];
    public long AnnouncementSeen { get; set; }
}

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
    /// <summary>Automatyczne raporty błędów na serwer (z końcówką dziennika, bez tokenów i e-maili).</summary>
    public bool SendErrorReports { get; set; } = true;
    /// <summary>Gry dodane ręcznie: plik (małymi literami, z .exe) → nazwa do pokazania.</summary>
    public Dictionary<string, string> MyGames { get; set; } = [];
    /// <summary>Pliki oznaczone „To nie gra” — wykrywanie je pomija.</summary>
    public List<string> NotGames { get; set; } = [];
    /// <summary>Nakładka w grze: "off", "game" (tylko gdy wykryto grę), "always" (zawsze w rozmowie); róg: tl, tr, bl, br.</summary>
    public string OverlayMode { get; set; } = "game";
    public string OverlayCorner { get; set; } = "tl";
    /// <summary>Rozmiar nakładki: s (mała), m (zwykła), l (duża).</summary>
    public string OverlaySize { get; set; } = "m";
    /// <summary>Nowe wiadomości w nakładce, gdy grasz (dymek na kilka sekund).</summary>
    public bool OverlayMessages { get; set; } = true;
    /// <summary>Motyw kolorów (zapamiętany lokalnie, żeby działał od pierwszej klatki; źródło prawdy: me.settings.theme).</summary>
    public string Theme { get; set; } = "glass";
    /// <summary>Skróty Ctrl+Shift+M / Ctrl+Shift+D działające w całym systemie (tylko w trakcie rozmowy).</summary>
    public bool GlobalShortcuts { get; set; } = true;
    /// <summary>Rozmiar tekstu wiadomości: 13 (mały), 14.5 (zwykły), 16.5 (duży), 18 (bardzo duży).</summary>
    public double MessageFontSize { get; set; } = 14.5;
    /// <summary>Tryb zwarty: bez awatarów, godzina przy każdej wiadomości, mniej odstępów.</summary>
    public bool CompactMessages { get; set; }
    /// <summary>Mniej ruchu: bez animacji (sprężyn, wjazdów, przenikania).</summary>
    public bool ReduceMotion { get; set; }
    /// <summary>6.1 zimowy wygląd: "auto" (1.12–6.01, z serwera), "on" (zawsze), "off" (wyłączony).</summary>
    public string WinterMode { get; set; } = "auto";
    /// <summary>6.1: zamknięty krzyżykiem baner „Twój GibCord ROK” (rok).</summary>
    public int WrappedBannerClosed { get; set; }
    /// <summary>Szkło Windows: okno prześwituje pulpit (Mica na Windows 11, akryl na Windows 10). Tylko na tym komputerze.</summary>
    public bool WindowsGlass { get; set; }
    /// <summary>Kółko myszy z wygładzaniem (płynne przewijanie z wyhamowaniem).</summary>
    public bool SmoothWheel { get; set; } = true;
    /// <summary>Kiedy wyczyścić opis statusu (0 = nie czyścić).</summary>
    public long StatusClearAt { get; set; }
    /// <summary>Który opis czyścimy — jeśli w międzyczasie ustawiono inny (np. na telefonie), zostawiamy go.</summary>
    public string StatusClearText { get; set; } = "";
    /// <summary>Czyje są szkice i ostatnie kanały (po zmianie konta czyścimy je).</summary>
    public long LastUserId { get; set; }
    /// <summary>Notatki o osobach zapisane na tym komputerze (konto → osoba → treść), gdy serwer ich jeszcze nie obsługuje.</summary>
    public Dictionary<long, Dictionary<long, string>> Notes { get; set; } = [];
    /// <summary>Własna kolejność serwerów na pasku (tylko na tym komputerze).</summary>
    public List<long> ServerOrder { get; set; } = [];
    public bool MembersPanel { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public int[]? Window { get; set; }   // x, y, szer., wys., zmaksymalizowane (0/1)
    /// <summary>Ustawienia głosu: urządzenia, czułość, „naciśnij, aby mówić”, głośność osób.</summary>
    public Voice.VoiceOptions Voice { get; set; } = new();
    /// <summary>Transmisja ekranu: jakość (720p / 1080p / 1440p), płynność (30 / 60) i dźwięk z komputera.</summary>
    public string StreamQuality { get; set; } = "1080p";
    public int StreamFps { get; set; } = 30;
    public bool StreamAudio { get; set; } = true;
    /// <summary>Ostatnio użyte emoji (najnowsze pierwsze; zwykłe albo &lt;:nazwa:id&gt;).</summary>
    public List<string> RecentEmoji { get; set; } = [];
    /// <summary>Ogłoszenie adminów zamknięte krzyżykiem (jego czas „at”).</summary>
    public long AnnouncementSeen { get; set; }
    /// <summary>Szkice wiadomości na kanałach (klucz listy → tekst).</summary>
    public Dictionary<long, string> Drafts { get; set; } = [];
    /// <summary>Kilka kont: ustawienia pozostałych kont (szkice, ostatnie kanały, kolejność serwerów…), aktywnego — w polach wyżej.</summary>
    public Dictionary<long, AccountPrefs> PerAccount { get; set; } = [];

    /// <summary>Przejście z konta <paramref name="from"/> na <paramref name="to"/>: odkłada jego rzeczy, wyjmuje tamtego.</summary>
    public void SwapAccount(long from, long to)
    {
        if (from == to) return;
        if (from != 0)
            PerAccount[from] = new AccountPrefs
            {
                Drafts = Drafts, LastChannel = LastChannel, LastServer = LastServer, LastDm = LastDm,
                StatusClearAt = StatusClearAt, StatusClearText = StatusClearText, ServerOrder = ServerOrder, AnnouncementSeen = AnnouncementSeen,
            };
        var a = to != 0 && PerAccount.Remove(to, out var got) ? got : new AccountPrefs();
        Drafts = a.Drafts; LastChannel = a.LastChannel; LastServer = a.LastServer; LastDm = a.LastDm;
        StatusClearAt = a.StatusClearAt; StatusClearText = a.StatusClearText; ServerOrder = a.ServerOrder; AnnouncementSeen = a.AnnouncementSeen;
        LastUserId = to;
    }

    public void UseEmoji(string e)
    {
        RecentEmoji.Remove(e);
        RecentEmoji.Insert(0, e);
        if (RecentEmoji.Count > 24) RecentEmoji.RemoveRange(24, RecentEmoji.Count - 24);
        Save();
    }

    static string? _path;
    static readonly object _saveLock = new();
    public static Prefs Load(string dir)
    {
        _path = Path.Combine(dir, "ustawienia.json");
        return SafeFile.Read<Prefs>(_path) ?? new();
    }
    public void Save()
    {
        if (_path is null) return;
        string json;
        lock (_saveLock) json = JsonSerializer.Serialize(this, Json.Options);
        SafeFile.Write(_path, json);
    }
}

/// <summary>
/// Zapis bez ryzyka: najpierw plik .tmp, potem podmiana — przerwany zapis (zawieszenie, brak prądu, aktualizacja)
/// nie zostawia uciętego JSON-a. Uszkodzony plik przy odczycie odkładamy jako .uszkodzony zamiast go nadpisać.
/// </summary>
public static class SafeFile
{
    static readonly object _lock = new();

    public static void Write(string path, string content)
    {
        lock (_lock)
        {
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, true);
            }
            catch { }
        }
    }

    public static T? Read<T>(string path) where T : class
    {
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                // zapis przerwany między .tmp a podmianą — ratujemy .tmp
                var tmp = path + ".tmp";
                if (!File.Exists(tmp)) return null;
                try { File.Move(tmp, path, true); } catch { return null; }
            }
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options); }
            catch
            {
                try { File.Copy(path, path + ".uszkodzony", true); } catch { }
                return null;
            }
        }
    }
}
