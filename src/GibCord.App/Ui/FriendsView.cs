using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>Znajomi: dostępni, wszyscy, oczekujące zaproszenia, dodawanie.</summary>
public sealed class FriendsView : Grid
{
    readonly Shell _sh;
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly StackPanel _list = new() { Padding = U.Th(24, 12, 24, 24), Spacing = 2 };
    readonly StackPanel _active = new() { Padding = U.Th(16, 16, 16, 24), Spacing = 10 };
    readonly Border _activeCol;
    string _tab = "online";

    public FriendsView(Shell sh)
    {
        _sh = sh;
        this.Rows("48", "*").Cols("*", "auto");
        var head = new Border { BorderBrush = Theme.Line, BorderThickness = U.Th(0, 0, 0, 1), Padding = U.Th(16, 0) };
        head.Child = U.H(14, U.H(8, U.I("\uE716", 18), U.T("Znajomi", 15, U.Bold)), new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = 22, Fill = U.W(.14) }, _tabs);
        _activeCol = new Border { Width = 320, BorderBrush = Theme.Line, BorderThickness = U.Th(1, 0, 0, 0), Child = U.Scroll(_active) };
        this.Add(head.At(0, 0, colSpan: 2), U.Scroll(_list).At(1, 0), _activeCol.At(1, 1));
        SizeChanged += (_, e) => _activeCol.Visibility = e.NewSize.Width > 860 ? Visibility.Visible : Visibility.Collapsed;
        if (_sh.S.Friends.Values.Any(f => f.Status == "incoming")) _tab = "pending";
        Refresh();
    }

    Store S => _sh.S;

    public void Refresh()
    {
        _tabs.Children.Clear();
        var pending = S.Friends.Values.Count(f => f.Status == "incoming");
        Tab("online", "Dostępni");
        Tab("all", "Wszyscy");
        Tab("pending", "Oczekujące", pending);
        var add = new Button
        {
            Content = U.T("Dodaj znajomego", 12.5, U.Bold, _tab == "add" ? Theme.Green : Theme.OnAccent), Height = 28, Padding = U.Th(10, 0),
            Background = _tab == "add" ? U.Alpha("#6fe3a5", .12) : Theme.Green, BorderThickness = U.Th(0), CornerRadius = new CornerRadius(8),
        };
        add.Click += (_, _) => { _tab = "add"; Refresh(); };
        _tabs.Children.Add(add);
        _list.Children.Clear();
        if (WrappedView.Available(_sh) && App.Prefs.WrappedBannerClosed != WrappedView.Year(_sh)) _list.Children.Add(WrappedView.Banner(_sh, Refresh)); // 6.1
        DrawActive();
        switch (_tab)
        {
            case "add": BuildAdd(); break;
            case "pending": BuildList(S.Friends.Values.Where(f => f.Status != "friends").OrderBy(f => f.Status).ThenByDescending(f => f.Since).ToList(), "Oczekujące", "Nie masz żadnych zaproszeń. Wyślij komuś swoje w „Dodaj znajomego”."); break;
            case "all": BuildList(Friends(), "Wszyscy znajomi", "Nie masz jeszcze znajomych. Dodaj kogoś po nazwie użytkownika."); break;
            default: BuildList(Friends().Where(f => S.StatusOf(f.UserId) != "offline").ToList(), "Dostępni", "Nikogo ze znajomych nie ma teraz w sieci. Gibek czeka razem z Tobą."); break;
        }
    }

    /// <summary>„Aktywni teraz”: znajomi, którzy grają, słuchają muzyki albo są w rozmowie.</summary>
    void DrawActive()
    {
        _active.Children.Clear();
        _active.Add(U.T("Aktywni teraz", 18, U.Bold));
        var any = false;
        foreach (var f in Friends())
        {
            var u = S.U(f.UserId);
            if (u is null || S.StatusOf(u.Id) == "offline") continue;
            var voice = S.Voice.FirstOrDefault(kv => kv.Value.Any(v => v.UserId == u.Id));
            string? line = u.GameName is { } g ? "🎮 " + g : u.Activity is { Type: "spotify" } a ? $"🎵 {a.Title} – {a.ArtistText}" : null;
            string? where = voice.Value is not null && S.ChannelById(voice.Key) is { } vc ? $"🔊 {vc.Name} · {S.ServerOfChannel(vc.Id)?.Name}" : null;
            if (line is null && where is null) continue;
            any = true;
            var uid = u.Id;
            var card = U.Box(U.V(6, U.H(10, new Avatar(32, true).Set(S, _sh.C.Api, uid, null, true), U.T(u.Name, 14, U.Bold)),
                line is null ? null : U.T(line, 12.5, null, Theme.Fg, wrap: true), where is null ? null : U.T(where, 12, U.Semi, Theme.Green, wrap: true)), U.W(.05), 12, U.Th(14, 12), Theme.Line, 1);
            card.Tapped += (_, _) => _sh.ShowProfile(uid, card);
            if (voice.Value is not null && S.ChannelById(voice.Key) is { } jc && S.ServerOfChannel(jc.Id) is { } js)
                ((StackPanel)card.Child).Add(U.Ghost("Dołącz", () => _sh.JoinVoiceChannel(js.Id, jc.Id), 28).With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
            _active.Add(card);
        }
        if (!any) _active.Add(U.T("Na razie cisza… Gdy ktoś ze znajomych zacznie grać, słuchać muzyki albo wejdzie na kanał głosowy, zobaczysz to tutaj.", 13, null, Theme.Mu, wrap: true));
    }

    List<Friend> Friends() => S.Friends.Values.Where(f => f.Status == "friends").OrderBy(f => S.U(f.UserId)?.Name ?? "", StringComparer.Create(Format.Pl, true)).ToList();

    void Tab(string key, string label, int badge = 0)
    {
        var on = _tab == key;
        var content = U.H(6, U.T(label, 13, U.Semi, on ? Theme.Fg : Theme.Mu));
        if (badge > 0) content.Add(U.Badge(badge));
        var b = new Button { Content = content, Height = 28, Padding = U.Th(10, 0), Background = on ? Theme.Active : Theme.Transparent, BorderThickness = U.Th(0), CornerRadius = new CornerRadius(8) };
        b.Click += (_, _) => { _tab = key; Refresh(); };
        _tabs.Children.Add(b);
    }

    void BuildList(List<Friend> items, string title, string empty)
    {
        _list.Children.Add(U.T($"{title.ToUpperInvariant()} — {items.Count}", 11, U.Bold, Theme.Mu2).With(t => t.Margin = U.Th(8, 8, 0, 8)));
        if (items.Count == 0)
        {
            _list.Children.Add(U.V(12,
                new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = 96, Height = 96, Opacity = .8 },
                U.T(empty, 14, null, Theme.Mu, wrap: true).With(t => { t.TextAlignment = TextAlignment.Center; t.MaxWidth = 360; })).With(p => { p.HorizontalAlignment = HorizontalAlignment.Center; p.Margin = U.Th(0, 60, 0, 0); }));
            return;
        }
        foreach (var f in items)
        {
            var u = S.U(f.UserId);
            if (u is null) continue;
            var uid = u.Id;
            var sub = f.Status switch
            {
                "incoming" => "Chce dodać Cię do znajomych",
                "outgoing" => "Wysłano zaproszenie",
                _ => u.GameName is { } gm ? "Gra w " + gm : u.Activity is { Type: "spotify" } a ? $"Słucha: {a.Title}" : !string.IsNullOrEmpty(u.CustomStatus) ? u.CustomStatus! : Format.StatusPl.GetValueOrDefault(S.StatusOf(uid), ""),
            };
            var actions = U.H(6);
            Button Round(string glyph, string tip, Action a, Microsoft.UI.Xaml.Media.Brush? fg = null) =>
                U.IconButton(glyph, tip, a, 15, 36, fg).With(b => { b.Background = U.B("#4D0A0618"); b.CornerRadius = new CornerRadius(18); });
            if (f.Status == "incoming")
            {
                actions.Add(Round("", "Przyjmij", async () => { var e = await _sh.C.AcceptFriend(uid); if (e is not null) _sh.Info(e, true); }, Theme.Green));
                actions.Add(Round("", "Odrzuć", async () => { var e = await _sh.C.RemoveFriend(uid); if (e is not null) _sh.Info(e, true); }, Theme.Red));
            }
            else if (f.Status == "outgoing")
                actions.Add(Round("", "Anuluj zaproszenie", async () => { var e = await _sh.C.RemoveFriend(uid); if (e is not null) _sh.Info(e, true); }));
            else
            {
                actions.Add(Round("\uE8BD", "Napisz", () => _sh.OpenDmWith(uid)));
                actions.Add(Round(G.Phone, "Zadzwoń", async () =>
                {
                    var (dm, err) = await _sh.C.OpenDm(uid);
                    if (err is not null) { _sh.Info(err, true); return; }
                    _sh.Go(0, dm);
                    _sh.StartCall(dm);
                }));
                var more = Round("", "Więcej", () => { });
                var menu = new MenuFlyout();
                menu.Items.Add(M.Item("Pokaż profil", "", () => _sh.ShowProfile(uid, more)));
                menu.Items.Add(M.Item("Usuń ze znajomych", "", async () =>
                {
                    if (!await M.Confirm(_sh, "Usunąć ze znajomych?", $"{u.Name} zniknie z Twojej listy znajomych.", "Usuń")) return;
                    var e = await _sh.C.RemoveFriend(uid);
                    if (e is not null) _sh.Info(e, true);
                }, danger: true));
                more.Flyout = menu;
                actions.Add(more);
            }
            var g = new Grid { Height = 62 }.Cols("auto", "*", "auto").Add(
                new Avatar(36, true).Set(S, _sh.C.Api, uid, null, true).At(0, 0),
                U.V(1, U.H(6, U.T(u.Name, 14.5, U.Bold), U.T("@" + u.Username, 12, null, Theme.Mu2)), U.T(sub, 12.5, null, Theme.Mu)).With(p => p.Margin = U.Th(12, 0)).At(0, 1),
                actions.At(0, 2));
            var row = U.Row(g, f.Status == "friends" ? () => _sh.OpenDmWith(uid) : null, false, 10, U.Th(10, 0), U.Th(0));
            _list.Children.Add(U.HLine(.06));
            _list.Children.Add(row);
        }
    }

    void BuildAdd()
    {
        var box = new TextBox { PlaceholderText = "Nazwa użytkownika znajomego", Height = 48, FontSize = 15, Padding = U.Th(14, 12), CornerRadius = new CornerRadius(12), FontFamily = Theme.Font };
        var result = U.T("", 13, U.Semi, Theme.Green, wrap: true);
        async void Send()
        {
            var name = box.Text.Trim();
            if (name.Length == 0) return;
            var e = await _sh.C.AddFriend(name);
            result.Text = e ?? $"Wysłano zaproszenie do @{name.TrimStart('@')}.";
            result.Foreground = e is null ? Theme.Green : Theme.Red;
            if (e is null) box.Text = "";
        }
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Send(); } };
        var go = U.Primary("Wyślij zaproszenie", Send, 48);
        go.HorizontalAlignment = HorizontalAlignment.Right;
        _list.Children.Add(U.V(10,
            U.T("DODAJ ZNAJOMEGO", 13, U.Bold),
            U.T("Wpisz nazwę użytkownika (tę z @). Gdy ta osoba przyjmie zaproszenie, zobaczycie nawzajem swój status i napiszecie do siebie bez wspólnego serwera.", 13.5, null, Theme.Mu, wrap: true),
            new Grid { ColumnSpacing = 10, Margin = U.Th(0, 6, 0, 0) }.Cols("*", "auto").Add(box.At(0, 0), go.At(0, 1)),
            result).With(p => p.MaxWidth = 720));
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
    }
}
