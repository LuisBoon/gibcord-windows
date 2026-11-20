using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using GibCord.Core;
using Microsoft.UI.Dispatching;

namespace GibCord.App;

/// <summary>
/// Nakładka w grze (jak w GibCordzie 1.0.7, wariant A „lista w rogu”): kto jest na kanale i kto mówi.
/// Osobne okno Win32 z przezroczystością na piksel (UpdateLayeredWindow), zawsze na wierzchu, klikanie przechodzi do gry,
/// nie zabiera fokusu i nie pojawia się na pasku zadań. Bez wstrzykiwania do gier (anty-cheaty).
/// Działa nad grami w oknie i w oknie bez ramki; nad wyłącznym pełnym ekranem Windows nic nie pokaże.
/// Ctrl+Shift+O — schowaj / pokaż.
/// </summary>
public sealed class GameOverlay : IDisposable
{
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0, WM_HOTKEY = 0x312, ULW_ALPHA = 2, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    const int HotkeyId = 0x6C01;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    readonly GibCordClient _c;
    readonly Func<bool> _inGame;
    readonly DispatcherQueueTimer _timer;
    readonly WndProc _proc;
    IntPtr _hwnd;
    bool _shown, _hidden, _hotkey;
    string _last = "";

    public GameOverlay(GibCordClient c, DispatcherQueue dq, Func<bool> inGame)
    {
        _c = c;
        _inGame = inGame;
        _proc = Proc;
        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => { try { Tick(); } catch (Exception ex) { App.Log("Nakładka: " + ex.Message); _timer.Stop(); } };
        _timer.Start();
    }

    void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandle(null), lpszClassName = "GibCordNakladka" };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "GibCordNakladka", "GibCord — nakładka", WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    /// <summary>Ctrl+Shift+O zajmujemy tylko w trakcie rozmowy — poza nią należy do innych programów (VS Code, przeglądarka).</summary>
    void SyncHotkey(bool inCall)
    {
        if (inCall == _hotkey) return;
        if (inCall)
        {
            EnsureWindow();
            _hotkey = RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 'O');
            if (!_hotkey) App.Log("Nakładka: skrót Ctrl+Shift+O zajęty przez inny program");
            _hotkey = true; // nie próbujemy co 200 ms
        }
        else
        {
            if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
            _hotkey = false;
            _hidden = false;
        }
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_HOTKEY && (int)w == HotkeyId)
        {
            try { _hidden = !_hidden; _last = ""; Tick(); } catch (Exception ex) { App.Log("Nakładka: " + ex.Message); }
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    void Tick()
    {
        var mode = App.Prefs.OverlayMode;
        var room = _c.VoiceChannelId;
        var want = mode != "off" && room != 0 && !_c.Offline && (mode == "always" || _inGame());
        SyncHotkey(mode != "off" && room != 0 && !_c.Offline);
        if (want) EnsureWindow();
        if (!want || _hidden) { if (_shown) { ShowWindow(_hwnd, SW_HIDE); _shown = false; } return; }
        var s = _c.Store;
        var members = (s.Voice.GetValueOrDefault(room) ?? []).Take(8).Select(m => (m.UserId, Name: s.NameOf(m.UserId, s.ServerOfChannel(room)), Color: Store.ColorOfUser(s.U(m.UserId)),
            Talk: _c.IsSpeaking(m.UserId), Muted: m.Muted || m.ServerMuted || m.Suppressed, Deaf: m.Deafened || m.ServerDeafened)).ToList();
        var key = App.Prefs.OverlayCorner + "|" + string.Join(";", members.Select(m => $"{m.UserId}{m.Talk}{m.Muted}{m.Deaf}{m.Name}"));
        if (key != _last) { _last = key; Render(members); }
        if (!_shown) { ShowWindow(_hwnd, SW_SHOWNOACTIVATE); _shown = true; }
    }

    void Render(List<(long UserId, string Name, string Color, bool Talk, bool Muted, bool Deaf)> members)
    {
        const int rowH = 34, pad = 10, w = 240;
        var h = pad * 2 + Math.Max(1, members.Count) * rowH;
        var k = App.Prefs.OverlaySize switch { "s" => .8f, "l" => 1.35f, _ => 1f };
        using var bmp = new Bitmap((int)Math.Ceiling(w * k), (int)Math.Ceiling(h * k), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.ScaleTransform(k, k);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(150, 18, 12, 34))) FillRound(g, bg, new RectangleF(0, 0, w, h), 12);
            using var font = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var small = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
            var y = pad;
            foreach (var m in members)
            {
                var c = ParseColor(m.Color);
                using (var av = new SolidBrush(c)) g.FillEllipse(av, pad, y + 3, 28, 28);
                if (m.Talk) using (var ring = new Pen(Color.FromArgb(255, 111, 227, 165), 2.5f)) g.DrawEllipse(ring, pad - 1, y + 2, 30, 30);
                var initial = m.Name.Length > 0 ? char.ToUpperInvariant(m.Name[0]).ToString() : "?";
                using (var dark = new SolidBrush(Color.FromArgb(255, 42, 22, 48)))
                {
                    var sz = g.MeasureString(initial, small);
                    g.DrawString(initial, small, dark, pad + 14 - sz.Width / 2, y + 17 - sz.Height / 2);
                }
                using (var fg = new SolidBrush(m.Talk ? Color.White : Color.FromArgb(220, 246, 242, 251)))
                    g.DrawString(m.Name, font, fg, new RectangleF(pad + 36, y + 7, w - pad * 2 - 36 - 36, 22), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                var ix = w - pad - 14;
                using var red = new SolidBrush(Color.FromArgb(255, 255, 107, 129));
                if (m.Deaf) { g.FillEllipse(red, ix, y + 12, 10, 10); ix -= 14; }
                if (m.Muted) g.FillRectangle(red, ix + 2, y + 13, 6, 9);
                y += rowH;
            }
        }
        Push(bmp);
    }

    void Push(Bitmap bmp)
    {
        var corner = App.Prefs.OverlayCorner;
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1), m = 16;
        var x = corner is "tr" or "br" ? sw - bmp.Width - m : m;
        var y = corner is "bl" or "br" ? sh - bmp.Height - m : m + 40;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        try
        {
            var dst = new POINT { X = x, Y = y };
            var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static void FillRound(Graphics g, Brush b, RectangleF r, float rad)
    {
        using var p = new GraphicsPath();
        var d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        g.FillPath(b, p);
    }

    static Color ParseColor(string hex)
    {
        try { return ColorTranslator.FromHtml(hex.Length is 4 or 7 ? hex : "#8b8599"); } catch { return Color.FromArgb(255, 139, 133, 153); }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hwnd != IntPtr.Zero) { UnregisterHotKey(_hwnd, HotkeyId); DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }
}
