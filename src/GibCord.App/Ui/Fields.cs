using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace GibCord.App.Ui;

// Własne pola wyboru GibCorda (wariant „A · Karta”) zamiast systemowych CheckBox / ComboBox / ToggleSwitch / Slider /
// RadioButtons / NumberBox / CalendarDatePicker / TimePicker. Właściwości i zdarzenia nazywają się jak w WinUI,
// żeby podmiana w kodzie była prosta.

/// <summary>Zmiana wartości (suwak, liczba, data, godzina).</summary>
sealed class ValueChange<T>(T oldValue, T newValue)
{
    public T OldValue { get; } = oldValue;
    public T NewValue { get; } = newValue;
    public T NewDate => NewValue;
    public T NewTime => NewValue;
}

/// <summary>Wspólne: nagłówek nad polem, przygaszenie wyłączonego, fokus z klawiatury.</summary>
abstract class FieldBase : UserControl
{
    protected readonly StackPanel Root = new() { Spacing = 6 };
    readonly ContentPresenter _header = new() { Visibility = Visibility.Collapsed };
    object? _headerValue;

    protected FieldBase()
    {
        Root.Children.Add(_header);
        base.Content = Root;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : .45;
    }

    /// <summary>Napis nad polem (tekst albo gotowy element).</summary>
    public object? Header
    {
        get => _headerValue;
        set
        {
            _headerValue = value;
            _header.Content = value switch { null => null, UIElement e => e, var o => U.T(o.ToString() ?? "", 12.5, U.Semi, U.W(.7)) };
            _header.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    protected static string TextOf(object? o) => o switch
    {
        null => "",
        string s => s,
        TextBlock t => t.Text,
        ContentControl { Content: var c } => TextOf(c),
        _ => o.ToString() ?? "",
    };

    protected static UIElement LabelOf(object? o, double size = 13.5) => o switch
    {
        UIElement e => e,
        var x => U.T(TextOf(x), size, null, Theme.Fg, wrap: true),
    };
}

/// <summary>Pole zaznaczenia: zaokrąglony kwadrat, zaznaczony = gradient akcentu z „ptaszkiem”.</summary>
sealed class CheckField : FieldBase
{
    readonly Border _box = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(6), BorderThickness = U.Th(2), VerticalAlignment = VerticalAlignment.Top, Margin = U.Th(0, 1, 0, 0) };
    readonly FontIcon _mark = U.I("", 12, Theme.OnAccent).With(i => { i.FontWeight = U.Bold; i.HorizontalAlignment = HorizontalAlignment.Center; });
    readonly ContentPresenter _label = new() { VerticalAlignment = VerticalAlignment.Center };
    bool? _on = false;
    object? _content;
    bool _hover;

    public event RoutedEventHandler? Checked, Unchecked, Click;

    public CheckField()
    {
        _box.Child = _mark;
        var row = new Grid { ColumnSpacing = 10, Background = Theme.Transparent }.Cols("auto", "*").Add(_box.At(0, 0), _label.At(0, 1));
        Root.Children.Add(row);
        PointerEntered += (_, _) => { _hover = true; Draw(); };
        PointerExited += (_, _) => { _hover = false; Draw(); };
        Tapped += (_, e) => { e.Handled = true; Toggle(); };
        KeyDown += (_, e) => { if (e.Key is VirtualKey.Space) { e.Handled = true; Toggle(); } };
        Draw();
    }

