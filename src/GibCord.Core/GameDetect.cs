using System.Text.Json;
using System.Text.RegularExpressions;

namespace GibCord.Core;

/// <summary>Uruchomiony program z perspektywy wykrywania gier (zbiera go aplikacja na Windowsie).</summary>
/// <param name="Exe">Nazwa pliku małymi literami, z „.exe”.</param>
/// <param name="Path">Pełna ścieżka pliku (może być nieznana — np. proces innego użytkownika).</param>
/// <param name="Titles">Tytuły widocznych okien programu (puste = brak okna, np. usługa albo pomocnik).</param>
/// <param name="Fullscreen">Okno na pierwszym planie zakrywa cały monitor (pełny ekran albo okno bez ramki).</param>
/// <param name="Product">Nazwa produktu z pliku (FileVersionInfo) — tylko dla kandydatów z pełnego ekranu.</param>
public sealed record ProcInfo(string Exe, string? Path, string[] Titles, bool Fullscreen = false, string? Product = null);

/// <summary>Wykryta gra: nazwa do pokazania, numer z listy serwera (jeśli stamtąd), skąd ją wiemy i po jakim pliku.</summary>
public sealed record GameHit(string Name, long? AppId, string Source, string Exe);

/// <summary>Gry zainstalowane przez sklepy (Steam, Epic, GOG…): folder instalacji → ładna nazwa.</summary>
public sealed class GameLibrary
{
    readonly List<(string Dir, string Name)> _dirs = [];

    public int Count => _dirs.Count;

