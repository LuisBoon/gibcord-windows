using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GibCord.Core;

public enum GatewayStatus { Offline, Connecting, Connected, Reconnecting, SessionEnded, Suspended, EmailGate }

/// <summary>Zdarzenie z WebSocket: typ (<c>t</c>) i cała ramka.</summary>
public readonly record struct GatewayEvent(string Type, JsonElement Data);

/// <summary>
/// Połączenie na żywo (API.md, sekcja 7). Łączy się z tokenem w nagłówku, sam wraca po zerwaniu
/// (1 → 30 s), mierzy ping. Zdarzenia przychodzą na wątku w tle — odbiorca sam przenosi je na swój wątek.
/// API v2 (serwer C#): zawsze prosi o ?v=2. Serwer v2 numeruje zdarzenia („s”); po zerwaniu łączymy się z
/// resume=SESJA.NUMER i dostajemy tylko przegapione (hello.resumed = true) — bez pobierania całego stanu.
/// Stary serwer ignoruje ?v=2 i działa jak dotąd (hello bez „v”).
/// </summary>
public sealed class Gateway : IAsyncDisposable
{
    readonly Uri _wsBase;
    readonly bool _desktop;
    readonly string _token;
    string? _v2Session;
    long _v2Seq;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    CancellationTokenSource? _cts;
    ClientWebSocket? _ws;
    Task? _loop;
    long _lastRx;
    long _pingSentAt;

    public event Action<GatewayEvent>? Event;
    public event Action<GatewayStatus, TimeSpan?>? StatusChanged;

    public GatewayStatus Status { get; private set; } = GatewayStatus.Offline;
    /// <summary>Ostatnie hello było z serwera v2 (zdarzenia z numerami).</summary>
    public bool V2 { get; private set; }
    /// <summary>Numer ostatniego odebranego zdarzenia (v2).</summary>
    public long Seq => Interlocked.Read(ref _v2Seq);
    public TimeSpan? Rtt { get; private set; }
    /// <summary>Ostatnie pomiary pingu (ms) — wykres w panelu rozmowy. Najwyżej 120.</summary>
    public double[] RttHistory { get { lock (_rtts) return [.. _rtts]; } }
    readonly Queue<double> _rtts = new();
    /// <summary>Co ile pingować serwer: zwykle 20 s, w rozmowie 3 s (żywy ping z wykresem).</summary>
    public int PingEveryMs => Fast?.Invoke() == true ? 3_000 : 20_000;
    /// <summary>Czy pingować często (aplikacja: gdy jest w rozmowie).</summary>
    public Func<bool>? Fast { get; set; }
    public int CloseCode { get; private set; }

