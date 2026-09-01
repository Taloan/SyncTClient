using System.Collections.Concurrent;
using SyncTClient.Bep;
using SyncTClient.Vfs;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;
using FileInfoType = SyncTClient.Bep.Proto.FileInfoType;
using Counter = SyncTClient.Bep.Proto.Counter;
using Vector = SyncTClient.Bep.Proto.Vector;

namespace SyncTClient.Mount;

/// <summary>
/// Was die Gegenstelle geaendert hat, wird auf den Ordner angewendet.
/// </summary>
/// <remarks>
/// Der Index allein reicht nicht. Er sagt, was die Gegenstelle fuehrt; im
/// Ordner steht davon nichts, solange niemand ihn anfasst. Platzhalter wurden
/// bisher nur einmal angelegt, beim Verbinden. Eine spaeter angelegte,
/// umbenannte, geaenderte oder geloeschte Datei blieb deshalb unsichtbar.
///
/// Angewendet wird nicht im Empfangsweg, sondern im Hintergrundlauf. Dort
/// laufen bereits die Schreibzugriffe auf dieselbe Datenbank, und der
/// Empfangsweg darf nicht auf das Dateisystem warten.
///
/// Eine geaenderte Datei wird nicht heruntergeladen. Sie wird zum Platzhalter,
/// und der Inhalt kommt, wenn jemand sie oeffnet. Das ist der Zweck dieses
/// Ordners: gehalten wird, was gebraucht wird.
/// </remarks>
public sealed partial class ShareHost
{
    /// <summary>Der Ordner, in dem ersetzte und geloeschte Versionen liegen.</summary>
    public const string VersionsFolder = ".stversions";

    /// <summary>Namen, deren Version von der Gegenstelle noch anzuwenden ist.</summary>
    private readonly ConcurrentDictionary<string, byte> _incoming = new(StringComparer.Ordinal);

    /// <summary>
    /// Namen, die auch dann angekuendigt werden, wenn Groesse und Zeit
    /// unveraendert sind.
    /// </summary>
    /// <remarks>
    /// Der Vorfilter in <c>Evaluate</c> uebergeht eine Datei, deren
    /// Groesse und Zeit zum eigenen Eintrag passen. Nach einem gewonnenen
    /// Konflikt trifft das zu, und trotzdem muss die eigene Version heraus:
    /// die Gegenstelle kennt sie nicht.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _force = new(StringComparer.Ordinal);

    private DateTime _lastVersionSweep = DateTime.MinValue;

