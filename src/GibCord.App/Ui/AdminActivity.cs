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
    readonly StackPanel _days = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly TextBlock _sum = U.T("", 12.5, null, Theme.Mu, wrap: true);
    readonly StackPanel _rows = new() { Spacing = 6 };
    string? _day;
    int _ver;

    static readonly System.Globalization.CultureInfo Pl = new("pl-PL");
    static readonly Brush OnBrush = U.B("#ffaa78"), BgBrush = U.B("#66ffaa78");

    Store S => _sh.S;

    public AdminActivity(Shell sh)
    {
        _sh = sh;
        Spacing = 12;
        var legend = U.H(16,
            U.H(6, new Rectangle { Width = 14, Height = 8, RadiusX = 3, RadiusY = 3, Fill = OnBrush }, U.T("na ekranie", 12, null, Theme.Mu)),
            U.H(6, new Rectangle { Width = 14, Height = 8, RadiusX = 3, RadiusY = 3, Fill = BgBrush }, U.T("w tle (np. zasobnik, inna karta)", 12, null, Theme.Mu)));
        var axis = new Grid { Margin = U.Th(236, 0, 0, 0) }.Cols("*", "*", "*", "*", "auto");
        var labels = new[] { "0:00", "6:00", "12:00", "18:00", "24:00" };
        for (var i = 0; i < labels.Length; i++) axis.Add(U.T(labels[i], 10.5, null, Theme.Mu2).At(0, i));
        Children.Add(U.Scroll2(_days));
        Children.Add(_sum);
        Children.Add(legend);
        Children.Add(axis);
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
            var d = await _sh.C.Api.GetJson("admin/activity" + (_day is null ? "" : "?date=" + _day));
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
            _sum.Text = $"Aktywnych tego dnia: {users.Count} z {Num(d, "total") ?? 0:0} kont." +
                (since is { } s && s > dayStart ? $" Historia zbiera się od {Time(s)} tego dnia." : since is null ? " Historia zapisuje się od wersji 5.10.2." : "");
            _rows.Children.Clear();
            if (users.Count == 0) { _rows.Children.Add(U.T("Tego dnia nikt nie był w GibCordzie (albo historia jeszcze się nie zbierała).", 13, null, Theme.Mu, wrap: true)); return; }
            const double DAY = 86_400_000;
            foreach (var x in users)
            {
                var uj = x.Prop("user");
                AddUser(uj);
                var uid = uj.Long("id") ?? 0;
                var onMin = Num(x, "onMin") ?? 0;
                var who = U.H(8, new Avatar(28).Set(S, _sh.C.Api, uid, null),
                    U.V(0, U.T(S.NameOf(uid), 13, U.Bold), U.T((uj.Str("email") ?? "brak maila") + " · " + (onMin > 0 ? Dur(onMin) + " na ekranie" : "tylko w tle"), 10.5, null, Theme.Mu2)));
                var whoBtn = U.Row(who, () => _ = Card(uid), false, 8, U.Th(6, 3), U.Th(0)).With(r => r.Width = 228);
                ToolTipService.SetToolTip(whoBtn, "Karta osoby");
                // pasek doby
                var track = new Grid { Height = 22, Background = U.W(.05), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
                var canvas = new Canvas();
                track.Children.Add(canvas);
                var segs = x.Prop("segments").ValueKind == JsonValueKind.Array ? x.Prop("segments").EnumerateArray().ToList() : [];
                void Render()
                {
                    canvas.Children.Clear();
                    var w = track.ActualWidth;
                    if (w <= 0) return;
                    double Pos(double t) => Math.Clamp((t - dayStart) / DAY, 0, 1) * w;
                    foreach (var g in segs)
                    {
                        var a = Pos(Num(g, "start") ?? 0);
                        var b = Pos(Num(g, "end") ?? 0);
                        var on = g.Str("kind") == "on";
                        var r = new Rectangle { Width = Math.Max(2, b - a), Height = on ? 16 : 10, RadiusX = 3, RadiusY = 3, Fill = on ? OnBrush : BgBrush };
                        Canvas.SetLeft(r, a);
                        Canvas.SetTop(r, on ? 3 : 6);
                        var devs = g.Prop("devices").ValueKind == JsonValueKind.Array ? string.Join(", ", g.Prop("devices").EnumerateArray().Select(v => v.GetString())) : "";
                        ToolTipService.SetToolTip(r, $"{S.NameOf(uid)}: {Time(Math.Max(Num(g, "start") ?? 0, dayStart))} – {(g.Prop("live").ValueKind == JsonValueKind.True ? "teraz" : Time(Math.Min(Num(g, "end") ?? 0, dayStart + DAY - 1)))} · {(on ? "na ekranie" : "w tle")}{(devs.Length > 0 ? " · " + devs : "")}");
                        canvas.Children.Add(r);
                    }
                    if (now < dayStart + DAY)
                    {
                        var l = new Rectangle { Width = 2, Height = 22, Fill = Theme.Green };
                        Canvas.SetLeft(l, Pos(now));
                        canvas.Children.Add(l);
                    }
                }
                track.SizeChanged += (_, _) => Render();
                _rows.Children.Add(new Grid { ColumnSpacing = 8 }.Cols("228", "*").Add(whoBtn.At(0, 0), track.At(0, 1)));
            }
        }
        catch (Exception ex)
        {
            if (ver != _ver) return;
            _rows.Children.Clear();
            _rows.Children.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true));
        }
    }

    /// <summary>Karta osoby: 14 dni na ekranie, ostatnie wejścia, zalogowane urządzenia.</summary>
    async Task Card(long uid)
    {
        var body = new StackPanel { Spacing = 12, Width = 560 };
        body.Children.Add(U.T("Wczytywanie…", 13, null, Theme.Mu));
        var d = new ContentDialog { Title = "Karta osoby", Content = new ScrollViewer { Content = body, MaxHeight = 560 }, CloseButtonText = "Zamknij", XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark };
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
                    U.T(sum > 0 ? $"Razem {Dur(sum)} · średnio {Dur(sum / Math.Max(1, days.Count))} dziennie." : "Jeszcze nie było na ekranie od początku historii.", 12, null, Theme.Mu))));
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
            }
            catch (Exception ex) { body.Children.Clear(); body.Children.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
        }
    }

    static FrameworkElement Section(string title, UIElement content) =>
        U.V(6, U.T(title.ToUpperInvariant(), 10.5, U.Bold, Theme.Mu2), content);
}
