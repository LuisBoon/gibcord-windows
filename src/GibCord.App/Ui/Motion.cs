using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace GibCord.App.Ui;

/// <summary>
/// Ruch w stylu „B · Sprężyście” (wybór użytkownika 21.11.2026, makieta https://claude.ai/artifact/1FumWrc1nDK1vTU5rCGLXJ):
/// sprężyny kompozycji (DampingRatio ≈ 0,72) na Translation/Scale elementów XAML, krótkie przenikanie (160 ms).
/// Wyłączone, gdy w ustawieniach jest „Mniej ruchu” albo gdy Windows ma wyłączone animacje.
/// Każda metoda jest odporna na błędy — animacja nigdy nie może wywrócić aplikacji.
/// </summary>
static class Motion
{
    static Windows.UI.ViewManagement.UISettings? _ui;
    static bool _sysOn = true;
    static long _sysAt;

    /// <summary>Czy animować (ustawienie „Mniej ruchu” + animacje w Windows; to drugie odświeżane co 5 s).</summary>
    public static bool On
    {
        get
        {
            if (Options.ForceMotion) return true; // CI (--motion): animacje mimo wyłączonych w systemie (na maszynach CI są wyłączone)
            if (App.Prefs.ReduceMotion || Options.Screenshot is not null) return false; // zrzuty w CI: bez ruchu, żeby nie złapać klatki w połowie
            var now = Environment.TickCount64;
            if (now - _sysAt > 5000)
            {
                _sysAt = now;
                try { _sysOn = (_ui ??= new()).AnimationsEnabled; } catch { _sysOn = true; }
            }
            return _sysOn;
        }
    }

    /// <summary>Ostatnie animacje elementu — żeby dało się je zatrzymać (np. widok wiadomości użyty ponownie w trakcie wjazdu).</summary>
    static readonly ConditionalWeakTable<UIElement, List<CompositionAnimation>> Running = new();

    static int _fails;
    /// <summary>Błąd animacji: zapis do dziennika (najwyżej 5 razy), aplikacja działa dalej bez ruchu w tym miejscu.</summary>
    static void Fail(Exception ex) { if (_fails++ < 5) App.Log("Ruch: " + ex.GetType().Name + ": " + ex.Message); }

    /// <summary>Do sprawdzenia w CI: ile animacji wystartowało.</summary>
    public static int Started { get; private set; }

    static Compositor Comp(UIElement e) => ElementCompositionPreview.GetElementVisual(e).Compositor;

    static void Start(UIElement e, CompositionAnimation a)
    {
        e.StartAnimation(a);
        Started++;
        var list = Running.GetOrCreateValue(e);
        list.RemoveAll(x => x.Target == a.Target);
        list.Add(a);
    }

    static SpringVector3NaturalMotionAnimation Spring(Compositor c, string target, Vector3 from, Vector3 to, double delayMs = 0, float damping = .72f, double periodMs = 45)
    {
        var a = c.CreateSpringVector3Animation();
        a.Target = target;
        a.InitialValue = from;
        a.FinalValue = to;
        a.DampingRatio = damping;
        a.Period = TimeSpan.FromMilliseconds(periodMs);
        if (delayMs > 0) { a.DelayTime = TimeSpan.FromMilliseconds(delayMs); a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay; }
        return a;
    }

