using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using WinRT;

namespace GibCord.App;

/// <summary>Ekran albo okno do udostępnienia (własne okienko wyboru): uchwyt, nazwa, opis i miniaturka PNG.</summary>
public sealed record ScreenSource(bool IsScreen, IntPtr Handle, string Title, string Sub)
{
    public byte[]? Thumb { get; set; }
}

/// <summary>
/// Lista ekranów i okien z miniaturkami (GDI: zrzut monitora, PrintWindow dla okien) oraz tworzenie GraphicsCaptureItem
/// prosto z uchwytu (IGraphicsCaptureItemInterop) — bez systemowego okienka wyboru.
/// </summary>
public static class ScreenSources
{
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc cb, IntPtr data);
    delegate bool MonitorProc(IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowProc cb, IntPtr data);
    delegate bool WindowProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int len, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }

    const int ThumbW = 320;

    /// <summary>Monitory: „Ekran 1 (główny)”, rozdzielczość.</summary>
    public static List<ScreenSource> Screens()
    {
        var list = new List<(IntPtr Mon, RECT R, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT r, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            var primary = GetMonitorInfo(m, ref mi) && (mi.dwFlags & 1) != 0;
            list.Add((m, mi.rcMonitor, primary));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(x => x.Primary).ThenBy(x => x.R.Left).Select((x, i) =>
            new ScreenSource(true, x.Mon, $"Ekran {i + 1}{(x.Primary ? " (główny)" : "")}", $"{x.R.Right - x.R.Left}×{x.R.Bottom - x.R.Top}")
            { Thumb = Grab(x.R) }).ToList();
    }

    /// <summary>Okna, które da się udostępnić: widoczne, z tytułem, nie narzędziowe, nie ukryte przez system, bez GibCorda.</summary>
    public static List<ScreenSource> Apps()
    {
        var res = new List<ScreenSource>();
        var me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
                var ex = (long)GetWindowLongPtr(h, -20);
                if ((ex & 0x80 /*WS_EX_TOOLWINDOW*/) != 0) return true;
                if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out var cloaked, 4) == 0 && cloaked != 0) return true;
                var len = GetWindowTextLength(h);
                if (len <= 0) return true;
                GetWindowThreadProcessId(h, out var pid);
                if (pid == me) return true;
                var sb = new StringBuilder(Math.Min(len + 1, 300));
                GetWindowText(h, sb, sb.Capacity);
                var title = sb.ToString();
                if (title is "Program Manager" or "Windows Input Experience") return true;
                string exe;
                try { using var p = Process.GetProcessById((int)pid); exe = p.ProcessName; } catch { exe = ""; }
                res.Add(new ScreenSource(false, h, title, exe + (IsIconic(h) ? " · zminimalizowane" : "")));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return res.Take(40).ToList();
    }

    /// <summary>Miniaturka okna (PrintWindow z pełną treścią — działa też z oknami rysowanymi przez kartę graficzną).</summary>
    public static void ThumbOf(ScreenSource s)
    {
        if (s.IsScreen || s.Thumb is not null) return;
        try
        {
            if (IsIconic(s.Handle) || !GetWindowRect(s.Handle, out var r)) return;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 8 || h < 8 || w > 10000 || h > 10000) return;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(s.Handle, hdc, 2 /*PW_RENDERFULLCONTENT*/); }
                finally { g.ReleaseHdc(hdc); }
            }
            s.Thumb = Png(bmp);
        }
        catch { }
    }

    static byte[]? Grab(RECT r)
    {
        try
        {
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 8 || h < 8) return null;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
            return Png(bmp);
        }
        catch { return null; }
    }

    /// <summary>Zmniejszenie do 320 px szerokości i PNG.</summary>
    static byte[] Png(Bitmap src)
    {
        var th = Math.Max(1, src.Height * ThumbW / Math.Max(1, src.Width));
        using var small = new Bitmap(ThumbW, th, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, ThumbW, th);
        }
        using var ms = new MemoryStream();
        small.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>GraphicsCaptureItem dla monitora albo okna bez systemowego okienka (IGraphicsCaptureItemInterop).</summary>
    public static unsafe GraphicsCaptureItem? ItemFor(ScreenSource s)
    {
        var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hs));
        IntPtr factory = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref interopIid, out factory));
            // vtable: IUnknown (3), CreateForWindow, CreateForMonitor
            var vtbl = *(IntPtr**)factory;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[s.IsScreen ? 4 : 3];
            var hr = create(factory, s.Handle, &itemIid, &item);
            Marshal.ThrowExceptionForHR(hr);
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(hs);
        }
    }
}
