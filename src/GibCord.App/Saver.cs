using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace GibCord.App;

/// <summary>
/// Oszczędzanie (6.4.2) — ten sam GibCord, bez rozłączania: kiedy okna nie widać, wygląd przestaje pracować.
/// <list type="bullet">
/// <item><b>Pełny</b> — okno na wierzchu albo widoczne, gdy nie grasz: wszystko jak zawsze.</item>
/// <item><b>Tło</b> — grasz, a okno widać obok (np. na drugim monitorze): obwódki mówiących odświeżane rzadziej, śnieg stoi.</item>
/// <item><b>Uśpiony</b> — okno schowane do zasobnika, zminimalizowane albo zasłonięte grą (lub innym oknem) na całym monitorze:
/// odświeżanie wyglądu stoi, obraz z kamer nie jest pobierany, wątek okna dostaje od Windowsa tryb oszczędny (EcoQoS),
/// a po chwili oddajemy pamięć. Głos, nakładka w grze, powiadomienia, skróty i zasobnik działają bez zmian.</item>
/// </list>
/// Wszystko dzieje się na wątku okna; <see cref="Changed"/> też tam przychodzi.
/// </summary>
static partial class Saver
{
    public enum Level { Full, Background, Asleep }

    public static Level Now { get; private set; }
    /// <summary>Okna nie widać — wygląd nie musi się odświeżać.</summary>
    public static bool Asleep => Now == Level.Asleep;
    /// <summary>Okna nie widać albo grasz — ozdoby stoją, odświeżanie rzadsze.</summary>
    public static bool Quiet => Now != Level.Full;
    public static event Action? Changed;

    /// <summary>CI (tryb pokazowy, --pomiar gra / zasloniete): poziom ustawiony na sztywno, żeby zmierzyć jego koszt.</summary>
    public static Level? Forced;

    static MainWindow? _w;
    static Func<bool> _inGame = () => false;
    static DispatcherQueueTimer? _check, _trim;
    static bool _eco;
    static int _memPrio = MEMORY_PRIORITY_NORMAL;

    public static void Init(MainWindow w, DispatcherQueue dq, Func<bool> inGame)
    {
        if (_w is not null) { _w = w; _inGame = inGame; Update(); return; }
        _w = w;
        _inGame = inGame;
        // gdy okno jest widoczne, ale nie na wierzchu: co 2 s sprawdzamy, czy coś je nie zasłania (gra mogła wejść na pełny ekran)
        _check = dq.CreateTimer();
        _check.Interval = TimeSpan.FromSeconds(2);
        _check.Tick += (_, _) => Update();
        // pamięć oddajemy dopiero po 10 s uśpienia — szybkie Alt+Tab tam i z powrotem nic nie kosztuje
        _trim = dq.CreateTimer();
        _trim.Interval = TimeSpan.FromSeconds(10);
        _trim.IsRepeating = false;
        _trim.Tick += (_, _) => GiveBackMemory();
        Update();
    }

