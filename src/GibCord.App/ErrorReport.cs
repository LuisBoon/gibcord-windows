using System.Text.Json;
using System.Text.RegularExpressions;

namespace GibCord.App;

/// <summary>
/// Automatyczne raporty błędów (6.0.8): nieobsłużony wyjątek, zawieszenie okna (strażnik), błąd aktualizacji albo animacji
/// idzie sam na serwer (POST /api/diag/error) z końcówką dziennika — bez tokenów i adresów e-mail, bez treści wiadomości
/// (dziennik ich nie zawiera). Ten sam błąd najwyżej raz na uruchomienie, najwyżej 10 raportów na uruchomienie.
/// Awaria, po której aplikacja się zamyka, albo zawieszenie, po którym ktoś zabił proces, trafia do pliku i wysyła się przy następnym starcie.
/// Wyłącznik: Ustawienia → Aplikacja → „Wysyłaj automatycznie raporty błędów”.
/// </summary>
public static class ErrorReport
{
    static readonly HashSet<string> _sent = [];
    static int _count;
    static string PendingPath => Path.Combine(App.DataDir, "raport-do-wyslania.json");

    sealed record Report(string Kind, string Message, string Detail, string Log, string Version, string Os);

    static bool Enabled => App.Prefs.SendErrorReports && !Options.Demo && !Options.OtherServer;

    /// <summary>Tokeny (JWT, ?token=…) i adresy e-mail nigdy nie wychodzą z komputera.</summary>
    public static string Scrub(string s)
    {
        s = Regex.Replace(s, @"eyJ[\w-]+\.[\w-]+\.[\w-]+", "[token]");
        s = Regex.Replace(s, @"((?:access_)?token=)[^&\s""]+", "$1[ukryty]", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"gcb_[\w-]+", "[klucz]");
        s = Regex.Replace(s, @"[\w.+-]+@[\w-]+\.[\w.-]+", "[e-mail]");
        return Regex.Replace(s, @"C:\\Users\\[^\\]+", @"C:\Users\[ja]", RegexOptions.IgnoreCase);
    }

    static Report Make(string kind, string message, string detail) => new(kind, Scrub(message), Scrub(detail), Scrub(App.LogTail(80)), Updater.Version,
        $"Windows {Environment.OSVersion.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");

    /// <summary>Wyślij raport teraz (w tle). <paramref name="kind"/>: wyjątek, zawieszenie, aktualizacja, animacja, zadanie.</summary>
    public static void Send(string kind, string message, string detail = "")
    {
        try
        {
            if (!Enabled) return;
            lock (_sent) { if (!_sent.Add(kind + "|" + message) || ++_count > 10) return; }
            var r = Make(kind, message, detail);
            _ = Task.Run(() => Post(r));
        }
        catch { }
    }

    /// <summary>Aplikacja zaraz się zamknie (albo może zostać zabita) — raport na dysk, wyśle się przy następnym starcie.</summary>
    public static void SaveForLater(string kind, string message, string detail = "")
    {
        try { if (Enabled) File.WriteAllText(PendingPath, JsonSerializer.Serialize(Make(kind, message, detail))); } catch { }
    }

    /// <summary>Raport zapisany na później już niepotrzebny (np. okno się odwiesiło i raport poszedł normalnie).</summary>
    public static void DropSaved() { try { File.Delete(PendingPath); } catch { } }

    /// <summary>Po zalogowaniu: wyślij raport z poprzedniego uruchomienia, jeśli jest.</summary>
    public static void SendSaved()
    {
        try
        {
            if (!Enabled || !File.Exists(PendingPath)) return;
            var r = JsonSerializer.Deserialize<Report>(File.ReadAllText(PendingPath));
            File.Delete(PendingPath);
            if (r is not null) _ = Task.Run(() => Post(r with { Message = "[poprzednie uruchomienie] " + r.Message }));
        }
        catch { }
    }

    static async Task Post(Report r)
    {
        try
        {
            var c = App.Window?.Client;
            if (c is null || !c.HasToken) return;
            await c.Api.PostJson("diag/error", new { app = "windows", version = r.Version, kind = r.Kind, message = r.Message, detail = r.Detail, log = r.Log, os = r.Os });
            App.Log($"Raport błędu wysłany ({r.Kind})");
        }
        catch (Exception ex) { App.Log("Raport błędu: nie udało się wysłać — " + ex.Message); }
    }
}
