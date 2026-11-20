using System.Runtime.InteropServices.WindowsRuntime;
using GibCord.App.Ui;
using GibCord.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GibCord.App;

/// <summary>Jedyne okno GibCorda: własny pasek tytułu, logowanie albo główny widok, zasobnik, powiadomienia.</summary>
public sealed class MainWindow : Window
{
    public GibCordClient Client { get; private set; } = null!;
    /// <summary>Mikrofon, głośniki i „naciśnij, aby mówić” dla rozmów (poza trybem pokazowym).</summary>
    public VoiceHost? Voice { get; private set; }
    /// <summary>Transmisja ekranu (Windows.Graphics.Capture).</summary>
    public ScreenShare? Screen { get; private set; }
    public Camera? Cam { get; private set; }
    Presence? _presence;
    GameOverlay? _overlay;
    public IntPtr Hwnd { get; }
    public DispatcherQueue Dq { get; }
    readonly Grid _root;
    readonly Grid _host = new();
    public TitleBarView TitleBar { get; }
    Shell? _shell;
    bool _quitting;
    int _lastUnread = -1;

    public MainWindow()
    {
        Dq = DispatcherQueue.GetForCurrentThread();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "GibCord";
        ExtendsContentIntoTitleBar = true;
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.ico")); } catch { }
        var tb = AppWindow.TitleBar;
        tb.PreferredHeightOption = TitleBarHeightOption.Standard;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = Theme.FgC;
        tb.ButtonInactiveForegroundColor = U.C("#99f6f2fb");
        tb.ButtonHoverBackgroundColor = U.C("#1Affffff");
        tb.ButtonHoverForegroundColor = Theme.FgC;
        tb.ButtonPressedBackgroundColor = U.C("#33ffffff");
        if (AppWindow.Presenter is OverlappedPresenter op) { op.PreferredMinimumWidth = 940; op.PreferredMinimumHeight = 560; }

        TitleBar = new TitleBarView(this);
        _root = new Grid().Rows("32", "*");
        _backdrop = Theme.Backdrop().At(0, 0, rowSpan: 2);
        _root.Add(_backdrop, TitleBar.At(0), _host.At(1));
        Content = _root;
        SetTitleBar(TitleBar.DragArea);
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();

        RestoreBounds();
        Activated += (_, e) =>
        {
            var active = e.WindowActivationState != WindowActivationState.Deactivated;
            Client?.SetWindowActive(active);
            TitleBar.SetActive(active);
            if (active && Client is not null && Client.ActiveChannelId != 0) Toasts.ClearFor(Client.ActiveChannelId);
        };
        AppWindow.Changed += (_, e) => { if (e.DidVisibilityChange) Client?.SetVisible(AppWindow.IsVisible); };
        AppWindow.Closing += (_, e) =>
        {
            SaveBounds();
            if (!_quitting && App.Prefs.CloseToTray && !Options.Demo && Tray.Available)
            {
                e.Cancel = true;
                AppWindow.Hide();
                Client?.SetWindowActive(false);
                Client?.SetVisible(false);
                return;
            }
            Quit();
        };
    }

