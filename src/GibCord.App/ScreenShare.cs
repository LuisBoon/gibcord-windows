using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using GibCord.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace GibCord.App;

/// <summary>Urządzenie Direct3D dla przechwytywania ekranu (Windows.Graphics.Capture wymaga IDirect3DDevice).</summary>
static class Direct3D
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out uint level, out IntPtr context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice Create()
    {
        const uint Hardware = 1, Warp = 5, BgraSupport = 0x20, SdkVersion = 7;
        var hr = D3D11CreateDevice(IntPtr.Zero, Hardware, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion, out var dev, out _, out var ctx);
        if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, Warp, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion, out dev, out _, out ctx);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(dev, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var insp));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(insp); }
                finally { Marshal.Release(insp); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(dev); if (ctx != IntPtr.Zero) Marshal.Release(ctx); }
    }
}

/// <summary>
/// Transmisja ekranu albo okna: systemowe okienko wyboru (Windows.Graphics.Capture), klatki BGRA → serwer głosu.
/// Bez wstrzykiwania się w gry; działa z każdym oknem, które Windows pozwala nagrać.
/// </summary>
public sealed class ScreenShare : IDisposable
{
    readonly GibCordClient _c;
    GraphicsCaptureItem? _item;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    IDirect3DDevice? _dev;
    Action<byte[], int, int>? _push;
    Windows.Graphics.SizeInt32 _size;
    long _last;
    int _frameMs = 33;
    int _busy;
    byte[] _buf = [];

    public bool Active => _session is not null;
    public string? Title => _item?.DisplayName;
    public event Action? Stopped;

    ScreenAudio? _audio;
    long _lastPreview;
    int _tw, _th;
    string _quality = "1080p";
    byte[] _scaled = [];

    public ScreenShare(GibCordClient c) { _c = c; }
    public bool AudioActive => _audio?.Active == true;
    public long StartedAt { get; private set; }

    /// <summary>Okienko wyboru ekranu/okna i start transmisji. Zwraca komunikat błędu albo null.</summary>
    public async Task<string?> Start(IntPtr hwnd, int fps = 30, string quality = "1080p", bool withAudio = false)
    {
        if (!GraphicsCaptureSession.IsSupported()) return "Ten Windows nie pozwala nagrywać ekranu (potrzebny Windows 10 1903 albo nowszy).";
        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var item = await picker.PickSingleItemAsync();
        if (item is null) return null; // anulowano
        Stop(silent: true);
        try
        {
            _item = item;
            _size = item.Size;
            var (w, h) = Fit(item.Size.Width, item.Size.Height, quality);
            _tw = w; _th = h; _quality = quality;
            _frameMs = 1000 / Math.Clamp(fps, 5, 60);
            _push = await _c.Voice.StartScreenShare(w, h, Math.Clamp(fps, 5, 60), withAudio);
            if (_push is null) { _item = null; return "Najpierw wejdź na kanał głosowy."; }
            if (withAudio && _c.Voice.ScreenAudio)
            {
                _audio = new ScreenAudio(_c);
                if (_audio.Start() is { } aerr) { _audio = null; App.Window?.CurrentShell?.Info(aerr + " Transmisja idzie bez dźwięku.", true); }
            }
            _c.SetStreaming(true);
            _dev = Direct3D.Create();
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_dev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(item);
            try { _session.IsCursorCaptureEnabled = true; } catch { }
            StartedAt = Environment.TickCount64;
            item.Closed += (_, _) => App.Window?.UiRun(() => { if (ReferenceEquals(_item, item)) Stop(); }); // tylko to okno, nie wcześniejsze
            _session.StartCapture();
            App.Log($"Transmisja: {item.DisplayName} {item.Size.Width}×{item.Size.Height}");
            return null;
        }
        catch (Exception ex)
        {
            App.Log("Transmisja: " + ex);
            Stop(silent: true);
            return "Nie udało się udostępnić ekranu: " + ex.Message;
        }
    }

    /// <summary>Rozmiar wysyłanego obrazu: mieści się w wybranej jakości (720p / 1080p / 1440p), z zachowaniem proporcji.</summary>
    static (int W, int H) Fit(int w, int h, string quality)
    {
        w = Math.Max(2, w); h = Math.Max(2, h);
        var (mw, mh) = quality switch { "720p" => (1280, 720), "1440p" => (2560, 1440), _ => (1920, 1080) };
        // pionowy ekran / okno: ograniczamy dłuższy bok tak samo
        if (h > w) (mw, mh) = (mh, mw);
        var k = Math.Min(1.0, Math.Min(mw / (double)w, mh / (double)h));
        return ((int)(w * k) & ~1, (int)(h * k) & ~1);
    }

