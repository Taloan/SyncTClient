using System.Diagnostics;
using System.Security.Cryptography;
using Google.Protobuf;
using SyncTClient.Bep;
using SyncTClient.Bep.Proto;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;

// Konsolenwerkzeug zum Nachweis, dass ein eigener BEP-Peer den Ordnerindex
// entgegennehmen und Inhalte gezielt anfordern kann, ohne irgendetwas lokal
// zu materialisieren. Der Vorlaeufer des eigentlichen Platzhalter-Clients.

var options = CommandLineOptions.Parse(args);
if (options is null) return 2;

try
{
    return await RunAsync(options);
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"FEHLER: {ex.Message}");
    return 1;
}

static async Task<int> RunAsync(CommandLineOptions o)
{
    var identity = DeviceIdentity.LoadOrCreate(o.Home);
    Console.WriteLine($"Eigene Device-ID:  {identity.Id}");

    if (o.ShowIdOnly)
    {
        Console.WriteLine();
        Console.WriteLine("Diese ID auf dem Peer als Geraet hinzufuegen und den Testordner");
        Console.WriteLine("mit ihr teilen. Danach ohne --id erneut starten.");
        return 0;
    }

    if (string.IsNullOrEmpty(o.Address) || string.IsNullOrEmpty(o.Target) || string.IsNullOrEmpty(o.Folder))
    {
        Console.Error.WriteLine("--addr, --target und --folder werden benoetigt (oder --id zum Auslesen der eigenen ID).");
        return 2;
    }

    var expectedPeer = DeviceId.Parse(o.Target);
    var (host, port) = SplitHostPort(o.Address);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Console.WriteLine($"Verbinde mit {host}:{port} ...");
    await using var connection = await BepConnection.ConnectAsync(host, port, identity, expectedPeer, ct: cts.Token);

    Console.WriteLine($"TLS ok, Peer verifiziert: {connection.PeerId}");
    Console.WriteLine($"Peer meldet sich als: {connection.PeerHello.DeviceName} " +
                      $"({connection.PeerHello.ClientName} {connection.PeerHello.ClientVersion})");
    Console.WriteLine();

    var index = new FolderIndex(o.Folder);
    var idle = new SemaphoreSlim(0);

    connection.ClusterConfigReceived += cc => PrintClusterConfig(cc, o.Folder, identity.Id, connection.PeerId);
    connection.IndexReceived += msg =>
    {
        if (msg.Folder != o.Folder) return;
        index.Absorb(msg.Files);
        Console.WriteLine($"  Index: +{msg.Files.Count}  -> {index.Count} Eintraege insgesamt");
        idle.Release();
    };
    connection.IndexUpdateReceived += msg =>
    {
        if (msg.Folder != o.Folder) return;
        index.Absorb(msg.Files);
        Console.WriteLine($"  IndexUpdate: +{msg.Files.Count}  -> {index.Count} Eintraege insgesamt");
        idle.Release();
    };

    var readLoop = connection.RunAsync(cts.Token);

    // Wir kuendigen den Ordner an, halten aber selbst nichts: MaxSequence und
    // IndexId fuer den Peer bleiben 0, damit er den vollen Index schickt.
    var clusterConfig = new ClusterConfig();
    var folder = new Folder { Id = o.Folder, Label = o.Folder, Type = FolderType.SendReceive };
    folder.Devices.Add(new Device
    {
        Id = ByteString.CopyFrom(identity.Id.Span),
        MaxSequence = 0,
        IndexId = (ulong)Random.Shared.NextInt64(1, long.MaxValue)
    });
    folder.Devices.Add(new Device
    {
        Id = ByteString.CopyFrom(connection.PeerId.Span),
        MaxSequence = 0,
        IndexId = 0
    });
    clusterConfig.Folders.Add(folder);
    await connection.SendClusterConfigAsync(clusterConfig, cts.Token);

    Console.WriteLine($"Sammle Index fuer Ordner \"{o.Folder}\" (max. {o.Wait.TotalSeconds:0}s) ...");

    // Nach drei ruhigen Sekunden gilt der Index als vollstaendig -- aber erst,
    // wenn ueberhaupt schon etwas kam. Der Peer laesst sich mit dem Start des
    // Index-Senders Zeit.
    var deadline = DateTime.UtcNow + o.Wait;
    while (DateTime.UtcNow < deadline && !readLoop.IsCompleted)
    {
        var signalled = await idle.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        if (!signalled && index.MessageCount > 0)
        {
            Console.WriteLine("(Index scheint vollstaendig)");
            break;
        }
    }

    if (readLoop.IsFaulted) await readLoop; // wirft die eigentliche Ursache

    var files = index.Snapshot();
    if (files.Count == 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(index.MessageCount > 0
            ? "Index empfangen, aber leer -- liegt im Ordner auf dem Server ueberhaupt eine Datei?"
            : """
              Keine einzige Index-Nachricht empfangen.
                - Ist das Geraet auf dem Peer bestaetigt (nicht nur eingetragen)?
                - Ist der Ordner mit diesem Geraet geteilt und nicht pausiert?
                - Enthaelt der Ordner mindestens eine Datei? (leerer Ordner => keine Nachricht)
              """);
        return 1;
    }

    Summarize(files, o.ListLimit);

    if (string.IsNullOrEmpty(o.Fetch))
    {
        Console.WriteLine();
        Console.WriteLine("Kein --fetch angegeben. Der Index steht -- genau das ist die Grundlage");
        Console.WriteLine("fuer die Platzhalter. Zum Beweis des Inhaltszugriffs mit --fetch <datei> starten.");
        return 0;
    }

    if (!index.TryGet(o.Fetch, out var file))
    {
        Console.Error.WriteLine($"\n\"{o.Fetch}\" ist nicht im Index (Pfad relativ zum Ordner, mit / als Trenner).");
        return 1;
    }

    await FetchAsync(connection, o, file);
    return 0;
}

