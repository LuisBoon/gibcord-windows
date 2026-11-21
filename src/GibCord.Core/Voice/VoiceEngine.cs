using System.Collections.Concurrent;
using System.Threading.Channels;
using LiveKit.Proto;
using LiveKit.Rtc;
using Room = LiveKit.Rtc.Room;
using RoomOptions = LiveKit.Rtc.RoomOptions;
using TrackPublishOptions = LiveKit.Rtc.TrackPublishOptions;
using AudioEncodingOptions = LiveKit.Rtc.AudioEncodingOptions;
using VideoEncodingOptions = LiveKit.Rtc.VideoEncodingOptions;
using AudioFrame = LiveKit.Rtc.AudioFrame;
using VideoFrame = LiveKit.Rtc.VideoFrame;
using AudioStream = LiveKit.Rtc.AudioStream;
using VideoStream = LiveKit.Rtc.VideoStream;
using AudioSource = LiveKit.Rtc.AudioSource;
using VideoSource = LiveKit.Rtc.VideoSource;
using Participant = LiveKit.Rtc.Participant;

namespace GibCord.Core.Voice;

/// <summary>Dane do połączenia z serwerem głosu (ramka <c>voice_media</c> albo POST /voice/token).</summary>
public sealed record VoiceMedia(long ChannelId, string Url, string Token, string Room, string Identity);

public enum VoiceState { Idle, Connecting, Connected, Reconnecting }

/// <summary>Obraz od jednej osoby (kamera albo udostępniony ekran) — ostatnia klatka do narysowania.</summary>
public sealed class VideoFeed
{
    readonly object _lock = new();
    byte[] _buf = [];
    int _w, _h;
    long _seq;

    public long UserId { get; init; }
    public bool Screen { get; init; }
    public string TrackSid { get; init; } = "";
    public int Width { get { lock (_lock) return _w; } }
    public int Height { get { lock (_lock) return _h; } }
    public long Sequence => Interlocked.Read(ref _seq);

    internal void Put(ReadOnlySpan<byte> bgra, int w, int h)
    {
        lock (_lock)
        {
            if (_buf.Length != bgra.Length) _buf = new byte[bgra.Length];
            bgra.CopyTo(_buf);
            _w = w; _h = h;
        }
        Interlocked.Increment(ref _seq);
    }

    /// <summary>Kopiuje najnowszą klatkę (BGRA), jeśli jest nowsza niż <paramref name="seen"/>.</summary>
    public bool TryCopy(ref long seen, Action<ReadOnlySpan<byte>, int, int> copy)
    {
        var s = Sequence;
        if (s == seen || s == 0) return false;
        lock (_lock) copy(_buf, _w, _h);
        seen = s;
        return true;
    }
}

/// <summary>
/// Rozmowa głosowa przez serwer LiveKit (dźwięk idzie: ja → serwer → reszta osób, bez połączeń „każdy z każdym”).
/// Okno podaje próbki z mikrofonu (<see cref="PushMic"/>) i pobiera zmiksowany dźwięk do głośników (<see cref="ReadOutput"/>);
/// wszystko inne — przetwarzanie, bramka głosu, subskrypcje, obraz — dzieje się tutaj. Działa też na Linuksie (testy).
/// </summary>
public sealed class VoiceEngine : IAsyncDisposable
{
    public const int Rate = 48000, FrameSamples = 480; // 10 ms
    const int Prebuffer = Rate * 40 / 1000, MaxLatency = Rate * 200 / 1000;
    const double SpeakDb = -48, SpeakHoldMs = 300;

    readonly object _gate = new();
    readonly ConcurrentDictionary<string, RemoteAudio> _audio = new();
    readonly ConcurrentDictionary<string, VideoFeed> _feeds = new();
    readonly ConcurrentDictionary<long, long> _remoteLoudAt = new();
    readonly HashSet<long> _watching = [];
    readonly System.Threading.Channels.Channel<AudioFrame> _micQ = System.Threading.Channels.Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    readonly float[] _micAcc = new float[FrameSamples];
    readonly float[] _revAcc = new float[FrameSamples];
    int _micFill, _revFill;
    Room? _room;
    AudioSource? _src;
    LocalAudioTrack? _micTrack;
    string? _micSid;
    AudioProcessingModule? _apm;
    (bool Ns, bool Ec, bool Agc) _apmCfg;
    Task? _micPump;
    CancellationTokenSource? _cts;
    VoiceMedia? _media;
    int _bitrate = 64;
    bool _camerasVisible = true;
    bool _clientLeaving;
    double _noiseFloor = -60, _micDb = -100;
    long _micLoudAt, _lastOpenAt, _pttUpAt;
    bool _pttWasDown;
    // udostępnianie ekranu
    VideoSource? _screenSrc;
    LocalVideoTrack? _screenTrack;
    string? _screenSid;
    AudioSource? _screenAudioSrc;
    LocalAudioTrack? _screenAudioTrack;
    string? _screenAudioSid;
    // kamera
    VideoSource? _camSrc;
    LocalVideoTrack? _camTrack;
    string? _camSid;

