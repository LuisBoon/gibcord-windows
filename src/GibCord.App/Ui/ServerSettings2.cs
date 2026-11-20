using System.Text.Json;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

public sealed partial class ServerSettingsPage
{
    /// <summary>Uprawnienia w grupach (jak na stronie): bit, nazwa, opis.</summary>
    static readonly (string Group, (long Bit, string Name, string Desc)[] Items)[] PermGroups =
    [
        ("Ogólne", [(Perm.ViewChannel, "Widzenie kanałów", "widzi kanały i czyta wiadomości"), (Perm.CreateInvite, "Zapraszanie", "tworzy linki z zaproszeniem"),
            (Perm.ManageChannels, "Zarządzanie kanałami", "tworzy, edytuje i usuwa kanały"), (Perm.ManageRoles, "Zarządzanie rolami", "tworzy role i nadaje te niżej od swojej"),
            (Perm.ManageExpressions, "Emoji, naklejki i dźwięki", "wgrywa i usuwa"), (Perm.ManageEvents, "Zarządzanie wydarzeniami", "edytuje i usuwa cudze wydarzenia"),
            (Perm.ViewAuditLog, "Dziennik zmian", "widzi, kto co zmienił na serwerze"), (Perm.ManageServer, "Zarządzanie serwerem", "nazwa, ikona, ustawienia")]),
        ("Członkowie", [(Perm.ManageNicknames, "Zmiana nicków", "zmienia nicki innych"), (Perm.Timeout, "Wyciszanie na czas", "blokuje pisanie i mówienie na czas"), (Perm.Kick, "Wyrzucanie", ""), (Perm.Ban, "Banowanie", "")]),
        ("Wiadomości", [(Perm.SendMessages, "Wysyłanie wiadomości", ""), (Perm.AddReactions, "Reakcje", ""), (Perm.AttachFiles, "Pliki i zdjęcia", "też wiadomości głosowe"),
            (Perm.MentionEveryone, "@wszyscy i wszystkie role", "oznacza wszystkich i role nieoznaczalne"), (Perm.ManageMessages, "Zarządzanie wiadomościami", "usuwa cudze, przypina, kończy ankiety")]),
        ("Głos", [(Perm.Connect, "Łączenie z kanałem", ""), (Perm.Speak, "Mówienie", ""), (Perm.Stream, "Transmisje i kamera", ""), (Perm.UseSoundboard, "Soundboard", ""), (Perm.VoiceModerate, "Moderacja głosu", "wycisza i rozłącza innych")]),
        ("Zaawansowane", [(Perm.Admin, "Administrator", "wszystkie uprawnienia naraz — dawaj tylko zaufanym")]),
    ];

    /// <summary>Bity, które wolno nadpisywać na kanale (API 14.2).</summary>
    static readonly (long Bit, string Name, bool Text, bool Voice)[] ChannelPerms =
    [
        (Perm.ViewChannel, "Widzenie kanału", true, true), (Perm.SendMessages, "Wysyłanie wiadomości", true, false), (Perm.AddReactions, "Reakcje", true, false),
        (Perm.AttachFiles, "Pliki i zdjęcia", true, false), (Perm.MentionEveryone, "@wszyscy i role", true, false), (Perm.ManageMessages, "Zarządzanie wiadomościami", true, false),
        (Perm.Connect, "Łączenie", false, true), (Perm.Speak, "Mówienie", false, true), (Perm.Stream, "Transmisje i kamera", false, true), (Perm.UseSoundboard, "Soundboard", false, true),
        (Perm.VoiceModerate, "Moderacja głosu", false, true), (Perm.ManageChannels, "Zarządzanie kanałem", true, true), (Perm.ManageRoles, "Zarządzanie uprawnieniami", true, true),
        (Perm.CreateInvite, "Zapraszanie", true, true),
    ];

    static readonly string[] RoleColors = ["#e8708f", "#ff8a65", "#f2c45a", "#9be15d", "#6fe3a5", "#4fd1c5", "#7cc7ff", "#b69cff", "#f78fb3", "#ffb98f", "#c7b3ff", "#8b8599"];

    long? _role; // wybrana rola (0 = @wszyscy)

    int MyTop()
    {
        var srv = Srv!;
        if (srv.OwnerId == S.Me.Id || (srv.MyPerms?.Server & Perm.Admin) != 0) return int.MaxValue;
        return S.RolesOf(srv, S.Me.Id).Select(r => r.Position).DefaultIfEmpty(0).Max();
    }

    // ───────────────────────── Role ─────────────────────────

