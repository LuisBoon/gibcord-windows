using System.Runtime.InteropServices.WindowsRuntime;
using GibCord.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace GibCord.App.Ui;

/// <summary>
/// „Obraz w obrazie”: oglądana transmisja w małym oknie zawsze na wierzchu (np. nad grą albo przeglądarką).
/// Okno można przesuwać za górny pasek i zmieniać jego rozmiar; zamyka się samo, gdy transmisja się skończy.
/// </summary>
public sealed class PipWindow : Window
{
    static PipWindow? _open;

    readonly Shell _sh;
    readonly long _uid;
    readonly Image _img = new() { Stretch = Stretch.Uniform };
    readonly DispatcherQueueTimer _timer;
    WriteableBitmap? _bmp;
    long _seen, _missingSince;
    bool _closed;

    public static bool IsOpenFor(long uid) => _open is { } o && o._uid == uid;
    public static bool IsOpen => _open is not null;

    /// <summary>Otwiera okienko dla transmisji tej osoby (drugie kliknięcie zamyka).</summary>
    public static void Toggle(Shell sh, long uid)
    {
        if (_open is { } o) { var same = o._uid == uid; o.Close(); if (same) return; }
        if (!sh.C.Voice.IsWatching(uid)) sh.C.Voice.Watch(uid);
        _open = new PipWindow(sh, uid);
        _open.Activate();
    }

    public static void CloseAll() { if (_open is { } o) { o._quiet = true; o.Close(); } }
    bool _quiet; // zamknięte razem z oknem głównym — nie odświeżamy już odpiętego widoku

    PipWindow(Shell sh, long uid)
    {
        _sh = sh;
        _uid = uid;
        var srv = sh.S.ServerOfChannel(sh.C.VoiceChannelId);
        var name = sh.S.NameOf(uid, srv);
        Title = $"GibCord — {name} nadaje";
        ExtendsContentIntoTitleBar = true;
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.ico")); } catch { }
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = Theme.FgC;
        tb.ButtonHoverBackgroundColor = U.C("#1Affffff");
        var p = OverlappedPresenter.Create();
        p.IsAlwaysOnTop = true;
        p.IsMinimizable = false;
        p.IsMaximizable = false;
        p.PreferredMinimumWidth = 260;
        p.PreferredMinimumHeight = 170;
        AppWindow.SetPresenter(p);
        // prawy dolny róg ekranu, 16:9
        var area = DisplayArea.GetFromWindowId(sh.W.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = Math.Max(1.0, sh.XamlRoot?.RasterizationScale ?? 1.0);
        int w = (int)(480 * scale), h = (int)(302 * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - (int)(24 * scale), area.Y + area.Height - h - (int)(24 * scale), w, h));

        var drag = new Grid { Height = 32, Background = U.B("#cc120c22") }.Cols("auto", "*");
        drag.Add(U.H(8, new Avatar(20).Set(sh.S, sh.C.Api, uid, srv), U.T($"{name} nadaje ekran", 12.5, U.Semi)).With(x => { x.Margin = U.Th(10, 0, 0, 0); x.VerticalAlignment = VerticalAlignment.Center; }).At(0, 0));
        var back = U.IconButton("\uE72B", "Wróć do GibCorda", () => { _sh.W.BringToFront(); }, 13, 28);
        var stop = U.IconButton("\uE711", "Przestań oglądać", () => { _sh.C.Voice.Unwatch(_uid); _sh.RefreshVoice(); Close(); }, 13, 28);
        var tools = new Border
        {
            Background = U.B("#99120c22"), CornerRadius = new CornerRadius(8), Padding = U.Th(4, 2), Margin = U.Th(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0,
            Child = U.H(2, back, stop),
        };
        var stage = new Grid { Background = U.B("#ff08040f") };
        stage.Add(_img);
        stage.Add(tools);
        stage.PointerEntered += (_, _) => tools.Opacity = 1;
        stage.PointerExited += (_, _) => tools.Opacity = 0;
        stage.DoubleTapped += (_, _) => _sh.W.BringToFront();
        var root = new Grid { RequestedTheme = ElementTheme.Dark }.Rows("32", "*");
        root.Add(drag.At(0), stage.At(1));
        Content = root;
        SetTitleBar(drag);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => { try { Tick(); } catch (Exception ex) { App.Log("Okienko transmisji: " + ex.Message); Close(); } };
        _timer.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            if (ReferenceEquals(_open, this)) _open = null;
            if (!_quiet) _sh.RefreshVoice();
        };
    }

    void Tick()
    {
        if (_closed) return;
        var c = _sh.C;
        if (c.VoiceChannelId == 0) { Close(); return; }
        var feed = c.Voice.Feeds.FirstOrDefault(f => f.Screen && f.UserId == _uid);
        var now = Environment.TickCount64;
        if (feed is null)
        {
            // transmisja się skończyła albo przestaliśmy oglądać — chwila zapasu na ponowne połączenie
            if (_missingSince == 0) _missingSince = now;
            else if (now - _missingSince > 6000) Close();
            return;
        }
        _missingSince = 0;
        var b = _bmp;
        var (mw, mh) = U.PixelSize(_img);
        feed.TryCopy(ref _seen, (px, w, h) =>
        {
            if (w <= 0 || h <= 0) return;
            if (b is null || b.PixelWidth != w || b.PixelHeight != h) { b = new WriteableBitmap(w, h); _img.Source = b; }
            using (var st = b.PixelBuffer.AsStream()) st.Write(px[..Math.Min(px.Length, w * h * 4)]);
            b.Invalidate();
        }, mw, mh);
        _bmp = b;
    }
}
