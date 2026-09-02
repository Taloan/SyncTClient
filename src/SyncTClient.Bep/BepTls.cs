using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

// "HashAlgorithm" gibt es in beiden Namensraeumen. Gemeint ist hier immer die
// Kennzahl aus dem Protokoll, nicht der Rechner aus .NET.
using TlsHash = Org.BouncyCastle.Tls.HashAlgorithm;

// Dasselbe bei der Zertifikatsanforderung: gemeint ist die aus dem Protokoll,
// nicht die aus .NET, mit der man ein Zertifikat ausstellt.
using TlsCertificateRequest = Org.BouncyCastle.Tls.CertificateRequest;

namespace SyncTClient.Bep;

/// <summary>
/// Der TLS-Handschlag fuer BEP, mit eigener Umsetzung statt der von Windows.
/// </summary>
/// <remarks>
/// Syncthing erzeugt die Zertifikate fuer Sync-Verbindungen mit Ed25519.
/// Windows kennt dieses Verfahren nicht, und <c>SslStream</c> reicht dort an
/// SChannel weiter; eine solche Gegenstelle ist damit nicht erreichbar --
/// eingehend liefert sie kein Zertifikat, ausgehend bricht der Handschlag ab.
/// Syncthing selbst laeuft auf demselben Windows ohne diese Einschraenkung,
/// weil Go seine TLS-Umsetzung mitbringt. Hier wird es genauso gehalten.
///
/// Ausgehandelt wird ausschliesslich TLS 1.3. Syncthing 2 laesst nichts
/// anderes zu, und die beiden Fassungen behandeln Zertifikate verschieden
/// genug, dass zwei Fassungen zwei Wege waeren.
/// </remarks>
public static class BepTls
{
    private const string BepProtocolName = "bep/1.0";

    /// <summary>So lange darf der Handschlag dauern.</summary>
    private static readonly TimeSpan Frist = TimeSpan.FromSeconds(15);

    /// <summary>Was nach dem Handschlag feststeht.</summary>
    public sealed class Verbindung(Stream stream, byte[] peerCertificate, string beschreibung)
    {
        /// <summary>Der Klartextstrom. Alles Weitere laeuft darueber.</summary>
        public Stream Stream { get; } = stream;

        /// <summary>Das Zertifikat der Gegenstelle, aus dem die Kennung entsteht.</summary>
        public byte[] PeerCertificate { get; } = peerCertificate;

        /// <summary>Fassung, Verfahren und Anwendungsprotokoll, fuer das Protokoll.</summary>
        public string Beschreibung { get; } = beschreibung;
    }

    /// <summary>Baut die Verbindung als Client auf.</summary>
    public static Task<Verbindung> ConnectAsync(
        Stream transport, DeviceIdentity identity, CancellationToken ct)
        => Aushandeln(transport, ct, () =>
        {
            var crypto = new BcTlsCrypto(new SecureRandom());
            var client = new BepClient(crypto, identity);
            var protokoll = new TlsClientProtocol(transport);

            protokoll.Connect(client);
            return ((TlsProtocol)protokoll, client.PeerCertificate, client.Kontext);
        });

    /// <summary>Nimmt die Verbindung als Server an.</summary>
    public static Task<Verbindung> AcceptAsync(
        Stream transport, DeviceIdentity identity, CancellationToken ct)
        => Aushandeln(transport, ct, () =>
        {
            var crypto = new BcTlsCrypto(new SecureRandom());
            var server = new BepServer(crypto, identity);
            var protokoll = new TlsServerProtocol(transport);

            protokoll.Accept(server);
            return ((TlsProtocol)protokoll, server.PeerCertificate, server.Kontext);
        });

