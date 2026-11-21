using System.Text.Json;
using GibCord.Core;

namespace GibCord.Core.Tests;

public class StoreTests
{
    static GatewayEvent Ev(string t, object data) =>
        new(t, JsonSerializer.SerializeToElement(data, Json.Options));

    static Store Make()
    {
        var st = new StateResponse
        {
            Me = new User { Id = 1, Username = "ja", DisplayName = "Ja", Status = "online", Online = true },
            Users = new()
            {
                [2] = new User { Id = 2, Username = "ola", DisplayName = "Ola", Online = true, Status = "idle" },
                [3] = new User { Id = 3, Username = "kuba", Online = false, Status = "online" },
            },
            Servers =
            [
                new Server
                {
                    Id = 100, Name = "Ekipa", OwnerId = 2,
                    Roles = [new Role { Id = 7, Name = "Mod", Color = "#ff0000", Position = 2 }],
                    Categories = [new Category { Id = 50, Name = "Głosowe", Position = 1 }, new Category { Id = 51, Name = "Tekstowe", Position = 0 }],
                    Channels =
                    [
                        new Channel { Id = 10, ServerId = 100, Name = "ogólny", Position = 0, CategoryId = 51, LastMessageId = 5 },
                        new Channel { Id = 11, ServerId = 100, Name = "Pogaduchy", Type = "voice", Position = 1, CategoryId = 50 },
                        new Channel { Id = 12, ServerId = 100, Name = "memy", Position = 2, CategoryId = 51 },
                        new Channel { Id = 13, ServerId = 100, Name = "zasady", Position = 0 },
                    ],
                    Members = [new Member { UserId = 1, RoleIds = [7], Nick = "Szef" }, new Member { UserId = 2 }],
                    MyPerms = new MyPerms { Server = Perm.ViewChannel | Perm.SendMessages, Channels = new() { [10] = Perm.ViewChannel | Perm.SendMessages, [12] = Perm.ViewChannel } },
                },
            ],
            Dms = [new Dm { Id = 20, UserIds = [1, 2], LastMessageId = 9 }],
            Reads = new() { [10] = 5, [20] = 3 },
        };
        var s = new Store();
        s.Load(st);
        return s;
    }

    static Message Msg(long id, long cid, long author, string content = "hej", string? nonce = null) =>
        new() { Id = id, ChannelId = cid, AuthorId = author, Content = content, CreatedAt = Format.NowMs(), Nonce = nonce };

    [Fact]
    public void NieprzeczytaneIWzmianki()
    {
        var s = Make();
        Assert.False(s.IsUnread(10));
        Assert.True(s.IsUnread(20)); // DM: last 9 > read 3
        var m = Msg(6, 10, 2, "hej <@1>"); m.Mentions = [1];
        s.Apply(Ev("message_create", new { message = m }));
        Assert.True(s.IsUnread(10));
        Assert.Equal(1, s.MentionsIn(10));
        Assert.Equal(1, s.ServerMentions(s.ServerById(100)!));
        s.Apply(Ev("read_state", new { channelId = 10, messageId = 6 }));
        Assert.False(s.IsUnread(10));
        Assert.Equal(0, s.MentionsIn(10));
    }

    [Fact]
    public void WlasnaWiadomoscJestPrzeczytana()
    {
        var s = Make();
        s.Apply(Ev("message_create", new { message = Msg(6, 10, 1) }));
        Assert.False(s.IsUnread(10));
    }

    [Fact]
    public void OczekujacaPodmienianaPoNonce()
    {
        var s = Make();
        s.SetWindow(10, [Msg(4, 10, 2), Msg(5, 10, 2)], hasOlder: false);
        var local = Msg(-1, 10, 1, "wysyłam", "abc");
        s.AddPending(local);
        Assert.Equal(SendState.Pending, s.Window(10).Items[^1].Send);
        // ktoś inny pisze w międzyczasie — ląduje przed oczekującą
        s.Apply(Ev("message_create", new { message = Msg(6, 10, 2, "obok") }));
        Assert.Equal(6, s.Window(10).Items[^2].Id);
        s.Apply(Ev("message_create", new { message = Msg(7, 10, 1, "wysyłam", "abc") }));
        var items = s.Window(10).Items;
        Assert.Equal([4L, 5, 6, 7], items.Select(x => x.Id));
        Assert.All(items, x => Assert.Equal(SendState.Sent, x.Send));
    }