    public VoiceOptions Options { get; set; } = new();
    public VoiceState State { get; private set; } = VoiceState.Idle;
    public long ChannelId => _media?.ChannelId ?? 0;
    public string? RoomName => _media?.Room;
    public long MyId { get; private set; }
    public bool Muted { get; private set; }
    public bool Deafened { get; private set; }
    /// <summary>Serwer pozwala mówić (brak wyciszenia przez moderatora / uprawnienia „Mówienie”).</summary>
    public bool CanSpeak { get; private set; } = true;
    public bool CanStream { get; private set; } = true;
    /// <summary>Jakość połączenia: 3 świetna, 2 dobra, 1 słaba, 0 zerwane.</summary>
    public int Quality { get; private set; } = 3;
    /// <summary>Klawisz „naciśnij, aby mówić” wciśnięty (ustawia okno).</summary>
    public bool PttDown { get; set; }
    /// <summary>Poziom mikrofonu po przetworzeniu (dBFS) — do paska w ustawieniach.</summary>
    public double MicDb => _micDb;
    /// <summary>Obecny próg bramki głosu (dB).</summary>
    public double Threshold => Options.AutoSens ? Math.Clamp(_noiseFloor + 14, -62, -24) : Options.Sens;
    public bool Transmitting => Environment.TickCount64 - Interlocked.Read(ref _micLoudAt) < SpeakHoldMs && !Muted && CanSpeak;
    public bool ScreenSharing => _screenTrack is not null;
    public bool CameraOn => _camTrack is not null;
    public IReadOnlyCollection<long> Watching { get { lock (_gate) return [.. _watching]; } }
    public IEnumerable<VideoFeed> Feeds => _feeds.Values;
    public int RemoteAudioCount => _audio.Count;

    /// <summary>Zmiana stanu: połączenie, uprawnienia, lista obrazów (wątek w tle).</summary>
    public event Action? Changed;
    /// <summary>Rozłączenie, którego nie zleciliśmy (np. moderator, zerwane połączenie) — powód po polsku.</summary>
    public event Action<string, bool>? Dropped;

    void Raise() { try { Changed?.Invoke(); } catch { } }

    // ───────────────────────── połączenie ─────────────────────────

