using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GibCord.Core.Voice;

namespace GibCord.Core.Tests;

/// <summary>Test głosu na żywo — tylko gdy działa serwer LiveKit (GC_LK_URL, np. ws://127.0.0.1:7880, klucz GC_LK_KEY/GC_LK_SECRET).
/// W CI: livekit-server --dev na Windowsie (devkey/secret).</summary>
public sealed class VoiceFactAttribute : FactAttribute
{
    public VoiceFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GC_LK_URL")))
            Skip = "Brak serwera LiveKit (GC_LK_URL)";
    }
}

public sealed class VoiceTests
{
    static readonly string Url = Environment.GetEnvironmentVariable("GC_LK_URL") ?? "";
    static readonly string Key = Environment.GetEnvironmentVariable("GC_LK_KEY") ?? "devkey";
    static readonly string Secret = Environment.GetEnvironmentVariable("GC_LK_SECRET") ?? "secret";

    /// <summary>Token LiveKit (JWT HS256) — tak samo jak robi to serwer GibCorda.</summary>
    static string Token(string identity, string room, bool canSpeak = true)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var sources = canSpeak ? new[] { "microphone", "camera", "screen_share", "screen_share_audio" } : new[] { "camera", "screen_share", "screen_share_audio" };
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = Key, ["sub"] = identity, ["name"] = "Test " + identity, ["nbf"] = now - 10, ["exp"] = now + 600,
            ["video"] = new Dictionary<string, object> { ["room"] = room, ["roomJoin"] = true, ["canPublish"] = true, ["canSubscribe"] = true, ["canPublishData"] = true, ["canPublishSources"] = sources },
        }));
        var sig = B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(head + "." + body)));
        return head + "." + body + "." + sig;
    }

    // pokój „vt-…”, nie „gc-…” — kopia serwera GibCorda wyrzuca z pokoi gc-<id> osoby, których nie ma w rozmowie
    static VoiceMedia Media(long cid, string id, bool canSpeak = true) => new(cid, Url, Token(id, $"vt-{cid}", canSpeak), $"vt-{cid}", id);

    static async Task<bool> Until(Func<bool> ok, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (ok()) return true; await Task.Delay(50); }
        return ok();
    }

    /// <summary>A mówi (sinus 440 Hz w czasie rzeczywistym), B ma to usłyszeć: dźwięk w głośnikach + zielona obwódka.</summary>
    [VoiceFact]
    public async Task DwieOsobySlyszaSiePrzezSerwer()
    {
        var cid = Random.Shared.NextInt64(100000, 999999);
        await using var a = new VoiceEngine();
        await using var b = new VoiceEngine();
        a.Options.NoiseSuppression = false; a.Options.AutoGain = false; a.Options.EchoCancellation = false; a.Options.AutoSens = false; a.Options.Sens = -60;
        await a.ConnectAsync(Media(cid, "1"), 64, false, false);
        await b.ConnectAsync(Media(cid, "2"), 64, false, false);
        Assert.Equal(VoiceState.Connected, a.State);
        Assert.Equal(VoiceState.Connected, b.State);

        using var cts = new CancellationTokenSource();
        double heardPeak = 0;
        var speak = Task.Run(async () =>
        {
            var buf = new float[480];
            long n = 0;
            var t0 = Environment.TickCount64;
            long frames = 0;
            while (!cts.IsCancellationRequested)
            {
                for (var i = 0; i < buf.Length; i++, n++) buf[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * n / 48000f);
                a.PushMic(buf);
                frames++;
                var due = t0 + frames * 10 - Environment.TickCount64;
                if (due > 0) await Task.Delay((int)due);
            }
        });
        var listen = Task.Run(async () =>
        {
            var outBuf = new float[480];
            var t0 = Environment.TickCount64; long frames = 0;
            while (!cts.IsCancellationRequested)
            {
                b.ReadOutput(outBuf);
                foreach (var s in outBuf) heardPeak = Math.Max(heardPeak, Math.Abs(s));
                frames++;
                var due = t0 + frames * 10 - Environment.TickCount64;
                if (due > 0) await Task.Delay((int)due);
            }
        });
        var heard = await Until(() => b.AudibleUsers.Contains(1) && b.IsSpeaking(1) && heardPeak > 0.05, 15000);
        var aTx = a.Transmitting;
        cts.Cancel();
        await Task.WhenAll(speak, listen);
        Assert.True(heard, $"B nie słyszy A (słyszalni: {string.Join(",", b.AudibleUsers)}, mówi: {b.IsSpeaking(1)}, szczyt {heardPeak:0.000}, A nadaje: {aTx})");
    }

    /// <summary>A mówi sinusem; zwraca największą głośność, jaką słyszą podani słuchacze (po kolei w tablicy).</summary>
    /// <param name="warmupMs">Najpierw tyle mówi bez liczenia: pierwsze ramki po subskrypcji mogą przejść, zanim LiveKit podłączy odszyfrowanie
    /// (wtedy słychać krótki szum z zaszyfrowanych danych — nie treść).</param>
    static async Task<double[]> SpeakAndListen(VoiceEngine speaker, VoiceEngine[] listeners, Func<double[], bool> done, int ms, int warmupMs = 0)
    {
        using var cts = new CancellationTokenSource();
        var peaks = new double[listeners.Length];
        var speak = Task.Run(async () =>
        {
            var buf = new float[480]; long n = 0, frames = 0; var t0 = Environment.TickCount64;
            while (!cts.IsCancellationRequested)
            {
                for (var i = 0; i < buf.Length; i++, n++) buf[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * n / 48000f);
                speaker.PushMic(buf); frames++;
                var due = t0 + frames * 10 - Environment.TickCount64; if (due > 0) await Task.Delay((int)due);
            }
        });
        var listen = Task.Run(async () =>
        {
            var buf = new float[480]; long frames = 0; var t0 = Environment.TickCount64;
            while (!cts.IsCancellationRequested)
            {
                for (var l = 0; l < listeners.Length; l++) { listeners[l].ReadOutput(buf); foreach (var s in buf) peaks[l] = Math.Max(peaks[l], Math.Abs(s)); }
                frames++;
                var due = t0 + frames * 10 - Environment.TickCount64; if (due > 0) await Task.Delay((int)due);
            }
        });
        if (warmupMs > 0) { await Task.Delay(warmupMs); for (var l = 0; l < peaks.Length; l++) peaks[l] = 0; }
        await Until(() => done(peaks), ms);
        if (warmupMs > 0) await Task.Delay(1500); // jeszcze chwila słuchania, żeby złapać przeciek do osoby z innym kluczem
        cts.Cancel();
        await Task.WhenAll(speak, listen);
        return peaks;
    }

    /// <summary>6.4: szyfrowanie od końca do końca w LiveKit — słyszy tylko osoba z tym samym kluczem rozmowy (także po wymianie klucza na nr 1).</summary>
    [VoiceFact]
    public async Task SzyfrowanaRozmowaSlyszyTylkoTenSamKlucz()
    {
        var cid = Random.Shared.NextInt64(100000, 999999);
        var k3 = new GibCord.Core.E2ee.E2eeKey(GibCord.Core.E2ee.E2eeCrypto.NewCallKey(), 3); // numer ≠ 0: klucz trafia na swoje miejsce w pierścieniu
        await using var a = new VoiceEngine();
        await using var b = new VoiceEngine();
        await using var c = new VoiceEngine();
        a.Options.NoiseSuppression = false; a.Options.AutoGain = false; a.Options.EchoCancellation = false; a.Options.AutoSens = false; a.Options.Sens = -60;
        await a.ConnectAsync(Media(cid, "21"), 64, false, false, default, k3);
        await b.ConnectAsync(Media(cid, "22"), 64, false, false, default, k3);
        await c.ConnectAsync(Media(cid, "23"), 64, false, false, default, new GibCord.Core.E2ee.E2eeKey(GibCord.Core.E2ee.E2eeCrypto.NewCallKey(), 3));
        Assert.True(a.Encrypted && b.Encrypted && c.Encrypted);
        var p = await SpeakAndListen(a, [b, c], x => x[0] > 0.05 && b.IsSpeaking(21), 15000, warmupMs: 2500);
        var dbg = $"\nA: {a.E2eeDebug()}\nB: {b.E2eeDebug()}\nC: {c.E2eeDebug()}";
        Assert.True(p[0] > 0.05, $"B (ten sam klucz) nie słyszy A: szczyt {p[0]:0.000}, stan {b.CryptStateOf(21)}{dbg}");
        Assert.True(p[1] < 0.01, $"C (inny klucz) słyszy A: szczyt {p[1]:0.000}{dbg}");
        Assert.NotEqual(LiveKit.Proto.EncryptionState.Ok, c.CryptStateOf(21));
        Assert.Equal(LiveKit.Proto.EncryptionState.Ok, b.CryptStateOf(21) ?? LiveKit.Proto.EncryptionState.Ok);

        // wymiana klucza (jak po wyjściu kogoś z grupy): B dostaje nr 4 i od razu nim szyfruje, A przełącza się po chwili
        var k4 = new GibCord.Core.E2ee.E2eeKey(GibCord.Core.E2ee.E2eeCrypto.NewCallKey(), 4);
        a.SetE2eeKey(k4, false);
        b.SetE2eeKey(k4, true);
        await Task.Delay(300);
        a.UseE2eeKey(k4);
        var p2 = await SpeakAndListen(a, [b, c], x => x[0] > 0.05, 10000, warmupMs: 500);
        Assert.True(p2[1] < 0.01, $"Po wymianie klucza C słyszy A (szczyt {p2[1]:0.000})");
        Assert.True(p2[0] > 0.05, $"Po wymianie klucza B nie słyszy A (szczyt {p2[0]:0.000}, stan {b.CryptStateOf(21)})");
    }

    /// <summary>Bez prawa mówienia (token bez mikrofonu) — rozmowa działa, ale mikrofon nie jest publikowany.</summary>
    [VoiceFact]
    public async Task BezPrawaMowieniaTylkoSluchamy()
    {
        var cid = Random.Shared.NextInt64(100000, 999999);
        await using var a = new VoiceEngine();
        await a.ConnectAsync(Media(cid, "7", canSpeak: false), 64, false, false);
        Assert.Equal(VoiceState.Connected, a.State);
        Assert.True(await Until(() => !a.CanSpeak, 5000), "Silnik powinien wiedzieć, że nie może mówić");
    }

    /// <summary>Oglądanie na żądanie: obraz ekranu przychodzi dopiero po Watch().</summary>
    [VoiceFact]
    public async Task EkranDopieroPoOgladaniu()
    {
        var cid = Random.Shared.NextInt64(100000, 999999);
        await using var a = new VoiceEngine();
        await using var b = new VoiceEngine();
        await a.ConnectAsync(Media(cid, "11"), 64, false, false);
        await b.ConnectAsync(Media(cid, "12"), 64, false, false);
        var push = await a.StartScreenShare(640, 360, 15, false);
        Assert.NotNull(push);
        using var cts = new CancellationTokenSource();
        var frames = Task.Run(async () =>
        {
            var px = new byte[640 * 360 * 4];
            byte c = 0;
            while (!cts.IsCancellationRequested) { Array.Fill(px, c++); push!(px, 640, 360); await Task.Delay(66); }
        });
        await Task.Delay(2500);
        Assert.DoesNotContain(b.Feeds, f => f.Screen);
        b.Watch(11);
        var got = await Until(() => b.Feeds.Any(f => f.Screen && f.UserId == 11 && f.Width > 0), 15000);
        b.Unwatch(11);
        var gone = await Until(() => !b.Feeds.Any(f => f.Screen), 8000);
        cts.Cancel();
        await frames;
        Assert.True(got, "B nie dostał obrazu ekranu po Watch()");
        Assert.True(gone, "Obraz ekranu powinien zniknąć po Unwatch()");
    }
}

