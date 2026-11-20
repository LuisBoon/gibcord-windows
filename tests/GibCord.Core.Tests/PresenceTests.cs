namespace GibCord.Core.Tests;

public sealed class PresenceTests
{
    static readonly List<DetectableGame> Games =
    [
        new(1, "Minecraft", ["javaw.exe", "minecraft.exe"], "minecraft"),
        new(2, "Counter-Strike 2", ["cs2.exe"], null),
        new(3, "Fortnite", ["fortniteclient-win64-shipping.exe"], null),
    ];

    [Fact]
    public void GraPoNazwieProcesu()
    {
        var g = GibCordClient.PickGame(Games, new HashSet<string> { "explorer.exe", "cs2.exe" }, _ => [], null);
        Assert.Equal("Counter-Strike 2", g?.Name);
    }

    [Fact]
    public void JavaToMinecraftTylkoZTytulemOkna()
    {
        var running = new HashSet<string> { "javaw.exe" };
        Assert.Null(GibCordClient.PickGame(Games, running, _ => ["IntelliJ IDEA"], null));
        Assert.Equal("Minecraft", GibCordClient.PickGame(Games, running, _ => ["Minecraft 1.21.4"], null)?.Name);
    }

    [Fact]
    public void ZostajePrzyObecnejGrze()
    {
        var running = new HashSet<string> { "cs2.exe", "fortniteclient-win64-shipping.exe" };
        Assert.Equal("Fortnite", GibCordClient.PickGame(Games, running, _ => [], Games[2])?.Name);
        Assert.Null(GibCordClient.PickGame(Games, new HashSet<string> { "notepad.exe" }, _ => [], Games[2]));
    }
}
