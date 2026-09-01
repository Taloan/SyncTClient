using System.Drawing;
using System.Windows;

namespace SyncTClient.Gui;

/// <summary>Was das Zeichen im Infobereich gerade meldet.</summary>
public enum TrayStatus
{
    Getrennt,
    Synchronisiert,
    Pausiert,
    Erledigt,
    Fehler
}

/// <summary>
/// Liefert das Zeichen für den Infobereich zum jeweiligen Zustand.
/// </summary>
/// <remarks>
/// Die Bilder sind gezeichnet, nicht gerechnet. Vorher wurde die Plakette zur
/// Laufzeit über das Programmsymbol gemalt. Das ging, sah aber aufgesetzt aus:
/// ein Kreis mit Strichzeichnung passt nicht zu einem Symbol mit Verlauf und
/// Schatten. Jetzt liegt für jeden Zustand eine eigene Vorlage bei, im selben
/// Stil gezeichnet.
///
/// Erzeugt werden die Symboldateien von tools/make-icon.ps1. Sie enthalten
/// alle Größen von 16 bis 256 unkomprimiert -- GDI+ liest keine
/// PNG-komprimierten Einträge, und genau darüber holt der Infobereich sein
/// Bild.
///
/// Die geladenen Symbole werden behalten. Der Zustand wird im Sekundentakt
/// geprüft; jedes Mal neu zu laden hieße, im Sekundentakt Handles zu erzeugen
/// und wieder einzusammeln.
/// </remarks>
public sealed class TrayBadge : IDisposable
{
    private readonly Dictionary<(TrayStatus Status, int Size), Icon> _fertig = [];

    private static string Datei(TrayStatus status) => status switch
    {
        TrayStatus.Erledigt => "Status-Ok.ico",
        TrayStatus.Synchronisiert => "Status-Work.ico",
        TrayStatus.Pausiert => "Status-Pause.ico",
        TrayStatus.Fehler => "Status-Error.ico",
        _ => "Status-Unknown.ico"
    };

    /// <summary>Das Zeichen für einen Zustand, in der gewünschten Kantenlänge.</summary>
    public Icon For(TrayStatus status, int size)
    {
        if (_fertig.TryGetValue((status, size), out var vorhanden)) return vorhanden;

        var icon = Laden(Datei(status), size) ?? SystemIcons.Application;
        _fertig[(status, size)] = icon;
        return icon;
    }

    /// <summary>
    /// Lädt die passende Größe aus einer eingebetteten Symboldatei.
    /// </summary>
    /// <remarks>
    /// Der Umweg über die Ressource statt über die Programmdatei hat einen
    /// Grund: nur so lässt sich die Größe auswählen. Aus einer .exe liefert
    /// Windows die Version, die es für richtig hält, und das ist auf einem
    /// hoch aufgelösten Bildschirm die falsche.
    /// </remarks>
    private static Icon? Laden(string name, int size)
    {
        try
        {
            var eintrag = Application.GetResourceStream(new Uri(name, UriKind.Relative));
            if (eintrag is null) return null;

            using var strom = eintrag.Stream;
            return new Icon(strom, new System.Drawing.Size(size, size));
        }
        catch (Exception)
        {
            // Ohne Zeichen ließe sich ein verstecktes Fenster nicht mehr
            // zurückholen. Der Aufrufer setzt dann das Standardsymbol ein.
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var icon in _fertig.Values) icon.Dispose();
        _fertig.Clear();
    }
}