    /// <summary>Przelicz poziom (zmiana okna, wykryta gra, ustawienie). Wołać z wątku okna.</summary>
    public static void Update()
    {
        if (_w is null) return;
        Level next;
        try { next = Compute(); } catch (Exception ex) { App.Log("Oszczędzanie: " + ex.Message); next = Level.Full; }
        // sprawdzanie co 2 s tylko wtedy, gdy okno widać, ale nie jest na wierzchu (o zmianie okna na wierzchu Windows nas nie powiadamia)
        var aw = _w.AppWindow;
        var watch = Forced is null && aw.IsVisible && aw.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized } && GetForegroundWindow() != _w.Hwnd;
        if (_check is not null && _check.IsRunning != watch) { if (watch) _check.Start(); else _check.Stop(); }
        if (next == Now) return;
        var was = Now;
        Now = next;
        App.Log($"Oszczędzanie: {Name(was)} → {Name(next)}");
        Apply();
        try { Changed?.Invoke(); } catch (Exception ex) { App.Log("Oszczędzanie (widok): " + ex); }
    }

    static string Name(Level l) => l switch { Level.Asleep => "uśpiony", Level.Background => "tło (gra)", _ => "pełny" };

    static Level Compute()
    {
        if (Forced is { } f) return f;
        if (!App.Prefs.SaveInBackground) return Level.Full;
        var aw = _w!.AppWindow;
        if (!aw.IsVisible) return Level.Asleep;
        if (aw.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return Level.Asleep;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _w.Hwnd || Ours(fg)) return Level.Full;
        if (Covers(fg, _w.Hwnd)) return Level.Asleep;
        return _inGame() ? Level.Background : Level.Full;
    }

    static bool Ours(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>Czy okno na wierzchu zakrywa całe widoczne GibCordowe okno (gra na pełnym ekranie, zmaksymalizowana przeglądarka).</summary>
    static bool Covers(IntPtr fg, IntPtr me)
    {
        if (!IsWindowVisible(fg) || IsIconic(fg)) return false;
        if (DwmGetWindowAttribute(fg, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false;
        var cls = new char[64];
        var n = GetClassName(fg, cls, cls.Length);
        var name = new string(cls, 0, Math.Max(0, n));
        // pulpit, pasek zadań, menu Start, przełącznik Alt+Tab — nie zasłaniają okna, choć bywają na cały ekran
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow"
            or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame" or "ForegroundStaging" or "TaskSwitcherWnd") return false;
        if (!Bounds(me, out var m) || !Bounds(fg, out var f)) return false;
        // część okna poza ekranem się nie liczy — przycinamy do monitora, na którym jest GibCord
        var mon = MonitorFromWindow(me, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(mon, ref mi))
        {
            m.Left = Math.Max(m.Left, mi.rcMonitor.Left); m.Top = Math.Max(m.Top, mi.rcMonitor.Top);
            m.Right = Math.Min(m.Right, mi.rcMonitor.Right); m.Bottom = Math.Min(m.Bottom, mi.rcMonitor.Bottom);
        }
        if (m.Right <= m.Left || m.Bottom <= m.Top) return true; // okno w całości poza ekranem
        return f.Left <= m.Left && f.Top <= m.Top && f.Right >= m.Right && f.Bottom >= m.Bottom;
    }

    /// <summary>Widoczne granice okna (bez niewidzialnych ramek do zmiany rozmiaru).</summary>
    static bool Bounds(IntPtr hwnd, out RECT r)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) == 0) return true;
        return GetWindowRect(hwnd, out r);
    }

    static void Apply()
    {
        var asleep = Now == Level.Asleep;
        // wątek okna: tryb oszczędny Windowsa (EcoQoS). Wątki dźwięku mają własny priorytet (MMCSS) i tego nie dziedziczą.
        if (asleep != _eco)
        {
            _eco = asleep;
            var st = new THREAD_POWER_THROTTLING_STATE { Version = 1, ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED, StateMask = asleep ? THREAD_POWER_THROTTLING_EXECUTION_SPEED : 0u };
            try { SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref st, Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>()); } catch { }
        }
        // strony pamięci GibCorda idą do Windowsa przed stronami gry, gdy zacznie jej brakować
        SetMemoryPriority(asleep ? MEMORY_PRIORITY_BELOW_NORMAL : MEMORY_PRIORITY_NORMAL);
        if (asleep) { _trim?.Stop(); _trim?.Start(); }
        else _trim?.Stop();
        // animacje bez końca (ramki awatarów, szkielety, „pisze…”) — kompozytor przestaje rysować klatki
        try { Ui.Motion.SleepLoops(asleep); } catch (Exception ex) { App.Log("Oszczędzanie (ruch): " + ex.Message); }
        // obraz z kamer i transmisji: nie pobieramy, gdy nikt go nie ogląda (okienko transmisji obok = oglądasz)
        try { _w?.Client?.Voice.SetVideoPaused(asleep && !Ui.PipWindow.IsOpen); } catch { }
    }

    static void SetMemoryPriority(int prio)
    {
        if (prio == _memPrio) return;
        _memPrio = prio;
        var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = (uint)prio };
        try { SetProcessInformation(GetCurrentProcess(), ProcessMemoryPriority, ref info, Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>()); } catch { }
    }

    /// <summary>
    /// Po 10 s uśpienia: sprzątanie pamięci C# i oddanie Windowsowi stron, których nikt nie dotyka (schowany wygląd).
    /// To, czego używa głos, wraca samo przy pierwszym użyciu (to tanie — strony są jeszcze w pamięci).
    /// </summary>
    static void GiveBackMemory()
    {
        if (!Asleep) return;
        var inCall = _w?.Client is { VoiceChannelId: not 0 };
        _ = Task.Run(() =>
        {
            try
            {
                var before = Environment.WorkingSet;
                // w rozmowie bez zatrzymywania wątków (mikrofon i głośniki liczy C#) — zwykłe sprzątanie w tle
                if (inCall) GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                else
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                }
                EmptyWorkingSet(GetCurrentProcess());
                App.Log($"Oszczędzanie: pamięć oddana ({before / 1048576} → {Environment.WorkingSet / 1048576} MB w użyciu)");
            }
            catch (Exception ex) { App.Log("Oszczędzanie (pamięć): " + ex.Message); }
        });
    }

    // ───────────────────────── Win32 ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct THREAD_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int ThreadPowerThrottling = 3; // THREAD_INFORMATION_CLASS: ThreadMemoryPriority, ThreadAbsoluteCpuPriority, ThreadDynamicCodePolicy, ThreadPowerThrottling
    const uint THREAD_POWER_THROTTLING_EXECUTION_SPEED = 1;
    const int ProcessMemoryPriority = 0;
    const int MEMORY_PRIORITY_BELOW_NORMAL = 4, MEMORY_PRIORITY_NORMAL = 5;

    [LibraryImport("user32.dll")] private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(IntPtr hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(IntPtr hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(IntPtr hwnd, out RECT r);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetClassName(IntPtr hwnd, [Out] char[] buf, int max);
    [LibraryImport("user32.dll")] private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GetCurrentThread();
    [LibraryImport("kernel32.dll")] private static partial IntPtr GetCurrentProcess();
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetThreadInformation(IntPtr thread, int cls, ref THREAD_POWER_THROTTLING_STATE info, int size);
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetProcessInformation(IntPtr proc, int cls, ref MEMORY_PRIORITY_INFORMATION info, int size);
    [LibraryImport("psapi.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyWorkingSet(IntPtr proc);
}
