using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace SyncTClient.Vfs;

/// <summary>
/// Der selbstverwaltete lokale Cache: hydrierte Dateien bis zu einem Budget.
/// Wird das Budget ueberschritten, wird verdraengt, worauf am laengsten nicht
/// zugegriffen wurde.
/// </summary>
/// <remarks>
/// Zwei Vorgaenge sind hier bewusst getrennt:
///
/// <b>Invalidierung</b>: die Datei hat sich geaendert, die vorgehaltenen
/// Bytes sind falsch. Das ist eine Frage der Korrektheit und geschieht
/// unabhaengig von jeder Groesse.
///
/// <b>Verdraengung</b>: der Cache ist voll, es muss etwas weichen. Ohne
/// diesen zweiten Mechanismus waechst der Cache unbegrenzt. Fotos aendern
/// sich nie, ein einmal geoeffnetes Bild von 2019 wuerde also nie entfernt,
/// und "bei Bedarf herunterladen" ergaebe nach einigen Monaten eine
/// Vollkopie.
///
/// Angeheftete Dateien, im Explorer "Immer auf diesem Geraet behalten", sind
/// von der Verdraengung ausgenommen.
/// </remarks>
public sealed class HydrationCache
{
    /// <summary>FILE_ATTRIBUTE_PINNED. Fehlt in .NETs FileAttributes.</summary>
    private const uint FileAttributePinned = 0x0008_0000;

    /// <summary>
    /// FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS. Das Attribut ist gesetzt, solange
    /// der Inhalt noch nicht lokal liegt. Fehlt in .NETs FileAttributes
    /// ebenfalls.
    /// </summary>
    private const uint FileAttributeRecallOnDataAccess = 0x0040_0000;

    private readonly string _rootPath;
    private readonly string _statePath;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CacheBudget? _budget;

    public HydrationCache(string rootPath, CacheBudget? budget, string statePath, Action<string>? log = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _statePath = statePath;
        _log = log;
        _budget = budget;
        Load();

        // Das Budget gilt fuer alle Freigaben zusammen. Ohne Anmeldung zaehlt
        // diese Freigabe nicht mit.
        budget?.Register(this);
    }

    /// <summary>Null oder kleiner bedeutet: kein Limit, nichts wird verdraengt.</summary>
    public long MaxBytes => _budget?.LimitsFor(RootPath).MaxBytes ?? 0;

    /// <summary>
    /// Meldet diesen Cache vom Budget ab. Die Freigabe laeuft nicht mehr.
    /// </summary>
    public void LeaveBudget() => _budget?.Forget(this);

    /// <summary>
    /// Zusaetzliche Pruefung, bevor eine Datei verdraengt wird.
    /// </summary>
    /// <remarks>
    /// Der Cache speichert nur Groesse und Zugriffszeit. Ob eine Datei die
    /// letzte Kopie im Netz ist, weiss allein die Freigabe, denn sie sieht die
    /// Indizes der Gegenstellen. Beim Verdraengen werden die lokalen Bytes
    /// geloescht. Das ist nur zulaessig, wenn sie sich erneut beschaffen
    /// lassen.
    ///
    /// Ist kein Rueckruf gesetzt, wird ohne diese Pruefung verdraengt.
    /// </remarks>
    public Func<string, bool>? MayEvict { get; set; }

    public long UsedBytes => _entries.Values.Sum(e => e.Bytes);

    public int FileCount => _entries.Count;

    /// <summary>Wo diese Freigabe liegt. Das Budget gruppiert danach.</summary>
    public string RootPath => _rootPath;

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

