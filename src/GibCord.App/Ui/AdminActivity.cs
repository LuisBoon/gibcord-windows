using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace GibCord.App.Ui;

/// <summary>
/// Panel admina → Aktywność (jak na stronie, wariant B+C z 5.10.2): oś doby — kto był w GibCordzie o jakiej godzinie
/// (na ekranie / w tle) — i karta osoby: 14 dni, ostatnie wejścia, zalogowane urządzenia.
/// </summary>
public sealed class AdminActivity : StackPanel
{
    readonly Shell _sh;
    readonly WrapPanel _days = new() { Spacing = 4 }; // dni zawijają się do nowej linii (w poziomym pasku ostatnie były ucięte)
    readonly TextBlock _sum = U.T("", 12.5, null, Theme.Mu, wrap: true);
    readonly StackPanel _rows = new() { Spacing = 6 };
    string? _day;
    int _ver;

    static readonly System.Globalization.CultureInfo Pl = new("pl-PL");
    static readonly Brush OnBrush = U.B("#ffaa78"), BgBrush = U.B("#66ffaa78"), ReleaseBrush = U.B("#7cc7ff"), UpdateBrush = U.B("#6fe3a5"), NowBrush = U.W(.6);

    Store S => _sh.S;

    public AdminActivity(Shell sh)
    {
        _sh = sh;
        Spacing = 12;
        var legend = new WrapPanel { Spacing = 16 }.Add(
            U.H(6, new Rectangle { Width = 14, Height = 10, RadiusX = 3, RadiusY = 3, Fill = OnBrush }, U.T("na ekranie", 12, null, Theme.Mu)),
            U.H(6, new Rectangle { Width = 14, Height = 6, RadiusX = 3, RadiusY = 3, Fill = BgBrush }, U.T("w tle (zasobnik, inna karta)", 12, null, Theme.Mu)),
            U.H(6, U.Box(U.T("6.0.x", 9.5, U.Bold, U.B("#0b1a2a")), ReleaseBrush, 4, U.Th(4, 0)), U.T("wydanie aplikacji", 12, null, Theme.Mu)),
            U.H(6, new Ellipse { Width = 10, Height = 10, Fill = UpdateBrush, Stroke = U.B("#1b1531"), StrokeThickness = 1.5 }, U.T("pobrał(a) aktualizację", 12, null, Theme.Mu)),
            U.H(6, new Rectangle { Width = 2, Height = 12, Fill = NowBrush }, U.T("teraz", 12, null, Theme.Mu)));
        Children.Add(_days);
        Children.Add(_sum);
        Children.Add(legend);
        Children.Add(_rows);
        _rows.Children.Add(U.T("Wczytywanie…", 13, null, Theme.Mu));
        Loaded += (_, _) => _ = Load();
    }

