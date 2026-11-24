using System.Text.Json;
using GibCord.Core.E2ee;
using GibCord.Core.Voice;

namespace GibCord.Core;

/// <summary>Gdzie trzymać klucz prywatny urządzenia (osobno dla każdego konta). Aplikacja: plik zaszyfrowany kluczem konta Windows (DPAPI).</summary>
public interface IE2eeKeyStore
{
    (string Dev, byte[] Key)? Load(long userId);
    void Save(long userId, string dev, byte[] key);
}

/// <summary>Klucze tylko w pamięci (testy, tryb pokazowy).</summary>
public sealed class MemoryE2eeKeyStore : IE2eeKeyStore
{
    readonly Dictionary<long, (string, byte[])> _d = [];
    public (string Dev, byte[] Key)? Load(long userId) { lock (_d) return _d.TryGetValue(userId, out var v) ? v : null; }
    public void Save(long userId, string dev, byte[] key) { lock (_d) _d[userId] = (dev, key); }
}

/// <summary>
/// 6.4: szyfrowane rozmowy prywatne i w grupach. Do rozmowy prywatnej wchodzimy z voice_join {e2ee:true};
/// gdy serwer odpowie voice_media {e2ee:true}, najpierw ustalamy klucz rozmowy (<see cref="E2eeCall"/>), dopiero potem łączymy się z LiveKit.
/// </summary>
public sealed partial class GibCordClient
{
    /// <summary>Magazyn klucza urządzenia — aplikacja podmienia na DPAPI.</summary>
    public IE2eeKeyStore E2eeKeys { get; set; } = new MemoryE2eeKeyStore();
    /// <summary>Dziennik techniczny (aplikacja zapisuje do dziennik.txt).</summary>
    public event Action<string>? Diag;

    E2eeDevice? _dev;
    long _devUid;
    bool _devRegistered;
    E2eeCall? _e2ee;
    VoiceMedia? _e2eeMedia;
    IReadOnlyCollection<long> _e2eeRoom = [];
    readonly List<JsonElement> _e2eeEarly = [];
    long _troubleAt;

    /// <summary>Szyfrowanie bieżącej rozmowy (null — rozmowa bez szyfrowania albo brak rozmowy).</summary>
    public E2eeCall? E2ee => _e2ee;

    /// <summary>Tryb pokazowy: stan kłódki na zrzutach.</summary>
    public E2eeStatus DemoE2ee { get; set; }
    public string DemoSafetyCode { get; set; } = "48 21 · 09 37 · 11 45";

    /// <summary>Stan szyfrowania rozmowy, w której jestem.</summary>
    public E2eeStatus E2eeState => Offline ? (VoiceChannelId != 0 ? DemoE2ee : E2eeStatus.Off) : _e2ee?.Status ?? E2eeStatus.Off;

    /// <summary>Czy rozmowa (kanał prywatny) jest szyfrowana — kłódka także dla osób spoza rozmowy.</summary>
    public bool CallEncrypted(long cid) => Offline ? DemoE2ee != E2eeStatus.Off && (cid == VoiceChannelId || Store.Voice.ContainsKey(cid)) : Store.VoiceE2ee.Contains(cid) || (_e2ee?.CallId == cid);

    /// <summary>Kod bezpieczeństwa rozmowy do porównania z rozmówcą (null, gdy nie da się go policzyć).</summary>
    public async Task<string?> SafetyCode()
    {
        if (Offline) return DemoE2ee == E2eeStatus.Active ? DemoSafetyCode : null;
        return _e2ee is { Status: E2eeStatus.Active } c ? await c.SafetyCode() : null;
    }

    /// <summary>„Spróbuj ponownie” przy błędzie wymiany klucza.</summary>
    public Task RetryE2ee() => _e2ee?.AskAgain() ?? Task.CompletedTask;

    /// <summary>Do voice_join: rozmowa prywatna / grupa (tylko tam działa szyfrowanie).</summary>
    bool WantsE2ee(long cid) => Store.DmById(cid) is not null;

    /// <summary>voice_media: szyfrowany pokój — najpierw klucz, potem LiveKit; zwykły — od razu LiveKit.</summary>
    async Task OnMedia(VoiceMedia m)
    {
        if (!m.E2ee) { DropE2ee(); await ConnectMedia(m, null); return; }
        if (_e2ee is { } cur && cur.CallId == m.ChannelId)
        {
            if (cur.Key is { } k) await ConnectMedia(m, k); else _e2eeMedia = m;
            return;
        }
        DropE2ee();
        // kto jest w pokoju w chwili wejścia (zrzut teraz — zanim cokolwiek się zmieni w trakcie rejestracji urządzenia)
        var others = (Store.Voice.GetValueOrDefault(m.ChannelId) ?? []).Select(v => v.UserId).Where(u => u != Store.Me.Id).ToList();
        _e2eeRoom = (Store.Voice.GetValueOrDefault(m.ChannelId) ?? []).Select(v => v.UserId).ToList();
        _e2eeMedia = m;
        var cid = m.ChannelId;
        E2eeDevice dev;
        try { dev = await EnsureDevice(); }
        catch (Exception ex)
        {
            Diag?.Invoke("e2ee: urządzenie: " + ex.Message);
            if (VoiceChannelId != cid) return;
            VoiceInfo?.Invoke("Nie udało się przygotować szyfrowania rozmowy: " + Friendly(ex), true);
            _ = LeaveVoice();
            return;
        }
        if (VoiceChannelId != cid || _e2eeMedia?.ChannelId != cid) return;
        var call = new E2eeCall(cid, Store.Me.Id, Store.DmById(cid)?.IsGroup == true, dev, new E2eeNet(this), () => _e2eeRoom);
        call.KeyReady += (k, now) => _ui(() => OnE2eeKey(call, k, now));
        call.SwitchTo += k => _ui(() => { if (_e2ee == call) Voice.UseE2eeKey(k); });
        call.Changed += () => _ui(() => { if (_e2ee == call) VoiceChanged?.Invoke(); });
        call.Log += s => Diag?.Invoke(s);
        _e2ee = call;
        VoiceChanged?.Invoke();
        await call.Start(others, Store.DmById(cid)?.UserIds); // rozgłoszone „hi” do członków rozmowy (jak strona)
        var early = _e2eeEarly.ToList();
        _e2eeEarly.Clear();
        foreach (var d in early) await call.OnMessage(d);
    }

