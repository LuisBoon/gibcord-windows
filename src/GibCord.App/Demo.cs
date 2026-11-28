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
    public const long MeId = 1, OlaId = 2, KubaId = 3, BonkusId = 4, MajaId = 5, BartekId = 6, DiscordId = 60;
    const long Eg = 10, Gc = 11, Sz = 12, Mc = 13;
    const long Ogolne = 100, Memy = 101, Granie = 102, Nauka = 103, Pogaduchy = 104, GranieV = 105, DmOla = 200;

    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, Json.Options);

    public static GibCordClient CreateClient(Action<Action> ui)
    {
        var c = new GibCordClient(new Uri("https://gibcord.pl"), new MemoryTokenStore("demo"), null, ui) { Offline = true };
        // 6.1: zima i podsumowanie roku w trybie pokazowym
        Winter.DemoOn = Options.View is "winter" or "wrapped" or "wrapped2" or "wrapped3" or "wrapcard" or "winterlogin";
        if (Options.View is "wrapped" or "wrapped2" or "wrapped3")
        {
            WrappedView.DemoData = DemoWrapped();
            WrappedView.DemoSlide = Options.View switch { "wrapped2" => 2, "wrapped3" => 9, _ => 0 };
        }
        c.Store.Load(State());
        var now = Format.NowMs();
        c.Store.SetWindow(Ogolne, Options.WheelTest ? [.. LongHistory(now), .. Messages(now)] : Messages(now), hasOlder: false);
        c.Store.SetWindow(DmOla, DmMessages(now), hasOlder: false);
        c.Store.SetWindow(Granie, BridgeMessages(now), hasOlder: false); // 6.4: most z Discordem
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
            case "voice":
                sh.C.DemoSpeaker = OlaId;
                sh.C.DemoRtt = [38, 41, 36, 52, 47, 39, 35, 44, 61, 48, 40, 37, 42, 39, 36, 45, 50, 41, 38, 34]; // wykres pingu w panelu rozmowy
                sh.JoinVoiceChannel(Eg, Pogaduchy);
                break;
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
            case "srvroles" or "srvroles2": sh.Go(Eg, Ogolne); sh.ShowServerSettings(Eg, "roles", selectRole: 1); break; // srvroles2: po „forest” — przełączniki w kolorze motywu Las
            case "srvmembers": sh.Go(Eg, Ogolne); sh.ShowServerSettings(Eg, "members"); break;
            case "setapp": sh.Go(Eg, Ogolne); sh.ShowSettings("app"); break;
            case "bots": sh.Go(Eg, Ogolne); sh.ShowSettings("bots"); break;
            case "setgames": // karta „Wykrywanie gier” z własną listą (bez zapisu ustawień)
                App.Prefs.MyGames["tibia.exe"] = "Tibia";
                if (!App.Prefs.NotGames.Contains("vlc.exe")) App.Prefs.NotGames.Add("vlc.exe");
                sh.Go(Eg, Ogolne); sh.ShowSettings("privacy");
                break;
            case "adminsys": sh.Go(Eg, Ogolne); sh.ShowAdmin("system"); break;
            case "adminstats": sh.Go(Eg, Ogolne); sh.ShowAdmin("stats"); break;
            case "adminact": sh.Go(Eg, Ogolne); sh.ShowAdmin("activity"); break;
            case "adminmail": sh.Go(Eg, Ogolne); sh.ShowAdmin("mail"); break;
            case "winter": sh.Go(Eg, Ogolne); break; // 6.1: śnieg + Gibek w czapce na pasku serwerów
            case "stage": // 6.3: scena — dwie osoby na scenie, ręka w górze, widownia
                if (sh.S.ChannelById(Pogaduchy) is { } stg) { stg.Kind = "stage"; stg.Name = "Quiz w piątek"; }
                sh.C.Store.Voice[Pogaduchy] = [new VoiceMember { UserId = MeId, Speaker = true }, new VoiceMember { UserId = OlaId, Speaker = true }, new VoiceMember { UserId = KubaId, Speaker = false, Suppressed = true, HandAt = Format.NowMs() - 30000 },
                    new VoiceMember { UserId = MajaId, Speaker = false, Suppressed = true }, new VoiceMember { UserId = BartekId, Speaker = false, Suppressed = true }];
                sh.C.DemoSpeaker = OlaId;
                sh.JoinVoiceChannel(Eg, Pogaduchy);
                break;
            case "board": // 6.3: tablica w rozmowie
                Ui.VoiceView.BoardOpenFor = Pogaduchy;
                sh.JoinVoiceChannel(Eg, Pogaduchy);
                break;
            case "kalambury" or "kalambury2" or "kalkoniec": // 6.2: Kalambury na tablicy
                sh.C.Store.Voice[Pogaduchy] = [new VoiceMember { UserId = MeId }, new VoiceMember { UserId = OlaId }, new VoiceMember { UserId = KubaId }, new VoiceMember { UserId = MajaId }];
                BoardPanel.DemoKal(Pogaduchy, Options.View, MeId, OlaId, KubaId, MajaId);
                Ui.VoiceView.BoardOpenFor = Pogaduchy;
                sh.C.DemoSpeaker = OlaId;
                sh.JoinVoiceChannel(Eg, Pogaduchy);
                break;
            case "vfx": // 6.2: zmieniacz głosu — ja jako robot, Ola jako Gibek
                sh.C.Store.Voice[Pogaduchy] = [new VoiceMember { UserId = OlaId, Fx = "gibek" }, new VoiceMember { UserId = KubaId }, new VoiceMember { UserId = MajaId, Fx = "jaskinia" }];
                sh.C.DemoSpeaker = OlaId;
                sh.JoinVoiceChannel(Eg, Pogaduchy);
                sh.C.SetFx("robot");
                break;
            case "radio": // 6.2: radio na kanale — pasek nad przyciskami i w panelu rozmowy
                sh.C.Store.Watches[Pogaduchy] = new WatchState { Mode = "radio", Session = "demo", Kind = "yt", VideoId = "jNQXAC9IVRw", Title = "Składanka do grania wieczorem", State = "playing", Position = 1040, UpdatedAt = Format.NowMs(), AddedBy = KubaId, By = KubaId, Key = "demo",
                    Queue = [new WatchItem { VideoId = "aqz-KE-bpKQ", Title = "Muzyka z gier na fortepianie", By = OlaId, Qid = "q1" }, new WatchItem { Kind = "file", Audio = true, Title = "nasz-hymn.mp3", By = MeId, Qid = "q2" }] };
                sh.C.DemoSpeaker = OlaId;
                sh.JoinVoiceChannel(Eg, Pogaduchy);
                break;
            case "wrapcard": sh.Go(Eg, Ogolne); break;
            case "wrapped" or "wrapped2" or "wrapped3": sh.Go(0, 0); WrappedView.Open(sh); break;
            case "forest":
                if (Theme.Name != "forest") sh.W.ApplyTheme("forest"); // przebudowa okna — nowe okno wróci tutaj i przejdzie na kanał
                else sh.Go(Eg, Ogolne);
                break;
            // ── 6.4 ──
            case "most": sh.Go(Eg, Granie); break; // kanał połączony z Discordem: wiadomości z Discorda + znaczek w nagłówku
            case "srvmost": // ustawienia serwera → Integracje → Most z Discordem (lista, kroki łączenia)
                ServerSettingsPage.DemoBridges = new BridgesInfo
                {
                    Enabled = true, Status = "ready", InviteUrl = "https://discord.com/oauth2/authorize",
                    Items = [new BridgeItem { Id = 1, ChannelId = Granie, Guild = "Ekipa z Discorda", Channel = "general", CreatedAt = Format.NowMs() - 3 * 86400000L }],
                };
                sh.Go(Eg, Ogolne); sh.ShowServerSettings(Eg, "integrations");
                break;
            case "e2ee" or "e2eekod": // szyfrowana rozmowa prywatna: kłódka na pasku rozmowy (e2eekod: okienko z kodem bezpieczeństwa)
                sh.C.DemoE2ee = Core.E2ee.E2eeStatus.Active;
                sh.C.Store.Voice[DmOla] = [new VoiceMember { UserId = OlaId }];
                sh.C.Store.VoiceE2ee.Add(DmOla);
                sh.C.DemoSpeaker = OlaId;
                sh.Go(0, DmOla);
                sh.StartCall(DmOla);
                break;
            case "setprofil": // ustawienia → Mój profil → wygląd profilu (ramka, efekt, kolory, wizytówka)
                SettingsPage.DemoShowcase = new ShowcaseOptions
                {
                    Games = [new ShowcaseGame { Name = "Minecraft", Hours = 38 }, new ShowcaseGame { Name = "Rocket League", Hours = 7 }],
                    Clips = [new ShowcaseClip { Mid = 1010, Name = "baza-timelapse.mp4", ChannelId = Ogolne, At = Format.NowMs() - 86400000 }],
                };
                SettingsPage.DemoScrollLook = true;
                sh.Go(Eg, Ogolne); sh.ShowSettings("profile");
                break;
            case "dmcall":
                sh.C.Store.Voice[DmOla] = [new VoiceMember { UserId = OlaId, Video = true }];
                sh.C.DemoSpeaker = OlaId;
                sh.Go(0, DmOla);
                sh.StartCall(DmOla);
                break;
            case "imgedit": sh.Go(Eg, Ogolne); sh.DemoImageEditor(); break;
            case "fields": sh.Go(Eg, Ogolne); FieldsDemo.Show(sh, ""); break;
            case "dropdown": sh.Go(Eg, Ogolne); FieldsDemo.Show(sh, "drop"); break;
            case "calendar": sh.Go(Eg, Ogolne); FieldsDemo.Show(sh, "date"); break;
            case "timepick": sh.Go(Eg, Ogolne); FieldsDemo.Show(sh, "time"); break;
            case "files": sh.Go(Eg, Ogolne); FieldsDemo.Files(sh); break;
            case "accounts": sh.Go(Eg, Ogolne); break; // kropka „3” przy panelu „ja” (konta w tle)
            case "later":
            {
                sh.C.DemoScheduled = true;
                var now = Format.NowMs();
                var tomorrow8 = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(8)).ToUnixTimeMilliseconds();
                sh.C.Store.LoadScheduled([
                    new ScheduledMessage { Id = 1, ChannelId = Ogolne, Content = "Wszystkiego najlepszego, Ola! 🎂🎉", SendAt = tomorrow8, CreatedAt = now },
                    new ScheduledMessage { Id = 2, ChannelId = Ogolne, Content = "Przypominam: o 20:00 turniej, nie spóźniać się", SendAt = tomorrow8 + 12 * 3600_000, CreatedAt = now },
                ]);
                sh.Go(Eg, Ogolne);
                break;
            }
            default: sh.Go(Eg, Ogolne); break;
        }
    }

    static User Usr(long id, string name, string color, string status, bool online = true, string? custom = null) =>
        new() { Id = id, Username = name.ToLowerInvariant(), DisplayName = name, Color = color, Status = status, Online = online, CustomStatus = custom, CreatedAt = 1763139200000 + id * 86400000 };

    static StateResponse State()
    {
        var me = Usr(MeId, "luisboon", "#ffb98f", "online");
        me.Staff = true; me.IsAdmin = true;
        // 6.4: profil jak z Nitro — ramki, kolory karty, efekt, wizytówka
        me.Decor = "sluchawki";
        me.ProfileTheme = ["#3fc1d9", "#1d3f8f"];
        me.ProfileFx = "babelki";
        me.Showcase = [new ShowcaseTile { K = "game", Name = "Minecraft", Hours = 38 }, new ShowcaseTile { K = "text", T = "robię GibCorda 🛠️" }];
        var ola = Usr(OlaId, "Ola", "#9fe3d5", "online");
        ola.Activity = new Activity { Type = "spotify", Title = "Dziś późno pójdę spać", Artists = J(new[] { "Kwiat Jabłoni" }), DurationMs = 208000, ProgressMs = 102000, IsPlaying = true, At = Format.NowMs() };
        ola.Bio = "rysuję, gram, śpię. w tej kolejności";
        ola.Pronouns = "ona/jej";
        ola.Decor = "zorza";
        ola.ProfileTheme = ["#ff9a62", "#8f3a8a"];
        ola.ProfileFx = "konfetti";
        ola.Showcase =
        [
            new ShowcaseTile { K = "game", Name = "Stardew Valley", Hours = 112 }, new ShowcaseTile { K = "spotify" },
            new ShowcaseTile { K = "clip", Mid = 1010, Name = "moja-wyspa.mp4", ChannelId = Ogolne, Width = 1280, Height = 720, Preview = "LZD8eGtR0}R-o}buayjZ0}Wp=xoJ" },
        ];
        var kuba = Usr(KubaId, "Kuba", "#c7b3ff", "online");
        kuba.Game = J(new { name = "Minecraft", since = Format.NowMs() - 25 * 60000 });
        kuba.Decor = "korona";
        var users = new Dictionary<long, User>
        {
            [MeId] = me, [OlaId] = ola, [KubaId] = kuba,
            [BonkusId] = Usr(BonkusId, "bonkus", "#ffd27a", "idle").With(u => u.Decor = "neon"),
            [DiscordId] = Usr(DiscordId, "Discord", "#5865f2", "offline", false).With(u => u.Bot = "webhook"), // 6.4: konto systemowe mostu
            [MajaId] = Usr(MajaId, "Maja", "#f4a3b8", "offline", false),
            [BartekId] = Usr(BartekId, "Bartek", "#a3c9f4", "offline", false),
        };
        Member Mem(long uid, params long[] roles) => new() { UserId = uid, RoleIds = [.. roles], JoinedAt = DateTimeOffset.UtcNow.AddDays(-30 - uid % 40).ToUnixTimeMilliseconds() };
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
                Ch(Memy, Eg, "memy", 1, 1, last: 2000), Ch(Granie, Eg, "granie", 1, 2, last: 6010, topic: "Wspólny kanał z serwerem na Discordzie").With(c => c.Bridge = new ChannelBridge { Guild = "Ekipa z Discorda", Channel = "general" }), Ch(Nauka, Eg, "nauka", 1, 3, last: 400),
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
            Reads = new() { [Ogolne] = 1007, [Memy] = 1990, [Granie] = 6010, [Nauka] = 400, [300] = 9, [301] = 5, [302] = 40, [DmOla] = 3007, [201] = 3000, [202] = 2500 },
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
            Msg(1005, Ogolne, MeId, "a tu mapa: https://gibcord.pl", yday - 3290000),
            Msg(1006, Ogolne, KubaId, "😂😂😂", yday - 3200000),
            Msg(1008, Ogolne, KubaId, "ktoś wbija dziś wieczorem na granie?", now - 13 * 60000),
            Msg(1009, Ogolne, OlaId, "ja po 20, muszę skończyć projekt na infę", now - 11 * 60000),
            Msg(1010, Ogolne, BonkusId, "zrzut z wczoraj, patrzcie jaka baza", now - 5 * 60000),
            Msg(1020, Ogolne, MeId, "nowy GibCord już się buduje 👀 <@2> <@3> sprawdźcie w sobotę", now - 60000),
        };
        list[8].Attachments = [new Attachment { Id = 1, Url = "https://gibcord.pl/icon-512.png", Name = "baza.png", Mime = "image/png", Width = 512, Height = 512, Size = 120000, Preview = "LZD8eGtR0}R-o}buayjZ0}Wp=xoJ" }];
        list[8].Reactions = [new Reaction { Emoji = "🔥", UserIds = [MeId, OlaId, KubaId, MajaId] }, new Reaction { Emoji = "😂", UserIds = [KubaId, BartekId] }];
        list[8].Thread = J(new { id = 1, name = "Sobotni turniej", count = 4, lastAt = now - 2 * 60000 });
        list[7].ReplyTo = new ReplyRef { Id = 1008, AuthorId = KubaId, Content = "ktoś wbija dziś wieczorem na granie?" };
        list[1].Mentions = [MajaId];
        list[9].Mentions = [OlaId, KubaId];
        if (Options.View == "wrapcard")
        {
            var w = Msg(1030, Ogolne, OlaId, "Mój GibCord 2026", now - 30000);
            w.Card = J(new
            {
                level = "info", kind = "wrapped", year = 2026, title = "Mój GibCord 2026", subtitle = "Nocny marek — Najlepsze rozmowy zaczynają się u Ciebie po północy.",
                badge = new { text = "Podsumowanie roku", level = "info" },
                stats = new object[]
                {
                    new { label = "Wiadomości", value = "1 284", note = "w 41 dni" }, new { label = "W głosie", value = "62 h" }, new { label = "Ulubiony kanał", value = "#ogólne", note = "Ekipa Gibka" },
                    new { label = "Emoji roku", value = "😂", note = "212 razy" }, new { label = "Gra roku", value = "Minecraft", note = "38 h" }, new { label = "Najlepsza godzina", value = "22:00" },
                },
                buttons = new[] { new { label = "Zobacz swoje", url = "https://gibcord.pl/podsumowanie" } },
            });
            list.Add(w);
        }
        return list;
    }

    /// <summary>6.4: przykładowe boty (zrzut CI --view bots).</summary>
    public static List<BotInfo> Bots() =>
    [
        new BotInfo { Id = 1, UserId = 50, Name = "Kostka", Username = "botk3x9a", Color = "#7fd6cc", Description = "Rzuca kośćmi i losuje, kto stawia pizzę.", Online = true,
            Servers = [new BotServer { Id = Eg, Name = "Ekipa Gibka" }], Commands = [new BotCommandName { Name = "kostka", Description = "Rzut kością" }, new BotCommandName { Name = "moneta", Description = "Orzeł czy reszka" }, new BotCommandName { Name = "wybierz", Description = "Wybiera jedno z podanych" }] },
        new BotInfo { Id = 2, UserId = 51, Name = "Strażnik serwera", Username = "bot7f21c", Color = "#ffb98f", Description = "Wysyła alarmy z panelu serwera.", Online = false },
    ];

    /// <summary>Przykładowe podsumowanie roku (zrzuty CI: --view wrapped / wrapped2 / wrapped3).</summary>
    static Wrapped DemoWrapped() => new()
    {
        Year = 2026, Since = "2026-11-14", Preview = false, User = new WUser { Id = MeId, Name = "Hubi" },
        Messages = new WMessages
        {
            Total = 1284, Chars = 61_200, Days = 41, TopHour = 22, Night = 214,
            Hours = [30, 22, 12, 5, 1, 0, 0, 2, 8, 14, 20, 26, 31, 28, 35, 44, 52, 60, 71, 88, 104, 121, 139, 96],
            BusiestDay = new WDay { Date = "2026-12-06", Count = 143 },
            TopChannels = [new WChannel { Id = Ogolne, Label = "#ogólne", ServerId = Eg, Server = "Ekipa Gibka", Count = 612 }, new WChannel { Id = Granie, Label = "#granie", ServerId = Eg, Server = "Ekipa Gibka", Count = 301 }, new WChannel { Id = 0, Label = "#pomysły", Server = "GibCord — społeczność", Count = 44 }],
            MiniGames = 17,
        },
        Emoji = [new WEmoji { Emoji = "😂", Count = 212 }, new WEmoji { Emoji = "🔥", Count = 96 }, new WEmoji { Emoji = "❤️", Count = 71 }, new WEmoji { Emoji = "👀", Count = 40 }, new WEmoji { Emoji = "💀", Count = 33 }],
        Reactions = new WReactions { Given = 402, Received = 515 },
        Media = new WMedia { Photos = 88, Videos = 6, Gifs = 140, Stickers = 23, VoiceNotes = 12, Polls = 4, Files = 104 },
        Voice = new WVoice { Minutes = 3720, StreamMinutes = 410, TopPeople = [new WPerson { UserId = KubaId, Minutes = 1500 }, new WPerson { UserId = OlaId, Minutes = 1210 }], TopChannel = new WChannel { Id = Pogaduchy, Label = "🔊 Pogaduchy", Server = "Ekipa Gibka", Minutes = 2900 } },
        Games = [new WGame { Name = "Minecraft", Minutes = 2280 }, new WGame { Name = "Counter-Strike 2", Minutes = 1310 }, new WGame { Name = "Rocket League", Minutes = 420 }],
        Servers = new WCount { Count = 4, Joined = 4 }, Friends = new WFriends { Count = 9, New = 9 }, Level = new WLevel { Level = 14, Server = "Ekipa Gibka" },
        Everyone = new WEveryone { Messages = 9810, VoiceMinutes = 18_400, Writers = 12, People = 14, Rank = 2 },
        Persona = new WPersona { Key = "night", Title = "Nocny marek", Text = "Najlepsze rozmowy zaczynają się u Ciebie po północy." },
        TopPeople = [new WPerson { UserId = OlaId, Count = 388 }, new WPerson { UserId = KubaId, Count = 251 }, new WPerson { UserId = MajaId, Count = 120 }],
    };

    /// <summary>CI (--wheel-test): długa historia o różnej wysokości wiadomości (krótkie, długie, kod, zdjęcia) — jak prawdziwy pełny kanał.</summary>
    static IEnumerable<Message> LongHistory(long now)
    {
        long[] who = [OlaId, KubaId, BonkusId, MajaId, BartekId, MeId];
        string[] texts =
        [
            "hej", "ktoś gra dziś?", "ja po 20 będę", "😂😂", "a widzieliście nowy odcinek? spoilerów nie będzie, ale końcówka była mocna i nie spodziewałem się, że tak to się skończy",
            "ok", "kod:\n```\nmc.ekipa-gibka.pl:25565\n```", "jutro sprawdzian z matmy, ktoś ma zadania z działu o funkcjach kwadratowych?\nbo ja nie ogarniam\nw ogóle", "+1", "zaraz wracam",
        ];
        var start = now - 2 * 86400000L;
        for (var i = 0; i < 160; i++)
        {
            var m = Msg(5000 + i, Ogolne, who[(i * 7 + i / 3) % who.Length], texts[(i * 3 + i / 5) % texts.Length], start + i * 400000L);
            if (i % 23 == 11) m.Attachments = [new Attachment { Id = 100 + i, Url = "https://gibcord.pl/icon-512.png", Name = "zrzut.png", Mime = "image/png", Width = 640, Height = 360, Size = 90000, Preview = "LZD8eGtR0}R-o}buayjZ0}Wp=xoJ" }];
            if (i % 17 == 5) m.Reactions = [new Reaction { Emoji = "🔥", UserIds = [OlaId, KubaId] }];
            yield return m;
        }
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

    /// <summary>6.4: kanał z mostem — wiadomości z Discorda (autor: konto „Discord”, nazwa i awatar w ext) przeplatane z GibCordem.</summary>
    static List<Message> BridgeMessages(long now)
    {
        Message Dc(long id, string name, string content, long at) { var m = Msg(id, Granie, DiscordId, content, at); m.Ext = new MessageExt { Src = "discord", Name = name }; return m; }
        return
        [
            Msg(6000, Granie, DiscordId, "Most z Discordem połączony: #granie ⇄ #general (Ekipa z Discorda).", now - 50 * 60000),
            Dc(6001, "Kasia_PL", "siema z discorda! 👋 widzicie mnie?", now - 20 * 60000),
            Dc(6002, "Kasia_PL", "bo u nas na #general wszystko przychodzi", now - 20 * 60000 + 8000),
            Msg(6003, Granie, OlaId, "widzimy! i to z Twoim nickiem 😄", now - 19 * 60000),
            Dc(6004, "Tomek", "to gramy dziś w rocket league? ja mogę od 19", now - 12 * 60000),
            Dc(6005, "Kasia_PL", "ja też 🙋‍♀️", now - 11 * 60000).With(m => m.Reactions = [new Reaction { Emoji = "👍", UserIds = [OlaId, KubaId] }]),
            Msg(6006, Granie, KubaId, "wbijam, zakładam pokój o 19:05", now - 9 * 60000).With(m => m.ReplyTo = new ReplyRef { Id = 6004, AuthorId = DiscordId, Content = "to gramy dziś w rocket league? ja mogę od 19" }),
            Msg(6010, Granie, MeId, "super, w końcu obie ekipy w jednym miejscu 🎉", now - 2 * 60000),
        ];
    }

    static List<Message> DmMessages(long now) =>
    [
        Msg(3001, DmOla, OlaId, "hej! widziałeś nowego GibCorda?", now - 40 * 60000),
        Msg(3002, DmOla, MeId, "no pewnie, sam go robię 😎", now - 39 * 60000),
        Msg(3003, DmOla, OlaId, "to jest szybsze niż discord serio", now - 38 * 60000),
        Msg(3004, DmOla, OlaId, "i ładniejsze", now - 38 * 60000 + 5000),
        Msg(3005, DmOla, MeId, "dzięki ❤️ jutro dorzucę głos", now - 20 * 60000),
        Msg(3006, DmOla, OlaId, "", now - 3 * 60000).With(m => m.Type = "game").With(m => m.Game = J(new { kind = "ttt", players = new[] { OlaId, MeId }, board = "X.O.X....", turn = MeId })),
        Msg(3007, DmOla, MeId, "", now - 60000).With(m => m.Type = "game").With(m => m.Game = J(new { kind = "dice", count = 2, sides = 6, rolls = new[] { 4, 6 }, total = 10 })),
    ];
}
