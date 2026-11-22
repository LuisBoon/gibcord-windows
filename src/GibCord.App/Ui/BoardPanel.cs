using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace GibCord.App.Ui;

/// <summary>
/// 6.3 Tablica w rozmowie: wspólne rysowanie na żywo (pióro, zakreślacz, linia, strzałka, prostokąt, elipsa, napis), kursory innych
/// z imieniem, cofanie swoich kresek, czyszczenie, zdjęcie jako tło i zapis obrazka na kanał. Płótno 16:9, współrzędne 0–1
/// (każdy rysuje w swojej skali). Stan trzyma serwer; panel dostaje board_state przy otwarciu i potem zmiany.
/// </summary>
public sealed class BoardPanel : Grid, IDisposable
{
    public static BoardPanel? Current { get; private set; }
    /// <summary>Kto ma tablicę otwartą (kanał → osoby) — do podświetlenia przycisku w rozmowie.</summary>
    public static readonly Dictionary<long, List<long>> Viewers = [];
    public static Action<long>? StatusChanged;

    const double VW = 1600; // szerokość „wirtualna” — grubości kresek są w tej skali
    static readonly string[] Colors = ["#ffffff", "#ffb98f", "#f4927f", "#e4668f", "#c7a2ff", "#7fd6cc", "#6fe3a5", "#ffd36e", "#2a1630"];
    readonly Shell _sh;
    readonly long _cid;
    readonly Grid _area = new() { Background = U.B("#1c1533"), CornerRadius = new CornerRadius(14) };
    readonly Image _bg = new() { Stretch = Stretch.UniformToFill, Opacity = .9 };
    readonly Canvas _ink = new(), _live = new();
    readonly List<JsonElement> _strokes = [];
    readonly Dictionary<string, FrameworkElement> _drafts = [];
    readonly Dictionary<long, (FrameworkElement El, long At)> _cursors = [];
    readonly Stack<string> _mine = new();
    readonly DispatcherQueueTimer _cursorFade;
    string _tool = "pen", _color = "#ffb98f";
    double _width = 4;
    List<double>? _pts;
    string? _draftId;
    FrameworkElement? _preview;
    long _lastDraft, _lastCursor;
    string? _bgUrl;
    readonly WrapPanel _tools = new() { Spacing = 6 }; // w wąskim oknie przyciski schodzą do drugiego rzędu
    readonly TextBlock _who = U.T("", 12, U.Semi, Theme.Mu);

