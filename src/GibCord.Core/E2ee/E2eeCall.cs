using System.Security.Cryptography;
using System.Text.Json;

namespace GibCord.Core.E2ee;

/// <summary>Moje urządzenie: identyfikator (16 znaków hex) i klucz P-256.</summary>
public sealed class E2eeDevice(string dev, ECDiffieHellman key)
{
    public string Dev { get; } = dev;
    public ECDiffieHellman Key { get; } = key;
    public string Pub { get; } = E2eeCrypto.PublicRaw(key);
}

/// <summary>Droga do serwera dla protokołu: wysłanie ramki e2ee_send i pobranie kluczy publicznych urządzeń.</summary>
public interface IE2eeTransport
{
    Task Send(object frame);
    Task<Dictionary<long, List<E2eeDeviceInfo>>> Devices(IReadOnlyCollection<long> uids);
}

public enum E2eeStatus { Off, Waiting, Active, Failed }

/// <summary>
/// 6.4: wymiana klucza jednej szyfrowanej rozmowy (PLAN-6.4.md, sekcja E — strona robi to samo):
/// • po wejściu: „hi” do pozostałych osób w pokoju; kto jest sam — losuje klucz (indeks 0);
/// • na „hi” odpowiada lider = najmniejsze uid wśród osób w pokoju, które mają klucz: „key” na urządzenie nadawcy;
/// • kto po 4 s nie ma klucza, wysyła „req” (odpowiada lider; od drugiej prośby — każdy, kto ma klucz);
/// • po wyjściu osoby z rozmowy grupowej lider losuje nowy klucz (indeks+1), rozsyła go, po 1,5 s sam przełącza szyfrowanie.
/// Dodatkowo (bezpiecznik): gdy wszyscy w pokoju czekają na klucz, losuje go osoba z najmniejszym uid.
/// Klasa nie zna LiveKit ani WebSocketu — dostaje ramki i oddaje klucz przez <see cref="KeyReady"/> (testy na Linuksie).
/// </summary>
public sealed class E2eeCall : IDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly IE2eeTransport _net;
    readonly Func<IReadOnlyCollection<long>> _room;
    readonly Dictionary<long, string> _peerDev = [];                 // urządzenie, którego osoba używa w tej rozmowie
    readonly Dictionary<long, List<E2eeDeviceInfo>> _devices = [];   // klucze publiczne (z serwera)
    readonly Dictionary<long, long> _waitingSince = [];              // kto prosił o klucz i (z tego, co wiem) jeszcze go nie ma
    readonly Dictionary<long, int> _asks = [];                       // ile razy prosił
    HashSet<long> _lastRoom = [];
    Timer? _reqTimer, _switchTimer;
    int _reqTries;
    bool _disposed;

    public long CallId { get; }
    public long MyUid { get; }
    public bool IsGroup { get; }
    public E2eeDevice Device { get; }
    public E2eeKey? Key { get; private set; }
    /// <summary>Kto dał (albo wylosował) obecny klucz — przy sporze o ten sam numer wygrywa mniejsze uid źródła (jak na stronie).</summary>
    public long KeySource { get; private set; }
    /// <summary>Trwa wymiana: nowy klucz jest już w pierścieniu (odszyfrowanie), szyfrować nim zaczniemy po <see cref="SwitchAfter"/>.</summary>
    public E2eeKey? Pending { get; private set; }
    long _pendingSource;
    public E2eeStatus Status { get; private set; } = E2eeStatus.Waiting;
    public string? Error { get; private set; }

    /// <summary>Opóźnienia (w testach krótsze).</summary>
    public TimeSpan ReqAfter { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan SwitchAfter { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan WaitingFor { get; init; } = TimeSpan.FromSeconds(6);
    public int MaxReqTries { get; init; } = 6;

    /// <summary>Jest klucz (pierwszy albo nowy po wymianie). <c>switchNow</c>: od razu szyfruj nim (false = tylko do odszyfrowania, przełączenie przyjdzie przez <see cref="SwitchTo"/>).</summary>
    public event Action<E2eeKey, bool>? KeyReady;
    /// <summary>Przełącz szyfrowanie na klucz o tym numerze (lider po 1,5 s od wymiany).</summary>
    public event Action<E2eeKey>? SwitchTo;
    public event Action? Changed;
    public event Action<string>? Log;

    public E2eeCall(long callId, long myUid, bool isGroup, E2eeDevice device, IE2eeTransport net, Func<IReadOnlyCollection<long>> roomMembers)
    {
        CallId = callId; MyUid = myUid; IsGroup = isGroup; Device = device; _net = net; _room = roomMembers;
    }

    /// <summary>Osoby w pokoju poza mną.</summary>
    HashSet<long> Others() => _room().Where(u => u != MyUid).ToHashSet();

    static long NowMs => Environment.TickCount64;

    /// <summary>
    /// Start po wejściu do szyfrowanego pokoju. <paramref name="othersAtJoin"/> — kto był w pokoju w chwili wejścia (zrzut z voice_state);
    /// <paramref name="members"/> — członkowie rozmowy (do nich idzie rozgłoszone „hi”, jak na stronie; null = osoby w pokoju).
    /// </summary>
    public async Task Start(IReadOnlyCollection<long> othersAtJoin, IReadOnlyCollection<long>? members = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _lastRoom = othersAtJoin.Where(u => u != MyUid).ToHashSet();
            if (_lastRoom.Count == 0)
            {
                Note("sam w pokoju — losuję klucz");
                SetKey(new E2eeKey(E2eeCrypto.NewCallKey(), 0), MyUid, switchNow: true);
                return;
            }
            Note($"w pokoju: {string.Join(",", _lastRoom)} — „hi”, czekam na klucz");
            foreach (var u in _lastRoom.Union(members ?? []).Where(u => u != MyUid).Distinct())
                await SendFrame(new { t = "e2ee_send", call = CallId, to = u, fromDev = Device.Dev, kind = "hi" }).ConfigureAwait(false);
            ArmReq();
        }
        finally { _gate.Release(); }
    }

    void ArmReq()
    {
        _reqTimer?.Dispose();
        _reqTimer = new Timer(_ => _ = OnReqTimer(), null, ReqAfter, Timeout.InfiniteTimeSpan);
    }

    async Task OnReqTimer()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || Key is not null) return;
            var others = Others();
            if (others.Count == 0)
            {
                // wszyscy wyszli, zanim przyszedł klucz — zaczynamy od nowa sami
                Note("pokój pusty — własny klucz");
                SetKey(new E2eeKey(E2eeCrypto.NewCallKey(), 0), MyUid, switchNow: true);
                return;
            }
            // zapas (umowa 24.11): nikt nie odpowiedział na 2 prośby „req” (~12 s) — klucz losuje najmniejsze uid w pokoju (indeks 0);
            // szybciej, gdy widać, że nikt w pokoju klucza nie ma (wszyscy sami o niego prosili)
            var now = NowMs;
            var allWaiting = others.All(u => _waitingSince.TryGetValue(u, out var t) && now - t < WaitingFor.TotalMilliseconds * 2);
            if ((allWaiting || _reqTries >= 2) && MyUid < others.Min())
            {
                Note("nikt nie ma klucza — losuję");
                SetKey(new E2eeKey(E2eeCrypto.NewCallKey(), 0), MyUid, switchNow: true);
                foreach (var u in others) await SendKeyTo(u, _peerDev.GetValueOrDefault(u)).ConfigureAwait(false);
                return;
            }
            if (++_reqTries > MaxReqTries)
            {
                Status = E2eeStatus.Failed;
                Error = "Nie udało się odebrać klucza szyfrowania od rozmówcy. Wyjdź z rozmowy i wejdź ponownie.";
                Raise();
                return;
            }
            Note($"brak klucza — „req” nr {_reqTries} do {string.Join(",", others)}");
            foreach (var u in others) await SendFrame(new { t = "e2ee_send", call = CallId, to = u, fromDev = Device.Dev, kind = "req" }).ConfigureAwait(false);
            ArmReq();
        }
        catch (Exception ex) { Note("req: " + ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>Ponowna prośba o klucz (przycisk „Spróbuj ponownie” albo odszyfrowanie przestało działać).</summary>
    public async Task AskAgain()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _reqTries = 0;
            if (Status == E2eeStatus.Failed) { Status = E2eeStatus.Waiting; Error = null; Raise(); }
            foreach (var u in Others()) await SendFrame(new { t = "e2ee_send", call = CallId, to = u, fromDev = Device.Dev, kind = "req" }).ConfigureAwait(false);
            if (Key is null) ArmReq();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Ramka e2ee_msg z serwera (tylko dla tej rozmowy).</summary>
    public async Task OnMessage(JsonElement d)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || d.Long("call") != CallId || d.Long("from") is not long from || from == MyUid) return;
            var fromDev = d.Str("fromDev") ?? "";
            if (fromDev.Length != 16) return;
            if (d.Str("dev") is { Length: > 0 } toDev && toDev != Device.Dev) return; // do innego mojego urządzenia
            var kind = d.Str("kind");
            Note($"od {from}:{fromDev}: {kind}{(d.Str("dev") is { Length: > 0 } ? " (do mnie)" : "")}");
            if (kind is "hi" or "req")
            {
                var known = _peerDev.GetValueOrDefault(from) == fromDev;
                _peerDev[from] = fromDev;
                _lastRoom.Add(from); // jest w pokoju (voice_state mogło jeszcze nie dojść)
                // „hi” zaadresowane do mnie = odpowiedź „jestem, to moje urządzenie” (osoba ma klucz) — nie prośba
                var direct = kind == "hi" && d.Str("dev") is { Length: > 0 };
                if (!direct) _waitingSince[from] = NowMs;
                var asks = _asks[from] = kind == "req" ? _asks.GetValueOrDefault(from) + 1 : 0;
                if (Key is not null && (IAmLeader() || asks >= 2)) { if (!direct || !known) await SendKeyTo(from, fromDev).ConfigureAwait(false); }
                else if (kind == "hi" && !direct && !known)
                    // nie jestem liderem: odpowiadam „hi” na urządzenie nadawcy — każdy zna urządzenia wszystkich (kod bezpieczeństwa)
                    await SendFrame(new { t = "e2ee_send", call = CallId, to = from, dev = fromDev, fromDev = Device.Dev, kind = "hi" }).ConfigureAwait(false);
                return;
            }
            if (kind != "key" || d.Str("dev") != Device.Dev) return;
            var bx = d.Prop("box");
            if (bx.Str("iv") is not { } iv || bx.Str("ct") is not { } ct) return;
            var pub = await PubOf(from, fromDev).ConfigureAwait(false);
            if (pub is null) { Note($"brak klucza publicznego {from}:{fromDev}"); return; }
            (string Key, int Index)? got;
            try { got = E2eeCrypto.ParseKeyPayload(E2eeCrypto.Open(Device.Key, pub, E2eeCrypto.Info(CallId, from, fromDev, MyUid, Device.Dev), new E2eeBox(iv, ct))); }
            catch (Exception ex) { Note($"nie otwiera się pudełko od {from}: {ex.Message}"); return; }
            if (got is not { } k || k.Index >= E2eeKey.Cycle) return; // numery 0–15 (jak strona)
            _peerDev[from] = fromDev;
            _waitingSince.Remove(from);
            _asks.Remove(from);
            var nk = new E2eeKey(k.Key, k.Index);
            if (Key is null) { SetKey(nk, from, switchNow: true); return; }
            if (nk == Key || nk == Pending) return;
            // jak na stronie: trwa wymiana i przyszedł klucz o tym samym numerze — wygrywa ten od mniejszego uid
            if (Pending is { } pend && nk.Index == pend.Index) { if (from < _pendingSource) StartPending(nk, from); return; }
            // spór o obecny numer — wygrywa klucz od mniejszego uid (od razu)
            if (nk.Index == Key.Index) { if (from < KeySource) SetKey(nk, from, switchNow: true); return; }
            // nowszy klucz (wymiana po wyjściu kogoś z grupy): do pierścienia od razu, szyfrowanie nim po 1,5 s — jak lider i strona
            if (E2eeKey.Newer(nk.Index, (Pending ?? Key).Index)) StartPending(nk, from);
        }
        catch (Exception ex) { Note("wiadomość: " + ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>Zmiana osób w pokoju (voice_state). W grupie: ktoś wyszedł → lider wymienia klucz.</summary>
    public async Task OnRoomChanged()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            var now = Others();
            var left = _lastRoom.Where(u => !now.Contains(u)).ToList();
            _lastRoom = now;
            foreach (var u in left) { _waitingSince.Remove(u); _asks.Remove(u); _peerDev.Remove(u); }
            if (Key is null && now.Count == 0 && left.Count > 0)
            {
                Note("wszyscy wyszli — własny klucz");
                SetKey(new E2eeKey(E2eeCrypto.NewCallKey(), 0), MyUid, switchNow: true);
                return;
            }
            if (left.Count == 0 || !IsGroup || Key is null || now.Count == 0 || !IAmLeader()) return;
            var next = new E2eeKey(E2eeCrypto.NewCallKey(), E2eeKey.Next((Pending ?? Key).Index));
            Note($"wymiana klucza po wyjściu {string.Join(",", left)} → {next.Index}");
            // nowy klucz: od razu do odszyfrowania, szyfrować nim zaczynam dopiero, gdy reszta go dostanie
            StartPending(next, MyUid);
            foreach (var u in now) await SendKeyTo(u, _peerDev.GetValueOrDefault(u), next).ConfigureAwait(false);
        }
        catch (Exception ex) { Note("wymiana: " + ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>Lider = najmniejsze uid wśród osób w pokoju, które (z tego, co wiem) mają klucz.</summary>
    bool IAmLeader()
    {
        if (Key is null) return false;
        var now = NowMs;
        var keyed = Others().Where(u => !(_waitingSince.TryGetValue(u, out var t) && now - t < WaitingFor.TotalMilliseconds));
        return keyed.All(u => u > MyUid);
    }

    /// <summary>Nowy klucz „na zapas”: od razu do pierścienia (odszyfrowanie), po <see cref="SwitchAfter"/> staje się obecnym i nim szyfrujemy.</summary>
    void StartPending(E2eeKey k, long source)
    {
        Pending = k;
        _pendingSource = source;
        Note($"nowy klucz nr {k.Index} od {source} — przełączę za {SwitchAfter.TotalSeconds:0.#} s");
        KeyReady?.Invoke(k, false);
        _switchTimer?.Dispose();
        _switchTimer = new Timer(_ => _ = FinishPending(k), null, SwitchAfter, Timeout.InfiniteTimeSpan);
        Raise();
    }

    async Task FinishPending(E2eeKey k)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || Pending != k) return;
            Key = k;
            KeySource = _pendingSource;
            Pending = null;
            SwitchTo?.Invoke(k);
            Raise();
        }
        finally { _gate.Release(); }
    }

    void SetKey(E2eeKey k, long source, bool switchNow)
    {
        if (Pending is not null) { Pending = null; _switchTimer?.Dispose(); _switchTimer = null; }
        Key = k;
        KeySource = source;
        Status = E2eeStatus.Active;
        Error = null;
        _reqTimer?.Dispose(); _reqTimer = null;
        KeyReady?.Invoke(k, switchNow);
        Raise();
    }

    /// <summary>Wysyła klucz osobie: na urządzenie, którego używa w rozmowie, a gdy go nie znam — na jej 3 ostatnio używane urządzenia.</summary>
    async Task SendKeyTo(long uid, string? dev, E2eeKey? only = null)
    {
        if (Key is null) return;
        // trwa wymiana — nowa osoba dostaje oba klucze (obecny i ten „na zapas”), jak na stronie
        var keys = only is not null ? [only] : Pending is { } p ? new List<E2eeKey> { Key, p } : [Key];
        var targets = new List<E2eeDeviceInfo>();
        if (dev is not null && await PubOf(uid, dev).ConfigureAwait(false) is { } pub) targets.Add(new E2eeDeviceInfo { Dev = dev, Pub = pub });
        else
        {
            await Refresh([uid]).ConfigureAwait(false);
            targets.AddRange((_devices.GetValueOrDefault(uid) ?? []).OrderByDescending(x => x.At).Take(3));
        }
        foreach (var t in targets)
            foreach (var key in keys)
            {
                var box = E2eeCrypto.Seal(Device.Key, t.Pub, E2eeCrypto.Info(CallId, MyUid, Device.Dev, uid, t.Dev), E2eeCrypto.KeyPayload(key.Key, key.Index));
                await SendFrame(new { t = "e2ee_send", call = CallId, to = uid, dev = t.Dev, fromDev = Device.Dev, kind = "key", box = new { iv = box.Iv, ct = box.Ct } }).ConfigureAwait(false);
            }
        _waitingSince.Remove(uid);
    }

    async Task<string?> PubOf(long uid, string dev)
    {
        if (_devices.GetValueOrDefault(uid)?.Find(x => x.Dev == dev) is { } hit) return hit.Pub;
        await Refresh([uid]).ConfigureAwait(false);
        return _devices.GetValueOrDefault(uid)?.Find(x => x.Dev == dev)?.Pub;
    }

    async Task Refresh(IReadOnlyCollection<long> uids)
    {
        try { foreach (var (u, list) in await _net.Devices(uids).ConfigureAwait(false)) _devices[u] = list; }
        catch (Exception ex) { Note("urządzenia: " + ex.Message); }
    }

    async Task SendFrame(object f)
    {
        try { await _net.Send(f).ConfigureAwait(false); }
        catch (Exception ex) { Note("wysyłka: " + ex.Message); }
    }

    /// <summary>Kod bezpieczeństwa z kluczy urządzeń wszystkich osób w pokoju (null, gdy czegoś brakuje).</summary>
    public async Task<string?> SafetyCode()
    {
        var others = Others();
        var missing = others.Where(u => !_peerDev.ContainsKey(u) || _devices.GetValueOrDefault(u)?.Find(x => x.Dev == _peerDev[u]) is null).ToList();
        if (missing.Count > 0) await Refresh(missing).ConfigureAwait(false);
        var people = new List<(long, string)> { (MyUid, Device.Pub) };
        foreach (var u in others)
        {
            var list = _devices.GetValueOrDefault(u);
            var d = _peerDev.TryGetValue(u, out var pd) ? list?.Find(x => x.Dev == pd) : list?.OrderByDescending(x => x.At).FirstOrDefault();
            if (d is null) return null;
            people.Add((u, d.Pub));
        }
        return E2eeCrypto.SafetyCode(people);
    }

    /// <summary>Do testów i dziennika: urządzenie osoby w tej rozmowie.</summary>
    public string? PeerDevice(long uid) => _peerDev.GetValueOrDefault(uid);

    void Raise() { try { Changed?.Invoke(); } catch { } }
    void Note(string s) { try { Log?.Invoke($"e2ee {CallId}: {s}"); } catch { } }

    public void Dispose()
    {
        _disposed = true;
        _reqTimer?.Dispose(); _reqTimer = null;
        _switchTimer?.Dispose(); _switchTimer = null;
    }
}
