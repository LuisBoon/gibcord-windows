using System.Text.Json;

namespace GibCord.Core;

public interface ITokenStore
{
    string? Load();
    void Save(string? token);
}

public sealed class MemoryTokenStore(string? token = null) : ITokenStore
{
    string? _t = token;
    public string? Load() => _t;
    public void Save(string? token) => _t = token;
}

/// <summary>
/// Spina wszystko: REST, WebSocket, stan i pamięć podręczną. Okno woła metody tej klasy
/// i słucha <see cref="Store.Changed"/>. Zmiany stanu zawsze idą przez <c>ui</c> (wątek okna).
/// </summary>
public sealed partial class GibCordClient : IAsyncDisposable
{
    readonly ITokenStore _tokens;
    readonly DiskCache? _cache;
    readonly Action<Action> _ui;
    readonly HashSet<long> _fresh = [];
    readonly HashSet<long> _loadingOlder = [];
    readonly Dictionary<long, long> _typingSent = [];
    readonly Dictionary<long, CancellationTokenSource> _readTimers = [];
    /// <summary>Wiadomości, które nie poszły przez brak internetu — wyślą się same po powrocie połączenia (ten sam nonce = bez duplikatów).</summary>
    readonly List<Message> _outbox = [];
    readonly HashSet<string> _cancelled = [];
    Timer? _outboxTimer;
    Gateway? _gw;
    bool _stateInFlight;
    bool _visible = true;
    int _session;

    public Store Store { get; } = new();
    public GibCordApi Api { get; }
    public Info? Info { get; private set; }
    public GatewayStatus Status { get; private set; } = GatewayStatus.Offline;
    public TimeSpan? Rtt => _gw?.Rtt;

    /// <summary>Kanał otwarty w oknie (0 = żaden, np. Znajomi).</summary>
    public long ActiveChannelId { get; private set; }
    /// <summary>Wątek otwarty z boku (0 = żaden).</summary>
    public long ActiveThreadId { get; private set; }
    /// <summary>Okno jest na wierzchu i aktywne — wiadomości na otwartym kanale są od razu przeczytane.</summary>
    public bool WindowActive { get; private set; } = true;

    public event Action<GatewayStatus, TimeSpan?>? StatusChanged;
    public event Action<Message>? Notify;
    /// <summary>Ktoś mnie oznaczył (każda wzmianka, niezależnie od powiadomień) — do skrzynki „Wzmianki”.</summary>
    public event Action<Message>? Mentioned;
    public event Action<User>? FriendRequest;
    public event Action<string>? SessionEnded;
    /// <summary>Zmiana na tablicy pomysłów (id kanału).</summary>
    public event Action<long>? IdeaEvent;
    /// <summary>Urodziny znajomego (albo moje — id = ja).</summary>
    public event Action<long>? Birthday;
    /// <summary>Nowe zgłoszenie na serwerze, który moderuję (albo zatrzymana wiadomość automoderacji).</summary>
    public event Action<JsonElement>? ReportCreated;
    /// <summary>Przypomnienie o wydarzeniu (15 min przed albo start: inMinutes = 0).</summary>
    public event Action<ServerEvent, int>? EventReminder;
    /// <summary>Serwer wymaga potwierdzenia adresu e-mail (WS 4005) — aplikacja pokazuje ekran z linkiem do wysłania.</summary>
    public event Action? EmailGateRequired;
    public event Action<string>? Error;

    public GibCordClient(Uri baseUri, ITokenStore tokens, DiskCache? cache, Action<Action> ui, HttpMessageHandler? handler = null)
    {
        _tokens = tokens;
        _cache = cache;
        _ui = ui;
        Api = new GibCordApi(baseUri, handler) { Token = tokens.Load() };
    }

    public bool HasToken => !string.IsNullOrEmpty(_tokens.Load());

    /// <summary>Tryb pokazowy: bez sieci, wszystko tylko w pamięci.</summary>
    public bool Offline { get; init; }

    // ───────────────────────── logowanie ─────────────────────────

    public async Task<LoginResult> Login(string username, string password)
    {
        var r = await Api.Login(username.Trim().TrimStart('@'), password);
        if (r.Token is not null) Accept(r.Token);
        return r;
    }

    public async Task<LoginResult> LoginMfa(string ticket, string code)
    {
        var r = await Api.LoginMfa(ticket, code);
        if (r.Token is not null) Accept(r.Token);
        return r;
    }

