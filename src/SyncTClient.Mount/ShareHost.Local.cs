using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf;
using SyncTClient.Bep;
using BlockInfo = SyncTClient.Bep.Proto.BlockInfo;
using Counter = SyncTClient.Bep.Proto.Counter;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;
using FileInfoType = SyncTClient.Bep.Proto.FileInfoType;
using BepIndex = SyncTClient.Bep.Proto.Index;
using BepIndexUpdate = SyncTClient.Bep.Proto.IndexUpdate;
using Vector = SyncTClient.Bep.Proto.Vector;

namespace SyncTClient.Mount;

/// <summary>
/// Die andere Richtung: was hier geaendert wird, geht an die Gegenstelle.
/// </summary>
/// <remarks>
/// Bisher hat dieser Teil des Programms nur gelesen. Er nahm den Index
/// entgegen, legte Platzhalter an und holte Bloecke. Ab hier schreibt er auch:
/// eine geaenderte Datei bekommt eine neue Fassungsnummer und wird
/// angekuendigt.
///
/// Der Ablauf ist in drei Schritte zerlegt, weil die Rueckrufe von Windows
/// sofort zurueckkehren muessen. <see cref="NoteLocalChange"/> vermerkt nur.
/// Ein Hintergrundlauf bewertet den Vermerk und entscheidet, ob ueberhaupt
/// etwas geschehen soll. Erst der dritte Schritt sendet.
///
/// Die wichtigste Vorkehrung sitzt im zweiten Schritt: der Vergleich des
/// gerechneten blocks_hash mit dem gespeicherten. Ohne ihn erzeugt jede eigene
/// Hydration, jede Attributaenderung und jede zurueckgespiegelte Ankuendigung
/// eine neue Fassung, die Gegenstelle spiegelt sie zurueck, und beide Seiten
/// laufen mit voller Bandbreite im Kreis.
/// </remarks>
public sealed partial class ShareHost
{
    /// <summary>Der Inhalt stimmt mit dem ueberein, was zuletzt angekuendigt wurde.</summary>
    private const int StateClean = 0;

    /// <summary>Angekuendigt.</summary>
    /// <remarks>
    /// Zustand 1 der Tabelle, "geaendert und noch nicht angekuendigt", kommt
    /// hier nicht vor. Die Liste der zu pruefenden Namen steht im
    /// Arbeitsspeicher; in die Datenbank kommt ein Eintrag erst, wenn seine
    /// Ankuendigung feststeht.
    /// </remarks>
    private const int StateAnnounced = 2;

    /// <summary>Hoechstens so viele Dateien stehen in einer Nachricht.</summary>
    /// <remarks>
    /// Eine FileInfo traegt ihre gesamte Blockliste: 32 Bytes je Block, bei
    /// einer 250-MB-Datei also rund 64 KB. Eine feste Zahl allein reicht als
    /// Schranke deshalb nicht, es braucht zusaetzlich
    /// <see cref="BatchBytes"/>. Umgekehrt waeren bei lauter kleinen Dateien
    /// tausende Eintraege in einer Nachricht moeglich; die Zahl haelt die
    /// Nachricht auch dann ueberschaubar.
    /// </remarks>
    private const int BatchFiles = 256;

    /// <summary>Ab dieser Groesse wird die Nachricht abgeschickt.</summary>
    private const int BatchBytes = 512 << 10;

    /// <summary>
    /// Mehr Loeschungen in einem Durchgang werden nicht gesendet.
    /// </summary>
    /// <remarks>
    /// Eine grosse Zahl auf einmal ist fast nie eine Loeschung. Sie entsteht,
    /// wenn ein Laufwerk fehlt, ein Ordner umbenannt wurde oder die Freigabe
    /// nicht eingehaengt ist. Der Schaden waere in diesem Fall nicht auf
    /// diesen Rechner beschraenkt: die Gegenstelle wuerde die Loeschungen
    /// uebernehmen.
    /// </remarks>
    private const int MaximumDeletions = 100;

    /// <summary>So lange bleibt ein Vermerk liegen, bevor er bewertet wird.</summary>
    /// <remarks>
    /// Ein Speichervorgang schreibt oft mehrmals hintereinander. Wer sofort
    /// rechnet, rechnet die Blockliste eines halb geschriebenen Standes und
    /// kuendigt sie an.
    /// </remarks>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    /// <summary>So oft wird auch ohne Vermerk nachgesehen.</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// So lange nach dem Ende einer Hydration gelten Meldungen zu dieser Datei
    /// noch als eigenes Werk.
    /// </summary>
    /// <remarks>
    /// Der Rueckruf von Windows kommt nicht zwingend, solange der Schreibvorgang
    /// laeuft. Er kann kurz danach eintreffen.
    /// </remarks>
    private const long HydrationEchoMs = 5_000;