/// <summary>Cała ścieżka z serwerem GibCorda: voice_join → voice_media → LiveKit → dźwięk (kopia testowa serwera z LiveKit).</summary>
public sealed class VoiceLiveTests(LivePair p) : IClassFixture<LivePair>
{
    [LiveFact]
    public async Task GlosPrzezSerwerGibCorda()
    {
        if (Environment.GetEnvironmentVariable("GC_TEST_VOICE") != "1") return; // tylko gdy kopia serwera ma LiveKit
        var (a, b) = (p.A, p.B);
        var vc = a.Store.Servers.SelectMany(s => s.Channels).First(c => c.IsVoice).Id;
        a.Voice.Options.NoiseSuppression = false; a.Voice.Options.AutoGain = false; a.Voice.Options.EchoCancellation = false;
        a.Voice.Options.AutoSens = false; a.Voice.Options.Sens = -60; a.Voice.Options.Muted = false; a.Voice.Options.Deafened = false;
        b.Voice.Options.Muted = false; b.Voice.Options.Deafened = false;
        await a.JoinVoice(vc);
        await b.JoinVoice(vc);
        await p.Until(() => a.Voice.State == Voice.VoiceState.Connected && b.Voice.State == Voice.VoiceState.Connected, "połączenie obu osób z serwerem głosu", 15000);
        await p.Until(() => b.Store.Voice.GetValueOrDefault(vc)?.Count == 2, "obie osoby na liście kanału", 8000);

        using var cts = new CancellationTokenSource();
        double peak = 0;
        var speak = Task.Run(async () =>
        {
            var buf = new float[480]; long n = 0, frames = 0; var t0 = Environment.TickCount64;
            while (!cts.IsCancellationRequested)
            {
                for (var i = 0; i < buf.Length; i++, n++) buf[i] = 0.3f * MathF.Sin(2 * MathF.PI * 330 * n / 48000f);
                a.Voice.PushMic(buf); frames++;
                var due = t0 + frames * 10 - Environment.TickCount64; if (due > 0) await Task.Delay((int)due);
            }
        });
        var listen = Task.Run(async () =>
        {
            var buf = new float[480]; long frames = 0; var t0 = Environment.TickCount64;
            while (!cts.IsCancellationRequested)
            {
                b.Voice.ReadOutput(buf); foreach (var s in buf) peak = Math.Max(peak, Math.Abs(s)); frames++;
                var due = t0 + frames * 10 - Environment.TickCount64; if (due > 0) await Task.Delay((int)due);
            }
        });
        var aid = a.Store.Me.Id;
        await p.Until(() => b.IsSpeaking(aid) && peak > 0.05, "B słyszy A", 15000);
        cts.Cancel(); await Task.WhenAll(speak, listen);

        await a.LeaveVoice();
        await p.Until(() => b.Store.Voice.GetValueOrDefault(vc)?.Count == 1, "A znika z kanału u B", 8000);
        await b.LeaveVoice();
    }
}

