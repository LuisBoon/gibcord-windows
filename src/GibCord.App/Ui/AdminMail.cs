using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>
/// Poczta w panelu admina (jak /admin/poczta na stronie, API.md sekcja 16): skrzynki i foldery | lista | podgląd.
/// HTML maila przychodzi oczyszczony z serwera, a i tak pokazujemy go w WebView2 bez skryptów i z CSP blokującym
/// zdalne zasoby, dopóki ktoś nie kliknie „Pokaż obrazki”. Linki otwierają się w przeglądarce.
/// </summary>
public sealed partial class AdminMail : Grid
{
    sealed class Addr { public string Address { get; set; } = ""; public string? Name { get; set; } }
    sealed class Att { public long Id { get; set; } public string Filename { get; set; } = ""; public string? ContentType { get; set; } public long Size { get; set; } public string Url { get; set; } = ""; }
    sealed class Box
    {
        public long Id { get; set; }
        public string LocalPart { get; set; } = "";
        public string Address { get; set; } = "";
        public string? DisplayName { get; set; }
        public List<string> Aliases { get; set; } = [];
        public bool System { get; set; }
        public JsonElement Unread { get; set; }
        public int Total { get; set; }
    }
    sealed class Mail
    {
        public long Id { get; set; }
        public long BoxId { get; set; }
        public string Folder { get; set; } = "inbox";
        public long? ThreadId { get; set; }
        public int? ThreadCount { get; set; }
        public string Direction { get; set; } = "in";
        public Addr? From { get; set; }
        public List<Addr> To { get; set; } = [];
        public List<Addr> Cc { get; set; } = [];
        public List<Addr>? ReplyTo { get; set; }
        public string? Subject { get; set; }
        public string? Snippet { get; set; }
        public long Date { get; set; }
        public long ReceivedAt { get; set; }
        public bool Seen { get; set; }
        public bool Starred { get; set; }
        public bool HasAttachments { get; set; }
        public string? Text { get; set; }
        public string? Html { get; set; }
        public bool HasRemoteImages { get; set; }
        public List<Att> Attachments { get; set; } = [];
        public JsonElement Auth { get; set; }
        public long When => Date > 0 ? Date : ReceivedAt;
    }

    static readonly (string Key, string Name, string Glyph)[] Folders =
    [
        ("inbox", "Odebrane", ""), ("sent", "Wysłane", ""), ("starred", "Z gwiazdką", ""),
        ("spam", "Spam", ""), ("archive", "Archiwum", ""), ("trash", "Kosz", ""),
    ];
    static readonly System.Globalization.CultureInfo Pl = new("pl-PL");

    readonly Shell _sh;
    readonly StackPanel _left = new() { Spacing = 2, Padding = U.Th(8, 12, 8, 12) };
    readonly StackPanel _rows = new() { Spacing = 2, Padding = U.Th(6, 0, 6, 12) };
    readonly Grid _read = new();
    readonly TextBox _search = new() { PlaceholderText = "Szukaj w poczcie…" };
    readonly TextBlock _listTitle = U.T("", 15, U.Bold);
    List<Box> _boxes = [];
    string _domain = "";
    long _box;
    string _folder = "inbox";
    string _q = "";
    List<Mail> _list = [];
    long? _next;
    Mail? _cur;
    List<Mail> _thread = [];
    bool _imgs;
    int _listVer, _openVer;
    WebView2? _web;
    bool _webFailed;
    Grid _mid = new();
    /// <summary>Wąskie okno: zamiast trzech kolumn lista albo podgląd (z „Wróć”).</summary>
    bool _narrow;

    GibCordApi Api => _sh.C.Api;

    public AdminMail(Shell sh)
    {
        _sh = sh;
        this.Cols("230", "370", "*");
        var compose = U.Primary("Napisz", () => _ = Compose(null, null, false), 36).With(b => { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Margin = U.Th(0, 0, 0, 10); });
        var left = new Grid { Background = U.W(.03), BorderBrush = Theme.Line, BorderThickness = U.Th(0, 0, 1, 0) }.Rows("auto", "*");
        left.Add(new Border { Padding = U.Th(8, 12, 8, 0), Child = compose }.At(0), U.Scroll(_left).At(1));

        _search.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { _q = _search.Text.Trim(); _cur = null; _thread = []; _openVer++; DrawRead(); _ = LoadList(); } };
        var tools = U.H(2,
            U.IconButton("", "Odśwież", () => { _ = LoadMeta(); _ = LoadList(); }, 13, 30),
            U.IconButton("", "Oznacz wszystko jako przeczytane", () => _ = AllRead(), 13, 30));
        var midHead = U.V(8, new Grid().Cols("*", "auto").Add(_listTitle.At(0, 0), tools.At(0, 1)), _search);
        midHead.Padding = U.Th(12, 12, 12, 8);
        var mid = _mid = new Grid { BorderBrush = Theme.Line, BorderThickness = U.Th(0, 0, 1, 0) }.Rows("auto", "*");
        mid.Add(midHead.At(0), U.Scroll(_rows).At(1));

