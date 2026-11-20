using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GibCord.Core;

/// <summary>Błąd z serwera: kod HTTP + komunikat po polsku (nadaje się do pokazania).</summary>
public sealed class ApiException(int status, string message, string? code = null, long? until = null, int? retryAfter = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public long? Until { get; } = until;
    public int? RetryAfter { get; } = retryAfter;
    public bool IsAuth => Status == 401;
}

/// <summary>REST /api/v1 z tokenem aplikacji (API.md, sekcja 2b).</summary>
public sealed class GibCordApi : IDisposable
{
    public const string UserAgent = "GibCord-Windows/6.0";
    readonly HttpClient _http;

    public Uri BaseUri { get; }
    public string? Token { get; set; }

    public GibCordApi(Uri baseUri, HttpMessageHandler? handler = null)
    {
        BaseUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            : new HttpClient(handler);
        _http.BaseAddress = BaseUri;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Add("X-DC-Client", "app");
    }

    /// <summary>Adresy plików w API są względne (/uploads/...). Zamienia je na pełne.</summary>
    public Uri? Abs(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var a) && (a.Scheme == "https" || a.Scheme == "http")) return a;
        return Uri.TryCreate(BaseUri, url, out var r) ? r : null;
    }

    async Task<JsonElement> SendRaw(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, "api/v1/" + path.TrimStart('/')) { Content = content };
        if (Token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        JsonElement body = default;
        if (bytes.Length > 0)
        {
            try { body = JsonDocument.Parse(bytes).RootElement.Clone(); }
            catch (JsonException) { if (res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode, "Serwer odpowiedział w złym formacie"); }
        }
        if (!res.IsSuccessStatusCode)
        {
            var msg = body.Str("error") ?? res.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Za dużo prób — poczekaj chwilę",
                HttpStatusCode.Unauthorized => "Sesja wygasła — zaloguj się ponownie",
                HttpStatusCode.NotFound => "Nie znaleziono",
                HttpStatusCode.RequestEntityTooLarge => "Plik jest za duży",
                >= HttpStatusCode.InternalServerError => "Serwer GibCorda chwilowo nie odpowiada",
                _ => $"Błąd {(int)res.StatusCode}",
            };
            int? retry = body.Long("retryAfter") is long r ? (int)r : null;
            throw new ApiException((int)res.StatusCode, msg, body.Str("code"), body.Long("until"), retry);
        }
        return body;
    }

    Task<JsonElement> Send(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        HttpContent? content = body is null ? null : new StringContent(JsonSerializer.Serialize(body, Json.Options), Encoding.UTF8, "application/json");
        return SendRaw(method, path, content, ct);
    }

    /// <summary>Surowy JSON z GET (np. lista gier do wykrywania).</summary>
    public Task<JsonElement> GetJson(string path, CancellationToken ct = default) => Send(HttpMethod.Get, path, ct: ct);

    async Task<T> Send<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var e = await Send(method, path, body, ct).ConfigureAwait(false);
        return e.To<T>() ?? throw new ApiException(500, "Pusta odpowiedź serwera");
    }

    // ── konto ──
    public Task<Info> GetInfo(CancellationToken ct = default) => Send<Info>(HttpMethod.Get, "info", ct: ct);
    public Task<LoginResult> Login(string username, string password, CancellationToken ct = default) =>
        Send<LoginResult>(HttpMethod.Post, "login", new { username, password }, ct);
    public Task<LoginResult> LoginMfa(string ticket, string code, CancellationToken ct = default) =>
        Send<LoginResult>(HttpMethod.Post, "login/mfa", new { ticket, code = code.Replace(" ", "").Replace("-", "") }, ct);
    public Task Logout(CancellationToken ct = default) => Send(HttpMethod.Post, "logout", ct: ct);
    public Task<LoginResult> Register(string username, string password, string? displayName, string? email, CancellationToken ct = default) =>
        Send<LoginResult>(HttpMethod.Post, "register", new { username = username.Trim().TrimStart('@'), password, displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(), email = string.IsNullOrWhiteSpace(email) ? null : email.Trim() }, ct);
    /// <summary>GET /api/me — działa też przy bramce e-mail (emailGate:true).</summary>
    public async Task<(User Me, bool EmailGate)> GetMe(CancellationToken ct = default)
    {
        var e = await Send(HttpMethod.Get, "me", ct: ct).ConfigureAwait(false);
        return (e.Prop("me").To<User>() ?? new User(), e.Bool("emailGate") == true);
    }
    public Task ResendEmail(CancellationToken ct = default) => Send(HttpMethod.Post, "email/resend", ct: ct);
    /// <summary>Przesiadka ze starej aplikacji: jednorazowy kod → sesja (bez wpisywania hasła).</summary>
    public Task<LoginResult> RedeemHandoff(string code, CancellationToken ct = default) =>
        Send<LoginResult>(HttpMethod.Post, "auth/handoff/redeem", new { code }, ct);
    public async Task<(StateResponse State, string Raw)> GetStateRaw(CancellationToken ct = default)
    {
        var e = await Send(HttpMethod.Get, "state", ct: ct).ConfigureAwait(false);
        return (e.To<StateResponse>() ?? throw new ApiException(500, "Pusty stan"), e.GetRawText());
    }
    public async Task<StateResponse> GetState(CancellationToken ct = default) => (await GetStateRaw(ct).ConfigureAwait(false)).State;
    public async Task<User> PatchMe(object changes, CancellationToken ct = default) =>
        (await Send(HttpMethod.Patch, "me", changes, ct).ConfigureAwait(false)).Prop("me").To<User>()!;
    public Task<User> GetUser(long id, CancellationToken ct = default) => Send<User>(HttpMethod.Get, $"users/{id}", ct: ct);

    // ── wiadomości ──
    public Task<List<Message>> GetMessages(long channelId, long? before = null, long? around = null, int limit = 50, long? thread = null, CancellationToken ct = default)
    {
        var q = $"channels/{channelId}/messages?limit={limit}";
        if (thread is long th) q += $"&thread={th}";
        if (before is long b) q += $"&before={b}";
        else if (around is long a) q += $"&around={a}";
        return Send<List<Message>>(HttpMethod.Get, q, ct: ct);
    }
    public Task<Message> SendMessage(long channelId, string content, long? replyTo = null, IReadOnlyList<long>? attachments = null, string? nonce = null,
        long? threadId = null, long? stickerId = null, CancellationToken ct = default) =>
        Send<Message>(HttpMethod.Post, $"channels/{channelId}/messages", new { content, replyTo, attachments = attachments is { Count: > 0 } ? attachments : null, nonce, threadId, stickerId }, ct);
    public Task<Message> EditMessage(long id, string content, CancellationToken ct = default) =>
        Send<Message>(HttpMethod.Patch, $"messages/{id}", new { content }, ct);
    public Task DeleteMessage(long id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"messages/{id}", ct: ct);
    public Task AddReaction(long messageId, string emoji, CancellationToken ct = default) =>
        Send(HttpMethod.Put, $"messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}", ct: ct);
    public Task RemoveReaction(long messageId, string emoji, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}", ct: ct);
    public Task MarkRead(long channelId, long messageId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"channels/{channelId}/read", new { messageId }, ct);
    public Task PinMessage(long id, CancellationToken ct = default) => Send(HttpMethod.Put, $"messages/{id}/pin", ct: ct);
    public Task UnpinMessage(long id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"messages/{id}/pin", ct: ct);
    public Task VotePoll(long id, IReadOnlyList<long> optionIds, CancellationToken ct = default) => Send(HttpMethod.Put, $"messages/{id}/poll", new { optionIds }, ct);
    public Task ClosePoll(long id, CancellationToken ct = default) => Send(HttpMethod.Post, $"messages/{id}/poll/close", ct: ct);
    public Task<Message> SendPoll(long channelId, string question, IReadOnlyList<string> options, bool multi, int hours, CancellationToken ct = default) =>
        Send<Message>(HttpMethod.Post, $"channels/{channelId}/messages", new { content = "", poll = new { question, options, multi, hours } }, ct);
    /// <summary>Mini-gra: {kind:"dice", count, sides} | {kind:"coin"} | {kind:"pick", options} | {kind:"ttt"|"rps", opponentId}.</summary>
    public Task<Message> CreateGame(long channelId, object body, CancellationToken ct = default) => Send<Message>(HttpMethod.Post, $"channels/{channelId}/games", body, ct);
    /// <summary>Ruch: {cell} (kółko i krzyżyk) albo {choice: kamien|papier|nozyce}.</summary>
    public Task<Message> GameMove(long messageId, object body, CancellationToken ct = default) => Send<Message>(HttpMethod.Post, $"messages/{messageId}/game", body, ct);
    public Task<List<Message>> GetPins(long channelId, CancellationToken ct = default) => Send<List<Message>>(HttpMethod.Get, $"channels/{channelId}/pins", ct: ct);

    /// <summary>Wysyła plik (surowe bajty). Zwraca załącznik do dołączenia do wiadomości.</summary>
    public async Task<Attachment> Upload(Stream data, string name, string? contentType, CancellationToken ct = default)
    {
        var content = new StreamContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        var e = await SendRaw(HttpMethod.Post, "uploads?name=" + Uri.EscapeDataString(name), content, ct).ConfigureAwait(false);
        return e.To<Attachment>()!;
    }

    // ── blokady, zgłoszenia, moderacja ──
    public Task Block(long userId, CancellationToken ct = default) => Send(HttpMethod.Post, "blocks", new { userId }, ct);
    public Task Unblock(long userId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"blocks/{userId}", ct: ct);
    public Task Report(long messageId, string reason, string? note, CancellationToken ct = default) =>
        Send(HttpMethod.Post, "reports", new { messageId, reason, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() }, ct);
    public Task Timeout(long serverId, long userId, int minutes, CancellationToken ct = default) => Send(HttpMethod.Post, $"servers/{serverId}/members/{userId}/timeout", new { minutes }, ct);
    public Task Kick(long serverId, long userId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"servers/{serverId}/members/{userId}", ct: ct);
    public Task Ban(long serverId, long userId, string? reason, int deleteMessagesHours, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"servers/{serverId}/bans", new { userId, reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), deleteMessagesHours }, ct);
    public Task VoiceModerate(long serverId, long userId, object action, CancellationToken ct = default) => Send(HttpMethod.Post, $"servers/{serverId}/voice-moderation/{userId}", action, ct);
    public Task SetNick(long serverId, long userId, string? nick, CancellationToken ct = default) =>
        Send(HttpMethod.Patch, $"servers/{serverId}/members/{(userId == 0 ? "@me" : userId.ToString())}", new Dictionary<string, object?> { ["nick"] = string.IsNullOrWhiteSpace(nick) ? null : nick.Trim() }, ct);
    public Task UnhideDm(long dmId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"dms/{dmId}/hide", ct: ct);
    public Task<Dm> CreateGroup(IReadOnlyList<long> userIds, CancellationToken ct = default) => Send<Dm>(HttpMethod.Post, "dms", new { userIds }, ct);
    public Task<Dm> RenameGroup(long dmId, string? name, CancellationToken ct = default) =>
        Send<Dm>(HttpMethod.Patch, $"dms/{dmId}", new Dictionary<string, object?> { ["name"] = string.IsNullOrWhiteSpace(name) ? null : name.Trim() }, ct);
    public Task<Dm> AddToGroup(long dmId, long userId, CancellationToken ct = default) => Send<Dm>(HttpMethod.Post, $"dms/{dmId}/members", new { userId }, ct);
    public Task RemoveFromGroup(long dmId, long userId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"dms/{dmId}/members/{(userId == 0 ? "@me" : userId.ToString())}", ct: ct);

    /// <summary>Obrazek wysyłany jako surowe bajty (ikona grupy / serwera, awatar, baner).</summary>
    public async Task<System.Text.Json.JsonElement> PostImage(string path, Stream data, string? contentType, CancellationToken ct = default)
    {
        var content = new StreamContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        return await SendRaw(HttpMethod.Post, path, content, ct).ConfigureAwait(false);
    }
    public Task<System.Text.Json.JsonElement> DeleteJson(string path, CancellationToken ct = default) => Send(HttpMethod.Delete, path, ct: ct);
    public Task<System.Text.Json.JsonElement> PatchJson(string path, object body, CancellationToken ct = default) => Send(HttpMethod.Patch, path, body, ct);
    public Task<System.Text.Json.JsonElement> PostJson(string path, object? body = null, CancellationToken ct = default) => Send(HttpMethod.Post, path, body, ct);
    public Task<System.Text.Json.JsonElement> PutJson(string path, object? body = null, CancellationToken ct = default) => Send(HttpMethod.Put, path, body, ct);

    // ── wyszukiwarka ──
    public Task<SearchResult> Search(SearchQuery q, CancellationToken ct = default) => Send<SearchResult>(HttpMethod.Get, "search?" + q.ToQueryString(), ct: ct);

    // ── GIF-y ──
    public Task<GifPage> GifsTrending(string? pos = null, int limit = 24, CancellationToken ct = default) =>
        Send<GifPage>(HttpMethod.Get, $"gifs/trending?limit={limit}" + (pos is null ? "" : "&pos=" + Uri.EscapeDataString(pos)), ct: ct);
    public Task<GifPage> GifsSearch(string q, string? pos = null, int limit = 24, CancellationToken ct = default) =>
        Send<GifPage>(HttpMethod.Get, $"gifs/search?limit={limit}&q={Uri.EscapeDataString(q)}" + (pos is null ? "" : "&pos=" + Uri.EscapeDataString(pos)), ct: ct);
    public Task<List<GifItem>> GifFavorites(CancellationToken ct = default) => Send<List<GifItem>>(HttpMethod.Get, "gifs/favorites", ct: ct);
    public Task AddGifFavorite(GifItem g, CancellationToken ct = default) =>
        Send(HttpMethod.Put, "gifs/favorites", new { url = g.Url, preview = g.Preview ?? g.Url, width = g.Width ?? 0, height = g.Height ?? 0 }, ct);
    public Task RemoveGifFavorite(string url, CancellationToken ct = default) => Send(HttpMethod.Delete, "gifs/favorites", new { url }, ct);

    // ── wątki (serwer 5.10) ──
    public async Task<(ThreadInfo Thread, Message? Starter)> GetThread(long id, CancellationToken ct = default)
    {
        var e = await Send(HttpMethod.Get, $"threads/{id}", ct: ct).ConfigureAwait(false);
        return (e.Prop("thread").To<ThreadInfo>() ?? throw new ApiException(404, "Nie ma takiego wątku"), e.Prop("starter").To<Message>());
    }
    public Task<ThreadInfo> CreateThread(long messageId, string? name, CancellationToken ct = default) =>
        Send<ThreadInfo>(HttpMethod.Post, $"messages/{messageId}/thread", new { name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() }, ct);
    public async Task<(List<ThreadInfo> Threads, int ArchivedCount)> GetThreads(long channelId, bool archived = false, CancellationToken ct = default)
    {
        var e = await Send(HttpMethod.Get, $"channels/{channelId}/threads" + (archived ? "?archived=1" : ""), ct: ct).ConfigureAwait(false);
        return (e.Prop("threads").To<List<ThreadInfo>>() ?? [], (int)(e.Long("archivedCount") ?? 0));
    }
    public Task<ThreadInfo> PatchThread(long id, object changes, CancellationToken ct = default) => Send<ThreadInfo>(HttpMethod.Patch, $"threads/{id}", changes, ct);
    public Task DeleteThread(long id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"threads/{id}", ct: ct);
    public Task<ThreadInfo> FollowThread(long id, bool on, CancellationToken ct = default) => Send<ThreadInfo>(on ? HttpMethod.Put : HttpMethod.Delete, $"threads/{id}/follow", ct: ct);
    public Task MarkThreadRead(long id, long messageId, CancellationToken ct = default) => Send(HttpMethod.Post, $"threads/{id}/read", new { messageId }, ct);

    // ── rozmowy prywatne, znajomi ──
    public Task<Dm> OpenDm(long userId, CancellationToken ct = default) => Send<Dm>(HttpMethod.Post, "dms", new { userId }, ct);
    public Task AddFriend(string username, CancellationToken ct = default) => Send(HttpMethod.Post, "friends", new { username = username.Trim().TrimStart('@') }, ct);
    public Task AcceptFriend(long userId, CancellationToken ct = default) => Send(HttpMethod.Post, $"friends/{userId}/accept", ct: ct);
    public Task RemoveFriend(long userId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"friends/{userId}", ct: ct);

    // ── serwery, zaproszenia, powiadomienia ──
    public async Task<string> CreateInvite(long serverId, CancellationToken ct = default) =>
        (await Send(HttpMethod.Post, $"servers/{serverId}/invites", ct: ct).ConfigureAwait(false)).Str("code") ?? "";
    public async Task<InviteInfo> GetInvite(string code, CancellationToken ct = default) =>
        (await Send(HttpMethod.Get, "invites/" + Uri.EscapeDataString(code), ct: ct).ConfigureAwait(false)).Prop("server").To<InviteInfo>() ?? throw new ApiException(404, "Zaproszenie nie działa");
    public Task<Server> JoinInvite(string code, CancellationToken ct = default) => Send<Server>(HttpMethod.Post, $"invites/{Uri.EscapeDataString(code)}/join", ct: ct);
    public Task<Server> CreateServer(string name, CancellationToken ct = default) => Send<Server>(HttpMethod.Post, "servers", new { name }, ct);
    public Task LeaveServer(long serverId, CancellationToken ct = default) => Send(HttpMethod.Post, $"servers/{serverId}/leave", ct: ct);
    /// <summary>scope: server | channel | dm. Null czyści wartość (dziedziczenie).</summary>
    public Task SetNotify(string scope, long id, string? level, long? mutedUntil, CancellationToken ct = default) =>
        Send(HttpMethod.Put, $"notify/{scope}/{id}", new Dictionary<string, object?> { ["level"] = level, ["mutedUntil"] = mutedUntil }, ct);
    public Task ForgotPassword(string login, CancellationToken ct = default) => Send(HttpMethod.Post, "password/forgot", new { login }, ct);
    public Task HideDm(long dmId, CancellationToken ct = default) => Send(HttpMethod.Post, $"dms/{dmId}/hide", ct: ct);

    // ── głos (LiveKit) ──
    /// <summary>Świeży token do serwera głosu — działa tylko, gdy jesteśmy na tym kanale (voice_state).</summary>
    public async Task<Voice.VoiceMedia?> VoiceToken(long channelId, CancellationToken ct = default) =>
        Voice.VoiceMediaJson.Parse(await Send(HttpMethod.Post, "voice/token", new { channelId }, ct).ConfigureAwait(false));

    public void Dispose() => _http.Dispose();
}