    /// <summary>Gehoert der Name zu unserer eigenen Sicherung?</summary>
    internal static bool IsVersionsPath(string name)
        => name.Equals(VersionsFolder, StringComparison.OrdinalIgnoreCase)
           || name.StartsWith(VersionsFolder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Vermerkt, dass diese Namen neu zu betrachten sind.</summary>
    /// <summary>Nimmt Namen entgegen und meldet, wie viele neu dazukamen.</summary>
    /// <remarks>
    /// Gezaehlt wird, was wirklich Arbeit macht. Ausgeschlossene Namen und
    /// alte Versionen fallen heraus, und ein Name, der ohnehin schon in der
    /// Schlange steht, ist keine zusaetzliche Aufgabe -- sonst waere die
    /// Gesamtzahl groesser als die Menge, die je abgearbeitet wird.
    /// </remarks>
    private int QueueIncoming(IEnumerable<string> names)
    {
        var neu = 0;

        foreach (var name in names)
        {
            if (!_config.Includes(name) || IsVersionsPath(name)) continue;
            if (_incoming.TryAdd(name, 0)) neu++;
        }

        if (neu > 0) Wake();
        return neu;
    }

    // ------------------------------------------------------------ Anwenden

    /// <summary>Was ein Durchgang bewirkt hat.</summary>
    private sealed class Bilanz
    {
        public int Angelegt;
        public int Ersetzt;
        public int Entfernt;
        public int Konflikte;

        public bool Leer => Angelegt + Ersetzt + Entfernt + Konflikte == 0;

        public override string ToString()
        {
            var teile = new List<string>(4);
            if (Angelegt > 0) teile.Add($"{Angelegt} neu");
            if (Ersetzt > 0) teile.Add($"{Ersetzt} geaendert");
            if (Entfernt > 0) teile.Add($"{Entfernt} entfernt");
            if (Konflikte > 0) teile.Add($"{Konflikte} Konflikt(e)");
            return string.Join(", ", teile);
        }
    }

    private void ApplyIncoming()
    {
        // Vor dem Verbinden gibt es keinen Ordner, auf den etwas anzuwenden
        // waere. Der erste Index geht in den Lauf, der die Platzhalter anlegt.
        if (_mount is null || _incoming.IsEmpty) return;

        // Angehalten heisst: kein fremder Stand ueberschreibt hier etwas. Die
        // Namen bleiben in der Schlange und werden beim Fortsetzen
        // abgearbeitet -- verloren geht nichts.
        if (IsPaused) return;

        var bilanz = new Bilanz();

        foreach (var name in _incoming.Keys.ToArray())
        {
            _incoming.TryRemove(name, out _);

            try
            {
                Apply(name, bilanz);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In Benutzung oder gesperrt. Die naechste Ankuendigung der
                // Gegenstelle nennt den Namen wieder.
                _log($"[{FolderId}] \"{name}\" liess sich nicht uebernehmen: {ex.Message}");
            }
        }

        // Nur was wirklich hergestellt wurde, zieht den Rueckstand herunter.
        // Ein Name, der schon so dastand, war nie ein Rueckstand.
        Fortschritt(bilanz.Angelegt + bilanz.Ersetzt + bilanz.Entfernt);

        if (!bilanz.Leer) _log($"[{FolderId}] von der Gegenstelle uebernommen: {bilanz}.");
    }

    private void Apply(string name, Bilanz bilanz)
    {
        BepFileInfo theirs;
        BepFileInfo? mine;

        lock (_indexGate)
        {
            if (!_index!.TryGet(name, out theirs)) return;
            mine = _index.TryGetLocal(name, out var eigene) ? eigene : null;
        }

        // Die entscheidende Pruefung, denn hier wird angelegt. Erst hier ist
        // bekannt, ob der Name ein Verzeichnis meint -- und "Dateien in X"
        // umfasst die losen Dateien von X, nicht seine Unterverzeichnisse.
        if (!_config.Includes(name, theirs.Type == FileInfoType.Directory)) return;

        var path = LocalPathOf(name);

        // Solange hier geschrieben wird, ist jede Meldung darueber unsere
        // eigene. Ohne diese Sperre kuendigen wir an, was wir gerade von der
        // Gegenstelle uebernommen haben.
        using var hold = HoldHydration(name);

        if (mine is not null && !mine.Deleted)
        {
            switch (VersionVectors.Compare(mine.Version, theirs.Version))
            {
                // Unsere Version enthaelt ihre. Sie geht hinaus, nicht herein.
                case VersionOrder.Neuer:
                    return;

                // Derselbe Stand. Nur wenn der Ordner ihm widerspricht, ist
                // etwas zu tun.
                case VersionOrder.Gleich when File.Exists(path) != theirs.Deleted:
                    return;

                case VersionOrder.Nebeneinander:
                    bilanz.Konflikte++;
                    if (!ResolveConflict(name, path, mine, theirs)) return;
                    break;
            }
        }

        if (theirs.Deleted)
        {
            RemoveLocally(name, path, bilanz);
            return;
        }

        if (theirs.Type == FileInfoType.Directory)
        {
            Directory.CreateDirectory(path);
            return;
        }

        PlaceRemoteVersion(name, path, theirs, bilanz);
    }

    /// <summary>
    /// Setzt den Platzhalter fuer die Version der Gegenstelle. Eine vorhandene
    /// Datei weicht ihm.
    /// </summary>
    /// <remarks>
    /// Der alte Eintrag wird fortgenommen und ein neuer angelegt, statt den
    /// vorhandenen umzuschreiben. Das gilt fuer einen Platzhalter ebenso wie
    /// fuer eine gewoehnliche Datei, und es kommt ohne die Sonderfaelle aus,
    /// die das Umschreiben eines teilweise gefuellten Platzhalters mit sich
    /// braechte.
    /// </remarks>
    private void PlaceRemoteVersion(string name, string path, BepFileInfo theirs, Bilanz bilanz)
    {
        var info = new System.IO.FileInfo(path);

        // Was schon so dasteht, wird nicht angefasst. Das trifft nach jeder
        // erneuten Ankuendigung derselben Datei zu, und ein Neuanlegen wuerde
        // einen geholten Inhalt ohne Not wegwerfen.
        if (info.Exists
            && IsPlaceholder(path)
            && info.Length == theirs.Size
            && new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() == theirs.ModifiedS)
        {
            return;
        }

        var neu = !info.Exists;

        // "Immer auf diesem Geraet behalten" haengt als Attribut an der Datei.
        // Gleich wird sie fortgenommen und neu angelegt; ohne dieses Merken
        // waere das Versprechen nach der naechsten Aenderung der Gegenstelle
        // stillschweigend aufgehoben.
        var angeheftet = !neu && IsPinnedFile(path);

        if (!neu)
        {
            // Nur was Inhalt hat, ist es wert gesichert zu werden. Ein
            // Platzhalter haelt keinen.
            if (IsPlaceholder(path) || !KeepVersion(name, path)) File.Delete(path);
            _cache?.Forget(name);
        }

        var entry = new VirtualEntry(
            name, theirs.Size, DateTimeOffset.FromUnixTimeSeconds(theirs.ModifiedS), false);

        if (!_mount!.CreatePlaceholder(entry))
        {
            _log($"[{FolderId}] Platzhalter fuer \"{name}\" liess sich nicht anlegen.");
            return;
        }

        if (angeheftet) _mount.SetPinned(path, true);

        // Unser eigener Eintrag ist damit hinfaellig. Was hier liegt, ist ab
        // jetzt die Version der Gegenstelle.
        lock (_indexGate) _index!.ForgetLocal(name);

        if (neu) bilanz.Angelegt++; else bilanz.Ersetzt++;
    }

    /// <summary>Ob diese Datei "immer auf diesem Geraet" bleiben soll.</summary>
    private static bool IsPinnedFile(string path)
    {
        try { return ((uint)new System.IO.FileInfo(path).Attributes & 0x0008_0000) != 0; }
        catch (Exception) { return false; }
    }

    private void RemoveLocally(string name, string path, Bilanz bilanz)
    {
        if (Directory.Exists(path))
        {
            // Ein Verzeichnis wird nur entfernt, wenn nichts mehr darin steht.
            // Was noch darin liegt, ist entweder noch nicht angekommen oder
            // gehoert uns.
            try
            {
                Directory.Delete(path);
                bilanz.Entfernt++;
            }
            catch (IOException)
            {
                // Nicht leer.
            }

            return;
        }

        if (File.Exists(path))
        {
            if (IsPlaceholder(path) || !KeepVersion(name, path)) File.Delete(path);
            _cache?.Forget(name);
            bilanz.Entfernt++;
        }

        lock (_indexGate) _index!.ForgetLocal(name);
    }

    // ------------------------------------------------------------ Konflikt

    /// <summary>
    /// Entscheidet, welche der beiden unabhaengig entstandenen Versionen gilt.
    /// </summary>
    /// <returns>
    /// <c>true</c>, wenn die Version der Gegenstelle gesetzt werden soll.
    /// <c>false</c>, wenn die eigene bleibt.
    /// </returns>
    private bool ResolveConflict(string name, string path, BepFileInfo mine, BepFileInfo theirs)
    {
        switch (_config.Conflict)
        {
            case ConflictResolution.Local:
                return KeepMine(name, theirs, "die eigene Version hat Vorrang");

            case ConflictResolution.Remote:
                _log($"[{FolderId}] Konflikt bei \"{name}\": die Version der Gegenstelle hat Vorrang.");
                return true;

            case ConflictResolution.Newer:
                return mine.ModifiedS > theirs.ModifiedS
                    ? KeepMine(name, theirs, "die eigene Version ist neuer")
                    : TakeTheirs(name, "die Version der Gegenstelle ist neuer");

            case ConflictResolution.Older:
                return mine.ModifiedS < theirs.ModifiedS
                    ? KeepMine(name, theirs, "die eigene Version ist aelter")
                    : TakeTheirs(name, "die Version der Gegenstelle ist aelter");

            default:
                // Beide bleiben. Die eigene wird zur Seite gelegt, die der
                // Gegenstelle nimmt den Platz ein.
                return KeepBoth(name, path);
        }
    }

    private bool TakeTheirs(string name, string grund)
    {
        _log($"[{FolderId}] Konflikt bei \"{name}\": {grund}.");
        return true;
    }

    /// <summary>
    /// Behaelt die eigene Version und sorgt dafuer, dass sie auch hinausgeht.
    /// </summary>
    /// <remarks>
    /// Die Zaehler der Gegenstelle werden dabei in den eigenen Vektor
    /// uebernommen. Ohne das stuenden beide Versionen weiter nebeneinander und
    /// der Konflikt kaeme mit jeder Ankuendigung zurueck.
    /// </remarks>
    private bool KeepMine(string name, BepFileInfo theirs, string grund)
    {
        lock (_indexGate)
        {
            if (_index!.TryGetLocal(name, out var eigene))
            {
                eigene.Version = Merge(eigene.Version, theirs.Version);
                _index.PutLocal(eigene, 1);
            }
        }

        // Der Vorfilter wuerde die Datei uebergehen: Groesse und Zeit haben
        // sich nicht geaendert, nur die Zustaendigkeit.
        _force[name] = 0;
        _dirty[name] = 0;
        Wake();

        _log($"[{FolderId}] Konflikt bei \"{name}\": {grund} -- sie wird angekuendigt.");
        return false;
    }

    /// <summary>Der Vektor, der beide Staende enthaelt.</summary>
    private static Vector Merge(Vector? a, Vector? b)
    {
        var vector = a?.Clone() ?? new Vector();

        foreach (var counter in b?.Counters ?? [])
        {
            var vorhanden = vector.Counters.FirstOrDefault(c => c.Id == counter.Id);
            if (vorhanden is null)
                vector.Counters.Add(new Counter { Id = counter.Id, Value = counter.Value });
            else
                vorhanden.Value = Math.Max(vorhanden.Value, counter.Value);
        }

        // Die Reihenfolge gehoert zum Vergleich, deshalb bleibt sie nach id
        // sortiert.
        var sortiert = vector.Counters.OrderBy(c => c.Id).ToList();
        vector.Counters.Clear();
        vector.Counters.AddRange(sortiert);
        return vector;
    }

    /// <summary>
    /// Der Geraetename, so wie er in einem Dateinamen stehen kann.
    /// </summary>
    /// <remarks>
    /// Syncthing setzt an diese Stelle die Kurzkennung des Geraets. Die ist
    /// auf beiden Seiten dieselbe, sagt aber niemandem, an welchem Geraet die
    /// Version entstand -- und genau das will man wissen, wenn man vor zwei
    /// Dateien steht.
    ///
    /// Beschraenkt auf Buchstaben, Ziffern, Strich und Unterstrich: der Name
    /// wird zum Bestandteil eines Dateinamens und geht ueber das Protokoll an
    /// die Gegenstelle, deren Dateisystem andere Regeln haben kann als dieses.
    /// </remarks>
    private string Geraetekennung()
    {
        var sauber = new string([.. _app.DeviceName
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .Take(24)]);

        return sauber.Length > 0 ? sauber : "unbenannt";
    }

    /// <summary>Legt die eigene Version unter eigenem Namen daneben.</summary>
    private bool KeepBoth(string name, string path)
    {
        if (!File.Exists(path) || IsPlaceholder(path))
        {
            // Ohne Inhalt gibt es nichts zu behalten.
            _log($"[{FolderId}] Konflikt bei \"{name}\": hier liegt kein Inhalt, " +
                 "die Version der Gegenstelle gilt.");
            return true;
        }

        var stamp = File.GetLastWriteTimeUtc(path).ToString("yyyyMMdd-HHmmss");
        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];
        var target = $"{stem}.sync-conflict-{stamp}-{Geraetekennung()}{extension}";

        try
        {
            File.Move(path, LocalPathOf(target), overwrite: false);
            _log($"[{FolderId}] Konflikt bei \"{name}\": die eigene Version liegt jetzt " +
                 $"unter \"{target}\".");

            // Die abgelegte Version ist eine neue Datei in der Freigabe und
            // wird als solche angekuendigt.
            _dirty[target] = 0;
            Wake();
        }
        catch (IOException ex)
        {
            _log($"[{FolderId}] Konflikt bei \"{name}\": die eigene Version liess sich nicht " +
                 $"zur Seite legen ({ex.Message}). Die Gegenstelle gilt.");
        }

        return true;
    }

