using System.Diagnostics;
using System.Text.Json;
using GibCord.Core;
using GibCord.Core.E2ee;

namespace GibCord.Core.Tests;

/// <summary>6.4: szyfrowane rozmowy — zgodność pudełek i kodu bezpieczeństwa ze stroną (WebCrypto) i przebieg wymiany klucza.</summary>
public class E2eeCryptoTests
{
    // Wektor z WebCrypto (Node 22, ten sam kod co strona i tools/test-e2ee.mjs serwera) — nie wymaga Node przy teście.
    const string APriv = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgze3evmw8og6q5wbruAyhIVTX0Xp3raPV3OCBt8xQUBihRANCAARVdO4EpZLd+c7bU2RNzGEZiJ7wbtetXTMRZKlyoBwWEpEbxcda2KKp5QsgPWqzeYmrrBXUQ0bPJXueGkSjyAr4";
    const string APub = "BFV07gSlkt35zttTZE3MYRmInvBu161dMxFkqXKgHBYSkRvFx1rYoqnlCyA9arN5iausFdRDRs8le54aRKPICvg=";
    const string BPriv = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgkCBqB2CdQabD7G5BaHHWm9Iv2LyTKuQ6sOM8H1aV7WChRANCAATcwoJLiEMlvIXHnIsRMDNlSM6XMNDqgv3KBuUnMuYcutlma2RQt4ba0g6D2pDWTfdtvbrTp8jpIhUvC3ji5wlv";
    const string BPub = "BNzCgkuIQyW8hcecixEwM2VIzpcw0OqC/coG5Scy5hy62WZrZFC3htrSDoPakNZN9229utOnyOkiFS8LeOLnCW8=";
    const string Info = "4242|7:a1a1a1a1a1a1a1a1|9:0f0f0f0f0f0f0f0f";
    const string Iv = "AQIDBAUGBwgJCgsM";
    const string Payload = "{\"k\":\"Zm9vYmFyYmF6cXV4enp6enp6enp6enp6enp6enp6eno\",\"i\":3}";
    const string Ct = "B1i5mHfad8aGFFSYU7wWi0FXsr5uCcOWFtPSa1E6St3sLzhbGRB06bjhccBRONbtQiS1nhM+7TlqA0sLpSDF9R0GwN0I4W2eKQ==";

    [Fact]
    public void PudelkoZeStronyOtwieraSieWAplikacji()
    {
        using var b = E2eeCrypto.ImportPrivate(Convert.FromBase64String(BPriv));
        Assert.Equal(BPub, E2eeCrypto.PublicRaw(b));
        var text = E2eeCrypto.Open(b, APub, Info, new E2eeBox(Iv, Ct));
        Assert.Equal(Payload, text);
        Assert.Equal(("Zm9vYmFyYmF6cXV4enp6enp6enp6enp6enp6enp6eno", 3), E2eeCrypto.ParseKeyPayload(text));
    }

    [Fact]
    public void PudelkoZAplikacjiJestTakieSamoJakZeStrony()
    {
        // ten sam klucz, iv i treść → AES-GCM daje te same bajty: aplikacja szyfruje dokładnie tak jak strona
        using var a = E2eeCrypto.ImportPrivate(Convert.FromBase64String(APriv));
        var box = E2eeCrypto.Seal(a, BPub, Info, Payload, Convert.FromBase64String(Iv));
        Assert.Equal(Ct, box.Ct);
        Assert.Equal(Iv, box.Iv);
        Assert.Equal(Payload, E2eeCrypto.KeyPayload("Zm9vYmFyYmF6cXV4enp6enp6enp6enp6enp6enp6eno", 3));
    }

