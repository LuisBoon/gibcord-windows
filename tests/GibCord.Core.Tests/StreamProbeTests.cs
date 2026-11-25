using GibCord.Core.Voice;
using LiveKit.Proto;

namespace GibCord.Core.Tests;

/// <summary>25.11.2026 czujnik cięcia transmisji: odczyt statystyk i ocena „tnie / nie tnie” (ta sama logika co media.js).</summary>
public sealed class StreamProbeTests
{
    static RtcStats Out(double t, ulong frames, ulong bytes, uint h, double cpu, double bw, string impl = "OpenH264")
    {
        var o = new OutboundRtpStreamStats { FramesEncoded = (uint)frames, FrameWidth = h * 16 / 9, FrameHeight = h, EncoderImplementation = impl, PowerEfficientEncoder = false };
        o.QualityLimitationDurations.Add("cpu", cpu); o.QualityLimitationDurations.Add("bandwidth", bw); o.QualityLimitationDurations.Add("none", 0);
        return new RtcStats { OutboundRtp = new RtcStats.Types.OutboundRtp { Rtc = new RtcStatsData { Id = "o1", Timestamp = (long)t }, Stream = new RtpStreamStats { Kind = "video", CodecId = "c1" }, Sent = new SentRtpStreamStats { BytesSent = bytes }, Outbound = o } };
    }
    static RtcStats Codec(string mime) => new() { Codec = new RtcStats.Types.Codec { Rtc = new RtcStatsData { Id = "c1" }, Codec_ = new CodecStats { MimeType = mime } } };

    [Fact]
    public void OdczytNadawania()
    {
        var s = StreamProbe.Read([Codec("video/H264"), Out(1000, 30, 500_000, 1080, 0.5, 0)], send: true);
        Assert.NotNull(s);
        Assert.Equal("h264", s!.Codec);
        Assert.Equal("OpenH264", s.Impl);
        Assert.Equal(1080, s.H);
        Assert.Equal(0.5, s.Cpu);
        Assert.Null(StreamProbe.Read([Out(1000, 30, 500_000, 1080, 0, 0)], send: false)); // odbiór szuka inbound-rtp
    }

    [Fact]
    public void ProcesorNieNadaza()
    {
        var p = new StreamProbe(true, null, 0);
        // 30 kl/s zamówione, wychodzi 12 kl/s, ograniczenie przez procesor cały czas
        for (var i = 0; i <= 5; i++) p.Add(StreamProbe.Read([Codec("video/H264"), Out(i * 2000, (ulong)(i * 24), (ulong)(i * 400_000), 1080, i * 2.0, 0)], true)!, i * 2000, null, out _);
        var tr = StreamProbe.Trouble(p.Window, 30, 1080);
        Assert.NotNull(tr);
        Assert.Equal("cpu", tr!.Reason);
        Assert.True(tr.Slow);
        Assert.Equal(12, tr.Fps);
    }

    [Fact]
    public void LaczeObnizaOstrosc()
    {
        var w = Enumerable.Range(0, 4).Select(_ => new StreamDelta(2, 29, 900, 540, 0, 2, 0, 0, 0, 0, 0, 0)).ToList();
        var tr = StreamProbe.Trouble(w, 30, 1080);
        Assert.NotNull(tr);
        Assert.Equal("bandwidth", tr!.Reason);
        Assert.False(tr.Slow);
        Assert.Equal(540, tr.H);
    }

    [Fact]
    public void NieruchomyEkranToNieProblem()
    {
        // mało klatek, bo nic się nie rusza — bez ograniczenia przez procesor ani łącze
        var w = Enumerable.Range(0, 4).Select(_ => new StreamDelta(2, 2, 50, 1080, 0, 0, 0, 0, 0, 0, 0, 0)).ToList();
        Assert.Null(StreamProbe.Trouble(w, 30, 1080));
        // za krótko, żeby oceniać
        Assert.Null(StreamProbe.Trouble([new StreamDelta(2, 5, 100, 1080, 2, 0, 0, 0, 0, 0, 0, 0)], 30, 1080));
    }

    [Fact]
    public void PodsumowaniePo30Sekundach()
    {
        var p = new StreamProbe(true, null, 0);
        Dictionary<string, object?>? rep = null;
        for (var i = 0; i <= 16 && rep is null; i++)
            p.Add(StreamProbe.Read([Codec("video/H264"), Out(i * 2000, (ulong)(i * 60), (ulong)(i * 1_000_000), 1080, 0, 0)], true)!, i * 2000, () => new() { ["app"] = "windows" }, out rep);
        Assert.NotNull(rep);
        Assert.Equal("send", rep!["role"]);
        Assert.Equal(30.0, rep["fps"]);
        Assert.Equal("h264", rep["codec"]);
        Assert.Equal("windows", rep["app"]);
        Assert.Equal(4000L, rep["kbps"]);
        Assert.Null(p.Flush(true, null)); // nic nowego od raportu
    }
}
