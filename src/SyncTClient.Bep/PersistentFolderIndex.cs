using Google.Protobuf;
using Microsoft.Data.Sqlite;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;
using FileInfoType = SyncTClient.Bep.Proto.FileInfoType;

namespace SyncTClient.Bep;

/// <summary>
/// Der Ordnerindex auf der Platte statt im Arbeitsspeicher.
/// </summary>
/// <remarks>
/// Bei 100.000 Dateien zu je 5 MB ergeben sich mit 128-KiB-Bloecken rund
/// 4 Millionen Blockhashes, also etwa 128 MB. So viel soll nicht dauerhaft im
/// Arbeitsspeicher liegen. Die vollstaendige <c>FileInfo</c> liegt deshalb als
/// Blob in der Datenbank und wird nur geladen, wenn eine Datei geholt wird.
///
/// Ausserdem ueberlebt die hoechste empfangene Sequenznummer den Neustart.
/// Beim naechsten Verbinden schickt der Peer nur noch Aenderungen statt des
/// gesamten Index.
/// </remarks>
public sealed class PersistentFolderIndex : IDisposable
{
    private readonly SqliteConnection _db;
    private int _messageCount;

    public string FolderId { get; }

    /// <summary>
    /// Schuetzt die Verbindung.
    /// </summary>
    /// <remarks>
    /// SqliteConnection fuehrt intern eine Liste ihrer Befehle. Sie ist nicht
    /// fuer mehrere Faeden gebaut: legt der eine einen Befehl an, waehrend der
    /// andere einen wegraeumt, greift das Wegraeumen daneben und die
    /// Anwendung endet mit einer ArgumentOutOfRangeException aus dem Inneren
    /// von Microsoft.Data.Sqlite.
    ///
    /// Genau das ist passiert: die Oberflaeche fragte im Sekundentakt, ob
    /// Dateien freigegeben werden duerfen, waehrend der Hintergrundlauf
    /// Ankuendigungen aufnahm. Die Sperre gehoert hierher und nicht zu den
    /// Aufrufern -- dort waere sie eine Verabredung, die jede neue Stelle
    /// einhalten muesste.
    /// </remarks>
    private readonly System.Threading.Lock _gate = new();

