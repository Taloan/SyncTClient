using Google.Protobuf;
using SyncTClient.Bep;
using SyncTClient.Bep.Proto;

namespace SyncTClient.Mount;

public enum PeerState
{
    Getrennt,
    Verbindet,
    Verbunden,
    Fehler
}

/// <summary>Ein Ordner, den die Gegenstelle mit uns teilt.</summary>
/// <param name="Accepted">Ob wir ihn schon uebernommen haben.</param>
public sealed record OfferedFolder(string FolderId, string Label, bool Accepted)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? FolderId : $"{Label} ({FolderId})";
}

/// <summary>
/// Eine Gegenstelle mit allen Ordnern, die von ihr kommen.
/// </summary>
/// <remarks>
/// Eine Verbindung je Geraet, nicht je Ordner -- so macht es Syncthing, und
/// nur so laesst sich beantworten, was eine Gegenstelle ueberhaupt anbietet:
/// sie zaehlt es im ClusterConfig auf, den sie gleich nach dem Hello schickt.
/// </remarks>
public sealed class PeerHost : IAsyncDisposable
{
    private readonly PeerConfig _config;
    private readonly AppConfig _app;
    private readonly DeviceIdentity _identity;
    private readonly Action<string> _log;
    private readonly Dictionary<string, ShareHost> _shares = new(StringComparer.Ordinal);

    private BepConnection? _connection;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<ClusterConfig> _clusterConfig = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PeerState _state = PeerState.Getrennt;

    public PeerHost(PeerConfig config, AppConfig app, DeviceIdentity identity, Action<string> log)
    {
        _config = config;
        _app = app;
        _identity = identity;
        _log = log;
    }

    public PeerConfig Config => _config;
    public string DeviceId => _config.DeviceId;

    /// <summary>Wie sich die Gegenstelle selbst nennt.</summary>
    public string ReportedName { get; private set; } = "";

    public string ClientVersion { get; private set; } = "";

    public string Display => string.IsNullOrWhiteSpace(_config.Name)
        ? (string.IsNullOrWhiteSpace(ReportedName) ? _config.ShortId : ReportedName)
        : _config.Name;

