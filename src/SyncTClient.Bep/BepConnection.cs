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
/// Dieser Client haelt keine Dateien vor. Er nimmt den Index entgegen, der
/// die Blocklisten aller Dateien des Peers enthaelt, und fordert Inhalte erst
/// on-demand blockweise an. Darauf baut das Platzhalter-Verfahren auf.
///
/// Angekuendigt wird nur, was ausdruecklich genannt ist. Zu einer Datei, die
/// wir mit Blockliste angekuendigt haben, fordert die Gegenstelle Bloecke an;
/// diese Anfragen beantwortet <see cref="Serve"/>.
/// </remarks>
public sealed class BepConnection : IAsyncDisposable
{
    private const string BepProtocolName = "bep/1.0";

    /// <summary>
    /// Wieviele Anfragen der Gegenstelle gleichzeitig bedient werden.
    /// </summary>
    /// <remarks>
    /// Jede Bedienung liest von der Platte und rechnet einen SHA-256 darueber.
    /// Ohne Schranke koennte die Gegenstelle allein ueber die Zahl ihrer
    /// Anfragen bestimmen, wieviel Arbeit hier gleichzeitig laeuft.
    /// </remarks>
    private const int ConcurrentServes = 4;

    private readonly TcpClient _tcp;
    private readonly SslStream _tls;

    /// <summary>Zaehlt die Bytes, die ueber diese Verbindung laufen.</summary>
    private readonly CountingStream _wire;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _serveGate = new(ConcurrentServes);
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

    /// <summary>Bytes, die seit dem Verbindungsaufbau empfangen wurden.</summary>
    public long BytesRead => _wire.BytesRead;

    /// <summary>Bytes, die seit dem Verbindungsaufbau gesendet wurden.</summary>
    public long BytesWritten => _wire.BytesWritten;

    /// <summary>
    /// Beschafft die Bytes zu einer Anfrage der Gegenstelle.
    /// </summary>
    /// <remarks>
    /// Diese Klasse verwaltet keine Dateien. Sie reicht die Anfrage an die
    /// obere Schicht weiter und sendet zurueck, was von dort kommt. Ist der
    /// Rueckruf nicht gesetzt, wird die Anfrage mit
    /// <see cref="ErrorCode.NoSuchFile"/> abgelehnt. Aus Sicht des Protokolls
    /// halten wir dann keine Daten.
    ///
    /// Nur bei <see cref="ErrorCode.NoError"/> werden Daten gesendet. Jeder
    /// Fehlercode wird ohne Nutzlast beantwortet.
    /// </remarks>
    public Func<Proto.Request, CancellationToken, Task<(ErrorCode Code, byte[] Data)>>? Serve { get; set; }

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

            var peerHello = await HelloExchange
                .ExchangeAsync(tls, OwnHello(deviceName), ct).ConfigureAwait(false);

            return new BepConnection(tcp, tls, peerId, peerHello);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Nimmt eine Verbindung an, die die Gegenstelle aufgebaut hat: TLS als
    /// Server, danach derselbe Hello-Austausch.
    /// </summary>
    /// <remarks>
    /// Eine erwartete Geraete-ID gibt es hier nicht. Welches Geraet die
    /// Verbindung aufbaut, steht erst nach dem Handschlag fest. Ob dieses
    /// Geraet zugelassen wird, entscheidet der Aufrufer anhand von
    /// <see cref="PeerId"/>.
    /// </remarks>
    public static async Task<BepConnection> AcceptAsync(
        TcpClient tcp, DeviceIdentity identity,
        string deviceName = "SyncTClient", CancellationToken ct = default)
    {
        try
        {
            tcp.NoDelay = true;
            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate,

                // Ohne Zertifikat der Gegenseite laesst sich keine Geraete-ID
                // bilden. Ohne Geraete-ID ist die Verbindung nicht brauchbar.
                ClientCertificateRequired = true,
                ApplicationProtocols = [new SslApplicationProtocol(BepProtocolName)],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }, ct).ConfigureAwait(false);

            if (tls.NegotiatedApplicationProtocol.ToString() != BepProtocolName)
                throw new InvalidDataException(
                    $"Die Gegenstelle hat \"{tls.NegotiatedApplicationProtocol}\" statt \"{BepProtocolName}\" ausgehandelt.");

            var remoteCert = tls.RemoteCertificate
                ?? throw new InvalidDataException("Die Gegenstelle hat kein Zertifikat geliefert.");
            var peerId = DeviceId.FromCertificate(remoteCert.Export(X509ContentType.Cert));

            var peerHello = await HelloExchange
                .ExchangeAsync(tls, OwnHello(deviceName), ct).ConfigureAwait(false);

