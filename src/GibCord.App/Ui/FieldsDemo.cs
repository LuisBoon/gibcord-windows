using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GibCord.App.Ui;

/// <summary>Zrzuty w CI: wszystkie własne pola wyboru w jednym okienku, otwarta lista / kalendarz / godzina, okno plików, menu przy zegarze.</summary>
static class FieldsDemo
{
    public static void Show(Shell sh, string open)
    {
        var drop = new DropField { Header = "Mikrofon", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var x in new[] { "Mikrofon (Realtek Audio)", "Słuchawki HyperX", "Domyślny Windows" }) drop.Items.Add(x);
        drop.SelectedIndex = 0;
        var radio = new RadioField { Header = "Płynność", MaxColumns = 2 };
        radio.Items.Add("30 kl/s");
        radio.Items.Add("60 kl/s (gry)");
        radio.SelectedIndex = 0;
        var date = new DateField { Header = "Dzień", Date = DateTimeOffset.Now.Date.AddDays(1), MinDate = DateTimeOffset.Now.Date };
        var time = new TimeField { Header = "Godzina", Time = new TimeSpan(20, 0, 0), MinuteIncrement = 5 };
        var chips = U.H(6,
            new ChipField { Content = U.H(6, U.I("", 14), U.T("Zamaż", 13)), IsChecked = true },
            new ChipField { Content = U.H(6, U.I("", 14), U.T("Przytnij", 13)) },
            new ChipField { Content = U.H(6, U.I("", 14), U.T("Rysuj", 13)) });
        var body = U.V(16,
            U.V(10, new CheckField { Content = "Dźwięk z komputera", IsChecked = true }, new CheckField { Content = "Pokazuj podgląd linków" }),
            drop,
            new SwitchField { Header = "Szkło Windows 11", IsOn = true, OnContent = "Włączone", OffContent = "Wyłączone" },
            radio,
            new SliderField { Header = "Głośność", Minimum = 0, Maximum = 200, StepFrequency = 5, Value = 120, HorizontalAlignment = HorizontalAlignment.Stretch },
            new Grid { ColumnSpacing = 12 }.Cols("auto", "auto", "*").Add(
                new NumberField { Header = "Limit osób", Minimum = 0, Maximum = 99, Value = 10 }.At(0, 0), date.At(0, 1), time.At(0, 2)),
            chips).With(p => p.Width = 420);
        var d = new AppDialog
        {
            Title = "Pola wyboru GibCorda", Content = body, PrimaryButtonText = "Zapisz", CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = sh.XamlRoot, Glyph = "",
        };
        _ = d.ShowAsync();
        if (open == "") return;
        // lista / kalendarz / godzina otwierają się, gdy okienko już stoi (pozycja pod polem)
        var t = sh.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(900);
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            if (open == "drop") drop.IsDropDownOpen = true;
            if (open == "date") date.OpenDemo();
            if (open == "time") time.OpenDemo();
        };
        t.Start();
    }

    /// <summary>Okno wyboru plików na przykładowym folderze (zdjęcia z miniaturkami, dokumenty, podfoldery).</summary>
    public static void Files(Shell sh)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GibCord-pokaz", "Pobrane");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in new[] { "Zrzuty ekranu", "Projekty" }) Directory.CreateDirectory(Path.Combine(dir, f));
            var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "gibcord.png");
            var i = 0;
            foreach (var n in new[] { "Gibek na plaży.png", "zdjęcie z wakacji.png", "mem dla Oli.png" })
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(logo)) File.Copy(logo, p, true);
                File.SetLastWriteTime(p, DateTime.Now.AddHours(-3 * ++i));
            }
            foreach (var (n, kb) in new[] { ("notatki ze spotkania.txt", 3), ("plan turnieju.pdf", 240), ("muzyka do filmu.mp3", 4100), ("instalator gry.exe", 52000), ("archiwum.zip", 18000) })
            {
                var p = Path.Combine(dir, n);
                using (var fs = File.Create(p)) fs.SetLength(kb * 1024L);
                File.SetLastWriteTime(p, DateTime.Now.AddDays(-++i));
            }
        }
        catch (Exception ex) { App.Log("Pokaz plików: " + ex.Message); }
        FileBrowser.DemoStart(dir);
        _ = FileBrowser.PickFiles(sh.XamlRoot);
    }
}
