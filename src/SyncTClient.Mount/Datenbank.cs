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
/// Gemessen an einer Datenbank mit 5006 Zeilen, deren <c>-wal</c> zuletzt bei
/// 4152 Byte stand: eine Kopie der <c>.db</c> allein enthielt 5003. Drei
/// bestätigte Zeilen lagen nur im <c>-wal</c>. Die Kopie ließ sich öffnen und
/// lesen, sie meldete keinen Fehler, sie war nur älter.
///
/// <para>Der Test, und was er nicht kann</para>
///
/// Geprüft wird die Länge des <c>-wal</c> und des <c>-journal</c>. Ist sie
/// null, steht alles in der <c>.db</c>. Ist sie größer, wird zurückgestellt.
///
/// Die Richtung stimmt damit immer: es wird nie zu früh übertragen. Der Preis
/// ist, dass zu oft zurückgestellt wird. Ein Checkpoint arbeitet die Rahmen in
/// die <c>.db</c> ein, kürzt die Datei danach aber nicht — gemessen behielt ein
/// 1,1 MB großes <c>-wal</c> seine Größe über <c>PASSIVE</c>, <c>FULL</c> und
/// <c>RESTART</c> hinweg, obwohl sein Inhalt längst in der <c>.db</c> stand.
/// Nur <c>PRAGMA wal_checkpoint(TRUNCATE)</c> brachte es auf null.
///
/// Der Kopf hilft dabei nicht weiter. Ausprobiert: das Salz im WAL-Kopf gegen
/// das Salz des ersten Rahmens zu halten unterscheidet die vier Fälle nicht —
/// es schlug in keinem an, in dem die Länge nicht ohnehin schon null war. Ob
/// die Rahmen eingearbeitet sind, steht nicht im <c>-wal</c>, sondern als
/// <c>nBackfill</c> im <c>-shm</c>, und dessen Aufbau ist ausdrücklich kein
/// dauerhaftes Format.
///
/// <para>Was daraus folgt</para>
///
/// Die Datenbank eines laufenden Programms bleibt in der Regel liegen, solange
/// das Programm läuft. Das ist keine Lücke, sondern das Ergebnis: eine
/// Datenbank, an der geschrieben wird, lässt sich auf Dateiebene nicht richtig
/// kopieren. Selbst bei Länge null liegt zwischen dem Nachsehen und dem Lesen
/// eine Lücke, in die ein Schreibvorgang fallen kann.
///
/// Wer eine Datenbank im laufenden Betrieb übertragen will, lässt ihren
/// Eigentümer einen Abzug schreiben — <c>VACUUM INTO</c> oder die Backup-API —
/// und überträgt den. Ein Abgleich, der die lebende Datei mitnimmt, kann das
/// nicht ersetzen.
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
    /// Liegt neben dieser Datei ein Journal mit Inhalt?
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