    void Roles()
    {
        var srv = Srv!;
        Title("Role", "Wyżej = ważniejsza. Rolami niżej od swojej najwyższej możesz zarządzać.");
        var top = MyTop();
        var roles = srv.Roles.OrderByDescending(r => r.Position).ToList();
        var list = new StackPanel { Spacing = 3, Width = 250 };
        list.Add(U.Primary("+ Nowa rola", async () =>
        {
            var name = new TextBox { PlaceholderText = "np. Gracze", MaxLength = 100 };
            if (await Dialog("Nowa rola", name, "Utwórz").ShowAsync() != ContentDialogResult.Primary || name.Text.Trim().Length == 0) return;
            try
            {
                var r = await Api.PostJson($"servers/{_sid}/roles", new { name = name.Text.Trim(), color = RoleColors[Random.Shared.Next(RoleColors.Length)] });
                _role = r.Long("id");
                _sh.Info("Rola utworzona");
            }
            catch (Exception ex) { Err(ex); }
        }, 36));
        foreach (var r in roles)
        {
            var rid = r.Id;
            var locked = r.Position >= top;
            var n = srv.Members.Count(m => m.RoleIds.Contains(r.Id));
            var row = U.Row(new Grid { Height = 34 }.Cols("auto", "*", "auto").Add(
                new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 12, Height = 12, Fill = U.B(r.Color ?? "#8b8599"), Margin = U.Th(0, 0, 10, 0) }.At(0, 0),
                U.H(6, string.IsNullOrEmpty(r.Icon) || r.Icon!.StartsWith('/') ? null : U.T(r.Icon!, 13), U.T(r.Name, 13.5, U.Semi), locked ? U.I("", 11, Theme.Mu2) : null).At(0, 1),
                U.T(n.ToString(), 11.5, null, Theme.Mu2).At(0, 2)), () => { _role = rid; DrawBody(keepScroll: true); }, _role == rid, 8, U.Th(10, 0), U.Th(0, 1));
            list.Add(row);
        }
        list.Add(U.Row(new Grid { Height = 34 }.Cols("auto", "*", "auto").Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 12, Height = 12, Fill = Theme.Gray, Margin = U.Th(0, 0, 10, 0) }.At(0, 0),
            U.T("@wszyscy", 13.5, U.Semi).At(0, 1), U.T(srv.Members.Count.ToString(), 11.5, null, Theme.Mu2).At(0, 2)), () => { _role = 0; DrawBody(keepScroll: true); }, _role == 0, 8, U.Th(10, 0), U.Th(0, 1)));

        var editor = new StackPanel { Spacing = 12 };
        if (_role == 0) BaseRole(editor);
        else if (_role is long sel && srv.Roles.Find(r => r.Id == sel) is { } role) RoleEditor(editor, role, role.Position >= top);
        else
        {
            editor.Add(U.T("Wybierz rolę z listy, żeby ją edytować.", 14, null, Theme.Mu));
            // auto-role
            var autos = new StackPanel { Spacing = 2 };
            var picked = srv.AutoRoleIds.ToHashSet();
            foreach (var r in roles.Where(r => r.Position < top))
            {
                var rid = r.Id;
                var cb = new CheckBox { Content = r.Name, IsChecked = picked.Contains(rid) };
                cb.Checked += (_, _) => picked.Add(rid);
                cb.Unchecked += (_, _) => picked.Remove(rid);
                autos.Add(cb);
            }
            editor.Add(Card(Label("Role dla nowych osób"), U.T("Każdy, kto dołączy z zaproszenia, dostanie te role.", 12.5, null, Theme.Mu, wrap: true), autos,
                U.Ghost("Zapisz", async () => await Run(async () => ApplyServer(await Api.PatchJson($"servers/{_sid}", new { autoRoleIds = picked.ToList() })), "Zapisano role dla nowych osób"), 32)
                    .With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        }
        _body.MaxWidth = 1000;
        _body.Add(new Grid { ColumnSpacing = 20 }.Cols("auto", "*").Add(list.With(l => l.VerticalAlignment = VerticalAlignment.Top).At(0, 0), editor.At(0, 1)));
    }

    StackPanel PermToggles(long perms, bool locked, Func<long, bool>? allowed = null)
    {
        var p = new StackPanel { Spacing = 6 };
        foreach (var (group, items) in PermGroups)
        {
            p.Add(Label(group));
            foreach (var (bit, name, desc) in items)
            {
                var t = new ToggleSwitch { IsOn = (perms & bit) == bit, OnContent = "", OffContent = "", IsEnabled = !locked && (allowed?.Invoke(bit) ?? true), Tag = bit, MinWidth = 0 };
                p.Add(new Grid().Cols("*", "auto").Add(U.V(0, U.T(name, 13.5, U.Semi), string.IsNullOrEmpty(desc) ? null : U.T(desc, 11.5, null, Theme.Mu2, wrap: true)).At(0, 0), t.At(0, 1)));
            }
        }
        return p;
    }

    static long ReadPerms(StackPanel p, long keep)
    {
        var v = keep;
        foreach (var t in p.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<ToggleSwitch>()))
        {
            var bit = (long)t.Tag;
            v = t.IsOn ? v | bit : v & ~bit;
        }
        return v;
    }

    void BaseRole(StackPanel ed)
    {
        var srv = Srv!;
        var perms = PermToggles(srv.BasePermissions, false, bit => bit != Perm.Admin);
        ed.Add(Card(U.T("@wszyscy", 18, U.Bold), U.T("Uprawnienia, które ma każdy na serwerze (role tylko dokładają).", 12.5, null, Theme.Mu, wrap: true), perms,
            U.Primary("Zapisz uprawnienia", async () => await Run(async () => ApplyServer(await Api.PatchJson($"servers/{_sid}", new { basePermissions = ReadPerms(perms, srv.BasePermissions) })), "Zapisano"), 38)
                .With(b => { b.Width = 220; b.HorizontalAlignment = HorizontalAlignment.Left; })));
    }

    void RoleEditor(StackPanel ed, Role r, bool locked)
    {
        var srv = Srv!;
        var name = new TextBox { Text = r.Name, MaxLength = 100, IsEnabled = !locked };
        var color = r.Color;
        var colorBox = new ContentControl();
        var hex = new TextBox { Text = r.Color ?? "", PlaceholderText = "#hex", Width = 110, IsEnabled = !locked };
        void DrawColors()
        {
            var w = new WrapPanel { Spacing = 6 };
            var none = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderBrush = color is null ? Theme.Fg : U.W(.25), BorderThickness = U.Th(2), Child = U.T("–", 12, null, Theme.Mu).With(t => t.HorizontalAlignment = HorizontalAlignment.Center) };
            none.Tip("Bez koloru");
            none.Tapped += (_, _) => { if (locked) return; color = null; hex.Text = ""; DrawColors(); };
            w.Children.Add(none);
            foreach (var c in RoleColors)
            {
                var b = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = U.B(c), BorderBrush = string.Equals(color, c, StringComparison.OrdinalIgnoreCase) ? Theme.Fg : Theme.Transparent, BorderThickness = U.Th(3) };
                var col = c;
                b.Tapped += (_, _) => { if (locked) return; color = col; hex.Text = col; DrawColors(); };
                w.Children.Add(b);
            }
            colorBox.Content = w;
        }
        DrawColors();
        hex.TextChanged += (_, _) => { if (System.Text.RegularExpressions.Regex.IsMatch(hex.Text.Trim(), "^#[0-9a-fA-F]{6}$")) { color = hex.Text.Trim(); DrawColors(); } };
        var icon = new TextBox { Text = r.Icon is { } ic && !ic.StartsWith('/') ? ic : "", PlaceholderText = "emoji, np. 🎮", MaxLength = 8, Width = 120, IsEnabled = !locked };
        var hoist = Toggle("Osobna grupa na liście osób", r.Hoist);
        var mention = Toggle("Każdy może ją oznaczyć (@rola)", r.Mentionable);
        var self = Toggle("Członkowie mogą ją sobie wziąć sami", r.SelfAssignable);
        var group = new TextBox { Text = r.Group ?? "", PlaceholderText = "Grupa w wyborze ról, np. Gry", MaxLength = 32 };
        var desc = new TextBox { Text = r.Description ?? "", PlaceholderText = "Opis (widać w wyborze ról)", MaxLength = 100 };
        foreach (var t in new Control[] { hoist, mention, self, group, desc }) t.IsEnabled = !locked;
        var perms = PermToggles(r.Permissions, locked);

        var head = new Grid().Cols("*", "auto").Add(U.H(10, new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 16, Height = 16, Fill = U.B(r.Color ?? "#8b8599") }, U.T(r.Name, 18, U.Bold)).At(0, 0));
        if (!locked)
            head.Add(U.H(4,
                U.IconButton("", "Wyżej", () => _ = Run(() => Api.PatchJson($"roles/{r.Id}", new { position = r.Position + 1 })), 12, 30),
                U.IconButton("", "Niżej", () => _ = Run(() => Api.PatchJson($"roles/{r.Id}", new { position = Math.Max(1, r.Position - 1) })), 12, 30),
                U.IconButton("", "Usuń rolę", async () =>
                {
                    if (!await M.Confirm(_sh, $"Usunąć rolę {r.Name}?", "Osoby stracą tę rolę i jej uprawnienia.", "Usuń")) return;
                    if (await Run(() => Api.DeleteJson($"roles/{r.Id}"), "Rola usunięta")) { _role = null; DrawBody(); }
                }, 12, 30)).At(0, 1));
        ed.Add(head);
        if (locked) ed.Add(U.T("Ta rola jest na równi albo wyżej niż Twoja najwyższa — nie możesz jej zmieniać.", 12.5, null, Theme.Yellow, wrap: true));
        ed.Add(Card(Label("Nazwa"), name, Label("Kolor"), colorBox, hex, Label("Ikona"), U.H(8, icon,
            locked ? null : U.Ghost("Wgraj obrazek", async () =>
            {
                if (await PickFile(".png", ".gif", ".webp", ".jpg", ".jpeg") is not { } f) return;
                using var d = f.Data;
                await Run(() => Api.PostImage($"roles/{r.Id}/icon", d, f.Mime), "Ikona roli zmieniona");
            }, 32),
            !locked && r.Icon is { } ri && ri.StartsWith('/') ? U.Ghost("Usuń obrazek", () => _ = Run(() => Api.DeleteJson($"roles/{r.Id}/icon"), "Ikona usunięta"), 32) : null),
            hoist, mention, self, group, desc));
        ed.Add(Card(Label("Uprawnienia"), perms));
        if (!locked)
            ed.Add(U.Primary("Zapisz rolę", async () =>
            {
                var body = new Dictionary<string, object?>
                {
                    ["name"] = name.Text.Trim(), ["color"] = color, ["icon"] = icon.Text.Trim().Length > 0 ? icon.Text.Trim() : (r.Icon?.StartsWith('/') == true ? r.Icon : null),
                    ["hoist"] = hoist.IsOn, ["mentionable"] = mention.IsOn, ["selfAssignable"] = self.IsOn, ["group"] = group.Text.Trim(), ["description"] = desc.Text.Trim(),
                    ["permissions"] = ReadPerms(perms, r.Permissions),
                };
                if (body["icon"] is string s2 && s2.StartsWith('/')) body.Remove("icon");
                await Run(() => Api.PatchJson($"roles/{r.Id}", body), "Rola zapisana");
            }, 40).With(b => { b.Width = 200; b.HorizontalAlignment = HorizontalAlignment.Left; }));

        // osoby z rolą
        var people = new StackPanel { Spacing = 2 };
        foreach (var m in srv.Members.Where(m => m.RoleIds.Contains(r.Id)).Take(100))
        {
            var uid = m.UserId;
            var g = new Grid().Cols("auto", "*", "auto").Add(new Avatar(26).Set(S, Api, uid, srv).At(0, 0), U.T(S.NameOf(uid, srv), 13, U.Semi).With(t => t.Margin = U.Th(10, 0)).At(0, 1));
            if (!locked) g.Add(U.IconButton("", "Zabierz rolę", () => _ = Run(() => Api.PutJson($"servers/{_sid}/roles/{r.Id}/members", new { remove = new[] { uid } })), 10, 26).At(0, 2));
            people.Add(g);
        }
        if (people.Children.Count == 0) people.Add(U.T("Nikt nie ma tej roli.", 12.5, null, Theme.Mu));
        ed.Add(Card(Label($"Osoby z tą rolą — {srv.Members.Count(m => m.RoleIds.Contains(r.Id))}"), people,
            locked ? null : U.Ghost("+ Dodaj osoby", async () =>
            {
                var have = srv.Members.Where(m => m.RoleIds.Contains(r.Id)).Select(m => m.UserId).ToList();
                var ids = await People.PickMembers(_sh, srv, "Nadaj rolę " + r.Name, have);
                if (ids is { Count: > 0 }) await Run(() => Api.PutJson($"servers/{_sid}/roles/{r.Id}/members", new { add = ids }), "Rola nadana");
            }, 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
    }

    // ───────────────────────── Uprawnienia kanału / kategorii ─────────────────────────

    async void Overrides(Channel? ch, Category? cat)
    {
        var srv = Srv!;
        var list = (ch?.Overrides ?? cat?.Overrides ?? []).Select(o => new PermOverride { Type = o.Type, Id = o.Id, Allow = o.Allow, Deny = o.Deny }).ToList();
        if (!list.Any(o => o.Type == "everyone")) list.Insert(0, new PermOverride { Type = "everyone", Id = 0 });
        var voice = ch?.IsVoice == true;
        var bits = ChannelPerms.Where(p => ch is null || (voice ? p.Voice : p.Text)).ToList();
        var targets = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var editor = new StackPanel { Spacing = 6 };
        var changed = new HashSet<(string, long)>();
        string Label2(PermOverride o) => o.Type switch { "everyone" => "@wszyscy", "role" => "Rola: " + (srv.Roles.Find(r => r.Id == o.Id)?.Name ?? "?"), _ => "Osoba: " + S.NameOf(o.Id, srv) };
        void DrawTargets(int sel)
        {
            targets.Items.Clear();
            foreach (var o in list) targets.Items.Add(Label2(o));
            targets.SelectedIndex = Math.Clamp(sel, 0, list.Count - 1);
        }
        void DrawEditor()
        {
            editor.Children.Clear();
            if (targets.SelectedIndex < 0 || targets.SelectedIndex >= list.Count) return;
            var o = list[targets.SelectedIndex];
            foreach (var (bit, name, _, _) in bits)
            {
                var state = (o.Allow & bit) != 0 ? 1 : (o.Deny & bit) != 0 ? -1 : 0;
                var seg = U.H(2);
                void Opt(int v, string label, Brush on)
                {
                    var b = new Button { Content = U.T(label, 12, U.Bold, state == v ? U.B("#ffffff") : Theme.Mu), Background = state == v ? on : U.W(.06), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(6), Padding = U.Th(8, 2), Height = 26 };
                    b.Click += (_, _) =>
                    {
                        o.Allow &= ~bit; o.Deny &= ~bit;
                        if (v == 1) o.Allow |= bit; else if (v == -1) o.Deny |= bit;
                        changed.Add((o.Type, o.Id));
                        DrawEditor();
                    };
                    seg.Add(b);
                }
                Opt(-1, "✕", Theme.Red2);
                Opt(0, "/", U.W(.25));
                Opt(1, "✓", U.B("#2fa66a"));
                editor.Add(new Grid().Cols("*", "auto").Add(U.T(name, 13, U.Semi).At(0, 0), seg.At(0, 1)));
            }
        }
        targets.SelectionChanged += (_, _) => DrawEditor();
        DrawTargets(0);
        DrawEditor();
        var add = new ComboBox { PlaceholderText = "+ Dodaj rolę albo osobę", HorizontalAlignment = HorizontalAlignment.Stretch };
        var addable = srv.Roles.Where(r => !list.Any(o => o.Type == "role" && o.Id == r.Id)).OrderByDescending(r => r.Position).Select(r => ("role", r.Id, "Rola: " + r.Name))
            .Concat(srv.Members.Where(m => !list.Any(o => o.Type == "member" && o.Id == m.UserId)).Take(200).Select(m => ("member", m.UserId, "Osoba: " + S.NameOf(m.UserId, srv)))).ToList();
        foreach (var a in addable) add.Items.Add(a.Item3);
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedIndex < 0) return;
            var (t, id, _) = addable[add.SelectedIndex];
            list.Add(new PermOverride { Type = t, Id = id });
            DrawTargets(list.Count - 1);
        };
        var info = U.T("✓ zezwól · / jak w rolach · ✕ zabroń. Osoba wygrywa z rolami, rola z @wszyscy.", 11.5, null, Theme.Mu2, wrap: true);
        var content = U.V(10, info, targets, add, editor).With(p => p.Width = 440);
        if (ch is { CategoryId: not null })
            content.Add(U.Ghost("Skopiuj uprawnienia z kategorii", async () => { await Run(() => Api.PostJson($"channels/{ch.Id}/overrides/sync"), "Skopiowano z kategorii"); }, 32));
        var d = Dialog(ch is not null ? $"Uprawnienia: {(voice ? "🔊 " : "#")}{ch.Name}" : $"Uprawnienia kategorii {cat!.Name}", content, "Zapisz");
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        var path = ch is not null ? $"channels/{ch.Id}" : $"categories/{cat!.Id}";
        foreach (var o in list.Where(o => changed.Contains((o.Type, o.Id))))
            if (!await Run(() => Api.PutJson($"{path}/overrides/{o.Type}/{o.Id}", new { allow = o.Allow, deny = o.Deny }))) return;
        if (changed.Count > 0) _sh.Info("Uprawnienia zapisane");
    }

    // ───────────────────────── Emoji, naklejki, dźwięki ─────────────────────────

    string _exprTab = "emoji";

    void Expressions()
    {
        var srv = Srv!;
        Title("Emoji, naklejki i dźwięki");
        var tabs = U.H(6);
        foreach (var (key, label) in new[] { ("emoji", $"Emoji · {srv.Emojis.Count}/100"), ("stickers", $"Naklejki · {srv.Stickers.Count}/30"), ("sounds", $"Dźwięki · {srv.Sounds.Count}/50") })
        {
            var on = _exprTab == key;
            var b = new Button { Content = U.T(label, 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(12, 4) };
            b.Click += (_, _) => { _exprTab = key; DrawBody(); };
            tabs.Add(b);
        }
        _body.Add(tabs);
        var name = new TextBox { PlaceholderText = _exprTab == "emoji" ? "nazwa: małe litery, cyfry i _ (np. kotek)" : "nazwa", MaxLength = 32, Width = 260 };
        var emo = new TextBox { PlaceholderText = "emoji, np. 📯", MaxLength = 8, Width = 110 };
        var vol = new Slider { Minimum = 10, Maximum = 100, Value = 100, Width = 160, Header = "Głośność" };
        var (exts, hint) = _exprTab switch
        {
            "stickers" => (new[] { ".png", ".gif", ".webp" }, "PNG, GIF albo WebP do 2 MB"),
            "sounds" => (new[] { ".mp3", ".ogg", ".wav", ".webm" }, "MP3, OGG, WAV albo WebM do 1 MB i 6 sekund"),
            _ => (new[] { ".png", ".gif", ".webp", ".jpg", ".jpeg" }, "PNG, GIF, WebP albo JPG do 512 KB"),
        };
        var upload = U.Primary("Wybierz plik i dodaj", async () =>
        {
            if (await PickFile(exts) is not { } f) return;
            using var d = f.Data;
            var n = name.Text.Trim().Length > 0 ? name.Text.Trim() : f.Name;
            if (_exprTab == "emoji") n = new string(n.ToLowerInvariant().Replace('ł', 'l').Normalize(System.Text.NormalizationForm.FormD).Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
            var q = "?name=" + Uri.EscapeDataString(n);
            if (_exprTab == "sounds") q += "&emoji=" + Uri.EscapeDataString(emo.Text.Trim()) + "&volume=" + (vol.Value / 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (await Run(() => Api.PostImage($"servers/{_sid}/{_exprTab}{q}", d, f.Mime), "Dodano")) name.Text = "";
        }, 36).With(b => b.Width = 220);
        var form = U.H(10, name, _exprTab == "sounds" ? emo : null, _exprTab == "sounds" ? vol : null, upload);
        _body.Add(Card(Label("Dodaj"), form, U.T(hint, 11.5, null, Theme.Mu2)));
        var grid = new WrapPanel { Spacing = 10 };
        FrameworkElement Item(UIElement preview, string label, string path, string current)
        {
            var rename = U.IconButton("", "Zmień nazwę", async () =>
            {
                var box = new TextBox { Text = current, MaxLength = 32 };
                if (await Dialog("Nowa nazwa", box, "Zapisz").ShowAsync() == ContentDialogResult.Primary) await Run(() => Api.PatchJson(path, new { name = box.Text.Trim() }), "Zapisano");
            }, 11, 26);
            var del = U.IconButton("", "Usuń", async () => { if (await M.Confirm(_sh, "Usunąć?", $"„{current}” zniknie z serwera.", "Usuń")) await Run(() => Api.DeleteJson(path), "Usunięto"); }, 11, 26);
            return new Border
            {
                Width = 150, Background = U.W(.045), CornerRadius = new CornerRadius(12), Padding = U.Th(10),
                Child = U.V(6, preview, U.T(label, 12.5, U.Semi).With(t => t.HorizontalAlignment = HorizontalAlignment.Center), U.H(2, rename, del).With(p => p.HorizontalAlignment = HorizontalAlignment.Center)),
            };
        }
        switch (_exprTab)
        {
            case "stickers":
                foreach (var st in srv.Stickers) grid.Children.Add(Item(new Image { Source = Images.Get(Api.Abs(st.Url), 200), Width = 96, Height = 96 }, st.Name, $"stickers/{st.Id}", st.Name));
                break;
            case "sounds":
                foreach (var so in srv.Sounds)
                {
                    var url = Api.Abs(so.Url);
                    var play = U.Ghost("▶ Odsłuchaj", () => Sfx.Play(url, so.Volume, _sh.C.Voice.Options.OutputDevice), 28);
                    grid.Children.Add(Item(U.V(4, U.T(string.IsNullOrEmpty(so.Emoji) ? "🔊" : so.Emoji!, 30).With(t => t.HorizontalAlignment = HorizontalAlignment.Center), play), so.Name, $"sounds/{so.Id}", so.Name));
                }
                break;
            default:
                foreach (var e in srv.Emojis) grid.Children.Add(Item(new Image { Source = Images.Get(Api.Abs(e.Url), 96), Width = 48, Height = 48 }, ":" + e.Name + ":", $"emojis/{e.Id}", e.Name));
                break;
        }
        if (grid.Children.Count == 0) _body.Add(U.T("Jeszcze nic tu nie ma.", 13.5, null, Theme.Mu));
        else _body.Add(grid);
    }

    // ───────────────────────── Automoderacja ─────────────────────────

    void Automod()
    {
        Title("Automoderacja", "Zatrzymuje spam, oszustwa i wulgaryzmy, zanim ktoś je zobaczy. Właściciel i administratorzy nie są sprawdzani.");
        var host = new StackPanel { Spacing = 12 };
        host.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
        _body.Add(host);
        _ = FillAutomod(host);
    }

    async Task FillAutomod(StackPanel host)
    {
        var srv = Srv!;
        JsonElement cfg;
        try { cfg = await Api.GetJson($"servers/{_sid}/automod"); }
        catch (Exception ex) { host.Children.Clear(); host.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); return; }
        host.Children.Clear();
        var canEdit = Can(Perm.ManageServer);
        var level = new RadioButtons();
        foreach (var t in new[] { "Wyłączona", "Łagodna — spam i Twoje słowa", "Średnia — do tego oszustwa i linki od nowych osób", "Ostra — do tego wulgaryzmy i KRZYK" }) level.Items.Add(t);
        level.SelectedIndex = (int)(cfg.Long("level") ?? 0);
        level.IsEnabled = canEdit;
        var words = new TextBox
        {
            Text = cfg.Prop("words").ValueKind == JsonValueKind.Array ? string.Join("\n", cfg.Prop("words").EnumerateArray().Select(w => w.GetString())) : "",
            PlaceholderText = "jedno słowo albo fraza w linii; * na końcu = wszystkie końcówki (np. głup*)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, IsEnabled = canEdit,
        };
        var exRoles = cfg.Prop("exemptRoles").ValueKind == JsonValueKind.Array ? cfg.Prop("exemptRoles").EnumerateArray().Select(x => x.GetInt64()).ToHashSet() : [];
        var exChans = cfg.Prop("exemptChannels").ValueKind == JsonValueKind.Array ? cfg.Prop("exemptChannels").EnumerateArray().Select(x => x.GetInt64()).ToHashSet() : [];
        var roles = new WrapPanel { Spacing = 8 };
        foreach (var r in srv.Roles.OrderByDescending(r => r.Position))
        {
            var rid = r.Id;
            var cb = new CheckBox { Content = r.Name, IsChecked = exRoles.Contains(rid), IsEnabled = canEdit, MinWidth = 0 };
            cb.Checked += (_, _) => exRoles.Add(rid); cb.Unchecked += (_, _) => exRoles.Remove(rid);
            roles.Children.Add(cb);
        }
        var texts = srv.Channels.Where(c => !c.IsVoice).OrderBy(c => c.Position).ToList();
        var chans = new WrapPanel { Spacing = 8 };
        foreach (var c in texts)
        {
            var cid = c.Id;
            var cb = new CheckBox { Content = "#" + c.Name, IsChecked = exChans.Contains(cid), IsEnabled = canEdit, MinWidth = 0 };
            cb.Checked += (_, _) => exChans.Add(cid); cb.Unchecked += (_, _) => exChans.Remove(cid);
            chans.Children.Add(cb);
        }
        var log = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = canEdit };
        log.Items.Add("Bez kanału (tylko zakładka Zgłoszenia)");
        foreach (var c in texts) log.Items.Add("# " + c.Name);
        log.SelectedIndex = cfg.Long("logChannelId") is long lc && texts.FindIndex(c => c.Id == lc) is var ix and >= 0 ? ix + 1 : 0;
        host.Add(Card(Label("Poziom"), level), Card(Label("Własne zakazane słowa"), words), Card(Label("Bez sprawdzania: role"), roles), Card(Label("Bez sprawdzania: kanały"), chans),
            Card(Label("Kanał z powiadomieniami dla moderatorów"), log));
        if (canEdit)
            host.Add(U.Primary("Zapisz automoderację", async () =>
            {
                await Run(() => Api.PatchJson($"servers/{_sid}/automod", new Dictionary<string, object?>
                {
                    ["level"] = level.SelectedIndex, ["words"] = words.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    ["exemptRoles"] = exRoles.ToList(), ["exemptChannels"] = exChans.ToList(), ["logChannelId"] = log.SelectedIndex > 0 ? texts[log.SelectedIndex - 1].Id : null,
                }), "Zapisano automoderację");
            }, 40).With(b => { b.Width = 240; b.HorizontalAlignment = HorizontalAlignment.Left; }));
        else host.Add(U.T("Zmieniać może osoba z uprawnieniem „Zarządzanie serwerem”.", 12.5, null, Theme.Mu2));
    }

    // ───────────────────────── Zgłoszenia ─────────────────────────

    string _repStatus = "open";

    void Reports()
    {
        Title("Zgłoszenia", "Wiadomości zgłoszone przez członków i zatrzymane przez automoderację.");
        var tabs = U.H(6);
        foreach (var (key, label) in new[] { ("open", "Otwarte"), ("resolved", "Załatwione"), ("dismissed", "Odrzucone") })
        {
            var on = _repStatus == key;
            var b = new Button { Content = U.T(label, 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(12, 4) };
            b.Click += (_, _) => { _repStatus = key; DrawBody(); };
            tabs.Add(b);
        }
        _body.Add(tabs);
        var list = new StackPanel { Spacing = 8 };
        list.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
        _body.Add(list);
        _ = FillReports(list);
    }

    static readonly Dictionary<string, string> Reasons = new()
    {
        ["spam"] = "Spam lub reklama", ["harassment"] = "Nękanie", ["nsfw"] = "Treści 18+", ["scam"] = "Oszustwo", ["other"] = "Coś innego", ["automod"] = "Automoderacja",
    };

    async Task FillReports(StackPanel list)
    {
        var srv = Srv!;
        try
        {
            var r = await Api.GetJson($"servers/{_sid}/reports?status={_repStatus}");
            if (r.Prop("users").ValueKind == JsonValueKind.Object)
                foreach (var p in r.Prop("users").EnumerateObject()) if (p.Value.To<User>() is { } u && !S.Users.ContainsKey(u.Id)) S.Users[u.Id] = u;
            list.Children.Clear();
            foreach (var rep in r.Prop("reports").EnumerateArray())
            {
                var id = rep.Long("id") ?? 0;
                var sn = rep.Prop("snapshot");
                var author = sn.Long("authorId") ?? 0;
                var reason = rep.Str("reason") ?? "other";
                var mid = rep.Long("messageId") ?? 0;
                var cid = rep.Long("channelId") ?? 0;
                var head = U.H(8, U.Box(U.T(Reasons.GetValueOrDefault(reason, reason), 10.5, U.Bold, Theme.OnAccent), reason == "automod" ? Theme.Teal : Theme.Yellow, 6, U.Th(6, 1)),
                    U.T("#" + (sn.Str("channelName") ?? "?") + " · " + Format.When(rep.Long("createdAt") ?? 0), 12, null, Theme.Mu));
                var body = U.V(6, head,
                    U.H(8, new Avatar(24).Set(S, Api, author, srv), U.T(S.NameOf(author, srv), 13.5, U.Bold)),
                    U.T(string.IsNullOrWhiteSpace(sn.Str("content")) ? "(załącznik)" : sn.Str("content")!, 13, null, Theme.Fg, wrap: true),
                    rep.Str("note") is { Length: > 0 } note ? U.T("Notatka: " + note, 12, null, Theme.Mu, wrap: true) : null,
                    reason != "automod" && rep.Long("reporterId") is long rid and > 0 ? U.T("Zgłasza: " + S.NameOf(rid, srv), 11.5, null, Theme.Mu2) : null);
                var actions = U.H(8);
                if (_repStatus == "open")
                {
                    if (reason == "automod") actions.Add(U.Ghost("To pomyłka — przywróć", async () => { if (await Run(() => Api.PostJson($"reports/{id}/restore"), "Wiadomość przywrócona")) DrawBody(); }, 30));
                    actions.Add(U.Ghost("Załatwione", async () => { if (await Run(() => Api.PatchJson($"reports/{id}", new { status = "resolved" }), "Oznaczono jako załatwione")) DrawBody(); }, 30));
                    if (reason != "automod") actions.Add(U.Ghost("Odrzuć", async () => { if (await Run(() => Api.PatchJson($"reports/{id}", new { status = "dismissed" }), "Odrzucono")) DrawBody(); }, 30));
                }
                else actions.Add(U.Ghost("Otwórz ponownie", async () => { if (await Run(() => Api.PatchJson($"reports/{id}", new { status = "open" }))) DrawBody(); }, 30));
                if (mid > 0 && cid > 0) actions.Add(U.Ghost("Przejdź do wiadomości", () => { _sh.CloseServerSettings(); _sh.JumpToMessage(cid, mid); }, 30));
                if (author > 0 && author != S.Me.Id)
                {
                    var mod = U.IconButton("", "Moderuj autora", () => { }, 14, 30);
                    mod.Flyout = UserMenu.Build(_sh, author, srv);
                    actions.Add(mod);
                }
                list.Add(Card(body, actions));
            }
            if (list.Children.Count == 0) list.Add(U.T(_repStatus == "open" ? "Brak otwartych zgłoszeń. 🎉" : "Pusto.", 13.5, null, Theme.Mu));
        }
        catch (Exception ex) { list.Children.Clear(); list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    }

    // ───────────────────────── Dziennik zmian ─────────────────────────

    static readonly Dictionary<string, string> Actions = new()
    {
        ["server_update"] = "zmienia ustawienia serwera", ["server_icon"] = "zmienia ikonę serwera", ["server_transfer"] = "przekazuje serwer",
        ["role_create"] = "tworzy rolę", ["role_update"] = "zmienia rolę", ["role_position"] = "przesuwa rolę", ["role_delete"] = "usuwa rolę", ["role_icon"] = "zmienia ikonę roli",
        ["member_role_add"] = "nadaje rolę", ["member_role_remove"] = "zabiera rolę", ["member_kick"] = "wyrzuca", ["member_ban"] = "banuje", ["member_unban"] = "zdejmuje bana",
        ["member_timeout"] = "wycisza na czas", ["member_nick"] = "zmienia nick", ["voice_moderate"] = "moderuje głos",
        ["channel_create"] = "tworzy kanał", ["channel_update"] = "zmienia kanał", ["channel_delete"] = "usuwa kanał", ["channel_overrides"] = "zmienia uprawnienia kanału",
        ["channel_overrides_sync"] = "kopiuje uprawnienia z kategorii", ["category_create"] = "tworzy kategorię", ["category_update"] = "zmienia kategorię", ["category_delete"] = "usuwa kategorię",
        ["category_overrides"] = "zmienia uprawnienia kategorii", ["layout_update"] = "zmienia kolejność kanałów", ["invite_create"] = "tworzy zaproszenie",
        ["message_delete"] = "usuwa cudzą wiadomość", ["message_pin"] = "przypina wiadomość", ["message_unpin"] = "odpina wiadomość",
        ["event_create"] = "tworzy wydarzenie", ["event_update"] = "zmienia wydarzenie", ["event_cancel"] = "odwołuje wydarzenie", ["event_delete"] = "usuwa wydarzenie",
        ["emoji_create"] = "dodaje emoji", ["emoji_update"] = "zmienia emoji", ["emoji_delete"] = "usuwa emoji", ["sticker_create"] = "dodaje naklejkę", ["sticker_update"] = "zmienia naklejkę",
        ["sticker_delete"] = "usuwa naklejkę", ["sound_create"] = "dodaje dźwięk", ["sound_update"] = "zmienia dźwięk", ["sound_delete"] = "usuwa dźwięk", ["report_handle"] = "rozpatruje zgłoszenie",
        ["automod_update"] = "zmienia automoderację", ["thread_create"] = "zakłada wątek", ["thread_update"] = "zmienia wątek", ["thread_delete"] = "usuwa wątek",
    };

    void Audit()
    {
        Title("Dziennik zmian", "Kto co zmienił na serwerze (ostatnie 90 dni).");
        var list = new StackPanel { Spacing = 4 };
        list.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
        _body.Add(list);
        _ = FillAudit(list, null);
    }

    async Task FillAudit(StackPanel list, long? before)
    {
        var srv = Srv!;
        try
        {
            var r = await Api.GetJson($"servers/{_sid}/audit?limit=50" + (before is long b ? "&before=" + b : ""));
            if (r.Prop("users").ValueKind == JsonValueKind.Object)
                foreach (var p in r.Prop("users").EnumerateObject()) if (p.Value.To<User>() is { } u && !S.Users.ContainsKey(u.Id)) S.Users[u.Id] = u;
            if (before is null) list.Children.Clear();
            else if (list.Children.Count > 0 && list.Children[^1] is Button) list.Children.RemoveAt(list.Children.Count - 1);
            long last = 0;
            var n = 0;
            foreach (var e in r.Prop("entries").EnumerateArray())
            {
                n++;
                last = e.Long("id") ?? 0;
                var actor = e.Long("actorId");
                var act = e.Str("action") ?? "";
                var what = Actions.GetValueOrDefault(act, act.Replace('_', ' '));
                var target = Target(e, srv);
                var row = new Grid { Padding = U.Th(10, 8) }.Cols("auto", "*", "auto").Add(
                    (actor is long a ? new Avatar(28).Set(S, Api, a, srv) : (FrameworkElement)U.I("", 18, Theme.Mu)).At(0, 0),
                    U.V(1, new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Theme.Font, FontSize = 13.5, Foreground = Theme.Fg }.With(t =>
                    {
                        t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = actor is long a2 ? S.NameOf(a2, srv) : "System", FontWeight = U.Bold });
                        t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " " + what + (target.Length > 0 ? " " : "") });
                        if (target.Length > 0) t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = target, FontWeight = U.Semi });
                    }), Details(e) is { Length: > 0 } det ? U.T(det, 11.5, null, Theme.Mu2, wrap: true) : null).With(p => p.Margin = U.Th(12, 0)).At(0, 1),
                    U.T(Format.When(e.Long("createdAt") ?? 0), 11.5, null, Theme.Mu2).At(0, 2));
                list.Add(new Border { Child = row, Background = U.W(.035), CornerRadius = new CornerRadius(10) });
            }
            if (n == 50) list.Add(U.Ghost("Starsze", () => _ = FillAudit(list, last), 32).With(b => b.HorizontalAlignment = HorizontalAlignment.Stretch));
            if (list.Children.Count == 0) list.Add(U.T("Pusto.", 13.5, null, Theme.Mu));
        }
        catch (Exception ex) { list.Children.Clear(); list.Add(U.T(GibCordClient.Friendly(ex), 13, null, Theme.Red, wrap: true)); }
    }

    string Target(JsonElement e, Server srv)
    {
        var id = e.Long("targetId") ?? 0;
        var ch = e.Prop("changes");
        return e.Str("targetType") switch
        {
            "user" or "member" => S.NameOf(id, srv),
            "role" => srv.Roles.Find(r => r.Id == id)?.Name ?? ch.Str("name") ?? ch.Prop("after").Str("name") ?? ch.Prop("before").Str("name") ?? "",
            "channel" => "#" + (S.ChannelById(id)?.Name ?? ch.Str("name") ?? ch.Prop("after").Str("name") ?? ch.Prop("before").Str("name") ?? "?"),
            "category" => srv.Categories.Find(c => c.Id == id)?.Name ?? ch.Str("name") ?? "",
            _ => ch.Str("name") ?? "",
        };
    }

    static string Details(JsonElement e)
    {
        var ch = e.Prop("changes");
        if (ch.ValueKind != JsonValueKind.Object) return "";
        if (ch.Str("reason") is { Length: > 0 } reason) return "Powód: " + reason;
        if (ch.Str("roleName") is { } rn) return "Rola: " + rn;
        if (ch.Prop("after").ValueKind == JsonValueKind.Object)
        {
            var parts = new List<string>();
            foreach (var p in ch.Prop("after").EnumerateObject().Take(4))
            {
                var before = ch.Prop("before").ValueKind == JsonValueKind.Object && ch.Prop("before").TryGetProperty(p.Name, out var bv) ? Short(bv) : "";
                parts.Add($"{p.Name}: {(before.Length > 0 ? before + " → " : "")}{Short(p.Value)}");
            }
            return string.Join(" · ", parts);
        }
        return "";
    }

    static string Short(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => (v.GetString() ?? "") is var s && s.Length > 40 ? s[..40] + "…" : v.GetString() ?? "",
        JsonValueKind.True => "tak", JsonValueKind.False => "nie", JsonValueKind.Null => "—",
        JsonValueKind.Number => v.ToString(), _ => "…",
    };
}
