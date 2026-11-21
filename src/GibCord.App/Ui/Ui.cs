using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.Text;

namespace GibCord.App.Ui;

/// <summary>Kolory i czcionki GibCorda (styl „Szkło”, jak na stronie i w makiecie 1).</summary>
public static class Theme
{
    public static FontFamily Font = new("ms-appx:///Assets/Fonts/Sora.ttf#Sora, "
        + new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Sora.ttf")).AbsoluteUri + "#Sora, Segoe UI Variable Text, Segoe UI, Segoe UI Emoji");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static FontFamily Icons => (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];

    public static readonly Color FgC = U.C("#f6f2fb");
    public static readonly Brush Fg = U.B("#f6f2fb");
    public static readonly Brush Mu = U.W(.62);
    public static readonly Brush Mu2 = U.W(.42);
    public static Brush Accent { get; private set; } = U.B("#ffb98f");
    public static Brush OnAccent { get; private set; } = U.B("#2a1630");

    /// <summary>Motyw (jak na stronie): glass, midnight, forest, cherry. Jasnego w aplikacji nie ma.</summary>
    public static string Name { get; private set; } = "glass";
    static (string A, string G1, string G2, string On, string Base, string B1, string B2, string B3) _p = ("#ffb98f", "#ffb98f", "#e8708f", "#2a1630", "#1b1531", "#1d6b74", "#6b2d7c", "#c6653f");
    public static readonly (string Key, string Name, string Desc)[] All = [("glass", "Szkło", "morski, śliwka, pomarańcz"), ("midnight", "Północ", "czysta czerń (OLED)"), ("forest", "Las", "zielenie i złoto"), ("cherry", "Wiśnia", "róże i czerwienie")];

    /// <summary>Ustawia kolory motywu. Działa dla nowo budowanych widoków — okno przebudowuje się po zmianie.</summary>
    public static void Apply(string? name)
    {
        Name = name is "midnight" or "forest" or "cherry" ? name : "glass";
        _p = Name switch
        {
            "midnight" => ("#ffb98f", "#ffb98f", "#e8708f", "#2a1630", "#000000", "#0b0b10", "#0b0b10", "#0b0b10"),
            "forest" => ("#b5e48c", "#d9ed92", "#76c893", "#13240f", "#0f1a14", "#2d6a4f", "#40513b", "#b08a3a"),
            "cherry" => ("#ff8fab", "#ffb3c6", "#ff4d6d", "#33000f", "#1a0a14", "#7a1238", "#3c1053", "#ff6b6b"),
            _ => ("#ffb98f", "#ffb98f", "#e8708f", "#2a1630", "#1b1531", "#1d6b74", "#6b2d7c", "#c6653f"),
        };
        Accent = U.B(_p.A);
        OnAccent = U.B(_p.On);
    }
    public static readonly Brush Green = U.B("#6fe3a5");
    public static readonly Brush Red = U.B("#ff6b81");
    public static readonly Brush Red2 = U.B("#ff4d6a");
    public static readonly Brush Yellow = U.B("#f2c45a");
    public static readonly Brush Gray = U.B("#8b8599");
    public static readonly Brush Teal = U.B("#7fd6cc");

    public static readonly Brush TitleBar = U.B("#6B0A0618");
    public static readonly Brush Rail = U.B("#4D0A0618");
    public static readonly Brush Side = U.W(.045);
    public static readonly Brush Main = U.W(.025);
    public static readonly Brush Members = U.W(.035);
    public static readonly Brush Line = U.W(.07);
    public static readonly Brush Hover = U.W(.07);
    public static readonly Brush Active = U.W(.13);
    public static readonly Brush Input = U.W(.08);
    public static readonly Brush InputLine = U.W(.12);
    public static readonly Brush Card = U.B("#4D0A0618");
    public static readonly Brush Dark = U.B("#FA281F40");
    public static readonly Brush MentionBg = U.B("#26ffb98f");
    public static readonly Brush MentionFg = U.B("#ffd0b0");
    public static readonly Brush MentionedRow = U.B("#14ffb98f");
    public static readonly Brush Transparent = new SolidColorBrush(Colors.Transparent);

    public static LinearGradientBrush AccentGradient() => new()
    {
        StartPoint = new(0, 0), EndPoint = new(1, 1),
        GradientStops = { new GradientStop { Color = U.C(_p.G1), Offset = 0 }, new GradientStop { Color = U.C(_p.G2), Offset = 1 } },
    };

    /// <summary>Tło okna: trzy miękkie plamy (morski, śliwkowy, pomarańczowy) na granacie — rysowane raz, bez rozmycia na żywo.</summary>
    /// <summary>Czy system pozwala na szkło: Mica (Windows 11) albo akryl (Windows 10).</summary>
    public static string? GlassKind
    {
        get
        {
            try
            {
                if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) return "mica";
                if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()) return "acrylic";
            }
            catch { }
            return null;
        }
    }
    public static bool GlassOn => App.Prefs.WindowsGlass && GlassKind is not null;

    public static Grid Backdrop()
    {
        static Rectangle Blob(string color, double cx, double cy, double rx, double ry) => new()
        {
            Fill = new RadialGradientBrush
            {
                Center = new(cx, cy), GradientOrigin = new(cx, cy), RadiusX = rx, RadiusY = ry,
                GradientStops = { new GradientStop { Color = U.C(color), Offset = 0 }, new GradientStop { Color = U.C("#00" + color[^6..]), Offset = 1 } },
            },
        };
        // szkło Windows: kolory motywu jako półprzezroczysty odcień nad rozmytym pulpitem
        var glass = GlassOn;
        string A(string c, string a) => glass ? "#" + a + c[1..] : c;
        var g = new Grid { Background = U.B(A(_p.Base, "8C")), IsHitTestVisible = false };
        if (Name == "midnight") return g; // OLED: czysta czerń
        g.Children.Add(Blob(A(_p.B1, "B3"), .08, .08, .62, .75));
        g.Children.Add(Blob(A(_p.B2, "B3"), .92, .14, .58, .70));
        g.Children.Add(Blob(A(_p.B3, "B3"), .70, 1.05, .60, .60));
        return g;
    }

    public static Brush StatusBrush(string status) => status switch
    {
        "online" => Green, "idle" => Yellow, "dnd" => Red, _ => Gray,
    };
}

