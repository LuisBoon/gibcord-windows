using System.Text.Json;

namespace GibCord.Core;

public enum ChangeKind
{
    All, Me, Servers, Server, Members, Dms, User, Presence, Reads, Friends, Voice, Typing,
    MessageAdded, MessageUpdated, MessageRemoved, MessagesReset, Notify, Blocked, Announcement, Thread, Watch,
}

/// <summary>Co się zmieniło. <c>Id</c> = serwer / użytkownik / kanał, zależnie od rodzaju.</summary>
public readonly record struct StoreChange(ChangeKind Kind, long Id = 0, long ChannelId = 0, Message? Message = null);

/// <summary>Załadowane wiadomości jednego kanału (rosnąco po id).</summary>
public sealed class MessageWindow
{
    public List<Message> Items { get; } = [];
    public bool HasOlder { get; set; } = true;
    /// <summary>Okno kończy się na najnowszej wiadomości — nowe można dopisywać na końcu.</summary>
    public bool AtTail { get; set; } = true;
    public bool FromCache { get; set; }
}

public sealed record ChannelGroup(Category? Category, List<Channel> Channels);

/// <summary>
/// Stan aplikacji: to, co przyszło z /api/state, aktualizowane zdarzeniami z WebSocket.
/// Nie jest wielowątkowy — wszystko wołamy z jednego wątku (w aplikacji: wątek okna).
/// </summary>
public sealed class Store
{
    public User Me { get; private set; } = new();
    public Dictionary<long, User> Users { get; } = [];
    public List<Server> Servers { get; } = [];
    public List<Dm> Dms { get; } = [];
    public Dictionary<long, long> Reads { get; } = [];
    public Dictionary<long, List<VoiceMember>> Voice { get; } = [];
    public Dictionary<long, Friend> Friends { get; } = [];
    public HashSet<long> Blocked { get; } = [];
    public NotifyState Notify { get; private set; } = new();
    public Announcement? Announcement { get; private set; }
    public Dictionary<long, int> Mentions { get; } = [];
    public Dictionary<long, MessageWindow> Windows { get; } = [];
    /// <summary>Znane wątki (z wiadomości, list i zdarzeń). Okno wiadomości wątku: <c>Windows[-id]</c>.</summary>
    public Dictionary<long, ThreadInfo> Threads { get; } = [];
    /// <summary>Wspólne oglądanie w pokojach (kanał głosowy / rozmowa) → stan.</summary>
    public Dictionary<long, WatchState> Watches { get; } = [];
    readonly Dictionary<long, Dictionary<long, long>> _typing = [];
    readonly Dictionary<long, (Channel C, Server S)> _chan = [];
    public bool Loaded { get; private set; }

    public event Action<StoreChange>? Changed;
    void Raise(ChangeKind k, long id = 0, long cid = 0, Message? m = null) => Changed?.Invoke(new StoreChange(k, id, cid, m));

    // ───────────────────────── wczytanie stanu ─────────────────────────

    public void Load(StateResponse st)
    {
        Me = st.Me;
        Users.Clear();
        foreach (var (id, u) in st.Users) Users[id] = u;
        Users[Me.Id] = Me;
        Servers.Clear(); Servers.AddRange(st.Servers);
        Dms.Clear(); Dms.AddRange(st.Dms);
        Reads.Clear(); foreach (var (k, v) in st.Reads) Reads[k] = v;
        Voice.Clear(); foreach (var (k, v) in st.Voice) Voice[k] = v;
        Friends.Clear(); foreach (var f in st.Friends) Friends[f.UserId] = f;
        Blocked.Clear(); Blocked.UnionWith(st.Blocked);
        Notify = st.Notify ?? new();
        Announcement = st.Announcement;
        Watches.Clear();
        foreach (var (k, v) in st.Watch) if (v is not null) Watches[k] = v;
        // wzmianki: liczymy tylko na żywo; po ponownym wczytaniu zostają tam, gdzie kanał dalej jest nieprzeczytany
        foreach (var cid in Mentions.Keys.ToList()) if (!IsUnread(cid)) Mentions.Remove(cid);
        Reindex();
        // okna wiadomości ze starego stanu mogą mieć dziury — kanał przeładuje się przy otwarciu
        foreach (var w in Windows.Values) w.FromCache = true;
        Loaded = true;
        Raise(ChangeKind.All);
    }

