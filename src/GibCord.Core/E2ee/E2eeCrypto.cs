using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GibCord.Core.E2ee;

/// <summary>
/// 6.4: kryptografia szyfrowanych rozmów — zgodna co do bajtu ze stroną (WebCrypto), opis: PLAN-6.4.md, sekcja E.
/// • klucz urządzenia: ECDH P-256, publiczny jako surowe 65 bajtów (0x04‖X‖Y) w base64 z dopełnieniem;
/// • pudełko: ECDH → 32 bajty → HKDF-SHA256(sól „GibCord-E2EE-1”, info „call|fromUid:fromDev|toUid:toDev”) → AES-256-GCM,
///   iv 12 bajtów, bez AAD, ct = szyfrogram‖tag (16 bajtów), jak w WebCrypto;
/// • klucz rozmowy: 32 losowe bajty w base64url bez dopełnienia (43 znaki) — do LiveKit idzie jako TEKST (bajty UTF-8);
/// • kod bezpieczeństwa: SHA-256 z linii „uid:pub” posortowanych po uid, połączonych \n → 3 grupy po 4 cyfry.
/// </summary>
public static class E2eeCrypto
{
    public static readonly byte[] Salt = Encoding.UTF8.GetBytes("GibCord-E2EE-1");

    /// <summary>Nowy klucz urządzenia (P-256).</summary>
    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Losowy identyfikator urządzenia: 16 znaków 0-9a-f.</summary>
    public static string NewDeviceId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>Klucz publiczny w postaci surowej (0x04‖X‖Y, 65 bajtów) w base64 — tak jak exportKey('raw') w WebCrypto.</summary>
    public static string PublicRaw(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 4;
        Pad32(p.X!).CopyTo(raw, 1);
        Pad32(p.Y!).CopyTo(raw, 33);
        return Convert.ToBase64String(raw);
    }

    static byte[] Pad32(byte[] b) => b.Length == 32 ? b : b.Length > 32 ? b[^32..] : [.. new byte[32 - b.Length], .. b];

