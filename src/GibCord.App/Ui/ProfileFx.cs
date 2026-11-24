using System.Numerics;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace GibCord.App.Ui;

/// <summary>
/// 6.4: efekt karty profilu — cząsteczki nad kartą przez ~4 s po otwarciu (konfetti, śnieg, iskry, serca, bąbelki).
/// Przy „Mniej ruchu” nic nie leci; na zrzutach CI (bez ruchu) — jedna nieruchoma klatka, żeby było widać, jak wygląda.
/// </summary>
static class ProfileFx
{
    static readonly string[] Confetti = ["#ff6b9a", "#ffd166", "#6fe3a5", "#7cc7ff", "#b69cff", "#ff8a65"];

    /// <summary>Warstwa na cząsteczki (nakładana na całą kartę, nie łapie kliknięć).</summary>
    public static Canvas Layer() => new() { IsHitTestVisible = false };

    public static void Play(Canvas host, string? fx, double w, double h)
    {
        host.Children.Clear();
        if (string.IsNullOrEmpty(fx) || w <= 0 || h <= 0) return;
        var frozen = !Motion.On;
        if (frozen && Options.Screenshot is null) return; // „Mniej ruchu”
        var rnd = new Random(fx.GetHashCode() ^ Environment.TickCount);
        var n = fx switch { "konfetti" => 38, "snieg" => 30, "iskry" => 30, "serca" => 16, "babelki" => 18, _ => 0 };
        for (var i = 0; i < n; i++)
        {
            double R(double a, double b) => a + rnd.NextDouble() * (b - a);
            FrameworkElement el;
            Vector3 from, to;
            float spin = 0;
            double ms, delay;
            switch (fx)
            {
                case "konfetti":
                    el = new Rectangle { Width = R(5, 8), Height = R(9, 13), RadiusX = 1.5, RadiusY = 1.5, Fill = U.B(Confetti[rnd.Next(Confetti.Length)]) };
                    var x0 = R(0, w);
                    from = new((float)x0, (float)R(-40, -10), 0); to = new((float)(x0 + R(-60, 60)), (float)(h + 20), 0);
                    spin = (float)R(-540, 540); ms = R(2200, 3400); delay = R(0, 900);
                    break;
                case "snieg":
                {
                    var d = R(3.5, 8);
                    el = new Ellipse { Width = d, Height = d, Fill = U.Alpha("#ffffff", R(.6, .95)) };
                    var x1 = R(0, w);
                    from = new((float)x1, -12, 0); to = new((float)(x1 + R(-40, 40)), (float)(h + 10), 0);
                    ms = R(3000, 4000); delay = R(0, 1200);
                    break;
                }
                case "iskry":
                {
                    var d = R(3, 5.5);
                    el = new Ellipse { Width = d, Height = d, Fill = U.B(rnd.Next(3) switch { 0 => "#ffd166", 1 => "#ffb347", _ => "#fff3b0" }) };
                    var cx = w / 2 + R(-50, 50); var cy = 130 + R(-10, 10); // spod awatara
                    var ang = R(0, Math.PI * 2); var dist = R(60, 150);
                    from = new((float)cx, (float)cy, 0); to = new((float)(cx + Math.Cos(ang) * dist), (float)(cy + Math.Sin(ang) * dist - 20), 0);
                    ms = R(900, 1500); delay = R(0, 2400);
                    break;
                }
                case "serca":
                    el = new TextBlock { Text = "♥", FontSize = R(14, 24), Foreground = U.B(rnd.Next(2) == 0 ? "#ff6b9a" : "#ff4d6a") };
                    var x2 = R(10, w - 20);
                    from = new((float)x2, (float)(h - 10), 0); to = new((float)(x2 + R(-30, 30)), (float)R(20, h * .4), 0);
                    spin = (float)R(-30, 30); ms = R(2400, 3400); delay = R(0, 1000);
                    break;
                default: // bąbelki
                {
                    var d = R(8, 20);
                    el = new Ellipse { Width = d, Height = d, Fill = U.Alpha("#ffffff", .08), Stroke = U.Alpha("#ffffff", .55), StrokeThickness = 1.2 };
                    var x3 = R(0, w);
                    from = new((float)x3, (float)(h + 10), 0); to = new((float)(x3 + R(-35, 35)), (float)R(-10, h * .3), 0);
                    ms = R(2600, 3600); delay = R(0, 900);
                    break;
                }
            }
            host.Children.Add(el);
            if (frozen)
            {
                // nieruchoma klatka „w locie” (zrzut ekranu)
                var t = (float)R(.15, .85);
                el.Translation = Vector3.Lerp(from, to, t);
                el.Opacity = .9;
                continue;
            }
            el.CenterPoint = new Vector3((float)(el.Width is > 0 ? el.Width / 2 : 6), (float)(el.Height is > 0 ? el.Height / 2 : 8), 0);
            el.Opacity = 0;
            Motion.Particle(el, from, to, spin, ms, delay);
        }
        if (frozen) return;
        var timer = host.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(4300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => host.Children.Clear();
        timer.Start();
    }
}
