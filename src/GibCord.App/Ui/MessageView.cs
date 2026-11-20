using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>Jedna wiadomość na liście (z awatarem albo sklejona z poprzednią, z paskiem akcji po najechaniu).</summary>
public sealed partial class MessageView : Grid
{
    readonly ChatView _v;
    Row? _row;
    Border? _tools;
    readonly Grid _body = new();
    static readonly string[] Quick = ["👍", "❤️", "😂"];
    /// <summary>Pierwsza wiadomość nad odpowiedziami w panelu wątku — bez paska akcji i znaczka wątku.</summary>
    public bool IsStarter { get; init; }

    public MessageView(ChatView v)
    {
        _v = v;
        Background = Theme.Transparent;
        Children.Add(_body);
        PointerEntered += (_, _) => { if (!IsStarter) ShowTools(true); };
        PointerExited += (_, _) => { if (!IsStarter) ShowTools(false); };
        RightTapped += (_, e) => { if (!IsStarter && _row is not null && _row.M.Send == SendState.Sent && !_row.M.IsSystem) { Menu().ShowAt(this, e.GetPosition(this)); e.Handled = true; } };
    }

    Store S => _v.S;
    Server? Srv => _v.Srv;
    GibCordApi Api => _v.Sh.C.Api;

    public void Unbind() { _row = null; ShowTools(false); }

    public void Bind(Row row)
    {
        _row = row;
        var m = row.M;
        _body.Children.Clear();
        _body.RowDefinitions.Clear();
        Background = Theme.Transparent;
        var stack = new StackPanel();
        if (row.DaySep) stack.Children.Add(DaySeparator(m.CreatedAt));
        if (row.NewMarker) stack.Children.Add(NewSeparator());
        stack.Children.Add(m.Type == "automod" && m.Automod.ValueKind == System.Text.Json.JsonValueKind.Object ? AutomodCard(m) : m.IsSystem ? SystemLine(m) : S.Blocked.Contains(m.AuthorId) && !_v.Revealed.Contains(m.Id) ? BlockedLine(m, row) : Normal(m, row));
        _body.Children.Add(stack);
        Opacity = m.Send is SendState.Pending or SendState.Queued ? .55 : 1;
    }

    // ── separatory ──

    static FrameworkElement DaySeparator(long ms) => new Grid { Margin = U.Th(16, 14, 16, 6) }.Cols("*", "auto", "*").Add(
        U.HLine(.09).At(0, 0), U.T(Format.Day(ms), 11, U.Semi, Theme.Mu2).With(t => t.Margin = U.Th(10, 0)).At(0, 1), U.HLine(.09).At(0, 2));

    static FrameworkElement NewSeparator() => new Grid { Margin = U.Th(16, 6, 16, 2) }.Cols("*", "auto").Add(
        new Rectangle { Height = 1, Fill = Theme.Red, VerticalAlignment = VerticalAlignment.Center }.At(0, 0),
        U.Box(U.T("NOWE", 9.5, U.Bold, U.B("#ffffff")), Theme.Red, 4, U.Th(5, 1)).At(0, 1));

    // ── karta automoderacji: co zatrzymano, reguła, przyciski dla moderatorów ──

    static readonly Dictionary<string, string> AmRule = new()
    {
        ["spam"] = "Spam", ["dup"] = "Powtórki", ["mentions"] = "Masowe wzmianki", ["words"] = "Zakazane słowa", ["scam"] = "Oszustwo",
        ["newlink"] = "Linki od nowych osób", ["profanity"] = "Wulgaryzmy", ["caps"] = "Krzyk",
    };