        this.Add(left.At(0, 0), mid.At(0, 1), _read.At(0, 2), _tools.At(0, 1, colSpan: 2));
        SizeChanged += (_, e) => { var n = e.NewSize.Width < 980; if (n != _narrow) { _narrow = n; Layout(); DrawRead(); } };
        DrawRead();
        Loaded += async (_, _) => { await LoadMeta(); await LoadList(); };
        Unloaded += (_, _) => { _closed = true; CloseWeb(); };
    }

    /// <summary>Szeroko: skrzynki | lista | podgląd. Wąsko: skrzynki | lista albo podgląd.</summary>
    void Layout()
    {
        ColumnDefinitions[0].Width = new GridLength(_narrow ? 190 : 230);
        ColumnDefinitions[1].Width = _narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(370);
        ColumnDefinitions[2].Width = _narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        var showRead = _narrow && _cur is not null;
        Grid.SetColumn(_read, _narrow ? 1 : 2);
        var tool = _view != "folder";
        _tools.Visibility = tool ? Visibility.Visible : Visibility.Collapsed;
        _mid.Visibility = tool || showRead ? Visibility.Collapsed : Visibility.Visible;
        _read.Visibility = !tool && (!_narrow || showRead) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── dane ─────────────────────────

    static int UnreadOf(Box b, string folder) =>
        b.Unread.ValueKind == JsonValueKind.Object && b.Unread.TryGetProperty(folder, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32()
        : b.Unread.ValueKind == JsonValueKind.Number && folder == "inbox" ? b.Unread.GetInt32() : 0;

    int _metaVer;

    async Task LoadMeta()
    {
        var ver = ++_metaVer;
        try
        {
            var r = Options.Demo ? Demo("meta") : await Api.GetJson("admin/mail");
            if (ver != _metaVer) return; // nowsze zapytanie już w drodze — liczniki z niego
            _domain = r.Str("domain") ?? "";
            _meta = r;
            _boxes = r.Prop("boxes").To<List<Box>>() ?? [];
            if (_box == 0 || _boxes.All(b => b.Id != _box)) _box = (_boxes.FirstOrDefault(b => !b.System) ?? _boxes.FirstOrDefault())?.Id ?? 0;
            DrawLeft();
        }
        catch (Exception ex) { _left.Children.Clear(); _left.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    }

    string ListQuery(long? before)
    {
        var q = new List<string> { "limit=50" };
        if (_box != 0) q.Add("boxId=" + _box);
        if (_q.Length > 0) q.Add("q=" + Uri.EscapeDataString(_q));
        else if (_folder == "starred") q.Add("starred=1");
        else q.Add("folder=" + _folder);
        if (before is long b) q.Add("before=" + b);
        return "admin/mail/messages?" + string.Join('&', q);
    }

    async Task LoadList(bool more = false)
    {
        var ver = ++_listVer;
        if (!more) { _rows.Children.Clear(); _rows.Add(U.T("Wczytuję…", 13, null, Theme.Mu2).With(t => t.Margin = U.Th(8))); }
        DrawListTitle();
        try
        {
            var r = Options.Demo ? Demo("list") : await Api.GetJson(ListQuery(more ? _next : null));
            if (ver != _listVer) return;
            var msgs = r.Prop("messages").To<List<Mail>>() ?? [];
            _next = r.Long("next");
            if (more) _list.AddRange(msgs); else _list = msgs;
            DrawRows();
            if (Options.Demo && _cur is null && _list.Count > 0) _ = Open(_list[0].Id);
        }
        catch (Exception ex)
        {
            if (ver != _listVer) return;
            _rows.Children.Clear();
            _rows.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true).With(t => t.Margin = U.Th(8)));
        }
    }

    async Task Open(long id, bool images = false)
    {
        var ver = ++_openVer;
        _imgs = images;
        try
        {
            var m = (Options.Demo ? Demo("msg") : await Api.GetJson($"admin/mail/messages/{id}" + (images ? "?images=1&peek=1" : ""))).To<Mail>();
            if (ver != _openVer || m is null) return;
            _cur = m;
            _thread = [];
            if (_list.Find(x => x.Id == id) is { Seen: false } row) { row.Seen = true; DrawRows(); }
            if (!images) _ = LoadMeta(); // otwarcie oznacza jako przeczytane (także maile z listy wątku)
            DrawRead();
            if (m.ThreadId is long t)
            {
                var th = (Options.Demo ? Demo("thread") : await Api.GetJson($"admin/mail/threads/{t}")).Prop("messages").To<List<Mail>>() ?? [];
                if (ver != _openVer) return;
                if (th.Count > 1) { _thread = th; DrawRead(); }
            }
        }
        catch (Exception ex) { if (ver == _openVer) _sh.Info(GibCordClient.Friendly(ex), true); }
    }

    async Task<bool> Patch(Mail m, object body, string? ok = null)
    {
        try
        {
            await Api.PatchJson($"admin/mail/messages/{m.Id}", body);
            if (ok is not null) _sh.Info(ok);
            return true;
        }
        catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); return false; }
    }

    async Task Move(Mail m, string folder, string ok)
    {
        if (!await Patch(m, new { folder }, ok)) return;
        RemoveCurrent(m);
    }

    async Task Delete(Mail m)
    {
        if (m.Folder == "trash" && !await M.Confirm(_sh, "Usunąć na zawsze?", "Wiadomość i jej załączniki znikną z serwera. Tego nie da się cofnąć.", "Usuń")) return;
        try
        {
            var r = await Api.DeleteJson($"admin/mail/messages/{m.Id}");
            _sh.Info(r.Str("deleted") == "permanent" ? "Usunięto na zawsze" : "Przeniesiono do kosza");
            RemoveCurrent(m);
        }
        catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
    }

    void RemoveCurrent(Mail m)
    {
        _list.RemoveAll(x => x.Id == m.Id);
        if (_cur?.Id == m.Id) { _cur = null; _thread = []; _openVer++; }
        DrawRows();
        DrawRead();
        _ = LoadMeta();
    }

    async Task AllRead()
    {
        var ids = _list.Where(m => !m.Seen).Select(m => m.Id).Take(200).ToList();
        if (ids.Count == 0) { _sh.Info("Wszystko przeczytane"); return; }
        try
        {
            await Api.PostJson("admin/mail/messages/bulk", new { ids, seen = true });
            foreach (var m in _list) if (ids.Contains(m.Id)) m.Seen = true;
            DrawRows();
            _ = LoadMeta();
        }
        catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
    }

    /// <summary>Dane pokazowe (--demo, zrzuty w CI).</summary>
    static JsonElement Demo(string what)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        object Msg(long id, string from, string name, string subj, string snip, long ago, bool seen, bool star = false, bool att = false, string dir = "in") => new
        {
            id, boxId = 1, folder = dir == "in" ? "inbox" : "sent", threadId = 7, direction = dir, from = new { address = from, name }, to = new[] { new { address = "kontakt@gibcord.pl", name = "GibCord" } },
            cc = Array.Empty<object>(), subject = subj, snippet = snip, date = now - ago, receivedAt = now - ago, seen, starred = star, hasAttachments = att,
            text = snip + "\n\nPozdrawiam,\n" + name, html = (string?)null, hasRemoteImages = false,
            attachments = att ? new[] { new { id = 3, filename = "zrzut.png", contentType = "image/png", size = 182000, url = "/api/admin/mail/attachments/3" } } : [],
            auth = new { spf = "pass", dkim = "pass", dmarc = "pass" },
        };
        object data = what switch
        {
            "meta" => new
            {
                domain = "gibcord.pl",
                boxes = new object[]
                {
                    new { id = 1, localPart = "kontakt", address = "kontakt@gibcord.pl", displayName = "GibCord", aliases = new[] { "postmaster", "abuse" }, system = false, unread = new { inbox = 2, spam = 1 }, total = 17 },
                    new { id = 2, localPart = "noreply", address = "noreply@gibcord.pl", displayName = "GibCord", aliases = Array.Empty<string>(), system = true, unread = new { inbox = 0, spam = 0 }, total = 3 },
                },
            },
            "list" => new
            {
                messages = new[]
                {
                    Msg(12, "ola@example.com", "Ola Nowak", "Pomysł na nowy kanał", "Hej! Może zrobimy kanał z muzyką na serwerze?", 600_000, false),
                    Msg(11, "partner@firma.pl", "Firma Partner", "Współpraca — propozycja", "Dzień dobry, chcielibyśmy zaproponować…", 7_200_000, false, att: true),
                    Msg(10, "kuba@example.com", "Kuba", "Re: Turniej w sobotę", "Będę o 18, biorę pady.", 90_000_000, true, star: true),
                },
                next = (long?)null,
            },
            "thread" => new { messages = new[] { Msg(9, "kontakt@gibcord.pl", "GibCord", "Turniej w sobotę", "Kto wpada?", 95_000_000, true, dir: "out"), Msg(12, "ola@example.com", "Ola Nowak", "Pomysł na nowy kanał", "Hej! Może zrobimy kanał z muzyką na serwerze?", 600_000, false) } },
            _ => Msg(12, "ola@example.com", "Ola Nowak", "Pomysł na nowy kanał", "Hej! Może zrobimy kanał z muzyką na serwerze? Mam już listę piosenek i bota do kolejki.", 600_000, true, att: true),
        };
        return JsonSerializer.SerializeToElement(data);
    }

    // ───────────────────────── lewa kolumna ─────────────────────────

    void DrawLeft()
    {
        _left.Children.Clear();
        foreach (var b in _boxes)
        {
            var head = U.V(0, U.T(b.LocalPart + "@", 13, U.Bold), U.T(b.DisplayName ?? (b.System ? "systemowa" : ""), 11, null, Theme.Mu2));
            head.Margin = U.Th(8, 10, 0, 4);
            _left.Add(head);
            foreach (var (key, name, glyph) in Folders)
            {
                var k = key;
                var bid = b.Id;
                var on = _view == "folder" && _box == b.Id && _folder == key && _q.Length == 0;
                var n = key is "inbox" or "spam" ? UnreadOf(b, key) : 0;
                var row = new Grid().Cols("26", "*", "auto").Add(
                    U.I(glyph, 13, on ? Theme.Fg : Theme.Mu).At(0, 0),
                    U.T(name, 13, on || n > 0 ? U.Bold : U.Semi, on ? Theme.Fg : Theme.Mu).At(0, 1),
                    n > 0 ? U.Badge(n).At(0, 2) : null);
                _left.Add(U.Row(row, () => { _view = "folder"; _box = bid; _folder = k; _q = ""; _search.Text = ""; _cur = null; _thread = []; _openVer++; DrawLeft(); DrawRead(); _ = LoadList(); }, on, 8, U.Th(8, 0), U.Th(0, 1)).With(r => r.Height = 32));
            }
        }
        _left.Add(U.HLine(.08).With(l => l.Margin = U.Th(4, 12)));
        void Tool(string key, string glyph, string label)
        {
            var on = _view == key;
            _left.Add(U.Row(U.H(8, U.I(glyph, 13, on ? Theme.Fg : Theme.Mu), U.T(label, 12.5, on ? U.Bold : U.Semi, on ? Theme.Fg : Theme.Mu)), () => ShowTool(key), on, 8, U.Th(8, 0), U.Th(0, 1)).With(r => r.Height = 32));
        }
        Tool("log", "\uE9D2", "Wysyłka i adresy");
        Tool("boxes", "\uE713", "Skrzynki i podpisy");
    }

    void DrawListTitle()
    {
        var box = _boxes.Find(b => b.Id == _box);
        _listTitle.Text = _q.Length > 0 ? $"Wyniki: „{_q}”" : $"{Folders.First(f => f.Key == _folder).Name}" + (box is null ? "" : " · " + box.LocalPart + "@");
    }

    // ───────────────────────── lista ─────────────────────────

    static string Who(Addr? a) => a is null ? "" : string.IsNullOrWhiteSpace(a.Name) ? a.Address : a.Name!;
    /// <summary>Data z nagłówka maila bywa absurdalna (spam z rokiem 10000) — przycinamy do zakresu .NET.</summary>
    static DateTimeOffset Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, 253_402_214_400_000)).ToLocalTime();
    static string When(long ms)
    {
        var d = Local(ms);
        return d.Date == DateTime.Today ? d.ToString("HH:mm") : d.Year == DateTime.Today.Year ? d.ToString("d MMM", Pl) : d.ToString("d.MM.yyyy");
    }
    static string Full(long ms) => Local(ms).ToString("ddd, d MMMM yyyy, HH:mm", Pl);

    void DrawRows()
    {
        _rows.Children.Clear();
        if (_list.Count == 0)
        {
            _rows.Add(U.T(_q.Length > 0 ? "Nic nie znaleziono." : "Pusto.", 13, null, Theme.Mu, wrap: true).With(t => t.Margin = U.Th(10)));
            return;
        }
        foreach (var m in _list)
        {
            var id = m.Id;
            var who = m.Direction == "out" ? "do: " + string.Join(", ", m.To.Select(Who)) : Who(m.From);
            var bold = !m.Seen && m.Direction == "in";
            var top = new Grid().Cols("*", "auto").Add(
                U.T(who, 13, bold ? U.Bold : U.Semi, bold ? Theme.Fg : Theme.Mu).At(0, 0),
                U.H(4, m.Starred ? U.I("", 11, Theme.Yellow) : null, m.HasAttachments ? U.I("", 11, Theme.Mu2) : null,
                    m.ThreadCount is > 1 ? U.T(m.ThreadCount.ToString()!, 10.5, U.Bold, Theme.Mu2) : null, U.T(When(m.When), 11, null, Theme.Mu2)).At(0, 1));
            var body = U.V(1, top,
                U.T(string.IsNullOrWhiteSpace(m.Subject) ? "(bez tematu)" : m.Subject!, 12.5, bold ? U.Bold : null, bold ? Theme.Fg : Theme.Mu),
                U.T(m.Snippet ?? "", 11.5, null, Theme.Mu2));
            // nieprzeczytana: pasek w kolorze akcentu z lewej
            var inner = new Grid().Cols("6", "*").Add(bold ? new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Left }.At(0, 0) : null, body.At(0, 1));
            _rows.Add(U.Row(inner, () => _ = Open(id), _cur?.Id == id, 8, U.Th(6, 7, 10, 7), U.Th(0, 1)));
        }
        if (_next is not null) _rows.Add(U.Ghost("Wczytaj starsze", () => _ = LoadList(more: true), 32).With(b => { b.HorizontalAlignment = HorizontalAlignment.Center; b.Margin = U.Th(0, 8); }));
    }

    // ───────────────────────── podgląd ─────────────────────────

    void DrawRead()
    {
        Layout();
        _read.Children.Clear();
        _read.RowDefinitions.Clear();
        var m = _cur;
        if (m is null)
        {
            _read.Add(U.V(8, U.I("", 40, Theme.Mu2), U.T("Wybierz wiadomość z listy.", 14, null, Theme.Mu))
                .With(p => { p.HorizontalAlignment = HorizontalAlignment.Center; p.VerticalAlignment = VerticalAlignment.Center; }));
            return;
        }
        _read.Rows("auto", "*");
        var head = new StackPanel { Spacing = 6, Padding = U.Th(18, 14, 18, 10) };
        if (_narrow) head.Add(U.Ghost("← Wróć do listy", () => { _cur = null; _thread = []; _openVer++; DrawRows(); DrawRead(); }, 28).With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        head.Add(U.T(string.IsNullOrWhiteSpace(m.Subject) ? "(bez tematu)" : m.Subject!, 18, U.Bold, wrap: true));
        var from = m.From is null ? "" : (string.IsNullOrWhiteSpace(m.From.Name) ? m.From.Address : $"{m.From.Name} <{m.From.Address}>");
        head.Add(U.T("Od: " + from, 12.5, U.Semi, wrap: true));
        head.Add(U.T("Do: " + string.Join(", ", m.To.Select(a => a.Address)) + (m.Cc.Count > 0 ? "   ·   DW: " + string.Join(", ", m.Cc.Select(a => a.Address)) : ""), 12, null, Theme.Mu, wrap: true));
        var meta = new WrapPanel { Spacing = 8 }.Add(U.T(Full(m.When), 11.5, null, Theme.Mu2)); // znaczki SPF/DKIM/DMARC zawijają się w wąskim podglądzie
        if (m.Direction == "in")
            foreach (var k in new[] { "spf", "dkim", "dmarc" })
                if (m.Auth.Str(k) is { } v)
                    meta.Add(U.Box(U.T($"{k.ToUpperInvariant()}: {v}", 10, U.Bold, v == "pass" ? Theme.Green : v is "fail" or "softfail" ? Theme.Red : Theme.Mu), U.W(.06), 5, U.Th(5, 1)));
        head.Add(meta);

        // przyciski
        var bar = new WrapPanel { Spacing = 4 };
        void Btn(string glyph, string label, Action a) => bar.Children.Add(U.Ghost("", a, 30).With(b => b.Content = U.H(6, U.I(glyph, 12), U.T(label, 12, U.Semi))));
        if (m.Direction == "in" && m.Folder is not ("trash" or "spam"))
        {
            Btn("", "Odpowiedz", () => _ = Compose(m, null, false));
            if (m.To.Count + m.Cc.Count > 1) Btn("", "Odpowiedz wszystkim", () => _ = Compose(m, null, true));
        }
        Btn("", "Przekaż", () => _ = Compose(null, m, false));
        Btn(m.Starred ? "" : "", m.Starred ? "Usuń gwiazdkę" : "Gwiazdka", async () =>
        {
            if (!await Patch(m, new { starred = !m.Starred })) return;
            m.Starred = !m.Starred;
            if (_list.Find(x => x.Id == m.Id) is { } r) r.Starred = m.Starred;
            DrawRows(); DrawRead();
        });
        if (m.Direction == "in") Btn("", "Nieprzeczytana", async () =>
        {
            if (!await Patch(m, new { seen = false })) return;
            if (_list.Find(x => x.Id == m.Id) is { } r) r.Seen = false;
            _cur = null; _openVer++; DrawRows(); DrawRead(); _ = LoadMeta();
        });
        if (m.Folder is not ("archive" or "trash" or "spam")) Btn("", "Archiwum", () => _ = Move(m, "archive", "Przeniesiono do archiwum"));
        if (m.Folder is "archive" or "trash") Btn("", "Przywróć", () => _ = Move(m, m.Direction == "out" ? "sent" : "inbox", "Przywrócono"));
        if (m.Direction == "in" && m.Folder == "spam") Btn("", "To nie spam", () => _ = Move(m, "inbox", "Przeniesiono do odebranych"));
        else if (m.Direction == "in" && m.Folder != "trash") Btn("", "Spam", () => _ = Move(m, "spam", "Oznaczono jako spam"));
        Btn("", m.Folder == "trash" ? "Usuń na zawsze" : "Kosz", () => _ = Delete(m));
        head.Add(bar);

        if (m.HasRemoteImages && !_imgs && m.Html is not null)
            head.Add(U.Box(U.H(10, U.T("Obrazki z internetu są zablokowane (nadawca nie dowie się, że czytasz).", 12, null, Theme.Mu),
                U.Ghost("Pokaż obrazki", () => _ = Open(m.Id, images: true), 28)), U.W(.05), 8, U.Th(10, 6)));
        if (m.Attachments.Count > 0)
        {
            var atts = new WrapPanel { Spacing = 6 };
            foreach (var a in m.Attachments)
            {
                var att = a;
                atts.Children.Add(U.Ghost("", () => _ = SaveAttachment(att), 30).With(b => b.Content = U.H(6, U.I("", 12), U.T($"{att.Filename} · {Format.Size(att.Size)}", 12, U.Semi))));
            }
            head.Add(atts);
        }
        if (_thread.Count > 1)
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var t in _thread)
            {
                var tid = t.Id;
                var txt = (t.Text ?? t.Snippet ?? "").Replace('\n', ' ').Replace('\r', ' ');
                list.Add(U.Row(new Grid().Cols("140", "*", "auto").Add(
                    U.T(t.Direction == "out" ? "Ty" : Who(t.From), 12, U.Bold).At(0, 0),
                    U.T(txt.Length > 90 ? txt[..90] : txt, 12, null, Theme.Mu).At(0, 1),
                    U.T(When(t.When), 11, null, Theme.Mu2).At(0, 2)), () => _ = Open(tid), t.Id == m.Id, 6, U.Th(8, 4), U.Th(0, 1)));
            }
            head.Add(new Expander { Header = U.T($"Wątek: {_thread.Count} {Format.Plural(_thread.Count, "wiadomość", "wiadomości", "wiadomości")}", 12.5, U.Semi), Content = list, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        _read.Add(U.Scroll(head).With(s => s.MaxHeight = 360).At(0));

        // treść: HTML w WebView2 (białe tło jak w programach pocztowych), inaczej tekst
        var host = new Border { Margin = U.Th(18, 0, 18, 18), CornerRadius = new CornerRadius(10), Background = U.B("#ffffff") };
        _read.Add(host.At(1));
        if (m.Html is not null && !_webFailed) _ = ShowHtml(host, m);
        else ShowText(host, m);
    }

    static void ShowText(Border host, Mail m)
    {
        host.Background = U.W(.04);
        host.Child = U.Scroll(U.T(m.Text ?? "", 13.5, null, Theme.Fg, wrap: true).With(t => { t.IsTextSelectionEnabled = true; t.Margin = U.Th(16); t.VerticalAlignment = VerticalAlignment.Top; }));
    }

    Task<WebView2>? _webTask;
    bool _closed, _expectNav;
    string? _tempFile;

    /// <summary>Jeden WebView2 na panel — kolejne wywołania czekają na to samo tworzenie (bez osieroconych procesów).</summary>
    Task<WebView2> GetWeb() => _webTask ??= CreateWeb();

    async Task<WebView2> CreateWeb()
    {
        var web = new WebView2();
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(App.DataDir, "webview-poczta"), new CoreWebView2EnvironmentOptions());
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        // linki z maila: tylko w przeglądarce (http/https/mailto), nigdy w podglądzie
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            // przepuszczamy wyłącznie nawigację, którą sami zleciliśmy (NavigateToString albo nasz plik tymczasowy)
            var ours = _expectNav && (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || e.Uri == "about:blank"
                || (_tempFile is not null && string.Equals(e.Uri, new Uri(_tempFile).AbsoluteUri, StringComparison.OrdinalIgnoreCase)));
            if (ours) { _expectNav = false; return; }
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        _web = web;
        if (_closed) { CloseWeb(); throw new OperationCanceledException(); }
        return web;
    }

    void CloseWeb()
    {
        try { _web?.Close(); } catch { }
        _web = null;
        _webTask = null;
        DeleteTemp();
    }

    void DeleteTemp()
    {
        if (_tempFile is { } f) { try { File.Delete(f); } catch { } _tempFile = null; }
    }

    async Task ShowHtml(Border host, Mail m)
    {
        WebView2 web;
        try { web = await GetWeb(); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // brak składnika WebView2 w Windowsie — do końca sesji pokazujemy tekst
            App.Log("Poczta: WebView2 — " + ex.Message);
            _webFailed = true;
            _webTask = null;
            if (_cur?.Id == m.Id && !_closed) ShowText(host, m);
            return;
        }
        if (_closed || _cur?.Id != m.Id || host.XamlRoot is null) return;
        try
        {
            if (web.Parent is Border old && !ReferenceEquals(old, host)) old.Child = null;
            host.Child = web;
            var csp = "default-src 'none'; img-src data:" + (_imgs ? " https: http:" : "") + "; style-src 'unsafe-inline'; font-src data:";
            var doc = $"<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"{csp}\"><base target=\"_blank\">" +
                "<style>html{color-scheme:light}body{margin:0;padding:18px 20px;font:14px/1.55 'Segoe UI',Arial,sans-serif;color:#222;background:#fff;overflow-wrap:anywhere}img{max-width:100%;height:auto}table{max-width:100%}blockquote{margin:0 0 0 6px;padding-left:10px;border-left:3px solid #ddd;color:#555}</style>" +
                $"</head><body>{m.Html}</body></html>";
            DeleteTemp();
            // NavigateToString ma limit ~2 MB (liczony w bajtach) — większe (obrazki wbudowane w treść) przez plik tymczasowy
            if (System.Text.Encoding.UTF8.GetByteCount(doc) < 1_400_000) { _expectNav = true; web.NavigateToString(doc); }
            else
            {
                var file = Path.Combine(App.DataDir, $"podglad-maila-{Guid.NewGuid():N}.html");
                await File.WriteAllTextAsync(file, doc);
                if (_closed || _cur?.Id != m.Id) { try { File.Delete(file); } catch { } return; }
                _tempFile = file;
                _expectNav = true;
                web.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
            }
        }
        catch (Exception ex)
        {
            // pojedynczy błąd (np. bardzo duży mail) — ten mail jako tekst, następne znowu w HTML
            App.Log("Poczta: podgląd — " + ex.Message);
            if (_cur?.Id == m.Id && !_closed) ShowText(host, m);
        }
    }

    static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto") _ = Launcher.LaunchUriAsync(u);
    }

    async Task SaveAttachment(Att a)
    {
        try
        {
            var ext = Path.GetExtension(a.Filename);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";
            var f = await FileBrowser.SaveFile(XamlRoot, Path.GetFileNameWithoutExtension(a.Filename) + ext, ext);
            if (f is null) return;
            var path = a.Url.StartsWith("/api/") ? a.Url[5..] : a.Url;
            var bytes = await Api.GetBytes(path);
            await File.WriteAllBytesAsync(f, bytes);
            U.MarkFromInternet(f); // załącznik z maila: Widok chroniony w Office, SmartScreen dla programów
            _sh.Info("Zapisano: " + Path.GetFileName(f));
        }
        catch (Exception ex) { _sh.Info("Nie udało się zapisać: " + GibCordClient.Friendly(ex), true); }
    }

    // ───────────────────────── pisanie ─────────────────────────

    static string Quote(Mail m)
    {
        var lines = string.Join("\n", (m.Text ?? "").Split('\n').Take(80).Select(l => "> " + l.TrimEnd('\r')));
        return $"\n\n{Full(m.When)}, {Who(m.From)} <{m.From?.Address}> napisał(a):\n{lines}";
    }

    static string Forwarded(Mail f) =>
        $"\n\n---------- Przekazana wiadomość ----------\nOd: {Who(f.From)} <{f.From?.Address}>\nData: {Full(f.When)}\nTemat: {f.Subject}\nDo: {string.Join(", ", f.To.Select(a => a.Address))}\n\n{f.Text}";

    /// <summary>Nowa wiadomość, odpowiedź (<paramref name="reply"/>) albo przekazanie (<paramref name="fwd"/>). Cytat dokleja się przy wysyłaniu, jak na stronie.</summary>
    async Task Compose(Mail? reply, Mail? fwd, bool all)
    {
        if (_boxes.Count == 0) { _sh.Info("Brak skrzynek — dodaj je na stronie.", true); return; }
        var fromBox = new DropField { Header = "Od", HorizontalAlignment = HorizontalAlignment.Stretch };
        var usable = _boxes.ToList();
        foreach (var b in usable) fromBox.Items.Add($"{b.DisplayName ?? b.LocalPart} <{b.Address}>");
        var defBox = (reply ?? fwd)?.BoxId ?? _box;
        fromBox.SelectedIndex = Math.Max(0, usable.FindIndex(b => b.Id == defBox && !b.System) is var i && i >= 0 ? i : usable.FindIndex(b => !b.System));
        var to = new TextBox { Header = "Do", PlaceholderText = "adres@przyklad.pl, druga@osoba.pl" };
        var cc = new TextBox { Header = "DW (opcjonalnie)" };
        var subject = new TextBox { Header = "Temat", MaxLength = 300 };
        var text = new TextBox { Header = "Treść", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 230 };
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        var sig = new CheckField { Content = "Dołącz podpis skrzynki", IsChecked = true };
        var note = U.T("", 11.5, null, Theme.Mu2, wrap: true);
        var err = U.T("", 12, null, Theme.Red, wrap: true);
        var files = new List<(long Id, string Name, ulong Size)>();
        var fileList = new WrapPanel { Spacing = 6 };
        var uploading = 0;
        ulong pendingBytes = 0;
        const ulong maxBytes = 10UL * 1048576; // jak na stronie: załączniki razem najwyżej 10 MB
        var addFile = U.Ghost("Dołącz plik…", async () =>
        {
            var f = await FileBrowser.PickFile(XamlRoot);
            if (f is null) return;
            var size = (await f.GetBasicPropertiesAsync()).Size;
            if (files.Aggregate(0UL, (n, x) => n + x.Size) + pendingBytes + size > maxBytes) { err.Text = "Załączniki razem mogą mieć najwyżej 10 MB."; return; }
            var label = U.T($"⏳ {f.Name}", 12, null, Theme.Mu);
            var chip = U.H(4, label);
            fileList.Children.Add(chip);
            uploading++;
            pendingBytes += size;
            try
            {
                using var s = await f.OpenStreamForReadAsync();
                var up = await Api.Upload(s, f.Name, f.ContentType);
                var entry = (up.Id, f.Name, size);
                files.Add(entry);
                label.Text = $"📎 {f.Name} · {Format.Size((long)size)}";
                label.Foreground = Theme.Fg;
                chip.Add(U.IconButton(G.Close, "Usuń z wiadomości", () => { files.Remove(entry); fileList.Children.Remove(chip); }, 9, 22));
            }
            catch (Exception ex) { label.Text = $"✕ {f.Name}: {GibCordClient.Friendly(ex)}"; label.Foreground = Theme.Red; }
            finally { uploading--; pendingBytes -= size; }
        }, 30);
        if (reply is not null)
        {
            var own = new HashSet<string>(_boxes.SelectMany(b => b.Aliases.Select(a => $"{a}@{_domain}").Append(b.Address)), StringComparer.OrdinalIgnoreCase);
            var main = (reply.ReplyTo is { Count: > 0 } rt ? rt[0] : reply.From)?.Address ?? "";
            var rcpt = new List<string> { main };
            if (all) rcpt.AddRange(reply.To.Concat(reply.Cc).Select(a => a.Address));
            // własne skrzynki i aliasy nie dostają kopii (jak na stronie); gdy zostałby nikt — główny adresat mimo wszystko
            var list = rcpt.Where(a => a.Length > 0 && !own.Contains(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            to.Text = string.Join(", ", list.Count > 0 ? list : [main]);
            subject.Text = System.Text.RegularExpressions.Regex.IsMatch(reply.Subject ?? "", "^(re|odp):", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? reply.Subject : "Re: " + reply.Subject;
            note.Text = "Pod Twoją odpowiedzią dołączy się cytat oryginału.";
        }
        if (fwd is not null)
        {
            subject.Text = System.Text.RegularExpressions.Regex.IsMatch(fwd.Subject ?? "", "^(fwd?|pd):", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? fwd.Subject : "Fwd: " + fwd.Subject;
            note.Text = "Dołączy się treść przekazywanej wiadomości" + (fwd.Attachments.Count > 0 ? " i jej załączniki." : ".");
        }
        var body = U.V(10, fromBox, to, cc, subject, text, U.H(10, sig, addFile), fileList, note, err);
        body.Width = 600;
        var d = new AppDialog
        {
            Title = reply is not null ? "Odpowiedź" : fwd is not null ? "Przekaż dalej" : "Nowa wiadomość",
            Content = new ScrollViewer { Content = body, MaxHeight = 620 }, PrimaryButtonText = "Wyślij", CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.None, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        d.Resources["ContentDialogMaxWidth"] = 700.0;
        var sending = false;
        var dialogClosed = false;
        d.Closed += (_, _) => dialogClosed = true;
        d.PrimaryButtonClick += async (_, e) =>
        {
            if (sending) { e.Cancel = true; return; } // drugi klik w trakcie wysyłki
            var def = e.GetDeferral();
            sending = true;
            d.IsPrimaryButtonEnabled = false;
            try
            {
                err.Text = "";
                if (to.Text.Trim().Length == 0) { err.Text = "Wpisz adresata."; e.Cancel = true; return; }
                if (uploading > 0) { err.Text = "Poczekaj, aż pliki się wyślą."; e.Cancel = true; return; }
                if (text.Text.Trim().Length == 0 && files.Count == 0 && fwd is null) { err.Text = "Napisz coś."; e.Cancel = true; return; }
                var box = usable[Math.Max(0, fromBox.SelectedIndex)];
                var content = text.Text.Replace("\r\n", "\n").Replace('\r', '\n') + (reply is not null ? Quote(reply) : fwd is not null ? Forwarded(fwd) : "");
                var payload = new Dictionary<string, object?>
                {
                    ["boxId"] = box.Id, ["to"] = to.Text.Trim(), ["cc"] = cc.Text.Trim(), ["subject"] = subject.Text.Trim(), ["text"] = content,
                    ["signature"] = sig.IsChecked == true, ["attachments"] = files.Select(f => f.Id).ToList(),
                };
                if (reply is not null) payload["inReplyTo"] = reply.Id;
                if (fwd is not null) { payload["forward"] = fwd.Id; payload["forwardAttachments"] = true; }
                await Api.PostJson("admin/mail/send", payload);
                _sh.Info("Wysłano");
            }
            catch (Exception ex)
            {
                err.Text = GibCordClient.Friendly(ex);
                e.Cancel = true;
                if (dialogClosed) _sh.Info("Nie wysłano: " + err.Text, true);
            }
            finally { sending = false; d.IsPrimaryButtonEnabled = true; def.Complete(); }
        };
        await M.WaitNoDialog(XamlRoot);
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        // odśwież wątek i listę (np. „Wysłane”)
        if (_cur is { } cur && (reply?.Id == cur.Id || fwd?.Id == cur.Id)) _ = Open(cur.Id);
        if (_folder == "sent") _ = LoadList();
    }
}
