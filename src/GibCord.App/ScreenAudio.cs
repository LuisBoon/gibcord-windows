using System.Runtime.InteropServices;
using GibCord.Core;

namespace GibCord.App;

/// <summary>
/// Dźwięk z komputera do transmisji ekranu — BEZ dźwięku samego GibCorda (głosy z rozmowy nie wracają do widzów).
/// Windows 10 2004+: „process loopback” z wykluczeniem naszego procesu (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK).
/// Format: 48 kHz, stereo, 16 bit → paczki po 10 ms do serwera głosu.
/// </summary>
public sealed class ScreenAudio : IDisposable
{
    readonly GibCordClient _c;
    Thread? _thread;
    volatile bool _stop;

    public ScreenAudio(GibCordClient c) { _c = c; }

    volatile bool _running;
    public bool Active => _thread is not null && _running;

    /// <summary>Startuje przechwytywanie (wątek MTA tworzy i używa obiektów audio). Zwraca komunikat błędu albo null.</summary>
    public string? Start()
    {
        if (Active) return null;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return "Dźwięk z komputera wymaga Windows 10 w wersji 2004 albo nowszej.";
        _stop = false;
        var ready = new TaskCompletionSource<string?>();
        _thread = new Thread(() =>
        {
            IAudioClient client;
            try { client = Activate(); }
            catch (Exception ex) { App.Log("Dźwięk z komputera: " + ex.Message); ready.TrySetResult("Nie udało się włączyć dźwięku z komputera."); return; }
            ready.TrySetResult(null);
            Loop(client);
        }) { IsBackground = true, Name = "GibCord dźwięk ekranu", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        var err = ready.Task.Wait(2500) ? ready.Task.Result : "Windows nie odpowiedział przy włączaniu dźwięku z komputera.";
        if (err is not null) { _stop = true; _thread = null; }
        return err;
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(500);
        _thread = null;
    }

    public void Dispose() => Stop();

    void Loop(IAudioClient client)
    {
        IAudioCaptureClient? cap = null;
        try
        {
            var iid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var o));
            cap = (IAudioCaptureClient)o;
            // tryb zdarzeń (jak w przykładzie Microsoftu „ApplicationLoopback”): Windows budzi nas, gdy są nowe próbki
            using var evt = new AutoResetEvent(false);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()));
            Marshal.ThrowExceptionForHR(client.Start());
            _running = true;
            var chunk = new short[960 * 2]; // 10 ms stereo
            var filled = 0;
            while (!_stop)
            {
                evt.WaitOne(100);
                while (cap.GetNextPacketSize(out var packet) == 0 && packet > 0)
                {
                    if (cap.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) break;
                    var silent = (flags & 2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                    var samples = (int)frames * 2;
                    var tmp = new short[samples];
                    if (!silent) Marshal.Copy(data, tmp, 0, samples);
                    cap.ReleaseBuffer(frames);
                    var i = 0;
                    while (i < samples)
                    {
                        var n = Math.Min(chunk.Length - filled, samples - i);
                        Array.Copy(tmp, i, chunk, filled, n);
                        filled += n; i += n;
                        if (filled == chunk.Length)
                        {
                            _c.Voice.PushScreenAudio(chunk);
                            chunk = new short[960 * 2];
                            filled = 0;
                        }
                    }
                }
            }
            client.Stop();
        }
        catch (Exception ex) { App.Log("Dźwięk z komputera (pętla): " + ex.Message); }
        finally
        {
            _running = false;
            if (cap is not null) Marshal.ReleaseComObject(cap);
            Marshal.ReleaseComObject(client);
        }
    }

    // ───────────────────────── COM: ActivateAudioInterfaceAsync + process loopback ─────────────────────────

    static IAudioClient Activate()
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)Environment.ProcessId,
            ProcessLoopbackMode = 1, // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
        };
        var size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        var blob = Marshal.AllocHGlobal(size);
        var pv = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(p, blob, false);
            Marshal.StructureToPtr(new PROPVARIANT_BLOB { vt = 65 /* VT_BLOB */, cbSize = (uint)size, pBlobData = blob }, pv, false);
            var handler = new Completion();
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out var op));
            if (!handler.Done.Wait(5000)) throw new TimeoutException("Windows nie odpowiedział");
            Marshal.ThrowExceptionForHR(op.GetActivateResult(out var hr, out var unk));
            Marshal.ThrowExceptionForHR(hr);
            var client = (IAudioClient)unk;
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = 48000, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = 48000 * 4, cbSize = 0 };
            const uint Loopback = 0x00020000, AutoConvert = 0x80000000, SrcDefaultQuality = 0x08000000;
            const uint EventCallback = 0x00040000;
            var hr2 = client.Initialize(0, Loopback | EventCallback | AutoConvert | SrcDefaultQuality, 2_000_000, 0, ref fmt, IntPtr.Zero);
            if (hr2 < 0) hr2 = client.Initialize(0, Loopback | EventCallback, 2_000_000, 0, ref fmt, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr2);
            return client;
        }
        finally { Marshal.FreeHGlobal(blob); Marshal.FreeHGlobal(pv); }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT_BLOB { public ushort vt; public ushort r1, r2, r3; public uint cbSize; public IntPtr pBlobData; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX { public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec; public uint nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize; }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation { [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface); }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint size);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