    public bool? IsChecked
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Draw();
            (value == true ? Checked : Unchecked)?.Invoke(this, new RoutedEventArgs());
        }
    }

    public new object? Content
    {
        get => _content;
        set
        {
            _content = value;
            _label.Content = value is null ? null : LabelOf(value);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, TextOf(value));
        }
    }

    void Toggle()
    {
        if (!IsEnabled) return;
        IsChecked = _on != true;
        if (_on == true) Motion.Pop(_box, .7f);
        Click?.Invoke(this, new RoutedEventArgs());
    }

    void Draw()
    {
        var on = _on != false;
        _box.Background = on ? Theme.AccentGradient() : _hover ? U.W(.06) : Theme.Transparent;
        _box.BorderBrush = on ? Theme.Transparent : _hover ? U.W(.6) : U.W(.35);
        _mark.Glyph = _on is null ? "" : "";
        _mark.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Przełącznik: pigułka z kółkiem, włączony = gradient akcentu.</summary>
sealed class SwitchField : FieldBase
{
    readonly Border _track = new() { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Padding = U.Th(3), BorderThickness = U.Th(0) };
    readonly Border _knob = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _state = U.T("", 13.5);
    bool _on, _hover, _shown;

    public event RoutedEventHandler? Toggled;
    public object? OnContent { get; set; }
    public object? OffContent { get; set; }

    public SwitchField()
    {
        _track.Child = _knob;
        var row = new Grid { ColumnSpacing = 10, Background = Theme.Transparent, HorizontalAlignment = HorizontalAlignment.Left }.Cols("auto", "auto").Add(_track.At(0, 0), _state.At(0, 1));
        Root.Children.Add(row);
        PointerEntered += (_, _) => { _hover = true; Draw(false); };
        PointerExited += (_, _) => { _hover = false; Draw(false); };
        Tapped += (_, e) => { e.Handled = true; Flip(); };
        KeyDown += (_, e) => { if (e.Key is VirtualKey.Space or VirtualKey.Enter) { e.Handled = true; Flip(); } };
        Loaded += (_, _) => { _shown = true; Draw(false); };
        Draw(false);
    }

    public bool IsOn
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Draw(_shown);
            Toggled?.Invoke(this, new RoutedEventArgs());
        }
    }

    void Flip() { if (IsEnabled) IsOn = !_on; }

    void Draw(bool animate)
    {
        _track.Background = _on ? Theme.AccentGradient() : _hover ? U.W(.2) : U.W(.14);
        _knob.Background = _on ? Theme.OnAccent : U.W(_hover ? .9 : .75);
        var x = _on ? 18f : 0f;
        if (animate) Motion.MoveTo(_knob, new Vector3(_on ? 0 : 18, 0, 0), new Vector3(x, 0, 0), true);
        else _knob.Translation = new Vector3(x, 0, 0);
        var label = TextOf(_on ? OnContent : OffContent);
        _state.Text = label;
        _state.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Wyskakujące karty pod polem (lista, kalendarz, godzina): pozycja pod polem albo nad nim, zamykanie klikiem obok.</summary>
static class DropCard
{
    public static Border Card(UIElement child, double pad = 5) => new()
    {
        Child = child, Background = Theme.Surface, CornerRadius = new CornerRadius(12), Padding = U.Th(pad),
        BorderBrush = U.W(.12), BorderThickness = U.Th(1),
    };

    public static Popup Open(FrameworkElement anchor, Border card, Action? closed = null, double minWidth = 0)
    {
        var root = anchor.XamlRoot;
        var popup = new Popup { XamlRoot = root, IsLightDismissEnabled = true, LightDismissOverlayMode = LightDismissOverlayMode.Off, Child = card };
        if (minWidth > 0) card.MinWidth = minWidth;
        card.Measure(new Size(root.Size.Width - 16, root.Size.Height - 16));
        var size = card.DesiredSize;
        var p = anchor.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var below = p.Y + anchor.ActualHeight + 4;
        var up = below + size.Height > root.Size.Height - 8 && p.Y - size.Height - 4 >= 8;
        popup.VerticalOffset = up ? p.Y - size.Height - 4 : Math.Max(8, Math.Min(below, root.Size.Height - size.Height - 8));
        popup.HorizontalOffset = Math.Max(8, Math.Min(p.X, root.Size.Width - size.Width - 8));
        popup.Closed += (_, _) => closed?.Invoke();
        popup.IsOpen = true;
        Motion.SlideIn(card, new Vector3(0, up ? 6 : -6, 0));
        return popup;
    }
}

/// <summary>Lista rozwijana: pole z obramowaniem i strzałką, lista jako ciemna karta z „ptaszkiem” przy wybranym.</summary>
sealed class DropField : FieldBase
{
    readonly Border _button = new() { CornerRadius = new CornerRadius(11), BorderThickness = U.Th(1), Padding = U.Th(12, 0, 10, 0), MinHeight = 38, MinWidth = 120 };
    readonly Grid _sizer = new();
    readonly TextBlock _text = U.T("", 13.5);
    readonly FontIcon _chev = U.I("", 11, Theme.Mu);
    int _index = -1;
    Popup? _popup;
    int _hot = -1;
    readonly List<Border> _rows = [];
    bool _hover;

    public ObservableCollection<object> Items { get; } = [];
    public string? PlaceholderText { get; set { field = value; DrawText(); } }
    public event SelectionChangedEventHandler? SelectionChanged;
    /// <summary>Lista otwarta przy krótkich listach rozwijanych: obsługa jak w ComboBox (zgodność).</summary>
    public bool IsDropDownOpen { get => _popup?.IsOpen == true; set { if (value) OpenList(); else _popup?.IsOpen = false; } }

    public DropField()
    {
        _text.TextTrimming = TextTrimming.CharacterEllipsis;
        _sizer.Children.Add(_text);
        _button.Child = new Grid { ColumnSpacing = 10 }.Cols("*", "auto").Add(_sizer.At(0, 0), _chev.At(0, 1));
        Root.Children.Add(_button);
        HorizontalAlignment = HorizontalAlignment.Left;
        Items.CollectionChanged += (_, _) =>
        {
            if (_index >= Items.Count) { _index = -1; }
            DrawText();
            BuildSizer();
        };
        _button.PointerEntered += (_, _) => { _hover = true; DrawFrame(); };
        _button.PointerExited += (_, _) => { _hover = false; DrawFrame(); };
        _button.Tapped += (_, e) => { e.Handled = true; if (IsEnabled) OpenList(); };
        KeyDown += OnKey;
        DrawFrame();
        DrawText();
    }

    public int SelectedIndex
    {
        get => _index;
        set
        {
            var v = value < 0 || value >= Items.Count ? -1 : value;
            if (v == _index) return;
            var old = _index >= 0 && _index < Items.Count ? Items[_index] : null;
            _index = v;
            DrawText();
            SelectionChanged?.Invoke(this, new SelectionChangedEventArgs(old is null ? [] : [old], v >= 0 ? [Items[v]] : []));
        }
    }

    public object? SelectedItem
    {
        get => _index >= 0 && _index < Items.Count ? Items[_index] : null;
        set => SelectedIndex = value is null ? -1 : Items.IndexOf(value);
    }

    void DrawText()
    {
        var has = _index >= 0 && _index < Items.Count;
        _text.Text = has ? TextOf(Items[_index]) : PlaceholderText ?? "";
        _text.Foreground = has ? Theme.Fg : Theme.Mu2;
    }

    // szerokość jak najdłuższa pozycja (jak w ComboBox), chyba że pole jest rozciągnięte
    void BuildSizer()
    {
        while (_sizer.Children.Count > 1) _sizer.Children.RemoveAt(1);
        if (Items.Count > 40) return;
        foreach (var it in Items) _sizer.Children.Add(U.T(TextOf(it), 13.5).With(t => { t.Opacity = 0; t.IsHitTestVisible = false; }));
    }

    void DrawFrame()
    {
        var open = _popup?.IsOpen == true;
        _button.BorderBrush = open ? Theme.Accent : _hover ? U.W(.28) : U.W(.12);
        _button.Background = _hover || open ? U.W(.09) : U.W(.06);
        _chev.Glyph = open ? "" : "";
    }

    void OpenList()
    {
        if (_popup?.IsOpen == true || Items.Count == 0 || XamlRoot is null) return;
        _rows.Clear();
        var list = new StackPanel { Spacing = 2 };
        for (var i = 0; i < Items.Count; i++)
        {
            var idx = i;
            var on = i == _index;
            var row = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = U.Th(10, 8), Background = on ? Theme.AccentA(.16) : Theme.Transparent,
                Child = new Grid { ColumnSpacing = 10 }.Cols("*", "auto").Add(
                    U.T(TextOf(Items[i]), 13, on ? U.Semi : null, on ? Theme.Fg : U.W(.85)).At(0, 0),
                    on ? U.I("", 12, Theme.Accent).With(x => x.FontWeight = U.Bold).At(0, 1) : null),
            };
            row.PointerEntered += (_, _) => Hot(idx);
            row.Tapped += (_, e) => { e.Handled = true; Pick(idx); };
            _rows.Add(row);
            list.Children.Add(row);
        }
        var sv = new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var card = DropCard.Card(sv);
        _hot = _index;
        _popup = DropCard.Open(_button, card, () => { DrawFrame(); Focus(FocusState.Programmatic); }, _button.ActualWidth);
        DrawFrame();
        if (_index > 0) card.Loaded += (_, _) => _rows[_index].StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = .5, AnimationDesired = false });
    }

    void Hot(int i)
    {
        if (_hot >= 0 && _hot < _rows.Count) _rows[_hot].Background = _hot == _index ? Theme.AccentA(.16) : Theme.Transparent;
        _hot = i;
        if (_hot >= 0 && _hot < _rows.Count)
        {
            _rows[_hot].Background = _hot == _index ? Theme.AccentA(.22) : U.W(.07);
            _rows[_hot].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    void Pick(int i)
    {
        _popup?.IsOpen = false;
        SelectedIndex = i;
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!IsEnabled || Items.Count == 0) return;
        var open = _popup?.IsOpen == true;
        switch (e.Key)
        {
            case VirtualKey.Down or VirtualKey.Up:
                var d = e.Key == VirtualKey.Down ? 1 : -1;
                if (open) Hot(Math.Clamp((_hot < 0 ? _index : _hot) + d, 0, Items.Count - 1));
                else SelectedIndex = Math.Clamp(_index + d, 0, Items.Count - 1);
                e.Handled = true;
                break;
            case VirtualKey.Enter or VirtualKey.Space:
                if (open) Pick(_hot >= 0 ? _hot : _index); else OpenList();
                e.Handled = true;
                break;
            case VirtualKey.Escape when open:
                _popup!.IsOpen = false;
                e.Handled = true;
                break;
        }
    }
}