    FrameworkElement AutomodCard(Message m)
    {
        var a = m.Automod;
        var open = a.Str("status") == "open";
        var reportId = a.Long("reportId") ?? 0;
        var author = a.Long("authorId") ?? 0;
        var srv = Srv;
        var head = U.H(8, U.Box(U.H(4, U.I("\uE83D", 11, Theme.OnAccent), U.T("Automoderacja", 10.5, U.Bold, Theme.OnAccent)), Theme.Teal, 6, U.Th(6, 2)),
            U.T(AmRule.GetValueOrDefault(a.Str("rule") ?? "", a.Str("rule") ?? ""), 13.5, U.Bold), U.T(Format.When(m.CreatedAt), 11, null, Theme.Mu2));
        var body = U.V(6, head,
            U.H(8, new Avatar(22).Set(S, Api, author, srv), U.T(S.NameOf(author, srv), 13, U.Bold), U.T("w #" + (a.Str("channelName") ?? ""), 12, null, Theme.Mu)),
            U.Box(U.T(string.IsNullOrWhiteSpace(a.Str("content")) ? "(bez tekstu)" : a.Str("content")!, 13, null, Theme.Fg, wrap: true), U.W(.05), 8, U.Th(10, 6)),
            a.Str("match") is { Length: > 0 } match ? U.T("Złapane: " + match, 11.5, null, Theme.Mu2) : null);
        if (open && srv is not null && _v.CanManage)
        {
            async void Act(string k)
            {
                try
                {
                    Task Resolve(string note) => Api.PatchJson($"reports/{reportId}", new { status = "resolved", handlerNote = note });
                    switch (k)
                    {
                        case "oops": await Api.PostJson($"reports/{reportId}/restore"); _v.Sh.Info("Wiadomość wróciła na kanał"); break;
                        case "mute": await Api.Timeout(srv.Id, author, 60); await Resolve("Wyciszono na godzinę"); _v.Sh.Info("Wyciszono na godzinę"); break;
                        case "kick":
                            if (!await M.Confirm(_v.Sh, "Wyrzucić?", "Osoba zniknie z serwera, ale może wrócić z zaproszeniem.", "Wyrzuć")) return;
                            await Api.Kick(srv.Id, author); await Resolve("Wyrzucono"); break;
                        case "ban":
                            if (!await M.Confirm(_v.Sh, "Zbanować?", "Osoba zniknie z serwera i nie wróci, dopóki nie zdejmiesz bana.", "Zbanuj")) return;
                            await Api.Ban(srv.Id, author, "Automoderacja: " + AmRule.GetValueOrDefault(a.Str("rule") ?? "", ""), 0); await Resolve("Zbanowano"); break;
                        default: await Resolve("Załatwione"); break;
                    }
                }
                catch (Exception ex) { _v.Sh.Info(GibCordClient.Friendly(ex), true); }
            }
            body.Add(U.H(6, U.Ghost("To pomyłka — przywróć", () => Act("oops"), 28), U.Ghost("Wycisz na 1 h", () => Act("mute"), 28),
                U.Ghost("Wyrzuć", () => Act("kick"), 28), U.Ghost("Zbanuj", () => Act("ban"), 28), U.Ghost("Załatwione", () => Act("done"), 28)));
        }
        else if (!open) body.Add(U.T("✓ " + (a.Str("handlerNote") ?? "Załatwione") + (a.Long("handledBy") is long hb and > 0 ? " · " + S.NameOf(hb, srv) : ""), 12, U.Semi, Theme.Green));
        var card = new Border { Child = body, Background = U.Alpha("#7fd6cc", .07), BorderBrush = U.Alpha("#7fd6cc", .35), BorderThickness = U.Th(1), CornerRadius = new CornerRadius(12), Padding = U.Th(14, 10), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, Opacity = open ? 1 : .7 };
        return new Grid { Padding = U.Th(16, 6, 16, 6) }.Cols("56", "*").Add(card.At(0, 1));
    }

    // ── wiadomość od zablokowanej osoby: schowana, „Pokaż” odsłania ──

    FrameworkElement BlockedLine(Message m, Row row)
    {
        var show = new HyperlinkButton { Content = U.T("Pokaż", 12.5, U.Bold, Theme.Accent), Padding = U.Th(4, 0) };
        show.Click += (_, _) => { _v.Revealed.Add(m.Id); Bind(row); };
        return new Grid { Padding = U.Th(16, 4, 16, 4) }.Cols("56", "*").Add(
            U.I("\uE8F8", 13, Theme.Mu2).With(i => i.HorizontalAlignment = HorizontalAlignment.Center).At(0, 0),
            U.H(4, U.T("Wiadomość od zablokowanej osoby", 12.5, null, Theme.Mu2), show).At(0, 1));
    }

    // ── wiadomość systemowa ──

    FrameworkElement SystemLine(Message m)
    {
        var who = S.NameOf(m.AuthorId, Srv);
        var (glyph, text) = m.Type switch
        {
            "join" => ("", $"{who} dołącza do serwera."),
            "pin" => ("", $"{who} przypina wiadomość."),
            "call" => ("", $"{who} rozpoczyna rozmowę."),
            "levelup" => ("", $"{who} wbija poziom {m.RefIdLong}! 🎉"),
            "event" => ("", $"Zaczyna się wydarzenie {m.Content}!"),
            "group_add" => ("", $"{who} dodaje do grupy {S.NameOf(m.RefIdLong ?? 0)}."),
            "group_leave" => ("", m.RefIdLong is long r && r != m.AuthorId ? $"{who} usuwa z grupy {S.NameOf(r)}." : $"{who} opuszcza grupę."),
            "group_rename" => ("", string.IsNullOrEmpty(m.Content) ? $"{who} zmienia nazwę grupy." : $"{who} zmienia nazwę grupy na {m.Content}."),
            "automod" => ("", "Automoderacja zatrzymała wiadomość."),
            "game" => ("", GameText(m)),
            _ => ("", string.IsNullOrEmpty(m.Content) ? "Wiadomość systemowa" : m.Content),
        };
        var g = new Grid { Padding = U.Th(16, 5, 16, 5) }.Cols("56", "*").Add(
            U.I(glyph, 15, m.Type == "levelup" ? Theme.Yellow : m.Type == "call" ? Theme.Green : Theme.Mu).With(i => i.HorizontalAlignment = HorizontalAlignment.Center).At(0, 0),
            U.H(8, U.T(text, 13.5, null, Theme.Mu, wrap: true), U.T(Format.When(m.CreatedAt), 11, null, Theme.Mu2)).At(0, 1));
        if (m.Type == "pin" && m.RefIdLong is long target) g.Tapped += async (_, _) => await _v.JumpTo(target);
        return g;
    }

    // ── zwykła wiadomość ──

