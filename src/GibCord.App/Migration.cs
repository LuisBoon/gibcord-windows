using System.Diagnostics;
using Microsoft.Win32;
using GibCord.App.Ui;
using Velopack.Windows;

namespace GibCord.App;

/// <summary>
/// 25.11.2026: przesiadka ze starej aplikacji (Electron 1.0.x) tak, żeby nowy GibCord nie znikał.
/// <para>
/// Dezinstalator starej aplikacji (NSIS z electron-builder) zamyka WSZYSTKIE procesy „GibCord.exe” użytkownika
/// (<c>taskkill /im GibCord.exe</c>) — także GibCorda 6 — i kasuje skróty „GibCord.lnk” z pulpitu i menu Start,
/// bo nazywają się tak samo. U fifi 21.11 i 23.11 GibCord 6 żył przez to 1–2 s, a potem nie było go czym uruchomić.
/// </para>
/// Dlatego: (1) zanim ruszy dezinstalator (przesiadka ze starej aplikacji albo nasze sprzątanie), zostawiamy „budzik” —
/// cmd.exe, który po 25 s uruchamia GibCorda ponownie (cmd.exe nie pada od taskkill /im GibCord.exe), a druga kopia, gdy
/// pierwsza jednak żyje, po cichu się zamyka; (2) po przesiadce odtwarzamy skróty i autostart; (3) gdy na komputerze jest
/// jeszcze stara aplikacja (instalator uruchomiony ręcznie, wersja ze Sklepu), usuwamy ją sami — nigdy w trakcie rozmowy.
/// </summary>
static class Migration
{
    const string RelaunchArg = "--po-przesiadce";
    static string FlagPath => Path.Combine(App.DataDir, "po-przesiadce.flag");

    /// <summary>Uruchomienie przez budzik (po zamknięciu przez dezinstalator starej aplikacji).</summary>
    public static bool IsRelaunch => Program.Args.Contains(RelaunchArg);

