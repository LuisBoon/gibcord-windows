using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.System;
using User = GibCord.Core.User;

namespace GibCord.App.Ui;

/// <summary>
/// Ustawienia na całe okno (jak na Discordzie): profil, konto, bezpieczeństwo (2FA, urządzenia), prywatność,
/// powiadomienia, głos, aplikacja. Esc albo ✕ zamyka.
/// </summary>
public sealed class SettingsPage : Grid
{
    readonly Shell _sh;
    readonly StackPanel _nav = new() { Spacing = 2, Padding = U.Th(12, 24, 12, 24) };
    readonly StackPanel _body = new() { Spacing = 14, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Padding = U.Th(40, 28, 40, 60) };
    readonly ScrollViewer _sv;
    string _section;

    Store S => _sh.S;
    GibCordApi Api => _sh.C.Api;

    public SettingsPage(Shell sh, string section)
    {
        _sh = sh;
        _section = section;
        Background = Theme.Page;
        this.Cols("250", "*", "auto");
        _sv = U.Scroll(_body);
        var close = U.IconButton(G.Close, "Zamknij (Esc)", sh.CloseSettings, 16, 40);
        close.Margin = U.Th(0, 20, 24, 0);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Background = U.W(.08);
        close.CornerRadius = new CornerRadius(20);
        this.Add(new Border { Background = U.W(.03), Child = U.Scroll(_nav) }.At(0, 0), _sv.At(0, 1), close.At(0, 2));
        KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; sh.CloseSettings(); } };
        S.Changed += OnChange;
        Unloaded += (_, _) => S.Changed -= OnChange;
        Draw();
    }

    void OnChange(StoreChange c)
    {
        // po zmianie „ja” (zapis profilu, 2FA, e-mail) — przerysuj, chyba że ktoś właśnie pisze w formularzu
        // konto: bez przerysowania (wpisywane hasła by znikły) — własne akcje odświeżają widok same
        if (c.Kind is ChangeKind.Me && _section is "security" or "privacy") Draw();
        if (c.Kind is ChangeKind.Blocked && _section == "privacy") Draw();
    }

    void Draw()
    {
        _nav.Children.Clear();
        void Head(string t) => _nav.Add(U.T(t, 11, U.Bold, Theme.Mu2).With(x => x.Margin = U.Th(10, 12, 0, 4)));
        void Item(string key, string glyph, string label)
        {
            var sel = _section == key;
            var row = U.Row(U.H(10, U.I(glyph, 15, sel ? Theme.Fg : Theme.Mu), U.T(label, 14, sel ? U.Bold : U.Semi, sel ? Theme.Fg : Theme.Mu)), () => { _section = key; Draw(); _sv.ChangeView(null, 0, null, true); }, sel, 8, U.Th(10, 0), U.Th(0, 1));
            row.Height = 38;
            _nav.Add(row);
        }
        Head("KONTO");
        Item("profile", "", "Mój profil");
        Item("account", "", "Konto");
        Item("security", "", "Bezpieczeństwo");
        Item("privacy", "", "Prywatność i blokady");
        Head("APLIKACJA");
        Item("voice", "", "Głos i wideo");
        Item("notify", "", "Powiadomienia");
        Item("app", "", "Aplikacja");
        _nav.Add(U.HLine(.08).With(l => l.Margin = U.Th(10, 10)));
        if (S.Me.IsAdmin) _nav.Add(U.Row(U.H(10, U.I("\uE7EF", 15, Theme.Accent), U.T("Panel admina GibCorda", 13.5, U.Semi, Theme.Accent)), () => _sh.ShowAdmin(), false, 8, U.Th(10, 0), U.Th(0, 1)).With(r => r.Height = 38));
        _nav.Add(U.Row(U.H(10, U.I("", 15, Theme.Mu), U.T($"Co nowego · {Updater.Version}", 13.5, U.Semi, Theme.Mu)), () => { _sh.CloseSettings(); _sh.OpenChannelByKind("changelog"); }, false, 8, U.Th(10, 0), U.Th(0, 1)).With(r => r.Height = 38));
        _nav.Add(U.Row(U.H(10, U.I("", 15, Theme.Red), U.T("Wyloguj się", 13.5, U.Semi, Theme.Red)), async () =>
        {
            if (!await M.Confirm(_sh, "Wylogować się?", "Na tym komputerze trzeba będzie zalogować się ponownie. Zapisane wiadomości znikną z dysku.", "Wyloguj")) return;
            await _sh.W.LogoutActive();
        }, false, 8, U.Th(10, 0), U.Th(0, 1)).With(r => r.Height = 38));

        _body.Children.Clear();
        switch (_section)
        {
            case "account": Account(); break;
            case "security": Security(); break;
            case "privacy": Privacy(); break;
            case "voice": Voice(); break;
            case "notify": Notify(); break;
            case "app": AppSection(); break;
            default: Profile(); break;
        }
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

    void Ok(string msg) => _sh.Info(msg);
    void Err(Exception ex) => _sh.Info(GibCordClient.Friendly(ex), true);

    static ToggleSwitch Toggle(string header, bool on, Action<bool> set)
    {
        var t = new ToggleSwitch { Header = U.T(header, 13.5, U.Semi), IsOn = on, OnContent = "Włączone", OffContent = "Wyłączone" };
        t.Toggled += (_, _) => set(t.IsOn);
        return t;
    }

    async Task<(Stream Data, string? Mime)?> PickImage()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _sh.W.Hwnd);
        var f = await picker.PickSingleFileAsync();
        if (f is null) return null;
        var props = await f.GetBasicPropertiesAsync();
        if (props.Size > 10 * 1024 * 1024) { _sh.Info("Obrazek może mieć najwyżej 10 MB", true); return null; }
        return (await f.OpenStreamForReadAsync(), string.IsNullOrEmpty(f.ContentType) ? null : f.ContentType);
    }

    async void UploadImage(string path, string ok)
    {
        if (await PickImage() is not { } img) return;
        try
        {
            using var d = img.Data;
            var r = await Api.PostImage(path, d, img.Mime);
            if (r.Prop("me").To<User>() is { } me) S.Apply(new GatewayEvent("user_update", System.Text.Json.JsonSerializer.SerializeToElement(new { user = me }, Json.Options)));
            Ok(ok);
            Draw();
        }
        catch (Exception ex) { Err(ex); }
    }

    static readonly string[] Colors = ["#ffb98f", "#e8708f", "#6fe3a5", "#7cc7ff", "#b69cff", "#f2c45a", "#4fd1c5", "#ff8a65", "#9be15d", "#f78fb3", "#c7b3ff", "#8b8599"];

    static WrapPanel Swatches(string? cur, Action<string?> pick, bool allowNone)
    {
        var w = new WrapPanel { Spacing = 8 };
        if (allowNone)
        {
            var none = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderBrush = cur is null ? Theme.Fg : U.W(.2), BorderThickness = U.Th(2), Background = Theme.AccentGradient() };
            none.Tip("Domyślny");
            none.Tapped += (_, _) => pick(null);
            w.Children.Add(none);
        }
        foreach (var c in Colors)
        {
            var on = string.Equals(cur, c, StringComparison.OrdinalIgnoreCase);
            var b = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = U.B(c), BorderBrush = on ? Theme.Fg : Theme.Transparent, BorderThickness = U.Th(3) };
            var col = c;
            b.Tapped += (_, _) => pick(col);
            w.Children.Add(b);
        }
        return w;
    }

    // ───────────────────────── Mój profil ─────────────────────────

    void Profile()
    {
        var me = S.Me;
        Title("Mój profil", "Tak widzą Cię inni — na serwerach, w rozmowach i na karcie profilu.");
        var display = new TextBox { Text = me.DisplayName ?? "", PlaceholderText = me.Username, MaxLength = 32 };
        var pronouns = new TextBox { Text = me.Pronouns ?? "", PlaceholderText = "np. ona/jej, on/jego", MaxLength = 40 };
        var bio = new TextBox { Text = me.Bio ?? "", PlaceholderText = "Kilka słów o sobie", MaxLength = 190, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
        var bioCount = U.T($"{bio.Text.Length}/190", 11, null, Theme.Mu2).With(t => t.HorizontalAlignment = HorizontalAlignment.Right);
        bio.TextChanged += (_, _) => bioCount.Text = $"{bio.Text.Length}/190";
        string? color = me.Color;
        string? banner = me.Banner;
        var colorBox = new ContentControl();
        var bannerBox = new ContentControl();
        void DrawColors()
        {
            colorBox.Content = Swatches(color, c => { color = c; DrawColors(); }, true);
            bannerBox.Content = Swatches(banner, c => { banner = c; DrawColors(); }, true);
        }
        DrawColors();
        DateTimeOffset? bday = DateTime.TryParse(me.Birthday, out var bd) ? new DateTimeOffset(bd) : null;
        var birthday = new CalendarDatePicker { Date = bday, PlaceholderText = "Wybierz datę", MinDate = new DateTimeOffset(1930, 1, 1, 0, 0, 0, TimeSpan.Zero), MaxDate = DateTimeOffset.Now };
        var showAge = new CheckBox { Content = "Pokazuj mój wiek", IsChecked = me.BirthdayShowAge };
        var save = U.Primary("Zapisz zmiany", async () =>
        {
            try
            {
                var body = new Dictionary<string, object?>
                {
                    ["displayName"] = display.Text.Trim(), ["pronouns"] = pronouns.Text.Trim(), ["bio"] = bio.Text.Trim(),
                    ["color"] = color, ["banner"] = banner,
                    ["birthday"] = birthday.Date is { } d ? d.ToString("yyyy-MM-dd") : null, ["birthdayShowAge"] = showAge.IsChecked == true,
                };
                var u = await Api.PatchMe(body);
                S.Apply(new GatewayEvent("user_update", System.Text.Json.JsonSerializer.SerializeToElement(new { user = u }, Json.Options)));
                Ok("Profil zapisany");
                Draw();
            }
            catch (Exception ex) { Err(ex); }
        }, 42).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Width = 200; });

        var form = U.V(10,
            Card(Label("Awatar"), U.H(14, new Avatar(72).Set(S, Api, me.Id), U.V(8,
                U.Ghost("Zmień awatar", () => UploadImage("me/avatar", "Awatar zmieniony"), 34),
                U.T("PNG, JPG, GIF (animowany) albo WebP, do 10 MB.", 11.5, null, Theme.Mu2, wrap: true)))),
            Card(Label("Nazwa wyświetlana"), display, Label("Zaimki"), pronouns, Label("O mnie"), bio, bioCount),
            Card(Label("Kolor nazwy (gdy rola nie nadaje koloru)"), colorBox),
            Card(Label("Baner"), bannerBox, U.H(8,
                U.Ghost("Wgraj obraz albo GIF", () => UploadImage("me/banner", "Baner zmieniony"), 34),
                me.BannerImage is not null ? U.Ghost("Usuń obraz", async () =>
                {
                    try { var r = await Api.DeleteJson("me/banner"); if (r.Prop("me").To<User>() is { } u) S.Apply(new GatewayEvent("user_update", System.Text.Json.JsonSerializer.SerializeToElement(new { user = u }, Json.Options))); Ok("Obraz banera usunięty"); Draw(); }
                    catch (Exception ex) { Err(ex); }
                }, 34) : null)),
            Card(Label("Urodziny"), birthday, showAge, U.T("W dniu urodzin przy nicku pojawi się 🎂, a znajomi dostaną przypomnienie.", 11.5, null, Theme.Mu2, wrap: true)),
            save);
        // podgląd obok tylko w szerokim oknie — w wąskim formularz ma całe miejsce
        var preview = _sh.ActualWidth > 1150 ? ProfileCard.Build(_sh, me.Id, () => { }) : null;
        var grid = new Grid { ColumnSpacing = 24 }.Cols("*", "auto").Add(form.At(0, 0));
        if (preview is not null)
            grid.Add(U.V(8, Label("Podgląd"), new Border { Child = preview, Background = Theme.Surface, CornerRadius = new CornerRadius(14) }).With(p => p.VerticalAlignment = VerticalAlignment.Top).At(0, 1));
        _body.MaxWidth = 1100;
        _body.Add(grid);
    }

    // ───────────────────────── Konto ─────────────────────────

    void Account()
    {
        _body.MaxWidth = 720;
        var me = S.Me;
        Title("Konto");
        var copy = U.Ghost("Kopiuj", () => { Sidebar.Copy("@" + me.Username); Ok("Skopiowano @" + me.Username); }, 30);
        _body.Add(Card(Label("Nazwa użytkownika"), U.H(10, U.T("@" + me.Username, 16, U.Bold), copy),
            U.T("Nazwy użytkownika nie da się zmienić — możesz zmienić nazwę wyświetlaną w „Mój profil”.", 12, null, Theme.Mu2, wrap: true)));

        // e-mail
        var email = new TextBox { PlaceholderText = "nowy@adres.pl" };
        var epass = new PasswordBox { PlaceholderText = "Obecne hasło" };
        var status = me.Email is null ? U.T("Brak adresu — dodaj go, żeby móc odzyskać hasło.", 13, null, Theme.Yellow, wrap: true)
            : me.EmailVerified ? U.H(8, U.T(me.Email, 14, U.Semi), U.Box(U.T("potwierdzony", 10.5, U.Bold, Theme.OnAccent), Theme.Green, 6, U.Th(6, 1)))
            : (FrameworkElement)U.V(6, U.H(8, U.T(me.Email, 14, U.Semi), U.Box(U.T("niepotwierdzony", 10.5, U.Bold, Theme.OnAccent), Theme.Yellow, 6, U.Th(6, 1))),
                U.Ghost("Wyślij link potwierdzający jeszcze raz", async () => { try { await Api.ResendEmail(); Ok("Wysłano — sprawdź pocztę (także „Spam”)"); } catch (Exception ex) { Err(ex); } }, 30));
        _body.Add(Card(Label("E-mail"), status, Label(me.Email is null ? "Dodaj adres" : "Zmień adres"), email, epass,
            U.Ghost(me.Email is null ? "Dodaj adres" : "Zmień adres", async () =>
            {
                try { var u = await Api.PatchMe(new { email = email.Text.Trim(), password = epass.Password }); S.Apply(new GatewayEvent("user_update", System.Text.Json.JsonSerializer.SerializeToElement(new { user = u }, Json.Options))); Ok("Zapisano — wysłaliśmy link potwierdzający"); Draw(); }
                catch (Exception ex) { Err(ex); }
            }, 34).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));

        // hasło
        var oldP = new PasswordBox { PlaceholderText = "Obecne hasło" };
        var newP = new PasswordBox { PlaceholderText = "Nowe hasło (co najmniej 8 znaków)" };
        var newP2 = new PasswordBox { PlaceholderText = "Powtórz nowe hasło" };
        var meter = U.T("", 12, U.Semi, Theme.Mu);
        newP.PasswordChanged += (_, _) =>
        {
            var p = newP.Password;
            var score = (p.Length >= 8 ? 1 : 0) + (p.Length >= 12 ? 1 : 0) + (p.Any(char.IsDigit) ? 1 : 0) + (p.Any(char.IsUpper) && p.Any(char.IsLower) ? 1 : 0) + (p.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
            (meter.Text, meter.Foreground) = p.Length == 0 ? ("", Theme.Mu) : score <= 2 ? ("Słabe hasło", Theme.Red) : score <= 3 ? ("Średnie hasło", Theme.Yellow) : ("Mocne hasło", Theme.Green);
        };
        _body.Add(Card(Label("Hasło"), oldP, newP, meter, newP2,
            U.Ghost("Zmień hasło", async () =>
            {
                if (newP.Password != newP2.Password) { _sh.Info("Nowe hasła się różnią", true); return; }
                try
                {
                    var check = await Api.PostJson("password/check", new { password = newP.Password });
                    if (check.Bool("ok") == false && !await M.Confirm(_sh, "Słabe hasło", (check.Str("message") ?? "To hasło jest łatwe do odgadnięcia.") + " Użyć go mimo to?", "Użyj mimo to")) return;
                }
                catch { /* sprawdzanie wycieków jest dodatkiem */ }
                try { await Api.PatchMe(new { oldPassword = oldP.Password, newPassword = newP.Password }); Ok("Hasło zmienione"); oldP.Password = newP.Password = newP2.Password = ""; }
                catch (Exception ex) { Err(ex); }
            }, 34).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));

        _body.Add(Card(Label("Na GibCordzie od"), U.T(Format.Local(me.CreatedAt).ToString("d MMMM yyyy", Format.Pl), 14, U.Semi)));
    }

    // ───────────────────────── Bezpieczeństwo ─────────────────────────

    void Security()
    {
        _body.MaxWidth = 720;
        var me = S.Me;
        Title("Bezpieczeństwo", "Logowanie dwuetapowe i urządzenia, na których jesteś zalogowany.");
        var mfaInfo = me.Mfa
            ? U.H(8, U.I("", 16, Theme.Green), U.T("Logowanie dwuetapowe jest włączone", 14, U.Bold, Theme.Green))
            : U.H(8, U.I("", 16, Theme.Yellow), U.T("Logowanie dwuetapowe jest wyłączone", 14, U.Bold, Theme.Yellow));
        var mfaCard = Card(Label("Logowanie dwuetapowe (2FA)"), mfaInfo,
            U.T(me.Mfa ? "Przy logowaniu na nowym urządzeniu poprosimy o 6 cyfr z aplikacji do kodów." : "Po włączeniu samo hasło nie wystarczy — potrzebny będzie też kod z telefonu (np. Google Authenticator, Microsoft Authenticator).", 12.5, null, Theme.Mu, wrap: true));
        var mfaBox = (StackPanel)mfaCard.Child;
        if (!me.Mfa) mfaBox.Add(U.Primary("Włącz logowanie dwuetapowe", () => MfaEnable(), 40).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Width = 280; }));
        else mfaBox.Add(U.H(8, U.Ghost("Nowe kody zapasowe", () => MfaCodes(), 34), U.Ghost("Wyłącz", () => MfaDisable(), 34).With(b => ((TextBlock)b.Content).Foreground = Theme.Red)));
        _body.Add(mfaCard);

        var list = new StackPanel { Spacing = 8 };
        list.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
        var logoutOthers = U.Ghost("Wyloguj wszystkie inne urządzenia", async () =>
        {
            if (!await M.Confirm(_sh, "Wylogować inne urządzenia?", "Wszędzie poza tym komputerem trzeba będzie zalogować się ponownie.", "Wyloguj")) return;
            try { await Api.PostJson("sessions/logout-others"); Ok("Wylogowano pozostałe urządzenia"); Draw(); } catch (Exception ex) { Err(ex); }
        }, 34).With(b => b.HorizontalAlignment = HorizontalAlignment.Left);
        _body.Add(Card(Label("Zalogowane urządzenia"), list, logoutOthers));
        _ = FillSessions(list);
    }

    async Task FillSessions(StackPanel list)
    {
        try
        {
            var arr = await Api.GetJson("sessions");
            list.Children.Clear();
            foreach (var s in arr.EnumerateArray())
            {
                var id = s.Str("id") ?? "";
                var cur = s.Bool("current") == true;
                var (icon, label) = Device(s.Str("userAgent") ?? "");
                var meta = (s.Str("ip") is { } ip ? ip + " · " : "") + "aktywne " + Format.When(s.Long("lastSeen") ?? s.Long("createdAt") ?? 0);
                var g = new Grid().Cols("auto", "*", "auto").Add(U.I(icon, 22, Theme.Mu).With(i => i.Margin = U.Th(0, 0, 14, 0)).At(0, 0),
                    U.V(1, U.H(8, U.T(label, 14, U.Semi), cur ? U.Box(U.T("to urządzenie", 10.5, U.Bold, Theme.OnAccent), Theme.Accent, 6, U.Th(6, 1)) : null), U.T(meta, 12, null, Theme.Mu2)).At(0, 1));
                if (!cur)
                    g.Add(U.Ghost("Wyloguj", async () =>
                    {
                        try { await Api.DeleteJson("sessions/" + Uri.EscapeDataString(id)); Ok("Urządzenie wylogowane"); Draw(); } catch (Exception ex) { Err(ex); }
                    }, 30).At(0, 2));
                list.Add(new Border { Child = g, Padding = U.Th(4, 6), BorderBrush = U.W(.06), BorderThickness = U.Th(0, 0, 0, 1) });
            }
        }
        catch (Exception ex) { list.Children.Clear(); list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    }

    /// <summary>Nazwa urządzenia z User-Agent (jak devLabel na stronie).</summary>
    static (string Icon, string Label) Device(string ua)
    {
        if (ua.Contains("GibCord-Windows")) return ("", "Aplikacja GibCord 6 · Windows");
        if (ua.Contains("Electron") || ua.Contains("GibCord/")) return ("", "Aplikacja GibCord (stara) · Windows");
        var os = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android" : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : "";
        var br = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox" : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "Przeglądarka";
        var phone = os is "iPhone" or "Android";
        return (phone ? "" : "", string.IsNullOrEmpty(os) ? br : $"{br} · {os}");
    }

    async void MfaEnable()
    {
        var pass = new PasswordBox { PlaceholderText = "Twoje hasło" };
        var d1 = new ContentDialog { Title = "Włącz logowanie dwuetapowe", Content = U.V(8, U.T("Najpierw potwierdź, że to Ty.", 13, null, Theme.Mu), pass), PrimaryButtonText = "Dalej", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark };
        if (await d1.ShowAsync() != ContentDialogResult.Primary) return;
        System.Text.Json.JsonElement setup;
        try { setup = await Api.PostJson("mfa/setup", new { password = pass.Password }); }
        catch (Exception ex) { Err(ex); return; }
        var qr = new Image { Width = 200, Height = 200 };
        if (setup.Str("qr") is { } svg)
        {
            try
            {
                var src = new SvgImageSource();
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
                await src.SetSourceAsync(ms.AsRandomAccessStream());
                qr.Source = src;
            }
            catch { }
        }
        var code = new TextBox { PlaceholderText = "123456", MaxLength = 7, FontSize = 18, TextAlignment = TextAlignment.Center };
        var secret = setup.Str("secret") ?? "";
        var d2 = new ContentDialog
        {
            Title = "Zeskanuj kod",
            Content = U.V(10, U.T("1. Otwórz aplikację do kodów i zeskanuj kod QR (albo wpisz klucz ręcznie).", 13, null, Theme.Mu, wrap: true),
                new Border { Background = U.B("#ffffff"), CornerRadius = new CornerRadius(10), Padding = U.Th(8), HorizontalAlignment = HorizontalAlignment.Center, Child = qr },
                U.H(8, U.T(secret, 12.5, U.Semi).With(t => { t.FontFamily = Theme.Mono; t.IsTextSelectionEnabled = true; }), U.Ghost("Kopiuj", () => Sidebar.Copy(secret), 28)),
                U.T("2. Wpisz 6 cyfr, które pokazuje aplikacja:", 13, null, Theme.Mu, wrap: true), code).With(p => p.Width = 360),
            PrimaryButtonText = "Włącz", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        while (true)
        {
            if (await d2.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                var r = await Api.PostJson("mfa/enable", new { code = code.Text.Replace(" ", "") });
                if (r.Prop("me").To<User>() is { } u) S.Apply(new GatewayEvent("user_update", System.Text.Json.JsonSerializer.SerializeToElement(new { user = u }, Json.Options)));
                ShowCodes(r.Prop("backupCodes"));
                return;
            }
            catch (Exception ex) { Err(ex); code.Text = ""; }
        }
    }

    async void MfaDisable()
    {
        var pass = new PasswordBox { PlaceholderText = "Twoje hasło" };
        var code = new TextBox { PlaceholderText = "Kod z aplikacji albo kod zapasowy" };
        var d = new ContentDialog { Title = "Wyłączyć logowanie dwuetapowe?", Content = U.V(8, U.T("Konto będzie chronione tylko hasłem.", 13, null, Theme.Mu, wrap: true), pass, code), PrimaryButtonText = "Wyłącz", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var r = await Api.PostJson("mfa/disable", new { password = pass.Password, code = code.Text.Replace(" ", "") });
            if (r.Prop("me").To<User>() is { } u) S.Apply(new GatewayEvent("user_update", System.Text.Json.JsonSerializer.SerializeToElement(new { user = u }, Json.Options)));
            Ok("Logowanie dwuetapowe wyłączone");
        }
        catch (Exception ex) { Err(ex); }
    }

    async void MfaCodes()
    {
        var code = new TextBox { PlaceholderText = "Kod z aplikacji" };
        var d = new ContentDialog { Title = "Nowe kody zapasowe", Content = U.V(8, U.T("Stare kody przestaną działać.", 13, null, Theme.Mu), code), PrimaryButtonText = "Wygeneruj", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        try { var r = await Api.PostJson("mfa/backup-codes", new { code = code.Text.Replace(" ", "") }); ShowCodes(r.Prop("backupCodes")); }
        catch (Exception ex) { Err(ex); }
    }

    async void ShowCodes(System.Text.Json.JsonElement codes)
    {
        var list = codes.ValueKind == System.Text.Json.JsonValueKind.Array ? codes.EnumerateArray().Select(c => c.GetString() ?? "").ToList() : [];
        var text = string.Join("\n", list);
        var d = new ContentDialog
        {
            Title = "Kody zapasowe",
            Content = U.V(10, U.T("Zapisz je w bezpiecznym miejscu. Każdy działa raz — gdy zgubisz telefon, zalogujesz się kodem zamiast cyfr z aplikacji.", 13, null, Theme.Mu, wrap: true),
                new Border { Background = U.W(.06), CornerRadius = new CornerRadius(10), Padding = U.Th(14, 10), Child = U.T(text, 15, U.Semi, wrap: true).With(t => { t.FontFamily = Theme.Mono; t.IsTextSelectionEnabled = true; }) }),
            PrimaryButtonText = "Kopiuj wszystkie", CloseButtonText = "Gotowe", XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() == ContentDialogResult.Primary) { Sidebar.Copy(text); Ok("Skopiowano kody zapasowe"); }
        Draw();
    }

    // ───────────────────────── Prywatność ─────────────────────────

    void Privacy()
    {
        _body.MaxWidth = 720;
        Title("Prywatność i blokady");
        var p = App.Prefs;
        _body.Add(Card(Label("Aktywność"),
            Toggle("Pokazuj innym, w co grasz", p.GameDetection, v => { p.GameDetection = v; p.Save(); }),
            Toggle("Pokazuj, czego słuchasz w Spotify", p.MusicDetection, v => { p.MusicDetection = v; p.Save(); }),
            U.T("Gry i muzykę wykrywa ta aplikacja na Twoim komputerze. Po wyłączeniu nikt ich nie zobaczy.", 12, null, Theme.Mu2, wrap: true)));
        var me = S.Me;
        var spotify = U.V(8);
        if (me.SpotifyConnected)
        {
            spotify.Add(U.H(8, U.T("🎵 Konto Spotify połączone", 14, U.Semi, Theme.Green)));
            spotify.Add(Toggle("Pokazuj innym, czego słuchasz (z konta Spotify)", me.ShowActivity, async v =>
            {
                try { var u = await Api.PatchMe(new { showActivity = v }); S.Apply(new GatewayEvent("user_update", System.Text.Json.JsonSerializer.SerializeToElement(new { user = u }, Json.Options))); }
                catch (Exception ex) { Err(ex); }
            }));
            spotify.Add(U.Ghost("Rozłącz Spotify", async () =>
            {
                try { await Api.DeleteJson("spotify"); Ok("Spotify rozłączone"); Draw(); } catch (Exception ex) { Err(ex); }
            }, 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        }
        else
        {
            spotify.Add(U.T("Połącz konto Spotify, żeby muzyka była widoczna też wtedy, gdy słuchasz na telefonie albo w przeglądarce.", 12.5, null, Theme.Mu, wrap: true));
            spotify.Add(U.Ghost("Połącz ze Spotify", async () =>
            {
                try { var r = await Api.GetJson("spotify/connect"); if (r.Str("url") is { } url) { _ = Launcher.LaunchUriAsync(new Uri(url)); Ok("Dokończ w przeglądarce — potem wróć tutaj"); } }
                catch (Exception ex) { Err(ex); }
            }, 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        }
        _body.Add(Card(Label("Spotify"), spotify));
        var blocked = new StackPanel { Spacing = 4 };
        foreach (var uid in S.Blocked)
        {
            var id = uid;
            var u = S.U(uid);
            var g = new Grid().Cols("auto", "*", "auto").Add(new Avatar(32).Set(S, Api, uid).At(0, 0),
                U.V(0, U.T(u?.Name ?? "Nieznana osoba", 14, U.Semi), U.T("@" + (u?.Username ?? "?"), 11.5, null, Theme.Mu2)).With(x => x.Margin = U.Th(12, 0)).At(0, 1),
                U.Ghost("Odblokuj", () => UserMenu.SetBlocked(_sh, id, false), 30).At(0, 2));
            blocked.Add(new Border { Child = g, Padding = U.Th(4, 6) });
        }
        if (S.Blocked.Count == 0) blocked.Add(U.T("Nikogo nie blokujesz. Zablokować możesz z menu osoby (prawy przycisk na nazwie).", 13, null, Theme.Mu, wrap: true));
        _body.Add(Card(Label($"Zablokowane osoby — {S.Blocked.Count}"), blocked,
            U.T("Zablokowana osoba nie napisze do Ciebie prywatnie, nie zadzwoni i nie zaprosi do znajomych. Jej wiadomości na wspólnych serwerach są schowane.", 12, null, Theme.Mu2, wrap: true)));
    }

    // ───────────────────────── Głos, powiadomienia, aplikacja ─────────────────────────

    void Voice()
    {
        _body.MaxWidth = 720;
        Title("Głos i wideo", "Mikrofon, głośniki, czułość, „naciśnij, aby mówić” i redukcja szumów.");
        _body.Add(Card(U.T("Wszystkie ustawienia rozmów są w jednym okienku — z testem mikrofonu.", 13, null, Theme.Mu, wrap: true),
            U.Primary("Otwórz ustawienia głosu", () => VoiceSettings.Show(_sh), 40).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Width = 260; })));
        var p = App.Prefs;
        _body.Add(Card(Label("Dźwięki"), Toggle("Dźwięki rozmów (wejście, wyjście, wyciszenie)", p.Sounds, v => { p.Sounds = v; p.Save(); })));
    }

    void Notify()
    {
        _body.MaxWidth = 720;
        Title("Powiadomienia", "Dymki Windows przy nowych wiadomościach. Na serwerach domyślnie tylko wzmianki — zmienisz to w menu serwera albo kanału.");
        var p = App.Prefs;
        _body.Add(Card(
            Toggle("Powiadomienia na pulpicie", p.Notifications, v => { p.Notifications = v; p.Save(); }),
            Toggle("Dźwięk nowej wiadomości", p.MessageSound, v => { p.MessageSound = v; p.Save(); }),
            U.T("Status „Nie przeszkadzać” wycisza wszystkie powiadomienia.", 12, null, Theme.Mu2, wrap: true),
            U.Ghost("Wyślij próbne powiadomienie", () => Toasts.Show("GibCord", "Tak wyglądają powiadomienia z GibCorda 🎉", 0, "test"), 34).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
    }

    void AppSection()
    {
        _body.MaxWidth = 720;
        Title("Aplikacja", $"GibCord {Updater.Version} dla Windows.");
        var p = App.Prefs;
        // motyw (wspólny ze stroną — zapisany na koncie)
        var themes = new WrapPanel { Spacing = 10 };
        foreach (var (key, name, desc) in Theme.All)
        {
            var on = Theme.Name == key;
            var (c1, c2, bg) = key switch { "midnight" => ("#ffb98f", "#e8708f", "#000000"), "forest" => ("#d9ed92", "#76c893", "#0f1a14"), "cherry" => ("#ffb3c6", "#ff4d6d", "#1a0a14"), _ => ("#ffb98f", "#e8708f", "#1b1531") };
            var sw = new Grid { Width = 120, Height = 64 }.Add(new Border { Background = U.B(bg), CornerRadius = new CornerRadius(10) },
                new Border { Width = 46, Height = 14, CornerRadius = new CornerRadius(7), Margin = U.Th(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                    Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = U.C(c1), Offset = 0 }, new GradientStop { Color = U.C(c2), Offset = 1 } } } });
            var card = new Border { Child = U.V(6, sw, U.T(name, 13, U.Bold), U.T(desc, 11, null, Theme.Mu2, wrap: true).With(t => t.MaxWidth = 120)), Padding = U.Th(8), CornerRadius = new CornerRadius(12),
                BorderBrush = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(2), Background = U.W(.03) };
            var k = key;
            card.Tapped += async (_, _) =>
            {
                try
                {
                    var settings = new Dictionary<string, object?>();
                    if (S.Me.Settings.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var pr in S.Me.Settings.EnumerateObject()) settings[pr.Name] = pr.Value;
                    settings["theme"] = k;
                    var u = await Api.PatchMe(new { settings });
                    S.Apply(new GatewayEvent("user_update", System.Text.Json.JsonSerializer.SerializeToElement(new { user = u }, Json.Options)));
                }
                catch (Exception ex) { Err(ex); return; } // bez zapisu na koncie motyw wróciłby po ponownym połączeniu
                _sh.W.ApplyTheme(k);
            };
            themes.Children.Add(card);
        }
        FrameworkElement? glass = Theme.GlassKind switch
        {
            "mica" => Toggle("Szkło Windows 11: okno lekko prześwituje pulpit", p.WindowsGlass, v => _sh.W.SetGlass(v)),
            "acrylic" => Toggle("Szkło Windows: rozmyte tło okna", p.WindowsGlass, v => _sh.W.SetGlass(v)),
            _ => null,
        };
        _body.Add(Card(Label("Motyw"), themes, U.T("Motyw zapisuje się na koncie — strona i telefon też go dostaną.", 11.5, null, Theme.Mu2, wrap: true), glass));
        var fontSize = new ComboBox { Header = U.T("Rozmiar tekstu wiadomości", 13.5, U.Semi), Width = 220 };
        var sizes = new[] { 13.0, 14.5, 16.5, 18.0 };
        foreach (var label in new[] { "Mały", "Zwykły", "Duży", "Bardzo duży" }) fontSize.Items.Add(label);
        fontSize.SelectedIndex = Math.Max(0, Array.IndexOf(sizes, p.MessageFontSize));
        fontSize.SelectionChanged += (_, _) => { p.MessageFontSize = sizes[Math.Max(0, fontSize.SelectedIndex)]; p.Save(); _sh.CurrentChat?.Rerender(); };
        _body.Add(Card(Label("Wygląd wiadomości"), fontSize,
            Toggle("Tryb zwarty (bez awatarów, godzina przy każdej wiadomości)", p.CompactMessages, v => { p.CompactMessages = v; p.Save(); _sh.CurrentChat?.Rerender(); }),
            Toggle("Mniej ruchu (bez animacji; tak samo, gdy animacje są wyłączone w Windows)", p.ReduceMotion, v => { p.ReduceMotion = v; p.Save(); }),
            Toggle("Kółko myszy z wygładzaniem (płynne przewijanie zamiast skoków)", p.SmoothWheel, v => { p.SmoothWheel = v; p.Save(); }),
            U.T("Zmiana działa od razu w otwartej rozmowie.", 11.5, null, Theme.Mu2)));
        _body.Add(Card(Label("Okno"),
            Toggle("Zamykanie do zasobnika (obok zegara)", p.CloseToTray, v => { p.CloseToTray = v; p.Save(); }),
            Toggle("Uruchamiaj razem z Windowsem", AutoStart.Enabled, AutoStart.Set),
            Toggle("Lista osób po prawej", p.MembersPanel, _ => _sh.ToggleMembers())));
        var mode = new RadioButtons { Header = U.T("Kiedy pokazywać", 13.5, U.Semi) };
        foreach (var label in new[] { "Wyłączona", "Tylko gdy grasz", "Zawsze w rozmowie" }) mode.Items.Add(label);
        mode.SelectedIndex = p.OverlayMode switch { "off" => 0, "always" => 2, _ => 1 };
        mode.SelectionChanged += (_, _) => { p.OverlayMode = mode.SelectedIndex switch { 0 => "off", 2 => "always", _ => "game" }; p.Save(); };
        var corner = new ComboBox { Header = U.T("Róg ekranu", 13.5, U.Semi), Width = 220 };
        foreach (var label in new[] { "Lewy górny", "Prawy górny", "Lewy dolny", "Prawy dolny" }) corner.Items.Add(label);
        corner.SelectedIndex = p.OverlayCorner switch { "tr" => 1, "bl" => 2, "br" => 3, _ => 0 };
        corner.SelectionChanged += (_, _) => { p.OverlayCorner = corner.SelectedIndex switch { 1 => "tr", 2 => "bl", 3 => "br", _ => "tl" }; p.Save(); };
        var size = new ComboBox { Header = U.T("Rozmiar", 13.5, U.Semi), Width = 220 };
        foreach (var label in new[] { "Mała", "Zwykła", "Duża" }) size.Items.Add(label);
        size.SelectedIndex = p.OverlaySize switch { "s" => 0, "l" => 2, _ => 1 };
        size.SelectionChanged += (_, _) => { p.OverlaySize = size.SelectedIndex switch { 0 => "s", 2 => "l", _ => "m" }; p.Save(); };
        _body.Add(Card(Label("Nakładka w grze (Ctrl+Shift+O)"), mode, corner, size,
            Toggle("Nowe wiadomości w nakładce, gdy grasz (dymek na kilka sekund)", p.OverlayMessages, v => { p.OverlayMessages = v; p.Save(); }),
            U.T("Pokazuje w grze, kto jest na kanale i kto mówi.", 12, null, Theme.Mu2, wrap: true)));
        _body.Add(Card(Label("Aktualizacje"), U.T($"Masz wersję {Updater.Version}. Nowe wersje instalują się same po ponownym uruchomieniu.", 13, null, Theme.Mu, wrap: true),
            U.Ghost("Sprawdź teraz", async () =>
            {
                var err = await Updater.Check(v => _sh.ShowUpdate(v));
                _sh.Info(err ?? "Szukam nowej wersji… jeśli jest, pojawi się pasek u góry.", err is not null);
            }, 34).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        var keys = new StackPanel { Spacing = 8 };
        keys.Add(Label("Skróty klawiszowe"));
        foreach (var (k, w) in Shortcuts) keys.Add(Shortcut(k, w));
        keys.Add(Toggle("Skróty Ctrl+Shift+M i Ctrl+Shift+D w całym systemie w trakcie rozmowy", p.GlobalShortcuts, v => { p.GlobalShortcuts = v; p.Save(); _sh.W.Keys?.Sync(); }));
        _body.Add(Card(keys));
        _body.Add(Card(Label("Pomoc i diagnostyka"),
            U.T("Coś nie działa (np. głos)? Skopiuj dane diagnostyczne i wklej je osobie, która pomaga. Nie zawierają haseł ani treści wiadomości.", 12.5, null, Theme.Mu, wrap: true),
            U.H(8,
                U.Ghost("Kopiuj dane diagnostyczne", () => { Sidebar.Copy(Diagnostics()); _sh.Info("Skopiowano — wklej to (Ctrl+V) w wiadomości."); }, 34),
                U.Ghost("Otwórz folder z dziennikiem", () => _ = Launcher.LaunchFolderPathAsync(App.DataDir), 34))));
        _body.Add(Card(Label("Strona"), U.Ghost("Otwórz GibCorda w przeglądarce", () => _ = Launcher.LaunchUriAsync(Api.BaseUri), 34).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
    }

    /// <summary>Wersja, system, urządzenia dźwięku, stan rozmowy i końcówka dziennika — bez tokenów i treści wiadomości.</summary>
    string Diagnostics()
    {
        var c = _sh.C;
        var v = c.Voice;
        var o = v.Options;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"GibCord {Updater.Version} · Windows {Environment.OSVersion.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")} · .NET {Environment.Version}");
        sb.AppendLine($"Serwer: {Api.BaseUri.Host} · połączenie: {c.Status} · ping: {(c.Rtt is { } r ? Math.Round(r.TotalMilliseconds) + " ms" : "—")}");
        sb.AppendLine($"Motyw: {App.Prefs.Theme} · skala: {XamlRoot?.RasterizationScale:0.##}");
        try
        {
            sb.AppendLine("Mikrofony: " + string.Join(" | ", AudioDevices.Inputs().Select(d => d.Name)));
            sb.AppendLine("Głośniki: " + string.Join(" | ", AudioDevices.Outputs().Select(d => d.Name)));
        }
        catch (Exception ex) { sb.AppendLine("Urządzenia: błąd " + ex.Message); }
        sb.AppendLine($"Wybrane: mikrofon „{(o.InputDevice == "" ? "domyślny" : o.InputDevice)}”, głośniki „{(o.OutputDevice == "" ? "domyślne" : o.OutputDevice)}”");
        sb.AppendLine($"Tryb: {o.Mode} · czułość auto: {o.AutoSens} · szumy: {o.NoiseSuppression} · echo: {o.EchoCancellation} · wzmocnienie: {o.AutoGain}");
        sb.AppendLine($"Rozmowa: kanał {c.VoiceChannelId} · silnik: {v.State} · jakość: {v.Quality} · wyciszony: {o.Muted} · bez dźwięku: {o.Deafened}");
        sb.AppendLine($"Dźwięk: {(_sh.W.Voice?.AudioError ?? "bez błędów")}");
        sb.AppendLine("— dziennik (ostatnie 80 linii) —");
        sb.AppendLine(App.LogTail(80));
        // na wszelki wypadek: tokeny (JWT, ?token=…) i adresy e-mail nie wychodzą z komputera
        var text = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"eyJ[\w-]+\.[\w-]+\.[\w-]+", "[token]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"((?:access_)?token=)[^&\s""]+", "$1[ukryty]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(text, @"[\w.+-]+@[\w-]+\.[\w.-]+", "[e-mail]");
    }

    /// <summary>Wszystkie skróty — w ustawieniach i w ściądze pod Ctrl+/.</summary>
    public static readonly (string Keys, string What)[] Shortcuts =
    [
        ("Ctrl+K", "Szybkie przejście do kanału, osoby albo serwera"),
        ("Ctrl+F", "Szukaj wiadomości"),
        ("Ctrl+I", "Skrzynka: wzmianki, zapisane, przypomnienia"),
        ("Ctrl+/", "Ta ściąga ze skrótami"),
        ("Alt+↑ / Alt+↓", "Poprzedni / następny kanał"),
        ("Alt+Shift+↑ / ↓", "Poprzedni / następny nieprzeczytany kanał"),
        ("Alt+← / Alt+→", "Wstecz / dalej"),
        ("↑ w pustym polu", "Edytuj ostatnią wiadomość"),
        (":serce", "Podpowiedzi emoji w polu pisania (Tab albo Enter wstawia)"),
        ("@imię", "Oznacz osobę albo rolę"),
        ("Shift+Enter", "Nowa linia w wiadomości"),
        ("Ctrl+Shift+O", "Nakładka w grze"),
        ("Ctrl+Shift+M", "Wycisz / włącz mikrofon (w rozmowie, działa też w grze)"),
        ("Ctrl+Shift+D", "Wyłącz / włącz dźwięk (w rozmowie, działa też w grze)"),
        ("Esc", "Zamknij okienko, wątek, odpowiedź"),
    ];

    public static FrameworkElement Shortcut(string keys, string what) => new Grid().Cols("170", "*").Add(
        U.Box(U.T(keys, 12, U.Bold).With(t => t.FontFamily = Theme.Mono), U.W(.08), 6, U.Th(8, 3)).With(b => b.HorizontalAlignment = HorizontalAlignment.Left).At(0, 0),
        U.T(what, 13, null, Theme.Mu, wrap: true).At(0, 1));
}