    public async Task<LoginResult> Register(string username, string password, string? displayName, string? email)
    {
        var r = await Api.Register(username, password, displayName, email);
        if (r.Token is not null) Accept(r.Token);
        return r;
    }

    public async Task<LoginResult> RedeemHandoff(string code)
    {
        var r = await Api.RedeemHandoff(code);
        if (r.Token is not null) Accept(r.Token);
        return r;
    }

    void Accept(string token)
    {
        _tokens.Save(token);
        Api.Token = token;
    }

    public async Task Logout()
    {
        try { await Api.Logout(); } catch { }
        await Stop();
        _tokens.Save(null);
        Api.Token = null;
        _cache?.Clear();
        ResetLocal();
    }

    /// <summary>Po wylogowaniu: stan, kolejka niewysłanych i otwarte kanały poprzedniego konta znikają.</summary>
    void ResetLocal()
    {
        lock (_outbox) { _outbox.Clear(); _cancelled.Clear(); }
        var t = _outboxTimer; _outboxTimer = null; t?.Dispose();
        _fresh.Clear();
        ActiveChannelId = 0;
        ActiveThreadId = 0;
        _ui(Store.Reset);
    }

    // ───────────────────────── start i połączenie ─────────────────────────

    /// <summary>Start po zalogowaniu: stan z dysku od razu, potem WebSocket i świeży stan z serwera.</summary>
    public async Task Start()
    {
        var token = _tokens.Load() ?? throw new InvalidOperationException("Brak tokenu");
        Api.Token = token;
        var session = ++_session;
        if (!Store.Loaded && _cache?.LoadState() is { } cached) _ui(() => Store.Load(cached));
        // drugi Start (np. podwójne „Już potwierdziłem”) nie może zostawić osieroconego połączenia
        var old = _gw; _gw = null;
        if (old is not null) _ = old.DisposeAsync().AsTask();
        _gw = new Gateway(Api.BaseUri, token);
        _gw.Event += ev => _ui(() => { if (session == _session) OnEvent(ev); });
        _gw.StatusChanged += (s, d) => _ui(() => { if (session == _session) OnStatus(s, d); });
        _gw.Start();
        _ = LoadInfo();
        _ = Fallback(session);
        await Task.CompletedTask;
    }

    /// <summary>Gdy WebSocket nie wstanie szybko (np. blokada w sieci), stan i tak przyjdzie przez REST.</summary>
    async Task Fallback(int session)
    {
        await Task.Delay(4000);
        if (session == _session && Status != GatewayStatus.Connected) await RefreshState();
    }

    async Task LoadInfo()
    {
        try { Info = await Api.GetInfo(); } catch { }
    }

    public async Task Stop()
    {
        _session++;
        var wasInVoice = VoiceChannelId != 0;
        VoiceChannelId = 0;
        if (_voice is not null) await _voice.DisconnectAsync();
        // silnik w stanie „Łączenie…” nie zgłasza zmiany — bez tego mikrofon zostałby otwarty
        if (wasInVoice) _ui(() => VoiceChanged?.Invoke());
        var gw = _gw; _gw = null;
        if (gw is not null) await gw.DisposeAsync();
        Status = GatewayStatus.Offline;
    }

    void OnStatus(GatewayStatus s, TimeSpan? delay)
    {
        Status = s;
        StatusChanged?.Invoke(s, delay);
        if (s == GatewayStatus.SessionEnded) EndSession("Sesja wygasła albo wylogowano Cię na innym urządzeniu. Zaloguj się ponownie.");
        else if (s == GatewayStatus.Suspended) EndSession("To konto jest zawieszone.");
        else if (s == GatewayStatus.EmailGate) { _ = Stop(); EmailGateRequired?.Invoke(); }
    }

    void EndSession(string why)
    {
        _tokens.Save(null);
        Api.Token = null;
        _cache?.Clear();
        _ = Stop();
        ResetLocal();
        SessionEnded?.Invoke(why);
    }

