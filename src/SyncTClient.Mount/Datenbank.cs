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
///
/// <para>Was hier ausdrücklich nicht geschieht</para>
///
/// Das Journal selbst einzuarbeiten — <c>PRAGMA wal_checkpoint(TRUNCATE)</c>
/// aus einer eigenen Verbindung — wäre technisch der bequeme Ausweg. Es
/// funktioniert auch: erprobt an einer 35-MB-Datenbank von Lightroom mit 8,4
/// MB im Journal, danach stand das Journal auf null, die Datei trug den
/// vollständigen Stand, und alle sechs Tabellen zählten unverändert 22 093
/// Zeilen.
///
/// Getan wird es trotzdem nicht. <b>Fremde Dateien werden nicht verändert.</b>
/// Dass ein Journal gefüllt liegen bleibt, kann eine Absicht sein und nicht
/// ein Versehen — ein Programm, das seine Arbeit angehalten hat und dort
/// fortsetzen will, wo es aufgehört hat. Was daraus folgt, entscheidet sein
/// Eigentümer, nicht der Abgleich. Ein Abgleich liest, überträgt und schreibt,
/// was ihm gehört; in den Zustand eines anderen Programms greift er nicht ein,
/// auch nicht hilfreich gemeint.
///
/// Der Preis ist benannt: solange ein Journal Inhalt hat, trägt die
/// Gegenstelle den Stand, der in der <c>.db</c> steht — bei einem beendeten
/// Programm womöglich für Wochen, bis es das nächste Mal läuft. Das ist die
/// richtige Seite, auf der man in dieser Frage irrt.
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
    /// Wie lange ein Satz unbewegt sein muss, damit er als verwaist gilt.
    /// </summary>
    /// <remarks>
    /// Ein Journal mit Inhalt heißt nicht, dass jemand schreibt. Es heißt,
    /// dass zuletzt jemand geschrieben hat.
    ///
    /// Gemessen an Lightroom: nach dem Beenden blieben neben
    /// <c>Managed Catalog.mcat</c> 8,4 MB im <c>-wal</c> und neben
    /// <c>Managed Catalog.wfindex</c> 6,1 MB liegen. Die <c>.mcat</c> selbst
    /// trug den Stand vom 15.08., das Journal den vom 07.09. — und weil das
    /// Journal Inhalt hatte, wurde der ganze Satz zurückgestellt. Die
    /// Gegenstelle behielt damit den Stand vom 15. August, nicht für Stunden,
    /// sondern auf Dauer: an einer Datenbank, die niemand mehr öffnet, wird
    /// das Journal nie leer.
    ///
    /// Das ist das Gegenteil dessen, was der Modus erreichen soll. Er soll
    /// verhindern, dass ein halber Stand übertragen wird, nicht dafür sorgen,
    /// dass gar keiner mehr ankommt.
    /// </remarks>
    public static readonly TimeSpan Ruhefrist = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wird an dieser Datenbank gerade geschrieben?
    /// </summary>
    /// <param name="pfad">
    /// Der lokale Pfad. Eine Begleitdatei wird auf ihre Datenbank
    /// zurückgeführt, damit die Antwort für den ganzen Satz dieselbe ist.
    /// </param>
    /// <remarks>
    /// Zwei Bedingungen, und beide müssen zutreffen.
    ///
    /// Erstens muss ein Journal Inhalt haben. Ist es leer, steht alles in der
    /// <c>.db</c>, und die Datei ist für sich gültig.
    ///
    /// Zweitens muss sich im Satz — <c>.db</c>, <c>-shm</c>, <c>-wal</c>,
    /// <c>-journal</c> — in den letzten <see cref="Ruhefrist"/> etwas bewegt
    /// haben. Bewegt sich nichts mehr, gehört der Satz zu einem beendeten
    /// Programm, und es gibt niemanden mehr, dem eine Übertragung
    /// dazwischenkommen könnte.
    ///
    /// Maßgeblich ist der jüngste Zeitpunkt im Satz, nicht der der
    /// <c>.db</c>. Im WAL-Modus bleibt die <c>.db</c> zwischen zwei
    /// Checkpoints unberührt, während das Journal im Sekundentakt wächst —
    /// wer nur auf sie sähe, hielte eine Datenbank unter Volllast für ruhig.
    ///
    /// Ein Zeitpunkt in der Zukunft — verstellte Uhr, mitkopierter
    /// Zeitstempel — ergibt ein negatives Alter und gilt nicht als
    /// Bewegung. Sonst bliebe der Satz für immer liegen.
    ///
    /// Gibt <c>false</c> zurück, wenn sich nichts feststellen lässt. Ein
    /// Zugriffsfehler beim Nachsehen darf keine Datei dauerhaft aufhalten.
    /// </remarks>
    public static bool Beschaeftigt(string pfad)
    {
        try
        {
            var basis = Basis(pfad);

            if (!MitInhalt(basis, ["-wal", "-journal"])) return false;
            if (LetzteBewegung(basis) is not { } zuletzt) return false;

            var her = DateTime.UtcNow - zuletzt;
            return her >= TimeSpan.Zero && her < Ruhefrist;
        }
        catch (Exception)
        {
            // Siehe oben.
        }

        return false;
    }

    /// <summary>
    /// Führt eine Begleitdatei auf ihre Datenbank zurück; alles andere bleibt.
    /// </summary>
    private static string Basis(string pfad)
    {
        foreach (var endung in Endungen)
            if (pfad.EndsWith(endung, StringComparison.OrdinalIgnoreCase))
                return pfad[..^endung.Length];

        return pfad;
    }

    /// <summary>
    /// Liegt ein Rollback-Journal mit Inhalt daneben?
    /// </summary>
    /// <remarks>
    /// Der Unterschied zum <c>-wal</c> ist die Richtung, und er entscheidet,
    /// was eine Kopie der <c>.db</c> allein wert ist.
    ///
    /// Im WAL-Modus stehen die <em>neueren</em> Daten im Journal. Die
    /// <c>.db</c> allein ist damit ein älterer, aber in sich gültiger Stand.
    ///
    /// Im Rollback-Modus ist es umgekehrt: die <c>.db</c> trägt bereits
    /// Seiten einer Transaktion, die noch nicht abgeschlossen ist, und das
    /// Journal hält den Weg zurück. Sie allein zu übertragen hieße, einen
    /// halben Schreibvorgang zu übertragen. Solange ein solches Journal
    /// Inhalt hat und sich nicht einarbeiten lässt, bleibt die Datei liegen.
    /// </remarks>
    public static bool RollbackOffen(string pfad)
    {
        try
        {
            return MitInhalt(Basis(pfad), ["-journal"]);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Steht in einem dieser Begleiter etwas?</summary>
    private static bool MitInhalt(string basis, string[] endungen)
    {
        foreach (var endung in endungen)
        {
            var begleit = basis + endung;

            // Length auf einer fehlenden Datei wirft; Exists zuerst.
            if (File.Exists(begleit) && new FileInfo(begleit).Length > 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Wann zuletzt an irgendeiner Datei dieses Satzes geschrieben wurde.
    /// </summary>
    private static DateTime? LetzteBewegung(string basis)
    {
        DateTime? juengste = null;

        foreach (var kandidat in (string[])
                 [basis, basis + "-shm", basis + "-wal", basis + "-journal"])
        {
            var info = new FileInfo(kandidat);
            if (!info.Exists) continue;

            var zeit = info.LastWriteTimeUtc;
            if (juengste is null || zeit > juengste.Value) juengste = zeit;
        }

        return juengste;
    }
}