    public void Start()
    {
        if (Options.Demo)
        {
            Client = Demo.CreateClient(UiRun);
            Client.Voice.Options = App.Prefs.Voice;
            if (Options.View == "login") ShowLogin(null); else ShowShell();
            Activate();
            if (Options.Screenshot is { } path) _ = Screenshot(path);
            return;
        }
        Client = new GibCordClient(new Uri(Options.Server), new DpapiTokenStore(Path.Combine(App.DataDir, "sesja.bin")), new DiskCache(Path.Combine(App.DataDir, "pamiec")), UiRun);
        Client.Voice.Options = App.Prefs.Voice;
        Screen = new ScreenShare(Client);
        Cam = new Camera(Client);
        Voice = new VoiceHost(Client, Dq, Screen, Cam);
        _presence = new Presence(Client);
        _overlay = new GameOverlay(Client, Dq, () => _presence.InGame);
        _keys = new GlobalKeys(Client);
        Client.SessionEnded += why => ShowLogin(why);
        Client.EmailGateRequired += ShowEmailGate;
        Client.Birthday += uid =>
        {
            var me = uid == Client.Store.Me.Id;
            var name = Client.Store.U(uid)?.Name ?? "Ktoś";
            var text = me ? "Wszystkiego najlepszego od całego GibCorda! 🎂" : $"{name} ma dziś urodziny 🎂 — złóż życzenia!";
            if (App.Prefs.Notifications) Toasts.Show(me ? "🎉 Sto lat!" : "🎂 Urodziny", text, 0, "bd" + uid);
            _shell?.Info(text);
        };
        Client.ReportCreated += r =>
        {
            var auto = r.Str("reason") == "automod";
            var srv = r.Long("serverId") is long sid ? Client.Store.ServerById(sid)?.Name : null;
            var title = auto ? "Automoderacja zatrzymała wiadomość" : "Nowe zgłoszenie";
            if (App.Prefs.Notifications) Toasts.Show(title, srv ?? "Rozmowa prywatna", 0, "rep" + r.Long("id"));
            _shell?.Info($"🚩 {title}{(srv is null ? "" : " · " + srv)} — zajrzyj do ustawień serwera → Zgłoszenia");
        };
        Client.EventReminder += (e, mins) =>
        {
            var srv = Client.Store.ServerById(e.ServerId)?.Name ?? "";
            var title = mins > 0 ? $"Za {mins} min: {e.Name}" : $"Zaczyna się: {e.Name}";
            if (App.Prefs.Notifications) Toasts.Show(title, srv, e.ChannelId ?? 0, "ev" + e.Id);
            _shell?.Info("📅 " + title);
        };
        Client.Notify += OnNotify;
        Client.FriendRequest += u => { if (App.Prefs.Notifications) Toasts.Show("Zaproszenie do znajomych", $"{u.Name} chce dodać Cię do znajomych", 0, "fr" + u.Id); };
        Client.Store.Changed += c => { if (c.Kind is ChangeKind.All or ChangeKind.Me) Dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SyncThemeFromAccount); };
        Client.Store.Changed += c => { if (c.Kind is ChangeKind.All or ChangeKind.Reads or ChangeKind.Dms or ChangeKind.MessageAdded or ChangeKind.Notify or ChangeKind.Servers) UpdateUnread(); };

        Toasts.Reply = (key, text) => Dq.TryEnqueue(async () =>
        {
            var err = await Client.Send(key, text);
            if (err is not null) Toasts.Show("Nie wysłano odpowiedzi", err, key, "err" + key);
            else Client.MarkRead(key);
        });
        Toasts.MarkRead = key => Dq.TryEnqueue(() => { Client.MarkRead(key); Toasts.ClearFor(key); });
        Toasts.Init(OpenFromNotification);
        DeepLink.Register();
        Tray.Init(this);
        HandleLaunchActivation(AppInstance.GetCurrent().GetActivatedEventArgs());
        AppInstance.GetCurrent().Activated += (_, e) => Dq.TryEnqueue(() => { BringToFront(); HandleLaunchActivation(e); });

