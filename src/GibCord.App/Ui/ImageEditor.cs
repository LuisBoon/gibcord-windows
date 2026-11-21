using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using GibCord.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using System.Runtime.InteropServices.WindowsRuntime;
using DColor = System.Drawing.Color;
using DRect = System.Drawing.Rectangle;
using XColor = Windows.UI.Color;

namespace GibCord.App.Ui;

/// <summary>
/// Edytor obrazka przed wysłaniem: przytnij, zamaż (piksele), rysuj, tekst, obróć, cofnij.
/// Pracuje na kopii w pamięci (System.Drawing); wynik bez metadanych — m.in. bez położenia GPS ze zdjęcia z telefonu.
/// </summary>
static class ImageEditor
{
    enum Tool { Crop, Blur, Draw, Text }
    const int ViewW = 720, ViewH = 380;
    static readonly string[] Colors = ["#ffb98f", "#ff4d6a", "#ffd34d", "#6fe3a5", "#7cc7ff", "#ffffff", "#111111"];

    public static bool CanEdit(string name, string? mime) =>
        (mime ?? "").ToLowerInvariant() is "image/png" or "image/jpeg" or "image/jpg" or "image/bmp"
        || System.IO.Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp";

    /// <summary>Otwiera edytor. Zwraca nowe bajty (i typ) albo null, gdy anulowano.</summary>
    public static async Task<(byte[] Data, string Mime)?> Open(Shell sh, byte[] data, string name)
    {
        Bitmap? cur;
        try { cur = Load(data); }
        catch { sh.Info("Tego obrazka nie da się edytować.", true); return null; }
        var jpeg = System.IO.Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg";
        var history = new Stack<Bitmap>();
        var tool = Tool.Blur;
        var color = Colors[1];

        var image = new Microsoft.UI.Xaml.Controls.Image { Stretch = Stretch.Fill };
        var canvas = new Canvas { Background = new SolidColorBrush(XColor.FromArgb(1, 0, 0, 0)) }; // przezroczyste, ale łapie wskaźnik
        var stage = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stage.Children.Add(image); stage.Children.Add(canvas);
        var host = new Grid { Width = ViewW, Height = ViewH, Background = U.B("#66000000"), CornerRadius = new CornerRadius(10) };
        host.Children.Add(stage);
        var info = U.T("", 12, null, Theme.Mu);
        double scale = 1;

        async Task Show()
        {
            scale = Math.Min(1.0, Math.Min((double)ViewW / cur!.Width, (double)ViewH / cur.Height));
            var w = Math.Max(1, cur.Width * scale); var h = Math.Max(1, cur.Height * scale);
            image.Width = w; image.Height = h; canvas.Width = w; canvas.Height = h;
            canvas.Children.Clear();
            using var ms = new MemoryStream();
            cur.Save(ms, ImageFormat.Png);
            var bi = new BitmapImage();
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ras.WriteAsync(ms.ToArray().AsBuffer());
            ras.Seek(0);
            await bi.SetSourceAsync(ras);
            image.Source = bi;
            info.Text = $"{cur.Width} × {cur.Height} px";
        }
        void Push() { history.Push(cur!); cur = (Bitmap)cur!.Clone(); }

        // ── narzędzia ──
        var toolBtns = new Dictionary<Tool, ChipField>();
        var swatches = U.H(6);
        void SetTool(Tool t)
        {
            tool = t;
            foreach (var (k, b) in toolBtns) b.IsChecked = k == t;
            swatches.Visibility = t is Tool.Draw or Tool.Text ? Visibility.Visible : Visibility.Collapsed;
        }
        ChipField TB(Tool t, string glyph, string label)
        {
            var b = new ChipField { Content = U.H(6, U.I(glyph, 14), U.T(label, 13)), Padding = U.Th(10, 5), CornerRadius = new CornerRadius(8) };
            b.Click += (_, _) => SetTool(t);
            toolBtns[t] = b;
            return b;
        }
        foreach (var c in Colors)
        {
            var sw = new Button { Width = 22, Height = 22, Padding = U.Th(0), CornerRadius = new CornerRadius(11), Background = U.B(c), BorderBrush = U.W(.4), BorderThickness = U.Th(1) };
            sw.Click += (_, _) => { color = c; foreach (var o in swatches.Children.OfType<Button>()) o.BorderThickness = U.Th(o == sw ? 3 : 1); };
            if (c == color) sw.BorderThickness = U.Th(3);
            swatches.Children.Add(sw);
        }
        Button Act(string glyph, string tip, Func<Task> a) { var b = U.IconButton(glyph, tip, () => _ = a(), 15, 32); b.Background = U.W(.08); return b; }
        var rotL = Act("", "Obróć w lewo", async () => { Push(); cur!.RotateFlip(RotateFlipType.Rotate270FlipNone); await Show(); });
        var rotR = Act("", "Obróć w prawo", async () => { Push(); cur!.RotateFlip(RotateFlipType.Rotate90FlipNone); await Show(); });
        rotL.RenderTransformOrigin = new Windows.Foundation.Point(.5, .5); rotL.RenderTransform = new ScaleTransform { ScaleX = -1 };
        var undo = Act("", "Cofnij (Ctrl+Z)", async () => { if (history.Count == 0) return; cur!.Dispose(); cur = history.Pop(); await Show(); });
        var bar = new Grid { ColumnSpacing = 6 }.Cols("auto", "auto", "auto", "auto", "*", "auto", "auto", "auto").Add(
            TB(Tool.Crop, "", "Przytnij").At(0, 0), TB(Tool.Blur, "", "Zamaż").At(0, 1), TB(Tool.Draw, "", "Rysuj").At(0, 2), TB(Tool.Text, "", "Tekst").At(0, 3),
            rotL.At(0, 5), rotR.At(0, 6), undo.At(0, 7));
        SetTool(Tool.Blur);

        // ── wskaźnik ──
        Windows.Foundation.Point? down = null;
        Microsoft.UI.Xaml.Shapes.Rectangle? rect = null;
        Polyline? line = null;
        Windows.Foundation.Point Clamp(Windows.Foundation.Point p) => new(Math.Clamp(p.X, 0, canvas.Width), Math.Clamp(p.Y, 0, canvas.Height));
        canvas.PointerPressed += (_, e) =>
        {
            var p = Clamp(e.GetCurrentPoint(canvas).Position);
            if (tool == Tool.Text) { PlaceText(p); e.Handled = true; return; }
            canvas.CapturePointer(e.Pointer);
            down = p;
            if (tool == Tool.Draw)
            {
                line = new Polyline { Stroke = U.B(color), StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                line.Points.Add(p);
                canvas.Children.Add(line);
            }
            else
            {
                rect = new Microsoft.UI.Xaml.Shapes.Rectangle { Stroke = U.B(tool == Tool.Crop ? "#ffb98f" : "#ffffff"), StrokeThickness = 2, StrokeDashArray = [4, 3], Fill = U.Alpha("#ffffff", tool == Tool.Blur ? .18 : .06) };
                Canvas.SetLeft(rect, p.X); Canvas.SetTop(rect, p.Y);
                canvas.Children.Add(rect);
            }
        };
        canvas.PointerMoved += (_, e) =>
        {
            if (down is not { } d) return;
            var p = Clamp(e.GetCurrentPoint(canvas).Position);
            if (line is not null) { line.Points.Add(p); return; }
            if (rect is null) return;
            Canvas.SetLeft(rect, Math.Min(d.X, p.X)); Canvas.SetTop(rect, Math.Min(d.Y, p.Y));
            rect.Width = Math.Abs(p.X - d.X); rect.Height = Math.Abs(p.Y - d.Y);
        };
        canvas.PointerReleased += async (_, e) =>
        {
            if (down is not { } d) return;
            down = null;
            canvas.ReleasePointerCapture(e.Pointer);
            var p = Clamp(e.GetCurrentPoint(canvas).Position);
            if (line is not null)
            {
                var pts = line.Points.Select(q => new PointF((float)(q.X / scale), (float)(q.Y / scale))).ToArray();
                line = null;
                if (pts.Length < 2) { await Show(); return; }
                Push();
                using (var g = Graphics.FromImage(cur!))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var pen = new Pen(ColorOf(color), (float)Math.Max(2, 4 / scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawLines(pen, pts);
                }
                await Show();
                return;
            }
            rect = null;
            var r = ToImage(Math.Min(d.X, p.X), Math.Min(d.Y, p.Y), Math.Abs(p.X - d.X), Math.Abs(p.Y - d.Y));
            if (r.Width < 4 || r.Height < 4) { await Show(); return; }
            Push();
            if (tool == Tool.Crop) { var c2 = cur!.Clone(r, PixelFormat.Format32bppArgb); cur.Dispose(); cur = c2; }
            else Pixelate(cur!, r);
            await Show();
        };
        DRect ToImage(double x, double y, double w, double h)
        {
            var r = new DRect((int)(x / scale), (int)(y / scale), (int)Math.Ceiling(w / scale), (int)Math.Ceiling(h / scale));
            r.Intersect(new DRect(0, 0, cur!.Width, cur.Height));
            return r;
        }
        void PlaceText(Windows.Foundation.Point p)
        {
            var tb = new TextBox { MinWidth = 140, FontSize = 18, FontWeight = U.Bold, Foreground = U.B(color), Background = U.B("#99000000"), PlaceholderText = "Wpisz i Enter" };
            Canvas.SetLeft(tb, p.X); Canvas.SetTop(tb, Math.Max(0, p.Y - 16));
            canvas.Children.Add(tb);
            var done = false;
            async Task Commit()
            {
                if (done) return;
                done = true;
                var t = tb.Text.Trim();
                canvas.Children.Remove(tb);
                if (t.Length == 0) return;
                Push();
                using (var g = Graphics.FromImage(cur!))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    var size = (float)Math.Max(12, 22 / scale);
                    using var path = new GraphicsPath();
                    using var family = new System.Drawing.FontFamily("Segoe UI");
                    path.AddString(t, family, (int)System.Drawing.FontStyle.Bold, size, new PointF((float)(p.X / scale), (float)(Math.Max(0, p.Y - 10) / scale)), StringFormat.GenericDefault);
                    using var outline = new Pen(DColor.FromArgb(200, 0, 0, 0), size / 6) { LineJoin = LineJoin.Round };
                    g.DrawPath(outline, path);
                    using var fill = new SolidBrush(ColorOf(color));
                    g.FillPath(fill, path);
                }
                await Show();
            }
            tb.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = Commit(); } else if (e.Key == VirtualKey.Escape) { e.Handled = true; done = true; canvas.Children.Remove(tb); } };
            tb.LostFocus += (_, _) => _ = Commit();
            tb.Loaded += (_, _) => tb.Focus(FocusState.Programmatic);
        }

