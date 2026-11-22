using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GibCord.Core;
using Microsoft.Win32;

namespace GibCord.App;

/// <summary>
/// Zbieranie danych do wykrywania gier na Windowsie: uruchomione programy z oknami i ścieżkami, okno na pełnym ekranie,
/// biblioteki sklepów (Steam, Epic, GOG, wpisy gier w rejestrze). Bez uprawnień administratora; działa w tle (nie w wątku okna).
/// </summary>
public static class GameScan
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, nint lParam);
    delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint hwnd, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint mon, ref MONITORINFO mi);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint h, uint flags, StringBuilder s, ref uint size);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    const uint GW_OWNER = 4, MONITOR_DEFAULTTONEAREST = 2, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int DWMWA_CLOAKED = 14;

    /// <summary>Widoczne okna główne (bez właściciela, z tytułem, nie ukryte przez system) → identyfikator procesu → tytuły.</summary>
    static Dictionary<uint, List<string>> WindowTitles()
    {
        var map = new Dictionary<uint, List<string>>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != 0) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true; // np. uśpione aplikacje ze Sklepu
                var len = GetWindowTextLength(h);
                if (len <= 0) return true;
                var sb = new StringBuilder(Math.Min(len + 1, 512));
                GetWindowText(h, sb, sb.Capacity);
                GetWindowThreadProcessId(h, out var pid);
                if (!map.TryGetValue(pid, out var l)) map[pid] = l = [];
                l.Add(sb.ToString());
            }
            catch { }
            return true;
        }, 0);
        return map;
    }

    /// <summary>Okno na pierwszym planie zakrywa cały monitor? (gra na pełnym ekranie albo w oknie bez ramki). Zwraca jego proces.</summary>
    static uint? FullscreenPid()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == 0 || !GetWindowRect(h, out var r)) return null;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return null; // pulpit, pasek zadań
            // zwykłe zmaksymalizowane okno (z paskiem tytułu) przy ukrytym pasku zadań też zakrywa ekran — to nie gra
            const long WS_CAPTION = 0x00C00000;
            if (((long)GetWindowLongPtr(h, -16) & WS_CAPTION) == WS_CAPTION) return null;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref mi)) return null;
            var m = mi.rcMonitor;
            if (r.Left > m.Left || r.Top > m.Top || r.Right < m.Right || r.Bottom < m.Bottom) return null;
            GetWindowThreadProcessId(h, out var pid);
            return pid == Environment.ProcessId ? null : pid;
        }
        catch { return null; }
    }

    /// <summary>Pełna ścieżka pliku procesu (bez uprawnień administratora; procesy systemowe i innych użytkowników — null).</summary>
    static string? ImagePath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);

    /// <summary>6.2 klipy: największe widoczne okno procesu o tej nazwie pliku (okno gry do nagrywania), albo 0.</summary>
    public static nint MainWindowOf(string exe)
    {
        exe = exe.ToLowerInvariant();
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.ProcessName.ToLowerInvariant() + ".exe" == exe) pids.Add((uint)p.Id); } catch { }
            finally { p.Dispose(); }
        }
        if (pids.Count == 0) return 0;
        nint best = 0; long bestArea = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, 4) != 0) return true; // 4 = GW_OWNER
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || !GetWindowRect(h, out var r)) return true;
            var area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, 0);
        return bestArea >= 320 * 240 ? best : 0;
    }

    /// <summary>Uruchomione programy: nazwa pliku każdego procesu; ścieżka i tytuły tylko dla tych z oknami (tanio).</summary>
    public static List<ProcInfo> Processes()
    {
        var titles = WindowTitles();
        var fs = FullscreenPid();
        var list = new List<ProcInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var pid = (uint)p.Id;
                if (pid == 0 || pid == 4 || pid == Environment.ProcessId) continue;
                var t = titles.TryGetValue(pid, out var tl) ? tl.ToArray() : [];
                var full = fs == pid;
                var path = t.Length > 0 || full ? ImagePath(pid) : null;
                string? product = null;
                if (full && path is not null)
                    try { var vi = FileVersionInfo.GetVersionInfo(path); product = string.IsNullOrWhiteSpace(vi.ProductName) ? vi.FileDescription : vi.ProductName; } catch { }
                list.Add(new ProcInfo(p.ProcessName.ToLowerInvariant() + ".exe", path, t, full, product));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>Programy z oknami (do „Dodaj grę” w ustawieniach): plik, tytuł okna — bez GibCorda i programów systemowych.</summary>
    public static List<(string Exe, string Title, string? Path)> WindowedApps()
    {
        var res = new List<(string, string, string?)>();
        foreach (var p in Processes().Where(p => p.Titles.Length > 0))
        {
            if (p.Path is { } path && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
            if (res.Any(r => r.Item1 == p.Exe)) continue;
            res.Add((p.Exe, p.Titles[0], p.Path));
        }
        return res.OrderBy(r => r.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Biblioteki sklepów: Steam (manifesty), Epic (manifesty), GOG (rejestr), wpisy gier w „Programy i funkcje”.</summary>
    public static GameLibrary Library()
    {
        var lib = new GameLibrary();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k?.GetValue("SteamPath") is string steam) lib.AddSteam(steam.Replace('/', '\\'));
            lib.AddEpic(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests"));
            foreach (var root in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
                using (var k = Registry.LocalMachine.OpenSubKey(root))
                    if (k is not null)
                        foreach (var id in k.GetSubKeyNames())
                            using (var g = k.OpenSubKey(id))
                                if (g?.GetValue("gameName") is string n && g.GetValue("path") is string pth) lib.Add(pth, n);
            // „Programy i funkcje”: gry ze Steama („Steam App …”) i wydawców gier (Blizzard, Riot, Roblox, Mojang, EA, Ubisoft…)
            var publishers = new[] { "Blizzard Entertainment", "Riot Games", "Roblox Corporation", "Mojang", "Electronic Arts", "Ubisoft", "Rockstar Games", "Bethesda", "CD PROJEKT", "Epic Games, Inc.", "Activision", "Bungie", "miHoYo", "HoYoverse", "Wargaming", "Krafton", "Innersloth", "Supercell" };
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    using (var k = hive.OpenSubKey(root))
                        if (k is not null)
                            foreach (var id in k.GetSubKeyNames())
                                using (var e = k.OpenSubKey(id))
                                {
                                    if (e?.GetValue("DisplayName") is not string name || e.GetValue("InstallLocation") is not string loc || loc.Length < 4) continue;
                                    var pub = e.GetValue("Publisher") as string ?? "";
                                    var steamApp = id.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase);
                                    if (!steamApp && !publishers.Any(x => pub.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                                    if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^steam$|launcher|battle\.net|client|app\b|desktop|connect|redist|sdk|driver|tools?\b|editor|studio|anti-?cheat", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                                    lib.Add(loc.Trim('"'), name);
                                }
        }
        catch (Exception ex) { App.Log("Biblioteka gier: " + ex.Message); }
        App.Log($"Biblioteka gier: {lib.Count} gier ze sklepów");
        return lib;
    }
}
