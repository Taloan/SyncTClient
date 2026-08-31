using System.Buffers;
using System.Security.Cryptography;
using Google.Protobuf;
using BlockInfo = SyncTClient.Bep.Proto.BlockInfo;

namespace SyncTClient.Bep;

/// <summary>
/// Zerlegt einen Dateiinhalt so in Bloecke, wie Syncthing es tut.
/// </summary>
/// <remarks>
/// Bisher konnte der Client Blocklisten nur lesen. Sie kamen mit dem Index von
/// der Gegenstelle. Zum Schreiben muss der Client sie selbst erzeugen, und
/// zwar Byte fuer Byte identisch. Blockgroesse, Grenzen und Hashes werden
/// allen anderen Knoten angekuendigt. Eine abweichende Zerlegung gilt dort als
/// andere Datei.
///
/// Diese Klasse enthaelt nur die Rechnung, kein Protokoll, kein Dateisystem
/// und keine Verbindung. Dadurch laesst sie sich gegen echte Daten pruefen
/// (<c>synctmount --blockcheck</c>).
/// </remarks>
public static class BlockList
{
    /// <summary>Kleinste Blockgroesse: 128 KiB.</summary>
    public const int MinimumBlockSize = 128 << 10;

    /// <summary>Groesste Blockgroesse: 16 MiB.</summary>
    public const int MaximumBlockSize = 16 << 20;

    /// <summary>Ab so vielen Bloecken wird die naechste Groesse genommen.</summary>
    private const int DesiredBlocks = 2000;

    /// <summary>
    /// Die Blockgroesse, die Syncthing fuer eine Datei dieser Groesse waehlt.
    /// </summary>
    /// <remarks>
    /// Nachgebildet nach <c>lib/protocol/blocksize.go</c>: die zulaessigen
    /// Groessen sind die Zweierpotenzen von 128 KiB bis 16 MiB. Gewaehlt wird
    /// die erste, bei der <c>fileSize &lt; 2000 * blockSize</c> gilt. Der
    /// Vergleich ist echt kleiner, nicht kleiner-gleich. Passt keine Groesse,
    /// bleibt es bei 16 MiB. Grosse Dateien haben dann mehr als 2000 Bloecke.
    ///
    /// Die Grenzen dieser Regel liegen genau auf den Vielfachen: 262.144.000
    /// Bytes (2000 x 128 KiB) ist die erste Groesse, die 256-KiB-Bloecke
    /// bekommt. Geprueft wurde die Regel nicht nur am Quelltext, sondern auch
    /// gegen die Ankuendigungen einer echten Gegenstelle.
    /// <c>synctmount --blockcheck</c> vergleicht je Datei die angekuendigte
    /// Blockgroesse mit dieser Rechnung.
    ///
    /// Zu beachten beim Vergleich: im Protokoll ist <c>block_size</c> ein
    /// optionales Feld, und 0 bedeutet nicht "keine Bloecke", sondern die
    /// Vorgabe von 128 KiB.
    /// </remarks>
    public static int BlockSizeFor(long fileSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);

        for (var size = MinimumBlockSize; size < MaximumBlockSize; size <<= 1)
            if (fileSize < (long)DesiredBlocks * size)
                return size;

        return MaximumBlockSize;
    }

    /// <summary>
    /// Rechnet die Blockliste eines Inhalts: Blockgroesse, die Bloecke selbst
    /// und den Hash ueber die Blockliste.
    /// </summary>
    /// <remarks>
    /// Der Strom wird vom aktuellen Stand an genau <paramref name="size"/>
    /// Bytes gelesen, blockweise durch einen geliehenen Puffer. Die Datei wird
    /// nie vollstaendig im Speicher gehalten. Bei 16-MiB-Bloecken belegt der
    /// Aufruf 16 MiB, unabhaengig von der Dateigroesse.
    ///
    /// <c>BlocksHash</c> ist SHA-256 ueber die Verkettung der rohen
    /// Blockhashes. Verkettet werden nur die 32 Bytes je Block, ohne Offset,
    /// ohne Groesse und ohne Trenner. Das entspricht
    /// <c>protocol.BlocksHash</c> in Syncthing und ist mit
    /// <c>synctmount --blockcheck</c> gegen die Ankuendigungen einer
    /// Gegenstelle geprueft worden.
    ///
    /// Eine leere Datei hat eine leere Blockliste. Der BlocksHash ist dann
    /// SHA-256 ueber eine leere Eingabe (<c>e3b0c442...</c>) und nicht leer.
    /// Das ergibt sich aus derselben Formel und ist kein Sonderfall.
    /// </remarks>
    /// <exception cref="EndOfStreamException">
    /// Der Strom endete vor <paramref name="size"/> Bytes.
    /// </exception>
    public static (int BlockSize, IReadOnlyList<BlockInfo> Blocks, byte[] BlocksHash) For(Stream content, long size)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(size);

        var blockSize = BlockSizeFor(size);
        var blocks = new List<BlockInfo>((int)((size + blockSize - 1) / blockSize));

        using var overAll = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);

        try
        {
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];

            for (long offset = 0; offset < size;)
            {
                // Der letzte Block ist in der Regel kuerzer. Er wird nicht
                // aufgefuellt.
                var want = (int)Math.Min(blockSize, size - offset);

                var got = content.ReadAtLeast(buffer.AsSpan(0, want), want, throwOnEndOfStream: false);
                if (got != want)
                    throw new EndOfStreamException(
                        $"Nach {offset + got} von {size} angekuendigten Bytes war der Strom zu Ende.");

                SHA256.HashData(buffer.AsSpan(0, want), hash);
                overAll.AppendData(hash);

                blocks.Add(new BlockInfo
                {
                    Offset = offset,
                    Size = want,
                    Hash = ByteString.CopyFrom(hash)
                });

                offset += want;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (blockSize, blocks, overAll.GetHashAndReset());
    }
}