    /// <summary>
    /// Fuehrt den Handschlag aus und prueft, was dabei herauskam.
    /// </summary>
    /// <remarks>
    /// Der Handschlag der Bibliothek laeuft blockierend. Er bekommt deshalb
    /// einen eigenen Thread und eine Frist am Strom darunter -- ein Abbruch
    /// ueber das Kennzeichen allein erreicht ein blockierendes Lesen nicht.
    /// </remarks>
    private static async Task<Verbindung> Aushandeln(
        Stream transport, CancellationToken ct,
        Func<(TlsProtocol Protokoll, byte[]? PeerCertificate, TlsContext Kontext)> handschlag)
    {
        var vorherLesen = Lesefrist(transport);
        var vorherSchreiben = Schreibfrist(transport);

        Setzen(transport, (int)Frist.TotalMilliseconds);

        try
        {
            var (protokoll, peerCertificate, kontext) = await Task
                .Run(handschlag, ct)
                .WaitAsync(Frist, ct)
                .ConfigureAwait(false);

            var kennwerte = kontext.SecurityParameters;
            var alpn = kennwerte.ApplicationProtocol?.GetUtf8Decoding();

            var beschreibung =
                $"[{kennwerte.NegotiatedVersion}, Verfahren 0x{kennwerte.CipherSuite:X4}, ALPN \"{alpn}\"]";

            if (alpn != BepProtocolName)
                throw new InvalidDataException(
                    $"Die Gegenstelle hat \"{alpn}\" statt \"{BepProtocolName}\" ausgehandelt.");

            // Ohne Zertifikat gibt es keine Geraete-ID. Bei einer eigenen
            // TLS-Umsetzung ist das kein zu erwartender Fall mehr, aber ein
            // stiller Fehlschlag waere schlimmer als eine Meldung.
            if (peerCertificate is null or { Length: 0 })
                throw new MissingPeerCertificateException(
                    "Die Gegenstelle hat kein Zertifikat geliefert. " + beschreibung);

            Setzen(transport, Timeout.Infinite);
            return new Verbindung(protokoll.Stream, peerCertificate, beschreibung);
        }
        catch
        {
            Setzen(transport, vorherLesen, vorherSchreiben);
            throw;
        }
    }

    // Nicht jeder Strom kennt Fristen.
    private static int Lesefrist(Stream s) => s.CanTimeout ? s.ReadTimeout : Timeout.Infinite;

    private static int Schreibfrist(Stream s) => s.CanTimeout ? s.WriteTimeout : Timeout.Infinite;

    /// <summary>
    /// Setzt die Fristen des Stroms darunter.
    /// </summary>
    /// <remarks>
    /// Null ist kein zulaessiger Wert und bedeutet an dieser Stelle "keine
    /// Frist". Ohne diese Umsetzung wuerde das Zuruecksetzen im Fehlerfall
    /// eine eigene Ausnahme werfen und die eigentliche verdecken.
    /// </remarks>
    private static void Setzen(Stream s, int lesen, int? schreiben = null)
    {
        if (!s.CanTimeout) return;

        s.ReadTimeout = lesen > 0 ? lesen : Timeout.Infinite;

        var wert = schreiben ?? lesen;
        s.WriteTimeout = wert > 0 ? wert : Timeout.Infinite;
    }

