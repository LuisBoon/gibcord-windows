using System.Collections.ObjectModel;
using GibCord.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace GibCord.App.Ui;

/// <summary>Wiersz listy: wiadomość + to, jak ją narysować (sklejona z poprzednią, separator dnia, „nowe”).</summary>
public sealed class Row(Message m)
{
    public Message M { get; } = m;
    public bool Grouped { get; set; }
    public bool DaySep { get; set; }
    public bool NewMarker { get; set; }
}

/// <summary>Tworzy i ponownie używa widoków wiadomości (lista rysuje tylko to, co widać).</summary>
sealed class MessageFactory(ChatView v) : IElementFactory
{
    readonly Stack<MessageView> _pool = new();
    public UIElement GetElement(ElementFactoryGetArgs args)
    {
        var mv = _pool.Count > 0 ? _pool.Pop() : new MessageView(v);
        mv.Bind((Row)args.Data);
        return mv;
    }
    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        if (args.Element is MessageView mv) { mv.Unbind(); _pool.Push(mv); }
    }
}

/// <summary>Kanał albo rozmowa: nagłówek, wiadomości, „pisze…”, pole wpisywania. Z <c>tid</c> — wątek w panelu z boku.</summary>
public sealed partial class ChatView : Grid, IDisposable
{
    public Shell Sh { get; }
    /// <summary>Kanał (w wątku: kanał-rodzic — od niego zależą uprawnienia).</summary>
    public long Cid { get; }
    /// <summary>Wątek (0 = zwykły kanał / rozmowa).</summary>
    public long Tid { get; }
    /// <summary>Klucz listy wiadomości w stanie: kanał albo −id wątku.</summary>
    public long Key => Tid != 0 ? -Tid : Cid;
    public bool IsThread => Tid != 0;
    public ThreadInfo? Thread => Tid != 0 ? S.ThreadById(Tid) : null;
    Message? _starter;
    /// <summary>Wiadomości zablokowanych osób odsłonięte przyciskiem „Pokaż”.</summary>
    public HashSet<long> Revealed { get; } = [];
    public Store S => Sh.S;
    public Server? Srv { get; }
    public Channel? Ch => S.ChannelById(Cid);
    public Dm? DmObj => S.DmById(Cid);

