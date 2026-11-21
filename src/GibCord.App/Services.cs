using System.Windows.Input;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.BadgeNotifications;
using Velopack;
using Velopack.Sources;

namespace GibCord.App;

/// <summary>Powiadomienia Windows (dymki w rogu ekranu i centrum powiadomień).</summary>
public static class Toasts
{
    static bool _ok;

    /// <summary>Odpowiedź wpisana w dymku (klucz rozmowy, tekst) i „Oznacz jako przeczytane” — bez otwierania okna.</summary>
    public static Action<long, string>? Reply;
    public static Action<long>? MarkRead;
    /// <summary>Kliknięcie w powiadomienie z konta w tle: (konto, klucz rozmowy).</summary>
    public static Action<long, long>? OpenAccount;

    public static void Init(Action<long> open)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, a) =>
            {
                a.Arguments.TryGetValue("c", out var cs);
                a.Arguments.TryGetValue("a", out var act);
                long.TryParse(cs, out var cid);
                if (a.Arguments.TryGetValue("acc", out var accs) && long.TryParse(accs, out var acc) && acc != 0) { OpenAccount?.Invoke(acc, cid); return; }
                switch (act)
                {
                    case "reply" when a.UserInput.TryGetValue("odp", out var text) && !string.IsNullOrWhiteSpace(text):
                        Reply?.Invoke(cid, text);
                        return;
                    case "reply":
                        return;
                    case "read":
                        MarkRead?.Invoke(cid);
                        return;
                }
                open(cid);
            };
            AppNotificationManager.Default.Register();
            _ok = true;
        }
        catch (Exception ex) { App.Log("Powiadomienia niedostępne: " + ex.Message); }
    }

    public static void Show(string title, string body, long channelId, string group, bool canReply = false, long account = 0)
    {
        if (!_ok) return;
        try
        {
            var b = new AppNotificationBuilder()
                .AddArgument("c", channelId.ToString())
                .AddArgument("acc", account.ToString())
                .AddText(title)
                .AddText(string.IsNullOrWhiteSpace(body) ? "Nowa wiadomość" : body);
            if (canReply && channelId != 0)
            {
                // odpowiedź prosto z dymka (jak w komunikatorach na telefonie)
                b.AddTextBox("odp", "Napisz odpowiedź…", "")
                 .AddButton(new AppNotificationButton("Wyślij").AddArgument("a", "reply").AddArgument("c", channelId.ToString()).SetInputId("odp"))
                 .AddButton(new AppNotificationButton("Przeczytane").AddArgument("a", "read").AddArgument("c", channelId.ToString()));
            }
            var n = b.BuildNotification();
            n.Group = group;
            AppNotificationManager.Default.Show(n);
        }
        catch (Exception ex) { App.Log("Powiadomienie: " + ex.Message); }
    }

    public static void ClearFor(long channelId)
    {
        if (!_ok) return;
        try { _ = AppNotificationManager.Default.RemoveByGroupAsync("c" + channelId); } catch { }
    }

    public static void Unregister()
    {
        if (!_ok) return;
        try { AppNotificationManager.Default.Unregister(); } catch { }
    }
}

/// <summary>Licznik na ikonie w pasku zadań.</summary>
public static class Badge
{
    public static void Set(int n)
    {
        try
        {
            if (n > 0) BadgeNotificationManager.Current.SetBadgeAsCount((uint)n);
            else BadgeNotificationManager.Current.ClearBadge();
        }
        catch { /* niedostępne w starszym Windowsie — trudno */ }
    }
}

public sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}

/// <summary>
/// Ikona w zasobniku (obok zegara): GibCord działa po zamknięciu okna, jak Discord.
/// Czysty Win32 (Shell_NotifyIcon + podklasa okna), bez dodatkowych bibliotek.
/// </summary>
public static partial class Tray
{
    const uint WM_APP_TRAY = 0x8000 + 17, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    static MainWindow? _w;
    static IntPtr _icon, _iconUnread;
    static bool _added, _unread;
    static uint _taskbarCreated;
    static SubclassProc? _proc;
    static string _tip = "GibCord";
    public static bool Available => _added;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImageW(IntPtr hinst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static IntPtr LoadIco(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        return LoadImageW(IntPtr.Zero, path, 1 /* IMAGE_ICON */, 16, 16, 0x10 /* LR_LOADFROMFILE */);
    }

    public static void Init(MainWindow w)
    {
        try
        {
            _w = w;
            _icon = LoadIco("tray.ico");
            _iconUnread = LoadIco("tray-unread.ico");
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            _proc = WndProc;
            SetWindowSubclass(w.Hwnd, _proc, (UIntPtr)17, UIntPtr.Zero);
            Add();
        }
        catch (Exception ex) { App.Log("Zasobnik niedostępny: " + ex.Message); }
    }

    static NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = _w!.Hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY, hIcon = _unread ? _iconUnread : _icon, szTip = _tip, szInfo = "", szInfoTitle = "",
    };

