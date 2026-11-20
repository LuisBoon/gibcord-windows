using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GibCord.App.Ui;

/// <summary>
/// Menu osoby pod prawym przyciskiem (lista osób, czat, karta profilu): profil, wiadomość, rozmowa, znajomi,
/// blokowanie i — z uprawnieniami — moderacja (wyciszenie czasowe, wyrzucenie, ban, głos).
/// </summary>
static class UserMenu
{
    const string GProfile = "", GChat = "", GCall = "", GAt = "", GCopy = "", GAddFriend = "", GRemove = "",
        GBlock = "", GShield = "", GClock = "", GKick = "", GBan = "", GTag = "";

    public static MenuFlyout Build(Shell sh, long uid, Server? srv, ChatView? chat = null, FrameworkElement? anchor = null)
    {
        var f = new MenuFlyout();
        var s = sh.S;
        var u = s.U(uid);
        var me = uid == s.Me.Id;
        if (anchor is not null) f.Items.Add(M.Item("Profil", GProfile, () => sh.ShowProfile(uid, anchor)));
        if (!me)
        {
            f.Items.Add(M.Item("Napisz wiadomość", GChat, () => sh.OpenDmWith(uid)));
            f.Items.Add(M.Item("Zadzwoń", GCall, async () =>
            {
                var (dm, err) = await sh.C.OpenDm(uid);
                if (err is not null) { sh.Info(err, true); return; }
                sh.Go(0, dm);
                sh.StartCall(dm);
            }));
        }
        if (chat is not null) f.Items.Add(M.Item("Wspomnij", GAt, () => chat!.InsertMention(uid)));
        f.Items.Add(M.Item("Kopiuj nazwę użytkownika", GCopy, () => { Sidebar.Copy("@" + (u?.Username ?? "")); sh.Info("Skopiowano @" + u?.Username); }));
        if (srv is not null && (me || s.CanServer(srv, Perm.ManageNicknames)))
            f.Items.Add(M.Item(me ? "Zmień swój nick na tym serwerze" : "Zmień nick", GTag, () => NickDialog(sh, srv, uid)));

        if (!me && u is not null)
        {
            f.Items.Add(new MenuFlyoutSeparator());
            var fr = s.Friends.GetValueOrDefault(uid);
            switch (fr?.Status)
            {
                case null when !s.Blocked.Contains(uid):
                    f.Items.Add(M.Item("Dodaj do znajomych", GAddFriend, async () => { var e = await sh.C.AddFriend(u.Username); sh.Info(e ?? "Wysłano zaproszenie do znajomych", e is not null); }));
                    break;
                case "incoming":
                    f.Items.Add(M.Item("Przyjmij zaproszenie", GAddFriend, async () => { var e = await sh.C.AcceptFriend(uid); if (e is not null) sh.Info(e, true); }));
                    break;
                case "outgoing":
                    f.Items.Add(M.Item("Anuluj zaproszenie", GRemove, async () => { var e = await sh.C.RemoveFriend(uid); if (e is not null) sh.Info(e, true); }));
                    break;
                case "friends":
                    f.Items.Add(M.Item("Usuń ze znajomych", GRemove, async () =>
                    {
                        if (!await M.Confirm(sh, "Usunąć ze znajomych?", $"{u.Name} zniknie z Twojej listy znajomych. Rozmowa prywatna zostaje.", "Usuń")) return;
                        var e = await sh.C.RemoveFriend(uid);
                        if (e is not null) sh.Info(e, true);
                    }));
                    break;
            }
            var blocked = s.Blocked.Contains(uid);
            f.Items.Add(M.Item(blocked ? "Odblokuj" : "Zablokuj", GBlock, () => SetBlocked(sh, uid, !blocked), danger: !blocked));
        }

        if (!me && srv is not null && Moderation(sh, srv, uid) is { Items.Count: > 0 } mod)
        {
            f.Items.Add(new MenuFlyoutSeparator());
            f.Items.Add(mod);
        }
        return f;
    }

