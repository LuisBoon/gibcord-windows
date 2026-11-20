using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

/// <summary>Emoji z polskimi słowami do szukania (kategoria → „emoji słowa|emoji słowa|…”).</summary>
static class EmojiData
{
    public static readonly (string Name, string Data)[] Groups =
    [
        ("Buźki", "😀 uśmiech radość|😃 uśmiech|😄 śmiech|😁 szczerzy|😆 śmiech|😅 pot ulga|🤣 turla śmiech|😂 łzy śmiech lol|🙂 uśmiech|🙃 odwrócony|😉 mrugnięcie|😊 rumieniec|😇 aniołek|🥰 serduszka zakochany|😍 zakochany oczy serca|🤩 gwiazdy wow|😘 buziak całus|😗 całus|😚 całus|😋 pyszne|😛 język|😜 język mrugnięcie|🤪 szalony|😝 język|🤑 pieniądze kasa|🤗 przytulas|🤭 ups|🤫 cicho sza|🤔 myśli hmm|🤐 zamknięte usta|🤨 brew podejrzliwy|😐 neutralny|😑 bez wyrazu|😶 bez ust|😏 cwaniak|😒 niezadowolony|🙄 przewraca oczami|😬 grymas|😮‍💨 wydech|🤥 kłamczuch|😌 ulga|😔 smutny|😪 śpiący|🤤 ślinka|😴 śpi zzz|😷 maseczka chory|🤒 gorączka|🤕 ranny|🤢 mdłości|🤮 wymioty|🤧 kicha|🥵 gorąco|🥶 zimno|🥴 pijany|😵 zawroty|🤯 wybuch głowy szok|🤠 kowboj|🥳 impreza urodziny|🥸 przebranie|😎 okulary fajny|🤓 kujon|🧐 monokl|😕 zmieszany|😟 zmartwiony|🙁 smutny|😮 zdziwiony|😯 zdziwiony|😲 zszokowany|😳 zawstydzony|🥺 proszę oczka|😦 przerażony|😧 przerażony|😨 strach|😰 stres|😥 smutny|😢 płacze łza|😭 ryczy płacz|😱 krzyk strach|😖 zmęczony|😣 wytrwały|😞 rozczarowany|😓 pot|😩 zmęczony|😫 zmęczony|🥱 ziewa nuda|😤 wściekły para|😡 zły wściekły|😠 zły|🤬 przekleństwa|😈 diabełek|👿 diabeł|💀 czaszka śmierć umieram|☠️ czaszka|💩 kupa|🤡 klaun|👹 potwór|👺 goblin|👻 duch|👽 kosmita ufo|👾 gra potwór|🤖 robot"),
        ("Gesty i ludzie", "👋 cześć macha|🤚 dłoń|🖐️ dłoń|✋ stop|🖖 vulcan|👌 ok|🤌 włoski|🤏 trochę|✌️ pokój|🤞 kciuki|🫰 serce palce|🤟 kocham|🤘 rock|🤙 dzwoń|👈 lewo|👉 prawo|👆 góra|🖕 środkowy|👇 dół|☝️ jeden|🫵 ty|👍 kciuk ok tak super|👎 nie słabo|✊ pięść|👊 pięść żółwik|🤛 żółwik|🤜 żółwik|👏 brawo oklaski|🙌 hura|🫶 serce dłonie|👐 dłonie|🤲 dłonie|🤝 umowa uścisk|🙏 proszę dziękuję modlitwa|✍️ pisze|💅 paznokcie|🤳 selfie|💪 siła mięsień|🦾 proteza|🧠 mózg|👀 oczy patrzy|👁️ oko|👅 język|👄 usta|🫦 usta|👶 dziecko|🧒 dziecko|👦 chłopiec|👧 dziewczynka|🧑 osoba|👨 mężczyzna|👩 kobieta|🧓 starsza|👴 dziadek|👵 babcia|🙋 zgłasza|🙅 nie|🙆 ok|💁 informacja|🤷 nie wiem wzrusza|🤦 facepalm|🙇 ukłon|🧏 głuchy|🏃 bieg|💃 taniec|🕺 taniec|👯 impreza|🧘 joga|🛌 łóżko|🧑‍💻 programista komputer|🧑‍🎓 student|🧑‍🏫 nauczyciel|🧑‍🍳 kucharz|🦸 bohater|🧙 czarodziej|🧛 wampir|🧟 zombie"),
        ("Serca i znaki", "❤️ serce miłość czerwone|🧡 pomarańczowe serce|💛 żółte serce|💚 zielone serce|💙 niebieskie serce|💜 fioletowe serce|🖤 czarne serce|🤍 białe serce|🤎 brązowe serce|💔 złamane serce|❣️ serce|💕 serca|💞 serca|💓 bijące serce|💗 rosnące serce|💖 błyszczące serce|💘 strzała amor|💝 prezent serce|💯 sto|💢 złość|💥 wybuch bum|💫 gwiazdki|💦 krople pot|💨 pęd|🕳️ dziura|💬 dymek|🗨️ dymek|💭 myśl|💤 sen zzz|🔥 ogień fire super|✨ iskry blask|⭐ gwiazda|🌟 gwiazda|⚡ błyskawica|🎉 impreza konfetti hura|🎊 konfetti|🎁 prezent|🎈 balon|🏆 puchar wygrana|🥇 złoto pierwsze|🥈 srebro|🥉 brąz|🏅 medal|✅ tak gotowe zrobione|❌ nie krzyżyk|❗ wykrzyknik|❓ pytanie|‼️ wykrzyknik|⁉️ pytanie|⚠️ uwaga ostrzeżenie|🚫 zakaz|⛔ stop|🔴 czerwone kółko|🟢 zielone kółko|🔵 niebieskie|🟡 żółte|⚫ czarne|⚪ białe|➕ plus|➖ minus|➡️ strzałka|⬅️ strzałka|⬆️ góra|⬇️ dół|🔄 odśwież|🆗 ok|🆒 cool|🆕 nowe|🆓 za darmo|🔞 18|♻️ recykling|💲 dolar|©️ copyright"),
        ("Jedzenie", "🍏 jabłko|🍎 jabłko|🍐 gruszka|🍊 pomarańcza|🍋 cytryna|🍌 banan|🍉 arbuz|🍇 winogrona|🍓 truskawka|🫐 borówki|🍒 wiśnie|🍑 brzoskwinia|🥭 mango|🍍 ananas|🥥 kokos|🥝 kiwi|🍅 pomidor|🥑 awokado|🥦 brokuł|🥕 marchew|🌽 kukurydza|🌶️ papryczka ostre|🥔 ziemniak|🥐 rogalik|🍞 chleb|🥖 bagietka|🧀 ser|🥚 jajko|🍳 jajecznica|🥓 bekon|🥩 mięso|🍗 udko kurczak|🌭 hotdog|🍔 burger|🍟 frytki|🍕 pizza|🥪 kanapka|🌮 taco|🌯 burrito|🥗 sałatka|🍝 makaron spaghetti|🍜 ramen zupa|🍣 sushi|🍤 krewetka|🥟 pierogi|🍦 lody|🍩 pączek|🍪 ciastko|🎂 tort urodziny|🍰 ciasto|🧁 babeczka|🍫 czekolada|🍬 cukierek|🍭 lizak|🍿 popcorn film|🥤 napój|🧋 bubble tea|☕ kawa|🍵 herbata|🍺 piwo|🍻 piwo toast|🥂 szampan toast|🍷 wino|🥃 whisky|🍹 drink|🧉 mate"),
        ("Zwierzęta i natura", "🐶 pies|🐱 kot|🐭 mysz|🐹 chomik|🐰 królik|🦊 lis|🐻 miś niedźwiedź|🐼 panda|🐨 koala|🐯 tygrys|🦁 lew|🐮 krowa|🐷 świnia|🐸 żaba|🐵 małpa|🙈 małpka nie widzę|🙉 nie słyszę|🙊 nie mówię|🐔 kura|🐧 pingwin|🐦 ptak|🐤 kurczaczek|🦆 kaczka|🦅 orzeł|🦉 sowa|🦇 nietoperz|🐺 wilk|🐗 dzik|🐴 koń|🦄 jednorożec|🐝 pszczoła|🐛 gąsienica|🦋 motyl|🐌 ślimak|🐞 biedronka|🐢 żółw|🐍 wąż|🦎 jaszczurka|🐙 ośmiornica|🦑 kałamarnica|🦀 krab|🐠 ryba|🐬 delfin|🐳 wieloryb|🦈 rekin|🐊 krokodyl|🐘 słoń|🦒 żyrafa|🐿️ wiewiórka|🦔 jeż|🌵 kaktus|🎄 choinka święta|🌲 drzewo|🌳 drzewo|🍀 koniczyna szczęście|🍁 liść jesień|🍂 liście|🌷 tulipan|🌹 róża|🌻 słonecznik|🌸 kwiat|🌞 słońce|🌙 księżyc noc|⭐ gwiazda|☀️ słońce|⛅ chmury|🌧️ deszcz|⛈️ burza|❄️ śnieg zima|☃️ bałwan|🌈 tęcza|🌊 fala morze|💧 kropla"),
        ("Czynności i rzeczy", "⚽ piłka nożna|🏀 koszykówka|🏈 futbol|⚾ baseball|🎾 tenis|🏐 siatkówka|🏓 ping pong|🏸 badminton|🥊 boks|🎿 narty|⛸️ łyżwy|🏋️ siłownia|🚴 rower|🏊 pływanie|🎮 gra gry pad|🕹️ joystick|🎲 kości|♟️ szachy|🎯 cel|🎳 kręgle|🎧 słuchawki muzyka|🎤 mikrofon karaoke|🎵 nuta muzyka|🎶 muzyka|🎸 gitara|🎹 pianino|🥁 bęben|🎬 film|📺 telewizor|📷 aparat zdjęcie|📸 zdjęcie|💻 laptop komputer|🖥️ komputer|⌨️ klawiatura|🖱️ myszka|📱 telefon|☎️ telefon|🔋 bateria|🔌 wtyczka|💡 pomysł żarówka|🔦 latarka|📚 książki nauka|📖 książka|✏️ ołówek|📝 notatka|📅 kalendarz|📌 pinezka|📎 spinacz|✂️ nożyczki|🔒 kłódka|🔑 klucz|🔨 młotek|🛠️ narzędzia|⚙️ zębatka ustawienia|🧪 eksperyment|💊 tabletka|💉 strzykawka|🧻 papier|🛒 zakupy|💰 pieniądze worek|💸 kasa leci|💳 karta|🎒 plecak szkoła|👕 koszulka|👟 but|🧢 czapka|👑 korona król|💍 pierścionek|🚗 auto samochód|🚕 taxi|🚌 autobus|🚓 policja|🚑 karetka|🚒 straż|🏍️ motor|🚲 rower|✈️ samolot|🚀 rakieta|🛸 ufo|🚢 statek|🏠 dom|🏫 szkoła|🏥 szpital|🏰 zamek|⛺ namiot|🗺️ mapa|🌍 ziemia świat"),
    ];