    [Fact]
    public void ZleInfoAlboZmienionaTrescNieOtwieraja()
    {
        using var b = E2eeCrypto.ImportPrivate(Convert.FromBase64String(BPriv));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => E2eeCrypto.Open(b, APub, "4242|7:a1a1a1a1a1a1a1a1|9:ffffffffffffffff", new E2eeBox(Iv, Ct)));
        var bad = Convert.FromBase64String(Ct); bad[3] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => E2eeCrypto.Open(b, APub, Info, new E2eeBox(Iv, Convert.ToBase64String(bad))));
    }

    [Fact]
    public void KodBezpieczenstwaJakNaStronie()
    {
        // wektor z WebCrypto: linie "uid:pub" sortowane po uid (liczbowo: 7, 9, 12), SHA-256 → 3 grupy po 4 cyfry
        Assert.Equal("24 33 · 46 54 · 64 11", E2eeCrypto.SafetyCode([(9, BPub), (7, APub), (12, APub)]));
        Assert.Matches(@"^\d\d \d\d · \d\d \d\d · \d\d \d\d$", E2eeCrypto.SafetyCode([(1, APub)]));
    }

    [Fact]
    public void KluczRozmowyIUrzadzenie()
    {
        var k = E2eeCrypto.NewCallKey();
        Assert.Equal(43, k.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", k);
        Assert.Matches("^[0-9a-f]{16}$", E2eeCrypto.NewDeviceId());
        using var key = E2eeCrypto.NewKey();
        var raw = Convert.FromBase64String(E2eeCrypto.PublicRaw(key));
        Assert.Equal(65, raw.Length);
        Assert.Equal(4, raw[0]);
        using var back = E2eeCrypto.ImportPrivate(E2eeCrypto.ExportPrivate(key));
        Assert.Equal(E2eeCrypto.PublicRaw(key), E2eeCrypto.PublicRaw(back));
        Assert.Equal(19, new E2eeKey(k, 19).Slot); // miejsce = numer (strona: keyringSize 256)
        Assert.Equal(0, E2eeKey.Next(254));        // 255 nie mieści się w pierścieniu LiveKit .NET — po 254 wracamy do 0
        Assert.Equal(4, E2eeKey.Next(3));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(k), new E2eeKey(k, 0).Material);
    }

    static string? Node()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var n in new[] { "node", "node.exe" })
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, n))) return Path.Combine(dir, n);
        return null;
    }

    static JsonElement RunNode(object input)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "e2ee-node.mjs");
        var psi = new ProcessStartInfo(Node()!, $"\"{script}\"") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(JsonSerializer.Serialize(input));
        p.StandardInput.Close();
        var outp = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(20000);
        Assert.True(p.ExitCode == 0, "node: " + err);
        return JsonDocument.Parse(outp).RootElement.Clone();
    }

    /// <summary>Na żywo z Node (WebCrypto), gdy jest na komputerze: losowe klucze, pudełka w obie strony, kod bezpieczeństwa.</summary>
    [Fact]
    public void ZgodnoscZWebCryptoNaZywo()
    {
        if (Node() is null) return; // bez Node zostają wektory powyżej
        using var a = E2eeCrypto.NewKey();
        using var b = E2eeCrypto.NewKey();
        var aPub = E2eeCrypto.PublicRaw(a); var bPub = E2eeCrypto.PublicRaw(b);
        var info = E2eeCrypto.Info(77, 1, "0123456789abcdef", 2, "fedcba9876543210");
        var key = E2eeCrypto.NewCallKey();
        // aplikacja → strona
        var box = E2eeCrypto.Seal(a, bPub, info, E2eeCrypto.KeyPayload(key, 5));
        var opened = RunNode(new { myPriv = Convert.ToBase64String(E2eeCrypto.ExportPrivate(b)), theirPub = aPub, info, box = new { iv = box.Iv, ct = box.Ct } });
        Assert.Equal((key, 5), E2eeCrypto.ParseKeyPayload(opened.GetProperty("text").GetString()!));
        // strona → aplikacja
        var sealedByNode = RunNode(new { myPriv = Convert.ToBase64String(E2eeCrypto.ExportPrivate(b)), theirPub = aPub, info, text = "{\"k\":\"" + key + "\",\"i\":200}" }).GetProperty("box");
        var back = E2eeCrypto.Open(a, bPub, info, new E2eeBox(sealedByNode.GetProperty("iv").GetString()!, sealedByNode.GetProperty("ct").GetString()!));
        Assert.Equal((key, 200), E2eeCrypto.ParseKeyPayload(back));
        // kod bezpieczeństwa
        var code = RunNode(new { lines = new[] { $"12:{bPub}", $"3:{aPub}" } }).GetProperty("code").GetString();
        Assert.Equal(code, E2eeCrypto.SafetyCode([(12, bPub), (3, aPub)]));
    }
}

