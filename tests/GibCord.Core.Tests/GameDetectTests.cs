using GibCord.Core;
using Xunit;

namespace GibCord.Core.Tests;

public class GameDetectTests
{
    static readonly Dictionary<string, string> NoMine = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> NoNot = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<DetectableGame> List = [new(5, "Minecraft", ["javaw.exe"], "Minecraft"), new(7, "Roblox", ["robloxplayerbeta.exe"], null)];

    static ProcInfo P(string exe, string? path = null, string? title = null, bool fs = false, string? product = null) =>
        new(exe, path, title is null ? [] : [title], fs, product);

    [Fact]
    public void ListaSerwera()
    {
        var hit = GameDetect.Pick([P("javaw.exe", @"C:\Java\javaw.exe", "Minecraft 1.21.3")], List, null, NoMine, NoNot, null, null);
        Assert.Equal("Minecraft", hit?.Name);
        Assert.Equal(5, hit?.AppId);
        // ten sam plik bez pasującego tytułu (inna aplikacja w Javie) — nie gra
        Assert.Null(GameDetect.Pick([P("javaw.exe", @"C:\Java\javaw.exe", "IntelliJ")], List, null, NoMine, NoNot, null, null));
    }

    [Fact]
    public void SteamPoFolderzeIPomocnicyPominięci()
    {
        var procs = new[]
        {
            P("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", "Steam"),
            P("unitycrashhandler64.exe", @"D:\SteamLibrary\steamapps\common\Lethal Company\UnityCrashHandler64.exe", "crash"),
            P("lethal company.exe", @"D:\SteamLibrary\steamapps\common\Lethal Company\Lethal Company.exe", "Lethal Company"),
        };
        var hit = GameDetect.Pick(procs, List, null, NoMine, NoNot, null, null);
        Assert.Equal("Lethal Company", hit?.Name);
        Assert.Equal("Steam", hit?.Source);
        Assert.Equal("lethal company.exe", hit?.Exe);
    }

    [Fact]
    public void BibliotekaDajeLadnaNazwe()
    {
        var lib = new GameLibrary();
        lib.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive", "Counter-Strike 2");
        var hit = GameDetect.Pick([P("cs2.exe", @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", "Counter-Strike 2")],
            List, lib, NoMine, NoNot, null, null);
        Assert.Equal("Counter-Strike 2", hit?.Name);
    }

    [Fact]
    public void RiotIEpicBezLauncherow()
    {
        var hit = GameDetect.Pick([
            P("riotclientux.exe", @"C:\Riot Games\Riot Client\UX\RiotClientUx.exe", "Riot Client"),
            P("valorant-win64-shipping.exe", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", "VALORANT  "),
        ], List, null, NoMine, NoNot, null, null);
        Assert.Equal("VALORANT", hit?.Name);
        Assert.Null(GameDetect.Pick([P("epicgameslauncher.exe", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", "Epic Games Launcher")],
            List, null, NoMine, NoNot, null, null));
    }

    [Fact]
    public void PelnyEkranDopieroZaDrugimRazem()
    {
        var p = P("pathofexile.exe", @"E:\Gry\PoE\PathOfExile.exe", "Path of Exile", fs: true, product: "Path of Exile");
        Assert.Null(GameDetect.Pick([p], List, null, NoMine, NoNot, null, null));
        var hit = GameDetect.Pick([p], List, null, NoMine, NoNot, null, "pathofexile.exe");
        Assert.Equal("Path of Exile", hit?.Name);
        Assert.Equal("pełny ekran", hit?.Source);
        // przeglądarka na pełnym ekranie (film) — nie gra
        Assert.Null(GameDetect.Pick([P("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "YouTube", fs: true)], List, null, NoMine, NoNot, null, "chrome.exe"));
        // silnik zamiast nazwy produktu — nazwa pliku (nigdy tytuł okna: mógłby zdradzić nazwę dokumentu)
        Assert.Equal("mojagra", GameDetect.NameOf(P("mojagra.exe", null, "Tajny dokument.docx", true, "Unreal Engine")));
    }

    [Fact]
    public void TwojaListaINieGra()
    {
        var mine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tibia.exe"] = "Tibia" };
        Assert.Equal("Tibia", GameDetect.Pick([P("tibia.exe", @"C:\Tibia\tibia.exe", "Tibia")], List, null, mine, NoNot, null, null)?.Name);
        var not = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lethal company.exe" };
        Assert.Null(GameDetect.Pick([P("lethal company.exe", @"D:\SteamLibrary\steamapps\common\Lethal Company\Lethal Company.exe", "Lethal Company")], List, null, NoMine, not, null, null));
    }

    [Fact]
    public void WykrytaGraSieTrzyma()
    {
        var cur = new GameHit("Roblox", 7, "lista GibCorda", "robloxplayerbeta.exe");
        var hit = GameDetect.Pick([
            P("robloxplayerbeta.exe", null, "Roblox"),
            P("lethal company.exe", @"D:\SteamLibrary\steamapps\common\Lethal Company\Lethal Company.exe", "Lethal Company"),
        ], List, null, NoMine, NoNot, cur, null);
        Assert.Same(cur, hit);
    }

    [Fact]
    public void Nazwy()
    {
        Assert.Equal("VALORANT", GameDetect.Pretty("VALORANT-Win64-Shipping"));
        Assert.Equal("Hollow Knight", GameDetect.Pretty("Hollow_Knight"));
    }
}