    /// <summary>Klucz publiczny z base64 surowych 65 bajtów (wyjątek przy złym kluczu).</summary>
    public static ECDiffieHellman ImportPublic(string b64)
    {
        var raw = Convert.FromBase64String(b64);
        if (raw.Length != 65 || raw[0] != 4) throw new CryptographicException("Zły klucz publiczny (oczekiwano 65 bajtów 0x04…)");
        var k = ECDiffieHellman.Create();
        k.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = raw[1..33], Y = raw[33..65] } });
        return k;
    }

    /// <summary>Klucz prywatny do zapisania (PKCS#8) — aplikacja trzyma go zaszyfrowanego kluczem konta Windows.</summary>
    public static byte[] ExportPrivate(ECDiffieHellman key) => key.ExportPkcs8PrivateKey();

    public static ECDiffieHellman ImportPrivate(byte[] pkcs8)
    {
        var k = ECDiffieHellman.Create();
        k.ImportPkcs8PrivateKey(pkcs8, out _);
        return k;
    }

    /// <summary>„call|fromUid:fromDev|toUid:toDev” — wiąże pudełko z rozmową, nadawcą i odbiorcą.</summary>
    public static string Info(long call, long fromUid, string fromDev, long toUid, string toDev) => $"{call}|{fromUid}:{fromDev}|{toUid}:{toDev}";

    /// <summary>Klucz AES pudełka: ECDH(mój prywatny, publiczny drugiej strony) → HKDF-SHA256 → 32 bajty.</summary>
    public static byte[] BoxKey(ECDiffieHellman mine, string theirPub, string info)
    {
        using var pub = ImportPublic(theirPub);
        var secret = mine.DeriveRawSecretAgreement(pub.PublicKey); // współrzędna X punktu (32 bajty) — jak deriveBits(…, 256)
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Salt, Encoding.UTF8.GetBytes(info)); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Zamyka tekst w pudełku. <paramref name="iv"/> tylko w testach (zgodność z wektorem ze strony) — normalnie losowy.</summary>
    public static E2eeBox Seal(ECDiffieHellman mine, string theirPub, string info, string plaintext, byte[]? iv = null)
    {
        var key = BoxKey(mine, theirPub, info);
        try
        {
            iv ??= RandomNumberGenerator.GetBytes(12);
            var pt = Encoding.UTF8.GetBytes(plaintext);
            var ct = new byte[pt.Length + 16];
            using var gcm = new AesGcm(key, 16);
            gcm.Encrypt(iv, pt, ct.AsSpan(0, pt.Length), ct.AsSpan(pt.Length, 16));
            return new E2eeBox(Convert.ToBase64String(iv), Convert.ToBase64String(ct));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Otwiera pudełko (wyjątek, gdy klucz / info się nie zgadza albo ktoś zmienił treść).</summary>
    public static string Open(ECDiffieHellman mine, string theirPub, string info, E2eeBox box)
    {
        var key = BoxKey(mine, theirPub, info);
        try
        {
            var iv = Convert.FromBase64String(box.Iv);
            var ct = Convert.FromBase64String(box.Ct);
            if (iv.Length != 12 || ct.Length < 16) throw new CryptographicException("Złe pudełko");
            var pt = new byte[ct.Length - 16];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(iv, ct.AsSpan(0, pt.Length), ct.AsSpan(pt.Length), pt);
            return Encoding.UTF8.GetString(pt);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Treść pudełka z kluczem rozmowy: {"k":"…","i":N}.</summary>
    public static string KeyPayload(string callKey, int index) => JsonSerializer.Serialize(new { k = callKey, i = index });

    public static (string Key, int Index)? ParseKeyPayload(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.Str("k") is not { Length: > 0 } k || r.Long("i") is not long i || i < 0 || i > 255) return null;
            return (k, (int)i);
        }
        catch { return null; }
    }

    /// <summary>Nowy klucz rozmowy: 32 losowe bajty, base64url bez dopełnienia (43 znaki).</summary>
    public static string NewCallKey() => B64Url(RandomNumberGenerator.GetBytes(32));

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Kod bezpieczeństwa rozmowy: „48 21 · 09 37 · 11 45” (taki sam u wszystkich, gdy nikt nie podmienił kluczy).</summary>
    public static string SafetyCode(IEnumerable<(long Uid, string Pub)> people)
    {
        var text = string.Join("\n", people.OrderBy(p => p.Uid).Select(p => $"{p.Uid}:{p.Pub}"));
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var groups = new List<string>();
        for (var i = 0; i < 6; i += 2)
        {
            var n = ((h[i] << 8) | h[i + 1]) % 10000;
            var s = n.ToString("D4");
            groups.Add(s[..2] + " " + s[2..]);
        }
        return string.Join(" · ", groups);
    }
}

/// <summary>Zaszyfrowane pudełko: {"iv": base64, "ct": base64 (szyfrogram‖tag)}.</summary>
public sealed record E2eeBox(string Iv, string Ct);

/// <summary>Klucz rozmowy z numerem (0–255, w kółko) — numer mówi LiveKit, którym kluczem zaszyfrowano ramkę.</summary>
public sealed record E2eeKey(string Key, int Index)
{
    /// <summary>Miejsce w pierścieniu kluczy LiveKit (16 miejsc, tak samo jak na stronie: indeks % 16).</summary>
    public int Slot => Index % RingSize;
    public const int RingSize = 16;
    /// <summary>Bajty podawane do LiveKit: tekst klucza w UTF-8 (strona: ExternalE2EEKeyProvider.setKey(tekst)).</summary>
    public byte[] Material => Encoding.UTF8.GetBytes(Key);
}

/// <summary>Urządzenie osoby z GET /api/e2ee/devices.</summary>
public sealed class E2eeDeviceInfo
{
    public string Dev { get; set; } = "";
    public string Pub { get; set; } = "";
    public long At { get; set; }
}
