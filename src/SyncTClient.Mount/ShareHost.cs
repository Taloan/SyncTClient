using Google.Protobuf;
using SyncTClient.Bep;
using SyncTClient.Bep.Proto;
using SyncTClient.Vfs;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;

namespace SyncTClient.Mount;

public enum ShareState
{
    Gestoppt,
    Verbindet,
    Bereit,
    Pausiert,
    Fehler
}

/// <summary>
/// Ein Share von Anfang bis Ende: Verbindung, Index, Platzhalter, Cache.
/// </summary>
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
    private readonly DeviceIdentity _identity;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _hydrationGate = new(ConcurrentHydrations);

    private BepConnection? _connection;
    private PersistentFolderIndex? _index;
    private CloudFilterMount? _mount;
    private HydrationCache? _cache;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private ShareState _state = ShareState.Gestoppt;

    private readonly SemaphoreSlim _indexArrived = new(0);
    private TaskCompletionSource<ClusterConfig> _clusterConfig =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ShareHost(ShareConfig config, AppConfig app, DeviceIdentity identity, Action<string> log)
    {
        _config = config;
        _app = app;
        _identity = identity;
        _log = log;
    }

    public string FolderId => _config.FolderId;

    public ShareConfig Config => _config;

    public string PeerName { get; private set; } = "";

    public ShareState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(value); }
    }

    /// <summary>Angehalten: Platzhalter bleiben, aber es wird nichts mehr geholt.</summary>
    public bool IsPaused { get; private set; }

    public event Action<ShareState>? StateChanged;
    public event Action<TransferInfo>? TransferStarted;
    public event Action<TransferInfo>? TransferFinished;
    public event Action? CacheChanged;

    public int IndexCount => _index?.Count ?? 0;
    public long CacheUsedBytes => _cache?.UsedBytes ?? 0;
    public long CacheMaxBytes => _cache?.MaxBytes ?? 0;
    public int CacheFileCount => _cache?.FileCount ?? 0;

    // ------------------------------------------------------------ Start und Stopp

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State is ShareState.Bereit or ShareState.Verbindet) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _clusterConfig = new TaskCompletionSource<ClusterConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsPaused = false;
        State = ShareState.Verbindet;

        try
        {
            var (host, port) = SplitHostPort(_app.Peer.Address);
            _log($"[{FolderId}] verbinde mit {host}:{port} ...");

            _connection = await BepConnection.ConnectAsync(
                host, port, _identity, DeviceId.Parse(_app.Peer.DeviceId), ct: token);
            PeerName = _connection.PeerHello.DeviceName;
            _log($"[{FolderId}] verbunden mit {PeerName}");

            var databasePath = Path.Combine(_app.HomeDirectory, $"index-{FolderId}.db");
            _index ??= new PersistentFolderIndex(databasePath, FolderId);

            _connection.ClusterConfigReceived += cc => _clusterConfig.TrySetResult(cc);
            _connection.IndexReceived += m => Absorb(m.Folder, m.Files);
            _connection.IndexUpdateReceived += m => Absorb(m.Folder, m.Files);

            _readLoop = _connection.RunAsync(token);

            await NegotiateIndexAsync(token);
            await CollectIndexAsync(token);

            Project();
            State = ShareState.Bereit;

            await ApplyModeAsync(token);
        }
        catch (Exception ex)
        {
            State = ShareState.Fehler;
            _log($"[{FolderId}] Fehler: {ex.Message}");
            throw;
        }
    }

    /// <summary>Trennt die Verbindung und meldet den Sync-Root ab. Der Index bleibt.</summary>
    public async Task StopAsync()
    {
        if (State == ShareState.Gestoppt) return;

        _cache?.Save();
        _mount?.Dispose();
        _mount = null;

        if (_cts is not null) await _cts.CancelAsync();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _readLoop = null;
        IsPaused = false;
        State = ShareState.Gestoppt;
        _log($"[{FolderId}] gestoppt.");
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
        _log($"[{FolderId}] angehalten -- Zugriffe werden abgewiesen.");
    }

    public void Resume()
    {
        if (State != ShareState.Pausiert) return;
        IsPaused = false;
        State = ShareState.Bereit;
        _log($"[{FolderId}] fortgesetzt.");
    }

    // ------------------------------------------------------------ Index

    /// <summary>
    /// Nennt dem Peer, was wir schon haben, damit er nur Neueres schickt.
    /// Hat er seinen Index neu aufgebaut -- erkennbar an einer anderen IndexId --
    /// werfen wir unseren weg und fangen von vorn an.
    /// </summary>
    private async Task NegotiateIndexAsync(CancellationToken ct)
    {
        var peerIndexId = 0UL;
        var maxSequence = 0L;

        // Syncthing schickt seinen ClusterConfig sofort nach dem Hello. Kommt
        // er wider Erwarten nicht, fangen wir eben bei null an.
        var received = await Task.WhenAny(_clusterConfig.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
        if (received == _clusterConfig.Task)
        {
            var folder = _clusterConfig.Task.Result.Folders.FirstOrDefault(f => f.Id == FolderId);
            var peerDevice = folder?.Devices.FirstOrDefault(
                d => DeviceId.FromBytes(d.Id.Span) == _connection!.PeerId);

            if (peerDevice is not null)
            {
                if (_index!.PeerIndexId != 0 && _index.PeerIndexId != peerDevice.IndexId)
                {
                    _log($"[{FolderId}] der Peer hat seinen Index neu aufgebaut -- verwerfe den lokalen.");
                    _index.Clear();
                }

                _index.PeerIndexId = peerDevice.IndexId;
                peerIndexId = peerDevice.IndexId;
                maxSequence = _index.MaxSequence;
            }
        }

        if (maxSequence > 0)
            _log($"[{FolderId}] setze bei Sequenz {maxSequence} fort ({_index!.Count} Eintraege bekannt).");

        var clusterConfig = new ClusterConfig();
        var announce = new Folder { Id = FolderId, Label = FolderId, Type = FolderType.SendReceive };
        announce.Devices.Add(new Device
        {
            Id = ByteString.CopyFrom(_identity.Id.Span),
            MaxSequence = 0,
            IndexId = (ulong)Random.Shared.NextInt64(1, long.MaxValue)
        });
        announce.Devices.Add(new Device
        {
            Id = ByteString.CopyFrom(_connection!.PeerId.Span),
            MaxSequence = maxSequence,
            IndexId = peerIndexId
        });
        clusterConfig.Folders.Add(announce);

        await _connection.SendClusterConfigAsync(clusterConfig, ct);
    }

    private void Absorb(string folder, IEnumerable<BepFileInfo> files)
    {
        if (folder != FolderId) return;

        var changed = _index!.Absorb(files);
        _indexArrived.Release();

        // Geaenderte Dateien duerfen nicht mit alten Bytes im Cache bleiben.
        // Das ist Korrektheit, nicht Cache-Politik.
        if (changed.Count > 0 && _cache is not null)
        {
            if (_cache.Invalidate(changed.Where(_config.Includes)) > 0)
                CacheChanged?.Invoke();
        }
    }

    private async Task CollectIndexAsync(CancellationToken ct)
    {
        _log($"[{FolderId}] warte auf den Index ...");
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline && !(_readLoop?.IsCompleted ?? true))
        {
            var signalled = await _indexArrived.WaitAsync(TimeSpan.FromSeconds(3), ct);
            if (!signalled && (_index!.MessageCount > 0 || _index.Count > 0)) break;
        }

        if (_index!.Count == 0)
            throw new InvalidOperationException(
                $"kein Index empfangen -- Geraet freigegeben und Ordner geteilt?");

        _log($"[{FolderId}] {_index.Count} Eintraege im Index.");
    }

    // ------------------------------------------------------------ Platzhalter

    private void Project()
    {
        _log($"[{FolderId}] registriere Sync-Root: {_config.LocalPath}");
        SyncRoot.Register(_config.LocalPath, "SyncTClient", "0.1");

        var statePath = Path.Combine(_app.HomeDirectory, $"cache-{FolderId}.json");
        var budget = _config.Mode == ShareMode.AlwaysLocal ? 0 : _config.CacheMaxBytes;
        _cache = new HydrationCache(_config.LocalPath, budget, statePath, _log);

        _mount = new CloudFilterMount(_config.LocalPath, this, _log);
        _mount.Connect();
        _mount.ProjectPlaceholders();

        _cache.ReconcileWithDisk();
        CacheChanged?.Invoke();
    }

    private async Task ApplyModeAsync(CancellationToken ct)
    {
        if (_config.Mode != ShareMode.AlwaysLocal) return;

        // "Vollstaendig lokal bereithalten" heisst schlicht: alles einmal
        // anfassen. Der erste Lesezugriff loest die Hydration aus.
        var pending = Enumerate()
            .Where(e => !e.IsDirectory && e.Size > 0)
            .Select(e => Path.Combine(_config.LocalPath, e.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
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

    /// <summary>Haelt das Cache-Budget ein; wird regelmaessig aufgerufen.</summary>
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
                _connection!, FolderId, file, offset, length, _app.Parallelism, progress, ct)
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _index?.Dispose();
        _index = null;
        _indexArrived.Dispose();
        _hydrationGate.Dispose();
        _cts?.Dispose();
    }

    private static (string Host, int Port) SplitHostPort(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon < 0 ? (address, 22000) : (address[..colon], int.Parse(address[(colon + 1)..]));
    }
}
