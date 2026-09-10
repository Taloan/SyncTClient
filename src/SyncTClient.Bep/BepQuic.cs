using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace SyncTClient.Bep;

/// <summary>
/// Dieselbe Sitzung über QUIC statt über TCP.
/// </summary>
/// <remarks>
/// QUIC läuft über UDP und bringt TLS 1.3 mit, statt es darüberzulegen.
/// Für den Abgleich zählt vor allem eines: UDP lässt sich durch ein NAT
/// hindurch aufbauen, wenn beide Seiten gleichzeitig hinauswählen. Wo TCP
/// eine Portweiterleitung braucht, kommt QUIC oft ohne aus — und wo auch das
/// nicht reicht, bleibt der Relay.
///
/// <para>Warum hier Windows den Handschlag macht und sonst nicht</para>
///
/// Für TCP bringt dieses Programm eine eigene TLS-Umsetzung mit. QUIC lässt
/// das nicht zu: die Verschlüsselung steckt im Protokoll selbst, und
/// <c>System.Net.Quic</c> reicht sie an MsQuic und damit an SChannel weiter.
/// Ein eigener Handschlag ließe sich dort nicht einhängen.
///
/// Tragfähig ist das, weil die Zertifikate es hergeben: nachgemessen an
/// diesem Client und an einem Syncthing v2.1.3 als Gegenstelle tragen beide
/// ECDSA über P-384 mit SHA-256. Das beherrscht SChannel.
///
/// Trifft QUIC doch einmal auf ein Zertifikat, mit dem SChannel nichts
/// anfangen kann, scheitert der Versuch und die nächste Adresse ist an der
/// Reihe. Der Weg über TCP bleibt davon unberührt — er benutzt diese Schicht
/// nicht.
///
/// <para>Geprüft wird nicht die Kette, sondern die Kennung</para>
///
/// Syncthing kennt keine Zertifizierungsstelle; die Identität eines Geräts
/// ist der Hash seines Zertifikats. Die Prüfung gegen die Wurzelspeicher des
/// Systems wird deshalb abgeschaltet und durch den Vergleich der Geräte-ID
/// ersetzt — dieselbe Regel wie bei TCP, nur an anderer Stelle.
///
/// <para>Die Plattformangaben</para>
///
/// <c>System.Net.Quic</c> trägt sie selbst: QUIC gibt es nicht überall. Sie
/// stehen deshalb auch hier, damit der Übersetzer es an der Aufrufstelle
/// sehen kann. Dieses Programm läuft ohnehin nur unter Windows — die Cloud
/// Filter API gibt es sonst nirgends.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public static class BepQuic
{
    private const string ProtokollName = "bep/1.0";

    /// <summary>So lange darf der Aufbau dauern.</summary>
    private static readonly TimeSpan Frist = TimeSpan.FromSeconds(15);

    /// <summary>Ob dieser Rechner QUIC überhaupt kann.</summary>
    /// <remarks>
    /// MsQuic liegt nicht auf jedem System, und unter Windows braucht es eine
    /// hinreichend neue Fassung. Wo es fehlt, wird QUIC übergangen statt
    /// gemeldet: es ist ein zusätzlicher Weg, kein notwendiger.
    /// </remarks>
    public static bool Moeglich => QuicConnection.IsSupported;

    /// <summary>Ob dieser Rechner über QUIC auch annehmen kann.</summary>
    public static bool AnnehmenMoeglich => QuicListener.IsSupported;

    /// <summary>Baut eine Sitzung über QUIC auf.</summary>
    public static async Task<BepConnection> ConnectAsync(
        string host, int port, DeviceIdentity identity, DeviceId expectedPeer,
        string deviceName, CancellationToken ct)
    {
        if (!Moeglich) throw new NotSupportedException("Dieser Rechner bringt kein QUIC mit.");

        var ziel = await AufloesenAsync(host, port, ct).ConfigureAwait(false);

        var einstellungen = new QuicClientConnectionOptions
        {
            RemoteEndPoint = ziel,

            // Wird beim Abbruch einer Sitzung mitgeschickt. Das Protokoll
            // kennt keine eigenen Codes; null heisst "ohne Angabe".
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundBidirectionalStreams = 1,

            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [new SslApplicationProtocol(ProtokollName)],
                ClientCertificates = [identity.Certificate],

                // Der Name spielt keine Rolle -- geprueft wird die
                // Geraete-ID. Leer laesst MsQuic nicht zu, deshalb der Host.
                TargetHost = host,

                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };

        var verbindung = await QuicConnection
            .ConnectAsync(einstellungen, ct)
            .AsTask().WaitAsync(Frist, ct).ConfigureAwait(false);

        QuicStream? strom = null;

        try
        {
            var peerId = KennungVon(verbindung.RemoteCertificate);

            if (expectedPeer != DeviceId.Empty && peerId != expectedPeer)
                throw new InvalidDataException(
                    $"Geraete-ID stimmt nicht. Erwartet: {expectedPeer}, bekommen: {peerId}");

            // Ein Strom je Sitzung. Die Gegenstelle nimmt ihn entgegen und
            // faehrt darauf dasselbe Protokoll wie ueber TCP.
            strom = await verbindung
                .OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct)
                .AsTask().WaitAsync(Frist, ct).ConfigureAwait(false);

            return await BepConnection.UeberQuicAsync(
                verbindung, strom, peerId, deviceName, ct).ConfigureAwait(false);
        }
        catch
        {
            if (strom is not null) await strom.DisposeAsync().ConfigureAwait(false);
            await verbindung.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Nimmt QUIC-Sitzungen entgegen.
    /// </summary>
    /// <remarks>
    /// Das Gegenstück zum Lauscher auf TCP, auf demselben Port — nur über
    /// UDP. Beide nebeneinander zu betreiben ist Absicht: welcher Weg
    /// zustande kommt, entscheidet die Gegenstelle anhand dessen, was in der
    /// Erkennung steht.
    /// </remarks>
    public sealed class Lauscher : IAsyncDisposable
    {
        private readonly DeviceIdentity _identity;
        private readonly string _deviceName;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _cts = new();

        private QuicListener? _lauscher;
        private Task? _lauf;

        public Lauscher(DeviceIdentity identity, string deviceName, Action<string> log)
        {
            _identity = identity;
            _deviceName = deviceName;
            _log = log;
        }

        /// <summary>Eine Gegenstelle hat sich über QUIC verbunden.</summary>
        public event Action<BepConnection, IPEndPoint?>? Eingehend;

        /// <summary>Der Port, auf dem gelauscht wird. Null heißt: gar nicht.</summary>
        public int Port { get; private set; }

        /// <summary>
        /// Beginnt zu lauschen. Ein Fehlschlag ist kein Grund abzubrechen —
        /// TCP läuft davon unberührt weiter.
        /// </summary>
        public async Task<bool> StartAsync(int port)
        {
            if (!AnnehmenMoeglich)
            {
                _log("QUIC: dieser Rechner kann darueber keine Verbindungen annehmen.");
                return false;
            }

            try
            {
                var einstellungen = new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, port),
                    ApplicationProtocols = [new SslApplicationProtocol(ProtokollName)],
                    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(
                        new QuicServerConnectionOptions
                        {
                            DefaultStreamErrorCode = 0,
                            DefaultCloseErrorCode = 0,
                            MaxInboundBidirectionalStreams = 1,

                            ServerAuthenticationOptions = new SslServerAuthenticationOptions
                            {
                                ApplicationProtocols = [new SslApplicationProtocol(ProtokollName)],
                                ServerCertificate = _identity.Certificate,

                                // Ohne Zertifikat der Gegenstelle gibt es
                                // keine Geraete-ID und damit keine
                                // brauchbare Verbindung.
                                ClientCertificateRequired = true,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                RemoteCertificateValidationCallback = static (_, _, _, _) => true
                            }
                        })
                };

                _lauscher = await QuicListener.ListenAsync(einstellungen, _cts.Token).ConfigureAwait(false);
                Port = _lauscher.LocalEndPoint.Port;
                _lauf = Task.Run(() => LaufAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                _log($"QUIC: der Lauscher liess sich nicht starten: {ex.Message}");
                return false;
            }
        }

        private async Task LaufAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _lauscher is not null)
            {
                QuicConnection? verbindung = null;

                try
                {
                    verbindung = await _lauscher.AcceptConnectionAsync(ct).ConfigureAwait(false);
                    var uebernommen = verbindung;
                    verbindung = null;

                    _ = Task.Run(() => HandschlagAsync(uebernommen, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (verbindung is not null) await verbindung.DisposeAsync().ConfigureAwait(false);
                    _log($"QUIC: eine eingehende Verbindung kam nicht zustande: {ex.Message}");
                }
            }
        }

        private async Task HandschlagAsync(QuicConnection verbindung, CancellationToken ct)
        {
            QuicStream? strom = null;

            try
            {
                var peerId = KennungVon(verbindung.RemoteCertificate);

                strom = await verbindung
                    .AcceptInboundStreamAsync(ct)
                    .AsTask().WaitAsync(Frist, ct).ConfigureAwait(false);

                var sitzung = await BepConnection
                    .UeberQuicAsync(verbindung, strom, peerId, _deviceName, ct).ConfigureAwait(false);

                if (Eingehend is { } empfaenger) empfaenger(sitzung, verbindung.RemoteEndPoint);
                else await sitzung.DisposeAsync("nicht uebernommen").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (strom is not null) await strom.DisposeAsync().ConfigureAwait(false);
                await verbindung.DisposeAsync().ConfigureAwait(false);
                _log($"QUIC: Handschlag fehlgeschlagen: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            if (_lauscher is not null) await _lauscher.DisposeAsync().ConfigureAwait(false);

            if (_lauf is not null)
            {
                try { await _lauf.ConfigureAwait(false); }
                catch (Exception) { /* endet mit dem Abbruch */ }
            }

            _cts.Dispose();
        }
    }

    /// <summary>Die Geräte-ID aus dem Zertifikat der Gegenstelle.</summary>
    private static DeviceId KennungVon(X509Certificate? zertifikat)
    {
        if (zertifikat is null)
            throw new MissingPeerCertificateException(
                "Die Gegenstelle hat ueber QUIC kein Zertifikat geliefert.");

        return DeviceId.FromCertificate(zertifikat.GetRawCertData());
    }

    /// <summary>
    /// Löst den Namen auf.
    /// </summary>
    /// <remarks>
    /// QUIC verlangt einen Endpunkt, keinen Namen. Steht in der Adresse schon
    /// eine IP-Adresse — der Regelfall, denn die Erkennung nennt Adressen —,
    /// wird gar nicht erst gefragt.
    /// </remarks>
    private static async Task<IPEndPoint> AufloesenAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var direkt)) return new IPEndPoint(direkt, port);

        var gefunden = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        if (gefunden.Length == 0)
            throw new IOException($"Zu \"{host}\" gibt es keine Adresse.");

        return new IPEndPoint(gefunden[0], port);
    }
}
