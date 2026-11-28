using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace GibCord.App.Ui;

/// <summary>Filmy i dźwięki w czacie: odtwarzacz w oknie (bez przeglądarki) i wiadomości głosowe.</summary>
static class MediaViews
{
    /// <summary>Jeden odtwarzacz dźwięku naraz (nowy zatrzymuje poprzedni — jak na stronie).</summary>
    static MediaPlayer? _current;

    public static bool IsVoiceMessage(Attachment a) => a.Mime.StartsWith("audio/") && a.Name.StartsWith("wiadomosc-glosowa");

    public static FrameworkElement Video(Uri url, Attachment a, Uri? poster = null, Uri? original = null)
    {
        double w = a.Width ?? 480, h = a.Height ?? 270;
        var k = Math.Min(1, Math.Min(440 / w, 320 / h));
        var player = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true, AutoPlay = false, Width = Math.Max(200, w * k), Height = Math.Max(120, h * k),
            Source = MediaSource.CreateFromUri(url), Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
        player.TransportControls.IsCompact = true;
        player.Unloaded += (_, _) => { try { player.MediaPlayer?.Pause(); } catch { } }; // przewinięcie / inny kanał — film nie gra dalej w tle
        player.TransportControls.IsZoomButtonVisible = false;
        if (poster is not null) try { player.PosterSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(poster); } catch { } // 6.6: klatka podglądu z serwera
        var box = new Grid();
        box.Children.Add(player);
        if (a.Processing) box.Children.Add(new Border { Background = U.B("#b3000000"), CornerRadius = new CornerRadius(8), Padding = U.Th(9, 4), Margin = U.Th(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Child = U.T("Przygotowuję film do odtwarzania…", 12, U.Semi, U.B("#ffffff")) });
        var frame = new Border { CornerRadius = new CornerRadius(10), Child = box, HorizontalAlignment = HorizontalAlignment.Left, Margin = U.Th(0, 3, 0, 2), Background = U.B("#cc08040f") };
        if (original is null || a.Original is null) return frame;
        var link = new HyperlinkButton { Content = U.T($"Pobierz oryginał ({a.Original.Name}, {Format.Size(a.Original.Size ?? 0)})", 12, U.Semi, Theme.Mu), Padding = U.Th(0), NavigateUri = original };
        return U.V(2, frame, link);
    }

    /// <summary>Pasek dźwięku: ▶ / ❚❚, postęp (klik przewija), czas. Dla wiadomości głosowej bez nazwy pliku.</summary>
    public static FrameworkElement Audio(Uri url, Attachment a)
    {
        var voice = IsVoiceMessage(a);
        MediaPlayer? mp = null;
        long playAt = 0;
        var play = new Button { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Padding = U.Th(0), Background = Theme.Accent, BorderThickness = U.Th(0), Content = U.I("", 14, Theme.OnAccent) };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 6, Width = voice ? 180 : 220, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Accent, Background = U.W(.12) };
        var time = U.T(voice ? "🎤" : "", 12, U.Semi, Theme.Mu);
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        void Tick()
        {
            if (mp is null) return;
            var s = mp.PlaybackSession;
            var dur = s.NaturalDuration;
            bar.Value = dur.TotalMilliseconds > 0 ? s.Position.TotalMilliseconds / dur.TotalMilliseconds : 0;
            time.Text = dur.TotalSeconds > 0 ? $"{Fmt(s.Position)} / {Fmt(dur)}" : Fmt(s.Position);
            var playing = s.PlaybackState == MediaPlaybackState.Playing;
            ((FontIcon)play.Content).Glyph = playing ? "" : "";
            if (!playing && s.PlaybackState != MediaPlaybackState.Opening && s.PlaybackState != MediaPlaybackState.Buffering && Environment.TickCount64 - playAt > 1500) timer.Stop();
        }
        timer.Tick += (_, _) => Tick();
        play.Click += (_, _) =>
        {
            if (mp is null)
            {
                mp = new MediaPlayer { Source = MediaSource.CreateFromUri(url), AutoPlay = false };
                mp.MediaEnded += (_, _) => mp.PlaybackSession.Position = TimeSpan.Zero;
                mp.MediaFailed += (_, e) => App.Log("Odtwarzanie: " + e.ErrorMessage);
            }
            if (mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) mp.Pause();
            else
            {
                if (_current is not null && _current != mp) _current.Pause();
                _current = mp;
                playAt = Environment.TickCount64;
                mp.Play();
            }
            timer.Start();
            Tick();
        };
        bar.Tapped += (_, e) =>
        {
            if (mp is null) return;
            var dur = mp.PlaybackSession.NaturalDuration;
            if (dur.TotalMilliseconds <= 0) return;
            mp.PlaybackSession.Position = TimeSpan.FromMilliseconds(dur.TotalMilliseconds * Math.Clamp(e.GetPosition(bar).X / bar.ActualWidth, 0, 1));
            Tick();
        };
        var row = U.H(10, play, bar, time);
        var content = voice ? (UIElement)row : U.V(6, U.T(a.Name, 13, U.Semi, RichText.LinkBrush).With(t => t.MaxWidth = 300), row);
        return U.Box(content, Theme.Card, 22, U.Th(8, 8, 14, 8), Theme.Line, 1).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = U.Th(0, 3, 0, 2); });
    }
}

/// <summary>Nagrywanie wiadomości głosowej (mikrofon Windows → M4A/AAC w pamięci). Strona i telefon odtwarzają to bez problemu.</summary>
sealed class VoiceRecorder : IDisposable
{
    MediaCapture? _mc;
    InMemoryRandomAccessStream? _stream;
    long _started;

    public bool Active => _mc is not null;
    public TimeSpan Elapsed => Active ? TimeSpan.FromMilliseconds(Environment.TickCount64 - _started) : TimeSpan.Zero;

    public async Task<string?> Start()
    {
        if (Active) return null;
        try
        {
            _mc = new MediaCapture();
            await _mc.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio, MediaCategory = MediaCategory.Speech });
            _stream = new InMemoryRandomAccessStream();
            var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
            await _mc.StartRecordToStreamAsync(profile, _stream);
            _started = Environment.TickCount64;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            Dispose();
            return "Windows nie pozwala aplikacji na mikrofon. Włącz: Ustawienia → Prywatność → Mikrofon → „Zezwalaj aplikacjom klasycznym”.";
        }
        catch (Exception ex) { Dispose(); return "Nie udało się włączyć mikrofonu: " + ex.Message; }
    }

    /// <summary>Kończy nagranie. Zwraca bajty M4A i długość (albo null przy anulowaniu / błędzie).</summary>
    public async Task<(byte[] Data, int Seconds)?> Stop(bool keep)
    {
        if (_mc is null || _stream is null) return null;
        var secs = (int)Math.Round(Elapsed.TotalSeconds);
        try
        {
            await _mc.StopRecordAsync();
            if (!keep) return null;
            var size = (uint)_stream.Size;
            var buf = new byte[size];
            using var input = _stream.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            await reader.LoadAsync(size);
            reader.ReadBytes(buf);
            return (buf, Math.Max(1, secs));
        }
        catch (Exception ex) { App.Log("Nagrywanie: " + ex.Message); return null; }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        try { _mc?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _mc = null;
        _stream = null;
    }
}
