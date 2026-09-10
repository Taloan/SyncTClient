using System.Buffers.Binary;
using System.Text;

namespace SyncTClient.Bep;

/// <summary>
/// Das Protokoll, mit dem ein Relay zwei Geräte zusammenbringt.
/// </summary>
/// <remarks>
/// Ein Relay ist ein fremder Rechner, der eine Leitung durchreicht. Er kommt
/// zum Zug, wenn keine der beiden Seiten für die andere erreichbar ist —
/// beide hinter einem Router, keine Portweiterleitung, kein durchstoßbares
/// NAT. Beide bauen dann eine ausgehende Verbindung zum Relay auf, und der
/// Relay verbindet die beiden Leitungen miteinander.
///
/// Verschlüsselt bleibt dabei alles: der Relay sieht nur den TLS-Strom, den
/// die beiden Enden untereinander aufbauen. Er kennt weder Inhalte noch
/// Dateinamen — nur, dass zwei Gerätekennungen miteinander sprechen.
///
/// <para>Die Nachrichten</para>
///
/// Jede Nachricht trägt einen Kopf aus drei Zahlen zu je vier Byte, in der
/// Reihenfolge des Netzes: eine feste Kennzahl, die Art, die Länge des
/// Rumpfes. Der Rumpf ist XDR — Zahlen zu vier Byte, Bytefolgen mit
/// vorangestellter Länge und aufgefüllt auf ein Vielfaches von vier.
///
/// Die Kennzahl steht am Anfang jeder Nachricht und ist die einzige
/// Möglichkeit, einen falschen Gegenüber zu bemerken, bevor man ihm folgt.
/// </remarks>
public static class RelayProtocol
{
    /// <summary>Steht am Anfang jeder Nachricht.</summary>
    private const uint Kennzahl = 0x9E79BC40;

    /// <summary>Der übliche Port eines Relays.</summary>
    public const int Port = 22067;

    /// <summary>Die Arten von Nachrichten, die es gibt.</summary>
    public enum Art
    {
        Ping = 0,
        Pong = 1,
        BeimRelayAnmelden = 2,
        SitzungBeitreten = 3,
        Antwort = 4,
        VerbindungAnfordern = 5,
        Einladung = 6,
        RelayVoll = 7
    }

    /// <summary>Eine gelesene Nachricht: die Art und ihr Rumpf.</summary>
    public readonly record struct Nachricht(Art Art, byte[] Rumpf);

    /// <summary>
    /// Was der Relay antwortet, wenn er etwas angenommen oder abgelehnt hat.
    /// </summary>
    /// <param name="Code">Null heißt angenommen.</param>
    public readonly record struct Antwort(int Code, string Meldung);

    /// <summary>
    /// Die Einladung zu einer vermittelten Leitung.
    /// </summary>
    /// <param name="Von">Die Gerätekennung der Gegenstelle, roh.</param>
    /// <param name="Schluessel">Weist die Leitung dieser Sitzung zu.</param>
    /// <param name="Adresse">
    /// Wohin die Leitung aufzubauen ist. Leer heißt: derselbe Rechner, von
    /// dem die Einladung kam.
    /// </param>
    /// <param name="Port">Der Port dazu.</param>
    /// <param name="AlsServer">
    /// Wer von beiden den TLS-Handschlag annimmt. Der Relay entscheidet das,
    /// und beide Seiten müssen sich daran halten — sonst warten zwei Clients
    /// aufeinander.
    /// </param>
    public readonly record struct Einladung(
        byte[] Von, byte[] Schluessel, string Adresse, int Port, bool AlsServer);

    // ------------------------------------------------------------ Schreiben

    /// <summary>Schickt eine Nachricht ohne Rumpf.</summary>
    public static Task SendenAsync(Stream strom, Art art, CancellationToken ct)
        => SendenAsync(strom, art, [], ct);