    [Fact]
    public void DuplikatNieDublujeSie()
    {
        var s = Make();
        s.SetWindow(10, [Msg(5, 10, 2)], hasOlder: false);
        s.Apply(Ev("message_create", new { message = Msg(6, 10, 2) }));
        s.Upsert(Msg(6, 10, 2, "nowa treść"), live: false);
        Assert.Equal(2, s.Window(10).Items.Count);
        Assert.Equal("nowa treść", s.Window(10).Items[^1].Content);
    }

    [Fact]
    public void EdycjaReakcjaUsuniecie()
    {
        var s = Make();
        s.SetWindow(10, [Msg(5, 10, 2)], hasOlder: false);
        var edited = Msg(5, 10, 2, "zmienione"); edited.EditedAt = 1;
        s.Apply(Ev("message_update", new { message = edited }));
        Assert.Equal("zmienione", s.Window(10).Items[0].Content);
        s.Apply(Ev("reaction_update", new { messageId = 5, channelId = 10, reactions = new[] { new { emoji = "👍", userIds = new[] { 1, 2 } } } }));
        Assert.Equal(2, s.Window(10).Items[0].Reactions[0].UserIds.Count);
        s.Apply(Ev("message_delete", new { id = 5, channelId = 10 }));
        Assert.Empty(s.Window(10).Items);
    }

    [Fact]
    public void DrzewoKanalow()
    {
        var s = Make();
        var tree = s.ChannelTree(s.ServerById(100)!);
        Assert.Null(tree[0].Category);               // bez kategorii pierwsze
        Assert.Equal("zasady", tree[0].Channels[0].Name);
        Assert.Equal("Tekstowe", tree[1].Category!.Name); // kategorie wg pozycji
        Assert.Equal(["ogólny", "memy"], tree[1].Channels.Select(c => c.Name));
        Assert.Equal("Głosowe", tree[2].Category!.Name);
        Assert.Equal("zasady", s.FirstTextChannel(s.ServerById(100)!)!.Name);
    }

    [Fact]
    public void NazwyKoloryUprawnienia()
    {
        var s = Make();
        var srv = s.ServerById(100)!;
        Assert.Equal("Szef", s.NameOf(1, srv));
        Assert.Equal("Ja", s.NameOf(1));
        Assert.Equal("#ff0000", s.NameColor(1, srv));
        Assert.True(s.Can(10, Perm.SendMessages));
        Assert.False(s.Can(12, Perm.SendMessages));
        Assert.True(s.Can(20, Perm.SendMessages)); // DM
        Assert.Equal("idle", s.StatusOf(2));
        Assert.Equal("offline", s.StatusOf(3));
        Assert.Equal("Ola", s.DmTitle(s.DmById(20)!));
    }

    [Fact]
    public void Powiadomienia()
    {
        var s = Make();
        // DM: domyślnie wszystkie
        Assert.True(s.ShouldNotify(Msg(10, 20, 2), activeChannelId: 0, windowActive: true));
        // ten sam kanał na wierzchu — nie
        Assert.False(s.ShouldNotify(Msg(10, 20, 2), activeChannelId: 20, windowActive: true));
        // ten sam kanał, ale okno schowane — tak
        Assert.True(s.ShouldNotify(Msg(10, 20, 2), activeChannelId: 20, windowActive: false));
        // serwer: domyślnie tylko wzmianki
        Assert.False(s.ShouldNotify(Msg(6, 10, 2), 0, true));
        var men = Msg(6, 10, 2, "<@1>"); men.Mentions = [1];
        Assert.True(s.ShouldNotify(men, 0, true));
        var role = Msg(6, 10, 2, "<@&7>"); role.MentionRoles = [7];
        Assert.True(s.ShouldNotify(role, 0, true));
        // własna — nie
        Assert.False(s.ShouldNotify(Msg(6, 10, 1), 0, true));
        // wyciszony kanał — nie
        s.Apply(Ev("notify_update", new { scope = "channel", id = 10, level = (string?)null, mutedUntil = Format.NowMs() + 60000 }));
        Assert.False(s.ShouldNotify(men, 0, true));
        // nie przeszkadzać — nic
        s.Apply(Ev("user_update", new { user = new User { Id = 1, Username = "ja", Status = "dnd", Online = true } }));
        Assert.False(s.ShouldNotify(Msg(10, 20, 2), 0, true));
    }

