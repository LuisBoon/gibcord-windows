using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GibCord.App.Ui;

/// <summary>
/// 6.3 Scena: kanał głosowy, na którym mówią tylko osoby na scenie. Widownia słucha i może podnieść rękę,
/// moderator (moderacja głosu albo zarządzanie kanałami) wpuszcza na scenę i przenosi na widownię.
/// </summary>
static class StageView
{
    public static bool IsMod(Shell sh, long cid) => sh.S.Can(cid, Perm.VoiceModerate) || sh.S.Can(cid, Perm.ManageChannels);

    static async void Act(Shell sh, long cid, string action, long? userId = null, string? done = null)
    {
        try { await sh.C.Api.Stage(cid, action, userId); if (done is not null) sh.Info(done); }
        catch (Exception ex) { sh.Info(ex is ApiException ae ? ae.Message : "Nie udało się — spróbuj jeszcze raz.", true); }
    }

    /// <summary>Środek widoku: scena (duże kafelki), ręce w górze (dla moderatora), widownia (małe awatary).</summary>
    public static FrameworkElement Build(Shell sh, long cid, List<VoiceMember> members, Server? srv, Func<VoiceMember, double, double, FrameworkElement> tile)
    {
        var me = sh.S.Me.Id;
        var mod = IsMod(sh, cid);
        var speakers = members.Where(m => m.Speaker == true).ToList();
        var audience = members.Where(m => m.Speaker != true).ToList();
        var hands = audience.Where(m => m.HandAt is > 0).OrderBy(m => m.HandAt).ToList();
        var col = new StackPanel { Spacing = 14, Margin = U.Th(20, 16), MaxWidth = 1100 };

        col.Add(U.H(8, U.I("", 16, Theme.Accent), U.T($"Na scenie · {speakers.Count}", 13, U.Bold, Theme.Mu)));
        if (speakers.Count == 0)
            col.Add(U.Box(U.T(mod ? "Scena jest pusta. Wejdź na nią przyciskiem na dole albo wpuść kogoś z widowni." : "Na scenie jeszcze nikogo nie ma. Podnieś rękę, jeśli chcesz coś powiedzieć.", 13.5, null, Theme.Mu, wrap: true), U.W(.04), 14, U.Th(16, 14)));
        else
        {
            var n = speakers.Count;
            var (w, h) = n <= 2 ? (300d, 190d) : n <= 4 ? (240d, 150d) : (190d, 120d);
            var wrap = new WrapPanel { Spacing = 12 };
            foreach (var vm in speakers)
            {
                var t = tile(vm, w, h);
                if (mod && vm.UserId != me)
                {
                    var f = new MenuFlyout();
                    f.Items.Add(M.Item("Przenieś na widownię", "", () => Act(sh, cid, "listen", vm.UserId)));
                    t.ContextFlyout = f;
                }
                wrap.Children.Add(t);
            }
            col.Add(wrap);
        }

        if (mod && hands.Count > 0)
        {
            col.Add(U.H(8, U.T("✋", 14), U.T($"Podniesione ręce · {hands.Count}", 13, U.Bold, Theme.Mu)).With(p => p.Margin = U.Th(0, 6, 0, 0)));
            var list = new StackPanel { Spacing = 6 };
            foreach (var vm in hands)
            {
                var row = new Grid { ColumnSpacing = 10 }.Cols("auto", "*", "auto", "auto").Add(
                    new Avatar(32).Set(sh.S, sh.C.Api, vm.UserId, srv).At(0, 0),
                    U.T(sh.S.NameOf(vm.UserId, srv), 14, U.Semi).With(t => t.VerticalAlignment = VerticalAlignment.Center).At(0, 1),
                    U.Primary("Wpuść na scenę", () => Act(sh, cid, "speak", vm.UserId), 32).At(0, 2),
                    U.Ghost("Opuść rękę", () => Act(sh, cid, "lower", vm.UserId), 32).At(0, 3));
                list.Add(U.Box(row, U.Alpha("#ffb98f", .08), 12, U.Th(10, 8), U.Alpha("#ffb98f", .3), 1));
            }
            col.Add(list);
        }

        col.Add(U.T($"Widownia · {audience.Count}", 13, U.Bold, Theme.Mu).With(t => t.Margin = U.Th(0, 6, 0, 0)));
        if (audience.Count == 0) col.Add(U.T("Nikt nie słucha z widowni.", 13, null, Theme.Mu2));
        else
        {
            var aw = new WrapPanel { Spacing = 10 };
            foreach (var vm in audience)
            {
                var chip = U.Box(U.H(8, new Avatar(28).Set(sh.S, sh.C.Api, vm.UserId, srv), U.T(sh.S.NameOf(vm.UserId, srv), 13, U.Semi), vm.HandAt is > 0 ? U.T("✋", 13) : null)
                    .With(p => p.VerticalAlignment = VerticalAlignment.Center), U.W(.05), 18, U.Th(6, 5, 12, 5));
                if (mod && vm.UserId != me)
                {
                    var f = new MenuFlyout();
                    f.Items.Add(M.Item("Wpuść na scenę", "", () => Act(sh, cid, "speak", vm.UserId)));
                    chip.ContextFlyout = f;
                }
                chip.Tapped += (_, _) => sh.ShowProfile(vm.UserId, chip);
                aw.Children.Add(chip);
            }
            col.Add(aw);
        }
        return U.Scroll(col.With(c => c.HorizontalAlignment = HorizontalAlignment.Center));
    }

    /// <summary>Przycisk sceny na dole: ręka w górę/w dół (widownia), wejście na scenę (moderator), zejście ze sceny.</summary>
    public static Button? Button(Shell sh, long cid, VoiceMember? mine)
    {
        if (mine is null) return null;
        Button b;
        if (mine.Speaker == true)
            b = U.IconButton("", "Zejdź ze sceny na widownię", () => Act(sh, cid, "listen", done: "Jesteś na widowni"), 16, 44);
        else if (IsMod(sh, cid))
            b = U.IconButton("", "Wejdź na scenę", () => Act(sh, cid, "speak", sh.S.Me.Id, "Jesteś na scenie — włącz mikrofon, żeby mówić"), 16, 44, U.B("#ffffff"));
        else if (mine.HandAt is > 0)
            b = U.IconButton("", "Opuść rękę", () => Act(sh, cid, "lower"), 16, 44, U.B("#ffffff"));
        else
            b = U.IconButton("", "Podnieś rękę — moderator może Cię wpuścić na scenę", () => Act(sh, cid, "hand", done: "Ręka w górze — czekaj, aż moderator Cię wpuści"), 16, 44);
        b.Background = mine.HandAt is > 0 || (mine.Speaker != true && IsMod(sh, cid)) ? U.B("#c86a3c") : U.W(.1);
        b.CornerRadius = new CornerRadius(22);
        return b;
    }

    /// <summary>Zdarzenia sceny z serwera: moderator widzi „podnosi rękę”, zaproszona osoba — „jesteś na scenie”.</summary>
    public static void OnEvent(Shell sh, GatewayEvent ev)
    {
        var cid = ev.Data.Long("channelId") ?? 0;
        var srv = sh.S.ServerOfChannel(cid);
        switch (ev.Type)
        {
            case "stage_hand" when ev.Data.Long("userId") is long u:
                sh.Info($"✋ {sh.S.NameOf(u, srv)} podnosi rękę na scenie");
                Tones.Play("joinOther");
                break;
            case "stage_invited":
                sh.Info("Jesteś na scenie — włącz mikrofon, żeby mówić");
                Tones.Play("join");
                break;
        }
    }
}
