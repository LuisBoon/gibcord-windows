using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

/// <summary>Prawa kolumna: osoby na serwerze (role z osobną grupą, dostępni, niedostępni) albo profil rozmówcy w DM.</summary>
public sealed class MembersPanel : Grid
{
    readonly Shell _sh;
    readonly StackPanel _list = new() { Padding = U.Th(10, 6, 10, 16) };

    public MembersPanel(Shell sh)
    {
        _sh = sh;
        Width = 236;
        Background = Theme.Members;
        BorderBrush = Theme.Line;
        BorderThickness = U.Th(1, 0, 0, 0);
        var sc = U.Scroll(_list);
        SmoothWheel.Attach(sc, _list);
        Children.Add(sc);
    }

    public void Refresh()
    {
        var s = _sh.S;
        var visible = s.Loaded && _sh.MembersVisible && _sh.ChannelId != 0 && !_sh.ThreadOpen;
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        _list.Children.Clear();
        _list.Padding = U.Th(10, 6, 10, 16);
        if (s.DmById(_sh.ChannelId) is { } d)
        {
            if (d.IsGroup) Group($"Osoby — {d.UserIds.Count}", d.UserIds, null);
            else if (s.DmOther(d) is { } o) DmProfile(o);
            return;
        }
        if (s.ServerById(_sh.ServerId) is not { } srv) return;
        if (srv.Members.Count == 0)
        {
            // lista osób jeszcze nie przyszła — szkielety
            for (var i = 0; i < 8; i++) _list.Children.Add(U.SkeletonMessage(i, 32).With(e => ((Grid)e).Padding = U.Th(6, 6, 10, 6)));
            return;
        }
        var online = new List<long>();
        var offline = new List<long>();
        foreach (var m in srv.Members) (s.StatusOf(m.UserId) == "offline" ? offline : online).Add(m.UserId);
        // role „osobna grupa” (hoist), od najwyższej
        var placed = new HashSet<long>();
        foreach (var r in srv.Roles.Where(r => r.Hoist).OrderByDescending(r => r.Position))
        {
            var ids = online.Where(id => !placed.Contains(id) && s.RolesOf(srv, id).FirstOrDefault(x => x.Hoist)?.Id == r.Id).ToList();
            if (ids.Count == 0) continue;
            placed.UnionWith(ids);
            Group($"{r.Name} — {ids.Count}", ids, srv);
        }
        var rest = online.Where(id => !placed.Contains(id)).ToList();
        if (rest.Count > 0) Group($"Dostępni — {rest.Count}", rest, srv);
        if (offline.Count > 0) Group($"Niedostępni — {offline.Count}", offline.Take(200).ToList(), srv, dim: true);
    }

    void Group(string title, List<long> ids, Server? srv, bool dim = false)
    {
        var s = _sh.S;
        _list.Children.Add(U.T(title.ToUpperInvariant(), 11, U.Bold, Theme.Mu2).With(t => t.Margin = U.Th(8, 14, 0, 4)));
        foreach (var id in ids.OrderBy(id => s.NameOf(id, srv), StringComparer.Create(Format.Pl, true)))
        {
            var u = s.U(id);
            var av = new Avatar(32, true).Set(s, _sh.C.Api, id, srv, true);
            var name = U.H(5, U.T(s.NameOf(id, srv), 13.5, U.Semi, U.B(s.NameColor(id, srv))));
            if (u?.Staff == true) name.Add(U.Box(U.T("GC", 8.5, U.Bold, Theme.OnAccent), U.B("#f2a93b"), 4, U.Th(3, 0)));
            if (srv?.OwnerId == id) name.Add(U.T("👑", 11).With(t => t.Tip("Właściciel serwera")));
            if (u?.BirthdayToday == true) name.Add(U.T("🎂", 11).With(t => t.Tip("Dziś ma urodziny!")));
            if (s.Notes.TryGetValue(id, out var note)) name.Add(U.I("\uE70B", 11, Theme.Accent).With(t => t.Tip("Twoja notatka: " + (note.Length > 120 ? note[..120] + "…" : note))));
            UIElement text = name;
            var act = ActivityLine(u);
            if (act is not null) text = U.V(0, name, act);
            var g = new Grid { Height = 44 }.Cols("auto", "*").Add(av.At(0, 0), ((FrameworkElement)text).With(t => t.Margin = U.Th(10, 0, 0, 0)).At(0, 1));
            var row = U.Row(g, null, false, 8, U.Th(6, 0), U.Th(0, 1));
            row.Tapped += (_, _) => _sh.ShowProfile(id, row);
            row.RightTapped += (_, e) => { e.Handled = true; UserMenu.Build(_sh, id, srv, _sh.CurrentChat, row).ShowAt(row, e.GetPosition(row)); };
            if (dim) row.Opacity = .45;
            _list.Children.Add(row);
        }
    }

