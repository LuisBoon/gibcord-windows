using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

/// <summary>6.4: most z Discordem — wygląd wiadomości z Discorda, znaczek w nagłówku kanału (PLAN-6.4.md, sekcja M „Klienci”).</summary>
static class BridgeUi
{
    /// <summary>Kolor Discorda: tło litery, gdy osoba z Discorda nie ma awatara, i znaczek „DISCORD”.</summary>
    public const string Blurple = "#5865f2";

    /// <summary>Znaczek przy nazwie (jak BOT).</summary>
    public static Border Tag() => U.Box(U.T("DISCORD", 9, U.Bold, U.B("#ffffff")), U.B(Blurple), 5, U.Th(4, 1))
        .With(b => b.Tip("Wiadomość z Discorda — przyszła przez most z połączonego kanału"));

    /// <summary>Mała karta po kliknięciu w nazwę albo awatar osoby z Discorda (zamiast profilu konta systemowego „Discord”).</summary>
    public static void ShowAuthor(Shell sh, MessageExt ext, Channel? ch, FrameworkElement anchor)
    {
        var name = ext.Name ?? "Discord";
        var where = ch?.Bridge is { } b ? $"Pisze na kanale #{b.Channel} na serwerze „{b.Guild}” na Discordzie. Jej wiadomości przychodzą tu przez most." : "Ta osoba pisze na Discordzie. Jej wiadomości przychodzą tu przez most.";
        var card = U.V(10,
            U.T("WIADOMOŚĆ Z DISCORDA", 10.5, U.Bold, Theme.Mu2),
            U.H(12, new Avatar(48).SetRaw(name, Blurple, sh.C.Api.Abs(ext.Avatar), null), U.V(4, U.T(name, 16, U.Bold), Tag().With(t => t.HorizontalAlignment = HorizontalAlignment.Left))),
            U.T(where, 12.5, null, Theme.Mu, wrap: true),
            U.T("To nie jest konto GibCorda — nie da się do tej osoby napisać prywatnie ani dodać jej do znajomych.", 11.5, null, Theme.Mu2, wrap: true));
        card.Width = 270;
        card.Padding = U.Th(16, 14);
        var fly = new Flyout { Content = card, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        fly.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
        {
            Setters = { new Setter(Control.PaddingProperty, new Thickness(0)), new Setter(Control.BackgroundProperty, Theme.Surface), new Setter(Control.CornerRadiusProperty, new CornerRadius(14)) },
        };
        fly.ShowAt(anchor);
    }

    /// <summary>Znaczek w nagłówku kanału: „⇄ Discord · #general” (wstrzymany: przygaszony z dopiskiem).</summary>
    public static FrameworkElement? HeaderChip(Channel c)
    {
        if (c.Bridge is not { } b) return null;
        var text = U.T($"⇄ Discord · #{b.Channel}" + (b.Paused ? " · wstrzymany" : ""), 11.5, U.Bold, b.Paused ? Theme.Mu : U.B("#dfe2ff"));
        var chip = U.Box(text, b.Paused ? U.W(.08) : U.Alpha(Blurple, .35), 8, U.Th(8, 2), b.Paused ? U.W(.12) : U.Alpha(Blurple, .7), 1);
        chip.VerticalAlignment = VerticalAlignment.Center;
        chip.Margin = U.Th(10, 0, 0, 0);
        chip.Tip($"Połączony z kanałem #{b.Channel} na serwerze „{b.Guild}” na Discordzie" + (b.Paused ? " — most jest wstrzymany (wiadomości nie przechodzą)" : ""));
        return chip;
    }
}

/// <summary>Ustawienia serwera → Integracje → „Most z Discordem”.</summary>
public sealed partial class ServerSettingsPage
{
    /// <summary>Tryb pokazowy (zrzut --view srvmost).</summary>
    public static BridgesInfo? DemoBridges;
    static BridgeCodeInfo? _demoCode;