    public BoardPanel(Shell sh, long cid)
    {
        _sh = sh; _cid = cid;
        Current = this;
        this.Rows("auto", "*");
        Padding = U.Th(16, 12, 16, 8);
        RowSpacing = 10;
        _area.Children.Add(_bg);
        _area.Children.Add(_ink);
        _area.Children.Add(_live);
        var host = new Grid();
        host.Children.Add(_area);
        host.SizeChanged += (_, e) => Fit(e.NewSize);
        _area.PointerPressed += Down;
        _area.PointerMoved += Move;
        _area.PointerReleased += Up;
        _area.PointerCanceled += (_, _) => Cancel();
        _area.PointerCaptureLost += (_, _) => { if (_pts is not null) Up(null, null); };
        _area.SizeChanged += (_, _) => Redraw();
        BuildTools();
        this.Add(_tools.At(0), host.At(1));
        _cursorFade = DispatcherQueue.CreateTimer();
        _cursorFade.Interval = TimeSpan.FromSeconds(1);
        _cursorFade.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            foreach (var (u, c) in _cursors.ToList()) if (now - c.At > 4000) { _live.Children.Remove(c.El); _cursors.Remove(u); }
        };
        _cursorFade.Start();
        _ = sh.C.SendLive(new { t = "board_open", channelId = cid });
        Unloaded += (_, _) => { };
    }

    public void Dispose()
    {
        _cursorFade.Stop();
        _ = _sh.C.SendLive(new { t = "board_close", channelId = _cid });
        if (Current == this) Current = null;
    }

    /// <summary>Płótno 16:9 wpisane w dostępne miejsce.</summary>
    void Fit(Size s)
    {
        if (s.Width <= 0 || s.Height <= 0) return;
        var w = Math.Min(s.Width, s.Height * 16 / 9);
        _area.Width = Math.Floor(w);
        _area.Height = Math.Floor(w * 9 / 16);
        _area.HorizontalAlignment = HorizontalAlignment.Center;
        _area.VerticalAlignment = VerticalAlignment.Center;
    }

    double K => (_area.ActualWidth > 0 ? _area.ActualWidth : 800) / VW;

    // ── pasek narzędzi ──
    void BuildTools()
    {
        _tools.Children.Clear();
        _tools.VerticalAlignment = VerticalAlignment.Center;
        foreach (var (key, glyph, tip) in new[] { ("pen", "", "Pióro"), ("marker", "", "Zakreślacz"), ("line", "", "Linia"), ("arrow", "", "Strzałka"), ("rect", "", "Prostokąt"), ("ellipse", "", "Elipsa"), ("text", "", "Napis") })
        {
            var on = _tool == key;
            var b = U.IconButton(glyph, tip, () => { _tool = key; BuildTools(); }, 15, 34, on ? Theme.OnAccent : null);
            b.Background = on ? Theme.Accent : U.W(.06);
            _tools.Add(b);
        }
        _tools.Add(new Rectangle { Width = 1, Height = 22, Fill = U.W(.14), Margin = U.Th(4, 0) });
        foreach (var c in Colors)
        {
            var sw = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = U.B(c), BorderBrush = _color == c ? U.W(1) : U.W(.2), BorderThickness = U.Th(_color == c ? 3 : 1), VerticalAlignment = VerticalAlignment.Center };
            var col = c;
            sw.Tapped += (_, _) => { _color = col; BuildTools(); };
            sw.Tip(col == "#2a1630" ? "Ciemny" : "Kolor");
            _tools.Add(sw);
        }
        _tools.Add(new Rectangle { Width = 1, Height = 22, Fill = U.W(.14), Margin = U.Th(4, 0) });
        foreach (var (wv, label) in new[] { (2.5, "Cienko"), (5.0, "Średnio"), (10.0, "Grubo") })
        {
            var on = Math.Abs(_width - wv) < .1;
            var dot = new Ellipse { Width = wv + 4, Height = wv + 4, Fill = on ? Theme.OnAccent : U.W(.85) };
            var b = new Button { Content = dot, Width = 34, Height = 34, Padding = U.Th(0), CornerRadius = new CornerRadius(8), Background = on ? Theme.Accent : U.W(.06), BorderThickness = U.Th(0) };
            var v = wv;
            b.Click += (_, _) => { _width = v; BuildTools(); };
            ToolTipService.SetToolTip(b, label);
            _tools.Add(b);
        }
        _tools.Add(new Rectangle { Width = 1, Height = 22, Fill = U.W(.14), Margin = U.Th(4, 0) });
        _tools.Add(U.IconButton("", "Cofnij swoją kreskę (Ctrl+Z)", Undo, 15, 34));
        _tools.Add(U.IconButton("", "Wyczyść tablicę (u wszystkich)", async () =>
        {
            if (await M.Confirm(_sh, "Wyczyścić tablicę?", "Zniknie wszystko, co narysowaliście — u wszystkich w rozmowie.", "Wyczyść"))
                _ = _sh.C.SendLive(new { t = "board_clear", channelId = _cid });
        }, 15, 34));
        _tools.Add(U.IconButton("", "Zdjęcie jako tło", PickBg, 15, 34));
        _tools.Add(U.IconButton("", "Zapisz obrazek na kanale", () => _ = SaveToChannel(), 15, 34));
        _tools.Add(_who.With(t => { t.Margin = U.Th(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        UpdateWho();
    }

    void UpdateWho()
    {
        var v = Viewers.GetValueOrDefault(_cid) ?? [];
        var others = v.Where(u => u != _sh.S.Me.Id).Select(u => _sh.S.NameOf(u, _sh.S.ServerOfChannel(_cid))).ToList();
        _who.Text = others.Count == 0 ? "Tylko Ty patrzysz na tablicę" : "Patrzą też: " + string.Join(", ", others.Take(4)) + (others.Count > 4 ? $" i {others.Count - 4} więcej" : "");
    }

    // ── rysowanie myszą / piórem ──
    (double X, double Y) Norm(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_area).Position;
        return (Math.Round(Math.Clamp(p.X / Math.Max(1, _area.ActualWidth), 0, 1), 4), Math.Round(Math.Clamp(p.Y / Math.Max(1, _area.ActualHeight), 0, 1), 4));
    }

    async void Down(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(_area).Properties.IsRightButtonPressed) return;
        var (x, y) = Norm(e);
        if (_tool == "text")
        {
            var box = new TextBox { PlaceholderText = "Co napisać?", MaxLength = 200 };
            var d = new AppDialog { Title = "Napis na tablicy", Content = box.With(b => b.Width = 320), PrimaryButtonText = "Dodaj", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary };
            box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
            if (await d.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0) return;
            Commit(new { id = NewId(), tool = "text", color = _color, width = _width, points = new[] { x, y }, text = box.Text.Trim() });
            return;
        }
        _area.CapturePointer(e.Pointer);
        _pts = [x, y];
        _draftId = NewId();
        _preview = null;
        Preview();
        e.Handled = true;
    }

    void Move(object sender, PointerRoutedEventArgs e)
    {
        var (x, y) = Norm(e);
        var now = Environment.TickCount64;
        if (now - _lastCursor > 50) { _lastCursor = now; _ = _sh.C.SendLive(new { t = "board_cursor", channelId = _cid, x, y }); }
        if (_pts is null) return;
        if (_tool is "pen" or "marker")
        {
            var lx = _pts[^2]; var ly = _pts[^1];
            if (Math.Abs(lx - x) + Math.Abs(ly - y) < .0015) return;
            if (_pts.Count < 4000) { _pts.Add(x); _pts.Add(y); }
        }
        else { if (_pts.Count > 2) _pts.RemoveRange(2, _pts.Count - 2); _pts.Add(x); _pts.Add(y); }
        Preview();
        if (now - _lastDraft > 70) { _lastDraft = now; SendDraft(); }
    }

    void Up(object? sender, PointerRoutedEventArgs? e)
    {
        if (_pts is null) return;
        var pts = _pts; _pts = null;
        if (e is not null) _area.ReleasePointerCapture(e.Pointer);
        if (_preview is not null) { _live.Children.Remove(_preview); _preview = null; }
        if (pts.Count == 2) { pts.Add(pts[0] + .0005); pts.Add(pts[1] + .0005); } // kropka
        if (_tool is not ("pen" or "marker") && pts.Count < 4) return;
        Commit(new { id = _draftId!, tool = _tool, color = _color, width = _width, points = pts });
    }

    void Cancel()
    {
        _pts = null;
        if (_preview is not null) { _live.Children.Remove(_preview); _preview = null; }
    }

    static string NewId() => Guid.NewGuid().ToString("N")[..12];

    void SendDraft()
    {
        if (_pts is null || _draftId is null) return;
        _ = _sh.C.SendLive(new { t = "board_draft", channelId = _cid, id = _draftId, tool = _tool, color = _color, width = _width, points = _pts });
    }

    void Preview()
    {
        if (_pts is null) return;
        if (_preview is not null) _live.Children.Remove(_preview);
        var el = JsonSerializer.SerializeToElement(new { id = _draftId, tool = _tool, color = _color, width = _width, points = _pts, userId = _sh.S.Me.Id });
        _preview = Shape(el);
        if (_preview is not null) _live.Children.Add(_preview);
    }

    void Commit(object stroke)
    {
        var el = JsonSerializer.SerializeToElement(stroke);
        var id = el.Str("id")!;
        _mine.Push(id);
        var withMe = JsonSerializer.SerializeToElement(new Dictionary<string, object?>(el.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, p.Value))) { ["userId"] = _sh.S.Me.Id });
        Add(withMe);
        _ = _sh.C.SendLive(new { t = "board_stroke", channelId = _cid, stroke });
    }

    void Undo()
    {
        while (_mine.Count > 0)
        {
            var id = _mine.Pop();
            var i = _strokes.FindLastIndex(s => s.Str("id") == id);
            if (i < 0) continue;
            _strokes.RemoveAt(i);
            Redraw();
            _ = _sh.C.SendLive(new { t = "board_undo", channelId = _cid, id });
            return;
        }
    }

    // ── rysowanie kresek ──
    void Add(JsonElement s) { _strokes.Add(s); if (Shape(s) is { } el) _ink.Children.Add(el); }

    void Redraw()
    {
        _ink.Children.Clear();
        foreach (var s in _strokes) if (Shape(s) is { } el) _ink.Children.Add(el);
        foreach (var (k, el) in _drafts.ToList()) { _live.Children.Remove(el); _drafts.Remove(k); }
    }

    FrameworkElement? Shape(JsonElement s)
    {
        var pts = s.Prop("points").ValueKind == JsonValueKind.Array ? s.Prop("points").EnumerateArray().Select(x => x.GetDouble()).ToList() : [];
        if (pts.Count < 2) return null;
        var W = _area.ActualWidth > 0 ? _area.ActualWidth : 800; var H = _area.ActualHeight > 0 ? _area.ActualHeight : 450;
        Point P(int i) => new(pts[i * 2] * W, pts[i * 2 + 1] * H);
        var color = U.B(s.Str("color") is { Length: 7 } c ? c : "#ffffff");
        var w = Math.Max(1, (s.Prop("width").ValueKind == JsonValueKind.Number ? s.Prop("width").GetDouble() : 4) * K * 2);
        var tool = s.Str("tool") ?? "pen";
        var n = pts.Count / 2;
        switch (tool)
        {
            case "pen" or "marker":
                {
                    var pl = new Polyline { Stroke = color, StrokeThickness = tool == "marker" ? w * 3 : w, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = tool == "marker" ? .42 : 1, IsHitTestVisible = false };
                    for (var i = 0; i < n; i++) pl.Points.Add(P(i));
                    return pl;
                }
            case "line" or "arrow":
                {
                    var a = P(0); var b = P(n - 1);
                    var g = new Canvas { IsHitTestVisible = false };
                    g.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = color, StrokeThickness = w, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                    if (tool == "arrow")
                    {
                        var ang = Math.Atan2(b.Y - a.Y, b.X - a.X); var len = Math.Max(12, w * 4);
                        foreach (var d in new[] { 2.6, -2.6 })
                            g.Children.Add(new Line { X1 = b.X, Y1 = b.Y, X2 = b.X + len * Math.Cos(ang + d), Y2 = b.Y + len * Math.Sin(ang + d), Stroke = color, StrokeThickness = w, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                    }
                    return g;
                }
            case "rect" or "ellipse":
                {
                    var a = P(0); var b = P(n - 1);
                    Microsoft.UI.Xaml.Shapes.Shape sh = tool == "rect" ? new Rectangle { RadiusX = 4, RadiusY = 4 } : new Ellipse();
                    sh.Stroke = color; sh.StrokeThickness = w; sh.IsHitTestVisible = false;
                    sh.Width = Math.Max(1, Math.Abs(b.X - a.X)); sh.Height = Math.Max(1, Math.Abs(b.Y - a.Y));
                    Canvas.SetLeft(sh, Math.Min(a.X, b.X)); Canvas.SetTop(sh, Math.Min(a.Y, b.Y));
                    return sh;
                }
            case "text":
                {
                    var t = U.T(s.Str("text") ?? "", Math.Max(10, (s.Prop("width").ValueKind == JsonValueKind.Number ? s.Prop("width").GetDouble() : 4) * 4 * K * 2 + 12 * K * 2), U.Bold, color);
                    t.IsHitTestVisible = false;
                    var p = P(0);
                    Canvas.SetLeft(t, p.X); Canvas.SetTop(t, p.Y);
                    return t;
                }
        }
        return null;
    }

    // ── zdarzenia z serwera ──
    public static void OnEvent(Shell sh, GatewayEvent ev)
    {
        var cid = ev.Data.Long("channelId") ?? 0;
        if (ev.Type == "board_status")
        {
            Viewers[cid] = ev.Data.Prop("viewers").ValueKind == JsonValueKind.Array ? ev.Data.Prop("viewers").EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
            StatusChanged?.Invoke(cid);
            if (Current is { } b0 && b0._cid == cid) b0.UpdateWho();
            return;
        }
        if (Current is not { } b || b._cid != cid) return;
        b.Apply(ev);
    }

    void Apply(GatewayEvent ev)
    {
        var d = ev.Data;
        switch (ev.Type)
        {
            case "board_state":
                _strokes.Clear();
                if (d.Prop("strokes").ValueKind == JsonValueKind.Array) foreach (var s in d.Prop("strokes").EnumerateArray()) _strokes.Add(s.Clone());
                SetBg(d.Str("bg"));
                if (d.Prop("viewers").ValueKind == JsonValueKind.Array) Viewers[_cid] = d.Prop("viewers").EnumerateArray().Select(x => x.GetInt64()).ToList();
                Redraw(); UpdateWho();
                break;
            case "board_stroke":
                {
                    var s = d.Prop("stroke").Clone();
                    var key = (s.Long("userId") ?? 0) + ":" + s.Str("id");
                    if (_drafts.Remove(key, out var dr)) _live.Children.Remove(dr);
                    Add(s);
                    break;
                }
            case "board_draft":
                {
                    var key = (d.Long("userId") ?? 0) + ":" + d.Str("id");
                    if (_drafts.Remove(key, out var old)) _live.Children.Remove(old);
                    if (Shape(d.Clone()) is { } el) { _drafts[key] = el; _live.Children.Insert(0, el); }
                    break;
                }
            case "board_remove":
                {
                    var id = d.Str("id");
                    var i = _strokes.FindLastIndex(s => s.Str("id") == id && s.Long("userId") == d.Long("userId"));
                    if (i >= 0) { _strokes.RemoveAt(i); Redraw(); }
                    break;
                }
            case "board_clear":
                _strokes.Clear(); _mine.Clear(); SetBg(null); Redraw();
                if (d.Long("userId") is long cu && cu != _sh.S.Me.Id) _sh.Info($"{_sh.S.NameOf(cu, _sh.S.ServerOfChannel(_cid))} wyczyścił(a) tablicę");
                break;
            case "board_bg":
                SetBg(d.Str("url"));
                break;
            case "board_cursor" when d.Long("userId") is long u && u != _sh.S.Me.Id:
                {
                    var x = (d.Prop("x").ValueKind == JsonValueKind.Number ? d.Prop("x").GetDouble() : 0) * _area.ActualWidth;
                    var y = (d.Prop("y").ValueKind == JsonValueKind.Number ? d.Prop("y").GetDouble() : 0) * _area.ActualHeight;
                    if (!_cursors.TryGetValue(u, out var c))
                    {
                        var color = Store.ColorOfUser(_sh.S.U(u));
                        var el = U.H(2,
                            new Polygon { Points = { new(0, 0), new(12, 8), new(6, 8), new(3, 14) }, Fill = U.B(color) },
                            U.Box(U.T(_sh.S.NameOf(u, _sh.S.ServerOfChannel(_cid)), 11, U.Bold, U.B("#2a1630")), U.B(color), 6, U.Th(6, 1)).With(bx => bx.Margin = U.Th(0, 8, 0, 0)));
                        el.IsHitTestVisible = false;
                        _live.Children.Add(el);
                        c = (el, 0);
                    }
                    Canvas.SetLeft(c.El, x); Canvas.SetTop(c.El, y);
                    _cursors[u] = (c.El, Environment.TickCount64);
                    break;
                }
        }
    }

    void SetBg(string? url)
    {
        _bgUrl = url;
        _bg.Source = url is null ? null : new BitmapImage(_sh.C.Api.Abs(url));
    }

    async void PickBg()
    {
        if (_bgUrl is not null && await M.Confirm(_sh, "Usunąć tło?", "Tablica wróci do pustego tła u wszystkich.", "Usuń tło")) { _ = _sh.C.SendLive(new { t = "board_bg", channelId = _cid, url = (string?)null }); return; }
        var f = await FileBrowser.PickFile(_sh.XamlRoot, [".png", ".jpg", ".jpeg", ".webp", ".gif"], FileBrowser.Place.Pictures);
        if (f is null) return;
        try
        {
            await using var st = await f.OpenStreamForReadAsync();
            var (att, err) = await _sh.C.Upload(st, f.Name, f.ContentType, st.Length);
            if (att is null) { _sh.Info(err ?? "Nie udało się wysłać zdjęcia.", true); return; }
            _ = _sh.C.SendLive(new { t = "board_bg", channelId = _cid, url = att.Url });
            SetBg(att.Url);
        }
        catch (Exception ex) { _sh.Info("Nie udało się wczytać zdjęcia: " + ex.Message, true); }
    }

    /// <summary>Obrazek tablicy (PNG) na wybrany kanał albo do rozmowy.</summary>
    async Task SaveToChannel()
    {
        try
        {
            var target = await WrappedView.PickChannel(_sh, "Gdzie wysłać obrazek z tablicy?");
            if (target is null) return;
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_area);
            var px = await rtb.GetPixelsAsync();
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, px.ToArray());
            await enc.FlushAsync();
            var bytes = new byte[ms.Size];
            ms.Seek(0);
            await ms.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, Windows.Storage.Streams.InputStreamOptions.None);
            using var mem = new MemoryStream(bytes);
            var (att, err) = await _sh.C.Upload(mem, $"tablica-{DateTime.Now:yyyy-MM-dd-HHmm}.png", "image/png", bytes.Length);
            if (att is null) { _sh.Info(err ?? "Nie udało się wysłać obrazka.", true); return; }
            await _sh.C.Api.SendMessage(target.Value.Id, "", attachments: [att.Id]);
            _sh.Info($"Obrazek z tablicy wysłany: {target.Value.Label}");
        }
        catch (Exception ex) { _sh.Info("Nie udało się zapisać tablicy: " + ex.Message, true); }
    }

    /// <summary>Tryb pokazowy: kilka kresek i kursor (zrzut CI --view board).</summary>
    public void Demo(long otherUser)
    {
        var demo = new object[]
        {
            new { id = "d1", userId = otherUser, tool = "rect", color = "#7fd6cc", width = 5.0, points = new[] { .12, .18, .42, .62 } },
            new { id = "d2", userId = otherUser, tool = "text", color = "#ffffff", width = 5.0, points = new[] { .14, .08 }, text = "Baza B" },
            new { id = "d3", userId = 1L, tool = "arrow", color = "#ffb98f", width = 5.0, points = new[] { .5, .75, .78, .35 } },
            new { id = "d4", userId = 1L, tool = "pen", color = "#e4668f", width = 4.0, points = new[] { .55, .2, .6, .26, .66, .24, .7, .3, .74, .28, .8, .33 } },
            new { id = "d5", userId = otherUser, tool = "ellipse", color = "#ffd36e", width = 5.0, points = new[] { .74, .22, .9, .44 } },
            new { id = "d6", userId = 1L, tool = "marker", color = "#6fe3a5", width = 5.0, points = new[] { .16, .8, .3, .82, .42, .8 } },
        };
        foreach (var s in demo) _strokes.Add(JsonSerializer.SerializeToElement(s, Json.Options));
        Viewers[_cid] = [1, otherUser];
        Loaded += (_, _) =>
        {
            Redraw(); UpdateWho();
            Apply(new GatewayEvent("board_cursor", JsonSerializer.SerializeToElement(new { channelId = _cid, userId = otherUser, x = .44, y = .5 })));
        };
    }
}
