using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace GibCord.App.Ui;

/// <summary>Pasek serwerów po lewej: Gibek (rozmowy prywatne), serwery, „+”.</summary>
public sealed class ServerRail : Grid
{
    readonly Shell _sh;
    readonly StackPanel _list = new() { Spacing = 9, Padding = U.Th(0, 12, 0, 12) };

    public ServerRail(Shell sh)
    {
        _sh = sh;
        Background = Theme.Rail;
        Children.Add(U.Scroll(_list).With(s => s.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden));
    }

    public void Refresh()
    {
        var s = _sh.S;
        _list.Children.Clear();
        var homeLogo = new Image { Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = 44, Height = 44 };
        var pending = s.Friends.Values.Count(f => f.Status == "incoming");
        _list.Add(Item(homeLogo, "Rozmowy prywatne i znajomi", _sh.ServerId == 0, unread: false, mentions: s.DmUnreadTotal() + pending, () => _sh.Go(0, _sh.ServerId == 0 ? _sh.ChannelId : App.Prefs.LastDm), home: true));
        _list.Add(new Rectangle { Width = 28, Height = 1, Fill = U.W(.16), HorizontalAlignment = HorizontalAlignment.Center });
        foreach (var srv in s.Servers)
        {
            FrameworkElement icon;
            if (_sh.C.Api.Abs(srv.Icon) is { } uri) icon = new Image { Source = Images.Get(uri, 96), Stretch = Stretch.UniformToFill };
            else if (srv.Official) icon = U.I("", 20);
            else icon = U.T(Initials(srv.Name), 13, U.Bold).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextTrimming = TextTrimming.None; });
            var sel = _sh.ServerId == srv.Id;
            var muted = s.ServerMuted(srv.Id);
            var item = Item(icon, srv.Name, sel, unread: s.ServerUnread(srv), mentions: muted ? 0 : s.ServerMentions(srv), () => _sh.Go(srv.Id, 0), bg: srv.Official ? U.B("#3b2a5c") : null);
            item.ContextFlyout = ServerMenu.Build(_sh, srv);
            _list.Add(item);
        }
        var add = Item(U.I("", 18, Theme.Green), "Dodaj serwer", false, false, 0, () => AddServerDialog.Show(_sh), bg: U.W(.07));
        _list.Add(add);
    }

    static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => w.EnumerateRunes().First().ToString());
        var s = string.Concat(words).ToUpperInvariant();
        return s.Length == 0 ? "?" : s;
    }

    static Grid Item(FrameworkElement content, string tip, bool selected, bool unread, int mentions, Action click, Brush? bg = null, bool home = false)
    {
        var box = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(home ? 0 : 15), Child = content,
            Background = home ? null : selected ? Theme.AccentGradient() : bg ?? U.W(.12),
        };
        if (selected && content is TextBlock tb) tb.Foreground = Theme.OnAccent;
        if (selected && content is FontIcon fi) fi.Foreground = Theme.OnAccent;
        var pill = new Border
        {
            Width = 5, CornerRadius = new CornerRadius(0, 4, 4, 0), Background = Theme.Fg, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Height = selected ? 30 : unread ? 8 : 0, Margin = U.Th(-12, 0, 0, 0),
        };
        var g = new Grid { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center };
        g.Add(box, pill);
        if (mentions > 0)
            g.Add(U.Badge(mentions).With(b => { b.HorizontalAlignment = HorizontalAlignment.Right; b.VerticalAlignment = VerticalAlignment.Bottom; b.Margin = U.Th(0, 0, -4, -4); b.BorderBrush = U.B("#1d1630"); b.BorderThickness = U.Th(2); b.Height = 22; b.CornerRadius = new CornerRadius(11); }));
        ToolTipService.SetToolTip(g, tip);
        ToolTipService.SetPlacement(g, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Right);
        g.PointerEntered += (_, _) => { if (!selected) { box.CornerRadius = new CornerRadius(home ? 0 : 12); if (!home) box.Background = U.W(.2); if (pill.Height == 0) pill.Height = 18; } };
        g.PointerExited += (_, _) => { if (!selected) { box.CornerRadius = new CornerRadius(home ? 0 : 15); if (!home) box.Background = bg ?? U.W(.12); pill.Height = unread ? 8 : 0; } };
        g.Tapped += (_, e) => { e.Handled = true; click(); };
        return g;
    }
}

