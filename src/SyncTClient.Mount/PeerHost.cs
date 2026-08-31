using System.Collections.Concurrent;
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
/// Es gibt eine Verbindung je Geraet, nicht je Ordner. So macht es Syncthing,
/// und nur so laesst sich feststellen, was eine Gegenstelle ueberhaupt
/// anbietet. Sie zaehlt die Ordner im ClusterConfig auf, den sie gleich nach
/// dem Hello schickt.
/// </remarks>
public sealed class PeerHost : IAsyncDisposable
{
    private readonly PeerConfig _config;
    private readonly AppConfig _app;
    private readonly DeviceIdentity _identity;
    private readonly Action<string> _log;
    // Die Freigaben werden aus mehreren Threads gelesen: aus der Leseschleife
    // (Route) und, seit die Gegenstelle Bloecke anfordern kann, aus dem
    // Threadpool. Ein Dictionary vertraegt gleichzeitiges Lesen und Schreiben
    // nicht. Geschrieben wird beim Uebernehmen und beim Loesen.
    private readonly ConcurrentDictionary<string, ShareHost> _shares = new(StringComparer.Ordinal);

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

    /// <summary>Was die Gegenstelle mit uns teilt, auch die noch nicht uebernommenen Ordner.</summary>
    public IReadOnlyList<OfferedFolder> Offered { get; private set; } = [];

    public IReadOnlyCollection<ShareHost> Shares => [.. _shares.Values];

    /// <summary>Bytes, die seit dem Verbinden ueber diese Verbindung liefen.</summary>
    public (long Read, long Written) Wire =>
        _connection is null ? (0, 0) : (_connection.BytesRead, _connection.BytesWritten);

    public event Action<PeerState>? StateChanged;
    public event Action? OfferedChanged;

    public ShareHost? ShareFor(string folderId)
        => _shares.TryGetValue(folderId, out var share) ? share : null;

    // ------------------------------------------------------------ Verbinden