/// <summary>Wybór jednej opcji: kółka (zaznaczone = gruba obwódka akcentu).</summary>
sealed class RadioField : FieldBase
{
    readonly StackPanel _col = new() { Spacing = 10 };
    readonly WrapPanel _row = new() { Spacing = 18, Visibility = Visibility.Collapsed };
    int _index = -1;
    public ObservableCollection<object> Items { get; } = [];
    public event SelectionChangedEventHandler? SelectionChanged;
    /// <summary>1 = opcje jedna pod drugą, więcej = obok siebie.</summary>
    public int MaxColumns { get; set { field = value; Rebuild(); } } = 1;

    public RadioField()
    {
        Root.Children.Add(_col);
        Root.Children.Add(_row);
        Items.CollectionChanged += (_, _) => Rebuild();
        KeyDown += (_, e) =>
        {
            if (Items.Count == 0) return;
            if (e.Key is VirtualKey.Down or VirtualKey.Right) { SelectedIndex = Math.Min(Items.Count - 1, _index + 1); e.Handled = true; }
            if (e.Key is VirtualKey.Up or VirtualKey.Left) { SelectedIndex = Math.Max(0, _index - 1); e.Handled = true; }
        };
    }

    public int SelectedIndex
    {
        get => _index;
        set
        {
            var v = value < 0 || value >= Items.Count ? -1 : value;
            if (v == _index) return;
            var old = _index >= 0 && _index < Items.Count ? Items[_index] : null;
            _index = v;
            Rebuild();
            SelectionChanged?.Invoke(this, new SelectionChangedEventArgs(old is null ? [] : [old], v >= 0 ? [Items[v]] : []));
        }
    }

