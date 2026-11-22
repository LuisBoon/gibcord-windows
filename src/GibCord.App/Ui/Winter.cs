using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace GibCord.App.Ui;

/// <summary>
/// 6.1 „Zima w GibCordzie”: od 1 grudnia do 6 stycznia (pora roku z serwera, /api/info → season) albo wg ustawienia
/// „Zimowy wygląd” (auto / on / off): śnieg w oknie, Gibek w czapce, zimowe dźwięki (dzwoneczki).
/// </summary>
static class Winter
{
    /// <summary>Tryb pokazowy (zrzuty CI --view winter / wrapped).</summary>
    public static bool DemoOn;

    public static bool Active => DemoOn || App.Prefs.WinterMode switch
    {
        "on" => true,
        "off" => false,
        _ => App.Window?.Client?.Info?.Season == "winter",
    };

    /// <summary>Zmiana pory roku albo ustawienia — śnieg i czapki same się pokazują/chowają.</summary>
    public static event Action? Changed;
    static bool _last;
    public static void Notify()
    {
        var now = Active;
        if (now == _last && !_first) return;
        _first = false;
        _last = now;
        Changed?.Invoke();
    }
    static bool _first = true;

    // ── czapka Mikołaja na Gibku ──
    // Współrzędne w układzie logo 512 px (icon.svg po przesunięciu o -20): głowa zaczyna się na y≈130, pałąk słuchawek ~110–135.
    static PathGeometry Cone(double k)
    {
        var f = new PathFigure { StartPoint = new(150 * k, 122 * k), IsClosed = true, IsFilled = true };
        f.Segments.Add(new BezierSegment { Point1 = new(176 * k, 40 * k), Point2 = new(300 * k, 8 * k), Point3 = new(400 * k, 54 * k) });
        f.Segments.Add(new BezierSegment { Point1 = new(372 * k, 72 * k), Point2 = new(362 * k, 98 * k), Point3 = new(362 * k, 122 * k) });
        var g = new PathGeometry();
        g.Figures.Add(f);
        return g;
    }

    /// <summary>Sama czapka na płótnie wielkości logo (kładzie się na obrazku Gibka).</summary>
    public static Canvas Hat(double size)
    {
        var k = size / 512;
        var c = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        c.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = Cone(k), Fill = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = U.C("#ff5a6e"), Offset = 0 }, new GradientStop { Color = U.C("#c8243f"), Offset = 1 } } } });
        var band = new Rectangle { Width = 240 * k, Height = 38 * k, RadiusX = 19 * k, RadiusY = 19 * k, Fill = U.B("#f6f1fa") };
        Canvas.SetLeft(band, 136 * k); Canvas.SetTop(band, 108 * k);
        c.Children.Add(band);
        var pom = new Ellipse { Width = 54 * k, Height = 54 * k, Fill = U.B("#f6f1fa") };
        Canvas.SetLeft(pom, 376 * k); Canvas.SetTop(pom, 28 * k);
        c.Children.Add(pom);
        return c;
    }

    /// <summary>Obrazek Gibka z czapką, która sama się pokazuje w zimie (i chowa po niej).</summary>
    public static Grid Hatted(FrameworkElement logo, double size)
    {
        var hat = Hat(size);
        var g = new Grid { Width = size, Height = size, Children = { logo, hat } };
        void Sync() => hat.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        Changed += Sync;
        g.Unloaded += (_, _) => Changed -= Sync;
        g.Loaded += (_, _) => { Changed -= Sync; Changed += Sync; Sync(); };
        return g;
    }
}

/// <summary>Śnieg nad oknem: kilkadziesiąt płatków, animacje kompozycji (poza wątkiem okna), bez klikania. „Mniej ruchu” = bez śniegu.</summary>
sealed class Snow : Canvas
{
    readonly Random _rnd = new(2026);
    readonly DispatcherQueueTimer _debounce;
    Size _built;

    public Snow()
    {
        IsHitTestVisible = false;
        _debounce = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(450);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Build();
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - _built.Width) > 40 || Math.Abs(e.NewSize.Height - _built.Height) > 40) { _debounce.Stop(); _debounce.Start(); } };
        Loaded += (_, _) => { Winter.Changed -= Build; Winter.Changed += Build; Build(); };
        Unloaded += (_, _) => Winter.Changed -= Build;
    }

    void Build()
    {
        Children.Clear();
        var w = ActualWidth; var h = ActualHeight;
        _built = new Size(w, h);
        if (!Winter.Active || w < 50 || h < 50) return;
        var still = !Motion.On;
        var n = (int)Math.Clamp(w * h / 22000, 24, 72);
        Compositor? comp = null;
        try { comp = CompositionTarget.GetCompositorForCurrentThread(); } catch { still = true; }
        for (var i = 0; i < n; i++)
        {
            var d = 2 + _rnd.NextDouble() * 3.6;
            var e = new Ellipse { Width = d, Height = d, Fill = U.W(.35 + _rnd.NextDouble() * .5) };
            Children.Add(e);
            var x = (float)(_rnd.NextDouble() * w);
            if (still || comp is null)
            {
                // bez ruchu: płatki stoją w miejscu (rzadziej), żeby zima była widoczna, ale nic się nie ruszało
                if (i % 2 == 1) { Children.Remove(e); continue; }
                e.Translation = new Vector3(x, (float)(_rnd.NextDouble() * h), 0);
                continue;
            }
            var drift = (float)((_rnd.NextDouble() - .5) * 120);
            var sway = (float)((_rnd.NextDouble() - .5) * 50);
            var secs = 8 + _rnd.NextDouble() * 10 * (1.4 - d / 6); // małe (dalsze) płatki spadają wolniej
            var a = comp.CreateVector3KeyFrameAnimation();
            a.Target = "Translation";
            a.InsertKeyFrame(0, new Vector3(x, -12, 0));
            a.InsertKeyFrame(.5f, new Vector3(x + drift / 2 + sway, (float)(h / 2), 0), comp.CreateLinearEasingFunction());
            a.InsertKeyFrame(1, new Vector3(x + drift, (float)h + 12, 0), comp.CreateLinearEasingFunction());
            a.Duration = TimeSpan.FromSeconds(secs);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            a.DelayTime = TimeSpan.FromSeconds(_rnd.NextDouble() * secs);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            try { e.StartAnimation(a); } catch (Exception ex) { App.Log("Śnieg: " + ex.Message); Children.Clear(); return; }
        }
    }
}
