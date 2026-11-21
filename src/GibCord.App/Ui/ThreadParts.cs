using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace GibCord.App.Ui;

/// <summary>Ikony wątków (Segoe Fluent Icons).</summary>
static class GT
{
    public const string Thread = "", Threads = "", Bell = "", More = "", Lock = "", Edit = "",
        Delete = "", Link = "", Archive = "", Close = "", Add = "";
}

/// <summary>Wątki w widoku czatu: nagłówek panelu wątku, lista wątków kanału, zakładanie wątku z wiadomości.</summary>
public sealed partial class ChatView
{
    /// <summary>Pierwsza wiadomość wątku (przychodzi z GET /api/threads/:id) — rysowana nad odpowiedziami.</summary>
    public void SetStarter(Message? starter)
    {
        _starter = starter;
        if (!_disposed) UpdateTop(S.Window(Key));
    }

    void ThreadHeader()
    {
        var t = Thread;
        var name = U.T(t?.Name ?? "Wątek", 15, U.Bold);
        name.Tip(t?.Name ?? "");
        var sub = U.T($"w #{Ch?.Name ?? ""}" + (t?.Locked == true ? " · zamknięty" : ""), 11.5, null, Theme.Mu);
        var following = t?.Following == true;
        var followText = U.T(following ? "Obserwujesz" : "Obserwuj", 12, U.Bold, following ? Theme.OnAccent : Theme.Fg);
        var follow = new Button
        {
            Content = U.H(6, U.I(GT.Bell, 13, following ? Theme.OnAccent : Theme.Fg), followText),
            Background = following ? Theme.Accent : U.W(.1), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(14), Padding = U.Th(10, 4), Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        follow.Tip(following ? "Dostajesz powiadomienia o nowych odpowiedziach. Kliknij, żeby przestać." : "Dostawaj powiadomienia o nowych odpowiedziach w tym wątku");
        follow.Click += async (_, _) =>
        {
            var err = await Sh.C.FollowThread(Tid, !following);
            if (err is not null) Sh.Info(err, true);
        };
        var more = U.IconButton(GT.More, "Więcej", () => { }, 15, 32);
        more.Flyout = ThreadMenu();
        var g = new Grid().Cols("auto", "*", "auto", "auto", "auto").Add(
            U.I(GT.Thread, 17, Theme.Accent).With(i => i.Margin = U.Th(0, 0, 10, 0)).At(0, 0),
            U.V(0, name, sub).With(p => p.VerticalAlignment = VerticalAlignment.Center).At(0, 1),
            follow.With(b => b.Margin = U.Th(8, 0, 4, 0)).At(0, 2),
            more.At(0, 3),
            U.IconButton(GT.Close, "Zamknij wątek (Esc)", Sh.CloseThread, 14, 32).At(0, 4));
        // w wąskim panelu sam dzwonek (kolor mówi, czy obserwujesz) — inaczej nazwa wątku kurczyła się do „Sobot…”
        g.SizeChanged += (_, e) => followText.Visibility = e.NewSize.Width < 400 ? Visibility.Collapsed : Visibility.Visible;
        _header.Child = g;
    }

    MenuFlyout ThreadMenu()
    {
        var f = new MenuFlyout();
        var t = Thread;
        if (t is null) return f;
        var mod = S.Can(Cid, Perm.ManageMessages);
        if (t.CreatorId == S.Me.Id || mod)
            f.Items.Add(M.Item("Zmień nazwę", GT.Edit, async () =>
            {
                var box = new TextBox { Text = t.Name, MaxLength = 100, SelectionStart = t.Name.Length };
                var d = new AppDialog
                {
                    Title = "Nazwa wątku", Content = box, PrimaryButtonText = "Zapisz", CloseButtonText = "Anuluj",
                    DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
                };
                if (await d.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() is not { Length: > 0 } n || n == t.Name) return;
                var err = await Sh.C.PatchThread(Tid, new { name = n });
                if (err is not null) Sh.Info(err, true);
            }));
        if (mod)
            f.Items.Add(M.Item(t.Locked ? "Otwórz wątek do pisania" : "Zamknij wątek do pisania", GT.Lock, async () =>
            {
                var err = await Sh.C.PatchThread(Tid, new { locked = !t.Locked });
                if (err is not null) Sh.Info(err, true);
                else Sh.Info(t.Locked ? "Wątek otwarty — znowu każdy może pisać." : "Wątek zamknięty — piszą w nim tylko moderatorzy.");
            }));
        f.Items.Add(M.Item("Kopiuj link do wątku", GT.Link, () =>
        {
            Sidebar.Copy($"{Sh.C.Api.BaseUri.AbsoluteUri.TrimEnd('/')}/kanal/{t.ServerId}/{t.ChannelId}/watek/{t.Id}");
            Sh.Info("Skopiowano link do wątku");
        }));
        if (mod)
        {
            f.Items.Add(new MenuFlyoutSeparator());
            f.Items.Add(M.Item("Usuń wątek", GT.Delete, async () =>
            {
                if (!await M.Confirm(Sh, "Usunąć wątek?", $"Wątek „{t.Name}” zniknie razem ze wszystkimi odpowiedziami ({t.Count}). Pierwsza wiadomość zostanie na kanale.", "Usuń")) return;
                try { await Sh.C.Api.DeleteThread(Tid); Sh.CloseThread(); }
                catch (Exception ex) { Sh.Info(GibCordClient.Friendly(ex), true); }
            }, danger: true));
        }
        return f;
    }

    /// <summary>Przycisk „Wątki” w nagłówku kanału (z kropką, gdy w obserwowanym wątku jest coś nowego).</summary>
    FrameworkElement ThreadsButton()
    {
        var b = U.IconButton(GT.Threads, "Wątki", () => { }, 16, 32);
        b.Click += (_, _) => ShowThreadList(b);
        if (S.UnreadThreadsIn(Cid) == 0) return b;
        var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = Theme.Red, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = U.Th(0, 5, 5, 0), IsHitTestVisible = false };
        return new Grid().Add(b, dot);
    }