/// <summary>Kolumna kanałów (serwer) albo rozmów prywatnych, a na dole panel „ja”.</summary>
public sealed class Sidebar : Grid
{
    readonly Shell _sh;
    readonly Border _header = new() { Height = 48, BorderBrush = Theme.Line, BorderThickness = U.Th(0, 0, 0, 1), Padding = U.Th(16, 0) };
    readonly StackPanel _list = new() { Padding = U.Th(0, 6, 0, 10) };
    readonly ScrollViewer _scroll;
    readonly Grid _me = new() { Height = 56, Padding = U.Th(10, 0, 6, 0), Background = Theme.Rail };
    readonly Border _voice = new();
    readonly HashSet<long> _collapsed = [];
    readonly List<(long Uid, Avatar Av)> _talk = [];

    public Sidebar(Shell sh)
    {
        _sh = sh;
        Background = Theme.Side;
        BorderBrush = Theme.Line;
        BorderThickness = U.Th(0, 0, 1, 0);
        this.Rows("48", "*", "auto", "auto");
        _scroll = U.Scroll(_list);
        this.Add(_header.At(0), _scroll.At(1), _voice.At(2), _me.At(3));
    }

    Store S => _sh.S;

    public void Refresh()
    {
        if (!S.Loaded) return;
        var offset = _scroll.VerticalOffset;
        _list.Children.Clear();
        _talk.Clear();
        _voice.Child = VoiceUi.Panel(_sh);
        if (_sh.ServerId > 0 && S.ServerById(_sh.ServerId) is { } srv) BuildServer(srv);
        else BuildHome();
        BuildMe();
        _scroll.UpdateLayout();
        _scroll.ChangeView(null, offset, null, true);
    }

    // ── serwer ──

    void BuildServer(Server srv)
    {
        var title = U.T(srv.Name, 14.5, U.Bold);
        var chev = U.I("", 12, Theme.Mu);
        var head = new Grid().Cols("*", "auto").Add(title.At(0, 0), chev.At(0, 1));
        _header.Child = head;
        _header.Background = null;
        _header.Tapped -= HeaderTapped;
        _header.Tapped += HeaderTapped;
        FlyoutBase.SetAttachedFlyout(_header, ServerMenu.Build(_sh, srv));

        foreach (var g in S.ChannelTree(srv))
        {
            if (g.Category is { } cat)
            {
                var collapsed = _collapsed.Contains(cat.Id);
                var row = U.H(4, U.I(collapsed ? "" : "", 9, Theme.Mu2), U.T(cat.Name.ToUpperInvariant(), 11, U.Bold, Theme.Mu2));
                var b = new Border { Child = row, Padding = U.Th(12, 14, 12, 4), Background = Theme.Transparent };
                b.Tapped += (_, _) => { if (!_collapsed.Remove(cat.Id)) _collapsed.Add(cat.Id); Refresh(); };
                _list.Children.Add(b);
                if (collapsed)
                {
                    // zwinięta kategoria pokazuje tylko otwarty i nieprzeczytane kanały
                    foreach (var c in g.Channels.Where(c => c.Id == _sh.ChannelId || (!c.IsVoice && S.IsUnread(c.Id) && !S.ChannelMuted(c.Id))))
                        _list.Children.Add(ChannelRow(c, srv));
                    continue;
                }
            }
            foreach (var c in g.Channels)
            {
                _list.Children.Add(ChannelRow(c, srv));
                if (c.IsVoice) foreach (var vm in S.Voice.GetValueOrDefault(c.Id) ?? []) _list.Children.Add(VoiceMemberRow(vm, srv));
            }
        }
    }

