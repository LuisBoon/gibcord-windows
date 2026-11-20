using System.Text.Json;
using GibCord.App.Ui;
using GibCord.Core;

namespace GibCord.App;

/// <summary>
/// Tryb pokazowy (--demo): przykładowe osoby i rozmowy jak na makiecie 1, bez łączenia z serwerem.
/// Służy do zrzutów ekranu w CI (sprawdzenie wyglądu bez Windowsa pod ręką).
/// </summary>
public static class Demo
{
    public const long MeId = 1, OlaId = 2, KubaId = 3, BonkusId = 4, MajaId = 5, BartekId = 6;
    const long Eg = 10, Gc = 11, Sz = 12, Mc = 13;
    const long Ogolne = 100, Memy = 101, Granie = 102, Nauka = 103, Pogaduchy = 104, GranieV = 105, DmOla = 200;

    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, Json.Options);

    public static GibCordClient CreateClient(Action<Action> ui)
    {
        var c = new GibCordClient(new Uri("https://gibcord.luisboon.pl"), new MemoryTokenStore("demo"), null, ui) { Offline = true };
        c.Store.Load(State());
        var now = Format.NowMs();
        c.Store.SetWindow(Ogolne, Messages(now), hasOlder: false);
        c.Store.SetWindow(DmOla, DmMessages(now), hasOlder: false);
        c.Store.Mentions[Memy] = 3;
        // wątek „Sobotni turniej” pod zrzutem z bazy (widok --view thread)
        c.Store.MergeThread(new ThreadInfo { Id = 1, ChannelId = Ogolne, ServerId = Eg, MessageId = 1010, Name = "Sobotni turniej", CreatorId = BonkusId, Count = 4,
            LastAt = now - 2 * 60000, LastMessageId = 4004, LastAuthors = [OlaId, KubaId, BonkusId], Following = true, LastRead = 4002 });
        c.Store.SetWindow(-1, ThreadMessages(now), hasOlder: false);
        c.Store.Typing(Ogolne, KubaId);
        return c;
    }

    public static void Navigate(Shell sh)
    {
        switch (Options.View)
        {
            case "friends": sh.Go(0, 0); break;
            case "dm": sh.Go(0, DmOla); break;
            case "voice": sh.C.DemoSpeaker = OlaId; sh.JoinVoiceChannel(Eg, Pogaduchy); break;
            case "overlay": sh.C.DemoSpeaker = OlaId; sh.JoinVoiceChannel(Eg, Pogaduchy); break; // obraz nakładki zapisuje się obok zrzutu
            case "watch":
                sh.C.Store.Watches[Pogaduchy] = new WatchState { Kind = "yt", VideoId = "jNQXAC9IVRw", Title = "Me at the zoo", State = "playing", Position = 3, UpdatedAt = Format.NowMs(), AddedBy = OlaId, By = OlaId, Key = "demo",
                    Queue = [new WatchItem { VideoId = "aqz-KE-bpKQ", Title = "Big Buck Bunny", By = KubaId, Qid = "q1" }] };
                sh.C.DemoSpeaker = OlaId;
                sh.JoinVoiceChannel(Eg, Pogaduchy);
                break;
            case "thread": sh.Go(Eg, Ogolne); sh.OpenThread(1); break;
            case "search": sh.Go(Eg, Ogolne); sh.OpenSearch("sobot"); break;
            case "settings": sh.Go(Eg, Ogolne); sh.ShowSettings("profile"); break;
            case "compact": App.Prefs.CompactMessages = true; sh.Go(Eg, Ogolne); break;
            case "inbox":
                sh.Go(Eg, Ogolne);
                foreach (var m in sh.C.Store.Window(Ogolne).Items.Where(x => x.Mentions.Count > 0).Take(3)) Inbox.AddMention(m, sh.C.Store);
                sh.OpenInbox();
                break;
            case "srvsettings": sh.Go(Eg, Ogolne); sh.ShowServerSettings(Eg, "channels"); break;
            case "srvroles": sh.Go(Eg, Ogolne); sh.ShowServerSettings(Eg, "roles", selectRole: 1); break;
            case "srvmembers": sh.Go(Eg, Ogolne); sh.ShowServerSettings(Eg, "members"); break;
            case "setapp": sh.Go(Eg, Ogolne); sh.ShowSettings("app"); break;
            case "adminsys": sh.Go(Eg, Ogolne); sh.ShowAdmin("system"); break;
            case "forest":
                if (Theme.Name != "forest") sh.W.ApplyTheme("forest"); // przebudowa okna — nowe okno wróci tutaj i przejdzie na kanał
                else sh.Go(Eg, Ogolne);
                break;
            case "dmcall":
                sh.C.Store.Voice[DmOla] = [new VoiceMember { UserId = OlaId, Video = true }];
                sh.C.DemoSpeaker = OlaId;
                sh.Go(0, DmOla);
                sh.StartCall(DmOla);
                break;
            default: sh.Go(Eg, Ogolne); break;
        }
    }

    static User Usr(long id, string name, string color, string status, bool online = true, string? custom = null) =>
        new() { Id = id, Username = name.ToLowerInvariant(), DisplayName = name, Color = color, Status = status, Online = online, CustomStatus = custom, CreatedAt = 1763139200000 + id * 86400000 };

    static StateResponse State()
    {
        var me = Usr(MeId, "luisboon", "#ffb98f", "online");
        me.Staff = true; me.IsAdmin = true;
        var ola = Usr(OlaId, "Ola", "#9fe3d5", "online");
        ola.Activity = new Activity { Type = "spotify", Title = "Dziś późno pójdę spać", Artists = J(new[] { "Kwiat Jabłoni" }), DurationMs = 208000, ProgressMs = 102000, IsPlaying = true, At = Format.NowMs() };
        ola.Bio = "rysuję, gram, śpię. w tej kolejności";
        ola.Pronouns = "ona/jej";
        var kuba = Usr(KubaId, "Kuba", "#c7b3ff", "online");
        kuba.Game = J(new { name = "Minecraft", since = Format.NowMs() - 25 * 60000 });
        var users = new Dictionary<long, User>
        {
            [MeId] = me, [OlaId] = ola, [KubaId] = kuba,
            [BonkusId] = Usr(BonkusId, "bonkus", "#ffd27a", "idle"),
            [MajaId] = Usr(MajaId, "Maja", "#f4a3b8", "offline", false),
            [BartekId] = Usr(BartekId, "Bartek", "#a3c9f4", "offline", false),
        };
        Member Mem(long uid, params long[] roles) => new() { UserId = uid, RoleIds = [.. roles] };
        Channel Ch(long id, long sid, string name, long cat, int pos, string type = "text", long? last = null, string? topic = null) =>
            new() { Id = id, ServerId = sid, Name = name, CategoryId = cat, Position = pos, Type = type, LastMessageId = last, Topic = topic };
        var eg = new Server
        {
            Id = Eg, Name = "Ekipa Gibka", OwnerId = MeId,
            Roles = [new Role { Id = 1, Name = "Załoga", Color = "#ffb98f", Position = 2, Hoist = true }, new Role { Id = 2, Name = "Gracze", Color = "#7fd6cc", Position = 1 }],
            Categories = [new Category { Id = 1, Name = "Tekstowe", Position = 0 }, new Category { Id = 2, Name = "Głosowe", Position = 1 }],
            Channels =
            [
                Ch(Ogolne, Eg, "ogólne", 1, 0, last: 1020, topic: "Wszystko, co nie pasuje gdzie indziej"),
                Ch(Memy, Eg, "memy", 1, 1, last: 2000), Ch(Granie, Eg, "granie", 1, 2, last: 500), Ch(Nauka, Eg, "nauka", 1, 3, last: 400),
                Ch(Pogaduchy, Eg, "Pogaduchy", 2, 4, "voice"), Ch(GranieV, Eg, "Granie", 2, 5, "voice"),
            ],
            Members = [Mem(MeId, 1), Mem(OlaId, 1), Mem(KubaId, 2), Mem(BonkusId, 2), Mem(MajaId), Mem(BartekId)],
            MyPerms = new MyPerms { Server = Perm.All },
        };
        var gc = new Server { Id = Gc, Name = "GibCord — społeczność", Official = true, OwnerId = MeId, Channels = [Ch(300, Gc, "ogłoszenia", 0, 0, last: 9)], Members = [Mem(MeId), Mem(OlaId)], MyPerms = new MyPerms { Server = Perm.All } };
        var sz = new Server { Id = Sz, Name = "Szkoła", OwnerId = OlaId, Channels = [Ch(301, Sz, "ogólny", 0, 0, last: 5)], Members = [Mem(MeId), Mem(OlaId), Mem(MajaId)], MyPerms = new MyPerms { Server = Perm.All } };
        var mc = new Server { Id = Mc, Name = "Minecraft", OwnerId = KubaId, Channels = [Ch(302, Mc, "serwer", 0, 0, last: 50)], Members = [Mem(MeId), Mem(KubaId)], MyPerms = new MyPerms { Server = Perm.All } };
        return new StateResponse
        {
            Me = me, Users = users, Servers = [eg, gc, sz, mc],
            Dms = [new Dm { Id = DmOla, UserIds = [MeId, OlaId], LastMessageId = 3007 }, new Dm { Id = 201, UserIds = [MeId, KubaId], LastMessageId = 3100 }, new Dm { Id = 202, Type = "group", Name = "Sobota 🎮", UserIds = [MeId, OlaId, KubaId, BonkusId], LastMessageId = 2500 }],
            Reads = new() { [Ogolne] = 1007, [Memy] = 1990, [Granie] = 500, [Nauka] = 400, [300] = 9, [301] = 5, [302] = 40, [DmOla] = 3007, [201] = 3000, [202] = 2500 },
            Voice = new()
            {
                [Pogaduchy] = [new VoiceMember { UserId = MeId }, new VoiceMember { UserId = OlaId }, new VoiceMember { UserId = KubaId, Muted = true }],
                [GranieV] = [new VoiceMember { UserId = BonkusId, Streaming = true }],
            },
            Friends = [new Friend { UserId = OlaId }, new Friend { UserId = KubaId }, new Friend { UserId = BonkusId }, new Friend { UserId = MajaId, Status = "incoming" }],
        };
    }

    static Message Msg(long id, long cid, long author, string content, long at) => new() { Id = id, ChannelId = cid, AuthorId = author, Content = content, CreatedAt = at };

    static List<Message> Messages(long now)
    {
        var yday = now - 86400000;
        var list = new List<Message>
        {
            Msg(1001, Ogolne, MajaId, "**Kto ma notatki z biologii?** Jutro kartkówka 😭", yday - 3600000),
            Msg(1002, Ogolne, OlaId, "mam, zaraz wrzucę na #nauka. <@5> pamiętaj o *fotosyntezie*", yday - 3500000),
            Msg(1003, Ogolne, BartekId, "a ktoś wie, czy jutro jest wf? ||nie mam stroju||", yday - 3400000),
            Msg(1004, Ogolne, MeId, "kod do serwera MC:\n```\nmc.ekipa-gibka.pl:25565\n```", yday - 3300000),
            Msg(1005, Ogolne, MeId, "a tu mapa: https://gibcord.luisboon.pl", yday - 3290000),
            Msg(1006, Ogolne, KubaId, "😂😂😂", yday - 3200000),
            Msg(1008, Ogolne, KubaId, "ktoś wbija dziś wieczorem na granie?", now - 13 * 60000),
            Msg(1009, Ogolne, OlaId, "ja po 20, muszę skończyć projekt na infę", now - 11 * 60000),
            Msg(1010, Ogolne, BonkusId, "zrzut z wczoraj, patrzcie jaka baza", now - 5 * 60000),
            Msg(1020, Ogolne, MeId, "nowy GibCord już się buduje 👀 <@2> <@3> sprawdźcie w sobotę", now - 60000),
        };
        list[8].Attachments = [new Attachment { Id = 1, Url = "https://gibcord.luisboon.pl/icon-512.png", Name = "baza.png", Mime = "image/png", Width = 512, Height = 512, Size = 120000 }];
        list[8].Reactions = [new Reaction { Emoji = "🔥", UserIds = [MeId, OlaId, KubaId, MajaId] }, new Reaction { Emoji = "😂", UserIds = [KubaId, BartekId] }];
        list[8].Thread = J(new { id = 1, name = "Sobotni turniej", count = 4, lastAt = now - 2 * 60000 });
        list[7].ReplyTo = new ReplyRef { Id = 1008, AuthorId = KubaId, Content = "ktoś wbija dziś wieczorem na granie?" };
        list[1].Mentions = [MajaId];
        list[9].Mentions = [OlaId, KubaId];
        return list;
    }

    static List<Message> ThreadMessages(long now)
    {
        Message T(long id, long author, string content, long at) { var m = Msg(id, Ogolne, author, content, at); m.ThreadId = 1; return m; }
        return
        [
            T(4001, BonkusId, "robimy w sobotę turniej na tej mapie? 🏆", now - 4 * 60000),
            T(4002, OlaId, "ja się piszę! drużyny po 2?", now - 3 * 60000),
            T(4003, KubaId, "biorę <@2> do drużyny 😎", now - 2 * 60000 - 30000),
            T(4004, OlaId, "deal. start 18:00?", now - 2 * 60000),
        ];
    }

    static List<Message> DmMessages(long now) =>
    [
        Msg(3001, DmOla, OlaId, "hej! widziałeś nowego GibCorda?", now - 40 * 60000),
        Msg(3002, DmOla, MeId, "no pewnie, sam go robię 😎", now - 39 * 60000),
        Msg(3003, DmOla, OlaId, "to jest szybsze niż discord serio", now - 38 * 60000),
        Msg(3004, DmOla, OlaId, "i ładniejsze", now - 38 * 60000 + 5000),
        Msg(3005, DmOla, MeId, "dzięki ❤️ jutro dorzucę głos", now - 20 * 60000),
        Msg(3006, DmOla, OlaId, "", now - 3 * 60000).With(m => m.Game = J(new { kind = "ttt", players = new[] { OlaId, MeId }, board = "X.O.X....", turn = MeId })),
        Msg(3007, DmOla, MeId, "", now - 60000).With(m => m.Game = J(new { kind = "dice", count = 2, sides = 6, rolls = new[] { 4, 6 }, total = 10 })),
    ];
}