    public object? SelectedItem
    {
        get => _index >= 0 && _index < Items.Count ? Items[_index] : null;
        set => SelectedIndex = value is null ? -1 : Items.IndexOf(value);
    }

    void Rebuild()
    {
        var wide = MaxColumns > 1;
        Panel list = wide ? _row : _col;
        _col.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        _row.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        _col.Children.Clear();
        _row.Children.Clear();
        for (var i = 0; i < Items.Count; i++)
        {
            var idx = i;
            var on = i == _index;
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = U.Th(on ? 5 : 2), BorderBrush = on ? Theme.Accent : U.W(.35), Background = Theme.Transparent, VerticalAlignment = VerticalAlignment.Top, Margin = U.Th(0, 1, 0, 0) };
            var row = new Grid { ColumnSpacing = 8, Background = Theme.Transparent }.Cols("auto", "*").Add(dot.At(0, 0), new ContentPresenter { Content = LabelOf(Items[i]), VerticalAlignment = VerticalAlignment.Center }.At(0, 1));
            row.PointerEntered += (_, _) => { if (!on) dot.BorderBrush = U.W(.6); };
            row.PointerExited += (_, _) => { if (!on) dot.BorderBrush = U.W(.35); };
            row.Tapped += (_, e) => { e.Handled = true; if (IsEnabled) { SelectedIndex = idx; Focus(FocusState.Pointer); } };
            list.Children.Add(row);
        }
    }
}