    FrameworkElement Normal(Message m, Row row)
    {
        var grid = new Grid { Padding = U.Th(16, row.Grouped ? 1 : 8, 16, 2) }.Cols("56", "*");
        var mentioned = m.AuthorId != S.Me.Id && S.MentionsMe(m);
        if (mentioned) Background = Theme.MentionedRow;
        var body = new StackPanel { Spacing = 3 };
        var r = 0;
        if (m.ReplyTo is { } rep)
        {
            grid.Rows("auto", "auto");
            var who = U.T(S.NameOf(rep.AuthorId, Srv), 12, U.Bold, U.B(S.NameColor(rep.AuthorId, Srv)));
            var snip = U.T(string.IsNullOrWhiteSpace(rep.Content) ? "załącznik" : Markup.Plain(rep.Content, id => "@" + S.NameOf(id, Srv), S.RoleName), 12, null, Theme.Mu);
            var line = U.H(6, U.T("↱", 13, null, Theme.Mu2), new Avatar(16).Set(S, Api, rep.AuthorId, Srv), who, snip);
            line.Margin = U.Th(30, 0, 0, 3);
            line.Tapped += async (_, e) => { e.Handled = true; await _v.JumpTo(rep.Id); };
            grid.Add(line.At(0, 0, colSpan: 2));
            r = 1;
        }
        if (row.Grouped)
        {
            var t = U.T(Format.Time(m.CreatedAt), 10.5, null, Theme.Mu2);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Top;
            t.Margin = U.Th(0, 4, 0, 0);
            t.Opacity = 0;
            grid.PointerEntered += (_, _) => t.Opacity = 1;
            grid.PointerExited += (_, _) => t.Opacity = 0;
            grid.Add(t.At(r, 0));
        }
        else
        {
            var av = new Avatar(40).Set(S, Api, m.AuthorId, Srv);
            av.VerticalAlignment = VerticalAlignment.Top;
            av.Margin = U.Th(0, 2, 0, 0);
            av.Tapped += (_, e) => { e.Handled = true; _v.Sh.ShowProfile(m.AuthorId, av); };
            av.RightTapped += (_, e) => { e.Handled = true; UserMenu.Build(_v.Sh, m.AuthorId, Srv, _v, av).ShowAt(av, e.GetPosition(av)); };
            grid.Add(av.At(r, 0));
            var name = U.T(S.NameOf(m.AuthorId, Srv), 14.5, U.Bold, U.B(S.NameColor(m.AuthorId, Srv)));
            name.Tapped += (_, e) => { e.Handled = true; _v.Sh.ShowProfile(m.AuthorId, name); };
            name.RightTapped += (_, e) => { e.Handled = true; UserMenu.Build(_v.Sh, m.AuthorId, Srv, _v, name).ShowAt(name, e.GetPosition(name)); };
            var head = U.H(8, name);
            if (S.U(m.AuthorId) is { Staff: true }) head.Add(U.Box(U.T("GC", 9, U.Bold, Theme.OnAccent), U.B("#f2a93b"), 5, U.Th(4, 1)).With(b => b.Tip("Właściciel GibCorda")));
            if (S.RolesOf(Srv, m.AuthorId).FirstOrDefault(x => !string.IsNullOrEmpty(x.Icon) && !x.Icon!.StartsWith('/') && !x.Icon.StartsWith("http")) is { } ri)
                head.Add(U.T(ri.Icon!, 13).With(t => t.Tip(ri.Name)));
            if (S.U(m.AuthorId) is { BirthdayToday: true }) head.Add(U.T("🎂", 12).With(t => t.Tip("Dziś ma urodziny!")));
            if (m.PinnedAt is not null) head.Add(U.I("\uE718", 11, Theme.Mu2).With(i => i.Tip("Przypięta")));
            var time = U.T(Format.When(m.CreatedAt), 11, null, Theme.Mu2);
            time.Tip(Format.Full(m.CreatedAt));
            head.Add(time);
            body.Children.Add(head);
        }

        // treść
        var cardDone = false;
        if (m.HasCard) { body.Children.Add(ServerCard(m.Card)); cardDone = true; }
        else if (_v.Ch is { IsCardChannel: true } && Markup.ParseAnnouncement(m.Content) is { } ann) { body.Children.Add(AnnouncementCard(ann)); cardDone = true; }
        if (!cardDone && GifOnly(m.Content) is { } gif) body.Children.Add(GifImage(gif));
        else if (!cardDone && !string.IsNullOrEmpty(m.Content))
            body.Children.Add(RichText.Build(m.Content, new RichText.Ctx(S, Api, Srv, uid => _v.Sh.ShowProfile(uid, this)), m.EditedAt is not null));
        if (m.Sticker is { } st)
            body.Children.Add(new Image { Source = Images.Get(Api.Abs(st.Url), 320), Width = 160, Height = 160, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }.With(i => i.Tip(st.Name)));
        if (m.Poll.ValueKind == JsonValueKind.Object) body.Children.Add(Poll(m));
        if (m.Game.ValueKind == JsonValueKind.Object && m.Type == "default") body.Children.Add(Fun.GameView(_v, m));
        foreach (var a in m.Attachments) body.Children.Add(AttachmentView(a));
        foreach (var yt in Markup.YouTubeIds(m.Content)) body.Children.Add(YouTube(yt));
        foreach (var code in Links.InviteCodes(m.Content)) body.Children.Add(Links.InviteCard(_v.Sh, code));
        if (m.Reactions.Count > 0) body.Children.Add(Reactions(m));
        if (m.Thread.ValueKind == JsonValueKind.Object && !IsStarter && !_v.IsThread) body.Children.Add(ThreadChip(m.Thread));
        if (m.Send == SendState.Queued)
        {
            var cancel = new HyperlinkButton { Content = U.T("Anuluj", 12, U.Bold, Theme.Mu), Padding = U.Th(0) };
            cancel.Click += (_, _) => _v.Delete(m, ask: false);
            body.Children.Add(U.H(8, U.I("\uE895", 12, Theme.Yellow), U.T("Czeka na internet — wyśle się sama", 12, U.Semi, Theme.Yellow), cancel));
        }
        if (m.Send == SendState.Failed)
        {
            var retry = new HyperlinkButton { Content = U.T("Ponów", 12, U.Bold, Theme.Accent), Padding = U.Th(0) };
            retry.Click += (_, _) => _v.Retry(m);
            var del = new HyperlinkButton { Content = U.T("Usuń", 12, U.Bold, Theme.Mu), Padding = U.Th(0) };
            del.Click += (_, _) => _v.Delete(m, ask: false);
            body.Children.Add(U.H(10, U.I("", 13, Theme.Red), U.T("Nie wysłano.", 12, U.Semi, Theme.Red), retry, del));
        }
        grid.Add(body.At(r, 1));
        if (mentioned)
            return new Grid().Add(new Rectangle { Width = 3, Fill = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Left }, grid);
        return grid;
    }

