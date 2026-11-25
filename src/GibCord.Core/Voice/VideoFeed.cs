using LiveKit.Proto;
using VideoFrame = LiveKit.Rtc.VideoFrame;

namespace GibCord.Core.Voice;

/// <summary>
/// Obraz od jednej osoby (kamera albo udostępniony ekran) — ostatnia klatka do narysowania.
/// <para>
/// 6.4.2 (transmisje „tną”): klatka przychodzi w postaci z dekodera (I420, 1,5 bajta na piksel) i NIE jest kopiowana —
/// trzymamy tylko odnośnik do bufora, który biblioteka i tak tworzy dla każdej klatki. Do BGRA przeliczamy dopiero przy
/// rysowaniu, tylko klatki, które okno faktycznie pokazuje (15–30 na sekundę), i od razu w rozmiarze kafelka.
/// Wcześniej: każda klatka (do 60 na sekundę) przeliczana do BGRA (8 MB przy 1080p, 15 MB przy 1440p), kopiowana jeszcze
/// raz tutaj i trzeci raz do bitmapy — setki MB na sekundę śmieci w pamięci, przez które przystawał też dźwięk rozmowy.
/// </para>
/// </summary>
public sealed class VideoFeed
{
    readonly object _lock = new(), _conv = new();
    VideoFrame? _frame;
    byte[] _bgra = [];
    long _seq;

    public long UserId { get; init; }
    public bool Screen { get; init; }
    public string TrackSid { get; init; } = "";
    /// <summary>Ścieżka z biblioteki (statystyki dla czujnika cięcia).</summary>
    internal LiveKit.Rtc.Track? Source { get; init; }
    public int Width { get { lock (_lock) return _frame?.Width ?? 0; } }
    public int Height { get { lock (_lock) return _frame?.Height ?? 0; } }
    public long Sequence => Interlocked.Read(ref _seq);

    internal void Put(VideoFrame f)
    {
        lock (_lock) _frame = f;
        Interlocked.Increment(ref _seq);
    }

    /// <summary>
    /// Najnowsza klatka jako BGRA, jeśli jest nowsza niż <paramref name="seen"/>. <paramref name="maxW"/>×<paramref name="maxH"/>
    /// (w pikselach ekranu, 0 = bez ograniczenia): klatka jest zmniejszana całkowitą krotnością tak, żeby nadal była
    /// co najmniej tej wielkości — resztę skaluje karta graficzna, więc tekst zostaje ostry.
    /// </summary>
    public bool TryCopy(ref long seen, Action<ReadOnlySpan<byte>, int, int> copy, int maxW = 0, int maxH = 0)
    {
        var s = Sequence;
        if (s == seen || s == 0) return false;
        // odnośnik bierzemy pod blokadą, liczymy już bez niej — wątek biblioteki (ten sam, który niesie dźwięk) nie czeka na rysowanie
        VideoFrame? f;
        lock (_lock) f = _frame;
        if (f is null || f.Width <= 0 || f.Height <= 0) return false;
        lock (_conv)
        {
            var (w, h) = ToBgra(f, Step(f.Width, f.Height, maxW, maxH), ref _bgra);
            if (w > 0) copy(_bgra.AsSpan(0, w * h * 4), w, h);
        }
        seen = s;
        return true;
    }

    /// <summary>Co który piksel brać: największa krotność, przy której obraz nadal wypełnia miejsce na ekranie.</summary>
    internal static int Step(int w, int h, int maxW, int maxH)
    {
        if (maxW <= 0 || maxH <= 0) return 1;
        var k = Math.Min(w / maxW, h / maxH);
        return Math.Clamp(k, 1, 8);
    }

