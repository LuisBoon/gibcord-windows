using Microsoft.UI.Dispatching;

namespace GibCord.App;

/// <summary>
/// Strażnik okna (6.0.6): osobny wątek co sekundę „puka” do wątku okna. Gdy okno nie odpowiada dłużej niż 5 s,
/// zapisuje do dziennika „ZAWIESZENIE” razem z ostatnimi śladami (co aplikacja robiła tuż przed), a po odwieszeniu — ile to trwało.
/// Dzięki temu dziennik od użytkownika pokazuje, gdzie stoi okno.
/// </summary>
public static class Watchdog
{
    static long _pong = Environment.TickCount64;
    static volatile bool _stopped;

    /// <summary>Koniec pracy (zamykanie aplikacji) — bez alarmów.</summary>
    public static void Stop() => _stopped = true;
    static readonly string?[] _crumbs = new string?[24];
    static int _next;

    /// <summary>Krótki ślad (wołany z wątku okna w ważnych miejscach: najechanie na serwer, odświeżenia, przewijanie).</summary>
    public static void Crumb(string what)
    {
        lock (_crumbs) _crumbs[_next++ % _crumbs.Length] = $"{DateTime.Now:HH:mm:ss.fff} {what}";
    }

    static string Trail()
    {
        lock (_crumbs)
        {
            var n = _crumbs.Length;
            return string.Join(" | ", Enumerable.Range(0, n).Select(i => _crumbs[(_next + i) % n]).Where(s => s is not null));
        }
    }

    public static void Start(DispatcherQueue dq)
    {
        var t = new Thread(() =>
        {
            var hungSince = 0L;
            while (!_stopped)
            {
                Thread.Sleep(1000);
                if (_stopped) return;
                try { dq.TryEnqueue(DispatcherQueuePriority.High, () => Volatile.Write(ref _pong, Environment.TickCount64)); } catch { }
                var now = Environment.TickCount64;
                var late = now - Volatile.Read(ref _pong);
                if (late > 5000 && hungSince == 0)
                {
                    hungSince = now - late;
                    App.Log($"ZAWIESZENIE: okno nie odpowiada od {late / 1000} s. Ostatnie ślady: {Trail()}");
                }
                else if (late < 2000 && hungSince != 0)
                {
                    App.Log($"Okno znów odpowiada (stało ok. {(now - hungSince) / 1000} s). Ślady: {Trail()}");
                    hungSince = 0;
                }
            }
        }) { IsBackground = true, Name = "GibCord-straznik", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }
}
