using System.Text.Json;
using GibCord.Core.Voice;

namespace GibCord.Core;

/// <summary>
/// Rozmowy głosowe: obecność na kanale idzie przez WebSocket (voice_join / voice_state, jak na stronie),
/// a sam dźwięk i obraz przez serwer LiveKit (<see cref="VoiceEngine"/>).
/// </summary>
public sealed partial class GibCordClient
{
    VoiceEngine? _voice;
    int _voiceAttempts;
    long _voiceJoinAt;

    public VoiceEngine Voice => _voice ??= CreateVoice();

    /// <summary>Kanał głosowy / rozmowa, w której chcemy być (0 = żadna).</summary>
    public long VoiceChannelId { get; private set; }

    /// <summary>Zmiana w rozmowie (połączenie, wyciszenie, obrazy) — na wątku okna.</summary>
    public event Action? VoiceChanged;
    /// <summary>Ktoś dzwoni: kanał rozmowy, kto.</summary>
    public event Action<long, long>? CallRing;
    public event Action<long>? CallEnded;
    /// <summary>Komunikat do pokazania (np. „Kanał jest pełny”).</summary>
    public event Action<string, bool>? VoiceInfo;

    VoiceEngine CreateVoice()
    {
        var v = new VoiceEngine();
        v.Changed += () => _ui(() => VoiceChanged?.Invoke());
        v.Dropped += (text, final) => _ui(() => OnVoiceDropped(text, final));
        v.DecryptTrouble += uid => _ui(() => OnDecryptTrouble(uid)); // 6.4
        return v;
    }

    /// <summary>Stan rozmowy dla okna (w trybie pokazowym udajemy połączenie).</summary>
    public VoiceState VoiceStatus => Offline ? (VoiceChannelId != 0 ? VoiceState.Connected : VoiceState.Idle)
        : VoiceChannelId == 0 ? VoiceState.Idle : Voice.State == VoiceState.Idle ? VoiceState.Connecting : Voice.State;

    /// <summary>Głośność mówienia 0–1 (pulsująca obwódka). W trybie pokazowym — falująca.</summary>
    public double SpeakLevel(long uid) => Offline ? (IsSpeaking(uid) ? .55 + .45 * Math.Sin(Environment.TickCount64 / 90.0) : 0) : Voice.Level(uid);
    public bool IsSpeaking(long uid) => Offline ? VoiceChannelId != 0 && uid == DemoSpeaker : Voice.IsSpeaking(uid);
    /// <summary>Tryb pokazowy: kto „mówi” (zielona obwódka na zrzucie).</summary>
    public long DemoSpeaker { get; set; }

    public async Task JoinVoice(long channelId)
    {
        if (channelId == 0) return;
        var o = Voice.Options;
        if (Offline)
        {
            VoiceChannelId = channelId;
            var list = Store.Voice.GetValueOrDefault(channelId) ?? [];
            if (!list.Any(m => m.UserId == Store.Me.Id)) list.Add(new VoiceMember { UserId = Store.Me.Id, Muted = o.Muted, Deafened = o.Deafened });
            Store.Voice[channelId] = list;
            Store.Apply(new GatewayEvent("voice_state", JsonSerializer.SerializeToElement(new { channelId, members = list }, Json.Options)));
            VoiceChanged?.Invoke();
            return;
        }
        if (_gw is null || Status != GatewayStatus.Connected) { VoiceInfo?.Invoke("Brak połączenia z GibCordem — spróbuj za chwilę", true); return; }
        if (VoiceChannelId == channelId && Voice.State != VoiceState.Idle) return;
        if (VoiceChannelId != 0 && VoiceChannelId != channelId) { DropE2ee(); await Voice.DisconnectAsync(); }
        VoiceChannelId = channelId;
        _voiceAttempts = 0;
        _voiceJoinAt = Environment.TickCount64;
        // 6.4: e2ee:true — umiemy szyfrować; pierwsza osoba w pustej rozmowie prywatnej ustala, że pokój jest szyfrowany
        await _gw.Send(new { t = "voice_join", channelId, muted = o.Muted || o.Deafened, deafened = o.Deafened, e2ee = WantsE2ee(channelId) });
        if (Fx is not null) // 6.2: efekt zostaje przy przejściu na inny kanał — chyba że tam są wyłączone
        {
            if (FxAllowed(channelId)) await _gw.Send(new { t = "voice_fx", fx = Fx });
            else { Fx = null; Voice.Fx.Effect = null; VoiceInfo?.Invoke("Na tym kanale efekty głosu są wyłączone — mówisz zwykłym głosem", false); }
        }
        VoiceChanged?.Invoke();
        _ = JoinWatchdog(channelId, _voiceJoinAt);
    }