    /// <summary>Świeży stan z serwera (po każdym połączeniu WebSocket — API.md, sekcja 7).</summary>
    public async Task RefreshState()
    {
        // „hello” w trakcie zapytania (np. z Fallback) — po nim pobieramy jeszcze raz, bo tamten stan może być sprzed połączenia
        if (_stateInFlight) { _stateAgain = true; return; }
        _stateInFlight = true;
        _stateAgain = false;
        _during = [];
        try
        {
            var (st, raw) = await Api.GetStateRaw();
            _ui(() =>
            {
                var during = _during;
                _during = null;
                Store.Load(st);
                // zdarzenia, które przyszły w trakcie pobierania, są świeższe niż zrzut — nakładamy je jeszcze raz
                if (during is not null)
                    foreach (var ev in during)
                        try { Store.Apply(ev); } catch { } // uszkodzone zdarzenie nie może zatrzymać wczytania
                _fresh.Clear();
                if (ActiveChannelId != 0) _ = OpenChannel(ActiveChannelId);
                if (ActiveThreadId != 0) _ = LoadThread(ActiveThreadId);
            });
            _cache?.SaveState(raw);
            if (_gw is not null) await _gw.Send(new { t = "visibility", visible = _visible });
        }
        catch (ApiException ex) when (ex.IsAuth) { _during = null; _ui(() => EndSession("Sesja wygasła — zaloguj się ponownie.")); }
        catch (Exception ex) { _during = null; _ui(() => Error?.Invoke(Friendly(ex))); }
        finally
        {
            _stateInFlight = false;
            if (_stateAgain) { _stateAgain = false; _ = RefreshState(); }
        }
    }

    List<GatewayEvent>? _during;
    bool _stateAgain;

    /// <summary>Zdarzenia, które można bezpiecznie nałożyć drugi raz na świeży zrzut stanu.</summary>
    static readonly HashSet<string> Replayable =
    [
        "message_create", "message_update", "message_delete", "reaction_update", "read_state", "presence", "user_update",
        "voice_state", "dm_create", "dm_update", "dm_hidden", "dm_remove", "friend_update", "friend_remove",
        "member_update", "channel_update", "server_update", "notify_update", "block_update", "thread_update", "watch_state",
    ];

    void OnEvent(GatewayEvent ev)
    {
        if (ev.Type == "hello") { _ = RefreshState(); RejoinVoiceAfterReconnect(); _ = FlushOutbox(); return; }
        if (_during is not null && Replayable.Contains(ev.Type) && _during.Count < 2000) _during.Add(ev);
        Store.Apply(ev);
        OnVoiceEvent(ev);
        switch (ev.Type)
        {
            case "message_create":
            {
                var m = ev.Data.Prop("message").To<Message>();
                if (m is null) break;
                if (m.ChannelId == ActiveChannelId && WindowActive && m.AuthorId != Store.Me.Id && m.ThreadId is null) ScheduleRead(m.ChannelId);
                if (m.ThreadId is long mt && mt == ActiveThreadId && WindowActive && m.AuthorId != Store.Me.Id) ScheduleRead(m.Key);
                if (Store.ShouldNotify(m, ActiveChannelId, WindowActive, ActiveThreadId)) Notify?.Invoke(m);
                if (m.AuthorId != Store.Me.Id && m.Type == "default" && !Store.Blocked.Contains(m.AuthorId) && Store.MentionsMe(m)) Mentioned?.Invoke(m);
                SaveSoon(m.Key);
                break;
            }
            case "message_update":
            case "message_delete":
            case "reaction_update":
                if ((ev.Data.Long("channelId") ?? ev.Data.Prop("message").Long("channelId")) is long cid) SaveSoon(cid);
                break;
            case "friend_update":
                if (ev.Data.Prop("friend").Str("status") == "incoming" && ev.Data.Prop("user").To<User>() is { } u) FriendRequest?.Invoke(u);
                break;
            case "idea":
                if (ev.Data.Long("channelId") is long ic) IdeaEvent?.Invoke(ic);
                break;
            case "birthday":
                if (ev.Data.Long("userId") is long bu) Birthday?.Invoke(bu);
                break;
            case "birthday_self":
                Birthday?.Invoke(Store.Me.Id);
                break;
            case "report_create":
                if (ev.Data.Prop("report") is { ValueKind: JsonValueKind.Object } rep) ReportCreated?.Invoke(rep);
                break;
            case "event_reminder":
                if (ev.Data.Prop("event").To<ServerEvent>() is { } er) EventReminder?.Invoke(er, (int)(ev.Data.Long("inMinutes") ?? 0));
                break;
        }
    }

    void SaveSoon(long cid)
    {
        if (cid > 0 && _cache is not null && Store.Windows.TryGetValue(cid, out var w) && w.AtTail) _cache.SaveMessages(cid, w.Items);
    }

    // ───────────────────────── kanały i wiadomości ─────────────────────────