        await Show();
        var hint = U.T("Przeciągnij po obrazku: zamaż albo przytnij zaznaczony fragment. Rysuj — trzymaj i prowadź. Tekst — kliknij w miejsce.", 12, null, Theme.Mu, wrap: true);
        var body = U.V(10, bar, swatches, host, new Grid { ColumnSpacing = 16 }.Cols("*", "auto").Add(hint.At(0, 0), info.At(0, 1)));
        var dlg = new AppDialog
        {
            Title = "Edytuj obrazek", Content = body, PrimaryButtonText = "Gotowe", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary,
            XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 820.0;
        // akcent GibCorda zamiast systemowego niebieskiego (przycisk „Gotowe” i wybrane narzędzie)
        foreach (var k in new[] { "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed" })
            dlg.Resources[k] = Theme.Accent;
        foreach (var k in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed" })
            dlg.Resources[k] = Theme.OnAccent;
        dlg.Resources["ContentDialogMaxHeight"] = 760.0;
        dlg.KeyDown += async (_, e) =>
        {
            var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrl && e.Key == VirtualKey.Z && history.Count > 0) { e.Handled = true; cur!.Dispose(); cur = history.Pop(); await Show(); }
        };
        await M.WaitNoDialog(sh.XamlRoot);
        var ok = await dlg.ShowAsync() == ContentDialogResult.Primary;
        foreach (var b in history) b.Dispose();
        if (!ok || history.Count == 0) { cur!.Dispose(); return null; } // bez zmian → zostaje oryginał
        try
        {
            using var outMs = new MemoryStream();
            if (jpeg)
            {
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                cur!.Save(outMs, enc, ep);
            }
            else cur!.Save(outMs, ImageFormat.Png);
            return (outMs.ToArray(), jpeg ? "image/jpeg" : "image/png");
        }
        finally { cur!.Dispose(); }
    }