    public void Add(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name)) return;
        var d = Norm(dir);
        if (d.Length < 4 || _dirs.Any(x => x.Dir == d)) return;
        _dirs.Add((d, name.Trim()));
        _dirs.Sort((a, b) => b.Dir.Length.CompareTo(a.Dir.Length)); // najdłuższy (najbardziej szczegółowy) folder wygrywa
    }

    static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant() + "\\";

    public string? Match(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var p = path.Replace('/', '\\').ToLowerInvariant();
        foreach (var (dir, name) in _dirs) if (p.StartsWith(dir, StringComparison.Ordinal)) return name;
        return null;
    }

    static readonly Regex VdfPath = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
    static readonly Regex AcfName = new("\"name\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
    static readonly Regex AcfDir = new("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);

    /// <summary>Steam: biblioteki z steamapps\libraryfolders.vdf, w każdej appmanifest_*.acf (name + installdir).</summary>
    public void AddSteam(string steamRoot)
    {
        try
        {
            var libs = new List<string> { steamRoot };
            var vdf = System.IO.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in VdfPath.Matches(File.ReadAllText(vdf))) libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var apps = System.IO.Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var t = File.ReadAllText(acf);
                        var name = AcfName.Match(t);
                        var dir = AcfDir.Match(t);
                        if (name.Success && dir.Success && !IsTool(name.Groups[1].Value))
                            Add(System.IO.Path.Combine(apps, "common", dir.Groups[1].Value), name.Groups[1].Value);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>Epic Games: pliki *.item w ProgramData\Epic\EpicGamesLauncher\Data\Manifests (DisplayName + InstallLocation).</summary>
    public void AddEpic(string manifestsDir)
    {
        try
        {
            if (!Directory.Exists(manifestsDir)) return;
            foreach (var f in Directory.EnumerateFiles(manifestsDir, "*.item"))
            {
                try
                {
                    using var d = JsonDocument.Parse(File.ReadAllText(f));
                    var r = d.RootElement;
                    if (r.Str("DisplayName") is { } n && r.Str("InstallLocation") is { } loc && !IsTool(n)) Add(loc, n);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Narzędzia ze sklepów, które nie są grami (Steamworks, redystrybucje, edytory).</summary>
    static bool IsTool(string name) =>
        Regex.IsMatch(name, @"steamworks|redistributable|redist|proton|steam linux runtime|dedicated server|\bsdk\b|soundtrack|wallpaper engine", RegexOptions.IgnoreCase);
}

/// <summary>
/// Nowe wykrywanie gier (6.0.5). Kolejność:
/// 1) Twoja lista (dodane ręcznie), 2) lista z serwera (pliki + fragment tytułu okna), 3) gry ze sklepów po folderze instalacji
/// (Steam/Epic z ładną nazwą z ich plików, oraz typowe foldery: GOG, Xbox, EA, Ubisoft, Riot, Rockstar, Amazon, itch),
/// 4) program na pełnym ekranie, który nie jest przeglądarką, odtwarzaczem ani narzędziem — dopiero gdy trzyma pełny ekran przez dwa skany.
/// Pliki oznaczone „To nie gra” są zawsze pomijane. Wykryta gra trzyma się, dopóki działa (bez przeskakiwania między kandydatami).
/// </summary>
public static class GameDetect
{
    static readonly (Regex Re, string Source)[] Folders =
    [
        (new(@"\\steamapps\\common\\([^\\]+)\\", RegexOptions.IgnoreCase), "Steam"),
        (new(@"\\epic games\\([^\\]+)\\", RegexOptions.IgnoreCase), "Epic Games"),
        (new(@"\\gog galaxy\\games\\([^\\]+)\\", RegexOptions.IgnoreCase), "GOG"),
        (new(@"\\gog games\\([^\\]+)\\", RegexOptions.IgnoreCase), "GOG"),
        (new(@"\\xboxgames\\([^\\]+)\\", RegexOptions.IgnoreCase), "Xbox"),
        (new(@"\\(?:ea games|origin games)\\([^\\]+)\\", RegexOptions.IgnoreCase), "EA"),
        (new(@"\\ubisoft game launcher\\games\\([^\\]+)\\", RegexOptions.IgnoreCase), "Ubisoft"),
        (new(@"\\riot games\\([^\\]+)\\", RegexOptions.IgnoreCase), "Riot"),
        (new(@"\\rockstar games\\([^\\]+)\\", RegexOptions.IgnoreCase), "Rockstar"),
        (new(@"\\amazon games\\library\\([^\\]+)\\", RegexOptions.IgnoreCase), "Amazon"),
        (new(@"\\itch\\apps\\([^\\]+)\\", RegexOptions.IgnoreCase), "itch.io"),
    ];

    /// <summary>Foldery w bibliotekach, które nie są grami (programy sklepów i dodatki).</summary>
    static readonly Regex NotGameFolder = new(@"^(steamworks shared|riot client|launcher|social club|rockstar games launcher|epic online services|directxredist|ea desktop|ea app|origin|ubisoft connect|uplay|steamvr|wallpaper_engine|wallpaper engine)$", RegexOptions.IgnoreCase);

    /// <summary>Pliki pomocnicze gier i sklepów: launchery, zgłaszanie awarii, instalatory, anty-cheaty, przeglądarki wbudowane.</summary>
    static readonly Regex Helper = new(@"launcher|crash|report|helper|setup|install|unins|redist|dxsetup|easyanticheat|^eac|battleye|_be\.exe$|beservice|updat|bootstrap|service|overlay|webview|cefprocess|cefsub|subprocess|prereq|vc_?redist|physx|activation|dedicated|server\.exe$|tool|editor|config|settings", RegexOptions.IgnoreCase);

    /// <summary>Programy, które bywają na pełnym ekranie, a nie są grami.</summary>
    static readonly HashSet<string> NotGamesFullscreen = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "opera_gx.exe", "brave.exe", "vivaldi.exe", "arc.exe", "iexplore.exe", "librewolf.exe", "waterfox.exe", "yandex.exe",
        "vlc.exe", "mpc-hc64.exe", "mpc-hc.exe", "mpc-be64.exe", "potplayermini64.exe", "potplayer.exe", "wmplayer.exe", "video.ui.exe", "microsoft.media.player.exe", "mpv.exe", "kodi.exe", "plex.exe", "netflix.exe", "spotify.exe", "stremio.exe",
        "powerpnt.exe", "acrord32.exe", "acrobat.exe", "sumatrapdf.exe", "winword.exe", "excel.exe", "onenote.exe",
        "mstsc.exe", "msrdc.exe", "vmware-vmx.exe", "vmware.exe", "virtualbox.exe", "virtualboxvm.exe", "vmconnect.exe", "parsecd.exe", "moonlight.exe", "teamviewer.exe", "anydesk.exe", "rustdesk.exe",
        "explorer.exe", "applicationframehost.exe", "searchhost.exe", "searchapp.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "lockapp.exe", "textinputhost.exe", "dwm.exe",
        "screenclippinghost.exe", "snippingtool.exe", "photos.exe", "microsoft.photos.exe", "windowsterminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "taskmgr.exe",
        "obs64.exe", "obs32.exe", "streamlabs obs.exe", "streamlabs desktop.exe", "discord.exe", "gibcord.exe", "ms-teams.exe", "teams.exe", "zoom.exe", "slack.exe", "whatsapp.exe", "telegram.exe", "skype.exe", "signal.exe",
        "code.exe", "devenv.exe", "rider64.exe", "idea64.exe", "pycharm64.exe", "notepad.exe", "notepad++.exe", "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "galaxyclient.exe", "battle.net.exe",
        "eadesktop.exe", "upc.exe", "riotclientux.exe", "riotclientservices.exe", "xboxpcapp.exe", "gamebar.exe", "nvidia share.exe", "nvcontainer.exe", "medal.exe", "outplayed.exe", "overwolf.exe",
        "photoshop.exe", "blender.exe", "davinci resolve.exe", "resolve.exe", "premiere pro.exe", "afterfx.exe", "unity.exe", "unrealeditor.exe", "ue4editor.exe",
    };

    /// <summary>Nazwy produktów, które nic nie mówią (silniki, systemy) — wtedy tytuł okna albo nazwa pliku.</summary>
    static readonly Regex VagueProduct = new(@"^(unreal ?engine|ue4|ue5|unity( player)?|java\(tm\).*|openjdk.*|microsoft® windows® operating system|electron|chromium|node\.js|python|godot.*)$", RegexOptions.IgnoreCase);

    /// <summary>Wybiera grę. <paramref name="fullscreenBefore"/> — plik, który był na pełnym ekranie przy poprzednim skanie (pełny ekran liczy się od drugiego razu).</summary>
    public static GameHit? Pick(IReadOnlyList<ProcInfo> procs, IReadOnlyList<DetectableGame> list, GameLibrary? lib,
        IReadOnlyDictionary<string, string> mine, ICollection<string> notGames, GameHit? current, string? fullscreenBefore)
    {
        bool Ignored(string exe) => notGames.Contains(exe);
        var running = procs.Where(p => !Ignored(p.Exe)).ToList();
        var exes = new HashSet<string>(running.Select(p => p.Exe), StringComparer.OrdinalIgnoreCase);

        // wykryta gra trzyma się, dopóki działa (inaczej przy dwóch kandydatach nazwa mogłaby skakać)
        GameHit? keep = current is not null && exes.Contains(current.Exe) ? current : null;

        // 1) Twoja lista
        foreach (var p in running)
            if (mine.TryGetValue(p.Exe, out var name) && !string.IsNullOrWhiteSpace(name))
                return keep?.Source == "Twoja lista" && keep.Exe == p.Exe ? keep : new GameHit(name.Trim(), null, "Twoja lista", p.Exe);

        if (keep is not null) return keep;

        // 2) lista z serwera
        IEnumerable<string> Titles(string exe) => running.Where(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Titles);
        if (GibCordClient.PickGame(list, exes, Titles, null) is { } known)
            return new GameHit(known.Name, known.Id, "lista GibCorda", known.Executables.First(e => exes.Contains(e) && (known.TitleHint is null || Titles(e).Any(t => t.Contains(known.TitleHint, StringComparison.OrdinalIgnoreCase)))));

        // 3) gry ze sklepów (tylko programy z oknem — pomocnicy i usługi zwykle okna nie mają)
        foreach (var p in running.Where(p => p.Titles.Length > 0 && p.Path is not null && !Helper.IsMatch(p.Exe)))
        {
            if (lib?.Match(p.Path) is { } fromLib) return new GameHit(fromLib, null, "biblioteka gier", p.Exe);
            foreach (var (re, source) in Folders)
            {
                var m = re.Match(p.Path!);
                if (!m.Success || NotGameFolder.IsMatch(m.Groups[1].Value)) continue;
                return new GameHit(Pretty(m.Groups[1].Value), null, source, p.Exe);
            }
        }

        // 4) pełny ekran (od drugiego skanu z rzędu)
        var fs = running.FirstOrDefault(p => p.Fullscreen && IsFullscreenGameCandidate(p));
        if (fs is not null && string.Equals(fs.Exe, fullscreenBefore, StringComparison.OrdinalIgnoreCase))
            return new GameHit(NameOf(fs), null, "pełny ekran", fs.Exe);
        return null;
    }

    /// <summary>Czy program na pełnym ekranie może być grą (nie przeglądarka, odtwarzacz, system, narzędzie).</summary>
    public static bool IsFullscreenGameCandidate(ProcInfo p) =>
        !NotGamesFullscreen.Contains(p.Exe) && !Helper.IsMatch(p.Exe)
        && (p.Path is null || !Regex.IsMatch(p.Path, @"^[a-z]:\\windows\\|\\windowsapps\\microsoft\.", RegexOptions.IgnoreCase));

    /// <summary>Nazwa gry z pełnego ekranu: nazwa produktu z pliku, inaczej tytuł okna, inaczej nazwa pliku.</summary>
    public static string NameOf(ProcInfo p)
    {
        if (!string.IsNullOrWhiteSpace(p.Product) && !VagueProduct.IsMatch(p.Product.Trim())) return Clip(p.Product.Trim());
        var title = p.Titles.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (title is not null) return Clip(title.Trim());
        return Pretty(System.IO.Path.GetFileNameWithoutExtension(p.Exe));
    }

    /// <summary>Folder albo plik jako nazwa: podkreślenia i myślniki na spacje, bez „-Win64-Shipping”.</summary>
    public static string Pretty(string s)
    {
        s = Regex.Replace(s, @"[-_ ]?(win64|win32|x64|shipping|dx11|dx12|vulkan)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s.Replace('_', ' '), @"\s{2,}", " ").Trim();
        return Clip(s.Length > 0 ? s : "Gra");
    }

    static string Clip(string s) => s.Length > 64 ? s[..64].TrimEnd() : s;
}