            return new BepConnection(tcp, tls, peerId, peerHello);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static Hello OwnHello(string deviceName) => new()
    {
        DeviceName = deviceName,
        ClientName = "SyncTClient",
        ClientVersion = "v0.1",
        NumConnections = 1,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000
    };

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
                MessageReceived?.Invoke(type, payload.Length);

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
                        // Ping ist nur ein Lebenszeichen und wird nicht
                        // beantwortet.
                        break;

                    case MessageType.Request:
                        // Nicht in dieser Schleife bedienen: Lesen von der
                        // Platte dauert, und solange die Schleife wartet, wird
                        // keine Response auf unsere eigenen Anfragen
                        // verarbeitet. Bei einer Datei, die wir gerade selbst
                        // hydrieren, entstuende dadurch ein Deadlock.
                        var request = Proto.Request.Parser.ParseFrom(payload);
                        _ = Task.Run(() => ServeAsync(request, ct), CancellationToken.None);
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

    /// <summary>
    /// Bedient eine Anfrage der Gegenstelle abseits der Leseschleife und
    /// schickt die Antwort, sobald sie feststeht.
    /// </summary>
    /// <remarks>
    /// Es wird in jedem Fall geantwortet. Eine unbeantwortete Anfrage laesst
    /// die Gegenstelle bis zum Ablauf ihrer eigenen Frist warten. Nur wenn die
    /// Verbindung bereits geschlossen ist, unterbleibt die Antwort. Die
    /// Gegenstelle erkennt den Abbruch dann selbst.
    /// </remarks>
    private async Task ServeAsync(Proto.Request request, CancellationToken ct)
    {
        var code = ErrorCode.NoSuchFile;
        byte[] data = [];

        try
        {
            var serve = Serve;
            if (serve is not null)
            {
                await _serveGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    (code, data) = await serve(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Die obere Schicht behandelt ihre Fehler selbst. Eine
                    // Ausnahme, die dennoch hier ankommt, wird als eigener
                    // Fehler gemeldet.
                    code = ErrorCode.Generic;
                    data = [];
                }
                finally
                {
                    _serveGate.Release();
                }
            }

            await SendAsync(MessageType.Response, new Response
            {
                Id = request.Id,
                Code = code,
                Data = code == ErrorCode.NoError && data is { Length: > 0 }
                    ? ByteString.CopyFrom(data)
                    : ByteString.Empty
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Abbruch oder geschlossene Verbindung. Die Antwort hat keinen
            // Empfaenger mehr.
        }
    }

    public Task SendClusterConfigAsync(ClusterConfig config, CancellationToken ct = default)
        => SendAsync(MessageType.ClusterConfig, config, ct);

    /// <summary>
    /// Schickt einen Index: unseren vollstaendigen Bestand zu einem Ordner.
    /// </summary>
    /// <remarks>
    /// Die Gegenstelle liest die Nachricht als vollstaendige Angabe und
    /// verwirft, was sie sonst von uns zu diesem Ordner hat. Nachtraege
    /// einzelner Aenderungen laufen ueber IndexUpdate.
    /// </remarks>
    public Task SendIndexAsync(Proto.Index index, CancellationToken ct = default)
        => SendAsync(MessageType.Index, index, ct);

    /// <summary>
    /// Schickt einen Nachtrag: nur die Dateien, die sich seit der letzten
    /// Nachricht geaendert haben.
    /// </summary>
    /// <remarks>
    /// Der Unterschied zum Index liegt nicht im Inhalt, sondern in der
    /// Auslegung: die Gegenstelle behaelt, was sie sonst von uns hat, und
    /// traegt nur die genannten Dateien nach. <c>prev_sequence</c> nennt die
    /// hoechste Nummer der vorigen Nachricht; passt sie nicht zu ihrem Stand,
    /// erkennt die Gegenstelle daran eine Luecke.
    /// </remarks>
    public Task SendIndexUpdateAsync(IndexUpdate update, CancellationToken ct = default)
        => SendAsync(MessageType.IndexUpdate, update, ct);

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

    /// <summary>Was hereinkam: Art und Groesse der Nutzlast.</summary>
    /// <remarks>
    /// Fuer das Protokoll. Ohne diese Meldung sieht man im Diagramm Verkehr
    /// und im Status "abgeglichen", und dazwischen keinen Zusammenhang. Was
    /// daraus wird -- eine Zeile je Nachricht oder eine Zusammenfassung --,
    /// entscheidet der Empfaenger; hier wird nur gemeldet.
    /// </remarks>
    public event Action<MessageType, int>? MessageReceived;

    /// <summary>Was hinausging.</summary>
    public event Action<MessageType, int>? MessageSent;

    private async Task SendAsync(MessageType type, IMessage message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await BepFraming.WriteMessageAsync(_wire, type, message, ct).ConfigureAwait(false);
            MessageSent?.Invoke(type, message.CalculateSize());
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
            // Ein fehlgeschlagenes Close ist beim Aufraeumen ohne Bedeutung.
        }

        await _tls.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
        _writeLock.Dispose();
        _serveGate.Dispose();
    }
}