    /// <summary>Schickt eine Nachricht mit Rumpf.</summary>
    public static async Task SendenAsync(
        Stream strom, Art art, byte[] rumpf, CancellationToken ct)
    {
        var puffer = new byte[12 + rumpf.Length];

        BinaryPrimitives.WriteUInt32BigEndian(puffer, Kennzahl);
        BinaryPrimitives.WriteInt32BigEndian(puffer.AsSpan(4), (int)art);
        BinaryPrimitives.WriteInt32BigEndian(puffer.AsSpan(8), rumpf.Length);
        rumpf.CopyTo(puffer, 12);

        await strom.WriteAsync(puffer, ct).ConfigureAwait(false);
        await strom.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Der Rumpf einer Anfrage nach einer Gegenstelle.</summary>
    public static byte[] VerbindungAnfordernRumpf(byte[] geraet) => BytesXdr(geraet);

    /// <summary>Der Rumpf eines Beitritts zu einer Sitzung.</summary>
    public static byte[] SitzungBeitretenRumpf(byte[] schluessel) => BytesXdr(schluessel);

    /// <summary>Länge, Daten, aufgefüllt auf vier.</summary>
    private static byte[] BytesXdr(byte[] daten)
    {
        var fuellung = (4 - daten.Length % 4) % 4;
        var puffer = new byte[4 + daten.Length + fuellung];

        BinaryPrimitives.WriteInt32BigEndian(puffer, daten.Length);
        daten.CopyTo(puffer, 4);
        return puffer;
    }

    // ------------------------------------------------------------ Lesen

    /// <summary>
    /// Liest die nächste Nachricht.
    /// </summary>
    /// <remarks>
    /// Eine Nachricht ohne die richtige Kennzahl am Anfang bricht das Lesen
    /// ab. Wer an diesem Port etwas anderes betreibt, soll nicht dazu führen,
    /// dass hier beliebige Längen als Puffergrößen gelesen werden.
    /// </remarks>
    public static async Task<Nachricht> LesenAsync(Stream strom, CancellationToken ct)
    {
        var kopf = new byte[12];
        await strom.ReadExactlyAsync(kopf, ct).ConfigureAwait(false);

        var kennzahl = BinaryPrimitives.ReadUInt32BigEndian(kopf);
        if (kennzahl != Kennzahl)
            throw new InvalidDataException(
                $"Am Relay-Port antwortet etwas anderes: Kennzahl 0x{kennzahl:X8} " +
                $"statt 0x{Kennzahl:X8}.");

        var art = (Art)BinaryPrimitives.ReadInt32BigEndian(kopf.AsSpan(4));
        var laenge = BinaryPrimitives.ReadInt32BigEndian(kopf.AsSpan(8));

        // Eine Nachricht dieses Protokolls ist klein. Eine unsinnige Laenge
        // ist kein Grund, Speicher dafuer bereitzustellen.
        if (laenge is < 0 or > 65536)
            throw new InvalidDataException($"Der Relay nennt eine Rumpflaenge von {laenge} Byte.");

        var rumpf = new byte[laenge];
        if (laenge > 0) await strom.ReadExactlyAsync(rumpf, ct).ConfigureAwait(false);

        return new Nachricht(art, rumpf);
    }

    /// <summary>Zerlegt den Rumpf einer Antwort.</summary>
    public static Antwort AntwortLesen(byte[] rumpf)
    {
        var leser = new Leser(rumpf);
        var code = leser.Int32();
        var meldung = Encoding.UTF8.GetString(leser.Bytes());
        return new Antwort(code, meldung);
    }

    /// <summary>Zerlegt den Rumpf einer Einladung.</summary>
    public static Einladung EinladungLesen(byte[] rumpf)
    {
        var leser = new Leser(rumpf);

        var von = leser.Bytes();
        var schluessel = leser.Bytes();
        var adresse = leser.Bytes();
        var port = leser.Int32();
        var alsServer = leser.Int32() != 0;

        return new Einladung(von, schluessel, AdresseAlsText(adresse), port, alsServer);
    }

    /// <summary>
    /// Die Adresse einer Einladung steht als rohe IP-Adresse, nicht als Text.
    /// </summary>
    /// <remarks>
    /// Vier Byte sind IPv4, sechzehn sind IPv6, nichts heißt "derselbe
    /// Rechner, von dem die Einladung kam". Der Aufrufer setzt dann den Host
    /// des Relays ein.
    /// </remarks>
    private static string AdresseAlsText(byte[] roh)
    {
        if (roh.Length == 0) return "";

        try
        {
            return new System.Net.IPAddress(roh).ToString();
        }
        catch (ArgumentException)
        {
            // Weder vier noch sechzehn Byte. Dann lieber der Host des Relays
            // als eine erfundene Adresse.
            return "";
        }
    }

    /// <summary>Liest XDR-Felder der Reihe nach aus einem Rumpf.</summary>
    private ref struct Leser(byte[] daten)
    {
        private readonly byte[] _daten = daten;
        private int _stelle;

        public int Int32()
        {
            if (_stelle + 4 > _daten.Length)
                throw new InvalidDataException("Der Rumpf der Relay-Nachricht ist zu kurz.");

            var wert = BinaryPrimitives.ReadInt32BigEndian(_daten.AsSpan(_stelle));
            _stelle += 4;
            return wert;
        }

        public byte[] Bytes()
        {
            var laenge = Int32();

            if (laenge < 0 || _stelle + laenge > _daten.Length)
                throw new InvalidDataException("Der Rumpf der Relay-Nachricht ist zu kurz.");

            var wert = _daten.AsSpan(_stelle, laenge).ToArray();

            // Aufgefuellt auf ein Vielfaches von vier.
            _stelle += laenge + (4 - laenge % 4) % 4;
            return wert;
        }
    }
}