static async Task FetchAsync(BepConnection connection, CommandLineOptions o, BepFileInfo file)
{
    var target = string.IsNullOrEmpty(o.Output)
        ? Path.GetFileName(file.Name.Replace('/', Path.DirectorySeparatorChar)) + ".fetched"
        : o.Output;

    Console.WriteLine();
    Console.WriteLine($"Hole \"{file.Name}\"");
    Console.WriteLine($"  Groesse:   {file.Size} Bytes");
    Console.WriteLine($"  Bloecke:   {file.Blocks.Count} zu je {file.BlockSize} Bytes");
    Console.WriteLine($"  Parallel:  {o.Parallelism}");

    var stopwatch = Stopwatch.StartNew();
    var data = await FileFetcher.FetchAllAsync(connection, o.Folder, file, o.Parallelism);
    stopwatch.Stop();

    await File.WriteAllBytesAsync(target, data);

    var seconds = stopwatch.Elapsed.TotalSeconds;
    Console.WriteLine();
    Console.WriteLine($"  OK -- alle {file.Blocks.Count} Bloecke verifiziert");
    Console.WriteLine($"  Dauer:     {stopwatch.ElapsedMilliseconds} ms " +
                      $"({data.Length / seconds / (1024 * 1024):0.0} MB/s)");
    Console.WriteLine($"  SHA-256:   {Convert.ToHexStringLower(SHA256.HashData(data))}");
    Console.WriteLine($"  Geschrieben nach: {target}");
}

static void PrintClusterConfig(ClusterConfig cc, string wanted, DeviceId me, DeviceId peer)
{
    Console.WriteLine($"Peer bietet {cc.Folders.Count} Ordner an (Secondary={cc.Secondary}):");
    foreach (var f in cc.Folders)
    {
        var mark = f.Id == wanted ? "->" : "  ";
        Console.WriteLine($"  {mark} {f.Id,-16} \"{f.Label}\"  Type={f.Type} StopReason={f.StopReason}");
        foreach (var d in f.Devices)
        {
            var id = DeviceId.FromBytes(d.Id.Span);
            var who = id == me ? "WIR" : id == peer ? "PEER" : "fremd";
            Console.WriteLine($"       [{who,-5}] {id.Short()}");
            Console.WriteLine($"               MaxSequence={d.MaxSequence}  IndexId={d.IndexId}  " +
                              $"Introducer={d.Introducer}  EncToken={d.EncryptionPasswordToken.Length} B");
        }
    }
    Console.WriteLine();
}