/// <summary>Wymiana klucza między kilkoma klientami przez udawany serwer w pamięci (ramki jak e2ee_send → e2ee_msg).</summary>
public class E2eeProtocolTests
{
    sealed class Hub
    {
        public readonly object Lock = new();
        public readonly HashSet<long> Room = [];
        public readonly Dictionary<long, E2eeCall> Calls = [];
        public readonly Dictionary<long, List<E2eeDeviceInfo>> Devices = [];
        public int Sent;

        public E2eeCall Join(long uid, bool group, long call = 500)
        {
            var key = E2eeCrypto.NewKey();
            var dev = new E2eeDevice(E2eeCrypto.NewDeviceId(), key);
            lock (Lock) { Devices[uid] = [new E2eeDeviceInfo { Dev = dev.Dev, Pub = dev.Pub, At = Environment.TickCount64 }]; Room.Add(uid); }
            var c = new E2eeCall(call, uid, group, dev, new Net(this, uid), () => { lock (Lock) return Room.ToList(); })
            {
                ReqAfter = TimeSpan.FromMilliseconds(250), SwitchAfter = TimeSpan.FromMilliseconds(150), WaitingFor = TimeSpan.FromMilliseconds(400),
            };
            lock (Lock) Calls[uid] = c;
            return c;
        }

        public void Leave(long uid) { lock (Lock) { Room.Remove(uid); Calls.Remove(uid, out var c); c?.Dispose(); } }

        public async Task RoomChanged() { List<E2eeCall> all; lock (Lock) all = [.. Calls.Values]; foreach (var c in all) await c.OnRoomChanged(); }

        sealed class Net(Hub h, long me) : IE2eeTransport
        {
            public Task Send(object frame)
            {
                var f = JsonSerializer.SerializeToElement(frame);
                Assert.Equal("e2ee_send", f.GetProperty("t").GetString());
                var to = f.GetProperty("to").GetInt64();
                E2eeCall? target;
                lock (h.Lock) { h.Sent++; h.Calls.TryGetValue(to, out target); if (!h.Room.Contains(to)) target = null; }
                if (target is null) return Task.CompletedTask;
                var msg = JsonSerializer.SerializeToElement(new
                {
                    t = "e2ee_msg", call = f.GetProperty("call").GetInt64(), from = me, fromDev = f.GetProperty("fromDev").GetString(),
                    dev = f.TryGetProperty("dev", out var d) ? d.GetString() : null, kind = f.GetProperty("kind").GetString(),
                    box = f.TryGetProperty("box", out var b) ? b : default(JsonElement?),
                });
                _ = Task.Run(async () => { await Task.Delay(5); await target.OnMessage(msg); });
                return Task.CompletedTask;
            }

            public Task<Dictionary<long, List<E2eeDeviceInfo>>> Devices(IReadOnlyCollection<long> uids)
            {
                lock (h.Lock) return Task.FromResult(uids.Where(h.Devices.ContainsKey).ToDictionary(u => u, u => h.Devices[u].ToList()));
            }
        }
    }

