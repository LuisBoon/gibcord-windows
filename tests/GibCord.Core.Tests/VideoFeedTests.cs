using GibCord.Core.Voice;
using LiveKit.Proto;
using VideoFrame = LiveKit.Rtc.VideoFrame;

namespace GibCord.Core.Tests;

/// <summary>6.4.2: obraz z kamer i transmisji w postaci z dekodera (I420 / NV12), do BGRA dopiero przy rysowaniu.</summary>
public sealed class VideoFeedTests
{
    static VideoFrame I420(int w, int h, byte y, byte u, byte v, bool nv12 = false)
    {
        int cw = (w + 1) / 2, ch = (h + 1) / 2;
        var data = new byte[w * h + cw * ch * 2];
        Array.Fill(data, y, 0, w * h);
        if (nv12) for (var i = 0; i < cw * ch; i++) { data[w * h + 2 * i] = u; data[w * h + 2 * i + 1] = v; }
        else { Array.Fill(data, u, w * h, cw * ch); Array.Fill(data, v, w * h + cw * ch, cw * ch); }
        return new VideoFrame(w, h, nv12 ? VideoBufferType.Nv12 : VideoBufferType.I420, data, new Dictionary<string, object>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CzerwonyZYuvDajeCzerwonyBgra(bool nv12)
    {
        // BT.601, zakres ograniczony: Y=81 U=90 V=240 to czysta czerwień
        var buf = Array.Empty<byte>();
        var (w, h) = VideoFeed.ToBgra(I420(64, 36, 81, 90, 240, nv12), 1, ref buf);
        Assert.Equal((64, 36), (w, h));
        for (var i = 0; i < w * h; i += 97)
        {
            Assert.InRange(buf[i * 4 + 2], 250, 255); // R
            Assert.InRange(buf[i * 4 + 1], 0, 5);     // G
            Assert.InRange(buf[i * 4 + 0], 0, 5);     // B
            Assert.Equal(255, buf[i * 4 + 3]);
        }
    }

    [Fact]
    public void SzaryZostajeSzary()
    {
        var buf = Array.Empty<byte>();
        VideoFeed.ToBgra(I420(32, 32, 126, 128, 128), 1, ref buf);
        // Y=126 → (126-16)·298/256 ≈ 128
        Assert.InRange(buf[0], 126, 130); Assert.InRange(buf[1], 126, 130); Assert.InRange(buf[2], 126, 130);
    }

    [Theory]
    [InlineData(1920, 1080, 0, 0, 1)]       // bez ograniczenia — pełny obraz
    [InlineData(1920, 1080, 640, 360, 3)]   // mały kafelek — co trzeci piksel
    [InlineData(1920, 1080, 1000, 600, 1)]  // większy niż połowa — pełny (resztę zmniejsza karta graficzna)
    [InlineData(2560, 1440, 300, 170, 8)]   // maks. 8
    public void KrokPasujeDoKafelka(int w, int h, int mw, int mh, int k) => Assert.Equal(k, VideoFeed.Step(w, h, mw, mh));

    [Fact]
    public void PomniejszonyObrazMaWymiaryKafelka()
    {
        var buf = Array.Empty<byte>();
        var (w, h) = VideoFeed.ToBgra(I420(1920, 1080, 81, 90, 240), VideoFeed.Step(1920, 1080, 640, 360), ref buf);
        Assert.Equal((640, 360), (w, h));
        Assert.InRange(buf[(360 * 640 - 1) * 4 + 2], 250, 255); // ostatni piksel też policzony
    }

    [Fact]
    public void NieparzysteWymiaryNieWychodzaPozaBufor()
    {
        var buf = Array.Empty<byte>();
        var (w, h) = VideoFeed.ToBgra(I420(641, 361, 81, 90, 240), 1, ref buf);
        Assert.Equal((640, 360), (w, h));
        (w, h) = VideoFeed.ToBgra(I420(641, 361, 81, 90, 240), 2, ref buf);
        Assert.Equal((320, 180), (w, h));
    }

    [Fact]
    public void FeedPodajeTylkoNoweKlatki()
    {
        var feed = new VideoFeed { UserId = 1 };
        long seen = 0;
        Assert.False(feed.TryCopy(ref seen, (_, _, _) => { }));
        feed.Put(I420(64, 36, 81, 90, 240));
        var got = (0, 0);
        Assert.True(feed.TryCopy(ref seen, (px, w, h) => got = (w, h), 32, 18));
        Assert.Equal((32, 18), got);
        Assert.False(feed.TryCopy(ref seen, (_, _, _) => { }));
        Assert.Equal(64, feed.Width);
    }
}
