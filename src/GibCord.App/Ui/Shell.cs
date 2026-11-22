using GibCord.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace GibCord.App.Ui;

/// <summary>
/// Główny widok (makieta 1 „Klasyk, dopracowany”): serwery · kanały · czat · osoby.
/// Pilnuje nawigacji (z historią wstecz/dalej) i rozsyła zmiany stanu do części okna.
/// </summary>
public sealed class Shell : Grid
{
    public MainWindow W { get; }
    public GibCordClient C => W.Client;
    public Store S => C.Store;

    readonly ServerRail _rail;
    readonly Sidebar _side;
    readonly Grid _main = new();
    readonly MembersPanel _members;
    readonly Grid _overlay = new();
    readonly StackPanel _toasts = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = U.Th(0, 0, 0, 96) };
    readonly Border _connBar;
    readonly TextBlock _connText;
    readonly Border _updateBar;
    readonly Border _annBar = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
    readonly DispatcherQueueTimer _refresh;
    ChatView? _chat;
    ChatView? _thread;
    SearchPanel? _search;
    InboxPanel? _inbox;
    Task _openTask = Task.CompletedTask;
    bool _jumping;
    readonly Border _threadHost = new() { Width = 440, Background = Theme.Members, BorderBrush = Theme.Line, BorderThickness = U.Th(1, 0, 0, 0), Visibility = Visibility.Collapsed };
    FriendsView? _friends;
    IdeasView? _ideas;
    VoiceView? _voice;
    readonly DispatcherQueueTimer _talk;
    long _ringCid;
    bool _dirtyRail, _dirtySide, _dirtyMembers, _dirtyMain, _dirtyVoice, _navigated;

    public long ServerId { get; private set; } = -1; // 0 = rozmowy prywatne
    public long ChannelId { get; private set; }      // 0 w rozmowach prywatnych = Znajomi
    readonly List<(long S, long C)> _hist = [];
    int _hi = -1;

    public Shell(MainWindow w)
    {
        W = w;
        this.Cols("68", "240", "*", "auto", "auto");
        _rail = new ServerRail(this);
        _side = new Sidebar(this);
        _members = new MembersPanel(this);
        _main.Background = Theme.Main;
        _connText = U.T("Łączenie z GibCordem…", 12.5, U.Semi, Theme.OnAccent);
        _connBar = new Border { Background = Theme.Yellow, Padding = U.Th(12, 4), Child = _connText, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
        _updateBar = new Border { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
        var center = new Grid().Rows("auto", "auto", "auto", "*").Add(_connBar.At(0), _updateBar.At(1), _annBar.At(2), _main.At(3));
        _threadHost.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape && !e.Handled) { e.Handled = true; CloseSidePanel(); } };
        // panel wątku: szeroki w dużym oknie, węższy w małym (kanał obok ma zostać czytelny)
        SizeChanged += (_, e) =>
        {
            _threadHost.Width = Math.Clamp(e.NewSize.Width - 68 - 240 - 420, 300, 480);
            // wąskie okno: lista osób chowa się sama (przycisk w nagłówku pokazuje ją na chwilę), szerokie — wraca wg ustawienia
            // z marginesem (980/1020): przeciąganie okna przy granicy nie przełącza listy osób w kółko
            var narrow = _narrow ? e.NewSize.Width < 1020 : e.NewSize.Width < 980;
            if (narrow == _narrow) return;
            _narrow = narrow;
            _narrowMembers = null;
            _members.Refresh();
            _chat?.RefreshHeader();
        };
        this.Add(_rail.At(0, 0), _side.At(0, 1), center.At(0, 2), _threadHost.At(0, 3), _members.At(0, 4));
        _overlay.Add(_toasts);
        _overlay.At(0, 0, colSpan: 5);
        _overlay.IsHitTestVisible = true;
        _overlay.Background = null;
        Canvas.SetZIndex(_overlay, 100); // dzwonek, Ctrl+K i komunikaty nad ustawieniami i panelem admina
        this.Add(_overlay);
        // 6.1: zima — śnieg nad wszystkim oprócz okienek (bez klikania)
        var snow = new Snow();
        snow.At(0, 0, colSpan: 5);
        Canvas.SetZIndex(snow, 90);
        this.Add(snow);
        C.SeasonChanged += Winter.Notify;

        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(60);
        _refresh.IsRepeating = false;
        _refresh.Tick += (_, _) => Flush();

        S.Changed += OnChange;
        C.StatusChanged += OnStatus;
        C.Error += OnError;
        C.VoiceChanged += OnVoiceChanged;
        C.VoiceInfo += OnVoiceInfo;
        C.CallRing += OnCallRing;
        C.CallEnded += OnCallEnded;

        _talk = DispatcherQueue.CreateTimer();
        _talk.Interval = TimeSpan.FromMilliseconds(150);
        Avatar.LevelOf = uid => C.SpeakLevel(uid);
        _talk.Tick += (_, _) =>
        {
            _side.TickTalk(); _chat?.TickTalk();
            // w rozmowie obwódki pulsują w rytm głosu — częściej (ok. 16 razy na sekundę)
            var want = TimeSpan.FromMilliseconds(C.VoiceChannelId != 0 && Motion.On ? 60 : 150);
            if (_talk.Interval != want) _talk.Interval = want;
        };
        _talk.Start();

        Accel(VirtualKey.K, VirtualKeyModifiers.Control, OpenSwitcher);
        Accel(VirtualKey.F, VirtualKeyModifiers.Control, () => OpenSearch());
        Accel(VirtualKey.I, VirtualKeyModifiers.Control, () => OpenInbox());
        Accel((VirtualKey)191, VirtualKeyModifiers.Control, () => _ = ShowShortcuts()); // Ctrl+/
        Accel(VirtualKey.Divide, VirtualKeyModifiers.Control, () => _ = ShowShortcuts());
        KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape && FocusMode) { e.Handled = true; ToggleFocus(); Dirty(voice: true); } };
        Accel(VirtualKey.Left, VirtualKeyModifiers.Menu, Back);
        Accel(VirtualKey.Right, VirtualKeyModifiers.Menu, Forward);
        Accel(VirtualKey.Up, VirtualKeyModifiers.Menu, () => Step(-1));
        Accel(VirtualKey.Down, VirtualKeyModifiers.Menu, () => Step(1));
        Accel(VirtualKey.Up, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift, () => StepUnread(-1));
        Accel(VirtualKey.Down, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift, () => StepUnread(1));

        if (S.Loaded) Loaded += (_, _) => NavigateInitial();
        else ShowLoading();
        // start: okno z ostatnim kanałem (z pamięci) płynnie „zjeżdża” na miejsce
        Loaded += (_, _) => { if (!_entered) { _entered = true; Motion.SlideIn(this, new System.Numerics.Vector3(0, 14, 0)); } };
    }

    void Accel(VirtualKey key, VirtualKeyModifiers mod, Action a)
    {
        var k = new KeyboardAccelerator { Key = key, Modifiers = mod };
        // przy otwartym okienku skróty aplikacji milczą (klawisz trafia do pola w okienku)
        k.Invoked += (_, e) => { if (AppDialog.OpenCount > 0) return; e.Handled = true; a(); };
        KeyboardAccelerators.Add(k);
        // skróty są na całym oknie — bez tego Windows pokazywał przy najechaniu gdziekolwiek dymek „Ctrl+K”
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
    }

    void OnError(string msg) => Info(msg, error: true);

    /// <summary>Odpięcie od klienta i zwolnienie widoków (wylogowanie, bramka, zmiana motywu — inaczej stary widok żyje dalej w tle).</summary>
    public void Detach()
    {
        PipWindow.CloseAll(); // okienko transmisji należy do tej sesji okna
        S.Changed -= OnChange;
        C.StatusChanged -= OnStatus;
        C.Error -= OnError;
        C.VoiceChanged -= OnVoiceChanged;
        C.VoiceInfo -= OnVoiceInfo;
        C.CallRing -= OnCallRing;
        C.CallEnded -= OnCallEnded;
        _talk.Stop();
        _refresh.Stop();
        _soundTimer?.Stop();
        _voice?.Dispose(); _voice = null;
        _chat?.Dispose(); _chat = null;
        _thread?.Dispose(); _thread = null;
        _ideas?.Dispose(); _ideas = null;
        _inbox?.Dispose(); _inbox = null;
        Tones.StopRing();
    }

    // ───────────────────────── rozmowy ─────────────────────────

    long _camAfterJoin;
    DispatcherQueueTimer? _soundTimer;

    void OnVoiceChanged()
    {
        Dirty(side: true, voice: true);
        // rozmowa wideo: kamera włącza się, gdy połączenie z głosem jest gotowe
        if (_camAfterJoin != 0 && C.VoiceChannelId == _camAfterJoin && C.VoiceStatus == GibCord.Core.Voice.VoiceState.Connected)
        {
            _camAfterJoin = 0;
            if (W.Cam?.Active != true) VoiceUi.ToggleCamera(this);
        }
        // etykiety soundboardu znikają po ~3 s
        if (C.SoundLabels.Count > 0)
        {
            _soundTimer ??= DispatcherQueue.CreateTimer();
            _soundTimer.Interval = TimeSpan.FromSeconds(3.7);
            _soundTimer.IsRepeating = false;
            _soundTimer.Tick -= SoundTick;
            _soundTimer.Tick += SoundTick;
            _soundTimer.Stop();
            _soundTimer.Start();
        }
    }

    void SoundTick(DispatcherQueueTimer t, object o)
    {
        var now = Environment.TickCount64;
        foreach (var k in C.SoundLabels.Where(x => x.Value.Until <= now).Select(x => x.Key).ToList()) C.SoundLabels.Remove(k);
        Dirty(side: true, voice: true);
    }

    public void StartVideoCall(long dmId)
    {
        _camAfterJoin = dmId;
        if (C.VoiceChannelId != dmId) _ = C.StartCall(dmId);
        else OnVoiceChanged();
    }
    void OnVoiceInfo(string msg, bool error) => Info(msg, error);

    void OnCallRing(long cid, long from)
    {
        if (C.VoiceChannelId == cid) return;
        _ringCid = cid;
        CloseOverlay();
        ShowOverlay(VoiceUi.RingCard(this, cid, from));
        Tones.StartRing();
        if (!W.AppWindow.IsVisible) Toasts.Show($"{S.NameOf(from, null)} dzwoni", "Kliknij, żeby odebrać w GibCordzie", cid, "call" + cid);
    }

    void OnCallEnded(long cid)
    {
        if (_ringCid != cid) return;
        _ringCid = 0;
        Tones.StopRing();
        CloseOverlay();
    }

    /// <summary>Klik w kanał głosowy: wejście do rozmowy i widok kanału.</summary>
    public void JoinVoiceChannel(long serverId, long cid)
    {
        if (C.VoiceChannelId != cid) _ = C.JoinVoice(cid);
        Go(serverId, cid);
    }

    public void StartCall(long dmId) { if (C.VoiceChannelId != dmId) _ = C.StartCall(dmId); }

    public void RefreshVoice() => Dirty(voice: true, side: true);
    public void RefreshRail() => Dirty(rail: true);
    /// <summary>Kilka kont: liczniki kont w tle przy panelu „ja”.</summary>
    public void RefreshAccounts() => Dirty(side: true);

    void ShowLoading()
    {
        _main.Children.Clear();
        _main.Children.Add(U.V(16, Splash.Gibek(112),
            U.T("Wczytywanie GibCorda…", 14, null, Theme.Mu).With(t => t.HorizontalAlignment = HorizontalAlignment.Center)).With(p => { p.HorizontalAlignment = HorizontalAlignment.Center; p.VerticalAlignment = VerticalAlignment.Center; }));
    }

    // ───────────────────────── zmiany stanu ─────────────────────────

    void OnChange(StoreChange c)
    {
        switch (c.Kind)
        {
            case ChangeKind.All:
                if (!_navigated) { NavigateInitial(); return; }
                // po ponownym wczytaniu: kanał mógł zniknąć (np. odebrane uprawnienia)
                if (ServerId > 0 && S.ServerById(ServerId) is null) { Go(0, 0); return; }
                if (ChannelId != 0 && S.ChannelById(ChannelId) is null && S.DmById(ChannelId) is null) { Go(ServerId, 0); return; }
                Dirty(rail: true, side: true, members: true, main: true);
                DrawAnnouncement();
                return;
            case ChangeKind.MessageAdded or ChangeKind.MessageUpdated or ChangeKind.MessageRemoved or ChangeKind.MessagesReset or ChangeKind.Typing:
                if (c.ChannelId == ChannelId) _chat?.OnChange(c);
                else if (_thread is not null && c.ChannelId == _thread.Key) _thread.OnChange(c);
                if (c.Kind == ChangeKind.MessageAdded) Dirty(rail: true, side: true);
                return;
            case ChangeKind.Thread:
                if (_thread is not null && c.Id == _thread.Tid && S.ThreadById(c.Id) is null) { CloseThread(); Info("Ten wątek został usunięty."); return; }
                if (c.ChannelId == ChannelId || c.Id == _thread?.Tid) Dirty(main: true);
                return;
            case ChangeKind.Reads:
                Dirty(rail: true, side: true);
                return;
            case ChangeKind.Presence or ChangeKind.User:
                Dirty(members: true, side: ServerId == 0 || c.Id == S.Me.Id, main: ServerId == 0 && ChannelId == 0);
                if (c.Id == S.Me.Id) Dirty(side: true);
                return;
            case ChangeKind.Me:
                Dirty(side: true);
                return;
            case ChangeKind.Server or ChangeKind.Members:
                if (c.Kind == ChangeKind.Server && c.Id == ServerId && ChannelId != 0 && S.ChannelById(ChannelId) is null) { Go(ServerId, 0); return; }
                Dirty(rail: true, side: c.Id == ServerId, members: c.Id == ServerId, main: c.Id == ServerId);
                return;
            case ChangeKind.Servers:
                if (ServerId > 0 && S.ServerById(ServerId) is null) { Go(0, 0); return; }
                Dirty(rail: true);
                return;
            case ChangeKind.Dms:
                Dirty(rail: true, side: ServerId == 0, main: ServerId == 0);
                return;
            case ChangeKind.Friends:
                Dirty(side: ServerId == 0, main: ServerId == 0 && ChannelId == 0, rail: true);
                return;
            case ChangeKind.Watch:
                Dirty(side: true, voice: true);
                return;
            case ChangeKind.Voice:
                Dirty(side: S.ServerOfChannel(c.Id)?.Id == ServerId || c.Id == C.VoiceChannelId, voice: c.Id == ChannelId);
                return;
            case ChangeKind.Notify or ChangeKind.Blocked:
                Dirty(rail: true, side: true, main: c.Kind == ChangeKind.Blocked, members: c.Kind == ChangeKind.Blocked);
                if (c.Kind == ChangeKind.Blocked) { _chat?.Rerender(); _thread?.Rerender(); }
                return;
            case ChangeKind.Announcement:
                DrawAnnouncement();
                return;
            case ChangeKind.Notes:
                Dirty(members: true);
                return;
        }
    }

    void Dirty(bool rail = false, bool side = false, bool members = false, bool main = false, bool voice = false)
    {
        _dirtyRail |= rail; _dirtySide |= side; _dirtyMembers |= members; _dirtyMain |= main; _dirtyVoice |= voice;
        if (!_refresh.IsRunning) _refresh.Start();
    }

    void Flush()
    {
        Watchdog.Crumb($"odśwież{(_dirtyRail ? " serwery" : "")}{(_dirtySide ? " kolumna" : "")}{(_dirtyMembers ? " osoby" : "")}{(_dirtyMain ? " nagłówek" : "")}{(_dirtyVoice ? " głos" : "")}");
        if (_dirtyRail) _rail.Refresh();
        if (_dirtySide) _side.Refresh();
        if (_dirtyMembers) _members.Refresh();
        if (_dirtyMain) { _chat?.RefreshHeader(); _thread?.RefreshHeader(); _friends?.Refresh(); }
        if (_dirtyVoice) { _voice?.Refresh(); _chat?.RefreshCall(); }
        _dirtyRail = _dirtySide = _dirtyMembers = _dirtyMain = _dirtyVoice = false;
    }

    void OnStatus(GatewayStatus s, TimeSpan? delay)
    {
        var show = s is GatewayStatus.Reconnecting or GatewayStatus.Connecting && S.Loaded;
        _connBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _connText.Text = s == GatewayStatus.Reconnecting && delay is { } d
            ? $"Brak połączenia z GibCordem — ponowna próba za {Math.Max(1, (int)Math.Round(d.TotalSeconds))} s…"
            : "Łączenie z GibCordem…";
    }

    // ───────────────────────── nawigacja ─────────────────────────

    void NavigateInitial()
    {
        if (_navigated || !S.Loaded) return;
        _navigated = true;
        if (!Options.Demo && !C.Offline) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = WhatsNew());
        var p = App.Prefs;
        if (Options.Demo) { Demo.Navigate(this); return; }
        if (p.LastServer > 0 && S.ServerById(p.LastServer) is { } srv)
            Go(srv.Id, p.LastChannel.TryGetValue(srv.Id, out var lc) && S.ChannelById(lc) is { IsVoice: false } ? lc : 0);
        else if (p.LastServer == 0) Go(0, p.LastDm != 0 && S.DmById(p.LastDm) is not null ? p.LastDm : 0);
        else if (ServerRail.Ordered(S).FirstOrDefault() is { } first) Go(first.Id, 0);
        else Go(0, 0);
    }

    /// <summary>Przejście: serwer (0 = prywatne) i kanał (0 = pierwszy tekstowy / Znajomi).</summary>
    public void Go(long serverId, long channelId, bool record = true)
    {
        Watchdog.Crumb($"przejście: serwer {serverId}, kanał {channelId}");
        _navigated = true;
        if (serverId > 0)
        {
            var srv = S.ServerById(serverId);
            if (srv is null) { serverId = 0; channelId = 0; }
            else if (channelId == 0 || S.ChannelById(channelId) is null)
                channelId = (App.Prefs.LastChannel.TryGetValue(serverId, out var lc) && S.ChannelById(lc) is { IsVoice: false } ? lc : S.FirstTextChannel(srv)?.Id) ?? 0;
        }
        else if (channelId != 0 && S.DmById(channelId) is null) channelId = 0;

        var same = serverId == ServerId && channelId == ChannelId;
        if (!same && _thread is not null && _thread.Cid != channelId) CloseThread(refresh: false);
        if (!same && _search is not null && serverId != ServerId && !_jumping) CloseSearch(refresh: false);
        ServerId = serverId;
        ChannelId = channelId;
        if (record && !same)
        {
            if (_hi < _hist.Count - 1) _hist.RemoveRange(_hi + 1, _hist.Count - _hi - 1);
            _hist.Add((serverId, channelId));
            if (_hist.Count > 100) _hist.RemoveAt(0);
            _hi = _hist.Count - 1;
        }
        W.TitleBar.SetNav(_hi > 0, _hi < _hist.Count - 1);

        if (serverId > 0) { App.Prefs.LastServer = serverId; if (channelId != 0 && S.ChannelById(channelId) is { IsVoice: false }) App.Prefs.LastChannel[serverId] = channelId; }
        else { App.Prefs.LastServer = 0; App.Prefs.LastDm = channelId; }
        App.Prefs.Save();

        _rail.Refresh();
        _side.Refresh();
        if (same && _main.Children.Count > 0 && (_chat is not null || _friends is not null || _voice is not null)) { _chat?.FocusComposer(); return; }
        ShowMain();
        _members.Refresh();
    }

    void ShowMain()
    {
        _chat?.Dispose();
        _chat = null;
        _voice?.Dispose();
        _voice = null;
        _ideas?.Dispose();
        _ideas = null;
        _friends = null;
        _main.Children.Clear();
        if (ChannelId != 0 && S.ChannelById(ChannelId) is { Kind: "ideas" })
        {
            C.CloseChannel();
            _ideas = new IdeasView(this, ChannelId);
            _main.Children.Add(_ideas);
            Motion.SlideIn(_ideas, new System.Numerics.Vector3(0, 10, 0));
        }
        else if (ChannelId != 0 && S.ChannelById(ChannelId) is { IsVoice: true })
        {
            C.CloseChannel();
            _voice = new VoiceView(this, ChannelId);
            _main.Children.Add(_voice);
            Motion.SlideIn(_voice, new System.Numerics.Vector3(0, 10, 0));
        }
        else if (ChannelId != 0)
        {
            _chat = new ChatView(this, ChannelId);
            _main.Children.Add(_chat);
            _openTask = C.OpenChannel(ChannelId);
            Toasts.ClearFor(ChannelId);
            Inbox.SeenChannel(ChannelId);
        }
        else if (ServerId == 0)
        {
            C.CloseChannel();
            _friends = new FriendsView(this);
            _main.Children.Add(_friends);
            Motion.SlideIn(_friends, new System.Numerics.Vector3(0, 10, 0));
        }
        else
        {
            C.CloseChannel();
            _main.Children.Add(U.V(10,
                U.I("", 40, Theme.Mu2),
                U.T("Na tym serwerze nie ma jeszcze kanału, który widzisz.", 14, null, Theme.Mu)).With(p => { p.HorizontalAlignment = HorizontalAlignment.Center; p.VerticalAlignment = VerticalAlignment.Center; }));
        }
    }

    public void OpenChannel(long cid)
    {
        if (S.ServerOfChannel(cid) is { } srv) Go(srv.Id, cid);
        else if (S.DmById(cid) is not null) Go(0, cid);
    }

    public void OpenFriends() => Go(0, 0);

    // ───────────────────────── wątki (panel z boku) ─────────────────────────

    public bool ThreadOpen => _thread is not null || _search is not null || _inbox is not null;
    public ChatView? CurrentChat => _chat;

    // ── wyszukiwarka (ten sam panel z prawej co wątek) ──

    public void OpenSearch(string? text = null)
    {
        if (!S.Loaded) return;
        if (_search is not null) { _search.Focus(text); return; }
        CloseThread(refresh: false);
        CloseInbox();
        _search = new SearchPanel(this, text);
        _threadHost.Child = _search;
        _threadHost.Visibility = Visibility.Visible;
        Motion.SlideIn(_threadHost, new System.Numerics.Vector3(40, 0, 0));
        _members.Refresh();
        _chat?.RefreshHeader();
    }

    void CloseSearch(bool refresh)
    {
        if (_search is null) return;
        _search = null;
        _threadHost.Child = null;
        _threadHost.Visibility = Visibility.Collapsed;
        if (refresh) { _members.Refresh(); _chat?.RefreshHeader(); _chat?.FocusComposer(); }
    }

    public void CloseSidePanel() { CloseInbox(); CloseSearch(refresh: true); CloseThread(); }

    // ── skrzynka (wzmianki, zapisane, przypomnienia) — ten sam panel z prawej ──
    public void OpenInbox(string tab = "mentions")
    {
        if (!S.Loaded) return;
        if (_inbox is not null) { CloseInbox(); return; }
        CloseThread(refresh: false);
        CloseSearch(refresh: false);
        _inbox = new InboxPanel(this, tab);
        _threadHost.Child = _inbox;
        _threadHost.Visibility = Visibility.Visible;
        Motion.SlideIn(_threadHost, new System.Numerics.Vector3(40, 0, 0));
        _members.Refresh();
    }

    void CloseInbox()
    {
        if (_inbox is null) return;
        _inbox.Dispose();
        _inbox = null;
        _threadHost.Child = null;
        _threadHost.Visibility = Visibility.Collapsed;
        _members.Refresh();
    }
    public bool SearchOpen => _search is not null;

    /// <summary>Skok do wiadomości z wyniku wyszukiwania (albo linku): kanał / rozmowa, a dla wiadomości w wątku — panel wątku.</summary>
    public async void JumpToMessage(long cid, long mid, long? threadId = null)
    {
        if (threadId is long tid) { OpenThread(tid, mid); return; }
        if (ChannelId != cid)
        {
            _jumping = true;
            try
            {
                if (S.ServerOfChannel(cid) is { } srv) Go(srv.Id, cid);
                else if (S.DmById(cid) is not null) Go(0, cid);
                else { Info("Nie masz już dostępu do tej rozmowy.", true); return; }
            }
            finally { _jumping = false; }
            try { await _openTask; } catch { }
        }
        if (_chat is not null && _chat.Cid == cid) await _chat.JumpTo(mid);
    }
    public long OpenThreadId => _thread?.Tid ?? 0;

    /// <summary>Otwiera wątek w panelu obok kanału (przechodzi na kanał-rodzica, jeśli trzeba). Opcjonalnie skacze do wiadomości.</summary>
    public async void OpenThread(long tid, long jumpTo = 0)
    {
        var t = S.ThreadById(tid);
        if (t is null || t.ChannelId == 0)
        {
            try { var (ft, _) = await C.Api.GetThread(tid); t = S.MergeThread(ft); }
            catch (Exception ex) { Info(GibCordClient.Friendly(ex), true); return; }
        }
        if (S.ServerOfChannel(t.ChannelId) is not { } srv) { Info("Nie masz dostępu do tego kanału.", true); return; }
        if (ChannelId != t.ChannelId) Go(srv.Id, t.ChannelId);
        if (_thread?.Tid == tid) { if (jumpTo > 0) await _thread.JumpTo(jumpTo); _thread.FocusComposer(); return; }
        CloseThread(refresh: false);
        CloseSearch(refresh: false);
        CloseInbox();
        var view = new ChatView(this, t.ChannelId, tid);
        _thread = view;
        _threadHost.Child = view;
        _threadHost.Visibility = Visibility.Visible;
        Motion.SlideIn(_threadHost, new System.Numerics.Vector3(40, 0, 0));
        _members.Refresh();
        Toasts.ClearFor(-tid);
        var (_, starter, err) = await C.OpenThread(tid);
        if (_thread != view) return;
        if (err is not null) { Info(err, true); CloseThread(); return; }
        view.SetStarter(starter);
        view.RefreshHeader();
        if (jumpTo > 0) await view.JumpTo(jumpTo);
    }

    public void CloseThread() => CloseThread(refresh: true);

    void CloseThread(bool refresh)
    {
        if (_thread is null) return;
        _search = null;
        _thread.Dispose();
        _thread = null;
        _threadHost.Child = null;
        _threadHost.Visibility = Visibility.Collapsed;
        C.CloseThread();
        if (refresh) { _members.Refresh(); _chat?.FocusComposer(); }
    }

    /// <summary>Kanał specjalny (np. „changelog” = #co-nowego na serwerze społeczności).</summary>
    public void OpenChannelByKind(string kind)
    {
        var c = S.Servers.Where(s => s.Official).Concat(S.Servers).SelectMany(s => s.Channels).FirstOrDefault(c => c.Kind == kind);
        if (c is not null) OpenChannel(c.Id);
        else Info("Nie znalazłem tego kanału.");
    }

    public async void OpenDmWith(long uid)
    {
        var (dm, err) = await C.OpenDm(uid);
        if (err is not null) { Info(err, true); return; }
        Go(0, dm);
    }

    public void Back() { if (_hi > 0) { _hi--; var (s, c) = _hist[_hi]; Go(s, c, record: false); } }
    public void Forward() { if (_hi < _hist.Count - 1) { _hi++; var (s, c) = _hist[_hi]; Go(s, c, record: false); } }

    /// <summary>Alt+↑/↓: poprzedni / następny kanał tekstowy na liście.</summary>
    void Step(int dir)
    {
        List<long> ids = ServerId > 0 && S.ServerById(ServerId) is { } srv
            ? S.ChannelTree(srv).SelectMany(g => g.Channels).Where(c => !c.IsVoice).Select(c => c.Id).ToList()
            : S.DmsSorted().Select(d => d.Id).ToList();
        if (ids.Count == 0) return;
        var i = ids.IndexOf(ChannelId);
        var j = Math.Clamp(i < 0 ? 0 : i + dir, 0, ids.Count - 1);
        if (ids[j] != ChannelId) Go(ServerId, ids[j]);
    }

    async Task ShowShortcuts()
    {
        if (M.DialogOpen(XamlRoot)) return;
        var list = new StackPanel { Spacing = 8 };
        foreach (var (k, w) in SettingsPage.Shortcuts) list.Add(SettingsPage.Shortcut(k, w));
        var d = new AppDialog
        {
            Title = "Skróty klawiszowe", Content = new ScrollViewer { Content = list, MaxHeight = 460 }.With(s => s.Width = 480), CloseButtonText = "Zamknij",
            XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        await d.ShowAsync();
    }

    /// <summary>Alt+Shift+↑/↓: poprzedni / następny nieprzeczytany kanał (wszystkie serwery po kolei, potem rozmowy prywatne).</summary>
    void StepUnread(int dir)
    {
        var all = new List<(long S, long C)>();
        foreach (var srv in ServerRail.Ordered(S))
            foreach (var c in S.ChannelTree(srv).SelectMany(g => g.Channels).Where(c => !c.IsVoice)) all.Add((srv.Id, c.Id));
        foreach (var d in S.DmsSorted()) all.Add((0, d.Id));
        var i = all.FindIndex(x => x.C == ChannelId);
        for (var k = 1; k <= all.Count; k++)
        {
            var j = ((i < 0 ? (dir > 0 ? -1 : 0) : i) + dir * k % all.Count + all.Count * 2) % all.Count;
            var (sid, cid) = all[j];
            if (cid != ChannelId && S.IsUnread(cid) && !S.ChannelMuted(cid)) { Go(sid, cid); return; }
        }
        Info("Wszystko przeczytane 🎉");
    }

    /// <summary>CI (--motion): przejście na następny kanał tekstowy tego serwera.</summary>
    public void DemoMotion()
    {
        if (S.ServerById(ServerId) is not { } srv) return;
        var next = S.ChannelTree(srv).SelectMany(g => g.Channels).FirstOrDefault(c => !c.IsVoice && c.Id != ChannelId);
        if (next is not null) Go(srv.Id, next.Id);
    }

    bool _narrow;
    bool? _narrowMembers;
    public bool MembersVisible => _narrow ? _narrowMembers == true : App.Prefs.MembersPanel;
    public void ToggleMembers()
    {
        if (_narrow) _narrowMembers = !MembersVisible; // w wąskim oknie tylko na teraz — ustawienie dla szerokiego zostaje
        else
        {
            App.Prefs.MembersPanel = !App.Prefs.MembersPanel;
            App.Prefs.Save();
        }
        _members.Refresh();
        if (MembersVisible) Motion.SlideIn(_members, new System.Numerics.Vector3(40, 0, 0));
        _chat?.RefreshHeader();
    }

    // ───────────────────────── okienka ─────────────────────────

    public void OpenSwitcher()
    {
        if (!S.Loaded) return;
        var sw = new QuickSwitcher(this);
        ShowOverlay(sw, sw.FocusBox);
    }

    /// <summary>Okienko na środku z przyciemnionym tłem (zamyka Esc albo klik obok).</summary>
    Func<Task>? _overlayClosing;
    bool _entered;
    Grid? _overlayDim;

    /// <param name="closing">Animacja przed zamknięciem (np. zdjęcie wraca na miejsce w czacie).</param>
    /// <param name="pop">Treść lekko wjeżdża (zwykłe okienka); podgląd zdjęcia ma własny przelot.</param>
    public void ShowOverlay(FrameworkElement content, Action? focus = null, Func<Task>? closing = null, bool pop = true)
    {
        var dim = new Grid { Background = U.B("#80080414") };
        _overlayClosing = closing;
        _overlayDim = dim;
        Motion.FadeIn(dim, 140);
        if (pop) Motion.SlideIn(content, new System.Numerics.Vector3(0, 12, 0));
        dim.Add(content.With(c => { c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Top; c.Margin = U.Th(0, 70, 0, 0); }));
        dim.Tapped += (_, e) => { if (ReferenceEquals(e.OriginalSource, dim)) CloseOverlay(); };
        dim.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { CloseOverlay(); e.Handled = true; } };
        _overlay.Children.Insert(Math.Max(0, _overlay.Children.IndexOf(_toasts)), dim);
        focus?.Invoke();
    }

    public void CloseOverlay()
    {
        if (_overlayClosing is { } f)
        {
            _overlayClosing = null;
            _ = CloseAfter(f, _overlayDim);
            return;
        }
        RemoveOverlay();
    }

    async Task CloseAfter(Func<Task> f, Grid? dim)
    {
        if (dim is not null) Motion.FadeOut(dim, 220);
        try { await f(); } catch { }
        // zdejmujemy tylko to okienko: w trakcie przelotu mogło się otworzyć inne (dzwonek rozmowy, Ctrl+K) — nie może zniknąć
        if (dim is null) { RemoveOverlay(); return; }
        _overlay.Children.Remove(dim);
        if (ReferenceEquals(_overlayDim, dim)) { _overlayDim = null; _overlayClosing = null; _chat?.FocusComposer(); }
    }

    void RemoveOverlay()
    {
        _overlayClosing = null;
        _overlayDim = null;
        for (var i = _overlay.Children.Count - 1; i >= 0; i--)
            if (_overlay.Children[i] != _toasts) _overlay.Children.RemoveAt(i);
        _chat?.FocusComposer();
    }

    /// <summary>Krótki komunikat na dole okna.</summary>
    public void Info(string msg, bool error = false)
    {
        var b = new Border
        {
            Background = error ? U.B("#F0b8364f") : Theme.Toast, CornerRadius = new CornerRadius(12), Padding = U.Th(16, 10),
            BorderBrush = U.W(.14), BorderThickness = U.Th(1), MaxWidth = 520,
            Child = U.HG(10, U.I(error ? "" : "", 15), U.T(msg, 13.5, U.Semi, wrap: true)),
        };
        _toasts.Children.Add(b);
        while (_toasts.Children.Count > 3) _toasts.Children.RemoveAt(0);
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(error ? 6 : 3.5);
        t.IsRepeating = false;
        t.Tick += (_, _) => _toasts.Children.Remove(b);
        t.Start();
    }

    // ── ustawienia na całe okno ──
    SettingsPage? _settings;

    public bool SettingsOpen => _settings is not null;

    public void ShowSettings(string section = "profile")
    {
        if (_settings is not null) CloseSettings();
        _settings = new SettingsPage(this, section);
        _settings.At(0, 0, colSpan: 5);
        Children.Add(_settings);
        _settings.Loaded += (_, _) => _settings?.Focus(FocusState.Programmatic);
    }

    public void CloseSettings()
    {
        if (_settings is null) return;
        Children.Remove(_settings);
        _settings = null;
        _chat?.FocusComposer();
    }

    AdminPage? _admin;

    public void ShowAdmin(string section = "stats")
    {
        if (!S.Me.IsAdmin) return;
        CloseAdmin();
        CloseSettings();
        _admin = new AdminPage(this, section);
        _admin.At(0, 0, colSpan: 5);
        Children.Add(_admin);
        _admin.Loaded += (_, _) => _admin?.Focus(FocusState.Programmatic);
    }

    public void CloseAdmin()
    {
        if (_admin is null) return;
        Children.Remove(_admin);
        _admin = null;
        _chat?.FocusComposer();
    }

    ServerSettingsPage? _srvSettings;

    public void ShowServerSettings(long serverId, string section = "overview", long? selectRole = null, long? editChannel = null)
    {
        CloseServerSettings();
        _srvSettings = new ServerSettingsPage(this, serverId, section, selectRole);
        _srvSettings.At(0, 0, colSpan: 5);
        Children.Add(_srvSettings);
        _srvSettings.Loaded += (_, _) =>
        {
            _srvSettings?.Focus(FocusState.Programmatic);
            if (editChannel is long ec) _srvSettings?.EditChannelById(ec);
        };
    }

    public void CloseServerSettings()
    {
        if (_srvSettings is null) return;
        Children.Remove(_srvSettings);
        _srvSettings = null;
        _chat?.FocusComposer();
    }

    // ── pełny ekran transmisji: chowa kolumny, okno na cały ekran ──
    public bool FocusMode { get; private set; }

    public void ToggleFocus()
    {
        FocusMode = !FocusMode;
        var v = FocusMode ? Visibility.Collapsed : Visibility.Visible;
        _rail.Visibility = v;
        _side.Visibility = v;
        if (FocusMode) { _members.Visibility = Visibility.Collapsed; _threadHost.Visibility = Visibility.Collapsed; }
        else { _members.Refresh(); _threadHost.Visibility = _threadHost.Child is null ? Visibility.Collapsed : Visibility.Visible; }
        W.SetFullScreen(FocusMode);
    }

    /// <summary>„Co nowego w GibCordzie” po aktualizacji serwisu (raz na wydanie, jak na stronie).</summary>
    async Task WhatsNew()
    {
        try
        {
            await Task.Delay(2500);
            var r = await C.Api.WhatsNew();
            if (r.Prop("releases") is not { ValueKind: System.Text.Json.JsonValueKind.Array } rel || rel.GetArrayLength() == 0) return;
            var panel = new StackPanel { Spacing = 14, Width = 460 };
            foreach (var x in rel.EnumerateArray())
            {
                panel.Add(U.HG(8, U.Box(U.T(x.Str("version") ?? "", 11, U.Bold, Theme.OnAccent), Theme.Accent, 6, U.Th(6, 2)), U.T(x.Str("title") ?? "", 16, U.Bold, wrap: true)));
                if (x.Prop("items").ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var it in x.Prop("items").EnumerateArray())
                        panel.Add(new Grid().Cols("36", "*").Add(U.T(it.Str("icon") ?? "•", 18).At(0, 0),
                            U.V(2, U.T(it.Str("title") ?? "", 13.5, U.Bold, wrap: true), U.T(it.Str("text") ?? "", 12.5, null, Theme.Mu, wrap: true)).At(0, 1)));
            }
            await M.WaitNoDialog(XamlRoot);
            var d = new AppDialog
            {
                Title = "Co nowego w GibCordzie", Content = new ScrollViewer { Content = panel, MaxHeight = 520 }, PrimaryButtonText = "Super!",
                SecondaryButtonText = "Cała historia zmian", XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
            };
            var res = await d.ShowAsync();
            try { await C.Api.WhatsNewSeen(); } catch { }
            if (res == ContentDialogResult.Secondary) OpenChannelByKind("changelog");
        }
        catch (Exception ex) { App.Log("Co nowego: " + ex.Message); }
    }

    /// <summary>Ogłoszenie właścicieli GibCorda (pasek u góry, zamykany krzyżykiem — do następnego ogłoszenia).</summary>
    void DrawAnnouncement()
    {
        var a = S.Announcement;
        if (a is null || string.IsNullOrWhiteSpace(a.Text) || App.Prefs.AnnouncementSeen == (a.At ?? 0)) { _annBar.Visibility = Visibility.Collapsed; return; }
        var warn = a.Level == "warn";
        _annBar.Background = warn ? U.B("#E6b8364f") : U.B("#E62a5a6e");
        _annBar.Padding = U.Th(14, 6, 6, 6);
        _annBar.Child = new Grid().Cols("auto", "*", "auto").Add(
            U.I("\uE789", 15).With(i => i.Margin = U.Th(0, 0, 10, 0)).At(0, 0),
            new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center }
                .With(t => { t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Ogłoszenie GibCorda: ", FontWeight = U.Bold }); t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = a.Text }); }).At(0, 1),
            U.IconButton(G.Close, "Zamknij", () => { App.Prefs.AnnouncementSeen = a.At ?? 0; App.Prefs.Save(); _annBar.Visibility = Visibility.Collapsed; }, 12, 28).At(0, 2));
        _annBar.Visibility = Visibility.Visible;
    }

    /// <summary>Komunikat z przyciskiem (np. „Cofnij”) — dłużej na ekranie.</summary>
    public void InfoUndo(string msg, string action, Action a)
    {
        var btn = new HyperlinkButton { Content = U.T(action, 13, U.Bold, Theme.Accent), Padding = U.Th(6, 0) };
        var b = new Border
        {
            Background = Theme.Toast, CornerRadius = new CornerRadius(12), Padding = U.Th(16, 8), BorderBrush = U.W(.14), BorderThickness = U.Th(1), MaxWidth = 520,
            Child = U.H(10, U.T(msg, 13.5, U.Semi), btn),
        };
        btn.Click += (_, _) => { _toasts.Children.Remove(b); a(); };
        _toasts.Children.Add(b);
        while (_toasts.Children.Count > 3) _toasts.Children.RemoveAt(0);
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(7);
        t.IsRepeating = false;
        t.Tick += (_, _) => _toasts.Children.Remove(b);
        t.Start();
    }

    public void ShowUpdate(string version)
    {
        _updateBar.Background = Theme.AccentGradient();
        _updateBar.Padding = U.Th(14, 6);
        _updateBar.Child = new Grid().Cols("*", "auto").Add(
            U.T($"Nowa wersja GibCorda ({version}) jest gotowa.", 13, U.Semi, Theme.OnAccent).At(0, 0),
            new Button
            {
                Content = U.T("Uruchom ponownie", 12.5, U.Bold, Theme.Fg), Background = U.B("#2a1630"), CornerRadius = new CornerRadius(8),
                Padding = U.Th(12, 4), BorderThickness = U.Th(0),
            }.With(b => b.Click += (_, _) => { if (Updater.Restart() is { } err) Info(err, true); }).At(0, 1));
        _updateBar.Visibility = Visibility.Visible;
    }

    public void ShowProfile(long uid, FrameworkElement anchor) => ProfileCard.Show(this, uid, anchor);

    /// <summary>Zrzut w CI: okienko emoji na środku.</summary>
    /// <summary>Tryb pokazowy: edytor obrazka na przykładowym „zrzucie ekranu” z czatu.</summary>
    public void DemoImageEditor()
    {
        using var bmp = new System.Drawing.Bitmap(1280, 720);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, 1280, 720), System.Drawing.Color.FromArgb(29, 107, 116), System.Drawing.Color.FromArgb(107, 45, 124), 35f);
            g.FillRectangle(bg, 0, 0, 1280, 720);
            using var panel = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(70, 0, 0, 0));
            g.FillRectangle(panel, 80, 80, 1120, 560);
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var f1 = new System.Drawing.Font("Segoe UI", 34, System.Drawing.FontStyle.Bold);
            using var f2 = new System.Drawing.Font("Segoe UI", 26);
            g.DrawString("Ustawienia konta", f1, white, 130, 120);
            g.DrawString("Nick: kubus_2009", f2, white, 130, 230);
            g.DrawString("E-mail: kuba.nowak@example.pl", f2, white, 130, 300);
            g.DrawString("Telefon: 600 123 456", f2, white, 130, 370);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        _ = ImageEditor.Open(this, ms.ToArray(), "zrzut-ekranu.png");
    }

    public void ShowPickerDemo()
    {
        if (_chat is null) return;
        Picker.Show(_chat, _chat, Picker.Tab.Emoji, _ => { }, _ => { }, _ => { }, demoHost: el => ShowOverlay(el));
    }

    /// <summary>Zrzut w CI: menu serwera (sprawdzenie wyglądu menu w barwach motywu).</summary>
    public void DemoMenu()
    {
        if (S.ServerById(ServerId) is { } srv) ServerMenu.Build(this, srv).ShowAt(_side, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = new Windows.Foundation.Point(20, 60) });
    }

    /// <summary>Zrzut w CI: własne okienko potwierdzenia (wariant A · Karta).</summary>
    public void DemoConfirm() => _ = M.Confirm(this, "Usunąć wiadomość?", "Tej operacji nie da się cofnąć. Wiadomość zniknie u wszystkich.", "Usuń");

    /// <summary>Zrzut w CI: karta profilu na środku (dymki nie trafiają na zrzut okna).</summary>
    public void ShowProfileDemo()
    {
        if (ProfileCard.Build(this, Demo.OlaId, CloseOverlay) is { } card)
            ShowOverlay(new Border { Child = card, Background = Theme.Surface, CornerRadius = new CornerRadius(14) });
    }

}
