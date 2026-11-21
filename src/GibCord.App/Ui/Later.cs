using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace GibCord.App.Ui;

/// <summary>
/// „Wyślij później” (nowy serwer, funkcja „scheduled”): menu z szybkimi terminami, okno wyboru daty i godziny,
/// pasek nad polem pisania z zaplanowanymi na tym kanale (wyślij teraz, zmień czas, popraw treść, usuń).
/// Wysyła serwer — także gdy komputer jest wyłączony.
/// </summary>
static class Later
{
    /// <summary>„dziś o 21:00”, „jutro o 8:00”, „w poniedziałek o 8:00”, „24.11 o 8:00”.</summary>
    public static string When(long ms)
    {
        var t = Format.Local(ms);
        var days = (t.Date - DateTime.Now.Date).Days;
        var h = t.ToString("H:mm", Format.Pl);
        return days switch
        {
            0 => "dziś o " + h,
            1 => "jutro o " + h,
            < 7 and > 1 => (t.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Wednesday ? "we " : "w ") + t.ToString("dddd", Format.Pl) + " o " + h,
            _ => t.ToString("d.MM", Format.Pl) + " o " + h,
        };
    }

    static DateTime At(DateTime day, int hour) => day.Date.AddHours(hour);

    /// <summary>Menu terminów. <paramref name="pick"/> dostaje wybrany czas (lokalny).</summary>
    public static MenuFlyout Menu(Shell sh, Action<DateTimeOffset> pick, string? header = null)
    {
        var m = new MenuFlyout();
        if (header is not null) m.Items.Add(new MenuFlyoutItem { Text = header, IsEnabled = false });
        var now = DateTime.Now;
        var opts = new List<(string, DateTime)>();
        var inHour = now.AddMinutes(60 - now.Minute % 15).AddSeconds(-now.Second);
        opts.Add(($"Za godzinę ({inHour:H:mm})", inHour));
        if (now.Hour < 19) opts.Add(("Dziś wieczorem o 20:00", At(now, 20)));
        opts.Add(("Jutro o 8:00", At(now.AddDays(1), 8)));
        if (now.DayOfWeek is not DayOfWeek.Sunday and not DayOfWeek.Saturday)
        {
            var sat = now.AddDays(((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7);
            opts.Add(("W sobotę o 10:00", At(sat, 10)));
        }
        var toMon = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        var mon = now.AddDays(toMon > 1 ? toMon : toMon + 7); // jutro poniedziałek → „jutro o 8:00” już jest
        opts.Add(("W poniedziałek o 8:00", At(mon, 8)));
        foreach (var (label, when) in opts) m.Items.Add(M.Item(label, "", () => pick(new DateTimeOffset(when))));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(M.Item("Wybierz datę i godzinę…", "", async () => { if (await Pick(sh, null, "Wyślij później") is { } w) pick(w); }));
        return m;
    }

    /// <summary>Okno z datą i godziną. Zwraca null przy anulowaniu.</summary>
    public static async Task<DateTimeOffset?> Pick(Shell sh, DateTimeOffset? initial, string title)
    {
        await M.WaitNoDialog(sh.XamlRoot);
        var start = initial?.LocalDateTime ?? DateTime.Now.AddHours(1);
        if (initial is null) start = start.AddMinutes(-start.Minute).AddMinutes(start.Minute >= 30 ? 60 : 30).AddSeconds(-start.Second);
        var date = new CalendarDatePicker { Date = start.Date, MinDate = DateTimeOffset.Now.Date, MaxDate = DateTimeOffset.Now.Date.AddDays(30), HorizontalAlignment = HorizontalAlignment.Stretch };
        var time = new TimePicker { Time = start.TimeOfDay, ClockIdentifier = "24HourClock", MinuteIncrement = 5 };
        var hint = U.T("Wiadomość wyśle serwer, nawet gdy komputer będzie wyłączony. Najdalej za 30 dni.", 12.5, null, Theme.Mu, wrap: true);
        var d = new ContentDialog
        {
            Title = title,
            Content = U.V(10, new Grid { ColumnSpacing = 8 }.Cols("*", "auto").Add(date.At(0, 0), time.At(0, 1)), hint).With(p => p.Width = 380),
            PrimaryButtonText = "Zaplanuj", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary || date.Date is not { } day) return null;
        var when = new DateTimeOffset(day.Date + time.Time);
        if (when < DateTimeOffset.Now.AddSeconds(30)) { sh.Info("Wybierz czas w przyszłości.", true); return null; }
        return when;
    }

    /// <summary>Pasek „Zaplanowane: N” nad polem pisania; po kliknięciu lista z akcjami.</summary>
    public sealed class Bar
    {
        readonly ChatView _v;
        public Border Root { get; } = new();
        public bool Visible => Root.Visibility == Visibility.Visible;
        readonly TextBlock _text = U.T("", 12.5, null, Theme.Mu).With(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; });
        readonly TextBlock _fail = U.T("", 12.5, U.Semi, Theme.Red2);

        public Bar(ChatView v)
        {
            _v = v;
            Root.CornerRadius = new CornerRadius(12, 12, 0, 0);
            Root.Background = Theme.AccentA(.08);
            Root.BorderBrush = Theme.AccentA(.25);
            Root.BorderThickness = U.Th(1, 1, 1, 0);
            Root.Padding = U.Th(14, 6, 8, 6);
            Root.Visibility = Visibility.Collapsed;
            var show = U.Ghost("Pokaż", Open, 28);
            Root.Child = new Grid().Cols("auto", "*", "auto").Add(U.I("", 13, Theme.Accent).With(i => i.Margin = U.Th(0, 0, 8, 0)).At(0, 0),
                new Grid { ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center }.Cols("*", "auto").Add(_text.At(0, 0), _fail.At(0, 1)).At(0, 1), show.At(0, 2));
        }

        public void Refresh()
        {
            var list = _v.Sh.S.ScheduledFor(_v.Key).ToList();
            Root.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count == 0) return;
            var next = list.Where(s => !s.Failed).OrderBy(s => s.SendAt).FirstOrDefault();
            _text.Text = (list.Count == 1 ? "Zaplanowana" : $"Zaplanowane ({list.Count})") + (next is null ? "" : " · " + When(next.SendAt));
            var failed = list.Count(s => s.Failed);
            _fail.Text = failed > 0 ? $"nie wysłano: {failed}" : "";
        }

        void Open()
        {
            var list = _v.Sh.S.ScheduledFor(_v.Key).ToList();
            if (list.Count == 0) return;
            var panel = U.V(8);
            panel.Width = 400;
            panel.Children.Add(U.T("Zaplanowane na tym kanale", 14, U.Semi));
            var fly = new Flyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
            foreach (var s in list) panel.Children.Add(Item(s, fly));
            fly.Content = new ScrollViewer { Content = panel, MaxHeight = 420 };
            fly.ShowAt(Root);
        }

        FrameworkElement Item(ScheduledMessage s, Flyout fly)
        {
            var c = _v.Sh.C;
            var sh = _v.Sh;
            var when = U.T(s.Failed ? "Nie wysłano" : When(s.SendAt), 12.5, U.Semi, s.Failed ? Theme.Red2 : Theme.Accent);
            var text = U.T(s.Content.Length > 0 ? s.Content : "(bez tekstu)", 13.5, null, null, wrap: true).With(t => { t.MaxLines = 4; t.TextTrimming = TextTrimming.CharacterEllipsis; });
            var files = s.Attachments.Count > 0 ? U.T("📎 " + string.Join(", ", s.Attachments.Select(a => a.Name)), 12, null, Theme.Mu) : null;
            var err = s.Failed && s.Error is { } e ? U.T(e, 12, null, Theme.Red2, wrap: true) : null;
            async void Run(Func<Task<string?>> f, string done) { fly.Hide(); var r = await f(); sh.Info(r ?? done, r is not null); }
            var now = U.Ghost("Wyślij teraz", () => Run(() => c.SendScheduledNow(s.Id), "Wysłano"), 28);
            var time = U.Ghost(s.Failed ? "Spróbuj ponownie" : "Zmień czas", () => { }, 28);
            time.Click += (_, _) => Menu(sh, w => Run(() => c.RescheduleMessage(s.Id, w), "Zaplanowano na " + When(w.ToUnixTimeMilliseconds())), "Nowy czas wysyłki").ShowAt(time);
            var edit = U.Ghost("Popraw", async () =>
            {
                fly.Hide();
                await M.WaitNoDialog(sh.XamlRoot);
                var box = new TextBox { Text = s.Content, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 4000, Width = 380 };
                var d = new ContentDialog { Title = "Popraw zaplanowaną wiadomość", Content = box, PrimaryButtonText = "Zapisz", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, RequestedTheme = ElementTheme.Dark };
                if (await d.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() == s.Content) return;
                var r = await c.EditScheduled(s.Id, box.Text.Trim());
                sh.Info(r ?? "Zapisano", r is not null);
            }, 28);
            var del = U.IconButton("", "Usuń zaplanowaną wiadomość", () => Run(() => c.DeleteScheduled(s.Id), "Usunięto"), 13, 28, Theme.Red2);
            var actions = U.H(6, now, time, edit, del);
            return U.Box(U.V(5, when, text, files, err, actions), U.W(.05), 10, U.Th(12, 10), s.Failed ? U.Alpha("#ff4d6a", .4) : U.W(.08), 1);
        }
    }
}
