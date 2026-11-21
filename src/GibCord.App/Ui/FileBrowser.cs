using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.System;

namespace GibCord.App.Ui;

/// <summary>
/// Własne okno wyboru i zapisu plików (zamiast okna Eksploratora „Otwórz/Zapisz”): miejsca z boku (Pulpit, Pobrane, Dokumenty,
/// Obrazy, Wideo, Muzyka i dyski), lista z miniaturkami zdjęć, szukanie w folderze, sortowanie, nowy folder przy zapisie.
/// Zwraca StorageFile, żeby reszta kodu (wysyłanie, zapisywanie) działała jak z systemowym oknem.
/// </summary>
static class FileBrowser
{
    public enum Place { Desktop, Downloads, Documents, Pictures, Videos, Music }

    static readonly CultureInfo Pl = new("pl-PL");
    static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    /// <summary>Ostatnio otwarty folder (osobno: zdjęcia, inne pliki, zapis) — do końca działania aplikacji.</summary>
    static readonly Dictionary<string, string> Last = [];
    const int MaxRows = 1500;

    public static async Task<StorageFile?> PickFile(XamlRoot root, string[]? exts = null, Place start = Place.Downloads) =>
        (await Run(root, exts, false, start, null)).Files.FirstOrDefault();

    public static async Task<IReadOnlyList<StorageFile>> PickFiles(XamlRoot root, string[]? exts = null, Place start = Place.Downloads) =>
        (await Run(root, exts, true, start, null)).Files;

    /// <summary>Zapis: zwraca pełną ścieżkę wybranego pliku (jeszcze niezapisanego) albo null, gdy anulowano.</summary>
    public static async Task<string?> SaveFile(XamlRoot root, string suggestedName, string ext, Place start = Place.Downloads) =>
        (await Run(root, [ext], false, start, suggestedName)).SavePath;

    /// <summary>Zrzut w CI: start w przykładowym folderze.</summary>
    public static void DemoStart(string dir) { foreach (var k in new[] { "files", "images", "save" }) Last[k] = dir; }

    sealed record Entry(string Path, string Name, bool Dir, DateTime Modified, long Size);

