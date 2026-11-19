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
}
