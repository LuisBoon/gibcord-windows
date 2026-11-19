using GibCord.Core;

namespace GibCord.Core.Tests;

/// <summary>Test na żywo — tylko gdy jest kopia testowa serwera (zmienna GC_TEST_URL, np. http://127.0.0.1:8693).
/// Wymaga kont „tester” i „ola” z hasłem z GC_TEST_PASS. Nigdy nie uruchamiać na produkcji.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GC_TEST_URL"))) Skip = "Brak kopii testowej serwera (GC_TEST_URL)";
    }
}

/// <summary>Dwa zalogowane konta na całą klasę testów (serwer ma limit 10 logowań na minutę).</summary>
public sealed class LivePair : IAsyncLifetime
{
    public static readonly Uri Url = new(Environment.GetEnvironmentVariable("GC_TEST_URL") ?? "http://127.0.0.1:1");
    public static readonly string Pass = Environment.GetEnvironmentVariable("GC_TEST_PASS") ?? "Tajne-haslo-123";
    public readonly object Lock = new();
    public GibCordClient A = null!, B = null!;
    public readonly List<Message> NotifA = [], NotifB = [];
    public readonly List<User> FriendReqB = [];

    public GibCordClient New() => new(Url, new MemoryTokenStore(), null, a => { lock (Lock) a(); });

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GC_TEST_URL"))) return;
        A = New(); B = New();
        Assert.NotNull((await A.Login("tester", Pass)).Token);
        Assert.NotNull((await B.Login("@Ola", Pass)).Token);
        A.Notify += m => NotifA.Add(m);
        B.Notify += m => NotifB.Add(m);
        B.FriendRequest += u => FriendReqB.Add(u);
        await A.Start(); await B.Start();
        await Until(() => A.Store.Loaded && B.Store.Loaded && A.Status == GatewayStatus.Connected && B.Status == GatewayStatus.Connected, "połączenie obu kont");
    }

    public async Task DisposeAsync()
    {
        if (A is not null) await A.DisposeAsync();
        if (B is not null) await B.DisposeAsync();
    }

    public async Task Until(Func<bool> cond, string what, int ms = 8000)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            lock (Lock) if (cond()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Nie doczekano się: " + what);
    }
}

public sealed class LiveTests(LivePair p) : IClassFixture<LivePair>
{
    [LiveFact]
    public async Task ZleHasloToPolskiKomunikat()
    {
        var c = p.New();
        var ex = await Assert.ThrowsAsync<ApiException>(() => c.Login("tester", "zle-haslo-xyz"));
        Assert.Equal(401, ex.Status);
        Assert.Contains("hasło", ex.Message);
        await c.DisposeAsync();
    }

    [LiveFact]
    public async Task RozmowaNaKanaleIWDm()
    {
        var (a, b) = (p.A, p.B);
        var chan = a.Store.Servers.SelectMany(s => s.Channels).First(c => c.Name == "pogaduchy");
        await a.OpenChannel(chan.Id);
        await p.Until(() => a.Store.Window(chan.Id).Items.Count > 0 && !a.Store.Window(chan.Id).FromCache, "wiadomości kanału");

        // B ma okno w tle — wzmianka daje powiadomienie i licznik
        b.SetWindowActive(false);
        await b.OpenChannel(0);
        var text = $"test {Guid.NewGuid():N} <@{b.Store.Me.Id}> **gruby**";
        Assert.Null(await a.Send(chan.Id, text));
        await p.Until(() => a.Store.Window(chan.Id).Items.Any(m => m.Content == text && m.Send == SendState.Sent && m.Id > 0), "potwierdzenie wysłania u A");
        await p.Until(() => p.NotifB.Any(m => m.Content == text), "powiadomienie u B");
        lock (p.Lock)
        {
            Assert.True(b.Store.IsUnread(chan.Id));
            Assert.True(b.Store.MentionsIn(chan.Id) >= 1);
            Assert.Single(a.Store.Window(chan.Id).Items, m => m.Content == text); // bez duplikatu po message_create
        }

        // B otwiera kanał na wierzchu — przeczytane
        b.SetWindowActive(true);
        await b.OpenChannel(chan.Id);
        await p.Until(() => !b.Store.IsUnread(chan.Id) && b.Store.MentionsIn(chan.Id) == 0, "odczyt u B");

        // edycja, reakcja, usunięcie widać u drugiej osoby
        var sent = a.Store.Window(chan.Id).Items.Last(m => m.Content == text);
        Assert.Null(await a.Edit(sent, text + " (poprawione)"));
        await p.Until(() => b.Store.Window(chan.Id).Items.Any(m => m.Id == sent.Id && m.EditedAt != null), "edycja u B");
        Assert.Null(await b.ToggleReaction(b.Store.Window(chan.Id).Items.First(m => m.Id == sent.Id), "👍"));
        await p.Until(() => a.Store.Window(chan.Id).Items.First(m => m.Id == sent.Id).Reactions.Any(r => r.Emoji == "👍"), "reakcja u A");
        Assert.Null(await a.Delete(a.Store.Window(chan.Id).Items.First(m => m.Id == sent.Id)));
        await p.Until(() => b.Store.Window(chan.Id).Items.All(m => m.Id != sent.Id), "usunięcie u B");

        // DM: domyślnie każda wiadomość to powiadomienie
        var (dmId, err) = await b.OpenDm(a.Store.Me.Id);
        Assert.Null(err);
        a.SetWindowActive(false);
        Assert.Null(await b.Send(dmId, "hej w prywatnej"));
        await p.Until(() => p.NotifA.Any(m => m.ChannelId == dmId && m.Content == "hej w prywatnej"), "powiadomienie DM u A");
        await p.Until(() => a.Store.DmById(dmId) is not null && a.Store.IsUnread(dmId), "DM nieprzeczytany u A");
        lock (p.Lock) Assert.Contains("Ola", a.Store.NotificationTitle(p.NotifA.Last()));
        a.SetWindowActive(true);
    }

