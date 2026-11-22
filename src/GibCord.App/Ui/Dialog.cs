using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>Kliknięcie przycisku okienka: <see cref="Cancel"/> = zostaw otwarte; <see cref="GetDeferral"/> = poczekaj (np. na wysyłkę).</summary>
sealed class DialogButtonArgs
{
    public bool Cancel { get; set; }
    readonly List<TaskCompletionSource> _wait = [];
    public Deferral GetDeferral() { var t = new TaskCompletionSource(); _wait.Add(t); return new Deferral(t); }
    internal Task WaitAll() => Task.WhenAll(_wait.Select(t => t.Task));

    public sealed class Deferral(TaskCompletionSource t) { public void Complete() => t.TrySetResult(); }
}

/// <summary>
/// Własne okienko GibCorda (wariant „A · Karta”) zamiast systemowego ContentDialog: karta na przyciemnionym tle,
/// ikona przy tytule, przyciski z prawej (czerwony tylko przy usuwaniu), Esc = Anuluj, Enter = przycisk domyślny.
/// API jak w ContentDialog, żeby podmiana w kodzie była prosta. Kilka okienek naraz jest dozwolone (układają się jedno na drugim).
/// </summary>
sealed class AppDialog
{
    /// <summary>Ile okienek jest teraz otwartych (skróty klawiszowe aplikacji czekają, aż zamkną się wszystkie).</summary>
    public static int OpenCount { get; private set; }

    public object? Title { get; set; }
    public object? Content { get; set; }
    public ContentDialogButton DefaultButton { get; set; }
    public XamlRoot? XamlRoot { get; set; }
    /// <summary>Tylko dla zgodności z ContentDialog — okienko zawsze ma barwy motywu GibCorda.</summary>
    public ElementTheme RequestedTheme { get; set; }
    /// <summary>Zgodność: „ContentDialogMaxWidth” / „ContentDialogMaxHeight” ustawiają rozmiar karty.</summary>
    /// <remarks>Zwykły słownik — ResourceDictionary podpowiadał domyślne wartości Windows (np. szerokość 548 px).</remarks>
    public Dictionary<string, object> Resources { get; } = [];
    public double MaxWidth { get; set; } = 560;
    public double MaxHeight { get; set; } = double.PositiveInfinity;
    /// <summary>Czerwony przycisk i ikona ostrzeżenia; null = zgadnij z napisu na przycisku („Usuń”, „Zbanuj”…).</summary>
    public bool? Danger { get; set; }
    /// <summary>Ikona w kafelku przy tytule (Segoe Fluent Icons); null = tylko przy niebezpiecznych akcjach.</summary>
    public string? Glyph { get; set; }

    public event TypedEventHandler<AppDialog, DialogButtonArgs>? PrimaryButtonClick, SecondaryButtonClick;
    public event TypedEventHandler<AppDialog, object?>? Opened, Closed;
    /// <summary>Klawisze w okienku (np. Ctrl+Z w edytorze zdjęć, przechwycenie klawisza „naciśnij, aby mówić”).</summary>
    public event KeyEventHandler? KeyDown, PreviewKeyDown;

    string? _primaryText, _secondaryText, _closeText;
    bool _primaryOn = true, _secondaryOn = true;
    Button? _primary, _secondary, _close;
    StackPanel? _buttons;
    TaskCompletionSource<ContentDialogResult>? _tcs;
    Grid? _layer;
    Popup? _popup;
    Border? _card;
    bool _done, _busy;
    UIElement? _focusBefore;

    public string? PrimaryButtonText { get => _primaryText; set { _primaryText = value; if (_buttons is not null) BuildButtons(); } }
    public string? SecondaryButtonText { get => _secondaryText; set { _secondaryText = value; if (_buttons is not null) BuildButtons(); } }
    public string? CloseButtonText { get => _closeText; set { _closeText = value; if (_buttons is not null) BuildButtons(); } }
    public bool IsPrimaryButtonEnabled { get => _primaryOn; set { _primaryOn = value; if (_primary is not null) _primary.IsEnabled = value; } }
    public bool IsSecondaryButtonEnabled { get => _secondaryOn; set { _secondaryOn = value; if (_secondary is not null) _secondary.IsEnabled = value; } }

