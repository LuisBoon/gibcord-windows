using GibCord.Core;
using GibCord.Core.E2ee;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GibCord.App.Ui;

/// <summary>6.4: kłódka szyfrowanej rozmowy prywatnej (pasek rozmowy, panel „w rozmowie”) i okienko z kodem bezpieczeństwa.</summary>
static class E2eeUi
{
    public const string Lock = "", Warn = "";

    /// <summary>Stan do pokazania: tekst, kolor, ikona. Null — rozmowa bez szyfrowania.</summary>
    public static (string Text, Brush Color, string Glyph)? State(GibCordClient c, long cid)
    {
        var here = c.VoiceChannelId == cid;
        if (here)
        {
            return c.E2eeState switch
            {
                E2eeStatus.Active => ("Szyfrowane od końca do końca", Theme.Green, Lock),
                E2eeStatus.Waiting => ("Czekam na klucz szyfrowania…", Theme.Yellow, Lock),
                E2eeStatus.Failed => ("Szyfrowanie nie działa — kliknij", Theme.Red, Warn),
                _ => c.CallEncrypted(cid) ? ("Szyfrowana rozmowa", Theme.Mu, Lock) : null,
            };
        }
        return c.CallEncrypted(cid) ? ("Szyfrowana rozmowa", Theme.Mu, Lock) : null;
    }

    /// <summary>Kłódka z napisem (klikalna — okienko z kodem bezpieczeństwa).</summary>
    public static FrameworkElement? Badge(Shell sh, long cid, double size = 12)
    {
        if (State(sh.C, cid) is not { } st) return null;
        var b = new Border
        {
            Child = U.H(5, U.I(st.Glyph, size, st.Color), U.T(st.Text, size, U.Semi, st.Color)), Background = Theme.Transparent,
            CornerRadius = new CornerRadius(6), Padding = U.Th(4, 1), HorizontalAlignment = HorizontalAlignment.Left,
        };
        b.Tip("Szyfrowanie od końca do końca — kliknij, żeby zobaczyć kod bezpieczeństwa");
        b.PointerEntered += (_, _) => b.Background = Theme.Hover;
        b.PointerExited += (_, _) => b.Background = Theme.Transparent;
        b.Tapped += (_, e) => { e.Handled = true; ShowInfo(sh, cid); };
        return b;
    }

    /// <summary>Sama kłódka (panel „w rozmowie” w kolumnie kanałów).</summary>
    public static FrameworkElement? Icon(Shell sh, long cid)
    {
        if (State(sh.C, cid) is not { } st) return null;
        var i = U.I(st.Glyph, 12, st.Color);
        i.Margin = U.Th(0, 0, 5, 0);
        i.Tip(st.Text);
        return i;
    }

    /// <summary>Okienko: co to znaczy + kod bezpieczeństwa do porównania z rozmówcą; przy błędzie — „Spróbuj ponownie”.</summary>
    public static async void ShowInfo(Shell sh, long cid)
    {
        var c = sh.C;
        var here = c.VoiceChannelId == cid;
        var codeText = U.T("…", 26, U.Bold, Theme.Fg).With(t => { t.FontFamily = Theme.Mono; t.HorizontalAlignment = HorizontalAlignment.Center; t.IsTextSelectionEnabled = true; });
        var codeBox = U.Box(U.V(6, U.T("KOD BEZPIECZEŃSTWA", 10.5, U.Bold, Theme.Mu2).With(t => t.HorizontalAlignment = HorizontalAlignment.Center), codeText), U.W(.05), 12, U.Th(14, 12), U.W(.1), 1);
        var panel = U.V(12,
            U.T("Dźwięk i obraz tej rozmowy są szyfrowane na urządzeniach osób w rozmowie. Serwer GibCorda przekazuje je dalej, ale nie może ich odsłuchać ani obejrzeć.", 13, null, Theme.Mu, wrap: true));
        var st = here ? c.E2eeState : E2eeStatus.Off;
        if (!here)
            panel.Add(U.T("Dołącz do rozmowy, żeby zobaczyć kod bezpieczeństwa.", 12.5, null, Theme.Mu2, wrap: true));
        else if (st == E2eeStatus.Waiting)
            panel.Add(U.H(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, U.T("Czekam na klucz szyfrowania od osób w rozmowie…", 12.5, U.Semi, Theme.Yellow, wrap: true)));
        else if (st == E2eeStatus.Failed)
            panel.Add(U.T(c.E2ee?.Error ?? "Nie udało się ustalić klucza szyfrowania.", 12.5, U.Semi, Theme.Red, wrap: true));
        else
        {
            panel.Add(codeBox);
            panel.Add(U.T("Porównajcie ten kod (na głos albo innym komunikatorem). Jeśli każdy widzi taki sam, nikt nie podsłuchuje. Kod się zmienia, gdy ktoś wejdzie z innego urządzenia.", 12, null, Theme.Mu2, wrap: true));
        }
        panel.Add(U.T("Szyfrowanie obejmuje rozmowę głosową, kamerę i transmisję ekranu — nie wiadomości na czacie.", 11.5, null, Theme.Mu2, wrap: true));
        panel.Width = 400;
        var d = new AppDialog
        {
            Title = "Szyfrowanie od końca do końca", Glyph = Lock, Content = panel, CloseButtonText = "Zamknij", XamlRoot = sh.XamlRoot,
            PrimaryButtonText = st == E2eeStatus.Failed ? "Spróbuj ponownie" : null, DefaultButton = ContentDialogButton.Close,
        };
        var shown = d.ShowAsync();
        if (st == E2eeStatus.Active)
        {
            var code = await c.SafetyCode();
            codeText.Text = code ?? "brak — spróbuj za chwilę";
        }
        if (await shown == ContentDialogResult.Primary) await c.RetryE2ee();
    }
}