    static List<(string E, string Keys)>? _all;
    public static List<(string E, string Keys)> All => _all ??= Groups.SelectMany(g => g.Data.Split('|').Select(x =>
    {
        var sp = x.IndexOf(' ');
        return sp < 0 ? (x, "") : (x[..sp], x[(sp + 1)..]);
    })).ToList();

    public static IEnumerable<string> InGroup(string data) => data.Split('|').Select(x => { var sp = x.IndexOf(' '); return sp < 0 ? x : x[..sp]; });

    public static string Fold(string s)
    {
        var n = s.ToLowerInvariant().Replace('ł', 'l').Normalize(System.Text.NormalizationForm.FormD);
        return new string(n.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }
}

/// <summary>Wybór emoji (z reakcji): pole szukania, ostatnio używane, emoji serwerów, kategorie.</summary>
public static class EmojiPicker
{
    public static void Show(FrameworkElement anchor, ChatView v, Action<string> pick) => Picker.Show(anchor, v, Picker.Tab.Emoji, pick, null, null, emojiOnly: true);
}

/// <summary>
/// Okienko „wyrażeń” obok pola pisania: Emoji · GIF-y · Naklejki (jak na stronie, plus szukanie emoji po polsku).
/// </summary>
public static class Picker
{
    public enum Tab { Emoji, Gif, Sticker }