    async void ShowThreadList(FrameworkElement anchor)
    {
        var list = new StackPanel { Spacing = 4, Width = 400 };
        var fly = new Flyout { Content = U.Scroll(list).With(s => s.MaxHeight = 520), Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        list.Add(U.T("Wątki", 15, U.Bold).With(t => t.Margin = U.Th(4, 0, 0, 6)));
        list.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22, Foreground = Theme.Mu });
        fly.ShowAt(anchor);
        await FillThreadList(list, fly, archived: false);
    }

    async Task FillThreadList(StackPanel list, Flyout fly, bool archived)
    {
        var (threads, archivedCount, err) = await Sh.C.ListThreads(Cid, archived);
        while (list.Children.Count > 1) list.Children.RemoveAt(1);
        if (err is not null) { list.Add(U.T(err, 13, null, Theme.Red, wrap: true)); return; }
        if (threads.Count == 0)
            list.Add(U.T(archived ? "Nie ma zarchiwizowanych wątków." : "Na tym kanale nie ma jeszcze aktywnych wątków. Załóż wątek z menu wiadomości (prawy przycisk → „Utwórz wątek”).",
                13, null, Theme.Mu, wrap: true).With(t => t.Margin = U.Th(4, 0, 4, 6)));
        foreach (var t in threads)
        {
            var authors = U.H(2);
            foreach (var a in t.LastAuthors.Take(3)) authors.Add(new Avatar(20).Set(S, Sh.C.Api, a, Srv));
            var title = U.HG(6, t.Unread ? new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Fg } : null,
                U.T(t.Name, 13.5, t.Unread ? U.Bold : U.Semi), t.Locked ? U.I(GT.Lock, 11, Theme.Mu2) : null);
            var meta = U.T($"{t.Count} {Format.Plural(t.Count, "odpowiedź", "odpowiedzi", "odpowiedzi")} · {Format.When(t.LastAt)}" + (t.Following == true ? " · obserwujesz" : ""), 11.5, null, Theme.Mu);
            var row = new Grid { Padding = U.Th(8, 8) }.Cols("*", "auto").Add(U.V(2, title, meta).At(0, 0), authors.With(a => a.VerticalAlignment = VerticalAlignment.Center).At(0, 1));
            var id = t.Id;
            list.Add(U.Row(row, () => { fly.Hide(); Sh.OpenThread(id); }, false, 10, U.Th(0), U.Th(0, 1)));
        }
        if (!archived && archivedCount > 0)
        {
            var more = U.Ghost($"Zarchiwizowane ({archivedCount})", () => { }, 32);
            more.Click += async (_, _) =>
            {
                while (list.Children.Count > 1) list.Children.RemoveAt(1);
                list.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22, Foreground = Theme.Mu });
                await FillThreadList(list, fly, archived: true);
            };
            list.Add(more.With(b => b.Margin = U.Th(0, 6, 0, 0)));
        }
    }

    /// <summary>„Utwórz wątek” z menu wiadomości: nazwa (podpowiedziana z treści) i od razu otwarcie panelu.</summary>
    public async void StartThread(Message m)
    {
        var plain = S.PlainText(m, 100).Replace('\n', ' ').Trim();
        if (plain.Length > 60) { plain = plain[..60]; var sp = plain.LastIndexOf(' '); if (sp > 20) plain = plain[..sp]; plain += "…"; }
        var box = new TextBox { Text = plain, PlaceholderText = "Nazwa wątku", MaxLength = 100 };
        box.Loaded += (_, _) => { box.SelectAll(); box.Focus(FocusState.Programmatic); };
        var d = new AppDialog
        {
            Title = "Nowy wątek", Content = U.V(8, U.T("Rozmowa z boku, przy tej wiadomości — kanał zostaje czysty.", 13, null, Theme.Mu, wrap: true), box),
            PrimaryButtonText = "Utwórz wątek", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var (t, err) = await Sh.C.CreateThread(m.Id, box.Text);
        if (err is not null || t is null) { Sh.Info(err ?? "Nie udało się założyć wątku", true); return; }
        Sh.OpenThread(t.Id);
    }
}
