using System.Diagnostics;
using SyncTClient.Bep;
using SyncTClient.Mount;
using SyncTClient.Vfs;
using BepBlockInfo = SyncTClient.Bep.Proto.BlockInfo;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;
using FileInfoType = SyncTClient.Bep.Proto.FileInfoType;

// Haengt Syncthing-Ordner als Platzhalter-Verzeichnisse in den Explorer.
// Nichts wird heruntergeladen, bis jemand eine Datei tatsaechlich oeffnet.

var configPath = Arg("--config") ?? AppConfig.DefaultConfigPath();

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
            // Dehydrierte Platzhalter auslassen. Sie zu lesen wuerde einen
            // Download ausloesen, und beim Testen sollen keine fremden
            // Dateien vom Server geholt werden.
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

// Fragt Windows nach der Vorschau. Prueft die Shell-Erweiterung von aussen.
if (Arg("--thumbcheck") is { } checkTarget) return ThumbnailCheck.Run(checkTarget);

// Erzeugt die Vorschau-Erweiterung direkt ueber COM.
if (args.Contains("--comtest")) return ComCheck.Run();

// Zeigt, was die Shell ueber eine Datei weiss.
if (Arg("--shellprops") is { } propsTarget) return ShellProperties.Run(propsTarget);

// Schaltet den Anheft-Zustand um. Dient dem Vergleich mit fremden Anbietern.
if (Arg("--pin") is { } pinTarget)
    return PinStateTool.Run(pinTarget, Arg("--state") ?? "unspecified", args.Contains("--recurse"));

// Fragt die Vorschau ueber den Zwischenspeicher der Shell ab. Das ist der Weg
// des Explorers.
if (Arg("--cachecheck") is { } cacheTarget)
    return ShellThumbnailCache.Run(cacheTarget, Arg("--width") is { } w ? uint.Parse(w) : 256u);

// Rechnet die Blocklisten lokal vorhandener Dateien nach und vergleicht sie
// mit dem, was die Gegenstelle angekuendigt hat.
if (args.Contains("--blockcheck")) return BlockCheck();

// Kuendigt genau eine lokale Datei bei der Gegenstelle an und bedient danach
// deren Blockanfragen. Der einzige Befehl, der etwas schreibt.
if (args.Contains("--announce"))
{
    if (Arg("--folder") is not { } announceFolder || Arg("--file") is not { } announceFile)
    {
        Console.Error.WriteLine("--announce braucht --folder und --file.");
        return 2;
    }

    return await AnnounceCommand.RunAsync(configPath, announceFolder, announceFile);
}

// Meldet die Vorschau-Erweiterung an, ohne den Client zu starten.
if (Arg("--register-thumbs") is { } thumbStore)
{
    if (ThumbnailProviderRegistration.FindLibrary() is not { } thumbLibrary)
    {
        Console.Error.WriteLine("synctthumbs.dll nicht gefunden -- ThumbProvider veroeffentlichen.");
        return 1;
    }

    Directory.CreateDirectory(thumbStore);
    ThumbnailProviderRegistration.RegisterStore(Path.GetFullPath(thumbStore));
    ThumbnailProviderRegistration.RegisterClass(thumbLibrary);
    Console.WriteLine($"Klasse:  {ThumbnailProviderRegistration.ClassId} -> {thumbLibrary}");
    Console.WriteLine($"Wirt:    {ThumbnailProviderRegistration.AppId} (DllSurrogate)");
    Console.WriteLine($"Vorrat:  {Path.GetFullPath(thumbStore)}");

    foreach (var id in ThumbnailProviderRegistration.OwnSyncRootIds())
        Console.WriteLine($"Sync-Root {id}: {(ThumbnailProviderRegistration.AttachToSyncRoot(id) ? "verbunden" : "nicht schreibbar")}");

    return 0;
}

