using System.Globalization;
using System.Text.Json.Serialization;

namespace GibCord.Core;

// 6.4: profil „jak z Nitro” — ramki wokół awatara, efekt przy otwarciu karty, kolory karty i wizytówka (PLAN-6.4.md, sekcja P).

/// <summary>Kafelek wizytówki: game {name, hours?} · clip {mid, url, channelId…} · spotify · text {t}.</summary>
public sealed class ShowcaseTile
{
    public string K { get; set; } = "";
    public string? Name { get; set; }
    public long? Hours { get; set; }
    public long? Mid { get; set; }
    public string? Url { get; set; }
    public long? ChannelId { get; set; }
    public long? ServerId { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>BlurHash klatki filmu (jeśli serwer go policzył).</summary>
    public string? Preview { get; set; }
    public string? T { get; set; }

    /// <summary>Kafelek do zapisu (PATCH /api/me) — tylko pola, które serwer przyjmuje.</summary>
    public object ToPatch() => K switch
    {
        "game" => new { k = "game", name = Name ?? "" },
        "clip" => new { k = "clip", mid = Mid ?? 0 },
        "text" => new { k = "text", t = T ?? "" },
        _ => (object)new { k = K },
    };
}

public sealed class DecorDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>spin | pulse | float | none.</summary>
    public string Anim { get; set; } = "none";
}

public sealed class FxDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>GET /api/profile/catalog.</summary>
public sealed class ProfileCatalog
{
    public List<DecorDef> Decor { get; set; } = [];
    public List<FxDef> Fx { get; set; } = [];
    public List<string> ShowcaseKinds { get; set; } = [];
    public int ShowcaseMax { get; set; } = 3;
}

/// <summary>GET /api/me/showcase-options: gry ze statystyk i własne filmy.</summary>
public sealed class ShowcaseOptions
{
    public List<ShowcaseGame> Games { get; set; } = [];
    public List<ShowcaseClip> Clips { get; set; } = [];
}
public sealed class ShowcaseGame { public string Name { get; set; } = ""; public long Hours { get; set; } }
public sealed class ShowcaseClip
{
    public long Mid { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long ChannelId { get; set; }
    public long At { get; set; }
}

public static class ProfileLook
{
    /// <summary>Spis jak w serwerze (S.Profile.cs) — gdy katalog z serwera jeszcze nie przyszedł albo w trybie pokazowym.</summary>
    public static readonly ProfileCatalog Builtin = new()
    {
        Decor =
        [
            D("zorza", "Zorza", "spin"), D("neon", "Neon", "pulse"), D("plomienie", "Płomienie", "float"), D("sniezynki", "Śnieżynki", "spin"),
            D("kocie-uszy", "Kocie uszy", "none"), D("sluchawki", "Słuchawki Gibka", "none"), D("korona", "Korona", "float"), D("wieniec", "Wieniec", "none"),
            D("piksele", "Piksele", "none"), D("serduszka", "Serduszka", "spin"), D("iskierki", "Iskierki", "pulse"), D("aureola", "Aureola", "float"),
        ],
        Fx = [F("konfetti", "Konfetti"), F("snieg", "Śnieg"), F("iskry", "Iskry"), F("serca", "Serca"), F("babelki", "Bąbelki")],
        ShowcaseKinds = ["game", "clip", "spotify", "text"],
        ShowcaseMax = 3,
    };
    static DecorDef D(string id, string name, string anim) => new() { Id = id, Name = name, Anim = anim };
    static FxDef F(string id, string name) => new() { Id = id, Name = name };

    /// <summary>Katalog z serwera (ustawia klient po pobraniu), inaczej wbudowany.</summary>
    public static ProfileCatalog Catalog { get; set; } = Builtin;

    public static DecorDef? DecorById(string? id) => string.IsNullOrEmpty(id) ? null : Catalog.Decor.Find(d => d.Id == id) ?? Builtin.Decor.Find(d => d.Id == id);

    /// <summary>Gotowe pary kolorów karty profilu.</summary>
    public static readonly (string Name, string C1, string C2)[] Presets =
    [
        ("Zachód słońca", "#ff9a62", "#8f3a8a"), ("Ocean", "#3fc1d9", "#1d3f8f"), ("Las", "#6fcf7a", "#1f4d3a"), ("Lawenda", "#c7b3ff", "#5a3b8c"),
        ("Malina", "#ff6b9a", "#5c1631"), ("Noc", "#3a3f73", "#0d0f1f"), ("Brzask", "#ffe08a", "#ff8a65"), ("Mięta", "#c8fff0", "#7cc7ff"),
    ];

    /// <summary>Względna jasność koloru #rrggbb (0–1, jak w WCAG).</summary>
    public static double Luminance(string hex)
    {
        if (!TryRgb(hex, out var r, out var g, out var b)) return 0;
        static double L(int c) { var s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(r) + 0.7152 * L(g) + 0.0722 * L(b);
    }

    /// <summary>Czy na tym gradiencie lepiej czytać ciemny tekst (średnia jasność obu kolorów).</summary>
    public static bool IsLight(string c1, string c2) => (Luminance(c1) + Luminance(c2)) / 2 > 0.35;

    public static bool TryRgb(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (hex is not { Length: 7 } || hex[0] != '#') return false;
        return int.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, null, out r) && int.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, null, out g)
            && int.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, null, out b);
    }

    public static bool ValidTheme(List<string>? t) => t is { Count: 2 } && t.All(c => TryRgb(c, out _, out _, out _));
}

// ───────────────────────── 6.4: most z Discordem (PLAN-6.4.md, sekcja M) ─────────────────────────

/// <summary>Pole „bridge” kanału: z czym jest połączony na Discordzie.</summary>
public sealed class ChannelBridge
{
    public string Guild { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool Paused { get; set; }
}

/// <summary>Wiadomość z zewnątrz (most): {src:"discord", name, avatar}.</summary>
public sealed class MessageExt
{
    public string? Src { get; set; }
    public string? Name { get; set; }
    public string? Avatar { get; set; }
    [JsonIgnore] public bool Discord => Src == "discord";
}

public sealed class BridgeItem
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public string Guild { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool Paused { get; set; }
    public long CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
}

/// <summary>GET /api/servers/:id/bridges.</summary>
public sealed class BridgesInfo
{
    public bool Enabled { get; set; }
    /// <summary>off | connecting | ready | error.</summary>
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? InviteUrl { get; set; }
    public List<BridgeItem> Items { get; set; } = [];
}

/// <summary>POST /api/channels/:id/bridge-code.</summary>
public sealed class BridgeCodeInfo
{
    public string Code { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string? InviteUrl { get; set; }
    public string Command { get; set; } = "";
}