    static string GameText(Message m)
    {
        var g = m.Game;
        return g.Str("kind") switch
        {
            "dice" => $"🎲 Rzut kośćmi: {string.Join(" + ", g.Prop("rolls").ValueKind == JsonValueKind.Array ? g.Prop("rolls").EnumerateArray().Select(x => x.ToString()) : [])} = {g.Long("total")}",
            "coin" => $"🪙 Rzut monetą: {g.Str("result")}",
            "pick" => $"🎯 Wylosowano: {g.Str("result")}",
            "ttt" => "❌⭕ Kółko i krzyżyk — zagraj na stronie GibCorda",
            "rps" => "✊✋✌️ Kamień, papier, nożyce — zagraj na stronie GibCorda",
            _ => "🎮 Mini-gra",
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^((?:https://(?:media\d*\.giphy\.com|i\.giphy\.com|media\.tenor\.com)|/uploads)/[^\s#]+)(?:#(\d+)x(\d+))?$")]
    private static partial System.Text.RegularExpressions.Regex GifRe();

    /// <summary>Wiadomość z samym GIF-em (jak na stronie: adres giphy/tenor albo nasz /uploads, opcjonalnie #SZERxWYS).</summary>
    static (string Url, int W, int H)? GifOnly(string? content)
    {
        var t = content?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        var m = GifRe().Match(t);
        if (m.Success)
        {
            var url = m.Groups[1].Value;
            if (url.StartsWith("/uploads/") && !System.Text.RegularExpressions.Regex.IsMatch(url, @"\.(gif|webp|png|jpe?g)(\?|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return null;
            if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return null;
            static int Dim(System.Text.RegularExpressions.Group g) => g.Success && int.TryParse(g.Value, out var v) && v is > 0 and <= 10000 ? v : 0; // złośliwe #99999999999x1 nie wywróci listy
            var gw = Dim(m.Groups[2]); var gh = Dim(m.Groups[3]);
            return (url, gw > 0 && gh > 0 ? gw : 0, gw > 0 && gh > 0 ? gh : 0);
        }
        if (t.Contains(' ') || t.Contains('\n') || !Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Scheme != "https") return null;
        return u.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? (t, 0, 0) : null;
    }

    FrameworkElement GifImage((string Url, int W, int H) g)
    {
        double w = g.W > 0 ? g.W : 300, h = g.H > 0 ? g.H : 220;
        var k = Math.Min(1, Math.Min(320 / w, 300 / h));
        w = Math.Round(w * k); h = Math.Round(h * k);
        var url = Api.Abs(g.Url);
        var img = new Image { Source = Images.Get(url), Stretch = Stretch.UniformToFill };
        var b = new Border { CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, Margin = U.Th(0, 2, 0, 2), Background = U.W(.05), Child = img };
        if (g.W > 0 && g.H > 0) { b.Width = w; b.Height = h; } else { b.MaxWidth = 320; b.MaxHeight = 300; img.Stretch = Stretch.Uniform; }
        if (url is not null) b.Tapped += (_, e) => { e.Handled = true; Lightbox.Show(_v.Sh, url, "GIF"); };
        return b;
    }

    FrameworkElement AttachmentView(Attachment a)
    {
        var url = Api.Abs(a.Url);
        if (a.IsImage && url is not null)
        {
            double w = a.Width ?? 320, h = a.Height ?? 220;
            var k = Math.Min(1, Math.Min(400 / w, 320 / h));
            w = Math.Max(40, Math.Round(w * k)); h = Math.Max(40, Math.Round(h * k));
            var img = new Image { Source = Images.Get(url, (int)Math.Min(800, w * 2)), Width = w, Height = h, Stretch = Stretch.UniformToFill };
            var b = new Border { CornerRadius = new CornerRadius(10), Width = w, Height = h, Child = img, HorizontalAlignment = HorizontalAlignment.Left, Margin = U.Th(0, 3, 0, 2), Background = U.W(.05) };
            b.Tapped += (_, e) => { e.Handled = true; Lightbox.Show(_v.Sh, url, a.Name); };
            b.Tip(a.Name);
            return b;
        }
        if (url is not null && a.Mime.StartsWith("video/") && a.Mime is "video/mp4" or "video/webm" or "video/quicktime" or "video/x-matroska")
        {
            var player = MediaViews.Video(url, a);
            if (_v.Sh.C.VoiceChannelId == 0 || _v.Sh.C.Offline || a.Mime is not ("video/mp4" or "video/webm")) return player;
            var together = new HyperlinkButton { Content = U.T("📺 Oglądajcie razem w rozmowie", 12, U.Bold, Theme.Accent), Padding = U.Th(0) };
            together.Click += (_, _) => WatchUi.StartFile(_v.Sh, a.Id, _v.S.Watches.ContainsKey(_v.Sh.C.VoiceChannelId));
            return U.V(2, player, together);
        }
        if (url is not null && a.Mime.StartsWith("audio/")) return MediaViews.Audio(url, a);
        var icon = a.Mime.StartsWith("video/") ? "\uE714" : a.Mime.StartsWith("audio/") ? "\uE8D6" : a.Mime == "application/pdf" ? "\uEA90" : "\uE8A5";
        var card = new Grid { Width = 360, HorizontalAlignment = HorizontalAlignment.Left }.Cols("auto", "*", "auto").Add(
            U.I(icon, 26, Theme.Accent).At(0, 0),
            U.V(1, U.T(a.Name, 13.5, U.Semi, RichText.LinkBrush), U.T(Format.Size(a.Size), 11.5, null, Theme.Mu2)).With(p => p.Margin = U.Th(12, 0)).At(0, 1),
            U.I("", 16, Theme.Mu).At(0, 2));
        var box = U.Box(card, Theme.Card, 10, U.Th(12, 10), Theme.Line, 1);
        box.Margin = U.Th(0, 3, 0, 2);
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.Tapped += (_, _) => { if (url is not null) _ = Launcher.LaunchUriAsync(url); };
        box.Tip("Otwórz albo pobierz w przeglądarce");
        return box;
    }

    FrameworkElement YouTube(string id)
    {
        var img = new Image { Source = Images.Get(new Uri($"https://i.ytimg.com/vi/{id}/hqdefault.jpg"), 640), Width = 320, Height = 180, Stretch = Stretch.UniformToFill };
        var play = new Border { Width = 56, Height = 40, CornerRadius = new CornerRadius(10), Background = U.B("#E6ff0033"), Child = U.I("", 18, U.B("#ffffff")).With(i => i.HorizontalAlignment = HorizontalAlignment.Center) };
        var g = new Grid { Width = 320, Height = 180 }.Add(img, play);
        var b = new Border { CornerRadius = new CornerRadius(10), Child = g, HorizontalAlignment = HorizontalAlignment.Left, Margin = U.Th(0, 3, 0, 2) };
        b.Tapped += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://www.youtube.com/watch?v=" + id));
        b.Tip("Otwórz w YouTube");
        if (_v.Sh.C.VoiceChannelId == 0 || _v.Sh.C.Offline) return b;
        var together = new HyperlinkButton { Content = U.T("📺 Oglądajcie razem w rozmowie", 12, U.Bold, Theme.Accent), Padding = U.Th(0) };
        together.Click += (_, _) => WatchUi.Start(_v.Sh, id, null, _v.S.Watches.ContainsKey(_v.Sh.C.VoiceChannelId));
        return U.V(2, b, together);
    }

    FrameworkElement Reactions(Message m)
    {
        var panel = new WrapPanel { Spacing = 6, Margin = U.Th(0, 4, 0, 0) };
        foreach (var r in m.Reactions)
        {
            var mine = r.UserIds.Contains(S.Me.Id);
            FrameworkElement em = r.Emoji.StartsWith("<") && Markup.ParseInline(r.Emoji).FirstOrDefault() is EmojiSpan es && S.EmojiById(es.Id) is { } ce
                ? new Image { Source = Images.Get(Api.Abs(ce.Url), 40), Width = 18, Height = 18 }
                : U.T(r.Emoji, 14);
            var b = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = U.Th(8, 2), Height = 28,
                Background = mine ? U.Alpha("#ffb98f", .16) : U.W(.08), BorderBrush = mine ? U.Alpha("#ffb98f", .5) : U.W(.12), BorderThickness = U.Th(1),
                Child = U.H(6, em, U.T(r.UserIds.Count.ToString(), 12, U.Bold, mine ? Theme.Accent : Theme.Fg)),
            };
            b.Tip(string.Join(", ", r.UserIds.Take(12).Select(id => S.NameOf(id, Srv))) + (r.UserIds.Count > 12 ? "…" : ""));
            if (_v.CanReact) b.Tapped += (_, e) => { e.Handled = true; _v.React(m, r.Emoji); };
            panel.Children.Add(b);
        }
        if (_v.CanReact)
        {
            var add = new Border { CornerRadius = new CornerRadius(8), Padding = U.Th(8, 2), Height = 28, Background = U.W(.05), Child = U.I("", 14, Theme.Mu) };
            add.Tapped += (_, e) => { e.Handled = true; EmojiPicker.Show(add, _v, emo => _v.React(m, emo)); };
            add.Tip("Dodaj reakcję");
            panel.Children.Add(add);
        }
        return panel;
    }

    FrameworkElement ThreadChip(JsonElement t)
    {
        var count = t.Long("count") ?? 0;
        var tid = t.Long("id") ?? 0;
        var known = S.ThreadById(tid);
        var unread = known?.Unread == true;
        var last = t.Long("lastAt") is long la && count > 0 ? " · " + Format.When(la) : "";
        var chip = U.Box(U.H(8, U.I(GT.Thread, 13, Theme.Accent), U.T(known?.Name ?? t.Str("name") ?? "Wątek", 12.5, U.Bold, Theme.Accent),
            U.T(count > 0 ? $"{count} {Format.Plural(count, "odpowiedź", "odpowiedzi", "odpowiedzi")}{last}" : "Brak odpowiedzi — napisz pierwszą", 12, null, Theme.Mu),
            unread ? new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Red } : null), U.W(.06), 10, U.Th(10, 6));
        chip.BorderBrush = Theme.Accent;
        chip.BorderThickness = U.Th(3, 0, 0, 0);
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        chip.Margin = U.Th(0, 4, 0, 0);
        chip.Tip("Otwórz wątek");
        chip.PointerEntered += (_, _) => chip.Background = U.W(.1);
        chip.PointerExited += (_, _) => chip.Background = U.W(.06);
        if (tid > 0) chip.Tapped += (_, e) => { e.Handled = true; _v.Sh.OpenThread(tid); };
        return chip;
    }

    FrameworkElement Poll(Message m)
    {
        var p = m.Poll;
        var options = p.Prop("options").ValueKind == JsonValueKind.Array ? p.Prop("options").EnumerateArray().ToList() : [];
        var total = options.Sum(o => o.Prop("userIds").ValueKind == JsonValueKind.Array ? o.Prop("userIds").GetArrayLength() : 0);
        var closed = p.Bool("closed") == true || (p.Long("endsAt") is long ea && ea <= Format.NowMs());
        var multi = p.Bool("multi") == true;
        var myVotes = options.Where(o => o.Prop("userIds").ValueKind == JsonValueKind.Array && o.Prop("userIds").EnumerateArray().Any(x => x.GetInt64() == S.Me.Id))
            .Select(o => o.Long("id") ?? 0).ToList();
        var panel = U.V(8, U.T("📊 " + (p.Str("question") ?? "Ankieta"), 14.5, U.Bold, wrap: true));
        if (multi && !closed) panel.Add(U.T("Możesz wybrać kilka odpowiedzi", 11.5, null, Theme.Mu2).With(t => t.Margin = U.Th(0, -4, 0, 0)));
        foreach (var o in options)
        {
            var votes = o.Prop("userIds").ValueKind == JsonValueKind.Array ? o.Prop("userIds").EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
            var mine = votes.Contains(S.Me.Id);
            var pct = total == 0 ? 0 : votes.Count * 100.0 / total;
            var bar = new Grid { Height = 34 };
            bar.Add(new Border { Background = U.W(.06), CornerRadius = new CornerRadius(8) },
                new Border { Background = mine ? U.Alpha("#ffb98f", .35) : U.W(.12), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, Width = 3.4 * pct },
                new Grid { Padding = U.Th(10, 0) }.Cols("*", "auto").Add(U.T(o.Str("text") ?? "", 13, mine ? U.Bold : U.Semi).At(0, 0), U.T($"{votes.Count} · {pct:0}%", 12, null, Theme.Mu).At(0, 1)));
            bar.Width = 340;
            if (!closed && o.Long("id") is long oid)
                bar.Tapped += async (_, _) =>
                {
                    List<long> next = multi ? (mine ? myVotes.Where(x => x != oid).ToList() : [.. myVotes, oid]) : mine ? [] : [oid];
                    try { await Api.VotePoll(m.Id, next); } catch (Exception ex) { _v.Sh.Info(GibCordClient.Friendly(ex), true); }
                };
            panel.Add(bar);
        }
        var voters = options.SelectMany(o => o.Prop("userIds").ValueKind == JsonValueKind.Array ? o.Prop("userIds").EnumerateArray().Select(x => x.GetInt64()) : []).Distinct().Count();
        var ends = !closed && p.Long("endsAt") is long e2 ? " · koniec " + Format.When(e2) : "";
        panel.Add(U.T(closed ? $"Zakończona · {voters} {Format.Plural(voters, "osoba", "osoby", "osób")} {Format.Plural(voters, "głosowała", "głosowały", "głosowało")}"
            : $"{voters} {Format.Plural(voters, "osoba", "osoby", "osób")}{ends} · kliknij, żeby zagłosować", 11.5, null, Theme.Mu2));
        if (!closed && (m.AuthorId == S.Me.Id || _v.CanManage))
        {
            var end = new HyperlinkButton { Content = U.T("Zakończ ankietę", 11.5, U.Bold, Theme.Accent), Padding = U.Th(0) };
            end.Click += async (_, _) => { try { await Api.ClosePoll(m.Id); } catch (Exception ex) { _v.Sh.Info(GibCordClient.Friendly(ex), true); } };
            panel.Add(end);
        }
        return U.Box(panel, Theme.Card, 12, U.Th(14, 12), Theme.Line, 1).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = U.Th(0, 4, 0, 0); });
    }

    FrameworkElement AnnouncementCard(AnnCard c)
    {
        var ctx = new RichText.Ctx(S, Api, Srv, uid => _v.Sh.ShowProfile(uid, this));
        var banner = new Grid { Height = 54, Background = Theme.AccentGradient(), Padding = U.Th(14, 0) }.Cols("auto", "*").Add(
            new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = 34, Height = 34 }.At(0, 0));
        if (c.Chip is { } chip) banner.Add(U.Box(U.T(chip, 11, U.Bold, Theme.Fg), U.B("#2a1630"), 7, U.Th(8, 3)).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = U.Th(12, 0); }).At(0, 1));
        var body = U.V(8, RichText.Build("**" + c.Title + "**", ctx with { Size = 16 }));
        foreach (var (bullet, text) in c.Items)
        {
            var t = RichText.Build(text, ctx);
            body.Add(bullet ? new Grid().Cols("18", "*").Add(new Ellipse { Width = 6, Height = 6, Fill = Theme.Accent, VerticalAlignment = VerticalAlignment.Top, Margin = U.Th(2, 9, 0, 0) }.At(0, 0), t.At(0, 1)) : t);
        }
        var card = new Grid { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left }.Rows("auto", "auto").Add(banner.At(0), U.Box(body, Theme.Card, 0, U.Th(16, 14)).At(1));
        return new Border { CornerRadius = new CornerRadius(14), Child = card, BorderBrush = Theme.Line, BorderThickness = U.Th(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = U.Th(0, 4, 0, 2) };
    }

