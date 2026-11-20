using System.Runtime.InteropServices.WindowsRuntime;
using GibCord.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace GibCord.App;

/// <summary>
/// Kamera w rozmowie: MediaCapture (Windows) → klatki BGRA 640×360 → serwer głosu (ścieżka „camera”).
/// Kamera jest włączona tylko na czas rozmowy z włączonym przyciskiem — Windows pokazuje wtedy diodę i ikonkę.
/// </summary>
public sealed class Camera : IDisposable
{
    readonly GibCordClient _c;
    MediaCapture? _mc;
    MediaFrameReader? _reader;
    Action<byte[], int, int>? _push;
    long _last;
    int _busy;
    byte[] _buf = [];

    public bool Active => _reader is not null;
    public long StartedAt { get; private set; }
    public Camera(GibCordClient c) { _c = c; }

    public async Task<string?> Start()
    {
        if (Active) return null;
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var pick = groups.SelectMany(g => g.SourceInfos.Select(i => (g, i)))
                .FirstOrDefault(x => x.i.SourceKind == MediaFrameSourceKind.Color && x.i.MediaStreamType is MediaStreamType.VideoRecord or MediaStreamType.VideoPreview);
            if (pick.g is null) return "Nie znaleziono kamery.";
            _mc = new MediaCapture();
            await _mc.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = pick.g, SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video, MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
            var src = _mc.FrameSources[pick.i.Id];
            _push = await _c.Voice.StartCamera(640, 360);
            if (_push is null) { Stop(); return "Najpierw wejdź do rozmowy."; }
            _reader = await _mc.CreateFrameReaderAsync(src, MediaEncodingSubtypes.Bgra8);
            _reader.FrameArrived += OnFrame;
            var st = await _reader.StartAsync();
            if (st != MediaFrameReaderStartStatus.Success)
            {
                Stop();
                return st == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable ? "Kamerę zajmuje inny program — zamknij go i spróbuj jeszcze raz." : "Kamera nie chce się włączyć (" + st + ").";
            }
            StartedAt = Environment.TickCount64;
            _c.SetVideo(true);
            return null;
        }
        catch (UnauthorizedAccessException) { Stop(); return "Windows nie pozwala GibCordowi używać kamery — włącz to w Ustawieniach → Prywatność → Kamera."; }
        catch (Exception ex) { App.Log("Kamera: " + ex); Stop(); return "Nie udało się włączyć kamery: " + ex.Message; }
    }

    void OnFrame(MediaFrameReader r, MediaFrameArrivedEventArgs _)
    {
        var now = Environment.TickCount64;
        if (now - _last < 40 || Interlocked.Exchange(ref _busy, 1) == 1) return; // ~24 kl./s
        _last = now;
        try
        {
            using var f = r.TryAcquireLatestFrame();
            var sb = f?.VideoMediaFrame?.SoftwareBitmap;
            if (sb is null || _push is null) return;
            using var bgra = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? SoftwareBitmap.Copy(sb) : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var w = bgra.PixelWidth & ~1; var h = bgra.PixelHeight & ~1;
            var n = bgra.PixelWidth * bgra.PixelHeight * 4;
            if (_buf.Length != n) _buf = new byte[n];
            bgra.CopyToBuffer(_buf.AsBuffer());
            if (w == bgra.PixelWidth && h == bgra.PixelHeight) _push(_buf, w, h);
        }
        catch (Exception ex) { App.Log("Kamera (klatka): " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    bool _republishing;

    /// <summary>Po ponownym połączeniu głosu kamera publikuje się od nowa (inni zobaczą obraz bez ponownego klikania).</summary>
    public async Task Republish()
    {
        if (!Active || _republishing) return;
        _republishing = true;
        try
        {
            var push = await _c.Voice.StartCamera(640, 360);
            if (!Active) { _ = _c.Voice.StopCamera(); return; } // wyłączono kamerę w trakcie — nie zostawiamy opublikowanej ścieżki
            if (push is null) { Stop(); return; }
            _push = push;
            StartedAt = Environment.TickCount64;
        }
        catch (Exception ex) { App.Log("Kamera po ponownym połączeniu: " + ex.Message); Stop(); }
        finally { _republishing = false; }
    }

    public void Stop()
    {
        var was = _reader is not null;
        try { if (_reader is not null) { _reader.FrameArrived -= OnFrame; _ = _reader.StopAsync(); _reader.Dispose(); } } catch { }
        try { _mc?.Dispose(); } catch { }
        _reader = null; _mc = null; _push = null;
        if (!was) return;
        _ = _c.Voice.StopCamera();
        _c.SetVideo(false);
    }

    public void Dispose() => Stop();
}