    static void Add()
    {
        var d = Data();
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
        if (!_added) App.Log("Zasobnik: Shell_NotifyIcon(NIM_ADD) nie wyszło");
    }

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_APP_TRAY)
        {
            // wyjątek w wywołaniu z natywnej pętli zamknąłby program bez śladu w dzienniku
            try
            {
                var ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev == WM_LBUTTONUP) _w?.BringToFront();
                else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu(hwnd);
            }
            catch (Exception ex) { App.Log("Zasobnik: " + ex); }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0) Add(); // Eksplorator uruchomił się ponownie
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    static void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, (UIntPtr)1, "Otwórz GibCord");
        AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)2, "Zamknij GibCord");
        GetCursorPos(out var p);
        SetForegroundWindow(hwnd);
        var cmd = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */, p.X, p.Y, hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == 1) _w?.BringToFront();
        else if (cmd == 2) _w?.Quit();
    }

    public static void SetUnread(bool unread, int n = 0)
    {
        if (!_added || (_unread == unread && !unread)) return;
        _unread = unread;
        _tip = unread ? $"GibCord — nieprzeczytane: {n}" : "GibCord";
        var d = Data();
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    public static void Dispose()
    {
        if (_w is null) return;
        try
        {
            if (_added) { var d = Data(); Shell_NotifyIconW(NIM_DELETE, ref d); }
            if (_proc is not null) RemoveWindowSubclass(_w.Hwnd, _proc, (UIntPtr)17);
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            if (_iconUnread != IntPtr.Zero) DestroyIcon(_iconUnread);
        }
        catch { }
        _added = false;
    }
}

/// <summary>Aktualizacje z wydań na GitHubie (Velopack): pobiera w tle, pyta o ponowne uruchomienie.</summary>
public static class Updater
{
    public const string Repo = "https://github.com/LuisBoon/gibcord-windows";
    static UpdateManager? _mgr;
    static UpdateInfo? _ready;

    public static string Version
    {
        get
        {
            try { if (_mgr?.CurrentVersion is { } v) return v.ToString(); } catch { }
            var a = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            var s = a.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)a[0]).InformationalVersion : "6.0.0";
            var plus = s.IndexOf('+');
            return plus > 0 ? s[..plus] : s;
        }
    }

    public static bool IsInstalled { get { try { return _mgr?.IsInstalled == true; } catch { return false; } } }
    /// <summary>Beta albo kompilacja testowa (wersja z „-”, np. 6.0.0-beta.10, 6.0.0-ci.42) — wtedy znaczek „beta”.</summary>
    public static bool IsPrerelease => Version.Contains('-');

    public static async Task Run(Action<string> onReady)
    {
        try
        {
            // Wersja zwykła (6.0.0, 6.0.1…) dostaje tylko zwykłe wydania; beta — także kolejne bety (i zwykłe, gdy są nowsze).
            _mgr = new UpdateManager(new GithubSource(Repo, null, prerelease: IsPrerelease));
            if (!_mgr.IsInstalled) return;
            while (true)
            {
                await Check(onReady);
                if (_ready is not null) return;
                await Task.Delay(TimeSpan.FromHours(3));
            }
        }
        catch (Exception ex) { App.Log("Aktualizacje: " + ex.Message); }
    }

    public static async Task<string?> Check(Action<string> onReady)
    {
        if (_mgr is null || !_mgr.IsInstalled) return "Aktualizacje działają po instalacji z instalatora";
        try
        {
            var u = await _mgr.CheckForUpdatesAsync();
            if (u is null) return null;
            await _mgr.DownloadUpdatesAsync(u);
            _ready = u;
            onReady(u.TargetFullRelease.Version.ToString());
            return null;
        }
        catch (Exception ex) { App.Log("Sprawdzanie aktualizacji: " + ex); return "Nie udało się pobrać aktualizacji: " + Why(ex); }
    }

    /// <summary>Zrozumiała przyczyna błędu aktualizacji (szuka też w wyjątkach wewnętrznych).</summary>
    static string Why(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            switch (e)
            {
                case System.Net.Http.HttpIOException: // zerwane połączenie w trakcie pobierania (to też IOException — nie „zajęte pliki”)
                    return "połączenie z GitHubem zerwało się w trakcie pobierania. Spróbuj jeszcze raz.";
                case HttpRequestException h when h.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests:
                    return "GitHub chwilowo ogranicza sprawdzanie aktualizacji. Spróbuj za godzinę albo pobierz instalator ze strony gibcord.luisboon.pl.";
                case HttpRequestException:
                    return "brak połączenia z GitHubem. Sprawdź internet i spróbuj jeszcze raz.";
                case TaskCanceledException or TimeoutException:
                    return "GitHub nie odpowiada. Spróbuj za chwilę.";
                case UnauthorizedAccessException:
                    return "brak dostępu do folderu aplikacji. Zainstaluj GibCorda instalatorem ze strony gibcord.luisboon.pl.";
                case IOException when (e.HResult & 0xFFFF) is 0x70 or 0x27: // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL — niezależnie od języka Windowsa
                    return "za mało miejsca na dysku.";
                case IOException:
                    return "pliki aplikacji są zajęte. Zamknij wszystkie okna GibCorda (także ikonę przy zegarze) i spróbuj ponownie.";
            }
        return ex.Message;
    }

    /// <summary>Inne okna GibCorda z tego samego folderu (np. drugie uruchomienie z --server) trzymają pliki — zamknij je przed podmianą.</summary>
    static void CloseOtherInstances()
    {
        try
        {
            var me = Environment.ProcessId;
            var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var parent = (Path.GetDirectoryName(dir) ?? dir).TrimEnd('\\') + "\\"; // …\GibCord6\ (obejmuje current\ i stare wersje; ukośnik — bez folderów-sąsiadów „GibCord6xyz”)
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("GibCord"))
            {
                if (p.Id == me) continue;
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path is not null && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); App.Log("Aktualizacja: zamknięto inne okno GibCorda (pid " + p.Id + ")"); }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Instaluje pobraną aktualizację i uruchamia aplikację od nowa. Zwraca komunikat błędu albo null.</summary>
    public static string? Restart()
    {
        if (_mgr is null || _ready is null) return "Najpierw sprawdź aktualizacje.";
        try
        {
            CloseOtherInstances();
            _mgr.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
            return null;
        }
        catch (Exception ex) { App.Log("Restart z aktualizacją: " + ex); return "Nie udało się zainstalować aktualizacji: " + Why(ex); }
    }
}