    static DColor ColorOf(string hex) => DColor.FromArgb(255, Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

    /// <summary>Wczytanie z obrotem z EXIF (zdjęcia z telefonu) i bez metadanych.</summary>
    static Bitmap Load(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var src = System.Drawing.Image.FromStream(ms, true, true);
        var rot = RotateFlipType.RotateNoneFlipNone;
        if (src.PropertyIdList.Contains(0x0112))
        {
            var o = src.GetPropertyItem(0x0112)?.Value is { Length: > 0 } v ? v[0] : (byte)1;
            rot = o switch { 2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.Rotate180FlipX,
                5 => RotateFlipType.Rotate90FlipX, 6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX, 8 => RotateFlipType.Rotate270FlipNone, _ => rot };
        }
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb); // nowa bitmapa = bez EXIF/GPS
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        bmp.RotateFlip(rot);
        return bmp;
    }

    /// <summary>Zamazanie: fragment zmniejszony do kilku pikseli i powiększony bez wygładzania (nieodwracalne, w przeciwieństwie do rozmycia).</summary>
    static void Pixelate(Bitmap bmp, DRect r)
    {
        var block = Math.Max(8, Math.Max(r.Width, r.Height) / 12);
        var sw = Math.Max(1, r.Width / block); var shh = Math.Max(1, r.Height / block);
        using var small = new Bitmap(sw, shh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(bmp, new DRect(0, 0, sw, shh), r, GraphicsUnit.Pixel);
        }
        using var g2 = Graphics.FromImage(bmp);
        g2.InterpolationMode = InterpolationMode.NearestNeighbor;
        g2.PixelOffsetMode = PixelOffsetMode.Half;
        g2.DrawImage(small, r, new DRect(0, 0, sw, shh), GraphicsUnit.Pixel);
    }
}
