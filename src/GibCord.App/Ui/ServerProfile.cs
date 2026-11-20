using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace GibCord.App.Ui;

/// <summary>Mój profil na serwerze (nick, osobny awatar) i role do samodzielnego wyboru.</summary>
static class ServerProfile
{
    public static async void Show(Shell sh, Server srv)
    {
        var s = sh.S;
        var me = s.MemberOf(srv, s.Me.Id);
        var nick = new TextBox { Text = me?.Nick ?? "", PlaceholderText = s.Me.Name, MaxLength = 32 };
        ContentDialog? d = null;
        async void Avatar(bool remove)
        {
            try
            {
                if (remove) await sh.C.Api.DeleteJson($"servers/{srv.Id}/members/@me/avatar");
                else
                {
                    var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                    foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }) picker.FileTypeFilter.Add(ext);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, sh.W.Hwnd);
                    var f = await picker.PickSingleFileAsync();
                    if (f is null) return;
                    using var st = await f.OpenStreamForReadAsync();
                    await sh.C.Api.PostImage($"servers/{srv.Id}/members/@me/avatar", st, f.ContentType);
                }
                d?.Hide();
                sh.Info(remove ? "Wrócił zwykły awatar" : "Awatar na tym serwerze zmieniony");
            }
            catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
        }
        var content = U.V(10,
            U.T($"Tak widzą Cię na {srv.Name}. Na innych serwerach nic się nie zmienia.", 13, null, Theme.Mu, wrap: true),
            U.T("NICK", 11, U.Bold, Theme.Mu2), nick,
            U.T("AWATAR NA TYM SERWERZE", 11, U.Bold, Theme.Mu2),
            U.H(12, new Avatar(56).Set(s, sh.C.Api, s.Me.Id, srv), U.V(6, U.Ghost("Zmień", () => Avatar(false), 30), me?.Avatar is not null ? U.Ghost("Przywróć zwykły", () => Avatar(true), 30) : null)));
        d = new ContentDialog
        {
            Title = "Mój profil na serwerze", Content = content.With(p => p.Width = 380), PrimaryButtonText = "Zapisz nick", CloseButtonText = "Zamknij",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary || nick.Text.Trim() == (me?.Nick ?? "")) return;
        try { await sh.C.Api.SetNick(srv.Id, 0, nick.Text); sh.Info("Nick zapisany"); }
        catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
    }

    /// <summary>Role „do wyboru” (selfAssignable), pogrupowane jak na stronie.</summary>
    public static async void PickRoles(Shell sh, Server srv)
    {
        var s = sh.S;
        var mine = (s.MemberOf(srv, s.Me.Id)?.RoleIds ?? []).ToHashSet();
        var panel = new StackPanel { Spacing = 8, Width = 400 };
        foreach (var grp in srv.Roles.Where(r => r.SelfAssignable).OrderByDescending(r => r.Position).GroupBy(r => string.IsNullOrWhiteSpace(r.Group) ? "Role" : r.Group!))
        {
            panel.Add(U.T(grp.Key.ToUpperInvariant(), 11, U.Bold, Theme.Mu2).With(t => t.Margin = U.Th(0, 6, 0, 0)));
            foreach (var r in grp)
            {
                var rid = r.Id;
                var on = mine.Contains(rid);
                var t = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
                t.Toggled += async (_, _) =>
                {
                    try
                    {
                        if (t.IsOn) await sh.C.Api.PutJson($"servers/{srv.Id}/members/@me/roles/{rid}");
                        else await sh.C.Api.DeleteJson($"servers/{srv.Id}/members/@me/roles/{rid}");
                    }
                    catch (Exception ex) { sh.Info(GibCordClient.Friendly(ex), true); }
                };
                panel.Add(new Grid().Cols("auto", "*", "auto").Add(
                    new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 12, Height = 12, Fill = U.B(r.Color ?? "#8b8599"), Margin = U.Th(0, 0, 10, 0) }.At(0, 0),
                    U.V(0, U.H(6, string.IsNullOrEmpty(r.Icon) || r.Icon!.StartsWith('/') ? null : U.T(r.Icon!, 13), U.T(r.Name, 14, U.Semi)),
                        string.IsNullOrWhiteSpace(r.Description) ? null : U.T(r.Description!, 12, null, Theme.Mu, wrap: true)).At(0, 1),
                    t.At(0, 2)));
            }
        }
        var d = new ContentDialog
        {
            Title = "Wybierz role", Content = new ScrollViewer { Content = panel, MaxHeight = 480 }, CloseButtonText = "Gotowe",
            XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        await d.ShowAsync();
    }
}
