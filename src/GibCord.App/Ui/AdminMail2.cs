using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

/// <summary>Poczta → „Wysyłka i adresy” (limity, mail testowy, niepotwierdzone adresy, dziennik) i „Skrzynki i podpisy” — jak na stronie.</summary>
public sealed partial class AdminMail
{
    readonly Grid _tools = new() { Visibility = Visibility.Collapsed };
    string _view = "folder";
    JsonElement _meta;
    string _logFilter = "all";
    long? _logNext;
    readonly List<JsonElement> _log = [];

    static readonly Dictionary<string, string> Kinds = new()
    {
        ["verify"] = "Potwierdzenie adresu", ["reset"] = "Reset hasła", ["test"] = "Mail testowy", ["panel"] = "Z panelu",
        ["login"] = "Nowe logowanie", ["security"] = "Bezpieczeństwo konta",
    };

    void ShowTool(string view)
    {
        _view = view;
        _cur = null;
        _thread = [];
        _openVer++;
        DrawLeft();
        Layout();
        _tools.Children.Clear();
        _tools.Children.Add(view == "log" ? LogView() : BoxesView());
    }

    static FrameworkElement Card(params UIElement?[] items)
    {
        var p = new StackPanel { Spacing = 10 };
        foreach (var i in items) if (i is not null) p.Children.Add(i);
        return new Border { Child = p, Padding = U.Th(16, 14), CornerRadius = new CornerRadius(12), Background = U.W(.04), BorderBrush = Theme.Line, BorderThickness = U.Th(1) };
    }