    public PersistentFolderIndex(string databasePath, string folderId)
    {
        FolderId = folderId;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();

        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS files (
                name     TEXT PRIMARY KEY,
                sequence INTEGER NOT NULL,
                size     INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                kind     INTEGER NOT NULL,
                deleted  INTEGER NOT NULL,
                version  BLOB,
                info     BLOB NOT NULL
            );

            CREATE INDEX IF NOT EXISTS files_sequence ON files(sequence);

            CREATE TABLE IF NOT EXISTS local_files (
                name     TEXT PRIMARY KEY,
                sequence INTEGER NOT NULL,
                size     INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                deleted  INTEGER NOT NULL,
                state    INTEGER NOT NULL,
                version  BLOB,
                info     BLOB NOT NULL
            );

            CREATE INDEX IF NOT EXISTS local_sequence ON local_files(sequence);
            CREATE INDEX IF NOT EXISTS local_state ON local_files(state);
            """);

        // Eine Ankuendigung ohne Bloecke heisst: die Gegenstelle kennt die
        // Datei, haelt sie aber nicht. Sie ist damit nicht zu beschaffen und
        // gehoert zum Rueckstand. Aeltere Datenbanken kennen die Spalte
        // nicht; sie bekommen sie hier, mit 1 als Vorgabe -- so entsteht aus
        // Unwissen kein erfundener Rueckstand.
        try { Execute("ALTER TABLE files ADD COLUMN has_blocks INTEGER NOT NULL DEFAULT 1"); }
        catch (SqliteException) { /* steht schon da */ }

        Schema2();
    }

    /// <summary>
    /// Traegt ein, von welchem Geraet ein Eintrag stammt.
    /// </summary>
    /// <remarks>
    /// Bis hierher war der Name allein der Schluessel: ein Ordner gehoerte
    /// genau einer Gegenstelle. Nehmen mehrere teil, ueberschreiben sich ihre
    /// Eintraege gegenseitig -- und mit ihnen die Blocklisten, an denen
    /// haengt, wer eine Datei wirklich haelt.
    ///
    /// Der Schluessel besteht deshalb aus Geraet und Name. Vorhandene Zeilen
    /// bekommen ein leeres Geraet; welches gemeint war, weiss erst die erste
    /// Verbindung, und <see cref="AdoptLegacy"/> traegt es dann nach.
    /// </remarks>
    private void Schema2()
    {
        if (GetMeta("schema") == "2") return;

        Execute("""
            CREATE TABLE IF NOT EXISTS files_neu (
                device     TEXT    NOT NULL,
                name       TEXT    NOT NULL,
                sequence   INTEGER NOT NULL,
                size       INTEGER NOT NULL,
                modified   INTEGER NOT NULL,
                kind       INTEGER NOT NULL,
                deleted    INTEGER NOT NULL,
                has_blocks INTEGER NOT NULL DEFAULT 1,
                version    BLOB,
                info       BLOB NOT NULL,
                PRIMARY KEY (device, name)
            );

            INSERT OR IGNORE INTO files_neu
                (device, name, sequence, size, modified, kind, deleted, has_blocks, version, info)
            SELECT '', name, sequence, size, modified, kind, deleted, has_blocks, version, info
            FROM files;

            DROP TABLE files;
            ALTER TABLE files_neu RENAME TO files;

            CREATE INDEX IF NOT EXISTS files_sequence ON files(device, sequence);
            CREATE INDEX IF NOT EXISTS files_name ON files(name);
            """);

        SetMeta("schema", "2");
    }

    /// <summary>
    /// Ordnet Eintraege aus einer aelteren Datenbank ihrer Gegenstelle zu.
    /// </summary>
    /// <remarks>
    /// Aufzurufen, sobald feststeht, mit wem gesprochen wird. Vor dem Umbau
    /// gab es nur eine Gegenstelle je Ordner; die Zeilen ohne Geraet gehoeren
    /// also der ersten, die sich meldet.
    /// </remarks>
    public void AdoptLegacy(string device)
    {
        if (string.IsNullOrEmpty(device)) return;

        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "UPDATE files SET device = $device WHERE device = ''";
        command.Parameters.AddWithValue("$device", device);

        if (command.ExecuteNonQuery() == 0) return;

        if (GetMeta("peerIndexId") is { Length: > 0 } alt)
        {
            SetMeta($"peerIndexId:{device}", alt);
            SetMeta("peerIndexId", "");
        }
    }

    /// <summary>Zahl der empfangenen Index-Nachrichten in dieser Sitzung.</summary>
    public int MessageCount => _messageCount;

    /// <summary>
    /// Fuehrt der Index ueberhaupt etwas?
    /// </summary>
    /// <remarks>
    /// Fuer die Frage "gibt es hier etwas" ist das Zaehlen die falsche
    /// Antwort. EXISTS haelt beim ersten Treffer an; COUNT(DISTINCT name)
    /// liest die ganze Tabelle und sortiert sie, bei hunderttausend Zeilen
    /// also jedes Mal von vorn.
    /// </remarks>
    public bool HasEntries
        => Scalar("SELECT EXISTS(SELECT 1 FROM files WHERE deleted = 0)") is long treffer && treffer != 0;

    /// <summary>
    /// Zahl der Dateien, die die Gegenstellen fuehren.
    /// </summary>
    /// <remarks>
    /// Gezaehlt werden Namen, nicht Zeilen. Fuehren drei Gegenstellen
    /// dieselbe Datei, ist es eine Datei und nicht drei.
    ///
    /// Teuer: die ganze Tabelle wird gelesen und sortiert. Fuer die Frage,
    /// ob ueberhaupt etwas dasteht, gibt es <see cref="HasEntries"/>.
    /// </remarks>
    public int Count => (int)(long)(
        Scalar("SELECT COUNT(DISTINCT name) FROM files WHERE deleted = 0") ?? 0L);

    /// <summary>Summe der Dateigroessen. Wird fuer die Anzeige gebraucht.</summary>
    public long TotalBytes => (long)(Scalar("""
        SELECT COALESCE(SUM(size), 0) FROM (
            SELECT name, MAX(size) AS size FROM files
            WHERE deleted = 0 AND kind = 0
            GROUP BY name)
        """) ?? 0L);

    /// <summary>
    /// Die hoechste bisher empfangene Sequenznummer. Wird dem Peer im
    /// ClusterConfig genannt, damit er nur Neueres schickt.
    /// </summary>
    public long MaxSequenceOf(string device)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM files WHERE device = $device";
        command.Parameters.AddWithValue("$device", device);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Die IndexId des Peers zu diesem Ordner. Aendert sie sich, hat der Peer
    /// seinen Index neu aufgebaut und wir muessen von vorn anfangen.
    /// </summary>
    public ulong PeerIndexIdOf(string device)
        => ulong.TryParse(GetMeta($"peerIndexId:{device}"), out var v) ? v : 0;

    public void SetPeerIndexId(string device, ulong value)
        => SetMeta($"peerIndexId:{device}", value.ToString());

    /// <summary>
    /// Unsere eigene IndexId zu diesem Ordner.
    /// </summary>
    /// <remarks>
    /// Das Gegenstueck zu <see cref="PeerIndexId"/> aus eigener Sicht. Eine
    /// geaenderte IndexId bedeutet im Protokoll, dass die Gegenstelle alles
    /// bisher Empfangene verwerfen soll. Sie wird deshalb einmal zufaellig
    /// bestimmt und danach aufbewahrt. Eine bei jeder Verhandlung neue Zahl
    /// wuerde die Gegenstelle bei jeder Verbindung von vorn anfangen lassen.
    ///
    /// Neu bestimmt wird sie nur absichtlich, naemlich wenn die eigene
    /// Buchfuehrung verloren ging und nicht mehr feststeht, welche Version
    /// bereits angekuendigt wurde.
    /// </remarks>
    public ulong OwnIndexId
    {
        get
        {
            if (ulong.TryParse(GetMeta("ownIndexId"), out var stored) && stored != 0) return stored;

            var fresh = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
            SetMeta("ownIndexId", fresh.ToString());
            return fresh;
        }
    }

    /// <summary>
    /// Verwirft die eigene IndexId. Die Gegenstelle faengt dann von vorn an.
    /// </summary>
    public void ResetOwnIndex() => SetMeta("ownIndexId", "0");

    /// <summary>
    /// Wie weit der eigene Index reicht: die hoechste Sequenznummer, die
    /// dieser Client selbst vergeben hat.
    /// </summary>
    /// <remarks>
    /// Sie steht im ClusterConfig im eigenen Eintrag und ist eine Auskunft
    /// ueber diesen Client, nicht ueber die Gegenstelle. Die Gegenstelle
    /// vergleicht den Wert mit dem, was sie von uns bereits hat, und erkennt
    /// daran, ob noch etwas aussteht. Welchen Stand sie selbst hat, teilt sie
    /// in ihrem eigenen ClusterConfig mit.
    ///
    /// Solange nichts angekuendigt wird, ist sie 0. Das ist der zutreffende
    /// Wert und keine Luecke.
    /// </remarks>
    public long LocalSequence
    {
        get => long.TryParse(GetMeta("localSequence"), out var v) ? v : 0;
        set => SetMeta("localSequence", value.ToString());
    }

    /// <summary>
    /// Nimmt einen Stapel Index-Eintraege auf und meldet zurueck, welche
    /// Dateien sich inhaltlich geaendert haben. Fuer diese Dateien muss ein
    /// zwischengespeicherter Inhalt verworfen werden.
    /// </summary>
    /// <param name="device">
    /// Von welcher Gegenstelle die Eintraege stammen. Jede fuehrt ihren
    /// eigenen Bestand; erst zusammen ergeben sie, wer was haelt.
    /// </param>
    public IReadOnlyList<string> Absorb(string device, IEnumerable<BepFileInfo> files)
    {
        using var gate = _gate.EnterScope();
        Interlocked.Increment(ref _messageCount);
        var changed = new List<string>();

        using var transaction = _db.BeginTransaction();

        using var lookup = _db.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT version FROM files WHERE device = $device AND name = $name";
        lookup.Parameters.AddWithValue("$device", device);
        var lookupName = lookup.Parameters.Add("$name", SqliteType.Text);

        using var upsert = _db.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO files (device, name, sequence, size, modified, kind, deleted, version, info, has_blocks)
            VALUES ($device, $name, $sequence, $size, $modified, $kind, $deleted, $version, $info, $hasBlocks)
            ON CONFLICT(device, name) DO UPDATE SET
                sequence   = excluded.sequence,
                size       = excluded.size,
                modified   = excluded.modified,
                kind       = excluded.kind,
                deleted    = excluded.deleted,
                version    = excluded.version,
                info       = excluded.info,
                has_blocks = excluded.has_blocks
            """;
        upsert.Parameters.AddWithValue("$device", device);
        var pName = upsert.Parameters.Add("$name", SqliteType.Text);
        var pSequence = upsert.Parameters.Add("$sequence", SqliteType.Integer);
        var pSize = upsert.Parameters.Add("$size", SqliteType.Integer);
        var pModified = upsert.Parameters.Add("$modified", SqliteType.Integer);
        var pKind = upsert.Parameters.Add("$kind", SqliteType.Integer);
        var pDeleted = upsert.Parameters.Add("$deleted", SqliteType.Integer);
        var pVersion = upsert.Parameters.Add("$version", SqliteType.Blob);
        var pInfo = upsert.Parameters.Add("$info", SqliteType.Blob);
        var pHasBlocks = upsert.Parameters.Add("$hasBlocks", SqliteType.Integer);

        foreach (var file in files)
        {
            var version = file.Version?.ToByteArray() ?? [];

            lookupName.Value = file.Name;
            var previous = lookup.ExecuteScalar() as byte[];
            if (previous is null || !previous.AsSpan().SequenceEqual(version))
                changed.Add(file.Name);

            pName.Value = file.Name;
            pSequence.Value = file.Sequence;
            pSize.Value = file.Size;
            pModified.Value = file.ModifiedS;
            pKind.Value = (int)file.Type;
            pDeleted.Value = file.Deleted ? 1 : 0;
            pVersion.Value = version;
            pInfo.Value = file.ToByteArray();

            // Ein Verzeichnis und eine leere Datei haben keine Bloecke und
            // fehlen trotzdem nicht.
            pHasBlocks.Value =
                file.Deleted || file.Size == 0 || file.Type != FileInfoType.File || file.Blocks.Count > 0
                    ? 1 : 0;

            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
        return changed;
    }

    // ------------------------------------------------------------ Eigener Bestand

    /// <summary>
    /// Die naechste eigene Sequenznummer. Sie wird sofort fortgeschrieben.
    /// </summary>
    /// <remarks>
    /// Eine Nummer darf nicht zweimal vergeben werden. Wird sie vergeben und
    /// die Ankuendigung scheitert danach, ist die Luecke unschaedlich: die
    /// Gegenstelle prueft nur, ob Nummern wachsen, nicht ob sie lueckenlos
    /// sind.
    /// </remarks>
    public long NextLocalSequence()
    {
        using var gate = _gate.EnterScope();
        var next = LocalSequence + 1;
        LocalSequence = next;
        return next;
    }

    /// <summary>Unsere eigene Version dieser Datei, sofern wir eine kennen.</summary>
    public bool TryGetLocal(string name, out BepFileInfo file)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT info FROM local_files WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);

