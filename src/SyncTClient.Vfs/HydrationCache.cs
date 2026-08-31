using System.Collections.Concurrent;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace SyncTClient.Vfs;

/// <summary>
/// Der selbstverwaltete lokale Cache: hydrierte Dateien bis zu einem Budget,
/// darueber hinaus fliegt raus, was am laengsten nicht angefasst wurde.
/// </summary>
/// <remarks>
/// Zwei Dinge, die gern verwechselt werden, sind hier bewusst getrennt:
///
/// <b>Invalidierung</b> -- die Datei hat sich geaendert, die vorgehaltenen
/// Bytes sind falsch. Das ist Korrektheit und passiert unabhaengig von jeder
/// Groesse.
///
/// <b>Verdraengung</b> -- der Cache ist voll, irgendetwas muss weichen. Ohne
/// diesen zweiten Mechanismus waechst der Cache unbegrenzt: Fotos aendern sich
/// nie, ein einmal geoeffnetes Bild von 2019 haette also nie einen Grund zu
/// gehen, und "bei Bedarf herunterladen" liefe nach ein paar Monaten auf eine
/// Vollkopie hinaus.
///
/// Angeheftete Dateien -- im Explorer "Immer auf diesem Geraet behalten" --
/// sind von der Verdraengung ausgenommen.
/// </remarks>
public sealed class HydrationCache
{
    /// <summary>FILE_ATTRIBUTE_PINNED. Fehlt in .NETs FileAttributes.</summary>
    private const uint FileAttributePinned = 0x0008_0000;

    /// <summary>
    /// FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS -- gesetzt, solange der Inhalt
    /// noch nicht lokal liegt. Fehlt in .NETs FileAttributes ebenfalls.
    /// </summary>
    private const uint FileAttributeRecallOnDataAccess = 0x0040_0000;

    private readonly string _rootPath;
    private readonly string _statePath;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _evictionLock = new(1, 1);

    public HydrationCache(string rootPath, long maxBytes, string statePath, Action<string>? log = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _statePath = statePath;
        _log = log;
        MaxBytes = maxBytes;
        Load();
    }

    /// <summary>Null oder kleiner bedeutet: kein Limit, nichts wird verdraengt.</summary>
    public long MaxBytes { get; }

    /// <summary>
    /// Zweite Meinung, bevor eine Datei verdraengt wird.
    /// </summary>
    /// <remarks>
    /// Der Cache kennt nur Groessen und Zugriffszeiten. Ob es die letzte
    /// Kopie einer Datei im Netz ist, weiss allein die Freigabe -- sie sieht
    /// die Indizes der Gegenstellen. Verdraengen heisst Bytes wegwerfen, und
    /// das darf nur, wer sie wiederbeschaffen kann.
    ///
    /// Ohne gesetzte Meinung wird verdraengt wie bisher.
    /// </remarks>
    public Func<string, bool>? MayEvict { get; set; }

    public long UsedBytes => _entries.Values.Sum(e => e.Bytes);

    public int FileCount => _entries.Count;

    private sealed record Entry(long Bytes, DateTimeOffset LastAccess);

    // ------------------------------------------------------------ Buchfuehrung

    /// <summary>Meldet, dass Bytes einer Datei lokal angekommen sind.</summary>
    public void NoteHydrated(string relativePath, long bytes)
    {
        _entries.AddOrUpdate(
            relativePath,
            _ => new Entry(bytes, DateTimeOffset.UtcNow),
            // Teilhydrationen summieren sich, bis die Datei vollstaendig ist.
            (_, existing) => existing with
            {
                Bytes = Math.Min(existing.Bytes + bytes, LogicalSize(relativePath)),
                LastAccess = DateTimeOffset.UtcNow
            });
    }

    public void NoteAccess(string relativePath)
    {
        if (_entries.TryGetValue(relativePath, out var entry))
            _entries[relativePath] = entry with { LastAccess = DateTimeOffset.UtcNow };
    }

    // ------------------------------------------------------------ Invalidierung

    /// <summary>
    /// Verwirft die lokalen Bytes der genannten Dateien, weil sie sich geaendert
    /// haben. Nicht zwischengespeicherte Dateien werden uebergangen.
    /// </summary>
    public int Invalidate(IEnumerable<string> relativePaths)
    {
        var dropped = 0;
        foreach (var path in relativePaths)
        {
            if (!_entries.ContainsKey(path)) continue;
            if (!Dehydrate(path, "geaendert")) continue;
            _entries.TryRemove(path, out _);
            dropped++;
        }

        if (dropped > 0) _log?.Invoke($"Cache: {dropped} geaenderte Dateien verworfen.");
        return dropped;
    }

    // ------------------------------------------------------------ Verdraengung