    static async Task<(IReadOnlyList<StorageFile> Files, string? SavePath)> Run(XamlRoot root, string[]? exts, bool multiple, Place start, string? saveName)
    {
        var save = saveName is not null;
        var filter = exts is { Length: > 0 } && !exts.Contains("*") ? exts.Select(e => e.ToLowerInvariant()).ToHashSet() : null;
        var imagesOnly = filter is not null && filter.All(ImageExts.Contains);
        var key = save ? "save" : imagesOnly ? "images" : "files";
        var folder = Last.TryGetValue(key, out var lf) && Directory.Exists(lf) ? lf : PathOf(start);
        var byDate = !save;
        var selected = new List<Entry>();
        List<Entry> entries = [];
        var ver = 0;
        StorageFile[] result = [];
        string? savePath = null;
        // przyciski powstają przed listą — wołają nawigację przez odnośniki ustawione niżej
        Action<string> go = _ => { };
        Action fill = () => { };
        Func<Task> newFolderAsync = () => Task.CompletedTask;

        // ── miejsca ──
        var places = new StackPanel { Spacing = 2 };
        var placeRows = new List<(string Path, Border Row)>();
        void AddPlace(string glyph, string label, string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            var row = U.Row(U.H(10, U.I(glyph, 15, Theme.Accent), U.T(label, 13)), () => go(path), false, 9, U.Th(10, 7), U.Th(0));
            placeRows.Add((path, row));
            places.Children.Add(row);
        }
        AddPlace("", "Pulpit", PathOf(Place.Desktop));
        AddPlace("", "Pobrane", PathOf(Place.Downloads));
        AddPlace("", "Dokumenty", PathOf(Place.Documents));
        AddPlace("", "Obrazy", PathOf(Place.Pictures));
        AddPlace("", "Wideo", PathOf(Place.Videos));
        AddPlace("", "Muzyka", PathOf(Place.Music));
        places.Children.Add(U.HLine(.08).With(l => l.Margin = U.Th(6, 6)));
        foreach (var d in SafeDrives())
        {
            var kind = d.DriveType switch { DriveType.Removable => "Dysk wymienny", DriveType.Network => "Dysk sieciowy", DriveType.CDRom => "Płyta", _ => "Dysk lokalny" };
            var name = string.IsNullOrWhiteSpace(Try(() => d.VolumeLabel)) ? kind : Try(() => d.VolumeLabel)!;
            AddPlace(d.DriveType switch { DriveType.Removable => "", DriveType.Network => "", _ => "" }, $"{name} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName);
        }

        // ── pasek u góry ──
        var crumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var crumbScroll = new ScrollViewer { Content = crumbs, HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var up = U.IconButton("", "Folder wyżej (Backspace)", () => { if (Directory.GetParent(folder) is { } p) go(p.FullName); }, 14, 34);
        up.Background = U.W(.08);
        var search = new TextBox { PlaceholderText = "Szukaj w tym folderze", Width = 200, VerticalAlignment = VerticalAlignment.Center };
        var sort = new Button { Background = U.W(.08), BorderThickness = U.Th(0), CornerRadius = new CornerRadius(10), Height = 34, Padding = U.Th(12, 0) };
        void DrawSort() => sort.Content = U.H(6, U.I(byDate ? "" : "", 13, Theme.Mu), U.T(byDate ? "Najnowsze" : "A–Z", 12.5, U.Semi));
        DrawSort();
        ToolTipService.SetToolTip(sort, "Kolejność: najnowsze u góry albo alfabetycznie");
        sort.Click += (_, _) => { byDate = !byDate; DrawSort(); fill(); };
        Button? newFolder = null;
        if (save)
        {
            newFolder = U.IconButton("", "Nowy folder", () => _ = newFolderAsync(), 14, 34);
            newFolder.Background = U.W(.08);
        }
        var top = new Grid { ColumnSpacing = 8 }.Cols("auto", "*", "auto", "auto", "auto").Add(up.At(0, 0), crumbScroll.At(0, 1), sort.At(0, 2), search.At(0, 3), newFolder?.At(0, 4));

        // ── lista ──
        var list = new StackPanel { Spacing = 1 };
        var listScroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = U.Th(0, 0, 8, 0) };
        var status = U.T("", 13, null, Theme.Mu, wrap: true).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = U.Th(0, 40, 0, 0); });
        var listArea = new Grid { Background = U.W(.03), CornerRadius = new CornerRadius(12), Padding = U.Th(6) }.Add(listScroll, status);
        var rowOf = new Dictionary<Entry, (Border Row, Border Check)>();

        // ── dół ──
        var nameBox = new TextBox { Text = saveName ?? "", PlaceholderText = "Nazwa pliku", VerticalAlignment = VerticalAlignment.Center };
        var info = U.T("", 12.5, null, Theme.Mu);
        var bottom = save
            ? new Grid { ColumnSpacing = 10 }.Cols("auto", "*", "auto").Add(U.T("Nazwa:", 13, U.Semi).At(0, 0), nameBox.At(0, 1), U.T(exts?.FirstOrDefault() ?? "", 13, null, Theme.Mu).At(0, 2))
            : (FrameworkElement)info;

