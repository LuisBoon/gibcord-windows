using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>Logowanie: nazwa + hasło, potem (jeśli włączone) kod z aplikacji do logowania dwuetapowego.</summary>
public sealed class LoginPage : Grid
{
    readonly MainWindow _w;
    readonly StackPanel _form = new() { Spacing = 14 };
    readonly TextBlock _error;
    string? _ticket;
    bool _busy;

    readonly long _backTo;

    public LoginPage(MainWindow w, string? message, long backTo = 0)
    {
        _w = w;
        _backTo = backTo;
        _error = U.T("", 13, null, Theme.Red, wrap: true).With(t => t.Visibility = Visibility.Collapsed);
        var card = new Border
        {
            Width = 420, Padding = U.Th(36, 32), CornerRadius = new CornerRadius(22), Background = Theme.Glass70,
            BorderBrush = U.W(.12), BorderThickness = U.Th(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = _form,
        };
        Children.Add(new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ShowPassword(message);
    }

    static TextBlock Label(string t) => U.T(t, 12, U.Semi, Theme.Mu).With(x => x.Margin = U.Th(2, 0, 0, -8));

    void Header(string title, string sub)
    {
        _form.Children.Clear();
        _form.Children.Add(new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center });
        _form.Children.Add(U.T(title, 24, U.Bold).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center; }));
        _form.Children.Add(U.T(sub, 13.5, null, Theme.Mu, wrap: true).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; t.Margin = U.Th(0, -8, 0, 6); }));
    }

    static TextBox Field(string placeholder) => new()
    {
        PlaceholderText = placeholder, Height = 42, FontSize = 14, FontFamily = Theme.Font, CornerRadius = new CornerRadius(10), Padding = U.Th(12, 10),
        IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
    };

    void ShowPassword(string? message)
    {
        _ticket = null;
        Header("Witaj w GibCordzie 6.0", Updater.IsPrerelease ? "Nowa aplikacja na Windows (wersja beta). Zaloguj się tym samym kontem co na stronie." : "Nowa aplikacja na Windows. Zaloguj się tym samym kontem co na stronie.");
        // uruchomienie z innym serwerem (--server) — wyraźne ostrzeżenie: spreparowany skrót mógłby podsunąć fałszywy serwer do zbierania haseł
        if (Options.OtherServer)
            _form.Children.Add(U.Box(U.T($"Uwaga: logujesz się do innego serwera niż GibCord — {new Uri(Options.Server).Authority}. Jeśli nie wiesz, skąd ten skrót, nie podawaj tu hasła.", 12.5, U.Semi, U.B("#ffffff"), wrap: true), U.B("#d6455e"), 10, U.Th(12, 8)));
        var user = Field("Twoja nazwa użytkownika");
        var pass = new PasswordBox { PlaceholderText = "Hasło", Height = 42, FontSize = 14, CornerRadius = new CornerRadius(10), Padding = U.Th(12, 10) };
        Button? go = null;
        void Submit() { if (go is not null) _ = DoLogin(user.Text, pass.Password, go); }
        go = U.Primary("Zaloguj się", Submit, 44);
        user.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { pass.Focus(FocusState.Keyboard); e.Handled = true; } };
        pass.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Submit(); e.Handled = true; } };

        _form.Add(Label("NAZWA UŻYTKOWNIKA"), user, Label("HASŁO"), pass, _error, go.With(b => b.Margin = U.Th(0, 6, 0, 0)));
        _form.Add(Action("Nie pamiętasz hasła?", () => ShowForgot(user.Text)));
        _form.Add(U.HLine(.1).With(l => l.Margin = U.Th(0, 4)));
        _form.Add(U.H(6,
            U.T("Nie masz konta?", 13, null, Theme.Mu),
            Action("Załóż konto", ShowRegister)).With(p => p.HorizontalAlignment = HorizontalAlignment.Center));
        // kilka kont: zapisane konta na tym komputerze i powrót do poprzedniego
        var saved = Accounts.Others(0).ToList();
        if (saved.Count > 0)
        {
            _form.Add(U.HLine(.1).With(l => l.Margin = U.Th(0, 4)));
            _form.Add(U.T(_backTo != 0 ? "TWOJE KONTA NA TYM KOMPUTERZE · WRÓĆ DO" : "TWOJE KONTA NA TYM KOMPUTERZE", 11, U.Bold, Theme.Mu2));
            foreach (var a in saved)
            {
                var row = U.Row(U.H(10, U.Box(U.T(a.Name.Length > 0 ? a.Name[..1].ToUpperInvariant() : "?", 13, U.Bold, Theme.OnAccent), U.B(a.Color), 16, U.Th(9, 4)),
                    U.V(0, U.T(a.Name, 13.5, U.Semi), U.T("@" + a.Username, 11.5, null, Theme.Mu))), () => _ = _w.ResumeAccount(a.Id), false, 10, U.Th(8, 6), U.Th(0, 1));
                _form.Add(row);
            }
        }
        SetError(message);
        user.Loaded += (_, _) => user.Focus(FocusState.Programmatic);
    }

    void ShowMfa()
    {
        Header("Logowanie dwuetapowe", "Wpisz 6 cyfr z aplikacji do kodów (np. Google Authenticator) albo jeden z kodów zapasowych.");
        var code = Field("123 456");
        code.FontSize = 20;
        code.TextAlignment = TextAlignment.Center;
        code.CharacterSpacing = 200;
        Button? go = null;
        void Submit() { if (go is not null) _ = DoMfa(code.Text, go); }
        go = U.Primary("Potwierdź", Submit, 44);
        code.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Submit(); e.Handled = true; } };
        code.TextChanged += (_, _) => { var d = new string(code.Text.Where(char.IsDigit).ToArray()); if (d.Length == 6 && code.Text.Length <= 7) Submit(); };
        _form.Add(Label("KOD"), code, _error, go);
        _form.Add(U.Ghost("Wróć do hasła", () => ShowPassword(null)).With(b => b.HorizontalAlignment = HorizontalAlignment.Center));
        SetError(null);
        code.Loaded += (_, _) => code.Focus(FocusState.Programmatic);
    }

    static HyperlinkButton Action(string text, Action a) => new HyperlinkButton
    {
        Content = U.T(text, 13, U.Semi, Theme.Accent), Padding = U.Th(0), HorizontalAlignment = HorizontalAlignment.Center,
    }.With(b => b.Click += (_, _) => a());

    void ShowRegister()
    {
        Header("Załóż konto", "Jedno konto działa wszędzie: w aplikacji, na stronie i na telefonie.");
        var user = Field("np. ola_123");
        var display = Field("np. Ola (opcjonalnie)");
        var email = Field("twoj@adres.pl");
        email.InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.EmailSmtpAddress) } };
        var pass = new PasswordBox { PlaceholderText = "Hasło (co najmniej 8 znaków)", Height = 42, FontSize = 14, CornerRadius = new CornerRadius(10), Padding = U.Th(12, 10) };
        var pass2 = new PasswordBox { PlaceholderText = "Powtórz hasło", Height = 42, FontSize = 14, CornerRadius = new CornerRadius(10), Padding = U.Th(12, 10) };
        Button? go = null;
        async void Submit()
        {
            if (_busy || go is null) return;
            if (pass.Password != pass2.Password) { SetError("Hasła się różnią."); return; }
            _busy = true; go.IsEnabled = false; SetError(null);
            try
            {
                var r = await _w.Client.Register(user.Text, pass.Password, display.Text, email.Text);
                if (r.Token is not null) _w.LoggedIn();
                else SetError("Konto założone — zaloguj się.");
            }
            catch (Exception ex) { SetError(GibCordClient.Friendly(ex)); }
            finally { _busy = false; go.IsEnabled = true; }
        }
        go = U.Primary("Załóż konto", Submit, 44);
        pass2.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Submit(); e.Handled = true; } };
        _form.Add(Label("NAZWA UŻYTKOWNIKA (a–z, 0–9, _ i .)"), user, Label("NAZWA WYŚWIETLANA"), display, Label("E-MAIL (wyślemy link potwierdzający)"), email,
            Label("HASŁO"), pass, pass2, _error, go.With(b => b.Margin = U.Th(0, 6, 0, 0)));
        _form.Add(U.T("Zakładając konto, akceptujesz zasady GibCorda: szanuj innych, bez spamu i treści nielegalnych.", 11.5, null, Theme.Mu2, wrap: true).With(t => t.TextAlignment = TextAlignment.Center));
        _form.Add(U.Ghost("Mam już konto", () => ShowPassword(null)).With(b => b.HorizontalAlignment = HorizontalAlignment.Center));
        SetError(null);
        user.Loaded += (_, _) => user.Focus(FocusState.Programmatic);
    }

    void ShowForgot(string login)
    {
        Header("Nie pamiętasz hasła?", "Podaj nazwę użytkownika albo e-mail. Jeśli adres jest potwierdzony, wyślemy na niego link do ustawienia nowego hasła.");
        var box = Field("nazwa użytkownika albo e-mail");
        box.Text = login;
        Button? go = null;
        async void Submit()
        {
            if (_busy || go is null || string.IsNullOrWhiteSpace(box.Text)) return;
            _busy = true; go.IsEnabled = false; SetError(null);
            try
            {
                await _w.Client.Api.ForgotPassword(box.Text.Trim());
                Header("Sprawdź skrzynkę", "Jeśli to konto ma potwierdzony e-mail, za chwilę przyjdzie link do zmiany hasła (ważny godzinę). Otwórz go, ustaw nowe hasło i wróć tutaj.");
                _form.Add(U.Primary("Wróć do logowania", () => ShowPassword(null), 44));
            }
            catch (Exception ex) { SetError(GibCordClient.Friendly(ex)); }
            finally { _busy = false; go.IsEnabled = true; }
        }
        go = U.Primary("Wyślij link", Submit, 44);
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Submit(); e.Handled = true; } };
        _form.Add(Label("KONTO"), box, _error, go);
        _form.Add(U.Ghost("Wróć do logowania", () => ShowPassword(null)).With(b => b.HorizontalAlignment = HorizontalAlignment.Center));
        SetError(null);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
    }

    static HyperlinkButton Link(string text, string url) => new()
    {
        Content = U.T(text, 13, U.Semi, Theme.Accent), NavigateUri = new Uri(url), Padding = U.Th(0),
    };

    static FrameworkElement LinkRow(string text, string url) => Link(text, url).With(l => l.HorizontalAlignment = HorizontalAlignment.Center);

    void SetError(string? msg)
    {
        _error.Text = msg ?? "";
        _error.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
    }

    async Task DoLogin(string user, string pass, Button go)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass)) { SetError("Wpisz nazwę użytkownika i hasło."); return; }
        _busy = true; go.IsEnabled = false; SetError(null);
        try
        {
            var r = await _w.Client.Login(user, pass);
            if (r.Mfa && r.Ticket is not null) { _ticket = r.Ticket; ShowMfa(); }
            else if (r.Token is not null) _w.LoggedIn();
            else SetError("Serwer nie zwrócił sesji. Spróbuj jeszcze raz.");
        }
        catch (Exception ex) { SetError(GibCordClient.Friendly(ex)); }
        finally { _busy = false; go.IsEnabled = true; }
    }

    async Task DoMfa(string code, Button go)
    {
        if (_busy || _ticket is null) return;
        _busy = true; go.IsEnabled = false; SetError(null);
        try
        {
            var r = await _w.Client.LoginMfa(_ticket, code);
            if (r.Token is not null) _w.LoggedIn();
        }
        catch (ApiException ex) when (ex.Code == "mfa_expired") { ShowPassword(ex.Message); }
        catch (Exception ex) { SetError(GibCordClient.Friendly(ex)); }
        finally { _busy = false; go.IsEnabled = true; }
    }
}
