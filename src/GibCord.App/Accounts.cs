using GibCord.Core;

namespace GibCord.App;

/// <summary>Konto zapamiętane na tym komputerze (do przełącznika kont).</summary>
public sealed class AccountInfo
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Avatar { get; set; }
    public string Color { get; set; } = "#ffb98f";
    public long LastUsed { get; set; }
}

/// <summary>
/// Kilka kont naraz. Aktywne konto ma token w sesja.bin (jak dotąd), pozostałe — w konto-ID.bin (DPAPI, tylko ten użytkownik Windows).
/// Każde konto ma własną pamięć podręczną (pamiec-ID) i własne szkice/ostatnie kanały (Prefs.PerAccount).
/// Konta nieaktywne są połączone w tle bez okna: dają powiadomienia i licznik nieprzeczytanych przy przełączniku.
/// </summary>
public static class Accounts
{
    static string FilePath => Path.Combine(App.DataDir, "konta.json");
    public static string ActiveTokenPath => Path.Combine(App.DataDir, "sesja.bin");
    public static string TokenPath(long id) => Path.Combine(App.DataDir, $"konto-{id}.bin");
    public static string CachePath(long id) => Path.Combine(App.DataDir, id > 0 ? $"pamiec-{id}" : "pamiec");

    static List<AccountInfo>? _list;
    public static List<AccountInfo> List => _list ??= SafeFile.Read<List<AccountInfo>>(FilePath) ?? [];
    public static event Action? Changed;

    static void Save() { SafeFile.Write(FilePath, Json.Serialize(List)); Changed?.Invoke(); }

    /// <summary>Konto zalogowane w oknie — dopisz albo odśwież (nazwa, awatar).</summary>
    public static void Remember(User me)
    {
        if (me.Id == 0) return;
        var a = List.FirstOrDefault(x => x.Id == me.Id);
        if (a is null) List.Add(a = new AccountInfo { Id = me.Id });
        a.Username = me.Username; a.Name = me.Name; a.Avatar = me.Avatar; a.Color = me.Color ?? "#ffb98f";
        a.LastUsed = Format.NowMs();
        Save();
    }

    /// <summary>Tryb pokazowy (zrzuty CI): przykładowe zapisane konta i ich nowe wiadomości.</summary>
    public static List<(AccountInfo Info, int Unread)>? Demo;

    /// <summary>Konta inne niż <paramref name="active"/>, które mają zapisany token.</summary>
    public static IEnumerable<AccountInfo> Others(long active) => Demo is { } d ? d.Select(x => x.Info).Where(a => a.Id != active)
        : List.Where(a => a.Id != active && File.Exists(TokenPath(a.Id))).OrderByDescending(a => a.LastUsed);

    /// <summary>Zapomnij konto (po wylogowaniu): token, pamięć podręczna i wpis.</summary>
    public static void Forget(long id)
    {
        Background.Stop(id);
        try { File.Delete(TokenPath(id)); } catch { }
        try { var d = CachePath(id); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        List.RemoveAll(a => a.Id == id);
        App.Prefs.PerAccount.Remove(id);
        Save();
    }

    /// <summary>Token aktywnego konta odkładany na bok (przy przełączaniu albo dodawaniu konta).</summary>
    public static void Park(long id)
    {
        if (id == 0 || !File.Exists(ActiveTokenPath)) return;
        File.Copy(ActiveTokenPath, TokenPath(id), true);
    }

    /// <summary>Token konta <paramref name="id"/> staje się aktywnym (sesja.bin).</summary>
    public static bool Unpark(long id)
    {
        if (!File.Exists(TokenPath(id))) return false;
        File.Copy(TokenPath(id), ActiveTokenPath, true);
        File.Delete(TokenPath(id));
        return true;
    }

    // ───────────────────────── konta w tle ─────────────────────────

    public static class Background
    {
        sealed class Bg { public GibCordClient C = null!; public int Unread; }
        static readonly Dictionary<long, Bg> _bg = [];
        /// <summary>Powiadomienie z konta w tle: (konto, tytuł, treść, klucz rozmowy).</summary>
        public static event Action<long, string, string, long>? Notify;
        public static event Action? UnreadChanged;
        public static Action<Action>? Ui;

        public static int UnreadOf(long id) => Demo is { } d ? d.FirstOrDefault(x => x.Info.Id == id).Unread : _bg.TryGetValue(id, out var b) ? b.Unread : 0;
        public static int UnreadTotal => Demo is { } d ? d.Sum(x => x.Unread) : _bg.Values.Sum(b => b.Unread);
        public static bool IsRunning(long id) => _bg.ContainsKey(id);

        public static void Start(long id, Uri server)
        {
            if (Ui is null || _bg.ContainsKey(id) || !File.Exists(TokenPath(id))) return;
            var c = new GibCordClient(server, new DpapiTokenStore(TokenPath(id)), null, Ui);
            var b = new Bg { C = c };
            _bg[id] = b;
            c.Notify += m => Notify?.Invoke(id, c.Store.NotificationTitle(m), c.Store.PlainText(m), m.Key);
            c.Store.Changed += ch =>
            {
                if (ch.Kind is not (ChangeKind.All or ChangeKind.Reads or ChangeKind.Dms or ChangeKind.MessageAdded or ChangeKind.Notify or ChangeKind.Servers)) return;
                var s = c.Store;
                var n = s.Loaded ? s.Mentions.Where(kv => !s.ChannelMuted(kv.Key)).Sum(kv => kv.Value) + s.DmUnreadTotal() : 0;
                if (n != b.Unread) { b.Unread = n; UnreadChanged?.Invoke(); }
                if (ch.Kind == ChangeKind.All && s.Me.Id == id) Remember(s.Me);
            };
            c.SessionEnded += _ => Ui(() => Forget(id)); // wylogowano to konto gdzie indziej — znika z listy
            c.SetVisible(false); // telefon nadal dostaje powiadomienia push, jak przy zamkniętym oknie
            c.SetWindowActive(false);
            _ = c.Start();
        }

        public static void Stop(long id)
        {
            if (!_bg.Remove(id, out var b)) return;
            _ = b.C.DisposeAsync().AsTask();
            UnreadChanged?.Invoke();
        }

        public static void StopAll() { foreach (var id in _bg.Keys.ToList()) Stop(id); }
    }
}