    static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static string Dur(double min)
    {
        var m = (long)Math.Round(min);
        return m >= 60 ? $"{m / 60} h {m % 60} min" : $"{m} min";
    }
    static string Time(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("HH:mm");
    static string DayName(DateTime d) => d.Date == DateTime.Today ? "dziś" : d.Date == DateTime.Today.AddDays(-1) ? "wczoraj" : d.ToString("ddd d MMM", Pl);

    void AddUser(JsonElement u) { if (u.To<User>() is { } x && !S.Users.ContainsKey(x.Id)) S.Users[x.Id] = x; }

    async Task Load()
    {
        var ver = ++_ver;
        try
        {
            var d = Options.Demo ? DemoDay() : await _sh.C.Api.GetJson("admin/activity" + (_day is null ? "" : "?date=" + _day));
            if (ver != _ver) return;
            _day = d.Str("date");
            // dni do wyboru
            _days.Children.Clear();
            var i = 0;
            foreach (var x in d.Prop("days").EnumerateArray())
            {
                var key = x.GetString() ?? "";
                var on = key == _day;
                var label = i == 0 ? "Dziś" : i == 1 ? "Wczoraj" : DateTime.TryParse(key, out var dt) ? dt.ToString("ddd d", Pl) : key;
                var b = new Button { Content = U.T(label, 12, U.Semi, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(8), Padding = U.Th(10, 4) };
                b.Click += (_, _) => { _day = key; _ = Load(); };
                _days.Children.Add(b);
                i++;
            }
            var users = d.Prop("users").ValueKind == JsonValueKind.Array ? d.Prop("users").EnumerateArray().ToList() : [];
            var since = Num(d, "since");
            var dayStart = Num(d, "dayStart") ?? 0;
            var now = Num(d, "now") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var releases = d.Prop("releases").ValueKind == JsonValueKind.Array ? d.Prop("releases").EnumerateArray().ToList() : [];
            _sum.Text = $"Aktywnych tego dnia: {users.Count} z {Num(d, "total") ?? 0:0} kont." +
                (releases.Count > 0 ? $" {releases.Count} {Format.Plural(releases.Count, "wydanie", "wydania", "wydań")} aplikacji (flagi nad wykresem)." : "") +
                (since is { } s && s > dayStart ? $" Historia zbiera się od {Time(s)} tego dnia." : since is null ? " Historia zapisuje się od wersji 5.10.2." : "");
            _rows.Children.Clear();
            if (users.Count == 0) { _rows.Children.Add(U.T("Tego dnia nikt nie był w GibCordzie (albo historia jeszcze się nie zbierała).", 13, null, Theme.Mu, wrap: true)); return; }
            const double DAY = 86_400_000;
            const double TrackH = 30;
            var nowIn = now < dayStart + DAY;
            // ── górny pas: flagi wydań (raz dla całego wykresu, piętrowo gdy blisko siebie) i godziny co 3 h ──
            var lane = new Canvas { Height = releases.Count > 0 ? 44 : 0 };
            var axisC = new Canvas { Height = 18 };
            void RenderHead()
            {
                lane.Children.Clear(); axisC.Children.Clear();
                var w = axisC.ActualWidth;
                if (w <= 0) return;
                double Pos(double t) => Math.Clamp((t - dayStart) / DAY, 0, 1) * w;
                var step = w / 8 >= 38 ? 3 : w / 4 >= 38 ? 6 : 12;
                for (var h = 0; h <= 24; h += step)
                {
                    var lab = U.T($"{h}:00", 10.5, null, Theme.Mu2);
                    lab.Measure(new Windows.Foundation.Size(100, 20));
                    Canvas.SetLeft(lab, Math.Clamp(w * h / 24 - lab.DesiredSize.Width / 2, 0, w - lab.DesiredSize.Width));
                    axisC.Children.Add(lab);
                }
                // wydania blisko siebie (mniej niż ~48 px) łączą się w jedną flagę „6.0.5 – 6.0.10”, lista w dymku
                var groups = new List<List<(string V, double At, double X)>>();
                foreach (var rl in releases.Select(rl => (V: rl.Str("version") ?? "", At: Num(rl, "at") ?? 0)).OrderBy(r => r.At))
                {
                    var x = Pos(rl.At);
                    if (groups.Count > 0 && x - groups[^1][^1].X < 48) groups[^1].Add((rl.V, rl.At, x));
                    else groups.Add([(rl.V, rl.At, x)]);
                }
                double[] levelEnd = [-1e9, -1e9];
                foreach (var gr in groups)
                {
                    var cx = (gr[0].X + gr[^1].X) / 2;
                    var label = gr.Count == 1 ? gr[0].V : $"{gr[0].V} – {gr[^1].V}";
                    var pill = U.Box(U.T(label, 10, U.Bold, U.B("#0b1a2a")), ReleaseBrush, 5, U.Th(5, 1));
                    pill.Measure(new Windows.Foundation.Size(300, 40));
                    if (pill.DesiredSize.Width > Math.Max(60, gr[^1].X - gr[0].X + 70))
                    {
                        pill = U.Box(U.T($"{gr.Count} wydań", 10, U.Bold, U.B("#0b1a2a")), ReleaseBrush, 5, U.Th(5, 1));
                        pill.Measure(new Windows.Foundation.Size(300, 40));
                    }
                    var pw = pill.DesiredSize.Width;
                    var left = Math.Clamp(cx - pw / 2, 0, Math.Max(0, w - pw));
                    var level = left > levelEnd[0] + 4 ? 0 : left > levelEnd[1] + 4 ? 1 : (levelEnd[0] <= levelEnd[1] ? 0 : 1);
                    levelEnd[level] = left + pw;
                    var top = level == 0 ? 22 : 2;
                    Canvas.SetLeft(pill, left); Canvas.SetTop(pill, top);
                    ToolTipService.SetToolTip(pill, string.Join("\n", gr.Select(r => $"Wydanie GibCorda {r.V} — {Time(r.At)}")));
                    foreach (var r in gr)
                    {
                        var stem = new Rectangle { Width = 1.5, Height = Math.Max(2, 44 - top - 16), Fill = ReleaseBrush, Opacity = .6 };
                        Canvas.SetLeft(stem, r.X - .75); Canvas.SetTop(stem, top + 16);
                        lane.Children.Add(stem);
                    }
                    lane.Children.Add(pill);
                }
                if (nowIn)
                {
                    var t = U.T("teraz", 10, U.Bold, NowBrush);
                    t.Measure(new Windows.Foundation.Size(100, 20));
                    Canvas.SetLeft(t, Math.Clamp(Pos(now) - t.DesiredSize.Width / 2, 0, w - t.DesiredSize.Width));
                    Canvas.SetTop(t, 0);
                    axisC.Children.Add(t);
                    foreach (var c in axisC.Children.OfType<TextBlock>().Where(c => c != t && Math.Abs(Canvas.GetLeft(c) - Canvas.GetLeft(t)) < 30)) c.Opacity = 0; // „teraz” zamiast godziny obok
                }
            }
            axisC.SizeChanged += (_, _) => RenderHead();
            // wąsko (mało miejsca na pasek doby): imię i czas nad paskiem, pasek na całą szerokość
            var wide = ActualWidth <= 0 || ActualWidth >= 760;
            _rows.Children.Add(wide ? new Grid { ColumnSpacing = 10 }.Cols("228", "*", "76").Add(U.V(2, lane, axisC).At(0, 1)) : U.V(2, lane, axisC));

            foreach (var x in users)
            {
                var uj = x.Prop("user");
                AddUser(uj);
                var uid = uj.Long("id") ?? 0;
                var onMin = Num(x, "onMin") ?? 0;
                var who = U.HG(8, new Avatar(30).Set(S, _sh.C.Api, uid, null),
                    U.V(0, U.T(S.NameOf(uid), 13, U.Bold), U.T(x.Str("client") ?? "—", 10.5, null, Theme.Mu2)));
                var whoBtn = U.Row(who, () => _ = Card(uid), false, 8, U.Th(6, 3), U.Th(0)).With(r => { if (wide) r.Width = 228; });
                ToolTipService.SetToolTip(whoBtn, "Karta osoby" + (x.Str("client") is { } cl ? " · " + cl : ""));
                var time = U.T(onMin > 0 ? Dur(onMin) : "w tle", 12, U.Bold, onMin > 0 ? Theme.Fg : Theme.Mu2).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; });
                // pasek doby
                var track = new Grid { Height = TrackH, Background = U.W(.04), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
                var canvas = new Canvas();
                track.Children.Add(canvas);
                var segs = x.Prop("segments").ValueKind == JsonValueKind.Array ? x.Prop("segments").EnumerateArray().ToList() : [];
                var updates = x.Prop("updates").ValueKind == JsonValueKind.Array ? x.Prop("updates").EnumerateArray().ToList() : [];
                void Render()
                {
                    canvas.Children.Clear();
                    var w = track.ActualWidth;
                    if (w <= 0) return;
                    double Pos(double t) => Math.Clamp((t - dayStart) / DAY, 0, 1) * w;
                    // delikatna siatka co 3 h i blade ślady wydań (szczegóły — flagi nad wykresem)
                    for (var h = 3; h < 24; h += 3)
                    {
                        var gl = new Rectangle { Width = 1, Height = TrackH, Fill = U.W(.06) };
                        Canvas.SetLeft(gl, w * h / 24);
                        canvas.Children.Add(gl);
                    }
                    foreach (var rl in releases)
                    {
                        var rlx = new Rectangle { Width = 1, Height = TrackH, Fill = ReleaseBrush, Opacity = .22 };
                        Canvas.SetLeft(rlx, Pos(Num(rl, "at") ?? 0));
                        canvas.Children.Add(rlx);
                    }
                    foreach (var g in segs.OrderBy(g => g.Str("kind") == "on" ? 1 : 0)) // tło pod spodem, ekran na wierzchu
                    {
                        var a = Pos(Num(g, "start") ?? 0);
                        var b = Pos(Num(g, "end") ?? 0);
                        var on = g.Str("kind") == "on";
                        var r = new Rectangle { Width = Math.Max(3, b - a), Height = on ? 18 : 8, RadiusX = on ? 5 : 4, RadiusY = on ? 5 : 4, Fill = on ? OnBrush : BgBrush };
                        Canvas.SetLeft(r, a);
                        Canvas.SetTop(r, on ? 6 : 11);
                        var devs = g.Prop("devices").ValueKind == JsonValueKind.Array ? string.Join(", ", g.Prop("devices").EnumerateArray().Select(v => v.GetString())) : "";
                        ToolTipService.SetToolTip(r, $"{S.NameOf(uid)}: {Time(Math.Max(Num(g, "start") ?? 0, dayStart))} – {(g.Prop("live").ValueKind == JsonValueKind.True ? "teraz" : Time(Math.Min(Num(g, "end") ?? 0, dayStart + DAY - 1)))} · {(on ? "na ekranie" : "w tle")}{(devs.Length > 0 ? " · " + devs : "")}");
                        canvas.Children.Add(r);
                    }
                    if (nowIn)
                    {
                        var l = new Rectangle { Width = 2, Height = TrackH, Fill = NowBrush };
                        Canvas.SetLeft(l, Pos(now) - 1);
                        canvas.Children.Add(l);
                    }
                    foreach (var up in updates)
                    {
                        var at = Num(up, "at") ?? 0;
                        var ux = Pos(at);
                        var dot = new Ellipse { Width = 14, Height = 14, Fill = UpdateBrush, Stroke = U.B("#1b1531"), StrokeThickness = 2.5 };
                        Canvas.SetLeft(dot, ux - 7);
                        Canvas.SetTop(dot, 8);
                        var rel = releases.FirstOrDefault(rr => rr.Str("version") == up.Str("version"));
                        var after = Num(rel, "at") is double ra && at >= ra ? $" (po {Dur((at - ra) / 60_000)} od wydania)" : "";
                        ToolTipService.SetToolTip(dot, $"{S.NameOf(uid)} pobrał(a) {up.Str("version")} o {Time(at)}{after} · {up.Str("label")}");
                        canvas.Children.Add(dot);
                        var vl = U.Box(U.T(up.Str("version") ?? "", 9.5, U.Bold, U.B("#0e2a1c")), UpdateBrush, 4, U.Th(4, 0));
                        vl.Measure(new Windows.Foundation.Size(100, 30));
                        Canvas.SetLeft(vl, ux + 9 + vl.DesiredSize.Width > w ? ux - 9 - vl.DesiredSize.Width : ux + 9);
                        Canvas.SetTop(vl, 7);
                        vl.IsHitTestVisible = false;
                        canvas.Children.Add(vl);
                    }
                }
                track.SizeChanged += (_, _) => Render();
                _rows.Children.Add(wide
                    ? new Grid { ColumnSpacing = 10 }.Cols("228", "*", "76").Add(whoBtn.At(0, 0), track.At(0, 1), time.At(0, 2))
                    : U.V(4, new Grid { ColumnSpacing = 10 }.Cols("*", "auto").Add(whoBtn.At(0, 0), time.At(0, 1)), track).With(v => v.Margin = U.Th(0, 4, 0, 4)));
            }
        }
        catch (Exception ex)
        {
            if (ver != _ver) return;
            _rows.Children.Clear();
            _rows.Children.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true));
        }
    }

    /// <summary>Zrzut w CI: dzień z kilkoma osobami, wydaniami i aktualizacjami.</summary>
    static JsonElement DemoDay()
    {
        var day = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        long H(double h) => day + (long)(h * 3_600_000);
        object Seg(double a, double b, string kind) => new { start = H(a), end = H(b), kind, live = false, devices = new[] { "Windows · GibCord 6.0.10" } };
        object U(long id, string name, string client, double on, object[] segs, object[] ups) => new { user = new { id, username = name.ToLowerInvariant(), displayName = name, color = "#ffb98f" }, onMin = on, last = H(17), segments = segs, updates = ups, client };
        var data = new
        {
            date = DateTime.Today.ToString("yyyy-MM-dd"), days = new[] { DateTime.Today.ToString("yyyy-MM-dd") }, dayStart = day, now = H(17.2), total = 10,
            releases = new[] { "6.0.1", "6.0.2", "6.0.3", "6.0.4", "6.0.5", "6.0.6", "6.0.7", "6.0.8", "6.0.9", "6.0.10" }.Select((v, i) => new { version = v, at = H(new[] { .8, 1.15, 2.57, 2.8, 12.28, 13.27, 13.62, 14.32, 14.88, 15.77 }[i]) }).ToArray(),
            users = new[]
            {
                U(1, "luisboon", "Windows · GibCord 6.0.10", 249, [Seg(0, .8, "on"), Seg(9.6, 9.7, "on"), Seg(10.3, 10.9, "on"), Seg(10.9, 11.8, "bg"), Seg(12.1, 13.6, "on"), Seg(14.2, 16.4, "on"), Seg(16.4, 17.2, "on")], [new { version = "6.0.10", label = "Windows · GibCord 6.0.10", at = H(16.9) }]),
                U(2, "Ola", "Windows · aplikacja 1.0.2", 4, [Seg(12.6, 16.1, "bg"), Seg(13.0, 13.1, "on")], []),
                U(3, "Kuba", "iPhone · Safari", 0, [Seg(.1, .25, "bg"), Seg(9.3, 9.35, "bg")], []),
                U(4, "bonkus", "Windows · GibCord 6.0.9", 35, [Seg(.0, .3, "bg"), Seg(2.3, 5, "bg"), Seg(15.9, 16.5, "on")], [new { version = "6.0.9", label = "Windows · GibCord 6.0.9", at = H(15.95) }]),
            },
        };
        return System.Text.Json.JsonSerializer.SerializeToElement(data, GibCord.Core.Json.Options);
    }

    /// <summary>Karta osoby: 14 dni na ekranie, ostatnie wejścia, zalogowane urządzenia.</summary>
    async Task Card(long uid)
    {
        var body = new StackPanel { Spacing = 12, Width = 560 };
        body.Children.Add(U.T("Wczytywanie…", 13, null, Theme.Mu));
        var d = new AppDialog { Title = "Karta osoby", Content = new ScrollViewer { Content = body, MaxHeight = 560 }, CloseButtonText = "Zamknij", XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark };
        d.Resources["ContentDialogMaxWidth"] = 680.0;
        _ = Fill();
        await M.WaitNoDialog(XamlRoot);
        await d.ShowAsync();

        async Task Fill()
        {
            try
            {
                var r = await _sh.C.Api.GetJson($"admin/users/{uid}/activity");
                var u = r.Prop("user");
                AddUser(u);
                body.Children.Clear();
                var created = Num(u, "createdAt") is { } c ? DateTimeOffset.FromUnixTimeMilliseconds((long)c).ToLocalTime().ToString("d MMMM yyyy", Pl) : "?";
                body.Children.Add(U.H(12, new Avatar(48).Set(S, _sh.C.Api, uid, null),
                    U.V(2, U.T(S.NameOf(uid), 17, U.Bold), U.T($"@{u.Str("username")} · konto od {created} · {Num(u, "messages") ?? 0:0} wiad.", 12, null, Theme.Mu),
                        U.T("Mail: " + (u.Str("email") ?? "brak") + (u.Prop("emailVerified").ValueKind == JsonValueKind.True ? " ✓" : ""), 12, null, Theme.Mu))));
                // 14 dni na ekranie
                var days = r.Prop("days").ValueKind == JsonValueKind.Array ? r.Prop("days").EnumerateArray().Reverse().ToList() : [];
                var max = Math.Max(30, days.Count > 0 ? days.Max(x => Num(x, "onMin") ?? 0) : 30);
                var sum = days.Sum(x => Num(x, "onMin") ?? 0);
                // średnia jak na stronie: tylko po dniach od początku historii
                var histSince = Num(r, "since");
                var counted = histSince is { } hs ? days.Count(x => DateTime.TryParse(x.Str("date"), out var dd) && new DateTimeOffset(dd.Date.AddDays(1).AddSeconds(-1)).ToUnixTimeMilliseconds() >= hs) : 0;
                var bars = new Grid { Height = 90, ColumnSpacing = 4 };
                for (var i = 0; i < days.Count; i++)
                {
                    bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var on = Num(days[i], "onMin") ?? 0;
                    var bg = Num(days[i], "bgMin") ?? 0;
                    var dateStr = days[i].Str("date") ?? "";
                    var bar = new Border
                    {
                        Height = on > 0 ? Math.Max(4, 86 * on / max) : 2, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3, 3, 0, 0),
                        Background = on > 0 ? (i == days.Count - 1 ? Theme.Accent : OnBrush) : U.W(.12),
                    };
                    ToolTipService.SetToolTip(bar, (DateTime.TryParse(dateStr, out var dt) ? DayName(dt) : dateStr) + ": " + (on > 0 ? Dur(on) + " na ekranie" : "nie było") + (bg > 0 ? $" · {Dur(bg)} w tle" : ""));
                    bars.Add(bar.At(0, i));
                }
                var labs = new Grid { ColumnSpacing = 4 };
                for (var i = 0; i < days.Count; i++)
                {
                    labs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var ds = days[i].Str("date") ?? "";
                    labs.Add(U.T(ds.Length >= 10 ? ds[8..].TrimStart('0') : "", 10, null, Theme.Mu2).With(t => t.HorizontalAlignment = HorizontalAlignment.Center).At(0, i));
                }
                body.Children.Add(Section("Na ekranie w ostatnich 14 dniach", U.V(4, bars, labs,
                    U.T(sum > 0 ? $"Razem {Dur(sum)} · średnio {Dur(sum / Math.Max(1, counted > 0 ? counted : 14))} dziennie." : "Jeszcze nie było na ekranie od początku historii.", 12, null, Theme.Mu))));
                // ostatnie wejścia
                var visits = new StackPanel { Spacing = 4 };
                foreach (var v in r.Prop("visits").ValueKind == JsonValueKind.Array ? r.Prop("visits").EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    var st = Num(v, "start") ?? 0;
                    var en = Num(v, "end") ?? st;
                    var live = v.Prop("live").ValueKind == JsonValueKind.True;
                    var onMin = Num(v, "onMin") ?? 0;
                    var devs = v.Prop("devices").ValueKind == JsonValueKind.Array ? string.Join(", ", v.Prop("devices").EnumerateArray().Select(x => x.GetString())) : "";
                    var day = DayName(DateTimeOffset.FromUnixTimeMilliseconds((long)st).LocalDateTime);
                    visits.Children.Add(new Grid().Cols("*", "auto").Add(
                        U.V(0, U.T($"{day} {Time(st)} – {(live ? "teraz" : Time(en))}", 12.5, U.Semi), U.T((onMin > 0 ? $"na ekranie {Dur(onMin)}" : "tylko w tle") + (devs.Length > 0 ? " · " + devs : ""), 11, null, Theme.Mu2)).At(0, 0),
                        U.T(Dur((en - st) / 60_000), 12, U.Bold, live ? Theme.Green : Theme.Mu).At(0, 1)));
                }
                if (visits.Children.Count == 0) visits.Children.Add(U.T("Jeszcze nic.", 12, null, Theme.Mu));
                body.Children.Add(Section("Ostatnie wejścia", visits));
                // urządzenia
                var devices = new StackPanel { Spacing = 4 };
                foreach (var x in r.Prop("devices").ValueKind == JsonValueKind.Array ? r.Prop("devices").EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    var online = x.Prop("online").ValueKind == JsonValueKind.True;
                    var since = Num(x, "createdAt") is { } c2 ? DateTimeOffset.FromUnixTimeMilliseconds((long)c2).ToLocalTime().ToString("d MMM HH:mm", Pl) : "";
                    var seen = Num(x, "lastSeen") is { } ls ? Format.When((long)ls) : "";
                    devices.Children.Add(new Grid().Cols("*", "auto").Add(
                        U.V(0, U.T(x.Str("label") ?? "urządzenie", 12.5, U.Semi), U.T($"Zalogowany {since}" + (x.Str("ip") is { } ip ? " · IP " + ip : ""), 11, null, Theme.Mu2)).At(0, 0),
                        U.T(online ? "połączone teraz" : seen, 11.5, U.Semi, online ? Theme.Green : Theme.Mu).At(0, 1)));
                }
                if (devices.Children.Count == 0) devices.Children.Add(U.T("Nie jest nigdzie zalogowany.", 12, null, Theme.Mu));
                body.Children.Add(Section("Zalogowane urządzenia", devices));
                // czego używa: programy i wersje (od kiedy, kiedy ostatnio) + aktualizacje na tle wydań
                var apps = new StackPanel { Spacing = 4 };
                foreach (var cx in r.Prop("clients").ValueKind == JsonValueKind.Array ? r.Prop("clients").EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    var first = Num(cx, "firstAt") is { } fa ? DateTimeOffset.FromUnixTimeMilliseconds((long)fa).ToLocalTime().ToString("d MMM HH:mm", Pl) : "";
                    apps.Children.Add(new Grid().Cols("*", "auto").Add(
                        U.V(0, U.T(cx.Str("label") ?? "?", 12.5, U.Semi), U.T("pierwszy raz " + first, 11, null, Theme.Mu2)).At(0, 0),
                        U.T(Num(cx, "lastAt") is { } la ? Format.When((long)la) : "", 11.5, U.Semi, Theme.Mu).At(0, 1)));
                }
                var rels = r.Prop("releases").ValueKind == JsonValueKind.Array ? r.Prop("releases").EnumerateArray().ToList() : [];
                foreach (var up in r.Prop("updates").ValueKind == JsonValueKind.Array ? r.Prop("updates").EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    var at = Num(up, "at") ?? 0;
                    var rel = rels.FirstOrDefault(rr => rr.Str("version") == up.Str("version"));
                    var after = Num(rel, "at") is double ra && at >= ra ? $" · {Dur((at - ra) / 60_000)} po wydaniu" : "";
                    apps.Children.Add(U.HG(8, new Ellipse { Width = 9, Height = 9, Fill = UpdateBrush },
                        U.T($"Pobrał(a) {up.Str("version")} — {DayName(DateTimeOffset.FromUnixTimeMilliseconds((long)at).LocalDateTime)} {Time(at)}{after}", 11.5, null, Theme.Mu)));
                }
                if (apps.Children.Count == 0) apps.Children.Add(U.T("Jeszcze brak danych — zapisują się od wersji 6.0.10.", 12, null, Theme.Mu));
                body.Children.Add(Section("Czego używa (aplikacja i wersja)", apps));
            }
            catch (Exception ex) { body.Children.Clear(); body.Children.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
        }
    }

    static FrameworkElement Section(string title, UIElement content) =>
        U.V(6, U.T(title.ToUpperInvariant(), 10.5, U.Bold, Theme.Mu2), content);
}