        var body = new Grid { ColumnSpacing = 14, RowSpacing = 10, Width = 780, Height = 470 }.Cols("190", "*").Rows("auto", "*", "auto");
        body.Add(new ScrollViewer { Content = places, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.At(0, 0, rowSpan: 3));
        body.Add(top.At(0, 1));
        body.Add(listArea.At(1, 1));
        body.Add(bottom.At(2, 1));

        var title = save ? "Zapisz jako" : multiple ? "Wybierz pliki" : imagesOnly ? "Wybierz obrazek" : "Wybierz plik";
        var dlg = new AppDialog
        {
            Title = title, Content = body, PrimaryButtonText = save ? "Zapisz" : multiple ? "Dodaj" : "Wybierz", CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = root, MaxWidth = 860, Glyph = save ? "" : "", Danger = false,
            IsPrimaryButtonEnabled = save && nameBox.Text.Trim().Length > 0,
        };
        nameBox.TextChanged += (_, _) => dlg.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
        search.TextChanged += (_, _) => Fill();
        dlg.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Back && e.OriginalSource is not TextBox && Directory.GetParent(folder) is { } p) { e.Handled = true; Go(p.FullName); }
        };
        dlg.PrimaryButtonClick += async (_, e) =>
        {
            if (save)
            {
                var def = e.GetDeferral();
                try
                {
                    var name = string.Concat(nameBox.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    var ext = exts?.FirstOrDefault() ?? "";
                    if (ext.Length > 0 && !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
                    var path = Path.Combine(folder, name);
                    if (File.Exists(path))
                    {
                        var ok = await new AppDialog
                        {
                            Title = "Zastąpić plik?", Content = $"W tym folderze jest już „{name}”. Zastąpić go nowym?", PrimaryButtonText = "Zastąp", CloseButtonText = "Anuluj",
                            DefaultButton = ContentDialogButton.Close, Danger = true, Glyph = "", XamlRoot = root,
                        }.ShowAsync() == ContentDialogResult.Primary;
                        if (!ok) { e.Cancel = true; return; }
                    }
                    savePath = path;
                }
                catch (Exception ex) { e.Cancel = true; Toast(ex); }
                finally { def.Complete(); }
                return;
            }
            // jeden zaznaczony folder + Enter/„Wybierz” = wejdź do folderu
            if (selected.Count == 1 && selected[0].Dir) { e.Cancel = true; Go(selected[0].Path); return; }
            var files = selected.Where(x => !x.Dir).ToList();
            if (files.Count == 0) { e.Cancel = true; return; }
            var d2 = e.GetDeferral();
            try
            {
                var list2 = new List<StorageFile>();
                foreach (var f in files) list2.Add(await StorageFile.GetFileFromPathAsync(f.Path));
                result = [.. list2];
            }
            catch (Exception ex) { e.Cancel = true; Toast(ex); }
            finally { d2.Complete(); }
        };

        void Toast(Exception ex)
        {
            info.Text = ex is UnauthorizedAccessException ? "Brak dostępu do tego pliku." : "Nie udało się: " + ex.Message;
            info.Foreground = Theme.Red;
            if (save) App.Window?.CurrentShell?.Info(info.Text, true);
        }

        void DrawSelection()
        {
            foreach (var (en, (row, check)) in rowOf)
            {
                var on = selected.Contains(en);
                row.Background = on ? Theme.AccentA(.16) : Theme.Transparent;
                check.Background = on ? Theme.AccentGradient() : Theme.Transparent;
                check.BorderBrush = on ? Theme.Transparent : U.W(.3);
                check.Child = on ? U.I("", 10, Theme.OnAccent).With(i => { i.FontWeight = U.Bold; i.HorizontalAlignment = HorizontalAlignment.Center; }) : null;
            }
            if (save) return;
            var files = selected.Where(x => !x.Dir).ToList();
            dlg.IsPrimaryButtonEnabled = files.Count > 0 || selected.Count == 1 && selected[0].Dir;
            info.Foreground = Theme.Mu;
            info.Text = files.Count switch
            {
                0 => multiple ? "Kliknij pliki, które chcesz dodać (możesz kilka)." : "Kliknij plik, a potem „Wybierz” — albo kliknij go dwa razy.",
                1 => $"{files[0].Name} · {SizeText(files[0].Size)}",
                var n => $"Zaznaczono {n} {Plural(n, "plik", "pliki", "plików")} · {SizeText(files.Sum(f => f.Size))}",
            };
        }

        void Click(Entry en)
        {
            if (save)
            {
                if (!en.Dir) nameBox.Text = en.Name;
                return;
            }
            if (multiple && !en.Dir)
            {
                selected.RemoveAll(x => x.Dir);
                if (!selected.Remove(en)) selected.Add(en);
            }
            else { selected.Clear(); selected.Add(en); }
            DrawSelection();
        }

        void Open(Entry en)
        {
            if (en.Dir) { Go(en.Path); return; }
            if (save) { nameBox.Text = en.Name; return; }
            if (!selected.Contains(en)) { if (!multiple) selected.Clear(); selected.Add(en); }
            DrawSelection();
            // podwójne kliknięcie pliku = od razu „Wybierz”
            dlg.IsPrimaryButtonEnabled = true;
            dlg.PressPrimary();
        }

        void Fill()
        {
            list.Children.Clear();
            rowOf.Clear();
            var q = search.Text.Trim();
            IEnumerable<Entry> shown = entries.Where(x => x.Dir || filter is null || filter.Contains(Path.GetExtension(x.Name).ToLowerInvariant()));
            if (q.Length > 0) shown = shown.Where(x => x.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
            var cmp = StringComparer.Create(Pl, true);
            shown = byDate
                ? shown.OrderByDescending(x => x.Dir).ThenByDescending(x => x.Modified)
                : shown.OrderByDescending(x => x.Dir).ThenBy(x => x.Name, cmp);
            var all = shown.ToList();
            var thumbs = new List<(Entry, Border)>();
            foreach (var en in all.Take(MaxRows))
            {
                var isImg = !en.Dir && ImageExts.Contains(Path.GetExtension(en.Name).ToLowerInvariant());
                var icon = new Border
                {
                    Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Background = en.Dir ? Theme.AccentA(.14) : U.W(.06),
                    Child = U.I(en.Dir ? "" : GlyphOf(en.Name), 16, en.Dir ? Theme.Accent : Theme.Mu).With(i => i.HorizontalAlignment = HorizontalAlignment.Center),
                };
                if (isImg) thumbs.Add((en, icon));
                var check = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = U.Th(1.5), BorderBrush = U.W(.3), Visibility = multiple && !en.Dir ? Visibility.Visible : Visibility.Collapsed };
                var grid = new Grid { ColumnSpacing = 12 }.Cols("auto", "auto", "*", "auto", "auto").Add(
                    check.At(0, 0), icon.At(0, 1),
                    U.T(en.Name, 13.5, en.Dir ? U.Semi : null).At(0, 2),
                    U.T(en.Modified.ToString(en.Modified.Year == DateTime.Now.Year ? "d MMM, HH:mm" : "d MMM yyyy", Pl), 12, null, Theme.Mu2).With(t => t.Width = 120).At(0, 3),
                    U.T(en.Dir ? "" : SizeText(en.Size), 12, null, Theme.Mu2).With(t => { t.Width = 70; t.TextAlignment = TextAlignment.Right; }).At(0, 4));
                if (!multiple || en.Dir) grid.ColumnDefinitions[0].Width = new GridLength(0);
                var row = U.Row(grid, null, false, 9, U.Th(8, 4), U.Th(0));
                row.Tapped += (_, e) => { e.Handled = true; Click(en); };
                row.DoubleTapped += (_, e) => { e.Handled = true; Open(en); };
                row.PointerExited += (_, _) => DrawOne(en);
                rowOf[en] = (row, check);
                list.Children.Add(row);
            }
            if (all.Count > MaxRows) list.Children.Add(U.T($"Pokazano {MaxRows} z {all.Count} — wpisz nazwę w „Szukaj”, żeby zawęzić.", 12.5, null, Theme.Mu, wrap: true).With(t => t.Margin = U.Th(10, 8)));
            status.Text = all.Count == 0 ? q.Length > 0 ? "Nic tu nie pasuje do „" + q + "”." : filter is not null ? "W tym folderze nie ma pasujących plików." : "Ten folder jest pusty." : "";
            status.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            selected.RemoveAll(x => !rowOf.ContainsKey(x));
            DrawSelection();
            _ = LoadThumbs(thumbs, ver);
        }

        void DrawOne(Entry en)
        {
            if (rowOf.TryGetValue(en, out var r)) r.Row.Background = selected.Contains(en) ? Theme.AccentA(.16) : Theme.Transparent;
        }

        async Task LoadThumbs(List<(Entry En, Border Box)> items, int my)
        {
            foreach (var (en, box) in items.Take(300))
            {
                if (my != ver) return;
                try
                {
                    var bytes = await Task.Run(() => new FileInfo(en.Path).Length < 40_000_000 ? File.ReadAllBytes(en.Path) : null);
                    if (bytes is null || my != ver) continue;
                    using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await ms.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                    ms.Seek(0);
                    var bi = new BitmapImage { DecodePixelWidth = 72, DecodePixelType = DecodePixelType.Logical };
                    await bi.SetSourceAsync(ms);
                    if (my != ver) return;
                    box.Child = new Image { Source = bi, Stretch = Stretch.UniformToFill };
                }
                catch { }
            }
        }

        void DrawCrumbs()
        {
            crumbs.Children.Clear();
            var parts = new List<(string Name, string Path)>();
            for (var d = new DirectoryInfo(folder); d is not null; d = d.Parent) parts.Insert(0, (d.Parent is null ? d.FullName.TrimEnd('\\') : d.Name, d.FullName));
            for (var i = 0; i < parts.Count; i++)
            {
                var (n, p) = parts[i];
                if (i > 0) crumbs.Children.Add(U.I("", 9, Theme.Mu2));
                var last = i == parts.Count - 1;
                crumbs.Children.Add(U.Row(U.T(n, 13, last ? U.Bold : U.Semi, last ? Theme.Fg : Theme.Mu), last ? null : () => Go(p), false, 7, U.Th(7, 4), U.Th(0)));
            }
            crumbScroll.UpdateLayout();
            crumbScroll.ChangeView(crumbScroll.ScrollableWidth + 1000, null, null, true);
            foreach (var (p, row) in placeRows) row.Background = string.Equals(p.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? Theme.Active : Theme.Transparent;
            up.IsEnabled = Directory.GetParent(folder) is not null;
        }

        async void Go(string path)
        {
            var my = ++ver;
            folder = path;
            Last[key] = path;
            selected.Clear();
            search.Text = "";
            DrawCrumbs();
            list.Children.Clear();
            rowOf.Clear();
            status.Text = "Wczytuję…";
            status.Visibility = Visibility.Visible;
            try
            {
                var got = await Task.Run(() => Read(path));
                if (my != ver) return;
                entries = got;
                Fill();
            }
            catch (Exception ex)
            {
                if (my != ver) return;
                entries = [];
                list.Children.Clear();
                status.Text = ex is UnauthorizedAccessException ? "Brak dostępu do tego folderu." : "Nie da się otworzyć folderu: " + ex.Message;
                status.Visibility = Visibility.Visible;
            }
            listScroll.ChangeView(null, 0, null, true);
        }

        async Task NewFolder()
        {
            var box = new TextBox { PlaceholderText = "Nazwa folderu", Text = "Nowy folder" };
            box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            var d = new AppDialog { Title = "Nowy folder", Content = box, PrimaryButtonText = "Utwórz", CloseButtonText = "Anuluj", DefaultButton = ContentDialogButton.Primary, XamlRoot = root, Glyph = "" };
            if (await d.ShowAsync() != ContentDialogResult.Primary) return;
            var n = string.Concat(box.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (n.Length == 0) return;
            try { var p = Path.Combine(folder, n); Directory.CreateDirectory(p); Go(p); }
            catch (Exception ex) { App.Window?.CurrentShell?.Info("Nie udało się utworzyć folderu: " + ex.Message, true); }
        }

        go = Go;
        fill = Fill;
        newFolderAsync = NewFolder;
        Go(folder);
        if (save) nameBox.Loaded += (_, _) =>
        {
            nameBox.Focus(FocusState.Programmatic);
            var dot = nameBox.Text.LastIndexOf('.');
            nameBox.Select(0, dot > 0 ? dot : nameBox.Text.Length);
        };
        await M.WaitNoDialog(root);
        var r = await dlg.ShowAsync();
        return r == ContentDialogResult.Primary ? (result, savePath) : ([], null);
    }

    static List<Entry> Read(string path)
    {
        var di = new DirectoryInfo(path);
        var list = new List<Entry>();
        foreach (var e in di.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System }))
        {
            if (e is DirectoryInfo d) list.Add(new Entry(d.FullName, d.Name, true, d.LastWriteTime, 0));
            else if (e is FileInfo f && !f.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                list.Add(new Entry(f.FullName, f.Name, false, f.LastWriteTime, f.Length));
            if (list.Count > 20_000) break;
        }
        return list;
    }

    static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in all)
        {
            bool ready;
            try { ready = d.IsReady; } catch { ready = false; }
            if (ready) yield return d;
        }
    }

    static string? Try(Func<string> f) { try { return f(); } catch { return null; } }

    static string GlyphOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".avif" or ".heic" => "",
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => "",
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" => "",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "",
        ".pdf" or ".doc" or ".docx" or ".txt" or ".odt" or ".rtf" or ".md" => "",
        ".xls" or ".xlsx" or ".csv" or ".ods" => "",
        ".exe" or ".msi" or ".bat" or ".cmd" => "",
        _ => "",
    };

    static string SizeText(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB".Replace('.', ','),
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB".Replace('.', ','),
    };

    static string Plural(int n, string one, string few, string many) =>
        n == 1 ? one : n % 10 is >= 2 and <= 4 && (n % 100 < 12 || n % 100 > 14) ? few : many;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);

    static string PathOf(Place p)
    {
        try
        {
            switch (p)
            {
                case Place.Downloads:
                    if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var dl) == 0 && Directory.Exists(dl)) return dl;
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                case Place.Desktop: return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                case Place.Documents: return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                case Place.Pictures: return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                case Place.Videos: return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                case Place.Music: return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            }
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