    /// <summary>
    /// Nimmt eine Datei aus der Buchfuehrung, ohne sie anzufassen.
    /// </summary>
    /// <remarks>
    /// Fuer eine Datei, die gerade fortgenommen und durch einen Platzhalter
    /// ersetzt wurde. Dehydrieren waere hier verkehrt: es gibt nichts mehr zu
    /// dehydrieren, und der Aufruf schluege fehl.
    /// </remarks>
    public bool Forget(string relativePath)
    {
        return _entries.TryRemove(relativePath, out _);
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
    /// Sorgt dafuer, dass das gemeinsame Budget wieder eingehalten wird.
    /// Welche Dateien weichen, entscheidet das Budget, denn nur es sieht alle
    /// Freigaben.
    /// </summary>
    public Task EnforceBudgetAsync() => _budget?.EnforceAsync() ?? Task.CompletedTask;

    /// <summary>Die Dateien, die hier verdraengt werden duerfen.</summary>
    internal IEnumerable<(string Path, long Bytes, DateTimeOffset LastAccess)> EvictionCandidates()
        => _entries
            .Where(e => !IsPinned(e.Key))
            .Where(e => MayEvict?.Invoke(e.Key) ?? true)
            .Select(e => (e.Key, e.Value.Bytes, e.Value.LastAccess));

    /// <summary>Gibt die Bytes einer einzelnen Datei frei.</summary>
    internal bool Evict(string relativePath)
    {
        if (!_entries.ContainsKey(relativePath)) return false;
        if (!Dehydrate(relativePath, "verdraengt")) return false;

        _entries.TryRemove(relativePath, out _);
        return true;
    }

    /// <summary>
    /// Schreibt die Buchfuehrung nach einer Verdraengungsrunde fort.
    /// </summary>
    internal void Persist() => Save();

    /// <summary>
    /// Gleicht die Buchfuehrung mit der Platte ab. Noetig beim Start, weil
    /// zwischen zwei Laeufen Dateien geoeffnet oder freigegeben worden sein
    /// koennen.
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
            // Ein dehydrierter Platzhalter traegt RecallOnDataAccess. Fehlt das
            // Attribut, liegen die Bytes tatsaechlich lokal.
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
            // Schreibzugriff ist noetig. Die ReadWrite-Freigabe verhindert,
            // dass ein Leser die Datei blockiert.
            using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            // Eine lokal geaenderte Datei ist nicht mehr abgeglichen, und
            // Dehydrieren wuerde die Aenderung verwerfen. Windows lehnt das
            // von sich aus ab. Diese Absage kaeme aber im selben Zweig an wie
            // "gerade in Benutzung, spaeter erneut versuchen". Dann bliebe
            // unbemerkt, dass hier eine Aenderung liegt, die nirgends
            // hingeschrieben wird.
            if (IsInSync(handle) == false)
            {
                _log?.Invoke($"  \"{relativePath}\" ist lokal geaendert und bleibt liegen " +
                             $"({reason}). Der Schreibweg fehlt noch -- die Aenderung erreicht " +
                             "die Gegenstelle nicht.");
                return false;
            }

            // Eine hineinkopierte Datei ist noch gar kein Platzhalter, und
            // dehydrieren laesst sich nur ein Platzhalter. Sie muss erst
            // umgewandelt werden -- beides in einem Zug, damit zwischen
            // Umwandlung und Freigabe kein Zustand entsteht, in dem die Datei
            // weder das eine noch das andere ist.
            var erfolg = IsPlaceholder(handle)
                ? Dehydrieren(handle, relativePath, reason)
                : Umwandeln(handle, relativePath, reason);

            return erfolg;
        }
        catch (IOException)
        {
            // In Benutzung. Beim naechsten Durchgang erneut versuchen.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gibt an, ob die Datei mit der Gegenstelle abgeglichen ist.
    /// </summary>
    /// <returns>
    /// <c>false</c> nur, wenn Windows den Abgleich ausdruecklich verneint.
    /// <c>true</c> auch dann, wenn sich der Zustand nicht ermitteln liess. Die
    /// eigentliche Sicherung ist die Weigerung der Cloud-Filter-Schicht, diese
    /// Pruefung macht sie nur sichtbar.
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

        // Was kein Platzhalter ist, hat keinen Abgleichzustand. Eine
        // gewoehnliche Datei im Ordner wird hier nicht behandelt.
        if ((state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER) == 0) return true;

        return (state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_IN_SYNC) != 0;
    }

    private unsafe bool Dehydrieren(SafeFileHandle handle, string relativePath, string reason)
    {
        // Laenge -1 bedeutet die ganze Datei.
        var result = PInvoke.CfDehydratePlaceholder(
            handle, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, null);

        if (result.Failed)
        {
            _log?.Invoke($"  Dehydrieren von \"{relativePath}\" ({reason}) " +
                         $"schlug fehl: 0x{(uint)result.Value:X8}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Macht aus einer gewoehnlichen Datei einen leeren Platzhalter.
    /// </summary>
    /// <remarks>
    /// Der Fall entsteht, wenn jemand Dateien in den Ordner kopiert. Sie
    /// liegen dann vollstaendig auf der Platte und tragen keinen
    /// Reparse-Point; die Buchfuehrung zaehlt sie mit, aber freigeben liess
    /// sich nichts, denn dehydrieren kann man nur einen Platzhalter.
    ///
    /// Umwandeln und Freigeben laufen in einem Zug (CF_CONVERT_FLAG_DEHYDRATE).
    /// Zwei getrennte Schritte liessen zwischendurch eine Datei zurueck, die
    /// weder gewoehnlich noch vollstaendig waere -- und ein Absturz dazwischen
    /// haette genau diesen Zustand hinterlassen.
    ///
    /// MARK_IN_SYNC sagt Windows, dass der Inhalt dem der Gegenstelle
    /// entspricht. Das ist hier zutreffend: freigegeben wird nur, was die
    /// Gegenstelle nachweislich vollstaendig fuehrt.
    /// </remarks>
    private unsafe bool Umwandeln(SafeFileHandle handle, string relativePath, string reason)
    {
        // Die Kennung kommt im Rueckruf zurueck. Derselbe Aufbau wie beim
        // Anlegen der Platzhalter: der volle relative Pfad.
        var identity = Marshal.StringToHGlobalUni(relativePath);
        try
        {
            var result = PInvoke.CfConvertToPlaceholder(
                handle,
                (void*)identity,
                (uint)((relativePath.Length + 1) * sizeof(char)),
                CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC | CF_CONVERT_FLAGS.CF_CONVERT_FLAG_DEHYDRATE,
                null,
                null);

            if (result.Failed)
            {
                _log?.Invoke($"  Umwandeln von \"{relativePath}\" in einen Platzhalter ({reason}) " +
                             $"schlug fehl: 0x{(uint)result.Value:X8}");
                return false;
            }

            _log?.Invoke($"  \"{relativePath}\" in einen Platzhalter umgewandelt ({reason}).");
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(identity);
        }
    }

    /// <summary>Ist die Datei ueberhaupt ein Platzhalter?</summary>
    private unsafe bool IsPlaceholder(SafeFileHandle handle)
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

        return (state & CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_PLACEHOLDER) != 0;
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
