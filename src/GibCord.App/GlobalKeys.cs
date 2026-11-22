using System.Runtime.InteropServices;
using GibCord.Core;

namespace GibCord.App;

/// <summary>
/// Skróty działające wszędzie, także w grze: Ctrl+Shift+M — mikrofon, Ctrl+Shift+D — dźwięk (wygłuszenie).
/// Ukryte okno tylko na komunikaty (HWND_MESSAGE) + RegisterHotKey.
/// </summary>
public sealed class GlobalKeys : IDisposable
{
    const int WM_HOTKEY = 0x312, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    const int IdMute = 0x6C11, IdDeaf = 0x6C12, IdClip = 0x6C13, MOD_ALT = 1, VK_F9 = 0x78;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    readonly GibCordClient _c;
    readonly WndProc _proc;
    IntPtr _hwnd;

    public GlobalKeys(GibCordClient c)
    {
        _c = c;
        _proc = Proc;
        try
        {
            var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandle(null), lpszClassName = "GibCordSkroty" };
            RegisterClassEx(ref wc);
            _hwnd = CreateWindowEx(0, "GibCordSkroty", "GibCord — skróty", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }
        catch (Exception ex) { App.Log("Skróty globalne: " + ex.Message); }
        c.VoiceChanged += Sync;
    }

    bool _on, _clipOn;
    /// <summary>6.2: Alt+F9 zajmujemy tylko, gdy bufor klipów nagrywa (inaczej skrót zostaje dla innych programów).</summary>
    public void SetClipKey(bool on)
    {
        if (on == _clipOn || _hwnd == IntPtr.Zero) return;
        _clipOn = on;
        if (on) { if (!RegisterHotKey(_hwnd, IdClip, MOD_ALT | MOD_NOREPEAT, VK_F9)) App.Log("Skrót Alt+F9 zajęty przez inny program"); }
        else UnregisterHotKey(_hwnd, IdClip);
    }

    /// <summary>Skróty zajmujemy tylko w trakcie rozmowy (poza nią Ctrl+Shift+M/D działają w innych programach).</summary>
    public void Sync()
    {
        var want = App.Prefs.GlobalShortcuts && _c.VoiceChannelId != 0 && _hwnd != IntPtr.Zero;
        if (want == _on) return;
        _on = want;
        if (want)
        {
            if (!RegisterHotKey(_hwnd, IdMute, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 'M')) App.Log("Skrót Ctrl+Shift+M zajęty przez inny program");
            if (!RegisterHotKey(_hwnd, IdDeaf, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 'D')) App.Log("Skrót Ctrl+Shift+D zajęty przez inny program");
        }
        else { UnregisterHotKey(_hwnd, IdMute); UnregisterHotKey(_hwnd, IdDeaf); }
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_HOTKEY)
        {
            switch ((int)w)
            {
                case IdMute: _c.ToggleMute(); Tones.Play(_c.Voice.Options.Muted ? "mute" : "unmute"); App.Prefs.Save(); return IntPtr.Zero;
                case IdDeaf: _c.ToggleDeafen(); Tones.Play(_c.Voice.Options.Deafened ? "mute" : "unmute"); App.Prefs.Save(); return IntPtr.Zero;
                case IdClip: App.Window?.UiRun(() => ClipCtl.Instance?.SaveNow()); return IntPtr.Zero;
            }
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    public void Dispose()
    {
        _c.VoiceChanged -= Sync;
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, IdMute);
        UnregisterHotKey(_hwnd, IdDeaf);
        UnregisterHotKey(_hwnd, IdClip);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
