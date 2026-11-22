using GibCord.Core;

namespace GibCord.App.Ui;

/// <summary>6.4: komendy „/” — wbudowane mini-gry serwera i komendy botów na serwerze (pamięć 2 min, odświeżana po commands_update).</summary>
static class Commands
{
    public sealed record Cmd(string Name, string Description, long BotId, long BotUserId, List<BotOption> Options, string? Builtin = null);

    static readonly Dictionary<long, (long At, List<BotCommand> List)> Cache = [];
    static readonly HashSet<long> Loading = [];

    /// <summary>Wbudowane (serwer sam je obsługuje jako mini-gry w wiadomości).</summary>
    public static readonly Cmd[] Builtins =
    [
        new("kosci", "Rzut kośćmi, np. /kosci 2d6", 0, 0, [], "/kosci "),
        new("moneta", "Orzeł czy reszka", 0, 0, [], "/moneta"),
        new("losuj", "Losuje jedno z podanych, np. /losuj pizza, kebab", 0, 0, [], "/losuj "),
    ];

    public static void Invalidate(long serverId) => Cache.Remove(serverId);

    /// <summary>Lista do podpowiedzi; jeśli nie ma jeszcze w pamięci — pobiera w tle i woła <paramref name="ready"/>.</summary>
    public static List<Cmd> For(Shell sh, long serverId, Action ready)
    {
        var list = new List<Cmd>(Builtins);
        if (serverId == 0) return list;
        if (Cache.TryGetValue(serverId, out var c) && Environment.TickCount64 - c.At < 120_000)
            list.AddRange(c.List.Select(b => new Cmd(b.Name, b.Description, b.BotId, b.BotUserId, b.Options)));
        else if (Loading.Add(serverId) && !sh.C.Offline)
            _ = Load(sh, serverId, ready);
        return list;
    }

    static async Task Load(Shell sh, long serverId, Action ready)
    {
        try { var l = await sh.C.Api.GetCommands(serverId); Cache[serverId] = (Environment.TickCount64, l); ready(); }
        catch { Cache[serverId] = (Environment.TickCount64, []); }
        finally { Loading.Remove(serverId); }
    }

    /// <summary>„/nazwa reszta” → komenda bota z opcjami (kolejno albo „opcja: wartość”). Null — to nie komenda bota.</summary>
    public static (Cmd Cmd, Dictionary<string, object?> Options)? Parse(Shell sh, long serverId, string text)
    {
        if (!text.StartsWith('/') || serverId == 0 || !Cache.TryGetValue(serverId, out var c)) return null;
        var sp = text.IndexOf(' ');
        var name = (sp < 0 ? text[1..] : text[1..sp]).ToLowerInvariant();
        var b = c.List.FirstOrDefault(x => x.Name == name);
        if (b is null) return null;
        var rest = sp < 0 ? "" : text[(sp + 1)..].Trim();
        var opts = new Dictionary<string, object?>();
        if (rest.Length > 0 && b.Options.Count > 0)
        {
            var named = System.Text.RegularExpressions.Regex.Matches(rest, @"(\p{L}[\p{L}\d_-]*)\s*[:=]\s*([^:=]+?)(?=\s+\p{L}[\p{L}\d_-]*\s*[:=]|$)");
            if (named.Count > 0 && named.All(m => b.Options.Any(o => o.Name == m.Groups[1].Value.ToLowerInvariant())))
                foreach (System.Text.RegularExpressions.Match m in named) opts[m.Groups[1].Value.ToLowerInvariant()] = Value(b.Options.First(o => o.Name == m.Groups[1].Value.ToLowerInvariant()), m.Groups[2].Value.Trim());
            else if (b.Options.Count == 1) opts[b.Options[0].Name] = Value(b.Options[0], rest);
            else
            {
                var parts = rest.Split(' ', b.Options.Count, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (var i = 0; i < parts.Length; i++) opts[b.Options[i].Name] = Value(b.Options[i], parts[i]);
            }
        }
        return (new Cmd(b.Name, b.Description, b.BotId, b.BotUserId, b.Options), opts);
    }

    static object? Value(BotOption o, string v) => o.Type switch
    {
        "number" when double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        "integer" when long.TryParse(v, out var l) => l,
        "boolean" => v.ToLowerInvariant() is "tak" or "true" or "1" or "t",
        _ => v,
    };
}
