namespace GibCord.Core.Voice;

/// <summary>Ustawienia dźwięku (zapisywane razem z resztą ustawień aplikacji).</summary>
public sealed class VoiceOptions
{
    /// <summary>Identyfikator mikrofonu ("" = domyślny w Windowsie).</summary>
    public string InputDevice { get; set; } = "";
    /// <summary>Identyfikator głośników / słuchawek ("" = domyślne w Windowsie).</summary>
    public string OutputDevice { get; set; } = "";
    /// <summary>Głośność mikrofonu 0–2 (1 = bez zmian).</summary>
    public double InputGain { get; set; } = 1;
    /// <summary>Głośność wszystkich osób 0–2.</summary>
    public double OutputVolume { get; set; } = 1;
    /// <summary>"vad" — wykrywanie głosu, "ptt" — naciśnij, aby mówić.</summary>
    public string Mode { get; set; } = "vad";
    /// <summary>Automatyczna czułość (próg dopasowuje się do szumu w pokoju).</summary>
    public bool AutoSens { get; set; } = true;
    /// <summary>Ręczny próg wykrywania głosu w dB (−80 … −10).</summary>
    public double Sens { get; set; } = -50;
    public bool NoiseSuppression { get; set; } = true;
    public bool EchoCancellation { get; set; } = true;
    public bool AutoGain { get; set; } = true;
    /// <summary>Klawisz „naciśnij, aby mówić” (kod wirtualnego klawisza Windows, 0 = nie ustawiono).</summary>
    public int PttKey { get; set; }
    /// <summary>Opóźnienie wyłączenia mikrofonu po puszczeniu klawisza (ms).</summary>
    public int PttReleaseMs { get; set; } = 200;
    /// <summary>Głośność transmisji (dźwięk z udostępnionego ekranu) 0–2.</summary>
    public double StreamVolume { get; set; } = 1;
    /// <summary>Głośność poszczególnych osób 0–2 (brak wpisu = 1).</summary>
    public Dictionary<long, double> UserVolume { get; set; } = [];
    /// <summary>Osoby wyciszone tylko u mnie.</summary>
    public HashSet<long> UserMuted { get; set; } = [];
    /// <summary>Wyciszony mikrofon / wyłączony dźwięk (pamiętane między rozmowami, jak na stronie).</summary>
    public bool Muted { get; set; }
    public bool Deafened { get; set; }

    public double VolumeOf(long uid) => UserVolume.TryGetValue(uid, out var v) ? Math.Clamp(v, 0, 2) : 1;
}

/// <summary>Ramka <c>voice_media</c> / odpowiedź POST /voice/token → <see cref="VoiceMedia"/>.</summary>
public static class VoiceMediaJson
{
    public static VoiceMedia? Parse(System.Text.Json.JsonElement e)
    {
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        var url = e.Str("url"); var token = e.Str("token");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return null;
        var cid = e.Long("channelId") ?? 0;
        return new VoiceMedia(cid, url, token, e.Str("room") ?? $"gc-{cid}", e.Str("identity") ?? "");
    }
}