    void OnE2eeKey(E2eeCall call, E2eeKey k, bool switchNow)
    {
        if (_e2ee != call) return;
        if (Voice.State == VoiceState.Idle || Voice.ChannelId != call.CallId)
        {
            // pierwszy klucz: dopiero teraz łączymy się z serwerem głosu
            if (_e2eeMedia is { } m && m.ChannelId == call.CallId) { _e2eeMedia = null; _ = ConnectMedia(m, k); }
            return;
        }
        Voice.SetE2eeKey(k, switchNow);
        VoiceChanged?.Invoke();
    }

    /// <summary>Klucz urządzenia: z dysku albo nowy; rejestracja na serwerze przy każdej rozmowie (świeży czas = „to urządzenie jest w użyciu”).</summary>
    async Task<E2eeDevice> EnsureDevice()
    {
        var uid = Store.Me.Id;
        if (_dev is null || _devUid != uid)
        {
            _dev = null;
            if (E2eeKeys.Load(uid) is { } saved)
            {
                try { _dev = new E2eeDevice(saved.Dev, E2eeCrypto.ImportPrivate(saved.Key)); }
                catch (Exception ex) { Diag?.Invoke("e2ee: zapisany klucz nie działa: " + ex.Message); }
            }
            if (_dev is null) _dev = NewDevice(uid);
            _devUid = uid;
            _devRegistered = false;
        }
        try { await Api.PutE2eeDevice(_dev.Dev, _dev.Pub); }
        catch (ApiException ex) when (ex.Status == 409)
        {
            // to id urządzenia ma na serwerze inny klucz (np. przywrócona kopia danych) — nowe urządzenie
            _dev = NewDevice(uid);
            await Api.PutE2eeDevice(_dev.Dev, _dev.Pub);
        }
        catch (ApiException ex) when (_devRegistered && ex.Status == 429) { }
        _devRegistered = true;
        return _dev;
    }

    E2eeDevice NewDevice(long uid)
    {
        var key = E2eeCrypto.NewKey();
        var d = new E2eeDevice(E2eeCrypto.NewDeviceId(), key);
        try { E2eeKeys.Save(uid, d.Dev, E2eeCrypto.ExportPrivate(key)); } catch (Exception ex) { Diag?.Invoke("e2ee: zapis klucza: " + ex.Message); }
        return d;
    }

    void DropE2ee()
    {
        _e2ee?.Dispose();
        _e2ee = null;
        _e2eeMedia = null;
        _e2eeEarly.Clear();
    }

    /// <summary>Zdarzenia z serwera dla szyfrowania (na wątku okna).</summary>
    void OnE2eeEvent(GatewayEvent ev)
    {
        var d = ev.Data;
        switch (ev.Type)
        {
            case "e2ee_msg":
                if (d.Long("call") is not long call || call != VoiceChannelId) return;
                if (_e2ee?.CallId == call) _ = _e2ee.OnMessage(d);
                else if (_e2eeEarly.Count < 50) _e2eeEarly.Add(d.Clone()); // przyszło, zanim urządzenie się zarejestrowało
                return;
            case "voice_state" when _e2ee is { } c && d.Long("channelId") == c.CallId:
                _e2eeRoom = (Store.Voice.GetValueOrDefault(c.CallId) ?? []).Select(v => v.UserId).ToList();
                _ = c.OnRoomChanged();
                return;
            case "error" when d.Str("op") == "e2ee_send":
                Diag?.Invoke("e2ee: serwer: " + d.Str("error"));
                return;
        }
    }

    /// <summary>LiveKit nie odszyfrowuje dźwięku osoby — poproś jeszcze raz o klucz (najwyżej co 10 s).</summary>
    void OnDecryptTrouble(long uid)
    {
        if (_e2ee is not { Status: E2eeStatus.Active } c) return;
        var now = Environment.TickCount64;
        if (now - _troubleAt < 10_000) return;
        _troubleAt = now;
        Diag?.Invoke($"e2ee: nie odszyfrowuje się {uid} — prośba o klucz");
        _ = c.AskAgain();
    }

    sealed class E2eeNet(GibCordClient c) : IE2eeTransport
    {
        public Task Send(object frame) => c._gw is { } gw ? gw.Send(frame) : Task.CompletedTask;
        public Task<Dictionary<long, List<E2eeDeviceInfo>>> Devices(IReadOnlyCollection<long> uids) => c.Api.GetE2eeDevices(uids);
    }
}
