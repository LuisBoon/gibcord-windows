using System.Text.RegularExpressions;

namespace GibCord.App;

/// <summary>
/// Linki gibcord://… (np. z przeglądarki albo innego programu) → ścieżka w GibCordzie. Te same kształty co w starej aplikacji:
/// gibcord://i/KOD, gibcord://kanal/S/C, gibcord://serwer/S, gibcord://dm/D.
/// Rejestracja w Windows tylko wtedy, gdy nikt inny (stara aplikacja) nie obsługuje już tych linków.
/// </summary>
public static partial class DeepLink
{
    static readonly (Regex Re, Func<Match, string> Path)[] Rules =
    [
        (new(@"^i/([A-Za-z0-9]{4,32})$"), m => $"/i/{m.Groups[1].Value}"),
        (new(@"^(?:zaproszenie|invite)/([A-Za-z0-9]{4,32})$"), m => $"/i/{m.Groups[1].Value}"),
        (new(@"^kanal/(\d{1,12})/(\d{1,12})(?:/(\d{1,15}))?$"), m => $"/kanal/{m.Groups[1].Value}/{m.Groups[2].Value}" + (m.Groups[3].Success ? "/" + m.Groups[3].Value : "")),
        (new(@"^kanal/(\d{1,12})/(\d{1,12})/watek/(\d{1,12})$"), m => $"/kanal/{m.Groups[1].Value}/{m.Groups[2].Value}/watek/{m.Groups[3].Value}"),
        (new(@"^serwer/(\d{1,12})$"), m => $"/serwer/{m.Groups[1].Value}"),
        (new(@"^dm/(\d{1,12})$"), m => $"/dm/{m.Groups[1].Value}"),
        (new(@"^$"), _ => "/"),
    ];

    public static string? Parse(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 300) return null;
        var m = Regex.Match(url, "^gibcord:///?(.*)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var rest = m.Groups[1].Value.Split('?', '#')[0].TrimEnd('/');
        foreach (var (re, fn) in Rules) { var r = re.Match(rest); if (r.Success) return fn(r); }
        return null;
    }

    /// <summary>Pierwszy link gibcord:// z wiersza poleceń.</summary>
    public static string? Find(string? args)
    {
        if (string.IsNullOrEmpty(args)) return null;
        var m = Regex.Match(args, @"gibcord:[^\s""]+", RegexOptions.IgnoreCase);
        return m.Success ? Parse(m.Value) : null;
    }

    /// <summary>Rejestruje gibcord:// dla tej kopii (instalator), jeśli linków nie obsługuje inny program.</summary>
    public static void Register()
    {
        try
        {
            if (Ui.Platform.IsPackaged || Options.Demo) return; // wersja ze Sklepu: protokół z manifestu
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var cmdKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\gibcord\shell\open\command");
            var cur = cmdKey?.GetValue("") as string;
            var ours = cur?.Contains("GibCord6", StringComparison.OrdinalIgnoreCase) == true;
            if (cur is not null && !ours) return; // obsługuje stara aplikacja — nie zabieramy jej linków
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\gibcord");
            k.SetValue("", "URL:GibCord");
            k.SetValue("URL Protocol", "");
            using var ic = k.CreateSubKey("DefaultIcon");
            ic.SetValue("", $"\"{exe}\",0");
            using var cmd = k.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex) { App.Log("Rejestracja gibcord://: " + ex.Message); }
    }
}
