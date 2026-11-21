using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace GibCord.App.Ui;

static class M
{
    public static MenuFlyoutItem Item(string text, string glyph, Action a, bool danger = false)
    {
        var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (danger) i.Foreground = Theme.Red;
        i.Click += (_, _) => a();
        return i;
    }

    public static ToggleMenuFlyoutItem Toggle(string text, bool on, Action<bool> set)
    {
        var i = new ToggleMenuFlyoutItem { Text = text, IsChecked = on };
        i.Click += (_, _) => set(i.IsChecked);
        return i;
    }

    public static RadioMenuFlyoutItem Radio(string text, string group, bool on, Action a)
    {
        var i = new RadioMenuFlyoutItem { Text = text, GroupName = group, IsChecked = on };
        i.Click += (_, _) => a();
        return i;
    }

    public static bool DialogOpen(XamlRoot? root) => AppDialog.OpenCount > 0;

    /// <summary>Czeka, aż zamknie się inne okienko (żeby nowe nie wyskoczyło nad formularzem, który ktoś wypełnia).</summary>
    public static async Task WaitNoDialog(XamlRoot? root)
    {
        for (var i = 0; i < 150 && AppDialog.OpenCount > 0; i++)
            await Task.Delay(100);
    }

    public static async Task<bool> Confirm(Shell sh, string title, string text, string ok)
    {
        await WaitNoDialog(sh.XamlRoot);
        var d = new AppDialog
        {
            Title = title, Content = text, PrimaryButtonText = ok, CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>Menu serwera (nazwa u góry kolumny / prawy klik na ikonie).</summary>
static class ServerMenu
{
    public static MenuFlyout Build(Shell sh, Server srv)
    {
        var f = new MenuFlyout();
        var s = sh.S;
        if (s.Can(s.FirstTextChannel(srv)?.Id ?? 0, Perm.CreateInvite) || srv.OwnerId == s.Me.Id)
            f.Items.Add(M.Item("Zaproś znajomych", "", async () =>
            {
                try
                {
                    var code = await sh.C.Api.CreateInvite(srv.Id);
                    var link = $"{sh.C.Api.BaseUri.AbsoluteUri.TrimEnd('/')}/i/{code}";
                    Sidebar.Copy(link);
                    sh.Info("Link z zaproszeniem skopiowany: " + link);
                }
                catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
            }));
        f.Items.Add(M.Item("Oznacz wszystko jako przeczytane", "\uE8FB", () => { foreach (var c in srv.Channels.Where(c => !c.IsVoice && s.IsUnread(c.Id))) sh.C.MarkRead(c.Id); }));
        f.Items.Add(M.Item("Wydarzenia", "\uE787", () => Events.Show(sh, srv.Id)));
        if (srv.Levels) f.Items.Add(M.Item("Ranking poziomów", "\uE734", () => Levels.Show(sh, srv)));
        f.Items.Add(NotifyMenu.Sub(sh, "server", srv.Id, s.Notify.Servers.GetValueOrDefault(srv.Id)));
        f.Items.Add(M.Item("Mój profil na tym serwerze", "\uE77B", () => ServerProfile.Show(sh, srv)));
        if (srv.Roles.Any(r => r.SelfAssignable)) f.Items.Add(M.Item("Wybierz role", "\uE8EC", () => ServerProfile.PickRoles(sh, srv)));
        var canSettings = new[] { Perm.ManageServer, Perm.ManageChannels, Perm.ManageRoles, Perm.Kick, Perm.Ban, Perm.Timeout, Perm.ManageExpressions, Perm.ManageMessages, Perm.ViewAuditLog, Perm.ManageNicknames }
            .Any(p => s.CanServer(srv, p));
        if (canSettings)
        {
            var section = s.CanServer(srv, Perm.ManageServer) ? "overview" : s.CanServer(srv, Perm.ManageChannels) ? "channels" : s.CanServer(srv, Perm.ManageRoles) ? "roles" : "members";
            f.Items.Add(M.Item("Ustawienia serwera", "\uE713", () => sh.ShowServerSettings(srv.Id, section)));
        }
        // kolejność na pasku (tylko na tym komputerze; można też przeciągać ikony)
        var pos = ServerRail.Ordered(s).FindIndex(x => x.Id == srv.Id);
        var count = s.Servers.Count;
        if (count > 1)
        {
            f.Items.Add(new MenuFlyoutSeparator());
            if (pos > 0) f.Items.Add(M.Item("Przesuń wyżej", "\uE70E", () => ServerRail.MoveBy(sh, srv.Id, -1)));
            if (pos >= 0 && pos < count - 1) f.Items.Add(M.Item("Przesuń niżej", "\uE70D", () => ServerRail.MoveBy(sh, srv.Id, 1)));
        }
        if (srv.OwnerId != s.Me.Id && !srv.Official)
        {
            f.Items.Add(new MenuFlyoutSeparator());
            f.Items.Add(M.Item("Opuść serwer", "", async () =>
            {
                if (!await M.Confirm(sh, "Opuścić serwer?", $"Wyjdziesz z serwera {srv.Name}. Wrócisz tylko z nowym zaproszeniem.", "Opuść")) return;
                try { await sh.C.Api.LeaveServer(srv.Id); } catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
            }, danger: true));
        }
        return f;
    }
}

/// <summary>Powiadomienia serwera / kanału: poziom i wyciszenie (API 13.2).</summary>
static class NotifyMenu
{
    public static MenuFlyoutSubItem Sub(Shell sh, string scope, long id, NotifySetting? cur)
    {
        var sub = new MenuFlyoutSubItem { Text = "Powiadomienia", Icon = new FontIcon { Glyph = "" } };
        var lvl = cur?.Level;
        var g = "lvl" + scope + id;
        sub.Items.Add(M.Radio("Domyślnie" + (scope == "server" ? " (tylko wzmianki)" : ""), g, lvl is null, () => Set(sh, scope, id, null, cur?.MutedUntil)));
        sub.Items.Add(M.Radio("Wszystkie wiadomości", g, lvl == "all", () => Set(sh, scope, id, "all", cur?.MutedUntil)));
        sub.Items.Add(M.Radio("Tylko wzmianki", g, lvl == "mentions", () => Set(sh, scope, id, "mentions", cur?.MutedUntil)));
        sub.Items.Add(M.Radio("Nic", g, lvl == "none", () => Set(sh, scope, id, "none", cur?.MutedUntil)));
        sub.Items.Add(new MenuFlyoutSeparator());
        var muted = cur?.MutedUntil is long t && t > Format.NowMs();
        if (muted) sub.Items.Add(M.Item("Wyłącz wyciszenie", "", () => Set(sh, scope, id, lvl, null)));
        else
        {
            foreach (var (label, mins) in new[] { ("Wycisz na godzinę", 60L), ("Wycisz na 8 godzin", 480L), ("Wycisz na dobę", 1440L), ("Wycisz do odwołania", 0L) })
                sub.Items.Add(M.Item(label, "", () => Set(sh, scope, id, lvl, mins == 0 ? Format.NowMs() + 100L * 365 * 86400000 : Format.NowMs() + mins * 60000)));
        }
        return sub;
    }

    public static void SetMute(Shell sh, string scope, long id, bool mute) =>
        Set(sh, scope, id, (scope == "channel" ? sh.S.Notify.Channels : scope == "server" ? sh.S.Notify.Servers : sh.S.Notify.Dms).GetValueOrDefault(id)?.Level,
            mute ? Format.NowMs() + 100L * 365 * 86400000 : null);

    static async void Set(Shell sh, string scope, long id, string? level, long? mutedUntil)
    {
        try { await sh.C.Api.SetNotify(scope, id, level, mutedUntil); }
        catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
    }
}

/// <summary>Zmiana statusu (klik w swój awatar na dole).</summary>
static class StatusMenu
{
    public static MenuFlyout Build(Shell sh)
    {
        var f = new MenuFlyout();
        var cur = sh.S.Me.Status;
        // kilka kont: przełącznik na górze
        if (!sh.C.Offline || Accounts.Demo is not null)
        {
            var me = sh.S.Me;
            var others = Accounts.Others(me.Id).ToList();
            var bgUnread = Accounts.Background.UnreadTotal;
            var acc = new MenuFlyoutSubItem { Text = others.Count == 0 ? "Konta" : $"Konta ({others.Count + 1})" + (bgUnread > 0 ? $" · {bgUnread} nowe" : ""), Icon = new FontIcon { Glyph = "\uE716" } };
            acc.Items.Add(new MenuFlyoutItem { Text = $"{me.Name} (@{me.Username})  ✓", IsEnabled = false });
            foreach (var o in others)
            {
                var n = Accounts.Background.UnreadOf(o.Id);
                acc.Items.Add(M.Item($"{o.Name} (@{o.Username})" + (n > 0 ? $"  · {n} nowe" : ""), "\uE77B", () => _ = sh.W.SwitchAccount(o.Id)));
            }
            acc.Items.Add(new MenuFlyoutSeparator());
            acc.Items.Add(M.Item("Dodaj konto…", "\uE710", () => _ = sh.W.AddAccount()));
            f.Items.Add(acc);
            f.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var (st, label) in new[] { ("online", "Dostępny"), ("idle", "Zaraz wracam"), ("dnd", "Nie przeszkadzać"), ("invisible", "Niewidoczny") })
        {
            var i = new MenuFlyoutItem { Text = label + (cur == st ? "  ✓" : ""), Icon = new FontIcon { Glyph = "", Foreground = Theme.StatusBrush(st == "invisible" ? "offline" : st) } };
            i.Click += async (_, _) => { var err = await sh.C.SetStatus(st); if (err is not null) sh.Info(err, true); };
            f.Items.Add(i);
        }
        f.Items.Add(new MenuFlyoutSeparator());
        f.Items.Add(M.Item("Ustaw opis statusu…", "", async () =>
        {
            var box = new TextBox { Text = sh.S.Me.CustomStatus ?? "", PlaceholderText = "np. gram w Minecrafta", MaxLength = 100 };
            var clear = new DropField { Header = "Wyczyść po", HorizontalAlignment = HorizontalAlignment.Stretch };
            var vals = new[] { 0L, 30, 60, 240, -1 };
            foreach (var l in new[] { "Nie czyść", "30 minutach", "Godzinie", "4 godzinach", "Końcu dnia" }) clear.Items.Add(l);
            clear.SelectedIndex = 0;
            var d = new AppDialog
            {
                Title = "Opis statusu", Content = U.V(10, box, clear).With(p => p.Width = 340), PrimaryButtonText = "Zapisz", SecondaryButtonText = "Wyczyść", CloseButtonText = "Anuluj",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
            };
            await M.WaitNoDialog(sh.XamlRoot);
            var r = await d.ShowAsync();
            if (r == ContentDialogResult.None) return;
            var text = r == ContentDialogResult.Secondary ? "" : box.Text.Trim();
            var mins = vals[Math.Max(0, clear.SelectedIndex)];
            var err = await sh.C.SetStatus(sh.S.Me.Status, text);
            if (err is not null) { sh.Info(err, true); return; }
            App.Prefs.StatusClearAt = text.Length == 0 || mins == 0 ? 0 : mins < 0 ? new DateTimeOffset(DateTime.Today.AddDays(1)).ToUnixTimeMilliseconds() : Format.NowMs() + mins * 60_000;
            App.Prefs.StatusClearText = App.Prefs.StatusClearAt > 0 ? text : "";
            App.Prefs.Save();
        }));
        f.Items.Add(M.Item("Edytuj profil", "\uE77B", () => sh.ShowSettings("profile")));
        f.Items.Add(M.Item("Kopiuj nazwę użytkownika", "\uE8C8", () => { Sidebar.Copy(sh.S.Me.Username); sh.Info("Skopiowano @" + sh.S.Me.Username); }));
        return f;
    }
}

/// <summary>Ustawienia (zębatka): powiadomienia, zasobnik, aktualizacje, wylogowanie.</summary>
static class SettingsMenu
{
    public static MenuFlyout Build(Shell sh)
    {
        var f = new MenuFlyout();
        var p = App.Prefs;
        f.Items.Add(M.Item("Głos i dźwięk…", "\uE720", () => VoiceSettings.Show(sh)));
        f.Items.Add(M.Toggle("Dźwięki rozmów", p.Sounds, v => { p.Sounds = v; p.Save(); }));
        f.Items.Add(M.Toggle("Pokazuj, w co grasz", p.GameDetection, v => { p.GameDetection = v; p.Save(); }));
        f.Items.Add(M.Toggle("Pokazuj, czego słuchasz w Spotify", p.MusicDetection, v => { p.MusicDetection = v; p.Save(); }));
        var ov = new MenuFlyoutSubItem { Text = "Nakładka w grze (Ctrl+Shift+O)", Icon = new FontIcon { Glyph = "\uE7FC" } };
        foreach (var (mode, label) in new[] { ("off", "Wyłączona"), ("game", "Tylko gdy grasz"), ("always", "Zawsze w rozmowie") })
            ov.Items.Add(M.Radio(label, "ovmode", p.OverlayMode == mode, () => { p.OverlayMode = mode; p.Save(); }));
        ov.Items.Add(new MenuFlyoutSeparator());
        foreach (var (corner, label) in new[] { ("tl", "Lewy górny róg"), ("tr", "Prawy górny róg"), ("bl", "Lewy dolny róg"), ("br", "Prawy dolny róg") })
            ov.Items.Add(M.Radio(label, "ovcorner", p.OverlayCorner == corner, () => { p.OverlayCorner = corner; p.Save(); }));
        f.Items.Add(ov);
        f.Items.Add(new MenuFlyoutSeparator());
        f.Items.Add(M.Toggle("Powiadomienia na pulpicie", p.Notifications, v => { p.Notifications = v; p.Save(); }));
        f.Items.Add(M.Toggle("Zamykanie do zasobnika (obok zegara)", p.CloseToTray, v => { p.CloseToTray = v; p.Save(); }));
        f.Items.Add(M.Toggle("Lista osób po prawej", p.MembersPanel, _ => sh.ToggleMembers()));
        f.Items.Add(M.Toggle("Uruchamiaj z Windowsem", AutoStart.Enabled, v => { AutoStart.Set(v); }));
        f.Items.Add(new MenuFlyoutSeparator());
        f.Items.Add(M.Item("Sprawdź aktualizacje", "", async () =>
        {
            var err = await Updater.Check(v => sh.ShowUpdate(v));
            sh.Info(err ?? "Szukam nowej wersji… jeśli jest, pojawi się pasek u góry.", err is not null);
        }));
        f.Items.Add(M.Item("Otwórz GibCorda w przeglądarce", "", () => _ = Launcher.LaunchUriAsync(sh.C.Api.BaseUri)));
        f.Items.Add(M.Item("Wszystkie ustawienia…", "\uE713", () => sh.ShowSettings()));
        if (sh.S.Me.IsAdmin) f.Items.Add(M.Item("Panel admina", "\uE7EF", () => sh.ShowAdmin()));
        f.Items.Add(M.Item($"GibCord {Updater.Version} · Co nowego", "", () => sh.OpenChannelByKind("changelog")));
        f.Items.Add(new MenuFlyoutSeparator());
        f.Items.Add(M.Item("Wyloguj się", "", async () =>
        {
            if (!await M.Confirm(sh, "Wylogować się?", "Na tym komputerze trzeba będzie zalogować się ponownie. Zapisane wiadomości znikną z dysku.", "Wyloguj")) return;
            await sh.W.LogoutActive();
        }, danger: true));
        return f;
    }
}

/// <summary>Dodanie serwera: dołączenie z zaproszenia albo nowy serwer — pusty, z gotowego szablonu, z szablonu GibCorda albo z Discorda.</summary>
static class AddServerDialog
{
    static readonly System.Text.RegularExpressions.Regex GcTpl = new(@"/szablon/([A-Za-z0-9]{12})");
    static readonly System.Text.RegularExpressions.Regex DcTpl = new(@"(?:discord\.new/|discord(?:app)?\.com/template/)([A-Za-z0-9]{2,32})");

    public static async void Show(Shell sh)
    {
        var code = new TextBox { PlaceholderText = "https://gibcord.luisboon.pl/i/abc123 albo abc123" };
        var name = new TextBox { PlaceholderText = "np. Ekipa z klasy", MaxLength = 100 };
        var list = new StackPanel { Spacing = 4 };
        var preview = new StackPanel { Spacing = 4 };
        var link = new TextBox { PlaceholderText = "Link do szablonu: GibCord (/szablon/…) albo Discord (discord.new/…)" };
        (string Source, string Code)? chosen = null;
        void Pick((string, string)? t, string label)
        {
            chosen = t;
            foreach (var b in list.Children.OfType<Border>()) b.Background = Equals(b.Tag, label) ? Theme.Active : U.W(.04);
        }
        Border Opt(string label, string blurb, string letter, (string, string)? t)
        {
            var b = new Border { Tag = label, Background = U.W(.04), CornerRadius = new CornerRadius(10), Padding = U.Th(10, 8),
                Child = U.H(10, U.Box(U.T(letter, 15, U.Bold, Theme.OnAccent).With(x => x.HorizontalAlignment = HorizontalAlignment.Center), Theme.Accent, 10, U.Th(0)).With(x => { x.Width = 36; x.Height = 36; }),
                    U.V(0, U.T(label, 13.5, U.Bold), U.T(blurb, 11.5, null, Theme.Mu, wrap: true).With(x => x.MaxWidth = 330))) };
            b.Tapped += (_, _) => { Pick(t, label); if (t is null) preview.Children.Clear(); };
            list.Add(b);
            return b;
        }
        Opt("Pusty serwer", "Jeden kanał tekstowy i jeden głosowy — resztę dodasz sam.", "+", null);
        Pick(null, "Pusty serwer");
        try
        {
            var arr = await sh.C.Api.GetJson("templates/builtin");
            foreach (var t in arr.EnumerateArray())
            {
                var key = t.Str("key") ?? "";
                var cnt = t.Prop("preview").Prop("counts");
                Opt(t.Str("name") ?? key, (t.Str("blurb") ?? "") + $" · {cnt.Long("text")} tekst., {cnt.Long("voice")} głos., {cnt.Long("roles")} ról", t.Str("letter") ?? "•", ("builtin", key));
            }
        }
        catch { }
        var seq = 0;
        link.TextChanged += async (_, _) =>
        {
            var my = ++seq;
            var m1 = GcTpl.Match(link.Text); var m2 = DcTpl.Match(link.Text);
            (string Source, string Code)? t = m1.Success ? ("gibcord", m1.Groups[1].Value) : m2.Success ? ("discord", m2.Groups[1].Value) : null;
            preview.Children.Clear();
            if (t is null) return;
            preview.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
            try
            {
                var p = await sh.C.Api.GetJson(t.Value.Source == "discord" ? $"templates/discord/{t.Value.Code}" : $"templates/{t.Value.Code}");
                if (my != seq) return; // w międzyczasie wklejono inny link
                preview.Children.Clear();
                var cnt = p.Prop("counts");
                preview.Add(U.T(p.Str("name") ?? "Szablon", 14, U.Bold));
                preview.Add(U.T($"{cnt.Long("categories")} kategorii · {cnt.Long("text")} tekstowych · {cnt.Long("voice")} głosowych · {cnt.Long("roles")} ról", 12, null, Theme.Mu));
                var chans = string.Join(", ", p.Prop("channels").EnumerateArray().Take(12).Select(c => (c.Str("type") == "voice" ? "🔊" : "#") + c.Str("name")));
                preview.Add(U.T(chans, 11.5, null, Theme.Mu2, wrap: true));
                if (p.Str("notes") is { Length: > 0 } notes) preview.Add(U.T(notes, 11.5, null, Theme.Yellow, wrap: true));
                Pick(t, "");
                if (string.IsNullOrWhiteSpace(name.Text)) name.Text = p.Str("serverName") ?? p.Str("name") ?? "";
            }
            catch (Exception ex) { preview.Children.Clear(); preview.Add(U.T(GibCordClient.Friendly(ex), 12, null, Theme.Red, wrap: true)); }
        };
        var panel = U.V(10,
            U.T("Masz zaproszenie? Wklej link albo kod:", 13, U.Semi, Theme.Mu), code,
            U.HLine(.12).With(l => l.Margin = U.Th(0, 8)),
            U.T("Albo załóż własny serwer:", 13, U.Semi, Theme.Mu), name,
            new ScrollViewer { Content = list, MaxHeight = 230 },
            link, preview).With(p => p.Width = 420);
        var d = new AppDialog
        {
            Title = "Dodaj serwer", Content = new ScrollViewer { Content = panel, MaxHeight = 600 }, PrimaryButtonText = "Dołącz", SecondaryButtonText = "Załóż serwer", CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        var r = await d.ShowAsync();
        try
        {
            if (r == ContentDialogResult.Primary && code.Text.Trim() is { Length: > 0 } c)
            {
                var k = c.TrimEnd('/');
                var slash = k.LastIndexOf('/');
                if (slash >= 0) k = k[(slash + 1)..];
                var srv = await sh.C.Api.JoinInvite(k);
                await sh.C.RefreshState();
                sh.Go(srv.Id, 0);
                if (sh.S.ServerById(srv.Id) is { } joined && joined.Roles.Any(x => x.SelfAssignable)) ServerProfile.PickRoles(sh, joined);
            }
            else if (r == ContentDialogResult.Secondary && name.Text.Trim() is { Length: > 0 } n)
            {
                object body = chosen is { } t ? new { name = n, template = new { source = t.Source, code = t.Code } } : new { name = n };
                var e = await sh.C.Api.PostJson("servers", body);
                var srvId = e.Long("id") ?? 0;
                await sh.C.RefreshState();
                sh.Go(srvId, 0);
                sh.Info("Serwer gotowy! Zaproś znajomych z menu serwera.");
            }
        }
        catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
    }
}

/// <summary>Start razem z Windowsem (wpis w rejestrze użytkownika, okno schowane do zasobnika).</summary>
static class AutoStart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "GibCord6";
    const string TaskId = "GibCordStartup"; // wersja ze Sklepu: zadanie startowe z manifestu (store/Package.appxmanifest)

    public static bool Enabled
    {
        get
        {
            if (Platform.IsPackaged)
            {
                try { var t = Windows.ApplicationModel.StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult(); return t.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy; }
                catch { return false; }
            }
            try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(Name) is string; } catch { return false; }
        }
    }

    public static async void Set(bool on)
    {
        if (Platform.IsPackaged)
        {
            try
            {
                var t = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
                if (on) await t.RequestEnableAsync(); else t.Disable();
            }
            catch (Exception ex) { App.Log("Autostart (Sklep): " + ex.Message); }
            return;
        }
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, true);
            if (k is null) return;
            if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --ukryty");
            else k.DeleteValue(Name, false);
        }
        catch { }
    }
}

/// <summary>Skąd jest ta kopia aplikacji: instalator (Velopack) czy Microsoft Store (paczka MSIX z tożsamością).</summary>
static class Platform
{
    static bool? _packaged;
    public static bool IsPackaged
    {
        get
        {
            if (_packaged is { } p) return p;
            try { _ = Windows.ApplicationModel.Package.Current.Id; _packaged = true; }
            catch { _packaged = false; }
            return _packaged.Value;
        }
    }
}
