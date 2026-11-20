using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

/// <summary>Menu „+” przy polu pisania (plik, ankieta, mini-gry) i wygląd mini-gier w czacie.</summary>
static class Fun
{
    static readonly string[] Die = ["", "⚀", "⚁", "⚂", "⚃", "⚄", "⚅"];
    static readonly (string Key, string Emoji, string Name)[] Rps = [("kamien", "✊", "kamień"), ("papier", "✋", "papier"), ("nozyce", "✌️", "nożyce")];

    public static MenuFlyout PlusMenu(ChatView v, Action pickFiles, bool canAttach)
    {
        var f = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
        if (canAttach) f.Items.Add(M.Item("Wyślij plik", "", pickFiles));
        if (!v.IsThread && v.S.Can(v.Cid, Perm.SendMessages))
        {
            f.Items.Add(M.Item("Ankieta", "", () => PollDialog(v)));
            f.Items.Add(new MenuFlyoutSeparator());
            f.Items.Add(M.Item("🎲  Rzuć kośćmi", "", () => DiceDialog(v)));
            f.Items.Add(M.Item("🪙  Rzut monetą", "", () => Create(v, new { kind = "coin" })));
            f.Items.Add(M.Item("🎯  Losowanie", "", () => PickDialog(v)));
            f.Items.Add(M.Item("⭕  Kółko i krzyżyk", "", () => Opponent(v, "ttt")));
            f.Items.Add(M.Item("✊  Kamień, papier, nożyce", "", () => Opponent(v, "rps")));
        }
        return f;
    }

    static async void Create(ChatView v, object body)
    {
        try { var m = await v.Sh.C.Api.CreateGame(v.Cid, body); v.S.Upsert(m, live: false); }
        catch (Exception ex) { v.Sh.Info(GibCordClient.Friendly(ex), true); }
    }

    static ContentDialog Dialog(ChatView v, string title, UIElement content, string ok) => new()
    {
        Title = title, Content = content, PrimaryButtonText = ok, CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary,
        XamlRoot = v.XamlRoot, RequestedTheme = ElementTheme.Dark,
    };

