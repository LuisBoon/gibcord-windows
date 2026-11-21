namespace GibCord.Core;

/// <summary>
/// BlurHash (https://blurha.sh, algorytm Wolt, licencja MIT) — dekodowanie rozmytego podglądu zdjęcia, który liczy serwer (pole „preview” załącznika).
/// Wynik: piksele BGRA (bez przezroczystości) do WriteableBitmap.
/// </summary>
public static class BlurHash
{
    const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";

    static int D83(string s, int from, int len)
    {
        var v = 0;
        for (var i = from; i < from + len; i++)
        {
            var d = Chars.IndexOf(s[i]);
            if (d < 0) throw new FormatException("znak spoza BlurHash");
            v = v * 83 + d;
        }
        return v;
    }

    static double ToLinear(int v) { var c = v / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    static byte ToSrgb(double v) { v = Math.Clamp(v, 0, 1); return (byte)Math.Clamp(Math.Round(v <= 0.0031308 ? v * 12.92 * 255 : (1.055 * Math.Pow(v, 1 / 2.4) - 0.055) * 255), 0, 255); }
    static double SignPow(double v, double e) => Math.CopySign(Math.Pow(Math.Abs(v), e), v);

    /// <summary>Czy napis wygląda na poprawny BlurHash (długość zgodna z liczbą składowych).</summary>
    public static bool IsValid(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length < 6) return false;
        try
        {
            var size = D83(hash, 0, 1);
            int nx = size % 9 + 1, ny = size / 9 + 1;
            return hash.Length == 4 + 2 * nx * ny && hash.All(c => Chars.Contains(c));
        }
        catch { return false; }
    }

    /// <summary>Piksele BGRA (w×h×4). Null, gdy napis jest uszkodzony.</summary>
    public static byte[]? Decode(string hash, int w, int h)
    {
        if (!IsValid(hash) || w <= 0 || h <= 0) return null;
        var size = D83(hash, 0, 1);
        int nx = size % 9 + 1, ny = size / 9 + 1;
        var max = (D83(hash, 1, 1) + 1) / 166.0;
        var col = new (double R, double G, double B)[nx * ny];
        var dc = D83(hash, 2, 4);
        col[0] = (ToLinear(dc >> 16), ToLinear((dc >> 8) & 255), ToLinear(dc & 255));
        for (var i = 1; i < col.Length; i++)
        {
            var v = D83(hash, 4 + i * 2, 2);
            col[i] = (SignPow((v / (19 * 19) - 9) / 9.0, 2) * max, SignPow((v / 19 % 19 - 9) / 9.0, 2) * max, SignPow((v % 19 - 9) / 9.0, 2) * max);
        }
        var px = new byte[w * h * 4];
        var cx = new double[w * nx];
        for (var x = 0; x < w; x++) for (var i = 0; i < nx; i++) cx[x * nx + i] = Math.Cos(Math.PI * x * i / w);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0;
                for (var j = 0; j < ny; j++)
                {
                    var by = Math.Cos(Math.PI * y * j / h);
                    for (var i = 0; i < nx; i++)
                    {
                        var basis = cx[x * nx + i] * by;
                        var c = col[j * nx + i];
                        r += c.R * basis; g += c.G * basis; b += c.B * basis;
                    }
                }
                var o = (y * w + x) * 4;
                px[o] = ToSrgb(b); px[o + 1] = ToSrgb(g); px[o + 2] = ToSrgb(r); px[o + 3] = 255;
            }
        }
        return px;
    }
}
