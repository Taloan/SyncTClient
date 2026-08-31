namespace SyncTClient.Vfs;

/// <summary>
/// Ein Budget für alle Freigaben zusammen.
/// </summary>
/// <remarks>
/// Der Cache ist kein Verzeichnis. Zwischengespeichert ist eine Datei, die an
/// ihrem Platz im Explorer liegt und ihre Bytes lokal hat; verdraengen heisst,
/// sie wieder zum Platzhalter zu machen. Ein Limit je Freigabe waere darum nur
/// eine Rechenaufgabe fuer den Benutzer -- die Platte ist eine, also ist das
/// Budget eines.
///
/// Verdraengt wird quer ueber alle Freigaben nach Zugriffszeit: der Ordner,
/// den seit Monaten niemand angefasst hat, gibt nach, nicht der, der zufaellig
/// gerade waechst.
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
    /// So viel soll auf dem Laufwerk frei bleiben. 0 heisst: keine Ruecksicht.
    /// </summary>
    /// <remarks>
    /// Die zweite Grenze. Ein Budget allein hilft nicht, wenn daneben etwas
    /// anderes die Platte fuellt -- dann ist der Cache im Recht und die Platte
    /// trotzdem voll.
    /// </remarks>
    public long MinimumFreeBytes { get; }

    /// <summary>Was auf dem Laufwerk des Caches frei ist. -1: nicht feststellbar.</summary>
    public long FreeBytes => FreeBytesOn(_probePath);

    /// <summary>
    /// Der freie Platz auf dem Laufwerk, auf dem dieser Pfad liegt.
    /// </summary>
    /// <remarks>
    /// Gefragt ist der Platz, der diesem Benutzer zusteht -- bei einem
    /// Kontingent ist das weniger als das, was die Platte noch hat.
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
            // Netzlaufwerk, abgezogener Stick, fehlende Rechte -- dann gilt
            // eben nur das Budget.
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

    /// <summary>Wohin die Bilanz gemeldet wird. Der erste, der sich meldet, gibt sie vor.</summary>
    public Action<string>? Log { get; set; }

    public long UsedBytes
    {
        get { lock (_caches) return _caches.Sum(c => c.UsedBytes); }
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
    /// Dehydriert, bis das Budget wieder eingehalten wird -- aelteste Zugriffe
    /// zuerst, ueber alle Freigaben hinweg. Angeheftete Dateien bleiben.
    /// </summary>
    public async Task EnforceAsync()
    {
        var used = UsedBytes;
        var limit = LimitFor(used);

        if (limit == long.MaxValue || used <= limit) return;

        await ShrinkToAsync(limit, "verdraengt").ConfigureAwait(false);
    }

    /// <summary>
    /// Gibt alles frei, was freigegeben werden darf: aus den Dateien werden
    /// wieder Platzhalter.
    /// </summary>
    /// <remarks>
    /// Angeheftete Dateien bleiben, und die Mindestzahl an Kopien gilt auch
    /// hier -- ein Knopf ist kein Grund, die letzte Kopie einer Datei
    /// wegzuwerfen.
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
