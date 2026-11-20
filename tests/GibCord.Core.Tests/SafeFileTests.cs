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

public sealed class AccountPrefsTests
{
    [Fact]
    public void KazdeKontoMaSwojeSzkiceIOstatnieKanaly()
    {
        var p = new Prefs { LastUserId = 1 };
        p.Drafts[10] = "szkic konta 1"; p.LastChannel[5] = 50; p.LastServer = 5; p.ServerOrder = [5, 6];
        p.SwapAccount(1, 2); // przełączenie na konto 2 — puste
        Assert.Empty(p.Drafts); Assert.Empty(p.LastChannel); Assert.Equal(-1, p.LastServer); Assert.Equal(2, p.LastUserId);
        p.Drafts[20] = "szkic konta 2";
        p.SwapAccount(2, 1); // powrót — wszystko konta 1 wraca
        Assert.Equal("szkic konta 1", p.Drafts[10]); Assert.Equal(50, p.LastChannel[5]); Assert.Equal(5, p.LastServer); Assert.Equal([5, 6], p.ServerOrder);
        Assert.False(p.Drafts.ContainsKey(20));
        Assert.Equal("szkic konta 2", p.PerAccount[2].Drafts[20]);
        p.SwapAccount(1, 1); // to samo konto — bez zmian
        Assert.Equal("szkic konta 1", p.Drafts[10]);
    }
}
