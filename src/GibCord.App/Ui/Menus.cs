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

    public static async Task<bool> Confirm(Shell sh, string title, string text, string ok)
    {
        var d = new ContentDialog
        {
            Title = title, Content = U.T(text, 14, null, null, wrap: true), PrimaryButtonText = ok, CloseButtonText = "Anuluj",
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
        f.Items.Add(M.Item("Oznacz wszystko jako przeczytane", "", () => { foreach (var c in srv.Channels.Where(c => !c.IsVoice && s.IsUnread(c.Id))) sh.C.MarkRead(c.Id); }));
        f.Items.Add(NotifyMenu.Sub(sh, "server", srv.Id, s.Notify.Servers.GetValueOrDefault(srv.Id)));
        f.Items.Add(M.Item("Ustawienia serwera na stronie", "", () => _ = Launcher.LaunchUriAsync(new Uri(sh.C.Api.BaseUri, $"serwer/{srv.Id}"))));
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
            var d = new ContentDialog
            {
                Title = "Opis statusu", Content = box, PrimaryButtonText = "Zapisz", SecondaryButtonText = "Wyczyść", CloseButtonText = "Anuluj",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
            };
            var r = await d.ShowAsync();
            if (r == ContentDialogResult.None) return;
            var err = await sh.C.SetStatus(sh.S.Me.Status, r == ContentDialogResult.Secondary ? "" : box.Text.Trim());
            if (err is not null) sh.Info(err, true);
        }));
        f.Items.Add(M.Item("Kopiuj nazwę użytkownika", "", () => { Sidebar.Copy(sh.S.Me.Username); sh.Info("Skopiowano @" + sh.S.Me.Username); }));
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
        f.Items.Add(M.Item("Pełne ustawienia konta (strona)", "", () => _ = Launcher.LaunchUriAsync(new Uri(sh.C.Api.BaseUri, "ustawienia"))));
        f.Items.Add(M.Item($"GibCord {Updater.Version} · Co nowego", "", () => sh.OpenChannelByKind("changelog")));
        f.Items.Add(new MenuFlyoutSeparator());
        f.Items.Add(M.Item("Wyloguj się", "", async () =>
        {
            if (!await M.Confirm(sh, "Wylogować się?", "Na tym komputerze trzeba będzie zalogować się ponownie. Zapisane wiadomości znikną z dysku.", "Wyloguj")) return;
            await sh.C.Logout();
            sh.W.ShowLogin(null);
        }, danger: true));
        return f;
    }
}

/// <summary>Dodanie serwera: dołączenie z zaproszenia albo nowy serwer.</summary>
static class AddServerDialog
{
    public static async void Show(Shell sh)
    {
        var code = new TextBox { PlaceholderText = "https://gibcord.luisboon.pl/i/abc123 albo abc123" };
        var name = new TextBox { PlaceholderText = "np. Ekipa z klasy", MaxLength = 100 };
        var panel = U.V(10,
            U.T("Masz zaproszenie? Wklej link albo kod:", 13, U.Semi, Theme.Mu), code,
            U.HLine(.12).With(l => l.Margin = U.Th(0, 8)),
            U.T("Albo załóż własny serwer:", 13, U.Semi, Theme.Mu), name);
        var d = new ContentDialog
        {
            Title = "Dodaj serwer", Content = panel, PrimaryButtonText = "Dołącz", SecondaryButtonText = "Załóż serwer", CloseButtonText = "Anuluj",
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
            }
            else if (r == ContentDialogResult.Secondary && name.Text.Trim() is { Length: > 0 } n)
            {
                var srv = await sh.C.Api.CreateServer(n);
                await sh.C.RefreshState();
                sh.Go(srv.Id, 0);
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
