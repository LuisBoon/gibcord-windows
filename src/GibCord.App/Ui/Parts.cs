using GibCord.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Text;
using Span = GibCord.Core.Span;

namespace GibCord.App.Ui;

/// <summary>Awatar: zdjęcie albo pierwsza litera na kolorze osoby, z kropką dostępności.</summary>
public sealed class Avatar : Grid
{
    readonly Ellipse _bg = new();
    readonly TextBlock _letter;
    readonly Ellipse _img = new() { Visibility = Visibility.Collapsed };
    readonly Ellipse _dot = new();
    readonly Border _dotWrap;
    readonly double _size;

    public Avatar(double size, bool withStatus = false, string ring = "#241c3b")
    {
        _size = size;
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        _letter = U.T("", Math.Round(size * .42), FontWeights.Bold, Theme.OnAccent);
        _letter.HorizontalAlignment = HorizontalAlignment.Center;
        _letter.TextTrimming = TextTrimming.None;
        Children.Add(_bg);
        Children.Add(_letter);
        Children.Add(_img);
        var d = Math.Max(10, Math.Round(size * .32));
        _dot.Width = _dot.Height = d;
        _dotWrap = new Border
        {
            Child = _dot, Width = d + 5, Height = d + 5, CornerRadius = new CornerRadius((d + 5) / 2), Background = U.B(ring),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2.5, -2.5),
            Padding = new Thickness(2.5), Visibility = withStatus ? Visibility.Visible : Visibility.Collapsed,
        };
        Children.Add(_dotWrap);
    }

    public Avatar Set(Store s, GibCordApi api, long uid, Server? srv = null, bool status = false)
    {
        var u = s.U(uid);
        SetRaw(s.NameOf(uid, srv), Store.ColorOfUser(u), api.Abs(s.AvatarOf(uid, srv)), status ? s.StatusOf(uid) : null);
        return this;
    }

    public Avatar SetRaw(string name, string color, Uri? image, string? status)
    {
        _bg.Fill = U.B(color);
        _letter.Text = name.Length > 0 ? char.ToUpper(name.EnumerateRunes().First().ToString()[0]).ToString() : "?";
        if (name.Length > 0 && char.IsSurrogate(name[0])) _letter.Text = name.EnumerateRunes().First().ToString();
        if (image is not null)
        {
            _img.Fill = new ImageBrush { ImageSource = Images.Get(image, (int)(_size * 2)), Stretch = Stretch.UniformToFill };
            _img.Visibility = Visibility.Visible;
        }
        else _img.Visibility = Visibility.Collapsed;
        if (status is not null)
        {
            _dot.Fill = Theme.StatusBrush(status);
            _dotWrap.Visibility = Visibility.Visible;
        }
        else _dotWrap.Visibility = Visibility.Collapsed;
        return this;
    }

    public bool Talking { set { _bg.Stroke = value ? Theme.Green : null; _bg.StrokeThickness = value ? 2 : 0; } }
}

/// <summary>Treść wiadomości: formatowanie z <see cref="Markup"/> zamienione na tekst WinUI.</summary>
public static class RichText
{
    public static readonly Brush LinkBrush = U.B("#8fd3ff");
    static readonly Brush CodeFg = U.B("#ffd2a8");
    static readonly Brush SpoilerBg = U.B("#3d3358");

    public sealed record Ctx(Store S, GibCordApi Api, Server? Srv, Action<long>? OnUser, double Size = 14.5);

