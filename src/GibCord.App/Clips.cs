using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using GibCord.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GibCord.App;

/// <summary>
/// 6.2 „Klipy z gry”: nagrywanie w tle (bufor), skrót Alt+F9 zapisuje ostatnie 15/30/60 s razem z dźwiękiem gry.
/// Obraz: Windows.Graphics.Capture (okno gry albo cały ekran) → MediaStreamSource z powierzchniami Direct3D (bez kopiowania do pamięci)
/// → MediaTranscoder (sprzętowe H.264 karty graficznej) → odcinki MP4 po kilka sekund na dysku. Przy zapisie bierzemy ostatnie odcinki
/// i składamy je (MediaComposition) w jeden plik w folderze Wideo\GibCord. Dźwięk: „dźwięk z komputera” bez samego GibCorda (rozmowy).
/// </summary>
public sealed class ClipRecorder : IDisposable
{
    public static string BufferDir => Path.Combine(App.DataDir, "klipy-bufor");
    public static string OutDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "GibCord");
    const double SegmentSeconds = 8;

    readonly string _label;
    readonly GraphicsCaptureItem _item;
    readonly int _fps;
    readonly uint _bitrate;
    readonly bool _audioOn;
    readonly (int W, int H) _max;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Channel<(Direct3D11CaptureFrame Frame, long Ticks)> _frames;
    readonly object _lock = new();
    readonly List<Segment> _done = [];
    readonly Queue<short> _pcm = new();
    IDirect3DDevice? _dev;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    Windows.Graphics.SizeInt32 _poolSize;
    ScreenAudio? _audio;
    Segment? _cur, _next; // _next: nowy odcinek czeka, aż koder będzie gotowy — dopiero wtedy przejmuje nagrywanie (bez dziury w obrazie)
    Timer? _rot;
    long _lastFrame;
    bool _disposed;
    int _saving;
    int _seq;

    public string Label => _label;
    public bool Running => _session is not null && !_disposed;
    public string? LastError { get; private set; }
    /// <summary>Ile sekund nagrania jest już w buforze.</summary>
    public double Buffered { get { lock (_lock) return _done.Sum(s => s.Duration.TotalSeconds) + (_cur is { Started: true } c ? (_clock.ElapsedTicks - c.StartTicks) / (double)Stopwatch.Frequency : 0); } }

    ClipRecorder(string label, GraphicsCaptureItem item, int fps, uint bitrate, bool audio, (int, int) max)
    {
        _label = label; _item = item; _fps = Math.Clamp(fps, 15, 60); _bitrate = bitrate; _audioOn = audio; _max = max;
        // najwyżej 2 klatki czekają na koder; starsze wylatują (i wracają do puli przechwytywania)
        _frames = System.Threading.Channels.Channel.CreateBounded<(Direct3D11CaptureFrame, long)>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true },
            dropped => { try { dropped.Item1.Dispose(); } catch { } });
    }

    /// <summary>Jakość z ustawień: "720p60", "1080p30", "1080p60".</summary>
    public static (int W, int H, int Fps, uint Bitrate) Quality(string q) => q switch
    {
        "1080p60" => (1920, 1080, 60, 12_000_000),
        "1080p30" => (1920, 1080, 30, 8_000_000),
        _ => (1280, 720, 60, 6_000_000),
    };

    public static ClipRecorder? Start(string label, GraphicsCaptureItem item, string quality, bool audio, GibCordClient? c)
    {
        var (w, h, fps, br) = Quality(quality);
        var r = new ClipRecorder(label, item, fps, br, audio, (w, h));
        if (r.Begin(c) is { } err) { App.Log("Klipy: " + err); r.Dispose(); return null; }
        return r;
    }

    string? Begin(GibCordClient? c)
    {
        try
        {
            Directory.CreateDirectory(BufferDir);
            foreach (var f in Directory.GetFiles(BufferDir, "*.mp4")) try { File.Delete(f); } catch { } // resztki z poprzedniego razu
            _dev = Direct3D.Create();
            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_dev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 4, _poolSize);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsCursorCaptureEnabled = false; } catch { }
            NoBorder(_session); // Windows 11: bez żółtej ramki wokół gry
            _item.Closed += (_, _) => Dispose();
            _session.StartCapture();
            if (_audioOn && c is not null)
            {
                _audio = new ScreenAudio(c, OnPcm);
                if (_audio.Start() is { } aerr) { App.Log("Klipy: " + aerr + " — bez dźwięku"); _audio = null; }
            }
            NewSegment();
            _rot = new Timer(_ => Tick(), null, 1000, 1000);
            App.Log($"Klipy: bufor włączony ({_label}, {_item.Size.Width}×{_item.Size.Height} → {_max.W}×{_max.H} {_fps} kl/s)");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>IGraphicsCaptureSession3.put_IsBorderRequired(false) — nie ma go w projekcji SDK 19041, więc wołamy przez tablicę metod.
    /// Windows 10 nie ma tego interfejsu (ramka zostaje), Windows 11 może odmówić — wtedy też tylko zapis w dzienniku.</summary>
    static unsafe void NoBorder(GraphicsCaptureSession s)
    {
        IntPtr unk = IntPtr.Zero, s3 = IntPtr.Zero;
        try
        {
            unk = WinRT.MarshalInspectable<GraphicsCaptureSession>.FromManaged(s);
            var iid = new Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be");
            if (Marshal.QueryInterface(unk, in iid, out s3) != 0 || s3 == IntPtr.Zero) { App.Log("Klipy: ramki nagrywania nie da się ukryć (Windows 10)"); return; }
            var vtbl = *(IntPtr**)s3;
            var put = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[7]; // IInspectable (6) → get_IsBorderRequired, put_IsBorderRequired
            var hr = put(s3, 0);
            if (hr != 0) App.Log($"Klipy: Windows nie pozwolił ukryć ramki (0x{hr:X8})");
        }
        catch (Exception ex) { App.Log("Klipy (ramka): " + ex.Message); }
        finally { if (s3 != IntPtr.Zero) Marshal.Release(s3); if (unk != IntPtr.Zero) Marshal.Release(unk); }
    }

    void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        var f = pool.TryGetNextFrame();
        if (f is null) return;
        var now = _clock.ElapsedTicks;
        // limit klatek: przy 60 Hz i 30 kl/s co druga klatka wylatuje od razu
        if (now - _lastFrame < Stopwatch.Frequency / (_fps + 5) || _disposed) { f.Dispose(); return; }
        if (f.ContentSize.Width != _poolSize.Width || f.ContentSize.Height != _poolSize.Height)
        {
            // okno gry zmieniło rozmiar: nowa pula i nowy odcinek (odcinek ma stały rozmiar obrazu)
            f.Dispose();
            _poolSize = f.ContentSize;
            if (_poolSize.Width < 16 || _poolSize.Height < 16) return;
            try { pool.Recreate(_dev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 4, _poolSize); } catch { }
            lock (_lock) { if (_cur is { } c) c.Ending = true; if (_next is { } n) n.Ending = true; _next = null; NewSegment(); }
            return;
        }
        _lastFrame = now;
        if (!_frames.Writer.TryWrite((f, now))) f.Dispose();
    }

    void OnPcm(short[] chunk)
    {
        lock (_pcm)
        {
            foreach (var s in chunk) _pcm.Enqueue(s);
            while (_pcm.Count > 48000 * 2) _pcm.Dequeue(); // najwyżej 1 s zaległości
        }
    }

    void Tick()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed || _cur is null) return;
                if (_next is null && _cur.Started && (_clock.ElapsedTicks - _cur.StartTicks) / (double)Stopwatch.Frequency >= SegmentSeconds) NewSegment();
                Cleanup();
            }
        }
        catch (Exception ex) { App.Log("Klipy (zegar): " + ex.Message); }
    }

    /// <summary>Zostawiamy tyle odcinków, żeby starczyło na najdłuższy klip (60 s) + zapas; starsze kasujemy (chyba że trwa zapis).</summary>
    void Cleanup()
    {
        if (_saving > 0) return;
        var need = 60 + SegmentSeconds * 2;
        double sum = 0;
        for (var i = _done.Count - 1; i >= 0; i--)
        {
            sum += _done[i].Duration.TotalSeconds;
            if (sum <= need) continue;
            var old = _done[i];
            _done.RemoveAt(i);
            try { File.Delete(old.Path); } catch { }
        }
    }

    /// <summary>Przygotowuje następny odcinek; poprzedni nagrywa dalej, aż ten nowy dostanie pierwsze żądanie próbki (Activate).</summary>
    void NewSegment()
    {
        if (_disposed || _next is not null) return;
        var seg = new Segment(this, Path.Combine(BufferDir, $"odcinek-{++_seq:D5}.mp4"), _poolSize);
        _next = seg;
        _cur ??= seg;
        _ = seg.Run();
    }

    /// <summary>Koder nowego odcinka ruszył: od tej chwili liczy się jego czas, a poprzedni odcinek się kończy.</summary>
    internal void Activate(Segment s)
    {
        lock (_lock)
        {
            if (s.Started) return;
            s.StartTicks = _clock.ElapsedTicks;
            if (_next == s) _next = null;
            if (_cur != s) { var old = _cur; _cur = s; if (old is not null) old.Ending = true; }
        }
    }

    internal void Finished(Segment s)
    {
        lock (_lock)
        {
            if (s.Ok && File.Exists(s.Path) && s.Duration.TotalSeconds > .5) { _done.Add(s); _done.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks)); }
            else try { File.Delete(s.Path); } catch { }
        }
    }

    /// <summary>Zapisuje ostatnie <paramref name="seconds"/> s do Wideo\GibCord. Zwraca ścieżkę albo null (LastError mówi, czemu).</summary>
    public async Task<string?> Save(int seconds, string? outPath = null)
    {
        Segment? cur;
        Interlocked.Increment(ref _saving);
        try
        {
            lock (_lock)
            {
                cur = _cur;
                if (cur is not null) { cur.Ending = true; NewSegment(); } // nagranie do teraz; następny odcinek ruszy sam
            }
            if (cur is not null) await Task.WhenAny(cur.Done, Task.Delay(8000));
            List<Segment> segs;
            lock (_lock) segs = _done.Where(s => File.Exists(s.Path)).ToList();
            if (segs.Count == 0) { LastError = "W buforze nie ma jeszcze nagrania — poczekaj kilka sekund."; return null; }
            outPath ??= NewClipPath(_label);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            // składanie odcinków gubi trochę czasu na łączeniach — mierzymy wynik i w razie potrzeby składamy jeszcze raz z zapasem
            double extra = 0, got = 0, total = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var want = seconds + extra;
                var clips = new List<MediaClip>();
                total = 0;
                for (var i = segs.Count - 1; i >= 0 && total < want; i--)
                {
                    var mc = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(segs[i].Path));
                    clips.Insert(0, mc);
                    total += mc.OriginalDuration.TotalSeconds;
                }
                var comp = new MediaComposition();
                foreach (var mc in clips) comp.Clips.Add(mc);
                if (total > want && comp.Clips.Count > 0)
                {
                    var cut = TimeSpan.FromSeconds(total - want);
                    if (cut < comp.Clips[0].OriginalDuration - TimeSpan.FromMilliseconds(200)) comp.Clips[0].TrimTimeFromStart = cut;
                }
                await File.WriteAllBytesAsync(outPath, []);
                var of = await StorageFile.GetFileFromPathAsync(outPath);
                var vp = comp.Clips[^1].GetVideoEncodingProperties();
                var prof = Profile(vp.Width > 0 ? (int)vp.Width : _max.W, vp.Height > 0 ? (int)vp.Height : _max.H, _fps, _bitrate, _audioOn);
                var r = await comp.RenderToFileAsync(of, MediaTrimmingPreference.Precise, prof);
                if (r != TranscodeFailureReason.None) { LastError = "Nie udało się złożyć klipu (" + r + ")."; try { File.Delete(outPath); } catch { } return null; }
                try { got = (await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(outPath))).OriginalDuration.TotalSeconds; } catch { break; }
                var shortBy = Math.Min(seconds, total) - got;
                if (shortBy < 0.4 || total < want) break; // dość dokładnie albo w buforze nie ma więcej nagrania
                extra += shortBy + 0.1;
            }
            App.Log($"Klipy: zapisano {got:0.#} s z {seconds} s ({new FileInfo(outPath).Length / 1024} KB)");
            return outPath;
        }
        catch (Exception ex) { LastError = "Nie udało się zapisać klipu: " + ex.Message; App.Log("Klipy (zapis): " + ex); return null; }
        finally { Interlocked.Decrement(ref _saving); }
    }

    public static string NewClipPath(string label)
    {
        var safe = string.Concat(label.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        if (safe.Length > 40) safe = safe[..40];
        if (safe.Length == 0) safe = "Klip";
        return Path.Combine(OutDir, $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
    }

    internal static MediaEncodingProfile Profile(int w, int h, int fps, uint bitrate, bool audio)
    {
        var p = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        p.Video.Width = (uint)w; p.Video.Height = (uint)h;
        p.Video.FrameRate.Numerator = (uint)fps; p.Video.FrameRate.Denominator = 1;
        p.Video.PixelAspectRatio.Numerator = 1; p.Video.PixelAspectRatio.Denominator = 1;
        p.Video.Bitrate = bitrate;
        if (audio) p.Audio = AudioEncodingProperties.CreateAac(48000, 2, 160_000);
        else p.Audio = null;
        return p;
    }

    /// <summary>Rozmiar wyjściowy: mieści się w jakości (np. 1280×720), zachowuje proporcje, parzyste wymiary.</summary>
    static (int W, int H) Fit(int w, int h, (int W, int H) max)
    {
        var (mw, mh) = h > w ? (max.H, max.W) : (max.W, max.H);
        var k = Math.Min(1.0, Math.Min(mw / (double)w, mh / (double)h));
        return (Math.Max(2, (int)(w * k) & ~1), Math.Max(2, (int)(h * k) & ~1));
    }

    /// <summary>Jeden odcinek nagrania: MediaStreamSource → MediaTranscoder → plik MP4.</summary>
    internal sealed class Segment(ClipRecorder r, string path, Windows.Graphics.SizeInt32 size)
    {
        public string Path { get; } = path;
        /// <summary>Chwila, od której liczą się znaczniki czasu (pierwsze żądanie próbki), -1 = jeszcze nie ruszył.</summary>
        public long StartTicks { get; set; } = -1;
        public bool Started => StartTicks >= 0;
        public volatile bool Ending;
        public bool Ok;
        public TimeSpan Duration;
        readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Done => _done.Task;
        TimeSpan _lastVideo = TimeSpan.MinValue, _audioPos;
        readonly List<Direct3D11CaptureFrame> _held = [];

        TimeSpan Now => TimeSpan.FromSeconds((r._clock.ElapsedTicks - StartTicks) / (double)Stopwatch.Frequency);

        public async Task Run()
        {
            IRandomAccessStream? stream = null;
            try
            {
                var vp = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)size.Width, (uint)size.Height);
                var vd = new VideoStreamDescriptor(vp);
                var mss = r._audioOn
                    ? new MediaStreamSource(vd, new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(48000, 2, 16)))
                    : new MediaStreamSource(vd);
                mss.BufferTime = TimeSpan.Zero;
                mss.Starting += (_, a) => a.Request.SetActualStartPosition(TimeSpan.Zero);
                mss.SampleRequested += OnSample;
                File.Create(Path).Dispose();
                stream = await FileRandomAccessStream.OpenAsync(Path, FileAccessMode.ReadWrite);
                var (ow, oh) = Fit(size.Width, size.Height, r._max);
                var tr = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
                var prep = await tr.PrepareMediaStreamSourceTranscodeAsync(mss, stream, Profile(ow, oh, r._fps, r._bitrate, r._audioOn));
                if (!prep.CanTranscode) { r.LastError = "Koder obrazu niedostępny (" + prep.FailureReason + ")."; App.Log("Klipy: " + r.LastError); return; }
                await prep.TranscodeAsync();
                Ok = true;
            }
            catch (Exception ex) { App.Log("Klipy (odcinek): " + ex.Message); r.LastError = ex.Message; }
            finally
            {
                lock (_held) { foreach (var f in _held) try { f.Dispose(); } catch { } _held.Clear(); }
                try { stream?.Dispose(); } catch { }
                Duration = _lastVideo > TimeSpan.Zero ? _lastVideo : TimeSpan.Zero;
                r.Finished(this);
                _done.TrySetResult();
            }
        }

        async void OnSample(MediaStreamSource s, MediaStreamSourceSampleRequestedEventArgs a)
        {
            var req = a.Request;
            var def = req.GetDeferral();
            try
            {
                if (!Started) r.Activate(this);
                if (req.StreamDescriptor is VideoStreamDescriptor) req.Sample = await NextVideo();
                else req.Sample = await NextAudio();
            }
            catch (Exception ex) { App.Log("Klipy (próbka): " + ex.Message); req.Sample = null; }
            finally { def.Complete(); }
        }

        async Task<MediaStreamSample?> NextVideo()
        {
            while (!Ending && !r._disposed)
            {
                using var cts = new CancellationTokenSource(250);
                try { if (!await r._frames.Reader.WaitToReadAsync(cts.Token)) return null; }
                catch (OperationCanceledException) { continue; }
                if (!r._frames.Reader.TryRead(out var it)) continue;
                var ts = TimeSpan.FromSeconds((it.Ticks - StartTicks) / (double)Stopwatch.Frequency);
                if (ts < TimeSpan.Zero || ts <= _lastVideo) { it.Frame.Dispose(); continue; }
                var sample = MediaStreamSample.CreateFromDirect3D11Surface(it.Frame.Surface, ts);
                var fr = it.Frame;
                lock (_held)
                {
                    _held.Add(fr);
                    // koder oddaje klatki po przetworzeniu (Processed); gdyby nie oddał — najstarsze wracają do puli same
                    while (_held.Count > 3) { try { _held[0].Dispose(); } catch { } _held.RemoveAt(0); }
                }
                sample.Processed += (_, _) => { lock (_held) { if (_held.Remove(fr)) try { fr.Dispose(); } catch { } } };
                _lastVideo = ts;
                return sample;
            }
            return null; // koniec odcinka
        }

        async Task<MediaStreamSample?> NextAudio()
        {
            const int Ms = 20, Frames = 48000 * Ms / 1000;
            var step = TimeSpan.FromMilliseconds(Ms);
            // dźwięk idzie równo z zegarem: czekamy, aż minie te 20 ms, i bierzemy to, co przyszło (brak = cisza)
            while (!Ending && !r._disposed)
            {
                var wait = _audioPos + step - Now;
                if (wait > TimeSpan.Zero) { await Task.Delay(wait < TimeSpan.FromMilliseconds(30) ? wait : TimeSpan.FromMilliseconds(30)); continue; }
                var buf = new short[Frames * 2];
                lock (r._pcm)
                {
                    var n = Math.Min(buf.Length, r._pcm.Count);
                    for (var i = 0; i < n; i++) buf[i] = r._pcm.Dequeue();
                }
                var bytes = new byte[buf.Length * 2];
                System.Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
                var sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), _audioPos);
                sample.Duration = step;
                _audioPos += step;
                return sample;
            }
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _rot?.Dispose(); } catch { }
        lock (_lock) { if (_cur is { } c) c.Ending = true; if (_next is { } n) n.Ending = true; }
        try { _audio?.Stop(); } catch { }
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        while (_frames.Reader.TryRead(out var f)) try { f.Frame.Dispose(); } catch { }
        _session = null; _pool = null;
        App.Log("Klipy: bufor wyłączony");
        // pliki bufora kasujemy po chwili (koder mógł jeszcze kończyć odcinek)
        _ = Task.Delay(3000).ContinueWith(_ => { lock (_lock) { foreach (var s in _done) try { File.Delete(s.Path); } catch { } _done.Clear(); } });
    }
}
