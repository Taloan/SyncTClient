using System.Net.Sockets;

namespace SyncTClient.Bep;

/// <summary>
/// Baut über ein Relay eine Leitung zu einer Gegenstelle auf.
/// </summary>
/// <remarks>
/// Der Ablauf hat zwei Verbindungen, und das ist keine Umständlichkeit,
/// sondern der Aufbau des Protokolls.
///
/// Die erste geht zum Relay selbst und ist mit TLS gesichert: darüber wird
/// nach der Gegenstelle gefragt. Antwortet der Relay mit einer Einladung,
/// nennt sie eine Adresse, einen Port und einen Schlüssel. Die zweite
/// Verbindung geht dorthin, nennt den Schlüssel — und ab da ist die Leitung
/// nichts weiter als ein Rohr zur Gegenstelle.
///
/// Über dieses Rohr läuft dann der gewohnte TLS-Handschlag zwischen den
/// beiden Geräten. Der Relay sieht davon nichts als verschlüsselte Bytes; er
/// kennt weder Dateinamen noch Inhalte. Was er erfährt, ist, dass zwei
/// Gerätekennungen miteinander sprechen.
/// </remarks>
public static class RelayClient
{
    /// <summary>So lange darf ein Schritt dauern.</summary>
    private static readonly TimeSpan Frist = TimeSpan.FromSeconds(15);

    /// <summary>Eine Leitung, die ein Relay vermittelt hat.</summary>
    /// <param name="Strom">Das Rohr zur Gegenstelle. TLS kommt darüber.</param>
    /// <param name="AlsServer">
    /// Ob wir den TLS-Handschlag annehmen statt ihn aufzubauen. Der Relay
    /// verteilt die Rollen; hielten sich beide Seiten nicht daran, warteten
    /// zwei Clients aufeinander.
    /// </param>
    public sealed record Leitung(Stream Strom, bool AlsServer) : IDisposable
    {
        public void Dispose() => Strom.Dispose();
    }

    /// <summary>Was in einer <c>relay://</c>-Adresse steht.</summary>
    public readonly record struct Adresse(string Host, int Port, DeviceId Relay);

    /// <summary>
    /// Zerlegt <c>relay://host:port/?id=KENNUNG</c>.
    /// </summary>
    /// <remarks>
    /// Die Kennung darf fehlen. Dann wird sie nicht geprüft — ein Relay ohne
    /// genannte Kennung ist immer noch besser als keine Verbindung, und die
    /// Gegenstelle dahinter wird ohnehin an ihrem eigenen Zertifikat
    /// gemessen.
    /// </remarks>
    public static Adresse Zerlegen(string adresse)
    {
        var uri = new Uri(adresse);
        var id = DeviceId.Empty;

        var frage = uri.Query;
        if (frage.StartsWith('?')) frage = frage[1..];

        foreach (var teil in frage.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!teil.StartsWith("id=", StringComparison.OrdinalIgnoreCase)) continue;
            if (DeviceId.TryParse(Uri.UnescapeDataString(teil[3..]), out var gelesen, out _)) id = gelesen;
        }

