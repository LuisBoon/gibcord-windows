using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using User = GibCord.Core.User;

namespace GibCord.App.Ui;

/// <summary>
/// Ustawienia serwera na całe okno: przegląd, kanały (z kategoriami, kolejnością i uprawnieniami), role, członkowie,
/// bany, emoji / naklejki / dźwięki, automoderacja, dziennik, zgłoszenia. Zakładki zależą od uprawnień.
/// </summary>
public sealed partial class ServerSettingsPage : Grid
{
    readonly Shell _sh;
    readonly long _sid;
    readonly StackPanel _nav = new() { Spacing = 2, Padding = U.Th(12, 24, 12, 24) };
    readonly StackPanel _body = new() { Spacing = 14, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left, Padding = U.Th(40, 28, 40, 60) };
    readonly ScrollViewer _sv;
    string _section;
    bool _redrawPending;

    Store S => _sh.S;
    GibCordApi Api => _sh.C.Api;
    Server? Srv => S.ServerById(_sid);

    public ServerSettingsPage(Shell sh, long serverId, string section, long? selectRole = null)
    {
        _sh = sh;
        _sid = serverId;
        _section = section;
        _role = selectRole;
        Background = Theme.Page;
        this.Cols("250", "*", "auto");
        _sv = U.Scroll(_body);
        var close = U.IconButton(G.Close, "Zamknij (Esc)", sh.CloseServerSettings, 16, 40);
        close.Margin = U.Th(0, 20, 24, 0);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Background = U.W(.08);
        close.CornerRadius = new CornerRadius(20);
        this.Add(new Border { Background = U.W(.03), Child = U.Scroll(_nav) }.At(0, 0), _sv.At(0, 1), close.At(0, 2));
        KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; sh.CloseServerSettings(); } };
        S.Changed += OnChange;
        Unloaded += (_, _) => S.Changed -= OnChange;
        Draw();
    }

    void OnChange(StoreChange c)
    {
        if (c.Kind is ChangeKind.Servers && Srv is null) { _sh.CloseServerSettings(); return; }
        OnBridgeChange(c); // 6.4: kanał połączył się z Discordem
        // zmiany serwera z innych miejsc (albo potwierdzenie naszej) — przerysuj listy, ale nie formularze w trakcie pisania
        // edytor roli z niezapisanymi zmianami nie przerysowuje się sam (zapis i tak odświeża widok)
        if (_section == "roles" && _role is not null) return;
        if (c.Kind is ChangeKind.Server or ChangeKind.Members && c.Id == _sid && _section is "channels" or "roles" or "members" or "emoji" && !_redrawPending)
        {
            _redrawPending = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _redrawPending = false; if (_section is "channels" or "roles" or "members" or "emoji") DrawBody(keepScroll: true); });
        }
    }

    bool Can(long perm) => S.CanServer(Srv, perm);
    bool Owner => Srv?.OwnerId == S.Me.Id;

    void Draw()
    {
        var srv = Srv;
        if (srv is null) return;
        _nav.Children.Clear();
        _nav.Add(U.H(10, new Avatar(36).SetRaw(srv.Name, "#7cc7ff", Api.Abs(srv.Icon), null), U.T(srv.Name, 15, U.Bold).With(t => t.MaxWidth = 170)).With(p => p.Margin = U.Th(10, 0, 0, 10)));
        void Item(string key, string glyph, string label)
        {
            var sel = _section == key;
            var row = U.Row(U.H(10, U.I(glyph, 15, sel ? Theme.Fg : Theme.Mu), U.T(label, 14, sel ? U.Bold : U.Semi, sel ? Theme.Fg : Theme.Mu)), () => { _section = key; Draw(); _sv.ChangeView(null, 0, null, true); }, sel, 8, U.Th(10, 0), U.Th(0, 1));
            row.Height = 38;
            _nav.Add(row);
        }
        if (Can(Perm.ManageServer)) Item("overview", "", "Przegląd");
        if (Can(Perm.ManageChannels)) Item("channels", "", "Kanały i kategorie");
        if (Can(Perm.ManageRoles)) Item("roles", "", "Role");
        if (Can(Perm.Kick) || Can(Perm.Ban) || Can(Perm.Timeout) || Can(Perm.ManageRoles) || Can(Perm.ManageNicknames)) Item("members", "", "Członkowie");
        if (Can(Perm.Ban)) Item("bans", "", "Bany");
        if (Can(Perm.ManageExpressions)) Item("emoji", "", "Emoji, naklejki, dźwięki");
        if (Can(Perm.ManageServer) || Can(Perm.ManageMessages)) Item("automod", "", "Automoderacja");
        if (Can(Perm.ManageServer)) Item("template", "\uE8A5", "Szablon serwera");
        if (Can(Perm.ManageServer) || Can(Perm.ManageChannels)) Item("integrations", "\uE99A", "Integracje"); // 6.4
        if (Can(Perm.ManageMessages)) Item("reports", "", "Zgłoszenia");
        if (Can(Perm.ViewAuditLog)) Item("audit", "", "Dziennik zmian");
        if (Owner)
        {
            _nav.Add(U.HLine(.08).With(l => l.Margin = U.Th(10, 10)));
            _nav.Add(U.Row(U.H(10, U.I("", 15, Theme.Red), U.T("Usuń serwer", 13.5, U.Semi, Theme.Red)), DeleteServer, false, 8, U.Th(10, 0), U.Th(0, 1)).With(r => r.Height = 38));
        }
        DrawBody();
    }

    void DrawBody(bool keepScroll = false)
    {
        var off = _sv.VerticalOffset;
        _body.Children.Clear();
        if (Srv is null) return;
        switch (_section)
        {
            case "channels": Channels(); break;
            case "roles": Roles(); break;
            case "members": Members(); break;
            case "bans": Bans(); break;
            case "emoji": Expressions(); break;
            case "automod": Automod(); break;
            case "reports": Reports(); break;
            case "audit": Audit(); break;
            case "template": Template(); break;
            case "integrations": Integrations(); break;
            default: Overview(); break;
        }
        if (keepScroll) _sv.ChangeView(null, off, null, true);
    }

    // ───────────────────────── pomocniki ─────────────────────────

    void Title(string t, string? sub = null)
    {
        _body.Add(U.T(t, 24, U.Bold));
        if (sub is not null) _body.Add(U.T(sub, 13.5, null, Theme.Mu, wrap: true).With(x => x.Margin = U.Th(0, -8, 0, 4)));
    }

    static TextBlock Label(string t) => U.T(t.ToUpperInvariant(), 11, U.Bold, Theme.Mu2).With(x => x.Margin = U.Th(0, 8, 0, -6));

    static Border Card(params UIElement?[] children) => new()
    {
        Background = U.W(.045), CornerRadius = new CornerRadius(14), Padding = U.Th(18, 16), BorderBrush = U.W(.07), BorderThickness = U.Th(1),
        Child = U.V(10, children),
    };

    void Err(Exception ex) => _sh.Info(GibCordClient.Friendly(ex), true);

    async Task<bool> Run(Func<Task> a, string? ok = null)
    {
        try { await a(); if (ok is not null) _sh.Info(ok); return true; }
        catch (Exception ex) { Err(ex); return false; }
    }

    void ApplyServer(System.Text.Json.JsonElement e)
    {
        if (e.To<Server>() is { Id: > 0 } srv) S.Apply(new GatewayEvent("server_update", System.Text.Json.JsonSerializer.SerializeToElement(new { server = srv }, Json.Options)));
    }

    async Task<(Stream Data, string? Mime, string Name)?> PickFile(params string[] exts)
    {
        var f = await FileBrowser.PickFile(XamlRoot, exts, FileBrowser.Place.Pictures);
        if (f is null) return null;
        return (await f.OpenStreamForReadAsync(), string.IsNullOrEmpty(f.ContentType) ? null : f.ContentType, Path.GetFileNameWithoutExtension(f.Name));
    }

    AppDialog Dialog(string title, UIElement content, string ok, bool danger = false) => new()
    {
        Title = title, Content = new ScrollViewer { Content = content, MaxHeight = 560 }, PrimaryButtonText = ok, CloseButtonText = "Anuluj",
        DefaultButton = danger ? ContentDialogButton.Close : ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
    };

    static SwitchField Toggle(string header, bool on) => new() { Header = U.T(header, 13.5, U.Semi), IsOn = on, OnContent = "Tak", OffContent = "Nie" };

    // ───────────────────────── Przegląd ─────────────────────────

    void Overview()
    {
        var srv = Srv!;
        Title("Przegląd");
        var name = new TextBox { Text = srv.Name, MaxLength = 100 };
        var desc = new TextBox { Text = srv.Description ?? "", PlaceholderText = "O czym jest ten serwer? (widać na karcie zaproszenia)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80, MaxLength = 300 };
        _body.Add(Card(Label("Ikona"), U.H(14, new Avatar(72).SetRaw(srv.Name, "#7cc7ff", Api.Abs(srv.Icon), null), U.V(8,
                U.Ghost("Zmień ikonę", async () =>
                {
                    if (await PickFile(".png", ".jpg", ".jpeg", ".gif", ".webp") is not { } f) return;
                    using var d = f.Data;
                    if (await Run(async () => ApplyServer(await Api.PostImage($"servers/{_sid}/icon", d, f.Mime)), "Ikona zmieniona")) DrawBody();
                }, 34),
                U.T("PNG, JPG, GIF albo WebP, do 10 MB.", 11.5, null, Theme.Mu2))),
            Label("Nazwa serwera"), name, Label("Opis"), desc,
            Label("Baner (na karcie zaproszenia)"), U.H(8,
                U.Ghost("Wgraj baner", async () =>
                {
                    if (await PickFile(".png", ".jpg", ".jpeg", ".gif", ".webp") is not { } f) return;
                    using var d = f.Data;
                    if (await Run(async () => ApplyServer(await Api.PostImage($"servers/{_sid}/banner", d, f.Mime)), "Baner zmieniony")) DrawBody();
                }, 34),
                srv.Banner is not null ? U.Ghost("Usuń baner", async () => { if (await Run(async () => ApplyServer(await Api.DeleteJson($"servers/{_sid}/banner")), "Baner usunięty")) DrawBody(); }, 34) : null),
            U.Primary("Zapisz", async () =>
            {
                await Run(async () => ApplyServer(await Api.PatchJson($"servers/{_sid}", new Dictionary<string, object?> { ["name"] = name.Text.Trim(), ["description"] = desc.Text.Trim() })), "Zapisano");
            }, 40).With(b => { b.Width = 160; b.HorizontalAlignment = HorizontalAlignment.Left; })));

        // poziomy
        var levels = Toggle("Poziomy (XP za pisanie i rozmowy)", srv.Levels);
        var msgs = Toggle("Wiadomość o awansie", srv.LevelMessages);
        var chan = new DropField { Header = U.T("Gdzie pisać o awansach", 13.5, U.Semi), HorizontalAlignment = HorizontalAlignment.Stretch };
        var texts = srv.Channels.Where(c => !c.IsVoice).OrderBy(c => c.Position).ToList();
        chan.Items.Add("Tam, gdzie padła wiadomość");
        foreach (var c in texts) chan.Items.Add("# " + c.Name);
        chan.SelectedIndex = srv.LevelChannelId is long lc && texts.FindIndex(c => c.Id == lc) is var ix and >= 0 ? ix + 1 : 0;
        _body.Add(Card(Label("Poziomy"), levels, msgs, chan,
            U.Ghost("Zapisz poziomy", async () =>
            {
                await Run(async () => ApplyServer(await Api.PatchJson($"servers/{_sid}", new Dictionary<string, object?>
                {
                    ["levels"] = levels.IsOn, ["levelMessages"] = msgs.IsOn, ["levelChannelId"] = chan.SelectedIndex > 0 ? texts[chan.SelectedIndex - 1].Id : null,
                })), "Zapisano ustawienia poziomów");
            }, 34).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));

        // zaproszenie
        _body.Add(Card(Label("Zaproszenie"), U.T("Każdy z tym linkiem może dołączyć (zbanowani nie).", 13, null, Theme.Mu, wrap: true),
            U.Ghost("Kopiuj link z zaproszeniem", async () =>
            {
                try { var code = await Api.CreateInvite(_sid); var link = $"{Api.BaseUri.AbsoluteUri.TrimEnd('/')}/i/{code}"; Sidebar.Copy(link); _sh.Info("Skopiowano: " + link); }
                catch (Exception ex) { Err(ex); }
            }, 34).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
    }

    // ───────────────────────── Szablon ─────────────────────────

    void Template()
    {
        Title("Szablon serwera", "Kanały, kategorie i role tego serwera jako przepis — ktoś może założyć taki sam serwer jednym kliknięciem. Bez wiadomości i członków.");
        var host = new StackPanel { Spacing = 12 };
        host.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
        _body.Add(host);
        _ = FillTemplate(host);
    }

    async Task FillTemplate(StackPanel host)
    {
        System.Text.Json.JsonElement t;
        try { t = (await Api.GetJson($"servers/{_sid}/template")).Prop("template"); }
        catch (Exception ex) { host.Children.Clear(); host.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); return; }
        host.Children.Clear();
        var name = new TextBox { Text = t.Str("name") ?? Srv!.Name, MaxLength = 100, PlaceholderText = "Nazwa szablonu" };
        var desc = new TextBox { Text = t.Str("description") ?? "", MaxLength = 120, PlaceholderText = "Krótki opis (opcjonalnie)" };
        if (t.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            host.Add(Card(Label("Nazwa"), name, Label("Opis"), desc,
                U.Primary("Utwórz szablon", async () => { if (await Run(() => Api.PostJson($"servers/{_sid}/template", new { name = name.Text.Trim(), description = desc.Text.Trim() }), "Szablon gotowy")) DrawBody(); }, 40)
                    .With(b => { b.Width = 200; b.HorizontalAlignment = HorizontalAlignment.Left; })));
            return;
        }
        var link = $"{Api.BaseUri.AbsoluteUri.TrimEnd('/')}/szablon/{t.Str("code")}";
        host.Add(Card(Label("Link do szablonu"), U.H(8, U.T(link, 13, U.Semi).With(x => x.IsTextSelectionEnabled = true), U.Ghost("Kopiuj", () => { Sidebar.Copy(link); _sh.Info("Skopiowano link do szablonu"); }, 30)),
            U.T($"Użyto {t.Long("uses") ?? 0} razy · zmieniony {Format.When(t.Long("updatedAt") ?? 0)}", 12, null, Theme.Mu)));
        if (t.Bool("dirty") == true)
            host.Add(Card(U.T("Serwer zmienił się od ostatniego zapisu szablonu.", 13, U.Semi, Theme.Yellow),
                U.Ghost("Zaktualizuj szablon", async () => { if (await Run(() => Api.PatchJson($"servers/{_sid}/template", new { sync = true }), "Szablon zaktualizowany")) DrawBody(); }, 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        host.Add(Card(Label("Nazwa"), name, Label("Opis"), desc,
            U.H(8, U.Ghost("Zapisz", async () => await Run(() => Api.PatchJson($"servers/{_sid}/template", new { name = name.Text.Trim(), description = desc.Text.Trim() }), "Zapisano"), 32),
                U.Ghost("Usuń szablon", async () =>
                {
                    if (await M.Confirm(_sh, "Usunąć szablon?", "Link przestanie działać. Serwery już założone z szablonu zostają.", "Usuń") && await Run(() => Api.DeleteJson($"servers/{_sid}/template"), "Szablon usunięty")) DrawBody();
                }, 32))));
    }

    async void DeleteServer()
    {
        var srv = Srv;
        if (srv is null) return;
        var box = new TextBox { PlaceholderText = srv.Name };
        var d = Dialog($"Usunąć serwer {srv.Name}?", U.V(10, U.T("Wszystkie kanały, wiadomości i pliki znikną na zawsze. Tego nie da się cofnąć.", 13.5, null, Theme.Red, wrap: true),
            U.T("Wpisz nazwę serwera, żeby potwierdzić:", 13, null, Theme.Mu), box), "Usuń na zawsze", danger: true);
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        if (box.Text.Trim() != srv.Name) { _sh.Info("Nazwa się nie zgadza — serwer nie został usunięty.", true); return; }
        if (await Run(() => Api.DeleteJson($"servers/{_sid}"), "Serwer usunięty")) { _sh.CloseServerSettings(); _sh.Go(0, 0); }
    }

    // ───────────────────────── Kanały i kategorie ─────────────────────────

    void Channels()
    {
        var srv = Srv!;
        Title("Kanały i kategorie", "Strzałki zmieniają kolejność. Kanał przenosisz do innej kategorii w jego ustawieniach.");
        var top = U.H(8,
            U.Primary("+ Kanał tekstowy", () => EditChannel(null, "text", null), 36).With(b => b.Width = 170),
            U.Ghost("+ Kanał głosowy", () => EditChannel(null, "voice", null), 36),
            U.Ghost("+ Scena", () => EditChannel(null, "stage", null), 36), // 6.3
            U.Ghost("+ Kategoria", async () =>
            {
                var name = new TextBox { PlaceholderText = "np. GRY", MaxLength = 100 };
                if (await Dialog("Nowa kategoria", name, "Utwórz").ShowAsync() != ContentDialogResult.Primary || name.Text.Trim().Length == 0) return;
                await Run(() => Api.PostJson($"servers/{_sid}/categories", new { name = name.Text.Trim() }), "Kategoria utworzona");
            }, 36));
        _body.Add(top);
        var tree = S.ChannelTree(srv);
        var cats = tree.Where(g => g.Category is not null).Select(g => g.Category!).ToList();
        foreach (var g in tree)
        {
            var box = new StackPanel { Spacing = 2 };
            if (g.Category is { } cat)
            {
                var ci = cats.IndexOf(cat);
                var head = new Grid().Cols("*", "auto").Add(U.T(cat.Name.ToUpperInvariant(), 12, U.Bold, Theme.Mu).At(0, 0), U.H(2,
                    U.IconButton("", "W górę", () => _ = MoveCategory(cats, ci, -1), 11, 28).With(b => b.IsEnabled = ci > 0),
                    U.IconButton("", "W dół", () => _ = MoveCategory(cats, ci, 1), 11, 28).With(b => b.IsEnabled = ci < cats.Count - 1),
                    U.IconButton("", "Dodaj kanał w tej kategorii", () => EditChannel(null, "text", cat.Id), 11, 28),
                    U.IconButton("", "Uprawnienia kategorii (szablon dla kanałów)", () => Overrides(null, cat), 11, 28),
                    U.IconButton("", "Zmień nazwę", async () =>
                    {
                        var name = new TextBox { Text = cat.Name, MaxLength = 100 };
                        if (await Dialog("Nazwa kategorii", name, "Zapisz").ShowAsync() != ContentDialogResult.Primary) return;
                        await Run(() => Api.PatchJson($"categories/{cat.Id}", new { name = name.Text.Trim() }), "Zapisano");
                    }, 11, 28),
                    U.IconButton("", "Usuń kategorię (kanały zostaną)", async () =>
                    {
                        if (!await M.Confirm(_sh, "Usunąć kategorię?", $"Kategoria {cat.Name} zniknie, a jej kanały przejdą na górę listy.", "Usuń")) return;
                        await Run(() => Api.DeleteJson($"categories/{cat.Id}"), "Kategoria usunięta");
                    }, 11, 28)).At(0, 1));
                box.Add(head.With(h => h.Margin = U.Th(4, 4, 0, 2)));
            }
            for (var i = 0; i < g.Channels.Count; i++)
            {
                var c = g.Channels[i];
                var idx = i;
                var list = g.Channels;
                var meta = c.IsVoice ? (c.UserLimit > 0 ? $"do {c.UserLimit} osób" : "") : (c.Readonly ? "tylko do czytania" : c.Slowmode > 0 ? $"tryb powolny {c.Slowmode} s" : c.Topic ?? "");
                var row = new Grid { Padding = U.Th(10, 4) }.Cols("auto", "*", "auto").Add(
                    (c.IsVoice ? (FrameworkElement)U.I(G.Speaker, 14, Theme.Mu) : U.T("#", 15, U.Semi, Theme.Mu)).With(t => { t.Margin = U.Th(0, 0, 10, 0); t.Width = 16; }).At(0, 0),
                    U.V(0, U.T(c.Name, 14, U.Semi), string.IsNullOrEmpty(meta) ? null : U.T(meta, 11.5, null, Theme.Mu2)).At(0, 1),
                    U.H(2,
                        U.IconButton("", "W górę", () => _ = MoveChannel(list, idx, -1), 11, 28).With(b => b.IsEnabled = idx > 0),
                        U.IconButton("", "W dół", () => _ = MoveChannel(list, idx, 1), 11, 28).With(b => b.IsEnabled = idx < list.Count - 1),
                        U.IconButton("", "Uprawnienia kanału", () => Overrides(c, null), 12, 28),
                        U.IconButton("", "Ustawienia kanału", () => EditChannel(c, c.Type, c.CategoryId), 12, 28),
                        U.IconButton("", "Usuń kanał", async () =>
                        {
                            if (!await M.Confirm(_sh, $"Usunąć #{c.Name}?", "Kanał zniknie razem ze wszystkimi wiadomościami. Tego nie da się cofnąć.", "Usuń")) return;
                            await Run(() => Api.DeleteJson($"channels/{c.Id}"), "Kanał usunięty");
                        }, 12, 28)).At(0, 2));
                box.Add(new Border { Child = row, Background = U.W(.04), CornerRadius = new CornerRadius(10) });
            }
            if (g.Channels.Count == 0) box.Add(U.T("(pusta kategoria)", 12, null, Theme.Mu2).With(t => t.Margin = U.Th(10, 2)));
            _body.Add(box);
        }
    }

    Task MoveCategory(List<Category> cats, int i, int dir)
    {
        var list = cats.ToList();
        var j = i + dir;
        if (j < 0 || j >= list.Count) return Task.CompletedTask;
        (list[i], list[j]) = (list[j], list[i]);
        return Run(async () => ApplyServer(await Api.PutJson($"servers/{_sid}/layout", new { categories = list.Select((c, k) => new { id = c.Id, position = k }).ToList() })));
    }

    Task MoveChannel(List<Channel> group, int i, int dir)
    {
        var list = group.ToList();
        var j = i + dir;
        if (j < 0 || j >= list.Count) return Task.CompletedTask;
        (list[i], list[j]) = (list[j], list[i]);
        return Run(async () => ApplyServer(await Api.PutJson($"servers/{_sid}/layout", new { channels = list.Select((c, k) => new { id = c.Id, categoryId = c.CategoryId, position = k }).ToList() })));
    }

    /// <summary>Kółko zębate przy kanale w kolumnie: od razu okienko ustawień tego kanału (na stronie „Kanały i kategorie”).</summary>
    public void EditChannelById(long cid)
    {
        if (Srv?.Channels.Find(x => x.Id == cid) is { } c) EditChannel(c, c.IsVoice ? "voice" : "text", c.CategoryId);
    }

    async void EditChannel(Channel? c, string type, long? categoryId)
    {
        var srv = Srv!;
        var voice = type is "voice" or "stage";
        var name = new TextBox { Text = c?.Name ?? "", PlaceholderText = voice ? "np. Pogaduchy" : "np. ogólny", MaxLength = 100 };
        var topic = new TextBox { Text = c?.Topic ?? "", PlaceholderText = "O czym jest ten kanał (opcjonalnie)", MaxLength = 1024, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64 };
        var cat = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        var cats = srv.Categories.OrderBy(x => x.Position).ToList();
        cat.Items.Add("Bez kategorii");
        foreach (var k in cats) cat.Items.Add(k.Name);
        cat.SelectedIndex = categoryId is long cid && cats.FindIndex(k => k.Id == cid) is var ix and >= 0 ? ix + 1 : 0;
        var sync = new CheckField { Content = "Skopiuj uprawnienia z kategorii", IsChecked = c is null && categoryId is not null };
        var slow = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        var slowVals = new[] { 0, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 21600 };
        foreach (var v in slowVals) slow.Items.Add(v == 0 ? "Wyłączony" : v < 60 ? $"{v} s" : v < 3600 ? $"{v / 60} min" : $"{v / 3600} h");
        slow.SelectedIndex = Math.Max(0, Array.IndexOf(slowVals, c?.Slowmode ?? 0));
        var ro = new CheckField { Content = "Tylko do czytania (piszą moderatorzy)", IsChecked = c?.Readonly == true };
        var limit = new NumberField { Header = "Limit osób (0 = bez limitu)", Value = c?.UserLimit ?? 0, Minimum = 0, Maximum = 99, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var bitrate = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        var bitVals = new[] { 32, 64, 96, 128 };
        foreach (var v in bitVals) bitrate.Items.Add($"{v} kb/s" + (v == 64 ? " (zwykła)" : v == 128 ? " (najlepsza)" : ""));
        bitrate.SelectedIndex = Math.Max(0, Array.IndexOf(bitVals, c?.Bitrate is > 0 ? c.Bitrate : 64));
        var stream = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        var streamVals = new[] { "720p", "1080p", "1440p" };
        foreach (var v in streamVals) stream.Items.Add("Transmisje do " + v);
        stream.SelectedIndex = Math.Max(0, Array.IndexOf(streamVals, c?.MaxStream ?? "1080p"));
        var fx = new CheckField { Content = "Efekty głosu (robot, Gibek, olbrzym, jaskinia, radio)", IsChecked = c?.FxOff != true }; // 6.2
        var form = U.V(8, U.T("Nazwa", 12.5, U.Semi, Theme.Mu), name, U.T("Kategoria", 12.5, U.Semi, Theme.Mu), cat, sync);
        if (!voice) form.Add(U.T("Opis", 12.5, U.Semi, Theme.Mu), topic, U.T("Tryb powolny", 12.5, U.Semi, Theme.Mu), slow, ro);
        else form.Add(limit, U.T("Jakość dźwięku", 12.5, U.Semi, Theme.Mu), bitrate, U.T("Transmisje ekranu", 12.5, U.Semi, Theme.Mu), stream, fx);
        form.Width = 420;
        var d = Dialog(c is null ? (type == "stage" ? "Nowa scena (mówią tylko osoby na scenie)" : voice ? "Nowy kanał głosowy" : "Nowy kanał tekstowy") : (voice ? "🔊 " : "#") + c.Name, form, c is null ? "Utwórz" : "Zapisz");
        if (await d.ShowAsync() != ContentDialogResult.Primary || name.Text.Trim().Length == 0) return;
        var body = new Dictionary<string, object?>
        {
            ["name"] = name.Text.Trim(), ["categoryId"] = cat.SelectedIndex > 0 ? cats[cat.SelectedIndex - 1].Id : null,
        };
        if (sync.IsChecked == true && cat.SelectedIndex > 0) body["syncPerms"] = true;
        if (c is null) body["type"] = type;
        if (!voice) { body["topic"] = topic.Text.Trim(); body["slowmode"] = slowVals[Math.Max(0, slow.SelectedIndex)]; body["readonly"] = ro.IsChecked == true; }
        else { body["userLimit"] = (int)(double.IsNaN(limit.Value) ? 0 : limit.Value); body["bitrate"] = bitVals[Math.Max(0, bitrate.SelectedIndex)]; body["maxStream"] = streamVals[Math.Max(0, stream.SelectedIndex)]; body["fxOff"] = fx.IsChecked != true; }
        if (c is null) await Run(() => Api.PostJson($"servers/{_sid}/channels", body), "Kanał utworzony");
        else await Run(() => Api.PatchJson($"channels/{c.Id}", body), "Zapisano");
    }

    // ───────────────────────── Członkowie i bany ─────────────────────────

    string _memberQuery = "";

    void Members()
    {
        var srv = Srv!;
        Title($"Członkowie — {srv.Members.Count}", "Kliknij osobę, żeby nadać role, zmienić nick albo moderować.");
        var search = new TextBox { Text = _memberQuery, PlaceholderText = "Szukaj osoby" };
        var list = new StackPanel { Spacing = 4 };
        void Fill()
        {
            list.Children.Clear();
            var q = EmojiData.Fold(_memberQuery.Trim());
            foreach (var m in srv.Members.Where(m => q.Length == 0 || EmojiData.Fold(S.NameOf(m.UserId, srv)).Contains(q) || EmojiData.Fold(S.U(m.UserId)?.Username ?? "").Contains(q))
                         .OrderBy(m => S.NameOf(m.UserId, srv), StringComparer.Create(Format.Pl, true)).Take(200))
            {
                var uid = m.UserId;
                var roles = new WrapPanel { Spacing = 4 };
                foreach (var r in S.RolesOf(srv, uid))
                    roles.Children.Add(U.Box(U.H(4, new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = U.B(r.Color ?? "#8b8599") }, U.T(r.Name, 11, U.Semi)), U.W(.07), 6, U.Th(6, 1)));
                var timeout = m.TimeoutUntil is long t && t > Format.NowMs() ? U.Box(U.T("wyciszony do " + Format.When(t), 10.5, U.Bold, Theme.OnAccent), Theme.Yellow, 6, U.Th(6, 1)) : null;
                var g = new Grid { Padding = U.Th(10, 6) }.Cols("auto", "*", "auto").Add(new Avatar(34, true).Set(S, Api, uid, srv, true).At(0, 0),
                    U.V(2, U.H(6, U.T(S.NameOf(uid, srv), 14, U.Semi, U.B(S.NameColor(uid, srv))), U.T("@" + (S.U(uid)?.Username ?? ""), 11.5, null, Theme.Mu2), srv.OwnerId == uid ? U.T("👑", 11) : null, timeout),
                        roles, m.JoinedAt > 0 ? U.T("Dołączył(a) " + Format.Local(m.JoinedAt).ToString("d MMM yyyy", Format.Pl), 11, null, Theme.Mu2) : null).With(p => p.Margin = U.Th(12, 0)).At(0, 1));
                var more = U.IconButton("", "Akcje", () => { }, 14, 32);
                var fly = UserMenu.Build(_sh, uid, srv);
                if (Can(Perm.ManageRoles) && srv.Roles.Count > 0)
                {
                    var sub = new MenuFlyoutSubItem { Text = "Role", Icon = new FontIcon { Glyph = "" } };
                    foreach (var r in srv.Roles.OrderByDescending(x => x.Position))
                    {
                        var rid = r.Id;
                        var has = m.RoleIds.Contains(rid);
                        sub.Items.Add(M.Toggle(r.Name, has, on => _ = Run(() => on ? Api.PutJson($"servers/{_sid}/members/{uid}/roles/{rid}") : Api.DeleteJson($"servers/{_sid}/members/{uid}/roles/{rid}"))));
                    }
                    fly.Items.Insert(0, sub);
                    fly.Items.Insert(1, new MenuFlyoutSeparator());
                }
                more.Flyout = fly;
                g.Add(more.At(0, 2));
                list.Add(new Border { Child = g, Background = U.W(.035), CornerRadius = new CornerRadius(10) });
            }
        }
        search.TextChanged += (_, _) => { _memberQuery = search.Text; Fill(); };
        Fill();
        _body.Add(search);
        _body.Add(list);
    }

    void Bans()
    {
        Title("Bany", "Zbanowane osoby nie mogą wrócić na serwer, nawet z zaproszeniem.");
        var list = new StackPanel { Spacing = 6 };
        list.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
        _body.Add(list);
        _ = FillBans(list);
    }

    async Task FillBans(StackPanel list)
    {
        try
        {
            var arr = await Api.GetJson($"servers/{_sid}/bans");
            list.Children.Clear();
            foreach (var b in arr.EnumerateArray())
            {
                var uid = b.Long("userId") ?? 0;
                var u = b.Prop("user").To<User>();
                var g = new Grid { Padding = U.Th(12, 8) }.Cols("auto", "*", "auto").Add(
                    new Avatar(34).SetRaw(u?.Name ?? "?", Store.ColorOfUser(u), Api.Abs(u?.Avatar), null).At(0, 0),
                    U.V(2, U.T(u?.Name ?? "Nieznana osoba", 14, U.Semi), U.T($"@{u?.Username} · {Format.When(b.Long("createdAt") ?? 0)}" + (b.Str("reason") is { Length: > 0 } r ? " · " + r : ""), 12, null, Theme.Mu, wrap: true)).With(p => p.Margin = U.Th(12, 0)).At(0, 1),
                    U.Ghost("Zdejmij bana", async () => { if (await Run(() => Api.DeleteJson($"servers/{_sid}/bans/{uid}"), "Ban zdjęty")) DrawBody(); }, 30).At(0, 2));
                list.Add(new Border { Child = g, Background = U.W(.035), CornerRadius = new CornerRadius(10) });
            }
            if (list.Children.Count == 0) list.Add(U.T("Nikt nie jest zbanowany.", 13.5, null, Theme.Mu));
        }
        catch (Exception ex) { list.Children.Clear(); list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    }
}