    /// <summary>So oft wird eine Datei erneut versucht, die sich nicht lesen liess.</summary>
    private const int MaximumAttempts = 5;

    /// <summary>Abstand zwischen zwei Durchgaengen ueber den Ordner.</summary>
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(60);

    private DateTime _lastScan = DateTime.UtcNow;

    private FileSystemWatcher? _watcher;

    /// <summary>Namen, die zu pruefen sind.</summary>
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.Ordinal);

    /// <summary>Namen, zu denen eine Loeschung gemeldet wurde.</summary>
    private readonly ConcurrentDictionary<string, byte> _removed = new(StringComparer.Ordinal);

    /// <summary>Dateien, in die dieser Client gerade selbst schreibt.</summary>
    private readonly ConcurrentDictionary<string, Hydration> _hydrating = new(StringComparer.Ordinal);

    /// <summary>Fehlversuche je Name.</summary>
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    /// <summary>Namen, zu denen bereits eine Meldung im Protokoll steht.</summary>
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    /// <summary>
    /// Serialisiert die Zugriffe auf die Datenbank.
    /// </summary>
    /// <remarks>
    /// Der Index haengt an einer einzigen SQLite-Verbindung, und die vertraegt
    /// keine gleichzeitigen Schreibvorgaenge. Bisher schrieb nur die
    /// Leseschleife ueber <see cref="Absorb"/>. Der Hintergrundlauf ist der
    /// zweite Schreiber.
    /// </remarks>
    private readonly object _indexGate = new();

    private readonly SemaphoreSlim _localWork = new(0);
    private int _pendingWake;

    private CancellationTokenSource? _localCts;
    private Task? _localLoop;

    /// <summary>Ob in dieser Sitzung schon ein Index fuer diesen Ordner ging.</summary>
    private bool _indexSent;

    /// <summary>Die hoechste Sequenznummer der zuletzt gesendeten Nachricht.</summary>
    private long _lastSentSequence;

    /// <summary>
    /// Die eigene Geraete-ID. Sie steht in <c>modified_by</c> und im eigenen
    /// Zaehler des Versionsvektors.
    /// </summary>
    /// <remarks>
    /// Ohne sie laesst sich keine Fassung fortschreiben. Ist sie leer, wird
    /// nichts angekuendigt. Gesetzt wird sie vom <see cref="PeerHost"/>, der
    /// das Geraetezertifikat haelt.
    /// </remarks>
    public Bep.DeviceId OwnDeviceId { get; set; } = Bep.DeviceId.Empty;

    // ------------------------------------------------------------ Vermerken

    /// <summary>
    /// Vermerkt, dass eine Datei sich geaendert haben koennte.
    /// </summary>
    /// <remarks>
    /// Aufrufer sind die Rueckrufe des Dateisystems. Sie duerfen nicht warten,
    /// deshalb geschieht hier nichts weiter als der Eintrag in eine Liste. Ob
    /// die Datei sich wirklich geaendert hat, stellt der Hintergrundlauf fest.
    /// Ein Vermerk zu viel kostet eine Blocklistenrechnung, ein Vermerk zu
    /// wenig kostet eine nicht uebertragene Aenderung.
    /// </remarks>
    public void NoteLocalChange(string relativePath)
    {
        if (NameOf(relativePath) is not { } name) return;

        // Was wir selbst gerade schreiben, ist keine Aenderung von aussen.
        if (IsHydrating(name)) return;

        _removed.TryRemove(name, out _);
        _dirty[name] = 0;
        Wake();
    }

    /// <summary>
    /// Vermerkt, dass eine Datei geloescht wurde.
    /// </summary>
    /// <remarks>
    /// Nur dieser Aufruf und der Scanbefund fuehren zu einer Loeschmeldung.
    /// Aus der blossen Abwesenheit einer Datei wird nie auf eine Loeschung
    /// geschlossen: eine fehlende Datei kann verschoben, umbenannt oder von
    /// einem nicht eingehaengten Laufwerk sein.
    /// </remarks>
    public void NoteLocalDelete(string relativePath)
    {
        if (NameOf(relativePath) is not { } name) return;

        _dirty.TryRemove(name, out _);
        _removed[name] = 0;
        Wake();
    }

    /// <summary>Weckt den Hintergrundlauf, ohne den Zaehler hochlaufen zu lassen.</summary>
    private void Wake()
    {
        if (Interlocked.Exchange(ref _pendingWake, 1) == 0) _localWork.Release();
    }

    /// <summary>
    /// Macht aus einem Pfad den Namen, unter dem das Protokoll die Datei
    /// fuehrt: relativ zur Freigabe, mit / als Trenner.
    /// </summary>
    /// <remarks>
    /// Angenommen wird beides, ein vollstaendiger lokaler Pfad und ein bereits
    /// relativer Name. Der Rueckruf des Dateisystems nennt den einen, der
    /// Scanbefund den anderen.
    /// </remarks>
    private string? NameOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return null;

        var text = path;

        if (Path.IsPathRooted(text))
        {
            if (!Owns(text)) return null;
            text = RelativeOf(text);
        }

        var name = text.Replace('\\', '/').Trim('/');
        if (name.Length == 0) return null;

        // Die eigene Sicherung ist kein Teil der Freigabe. Sie wird weder
        // angekuendigt noch beim Durchgang betrachtet.
        if (IsVersionsPath(name)) return null;

        foreach (var part in name.Split('/'))
            if (part.Length == 0 || part == "." || part == "..")
                return null;

        return name;
    }

    // ------------------------------------------------------------ Unterdrueckung

    /// <summary>Eine Datei, in die dieser Client gerade schreibt.</summary>
    private sealed class Hydration
    {
        public int Depth;
        public long ReleasedTicks;
        public bool Dropped;
    }

    /// <summary>
    /// Haelt eine Datei fuer die Dauer einer Hydration von der Erkennung fern.
    /// </summary>
    /// <remarks>
    /// Die Hydration schreibt in genau die Datei, deren Aenderung wir sonst
    /// bemerken wuerden. Ohne diese Sperre kuendigen wir an, was wir gerade
    /// selbst empfangen haben. Der Vergleich der Blocklisten faengt denselben
    /// Fall ein zweites Mal ab; beide werden gebraucht, denn der Vergleich
    /// kostet eine vollstaendige Rechnung ueber die Datei.
    /// </remarks>
    private IDisposable HoldHydration(string relativePath)
    {
        var name = NameOf(relativePath);
        if (name is null) return NoHold.Instance;

        while (true)
        {
            var mark = _hydrating.GetOrAdd(name, _ => new Hydration());
            lock (mark)
            {
                // Zwischen GetOrAdd und dem Sperren kann der Eintrag aus der
                // Liste genommen worden sein. Dann gilt er nicht mehr.
                if (mark.Dropped) continue;

                mark.Depth++;
                return new Hold(mark);
            }
        }
    }

    private sealed class Hold(Hydration mark) : IDisposable
    {
        public void Dispose()
        {
            lock (mark)
            {
                mark.Depth--;
                mark.ReleasedTicks = Environment.TickCount64;
            }
        }
    }

    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();
        public void Dispose() { }
    }

    private bool IsHydrating(string name)
    {
        if (!_hydrating.TryGetValue(name, out var mark)) return false;

        lock (mark)
            return mark.Depth > 0 || Environment.TickCount64 - mark.ReleasedTicks < HydrationEchoMs;
    }

    /// <summary>Raeumt die Sperren fort, deren Nachhall abgelaufen ist.</summary>
    private void SweepHydrations()
    {
        foreach (var (name, mark) in _hydrating)
        {
            lock (mark)
            {
                if (mark.Depth > 0 || Environment.TickCount64 - mark.ReleasedTicks < HydrationEchoMs)
                    continue;

                mark.Dropped = true;
            }

            _hydrating.TryRemove(name, out _);
        }
    }

    // ------------------------------------------------------------ Durchgang beim Start

    /// <summary>
    /// Vergleicht den Bestand auf der Platte mit dem eigenen Index.
    /// </summary>
    /// <remarks>
    /// Gelesen werden je Datei nur Attribute, Groesse und Zeit. Der Inhalt
    /// bleibt ungelesen, denn ein Lesezugriff auf einen Platzhalter wuerde die
    /// Datei ueber genau die Verbindung holen, an der wir gerade nichts wollen.
    ///
    /// Der Durchgang stellt nur fest, was zu pruefen ist. Ob eine Datei sich
    /// wirklich geaendert hat, entscheidet erst die Blockliste.
    /// </remarks>
    private void ScanLocal(bool quiet = false)
    {
        var root = _config.LocalPath;
        if (!Directory.Exists(root)) return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,

            // Die Vorgabe uebergeht versteckte und System-Dateien. In einer
            // Freigabe gehoeren sie dazu.
            AttributesToSkip = 0
        };

        var found = 0;

        // Was im Ordner steht, mit Groesse und Zeit. Platzhalter gehoeren
        // dazu: fuer den Rueckstand zaehlt, ob der Eintrag da ist und zum
        // Index passt, nicht ob sein Inhalt lokal liegt.
        var vorhanden = new Dictionary<string, (long Size, long ModifiedS)>(StringComparer.Ordinal);

        try
        {
            foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                if (NameOf(info.FullName) is not { } name) continue;
                if (!_config.Includes(name)) continue;

                vorhanden[name] = (
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds());

                // Ein Platzhalter ist nicht vollstaendig hier. Angekuendigt
                // wird nur, was wir ganz haben.
                if (((uint)info.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0) continue;

                var known = LocalCopy(name);
                if (known is not null && !known.Deleted &&
                    known.Size == info.Length &&
                    known.ModifiedS == new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())
                    continue;

                _dirty[name] = 0;
                found++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"[{FolderId}] der Durchgang ueber \"{root}\" brach ab: {ex.Message}");
            return;
        }

        LastScan = DateTime.Now;
        MeasureOutstanding(vorhanden);

        if (found == 0) return;

        if (found > 0 && !quiet) _log($"[{FolderId}] {found} lokale Dateien sind zu pruefen.");
        if (found > 0 && quiet) _log($"[{FolderId}] {found} neue oder geaenderte Dateien gefunden.");
        Wake();
    }

    /// <summary>
    /// Zaehlt, was die Gegenstelle fuehrt und hier noch nicht so dasteht.
    /// </summary>
    /// <remarks>
    /// Dieselbe Bedingung, nach der auch uebernommen wird: eine Datei gilt als
    /// abgeglichen, wenn sie da ist und Groesse und Zeit zum Index passen. Ob
    /// ihr Inhalt lokal liegt, spielt keine Rolle -- ein Platzhalter ist der
    /// erwuenschte Zustand und kein Rueckstand.
    ///
    /// Gerechnet wird auf dem Verzeichnis, das der Durchgang ohnehin gelesen
    /// hat. Ein eigener Lauf ueber den Index mit einem Zugriff je Datei waere
    /// dieselbe Auskunft zum doppelten Preis.
    /// </remarks>
    private void MeasureOutstanding(Dictionary<string, (long Size, long ModifiedS)> vorhanden)
    {
        var offen = 0;
        long bytes = 0;

        try
        {
            lock (_indexGate)
            {
                if (_index is null) return;

                foreach (var (name, size, modifiedS, isDirectory) in _index.EnumerateLight())
                {
                    if (isDirectory || !_config.Includes(name)) continue;
                    if (vorhanden.TryGetValue(name, out var da)
                        && da.Size == size && da.ModifiedS == modifiedS) continue;

                    offen++;
                    bytes += size;
                }
            }
        }
        catch (Exception)
        {
            // Eine Zahl fuer die Anzeige. Sie ist es nicht wert, den
            // Hintergrundlauf abzubrechen.
            return;
        }

        Outstanding = offen;
        OutstandingBytes = bytes;
        UpdateOutstandingPhase();
    }

    // ------------------------------------------------------------ Hintergrundlauf

    /// <summary>
    /// Beobachtet den Ordner und meldet Aenderungen sofort.
    /// </summary>
    /// <remarks>
    /// Die Rueckrufe der Cloud-Files-Schicht kommen nur fuer Platzhalter. Eine
    /// Datei, die jemand in den Ordner kopiert, war nie einer, und fuer sie
    /// kommt keine Meldung. Der Beobachter schliesst diese Luecke.
    ///
    /// Er ersetzt den regelmaessigen Durchgang nicht. Sein Puffer laeuft bei
    /// vielen Aenderungen auf einmal ueber, und dann gehen Ereignisse
    /// ersatzlos verloren. Syncthing und der Nextcloud-Client halten es
    /// ebenso: Beobachter fuer die Geschwindigkeit, Durchgang fuer die
    /// Vollstaendigkeit. Bei einem Ueberlauf wird der naechste Durchgang
    /// vorgezogen.
    /// </remarks>
    private void StartWatching()
    {
        if (!Directory.Exists(_config.LocalPath)) return;

        try
        {
            var watcher = new FileSystemWatcher(_config.LocalPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,

                // Der Standardpuffer fasst wenige Dutzend Ereignisse. Beim
                // Kopieren eines Ordners ist er sofort voll.
                InternalBufferSize = 64 * 1024
            };

            watcher.Created += (_, e) => NoteLocalChange(e.FullPath);
            watcher.Changed += (_, e) => NoteLocalChange(e.FullPath);
            watcher.Deleted += (_, e) => NoteLocalDelete(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                NoteLocalDelete(e.OldFullPath);
                NoteLocalChange(e.FullPath);
            };

            watcher.Error += (_, e) =>
            {
                _log($"[{FolderId}] Beobachter hat Ereignisse verloren ({e.GetException().Message}). " +
                     "Der Ordner wird vollstaendig durchgesehen.");

                _lastScan = DateTime.MinValue;
                Wake();
            };

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Ohne Beobachter bleibt der regelmaessige Durchgang.
            _log($"[{FolderId}] kein Beobachter fuer {_config.LocalPath}: {ex.Message}");
        }
    }

    private void StopWatching()
    {
        var watcher = _watcher;
        _watcher = null;

        if (watcher is null) return;

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private void StartLocalLoop()
    {
        StartWatching();

        if (_localLoop is not null) return;

        _localCts = new CancellationTokenSource();
        var token = _localCts.Token;
        _localLoop = Task.Run(() => RunLocalAsync(token), CancellationToken.None);
    }

    private async Task StopLocalLoopAsync()
    {
        StopWatching();

        if (_localCts is null) return;

        await _localCts.CancelAsync();

        try
        {
            if (_localLoop is not null) await _localLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Erwartet.
        }

        _localLoop = null;
        _localCts.Dispose();
        _localCts = null;
    }

    private async Task RunLocalAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _localWork.WaitAsync(IdleInterval, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _pendingWake, 0);

                SweepHydrations();

                // Erst hereinnehmen, dann hinausgeben. Andernfalls wuerde eine
                // Datei, die beide Seiten geaendert haben, als eigene Aenderung
                // angekuendigt, bevor der Konflikt bemerkt ist.
                ApplyIncoming();
                SettlePhase();
                SweepVersions();

                // Die Meldungen der Cloud-Files-Schicht sind der schnelle Weg,
                // aber nicht der einzige. Fuer eine Datei, die neu in den
                // Ordner kopiert wurde, kommt moeglicherweise keine Meldung:
                // sie war nie ein Platzhalter. Deshalb wird in Abstaenden
                // nachgesehen. Der Durchgang liest nur Attribute, Groesse und
                // Zeit, keine Inhalte.
                if (DateTime.UtcNow - _lastScan >= RescanInterval)
                {
                    _lastScan = DateTime.UtcNow;
                    ScanLocal(quiet: true);
                }

                if (_dirty.IsEmpty && _removed.IsEmpty) continue;

                await Task.Delay(SettleDelay, ct).ConfigureAwait(false);

                try
                {
                    await PublishAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"[{FolderId}] lokale Aenderungen: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Der Ordner wird angehalten.
        }
    }

    /// <summary>
    /// Bewertet die Vermerke und schickt, was uebrig bleibt.
    /// </summary>
    private async Task PublishAsync(CancellationToken ct)
    {
        if (_connection is not { } connection || _index is null) return;
        if (IsPaused) return;

        // Ohne eigene Geraete-ID liesse sich kein Zaehler fortschreiben. Eine
        // Ankuendigung ohne eigenen Zaehler waere fuer die Gegenstelle
        // dieselbe Fassung wie zuvor.
        if (OwnDeviceId == Bep.DeviceId.Empty) return;

        var batch = new List<BepFileInfo>();
        var bytes = 0;

        foreach (var name in _dirty.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            // Waehrend der Sammelfrist kann eine Hydration begonnen haben.
            if (IsHydrating(name)) continue;

            _dirty.TryRemove(name, out _);

            if (Evaluate(name) is not { } file) continue;

            batch.Add(file);
            bytes += file.CalculateSize();

            if (batch.Count < BatchFiles && bytes < BatchBytes) continue;

            await FlushAsync(connection, batch, ct).ConfigureAwait(false);
            bytes = 0;
        }

        foreach (var file in Deletions())
        {
            batch.Add(file);
            bytes += file.CalculateSize();

            if (batch.Count < BatchFiles && bytes < BatchBytes) continue;

            await FlushAsync(connection, batch, ct).ConfigureAwait(false);
            bytes = 0;
        }

        await FlushAsync(connection, batch, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ Bewerten

    /// <summary>
    /// Stellt fest, ob zu diesem Namen etwas anzukuendigen ist, und baut die
    /// Ankuendigung.
    /// </summary>
    /// <remarks>
    /// Liefert <c>null</c>, wenn nichts zu tun ist. Das ist der haeufige Fall:
    /// ein Platzhalter, eine unveraenderte Datei, ein ausgeschlossener Zweig.
    /// </remarks>
    private BepFileInfo? Evaluate(string name)
    {
        if (!_config.Includes(name)) return Done(name);

        var path = ResolveInside(name);
        if (path is null) return Done(name);

        var info = new System.IO.FileInfo(path);

        // Eine fehlende Datei ist keine Loeschung. Sie kann verschoben oder
        // umbenannt worden sein, und der Zweig kann von einem Laufwerk
        // stammen, das gerade nicht da ist.
        if (!info.Exists) return Done(name);
        if ((info.Attributes & FileAttributes.Directory) != 0) return Done(name);

        // Ein Platzhalter hat den Inhalt nicht. Wir kuendigen nur an, was
        // vollstaendig hier liegt.
        if (((uint)info.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0) return Done(name);

        if (AnnouncedName(name) is not { } announced) return Done(name);

        var length = info.Length;
        var modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

        // Nach einem gewonnenen Konflikt muss die Datei hinaus, obwohl sich an
        // ihr nichts geaendert hat. Geaendert hat sich, was die Gegenstelle
        // von ihr weiss.
        var erzwungen = _force.TryRemove(name, out _);

        // Groesse und Zeit zuerst. Windows meldet jedes geschlossene Handle,
        // auch nach reinem Lesen. Ohne diesen Vergleich wuerde jede geoeffnete
        // Datei vollstaendig neu gehasht, bei einer grossen Datei ueber
        // hundert Megabyte Leserei fuer ein Ergebnis, das feststeht.
        //
        // Der Vergleich ist eine Heuristik. Wer Groesse und Sekunde
        // beibehaelt und den Inhalt aendert, wird hier uebersehen -- der
        // Durchgang beim Start faende es ebenfalls nicht. Der Beweis waere
        // ein Hash ueber alles, und den kostet er nicht.
        if (!erzwungen
            && _index!.TryGetLocal(name, out var previous)
            && !previous.Deleted
            && previous.Size == length
            && previous.ModifiedS == modified)
        {
            return Done(name);
        }

        int blockSize;
        IReadOnlyList<BlockInfo> blocks;
        byte[] blocksHash;

        try
        {
            using var content = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan);

            // Zwischen der Pruefung oben und dem Lesen kann die Datei
            // verdraengt worden sein. Das Oeffnen allein holt sie noch nicht,
            // das Lesen wuerde es.
            if (IsPlaceholder(path)) return Done(name);

            (blockSize, blocks, blocksHash) = BlockList.For(content, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Eine Datei, die gerade geschrieben wird, laesst sich oft nicht
            // in einem Zug lesen. Sie kommt zurueck in die Liste.
            Retry(name, ex);
            return null;
        }

        var known = LocalCopy(announced);

        // Die wichtigste Pruefung. Gleicher Inhalt heisst: keine neue Fassung,
        // keine Ankuendigung. Ohne sie erzeugt jede eigene Hydration und jede
        // zurueckgespiegelte Ankuendigung eine weitere Runde.
        //
        // Der Zustand des Eintrags bleibt dabei, wie er ist. "Angekuendigt und
        // noch nicht bestaetigt" endet erst mit der Bestaetigung durch die
        // Gegenstelle, nicht damit, dass die Datei sich seither nicht
        // geaendert hat.
        if (known is not null && !known.Deleted && known.BlocksHash.Span.SequenceEqual(blocksHash))
            return Done(name);

        // Zum ersten Mal gesehen, und der Inhalt ist genau der, den die
        // Gegenstelle angekuendigt hat: die Datei ist von dort gekommen. Sie
        // wird in den eigenen Bestand uebernommen, aber nicht angekuendigt.
        if (known is null && PeerCopy(announced) is { } peer &&
            !peer.Deleted && peer.Size == length && peer.BlocksHash.Span.SequenceEqual(blocksHash))
        {
            var adopted = peer.Clone();

            // Die Sequenznummer der Gegenstelle gehoert nicht in die eigene
            // Zaehlung. Angekuendigt haben wir diese Fassung nie.
            adopted.Sequence = 0;
            Store(adopted, StateClean);
            return Done(name);
        }

        var modifiedUtc = info.LastWriteTimeUtc;

        var file = new BepFileInfo
        {
            Name = announced,
            Type = FileInfoType.File,
            Size = length,

            // Windows kennt keinen Unix-Modus. Ohne no_permissions waere die
            // Ankuendigung die Behauptung, die Datei habe den Modus 0000.
            Permissions = 0,
            NoPermissions = true,

            ModifiedS = new DateTimeOffset(modifiedUtc).ToUnixTimeSeconds(),
            ModifiedNs = (int)((modifiedUtc.Ticks - DateTime.UnixEpoch.Ticks) % TimeSpan.TicksPerSecond * 100),
            Deleted = false,
            Invalid = false,
            Sequence = NextSequence(),
            ModifiedBy = OwnDeviceId.ShortId(),
            Version = NextVersion(known?.Version ?? PeerCopy(announced)?.Version),
            BlockSize = blockSize,
            BlocksHash = ByteString.CopyFrom(blocksHash)

            // local_flags bleibt ungesetzt. bep.proto sagt ausdruecklich, dass
            // das Feld nicht ueber den Draht geht.
        };
        file.Blocks.AddRange(blocks);

        // Erst schreiben, dann senden. Geht das Senden schief, steht der
        // Eintrag trotzdem im eigenen Bestand und die Sequenznummer ist
        // vergeben. Umgekehrt waere die Datei angekuendigt, ohne dass wir sie
        // fuehren, und die naechste Anfrage danach wuerde abgelehnt.
        Store(file, StateAnnounced);

        _attempts.TryRemove(name, out _);
        return file;
    }

    /// <summary>Nichts zu tun: Fehlversuche vergessen und <c>null</c> liefern.</summary>
    private BepFileInfo? Done(string name)
    {
        _attempts.TryRemove(name, out _);
        return null;
    }

    private void Retry(string name, Exception ex)
    {
        var attempts = _attempts.AddOrUpdate(name, 1, (_, count) => count + 1);

        if (attempts <= MaximumAttempts)
        {
            // Ohne Wecken: der naechste Leerlauf greift den Namen auf.
            _dirty[name] = 0;
            return;
        }

        _attempts.TryRemove(name, out _);
        _log($"[{FolderId}] \"{name}\" liess sich nach {MaximumAttempts} Versuchen nicht lesen: {ex.Message}");
    }

    /// <summary>
    /// Schreibt die Fassung fort: nur der eigene Zaehler wird beruehrt.
    /// </summary>
    /// <remarks>
    /// Fremde Zaehler bleiben stehen. Sie sind die Aussage anderer Geraete
    /// darueber, was sie gesehen haben; sie zu aendern hiesse, in ihrem Namen
    /// zu sprechen. Der eigene Wert ist die Unix-Sekunde, mindestens aber der
    /// bisherige Wert plus eins: zwei Aenderungen in derselben Sekunde
    /// muessen unterscheidbar bleiben.
    ///
    /// Die Liste bleibt nach id sortiert. Die Reihenfolge gehoert zum
    /// Vergleich; dieselben Zaehler in anderer Reihenfolge waeren fuer die
    /// Gegenstelle eine andere Fassung.
    /// </remarks>
    private Vector NextVersion(Vector? previous)
    {
        var vector = previous is null ? new Vector() : previous.Clone();
        var mine = OwnDeviceId.ShortId();
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var counter = vector.Counters.FirstOrDefault(c => c.Id == mine);
        if (counter is null)
            vector.Counters.Add(new Counter { Id = mine, Value = now });
        else
            counter.Value = Math.Max(now, counter.Value + 1);

        var sorted = vector.Counters.OrderBy(c => c.Id).ToList();
        vector.Counters.Clear();
        vector.Counters.AddRange(sorted);

        return vector;
    }

    /// <summary>
    /// Der Name, wie er ueber den Draht geht: in NFC.
    /// </summary>
    /// <remarks>
    /// Windows und Linux legen zusammengesetzte Zeichen verschieden ab. Ohne
    /// Normalisierung waere derselbe Dateiname auf beiden Seiten ein anderer
    /// Eintrag. Ist die Normalisierung abgeschaltet (InvariantGlobalization),
    /// bleibt nur reines ASCII: dort ist der Name bereits in NFC.
    /// </remarks>
    private string? AnnouncedName(string name)
    {
        try
        {
            return name.Normalize(NormalizationForm.FormC);
        }
        catch (PlatformNotSupportedException)
        {
            if (Ascii.IsValid(name)) return name;

            if (_warned.TryAdd(name, 0))
                _log($"[{FolderId}] \"{name}\" laesst sich hier nicht NFC-normalisieren -- nicht angekuendigt.");

            return null;
        }
    }

    // ------------------------------------------------------------ Loeschungen

    /// <summary>
    /// Baut die Loeschmeldungen dieses Durchgangs.
    /// </summary>
    /// <remarks>
    /// Vier Sicherungen liegen davor, und jede einzelne verwirft im Zweifel
    /// die Meldung. Eine ausgebliebene Loeschung kostet einen zweiten Anlauf.
    /// Eine erfundene Loeschung nimmt der Gegenstelle die Datei.
    /// </remarks>
    private IReadOnlyList<BepFileInfo> Deletions()
    {
        if (_removed.IsEmpty) return [];

        // Erste Sicherung: ohne Wurzelverzeichnis ist nichts geloescht. Dann
        // fehlt das Laufwerk oder die Freigabe ist nicht eingehaengt.
        if (!Directory.Exists(_config.LocalPath))
        {
            Drop($"\"{_config.LocalPath}\" gibt es nicht");
            return [];
        }

        // Zweite Sicherung: ein leeres Wurzelverzeichnis heisst dasselbe.
        // Niemand loescht eine ganze Freigabe Datei fuer Datei.
        if (!Directory.EnumerateFileSystemEntries(_config.LocalPath).Any())
        {
            Drop($"\"{_config.LocalPath}\" wirkt leer");
            return [];
        }

        var candidates = new List<(string Name, Vector? Version)>();

        foreach (var name in _removed.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            _removed.TryRemove(name, out _);

            // Dritte Sicherung: ausgeschlossene Zweige fehlen mit Absicht.
            // Sie sind nicht geloescht, sie waren nie da.
            if (!_config.Includes(name)) continue;

            if (AnnouncedName(name) is not { } announced) continue;
            if (ResolveInside(name) is not { } path) continue;

            // Wieder da: dann war es keine Loeschung.
            if (File.Exists(path) || Directory.Exists(path)) continue;

            // Das Elternverzeichnis muss vorhanden und lesbar sein. Fehlt es,
            // ist die Abwesenheit der Datei kein Beleg fuer eine Loeschung.
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !Readable(parent)) continue;

            // Was weder wir noch die Gegenstelle fuehren, laesst sich nicht
            // loeschen.
            var known = LocalCopy(announced) ?? PeerCopy(announced);
            if (known is null || known.Deleted) continue;

            candidates.Add((announced, known.Version));
        }

        if (candidates.Count == 0) return [];

        // Vierte Sicherung: eine grosse Zahl auf einmal ist fast nie eine
        // Loeschung.
        if (candidates.Count > MaximumDeletions)
        {
            _log($"[{FolderId}] {candidates.Count} Loeschungen in einem Durchgang -- " +
                 $"mehr als {MaximumDeletions}, deshalb nicht gesendet.");
            return [];
        }

        var files = new List<BepFileInfo>(candidates.Count);

        foreach (var (name, version) in candidates)
        {
            var file = new BepFileInfo
            {
                Name = name,
                Type = FileInfoType.File,
                Size = 0,
                Permissions = 0,
                NoPermissions = true,
                ModifiedS = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ModifiedNs = 0,
                Deleted = true,
                Invalid = false,
                Sequence = NextSequence(),
                ModifiedBy = OwnDeviceId.ShortId(),
                Version = NextVersion(version)

                // Keine Bloecke und kein blocks_hash. Eine geloeschte Datei
                // hat keinen Inhalt.
            };

            Store(file, StateAnnounced);
            files.Add(file);
        }

        _log($"[{FolderId}] {files.Count} Loeschungen werden angekuendigt.");
        return files;
    }

    /// <summary>Verwirft alle gemeldeten Loeschungen ungesendet.</summary>
    private void Drop(string reason)
    {
        var count = _removed.Count;
        _removed.Clear();
        _log($"[{FolderId}] {count} gemeldete Loeschungen verworfen: {reason}.");
    }

    private static bool Readable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;

            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // ------------------------------------------------------------ Senden

    /// <summary>
    /// Schickt einen Stapel und leert ihn.
    /// </summary>
    /// <remarks>
    /// Die erste Nachricht je Ordner und Sitzung ist ein Index, jede weitere
    /// ein IndexUpdate mit <c>prev_sequence</c> der vorigen. Daran erkennt die
    /// Gegenstelle eine Luecke: fehlt ihr eine Nachricht, passt die
    /// Vorgaengernummer nicht zu ihrem Stand.
    /// </remarks>
    private async Task FlushAsync(BepConnection connection, List<BepFileInfo> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        var last = batch.Max(f => f.Sequence);

        if (_indexSent)
        {
            var update = new BepIndexUpdate
            {
                Folder = FolderId,
                LastSequence = last,
                PrevSequence = _lastSentSequence
            };
            update.Files.AddRange(batch);

            await connection.SendIndexUpdateAsync(update, ct).ConfigureAwait(false);
        }
        else
        {
            var index = new BepIndex { Folder = FolderId, LastSequence = last };
            index.Files.AddRange(batch);

            await connection.SendIndexAsync(index, ct).ConfigureAwait(false);
            _indexSent = true;
        }

        _lastSentSequence = last;
        _log($"[{FolderId}] {batch.Count} Aenderungen angekuendigt, Sequenz bis {last}.");
        batch.Clear();
    }

    // ------------------------------------------------------------ Datenbank

    private BepFileInfo? LocalCopy(string name)
    {
        lock (_indexGate)
            return _index is not null && _index.TryGetLocal(name, out var file) ? file : null;
    }

    private BepFileInfo? PeerCopy(string name)
    {
        lock (_indexGate)
            return _index is not null && _index.TryGet(name, out var file) ? file : null;
    }

    private void Store(BepFileInfo file, int state)
    {
        lock (_indexGate) _index?.PutLocal(file, state);
    }

    private long NextSequence()
    {
        lock (_indexGate) return _index?.NextLocalSequence() ?? 0;
    }

    /// <summary>
    /// Ob wir diesen Namen ausliefern koennen: entweder fuehrt ihn die
    /// Gegenstelle, oder wir haben ihn selbst angekuendigt.
    /// </summary>
    /// <remarks>
    /// Ohne den zweiten Fall kuendigten wir Dateien an, die wir anschliessend
    /// nicht herausgeben. Die Gegenstelle fragte danach und bekaeme
    /// "kenne ich nicht" von dem Geraet, das die Datei angeboten hat.
    /// </remarks>
    private bool KnownHere(string name)
    {
        lock (_indexGate)
        {
            if (_index is null) return false;
            if (_index.TryGet(name, out var peer) && !peer.Deleted) return true;
            return _index.TryGetLocal(name, out var mine) && !mine.Deleted;
        }
    }
}