    /// <summary>Otwiera kanał: wiadomości z dysku od razu, potem najnowsze z serwera.</summary>
    public async Task OpenChannel(long cid)
    {
        ActiveChannelId = cid;
        if (cid == 0) return;
        if (Offline) { if (Store.IsUnread(cid)) Store.MarkReadLocal(cid, Store.LastIdOf(cid)); return; }
        var w = Store.Window(cid);
        if (!_fresh.Contains(cid) || !w.AtTail)
        {
            if (w.Items.Count == 0 && _cache?.LoadMessages(cid) is { Count: > 0 } cached)
                Store.SetWindow(cid, cached, hasOlder: true, atTail: true, fromCache: true);
            try
            {
                var msgs = await Api.GetMessages(cid, limit: 50);
                _ui(() =>
                {
                    Store.SetWindow(cid, msgs, hasOlder: msgs.Count >= 50);
                    _fresh.Add(cid);
                });
                SaveSoon(cid);
            }
            catch (ApiException ex) when (ex.IsAuth) { _ui(() => EndSession("Sesja wygasła — zaloguj się ponownie.")); return; }
            catch (Exception ex) { _ui(() => Error?.Invoke(Friendly(ex))); }
        }
        if (WindowActive && ActiveChannelId == cid) ScheduleRead(cid, 0); // w międzyczasie mógł się otworzyć inny kanał
    }

    public void CloseChannel() => ActiveChannelId = 0;

    /// <summary>Starsze wiadomości (przewinięcie do góry). Zwraca liczbę dociągniętych.</summary>
    public async Task<int> LoadOlder(long cid)
    {
        var w = Store.Window(cid);
        if (Offline || !w.HasOlder || !_loadingOlder.Add(cid)) return 0;
        try
        {
            var first = w.Items.FirstOrDefault(x => x.Send == SendState.Sent && x.Id > 0);
            if (first is null) return 0;
            var (ch, th) = Where(cid);
            var msgs = await Api.GetMessages(ch, before: first.Id, limit: 50, thread: th);
            _ui(() => Store.PrependOlder(cid, msgs, msgs.Count >= 50));
            return msgs.Count;
        }
        catch (Exception ex) { _ui(() => Error?.Invoke(Friendly(ex))); return 0; }
        finally { _loadingOlder.Remove(cid); }
    }

    /// <summary>Po skoku w przeszłość: doczytuje nowsze wiadomości przy przewijaniu w dół. Zwraca liczbę dopisanych.</summary>
    public async Task<int> LoadNewer(long cid)
    {
        var w = Store.Window(cid);
        if (Offline || w.AtTail || !_loadingOlder.Add(-cid - 1_000_000_000_000)) return 0;
        try
        {
            var last = w.Items.LastOrDefault(x => x.Send == SendState.Sent && x.Id > 0);
            if (last is null) return 0;
            var (ch, th) = Where(cid);
            var msgs = await Api.GetMessages(ch, around: last.Id, limit: 100, thread: th);
            var newer = msgs.Where(x => x.Id > last.Id).ToList();
            var newest = cid < 0 ? Store.ThreadById(-cid)?.LastMessageId ?? 0 : Store.LastIdOf(cid);
            // serwer przy around=X&limit=100 daje najwyżej 49 nowszych niż X — koniec poznajemy po najnowszej znanej wiadomości
            var atTail = newer.Count == 0 || newer[^1].Id >= newest;
            _ui(() => Store.AppendNewer(cid, newer, atTail));
            if (atTail && WindowActive && (cid < 0 ? -cid == ActiveThreadId : cid == ActiveChannelId)) ScheduleRead(cid, 0);
            return newer.Count;
        }
        catch (Exception ex) { _ui(() => Error?.Invoke(Friendly(ex))); return 0; }
        finally { _loadingOlder.Remove(-cid - 1_000_000_000_000); }
    }

    /// <summary>Skok do wiadomości (np. odpowiedź) — wczytuje jej okolicę, gdy nie ma jej na liście.</summary>
    public async Task<bool> JumpTo(long cid, long messageId)
    {
        if (Store.Window(cid).Items.Any(x => x.Id == messageId)) return true;
        try
        {
            var (ch, th) = Where(cid);
            var msgs = await Api.GetMessages(ch, around: messageId, limit: 50, thread: th);
            var last = cid < 0 ? Store.ThreadById(-cid)?.LastMessageId ?? 0 : Store.LastIdOf(cid);
            _ui(() => Store.SetWindow(cid, msgs, hasOlder: true, atTail: msgs.Count > 0 && msgs[^1].Id >= last));
            return msgs.Any(x => x.Id == messageId);
        }
        catch (Exception ex) { _ui(() => Error?.Invoke(Friendly(ex))); return false; }
    }

