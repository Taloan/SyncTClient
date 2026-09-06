namespace SyncTClient.Mount;

/// <summary>
/// Erkennt, ob eine Datei zu einer Datenbank gehört, die gerade nicht in sich
/// abgeschlossen ist.
/// </summary>
/// <remarks>
/// Eine SQLite-Datenbank ist nicht eine Datei, sondern ein Satz. Im WAL-Modus
/// steht der neueste Stand gerade <em>nicht</em> in der <c>.db</c>: bestätigte
/// Transaktionen liegen im <c>-wal</c>, bis ein Checkpoint sie einarbeitet. Wer
/// die <c>.db</c> allein kopiert, überträgt einen veralteten Anfang — ohne dass
/// irgendwo ein Fehler auftaucht.
///
/// Daraus folgt aber auch der Umkehrschluss, und der ist brauchbar: ist das
/// <c>-wal</c> leer, steht alles in der <c>.db</c>, und sie ist für sich allein
/// gültig. Das ist der einzige Zeitpunkt, zu dem eine Dateikopie überhaupt
/// stimmen kann.
///
/// <para>Warum nicht die bloße Existenz zählt:</para>
///
/// SQLite räumt <c>-wal</c> und <c>-shm</c> beim sauberen Schließen der letzten
/// Verbindung weg. Einige Anwendungen schalten das ab und lassen ein leeres
/// <c>-wal</c> dauerhaft stehen. Es zu sehen hieße dann, die Datenbank nie mehr
/// zu übertragen. Maßgeblich ist deshalb, ob etwas <em>darin</em> steht.
///
/// Für den älteren Rücksetz-Journalmodus gilt dasselbe mit <c>-journal</c>: es
/// ist nur während einer Transaktion nicht leer.
/// </remarks>
public static class Datenbank
{
    /// <summary>Die Endungen, die zu einer Datenbank gehören, aber nicht sie selbst sind.</summary>
    /// <remarks>
    /// <c>-shm</c> ist Arbeitsspeicher auf Platte, ein Index in das <c>-wal</c>;
    /// SQLite legt es beim Öffnen neu an. Ein von woanders mitgebrachtes
    /// <c>-shm</c> kann dazu führen, dass das <c>-wal</c> falsch gelesen wird.
    ///
    /// <c>-wal</c> und <c>-journal</c> sind nur zusammen mit genau <em>dieser</em>
    /// <c>.db</c> im selben Augenblick sinnvoll. Über zwei Dateien hinweg
    /// atomar zu übertragen kann das Protokoll nicht, also wird es gelassen.
    /// </remarks>
    private static readonly string[] Endungen = ["-shm", "-wal", "-journal"];

    /// <summary>
    /// Gehört dieser Name zu einer Datenbank, ohne die Datenbank zu sein?
    /// </summary>
    public static bool IstBegleitdatei(string name)
    {
        foreach (var endung in Endungen)
            if (name.EndsWith(endung, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Liegt neben dieser Datei ein Journal mit unverarbeitetem Inhalt?
    /// </summary>
    /// <param name="pfad">Der lokale Pfad der Datenbankdatei selbst.</param>
    /// <remarks>
    /// Gibt <c>false</c> zurück, wenn sich nichts feststellen lässt. Ein
    /// Zugriffsfehler beim Nachsehen darf keine Datei dauerhaft aufhalten —
    /// dann gilt der bisherige Weg, und der ist nicht schlechter als vorher.
    /// </remarks>
    public static bool Beschaeftigt(string pfad)
    {
        try
        {
            foreach (var endung in (string[])["-wal", "-journal"])
            {
                var begleit = pfad + endung;

                // Length auf einer fehlenden Datei wirft; Exists zuerst.
                if (File.Exists(begleit) && new FileInfo(begleit).Length > 0) return true;
            }
        }
        catch (Exception)
        {
            // Siehe oben.
        }

        return false;
    }
}