    static Brush LevelBrush(string? lv) => lv switch { "ok" => Theme.Green, "warn" => Theme.Yellow, "error" => Theme.Red, _ => Theme.Teal };

    static FrameworkElement ServerCard(JsonElement c)
    {
        var top = new Grid { Padding = U.Th(14, 12) }.Cols("*", "auto");
        top.Background = U.Alpha(c.Str("level") switch { "ok" => "#6fe3a5", "warn" => "#f2c45a", "error" => "#ff6b81", _ => "#7fd6cc" }, .14);
        top.Add(U.V(2, U.T(c.Str("title") ?? "", 15, U.Bold, wrap: true), c.Str("subtitle") is { } sub ? U.T(sub, 12.5, null, Theme.Mu, wrap: true) : null).At(0, 0));
        if (c.Prop("badge").Str("text") is { } badge)
            top.Add(U.Box(U.T(badge, 11, U.Bold, Theme.OnAccent), LevelBrush(c.Prop("badge").Str("level") ?? c.Str("level")), 8, U.Th(8, 3)).With(b => b.VerticalAlignment = VerticalAlignment.Top).At(0, 1));
        var panel = new StackPanel { Spacing = 10, Padding = U.Th(14, 12) };
        if (c.Prop("stats").ValueKind == JsonValueKind.Array)
        {
            var tiles = new WrapPanel { Spacing = 6 };
            foreach (var st in c.Prop("stats").EnumerateArray())
            {
                var v = U.H(6, st.Str("level") is { } lv ? new Ellipse { Width = 8, Height = 8, Fill = LevelBrush(lv) } : null, U.T(st.Str("value") ?? "", 16, U.Bold));
                tiles.Children.Add(U.Box(U.V(2, U.T(st.Str("label") ?? "", 11, U.Semi, Theme.Mu), v, st.Str("note") is { } n ? U.T(n, 10.5, null, Theme.Mu2) : null), U.W(.05), 10, U.Th(10, 8)).With(b => b.MinWidth = 140));
            }
            panel.Add(tiles);
        }
        if (c.Prop("sections").ValueKind == JsonValueKind.Array)
            foreach (var sec in c.Prop("sections").EnumerateArray())
            {
                if (sec.Str("title") is { } st) panel.Add(U.T(st, 12.5, U.Bold, Theme.Mu));
                if (sec.Prop("items").ValueKind != JsonValueKind.Array) continue;
                foreach (var it in sec.Prop("items").EnumerateArray())
                {
                    var row = new Grid().Cols("14", "*", "auto").Add(new Ellipse { Width = 6, Height = 6, Fill = LevelBrush(it.Str("level")), VerticalAlignment = VerticalAlignment.Top, Margin = U.Th(0, 7, 0, 0) }.At(0, 0),
                        U.T(it.Str("text") ?? "", 13, null, Theme.Fg, wrap: true).At(0, 1));
                    if (it.Str("url") is { } u && u.StartsWith("https://"))
                        row.Add(new HyperlinkButton { Content = U.T(it.Str("cta") ?? "Pokaż", 12, U.Bold, Theme.Accent), NavigateUri = new Uri(u), Padding = U.Th(6, 0) }.At(0, 2));
                    panel.Add(row);
                }
            }
        if (c.Prop("buttons").ValueKind == JsonValueKind.Array)
        {
            var btns = U.H(8);
            foreach (var b in c.Prop("buttons").EnumerateArray())
                if (b.Str("url") is { } u && u.StartsWith("https://"))
                    btns.Add(new HyperlinkButton { Content = U.T(b.Str("label") ?? "Otwórz", 12.5, U.Bold, Theme.OnAccent), NavigateUri = new Uri(u), Background = Theme.Accent, CornerRadius = new CornerRadius(8), Padding = U.Th(12, 5) });
            if (btns.Children.Count > 0) panel.Add(btns);
        }
        var card = new Grid { MaxWidth = 640 }.Rows("auto", "auto").Add(top.At(0), panel.At(1));
        return new Border { CornerRadius = new CornerRadius(14), Child = card, Background = Theme.Card, BorderBrush = Theme.Line, BorderThickness = U.Th(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = U.Th(0, 4, 0, 2) };
    }

    // ── pasek akcji i menu ──

    void ShowTools(bool on)
    {
        if (_tools is not null) { _body.Children.Remove(_tools); _tools = null; }
        if (_row is not null && !_row.M.IsSystem) Background = on ? U.W(.035) : _row.M.AuthorId != S.Me.Id && S.MentionsMe(_row.M) ? Theme.MentionedRow : Theme.Transparent;
        if (!on || _row is null || _row.M.IsSystem || _row.M.Send != SendState.Sent) return;
        var m = _row.M;
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        if (_v.CanReact)
        {
            foreach (var q in Quick)
            {
                var b = U.IconButton("", "Reakcja " + q, () => _v.React(m, q), 14, 32);
                b.Content = U.T(q, 15);
                p.Add(b);
            }
            var pick = U.IconButton("", "Dodaj reakcję", () => { }, 15, 32);
            pick.Click += (_, _) => EmojiPicker.Show(pick, _v, emo => _v.React(m, emo));
            p.Add(pick);
        }
        p.Add(U.IconButton("\uE97A", "Odpowiedz", () => _v.Reply(m), 15, 32));
        if (_v.CanStartThread(m)) p.Add(U.IconButton(GT.Thread, m.Thread.ValueKind == JsonValueKind.Object ? "Otwórz wątek" : "Utwórz wątek", () => OpenOrStartThread(m), 15, 32));
        if (m.AuthorId == S.Me.Id) p.Add(U.IconButton("", "Edytuj", () => _v.Edit(m), 15, 32));
        var more = U.IconButton("", "Więcej", () => { }, 15, 32);
        more.Flyout = Menu();
        p.Add(more);
        _tools = new Border
        {
            Child = p, Background = U.B("#F2281f40"), CornerRadius = new CornerRadius(10), BorderBrush = U.W(.12), BorderThickness = U.Th(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = U.Th(0, -14, 18, 0), Padding = U.Th(2),
        };
        Canvas.SetZIndex(_tools, 10);
        _body.Children.Add(_tools);
    }

    MenuFlyout Menu()
    {
        var m = _row!.M;
        var f = new MenuFlyout();
        f.Items.Add(M.Item("Odpowiedz", "", () => _v.Reply(m)));
        if (_v.CanReact)
        {
            var sub = new MenuFlyoutSubItem { Text = "Dodaj reakcję", Icon = new FontIcon { Glyph = "" } };
            foreach (var e in new[] { "👍", "❤️", "😂", "😮", "😢", "🔥", "🎉", "👀" }) sub.Items.Add(M.Item(e, "", () => _v.React(m, e)));
            f.Items.Add(sub);
        }
        if (_v.CanStartThread(m)) f.Items.Add(M.Item(m.Thread.ValueKind == JsonValueKind.Object ? "Otwórz wątek" : "Utwórz wątek", GT.Thread, () => OpenOrStartThread(m)));
        if (m.AuthorId == S.Me.Id) f.Items.Add(M.Item("Edytuj", "\uE70F", () => _v.Edit(m)));
        if (_v.CanPin) f.Items.Add(M.Item(m.PinnedAt is null ? "Przypnij" : "Odepnij", "", () => _v.TogglePin(m)));
        f.Items.Add(M.Item("Kopiuj tekst", "", () => Sidebar.Copy(m.Content)));
        f.Items.Add(M.Item("Wspomnij autora", "\uE910", () => _v.InsertMention(m.AuthorId)));
        f.Items.Add(M.Item("Kopiuj link do wiadomości", "\uE71B", () =>
        {
            var b = _v.Sh.C.Api.BaseUri.AbsoluteUri.TrimEnd('/');
            Sidebar.Copy(Srv is { } sv ? (m.ThreadId is long th ? $"{b}/kanal/{sv.Id}/{m.ChannelId}/watek/{th}" : $"{b}/kanal/{sv.Id}/{m.ChannelId}/{m.Id}") : $"{b}/dm/{m.ChannelId}/{m.Id}");
            _v.Sh.Info("Skopiowano link do wiadomości");
        }));
        if (m.AuthorId != S.Me.Id) f.Items.Add(M.Item("Zgłoś wiadomość", "\uE7BA", () => UserMenu.Report(_v.Sh, m)));
        if (m.AuthorId == S.Me.Id || _v.CanManage)
        {
            f.Items.Add(new MenuFlyoutSeparator());
            f.Items.Add(M.Item("Usuń wiadomość", "", () => _v.Delete(m), danger: true));
        }
        return f;
    }

    void OpenOrStartThread(Message m)
    {
        if (m.Thread.Long("id") is long tid) _v.Sh.OpenThread(tid);
        else _v.StartThread(m);
    }

    /// <summary>Krótkie podświetlenie po skoku do wiadomości.</summary>
    public void Flash()
    {
        var b = U.Alpha("#ffb98f", .22);
        Background = b;
        var anim = new DoubleAnimation { From = .22, To = 0, Duration = TimeSpan.FromSeconds(1.6), BeginTime = TimeSpan.FromSeconds(.6) };
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, b);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }
}