    static ScalarKeyFrameAnimation Fade(Compositor c, float from, float to, double ms = 160, double delayMs = 0)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.Target = "Opacity";
        a.InsertKeyFrame(0f, from);
        a.InsertKeyFrame(1f, to, c.CreateCubicBezierEasingFunction(new Vector2(.2f, .7f), new Vector2(.2f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        if (delayMs > 0) { a.DelayTime = TimeSpan.FromMilliseconds(delayMs); a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay; }
        return a;
    }

    /// <summary>Wjazd: przesunięcie z <paramref name="from"/> do zera sprężyną + pojawienie się (0 → <paramref name="opacity"/>).</summary>
    public static void SlideIn(UIElement e, Vector3 from, double delayMs = 0, float opacity = 1, float scaleFrom = 1)
    {
        if (!On) return;
        try
        {
            var c = Comp(e);
            Start(e, Spring(c, "Translation", from, Vector3.Zero, delayMs));
            Start(e, Fade(c, 0, opacity, 160, delayMs));
            if (scaleFrom != 1 && e is FrameworkElement fe)
            {
                fe.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight, 0);
                Start(e, Spring(c, "Scale", new Vector3(scaleFrom, scaleFrom, 1), Vector3.One, delayMs));
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Nowa wiadomość: wjeżdża od dołu z lekkim odbiciem (jak w makiecie B).</summary>
    public static void MessageIn(FrameworkElement e, float opacity) => SlideIn(e, new Vector3(0, 18, 0), 0, opacity, .97f);

    /// <summary>Zatrzymuje trwające animacje elementu i wraca do spoczynku.</summary>
    public static void Reset(UIElement e)
    {
        if (!Running.TryGetValue(e, out var list) || list.Count == 0) return;
        try
        {
            foreach (var a in list) e.StopAnimation(a);
            list.Clear();
            e.Translation = Vector3.Zero;
            e.Scale = Vector3.One;
            // zatrzymane przenikanie zostawiało wartość „w pół drogi” — wymuszamy ponowne przyłożenie przezroczystości z XAML
            var o = e.Opacity;
            e.Opacity = o >= 1 ? .99 : 1;
            e.Opacity = o;
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Przesunięcie (Translation) do <paramref name="to"/> sprężyną z <paramref name="from"/>; bez ruchu — od razu na miejsce.</summary>
    public static void MoveTo(UIElement e, Vector3 from, Vector3 to, bool animate)
    {
        try
        {
            var c = Comp(e);
            Start(e, Spring(c, "Translation", animate && On ? from : to, to));
        }
        catch (Exception ex) { Fail(ex); e.Translation = to; }
    }

    /// <summary>Skala do <paramref name="to"/> sprężyną (środek elementu).</summary>
    public static void ScaleTo(FrameworkElement e, float to, float damping = .6f)
    {
        if (!On) { e.Scale = new Vector3(to, to, 1); return; }
        try
        {
            e.CenterPoint = new Vector3((float)e.ActualWidth / 2, (float)e.ActualHeight / 2, 0);
            var a = Comp(e).CreateSpringVector3Animation();
            a.Target = "Scale";
            a.FinalValue = new Vector3(to, to, 1);
            a.DampingRatio = damping;
            a.Period = TimeSpan.FromMilliseconds(40);
            Start(e, a);
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Przycisk „wciska się” przy naciśnięciu i sprężyście wraca.</summary>
    public static T Pressable<T>(T e, float pressed = .9f) where T : UIElement
    {
        if (e is not FrameworkElement fe) return e;
        fe.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => ScaleTo(fe, pressed, .9f)), true);
        PointerEventHandler up = (_, _) => ScaleTo(fe, 1f);
        fe.AddHandler(UIElement.PointerReleasedEvent, up, true);
        fe.AddHandler(UIElement.PointerCaptureLostEvent, up, true);
        fe.AddHandler(UIElement.PointerExitedEvent, up, true);
        return e;
    }

    // ── ikony serwerów (6.0.6): koło ↔ kwadrat i pasek z boku bez pracy w każdej klatce w wątku okna ──

    sealed class RoundState { public Microsoft.UI.Dispatching.DispatcherQueueTimer T = null!; public double From, To; public int Step; }
    static readonly ConditionalWeakTable<Border, RoundState> Rounds = new();

    /// <summary>
    /// Zaokrąglenie ikony serwera (koło ↔ kwadrat): 4 kroki co 28 ms z wyhamowaniem — zamiast ustawiania CornerRadius w każdej klatce
    /// (CompositionTarget.Rendering). Przycięcie przez kompozycję odpada: Windows nie pozwala zmieniać Clip warstwy elementu XAML.
    /// </summary>
    public static void RoundTo(Border e, double to, bool animate = true)
    {
        try
        {
            var st = Rounds.GetValue(e, b =>
            {
                var x = new RoundState { T = b.DispatcherQueue.CreateTimer() };
                x.T.Interval = TimeSpan.FromMilliseconds(28);
                x.T.Tick += (_, _) =>
                {
                    x.Step++;
                    var k = Math.Min(1, x.Step / 4.0);
                    b.CornerRadius = new CornerRadius(x.From + (x.To - x.From) * (1 - Math.Pow(1 - k, 3)));
                    if (k >= 1) x.T.Stop();
                };
                return x;
            });
            st.T.Stop();
            if (!animate || !On || Math.Abs(e.CornerRadius.TopLeft - to) < .5) { e.CornerRadius = new CornerRadius(to); return; }
            st.From = e.CornerRadius.TopLeft; st.To = to; st.Step = 0;
            st.T.Start();
        }
        catch (Exception ex) { Fail(ex); e.CornerRadius = new CornerRadius(to); }
    }

    /// <summary>Pasek przy ikonie serwera: wysokość <paramref name="to"/> z <paramref name="max"/> jako skala pionowa w kompozycji (bez przeliczania układu co klatkę).</summary>
    public static void BarTo(FrameworkElement e, double to, double max)
    {
        var s = (float)Math.Clamp(to / max, 0, 1);
        e.CenterPoint = new Vector3((float)e.Width / 2, (float)max / 2, 0);
        // przed pojawieniem się na ekranie bez animacji (StartAnimation na elemencie spoza drzewa = „Access is denied”)
        if (!On || !e.IsLoaded) { Reset(e); e.Scale = new Vector3(1, s, 1); return; }
        try
        {
            var a = Comp(e).CreateSpringVector3Animation();
            a.Target = "Scale";
            a.FinalValue = new Vector3(1, s, 1);
            a.DampingRatio = .55f;
            a.Period = TimeSpan.FromMilliseconds(45);
            Start(e, a);
        }
        catch (Exception ex) { Fail(ex); e.Scale = new Vector3(1, s, 1); }
    }

    /// <summary>Lekkie powiększenie po najechaniu (ikony serwerów).</summary>
    public static void Hoverable(FrameworkElement e, float hover = 1.06f)
    {
        e.PointerEntered += (_, _) => ScaleTo(e, hover);
        e.PointerExited += (_, _) => ScaleTo(e, 1f);
        e.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => ScaleTo(e, .94f, .9f)), true);
        e.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => ScaleTo(e, hover)), true);
    }

    // (6.0.6) usunięte: Height() — animacja wysokości z odbiciem (BackEase) przestrzeliwała poniżej zera przy zjeżdżaniu
    // z ikony serwera; ujemna wysokość = błąd w każdej klatce i zawieszone okno. Pasek: BarTo (skala w kompozycji).

    // ───────────────────────── szlif 6.0.3 ─────────────────────────

    /// <summary>Pojawienie się (0 → 1).</summary>
    public static void FadeIn(UIElement e, double ms = 160)
    {
        if (!On) return;
        try { Start(e, Fade(Comp(e), 0, 1, ms)); } catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Znikanie (bieżąca → 0) — np. przyciemnienie przy zamykaniu podglądu.</summary>
    public static void FadeOut(UIElement e, double ms = 180)
    {
        if (!On) return;
        try { Start(e, Fade(Comp(e), (float)e.Opacity, 0, ms)); } catch (Exception ex) { Fail(ex); }
    }

    /// <summary>„Pop”: skala od <paramref name="from"/> do 1 z mocnym odbiciem, ze środka elementu (gdy zna już swój rozmiar).</summary>
    public static void Pop(FrameworkElement e, float from = .4f)
    {
        if (!On) return;
        void Go()
        {
            try
            {
                e.CenterPoint = new Vector3((float)e.ActualWidth / 2, (float)e.ActualHeight / 2, 0);
                Start(e, Spring(Comp(e), "Scale", new Vector3(from, from, 1), Vector3.One, 0, .42f, 50));
            }
            catch (Exception ex) { Fail(ex); }
        }
        if (e.ActualWidth > 0) { Go(); return; }
        SizeChangedEventHandler? h = null;
        h = (_, a) => { if (a.NewSize.Width <= 0) return; e.SizeChanged -= h; Go(); };
        e.SizeChanged += h;
    }

    /// <summary>Przelot: element startuje przesunięty o <paramref name="tFrom"/> i w skali <paramref name="sFrom"/> (wokół <paramref name="center"/>) i sprężyście trafia w <paramref name="tTo"/>/<paramref name="sTo"/>.</summary>
    public static void Fly(UIElement e, Vector3 center, Vector3 tFrom, float sFrom, Vector3 tTo, float sTo, float damping = .82f)
    {
        try
        {
            var c = Comp(e);
            e.CenterPoint = center;
            Start(e, Spring(c, "Translation", tFrom, tTo, 0, damping, 55));
            Start(e, Spring(c, "Scale", new Vector3(sFrom, sFrom, 1), new Vector3(sTo, sTo, 1), 0, damping, 55));
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Bez końca: przezroczystość tam i z powrotem (szkielety przy ładowaniu). Przy „Mniej ruchu” stoi w miejscu.</summary>
    public static void Pulse(UIElement e, double delayMs = 0)
    {
        if (!On) { e.Opacity = .7; return; }
        try
        {
            var c = Comp(e);
            var a = c.CreateScalarKeyFrameAnimation();
            a.Target = "Opacity";
            a.InsertKeyFrame(0f, .4f);
            a.InsertKeyFrame(.5f, 1f);
            a.InsertKeyFrame(1f, .4f);
            a.Duration = TimeSpan.FromMilliseconds(1400);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            if (delayMs > 0) { a.DelayTime = TimeSpan.FromMilliseconds(delayMs); a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay; }
            Start(e, a);
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Bez końca: kropka „pisze…” podskakuje (z opóźnieniem dla kolejnych kropek).</summary>
    public static void Hop(UIElement e, double delayMs)
    {
        if (!On) return;
        try
        {
            var c = Comp(e);
            var a = c.CreateVector3KeyFrameAnimation();
            a.Target = "Translation";
            a.InsertKeyFrame(0f, Vector3.Zero);
            a.InsertKeyFrame(.25f, new Vector3(0, -4, 0));
            a.InsertKeyFrame(.5f, Vector3.Zero);
            a.InsertKeyFrame(1f, Vector3.Zero);
            a.Duration = TimeSpan.FromMilliseconds(1100);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            Start(e, a);
            var o = c.CreateScalarKeyFrameAnimation();
            o.Target = "Opacity";
            o.InsertKeyFrame(0f, .45f);
            o.InsertKeyFrame(.25f, 1f);
            o.InsertKeyFrame(.5f, .45f);
            o.InsertKeyFrame(1f, .45f);
            o.Duration = TimeSpan.FromMilliseconds(1100);
            o.IterationBehavior = AnimationIterationBehavior.Forever;
            o.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            Start(e, o);
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Bez końca: skala klatkami (oddychanie logo, mrugnięcie powiek). Klatki: (postęp, skala X, skala Y).</summary>
    public static void Loop(UIElement e, Vector3 center, double ms, params (float At, float X, float Y)[] frames)
    {
        if (!On) return;
        try
        {
            var c = Comp(e);
            e.CenterPoint = center;
            var a = c.CreateVector3KeyFrameAnimation();
            a.Target = "Scale";
            foreach (var (at, x, y) in frames) a.InsertKeyFrame(at, new Vector3(x, y, 1));
            a.Duration = TimeSpan.FromMilliseconds(ms);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            Start(e, a);
        }
        catch (Exception ex) { Fail(ex); }
    }

    // sprężyna liczona klatka po klatce — dla właściwości, których kompozycja nie animuje (np. zaokrąglenie rogów)
    sealed class Tw { public double X, V, To; public Action<double> Apply = null!; }
    static readonly ConditionalWeakTable<object, Tw> Tweens = new();
    static readonly List<Tw> Active = [];
    static bool _hooked;
    static long _frameAt;

    /// <summary>Wartość sprężyście z <paramref name="current"/> do <paramref name="to"/>; <paramref name="apply"/> co klatkę. Klucz = element (kolejne wywołania przejmują ruch).</summary>
    public static void Tween(object key, double current, double to, Action<double> apply)
    {
        if (!On) { apply(to); return; }
        var t = Tweens.GetValue(key, _ => new Tw { X = current });
        if (!Active.Contains(t)) { t.X = current; t.V = 0; Active.Add(t); }
        t.To = to;
        t.Apply = apply;
        if (!_hooked)
        {
            _hooked = true;
            _frameAt = Environment.TickCount64;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        }
    }

    static void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Clamp((now - _frameAt) / 1000.0, .001, .05);
        _frameAt = now;
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var t = Active[i];
            var acc = -320 * (t.X - t.To) - 22 * t.V;
            t.V += acc * dt;
            t.X += t.V * dt;
            if (Math.Abs(t.X - t.To) < .05 && Math.Abs(t.V) < .5) { t.X = t.To; t.V = 0; Active.RemoveAt(i); }
            try { t.Apply(t.X); } catch (Exception ex) { Fail(ex); Active.Remove(t); }
        }
        if (Active.Count == 0) { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame; _hooked = false; }
    }
}

/// <summary>
/// Kółko myszy z wygładzaniem (szlif, pkt 9): każdy ząbek dodaje ~100 px do drogi, a lista dojeżdża tam płynnie z wyhamowaniem
/// (jak w Edge); szybkie kręcenie się sumuje. Touchpady precyzyjne zostają przy systemowym przewijaniu (DirectManipulation).
/// Wyłączone przy „Mniej ruchu” albo w ustawieniach.
/// </summary>
static class SmoothWheel
{
    sealed class St { public ScrollViewer Sv = null!; public double Pending; }
    static readonly List<St> Running = [];
    static bool _hooked;
    static long _at;
    const double Step = 100;

    public static bool Enabled => App.Prefs.SmoothWheel && Motion.On;

    /// <summary>Podpina wygładzanie: zdarzenie kółka łapie treść, zanim dotrze do ScrollViewera.</summary>
    public static void Attach(ScrollViewer sv, UIElement content)
    {
        var st = new St { Sv = sv };
        content.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) => OnWheel(st, e)), false);
    }

    static void OnWheel(St st, PointerRoutedEventArgs e)
    {
        if (!Enabled || e.Handled) return;
        if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0) return; // Ctrl + kółko = powiększanie
        var p = e.GetCurrentPoint(st.Sv).Properties;
        if (p.IsHorizontalMouseWheel || p.MouseWheelDelta == 0 || st.Sv.ScrollableHeight <= 0) return;
        e.Handled = true;
        st.Pending -= p.MouseWheelDelta / 120.0 * Step;
        if (!Running.Contains(st)) Running.Add(st);
        if (!_hooked)
        {
            _hooked = true;
            _at = Environment.TickCount64;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        }
    }

    static void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Clamp((now - _at) / 1000.0, .001, .05);
        _at = now;
        var k = 1 - Math.Exp(-dt * 13); // ok. 90% drogi w 180 ms
        for (var i = Running.Count - 1; i >= 0; i--)
        {
            var st = Running[i];
            var step = Math.Abs(st.Pending) < 1 ? st.Pending : st.Pending * k;
            var from = st.Sv.VerticalOffset;
            var to = Math.Clamp(from + step, 0, st.Sv.ScrollableHeight);
            st.Pending -= step;
            if (Math.Abs(to - from) < .01 && (to <= 0 || to >= st.Sv.ScrollableHeight)) st.Pending = 0; // dojechało do końca listy
            else try { st.Sv.ChangeView(null, to, null, true); } catch { st.Pending = 0; }
            if (Math.Abs(st.Pending) < .01) Running.RemoveAt(i);
        }
        if (Running.Count == 0) { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame; _hooked = false; }
    }

    /// <summary>Przerywa resztkę wygładzonego przewijania (przewijanie z programu: na dół, skok do wiadomości).</summary>
    public static void Cancel(ScrollViewer sv)
    {
        for (var i = Running.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Running[i].Sv, sv)) { Running[i].Pending = 0; Running.RemoveAt(i); }
    }

    /// <summary>Do samotestu w CI: czy coś jeszcze jedzie.</summary>
    public static bool Busy => Running.Count > 0;
}