    public Gateway(Uri baseUri, string token, bool desktop = true)
    {
        _wsBase = new UriBuilder(new Uri(baseUri, "ws")) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws" }.Uri;
        _desktop = desktop;
        _token = token;
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Run(_cts.Token));
    }

    void SetStatus(GatewayStatus s, TimeSpan? delay = null)
    {
        Status = s;
        try { StatusChanged?.Invoke(s, delay); } catch { /* błąd odbiorcy nie może zabić pętli */ }
    }

    async Task Run(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetStatus(attempt == 0 ? GatewayStatus.Connecting : GatewayStatus.Reconnecting);
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
            ws.Options.SetRequestHeader("User-Agent", GibCordApi.UserAgent);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            ws.Options.CollectHttpResponseDetails = true;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(ConnectUri(), connectCts.Token).ConfigureAwait(false);
                _ws = ws;
                attempt = 0;
                Interlocked.Exchange(ref _lastRx, Environment.TickCount64);
                SetStatus(GatewayStatus.Connected);
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pinger = PingLoop(ws, pingCts.Token);
                await Receive(ws, ct).ConfigureAwait(false);
                pingCts.Cancel();
                try { await pinger.ConfigureAwait(false); } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (WebSocketException) when (ws.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                CloseCode = 4001;
                SetStatus(GatewayStatus.SessionEnded);
                return;
            }
            catch { /* zerwane — spróbujemy ponownie */ }
            finally { _ws = null; }

            var code = (int?)ws.CloseStatus ?? 0;
            if (code is 4001 or 4003 or 4005)
            {
                CloseCode = code;
                SetStatus(code == 4003 ? GatewayStatus.Suspended : code == 4005 ? GatewayStatus.EmailGate : GatewayStatus.SessionEnded);
                return;
            }
            if (ct.IsCancellationRequested) break;
            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5))) * (0.8 + Random.Shared.NextDouble() * 0.4));
            SetStatus(GatewayStatus.Reconnecting, delay);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        SetStatus(GatewayStatus.Offline);
    }

    Uri ConnectUri()
    {
        var q = (_desktop ? "client=desktop&" : "") + "v=2";
        if (_v2Session is { } s) q += "&resume=" + Uri.EscapeDataString(s + "." + Interlocked.Read(ref _v2Seq));
        return new UriBuilder(_wsBase) { Query = q }.Uri;
    }

    async Task Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                Interlocked.Exchange(ref _lastRx, Environment.TickCount64);
                if (r.MessageType != WebSocketMessageType.Text) continue;
                JsonElement root;
                try { root = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length)).RootElement.Clone(); }
                catch (JsonException) { continue; }
                var t = root.Str("t");
                if (t is null) continue;
                // v2: zdarzenie z numerem — dubel (np. przy wznowieniu) pomijamy
                if (root.Long("s") is long sn) { if (sn <= Interlocked.Read(ref _v2Seq)) continue; Interlocked.Exchange(ref _v2Seq, sn); }
                if (t == "hello")
                {
                    if (root.Long("v") == 2 && root.Str("session") is { } sess)
                    {
                        V2 = true;
                        _v2Session = sess;
                        if (root.Bool("resumed") != true) Interlocked.Exchange(ref _v2Seq, root.Long("seq") ?? 0); // nowy początek — stan i tak przyjdzie z /api/v2/state
                    }
                    else { V2 = false; _v2Session = null; Interlocked.Exchange(ref _v2Seq, 0); }
                }
                if (t == "pong")
                {
                    var sent = Interlocked.Exchange(ref _pingSentAt, 0);
                    if (sent > 0)
                    {
                        Rtt = TimeSpan.FromMilliseconds(Environment.TickCount64 - sent);
                        lock (_rtts) { _rtts.Enqueue(Rtt.Value.TotalMilliseconds); while (_rtts.Count > 120) _rtts.Dequeue(); }
                    }
                }
                try { Event?.Invoke(new GatewayEvent(t, root)); } catch { /* błąd odbiorcy nie zrywa połączenia */ }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    async Task PingLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // w rozmowie co 3 s (ping na żywo), inaczej co 20 s; przy zmianie trybu najwyżej 3 s czekania
            for (var waited = 0; waited < PingEveryMs; waited += 1000) await Task.Delay(1000, ct).ConfigureAwait(false);
            // nic nie przyszło przez 70 s (serwer pinguje co 25 s) — połączenie jest martwe
            if (Environment.TickCount64 - Interlocked.Read(ref _lastRx) > 70_000) { ws.Abort(); return; }
            Interlocked.Exchange(ref _pingSentAt, Environment.TickCount64);
            await Send(new { t = "ping", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }).ConfigureAwait(false);
        }
    }

    /// <summary>Wysyła ramkę JSON. Zwraca false, gdy nie ma połączenia.</summary>
    public async Task<bool> Send(object frame)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return false;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, Json.Options));
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
        finally { _sendLock.Release(); }
    }

    /// <summary>Zrywa bieżące połączenie (np. po wybudzeniu komputera) — pętla od razu łączy ponownie.</summary>
    public void Kick() { try { _ws?.Abort(); } catch { } }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _cts?.Dispose();
    }
}