    [Fact]
    public void ObecnoscZnajomiRozmowy()
    {
        var s = Make();
        s.Apply(Ev("presence", new { userId = 3, online = true, status = "dnd", customStatus = "gram" }));
        Assert.Equal("dnd", s.StatusOf(3));
        s.Apply(Ev("friend_update", new { friend = new Friend { UserId = 4, Status = "incoming" }, user = new User { Id = 4, Username = "nowy" } }));
        Assert.Equal("incoming", s.Friends[4].Status);
        Assert.Equal("nowy", s.U(4)!.Name);
        s.Apply(Ev("friend_remove", new { userId = 4 }));
        Assert.False(s.Friends.ContainsKey(4));
        s.Apply(Ev("dm_create", new { dm = new Dm { Id = 21, Type = "group", UserIds = [1, 2, 3], LastMessageId = 30 }, users = new Dictionary<string, User> { ["3"] = new() { Id = 3, Username = "kuba" } } }));
        Assert.Equal(21, s.DmsSorted()[0].Id);
        Assert.Equal("Ola, kuba", s.DmTitle(s.DmById(21)!));
    }

    [Fact]
    public void KanalyNaZywo()
    {
        var s = Make();
        s.Apply(Ev("channel_create", new { channel = new Channel { Id = 14, ServerId = 100, Name = "nowy" } }));
        Assert.Equal(100, s.ServerOfChannel(14)!.Id);
        s.Apply(Ev("channel_delete", new { channelId = 14, serverId = 100 }));
        Assert.Null(s.ChannelById(14));
        s.Apply(Ev("perms_update", new { serverId = 100, myPerms = new MyPerms { Server = Perm.All, Channels = new() { [12] = Perm.All } } }));
        Assert.True(s.Can(12, Perm.SendMessages));
        s.Apply(Ev("server_delete", new { serverId = 100 }));
        Assert.Empty(s.Servers);
        Assert.Null(s.ChannelById(10));
    }

    [Fact]
    public void WatkiNieTrafiajaNaKanal()
    {
        var s = Make();
        s.SetWindow(10, [Msg(5, 10, 2)], hasOlder: false);
        var t = Msg(6, 10, 2); t.ThreadId = 3;
        s.Apply(Ev("message_create", new { message = t }));
        Assert.Single(s.Window(10).Items);
    }