    static FrameworkElement Tile(string value, string label, string? sub = null, double? frac = null)
    {
        var p = U.V(2, U.T(value, 20, U.Bold), U.T(label, 12.5, U.Semi, Theme.Mu));
        if (frac is { } f)
        {
            var bar = new Grid { Height = 6, CornerRadius = new CornerRadius(3), Background = U.W(.1), Margin = U.Th(0, 6, 0, 0) };
            bar.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Width = 178 * Math.Clamp(f, 0, 1), Background = f >= .95 ? Theme.Red : f >= .75 ? Theme.Yellow : Theme.Green });
            p.Children.Add(bar);
        }
        if (sub is not null) p.Children.Add(U.T(sub, 11, null, Theme.Mu2));
        return new Border { Width = 210, Padding = U.Th(14, 10), CornerRadius = new CornerRadius(12), Background = U.W(.06), BorderBrush = Theme.Line, BorderThickness = U.Th(1), Child = p };
    }

    // ───────────────────────── Wysyłka i adresy ─────────────────────────

    FrameworkElement LogView()
    {
        var body = new StackPanel { Spacing = 14, Padding = U.Th(20, 16, 20, 24) };
        body.Children.Add(U.T("Wysyłka i adresy", 20, U.Bold));
        var q = _meta.Prop("quota");
        long Q(string per, string k) => q.Prop(per).Long(k) ?? 0;
        var daySent = Q("day", "sent"); var dayLim = Math.Max(1, q.Prop("day").Long("limit") ?? 100);
        var monSent = Q("month", "sent"); var monLim = Math.Max(1, q.Prop("month").Long("limit") ?? 3000);
        var st = _meta.Prop("stats");
        var unvCount = U.T("…", 20, U.Bold);
        var tiles = new WrapPanel { Spacing = 10 };
        tiles.Children.Add(Tile($"{daySent} / {dayLim}", "wysłanych dziś", null, (double)daySent / dayLim));
        tiles.Children.Add(Tile($"{monSent} / {monLim}", "w tym miesiącu", null, (double)monSent / monLim));
        tiles.Children.Add(Tile((st.Long("failed7d") ?? 0).ToString(), "nieudanych (7 dni)", $"z {st.Long("sent7d") ?? 0} wysłanych"));
        tiles.Children.Add(new Border { Width = 210, Padding = U.Th(14, 10), CornerRadius = new CornerRadius(12), Background = U.W(.06), BorderBrush = Theme.Line, BorderThickness = U.Th(1), Child = U.V(2, unvCount, U.T("niepotwierdzonych adresów", 12.5, U.Semi, Theme.Mu)) });
        body.Children.Add(tiles);
        body.Children.Add(U.T("Darmowy plan Resend: 100 maili dziennie i 3000 miesięcznie (licznik zeruje się o północy UTC). Z panelu można wysłać najwyżej 95 dziennie — reszta zostaje na potwierdzenia i reset hasła.", 12, null, Theme.Mu2, wrap: true));
        body.Children.Add(U.Ghost("Wyślij mail testowy…", () => _ = TestMail(), 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Left));

        var unv = new StackPanel { Spacing = 4 };
        unv.Children.Add(U.T("Wczytywanie…", 12.5, null, Theme.Mu));
        body.Children.Add(Card(U.T("Niepotwierdzone adresy", 15, U.Bold), unv));
        _ = LoadUnverified(unv, unvCount);

        var logList = new StackPanel { Spacing = 2 };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void Filters()
        {
            filters.Children.Clear();
            foreach (var (k, n) in new[] { ("all", "Wszystkie"), ("failed", "Nieudane") })
            {
                var key = k;
                var on = _logFilter == k;
                var b = new Button { Content = U.T(n, 12, U.Semi, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(8), Padding = U.Th(10, 3) };
                b.Click += (_, _) => { _logFilter = key; Filters(); _ = LoadLog(logList, false); };
                filters.Children.Add(b);
            }
        }
        Filters();
        body.Children.Add(Card(U.H(12, U.T("Dziennik wysyłki", 15, U.Bold), filters), logList));
        _ = LoadLog(logList, false);
        return U.Scroll(body);
    }

    async Task LoadUnverified(StackPanel host, TextBlock count)
    {
        try
        {
            var l = await Api.GetJson("admin/mail/unverified");
            var items = l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().ToList() : [];
            count.Text = items.Count.ToString();
            host.Children.Clear();
            if (items.Count == 0) { host.Children.Add(U.T("Wszyscy z adresem e-mail już go potwierdzili.", 12.5, null, Theme.Mu)); return; }
            foreach (var x in items)
            {
                var u = x.Prop("user");
                if (u.To<User>() is { } usr && !_sh.S.Users.ContainsKey(usr.Id)) _sh.S.Users[usr.Id] = usr;
                var uid = u.Long("id") ?? 0;
                var reg = x.Long("registeredAt") ?? u.Long("createdAt") ?? 0;
                var sent = x.Long("lastSentAt");
                var info = $"{x.Str("email")} · konto od {Local(reg):d MMM yyyy}" + (sent is long ls ? $" · link wysłany {Format.When(ls)}" : " · link jeszcze nie wysłany");
                var row = new Grid { ColumnSpacing = 6, Padding = U.Th(4, 4) }.Cols("auto", "*", "auto", "auto").Add(
                    new Avatar(28).Set(_sh.S, Api, uid, null).At(0, 0),
                    U.V(0, U.T(_sh.S.NameOf(uid), 13, U.Bold), U.T(info, 11, null, Theme.Mu2)).At(0, 1),
                    U.Ghost("Wyślij link ponownie", async () =>
                    {
                        try { await Api.PostJson($"admin/users/{uid}/email/resend", new { }); _sh.Info("Wysłano link ponownie"); await LoadUnverified(host, count); }
                        catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
                    }, 28).At(0, 2),
                    U.Ghost("Potwierdź ręcznie", async () =>
                    {
                        if (!await M.Confirm(_sh, "Potwierdzić ręcznie?", "Adres zostanie oznaczony jako potwierdzony bez klikania w link. Rób to tylko, jeśli wiesz, że należy do tej osoby.", "Potwierdź")) return;
                        try { await Api.PostJson($"admin/users/{uid}/email/verify", new { }); _sh.Info("Potwierdzono"); await LoadUnverified(host, count); }
                        catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
                    }, 28).At(0, 3));
                host.Children.Add(row);
            }
        }
        catch (Exception ex) { host.Children.Clear(); host.Children.Add(U.T(GibCordClient.Friendly(ex), 12.5, null, Theme.Red, wrap: true)); }
    }

    async Task LoadLog(StackPanel host, bool more)
    {
        try
        {
            var q = new List<string> { "limit=50" };
            if (_logFilter == "failed") { q.Add("failed=1"); q.Add("since=" + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 30L * 86_400_000)); }
            if (more && _logNext is long b) q.Add("before=" + b);
            var r = await Api.GetJson("admin/mail/log?" + string.Join('&', q));
            var entries = r.Prop("entries").ValueKind == JsonValueKind.Array ? r.Prop("entries").EnumerateArray().ToList() : [];
            if (!more) _log.Clear();
            _log.AddRange(entries);
            _logNext = entries.Count >= 50 ? entries[^1].Long("id") : null;
            host.Children.Clear();
            if (_log.Count == 0) { host.Children.Add(U.T(_logFilter == "failed" ? "Brak nieudanych wysyłek w ostatnich 30 dniach." : "Jeszcze nic nie wysłano.", 12.5, null, Theme.Mu)); return; }
            host.Children.Add(new Grid { ColumnSpacing = 10, Padding = U.Th(4, 2) }.Cols("110", "150", "*", "*", "160").Add(
                U.T("Kiedy", 11, U.Bold, Theme.Mu2).At(0, 0), U.T("Rodzaj", 11, U.Bold, Theme.Mu2).At(0, 1), U.T("Do", 11, U.Bold, Theme.Mu2).At(0, 2),
                U.T("Temat", 11, U.Bold, Theme.Mu2).At(0, 3), U.T("Wynik", 11, U.Bold, Theme.Mu2).At(0, 4)));
            foreach (var x in _log)
            {
                var ok = x.Prop("ok").ValueKind == JsonValueKind.True;
                var errText = x.Str("error") ?? "błąd";
                var kind = x.Str("kind") ?? "";
                var res = U.T(ok ? "✓ wysłano" : "✕ " + (errText.Length > 60 ? errText[..60] : errText), 11.5, U.Semi, ok ? Theme.Green : Theme.Red);
                if (!ok) ToolTipService.SetToolTip(res, errText);
                host.Children.Add(new Grid { ColumnSpacing = 10, Padding = U.Th(4, 3), Background = U.W(.03), CornerRadius = new CornerRadius(6) }.Cols("110", "150", "*", "*", "160").Add(
                    U.T(Local(x.Long("createdAt") ?? 0).ToString("d MMM HH:mm", Pl), 11.5, null, Theme.Mu).At(0, 0),
                    U.T(Kinds.GetValueOrDefault(kind, kind), 11.5, null, Theme.Fg).At(0, 1),
                    U.T(x.Str("to") ?? "", 11.5, null, Theme.Fg).At(0, 2),
                    U.T(x.Str("subject") ?? "", 11.5, null, Theme.Mu).At(0, 3),
                    res.At(0, 4)));
            }
            if (_logNext is not null) host.Children.Add(U.Ghost("Starsze", () => _ = LoadLog(host, true), 28).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = U.Th(0, 6, 0, 0); }));
        }
        catch (Exception ex) { host.Children.Clear(); host.Children.Add(U.T(GibCordClient.Friendly(ex), 12.5, null, Theme.Red, wrap: true)); }
    }

    async Task TestMail()
    {
        var to = new TextBox { PlaceholderText = "adres@example.com", Text = _sh.S.Me.Email ?? "", Width = 360 };
        var err = U.T("", 12, null, Theme.Red, wrap: true);
        var d = new ContentDialog
        {
            Title = "Mail testowy", PrimaryButtonText = "Wyślij test", CloseButtonText = "Anuluj", XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
            Content = U.V(10, U.T("Wyślemy krótki mail w wyglądzie GibCorda, żeby sprawdzić, czy dochodzi (zajrzyj też do spamu).", 12.5, null, Theme.Mu, wrap: true), to, err),
        };
        var sending = false;
        d.PrimaryButtonClick += async (_, e) =>
        {
            if (sending) { e.Cancel = true; return; }
            var def = e.GetDeferral();
            sending = true;
            d.IsPrimaryButtonEnabled = false;
            try { await Api.PostJson("admin/mail/test", new { to = to.Text.Trim() }); _sh.Info("Wysłano mail testowy ✓"); }
            catch (Exception ex) { err.Text = GibCordClient.Friendly(ex); e.Cancel = true; }
            finally { sending = false; d.IsPrimaryButtonEnabled = true; def.Complete(); }
        };
        await M.WaitNoDialog(XamlRoot);
        if (await d.ShowAsync() == ContentDialogResult.Primary) { await LoadMeta(); if (_view == "log") ShowTool("log"); }
    }

    // ───────────────────────── Skrzynki i podpisy ─────────────────────────

    static List<string> ParseAliases(string s) =>
        s.Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(x => (x.Contains('@') ? x[..x.IndexOf('@')] : x).Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();

    FrameworkElement BoxesView()
    {
        var body = new StackPanel { Spacing = 14, Padding = U.Th(20, 16, 20, 24) };
        body.Children.Add(U.T("Skrzynki i podpisy", 20, U.Bold));
        var boxes = _meta.Prop("boxes").ValueKind == JsonValueKind.Array ? _meta.Prop("boxes").EnumerateArray().ToList() : [];
        foreach (var b in boxes)
        {
            var id = b.Long("id") ?? 0;
            var system = b.Prop("system").ValueKind == JsonValueKind.True;
            var total = b.Long("total") ?? 0;
            var address = b.Str("address") ?? "";
            var name = new TextBox { Header = "Nazwa nadawcy", Text = b.Str("displayName") ?? "", PlaceholderText = "np. GibCord", MaxLength = 60 };
            var aliases = new TextBox
            {
                Header = "Dodatkowe adresy (aliasy)", PlaceholderText = "np. postmaster, abuse",
                Text = b.Prop("aliases").ValueKind == JsonValueKind.Array ? string.Join(", ", b.Prop("aliases").EnumerateArray().Select(x => x.GetString())) : "",
            };
            var sig = new TextBox { Header = "Podpis", Text = b.Str("signature") ?? "", PlaceholderText = "np. Pozdrawiamy, ekipa GibCorda", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
            var buttons = new Grid().Cols("*", "auto").Add(
                system ? null : U.Ghost("Usuń skrzynkę", async () =>
                {
                    var what = total > 0 ? $", a {total} {Format.Plural((int)Math.Min(total, int.MaxValue), "wiadomość zostanie usunięta", "wiadomości zostaną usunięte", "wiadomości zostanie usuniętych")} na zawsze" : "";
                    if (!await M.Confirm(_sh, "Usunąć skrzynkę?", $"Adres {address} przestanie odbierać pocztę{what}.", "Usuń")) return;
                    try
                    {
                        await Api.DeleteJson($"admin/mail/boxes/{id}" + (total > 0 ? "?force=1" : ""));
                        _sh.Info("Usunięto skrzynkę");
                        if (_box == id) _box = 0;
                        await LoadMeta();
                        ShowTool("boxes");
                    }
                    catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
                }, 30).At(0, 0),
                U.Primary("Zapisz", async () =>
                {
                    try
                    {
                        await Api.PatchJson($"admin/mail/boxes/{id}", new { displayName = name.Text.Trim(), signature = sig.Text.Replace("\r\n", "\n").Replace('\r', '\n'), aliases = ParseAliases(aliases.Text) });
                        _sh.Info("Zapisano");
                        await LoadMeta();
                        ShowTool("boxes");
                    }
                    catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
                }, 32).At(0, 1));
            body.Children.Add(Card(
                U.H(8, U.I("", 15), U.T(address, 14, U.Bold), system ? U.Box(U.T("systemowa", 10, U.Bold, Theme.Mu), U.W(.08), 5, U.Th(5, 1)) : null,
                    U.T($"{total} {Format.Plural((int)Math.Min(total, int.MaxValue), "wiadomość", "wiadomości", "wiadomości")}", 11.5, null, Theme.Mu2)),
                system ? U.T("Z tego adresu wychodzą maile GibCorda (potwierdzenia, reset hasła). Tu trafiają też odpowiedzi na nie. Nie da się jej usunąć.", 12, null, Theme.Mu2, wrap: true) : null,
                name, aliases, U.T("Poczta na aliasy trafi do tej skrzynki. Wpisz samą część przed @.", 11, null, Theme.Mu2), sig, buttons));
        }
        // nowa skrzynka
        var lp = new TextBox { Header = "Adres", PlaceholderText = "np. hubert", MaxLength = 40, Width = 220 };
        var dn = new TextBox { Header = "Nazwa nadawcy", PlaceholderText = "np. Hubert z GibCorda", MaxLength = 60, Width = 260 };
        body.Children.Add(Card(U.H(8, U.I("", 15), U.T("Nowa skrzynka", 14, U.Bold)),
            U.H(10, lp, U.T("@" + _domain, 13, U.Semi, Theme.Mu).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = U.Th(0, 0, 0, 8); }), dn),
            U.Primary("Utwórz", async () =>
            {
                var local = lp.Text.Trim().ToLowerInvariant();
                if (local.Contains('@')) local = local[..local.IndexOf('@')];
                if (local.Length == 0) { _sh.Info("Wpisz adres", true); return; }
                try
                {
                    var r = await Api.PostJson("admin/mail/boxes", new Dictionary<string, object?> { ["localPart"] = local, ["displayName"] = dn.Text.Trim().Length > 0 ? dn.Text.Trim() : null });
                    _sh.Info("Utworzono " + r.Str("address"));
                    await LoadMeta();
                    ShowTool("boxes");
                }
                catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
            }, 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Right)));
        return U.Scroll(body);
    }
}