    public static void Show(FrameworkElement anchor, ChatView v, Tab tab, Action<string> pickEmoji, Action<GifItem>? pickGif, Action<Sticker>? pickSticker, bool emojiOnly = false,
        Action<FrameworkElement>? demoHost = null)
    {
        var sh = v.Sh;
        var s = v.S;
        var api = sh.C.Api;
        Flyout fly = new() { Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        fly.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter)) { Setters = { new Setter(FrameworkElement.MaxWidthProperty, 480.0), new Setter(Control.PaddingProperty, new Thickness(10)) } };
        var stickers = s.Servers.Where(x => x.Stickers.Count > 0).OrderByDescending(x => x.Id == v.Srv?.Id).ToList();
        var gifsOn = sh.C.Info?.Has("gifs") != false;

        var search = new TextBox { PlaceholderText = "Szukaj emoji", Height = 34, FontSize = 13.5 };
        var body = new Grid();
        var tabs = U.H(4);
        var root = new Grid { Width = 420, Height = 440 }.Rows("auto", "auto", "*").Add(tabs.At(0), search.With(b => b.Margin = U.Th(0, 8, 0, 8)).At(1), body.At(2));
        var cur = tab;
        string gifMode = gifsOn ? "trend" : "fav";
        string? gifNext = null;
        var gifLoading = false;
        var gifDone = false;
        var gifSeq = 0;
        List<GifItem> favs = [];
        HashSet<string> favSet = [];
        var favsLoaded = false;