    static async void DiceDialog(ChatView v)
    {
        var count = new NumberBox { Header = "Ile kości", Minimum = 1, Maximum = 10, Value = 2, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var sides = new NumberBox { Header = "Ścianek", Minimum = 2, Maximum = 1000, Value = 6, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var quick = U.H(6);
        ContentDialog? d = null;
        foreach (var (c, s) in new[] { (1, 6), (2, 6), (3, 6), (1, 20), (1, 100) })
            quick.Add(U.Ghost($"{c}k{s}", () => { d?.Hide(); Create(v, new { kind = "dice", count = c, sides = s }); }, 30));
        d = Dialog(v, "🎲 Rzut kośćmi", U.V(12, quick, new Grid { ColumnSpacing = 10 }.Cols("*", "*").Add(count.At(0, 0), sides.At(0, 1))), "🎲 Rzuć");
        if (await d.ShowAsync() == ContentDialogResult.Primary)
            Create(v, new { kind = "dice", count = (int)(double.IsNaN(count.Value) ? 1 : count.Value), sides = (int)(double.IsNaN(sides.Value) ? 6 : sides.Value) });
    }

    static async void PickDialog(ChatView v)
    {
        var box = new TextBox { PlaceholderText = "pizza, kebab, sushi\nalbo każda opcja w osobnej linii", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
        var d = Dialog(v, "🎯 Losowanie", U.V(8, U.T("Z czego losujemy?", 13, U.Semi, Theme.Mu), box), "🎯 Losuj");
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var opts = box.Text.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20).ToList();
        if (opts.Count < 2) { v.Sh.Info("Podaj co najmniej 2 opcje", true); return; }
        Create(v, new { kind = "pick", options = opts });
    }

    static async void Opponent(ChatView v, string kind)
    {
        var s = v.S;
        IEnumerable<long> ids = v.DmObj is { } dm ? dm.UserIds : v.Srv?.Members.Select(m => m.UserId) ?? [];
        var people = ids.Where(id => id != s.Me.Id && s.U(id) is not null).OrderBy(id => s.StatusOf(id) == "offline").ThenBy(id => s.NameOf(id, v.Srv)).ToList();
        if (people.Count == 0) { v.Sh.Info("Nie ma tu nikogo do gry", true); return; }
        if (people.Count == 1) { Create(v, new { kind, opponentId = people[0] }); return; }
        var search = new TextBox { PlaceholderText = "Szukaj osoby" };
        var list = new StackPanel { Spacing = 2 };
        ContentDialog? d = null;
        void Draw()
        {
            list.Children.Clear();
            var q = EmojiData.Fold(search.Text.Trim());
            foreach (var id in people.Where(id => q.Length == 0 || EmojiData.Fold(s.NameOf(id, v.Srv)).Contains(q) || EmojiData.Fold(s.U(id)!.Username).Contains(q)).Take(30))
            {
                var uid = id;
                list.Add(U.Row(U.H(10, new Avatar(28, true).Set(s, v.Sh.C.Api, id, v.Srv, true), U.T(s.NameOf(id, v.Srv), 13.5, U.Semi)), () => { d?.Hide(); Create(v, new { kind, opponentId = uid }); }, false, 8, U.Th(8, 4), U.Th(0, 1)));
            }
        }
        search.TextChanged += (_, _) => Draw();
        Draw();
        d = new ContentDialog
        {
            Title = kind == "ttt" ? "⭕ Kółko i krzyżyk — z kim?" : "✊ Kamień, papier, nożyce — z kim?",
            Content = U.V(8, search, new ScrollViewer { Content = list, MaxHeight = 320 }), CloseButtonText = "Anuluj", XamlRoot = v.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        await d.ShowAsync();
    }

    static async void PollDialog(ChatView v)
    {
        var q = new TextBox { PlaceholderText = "np. Na co idziemy w sobotę?", MaxLength = 300 };
        var opts = new StackPanel { Spacing = 6 };
        void AddOpt() { if (opts.Children.Count < 10) opts.Children.Add(new TextBox { PlaceholderText = $"Odpowiedź {opts.Children.Count + 1}", MaxLength = 100 }); }
        AddOpt(); AddOpt();
        var add = U.Ghost("+ Dodaj odpowiedź", AddOpt, 30);
        var hours = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var choices = (v.Sh.C.Info?.PollHours is { Length: > 0 } ph ? ph : [0, 1, 4, 8, 24, 72, 168]).ToList();
        foreach (var h in choices) hours.Items.Add(h switch { 0 => "Bez limitu czasu", 1 => "1 godzina", < 24 => $"{h} {Format.Plural(h, "godzina", "godziny", "godzin")}", 24 => "1 dzień", 168 => "1 tydzień", _ => $"{h / 24} {Format.Plural(h / 24, "dzień", "dni", "dni")}" });
        hours.SelectedIndex = Math.Max(0, choices.IndexOf(24));
        var multi = new CheckBox { Content = "Można zaznaczyć kilka odpowiedzi" };
        var d = Dialog(v, "📊 Nowa ankieta", new ScrollViewer { MaxHeight = 480, Content = U.V(10, U.T("Pytanie", 12.5, U.Semi, Theme.Mu), q, U.T("Odpowiedzi", 12.5, U.Semi, Theme.Mu), opts, add,
            U.T("Czas trwania", 12.5, U.Semi, Theme.Mu), hours, multi) }, "Wyślij ankietę");
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var answers = opts.Children.OfType<TextBox>().Select(t => t.Text.Trim()).Where(t => t.Length > 0).ToList();
        if (q.Text.Trim().Length == 0 || answers.Count < 2) { v.Sh.Info("Ankieta potrzebuje pytania i co najmniej dwóch odpowiedzi", true); return; }
        try { var m = await v.Sh.C.Api.SendPoll(v.Cid, q.Text.Trim(), answers, multi.IsChecked == true, choices[Math.Max(0, hours.SelectedIndex)]); v.S.Upsert(m, live: false); }
        catch (Exception ex) { v.Sh.Info(GibCordClient.Friendly(ex), true); }
    }

    // ───────────────────────── mini-gry w czacie ─────────────────────────

    public static FrameworkElement GameView(ChatView v, Message m)
    {
        var g = m.Game;
        var s = v.S;
        var me = s.Me.Id;
        string Name(long id) => s.NameOf(id, v.Srv);
        List<long> Players() => g.Prop("players").ValueKind == JsonValueKind.Array ? g.Prop("players").EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
        long? WinnerId() => g.Prop("winner").ValueKind == JsonValueKind.Number ? g.Prop("winner").GetInt64() : null;
        bool Draw() => g.Str("winner") == "draw";
        FrameworkElement Box(UIElement c) => U.Box(c, Theme.Card, 12, U.Th(14, 12), Theme.Line, 1).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = U.Th(0, 4, 0, 2); });
        TextBlock Title(string t) => U.T(t, 12, U.Bold, Theme.Mu);

        switch (g.Str("kind"))
        {
            case "dice":
            {
                var sides = g.Long("sides") ?? 6;
                var rolls = g.Prop("rolls").ValueKind == JsonValueKind.Array ? g.Prop("rolls").EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
                var faces = U.H(6);
                foreach (var r in rolls) faces.Add(sides == 6 && r is >= 1 and <= 6 ? U.T(Die[r], 34) : U.Box(U.T(r.ToString(), 18, U.Bold), U.W(.1), 8, U.Th(10, 4)));
                return Box(U.V(6, Title($"🎲 Rzut {g.Long("count") ?? rolls.Count}k{sides}"), faces,
                    U.T(rolls.Count > 1 ? $"Suma: {g.Long("total")}" : $"Wypadło {rolls.FirstOrDefault()}", 14, U.Bold)));
            }
            case "coin":
            {
                var orzel = g.Str("result") == "orzeł";
                return Box(U.H(12, U.T(orzel ? "🦅" : "🪙", 34), U.V(2, Title("Rzut monetą"), U.T(orzel ? "Orzeł!" : "Reszka!", 16, U.Bold))));
            }
            case "pick":
            {
                var res = g.Str("result") ?? "";
                var all = new WrapPanel { Spacing = 6 };
                if (g.Prop("options").ValueKind == JsonValueKind.Array)
                    foreach (var o in g.Prop("options").EnumerateArray().Select(x => x.GetString() ?? ""))
                        all.Children.Add(U.Box(U.T(o, 12, o == res ? U.Bold : null, o == res ? Theme.OnAccent : Theme.Fg), o == res ? Theme.Accent : U.W(.08), 8, U.Th(8, 3)));
                return Box(U.V(8, Title("🎯 Losowanie"), U.T(res, 17, U.Bold, wrap: true), all).With(p => p.MaxWidth = 420));
            }
            case "ttt":
            {
                var pl = Players();
                var board = g.Str("board") ?? ".........";
                var turn = g.Long("turn");
                var winner = WinnerId();
                var over = winner is not null || Draw();
                var line = g.Prop("line").ValueKind == JsonValueKind.Array ? g.Prop("line").EnumerateArray().Select(x => (int)x.GetInt64()).ToHashSet() : [];
                var myTurn = !over && turn == me;
                var grid = new Grid { RowSpacing = 4, ColumnSpacing = 4 }.Rows("44", "44", "44").Cols("44", "44", "44");
                for (var i = 0; i < 9 && i < board.Length; i++)
                {
                    var c = board[i];
                    var cell = i;
                    var b = new Button
                    {
                        Content = U.T(c == 'X' ? "✕" : c == 'O' ? "○" : "", 20, U.Bold, c == 'X' ? Theme.Accent : Theme.Teal),
                        Width = 44, Height = 44, Padding = U.Th(0), CornerRadius = new CornerRadius(8), BorderThickness = U.Th(0),
                        Background = line.Contains(i) ? U.Alpha("#6fe3a5", .3) : U.W(myTurn && c == '.' ? .12 : .06), IsEnabled = myTurn && c == '.',
                    };
                    b.Click += async (_, _) => await Move(v, m, new { cell });
                    grid.Add(b.At(i / 3, i % 3));
                }
                var status = Draw() ? "Remis!" : winner is long w ? $"Wygrywa {Name(w)}!" : myTurn ? "Twój ruch" : turn is long t ? $"Ruch: {Name(t)}" : "";
                var side = U.V(4, Title("Kółko i krzyżyk"), U.T(status, 15, U.Bold, myTurn ? Theme.Accent : Theme.Fg),
                    pl.Count == 2 ? U.T($"{Name(pl[0])} ✕ · {Name(pl[1])} ○", 12, null, Theme.Mu, wrap: true) : null,
                    over && pl.Contains(me) ? Rematch(v, "ttt", pl.First(x => x != me)) : null);
                side.MaxWidth = 220;
                return Box(U.H(16, grid, side));
            }
            case "rps":
            {
                var pl = Players();
                var i = pl.IndexOf(me);
                var done = WinnerId() is not null || Draw();
                var chosen = g.Prop("chosen").ValueKind == JsonValueKind.Array ? g.Prop("chosen").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.True).ToList() : [false, false];
                var choices = g.Prop("choices").ValueKind == JsonValueKind.Array ? g.Prop("choices").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList() : [null, null];
                FrameworkElement Side(int k)
                {
                    var ch = k < choices.Count ? choices[k] : null;
                    var r = Rps.FirstOrDefault(x => x.Key == ch);
                    FrameworkElement pick = done && ch is not null ? U.V(0, U.T(r.Emoji, 30), U.T(r.Name, 11.5, null, Theme.Mu)) : U.T(k < chosen.Count && chosen[k] ? "✓" : "…", 22, U.Bold, Theme.Mu);
                    return U.V(4, U.T(k < pl.Count ? Name(pl[k]) : "?", 12.5, U.Bold), pick).With(p => { p.HorizontalAlignment = HorizontalAlignment.Center; p.MinWidth = 90; });
                }
                var status = Draw() ? "Remis!" : WinnerId() is long w ? $"Wygrywa {Name(w)}!" : i >= 0 && i < chosen.Count && !chosen[i] ? "Wybierz w tajemnicy:" : "Czekamy na wybór…";
                var panel = U.V(10, Title("Kamień, papier, nożyce"), U.T(status, 15, U.Bold), U.H(14, Side(0), U.T("vs", 13, U.Bold, Theme.Mu2), Side(1)));
                if (i >= 0 && !done && i < chosen.Count && !chosen[i])
                {
                    var btns = U.H(8);
                    foreach (var (key, emo, name) in Rps)
                    {
                        var b = new Button { Content = U.T(emo, 22), Width = 52, Height = 46, CornerRadius = new CornerRadius(10), Background = U.W(.1), BorderThickness = U.Th(0) };
                        b.Tip(name);
                        var choice = key;
                        b.Click += async (_, _) => await Move(v, m, new { choice });
                        btns.Add(b);
                    }
                    panel.Add(btns);
                }
                if (done && i >= 0) panel.Add(Rematch(v, "rps", pl.First(x => x != me)));
                return Box(panel);
            }
        }
        return Box(U.T("🎮 Mini-gra", 14, U.Semi));
    }

    static FrameworkElement Rematch(ChatView v, string kind, long opp)
    {
        var b = new HyperlinkButton { Content = U.T("Rewanż", 12.5, U.Bold, Theme.Accent), Padding = U.Th(0) };
        b.Click += (_, _) => Create(v, new { kind, opponentId = opp });
        return b;
    }

    static async Task Move(ChatView v, Message m, object body)
    {
        try { var r = await v.Sh.C.Api.GameMove(m.Id, body); v.S.Upsert(r, live: false); }
        catch (Exception ex) { v.Sh.Info(GibCordClient.Friendly(ex), true); }
    }
}