    public static async void SetBlocked(Shell sh, long uid, bool on)
    {
        var name = sh.S.U(uid)?.Name ?? "tę osobę";
        if (on && !await M.Confirm(sh, $"Zablokować {name}?",
                "Nie napisze do Ciebie prywatnie, nie zadzwoni i nie zaprosi do znajomych. Jej wiadomości na wspólnych serwerach będą ukryte. Ona nie dostanie o tym informacji.", "Zablokuj"))
            return;
        try
        {
            if (on) await sh.C.Api.Block(uid); else await sh.C.Api.Unblock(uid);
            sh.Info(on ? $"Zablokowano: {name}" : $"Odblokowano: {name}");
        }
        catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
    }

    static MenuFlyoutSubItem? Moderation(Shell sh, Server srv, long uid)
    {
        var s = sh.S;
        if (srv.OwnerId == uid) return null;
        var sub = new MenuFlyoutSubItem { Text = "Moderacja", Icon = new FontIcon { Glyph = GShield } };
        var name = s.NameOf(uid, srv);
        if (s.CanServer(srv, Perm.Timeout))
        {
            var to = s.MemberOf(srv, uid)?.TimeoutUntil is long t && t > Format.NowMs();
            if (to) sub.Items.Add(M.Item("Zdejmij wyciszenie", GClock, () => Do(sh, () => sh.C.Api.Timeout(srv.Id, uid, 0), $"{name} może znowu pisać")));
            else
            {
                var tsub = new MenuFlyoutSubItem { Text = "Wycisz czasowo", Icon = new FontIcon { Glyph = GClock } };
                foreach (var (label, mins) in new[] { ("5 minut", 5), ("1 godzinę", 60), ("1 dzień", 1440), ("1 tydzień", 10080) })
                    tsub.Items.Add(M.Item(label, "", () => Do(sh, () => sh.C.Api.Timeout(srv.Id, uid, mins), $"{name} — wyciszenie na {label}")));
                sub.Items.Add(tsub);
            }
        }
        var inVoice = s.Voice.FirstOrDefault(kv => s.ServerOfChannel(kv.Key)?.Id == srv.Id && kv.Value.Any(v => v.UserId == uid));
        if (inVoice.Value is { } list && list.Find(v => v.UserId == uid) is { } vm && s.CanServer(srv, Perm.VoiceModerate))
        {
            sub.Items.Add(M.Item(vm.ServerMuted ? "Wyłącz wyciszenie w głosie" : "Wycisz w głosie (dla wszystkich)", "", () => Do(sh, () => sh.C.Api.VoiceModerate(srv.Id, uid, new { mute = !vm.ServerMuted }), null)));
            sub.Items.Add(M.Item(vm.ServerDeafened ? "Wyłącz wygłuszenie" : "Wygłusz w głosie", "", () => Do(sh, () => sh.C.Api.VoiceModerate(srv.Id, uid, new { deafen = !vm.ServerDeafened }), null)));
            sub.Items.Add(M.Item("Rozłącz z kanału głosowego", "", () => Do(sh, () => sh.C.Api.VoiceModerate(srv.Id, uid, new { disconnect = true }), $"{name} — rozłączono")));
        }
        if (s.CanServer(srv, Perm.Kick))
            sub.Items.Add(M.Item("Wyrzuć z serwera", GKick, async () =>
            {
                if (!await M.Confirm(sh, $"Wyrzucić {name}?", $"{name} zniknie z serwera {srv.Name}. Wróci tylko z nowym zaproszeniem.", "Wyrzuć")) return;
                Do(sh, () => sh.C.Api.Kick(srv.Id, uid), $"Wyrzucono: {name}");
            }, danger: true));
        if (s.CanServer(srv, Perm.Ban))
            sub.Items.Add(M.Item("Zbanuj", GBan, () => BanDialog(sh, srv, uid), danger: true));
        return sub;
    }