/// <summary>Rozmowa mieszana: aplikacja (tester) i strona w przeglądarce w jednym kanale (strona uruchamiana obok, GC_TEST_WEB=1).</summary>
public sealed class VoiceWebTests(LivePair p) : IClassFixture<LivePair>
{
    [LiveFact]
    public async Task AplikacjaIStronaSlyszaSie()
    {
        if (Environment.GetEnvironmentVariable("GC_TEST_WEB") != "1") return;
        var a = p.A;
        var vc = a.Store.Servers.SelectMany(s => s.Channels).First(c => c.IsVoice).Id;
        a.Voice.Options.NoiseSuppression = false; a.Voice.Options.AutoGain = false; a.Voice.Options.EchoCancellation = false;
        a.Voice.Options.AutoSens = false; a.Voice.Options.Sens = -60; a.Voice.Options.Muted = false; a.Voice.Options.Deafened = false;
        await a.JoinVoice(vc);
        await p.Until(() => a.Voice.State == Voice.VoiceState.Connected, "połączenie z serwerem głosu", 15000);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "gc-native-ready"), vc.ToString());
        using var cts = new CancellationTokenSource();
        double peak = 0; long heardFrom = 0;
        var speak = Task.Run(async () =>
        {
            var buf = new float[480]; long n = 0, frames = 0; var t0 = Environment.TickCount64;
            while (!cts.IsCancellationRequested)
            {
                for (var i = 0; i < buf.Length; i++, n++) buf[i] = 0.3f * MathF.Sin(2 * MathF.PI * 330 * n / 48000f) * ((n / 24000) % 2 == 0 ? 1 : 0);
                a.Voice.PushMic(buf); frames++;
                var due = t0 + frames * 10 - Environment.TickCount64; if (due > 0) await Task.Delay((int)due);
            }
        });
        var listen = Task.Run(async () =>
        {
            var buf = new float[480]; long frames = 0; var t0 = Environment.TickCount64;
            while (!cts.IsCancellationRequested)
            {
                a.Voice.ReadOutput(buf); foreach (var s in buf) peak = Math.Max(peak, Math.Abs(s)); frames++;
                foreach (var m in a.Store.Voice.GetValueOrDefault(vc) ?? []) if (m.UserId != a.Store.Me.Id && a.IsSpeaking(m.UserId)) heardFrom = m.UserId;
                var due = t0 + frames * 10 - Environment.TickCount64; if (due > 0) await Task.Delay((int)due);
            }
        });
        await p.Until(() => heardFrom != 0 && peak > 0.05, "aplikacja słyszy stronę", 60000);
        await Task.Delay(8000); // strona też musi usłyszeć aplikację
        cts.Cancel(); await Task.WhenAll(speak, listen);
        await a.LeaveVoice();
    }
}