    void Reindex()
    {
        _chan.Clear();
        foreach (var s in Servers) foreach (var c in s.Channels) _chan[c.Id] = (c, s);
    }

    // ───────────────────────── pytania o stan ─────────────────────────

    public User? U(long id) => Users.TryGetValue(id, out var u) ? u : null;
    public Server? ServerById(long id) => Servers.Find(s => s.Id == id);
    public Channel? ChannelById(long id) => _chan.TryGetValue(id, out var x) ? x.C : null;
    public Server? ServerOfChannel(long cid) => _chan.TryGetValue(cid, out var x) ? x.S : null;
    public Dm? DmById(long id) => Dms.Find(d => d.Id == id);
    public bool IsDm(long cid) => DmById(cid) is not null;

    public long LastIdOf(long cid) => ChannelById(cid)?.LastMessageId ?? DmById(cid)?.LastMessageId ?? 0;
    public bool IsUnread(long cid) { var last = LastIdOf(cid); return last > 0 && last > Reads.GetValueOrDefault(cid); }
    public int MentionsIn(long cid) => Mentions.GetValueOrDefault(cid);

    public Member? MemberOf(Server? s, long uid) => s?.Members.Find(m => m.UserId == uid);

    public List<Role> RolesOf(Server? s, long uid)
    {
        var m = MemberOf(s, uid);
        if (s is null || m is null || m.RoleIds.Count == 0) return [];
        return s.Roles.Where(r => m.RoleIds.Contains(r.Id)).OrderByDescending(r => r.Position).ToList();
    }

    public string NameOf(long uid, Server? s = null)
    {
        var nick = MemberOf(s, uid)?.Nick;
        if (!string.IsNullOrWhiteSpace(nick)) return nick!;
        return U(uid)?.Name ?? "Nieznany";
    }

    static readonly string[] Palette = ["#ffb98f", "#e8708f", "#6fe3a5", "#7cc7ff", "#b69cff", "#f2c45a", "#4fd1c5", "#ff8a65", "#9be15d", "#f78fb3"];
    public static string ColorOfUser(User? u) => !string.IsNullOrEmpty(u?.Color) ? u!.Color! : Palette[(int)((u?.Id ?? 0) % Palette.Length)];

    /// <summary>Kolor nazwy: najwyższa rola z kolorem, inaczej kolor akcentu osoby (jak na stronie).</summary>
    public string NameColor(long uid, Server? s) =>
        RolesOf(s, uid).FirstOrDefault(r => !string.IsNullOrEmpty(r.Color))?.Color ?? ColorOfUser(U(uid));

    public string? AvatarOf(long uid, Server? s = null) => MemberOf(s, uid)?.Avatar ?? U(uid)?.Avatar;

    /// <summary>Dostępność do pokazania: online / idle / dnd / offline.</summary>
    public string StatusOf(long uid)
    {
        var u = U(uid);
        if (u is null) return "offline";
        if (uid == Me.Id) return Me.Status == "invisible" ? "offline" : (string.IsNullOrEmpty(Me.Status) ? "online" : Me.Status);
        return u.Online ? (string.IsNullOrEmpty(u.Status) || u.Status == "invisible" ? "online" : u.Status) : "offline";
    }

    public long PermsIn(long cid)
    {
        var s = ServerOfChannel(cid);
        if (s is null) return Perm.All; // DM i grupy
        if (s.OwnerId == Me.Id) return Perm.All;
        if (s.MyPerms is { } mp)
        {
            if ((mp.Server & Perm.Admin) != 0) return Perm.All;
            return mp.Channels.TryGetValue(cid, out var p) ? p : 0;
        }
        return Perm.All;
    }
    public bool Can(long cid, long perm) => (PermsIn(cid) & perm) == perm;

    /// <summary>Uprawnienie na poziomie serwera (właściciel i administrator mogą wszystko).</summary>
    public bool CanServer(Server? s, long perm)
    {
        if (s is null) return false;
        if (s.OwnerId == Me.Id) return true;
        var p = s.MyPerms?.Server ?? 0;
        return (p & Perm.Admin) != 0 || (p & perm) == perm;
    }
    public long? TimedOutUntil(Server? s)
    {
        var t = MemberOf(s, Me.Id)?.TimeoutUntil;
        return t is long v && v > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? v : null;
    }

