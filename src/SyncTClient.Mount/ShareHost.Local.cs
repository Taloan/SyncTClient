using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf;
using SyncTClient.Bep;
using SyncTClient.Vfs;
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
/// eine geaenderte Datei bekommt eine neue Versionsnummer und wird
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
/// eine neue Version, die Gegenstelle spiegelt sie zurueck, und beide Seiten
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

    /// <summary>So lange wartet ein Vermerk, bevor er bewertet wird.</summary>
    /// <remarks>
    /// Ein Speichervorgang schreibt oft mehrmals hintereinander. Wer sofort
    /// rechnet, rechnet die Blockliste eines halb geschriebenen Standes und
    /// kuendigt sie an.
    /// </remarks>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    /// <summary>So oft wird auch ohne Vermerk nachgesehen.</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// So oft wird nachgeholt, was bei "vollstaendig lokal" leer dasteht.
    /// </summary>
    /// <remarks>
    /// Nicht im Stundentakt des Ordnerdurchgangs. Dort hing es bisher allein,
    /// und eine Datei, die beim Verbinden nicht ankam, blieb bis zu
    /// fuenfundsiebzig Minuten leer -- waehrenddessen stand der Ordner auf
    /// "gleicht ab" bei neunundneunzig Prozent, und im Protokoll standen
    /// Lebenszeichen.
    ///
    /// Der Versuch kostet nichts, wenn nichts offen ist: die Liste kommt aus
    /// dem Rueckstand, der ohnehin gefuehrt wird.
    /// </remarks>
    private static readonly TimeSpan FetchInterval = TimeSpan.FromMinutes(1);

    private DateTime _lastFetch = DateTime.MinValue;

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
    /// <summary>
    /// Der Abstand zwischen zwei vollstaendigen Durchgaengen.
    /// </summary>
    /// <remarks>
    /// Eine Stunde, wie bei Syncthing. Der Beobachter meldet, was geschieht;
    /// der Durchgang ist das Netz darunter, fuer die Faelle, in denen eine
    /// Meldung ausbleibt -- ein uebergelaufener Puffer, eine Datei, die ein
    /// Programm offen haelt, ein Treiber, der nichts sagt.
    ///
    /// Vorher stand hier eine Minute, und der Durchgang war damit der teuerste
    /// Posten des Programms: einundsiebzigtausend Dateien, sechzigmal in der
    /// Stunde.
    /// </remarks>
    private static readonly TimeSpan RescanInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Der eigene Abstand dieser Freigabe: drei Viertel bis fuenf Viertel des
    /// Vorgabewerts.
    /// </summary>
    /// <remarks>
    /// Sieben Freigaben starten miteinander und haetten mit demselben Abstand
    /// auch ihren Durchgang miteinander -- einmal je Stunde saehe der Rechner
    /// aus wie unter Last, und dazwischen geschieht nichts. Gestreut faellt
    /// dieselbe Arbeit nicht mehr auf. Syncthing streut aus demselben Grund
    /// und in derselben Spanne.
    ///
    /// Einmal je Sitzung gezogen, nicht je Durchgang: ein Abstand, der sich
    /// staendig aendert, ist keiner.
    /// </remarks>
    private TimeSpan _rescanInterval = Streuen(RescanInterval);

    private static TimeSpan Streuen(TimeSpan abstand)
        => abstand * (0.75 + Random.Shared.NextDouble() * 0.5);

    /// <summary>
    /// Der Abstand ohne Beobachter: eine Minute, wie frueher.
    /// </summary>
    /// <remarks>
    /// Die Stunde ist nur zu rechtfertigen, solange etwas anderes die
    /// Aenderungen meldet. Faellt der Beobachter aus -- abgeschaltet, oder
    /// vom Laufwerk nicht getragen --, ist der Durchgang wieder die einzige
    /// Quelle, und dann darf er nicht stuendlich sein.
    /// </remarks>
    private void OhneBeobachter()
        => _rescanInterval = Streuen(Eingestellt() < TimeSpan.FromMinutes(1)
            ? Eingestellt()
            : TimeSpan.FromMinutes(1));

    /// <summary>Mit Beobachter gilt, was eingestellt ist.</summary>
    private void MitBeobachter() => _rescanInterval = Streuen(Eingestellt());

    /// <summary>Der eingestellte Abstand, in vertretbaren Grenzen.</summary>
    /// <remarks>
    /// Unter dreissig Sekunden lohnt kein Durchgang -- er dauert bei einem
    /// grossen Ordner laenger als der Abstand und liefe damit dauernd. Ueber
    /// einer Woche ist es kein Netz mehr, sondern eine Behauptung.
    /// </remarks>
    private TimeSpan Eingestellt()
        => TimeSpan.FromSeconds(Math.Clamp(_config.ScanIntervalSeconds, 30, 7 * 24 * 3600));

    /// <summary>
    /// So lange nach der eigenen Ankuendigung bleibt eine Datei liegen, bevor
    /// sie aus einem abgewaehlten Zweig entfernt wird.
    /// </summary>
    /// <remarks>
    /// Kurz gehalten. Die Gegenstelle fragt gleich nach der Ankuendigung nach
    /// den Bloecken oder gar nicht; wer laenger wartet, wartet umsonst.
    /// </remarks>
    private const long PruneDelayMs = 10_000;

    /// <summary>
    /// Bis zu so vielen Dateien wird jede einzeln ins Protokoll geschrieben.
    /// </summary>
    /// <remarks>
    /// Beim ersten Abgleich gehen tausende auf einmal hinaus; die einzeln zu
    /// nennen waere kein Protokoll mehr. Bei einer Handvoll ist es genau das,
    /// was man wissen will.
    /// </remarks>
    private const int AnnounceDetails = 8;

    /// <summary>So oft wird geprueft, was sich entfernen laesst.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(2);

    private DateTime _lastPrune = DateTime.MinValue;

    /// <summary>Wann ein Name zuletzt angekuendigt wurde.</summary>
    /// <remarks>
    /// Nur im Arbeitsspeicher. Nach einem Neustart gilt jede Datei als alt
    /// genug -- die Gegenstelle hatte die ganze Zwischenzeit.
    /// </remarks>
    private readonly ConcurrentDictionary<string, long> _announcedAt = new(StringComparer.Ordinal);

    private DateTime _lastScan = DateTime.UtcNow;

    private FileSystemWatcher? _watcher;

    /// <summary>
    /// Woher eine Datei kam, die gerade hierher verschoben wurde.
    /// </summary>
    /// <remarks>
    /// Nur fuer den einen Zug von der Meldung bis zur Bewertung. Danach ist
    /// der Eintrag verbraucht: eine zweite Bewertung desselben Namens ist
    /// keine Verschiebung mehr.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string> _renamedFrom = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Ob in dieser Sitzung schon ein vollstaendiger Index an diese
    /// Gegenstelle ging.
    /// </summary>
    /// <remarks>
    /// Je Gegenstelle gefuehrt. Eine, die sich neu verbindet, braucht einen
    /// vollstaendigen Index, auch wenn die anderen laengst Nachtraege
    /// bekommen.
    /// </remarks>
    private readonly ConcurrentDictionary<string, bool> _indexSentTo =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Die hoechste Sequenznummer der zuletzt gesendeten Nachricht, je
    /// Gegenstelle.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _lastSentTo =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Die eigene Geraete-ID. Sie steht in <c>modified_by</c> und im eigenen
    /// Zaehler des Versionsvektors.
    /// </summary>
    /// <remarks>
    /// Ohne sie laesst sich keine Version fortschreiben. Ist sie leer, wird
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

        // Die Auswahl wird hier nicht geprueft. Sie sagt, was auf diesem
        // Geraet liegen soll, nicht was zur Freigabe gehoert -- eine Datei
        // ausserhalb der Auswahl wird also ebenso angekuendigt und uebertragen.
        // Entfernt wird sie erst danach, von PruneExcluded.

        // Was wir selbst gerade schreiben, ist keine Aenderung von aussen.
        if (IsHydrating(name)) return;

        // Die Datei ist wieder da, also ist sie nicht geloescht. Das ist
        // richtig -- aber wenn hier eine vorgemerkte Loeschung stirbt, muss
        // man es sehen koennen.
        //
        // Genau daran scheiterten zwei Laeufe hintereinander: der Start merkte
        // die Loeschung vor, ein anderer Weg legte die Datei sofort wieder an,
        // und die Loeschung war fort, ohne dass eine Zeile davon zeugte. Aus
        // dem Protokoll sah es aus, als waere sie nie erkannt worden.
        if (_removed.TryRemove(name, out _))
            _log($"[{FolderId}] \"{name}\" ist wieder da -- die vorgemerkte Loeschung entfaellt.");

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

        // Dieselbe Bedingung wie beim Start, und aus demselben Grund. Faellt
        // ein Laufwerk im laufenden Betrieb aus, meldet der Beobachter das
        // Verschwinden jeder einzelnen Datei, und von hier aus saehe das wie
        // eine Loeschung von Hand aus. Die Markierung loescht niemand; fehlt
        // sie, fehlt der Ordner.
        if (!Directory.Exists(MarkerPath))
        {
            if (_warned.TryAdd("marker", 0))
                _log($"[{FolderId}] Die Ordnermarkierung \"{MarkerFolder}\" fehlt. " +
                     "Loeschungen werden nicht weitergegeben, solange das so ist.");

            return;
        }

        // Ausserhalb der Auswahl entfernen wir selbst, sobald die Gegenstelle
        // die Datei fuehrt. Das ist keine Loeschung, sondern das Ende des
        // Weges. Wuerde sie angekuendigt, loeschte die Gegenstelle die eben
        // empfangene Datei sofort wieder.
        //
        // Die Bedingung trifft nur auf diesen Fall zu: eine Datei ausserhalb
        // der Auswahl, die die Gegenstelle vollstaendig fuehrt, kann nur von
        // uns dorthin gebracht worden sein.
        if (!_config.Includes(name) && MayEvict(name)) return;

        if (IsHydrating(name)) return;

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
        if (IsHousekeeping(name)) return null;

        // Und dasselbe fuer die Muster. Hier, weil jeder Weg von einem Pfad
        // zu einem Namen hier vorbeikommt: der Durchgang ueber den Ordner,
        // das Ankuendigen, das Holen, das Entfernen. Was hier ausfaellt, wird
        // von keinem davon angefasst -- auch nicht geloescht, denn ein Muster
        // sagt "gehoert nicht dazu" und nicht "darf weg".
        if (_config.IsIgnored(name)) return null;

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
        var uhr = System.Diagnostics.Stopwatch.StartNew();

        // Was im Ordner steht, mit Groesse und Zeit. Platzhalter gehoeren
        // dazu: fuer den Rueckstand zaehlt, ob der Eintrag da ist und zum
        // Index passt, nicht ob sein Inhalt lokal liegt.
        var vorhanden = new Dictionary<string, (long Size, long ModifiedS)>(StringComparer.Ordinal);

        // Was davon wirklich Bytes haelt. Der Cache fuehrt sonst nur, was er
        // selbst geholt hat, und wuesste von hineinkopierten Dateien nichts.
        var mitInhalt = new Dictionary<string, (long Bytes, DateTimeOffset LastAccess)>(StringComparer.Ordinal);

        // Was am Platzhalter steht und was wir vermerkt haben, geht
        // auseinander, sobald jemand das Menue von Windows selbst benutzt --
        // "Immer auf diesem Geraet behalten" steht dort ebenfalls. Gesammelt
        // wird waehrend des Durchgangs, gerichtet danach: das Setzen oeffnet
        // die Datei, und das gehoert nicht in eine Aufzaehlung.
        var zuRichten = new List<(string Pfad, bool Lokal)>();

        try
        {
            // Verzeichnisse gehoeren ebenso zur Freigabe. Sie tragen keinen
            // Inhalt, aber ein leerer Ordner ist eine Aussage -- ohne ihn
            // entsteht er auf der Gegenseite erst, wenn eine Datei darin
            // landet. In "vorhanden" gehoeren sie nicht: dort werden Groessen
            // verglichen, und ein Verzeichnis hat keine.
            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories("*", options))
            {
                if (NameOf(dir.FullName) is not { } name) continue;

                var gefuehrt = LocalCopy(name);
                if (gefuehrt is not null && !gefuehrt.Deleted) continue;

                _dirty[name] = 0;
                found++;
            }

            foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                if (NameOf(info.FullName) is not { } name) continue;

                vorhanden[name] = (
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds());

                // Der Abgleich mit dem Anheft-Zustand.
                //
                // Ohne Vermerk gilt, was Windows sagt: wer ueber dessen
                // eigenes Menue anheftet, hat damit einen Modus gewaehlt, und
                // der gehoert in die Datenbank.
                //
                // Mit Vermerk gilt die Datenbank. Ein Platzhalter, der neu
                // entstanden ist, traegt den Zustand seines Ordners noch
                // nicht -- angelegt wird er ohne, und erst hier bekommt er
                // ihn.
                var angeheftet = ((uint)info.Attributes & Angeheftet) != 0;
                var vermerk = ModusVon(name);

                if (vermerk is null)
                {
                    if (angeheftet) ModusMerken(name, lokal: true);
                }
                else if (vermerk.Value != angeheftet)
                {
                    zuRichten.Add((info.FullName, vermerk.Value));
                }

                // Ein Platzhalter ist nicht vollstaendig hier. Angekuendigt
                // wird nur, was wir ganz haben.
                if (((uint)info.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0) continue;

                mitInhalt[name] = (info.Length, new DateTimeOffset(info.LastAccessTimeUtc));

                var known = LocalCopy(name);
                if (known is not null && !known.Deleted &&
                    known.Size == info.Length &&
                    known.ModifiedS == new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())
                    continue;

                _dirty[name] = 0;
                found++;
            }
        }
        catch (Exception ex)
        {
            // Auch alles andere. Ein Durchgang ueber den Ordner ist eine
            // Bestandsaufnahme; scheitert sie, ist das eine Meldung wert,
            // aber kein Grund, die Freigabe in den Fehlerzustand zu setzen
            // und damit auch das Ausliefern von Dateien zu beenden.
            _log($"[{FolderId}] der Durchgang ueber \"{root}\" brach ab: {Herkunft(ex)}");
            return;
        }

        // Erst jetzt, ausserhalb der Aufzaehlung.
        if (zuRichten.Count > 0 && _mount is not null)
        {
            // Und nicht alle auf einmal. Ein Ordner mit sechsundsechzigtausend
            // Dateien auf "immer lokal" waere sonst ein Durchgang, der nur
            // noch Attribute schreibt. Der Rest kommt im naechsten -- und
            // bleibt die Zahl ueber die Durchgaenge hinweg gleich, gelingt das
            // Setzen nicht, und das ist im Protokoll zu sehen.
            const int JeDurchgang = 2000;

            var stapel = zuRichten.Take(JeDurchgang).ToList();
            foreach (var (pfad, lokal) in stapel) _mount.SetPinned(pfad, lokal);

            var lokale = stapel.Count(e => e.Lokal);
            _log($"[{FolderId}] Anheft-Zustand nachgezogen: {lokale} auf \"immer lokal\", " +
                 $"{stapel.Count - lokale} auf Platzhalter" +
                 (zuRichten.Count > stapel.Count
                     ? $", {zuRichten.Count - stapel.Count} folgen im naechsten Durchgang."
                     : "."));
        }

        LastScan = DateTime.Now;
        _mitInhalt = mitInhalt;
        // Aus der Liste der freigegebenen Dateien faellt, was es nicht mehr
        // gibt. Mehr kann der Durchgang dazu nicht sagen: das Attribut
        // UNPINNED traegt jeder Platzhalter, es ist die Bedingung dafuer, dass
        // Windows ueberhaupt ein Ueberlagerungssymbol zeigt -- daran ist nicht
        // abzulesen, ob jemand den Platz freigegeben hat.
        // Was es nicht mehr gibt, braucht keinen Modus mehr. Verzeichnisse
        // stehen nicht in "vorhanden" -- der Durchgang sammelt dort nur
        // Dateien --, deshalb wird bei ihnen auf der Platte nachgesehen.
        foreach (var name in _modus.Keys.ToList())
        {
            if (vorhanden.ContainsKey(name)) continue;

            var pfad = LocalPathOf(name);
            if (File.Exists(pfad) || Directory.Exists(pfad)) continue;

            ModusVergessen(name);
        }
        _vorhanden = vorhanden;

        // Der Durchgang rechnet die Zahlen gleich selbst; was zwischen zwei
        // Durchgaengen anfiel, ist damit erledigt.
        _zahlenVeraltet = false;
        _letzteZahlen = DateTime.UtcNow;

        // Und was der Durchgang nicht mehr angetroffen hat. Vorher blieb das
        // ungenutzt: die Liste sagte, was da ist, und niemand fragte, was
        // fehlt.
        FehlendeAusDemDurchgang(vorhanden);

        // Nur wenn es auffaellt. Ein Durchgang ueber fuenfundvierzigtausend
        // Dateien kostet Zeit, und er laeuft in jeder Minute; eine Zeile je
        // Minute je Freigabe waere aber Laerm. Gemeldet wird, was ueber einer
        // Viertelsekunde liegt -- das ist die Groessenordnung, ab der es sich
        // lohnt, ueber den Abstand nachzudenken.
        if (uhr.ElapsedMilliseconds > 250)
            _log($"[{FolderId}] Durchgang ueber {vorhanden.Count} Dateien: {uhr.ElapsedMilliseconds} ms.");

        // Erst die Bilanz nachziehen, dann messen: die Anzeige des belegten
        // Platzes haengt daran, und der naechste Takt entscheidet auf dieser
        // Grundlage, ob etwas freigegeben werden muss.
        _cache?.ReconcileWith(mitInhalt);
        CacheChanged?.Invoke();

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
    /// Gezaehlt wird in beide Richtungen. Was die Gegenstelle fuehrt und hier
    /// fehlt, steht aus -- und was hier liegt und sie nicht kennt, ebenso. Es
    /// ist dieselbe Differenz, nur von der anderen Seite gesehen; solange die
    /// beiden Staende auseinandergehen, ist der Abgleich nicht fertig.
    ///
    /// Als abgeglichen gilt eine Datei, wenn sie auf beiden Seiten steht und
    /// Groesse und Zeit zusammenpassen. Ob ihr Inhalt lokal liegt, spielt
    /// keine Rolle -- ein Platzhalter ist der erwuenschte Zustand und kein
    /// Rueckstand.
    ///
    /// Gerechnet wird auf dem Verzeichnis, das der Durchgang ohnehin gelesen
    /// hat. Ein eigener Lauf ueber den Index mit einem Zugriff je Datei waere
    /// dieselbe Auskunft zum doppelten Preis.
    /// </remarks>
    /// <summary>Was beim letzten Durchgang wirklich Inhalt hielt.</summary>
    private Dictionary<string, (long Bytes, DateTimeOffset LastAccess)> _mitInhalt = [];

    /// <summary>
    /// Platzhalter, deren Inhalt der Anwender ausdruecklich freigegeben hat.
    /// </summary>
    /// <remarks>
    /// Bei "vollstaendig lokal" gilt jeder Inhalt, der fehlt, als Rueckstand
    /// und wird nachgeholt. Fuer diese hier nicht: die Betriebsart ist der
    /// Modus fuer neue Dateien, "Speicherplatz freigeben" ist eine Aussage
    /// ueber eine bestehende. Sonst kam sie binnen einer Minute zurueck.
    /// </remarks>
    /// <summary>
    /// Der Modus je Datei und je Ordner. True heisst "immer lokal".
    /// </summary>
    /// <remarks>
    /// Was hier nicht steht, folgt der Betriebsart der Freigabe -- die gilt
    /// fuer neue Dateien. Ein Eintrag auf einem Ordner gilt fuer alles
    /// darunter, bis ein Eintrag weiter unten etwas anderes sagt; deshalb
    /// wird von unten nach oben gesucht.
    /// </remarks>
    private readonly ConcurrentDictionary<string, bool> _modus = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Liest aus dem Index, welche Dateien freigegeben sind.
    /// </summary>
    /// <remarks>
    /// Gefuehrt wird das selbst, weil das Dateisystem die Frage nicht
    /// beantwortet: FILE_ATTRIBUTE_UNPINNED traegt jeder Platzhalter -- ohne
    /// den Anheft-Zustand zeigt Windows an ihm gar kein
    /// Ueberlagerungssymbol. Wer daran ablesen wollte, was der Anwender
    /// freigegeben hat, bekaeme jeden neu angelegten Platzhalter dazu.
    ///
    /// Und zwar im Index derselben Freigabe, in einer eigenen Tabelle. Er ist
    /// ohnehin die Ablage fuer alles, was je Ordner zu merken ist, und eine
    /// Datenbank haelt einen abgebrochenen Schreibvorgang aus.
    /// </remarks>
    private void ModiLesen()
    {
        try
        {
            lock (_indexGate)
            {
                if (_index is null) return;

                foreach (var (name, lokal) in _index.Modes()) _modus[name] = lokal;
            }
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Modi lesen: {Herkunft(ex)}");
        }
    }

    /// <summary>Vermerkt den Modus einer Datei oder eines Ordners.</summary>
    private void ModusMerken(string name, bool lokal)
    {
        _modus[name] = lokal;

        try
        {
            lock (_indexGate) _index?.SetMode(name, lokal);
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Modus schreiben: {Herkunft(ex)}");
        }
    }

    private void ModusVergessen(string name)
    {
        _modus.TryRemove(name, out _);

        try
        {
            lock (_indexGate) _index?.ClearMode(name);
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Modus schreiben: {Herkunft(ex)}");
        }
    }

    /// <summary>
    /// Der Modus, der fuer diesen Namen gilt -- oder null fuer die
    /// Betriebsart der Freigabe.
    /// </summary>
    /// <remarks>
    /// Von unten nach oben: der Eintrag an der Datei selbst wiegt schwerer
    /// als der an ihrem Ordner, und der an einem Unterordner schwerer als der
    /// am Ordner darueber. So laesst sich ein ganzer Zweig freigeben und eine
    /// einzelne Datei darin trotzdem behalten.
    /// </remarks>
    public bool? ModusVon(string relativePath)
    {
        if (_modus.IsEmpty) return null;
        if (_modus.TryGetValue(relativePath, out var eigen)) return eigen;

        for (var schnitt = relativePath.LastIndexOf('/'); schnitt > 0;
             schnitt = relativePath.LastIndexOf('/', schnitt - 1))
        {
            if (_modus.TryGetValue(relativePath[..schnitt], out var oben)) return oben;
        }

        return null;
    }

    /// <summary>Was der letzte Durchgang im Ordner angetroffen hat.</summary>
    /// <remarks>
    /// Damit die Zahlen fuer die Anzeige neu gerechnet werden koennen, ohne
    /// den Ordner noch einmal abzugehen. Der Durchgang ueber
    /// sechsundsechzigtausend Dateien kostet Sekunden; der Lauf ueber den
    /// Index kostet einen Bruchteil davon und liefert genau die Zahl, die
    /// sich geaendert hat.
    /// </remarks>
    private Dictionary<string, (long Size, long ModifiedS)> _vorhanden = [];

    /// <summary>Hat sich seit der letzten Zaehlung am Index etwas bewegt?</summary>
    private bool _zahlenVeraltet;

    /// <summary>Wann zuletzt gezaehlt wurde.</summary>
    private DateTime _letzteZahlen = DateTime.MinValue;

    /// <summary>
    /// Nicht oefter als das. Bei einer Freigabe, in der jemand gerade
    /// arbeitet, bewegt sich der Index im Sekundentakt -- und der Lauf ueber
    /// hunderttausend Eintraege gehoert nicht in jede dieser Sekunden.
    /// </summary>
    private static readonly TimeSpan Zaehlabstand = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Rechnet die Zahlen fuer die Anzeige neu, wenn der Index sich bewegt
    /// hat.
    /// </summary>
    /// <remarks>
    /// Bisher hingen sie am Durchgang ueber den Ordner, und der laeuft
    /// stuendlich. Eine Loeschung ging hinaus, der Index stimmte, die
    /// Gegenstelle fuehrte die Datei nicht mehr -- und die Anzeige nannte
    /// eine Stunde lang die alte Zahl. Von aussen sah das aus, als sei die
    /// Loeschung nicht durchgegangen.
    ///
    /// Gerechnet wird auf dem Bestand des letzten Durchgangs. Fuer die
    /// Zahlen der Gegenstelle spielt er ohnehin keine Rolle -- die kommen
    /// aus dem Index. Fuer die eigenen kann er einen Augenblick nachhinken,
    /// bis der naechste Durchgang ihn erneuert.
    /// </remarks>
    private void ZahlenNachziehen()
    {
        if (!_zahlenVeraltet) return;
        if (DateTime.UtcNow - _letzteZahlen < Zaehlabstand) return;

        _zahlenVeraltet = false;
        _letzteZahlen = DateTime.UtcNow;

        MeasureOutstanding(_vorhanden);
    }

    /// <summary>
    /// Warum dieser Eintrag noch aussteht -- in Worten, mit den Zahlen, die
    /// nicht zusammenpassen.
    /// </summary>
    /// <remarks>
    /// "Steht hier nicht so da" hat drei ganz verschiedene Ursachen: die
    /// Datei fehlt, sie ist anders gross, oder sie traegt eine andere Zeit.
    /// Die erste ist ein Rueckstand, die dritte oft nur eine eigene
    /// Aenderung, die noch nicht heraus ist. Ohne die Unterscheidung raet
    /// man, und ein Verdacht wie "die Datei ist gesperrt" liegt naeher als
    /// die Wahrheit.
    /// </remarks>
    private string Grund(
        string name, long size, long modifiedS,
        Dictionary<string, (long Size, long ModifiedS)> vorhanden, bool fehlt)
    {
        // "Ohne Inhalt" hiess das einmal, und das las sich wie "die Datei ist
        // da, aber leer" -- das Gegenteil dessen, was gemeint ist. Gemeint
        // ist: der Eintrag steht richtig da, Name, Groesse und Zeit stimmen,
        // der Inhalt ist aber nicht uebertragen.
        //
        // Laeuft die Hydration bereits, gehoert das dazu. Der Rueckstand wird
        // gemessen, waehrend uebertragen wird; ohne diesen Zusatz meldet die
        // Zeile einen Zustand, der sich Sekunden spaeter erledigt hat.
        if (!fehlt)
            return IsHydrating(name)
                ? "hier nur ein Platzhalter, Übertragung läuft"
                : "hier nur ein Platzhalter";

        if (!vorhanden.TryGetValue(name, out var da)) return "liegt hier nicht";

        if (da.Size != size)
        {
            // Gerundet sehen 179964 und 179970 Bytes beide wie "176 KB" aus,
            // und die Zeile behauptet dann einen Unterschied, den sie selbst
            // nicht zeigt. In dem Fall die Bytes.
            var hier = Format.Bytes(da.Size);
            var dort = Format.Bytes(size);

            return hier == dort
                ? $"hier {da.Size} statt {size} Bytes"
                : $"hier {hier} statt {dort}";
        }


        return $"hier {Zeit(da.ModifiedS)} statt {Zeit(modifiedS)}";
    }

    /// <summary>So viele offene Namen werden gemerkt.</summary>
    private const int ListenGrenze = 2000;

    /// <summary>So viele Namen holt eine Seite aus dem Index.</summary>
    private const int Seitengroesse = 2000;

    /// <summary>
    /// Liest den Index in Seiten und gibt zwischen ihnen die Sperre frei.
    /// </summary>
    /// <remarks>
    /// Der Aufrufer bekommt eine einzige Folge und merkt davon nichts. Was
    /// er nicht bekommt, ist eine Momentaufnahme: zwischen zwei Seiten kann
    /// sich der Index aendern. Fuer eine Zaehlung, die im naechsten Durchgang
    /// ohnehin neu gemacht wird, ist das kein Verlust -- fuer die Bedienbarkeit
    /// des Programms ist es der Unterschied.
    /// </remarks>
    private IEnumerable<(string Name, long Size, long ModifiedS, bool IsDirectory, bool HasContent)> Seitenweise()
    {
        var nach = "";

        while (true)
        {
            IReadOnlyList<(string Name, long Size, long ModifiedS, bool IsDirectory, bool HasContent)> seite;
            lock (_indexGate)
            {
                if (_index is null) yield break;
                seite = _index.EnumerateLight(nach, Seitengroesse);
            }

            if (seite.Count == 0) yield break;
            nach = seite[^1].Name;

            foreach (var eintrag in seite) yield return eintrag;

            // Zwischen zwei Seiten aus der Hand geben.
            Thread.Sleep(0);
        }
    }

    /// <summary>Platzhalter, die bei "vollstaendig lokal" noch zu fuellen sind.</summary>
    private List<string> _ohneInhalt = [];

    private static string Zeit(long unixSekunden)
        => DateTimeOffset.FromUnixTimeSeconds(unixSekunden).ToLocalTime().ToString("dd.MM. HH:mm:ss");

    private void MeasureOutstanding(Dictionary<string, (long Size, long ModifiedS)> vorhanden)
    {
        var offen = 0;
        long bytes = 0;
        var wartend = 0;
        long wartendBytes = 0;

        // Die ersten paar Namen. Eine Zahl allein laesst raten, welche Dateien
        // gemeint sind -- und bei zwei Ordnern mit derselben Groesse raet man
        // falsch.
        var wartendeNamen = new List<string>();

        // Und dasselbe fuer den Rueckstand selbst. "2 von 976 Dateien" laesst
        // nicht erkennen, welche zwei -- und wenn zwei Dateien dauerhaft
        // stehen bleiben, ist genau das die Frage.
        var offeneNamen = new List<string>();

        // Und die andere Richtung: angekuendigt, aber von der Gegenstelle
        // noch nicht abgerufen. Kein Rueckstand von uns, denn hier ist nichts
        // zu tun ausser die Bloecke bereitzuhalten.
        var ausgehend = 0;
        long ausgehendBytes = 0;
        var ausgehendeNamen = new List<string>();

        // Und die vollstaendige Liste fuer das Fenster. Gedeckelt: bei einer
        // frisch verbundenen Freigabe stehen alle Dateien offen, und
        // hunderttausend Zeilen liest niemand.
        var offeneListe = new List<OutstandingItem>();

        // Und die Namen, die bei "vollstaendig lokal" noch leer sind. Der
        // Durchgang stellt sie ohnehin fest; ein zweiter Lauf ueber den Index
        // waere dieselbe Auskunft zum doppelten Preis.
        var ohneInhalt = new List<string>();
        var gesamt = 0;
        long gesamtBytes = 0;
        var vereint = 0;
        long vereintBytes = 0;
        long vorhandenBytes = 0;

        foreach (var eintrag in vorhanden.Values) vorhandenBytes += eintrag.Size;

        try
        {
            {
                if (_index is null) return;

                var bekannt = new HashSet<string>(StringComparer.Ordinal);

                // Seitenweise, und die Sperre nur je Seite.
                //
                // Vorher lag sie auf dem ganzen Durchgang: bei hunderttausend
                // Dateien Sekunden am Stueck, in denen niemand sonst an die
                // Datenbank kam -- auch der Schreiber nicht, der gerade den
                // Index aufnimmt. Und die vollstaendige Liste war ein
                // einziger grosser Brocken im Speicher.
                foreach (var (name, size, modifiedS, isDirectory, hatInhalt) in Seitenweise())
                {
                    if (isDirectory) continue;

                    // Ein Muster nimmt den Namen ganz aus dem Abgleich. Der
                    // Index sollte ihn gar nicht mehr fuehren; bis der
                    // Durchgang aufgeraeumt hat, zaehlt er hier jedenfalls
                    // nicht mit.
                    if (_config.IsIgnored(name)) continue;

                    // Abgewaehltes zaehlt gar nicht -- weder als Rueckstand
                    // noch im Nenner.
                    //
                    // Frueher galt es als offen, solange es hier noch lag und
                    // "darauf wartete, hinauszugehen". Das kann es aber nicht
                    // einloesen: entfernt wird nur, was die Gegenstelle
                    // vollstaendig fuehrt, und gerade das tut sie bei diesen
                    // Dateien nicht. Der Balken stand damit dauerhaft kurz vor
                    // hundert -- wegen eines Zweiges, den jemand ausdruecklich
                    // abgewaehlt hat.
                    //
                    // Was hier faelschlich liegt, meldet das Entfernen. Der
                    // Abgleich hat damit nichts zu tun.
                    // Ausserhalb der Auswahl ist die Abwesenheit der
                    // erwuenschte Zustand. Offen ist ein solcher Eintrag nur,
                    // solange wir seinen Inhalt halten und er noch hinaus
                    // muss: erst hochladen, dann hier entfernen.
                    //
                    // Ein Platzhalter zaehlt dabei nicht. Er haelt nichts,
                    // hat nichts hinauszugeben und verschwindet beim naechsten
                    // Entfernen von selbst.
                    if (!_config.Includes(name))
                    {
                        if (!_mitInhalt.ContainsKey(name)) continue;

                        offen++;
                        bytes += size;
                        vereint++;
                        vereintBytes += size;
                        continue;
                    }


                    bekannt.Add(name);
                    gesamt++;
                    gesamtBytes += size;
                    vereint++;
                    vereintBytes += size;

                    // Zwei Gruende, dass etwas aussteht. Der erste ist unser
                    // eigener: der Eintrag steht hier noch nicht so da.
                    var fehlt = !vorhanden.TryGetValue(name, out var da)
                                || da.Size != size || da.ModifiedS != modifiedS;

                    // Der zweite gehoert der Gegenstelle: sie kennt die Datei,
                    // haelt sie aber nicht. Der Platzhalter steht dann zwar
                    // richtig da, ist aber nicht zu fuellen -- abgeglichen ist
                    // das nicht.
                    //
                    // Und der dritte gilt nur bei "vollstaendig lokal": dort
                    // ist ein Platzhalter kein erwuenschter Zustand, sondern
                    // eine Zusage, die nicht eingehalten ist. Bei on-demand
                    // waere er der Normalfall.
                    var leer = _config.Mode == ShareMode.AlwaysLocal
                               && !_mitInhalt.ContainsKey(name)
                               && ModusVon(name) != false;

                    if (!fehlt && hatInhalt && !leer) continue;

                    // Wessen Rueckstand ist das?
                    //
                    // Steht hier eine neuere Fassung, als die Gegenstelle
                    // fuehrt, fehlt uns nichts: sie hat unsere Ankuendigung
                    // noch nicht abgerufen. Zusammen mit dem eigenen
                    // Rueckstand gezaehlt behauptet die Anzeige, hier sei
                    // etwas zu tun -- und wenn beides auf null steht, obwohl
                    // die Gegenstelle noch zwei Dateien offen fuehrt, sagen
                    // beide Seiten verschiedene Dinge ueber denselben Ordner.
                    if (fehlt && NurHierNeuer(name))
                    {
                        ausgehend++;
                        ausgehendBytes += vorhanden.TryGetValue(name, out var meins) ? meins.Size : size;
                        if (ausgehendeNamen.Count < 5) ausgehendeNamen.Add(name);
                        continue;
                    }

                    // Eine Datei, die die Gegenstelle selbst nicht haelt, ist
                    // nicht abgeglichen -- aber auch nicht zu beschaffen. Sie
                    // getrennt zu zaehlen ist der Unterschied zwischen "es
                    // fehlt noch etwas" und "hier ist nichts mehr zu tun".
                    //
                    // Zusammengezaehlt stand der Balken sonst fuer immer kurz
                    // vor hundert und der Zustand auf "gleicht ab", ohne dass
                    // irgendein Handgriff daran etwas geaendert haette.
                    if (!fehlt && !hatInhalt)
                    {
                        wartend++;
                        wartendBytes += size;
                        if (wartendeNamen.Count < 5) wartendeNamen.Add(name);
                        continue;
                    }

                    offen++;
                    bytes += size;
                    if (leer && !fehlt) ohneInhalt.Add(name);

                    var grund = Grund(name, size, modifiedS, vorhanden, fehlt);
                    if (offeneNamen.Count < 5)
                        offeneNamen.Add($"{name} ({Format.Bytes(size)}, {grund})");
                    if (offeneListe.Count < ListenGrenze)
                        offeneListe.Add(new OutstandingItem(name, size, grund));
                }

                // Die andere Richtung: was hier liegt und noch nicht
                // angekuendigt ist.
                //
                // Massgeblich ist die eigene Ankuendigung, nicht der Index der
                // Gegenstelle. Ob sie den Namen zurueckspiegelt, ist ihre
                // Buchfuehrung; manche tun es nie. Wer darauf wartet, zeigt
                // einen Rueckstand an, der nie kleiner wird -- ein Balken, der
                // fuer immer bei 100 Prozent und ein paar offenen Bytes steht.
                foreach (var (name, eintrag) in vorhanden)
                {
                    if (bekannt.Contains(name)) continue;

                    vereint++;
                    vereintBytes += eintrag.Size;

                    // Angekuendigt heisst: genau diese Version ist heraus.
                    // Groesse und Zeit muessen dazu passen, sonst steht die
                    // Aenderung noch aus.
                    bool angekuendigt;
                    lock (_indexGate)
                    {
                        angekuendigt = _index is not null
                                       && _index.TryGetLocal(name, out var eigene)
                                       && !eigene.Deleted
                                       && eigene.Size == eintrag.Size
                                       && eigene.ModifiedS == eintrag.ModifiedS;
                    }

                    if (angekuendigt) continue;

                    // Und zur Bewertung vormerken. Der Durchgang ueber den
                    // Ordner uebergeht Platzhalter -- sie halten keinen Inhalt,
                    // also gibt es normalerweise nichts anzukuendigen. Ein
                    // hierher verschobener Platzhalter ist die Ausnahme: seine
                    // Blockliste steht unter dem alten Namen, und den nennt
                    // seine Identitaet. Ohne diesen Vermerk kaeme er nie zur
                    // Bewertung und bliebe fuer immer offen.
                    _dirty[name] = 0;

                    offen++;
                    bytes += eintrag.Size;
                    const string nochNicht = "hier vorhanden, noch nicht angekündigt";
                    if (offeneNamen.Count < 5)
                        offeneNamen.Add($"{name} ({Format.Bytes(eintrag.Size)}, {nochNicht})");
                    if (offeneListe.Count < ListenGrenze)
                        offeneListe.Add(new OutstandingItem(name, eintrag.Size, nochNicht));
                }
            }
        }
        catch (Exception ex)
        {
            // Eine Zahl fuer die Anzeige. Sie ist es nicht wert, den
            // Hintergrundlauf abzubrechen -- aber sie ist es wert, dass man
            // erfaehrt, warum sie fehlt.
            _log($"[{FolderId}] Rueckstand liess sich nicht bestimmen: {Herkunft(ex)}");
            return;
        }

        // Nur wenn sich etwas bewegt hat -- und einmal am Anfang. Eine Zeile
        // je Minute, die immer dasselbe sagt, verdeckt die Zeilen, die etwas
        // sagen; gar keine Zeile laesst offen, ob ueberhaupt gemessen wurde.
        if (offen != Outstanding || IndexFiles == 0)
            _log($"[{FolderId}] Rueckstand: {offen} von {vereint} Dateien, " +
                 $"{bytes / (1024.0 * 1024.0):0.0} von {vereintBytes / (1024.0 * 1024.0):0.0} MB " +
                 $"(Gegenstelle {gesamt}, hier {vorhanden.Count}" +
                 (wartend > 0 ? $", {wartend} haelt die Gegenstelle selbst nicht" : "") + ")." +
                 (offeneNamen.Count > 0 ? " Offen: " + string.Join(", ", offeneNamen) +
                     (offen > offeneNamen.Count ? $" und {offen - offeneNamen.Count} weitere" : "") + "." : ""));

        LocalFiles = vorhanden.Count;
        LocalBytes = vorhandenBytes;
        IndexFiles = gesamt;
        IndexTotalBytes = gesamtBytes;
        SyncTotal = vereint;
        SyncTotalBytes = vereintBytes;
        // Nur wenn sich die Menge geaendert hat. Sie aendert sich selten und
        // steht sonst in jeder Minute noch einmal da.
        if (wartend != Awaiting && wartend > 0)
            _log($"[{FolderId}] wartet auf die Gegenstelle: " +
                 string.Join(", ", wartendeNamen.Select(n => $"\"{n}\"")) +
                 (wartend > wartendeNamen.Count ? $" und {wartend - wartendeNamen.Count} weitere" : "") + ".");

        // Nur wenn sich die Menge geaendert hat, und auch die Rueckkehr auf
        // null gehoert dazu: sie ist die Nachricht, dass die Gegenstelle
        // abgerufen hat.
        if (ausgehend != Outgoing)
            _log(ausgehend > 0
                ? $"[{FolderId}] an die Gegenstelle offen: {ausgehend} Dateien, " +
                  $"{ausgehendBytes / (1024.0 * 1024.0):0.0} MB. Angekuendigt, noch nicht abgerufen: " +
                  string.Join(", ", ausgehendeNamen) +
                  (ausgehend > ausgehendeNamen.Count ? $" und {ausgehend - ausgehendeNamen.Count} weitere" : "") + "."
                : $"[{FolderId}] die Gegenstelle hat alles Angekuendigte abgerufen.");

        Outgoing = ausgehend;
        OutgoingBytes = ausgehendBytes;

        Outstanding = offen;
        OutstandingBytes = bytes;
        OutstandingItems = offeneListe;
        _ohneInhalt = ohneInhalt;
        Awaiting = wartend;
        AwaitingBytes = wartendBytes;
        UpdateOutstandingPhase();
    }

    /// <summary>
    /// Nimmt den ganzen Index noch einmal vor.
    /// </summary>
    /// <remarks>
    /// Nach einer geaenderten Auswahl. Das Ausschliessen entfernt, aber das
    /// Wiederaufnehmen legte bisher nichts an: die Auswahl war ein Filter fuer
    /// das, was hereinkam, und was nie wieder angekuendigt wird, kam auch nie
    /// wieder. Ein Zweig, den jemand versehentlich abgewaehlt hatte, blieb
    /// damit fort, obwohl das Haekchen wieder stand.
    ///
    /// Angelegt wird nur, was fehlt. Ein Platzhalter, der schon richtig
    /// dasteht, wird nicht angefasst -- sonst ginge ein geholter Inhalt ohne
    /// Not verloren.
    /// </remarks>
    public void RequeueAll()
    {
        lock (_indexGate)
            if (_index is not null) QueueIncoming(_index.AllNames());

        // Der naechste Durchgang soll neu messen und nicht erst in einer
        // Minute: der Rueckstand hat sich gerade geaendert.
        _lastScan = DateTime.MinValue;
        Wake();
    }

    /// <summary>
    /// Raeumt weg, was ein Muster aus dem Abgleich genommen hat.
    /// </summary>
    /// <remarks>
    /// Ein Muster wirkt ab dem Moment, in dem es dasteht -- fuer alles, was
    /// danach kommt. Was schon im Index steht und schon im Ordner liegt,
    /// bliebe ohne diesen Durchgang stehen: im Baum sichtbar, im Rueckstand
    /// gezaehlt, obwohl niemand mehr etwas damit vorhat.
    ///
    /// Dateien mit Inhalt werden dabei nicht angefasst. Ein Muster sagt "das
    /// gehoert nicht zum Abgleich", nicht "das darf weg" -- wer <c>*.jpg</c>
    /// tippt, um kuenftige Bilder herauszuhalten, will nicht seine
    /// vorhandenen verlieren. Sie bleiben als gewoehnliche Dateien liegen.
    ///
    /// Ein leerer Platzhalter geht dagegen fort. Er haelt nichts, und ohne
    /// Abgleich kann er auch nie wieder etwas halten: er waere ein Name, der
    /// beim Anklicken einen Fehler ergibt.
    /// </remarks>
    public (int Entfernt, int Geblieben) PurgeIgnored()
    {
        List<string> namen;
        lock (_indexGate)
        {
            if (_index is null) return (0, 0);

            // Die Verwaltungsnamen ebenso. Sie kommen heute nicht mehr
            // herein, standen aber im Index, solange sie es taten -- und ein
            // Eintrag, den niemand mehr anwendet, zaehlt dort bis in alle
            // Ewigkeit als Rueckstand.
            namen = [.. _index.AllNames().Where(n => _config.IsIgnored(n) || IsHousekeeping(n))];
        }

        if (namen.Count == 0) return (0, 0);

        var entfernt = 0;
        var geblieben = 0;

        foreach (var name in namen)
        {
            try
            {
                var path = LocalPathOf(name);

                if (File.Exists(path))
                {
                    var info = new System.IO.FileInfo(path);
                    var leer = ((uint)info.Attributes
                                & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0;

                    if (leer) File.Delete(path);
                    else geblieben++;
                }

                lock (_indexGate) _index?.Forget(name);
                entfernt++;
            }
            catch (Exception ex)
            {
                _log($"[{FolderId}] \"{name}\" liess sich nicht ausnehmen: {Herkunft(ex)}");
            }
        }

        _log($"[{FolderId}] {entfernt} Namen durch Muster ausgenommen" +
             (geblieben > 0 ? $", {geblieben} davon liegen weiter im Ordner" : "") + ".");

        _lastScan = DateTime.MinValue;
        Wake();
        return (entfernt, geblieben);
    }

    /// <summary>
    /// Entfernt, was hier nicht liegen soll, sobald es anderswo angekommen ist.
    /// </summary>
    /// <remarks>
    /// Ein abgewaehlter Zweig ist nicht vom Abgleich ausgenommen. Er sagt nur,
    /// dass sein Inhalt auf diesem Geraet nicht liegen soll. Was dort ankommt,
    /// wird angekuendigt und uebertragen wie jede andere Datei; erst wenn die
    /// Platzhalter-Schwelle erreicht ist -- so viele andere Knoten fuehren sie
    /// vollstaendig -- verschwindet sie hier. Nicht als Platzhalter, sondern
    /// ganz, denn zu sehen sein soll sie ja gerade nicht.
    ///
    /// Angekuendigt wird das Entfernen nicht. Sonst loeschte die Gegenstelle
    /// die eben empfangene Datei sofort wieder. Die Vorkehrung dafuer steht in
    /// <see cref="NoteLocalDelete"/> und im Loeschdurchgang.
    ///
    /// Laeuft im Hintergrund mit, nicht nur beim Speichern der Auswahl. Eine
    /// Datei, die erst nach dem Abwaehlen hineinkopiert wird, braucht denselben
    /// Weg wie eine, die schon da war.
    /// </remarks>
    public (int Files, long Bytes) PruneExcluded()
    {
        // Leere Liste heisst: alles gehoert dazu. Dann gibt es nichts zu tun.
        if (_config.Included.Count == 0 || !Directory.Exists(_config.LocalPath)) return (0, 0);

        var anzahl = 0;
        long bytes = 0;

        // Was abgewaehlt ist und trotzdem liegen bleibt, weil eine der
        // Sicherungen es verbietet. Ohne diese Liste steht ein abgewaehlter
        // Zweig weiter im Ordner und niemand sagt warum.
        var geblieben = new List<string>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        foreach (var info in new DirectoryInfo(_config.LocalPath).EnumerateFiles("*", options))
        {
            if (NameOf(info.FullName) is not { } name) continue;
            if (_config.Includes(name)) continue;

            // Ein Platzhalter haelt keinen Inhalt. Ihn zu entfernen kann
            // nichts kosten, und die Pruefungen darunter schuetzen Inhalt --
            // nicht Namen.
            //
            // Ohne diese Ausnahme blieb ein abgewaehlter Zweig fuer immer
            // stehen, sobald die Gegenstelle seine Dateien auch nicht fuehrt:
            // hochladen unmoeglich, weil wir die Bytes nicht haben, entfernen
            // verboten, weil niemand sie hat. Ein Name ohne Inhalt, den
            // niemand loswird.
            var leer = ((uint)info.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0;

            // Die zweite Sperre, unabhaengig von der Oberflaeche: entfernt wird
            // nur, was die Platzhalter-Schwelle erreicht hat. Ein Fehler in der
            // Auswahl kostet dann Platz und keine Daten -- und genau so ein
            // Fehler hat einmal genuegt.
            if (!leer && !MayEvict(name)) { geblieben.Add(name); continue; }

            // Und nur, was in genau dieser Fassung angekuendigt ist.
            //
            // MayEvict fragt, ob die Gegenstelle eine Datei dieses Namens
            // fuehrt -- nicht, ob sie diesen Inhalt fuehrt. Wer sich darauf
            // verlaesst, loescht eine lokale Aenderung, die noch niemand
            // gesehen hat, weil zufaellig derselbe Name drueben liegt.
            if (!leer && !Angekuendigt(name, info)) { geblieben.Add(name); continue; }

            // Und der Beweis dafuer, gerechnet und nicht geschaetzt.
            if (!leer && !InhaltStimmt(name, info)) { geblieben.Add(name); continue; }

            // Und die dritte: eine eben erst angekuendigte Datei bleibt
            // liegen, bis die Gegenstelle Gelegenheit hatte, sie zu holen.
            //
            // Die Schwelle allein reicht dafuer nicht. Sie zaehlt Eintraege
            // mit Blockliste, und die Gegenstelle spiegelt unsere eigene
            // Ankuendigung samt Blockliste zurueck, lange bevor sie ein Byte
            // geholt hat. Wer das fuer einen Besitznachweis haelt, loescht die
            // Datei, bevor sie irgendwo ankommt.
            //
            // Ein besseres Merkmal gibt das Protokoll nicht her: BEP sagt dem
            // Sender nie, dass der Empfaenger fertig ist. Also wird gewartet.
            if (_announcedAt.TryGetValue(name, out var seit)
                && Environment.TickCount64 - seit < PruneDelayMs)
            {
                continue;
            }

            try
            {
                var laenge = info.Length;

                // Die Sperre haelt die eigene Loeschung von der Erkennung fern.
                // Der Beobachter meldet sie sonst als Aenderung von aussen.
                using (HoldHydration(name)) info.Delete();

                _removed.TryRemove(name, out _);
                _dirty.TryRemove(name, out _);

                _cache?.Forget(name);
                lock (_indexGate) _index?.ForgetLocal(name);

                anzahl++;
                bytes += laenge;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"[{FolderId}] \"{name}\" liess sich nicht entfernen: {ex.Message}");
            }
        }

        // Die Regel ist richtig -- entfernt wird nur, was anderswo
        // vollstaendig liegt --, aber sie muss sich erklaeren. Sonst sieht
        // eine Abwahl aus, als haette sie nicht gewirkt.
        //
        // Und zwar gerade dann, wenn nichts entfernt wurde. Die Zeile stand
        // versehentlich innerhalb der Bedingung darunter und schwieg damit in
        // genau dem Fall, in dem jemand eine Erklaerung sucht.
        if (geblieben.Count > 0)
            _log($"[{FolderId}] {geblieben.Count} abgewaehlte Dateien bleiben liegen: " +
                 string.Join(", ", geblieben.Take(5).Select(n => $"\"{n}\"")) +
                 (geblieben.Count > 5 ? $" und {geblieben.Count - 5} weitere" : "") +
                 " -- die Gegenstelle fuehrt sie nicht, Entfernen waere die letzte Kopie.");

        if (anzahl > 0)
        {
            LeereVerzeichnisse(_config.LocalPath);

            // Nicht "uebertragen": eine Datei, deren Bloecke die Gegenstelle
            // schon hatte, ist nie ueber die Leitung gegangen. Gesagt wird,
            // was feststeht -- sie liegt dort, also nicht mehr hier.
            _log($"[{FolderId}] {anzahl} Dateien liegen auf der Gegenstelle und " +
                 $"wurden hier entfernt ({bytes / (1024.0 * 1024.0):0.0} MB) -- " +
                 "sie sollen auf diesem Geraet nicht liegen.");

            // Und dem Datei-Manager sagen, dass sich hier etwas geaendert hat.
            //
            // Ohne diesen Anstoss stand ein abgewaehlter Zweig weiter im
            // Fenster, obwohl er von der Platte fort war, bis irgendetwas die
            // Ansicht zum Neulesen brachte -- und das sah aus, als haette das
            // Abwaehlen nicht gewirkt.
            AnsichtAuffrischen(_config.LocalPath);
        }

        return (anzahl, bytes);
    }

    /// <summary>
    /// Steht diese Datei in genau dieser Fassung im eigenen Bestand?
    /// </summary>
    /// <remarks>
    /// Der eigene Bestand haelt fest, was angekuendigt wurde. Weichen Groesse
    /// oder Zeit davon ab, liegt hier eine Aenderung, die noch niemand kennt
    /// -- und die darf weder entfernt noch stillschweigend ueberschrieben
    /// werden.
    /// </remarks>
    private bool Angekuendigt(string name, System.IO.FileInfo info)
    {
        lock (_indexGate)
        {
            if (_index is null || !_index.TryGetLocal(name, out var eigene)) return false;

            return !eigene.Deleted
                   && eigene.Size == info.Length
                   && eigene.ModifiedS == new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
        }
    }

    /// <summary>
    /// Stimmt der Inhalt auf der Platte mit dem ueberein, was angekuendigt
    /// wurde?
    /// </summary>
    /// <remarks>
    /// Groesse und Zeit sind eine Heuristik. Fuer die Frage, ob eine Datei neu
    /// zu bewerten ist, genuegt sie: irrt sie sich, wird einmal zu viel
    /// gerechnet. Fuer die Frage, ob eine Datei geloescht werden darf, genuegt
    /// sie nicht -- irrt sie sich, ist ein Inhalt fort, den niemand mehr hat.
    ///
    /// Also wird gelesen und gerechnet. Die Datei steht ohnehin vor dem
    /// Loeschen; ein Lesen mehr ist der billigste Teil daran.
    /// </remarks>
    private bool InhaltStimmt(string name, System.IO.FileInfo info)
    {
        BepFileInfo? eigene;
        lock (_indexGate)
            eigene = _index is not null && _index.TryGetLocal(name, out var e) ? e : null;

        if (eigene is null || eigene.Deleted || eigene.BlocksHash.IsEmpty) return false;

        try
        {
            using var content = new FileStream(
                info.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan);

            // Ein Platzhalter wuerde beim Lesen aus dem Netz geholt. Er hat
            // hier ohnehin nichts zu suchen: entfernt wird, was Inhalt haelt.
            if (IsPlaceholder(info.FullName)) return false;

            var (_, _, hash) = BlockList.For(content, info.Length);
            return eigene.BlocksHash.Span.SequenceEqual(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In Benutzung. Dann bleibt sie liegen, und zwar mit Absicht.
            return false;
        }
    }

    /// <summary>
    /// Liegt hier eine Aenderung, die noch nicht angekuendigt ist?
    /// </summary>
    /// <remarks>
    /// Ein Platzhalter zaehlt nicht: er haelt keinen Inhalt und kann nichts
    /// Ungesagtes enthalten.
    /// </remarks>
    internal bool NochNichtGesagt(string name)
    {
        var path = ResolveInside(name);
        if (path is null) return false;

        var info = new System.IO.FileInfo(path);
        if (!info.Exists) return false;
        if (((uint)info.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0) return false;

        return !Angekuendigt(name, info);
    }

    /// <summary>Raeumt Verzeichnisse weg, in denen nichts mehr steht.</summary>
    /// <remarks>
    /// Von unten nach oben, denn ein Verzeichnis wird erst leer, nachdem sein
    /// letztes Unterverzeichnis verschwunden ist. Die Wurzel bleibt: sie ist
    /// die Freigabe selbst.
    /// </remarks>
    private void LeereVerzeichnisse(string wurzel)
    {
        foreach (var pfad in Directory.EnumerateDirectories(wurzel))
        {
            try
            {
                LeereVerzeichnisse(pfad);

                if (NameOf(pfad) is not { } name) continue;
                if (_config.Includes(name, isDirectory: true)) continue;
                if (Directory.EnumerateFileSystemEntries(pfad).Any()) continue;

                // Unter der Sperre, und die Vermerke gleich hinterher fort.
                //
                // Sonst meldet der Beobachter das Entfernen als Loeschung, und
                // die geht hinaus: die Vorkehrung, die das fuer Dateien
                // verhindert, fragt MayEvict -- und das ist fuer ein
                // Verzeichnis immer falsch, es hat weder Groesse noch Bloecke.
                // Die Gegenstelle loeschte daraufhin das Verzeichnis samt
                // allem, was wir gerade hineingeschickt haben.
                using (HoldHydration(name)) Directory.Delete(pfad);

                _removed.TryRemove(name, out _);
                _dirty.TryRemove(name, out _);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In Benutzung. Beim naechsten Mal.
                _log($"[{FolderId}] \"{pfad}\" liess sich nicht entfernen: {ex.Message}");
            }
        }
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
                     "Der Ordner wird vollstaendig geprueft.");

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
                SweepOutgoing();
                PflegeBeobachter();

                // Erst hereinnehmen, dann hinausgeben. Andernfalls wuerde eine
                // Datei, die beide Seiten geaendert haben, als eigene Aenderung
                // angekuendigt, bevor der Konflikt bemerkt ist.
                ApplyIncoming();
                SettlePhase();
                SweepVersions();

                // Und die Zahlen fuer die Anzeige, falls sich der Index
                // bewegt hat. Nicht der Durchgang ueber den Ordner -- nur der
                // Lauf ueber den Index, der die Zahlen ohnehin liefert.
                ZahlenNachziehen();

                // Die Meldungen der Cloud-Files-Schicht sind der schnelle Weg,
                // aber nicht der einzige. Fuer eine Datei, die neu in den
                // Ordner kopiert wurde, kommt moeglicherweise keine Meldung:
                // sie war nie ein Platzhalter. Deshalb wird in Abstaenden
                // nachgesehen. Der Durchgang liest nur Attribute, Groesse und
                // Zeit, keine Inhalte.
                if (DateTime.UtcNow - _lastScan >= _rescanInterval)
                {
                    _lastScan = DateTime.UtcNow;
                    ScanLocal(quiet: true);

                    // "Vollstaendig lokal" wurde einmal beim Verbinden
                    // eingeloest. Ein Platzhalter, der danach entsteht -- eine
                    // neue Datei der Gegenstelle, ein Zweig, den jemand wieder
                    // angehakt hat, ein Versuch, der abgebrochen ist --, blieb
                    // fuer immer leer: als Rueckstand gezaehlt, ohne dass
                    // irgendein Handgriff daran etwas geaendert haette.
                    await FetchMissingAsync(ct).ConfigureAwait(false);
                    _lastFetch = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - _lastFetch >= FetchInterval)
                {
                    _lastFetch = DateTime.UtcNow;
                    await FetchMissingAsync(ct).ConfigureAwait(false);
                }

                // Oefter als der Durchgang ueber den Ordner: das Entfernen
                // liest nur Namen und Zeiten und wartet auf eine Bedingung,
                // die jederzeit eintreten kann.
                if (DateTime.UtcNow - _lastPrune >= PruneInterval)
                {
                    _lastPrune = DateTime.UtcNow;
                    PruneExcluded();
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
    /// <param name="fortschritt">
    /// Wird nach jedem betrachteten Namen gerufen. Das Rechnen der Blocklisten
    /// ist der teure Teil eines uebernommenen Ordners -- bei sechzig Gigabyte
    /// anderthalb Minuten. Ohne diese Meldung steht die Anzeige derweil still.
    /// </param>
    private async Task PublishAsync(CancellationToken ct, Action<int>? fortschritt = null)
    {
        if (_connections.IsEmpty || _index is null) return;
        if (IsPaused) return;

        // Ohne eigene Geraete-ID liesse sich kein Zaehler fortschreiben. Eine
        // Ankuendigung ohne eigenen Zaehler waere fuer die Gegenstelle
        // dieselbe Version wie zuvor.
        if (OwnDeviceId == Bep.DeviceId.Empty) return;

        var batch = new List<BepFileInfo>();
        var bytes = 0;

        var betrachtet = 0;

        foreach (var name in _dirty.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            fortschritt?.Invoke(++betrachtet);

            // Waehrend der Sammelfrist kann eine Hydration begonnen haben.
            if (IsHydrating(name)) continue;

            _dirty.TryRemove(name, out _);

            if (Evaluate(name) is not { } file) continue;

            batch.Add(file);
            bytes += file.CalculateSize();

            if (batch.Count < BatchFiles && bytes < BatchBytes) continue;

            await FlushAsync(batch, ct).ConfigureAwait(false);
            bytes = 0;
        }

        foreach (var file in Deletions())
        {
            batch.Add(file);
            bytes += file.CalculateSize();

            if (batch.Count < BatchFiles && bytes < BatchBytes) continue;

            await FlushAsync(batch, ct).ConfigureAwait(false);
            bytes = 0;
        }

        await FlushAsync(batch, ct).ConfigureAwait(false);
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
        var path = ResolveInside(name);
        if (path is null) return Done(name);

        if (AnnouncedName(name) is not { } announced) return Done(name);

        // Verzeichnisse zuerst. Fuer sie ist FileInfo.Exists falsch, die
        // Pruefung darunter verwuerfe sie also stillschweigend -- und ein
        // leerer Ordner entstuende auf der Gegenseite nie.
        if (Directory.Exists(path)) return EvaluateDirectory(name, announced, path);

        var info = new System.IO.FileInfo(path);

        // Eine fehlende Datei ist keine Loeschung. Sie kann verschoben oder
        // umbenannt worden sein, und der Zweig kann von einem Laufwerk
        // stammen, das gerade nicht da ist.
        if (!info.Exists) return Done(name);

        // Ein Platzhalter hat den Inhalt nicht. Angekuendigt wird nur, was
        // vollstaendig hier liegt -- ausser er ist gerade hierher verschoben
        // worden. Dann kennen wir seine Bloecke, auch ohne sie zu haben.
        //
        // Der Gegenstelle sieht das nach einem unvollstaendigen Knoten aus:
        // Syncthing rechnet die Vollstaendigkeit eines Geraets aus dem Index,
        // den es ankuendigt, und zeigt uns dauerhaft mit wenigen Prozent. Das
        // ist der Preis, und er ist richtig bezahlt.
        //
        // Denn die Platzhalter-Schwelle ist eine gegenseitige Zusage. Wer eine
        // Datei ankuendigt, sagt damit: bei mir ist sie zu holen. Andere geben
        // daraufhin ihren Speicherplatz frei. Kuendigten wir an, was wir nicht
        // halten, gaebe ein anderer Knoten seine Kopie im Vertrauen auf eine
        // auf, die es nicht gibt -- und bei Schwelle 1 waere die letzte echte
        // Kopie fort, ohne dass eine Seite einen Fehler saehe.
        if (((uint)info.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0)
        {
            // Der Vermerk aus dem Rueckruf ist der schnelle Weg. Fehlt er --
            // etwa weil das Verschieben in einer frueheren Sitzung geschah --,
            // steht der urspruengliche Name in der Datei selbst.
            // Der Vermerk aus dem Rueckruf sagt: gerade eben verschoben. Dann
            // gehoert die Loeschung des alten Namens dazu.
            var geradeEben = _renamedFrom.TryRemove(name, out var vorher);

            // Sonst die Identitaet aus der Datei. Sie sagt nur, woher der
            // Platzhalter einmal kam -- und das kann Monate her sein.
            vorher ??= CloudFilterMount.OriginalName(path, Einmal(name));

            if (vorher is null || vorher == name) return Done(name);

            return EvaluateMoved(name, announced, vorher, info, geradeEben);
        }

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
            // freigegeben worden sein. Das Oeffnen allein holt sie noch nicht,
            // das Lesen wuerde es.
            if (IsPlaceholder(path)) return Done(name);

            (blockSize, blocks, blocksHash) = BlockList.For(content, length);

            // Hier steht fest, dass die Datei ihren Inhalt lokal haelt: der
            // Platzhalter waere oben ausgestiegen. Der Durchgang ueber den
            // Ordner faende es auch, aber erst in der naechsten Minute.
            _cache?.NoteContent(name, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Eine Datei, die gerade geschrieben wird, laesst sich oft nicht
            // in einem Zug lesen. Sie kommt zurueck in die Liste.
            Retry(name, ex);
            return null;
        }

        var known = LocalCopy(announced);

        // Die wichtigste Pruefung. Gleicher Inhalt heisst: keine neue Version,
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
            // Zaehlung. Angekuendigt haben wir diese Version nie.
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

    /// <summary>
    /// Kuendigt ein Verzeichnis an.
    /// </summary>
    /// <remarks>
    /// Ein Verzeichnis traegt keinen Inhalt und keine Blockliste. Angekuendigt
    /// wird es trotzdem, denn ein leerer Ordner ist eine Aussage: ohne ihn
    /// entsteht er auf der Gegenseite nur dann, wenn spaeter eine Datei darin
    /// landet.
    /// </remarks>
    private BepFileInfo? EvaluateDirectory(string name, string announced, string path)
    {
        long modified;
        try
        {
            modified = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Done(name);
        }

        var known = LocalCopy(announced);

        // Schon angekuendigt: dann gibt es nichts mehr zu sagen.
        //
        // Hier stand ein Vergleich der Zeit, und das war eine Rueckkopplung.
        // Die Zeit eines Verzeichnisses aendert sich, sobald irgendetwas
        // darin entsteht oder verschwindet -- also bei jedem Platzhalter, den
        // wir selbst anlegen, bei jeder Datei, die wir selbst holen, bei
        // jeder ".synct-neu", die wir selbst daneben schreiben.
        //
        // Jede dieser Aenderungen wurde angekuendigt, die Gegenstelle nahm
        // sie auf und schickte sie zurueck, und die naechste Datei im selben
        // Ordner begann von vorn. Bei achtundvierzigtausend Verzeichnissen
        // wird daraus ein Strom, der nicht abreisst: Sequenznummern in
        // Millionenhoehe fuer sechsundsechzigtausend Dateien.
        //
        // Die Zeit eines Verzeichnisses traegt fuer niemanden eine
        // Information. Was darin liegt, wird fuer sich abgeglichen; das
        // Verzeichnis selbst ist nur die Aussage, dass es da ist.
        if (known is not null && !known.Deleted && known.Type == FileInfoType.Directory)
            return Done(name);

        // Die Gegenstelle fuehrt es bereits. Dann ist es von dort gekommen und
        // wird nur in den eigenen Bestand uebernommen.
        if (known is null && PeerCopy(announced) is { Deleted: false, Type: FileInfoType.Directory } peer)
        {
            var adopted = peer.Clone();
            adopted.Sequence = 0;
            Store(adopted, StateClean);
            return Done(name);
        }

        var file = new BepFileInfo
        {
            Name = announced,
            Type = FileInfoType.Directory,
            Size = 0,
            Permissions = 0,
            NoPermissions = true,
            ModifiedS = modified,
            ModifiedNs = 0,
            Deleted = false,
            Invalid = false,
            Sequence = NextSequence(),
            ModifiedBy = OwnDeviceId.ShortId(),
            Version = NextVersion(known?.Version ?? PeerCopy(announced)?.Version)
        };

        Store(file, StateAnnounced);
        _attempts.TryRemove(name, out _);
        return file;
    }

    /// <summary>
    /// Kuendigt einen Platzhalter an, der hierher verschoben wurde.
    /// </summary>
    /// <remarks>
    /// Ein Platzhalter haelt seinen Inhalt nicht, aber wir kennen ihn: die
    /// Blockliste steht unter dem alten Namen im Bestand. Sie wird
    /// uebernommen, und damit ist die Datei unter dem neuen Namen angekuendigt,
    /// ohne dass ein einziges Byte ueber die Verbindung geht -- die Gegenstelle
    /// hat die Bloecke bereits und schreibt die Datei aus ihrer eigenen Kopie.
    ///
    /// Die Groesse muss uebereinstimmen. Sonst ist es nicht dieselbe Datei,
    /// und eine Ankuendigung mit fremden Bloecken waere eine Falschaussage.
    /// </remarks>
    /// <param name="geradeEben">
    /// Ob die Zuordnung aus dem Rueckruf stammt, das Verschieben also eben
    /// geschehen ist. Nur dann gehoert die Loeschung des alten Namens dazu.
    ///
    /// Die Identitaet allein reicht dafuer nicht: sie haftet dauerhaft an der
    /// Datei. Ein Platzhalter, der vor Monaten verschoben wurde, nennt seinen
    /// Ursprungsnamen noch heute -- und unter dem kann laengst wieder eine
    /// andere Datei liegen. Sie zu loeschen waere kein Nachziehen, sondern ein
    /// Uebergriff.
    /// </param>
    private BepFileInfo? EvaluateMoved(
        string name, string announced, string vorher, System.IO.FileInfo info,
        bool geradeEben)
    {
        if (AnnouncedName(vorher) is not { } alt) return Done(name);

        // Der eigene Eintrag zum alten Namen kann eine Loeschmarke sein: null
        // Bytes, keine Bloecke. Sie ist vorhanden und traegt trotzdem nichts.
        // Genommen wird, was eine Blockliste hat.
        static bool Traegt(BepFileInfo? f) => f is { Deleted: false } && f.Blocks.Count > 0;

        var eigene = LocalCopy(alt);
        var fremde = PeerCopy(alt);
        var quelle = Traegt(eigene) ? eigene : Traegt(fremde) ? fremde : eigene ?? fremde;

        if (quelle is null)
        {
            Einmal(name)($"[{FolderId}] \"{name}\" kam von \"{alt}\" -- dazu ist nichts bekannt.");
            return Done(name);
        }

        if (quelle.Deleted || quelle.Size != info.Length || quelle.Blocks.Count == 0)
        {
            Einmal(name)($"[{FolderId}] \"{name}\" kam von \"{alt}\", passt aber nicht: " +
                         $"{quelle.Size} statt {info.Length} Bytes, {quelle.Blocks.Count} Bloecke" +
                         (quelle.Deleted ? ", geloescht" : "") + ".");
            return Done(name);
        }

        var modifiedUtc = info.LastWriteTimeUtc;

        var file = new BepFileInfo
        {
            Name = announced,
            Type = FileInfoType.File,
            Size = quelle.Size,
            Permissions = 0,
            NoPermissions = true,
            ModifiedS = new DateTimeOffset(modifiedUtc).ToUnixTimeSeconds(),
            ModifiedNs = (int)((modifiedUtc.Ticks - DateTime.UnixEpoch.Ticks) % TimeSpan.TicksPerSecond * 100),
            Deleted = false,
            Invalid = false,
            Sequence = NextSequence(),
            ModifiedBy = OwnDeviceId.ShortId(),
            Version = NextVersion(LocalCopy(announced)?.Version ?? PeerCopy(announced)?.Version),
            BlockSize = quelle.BlockSize,
            BlocksHash = quelle.BlocksHash
        };

        file.Blocks.AddRange(quelle.Blocks);

        Store(file, StateAnnounced);
        _attempts.TryRemove(name, out _);

        // Jetzt erst die Loeschung des alten Namens, und nur bei einem eben
        // geschehenen Verschieben. Sie geht im selben Durchgang hinaus, aber
        // nach der Ankuendigung: PublishAsync bewertet zuerst die Vermerke und
        // sammelt danach die Loeschungen ein.
        if (geradeEben) _removed[alt] = 0;

        _log($"[{FolderId}] \"{alt}\" liegt jetzt unter \"{announced}\" -- " +
             "angekuendigt mit den bekannten Bloecken, ohne Uebertragung.");

        return file;
    }

    /// <summary>
    /// Ein Meldeweg, der je Name nur einmal schreibt.
    /// </summary>
    /// <remarks>
    /// Die Bewertung laeuft jede Minute ueber dieselben Namen. Ein Grund, der
    /// sich nicht aendert, gehoert einmal ins Protokoll und nicht sechzigmal
    /// je Stunde.
    /// </remarks>
    private Action<string> Einmal(string name)
        => text => { if (_warned.TryAdd(name, 0)) _log(text); };

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
    /// Schreibt die Version fort: nur der eigene Zaehler wird beruehrt.
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
    /// Gegenstelle eine andere Version.
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

            // Dritte Sicherung: was wir ausserhalb der Auswahl selbst
            // entfernt haben, ist keine Loeschung. Siehe NoteLocalDelete.
            if (!_config.Includes(name) && MayEvict(name)) continue;

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
    /// <summary>
    /// Der gesamte eigene Bestand, mit dem Stapel darin.
    /// </summary>
    /// <remarks>
    /// Fuer den vollstaendigen Index. Der Stapel steht in aller Regel schon in
    /// der Datenbank -- Evaluate schreibt vor dem Senden --, aber verlassen
    /// wird sich nicht darauf: eine Datei zweimal im Index waere eine
    /// widerspruechliche Aussage, eine fehlende ein Verlust.
    /// </remarks>
    private List<BepFileInfo> Bestand(List<BepFileInfo> batch)
    {
        List<BepFileInfo> gespeichert;
        lock (_indexGate) gespeichert = [.. _index?.LocalFrom(0) ?? []];

        var namen = new HashSet<string>(batch.Select(f => f.Name), StringComparer.Ordinal);

        var alle = new List<BepFileInfo>(gespeichert.Count + batch.Count);

        // Nur was eine eigene Sequenznummer hat, gehoert in den Index.
        //
        // Eine uebernommene Datei traegt die Null: sie stammt von der
        // Gegenstelle, wir haben sie nie angekuendigt, und der Eintrag haelt
        // nur fest, dass wir sie haben. Im Protokoll ist die Sequenznummer
        // aber eindeutig und aufsteigend. Mehrere Nullen in einer Nachricht
        // sind darum kein Schoenheitsfehler, sondern ein Formfehler --
        // Syncthing beantwortet ihn mit "duplicate remote sequence number 0"
        // und schliesst die Verbindung.
        //
        // Genau deshalb brach die Leitung ab, sobald ein Ordner mit
        // vorhandenen Dateien uebernommen wurde: die Aufnahme des Bestands
        // erzeugt lauter Eintraege mit Nummer null.
        alle.AddRange(gespeichert.Where(f => f.Sequence > 0 && !namen.Contains(f.Name)));
        alle.AddRange(batch);

        return alle;
    }

    /// <summary>Der Versionsvektor in einer Zeile.</summary>
    private static string Kurz(Vector? version)
        => version is null || version.Counters.Count == 0
            ? "leer"
            : string.Join(", ", version.Counters.Select(c => $"{c.Id:x}:{c.Value}"));

    /// <summary>
    /// Schickt den Stapel an alle beteiligten Gegenstellen.
    /// </summary>
    /// <remarks>
    /// Derselbe Stapel an jede: die Ankuendigung ist eine Aussage ueber
    /// unseren Bestand und nicht ueber die Beziehung zu einer Gegenstelle.
    /// Verschieden ist nur die Verpackung -- wer noch keinen Index bekommen
    /// hat, bekommt einen, die uebrigen einen Nachtrag mit der Nummer ihres
    /// eigenen Vorgaengers.
    ///
    /// Scheitert eine Verbindung, laufen die uebrigen weiter. Der Ausfall einer
    /// Gegenstelle ist kein Grund, den anderen nichts zu sagen.
    /// </remarks>
    private async Task FlushAsync(List<BepFileInfo> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        var last = batch.Max(f => f.Sequence);
        var erreicht = 0;

        // Vor dem Senden aufschreiben, nicht danach. Bricht die Verbindung
        // ab, ist gerade das der Inhalt, den man sehen will -- und der stand
        // bisher nirgends.
        if (batch.Count <= AnnounceDetails)
        {
            foreach (var eintrag in batch)
            {
                _log($"[{FolderId}]   {(eintrag.Deleted ? "geloescht" : "vorhanden")} " +
                     $"\"{eintrag.Name}\", {eintrag.Size} B, {eintrag.Blocks.Count} Bloecke, " +
                     $"Blockgroesse {eintrag.BlockSize}, Typ {eintrag.Type}, " +
                     $"Sequenz {eintrag.Sequence}, Version {Kurz(eintrag.Version)}");
            }
        }

        foreach (var (device, connection) in _connections)
        {
            try
            {
                if (_indexSentTo.TryGetValue(device, out var gesendet) && gesendet)
                {
                    var update = new BepIndexUpdate
                    {
                        Folder = FolderId,
                        LastSequence = last,
                        PrevSequence = _lastSentTo.GetValueOrDefault(device)
                    };
                    update.Files.AddRange(batch);

                    _log($"[{FolderId}] -> IndexUpdate an {device[..7]}: " +
                         $"{batch.Count} Dateien, prev {update.PrevSequence}, bis {last}.");

                    await connection.SendIndexUpdateAsync(update, ct).ConfigureAwait(false);
                }
                else
                {
                    // Ein Index ist die Aussage "das ist mein vollstaendiger
                    // Bestand zu diesem Ordner". Nur den gerade geaenderten
                    // Stapel hineinzuschreiben hiesse, der Gegenstelle zu
                    // sagen, unser Ordner bestehe aus diesen paar Dateien --
                    // und alles frueher Angekuendigte waere fuer sie fort.
                    //
                    // Genommen wird deshalb der gesamte eigene Bestand. Der
                    // Stapel steckt darin, denn Evaluate hat ihn vor dem
                    // Senden geschrieben.
                    var alle = Bestand(batch);

                    var index = new BepIndex { Folder = FolderId, LastSequence = last };
                    index.Files.AddRange(alle);

                    _log($"[{FolderId}] -> Index an {device[..7]}: " +
                         $"{alle.Count} Dateien (Bestand), bis {last}.");

                    await connection.SendIndexAsync(index, ct).ConfigureAwait(false);
                    _indexSentTo[device] = true;
                }

                _lastSentTo[device] = last;
                erreicht++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Und die Verbindung herausnehmen. Ein geschlossener Socket wird
                // nicht dadurch besser, dass man ihn weiter beschreibt -- ohne
                // dies scheiterte jede Ankuendigung von nun an, immer mit
                // derselben Zeile. Der PeerHost haengt beim Verbinden eine
                // neue ein.
                DropConnection(device);
                LineLost?.Invoke(device);

                // Und die Dateien zurueck in die Vermerke.
                //
                // Der eigene Eintrag steht zu diesem Zeitpunkt schon auf
                // "angekuendigt" -- Evaluate schreibt ihn, bevor gesendet
                // wird, damit eine vergebene Sequenznummer nicht verlorengeht.
                // Scheitert das Senden, gilt die Datei damit als erledigt,
                // und der Vorfilter uebergeht sie fortan: Groesse und Zeit
                // passen ja zum eigenen Eintrag. Sie waere nie wieder
                // angekuendigt worden.
                foreach (var eintrag in batch)
                {
                    if (eintrag.Deleted)
                    {
                        _removed[eintrag.Name] = 0;
                        continue;
                    }

                    _dirty[eintrag.Name] = 0;

                    // Ohne dies faellt sie beim naechsten Mal durch den
                    // Vorfilter: an der Datei hat sich nichts geaendert, nur
                    // das Wissen der Gegenstelle ueber sie.
                    _force[eintrag.Name] = 0;
                }

                Wake();

                _log($"[{FolderId}] Ankuendigung fehlgeschlagen, Verbindung verworfen, " +
                     $"{batch.Count} Dateien erneut vorgemerkt: {ex.Message}");
            }
        }

        if (erreicht > 0)
        {
            var jetzt = Environment.TickCount64;
            foreach (var file in batch) _announcedAt[file.Name] = jetzt;

            _log($"[{FolderId}] {batch.Count} Aenderungen angekuendigt, Sequenz bis {last} " +
                 $"({erreicht} Gegenstelle(n)).");

        }

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

        // Auch der eigene Bestand geht in die Zahlen ein -- eine Loeschung,
        // die wir gerade angekuendigt haben, ebenso wie eine Datei, die wir
        // uebernommen haben.
        _zahlenVeraltet = true;

        // Und ein Durchgang, denn das Nachziehen allein genuegt nicht: es
        // rechnet auf dem Bestand des letzten Durchgangs, und der kennt die
        // gerade angekuendigte Groesse und Zeit noch nicht. Ohne ihn stuende
        // die eigene Aenderung bis zum naechsten stuendlichen Durchgang mit
        // ihren alten Zahlen da.
        _lastScan = DateTime.MinValue;
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
