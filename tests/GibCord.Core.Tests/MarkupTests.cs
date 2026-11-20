using GibCord.Core;

namespace GibCord.Core.Tests;

public class MarkupTests
{
    static List<Span> Line(string s) => ((LinesBlock)Markup.Parse(s)[0]).Lines[0];

    [Fact]
    public void ZwyklyTekst()
    {
        var sp = Line("hej wszystkim");
        Assert.Equal([new TextSpan("hej wszystkim", TextStyle.None)], sp);
    }

    [Fact]
    public void PogrubienieKursywaPrzekreslenie()
    {
        var sp = Line("a **gruby** i *krzywy* i ~~nie~~ i __pod__ i ||sekret||");
        Assert.Contains(new TextSpan("gruby", TextStyle.Bold), sp);
        Assert.Contains(new TextSpan("krzywy", TextStyle.Italic), sp);
        Assert.Contains(new TextSpan("nie", TextStyle.Strike), sp);
        Assert.Contains(new TextSpan("pod", TextStyle.Underline), sp);
        Assert.Contains(new TextSpan("sekret", TextStyle.Spoiler), sp);
    }

    [Fact]
    public void GwiazdkaWSrodkuSlowaToNieKursywa()
    {
        var sp = Line("2*3*4");
        Assert.Equal([new TextSpan("2*3*4", TextStyle.None)], sp);
    }

    [Fact]
    public void WzmiankiEmojiLinki()
    {
        var sp = Line("hej <@12> zobacz https://gibcord.luisboon.pl/x. <:gibek:7> <@&3> @everyone");
        Assert.Contains(new MentionSpan(12, TextStyle.None), sp);
        Assert.Contains(new LinkSpan("https://gibcord.luisboon.pl/x", TextStyle.None), sp);
        Assert.Contains(new EmojiSpan(7, "gibek", false, TextStyle.None), sp);
        Assert.Contains(new RoleMentionSpan(3, TextStyle.None), sp);
        Assert.Contains(new EveryoneSpan(TextStyle.None), sp);
        // kropka po linku nie należy do linku
        Assert.Contains(sp, x => x is TextSpan t && t.Text.StartsWith(". "));
    }

    [Fact]
    public void WzmiankaWPogrubieniu()
    {
        var sp = Line("**cześć <@5>!**");
        Assert.Contains(new MentionSpan(5, TextStyle.Bold), sp);
    }

    [Fact]
    public void KodNieJestFormatowany()
    {
        var sp = Line("`**nie gruby**` i **gruby**");
        Assert.Contains(new CodeSpan("**nie gruby**", TextStyle.None), sp);
        Assert.Contains(new TextSpan("gruby", TextStyle.Bold), sp);
    }

    [Fact]
    public void BlokKoduICytat()
    {
        var b = Markup.Parse("przed\n```js\nconst a = 1;\n```\n> cytat\n> dalej\npo");
        Assert.IsType<LinesBlock>(b[0]);
        Assert.Equal("const a = 1;", Assert.IsType<CodeBlock>(b[1]).Code);
        var q = Assert.IsType<LinesBlock>(b[2]);
        Assert.True(q.Quote);
        Assert.Equal(2, q.Lines.Count);
        Assert.False(Assert.IsType<LinesBlock>(b[3]).Quote);
    }

    [Fact]
    public void ZnakiPrywatneSaUsuwane()
    {
        var sp = Line("a\uE101b\uF000c");
        Assert.Equal([new TextSpan("abc", TextStyle.None)], sp);
    }

    [Fact]
    public void TekstDoPodgladu()
    {
        var t = Markup.Plain("**hej** <@1> i <@&2> <:kot:9>", id => "@Ola", id => "Moderatorzy");
        Assert.Equal("hej @Ola i @Moderatorzy :kot:", t);
    }

    [Theory]
    [InlineData("😂", true)]
    [InlineData("👍🏻 ❤️", true)]
    [InlineData("🇵🇱", true)]
    [InlineData("<:gibek:5>", true)]
    [InlineData("hej 😂", false)]
    [InlineData("123", false)]
    public void DuzeEmoji(string s, bool big) => Assert.Equal(big, Markup.IsBigEmoji(s));

    [Fact]
    public void KartaOgloszenia()
    {
        var c = Markup.ParseAnnouncement("[WERSJA 5.10]\nWątki i kolejka\n- 🧵 Wątki: opis\n- drugi\n\nAkapit")!;
        Assert.Equal("WERSJA 5.10", c.Chip);
        Assert.Equal("Wątki i kolejka", c.Title);
        Assert.Equal(3, c.Items.Count);
        Assert.True(c.Items[0].Bullet);
        Assert.False(c.Items[2].Bullet);
    }

    [Fact]
    public void YouTube()
    {
        Assert.Equal(["dQw4w9WgXcQ"], Markup.YouTubeIds("zobacz https://youtu.be/dQw4w9WgXcQ i https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(1, "wiadomość")]
    [InlineData(2, "wiadomości")]
    [InlineData(5, "wiadomości")]
    [InlineData(22, "wiadomości")]
    [InlineData(12, "wiadomości")]
    public void Odmiana(long n, string expect) => Assert.Equal(expect, Format.Plural(n, "wiadomość", "wiadomości", "wiadomości"));

    [Fact]
    public void Osoby()
    {
        Assert.Equal("osoby", Format.Plural(3, "osoba", "osoby", "osób"));
        Assert.Equal("osób", Format.Plural(15, "osoba", "osoby", "osób"));
    }

    [Fact]
    public void Kiedy()
    {
        var now = DateTime.Now;
        var ms = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        Assert.StartsWith("dziś o ", Format.When(ms));
        Assert.StartsWith("wczoraj o ", Format.When(ms - 86400000L));
        Assert.Matches(@"^\d\d\.\d\d\.\d{4} \d\d:\d\d$", Format.When(ms - 5 * 86400000L));
    }

    [Fact]
    public void Rozmiar()
    {
        Assert.Equal("512 B", Format.Size(512));
        Assert.Equal("2,5 MB", Format.Size(2621440));
    }

    [Fact]
    public void ZlosliweWejscieNieWywracaParsera()
    {
        // cyfry spoza ASCII (arabskie, pełnej szerokości) i znaki zastępcze atomów — zwykły tekst, bez wyjątku
        var spans = Markup.ParseInline("<@١٢٣> <:x:１２> <@&٤> <@5>\uF000\uF000");
        Assert.Single(spans.OfType<MentionSpan>());
        Assert.DoesNotContain(spans, s => s is EmojiSpan or RoleMentionSpan);
        var blocks = Markup.Parse("**" + new string('*', 4000) + "** <@99999999999999999999>");
        Assert.NotEmpty(blocks);
    }
}
