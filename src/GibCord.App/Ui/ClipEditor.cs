using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Storage;

namespace GibCord.App.Ui;

/// <summary>
/// 6.2: okienko klipu — podgląd, przycięcie (początek/koniec), wysłanie na kanał albo do rozmowy, pokazanie w folderze.
/// Przycięty klip zapisuje się obok oryginału („… (przycięty).mp4”), oryginał zostaje.
/// </summary>
static class ClipEditor
{
    public static void Show(Shell sh, string path)
    {
        if (!File.Exists(path)) { sh.Info("Tego klipu już nie ma na dysku.", true); return; }
        var player = new MediaPlayerElement
        {
            Source = MediaSource.CreateFromUri(new Uri(path)), AreTransportControlsEnabled = true, AutoPlay = true,
            Width = 640, Height = 360, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
        var dur = 0.0;
        var start = new SliderField { Header = U.T("Początek", 12.5, U.Semi), StepFrequency = .1, Format = Time, Width = 300 };
        var end = new SliderField { Header = U.T("Koniec", 12.5, U.Semi), StepFrequency = .1, Format = Time, Width = 300 };
        var len = U.T("", 12.5, U.Semi, Theme.Mu);
        void Len() => len.Text = dur <= 0 ? "Wczytywanie…" : $"Długość: {Time(Math.Max(0, end.Value - start.Value))} z {Time(dur)}";
        Len();
        player.MediaPlayer.MediaOpened += (mp, _) => App.Window?.UiRun(() =>
        {
            dur = mp.PlaybackSession.NaturalDuration.TotalSeconds;
            start.Maximum = dur; end.Maximum = dur; end.Value = dur;
            Len();
        });
        start.ValueChanged += (_, e) => { if (end.Value < e.NewValue + .5) end.Value = Math.Min(dur, e.NewValue + .5); player.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue); Len(); };
        end.ValueChanged += (_, e) => { if (start.Value > e.NewValue - .5) start.Value = Math.Max(0, e.NewValue - .5); Len(); };

        bool Trimmed() => dur > 0 && (start.Value > .05 || end.Value < dur - .05);
        Button? send = null;
        send = U.Primary("Wyślij na kanał…", async () =>
        {
            var target = await WrappedView.PickChannel(sh, "Gdzie wysłać klip?");
            if (target is null) return;
            send!.IsEnabled = false;
            try
            {
                var file = Trimmed() ? await Trim(path, start.Value, end.Value) : path;
                if (file is null) { sh.Info("Nie udało się przyciąć klipu.", true); return; }
                sh.Info("Wysyłanie klipu…");
                await using var fs = File.OpenRead(file);
                var (att, err) = await sh.C.Upload(fs, Path.GetFileName(file), "video/mp4", fs.Length);
                if (att is null) { sh.Info(err ?? "Nie udało się wysłać klipu.", true); return; }
                await sh.C.Api.SendMessage(target.Value.Id, "", attachments: [att.Id]);
                player.MediaPlayer.Pause();
                sh.CloseOverlay();
                sh.Info($"Klip wysłany: {target.Value.Label}");
            }
            catch (Exception ex) { sh.Info(ex is ApiException ae ? ae.Message : "Nie udało się wysłać klipu: " + ex.Message, true); }
            finally { send!.IsEnabled = true; }
        });
        var save = U.Ghost("Zapisz przycięty", async () =>
        {
            if (!Trimmed()) { sh.Info("Najpierw przesuń początek albo koniec."); return; }
            var f = await Trim(path, start.Value, end.Value);
            sh.Info(f is null ? "Nie udało się przyciąć klipu." : "Zapisano: " + Path.GetFileName(f), f is null);
        });
        var folder = U.Ghost("Pokaż w folderze", () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } });
        var close = U.IconButton("", "Zamknij", () => { player.MediaPlayer.Pause(); sh.CloseOverlay(); }, 14, 30);
        var head = new Grid().Cols("*", "auto").Add(U.V(2, U.T("Klip z gry", 17, U.Bold), U.T(Path.GetFileName(path), 12, null, Theme.Mu2).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis)).At(0, 0), close.At(0, 1));
        var trims = U.H(16, start, end);
        var actions = U.H(10, send, save, folder);
        var body = U.V(12, head, new Border { CornerRadius = new CornerRadius(12), Child = player, Background = U.B("#000000") }, trims, len, actions);
        var card = U.Box(body, Theme.Surface, 18, U.Th(18), Theme.Line, 1).With(b => b.MaxWidth = 700);
        sh.ShowOverlay(card, pop: true);
    }

    static string Time(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss\.f");

    /// <summary>Przycięta kopia (sprzętowe kodowanie) obok oryginału.</summary>
    static async Task<string?> Trim(string path, double from, double to)
    {
        try
        {
            var comp = new MediaComposition();
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
            clip.TrimTimeFromStart = TimeSpan.FromSeconds(Math.Max(0, from));
            clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, clip.OriginalDuration.TotalSeconds - to));
            comp.Clips.Add(clip);
            var outPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (przycięty).mp4");
            for (var i = 2; File.Exists(outPath); i++) outPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + $" (przycięty {i}).mp4");
            await File.WriteAllBytesAsync(outPath, []);
            var of = await StorageFile.GetFileFromPathAsync(outPath);
            var (w, h, fps, br) = ClipRecorder.Quality(App.Prefs.ClipQuality);
            var vp = clip.GetVideoEncodingProperties();
            if (vp.Width > 0 && vp.Height > 0) { w = (int)vp.Width; h = (int)vp.Height; }
            var r = await comp.RenderToFileAsync(of, MediaTrimmingPreference.Precise, ClipRecorder.Profile(w, h, fps, br, true));
            if (r != Windows.Media.Transcoding.TranscodeFailureReason.None) { try { File.Delete(outPath); } catch { } return null; }
            return outPath;
        }
        catch (Exception ex) { App.Log("Klipy (przycinanie): " + ex.Message); return null; }
    }
}
