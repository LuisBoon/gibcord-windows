using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using User = GibCord.Core.User;

namespace GibCord.App.Ui;

/// <summary>
/// Tablica pomysłów (kanał kind="ideas", np. na serwerze społeczności): lista z głosami, sortowanie (najlepsze / nowe / gotowe),
/// nowy pomysł, szczegóły z komentarzami, status (moderatorzy), edycja i usuwanie.
/// </summary>
public sealed class IdeasView : Grid, IDisposable
{
    static readonly Dictionary<string, (string Label, string Color)> Status = new()
    {
        ["new"] = ("Nowy", "#8b8599"), ["planned"] = ("Planowany", "#7cc7ff"), ["wip"] = ("W budowie", "#f2c45a"), ["done"] = ("Gotowe", "#6fe3a5"), ["declined"] = ("Odrzucony", "#ff6b81"),
    };

    readonly Shell _sh;
    readonly long _cid;
    readonly StackPanel _list = new() { Spacing = 8, Padding = U.Th(24, 12, 24, 30), MaxWidth = 860 };
    readonly StackPanel _sorts = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    string _sort = "top";
    bool _canPost, _canMod, _disposed;

    GibCordApi Api => _sh.C.Api;
    Store S => _sh.S;

    public IdeasView(Shell sh, long cid)
    {
        _sh = sh;
        _cid = cid;
        this.Rows("48", "auto", "*");
        var ch = S.ChannelById(cid);
        var head = new Border { BorderBrush = Theme.Line, BorderThickness = U.Th(0, 0, 0, 1), Padding = U.Th(16, 0) };
        head.Child = new Grid().Cols("auto", "*", "auto").Add(U.H(10, U.T("💡", 18), U.T(ch?.Name ?? "pomysły", 15, U.Bold)).At(0, 0),
            U.T(ch?.Topic ?? "Zgłaszaj pomysły i głosuj na najlepsze", 12.5, null, Theme.Mu).With(t => t.Margin = U.Th(14, 0)).At(0, 1));
        var bar = new Grid { Padding = U.Th(24, 12, 24, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left }.Cols("*", "auto").Add(_sorts.At(0, 0),
            U.Primary("+ Nowy pomysł", () => Edit(null), 34).With(b => b.Width = 170).At(0, 1));
        this.Add(head.At(0), bar.At(1), U.Scroll(_list).At(2));
        _sh.C.IdeaEvent += OnIdea;
        DrawSorts();
        _ = Load();
    }

    public void Dispose() { _disposed = true; _sh.C.IdeaEvent -= OnIdea; }

    void OnIdea(long cid) { if (cid == _cid && !_disposed) _ = Load(); }

    void DrawSorts()
    {
        _sorts.Children.Clear();
        foreach (var (k, label) in new[] { ("top", "🔥 Najlepsze"), ("new", "🆕 Nowe"), ("done", "✅ Gotowe") })
        {
            var on = _sort == k;
            var b = new Button { Content = U.T(label, 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(12, 4) };
            b.Click += (_, _) => { _sort = k; DrawSorts(); _ = Load(); };
            _sorts.Add(b);
        }
    }

    void AddUsers(JsonElement map) { if (map.ValueKind == JsonValueKind.Object) foreach (var p in map.EnumerateObject()) if (p.Value.To<User>() is { } u && !S.Users.ContainsKey(u.Id)) S.Users[u.Id] = u; }

    async Task Load()
    {
        try
        {
            var r = await Api.GetJson($"channels/{_cid}/ideas?sort={_sort}");
            if (_disposed) return;
            AddUsers(r.Prop("users"));
            _canPost = r.Bool("canPost") == true;
            _canMod = r.Bool("canModerate") == true;
            _list.Children.Clear();
            foreach (var i in r.Prop("ideas").EnumerateArray()) _list.Add(Card(i));
            if (_list.Children.Count == 0) _list.Add(U.T(_sort == "done" ? "Jeszcze nic nie jest gotowe." : "Nie ma jeszcze pomysłów — dodaj pierwszy!", 14, null, Theme.Mu));
        }
        catch (Exception ex) { _list.Children.Clear(); _list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    }

    FrameworkElement StatusChip(string st) => Status.TryGetValue(st, out var x) ? U.Box(U.T(x.Label, 10.5, U.Bold, Theme.OnAccent), U.B(x.Color), 6, U.Th(6, 1)) : U.T(st, 11);

    FrameworkElement Card(JsonElement i)
    {
        var id = i.Long("id") ?? 0;
        var voted = i.Bool("voted") == true;
        var vote = new Button
        {
            Width = 54, Height = 58, CornerRadius = new CornerRadius(12), BorderThickness = U.Th(1), BorderBrush = voted ? Theme.Accent : U.W(.12), Background = voted ? Theme.AccentA(.15) : U.W(.05),
            Content = U.V(0, U.T("▲", 13, U.Bold, voted ? Theme.Accent : Theme.Mu).With(t => t.HorizontalAlignment = HorizontalAlignment.Center), U.T((i.Long("votes") ?? 0).ToString(), 15, U.Bold, voted ? Theme.Accent : Theme.Fg).With(t => t.HorizontalAlignment = HorizontalAlignment.Center)),
        };
        vote.Click += async (_, _) => { try { await Api.PostJson($"ideas/{id}/vote", new { on = !voted }); await Load(); } catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); } };
        var body = U.V(4, U.HG(8, StatusChip(i.Str("status") ?? "new"), U.T(i.Str("title") ?? "", 15, U.Bold, wrap: true)),
            string.IsNullOrWhiteSpace(i.Str("body")) ? null : U.T((i.Str("body") ?? "") is var b2 && b2.Length > 220 ? b2[..220] + "…" : b2, 13, null, Theme.Mu, wrap: true),
            U.T($"{S.NameOf(i.Long("authorId") ?? 0)} · {Format.When(i.Long("createdAt") ?? 0)} · 💬 {i.Long("comments") ?? 0}", 11.5, null, Theme.Mu2));
        var g = new Grid { ColumnSpacing = 14 }.Cols("auto", "*").Add(vote.At(0, 0), body.At(0, 1));
        var card = new Border { Child = g, Background = U.W(.04), CornerRadius = new CornerRadius(14), Padding = U.Th(14, 12), BorderBrush = U.W(.07), BorderThickness = U.Th(1) };
        body.Tapped += (_, _) => Open(id);
        card.PointerEntered += (_, _) => card.Background = U.W(.07);
        card.PointerExited += (_, _) => card.Background = U.W(.04);
        return card;
    }

    async void Open(long id)
    {
        JsonElement r;
        try { r = await Api.GetJson($"ideas/{id}"); AddUsers(r.Prop("users")); }
        catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); return; }
        var i = r.Prop("idea");
        var own = (i.Long("authorId") ?? 0) == S.Me.Id;
        var mod = r.Bool("canModerate") == true;
        var panel = new StackPanel { Spacing = 10, Width = 520 };
        panel.Add(U.H(8, StatusChip(i.Str("status") ?? "new"), U.T($"▲ {i.Long("votes")}", 13, U.Bold, Theme.Accent)));
        panel.Add(U.T(i.Str("title") ?? "", 18, U.Bold, wrap: true));
        panel.Add(U.T($"{S.NameOf(i.Long("authorId") ?? 0)} · {Format.When(i.Long("createdAt") ?? 0)}", 12, null, Theme.Mu));
        if (!string.IsNullOrWhiteSpace(i.Str("body"))) panel.Add(RichText.Build(i.Str("body")!, new RichText.Ctx(S, Api, S.ServerOfChannel(_cid), null, 13.5)));
        ContentDialog? d = null;
        Func<Task>? after = null; // akcja z kolejnym okienkiem — dopiero po zamknięciu tego
        if (mod)
        {
            var st = new ComboBox { Header = U.T("Status", 12.5, U.Semi, Theme.Mu) };
            foreach (var (k, v) in Status) st.Items.Add(v.Label);
            st.SelectedIndex = Status.Keys.ToList().IndexOf(i.Str("status") ?? "new");
            st.SelectionChanged += async (_, _) =>
            {
                try { await Api.PatchJson($"ideas/{id}", new { status = Status.Keys.ElementAt(st.SelectedIndex) }); _ = Load(); } catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
            };
            panel.Add(st);
        }
        if (own || mod)
            panel.Add(U.H(8,
                U.Ghost("Edytuj", () => { after = () => { Edit(i); return Task.CompletedTask; }; d?.Hide(); }, 30),
                U.Ghost("Usuń", () =>
                {
                    after = async () =>
                    {
                        if (!await M.Confirm(_sh, "Usunąć pomysł?", "Zniknie razem z głosami i komentarzami.", "Usuń")) return;
                        try { await Api.DeleteJson($"ideas/{id}"); _ = Load(); } catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
                    };
                    d?.Hide();
                }, 30)));
        panel.Add(U.HLine(.1));
        var comments = r.Prop("comments").EnumerateArray().ToList();
        panel.Add(U.T($"KOMENTARZE — {comments.Count}", 11, U.Bold, Theme.Mu2));
        foreach (var k in comments)
            panel.Add(new Grid { ColumnSpacing = 10 }.Cols("auto", "*").Add(new Avatar(28).Set(S, Api, k.Long("authorId") ?? 0).With(a => a.VerticalAlignment = VerticalAlignment.Top).At(0, 0),
                U.V(1, U.H(6, U.T(S.NameOf(k.Long("authorId") ?? 0), 13, U.Bold), U.T(Format.When(k.Long("createdAt") ?? 0), 11, null, Theme.Mu2)), U.T(k.Str("content") ?? "", 13, null, Theme.Fg, wrap: true)).At(0, 1)));
        var box = new TextBox { PlaceholderText = "Napisz komentarz…", MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        if (r.Bool("canComment") == true) panel.Add(box);
        d = new ContentDialog
        {
            Title = "Pomysł", Content = new ScrollViewer { Content = panel, MaxHeight = 560 }, CloseButtonText = "Zamknij",
            PrimaryButtonText = r.Bool("canComment") == true ? "Wyślij komentarz" : "", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        var res = await d.ShowAsync();
        if (after is not null) { await after(); return; }
        if (res == ContentDialogResult.Primary && box.Text.Trim().Length > 0)
        {
            try { await Api.PostJson($"ideas/{id}/comments", new { content = box.Text.Trim() }); Open(id); }
            catch (Exception ex) { _sh.Info(GibCordClient.Friendly(ex), true); }
        }
    }

    async void Edit(JsonElement? i)
    {
        if (i is null && !_canPost) { _sh.Info("Nie możesz dodawać pomysłów na tym kanale.", true); return; }
        var title = new TextBox { Text = i?.Str("title") ?? "", PlaceholderText = "Krótko: co dodać albo zmienić?", MaxLength = 120 };
        var body = new TextBox { Text = i?.Str("body") ?? "", PlaceholderText = "Szczegóły: po co, jak to widzisz… (opcjonalnie)", MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140 };
        var d = new ContentDialog
        {
            Title = i is null ? "💡 Nowy pomysł" : "Edytuj pomysł", Content = U.V(8, title, body).With(p => p.Width = 460),
            PrimaryButtonText = i is null ? "Dodaj" : "Zapisz", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (i is { } ex) await Api.PatchJson($"ideas/{ex.Long("id")}", new { title = title.Text.Trim(), body = body.Text.Trim() });
            else await Api.PostJson($"channels/{_cid}/ideas", new { title = title.Text.Trim(), body = body.Text.Trim() });
            _ = Load();
        }
        catch (Exception e) { _sh.Info(GibCordClient.Friendly(e), true); }
    }
}