    public async Task ConnectAsync(IEnumerable<ShareConfig> shares, CancellationToken ct = default)
    {
        if (State is PeerState.Verbunden or PeerState.Verbindet) return;

        var token = Begin(ct);

        try
        {
            await RunSessionAsync(await DialAsync(token), shares, token);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Baut die Verbindung zur Gegenstelle auf, unter der eingetragenen
    /// Adresse oder unter den Adressen aus der Erkennung.
    /// </summary>
    /// <remarks>
    /// Mehrere Adressen sind der Normalfall: ein Geraet meldet seine lokale
    /// Adresse, seine oeffentliche und die seines Relays. Verwendet wird die
    /// erste, ueber die eine Verbindung zustande kommt.
    /// </remarks>
    private async Task<BepConnection> DialAsync(CancellationToken ct)
    {
        var expected = DeviceId.Length > 0 ? Bep.DeviceId.Parse(DeviceId) : Bep.DeviceId.Empty;
        var candidates = await CandidatesAsync(expected, ct);

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "keine Adresse bekannt -- weder eingetragen noch von der Erkennung genannt.");

        Exception? last = null;

        foreach (var candidate in candidates)
        {
            if (candidate.StartsWith("relay://", StringComparison.OrdinalIgnoreCase))
            {
                if (!_app.Relays || !_config.Relays)
                {
                    _log($"[{Display}] Relay uebergangen -- fuer diese Gegenstelle abgeschaltet.");
                    last ??= new NotSupportedException(
                        "nur ueber einen Relay zu erreichen, und Relays sind abgeschaltet.");
                    continue;
                }

                _log($"[{Display}] {candidate}");
                last ??= new NotSupportedException(
                    "die Gegenstelle ist nur ueber einen Relay zu erreichen -- das kann dieser Client noch nicht.");
                continue;
            }

            if (candidate.StartsWith("quic://", StringComparison.OrdinalIgnoreCase))
            {
                last ??= new NotSupportedException("die Gegenstelle bietet nur QUIC an.");
                continue;
            }

            try
            {
                var (host, port) = SplitHostPort(Bare(candidate));
                _log($"[{Display}] verbinde mit {host}:{port} ...");
                return await BepConnection.ConnectAsync(host, port, _identity, expected, ct: ct);
            }
            catch (Exception ex)
            {
                last = ex;
                _log($"[{Display}] {candidate} fuehrt nicht zum Ziel: {ex.Message}");
            }
        }

        throw last ?? new IOException("keine der Adressen fuehrte zu einer Verbindung.");
    }

    /// <summary>Alle Adressen, unter denen die Gegenstelle zu versuchen ist.</summary>
    private async Task<IReadOnlyList<string>> CandidatesAsync(Bep.DeviceId expected, CancellationToken ct)
    {
        // Ist eine Adresse eingetragen, wird nicht gesucht.
        if (!string.IsNullOrWhiteSpace(_config.Address) &&
            !_config.Address.Equals("dynamic", StringComparison.OrdinalIgnoreCase))
            return [_config.Address];

        if (expected == Bep.DeviceId.Empty) return [];

        // Zuerst im eigenen Netz nachsehen. Das ist schneller und genauer,
        // und kein Server erfaehrt dabei, wonach gesucht wird.
        var local = _app.Local?.AddressesFor(expected) ?? [];
        if (local.Count > 0)
        {
            _log($"[{Display}] im lokalen Netz gefunden: {string.Join(", ", local)}");
            return local;
        }

        if (!_app.Discovery || !_config.Discovery)
        {
            _log($"[{Display}] keine Adresse eingetragen, und die Erkennung ist abgeschaltet.");
            return [];
        }

        _log($"[{Display}] frage die Erkennung nach {expected.Short()} ...");

        foreach (var server in _app.LookupServers)
        {
            try
            {
                using var discovery = new GlobalDiscovery(server);
                var found = await discovery.LookupAsync(expected, ct);

                // Die Antwort des ersten Servers mit einem Ergebnis genuegt,
                // die uebrigen liefern dieselben Adressen.
                if (found.Count == 0) continue;

                _log($"[{Display}] Erkennung nennt {found.Count}: {string.Join(", ", found)}");
                return found;
            }
            catch (Exception ex)
            {
                _log($"[{Display}] {Bep.GlobalDiscovery.HostOf(server)} antwortet nicht: {ex.Message}");
            }
        }

        _log($"[{Display}] die Erkennung kennt keine Adresse.");
        return [];
    }

    /// <summary>Entfernt aus einer Adresse das Schema und alles hinter dem Host.</summary>
    private static string Bare(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        var bare = scheme < 0 ? address : address[(scheme + 3)..];

        var slash = bare.IndexOf('/');
        return slash < 0 ? bare : bare[..slash];
    }

    /// <summary>
    /// Uebernimmt eine Verbindung, die die Gegenstelle aufgebaut hat.
    /// </summary>
    /// <remarks>
    /// Ab dem Hello gibt es keinen Unterschied mehr. Keine Nachricht des
    /// Protokolls sagt, welche Seite die Verbindung aufgebaut hat. Deshalb
    /// unterscheidet sich hier nur der Aufbau, die Sitzung selbst ist
    /// dieselbe.
    ///
    /// Nur auf diesem Weg erreicht uns eine Gegenstelle, zu der wir keine
    /// Verbindung aufbauen koennen, weil ihre Adresse wechselt oder weil sie
    /// hinter einem Router liegt.
    /// </remarks>
    public async Task AcceptAsync(
        BepConnection connection, IEnumerable<ShareConfig> shares, CancellationToken ct = default)
    {
        if (State is PeerState.Verbunden or PeerState.Verbindet)
        {
            // Eine zweite Verbindung zur selben Gegenstelle wird nicht
            // gebraucht.
            await connection.DisposeAsync();
            return;
        }

        var token = Begin(ct);
        _log($"[{Display}] eingehende Verbindung angenommen.");

        try
        {
            await RunSessionAsync(connection, shares, token);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    private CancellationToken Begin(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _clusterConfig = new TaskCompletionSource<ClusterConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = PeerState.Verbindet;
        LastError = null;
        return _cts.Token;
    }

    private void Fail(Exception ex)
    {
        LastError = ex.Message;
        State = PeerState.Fehler;
        _log($"[{Display}] Fehler: {ex.Message}");
    }

    /// <summary>Alles, was nach dem Hello gleich ablaeuft, ob aufgebaut oder angenommen.</summary>
    private async Task RunSessionAsync(
        BepConnection connection, IEnumerable<ShareConfig> shares, CancellationToken token)
    {
        _connection = connection;

        ReportedName = connection.PeerHello.DeviceName;
        ClientVersion = connection.PeerHello.ClientVersion;
        _log($"[{Display}] verbunden ({ClientVersion})");

        connection.ClusterConfigReceived += cc => OnClusterConfig(cc);
        connection.IndexReceived += m => Route(m.Folder, m.Files);
        connection.IndexUpdateReceived += m => Route(m.Folder, m.Files);
        connection.Serve = ServeAsync;

        _readLoop = connection.RunAsync(token);

        // Die Ordner vorbereiten, bevor angekuendigt wird. Ihr Indexstand
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
            try { await share.StartAsync(connection, token); }
            catch (Exception ex) { _log($"[{share.FolderId}] {ex.Message}"); }
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
        // er wider Erwarten nicht, wird bei null begonnen.
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
            // Unser eigener Eintrag. Beide Zahlen sind verbindliche Angaben
            // an die Gegenstelle: eine wechselnde IndexId bedeutet, dass die
            // Gegenstelle alles bisher ueber uns Gespeicherte verwerfen soll.
            // NegotiateAsync laeuft auch mitten in der Sitzung, etwa beim
            // Uebernehmen eines Ordners.
            entry.Devices.Add(new Device
            {
                Id = ByteString.CopyFrom(_identity.Id.Span),
                MaxSequence = share.LocalSequence,
                IndexId = share.OwnIndexId
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

    /// <summary>
    /// Reicht eine Blockanfrage der Gegenstelle an den zustaendigen Ordner
    /// weiter, so wie <see cref="Route"/> es fuer Index-Nachrichten tut.
    /// </summary>
    /// <remarks>
    /// Die Anfrage nennt den Ordner selbst. Ist er hier nicht eingerichtet,
    /// faellt die Antwort so aus wie fuer eine unbekannte Datei.
    /// </remarks>
    private Task<(ErrorCode Code, byte[] Data)> ServeAsync(Request request, CancellationToken ct)
    {
        if (_shares.TryGetValue(request.Folder, out var share))
            return share.ServeAsync(request, ct);

        _log($"[{Display}] Anfrage nach \"{request.Name}\" abgelehnt: Ordner \"{request.Folder}\" ist hier nicht eingerichtet.");
        return Task.FromResult<(ErrorCode, byte[])>((ErrorCode.NoSuchFile, []));
    }

    // ------------------------------------------------------------ Verwalten

    /// <summary>
    /// Erster Schritt zum Uebernehmen: den Ordner ankuendigen und seinen
    /// Index holen. Im Explorer entsteht dabei noch nichts.
    /// </summary>
    /// <remarks>
    /// Getrennt vom zweiten Schritt, damit vorher gefragt werden kann, wohin
    /// der Ordner soll und welche Zweige daraus uebernommen werden. Beides
    /// laesst sich erst entscheiden, wenn der Inhalt bekannt ist.
    /// </remarks>
    public async Task<ShareHost> PrepareAsync(ShareConfig share, CancellationToken ct = default)
    {
        if (_connection is null) throw new InvalidOperationException("nicht verbunden.");

        var host = new ShareHost(share, _app, _log);
        host.OpenIndex();
        _shares[share.FolderId] = host;

        // Erneut ankuendigen, jetzt mit dem neuen Ordner.
        await NegotiateAsync(ct);
        await host.PrepareAsync(_connection, ct);

        return host;
    }

    /// <summary>Zweiter Schritt: uebernehmen, was bestaetigt wurde.</summary>
    public async Task CommitAsync(ShareHost host, CancellationToken ct = default)
    {
        ShareAdded?.Invoke(host);
        await host.CommitAsync(ct);
        MarkOffered();
    }

    /// <summary>
    /// Nimmt zurueck, was <see cref="PrepareAsync"/> angelegt hat.
    /// </summary>
    /// <remarks>
    /// Ohne die erneute Ankuendigung schickt die Gegenstelle weiter
    /// Aktualisierungen fuer einen Ordner, der nicht uebernommen wurde.
    /// </remarks>
    public async Task DiscardAsync(ShareHost host)
    {
        _shares.TryRemove(host.FolderId, out _);

        await host.UnbindAsync();
        await host.DisposeAsync();

        if (_connection is not null && State == PeerState.Verbunden)
        {
            try { await NegotiateAsync(CancellationToken.None); }
            catch (Exception ex) { _log($"[{Display}] {ex.Message}"); }
        }

        MarkOffered();
    }

    /// <summary>Beide Schritte auf einmal, fuer Aufrufer ohne Rueckfrage.</summary>
    public async Task<ShareHost> AcceptAsync(ShareConfig share, CancellationToken ct = default)
    {
        var host = await PrepareAsync(share, ct);
        await CommitAsync(host, ct);
        return host;
    }

    public async Task UnbindAsync(string folderId)
    {
        if (!_shares.TryRemove(folderId, out var share)) return;
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