        if (Client.HasToken) { ShowShell(); _ = Client.Start(); }
        else if (Handoff.Pending() is { } code) _ = LoginFromHandoff(code);
        else ShowLogin(null);
        if (Options.Hidden && Client.HasToken) Client.SetVisible(false); else Activate();
        _ = Updater.Run(v => _shell?.ShowUpdate(v));
    }

    /// <summary>Przesiadka ze starej aplikacji: logowanie kodem zostawionym przez nią na dysku.</summary>
    async Task LoginFromHandoff(string code)
    {
        try
        {
            var r = await Client.RedeemHandoff(code);
            if (r.Token is not null) { App.Log("Przesiadka: zalogowano kodem ze starej aplikacji"); ShowShell(); _ = Client.Start(); return; }
        }
        catch (Exception ex) { App.Log("Przesiadka: " + ex.Message); }
        ShowLogin("Witaj w nowym GibCordzie! Zaloguj się raz jeszcze — kolejne razy już nie trzeba.");
    }

    public void UiRun(Action a)
    {
        if (Dq.HasThreadAccess) a();
        else Dq.TryEnqueue(() => a());
    }

    // ───────────────────────── widoki ─────────────────────────

    public void ShowLogin(string? message)
    {
        _shell?.Detach();
        _shell = null;
        _host.Children.Clear();
        _host.Children.Add(new LoginPage(this, message));
        TitleBar.SetShellMode(false);
        UpdateUnread();
    }

    /// <summary>Pełny ekran (transmisja) — tytuł i ramka okna znikają, Esc wraca.</summary>
    public void SetFullScreen(bool on)
    {
        try
        {
            AppWindow.SetPresenter(on ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen : Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
            if (!on && AppWindow.Presenter is OverlappedPresenter op) { op.PreferredMinimumWidth = 940; op.PreferredMinimumHeight = 560; }
        }
        catch (Exception ex) { App.Log("Pełny ekran: " + ex.Message); }
        TitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    Grid _backdrop;

    /// <summary>Zmiana motywu: nowe kolory, nowe tło i przebudowa widoków (stan i rozmowa zostają).</summary>
    public void ApplyTheme(string name)
    {
        if (Theme.Name == name && App.Prefs.Theme == name) return;
        Theme.Apply(name);
        App.Prefs.Theme = Theme.Name;
        App.Prefs.Save();
        var nb = Theme.Backdrop().At(0, 0, rowSpan: 2);
        var i = _root.Children.IndexOf(_backdrop);
        if (i >= 0) _root.Children[i] = nb; else _root.Children.Insert(0, nb);
        _backdrop = nb;
        if (_shell is not null)
        {
            var reopen = _shell.SettingsOpen;
            ShowShell();
            if (reopen) Dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _shell?.ShowSettings("app"));
        }
    }

    /// <summary>Motyw z konta (ustawiony na stronie albo na innym urządzeniu).</summary>
    void SyncThemeFromAccount()
    {
        var t = Client.Store.Me.Settings.Str("theme");
        if (t is not null && t != Theme.Name && t != "light") ApplyTheme(t);
    }

    GlobalKeys? _keys;

    public void ShowEmailGate()
    {
        _shell?.Detach();
        _shell = null;
        _host.Children.Clear();
        _host.Children.Add(new EmailGatePage(this));
        TitleBar.SetShellMode(false);
    }

    public void ShowShell()
    {
        _host.Children.Clear();
        _shell = new Shell(this);
        _host.Children.Add(_shell);
        TitleBar.SetShellMode(true);
        UpdateTitleBarRegions();
    }

    /// <summary>Po zalogowaniu na stronie logowania.</summary>
    public void LoggedIn()
    {
        ShowShell();
        _ = Client.Start();
    }

    public Shell? CurrentShell => _shell;

    // ───────────────────────── powiadomienia i licznik ─────────────────────────

    void OnNotify(Message m)
    {
        Tones.Play("msg");
        if (!App.Prefs.Notifications) return;
        var s = Client.Store;
        Toasts.Show(s.NotificationTitle(m), s.PlainText(m), m.Key, "c" + m.Key, canReply: s.Can(m.ChannelId, Perm.SendMessages));
        if (Native.GetForegroundWindow() != Hwnd) Native.Flash(Hwnd);
    }

    void OpenFromNotification(long cid) => Dq.TryEnqueue(() =>
    {
        BringToFront();
        if (cid == 0) _shell?.OpenFriends();
        else if (cid < 0) _shell?.OpenThread(-cid);
        else _shell?.OpenChannel(cid);
    });

    void HandleLaunchActivation(AppActivationArguments a)
    {
        try
        {
            if (a.Kind == ExtendedActivationKind.AppNotification && a.Data is AppNotificationActivatedEventArgs n && n.Arguments.TryGetValue("c", out var c) && long.TryParse(c, out var cid))
                OpenFromNotification(cid);
            else if (a.Kind == ExtendedActivationKind.Launch && a.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la && DeepLink.Find(la.Arguments) is { } path)
                OpenPath(path);
            else if (a.Kind == ExtendedActivationKind.Protocol && a.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs pa && DeepLink.Parse(pa.Uri.AbsoluteUri) is { } path2)
                OpenPath(path2);
        }
        catch { }
    }

    /// <summary>Ścieżka z linku gibcord:// (np. /kanal/1/2, /i/KOD) — otwierana jak link do GibCorda w czacie.</summary>
    void OpenPath(string path) => Dq.TryEnqueue(async () =>
    {
        BringToFront();
        for (var i = 0; i < 50 && (_shell is null || !Client.Store.Loaded); i++) await Task.Delay(200); // po starcie poczekaj na stan
        if (_shell is null || path == "/") return;
        Ui.Links.Open(_shell, new Uri(Client.Api.BaseUri, path.TrimStart('/')));
    });

    void UpdateUnread()
    {
        var s = Client?.Store;
        if (s is null || !s.Loaded) { Title = "GibCord"; Tray.SetUnread(false); return; }
        var n = s.Mentions.Where(kv => !s.ChannelMuted(kv.Key)).Sum(kv => kv.Value) + s.DmUnreadTotal();
        if (n == _lastUnread) return;
        _lastUnread = n;
        Title = n > 0 ? $"({n}) GibCord" : "GibCord";
        Tray.SetUnread(n > 0, n);
        Badge.Set(n);
    }

    // ───────────────────────── okno ─────────────────────────

    public void BringToFront()
    {
        if (!AppWindow.IsVisible) AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } op) op.Restore();
        Activate();
        Native.SetForegroundWindow(Hwnd);
    }

    public void Quit()
    {
        _quitting = true;
        SaveBounds();
        Tray.Dispose();
        Toasts.Unregister();
        _ = Client?.DisposeAsync();
        Application.Current.Exit();
    }

    void RestoreBounds()
    {
        var w = App.Prefs.Window;
        try
        {
            if (w is { Length: 5 } && w[2] >= 940 && w[3] >= 560)
            {
                var area = DisplayArea.GetFromRect(new RectInt32(w[0], w[1], w[2], w[3]), DisplayAreaFallback.Nearest).WorkArea;
                var x = Math.Clamp(w[0], area.X - w[2] + 120, area.X + area.Width - 120);
                var y = Math.Clamp(w[1], area.Y, area.Y + area.Height - 80);
                AppWindow.MoveAndResize(new RectInt32(x, y, w[2], w[3]));
                if (w[4] == 1 && AppWindow.Presenter is OverlappedPresenter op) op.Maximize();
                return;
            }
            var da = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var scale = Native.DpiScale(Hwnd);
            int ww = (int)Math.Min(1280 * scale, da.Width * .9), hh = (int)Math.Min(800 * scale, da.Height * .9);
            AppWindow.MoveAndResize(new RectInt32(da.X + (da.Width - ww) / 2, da.Y + (da.Height - hh) / 2, ww, hh));
        }
        catch { }
    }

    void SaveBounds()
    {
        try
        {
            if (AppWindow.Presenter is not OverlappedPresenter op) return;
            var max = op.State == OverlappedPresenterState.Maximized;
            if (op.State == OverlappedPresenterState.Minimized) return;
            var p = AppWindow.Position; var sz = AppWindow.Size;
            App.Prefs.Window = max && App.Prefs.Window is { Length: 5 } old ? [old[0], old[1], old[2], old[3], 1] : [p.X, p.Y, sz.Width, sz.Height, max ? 1 : 0];
            App.Prefs.Save();
        }
        catch { }
    }

    /// <summary>Przyciski i wyszukiwarka na pasku tytułu muszą przyjmować kliknięcia (reszta paska przesuwa okno).</summary>
    public void UpdateTitleBarRegions()
    {
        try
        {
            if (TitleBar.XamlRoot is null) return;
            var scale = TitleBar.XamlRoot.RasterizationScale;
            var rects = TitleBar.Interactive().Where(e => e.Visibility == Visibility.Visible && e.ActualWidth > 0).Select(e =>
            {
                var r = e.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
                return new RectInt32((int)(r.X * scale), (int)(r.Y * scale), (int)(r.Width * scale), (int)(r.Height * scale));
            }).ToArray();
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rects);
        }
        catch { }
    }

    /// <summary>Zrzut okna do pliku PNG (CI: sprawdzenie wyglądu bez Windowsa pod ręką).</summary>
    async Task Screenshot(string path)
    {
        try
        {
            await Task.Delay(Options.View is "switcher" or "profile" or "emoji" ? 3500 : 2500);
            if (Options.View == "switcher") _shell?.OpenSwitcher();
            if (Options.View == "profile") _shell?.ShowProfileDemo();
            if (Options.View == "emoji") _shell?.ShowPickerDemo();
            if (Options.View is "switcher" or "profile" or "emoji") await Task.Delay(1200);
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_root);
            var pixels = await rtb.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var dpi = 96 * _root.XamlRoot.RasterizationScale;
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, dpi, dpi, pixels.ToArray());
            await enc.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex) { await File.WriteAllTextAsync(path + ".blad.txt", ex.ToString()); }
        Quit();
    }
}
