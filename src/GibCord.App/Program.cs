using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Velopack;

namespace GibCord.App;

public static class Program
{
    public const string InstanceKey = "GibCord6";
    public static string[] Args { get; private set; } = [];

    [STAThread]
    static int Main(string[] args)
    {
        Args = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            App.Log("Błąd (AppDomain): " + e.ExceptionObject);
            // aplikacja zaraz się zamknie — raport na dysk, pójdzie przy następnym starcie
            ErrorReport.SaveForLater("awaria", (e.ExceptionObject as Exception)?.GetType().Name + ": " + (e.ExceptionObject as Exception)?.Message, e.ExceptionObject?.ToString() ?? "");
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { App.Log("Błąd zadania w tle: " + e.Exception.InnerException?.Message); ErrorReport.Send("zadanie", e.Exception.InnerException?.GetType().Name + ": " + e.Exception.InnerException?.Message, e.Exception.ToString()); e.SetObserved(); };
        try
        {
            // bez kodów zaproszeń i adresów z gibcord:// w dzienniku (trafia do „Kopiuj dane diagnostyczne”)
            App.Log($"Start {string.Join(' ', args.Select(a => a.StartsWith("gibcord:", StringComparison.OrdinalIgnoreCase) ? "gibcord:…" : a))}");
            // Velopack: obsługa instalacji / aktualizacji (musi być na samym początku)
            VelopackApp.Build().SetArgs(args).Run();

            WinRT.ComWrappersSupport.InitializeComWrappers();
            if (!Options.Demo && RedirectToRunningInstance()) return 0;
            if (Options.Light) return LightMode.Run();
            App.Log("Application.Start");
            Application.Start(p =>
            {
                try
                {
                    var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(ctx);
                    _ = new App();
                }
                catch (Exception ex) { App.Log("Błąd przy tworzeniu App: " + ex); throw; }
            });
            App.Log("Koniec");
        }
        catch (Exception ex) { App.Log("Błąd w Main: " + ex); return 1; }
        return 0;
    }

    /// <summary>Druga kopia GibCorda tylko pokazuje okno pierwszej (np. klik w skrót albo powiadomienie).</summary>
    static bool RedirectToRunningInstance()
    {
        AppInstance key;
        try { key = AppInstance.FindOrRegisterForKey(Options.OtherServer ? InstanceKey + "-" + Options.ServerSlug : InstanceKey); }
        catch { return false; }
        if (key.IsCurrent) return false;
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var evt = Native.CreateEvent(IntPtr.Zero, true, false, null);
        _ = Task.Run(() =>
        {
            try { key.RedirectActivationToAsync(args).AsTask().Wait(5000); } catch { }
            Native.SetEvent(evt);
        });
        Native.CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [evt], out _);
        return true;
    }
}

/// <summary>Opcje z wiersza poleceń (zrzuty ekranu w CI, tryb pokazowy).</summary>
public static class Options
{
    public static string? Arg(string name)
    {
        var a = Program.Args;
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
    /// <summary>--demo: przykładowe dane bez łączenia z serwerem.</summary>
    public static bool Demo => Program.Args.Contains("--demo");
    /// <summary>--screenshot plik.png: zrzut okna po starcie i wyjście.</summary>
    public static string? Screenshot => Arg("--screenshot");
    /// <summary>--motion: animacje także przy zrzucie (CI sprawdza, czy ruch działa).</summary>
    public static bool ForceMotion => Program.Args.Contains("--motion");
    /// <summary>--wheel-test: CI kręci kółkiem nad czatem i zapisuje w dzienniku, o ile się przewinęło.</summary>
    public static bool WheelTest => Program.Args.Contains("--wheel-test");
    /// <summary>--view login|chat|friends|dm|profile|switcher</summary>
    public static string View => Arg("--view") ?? "chat";
    /// <summary>--server https://...: inny serwer (np. kopia testowa).</summary>
    public static string Server => Safe(Arg("--server")) ?? Safe(Environment.GetEnvironmentVariable("GIBCORD_SERVER")) ?? DefaultServer;
    /// <summary>Inny serwer tylko przez https (http wyłącznie na tym komputerze — testy); inaczej zostaje prawdziwy GibCord.</summary>
    static string? Safe(string? s) => s is not null && Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback)) ? s : null;
    public const string DefaultServer = "https://gibcord.luisboon.pl";
    /// <summary>Inny serwer niż prawdziwy GibCord (kopia do próby) — osobne dane, osobne okno, znacznik w pasku.</summary>
    public static bool OtherServer => !Demo && Server.TrimEnd('/') != DefaultServer;
    /// <summary>„gibcord.luisboon.pl:8443” → „gibcord.luisboon.pl-8443” (do nazw katalogu i instancji).</summary>
    public static string ServerSlug => System.Text.RegularExpressions.Regex.Replace(new Uri(Server).Authority, "[^A-Za-z0-9.-]", "-");
    public static bool Hidden => Program.Args.Contains("--ukryty");
    /// <summary>--pomiar okno|schowane|zwolnione: CI mierzy pamięć i procesor w danym stanie okna (tryb pokazowy).</summary>
    public static string? Measure => Arg("--pomiar");
    /// <summary>--lekki: tryb gry bez okna i bez WinUI — połączenie, głos, nakładka, zasobnik, powiadomienia.</summary>
    public static bool Light => Program.Args.Contains("--lekki");
}

static partial class Native
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateEvent(IntPtr attrs, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(IntPtr handle);

    [LibraryImport("ole32.dll")]
    public static partial uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, [In] IntPtr[] handles, out uint index);

    [StructLayout(LayoutKind.Sequential)]
    public struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlashWindowEx(ref FLASHWINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    public static double DpiScale(IntPtr hwnd) { var d = GetDpiForWindow(hwnd); return d == 0 ? 1 : d / 96.0; }

    /// <summary>Miganie paska zadań do czasu, aż ktoś kliknie okno.</summary>
    public static void Flash(IntPtr hwnd)
    {
        var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = 0x2 | 0xC, uCount = 3, dwTimeout = 0 };
        FlashWindowEx(ref fi);
    }
}