    public static FrameworkElement Build(string content, Ctx c, bool edited = false)
    {
        var big = Markup.IsBigEmoji(content);
        var blocks = Markup.Parse(content);
        var panel = new StackPanel { Spacing = 4 };
        for (var i = 0; i < blocks.Count; i++)
        {
            var last = i == blocks.Count - 1;
            switch (blocks[i])
            {
                case CodeBlock cb:
                    panel.Children.Add(new Border
                    {
                        Background = Theme.Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                        Child = new TextBlock { Text = cb.Code, FontFamily = Theme.Mono, FontSize = 13, Foreground = Theme.Fg, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    });
                    if (last && edited) panel.Children.Add(U.T("(edytowano)", 11, null, Theme.Mu2));
                    break;
                case LinesBlock lb:
                    var rtb = Lines(lb, c, big, last && edited);
                    if (lb.Quote)
                        panel.Children.Add(new Grid().Cols("4", "*").Add(
                            new Border { Background = U.W(.3), CornerRadius = new CornerRadius(2) }.At(0, 0),
                            rtb.With(r => r.Margin = new Thickness(10, 0, 0, 0)).At(0, 1)));
                    else panel.Children.Add(rtb);
                    break;
            }
        }
        if (panel.Children.Count == 1) { var only = (FrameworkElement)panel.Children[0]; panel.Children.Clear(); return only; }
        return panel;
    }

    static RichTextBlock Lines(LinesBlock lb, Ctx c, bool big, bool edited)
    {
        var rtb = new RichTextBlock
        {
            FontFamily = Theme.Font, FontSize = big ? 40 : c.Size, Foreground = Theme.Fg, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, LineHeight = big ? 48 : c.Size * 1.45, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        var p = new Paragraph();
        for (var i = 0; i < lb.Lines.Count; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            foreach (var sp in lb.Lines[i]) p.Inlines.Add(Inline(sp, c, big));
        }
        if (edited) p.Inlines.Add(new Run { Text = "  (edytowano)", FontSize = 11, Foreground = Theme.Mu2 });
        rtb.Blocks.Add(p);
        return rtb;
    }

    static Inline Styled(Inline i, TextStyle st)
    {
        if (st.HasFlag(TextStyle.Bold)) i.FontWeight = FontWeights.Bold;
        if (st.HasFlag(TextStyle.Italic)) i.FontStyle = FontStyle.Italic;
        var deco = TextDecorations.None;
        if (st.HasFlag(TextStyle.Underline)) deco |= TextDecorations.Underline;
        if (st.HasFlag(TextStyle.Strike)) deco |= TextDecorations.Strikethrough;
        if (deco != TextDecorations.None) i.TextDecorations = deco;
        return i;
    }

    static Inline Inline(Span sp, Ctx c, bool big)
    {
        if (sp.Style.HasFlag(TextStyle.Spoiler)) return Spoiler(sp, c);
        switch (sp)
        {
            case TextSpan t: return Styled(new Run { Text = t.Text }, t.Style);
            case MentionSpan m:
            {
                var h = new Hyperlink { UnderlineStyle = UnderlineStyle.None, Foreground = Theme.MentionFg, FontWeight = FontWeights.SemiBold };
                h.Inlines.Add(new Run { Text = "@" + c.S.NameOf(m.UserId, c.Srv) });
                h.Click += (_, _) => c.OnUser?.Invoke(m.UserId);
                return h;
            }
            case RoleMentionSpan r:
            {
                var role = c.S.RoleById(r.RoleId);
                return new Run { Text = "@" + (role?.Name ?? "rola"), FontWeight = FontWeights.SemiBold, Foreground = role?.Color is { } col ? U.B(col) : Theme.MentionFg };
            }
            case EveryoneSpan: return new Run { Text = "@wszyscy", FontWeight = FontWeights.SemiBold, Foreground = Theme.MentionFg };
            case EmojiSpan e:
            {
                var em = c.S.EmojiById(e.Id);
                if (em is null) return new Run { Text = ":" + e.Name + ":" };
                var size = big ? 44 : c.Size * 1.45;
                var img = new Image { Source = Images.Get(c.Api.Abs(em.Url), (int)(size * 2)), Width = size, Height = size, Margin = new Thickness(1, 0, 1, big ? 0 : -5) };
                ToolTipService.SetToolTip(img, ":" + e.Name + ":");
                return new InlineUIContainer { Child = img };
            }
            case LinkSpan l:
            {
                var h = new Hyperlink { Foreground = LinkBrush, UnderlineStyle = UnderlineStyle.None };
                if (Uri.TryCreate(l.Url, UriKind.Absolute, out var u)) h.NavigateUri = u;
                h.Inlines.Add(Styled(new Run { Text = l.Url }, l.Style));
                ToolTipService.SetToolTip(h, l.Url);
                return h;
            }
            case CodeSpan code: return new Run { Text = code.Code, FontFamily = Theme.Mono, Foreground = CodeFg, FontSize = c.Size - 1 };
        }
        return new Run();
    }

    /// <summary>||spoiler|| — zakryty, odsłania się po kliknięciu.</summary>
    static Inline Spoiler(Span sp, Ctx c)
    {
        var text = sp switch { TextSpan t => t.Text, MentionSpan m => "@" + c.S.NameOf(m.UserId, c.Srv), LinkSpan l => l.Url, CodeSpan k => k.Code, EmojiSpan e => ":" + e.Name + ":", _ => "…" };
        var tb = new TextBlock { Text = text, FontFamily = Theme.Font, FontSize = c.Size, Foreground = Theme.Fg, Opacity = 0 };
        var b = new Border { Child = tb, Background = SpoilerBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(0, 0, 0, -5) };
        ToolTipService.SetToolTip(b, "Spoiler — kliknij, żeby zobaczyć");
        b.Tapped += (_, e) => { tb.Opacity = 1; b.Background = U.W(.1); ToolTipService.SetToolTip(b, null); e.Handled = true; };
        return new InlineUIContainer { Child = b };
    }
}
