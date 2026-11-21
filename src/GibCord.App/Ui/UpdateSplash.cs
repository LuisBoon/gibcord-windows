using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GibCord.App.Ui;

/// <summary>
/// Małe okienko przy starcie (jak w Discordzie): Gibek, „Sprawdzanie aktualizacji…” i pasek postępu. Bez paska tytułu,
/// stały rozmiar, na środku ekranu. Główne okno pokazuje się dopiero po nim (albo aplikacja uruchamia się od nowa w nowej wersji).
/// </summary>
public sealed class UpdateSplash : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    readonly TextBlock _text;
    readonly ProgressBar _bar;
    bool _closing;

    /// <summary>Treść okienka (też do zrzutu w CI w głównym oknie).</summary>
    public static (FrameworkElement Root, TextBlock Text, ProgressBar Bar) Build(double gibek)
    {
        var text = U.T("Sprawdzanie aktualizacji…", 13.5, U.Semi, Theme.Mu);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new ProgressBar { IsIndeterminate = true, Width = 200, Foreground = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Center };
        var box = U.V(16, Splash.Gibek(gibek).With(g => g.HorizontalAlignment = HorizontalAlignment.Center), text, bar);
        box.HorizontalAlignment = HorizontalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        var root = new Grid();
        root.Children.Add(Theme.Backdrop());
        root.Children.Add(box);
        return (root, text, bar);
    }

    public UpdateSplash()
    {
        Title = "GibCord";
        var (root, text, bar) = Build(96);
        _text = text; _bar = bar;
        Content = root;
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.ico")); } catch { }
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            op.IsResizable = false; op.IsMaximizable = false; op.IsMinimizable = false;
            op.SetBorderAndTitleBar(true, false);
        }
        try
        {
            var scale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
            int w = (int)(300 * scale), h = (int)(340 * scale);
            var area = DisplayArea.Primary.WorkArea;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        }
        catch { }
        // Alt+F4 w trakcie sprawdzania niczego nie psuje — okienko zamyka się samo, gdy skończy
        AppWindow.Closing += (_, e) => { if (!_closing) e.Cancel = true; };
    }

    public void Set(string msg, int? pct)
    {
        _text.Text = msg;
        if (pct is int p) { _bar.IsIndeterminate = false; _bar.Value = p; } else _bar.IsIndeterminate = true;
    }

    public void Finish() { _closing = true; try { Close(); } catch { } }
}