    public bool MentionsMe(Message m)
    {
        if (m.Mentions.Contains(Me.Id) || m.MentionEveryone) return true;
        if (m.MentionRoles.Count == 0) return false;
        var mine = MemberOf(ServerOfChannel(m.ChannelId), Me.Id)?.RoleIds;
        return mine is not null && m.MentionRoles.Any(mine.Contains);
    }

    public User? DmOther(Dm d) => U(d.UserIds.FirstOrDefault(x => x != Me.Id, Me.Id));

    public string DmTitle(Dm d)
    {
        if (!d.IsGroup) return DmOther(d)?.Name ?? "Rozmowa";
        if (!string.IsNullOrWhiteSpace(d.Name)) return d.Name!;
        var names = d.UserIds.Where(x => x != Me.Id).Select(x => U(x)?.Name ?? "?").ToList();
        return names.Count == 0 ? "Grupa" : string.Join(", ", names);
    }

    public bool DmHidden(Dm d) => d.HiddenAt is long h && (d.LastMessageId ?? 0) <= h;

    /// <summary>Rozmowy prywatne od najnowszej (bez zamkniętych ✕).</summary>
    public List<Dm> DmsSorted() => Dms.Where(d => !DmHidden(d)).OrderByDescending(d => d.LastMessageId ?? d.Id).ToList();

    /// <summary>Kanały serwera: najpierw bez kategorii, potem kategorie wg pozycji.</summary>
    public List<ChannelGroup> ChannelTree(Server s)
    {
        static IEnumerable<Channel> Sort(IEnumerable<Channel> cs) => cs.OrderBy(c => c.IsVoice ? 1 : 0).ThenBy(c => c.Position).ThenBy(c => c.Id);
        var groups = new List<ChannelGroup>();
        var catIds = s.Categories.Select(c => c.Id).ToHashSet();
        var loose = s.Channels.Where(c => c.CategoryId is null || !catIds.Contains(c.CategoryId.Value)).ToList();
        if (loose.Count > 0) groups.Add(new ChannelGroup(null, Sort(loose).ToList()));
        foreach (var cat in s.Categories.OrderBy(c => c.Position).ThenBy(c => c.Id))
            groups.Add(new ChannelGroup(cat, Sort(s.Channels.Where(c => c.CategoryId == cat.Id)).ToList()));
        return groups;
    }

    public Channel? FirstTextChannel(Server s) =>
        ChannelTree(s).SelectMany(g => g.Channels).FirstOrDefault(c => !c.IsVoice);

    // ── powiadomienia: wyciszenia i poziomy (API.md 13.2) ──
    static bool MutedNow(NotifySetting? n) => n?.MutedUntil is long t && t > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool ChannelMuted(long cid)
    {
        var s = ServerOfChannel(cid);
        if (s is null) return MutedNow(Notify.Dms.GetValueOrDefault(cid)) || Notify.Dms.GetValueOrDefault(cid)?.Level == "none";
        var ch = Notify.Channels.GetValueOrDefault(cid);
        var sv = Notify.Servers.GetValueOrDefault(s.Id);
        return MutedNow(ch) || MutedNow(sv) || (ch?.Level ?? sv?.Level) == "none";
    }

    public bool ServerMuted(long sid) => MutedNow(Notify.Servers.GetValueOrDefault(sid));

    public string NotifyLevel(long cid)
    {
        var s = ServerOfChannel(cid);
        if (s is null) return Notify.Dms.GetValueOrDefault(cid)?.Level ?? "all";
        return Notify.Channels.GetValueOrDefault(cid)?.Level ?? Notify.Servers.GetValueOrDefault(s.Id)?.Level ?? "mentions";
    }

    public bool ServerUnread(Server s) => !ServerMuted(s.Id) && s.Channels.Any(c => !c.IsVoice && IsUnread(c.Id) && !ChannelMuted(c.Id));
    public int ServerMentions(Server s) => s.Channels.Sum(c => MentionsIn(c.Id));
    public int DmUnreadTotal() => Dms.Count(d => !DmHidden(d) && IsUnread(d.Id) && !ChannelMuted(d.Id));