    public PeerState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(value); }
    }

    public string? LastError { get; private set; }

    /// <summary>Was die Gegenstelle mit uns teilt -- auch, was wir noch nicht uebernommen haben.</summary>
    public IReadOnlyList<OfferedFolder> Offered { get; private set; } = [];

    public IReadOnlyCollection<ShareHost> Shares => _shares.Values;

    public event Action<PeerState>? StateChanged;
    public event Action? OfferedChanged;

    public ShareHost? ShareFor(string folderId)
        => _shares.TryGetValue(folderId, out var share) ? share : null;

    // ------------------------------------------------------------ Verbinden

    public async Task ConnectAsync(IEnumerable<ShareConfig> shares, CancellationToken ct = default)
    {
        if (State is PeerState.Verbunden or PeerState.Verbindet) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _clusterConfig = new TaskCompletionSource<ClusterConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = PeerState.Verbindet;
        LastError = null;

        try
        {
            var (host, port) = SplitHostPort(_config.Address);
            _log($"[{Display}] verbinde mit {host}:{port} ...");

            _connection = await BepConnection.ConnectAsync(
                host, port, _identity, DeviceId.Length > 0 ? Bep.DeviceId.Parse(DeviceId) : default, ct: token);

            ReportedName = _connection.PeerHello.DeviceName;
            ClientVersion = _connection.PeerHello.ClientVersion;
            _log($"[{Display}] verbunden ({ClientVersion})");

            _connection.ClusterConfigReceived += cc => OnClusterConfig(cc);
            _connection.IndexReceived += m => Route(m.Folder, m.Files);
            _connection.IndexUpdateReceived += m => Route(m.Folder, m.Files);

            _readLoop = _connection.RunAsync(token);

            // Die Ordner vorbereiten, bevor wir ankuendigen -- ihr Indexstand
            // geht in die Ankuendigung ein.
            foreach (var share in shares)
            {
                var host2 = new ShareHost(share, _app, _log);
                host2.OpenIndex();
                _shares[share.FolderId] = host2;
                ShareAdded?.Invoke(host2);
            }

            await NegotiateAsync(token);
            State = PeerState.Verbunden;

            foreach (var share in _shares.Values)
            {
                try { await share.StartAsync(_connection, token); }
                catch (Exception ex) { _log($"[{share.FolderId}] {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            State = PeerState.Fehler;
            _log($"[{Display}] Fehler: {ex.Message}");
            throw;
        }
    }

    public event Action<ShareHost>? ShareAdded;

    /// <summary>
    /// Kuendigt alle Ordner dieser Gegenstelle in einer einzigen Nachricht an
    /// und nennt je Ordner unseren Stand, damit nur Neueres kommt.
    /// </summary>
    private async Task NegotiateAsync(CancellationToken ct)
    {
        // Syncthing schickt seinen ClusterConfig sofort nach dem Hello. Kommt
        // er wider Erwarten nicht, fangen wir eben bei null an.
        await Task.WhenAny(_clusterConfig.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));

        var peerFolders = _clusterConfig.Task.IsCompletedSuccessfully
            ? _clusterConfig.Task.Result.Folders
            : [];

        var announcement = new ClusterConfig();

        foreach (var share in _shares.Values)
        {
            var peerIndexId = 0UL;
            var maxSequence = 0L;

            var folder = peerFolders.FirstOrDefault(f => f.Id == share.FolderId);
            var peerDevice = folder?.Devices.FirstOrDefault(
                d => Bep.DeviceId.FromBytes(d.Id.Span) == _connection!.PeerId);

            if (peerDevice is not null)
            {
                if (share.PeerIndexId != 0 && share.PeerIndexId != peerDevice.IndexId)
                    share.ResetIndex(peerDevice.IndexId);
                else
                    share.RememberPeerIndexId(peerDevice.IndexId);

                peerIndexId = peerDevice.IndexId;
                maxSequence = share.MaxSequence;
            }

            if (maxSequence > 0)
                _log($"[{share.FolderId}] setze bei Sequenz {maxSequence} fort ({share.IndexCount} Eintraege bekannt).");

            var entry = new Folder
            {
                Id = share.FolderId,
                Label = share.Config.Label,
                Type = FolderType.SendReceive
            };
            entry.Devices.Add(new Device
            {
                Id = ByteString.CopyFrom(_identity.Id.Span),
                MaxSequence = 0,
                IndexId = (ulong)Random.Shared.NextInt64(1, long.MaxValue)
            });
            entry.Devices.Add(new Device
            {
                Id = ByteString.CopyFrom(_connection!.PeerId.Span),
                MaxSequence = maxSequence,
                IndexId = peerIndexId
            });
            announcement.Folders.Add(entry);
        }

        await _connection!.SendClusterConfigAsync(announcement, ct);
    }

    private void OnClusterConfig(ClusterConfig config)
    {
        _clusterConfig.TrySetResult(config);

        Offered = config.Folders
            .Select(f => new OfferedFolder(f.Id, f.Label, _shares.ContainsKey(f.Id)))
            .OrderBy(f => f.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        OfferedChanged?.Invoke();
    }

    private void Route(string folderId, IEnumerable<Bep.Proto.FileInfo> files)
    {
        if (_shares.TryGetValue(folderId, out var share)) share.Absorb(files);
    }

    // ------------------------------------------------------------ Verwalten

    /// <summary>Nimmt einen angebotenen Ordner in Betrieb, ohne neu zu verbinden.</summary>
    public async Task<ShareHost> AcceptAsync(ShareConfig share, CancellationToken ct = default)
    {
        if (_connection is null) throw new InvalidOperationException("nicht verbunden.");

        var host = new ShareHost(share, _app, _log);
        host.OpenIndex();
        _shares[share.FolderId] = host;
        ShareAdded?.Invoke(host);

        // Erneut ankuendigen -- jetzt mit dem neuen Ordner dabei.
        await NegotiateAsync(ct);
        await host.StartAsync(_connection, ct);

        MarkOffered();
        return host;
    }

    public async Task UnbindAsync(string folderId)
    {
        if (!_shares.Remove(folderId, out var share)) return;
        await share.UnbindAsync();
        await share.DisposeAsync();
        MarkOffered();
    }

    private void MarkOffered()
    {
        Offered = Offered.Select(o => o with { Accepted = _shares.ContainsKey(o.FolderId) }).ToList();
        OfferedChanged?.Invoke();
    }

    public async Task DisconnectAsync()
    {
        foreach (var share in _shares.Values) await share.StopAsync();

        if (_cts is not null) await _cts.CancelAsync();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _readLoop = null;
        State = PeerState.Getrennt;
        _log($"[{Display}] getrennt.");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        foreach (var share in _shares.Values) await share.DisposeAsync();
        _shares.Clear();
        _cts?.Dispose();
    }

    private static (string Host, int Port) SplitHostPort(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon < 0 ? (address, 22000) : (address[..colon], int.Parse(address[(colon + 1)..]));
    }
}