    /// <summary>Na samym początku Main: przesiadka ze starej aplikacji właśnie trwa (świeży przesiadka.json) — budzik i znacznik.</summary>
    public static void EarlyStart()
    {
        if (Options.Demo || Options.OtherServer || Platform.IsPackaged || IsRelaunch) return;
        try
        {
            var p = Path.Combine(App.DataDir, "przesiadka.json");
            if (!File.Exists(p) || DateTime.UtcNow - File.GetLastWriteTimeUtc(p) > TimeSpan.FromMinutes(10)) return;
            Directory.CreateDirectory(App.DataDir);
            File.WriteAllText(FlagPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            ArmRelaunch(25);
            App.Log("Przesiadka: budzik na wypadek zamknięcia przez dezinstalator starej aplikacji");
        }
        catch (Exception ex) { App.Log("Przesiadka (start): " + ex.Message); }
    }

    /// <summary>Po starcie, w tle: skróty i autostart po przesiadce, potem usunięcie starej aplikacji (gdy <paramref name="canRemove"/>: zalogowany, bez rozmowy).</summary>
    public static void AfterStart(Func<bool> canRemove)
    {
        if (Options.Demo || Options.OtherServer || Platform.IsPackaged) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(IsRelaunch ? 5 : 40)).ConfigureAwait(false);
            try { await FixAfterMigration().ConfigureAwait(false); } catch (Exception ex) { App.Log("Przesiadka (skróty): " + ex.Message); }
            for (var i = 0; i < 180; i++) // do 3 h czekamy na zalogowanie i koniec rozmowy
            {
                if (canRemove())
                {
                    try { RemoveOldApp(); } catch (Exception ex) { App.Log("Przesiadka (stara aplikacja): " + ex.Message); }
                    return;
                }
                await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            }
        });
    }

    /// <summary>„Budzik”: cmd.exe po <paramref name="seconds"/> s uruchamia GibCorda ponownie (ping zamiast timeout — timeout nie działa bez konsoli).</summary>
    static void ArmRelaunch(int seconds)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Process.Start(new ProcessStartInfo(cmd, $"/d /c ping -n {seconds + 1} 127.0.0.1 >nul & start \"\" \"{exe}\" {RelaunchArg}")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
    }

    static bool OldUninstallerRunning()
    {
        // NSIS kopiuje dezinstalator do %TEMP% jako Un_A.exe i stamtąd go uruchamia
        foreach (var n in new[] { "Un_A", "Uninstall GibCord" })
        {
            var ps = Process.GetProcessesByName(n);
            var any = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    /// <summary>Po przesiadce: skróty na pulpicie i w menu Start (dezinstalator starej aplikacji je skasował) i autostart jak w starej.</summary>
    static async Task FixAfterMigration()
    {
        if (!File.Exists(FlagPath)) return;
        for (var i = 0; i < 90 && OldUninstallerRunning(); i++) await Task.Delay(2000).ConfigureAwait(false); // do 3 min
        await Task.Delay(3000).ConfigureAwait(false);
        for (var round = 0; round < 2; round++)
        {
            var sc = new Shortcuts();
            var exe = Path.GetFileName(Environment.ProcessPath) ?? "GibCord.exe";
            var missing = ShortcutLocation.None;
            foreach (var loc in new[] { ShortcutLocation.Desktop, ShortcutLocation.StartMenuRoot })
                if (sc.FindShortcuts(exe, loc).Count == 0) missing |= loc;
            if (missing != ShortcutLocation.None) { sc.CreateShortcutForThisExe(missing); App.Log($"Przesiadka: odtworzone skróty ({missing})"); }
            if (round == 0) await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false); // drugi raz, gdyby dezinstalator skończył później
        }
        if (!AutoStart.Enabled) { AutoStart.Set(true); App.Log("Przesiadka: start razem z Windowsem włączony (jak w starej aplikacji)"); }
        File.Delete(FlagPath);
    }

    /// <summary>Stara aplikacja GibCord 1.x jeszcze jest na komputerze — usuwamy ją (najpierw budzik, bo dezinstalator nas zamknie).</summary>
    static void RemoveOldApp()
    {
        // wersja spoza Sklepu (electron-builder, instalacja na użytkownika): wpis w Uninstall z „Uninstall GibCord.exe”
        string? cmdline = null, ver = null;
        using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
        {
            if (root is not null)
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    var un = k?.GetValue("QuietUninstallString") as string ?? k?.GetValue("UninstallString") as string;
                    var dv = k?.GetValue("DisplayVersion") as string;
                    if (un is null || !un.Contains("Uninstall GibCord.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (dv is null || !Version.TryParse(dv, out var v) || v.Major >= 6) continue;
                    cmdline = un; ver = dv; break;
                }
        }
        if (cmdline is not null && SplitCommand(cmdline) is var (exe, args) && File.Exists(exe))
        {
            if (!args.Contains("/S", StringComparison.Ordinal)) args = (args + " /S").Trim();
            File.WriteAllText(FlagPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            ArmRelaunch(25);
            App.Log($"Przesiadka: usuwam starą aplikację GibCord {ver}");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            return; // dezinstalator zaraz nas zamknie — wersję ze Sklepu sprawdzi kolejne uruchomienie (budzik)
        }
        RemoveOldStorePackage();
    }

    static (string Exe, string Args)? SplitCommand(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"')) { var e = s.IndexOf('"', 1); return e > 1 ? (s[1..e], s[(e + 1)..].Trim()) : null; }
        var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? (s[..(i + 4)], s[(i + 4)..].Trim()) : null;
    }

    /// <summary>Stara wersja ze Sklepu (paczka GibCord.GibCord 1.x) — tylko dla tego użytkownika; nowa paczka 6.x zostaje.</summary>
    static void RemoveOldStorePackage()
    {
        var pm = new Windows.Management.Deployment.PackageManager();
        foreach (var p in pm.FindPackagesForUser("", "GibCord.GibCord", "CN=04559352-DF71-4AA3-A4CF-B55AA03F9922"))
        {
            var v = p.Id.Version;
            if (v.Major >= 6) continue;
            App.Log($"Przesiadka: usuwam starą wersję ze Sklepu {v.Major}.{v.Minor}.{v.Build}");
            try { pm.RemovePackageAsync(p.Id.FullName).AsTask().Wait(TimeSpan.FromMinutes(2)); }
            catch (Exception ex) { App.Log("Przesiadka (Sklep): " + ex.Message); }
        }
    }
}