    /// <summary>Wraca na koniec rozmowy po skoku w przeszłość.</summary>
    public async Task JumpToPresent(long cid)
    {
        _fresh.Remove(cid);
        Store.Window(cid).AtTail = false;
        if (cid < 0) await LoadThread(-cid); else await OpenChannel(cid);
    }

    /// <summary>Klucz listy → (kanał, wątek). Wątki mają klucz ujemny (−id wątku).</summary>
    (long Channel, long? Thread) Where(long key) => key < 0 ? (Store.ThreadById(-key)?.ChannelId ?? 0, -key) : (key, null);

    /// <summary>Wątek nieznany po starcie (np. odpowiedź z dymka, gdy aplikacja była zamknięta) — dociągamy jego dane.</summary>
    public async Task EnsureThread(long tid)
    {
        if (Store.ThreadById(tid) is not null) return;
        try { var (t, _) = await Api.GetThread(tid); _ui(() => Store.MergeThread(t)); } catch { }
    }

    // ───────────────────────── wątki ─────────────────────────

    /// <summary>Otwiera wątek z boku: dane wątku + pierwsza wiadomość, potem najnowsze odpowiedzi.</summary>
    public async Task<(ThreadInfo? Thread, Message? Starter, string? Error)> OpenThread(long tid)
    {
        ActiveThreadId = tid;
        if (Offline)
        {
            var ot = Store.ThreadById(tid);
            return (ot, ot is null ? null : Store.Window(ot.ChannelId).Items.Find(x => x.Id == ot.MessageId), null);
        }
        try
        {
            var (t, starter) = await Api.GetThread(tid);
            ThreadInfo? merged = null;
            _ui(() => merged = Store.MergeThread(t));
            await LoadThread(tid);
            return (merged ?? t, starter, null);
        }
        catch (ApiException ex) when (ex.IsAuth) { _ui(() => EndSession("Sesja wygasła — zaloguj się ponownie.")); return (null, null, ex.Message); }
        catch (Exception ex) { return (null, null, Friendly(ex)); }
    }

    async Task LoadThread(long tid)
    {
        var t = Store.ThreadById(tid);
        if (t is null) return;
        try
        {
            var msgs = await Api.GetMessages(t.ChannelId, limit: 50, thread: tid);
            _ui(() => { Store.SetWindow(-tid, msgs, hasOlder: msgs.Count >= 50); _fresh.Add(-tid); });
            if (WindowActive && ActiveThreadId == tid) ScheduleRead(-tid, 0);
        }
        catch (Exception ex) { _ui(() => Error?.Invoke(Friendly(ex))); }
    }

    public void CloseThread() { if (ActiveThreadId != 0 && !Offline) Store.Windows.Remove(-ActiveThreadId); ActiveThreadId = 0; }

    public async Task<(ThreadInfo? Thread, string? Error)> CreateThread(long messageId, string? name)
    {
        try { var t = await Api.CreateThread(messageId, name); ThreadInfo? m = null; _ui(() => m = Store.MergeThread(t)); return (m ?? t, null); }
        catch (Exception ex) { return (null, Friendly(ex)); }
    }

    public async Task<string?> FollowThread(long tid, bool on)
    {
        try { var t = await Api.FollowThread(tid, on); _ui(() => Store.MergeThread(t)); return null; }
        catch (Exception ex) { return Friendly(ex); }
    }

    public async Task<string?> PatchThread(long tid, object changes)
    {
        try { var t = await Api.PatchThread(tid, changes); _ui(() => Store.MergeThread(t)); return null; }
        catch (Exception ex) { return Friendly(ex); }
    }

    public async Task<(List<ThreadInfo> Threads, int Archived, string? Error)> ListThreads(long cid, bool archived = false)
    {
        if (Offline) return (Store.Threads.Values.Where(t => t.ChannelId == cid).ToList(), 0, null);
        try
        {
            var (list, n) = await Api.GetThreads(cid, archived);
            var merged = new List<ThreadInfo>();
            _ui(() => { foreach (var t in list) merged.Add(Store.MergeThread(t)); });
            return (merged.Count == list.Count ? merged : list, n, null);
        }
        catch (Exception ex) { return ([], 0, Friendly(ex)); }
    }

    public void SetWindowActive(bool active)
    {
        if (WindowActive == active) return;
        WindowActive = active;
        if (active && ActiveChannelId != 0) ScheduleRead(ActiveChannelId, 0);
        if (active && ActiveThreadId != 0) ScheduleRead(-ActiveThreadId, 0);
    }