    [Fact]
    public void WatkiOsobneOknoIZnaczek()
    {
        var s = Make();
        s.SetWindow(10, [Msg(5, 10, 2, "zaczynam")], hasOlder: false);
        // wątek przy wiadomości 5: znaczek pod wiadomością na kanale
        s.Apply(Ev("thread_update", new { thread = new ThreadInfo { Id = 3, ChannelId = 10, ServerId = 100, MessageId = 5, Name = "Plan", Count = 0 } }));
        Assert.Equal("Plan", s.Window(10).Items[0].Thread.Str("name"));
        // wiadomość z wątku nie trafia na kanał; okno wątku dopiero po otwarciu
        var t1 = Msg(6, 10, 2, "w wątku"); t1.ThreadId = 3;
        s.Apply(Ev("message_create", new { message = t1 }));
        Assert.Single(s.Window(10).Items);
        Assert.False(s.Windows.ContainsKey(-3));
        Assert.Equal(6, s.ThreadById(3)!.LastMessageId);
        s.SetWindow(-3, [t1, Msg(5, 10, 2)], hasOlder: false); // tylko wiadomości tego wątku
        Assert.Single(s.Window(-3).Items);
        var t2 = Msg(7, 10, 2, "dalej"); t2.ThreadId = 3;
        s.Apply(Ev("message_create", new { message = t2 }));
        Assert.Equal(2, s.Window(-3).Items.Count);
        Assert.Equal(5, s.LastIdOf(10)); // wątek nie przesuwa „ostatniej” kanału
        // „pisze…” w wątku osobno
        s.Apply(Ev("typing", new { channelId = 10, userId = 2, threadId = 3 }));
        Assert.Contains(2L, s.TypingIn(-3));
        Assert.Empty(s.TypingIn(10));
        // obserwowanie i nieprzeczytane
        s.Apply(Ev("thread_follow", new { threadId = 3, channelId = 10, following = true, lastRead = 6 }));
        Assert.True(s.ThreadById(3)!.Unread);
        Assert.Equal(1, s.UnreadThreadsIn(10));
        // thread_update bez „following” nie kasuje obserwowania
        s.Apply(Ev("thread_update", new { thread = new ThreadInfo { Id = 3, ChannelId = 10, ServerId = 100, MessageId = 5, Name = "Plan B", Count = 2, LastMessageId = 7 } }));
        Assert.True(s.ThreadById(3)!.Following);
        s.MarkThreadReadLocal(3, 7);
        Assert.False(s.ThreadById(3)!.Unread);
        // edycja, reakcja i usunięcie w wątku
        var ed = Msg(7, 10, 2, "poprawione"); ed.ThreadId = 3;
        s.Apply(Ev("message_update", new { message = ed }));
        Assert.Equal("poprawione", s.Window(-3).Items[^1].Content);
        s.Apply(Ev("reaction_update", new { messageId = 7, channelId = 10, reactions = new[] { new { emoji = "👍", userIds = new[] { 1 } } } }));
        Assert.Single(s.Window(-3).Items[^1].Reactions);
        s.Apply(Ev("message_delete", new { id = 7, channelId = 10, threadId = 3 }));
        Assert.Single(s.Window(-3).Items);
        // usunięcie wątku: znika okno i znaczek
        s.Apply(Ev("thread_delete", new { threadId = 3, channelId = 10, messageId = 5 }));
        Assert.Null(s.ThreadById(3));
        Assert.False(s.Windows.ContainsKey(-3));
        Assert.Equal(JsonValueKind.Undefined, s.Window(10).Items[0].Thread.ValueKind);
    }

    [Fact]
    public void DoczytanieNowszychPoSkoku()
    {
        var s = Make();
        // okno ze środka historii (po skoku)
        s.SetWindow(10, [Msg(2, 10, 2), Msg(3, 10, 2)], hasOlder: true, atTail: false);
        var added = new List<long>();
        s.Changed += c => { if (c.Kind == ChangeKind.MessageAdded) added.Add(c.Id); };
        s.AppendNewer(10, [Msg(3, 10, 2), Msg(4, 10, 1), Msg(5, 10, 2)], atTail: true);
        Assert.Equal([2L, 3, 4, 5], s.Window(10).Items.Select(m => m.Id));
        Assert.Equal([4L, 5], added);
        Assert.True(s.Window(10).AtTail);
    }

    [Fact]
    public void BezOknaDlaNieotwartegoKanaluIUsuniecieWWatku()
    {
        var s = Make();
        // wiadomość na nieotwartym kanale: tylko „ostatnia wiadomość”, bez okna w pamięci
        s.Apply(Ev("message_create", new { message = Msg(30, 12, 2) }));
        Assert.False(s.Windows.ContainsKey(12));
        Assert.Equal(30, s.LastIdOf(12));
        // usunięcie z panelu admina przychodzi bez threadId — znika też z otwartego wątku
        s.Apply(Ev("thread_update", new { thread = new ThreadInfo { Id = 9, ChannelId = 10, ServerId = 100, MessageId = 5 } }));
        var t = Msg(40, 10, 2); t.ThreadId = 9;
        s.SetWindow(-9, [t], hasOlder: false);
        s.Apply(Ev("message_delete", new { id = 40, channelId = 10 }));
        Assert.Empty(s.Window(-9).Items);
    }

