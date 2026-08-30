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
/// 4 Millionen Blockhashes -- etwa 128 MB, die niemand dauerhaft im RAM halten
/// will. Die vollstaendige <c>FileInfo</c> liegt deshalb als Blob in der
/// Datenbank und wird nur geladen, wenn wirklich eine Datei geholt wird.
///
/// Zweiter Gewinn: die hoechste empfangene Sequenznummer ueberlebt den
/// Neustart. Beim naechsten Verbinden schickt der Peer nur noch Aenderungen
/// statt des gesamten Index.
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
            """);
    }

    /// <summary>Zahl der empfangenen Index-Nachrichten in dieser Sitzung.</summary>
    public int MessageCount => _messageCount;

    public int Count => (int)(long)(Scalar("SELECT COUNT(*) FROM files WHERE deleted = 0") ?? 0L);

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
    /// Nimmt einen Stapel Index-Eintraege auf und meldet zurueck, welche
    /// Dateien sich inhaltlich geaendert haben -- fuer die muss ein
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
            INSERT INTO files (name, sequence, size, modified, kind, deleted, version, info)
            VALUES ($name, $sequence, $size, $modified, $kind, $deleted, $version, $info)
            ON CONFLICT(name) DO UPDATE SET
                sequence = excluded.sequence,
                size     = excluded.size,
                modified = excluded.modified,
                kind     = excluded.kind,
                deleted  = excluded.deleted,
                version  = excluded.version,
                info     = excluded.info
            """;
        var pName = upsert.Parameters.Add("$name", SqliteType.Text);
        var pSequence = upsert.Parameters.Add("$sequence", SqliteType.Integer);
        var pSize = upsert.Parameters.Add("$size", SqliteType.Integer);
        var pModified = upsert.Parameters.Add("$modified", SqliteType.Integer);
        var pKind = upsert.Parameters.Add("$kind", SqliteType.Integer);
        var pDeleted = upsert.Parameters.Add("$deleted", SqliteType.Integer);
        var pVersion = upsert.Parameters.Add("$version", SqliteType.Blob);
        var pInfo = upsert.Parameters.Add("$info", SqliteType.Blob);

        foreach (var file in files)
        {
            var version = file.Version?.ToByteArray() ?? [];

            lookupName.Value = file.Name;
            if (lookup.ExecuteScalar() is byte[] previous && !previous.AsSpan().SequenceEqual(version))
                changed.Add(file.Name);

            pName.Value = file.Name;
            pSequence.Value = file.Sequence;
            pSize.Value = file.Size;
            pModified.Value = file.ModifiedS;
            pKind.Value = (int)file.Type;
            pDeleted.Value = file.Deleted ? 1 : 0;
            pVersion.Value = version;
            pInfo.Value = file.ToByteArray();
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
        return changed;
    }

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
    /// Nur die Angaben, die fuer Platzhalter gebraucht werden -- ohne die
    /// Blocklisten, die den Grossteil der Datenmenge ausmachen.
    /// </summary>
    public IEnumerable<(string Name, long Size, long ModifiedS, bool IsDirectory)> EnumerateLight()
    {
        using var command = _db.CreateCommand();
        command.CommandText = """
            SELECT name, size, modified, kind FROM files
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
                (FileInfoType)reader.GetInt32(3) == FileInfoType.Directory);
        }
    }

    /// <summary>Verwirft alles -- noetig, wenn der Peer seinen Index neu aufgebaut hat.</summary>
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