    static FrameworkElement? ActivityLine(User? u)
    {
        if (u is null) return null;
        if (u.GameName is { } g) return U.H(4, U.I("", 11, Theme.Green), U.T("Gra w " + g, 11, null, Theme.Mu));
        if (u.Activity is { Type: "spotify" }) return U.H(4, U.I("", 11, Theme.Green), U.T("Słucha Spotify", 11, null, Theme.Mu));
        if (!string.IsNullOrEmpty(u.CustomStatus)) return U.T(u.CustomStatus!, 11, null, Theme.Mu);
        return null;
    }

    void DmProfile(User o)
    {
        var s = _sh.S;
        var bannerBrush = o.BannerImage is { } bi && _sh.C.Api.Abs(bi) is { } bu
            ? (Brush)new ImageBrush { ImageSource = Images.Get(bu, 480), Stretch = Stretch.UniformToFill }
            : o.Banner is { } bc ? U.B(bc) : Theme.AccentGradient();
        _list.Padding = U.Th(0, 0, 0, 16);
        _list.Children.Add(new Border { Height = 90, Background = bannerBrush });
        var av = new Avatar(76, true, Theme.RingHex).Set(s, _sh.C.Api, o.Id, null, true);
        av.HorizontalAlignment = HorizontalAlignment.Left;
        av.Margin = U.Th(16, -40, 0, 0);
        _list.Children.Add(av);
        var body = U.V(10);
        body.Padding = U.Th(16, 8, 16, 0);
        body.Add(U.T(o.Name, 19, U.Bold, wrap: true), U.T("@" + o.Username + (string.IsNullOrEmpty(o.Pronouns) ? "" : " · " + o.Pronouns), 12.5, null, Theme.Mu).With(t => t.Margin = U.Th(0, -8, 0, 0)));
        if (!string.IsNullOrEmpty(o.CustomStatus)) body.Add(U.T(o.CustomStatus!, 13, null, Theme.Fg, wrap: true));
        Border Card(string label, UIElement content) => U.Box(U.V(6, U.T(label.ToUpperInvariant(), 10.5, U.Bold, Theme.Mu2), content), U.B("#4D0A0618"), 12, U.Th(12, 10));
        if (o.Activity is { Type: "spotify" } a)
        {
            var art = a.Image is { } img && Uri.TryCreate(img, UriKind.Absolute, out var iu) ? new Image { Source = Images.Get(iu, 112), Width = 48, Height = 48 } : null;
            body.Add(Card("Słucha Spotify", U.H(10, art, U.V(1, U.T(a.Title ?? "", 12.5, U.Bold), U.T(a.ArtistText, 11.5, null, Theme.Mu)))));
        }
        if (o.GameName is { } game) body.Add(Card("Gra", U.H(8, U.I("", 15, Theme.Green), U.T(game, 12.5, U.Bold))));
        if (!string.IsNullOrWhiteSpace(o.Bio)) body.Add(Card("O mnie", U.T(o.Bio!, 12.5, null, Theme.Fg, wrap: true)));
        body.Add(Card("Na GibCordzie od", U.T(Format.Local(o.CreatedAt).ToString("d MMMM yyyy", Format.Pl), 12.5)));
        var common = s.Servers.Where(x => x.Members.Any(m => m.UserId == o.Id)).Select(x => x.Name).ToList();
        if (common.Count > 0) body.Add(Card($"Wspólne serwery — {common.Count}", U.T(string.Join(", ", common.Take(6)), 12.5, null, Theme.Fg, wrap: true)));
        _list.Children.Add(body);
    }
}
