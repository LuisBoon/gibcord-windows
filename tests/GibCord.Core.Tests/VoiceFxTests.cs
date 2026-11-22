using GibCord.Core.Voice;
using Xunit;

namespace GibCord.Core.Tests;

/// <summary>6.2 zmieniacz głosu: te same wyniki co węzeł gc-fx na stronie (media.js).</summary>
public class VoiceFxTests
{
    const int Sr = 48000;

    static float[] Voice()
    {
        var x = new float[Sr * 2];
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)Sr; var env = .5 + .5 * Math.Sin(2 * Math.PI * 3 * t);
            x[i] = (float)(.3 * env * (Math.Sin(2 * Math.PI * 200 * t) + .5 * Math.Sin(2 * Math.PI * 400 * t) + .25 * Math.Sin(2 * Math.PI * 600 * t)) / 1.75);
        }
        return x;
    }

    static double Pitch(float[] x)
    {
        var a = x.AsSpan(Sr / 2, 4096); double best = 0; var bl = 0;
        for (var l = 40; l < 600; l++) { double s = 0; for (var i = 0; i < a.Length - l; i++) s += a[i] * a[i + l]; if (s > best) { best = s; bl = l; } }
        return Sr / (double)bl;
    }

    static float[] Run(string? fx)
    {
        var f = new VoiceFx(Sr) { Effect = fx };
        var x = Voice();
        for (var i = 0; i < x.Length; i += 480) f.Process(x.AsSpan(i, Math.Min(480, x.Length - i))); // ramki 10 ms jak w silniku
        return x;
    }

    [Theory]
    [InlineData(null, 200)]
    [InlineData("gibek", 320)]
    [InlineData("olbrzym", 140)]
    [InlineData("jaskinia", 200)]
    [InlineData("radio", 200)]
    public void WysokoscGlosu(string? fx, double hz)
    {
        var y = Run(fx);
        Assert.All(y, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1));
        Assert.InRange(Pitch(y), hz * .95, hz * 1.05);
    }

    [Fact]
    public void RobotBezPrzesteru()
    {
        var y = Run("robot");
        var rms = Math.Sqrt(y.Average(v => (double)v * v));
        Assert.InRange(rms, .03, .2);
        Assert.True(y.Max(Math.Abs) < .9);
    }

    [Fact]
    public void NieznanyEfektToZwyklyGlos()
    {
        var f = new VoiceFx(Sr) { Effect = "kaczka" };
        Assert.Null(f.Effect);
    }
}
