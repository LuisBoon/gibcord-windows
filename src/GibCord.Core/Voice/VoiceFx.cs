namespace GibCord.Core.Voice;

/// <summary>
/// 6.2 zmieniacz głosu: efekt na mikrofonie (za bramką, przed wysłaniem), mono 48 kHz. Ten sam algorytm co na stronie
/// (media.js, węzeł gc-fx): robot (modulacja pierścieniowa + metaliczny grzebień), gibek (wyżej ×1,6), olbrzym (niżej ×0,7),
/// jaskinia (pogłos Freeverb + echo), radio (pasmo 450–2900 Hz, przester, szum tylko gdy ktoś mówi).
/// </summary>
public sealed class VoiceFx
{
    public static readonly string[] Names = ["robot", "gibek", "olbrzym", "jaskinia", "radio"];
    readonly int _sr;
    readonly object _lock = new();
    string? _fx;
    // zmiana wysokości
    const int N = 16384;
    readonly float[] _buf = new float[N];
    int _w;
    double _ph;
    readonly int _win;
    // robot
    double _rp;
    readonly float[] _cd;
    int _ci;
    // jaskinia
    readonly (float[] B, int I, float F)[] _cb;
    readonly (float[] B, int I)[] _ab;
    readonly float[] _eb;
    int _ei;
    // radio
    Biquad _hp, _lp, _hp2;
    float _env, _nz;
    readonly Random _rnd = new(7);

    struct Biquad
    {
        public double B0, B1, B2, A1, A2, X1, X2, Y1, Y2;
        public static Biquad Make(bool high, double f, int sr)
        {
            var w = 2 * Math.PI * f / sr; var c = Math.Cos(w); var al = Math.Sin(w) / (2 * 0.7071); var a0 = 1 + al;
            var b0 = (high ? 1 + c : 1 - c) / 2; var b1 = high ? -(1 + c) : 1 - c;
            return new Biquad { B0 = b0 / a0, B1 = b1 / a0, B2 = b0 / a0, A1 = -2 * c / a0, A2 = (1 - al) / a0 };
        }
        public double Run(double x) { var y = B0 * x + B1 * X1 + B2 * X2 - A1 * Y1 - A2 * Y2; X2 = X1; X1 = x; Y2 = Y1; Y1 = y; return y; }
        public void Reset() => X1 = X2 = Y1 = Y2 = 0;
    }

    public VoiceFx(int sampleRate = 48000)
    {
        _sr = sampleRate;
        _win = (int)Math.Round(sampleRate * 0.05);
        _cd = new float[(int)Math.Round(sampleRate * 0.006)];
        _cb = new[] { 1557, 1617, 1491, 1422 }.Select(n => (new float[(int)Math.Round(n * sampleRate / 44100.0)], 0, 0f)).ToArray();
        _ab = new[] { 225, 556 }.Select(n => (new float[(int)Math.Round(n * sampleRate / 44100.0)], 0)).ToArray();
        _eb = new float[(int)Math.Round(sampleRate * 0.24)];
        _hp = Biquad.Make(true, 450, sampleRate); _lp = Biquad.Make(false, 2900, sampleRate); _hp2 = Biquad.Make(true, 70, sampleRate);
    }

    /// <summary>Bieżący efekt (null = bez).</summary>
    public string? Effect
    {
        get => _fx;
        set
        {
            var v = value is not null && Names.Contains(value) ? value : null;
            lock (_lock)
            {
                if (v == _fx) return;
                _fx = v;
                Array.Clear(_buf); Array.Clear(_cd); Array.Clear(_eb); _ph = 0; _nz = 0; _env = 0;
                for (var i = 0; i < _cb.Length; i++) { Array.Clear(_cb[i].B); _cb[i].F = 0; }
                foreach (var a in _ab) Array.Clear(a.B);
                _hp.Reset(); _lp.Reset(); _hp2.Reset();
            }
        }
    }

    /// <summary>Przetwarza próbki w miejscu (−1…1).</summary>
    public void Process(Span<float> s)
    {
        if (_fx is null) return;
        lock (_lock)
            for (var i = 0; i < s.Length; i++) s[i] = Math.Clamp(One(s[i]), -1f, 1f);
    }

    float Rd(double d)
    {
        var p = _w - d; if (p < 0) p += N;
        var i = (int)Math.Floor(p); var f = (float)(p - i);
        return _buf[i & (N - 1)] * (1 - f) + _buf[(i + 1) & (N - 1)] * f;
    }

    float Shift(float x, double r)
    {
        _buf[_w] = x;
        var ph = _ph + (1 - r) / _win; ph -= Math.Floor(ph); _ph = ph;
        var p2 = (ph + 0.5) % 1;
        var y = (float)((1 - Math.Abs(2 * ph - 1)) * Rd(ph * _win + 1) + (1 - Math.Abs(2 * p2 - 1)) * Rd(p2 * _win + 1));
        _w = (_w + 1) & (N - 1);
        return y;
    }

    float Rev(float x)
    {
        float s = 0;
        for (var k = 0; k < _cb.Length; k++)
        {
            ref var c = ref _cb[k];
            var o = c.B[c.I];
            c.F = o * 0.8f + c.F * 0.2f;
            c.B[c.I] = x + c.F * 0.84f;
            if (++c.I >= c.B.Length) c.I = 0;
            s += o;
        }
        s *= 0.25f;
        for (var k = 0; k < _ab.Length; k++)
        {
            ref var a = ref _ab[k];
            var o = a.B[a.I];
            var y = o - s;
            a.B[a.I] = s + o * 0.5f;
            if (++a.I >= a.B.Length) a.I = 0;
            s = y;
        }
        return s;
    }

    float One(float x)
    {
        switch (_fx)
        {
            case "robot":
                {
                    _rp += 55.0 / _sr; if (_rp >= 1) _rp -= 1;
                    var y = x * (float)Math.Sin(2 * Math.PI * _rp) * 1.4f;
                    y += _cd[_ci] * 0.6f; _cd[_ci] = y; if (++_ci >= _cd.Length) _ci = 0;
                    return y * 0.75f;
                }
            case "gibek": return Shift(x, 1.6);
            case "olbrzym": return (float)_hp2.Run(Shift(x, 0.7)) * 1.25f;
            case "jaskinia":
                {
                    var wet = Rev(x * 0.6f);
                    var e = _eb[_ei]; _eb[_ei] = x * 0.8f + e * 0.38f; if (++_ei >= _eb.Length) _ei = 0;
                    return x * 0.75f + wet * 0.55f + e * 0.35f;
                }
            case "radio":
                {
                    var a = Math.Abs(x); _env = a > _env ? a : _env * 0.9995f;
                    var y = (float)Math.Tanh(_lp.Run(_hp.Run(x)) * 3.2) * 0.7f;
                    _nz = _nz * 0.97f + (float)(_rnd.NextDouble() - 0.5) * 0.006f;
                    return y + _nz * Math.Min(1f, _env * 8);
                }
            default: return x;
        }
    }
}
