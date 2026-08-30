using System.Security.Cryptography;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;

namespace SyncTClient.Bep;

/// <summary>
/// Holt Dateiinhalte blockweise vom Peer -- die Hydration eines Platzhalters.
/// </summary>
public static class FileFetcher
{
    /// <summary>
    /// Laedt einen zusammenhaengenden Bereich einer Datei.
    /// </summary>
    /// <remarks>
    /// Genau diese Signatur braucht spaeter der CfAPI-Callback: Windows meldet
    /// beim Zugriff auf einen Platzhalter einen Offset und eine Laenge, nicht
    /// die ganze Datei. Blockweiser Zufallszugriff ist also nicht nur moeglich,
    /// sondern der Normalfall.
    /// </remarks>
    public static async Task<byte[]> FetchRangeAsync(
        BepConnection connection, string folderId, BepFileInfo file,
        long offset, long length, int parallelism = 8,
        IProgress<int>? blocksDone = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > file.Size)
            throw new ArgumentOutOfRangeException(nameof(length),
                $"Bereich {offset}+{length} liegt hinter dem Dateiende ({file.Size}).");
        if (file.Blocks.Count == 0)
            throw new InvalidOperationException(
                $"\"{file.Name}\" hat keine Blockliste -- der Peer haelt die Datei selbst nicht.");

        var end = offset + length;
        var needed = file.Blocks
            .Select((block, position) => (block, position))
            .Where(x => x.block.Offset < end && x.block.Offset + x.block.Size > offset)
            .ToList();

        var result = new byte[length];
        var done = 0;

        await Parallel.ForEachAsync(
            needed,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (item, token) =>
            {
                var (block, position) = item;

                var data = await connection.RequestAsync(
                    folderId, file.Name, block.Offset, block.Size, block.Hash, position, token)
                    .ConfigureAwait(false);

                if (data.Length != block.Size)
                    throw new InvalidDataException(
                        $"Block {position}: {data.Length} statt {block.Size} Bytes.");

                // Jeder Block traegt seinen Hash im Index -- also pruefen wir ihn
                // auch. Das faengt sowohl Uebertragungsfehler als auch einen
                // Peer, der etwas anderes liefert als angekuendigt.
                if (!SHA256.HashData(data).AsSpan().SequenceEqual(block.Hash.Span))
                    throw new InvalidDataException(
                        $"Block {position} von \"{file.Name}\": Hash stimmt nicht.");

                // Ueberlappung des Blocks mit dem angeforderten Bereich.
                var from = Math.Max(offset, block.Offset);
                var to = Math.Min(end, block.Offset + block.Size);
                data.AsSpan((int)(from - block.Offset), (int)(to - from))
                    .CopyTo(result.AsSpan((int)(from - offset)));

                blocksDone?.Report(Interlocked.Increment(ref done));
            }).ConfigureAwait(false);

        return result;
    }

    /// <summary>Laedt eine Datei vollstaendig.</summary>
    public static Task<byte[]> FetchAllAsync(
        BepConnection connection, string folderId, BepFileInfo file,
        int parallelism = 8, IProgress<int>? blocksDone = null, CancellationToken ct = default)
        => FetchRangeAsync(connection, folderId, file, 0, file.Size, parallelism, blocksDone, ct);
}
