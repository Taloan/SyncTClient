using SyncTClient.Bep;
using SyncTClient.Vfs;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;

namespace SyncTClient.Mount;

public enum ShareState
{
    Gestoppt,
    Wartet,
    Bereit,
    Pausiert,
    Fehler
}

/// <summary>
/// Ein Share: Index, Platzhalter, Cache, Vorschaubilder.
/// </summary>
/// <remarks>
/// Die Verbindung gehoert ihm nicht -- die haelt <see cref="PeerHost"/> und
/// teilt sie unter allen Ordnern derselben Gegenstelle. Syncthing macht es
/// genauso: eine Verbindung je Geraet, nicht je Ordner.
/// </remarks>
public sealed class ShareHost : IAsyncDisposable, IContentSource
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS -- Inhalt liegt noch nicht lokal.</summary>
    private const uint RecallOnDataAccess = 0x0040_0000;

    /// <summary>
    /// Wieviele Dateien gleichzeitig geholt werden. Der Rest wartet sichtbar --
    /// ohne diese Schranke gaebe es keine Warteschlange, sondern nur einen
    /// Schwarm, der sich gegenseitig die Bandbreite wegnimmt.
    /// </summary>
    private const int ConcurrentHydrations = 3;

    private readonly ShareConfig _config;
    private readonly AppConfig _app;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _hydrationGate = new(ConcurrentHydrations);
    private readonly SemaphoreSlim _indexArrived = new(0);

    private BepConnection? _connection;
    private PersistentFolderIndex? _index;
    private CloudFilterMount? _mount;
    private HydrationCache? _cache;
    private ThumbnailStore? _thumbnails;
    private Task? _thumbnailJob;
    private string? _syncRootId;
    private ShareState _state = ShareState.Gestoppt;

    public ShareHost(ShareConfig config, AppConfig app, Action<string> log)
    {
        _config = config;
        _app = app;
        _log = log;
    }

    public string FolderId => _config.FolderId;
    public ShareConfig Config => _config;

    public ShareState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(value); }
    }

    public bool IsPaused { get; private set; }

    public event Action<ShareState>? StateChanged;
    public event Action<TransferInfo>? TransferStarted;
    public event Action<TransferInfo>? TransferFinished;
    public event Action? CacheChanged;
    public event Action<int, int>? ThumbnailProgress;

    // ------------------------------------------------------------ Zahlen

    public int IndexCount => _index?.Count ?? 0;
    public long IndexBytes => _index?.TotalBytes ?? 0;
    public long MaxSequence => _index?.MaxSequence ?? 0;
    public ulong PeerIndexId => _index?.PeerIndexId ?? 0;
    public long CacheUsedBytes => _cache?.UsedBytes ?? 0;
    public long CacheMaxBytes => _cache?.MaxBytes ?? 0;
    public int CacheFileCount => _cache?.FileCount ?? 0;
    public (int Count, long Bytes) ThumbnailUsage() => _thumbnails?.Usage() ?? (0, 0);

    // ------------------------------------------------------------ Index

    /// <summary>
    /// Oeffnet die Datenbank, bevor die Gegenstelle angesprochen wird -- ihr
    /// Stand geht in die Ankuendigung ein, damit nur Neueres kommt.
    /// </summary>
    public void OpenIndex()
    {
        var databasePath = Path.Combine(_app.HomeDirectory, $"index-{FolderId}.db");
        _index ??= new PersistentFolderIndex(databasePath, FolderId);
    }

    /// <summary>Der Peer hat seinen Index neu aufgebaut -- unserer ist wertlos.</summary>
    public void ResetIndex(ulong newPeerIndexId)
    {
        _log($"[{FolderId}] die Gegenstelle hat ihren Index neu aufgebaut -- verwerfe den lokalen.");
        _index!.Clear();
        _index.PeerIndexId = newPeerIndexId;
    }

    public void RememberPeerIndexId(ulong id)
    {
        if (_index is not null) _index.PeerIndexId = id;
    }

    /// <summary>Nimmt einen Stapel Index-Eintraege auf, den der PeerHost zugestellt hat.</summary>
    public void Absorb(IEnumerable<BepFileInfo> files)
    {
        var changed = _index!.Absorb(files);
        _indexArrived.Release();

        // Geaenderte Dateien duerfen nicht mit alten Bytes im Cache bleiben.
        // Das ist Korrektheit, nicht Cache-Politik.
        if (changed.Count > 0 && _cache is not null && _cache.Invalidate(
                changed.Where(_config.Includes).Select(LocalPathOf)) > 0)
        {
            CacheChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------ Start und Stopp

    public async Task StartAsync(BepConnection connection, CancellationToken ct)
    {
        _connection = connection;
        State = ShareState.Wartet;

        try
        {
            await WaitForIndexAsync(ct);
            await ProjectAsync();
            State = ShareState.Bereit;

            // Vorschaubilder im Hintergrund -- der Nutzer soll nicht warten.
            _thumbnailJob = Task.Run(() => GenerateThumbnailsAsync(ct), ct);

            await ApplyModeAsync(ct);
        }
        catch (Exception ex)
        {
            State = ShareState.Fehler;
            _log($"[{FolderId}] {ex.Message}");
            throw;
        }
    }

    private async Task WaitForIndexAsync(CancellationToken ct)
    {
        if (_index!.Count > 0) return;

        _log($"[{FolderId}] warte auf den Index ...");
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var signalled = await _indexArrived.WaitAsync(TimeSpan.FromSeconds(3), ct);
            if (!signalled && _index.Count > 0) break;
        }

        if (_index.Count == 0)
            throw new InvalidOperationException(
                "kein Index empfangen -- ist der Ordner mit diesem Geraet geteilt?");
    }

    public async Task StopAsync()
    {
        if (State == ShareState.Gestoppt) return;

        _cache?.Save();
        _mount?.Dispose();
        _mount = null;
        _connection = null;
        IsPaused = false;
        State = ShareState.Gestoppt;
    }

    /// <summary>
    /// Haelt an, ohne die Platzhalter aufzugeben. Anfragen werden abgewiesen
    /// statt liegengelassen -- ein wartender Zugriff wuerde den Explorer
    /// blockieren, bis Windows von selbst aufgibt.
    /// </summary>
    public void Pause()
    {
        if (State != ShareState.Bereit) return;
        IsPaused = true;
        State = ShareState.Pausiert;
    }

    public void Resume()
    {
        if (State != ShareState.Pausiert) return;
        IsPaused = false;
        State = ShareState.Bereit;
    }

    // ------------------------------------------------------------ Platzhalter

    private async Task ProjectAsync()
    {
        _log($"[{FolderId}] registriere Sync-Root: {_config.LocalPath}");

        // Ueber StorageProviderSyncRootManager statt CfRegisterSyncRoot: nur
        // dieser Weg legt den Registry-Schluessel an, an dem die
        // Vorschau-Erweiterung haengt. Nebenbei erscheint der Ordner mit Namen
        // und Symbol in der Navigationsleiste des Explorers.
        var name = string.IsNullOrWhiteSpace(_config.Label) ? FolderId : _config.Label;
        _syncRootId = await WinRtSyncRoot.RegisterAsync(_config.LocalPath, $"SyncT {name}", "0.1");

        var statePath = Path.Combine(_app.HomeDirectory, $"cache-{FolderId}.json");
        var budget = _config.Mode == ShareMode.AlwaysLocal ? 0 : _config.CacheMaxBytes;
        _cache = new HydrationCache(_config.LocalPath, budget, statePath, _log);

        _thumbnails = new ThumbnailStore(Path.Combine(_app.HomeDirectory, "thumbs"));
        RegisterThumbnailProvider();

        _mount = new CloudFilterMount(_config.LocalPath, this, _log);
        _mount.Connect();
        _mount.ProjectPlaceholders();

        _cache.ReconcileWithDisk();
        CacheChanged?.Invoke();
    }

    /// <summary>
    /// Meldet die Shell-Erweiterung an, damit der Explorer die vorbereiteten
    /// Vorschauen zeigt statt eines Ersatzsymbols.
    /// </summary>
    private void RegisterThumbnailProvider()
    {
        if (!_config.GenerateThumbnails || _syncRootId is null || _thumbnails is null) return;

        var library = ThumbnailProviderRegistration.FindLibrary();
        if (library is null)
        {
            _log($"[{FolderId}] synctthumbs.dll nicht gefunden -- keine Vorschaubilder im Explorer.");
            return;
        }

        try
        {
            ThumbnailProviderRegistration.RegisterClass(library, _thumbnails.Directory);
            if (!ThumbnailProviderRegistration.AttachToSyncRoot(_syncRootId))
                _log($"[{FolderId}] Vorschau-Erweiterung liess sich nicht am Sync-Root eintragen.");
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Vorschau-Erweiterung: {ex.Message}");
        }
    }

    private async Task ApplyModeAsync(CancellationToken ct)
    {
        if (_config.Mode != ShareMode.AlwaysLocal) return;

        // "Vollstaendig lokal bereithalten" heisst schlicht: alles einmal
        // anfassen. Der erste Lesezugriff loest die Hydration aus.
        var pending = Enumerate()
            .Where(e => !e.IsDirectory && e.Size > 0)
            .Select(e => LocalPathOf(e.RelativePath))
            .Where(p => File.Exists(p) && ((uint)new System.IO.FileInfo(p).Attributes & RecallOnDataAccess) != 0)
            .ToList();

        if (pending.Count == 0) return;
        _log($"[{FolderId}] Modus AlwaysLocal: hole {pending.Count} noch fehlende Dateien ...");

        var done = 0;
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (path, token) =>
            {
                try
                {
                    // Ein einziges Byte genuegt: der Lesezugriff loest die
                    // Hydration der ganzen Datei aus.
                    await using var stream = File.OpenRead(path);
                    var probe = new byte[1];
                    await stream.ReadExactlyAsync(probe, token);
                }
                catch (Exception ex)
                {
                    _log($"  {Path.GetFileName(path)}: {ex.Message}");
                }

                if (Interlocked.Increment(ref done) % 50 == 0)
                    _log($"[{FolderId}] {done}/{pending.Count} geholt.");
            });

        _log($"[{FolderId}] vollstaendig lokal.");
    }

    // ------------------------------------------------------------ Vorschaubilder

    /// <summary>
    /// Holt fuer jedes Bild den Dateikopf und legt die darin eingebettete
    /// Vorschau ab. Ein Kopf ist genau ein Block -- bei 510 Fotos kostet das
    /// rund 64 MB statt der 943 MB, die die vollen Dateien haetten.
    /// </summary>
    private async Task GenerateThumbnailsAsync(CancellationToken ct)
    {
        if (_thumbnails is null || !_config.GenerateThumbnails || _connection is null) return;

        var pending = Enumerate()
            .Where(e => !e.IsDirectory && e.Size > 0 && ExifThumbnail.LooksLikeJpeg(e.RelativePath))
            .Where(e => !_thumbnails.Has(LocalPathOf(e.RelativePath)))
            .ToList();

        if (pending.Count == 0) return;
        _log($"[{FolderId}] erzeuge Vorschaubilder fuer {pending.Count} Bilder ...");

        int done = 0, made = 0;

        // Absichtlich genuegsam: das laeuft nebenher und darf einen
        // Doppelklick des Nutzers nicht ausbremsen.
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (entry, token) =>
            {
                try
                {
                    if (!_index!.TryGet(entry.RelativePath, out var file)) return;

                    var wanted = Math.Min(ExifThumbnail.RequiredPrefixBytes, entry.Size);

                    // Direkt ueber BEP, nicht ueber das Dateisystem: der
                    // Platzhalter bleibt dabei dehydriert.
                    var head = await FileFetcher.FetchRangeAsync(
                        _connection, FolderId, file, 0, wanted, _app.Parallelism, ct: token)
                        .ConfigureAwait(false);

                    var thumbnail = ExifThumbnail.TryExtract(head);
                    if (thumbnail is not null)
                    {
                        _thumbnails.Save(LocalPathOf(entry.RelativePath), thumbnail);
                        Interlocked.Increment(ref made);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log($"  Vorschau fuer \"{entry.RelativePath}\": {ex.Message}");
                }

                var count = Interlocked.Increment(ref done);
                if (count % 25 == 0 || count == pending.Count)
                {
                    ThumbnailProgress?.Invoke(count, pending.Count);
                    _log($"[{FolderId}] Vorschaubilder: {count}/{pending.Count}");
                }
            });

        _log($"[{FolderId}] {made} Vorschaubilder erzeugt.");
    }

    // ------------------------------------------------------------ Cache

    public async Task EnforceBudgetAsync()
    {
        if (_cache is null) return;
        await _cache.EnforceBudgetAsync();
        CacheChanged?.Invoke();
    }

    public string Stats()
        => _cache is null
            ? $"[{FolderId}] noch nicht bereit"
            : $"[{FolderId}] {_cache.FileCount} Dateien lokal, " +
              $"{_cache.UsedBytes / (1024.0 * 1024.0):0.0} MB" +
              (_cache.MaxBytes > 0 ? $" von {_cache.MaxBytes / (1024.0 * 1024.0):0.0} MB" : "");

    // ------------------------------------------------------------ IContentSource

    public IReadOnlyList<VirtualEntry> Enumerate()
        => _index!.EnumerateLight()
            .Where(e => _config.Includes(e.Name))
            .Select(e => new VirtualEntry(
                e.Name, e.Size, DateTimeOffset.FromUnixTimeSeconds(e.ModifiedS), e.IsDirectory))
            .ToList();

    public async Task<byte[]> ReadAsync(string relativePath, long offset, long length, CancellationToken ct)
    {
        if (IsPaused)
            throw new InvalidOperationException($"\"{FolderId}\" ist angehalten.");
        if (_connection is null)
            throw new InvalidOperationException($"\"{FolderId}\" ist nicht verbunden.");

        if (!_index!.TryGet(relativePath, out var file))
            throw new FileNotFoundException($"\"{relativePath}\" ist nicht im Index.");

        var transfer = new TransferInfo(FolderId, relativePath, length);
        TransferStarted?.Invoke(transfer);

        // Ab hier steht der Auftrag in der Warteschlange, bis ein Platz frei wird.
        await _hydrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            transfer.State = TransferState.Laeuft;

            var blockSize = Math.Max(file.BlockSize, 1);
            var progress = new Progress<int>(blocks =>
                transfer.DoneBytes = Math.Min((long)blocks * blockSize, length));

            var data = await FileFetcher.FetchRangeAsync(
                _connection, FolderId, file, offset, length, _app.Parallelism, progress, ct)
                .ConfigureAwait(false);

            transfer.DoneBytes = data.Length;
            transfer.State = TransferState.Fertig;

            _cache?.NoteHydrated(relativePath, data.Length);
            CacheChanged?.Invoke();

            // Nach dem Zuwachs pruefen, ob das Budget noch stimmt -- im
            // Hintergrund, damit der Hydrations-Rueckruf nicht darauf wartet.
            _ = Task.Run(EnforceBudgetAsync, CancellationToken.None);

            return data;
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Fehler;
            transfer.Error = ex.Message;
            throw;
        }
        finally
        {
            _hydrationGate.Release();
            TransferFinished?.Invoke(transfer);
        }
    }

    // ------------------------------------------------------------ Ende

    /// <summary>
    /// Loest die Bindung vollstaendig: Sync-Root abmelden, Vorschaubilder
    /// verwerfen, Index loeschen. Die lokalen Dateien bleiben, wo sie sind --
    /// darueber entscheidet der Aufrufer.
    /// </summary>
    public async Task UnbindAsync()
    {
        await StopAsync();

        if (_syncRootId is not null)
        {
            ThumbnailProviderRegistration.DetachFromSyncRoot(_syncRootId);
            try { WinRtSyncRoot.Unregister(_syncRootId); } catch { /* schon weg */ }
            _syncRootId = null;
        }

        _index?.Dispose();
        _index = null;

        var databasePath = Path.Combine(_app.HomeDirectory, $"index-{FolderId}.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(databasePath + suffix); } catch { /* egal */ }

        try { File.Delete(Path.Combine(_app.HomeDirectory, $"cache-{FolderId}.json")); } catch { /* egal */ }

        _log($"[{FolderId}] Bindung geloest.");
    }

    private string LocalPathOf(string relativePath)
        => Path.Combine(_config.LocalPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _index?.Dispose();
        _index = null;
        _indexArrived.Dispose();
        _hydrationGate.Dispose();
    }
}
