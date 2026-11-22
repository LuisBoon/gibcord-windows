namespace GibCord.Core;

// 6.1: „Twój GibCord” — podsumowanie roku (GET /api/wrapped/:rok) i pora roku z /api/info.

public sealed class WrappedInfo
{
    public int Year { get; set; }
    public bool Open { get; set; }
    public string? From { get; set; }
}

public sealed class Wrapped
{
    public int Year { get; set; }
    public long GeneratedAt { get; set; }
    public string? Since { get; set; }
    public bool Preview { get; set; }
    public WUser User { get; set; } = new();
    public WMessages Messages { get; set; } = new();
    public List<WEmoji> Emoji { get; set; } = [];
    public WReactions Reactions { get; set; } = new();
    public WMedia Media { get; set; } = new();
    public WVoice Voice { get; set; } = new();
    public List<WGame> Games { get; set; } = [];
    public WCount Servers { get; set; } = new();
    public WFriends Friends { get; set; } = new();
    public WLevel? Level { get; set; }
    public WEveryone Everyone { get; set; } = new();
    public WPersona Persona { get; set; } = new();
    public List<WPerson> TopPeople { get; set; } = [];
    public Dictionary<string, User> Users { get; set; } = [];

    public User? UserOf(long id) => Users.TryGetValue(id.ToString(), out var u) ? u : null;
}

public sealed class WUser { public long Id { get; set; } public string Name { get; set; } = ""; }
public sealed class WMessages
{
    public long Total { get; set; }
    public long Chars { get; set; }
    public long Days { get; set; }
    public List<long> Hours { get; set; } = [];
    public int TopHour { get; set; }
    public long Night { get; set; }
    public WDay? BusiestDay { get; set; }
    public List<WChannel> TopChannels { get; set; } = [];
    public WFirst? First { get; set; }
    public long MiniGames { get; set; }
}
public sealed class WDay { public string Date { get; set; } = ""; public long Count { get; set; } }
public sealed class WChannel
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public long? ServerId { get; set; }
    public string? Server { get; set; }
    public string? ServerIcon { get; set; }
    public long Count { get; set; }
    public long Minutes { get; set; }
}
public sealed class WFirst { public long At { get; set; } public string? Where { get; set; } }
public sealed class WEmoji { public string Emoji { get; set; } = ""; public long Count { get; set; } }
public sealed class WReactions { public long Given { get; set; } public long Received { get; set; } }
public sealed class WMedia
{
    public long Photos { get; set; }
    public long Videos { get; set; }
    public long VoiceNotes { get; set; }
    public long Files { get; set; }
    public long Gifs { get; set; }
    public long Stickers { get; set; }
    public long Polls { get; set; }
}
public sealed class WVoice
{
    public long Minutes { get; set; }
    public long StreamMinutes { get; set; }
    public List<WPerson> TopPeople { get; set; } = [];
    public WChannel? TopChannel { get; set; }
}
public sealed class WPerson { public long UserId { get; set; } public long Minutes { get; set; } public long Count { get; set; } }
public sealed class WGame { public string Name { get; set; } = ""; public long Minutes { get; set; } }
public sealed class WCount { public long Count { get; set; } public long Joined { get; set; } }
public sealed class WFriends { public long Count { get; set; } public long New { get; set; } }
public sealed class WLevel { public int Level { get; set; } public string? Server { get; set; } }
public sealed class WEveryone
{
    public long Messages { get; set; }
    public long VoiceMinutes { get; set; }
    public long Writers { get; set; }
    public long People { get; set; }
    public int? Rank { get; set; }
}
public sealed class WPersona { public string Key { get; set; } = ""; public string Title { get; set; } = ""; public string Text { get; set; } = ""; }