    static readonly string[] DangerWords = ["Usuń", "Zbanuj", "Wyrzuć", "Zawieś", "Opuść", "Zablokuj", "Wyloguj", "Rozłącz", "Odrzuć", "Zakończ", "Wyczyść", "Zresetuj", "Cofnij dostęp"];

    bool IsDanger => Danger ?? (_primaryText is { Length: > 0 } t && DangerWords.Any(w => t.StartsWith(w, StringComparison.OrdinalIgnoreCase)));

    string? IconGlyph => Glyph ?? (!IsDanger ? null
        : _primaryText!.StartsWith("Usuń") || _primaryText.StartsWith("Wyczyść") ? ""
        : _primaryText.StartsWith("Zbanuj") || _primaryText.StartsWith("Zablokuj") || _primaryText.StartsWith("Zawieś") ? ""
        : _primaryText.StartsWith("Wyloguj") || _primaryText.StartsWith("Opuść") || _primaryText.StartsWith("Wyrzuć") ? ""
        : "");

    public Task<ContentDialogResult> ShowAsync()
    {
        if (_tcs is not null) return _tcs.Task;
        _tcs = new TaskCompletionSource<ContentDialogResult>();
        var root = XamlRoot ?? throw new InvalidOperationException("Okienko bez XamlRoot");
        if (Resources.TryGetValue("ContentDialogMaxWidth", out var mw) && mw is double w) MaxWidth = w;
        if (Resources.TryGetValue("ContentDialogMaxHeight", out var mh) && mh is double h) MaxHeight = h;
        _focusBefore = FocusManager.GetFocusedElement(root) as UIElement;

        var layer = _layer = Build();
        if (root.Content is Panel host)
        {
            // warstwa w samym oknie (nie w wyskakującym) — przykrywa wszystko, trafia też na zrzuty ekranu w CI
            Grid.SetRowSpan(layer, 64);
            Grid.SetColumnSpan(layer, 64);
            Canvas.SetZIndex(layer, 10_000 + OpenCount);
            host.Children.Add(layer);
        }
        else
        {
            _popup = new Popup { XamlRoot = root, Child = layer };
            void Fit() { layer.Width = root.Size.Width; layer.Height = root.Size.Height; }
            Fit();
            root.Changed += (_, _) => Fit();
            _popup.IsOpen = true;
        }
        OpenCount++;
        Motion.FadeIn(layer, 140);
        Motion.SlideIn(_card!, new System.Numerics.Vector3(0, 14, 0), 0, 1, .97f);
        layer.Loaded += (_, _) => FocusFirst();
        Opened?.Invoke(this, null);
        return _tcs.Task;
    }

    /// <summary>Jak kliknięcie głównego przycisku (np. podwójne kliknięcie pliku w oknie wyboru).</summary>
    public void PressPrimary() => Press(ContentDialogResult.Primary);

    /// <summary>Zamyka okienko bez wyboru (jak Esc).</summary>
    public void Hide() => Finish(ContentDialogResult.None);