        Button Cell(UIElement content, string tip, Action click, double size = 40)
        {
            var b = new Button { Content = content, Width = size, Height = size, Padding = U.Th(0), Background = Theme.Transparent, BorderThickness = U.Th(0), CornerRadius = new CornerRadius(8) };
            b.Tip(tip);
            b.Click += (_, _) => click();
            return b;
        }
        void Pick(string e) { App.Prefs.UseEmoji(e); fly.Hide(); pickEmoji(e); }
        FrameworkElement EmojiCell(string e)
        {
            if (Markup.ParseInline(e).FirstOrDefault() is EmojiSpan es && s.EmojiById(es.Id) is { } ce)
                return Cell(new Image { Source = Images.Get(api.Abs(ce.Url), 56), Width = 26, Height = 26 }, ":" + ce.Name + ":", () => Pick(e));
            return Cell(U.T(e, 20).With(t => t.TextTrimming = TextTrimming.None), e, () => Pick(e));
        }
        static TextBlock Head(string t) => U.T(t.ToUpperInvariant(), 10.5, U.Bold, Theme.Mu2).With(x => x.Margin = U.Th(2, 8, 0, 2));

        // ── emoji ──
        void DrawEmoji()
        {
            var q = EmojiData.Fold(search.Text.Trim());
            var panel = new StackPanel { Spacing = 2 };
            void Group(string name, IEnumerable<FrameworkElement> items)
            {
                var wrap = new WrapPanel { Spacing = 2 };
                foreach (var i in items) wrap.Children.Add(i);
                if (wrap.Children.Count == 0) return;
                panel.Children.Add(Head(name));
                panel.Children.Add(wrap);
            }
            var servers = s.Servers.Where(x => x.Emojis.Count > 0).OrderByDescending(x => x.Id == v.Srv?.Id).ToList();
            if (q.Length == 0)
            {
                Group("Ostatnio", App.Prefs.RecentEmoji.Where(e => !e.StartsWith('<') || (Markup.ParseInline(e).FirstOrDefault() is EmojiSpan es && s.EmojiById(es.Id) is not null)).Select(EmojiCell));
                foreach (var srv in servers) Group(srv.Name, srv.Emojis.Select(e => EmojiCell($"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>")));
                foreach (var (name, data) in EmojiData.Groups) Group(name, EmojiData.InGroup(data).Select(EmojiCell));
                panel.Children.Add(U.T("Jeszcze więcej emoji: Win + . (kropka)", 11, null, Theme.Mu2).With(t => t.Margin = U.Th(2, 8, 0, 0)));
            }
            else
            {
                foreach (var srv in servers) Group(srv.Name, srv.Emojis.Where(e => EmojiData.Fold(e.Name).Contains(q)).Select(e => EmojiCell($"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>")));
                Group("Emoji", EmojiData.All.Where(x => EmojiData.Fold(x.Keys).Split(' ').Any(k => k.StartsWith(q)) || (q.Length >= 3 && EmojiData.Fold(x.Keys).Contains(q))).Select(x => EmojiCell(x.E)));
                if (panel.Children.Count == 0) panel.Children.Add(U.T("Nic nie znaleziono. Spróbuj innego słowa albo Win + . (kropka).", 13, null, Theme.Mu, wrap: true).With(t => t.Margin = U.Th(4, 12)));
            }
            body.Children.Clear();
            body.Children.Add(U.Scroll(panel));
        }

