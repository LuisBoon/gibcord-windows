using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using User = GibCord.Core.User;

namespace GibCord.App.Ui;

/// <summary>
/// Panel admina GibCorda (tylko właściciele serwisu): przegląd, konta, serwery z podglądem, zgłoszenia, dziennik, ogłoszenia.
/// Każda akcja i podgląd trafia na serwerze do dziennika admina. Poczta zostaje na stronie.
/// </summary>
public sealed class AdminPage : Grid
{
    readonly Shell _sh;
    readonly StackPanel _nav = new() { Spacing = 2, Padding = U.Th(12, 24, 12, 24) };
    readonly StackPanel _body = new() { Spacing = 14, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, Padding = U.Th(40, 28, 40, 60) };
    readonly ScrollViewer _sv;
    readonly Grid _wide = new(); // sekcje na całą wysokość (poczta: trzy kolumny)
    string _section = "stats";
    string _q = "", _filter = "all";

    GibCordApi Api => _sh.C.Api;
    Store S => _sh.S;

    public AdminPage(Shell sh, string section = "stats")
    {
        _sh = sh;
        _section = section;
        Background = U.B("#F5181230");
        this.Cols("250", "*", "auto");
        _sv = U.Scroll(_body);
        var close = U.IconButton(G.Close, "Zamknij (Esc)", sh.CloseAdmin, 16, 40);
        close.Margin = U.Th(0, 20, 24, 0);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Background = U.W(.08);
        close.CornerRadius = new CornerRadius(20);
        this.Add(new Border { Background = U.W(.03), Child = U.Scroll(_nav) }.At(0, 0), _sv.At(0, 1), _wide.At(0, 1), close.At(0, 2));
        KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; sh.CloseAdmin(); } };
        Draw();
    }

    void Draw()
    {
        _nav.Children.Clear();
        _nav.Add(U.H(10, U.T("🛠️", 20), U.T("Panel admina", 16, U.Bold)).With(p => p.Margin = U.Th(10, 0, 0, 12)));
        void Item(string key, string glyph, string label)
        {
            var sel = _section == key;
            var row = U.Row(U.H(10, U.I(glyph, 15, sel ? Theme.Fg : Theme.Mu), U.T(label, 14, sel ? U.Bold : U.Semi, sel ? Theme.Fg : Theme.Mu)), () => { _section = key; Draw(); _sv.ChangeView(null, 0, null, true); }, sel, 8, U.Th(10, 0), U.Th(0, 1));
            row.Height = 38;
            _nav.Add(row);
        }
        Item("stats", "", "Przegląd");
        Item("users", "", "Konta");
        Item("servers", "", "Serwery");
        Item("reports", "", "Zgłoszenia");
        Item("system", "\uE9D9", "System i wykresy");
        Item("activity", "\uE823", "Aktywność osób");
        Item("log", "", "Dziennik");
        Item("announce", "", "Ogłoszenie");
        Item("mail", "\uE715", "Poczta");
        _body.Children.Clear();
        _wide.Children.Clear();
        var wide = _section == "mail";
        _sv.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        _wide.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        if (wide) { _wide.Children.Add(new AdminMail(_sh)); return; }
        switch (_section)
        {
            case "users": Users(); break;
            case "servers": Servers(); break;
            case "reports": Reports(); break;
            case "log": Log(); break;
            case "activity":
                Title("Aktywność osób", "Kto był w GibCordzie o jakiej godzinie. Kliknij osobę, żeby zobaczyć jej kartę.");
                _body.Add(new AdminActivity(_sh));
                break;
            case "system":
                Title("System i wykresy", "Stan maszyny, ruch i osoby — jak w panelu na stronie.");
                _body.Add(new AdminSystem(_sh));
                break;
            case "announce": Announce(); break;
            default: Stats(); break;
        }
    }

    void Title(string t, string? sub = null)
    {
        _body.Add(U.T(t, 24, U.Bold));
        if (sub is not null) _body.Add(U.T(sub, 13.5, null, Theme.Mu, wrap: true).With(x => x.Margin = U.Th(0, -8, 0, 4)));
    }

    StackPanel Spinner() { var p = new StackPanel { Spacing = 6 }; p.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 }); _body.Add(p); return p; }
    static void Fail(StackPanel p, Exception ex) { p.Children.Clear(); p.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    async Task<bool> Run(Func<Task> a, string? ok = null)
    {
        try { await a(); if (ok is not null) _sh.Info(ok); return true; }
        catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); return false; }
    }
    void Users(JsonElement map) { if (map.ValueKind == JsonValueKind.Object) foreach (var p in map.EnumerateObject()) if (p.Value.To<User>() is { } u && !S.Users.ContainsKey(u.Id)) S.Users[u.Id] = u; }

    // ───────────────────────── Przegląd ─────────────────────────

    void Stats()
    {
        Title("Przegląd");
        var p = Spinner();
        _ = FillStats(p);
    }

    async Task FillStats(StackPanel p)
    {
        try
        {
            var s = await Api.GetJson("admin/stats");
            p.Children.Clear();
            var tiles = new WrapPanel { Spacing = 10 };
            void Tile(string label, string value, string? note = null) =>
                tiles.Children.Add(U.Box(U.V(2, U.T(label, 11.5, U.Semi, Theme.Mu), U.T(value, 24, U.Bold), note is null ? null : U.T(note, 11, null, Theme.Mu2)), U.W(.05), 14, U.Th(16, 12), U.W(.08), 1).With(b => b.MinWidth = 190));
            Tile("Konta", $"{s.Long("users")}", $"+{s.Long("usersToday")} dziś");
            Tile("Teraz w sieci", $"{s.Long("online")}");
            Tile("Serwery", $"{s.Long("servers")}");
            Tile("Wiadomości", $"{s.Long("messages")}", $"+{s.Long("messagesToday")} dziś");
            Tile("W rozmowach", $"{s.Long("voiceNow")}", $"{s.Long("streamsNow")} transmisji");
            Tile("Pliki", Format.Size(s.Long("uploadsBytes") ?? 0));
            Tile("Baza danych", Format.Size(s.Long("dbBytes") ?? 0));
            var up = TimeSpan.FromSeconds(s.Long("uptimeSec") ?? 0);
            Tile("Serwer działa", up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min", "wersja " + s.Str("version"));
            p.Add(tiles);
        }
        catch (Exception ex) { Fail(p, ex); }
    }

    // ───────────────────────── Konta ─────────────────────────

    void Users()
    {
        Title("Konta");
        var search = new TextBox { Text = _q, PlaceholderText = "Szukaj: nazwa, e-mail, IP" };
        var filters = U.H(6);
        foreach (var (k, label) in new[] { ("all", "Wszystkie"), ("online", "W sieci"), ("suspended", "Zawieszone"), ("admins", "Admini") })
        {
            var on = _filter == k;
            var b = new Button { Content = U.T(label, 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(12, 4) };
            b.Click += (_, _) => { _filter = k; Draw(); };
            filters.Add(b);
        }
        _body.Add(search);
        _body.Add(filters);
        var p = Spinner();
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(400);
        t.IsRepeating = false;
        t.Tick += (_, _) => { _q = search.Text; _ = FillUsers(p, 0); };
        search.TextChanged += (_, _) => { t.Stop(); t.Start(); };
        _ = FillUsers(p, 0);
    }

    async Task FillUsers(StackPanel p, int offset)
    {
        try
        {
            var r = await Api.GetJson($"admin/users?q={Uri.EscapeDataString(_q)}&filter={_filter}&offset={offset}&limit=50");
            if (offset == 0) p.Children.Clear(); else if (p.Children.Count > 0 && p.Children[^1] is Button) p.Children.RemoveAt(p.Children.Count - 1);
            var total = (int)(r.Long("total") ?? 0);
            if (offset == 0) p.Add(U.T($"{total} {Format.Plural(total, "konto", "konta", "kont")}", 12, U.Semi, Theme.Mu));
            var n = 0;
            foreach (var u in r.Prop("users").EnumerateArray())
            {
                n++;
                var id = u.Long("id") ?? 0;
                var user = u.To<User>()!;
                var sus = u.Bool("suspended") == true;
                var badges = U.H(6, u.Bool("isAdmin") == true ? U.Box(U.T("admin", 10.5, U.Bold, Theme.OnAccent), U.B("#f2a93b"), 6, U.Th(6, 1)) : null,
                    sus ? U.Box(U.T(u.Long("suspendedUntil") is long su ? "zawieszone do " + Format.When(su) : "zawieszone", 10.5, U.Bold, U.B("#ffffff")), Theme.Red2, 6, U.Th(6, 1)) : null,
                    user.Online ? U.Box(U.T("w sieci", 10.5, U.Bold, Theme.OnAccent), Theme.Green, 6, U.Th(6, 1)) : null);
                var meta = $"@{user.Username} · {u.Str("email") ?? "bez e-maila"} · {u.Long("messages") ?? 0} wiad. · {u.Long("servers") ?? 0} serw. · od {Format.Local(user.CreatedAt):d MMM yyyy}"
                    + (u.Long("lastSeen") is long ls ? " · ostatnio " + Format.When(ls) : "") + (u.Str("lastIp") is { } ip ? " · " + ip : "");
                var g = new Grid { Padding = U.Th(12, 8) }.Cols("auto", "*", "auto").Add(
                    new Avatar(36).SetRaw(user.Name, Store.ColorOfUser(user), Api.Abs(user.Avatar), null).At(0, 0),
                    U.V(2, U.H(8, U.T(user.Name, 14, U.Bold), badges), U.T(meta, 11.5, null, Theme.Mu, wrap: true),
                        sus && u.Str("suspendReason") is { Length: > 0 } why ? U.T("Powód: " + why, 11.5, null, Theme.Red, wrap: true) : null).With(x => x.Margin = U.Th(12, 0)).At(0, 1));
                var more = U.IconButton("", "Akcje", () => { }, 14, 32);
                more.Flyout = UserActions(id, user, u.Bool("isAdmin") == true, sus);
                g.Add(more.At(0, 2));
                p.Add(new Border { Child = g, Background = U.W(.035), CornerRadius = new CornerRadius(10) });
            }
            if (offset + n < total) p.Add(U.Ghost("Więcej", () => _ = FillUsers(p, offset + n), 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Stretch));
        }
        catch (Exception ex) { Fail(p, ex); }
    }

    MenuFlyout UserActions(long id, User u, bool admin, bool suspended)
    {
        var f = new MenuFlyout();
        f.Items.Add(M.Item("Profil", "", () => { if (!S.Users.ContainsKey(id)) S.Users[id] = u; _sh.ShowProfile(id, this); }));
        if (id != S.Me.Id) f.Items.Add(M.Item("Napisz wiadomość", "", () => { _sh.CloseAdmin(); _sh.OpenDmWith(id); }));
        f.Items.Add(new MenuFlyoutSeparator());
        if (suspended) f.Items.Add(M.Item("Zdejmij zawieszenie", "", async () => { if (await Run(() => Api.DeleteJson($"admin/users/{id}/suspend"), "Zawieszenie zdjęte")) Draw(); }));
        else if (!admin && id != S.Me.Id) f.Items.Add(M.Item("Zawieś konto…", "", async () =>
        {
            var hours = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var vals = new[] { 1, 24, 72, 168, 720, 0 };
            foreach (var h in vals) hours.Items.Add(h switch { 0 => "Bezterminowo", 1 => "1 godzina", 24 => "1 dzień", 72 => "3 dni", 168 => "Tydzień", _ => "30 dni" });
            hours.SelectedIndex = 1;
            var reason = new TextBox { PlaceholderText = "Powód (zobaczy go przy logowaniu)", MaxLength = 300 };
            var d = new ContentDialog { Title = $"Zawiesić {u.Name}?", Content = U.V(8, U.T("Wyloguje tę osobę wszędzie.", 13, null, Theme.Mu), hours, reason).With(x => x.Width = 360),
                PrimaryButtonText = "Zawieś", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark };
            if (await d.ShowAsync() != ContentDialogResult.Primary) return;
            if (await Run(() => Api.PostJson($"admin/users/{id}/suspend", new { hours = vals[Math.Max(0, hours.SelectedIndex)], reason = reason.Text.Trim() }), "Konto zawieszone")) Draw();
        }, danger: true));
        f.Items.Add(M.Item("Wyloguj wszędzie", "", async () =>
        {
            if (await M.Confirm(_sh, "Wylogować wszędzie?", $"{u.Name} będzie musiał(a) zalogować się ponownie na każdym urządzeniu.", "Wyloguj"))
                await Run(() => Api.PostJson($"admin/users/{id}/logout-all"), "Wylogowano ze wszystkich urządzeń");
        }));
        f.Items.Add(M.Item("Ustaw nowe hasło…", "", async () =>
        {
            var pass = new TextBox { PlaceholderText = "Nowe hasło (co najmniej 8 znaków)" };
            var d = new ContentDialog { Title = $"Nowe hasło dla {u.Name}", Content = U.V(8, U.T("Wyloguje tę osobę wszędzie. Przekaż jej hasło bezpiecznie.", 13, null, Theme.Mu, wrap: true), pass).With(x => x.Width = 360),
                PrimaryButtonText = "Ustaw", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark };
            if (await d.ShowAsync() == ContentDialogResult.Primary) await Run(() => Api.PostJson($"admin/users/{id}/password", new { newPassword = pass.Text }), "Hasło ustawione");
        }));
        f.Items.Add(M.Item("Wyłącz logowanie dwuetapowe", "", async () =>
        {
            if (await M.Confirm(_sh, "Wyłączyć 2FA?", $"Gdy {u.Name} zgubił(a) telefon. Dostanie maila o zmianie.", "Wyłącz"))
                await Run(() => Api.PostJson($"admin/users/{id}/mfa-reset"), "2FA wyłączone");
        }));
        if (id != S.Me.Id)
            f.Items.Add(M.Item(admin ? "Odbierz uprawnienia admina" : "Nadaj uprawnienia admina", "", async () =>
            {
                if (!await M.Confirm(_sh, admin ? "Odebrać admina?" : "Nadać admina?", admin ? $"{u.Name} straci dostęp do panelu admina." : $"{u.Name} dostanie pełny dostęp do panelu admina GibCorda.", admin ? "Odbierz" : "Nadaj")) return;
                if (await Run(() => Api.PatchJson($"admin/users/{id}", new { isAdmin = !admin }), "Zapisano")) Draw();
            }, danger: !admin));
        return f;
    }

    // ───────────────────────── Serwery ─────────────────────────

    void Servers()
    {
        Title("Serwery", "Podgląd jest tylko do odczytu i trafia do dziennika. Rozmów prywatnych nie da się podglądać.");
        var search = new TextBox { Text = _q, PlaceholderText = "Szukaj serwera" };
        _body.Add(search);
        var p = Spinner();
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(400);
        t.IsRepeating = false;
        t.Tick += (_, _) => { _q = search.Text; _ = FillServers(p); };
        search.TextChanged += (_, _) => { t.Stop(); t.Start(); };
        _ = FillServers(p);
    }

    async Task FillServers(StackPanel p)
    {
        try
        {
            var r = await Api.GetJson($"admin/servers?q={Uri.EscapeDataString(_q)}&limit=100");
            Users(r.Prop("users"));
            p.Children.Clear();
            foreach (var s in r.Prop("servers").EnumerateArray())
            {
                var id = s.Long("id") ?? 0;
                var name = s.Str("name") ?? "";
                var owner = s.Long("ownerId") ?? 0;
                var meta = $"właściciel: {S.NameOf(owner)} · {s.Long("memberCount")} os. · {s.Long("channelCount")} kan. · {s.Long("messageCount")} wiad. · od {Format.Local(s.Long("createdAt") ?? 0):d MMM yyyy}"
                    + (s.Long("lastActivity") is long la ? " · ruch " + Format.When(la) : "");
                var g = new Grid { Padding = U.Th(12, 8) }.Cols("auto", "*", "auto").Add(
                    new Avatar(36).SetRaw(name, "#7cc7ff", Api.Abs(s.Str("icon")), null).At(0, 0),
                    U.V(2, U.T(name, 14, U.Bold), U.T(meta, 11.5, null, Theme.Mu, wrap: true)).With(x => x.Margin = U.Th(12, 0)).At(0, 1));
                var more = U.IconButton("", "Akcje", () => { }, 14, 32);
                var f = new MenuFlyout();
                f.Items.Add(M.Item("Podgląd kanałów", "", () => Preview(id, name)));
                f.Items.Add(M.Item("Przekaż innej osobie…", "", async () =>
                {
                    var full = await Api.GetJson($"admin/servers/{id}");
                    Users(full.Prop("users"));
                    var members = full.Prop("server").Prop("members").EnumerateArray().Select(m => m.Long("userId") ?? 0).Where(x => x != owner).ToList();
                    var pick = await People.Pick(_sh, $"Nowy właściciel: {name}", 1, n => n == 0 ? "Wybierz osobę" : "Przekaż", null, members);
                    if (pick is { Count: 1 }) await Run(() => Api.PostJson($"admin/servers/{id}/transfer", new { userId = pick[0] }), "Serwer przekazany");
                }));
                f.Items.Add(M.Item("Usuń serwer…", "", async () =>
                {
                    var reason = new TextBox { PlaceholderText = "Powód (do dziennika)" };
                    var d = new ContentDialog { Title = $"Usunąć {name}?", Content = U.V(8, U.T("Wszystkie kanały i wiadomości znikną. Tego nie da się cofnąć.", 13, null, Theme.Red, wrap: true), reason).With(x => x.Width = 360),
                        PrimaryButtonText = "Usuń", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark };
                    if (await d.ShowAsync() == ContentDialogResult.Primary && await Run(() => Api.DeleteJson($"admin/servers/{id}", new { reason = reason.Text.Trim() }), "Serwer usunięty")) Draw();
                }, danger: true));
                more.Flyout = f;
                g.Add(more.At(0, 2));
                p.Add(new Border { Child = g, Background = U.W(.035), CornerRadius = new CornerRadius(10) });
            }
            if (p.Children.Count == 0) p.Add(U.T("Nic nie znaleziono.", 13.5, null, Theme.Mu));
        }
        catch (Exception ex) { Fail(p, ex); }
    }

    async void Preview(long sid, string name)
    {
        JsonElement full;
        try { full = await Api.GetJson($"admin/servers/{sid}"); Users(full.Prop("users")); }
        catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); return; }
        var chans = full.Prop("server").Prop("channels").EnumerateArray().Where(c => c.Str("type") == "text").Select(c => (Id: c.Long("id") ?? 0, Name: c.Str("name") ?? "")).ToList();
        var pick = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in chans) pick.Items.Add("# " + c.Name);
        var list = new StackPanel { Spacing = 6 };
        var armed = new HashSet<long>();
        var seq = 0;
        async void Load()
        {
            var my = ++seq;
            list.Children.Clear();
            if (pick.SelectedIndex < 0) return;
            var cid = chans[pick.SelectedIndex].Id;
            try
            {
                var msgs = await Api.GetJson($"admin/channels/{cid}/messages?limit=50");
                if (my != seq) return; // w międzyczasie wybrano inny kanał
                foreach (var m in msgs.EnumerateArray().Reverse())
                {
                    var mid = m.Long("id") ?? 0;
                    var author = m.Long("authorId") ?? 0;
                    var row = new Grid().Cols("*", "auto").Add(U.V(1, U.H(6, U.T(S.NameOf(author), 12.5, U.Bold), U.T(Format.When(m.Long("createdAt") ?? 0), 11, null, Theme.Mu2)),
                        U.T(m.Str("content") is { Length: > 0 } t ? t : "(załącznik)", 12.5, null, Theme.Fg, wrap: true)).At(0, 0),
                        U.IconButton("", "Usuń wiadomość (moderacja)", async () =>
                        {
                            // potwierdzenie w miejscu (to okienko jest już otwarte — drugiego nie da się pokazać)
                            if (!armed.Remove(mid)) { armed.Add(mid); _sh.Info("Kliknij kosz jeszcze raz, żeby usunąć tę wiadomość u wszystkich."); return; }
                            if (await Run(() => Api.DeleteJson($"admin/messages/{mid}"), "Usunięto")) Load();
                        }, 11, 26).At(0, 1));
                    list.Add(row);
                }
                if (list.Children.Count == 0) list.Add(U.T("Pusto.", 13, null, Theme.Mu));
            }
            catch (Exception ex) { list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
        }
        pick.SelectionChanged += (_, _) => Load();
        if (chans.Count > 0) pick.SelectedIndex = 0;
        var d = new ContentDialog
        {
            Title = $"Podgląd: {name}", Content = U.V(10, pick, new ScrollViewer { Content = list, Height = 420 }).With(x => x.Width = 520), CloseButtonText = "Zamknij",
            XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        await d.ShowAsync();
    }

    // ───────────────────────── Zgłoszenia, dziennik, ogłoszenie ─────────────────────────

    string _rep = "open";

    void Reports()
    {
        Title("Zgłoszenia", "Wszystkie — także z rozmów prywatnych (tylko kopia zgłoszonej wiadomości).");
        var tabs = U.H(6);
        foreach (var (k, label) in new[] { ("open", "Otwarte"), ("resolved", "Załatwione"), ("dismissed", "Odrzucone") })
        {
            var on = _rep == k;
            var b = new Button { Content = U.T(label, 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(12, 4) };
            b.Click += (_, _) => { _rep = k; Draw(); };
            tabs.Add(b);
        }
        _body.Add(tabs);
        var p = Spinner();
        _ = FillReports(p);
    }

    async Task FillReports(StackPanel p)
    {
        try
        {
            var r = await Api.GetJson($"admin/reports?status={_rep}");
            Users(r.Prop("users"));
            p.Children.Clear();
            foreach (var rep in r.Prop("reports").EnumerateArray())
            {
                var id = rep.Long("id") ?? 0;
                var sn = rep.Prop("snapshot");
                var author = sn.Long("authorId") ?? 0;
                var where = sn.Str("serverName") is { } sv ? $"{sv} · #{sn.Str("channelName")}" : "rozmowa prywatna";
                var body = U.V(4, U.H(8, U.Box(U.T(rep.Str("reason") ?? "", 10.5, U.Bold, Theme.OnAccent), Theme.Yellow, 6, U.Th(6, 1)), U.T(where + " · " + Format.When(rep.Long("createdAt") ?? 0), 12, null, Theme.Mu)),
                    U.T(S.NameOf(author), 13.5, U.Bold), U.T(sn.Str("content") is { Length: > 0 } c ? c : "(załącznik)", 13, null, Theme.Fg, wrap: true),
                    rep.Str("note") is { Length: > 0 } n ? U.T("Notatka: " + n, 12, null, Theme.Mu, wrap: true) : null);
                var actions = U.H(8);
                if (_rep == "open")
                {
                    actions.Add(U.Ghost("Załatwione", async () => { if (await Run(() => Api.PatchJson($"reports/{id}", new { status = "resolved" }))) Draw(); }, 30));
                    actions.Add(U.Ghost("Odrzuć", async () => { if (await Run(() => Api.PatchJson($"reports/{id}", new { status = "dismissed" }))) Draw(); }, 30));
                }
                p.Add(new Border { Child = U.V(10, body, actions), Background = U.W(.045), CornerRadius = new CornerRadius(12), Padding = U.Th(16, 12) });
            }
            if (p.Children.Count == 0) p.Add(U.T(_rep == "open" ? "Brak otwartych zgłoszeń. 🎉" : "Pusto.", 13.5, null, Theme.Mu));
        }
        catch (Exception ex) { Fail(p, ex); }
    }

    void Log()
    {
        Title("Dziennik admina", "Każda akcja i każdy podgląd w panelu.");
        var p = Spinner();
        _ = FillLog(p);
    }

    async Task FillLog(StackPanel p)
    {
        try
        {
            var r = await Api.GetJson("admin/log?limit=150");
            Users(r.Prop("users"));
            p.Children.Clear();
            foreach (var e in r.Prop("entries").EnumerateArray())
            {
                var det = e.Prop("details");
                var detText = det.ValueKind == JsonValueKind.Object ? string.Join(" · ", det.EnumerateObject().Take(4).Select(x => $"{x.Name}: {(x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.ToString())}")) : "";
                if (detText.Length > 160) detText = detText[..160] + "…";
                p.Add(new Border
                {
                    Background = U.W(.035), CornerRadius = new CornerRadius(10), Padding = U.Th(12, 8),
                    Child = new Grid().Cols("*", "auto").Add(U.V(2, U.H(6, U.T(S.NameOf(e.Long("adminId") ?? 0), 13, U.Bold), U.T((e.Str("action") ?? "").Replace('_', ' '), 13, U.Semi, Theme.Accent),
                        U.T($"{e.Str("targetType")} {e.Long("targetId")}", 12, null, Theme.Mu)), detText.Length > 0 ? U.T(detText, 11.5, null, Theme.Mu2, wrap: true) : null).At(0, 0),
                        U.T(Format.When(e.Long("createdAt") ?? 0), 11.5, null, Theme.Mu2).At(0, 1)),
                });
            }
            if (p.Children.Count == 0) p.Add(U.T("Pusto.", 13.5, null, Theme.Mu));
        }
        catch (Exception ex) { Fail(p, ex); }
    }

    void Announce()
    {
        Title("Ogłoszenie", "Pasek u góry u wszystkich (strona, telefon, aplikacje) przez 24 godziny.");
        var cur = S.Announcement;
        if (cur?.Text is { Length: > 0 } t) _body.Add(U.Box(U.V(6, U.T("TERAZ WIDAĆ", 11, U.Bold, Theme.Mu2), U.T(t, 14, U.Semi, wrap: true),
            U.Ghost("Usuń ogłoszenie", async () => { if (await Run(() => Api.DeleteJson("admin/announce"), "Ogłoszenie usunięte")) Draw(); }, 30).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)), U.W(.05), 12, U.Th(16, 12)));
        var text = new TextBox { PlaceholderText = "np. Dziś o 22:00 krótka przerwa techniczna (ok. 5 minut).", MaxLength = 300, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
        var warn = new CheckBox { Content = "Ważne (czerwony pasek)" };
        _body.Add(text);
        _body.Add(warn);
        _body.Add(U.Primary("Wyślij do wszystkich", async () =>
        {
            if (text.Text.Trim().Length == 0) return;
            if (!await M.Confirm(_sh, "Wysłać ogłoszenie?", "Zobaczą je wszyscy użytkownicy GibCorda.", "Wyślij")) return;
            if (await Run(() => Api.PostJson("admin/announce", new { text = text.Text.Trim(), level = warn.IsChecked == true ? "warn" : "info" }), "Ogłoszenie wysłane")) Draw();
        }, 40).With(b => { b.Width = 220; b.HorizontalAlignment = HorizontalAlignment.Left; }));
    }
}