    // ------------------------------------------------------------ Sicherung

    /// <summary>
    /// Legt eine Version ab, bevor sie ersetzt oder geloescht wird.
    /// </summary>
    /// <returns>
    /// <c>true</c>, wenn die Datei verschoben wurde und an ihrem Platz nichts
    /// mehr liegt.
    /// </returns>
    /// <remarks>
    /// Die Ablage liegt im Ordner selbst, damit das Verschieben ein Verschieben
    /// bleibt. Ausserhalb des Sync-Roots waere es ein Kopieren, und ein
    /// Platzhalter wuerde dabei aus dem Netz geholt.
    /// </remarks>
    private bool KeepVersion(string name, string path)
    {
        if (!_config.KeepVersions) return false;

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path).ToString("yyyyMMdd-HHmmss");
            var extension = Path.GetExtension(name);
            var stem = name[..^extension.Length];

            var target = LocalPathOf($"{VersionsFolder}/{stem}~{stamp}{extension}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Zweimal dieselbe Sekunde ist moeglich. Die aeltere Ablage bleibt.
            if (File.Exists(target))
            {
                File.Delete(path);
                return true;
            }

            File.Move(path, target);

            // Das Verschieben nimmt die urspruengliche Erstellzeit mit. Fuer
            // die Haltedauer zaehlt aber, wann die Version hier abgelegt
            // wurde, sonst faellt eine alte Datei sofort wieder fort.
            File.SetCreationTimeUtc(target, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"[{FolderId}] \"{name}\" liess sich nicht sichern: {ex.Message}");
            return false;
        }
    }

    /// <summary>Wirft ab, was laenger liegt als vereinbart.</summary>
    private void SweepVersions()
    {
        if (!_config.KeepVersions || _config.VersionDays <= 0) return;
        if (DateTime.UtcNow - _lastVersionSweep < TimeSpan.FromHours(1)) return;

        _lastVersionSweep = DateTime.UtcNow;

        var root = LocalPathOf(VersionsFolder);
        if (!Directory.Exists(root)) return;

        var grenze = DateTime.UtcNow - TimeSpan.FromDays(_config.VersionDays);
        var geloescht = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // Massgeblich ist, wann die Version abgelegt wurde. Die
                // Schreibzeit ist die der urspruenglichen Datei und kann weit
                // zurueckliegen.
                if (File.GetCreationTimeUtc(file) > grenze) continue;

                try { File.Delete(file); geloescht++; }
                catch (IOException) { /* in Benutzung */ }
            }

            // Die tiefsten Ordner zuerst, sonst bleibt die Ebene darueber
            // stehen, weil sie beim Pruefen noch nicht leer war.
            foreach (var directory in Directory
                         .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (IOException) { /* nicht leer */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"[{FolderId}] die Sicherung liess sich nicht aufraeumen: {ex.Message}");
            return;
        }

        if (geloescht > 0)
            _log($"[{FolderId}] {geloescht} Versionen aelter als {_config.VersionDays} Tage entfernt.");
    }
}
