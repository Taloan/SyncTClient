namespace SyncTClient.Vfs;

/// <summary>Was auf einem Datenträger liegt und was davon weichen dürfte.</summary>
/// <param name="Root">Die Wurzel, etwa <c>C:\</c>.</param>
/// <param name="UsedBytes">Was die Freigaben auf diesem Datenträger belegen.</param>
/// <param name="Files">Wie viele Dateien das sind.</param>
/// <param name="FreeBytes">Freier Platz, oder -1 wenn nicht feststellbar.</param>
/// <param name="EvictableBytes">Davon freigebbar.</param>
/// <param name="EvictableFiles">Wie viele Dateien das sind.</param>
public sealed record VolumeUsage(
    string Root,
    long UsedBytes,
    int Files,
    long FreeBytes,
    long EvictableBytes,
    int EvictableFiles);

/// <summary>
/// Wacht darüber, wie viel Platz die Inhalte der Freigaben belegen.
/// </summary>
/// <remarks>
/// Es gibt kein Cache-Verzeichnis. Eine geholte Datei liegt an ihrem Platz im
/// Explorer und hält ihre Bytes dort. Freigeben bedeutet, sie wieder zum
/// Platzhalter zu machen -- gleicher Name, gleicher Ort, nur ohne Inhalt.
///
/// Gerechnet wird <b>je Datenträger</b> und nicht programmweit. Freigaben
/// können auf verschiedenen Laufwerken liegen, und ein gemeinsames Limit
/// hülfe dann niemandem: es würde Dateien auf einer leeren Platte freigeben,
/// weil eine andere volläuft. Der freie Platz ist ohnehin eine Eigenschaft
/// des Datenträgers, nicht des Programms.
///
/// Freigegeben wird nach Zugriffszeit, über alle Freigaben desselben
/// Datenträgers hinweg. Zuerst weicht, was seit Monaten niemand geöffnet hat,
/// und nicht das, was zur gerade wachsenden Freigabe gehört.
/// </remarks>
public sealed class CacheBudget
{
    private readonly List<HydrationCache> _caches = [];
    private readonly SemaphoreSlim _evictionLock = new(1, 1);

    public CacheBudget(long maxBytes, long minimumFreeBytes)
    {
        MaxBytes = maxBytes;
        MinimumFreeBytes = minimumFreeBytes;
    }

    /// <summary>
    /// Höchstens so viel darf auf <em>jedem</em> Datenträger belegt sein.
    /// 0 oder kleiner bedeutet: kein Limit.
    /// </summary>
    public long MaxBytes { get; }

    /// <summary>
    /// So viel soll auf <em>jedem</em> Datenträger frei bleiben. 0 bedeutet,
    /// dass der freie Platz nicht berücksichtigt wird.
    /// </summary>
    /// <remarks>
    /// Die zweite Grenze. Ein Limit allein reicht nicht, wenn andere Programme
    /// die Platte füllen: die Freigaben halten dann ihr Limit ein, und die
    /// Platte ist trotzdem voll.
    /// </remarks>
    public long MinimumFreeBytes { get; }

    /// <summary>Wohin die Bilanz gemeldet wird.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Der freie Platz auf dem Datenträger, auf dem dieser Pfad liegt.
    /// </summary>
    /// <remarks>
    /// Gemeint ist der Platz, der diesem Benutzer zusteht. Bei einem
    /// Kontingent ist das weniger, als die Platte insgesamt noch frei hat.
    /// </remarks>
    public static long FreeBytesOn(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string RootOf(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? path; }
        catch (Exception) { return path; }
    }

    // ------------------------------------------------------------ Bestand

