using System.Windows.Input;
using GibCord.App.Ui;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
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

    public static void Init(Action<long> open)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, a) =>
            {
                if (a.Arguments.TryGetValue("c", out var c) && long.TryParse(c, out var cid)) open(cid);
                else open(0);
            };
            AppNotificationManager.Default.Register();
            _ok = true;
        }
        catch (Exception ex) { App.Log("Powiadomienia niedostępne: " + ex.Message); }
    }

    public static void Show(string title, string body, long channelId, string group)
    {
        if (!_ok) return;
        try
        {
            var n = new AppNotificationBuilder()
                .AddArgument("c", channelId.ToString())
                .AddText(title)
                .AddText(string.IsNullOrWhiteSpace(body) ? "Nowa wiadomość" : body)
                .BuildNotification();
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

/// <summary>Ikona w zasobniku (obok zegara): GibCord działa po zamknięciu okna, jak Discord.</summary>
public static class Tray
{
    static TaskbarIcon? _icon;
    static bool _unread;
    public static bool Available => _icon is not null;

    public static void Init(MainWindow w)
    {
        try
        {
            var menu = new MenuFlyout();
            menu.Items.Add(new MenuFlyoutItem { Text = "Otwórz GibCord", Command = new RelayCommand(w.BringToFront) });
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = "Zamknij GibCord", Command = new RelayCommand(w.Quit) });
            _icon = new TaskbarIcon
            {
                ToolTipText = "GibCord",
                IconSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"))),
                ContextMenuMode = ContextMenuMode.PopupMenu,
                ContextFlyout = menu,
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(w.BringToFront),
            };
            _icon.ForceCreate(false);
        }
        catch (Exception ex) { App.Log("Zasobnik niedostępny: " + ex.Message); _icon = null; }
    }

    public static void SetUnread(bool unread, int n = 0)
    {
        if (_icon is null || _unread == unread) return;
        _unread = unread;
        try
        {
            _icon.IconSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", unread ? "tray-unread.ico" : "tray.ico")));
            _icon.ToolTipText = unread ? $"GibCord — nieprzeczytane: {n}" : "GibCord";
        }
        catch { }
    }

    public static void Dispose()
    {
        try { _icon?.Dispose(); } catch { }
        _icon = null;
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

    public static async Task Run(Action<string> onReady)
    {
        try
        {
            _mgr = new UpdateManager(new GithubSource(Repo, null, true));
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
        catch (Exception ex) { App.Log("Sprawdzanie aktualizacji: " + ex.Message); return "Nie udało się sprawdzić aktualizacji"; }
    }

    public static void Restart()
    {
        if (_mgr is null || _ready is null) return;
        try { _mgr.ApplyUpdatesAndRestart(_ready.TargetFullRelease); } catch (Exception ex) { App.Log("Restart z aktualizacją: " + ex.Message); }
    }
}
