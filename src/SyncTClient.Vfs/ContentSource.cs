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
/// Die Schnittstelle ist bewusst schmal. Die CfAPI-Schicht muss dadurch nichts
/// von Syncthing wissen und laesst sich gegen eine Attrappe testen.
/// </remarks>
public interface IContentSource
{
    /// <summary>Alle bekannten Eintraege, Verzeichnisse eingeschlossen.</summary>
    IReadOnlyList<VirtualEntry> Enumerate();

    /// <summary>
    /// Liefert einen Bereich einer Datei. Windows fragt genau so an: mit
    /// Offset und Laenge, nicht nach der ganzen Datei.
    /// </summary>
    Task<byte[]> ReadAsync(string relativePath, long offset, long length, CancellationToken ct);

    /// <summary>
    /// Kuendigt an, dass ein zusammenhaengender Bereich geholt wird. Er kommt
    /// in mehreren Stuecken, zaehlt aber als eine Uebertragung.
    /// </summary>
    /// <remarks>
    /// Ohne diese Klammer entstuende je Stueck ein eigener Eintrag in der
    /// Uebertragungsliste. Sie zeigte dann fuenfundzwanzigmal "8 MB" statt
    /// einmal die Datei. Implementierungen ohne Anzeige brauchen das nicht,
    /// deshalb gibt es eine Vorgabe.
    /// </remarks>
    IDisposable BeginRange(string relativePath, long totalLength) => NoScope.Instance;
}

/// <summary>Ein Bereich, der nicht verfolgt wird.</summary>
public sealed class NoScope : IDisposable
{
    public static readonly NoScope Instance = new();

    private NoScope() { }

    public void Dispose() { }
}
