using SyncTClient.Bep;
using SyncTClient.Bep.Proto;
using SyncTClient.Vfs;

namespace SyncTClient.Mount;

/// <summary>
/// Verbindet die beiden Haelften: der Syncthing-Index liefert den Katalog fuer
/// die Platzhalter, und ein Hydrations-Rueckruf wird zu Block-Requests.
/// </summary>
public sealed class BepContentSource(
    BepConnection connection,
    FolderIndex index,
    string folderId,
    int parallelism = 8,
    Action<string>? log = null) : IContentSource
{
    public IReadOnlyList<VirtualEntry> Enumerate()
        => index.Snapshot()
            .Where(f => !f.Deleted && !string.IsNullOrEmpty(f.Name))
            .Select(f => new VirtualEntry(
                RelativePath: f.Name,
                Size: f.Size,
                LastWrite: DateTimeOffset.FromUnixTimeSeconds(f.ModifiedS),
                IsDirectory: f.Type == FileInfoType.Directory))
            .ToList();

    public async Task<byte[]> ReadAsync(string relativePath, long offset, long length, CancellationToken ct)
    {
        if (!index.TryGet(relativePath, out var file))
            throw new FileNotFoundException($"\"{relativePath}\" ist nicht im Index.");

        var started = DateTimeOffset.UtcNow;
        var data = await FileFetcher.FetchRangeAsync(
            connection, folderId, file, offset, length, parallelism, ct: ct).ConfigureAwait(false);

        var elapsed = DateTimeOffset.UtcNow - started;
        log?.Invoke($"  {data.Length} Bytes in {elapsed.TotalMilliseconds:0} ms " +
                    $"({data.Length / Math.Max(elapsed.TotalSeconds, 0.001) / (1024 * 1024):0.0} MB/s)");
        return data;
    }
}
