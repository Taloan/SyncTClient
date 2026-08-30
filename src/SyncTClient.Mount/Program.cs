using SyncTClient.Bep;
using SyncTClient.Mount;
using SyncTClient.Vfs;

// Haengt Syncthing-Ordner als Platzhalter-Verzeichnisse in den Explorer.
// Nichts wird heruntergeladen, bis jemand eine Datei tatsaechlich oeffnet.

var configPath = Arg("--config") ?? "synct.json";

// --- Wartungsbefehle ---------------------------------------------------------

// Prueft, ob sich aus dem Kopf einer Datei ein Vorschaubild gewinnen laesst.
if (Arg("--thumbtest") is { } thumbTarget)
{
    var files = Directory.Exists(thumbTarget)
        ? Directory.EnumerateFiles(thumbTarget, "*.jp*g", SearchOption.AllDirectories).Take(40).ToList()
        : [thumbTarget];

    int hit = 0, miss = 0, unread = 0, skipped = 0;
    foreach (var file in files)
    {
        try
        {
            // Dehydrierte Platzhalter auslassen -- sie zu lesen wuerde einen
            // Download ausloesen, und beim Testen will niemand fremde Dateien
            // vom Server holen.
            const uint recallOnDataAccess = 0x0040_0000;
            if (((uint)new FileInfo(file).Attributes & recallOnDataAccess) != 0) { skipped++; continue; }

            var prefix = new byte[ExifThumbnail.RequiredPrefixBytes];
            using var stream = File.OpenRead(file);
            var read = stream.ReadAtLeast(prefix, Math.Min(prefix.Length, (int)stream.Length), false);

            var thumbnail = ExifThumbnail.TryExtract(prefix.AsSpan(0, read));
            if (thumbnail is null) { miss++; Console.WriteLine($"  --      {Path.GetFileName(file)}"); }
            else { hit++; Console.WriteLine($"  {thumbnail.Length,6} B  {Path.GetFileName(file)}"); }
        }
        catch (Exception ex)
        {
            unread++;
            Console.WriteLine($"  Fehler  {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{hit} mit Vorschau, {miss} ohne, {unread} nicht lesbar, {skipped} nicht lokal (uebersprungen).");
    return 0;
}

// Fragt Windows nach der Vorschau -- pruet die Shell-Erweiterung von aussen.
if (Arg("--thumbcheck") is { } checkTarget) return ThumbnailCheck.Run(checkTarget);

if (args.Contains("--clean-winrt")) return CleanWinRt();
if (Arg("--reset") is { } pathToReset) return Reset(pathToReset);
if (Arg("--unregister") is { } pathToRelease)
{
    SyncRoot.Unregister(pathToRelease);
    Console.WriteLine($"Sync-Root abgemeldet: {pathToRelease}");
    return 0;
}

if (args.Contains("--init"))
{
    if (Arg("--addr") is not { } peerAddress ||
        Arg("--target") is not { } peerDevice ||
        Arg("--folder") is not { } initialFolder)
    {
        Console.Error.WriteLine("--init braucht --addr, --target und --folder.");
        return 2;
    }

    AppConfig.Template(peerAddress, peerDevice, initialFolder).Save(configPath);
    Console.WriteLine($"Vorlage geschrieben: {Path.GetFullPath(configPath)}");
    Console.WriteLine("Darin Modus, Cache-Budget und Teilbaum-Auswahl anpassen, dann ohne --init starten.");
    return 0;
}

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"""
        Keine Konfiguration unter "{Path.GetFullPath(configPath)}".

          synctmount --init --addr <host:port> --target <peer-id> --folder <folder-id>

        Weitere Befehle:
          --config <pfad>       andere Konfigurationsdatei
          --reset <pfad>        haengengebliebenen Sync-Root loeschen
          --unregister <pfad>   Sync-Root nur abmelden
          --clean-winrt         verwaiste WinRT-Registrierungen abmelden
        """);
    return 2;
}

