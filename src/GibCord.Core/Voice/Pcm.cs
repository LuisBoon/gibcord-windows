namespace GibCord.Core.Voice;

/// <summary>Drobne obliczenia na próbkach dźwięku (float −1…1 albo 16 bit).</summary>
public static class Pcm
{
    /// <summary>Poziom ramki w dBFS (−100 dla ciszy).</summary>
    public static double Db(ReadOnlySpan<short> s)
    {
        if (s.IsEmpty) return -100;
        double sum = 0;
        foreach (var v in s) { double f = v / 32768.0; sum += f * f; }
        var rms = Math.Sqrt(sum / s.Length);
        return rms > 1e-5 ? 20 * Math.Log10(rms) : -100;
    }

    public static double Db(ReadOnlySpan<float> s)
    {
        if (s.IsEmpty) return -100;
        double sum = 0;
        foreach (var v in s) sum += v * v;
        var rms = Math.Sqrt(sum / s.Length);
        return rms > 1e-5 ? 20 * Math.Log10(rms) : -100;
    }

    /// <summary>dB → 0…1 do pasków poziomu (−70 dB = 0, −10 dB = 1).</summary>
    public static double Meter(double db) => Math.Clamp((db + 70) / 60, 0, 1);

    public static short ToShort(float f)
    {
        var v = f * 32767f;
        return v >= 32767f ? (short)32767 : v <= -32768f ? (short)-32768 : (short)v;
    }

    /// <summary>Miękkie obcięcie: głośne miejsca łagodnie spłaszczone zamiast trzasków.</summary>
    public static float SoftClip(float x)
    {
        if (x > 0.9f) return 0.9f + 0.1f * MathF.Tanh((x - 0.9f) * 10f);
        if (x < -0.9f) return -0.9f + 0.1f * MathF.Tanh((x + 0.9f) * 10f);
        return x;
    }
}

/// <summary>
/// Prosta zmiana częstotliwości próbkowania (interpolacja liniowa), strumieniowo.
/// Wystarcza do mowy, gdy karta dźwiękowa pracuje np. na 44,1 kHz zamiast 48 kHz.
/// </summary>
public sealed class LinearResampler(int inRate, int outRate)
{
    readonly double _step = (double)inRate / outRate;
    double _pos;       // pozycja w strumieniu wejściowym względem _prev
    float _prev;
    bool _hasPrev;

    public int InRate { get; } = inRate;
    public int OutRate { get; } = outRate;

    /// <summary>Przetwarza kawałek wejścia, wynik dopisuje do <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        if (InRate == OutRate) { foreach (var v in input) output.Add(v); return; }
        if (input.IsEmpty) return;
        if (!_hasPrev) { _prev = input[0]; _hasPrev = true; }
        // pozycja 0 = ostatnia próbka poprzedniego kawałka (_prev), pozycja k = input[k - 1]
        while (true)
        {
            var k = (int)_pos;
            if (k + 1 > input.Length) break;
            var a = k == 0 ? _prev : input[k - 1];
            var b = input[k];
            output.Add(a + (b - a) * (float)(_pos - k));
            _pos += _step;
        }
        _pos -= input.Length;
        _prev = input[^1];
    }
}

/// <summary>Bufor kołowy próbek jednej osoby — wyrównuje nierówne przychodzenie ramek z sieci.</summary>
public sealed class SampleRing(int capacity)
{
    readonly float[] _buf = new float[capacity];
    readonly object _lock = new();
    int _read, _count;
    bool _primed;

    public int Count { get { lock (_lock) return _count; } }

    public void Write(ReadOnlySpan<short> data)
    {
        lock (_lock)
        {
            foreach (var s in data)
            {
                if (_count == _buf.Length) { _read = (_read + 1) % _buf.Length; _count--; } // pełny: najstarsze wypada
                _buf[(_read + _count) % _buf.Length] = s / 32768f;
                _count++;
            }
        }
    }

    /// <summary>
    /// Dodaje próbki (× <paramref name="gain"/>) do <paramref name="dst"/>. Na starcie czeka, aż zbierze się
    /// <paramref name="prebuffer"/> próbek; gdy zbierze się za dużo (opóźnienie), przeskakuje do przodu.
    /// </summary>
    public void MixInto(Span<float> dst, float gain, int prebuffer, int maxLatency)
    {
        lock (_lock)
        {
            if (!_primed)
            {
                if (_count < prebuffer) return;
                _primed = true;
            }
            if (_count > maxLatency)
            {
                var skip = _count - prebuffer;
                _read = (_read + skip) % _buf.Length;
                _count -= skip;
            }
            var n = Math.Min(dst.Length, _count);
            for (var i = 0; i < n; i++) dst[i] += _buf[(_read + i) % _buf.Length] * gain;
            _read = (_read + n) % _buf.Length;
            _count -= n;
            if (_count == 0 && n < dst.Length) _primed = false; // zabrakło — znów poczekamy na zapas
        }
    }

    public void Clear() { lock (_lock) { _read = 0; _count = 0; _primed = false; } }
}