/// <summary>Suwak: cienka ścieżka, wypełnienie gradientem akcentu, kółko do przeciągania.</summary>
sealed class SliderField : FieldBase
{
    readonly Grid _area = new() { Height = 28, Background = Theme.Transparent };
    readonly Border _track = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = U.W(.14), VerticalAlignment = VerticalAlignment.Center };
    readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _thumb = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, BorderThickness = U.Th(4) };
    readonly ToolTip _tip = new();
    double _value, _min, _max = 100;
    bool _drag;

    public double StepFrequency { get; set; } = 1;
    public event TypedEventHandler<SliderField, ValueChange<double>>? ValueChanged;
    /// <summary>Jak wyświetlać wartość w dymku przy przeciąganiu (domyślnie liczba).</summary>
    public Func<double, string>? Format { get; set; }

    public SliderField()
    {
        _fill.Background = Theme.AccentGradient();
        _thumb.Background = Theme.Fg;
        _thumb.BorderBrush = Theme.Accent;
        _area.Children.Add(_track);
        _area.Children.Add(_fill);
        _area.Children.Add(_thumb);
        Root.Children.Add(_area);
        ToolTipService.SetToolTip(_thumb, _tip);
        _area.SizeChanged += (_, _) => Draw();
        _area.PointerPressed += (_, e) => { if (!IsEnabled) return; _drag = _area.CapturePointer(e.Pointer); SetFrom(e.GetCurrentPoint(_area).Position.X); Focus(FocusState.Pointer); e.Handled = true; };
        _area.PointerMoved += (_, e) => { if (_drag) SetFrom(e.GetCurrentPoint(_area).Position.X); };
        _area.PointerReleased += (_, e) => { _drag = false; _area.ReleasePointerCapture(e.Pointer); };
        _area.PointerCaptureLost += (_, _) => _drag = false;
        _area.PointerEntered += (_, _) => Motion.ScaleTo(_thumb, 1.12f);
        _area.PointerExited += (_, _) => Motion.ScaleTo(_thumb, 1);
        KeyDown += (_, e) =>
        {
            var step = Math.Max(StepFrequency, (_max - _min) / 100);
            if (e.Key is VirtualKey.Right or VirtualKey.Up) { Value += step; e.Handled = true; }
            if (e.Key is VirtualKey.Left or VirtualKey.Down) { Value -= step; e.Handled = true; }
            if (e.Key is VirtualKey.Home) { Value = _min; e.Handled = true; }
            if (e.Key is VirtualKey.End) { Value = _max; e.Handled = true; }
        };
        PointerWheelChanged += (_, e) =>
        {
            if (!IsEnabled || FocusState == FocusState.Unfocused) return; // kółko tylko na aktywnym suwaku — przewijanie strony nie zmienia głośności
            Value += Math.Sign(e.GetCurrentPoint(this).Properties.MouseWheelDelta) * Math.Max(StepFrequency, (_max - _min) / 50);
            e.Handled = true;
        };
    }

    public double Minimum { get => _min; set { _min = value; Value = _value; Draw(); } }
    public double Maximum { get => _max; set { _max = value; Value = _value; Draw(); } }

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, _min, Math.Max(_min, _max));
            if (StepFrequency > 0) v = Math.Clamp(_min + Math.Round((v - _min) / StepFrequency) * StepFrequency, _min, Math.Max(_min, _max));
            if (v == _value) return;
            var old = _value;
            _value = v;
            Draw();
            ValueChanged?.Invoke(this, new ValueChange<double>(old, v));
        }
    }

    void SetFrom(double x)
    {
        var w = Math.Max(1, _area.ActualWidth - _thumb.Width);
        Value = _min + Math.Clamp((x - _thumb.Width / 2) / w, 0, 1) * (_max - _min);
    }

    void Draw()
    {
        var w = Math.Max(0, _area.ActualWidth - _thumb.Width);
        var t = _max > _min ? (_value - _min) / (_max - _min) : 0;
        _thumb.Margin = U.Th(t * w, 0, 0, 0);
        _fill.Width = Math.Max(0, t * w + _thumb.Width / 2);
        _tip.Content = Format?.Invoke(_value) ?? Math.Round(_value).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Liczba: pole tekstowe z przyciskami − i +.</summary>
sealed class NumberField : FieldBase
{
    readonly TextBox _box = new() { TextAlignment = TextAlignment.Center, MinWidth = 64, VerticalAlignment = VerticalAlignment.Center };
    double _value, _min = double.MinValue, _max = double.MaxValue;

    public event TypedEventHandler<NumberField, ValueChange<double>>? ValueChanged;
    /// <summary>Zgodność z NumberBox — przyciski −/+ są zawsze obok pola.</summary>
    public NumberBoxSpinButtonPlacementMode SpinButtonPlacementMode { get; set; }

    public NumberField()
    {
        IsTabStop = false;
        _box.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
        var minus = U.IconButton("", "Mniej", () => Value -= 1, 12, 34);
        var plus = U.IconButton("", "Więcej", () => Value += 1, 12, 34);
        minus.Background = plus.Background = U.W(.08);
        var row = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left }.Cols("auto", "*", "auto").Add(minus.At(0, 0), _box.At(0, 1), plus.At(0, 2));
        Root.Children.Add(row);
        _box.LostFocus += (_, _) => Commit();
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) Commit();
            if (e.Key == VirtualKey.Up) { Value += 1; e.Handled = true; }
            if (e.Key == VirtualKey.Down) { Value -= 1; e.Handled = true; }
        };
        Draw();
    }

    public double Minimum { get => _min; set { _min = value; Value = _value; } }
    public double Maximum { get => _max; set { _max = value; Value = _value; } }

    public double Value
    {
        get => _value;
        set
        {
            var v = double.IsNaN(value) ? _min : Math.Clamp(value, _min, Math.Max(_min, _max));
            if (v == _value) { Draw(); return; }
            var old = _value;
            _value = v;
            Draw();
            ValueChanged?.Invoke(this, new ValueChange<double>(old, v));
        }
    }

    void Commit()
    {
        if (double.TryParse(_box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Value = Math.Round(v);
        else Draw();
    }

    void Draw() => _box.Text = _value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Pole „otwiera kartę” (data, godzina): wygląda jak lista rozwijana, z ikoną z lewej.</summary>
abstract class PickFieldBase : FieldBase
{
    protected readonly Border Button = new() { CornerRadius = new CornerRadius(11), BorderThickness = U.Th(1), Padding = U.Th(12, 0), MinHeight = 38, MinWidth = 140 };
    protected readonly TextBlock Text = U.T("", 13.5);
    protected Popup? Popup;
    bool _hover;

    protected PickFieldBase(string glyph)
    {
        Button.Child = U.H(10, U.I(glyph, 14, Theme.Accent), Text);
        Root.Children.Add(Button);
        HorizontalAlignment = HorizontalAlignment.Left;
        Button.PointerEntered += (_, _) => { _hover = true; DrawFrame(); };
        Button.PointerExited += (_, _) => { _hover = false; DrawFrame(); };
        Button.Tapped += (_, e) => { e.Handled = true; if (IsEnabled) Open(); };
        KeyDown += (_, e) => { if (e.Key is VirtualKey.Enter or VirtualKey.Space && IsEnabled) { e.Handled = true; Open(); } };
        DrawFrame();
    }

    protected void DrawFrame()
    {
        var open = Popup?.IsOpen == true;
        Button.BorderBrush = open ? Theme.Accent : _hover ? U.W(.28) : U.W(.12);
        Button.Background = _hover || open ? U.W(.09) : U.W(.06);
    }

    protected abstract Border BuildCard();

    void Open()
    {
        if (Popup?.IsOpen == true || XamlRoot is null) return;
        Popup = DropCard.Open(Button, BuildCard(), () => { DrawFrame(); Focus(FocusState.Programmatic); });
        DrawFrame();
    }

    protected void Close() => Popup?.IsOpen = false;

    /// <summary>Zrzut w CI: otwarta karta (kalendarz / godzina).</summary>
    public void OpenDemo() => Open();

    protected static Border Cell(string text, bool on, bool today, bool enabled, Action click, double w = 36, double h = 34)
    {
        var b = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(9),
            Background = on ? Theme.AccentGradient() : Theme.Transparent,
            BorderBrush = today && !on ? Theme.Accent : Theme.Transparent, BorderThickness = U.Th(today && !on ? 1 : 0),
            Child = U.T(text, 13, on ? U.Bold : null, on ? Theme.OnAccent : enabled ? Theme.Fg : U.W(.25)).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
        };
        if (!enabled) return b;
        b.PointerEntered += (_, _) => { if (!on) b.Background = U.W(.08); };
        b.PointerExited += (_, _) => { if (!on) b.Background = Theme.Transparent; };
        b.Tapped += (_, e) => { e.Handled = true; click(); };
        return b;
    }
}

/// <summary>Data: kalendarz z miesiącami po polsku (tydzień od poniedziałku), klik w nagłówek = wybór roku.</summary>
sealed class DateField : PickFieldBase
{
    static readonly CultureInfo Pl = new("pl-PL");
    DateTimeOffset? _date;
    DateTime _month = DateTime.Today;
    bool _years;

    public DateTimeOffset MinDate { get; set; } = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset MaxDate { get; set; } = new(2100, 12, 31, 0, 0, 0, TimeSpan.Zero);
    public string? PlaceholderText { get; set { field = value; Draw(); } } = "Wybierz datę";
    public event TypedEventHandler<DateField, ValueChange<DateTimeOffset?>>? DateChanged;

    public DateField() : base("") => Draw();

    public DateTimeOffset? Date
    {
        get => _date;
        set
        {
            if (_date?.Date == value?.Date && _date.HasValue == value.HasValue) return;
            var old = _date;
            _date = value;
            Draw();
            DateChanged?.Invoke(this, new ValueChange<DateTimeOffset?>(old, value));
        }
    }

    void Draw()
    {
        Text.Text = _date is { } d ? d.Date.ToString("d MMMM yyyy", Pl) : PlaceholderText ?? "";
        Text.Foreground = _date is null ? Theme.Mu2 : Theme.Fg;
    }

    protected override Border BuildCard()
    {
        _month = new DateTime((_date?.Date ?? Clamp(DateTime.Today)).Year, (_date?.Date ?? Clamp(DateTime.Today)).Month, 1);
        _years = false;
        var host = new Border { Width = 7 * 38 };
        var card = DropCard.Card(host, 10);
        void Redraw() { host.Child = _years ? Years(Redraw) : Month(Redraw); }
        Redraw();
        return card;
    }

    DateTime Clamp(DateTime d) => d < MinDate.Date ? MinDate.Date : d > MaxDate.Date ? MaxDate.Date : d;

    UIElement Month(Action redraw)
    {
        var title = Pl.DateTimeFormat.GetMonthName(_month.Month);
        var head = new Grid().Cols("auto", "*", "auto");
        var prevOk = _month > new DateTime(MinDate.Year, MinDate.Month, 1);
        var nextOk = _month < new DateTime(MaxDate.Year, MaxDate.Month, 1);
        head.Add(U.IconButton("", "Poprzedni miesiąc", () => { if (prevOk) { _month = _month.AddMonths(-1); redraw(); } }, 12, 32).With(b => b.IsEnabled = prevOk).At(0, 0));
        var label = new Border
        {
            Background = Theme.Transparent, CornerRadius = new CornerRadius(8), Padding = U.Th(8, 4), HorizontalAlignment = HorizontalAlignment.Center,
            Child = U.H(6, U.T(char.ToUpper(title[0]) + title[1..] + " " + _month.Year, 14, U.Bold), U.I("", 10, Theme.Mu)),
        };
        label.PointerEntered += (_, _) => label.Background = U.W(.08);
        label.PointerExited += (_, _) => label.Background = Theme.Transparent;
        label.Tapped += (_, e) => { e.Handled = true; _years = true; redraw(); };
        ToolTipService.SetToolTip(label, "Wybierz rok");
        head.Add(label.At(0, 1));
        head.Add(U.IconButton("", "Następny miesiąc", () => { if (nextOk) { _month = _month.AddMonths(1); redraw(); } }, 12, 32).With(b => b.IsEnabled = nextOk).At(0, 2));

        var grid = new Grid { RowSpacing = 2, ColumnSpacing = 2, Margin = U.Th(0, 8, 0, 0) };
        for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        var names = new[] { "pn", "wt", "śr", "cz", "pt", "sb", "nd" };
        for (var c = 0; c < 7; c++) grid.Add(U.T(names[c], 11, U.Bold, Theme.Mu2).With(t => t.HorizontalAlignment = HorizontalAlignment.Center).At(0, c));
        var first = ((int)_month.DayOfWeek + 6) % 7;
        var days = DateTime.DaysInMonth(_month.Year, _month.Month);
        var rows = (first + days + 6) / 7;
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        for (var d = 1; d <= days; d++)
        {
            var day = new DateTime(_month.Year, _month.Month, d);
            var i = first + d - 1;
            var ok = day >= MinDate.Date && day <= MaxDate.Date;
            grid.Add(Cell(d.ToString(), _date?.Date == day, day == DateTime.Today, ok, () =>
            {
                Date = new DateTimeOffset(day);
                Close();
            }).At(1 + i / 7, i % 7));
        }
        var today = DateTime.Today;
        var foot = new Grid { Margin = U.Th(0, 6, 0, 0) }.Cols("*", "auto");
        if (today >= MinDate.Date && today <= MaxDate.Date)
            foot.Add(new HyperlinkButton { Content = U.T("Dziś", 12.5, U.Semi, Theme.Accent), Padding = U.Th(6, 2) }.With(h => h.Click += (_, _) => { Date = new DateTimeOffset(today); Close(); }).At(0, 1));
        return U.V(0, head, grid, foot);
    }

    UIElement Years(Action redraw)
    {
        var wrap = new WrapPanel { Spacing = 4 };
        Border? current = null;
        for (var y = MaxDate.Year; y >= MinDate.Year; y--)
        {
            var yy = y;
            var cell = Cell(y.ToString(), y == _month.Year, y == DateTime.Today.Year, true, () =>
            {
                _month = new DateTime(yy, Math.Clamp(_month.Month, yy == MinDate.Year ? MinDate.Month : 1, yy == MaxDate.Year ? MaxDate.Month : 12), 1);
                _years = false;
                redraw();
            }, 60, 34);
            if (y == _month.Year) current = cell;
            wrap.Children.Add(cell);
        }
        var sv = new ScrollViewer { Content = wrap, Height = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (current is not null) sv.Loaded += (_, _) => current.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = .5, AnimationDesired = false });
        return U.V(8, U.T("Wybierz rok", 14, U.Bold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center), sv);
    }
}