    /// <summary>Czy pokazać powiadomienie Windows o tej wiadomości.</summary>
    public bool ShouldNotify(Message m, long activeChannelId, bool windowActive, long activeThreadId = 0)
    {
        if (m.AuthorId == Me.Id || m.Type != "default" || Blocked.Contains(m.AuthorId)) return false;
        if (Me.Status == "dnd") return false;
        if (m.ThreadId is long tid)
        {
            // wątek: tylko obserwowany albo wzmianka (jak na stronie), i nie gdy właśnie go czytam
            if (windowActive && tid == activeThreadId) return false;
            return !ChannelMuted(m.ChannelId) && (MentionsMe(m) || Threads.GetValueOrDefault(tid)?.Following == true);
        }
        if (windowActive && activeChannelId == m.ChannelId) return false;
        if (ChannelMuted(m.ChannelId)) return false;
        return NotifyLevel(m.ChannelId) switch { "all" => true, "none" => false, _ => MentionsMe(m) };
    }

    public string NotificationTitle(Message m)
    {
        var who = NameOf(m.AuthorId, ServerOfChannel(m.ChannelId));
        if (m.ThreadId is long tid) return $"{who} · 🧵 {Threads.GetValueOrDefault(tid)?.Name ?? "wątek"}";
        if (DmById(m.ChannelId) is { } d) return d.IsGroup ? $"{who} ({DmTitle(d)})" : who;
        var c = ChannelById(m.ChannelId);
        var s = ServerOfChannel(m.ChannelId);
        return c is null ? who : $"{who} (#{c.Name}, {s?.Name})";
    }

    /// <summary>Treść do podglądu: wzmianki jako @nazwa, bez znaczników formatowania.</summary>
    public string PlainText(Message m, int max = 140)
    {
        var t = Markup.Plain(m.Content, id => "@" + NameOf(id, ServerOfChannel(m.ChannelId)), RoleName);
        if (string.IsNullOrWhiteSpace(t))
        {
            if (m.Sticker is not null) t = "Naklejka: " + m.Sticker.Name;
            else if (m.Attachments.Count > 0) t = m.Attachments.All(a => a.IsImage) ? "🖼️ Obrazek" : "📎 " + m.Attachments[0].Name;
            else if (m.Poll.ValueKind == JsonValueKind.Object) t = "📊 " + (m.Poll.Str("question") ?? "Ankieta");
        }
        return t.Length > max ? t[..(max - 1)] + "…" : t;
    }

    public string? RoleName(long roleId)
    {
        foreach (var s in Servers) foreach (var r in s.Roles) if (r.Id == roleId) return r.Name;
        return null;
    }
    public Role? RoleById(long roleId)
    {
        foreach (var s in Servers) foreach (var r in s.Roles) if (r.Id == roleId) return r;
        return null;
    }
    public Emoji? EmojiById(long id)
    {
        foreach (var s in Servers) foreach (var e in s.Emojis) if (e.Id == id) return e;
        return null;
    }

    // ── „pisze…” ──
    public void Typing(long cid, long uid)
    {
        if (uid == Me.Id) return;
        if (!_typing.TryGetValue(cid, out var map)) _typing[cid] = map = [];
        map[uid] = Environment.TickCount64 + 8000;
        Raise(ChangeKind.Typing, cid, cid);
    }
    public List<long> TypingIn(long cid)
    {
        if (!_typing.TryGetValue(cid, out var map)) return [];
        var now = Environment.TickCount64;
        foreach (var k in map.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) map.Remove(k);
        return map.Keys.ToList();
    }
    void StopTyping(long cid, long uid)
    {
        if (_typing.TryGetValue(cid, out var map) && map.Remove(uid)) Raise(ChangeKind.Typing, cid, cid);
    }

    // ───────────────────────── okna wiadomości ─────────────────────────

    public MessageWindow Window(long cid) => Windows.TryGetValue(cid, out var w) ? w : Windows[cid] = new MessageWindow();

    public void SetWindow(long cid, List<Message> msgs, bool hasOlder, bool atTail = true, bool fromCache = false)
    {
        var w = Window(cid);
        var pending = w.Items.Where(x => x.Send != SendState.Sent).ToList();
        w.Items.Clear();
        w.Items.AddRange(msgs.Where(x => x.Key == cid).OrderBy(x => x.Id));
        if (atTail) foreach (var p in pending) if (!w.Items.Any(x => x.Nonce is not null && x.Nonce == p.Nonce)) w.Items.Add(p);
        w.HasOlder = hasOlder;
        w.AtTail = atTail;
        w.FromCache = fromCache;
        Raise(ChangeKind.MessagesReset, cid, cid);
    }

