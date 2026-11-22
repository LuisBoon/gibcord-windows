namespace GibCord.Core;

/// <summary>6.2 Kalambury na tablicy: stan gry z ramki board_game (bez hasła — to dostaje tylko rysujący w board_word).</summary>
public sealed class KalGame
{
    public string Phase { get; set; } = "draw"; // draw | reveal | end
    public long Host { get; set; }
    public long Drawer { get; set; }
    public int Round { get; set; }
    public int Rounds { get; set; }
    public int Turn { get; set; }
    public int Secs { get; set; }
    public long StartedAt { get; set; }
    public long EndsAt { get; set; }
    public string Mask { get; set; } = "";
    public string? Category { get; set; }
    public List<long> Guessed { get; set; } = [];
    public List<long> Order { get; set; } = [];
    public int SkipsLeft { get; set; }
    public List<KalScore> Scores { get; set; } = [];
    public long Now { get; set; }
    /// <summary>Hasło — tylko po zakończeniu tury (reveal / end).</summary>
    public string? Word { get; set; }
    public string? Reason { get; set; }
}

public sealed class KalScore
{
    public long UserId { get; set; }
    public int Score { get; set; }
}

public sealed class KalWordsInfo
{
    public List<string> Custom { get; set; } = [];
    public List<string> Categories { get; set; } = [];
}