        // ── GIF-y ──
        var gifGrid = new Grid();
        var gifCols = new[] { new StackPanel { Spacing = 6 }, new StackPanel { Spacing = 6 } };
        double[] colH = [0, 0];
        var gifStatus = U.T("", 13, null, Theme.Mu, wrap: true).With(t => { t.Margin = U.Th(4, 12); t.HorizontalAlignment = HorizontalAlignment.Center; });
        ScrollViewer? gifScroll = null;

        FrameworkElement GifTile(GifItem g)
        {
            double w = g.Width ?? 200, h = g.Height ?? 150;
            var tileW = 200.0;
            var tileH = Math.Clamp(tileW * h / Math.Max(1, w), 60, 320);
            var img = new Image { Source = Images.Get(api.Abs(g.Preview ?? g.Url)), Stretch = Stretch.UniformToFill, Width = tileW, Height = tileH };
            var star = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = U.B("#B0140c24"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = U.Th(0, 6, 6, 0), Child = U.T(favSet.Contains(g.Url) ? "★" : "☆", 15, U.Bold, favSet.Contains(g.Url) ? Theme.Yellow : U.B("#ffffff")).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            };
            star.Tip("Ulubione");
            star.Tapped += async (_, e) =>
            {
                e.Handled = true;
                var on = favSet.Contains(g.Url);
                try
                {
                    if (on) { await api.RemoveGifFavorite(g.Url); favSet.Remove(g.Url); favs.RemoveAll(f => f.Url == g.Url); }
                    else { await api.AddGifFavorite(g); favSet.Add(g.Url); favs.Insert(0, g); }
                    ((TextBlock)star.Child).Text = on ? "☆" : "★";
                    ((TextBlock)star.Child).Foreground = on ? U.B("#ffffff") : Theme.Yellow;
                    if (on && gifMode == "fav") ResetGifs();
                }
                catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
            };
            var tile = new Grid { Width = tileW, Height = tileH }.Add(new Border { Background = U.W(.06) }, img, star);
            var b = new Border { CornerRadius = new CornerRadius(8), Child = tile };
            b.Tip(string.IsNullOrWhiteSpace(g.Title) ? "Wyślij GIF" : g.Title!);
            b.Tapped += (_, _) => { fly.Hide(); pickGif?.Invoke(g); };
            b.PointerEntered += (_, _) => b.Opacity = .85;
            b.PointerExited += (_, _) => b.Opacity = 1;
            return b;
        }

        void AddGifs(IEnumerable<GifItem> items)
        {
            foreach (var g in items)
            {
                var col = colH[0] <= colH[1] ? 0 : 1;
                var t = GifTile(g);
                gifCols[col].Children.Add(t);
                colH[col] += Math.Clamp(200.0 * (g.Height ?? 150) / Math.Max(1, g.Width ?? 200), 60, 320) + 6;
            }
        }

