namespace SyncTClient.Vfs;

/// <summary>Ein Eintrag, wie ihn der Explorer als Platzhalter zeigen soll.</summary>
/// <param name="RelativePath">Pfad unterhalb des Sync-Roots, mit / als Trenner.</param>
public sealed record VirtualEntry(
    string RelativePath,
    long Size,
    DateTimeOffset LastWrite,
    bool IsDirectory);

/// <summary>
/// Woher die Platzhalter ihre Inhalte bekommen.
/// </summary>
/// <remarks>
/// Bewusst schmal gehalten, damit die CfAPI-Schicht nichts von Syncthing
/// wissen muss -- und damit sie sich gegen eine Attrappe testen laesst.
/// </remarks>
public interface IContentSource
{
    /// <summary>Alle bekannten Eintraege, Verzeichnisse eingeschlossen.</summary>
    IReadOnlyList<VirtualEntry> Enumerate();

    /// <summary>
    /// Liefert einen Bereich einer Datei. Genau so fragt Windows: mit Offset
    /// und Laenge, nicht nach der ganzen Datei.
    /// </summary>
    Task<byte[]> ReadAsync(string relativePath, long offset, long length, CancellationToken ct);
}
