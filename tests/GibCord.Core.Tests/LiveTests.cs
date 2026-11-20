using GibCord.Core;

namespace GibCord.Core.Tests;

/// <summary>Test na żywo — tylko gdy jest kopia testowa serwera (zmienna GC_TEST_URL, np. http://127.0.0.1:8693).
/// Wymaga kont „tester” i „ola” z hasłem z GC_TEST_PASS. Nigdy nie uruchamiać na produkcji.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GC_TEST_URL")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GC_TEST_PASS")))
            Skip = "Brak kopii testowej serwera (GC_TEST_URL i GC_TEST_PASS)";
    }
}

/// <summary>Dwa zalogowane konta na całą klasę testów (serwer ma limit 10 logowań na minutę).</summary>
public sealed class LivePair : IAsyncLifetime
{
    public static readonly Uri Url = new(Environment.GetEnvironmentVariable("GC_TEST_URL") ?? "http://127.0.0.1:1");
    public static readonly string Pass = Environment.GetEnvironmentVariable("GC_TEST_PASS") ?? "";
    public readonly object Lock = new();
    public GibCordClient A = null!, B = null!;
    public readonly List<Message> NotifA = [], NotifB = [];
    public readonly List<User> FriendReqB = [];

    public GibCordClient New() => new(Url, new MemoryTokenStore(), null, a => { lock (Lock) a(); });

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GC_TEST_URL")) || Pass.Length == 0) return;
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
    public async Task WatkiIWyszukiwarka()
    {
        var (a, b) = (p.A, p.B);
        var chan = a.Store.Servers.SelectMany(s => s.Channels).First(c => c.Name == "pogaduchy");
        await a.OpenChannel(chan.Id);
        await b.OpenChannel(chan.Id);
        var tag = "watek" + Guid.NewGuid().ToString("N")[..8];
        Assert.Null(await a.Send(chan.Id, "zaczynam " + tag));
        await p.Until(() => a.Store.Window(chan.Id).Items.Any(m => m.Content == "zaczynam " + tag && m.Id > 0), "wiadomość startowa");
        var starter = a.Store.Window(chan.Id).Items.Last(m => m.Content == "zaczynam " + tag);

        // nowy wątek: znaczek pod wiadomością u obu osób
        var (t, err) = await a.CreateThread(starter.Id, "Plan " + tag);
        Assert.Null(err);
        Assert.NotNull(t);
        await p.Until(() => b.Store.ThreadById(t!.Id) is { } bt && bt.Name == "Plan " + tag, "wątek u B");
        await p.Until(() => b.Store.Window(chan.Id).Items.Any(m => m.Id == starter.Id && m.Thread.Str("name") == "Plan " + tag), "znaczek wątku u B");
        lock (p.Lock) Assert.True(a.Store.ThreadById(t!.Id)!.Following); // zakładający obserwuje

        // obie osoby w wątku; wiadomości trafiają tylko do wątku
        var (ta, st, e1) = await a.OpenThread(t!.Id);
        Assert.Null(e1);
        Assert.Equal(starter.Id, st!.Id);
        var (_, _, e2) = await b.OpenThread(t.Id);
        Assert.Null(e2);
        var text = $"w wątku {tag} <@{b.Store.Me.Id}>";
        Assert.Null(await a.Send(-t.Id, text));
        await p.Until(() => b.Store.Window(-t.Id).Items.Any(m => m.Content == text), "odpowiedź w wątku u B");
        lock (p.Lock)
        {
            Assert.Single(a.Store.Window(-t.Id).Items, m => m.Content == text);
            Assert.DoesNotContain(a.Store.Window(chan.Id).Items, m => m.Content == text);
            Assert.DoesNotContain(b.Store.Window(chan.Id).Items, m => m.Content == text);
        }
        await p.Until(() => b.Store.ThreadById(t.Id)?.Following == true, "wzmianka = obserwowanie u B");
        await p.Until(() => a.Store.ThreadById(t.Id)?.Count >= 1, "licznik odpowiedzi");
        // „pisze…” w wątku
        a.Typing(-t.Id);
        await p.Until(() => b.Store.TypingIn(-t.Id).Contains(a.Store.Me.Id), "„pisze…” w wątku u B");
        // lista wątków kanału
        var (list, _, e3) = await b.ListThreads(chan.Id);
        Assert.Null(e3);
        Assert.Contains(list, x => x.Id == t.Id);

        // wyszukiwarka: tekst z wątku, z filtrem autora (od:@tester), na kanale
        var q = a.ParseSearch($"{tag} od:@tester") with { ChannelId = chan.Id };
        Assert.NotNull(q.AuthorId);
        var (r, e4) = await b.Search(q);
        Assert.Null(e4);
        Assert.True(r!.Total >= 2);
        Assert.Contains(r.Messages, m => m.ThreadId == t.Id && m.Content == text);
        var (r2, _) = await b.Search(new SearchQuery(tag, Has: "pinned", ServerId: chan.ServerId));
        Assert.Equal(0, r2!.Total);

        // przestaję obserwować, zmiana nazwy, usunięcie wątku
        Assert.Null(await b.FollowThread(t.Id, false));
        await p.Until(() => b.Store.ThreadById(t.Id)?.Following == false, "nie obserwuje");
        Assert.Null(await a.PatchThread(t.Id, new { name = "Nowa " + tag }));
        await p.Until(() => b.Store.ThreadById(t.Id)?.Name == "Nowa " + tag, "nowa nazwa u B");
        await a.Api.DeleteThread(t.Id);
        await p.Until(() => b.Store.ThreadById(t.Id) is null && b.Store.Window(chan.Id).Items.First(m => m.Id == starter.Id).Thread.ValueKind != System.Text.Json.JsonValueKind.Object, "usunięcie wątku u B");
        a.CloseThread(); b.CloseThread();
    }