    /// <summary>Okno widoczne / schowane — serwer wysyła push na telefon tylko, gdy żadne okno nie jest widoczne.</summary>
    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        _ = _gw?.Send(new { t = "visibility", visible });
    }

    void ScheduleRead(long cid, int delayMs = 400)
    {
        if (cid < 0) { ScheduleThreadRead(-cid); return; }
        if (Offline || (!Store.IsUnread(cid) && Store.MentionsIn(cid) == 0)) return;
        if (_readTimers.TryGetValue(cid, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _readTimers[cid] = cts;
        _ = Task.Run(async () =>
        {
            try { if (delayMs > 0) await Task.Delay(delayMs, cts.Token); } catch { return; }
            long last = 0;
            var tcs = new TaskCompletionSource();
            _ui(() =>
            {
                last = Store.LastIdOf(cid);
                if (last > 0) Store.MarkReadLocal(cid, last);
                tcs.TrySetResult();
            });
            await tcs.Task;
            if (last > 0) { try { await Api.MarkRead(cid, last); } catch { } }
        });
    }

    public void MarkRead(long cid) => ScheduleRead(cid, 0);

    /// <summary>Wątek: „przeczytane” zapisuje się tylko obserwującym (jak na stronie).</summary>
    void ScheduleThreadRead(long tid)
    {
        var t = Store.ThreadById(tid);
        if (t is null) return;
        var last = Store.Windows.GetValueOrDefault(-tid)?.Items.LastOrDefault(x => x.Send == SendState.Sent)?.Id ?? t.LastMessageId ?? 0;
        var before = t.LastRead ?? 0;
        Store.MarkThreadReadLocal(tid, last); // gasi też wzmianki z wątku w liczniku kanału
        if (Offline || t.Following != true || last <= before) return;
        _ = Task.Run(async () => { try { await Api.MarkThreadRead(tid, last); } catch { } });
    }

    /// <summary>Wysyła wiadomość. Na liście pojawia się od razu; nonce chroni przed podwójnym wysłaniem.</summary>
    /// <summary><paramref name="cid"/> = klucz listy: kanał / rozmowa, albo −id wątku.</summary>
    public async Task<string?> Send(long cid, string content, ReplyRef? replyTo = null, IReadOnlyList<Attachment>? attachments = null, Sticker? sticker = null)
    {
        content = content.Trim();
        if (content.Length == 0 && attachments is not { Count: > 0 } && sticker is null) return null;
        var nonce = Guid.NewGuid().ToString("N");
        var (ch, th) = Where(cid);
        var local = new Message
        {
            Id = -Environment.TickCount64, ChannelId = ch, ThreadId = th, AuthorId = Store.Me.Id, Content = content, CreatedAt = Format.NowMs(),
            Nonce = nonce, ReplyTo = replyTo, Attachments = attachments?.ToList() ?? [], Sticker = sticker,
        };
        Store.AddPending(local);
        return await Deliver(local);
    }

    async Task<string?> Deliver(Message local)
    {
        if (Offline)
        {
            await Task.Delay(300);
            var copy = Json.Deserialize<Message>(Json.Serialize(local))!;
            copy.Id = Math.Max(Store.LastIdOf(local.ChannelId), Store.Window(local.Key).Items.Where(x => x.Send == SendState.Sent).Select(x => x.Id).DefaultIfEmpty().Max()) + 1;
            _ui(() => { Store.Upsert(copy, live: false); if (copy.ThreadId is null) Store.MarkReadLocal(copy.ChannelId, copy.Id); });
            return null;
        }
        try
        {
            var m = await Api.SendMessage(local.ChannelId, local.Content, local.ReplyTo?.Id, local.Attachments.Select(a => a.Id).ToList(), local.Nonce, local.ThreadId, local.Sticker?.Id);
            _ui(() =>
            {
                Store.Upsert(m, live: false);
                if (m.ThreadId is long mt) Store.MarkThreadReadLocal(mt, m.Id); else Store.MarkReadLocal(m.ChannelId, m.Id);
            });
            SaveSoon(local.Key);
            bool more;
            lock (_outbox) more = _outbox.Count > 0;
            if (more) _ = FlushOutbox(); // internet wrócił — wyślij też te, które czekały
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && Status != GatewayStatus.Connected))
        {
            // brak sieci: zostaje szara z „czeka na internet” i pójdzie sama po powrocie połączenia
            lock (_outbox) if (!_outbox.Contains(local)) _outbox.Add(local);
            _ui(() => Store.FailPending(local.Key, local.Nonce!, queued: true));
            // ponowna próba co 10 s (gdy WebSocket przetrwał krótką awarię, „hello” nie przyjdzie)
            _outboxTimer ??= new Timer(_ => _ = FlushOutbox(), null, 10_000, 10_000);
            return null;
        }
        catch (Exception ex)
        {
            _ui(() => Store.FailPending(local.Key, local.Nonce!));
            return Friendly(ex);
        }
    }

    /// <summary>Po powrocie połączenia: kolejka wiadomości wysyłana po kolei.</summary>
    async Task FlushOutbox()
    {
        List<Message> items;
        lock (_outbox)
        {
            items = [.. _outbox];
            _outbox.Clear();
            if (items.Count == 0) { _outboxTimer?.Dispose(); _outboxTimer = null; return; }
        }
        foreach (var m in items)
        {
            lock (_outbox) if (m.Nonce is not null && _cancelled.Remove(m.Nonce)) continue; // „Anuluj” w trakcie
            m.Send = SendState.Pending;
            _ui(() => Store.Upsert(m, live: false));
            await Deliver(m);
        }
    }

    public Task<string?> Retry(Message failed)
    {
        failed.Send = SendState.Pending;
        Store.Upsert(failed, live: false);
        return Deliver(failed);
    }

    public async Task<string?> Edit(Message m, string content)
    {
        try { var r = await Api.EditMessage(m.Id, content.Trim()); _ui(() => Store.Upsert(r, live: false)); return null; }
        catch (Exception ex) { return Friendly(ex); }
    }

    public async Task<string?> Delete(Message m)
    {
        if (m.Send != SendState.Sent)
        {
            lock (_outbox) { _outbox.Remove(m); if (m.Nonce is not null && m.Send is SendState.Queued or SendState.Pending) _cancelled.Add(m.Nonce); }
            Store.RemoveLocal(m.Key, m.Id);
            return null;
        }
        try { await Api.DeleteMessage(m.Id); _ui(() => Store.RemoveLocal(m.Key, m.Id)); return null; }
        catch (Exception ex) { return Friendly(ex); }
    }

    public async Task<string?> ToggleReaction(Message m, string emoji)
    {
        var mine = m.Reactions.Find(r => r.Emoji == emoji)?.UserIds.Contains(Store.Me.Id) == true;
        try
        {
            if (mine) await Api.RemoveReaction(m.Id, emoji); else await Api.AddReaction(m.Id, emoji);
            return null;
        }
        catch (Exception ex) { return Friendly(ex); }
    }

    /// <summary>„pisze…” — najwyżej co 3 s na kanał (albo wątek: klucz ujemny).</summary>
    public void Typing(long cid)
    {
        var now = Environment.TickCount64;
        if (now - _typingSent.GetValueOrDefault(cid) < 3000) return;
        _typingSent[cid] = now;
        var (ch, th) = Where(cid);
        _ = _gw?.Send(th is long t ? new { t = "typing", channelId = ch, threadId = (long?)t } : new { t = "typing", channelId = ch, threadId = (long?)null });
    }

    public void StoppedTyping(long cid) => _typingSent.Remove(cid);

    public async Task<(Attachment? Att, string? Error)> Upload(Stream data, string name, string? mime, long size)
    {
        var max = Info?.Limits.UploadBytes ?? 25L * 1024 * 1024;
        if (size > max) return (null, $"Plik jest za duży (najwyżej {Format.Size(max)})");
        try { return (await Api.Upload(data, name, mime), null); }
        catch (Exception ex) { return (null, Friendly(ex)); }
    }

    // ───────────────────────── wyszukiwarka ─────────────────────────

    /// <summary>Szuka wiadomości. Osoby z wyników trafiają do stanu (żeby były nazwy i awatary).</summary>
    public async Task<(SearchResult? Result, string? Error)> Search(SearchQuery q, CancellationToken ct = default)
    {
        if (Offline)
        {
            // tryb pokazowy: szukanie w tym, co jest w pamięci
            var f = Fold(q.Text);
            var hits = Store.Windows.Values.SelectMany(w => w.Items).Where(m => m.Send == SendState.Sent && !m.IsSystem
                && (f.Length == 0 || Fold(m.Content).Contains(f)) && (q.AuthorId is null || m.AuthorId == q.AuthorId)
                && (q.ChannelId is null || m.ChannelId == q.ChannelId) && (q.ServerId is null || Store.ServerOfChannel(m.ChannelId)?.Id == q.ServerId)
                && (!q.Dms || Store.IsDm(m.ChannelId))
                && (q.Has switch { "image" => m.Attachments.Any(a => a.IsImage), "file" => m.Attachments.Count > 0, "link" => m.Content.Contains("http"), "pinned" => m.PinnedAt is not null, _ => true }))
                .DistinctBy(m => m.Id).OrderByDescending(m => m.Id).ToList();
            return (new SearchResult { Total = hits.Count, Messages = hits.Skip(q.Offset).Take(25).ToList() }, null);
        }
        try
        {
            var r = await Api.Search(q, ct);
            _ui(() => { foreach (var (id, u) in r.Users) if (!Store.Users.ContainsKey(id)) Store.Users[id] = u; });
            return (r, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, null); }
        catch (Exception ex) { return (null, Friendly(ex)); }
    }

    /// <summary>„od:@ola ma:zdjęcie tekst” → zapytanie (jak parseQuery na stronie).</summary>
    public SearchQuery ParseSearch(string text)
    {
        long? author = null; string? has = null;
        var words = new List<string>();
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var lw = w.ToLowerInvariant();
            if ((lw.StartsWith("od:") || lw.StartsWith("from:")) && w.Length > lw.IndexOf(':') + 1)
            {
                var name = Fold(w[(w.IndexOf(':') + 1)..].TrimStart('@'));
                var u = Store.Users.Values.FirstOrDefault(x => Fold(x.Username) == name || Fold(x.Name) == name);
                if (u is not null) { author = u.Id; continue; }
            }
            else if (lw.StartsWith("ma:") || lw.StartsWith("has:"))
            {
                var v = Fold(w[(w.IndexOf(':') + 1)..]);
                has = v.Contains("zdj") || v.Contains("obraz") || v.Contains("image") || v.Contains("foto") ? "image"
                    : v.Contains("plik") || v.Contains("file") ? "file" : v.Contains("link") ? "link" : v.Contains("przyp") || v.Contains("pin") ? "pinned" : null;
                continue;
            }
            words.Add(w);
        }
        return new SearchQuery(string.Join(' ', words), author, has);
    }

    public static string Fold(string s)
    {
        var n = s.ToLowerInvariant().Replace('ł', 'l').Normalize(System.Text.NormalizationForm.FormD);
        return new string(n.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    // ───────────────────────── znajomi, rozmowy, status ─────────────────────────

    public async Task<(long DmId, string? Error)> OpenDm(long userId)
    {
        var existing = Store.Dms.Find(d => !d.IsGroup && d.UserIds.Contains(userId) && d.UserIds.Contains(Store.Me.Id));
        if (existing is not null) { existing.HiddenAt = null; return (existing.Id, null); }
        try
        {
            var d = await Api.OpenDm(userId);
            _ui(() => Store.Apply(new GatewayEvent("dm_update", JsonSerializer.SerializeToElement(new { dm = d }, Json.Options))));
            return (d.Id, null);
        }
        catch (Exception ex) { return (0, Friendly(ex)); }
    }

    public async Task<string?> AddFriend(string username)
    {
        try { await Api.AddFriend(username); return null; } catch (Exception ex) { return Friendly(ex); }
    }
    public async Task<string?> AcceptFriend(long uid)
    {
        try { await Api.AcceptFriend(uid); return null; } catch (Exception ex) { return Friendly(ex); }
    }
    public async Task<string?> RemoveFriend(long uid)
    {
        try { await Api.RemoveFriend(uid); return null; } catch (Exception ex) { return Friendly(ex); }
    }

    public async Task<string?> SetStatus(string status, string? customStatus = null)
    {
        try
        {
            object body = customStatus is null ? new { status } : new { status, customStatus };
            var me = await Api.PatchMe(body);
            _ui(() => Store.Apply(new GatewayEvent("user_update", JsonSerializer.SerializeToElement(new { user = me }, Json.Options))));
            return null;
        }
        catch (Exception ex) { return Friendly(ex); }
    }

    public static string Friendly(Exception ex) => ex switch
    {
        ApiException a => a.Message,
        HttpRequestException => "Brak połączenia z GibCordem — sprawdź internet",
        TaskCanceledException => "Serwer nie odpowiedział na czas — spróbuj jeszcze raz",
        _ => "Coś poszło nie tak: " + ex.Message,
    };

    public async ValueTask DisposeAsync()
    {
        await Stop();
        Api.Dispose();
    }
}