    static async Task<bool> Until(Func<bool> ok, int ms = 4000)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (ok()) return true; await Task.Delay(20); }
        return ok();
    }

    [Fact]
    public async Task LiderOdpowiadaAPoWyjsciuZGrupyJestNowyKlucz()
    {
        var h = new Hub();
        var a = h.Join(5, group: true);
        await a.Start([]);
        Assert.Equal(E2eeStatus.Active, a.Status);
        Assert.Equal(0, a.Key!.Index);

        var b = h.Join(3, group: true);
        await h.RoomChanged(); // voice_state z nową osobą dochodzi do wszystkich
        await b.Start([5]);
        Assert.True(await Until(() => b.Key == a.Key), "B dostaje klucz od A (lider: jedyna osoba z kluczem)");
        Assert.Equal(a.Device.Dev, b.PeerDevice(5));

        var c = h.Join(8, group: true);
        await h.RoomChanged();
        await c.Start([5, 3]);
        Assert.True(await Until(() => c.Key == a.Key), "C dostaje ten sam klucz");
        Assert.True(await Until(() => c.PeerDevice(5) == a.Device.Dev && c.PeerDevice(3) == b.Device.Dev), "C zna urządzenia obu osób (odpowiedź „hi” od nie-lidera)");
        var oldKey = a.Key!;
        Assert.Equal(await a.SafetyCode(), await b.SafetyCode());
        Assert.Equal(await a.SafetyCode(), await c.SafetyCode());

        // C wychodzi z grupy → lider (najmniejsze uid z kluczem = B) losuje nowy klucz z numerem +1
        var switched = 0;
        b.SwitchTo += _ => Interlocked.Increment(ref switched);
        h.Leave(8);
        await h.RoomChanged();
        Assert.True(await Until(() => a.Key is { Index: 1 } && b.Key is { Index: 1 } && a.Key == b.Key), "A i B mają nowy klucz nr 1");
        Assert.NotEqual(oldKey.Key, a.Key!.Key);
        Assert.Equal(oldKey, c.Key); // C go nie zna
        Assert.True(await Until(() => Volatile.Read(ref switched) == 1, 2000), "lider przełącza szyfrowanie po chwili");
    }

    [Fact]
    public async Task WRozmowieWDwojeBezWymianyKlucza()
    {
        var h = new Hub();
        var a = h.Join(1, group: false);
        await a.Start([]);
        var b = h.Join(2, group: false);
        await b.Start([1]);
        Assert.True(await Until(() => b.Key == a.Key));
        var k = a.Key;
        h.Leave(2);
        await h.RoomChanged();
        await Task.Delay(300);
        Assert.Equal(k, a.Key);
    }

    [Fact]
    public async Task GdyWszyscyCzekajaKluczLosujeNajmniejszeUid()
    {
        // obie osoby weszły „nie same” (np. stara lista w pokoju) i czekają na siebie nawzajem
        var h = new Hub();
        var a = h.Join(2, group: false);
        var b = h.Join(4, group: false);
        await Task.WhenAll(a.Start([4]), b.Start([2]));
        Assert.True(await Until(() => a.Key is not null && a.Key == b.Key), "klucz ustalony mimo braku lidera");
    }

    [Fact]
    public async Task PustyPokojPoChwiliToWlasnyKlucz()
    {
        var h = new Hub();
        var a = h.Join(7, group: false);
        await a.Start([9]); // 9 już wyszła — nikt nie odpowie
        Assert.Equal(E2eeStatus.Waiting, a.Status);
        Assert.True(await Until(() => a.Status == E2eeStatus.Active && a.Key is { Index: 0 }));
    }

    [Fact]
    public async Task ObcyKluczNieOtworzySie()
    {
        // pudełko zaadresowane do innego urządzenia jest pomijane, a zepsute nie psuje stanu
        var h = new Hub();
        var a = h.Join(1, group: false);
        await a.Start([2]);
        var fake = JsonSerializer.SerializeToElement(new { t = "e2ee_msg", call = 500, from = 2, fromDev = "0000000000000000", dev = a.Device.Dev, kind = "key", box = new { iv = "AQIDBAUGBwgJCgsM", ct = "AAAAAAAAAAAAAAAAAAAAAA==" } });
        await a.OnMessage(fake);
        Assert.Null(a.Key);
        a.Dispose();
    }
}

public class ProfileAndBridgeTests
{
    [Fact]
    public void KolorTekstuNaKarcieProfilu()
    {
        Assert.True(ProfileLook.IsLight("#ffffff", "#c8fff0"));
        Assert.False(ProfileLook.IsLight("#3a3f73", "#0d0f1f"));
        Assert.True(ProfileLook.ValidTheme(["#ff0000", "#00ff00"]));
        Assert.False(ProfileLook.ValidTheme(["red", "#00ff00"]));
        Assert.Equal("Zorza", ProfileLook.DecorById("zorza")?.Name);
        Assert.Null(ProfileLook.DecorById("nie-ma"));
    }

