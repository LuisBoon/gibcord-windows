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
        AppDomain.CurrentDomain.UnhandledException += (_, e) => App.Log("Błąd (AppDomain): " + e.ExceptionObject);
        try
        {
            App.Log($"Start {string.Join(' ', args)}");
            // Velopack: obsługa instalacji / aktualizacji (musi być na samym początku)
            VelopackApp.Build().SetArgs(args).Run();

            WinRT.ComWrappersSupport.InitializeComWrappers();
            if (!Options.Demo && RedirectToRunningInstance()) return 0;
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
        try { key = AppInstance.FindOrRegisterForKey(InstanceKey); }
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
    static string? Arg(string name)
    {
        var a = Program.Args;
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
    /// <summary>--demo: przykładowe dane bez łączenia z serwerem.</summary>
    public static bool Demo => Program.Args.Contains("--demo");
    /// <summary>--screenshot plik.png: zrzut okna po starcie i wyjście.</summary>
    public static string? Screenshot => Arg("--screenshot");
    /// <summary>--view login|chat|friends|dm|profile|switcher</summary>
    public static string View => Arg("--view") ?? "chat";
    /// <summary>--server https://...: inny serwer (np. kopia testowa).</summary>
    public static string Server => Arg("--server") ?? Environment.GetEnvironmentVariable("GIBCORD_SERVER") ?? "https://gibcord.luisboon.pl";
    public static bool Hidden => Program.Args.Contains("--ukryty");
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