    public async Task ConnectAsync(VoiceMedia media, int bitrateKbps, bool muted, bool deafened, CancellationToken ct = default)
    {
        if (_room is { IsConnected: true } && _media?.Room == media.Room)
        {
            _media = media; // nowy token na później (np. ponowne łączenie)
            return;
        }
        await DisconnectAsync().ConfigureAwait(false);
        _media = media;
        _bitrate = bitrateKbps <= 0 ? 64 : Math.Clamp(bitrateKbps, 8, 256);
        MyId = long.TryParse(media.Identity, out var me) ? me : 0;
        Muted = muted || deafened;
        Deafened = deafened;
        CanSpeak = true; CanStream = true; Quality = 3;
        _clientLeaving = false;
        State = VoiceState.Connecting;
        Raise();
        var room = new Room();
        Wire(room);
        _room = room;
        _cts = new CancellationTokenSource();
        try
        {
            await room.ConnectAsync(media.Url, media.Token, new RoomOptions { AutoSubscribe = false, Dynacast = true, AdaptiveStream = false, JoinRetries = 2 }, ct).ConfigureAwait(false);
        }
        catch
        {
            if (ReferenceEquals(_room, room)) { _room = null; _media = null; State = VoiceState.Idle; }
            try { room.Dispose(); } catch { }
            Raise();
            throw;
        }
        if (!ReferenceEquals(_room, room)) return; // w międzyczasie rozłączono
        State = VoiceState.Connected;
        foreach (var p in room.RemoteParticipants.Values) ApplySubscriptions(p);
        Raise();
        _micPump = Task.Run(() => MicPump(_cts.Token));
        await PublishMic().ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        var room = _room;
        _room = null;
        _clientLeaving = true;
        _cts?.Cancel();
        _cts = null;
        foreach (var a in _audio.Values) a.Dispose();
        _audio.Clear();
        _feeds.Clear();
        _remoteLoudAt.Clear();
        lock (_gate) _watching.Clear();
        _micTrack = null; _micSid = null;
        _screenTrack = null; _screenSid = null; _screenAudioTrack = null; _screenAudioSid = null;
        var cam = _camSrc; _camSrc = null; _camTrack = null; _camSid = null;
        try { cam?.Dispose(); } catch { }
        var src = _src; _src = null;
        var ss = _screenSrc; _screenSrc = null;
        var sas = _screenAudioSrc; _screenAudioSrc = null;
        while (_micQ.Reader.TryRead(out _)) { }
        if (room is not null)
        {
            try { await room.DisconnectAsync().ConfigureAwait(false); } catch { }
            try { room.Dispose(); } catch { }
        }
        try { src?.Dispose(); } catch { }
        try { ss?.Dispose(); } catch { }
        try { sas?.Dispose(); } catch { }
        if (_micPump is { } pump) { try { await pump.ConfigureAwait(false); } catch { } }
        _micPump = null;
        _media = null;
        if (State != VoiceState.Idle) { State = VoiceState.Idle; Raise(); }
    }

