using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

/// <summary>6.4: „Mój profil” → wygląd profilu: ramka wokół awatara, efekt karty, kolory karty, wizytówka (zapis razem z resztą profilu).</summary>
public sealed partial class SettingsPage
{
    /// <summary>Szkic zmian wyglądu (zapis przyciskiem „Zapisz zmiany”).</summary>
    sealed class LookDraft
    {
        public string? Decor;
        public string? Fx;
        public List<string>? Theme;
        public List<ShowcaseTile> Showcase = [];
    }

    static bool _catalogAsked;
    ShowcaseOptions? _scOptions;

    /// <summary>Tryb pokazowy: przykładowe gry i klipy do wizytówki.</summary>
    public static ShowcaseOptions? DemoShowcase;
    /// <summary>Tryb pokazowy: przewiń „Mój profil” do wyglądu profilu (zrzut --view setprofil).</summary>
    public static bool DemoScrollLook;

    async void LoadCatalog(Action redraw)
    {
        if (_catalogAsked || _sh.C.Offline) return;
        _catalogAsked = true;
        try { ProfileLook.Catalog = await Api.GetProfileCatalog(); redraw(); } catch { }
    }

    /// <summary>Karty wyglądu profilu. <paramref name="changed"/> — po każdej zmianie (podgląd obok).</summary>
    FrameworkElement LookCards(LookDraft d, User me, Action<bool> changed)
    {
        var host = new StackPanel { Spacing = 10 };
        // mały podgląd (zawsze widoczny — duży jest obok tylko w szerokim oknie): awatar z ramką na kolorach karty + efekt
        var fxLayer = ProfileFx.Layer();
        var miniAv = new Avatar(72, false);
        var miniName = U.T(me.Name, 16, U.Bold);
        var miniBg = new Border { CornerRadius = new CornerRadius(14), Height = 128 };
        var mini = new Grid { Height = 128 }.Add(miniBg, U.H(16, miniAv, U.V(2, miniName, U.T("@" + me.Username, 12, null, Theme.Mu))).With(p => { p.Margin = U.Th(26, 0, 0, 0); p.VerticalAlignment = VerticalAlignment.Center; }), fxLayer);
        void DrawMini()
        {
            miniAv.Set(S, Api, me.Id).SetDecor(d.Decor, Api);
            if (ProfileLook.ValidTheme(d.Theme))
            {
                miniBg.Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop { Color = U.C(d.Theme![0]), Offset = 0 }, new GradientStop { Color = U.C(d.Theme[1]), Offset = 1 } }, 90);
                miniName.Foreground = ProfileLook.IsLight(d.Theme[0], d.Theme[1]) ? U.B("#16121f") : Theme.Fg;
            }
            else { miniBg.Background = U.W(.05); miniName.Foreground = Theme.Fg; }
        }
        void PlayFx() { if (mini.ActualWidth > 0) ProfileFx.Play(fxLayer, d.Fx, mini.ActualWidth, mini.ActualHeight); }
        DrawMini();

        // ── ramka ──
        var decoWrap = new WrapPanel { Spacing = 8 };
        void DrawDecor()
        {
            decoWrap.Children.Clear();
            void Tile(string? id, string name)
            {
                var on = d.Decor == id;
                var av = new Avatar(44).Set(S, Api, me.Id).SetDecor(id, Api);
                av.HorizontalAlignment = HorizontalAlignment.Center;
                av.Margin = U.Th(0, 8, 0, 4);
                var t = new Border
                {
                    Width = 84, Height = 92, CornerRadius = new CornerRadius(12), Background = on ? Theme.AccentA(.18) : U.W(.04),
                    BorderBrush = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(on ? 2 : 1),
                    Child = U.V(2, av, U.T(name, 11, on ? U.Bold : U.Semi, on ? Theme.Fg : Theme.Mu).With(x => { x.HorizontalAlignment = HorizontalAlignment.Center; x.TextTrimming = TextTrimming.CharacterEllipsis; x.MaxWidth = 76; })),
                };
                t.Tip(id is null ? "Bez ramki" : name);
                t.Tapped += (_, _) => { d.Decor = id; DrawDecor(); DrawMini(); changed(false); };
                Motion.Pressable(t, .95f);
                decoWrap.Children.Add(t);
            }
            Tile(null, "Brak");
            foreach (var dd in ProfileLook.Catalog.Decor) Tile(dd.Id, dd.Name);
        }
        DrawDecor();

        // ── efekt ──
        var fxWrap = new WrapPanel { Spacing = 6 };
        void DrawFx()
        {
            fxWrap.Children.Clear();
            void Chip(string? id, string name)
            {
                var c = new ChipField { Content = name, IsChecked = d.Fx == id };
                c.Click += (_, _) => { d.Fx = id; DrawFx(); PlayFx(); changed(true); };
                fxWrap.Children.Add(c);
            }
            Chip(null, "Brak");
            foreach (var f in ProfileLook.Catalog.Fx) Chip(f.Id, f.Name);
        }
        DrawFx();
        var replay = U.Ghost("▶ Pokaż efekt", () => { PlayFx(); changed(true); }, 30);

        // ── kolory karty ──
        var colorsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void DrawColors()
        {
            var t = ProfileLook.ValidTheme(d.Theme) ? d.Theme! : null;
            Button Swatch(int i)
            {
                var hex = t?[i] ?? (i == 0 ? "#3a2d5c" : "#1a1426");
                var b = new Button
                {
                    Width = 44, Height = 32, CornerRadius = new CornerRadius(8), Background = U.B(hex), BorderBrush = U.W(.25), BorderThickness = U.Th(1),
                    Content = t is null ? U.T("+", 14, U.Bold, Theme.Fg) : null,
                };
                b.Tip(i == 0 ? "Kolor u góry karty" : "Kolor u dołu karty");
                var picker = new ColorPicker { Color = U.C(hex), IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = true, Width = 300 };
                var fly = new Flyout { Content = picker };
                fly.Closed += (_, _) =>
                {
                    var c = picker.Color;
                    var nh = $"#{c.R:x2}{c.G:x2}{c.B:x2}";
                    var cur = t is null ? new List<string> { "#3a2d5c", "#1a1426" } : [.. t];
                    if (cur[i] == nh && t is not null) return;
                    cur[i] = nh;
                    d.Theme = cur;
                    DrawColors(); DrawMini(); changed(false);
                };
                b.Flyout = fly;
                return b;
            }
            var presets = new WrapPanel { Spacing = 6 };
            foreach (var (name, c1, c2) in ProfileLook.Presets)
            {
                var on = t is not null && t[0] == c1 && t[1] == c2;
                var p = new Border
                {
                    Width = 38, Height = 38, CornerRadius = new CornerRadius(19), BorderBrush = on ? Theme.Fg : U.W(.15), BorderThickness = U.Th(on ? 3 : 1),
                    Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop { Color = U.C(c1), Offset = 0 }, new GradientStop { Color = U.C(c2), Offset = 1 } }, 90),
                };
                p.Tip(name);
                var (a1, a2) = (c1, c2);
                p.Tapped += (_, _) => { d.Theme = [a1, a2]; DrawColors(); DrawMini(); changed(false); };
                Motion.Pressable(p, .92f);
                presets.Children.Add(p);
            }
            colorsHost.Content = U.V(10,
                U.HG(10, U.V(4, U.T("Góra", 11, null, Theme.Mu2), Swatch(0)), U.T("→", 16, null, Theme.Mu2).With(x => x.VerticalAlignment = VerticalAlignment.Bottom), U.V(4, U.T("Dół", 11, null, Theme.Mu2), Swatch(1)),
                    t is not null ? U.Ghost("Przywróć domyślne", () => { d.Theme = null; DrawColors(); DrawMini(); changed(false); }, 32).With(x => x.VerticalAlignment = VerticalAlignment.Bottom) : null),
                U.T("Gotowe zestawy", 11, null, Theme.Mu2), presets);
        }
        DrawColors();

        // ── wizytówka ──
        var scList = new StackPanel { Spacing = 6 };
        var scAdd = new WrapPanel { Spacing = 6 };
        void DrawShowcase()
        {
            scList.Children.Clear();
            if (d.Showcase.Count == 0) scList.Add(U.T("Wizytówka jest pusta. Dodaj do 3 kafelków — pokażą się na Twojej karcie profilu.", 12.5, null, Theme.Mu2, wrap: true));
            for (var i = 0; i < d.Showcase.Count; i++)
            {
                var t = d.Showcase[i];
                var idx = i;
                var (icon, label) = t.K switch
                {
                    "game" => ("🎮", $"Gra: {t.Name}" + (t.Hours is > 0 ? $" · {t.Hours} h" : "")),
                    "clip" => ("🎬", $"Klip: {t.Name ?? "film"}"),
                    "spotify" => ("🎵", "Spotify — co teraz słuchasz"),
                    _ => ("💬", $"Napis: „{t.T}”"),
                };
                var row = new Grid { ColumnSpacing = 6 }.Cols("auto", "*", "auto", "auto", "auto").Add(
                    U.T(icon, 15).At(0, 0), U.T(label, 13, U.Semi).With(x => x.TextTrimming = TextTrimming.CharacterEllipsis).At(0, 1),
                    U.IconButton("", "W górę", () => { if (idx == 0) return; (d.Showcase[idx - 1], d.Showcase[idx]) = (d.Showcase[idx], d.Showcase[idx - 1]); DrawShowcase(); changed(false); }, 12, 28).With(b => b.IsEnabled = idx > 0).At(0, 2),
                    U.IconButton("", "W dół", () => { if (idx >= d.Showcase.Count - 1) return; (d.Showcase[idx + 1], d.Showcase[idx]) = (d.Showcase[idx], d.Showcase[idx + 1]); DrawShowcase(); changed(false); }, 12, 28).With(b => b.IsEnabled = idx < d.Showcase.Count - 1).At(0, 3),
                    U.IconButton("", "Usuń z wizytówki", () => { d.Showcase.RemoveAt(idx); DrawShowcase(); changed(false); }, 12, 28).At(0, 4));
                scList.Add(U.Box(row, U.W(.04), 10, U.Th(10, 4)));
            }
            scAdd.Children.Clear();
            var full = d.Showcase.Count >= ProfileLook.Catalog.ShowcaseMax;
            Button Add(string text, Func<FrameworkElement> flyout)
            {
                var b = U.Ghost(text, () => { }, 30);
                b.IsEnabled = !full;
                b.Click += (_, _) =>
                {
                    var fly = new Flyout { Content = flyout(), Placement = FlyoutPlacementMode.Bottom };
                    fly.ShowAt(b);
                };
                return b;
            }
            void Push(ShowcaseTile t) { if (d.Showcase.Count < ProfileLook.Catalog.ShowcaseMax) d.Showcase.Add(t); DrawShowcase(); changed(false); }
            scAdd.Children.Add(Add("+ Gra", () => GamePicker(d, Push)));
            scAdd.Children.Add(Add("+ Klip", () => ClipPicker(d, Push)));
            var sp = U.Ghost("+ Spotify", () => Push(new ShowcaseTile { K = "spotify" }), 30);
            sp.IsEnabled = !full && !d.Showcase.Any(x => x.K == "spotify");
            scAdd.Children.Add(sp);
            scAdd.Children.Add(Add("+ Napis", () =>
            {
                var tb = new TextBox { PlaceholderText = "np. Szukam ekipy do Minecrafta", MaxLength = 40, Width = 260 };
                var ok = U.Primary("Dodaj", () => { var v = tb.Text.Trim(); if (v.Length > 0) Push(new ShowcaseTile { K = "text", T = v }); }, 32);
                return U.V(8, U.T("Krótki napis (do 40 znaków)", 12, U.Semi, Theme.Mu), tb, ok);
            }));
        }
        DrawShowcase();
        LoadCatalog(() => { DrawDecor(); DrawFx(); DrawShowcase(); });

        host.Add(Card(Label("Podgląd"), mini));
        host.Add(Card(Label("Ramka wokół awatara"), U.T("Widać ją przy Twoich wiadomościach, na liście osób, w rozmowach i na karcie profilu.", 12, null, Theme.Mu2, wrap: true), decoWrap));
        host.Add(Card(Label("Efekt karty profilu"), U.T("Krótka animacja, gdy ktoś otworzy Twoją kartę (nie u osób z włączonym „Mniej ruchu”).", 12, null, Theme.Mu2, wrap: true), fxWrap, replay.With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        host.Add(Card(Label("Kolory karty profilu"), colorsHost, U.T("Kolor tekstu na karcie dobiera się sam, żeby było go dobrze widać.", 11.5, null, Theme.Mu2, wrap: true)));
        host.Add(Card(Label("Wizytówka"), U.T("Do 3 kafelków na karcie profilu: ulubiona gra (z godzinami w grze), Twój klip, to, czego słuchasz na Spotify, albo krótki napis.", 12, null, Theme.Mu2, wrap: true), scList, scAdd));
        return host;
    }

    async Task<ShowcaseOptions> ScOptions()
    {
        if (DemoShowcase is not null) return DemoShowcase;
        if (_scOptions is not null) return _scOptions;
        try { return _scOptions = await Api.GetShowcaseOptions(); } catch (Exception ex) { Err(ex); return new ShowcaseOptions(); }
    }

    FrameworkElement GamePicker(LookDraft d, Action<ShowcaseTile> push)
    {
        var list = new StackPanel { Spacing = 2 };
        var own = new TextBox { PlaceholderText = "Albo wpisz nazwę gry", MaxLength = 60, Width = 250 };
        var add = U.Primary("Dodaj", () =>
        {
            var n = own.Text.Trim();
            if (n.Length == 0) return;
            if (d.Showcase.Any(x => x.K == "game" && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))) { _sh.Info("Ta gra już jest w wizytówce", true); return; }
            push(new ShowcaseTile { K = "game", Name = n });
        }, 32);
        var panel = U.V(8, U.T("Gry, w które grasz (z wykrywania gier)", 12, U.Semi, Theme.Mu), list, U.HG(6, own, add));
        panel.Width = 330;
        list.Add(U.T("Wczytywanie…", 12, null, Theme.Mu2));
        _ = Fill();
        async Task Fill()
        {
            var o = await ScOptions();
            list.Children.Clear();
            if (o.Games.Count == 0) list.Add(U.T("Brak gier w statystykach — wpisz nazwę poniżej.", 12, null, Theme.Mu2, wrap: true));
            foreach (var g in o.Games.Take(10))
            {
                var gg = g;
                var used = d.Showcase.Any(x => x.K == "game" && string.Equals(x.Name, gg.Name, StringComparison.OrdinalIgnoreCase));
                var row = U.Row(new Grid().Cols("*", "auto").Add(U.T("🎮 " + gg.Name, 13, U.Semi, used ? Theme.Mu2 : Theme.Fg).At(0, 0), U.T(gg.Hours > 0 ? $"{gg.Hours} h" : "", 12, null, Theme.Mu2).At(0, 1)),
                    () => { if (!used) push(new ShowcaseTile { K = "game", Name = gg.Name, Hours = gg.Hours }); }, false, 8, U.Th(8, 5));
                list.Add(row);
            }
        }
        return panel;
    }

    FrameworkElement ClipPicker(LookDraft d, Action<ShowcaseTile> push)
    {
        var list = new StackPanel { Spacing = 2 };
        var panel = U.V(8, U.T("Twoje filmy wysłane na GibCordzie", 12, U.Semi, Theme.Mu), list,
            U.T("Klip zobaczą tylko osoby, które mają dostęp do kanału, na którym go wysłano.", 11, null, Theme.Mu2, wrap: true));
        panel.Width = 330;
        list.Add(U.T("Wczytywanie…", 12, null, Theme.Mu2));
        _ = Fill();
        async Task Fill()
        {
            var o = await ScOptions();
            list.Children.Clear();
            if (o.Clips.Count == 0) list.Add(U.T("Nie masz jeszcze filmów — wyślij klip na kanał albo w rozmowie, a pojawi się tutaj.", 12, null, Theme.Mu2, wrap: true));
            foreach (var c in o.Clips.Take(12))
            {
                var cc = c;
                var used = d.Showcase.Any(x => x.K == "clip" && x.Mid == cc.Mid);
                var where = S.ChannelById(cc.ChannelId) is { } ch ? "#" + ch.Name : S.DmById(cc.ChannelId) is { } dm ? S.DmTitle(dm) : "";
                list.Add(U.Row(U.V(0, U.T("🎬 " + cc.Name, 13, U.Semi, used ? Theme.Mu2 : Theme.Fg).With(x => x.TextTrimming = TextTrimming.CharacterEllipsis), U.T($"{where} · {Format.When(cc.At)}", 11, null, Theme.Mu2)),
                    () => { if (!used) push(new ShowcaseTile { K = "clip", Mid = cc.Mid, Name = cc.Name, Url = cc.Url, ChannelId = cc.ChannelId }); }, false, 8, U.Th(8, 5)));
            }
        }
        return panel;
    }

    /// <summary>Osoba do podglądu karty: ja ze zmianami ze szkicu (bez zapisu).</summary>
    static User PreviewUser(User me, LookDraft d, string? displayName, string? pronouns, string? bio, string? banner)
    {
        var u = Json.Deserialize<User>(Json.Serialize(me)) ?? new User();
        u.Decor = d.Decor; u.ProfileFx = d.Fx; u.ProfileTheme = d.Theme is null ? null : [.. d.Theme]; u.Showcase = [.. d.Showcase];
        if (displayName is not null) u.DisplayName = displayName.Length == 0 ? null : displayName;
        if (pronouns is not null) u.Pronouns = pronouns;
        if (bio is not null) u.Bio = bio;
        u.Banner = banner;
        return u;
    }
}
