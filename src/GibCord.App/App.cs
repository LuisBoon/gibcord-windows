using System.Security.Cryptography;
using System.Text;
using GibCord.App.Ui;
using GibCord.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace GibCord.App;

/// <summary>Aplikacja WinUI bez plików XAML: zasoby kontrolek i metadane podajemy ręcznie.</summary>
public sealed class App : Application, IXamlMetadataProvider
{
    readonly XamlControlsXamlMetaDataProvider _meta = new();
    public IXamlType GetXamlType(Type type) => _meta.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _meta.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _meta.GetXmlnsDefinitions();

    // kopia do próby (--server …) ma osobny katalog: tokeny, pamięć i ustawienia nie mieszają się z prawdziwym GibCordem
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Options.OtherServer ? "GibCord6-dane-" + Options.ServerSlug : "GibCord6-dane");
    public static Prefs Prefs { get; internal set; } = new();
    public static MainWindow? Window { get; private set; }

    public App()
    {
        RequestedTheme = ApplicationTheme.Dark;
        UnhandledException += (_, e) =>
        {
            Log("Nieobsłużony błąd: " + e.Exception);
            ErrorReport.Send("wyjątek", e.Exception.GetType().Name + ": " + e.Exception.Message, e.Exception.ToString());
            e.Handled = true; // jeden błąd w widoku nie może zamknąć całego GibCorda
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Log("OnLaunched: zasoby");
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Directory.CreateDirectory(DataDir);
            Prefs = Prefs.Load(DataDir);
            GibCord.Core.GibCordApi.UserAgent = "GibCord-Windows/" + Updater.Version; // pełna wersja (np. 6.0.10) — panel admina: kto ma którą
            Ui.Theme.Apply(Prefs.Theme);
            Log("OnLaunched: okno");
            Window = new MainWindow();
            Log("OnLaunched: start");
            Window.Start();
            Log("OnLaunched: gotowe");
        }
        catch (Exception ex) { Log("Błąd w OnLaunched: " + ex); throw; }
    }

    static readonly object _logLock = new();
    public static string LogPath => Path.Combine(DataDir, "dziennik.txt");

    /// <summary>Dziennik w %LocalAppData%: najwyżej ~2 MB, potem poprzedni trafia do dziennik-stary.txt.</summary>
    public static void Log(string line)
    {
        lock (_logLock)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var p = LogPath;
                if (File.Exists(p) && new FileInfo(p).Length > 2_000_000) File.Move(p, Path.Combine(DataDir, "dziennik-stary.txt"), true);
                File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }

    /// <summary>Ostatnie linie dziennika (do „Kopiuj dane diagnostyczne”).</summary>
    public static string LogTail(int lines)
    {
        lock (_logLock)
        {
            try { return File.Exists(LogPath) ? string.Join(Environment.NewLine, File.ReadLines(LogPath).TakeLast(lines)) : "(pusty)"; }
            catch (Exception ex) { return "(nie da się odczytać: " + ex.Message + ")"; }
        }
    }
}

/// <summary>Token sesji zaszyfrowany kluczem konta Windows (DPAPI) — inny użytkownik komputera go nie odczyta.</summary>
public sealed class DpapiTokenStore(string path) : ITokenStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GibCord6-token");

    public string? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }

    public void Save(string? token)
    {
        try
        {
            if (token is null) { if (File.Exists(path)) File.Delete(path); return; }
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { App.Log("Zapis tokenu: " + ex.Message); }
    }
}

/// <summary>
/// 6.4: klucz prywatny urządzenia do szyfrowanych rozmów (P-256, PKCS#8) — osobno dla każdego konta, w pliku e2ee-ID.bin
/// zaszyfrowanym DPAPI (tylko ten użytkownik Windows go odczyta). Strona trzyma swój jako niewyeksportowywalny klucz WebCrypto.
/// </summary>
public sealed class DpapiE2eeKeyStore(string dir) : GibCord.Core.IE2eeKeyStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GibCord6-e2ee");

    string PathOf(long uid) => Path.Combine(dir, $"e2ee-{uid}.bin");

    public (string Dev, byte[] Key)? Load(long userId)
    {
        try
        {
            var p = PathOf(userId);
            if (!File.Exists(p)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.CurrentUser);
            // format: 16 znaków identyfikatora urządzenia + PKCS#8
            if (raw.Length < 17) return null;
            return (Encoding.ASCII.GetString(raw, 0, 16), raw[16..]);
        }
        catch (Exception ex) { App.Log("Klucz szyfrowania: " + ex.Message); return null; }
    }

    public void Save(long userId, string dev, byte[] key)
    {
        var raw = new byte[16 + key.Length];
        Encoding.ASCII.GetBytes(dev, 0, 16, raw, 0);
        key.CopyTo(raw, 16);
        Directory.CreateDirectory(dir);
        var p = PathOf(userId);
        File.WriteAllBytes(p + ".tmp", ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser));
        File.Move(p + ".tmp", p, true);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
    }
}
