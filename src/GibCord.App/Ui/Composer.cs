using System.Text.RegularExpressions;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>Pole wpisywania: Enter wysyła, Shift+Enter nowa linia, ↑ edytuje ostatnią, @ podpowiada osoby.</summary>
public sealed partial class Composer : StackPanel
{
    readonly ChatView _v;
    readonly TextBox _box;
    readonly Border _frame;
    /// <summary>Wysokość pola pisania w jednej linii i panelu „ja” w pasku bocznym — obie karty stoją na jednej linii.</summary>
    public const double BarHeight = 52;
    /// <summary>Odstęp kart na dole okna od jego dolnej krawędzi.</summary>
    public const double BottomGap = 12;
    readonly Border _bar = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(12, 12, 0, 0), Background = U.B("#4D0A0618"), Padding = U.Th(14, 6, 8, 6) };
    readonly StackPanel _files = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed, Padding = U.Th(12, 10, 12, 4) };
    readonly Border _blocked = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(14), Background = U.W(.05), Padding = U.Th(16, 8), Margin = U.Th(16, 4, 16, BottomGap), MinHeight = BarHeight };
    readonly TextBlock _hint = U.T("", 11, null, Theme.Mu2);
    readonly Button _attach;
    readonly Popup _suggest = new() { IsLightDismissEnabled = false };
    readonly StackPanel _suggestList = new() { Padding = U.Th(6) };
    readonly List<Pending> _pending = [];
    readonly Dictionary<string, long> _mentions = [];
    readonly Dictionary<string, long> _roleMentions = [];
    bool _restoring, _draftDirty, _submitting;
    string? _restoredText;
    Button _mic = null!;
    Button _later = null!;
    bool _narrow;
    readonly Later.Bar _schedBar;
    VoiceRecorder? _rec;
    readonly Border _recBar = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(14), Background = U.Alpha("#ff4d6a", .14), BorderBrush = U.Alpha("#ff4d6a", .5), BorderThickness = U.Th(1), Padding = U.Th(14, 8), MinHeight = BarHeight };
    readonly TextBlock _recTime = U.T("0:00", 14, U.Bold);
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _recTimer;
    ReplyRef? _reply;
    Message? _editing;
    List<(string Label, string Insert)> _sugg = [];
    int _suggIdx;
    bool _emojiMode; // podpowiedzi „:serce” zamiast „@osoba”
    int _atStart = -1;

    sealed class Pending
    {
        public string Name = "";
        public Attachment? Att;
        /// <summary>Obrazek do edycji przed wysłaniem (PNG/JPEG/BMP do 30 MB).</summary>
        public byte[]? Data;
        public string? Mime;
        public string? Error;
        public Task? Upload;
    }

    public Composer(ChatView v)
    {
        _v = v;
        _box = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, MinHeight = 46, FontSize = 14.5, FontFamily = Theme.Font,
            BorderThickness = U.Th(0), Background = Theme.Transparent, Padding = U.Th(4, 14, 4, 12), VerticalAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = true, Language = "pl-PL",
        };
        // przezroczyste tło także po najechaniu i w trakcie pisania
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _box.Resources[k] = Theme.Transparent;
        _box.PreviewKeyDown += OnKey;
        _box.TextChanged += (_, _) => OnText();
        _box.Paste += OnPaste;

        _attach = U.IconButton("\uE710", "Plik, ankieta, mini-gry", () => { }, 16, 34);
        _attach.Click += (_, _) =>
        {
            var menu = Fun.PlusMenu(_v, () => _ = PickFiles(), CanAttach);
            // wąskie pole: „Wyślij później” i wiadomość głosowa są wtedy tutaj (ich ikonki po prawej się chowają)
            if (_narrow)
            {
                var extra = new List<MenuFlyoutItemBase>();
                if (_later.Visibility == Visibility.Visible) extra.Add(M.Item("Wyślij później", "\uE823", OpenLater));
                if (_mic.Visibility == Visibility.Visible) extra.Add(M.Item("Wiadomość głosowa", "\uE720", () => _ = StartRecording()));
                if (extra.Count > 0) { menu.Items.Insert(0, new MenuFlyoutSeparator()); for (var i = extra.Count - 1; i >= 0; i--) menu.Items.Insert(0, extra[i]); }
            }
            menu.ShowAt(_attach);
        };
        var emoji = U.IconButton("\uE76E", "Emoji i naklejki", () => { }, 17, 34);
        emoji.Click += (_, _) => Picker.Show(emoji, _v, Picker.Tab.Emoji, Insert, SendGif, SendSticker);
        var gif = new Button
        {
            Content = new Border { BorderBrush = Theme.Mu, BorderThickness = U.Th(1.5), CornerRadius = new CornerRadius(4), Padding = U.Th(3, 0), Child = U.T("GIF", 10, U.Bold, Theme.Mu) },
            Width = 34, Height = 34, Padding = U.Th(0), Background = Theme.Transparent, BorderThickness = U.Th(0), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Bottom,
        };
        gif.Tip("GIF-y");
        gif.Click += (_, _) => Picker.Show(gif, _v, Picker.Tab.Gif, Insert, SendGif, SendSticker);
        _mic = U.IconButton("\uE720", "Wiadomość głosowa (nagraj i wyślij)", () => _ = StartRecording(), 16, 34);
        _mic.VerticalAlignment = VerticalAlignment.Bottom;
        // „Wyślij później” — tylko gdy serwer to obsługuje (nowy serwer)
        _later = U.IconButton("\uE823", "Wyślij później", () => { }, 16, 34);
        _later.VerticalAlignment = VerticalAlignment.Bottom;
        _later.Click += (_, _) => OpenLater();
        _schedBar = new Later.Bar(_v);
        var extraIcons = U.H(0, _later, _mic);
        var right = U.H(0, extraIcons, gif, emoji);
        // Jedna linia: pole 46 px + 2 px u góry i u dołu + ramka = 52 px, ikony (34 px) na środku — tak samo jak panel „ja” w pasku bocznym.
        // Przy dłuższym tekście pole rośnie, a ikony zostają przy dolnej krawędzi (6 px od niej).
        right.VerticalAlignment = VerticalAlignment.Bottom;
        right.Margin = U.Th(0, 0, 0, 6);
        var row = new Grid().Cols("auto", "*", "auto").Add(_attach.At(0, 0).With(b => { b.VerticalAlignment = VerticalAlignment.Bottom; b.Margin = U.Th(0, 0, 0, 6); }), _box.At(0, 1),
            right.At(0, 2));
        row.Margin = U.Th(6, 2, 6, 2);
        // wąskie okno: zegar i mikrofon chowają się (są w menu „+”), żeby GIF i emoji nie wypadały za krawędź
        row.SizeChanged += (_, e) =>
        {
            _narrow = e.NewSize.Width < 380;
            extraIcons.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
        };
        var inner = U.V(0, _files, row);
        _frame = new Border { Child = inner, CornerRadius = new CornerRadius(14), Background = Theme.Input, BorderBrush = Theme.InputLine, BorderThickness = U.Th(1) };
        _box.GotFocus += (_, _) => _frame.BorderBrush = Theme.AccentA(.45);
        _box.LostFocus += (_, _) => { _frame.BorderBrush = Theme.InputLine; HideSuggest(); };

        var cancelRec = U.Ghost("Anuluj", () => _ = StopRecording(false), 32);
        var sendRec = U.Primary("Wyślij", () => _ = StopRecording(true), 32).With(b => { b.HorizontalAlignment = HorizontalAlignment.Right; b.Width = 110; });
        _recBar.Child = new Grid().Cols("auto", "auto", "*", "auto", "auto").Add(
            new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 12, Height = 12, Fill = Theme.Red2, Margin = U.Th(0, 0, 10, 0) }.At(0, 0),
            U.H(8, U.T("Nagrywanie", 14, U.Semi), _recTime).At(0, 1),
            cancelRec.With(b => b.Margin = U.Th(0, 0, 8, 0)).At(0, 3), sendRec.At(0, 4));
        var wrap = U.V(0, _hint.With(h => { h.Margin = U.Th(4, 0, 0, 4); h.Visibility = Visibility.Collapsed; }), _schedBar.Root, _bar, _frame, _recBar);
        wrap.Margin = U.Th(16, 2, 16, BottomGap);
        this.Add(wrap, _blocked);
        _suggest.Child = new Border { Child = _suggestList, Background = U.B("#F5281f40"), CornerRadius = new CornerRadius(12), BorderBrush = U.W(.14), BorderThickness = U.Th(1), Width = 320 };
        Loaded += (_, _) => { _suggest.XamlRoot = XamlRoot; _v.Sh.S.Changed += OnStoreChanged; RefreshLater(); };
        Unloaded += (_, _) => _v.Sh.S.Changed -= OnStoreChanged;
        RefreshState();
        // szkic z poprzedniej wizyty na tym kanale
        if (App.Prefs.Drafts.TryGetValue(_v.Key, out var draft) && !string.IsNullOrEmpty(draft))
        {
            _restoring = true;
            _box.Text = Decode(draft);
            _restoredText = _box.Text; // TextChanged przychodzi później — po tym poznamy, że to nie pisanie
            _box.SelectionStart = _box.Text.Length;
            _restoring = false;
        }
    }

    /// <summary>Szkic zapisuje się na dysk przy wyjściu z kanału (w pamięci — na bieżąco).</summary>
    public void SaveDraft()
    {
        if (!_draftDirty) return;
        _draftDirty = false;
        if (App.Prefs.Drafts.Count > 60) foreach (var k in App.Prefs.Drafts.Keys.Take(App.Prefs.Drafts.Count - 60).ToList()) App.Prefs.Drafts.Remove(k);
        App.Prefs.Save();
    }

    Store S => _v.S;
    public bool CanAttach => _v.S.Can(_v.Cid, Perm.AttachFiles);
    public void FocusBox() { if (_box.Visibility == Visibility.Visible) _box.Focus(FocusState.Programmatic); }

    /// <summary>Uprawnienia, wyciszenie, kanał tylko do odczytu, tryb powolny.</summary>
    public void RefreshState()
    {
        var can = S.Can(_v.Cid, Perm.SendMessages);
        var ch = _v.Ch;
        string? why = null;
        if (S.TimedOutUntil(_v.Srv) is long until) why = $"Masz wyciszenie do {Format.When(until)} — nie możesz teraz pisać.";
        else if (!can) why = "Nie masz uprawnień do pisania na tym kanale.";
        else if (ch is { Readonly: true } && !S.Can(_v.Cid, Perm.ManageMessages)) why = "Ten kanał jest tylko do czytania.";
        else if (_v.DmObj is { IsGroup: false } d && S.DmOther(d) is { } o && S.Blocked.Contains(o.Id)) why = $"Blokujesz {o.Name}. Kliknij nazwę u góry rozmowy i wybierz „Odblokuj”, żeby pisać.";
        else if (_v.Thread is { Locked: true } && !S.Can(_v.Cid, Perm.ManageMessages)) why = "Ten wątek jest zamknięty. Piszą w nim tylko moderatorzy.";
        _blocked.Child = U.HG(10, U.I("", 15, Theme.Mu), U.T(why ?? "", 13.5, null, Theme.Mu, wrap: true)).With(h => h.VerticalAlignment = VerticalAlignment.Center);
        _blocked.Visibility = why is null ? Visibility.Collapsed : Visibility.Visible;
        ((FrameworkElement)Children[0]).Visibility = why is null ? Visibility.Visible : Visibility.Collapsed;
        _attach.Visibility = CanAttach || !_v.IsThread ? Visibility.Visible : Visibility.Collapsed;
        _mic.Visibility = CanAttach && !_v.Sh.C.Offline ? Visibility.Visible : Visibility.Collapsed;
        _box.PlaceholderText = _v.IsThread ? "Napisz w wątku" : ch is not null ? $"Napisz na #{ch.Name}" : _v.DmObj is { } dm ? $"Napisz do {(dm.IsGroup ? S.DmTitle(dm) : "@" + S.DmOther(dm)?.Name)}" : "Napisz wiadomość";
        _hint.Text = ch is { Slowmode: > 0 } && !S.Can(_v.Cid, Perm.ManageMessages) ? $"Tryb powolny: jedna wiadomość co {ch.Slowmode} s" : "";
        _hint.Visibility = why is null && _hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── klawisze i tekst ─────────────────────────

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (_suggest.IsOpen)
        {
            if (e.Key == VirtualKey.Down) { _suggIdx = (_suggIdx + 1) % _sugg.Count; DrawSuggest(); e.Handled = true; return; }
            if (e.Key == VirtualKey.Up) { _suggIdx = (_suggIdx - 1 + _sugg.Count) % _sugg.Count; DrawSuggest(); e.Handled = true; return; }
            if (e.Key is VirtualKey.Tab or VirtualKey.Enter) { Accept(_suggIdx); e.Handled = true; return; }
            if (e.Key == VirtualKey.Escape) { HideSuggest(); e.Handled = true; return; }
        }
        switch (e.Key)
        {
            case VirtualKey.Enter when !shift:
                e.Handled = true;
                _ = Submit();
                break;
            case VirtualKey.Escape when _editing is not null || _reply is not null:
                e.Handled = true;
                CancelBar();
                break;
            case VirtualKey.Up when _box.Text.Length == 0 && _editing is null:
                if (_v.LastOwn() is { } last) { e.Handled = true; StartEdit(last); }
                break;
        }
    }

    void OnText()
    {
        if (_restoring) return;
        if (_restoredText is not null && _box.Text == _restoredText) { _restoredText = null; return; } // szkic wczytany przy wejściu na kanał
        _restoredText = null;
        if (_box.Text.Length > 0 && _editing is null) _v.Sh.C.Typing(_v.Key);
        if (_editing is null)
        {
            var t = _box.Text.Trim();
            if (t.Length > 0) App.Prefs.Drafts[_v.Key] = Encode(_box.Text);
            else App.Prefs.Drafts.Remove(_v.Key);
            _draftDirty = true;
        }
        UpdateSuggest();
    }

    void Insert(string text)
    {
        var at = _box.SelectionStart;
        _box.Text = _box.Text.Insert(at, text);
        _box.SelectionStart = at + text.Length;
        _box.Focus(FocusState.Programmatic);
    }

    public void InsertMention(long uid)
    {
        var name = "@" + S.NameOf(uid, _v.Srv);
        _mentions[name] = uid;
        Insert((_box.Text.Length > 0 && !_box.Text.EndsWith(' ') ? " " : "") + name + " ");
    }

    // ── podpowiedzi @osoba ──

    [GeneratedRegex(@"(?:^|\s)@([\p{L}\p{N}_.]{0,32})$")]
    private static partial Regex AtRe();

    [GeneratedRegex(@"(?:^|\s):([\p{L}\p{N}_]{2,32})$")]
    private static partial Regex ColonRe();

    /// <summary>„:ser” → ❤️ serce, emoji serwerów (najpierw z tego serwera), ostatnio używane wyżej.</summary>
    bool EmojiSuggest(string upto)
    {
        var m = ColonRe().Match(upto);
        if (!m.Success) return false;
        var q = Fold(m.Groups[1].Value);
        var list = new List<(string Label, string Insert)>();
        foreach (var srv in S.Servers.Where(x => x.Emojis.Count > 0).OrderByDescending(x => x.Id == _v.Srv?.Id))
            foreach (var e in srv.Emojis.Where(e => Fold(e.Name).StartsWith(q) || (q.Length >= 3 && Fold(e.Name).Contains(q))))
            {
                if (list.Count >= 4) break;
                list.Add(($":{e.Name}:  ·  {srv.Name}", $"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>"));
            }
        var recent = App.Prefs.RecentEmoji;
        foreach (var (e, keys) in EmojiData.All
            .Where(x => Fold(x.Keys).Split(' ').Any(w => w.StartsWith(q)))
            .OrderBy(x => recent.IndexOf(x.E) is var i && i >= 0 ? i : 999)
            .Take(8 - list.Count))
            list.Add((keys, e));
        if (list.Count == 0) return false;
        _emojiMode = true;
        _sugg = list;
        _atStart = upto.Length - m.Groups[1].Value.Length - 1;
        return true;
    }

    void UpdateSuggest()
    {
        var upto = _box.Text[..Math.Min(_box.SelectionStart, _box.Text.Length)];
        if (EmojiSuggest(upto)) { ShowSuggest(); return; }
        _emojiMode = false;
        var m = AtRe().Match(upto);
        if (!m.Success) { HideSuggest(); return; }
        var q = Fold(m.Groups[1].Value);
        _atStart = upto.Length - m.Groups[1].Value.Length - 1;
        IEnumerable<long> ids = _v.Srv is { } srv ? srv.Members.Select(x => x.UserId) : _v.DmObj?.UserIds ?? [];
        _sugg = ids.Where(id => id != S.Me.Id)
            .Select(id => (Id: id, Name: S.NameOf(id, _v.Srv), User: S.U(id)?.Username ?? ""))
            .Where(x => Fold(x.Name).StartsWith(q) || Fold(x.User).StartsWith(q) || (q.Length >= 2 && Fold(x.Name).Contains(q)))
            .OrderBy(x => S.StatusOf(x.Id) == "offline").ThenBy(x => x.Name)
            .Take(7)
            .Select(x => ($"{x.Name}  ·  @{x.User}", "@" + x.Name + "|" + x.Id))
            .ToList();
        if (_v.Srv is { } rs)
        {
            var all = S.Can(_v.Cid, Perm.MentionEveryone);
            foreach (var r in rs.Roles.Where(r => r.Name != "@everyone" && (r.Mentionable || all) && (Fold(r.Name).StartsWith(q) || (q.Length >= 2 && Fold(r.Name).Contains(q)))).Take(4))
            {
                var n = rs.Members.Count(m => m.RoleIds.Contains(r.Id));
                _sugg.Add(($"@{r.Name}  ·  rola ({n} {Format.Plural(n, "osoba", "osoby", "osób")})", "@" + r.Name + "|r" + r.Id));
            }
        }
        if (_v.Srv is not null && S.Can(_v.Cid, Perm.MentionEveryone) && ("wszyscy".StartsWith(q) || "everyone".StartsWith(q)))
            _sugg.Add(("@wszyscy — powiadamia wszystkich na kanale", "@everyone|0"));
        if (_sugg.Count == 0) { HideSuggest(); return; }
        ShowSuggest();
    }

    void ShowSuggest()
    {
        _suggIdx = Math.Min(_suggIdx, _sugg.Count - 1);
        DrawSuggest();
        if (!_suggest.IsOpen)
        {
            var pt = _frame.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            _suggest.HorizontalOffset = pt.X + 8;
            _suggest.VerticalOffset = pt.Y - 12 - _sugg.Count * 36 - 12;
            _suggest.IsOpen = true;
        }
    }

    void DrawSuggest()
    {
        _suggestList.Children.Clear();
        for (var i = 0; i < _sugg.Count; i++)
        {
            var idx = i;
            var (label, ins) = _sugg[i];
            var key = ins[(ins.LastIndexOf('|') + 1)..];
            UIElement icon;
            if (_emojiMode)
                icon = Markup.ParseInline(ins).FirstOrDefault() is EmojiSpan es && S.EmojiById(es.Id) is { } ce
                    ? new Image { Source = Images.Get(_v.Sh.C.Api.Abs(ce.Url), 48), Width = 22, Height = 22 }
                    : U.T(ins, 18).With(t => { t.Width = 24; t.TextAlignment = TextAlignment.Center; });
            else if (key.StartsWith('r')) icon = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 14, Height = 14, Margin = U.Th(5, 0), Fill = U.B(S.RoleById(long.Parse(key[1..]))?.Color ?? "#8b8599") };
            else { var uid = long.Parse(key); icon = uid == 0 ? U.I("\uE716", 16) : new Avatar(24).Set(S, _v.Sh.C.Api, uid, _v.Srv); }
            var row = U.Row(U.H(10, icon, U.T(label, 13.5, U.Semi)), () => Accept(idx), i == _suggIdx, 8, U.Th(8, 0), U.Th(0, 1));
            row.Height = 34;
            _suggestList.Children.Add(row);
        }
        if (_suggest.IsOpen)
            _suggest.VerticalOffset = _frame.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).Y - 12 - _sugg.Count * 36 - 12;
    }

    void Accept(int i)
    {
        if (i < 0 || i >= _sugg.Count || _atStart < 0) return;
        var ins = _sugg[i].Insert;
        if (_emojiMode)
        {
            App.Prefs.UseEmoji(ins);
            var endE = Math.Min(_box.SelectionStart, _box.Text.Length);
            _box.Text = _box.Text[.._atStart] + ins + _box.Text[endE..];
            _box.SelectionStart = _atStart + ins.Length;
            HideSuggest();
            return;
        }
        var bar = ins.LastIndexOf('|');
        var name = ins[..bar];
        var key = ins[(bar + 1)..];
        if (key.StartsWith('r')) _roleMentions[name] = long.Parse(key[1..]);
        else if (long.Parse(key) is var uid && uid != 0) _mentions[name] = uid;
        var end = _box.SelectionStart;
        _box.Text = _box.Text[.._atStart] + name + " " + _box.Text[end..];
        _box.SelectionStart = _atStart + name.Length + 1;
        HideSuggest();
    }

    void HideSuggest() { _suggest.IsOpen = false; _atStart = -1; _suggIdx = 0; _emojiMode = false; }

    static string Fold(string s)
    {
        var n = s.ToLowerInvariant().Replace('ł', 'l').Normalize(System.Text.NormalizationForm.FormD);
        return new string(n.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    /// <summary>„@Ola” wybrane z podpowiedzi → &lt;@id&gt; w wysyłanej treści.</summary>
    string Encode(string text)
    {
        foreach (var (name, id) in _roleMentions.OrderByDescending(kv => kv.Key.Length))
            text = text.Replace(name, $"<@&{id}>");
        foreach (var (name, id) in _mentions.OrderByDescending(kv => kv.Key.Length))
            text = text.Replace(name, $"<@{id}>");
        return text;
    }

    string Decode(string content)
    {
        content = Regex.Replace(content, @"<@&([0-9]{1,18})>", m =>
        {
            if (!long.TryParse(m.Groups[1].Value, out var id)) return m.Value;
            var name = "@" + (S.RoleById(id)?.Name ?? "rola");
            _roleMentions[name] = id;
            return name;
        });
        return Regex.Replace(content, @"<@([0-9]{1,18})>", m =>
        {
            if (!long.TryParse(m.Groups[1].Value, out var id)) return m.Value;
            var name = "@" + S.NameOf(id, _v.Srv);
            _mentions[name] = id;
            return name;
        });
    }

    // ───────────────────────── odpowiedź / edycja ─────────────────────────

    public void StartReply(Message m)
    {
        _editing = null;
        _reply = new ReplyRef { Id = m.Id, AuthorId = m.AuthorId, Content = m.Content.Length > 200 ? m.Content[..200] : m.Content };
        ShowBar($"Odpowiadasz: {S.NameOf(m.AuthorId, _v.Srv)}", "");
        FocusBox();
    }

    public void StartEdit(Message m)
    {
        _reply = null;
        _editing = m;
        _box.Text = Decode(m.Content);
        _box.SelectionStart = _box.Text.Length;
        ShowBar("Edytujesz wiadomość · Enter zapisuje, Esc anuluje", "");
        FocusBox();
    }

    void ShowBar(string text, string glyph)
    {
        _bar.Child = new Grid().Cols("auto", "*", "auto").Add(U.I(glyph, 13, Theme.Accent).At(0, 0),
            U.T(text, 12.5, U.Semi, Theme.Mu).With(t => t.Margin = U.Th(8, 0)).At(0, 1),
            U.IconButton("", "Anuluj (Esc)", CancelBar, 11, 24).At(0, 2));
        _bar.Visibility = Visibility.Visible;
        RefreshLater();
    }

    void CancelBar()
    {
        if (_editing is not null) _box.Text = "";
        _editing = null;
        _reply = null;
        _bar.Visibility = Visibility.Collapsed;
        RefreshLater();
    }

    // ───────────────────────── wyślij później ─────────────────────────

    void OnStoreChanged(StoreChange ch) { if (ch.Kind is ChangeKind.Scheduled or ChangeKind.All) RefreshLater(); }

    void RefreshLater()
    {
        _later.Visibility = _v.Sh.C.CanSchedule && _editing is null && _rec is null ? Visibility.Visible : Visibility.Collapsed;
        _schedBar.Refresh();
        // zaokrąglenia: pasek zaplanowanych / odpowiedzi nad polem pisania łączą się z nim
        var sched = _schedBar.Visible;
        var bar = _bar.Visibility == Visibility.Visible;
        _bar.CornerRadius = sched ? new CornerRadius(0) : new CornerRadius(12, 12, 0, 0);
        _frame.CornerRadius = sched || bar ? new CornerRadius(0, 0, 14, 14) : new CornerRadius(14);
    }

    void OpenLater()
    {
        if (Encode(_box.Text).Trim().Length == 0 && !_pending.Any(p => p.Att is not null || p.Upload is { IsCompleted: false }))
        {
            _v.Sh.Info("Najpierw napisz wiadomość, a potem wybierz, kiedy ma wyjść.");
            FocusBox();
            return;
        }
        Later.Menu(_v.Sh, w => _ = SubmitLater(w), "Wyślij później").ShowAt(_later);
    }

    async Task SubmitLater(DateTimeOffset when)
    {
        if (_submitting) return;
        _submitting = true;
        try
        {
            HideSuggest();
            if (_pending.Any(p => p.Upload is { IsCompleted: false }))
            {
                _v.Sh.Info("Poczekaj, aż pliki się wyślą…");
                await Task.WhenAll(_pending.Where(p => p.Upload is not null).Select(p => p.Upload!));
            }
            var text = Encode(_box.Text).Trim();
            var atts = _pending.Where(p => p.Att is not null).Select(p => p.Att!).ToList();
            if (text.Length == 0 && atts.Count == 0) return;
            var err = await _v.Sh.C.Schedule(_v.Key, text, atts, _reply, when);
            if (err is not null) { _v.Sh.Info(err, true); return; }
            _box.Text = "";
            _mentions.Clear();
            _roleMentions.Clear();
            App.Prefs.Drafts.Remove(_v.Key);
            _draftDirty = true;
            CancelBar();
            ClearFiles();
            _v.Sh.C.StoppedTyping(_v.Key);
            _v.Sh.Info("Zaplanowano: " + Later.When(when.ToUnixTimeMilliseconds()));
        }
        finally { _submitting = false; }
    }

    // ───────────────────────── wysyłanie ─────────────────────────

    async Task Submit()
    {
        if (_submitting) return; // drugi Enter, gdy pliki jeszcze się wysyłają
        _submitting = true;
        try { await SubmitCore(); }
        finally { _submitting = false; }
    }

    /// <summary>Wyjście z kanału w trakcie nagrywania: nagranie przepada (nic się nie wysyła).</summary>
    public void CancelRecording() { if (_rec is not null) _ = StopRecording(false); }

    async Task SubmitCore()
    {
        HideSuggest();
        var text = Encode(_box.Text).Trim();
        if (_editing is { } m)
        {
            if (text.Length == 0) { CancelBar(); _v.Delete(m); return; }
            var em = m;
            CancelBar();
            _box.Text = "";
            if (text != em.Content) { var err = await _v.Sh.C.Edit(em, text); if (err is not null) _v.Sh.Info(err, true); }
            return;
        }
        if (_pending.Any(p => p.Upload is { IsCompleted: false }))
        {
            _v.Sh.Info("Poczekaj, aż pliki się wyślą…");
            await Task.WhenAll(_pending.Where(p => p.Upload is not null).Select(p => p.Upload!));
        }
        var atts = _pending.Where(p => p.Att is not null).Select(p => p.Att!).ToList();
        if (text.Length == 0 && atts.Count == 0) return;
        var max = _v.Sh.C.Info?.Limits.MessageLength ?? 4000;
        if (text.Length > max) { _v.Sh.Info($"Wiadomość jest za długa ({text.Length} z {max} znaków).", true); return; }
        var reply = _reply;
        _box.Text = "";
        _mentions.Clear();
        _roleMentions.Clear();
        App.Prefs.Drafts.Remove(_v.Key);
        _draftDirty = true;
        CancelBar();
        ClearFiles();
        _v.Sh.C.StoppedTyping(_v.Key);
        var error = await _v.Sh.C.Send(_v.Key, text, reply, atts);
        if (error is not null) _v.Sh.Info(error, true);
    }

    // ───────────────────────── wiadomość głosowa ─────────────────────────

    async Task StartRecording()
    {
        if (_rec is not null) return;
        if (!CanAttach) { _v.Sh.Info("Na tym kanale nie można wysyłać plików ani nagrań.", true); return; }
        if (_v.Sh.C.VoiceChannelId != 0) { _v.Sh.Info("Najpierw wyjdź z rozmowy — mikrofon jest teraz zajęty przez rozmowę.", true); return; }
        _rec = new VoiceRecorder();
        var err = await _rec.Start();
        if (err is not null) { _rec = null; _v.Sh.Info(err, true); return; }
        _frame.Visibility = Visibility.Collapsed;
        _recBar.Visibility = Visibility.Visible;
        _recTime.Text = "0:00";
        _recTimer = DispatcherQueue.CreateTimer();
        _recTimer.Interval = TimeSpan.FromMilliseconds(250);
        _recTimer.Tick += (_, _) =>
        {
            if (_rec is null) return;
            var t = _rec.Elapsed;
            _recTime.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            if (t.TotalMinutes >= 5) _ = StopRecording(true); // najwyżej 5 minut
        };
        _recTimer.Start();
    }

    async Task StopRecording(bool send)
    {
        var rec = _rec;
        if (rec is null) return;
        _rec = null;
        _recTimer?.Stop();
        _recBar.Visibility = Visibility.Collapsed;
        _frame.Visibility = Visibility.Visible;
        var r = await rec.Stop(send);
        if (!send || r is not { } got) return;
        if (got.Seconds < 1 || got.Data.Length < 1000) { _v.Sh.Info("Nagranie jest za krótkie.", true); return; }
        var reply = _reply;
        if (_reply is not null) CancelBar();
        var (att, err) = await _v.Sh.C.Upload(new MemoryStream(got.Data), $"wiadomosc-glosowa-{got.Seconds}s.m4a", "audio/mp4", got.Data.Length);
        if (err is not null || att is null) { _v.Sh.Info(err ?? "Nie udało się wysłać nagrania", true); return; }
        var e2 = await _v.Sh.C.Send(_v.Key, "", reply, [att]);
        if (e2 is not null) _v.Sh.Info(e2, true);
    }

    /// <summary>GIF z okienka: wiadomość z adresem (i rozmiarem) — tak samo jak na stronie.</summary>
    async void SendGif(GifItem g)
    {
        var reply = _reply;
        if (_reply is not null) CancelBar();
        var err = await _v.Sh.C.Send(_v.Key, g.MessageText, reply);
        if (err is not null) _v.Sh.Info(err, true);
    }

    async void SendSticker(Sticker st)
    {
        var reply = _reply;
        if (_reply is not null) CancelBar();
        var err = await _v.Sh.C.Send(_v.Key, "", reply, null, st);
        if (err is not null) _v.Sh.Info(err, true);
    }

    // ───────────────────────── pliki ─────────────────────────

    async Task PickFiles()
    {
        var files = await FileBrowser.PickFiles(XamlRoot);
        foreach (var f in files) await AddFile(f);
    }

    async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        var dv = Clipboard.GetContent();
        if (dv.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            foreach (var it in await dv.GetStorageItemsAsync()) if (it is StorageFile f) await AddFile(f);
        }
        else if (dv.Contains(StandardDataFormats.Bitmap) && !dv.Contains(StandardDataFormats.Text))
        {
            e.Handled = true;
            var r = await dv.GetBitmapAsync();
            using var s = await r.OpenReadAsync();
            var ms = new MemoryStream();
            await s.AsStreamForRead().CopyToAsync(ms);
            ms.Position = 0;
            AddStream(ms, $"wklejony-{DateTime.Now:yyyyMMdd-HHmmss}.png", "image/png", ms.Length);
        }
    }

    public async Task AddFile(StorageFile f)
    {
        if (!CanAttach) { _v.Sh.Info("Na tym kanale nie można wysyłać plików.", true); return; }
        var props = await f.GetBasicPropertiesAsync();
        var stream = await f.OpenStreamForReadAsync();
        AddStream(stream, f.Name, string.IsNullOrEmpty(f.ContentType) ? null : f.ContentType, (long)props.Size);
    }

    void AddStream(Stream data, string name, string? mime, long size)
    {
        var max = _v.Sh.C.Info?.Limits.AttachmentsPerMessage ?? 10;
        if (_pending.Count >= max) { _v.Sh.Info($"Najwyżej {max} plików w jednej wiadomości.", true); data.Dispose(); return; }
        var p = new Pending { Name = name, Mime = mime };
        _pending.Add(p);
        DrawFiles();
        var editable = ImageEditor.CanEdit(name, mime) && size <= 30 * 1024 * 1024;
        p.Upload = Task.Run(async () =>
        {
            if (editable)
            {
                // kopia w pamięci — do miniaturki i edytora
                var mem = new MemoryStream();
                await data.CopyToAsync(mem);
                data.Dispose();
                var bytes = mem.ToArray();
                data = new MemoryStream(bytes);
                _v.Sh.W.UiRun(() => { p.Data = bytes; DrawFiles(); });
            }
            var (att, err) = await _v.Sh.C.Upload(data, name, mime, size);
            data.Dispose();
            _v.Sh.W.UiRun(() => { p.Att = att; p.Error = err; DrawFiles(); if (err is not null) _v.Sh.Info($"{name}: {err}", true); });
        });
        FocusBox();
    }

    void DrawFiles()
    {
        _files.Children.Clear();
        foreach (var p in _pending.ToList())
        {
            var state = p.Error is not null ? U.I("", 14, Theme.Red) : p.Att is null ? new ProgressRing { IsActive = true, Width = 14, Height = 14 } : (FrameworkElement)U.I("", 14, Theme.Green);
            var x = U.IconButton("", "Usuń z wiadomości", () => { _pending.Remove(p); DrawFiles(); }, 10, 22);
            FrameworkElement? thumb = null, edit = null;
            if (p.Data is { } img)
            {
                var bi = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelHeight = 56 };
                _ = LoadThumb(bi, img);
                thumb = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Child = new Image { Source = bi, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill } };
                edit = U.IconButton("\uE70F", "Edytuj obrazek (przytnij, zamaż, rysuj)", () => EditPending(p), 12, 24);
            }
            var card = U.Box(U.H(8, thumb ?? state, U.T(p.Name, 12.5, U.Semi).With(t => t.MaxWidth = 180), thumb is null ? null : state, edit, x), U.W(.08), 10, U.Th(thumb is null ? 10 : 4, 4, 4, 4));
            _files.Children.Add(card);
        }
        _files.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static async Task LoadThumb(Microsoft.UI.Xaml.Media.Imaging.BitmapImage bi, byte[] data)
    {
        try
        {
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var w = ras.AsStreamForWrite()) { await w.WriteAsync(data); await w.FlushAsync(); }
            ras.Seek(0);
            await bi.SetSourceAsync(ras);
        }
        catch { }
    }

    /// <summary>Edycja obrazka przed wysłaniem — wynik idzie jako nowy plik (stary, już przesłany, serwer sprzątnie).</summary>
    async void EditPending(Pending p)
    {
        if (p.Data is not { } data) return;
        var r = await ImageEditor.Open(_v.Sh, data, p.Name);
        if (r is not { } got) return;
        var i = _pending.IndexOf(p);
        if (i < 0) return;
        _pending.RemoveAt(i);
        var name = System.IO.Path.GetFileNameWithoutExtension(p.Name) + (got.Mime == "image/jpeg" ? ".jpg" : ".png");
        AddStream(new MemoryStream(got.Data), name, got.Mime, got.Data.Length);
        // nowa karta trafia na koniec listy — wraca na stare miejsce
        if (_pending.Count > 0 && i < _pending.Count - 1) { var n = _pending[^1]; _pending.RemoveAt(_pending.Count - 1); _pending.Insert(i, n); DrawFiles(); }
    }

    void ClearFiles()
    {
        _pending.Clear();
        DrawFiles();
    }
}
