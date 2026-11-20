using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GibCord.Core;

[Flags]
public enum TextStyle { None = 0, Bold = 1, Italic = 2, Underline = 4, Strike = 8, Spoiler = 16 }

public abstract record Span(TextStyle Style);
public sealed record TextSpan(string Text, TextStyle Style) : Span(Style);
public sealed record MentionSpan(long UserId, TextStyle Style) : Span(Style);
public sealed record RoleMentionSpan(long RoleId, TextStyle Style) : Span(Style);
public sealed record EveryoneSpan(TextStyle Style) : Span(Style);
public sealed record EmojiSpan(long Id, string Name, bool Animated, TextStyle Style) : Span(Style);
public sealed record LinkSpan(string Url, TextStyle Style) : Span(Style);
public sealed record CodeSpan(string Code, TextStyle Style) : Span(Style);

public abstract record Block;
/// <summary>Kolejne linie tekstu; <c>Quote</c> = linie zaczynające się od „> ”.</summary>
public sealed record LinesBlock(List<List<Span>> Lines, bool Quote) : Block;
public sealed record CodeBlock(string Code) : Block;

/// <summary>Karta ogłoszenia (#ogłoszenia, #co-nowego): [ZNACZEK], tytuł, punkty i akapity.</summary>
public sealed record AnnCard(string? Chip, string Title, List<(bool Bullet, string Text)> Items);

/// <summary>
/// Formatowanie wiadomości — ten sam zestaw co na stronie (core.js → fmt):
/// ```kod```, `kod`, **pogrubienie**, __podkreślenie__, *kursywa*, ~~przekreślenie~~, ||spoiler||,
/// &gt; cytat, &lt;@id&gt;, &lt;@&amp;rola&gt;, &lt;:emoji:id&gt;, @everyone/@wszyscy i linki.
/// </summary>
public static partial class Markup
{
    const char BOn = '', BOff = '', IOn = '', IOff = '', UOn = '', UOff = '',
        SOn = '', SOff = '', POn = '', POff = '';
    const int AtomBase = 0xF000;

    [GeneratedRegex(@"```(?:[a-z0-9+#-]*\n)?([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex CodeBlockRe();

    [GeneratedRegex(@"(?<code>`[^`\n]+`)|(?<user><@(?<uid>[0-9]{1,18})>)|(?<emoji><(?<an>a?):(?<en>[a-z0-9_]{2,32}):(?<eid>[0-9]{1,18})>)|(?<role><@&(?<rid>[0-9]{1,18})>)|(?<every>(?<=^|\s)@(?:everyone|wszyscy)\b)|(?<url>\bhttps?://[^\s<>]+[^\s<>.,:;""')\]!?])")]
    private static partial Regex AtomRe();

    [GeneratedRegex(@"\*\*([^*\n]+)\*\*")] private static partial Regex BoldRe();
    [GeneratedRegex(@"__([^_\n]+)__")] private static partial Regex UnderRe();
    [GeneratedRegex(@"(^|[^A-Za-z0-9_*])\*([^\s*](?:[^*\n]*[^\s*])?)\*(?![A-Za-z0-9_*])")] private static partial Regex ItalicRe();
    [GeneratedRegex(@"~~([^~\n]+)~~")] private static partial Regex StrikeRe();
    [GeneratedRegex(@"\|\|([^|\n]+)\|\|")] private static partial Regex SpoilerRe();
    [GeneratedRegex(@"[--]")] private static partial Regex OurCharsRe();

    public static List<Block> Parse(string? src)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrEmpty(src)) return blocks;
        var s = OurCharsRe().Replace(src.Replace("\r\n", "\n"), "");
        var pos = 0;
        foreach (Match m in CodeBlockRe().Matches(s))
        {
            AddText(blocks, s[pos..m.Index], trimEnd: true, trimStart: pos > 0);
            blocks.Add(new CodeBlock(m.Groups[1].Value.Trim('\n')));
            pos = m.Index + m.Length;
        }
        AddText(blocks, s[pos..], trimEnd: false, trimStart: pos > 0);
        return blocks;
    }

    static void AddText(List<Block> blocks, string text, bool trimEnd, bool trimStart)
    {
        if (trimStart && text.StartsWith('\n')) text = text[1..];
        if (trimEnd && text.EndsWith('\n')) text = text[..^1];
        if (text.Length == 0) return;
        LinesBlock? cur = null;
        foreach (var line in text.Split('\n'))
        {
            var quote = line.StartsWith("> ");
            var body = quote ? line[2..] : line;
            if (cur is null || cur.Quote != quote) blocks.Add(cur = new LinesBlock([], quote));
            cur.Lines.Add(ParseInline(body));
        }
    }