    [LiveFact]
    public async Task RejestracjaBlokadaZgloszenie()
    {
        // nowe konto z aplikacji (token od razu), GET /api/me bez bramki na kopii testowej
        var c = p.New();
        var name = "nowy" + Guid.NewGuid().ToString("N")[..6];
        var r = await c.Register(name, LivePair.Pass, "Nowy Testowy", name + "@example.test");
        Assert.NotNull(r.Token);
        var (me, gate) = await c.Api.GetMe();
        Assert.Equal(name, me.Username);
        Assert.False(gate);
        await c.DisposeAsync();

        // blokada: stan u blokującego przychodzi przez block_update
        var (a, b) = (p.A, p.B);
        await a.Api.Block(b.Store.Me.Id);
        await p.Until(() => a.Store.Blocked.Contains(b.Store.Me.Id), "blokada u A");
        await a.Api.Unblock(b.Store.Me.Id);
        await p.Until(() => !a.Store.Blocked.Contains(b.Store.Me.Id), "odblokowanie u A");
        // znajomość wróci w teście Znajomi — blokada ją usuwa
        // zgłoszenie cudzej wiadomości
        var chan = a.Store.Servers.SelectMany(s => s.Channels).First(x => x.Name == "pogaduchy");
        var m = await b.Api.SendMessage(chan.Id, "do zgłoszenia " + name);
        await a.Api.Report(m.Id, "spam", "test z aplikacji");
        var ex = await Assert.ThrowsAsync<ApiException>(() => b.Api.Report(m.Id, "spam", null));
        Assert.Contains("wlasnej", GibCordClient.Fold(ex.Message));
    }

    [LiveFact]
    public async Task AnkietyIMiniGry()
    {
        var (a, b) = (p.A, p.B);
        var chan = a.Store.Servers.SelectMany(s => s.Channels).First(x => x.Name == "pogaduchy");
        // ankieta wielokrotnego wyboru: dwa głosy jednej osoby, potem zamknięcie
        var poll = await a.Api.SendPoll(chan.Id, "Co w sobotę?", ["kino", "gry", "spacer"], true, 24);
        var ids = poll.Poll.Prop("options").EnumerateArray().Select(o => o.Long("id")!.Value).ToList();
        await b.Api.VotePoll(poll.Id, [ids[0], ids[1]]);
        await a.Api.ClosePoll(poll.Id);
        var after = (await a.Api.GetMessages(chan.Id, around: poll.Id, limit: 5)).First(m => m.Id == poll.Id);
        Assert.True(after.Poll.Bool("closed"));
        Assert.Equal(2, after.Poll.Prop("options").EnumerateArray().Count(o => o.Prop("userIds").EnumerateArray().Any(x => x.GetInt64() == b.Store.Me.Id)));

        // kości i kółko i krzyżyk z ruchem
        var dice = await a.Api.CreateGame(chan.Id, new { kind = "dice", count = 2, sides = 6 });
        Assert.Equal(2, dice.Game.Prop("rolls").GetArrayLength());
        var ttt = await a.Api.CreateGame(chan.Id, new { kind = "ttt", opponentId = b.Store.Me.Id });
        var turn = ttt.Game.Long("turn")!.Value;
        var mover = turn == a.Store.Me.Id ? a : b;
        var moved = await mover.Api.GameMove(ttt.Id, new { cell = 4 });
        Assert.NotEqual('.', moved.Game.Str("board")![4]);
        var ex = await Assert.ThrowsAsync<ApiException>(() => mover.Api.GameMove(ttt.Id, new { cell = 0 }));
        Assert.False(string.IsNullOrEmpty(ex.Message));
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