    /// <summary>Gdy serwer nie przyśle danych do głosu (stary serwer bez LiveKit), mówimy to wprost.</summary>
    async Task JoinWatchdog(long channelId, long at)
    {
        await Task.Delay(12000);
        _ui(() =>
        {
            if (VoiceChannelId != channelId || _voiceJoinAt != at || Voice.State != VoiceState.Idle) return;
            VoiceInfo?.Invoke("Serwer nie przysłał połączenia głosowego — spróbuj ponownie za chwilę", true);
            _ = LeaveVoice();
        });
    }

    public async Task LeaveVoice()
    {
        var had = VoiceChannelId;
        VoiceChannelId = 0;
        DropE2ee();
        if (Offline)
        {
            if (had != 0 && Store.Voice.TryGetValue(had, out var l)) { l.RemoveAll(m => m.UserId == Store.Me.Id); Store.Apply(new GatewayEvent("voice_state", JsonSerializer.SerializeToElement(new { channelId = had, members = l }, Json.Options))); }
            VoiceChanged?.Invoke();
            return;
        }
        if (had != 0 && _gw is not null) await _gw.Send(new { t = "voice_leave" });
        await Voice.DisconnectAsync();
        VoiceChanged?.Invoke();
    }

    public void ToggleMute()
    {
        var o = Voice.Options;
        if (o.Deafened) { o.Deafened = false; o.Muted = false; }
        else o.Muted = !o.Muted;
        ApplyMute();
    }

    public void ToggleDeafen()
    {
        var o = Voice.Options;
        o.Deafened = !o.Deafened;
        ApplyMute();
    }

    void ApplyMute()
    {
        var o = Voice.Options;
        Voice.SetDeafened(o.Deafened);
        if (VoiceChannelId != 0 && !Offline) _ = _gw?.Send(new { t = "voice_mute", muted = o.Muted || o.Deafened, deafened = o.Deafened });
        if (Offline && VoiceChannelId != 0 && Store.Voice.GetValueOrDefault(VoiceChannelId)?.Find(m => m.UserId == Store.Me.Id) is { } me)
        {
            me.Muted = o.Muted || o.Deafened; me.Deafened = o.Deafened;
            Store.Apply(new GatewayEvent("voice_state", JsonSerializer.SerializeToElement(new { channelId = VoiceChannelId, members = Store.Voice[VoiceChannelId] }, Json.Options)));
        }
        VoiceChanged?.Invoke();
    }

    // ── 6.2 zmieniacz głosu ──

    /// <summary>Mój efekt głosu (null = zwykły głos). Trzyma się do zamknięcia aplikacji, także po przejściu na inny kanał.</summary>
    public string? Fx { get; private set; }
    public bool FxAllowed(long cid) => Store.ChannelById(cid) is not { FxOff: true };

    public void SetFx(string? fx)
    {
        if (fx is not null && !VoiceFx.Names.Contains(fx)) fx = null;
        if (fx is not null && VoiceChannelId != 0 && !FxAllowed(VoiceChannelId)) { VoiceInfo?.Invoke("Na tym kanale efekty głosu są wyłączone", true); return; }
        Fx = fx;
        Voice.Fx.Effect = fx;
        if (VoiceChannelId != 0 && !Offline) _ = _gw?.Send(new { t = "voice_fx", fx });
        if (Offline && VoiceChannelId != 0 && Store.Voice.GetValueOrDefault(VoiceChannelId)?.Find(m => m.UserId == Store.Me.Id) is { } me)
        {
            me.Fx = fx;
            Store.Apply(new GatewayEvent("voice_state", JsonSerializer.SerializeToElement(new { channelId = VoiceChannelId, members = Store.Voice[VoiceChannelId] }, Json.Options)));
        }
        VoiceChanged?.Invoke();
    }