    async Task<Border> BridgeCard(Server srv)
    {
        var box = new StackPanel { Spacing = 10 };
        var card = Card(U.H(8, U.T("⇄", 15, U.Bold, U.B(BridgeUi.Blurple)), U.T("MOST Z DISCORDEM", 11, U.Bold, Theme.Mu2)), box);
        BridgesInfo info;
        try { info = DemoBridges ?? (Can(Perm.ManageServer) ? await Api.GetBridges(srv.Id) : new BridgesInfo { Enabled = _sh.C.Info?.Has("bridge") == true }); }
        catch (Exception ex) { box.Add(U.T(GibCordClient.Friendly(ex), 12.5, null, Theme.Red, wrap: true)); return card; }
        if (!info.Enabled)
        {
            box.Add(U.T("Most z Discordem nie jest jeszcze włączony na tym GibCordzie. Właściciel GibCorda musi dodać bota.", 13, null, Theme.Mu, wrap: true));
            return card;
        }
        if (!Can(Perm.ManageServer))
        {
            box.Add(U.T("Mosty z Discordem ustawia ktoś z prawem „Zarządzanie serwerem”.", 12.5, null, Theme.Mu, wrap: true));
            return card;
        }
        box.Add(U.T("Kanał tekstowy GibCorda i kanał na Discordzie piszą wtedy razem: wiadomości, odpowiedzi, edycje, usuwanie, obrazki i reakcje przechodzą w obie strony.", 12.5, null, Theme.Mu, wrap: true));
        // stan bota
        var (stTxt, stCol) = info.Status switch
        {
            "ready" => ("Bot Discorda działa", Theme.Green),
            "connecting" => ("Bot Discorda łączy się…", Theme.Yellow),
            "error" => ("Bot Discorda ma problem" + (string.IsNullOrEmpty(info.Error) ? "" : ": " + info.Error), Theme.Red),
            _ => ("Bot Discorda jest wyłączony", Theme.Mu),
        };
        box.Add(U.H(8, new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 9, Height = 9, Fill = stCol }, U.T(stTxt, 12.5, U.Semi, stCol, wrap: true)));

        // lista mostów
        if (info.Items.Count == 0) box.Add(U.T("Żaden kanał tego serwera nie jest jeszcze połączony z Discordem.", 12.5, null, Theme.Mu2, wrap: true));
        foreach (var it in info.Items)
        {
            var b = it;
            var ch = srv.Channels.Find(c => c.Id == b.ChannelId);
            var pause = new SwitchField { Header = "Wstrzymaj", IsOn = b.Paused };
            pause.Toggled += async (_, _) =>
            {
                if (DemoBridges is not null) { b.Paused = pause.IsOn; return; }
                try { await Api.PatchBridge(b.Id, pause.IsOn); _sh.Info(pause.IsOn ? "Most wstrzymany — wiadomości nie przechodzą" : "Most znowu działa"); }
                catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
            };
            var row = new Grid { ColumnSpacing = 10 }.Cols("*", "auto", "auto").Add(
                U.V(2,
                    U.H(6, U.T("#" + (ch?.Name ?? "kanał"), 14, U.Bold), U.T("⇄", 13, U.Bold, U.B(BridgeUi.Blurple)), U.T("#" + b.Channel, 14, U.Bold, U.B("#c9cdfb"))),
                    U.T($"Discord: {b.Guild} · od {Format.Full(b.CreatedAt)}" + (b.Paused ? " · wstrzymany" : ""), 11.5, null, Theme.Mu2)).At(0, 0),
                pause.With(p => p.VerticalAlignment = VerticalAlignment.Center).At(0, 1),
                U.Ghost("Rozłącz", async () =>
                {
                    if (!await M.Confirm(_sh, $"Rozłączyć #{ch?.Name} z Discordem?", $"Wiadomości przestaną przechodzić między #{ch?.Name} a #{b.Channel} na serwerze „{b.Guild}”. Bot usunie swój webhook na Discordzie. Połączyć znowu można nowym kodem.", "Rozłącz")) return;
                    if (DemoBridges is not null) { DemoBridges.Items.Remove(b); Integrations0(); return; }
                    if (await Run(() => Api.DeleteBridge(b.Id), "Most rozłączony")) Integrations0();
                }, 30).With(x => x.VerticalAlignment = VerticalAlignment.Center).At(0, 2));
            box.Add(U.Box(row, U.W(.04), 12, U.Th(12, 8)));
        }