    public void PrependOlder(long cid, List<Message> older, bool hasOlder)
    {
        var w = Window(cid);
        var have = w.Items.Select(x => x.Id).ToHashSet();
        w.Items.InsertRange(0, older.Where(x => x.Key == cid && !have.Contains(x.Id)).OrderBy(x => x.Id));
        w.HasOlder = hasOlder;
        Raise(ChangeKind.MessagesReset, cid, cid);
    }

    /// <summary>Wiadomość wysyłana — od razu na liście, szara do potwierdzenia.</summary>
    public void AddPending(Message m)
    {
        var w = Window(m.Key);
        m.Send = SendState.Pending;
        w.Items.Add(m);
        Raise(ChangeKind.MessageAdded, m.Id, m.Key, m);
    }

    public void FailPending(long cid, string nonce)
    {
        var m = Window(cid).Items.Find(x => x.Nonce == nonce && x.Send != SendState.Sent);
        if (m is null) return;
        m.Send = SendState.Failed;
        Raise(ChangeKind.MessageUpdated, m.Id, cid, m);
    }

    public void RemoveLocal(long cid, long id)
    {
        var w = Window(cid);
        var i = w.Items.FindIndex(x => x.Id == id);
        if (i < 0) return;
        var m = w.Items[i];
        w.Items.RemoveAt(i);
        Raise(ChangeKind.MessageRemoved, id, cid, m);
    }

    /// <summary>Wiadomość z serwera (odpowiedź na POST albo message_create) — podmienia oczekującą o tym samym nonce.</summary>
    public void Upsert(Message m, bool live = true)
    {
        var key = m.Key;
        if (m.ThreadId is long tid)
        {
            if (Threads.TryGetValue(tid, out var th) && (th.LastMessageId ?? 0) < m.Id) { th.LastMessageId = m.Id; th.LastAt = Math.Max(th.LastAt, m.CreatedAt); }
            if (!Windows.ContainsKey(key)) return; // wątek nie jest otwarty — dociągnie się przy otwarciu
        }
        else BumpLast(m);
        var w = Window(key);
        var i = w.Items.FindIndex(x => x.Id == m.Id);
        if (i < 0 && m.Nonce is not null) i = w.Items.FindIndex(x => x.Send != SendState.Sent && x.Nonce == m.Nonce);
        if (i >= 0)
        {
            var old = w.Items[i];
            w.Items[i] = m;
            if (old.Id != m.Id) { Raise(ChangeKind.MessageRemoved, old.Id, key, old); Raise(ChangeKind.MessageAdded, m.Id, key, m); }
            else Raise(ChangeKind.MessageUpdated, m.Id, key, m);
            return;
        }
        if (!w.AtTail && live) { Raise(ChangeKind.Reads, m.ChannelId); return; }
        // na właściwe miejsce (zwykle na koniec, przed oczekującymi)
        var at = w.Items.Count;
        while (at > 0 && (w.Items[at - 1].Send != SendState.Sent || w.Items[at - 1].Id > m.Id)) at--;
        w.Items.Insert(at, m);
        Raise(ChangeKind.MessageAdded, m.Id, key, m);
    }

    void BumpLast(Message m)
    {
        if (ChannelById(m.ChannelId) is { } c) { if ((c.LastMessageId ?? 0) < m.Id) c.LastMessageId = m.Id; }
        else if (DmById(m.ChannelId) is { } d)
        {
            if ((d.LastMessageId ?? 0) < m.Id) d.LastMessageId = m.Id;
            Raise(ChangeKind.Dms);
        }
    }

    public void MarkReadLocal(long cid, long mid)
    {
        if (Reads.GetValueOrDefault(cid) >= mid && !Mentions.ContainsKey(cid)) return;
        if (Reads.GetValueOrDefault(cid) < mid) Reads[cid] = mid;
        if (!IsUnread(cid)) Mentions.Remove(cid);
        Raise(ChangeKind.Reads, cid, cid);
    }

    // ───────────────────────── wątki ─────────────────────────

    public ThreadInfo? ThreadById(long id) => Threads.GetValueOrDefault(id);

    /// <summary>Nowe dane wątku (z listy, otwarcia albo thread_update). „Obserwuję” i „przeczytane” zostają, gdy ich brak.</summary>
    public ThreadInfo MergeThread(ThreadInfo t)
    {
        if (Threads.TryGetValue(t.Id, out var old))
        {
            t.Following ??= old.Following;
            t.LastRead ??= old.LastRead;
        }
        Threads[t.Id] = t;
        // znaczek wątku pod wiadomością na kanale
        if (Windows.GetValueOrDefault(t.ChannelId)?.Items.Find(x => x.Id == t.MessageId) is { } starter)
        {
            starter.Thread = JsonSerializer.SerializeToElement(t, Json.Options);
            Raise(ChangeKind.MessageUpdated, starter.Id, t.ChannelId, starter);
        }
        Raise(ChangeKind.Thread, t.Id, t.ChannelId);
        return t;
    }

