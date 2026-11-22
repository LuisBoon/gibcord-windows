using System.Globalization;
using GibCord.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>
/// 6.1 „Twój GibCord 2026” — podsumowanie roku jako opowieść ze slajdami (klik z prawej = dalej, z lewej = wstecz, strzałki, Esc).
/// Slajdy bez danych są pomijane (np. gry — serwer liczy je dopiero od 6.1). Na końcu „Wyślij na kanał” (karta bez osób).
/// </summary>
static class WrappedView
{
    static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");
    /// <summary>Tryb pokazowy: gotowe dane i numer slajdu na start (zrzuty CI).</summary>
    public static Wrapped? DemoData;
    public static int DemoSlide;

    public static int Year(Shell sh) => sh.C.Info?.Wrapped?.Year ?? DateTime.Now.Year;
    /// <summary>Czy pokazywać wejścia (baner, menu): od 1 grudnia dla wszystkich, admin widzi wcześniej jako podgląd.</summary>
    public static bool Available(Shell sh) => DemoData is not null || sh.C.Info?.Wrapped is { } w && (w.Open || sh.S.Me.IsAdmin);

    /// <summary>Baner w widoku startowym: „Twój GibCord ROK jest gotowy” (krzyżyk chowa go na ten rok).</summary>
    public static FrameworkElement Banner(Shell sh, Action closed)
    {
        var year = Year(sh);
        var x = U.IconButton("\uE711", "Schowaj", () => { App.Prefs.WrappedBannerClosed = year; App.Prefs.Save(); closed(); }, 12, 28, U.W(.85));
        var preview = sh.C.Info?.Wrapped is { Open: false };
        var body = new Grid { ColumnSpacing = 14 }.Cols("auto", "*", "auto", "auto").Add(
            new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = 46, Height = 46 }.At(0, 0),
            U.V(2, U.T($"Twój GibCord {year} jest gotowy", 16, U.Bold, U.W(1)), U.T(preview ? "Podgląd dla admina — dla wszystkich od 1 grudnia." : "Wiadomości, głos, gry i emoji z całego roku w kilku slajdach.", 12.5, null, U.W(.85), wrap: true)).With(p => p.VerticalAlignment = VerticalAlignment.Center).At(0, 1),
            new Button { Content = U.T("Zobacz", 13, U.Bold, U.B("#2a1730")), Background = U.B("#ffe3d1"), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(10), Padding = U.Th(16, 7), VerticalAlignment = VerticalAlignment.Center }
                .With(b => b.Click += (_, _) => Open(sh)).At(0, 2),
            x.With(b => b.VerticalAlignment = VerticalAlignment.Top).At(0, 3));
        var card = new Border
        {
            Child = body, Padding = U.Th(16, 12), CornerRadius = new CornerRadius(16), Margin = U.Th(0, 0, 0, 12),
            Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = U.C("#e4668f"), Offset = 0 }, new GradientStop { Color = U.C("#f4927f"), Offset = .55 }, new GradientStop { Color = U.C("#ffc49e"), Offset = 1 } } },
        };
        card.Tapped += (_, e) => { if (e.OriginalSource is DependencyObject o && !Story.InButton(o)) Open(sh); };
        return card;
    }

    public static async void Open(Shell sh)
    {
        var year = Year(sh);
        Wrapped? w = DemoData;
        if (w is null)
        {
            if (sh.C.Info?.Wrapped is { Open: false } && !sh.S.Me.IsAdmin) { sh.Info("Podsumowanie roku otworzy się 1 grudnia."); return; }
            try { w = await sh.C.Api.GetWrapped(year); }
            catch (Exception ex) { sh.Info(ex is ApiException ae ? ae.Message : "Nie udało się wczytać podsumowania — spróbuj za chwilę.", true); return; }
        }
        var slides = Slides(sh, w);
        var host = new Story(sh, w, slides, DemoData is not null ? Math.Clamp(DemoSlide, 0, slides.Count - 1) : 0);
        sh.ShowOverlay(host, () => host.Focus(FocusState.Programmatic), pop: true);
    }

    // ── liczby i słowa po polsku ──
    static string N(long n) => n.ToString("N0", Pl);
    static string Hours(long min) => min < 60 ? $"{min} min" : min < 600 ? (min / 60.0).ToString("0.#", Pl) + " h" : N(min / 60) + " h";
    /// <summary>1 wiadomość, 2 wiadomości, 5 wiadomości.</summary>
    static string Pl3(long n, string one, string few, string many)
    {
        var a = Math.Abs(n);
        if (a == 1) return one;
        var d = a % 10; var dd = a % 100;
        return d is >= 2 and <= 4 && dd is < 12 or > 14 ? few : many;
    }
    static string Day(string iso) => DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("d MMMM", Pl) : iso;

    // każdy slajd ma swój gradient (barwy GibCorda: turkus, śliwka, koral, róż, brzoskwinia)
    static readonly (string A, string B)[] Grad =
    [
        ("#1f7680", "#3b2059"), ("#e4668f", "#f4927f"), ("#3b2059", "#191330"), ("#2b6f6a", "#1f3b52"), ("#f4927f", "#ffc49e"),
        ("#5a3b8c", "#e4668f"), ("#174a4f", "#3b2059"), ("#ff9e7a", "#e4668f"), ("#2a2147", "#5a3b8c"), ("#e4668f", "#3b2059"), ("#1f7680", "#f4927f"),
    ];

    sealed record Slide(string Key, Func<FrameworkElement> Build, bool Light = false);

    static List<Slide> Slides(Shell sh, Wrapped w)
    {
        var l = new List<Slide>();
        var m = w.Messages;
        l.Add(new("intro", () => Col(
            Splash.Gibek(120), // zimą sam założy czapkę
            Kicker(w.Preview ? $"Podgląd · GibCord {w.Year}" : $"GibCord {w.Year}"),
            Big("Twój rok", 46),
            Text($"{w.User.Name}, oto co działo się u Ciebie od 14 listopada. Klikaj, żeby iść dalej."))));
        if (m.Total > 0)
            l.Add(new("messages", () => Col(
                Kicker("Wiadomości"),
                Big(N(m.Total), 64),
                Text($"{Pl3(m.Total, "wiadomość", "wiadomości", "wiadomości")} w {N(m.Days)} {Pl3(m.Days, "dzień", "dni", "dni")}."),
                m.BusiestDay is { } bd ? Text($"Najgłośniej było {Day(bd.Date)}: {N(bd.Count)} {Pl3(bd.Count, "wiadomość", "wiadomości", "wiadomości")} jednego dnia.") : null,
                m.Chars >= 1800 ? Text($"Razem {N(m.Chars)} znaków — mniej więcej {N(Math.Max(1, m.Chars / 1800))} {Pl3(m.Chars / 1800, "strona", "strony", "stron")} książki.", true) : null), true));
        if (m.Total > 0 && m.Hours.Count == 24)
            l.Add(new("hours", () => Col(
                Kicker("Pora dnia"),
                Big($"{m.TopHour}:00", 56),
                Text("O tej godzinie piszesz najczęściej."),
                HourChart(m.Hours, m.TopHour),
                m.Night > 0 ? Text($"Po północy (0–5): {N(m.Night)} {Pl3(m.Night, "wiadomość", "wiadomości", "wiadomości")}.", true) : null)));
        if (m.TopChannels.Count > 0)
            l.Add(new("channels", () => Col(
                Kicker("Ulubione kanały"),
                Big(m.TopChannels[0].Label, 40),
                Text(m.TopChannels[0].Server is { } s0 ? $"na serwerze {s0}" : ""),
                List(m.TopChannels.Select((c, i) => Line(i + 1, ServerIcon(sh, c), c.Label, c.Server, N(c.Count))).ToArray()))));
        var people = w.TopPeople.Select(p => (p.UserId, Val: N(p.Count) + " wiad.")).ToList();
        var vpeople = w.Voice.TopPeople.Select(p => (p.UserId, Val: Hours(p.Minutes))).ToList();
        if (people.Count > 0 || vpeople.Count > 0)
            l.Add(new("people", () => Col(
                Kicker("Twoi ludzie"),
                Big(Name(sh, w, (people.Count > 0 ? people : vpeople)[0].UserId), 40),
                Text(people.Count > 0 ? "Z tą osobą najwięcej piszesz w rozmowach prywatnych." : "Z tą osobą najdłużej rozmawiasz na głosowym."),
                people.Count > 0 ? List([Sub("Rozmowy prywatne"), .. people.Select((p, i) => Line(i + 1, Face(sh, w, p.UserId), Name(sh, w, p.UserId), null, p.Val))]) : null,
                vpeople.Count > 0 ? List([Sub("Na głosowym"), .. vpeople.Select((p, i) => Line(i + 1, Face(sh, w, p.UserId), Name(sh, w, p.UserId), null, p.Val))]) : null,
                Text("Ten slajd widzisz tylko Ty — nie trafia do udostępnianej karty.", true))));
        if (w.Voice.Minutes > 0)
            l.Add(new("voice", () => Col(
                Kicker("Na głosowym"),
                Big(Hours(w.Voice.Minutes), 64),
                Text("w rozmowach głosowych z innymi."),
                w.Voice.TopChannel is { } tc ? Text($"Najczęściej: {tc.Label}{(tc.Server is { } ts ? " · " + ts : "")} ({Hours(tc.Minutes)}).") : null,
                w.Voice.StreamMinutes > 0 ? Text($"Do tego {Hours(w.Voice.StreamMinutes)} transmisji ekranu dla innych.", true) : null), true));
        if (w.Games.Count > 0)
            l.Add(new("games", () => Col(
                Kicker("Gry"),
                Big(w.Games[0].Name, 40),
                Text($"{Hours(w.Games[0].Minutes)} — to Twoja gra roku."),
                List(w.Games.Select((g, i) => Bar(i + 1, g.Name, Hours(g.Minutes), g.Minutes / (double)Math.Max(1, w.Games[0].Minutes))).ToArray()),
                Text("GibCord liczy gry od wersji 6.1, więc to dopiero początek.", true))));
        if (w.Emoji.Count > 0)
            l.Add(new("emoji", () => Col(
                Kicker("Emoji roku"),
                EmojiBig(sh, w.Emoji[0].Emoji),
                Text($"{N(w.Emoji[0].Count)} {Pl3(w.Emoji[0].Count, "raz", "razy", "razy")} — w wiadomościach i reakcjach."),
                w.Emoji.Count > 1 ? Row(w.Emoji.Skip(1).Select(e => (UIElement)U.V(2, EmojiSmall(sh, e.Emoji), U.T(N(e.Count), 12, U.Semi, U.W(.75)).With(t => t.HorizontalAlignment = HorizontalAlignment.Center))).ToArray()) : null,
                Text($"Reakcje: {N(w.Reactions.Given)} od Ciebie, {N(w.Reactions.Received)} pod Twoimi wiadomościami.", true)), true));
        var md = w.Media;
        if (md.Photos + md.Gifs + md.Stickers + md.Polls + md.VoiceNotes + md.Videos + m.MiniGames > 0)
            l.Add(new("media", () => Col(
                Kicker("Nie tylko tekst"),
                Big(N(md.Photos + md.Videos + md.Gifs + md.Stickers + md.VoiceNotes), 64),
                Text("zdjęć, filmów, GIF-ów, naklejek i wiadomości głosowych."),
                Tiles(("Zdjęcia", md.Photos), ("Filmy", md.Videos), ("GIF-y", md.Gifs), ("Naklejki", md.Stickers), ("Głosówki", md.VoiceNotes), ("Ankiety", md.Polls), ("Mini-gry", m.MiniGames)))));
        l.Add(new("persona", () => Col(
            Kicker("Twój styl"),
            Big(w.Persona.Title, 48),
            Text(w.Persona.Text),
            w.Everyone.Rank is int r && w.Everyone.Writers > 1 ? Text($"Pod względem liczby wiadomości: {r}. miejsce na {N(w.Everyone.Writers)} piszących.") : null,
            w.Level is { Level: > 0 } lv ? Text($"Najwyższy poziom: {lv.Level}{(lv.Server is { } ls ? " na " + ls : "")}.", true) : null), true));
        l.Add(new("everyone", () => Col(
            Kicker("Cały GibCord"),
            Big(N(w.Everyone.Messages), 56),
            Text($"wiadomości od wszystkich i {Hours(w.Everyone.VoiceMinutes)} na głosowych."),
            Text($"{N(w.Everyone.People)} {Pl3(w.Everyone.People, "osoba", "osoby", "osób")} na GibCordzie, {N(w.Servers.Count)} {Pl3(w.Servers.Count, "Twój serwer", "Twoje serwery", "Twoich serwerów")}, {N(w.Friends.Count)} {Pl3(w.Friends.Count, "znajomy", "znajomych", "znajomych")}.", true))));
        l.Add(new("end", () => Col(
            Kicker($"Do zobaczenia w {w.Year + 1}"),
            Big("Dzięki za ten rok!", 44),
            Text("Pochwal się na kanale — karta pokaże Twoje liczby, ale bez osób, z którymi piszesz."),
            U.H(10,
                U.Primary("Wyślij na kanał", () => _ = Share(sh, w)),
                U.Ghost("Zamknij", () => sh.CloseOverlay())).With(p => p.Margin = U.Th(0, 8, 0, 0))), true));
        return l;
    }

    // ── klocki slajdów ──
    static StackPanel Col(params UIElement?[] els) => U.V(14, els).With(p => { p.VerticalAlignment = VerticalAlignment.Center; });
    static TextBlock Kicker(string t) => U.T(t.ToUpper(Pl), 12, U.Bold, U.W(.78)).With(x => x.CharacterSpacing = 140);
    static TextBlock Big(string t, double size) => U.T(t, size, U.Bold, U.W(1), wrap: true).With(x => { x.LineHeight = size * 1.05; x.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; x.TextTrimming = TextTrimming.CharacterEllipsis; x.MaxLines = 3; });
    static TextBlock Text(string t, bool dim = false) => U.T(t, dim ? 13 : 16, dim ? null : U.Semi, U.W(dim ? .72 : .95), wrap: true);
    static TextBlock Sub(string t) => U.T(t, 12, U.Bold, U.W(.7));
    static StackPanel List(params UIElement?[] rows) => U.V(8, rows).With(p => p.Margin = U.Th(0, 6, 0, 0));
    static StackPanel Row(params UIElement[] els) => U.H(14, els);

    static Grid Line(int n, FrameworkElement? icon, string name, string? sub, string val) =>
        new Grid().Cols("22", "auto", "*", "auto").Add(
            U.T(n.ToString(), 13, U.Bold, U.W(.6)).With(t => t.VerticalAlignment = VerticalAlignment.Center).At(0, 0),
            icon?.With(i => { i.Margin = U.Th(0, 0, 10, 0); i.VerticalAlignment = VerticalAlignment.Center; }).At(0, 1),
            U.V(0, U.T(name, 15, U.Bold, U.W(1)).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis), sub is null ? null : U.T(sub, 11.5, null, U.W(.65)).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis)).With(p => p.VerticalAlignment = VerticalAlignment.Center).At(0, 2),
            U.T(val, 14, U.Bold, U.W(.9)).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = U.Th(10, 0, 0, 0); }).At(0, 3));

    static Grid Bar(int n, string name, string val, double frac)
    {
        var track = new Grid { Height = 8, Margin = U.Th(0, 4, 0, 0) };
        track.Add(new Border { Background = U.W(.16), CornerRadius = new CornerRadius(4) });
        var fill = new Border { Background = U.W(.9), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
        track.Add(fill);
        track.SizeChanged += (_, e) => fill.Width = Math.Max(8, e.NewSize.Width * Math.Clamp(frac, 0, 1));
        return new Grid().Cols("22", "*", "auto").Add(
            U.T(n.ToString(), 13, U.Bold, U.W(.6)).At(0, 0),
            U.V(0, U.T(name, 14.5, U.Bold, U.W(1)).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis), track).At(0, 1),
            U.T(val, 13.5, U.Bold, U.W(.9)).With(t => t.Margin = U.Th(10, 0, 0, 0)).At(0, 2));
    }

    static FrameworkElement HourChart(List<long> hours, int top)
    {
        var max = Math.Max(1, hours.Max());
        var g = new Grid { Height = 120, Margin = U.Th(0, 6, 0, 0) };
        for (var i = 0; i < 24; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 24; i++)
        {
            var h = Math.Max(3, 96 * hours[i] / (double)max);
            var bar = new Border { Height = h, VerticalAlignment = VerticalAlignment.Bottom, Margin = U.Th(1.5, 0, 1.5, 18), CornerRadius = new CornerRadius(3, 3, 1, 1), Background = i == top ? U.B("#ffd9c2") : U.W(hours[i] > 0 ? .45 : .14) };
            g.Add(bar.At(0, i));
            if (i % 6 == 0) g.Add(U.T(i.ToString(), 10.5, U.Semi, U.W(.6)).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; }).At(0, i));
        }
        return g;
    }

    static WrapPanel Tiles(params (string Label, long N)[] items)
    {
        var p = new WrapPanel { Spacing = 8, Margin = U.Th(0, 6, 0, 0) };
        foreach (var (label, n) in items.Where(x => x.N > 0))
            p.Children.Add(U.Box(U.V(2, U.T(N(n), 20, U.Bold, U.W(1)), U.T(label, 11.5, U.Semi, U.W(.75))), U.W(.14), 12, U.Th(12, 8)).With(b => b.MinWidth = 96));
        return p;
    }

    static FrameworkElement? ServerIcon(Shell sh, WChannel c)
    {
        if (c.ServerId is long sid && sh.S.ServerById(sid) is { } srv) return new Avatar(30).SetRaw(srv.Name, "#5a3b8c", sh.C.Api.Abs(srv.Icon), null);
        return U.Box(U.T("#", 15, U.Bold, U.W(.9)).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }), U.W(.16), 9).With(b => { b.Width = 30; b.Height = 30; });
    }
    static string Name(Shell sh, Wrapped w, long uid) => w.UserOf(uid)?.Name is { Length: > 0 } n ? n : sh.S.U(uid)?.Name ?? "Ktoś";
    static FrameworkElement Face(Shell sh, Wrapped w, long uid)
    {
        if (sh.S.U(uid) is null && w.UserOf(uid) is { } u) return new Avatar(30).SetRaw(u.Name, Store.ColorOfUser(u), sh.C.Api.Abs(u.Avatar), null);
        return new Avatar(30).Set(sh.S, sh.C.Api, uid);
    }
    static FrameworkElement EmojiBig(Shell sh, string e) => Emoji(sh, e, 88);
    static FrameworkElement EmojiSmall(Shell sh, string e) => Emoji(sh, e, 34);
    static FrameworkElement Emoji(Shell sh, string e, double size)
    {
        var m = System.Text.RegularExpressions.Regex.Match(e, @"^<a?:([a-z0-9_]{2,32}):([0-9]+)>$");
        if (m.Success && long.TryParse(m.Groups[2].Value, out var id) && sh.S.EmojiById(id) is { } em)
            return new Image { Source = Images.Get(sh.C.Api.Abs(em.Url), (int)size * 2), Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
        return U.T(m.Success ? ":" + m.Groups[1].Value + ":" : e, m.Success ? size / 2.5 : size, null, U.W(1)).With(t => t.FontFamily = new FontFamily("Segoe UI Emoji"));
    }

    // ── udostępnianie ──
    static async Task Share(Shell sh, Wrapped w)
    {
        if (DemoData is not null) { sh.Info("Tryb pokazowy — nic nie wysłano."); return; }
        var target = await PickChannel(sh);
        if (target is null) return;
        try
        {
            await sh.C.Api.ShareWrapped(w.Year, target.Value.Id);
            sh.CloseOverlay();
            sh.Info($"Wysłane: {target.Value.Label}");
        }
        catch (Exception ex) { sh.Info(ex is ApiException ae ? ae.Message : "Nie udało się wysłać — spróbuj jeszcze raz.", true); }
    }

    /// <summary>Wybór kanału tekstowego albo rozmowy (z szukaniem).</summary>
    static async Task<(long Id, string Label)?> PickChannel(Shell sh)
    {
        var all = new List<(long Id, string Label, string Sub)>();
        foreach (var srv in sh.S.Servers.OrderBy(s => s.Name))
            foreach (var ch in srv.Channels.Where(c => c.Type == "text" && sh.S.Can(c.Id, Perm.ViewChannel | Perm.SendMessages)).OrderBy(c => c.Position))
                all.Add((ch.Id, "#" + ch.Name, srv.Name));
        foreach (var dm in sh.S.DmsSorted())
            all.Add((dm.Id, sh.S.DmTitle(dm), dm.Type == "group" ? "Grupa" : "Rozmowa prywatna"));
        var search = new TextBox { PlaceholderText = "Szukaj kanału albo osoby", Margin = U.Th(0, 0, 0, 8) };
        var list = new StackPanel { Spacing = 2 };
        (long Id, string Label)? chosen = null;
        var d = new AppDialog { Title = "Gdzie wysłać podsumowanie?", CloseButtonText = "Anuluj" };
        void Fill()
        {
            list.Children.Clear();
            var q = search.Text.Trim();
            foreach (var it in all.Where(x => q.Length == 0 || x.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase) || x.Sub.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(60))
            {
                var item = it;
                list.Children.Add(U.Row(U.V(0, U.T(item.Label, 14, U.Semi), U.T(item.Sub, 11.5, null, Theme.Mu2)), () => { chosen = (item.Id, item.Label); d.Hide(); }, pad: U.Th(10, 6)));
            }
            if (list.Children.Count == 0) list.Children.Add(U.T("Nic nie pasuje.", 13, null, Theme.Mu));
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        d.Content = U.V(0, search, new ScrollViewer { Content = list, Height = 340 }).With(x => x.Width = 380);
        await M.WaitNoDialog(sh.XamlRoot);
        await d.ShowAsync();
        return chosen;
    }

    /// <summary>Opowieść: jeden slajd na raz, paski postępu u góry.</summary>
    sealed class Story : UserControl
    {
        readonly Shell _sh;
        readonly List<Slide> _slides;
        readonly Grid _bars = new() { ColumnSpacing = 4 };
        readonly Border _card;
        readonly Grid _face = new();
        int _i;

        public Story(Shell sh, Wrapped w, List<Slide> slides, int start)
        {
            _sh = sh; _slides = slides; _i = start;
            IsTabStop = true;
            UseSystemFocusVisuals = false;
            foreach (var _ in slides) _bars.ColumnDefinitions.Add(new ColumnDefinition());
            var close = U.IconButton("", "Zamknij (Esc)", () => sh.CloseOverlay(), 14, 30, U.W(.9));
            var top = new Grid().Cols("*", "auto").Add(_bars.With(b => b.VerticalAlignment = VerticalAlignment.Center).At(0, 0), close.At(0, 1));
            var hint = U.T("Kliknij z prawej, żeby iść dalej · z lewej, żeby wrócić", 11, null, U.W(.55)).With(t => t.HorizontalAlignment = HorizontalAlignment.Center);
            var layout = new Grid { Padding = U.Th(26, 18, 26, 16) }.Rows("auto", "*", "auto").Add(top.At(0), _face.At(1), hint.At(2));
            _card = new Border { CornerRadius = new CornerRadius(24), Child = layout };
            Content = _card;
            var h = Math.Clamp((sh.ActualHeight > 0 ? sh.ActualHeight : 800) - 90, 520, 760);
            Width = 440; Height = h; Margin = U.Th(0, -30, 0, 0);
            Show(false);
            _face.Tapped += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject o && InButton(o)) return;
                var x = e.GetPosition(_face).X;
                if (x < _face.ActualWidth / 3) Step(-1); else Step(1);
            };
            KeyDown += (_, e) =>
            {
                if (e.Key is VirtualKey.Right or VirtualKey.Space or VirtualKey.PageDown) { Step(1); e.Handled = true; }
                else if (e.Key is VirtualKey.Left or VirtualKey.PageUp) { Step(-1); e.Handled = true; }
                else if (e.Key == VirtualKey.Escape) { sh.CloseOverlay(); e.Handled = true; }
            };
        }

        public static bool InButton(DependencyObject o)
        {
            for (var x = o; x is not null; x = VisualTreeHelper.GetParent(x)) if (x is ButtonBase or TextBox) return true;
            return false;
        }

        void Step(int d)
        {
            var n = _i + d;
            if (n < 0) return;
            if (n >= _slides.Count) { _sh.CloseOverlay(); return; }
            _i = n;
            Show(true, d);
        }

        void Show(bool animate, int dir = 1)
        {
            var (a, b) = Grad[_i % Grad.Length];
            _card.Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = U.C(a), Offset = 0 }, new GradientStop { Color = U.C(b), Offset = 1 } } };
            _bars.Children.Clear();
            for (var k = 0; k < _slides.Count; k++)
                _bars.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = U.W(k <= _i ? .92 : .28) }.At(0, k));
            _face.Children.Clear();
            FrameworkElement el;
            try { el = _slides[_i].Build(); }
            catch (Exception ex) { App.Log("Podsumowanie: " + ex.Message); el = Text("Tego slajdu nie udało się pokazać."); }
            _face.Children.Add(el);
            if (animate) Motion.SlideIn(el, new System.Numerics.Vector3(18 * dir, 0, 0));
        }
    }
}
