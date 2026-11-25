using System.Text.Json;

namespace GibCord.Core.Tests;

/// <summary>
/// 25.11.2026 na żywo z kopią serwera (tools/transmisja/strona-h264.mjs): strona nadaje ekran w H.264 w szyfrowanej rozmowie,
/// a GibCord 6 go ogląda — obraz dochodzi, odszyfrowany i zdekodowany. Bez zmiennych GC_TEST_URL / GC_TEST_SCREEN test nic nie robi.
/// </summary>
public sealed class ScreenInteropTests
{
    [Fact]
    public async Task EkranZeStronyWH264()
    {
        var dir = Environment.GetEnvironmentVariable("GC_TEST_SCREEN");
        var url = Environment.GetEnvironmentVariable("GC_TEST_URL");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(url)) return;
        static async Task<bool> Until(Func<bool> ok, int ms) { var end = Environment.TickCount64 + ms; while (Environment.TickCount64 < end) { if (ok()) return true; await Task.Delay(200); } return ok(); }
        Assert.True(await Until(() => File.Exists(Path.Combine(dir, "ready.json")), 120000), "brak ready.json");
        var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "ready.json"))).RootElement;
        long ch = ready.GetProperty("channel").GetInt64(), dm = ready.GetProperty("dm").GetInt64(), sender = ready.GetProperty("sender").GetInt64();
        var l = new object();
        await using var a = new GibCordClient(new Uri(url), new MemoryTokenStore(), null, x => { lock (l) x(); });
        Assert.NotNull((await a.Login(ready.GetProperty("user").GetString()!, ready.GetProperty("pass").GetString()!)).Token);
        await a.Start();
        Assert.True(await Until(() => { lock (l) return a.Store.Loaded && a.Status == GatewayStatus.Connected; }, 20000), "połączenie aplikacji");
        a.Voice.Options.Muted = true;
        // faza 1: kanał serwera (bez szyfrowania), faza 2: rozmowa prywatna (szyfrowana od końca do końca)
        foreach (var (phase, room) in new[] { (1, ch), (2, dm) })
        {
            if (phase == 2) Assert.True(await Until(() => File.Exists(Path.Combine(dir, "web-in-2.json")), 120000), "strona nie zaczęła fazy 2");
            long frames = 0; int w = 0, h = 0; string? err = null; var enc = false;
            try
            {
                await a.JoinVoice(room);
                if (!await Until(() => a.Voice.State == Voice.VoiceState.Connected && (phase == 1 || a.Voice.Encrypted), 30000)) throw new Exception("aplikacja nie weszła do rozmowy");
                enc = a.Voice.Encrypted;
                a.Voice.Watch(sender); // subskrypcja ruszy, gdy strona opublikuje ekran
                if (!await Until(() => a.Voice.Feeds.Any(x => x.Screen && x.UserId == sender), 90000)) throw new Exception("brak ścieżki ekranu ze strony");
                var seen = 0L; var t0 = Environment.TickCount64;
                while (Environment.TickCount64 - t0 < 40000 && frames < 40)
                {
                    var f = a.Voice.Feeds.FirstOrDefault(x => x.Screen && x.UserId == sender);
                    if (f is not null && f.TryCopy(ref seen, (px, fw, fh) => { w = fw; h = fh; }, 320, 180)) frames++;
                    await Task.Delay(50);
                }
            }
            catch (Exception ex) { err = ex.Message; }
            finally { File.WriteAllText(Path.Combine(dir, $"app-done-{phase}.json"), JsonSerializer.Serialize(new { frames, w, h, enc, error = err })); }
            Assert.Null(err);
            Assert.True(frames > 10, $"faza {phase}: GibCord 6 dostał {frames} klatek ze strony");
            await a.LeaveVoice();
            await Task.Delay(1500);
        }
    }
}
