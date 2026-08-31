namespace SyncTClient.Vfs;

/// <summary>
/// Ein Budget für alle Freigaben zusammen.
/// </summary>
/// <remarks>
/// Der Cache ist kein eigenes Verzeichnis. Eine zwischengespeicherte Datei
/// liegt an ihrem Platz im Explorer und haelt ihre Bytes lokal. Verdraengen
/// bedeutet, sie wieder zum Platzhalter zu machen. Ein Limit je Freigabe waere
/// deshalb nur eine Rechenaufgabe fuer den Benutzer. Es gibt eine Platte, also
/// gibt es ein Budget.
///
/// Verdraengt wird ueber alle Freigaben hinweg nach Zugriffszeit. Zuerst
/// weichen Dateien, auf die seit Monaten niemand zugegriffen hat, und nicht
/// die Dateien der Freigabe, die gerade waechst.
/// </remarks>
public sealed class CacheBudget
{
    private readonly List<HydrationCache> _caches = [];
    private readonly SemaphoreSlim _evictionLock = new(1, 1);

    private readonly string _probePath;

    public CacheBudget(long maxBytes, long minimumFreeBytes, string probePath)
    {
        MaxBytes = maxBytes;
        MinimumFreeBytes = minimumFreeBytes;
        _probePath = probePath;
    }

    /// <summary>Null oder kleiner bedeutet: kein Limit, nichts wird verdraengt.</summary>
    public long MaxBytes { get; }

    /// <summary>
    /// So viel soll auf dem Laufwerk frei bleiben. 0 bedeutet, dass der freie
    /// Platz nicht beruecksichtigt wird.
    /// </summary>
    /// <remarks>
    /// Das ist die zweite Grenze. Ein Budget allein reicht nicht, wenn andere
    /// Programme die Platte fuellen. Der Cache haelt dann sein Budget ein, die
    /// Platte ist trotzdem voll.
    /// </remarks>
    public long MinimumFreeBytes { get; }

    /// <summary>
    /// Der freie Platz auf dem Laufwerk des Caches. -1 bedeutet, dass er sich
    /// nicht feststellen laesst.
    /// </summary>
    public long FreeBytes => FreeBytesOn(_probePath);