    readonly Border _header = new() { Height = 48, BorderBrush = Theme.Line, BorderThickness = U.Th(0, 0, 0, 1), Padding = U.Th(16, 0, 10, 0) };
    readonly ScrollViewer _sv;
    readonly ItemsRepeater _rep;
    readonly ObservableCollection<Row> _rows = [];
    readonly StackPanel _top = new() { Padding = U.Th(16, 24, 16, 8) };
    readonly TextBlock _typing;
    readonly Composer _composer;
    readonly Border _pill;
    readonly CallBar? _call;
    readonly Grid _listArea = new Grid().Rows("auto", "*");
    readonly Border _unreadBar = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top, Margin = U.Th(16, 8, 16, 0), CornerRadius = new CornerRadius(10), Background = U.B("#E6e8708f"), Padding = U.Th(14, 6) };
    VoiceView? _stage;
    readonly DispatcherQueueTimer _typingTimer;
    readonly long _lastReadAtOpen;
    bool _stick = true;
    bool _loadingOlder, _loadingNewer, _unreadShown;
    bool _disposed;
    int _bottomTries;
    double _lastOffset;
    long _progUntil;

    public ChatView(Shell sh, long cid, long tid = 0)
    {
        Sh = sh;
        Cid = cid;
        Tid = tid;
        Srv = S.ServerOfChannel(cid);
        _lastReadAtOpen = tid != 0 ? S.ThreadById(tid)?.LastRead ?? 0 : S.Reads.GetValueOrDefault(cid);
        this.Rows("48", "*", "20", "auto");

        _rep = new ItemsRepeater { ItemsSource = _rows, ItemTemplate = new MessageFactory(this), Layout = new StackLayout { Spacing = 0 } };
        var content = U.V(0, _top, _rep);
        content.Padding = U.Th(0, 0, 0, 12);
        _sv = U.Scroll(content);
        _sv.VerticalAnchorRatio = 1;
        _sv.ViewChanged += (_, _) => OnScrolled();
        content.SizeChanged += (_, _) => { if (_stick) { _bottomTries = 0; ScrollToBottom(); } };
        _sv.SizeChanged += (_, _) => { if (_stick) { _bottomTries = 0; ScrollToBottom(); } };

        _pill = new Border
        {
            Background = Theme.AccentGradient(), CornerRadius = new CornerRadius(14), Padding = U.Th(14, 6), Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = U.Th(0, 0, 0, 12),
        };
        _pill.Tapped += async (_, _) => { if (!S.Window(Key).AtTail) await Sh.C.JumpToPresent(Key); _stick = true; ScrollToBottom(); _pill.Visibility = Visibility.Collapsed; };

        _typing = U.T("", 11.5, null, Theme.Mu);
        _typing.Margin = U.Th(18, 0, 0, 0);
        _composer = new Composer(this);
        if (tid == 0 && S.DmById(cid) is not null) { _call = new CallBar(sh, cid); _listArea.Add(_call.At(0)); }
        _listArea.Add(_sv.At(1), _pill.At(1), _unreadBar.At(1));
        this.Add(_header.At(0), _listArea.At(1), _typing.At(2), _composer.At(3));

        AllowDrop = true;
        DragOver += (_, e) => { if (_composer.CanAttach && e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "Wyślij na czat"; } };
        Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            foreach (var it in await e.DataView.GetStorageItemsAsync()) if (it is StorageFile f) await _composer.AddFile(f);
        };

        _typingTimer = DispatcherQueue.CreateTimer();
        _typingTimer.Interval = TimeSpan.FromSeconds(2);
        _typingTimer.Tick += (_, _) => UpdateTyping();
        _typingTimer.Start();

        RefreshHeader();
        Sync();
        Loaded += (_, _) => FocusComposer();
    }

    public void Dispose()
    {
        _composer.SaveDraft();
        _stage?.Dispose();
        _disposed = true;
        _typingTimer.Stop();
        Sh.C.StoppedTyping(Key);
    }

    public void FocusComposer() => _composer.FocusBox();

    /// <summary>„N nowych wiadomości od HH:MM” u góry — skok do pierwszej nieprzeczytanej albo zamknięcie.</summary>
    void ShowUnreadBar()
    {
        if (_lastReadAtOpen <= 0) return;
        var unread = S.Window(Key).Items.Where(m => m.Id > _lastReadAtOpen && m.AuthorId != S.Me.Id && m.Send == SendState.Sent && !m.IsSystem).ToList();
        if (unread.Count < 3) return; // przy kilku nowych wystarczy znacznik NOWE
        var first = unread[0];
        var jump = new HyperlinkButton { Content = U.T("Skocz do pierwszej", 12.5, U.Bold, U.B("#ffffff")), Padding = U.Th(8, 0) };
        jump.Click += async (_, _) => { _unreadBar.Visibility = Visibility.Collapsed; await JumpTo(first.Id); };
        var close = U.IconButton(G.Close, "Zamknij", () => _unreadBar.Visibility = Visibility.Collapsed, 10, 24, U.B("#ffffff"));
        _unreadBar.Child = new Grid().Cols("*", "auto", "auto").Add(
            U.T($"{unread.Count}{(S.Window(Key).HasOlder && unread.Count >= 40 ? "+" : "")} {Format.Plural(unread.Count, "nowa wiadomość", "nowe wiadomości", "nowych wiadomości")} od {Format.Time(first.CreatedAt)}", 12.5, U.Bold, U.B("#ffffff")).At(0, 0),
            jump.At(0, 1), close.At(0, 2));
        _unreadBar.Visibility = Visibility.Visible;
    }

    /// <summary>Przerysowanie wszystkich wiadomości (np. po zablokowaniu / odblokowaniu osoby).</summary>
    public void Rerender() { if (_disposed) return; _rows.Clear(); Sync(); }

    /// <summary>Pasek rozmowy (DM): kto rozmawia, dołącz / rozłącz.</summary>
    public void RefreshCall() { if (_disposed) return; _call?.Refresh(); UpdateStage(); RefreshHeader(); }
    public void TickTalk() => _call?.Tick();

    /// <summary>Rozmowa prywatna z kamerą albo transmisją: kafelki nad czatem (jak na kanale głosowym, tylko mniejsze).</summary>
    void UpdateStage()
    {
        if (_call is null) return;
        var c = Sh.C;
        var members = S.Voice.GetValueOrDefault(Cid) ?? [];
        var want = c.VoiceChannelId == Cid && (members.Any(m => m.Video || m.Streaming) || c.Voice.Feeds.Any() || Sh.W.Cam?.Active == true);
        if (want && _stage is null)
        {
            _stage = new VoiceView(Sh, Cid, compact: true) { Height = Math.Clamp(ActualHeight * .5, 260, 520) };
            _listArea.Add(_stage.At(0));
        }
        else if (!want && _stage is not null)
        {
            _stage.Dispose();
            _listArea.Children.Remove(_stage);
            _stage = null;
        }
        else _stage?.Refresh();
        if (_stage is not null) _call.Visibility = Visibility.Collapsed;
    }

    // ───────────────────────── nagłówek ─────────────────────────

    public void RefreshHeader()
    {
        if (_disposed) return;
        if (IsThread) { ThreadHeader(); _composer.RefreshState(); return; }
        var g = new Grid().Cols("auto", "auto", "*", "auto");
        if (Ch is { } c)
        {
            g.Add(U.T("#", 20, U.Semi, Theme.Mu).With(t => t.Margin = U.Th(0, 0, 8, 2)).At(0, 0), U.T(c.Name, 15, U.Bold).At(0, 1));
            if (!string.IsNullOrWhiteSpace(c.Topic))
            {
                var topic = U.T(c.Topic!.Replace('\n', ' '), 12.5, null, Theme.Mu);
                topic.Tip(c.Topic!);
                g.Add(new Border { BorderBrush = U.W(.14), BorderThickness = U.Th(1, 0, 0, 0), Margin = U.Th(12, 0, 12, 0), Padding = U.Th(12, 0, 0, 0), Child = topic, Height = 20 }.At(0, 2));
            }
        }
        else if (DmObj is { } d)
        {
            if (d.IsGroup)
                g.Add(new Avatar(26).SetRaw(S.DmTitle(d), "#b69cff", Sh.C.Api.Abs(d.Icon), null).At(0, 0),
                    U.T(S.DmTitle(d), 15, U.Bold).With(t => t.Margin = U.Th(10, 0, 0, 0)).At(0, 1),
                    U.T($"{d.UserIds.Count} {Format.Plural(d.UserIds.Count, "osoba", "osoby", "osób")}", 12.5, null, Theme.Mu).With(t => t.Margin = U.Th(12, 0, 0, 0)).At(0, 2));
            else if (S.DmOther(d) is { } o)
            {
                var name = U.T(o.Name, 15, U.Bold).With(t => t.Margin = U.Th(10, 0, 0, 0));
                name.Tapped += (_, _) => Sh.ShowProfile(o.Id, name);
                g.Add(new Avatar(26, true).Set(S, Sh.C.Api, o.Id, null, true).At(0, 0), name.At(0, 1),
                    U.T(StatusLine(o), 12.5, null, Theme.Mu).With(t => t.Margin = U.Th(12, 0, 0, 0)).At(0, 2));
            }
        }
        var threads = Ch is { Type: "text" } tc && tc.Kind != "ideas" ? ThreadsButton() : null;
        var search = U.IconButton("\uE721", "Szukaj wiadomości (Ctrl+F)", () => Sh.OpenSearch(), 16, 32, Sh.SearchOpen ? Theme.Fg : null);
        var icons = U.H(2,
            DmObj is not null ? U.IconButton(G.Phone, Sh.C.VoiceChannelId == Cid ? "Jesteś w tej rozmowie" : "Zadzwoń", () => Sh.StartCall(Cid), 16, 32, Sh.C.VoiceChannelId == Cid ? Theme.Green : null) : null,
            DmObj is not null && !Sh.C.Offline && Sh.W.Cam is not null ? U.IconButton(G.Camera, "Rozmowa wideo", () => Sh.StartVideoCall(Cid), 16, 32) : null,
            DmObj is { IsGroup: true } ? U.IconButton("\uE716", "Ustawienia grupy (osoby, nazwa, obrazek)", () => People.GroupSettings(Sh, Cid), 16, 32) : null,
            DmObj is { IsGroup: false } ? U.IconButton("\uE8FA", "Utwórz grupę z tą osobą", async () =>
            {
                var other = S.DmOther(DmObj!)?.Id ?? 0;
                var ids = await People.Pick(Sh, "Nowa grupa", 8, n => n == 0 ? "Wybierz osoby" : $"Utwórz grupę ({n + 2} osób)", [other]);
                if (ids is null) return;
                try { var dm = await Sh.C.Api.CreateGroup([other, .. ids]); S.Apply(new GatewayEvent("dm_create", System.Text.Json.JsonSerializer.SerializeToElement(new { dm }, Json.Options))); Sh.Go(0, dm.Id); }
                catch (Exception ex) { Sh.Info(GibCordClient.Friendly(ex), true); }
            }, 16, 32) : null,
            threads,
            U.IconButton("\uE718", "Przypięte wiadomości", ShowPins, 16, 32),
            search,
            U.IconButton("", Sh.MembersVisible ? "Schowaj listę osób" : "Pokaż listę osób", Sh.ToggleMembers, 16, 32, Sh.MembersVisible ? Theme.Fg : Theme.Mu));
        g.Add(icons.At(0, 3));
        _header.Child = g;
        _composer.RefreshState();
    }

    string StatusLine(User u)
    {
        var st = S.StatusOf(u.Id);
        if (u.GameName is { } game) return "Gra w " + game;
        if (u.Activity is { Type: "spotify" } a) return $"Słucha: {a.Title} – {a.ArtistText}";
        if (!string.IsNullOrEmpty(u.CustomStatus)) return u.CustomStatus!;
        return Format.StatusPl.GetValueOrDefault(st, "");
    }

    async void ShowPins()
    {
        var list = new StackPanel { Spacing = 8, Width = 420 };
        list.Add(U.T("Przypięte wiadomości", 15, U.Bold));
        var fly = new Flyout { Content = U.Scroll(list).With(s => s.MaxHeight = 520), Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        fly.ShowAt(_header);
        try
        {
            var pins = await Sh.C.Api.GetPins(Cid);
            if (pins.Count == 0) list.Add(U.T("Na tym kanale nic nie jest przypięte.", 13, null, Theme.Mu, wrap: true));
            foreach (var m in pins)
            {
                var card = U.V(4,
                    U.H(8, new Avatar(22).Set(S, Sh.C.Api, m.AuthorId, Srv), U.T(S.NameOf(m.AuthorId, Srv), 13, U.Bold, U.B(S.NameColor(m.AuthorId, Srv))), U.T(Format.When(m.CreatedAt), 11, null, Theme.Mu2)),
                    U.T(S.PlainText(m, 300), 13, null, Theme.Fg, wrap: true));
                var b = U.Box(card, Theme.Card, 10, U.Th(12, 10));
                b.Tapped += async (_, _) => { fly.Hide(); await JumpTo(m.Id); };
                list.Add(b);
            }
        }
        catch (Exception ex) { list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    }

    // ───────────────────────── lista wiadomości ─────────────────────────

    List<Row> BuildRows(IReadOnlyList<Message> items)
    {
        var rows = new List<Row>(items.Count);
        Message? prev = null;
        var newPlaced = false;
        foreach (var m in items)
        {
            var r = new Row(m) { DaySep = prev is null || !Format.SameDay(prev.CreatedAt, m.CreatedAt) };
            r.Grouped = Groups(prev, m, r.DaySep);
            if (!newPlaced && _lastReadAtOpen > 0 && m.Id > _lastReadAtOpen && m.AuthorId != S.Me.Id && m.Send == SendState.Sent && prev is not null && prev.Id <= _lastReadAtOpen)
            {
                r.NewMarker = true; r.Grouped = false; newPlaced = true;
            }
            rows.Add(r);
            prev = m;
        }
        return rows;
    }

    static bool Groups(Message? prev, Message m, bool daySep) =>
        prev is not null && !daySep && !prev.IsSystem && !m.IsSystem && prev.AuthorId == m.AuthorId && m.ReplyTo is null
        && m.CreatedAt - prev.CreatedAt < 7 * 60_000 && !m.HasCard && !prev.HasCard;

    /// <summary>Pełne odświeżenie (albo dopisanie starszych na górze bez skakania).</summary>
    void Sync()
    {
        var w = S.Window(Key);
        var items = w.Items;
        var fresh = BuildRows(items);
        if (_rows.Count > 0 && items.Count > _rows.Count)
        {
            var k = items.Count - _rows.Count;
            var sameTail = true;
            for (var i = 0; i < _rows.Count && sameTail; i++) if (items[k + i].Id != _rows[i].M.Id) sameTail = false;
            if (sameTail)
            {
                for (var i = k - 1; i >= 0; i--) _rows.Insert(0, fresh[i]);
                _rows[k] = fresh[k];
                UpdateTop(w);
                return;
            }
        }
        _rows.Clear();
        foreach (var r in fresh) _rows.Add(r);
        UpdateTop(w);
        if (!_unreadShown && !w.FromCache && w.Items.Count > 0) { _unreadShown = true; ShowUnreadBar(); }
        if (w.AtTail) { _stick = true; _bottomTries = 0; ScrollToBottom(); }
        UpdatePill();
    }

    void UpdateTop(MessageWindow w)
    {
        _top.Children.Clear();
        if (w.HasOlder && w.Items.Count > 0)
        {
            _top.Children.Add(new ProgressRing { IsActive = _loadingOlder, Width = 22, Height = 22, Foreground = Theme.Mu, HorizontalAlignment = HorizontalAlignment.Center });
            return;
        }
        if (w.Items.Count == 0 && w.HasOlder) return; // jeszcze się wczytuje
        // początek rozmowy
        if (IsThread)
        {
            if (_starter is { } st)
            {
                var mv = new MessageView(this) { IsStarter = true };
                mv.Bind(new Row(st));
                _top.Add(mv.With(x => x.Margin = U.Th(-16, 0, -16, 0)));
            }
            else _top.Add(U.T("Pierwsza wiadomość wątku została usunięta.", 13, null, Theme.Mu, wrap: true));
            var n = Thread?.Count ?? w.Items.Count;
            _top.Add(new Grid { Margin = U.Th(0, 12, 0, 4) }.Cols("auto", "*").Add(
                U.T(n > 0 ? $"{n} {Format.Plural(n, "odpowiedź", "odpowiedzi", "odpowiedzi")}" : "Napisz pierwszą odpowiedź", 12, U.Bold, Theme.Mu).At(0, 0),
                U.HLine(.1).With(l => l.Margin = U.Th(10, 0, 0, 0)).At(0, 1)));
            return;
        }
        if (Ch is { } c)
        {
            _top.Add(new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = U.W(.1), HorizontalAlignment = HorizontalAlignment.Left, Child = U.T("#", 34, U.Semi).With(t => t.HorizontalAlignment = HorizontalAlignment.Center) },
                U.T($"Witaj na #{c.Name}!", 26, U.Bold).With(t => t.Margin = U.Th(0, 12, 0, 4)),
                U.T($"To początek kanału #{c.Name}.", 14, null, Theme.Mu));
        }
        else if (DmObj is { } d)
        {
            var other = d.IsGroup ? null : S.DmOther(d);
            _top.Add(other is null ? new Avatar(72).SetRaw(S.DmTitle(d), "#b69cff", Sh.C.Api.Abs(d.Icon), null) : new Avatar(72).Set(S, Sh.C.Api, other.Id),
                U.T(S.DmTitle(d), 26, U.Bold).With(t => t.Margin = U.Th(0, 12, 0, 4)),
                U.T(other is null ? "To początek tej grupy." : $"To początek Twojej rozmowy z {other.Name}.", 14, null, Theme.Mu));
        }
    }

    public void OnChange(StoreChange c)
    {
        if (_disposed) return;
        switch (c.Kind)
        {
            case ChangeKind.MessagesReset:
                Sync();
                break;
            case ChangeKind.MessageAdded when c.Message is { } m:
            {
                var items = S.Window(Key).Items;
                var idx = items.FindIndex(x => ReferenceEquals(x, m) || x.Id == m.Id);
                if (idx < 0) break;
                if (idx != _rows.Count) { Sync(); break; }
                var prev = idx > 0 ? items[idx - 1] : null;
                var r = new Row(m) { DaySep = prev is null || !Format.SameDay(prev.CreatedAt, m.CreatedAt) };
                r.Grouped = Groups(prev, m, r.DaySep);
                _rows.Add(r);
                if (m.AuthorId == S.Me.Id && !_loadingNewer) { _stick = true; ScrollToBottom(); }
                else if (!_stick && !_loadingNewer) ShowPill("Nowe wiadomości  ↓");
                break;
            }
            case ChangeKind.MessageUpdated when c.Message is { } m:
            {
                var i = IndexOf(m.Id);
                if (i >= 0) _rows[i] = new Row(m) { Grouped = _rows[i].Grouped, DaySep = _rows[i].DaySep, NewMarker = _rows[i].NewMarker };
                break;
            }
            case ChangeKind.MessageRemoved:
            {
                var i = IndexOf(c.Id);
                if (i < 0) break;
                _rows.RemoveAt(i);
                if (i < _rows.Count)
                {
                    var prev = i > 0 ? _rows[i - 1].M : null;
                    var nx = _rows[i];
                    var r = new Row(nx.M) { DaySep = prev is null || !Format.SameDay(prev.CreatedAt, nx.M.CreatedAt), NewMarker = nx.NewMarker };
                    r.Grouped = !r.NewMarker && Groups(prev, nx.M, r.DaySep);
                    if (r.Grouped != nx.Grouped || r.DaySep != nx.DaySep) _rows[i] = r;
                }
                break;
            }
            case ChangeKind.Typing:
                UpdateTyping();
                break;
        }
    }

    int IndexOf(long id)
    {
        for (var i = _rows.Count - 1; i >= 0; i--) if (_rows[i].M.Id == id) return i;
        return -1;
    }

    void UpdateTyping()
    {
        if (_disposed) return;
        var ids = S.TypingIn(Key).Where(id => !S.Blocked.Contains(id)).ToList();
        _typing.Text = ids.Count switch
        {
            0 => "",
            1 => $"{S.NameOf(ids[0], Srv)} pisze…",
            2 => $"{S.NameOf(ids[0], Srv)} i {S.NameOf(ids[1], Srv)} piszą…",
            _ => "Kilka osób pisze…",
        };
    }

    void OnScrolled()
    {
        var off = _sv.VerticalOffset;
        var atBottom = _sv.ScrollableHeight - off < 80;
        // „przyklejenie” do dołu puszcza tylko przewinięcie w górę przez człowieka (lista rośnie przy wczytywaniu)
        if (atBottom) _stick = S.Window(Key).AtTail;
        if (_unreadBar.Visibility == Visibility.Visible && off < 200 && _rows.Count > 0 && !S.Window(Key).HasOlder) _unreadBar.Visibility = Visibility.Collapsed;
        else if (off < _lastOffset - 1 && Environment.TickCount64 > _progUntil) _stick = false;
        _lastOffset = off;
        if (_stick && _pill.Visibility == Visibility.Visible && S.Window(Key).AtTail) _pill.Visibility = Visibility.Collapsed;
        if (_sv.VerticalOffset < 600 && !_loadingOlder && S.Window(Key).HasOlder && _rows.Count > 0) _ = LoadOlder();
        if (_sv.ScrollableHeight - off < 700 && !_loadingNewer && !S.Window(Key).AtTail && _rows.Count > 0) _ = LoadNewer();
    }

    async Task LoadOlder()
    {
        _loadingOlder = true;
        UpdateTop(S.Window(Key));
        try { await Sh.C.LoadOlder(Key); }
        finally { _loadingOlder = false; if (!_disposed) UpdateTop(S.Window(Key)); }
    }

    async Task LoadNewer()
    {
        _loadingNewer = true;
        try { await Sh.C.LoadNewer(Key); }
        finally { _loadingNewer = false; if (!_disposed) UpdatePill(); }
    }

    void ScrollToBottom()
    {
        if (_disposed) return;
        _progUntil = Environment.TickCount64 + 400;
        _sv.ChangeView(null, _sv.ScrollableHeight, null, true);
        // wysokość listy szacowana przy wirtualizacji — dociągnij jeszcze kilka razy
        if (_bottomTries++ < 8)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_stick || _disposed) return;
                _progUntil = Environment.TickCount64 + 400;
                _sv.UpdateLayout();
                _sv.ChangeView(null, _sv.ScrollableHeight, null, true);
                if (_sv.ScrollableHeight - _sv.VerticalOffset > 2) ScrollToBottom();
            });
    }

    void UpdatePill()
    {
        if (!S.Window(Key).AtTail) ShowPill("Oglądasz starsze wiadomości · Wróć na dół  ↓");
        else _pill.Visibility = Visibility.Collapsed;
    }

    void ShowPill(string text)
    {
        _pill.Child = U.T(text, 12.5, U.Bold, Theme.OnAccent);
        _pill.Visibility = Visibility.Visible;
    }

    /// <summary>Przewija do wiadomości (wczytuje jej okolicę, jeśli trzeba) i ją podświetla.</summary>
    public async Task JumpTo(long messageId)
    {
        if (!await Sh.C.JumpTo(Key, messageId)) { Sh.Info("Tej wiadomości już nie ma.", true); return; }
        var i = IndexOf(messageId);
        if (i < 0) return;
        _stick = false;
        var el = _rep.GetOrCreateElement(i);
        el.UpdateLayout();
        el.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = .4, AnimationDesired = false });
        if (el is MessageView mv) mv.Flash();
        UpdatePill();
    }

    // ───────────────────────── akcje z wiadomości ─────────────────────────

    public void Reply(Message m) => _composer.StartReply(m);
    public void Edit(Message m) => _composer.StartEdit(m);
    public void InsertMention(long uid) => _composer.InsertMention(uid);

    public async void Delete(Message m, bool ask = true)
    {
        if (ask && m.Send == SendState.Sent && !await M.Confirm(Sh, "Usunąć wiadomość?", "Tej operacji nie da się cofnąć.", "Usuń")) return;
        var err = await Sh.C.Delete(m);
        if (err is not null) Sh.Info(err, true);
    }

    public async void React(Message m, string emoji)
    {
        var err = await Sh.C.ToggleReaction(m, emoji);
        if (err is not null) Sh.Info(err, true);
    }

    public async void Retry(Message m)
    {
        var err = await Sh.C.Retry(m);
        if (err is not null) Sh.Info(err, true);
    }

    public async void TogglePin(Message m)
    {
        try
        {
            if (m.PinnedAt is null) await Sh.C.Api.PinMessage(m.Id); else await Sh.C.Api.UnpinMessage(m.Id);
        }
        catch (Exception ex) { Sh.Info(GibCordClient.Friendly(ex), true); }
    }

    public bool CanPin => !IsThread && (DmObj is not null || S.Can(Cid, Perm.ManageMessages));
    /// <summary>Wątek z wiadomości: kanał tekstowy serwera (nie tablica pomysłów), zwykła wiadomość bez wątku.</summary>
    public bool CanStartThread(Message m) => !IsThread && Ch is { Type: "text" } c && c.Kind != "ideas" && m.Type == "default" && m.ThreadId is null && m.Send == SendState.Sent
        && S.Can(Cid, Perm.SendMessages) && S.TimedOutUntil(Srv) is null;
    public bool CanManage => DmObj is null && S.Can(Cid, Perm.ManageMessages);
    public bool CanReact => S.Can(Cid, Perm.AddReactions) && S.TimedOutUntil(Srv) is null;

    /// <summary>Ostatnia własna wiadomość (strzałka w górę w pustym polu = edycja).</summary>
    public Message? LastOwn() => S.Window(Key).Items.LastOrDefault(m => m.AuthorId == S.Me.Id && m.Send == SendState.Sent && !m.IsSystem);
}