    /// <summary>
    /// Dehydriert, bis das Budget wieder eingehalten wird -- aelteste Zugriffe
    /// zuerst, angeheftete Dateien bleiben.
    /// </summary>
    public async Task EnforceBudgetAsync()
    {
        if (MaxBytes <= 0) return;
        if (UsedBytes <= MaxBytes) return;

        await _evictionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var used = UsedBytes;
            if (used <= MaxBytes) return;

            var candidates = _entries
                .Where(e => !IsPinned(e.Key))
                .Where(e => MayEvict?.Invoke(e.Key) ?? true)
                .OrderBy(e => e.Value.LastAccess)
                .ToList();

            var evicted = 0;
            long freed = 0;

            foreach (var (path, entry) in candidates)
            {
                if (used - freed <= MaxBytes) break;
                if (!Dehydrate(path, "verdraengt")) continue;
                _entries.TryRemove(path, out _);
                freed += entry.Bytes;
                evicted++;
            }

            if (evicted > 0)
            {
                _log?.Invoke($"Cache: {evicted} Dateien verdraengt, {freed / (1024.0 * 1024.0):0.0} MB frei " +
                             $"({(used - freed) / (1024.0 * 1024.0):0.0} von {MaxBytes / (1024.0 * 1024.0):0.0} MB belegt).");
            }
            else if (used > MaxBytes)
            {
                _log?.Invoke($"Cache: ueber Budget ({used / (1024.0 * 1024.0):0.0} MB), " +
                             "aber nichts verdraengbar -- alles angeheftet oder in Benutzung.");
            }

            Save();
        }
        finally
        {
            _evictionLock.Release();
        }
    }

    /// <summary>
    /// Gleicht die Buchfuehrung mit der Platte ab. Noetig beim Start, weil
    /// zwischen zwei Laeufen jemand Dateien angefasst oder freigegeben haben kann.
    /// </summary>
    public void ReconcileWithDisk()
    {
        if (!Directory.Exists(_rootPath)) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var full in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_rootPath, full).Replace(Path.DirectorySeparatorChar, '/');
            seen.Add(relative);

            var info = new FileInfo(full);
            // Ein dehydrierter Platzhalter traegt RecallOnDataAccess; ohne das
            // Attribut liegen die Bytes tatsaechlich lokal.
            var hydrated = ((uint)info.Attributes & FileAttributeRecallOnDataAccess) == 0;

            if (hydrated)
            {
                _entries.AddOrUpdate(
                    relative,
                    _ => new Entry(info.Length, info.LastAccessTimeUtc),
                    (_, existing) => existing with { Bytes = info.Length });
            }
            else
            {
                _entries.TryRemove(relative, out _);
            }
        }

        // Was nicht mehr auf der Platte liegt, gehoert auch nicht in die Bilanz.
        foreach (var stale in _entries.Keys.Where(k => !seen.Contains(k)).ToList())
            _entries.TryRemove(stale, out _);

        _log?.Invoke($"Cache: {FileCount} Dateien lokal, {UsedBytes / (1024.0 * 1024.0):0.0} MB" +
                     (MaxBytes > 0 ? $" von {MaxBytes / (1024.0 * 1024.0):0.0} MB Budget." : " (kein Budget gesetzt)."));
    }

    // ------------------------------------------------------------ Windows

    private bool Dehydrate(string relativePath, string reason)
    {
        var full = FullPath(relativePath);
        if (!File.Exists(full)) return false;

        try
        {
            // Schreibzugriff ist noetig; ReadWrite-Freigabe, damit ein Leser
            // die Datei nicht blockiert.
            using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            // Eine lokal geaenderte Datei ist nicht mehr abgeglichen, und
            // Dehydrieren wuerde die Aenderung verwerfen. Windows lehnt das
            // von sich aus ab -- aber die Absage kaeme im selben Zweig an wie
            // "gerade in Benutzung, spaeter noch einmal", und dann wuesste
            // niemand, dass hier eine Aenderung liegt, die nirgends hingeht.
            if (IsInSync(handle) == false)
            {
                _log?.Invoke($"  \"{relativePath}\" ist lokal geaendert und bleibt liegen " +
                             $"({reason}). Der Schreibweg fehlt noch -- die Aenderung erreicht " +
                             "die Gegenstelle nicht.");
                return false;
            }

            unsafe
            {
                // Laenge -1 heisst: die ganze Datei.
                var result = PInvoke.CfDehydratePlaceholder(
                    handle, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, null);

                if (result.Failed)
                {
                    _log?.Invoke($"  Dehydrieren von \"{relativePath}\" ({reason}) " +
                                 $"schlug fehl: 0x{(uint)result.Value:X8}");
                    return false;
                }
            }
            return true;
        }
        catch (IOException)
        {
            // In Benutzung -- beim naechsten Durchgang erneut versuchen.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ist die Datei mit der Gegenstelle abgeglichen?
    /// </summary>
    /// <returns>
    /// <c>false</c> nur, wenn Windows das ausdruecklich verneint;
    /// <c>true</c> auch dann, wenn sich die Frage nicht beantworten liess --
    /// die eigentliche Sicherung ist die Weigerung der Cloud-Filter-Schicht,
    /// diese Pruefung macht sie nur sichtbar.
    /// </returns>
    private unsafe bool IsInSync(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var info = new FILE_ATTRIBUTE_TAG_INFO();

        if (!PInvoke.GetFileInformationByHandleEx(
                handle, FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo,
                &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            return true;
        }

        var state = PInvoke.CfGetPlaceholderStateFromFileInfo(
            &info, FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo);

        // Was kein Platzhalter ist, hat auch keinen Abgleichzustand -- eine
        // gewoehnliche Datei im Ordner geht uns nichts an.
        if ((state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER) == 0) return true;

        return (state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC) != 0;
    }

    private bool IsPinned(string relativePath)
    {
        try
        {
            return ((uint)new FileInfo(FullPath(relativePath)).Attributes & FileAttributePinned) != 0;
        }
        catch
        {
            return false;
        }
    }

    private long LogicalSize(string relativePath)
    {
        try { return new FileInfo(FullPath(relativePath)).Length; }
        catch { return long.MaxValue; }
    }

    private string FullPath(string relativePath)
        => Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    // ------------------------------------------------------------ Zustand

    private void Load()
    {
        if (!File.Exists(_statePath)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_statePath));
            if (stored is null) return;
            foreach (var (path, entry) in stored) _entries[path] = entry;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Cache-Zustand nicht lesbar, beginne neu: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_statePath))!);
            File.WriteAllText(_statePath,
                JsonSerializer.Serialize(_entries.ToDictionary(e => e.Key, e => e.Value)));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Cache-Zustand nicht speicherbar: {ex.Message}");
        }
    }
}
