using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GibCord.App.Ui;

/// <summary>Pasek tytułu: Gibek, wstecz/dalej, szukajka (Ctrl+K). Przyciski okna rysuje Windows po prawej.</summary>
public sealed class TitleBarView : Grid
{
    readonly MainWindow _w;
    readonly Button _back, _fwd;
    readonly Border _search;
    readonly TextBlock _name;
    public Grid DragArea => this;

    public TitleBarView(MainWindow w)
    {
        _w = w;
        Background = Theme.TitleBar;
        this.Cols("auto", "auto", "*", "auto", "*", "140");
        var logo = new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = 18, Height = 18 };
        _name = U.T("GibCord", 12.5, U.Bold);
        var beta = U.Box(U.T("6.0 beta", 10, U.Bold, Theme.OnAccent), Theme.Accent, 5, U.Th(5, 1));
        beta.VerticalAlignment = VerticalAlignment.Center;
        this.Add(U.H(8, logo, _name, beta).With(p => p.Margin = U.Th(12, 0, 0, 0)).At(0, 0));

        _back = U.IconButton("", "Wstecz (Alt+←)", () => _w.CurrentShell?.Back(), 12, 28);
        _fwd = U.IconButton("", "Dalej (Alt+→)", () => _w.CurrentShell?.Forward(), 12, 28);
        this.Add(U.H(2, _back, _fwd).With(p => p.Margin = U.Th(24, 0, 0, 0)).At(0, 1));

        var hint = U.Box(U.T("Ctrl K", 10.5, null, Theme.Mu), U.W(.12), 5, U.Th(5, 1));
        var inner = new Grid().Cols("auto", "*", "auto").Add(
            U.I("", 12, Theme.Mu).At(0, 0),
            U.T("Szukaj albo przejdź do…", 12, null, Theme.Mu).With(t => t.Margin = U.Th(8, 0, 0, 0)).At(0, 1),
            hint.At(0, 2));
        _search = new Border { Child = inner, Width = 380, Height = 24, CornerRadius = new CornerRadius(8), Background = Theme.Input, Padding = U.Th(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        _search.PointerEntered += (_, _) => _search.Background = U.W(.12);
        _search.PointerExited += (_, _) => _search.Background = Theme.Input;
        _search.Tapped += (_, _) => _w.CurrentShell?.OpenSwitcher();
        _search.Tip("Szybkie przejście do kanału, rozmowy albo osoby (Ctrl+K)");
        this.Add(_search.At(0, 3));
    }

    public IEnumerable<FrameworkElement> Interactive() => [_back, _fwd, _search];

    public void SetShellMode(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        _back.Visibility = _fwd.Visibility = _search.Visibility = v;
        _w.UpdateTitleBarRegions();
    }

    public void SetNav(bool canBack, bool canFwd)
    {
        _back.IsEnabled = canBack;
        _fwd.IsEnabled = canFwd;
    }

    public void SetActive(bool active) => _name.Opacity = active ? 1 : .6;
}