    /// <summary>Was auf welchem Datenträger liegt, ein Eintrag je Laufwerk.</summary>
    public IReadOnlyList<VolumeUsage> Volumes
    {
        get
        {
            HydrationCache[] caches;
            lock (_caches) caches = [.. _caches];

            return [.. caches
                .GroupBy(c => RootOf(c.RootPath), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var kandidaten = g.SelectMany(c => c.EvictionCandidates()).ToList();

                    return new VolumeUsage(
                        g.Key,
                        g.Sum(c => c.UsedBytes),
                        g.Sum(c => c.FileCount),
                        FreeBytesOn(g.Key),
                        kandidaten.Sum(e => e.Bytes),
                        kandidaten.Count);
                })];
        }
    }

    /// <summary>Was alle Datenträger zusammen belegen.</summary>
    public long UsedBytes
    {
        get { lock (_caches) return _caches.Sum(c => c.UsedBytes); }
    }

    /// <summary>Welche Grenze das Holen einer Datei verhindert.</summary>
    public enum Limit
    {
        /// <summary>Keine. Die Datei passt.</summary>
        None,

        /// <summary>Die Datei ist größer als das Limit des Datenträgers.</summary>
        Budget,

        /// <summary>Es bliebe zu wenig auf dem Datenträger frei.</summary>
        FreeSpace
    }

    /// <summary>
    /// Prüft vor dem Holen, ob eine Datei dieser Größe Platz hat.
    /// </summary>
    /// <remarks>
    /// Ohne diese Prüfung läuft der Download an, gibt unterwegs alles andere
    /// auf demselben Datenträger frei und wird am Ende selbst verworfen. Das
    /// kostet Übertragung ohne Ergebnis. Stattdessen wird vorher abgesagt und
    /// der Grund genannt.
    /// </remarks>
    public Limit CanHold(long bytes, string targetPath)
    {
        // Eine Datei, die allein schon größer ist als das Limit, kann nie
        // bleiben. Daran ändert auch Freigeben nichts.
        if (MaxBytes > 0 && bytes > MaxBytes) return Limit.Budget;
        if (MinimumFreeBytes <= 0) return Limit.None;

        var free = FreeBytesOn(targetPath);
        if (free < 0) return Limit.None;

        var root = RootOf(targetPath);
        var freigebbar = Volumes.FirstOrDefault(v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
            ?.EvictableBytes ?? 0;

        // Freigebbares zählt als Reserve. Es würde beim Holen ohnehin weichen.
        return free + freigebbar - bytes < MinimumFreeBytes ? Limit.FreeSpace : Limit.None;
    }

    // ------------------------------------------------------------ Anmeldung

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

    // ------------------------------------------------------------ Freigeben

    /// <summary>Zieht die Grenzen auf jedem Datenträger nach.</summary>
    public async Task EnforceAsync()
    {
        foreach (var volume in Volumes)
        {
            var limit = LimitFor(volume);
            if (limit == long.MaxValue || volume.UsedBytes <= limit) continue;

            await ShrinkToAsync(volume.Root, limit, "freigegeben").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gibt frei, was freigegeben werden darf -- auf einem Datenträger oder
    /// auf allen.
    /// </summary>
    /// <remarks>
    /// Angeheftete Dateien bleiben, und die Platzhalter-Schwelle gilt auch
    /// hier. Auch auf Knopfdruck wird die letzte Kopie einer Datei im Netz
    /// nicht verworfen.
    /// </remarks>
    public async Task<(int Files, long Bytes)> ClearAsync(string? root = null)
    {
        var summe = (Files: 0, Bytes: 0L);

        foreach (var volume in Volumes)
        {
            if (root is not null && !volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;

            var (files, bytes) = await ShrinkToAsync(volume.Root, 0, "freigegeben").ConfigureAwait(false);
            summe = (summe.Files + files, summe.Bytes + bytes);
        }

        return summe;
    }

    /// <summary>Bis wohin dieser Datenträger schrumpfen muss.</summary>
    private long LimitFor(VolumeUsage volume)
    {
        var limit = MaxBytes > 0 ? MaxBytes : long.MaxValue;

        if (MinimumFreeBytes <= 0 || volume.FreeBytes < 0) return limit;

        // Jedes freigegebene Byte ist ein Byte mehr frei.
        var fehlend = MinimumFreeBytes - volume.FreeBytes;
        return fehlend <= 0 ? limit : Math.Min(limit, Math.Max(0, volume.UsedBytes - fehlend));
    }

    private async Task<(int Files, long Bytes)> ShrinkToAsync(string root, long limit, string reason)
    {
        await _evictionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            HydrationCache[] caches;
            lock (_caches)
                caches = [.. _caches.Where(c => RootOf(c.RootPath).Equals(root, StringComparison.OrdinalIgnoreCase))];

            var belegt = caches.Sum(c => c.UsedBytes);
            if (belegt <= limit) return (0, 0);

            var kandidaten = caches
                .SelectMany(c => c.EvictionCandidates().Select(e => (Cache: c, e.Path, e.Bytes, e.LastAccess)))
                .OrderBy(e => e.LastAccess)
                .ToList();

            var anzahl = 0;
            long frei = 0;

            foreach (var kandidat in kandidaten)
            {
                if (belegt - frei <= limit) break;
                if (!kandidat.Cache.Evict(kandidat.Path)) continue;

                frei += kandidat.Bytes;
                anzahl++;
            }

            if (anzahl > 0)
            {
                foreach (var cache in caches) cache.Persist();
                Log?.Invoke($"{root} {anzahl} Dateien {reason}, {frei / (1024.0 * 1024.0):0.0} MB frei " +
                            $"({(belegt - frei) / (1024.0 * 1024.0):0.0} MB belegt).");
            }
            else
            {
                Log?.Invoke($"{root} nichts {reason} ({belegt / (1024.0 * 1024.0):0.0} MB belegt) -- " +
                            "angeheftet, in Benutzung, oder die Platzhalter-Schwelle ist nicht erreicht.");
            }

            return (anzahl, frei);
        }
        finally
        {
            _evictionLock.Release();
        }
    }
}