        // połącz kanał
        var bridged = info.Items.Select(i => i.ChannelId).ToHashSet();
        var texts = srv.Channels.Where(c => c.Type == "text" && c.Kind is null or "" && !bridged.Contains(c.Id)).OrderBy(c => c.Position).ToList();
        if (texts.Count == 0) return card;
        var pick = new DropField { Header = U.T("Kanał GibCorda", 12.5, U.Semi), Width = 260 };
        foreach (var c in texts) pick.Items.Add("#" + c.Name);
        pick.SelectedIndex = 0;
        var steps = new StackPanel { Spacing = 10 };
        var go = U.Primary("Połącz kanał", async () =>
        {
            var ch = texts[Math.Max(0, pick.SelectedIndex)];
            BridgeCodeInfo code;
            try { code = DemoBridges is not null ? _demoCode ??= new BridgeCodeInfo { Code = "GC-7KQM-X4PD", Command = "/gibcord polacz kod:GC-7KQM-X4PD", InviteUrl = "https://discord.com/oauth2/authorize", ExpiresAt = Format.NowMs() + 30 * 60_000 } : await Api.BridgeCode(ch.Id); }
            catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); return; }
            ShowSteps(steps, ch, code);
        }, 34);
        box.Add(U.HLine(.08));
        box.Add(U.T("POŁĄCZ KANAŁ", 11, U.Bold, Theme.Mu2));
        box.Add(U.HG(10, pick, go.With(b => b.VerticalAlignment = VerticalAlignment.Bottom)));
        box.Add(steps);
        if (DemoBridges is not null && _demoCode is not null) ShowSteps(steps, texts[0], _demoCode);
        return card;
    }

    /// <summary>Trzy kroki łączenia: bot na serwerze Discord → komenda z kodem → kanał łączy się sam (channel_update odświeża listę).</summary>
    void ShowSteps(StackPanel steps, Channel ch, BridgeCodeInfo code)
    {
        steps.Children.Clear();
        FrameworkElement Step(int n, string title, params UIElement?[] more)
        {
            var num = U.Box(U.T(n.ToString(), 12, U.Bold, U.B("#ffffff")), U.B(BridgeUi.Blurple), 11, U.Th(0)).With(b => { b.Width = b.Height = 22; ((TextBlock)b.Child).HorizontalAlignment = HorizontalAlignment.Center; ((TextBlock)b.Child).VerticalAlignment = VerticalAlignment.Center; });
            var body = U.V(6, U.T(title, 13.5, U.Semi, Theme.Fg, wrap: true));
            foreach (var m in more) if (m is not null) body.Add(m);
            return new Grid { ColumnSpacing = 10 }.Cols("auto", "*").Add(num.With(b => b.VerticalAlignment = VerticalAlignment.Top).At(0, 0), body.At(0, 1));
        }
        var left = Math.Max(0, (code.ExpiresAt - Format.NowMs()) / 60_000);
        var cmd = new TextBlock { Text = code.Command, FontFamily = Theme.Mono, FontSize = 13.5, Foreground = Theme.Fg, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var copy = U.Ghost("Kopiuj", () => { Sidebar.Copy(code.Command); _sh.Info("Komenda skopiowana — wklej ją na kanale Discorda"); }, 30);
        steps.Add(U.Box(U.V(12,
            U.T($"Łączenie #{ch.Name}", 14, U.Bold),
            Step(1, "Dodaj bota GibCorda na swój serwer Discord", code.InviteUrl is { } inv && Uri.TryCreate(inv, UriKind.Absolute, out var iu)
                ? U.Ghost("Dodaj bota na Discorda", () => _ = Windows.System.Launcher.LaunchUriAsync(iu), 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)
                : U.T("Link do dodania bota da właściciel GibCorda.", 12, null, Theme.Mu2, wrap: true)),
            Step(2, "Na wybranym kanale Discorda wpisz:", U.HG(8, U.Box(cmd, U.B("#33000000"), 8, U.Th(10, 6)), copy),
                U.T($"Kod jest ważny 30 minut (zostało {left} min). Potrzebne jest prawo „Zarządzanie kanałami” na Discordzie.", 11.5, null, Theme.Mu2, wrap: true)),
            Step(3, "Gotowe — kanał połączy się sam", U.T("Lista mostów odświeży się, gdy bot potwierdzi połączenie.", 11.5, null, Theme.Mu2, wrap: true))),
            U.Alpha(BridgeUi.Blurple, .12), 12, U.Th(14, 12), U.Alpha(BridgeUi.Blurple, .45), 1));
        _bridgeWaitFor = ch.Id;
    }

    long _bridgeWaitFor;

    /// <summary>channel_update z polem bridge dla kanału, który właśnie łączymy → odśwież sekcję.</summary>
    void OnBridgeChange(StoreChange c)
    {
        if (_bridgeWaitFor == 0 || c.Kind != ChangeKind.Server || S.ChannelById(_bridgeWaitFor) is not { Bridge: not null }) return;
        _bridgeWaitFor = 0;
        _sh.Info("Kanał połączony z Discordem");
        if (_section == "integrations") Integrations0();
    }
}