    [LiveFact]
    public async Task PisanieZalacznikIStarszeWiadomosci()
    {
        var (a, b) = (p.A, p.B);
        var chan = a.Store.Servers.SelectMany(s => s.Channels).First(c => c.Name == "błędy");
        await a.OpenChannel(chan.Id);
        await b.OpenChannel(chan.Id);
        a.Typing(chan.Id);
        await p.Until(() => b.Store.TypingIn(chan.Id).Contains(a.Store.Me.Id), "„pisze…” u B");

        // obrazek 1×1 PNG
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var (att, upErr) = await a.Upload(new MemoryStream(png), "kropka.png", "image/png", png.Length);
        Assert.Null(upErr);
        Assert.NotNull(att);
        Assert.Null(await a.Send(chan.Id, "", null, [att!]));
        await p.Until(() => b.Store.Window(chan.Id).Items.Any(m => m.Attachments.Any(x => x.Id == att!.Id && x.IsImage && x.Width == 1)), "obrazek u B");
        Assert.NotNull(a.Api.Abs(att!.Url));

        // seria wiadomości (limit serwera: 10 na 5 s) → dociąganie starszych w kolejności, bez dziur
        var have = b.Store.Window(chan.Id).Items.Count(m => m.Content.StartsWith("seria"));
        for (var i = 0; i < 60 - have; i++) { await a.Api.SendMessage(chan.Id, "seria " + i); await Task.Delay(560); }
        await b.JumpToPresent(chan.Id);
        await p.Until(() => b.Store.Window(chan.Id).Items.Count >= 50, "ostatnie 50 u B");
        Assert.True(b.Store.Window(chan.Id).HasOlder);
        Assert.True(await b.LoadOlder(chan.Id) > 0);
        lock (p.Lock)
        {
            var ids = b.Store.Window(chan.Id).Items.Select(m => m.Id).ToList();
            Assert.Equal(ids.OrderBy(x => x), ids);
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.True(ids.Count > 50);
        }
    }

    [LiveFact]
    public async Task Znajomi()
    {
        var (a, b) = (p.A, p.B);
        var bid = b.Store.Me.Id;
        if (a.Store.Friends.ContainsKey(bid)) { Assert.Null(await a.RemoveFriend(bid)); await p.Until(() => !a.Store.Friends.ContainsKey(bid), "usunięcie znajomego"); }
        Assert.Null(await a.AddFriend("ola"));
        await p.Until(() => p.FriendReqB.Count > 0 && b.Store.Friends.TryGetValue(a.Store.Me.Id, out var f) && f.Status == "incoming", "zaproszenie u B");
        Assert.Null(await b.AcceptFriend(a.Store.Me.Id));
        await p.Until(() => a.Store.Friends.TryGetValue(bid, out var f) && f.Status == "friends", "znajomi u A");
        // status widać u znajomego
        Assert.Null(await b.SetStatus("dnd"));
        await p.Until(() => a.Store.StatusOf(bid) == "dnd", "status u A");
        Assert.Null(await b.SetStatus("online"));
    }

    [LiveFact]
    public async Task PamiecPodrecznaNaDysku()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc-cache-" + Guid.NewGuid().ToString("N"));
        var cache = new DiskCache(dir);
        var a = p.A;
        var chan = a.Store.Servers.SelectMany(s => s.Channels).First(c => c.Name == "co-nowego");
        await a.OpenChannel(chan.Id);
        await p.Until(() => a.Store.Window(chan.Id).Items.Count > 0, "wiadomości");
        List<Message> items; lock (p.Lock) items = a.Store.Window(chan.Id).Items.ToList();
        cache.SaveMessages(chan.Id, items);
        await p.Until(() => cache.LoadMessages(chan.Id) is { Count: > 0 }, "zapis na dysku", 5000);
        var back = cache.LoadMessages(chan.Id)!;
        Assert.Equal(items.Select(m => m.Id), back.Select(m => m.Id));
        Assert.Equal(items[^1].Content, back[^1].Content);
        var (_, raw) = await a.Api.GetStateRaw();
        cache.SaveState(raw);
        await p.Until(() => cache.LoadState() is not null, "stan na dysku", 5000);
        Assert.Equal(a.Store.Me.Id, cache.LoadState()!.Me.Id);
        cache.Clear();
        Assert.Null(cache.LoadMessages(chan.Id));
        Directory.Delete(dir, true);
    }
}