    /// <summary>Odsłuch: słyszę siebie z efektem (najlepiej w słuchawkach).</summary>
    public void SetFxMonitor(bool on) { Voice.FxMonitor = on; VoiceChanged?.Invoke(); }

    /// <summary>Transmisja ekranu: najpierw serwer (uprawnienia, „NA ŻYWO” u innych), potem obraz.</summary>
    public void SetStreaming(bool on) { if (!Offline) _ = _gw?.Send(new { t = "voice_stream", on }); }
    public void SetVideo(bool on) { if (!Offline) _ = _gw?.Send(new { t = "voice_video", on }); }

    public void DeclineCall(long channelId) { if (!Offline) _ = _gw?.Send(new { t = "call_decline", channelId }); }

    /// <summary>Rozmowa w DM / grupie: wejście do pokoju dzwoni do pozostałych (robi to serwer).</summary>
    public Task StartCall(long dmId) => JoinVoice(dmId);

    int BitrateOf(long cid) => Store.ChannelById(cid) is { Bitrate: > 0 } c ? c.Bitrate : 64;

    // ── wspólne oglądanie ──

    /// <summary>Reakcja na wspólnym oglądaniu (latające emoji): pokój, osoba, emoji.</summary>
    public event Action<long, long, string>? WatchReacted;
    public static readonly string[] WatchReactions = ["😂", "🔥", "😮", "👏", "❤️", "😢", "👍", "💀"];

    /// <summary>Ramka wspólnego oglądania (watch_set, watch_ctl, watch_queue, watch_unqueue, watch_skip, watch_stop, watch_ended, watch_react).</summary>
    public void Watch(object frame)
    {
        if (VoiceChannelId == 0) { VoiceInfo?.Invoke("Najpierw wejdź na kanał głosowy", true); return; }
        if (!Offline) _ = _gw?.Send(frame);
    }

    // ── podglądy transmisji ──

    /// <summary>Kiedy przyszedł ostatni podgląd transmisji danej osoby (stream_preview).</summary>
    public Dictionary<long, long> StreamPreviews { get; } = [];

    // ── soundboard ──

    /// <summary>Dźwięk z soundboardu w moim pokoju (do odtworzenia przez aplikację). Url względny (/sounds/… albo /uploads/…).</summary>
    public event Action<SoundPlay>? SoundPlayed;
    /// <summary>Kto właśnie puścił dźwięk (etykieta przy osobie na ~3 s).</summary>
    public Dictionary<long, (string Label, long Until)> SoundLabels { get; } = [];

    /// <summary>Puszcza dźwięk w pokoju: wbudowany (klucz) albo z serwera (id). Serwer pilnuje limitów i uprawnień.</summary>
    public void PlaySound(long? soundId, string? builtin)
    {
        if (VoiceChannelId == 0) { VoiceInfo?.Invoke("Najpierw wejdź na kanał głosowy", true); return; }
        if (Offline) { OnSound(VoiceChannelId, Store.Me.Id, builtin is null ? "/sounds/ding.mp3" : $"/sounds/{builtin}.mp3", 1, soundId, builtin); return; }
        _ = _gw?.Send(builtin is not null ? (object)new { t = "sound_play", builtin } : new { t = "sound_play", soundId });
    }

    public static readonly Dictionary<string, (string Emoji, string Name)> BuiltinSounds = new()
    {
        ["airhorn"] = ("📯", "Trąbka"), ["drumroll"] = ("🥁", "Werble"), ["rimshot"] = ("🥁", "Ba-dum-tss"), ["applause"] = ("👏", "Oklaski"),
        ["sad"] = ("😢", "Smutna trąbka"), ["boing"] = ("🤪", "Boing"), ["ding"] = ("🔔", "Ding"), ["tada"] = ("🎉", "Tadam"),
    };