    Grid Build()
    {
        var layer = new Grid { Background = U.B("#99080414"), TabFocusNavigation = KeyboardNavigationMode.Cycle };
        var danger = IsDanger;
        var glyph = IconGlyph;

        // tytuł (+ ikona) — krótki tekst treści stoi pod tytułem, obok ikony (jak w makiecie A)
        UIElement? body = Content switch
        {
            string s => U.T(s, 13.5, null, Theme.Mu, wrap: true),
            UIElement e => e,
            null => null,
            var o => U.T(o.ToString() ?? "", 13.5, null, Theme.Mu, wrap: true),
        };
        var shortText = body is TextBlock;
        if (body is TextBlock tb) { tb.FontSize = Math.Min(tb.FontSize, 13.5); if (ReferenceEquals(tb.Foreground, Theme.Fg) || tb.Foreground is null) tb.Foreground = U.W(.74); tb.LineHeight = 20; }

        var titleCol = U.V(6);
        if (Title is { } t) titleCol.Add(Title is UIElement te ? te : U.T(t.ToString() ?? "", 19, U.Bold, wrap: true).With(x => x.Margin = U.Th(0, glyph is not null && !shortText ? 8 : 1, 0, 0)));
        if (shortText && body is not null) titleCol.Add(body);

        var head = new Grid { ColumnSpacing = 14 }.Cols("auto", "*", "auto");
        if (glyph is not null)
        {
            var tile = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top,
                Background = danger ? U.Alpha("#ff6b81", .16) : Theme.AccentA(.16),
                Child = U.I(glyph, 19, danger ? U.B("#ff8fa0") : Theme.Accent).With(i => i.HorizontalAlignment = HorizontalAlignment.Center),
            };
            head.Add(tile.At(0, 0));
        }
        head.Add(titleCol.At(0, 1));
        head.Add(U.IconButton(G.Close, "Zamknij (Esc)", Hide, 13, 30).With(b => b.VerticalAlignment = VerticalAlignment.Top).At(0, 2));

