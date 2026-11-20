using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>
/// Wyszukiwarka wiadomości w panelu z prawej (Ctrl+F): tekst, filtry „od:@ktoś” i „ma:zdjęcie|plik|link|przypięte”,
/// zakres (ten kanał, ten serwer, rozmowy prywatne, wszędzie), wyniki ze skokiem do wiadomości.
/// </summary>
public sealed class SearchPanel : Grid
{
    readonly Shell _sh;
    readonly TextBox _box;
    readonly StackPanel _scopes = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly StackPanel _list = new() { Spacing = 6, Padding = U.Th(12, 4, 12, 16) };
    readonly TextBlock _status = U.T("", 12, U.Semi, Theme.Mu);
    readonly ScrollViewer _sv;
    readonly long _channelId, _serverId;
    string _scope;        // channel | server | dms | all
    string? _has;
    int _offset;
    int _seq;
    CancellationTokenSource? _cts;

    Store S => _sh.S;

    public SearchPanel(Shell sh, string? initial)
    {
        _sh = sh;
        _channelId = sh.ChannelId;
        _serverId = sh.ServerId;
        _scope = sh.ServerId > 0 ? "server" : sh.ChannelId != 0 ? "channel" : "dms";
        this.Rows("auto", "auto", "auto", "*");

        _box = new TextBox { Text = initial ?? "", PlaceholderText = "Szukaj… (np. od:@ola ma:zdjęcie)", Height = 36, FontSize = 13.5 };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; Run(0); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; _sh.CloseSidePanel(); }
        };
        var head = new Grid { Padding = U.Th(12, 10, 8, 6) }.Cols("auto", "*", "auto").Add(
            U.I("", 16, Theme.Mu).With(i => i.Margin = U.Th(2, 0, 10, 0)).At(0, 0),
            _box.At(0, 1),
            U.IconButton(G.Close, "Zamknij wyszukiwanie (Esc)", _sh.CloseSidePanel, 14, 32).With(b => b.Margin = U.Th(4, 0, 0, 0)).At(0, 2));
        var tools = U.V(8, U.Scroll2(_scopes), U.Scroll2(_filters));
        tools.Margin = U.Th(12, 0, 12, 6);
        _status.Margin = U.Th(14, 4, 12, 4);
        _sv = U.Scroll(_list);
        this.Add(head.At(0), tools.At(1), _status.At(2), _sv.At(3));
        DrawChips();
        Loaded += (_, _) => { _box.Focus(FocusState.Programmatic); _box.SelectAll(); };
        if (!string.IsNullOrWhiteSpace(initial)) Run(0);
        else Hint();
    }

    public void Focus(string? text)
    {
        if (text is not null) { _box.Text = text; Run(0); }
        _box.Focus(FocusState.Programmatic);
        _box.SelectAll();
    }

    void Hint()
    {
        _list.Children.Clear();
        _status.Text = "";
        _list.Add(U.V(8,
            U.T("Wpisz, czego szukasz, i naciśnij Enter.", 13.5, U.Semi, Theme.Fg, wrap: true),
            Tip("od:@nazwa", "wiadomości tej osoby"),
            Tip("ma:zdjęcie", "ze zdjęciem (też: plik, link, przypięte)"),
            U.T("Polskie znaki możesz pomijać: „zolw” znajdzie „żółw”.", 12, null, Theme.Mu, wrap: true)).With(p => p.Margin = U.Th(2, 10, 2, 0)));
        static FrameworkElement Tip(string code, string what) => U.H(8,
            U.Box(U.T(code, 12, U.Bold, Theme.Accent).With(t => t.FontFamily = Theme.Mono), U.W(.08), 6, U.Th(6, 2)),
            U.T(what, 12.5, null, Theme.Mu, wrap: true));
    }

    void DrawChips()
    {
        _scopes.Children.Clear();
        void Scope(string key, string label)
        {
            var on = _scope == key;
            var b = Chip(label, on);
            b.Click += (_, _) => { if (_scope == key) return; _scope = key; DrawChips(); if (!_sh.C.ParseSearch(_box.Text).IsEmpty || _has is not null) Run(0); };
            _scopes.Add(b);
        }
        if (_channelId > 0 || (_channelId != 0 && S.DmById(_channelId) is not null)) Scope("channel", S.ChannelById(_channelId) is { } c ? "#" + c.Name : "Ta rozmowa");
        if (_serverId > 0 && S.ServerById(_serverId) is { } srv) Scope("server", srv.Name);
        Scope("dms", "Rozmowy prywatne");
        Scope("all", "Wszędzie");

        _filters.Children.Clear();
        foreach (var (key, label) in new[] { ("image", "🖼️ Zdjęcia"), ("file", "📎 Pliki"), ("link", "🔗 Linki"), ("pinned", "📌 Przypięte") })
        {
            var on = _has == key;
            var b = Chip(label, on);
            b.Click += (_, _) => { _has = _has == key ? null : key; DrawChips(); Run(0); };
            _filters.Add(b);
        }
    }

    static Button Chip(string label, bool on) => new()
    {
        Content = U.T(label, 12, U.Bold, on ? Theme.OnAccent : Theme.Fg).With(t => t.MaxWidth = 170), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0),
        CornerRadius = new CornerRadius(12), Padding = U.Th(10, 3), Height = 26,
    };

    SearchQuery Query(int offset)
    {
        var q = _sh.C.ParseSearch(_box.Text) with { Offset = offset };
        if (_has is not null) q = q with { Has = _has };
        return _scope switch
        {
            "channel" => q with { ChannelId = _channelId },
            "server" => q with { ServerId = _serverId },
            "dms" => q with { Dms = true },
            _ => q,
        };
    }

    async void Run(int offset)
    {
        var q = Query(offset);
        if (q.IsEmpty) { Hint(); return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var seq = ++_seq;
        _offset = offset;
        if (offset == 0)
        {
            _list.Children.Clear();
            _list.Add(new ProgressRing { IsActive = true, Width = 26, Height = 26, Foreground = Theme.Mu, Margin = U.Th(0, 20, 0, 0) });
            _status.Text = "Szukam…";
        }
        var (r, err) = await _sh.C.Search(q, _cts.Token);
        if (seq != _seq) return;
        if (offset == 0) _list.Children.Clear();
        else if (_list.Children.Count > 0 && _list.Children[^1] is Button) _list.Children.RemoveAt(_list.Children.Count - 1);
        if (err is not null) { _status.Text = ""; _list.Add(U.T(err, 13, null, Theme.Red, wrap: true)); return; }
        if (r is null) return;
        _status.Text = r.Total == 0 ? "Nic nie znaleziono." : $"{r.Total} {Format.Plural(r.Total, "wynik", "wyniki", "wyników")}";
        if (r.Total == 0)
        {
            _list.Add(U.T(_scope == "channel" ? "Spróbuj szukać na całym serwerze albo wszędzie." : "Spróbuj innych słów albo usuń filtry.", 13, null, Theme.Mu, wrap: true).With(t => t.Margin = U.Th(2, 8, 0, 0)));
            return;
        }
        long lastChan = -1;
        foreach (var m in r.Messages)
        {
            if (m.Key != lastChan && (_scope != "channel" || m.ThreadId is not null))
            {
                lastChan = m.Key;
                _list.Add(U.T(Where(m, r), 11.5, U.Bold, Theme.Mu2).With(t => t.Margin = U.Th(4, 8, 0, 0)));
            }
            _list.Add(Card(m));
        }
        if (offset + r.Messages.Count < r.Total)
        {
            var more = U.Ghost($"Pokaż więcej ({r.Total - offset - r.Messages.Count})", () => Run(offset + 25), 34);
            more.HorizontalAlignment = HorizontalAlignment.Stretch;
            _list.Add(more);
        }
        if (offset == 0) _sv.ChangeView(null, 0, null, true);
    }

    string Where(Message m, SearchResult r)
    {
        if (S.DmById(m.ChannelId) is { } d) return d.IsGroup ? "👥 " + S.DmTitle(d) : "@" + S.DmTitle(d);
        var name = S.ChannelById(m.ChannelId)?.Name ?? r.Channels.GetValueOrDefault(m.ChannelId)?.Name ?? "kanał";
        var srv = S.ServerOfChannel(m.ChannelId);
        return "#" + name + (srv is not null && _scope == "all" ? " · " + srv.Name : "") + (m.ThreadId is not null ? " · 🧵 wątek" : "");
    }

    FrameworkElement Card(Message m)
    {
        var srv = S.ServerOfChannel(m.ChannelId);
        var api = _sh.C.Api;
        var body = new StackPanel { Spacing = 3 };
        body.Add(U.H(8, U.T(S.NameOf(m.AuthorId, srv), 13.5, U.Bold, U.B(S.NameColor(m.AuthorId, srv))), U.T(Format.When(m.CreatedAt), 11, null, Theme.Mu2),
            m.PinnedAt is not null ? U.T("📌", 11) : null));
        if (!string.IsNullOrWhiteSpace(m.Content))
        {
            var content = m.Content.Length > 600 ? m.Content[..600] + "…" : m.Content;
            body.Add(RichText.Build(content, new RichText.Ctx(S, api, srv, null, 13.5)));
        }
        foreach (var a in m.Attachments.Take(4))
        {
            if (a.IsImage && api.Abs(a.Url) is { } u)
            {
                double w = a.Width ?? 160, h = a.Height ?? 120;
                var k = Math.Min(1, Math.Min(220 / w, 140 / h));
                body.Add(new Border { CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(30, w * k), Height = Math.Max(30, h * k),
                    Child = new Image { Source = Images.Get(u, 440), Stretch = Stretch.UniformToFill } });
            }
            else body.Add(U.T("📎 " + a.Name, 12.5, U.Semi, RichText.LinkBrush));
        }
        if (m.Sticker is { } st) body.Add(U.T("Naklejka: " + st.Name, 12.5, null, Theme.Mu));
        var g = new Grid().Cols("40", "*").Add(new Avatar(32).Set(S, api, m.AuthorId, srv).With(a => a.VerticalAlignment = VerticalAlignment.Top).At(0, 0), body.At(0, 1));
        var card = new Border { Child = g, Background = Theme.Card, CornerRadius = new CornerRadius(10), Padding = U.Th(10, 8), BorderBrush = Theme.Line, BorderThickness = U.Th(1) };
        card.PointerEntered += (_, _) => card.Background = U.W(.08);
        card.PointerExited += (_, _) => card.Background = Theme.Card;
        card.Tip("Przejdź do wiadomości");
        card.Tapped += (_, _) => _sh.JumpToMessage(m.ChannelId, m.Id, m.ThreadId);
        return card;
    }
}
