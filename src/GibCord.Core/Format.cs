using System.Globalization;

namespace GibCord.Core;

/// <summary>Daty, rozmiary i odmiana liczebników po polsku (jak na stronie).</summary>
public static class Format
{
    public static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime;
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string Time(long ms) => Local(ms).ToString("HH:mm", Pl);

    /// <summary>„dziś o 18:02”, „wczoraj o 18:02”, „12.11.2026 18:02”.</summary>
    public static string When(long ms, DateTime? now = null)
    {
        var d = Local(ms).Date;
        var today = (now ?? DateTime.Now).Date;
        var diff = (today - d).Days;
        if (diff == 0) return "dziś o " + Time(ms);
        if (diff == 1) return "wczoraj o " + Time(ms);
        return Local(ms).ToString("dd.MM.yyyy HH:mm", Pl);
    }

    /// <summary>„czwartek, 20 listopada 2026”.</summary>
    public static string Day(long ms) => Local(ms).ToString("dddd, d MMMM yyyy", Pl);
    public static string Full(long ms) => Local(ms).ToString("dddd, d MMMM yyyy, HH:mm", Pl);

    public static bool SameDay(long a, long b) => Local(a).Date == Local(b).Date;

    public static string Size(long b) =>
        b < 1024 ? b + " B" : b < 1048576 ? (b / 1024.0).ToString("0", Pl) + " KB" : (b / 1048576.0).ToString("0.0", Pl) + " MB";

    /// <summary>1 wiadomość, 2 wiadomości, 5 wiadomości.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        if (n == 1) return one;
        var m10 = n % 10; var m100 = n % 100;
        return m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20) ? few : many;
    }

    public static string Duration(long ms)
    {
        var s = Math.Max(0, ms / 1000);
        return $"{s / 60}:{s % 60:00}";
    }

    public static readonly Dictionary<string, string> StatusPl = new()
    {
        ["online"] = "Dostępny", ["idle"] = "Zaraz wracam", ["dnd"] = "Nie przeszkadzać", ["invisible"] = "Niewidoczny", ["offline"] = "Niedostępny",
    };
}