        async Task EnsureFavs()
        {
            if (favsLoaded) return;
            try { favs = await api.GifFavorites(); favSet = favs.Select(f => f.Url).ToHashSet(); favsLoaded = true; }
            catch { favs = []; }
        }

        async Task LoadGifs()
        {
            if (gifLoading || gifDone) return;
            gifLoading = true;
            var seq = ++gifSeq;
            await EnsureFavs();
            if (seq != gifSeq) { gifLoading = false; return; }
            var q = search.Text.Trim();
            try
            {
                if (gifMode == "fav")
                {
                    var fq = EmojiData.Fold(q);
                    var list = favs.Where(f => fq.Length == 0 || EmojiData.Fold(f.Title ?? "").Contains(fq) || f.Url.Contains(fq)).ToList();
                    AddGifs(list);
                    gifDone = true;
                    gifStatus.Text = list.Count == 0 ? "Nie masz jeszcze ulubionych GIF-ów. Kliknij gwiazdkę na GIF-ie, żeby go tu zapisać." : "";
                }
                else
                {
                    gifStatus.Text = gifNext is null ? "Wczytywanie…" : "";
                    var page = q.Length > 0 ? await api.GifsSearch(q, gifNext) : await api.GifsTrending(gifNext);
                    if (seq != gifSeq) return;
                    if (!page.Enabled) { gifStatus.Text = "Wyszukiwarka GIF-ów nie jest włączona na tym serwerze. Zostają ulubione."; gifDone = true; return; }
                    AddGifs(page.Results);
                    gifNext = page.Next;
                    if (gifNext is null || page.Results.Count == 0) gifDone = true;
                    gifStatus.Text = colH[0] + colH[1] == 0 ? "Nic nie znaleziono." : gifDone ? $"Powered by {(page.Provider ?? sh.C.Info?.GifProvider ?? "GIPHY").ToUpperInvariant()}" : "";
                }
            }
            catch (Exception ex) { if (seq == gifSeq) gifStatus.Text = GibCordClient.Friendly(ex); }
            finally { if (seq == gifSeq) gifLoading = false; }
        }

        void ResetGifs()
        {
            gifSeq++;
            gifLoading = false;
            gifDone = false;
            gifNext = null;
            colH[0] = colH[1] = 0;
            foreach (var c in gifCols) c.Children.Clear();
            _ = LoadGifs();
        }

