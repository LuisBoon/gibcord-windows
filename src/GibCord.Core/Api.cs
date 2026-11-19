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
    public Task<List<Message>> GetMessages(long channelId, long? before = null, long? around = null, int limit = 50, CancellationToken ct = default)
    {
        var q = $"channels/{channelId}/messages?limit={limit}";
        if (before is long b) q += $"&before={b}";
        else if (around is long a) q += $"&around={a}";
        return Send<List<Message>>(HttpMethod.Get, q, ct: ct);
    }
    public Task<Message> SendMessage(long channelId, string content, long? replyTo = null, IReadOnlyList<long>? attachments = null, string? nonce = null, CancellationToken ct = default) =>
        Send<Message>(HttpMethod.Post, $"channels/{channelId}/messages", new { content, replyTo, attachments = attachments is { Count: > 0 } ? attachments : null, nonce }, ct);
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
    public Task<List<Message>> GetPins(long channelId, CancellationToken ct = default) => Send<List<Message>>(HttpMethod.Get, $"channels/{channelId}/pins", ct: ct);

    /// <summary>Wysyła plik (surowe bajty). Zwraca załącznik do dołączenia do wiadomości.</summary>
    public async Task<Attachment> Upload(Stream data, string name, string? contentType, CancellationToken ct = default)
    {
        var content = new StreamContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        var e = await SendRaw(HttpMethod.Post, "uploads?name=" + Uri.EscapeDataString(name), content, ct).ConfigureAwait(false);
        return e.To<Attachment>()!;
    }

    // ── rozmowy prywatne, znajomi ──
    public Task<Dm> OpenDm(long userId, CancellationToken ct = default) => Send<Dm>(HttpMethod.Post, "dms", new { userId }, ct);
    public Task AddFriend(string username, CancellationToken ct = default) => Send(HttpMethod.Post, "friends", new { username = username.Trim().TrimStart('@') }, ct);
    public Task AcceptFriend(long userId, CancellationToken ct = default) => Send(HttpMethod.Post, $"friends/{userId}/accept", ct: ct);
    public Task RemoveFriend(long userId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"friends/{userId}", ct: ct);

    // ── serwery, zaproszenia, powiadomienia ──
    public async Task<string> CreateInvite(long serverId, CancellationToken ct = default) =>
        (await Send(HttpMethod.Post, $"servers/{serverId}/invites", ct: ct).ConfigureAwait(false)).Str("code") ?? "";
    public async Task<(long Id, string Name, int Members)> GetInvite(string code, CancellationToken ct = default)
    {
        var e = await Send(HttpMethod.Get, "invites/" + Uri.EscapeDataString(code), ct: ct).ConfigureAwait(false);
        var sv = e.Prop("server");
        return (sv.Long("id") ?? 0, sv.Str("name") ?? "", (int)(sv.Long("memberCount") ?? 0));
    }
    public Task<Server> JoinInvite(string code, CancellationToken ct = default) => Send<Server>(HttpMethod.Post, $"invites/{Uri.EscapeDataString(code)}/join", ct: ct);
    public Task<Server> CreateServer(string name, CancellationToken ct = default) => Send<Server>(HttpMethod.Post, "servers", new { name }, ct);
    public Task LeaveServer(long serverId, CancellationToken ct = default) => Send(HttpMethod.Post, $"servers/{serverId}/leave", ct: ct);
    /// <summary>scope: server | channel | dm. Null czyści wartość (dziedziczenie).</summary>
    public Task SetNotify(string scope, long id, string? level, long? mutedUntil, CancellationToken ct = default) =>
        Send(HttpMethod.Put, $"notify/{scope}/{id}", new Dictionary<string, object?> { ["level"] = level, ["mutedUntil"] = mutedUntil }, ct);
    public Task ForgotPassword(string login, CancellationToken ct = default) => Send(HttpMethod.Post, "password/forgot", new { login }, ct);
    public Task HideDm(long dmId, CancellationToken ct = default) => Send(HttpMethod.Post, $"dms/{dmId}/hide", ct: ct);

    public void Dispose() => _http.Dispose();
}