/// <summary>Godzina: dwie kolumny (godziny i minuty co <see cref="MinuteIncrement"/>), format 24-godzinny.</summary>
sealed class TimeField : PickFieldBase
{
    TimeSpan _time;
    public int MinuteIncrement { get; set; } = 1;
    /// <summary>Zgodność z TimePicker — zawsze 24 godziny.</summary>
    public string? ClockIdentifier { get; set; }
    public event TypedEventHandler<TimeField, ValueChange<TimeSpan>>? TimeChanged;

    public TimeField() : base("")
    {
        MinWidth = 0;
        Button.MinWidth = 110;
        Draw();
    }

    public TimeSpan Time
    {
        get => _time;
        set
        {
            var v = new TimeSpan(value.Hours, value.Minutes, 0);
            if (v == _time) return;
            var old = _time;
            _time = v;
            Draw();
            TimeChanged?.Invoke(this, new ValueChange<TimeSpan>(old, v));
        }
    }

    public TimeSpan? SelectedTime { get => _time; set => Time = value ?? TimeSpan.Zero; }

    void Draw() => Text.Text = $"{_time.Hours:00}:{_time.Minutes:00}";

    protected override Border BuildCard()
    {
        var step = Math.Clamp(MinuteIncrement, 1, 30);
        var mins = Enumerable.Range(0, 60 / step).Select(i => i * step).ToList();
        if (!mins.Contains(_time.Minutes)) mins = [.. mins.Append(_time.Minutes).Order()];
        var h = _time.Hours;
        var m = _time.Minutes;
        var hours = Column(Enumerable.Range(0, 24).ToList(), () => h, v => { h = v; Time = new TimeSpan(h, m, 0); });
        var minutes = Column(mins, () => m, v => { m = v; Time = new TimeSpan(h, m, 0); Close(); });
        var body = U.V(8,
            new Grid { ColumnSpacing = 6 }.Cols("*", "*").Add(U.T("godz.", 11, U.Bold, Theme.Mu2).With(t => t.HorizontalAlignment = HorizontalAlignment.Center).At(0, 0), U.T("min", 11, U.Bold, Theme.Mu2).With(t => t.HorizontalAlignment = HorizontalAlignment.Center).At(0, 1)),
            new Grid { ColumnSpacing = 6 }.Cols("*", "*").Add(hours.At(0, 0), minutes.At(0, 1)));
        return DropCard.Card(body, 8);
    }