    void HeaderTapped(object s, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => FlyoutBase.ShowAttachedFlyout(_header);

    FrameworkElement ChannelRow(Channel c, Server srv)
    {
        var sel = c.Id == _sh.ChannelId;
        var unread = !c.IsVoice && S.IsUnread(c.Id);
        var muted = S.ChannelMuted(c.Id);
        var mentions = S.MentionsIn(c.Id);
        var fg = sel || (unread && !muted) ? Theme.Fg : muted ? Theme.Mu2 : Theme.Mu;
        FrameworkElement glyph = c.IsVoice ? U.I("", 15, fg) : U.T("#", 17, U.Semi, fg).With(t => t.Width = 16);
        var name = U.T(c.Name, 14, unread && !muted ? U.Bold : U.Semi, fg);
        var g = new Grid { Height = 32 }.Cols("22", "*", "auto").Add(glyph.At(0, 0), name.At(0, 1));
        if (mentions > 0) g.Add(U.Badge(mentions).At(0, 2));
        else if (c.IsVoice && (S.Voice.GetValueOrDefault(c.Id) ?? []).Any(v => v.Streaming))
            g.Add(U.Box(U.T("NA ŻYWO", 9.5, U.Bold, U.B("#ffffff")), Theme.Red2, 5, U.Th(5, 1)).At(0, 2));
        else if (c.IsVoice && (S.Voice.GetValueOrDefault(c.Id)?.Count ?? 0) > 0 && c.UserLimit > 0)
            g.Add(U.T($"{S.Voice[c.Id].Count}/{c.UserLimit}", 11, null, Theme.Mu2).At(0, 2));
        if (unread && !sel && !muted)
            g.Add(new Ellipse { Width = 6, Height = 6, Fill = Theme.Fg, HorizontalAlignment = HorizontalAlignment.Left, Margin = U.Th(-14, 0, 0, 0) }.At(0, 0));
        var row = U.Row(g, () => { if (c.IsVoice) _sh.JoinVoiceChannel(srv.Id, c.Id); else _sh.Go(srv.Id, c.Id); }, sel);
        if (!string.IsNullOrEmpty(c.Topic)) row.Tip(c.Topic!);
        var menu = new MenuFlyout();
        if (!c.IsVoice)
        {
            menu.Items.Add(Item("Oznacz jako przeczytane", "", () => _sh.C.MarkRead(c.Id)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        if (!c.IsVoice) menu.Items.Add(NotifyMenu.Sub(_sh, "channel", c.Id, S.Notify.Channels.GetValueOrDefault(c.Id)));
        else menu.Items.Add(Item(muted ? "Wyłącz wyciszenie" : "Wycisz kanał", muted ? "\uE767" : "\uE74F", () => NotifyMenu.SetMute(_sh, "channel", c.Id, !muted)));
        menu.Items.Add(Item("Kopiuj nazwę kanału", "", () => Copy("#" + c.Name)));
        row.ContextFlyout = menu;
        return row;
    }

    FrameworkElement VoiceMemberRow(VoiceMember vm, Server srv)
    {
        var av = new Avatar(22).Set(S, _sh.C.Api, vm.UserId, srv);
        _talk.Add((vm.UserId, av));
        var icons = U.H(4);
        if (_sh.C.SoundLabels.TryGetValue(vm.UserId, out var sl) && sl.Until > Environment.TickCount64) icons.Add(U.Box(U.T(sl.Label, 10, U.Bold, Theme.OnAccent), Theme.Accent, 4, U.Th(4, 0)));
        if (vm.Streaming) icons.Add(U.Box(U.T("NA ŻYWO", 9, U.Bold, U.B("#ffffff")), Theme.Red2, 4, U.Th(4, 0)));
        if (vm.Video) icons.Add(U.I("", 12, Theme.Mu));
        if (vm.Muted || vm.ServerMuted || vm.Suppressed) icons.Add(U.I("", 12, Theme.Red));
        if (vm.Deafened || vm.ServerDeafened) icons.Add(U.I("", 12, Theme.Red));
        var g = new Grid { Height = 28, Margin = U.Th(28, 0, 0, 0) }.Cols("auto", "*", "auto").Add(
            av.At(0, 0), U.T(S.NameOf(vm.UserId, srv), 13, null, U.W(.78)).With(t => t.Margin = U.Th(8, 0, 4, 0)).At(0, 1), icons.At(0, 2));
        var row = U.Row(g, () => _sh.ShowProfile(vm.UserId, g));
        if (vm.UserId != S.Me.Id) row.ContextFlyout = VoiceUi.VolumeFlyout(_sh, vm.UserId, srv);
        return row;
    }

    /// <summary>Zielone obwódki osób, które właśnie mówią (co ~0,15 s, bez przebudowy listy).</summary>
    public void TickTalk() { foreach (var (uid, av) in _talk) av.Talking = _sh.C.IsSpeaking(uid); }

    // ── rozmowy prywatne ──

    void BuildHome()
    {
        _header.Child = null;
        var search = new Border
        {
            Background = U.B("#4D0A0618"), CornerRadius = new CornerRadius(8), Height = 30, Padding = U.Th(10, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = U.T("Znajdź albo zacznij rozmowę", 12.5, null, Theme.Mu2),
        };
        search.Tapped += (_, _) => _sh.OpenSwitcher();
        _header.Child = search;

        var pending = S.Friends.Values.Count(f => f.Status == "incoming");
        var fr = new Grid { Height = 40 }.Cols("30", "*", "auto").Add(U.I("", 18).At(0, 0), U.T("Znajomi", 14.5, U.Semi).At(0, 1));
        if (pending > 0) fr.Add(U.Badge(pending).At(0, 2));
        _list.Children.Add(U.Row(fr, () => _sh.Go(0, 0), _sh.ServerId == 0 && _sh.ChannelId == 0));

        var head = new Grid().Cols("*", "auto").Add(U.T("ROZMOWY PRYWATNE", 11, U.Bold, Theme.Mu2).At(0, 0),
            U.IconButton("\uE710", "Nowa rozmowa albo grupa", () => People.NewConversation(_sh), 12, 22).At(0, 1));
        _list.Children.Add(new Border { Child = head, Padding = U.Th(16, 16, 10, 4) });

        var dms = S.DmsSorted();
        if (dms.Count == 0)
            _list.Children.Add(U.T("Tu pojawią się Twoje rozmowy. Napisz do kogoś ze znajomych albo z serwera.", 12.5, null, Theme.Mu2, wrap: true).With(t => t.Margin = U.Th(16, 6)));
        foreach (var d in dms) _list.Children.Add(DmRow(d));
    }

    FrameworkElement DmRow(Dm d)
    {
        var sel = d.Id == _sh.ChannelId;
        var unread = S.IsUnread(d.Id) && !S.ChannelMuted(d.Id);
        Avatar av;
        string sub;
        if (d.IsGroup)
        {
            av = new Avatar(32).SetRaw(S.DmTitle(d), "#b69cff", _sh.C.Api.Abs(d.Icon), null);
            sub = $"{d.UserIds.Count} {Format.Plural(d.UserIds.Count, "osoba", "osoby", "osób")}";
        }
        else
        {
            var other = S.DmOther(d);
            av = new Avatar(32, withStatus: true).Set(S, _sh.C.Api, other?.Id ?? 0, null, status: true);
            sub = other?.GameName is { } game ? "Gra w " + game : other?.Activity is { Type: "spotify" } ? "Słucha Spotify" : other?.CustomStatus ?? "";
        }
        var name = U.T(S.DmTitle(d), 14, unread ? U.Bold : U.Semi, sel || unread ? Theme.Fg : Theme.Mu);
        var text = string.IsNullOrEmpty(sub) ? (UIElement)name : U.V(0, name, U.T(sub, 11.5, null, Theme.Mu2));
        async void CloseDm()
        {
            d.HiddenAt = d.LastMessageId ?? 0;
            Refresh();
            if (_sh.ChannelId == d.Id) _sh.Go(0, 0);
            try { await _sh.C.Api.HideDm(d.Id); } catch { return; }
            _sh.InfoUndo("Rozmowa zamknięta", "Cofnij", async () =>
            {
                try { await _sh.C.Api.UnhideDm(d.Id); d.HiddenAt = null; Refresh(); } catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
            });
        }
        var close = U.IconButton("\uE711", "Zamknij rozmowę (wróci, gdy ktoś napisze)", CloseDm, 10, 22);
        close.Visibility = Visibility.Collapsed;
        var g = new Grid { Height = 44 }.Cols("auto", "*", "auto").Add(av.At(0, 0), ((FrameworkElement)text).With(t => t.Margin = U.Th(10, 0, 4, 0)).At(0, 1));
        var mentions = S.MentionsIn(d.Id);
        if (unread && !sel) g.Add(U.Badge(Math.Max(1, mentions)).At(0, 2));
        else g.Add(close.At(0, 2));
        var row = U.Row(g, () => _sh.Go(0, d.Id), sel, pad: U.Th(8, 0));
        var dmMenu = new MenuFlyout();
        if (S.IsUnread(d.Id)) dmMenu.Items.Add(Item("Oznacz jako przeczytane", "\uE8FB", () => _sh.C.MarkRead(d.Id)));
        dmMenu.Items.Add(NotifyMenu.Sub(_sh, "dm", d.Id, S.Notify.Dms.GetValueOrDefault(d.Id)));
        if (!d.IsGroup && S.DmOther(d) is { } du)
        {
            dmMenu.Items.Add(new MenuFlyoutSeparator());
            var um = UserMenu.Build(_sh, du.Id, null);
            var items = um.Items.ToList();
            um.Items.Clear();
            foreach (var it in items) dmMenu.Items.Add(it);
        }
        dmMenu.Items.Add(new MenuFlyoutSeparator());
        dmMenu.Items.Add(Item("Zamknij rozmowę", "\uE711", CloseDm));
        row.ContextFlyout = dmMenu;
        row.PointerEntered += (_, _) => close.Visibility = Visibility.Visible;
        row.PointerExited += (_, _) => close.Visibility = Visibility.Collapsed;
        return row;
    }

    // ── panel „ja” ──

    void BuildMe()
    {
        _me.Children.Clear();
        _me.ColumnDefinitions.Clear();
        _me.Cols("auto", "*", "auto");
        var me = S.Me;
        var av = new Avatar(34, withStatus: true, ring: "#2a2140").Set(S, _sh.C.Api, me.Id, null, status: true);
        var status = S.StatusOf(me.Id);
        var sub = !string.IsNullOrEmpty(me.CustomStatus) ? me.CustomStatus! : Format.StatusPl.GetValueOrDefault(me.Status == "invisible" ? "invisible" : status, "Dostępny");
        var who = U.V(0, U.T(me.Name, 12.5, U.Bold), U.T(sub, 11, null, Theme.Mu));
        var left = new Grid().Cols("auto", "*").Add(av.At(0, 0), who.With(w => w.Margin = U.Th(9, 0, 0, 0)).At(0, 1));
        var leftBtn = new Border { Child = left, CornerRadius = new CornerRadius(8), Padding = U.Th(2, 4), Background = Theme.Transparent };
        leftBtn.PointerEntered += (_, _) => leftBtn.Background = Theme.Hover;
        leftBtn.PointerExited += (_, _) => leftBtn.Background = Theme.Transparent;
        leftBtn.Tip("Zmień status");
        leftBtn.ContextFlyout = StatusMenu.Build(_sh);
        leftBtn.Tapped += (_, _) => leftBtn.ContextFlyout.ShowAt(leftBtn);
        var (mic, hp) = VoiceUi.MuteButtons(_sh);
        var cog = U.IconButton("", "Ustawienia", () => { }, 15, 30);
        cog.Flyout = SettingsMenu.Build(_sh);
        _me.Add(leftBtn.At(0, 0, colSpan: 2), U.H(0, mic, hp, cog).At(0, 2));
    }

    static MenuFlyoutItem Item(string text, string glyph, Action a)
    {
        var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        i.Click += (_, _) => a();
        return i;
    }

    public static void Copy(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }
}
