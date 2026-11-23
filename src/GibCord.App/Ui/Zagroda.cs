using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>
/// 6.3 Chmurna Zagroda: gra farmerska GibCorda. Sama gra to strona /zagroda/ z serwera (ta sama co w przeglądarce i na telefonie),
/// tu w WebView2 w panelu obok czatu (wybór „obok czatu”), z przełącznikiem „na całe okno”. Klucz sesji trafia do strony
/// skryptem przed jej startem (window.__gcToken) — tylko dla adresów naszego serwera; inne adresy otwierają się w przeglądarce.
/// Zdarzenia zg_* z bramki (np. ktoś pomógł przy sterowcu) idą do gry jako wiadomość „zg_refresh”.
/// </summary>
public sealed class ZagrodaPanel : Grid
{
    readonly Shell _sh;
    readonly WebView2 _web = new();
    readonly Button _fullBtn;
    bool _started;
    public bool Full { get; private set; } = true; // wybór 1: osobne miejsce — na start całe okno

    public ZagrodaPanel(Shell sh)
    {
        _sh = sh;
        Background = Theme.Members;
        this.Rows("auto", "*");
        _fullBtn = U.IconButton("\uE73F", "Czat obok gry", () => sh.ZagrodaFull(!Full), 14, 30);
        var head = new Grid { Padding = U.Th(12, 6, 6, 6), ColumnSpacing = 4 }.Cols("auto", "*", "auto", "auto");
        head.Add(U.T("", 15, null, Theme.Accent).With(t => t.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")).At(0, 0),
            U.T("Chmurna Zagroda", 14, U.Bold).With(t => t.Margin = U.Th(8, 0, 0, 0)).At(0, 1),
            _fullBtn.At(0, 2),
            U.IconButton("", "Zamknij grę", () => sh.CloseZagroda(), 14, 30).At(0, 3));
        this.Add(head.At(0, 0), _web.At(1, 0));
        _web.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0x58, 0xb0, 0xef);
    }

    public void SetFull(bool on)
    {
        Full = on;
        if (_fullBtn.Content is TextBlock t) t.Text = on ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(_fullBtn, on ? "Czat obok gry" : "Gra na całe okno");
    }

    static void Open(Uri u) => _ = Launcher.LaunchUriAsync(u);
    Uri Base => _sh.C.Api.BaseUri;
    bool Ours(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Base.Scheme && string.Equals(u.Host, Base.Host, StringComparison.OrdinalIgnoreCase) && u.Port == Base.Port;

    public async void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(App.DataDir, "webview"),
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" });
            await _web.EnsureCoreWebView2Async(env);
            if (_web.CoreWebView2 is not { } core) return;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            var token = JsonSerializer.Serialize(_sh.C.Api.Token ?? "");
            var origin = JsonSerializer.Serialize(Base.GetLeftPart(UriPartial.Authority));
            // klucz tylko dla strony z naszego serwera (sprawdzane w samej stronie, zanim ruszy gra)
            await core.AddScriptToExecuteOnDocumentCreatedAsync($"if (location.origin === {origin}) window.__gcToken = {token};");
            core.NewWindowRequested += (s, e) => { e.Handled = true; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme is "https" or "http") Open(u); };
            core.NavigationStarting += (s, e) =>
            {
                if (Ours(e.Uri) && new Uri(e.Uri).AbsolutePath.StartsWith("/zagroda/", StringComparison.Ordinal)) return;
                e.Cancel = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && !Ours(e.Uri)) Open(u);
            };
            core.Navigate(new Uri(Base, "zagroda/?panel=1&app=1").AbsoluteUri);
        }
        catch (Exception ex)
        {
            App.Log("Zagroda: " + ex);
            _started = false;
            _sh.Info("Gra nie działa — brakuje składnika Microsoft Edge WebView2 w Windowsie.", true);
        }
    }

    /// <summary>Zdarzenie z serwera dla gry (zg_*): gra pobierze świeży stan.</summary>
    public void Notify(string type)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "zg_refresh", ev = type })); } catch { }
    }
}
