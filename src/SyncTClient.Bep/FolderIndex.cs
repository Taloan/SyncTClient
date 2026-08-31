using System.Collections.Concurrent;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;

namespace SyncTClient.Bep;

/// <summary>
/// Der Index eines Ordners, so wie der Peer ihn geschickt hat: alle
/// Dateinamen, Groessen und die vollstaendigen Blocklisten.
/// </summary>
/// <remarks>
/// Das ist die Grundlage fuer Platzhalter: der Katalog liegt vor, die
/// Inhalte nicht. Ein Eintrag ohne Blockliste bedeutet, dass der Peer die
/// Datei selbst nicht haelt.
///
/// Diese Implementierung haelt alles im Speicher. Fuer eine echte
/// Fotobibliothek muss das auf die Platte: 100.000 Dateien zu je 5 MB
/// ergeben bei 128-KiB-Bloecken rund 4 Millionen Blockhashes, also etwa
/// 128 MB allein an Hashes.
/// </remarks>
public sealed class FolderIndex(string folderId)
{
    private readonly ConcurrentDictionary<string, BepFileInfo> _files = new(StringComparer.Ordinal);

    public string FolderId { get; } = folderId;

    /// <summary>
    /// Zahl der empfangenen Index-Nachrichten. Sie unterscheidet "der Ordner
    /// ist leer" von "es kam ueberhaupt nichts an". Syncthing verschickt nur
    /// nicht-leere Stapel, ein leerer Ordner erzeugt also gar keine Nachricht.
    /// </summary>
    public int MessageCount => _messageCount;

    private int _messageCount;

    public int Count => _files.Count;

    public void Absorb(IEnumerable<BepFileInfo> files)
    {
        Interlocked.Increment(ref _messageCount);
        foreach (var file in files)
            _files[file.Name] = file;
    }

    public bool TryGet(string name, out BepFileInfo file)
        => _files.TryGetValue(name, out file!);

    public IReadOnlyList<BepFileInfo> Snapshot() => _files.Values.ToList();
}
