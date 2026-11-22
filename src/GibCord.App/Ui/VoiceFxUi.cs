using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

/// <summary>
/// 6.2 Zmieniacz głosu: przycisk w rozmowie (pokazuje emoji wybranego efektu), okienko z efektami i odsłuchem,
/// emoji przy osobach, które mówią z efektem. Sam dźwięk liczy <see cref="GibCord.Core.Voice.VoiceFx"/> w silniku głosu.
/// </summary>
static class VoiceFxUi
{
    public static readonly (string Key, string Emoji, string Name, string Desc)[] List =
    [
        ("robot", "🤖", "Robot", "Metaliczny głos maszyny"),
        ("gibek", "🎈", "Gibek", "Wysoko, jak po helu"),
        ("olbrzym", "🗿", "Olbrzym", "Nisko i grubo"),
        ("jaskinia", "🦇", "Jaskinia", "Pogłos i echo"),
        ("radio", "📻", "Radio", "Jak z krótkofalówki"),
    ];

    public static string? Emoji(string? fx) => fx is null ? null : List.FirstOrDefault(x => x.Key == fx).Emoji;
    public static string? Name(string? fx) => fx is null ? null : List.FirstOrDefault(x => x.Key == fx).Name;

    /// <summary>Emoji efektu przy osobie (kafelek, lista na kanale) — null, gdy mówi zwykłym głosem.</summary>
    public static TextBlock? Badge(string? fx) => Emoji(fx) is { } e ? U.T(e, 12).With(t => { t.VerticalAlignment = VerticalAlignment.Center; ToolTipService.SetToolTip(t, "Efekt głosu: " + Name(fx)); }) : null;

    /// <summary>Okrągły przycisk na dole rozmowy.</summary>
    public static Button Button(Shell sh, double size = 44)
    {
        var fx = sh.C.Fx;
        Button b;
        if (Emoji(fx) is { } e)
        {
            b = new Button { Content = U.T(e, 19), Width = size, Height = size, Padding = U.Th(0), BorderThickness = U.Th(0), Background = U.B("#6b2d7c") };
            ToolTipService.SetToolTip(b, $"Efekt głosu: {Name(fx)} — zmień");
        }
        else b = U.IconButton("", "Efekty głosu", () => { }, 16, size);
        if (fx is null) b.Background = U.W(.1);
        b.CornerRadius = new CornerRadius(size / 2);
        b.Click += (_, _) => Show(sh, b);
        return b;
    }

    public static void Show(Shell sh, FrameworkElement anchor)
    {
        var c = sh.C;
        var panel = new StackPanel { Spacing = 8, Width = 320 };
        var fly = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        var allowed = c.VoiceChannelId == 0 || c.FxAllowed(c.VoiceChannelId);
        panel.Add(U.T("Efekty głosu", 15, U.Bold));
        if (!allowed) panel.Add(U.T("Moderator wyłączył efekty na tym kanale.", 12.5, null, Theme.Mu, wrap: true));
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 }.Cols("*", "*");
        var all = new List<(string? Key, string Emoji, string Name, string Desc)> { (null, "🎙️", "Bez efektu", "Twój zwykły głos") };
        all.AddRange(List.Select(x => ((string?)x.Key, x.Emoji, x.Name, x.Desc)));
        for (var i = 0; i < all.Count; i++)
        {
            var (key, emo, name, desc) = all[i];
            var on = c.Fx == key;
            var cell = new Grid { ColumnSpacing = 8 }.Cols("auto", "*").Add(
                U.T(emo, 20).With(t => t.VerticalAlignment = VerticalAlignment.Center).At(0, 0),
                U.V(0, U.T(name, 13, U.Semi), U.T(desc, 11, null, Theme.Mu).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis)).At(0, 1));
            var btn = new Button
            {
                Content = cell, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = U.Th(10, 7),
                CornerRadius = new CornerRadius(10), Background = on ? U.Alpha("#ffb98f", .16) : U.W(.05), BorderBrush = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(on ? 1.5 : 1),
                IsEnabled = key is null || allowed,
            };
            var k = key;
            btn.Click += (_, _) => { c.SetFx(k); fly.Hide(); sh.Info(k is null ? "Efekt głosu wyłączony" : $"Mówisz jako: {Name(k)}"); };
            grid.Add(btn.At(i / 2, i % 2));
        }
        grid.Rows(Enumerable.Repeat("auto", (all.Count + 1) / 2).ToArray());
        panel.Add(grid);
        var mon = new SwitchField { Header = "Odsłuch — słyszysz siebie tak, jak słyszą Cię inni", IsOn = c.Voice.FxMonitor };
        mon.Toggled += (_, _) => c.SetFxMonitor(mon.IsOn);
        panel.Add(mon);
        panel.Add(U.T("Odsłuch najlepiej w słuchawkach, bo z głośników może piszczeć.", 11.5, null, Theme.Mu2, wrap: true));
        fly.ShowAt(anchor);
    }
}
