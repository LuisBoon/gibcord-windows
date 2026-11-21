using GibCord.Core;
using Xunit;

namespace GibCord.Core.Tests;

public class BlurHashTests
{
    // przykład ze strony blurha.sh (4×3 składowe, 28 znaków)
    const string Sample = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";

    [Fact]
    public void PoprawnyIBledny()
    {
        Assert.True(BlurHash.IsValid(Sample));
        Assert.False(BlurHash.IsValid(""));
        Assert.False(BlurHash.IsValid(null));
        Assert.False(BlurHash.IsValid(Sample[..20])); // za krótki
        Assert.False(BlurHash.IsValid("LEHV6nWB2yk8pyo0adR*.7kCMdn\"")); // znak spoza alfabetu
    }

    [Fact]
    public void DekodujeDoPikseliBgra()
    {
        var px = BlurHash.Decode(Sample, 32, 24)!;
        Assert.Equal(32 * 24 * 4, px.Length);
        Assert.All(Enumerable.Range(0, 32 * 24), i => Assert.Equal(255, px[i * 4 + 3]));
        // średni kolor ≈ składowa stała (DC) z napisu: „V6nW” → kolor zakodowany w 4 znakach
        double b = 0, g = 0, r = 0;
        for (var i = 0; i < 32 * 24; i++) { b += px[i * 4]; g += px[i * 4 + 1]; r += px[i * 4 + 2]; }
        var n = 32 * 24.0;
        Assert.InRange(r / n, 60, 200);
        Assert.InRange(g / n, 40, 200);
        Assert.InRange(b / n, 20, 200);
        Assert.Null(BlurHash.Decode("zepsuty", 8, 8));
    }
}
