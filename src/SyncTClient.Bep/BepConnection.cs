using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using SyncTClient.Bep.Proto;

namespace SyncTClient.Bep;

/// <summary>
/// Eine BEP-Verbindung zu genau einem Peer.
/// </summary>
/// <remarks>
/// Dieser Client haelt bewusst <em>keine</em> Dateien vor. Er nimmt den Index
/// entgegen -- der die Blocklisten aller Dateien des Peers enthaelt -- und
/// fordert Inhalte erst bei Bedarf blockweise an. Genau darauf baut die
/// Platzhalter-Idee auf.
///
/// Weil wir nichts mit Blockliste annoncieren, fragt uns auch niemand nach
/// Daten: Syncthing entfernt beim Setzen lokaler Flags die Blockliste und
/// setzt die Groesse auf null (siehe setNoContent in bep_fileinfo.go).
/// </remarks>
public sealed class BepConnection : IAsyncDisposable
{
    private const string BepProtocolName = "bep/1.0";

    private readonly TcpClient _tcp;
    private readonly SslStream _tls;

    /// <summary>Zaehlt, was ueber diese Verbindung geht.</summary>
    private readonly CountingStream _wire;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextRequestId;

    private BepConnection(TcpClient tcp, SslStream tls, DeviceId peerId, Hello peerHello)
    {
        _tcp = tcp;
        _tls = tls;
        _wire = new CountingStream(tls);
        PeerId = peerId;
        PeerHello = peerHello;
    }

    public DeviceId PeerId { get; }
    public Hello PeerHello { get; }

    /// <summary>Bytes, die seit dem Verbinden ueber die Leitung kamen.</summary>
    public long BytesRead => _wire.BytesRead;

    /// <summary>Bytes, die seit dem Verbinden hinausgingen.</summary>
    public long BytesWritten => _wire.BytesWritten;

    public event Action<ClusterConfig>? ClusterConfigReceived;
    public event Action<Proto.Index>? IndexReceived;
    public event Action<IndexUpdate>? IndexUpdateReceived;
    public event Action<string>? Closed;

    /// <summary>
    /// Baut TLS auf, prueft die Geraete-ID des Peers gegen
    /// <paramref name="expectedPeer"/> und fuehrt den Hello-Austausch durch.
    /// </summary>
    public static async Task<BepConnection> ConnectAsync(
        string host, int port, DeviceIdentity identity, DeviceId expectedPeer,
        string deviceName = "SyncTClient", CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);

            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ClientCertificates = new X509Certificate2Collection(identity.Certificate),
                ApplicationProtocols = [new SslApplicationProtocol(BepProtocolName)],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }, ct).ConfigureAwait(false);

            if (tls.NegotiatedApplicationProtocol.ToString() != BepProtocolName)
                throw new InvalidDataException(
                    $"Peer hat \"{tls.NegotiatedApplicationProtocol}\" statt \"{BepProtocolName}\" ausgehandelt.");

            var remoteCert = tls.RemoteCertificate
                ?? throw new InvalidDataException("Peer hat kein Zertifikat geliefert.");
            var peerId = DeviceId.FromCertificate(remoteCert.Export(X509ContentType.Cert));

            if (expectedPeer != DeviceId.Empty && peerId != expectedPeer)
                throw new InvalidDataException(
                    $"Geraete-ID stimmt nicht.\n  erwartet: {expectedPeer}\n  bekommen: {peerId}");

            var peerHello = await HelloExchange.ExchangeAsync(tls, new Hello
            {
                DeviceName = deviceName,
                ClientName = "SyncTClient",
                ClientVersion = "v0.1",
                NumConnections = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000
            }, ct).ConfigureAwait(false);

            return new BepConnection(tcp, tls, peerId, peerHello);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Die Leseschleife. Laeuft bis zum Abbruch oder bis der Peer schliesst;
    /// muss parallel zu <see cref="RequestAsync"/> laufen.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, payload) = await BepFraming.ReadMessageAsync(_wire, ct).ConfigureAwait(false);

                switch (type)
                {
                    case MessageType.ClusterConfig:
                        ClusterConfigReceived?.Invoke(ClusterConfig.Parser.ParseFrom(payload));
                        break;

                    case MessageType.Index:
                        IndexReceived?.Invoke(Proto.Index.Parser.ParseFrom(payload));
                        break;

                    case MessageType.IndexUpdate:
                        IndexUpdateReceived?.Invoke(IndexUpdate.Parser.ParseFrom(payload));
                        break;

                    case MessageType.Response:
                        var response = Response.Parser.ParseFrom(payload);
                        if (_pending.TryRemove(response.Id, out var waiter))
                            waiter.TrySetResult(response);
                        break;

                    case MessageType.Ping:
                        // Nur ein Lebenszeichen, keine Antwort noetig.
                        break;

                    case MessageType.Request:
                        // Wir halten nichts vor. Hoeflich absagen statt schweigen.
                        var request = Proto.Request.Parser.ParseFrom(payload);
                        await SendAsync(MessageType.Response, new Response
                        {
                            Id = request.Id,
                            Code = ErrorCode.NoSuchFile
                        }, ct).ConfigureAwait(false);
                        break;

                    case MessageType.Close:
                        var close = Close.Parser.ParseFrom(payload);
                        FailPending(new IOException($"Peer hat geschlossen: {close.Reason}"));
                        Closed?.Invoke(close.Reason);
                        return;

                    case MessageType.DownloadProgress:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            FailPending(new OperationCanceledException("Verbindung abgebrochen."));
        }
        catch (Exception ex)
        {
            FailPending(ex);
            Closed?.Invoke(ex.Message);
            throw;
        }
    }

    public Task SendClusterConfigAsync(ClusterConfig config, CancellationToken ct = default)
        => SendAsync(MessageType.ClusterConfig, config, ct);

    /// <summary>
    /// Fordert genau einen Block an. Mehrere Aufrufe duerfen gleichzeitig
    /// laufen; die Zuordnung der Antworten erfolgt ueber die Request-ID.
    /// </summary>
    public async Task<byte[]> RequestAsync(
        string folder, string name, long offset, int size, ByteString hash, int blockNo,
        CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var waiter = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        try
        {
            await SendAsync(MessageType.Request, new Proto.Request
            {
                Id = id,
                Folder = folder,
                Name = name,
                Offset = offset,
                Size = size,
                Hash = hash,
                BlockNo = blockNo
            }, ct).ConfigureAwait(false);

            await using var registration = ct.Register(() => waiter.TrySetCanceled(ct));
            var response = await waiter.Task.ConfigureAwait(false);

            if (response.Code != ErrorCode.NoError)
                throw new IOException($"Peer lehnte Block {blockNo} von \"{name}\" ab: {response.Code}.");

            return response.Data.ToByteArray();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(MessageType type, IMessage message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await BepFraming.WriteMessageAsync(_wire, type, message, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var waiter))
                waiter.TrySetException(ex);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await SendAsync(MessageType.Close, new Close { Reason = "fertig" }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Beim Aufraeumen ist ein fehlgeschlagenes Close belanglos.
        }

        await _tls.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
        _writeLock.Dispose();
    }
}