        if (command.ExecuteScalar() is byte[] blob)
        {
            file = BepFileInfo.Parser.ParseFrom(blob);
            return true;
        }

        file = null!;
        return false;
    }

    /// <summary>Schreibt unsere eigene Version fort.</summary>
    /// <param name="state">
    /// 0 sauber, 1 geaendert und noch nicht angekuendigt, 2 angekuendigt und
    /// noch nicht von der Gegenstelle bestaetigt.
    /// </param>
    public void PutLocal(BepFileInfo file, int state)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = """
            INSERT INTO local_files (name, sequence, size, modified, deleted, state, version, info)
            VALUES ($name, $sequence, $size, $modified, $deleted, $state, $version, $info)
            ON CONFLICT(name) DO UPDATE SET
                sequence = excluded.sequence,
                size     = excluded.size,
                modified = excluded.modified,
                deleted  = excluded.deleted,
                state    = excluded.state,
                version  = excluded.version,
                info     = excluded.info
            """;

        command.Parameters.AddWithValue("$name", file.Name);
        command.Parameters.AddWithValue("$sequence", file.Sequence);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$modified", file.ModifiedS);
        command.Parameters.AddWithValue("$deleted", file.Deleted ? 1 : 0);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$version", file.Version?.ToByteArray() ?? []);
        command.Parameters.AddWithValue("$info", file.ToByteArray());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Nimmt den eigenen Eintrag fort. Danach gilt fuer diesen Namen allein,
    /// was die Gegenstelle fuehrt.
    /// </summary>
    public void ForgetLocal(string name)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "DELETE FROM local_files WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Nimmt einen Namen ganz fort -- den eigenen Eintrag und den jeder
    /// Gegenstelle.
    /// </summary>
    /// <remarks>
    /// Fuer Namen, die ein Muster aus dem Abgleich nimmt. Sie stehen sonst
    /// weiter im Baum und zaehlen im Rueckstand, obwohl niemand mehr etwas
    /// mit ihnen vorhat.
    /// </remarks>
    public void Forget(string name)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = """
            DELETE FROM files WHERE name = $name;
            DELETE FROM local_files WHERE name = $name;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    /// <summary>Setzt den Zustand einer eigenen Datei, ohne sie neu zu schreiben.</summary>
    public void SetLocalState(string name, int state)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "UPDATE local_files SET state = $state WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$state", state);
        command.ExecuteNonQuery();
    }

    /// <summary>Alle eigenen Dateien in diesem Zustand.</summary>
    public IReadOnlyList<BepFileInfo> LocalInState(int state)
    {
        using var gate = _gate.EnterScope();
        var found = new List<BepFileInfo>();

        using var command = _db.CreateCommand();
        command.CommandText = "SELECT info FROM local_files WHERE state = $state ORDER BY sequence";
        command.Parameters.AddWithValue("$state", state);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            found.Add(BepFileInfo.Parser.ParseFrom((byte[])reader["info"]));

        return found;
    }

    /// <summary>Alles Eigene ab dieser Sequenznummer, aufsteigend.</summary>
    public IReadOnlyList<BepFileInfo> LocalFrom(long sequence)
    {
        using var gate = _gate.EnterScope();
        var found = new List<BepFileInfo>();

        using var command = _db.CreateCommand();
        command.CommandText = "SELECT info FROM local_files WHERE sequence >= $sequence ORDER BY sequence";
        command.Parameters.AddWithValue("$sequence", sequence);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            found.Add(BepFileInfo.Parser.ParseFrom((byte[])reader["info"]));

        return found;
    }

    /// <summary>Wie viele eigene Dateien wir fuehren.</summary>
    public int LocalCount => (int)(long)(Scalar("SELECT COUNT(*) FROM local_files") ?? 0L);

    /// <summary>
    /// Die beste Ankuendigung zu diesem Namen.
    /// </summary>
    /// <remarks>
    /// Mehrere Gegenstellen koennen denselben Namen fuehren, und nicht alle
    /// mit Inhalt. Genommen wird die, die am meisten sagt: eine vorhandene vor
    /// einer geloeschten, eine mit Blockliste vor einer ohne.
    /// </remarks>
    public bool TryGet(string name, out BepFileInfo file)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = """
            SELECT info FROM files WHERE name = $name
            ORDER BY deleted ASC, has_blocks DESC, sequence DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$name", name);

        if (command.ExecuteScalar() is byte[] blob)
        {
            file = BepFileInfo.Parser.ParseFrom(blob);
            return true;
        }

        file = null!;
        return false;
    }

    /// <summary>
    /// Alle Ankuendigungen zu diesem Namen, je Gegenstelle eine.
    /// </summary>
    /// <remarks>
    /// <see cref="TryGet"/> waehlt die aussagekraeftigste aus und genuegt
    /// ueberall dort, wo eine Datei zu beschaffen ist. Wer zaehlen muss, auf
    /// wie vielen Knoten eine Datei liegt, braucht sie alle -- und zwar mit
    /// Blockliste, denn nur die beweist, welche Bytes dort liegen.
    /// </remarks>
    public IReadOnlyList<BepFileInfo> All(string name)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT info FROM files WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);

        var gefunden = new List<BepFileInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            gefunden.Add(BepFileInfo.Parser.ParseFrom((byte[])reader["info"]));

        return gefunden;
    }

    /// <summary>
    /// Alle Namen, die die Gegenstelle fuehrt -- die geloeschten
    /// eingeschlossen.
    /// </summary>
    /// <remarks>
    /// Fuer den Abgleich zwischen Index und Ordner. Ein geloeschter Eintrag
    /// gehoert dazu, denn er ist der Grund, eine noch vorhandene Datei
    /// fortzunehmen.
    /// </remarks>
    public IReadOnlyList<string> AllNames()
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT DISTINCT name FROM files WHERE name <> '' ORDER BY name";

        var namen = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) namen.Add(reader.GetString(0));

        return namen;
    }

    /// <summary>
    /// Nur die Angaben, die fuer Platzhalter gebraucht werden. Die
    /// Blocklisten bleiben aussen vor, sie machen den Grossteil der
    /// Datenmenge aus.
    /// </summary>
    public IReadOnlyList<(string Name, long Size, long ModifiedS, bool IsDirectory, bool HasContent)> EnumerateLight()
        => EnumerateLight("", int.MaxValue);

    /// <summary>
    /// Dieselben Angaben, aber seitenweise.
    /// </summary>
    /// <remarks>
    /// Bei hunderttausend Dateien ist die vollstaendige Liste ein einziger
    /// grosser Brocken: die Sperre liegt darauf, solange sie entsteht, und
    /// der Speicher dafuer wird am Stueck angefordert und wieder freigegeben.
    /// Beides trifft alle anderen -- die Datenbank ist so lange belegt, und
    /// die Speicherbereinigung haelt fuer einen solchen Brocken das ganze
    /// Programm an.
    ///
    /// Weitergeblaettert wird ueber den Namen und nicht ueber OFFSET: OFFSET
    /// zaehlt bei jeder Seite von vorn und wird gegen Ende quadratisch teuer.
    /// </remarks>
    /// <param name="nach">Der letzte Name der vorigen Seite, oder leer.</param>
    /// <param name="hoechstens">Wie viele Namen die Seite umfasst.</param>
    public IReadOnlyList<(string Name, long Size, long ModifiedS, bool IsDirectory, bool HasContent)>
        EnumerateLight(string nach, int hoechstens)
    {
        using var gate = _gate.EnterScope();
        var eintraege = new List<(string, long, long, bool, bool)>();
        using var command = _db.CreateCommand();
        // Je Name eine Zeile, auch wenn mehrere Gegenstellen ihn fuehren.
        // Inhalt hat er, sobald ihn eine von ihnen fuehrt.
        command.CommandText = """
            SELECT name, MAX(size), MAX(modified), MAX(kind), MAX(has_blocks) FROM files
            WHERE deleted = 0 AND name <> '' AND name > $nach
            GROUP BY name
            ORDER BY name
            LIMIT $hoechstens
            """;

        command.Parameters.AddWithValue("$nach", nach);
        command.Parameters.AddWithValue("$hoechstens", hoechstens);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            eintraege.Add((
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                (FileInfoType)reader.GetInt32(3) == FileInfoType.Directory,
                reader.GetInt32(4) != 0));
        }

        return eintraege;
    }

    /// <summary>
    /// Verwirft alles. Noetig, wenn der Peer seinen Index neu aufgebaut hat.
    /// </summary>
    public void Clear(string device)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE device = $device";
        command.Parameters.AddWithValue("$device", device);
        command.ExecuteNonQuery();
    }

    /// <summary>Welche Gegenstellen diese Datei vollstaendig fuehren.</summary>
    /// <remarks>
    /// Fuer die Wahl der Verbindung: geholt wird bei einer Gegenstelle, die den
    /// Inhalt hat, nicht bei irgendeiner.
    /// </remarks>
    public IReadOnlyList<string> HolderDevices(string name)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = """
            SELECT device FROM files
            WHERE name = $name AND deleted = 0 AND has_blocks = 1 AND size > 0
            """;
        command.Parameters.AddWithValue("$name", name);

        var geraete = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) geraete.Add(reader.GetString(0));

        return geraete;
    }

    /// <summary>
    /// Wie viele Gegenstellen diese Datei vollstaendig fuehren.
    /// </summary>
    /// <remarks>
    /// Das ist die Zahl, an der die Platzhalter-Schwelle haengt. Eine
    /// Ankuendigung ohne Blockliste zaehlt nicht: die Gegenstelle kennt die
    /// Datei dann, haelt sie aber nicht.
    /// </remarks>
    public int Holders(string name)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM files
            WHERE name = $name AND deleted = 0 AND has_blocks = 1 AND size > 0
            """;
        command.Parameters.AddWithValue("$name", name);
        return (int)(long)(command.ExecuteScalar() ?? 0L);
    }

    // ------------------------------------------------------------ Kleinkram

    private void Execute(string sql)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private string? GetMeta(string key)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value)
    {
        using var gate = _gate.EnterScope();
        using var command = _db.CreateCommand();
        command.CommandText = """
            INSERT INTO meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Verdichtet die Datenbank, hoechstens einmal im genannten Abstand.
    /// </summary>
    /// <remarks>
    /// Eine Datenbank, aus der viel geloescht wurde, wird nicht kleiner: die
    /// Seiten bleiben stehen und werden wiederverwendet. Bei einem Ordner,
    /// der sich staendig aendert, sammelt sich das an. VACUUM schreibt sie neu.
    ///
    /// Beim Beenden, nicht im Betrieb: die Datenbank wird dabei vollstaendig
    /// kopiert, und waehrenddessen geht nichts anderes. Beim Beenden wartet
    /// niemand darauf.
    ///
    /// Der Zeitpunkt steht in der Datenbank selbst und nicht in der
    /// Konfiguration. Er gehoert zu dieser Datei; wird sie geloescht, ist auch
    /// die Frage nach ihrem letzten Verdichten gegenstandslos.
    /// </remarks>
    /// <returns>Ob verdichtet wurde.</returns>
    public bool CompactIfDue(TimeSpan abstand)
    {
        using var gate = _gate.EnterScope();

        var zuletzt = GetMeta("lastVacuum");

        if (DateTimeOffset.TryParse(zuletzt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var wann)
            && DateTimeOffset.UtcNow - wann < abstand)
        {
            return false;
        }

        try
        {
            Execute("VACUUM");
            Execute("PRAGMA optimize");
        }
        catch (SqliteException)
        {
            // Verdichten ist eine Wohltat, keine Pflicht. Beim naechsten Mal.
            return false;
        }

        // Auch bei einem Fehlschlag waere ein Vermerk richtig -- sonst wird es
        // bei jedem Beenden neu versucht. Aber ein Fehlschlag ist selten, und
        // ein Versuch je Beenden ist billiger als ein Vermerk, der eine
        // Verdichtung behauptet, die nie stattfand.
        SetMeta("lastVacuum", DateTimeOffset.UtcNow.ToString("O"));
        return true;
    }

    /// <summary>
    /// Raeumt den ganzen Index aus.
    /// </summary>
    /// <remarks>
    /// Fuer das Loesen einer Bindung. Die Datei danach zu loeschen ist der
    /// sauberere Weg, aber der unzuverlaessigere: sie muss dafuer erst
    /// freigegeben sein, und ob das gelingt, haengt an Dingen, die hier
    /// niemand in der Hand hat -- ein Vorrat offener Verbindungen, ein
    /// Virenscanner, ein Explorer-Fenster.
    ///
    /// Ausraeumen gelingt immer, denn es geschieht ueber die offene
    /// Verbindung. Bleibt die Datei danach liegen, ist sie leer, und der
    /// naechste Versuch faengt bei null an -- darauf kommt es an.
    ///
    /// Der Schema-Vermerk bleibt stehen. Ohne ihn liefe beim naechsten
    /// Oeffnen der Umbau noch einmal, und der baut Tabellen um, die schon
    /// richtig stehen.
    /// </remarks>
    public void ClearAll()
    {
        using var gate = _gate.EnterScope();

        Execute("""
            DELETE FROM files;
            DELETE FROM local_files;
            DELETE FROM meta WHERE key <> 'schema';
            """);

        // Der Platz gehoert danach niemandem mehr. Bei fuenfundvierzigtausend
        // Dateien sind das einige Dutzend Megabyte, die sonst als leere Seiten
        // liegenblieben.
        try { Execute("VACUUM"); }
        catch (SqliteException) { /* Platz sparen ist kein Grund zu scheitern */ }
    }

    /// <summary>
    /// Schliesst die Datenbank und gibt die Datei frei.
    /// </summary>
    /// <remarks>
    /// Dispose allein gibt sie nicht frei. Microsoft.Data.Sqlite fuehrt einen
    /// Vorrat offener Verbindungen; eine verworfene wandert dorthin zurueck
    /// und haelt die Datei weiter offen. Wer sie danach loeschen will --
    /// beim Loesen einer Bindung etwa --, scheitert stillschweigend, und der
    /// naechste Versuch findet den alten Index vor.
    ///
    /// Genau das war zu sehen: eine Freigabe, getrennt und neu verbunden,
    /// holte sich keinen neuen Baum. Sie hatte ja noch den alten.
    /// </remarks>
    public void Dispose()
    {
        using var gate = _gate.EnterScope();
        _db.Dispose();
        SqliteConnection.ClearPool(_db);
    }
}
