using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SyncTClient.Gui;

/// <summary>
/// Zeigt einen Pfad an und kuerzt ihn, wenn die Spalte zu schmal ist.
/// </summary>
/// <remarks>
/// Gekuerzt wird auf Wurzel, Auslassung und letzten Ordner:
/// "C:\Users\dirkm\CameraImporter" wird zu "C:\...\CameraImporter". Das
/// Abschneiden am Ende taugt dafuer nicht -- es nimmt genau den Teil fort,
/// der die Freigaben unterscheidet, und uebrig bleibt dreimal
/// "C:\Users\dirkm\AppData...".
///
/// Die Entscheidung haengt an der Spaltenbreite und gehoert deshalb hierher
/// und nicht in die Zeile: dieselbe Freigabe wird bei breiter Spalte
/// vollstaendig und bei schmaler gekuerzt angezeigt.
///
/// Eine Rueckkopplung entsteht dabei nicht. Gemessen wird immer der volle
/// Pfad gegen die verfuegbare Breite, und die haengt am Feld, nicht am Text:
/// der Textblock ist gedehnt und damit so breit wie die Zelle.
/// </remarks>
public static class PfadVerkuerzung
{
    /// <summary>Der vollstaendige Pfad. Angezeigt wird, was hineinpasst.</summary>
    public static readonly DependencyProperty PfadProperty =
        DependencyProperty.RegisterAttached(
            "Pfad", typeof(string), typeof(PfadVerkuerzung),
            new PropertyMetadata(null, Geaendert));

    public static void SetPfad(DependencyObject ziel, string wert) => ziel.SetValue(PfadProperty, wert);

    public static string? GetPfad(DependencyObject ziel) => (string?)ziel.GetValue(PfadProperty);

    private static void Geaendert(DependencyObject ziel, DependencyPropertyChangedEventArgs e)
    {
        if (ziel is not TextBlock block) return;

        // Einmal anmelden, auch wenn der Pfad sich mehrfach aendert.
        block.SizeChanged -= AufGroesse;
        block.SizeChanged += AufGroesse;

        Anwenden(block);
    }

    private static void AufGroesse(object absender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) Anwenden((TextBlock)absender);
    }

    private static void Anwenden(TextBlock block)
    {
        if (Ziel(block) is not { } lauf) return;

        var pfad = GetPfad(block) ?? "";
        var breite = block.ActualWidth;

        // Vor dem ersten Anordnen ist die Breite unbekannt. Dann der volle
        // Pfad; das naechste SizeChanged rechnet nach.
        lauf.Text = breite <= 0 || Passt(block, pfad, breite) ? pfad : Gekuerzt(pfad);
    }

    /// <summary>Der Textlauf innerhalb des Verweises.</summary>
    private static Run? Ziel(TextBlock block)
        => block.Inlines.FirstInline is Hyperlink verweis
            ? verweis.Inlines.FirstInline as Run
            : block.Inlines.FirstInline as Run;

    private static bool Passt(TextBlock block, string text, double breite)
    {
        var schrift = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);

        var gesetzt = new FormattedText(
            text, CultureInfo.CurrentCulture, block.FlowDirection, schrift, block.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);

        return gesetzt.Width <= breite;
    }

    /// <summary>
    /// Wurzel, Auslassung, letzter Ordner.
    /// </summary>
    /// <remarks>
    /// Bleibt der Pfad ohne Wurzel oder besteht er nur aus Wurzel und einem
    /// Namen, ist nichts wegzulassen: das Ergebnis waere nicht kuerzer,
    /// sondern nur ungenauer. Dann bleibt er, wie er ist, und das Abschneiden
    /// am Ende uebernimmt der Textblock.
    /// </remarks>
    private static string Gekuerzt(string pfad)
    {
        var wurzel = Path.GetPathRoot(pfad);
        if (string.IsNullOrEmpty(wurzel)) return pfad;

        var letzter = Path.GetFileName(pfad.TrimEnd(Path.DirectorySeparatorChar));
        if (letzter.Length == 0) return pfad;

        var kurz = Path.Combine(wurzel, "...", letzter);
        return kurz.Length < pfad.Length ? kurz : pfad;
    }
}
