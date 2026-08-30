using Google.Protobuf;
using SyncTClient.Bep;
using SyncTClient.Bep.Proto;
using SyncTClient.Mount;
using SyncTClient.Vfs;

// Haengt einen Syncthing-Ordner als Platzhalter-Verzeichnis in den Explorer.
// Nichts wird heruntergeladen, bis jemand eine Datei tatsaechlich oeffnet.

var home = Arg("--home") ?? "synct-home";
var address = Arg("--addr");
var target = Arg("--target");
var folderId = Arg("--folder");
var root = Arg("--root");
var parallelism = int.Parse(Arg("--par") ?? "8");
var waitSeconds = double.Parse(Arg("--wait") ?? "20");
var cleanup = args.Contains("--cleanup");
var useWinRt = args.Contains("--winrt");

// Alle ueber WinRT angemeldeten Roots auflisten und abmelden. Sie ueberleben
// das Loeschen des Verzeichnisses, weil die Registrierung davon unabhaengig ist.
if (args.Contains("--clean-winrt"))
{
    var found = 0;
    foreach (var (id, rootPath) in WinRtSyncRoot.ListOwn())
    {
        found++;
        var exists = Directory.Exists(rootPath) ? "" : " (Verzeichnis weg)";
        try
        {
            WinRtSyncRoot.Unregister(id);
            Console.WriteLine($"abgemeldet: {rootPath}{exists}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"bleibt:     {rootPath} -- {ex.Message}");
        }
    }
    if (found == 0) Console.WriteLine("Keine eigenen WinRT-Sync-Roots angemeldet.");
    return 0;
}

// Aufraeumen eines haengengebliebenen Sync-Roots. Der Cloud-Filter-Treiber
// gibt Platzhalter nur frei, solange ein Anbieter verbunden ist -- also
// verbinden wir kurz einen leeren, loeschen, und melden ab.
if (Arg("--reset") is { } pathToReset)
{
    try
    {
        SyncRoot.Register(pathToReset, providerName: "SyncTClient", providerVersion: "0.1");
        using (var repair = new CloudFilterMount(pathToReset, new EmptySource(), Console.WriteLine))
        {
            repair.Connect();
            Directory.Delete(pathToReset, recursive: true);
        }
        Console.WriteLine($"Geloescht: {pathToReset}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Reset fehlgeschlagen: {ex.Message}");
    }

    try { SyncRoot.Unregister(pathToReset); } catch { /* schon weg */ }
    return Directory.Exists(pathToReset) ? 1 : 0;
}

// Abmelden und beenden -- ohne das laesst sich ein Sync-Root nicht loeschen.
if (Arg("--unregister") is { } pathToRelease)
{
    SyncRoot.Unregister(pathToRelease);
    Console.WriteLine($"Sync-Root abgemeldet: {pathToRelease}");
    return 0;
}

if (address is null || target is null || folderId is null)
{
    Console.Error.WriteLine("""
        synctmount -- Syncthing-Ordner als Platzhalter im Explorer

          --addr <host:port>  Adresse des Peers
          --target <id>       Device-ID des Peers
          --folder <id>       Folder-ID
          --root <pfad>       Wo der Ordner erscheinen soll
                              (Standard: %USERPROFILE%\SyncT\<folder-id>)
          --home <pfad>       Verzeichnis fuer das Geraetezertifikat
          --par <n>           Parallele Block-Requests (Standard: 8)
          --wait <sekunden>   Wartezeit auf den Index (Standard: 20)
          --cleanup           Beim Beenden Sync-Root abmelden und loeschen
          --winrt             Sync-Root ueber StorageProviderSyncRootManager
                              anmelden statt ueber CfRegisterSyncRoot
        """);
    return 2;
}

root ??= Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT", folderId);

var identity = DeviceIdentity.LoadOrCreate(home);
Console.WriteLine($"Eigene Device-ID: {identity.Id}");

var (host, port) = SplitHostPort(address);
using var cts = new CancellationTokenSource();

Console.WriteLine($"Verbinde mit {host}:{port} ...");
await using var connection = await BepConnection.ConnectAsync(
    host, port, identity, DeviceId.Parse(target), ct: cts.Token);
Console.WriteLine($"Verbunden mit {connection.PeerHello.DeviceName} ({connection.PeerHello.ClientVersion})");

var index = new FolderIndex(folderId);
var idle = new SemaphoreSlim(0);
connection.IndexReceived += m => { if (m.Folder == folderId) { index.Absorb(m.Files); idle.Release(); } };
connection.IndexUpdateReceived += m => { if (m.Folder == folderId) { index.Absorb(m.Files); idle.Release(); } };

var readLoop = connection.RunAsync(cts.Token);

var clusterConfig = new ClusterConfig();
var folder = new Folder { Id = folderId, Label = folderId, Type = FolderType.SendReceive };
folder.Devices.Add(new Device
{
    Id = ByteString.CopyFrom(identity.Id.Span),
    IndexId = (ulong)Random.Shared.NextInt64(1, long.MaxValue)
});
folder.Devices.Add(new Device { Id = ByteString.CopyFrom(connection.PeerId.Span) });
clusterConfig.Folders.Add(folder);
await connection.SendClusterConfigAsync(clusterConfig, cts.Token);

Console.WriteLine("Warte auf den Index ...");
var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
while (DateTime.UtcNow < deadline && !readLoop.IsCompleted)
{
    if (!await idle.WaitAsync(TimeSpan.FromSeconds(3), cts.Token) && index.MessageCount > 0) break;
}

if (index.Count == 0)
{
    Console.Error.WriteLine("Kein Index empfangen -- ist das Geraet freigegeben und der Ordner geteilt?");
    return 1;
}
Console.WriteLine($"{index.Count} Eintraege im Index.");

// --- Ab hier wird es sichtbar ------------------------------------------------

Console.WriteLine($"Registriere Sync-Root: {root}");
string? winRtId = null;
if (useWinRt)
{
    winRtId = await WinRtSyncRoot.RegisterAsync(root, $"SyncT {folderId}", "0.1");
    Console.WriteLine($"  ueber StorageProviderSyncRootManager, Id={winRtId}");
}
else
{
    SyncRoot.Register(root, providerName: "SyncTClient", providerVersion: "0.1");
    Console.WriteLine("  ueber CfRegisterSyncRoot");
}

var source = new BepContentSource(connection, index, folderId, parallelism, Console.WriteLine);
using var mount = new CloudFilterMount(root, source, Console.WriteLine);
mount.Connect();
mount.ProjectPlaceholders();

Console.WriteLine();
Console.WriteLine($"Bereit. Der Ordner steht jetzt im Explorer:");
Console.WriteLine($"  {root}");
Console.WriteLine("Eine Datei oeffnen loest die Hydration aus. Strg+C zum Beenden.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
await Task.WhenAny(stop.Task, readLoop);

Console.WriteLine("\nBeende ...");
mount.Dispose();

if (cleanup)
{
    Console.WriteLine("Melde Sync-Root ab und raeume auf.");
    if (winRtId is not null) WinRtSyncRoot.Unregister(winRtId);
    else SyncRoot.Unregister(root);
    try { Directory.Delete(root, recursive: true); }
    catch (Exception ex) { Console.Error.WriteLine($"Aufraeumen: {ex.Message}"); }
}

cts.Cancel();
return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static (string Host, int Port) SplitHostPort(string address)
{
    var colon = address.LastIndexOf(':');
    return colon < 0 ? (address, 22000) : (address[..colon], int.Parse(address[(colon + 1)..]));
}


/// <summary>Leere Quelle fuer den Reparaturmodus -- es soll nichts projiziert werden.</summary>
file sealed class EmptySource : IContentSource
{
    public IReadOnlyList<VirtualEntry> Enumerate() => [];

    public Task<byte[]> ReadAsync(string relativePath, long offset, long length, CancellationToken ct)
        => throw new FileNotFoundException(relativePath);
}
