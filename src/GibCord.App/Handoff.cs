using System.Text.Json;

namespace GibCord.App;

/// <summary>
/// Przesiadka ze starej aplikacji (Electron): przed odinstalowaniem zapisuje jednorazowy kod logowania
/// w %LocalAppData%\GibCord6-dane\przesiadka.json. Nowa aplikacja wymienia go na sesję i kasuje plik.
/// </summary>
public static class Handoff
{
    static string PathOf => Path.Combine(App.DataDir, "przesiadka.json");

    public static string? Pending()
    {
        try
        {
            if (!File.Exists(PathOf)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(PathOf));
            File.Delete(PathOf); // jednorazowy — nawet jeśli wymiana się nie uda
            var r = doc.RootElement;
            var code = r.TryGetProperty("code", out var c) ? c.GetString() : null;
            var at = r.TryGetProperty("at", out var a) && a.TryGetInt64(out var t) ? t : 0;
            if (string.IsNullOrEmpty(code) || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at > 10 * 60_000) return null;
            return code;
        }
        catch { return null; }
    }
}
