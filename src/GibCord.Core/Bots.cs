using System.Text.Json;

namespace GibCord.Core;

// 6.4: boty, komendy „/” i webhooki.

public sealed class BotInfo
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string? Avatar { get; set; }
    public string? Color { get; set; }
    public string Description { get; set; } = "";
    public bool Public { get; set; }
    public bool Online { get; set; }
    public long CreatedAt { get; set; }
    public long TokenAt { get; set; }
    /// <summary>Tylko zaraz po utworzeniu bota.</summary>
    public string? Token { get; set; }
    public List<BotServer> Servers { get; set; } = [];
    public List<BotCommandName> Commands { get; set; } = [];
}
public sealed class BotServer { public long Id { get; set; } public string Name { get; set; } = ""; public string? Icon { get; set; } }
public sealed class BotCommandName { public string Name { get; set; } = ""; public string Description { get; set; } = ""; }

/// <summary>Komenda bota na serwerze (podpowiedzi po „/”).</summary>
public sealed class BotCommand
{
    public long BotId { get; set; }
    public long BotUserId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<BotOption> Options { get; set; } = [];
}
public sealed class BotOption
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
}

public sealed class WebhookInfo
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public long CreatedAt { get; set; }
}
