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
            if (App.Prefs.ReduceMotion || (Options.Screenshot is not null && !Options.ForceMotion)) return false; // zrzuty w CI: bez ruchu, żeby nie złapać klatki w połowie
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

    /// <summary>Lekkie powiększenie po najechaniu (ikony serwerów).</summary>
    public static void Hoverable(FrameworkElement e, float hover = 1.06f)
    {
        e.PointerEntered += (_, _) => ScaleTo(e, hover);
        e.PointerExited += (_, _) => ScaleTo(e, 1f);
        e.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => ScaleTo(e, .94f, .9f)), true);
        e.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => ScaleTo(e, hover)), true);
    }

    /// <summary>Wysokość z odbiciem (pasek przy ikonie serwera) — animacja XAML, bo skalowanie spłaszczałoby zaokrąglenia.</summary>
    static readonly ConditionalWeakTable<FrameworkElement, Storyboard> Heights = new();

    public static void Height(FrameworkElement e, double to)
    {
        var from = e.ActualHeight > 0 || !double.IsNaN(e.Height) ? (e.ActualHeight > 0 ? e.ActualHeight : e.Height) : to;
        if (Heights.TryGetValue(e, out var old)) { old.Stop(); Heights.Remove(e); }
        e.Height = to;
        if (!On || Math.Abs(from - to) < .5) return;
        try
        {
            var a = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(320), EnableDependentAnimation = true,
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .45 },
            };
            Storyboard.SetTarget(a, e);
            Storyboard.SetTargetProperty(a, "Height");
            var sb = new Storyboard();
            sb.Children.Add(a);
            sb.Completed += (_, _) => { if (Heights.TryGetValue(e, out var cur) && cur == sb) Heights.Remove(e); };
            Heights.AddOrUpdate(e, sb);
            sb.Begin();
        }
        catch (Exception ex) { Fail(ex); }
    }
}
