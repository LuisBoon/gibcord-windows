using System.Text.RegularExpressions;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>
/// Linki do GibCorda w czacie: /kanal/S/C(/M | /watek/T), /serwer/S, /dm/D otwierają się w aplikacji,
/// a /i/KOD pokazuje kartę zaproszenia z „Dołącz” (jak na stronie).
/// </summary>
static partial class Links
{
    static readonly HashSet<string> Hosts = ["gibcord.luisboon.pl", "dc.luisboon.pl"];
    static readonly Dictionary<string, (long At, InviteInfo? Info)> Cache = [];

    public static bool IsOwn(Uri u) => (u.Scheme is "https" or "http") && (Hosts.Contains(u.Host) || App.Window?.Client?.Api.BaseUri.Host == u.Host);

    [GeneratedRegex(@"https?://[^\s<>]+")]
    private static partial Regex UrlRe();
    [GeneratedRegex(@"^/i/([A-Za-z0-9]{4,32})/?$")]
    private static partial Regex InviteRe();

    /// <summary>Kody zaproszeń z treści (najwyżej 2, jak na stronie).</summary>
    public static List<string> InviteCodes(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text) || !text.Contains("/i/")) return list;
        foreach (Match m in UrlRe().Matches(text))
        {
            if (!Uri.TryCreate(m.Value.TrimEnd('.', ',', ':', ';', '"', '\'', ')', ']', '!', '?'), UriKind.Absolute, out var u) || !IsOwn(u)) continue;
            var k = InviteRe().Match(u.AbsolutePath);
            if (k.Success && !list.Contains(k.Groups[1].Value)) list.Add(k.Groups[1].Value);
            if (list.Count == 2) break;
        }
        return list;
    }

    public static async void Open(Shell sh, Uri u)
    {
        var p = u.AbsolutePath.Trim('/').Split('/');
        long N(int i) => i < p.Length && long.TryParse(p[i], out var v) ? v : 0;
        switch (p.FirstOrDefault())
        {
            case "kanal" when N(1) > 0 && N(2) > 0:
                if (sh.S.ChannelById(N(2)) is null) { sh.Info("Nie masz dostępu do tego kanału albo już go nie ma.", true); return; }
                if (p.Length >= 5 && p[3] == "watek" && N(4) > 0) { sh.OpenThread(N(4)); return; }
                if (N(3) > 0) { sh.JumpToMessage(N(2), N(3)); return; }
                sh.Go(N(1), N(2));
                return;
            case "serwer" when N(1) > 0:
                if (sh.S.ServerById(N(1)) is null) { sh.Info("Nie jesteś na tym serwerze.", true); return; }
                sh.Go(N(1), 0);
                return;
            case "dm" when N(1) > 0:
                if (sh.S.DmById(N(1)) is null) { sh.Info("Nie ma takiej rozmowy.", true); return; }
                if (N(2) > 0) sh.JumpToMessage(N(1), N(2)); else sh.Go(0, N(1));
                return;
            case "i" when p.Length >= 2:
                await Join(sh, p[1]);
                return;
        }
        _ = Launcher.LaunchUriAsync(u);
    }

    static async Task Join(Shell sh, string code)
    {
        try
        {
            var info = await Get(sh, code);
            if (info is null) { sh.Info("To zaproszenie nie działa — wygasło albo serwera już nie ma.", true); return; }
            if (sh.S.ServerById(info.Id) is not null) { sh.Go(info.Id, 0); return; }
            var srv = await sh.C.Api.JoinInvite(code);
            await sh.C.RefreshState();
            sh.Go(srv.Id, 0);
            sh.Info($"Witaj na serwerze {srv.Name}!");
        }
        catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
    }

    static async Task<InviteInfo?> Get(Shell sh, string code)
    {
        if (Cache.TryGetValue(code, out var c) && Environment.TickCount64 - c.At < 120_000) return c.Info;
        InviteInfo? info = null;
        try { info = await sh.C.Api.GetInvite(code); } catch { }
        Cache[code] = (Environment.TickCount64, info);
        return info;
    }

    static readonly string[][] Grad = [["#1d6b74", "#6b2d7c", "#c6653f"], ["#e8708f", "#6b2d7c", "#1b1531"], ["#1d6b74", "#2a5d4a", "#1b1531"], ["#ffb98f", "#c6653f", "#4a1f45"]];

    /// <summary>Karta zaproszenia pod wiadomością: baner, ikona, nazwa, dostępni / członkowie, data założenia, opis, Dołącz / Przejdź.</summary>
    public static FrameworkElement InviteCard(Shell sh, string code)
    {
        var host = new Border { Width = 380, Height = 92, CornerRadius = new CornerRadius(14), Background = Theme.Card, BorderBrush = Theme.Line, BorderThickness = U.Th(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = U.Th(0, 4, 0, 2) };
        host.Child = new ProgressRing { IsActive = true, Width = 22, Height = 22, Foreground = Theme.Mu };
        _ = Fill(sh, code, host);
        return host;
    }

    static async Task Fill(Shell sh, string code, Border host)
    {
        var info = await Get(sh, code);
        host.Height = double.NaN;
        if (info is null)
        {
            host.Child = U.H(12, U.I(G.Close, 20, Theme.Red), U.V(2, U.T("Zaproszenie nie działa", 14, U.Bold), U.T("Wygasło albo serwer już nie istnieje.", 12, null, Theme.Mu))).With(p => p.Margin = U.Th(16, 14));
            return;
        }
        var api = sh.C.Api;
        var g = Grad[info.Id % Grad.Length];
        Brush bannerBrush = info.Banner is { } b && api.Abs(b) is { } bu
            ? new ImageBrush { ImageSource = Images.Get(bu, 760), Stretch = Stretch.UniformToFill }
            : new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = U.C(g[0]), Offset = 0 }, new GradientStop { Color = U.C(g[1]), Offset = .6 }, new GradientStop { Color = U.C(g[2]), Offset = 1 } } };
        var banner = new Border { Height = 64, Background = bannerBrush, CornerRadius = new CornerRadius(14, 14, 0, 0) };
        var icon = new Avatar(52, false, "#2a2140").SetRaw(info.Name, "#7cc7ff", api.Abs(info.Icon), null);
        icon.Margin = U.Th(14, -28, 0, 0);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var mine = sh.S.ServerById(info.Id) is not null;
        var btn = new Button
        {
            Content = U.T(mine ? "Przejdź do serwera" : "Dołącz", 13, U.Bold, mine ? Theme.Fg : U.B("#ffffff")), Background = mine ? U.W(.12) : U.B("#2fa66a"),
            BorderThickness = U.Th(0), CornerRadius = new CornerRadius(10), Height = 36, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        btn.Click += async (_, _) => { btn.IsEnabled = false; await Join(sh, code); btn.IsEnabled = true; };
        var since = info.CreatedAt > 0 ? "Zał. " + Format.Local(info.CreatedAt).ToString("MMMM yyyy", Format.Pl) : null;
        var stats = U.H(14,
            U.H(5, new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.Green }, U.T($"{info.OnlineCount} online", 12, null, Theme.Mu)),
            U.H(5, new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.Gray }, U.T($"{info.MemberCount} {Format.Plural(info.MemberCount, "członek", "członków", "członków")}", 12, null, Theme.Mu)));
        var body = U.V(6, U.T(info.Name, 16, U.Bold, wrap: true), stats, since is null ? null : U.T(since, 11.5, null, Theme.Mu2),
            string.IsNullOrWhiteSpace(info.Description) ? null : U.T(info.Description!, 12.5, null, Theme.Fg, wrap: true), btn);
        body.Padding = U.Th(16, 6, 16, 14);
        host.Child = U.V(0, banner, icon, body);
    }
}