    [Fact]
    public void WizytowkaDoZapisu()
    {
        var tiles = new List<ShowcaseTile> { new() { K = "game", Name = "Minecraft", Hours = 38 }, new() { K = "clip", Mid = 12, Url = "/uploads/x.mp4" }, new() { K = "spotify" }, new() { K = "text", T = "hej" } };
        var json = JsonSerializer.Serialize(tiles.Select(t => t.ToPatch()));
        Assert.Equal("[{\"k\":\"game\",\"name\":\"Minecraft\"},{\"k\":\"clip\",\"mid\":12},{\"k\":\"spotify\"},{\"k\":\"text\",\"t\":\"hej\"}]", json);
    }

    [Fact]
    public void PolaZSerwera()
    {
        var u = Json.Deserialize<User>("{\"id\":4,\"username\":\"ola\",\"decor\":\"zorza\",\"profileFx\":\"snieg\",\"profileTheme\":[\"#112233\",\"#445566\"],\"showcase\":[{\"k\":\"game\",\"name\":\"Tibia\",\"hours\":12}]}")!;
        Assert.Equal("zorza", u.Decor);
        Assert.Equal("snieg", u.ProfileFx);
        Assert.Equal(["#112233", "#445566"], u.ProfileTheme!);
        Assert.Equal(12, u.Showcase![0].Hours);
        var m = Json.Deserialize<Message>("{\"id\":1,\"channelId\":2,\"authorId\":3,\"content\":\"hej\",\"ext\":{\"src\":\"discord\",\"name\":\"Kasia\",\"avatar\":null}}")!;
        Assert.True(m.FromDiscord);
        Assert.Equal("Kasia", m.Ext!.Name);
        var c = Json.Deserialize<Channel>("{\"id\":9,\"serverId\":1,\"name\":\"ogólne\",\"bridge\":{\"guild\":\"Ekipa\",\"channel\":\"general\",\"paused\":true}}")!;
        Assert.True(c.Bridge is { Guild: "Ekipa", Channel: "general", Paused: true });
    }

    [Fact]
    public void KlodkaZStanuIVoiceState()
    {
        var s = new Store();
        s.Load(new StateResponse { Me = new User { Id = 1 }, Dms = [new Dm { Id = 20, UserIds = [1, 2] }], Voice = new() { [20] = [new VoiceMember { UserId = 2 }] }, VoiceE2ee = [20] });
        Assert.Contains(20L, s.VoiceE2ee);
        s.Apply(new GatewayEvent("voice_state", JsonSerializer.SerializeToElement(new { channelId = 20, members = Array.Empty<object>(), e2ee = false })));
        Assert.DoesNotContain(20L, s.VoiceE2ee);
        s.Apply(new GatewayEvent("voice_state", JsonSerializer.SerializeToElement(new { channelId = 20, members = new[] { new { userId = 1 } }, e2ee = true })));
        Assert.Contains(20L, s.VoiceE2ee);
    }

    [Fact]
    public void WiadomosciZDiscordaNieSklejajaSieMiedzyOsobami()
    {
        Message M(string? ext) => new() { AuthorId = 50, Ext = ext is null ? null : new MessageExt { Src = "discord", Name = ext } };
        Assert.True(M("Kasia").SameSender(M("Kasia")));
        Assert.False(M("Kasia").SameSender(M("Tomek")));
        Assert.False(M(null).SameSender(M("Kasia")));
        Assert.True(M(null).SameSender(M(null)));
    }
}

