using LiveKit.Proto;

namespace GibCord.Core.Voice;

/// <summary>Jedna próbka obrazu (nadawanie albo odbiór) ze statystyk WebRTC.</summary>
public sealed record StreamSample(double T, long Frames, long Bytes, int W, int H, string? Codec, string? Impl, bool? Hw,
    double Cpu, double Bw, double Other, long Freezes, double FreezeSec, long Dropped, long Lost, long Pkts, double? Jitter, double? Rtt, double? LossOut);

/// <summary>Zmiana między dwiema próbkami (na sekundę albo sumy z tego okresu).</summary>
public sealed record StreamDelta(double Dt, double Fps, double Kbps, int H, double Cpu, double Bw, double Other, long Freezes, double FreezeSec, long Dropped, long Lost, long Pkts);

/// <summary>Obraz „tnie”: przez procesor („cpu”) albo łącze („bandwidth”).</summary>
public sealed record StreamTrouble(string Reason, int Fps, int H, bool Slow);

/// <summary>
/// 25.11.2026 czujnik cięcia transmisji — ta sama logika co na stronie (media.js: streamSample / streamTrouble).
/// Co 2 s próbka, z ostatnich ~8 s ocena, czy nadawany obraz zwalnia z winy komputera albo łącza, co 30 s podsumowanie na serwer
/// (POST /api/diag/stream), żeby przy „tnie” było widać przyczynę.
/// </summary>
public sealed class StreamProbe
{
    public bool Send { get; }
    public long? Peer { get; }
    public long Since { get; }
    StreamSample? _prev;
    public StreamSample? Last { get; private set; }
    readonly List<StreamDelta> _win = [];
    Acc? _acc;
    sealed class Acc { public double T, F, MinFps = double.PositiveInfinity, Kb, Cpu, Bw, Other, FreezeSec; public long Freezes, Dropped, Lost, Pkts; public string? Hint; public long Since; }

    public StreamProbe(bool send, long? peer, long nowMs) { Send = send; Peer = peer; Since = nowMs; }
    public IReadOnlyList<StreamDelta> Window => _win;

    public static StreamSample? Read(IEnumerable<RtcStats> stats, bool send)
    {
        var all = stats.ToList();
        var codecs = all.Where(s => s.StatsCase == RtcStats.StatsOneofCase.Codec).ToDictionary(s => s.Codec.Rtc.Id, s => s.Codec.Codec_.MimeType);
        StreamSample? best = null;
        double? rtt = null, lossOut = null;
        foreach (var s in all)
        {
            if (send && s.StatsCase == RtcStats.StatsOneofCase.RemoteInboundRtp && s.RemoteInboundRtp.Stream.Kind == "video")
            { rtt = s.RemoteInboundRtp.RemoteInbound.RoundTripTime * 1000; lossOut = s.RemoteInboundRtp.RemoteInbound.FractionLost * 100; }
            StreamSample? x = null;
            if (send && s.StatsCase == RtcStats.StatsOneofCase.OutboundRtp && s.OutboundRtp.Stream.Kind == "video")
            {
                var o = s.OutboundRtp.Outbound; var q = o.QualityLimitationDurations;
                x = new StreamSample(s.OutboundRtp.Rtc.Timestamp, (long)o.FramesEncoded, (long)s.OutboundRtp.Sent.BytesSent, (int)o.FrameWidth, (int)o.FrameHeight,
                    Mime(codecs, s.OutboundRtp.Stream.CodecId), o.HasEncoderImplementation ? o.EncoderImplementation : null, o.HasPowerEfficientEncoder ? o.PowerEfficientEncoder : null,
                    Q(q, "cpu"), Q(q, "bandwidth"), Q(q, "other"), 0, 0, 0, 0, 0, null, null, null);
            }
            else if (!send && s.StatsCase == RtcStats.StatsOneofCase.InboundRtp && s.InboundRtp.Stream.Kind == "video")
            {
                var i = s.InboundRtp.Inbound; var r = s.InboundRtp.Received;
                x = new StreamSample(s.InboundRtp.Rtc.Timestamp, (long)i.FramesDecoded, (long)i.BytesReceived, (int)i.FrameWidth, (int)i.FrameHeight,
                    Mime(codecs, s.InboundRtp.Stream.CodecId), i.HasDecoderImplementation ? i.DecoderImplementation : null, i.HasPowerEfficientDecoder ? i.PowerEfficientDecoder : null,
                    0, 0, 0, (long)i.FreezeCount, i.TotalFreezeDuration, (long)i.FramesDropped, Math.Max(0, r.PacketsLost), (long)r.PacketsReceived, r.Jitter * 1000, null, null);
            }
            if (x is not null && (best is null || x.W * x.H >= best.W * best.H)) best = x;
        }
        return best is null ? null : best with { Rtt = rtt, LossOut = lossOut };
    }

    static double Q(Google.Protobuf.Collections.MapField<string, double>? q, string k) => q is not null && q.TryGetValue(k, out var v) ? v : 0;