    /// <summary>Unsere Seite: Zertifikat und Schluessel in der Form der Bibliothek.</summary>
    private static TlsCredentials EigeneKennung(
        TlsContext context, BcTlsCrypto crypto, DeviceIdentity identity, byte[] anfrageKennung)
    {
        var (schluessel, verfahren) = Schluessel(identity);

        var zertifikat = new Certificate(
            anfrageKennung,
            [new CertificateEntry(new BcTlsCertificate(crypto, identity.Certificate.RawData), null)]);

        return new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(context), crypto, schluessel, zertifikat, verfahren);
    }

    /// <summary>
    /// Der private Schluessel und das dazu passende Signaturverfahren.
    /// </summary>
    /// <remarks>
    /// Das Verfahren haengt an der Kurve: TLS 1.3 laesst zu einer Kurve genau
    /// eine Hashlaenge zu. Wer hier etwas anderes einsetzt, bekommt sein
    /// Zertifikat nicht unter, ohne dass die Gegenstelle sagen koennte, warum.
    /// </remarks>
    private static (AsymmetricKeyParameter Schluessel, SignatureAndHashAlgorithm Verfahren) Schluessel(
        DeviceIdentity identity)
    {
        if (identity.Certificate.GetECDsaPrivateKey() is { } ec)
        {
            var hash = ec.KeySize switch
            {
                <= 256 => TlsHash.sha256,
                <= 384 => TlsHash.sha384,
                _ => TlsHash.sha512
            };

            return (PrivateKeyFactory.CreateKey(ec.ExportPkcs8PrivateKey()),
                    new SignatureAndHashAlgorithm(hash, SignatureAlgorithm.ecdsa));
        }

        if (identity.Certificate.GetRSAPrivateKey() is { } rsa)
        {
            return (PrivateKeyFactory.CreateKey(rsa.ExportPkcs8PrivateKey()),
                    new SignatureAndHashAlgorithm(
                        TlsHash.Intrinsic, SignatureAlgorithm.rsa_pss_rsae_sha256));
        }

        throw new CryptographicException(
            "Zum eigenen Zertifikat liegt kein verwendbarer privater Schluessel vor.");
    }

    private static IList<ProtocolName> Anwendungsprotokolle()
        => [ProtocolName.AsUtf8Encoding(BepProtocolName)];

    /// <summary>Wir bauen die Verbindung auf.</summary>
    private sealed class BepClient(BcTlsCrypto crypto, DeviceIdentity identity)
        : DefaultTlsClient(crypto)
    {
        public byte[]? PeerCertificate { get; private set; }

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

        protected override IList<ProtocolName> GetProtocolNames() => Anwendungsprotokolle();

        public override TlsAuthentication GetAuthentication() => new Pruefung(this, identity);

        /// <summary>
        /// Jedes Zertifikat wird angenommen, und das ist Absicht.
        /// </summary>
        /// <remarks>
        /// Syncthing kennt keine Zertifizierungsstelle. Die Identitaet eines
        /// Geraets ist der Hash seines Zertifikats; geprueft wird sie eine
        /// Ebene hoeher gegen die erwartete Kennung. Eine Pruefung gegen die
        /// Wurzelspeicher des Systems wuerde jede Gegenstelle abweisen.
        /// </remarks>
        private sealed class Pruefung(BepClient client, DeviceIdentity identity) : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
                => client.PeerCertificate = serverCertificate.Certificate.GetCertificateAt(0).GetEncoded();

            public TlsCredentials GetClientCredentials(TlsCertificateRequest certificateRequest)
                => EigeneKennung(
                    client.Kontext, (BcTlsCrypto)client.Crypto, identity,
                    certificateRequest.GetCertificateRequestContext());
        }

        /// <summary>Der Zusammenhang ist geschuetzt, die innere Klasse braucht ihn.</summary>
        public TlsContext Kontext => m_context;
    }

    /// <summary>Die Gegenstelle baut die Verbindung auf.</summary>
    private sealed class BepServer(BcTlsCrypto crypto, DeviceIdentity identity)
        : DefaultTlsServer(crypto)
    {
        public byte[]? PeerCertificate { get; private set; }

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

        protected override IList<ProtocolName> GetProtocolNames() => Anwendungsprotokolle();

        public override TlsCredentials GetCredentials()
            => EigeneKennung(m_context, (BcTlsCrypto)Crypto, identity, TlsUtilities.EmptyBytes);

        /// <summary>
        /// Fordert das Zertifikat der Gegenstelle an.
        /// </summary>
        /// <remarks>
        /// Die genannten Signaturverfahren entscheiden, welches Zertifikat die
        /// Gegenstelle ueberhaupt anbieten kann. Genau hier fehlte unter
        /// Windows Ed25519, worauf sie ein leeres Zertifikat schickte.
        /// </remarks>
        public override TlsCertificateRequest GetCertificateRequest()
            => new(TlsUtilities.EmptyBytes,
                   TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context), null, null);

        /// <summary>Der Zusammenhang ist geschuetzt, der Aufrufer braucht ihn.</summary>
        public TlsContext Kontext => m_context;

        public override void NotifyClientCertificate(Certificate clientCertificate)
            => PeerCertificate = clientCertificate.IsEmpty
                ? null
                : clientCertificate.GetCertificateAt(0).GetEncoded();
    }
}
