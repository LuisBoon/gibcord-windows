using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GibCord.App.Ui;

/// <summary>
/// Bramka e-mail (serwer wymaga potwierdzonego adresu, WS 4005): wysłanie linku ponownie, zmiana adresu,
/// samo sprawdzanie co 10 s — po potwierdzeniu w poczcie aplikacja wpuszcza dalej bez ponownego logowania.
/// </summary>
public sealed class EmailGatePage : Grid
{
    readonly MainWindow _w;
    readonly StackPanel _form = new() { Spacing = 14 };
    readonly TextBlock _msg = U.T("", 13, null, Theme.Mu, wrap: true);
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    string? _email;
    bool _done;

    public EmailGatePage(MainWindow w)
    {
        _w = w;
        var card = new Border
        {
            Width = 460, Padding = U.Th(36, 32), CornerRadius = new CornerRadius(22), Background = U.B("#B3221a3d"),
            BorderBrush = U.W(.12), BorderThickness = U.Th(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = _form,
        };
        Children.Add(new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.Tick += (_, _) => _ = Check(false);
        _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        Draw();
        _ = Check(false);
    }

    void Draw()
    {
        _form.Children.Clear();
        _form.Add(new Image { Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center });
        _form.Add(U.T("Potwierdź adres e-mail", 22, U.Bold).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; }));
        _form.Add(U.T(_email is null ? "Żeby korzystać z GibCorda, potwierdź swój adres e-mail — kliknij link, który do Ciebie wysłaliśmy."
            : $"Wysłaliśmy link na {_email}. Kliknij go w poczcie — ta strona sama to zauważy.", 13.5, null, Theme.Mu, wrap: true).With(t => t.TextAlignment = TextAlignment.Center));
        _form.Add(_msg);
        _form.Add(U.Primary("Już potwierdziłem — sprawdź", () => _ = Check(true), 44));
        _form.Add(U.Ghost("Wyślij link jeszcze raz", async () =>
        {
            try { await _w.Client.Api.ResendEmail(); Say("Wysłano. Sprawdź też folder „Spam” albo „Oferty”."); }
            catch (Exception ex) { Say(GibCordClient.Friendly(ex), true); }
        }).With(b => b.HorizontalAlignment = HorizontalAlignment.Stretch));
        var email = new TextBox { PlaceholderText = "nowy@adres.pl", Height = 40 };
        var pass = new PasswordBox { PlaceholderText = "Twoje hasło (do potwierdzenia zmiany)", Height = 40 };
        var change = new Expander
        {
            Header = U.T("Zły adres? Zmień go", 13, U.Semi), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = U.V(8, email, pass, U.Ghost("Zmień adres i wyślij link", async () =>
            {
                try
                {
                    await _w.Client.Api.PatchMe(new { email = email.Text.Trim(), password = pass.Password });
                    _email = email.Text.Trim();
                    Draw();
                    Say("Adres zmieniony — wysłaliśmy nowy link.");
                }
                catch (Exception ex) { Say(GibCordClient.Friendly(ex), true); }
            })),
        };
        _form.Add(change);
        _form.Add(U.Ghost("Wyloguj się", async () => { _timer.Stop(); await _w.Client.Logout(); _w.ShowLogin(null); }).With(b => b.HorizontalAlignment = HorizontalAlignment.Center));
    }

    void Say(string text, bool error = false)
    {
        _msg.Text = text;
        _msg.Foreground = error ? Theme.Red : Theme.Green;
    }

    async Task Check(bool manual)
    {
        if (_done) return;
        try
        {
            var (me, gate) = await _w.Client.Api.GetMe();
            if (_done) return; // dwuklik albo zbieg z timerem — drugi raz nie wchodzimy
            if (_email != me.Email && !string.IsNullOrEmpty(me.Email)) { _email = me.Email; Draw(); }
            if (!gate || me.EmailVerified)
            {
                _done = true;
                _timer.Stop();
                _w.LoggedIn();
                return;
            }
            if (manual) Say("Jeszcze nie widzimy potwierdzenia. Kliknij link w mailu i spróbuj za chwilę.", true);
        }
        catch (ApiException ex) when (ex.IsAuth) { _timer.Stop(); _w.ShowLogin("Sesja wygasła — zaloguj się ponownie."); }
        catch (Exception ex) { if (manual) Say(GibCordClient.Friendly(ex), true); }
    }
}
