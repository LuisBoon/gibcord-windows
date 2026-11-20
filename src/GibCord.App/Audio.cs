using System.Runtime.InteropServices;
using GibCord.Core;
using GibCord.Core.Voice;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace GibCord.App;

public sealed record AudioDevice(string Id, string Name);

/// <summary>Lista mikrofonów i głośników z Windowsa (WASAPI).</summary>
public static class AudioDevices
{
    public static List<AudioDevice> Inputs() => List(DataFlow.Capture);
    public static List<AudioDevice> Outputs() => List(DataFlow.Render);

    static List<AudioDevice> List(DataFlow flow)
    {
        var list = new List<AudioDevice> { new("", flow == DataFlow.Capture ? "Domyślny mikrofon Windows" : "Domyślne głośniki Windows") };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                try { list.Add(new(d.ID, d.FriendlyName)); } catch { }
        }
        catch (Exception ex) { App.Log("Lista urządzeń dźwięku: " + ex.Message); }
        return list;
    }

    public static MMDevice? Get(string id, DataFlow flow)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(id)) { try { return en.GetDevice(id); } catch { } }
            return en.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? Role.Communications : Role.Multimedia);
        }
        catch { return null; }
    }
}

/// <summary>
/// Mikrofon i głośniki podczas rozmowy: WASAPI (tryb współdzielony) ↔ <see cref="VoiceEngine"/> (48 kHz mono).
/// Mikrofon jest otwarty tylko w rozmowie albo podczas testu w ustawieniach — Windows pokazuje wtedy ikonkę mikrofonu.
/// </summary>
public sealed class AudioIo : IDisposable
{
    readonly VoiceEngine _v;
    WasapiCapture? _cap;
    WasapiOut? _out;
    WdlResampler? _rs;
    int _inRate;
    float[] _mono = [], _res = [];

    public string? Error { get; private set; }
    public bool Capturing => _cap is not null;
    public bool Playing => _out is not null;

    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public AudioIo(VoiceEngine v) { _v = v; }

    public void Start(string inputId, string outputId, bool output = true)
    {
        Stop();
        Error = null;
        try { StartCapture(inputId); }
        catch (Exception ex) { Error = "Nie udało się włączyć mikrofonu: " + ex.Message; App.Log(Error); StopCapture(); }
        if (!output) return;
        try { StartOutput(outputId); }
        catch (Exception ex) { Error ??= "Nie udało się włączyć głośników: " + ex.Message; App.Log("Głośniki: " + ex); StopOutput(); }
    }

    void StartCapture(string inputId)
    {
        var dev = AudioDevices.Get(inputId, DataFlow.Capture) ?? throw new InvalidOperationException("brak mikrofonu");
        var cap = new WasapiCapture(dev, true, 20);
        var fmt = cap.WaveFormat;
        _inRate = fmt.SampleRate;
        _rs = null;
        if (_inRate != VoiceEngine.Rate)
        {
            _rs = new WdlResampler();
            _rs.SetMode(true, 2, false);
            _rs.SetFilterParms();
            _rs.SetFeedMode(true);
            _rs.SetRates(_inRate, VoiceEngine.Rate);
        }
        cap.DataAvailable += (_, e) => OnMic(e.Buffer, e.BytesRecorded, fmt);
        cap.RecordingStopped += (_, e) => { if (e.Exception is not null) { Error = "Mikrofon przestał działać: " + e.Exception.Message; App.Log(Error); } };
        cap.StartRecording();
        _cap = cap;
    }

