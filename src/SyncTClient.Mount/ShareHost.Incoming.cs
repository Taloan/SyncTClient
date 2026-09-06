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

    /// <summary>Die Kennzeichnung, an der Syncthing einen eingehaengten Ordner erkennt.</summary>
    /// <remarks>
    /// Sie gehoert dem Programm auf dieser Seite und beschreibt seinen
    /// Zustand. Uebertragen ergaebe sie keinen Sinn: die Gegenstelle hat ihre
    /// eigene, und beide meinen etwas anderes.
    /// </remarks>
    public const string MarkerFolder = ".stfolder";

    /// <summary>
    /// Woran die eigene Nebendatei zu erkennen ist.
    /// </summary>
    /// <remarks>
    /// Sie entsteht neben der Zieldatei, waehrend deren Inhalt geholt wird,
    /// und verschwindet, sobald er vollstaendig ist. Fuer den Abgleich
    /// existiert sie nicht.
    /// </remarks>
    public const string TempSuffix = ".synct-neu";

    /// <summary>Die Musterliste von Syncthing.</summary>
    /// <remarks>
    /// Sie gehoert demselben Geraet wie die Liste, die hier unter
    /// "Ignorieren" steht: eine Aussage darueber, was dieses eine Geraet
    /// nicht will. Sie zu uebertragen hiesse, diese Entscheidung allen
    /// anderen aufzuzwingen -- und zwar in einer Datei, die selbst wieder
    /// Gegenstand des Abgleichs waere und bei beidseitiger Aenderung einen
    /// Konflikt ergaebe. Syncthing selbst uebertraegt sie ebenfalls nicht.
    /// </remarks>
    public const string IgnoreFile = ".stignore";

    /// <summary>
    /// Gehoert der Name zur Verwaltung und nicht zum Inhalt?
    /// </summary>
    /// <remarks>
    /// Die eigene Sicherung und die Ordnerkennzeichnung. Syncthing haelt es
    /// genauso -- beide sind Buchfuehrung eines Geraets ueber sich selbst und
    /// haben auf der Leitung nichts verloren.
    ///
    /// Der Ordner eines anderen Programms -- etwa ".sync" von Resilio --
    /// steht hier nicht. Er ist Inhalt dieses Ordners, und ob er
    /// mitgenommen wird, entscheidet niemand ausser dem, dem er gehoert.
    /// </remarks>
    public static bool IsHousekeeping(string name)
        => Unterhalb(name, VersionsFolder)
           || Unterhalb(name, MarkerFolder)
           || Heisst(name, IgnoreFile)
           || IstArbeitsdatei(name);

    /// <summary>
    /// Der eigene Namensraum von Syncthing.
    /// </summary>
    /// <remarks>
    /// Zwei Praefixe: "~syncthing~" bis Version 0.14 und ".syncthing."
    /// danach. Syncthing selbst uebergeht damit jede Datei und jeden Ordner,
    /// gleich wie der Name weitergeht -- nicht nur die Zwischenstaende auf
    /// ".tmp". Wer nur auf die Endung sieht, laesst den Rest hindurch.
    ///
    /// Geprueft wird jeder Namensteil, nicht nur der letzte: trifft es einen
    /// Ordner, gehoert alles darunter dazu.
    ///
    /// Anders als ".sync" von Resilio, das Inhalt dieses Ordners ist und
    /// ueber dessen Mitnahme niemand ausser dem Anwender entscheidet: diese
    /// hier gehoeren zur Verwaltung eines Abgleichs, so wie ".stfolder".
    ///
    /// Eine liegengebliebene meldet sich sonst als dauerhafter Rueckstand.
    /// Sie wurde einmal leer angekuendigt, spaeter vollgeschrieben, und die
    /// beiden Staende gehen von da an auseinander.
    /// </remarks>
    private static bool IstArbeitsdatei(string name)
    {
        foreach (var teil in name.Split('/'))
        {
            if (teil.StartsWith("~syncthing~", StringComparison.OrdinalIgnoreCase)
                || teil.StartsWith(".syncthing.", StringComparison.OrdinalIgnoreCase))
                return true;

            // Und unsere eigene.
            //
            // Der Inhalt wird nach "X.synct-neu" geholt und erst danach an
            // seinen Platz geschoben. Dazwischen meldet der Beobachter eine
            // neue Datei; sie kam in den Bestand, wurde angekuendigt, und das
            // Verschieben galt anschliessend als Loeschung. Elf davon
            // erschienen bei jedem Durchgang aufs Neue als Rueckstand, obwohl
            // sie nur der Weg zu den elf richtigen Dateien waren.
            if (teil.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Traegt der Name diesen Dateinamen, gleich in welcher Ebene?</summary>
    private static bool Heisst(string name, string dateiname)
    {
        var schnitt = name.LastIndexOf('/');
        var letzter = schnitt < 0 ? name : name[(schnitt + 1)..];
        return letzter.Equals(dateiname, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Unterhalb(string name, string ordner)
        => name.Equals(ordner, StringComparison.OrdinalIgnoreCase)
           || name.StartsWith(ordner + "/", StringComparison.OrdinalIgnoreCase);

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
            if (!_config.Includes(name) || IsHousekeeping(name)) continue;
            if (_config.IsIgnored(name)) continue;
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
        var zurueckgestellt = 0;

        foreach (var name in _incoming.Keys.ToArray())
        {
            // Erst melden, dann annehmen.
            //
            // Steht fuer diesen Namen bei uns eine Loeschung aus, ist der
            // Stand der Gegenstelle der aeltere -- sie hat von der Loeschung
            // noch nicht erfahren. Wird er trotzdem angewandt, entsteht die
            // Datei hier neu, gilt damit als "wieder da", und die Loeschung
            // faellt weg, ohne je gesendet worden zu sein. Gemessen: zweimal
            // geloescht, zweimal binnen einer Sekunde wieder angelegt, beide
            // Gegenstellen unveraendert.
            //
            // Der Name bleibt in der Schlange. Sobald die Loeschung heraus
            // ist -- oder eine der Sicherungen in Deletions() sie verwirft --
            // steht er nicht mehr in _removed und wird im naechsten Durchgang
            // angewandt. Verloren geht dabei nichts.
            if (_removed.ContainsKey(name))
            {
                zurueckgestellt++;
                continue;
            }

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

        if (!bilanz.Leer)
        {
            _log($"[{FolderId}] von der Gegenstelle uebernommen: {bilanz}.");

            // Und einen Durchgang anfordern, sonst zeigt die Anzeige das
            // Gegenteil dessen, was gerade geschehen ist.
            //
            // Der Rueckstand vergleicht den Index der Gegenstelle mit dem
            // Bestand des letzten Durchgangs, und den setzt allein der
            // Durchgang. Eben uebernommen heisst: die Datei liegt mit ihrer
            // neuen Zeit auf der Platte, im Bestand steht aber noch die alte.
            // Gemessen: "Ross.sub (11 MB, hier 03.09. 10:35:26 statt 03.09.
            // 12:41:59)" -- eine Sekunde nachdem genau diese Datei
            // hereingeholt worden war.
            //
            // Nur wenn wirklich etwas hergestellt wurde. Ein Durchgang je
            // Takt waere zu teuer; ein Durchgang je Uebernahme ist es nicht,
            // denn er liest bloss Attribute, und uebernommen wird selten.
            _lastScan = DateTime.MinValue;
        }

        // Einmal je Durchgang, nicht je Name: haengt eine Loeschung fest,
        // weil die Gegenstelle gerade nicht erreichbar ist, waere jede Zeile
        // dieselbe.
        if (zurueckgestellt > 0)
            _log($"[{FolderId}] {zurueckgestellt} Eintraege der Gegenstelle zurueckgestellt, " +
                 "bis unsere Loeschung dazu heraus ist.");
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

        // Ein Muster kann nach dem Index dazugekommen sein. Angelegt wird
        // dann nichts mehr; was schon dasteht, raeumt der Durchgang weg.
        if (_config.IsIgnored(name)) return;

        var path = LocalPathOf(name);

        // Smart-Datenbankmodus, die andere Richtung.
        //
        // Nur nicht zu senden genuegt nicht. Bliebe der eingehende Weg offen,
        // haette die Gegenstelle immer die juengere Fassung -- und jede ihrer
        // Aenderungen legte unsere lokale Datei als Konfliktkopie zur Seite,
        // waehrend ein Programm sie offen haelt. Genau das ist hier passiert:
        // an einem Tag 198 Konfliktkopien im Profil eines laufenden Browsers.
        //
        // Solange also das Journal neben unserer Datei nicht leer ist, wird
        // hier nichts angefasst. Der naechste Durchgang sieht wieder nach.
        if (_app.SmartDatabaseMode && (Datenbank.IstBeifahrer(name) || Datenbank.Beschaeftigt(path)))
            return;

        // Solange hier geschrieben wird, ist jede Meldung darueber unsere
        // eigene. Ohne diese Sperre kuendigen wir an, was wir gerade von der
        // Gegenstelle uebernommen haben.
        using var hold = HoldHydration(name);

        // Eine Aenderung, die noch nicht angekuendigt ist, steht in keinem
        // Versionsvektor. Der Vergleich saehe unsere alte Fassung und gaebe
        // der Gegenstelle recht -- und der ungesagte Inhalt waere fort, ohne
        // dass je ein Konflikt gemeldet wurde. Er ist einer: beide Seiten
        // haben geaendert, ohne voneinander zu wissen.
        var ungesagt = mine is not null && !mine.Deleted && NochNichtGesagt(name);

        if (ungesagt && !theirs.Deleted)
        {
            bilanz.Konflikte++;
            if (!ResolveConflict(name, path, mine!, theirs)) return;
        }
        else if (mine is not null && !mine.Deleted)
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

        // Was schon so dasteht, wird nicht angefasst -- gleiche Groesse,
        // gleiche Sekunde, fertig.
        //
        // Ob es ein Platzhalter ist oder eine gewoehnliche Datei, spielt dabei
        // keine Rolle. Frueher stand hier die Bedingung "und ist ein
        // Platzhalter", und damit traf sie auf einen Ordner, der schon
        // dieselben Daten enthielt, nie zu: jede Datei wurde geloescht, durch
        // einen Platzhalter ersetzt und danach wieder heruntergeladen. Wer ein
        // gewachsenes Verzeichnis uebernimmt, laedt so seinen eigenen Bestand
        // ein zweites Mal aus dem Netz.
        //
        // Es ist dieselbe Heuristik, nach der auch der Durchgang ueber den
        // Ordner entscheidet: Groesse und Sekunde. Der Beweis waere ein Hash
        // ueber alles, und den kostet keine der beiden Stellen.
        if (info.Exists
            && info.Length == theirs.Size
            && new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() == theirs.ModifiedS)
        {
            // Der Eintrag gehoert ab jetzt der Gegenstelle. Ohne diesen
            // Schnitt gilt die vorhandene Datei als eigene Aenderung und wird
            // angekuendigt -- eine Fassung, die dieselben Bytes traegt, aber
            // einen anderen Versionsvektor.
            lock (_indexGate) _index!.ForgetLocal(name);
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
            if (!NimmFort(name, path)) return;
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
            if (!NimmFort(name, path)) return;
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
    /// <summary>
    /// Legt die Ablage an und setzt sie auf versteckt.
    /// </summary>
    /// <remarks>
    /// Syncthing haelt es bei seinen eigenen Verwaltungsordnern genauso, und
    /// aus demselben Grund: sie gehoeren nicht zum Inhalt der Freigabe. Wer
    /// den Ordner oeffnet, will seine Dateien sehen und nicht den Keller, in
    /// dem die ersetzten Fassungen liegen.
    ///
    /// Gesetzt wird beim Anlegen und beim Start -- nicht laufend. Ein
    /// Ordner, der vor dieser Regel entstanden ist, bekommt das Attribut
    /// sonst nie, und genau das war der haeufige Fall.
    ///
    /// Wer es zwischen zwei Starts entfernt, behaelt seine Entscheidung bis
    /// zum naechsten Start. Sie bei jedem Durchgang zu ueberschreiben waere
    /// eine Bevormundung; sie einmal je Sitzung zu setzen ist die Vorgabe.
    /// </remarks>
    internal void VersteckeWurzel()
    {
        var wurzel = LocalPathOf(VersionsFolder);

        try
        {
            var info = Directory.Exists(wurzel)
                ? new DirectoryInfo(wurzel)
                : Directory.CreateDirectory(wurzel);

            if ((info.Attributes & FileAttributes.Hidden) == 0)
                info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex)
        {
            // Die Ablage selbst legt der naechste Aufruf an. Ohne das
            // Attribut ist sie sichtbar, aber sie tut, was sie soll.
            _log($"[{FolderId}] \"{VersionsFolder}\" liess sich nicht verstecken: {ex.Message}");
        }
    }

    /// <summary>
    /// Nimmt den bisherigen Inhalt fort, und zwar nur mit Ablage.
    /// </summary>
    /// <remarks>
    /// Ein Platzhalter haelt keinen Inhalt, da gibt es nichts abzulegen; und
    /// ist der Papierkorb abgeschaltet, ist das Loeschen die eingestellte
    /// Absicht. In jedem anderen Fall gilt: gelingt die Ablage nicht, bleibt
    /// die Datei stehen.
    ///
    /// Vorher wurde sie auch dann geloescht. Der Papierkorb war eingeschaltet,
    /// im Protokoll stand "liess sich nicht sichern", und der Inhalt war
    /// trotzdem fort -- in genau dem Fall, fuer den es ihn gibt. Erreichbar
    /// war das unter anderem ueber die Pfadlaenge: die Ablage haengt an den
    /// Namen ".stversions/" und einen Zeitstempel, gut dreissig Zeichen, und
    /// das Verschieben scheiterte, waehrend das Loeschen auf den kuerzeren
    /// urspruenglichen Pfad gelang.
    ///
    /// Der Rueckgabewert sagt, ob der Aufrufer fortfahren darf. Bei "nein"
    /// steht der Name wieder in der Warteschlange, und der naechste Takt
    /// versucht es erneut.
    /// </remarks>
    private bool NimmFort(string name, string path)
    {
        if (IsPlaceholder(path) || !_config.KeepVersions)
        {
            File.Delete(path);
            return true;
        }

        if (KeepVersion(name, path)) return true;

        _incoming[name] = 0;

        // Je Name einmal. Der Grund aendert sich nicht, und der Versuch
        // laeuft in jedem Takt.
        if (_warned.TryAdd("sichern:" + name, 0))
            _log($"[{FolderId}] \"{name}\" bleibt unveraendert stehen: die bisherige Fassung " +
                 "liess sich nicht im Papierkorb ablegen, und ohne Ablage wird nichts entfernt.");

        return false;
    }

    /// <summary>
    /// Der Pfad in der Form, die Windows ohne Laengengrenze annimmt.
    /// </summary>
    /// <remarks>
    /// Ohne dieses Praefix gilt MAX_PATH von 260 Zeichen. Die Ablage
    /// verlaengert jeden Namen um gut dreissig Zeichen und trifft die Grenze
    /// deshalb frueher als der Ordner selbst.
    /// </remarks>
    private static string OhneLaengengrenze(string pfad)
        => pfad.StartsWith(@"\\", StringComparison.Ordinal) ? pfad : @"\\?\" + pfad;

    private bool KeepVersion(string name, string path)
    {
        if (!_config.KeepVersions) return false;

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path).ToString("yyyyMMdd-HHmmss");
            var extension = Path.GetExtension(name);
            var stem = name[..^extension.Length];

            var target = OhneLaengengrenze(LocalPathOf($"{VersionsFolder}/{stem}~{stamp}{extension}"));
            VersteckeWurzel();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Zweimal dieselbe Sekunde ist moeglich. Dann bekommt die zweite
            // Ablage eine laufende Nummer.
            //
            // Vorher wurde in diesem Fall die Datei geloescht, statt sie zu
            // sichern -- in einer Methode, deren einziger Zweck das Sichern
            // ist. Zwei Aenderungen innerhalb einer Sekunde sind selten, und
            // genau deshalb faellt so etwas nie auf.
            for (var nummer = 1; File.Exists(target) && nummer < 1000; nummer++)
                target = OhneLaengengrenze(
                    LocalPathOf($"{VersionsFolder}/{stem}~{stamp}-{nummer}{extension}"));

            if (File.Exists(target))
            {
                _log($"[{FolderId}] \"{name}\" liess sich nicht sichern: " +
                     "zu viele Ablagen aus derselben Sekunde.");
                return false;
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