        var grid = new Grid().Rows("auto", "*", "auto");
        grid.Add(head.At(0));
        if (!shortText && body is not null)
        {
            var sv = new ScrollViewer
            {
                Content = body, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = U.Th(0, 0, 4, 0), Margin = U.Th(0, 18, -4, 0),
            };
            grid.Add(sv.At(1));
        }
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = U.Th(0, shortText ? 22 : 20, 0, 0) };
        BuildButtons();
        grid.Add(_buttons.At(2));

        var card = _card = new Border
        {
            Background = Theme.Surface, CornerRadius = new CornerRadius(18), BorderBrush = U.W(.12), BorderThickness = U.Th(1), Padding = U.Th(24, 22, 24, 22),
            MinWidth = 380, MaxWidth = MaxWidth, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = grid,
        };
        card.Tapped += (_, e) => e.Handled = true;
        // zasoby przekazane jak do ContentDialog (np. kolory przycisków w edytorze zdjęć) trafiają do karty
        foreach (var (k, v) in Resources) if (k is not "ContentDialogMaxWidth" and not "ContentDialogMaxHeight") card.Resources[k] = v;
        layer.Add(card);
        layer.SizeChanged += (_, e) =>
        {
            card.MaxWidth = Math.Max(300, Math.Min(MaxWidth, e.NewSize.Width - 32));
            card.MaxHeight = Math.Max(200, Math.Min(MaxHeight, e.NewSize.Height - 40));
        };
        // klik obok karty nie zamyka (formularz by przepadł) — karta tylko lekko „podskakuje”, że czeka na wybór
        layer.Tapped += (_, e) => { if (ReferenceEquals(e.OriginalSource, layer)) Motion.Pop(card, .985f); };
        layer.PreviewKeyDown += (s, e) => PreviewKeyDown?.Invoke(s, e);
        layer.KeyDown += (s, e) => { KeyDown?.Invoke(s, e); OnKey(s, e); };
        return layer;
    }

    void BuildButtons()
    {
        if (_buttons is null) return;
        _buttons.Children.Clear();
        _primary = _secondary = _close = null;
        if (_closeText is { Length: > 0 } ct) _buttons.Add(_close = Ghost(ct, () => Press(ContentDialogResult.None)));
        if (_secondaryText is { Length: > 0 } st) _buttons.Add(_secondary = Ghost(st, () => Press(ContentDialogResult.Secondary)).With(b => b.IsEnabled = _secondaryOn));
        if (_primaryText is { Length: > 0 } pt)
        {
            var b = U.Primary(pt, () => Press(ContentDialogResult.Primary), 38);
            b.HorizontalAlignment = HorizontalAlignment.Right;
            b.CornerRadius = new CornerRadius(11);
            if (IsDanger)
            {
                b.Background = Theme.Red;
                b.Resources["ButtonBackgroundPointerOver"] = U.B("#ff8296");
                b.Resources["ButtonBackgroundPressed"] = Theme.Red2;
                b.Resources["ButtonBackgroundDisabled"] = Theme.Red;
                if (b.Content is TextBlock t) t.Foreground = U.B("#2a0f18");
            }
            b.IsEnabled = _primaryOn;
            _buttons.Add(_primary = b);
        }
        _buttons.Visibility = _buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static Button Ghost(string text, Action click) => U.Ghost(text, click, 38).With(b =>
    {
        b.CornerRadius = new CornerRadius(11);
        b.Padding = U.Th(18, 0);
        if (b.Content is TextBlock t) { t.FontSize = 13.5; t.FontWeight = U.Bold; }
    });

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == VirtualKey.Escape) { e.Handled = true; Hide(); return; }
        if (e.Key != VirtualKey.Enter || DefaultButton == ContentDialogButton.None) return;
        // Enter w polu wielowierszowym = nowa linia; na przycisku = ten przycisk
        if (e.OriginalSource is TextBox { AcceptsReturn: true } or RichEditBox or ButtonBase) return;
        e.Handled = true;
        Press(DefaultButton switch { ContentDialogButton.Primary => ContentDialogResult.Primary, ContentDialogButton.Secondary => ContentDialogResult.Secondary, _ => ContentDialogResult.None });
    }

    void FocusFirst()
    {
        if (_card is null || _done) return;
        // pierwsze pole w treści (np. pole tekstowe); przy potwierdzeniach — przycisk domyślny („Anuluj” przy usuwaniu)
        Control? target = DefaultButton switch
        {
            ContentDialogButton.Close when Content is string or TextBlock => _close,
            ContentDialogButton.Primary when Content is string or TextBlock => _primary,
            _ => null,
        };
        if (target is null && _card.Child is Grid g && g.Children.OfType<ScrollViewer>().FirstOrDefault() is { Content: DependencyObject body })
            target = FocusManager.FindFirstFocusableElement(body) as Control;
        target ??= DefaultButton == ContentDialogButton.Primary ? _primary : _close ?? _primary;
        target?.Focus(FocusState.Programmatic);
    }

    async void Press(ContentDialogResult r)
    {
        if (_done || _busy) return;
        var ev = r switch { ContentDialogResult.Primary => PrimaryButtonClick, ContentDialogResult.Secondary => SecondaryButtonClick, _ => null };
        if (ev is not null)
        {
            var a = new DialogButtonArgs();
            _busy = true;
            try { ev(this, a); await a.WaitAll(); }
            catch (Exception ex) { App.Log("Okienko: " + ex.Message); a.Cancel = true; }
            finally { _busy = false; }
            if (a.Cancel) return;
        }
        Finish(r);
    }

    void Finish(ContentDialogResult r)
    {
        if (_done || _tcs is null) return;
        _done = true;
        OpenCount = Math.Max(0, OpenCount - 1);
        var layer = _layer;
        if (layer is not null)
        {
            layer.IsHitTestVisible = false;
            Motion.FadeOut(layer, 120);
            _ = RemoveLater(layer);
        }
        try { Closed?.Invoke(this, null); } catch (Exception ex) { App.Log("Okienko (zamknięcie): " + ex.Message); }
        if (_focusBefore is { XamlRoot: not null } f) f.Focus(FocusState.Programmatic);
        _tcs.TrySetResult(r);
    }

    async Task RemoveLater(Grid layer)
    {
        await Task.Delay(Motion.On ? 130 : 0);
        if (_popup is not null) { _popup.IsOpen = false; _popup.Child = null; }
        else if (layer.Parent is Panel p) p.Children.Remove(layer);
    }
}