        void DrawGifs()
        {
            var seg = U.H(6);
            void ModeBtn(string mode, string label)
            {
                var on = gifMode == mode;
                var b = new Button { Content = U.T(label, 12, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(12, 3), Height = 26 };
                b.Click += (_, _) => { gifMode = mode; search.Text = ""; DrawGifs(); };
                seg.Add(b);
            }
            if (gifsOn) ModeBtn("trend", "Na czasie");
            ModeBtn("fav", "★ Ulubione");
            gifGrid.Children.Clear();
            gifGrid.ColumnDefinitions.Clear();
            gifGrid.Cols("*", "*");
            gifCols[0].Margin = U.Th(0, 0, 3, 0);
            gifCols[1].Margin = U.Th(3, 0, 0, 0);
            foreach (var c in gifCols) { if (c.Parent is Panel p) p.Children.Remove(c); }
            gifGrid.Add(gifCols[0].At(0, 0), gifCols[1].At(0, 1));
            if (gifStatus.Parent is Panel sp) sp.Children.Remove(gifStatus);
            var content = U.V(6, gifGrid, gifStatus);
            gifScroll = U.Scroll(content);
            gifScroll.ViewChanged += (_, _) => { if (gifScroll.VerticalOffset + gifScroll.ViewportHeight > gifScroll.ExtentHeight - 300) _ = LoadGifs(); };
            body.Children.Clear();
            body.Children.Add(new Grid().Rows("auto", "*").Add(seg.With(x => x.Margin = U.Th(0, 0, 0, 8)).At(0), gifScroll.At(1)));
            ResetGifs();
        }

        // ── naklejki ──
        void DrawStickers()
        {
            var q = EmojiData.Fold(search.Text.Trim());
            var panel = new StackPanel { Spacing = 2 };
            foreach (var srv in stickers)
            {
                var list = srv.Stickers.Where(x => q.Length == 0 || EmojiData.Fold(x.Name).Contains(q)).ToList();
                if (list.Count == 0) continue;
                panel.Children.Add(Head(srv.Name));
                var wrap = new WrapPanel { Spacing = 6 };
                foreach (var st in list)
                {
                    var stc = st;
                    wrap.Children.Add(Cell(new Image { Source = Images.Get(api.Abs(st.Url), 160), Width = 80, Height = 80, Stretch = Stretch.Uniform }, st.Name, () => { fly.Hide(); pickSticker?.Invoke(stc); }, 92));
                }
                panel.Children.Add(wrap);
            }
            if (panel.Children.Count == 0) panel.Children.Add(U.T(q.Length > 0 ? "Brak naklejek o tej nazwie." : "Na Twoich serwerach nie ma jeszcze naklejek.", 13, null, Theme.Mu, wrap: true).With(t => t.Margin = U.Th(4, 12)));
            body.Children.Clear();
            body.Children.Add(U.Scroll(panel));
        }

        void Draw()
        {
            tabs.Children.Clear();
            void TabBtn(Tab t, string label)
            {
                var on = cur == t;
                var b = new Button
                {
                    Content = U.T(label, 13, U.Bold, on ? Theme.Fg : Theme.Mu), Background = on ? U.W(.12) : Theme.Transparent, BorderThickness = U.Th(0),
                    CornerRadius = new CornerRadius(8), Padding = U.Th(12, 4), Height = 30,
                };
                b.Click += (_, _) => { if (cur == t) return; cur = t; search.Text = ""; Draw(); };
                tabs.Add(b);
            }
            if (!emojiOnly)
            {
                TabBtn(Tab.Emoji, "Emoji");
                if (pickGif is not null) TabBtn(Tab.Gif, "GIF-y");
                if (pickSticker is not null && stickers.Count > 0) TabBtn(Tab.Sticker, "Naklejki");
            }
            tabs.Visibility = emojiOnly ? Visibility.Collapsed : Visibility.Visible;
            search.PlaceholderText = cur switch { Tab.Gif => gifMode == "fav" ? "Szukaj w ulubionych" : "Szukaj GIF-ów", Tab.Sticker => "Szukaj naklejek", _ => "Szukaj emoji (np. serce, śmiech, ogień)" };
            switch (cur)
            {
                case Tab.Gif: DrawGifs(); break;
                case Tab.Sticker: DrawStickers(); break;
                default: DrawEmoji(); break;
            }
        }

        var typingTimer = sh.DispatcherQueue.CreateTimer();
        typingTimer.Interval = TimeSpan.FromMilliseconds(350);
        typingTimer.IsRepeating = false;
        typingTimer.Tick += (_, _) => { if (cur == Tab.Gif) { if (search.Text.Trim().Length > 0 && gifMode == "fav" && gifsOn) gifMode = "trend"; ResetGifs(); } };
        search.TextChanged += (_, _) =>
        {
            if (cur == Tab.Gif) { typingTimer.Stop(); typingTimer.Start(); }
            else if (cur == Tab.Sticker) DrawStickers();
            else DrawEmoji();
        };
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || cur != Tab.Emoji) return;
            var q = EmojiData.Fold(search.Text.Trim());
            var first = EmojiData.All.FirstOrDefault(x => EmojiData.Fold(x.Keys).Split(' ').Any(k => k.StartsWith(q))).E;
            if (q.Length > 0 && first is not null) { e.Handled = true; Pick(first); }
        };
        if (cur == Tab.Gif && pickGif is null) cur = Tab.Emoji;
        Draw();
        if (demoHost is not null) { demoHost(new Border { Child = root, Background = U.B("#FA2a2147"), CornerRadius = new CornerRadius(12), Padding = U.Th(10) }); return; }
        fly.Content = root;
        fly.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        fly.ShowAt(anchor);
    }
}
