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
    }

    /// <summary>Zahl der empfangenen Index-Nachrichten in dieser Sitzung.</summary>
    public int MessageCount => _messageCount;

    public int Count => (int)(long)(Scalar("SELECT COUNT(*) FROM files WHERE deleted = 0") ?? 0L);

    /// <summary>Summe der Dateigroessen. Wird fuer die Anzeige gebraucht.</summary>
    public long TotalBytes =>
        (long)(Scalar("SELECT COALESCE(SUM(size), 0) FROM files WHERE deleted = 0 AND kind = 0") ?? 0L);

    /// <summary>
    /// Die hoechste bisher empfangene Sequenznummer. Wird dem Peer im
    /// ClusterConfig genannt, damit er nur Neueres schickt.
    /// </summary>
    public long MaxSequence => (long)(Scalar("SELECT COALESCE(MAX(sequence), 0) FROM files") ?? 0L);

    /// <summary>
    /// Die IndexId des Peers zu diesem Ordner. Aendert sie sich, hat der Peer
    /// seinen Index neu aufgebaut und wir muessen von vorn anfangen.
    /// </summary>
    public ulong PeerIndexId
    {
        get => ulong.TryParse(GetMeta("peerIndexId"), out var v) ? v : 0;
        set => SetMeta("peerIndexId", value.ToString());
    }

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
    /// Buchfuehrung verloren ging und nicht mehr feststeht, welche Fassung
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
    public IReadOnlyList<string> Absorb(IEnumerable<BepFileInfo> files)
    {
        Interlocked.Increment(ref _messageCount);
        var changed = new List<string>();

        using var transaction = _db.BeginTransaction();

        using var lookup = _db.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT version FROM files WHERE name = $name";
        var lookupName = lookup.Parameters.Add("$name", SqliteType.Text);

        using var upsert = _db.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO files (name, sequence, size, modified, kind, deleted, version, info, has_blocks)
            VALUES ($name, $sequence, $size, $modified, $kind, $deleted, $version, $info, $hasBlocks)
            ON CONFLICT(name) DO UPDATE SET
                sequence   = excluded.sequence,
                size       = excluded.size,
                modified   = excluded.modified,
                kind       = excluded.kind,
                deleted    = excluded.deleted,
                version    = excluded.version,
                info       = excluded.info,
                has_blocks = excluded.has_blocks
            """;
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
        var next = LocalSequence + 1;
        LocalSequence = next;
        return next;
    }

    /// <summary>Unsere eigene Fassung dieser Datei, sofern wir eine kennen.</summary>
    public bool TryGetLocal(string name, out BepFileInfo file)
    {
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

    /// <summary>Schreibt unsere eigene Fassung fort.</summary>
    /// <param name="state">
    /// 0 sauber, 1 geaendert und noch nicht angekuendigt, 2 angekuendigt und
    /// noch nicht von der Gegenstelle bestaetigt.
    /// </param>
    public void PutLocal(BepFileInfo file, int state)
    {
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
        using var command = _db.CreateCommand();
        command.CommandText = "DELETE FROM local_files WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    /// <summary>Setzt den Zustand einer eigenen Datei, ohne sie neu zu schreiben.</summary>
    public void SetLocalState(string name, int state)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "UPDATE local_files SET state = $state WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$state", state);
        command.ExecuteNonQuery();
    }

    /// <summary>Alle eigenen Dateien in diesem Zustand.</summary>
    public IReadOnlyList<BepFileInfo> LocalInState(int state)
    {
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

    public bool TryGet(string name, out BepFileInfo file)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT info FROM files WHERE name = $name";
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
    /// Alle Namen, die die Gegenstelle fuehrt -- die geloeschten
    /// eingeschlossen.
    /// </summary>
    /// <remarks>
    /// Fuer den Abgleich zwischen Index und Ordner. Ein geloeschter Eintrag
    /// gehoert dazu, denn er ist der Grund, eine noch vorhandene Datei
    /// fortzunehmen.
    /// </remarks>
    public IEnumerable<string> AllNames()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT name FROM files WHERE name <> '' ORDER BY name";

        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    /// <summary>
    /// Nur die Angaben, die fuer Platzhalter gebraucht werden. Die
    /// Blocklisten bleiben aussen vor, sie machen den Grossteil der
    /// Datenmenge aus.
    /// </summary>
    public IEnumerable<(string Name, long Size, long ModifiedS, bool IsDirectory, bool HasContent)> EnumerateLight()
    {
        using var command = _db.CreateCommand();
        command.CommandText = """
            SELECT name, size, modified, kind, has_blocks FROM files
            WHERE deleted = 0 AND name <> ''
            ORDER BY name
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return (
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                (FileInfoType)reader.GetInt32(3) == FileInfoType.Directory,
                reader.GetInt32(4) != 0);
        }
    }

    /// <summary>
    /// Verwirft alles. Noetig, wenn der Peer seinen Index neu aufgebaut hat.
    /// </summary>
    public void Clear() => Execute("DELETE FROM files");

    // ------------------------------------------------------------ Kleinkram

    private void Execute(string sql)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private string? GetMeta(string key)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value)
    {
        using var command = _db.CreateCommand();
        command.CommandText = """
            INSERT INTO meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