    /// <summary>Wątki kanału, które obserwuję i mają coś nowego.</summary>
    public int UnreadThreadsIn(long cid) => Threads.Values.Count(t => t.ChannelId == cid && t.Unread && !t.Archived);

    public void MarkThreadReadLocal(long tid, long mid)
    {
        if (!Threads.TryGetValue(tid, out var t) || (t.LastRead ?? 0) >= mid) return;
        t.LastRead = mid;
        Raise(ChangeKind.Thread, tid, t.ChannelId);
    }

    // ───────────────────────── zdarzenia z WebSocket ─────────────────────────

    /// <summary>Użytkownicy w zdarzeniu: mapa {id: User} albo lista.</summary>
    void AddUsers(JsonElement users)
    {
        if (users.ValueKind == JsonValueKind.Object)
            foreach (var p in users.EnumerateObject()) { if (p.Value.To<User>() is { } u) Users[u.Id] = u; }
        else if (users.ValueKind == JsonValueKind.Array)
            foreach (var x in users.EnumerateArray()) { if (x.To<User>() is { } u) Users[u.Id] = u; }
    }

    public void Apply(GatewayEvent ev)
    {
        var e = ev.Data;
        switch (ev.Type)
        {
            case "message_create":
            {
                var m = e.Prop("message").To<Message>();
                if (m is null) break;
                StopTyping(m.ChannelId, m.AuthorId);
                if (m.ThreadId is long mt)
                {
                    StopTyping(m.Key, m.AuthorId);
                    Upsert(m);
                    if (m.AuthorId == Me.Id) MarkThreadReadLocal(mt, m.Id);
                    else if (MentionsMe(m) && !Blocked.Contains(m.AuthorId)) { Mentions[m.ChannelId] = MentionsIn(m.ChannelId) + 1; Raise(ChangeKind.Reads, m.ChannelId, m.ChannelId); }
                    break;
                }
                if (DmById(m.ChannelId) is { HiddenAt: not null } hd) hd.HiddenAt = null;
                Upsert(m);
                if (m.AuthorId == Me.Id) MarkReadLocal(m.ChannelId, m.Id);
                else
                {
                    if (MentionsMe(m) && !Blocked.Contains(m.AuthorId)) Mentions[m.ChannelId] = MentionsIn(m.ChannelId) + 1;
                    Raise(ChangeKind.Reads, m.ChannelId, m.ChannelId);
                }
                break;
            }
            case "message_update":
            {
                var m = e.Prop("message").To<Message>();
                if (m is null) break;
                var w = Windows.GetValueOrDefault(m.Key);
                var i = w?.Items.FindIndex(x => x.Id == m.Id) ?? -1;
                if (w is not null && i >= 0) { w.Items[i] = m; Raise(ChangeKind.MessageUpdated, m.Id, m.Key, m); }
                break;
            }
            case "message_delete":
            {
                if (e.Long("id") is long id && e.Long("channelId") is long cid) RemoveLocal(e.Long("threadId") is long dt ? -dt : cid, id);
                break;
            }
            case "reaction_update":
            {
                if (e.Long("messageId") is not long mid || e.Long("channelId") is not long cid) break;
                // reakcja w wątku przychodzi z id kanału-rodzica — szukamy też w otwartych wątkach
                var key = cid;
                var m = Windows.GetValueOrDefault(cid)?.Items.Find(x => x.Id == mid);
                if (m is null)
                    foreach (var (k, tw) in Windows)
                        if (k < 0 && tw.Items.Find(x => x.Id == mid) is { } tm) { m = tm; key = k; break; }
                if (m is null) break;
                m.Reactions = e.Prop("reactions").To<List<Reaction>>() ?? [];
                Raise(ChangeKind.MessageUpdated, mid, key, m);
                break;
            }
            case "typing":
                if (e.Long("channelId") is long tc && e.Long("userId") is long tu) Typing(e.Long("threadId") is long tt ? -tt : tc, tu);
                break;
            case "read_state":
                if (e.Long("channelId") is long rc && e.Long("messageId") is long rm) MarkReadLocal(rc, rm);
                break;
            case "presence":
            {
                if (e.Long("userId") is not long uid || U(uid) is not { } u) break;
                if (e.Bool("online") is bool on) u.Online = on;
                if (e.Str("status") is string st) u.Status = st;
                if (e.TryGetProperty("customStatus", out _)) u.CustomStatus = e.Str("customStatus");
                if (e.TryGetProperty("activity", out var act)) u.Activity = act.To<Activity>();
                if (e.TryGetProperty("game", out var g)) u.Game = g.Clone();
                Raise(ChangeKind.Presence, uid);
                break;
            }
            case "user_update":
            {
                var u = e.Prop("user").To<User>();
                if (u is null) break;
                if (u.Id == Me.Id) { Me = u; Raise(ChangeKind.Me, u.Id); }
                Users[u.Id] = u;
                Raise(ChangeKind.User, u.Id);
                break;
            }
            case "friend_update":
            {
                var f = e.Prop("friend").To<Friend>();
                if (f is null) break;
                Friends[f.UserId] = f;
                if (e.Prop("user").To<User>() is { } fu) Users[fu.Id] = fu;
                Raise(ChangeKind.Friends, f.UserId);
                break;
            }
            case "friend_remove":
                if (e.Long("userId") is long fr) { Friends.Remove(fr); Raise(ChangeKind.Friends, fr); }
                break;
            case "server_create":
            {
                var s = e.Prop("server").To<Server>();
                if (s is null) break;
                AddUsers(e.Prop("users"));
                foreach (var v in e.Prop("voice").To<Dictionary<long, List<VoiceMember>>>() ?? []) Voice[v.Key] = v.Value;
                Servers.RemoveAll(x => x.Id == s.Id);
                Servers.Add(s);
                Reindex();
                Raise(ChangeKind.Servers, s.Id);
                break;
            }
            case "server_update":
            {
                var s = e.Prop("server").To<Server>();
                if (s is null) break;
                var i = Servers.FindIndex(x => x.Id == s.Id);
                if (i >= 0) Servers[i] = s; else Servers.Add(s);
                Reindex();
                Raise(ChangeKind.Server, s.Id);
                break;
            }
            case "server_delete":
                if (e.Long("serverId") is long sd) { Servers.RemoveAll(x => x.Id == sd); Reindex(); Raise(ChangeKind.Servers, sd); }
                break;
            case "member_add":
            {
                if (e.Long("serverId") is not long sid || ServerById(sid) is not { } s) break;
                var m = e.Prop("member").To<Member>();
                if (m is null) break;
                s.Members.RemoveAll(x => x.UserId == m.UserId);
                s.Members.Add(m);
                if (e.Prop("user").To<User>() is { } mu) Users[mu.Id] = mu;
                Raise(ChangeKind.Members, sid);
                break;
            }
            case "member_update":
            {
                if (e.Long("serverId") is not long sid || ServerById(sid) is not { } s) break;
                var m = e.Prop("member").To<Member>();
                if (m is null) break;
                var i = s.Members.FindIndex(x => x.UserId == m.UserId);
                if (i >= 0) s.Members[i] = m; else s.Members.Add(m);
                Raise(ChangeKind.Members, sid);
                break;
            }
            case "member_remove":
                if (e.Long("serverId") is long rs && e.Long("userId") is long ru && ServerById(rs) is { } srv)
                { srv.Members.RemoveAll(x => x.UserId == ru); Raise(ChangeKind.Members, rs); }
                break;
            case "channel_create":
            case "channel_update":
            {
                var c = e.Prop("channel").To<Channel>();
                if (c is null || ServerById(c.ServerId) is not { } s) break;
                var i = s.Channels.FindIndex(x => x.Id == c.Id);
                if (i >= 0) s.Channels[i] = c; else s.Channels.Add(c);
                Reindex();
                Raise(ChangeKind.Server, s.Id);
                break;
            }
            case "channel_delete":
            {
                if (e.Long("channelId") is not long cid) break;
                var s = ServerOfChannel(cid) ?? (e.Long("serverId") is long ds ? ServerById(ds) : null);
                if (s is null) break;
                s.Channels.RemoveAll(x => x.Id == cid);
                Windows.Remove(cid);
                Reindex();
                Raise(ChangeKind.Server, s.Id);
                break;
            }
            case "perms_update":
                if (e.Long("serverId") is long ps && ServerById(ps) is { } psrv) { psrv.MyPerms = e.Prop("myPerms").To<MyPerms>(); Raise(ChangeKind.Server, ps); }
                break;
            case "dm_create":
            case "dm_update":
            {
                var d = e.Prop("dm").To<Dm>();
                if (d is null) break;
                AddUsers(e.Prop("users"));
                var i = Dms.FindIndex(x => x.Id == d.Id);
                if (i >= 0) Dms[i] = d; else Dms.Add(d);
                Raise(ChangeKind.Dms, d.Id);
                break;
            }
            case "dm_hidden":
                if (e.Long("dmId") is long hid && DmById(hid) is { } hdm) { hdm.HiddenAt = e.Long("hiddenAt"); Raise(ChangeKind.Dms, hid); }
                break;
            case "dm_remove":
                if (e.Long("dmId") is long dr) { Dms.RemoveAll(x => x.Id == dr); Windows.Remove(dr); Raise(ChangeKind.Dms, dr); }
                break;
            case "voice_state":
                if (e.Long("channelId") is long vc) { Voice[vc] = e.Prop("members").To<List<VoiceMember>>() ?? []; Raise(ChangeKind.Voice, vc, vc); }
                break;
            case "notify_update":
            {
                if (e.Long("id") is not long nid) break;
                var dict = e.Str("scope") switch { "server" => Notify.Servers, "channel" => Notify.Channels, "dm" => Notify.Dms, _ => null };
                if (dict is null) break;
                var lvl = e.Str("level"); var mu = e.Long("mutedUntil");
                if (lvl is null && mu is null) dict.Remove(nid); else dict[nid] = new NotifySetting { Level = lvl, MutedUntil = mu };
                Raise(ChangeKind.Notify, nid);
                break;
            }
            case "block_update":
                Blocked.Clear(); Blocked.UnionWith(e.Prop("blocked").To<List<long>>() ?? []);
                Raise(ChangeKind.Blocked);
                break;
            case "event_create":
            case "event_update":
            {
                var ev2 = e.Prop("event").To<ServerEvent>();
                if (ev2 is null || ServerById(ev2.ServerId) is not { } es) break;
                es.Events.RemoveAll(x => x.Id == ev2.Id);
                if (ev2.Open) es.Events.Add(ev2);
                Raise(ChangeKind.Server, es.Id);
                break;
            }
            case "event_delete":
            {
                var ev3 = e.Prop("event").To<ServerEvent>();
                var id3 = ev3?.Id ?? e.Long("eventId") ?? e.Long("id") ?? 0;
                foreach (var s3 in Servers) if (s3.Events.RemoveAll(x => x.Id == id3) > 0) Raise(ChangeKind.Server, s3.Id);
                break;
            }
            case "watch_state":
            {
                if (e.Long("channelId") is not long wc) break;
                var w = e.Prop("watch").To<WatchState>();
                if (w is null) Watches.Remove(wc); else Watches[wc] = w;
                Raise(ChangeKind.Watch, wc, wc);
                break;
            }
            case "thread_update":
                if (e.Prop("thread").To<ThreadInfo>() is { } t) MergeThread(t);
                break;
            case "thread_delete":
            {
                if (e.Long("threadId") is not long dtid) break;
                Threads.Remove(dtid);
                Windows.Remove(-dtid);
                if (e.Long("channelId") is long dc && e.Long("messageId") is long dm && Windows.GetValueOrDefault(dc)?.Items.Find(x => x.Id == dm) is { } starter)
                { starter.Thread = default; Raise(ChangeKind.MessageUpdated, dm, dc, starter); }
                Raise(ChangeKind.Thread, dtid, e.Long("channelId") ?? 0);
                break;
            }
            case "thread_follow":
            {
                if (e.Long("threadId") is not long ftid) break;
                if (!Threads.TryGetValue(ftid, out var ft))
                    Threads[ftid] = ft = new ThreadInfo { Id = ftid, ChannelId = e.Long("channelId") ?? 0 };
                ft.Following = e.Bool("following") == true;
                ft.LastRead = e.Long("lastRead");
                Raise(ChangeKind.Thread, ftid, ft.ChannelId);
                break;
            }
            case "announcement":
                Announcement = e.Str("text") is null ? null : e.To<Announcement>();
                Raise(ChangeKind.Announcement);
                break;
        }
    }
}