    void OnMic(byte[] buf, int bytes, WaveFormat fmt)
    {
        var ch = Math.Max(1, fmt.Channels);
        int frames;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt is WaveFormatExtensible x && x.SubFormat == IeeeFloat))
        {
            var f = MemoryMarshal.Cast<byte, float>(buf.AsSpan(0, bytes - bytes % (4 * ch)));
            frames = f.Length / ch;
            Ensure(ref _mono, frames);
            for (var i = 0; i < frames; i++) { float s = 0; for (var c = 0; c < ch; c++) s += f[i * ch + c]; _mono[i] = s / ch; }
        }
        else if (fmt.BitsPerSample == 16)
        {
            var s16 = MemoryMarshal.Cast<byte, short>(buf.AsSpan(0, bytes - bytes % (2 * ch)));
            frames = s16.Length / ch;
            Ensure(ref _mono, frames);
            for (var i = 0; i < frames; i++) { float s = 0; for (var c = 0; c < ch; c++) s += s16[i * ch + c] / 32768f; _mono[i] = s / ch; }
        }
        else if (fmt.BitsPerSample == 24)
        {
            var step = 3 * ch;
            frames = bytes / step;
            Ensure(ref _mono, frames);
            for (var i = 0; i < frames; i++)
            {
                float s = 0;
                for (var c = 0; c < ch; c++) { var o = i * step + c * 3; s += ((buf[o] << 8 | buf[o + 1] << 16 | buf[o + 2] << 24) >> 8) / 8388608f; }
                _mono[i] = s / ch;
            }
        }
        else if (fmt.BitsPerSample == 32)
        {
            var s32 = MemoryMarshal.Cast<byte, int>(buf.AsSpan(0, bytes - bytes % (4 * ch)));
            frames = s32.Length / ch;
            Ensure(ref _mono, frames);
            for (var i = 0; i < frames; i++) { float s = 0; for (var c = 0; c < ch; c++) s += s32[i * ch + c] / 2147483648f; _mono[i] = s / ch; }
        }
        else return;
        if (frames == 0) return;
        if (_rs is null) { _v.PushMic(_mono.AsSpan(0, frames)); return; }
        var outMax = (int)((long)frames * VoiceEngine.Rate / _inRate) + 32;
        Ensure(ref _res, outMax);
        var want = _rs.ResamplePrepare(frames, 1, out var inBuf, out var inOff);
        Array.Copy(_mono, 0, inBuf, inOff, Math.Min(want, frames));
        var got = _rs.ResampleOut(_res, 0, frames, outMax, 1);
        if (got > 0) _v.PushMic(_res.AsSpan(0, got));
    }

    static void Ensure(ref float[] a, int n) { if (a.Length < n) a = new float[Math.Max(n, 4096)]; }

    void StartOutput(string outputId)
    {
        var dev = AudioDevices.Get(outputId, DataFlow.Render) ?? throw new InvalidOperationException("brak głośników");
        var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, 40);
        o.Init(new EngineOutput(_v));
        o.PlaybackStopped += (_, e) => { if (e.Exception is not null) { Error = "Głośniki przestały działać: " + e.Exception.Message; App.Log(Error); } };
        o.Play();
        _out = o;
    }

    void StopCapture() { var c = _cap; _cap = null; try { c?.StopRecording(); } catch { } try { c?.Dispose(); } catch { } }
    void StopOutput() { var o = _out; _out = null; try { o?.Stop(); } catch { } try { o?.Dispose(); } catch { } }

    public void Stop() { StopCapture(); StopOutput(); }
    public void Dispose() => Stop();

    /// <summary>Zmiksowany dźwięk rozmowy: 48 kHz stereo (mono powielone na dwa kanały).</summary>
    sealed class EngineOutput(VoiceEngine v) : ISampleProvider
    {
        float[] _m = new float[4800];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoiceEngine.Rate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            if (_m.Length < frames) _m = new float[frames];
            try { v.ReadOutput(_m.AsSpan(0, frames)); } catch { Array.Clear(_m, 0, frames); }
            for (var i = 0; i < frames; i++) { buffer[offset + 2 * i] = _m[i]; buffer[offset + 2 * i + 1] = _m[i]; }
            return frames * 2;
        }
    }
}

/// <summary>Krótkie dźwięki rozmów (wejście, wyjście, wyciszenie, dzwonek) — miękkie sinusy jak „Miękkie” na stronie.</summary>
public static class Tones
{
    static WasapiOut? _ring;

    public static void Play(string kind)
    {
        if (!App.Prefs.Sounds || Options.Demo) return;
        (double f, double ms)[] notes = kind switch
        {
            "join" => [(523.25, 90), (783.99, 140)],
            "leave" => [(659.25, 90), (440, 150)],
            "joinOther" => [(587.33, 110)],
            "leaveOther" => [(392, 120)],
            "mute" => [(440, 70)],
            "unmute" => [(660, 70)],
            _ => [(600, 80)],
        };
        _ = Task.Run(() =>
        {
            try
            {
                using var o = new WasapiOut(AudioClientShareMode.Shared, 60);
                o.Init(new Notes(notes, 0.18f));
                o.Play();
                while (o.PlaybackState == PlaybackState.Playing) Thread.Sleep(20);
            }
            catch { }
        });
    }

    /// <summary>Dzwonek przychodzącej rozmowy — gra w kółko, aż <see cref="StopRing"/>.</summary>
    public static void StartRing()
    {
        if (Options.Demo || _ring is not null) return;
        try
        {
            var o = new WasapiOut(AudioClientShareMode.Shared, 60);
            o.Init(new Notes([(659.25, 160), (0, 40), (830.61, 160), (0, 40), (987.77, 220), (0, 1200)], 0.16f, loop: true));
            o.Play();
            _ring = o;
        }
        catch { }
    }