    static async void Do(Shell sh, Func<Task> a, string? ok)
    {
        try { await a(); if (ok is not null) sh.Info(ok); }
        catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
    }

    static async void BanDialog(Shell sh, Server srv, long uid)
    {
        var name = sh.S.NameOf(uid, srv);
        var reason = new TextBox { PlaceholderText = "Powód (opcjonalnie, widać go w dzienniku serwera)", MaxLength = 200 };
        var del = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var label in new[] { "Nie usuwaj wiadomości", "Usuń wiadomości z ostatniej godziny", "Usuń wiadomości z ostatniej doby", "Usuń wiadomości z ostatnich 7 dni" }) del.Items.Add(label);
        del.SelectedIndex = 0;
        var d = new ContentDialog
        {
            Title = $"Zbanować {name}?", Content = U.V(10, U.T("Wyrzuci tę osobę i zablokuje jej powrót na serwer.", 13, null, Theme.Mu, wrap: true), reason, del),
            PrimaryButtonText = "Zbanuj", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Close, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var hours = del.SelectedIndex switch { 1 => 1, 2 => 24, 3 => 168, _ => 0 };
        Do(sh, () => sh.C.Api.Ban(srv.Id, uid, reason.Text, hours), $"Zbanowano: {name}");
    }

    static async void NickDialog(Shell sh, Server srv, long uid)
    {
        var me = uid == sh.S.Me.Id;
        var cur = sh.S.MemberOf(srv, uid)?.Nick ?? "";
        var box = new TextBox { Text = cur, PlaceholderText = sh.S.U(uid)?.Name ?? "", MaxLength = 32 };
        var d = new ContentDialog
        {
            Title = me ? $"Twój nick na {srv.Name}" : $"Nick: {sh.S.U(uid)?.Name}", Content = U.V(8, U.T("Pusty = zwykła nazwa.", 12.5, null, Theme.Mu), box),
            PrimaryButtonText = "Zapisz", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() == cur) return;
        Do(sh, () => sh.C.Api.SetNick(srv.Id, me ? 0 : uid, box.Text), "Nick zapisany");
    }

    /// <summary>„Zgłoś wiadomość”: powód + opcjonalny opis (trafia do moderatorów serwera albo — z DM — do właścicieli GibCorda).</summary>
    public static async void Report(Shell sh, Message m)
    {
        var reasons = new[] { ("spam", "Spam lub reklama"), ("harassment", "Nękanie, obrażanie"), ("nsfw", "Treści 18+"), ("scam", "Oszustwo, wyłudzenie"), ("other", "Coś innego") };
        var radios = new RadioButtons();
        foreach (var (_, label) in reasons) radios.Items.Add(label);
        radios.SelectedIndex = 0;
        var note = new TextBox { PlaceholderText = "Dodatkowe informacje (opcjonalnie)", MaxLength = 500, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64 };
        var srv = sh.S.ServerOfChannel(m.ChannelId);
        var snap = U.Box(U.V(2, U.T(sh.S.NameOf(m.AuthorId, srv), 12.5, U.Bold), U.T(sh.S.PlainText(m, 240), 12.5, null, Theme.Mu, wrap: true)), U.W(.06), 8, U.Th(10, 8));
        var d = new ContentDialog
        {
            Title = "Zgłoś wiadomość",
            Content = U.V(10, snap, radios, note, U.T(srv is not null ? "Zgłoszenie zobaczą moderatorzy tego serwera i właściciele GibCorda." : "Zgłoszenie z rozmowy prywatnej zobaczą tylko właściciele GibCorda — tylko tę jedną wiadomość.", 11.5, null, Theme.Mu2, wrap: true)),
            PrimaryButtonText = "Wyślij zgłoszenie", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var reason = reasons[Math.Max(0, radios.SelectedIndex)].Item1;
        Do(sh, () => sh.C.Api.Report(m.Id, reason, note.Text), "Dziękujemy — zgłoszenie wysłane");
    }
}
