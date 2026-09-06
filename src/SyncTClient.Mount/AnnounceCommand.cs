using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using SyncTClient.Bep;
using SyncTClient.Bep.Proto;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;
using BepIndex = SyncTClient.Bep.Proto.Index;
using BepRequest = SyncTClient.Bep.Proto.Request;

namespace SyncTClient.Mount;

/// <summary>
/// Kuendigt genau eine lokale Datei bei der Gegenstelle an und beantwortet
/// danach deren Blockanfragen.
/// </summary>
/// <remarks>
/// Das ist der erste Schreibvorgang dieses Clients. Bisher hat er nur gelesen:
/// Index entgegennehmen, Bloecke anfordern, Anfragen zu Dateien beantworten,
/// die die Gegenstelle selbst angekuendigt hatte.
///
/// Angekuendigt wird nur die Datei, die auf der Kommandozeile steht. Lokale
/// Aenderungen werden nicht gesucht, nichts wird geloescht, und ein Name, den
/// die Gegenstelle bereits fuehrt, wird abgelehnt.
/// </remarks>
internal static class AnnounceCommand
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: der Inhalt liegt noch nicht lokal.</summary>
    private const uint RecallOnDataAccess = 0x0040_0000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: schon das Oeffnen holt den Inhalt.</summary>
    private const uint RecallOnOpen = 0x0004_0000;

    /// <summary>FILE_ATTRIBUTE_OFFLINE: der Inhalt liegt woanders.</summary>
    private const uint Offline = 0x1000;

    /// <summary>So lange bleibt die Verbindung nach der Ankuendigung stehen.</summary>
    private static readonly TimeSpan ServeWindow = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(string configPath, string folderId, string wantedFile)
    {
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Keine Konfiguration unter \"{Path.GetFullPath(configPath)}\".");
            return 2;
        }

        var config = AppConfig.Load(configPath);
        config.ResolveAgainst(configPath);

        var share = config.Shares.FirstOrDefault(
            s => s.FolderId.Equals(folderId, StringComparison.OrdinalIgnoreCase));

        if (share is null)
        {
            Console.Error.WriteLine($"Keine Freigabe mit der Ordner-ID \"{folderId}\".");
            return 2;
        }

        if (config.PeerFor(share) is not { } peerConfig)
        {
            Console.Error.WriteLine($"Zu \"{share.Display}\" ist keine Gegenstelle eingetragen.");
            return 2;
        }

        // --- Die Datei --------------------------------------------------

        if (RelativeNameOf(wantedFile) is not { } protocolName)
        {
            Console.Error.WriteLine(
                $"\"{wantedFile}\" ist kein Name innerhalb der Freigabe. " +
                "Erwartet wird ein relativer Pfad ohne \"..\".");
            return 2;
        }

        var root = Path.GetFullPath(share.LocalPath).TrimEnd(Path.DirectorySeparatorChar);
        var localPath = Path.GetFullPath(
            Path.Combine(root, protocolName.Replace('/', Path.DirectorySeparatorChar)));

        if (!localPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"\"{wantedFile}\" fuehrt aus \"{root}\" heraus.");
            return 2;
        }

        var local = new System.IO.FileInfo(localPath);
        if (!local.Exists)
        {
            Console.Error.WriteLine($"\"{localPath}\" gibt es nicht.");
            return 2;
        }

        // Ein Platzhalter hat den Inhalt nicht. Ihn zu lesen wuerde ihn ueber
        // dieselbe Verbindung herunterladen, ueber die er gleich angekuendigt
        // werden soll.
        if (((uint)local.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0)
        {
            Console.Error.WriteLine(
                $"\"{localPath}\" liegt nur als Platzhalter vor. " +
                "Angekuendigt wird nur, was vollstaendig lokal liegt.");
            return 2;
        }

        // Der Name geht so ueber den Draht, wie er hier steht. Windows und
        // Linux legen zusammengesetzte Zeichen verschieden ab; ohne
        // Normalisierung waere derselbe Dateiname auf beiden Seiten ein
        // anderer Eintrag.
        string announcedName;
        try
        {
            announcedName = protocolName.Normalize(NormalizationForm.FormC);
        }
        catch (PlatformNotSupportedException)
        {
            // InvariantGlobalization schaltet die Normalisierung ab. Bei
            // reinem ASCII faellt das nicht auf, bei Umlauten schon. Dann
            // lieber abbrechen als einen Namen ankuendigen, dessen Form nicht
            // feststeht.
            Console.Error.WriteLine(
                $"\"{protocolName}\" laesst sich in diesem Programm nicht NFC-normalisieren " +
                "(InvariantGlobalization). Nur reine ASCII-Namen sind zurzeit ankuendbar.");
            return 2;
        }

        // --- Der Index der Gegenstelle ----------------------------------

        var databasePath = Path.Combine(config.HomeDirectory, $"index-{share.FolderId}.db");
        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine(
                $"Kein Index unter \"{databasePath}\". Ohne ihn laesst sich nicht pruefen, " +
                "ob die Gegenstelle den Namen schon fuehrt.");
            return 2;
        }

        using var index = new PersistentFolderIndex(databasePath, share.FolderId);

        // Sicherheitspruefung. Ein neuer Name kann mit nichts zusammenstossen.
        // Ein bestehender loest bei der Gegenstelle einen Versionsvergleich
        // aus, und der ist hier noch nicht gebaut: die Ankuendigung koennte
        // eine fremde, neuere Version ueberschreiben.
        if (index.TryGet(announcedName, out _) ||
            (announcedName != protocolName && index.TryGet(protocolName, out _)))
        {
            Console.Error.WriteLine(
                $"\"{announcedName}\" steht bereits im Index der Gegenstelle. " +
                "Dieser Befehl kuendigt nur Namen an, die dort noch nicht vorkommen.");
            return 2;
        }

        // --- Die FileInfo -----------------------------------------------

        var identity = DeviceIdentity.LoadOrCreate(config.HomeDirectory);
        var shortId = identity.Id.ShortId();

        int blockSize;
        IReadOnlyList<BlockInfo> blocks;
        byte[] blocksHash;
        try
        {
            // Nicht File.OpenRead: das teilt nur zum Lesen, und wer die
            // Datei danach schreiben will, bekommt eine Absage. Waehrend hier
            // ueber eine grosse Datei gehasht wird, stuende ein fremdes
            // Programm still -- ein Abgleich hat kein anderes Programm
            // aufzuhalten. Die uebrigen Lesewege des Clients tun es laengst so.
            using var content = new FileStream(
                localPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan);
            (blockSize, blocks, blocksHash) = BlockList.For(content, local.Length);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\"{localPath}\" liess sich nicht lesen: {ex.Message}");
            return 1;
        }

        var modifiedUtc = local.LastWriteTimeUtc;
        var file = new BepFileInfo
        {
            Name = announcedName,
            Type = FileInfoType.File,
            Size = local.Length,

            // Windows kennt keinen Unix-Modus. Ohne no_permissions waere die
            // Ankuendigung die Behauptung, die Datei habe den Modus 0000, und
            // ein Syncthing unter Linux zoege das nach.
            Permissions = 0,
            NoPermissions = true,

            ModifiedS = new DateTimeOffset(modifiedUtc).ToUnixTimeSeconds(),
            ModifiedNs = (int)((modifiedUtc.Ticks - DateTime.UnixEpoch.Ticks) % TimeSpan.TicksPerSecond * 100),
            Deleted = false,
            Invalid = false,
            Sequence = index.LocalSequence + 1,
            ModifiedBy = shortId,
            Version = new Vector
            {
                Counters =
                {
                    new Counter { Id = shortId, Value = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                }
            },
            BlockSize = blockSize,
            BlocksHash = ByteString.CopyFrom(blocksHash)

            // local_flags bleibt ungesetzt. bep.proto sagt ausdruecklich, dass
            // das Feld nicht ueber den Draht geht.
        };
        file.Blocks.AddRange(blocks);

        Describe(file, share, localPath, protocolName, identity.Id, index.LocalSequence, modifiedUtc);

        // --- Verbinden ---------------------------------------------------

        using var cts = new CancellationTokenSource();
        var peer = new PeerHost(peerConfig, config, identity, Console.WriteLine);
        var requests = 0;
        var delivered = 0L;

        try
        {
            try
            {
                // Verbinden, ohne die Freigabe einzuhaengen. ConnectAsync mit
                // der Freigabe wuerde den Sync-Root anmelden, alle Platzhalter
                // anlegen und bei Mode = AlwaysLocal den gesamten fehlenden
                // Bestand holen. Fuer eine Ankuendigung ist beides unnoetig.
                // PrepareAsync tauscht den ClusterConfig mit dem Ordner aus
                // und wartet auf den Index; im Explorer entsteht nichts.
                await peer.ConnectAsync([], cts.Token);
                await peer.PrepareAsync(share, cts.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{peer.Display}] nicht verbunden: {ex.Message}");
                return 1;
            }

            if (peer.Connection is not { } connection)
            {
                Console.Error.WriteLine($"[{peer.Display}] keine Verbindung.");
                return 1;
            }

            // Die angekuendigte Datei steht nicht im Index der Gegenstelle,
            // und genau daran erkennt ShareHost eine Datei, die es hier gibt.
            // Deshalb wird die Bedienung vorgeschaltet: die eine Datei
            // beantwortet dieser Befehl selbst, alles andere geht weiter an
            // die Freigabe.
            var inner = connection.Serve;
            connection.Serve = async (request, token) =>
            {
                Interlocked.Increment(ref requests);

                ErrorCode code;
                byte[] data;
                string note;

                if (request.Folder == share.FolderId && request.Name == file.Name)
                {
                    (code, data, note) = await ServeAnnouncedAsync(request, localPath, token);
                }
                else if (inner is not null)
                {
                    (code, data) = await inner(request, token);
                    note = "aus der Freigabe";
                }
                else
                {
                    (code, data, note) = (ErrorCode.NoSuchFile, [], "hier nicht zustaendig");
                }

                if (code == ErrorCode.NoError) Interlocked.Add(ref delivered, data.Length);

                Console.WriteLine(
                    $"  Anfrage \"{request.Name}\" Offset {request.Offset}, {request.Size} Bytes -> " +
                    (code == ErrorCode.NoError
                        ? $"{data.Length} Bytes geliefert ({note})"
                        : $"abgelehnt, {code}: {note}"));

                return (code, data);
            };

            // Index, nicht IndexUpdate: fuer diesen Ordner wurde noch nie
            // etwas geschickt. Die Nachricht ist die vollstaendige Angabe
            // unseres Bestandes, und der besteht aus dieser einen Datei.
            var message = new BepIndex { Folder = share.FolderId, LastSequence = file.Sequence };
            message.Files.Add(file);

            await connection.SendIndexAsync(message, cts.Token);
            index.LocalSequence = file.Sequence;

            Console.WriteLine();
            Console.WriteLine(
                $"Index gesendet: {message.CalculateSize()} Bytes. " +
                $"Eigene Sequenz steht jetzt auf {index.LocalSequence}.");
            Console.WriteLine($"Bleibe {ServeWindow.TotalSeconds:F0} Sekunden verbunden. Strg+C beendet frueher.");
            Console.WriteLine();

            var stop = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
            await Task.WhenAny(stop.Task, Task.Delay(ServeWindow, cts.Token));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FEHLER: {ex.Message}");
            return 1;
        }
        finally
        {
            var (read, written) = peer.Wire;

            Console.WriteLine();
            Console.WriteLine(
                $"Bilanz: {Volatile.Read(ref requests)} Anfragen, " +
                $"{Interlocked.Read(ref delivered)} Bytes ausgeliefert.");
            Console.WriteLine($"        Verbindung: {read} Bytes empfangen, {written} Bytes gesendet.");

            await peer.DisposeAsync();
            await cts.CancelAsync();
        }

        return 0;
    }

    /// <summary>
    /// Schreibt alle Felder der Ankuendigung auf die Konsole, bevor sie
    /// abgeschickt wird.
    /// </summary>
    /// <remarks>
    /// Der erste Schreibvorgang soll nachvollziehbar sein und nicht nur
    /// gelingen. Was hier steht, geht so ueber den Draht.
    /// </remarks>
    private static void Describe(
        BepFileInfo file, ShareConfig share, string localPath, string protocolName,
        Bep.DeviceId ownId, long previousSequence, DateTime modifiedUtc)
    {
        Console.WriteLine($"Eigene Device-ID: {ownId}");
        Console.WriteLine();
        Console.WriteLine($"Ankuendigung an \"{share.Display}\":");
        Console.WriteLine($"  Datei:        {localPath}");
        Console.WriteLine($"  name:         \"{file.Name}\"" +
                          (file.Name == protocolName ? "" : $" (NFC-normalisiert aus \"{protocolName}\")"));
        Console.WriteLine($"  type:         {file.Type}");
        Console.WriteLine($"  size:         {file.Size} Bytes");
        Console.WriteLine($"  permissions:  {file.Permissions}, no_permissions: {file.NoPermissions}");
        Console.WriteLine($"  modified_s:   {file.ModifiedS} ({modifiedUtc:yyyy-MM-dd HH:mm:ss} UTC)");
        Console.WriteLine($"  modified_ns:  {file.ModifiedNs}");
        Console.WriteLine($"  deleted:      {file.Deleted}, invalid: {file.Invalid}");
        Console.WriteLine($"  sequence:     {file.Sequence} (bisher {previousSequence})");
        Console.WriteLine($"  modified_by:  {file.ModifiedBy} (Short-ID {ownId.Short()})");
        Console.WriteLine($"  version:      " + string.Join(
            ", ", file.Version.Counters.Select(c => $"{{ id = {c.Id}, value = {c.Value} }}")));
        Console.WriteLine($"  block_size:   {file.BlockSize} Bytes ({file.BlockSize / 1024} KiB)");
        Console.WriteLine($"  blocks:       {file.Blocks.Count}");
        Console.WriteLine($"  blocks_hash:  {Short(file.BlocksHash.Span)}");
    }

    /// <summary>
    /// Beantwortet eine Anfrage nach der angekuendigten Datei aus den Bytes,
    /// die auf der Platte liegen.
    /// </summary>
    /// <remarks>
    /// Geprueft wird wie in <see cref="ShareHost.ServeAsync"/>: temporaere
    /// Datei, Vorhandensein, Materialisierung, Bereich, Hash. Der Hash gehoert
    /// zur Anfrage und nicht zu unserer Datei. Weicht er ab, hat sich die
    /// Datei seit der Ankuendigung geaendert, und diese Bytes sind nicht der
    /// angefragte Block.
    /// </remarks>
    private static async Task<(ErrorCode Code, byte[] Data, string Note)> ServeAnnouncedAsync(
        BepRequest request, string localPath, CancellationToken ct)
    {
        if (request.FromTemporary)
            return (ErrorCode.NoSuchFile, [], "nach der temporaeren Datei gefragt");

        var local = new System.IO.FileInfo(localPath);
        if (!local.Exists)
            return (ErrorCode.NoSuchFile, [], "liegt nicht mehr da");

        if (((uint)local.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0)
            return (ErrorCode.NoSuchFile, [], "liegt inzwischen nur noch als Platzhalter vor");

        if (request.Size <= 0 || request.Size > BlockList.MaximumBlockSize)
            return (ErrorCode.NoSuchFile, [], $"unmoegliche Blockgroesse {request.Size}");

        if (request.Offset < 0 || request.Offset > local.Length - request.Size)
            return (ErrorCode.NoSuchFile, [],
                $"Bereich {request.Offset}+{request.Size} liegt nicht in {local.Length} Bytes");

        byte[] data;
        try
        {
            data = new byte[request.Size];
            await using var stream = new FileStream(
                localPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.Asynchronous);

            stream.Seek(request.Offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(data, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ErrorCode.Generic, [], ex.Message);
        }

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), request.Hash.Span))
            return (ErrorCode.InvalidFile, [], "unsere Bytes ergeben einen anderen Hash");

        return (ErrorCode.NoError, data, "aus der angekuendigten Datei");
    }

    /// <summary>
    /// Macht aus der Eingabe einen Namen, wie ihn das Protokoll fuehrt: relativ
    /// zur Freigabe, mit / als Trenner. Liefert <c>null</c>, wenn der Name
    /// dafuer nicht taugt.
    /// </summary>
    private static string? RelativeNameOf(string wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted) || wanted.Contains('\0')) return null;
        if (Path.IsPathRooted(wanted)) return null;

        var name = wanted.Replace('\\', '/').Trim('/');
        var parts = name.Split('/');

        if (parts.Any(p => p.Length == 0 || p == "." || p == "..")) return null;

        return name;
    }

    private static string Short(ReadOnlySpan<byte> value)
        => Convert.ToHexStringLower(value[..Math.Min(8, value.Length)]) + (value.Length > 8 ? "..." : "");
}
