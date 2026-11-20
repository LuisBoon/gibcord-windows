using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GibCord.App.Ui;

/// <summary>Skrzynka w panelu z prawej: Wzmianki · Zapisane · Przypomnienia. Klik = skok do wiadomości.</summary>
public sealed class InboxPanel : Grid, IDisposable
{
    readonly Shell _sh;
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly StackPanel _list = new() { Spacing = 6, Padding = U.Th(12, 4, 12, 16) };
    string _tab;

    Store S => _sh.S;

    public InboxPanel(Shell sh, string tab)
    {
        _sh = sh;
        _tab = tab;
        this.Rows("auto", "auto", "*");
        var head = new Grid { Padding = U.Th(14, 10, 8, 4) }.Cols("auto", "*", "auto").Add(
            U.I("", 16, Theme.Accent).With(i => i.Margin = U.Th(0, 0, 10, 0)).At(0, 0), U.T("Skrzynka", 16, U.Bold).At(0, 1),
            U.IconButton(G.Close, "Zamknij (Esc)", sh.CloseSidePanel, 14, 32).At(0, 2));
        this.Add(head.At(0), U.Scroll2(_tabs).With(t => t.Margin = U.Th(12, 4, 12, 8)).At(1), U.Scroll(_list).At(2));
        Inbox.Changed += Draw;
        if (_tab == "mentions") Inbox.MarkSeen();
        Draw();
    }

    public void Dispose() => Inbox.Changed -= Draw;

    void Draw()
    {
        _tabs.Children.Clear();
        void Tab(string key, string label)
        {
            var on = _tab == key;
            var b = new Button { Content = U.T(label, 12.5, U.Bold, on ? Theme.OnAccent : Theme.Fg), Background = on ? Theme.Accent : U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(12), Padding = U.Th(12, 4) };
            b.Click += (_, _) => { _tab = key; if (key == "mentions") Inbox.MarkSeen(); Draw(); };
            _tabs.Add(b);
        }
        Tab("mentions", $"@ Wzmianki · {Inbox.Mentions.Count}");
        Tab("saved", $"🔖 Zapisane · {Inbox.Saved.Count}");
        Tab("reminders", $"⏰ Przypomnienia · {Inbox.Reminders.Count}");

        _list.Children.Clear();
        var items = _tab switch { "saved" => Inbox.Saved, "reminders" => Inbox.Reminders, _ => Inbox.Mentions };
        if (items.Count == 0)
        {
            _list.Add(U.T(_tab switch
            {
                "saved" => "Nic tu nie ma. Zapisz wiadomość z jej menu (prawy przycisk → „Zapisz wiadomość”), żeby łatwo do niej wrócić.",
                "reminders" => "Brak przypomnień. Prawy przycisk na wiadomości → „Przypomnij mi” — GibCord da znać o wybranej porze.",
                _ => "Gdy ktoś Cię oznaczy (@Ty, @rola albo @wszyscy), wiadomość trafi tutaj — także z kanałów, których nie czytasz.",
            }, 13, null, Theme.Mu, wrap: true).With(t => t.Margin = U.Th(4, 8)));
            return;
        }
        if (_tab == "mentions")
            _list.Add(U.Ghost("Wyczyść wzmianki", () => Inbox.ClearMentions(), 28).With(b => b.HorizontalAlignment = HorizontalAlignment.Right));
        foreach (var it in items.ToList()) _list.Add(Card(it));
    }

    FrameworkElement Card(InboxItem it)
    {
        var srv = S.ServerOfChannel(it.ChannelId);
        var top = U.H(8, U.T(S.NameOf(it.AuthorId, srv), 13, U.Bold, U.B(S.NameColor(it.AuthorId, srv))), U.T(Format.When(it.At), 11, null, Theme.Mu2),
            !it.Seen && _tab == "mentions" ? new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Red } : null);
        var body = U.V(3, U.T(it.Where, 11, U.Bold, Theme.Mu2), top, U.T(string.IsNullOrWhiteSpace(it.Text) ? "(załącznik)" : it.Text, 13, null, Theme.Fg, wrap: true),
            _tab == "reminders" ? U.T("⏰ " + Format.When(it.Due), 12, U.Semi, Theme.Yellow) : null);
        var g = new Grid().Cols("40", "*", "auto").Add(new Avatar(30).Set(S, _sh.C.Api, it.AuthorId, srv).With(a => a.VerticalAlignment = VerticalAlignment.Top).At(0, 0), body.At(0, 1),
            U.IconButton(G.Close, _tab == "reminders" ? "Anuluj przypomnienie" : "Usuń ze skrzynki", () => Inbox.Remove(it), 10, 26).With(b => b.VerticalAlignment = VerticalAlignment.Top).At(0, 2));
        var card = new Border { Child = g, Background = Theme.Card, CornerRadius = new CornerRadius(10), Padding = U.Th(10, 8), BorderBrush = Theme.Line, BorderThickness = U.Th(1) };
        card.PointerEntered += (_, _) => card.Background = U.W(.08);
        card.PointerExited += (_, _) => card.Background = Theme.Card;
        card.Tapped += (_, _) => _sh.JumpToMessage(it.ChannelId, it.MessageId, it.ThreadId);
        card.Tip("Przejdź do wiadomości");
        return card;
    }
}
