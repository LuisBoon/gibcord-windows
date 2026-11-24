using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GibCord.App.Ui;

/// <summary>6.4: ustawienia serwera → Integracje: most z Discordem (Bridge.cs), boty na serwerze (usuwanie) i webhooki kanałów (adres do wklejenia w inny program).</summary>
public sealed partial class ServerSettingsPage
{
    async void Integrations()
    {
        var srv = Srv!;
        Title("Integracje", "Most z Discordem, boty i webhooki tego serwera. Webhook to adres, pod który inny program (np. panel serwera, sklep, alarm) wysyła wiadomości na kanał.");
        // 6.4: most z Discordem (na górze — wczytuje się z serwera, miejsce rezerwujemy od razu)
        var bridgeHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _body.Add(bridgeHost);
        bridgeHost.Content = await BridgeCard(srv);
        // boty
        var bots = srv.Members.Where(m => S.U(m.UserId)?.Bot == "bot").ToList();
        var bl = new StackPanel { Spacing = 8 };
        if (bots.Count == 0) bl.Add(U.T("Na serwerze nie ma botów. Bota dodajesz w Ustawienia → Moje boty (swojego) albo przyciskiem od jego twórcy.", 12.5, null, Theme.Mu, wrap: true));
        foreach (var m in bots)
        {
            var uid = m.UserId;
            var row = new Grid { ColumnSpacing = 10 }.Cols("auto", "*", "auto").Add(
                new Avatar(34).Set(S, Api, uid, srv, status: true).At(0, 0),
                U.V(0, U.HG(6, U.T(S.NameOf(uid, srv), 14, U.Bold), U.Box(U.T("BOT", 9, U.Bold, U.B("#1b1531")), U.B("#7cc7ff"), 5, U.Th(4, 1))), U.T("@" + (S.U(uid)?.Username ?? ""), 11.5, null, Theme.Mu2)).With(p => p.VerticalAlignment = VerticalAlignment.Center).At(0, 1),
                Can(Perm.Kick) ? U.Ghost("Usuń z serwera", async () =>
                {
                    if (!await M.Confirm(_sh, $"Usunąć {S.NameOf(uid, srv)} z serwera?", "Bot zniknie z serwera, a jego komendy przestaną się podpowiadać.", "Usuń")) return;
                    if (await Run(() => Api.Kick(srv.Id, uid), "Bot usunięty z serwera")) Integrations0();
                }, 30).At(0, 2) : null);
            bl.Add(U.Box(row, U.W(.04), 12, U.Th(10, 8)));
        }
        _body.Add(Card(U.T("BOTY", 11, U.Bold, Theme.Mu2), bl));

        // webhooki
        var wl = new StackPanel { Spacing = 8 };
        var texts = srv.Channels.Where(c => c.Type == "text" && S.Can(c.Id, Perm.ManageChannels)).OrderBy(c => c.Position).ToList();
        var pick = new DropField { Header = U.T("Kanał", 12.5, U.Semi), Width = 260 };
        foreach (var c in texts) pick.Items.Add("#" + c.Name);
        if (texts.Count > 0) pick.SelectedIndex = 0;
        var name = new TextBox { PlaceholderText = "Nazwa, np. Panel serwera", MaxLength = 32, Width = 240 };
        var create = U.Primary("Utwórz webhook", async () =>
        {
            if (texts.Count == 0) return;
            try
            {
                var w = await Api.CreateWebhook(texts[Math.Max(0, pick.SelectedIndex)].Id, name.Text.Trim());
                if (w.Url is { } url) { Sidebar.Copy(url); _sh.Info("Webhook utworzony — adres skopiowany. Nie pokazuj go nikomu: kto go zna, może pisać na kanale."); }
                Integrations0();
            }
            catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
        }, 34);
        wl.Add(U.HG(10, pick, name.With(t => t.VerticalAlignment = VerticalAlignment.Bottom), create.With(b => b.VerticalAlignment = VerticalAlignment.Bottom)));
        var hooks = new StackPanel { Spacing = 6 };
        wl.Add(hooks);
        _body.Add(Card(U.T("WEBHOOKI", 11, U.Bold, Theme.Mu2), wl,
            U.T("Przykład: curl -X POST -H \"Content-Type: application/json\" -d '{\"content\":\"Kopia gotowa\"}' ADRES_WEBHOOKA", 11.5, null, Theme.Mu2, wrap: true).With(t => t.FontFamily = Theme.Mono)));
        foreach (var c in _sh.C.Offline ? new List<Channel>() : texts) // tryb pokazowy: bez zapytań do serwera
        {
            List<WebhookInfo> list;
            try { list = await Api.GetWebhooks(c.Id); } catch { continue; }
            foreach (var w in list)
            {
                var wid = w.Id;
                var row = new Grid { ColumnSpacing = 8 }.Cols("*", "auto", "auto").Add(
                    U.V(0, U.T(w.Name, 13.5, U.Bold), U.T("#" + c.Name + " · od " + Format.Full(w.CreatedAt), 11.5, null, Theme.Mu2)).At(0, 0),
                    U.Ghost("Kopiuj adres", () => { if (w.Url is { } u) { Sidebar.Copy(u); _sh.Info("Adres webhooka skopiowany"); } }, 30).At(0, 1),
                    U.Ghost("Usuń", async () =>
                    {
                        if (!await M.Confirm(_sh, $"Usunąć webhook „{w.Name}”?", "Program, który go używa, nie będzie już mógł pisać na kanale.", "Usuń")) return;
                        if (await Run(() => Api.DeleteWebhook(wid), "Webhook usunięty")) Integrations0();
                    }, 30).At(0, 2));
                hooks.Add(U.Box(row, U.W(.04), 10, U.Th(10, 6)));
            }
        }
        if (hooks.Children.Count == 0) hooks.Add(U.T("Brak webhooków.", 12.5, null, Theme.Mu));
    }

    void Integrations0() { _body.Children.Clear(); Integrations(); }
}