/// <summary>6.4 na żywo: dwie nowe osoby na kopii testowej serwera (GC_TEST_URL, GC_TEST_E2EE=1, serwer z LiveKit) — szyfrowana rozmowa prywatna.</summary>
public sealed class E2eeLiveTests
{
    [Fact]
    public async Task RozmowaPrywatnaSzyfrowanaNaZywo()
    {
        var env = Environment.GetEnvironmentVariable("GC_TEST_URL");
        if (string.IsNullOrEmpty(env) || Environment.GetEnvironmentVariable("GC_TEST_E2EE") != "1") return;
        var l = new object();
        GibCordClient New() => new(new Uri(env), new MemoryTokenStore(), null, x => { lock (l) x(); });
        await using var a = New();
        await using var b = New();
        var tag = Guid.NewGuid().ToString("N")[..6];
        const string pass = "Haslo-testowe-1234";
        Assert.NotNull((await a.Register("wa" + tag, pass, "Aplikacja A", $"wa{tag}@example.com")).Token);
        Assert.NotNull((await b.Register("wb" + tag, pass, "Aplikacja B", $"wb{tag}@example.com")).Token);
        await a.Start(); await b.Start();
        async Task Until(Func<bool> ok, string what, int ms = 10000)
        {
            var end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end) { lock (l) if (ok()) return; await Task.Delay(50); }
            Assert.Fail("Nie doczekano się: " + what);
        }
        await Until(() => a.Store.Loaded && b.Store.Loaded && a.Status == GatewayStatus.Connected && b.Status == GatewayStatus.Connected, "połączenie");
        var (dm, err) = await a.OpenDm(b.Store.Me.Id);
        Assert.Null(err);
        await Until(() => b.Store.DmById(dm) is not null, "rozmowa u B");
        foreach (var c in new[] { a, b })
        {
            var o = c.Voice.Options;
            o.NoiseSuppression = false; o.AutoGain = false; o.EchoCancellation = false; o.AutoSens = false; o.Sens = -60; o.Muted = false; o.Deafened = false;
        }
        // A dzwoni — sam w pokoju losuje klucz; B dołącza — „hi”, A (lider) odsyła klucz w pudełku
        await a.JoinVoice(dm);
        await Until(() => a.Voice.State == Voice.VoiceState.Connected && a.Voice.Encrypted && a.E2ee?.Status == E2eeStatus.Active, "A w szyfrowanym pokoju", 20000);
        await Until(() => b.Store.VoiceE2ee.Contains(dm), "kłódka u B przed wejściem");
        await b.JoinVoice(dm);
        await Until(() => b.Voice.State == Voice.VoiceState.Connected && b.E2ee?.Key is not null, "B dostaje klucz i łączy się", 20000);
        Assert.Equal(a.E2ee!.Key, b.E2ee!.Key);
        var aid = a.Store.Me.Id;
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
        try { await Until(() => peak > 0.05 && b.IsSpeaking(aid), "B słyszy odszyfrowany głos A", 20000); }
        finally { cts.Cancel(); await Task.WhenAll(speak, listen); }
        Assert.Equal(LiveKit.Proto.EncryptionState.Ok, b.Voice.CryptStateOf(aid) ?? LiveKit.Proto.EncryptionState.Ok);
        var codeA = await a.SafetyCode();
        Assert.NotNull(codeA);
        Assert.Equal(codeA, await b.SafetyCode());
        await b.LeaveVoice();
        await a.LeaveVoice();
    }
}