    public static List<Span> ParseInline(string line)
    {
        line = OurCharsRe().Replace(line, ""); // znaki zastępcze z wejścia nie mogą udawać wzmianek
        var atoms = new List<Match>();
        var marked = AtomRe().Replace(line, m => { atoms.Add(m); return ((char)(AtomBase + atoms.Count - 1)).ToString(); });
        marked = BoldRe().Replace(marked, $"{BOn}$1{BOff}");
        marked = UnderRe().Replace(marked, $"{UOn}$1{UOff}");
        marked = ItalicRe().Replace(marked, $"$1{IOn}$2{IOff}");
        marked = StrikeRe().Replace(marked, $"{SOn}$1{SOff}");
        marked = SpoilerRe().Replace(marked, $"{POn}$1{POff}");

        var spans = new List<Span>();
        var style = TextStyle.None;
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { spans.Add(new TextSpan(sb.ToString(), style)); sb.Clear(); } }
        foreach (var ch in marked)
        {
            switch (ch)
            {
                case BOn: Flush(); style |= TextStyle.Bold; continue;
                case BOff: Flush(); style &= ~TextStyle.Bold; continue;
                case IOn: Flush(); style |= TextStyle.Italic; continue;
                case IOff: Flush(); style &= ~TextStyle.Italic; continue;
                case UOn: Flush(); style |= TextStyle.Underline; continue;
                case UOff: Flush(); style &= ~TextStyle.Underline; continue;
                case SOn: Flush(); style |= TextStyle.Strike; continue;
                case SOff: Flush(); style &= ~TextStyle.Strike; continue;
                case POn: Flush(); style |= TextStyle.Spoiler; continue;
                case POff: Flush(); style &= ~TextStyle.Spoiler; continue;
            }
            var ai = ch - AtomBase;
            if (ai >= 0 && ai < atoms.Count)
            {
                Flush();
                spans.Add(Atom(atoms[ai], style));
                continue;
            }
            sb.Append(ch);
        }
        Flush();
        return spans;
    }

    static Span Atom(Match m, TextStyle st)
    {
        if (m.Groups["code"].Success) return new CodeSpan(m.Value[1..^1], st);
        static long Id(Group g) => long.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1;
        if (m.Groups["user"].Success) return Id(m.Groups["uid"]) is var u and >= 0 ? new MentionSpan(u, st) : new TextSpan(m.Value, st);
        if (m.Groups["emoji"].Success) return Id(m.Groups["eid"]) is var e and >= 0 ? new EmojiSpan(e, m.Groups["en"].Value, m.Groups["an"].Value == "a", st) : new TextSpan(m.Value, st);
        if (m.Groups["role"].Success) return Id(m.Groups["rid"]) is var r and >= 0 ? new RoleMentionSpan(r, st) : new TextSpan(m.Value, st);
        if (m.Groups["every"].Success) return new EveryoneSpan(st);
        return new LinkSpan(m.Value, st);
    }

    /// <summary>Tekst do podglądu (powiadomienie, odpowiedź): bez znaczników.</summary>
    public static string Plain(string? src, Func<long, string> userName, Func<long, string?> roleName)
    {
        if (string.IsNullOrEmpty(src)) return "";
        var sb = new StringBuilder();
        foreach (var b in Parse(src))
        {
            if (sb.Length > 0) sb.Append(' ');
            if (b is CodeBlock cb) { sb.Append(cb.Code.Replace('\n', ' ')); continue; }
            var lb = (LinesBlock)b;
            for (var i = 0; i < lb.Lines.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                foreach (var sp in lb.Lines[i])
                    sb.Append(sp switch
                    {
                        TextSpan t => t.Text,
                        MentionSpan mu => userName(mu.UserId),
                        RoleMentionSpan r => "@" + (roleName(r.RoleId) ?? "rola"),
                        EveryoneSpan => "@wszyscy",
                        EmojiSpan e => ":" + e.Name + ":",
                        LinkSpan l => l.Url,
                        CodeSpan c => c.Code,
                        _ => "",
                    });
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Wiadomość z samych emoji (do 48 znaków) — pokazujemy je duże.</summary>
    public static bool IsBigEmoji(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var t = Regex.Replace(content, @"<a?:[a-z0-9_]{2,32}:[0-9]+>", "⭐");
        if (t.Length > 48) return false;
        var any = false;
        foreach (var r in t.EnumerateRunes())
        {
            var v = r.Value;
            if (Rune.IsWhiteSpace(r) || v == 0x200D || v == 0xFE0F || v == 0x20E3 || (v >= 0x1F3FB && v <= 0x1F3FF) || (v >= 0xE0020 && v <= 0xE007F)) continue;
            if (IsPictographic(v)) { any = true; continue; }
            return false;
        }
        return any;
    }

    static bool IsPictographic(int v) =>
        (v >= 0x1F000 && v <= 0x1FAFF) || (v >= 0x2600 && v <= 0x27BF) || (v >= 0x2B00 && v <= 0x2BFF) || (v >= 0x2190 && v <= 0x21FF) ||
        (v >= 0x2300 && v <= 0x23FF) || (v >= 0x25A0 && v <= 0x25FF) || (v >= 0x2900 && v <= 0x297F) || (v >= 0x1F1E6 && v <= 0x1F1FF) ||
        v is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x24C2;

    [GeneratedRegex(@"^\[[^\]\n]{1,40}\]$")] private static partial Regex ChipRe();
    [GeneratedRegex(@"^[-•*]\s+(.*)$")] private static partial Regex BulletRe();

    public static AnnCard? ParseAnnouncement(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        string? chip = null;
        if (ChipRe().IsMatch(lines[0].Trim())) { chip = lines[0].Trim()[1..^1]; lines.RemoveAt(0); }
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        if (lines.Count == 0) return null;
        var title = lines[0].TrimStart('#').Trim();
        lines.RemoveAt(0);
        var items = new List<(bool, string)>();
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.Length == 0) continue;
            var b = BulletRe().Match(t);
            items.Add(b.Success ? (true, b.Groups[1].Value) : (false, t));
        }
        return new AnnCard(chip, title, items);
    }

    /// <summary>Pierwsze 2 linki do YouTube (podgląd filmu).</summary>
    public static List<string> YouTubeIds(string? text)
    {
        var ids = new List<string>();
        if (string.IsNullOrEmpty(text)) return ids;
        foreach (Match m in Regex.Matches(text, @"https?://(?:www\.|m\.)?(?:youtube\.com/(?:watch\?(?:[^\s#]*&)?v=|shorts/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})"))
        {
            if (!ids.Contains(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
            if (ids.Count == 2) break;
        }
        return ids;
    }
}
