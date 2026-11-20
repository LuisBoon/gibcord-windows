using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace GibCord.App.Ui;

/// <summary>Wybór osób (nowa rozmowa / grupa, dodawanie do grupy) i ustawienia grupy.</summary>
static class People
{
    /// <summary>Znajomi + osoby ze wspólnych serwerów (bez zablokowanych i siebie), dostępni pierwsi.</summary>
    static List<long> Candidates(Store s, IEnumerable<long>? exclude = null)
    {
        var ex = new HashSet<long>(exclude ?? []) { s.Me.Id };
        var ids = s.Friends.Values.Where(f => f.Status == "friends").Select(f => f.UserId)
            .Concat(s.Servers.SelectMany(x => x.Members).Select(m => m.UserId))
            .Distinct().Where(id => !ex.Contains(id) && !s.Blocked.Contains(id) && s.U(id) is not null);
        var friends = s.Friends.Where(f => f.Value.Status == "friends").Select(f => f.Key).ToHashSet();
        return ids.OrderByDescending(friends.Contains).ThenBy(id => s.StatusOf(id) == "offline").ThenBy(id => s.U(id)!.Name, StringComparer.Create(Format.Pl, true)).ToList();
    }

    /// <summary>Okienko z listą osób do zaznaczenia. Zwraca wybrane id albo null (anulowano).</summary>
    public static async Task<List<long>?> Pick(Shell sh, string title, int max, Func<int, string> okLabel, IEnumerable<long>? exclude = null, List<long>? only = null)
    {
        var s = sh.S;
        var all = only ?? Candidates(s, exclude);
        var picked = new List<long>();
        var search = new TextBox { PlaceholderText = "Wpisz nazwę znajomego" };
        var chips = new WrapPanel { Spacing = 6 };
        var list = new StackPanel { Spacing = 2 };
        var friends = s.Friends.Where(f => f.Value.Status == "friends").Select(f => f.Key).ToHashSet();
        ContentDialog? d = null;
        void Draw()
        {
            chips.Children.Clear();
            foreach (var id in picked)
            {
                var uid = id;
                var x = new Button { Content = U.H(6, U.T(s.U(id)!.Name, 12, U.Semi), U.T("✕", 10, null, Theme.Mu)), Background = U.Alpha("#ffb98f", .18), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(10, 3) };
                x.Click += (_, _) => { picked.Remove(uid); Draw(); };
                chips.Children.Add(x);
            }
            list.Children.Clear();
            var q = EmojiData.Fold(search.Text.Trim());
            foreach (var id in all.Where(id => q.Length == 0 || EmojiData.Fold(s.U(id)!.Name).Contains(q) || EmojiData.Fold(s.U(id)!.Username).Contains(q)).Take(60))
            {
                var uid = id;
                var on = picked.Contains(id);
                var check = new CheckBox { IsChecked = on, MinWidth = 0, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
                var g = new Grid().Cols("auto", "*", "auto").Add(new Avatar(30, true).Set(s, sh.C.Api, id, null, true).At(0, 0),
                    U.V(0, U.T(s.U(id)!.Name, 13.5, U.Semi), U.T("@" + s.U(id)!.Username + (friends.Contains(id) ? " · znajomy" : ""), 11, null, Theme.Mu2)).With(p => p.Margin = U.Th(10, 0)).At(0, 1),
                    check.At(0, 2));
                list.Add(U.Row(g, () =>
                {
                    if (picked.Contains(uid)) picked.Remove(uid);
                    else if (picked.Count < max) picked.Add(uid);
                    else { sh.Info($"Najwyżej {max} {Format.Plural(max, "osoba", "osoby", "osób")}", true); return; }
                    Draw();
                }, on, 8, U.Th(8, 4), U.Th(0, 1)));
            }
            if (list.Children.Count == 0) list.Add(U.T("Nikogo takiego nie ma wśród znajomych i osób z Twoich serwerów.", 13, null, Theme.Mu, wrap: true));
            if (d is not null) { d.PrimaryButtonText = okLabel(picked.Count); d.IsPrimaryButtonEnabled = picked.Count > 0; }
        }
        search.TextChanged += (_, _) => Draw();
        d = new ContentDialog
        {
            Title = title, Content = U.V(10, search, chips, new ScrollViewer { Content = list, Height = 340 }).With(p => p.Width = 420),
            PrimaryButtonText = okLabel(0), IsPrimaryButtonEnabled = false, CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary,
            XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        Draw();
        search.Loaded += (_, _) => search.Focus(FocusState.Programmatic);
        return await d.ShowAsync() == ContentDialogResult.Primary && picked.Count > 0 ? picked : null;
    }

    /// <summary>Wybór członków serwera (np. nadanie roli kilku osobom naraz).</summary>
    public static Task<List<long>?> PickMembers(Shell sh, Server srv, string title, IEnumerable<long> exclude)
    {
        var ex = exclude.ToHashSet();
        var ids = srv.Members.Select(m => m.UserId).Where(id => !ex.Contains(id) && sh.S.U(id) is not null)
            .OrderBy(id => sh.S.NameOf(id, srv), StringComparer.Create(Format.Pl, true)).ToList();
        return Pick(sh, title, 200, n => n == 0 ? "Wybierz osoby" : $"Dodaj ({n})", null, ids);
    }

    /// <summary>„+” przy rozmowach prywatnych: jedna osoba = rozmowa 1:1, więcej = grupa (do 10 osób).</summary>
    public static async void NewConversation(Shell sh)
    {
        var ids = await Pick(sh, "Nowa rozmowa", 9, n => n <= 1 ? "Napisz" : $"Utwórz grupę ({n + 1} osób)");
        if (ids is null) return;
        if (ids.Count == 1) { sh.OpenDmWith(ids[0]); return; }
        try
        {
            var dm = await sh.C.Api.CreateGroup(ids);
            sh.S.Apply(new GatewayEvent("dm_create", System.Text.Json.JsonSerializer.SerializeToElement(new { dm }, Json.Options)));
            sh.Go(0, dm.Id);
        }
        catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
    }

    /// <summary>Ustawienia grupy: obrazek, nazwa, osoby (dodaj / usuń — właściciel), wyjście z grupy.</summary>
    public static async void GroupSettings(Shell sh, long dmId)
    {
        var s = sh.S;
        if (s.DmById(dmId) is not { IsGroup: true } d) return;
        var own = d.OwnerId == s.Me.Id;
        ContentDialog? dlg = null;
        var name = new TextBox { Text = d.Name ?? "", PlaceholderText = s.DmTitle(d), MaxLength = 100 };
        var icon = U.Ghost("Zmień obrazek", async () =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, sh.W.Hwnd);
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try
            {
                using var st = await f.OpenStreamForReadAsync();
                await sh.C.Api.PostImage($"dms/{dmId}/icon", st, f.ContentType);
                sh.Info("Obrazek grupy zmieniony");
            }
            catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
        }, 32);
        var members = new StackPanel { Spacing = 2 };
        foreach (var uid in d.UserIds)
        {
            var id = uid;
            var g = new Grid().Cols("auto", "*", "auto").Add(new Avatar(28, true).Set(s, sh.C.Api, uid, null, true).At(0, 0),
                U.H(6, U.T(s.NameOf(uid), 13.5, U.Semi), d.OwnerId == uid ? U.T("👑", 11) : null, uid == s.Me.Id ? U.T("(Ty)", 11.5, null, Theme.Mu2) : null).With(p => p.Margin = U.Th(10, 0)).At(0, 1));
            if (own && uid != s.Me.Id)
                g.Add(U.IconButton("", "Usuń z grupy", async () =>
                {
                    try { await sh.C.Api.RemoveFromGroup(dmId, id); dlg?.Hide(); } catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
                }, 11, 26).At(0, 2));
            members.Add(new Border { Child = g, Padding = U.Th(6, 4) });
        }
        var add = d.UserIds.Count < 10 ? U.Ghost("+ Dodaj osoby", async () =>
        {
            dlg?.Hide();
            var ids = await Pick(sh, "Dodaj do grupy", 10 - d.UserIds.Count, n => n == 0 ? "Dodaj" : $"Dodaj ({n})", d.UserIds);
            if (ids is null) return;
            foreach (var id in ids) { try { await sh.C.Api.AddToGroup(dmId, id); } catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); break; } }
        }, 32) : null;
        var leave = U.Ghost("Opuść grupę", async () =>
        {
            dlg?.Hide();
            if (!await M.Confirm(sh, "Opuścić grupę?", "Nie zobaczysz już nowych wiadomości. Wrócić możesz tylko, gdy ktoś Cię doda.", "Opuść")) return;
            try { await sh.C.Api.RemoveFromGroup(dmId, 0); sh.Go(0, 0); } catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
        }, 32).With(b => ((TextBlock)b.Content).Foreground = Theme.Red);
        dlg = new ContentDialog
        {
            Title = "Ustawienia grupy",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = U.V(10,
                    U.H(12, new Avatar(56).SetRaw(s.DmTitle(d), "#b69cff", sh.C.Api.Abs(d.Icon), null), icon),
                    U.T("NAZWA GRUPY", 11, U.Bold, Theme.Mu2), name,
                    U.T($"OSOBY — {d.UserIds.Count}/10", 11, U.Bold, Theme.Mu2), members, add, U.HLine(.1), leave).With(p => p.Width = 400),
            },
            PrimaryButtonText = "Zapisz nazwę", CloseButtonText = "Zamknij", DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && name.Text.Trim() != (d.Name ?? ""))
        {
            try { await sh.C.Api.RenameGroup(dmId, name.Text); } catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
        }
    }
}
