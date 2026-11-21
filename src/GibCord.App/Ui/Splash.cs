using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace GibCord.App.Ui;

/// <summary>Start z Gibkiem (szlif 6.0.3, pkt 10): logo „oddycha” i mruga, dopóki GibCord się wczytuje.</summary>
static class Splash
{
    /// <summary>Logo Gibka z powiekami: oczy na obrazku 256 px mają ok. x 102–117 i 138–153, y 124–145.</summary>
    public static FrameworkElement Gibek(double size)
    {
        var k = size / 256;
        var img = new Image { Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png"))), Width = size, Height = size };
        var lids = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        // powieki w kolorze twarzy, domyślnie „zwinięte” (skala Y = 0) — przy mrugnięciu rozwijają się od góry
        foreach (var (x, color) in new[] { (99.0, "#f79f87"), (135.0, "#f4937f") })
        {
            var lid = new Ellipse { Width = 22 * k, Height = 26 * k, Fill = U.B(color) };
            Canvas.SetLeft(lid, x * k);
            Canvas.SetTop(lid, 121 * k);
            lid.Scale = new Vector3(1, 0, 1);
            lids.Children.Add(lid);
            Motion.Loop(lid, new Vector3((float)(11 * k), 0, 0), 3200, (0f, 1f, 0f), (.86f, 1f, 0f), (.9f, 1f, 1f), (.95f, 1f, 0f), (1f, 1f, 0f));
        }
        var inner = new Grid { Width = size, Height = size };
        inner.Children.Add(img);
        inner.Children.Add(lids);
        // lekkie „oddychanie” logo (wewnątrz), a całość na start wyskakuje (na zewnątrz) — dwie animacje skali na dwóch elementach
        Motion.Loop(inner, new Vector3((float)size / 2, (float)size / 2, 0), 2600, (0f, 1f, 1f), (.5f, 1.045f, 1.045f), (1f, 1f, 1f));
        var g = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, Children = { inner } };
        Motion.Pop(g, .7f);
        return g;
    }
}
