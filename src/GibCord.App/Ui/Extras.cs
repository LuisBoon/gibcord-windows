using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>Obrazek na cały ekran: przybliżanie kółkiem (Ctrl+kółko albo gest), zapis na dysk, kopiowanie, otwarcie w przeglądarce. Esc zamyka.</summary>
public static class Lightbox
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static void Show(Shell sh, Uri url, string name)
    {
        var img = new Image { Source = Images.Get(url), Stretch = Stretch.Uniform, MaxWidth = 1600, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var zoom = new ScrollViewer
        {
            Content = img, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 1, MaxZoomFactor = 8,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        // przy powiększeniu 1 obraz ma się mieścić w oknie (ScrollViewer sam w sobie nie ogranicza zawartości)
        zoom.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
            img.MaxWidth = Math.Min(1600, e.NewSize.Width);
            img.MaxHeight = e.NewSize.Height;
        };
        img.DoubleTapped += (_, e) => { zoom.ChangeView(null, null, zoom.ZoomFactor > 1.1f ? 1 : 2.5f); e.Handled = true; };
        var save = U.Ghost("Zapisz…", async () =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, SuggestedFileName = Path.GetFileNameWithoutExtension(name) };
                var ext = Path.GetExtension(name);
                if (string.IsNullOrEmpty(ext)) ext = Path.GetExtension(url.AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                picker.FileTypeChoices.Add("Obraz", [ext]);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, sh.W.Hwnd);
                var f = await picker.PickSaveFileAsync();
                if (f is null) return;
                var bytes = await Http.GetByteArrayAsync(url);
                await Windows.Storage.FileIO.WriteBytesAsync(f, bytes);
                sh.Info("Zapisano: " + f.Name);
            }
            catch (Exception ex) { sh.Info("Nie udało się zapisać: " + ex.Message, true); }
        });
        var copy = U.Ghost("Kopiuj obraz", async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await ms.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                ms.Seek(0);
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(ms));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                sh.Info("Obraz skopiowany");
            }
            catch (Exception ex) { sh.Info("Nie udało się skopiować: " + ex.Message, true); }
        });
        var open = U.Ghost("Otwórz w przeglądarce", () => _ = Launcher.LaunchUriAsync(url));
        var close = U.Ghost("Zamknij (Esc)", sh.CloseOverlay);
        var g = new Grid { Padding = U.Th(40, 0, 40, 30), MaxHeight = 900 }.Rows("*", "auto").Add(
            zoom.At(0),
            U.V(4, U.H(10, U.T(name, 13, U.Semi, Theme.Mu), save, copy, open, close).With(p => p.HorizontalAlignment = HorizontalAlignment.Center),
                U.T("Kółko myszy z Ctrl albo podwójne kliknięcie — przybliżenie", 11, null, Theme.Mu2).With(t => t.HorizontalAlignment = HorizontalAlignment.Center)).With(p => p.Margin = U.Th(0, 12, 0, 0)).At(1));
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

    static string BadgeName(string b) => b switch
    {
        "early" => "🐣 Pierwsza pięćdziesiątka", "admin" => "👑 Właściciel GibCorda", "owner" => "🏰 Właściciel serwera", "chatty" => "💬 Gaduła",
        "talker" => "🎙️ Gawędziarz", "streamer" => "📺 Nadawca", "social" => "🤝 Towarzyski", "gifs" => "🎞️ GIF-omaniak", _ => "🏅 " + b,
    };

    /// <summary>Prywatna notatka o osobie: zapis po wyjściu z pola (na serwerze, gdy go obsługuje; inaczej na tym komputerze).</summary>
    static Border NoteCard(Shell sh, long uid)
    {
        var cur = sh.S.Notes.GetValueOrDefault(uid) ?? "";
        var box = new TextBox
        {
            Text = cur, PlaceholderText = "Kliknij, żeby dodać notatkę: skąd ją znasz, nick w grze…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = 500, MinHeight = 34, MaxHeight = 140, FontSize = 13, BorderThickness = U.Th(0), Background = Theme.Transparent, Padding = U.Th(0, 4),
        };
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[k] = Theme.Transparent;
        var where = U.T(sh.C.NotesSynced ? "" : "zapisana na tym komputerze", 10.5, null, Theme.Mu2);
        where.Visibility = sh.C.NotesSynced ? Visibility.Collapsed : Visibility.Visible;
        box.LostFocus += async (_, _) =>
        {
            var t = box.Text.Trim();
            if (t == cur) return;
            cur = t;
            var err = await sh.C.SetNote(uid, t);
            if (err is not null) sh.Info(err, true);
        };
        var head = new Grid().Cols("*", "auto").Add(U.T("NOTATKA · WIDZISZ TYLKO TY", 10.5, U.Bold, Theme.Mu2).At(0, 0), where.At(0, 1));
        return U.Box(U.V(4, head, box), U.B("#4D0A0618"), 12, U.Th(12, 8, 12, 6), U.Alpha("#ffb98f", .35), 1);
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
        if (u.Birthday is { Length: >= 10 } bd && DateTime.TryParse(bd, out var bday))
            body.Add(Card("Urodziny", U.T((u.BirthdayToday ? "🎂 Dziś! " : "") + bday.ToString("d MMMM", Format.Pl), 13, u.BirthdayToday ? U.Bold : null)));
        var since = new WrapPanel { Spacing = 16 };
        since.Children.Add(U.V(2, U.T("NA GIBCORDZIE OD", 10.5, U.Bold, Theme.Mu2), U.T(Format.Local(u.CreatedAt).ToString("d MMM yyyy", Format.Pl), 13)));
        if (s.MemberOf(srv, uid) is { JoinedAt: > 0 } mem) since.Children.Add(U.V(2, U.T("NA SERWERZE OD", 10.5, U.Bold, Theme.Mu2), U.T(Format.Local(mem.JoinedAt).ToString("d MMM yyyy", Format.Pl), 13)));
        if (s.Friends.GetValueOrDefault(uid) is { Status: "friends", Since: > 0 } fr) since.Children.Add(U.V(2, U.T("ZNAJOMI OD", 10.5, U.Bold, Theme.Mu2), U.T(Format.Local(fr.Since).ToString("d MMM yyyy", Format.Pl), 13)));
        body.Add(U.Box(since, U.B("#4D0A0618"), 12, U.Th(12, 10)));
        if (srv is { Levels: true } && s.MemberOf(srv, uid) is not null && !sh.C.Offline) body.Add(U.Box(Levels.Progress(sh, srv, uid), U.B("#4D0A0618"), 12, U.Th(12, 10)));
        if (u.Badges.Count > 0)
        {
            var badges = new WrapPanel { Spacing = 6 };
            foreach (var b in u.Badges) badges.Children.Add(U.Box(U.T(BadgeName(b), 11.5, U.Semi), U.W(.08), 8, U.Th(8, 3)));
            body.Add(Card("Odznaki", badges));
        }
        if (uid != s.Me.Id) body.Add(NoteCard(sh, uid));
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
            else if (s.Blocked.Contains(uid)) btns.Add(U.Ghost("Odblokuj", () => { close(); UserMenu.SetBlocked(sh, uid, false); }).With(b => { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Height = 36; }).At(0, 1));
            btns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var more = U.IconButton("\uE712", "Więcej", () => { }, 15, 36);
            more.Background = U.W(.1);
            more.Flyout = UserMenu.Build(sh, uid, srv, sh.CurrentChat);
            btns.Add(more.At(0, 2));
            body.Add(btns);
        }
        return new Grid { Width = 320 }.Rows("auto", "auto", "auto").Add(banner.At(0), av.At(1), body.At(2));
    }
}
