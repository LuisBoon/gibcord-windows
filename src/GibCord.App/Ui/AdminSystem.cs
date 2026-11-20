using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace GibCord.App.Ui;

/// <summary>
/// Panel admina → System: stan maszyny teraz (kafelki) i wykresy z /api/admin/metrics (jak na stronie),
/// zakres godzina / doba / tydzień / miesiąc, odświeżane co 15 s, dopóki panel jest widoczny.
/// </summary>
public sealed class AdminSystem : StackPanel
{
    readonly Shell _sh;
    readonly StackPanel _ranges = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly TextBlock _upd = U.T("", 11.5, null, Theme.Mu2);
    readonly WrapPanel _tiles = new() { Spacing = 10 };
    readonly StackPanel _charts = new() { Spacing = 14 };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    string _range = "24h";
    JsonElement _data;
    bool _loading;

    static string Pct(double? v) => v is null ? "—" : Math.Round(v.Value) + "%";
    static string GB(double? b) => b is null ? "—" : (b.Value / 1073741824).ToString(b.Value >= 10737418240 ? "0" : "0.0", Pl) + " GB";
    static string Rate(double? bps) => bps is null ? "—" : bps >= 1e6 ? (bps.Value / 1e6).ToString("0.0", Pl) + " Mb/s" : Math.Round(bps.Value / 1e3) + " kb/s";
    static string Dur(double? s)
    {
        var t = (long)Math.Round(s ?? 0);
        long d = t / 86400, h = t % 86400 / 3600, m = t % 3600 / 60;
        return d > 0 ? $"{d} d {h} h" : h > 0 ? $"{h} h {m} min" : $"{m} min";
    }
    static readonly System.Globalization.CultureInfo Pl = new("pl-PL");

    static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    sealed record Series(string Name, string Color, Func<JsonElement, double?> Get);
    sealed record ChartDef(string Title, string Sub, Series[] Series, Func<double?, string> Fmt, bool Area = false, double? YMax = null, (double V, string Label)? Ref = null);