    public static void StopRing()
    {
        var o = _ring; _ring = null;
        try { o?.Stop(); o?.Dispose(); } catch { }
    }

    sealed class Notes((double f, double ms)[] notes, float vol, bool loop = false) : ISampleProvider
    {
        int _i, _pos;
        double _phase;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            var n = 0;
            while (n < count)
            {
                if (_i >= notes.Length) { if (!loop) break; _i = 0; }
                var (f, ms) = notes[_i];
                var len = (int)(48000 * ms / 1000);
                var t = _pos / (double)len;
                var env = Math.Min(1, Math.Min(t * 12, (1 - t) * 6)); // szybki atak, łagodne wygaszenie
                buffer[offset + n++] = f <= 0 ? 0 : (float)(Math.Sin(_phase) * env * vol);
                _phase += 2 * Math.PI * f / 48000;
                if (++_pos >= len) { _pos = 0; _i++; _phase = 0; }
            }
            return n;
        }
    }
}

/// <summary>
/// Spina rozmowę z Windowsem: mikrofon/głośniki tylko w rozmowie, „naciśnij, aby mówić” (klawisz działa też w grze),
/// dźwięki wejścia/wyjścia osób i dzwonek.
/// </summary>
public sealed class VoiceHost
{
    readonly GibCordClient _c;
    readonly AudioIo _io;
    readonly DispatcherQueueTimer _ptt;
    long _room;
    HashSet<long> _members = [];
    string _devIn = "", _devOut = "";
    bool _testing;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    public VoiceHost(GibCordClient c, DispatcherQueue dq)
    {
        _c = c;
        _io = new AudioIo(c.Voice);
        _ptt = dq.CreateTimer();
        _ptt.Interval = TimeSpan.FromMilliseconds(15);
        _ptt.Tick += (_, _) =>
        {
            var o = _c.Voice.Options;
            _c.Voice.PttDown = o.Mode == "ptt" && o.PttKey > 0 && (GetAsyncKeyState(o.PttKey) & 0x8000) != 0;
        };
        c.VoiceChanged += Sync;
        c.Store.Changed += ch => { if (ch.Kind is ChangeKind.Voice or ChangeKind.All) MembersChanged(); };
    }

    public string? AudioError => _io.Error;

    /// <summary>Po zmianie urządzeń w ustawieniach.</summary>
    public void Restart()
    {
        if (!_io.Capturing && !_io.Playing) return;
        var o = _c.Voice.Options;
        _devIn = o.InputDevice; _devOut = o.OutputDevice;
        _io.Start(o.InputDevice, o.OutputDevice, output: !_testing || _c.VoiceChannelId != 0);
    }

    /// <summary>Test mikrofonu w ustawieniach (bez rozmowy): sam mikrofon, poziom w <see cref="VoiceEngine.MicDb"/>.</summary>
    public void SetTesting(bool on)
    {
        _testing = on;
        if (_c.VoiceChannelId != 0) return;
        if (on) _io.Start(_c.Voice.Options.InputDevice, "", output: false);
        else _io.Stop();
    }

    void Sync()
    {
        var inCall = _c.VoiceChannelId != 0 && !_c.Offline;
        var o = _c.Voice.Options;
        if (inCall && (!_io.Capturing || !_io.Playing || _devIn != o.InputDevice || _devOut != o.OutputDevice))
        {
            _devIn = o.InputDevice; _devOut = o.OutputDevice;
            _io.Start(o.InputDevice, o.OutputDevice);
        }
        else if (!inCall && (_io.Capturing || _io.Playing) && !_testing) _io.Stop();
        if (inCall && !_ptt.IsRunning) _ptt.Start();
        if (!inCall && _ptt.IsRunning) { _ptt.Stop(); _c.Voice.PttDown = false; }
        if (_c.VoiceChannelId != _room)
        {
            if (_room == 0 && _c.VoiceChannelId != 0) Tones.Play("join");
            else if (_room != 0 && _c.VoiceChannelId == 0) Tones.Play("leave");
            _room = _c.VoiceChannelId;
            _members = Members();
        }
    }

    HashSet<long> Members() => [.. (_c.Store.Voice.GetValueOrDefault(_room) ?? []).Select(m => m.UserId)];

    void MembersChanged()
    {
        if (_room == 0) return;
        var now = Members();
        var me = _c.Store.Me.Id;
        if (now.Except(_members).Any(u => u != me)) Tones.Play("joinOther");
        else if (_members.Except(now).Any(u => u != me)) Tones.Play("leaveOther");
        _members = now;
    }
}