/// <summary>
/// 6.4 na żywo: most z Discordem z punktu widzenia aplikacji — kopia serwera z udawanym Discordem
/// (GC_TEST_BRIDGE_URL = serwer, GC_FAKE_DISCORD = tools/udawany-discord.mjs). Ustawienia mostu, kod, połączenie, wiadomość z Discorda, wstrzymanie, rozłączenie.
/// </summary>
public sealed class BridgeLiveTests
{
    [Fact]
    public async Task MostZDiscordemWAplikacji()
    {
        var url = Environment.GetEnvironmentVariable("GC_TEST_BRIDGE_URL");
        var fake = Environment.GetEnvironmentVariable("GC_FAKE_DISCORD");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(fake)) return;
        var l = new object();
        await using var a = new GibCordClient(new Uri(url), new MemoryTokenStore(), null, x => { lock (l) x(); });
        var tag = Guid.NewGuid().ToString("N")[..6];
        Assert.NotNull((await a.Register("mw" + tag, "Haslo-testowe-1234", "Most Windows", $"mw{tag}@example.com")).Token);
        await a.Start();
        async Task Until(Func<bool> ok, string what, int ms = 8000)
        {
            var end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end) { lock (l) if (ok()) return; await Task.Delay(50); }
            Assert.Fail("Nie doczekano się: " + what);
        }
        await Until(() => a.Store.Loaded && a.Status == GatewayStatus.Connected, "połączenie");
        var srv = await a.Api.CreateServer("Mostowy " + tag);
        await Until(() => a.Store.ServerById(srv.Id) is not null, "serwer w stanie");
        var chan = a.Store.ServerById(srv.Id)!.Channels.First(c => c.Type == "text");
        await a.OpenChannel(chan.Id);

        var info = await a.Api.GetBridges(srv.Id);
        Assert.True(info.Enabled);
        Assert.Equal("ready", info.Status);
        Assert.Contains("client_id=", info.InviteUrl);
        Assert.Empty(info.Items);
        var code = await a.Api.BridgeCode(chan.Id);
        Assert.Matches("^GC-[A-Z2-9]{4}-[A-Z2-9]{4}$", code.Code);
        Assert.Contains(code.Code, code.Command);

        using var http = new HttpClient();
        var dcChan = "9" + Random.Shared.Next(100000, 999999); // nowy kanał „Discorda” w każdym przebiegu (jeden kanał = jeden most)
        async Task Dispatch(string t, object d) =>
            (await http.PostAsync(fake + "/_test/dispatch", new StringContent(JsonSerializer.Serialize(new { t, d }), System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        // na „Discordzie” ktoś z prawem „Zarządzanie kanałami” wpisuje /gibcord polacz kod:…
        await Dispatch("INTERACTION_CREATE", new
        {
            id = "i" + tag, token = "itok", type = 2, guild_id = "777", channel_id = dcChan, channel = new { id = dcChan, name = "general", type = 0 },
            member = new { permissions = "16", user = new { id = "55", username = "kasia" } },
            data = new { name = "gibcord", options = new[] { new { type = 1, name = "polacz", options = new[] { new { name = "kod", type = 3, value = code.Code } } } } },
        });
        await Until(() => a.Store.ChannelById(chan.Id)?.Bridge is { Guild: "Ekipa z Discorda", Channel: "general", Paused: false }, "znaczek mostu na kanale (channel_update)");
        var items = (await a.Api.GetBridges(srv.Id)).Items;
        Assert.Single(items);
        Assert.Equal(chan.Id, items[0].ChannelId);

        // wiadomość z Discorda: autor = konto „Discord”, nazwa i awatar w ext; dwie osoby z Discorda się nie sklejają
        await Dispatch("MESSAGE_CREATE", new { id = "5001" + tag, channel_id = dcChan, guild_id = "777", type = 0, content = "siema z discorda", author = new { id = "55", username = "kasia", global_name = "Kasia", avatar = (string?)null }, member = new { nick = "Kasia z DC" }, mentions = Array.Empty<object>(), attachments = Array.Empty<object>() });
        await Dispatch("MESSAGE_CREATE", new { id = "5002" + tag, channel_id = dcChan, guild_id = "777", type = 0, content = "ja też", author = new { id = "56", username = "tomek", avatar = (string?)null }, member = new { }, mentions = Array.Empty<object>(), attachments = Array.Empty<object>() });
        await Until(() => a.Store.Window(chan.Id).Items.Count(m => m.FromDiscord) >= 2, "wiadomości z Discorda w oknie kanału");
        Message k, t;
        lock (l) { k = a.Store.Window(chan.Id).Items.First(m => m.Content == "siema z discorda"); t = a.Store.Window(chan.Id).Items.First(m => m.Content == "ja też"); }
        Assert.Equal("Kasia z DC", k.Ext!.Name);
        Assert.Equal("discord", k.Ext.Src);
        Assert.Equal("webhook", a.Store.U(k.AuthorId)?.Bot);
        Assert.Equal(k.AuthorId, t.AuthorId);
        Assert.False(k.SameSender(t));

        // wstrzymanie i rozłączenie widać na żywo
        var patched = await a.Api.PatchBridge(items[0].Id, true);
        Assert.True(patched.Paused);
        await Until(() => a.Store.ChannelById(chan.Id)?.Bridge is { Paused: true }, "wstrzymany most");
        await a.Api.DeleteBridge(items[0].Id);
        await Until(() => a.Store.ChannelById(chan.Id) is { Bridge: null }, "rozłączony most");
        Assert.Empty((await a.Api.GetBridges(srv.Id)).Items);
    }
}
