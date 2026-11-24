using System.Runtime.InteropServices;
using GibCord.Core;
using Microsoft.UI.Dispatching;

namespace GibCord.App;

/// <summary>
/// Tryb gry „lekki” (--lekki): GibCord bez okna i bez WinUI — sam rdzeń (połączenie, głos), nakładka w grze,
/// zasobnik i powiadomienia. Pętla komunikatów Win32 i DispatcherQueue na tym samym wątku, więc timery i async działają jak w oknie.
/// Na razie wersja próbna do pomiaru w CI (tryb pokazowy).
/// </summary>
static partial class LightMode
{
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(ref MSG msg);
    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    public static int Run()
    {
        var ctl = DispatcherQueueController.CreateOnCurrentThread();
        var dq = ctl.DispatcherQueue;
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dq));
        Directory.CreateDirectory(App.DataDir);
        App.Prefs = Prefs.Load(App.DataDir);
        App.Log("Tryb lekki: start");

        void Ui(Action a) { if (dq.HasThreadAccess) a(); else dq.TryEnqueue(() => a()); }
        var client = Options.Demo
            ? new GibCordClient(new Uri(Options.DefaultServer), new MemoryTokenStore("demo"), null, Ui) { Offline = true }
            : new GibCordClient(new Uri(Options.Server), new DpapiTokenStore(Accounts.ActiveTokenPath), new DiskCache(Accounts.CachePath(App.Prefs.LastUserId)), Ui) { Prefs = App.Prefs };
        client.Voice.Options = App.Prefs.Voice;
        var overlay = new GameOverlay(client, dq, () => true);

        if (Options.Demo)
        {
            // CI: po 3 minutach koniec (skrypt pomiaru i tak zamyka proces wcześniej)
            var stop = dq.CreateTimer();
            stop.Interval = TimeSpan.FromMinutes(3);
            stop.Tick += (_, _) => PostQuitMessage(0);
            stop.Start();
            _ = Task.Run(async () =>
            {
                await Task.Delay(6500);
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                App.Log("Pomiar: tryb lekki gotowy");
            });
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        GC.KeepAlive(overlay);
        App.Log("Tryb lekki: koniec");
        return 0;
    }
}