    /// <summary>
    /// Der freie Platz auf dem Laufwerk, auf dem dieser Pfad liegt.
    /// </summary>
    /// <remarks>
    /// Gemeint ist der Platz, der diesem Benutzer zusteht. Bei einem
    /// Kontingent ist das weniger, als die Platte insgesamt noch frei hat.
    /// </remarks>
    public static long FreeBytesOn(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            // Netzlaufwerk, abgezogener Stick oder fehlende Rechte. Dann gilt
            // nur das Budget.
            return -1;
        }
    }

    /// <summary>
    /// Wie viel der Cache halten darf. Zwei Grenzen, es gilt die schaerfere.
    /// </summary>
    private long LimitFor(long used)
    {
        var limit = MaxBytes > 0 ? MaxBytes : long.MaxValue;
        var free = FreeBytes;

        if (MinimumFreeBytes <= 0 || free < 0) return limit;

        // Jedes freigegebene Byte ist ein Byte mehr frei.
        var missing = MinimumFreeBytes - free;
        return missing <= 0 ? limit : Math.Min(limit, Math.Max(0, used - missing));
    }

    /// <summary>
    /// Wohin die Bilanz gemeldet wird. Es gilt der zuerst gesetzte Empfaenger.
    /// </summary>
    public Action<string>? Log { get; set; }

    public long UsedBytes
    {
        get { lock (_caches) return _caches.Sum(c => c.UsedBytes); }
    }

    /// <summary>Welche Grenze das Holen einer Datei verhindert.</summary>
    public enum Limit
    {
        /// <summary>Keine. Die Datei passt.</summary>
        None,

        /// <summary>Die Datei ist groesser als das Budget des Caches.</summary>
        Budget,

        /// <summary>Es bliebe zu wenig auf der Platte frei.</summary>
        FreeSpace
    }

    /// <summary>
    /// Wie viele Bytes sich freigeben liessen.
    /// </summary>
    /// <remarks>
    /// Angeheftete Dateien und Dateien, die sonst nirgends vollstaendig
    /// vorliegen, zaehlen nicht mit. Sie duerfen nicht verdraengt werden und
    /// stehen deshalb nicht als Reserve zur Verfuegung.
    /// </remarks>
    public long EvictableBytes
    {
        get
        {
            HydrationCache[] caches;
            lock (_caches) caches = [.. _caches];

            return caches.Sum(c => c.EvictionCandidates().Sum(e => e.Bytes));
        }
    }

    /// <summary>
    /// Prüft vor dem Holen, ob eine Datei dieser Größe Platz hat.
    /// </summary>
    /// <remarks>
    /// Ohne diese Prüfung laeuft der Download an, verdraengt unterwegs den
    /// gesamten uebrigen Cache und wird am Ende selbst verworfen. Das kostet
    /// Uebertragung ohne Ergebnis. Stattdessen wird vorher abgesagt und der
    /// Grund genannt.
    /// </remarks>
    public Limit CanHold(long bytes)
    {
        // Eine Datei, die allein schon groesser ist als das Budget, kann nie
        // bleiben. Daran aendert auch Verdraengen nichts.
        if (MaxBytes > 0 && bytes > MaxBytes) return Limit.Budget;

        if (MinimumFreeBytes <= 0) return Limit.None;

        var free = FreeBytes;
        if (free < 0) return Limit.None;

        // Verdraengbares zaehlt als Reserve. Es wuerde beim Holen ohnehin
        // weichen und gibt seinen Platz frei.
        return free + EvictableBytes - bytes < MinimumFreeBytes ? Limit.FreeSpace : Limit.None;
    }

    public void Register(HydrationCache cache)
    {
        lock (_caches)
            if (!_caches.Contains(cache))
                _caches.Add(cache);
    }

    public void Forget(HydrationCache cache)
    {
        lock (_caches) _caches.Remove(cache);
    }

    /// <summary>
    /// Dehydriert, bis das Budget wieder eingehalten wird. Aelteste Zugriffe
    /// zuerst, ueber alle Freigaben hinweg. Angeheftete Dateien bleiben
    /// erhalten.
    /// </summary>
    public async Task EnforceAsync()
    {
        var used = UsedBytes;
        var limit = LimitFor(used);

        if (limit == long.MaxValue || used <= limit) return;

        await ShrinkToAsync(limit, "verdraengt").ConfigureAwait(false);
    }

    /// <summary>
    /// Gibt alles frei, was freigegeben werden darf. Aus den Dateien werden
    /// wieder Platzhalter.
    /// </summary>
    /// <remarks>
    /// Angeheftete Dateien bleiben erhalten, und die Mindestzahl an Kopien
    /// gilt auch hier. Auch auf Knopfdruck wird die letzte Kopie einer Datei
    /// nicht verworfen.
    /// </remarks>
    public Task<(int Files, long Bytes)> ClearAsync() => ShrinkToAsync(0, "geleert");

    private async Task<(int Files, long Bytes)> ShrinkToAsync(long limit, string reason)
    {
        await _evictionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            HydrationCache[] caches;
            lock (_caches) caches = [.. _caches];

            var used = caches.Sum(c => c.UsedBytes);
            if (used <= limit) return (0, 0);

            var candidates = caches
                .SelectMany(c => c.EvictionCandidates().Select(e => (Cache: c, e.Path, e.Bytes, e.LastAccess)))
                .OrderBy(e => e.LastAccess)
                .ToList();

            var evicted = 0;
            long freed = 0;

            foreach (var candidate in candidates)
            {
                if (used - freed <= limit) break;
                if (!candidate.Cache.Evict(candidate.Path)) continue;
                freed += candidate.Bytes;
                evicted++;
            }

            if (evicted > 0)
            {
                foreach (var cache in caches) cache.Persist();
                Log?.Invoke($"Cache: {evicted} Dateien {reason}, {freed / (1024.0 * 1024.0):0.0} MB frei " +
                            $"({(used - freed) / (1024.0 * 1024.0):0.0} MB belegt).");
            }
            else
            {
                Log?.Invoke($"Cache: nichts {reason} ({used / (1024.0 * 1024.0):0.0} MB belegt) -- " +
                            "alles angeheftet oder in Benutzung.");
            }

            return (evicted, freed);
        }
        finally
        {
            _evictionLock.Release();
        }
    }
}
