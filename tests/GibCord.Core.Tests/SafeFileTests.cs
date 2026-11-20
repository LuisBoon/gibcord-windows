using GibCord.Core;

namespace GibCord.Core.Tests;

public class SafeFileTests
{
    sealed class Dane { public int Liczba { get; set; } public string Tekst { get; set; } = ""; }

    static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "gc-safefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void ZapisIOdczyt()
    {
        var p = Path.Combine(Dir(), "a.json");
        SafeFile.Write(p, """{"liczba":5,"tekst":"zażółć"}""");
        var d = SafeFile.Read<Dane>(p);
        Assert.NotNull(d);
        Assert.Equal(5, d!.Liczba);
        Assert.Equal("zażółć", d.Tekst);
        Assert.False(File.Exists(p + ".tmp"));
    }

    [Fact]
    public void UszkodzonyPlikZostajeOdlozony()
    {
        var p = Path.Combine(Dir(), "b.json");
        File.WriteAllText(p, """{"liczba":5,"tek""");
        Assert.Null(SafeFile.Read<Dane>(p));
        Assert.True(File.Exists(p + ".uszkodzony"));
    }

    [Fact]
    public void ZapisPrzerwanyPrzedPodmianaJestRatowany()
    {
        var p = Path.Combine(Dir(), "c.json");
        File.WriteAllText(p + ".tmp", """{"liczba":7}""");
        Assert.Equal(7, SafeFile.Read<Dane>(p)?.Liczba);
        Assert.True(File.Exists(p));
    }

    [Fact]
    public void BrakPlikuToNull() => Assert.Null(SafeFile.Read<Dane>(Path.Combine(Dir(), "nie-ma.json")));
}