        return new Adresse(uri.Host, uri.Port > 0 ? uri.Port : RelayProtocol.Port, id);
    }

    /// <summary>
    /// Fragt einen Relay nach einer Gegenstelle und baut die vermittelte
    /// Leitung auf.
    /// </summary>
    public static async Task<Leitung> VerbindenAsync(
        string relayAdresse, DeviceIdentity identity, DeviceId ziel,
        Action<string> log, CancellationToken ct)
    {
        var relay = Zerlegen(relayAdresse);

        var einladung = await AnfragenAsync(relay, identity, ziel, log, ct).ConfigureAwait(false);

        // Eine leere Adresse heisst: derselbe Rechner, von dem die Einladung
        // kam. Der Relay kennt seine eigene aeussere Adresse nicht immer.
        var host = string.IsNullOrEmpty(einladung.Adresse) ? relay.Host : einladung.Adresse;

        log($"Relay {relay.Host}: Einladung erhalten, Leitung ueber {host}:{einladung.Port}" +
            $" ({(einladung.AlsServer ? "wir nehmen an" : "wir bauen auf")}).");

        return await BeitretenAsync(host, einladung, ct).ConfigureAwait(false);
    }

    /// <summary>Die erste Verbindung: zum Relay, gesichert, und die Anfrage.</summary>
    private static async Task<RelayProtocol.Einladung> AnfragenAsync(
        Adresse relay, DeviceIdentity identity, DeviceId ziel,
        Action<string> log, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };

        await tcp.ConnectAsync(relay.Host, relay.Port, ct)
            .AsTask().WaitAsync(Frist, ct).ConfigureAwait(false);

        var tls = await BepTls
            .ConnectAsync(tcp.GetStream(), identity, ct, BepTls.RelayProtocolName)
            .ConfigureAwait(false);

        // Der Relay wird an seinem Zertifikat gemessen, so wie jede
        // Gegenstelle. Steht in der Adresse keine Kennung, faellt die
        // Pruefung aus -- dann hat sie niemand behauptet.
        if (relay.Relay != DeviceId.Empty)
        {
            var gemessen = DeviceId.FromCertificate(tls.PeerCertificate);
            if (gemessen != relay.Relay)
                throw new InvalidDataException(
                    $"Der Relay nennt sich {relay.Relay.Short()}, sein Zertifikat sagt {gemessen.Short()}.");
        }

        await RelayProtocol.SendenAsync(
            tls.Stream, RelayProtocol.Art.VerbindungAnfordern,
            RelayProtocol.VerbindungAnfordernRumpf(ziel.ToByteArray()), ct).ConfigureAwait(false);

        // Der Relay schickt dazwischen Ping. Wer darauf nicht antwortet,
        // wird nach kurzer Zeit getrennt.
        while (true)
        {
            var nachricht = await RelayProtocol.LesenAsync(tls.Stream, ct)
                .WaitAsync(Frist, ct).ConfigureAwait(false);

            switch (nachricht.Art)
            {
                case RelayProtocol.Art.Ping:
                    await RelayProtocol.SendenAsync(tls.Stream, RelayProtocol.Art.Pong, ct)
                        .ConfigureAwait(false);
                    continue;

                case RelayProtocol.Art.Einladung:
                    return RelayProtocol.EinladungLesen(nachricht.Rumpf);

                case RelayProtocol.Art.RelayVoll:
                    throw new IOException($"Der Relay {relay.Host} ist ausgelastet.");

                case RelayProtocol.Art.Antwort:
                    var antwort = RelayProtocol.AntwortLesen(nachricht.Rumpf);
                    throw new IOException(
                        $"Der Relay {relay.Host} vermittelt nicht: {antwort.Meldung} (Code {antwort.Code}).");

                default:
                    log($"Relay {relay.Host}: unerwartete Nachricht {nachricht.Art}, uebergangen.");
                    continue;
            }
        }
    }

    /// <summary>Die zweite Verbindung: zur vermittelten Leitung, mit dem Schlüssel.</summary>
    /// <remarks>
    /// Hier gibt es kein TLS zum Relay. Die Leitung ist ein Rohr, und was
    /// darin gesichert wird, machen die beiden Enden unter sich aus.
    /// </remarks>
    private static async Task<Leitung> BeitretenAsync(
        string host, RelayProtocol.Einladung einladung, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };

        try
        {
            await tcp.ConnectAsync(host, einladung.Port, ct)
                .AsTask().WaitAsync(Frist, ct).ConfigureAwait(false);

            var strom = tcp.GetStream();

            await RelayProtocol.SendenAsync(
                strom, RelayProtocol.Art.SitzungBeitreten,
                RelayProtocol.SitzungBeitretenRumpf(einladung.Schluessel), ct).ConfigureAwait(false);

            var nachricht = await RelayProtocol.LesenAsync(strom, ct)
                .WaitAsync(Frist, ct).ConfigureAwait(false);

            if (nachricht.Art != RelayProtocol.Art.Antwort)
                throw new InvalidDataException(
                    $"Auf den Beitritt zur Sitzung kam {nachricht.Art} statt einer Antwort.");

            var antwort = RelayProtocol.AntwortLesen(nachricht.Rumpf);
            if (antwort.Code != 0) throw new IOException(Abgelehnt(antwort));

            return new Leitung(new TcpLeitung(tcp), einladung.AlsServer);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Was eine Absage des Relays bedeutet, auf Deutsch.
    /// </summary>
    /// <remarks>
    /// Der Relay antwortet englisch und knapp. Ein Fall lohnt die
    /// Übersetzung, weil er kein Fehler ist, sondern ein Zustand: zwischen
    /// zwei Geräten führt ein Relay genau eine Sitzung. Wer eine zweite
    /// aufbaut — weil die erste eben abgerissen ist und der Relay es noch
    /// nicht bemerkt hat —, bekommt diese Absage und muss warten, nicht
    /// suchen.
    ///
    /// Gemessen: derselbe Versuch dreimal hintereinander, während eine
    /// Sitzung bestand, ergab drei verschiedene Bilder — einmal einen
    /// abgelehnten TLS-Handschlag, einmal ein Ende des Stroms mitten im
    /// Handschlag, einmal diese Absage im Klartext. Nur die dritte sagt,
    /// was los ist; die beiden anderen sind dieselbe Ursache, nur früher
    /// bemerkt.
    /// </remarks>
    private static string Abgelehnt(RelayProtocol.Antwort antwort)
    {
        if (antwort.Meldung.Contains("already connected", StringComparison.OrdinalIgnoreCase))
            return "der Relay fuehrt bereits eine Sitzung zwischen diesen beiden Geraeten";

        return $"die Sitzung wurde abgelehnt: {antwort.Meldung} (Code {antwort.Code})";
    }

    /// <summary>
    /// Der Strom einer Leitung, der beim Schließen auch den Socket schließt.
    /// </summary>
    /// <remarks>
    /// <c>NetworkStream</c> aus <c>TcpClient.GetStream</c> lässt den Socket
    /// stehen. Ohne diese Hülle bliebe nach jeder beendeten Sitzung eine
    /// offene Verbindung zum Relay zurück.
    /// </remarks>
    private sealed class TcpLeitung(TcpClient tcp) : Stream
    {
        private readonly NetworkStream _strom = tcp.GetStream();

        public override bool CanRead => _strom.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _strom.CanWrite;
        public override bool CanTimeout => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int ReadTimeout
        {
            get => _strom.ReadTimeout;
            set => _strom.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => _strom.WriteTimeout;
            set => _strom.WriteTimeout = value;
        }

        public override void Flush() => _strom.Flush();

        public override Task FlushAsync(CancellationToken ct) => _strom.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count)
            => _strom.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _strom.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _strom.ReadAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count)
            => _strom.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _strom.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _strom.WriteAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _strom.Dispose();
                tcp.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
