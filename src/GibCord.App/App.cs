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

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GibCord6-dane");
    public static Prefs Prefs { get; private set; } = new();
    public static MainWindow? Window { get; private set; }

    public App()
    {
        RequestedTheme = ApplicationTheme.Dark;
        UnhandledException += (_, e) =>
        {
            Log("Nieobsłużony błąd: " + e.Exception);
            e.Handled = true; // jeden błąd w widoku nie może zamknąć całego GibCorda
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Directory.CreateDirectory(DataDir);
        Prefs = Prefs.Load(DataDir);
        Window = new MainWindow();
        Window.Start();
    }

    public static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "dziennik.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
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
