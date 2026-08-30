using SyncTClient.Bep;
using SyncTClient.Mount;
using SyncTClient.Vfs;

// Haengt Syncthing-Ordner als Platzhalter-Verzeichnisse in den Explorer.
// Nichts wird heruntergeladen, bis jemand eine Datei tatsaechlich oeffnet.

var configPath = Arg("--config") ?? "synct.json";

// --- Wartungsbefehle ---------------------------------------------------------

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
if (config.Shares.Count == 0)
{
    Console.Error.WriteLine("Die Konfiguration enthaelt keine Shares.");
    return 2;
}

var identity = DeviceIdentity.LoadOrCreate(config.HomeDirectory);
Console.WriteLine($"Eigene Device-ID: {identity.Id}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
var hosts = new List<ShareHost>();
var exitCode = 0;

try
{
    foreach (var share in config.Shares)
    {
        var host = new ShareHost(share, config, identity, Console.WriteLine);
        hosts.Add(host);
        await host.StartAsync(cts.Token);
        Console.WriteLine($"[{share.FolderId}] bereit unter {share.LocalPath}");
        Console.WriteLine();
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
        foreach (var host in hosts) await host.EnforceBudgetAsync();
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
    Console.WriteLine("\nBeende ...");
    foreach (var host in hosts)
    {
        Console.WriteLine("  " + host.Stats());
        await host.DisposeAsync();
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