    void OnSound(long cid, long uid, string url, double volume, long? soundId, string? builtin)
    {
        string label;
        if (builtin is not null) label = BuiltinSounds.TryGetValue(builtin, out var b) ? b.Emoji + " " + b.Name : "🔊 " + builtin;
        else
        {
            var snd = Store.ServerOfChannel(cid)?.Sounds.Find(x => x.Id == soundId);
            label = (string.IsNullOrEmpty(snd?.Emoji) ? "🔊" : snd!.Emoji) + " " + (snd?.Name ?? "dźwięk");
        }
        SoundLabels[uid] = (label, Environment.TickCount64 + 3500);
        if (!Voice.Options.Deafened) SoundPlayed?.Invoke(new SoundPlay(cid, uid, url, Math.Clamp(volume, 0, 1), label));
        VoiceChanged?.Invoke();
    }

    void OnVoiceEvent(GatewayEvent ev)
    {
        var d = ev.Data;
        OnE2eeEvent(ev); // 6.4
        switch (ev.Type)
        {
            case "voice_media":
            {
                var m = VoiceMediaJson.Parse(d);
                if (m is null || m.ChannelId != VoiceChannelId || VoiceChannelId == 0) return;
                _ = OnMedia(m); // 6.4: szyfrowany pokój — najpierw klucz rozmowy
                return;
            }
            case "voice_disconnect":
            {
                if (VoiceChannelId == 0) return;
                var reason = d.Str("reason");
                VoiceChannelId = 0;
                DropE2ee();
                _ = Voice.DisconnectAsync();
                VoiceInfo?.Invoke(reason == "moved" ? "Dołączono do rozmowy na innym urządzeniu" : "Rozłączono z kanału głosowego", false);
                VoiceChanged?.Invoke();
                return;
            }
            case "stream_preview":
                if (d.Long("userId") is long pu && d.Long("at") is long pat) { StreamPreviews[pu] = pat; VoiceChanged?.Invoke(); }
                return;
            case "watch_react":
                if (d.Long("channelId") is long rc2 && d.Long("userId") is long ru && d.Str("emoji") is { } emo) WatchReacted?.Invoke(rc2, ru, emo);
                return;
            case "error" when d.Str("op") is { } op && (op.StartsWith("watch_") || op.StartsWith("board_game_") || op == "voice_fx"):
                VoiceInfo?.Invoke(d.Str("error") ?? "Nie udało się", true);
                return;
            case "voice_state" when Fx is not null && d.Long("channelId") is long fc && fc == VoiceChannelId && !FxAllowed(fc):
                // 6.2: moderator wyłączył efekty na kanale — serwer zdjął mój efekt, zdejmuję też u siebie
                if (Store.Voice.GetValueOrDefault(fc)?.Find(m => m.UserId == Store.Me.Id) is { Fx: null })
                {
                    Fx = null; Voice.Fx.Effect = null;
                    VoiceInfo?.Invoke("Moderator wyłączył efekty głosu na tym kanale", false);
                    VoiceChanged?.Invoke();
                }
                return;
            case "error" when d.Str("op") == "sound_play":
                VoiceInfo?.Invoke(d.Str("error") ?? "Nie udało się puścić dźwięku", true);
                return;
            case "error" when d.Str("op") is "voice_join" or "voice_stream" or "voice_video":
            {
                var msg = d.Str("error") ?? "Nie udało się";
                // 6.4: 426 — do szyfrowanej rozmowy wchodzi tylko klient, który umie szyfrować
                if (d.Str("code") == "e2ee_required") msg = "Ta rozmowa jest szyfrowana od końca do końca, a ta wersja GibCorda nie może do niej dołączyć. Zaktualizuj GibCorda i spróbuj jeszcze raz.";
                if (d.Str("op") == "voice_join" && Voice.State == VoiceState.Idle) { VoiceChannelId = 0; DropE2ee(); VoiceChanged?.Invoke(); }
                if (d.Str("op") == "voice_stream" && Voice.ScreenSharing) _ = Voice.StopScreenShare();
                VoiceInfo?.Invoke(msg, true);
                return;
            }
            case "sound_play":
                if (d.Long("channelId") is long sc && sc == VoiceChannelId && d.Long("userId") is long su && d.Str("url") is { } surl && !Store.Blocked.Contains(su))
                    OnSound(sc, su, surl, d.TryGetProperty("volume", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : 1, d.Long("soundId"), d.Str("builtin"));
                return;
            case "call_ring":
                if (d.Long("channelId") is long rc && d.Long("from") is long from && rc != VoiceChannelId) CallRing?.Invoke(rc, from);
                return;
            case "call_end":
                if (d.Long("channelId") is long ec) CallEnded?.Invoke(ec);
                return;
            case "call_declined":
                if (d.Long("channelId") is long dc && dc == VoiceChannelId && d.Long("userId") is long who && who != Store.Me.Id)
                    VoiceInfo?.Invoke($"{Store.NameOf(who, null)} odrzuca rozmowę", false);
                return;
        }
    }

    async Task ConnectMedia(VoiceMedia m, E2ee.E2eeKey? key)
    {
        var o = Voice.Options;
        try
        {
            await Voice.ConnectAsync(m, BitrateOf(m.ChannelId), o.Muted || o.Deafened, o.Deafened, default, key);
            _voiceAttempts = 0;
        }
        catch (Exception ex)
        {
            _ui(() =>
            {
                if (VoiceChannelId != m.ChannelId) return;
                if (++_voiceAttempts <= 2) { _ = RetryMedia(m.ChannelId, 1500); return; }
                VoiceInfo?.Invoke("Nie udało się połączyć z serwerem głosu: " + ex.Message, true);
                _ = LeaveVoice();
            });
        }
    }

    /// <summary>Ponowne łączenie z głosem: świeży token przez REST, a gdy serwer nas już nie widzi — ponowne wejście.</summary>
    async Task RetryMedia(long channelId, int delayMs, int netTries = 0)
    {
        await Task.Delay(delayMs);
        if (VoiceChannelId != channelId || Voice.State != VoiceState.Idle) return;
        try
        {
            if (await Api.VoiceToken(channelId) is { } m && VoiceChannelId == channelId) { await OnMedia(m with { ChannelId = channelId }); return; }
        }
        catch (ApiException) { }
        catch
        {
            // chwilowy brak internetu: próbujemy dalej (rosnące odstępy, łącznie ok. 1,5 minuty), potem uczciwie wychodzimy
            if (netTries < 10) { _ = RetryMedia(channelId, Math.Min(2000 * (netTries + 1), 15000), netTries + 1); return; }
            _ui(() =>
            {
                if (VoiceChannelId != channelId) return;
                VoiceInfo?.Invoke("Brak połączenia z internetem — rozłączono z rozmową.", true);
                _ = LeaveVoice();
            });
            return;
        }
        var o = Voice.Options;
        if (VoiceChannelId == channelId && _gw is not null) await _gw.Send(new { t = "voice_join", channelId, muted = o.Muted || o.Deafened, deafened = o.Deafened, e2ee = WantsE2ee(channelId) });
    }

    void OnVoiceDropped(string text, bool final)
    {
        if (VoiceChannelId == 0) return;
        if (final || ++_voiceAttempts > 3)
        {
            VoiceInfo?.Invoke(text, !final);
            _ = LeaveVoice();
            return;
        }
        VoiceChanged?.Invoke();
        _ = RetryMedia(VoiceChannelId, 800 * _voiceAttempts);
    }

    /// <summary>Po ponownym połączeniu WebSocket serwer już nas nie ma na kanale — wracamy.</summary>
    void RejoinVoiceAfterReconnect()
    {
        if (VoiceChannelId == 0 || _gw is null) return;
        var o = Voice.Options;
        _ = _gw.Send(new { t = "voice_join", channelId = VoiceChannelId, muted = o.Muted || o.Deafened, deafened = o.Deafened, e2ee = WantsE2ee(VoiceChannelId) });
    }
}
