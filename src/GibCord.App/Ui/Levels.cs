using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GibCord.App.Ui;

/// <summary>Poziomy: ranking serwera (XP, wiadomości, minuty w rozmowach) i postęp na karcie profilu.</summary>
static class Levels
{
    public static void Show(Shell sh, Server srv)
    {
        var list = new StackPanel { Spacing = 4 };
        var card = new Border { Width = 560, MaxHeight = 640, Background = U.B("#FA2a2147"), CornerRadius = new CornerRadius(16), Padding = U.Th(22, 18), BorderBrush = U.W(.12), BorderThickness = U.Th(1) };
        var head = new Grid().Cols("*", "auto").Add(U.H(10, U.T("🏆", 20), U.T($"Ranking · {srv.Name}", 18, U.Bold)).At(0, 0), U.IconButton(G.Close, "Zamknij", sh.CloseOverlay, 13, 32).At(0, 1));
        card.Child = new Grid().Rows("auto", "auto", "*").Add(head.At(0),
            U.T("XP za pisanie (raz na minutę) i rozmowy głosowe z innymi.", 12.5, null, Theme.Mu, wrap: true).With(t => t.Margin = U.Th(0, 4, 0, 10)).At(1),
            U.Scroll(list).At(2));
        list.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });
        sh.ShowOverlay(card);
        _ = Fill(sh, srv, list);
    }

    static async Task Fill(Shell sh, Server srv, StackPanel list)
    {
        try
        {
            var arr = await sh.C.Api.GetJson($"servers/{srv.Id}/leaderboard?limit=50");
            list.Children.Clear();
            foreach (var e in arr.EnumerateArray())
            {
                var uid = e.Long("userId") ?? 0;
                var rank = e.Long("rank") ?? 0;
                var lvl = e.Long("level") ?? 0;
                var medal = rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => "#" + rank };
                var me = uid == sh.S.Me.Id;
                var g = new Grid { Padding = U.Th(10, 6) }.Cols("44", "auto", "*", "auto").Add(
                    U.T(medal, rank <= 3 ? 20 : 13, U.Bold, Theme.Mu).With(t => t.HorizontalAlignment = HorizontalAlignment.Center).At(0, 0),
                    new Avatar(32).Set(sh.S, sh.C.Api, uid, srv).At(0, 1),
                    U.V(1, U.T(sh.S.NameOf(uid, srv), 14, U.Bold, U.B(sh.S.NameColor(uid, srv))),
                        U.T($"{e.Long("messages") ?? 0} {Format.Plural(e.Long("messages") ?? 0, "wiadomość", "wiadomości", "wiadomości")} · {e.Long("voiceMinutes") ?? 0} min w rozmowach", 11.5, null, Theme.Mu2)).With(p => p.Margin = U.Th(12, 0)).At(0, 2),
                    U.V(0, U.T($"poziom {lvl}", 13, U.Bold, Theme.Yellow), U.T($"{e.Long("xp") ?? 0} XP", 11, null, Theme.Mu2)).With(p => p.HorizontalAlignment = HorizontalAlignment.Right).At(0, 3));
                list.Add(new Border { Child = g, Background = me ? U.Alpha("#ffb98f", .1) : U.W(.035), CornerRadius = new CornerRadius(10) });
            }
            if (list.Children.Count == 0) list.Add(U.T("Jeszcze nikt nie zdobył XP.", 13.5, null, Theme.Mu));
        }
        catch (Exception ex) { list.Children.Clear(); list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    }

    /// <summary>Pasek postępu poziomu (dociągany po otwarciu karty profilu).</summary>
    public static FrameworkElement Progress(Shell sh, Server srv, long uid)
    {
        var host = new StackPanel { Spacing = 4 };
        _ = FillProgress(sh, srv, uid, host);
        return host;
    }

    static async Task FillProgress(Shell sh, Server srv, long uid, StackPanel host)
    {
        try
        {
            var st = await sh.C.Api.GetJson($"servers/{srv.Id}/members/{(uid == sh.S.Me.Id ? "@me" : uid.ToString())}/stats");
            var cur = st.Long("levelXp") ?? 0;
            var next = Math.Max(1, st.Long("nextLevelXp") ?? 1);
            host.Add(new Grid().Cols("*", "auto").Add(U.T($"⭐ Poziom {st.Long("level") ?? 0}", 13, U.Bold, Theme.Yellow).At(0, 0), U.T($"#{st.Long("rank") ?? 0} na serwerze", 12, null, Theme.Mu).At(0, 1)));
            host.Add(new ProgressBar { Minimum = 0, Maximum = next, Value = cur, Height = 6, Foreground = Theme.Yellow, Background = U.W(.1) });
            host.Add(U.T($"{cur} / {next} XP do następnego · {st.Long("messages") ?? 0} wiad. · {st.Long("voiceMinutes") ?? 0} min głosu", 11, null, Theme.Mu2, wrap: true));
        }
        catch { host.Visibility = Visibility.Collapsed; }
    }
}