    [Fact]
    public void WiadomoscZCzasuPobieraniaZostaje()
    {
        var s = Make();
        // z dysku: 4, 5 i 7 (7 usunięto, gdy aplikacja była zamknięta)
        s.SetWindow(10, [Msg(4, 10, 2), Msg(5, 10, 2), Msg(7, 10, 2)], hasOlder: true, fromCache: true);
        s.BeginFetch(10); // pobieranie w toku
        s.Apply(Ev("message_create", new { message = Msg(8, 10, 2, "na żywo") }));
        // odpowiedź z serwera sprzed wiadomości 8 (bez usuniętej 7)
        s.SetWindow(10, [Msg(4, 10, 2), Msg(5, 10, 2)], hasOlder: false);
        Assert.Equal([4L, 5, 8], s.Window(10).Items.Select(x => x.Id).ToArray());
        // kolejne pobranie bez nowych zdarzeń — 8 jest już w odpowiedzi, nic się nie dubluje
        s.BeginFetch(10);
        s.SetWindow(10, [Msg(4, 10, 2), Msg(5, 10, 2), Msg(8, 10, 2)], hasOlder: false);
        Assert.Equal([4L, 5, 8], s.Window(10).Items.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void UsunietaPodczasRozlaczeniaNieWraca()
    {
        var s = Make();
        s.Window(10);
        s.Apply(Ev("message_create", new { message = Msg(8, 10, 2, "później usunięta") })); // na żywo, przed rozłączeniem
        s.BeginFetch(10); // ponowne połączenie
        s.SetWindow(10, [Msg(4, 10, 2), Msg(5, 10, 2)], hasOlder: false);
        Assert.Equal([4L, 5], s.Window(10).Items.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void WzmiankaZCzasuPobieraniaStanuZostaje()
    {
        var s = Make();
        var m = Msg(6, 10, 2, "hej <@1>"); m.Mentions = [1];
        s.Apply(Ev("message_create", new { message = m })); // przyszła w trakcie GET /state
        var st = new StateResponse { Me = s.Me, Users = new(s.Users), Servers = [.. s.Servers], Dms = [.. s.Dms], Reads = new() { [10] = 5 } };
        st.Servers[0].Channels.First(c => c.Id == 10).LastMessageId = 5; // zrzut sprzed wiadomości 6
        s.Load(st, keepMentions: new HashSet<long> { 10 });
        s.Apply(Ev("message_create", new { message = m })); // odtworzenie
        Assert.True(s.IsUnread(10));
        Assert.Equal(1, s.MentionsIn(10));
    }

    [Fact]
    public void TaSamaWzmiankaLiczonaRaz()
    {
        var s = Make();
        var m = Msg(6, 10, 2, "hej <@1>"); m.Mentions = [1];
        s.Apply(Ev("message_create", new { message = m }));
        s.Apply(Ev("message_create", new { message = m })); // odtworzone po wczytaniu stanu
        Assert.Equal(1, s.MentionsIn(10));
    }

    [Fact]
    public void ResetCzysciKonto()
    {
        var s = Make();
        s.Window(10);
        s.Reset();
        Assert.False(s.Loaded);
        Assert.Empty(s.Servers);
        Assert.Empty(s.Dms);
        Assert.Empty(s.Windows);
        Assert.Equal(0, s.Me.Id);
    }
}

public class LooseBoolTests
{
    [Xunit.Fact]
    public void OnlineZPaneluAdmina()
    {
        // panel admina: online = "now" | "bg" | null; inne miejsca: liczba albo true/false
        Xunit.Assert.True(GibCord.Core.Json.Deserialize<GibCord.Core.User>("{\"id\":1,\"online\":\"now\"}")!.Online);
        Xunit.Assert.True(GibCord.Core.Json.Deserialize<GibCord.Core.User>("{\"id\":1,\"online\":\"bg\"}")!.Online);
        Xunit.Assert.False(GibCord.Core.Json.Deserialize<GibCord.Core.User>("{\"id\":1,\"online\":null}")!.Online);
        Xunit.Assert.True(GibCord.Core.Json.Deserialize<GibCord.Core.User>("{\"id\":1,\"online\":2}")!.Online);
        Xunit.Assert.False(GibCord.Core.Json.Deserialize<GibCord.Core.User>("{\"id\":1,\"online\":false}")!.Online);
    }
}