    static string? Mime(Dictionary<string, string> codecs, string id)
    {
        if (!codecs.TryGetValue(id, out var m) || string.IsNullOrEmpty(m)) return null;
        var i = m.IndexOf('/');
        return (i >= 0 ? m[(i + 1)..] : m).ToLowerInvariant();
    }

    public static StreamDelta? Delta(StreamSample s, StreamSample? p)
    {
        if (p is null || s.T <= p.T || s.Frames < p.Frames || s.Bytes < p.Bytes) return null;
        var dt = (s.T - p.T) / 1000;
        static double D(double a, double b) => Math.Max(0, a - b);
        static long L(long a, long b) => Math.Max(0, a - b);
        return new StreamDelta(dt, (s.Frames - p.Frames) / dt, (s.Bytes - p.Bytes) * 8 / 1000.0 / dt, s.H,
            D(s.Cpu, p.Cpu), D(s.Bw, p.Bw), D(s.Other, p.Other), L(s.Freezes, p.Freezes), D(s.FreezeSec, p.FreezeSec), L(s.Dropped, p.Dropped), L(s.Lost, p.Lost), L(s.Pkts, p.Pkts));
    }

    /// <summary>Z ostatnich ≥ 6 s: ograniczenie przez co najmniej połowę czasu i wyraźnie mniej klatek albo niższy obraz. Nieruchomy ekran to nie problem.</summary>
    public static StreamTrouble? Trouble(IReadOnlyList<StreamDelta> win, int targetFps, int targetH)
    {
        var t = win.Sum(x => x.Dt); if (t < 6) return null;
        var cpu = win.Sum(x => x.Cpu); var bw = win.Sum(x => x.Bw);
        if (cpu + bw < t * 0.5) return null;
        var fps = win.Sum(x => x.Fps * x.Dt) / t; var h = win.Min(x => x.H);
        var slow = fps < targetFps * 0.7; var soft = targetH > 0 && h > 0 && h < targetH * 0.75;
        return slow || soft ? new StreamTrouble(cpu >= bw ? "cpu" : "bandwidth", (int)Math.Round(fps), h, slow) : null;
    }

    /// <summary>Nowa próbka. Zwraca różnicę (null przy pierwszej); <paramref name="report"/> co ~30 s gotowe podsumowanie dla serwera.</summary>
    public StreamDelta? Add(StreamSample s, long nowMs, Func<Dictionary<string, object?>>? extra, out Dictionary<string, object?>? report)
    {
        report = null;
        var d = Delta(s, _prev);
        _prev = s; Last = s;
        if (d is null) return null;
        _win.Add(d);
        while (_win.Count > 1 && _win.Sum(x => x.Dt) - _win[0].Dt >= 8) _win.RemoveAt(0);
        var a = _acc ??= new Acc { Since = nowMs };
        a.T += d.Dt; a.F += d.Fps * d.Dt; a.MinFps = Math.Min(a.MinFps, d.Fps); a.Kb += d.Kbps * d.Dt;
        a.Cpu += d.Cpu; a.Bw += d.Bw; a.Other += d.Other; a.Freezes += d.Freezes; a.FreezeSec += d.FreezeSec; a.Dropped += d.Dropped; a.Lost += d.Lost; a.Pkts += d.Pkts;
        if (nowMs - a.Since >= 30_000) report = Flush(false, extra);
        return d;
    }

    public void MarkHint(string reason) { if (_acc is { } a) a.Hint = reason; }

    /// <summary>Podsumowanie od ostatniego raportu (null, gdy obraz był wstrzymany — nikt nie oglądał).</summary>
    public Dictionary<string, object?>? Flush(bool end, Func<Dictionary<string, object?>>? extra)
    {
        var a = _acc; _acc = null;
        if (a is null || a.T < 5 || a.F <= 0) return null;
        var l = Last;
        static double? R1(double? v) => v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ? null : Math.Round(v.Value, 1);
        var d = new Dictionary<string, object?>
        {
            ["role"] = Send ? "send" : "recv", ["codec"] = l?.Codec, ["impl"] = l?.Impl, ["hw"] = l?.Hw, ["w"] = l?.W, ["h"] = l?.H,
            ["sec"] = R1(a.T), ["fps"] = R1(a.F / a.T), ["minFps"] = R1(a.MinFps), ["kbps"] = (long)Math.Round(a.Kb / a.T), ["end"] = end,
        };
        if (Send) { d["cpu"] = R1(a.Cpu); d["bw"] = R1(a.Bw); d["other"] = R1(a.Other); d["rtt"] = R1(l?.Rtt); d["lossOut"] = R1(l?.LossOut); d["hint"] = a.Hint; }
        else { d["peer"] = Peer; d["freezes"] = a.Freezes; d["freezeSec"] = R1(a.FreezeSec); d["dropped"] = a.Dropped; d["loss"] = a.Pkts + a.Lost > 0 ? R1(a.Lost * 100.0 / (a.Pkts + a.Lost)) : 0; d["jitter"] = R1(l?.Jitter); }
        if (extra is not null) foreach (var (k, v) in extra()) d[k] = v;
        return d;
    }
}
