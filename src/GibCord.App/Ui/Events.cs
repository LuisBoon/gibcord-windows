using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GibCord.App.Ui;

/// <summary>Wydarzenia serwera: lista (trwające, zaplanowane, niedawne), „Zainteresowany”, tworzenie, edycja, odwołanie.</summary>
static class Events
{
    public static void Show(Shell sh, long serverId)
    {
        var list = new StackPanel { Spacing = 10 };
        var card = new Border
        {
            Width = 560, MaxHeight = 640, Background = Theme.Surface, CornerRadius = new CornerRadius(16), Padding = U.Th(22, 18), BorderBrush = U.W(.12), BorderThickness = U.Th(1),
        };
        var head = new Grid().Cols("*", "auto", "auto").Add(
            U.H(10, U.I("", 18, Theme.Accent), U.T("Wydarzenia", 19, U.Bold)).At(0, 0),
            U.Primary("Utwórz wydarzenie", () => { sh.CloseOverlay(); Edit(sh, serverId, null); }, 34).With(b => { b.Width = 190; b.Margin = U.Th(0, 0, 8, 0); }).At(0, 1),
            U.IconButton(G.Close, "Zamknij", sh.CloseOverlay, 13, 32).At(0, 2));
        card.Child = new Grid().Rows("auto", "*").Add(head.At(0), U.Scroll(list).With(s => s.Margin = U.Th(0, 14, 0, 0)).At(1));
        list.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });
        sh.ShowOverlay(card);
        _ = Fill(sh, serverId, list);
    }

    static async Task Fill(Shell sh, long serverId, StackPanel list)
    {
        List<ServerEvent> evs;
        try { evs = await sh.C.Api.GetEvents(serverId); }
        catch (Exception ex) { list.Children.Clear(); list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); return; }
        list.Children.Clear();
        if (evs.Count == 0)
        {
            list.Add(U.V(8, U.T("Nic nie jest zaplanowane.", 14, U.Semi), U.T("Wieczór z grami, wspólne oglądanie, nauka przed sprawdzianem? Utwórz wydarzenie — kto kliknie „Zainteresowany”, dostanie przypomnienie 15 minut przed startem.", 13, null, Theme.Mu, wrap: true)));
            return;
        }
        foreach (var e in evs.OrderBy(x => x.Status switch { "active" => 0, "scheduled" => 1, _ => 2 }).ThenBy(x => x.Open ? x.StartAt : -x.StartAt))
            list.Add(Card(sh, e, () => _ = Fill(sh, serverId, list)));
    }

    static FrameworkElement Card(Shell sh, ServerEvent e, Action reload)
    {
        var s = sh.S;
        var srv = s.ServerById(e.ServerId);
        var me = s.Me.Id;
        var going = e.Rsvp.Contains(me);
        var when = Format.Local(e.StartAt).ToString("dddd, d MMMM · HH:mm", Format.Pl);
        if (e.EndAt is long end) when += " – " + Format.Local(end).ToString(Format.SameDay(e.StartAt, end) ? "HH:mm" : "d MMM HH:mm", Format.Pl);
        var chip = e.Status switch
        {
            "active" => U.Box(U.T("TRWA", 10, U.Bold, U.B("#ffffff")), Theme.Green, 6, U.Th(6, 2)),
            "canceled" => U.Box(U.T("ODWOŁANE", 10, U.Bold, U.B("#ffffff")), Theme.Red2, 6, U.Th(6, 2)),
            "ended" => U.Box(U.T("ZAKOŃCZONE", 10, U.Bold, Theme.Fg), U.W(.15), 6, U.Th(6, 2)),
            _ => U.Box(U.T(Format.When(e.StartAt).ToUpperInvariant(), 10, U.Bold, Theme.OnAccent), Theme.Accent, 6, U.Th(6, 2)),
        };
        var where = e.ChannelId is long cid && s.ChannelById(cid) is { } ch ? (ch.IsVoice ? "🔊 " : "# ") + ch.Name : !string.IsNullOrWhiteSpace(e.Location) ? "📍 " + e.Location : null;
        var body = U.V(6, U.H(8, chip, U.T(when, 12, U.Semi, Theme.Mu)), U.T(e.Name, 16, U.Bold, wrap: true),
            where is null ? null : U.T(where, 12.5, U.Semi, Theme.Teal),
            string.IsNullOrWhiteSpace(e.Description) ? null : U.T(e.Description!, 13, null, Theme.Fg, wrap: true));
        var people = U.H(4);
        foreach (var uid in e.Rsvp.Take(6)) people.Add(new Avatar(22).Set(s, sh.C.Api, uid, srv));
        people.Add(U.T(e.Rsvp.Count == 0 ? "nikt jeszcze się nie zapisał" : $"{e.Rsvp.Count} {Format.Plural(e.Rsvp.Count, "osoba zainteresowana", "osoby zainteresowane", "osób zainteresowanych")}", 12, null, Theme.Mu).With(t => t.Margin = U.Th(6, 0, 0, 0)));
        var actions = U.H(8);
        if (e.Open)
        {
            var rsvp = new Button
            {
                Content = U.T(going ? "✓ Zainteresowany" : "Zainteresowany", 12.5, U.Bold, going ? Theme.OnAccent : Theme.Fg), Background = going ? Theme.Accent : U.W(.1),
                BorderThickness = U.Th(0), CornerRadius = new CornerRadius(10), Padding = U.Th(12, 5),
            };
            rsvp.Click += async (_, _) => { try { await sh.C.Api.Rsvp(e.Id, !going); reload(); } catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); } };
            actions.Add(rsvp);
            if (e.Active && e.ChannelId is long vc && s.ChannelById(vc) is { IsVoice: true })
                actions.Add(U.Ghost("Dołącz do kanału", () => { sh.CloseOverlay(); sh.JoinVoiceChannel(e.ServerId, vc); }, 30));
        }
        var canEdit = e.CreatorId == me || s.CanServer(srv, Perm.ManageEvents);
        if (canEdit)
        {
            var more = U.IconButton("", "Więcej", () => { }, 14, 30);
            var f = new MenuFlyout();
            if (e.Status == "scheduled") f.Items.Add(M.Item("Edytuj", "", () => { sh.CloseOverlay(); Edit(sh, e.ServerId, e); }));
            if (e.Open) f.Items.Add(M.Item("Odwołaj", "", async () =>
            {
                if (!await M.Confirm(sh, "Odwołać wydarzenie?", $"„{e.Name}” zostanie oznaczone jako odwołane.", "Odwołaj")) return;
                try { await sh.C.Api.PatchEvent(e.Id, new { status = "canceled" }); reload(); } catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
            }));
            f.Items.Add(M.Item("Usuń", "", async () =>
            {
                if (!await M.Confirm(sh, "Usunąć wydarzenie?", "Zniknie z listy u wszystkich.", "Usuń")) return;
                try { await sh.C.Api.DeleteEvent(e.Id); reload(); } catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
            }, danger: true));
            more.Flyout = f;
            actions.Add(more);
        }
        var g = U.V(10, body, people, actions);
        return new Border { Child = g, Background = e.Active ? U.Alpha("#6fe3a5", .08) : U.W(.05), CornerRadius = new CornerRadius(12), Padding = U.Th(16, 14), BorderBrush = e.Active ? U.Alpha("#6fe3a5", .4) : U.W(.08), BorderThickness = U.Th(1), Opacity = e.Open ? 1 : .6 };
    }

    /// <summary>Formularz: nazwa, data i godzina startu (i końca), miejsce (kanał albo tekst), opis.</summary>
    public static async void Edit(Shell sh, long serverId, ServerEvent? ev)
    {
        var s = sh.S;
        var srv = s.ServerById(serverId);
        if (srv is null) return;
        var name = new TextBox { Text = ev?.Name ?? "", PlaceholderText = "np. Wieczór z Minecraftem", MaxLength = 100 };
        var start = ev is null ? DateTime.Now.AddHours(1) : Format.Local(ev.StartAt);
        if (ev is null) start = start.AddMinutes(-start.Minute).AddMinutes(start.Minute >= 30 ? 60 : 30).AddSeconds(-start.Second);
        var date = new DateField { Date = start.Date, MinDate = DateTimeOffset.Now.Date, HorizontalAlignment = HorizontalAlignment.Stretch };
        var time = new TimeField { Time = start.TimeOfDay, ClockIdentifier = "24HourClock", MinuteIncrement = 5 };
        var hasEnd = new CheckField { Content = "Podaj godzinę końca", IsChecked = ev?.EndAt is not null };
        var endTime = new TimeField { Time = ev?.EndAt is long ee ? Format.Local(ee).TimeOfDay : start.AddHours(2).TimeOfDay, ClockIdentifier = "24HourClock", MinuteIncrement = 5, Visibility = ev?.EndAt is null ? Visibility.Collapsed : Visibility.Visible };
        hasEnd.Checked += (_, _) => endTime.Visibility = Visibility.Visible;
        hasEnd.Unchecked += (_, _) => endTime.Visibility = Visibility.Collapsed;
        var place = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        var channels = s.ChannelTree(srv).SelectMany(g => g.Channels).ToList();
        place.Items.Add("Bez kanału (wpiszę miejsce)");
        foreach (var c in channels) place.Items.Add((c.IsVoice ? "🔊 " : "# ") + c.Name);
        place.SelectedIndex = ev?.ChannelId is long ecid && channels.FindIndex(c => c.Id == ecid) is var ix and >= 0 ? ix + 1 : Math.Max(0, channels.FindIndex(c => c.IsVoice) + 1);
        var location = new TextBox { Text = ev?.Location ?? "", PlaceholderText = "np. Discord MC, boisko przy szkole", MaxLength = 100 };
        var desc = new TextBox { Text = ev?.Description ?? "", PlaceholderText = "Co, gdzie, co zabrać… (opcjonalnie)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80, MaxLength = 1000 };
        var d = new AppDialog
        {
            Title = ev is null ? "Nowe wydarzenie" : "Edytuj wydarzenie",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = U.V(8, U.T("Nazwa", 12.5, U.Semi, Theme.Mu), name, U.T("Kiedy", 12.5, U.Semi, Theme.Mu), new Grid { ColumnSpacing = 8 }.Cols("*", "auto").Add(date.At(0, 0), time.At(0, 1)),
                    hasEnd, endTime, U.T("Gdzie", 12.5, U.Semi, Theme.Mu), place, location, U.T("Opis", 12.5, U.Semi, Theme.Mu), desc).With(p => p.Width = 400),
            },
            PrimaryButtonText = ev is null ? "Utwórz" : "Zapisz", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        if (name.Text.Trim().Length == 0 || date.Date is not { } day) { sh.Info("Podaj nazwę i dzień wydarzenia", true); return; }
        var startLocal = day.Date + time.Time;
        var startMs = new DateTimeOffset(startLocal).ToUnixTimeMilliseconds();
        long? endMs = null;
        if (hasEnd.IsChecked == true)
        {
            var endLocal = day.Date + endTime.Time;
            if (endLocal <= startLocal) endLocal = endLocal.AddDays(1);
            endMs = new DateTimeOffset(endLocal).ToUnixTimeMilliseconds();
        }
        long? channelId = place.SelectedIndex > 0 ? channels[place.SelectedIndex - 1].Id : null;
        var body = new Dictionary<string, object?>
        {
            ["name"] = name.Text.Trim(), ["startAt"] = startMs, ["endAt"] = endMs, ["description"] = desc.Text.Trim(),
            ["location"] = location.Text.Trim(), ["channelId"] = channelId,
        };
        try
        {
            if (ev is null) await sh.C.Api.CreateEvent(serverId, body); else await sh.C.Api.PatchEvent(ev.Id, body);
            sh.Info(ev is null ? "Wydarzenie utworzone" : "Zapisano zmiany");
            Show(sh, serverId);
        }
        catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
    }
}