// Ruft einen Vorschau-Anbieter unmittelbar auf. Ohne --clsid ist das der
// eigene. Mit einer fremden CLSID laesst sich ein Anbieter vermessen, der
// funktioniert.
if (Arg("--providertest") is { } probeTarget)
{
    var providerId = Arg("--clsid") is { } given
        ? Guid.Parse(given)
        : new Guid("7E4B2A61-3C9D-4F58-9A17-6D2E5B84C013");

    var probeWidth = Arg("--width") is { } widthText ? uint.Parse(widthText) : 256u;
    var probeContext = Arg("--ctx") switch
    {
        "inproc" => ProviderProbe.InProc,
        "local" => ProviderProbe.LocalServer,
        _ => ProviderProbe.InProc | ProviderProbe.LocalServer
    };

    return ProviderProbe.Run(providerId, probeTarget, probeWidth, probeContext);
}

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
    Console.WriteLine("Darin Modus, Cache-Obergrenze und Teilbaum-Auswahl anpassen, dann ohne --init starten.");
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
          --blockcheck          eigene Blocklisten gegen den Index pruefen
          --announce --folder <ordner-id> --file <relativer/name>
                                eine lokale Datei bei der Gegenstelle ankuendigen
        """);
    return 2;
}

// --- Betrieb -----------------------------------------------------------------

var config = AppConfig.Load(configPath);
config.ResolveAgainst(configPath);
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

    // Das Budget wird nach jeder Hydration geprueft. Dieser Takt deckt die
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

// Vergleicht die selbst gerechnete Blockliste mit der der Gegenstelle.
//
// Der Befehl prueft BlockList nach: Blockgroessen-Regel und BlocksHash-Formel
// sind aus Syncthings Quelltext abgeleitet, und erst der Vergleich mit echten
// Ankuendigungen zeigt, ob sie stimmen. Geprueft wird byteweise: Blockgroesse,
// Zahl der Bloecke, je Block Hash, Offset und Groesse, dazu der BlocksHash.
int BlockCheck()
{
    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"Keine Konfiguration unter \"{Path.GetFullPath(configPath)}\".");
        return 2;
    }

    var settings = AppConfig.Load(configPath);
    settings.ResolveAgainst(configPath);
    var wantedFolder = Arg("--folder");
    var perShareLimit = Arg("--max") is { } maxText && int.TryParse(maxText, out var given) && given > 0
        ? given
        : int.MaxValue;

    var chosen = settings.Shares
        .Where(s => wantedFolder is null || s.FolderId.Equals(wantedFolder, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (chosen.Count == 0)
    {
        Console.Error.WriteLine(wantedFolder is null
            ? "Die Konfiguration enthaelt keine Freigaben."
            : $"Keine Freigabe mit der Ordner-ID \"{wantedFolder}\".");
        return 2;
    }

    // Zaehler ueber alle Freigaben hinweg. Aus ihnen ergibt sich am Ende das
    // Ergebnis.
    long sizeRuleChecked = 0, sizeRuleFailed = 0;
    long blocksHashChecked = 0, blocksHashFailed = 0;
    long differingTotal = 0;
    var sizesSeen = new SortedDictionary<int, int>();

    foreach (var share in chosen)
    {
        var databasePath = Path.Combine(settings.HomeDirectory, $"index-{share.FolderId}.db");
        if (!File.Exists(databasePath))
        {
            Console.WriteLine($"[{share.Display}] kein Index unter \"{Path.GetFullPath(databasePath)}\" -- uebersprungen.");
            continue;
        }

        using var index = new PersistentFolderIndex(databasePath, share.FolderId);

        // Erst die leichte Liste vollstaendig einsammeln, dann je Eintrag die
        // volle FileInfo holen. Die Blocklisten sind der grosse Teil des
        // Index und sollen nicht alle gleichzeitig im Speicher liegen.
        var entries = index.EnumerateLight().Where(e => !e.IsDirectory).ToList();

        int examined = 0, equal = 0, differing = 0;
        int dehydrated = 0, absent = 0, sizeDiffers = 0, withoutBlocks = 0, unreadable = 0;
        long bytes = 0;
        var shown = 0;
        var clock = Stopwatch.StartNew();

        foreach (var entry in entries)
        {
            if (examined >= perShareLimit) break;
            if (!index.TryGet(entry.Name, out var announced)) continue;
            if (announced.Type != FileInfoType.File || announced.Deleted || announced.Invalid) continue;

            // Ohne Blockliste haelt die Gegenstelle die Datei selbst nicht
            // vor. Es gibt dann nichts zu vergleichen.
            if (announced.Blocks.Count == 0 && announced.Size > 0) { withoutBlocks++; continue; }

            var localPath = Path.Combine(share.LocalPath, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            var local = new FileInfo(localPath);
            if (!local.Exists) { absent++; continue; }

            // Ein dehydrierter Platzhalter wird gezaehlt und nicht gelesen.
            // Ihn zu lesen wuerde ihn herunterladen. Bei einer Bibliothek
            // dieser Groesse waeren das Stunden Uebertragung und ein vielfach
            // ueberschrittenes Cache-Budget. RECALL_ON_DATA_ACCESS ist das
            // entscheidende Merkmal. Die beiden anderen Attribute bedeuten
            // ebenfalls, dass die Bytes nicht lokal liegen.
            const uint recallOnDataAccess = 0x0040_0000;
            const uint recallOnOpen = 0x0004_0000;
            const uint offline = 0x0000_1000;
            if (((uint)local.Attributes & (recallOnDataAccess | recallOnOpen | offline)) != 0)
            {
                dehydrated++;
                continue;
            }

            // Eine lokal veraenderte Datei ergibt zwangslaeufig eine andere
            // Blockliste. Das sagt nichts ueber die Rechnung aus.
            if (local.Length != announced.Size) { sizeDiffers++; continue; }

            int blockSize;
            byte[] blocksHash;
            string? problem;
            try
            {
                using var stream = File.OpenRead(localPath);
                (blockSize, var blocks, blocksHash) = BlockList.For(stream, local.Length);
                problem = BlockDifference(announced, blockSize, blocks);
            }
            catch (Exception ex)
            {
                unreadable++;
                Console.WriteLine($"  nicht lesbar: {entry.Name} -- {ex.Message}");
                continue;
            }

            examined++;
            bytes += local.Length;

            var announcedBlockSize = AnnouncedBlockSize(announced);
            sizesSeen[announcedBlockSize] = sizesSeen.GetValueOrDefault(announcedBlockSize) + 1;

            sizeRuleChecked++;
            if (blockSize != announcedBlockSize) sizeRuleFailed++;

            var hashDiffers = announced.BlocksHash.Length > 0
                              && !blocksHash.AsSpan().SequenceEqual(announced.BlocksHash.Span);

            // Ueber die BlocksHash-Formel sagt nur eine Datei etwas aus, deren
            // Blockliste schon uebereinstimmt. Weichen die Bloecke ab, weicht
            // der Hash darueber zwangslaeufig ebenfalls ab, und das sagt
            // nichts ueber die Formel aus. Aeltere Gegenstellen schicken das
            // Feld gar nicht.
            if (problem is null && announced.BlocksHash.Length > 0)
            {
                blocksHashChecked++;
                if (hashDiffers) blocksHashFailed++;
            }

            problem ??= hashDiffers
                ? $"BlocksHash: erwartet {Short(announced.BlocksHash.Span)}, berechnet {Short(blocksHash)}"
                : null;

            if (problem is null) { equal++; continue; }

            differing++;
            if (shown++ < 5)
            {
                Console.WriteLine($"  abweichend: {entry.Name}");
                Console.WriteLine($"              {problem}");
            }
        }

        clock.Stop();
        differingTotal += differing;

        var megabytes = bytes / 1024.0 / 1024.0;
        var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);

        Console.WriteLine(
            $"[{share.Display}] geprueft {examined}, uebereinstimmend {equal}, abweichend {differing}, " +
            $"uebersprungen {absent + dehydrated + sizeDiffers + withoutBlocks + unreadable}" +
            $" (nicht materialisiert {dehydrated}, nicht vorhanden {absent}, " +
            $"Groesse abweichend {sizeDiffers}, ohne Blockliste {withoutBlocks}, nicht lesbar {unreadable})");
        Console.WriteLine(
            $"          {megabytes:F1} MB in {clock.Elapsed.TotalSeconds:F1} s -- {megabytes / seconds:F1} MB/s");
        Console.WriteLine();
    }

    if (sizesSeen.Count > 0)
    {
        Console.WriteLine("Angekuendigte Blockgroessen im geprueften Bestand:");
        foreach (var (size, count) in sizesSeen)
            Console.WriteLine($"  {size / 1024,6} KiB  {count} Dateien");
        Console.WriteLine();
    }

    Console.WriteLine(sizeRuleChecked == 0
        ? "Blockgroessen-Regel: nicht pruefbar -- keine Datei lag lokal vollstaendig vor."
        : sizeRuleFailed == 0
            ? $"Blockgroessen-Regel bestaetigt: {sizeRuleChecked} Dateien, " +
              $"{sizesSeen.Count} verschiedene Groessen, keine Abweichung."
            : $"Blockgroessen-Regel WIDERLEGT: {sizeRuleFailed} von {sizeRuleChecked} Dateien weichen ab.");

    Console.WriteLine(blocksHashChecked == 0
        ? "BlocksHash-Formel: nicht pruefbar -- keine Ankuendigung trug einen BlocksHash."
        : blocksHashFailed == 0
            ? $"BlocksHash-Formel bestaetigt (SHA-256 ueber die verketteten Blockhashes): " +
              $"{blocksHashChecked} Dateien, keine Abweichung."
            : $"BlocksHash-Formel WIDERLEGT: {blocksHashFailed} von {blocksHashChecked} Dateien weichen ab.");

    return differingTotal == 0 ? 0 : 1;
}

// Im Protokoll ist block_size optional. 0 bedeutet nicht "keine", sondern 128 KiB.
static int AnnouncedBlockSize(BepFileInfo file)
    => file.BlockSize == 0 ? BlockList.MinimumBlockSize : file.BlockSize;

// Liefert die erste Stelle, an der sich die gerechnete Blockliste von der
// angekuendigten unterscheidet, oder null, wenn sie Byte fuer Byte gleich
// sind. Der BlocksHash wird getrennt geprueft, damit er als eigener Befund
// zaehlt und nicht in einer ohnehin abweichenden Liste untergeht.
static string? BlockDifference(BepFileInfo announced, int blockSize, IReadOnlyList<BepBlockInfo> blocks)
{
    var announcedBlockSize = AnnouncedBlockSize(announced);
    if (blockSize != announcedBlockSize)
        return $"Blockgroesse: erwartet {announcedBlockSize / 1024} KiB, berechnet {blockSize / 1024} KiB " +
               $"(Dateigroesse {announced.Size})";

    if (blocks.Count != announced.Blocks.Count)
        return $"Blockzahl: erwartet {announced.Blocks.Count}, berechnet {blocks.Count}";

    for (var i = 0; i < blocks.Count; i++)
    {
        var mine = blocks[i];
        var theirs = announced.Blocks[i];

        if (mine.Offset != theirs.Offset)
            return $"Block {i} Offset: erwartet {theirs.Offset}, berechnet {mine.Offset}";
        if (mine.Size != theirs.Size)
            return $"Block {i} Groesse: erwartet {theirs.Size}, berechnet {mine.Size}";
        if (!mine.Hash.Span.SequenceEqual(theirs.Hash.Span))
            return $"Block {i} Hash: erwartet {Short(theirs.Hash.Span)}, berechnet {Short(mine.Hash.Span)}";
    }

    return null;
}

static string Short(ReadOnlySpan<byte> value)
    => Convert.ToHexStringLower(value[..Math.Min(8, value.Length)]) + (value.Length > 8 ? "..." : "");

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
// verbunden ist. Deshalb wird kurz ein leerer Anbieter verbunden, dann
// geloescht und danach abgemeldet.
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

/// <summary>Leere Quelle fuer den Reparaturmodus. Es soll nichts projiziert werden.</summary>
file sealed class EmptySource : IContentSource
{
    public IReadOnlyList<VirtualEntry> Enumerate() => [];

    public Task<byte[]> ReadAsync(string relativePath, long offset, long length, CancellationToken ct)
        => throw new FileNotFoundException(relativePath);
}