static void Summarize(IReadOnlyList<BepFileInfo> files, int limit)
{
    long totalBytes = 0;
    int regular = 0, directories = 0, deleted = 0, withoutBlocks = 0;

    foreach (var f in files)
    {
        if (f.Deleted) { deleted++; continue; }
        if (f.Type == FileInfoType.Directory) { directories++; continue; }
        regular++;
        totalBytes += f.Size;
        if (f.Blocks.Count == 0) withoutBlocks++;
    }

    Console.WriteLine();
    Console.WriteLine("Index empfangen:");
    Console.WriteLine($"  Dateien:         {regular} ({totalBytes / (1024.0 * 1024.0):0.0} MB)");
    Console.WriteLine($"  Verzeichnisse:   {directories}");
    Console.WriteLine($"  Geloescht:       {deleted}");
    Console.WriteLine($"  ohne Blockliste: {withoutBlocks}  <- die haelt der Peer selbst nicht");
    Console.WriteLine();
    Console.WriteLine("  (Auf der Platte liegt hier nichts davon.)");
    Console.WriteLine();
    Console.WriteLine($"{"NAME",-56} {"GROESSE",12} {"BLOECKE",8}");

    var shown = 0;
    foreach (var f in files.Where(f => !f.Deleted && f.Type != FileInfoType.Directory)
                           .OrderBy(f => f.Name, StringComparer.Ordinal))
    {
        if (shown >= limit)
        {
            Console.WriteLine($"... und {regular - shown} weitere");
            break;
        }
        var name = f.Name.Length <= 56 ? f.Name : "..." + f.Name[^53..];
        Console.WriteLine($"{name,-56} {f.Size,12} {f.Blocks.Count,8}");
        shown++;
    }
}

static (string Host, int Port) SplitHostPort(string address)
{
    var colon = address.LastIndexOf(':');
    if (colon < 0) return (address, 22000);
    return (address[..colon], int.Parse(address[(colon + 1)..]));
}

/// <summary>Sehr einfache Kommandozeilenauswertung -- kein Bedarf fuer mehr.</summary>
internal sealed record CommandLineOptions
{
    public string Home { get; init; } = "synct-home";
    public bool ShowIdOnly { get; init; }
    public string Address { get; init; } = "";
    public string Target { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Fetch { get; init; } = "";
    public string Output { get; init; } = "";
    public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(20);
    public int Parallelism { get; init; } = 8;
    public int ListLimit { get; init; } = 20;

    public static CommandLineOptions? Parse(string[] args)
    {
        var o = new CommandLineOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i].TrimStart('-').ToLowerInvariant();
            if (key is "h" or "help")
            {
                PrintUsage();
                return null;
            }
            if (key == "id") { o = o with { ShowIdOnly = true }; continue; }

            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Zu \"{args[i]}\" fehlt der Wert.");
                return null;
            }
            var value = args[++i];

            o = key switch
            {
                "home" => o with { Home = value },
                "addr" => o with { Address = value },
                "target" => o with { Target = value },
                "folder" => o with { Folder = value },
                "fetch" => o with { Fetch = value },
                "out" => o with { Output = value },
                "wait" => o with { Wait = TimeSpan.FromSeconds(double.Parse(value)) },
                "par" => o with { Parallelism = int.Parse(value) },
                "list" => o with { ListLimit = int.Parse(value) },
                _ => o
            };

            if (key is not ("home" or "addr" or "target" or "folder" or "fetch" or "out" or "wait" or "par" or "list"))
            {
                Console.Error.WriteLine($"Unbekannte Option \"{args[i - 1]}\".");
                return null;
            }
        }

        return o;
    }

    private static void PrintUsage() => Console.WriteLine("""
        SyncTClient.Probe -- BEP-Peer ohne lokale Materialisierung

          --id                Eigene Device-ID ausgeben und beenden
          --home <pfad>       Verzeichnis fuer das Geraetezertifikat (Standard: synct-home)
          --addr <host:port>  Adresse des Peers (Standardport 22000)
          --target <id>       Erwartete Device-ID des Peers
          --folder <id>       Folder-ID des Ordners
          --fetch <datei>     Datei blockweise holen und verifizieren
          --out <pfad>        Zieldatei fuer --fetch
          --wait <sekunden>   Maximale Wartezeit auf den Index (Standard: 20)
          --par <n>           Parallele Block-Requests (Standard: 8)
          --list <n>          Wieviele Dateien anzeigen (Standard: 20)
        """);
}