    /// <summary>Klatka → BGRA (co <paramref name="k"/>-ty piksel, przy k ≥ 2 średnia z 2×2). Zwraca wymiary wyniku.</summary>
    internal static (int W, int H) ToBgra(VideoFrame f, int k, ref byte[] dst)
    {
        var type = f.Type;
        if (type is not (VideoBufferType.I420 or VideoBufferType.Nv12 or VideoBufferType.Bgra))
        {
            // inny format z dekodera (np. 10-bitowy) — przeliczenie w bibliotece, jak dawniej
            try { f = f.Convert(VideoBufferType.Bgra); type = VideoBufferType.Bgra; }
            catch { return (0, 0); }
        }
        var w = f.Width / k & ~1; var h = f.Height / k & ~1;
        if (w <= 0 || h <= 0) return (0, 0);
        var need = w * h * 4;
        if (dst.Length < need) dst = new byte[need];
        var src = f.DataBytes;
        switch (type)
        {
            case VideoBufferType.I420: Yuv(src, f.Width, f.Height, nv12: false, k, dst, w, h); break;
            case VideoBufferType.Nv12: Yuv(src, f.Width, f.Height, nv12: true, k, dst, w, h); break;
            default: Bgra(src, f.Width, f.Height, k, dst, w, h); break;
        }
        return (w, h);
    }

    /// <summary>I420 / NV12 (BT.601, zakres ograniczony — tak jak liczyła biblioteka) → BGRA. Wiersze równolegle.</summary>
    static unsafe void Yuv(byte[] src, int sw, int sh, bool nv12, int k, byte[] dst, int dw, int dh)
    {
        int cw = (sw + 1) / 2, ch = (sh + 1) / 2;
        var ySize = sw * sh;
        if (src.Length < ySize + cw * ch * 2) return;
        fixed (byte* ps = src, pd = dst)
        {
            var s0 = (nint)ps; var d0 = (nint)pd;
            void Row(int y)
            {
                var sp = (byte*)s0;
                var yy = Math.Min(sh - 1, y * k);
                var y0 = sp + (long)yy * sw;
                var y1 = k >= 2 ? sp + (long)Math.Min(sh - 1, yy + 1) * sw : y0;
                var crow = yy / 2;
                var u = nv12 ? sp + ySize + (long)crow * cw * 2 : sp + ySize + (long)crow * cw;
                var v = nv12 ? u + 1 : sp + ySize + (long)cw * ch + (long)crow * cw;
                var cstep = nv12 ? 2 : 1;
                var o = (byte*)d0 + (long)y * dw * 4;
                for (var x = 0; x < dw; x++)
                {
                    var xx = Math.Min(sw - 1, x * k);
                    int Y = k >= 2 ? (y0[xx] + y0[Math.Min(sw - 1, xx + 1)] + y1[xx] + y1[Math.Min(sw - 1, xx + 1)] + 2) >> 2 : y0[xx];
                    var ci = (xx / 2) * cstep;
                    int c = (Y - 16) * 298, d = u[ci] - 128, e = v[ci] - 128;
                    o[0] = Clamp((c + 516 * d + 128) >> 8);
                    o[1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                    o[2] = Clamp((c + 409 * e + 128) >> 8);
                    o[3] = 255;
                    o += 4;
                }
            }
            if ((long)dw * dh < 200_000) for (var y = 0; y < dh; y++) Row(y);
            else Parallel.For(0, dh, Row);
        }
    }

    static unsafe void Bgra(byte[] src, int sw, int sh, int k, byte[] dst, int dw, int dh)
    {
        if (src.Length < sw * sh * 4) return;
        if (k == 1 && dw == sw && dh <= sh) { Buffer.BlockCopy(src, 0, dst, 0, dw * dh * 4); return; }
        fixed (byte* ps = src, pd = dst)
        {
            for (var y = 0; y < dh; y++)
            {
                var r = ps + (long)Math.Min(sh - 1, y * k) * sw * 4;
                var o = (uint*)(pd + (long)y * dw * 4);
                for (var x = 0; x < dw; x++) o[x] = ((uint*)r)[Math.Min(sw - 1, x * k)];
            }
        }
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