    ScrollViewer Column(List<int> values, Func<int> current, Action<int> pick)
    {
        var list = new StackPanel { Spacing = 2 };
        var sv = new ScrollViewer { Content = list, Height = 220, Width = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        void Fill()
        {
            list.Children.Clear();
            foreach (var v in values)
            {
                var vv = v;
                var cell = Cell(v.ToString("00"), v == current(), false, true, () => { pick(vv); Fill(); }, 60, 32);
                list.Children.Add(cell);
            }
        }
        Fill();
        sv.Loaded += (_, _) =>
        {
            var i = values.IndexOf(current());
            if (i >= 0) sv.ChangeView(null, Math.Max(0, i * 34 - 94), null, true);
        };
        return sv;
    }
}

/// <summary>Przycisk-przełącznik („chip”): narzędzia w edytorze zdjęć, role do podglądu uprawnień.</summary>
sealed class ChipField : FieldBase
{
    readonly Border _b = new() { BorderThickness = U.Th(1), CornerRadius = new CornerRadius(10), Padding = U.Th(10, 5) };
    bool? _on = false;
    bool _hover;
    public event RoutedEventHandler? Checked, Unchecked, Click;

    public ChipField()
    {
        Root.Children.Add(_b);
        Loaded += (_, _) =>
        {
            if (Padding != default) _b.Padding = Padding;
            if (CornerRadius != default) _b.CornerRadius = CornerRadius;
        };
        PointerEntered += (_, _) => { _hover = true; Draw(); };
        PointerExited += (_, _) => { _hover = false; Draw(); };
        Tapped += (_, e) => { e.Handled = true; Toggle(); };
        KeyDown += (_, e) => { if (e.Key is VirtualKey.Space or VirtualKey.Enter) { e.Handled = true; Toggle(); } };
        Motion.Pressable(this, .95f);
        Draw();
    }

    public new object? Content { get => _b.Child; set => _b.Child = LabelOf(value, 13); }

    public bool? IsChecked
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Draw();
            (value == true ? Checked : Unchecked)?.Invoke(this, new RoutedEventArgs());
        }
    }

    void Toggle()
    {
        if (!IsEnabled) return;
        IsChecked = _on != true;
        Click?.Invoke(this, new RoutedEventArgs());
    }

    void Draw()
    {
        var on = _on == true;
        _b.Background = on ? Theme.AccentA(_hover ? .28 : .2) : U.W(_hover ? .12 : .07);
        _b.BorderBrush = on ? Theme.Accent : U.W(.1);
    }
}
