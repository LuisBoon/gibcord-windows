using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>Wybór emoji: najczęstsze + emoji serwerów. (Pełna lista systemowa: Win + kropka.)</summary>
public static class EmojiPicker
{
    static readonly (string Name, string[] Items)[] Groups =
    [
        ("Buźki", ["😀", "😂", "🤣", "😊", "😍", "🥰", "😘", "😎", "🤩", "🥳", "😏", "😅", "😭", "😢", "😡", "🤯", "😱", "🥺", "😴", "🤔", "🤨", "🙄", "😬", "🤫", "🤡", "💀", "👻", "🤖"]),
        ("Gesty", ["👍", "👎", "👏", "🙌", "🙏", "🤝", "👋", "✌️", "🤞", "👌", "🤌", "💪", "👀", "🫡", "🫶", "✊"]),
        ("Serca i znaki", ["❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "💔", "🔥", "✨", "⭐", "💯", "✅", "❌", "⚠️", "❓", "‼️", "🎉", "🎊"]),
        ("Inne", ["🎮", "🕹️", "🎧", "🎵", "📺", "📷", "💻", "📱", "⚽", "🏀", "🍕", "🍔", "🍟", "🍿", "☕", "🍺", "🎂", "🌙", "☀️", "🌈", "🐶", "🐱", "🦊", "🐸"]),
    ];

    public static void Show(FrameworkElement anchor, ChatView v, Action<string> pick)
    {
        var panel = new StackPanel { Spacing = 6, Width = 344 };
        Flyout? fly = null;
        void Add(string group, IEnumerable<FrameworkElement> items)
        {
            panel.Children.Add(U.T(group.ToUpperInvariant(), 10.5, U.Bold, Theme.Mu2).With(t => t.Margin = U.Th(2, 6, 0, 0)));
            var wrap = new WrapPanel { Spacing = 2 };
            foreach (var i in items) wrap.Children.Add(i);
            panel.Children.Add(wrap);
        }
        Button Btn(UIElement content, string tip, string value)
        {
            var b = new Button { Content = content, Width = 40, Height = 40, Padding = U.Th(0), Background = Theme.Transparent, BorderThickness = U.Th(0), CornerRadius = new CornerRadius(8) };
            b.Tip(tip);
            b.Click += (_, _) => { fly?.Hide(); pick(value); };
            return b;
        }
        foreach (var srv in v.S.Servers.Where(s => s.Emojis.Count > 0).OrderByDescending(s => s.Id == v.Srv?.Id))
            Add(srv.Name, srv.Emojis.Take(40).Select(e => (FrameworkElement)Btn(new Image { Source = Images.Get(v.Sh.C.Api.Abs(e.Url), 56), Width = 26, Height = 26 }, ":" + e.Name + ":", $"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>")));
        foreach (var (name, items) in Groups) Add(name, items.Select(e => (FrameworkElement)Btn(U.T(e, 20).With(t => t.TextTrimming = TextTrimming.None), e, e)));
        panel.Children.Add(U.T("Więcej emoji: Win + . (kropka)", 11, null, Theme.Mu2).With(t => t.Margin = U.Th(2, 6, 0, 0)));
        fly = new Flyout { Content = U.Scroll(panel).With(s => s.MaxHeight = 380), Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        fly.ShowAt(anchor);
    }
}

/// <summary>Obrazek na cały ekran (klik obok albo Esc zamyka).</summary>
public static class Lightbox
{
    public static void Show(Shell sh, Uri url, string name)
    {
        var img = new Image { Source = Images.Get(url), Stretch = Stretch.Uniform, MaxWidth = 1600 };
        var open = U.Ghost("Otwórz w przeglądarce", () => _ = Launcher.LaunchUriAsync(url));
        var close = U.Ghost("Zamknij (Esc)", sh.CloseOverlay);
        var g = new Grid { Padding = U.Th(40, 0, 40, 30), MaxHeight = 900 }.Rows("*", "auto").Add(
            img.At(0),
            U.H(10, U.T(name, 13, U.Semi, Theme.Mu), open, close).With(p => { p.HorizontalAlignment = HorizontalAlignment.Center; p.Margin = U.Th(0, 12, 0, 0); }).At(1));
        sh.ShowOverlay(g, () => close.Focus(FocusState.Programmatic));
    }
}

/// <summary>Ctrl+K: wpisz kawałek nazwy kanału, serwera, osoby albo rozmowy i przejdź tam Enterem.</summary>
public sealed class QuickSwitcher : Grid
{
    readonly Shell _sh;
    readonly TextBox _q;
    readonly StackPanel _list = new() { Spacing = 2 };
    List<(string Label, string Sub, Action Go, Func<FrameworkElement> Icon)> _items = [];
    int _sel;

    public QuickSwitcher(Shell sh)
    {
        _sh = sh;
        Width = 560;
        var frame = new Border { CornerRadius = new CornerRadius(16), Background = U.B("#F7281f40"), BorderBrush = U.W(.16), BorderThickness = U.Th(1) };
        Children.Add(frame);
        _q = new TextBox { PlaceholderText = "Dokąd idziemy? Kanał, serwer, osoba…", FontSize = 16, BorderThickness = U.Th(0), Padding = U.Th(16, 14), FontFamily = Theme.Font };
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" }) _q.Resources[k] = Theme.Transparent;
        _q.TextChanged += (_, _) => { _sel = 0; Search(); };
        _q.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Down) { _sel = Math.Min(_sel + 1, _items.Count - 1); Draw(); e.Handled = true; }
            else if (e.Key == VirtualKey.Up) { _sel = Math.Max(_sel - 1, 0); Draw(); e.Handled = true; }
            else if (e.Key == VirtualKey.Enter && _items.Count > 0) { e.Handled = true; var go = _items[_sel].Go; _sh.CloseOverlay(); go(); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; _sh.CloseOverlay(); }
        };
        var foot = U.H(16, U.T("↑ ↓ wybierz", 11, null, Theme.Mu2), U.T("Enter — przejdź", 11, null, Theme.Mu2), U.T("Esc — zamknij", 11, null, Theme.Mu2));
        foot.Margin = U.Th(16, 8, 16, 10);
        frame.Child = new Grid().Rows("auto", "auto", "auto", "auto").Add(_q.At(0), U.HLine(.1).At(1), U.Scroll(_list).With(s => { s.MaxHeight = 380; s.Padding = U.Th(8); }).At(2), foot.At(3));
        Search();
    }

    public void FocusBox() => _q.Loaded += (_, _) => _q.Focus(FocusState.Programmatic);

    static string Fold(string s)
    {
        var n = s.ToLowerInvariant().Replace('ł', 'l').Normalize(System.Text.NormalizationForm.FormD);
        return new string(n.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    void Search()
    {
        var s = _sh.S;
        var q = Fold(_q.Text.Trim().TrimStart('#', '@'));
        var all = new List<(int Score, (string, string, Action, Func<FrameworkElement>) Item)>();
        int Score(string name) { var f = Fold(name); return q.Length == 0 ? 1 : f.StartsWith(q) ? 3 : f.Contains(q) ? 2 : 0; }
        foreach (var d in s.DmsSorted())
        {
            var dd = d;
            var title = s.DmTitle(d);
            var sc = Score(title);
            if (sc > 0) all.Add((sc + (s.IsUnread(d.Id) ? 2 : 0) + 1, (title, d.IsGroup ? "grupa" : "rozmowa prywatna", () => _sh.Go(0, dd.Id),
                () => d.IsGroup ? new Avatar(24).SetRaw(title, "#b69cff", null, null) : new Avatar(24, true).Set(s, _sh.C.Api, s.DmOther(dd)?.Id ?? 0, null, true))));
        }
        foreach (var srv in s.Servers)
        {
            var sv = srv;
            var ss = Score(srv.Name);
            if (ss > 0 && q.Length > 0) all.Add((ss, (srv.Name, "serwer", () => _sh.Go(sv.Id, 0), () => U.I("", 16))));
            foreach (var c in srv.Channels.Where(c => !c.IsVoice))
            {
                var cc = c;
                var sc = Score(c.Name);
                if (sc > 0) all.Add((sc + (s.IsUnread(c.Id) ? 1 : 0) + (s.MentionsIn(c.Id) > 0 ? 2 : 0), ("#" + c.Name, srv.Name, () => _sh.Go(sv.Id, cc.Id), () => U.T("#", 17, U.Semi, Theme.Mu))));
            }
        }
        if (q.Length > 0)
            foreach (var u in s.Users.Values.Where(u => u.Id != s.Me.Id))
            {
                var uu = u;
                var sc = Math.Max(Score(u.Name), Score(u.Username));
                if (sc > 0 && !s.Dms.Any(d => !d.IsGroup && d.UserIds.Contains(u.Id) && !s.DmHidden(d)))
                    all.Add((sc, (u.Name, "@" + u.Username + " · napisz", () => _sh.OpenDmWith(uu.Id), () => new Avatar(24, true).Set(s, _sh.C.Api, uu.Id, null, true))));
            }
        _items = all.OrderByDescending(x => x.Score).Take(q.Length == 0 ? 8 : 14).Select(x => x.Item).ToList();
        Draw();
    }

    void Draw()
    {
        _list.Children.Clear();
        if (_items.Count == 0) { _list.Children.Add(U.T("Nic nie pasuje. Spróbuj innej nazwy.", 13, null, Theme.Mu).With(t => t.Margin = U.Th(10, 8))); return; }
        if (_q.Text.Length == 0) _list.Children.Add(U.T("OSTATNIE ROZMOWY I KANAŁY", 10.5, U.Bold, Theme.Mu2).With(t => t.Margin = U.Th(10, 4, 0, 4)));
        for (var i = 0; i < _items.Count; i++)
        {
            var (label, sub, go, icon) = _items[i];
            var g = new Grid { Height = 38 }.Cols("34", "auto", "*").Add(icon().At(0, 0), U.T(label, 14, U.Semi).At(0, 1),
                U.T(sub, 12, null, Theme.Mu2).With(t => t.Margin = U.Th(10, 0, 0, 0)).At(0, 2));
            var go2 = go;
            _list.Children.Add(U.Row(g, () => { _sh.CloseOverlay(); go2(); }, i == _sel, 10, U.Th(8, 0), U.Th(0)));
        }
    }
}

/// <summary>Karta profilu po kliknięciu w nazwę albo awatar.</summary>
public static class ProfileCard
{
    public static void Show(Shell sh, long uid, FrameworkElement anchor)
    {
        Flyout? fly = null;
        var card = Build(sh, uid, () => fly?.Hide());
        if (card is null) return;
        fly = new Flyout { Content = card, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        fly.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
        {
            Setters =
            {
                new Setter(Control.PaddingProperty, new Thickness(0)),
                new Setter(Control.BackgroundProperty, U.B("#FA2a2147")),
                new Setter(Control.CornerRadiusProperty, new CornerRadius(14)),
                new Setter(FrameworkElement.MaxWidthProperty, 340.0),
            },
        };
        fly.ShowAt(anchor);
    }

    public static FrameworkElement? Build(Shell sh, long uid, Action close)
    {
        var s = sh.S;
        var u = s.U(uid);
        if (u is null) return null;
        var srv = sh.ServerId > 0 ? s.ServerById(sh.ServerId) : null;
        var bannerBrush = u.BannerImage is { } bi && sh.C.Api.Abs(bi) is { } bu
            ? (Brush)new ImageBrush { ImageSource = Images.Get(bu, 680), Stretch = Stretch.UniformToFill }
            : u.Banner is { } bc ? U.B(bc) : Theme.AccentGradient();
        var banner = new Border { Height = 96, Background = bannerBrush, CornerRadius = new CornerRadius(14, 14, 0, 0) };
        var av = new Avatar(80, true, "#2a2140").Set(s, sh.C.Api, uid, srv, true);
        av.HorizontalAlignment = HorizontalAlignment.Left;
        av.Margin = U.Th(16, -44, 0, 0);
        var body = U.V(10);
        body.Padding = U.Th(16, 6, 16, 16);
        var nameRow = U.H(8, U.T(s.NameOf(uid, srv), 20, U.Bold));
        if (u.Staff) nameRow.Add(U.Box(U.T("Właściciel GibCorda", 10, U.Bold, Theme.OnAccent), U.B("#f2a93b"), 6, U.Th(6, 2)));
        body.Add(nameRow);
        var sub = "@" + u.Username + (string.IsNullOrEmpty(u.Pronouns) ? "" : " · " + u.Pronouns);
        body.Add(U.T(sub, 12.5, null, Theme.Mu).With(t => t.Margin = U.Th(0, -8, 0, 0)));
        if (!string.IsNullOrEmpty(u.CustomStatus)) body.Add(U.T(u.CustomStatus!, 13, null, Theme.Fg, wrap: true));

        Border Card(string label, UIElement content) => U.Box(U.V(6, U.T(label.ToUpperInvariant(), 10.5, U.Bold, Theme.Mu2), content), U.B("#4D0A0618"), 12, U.Th(12, 10));
        if (u.Activity is { Type: "spotify" } a)
        {
            var art = a.Image is { } img && Uri.TryCreate(img, UriKind.Absolute, out var iu) ? new Image { Source = Images.Get(iu, 112), Width = 52, Height = 52 } : null;
            body.Add(Card("Słucha Spotify", U.H(10, art, U.V(1, U.T(a.Title ?? "", 13, U.Bold), U.T(a.ArtistText, 12, null, Theme.Mu)))));
        }
        if (u.GameName is { } game) body.Add(Card("Gra", U.H(8, U.I("", 16, Theme.Green), U.T(game, 13, U.Bold))));
        if (!string.IsNullOrWhiteSpace(u.Bio)) body.Add(Card("O mnie", U.T(u.Bio!, 13, null, Theme.Fg, wrap: true)));
        var roles = s.RolesOf(srv, uid);
        if (roles.Count > 0)
        {
            var wrap = new WrapPanel { Spacing = 6 };
            foreach (var r in roles)
                wrap.Children.Add(U.Box(U.H(6, new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 9, Height = 9, Fill = U.B(r.Color ?? "#8b8599") }, U.T(r.Name, 12, U.Semi)), U.W(.07), 8, U.Th(8, 3)));
            body.Add(Card("Role", wrap));
        }
        body.Add(Card("Na GibCordzie od", U.T(Format.Local(u.CreatedAt).ToString("d MMMM yyyy", Format.Pl), 13)));
        if (uid != s.Me.Id)
        {
            var btns = new Grid { ColumnSpacing = 8 }.Cols("*", "*");
            btns.Add(U.Primary("Wiadomość", () => { close(); sh.OpenDmWith(uid); }, 36).At(0, 0));
            var f = s.Friends.GetValueOrDefault(uid);
            Button? fb = f?.Status switch
            {
                null => U.Ghost("Dodaj do znajomych", async () => { var e = await sh.C.AddFriend(u.Username); sh.Info(e ?? "Wysłano zaproszenie do znajomych", e is not null); close(); }),
                "incoming" => U.Ghost("Przyjmij zaproszenie", async () => { var e = await sh.C.AcceptFriend(uid); if (e is not null) sh.Info(e, true); close(); }),
                "outgoing" => U.Ghost("Zaproszenie wysłane", () => { }).With(b => b.IsEnabled = false),
                _ => null,
            };
            if (fb is not null) btns.Add(fb.With(b => { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Height = 36; }).At(0, 1));
            body.Add(btns);
        }
        return new Grid { Width = 320 }.Rows("auto", "auto", "auto").Add(banner.At(0), av.At(1), body.At(2));
    }
}