/// <summary>Krótkie pomocniki do budowania interfejsu w C#.</summary>
public static class U
{
    static readonly Dictionary<string, SolidColorBrush> _brushes = [];

    public static Color C(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        if (h.Length == 6) h = "FF" + h;
        if (h.Length != 8 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return Colors.Gray;
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static SolidColorBrush B(string hex)
    {
        if (_brushes.TryGetValue(hex, out var b)) return b;
        return _brushes[hex] = new SolidColorBrush(C(hex));
    }

    /// <summary>Biały z przezroczystością (jak rgba(255,255,255,a) na stronie).</summary>
    public static SolidColorBrush W(double a) => new(Color.FromArgb((byte)Math.Round(a * 255), 255, 255, 255));

    public static SolidColorBrush Alpha(string hex, double a) { var c = C(hex); return new SolidColorBrush(Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B)); }

    public static T With<T>(this T o, Action<T> a) { a(o); return o; }

    static GridLength Len(string d) => d switch
    {
        "auto" => GridLength.Auto,
        "*" => new GridLength(1, GridUnitType.Star),
        _ when d.EndsWith('*') => new GridLength(double.Parse(d[..^1], CultureInfo.InvariantCulture), GridUnitType.Star),
        _ => new GridLength(double.Parse(d, CultureInfo.InvariantCulture)),
    };

    public static Grid Rows(this Grid g, params string[] defs) { foreach (var d in defs) g.RowDefinitions.Add(new RowDefinition { Height = Len(d) }); return g; }
    public static Grid Cols(this Grid g, params string[] defs) { foreach (var d in defs) g.ColumnDefinitions.Add(new ColumnDefinition { Width = Len(d) }); return g; }

    public static T At<T>(this T e, int row = 0, int col = 0, int rowSpan = 1, int colSpan = 1) where T : FrameworkElement
    {
        Grid.SetRow(e, row); Grid.SetColumn(e, col);
        if (rowSpan > 1) Grid.SetRowSpan(e, rowSpan);
        if (colSpan > 1) Grid.SetColumnSpan(e, colSpan);
        return e;
    }

    public static Grid Add(this Grid g, params UIElement?[] els) { foreach (var e in els) if (e is not null) g.Children.Add(e); return g; }
    public static StackPanel Add(this StackPanel p, params UIElement?[] els) { foreach (var e in els) if (e is not null) p.Children.Add(e); return p; }

    public static StackPanel H(double spacing, params UIElement?[] children) =>
        new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing }.Add(children);
    public static StackPanel V(double spacing, params UIElement?[] children) =>
        new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing }.Add(children);

    public static TextBlock T(string text, double size = 14, FontWeight? weight = null, Brush? fg = null, bool wrap = false) => new()
    {
        Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = fg ?? Theme.Fg, FontFamily = Theme.Font,
        TextWrapping = wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static FontIcon I(string glyph, double size = 16, Brush? fg = null) => new()
    {
        Glyph = glyph, FontSize = size, FontFamily = Theme.Icons, Foreground = fg ?? Theme.Fg, VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Box(UIElement? child, Brush? bg = null, double radius = 0, Thickness? pad = null, Brush? border = null, double borderW = 0) => new()
    {
        Child = child, Background = bg, CornerRadius = new CornerRadius(radius), Padding = pad ?? new Thickness(0),
        BorderBrush = border, BorderThickness = new Thickness(borderW), VerticalAlignment = VerticalAlignment.Center,
    };

    public static Thickness Th(double all) => new(all);
    public static Thickness Th(double h, double v) => new(h, v, h, v);
    public static Thickness Th(double l, double t, double r, double b) => new(l, t, r, b);

    /// <summary>Płaski przycisk z ikoną (pasek kanału, pole wpisywania).</summary>
    public static Button IconButton(string glyph, string tip, Action click, double size = 16, double box = 32, Brush? fg = null)
    {
        var b = new Button
        {
            Content = I(glyph, size, fg ?? Theme.Mu), Width = box, Height = box, Padding = Th(0), Background = Theme.Transparent,
            BorderThickness = Th(0), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip); // czytniki ekranu (Narrator) mówią, co robi przycisk
        b.Click += (_, _) => click();
        return Motion.Pressable(b, .88f);
    }

    /// <summary>Przycisk w kolorze akcentu (gradient brzoskwinia → róż).</summary>
    public static Button Primary(string text, Action click, double height = 40)
    {
        var b = new Button
        {
            Content = T(text, 14, FontWeights.Bold, Theme.OnAccent), Height = height, Padding = Th(18, 0),
            Background = Theme.AccentGradient(), BorderThickness = Th(0), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        b.Resources["ButtonBackgroundPointerOver"] = U.B("#ffc9a6");
        b.Resources["ButtonBackgroundPressed"] = U.B("#f0a986");
        b.Click += (_, _) => click();
        return Motion.Pressable(b, .96f);
    }

    public static Button Ghost(string text, Action click, double height = 36)
    {
        var b = new Button
        {
            Content = T(text, 13, FontWeights.SemiBold), Height = height, Padding = Th(14, 0), Background = U.W(.1),
            BorderThickness = Th(0), CornerRadius = new CornerRadius(10),
        };
        b.Click += (_, _) => click();
        return Motion.Pressable(b, .96f);
    }

    /// <summary>Klikalny wiersz (kanał, rozmowa, osoba): tło przy najechaniu, zaznaczenie, ręka.</summary>
    public static Border Row(UIElement content, Action? click, bool selected = false, double radius = 9, Thickness? pad = null, Thickness? margin = null, Brush? selectedBg = null)
    {
        var bg = selected ? selectedBg ?? Theme.Active : Theme.Transparent;
        var b = new Border { Child = content, Background = bg, CornerRadius = new CornerRadius(radius), Padding = pad ?? Th(10, 0), Margin = margin ?? Th(8, 1) };
        b.PointerEntered += (_, _) => { if (!selected) b.Background = Theme.Hover; };
        b.PointerExited += (_, _) => b.Background = bg;
        if (click is not null)
        {
            b.Tapped += (_, e) => { e.Handled = true; click(); };
            b.IsTabStop = false;
        }
        return b;
    }

    /// <summary>Szkielet wiadomości (pulsuje, dopóki nie przyjdzie treść): kółko awatara, krótka nazwa i linijka tekstu.</summary>
    public static FrameworkElement SkeletonMessage(int i, double avatar = 36)
    {
        double[] widths = [.62, .38, .74, .5, .66, .3];
        var bar = (double w, double h) => new Border { Height = h, Width = w, CornerRadius = new CornerRadius(h / 2), Background = W(.11), HorizontalAlignment = HorizontalAlignment.Left };
        var text = new Grid();
        text.SizeChanged += (_, e) => { if (text.Children.Count == 0 && e.NewSize.Width > 0) text.Children.Add(bar(Math.Max(60, e.NewSize.Width * widths[i % widths.Length]), 10)); };
        var col = V(8, bar(70 + (i * 37) % 60, 10), text);
        col.VerticalAlignment = VerticalAlignment.Center;
        var g = new Grid { Padding = Th(16, 8, 24, 8), ColumnSpacing = 14 }.Cols(avatar.ToString(System.Globalization.CultureInfo.InvariantCulture), "*").Add(
            new Microsoft.UI.Xaml.Shapes.Ellipse { Width = avatar, Height = avatar, Fill = W(.11) }.At(0, 0), col.At(0, 1));
        Ui.Motion.Pulse(g, i * 120);
        return g;
    }

    public static Border Badge(int n, Brush? bg = null) => new()
    {
        Background = bg ?? Theme.Red, CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18, Padding = Th(5, 0),
        Child = T(n > 99 ? "99+" : n.ToString(CultureInfo.InvariantCulture), 10.5, FontWeights.Bold, U.B("#ffffff")).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Rectangle HLine(double opacity = .07) => new() { Height = 1, Fill = U.W(opacity), HorizontalAlignment = HorizontalAlignment.Stretch };

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Enabled, HorizontalScrollMode = ScrollMode.Disabled,
    };

    /// <summary>Poziome przewijanie (rząd przycisków, który może nie zmieścić się w wąskim panelu).</summary>
    public static ScrollViewer Scroll2(UIElement content) => new()
    {
        Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
    };

    public static void Tip(this FrameworkElement e, string text)
    {
        ToolTipService.SetToolTip(e, text);
        if (string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(e))) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(e, text);
    }

    public static void Hand(this UIElement e) => e.With(_ => { });

    /// <summary>Tekst do pokazania w dymku błędu / informacji na dole okna.</summary>
    public static FontWeight Semi => FontWeights.SemiBold;
    public static FontWeight Bold => FontWeights.Bold;
}

/// <summary>Obrazki z sieci: wspólne BitmapImage dla tych samych adresów (awatary pojawiają się wiele razy).</summary>
public static class Images
{
    static readonly Dictionary<string, BitmapImage> _cache = [];
    static readonly LinkedList<string> _order = new();
    /// <summary>Nieudane pobrania: nowa próba najwcześniej po 3 minutach (404 nie może kręcić zapytań przy każdym odświeżeniu).</summary>
    static readonly Dictionary<string, long> _failedAt = [];

    static readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap> _blur = [];

    /// <summary>Rozmyty podgląd z BlurHash (mały obrazek, rozciągany — rozmycie wychodzi samo). Null przy złym napisie.</summary>
    public static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? Blur(string? hash, double aspect)
    {
        if (!GibCord.Core.BlurHash.IsValid(hash)) return null;
        int w = 32, h = (int)Math.Clamp(Math.Round(32 / Math.Max(.1, aspect)), 8, 32);
        var key = hash + "|" + h;
        if (_blur.TryGetValue(key, out var hit)) return hit;
        try
        {
            if (GibCord.Core.BlurHash.Decode(hash!, w, h) is not { } px) return null;
            var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(w, h);
            using (var s = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(wb.PixelBuffer)) s.Write(px, 0, px.Length);
            wb.Invalidate();
            if (_blur.Count > 300) _blur.Clear();
            _blur[key] = wb;
            return wb;
        }
        catch { return null; }
    }

    /// <summary>Czy obraz jest już wczytany (wtedy bez przenikania) albo się nie udał (wtedy też pokazujemy od razu).</summary>
    public static bool Ready(BitmapImage? b) => b is null || b.PixelWidth > 0 || _failedAt.Keys.Any(k => _cache.TryGetValue(k, out var x) && ReferenceEquals(x, b));

    public static BitmapImage? Get(Uri? uri, int decodeWidth = 0)
    {
        if (uri is null) return null;
        var key = uri.AbsoluteUri + "|" + decodeWidth;
        if (_cache.TryGetValue(key, out var b))
        {
            // poprzednio się nie udało (np. start przed Wi-Fi) — po 3 minutach próbujemy jeszcze raz
            if (!_failedAt.TryGetValue(key, out var t) || Environment.TickCount64 - t < 180_000) return b;
            _failedAt.Remove(key);
            _cache.Remove(key);
            _order.Remove(key);
        }
        b = new BitmapImage { UriSource = uri };
        if (decodeWidth > 0) b.DecodePixelWidth = decodeWidth;
        // nieudane pobranie (np. autostart przed Wi-Fi) nie może zostać w pamięci do końca sesji
        b.ImageFailed += (_, _) =>
        {
            if (_failedAt.Count > 500) _failedAt.Clear();
            _failedAt[key] = Environment.TickCount64;
        };
        _cache[key] = b;
        _order.AddLast(key);
        if (_order.Count > 600) { _cache.Remove(_order.First!.Value); _order.RemoveFirst(); }
        return b;
    }
}

/// <summary>Elementy w rzędach, zawijane do następnej linii (reakcje, kafelki).</summary>
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 6;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, y = 0, line = 0, maxW = 0;
        foreach (var c in Children)
        {
            c.Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > available.Width) { y += line + Spacing; x = 0; line = 0; }
            x += d.Width + Spacing;
            line = Math.Max(line, d.Height);
            maxW = Math.Max(maxW, x - Spacing);
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? maxW : Math.Min(maxW, available.Width), y + line);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var c in Children)
        {
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > final.Width) { y += line + Spacing; x = 0; line = 0; }
            c.Arrange(new Windows.Foundation.Rect(x, y, d.Width, d.Height));
            x += d.Width + Spacing;
            line = Math.Max(line, d.Height);
        }
        return final;
    }
}