    void Wire(Room room)
    {
        room.TrackPublished += (_, e) => { if (ReferenceEquals(_room, room)) Decide(e.Publication, e.Participant); };
        room.TrackSubscribed += (_, e) => { if (ReferenceEquals(_room, room)) OnSubscribed(e.Track, e.Publication, e.Participant); };
        room.TrackUnsubscribed += (_, e) => Remove(e.Publication.Sid);
        room.TrackUnpublished += (_, e) => Remove(e.Publication.Sid);
        room.ParticipantDisconnected += (_, p) =>
        {
            foreach (var pub in p.TrackPublications.Values) Remove(pub.Sid);
            if (Uid(p) is long u) _remoteLoudAt.TryRemove(u, out long _);
        };
        room.ConnectionQualityChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Participant, room.LocalParticipant) && e.Participant?.Identity != _media?.Identity) return;
            Quality = e.Quality switch { ConnectionQuality.QualityExcellent => 3, ConnectionQuality.QualityGood => 2, ConnectionQuality.QualityPoor => 1, _ => 0 };
            Raise();
        };
        room.Reconnecting += (_, _) => { if (ReferenceEquals(_room, room)) { State = VoiceState.Reconnecting; Raise(); } };
        room.Reconnected += (_, _) => { if (ReferenceEquals(_room, room)) { State = VoiceState.Connected; Raise(); } };
        room.ParticipantPermissionsChanged += (_, e) =>
        {
            if (e.Participant?.Identity != _media?.Identity || e.Permissions is not { } perm) return;
            var speak = perm.CanPublish && (perm.CanPublishSources.Count == 0 || perm.CanPublishSources.Contains(TrackSource.SourceMicrophone));
            var stream = perm.CanPublish && (perm.CanPublishSources.Count == 0 || perm.CanPublishSources.Contains(TrackSource.SourceScreenshare));
            var was = CanSpeak;
            CanSpeak = speak; CanStream = stream;
            if (!speak) while (_micQ.Reader.TryRead(out AudioFrame? _)) { }
            if (speak && !was && ReferenceEquals(_room, room)) _ = PublishMic();
            if (!stream && _screenTrack is not null) _ = StopScreenShare();
            Raise();
        };
        room.Disconnected += (_, reason) =>
        {
            if (!ReferenceEquals(_room, room)) return; // rozłączyliśmy sami
            var leaving = _clientLeaving;
            _ = Task.Run(async () =>
            {
                await DisconnectAsync().ConfigureAwait(false);
                if (leaving || reason == DisconnectReason.ClientInitiated) return;
                var (text, final) = reason switch
                {
                    DisconnectReason.ParticipantRemoved => ("Rozłączono z kanału głosowego", false),
                    DisconnectReason.RoomDeleted or DisconnectReason.RoomClosed => ("Kanał głosowy został zamknięty", true),
                    DisconnectReason.DuplicateIdentity => ("Dołączono do rozmowy na innym urządzeniu", true),
                    DisconnectReason.ServerShutdown => ("Serwer głosu uruchamia się ponownie", false),
                    _ => ("Zerwane połączenie z serwerem głosu", false),
                };
                try { Dropped?.Invoke(text, final); } catch { }
            });
        };
    }

    static long? Uid(Participant? p) => p is not null && long.TryParse(p.Identity, out var u) ? u : null;

    // ───────────────────────── subskrypcje (oglądanie na żądanie) ─────────────────────────

    void ApplySubscriptions(Participant p)
    {
        foreach (var pub in p.TrackPublications.Values)
            if (pub is RemoteTrackPublication rp) Decide(rp, p);
    }

    void Decide(RemoteTrackPublication pub, Participant p)
    {
        var uid = Uid(p) ?? 0;
        bool want;
        lock (_gate)
            want = pub.Source switch
            {
                TrackSource.SourceMicrophone => true,
                TrackSource.SourceCamera => _camerasVisible,
                TrackSource.SourceScreenshare or TrackSource.SourceScreenshareAudio => _watching.Contains(uid),
                _ => false,
            };
        try { if (pub.IsSubscribed != want) pub.SetSubscribed(want); } catch { }
    }

    void Resubscribe(long uid)
    {
        if (_room is not { } room) return;
        foreach (var p in room.RemoteParticipants.Values)
            if (Uid(p) == uid) ApplySubscriptions(p);
    }

    /// <summary>Zacznij oglądać transmisję osoby (obraz + dźwięk ekranu).</summary>
    public void Watch(long uid) { lock (_gate) _watching.Add(uid); Resubscribe(uid); Raise(); }
    public void Unwatch(long uid) { lock (_gate) _watching.Remove(uid); Resubscribe(uid); Raise(); }
    public bool IsWatching(long uid) { lock (_gate) return _watching.Contains(uid); }

    /// <summary>Kamery pobieramy tylko, gdy widok kanału jest na ekranie.</summary>
    public void SetCamerasVisible(bool on)
    {
        lock (_gate) { if (_camerasVisible == on) return; _camerasVisible = on; }
        if (_room is { } room) foreach (var p in room.RemoteParticipants.Values) ApplySubscriptions(p);
    }

    void OnSubscribed(Track track, RemoteTrackPublication pub, Participant p)
    {
        var uid = Uid(p) ?? 0;
        if (track.Kind == TrackKind.KindAudio)
        {
            var ra = new RemoteAudio(uid, pub.Source == TrackSource.SourceScreenshareAudio, this);
            if (!_audio.TryAdd(pub.Sid, ra)) { ra.Dispose(); return; }
            ra.Start(track);
        }
        else if (track.Kind == TrackKind.KindVideo)
        {
            var feed = new VideoFeed { UserId = uid, Screen = pub.Source == TrackSource.SourceScreenshare, TrackSid = pub.Sid };
            if (!_feeds.TryAdd(pub.Sid, feed)) return;
            var ct = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var vs = new VideoStream(track, VideoBufferType.Bgra, 2);
                    await foreach (var ev in vs.WithCancellation(ct).ConfigureAwait(false))
                    {
                        if (!_feeds.ContainsKey(pub.Sid)) break;
                        var f = ev.Frame;
                        feed.Put(f.Data, f.Width, f.Height);
                    }
                }
                catch { }
            }, ct);
            Raise();
        }
    }

    void Remove(string sid)
    {
        if (_audio.TryRemove(sid, out var a)) a.Dispose();
        if (_feeds.TryRemove(sid, out _)) Raise();
    }

    // ───────────────────────── mikrofon ─────────────────────────

    async Task PublishMic()
    {
        var room = _room;
        if (room?.LocalParticipant is not { } lp || _micTrack is not null || !CanSpeak) return;
        try
        {
            _src ??= new AudioSource(Rate, 1, 200);
            var track = LocalAudioTrack.Create("microphone", _src);
            var pub = await lp.PublishTrackAsync(track, new TrackPublishOptions
            {
                Source = TrackSource.SourceMicrophone, Dtx = true, Red = true,
                AudioEncoding = new AudioEncodingOptions { MaxBitrate = (uint)(_bitrate * 1000) },
            }).ConfigureAwait(false);
            _micTrack = track;
            _micSid = pub.Sid;
            if (Muted) track.Mute();
        }
        catch (Exception)
        {
            // brak uprawnienia do mówienia (np. wyciszenie przez moderatora) — słuchamy dalej
            CanSpeak = false;
            Raise();
        }
    }

    public void SetMuted(bool muted)
    {
        Muted = muted || Deafened;
        try { if (Muted) _micTrack?.Mute(); else _micTrack?.Unmute(); } catch { }
        if (Muted) while (_micQ.Reader.TryRead(out _)) { }
        Raise();
    }

    public void SetDeafened(bool deafened)
    {
        Deafened = deafened;
        SetMuted(deafened || Options.Muted);
    }

    /// <summary>Zmiana jakości kanału (kb/s) — działa od następnego połączenia z kanałem.</summary>
    public void SetBitrate(int kbps) => _bitrate = Math.Clamp(kbps, 8, 256);

    AudioProcessingModule? Apm()
    {
        var cfg = (Options.NoiseSuppression, Options.EchoCancellation, Options.AutoGain);
        if (cfg == _apmCfg && (_apm is not null || cfg == (false, false, false))) return _apm;
        try { _apm?.Dispose(); } catch { }
        _apm = null;
        _apmCfg = cfg;
        if (cfg == (false, false, false)) return null;
        try
        {
            _apm = new AudioProcessingModule(cfg.Item2, cfg.Item1, true, cfg.Item3);
            _apm.SetStreamDelayMs(60);
        }
        catch { _apm = null; }
        return _apm;
    }

    /// <summary>
    /// Próbki z mikrofonu (mono, 48 kHz, −1…1), dowolnej długości — wątek nagrywania.
    /// Tnie na ramki 10 ms, przetwarza (szumy, echo, głośność), pilnuje bramki i wysyła.
    /// </summary>
    public void PushMic(ReadOnlySpan<float> samples)
    {
        var gain = (float)Math.Clamp(Options.InputGain, 0, 2);
        foreach (var s in samples)
        {
            _micAcc[_micFill++] = s * gain;
            if (_micFill == FrameSamples) { _micFill = 0; MicFrame(); }
        }
    }

    void MicFrame()
    {
        var pcm = new short[FrameSamples];
        for (var i = 0; i < FrameSamples; i++) pcm[i] = Pcm.ToShort(_micAcc[i]);
        var frame = new AudioFrame(pcm, Rate, 1, FrameSamples);
        try { Apm()?.ProcessStream(frame); } catch { }
        var db = Pcm.Db(frame.Data);
        _micDb = db;
        var now = Environment.TickCount64;
        // szum tła: szybko w dół, powoli w górę (≈ 3 dB/s)
        _noiseFloor = db < _noiseFloor ? db * 0.3 + _noiseFloor * 0.7 : _noiseFloor + 0.03;
        _noiseFloor = Math.Clamp(_noiseFloor, -90, -20);
        bool open;
        if (Options.Mode == "ptt")
        {
            if (PttDown) { open = true; _pttWasDown = true; }
            else
            {
                if (_pttWasDown) { _pttUpAt = now; _pttWasDown = false; }
                open = now - _pttUpAt < Math.Clamp(Options.PttReleaseMs, 0, 2000);
            }
        }
        else
        {
            if (db > Threshold) _lastOpenAt = now;
            open = now - _lastOpenAt < SpeakHoldMs;
        }
        var send = !Muted && CanSpeak && _src is not null && _micTrack is not null;
        if (open && send && db > SpeakDb - 6) Interlocked.Exchange(ref _micLoudAt, now);
        if (!send) return;
        if (!open) frame.DataMutable.Clear(); // cisza (DTX wysyła wtedy prawie nic)
        _micQ.Writer.TryWrite(frame);
    }

    async Task MicPump(CancellationToken ct)
    {
        try
        {
            while (await _micQ.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                while (_micQ.Reader.TryRead(out var f))
                {
                    var src = _src;
                    if (src is null) continue;
                    try { await src.CaptureFrameAsync(f, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch { }
                }
        }
        catch (OperationCanceledException) { }
    }

    // ───────────────────────── głośniki ─────────────────────────

    /// <summary>Zmiksowany dźwięk wszystkich osób (mono, 48 kHz) — wątek odtwarzania.</summary>
    public void ReadOutput(Span<float> dst)
    {
        dst.Clear();
        if (!Deafened)
        {
            var master = (float)Math.Clamp(Options.OutputVolume, 0, 2);
            foreach (var a in _audio.Values)
            {
                if (Options.UserMuted.Contains(a.UserId)) { a.Ring.Clear(); continue; }
                var g = master * (float)Options.VolumeOf(a.UserId) * (a.Screen ? (float)Math.Clamp(Options.StreamVolume, 0, 2) : 1f);
                a.Ring.MixInto(dst, g, Prebuffer, MaxLatency);
            }
            for (var i = 0; i < dst.Length; i++) dst[i] = Pcm.SoftClip(dst[i]);
        }
        // to, co gra w głośnikach, trafia do usuwania echa
        if (_apm is { } apm && Options.EchoCancellation)
            foreach (var s in dst)
            {
                _revAcc[_revFill++] = s;
                if (_revFill < FrameSamples) continue;
                _revFill = 0;
                var pcm = new short[FrameSamples];
                for (var i = 0; i < FrameSamples; i++) pcm[i] = Pcm.ToShort(_revAcc[i]);
                try { apm.ProcessReverseStream(new AudioFrame(pcm, Rate, 1, FrameSamples)); } catch { }
            }
    }

    // ───────────────────────── kto mówi ─────────────────────────

    internal void Heard(long uid, double db)
    {
        var now = Environment.TickCount64;
        if (db > SpeakDb) _remoteLoudAt[uid] = now;
        _remoteDb[uid] = (db, now);
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<long, (double Db, long At)> _remoteDb = new();

    /// <summary>Głośność mówienia 0–1 (do pulsującej obwódki): −50 dB = 0, −15 dB = 1. Dla mnie: mikrofon, gdy nadaje.</summary>
    public double Level(long uid)
    {
        if (State == VoiceState.Idle) return 0;
        static double Norm(double db) => Math.Clamp((db + 50) / 35, 0, 1);
        if (uid == MyId) return Transmitting ? Norm(_micDb) : 0;
        return _remoteDb.TryGetValue(uid, out var v) && Environment.TickCount64 - v.At < 250 ? Norm(v.Db) : 0;
    }

    /// <summary>Czy osoba właśnie mówi (zielona obwódka). Dla mnie: czy mikrofon właśnie nadaje.</summary>
    public bool IsSpeaking(long uid)
    {
        if (State == VoiceState.Idle) return false;
        if (uid == MyId) return Transmitting;
        return _remoteLoudAt.TryGetValue(uid, out var t) && Environment.TickCount64 - t < SpeakHoldMs;
    }

    /// <summary>Ile osób słyszymy (z działającym dźwiękiem) — do testów i diagnostyki.</summary>
    public IReadOnlyList<long> AudibleUsers => [.. _audio.Values.Where(a => !a.Screen).Select(a => a.UserId).Distinct()];

    // ───────────────────────── udostępnianie ekranu ─────────────────────────

    /// <summary>Zaczyna transmisję: zwraca funkcję, do której okno podaje klatki BGRA (szer., wys.).</summary>
    public async Task<Action<byte[], int, int>?> StartScreenShare(int width, int height, int fps, bool withAudio)
    {
        var room = _room;
        if (room?.LocalParticipant is not { } lp) return null;
        await StopScreenShare().ConfigureAwait(false);
        var src = new VideoSource(width, height, isScreencast: true);
        var track = LocalVideoTrack.Create("screen", src);
        var br = (uint)(width * height >= 2560 * 1440 ? 8_000_000 : width * height >= 1920 * 1080 ? (fps > 30 ? 6_000_000 : 4_000_000) : (fps > 30 ? 3_500_000 : 2_500_000));
        var pub = await lp.PublishTrackAsync(track, new TrackPublishOptions
        {
            Source = TrackSource.SourceScreenshare, Simulcast = false, VideoCodec = VideoCodec.H264,
            VideoEncoding = new VideoEncodingOptions { MaxBitrate = br, MaxFramerate = (uint)fps },
        }).ConfigureAwait(false);
        _screenSrc = src; _screenTrack = track; _screenSid = pub.Sid;
        if (withAudio)
        {
            try
            {
                _screenAudioSrc = new AudioSource(Rate, 2, 200);
                _screenAudioTrack = LocalAudioTrack.Create("screen-audio", _screenAudioSrc);
                var ap = await lp.PublishTrackAsync(_screenAudioTrack, new TrackPublishOptions
                {
                    Source = TrackSource.SourceScreenshareAudio, Dtx = false, Red = false,
                    AudioEncoding = new AudioEncodingOptions { MaxBitrate = 128_000 },
                }).ConfigureAwait(false);
                _screenAudioSid = ap.Sid;
            }
            catch { _screenAudioSrc = null; _screenAudioTrack = null; }
        }
        Raise();
        return (bgra, w, h) =>
        {
            var s = _screenSrc;
            if (s is null) return;
            try { s.CaptureFrame(new VideoFrame(w, h, VideoBufferType.Bgra, bgra)); } catch { }
        };
    }

    /// <summary>Dźwięk z ekranu (stereo przeplatane, 48 kHz, 10 ms = 960 próbek).</summary>
    public void PushScreenAudio(short[] interleavedStereo10ms)
    {
        var s = _screenAudioSrc;
        if (s is null) return;
        try { s.CaptureFrame(new AudioFrame(interleavedStereo10ms, Rate, 2, interleavedStereo10ms.Length / 2)); } catch { }
    }

    public bool ScreenAudio => _screenAudioTrack is not null;

    // ───────────────────────── kamera ─────────────────────────

    /// <summary>Włącza kamerę w rozmowie: zwraca funkcję, do której okno podaje klatki BGRA.</summary>
    public async Task<Action<byte[], int, int>?> StartCamera(int width, int height)
    {
        var room = _room;
        if (room?.LocalParticipant is not { } lp) return null;
        await StopCamera().ConfigureAwait(false);
        var src = new VideoSource(width, height, isScreencast: false);
        var track = LocalVideoTrack.Create("camera", src);
        var pub = await lp.PublishTrackAsync(track, new TrackPublishOptions
        {
            Source = TrackSource.SourceCamera, Simulcast = false,
            VideoEncoding = new VideoEncodingOptions { MaxBitrate = 600_000, MaxFramerate = 24 },
        }).ConfigureAwait(false);
        _camSrc = src; _camTrack = track; _camSid = pub.Sid;
        Raise();
        return (bgra, w, h) =>
        {
            var s = _camSrc;
            if (s is null) return;
            try { s.CaptureFrame(new VideoFrame(w, h, VideoBufferType.Bgra, bgra)); } catch { }
        };
    }

    public async Task StopCamera()
    {
        var lp = _room?.LocalParticipant;
        var sid = _camSid; var src = _camSrc;
        _camSrc = null; _camTrack = null; _camSid = null;
        if (lp is not null && sid is not null) { try { await lp.UnpublishTrackAsync(sid).ConfigureAwait(false); } catch { } }
        try { src?.Dispose(); } catch { }
        Raise();
    }

    public async Task StopScreenShare()
    {
        var lp = _room?.LocalParticipant;
        var sid = _screenSid; var asid = _screenAudioSid;
        var src = _screenSrc; var asrc = _screenAudioSrc;
        _screenSrc = null; _screenTrack = null; _screenSid = null;
        _screenAudioSrc = null; _screenAudioTrack = null; _screenAudioSid = null;
        if (lp is not null)
        {
            if (sid is not null) { try { await lp.UnpublishTrackAsync(sid).ConfigureAwait(false); } catch { } }
            if (asid is not null) { try { await lp.UnpublishTrackAsync(asid).ConfigureAwait(false); } catch { } }
        }
        try { src?.Dispose(); } catch { }
        try { asrc?.Dispose(); } catch { }
        Raise();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        try { _apm?.Dispose(); } catch { }
        _apm = null;
    }

    // ───────────────────────── dźwięk jednej osoby ─────────────────────────

    sealed class RemoteAudio(long uid, bool screen, VoiceEngine owner) : IDisposable
    {
        readonly CancellationTokenSource _cts = new();
        public long UserId { get; } = uid;
        public bool Screen { get; } = screen;
        public SampleRing Ring { get; } = new(Rate * (screen ? 2 : 1) / 2);

        public void Start(Track track)
        {
            var ct = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var stream = new AudioStream(track, (uint)Rate, 1u);
                    await foreach (var ev in stream.WithCancellation(ct).ConfigureAwait(false))
                    {
                        var f = ev.Frame;
                        Ring.Write(f.Data);
                        if (!Screen) owner.Heard(UserId, Pcm.Db(f.Data));
                    }
                }
                catch { }
            }, ct);
        }

        public void Dispose() { try { _cts.Cancel(); } catch { } Ring.Clear(); }
    }
}
