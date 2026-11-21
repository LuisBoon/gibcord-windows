using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>
/// Własne menu ikonki przy zegarze (zamiast szarego menu Windows): małe okienko bez ramki nad kursorem, w barwach GibCorda.
/// Zamyka się po kliknięciu obok, Esc albo po wyborze.
/// </summary>
static class TrayMenu
{
    static Window? _open;
    /// <summary>Zrzut w CI: zawartość ostatnio otwartego menu.</summary>
    public static FrameworkElement? DemoRoot { get; private set; }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Show(MainWindow w, int cursorX, int cursorY)
    {
        try { _open?.Close(); } catch { }
        var c = w.Client;
        var inVoice = c is not null && c.VoiceChannelId != 0;
        var muted = c is not null && (c.Voice.Options.Muted || c.Voice.Options.Deafened);

        Window? win = null;
        void Run(Action a) { try { win?.Close(); } catch { } a(); }

        var items = new StackPanel { Spacing = 2 };
        Border Item(string glyph, string text, Action a, bool danger = false)
        {
            var row = U.Row(U.H(12, U.I(glyph, 15, danger ? Theme.Red : Theme.Accent), U.T(text, 13.5, U.Semi, danger ? Theme.Red : Theme.Fg)), () => Run(a), false, 9, U.Th(12, 9), U.Th(0));
            row.IsTabStop = true;
            row.UseSystemFocusVisuals = true;
            row.KeyDown += (_, e) => { if (e.Key is VirtualKey.Enter or VirtualKey.Space) { e.Handled = true; Run(a); } };
            return row;
        }
        var unread = Tray.UnreadText;
        var head = new Grid { ColumnSpacing = 10, Margin = U.Th(8, 4, 8, 8) }.Cols("auto", "*").Add(
            new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = 26, Height = 26 }.At(0, 0),
            U.V(0, U.T("GibCord", 14, U.Bold), U.T(unread ?? (inVoice ? "Na kanale głosowym" : "Wszystko przeczytane"), 11.5, null, Theme.Mu)).At(0, 1));
        items.Add(head);
        items.Add(Item("", "Otwórz GibCord", w.BringToFront));
        if (inVoice)
        {
            items.Add(Item(muted ? "" : "", muted ? "Włącz mikrofon" : "Wycisz mikrofon", () => c!.ToggleMute()));
            items.Add(Item("", "Rozłącz z głosem", () => _ = c!.LeaveVoice(), danger: true));
        }
        items.Add(Item("", "Ustawienia", () => { w.BringToFront(); w.CurrentShell?.ShowSettings(); }));
        items.Add(U.HLine(.08).With(l => l.Margin = U.Th(6, 4)));
        items.Add(Item("", "Zamknij GibCord", w.Quit));

        var root = new Grid { Background = Theme.Surface, Padding = U.Th(6), BorderBrush = U.W(.12), BorderThickness = U.Th(1), RequestedTheme = ElementTheme.Dark };
        root.Children.Add(items);
        root.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; Run(() => { }); } };

        win = new Window { Content = root, Title = "GibCord" };
        DemoRoot = root;
        _open = win;
        var aw = win.AppWindow;
        aw.IsShownInSwitchers = false;
        if (aw.Presenter is OverlappedPresenter op)
        {
            op.IsResizable = false; op.IsMaximizable = false; op.IsMinimizable = false; op.IsAlwaysOnTop = true;
            op.SetBorderAndTitleBar(false, false);
        }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
        try { var round = 2; DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); } catch { }

        // rozmiar w pikselach ekranu, na którym jest kursor; menu nad kursorem (pasek zadań zwykle na dole), w granicach obszaru roboczego
        var area = DisplayArea.GetFromPoint(new PointInt32(cursorX, cursorY), DisplayAreaFallback.Nearest).WorkArea;
        aw.Move(new PointInt32(area.X + 10, area.Y + 10));
        var scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;
        root.Measure(new Windows.Foundation.Size(260, double.PositiveInfinity));
        var wPx = (int)Math.Ceiling(260 * scale);
        var hPx = (int)Math.Ceiling((root.DesiredSize.Height > 0 ? root.DesiredSize.Height : 280) * scale);
        var x = Math.Clamp(cursorX - wPx + 12, area.X + 4, area.X + area.Width - wPx - 4);
        var y = cursorY - hPx - 8 < area.Y ? Math.Min(cursorY + 8, area.Y + area.Height - hPx - 4) : cursorY - hPx - 8;
        aw.MoveAndResize(new RectInt32(x, y, wPx, hPx));

        win.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) { try { win.Close(); } catch { } }
        };
        win.Closed += (_, _) => { if (ReferenceEquals(_open, win)) _open = null; };
        root.Loaded += (_, _) =>
        {
            // po pierwszym ułożeniu znamy prawdziwą wysokość — dopasuj okno
            var real = (int)Math.Ceiling(root.ActualHeight * root.XamlRoot.RasterizationScale);
            if (real > 0 && Math.Abs(real - hPx) > 2)
            {
                var ny = y < cursorY ? cursorY - real - 8 : y;
                aw.MoveAndResize(new RectInt32(x, Math.Max(area.Y + 4, ny), wPx, real));
            }
            if (items.Children.OfType<Border>().FirstOrDefault() is { } first) first.Focus(FocusState.Programmatic);
        };
        win.Activate();
        Motion.FadeIn(items, 120);
    }
}