    /// <summary>Zmniejszenie klatki BGRA (średnia z 4 sąsiednich pikseli — tekst zostaje czytelny). Wiersze liczone równolegle.</summary>
    static unsafe void Scale(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        var fx = (sw << 16) / dw;
        var fy = (sh << 16) / dh;
        fixed (byte* ps = src, pd = dst)
        {
            var s0 = (IntPtr)ps; var d0 = (IntPtr)pd;
            Parallel.For(0, dh, y =>
            {
                var sp = (byte*)s0; var dp = (byte*)d0;
                var sy = Math.Min(sh - 2, (y * fy) >> 16);
                var r0 = sp + (long)sy * sw * 4;
                var r1 = r0 + sw * 4;
                var o = dp + (long)y * dw * 4;
                for (var x = 0; x < dw; x++)
                {
                    var sx = Math.Min(sw - 2, (x * fx) >> 16) * 4;
                    o[0] = (byte)((r0[sx] + r0[sx + 4] + r1[sx] + r1[sx + 4] + 2) >> 2);
                    o[1] = (byte)((r0[sx + 1] + r0[sx + 5] + r1[sx + 1] + r1[sx + 5] + 2) >> 2);
                    o[2] = (byte)((r0[sx + 2] + r0[sx + 6] + r1[sx + 2] + r1[sx + 6] + 2) >> 2);
                    o[3] = 255;
                    o += 4;
                }
            });
        }
    }

    async void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null || _push is null) return;
        var now = Environment.TickCount64;
        if (now - _last < _frameMs || Interlocked.Exchange(ref _busy, 1) == 1) return;
        _last = now;
        try
        {
            if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
            {
                _size = frame.ContentSize; // okno zmieniło rozmiar
                pool.Recreate(_dev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                return;
            }
            using var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
            if (now - _lastPreview > 10_000) { _lastPreview = now; _ = SendPreview(SoftwareBitmap.Copy(sb)); }
            var w = sb.PixelWidth & ~1; var h = sb.PixelHeight & ~1;
            var full = sb.PixelWidth * sb.PixelHeight * 4;
            if (_buf.Length != full) _buf = new byte[full];
            sb.CopyToBuffer(_buf.AsBuffer());
            // większe niż wybrana jakość → zmniejszamy (okno mogło też zmienić rozmiar)
            var (tw, th) = Fit(sb.PixelWidth, sb.PixelHeight, _quality);
            if (tw < w || th < h)
            {
                if (_scaled.Length != tw * th * 4) _scaled = new byte[tw * th * 4];
                Scale(_buf, sb.PixelWidth, sb.PixelHeight, _scaled, tw, th);
                _push(_scaled, tw, th);
            }
            else if (w != sb.PixelWidth || h != sb.PixelHeight)
            {
                // nieparzyste wymiary: przycinamy do parzystych (wymóg kodera)
                var cropped = new byte[w * h * 4];
                for (var y = 0; y < h; y++) Buffer.BlockCopy(_buf, y * sb.PixelWidth * 4, cropped, y * w * 4, w * 4);
                _push(cropped, w, h);
            }
            else _push(_buf, w, h);
        }
        catch (Exception ex) { App.Log("Transmisja (klatka): " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Mały kadr (480 px, JPEG) dla podglądu u innych — co 10 s.</summary>
    async Task SendPreview(SoftwareBitmap sb)
    {
        try
        {
            using (sb)
            {
                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
                enc.SetSoftwareBitmap(SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore));
                var k = Math.Min(1.0, 480.0 / Math.Max(1, sb.PixelWidth));
                enc.BitmapTransform.ScaledWidth = (uint)Math.Max(2, sb.PixelWidth * k);
                enc.BitmapTransform.ScaledHeight = (uint)Math.Max(2, sb.PixelHeight * k);
                enc.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                await enc.FlushAsync();
                var bytes = new byte[ms.Size];
                ms.Seek(0);
                await ms.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, Windows.Storage.Streams.InputStreamOptions.None);
                if (bytes.Length > 100) await _c.Api.PostImage("voice/preview", new MemoryStream(bytes), "image/jpeg");
            }
        }
        catch (Exception ex) { App.Log("Podgląd transmisji: " + ex.Message); }
    }

    public void Stop(bool silent = false)
    {
        var was = _session is not null || _push is not null;
        try { _session?.Dispose(); } catch { }
        try { if (_pool is not null) { _pool.FrameArrived -= OnFrame; _pool.Dispose(); } } catch { }
        _session = null; _pool = null; _push = null; _item = null;
        try { _audio?.Stop(); } catch { }
        _audio = null;
        try { (_dev as IDisposable)?.Dispose(); } catch { }
        _dev = null;
        if (!was) return;
        _ = _c.Voice.StopScreenShare();
        _c.SetStreaming(false);
        if (!silent) Stopped?.Invoke();
    }

    public void Dispose() => Stop(silent: true);
}