// --- Betrieb -----------------------------------------------------------------

var config = AppConfig.Load(configPath);
if (config.Peers.Count == 0)
{
    Console.Error.WriteLine("Die Konfiguration enthaelt keine Gegenstellen.");
    return 2;
}

var identity = DeviceIdentity.LoadOrCreate(config.HomeDirectory);
Console.WriteLine($"Eigene Device-ID: {identity.Id}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
var peers = new List<PeerHost>();
var exitCode = 0;

try
{
    foreach (var peerConfig in config.Peers)
    {
        var peer = new PeerHost(peerConfig, config, identity, Console.WriteLine);
        peers.Add(peer);

        try
        {
            await peer.ConnectAsync(config.SharesOf(peerConfig), cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{peer.Display}] nicht verbunden: {ex.Message}");
            continue;
        }

        foreach (var offered in peer.Offered.Where(o => !o.Accepted))
            Console.WriteLine($"  angeboten, nicht uebernommen: {offered.Display}");

        Console.WriteLine();
    }

    if (peers.All(p => p.State != PeerState.Verbunden))
    {
        Console.Error.WriteLine("Keine Gegenstelle erreichbar.");
        return 1;
    }

    Console.WriteLine("Laeuft. Strg+C zum Beenden.");

    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };

    // Das Budget wird nach jeder Hydration geprueft; dieser Takt faengt die
    // Faelle ab, in denen von aussen etwas dazukommt.
    while (!stop.Task.IsCompleted)
    {
        var tick = await Task.WhenAny(stop.Task, Task.Delay(TimeSpan.FromMinutes(1), cts.Token));
        if (tick == stop.Task) break;
        foreach (var share in peers.SelectMany(p => p.Shares)) await share.EnforceBudgetAsync();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"FEHLER: {ex.Message}");
    exitCode = 1;
}
finally
{
    Console.WriteLine();
    Console.WriteLine("Beende ...");
    foreach (var peer in peers)
    {
        foreach (var share in peer.Shares) Console.WriteLine("  " + share.Stats());
        await peer.DisposeAsync();
    }
    cts.Cancel();
}

return exitCode;

// --- Hilfsmittel -------------------------------------------------------------

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int CleanWinRt()
{
    var found = 0;
    foreach (var (id, rootPath) in WinRtSyncRoot.ListOwn())
    {
        found++;
        try
        {
            WinRtSyncRoot.Unregister(id);
            Console.WriteLine($"abgemeldet: {rootPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"bleibt:     {rootPath} -- {ex.Message}");
        }
    }

    if (found == 0) Console.WriteLine("Keine eigenen WinRT-Sync-Roots angemeldet.");
    return 0;
}

// Der Cloud-Filter-Treiber gibt Platzhalter nur frei, solange ein Anbieter
// verbunden ist -- also verbinden wir kurz einen leeren, loeschen, und melden ab.
static int Reset(string path)
{
    try
    {
        SyncRoot.Register(path, "SyncTClient", "0.1");
        using (var repair = new CloudFilterMount(path, new EmptySource(), Console.WriteLine))
        {
            repair.Connect();
            Directory.Delete(path, recursive: true);
        }
        Console.WriteLine($"Geloescht: {path}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Reset fehlgeschlagen: {ex.Message}");
    }

    try { SyncRoot.Unregister(path); } catch { /* schon weg */ }
    return Directory.Exists(path) ? 1 : 0;
}

/// <summary>Leere Quelle fuer den Reparaturmodus -- es soll nichts projiziert werden.</summary>
file sealed class EmptySource : IContentSource
{
    public IReadOnlyList<VirtualEntry> Enumerate() => [];

    public Task<byte[]> ReadAsync(string relativePath, long offset, long length, CancellationToken ct)
        => throw new FileNotFoundException(relativePath);
}