    public AdminSystem(Shell sh)
    {
        _sh = sh;
        Spacing = 14;
        foreach (var (k, n) in new[] { ("1h", "Godzina"), ("24h", "Doba"), ("7d", "Tydzień"), ("30d", "Miesiąc") })
        {
            var key = k;
            var b = new Button { Content = U.T(n, 12.5, U.Semi), Tag = key, BorderThickness = U.Th(0), CornerRadius = new CornerRadius(8), Padding = U.Th(12, 5) };
            b.Click += (_, _) => { _range = key; MarkRange(); _ = Load(); };
            _ranges.Children.Add(b);
        }
        MarkRange();
        Children.Add(U.H(14, _ranges, _upd));
        Children.Add(_tiles);
        Children.Add(_charts);
        _tiles.Children.Add(U.T("Wczytywanie…", 13, null, Theme.Mu));
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += (_, _) => _ = Load(quiet: true);
        Loaded += (_, _) => { _timer.Start(); _ = Load(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    void MarkRange()
    {
        foreach (var b in _ranges.Children.OfType<Button>())
        {
            var on = (string)b.Tag == _range;
            b.Background = on ? Theme.Accent : U.W(.08);
            if (b.Content is TextBlock t) t.Foreground = on ? Theme.OnAccent : Theme.Fg;
        }
    }

    int _ver;

    async Task Load(bool quiet = false)
    {
        if (quiet && _loading) return; // odświeżenie z timera, gdy poprzednie jeszcze trwa — pomijamy
        var ver = ++_ver;
        var range = _range;
        _loading = true;
        try
        {
            var data = Options.Demo ? DemoData() : await _sh.C.Api.GetJson($"admin/metrics?range={range}");
            if (ver != _ver || range != _range) return; // w międzyczasie wybrano inny zakres
            _data = data;
            Draw();
        }
        catch (Exception ex)
        {
            if (!quiet && ver == _ver) { _tiles.Children.Clear(); _tiles.Children.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
        }
        finally { if (ver == _ver) _loading = false; }
    }

    void Draw()
    {
        var n = _data.Prop("now");
        _upd.Text = "odświeżone " + DateTime.Now.ToString("HH:mm") + " · co 15 s";
        var memTotal = Num(n, "memTotal");
        var memPct = memTotal > 0 ? Num(n, "memUsed") / memTotal * 100 : null;
        var diskTotal = Num(n, "diskTotal");
        var diskPct = diskTotal > 0 ? Num(n, "diskUsed") / diskTotal * 100 : null;
        var cores = (int)(Num(n, "cores") ?? 0);
        var load = n.Prop("load").ValueKind == JsonValueKind.Array ? string.Join(" / ", n.Prop("load").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble().ToString("0.##", Pl) : "—")) : "—";
        var coturn = n.Str("coturn");
        _tiles.Children.Clear();
        _tiles.Children.Add(Tile(Pct(Num(n, "cpu")), "procesor", $"{cores} {Format.Plural(cores, "rdzeń", "rdzenie", "rdzeni")} · obciążenie {load}", Num(n, "cpu") > 85));
        _tiles.Children.Add(Tile(GB(Num(n, "memUsed")), "pamięci RAM zajęte", $"z {GB(memTotal)} · {Pct(memPct)}" + (Num(n, "swapUsed") is > 0 and var sw ? " · swap " + GB(sw) : ""), memPct > 90));
        _tiles.Children.Add(Tile(GB(Num(n, "diskUsed")), "dysku zajęte", $"z {GB(diskTotal)} · {Pct(diskPct)}", diskPct > 90));
        _tiles.Children.Add(Tile("↓ " + Rate(Num(n, "rx")), "sieć", "↑ " + Rate(Num(n, "tx"))));
        _tiles.Children.Add(Tile((Num(n, "online") ?? 0).ToString("0"), "osób online", $"{Num(n, "ws") ?? 0:0} połączeń · {Num(n, "voice") ?? 0:0} w rozmowach" + (Num(n, "streams") is > 0 and var st ? $" · {st:0} nadaje" : "")));
        _tiles.Children.Add(Tile(Num(n, "rss") is { } rss ? Math.Round(rss / 1048576) + " MB" : "—", "proces GibCord", $"procesor {Pct(Num(n, "procCpu"))} · działa {Dur(Num(n, "procUptimeSec"))}"));
        _tiles.Children.Add(Tile(Dur(Num(n, "machineUptimeSec")), "maszyna działa", $"Node {n.Str("node")} · TURN: " + (coturn == "active" ? "działa" : coturn == "inactive" ? "zatrzymany" : "brak"), coturn == "inactive"));

        var pts = _data.Prop("points").ValueKind == JsonValueKind.Array ? _data.Prop("points").EnumerateArray().ToList() : [];
        var defs = new ChartDef[]
        {
            new("Procesor", "zajętość wszystkich rdzeni", [new("Procesor", "#ffaa78", p => Num(p, "cpu"))], Pct, Area: true, YMax: 100),
            new("Pamięć RAM", "zajęta pamięć", [new("RAM", "#ffaa78", p => Num(p, "memUsed"))], GB, Area: true, YMax: memTotal, Ref: memTotal is { } mt ? (mt, "całość " + GB(mt)) : null),
            new("Obciążenie systemu", "średnia z 1 minuty; kreska = liczba rdzeni", [new("Obciążenie", "#ffaa78", p => Num(p, "load1"))], v => v is null ? "—" : v.Value.ToString("0.##", Pl),
                Ref: cores > 0 ? (cores, $"{cores} {Format.Plural(cores, "rdzeń", "rdzenie", "rdzeni")}") : null),
            new("Sieć", "ruch maszyny", [new("Pobieranie", "#ffaa78", p => Num(p, "rx")), new("Wysyłanie", "#6fe3a5", p => Num(p, "tx"))], Rate),
            new("Osoby", "na GibCordzie i w rozmowach głosowych", [new("Online", "#ffaa78", p => Num(p, "online")), new("W rozmowach", "#6fe3a5", p => Num(p, "voice"))], v => v is null ? "—" : Math.Round(v.Value).ToString()),
            new("Wiadomości", "wysłane w danym przedziale", [new("Wiadomości", "#ffaa78", p => Num(p, "msgs"))], v => v is null ? "—" : Math.Round(v.Value).ToString(), Area: true),
            new("Pamięć serwera GibCord", "proces dc (Node.js)", [new("Proces", "#ffaa78", p => Num(p, "rss"))], v => v is null ? "—" : Math.Round(v.Value / 1048576) + " MB", Area: true),
        };
        _charts.Children.Clear();
        var step = (Num(_data, "stepSec") ?? 60) * 1000;
        foreach (var d in defs) _charts.Children.Add(Chart(d, pts, step));
        // tryb pokazowy: przewiń do wykresów (zrzut w CI ma mało miejsca)
        if (Options.Demo)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                DependencyObject? p = this;
                while (p is not null and not ScrollViewer) p = VisualTreeHelper.GetParent(p);
                (p as ScrollViewer)?.ChangeView(null, 470, null, true);
            });
    }

    static FrameworkElement Tile(string value, string label, string sub, bool? warn = false) => new Border
    {
        Width = 210, Padding = U.Th(14, 10), CornerRadius = new CornerRadius(12),
        Background = warn == true ? U.B("#33ff6b81") : U.W(.06), BorderBrush = warn == true ? Theme.Red : Theme.Line, BorderThickness = U.Th(1),
        Child = U.V(2, U.T(value, 20, U.Bold), U.T(label, 12.5, U.Semi, Theme.Mu), U.T(sub, 11, null, Theme.Mu2, wrap: true)),
    };

    FrameworkElement Chart(ChartDef def, List<JsonElement> pts, double step)
    {
        var head = U.H(10, U.T(def.Title, 14, U.Bold), U.T(def.Sub, 11.5, null, Theme.Mu2));
        if (def.Series.Length > 1)
            foreach (var s in def.Series) head.Add(U.H(4, new Rectangle { Width = 10, Height = 3, Fill = U.B(s.Color), VerticalAlignment = VerticalAlignment.Center }, U.T(s.Name, 11.5, null, Theme.Mu)));
        var canvas = new Canvas { Height = 170 };
        var last = U.T("", 12, U.Semi, Theme.Fg);
        var box = new Border { Padding = U.Th(12, 10), CornerRadius = new CornerRadius(12), Background = U.W(.04), BorderBrush = Theme.Line, BorderThickness = U.Th(1), Child = U.V(8, head, canvas, last) };
        void Render()
        {
            canvas.Children.Clear();
            var W = canvas.ActualWidth;
            if (W < 64 + 10 + 120) return; // za wąsko na osie i podpisy
            const double H = 170, L = 64, R = 10, T = 8, B = 22;
            var vals = def.Series.SelectMany(s => pts.Select(s.Get)).Where(v => v is not null && !double.IsNaN(v.Value)).Select(v => v!.Value).ToList();
            var max = def.YMax ?? NiceMax(Math.Max(vals.Count > 0 ? vals.Max() : 1, def.Ref?.V ?? 0) * 1.08);
            if (max <= 0) max = 1;
            double t0 = pts.Count > 0 ? Num(pts[0], "t") ?? 0 : 0, t1 = pts.Count > 0 ? Num(pts[^1], "t") ?? 1 : 1;
            if (t1 <= t0) t1 = t0 + 1;
            double X(double t) => L + (t - t0) / (t1 - t0) * (W - L - R);
            double Y(double v) => T + (1 - Math.Clamp(v / max, 0, 1.05)) * (H - T - B);
            // siatka i podpisy osi Y
            foreach (var f in new[] { 0, .5, 1 })
            {
                var y = Y(max * f);
                canvas.Children.Add(new Line { X1 = L, X2 = W - R, Y1 = y, Y2 = y, Stroke = U.W(.08), StrokeThickness = 1 });
                var lab = U.T(def.Fmt(max * f), 10.5, null, Theme.Mu2);
                Canvas.SetLeft(lab, 0); Canvas.SetTop(lab, y - 8);
                canvas.Children.Add(lab);
            }
            // podpisy osi X (początek, środek, koniec)
            if (pts.Count > 1)
                foreach (var f in new[] { 0, .5, 1 })
                {
                    var t = t0 + (t1 - t0) * f;
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)t).ToLocalTime();
                    var lab = U.T(_range is "1h" or "24h" ? dt.ToString("HH:mm") : dt.ToString("ddd d", Pl) + (_range == "7d" ? dt.ToString(" HH:mm") : ""), 10.5, null, Theme.Mu2);
                    lab.Measure(new Size(200, 30));
                    Canvas.SetLeft(lab, Math.Clamp(X(t) - lab.DesiredSize.Width * f, L, Math.Max(L, W - R - lab.DesiredSize.Width)));
                    Canvas.SetTop(lab, H - B + 4);
                    canvas.Children.Add(lab);
                }
            // linia odniesienia (np. całość RAM, liczba rdzeni)
            if (def.Ref is { } rf)
            {
                var y = Y(rf.V);
                canvas.Children.Add(new Line { X1 = L, X2 = W - R, Y1 = y, Y2 = y, Stroke = U.W(.35), StrokeThickness = 1, StrokeDashArray = [4, 3] });
                var lab = U.T(rf.Label, 10.5, null, Theme.Mu);
                lab.Measure(new Size(200, 30));
                Canvas.SetLeft(lab, W - R - lab.DesiredSize.Width); Canvas.SetTop(lab, y - 16);
                canvas.Children.Add(lab);
            }
            // serie: przerwa w danych (brak kubełków) = przerwa w linii
            foreach (var s in def.Series)
            {
                var seg = new PointCollection();
                double? prev = null;
                void Flush()
                {
                    if (seg.Count == 0) return;
                    if (def.Area && def.Series.Length == 1 && seg.Count > 1)
                    {
                        var poly = new PointCollection();
                        foreach (var p in seg) poly.Add(p);
                        poly.Add(new Point(seg[^1].X, Y(0)));
                        poly.Add(new Point(seg[0].X, Y(0)));
                        canvas.Children.Add(new Polygon { Points = poly, Fill = new SolidColorBrush(U.C(s.Color)) { Opacity = .16 } });
                    }
                    canvas.Children.Add(seg.Count == 1
                        ? new Ellipse { Width = 4, Height = 4, Fill = U.B(s.Color), Margin = new Thickness(seg[0].X - 2, seg[0].Y - 2, 0, 0) }
                        : new Polyline { Points = seg, Stroke = U.B(s.Color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
                    seg = new PointCollection();
                }
                foreach (var p in pts)
                {
                    var v = s.Get(p);
                    var t = Num(p, "t") ?? 0;
                    if (v is null || double.IsNaN(v.Value) || (prev is not null && t - prev > step * 2.5)) Flush();
                    if (v is null || double.IsNaN(v.Value)) { prev = null; continue; }
                    seg.Add(new Point(X(t), Y(v.Value)));
                    prev = t;
                }
                Flush();
            }
        }
        canvas.SizeChanged += (_, _) => Render();
        canvas.Loaded += (_, _) => Render();
        // ostatnia wartość pod wykresem
        last.Text = string.Join("   ", def.Series.Select(s => s.Name + ": " + def.Fmt(pts.Select(s.Get).LastOrDefault(v => v is not null))));
        return box;
    }

    /// <summary>Dane pokazowe (tryb --demo, zrzuty w CI): doba w kubełkach 5-minutowych.</summary>
    static JsonElement DemoData()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var step = 300_000L;
        var first = now / step * step - 287 * step;
        var pts = new List<object>();
        for (var i = 0; i < 288; i++)
        {
            if (i is > 150 and < 156) continue; // przerwa (np. restart) — linia ma się urwać
            var h = (i / 12.0) % 24;
            var day = Math.Max(0, Math.Sin((h - 7) / 24 * 2 * Math.PI));
            pts.Add(new
            {
                t = first + i * step, cpu = 6 + 30 * day + (i % 7), load1 = 0.2 + 1.4 * day, memUsed = 2.1e9 + 0.8e9 * day, memTotal = 4.0e9,
                rx = 2e5 + 3.5e6 * day, tx = 1e5 + 2.4e6 * day, rss = 1.5e8 + 2e7 * day, online = Math.Round(2 + 9 * day), voice = Math.Round(4 * day), msgs = Math.Round(40 * day),
            });
        }
        var data = new
        {
            range = "24h", stepSec = 300, points = pts,
            now = new { cpu = 23.4, load1 = 0.84, memUsed = 2.6e9, memTotal = 4.0e9, swapUsed = 0, diskUsed = 2.9e10, diskTotal = 3.9e10, rx = 1.8e6, tx = 9.5e5, rss = 1.62e8,
                procCpu = 3.1, ws = 14, online = 9, voice = 3, streams = 1, cores = 2, load = new[] { 0.84, 0.66, 0.51 }, machineUptimeSec = 1_210_000, procUptimeSec = 86_000, node = "v22.23.5", coturn = "active" },
        };
        return JsonSerializer.SerializeToElement(data);
    }

    static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        var p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var n = v / p;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * p;
    }
}
