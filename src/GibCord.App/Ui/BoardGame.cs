using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>
/// 6.2 Kalambury na tablicy: pasek gry (kto rysuje, hasło albo jego zarys, czas), panel z wynikami, zgadywaniem
/// i przyciskiem „✓ na głos” dla rysującego, karta z hasłem po turze i tabela wyników na koniec.
/// Stan gry trzyma serwer (board_game / board_word / board_guess) — tu tylko pamiętamy go i rysujemy.
/// </summary>
public sealed partial class BoardPanel
{
    sealed record KalLine(string Kind, long UserId, string? Text, int Points, bool Voice);
    /// <summary>Gra na kanale (także gdy tablica jest zamknięta — przycisk w rozmowie świeci).</summary>
    public static readonly Dictionary<long, KalGame> Games = [];
    static readonly Dictionary<long, string> Words = [];
    static readonly Dictionary<long, List<KalLine>> Feed = [];
    static long _skew;
    static readonly (string Key, string Name)[] Cats = [("wszystko", "Wszystko"), ("zwierzeta", "Zwierzęta"), ("jedzenie", "Jedzenie"), ("rzeczy", "Rzeczy"), ("miejsca", "Miejsca"), ("gry", "Gry"), ("wlasne", "Własne hasła")];

    readonly Border _gameBar = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(12), Padding = U.Th(12, 6, 8, 6), MinHeight = 44 };
    readonly Grid _side = new() { Width = 250, RowSpacing = 8, Visibility = Visibility.Collapsed };
    readonly StackPanel _scores = new() { Spacing = 2 };
    readonly StackPanel _feed = new() { Spacing = 3 };
    readonly ScrollViewer _feedScroll = new();
    readonly TextBox _guess = new() { PlaceholderText = "Wpisz hasło i Enter", MaxLength = 80 };
    readonly Grid _revealOver = new() { Visibility = Visibility.Collapsed, Background = U.B("#8c0e091e") };
    readonly Grid _endOver = new() { Visibility = Visibility.Collapsed, Background = U.B("#b30e091e"), CornerRadius = new CornerRadius(14) };
    readonly TextBlock _time = U.T("", 14, U.Bold);
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _kalTimer;
    bool _lockedDrawn;
    long _lockInfoAt;
    string? _barKey;

    KalGame? G => Games.GetValueOrDefault(_cid);
    bool KalLocked => G is { Phase: "draw" } g && g.Drawer != _sh.S.Me.Id;
    static int Left(KalGame g) => (int)Math.Max(0, Math.Ceiling((g.EndsAt - (Format.NowMs() + _skew)) / 1000.0));

    /// <summary>Z konstruktora: panel po prawej, nakładki, zegar.</summary>
    Grid KalBuild(FrameworkElement host)
    {
        _gameBar.Background = U.W(.05);
        _gameBar.BorderBrush = Theme.Line; _gameBar.BorderThickness = U.Th(1);
        var scoresBox = U.Box(U.V(4, U.T("WYNIKI", 10.5, U.Bold, Theme.Mu2), _scores), U.W(.05), 12, U.Th(8), Theme.Line, 1);
        _feedScroll.Content = _feed;
        var feedBox = U.Box(_feedScroll, U.W(.05), 12, U.Th(10, 8), Theme.Line, 1);
        _guess.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            var t = _guess.Text.Trim();
            if (t.Length == 0) return;
            _guess.Text = "";
            _ = _sh.C.SendLive(new { t = "board_game_guess", channelId = _cid, text = t });
        };
        var send = U.IconButton("", "Wyślij", () => { var t = _guess.Text.Trim(); if (t.Length == 0) return; _guess.Text = ""; _ = _sh.C.SendLive(new { t = "board_game_guess", channelId = _cid, text = t }); }, 14, 34);
        var form = new Grid { ColumnSpacing = 6 }.Cols("*", "auto").Add(_guess.At(0, 0), send.At(0, 1));
        _side.Rows("auto", "*", "auto");
        _side.Add(scoresBox.At(0), feedBox.At(1), form.At(2));
        _area.Children.Add(_revealOver);
        var row = new Grid { ColumnSpacing = 10 }.Cols("*", "auto");
        row.Add(host.At(0, 0), _side.At(0, 1), _endOver.At(0, 0, colSpan: 2));
        _kalTimer = DispatcherQueue.CreateTimer();
        _kalTimer.Interval = TimeSpan.FromMilliseconds(250);
        _kalTimer.Tick += (_, _) => { if (G is { } g && g.Phase != "end") KalBar(g); };
        _kalTimer.Start();
        Loaded += (_, _) => KalRender();
        return row;
    }

    Button KalButton()
    {
        var g = G;
        var on = g is not null && g.Phase != "end";
        var b = new Button { Content = U.H(6, U.I("", 14, on ? Theme.OnAccent : null), U.T("Kalambury", 12.5, U.Bold, on ? Theme.OnAccent : null)), Height = 34, Padding = U.Th(10, 0), CornerRadius = new CornerRadius(8), BorderThickness = U.Th(0), Background = on ? Theme.Accent : U.W(.06) };
        ToolTipService.SetToolTip(b, on ? "Kalambury trwają" : "Kalambury: jedna osoba rysuje, reszta zgaduje");
        b.Click += (_, _) =>
        {
            var gg = G;
            if (gg is null || gg.Phase == "end") { KalStart(); return; }
            var f = new MenuFlyout();
            var can = gg.Host == _sh.S.Me.Id || _sh.S.Can(_cid, Perm.VoiceModerate);
            if (can) f.Items.Add(M.Item("Zakończ grę", "", () => _ = _sh.C.SendLive(new { t = "board_game_stop", channelId = _cid }), danger: true));
            else f.Items.Add(new MenuFlyoutItem { Text = $"Grę może zakończyć {_sh.S.NameOf(gg.Host)} albo moderator", IsEnabled = false });
            f.ShowAt(b);
        };
        return b;
    }

    void KalLockTools()
    {
        var locked = KalLocked;
        _lockedDrawn = locked;
        foreach (var ch in _tools.Children.Skip(1))
        {
            if (ch is Control c) c.IsEnabled = !locked;
            else if (ch is FrameworkElement fe) { fe.IsHitTestVisible = !locked; fe.Opacity = locked ? .35 : 1; }
        }
    }

    /// <summary>Blokada rysowania dla zgadujących (z Down()).</summary>
    bool KalBlockDraw()
    {
        if (!KalLocked) return false;
        if (Environment.TickCount64 - _lockInfoAt > 4000) { _lockInfoAt = Environment.TickCount64; _sh.Info($"Teraz rysuje {_sh.S.NameOf(G!.Drawer, _sh.S.ServerOfChannel(_cid))} — Ty zgadujesz (wpisz hasło z prawej)"); }
        return true;
    }

    void KalRender()
    {
        var g = G;
        BuildTools(); // przycisk Kalambur i blokada narzędzi dla zgadujących
        if (g is null)
        {
            _gameBar.Visibility = _side.Visibility = _revealOver.Visibility = _endOver.Visibility = Visibility.Collapsed;
            return;
        }
        _gameBar.Visibility = _side.Visibility = Visibility.Visible;
        KalBar(g);
        KalScores(g);
        KalFeed();
        var me = _sh.S.Me.Id;
        _guess.IsEnabled = g.Phase == "draw";
        _guess.PlaceholderText = g.Phase != "draw" ? "Poczekaj na następną turę" : g.Drawer == me || g.Guessed.Contains(me) ? "Pisz do tych, co zgadli" : "Wpisz hasło i Enter";
        // karta z hasłem po turze / wyniki na koniec
        _revealOver.Children.Clear(); _endOver.Children.Clear();
        _revealOver.Visibility = g.Phase == "reveal" ? Visibility.Visible : Visibility.Collapsed;
        _endOver.Visibility = g.Phase == "end" ? Visibility.Visible : Visibility.Collapsed;
        var srv = _sh.S.ServerOfChannel(_cid);
        if (g.Phase == "reveal")
        {
            var who = g.Guessed.Select(u => _sh.S.NameOf(u, srv)).ToList();
            _revealOver.Children.Add(Card(U.V(4, U.T("To było", 12.5, null, Theme.Mu).Center(), U.T((g.Word ?? "").ToUpperInvariant(), 26, U.Bold, Theme.Accent).Center(),
                U.T(who.Count > 0 ? "Zgadli: " + string.Join(", ", who) : "Nikt nie zgadł", 12.5, null, Theme.Mu, wrap: true).Center())));
        }
        else if (g.Phase == "end")
        {
            var list = new StackPanel { Spacing = 4, MinWidth = 240 };
            string[] medal = ["🥇", "🥈", "🥉"];
            var i = 0;
            foreach (var s in g.Scores)
            {
                var r = new Grid { ColumnSpacing = 8, Padding = U.Th(8, 4) }.Cols("28", "auto", "*", "auto").Add(
                    U.T(i < 3 ? medal[i] : $"{i + 1}", 14).At(0, 0), new Avatar(22).Set(_sh.S, _sh.C.Api, s.UserId, srv).At(0, 1),
                    U.T(_sh.S.NameOf(s.UserId, srv), 14).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis).At(0, 2), U.T($"{s.Score}", 14, U.Bold).At(0, 3));
                list.Add(U.Box(r, U.W(.06), 10));
                i++;
            }
            var btns = U.H(8, U.Primary("Zagraj jeszcze raz", () => { _ = _sh.C.SendLive(new { t = "board_game_stop", channelId = _cid }); KalStart(); }, 36),
                U.Ghost("Zamknij wyniki", () => _ = _sh.C.SendLive(new { t = "board_game_stop", channelId = _cid }), 36)).With(p => p.HorizontalAlignment = HorizontalAlignment.Center);
            _endOver.Children.Add(Card(U.V(8, U.T("Koniec gry", 24, U.Bold).Center(), g.Reason is { } why ? U.T(why, 12.5, null, Theme.Mu).Center() : null,
                new ScrollViewer { Content = list, MaxHeight = 220 }, btns)));
        }
    }

    static Border Card(FrameworkElement child) => new()
    {
        Child = child, Background = U.B("#ff241a3d"), CornerRadius = new CornerRadius(18), Padding = U.Th(22, 16), BorderBrush = U.W(.14), BorderThickness = U.Th(1),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420,
    };

    void KalBar(KalGame g)
    {
        var me = _sh.S.Me.Id;
        var srv = _sh.S.ServerOfChannel(_cid);
        var left = Left(g);
        var word = Words.GetValueOrDefault(_cid);
        var key = $"{g.Phase}|{g.Turn}|{g.Drawer}|{g.Mask}|{word}|{g.Guessed.Count}|{g.SkipsLeft}|{(g.Phase == "draw" && g.Drawer == me && Format.NowMs() + _skew - g.StartedAt < 15000)}";
        _time.Text = g.Phase == "draw" ? $"{left}" : g.Phase == "reveal" ? $"za {left} s" : "";
        _time.Foreground = g.Phase == "draw" && left <= 10 ? Theme.Red : Theme.Fg;
        if (key == _barKey) return;
        _barKey = key;
        var rd = U.T($"runda {Math.Min(g.Round, g.Rounds)}/{g.Rounds}", 12, null, Theme.Mu2).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        if (_time.Parent is Border pb) pb.Child = null;
        _time.HorizontalAlignment = HorizontalAlignment.Center;
        var timeBox = U.Box(_time, U.W(.08), 14, U.Th(10, 3));
        FrameworkElement leftPart;
        if (g.Phase == "draw" && g.Drawer == me)
        {
            var canSkip = g.SkipsLeft > 0 && g.Guessed.Count == 0 && Format.NowMs() + _skew - g.StartedAt < 15000;
            leftPart = U.H(10, U.I("", 15, Theme.Accent), U.T("Rysujesz", 13.5, null, Theme.Mu), U.T((word ?? "…").ToUpperInvariant(), 18, U.Bold, Theme.Accent),
                canSkip ? U.Ghost($"Inne hasło ({g.SkipsLeft})", () => _ = _sh.C.SendLive(new { t = "board_game_skip", channelId = _cid }), 30) : null);
        }
        else if (g.Phase == "draw")
        {
            var mask = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var ch in g.Mask)
            {
                if (ch == '_') mask.Children.Add(new Border { Width = 14, Height = 3, CornerRadius = new CornerRadius(2), Background = U.W(.75), VerticalAlignment = VerticalAlignment.Bottom, Margin = U.Th(0, 0, 0, 3) });
                else if (ch == ' ') mask.Children.Add(new Border { Width = 8 });
                else mask.Children.Add(U.T(char.ToUpperInvariant(ch).ToString(), 17, U.Bold, Theme.Accent));
            }
            var letters = g.Mask.Count(c => c != ' ' && c != '-');
            leftPart = U.H(10, new Avatar(22).Set(_sh.S, _sh.C.Api, g.Drawer, srv), U.T($"Rysuje {_sh.S.NameOf(g.Drawer, srv)}", 13.5, U.Semi), mask, U.T($"{letters}", 12, null, Theme.Mu2),
                g.Guessed.Contains(me) ? U.T("✓ Zgadłeś(-aś)!", 13, U.Bold, Theme.Green) : null);
        }
        else if (g.Phase == "reveal")
            leftPart = U.H(10, U.T("To było:", 13.5, null, Theme.Mu), U.T((g.Word ?? "").ToUpperInvariant(), 18, U.Bold, Theme.Accent), U.T($"zgadło {g.Guessed.Count}", 12, null, Theme.Mu2));
        else
        {
            var win = g.Scores.FirstOrDefault();
            leftPart = U.H(8, U.T("🏁 Koniec gry", 14, U.Bold), win is { Score: > 0 } ? U.T($"wygrywa {_sh.S.NameOf(win.UserId, srv)}", 13.5, null, Theme.Mu) : null);
        }
        foreach (var e in ((StackPanel)leftPart).Children.OfType<FrameworkElement>()) e.VerticalAlignment = VerticalAlignment.Center;
        _gameBar.Child = new Grid { ColumnSpacing = 10 }.Cols("*", "auto", "auto").Add(leftPart.At(0, 0), rd.At(0, 1), (g.Phase == "end" ? (FrameworkElement)new Border() : timeBox).At(0, 2));
    }

    void KalScores(KalGame g)
    {
        var me = _sh.S.Me.Id;
        var srv = _sh.S.ServerOfChannel(_cid);
        var inRoom = (_sh.S.Voice.GetValueOrDefault(_cid) ?? []).Select(m => m.UserId).ToHashSet();
        var drawerMe = g.Phase == "draw" && g.Drawer == me;
        _scores.Children.Clear();
        var i = 0;
        foreach (var s in g.Scores)
        {
            var guessed = g.Guessed.Contains(s.UserId); var draws = g.Phase == "draw" && g.Drawer == s.UserId;
            var uid = s.UserId;
            var r = new Grid { ColumnSpacing = 6, Padding = U.Th(6, 3), Opacity = inRoom.Contains(uid) ? 1 : .45 }.Cols("16", "auto", "*", "auto", "auto").Add(
                U.T($"{++i}", 11.5, null, Theme.Mu2).With(t => t.VerticalAlignment = VerticalAlignment.Center).At(0, 0),
                new Avatar(20).Set(_sh.S, _sh.C.Api, uid, srv).At(0, 1),
                U.T(_sh.S.NameOf(uid, srv), 13).With(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.VerticalAlignment = VerticalAlignment.Center; }).At(0, 2),
                U.T($"{s.Score}", 13, U.Bold).With(t => t.VerticalAlignment = VerticalAlignment.Center).At(0, 4));
            if (draws) r.Add(U.I("", 12, Theme.Accent).At(0, 3));
            else if (guessed) r.Add(U.T("✓", 13, U.Bold, Theme.Green).At(0, 3));
            else if (drawerMe && inRoom.Contains(uid))
            {
                var aw = new HyperlinkButton { Content = U.T("✓ na głos", 11.5, U.Bold, Theme.Accent), Padding = U.Th(4, 0) };
                ToolTipService.SetToolTip(aw, $"{_sh.S.NameOf(uid, srv)} powiedział(a) hasło na głos");
                aw.Click += (_, _) => _ = _sh.C.SendLive(new { t = "board_game_award", channelId = _cid, userId = uid });
                r.Add(aw.At(0, 3));
            }
            _scores.Add(U.Box(r, draws ? U.Alpha("#ffb98f", .12) : guessed ? U.Alpha("#7fe0a8", .1) : null, 8));
        }
    }

    void KalFeed()
    {
        var srv = _sh.S.ServerOfChannel(_cid);
        _feed.Children.Clear();
        var lines = Feed.GetValueOrDefault(_cid) ?? [];
        if (lines.Count == 0) _feed.Add(U.T("Tu pojawią się próby zgadywania.", 12, null, Theme.Mu2, wrap: true));
        foreach (var l in lines)
        {
            var n = _sh.S.NameOf(l.UserId, srv);
            FrameworkElement t = l.Kind switch
            {
                "sys" => U.T(l.Text ?? "", 12, null, Theme.Mu2, wrap: true).Center(),
                "ok" => U.T($"✓ {n} {(l.Voice ? "zgaduje na głos" : "zgaduje")}! +{l.Points}", 13, U.Semi, Theme.Green, wrap: true),
                "close" => U.T($"{n}: {l.Text} — blisko!", 13, null, Theme.Yellow, wrap: true),
                "priv" => U.T($"{n}: {l.Text}", 13, null, Theme.Mu, wrap: true).With(x => x.FontStyle = Windows.UI.Text.FontStyle.Italic),
                _ => U.T($"{n}: {l.Text}", 13, null, null, wrap: true),
            };
            _feed.Add(t);
        }
        _feedScroll.UpdateLayout();
        _feedScroll.ChangeView(null, _feedScroll.ScrollableHeight, null, true);
    }

    async void KalStart()
    {
        var p = App.Prefs;
        var people = _sh.S.Voice.GetValueOrDefault(_cid)?.Count ?? 0;
        if (people < 2) { _sh.Info("Do Kalamburów potrzeba co najmniej 2 osób w rozmowie.", true); return; }
        var cat = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, n) in Cats) cat.Items.Add(n);
        cat.SelectedIndex = Math.Max(0, Array.FindIndex(Cats, c => c.Key == p.KalCategory));
        var rounds = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var n in new[] { "1 raz", "2 razy", "3 razy" }) rounds.Items.Add(n);
        rounds.SelectedIndex = Math.Clamp(p.KalRounds - 1, 0, 2);
        var secs = new DropField { HorizontalAlignment = HorizontalAlignment.Stretch };
        var secVals = new[] { 60, 80, 100 };
        foreach (var v in secVals) secs.Items.Add($"{v} s");
        secs.SelectedIndex = Math.Max(0, Array.IndexOf(secVals, p.KalSecs));
        var custom = new TextBox { PlaceholderText = "Gibek, nasz serwer, pizza z ananasem", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
        var customBox = U.V(4, U.T("Własne hasła serwera (po przecinku albo w osobnych liniach, co najmniej 5)", 12, U.Semi, Theme.Mu, wrap: true), custom);
        customBox.Visibility = Cats[cat.SelectedIndex].Key == "wlasne" ? Visibility.Visible : Visibility.Collapsed;
        cat.SelectionChanged += (_, _) => customBox.Visibility = cat.SelectedIndex >= 0 && Cats[cat.SelectedIndex].Key == "wlasne" ? Visibility.Visible : Visibility.Collapsed;
        _ = Task.Run(async () =>
        {
            try { var w = await _sh.C.Api.KalamburyWords(_cid); if (w.Custom.Count > 0) DispatcherQueue.TryEnqueue(() => { if (custom.Text.Length == 0) custom.Text = string.Join(", ", w.Custom); }); } catch { }
        });
        var two = new Grid { ColumnSpacing = 10 }.Cols("*", "*").Add(U.V(4, U.T("Ile razy rysuje każdy", 12, U.Semi, Theme.Mu), rounds).At(0, 0), U.V(4, U.T("Czas na rysunek", 12, U.Semi, Theme.Mu), secs).At(0, 1));
        var body = U.V(10, U.T($"Jedna osoba rysuje hasło, reszta zgaduje — wpisując albo na głos. Rysujący zmienia się co turę, a za szybkie zgadnięcie jest więcej punktów. Gra {people} {Format.Plural(people, "osoba", "osoby", "osób")}.", 13, null, Theme.Mu, wrap: true),
            U.V(4, U.T("Hasła", 12, U.Semi, Theme.Mu), cat), customBox, two).With(x => x.Width = 420);
        var d = new AppDialog { Title = "Kalambury", Content = body, PrimaryButtonText = "Zaczynamy", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = _sh.XamlRoot, RequestedTheme = ElementTheme.Dark };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var key = Cats[Math.Max(0, cat.SelectedIndex)].Key;
        p.KalCategory = key; p.KalRounds = rounds.SelectedIndex + 1; p.KalSecs = secVals[Math.Max(0, secs.SelectedIndex)]; p.Save();
        if (key == "wlasne")
        {
            var words = custom.Text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length >= 2).ToList();
            if (words.Count < 5) { _sh.Info("Wpisz co najmniej 5 haseł.", true); return; }
            await _sh.C.SendLive(new { t = "board_game_start", channelId = _cid, rounds = p.KalRounds, secs = p.KalSecs, category = key, custom = words });
        }
        else await _sh.C.SendLive(new { t = "board_game_start", channelId = _cid, rounds = p.KalRounds, secs = p.KalSecs, category = key });
    }

    /// <summary>Tryb pokazowy (zrzuty CI): kalambury — zgaduję; kalambury2 — rysuję; kalkoniec — wyniki.</summary>
    public static void DemoKal(long cid, string view, long me, long ola, long kuba, long maja)
    {
        var now = Format.NowMs();
        var drawerMe = view == "kalambury2";
        var end = view == "kalkoniec";
        var g = new KalGame
        {
            Phase = end ? "end" : "draw", Host = ola, Drawer = end ? 0 : drawerMe ? me : ola, Round = 1, Rounds = 2, Turn = 3, Secs = 80,
            StartedAt = now - (drawerMe ? 5000 : 38000), EndsAt = now + (drawerMe ? 75000 : 42000), Mask = drawerMe ? "_____" : "_a___", Category = "wszystko",
            Guessed = drawerMe ? [] : [kuba], Order = [me, ola, kuba, maja], SkipsLeft = 2, Now = now,
            Scores = end ? [new() { UserId = kuba, Score = 31 }, new() { UserId = me, Score = 24 }, new() { UserId = ola, Score = 19 }, new() { UserId = maja, Score = 8 }]
                : [new() { UserId = kuba, Score = 22 }, new() { UserId = ola, Score = 14 }, new() { UserId = me, Score = 10 }, new() { UserId = maja, Score = 6 }],
        };
        Games[cid] = g;
        if (drawerMe) Words[cid] = "zamek";
        Feed[cid] = drawerMe
            ? [new KalLine("sys", 0, "Twoja kolej — rysuj!", 0, false), new KalLine("guess", maja, "dom", 0, false), new KalLine("close", kuba, "zamak", 0, false)]
            : [new KalLine("sys", 0, "Rysuje Ola", 0, false), new KalLine("guess", maja, "dom", 0, false), new KalLine("guess", me, "wieża", 0, false), new KalLine("ok", kuba, null, 12, false), new KalLine("close", me, "zamak", 0, false)];
    }

    /// <summary>Tryb pokazowy: zamek narysowany na tablicy.</summary>
    public void DemoCastle(long drawer)
    {
        var demo = new object[]
        {
            new { id = "k0", userId = drawer, tool = "line", color = "#b8abc9", width = 2.5, points = new[] { .1, .85, .9, .85 } },
            new { id = "k1", userId = drawer, tool = "pen", color = "#ffb98f", width = 5.0, points = new[] { .25, .85, .25, .3, .3, .3, .3, .37, .35, .37, .35, .3, .4, .3, .4, .85 } },
            new { id = "k2", userId = drawer, tool = "pen", color = "#ffb98f", width = 5.0, points = new[] { .4, .55, .45, .55, .45, .5, .5, .5, .5, .55, .55, .55, .55, .5, .6, .5, .6, .55 } },
            new { id = "k3", userId = drawer, tool = "pen", color = "#ffb98f", width = 5.0, points = new[] { .6, .85, .6, .3, .65, .3, .65, .37, .7, .37, .7, .3, .75, .3, .75, .85 } },
            new { id = "k4", userId = drawer, tool = "pen", color = "#ffb98f", width = 5.0, points = new[] { .44, .85, .44, .7, .47, .64, .5, .62, .53, .64, .56, .7, .56, .85 } },
            new { id = "k5", userId = drawer, tool = "pen", color = "#e4668f", width = 4.0, points = new[] { .68, .3, .68, .14, .78, .18, .68, .22 } },
            new { id = "k6", userId = drawer, tool = "ellipse", color = "#ffd36e", width = 5.0, points = new[] { .82, .1, .9, .26 } },
        };
        foreach (var s in demo) _strokes.Add(JsonSerializer.SerializeToElement(s, Json.Options));
        Viewers[_cid] = [1, drawer];
        Loaded += (_, _) => { Redraw(); UpdateWho(); KalRender(); };
    }

    /// <summary>Po wyjściu z rozmowy: stan gier jest już nieaktualny (po powrocie serwer przyśle go od nowa).</summary>
    public static void ForgetGames() { Games.Clear(); Words.Clear(); Feed.Clear(); }

    static void FeedAdd(long cid, KalLine l)
    {
        if (!Feed.TryGetValue(cid, out var f)) Feed[cid] = f = [];
        f.Add(l);
        if (f.Count > 60) f.RemoveAt(0);
    }

    /// <summary>Zdarzenia gry (z OnEvent). true = obsłużone.</summary>
    static bool KalEvent(Shell sh, GatewayEvent ev, long cid)
    {
        var d = ev.Data;
        var me = sh.S.Me.Id;
        switch (ev.Type)
        {
            case "board_game":
                {
                    var prev = Games.GetValueOrDefault(cid);
                    var g = d.Prop("game").ValueKind == JsonValueKind.Object ? d.Prop("game").To<KalGame>() : null;
                    if (g is null) { Games.Remove(cid); Words.Remove(cid); }
                    else
                    {
                        if (g.Now > 0) _skew = g.Now - Format.NowMs();
                        Games[cid] = g;
                        var fresh = prev is null || prev.Phase == "end";
                        if (fresh && g.Phase != "end") Feed[cid] = [];
                        if (g.Phase == "draw" && (prev is null || prev.Turn != g.Turn))
                        {
                            if (g.Drawer != me) Words.Remove(cid);
                            FeedAdd(cid, new KalLine("sys", 0, g.Drawer == me ? "Twoja kolej — rysuj!" : $"Rysuje {sh.S.NameOf(g.Drawer, sh.S.ServerOfChannel(cid))}", 0, false));
                            if (g.Drawer == me && !fresh) Tones.Play("join");
                        }
                        if (g.Phase == "reveal" && prev?.Phase == "draw") FeedAdd(cid, new KalLine("sys", 0, $"To było: {g.Word}", 0, false));
                        // gra właśnie ruszyła i jestem w tej rozmowie → tablica otwiera się sama
                        if (fresh && g.Phase == "draw" && sh.C.VoiceChannelId == cid)
                        {
                            VoiceView.BoardOpenFor = cid;
                            sh.RefreshVoice();
                            Tones.Play("live");
                            if (g.Host != me) sh.Info($"{sh.S.NameOf(g.Host, sh.S.ServerOfChannel(cid))} zaczyna Kalambury!");
                        }
                    }
                    StatusChanged?.Invoke(cid);
                    break;
                }
            case "board_word":
                if (d.Str("word") is { } w) Words[cid] = w;
                break;
            case "board_guess":
                {
                    var uid = d.Long("userId") ?? 0;
                    var kind = d.Bool("correct") == true ? "ok" : d.Bool("close") == true ? "close" : d.Bool("private") == true ? "priv" : "guess";
                    FeedAdd(cid, new KalLine(kind, uid, d.Str("text"), (int)(d.Long("points") ?? 0), d.Bool("voice") == true));
                    if (kind == "ok") Tones.Play(uid == me ? "live" : "msg");
                    break;
                }
            default: return false;
        }
        if (Current is { } b && b._cid == cid) b.KalRender();
        return true;
    }
}

static class KalExt
{
    public static T Center<T>(this T e) where T : FrameworkElement { e.HorizontalAlignment = HorizontalAlignment.Center; if (e is TextBlock t) t.TextAlignment = TextAlignment.Center; return e; }
}
