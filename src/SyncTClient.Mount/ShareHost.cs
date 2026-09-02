using System.Security.Cryptography;
using System.Collections.Concurrent;
using SyncTClient.Bep;
using SyncTClient.Vfs;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;
using BepRequest = SyncTClient.Bep.Proto.Request;
using ErrorCode = SyncTClient.Bep.Proto.ErrorCode;
using FileInfoType = SyncTClient.Bep.Proto.FileInfoType;

namespace SyncTClient.Mount;

public enum ShareState
{
    Gestoppt,
    Wartet,
    Bereit,
    Pausiert,
    Fehler
}

/// <summary>
/// Woran eine Freigabe beim Hochfahren gerade arbeitet.
/// </summary>
/// <remarks>
/// Der Abgleich nach einem Neustart dauert bei grossen Freigaben lange, und
/// die Teilschritte sind ungleich lang. Ohne die Benennung der Phase waere ein
/// Fortschrittsbalken irrefuehrend: er wuerde stillzustehen scheinen, waehrend
/// tatsaechlich ein anderer Schritt laeuft.
/// </remarks>
public enum SyncPhase
{
    Ruht,
    Index,
    Platzhalter,
    Cache,
    Inhalte,

    /// <summary>
    /// Fertig war es schon, aber die Gegenstelle hat wieder etwas zu sagen.
    /// </summary>
    /// <remarks>
    /// Der Abgleich hoert nicht auf, wenn er einmal durchgelaufen ist. Solange
    /// noch Ankuendigungen eintreffen, die Arbeit machen, oder die Gegenstelle
    /// selbst noch laedt, ist der Stand nicht der gemeinsame -- und "fertig"
    /// waere schlicht falsch.
    /// </remarks>
    Abgleich,

    Fertig
}

/// <summary>
/// Ein Share: Index, Platzhalter, Cache, Vorschaubilder.
/// </summary>
/// <remarks>
/// Die Verbindung gehoert nicht zu dieser Klasse. Sie liegt bei
/// <see cref="PeerHost"/> und wird von allen Ordnern derselben Gegenstelle
/// gemeinsam genutzt. Syncthing macht es genauso: eine Verbindung je Geraet,
/// nicht je Ordner.
/// </remarks>
public sealed partial class ShareHost : IAsyncDisposable, IContentSource
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: der Inhalt liegt noch nicht lokal.</summary>
    private const uint RecallOnDataAccess = 0x0040_0000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: schon das Oeffnen holt den Inhalt.</summary>
    private const uint RecallOnOpen = 0x0004_0000;

    /// <summary>FILE_ATTRIBUTE_OFFLINE: der Inhalt liegt woanders.</summary>
    private const uint Offline = 0x1000;

    /// <summary>Groesster Block, den das Protokoll kennt: 16 MiB.</summary>
    private const int MaximumRequestSize = 16 << 20;

    /// <summary>Gibt an, ob von dieser Datei nur der Name lokal vorliegt.</summary>
    /// <remarks>
    /// Die drei Attribute bedeuten dasselbe: der Inhalt liegt woanders. Ein
    /// Lesezugriff holt ihn. Beim Bedienen einer Anfrage waere das ein
    /// Herunterladen, nur um die Bytes zurueckzugeben. Im Zweifel gilt die
    /// Datei als Platzhalter, denn eine Absage kostet nichts, ein
    /// irrtuemlicher Zugriff auf das Netz dagegen schon.
    /// </remarks>
    private static bool IsPlaceholder(string path)
    {
        try
        {
            var attributes = (uint)new System.IO.FileInfo(path).Attributes;
            return (attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Wieviele Dateien gleichzeitig geholt werden. Die uebrigen warten
    /// sichtbar in der Warteschlange. Ohne diese Schranke gibt es keine
    /// Warteschlange, sondern beliebig viele gleichzeitige Uebertragungen,
    /// die sich die Bandbreite teilen.
    /// </summary>
    private const int ConcurrentHydrations = 3;

    private readonly ShareConfig _config;
    private readonly AppConfig _app;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _hydrationGate = new(ConcurrentHydrations);
    private readonly SemaphoreSlim _indexArrived = new(0);

    /// <summary>
    /// Die Verbindungen zu den beteiligten Gegenstellen, je Geraet eine.
    /// </summary>
    /// <remarks>
    /// Ein Ordner gehoert nicht einer Gegenstelle, sondern hat Teilnehmer.
    /// Angekuendigt wird an alle; geholt wird bei einer, die den Inhalt hat.
    /// </remarks>
    private readonly ConcurrentDictionary<string, BepConnection> _connections =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Irgendeine Verbindung. Fuer alles, was jede beantworten kann.</summary>
    private BepConnection? AnyLine => _connections.Values.FirstOrDefault();

    /// <summary>
    /// Die Verbindung zu einer Gegenstelle, die diese Datei fuehrt.
    /// </summary>
    /// <remarks>
    /// Bei irgendeiner zu fragen waere ein Fehlschlag mit Ansage: eine
    /// Gegenstelle, die den Namen nur kennt, liefert keine Bloecke. Kennt
    /// keine der verbundenen die Datei, bleibt der Versuch bei der ersten --
    /// dann ist die Absage die Auskunft.
    /// </remarks>
    private BepConnection? LineFor(string name)
    {
        if (_connections.IsEmpty) return null;
        if (_index is null) return AnyLine;

        List<string> halter;
        lock (_indexGate) halter = [.. _index.HolderDevices(name)];

        foreach (var device in halter)
            if (_connections.TryGetValue(device, out var line)) return line;

        return AnyLine;
    }
    private PersistentFolderIndex? _index;
    private CloudFilterMount? _mount;
    private HydrationCache? _cache;
    private ThumbnailStore? _thumbnails;

    /// <summary>
    /// Begrenzt, wie viele Dateikoepfe gleichzeitig unterwegs sind.
    /// </summary>
    /// <remarks>
    /// Der Explorer fragt einen ganzen Ordner auf einmal ab. Ungebremst
    /// waeren das hunderte gleichzeitiger Anfragen. Ein Doppelklick, mit dem
    /// tatsaechlich eine Datei geoeffnet werden soll, muesste dahinter
    /// warten.
    /// </remarks>
    private readonly SemaphoreSlim _thumbnailGate = new(6);

    private int _thumbnailsMade;

    private string? _syncRootId;
    private ShareState _state = ShareState.Gestoppt;

    public ShareHost(ShareConfig config, AppConfig app, Action<string> log)
    {
        _config = config;
        _app = app;
        _log = log;
    }

    public string FolderId => _config.FolderId;
    public ShareConfig Config => _config;

    public ShareState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(value); }
    }

    public bool IsPaused { get; private set; }

    public event Action<ShareState>? StateChanged;
    /// <summary>
    /// Eine Verbindung liess sich nicht mehr beschreiben. Der Parameter ist das
    /// Geraet.
    /// </summary>
    /// <remarks>
    /// Ein geschlossener Socket faellt beim Schreiben auf, nicht unbedingt
    /// beim Lesen: ein halb offener Anschluss laesst den Leser warten, bis
    /// eine Zeitschranke greift. Wer schreibt, merkt es sofort -- und der
    /// PeerHost muss es erfahren, sonst gilt die Gegenstelle weiter als
    /// verbunden, waehrend sie es nicht ist.
    /// </remarks>
    public event Action<string>? LineLost;

    public event Action<TransferInfo>? TransferStarted;
    public event Action<TransferInfo>? TransferFinished;

    /// <summary>Eine Datei liess sich nicht holen, weil eine Grenze erreicht ist.</summary>
    public event Action<CacheLimitHit>? LimitReached;
    public event Action? CacheChanged;
    /// <summary>Meldet, wie viele Vorschauen bisher auf Anforderung entstanden sind.</summary>
    public event Action<int>? ThumbnailProduced;

    /// <summary>Meldet den Fortschritt des Abgleichs.</summary>
    public event Action? SyncProgressChanged;

    public SyncPhase Phase { get; private set; } = SyncPhase.Ruht;

    /// <summary>Erledigte Einheiten der laufenden Phase.</summary>
    public int PhaseDone { get; private set; }

    /// <summary>
    /// Erwartete Einheiten der laufenden Phase, oder 0 wenn unbekannt.
    /// </summary>
    /// <remarks>
    /// Beim ersten Abgleich ist vorab nicht bekannt, wie viele Eintraege der
    /// Index enthaelt, denn er kommt in Stapeln. Statt einer geschaetzten
    /// Zahl steht hier 0, und die Oberflaeche zeigt dafuer einen unbestimmten
    /// Balken.
    /// </remarks>
    public int PhaseTotal { get; private set; }

    private void SetPhase(SyncPhase phase, int done = 0, int total = 0)
    {
        Phase = phase;
        PhaseDone = done;
        PhaseTotal = total;
        SyncProgressChanged?.Invoke();
    }

    // ------------------------------------------------------------ Zahlen

    public int IndexCount => _index?.Count ?? 0;
    public long IndexBytes => _index?.TotalBytes ?? 0;
    /// <summary>
    /// Der Stand, bis zu dem wir von dieser Gegenstelle gehoert haben.
    /// </summary>
    /// <remarks>
    /// Je Gegenstelle eine eigene Zaehlung. Sie vergibt ihre Sequenznummern
    /// selbst und weiss nichts von denen der anderen.
    /// </remarks>
    public long MaxSequenceFor(string device) => _index?.MaxSequenceOf(device) ?? 0;

    public ulong PeerIndexIdFor(string device) => _index?.PeerIndexIdOf(device) ?? 0;

    /// <summary>Unsere eigene IndexId zu diesem Ordner. Sie bleibt ueber Neustarts hinweg dieselbe.</summary>
    public ulong OwnIndexId => _index?.OwnIndexId ?? 0;

    /// <summary>Wie weit unser eigener Index reicht.</summary>
    public long LocalSequence => _index?.LocalSequence ?? 0;
    public long CacheUsedBytes => _cache?.UsedBytes ?? 0;
    public long CacheMaxBytes => _cache?.MaxBytes ?? 0;
    public int CacheFileCount => _cache?.FileCount ?? 0;
    public (int Count, long Bytes) ThumbnailUsage() => _thumbnails?.Usage() ?? (0, 0);

    /// <summary>Was diese Freigabe seit dem Start empfangen hat.</summary>
    /// <remarks>
    /// Die Verbindung zaehlt nur je Gegenstelle. Fuer eine Spalte je Freigabe
    /// wird dieser Zaehler gebraucht. Er enthaelt Hydration und
    /// Vorschau-Koepfe, also alles, was wegen dieser Freigabe uebertragen
    /// wurde.
    /// </remarks>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Was diese Freigabe seit dem Start ausgeliefert hat.</summary>
    /// <remarks>
    /// Solange der Client nur las, waere die Spalte dauerhaft null gewesen und
    /// haette nichts gesagt. Seit er Bloecke beantwortet, ist sie die
    /// Gegenprobe: sie zeigt, dass die andere Seite tatsaechlich bei uns holt.
    /// </remarks>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Wann zuletzt etwas fuer diese Freigabe ankam.</summary>
    public DateTime? LastTransfer { get; private set; }

    // -------------------------------------------------------- Replikation

    /// <summary>
    /// Wie viele andere Knoten diese Datei vollstaendig fuehren.
    /// </summary>
    /// <remarks>
    /// Ein BEP-Index ist pro Datei alles oder nichts: wer eine Datei
    /// auffuehrt, haelt sie vollstaendig. Unvollstaendige Dateien liegen als
    /// temporaere Dateien daneben und stehen nicht im Index. Deshalb genuegt
    /// die Pruefung, ob die Datei mit Inhalt im Index vorkommt. Ein ueber
    /// mehrere Knoten verteilter Teilbestand einer einzelnen Datei kommt
    /// nicht vor.
    ///
    /// Zurzeit ist je Freigabe genau eine Gegenstelle eingetragen, das
    /// Ergebnis ist also 0 oder 1. Die Signatur laesst groessere Werte
    /// bereits zu.
    /// </remarks>
    public int HoldersOf(string relativePath)
    {
        if (_index is null) return 0;

        // Auch hier die Sperre. Der Aufruf kommt aus dem Freigeben von Speicherplatz, und das
        // laeuft in einem anderen Faden als der Hintergrundlauf.
        lock (_indexGate) return _index.Holders(relativePath);
    }

    /// <summary>
    /// Ob eine Datei nach der Ankuendigung der Gegenstelle wiederbeschaffbar
    /// ist. Nur dann darf ihr Speicherplatz hier freigegeben werden.
    /// </summary>
    private bool MayEvict(string relativePath)
    {
        var wanted = _app.MinimumCopies;
        if (wanted <= 0) return true;
        if (HoldersOf(relativePath) >= wanted) return true;

        // Eine leere Datei erreicht die Schwelle sonst nie: HasContent
        // verlangt Bloecke, und die hat sie nicht. Sie bliebe als einzige
        // fuer immer hier stehen. Zu verlieren ist dabei nichts, null Bytes
        // sind null Bytes.
        //
        // Massgeblich ist die eigene Groesse, nicht die angekuendigte. Die
        // Gegenstelle setzt die Groesse auch dann auf null, wenn sie eine
        // grosse Datei nur kennt und nicht haelt; wer sich darauf verliesse,
        // gaebe den Platz der letzten Kopie einer Datei frei.
        return LeerHier(relativePath) && LeerDortAuch(relativePath);
    }

    private bool LeerHier(string relativePath)
    {
        try
        {
            var info = new System.IO.FileInfo(LocalPathOf(relativePath));
            return info.Exists && info.Length == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool LeerDortAuch(string relativePath)
    {
        if (_index is null) return false;

        lock (_indexGate)
            return _index.TryGet(relativePath, out var file) && !file.Deleted && file.Size == 0;
    }

    /// <summary>Gibt an, ob diese Ankuendigung Inhalt fuehrt und nicht nur einen Namen.</summary>
    /// <remarks>
    /// <c>setNoContent()</c> in Syncthing streicht genau diese beiden Felder.
    /// Eine Ankuendigung ohne Bloecke bedeutet, dass die Gegenstelle die Datei
    /// kennt, sie aber nicht selbst vorhaelt.
    /// </remarks>
    private static bool HasContent(BepFileInfo file)
        => !file.Deleted && file.Size > 0 && file.Blocks.Count > 0;

    /// <summary>
    /// Wie viele erreichbare Knoten diese Freigabe fuehren. Der Wert dient
    /// der Anzeige.
    /// </summary>
    /// <remarks>
    /// Der Wert ist eine Untergrenze und keine vollstaendige Aussage ueber
    /// das Netz. Ueber Knoten, mit denen gerade keine Verbindung besteht, ist
    /// nichts bekannt.
    /// </remarks>
    private bool _kenntEtwas;
    private DateTime _kenntGeprueft = DateTime.MinValue;

    /// <summary>
    /// Wie viele erreichbare Knoten den Inhalt vorhalten koennen.
    /// </summary>
    /// <remarks>
    /// Hier stand "_index.Count > 0", und das ist ein SELECT COUNT(DISTINCT
    /// name) ueber die ganze Tabelle. Gelesen wird die Zahl aus der Tabelle
    /// der Oberflaeche, viermal je Zeile und Sekunde -- bei acht Freigaben
    /// also zweiunddreissig vollstaendige Zaehlungen in der Sekunde, eine
    /// davon ueber hundertvierzehntausend Zeilen, waehrend derselbe Index
    /// gerade beschrieben wird.
    ///
    /// Der Sekundentakt der Oberflaeche brauchte dadurch sieben Sekunden.
    /// Gemessen, nicht geraten: "Takt 7762 ms: ... Zeilen 7761 ...".
    ///
    /// Gefragt ist ohnehin nur, ob der Index etwas fuehrt. Das beantwortet
    /// EXISTS beim ersten Treffer -- und auch das nur alle zwei Sekunden,
    /// denn oefter aendert sich die Antwort nicht.
    /// </remarks>
    public int ReachableCopies
    {
        get
        {
            if (DateTime.UtcNow - _kenntGeprueft > TimeSpan.FromSeconds(2))
            {
                _kenntGeprueft = DateTime.UtcNow;

                try { lock (_indexGate) _kenntEtwas = _index?.HasEntries ?? false; }
                catch (Exception) { /* die vorige Antwort bleibt stehen */ }
            }

            return _kenntEtwas ? _connections.Count : 0;
        }
    }

    private long _bytesReceived;
    private long _bytesSent;

    private void NoteSent(long bytes)
    {
        Interlocked.Add(ref _bytesSent, bytes);
        LastTransfer = DateTime.Now;
    }

    private void NoteReceived(long bytes)
    {
        Interlocked.Add(ref _bytesReceived, bytes);
        LastTransfer = DateTime.Now;
    }

    // ------------------------------------------------------------ Index

    /// <summary>
    /// Oeffnet die Datenbank, bevor die Gegenstelle angesprochen wird. Ihr
    /// Stand geht in die Ankuendigung ein, damit nur Neueres geschickt wird.
    /// </summary>
    public void OpenIndex()
    {
        var databasePath = Path.Combine(_app.HomeDirectory, $"index-{FolderId}.db");
        _index ??= new PersistentFolderIndex(databasePath, FolderId);
    }

    /// <summary>
    /// Eine Gegenstelle hat ihren Index neu aufgebaut. Ihr Teil des lokalen
    /// Bestandes ist damit unbrauchbar.
    /// </summary>
    /// <remarks>
    /// Verworfen wird nur, was von ihr stammt. Was die anderen Gegenstellen
    /// fuehren, geht das nichts an.
    /// </remarks>
    public void ResetIndex(string device, ulong newPeerIndexId)
    {
        _log($"[{FolderId}] die Gegenstelle hat ihren Index neu aufgebaut -- verwerfe ihren Teil.");

        lock (_indexGate)
        {
            _index!.Clear(device);
            _index.SetPeerIndexId(device, newPeerIndexId);
        }
    }

    /// <summary>
    /// Sucht sofort nach Aenderungen, statt auf den naechsten Durchgang zu
    /// warten.
    /// </summary>
    /// <remarks>
    /// Noetig ist das selten: der Beobachter meldet Aenderungen sofort und ein
    /// Durchgang laeuft ohnehin jede Minute. Der Beobachter verliert aber
    /// Ereignisse, wenn viele auf einmal kommen, und dann will man nicht
    /// warten, sondern nachsehen.
    /// </remarks>
    public void RescanNow()
    {
        _lastScan = DateTime.MinValue;
        Wake();
    }

    /// <summary>
    /// Rechnet die Blocklisten aller lokalen Dateien neu.
    /// </summary>
    /// <remarks>
    /// Der Durchgang uebergeht eine Datei, deren Groesse und Sekunde zum
    /// eigenen Eintrag passen. Das ist die Heuristik, mit der jeder
    /// Abgleichdienst arbeitet, und sie irrt sich selten -- aber wer Groesse
    /// und Sekunde beibehaelt und den Inhalt aendert, wird nie bemerkt. Nach
    /// einem Absturz, nach einem fremden Werkzeug im Ordner oder einfach aus
    /// Misstrauen will man den Beweis statt der Vermutung.
    ///
    /// Angekuendigt wird darum nichts weiter: gerechnet wird alles, gemeldet
    /// nur, was sich wirklich unterscheidet. Der Vergleich der Blocklisten
    /// steht hinter der Heuristik und wird hier nicht uebergangen.
    ///
    /// Platzhalter bleiben aussen vor. Sie halten keinen Inhalt, und ein
    /// Lesen wuerde sie aus dem Netz holen -- der Beweis waere teurer als die
    /// Datei.
    /// </remarks>
    public int RebuildIndex()
    {
        if (!Directory.Exists(_config.LocalPath)) return 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        var anzahl = 0;

        foreach (var info in new DirectoryInfo(_config.LocalPath).EnumerateFiles("*", options))
        {
            if (((uint)info.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0) continue;
            if (NameOf(info.FullName) is not { } name) continue;
            if (!_config.Includes(name)) continue;

            _force[name] = 0;
            _dirty[name] = 0;
            anzahl++;
        }

        _log($"[{FolderId}] {anzahl} Dateien werden neu gerechnet.");

        _lastScan = DateTime.MinValue;
        Wake();

        return anzahl;
    }

    /// <summary>
    /// Verwirft, was wir von dieser Gegenstelle wissen, und faengt von vorn an.
    /// </summary>
    /// <remarks>
    /// Das Gegenstueck zu <see cref="ResetIndex"/>, nur von Hand ausgeloest.
    /// Von selbst geschieht es nur, wenn die Gegenstelle ihre Index-Id
    /// wechselt -- wenn also sie es fuer noetig haelt. Bleibt ein Bestand aus
    /// anderem Grund stehen, gibt es sonst keinen Weg heraus.
    ///
    /// Verworfen wird beides: was sie uns gesagt hat, und was wir ihr gesagt
    /// haben. Nach der naechsten Verhandlung schickt sie einen vollstaendigen
    /// Index, und wir schicken ebenfalls einen.
    /// </remarks>
    public void Resync(string device)
    {
        lock (_indexGate)
        {
            _index?.Clear(device);
            _index?.SetPeerIndexId(device, 0);
        }

        _indexSentTo[device] = false;
        _lastSentTo[device] = 0;

        _log($"[{FolderId}] Bestand dieser Gegenstelle verworfen -- sie wird neu abgefragt.");
    }

    public void RememberPeerIndexId(string device, ulong id)
    {
        if (_index is not null) lock (_indexGate) _index.SetPeerIndexId(device, id);
    }

    /// <summary>
    /// Ordnet Eintraege aus einer aelteren Datenbank dieser Gegenstelle zu.
    /// </summary>
    public void AdoptLegacy(string device)
    {
        if (_index is not null) lock (_indexGate) _index.AdoptLegacy(device);
    }

    /// <summary>Was noch in den Index geschrieben werden muss.</summary>
    /// <remarks>
    /// Die Schranke ist Absicht. Laeuft die Schlange voll, wartet die
    /// Leseschleife -- und damit schliesst sich das TCP-Fenster, und die
    /// Gegenstelle schickt langsamer. Das ist die einzige Bremse, die wirklich
    /// wirkt: ohne sie sammelten sich Millionen Eintraege im Arbeitsspeicher,
    /// waehrend die Platte nicht hinterherkommt.
    /// </remarks>
    private readonly System.Collections.Concurrent.BlockingCollection<(string Device, IReadOnlyList<BepFileInfo> Files)>
        _indexSchlange = new(boundedCapacity: 4);

    /// <summary>So viele Eintraege gehen in einem Zug in die Datenbank.</summary>
    /// <remarks>
    /// Die Gegenstelle schickt tausend je Nachricht. So viele in einer
    /// Transaktion sind fuer die Datenbank die guenstigste Form -- und fuer
    /// alles andere die unguenstigste, denn solange sie laeuft, ist die
    /// Datenbank belegt.
    /// </remarks>
    private const int Haeppchen = 200;

    private Thread? _indexSchreiber;
    private readonly object _schreiberGate = new();

    /// <summary>
    /// Der Faden, der den Index schreibt.
    /// </summary>
    /// <remarks>
    /// Ein eigener, und ausdruecklich unterhalb der normalen Rangstufe.
    ///
    /// Vorher lief das Schreiben auf der Leseschleife: je Nachricht tausend
    /// Eintraege, je Eintrag eine Abfrage und ein Schreibvorgang, zehn
    /// Nachrichten in der Sekunde. Das ist ein voller Kern, dauerhaft, und
    /// die Oberflaeche stand daneben in derselben Warteschlange des
    /// Betriebssystems. Fenster wechseln dauerte Sekunden.
    ///
    /// Ein Index, der eine Minute spaeter fertig ist, faellt niemandem auf.
    /// Ein Programm, das eine Sekunde spaeter auf einen Klick antwortet,
    /// jedem.
    /// </remarks>
    private void SchreiberStarten()
    {
        lock (_schreiberGate)
        {
            if (_indexSchreiber is not null) return;

            _indexSchreiber = new Thread(SchreiberLauf)
            {
                IsBackground = true,
                Name = $"Index {FolderId}",
                Priority = ThreadPriority.BelowNormal
            };

            _indexSchreiber.Start();
        }
    }

    private void SchreiberLauf()
    {
        try
        {
            foreach (var (device, stapel) in _indexSchlange.GetConsumingEnumerable())
            {
                try
                {
                    // In kleinen Haeppchen, nicht in einem Rutsch.
                    //
                    // Die Gegenstelle schickt tausend Eintraege je Nachricht.
                    // Tausend Zeilen in einer Sperre und einer Transaktion
                    // heisst: solange die laeuft, kommt niemand an die
                    // Datenbank -- nicht der Durchgang, nicht die Anzeige.
                    // Zweihundert dauern ein Fuenftel so lang, und dazwischen
                    // ist die Sperre offen.
                    for (var i = 0; i < stapel.Count; i += Haeppchen)
                    {
                        var teil = stapel.Skip(i).Take(Haeppchen).ToList();

                        IReadOnlyList<string> changed;
                        lock (_indexGate) changed = _index!.Absorb(device, teil);

                        if (QueueIncoming(changed) > 0) PeerBusy();

                        // Zwischen zwei Haeppchen aus der Hand geben.
                        Thread.Sleep(1);
                    }

                    _indexArrived.Release();

                    if (Phase == SyncPhase.Index)
                        SetPhase(SyncPhase.Index, Interlocked.Add(ref _aufgenommen, stapel.Count));
                }
                catch (Exception ex)
                {
                    _log($"[{FolderId}] Index aufnehmen: {ex.Message}");
                }

                // Zwischen zwei Stapeln kurz aus der Hand geben. Die niedrige
                // Rangstufe genuegt, solange andere Faeden etwas zu tun haben;
                // dieser Punkt hilft dort, wo sie gerade nichts tun und
                // trotzdem gleich etwas wollen -- ein Klick zum Beispiel.
                Thread.Sleep(1);
            }
        }
        catch (ObjectDisposedException)
        {
            // Der Ordner wird angehalten.
        }
        catch (InvalidOperationException)
        {
            // Die Schlange ist geschlossen.
        }
    }

    /// <summary>Nimmt einen Stapel Index-Eintraege auf, den der PeerHost zugestellt hat.</summary>
    /// <remarks>
    /// Hier wird nur eingereiht. Geschrieben wird auf einem eigenen Faden
    /// unterhalb der normalen Rangstufe -- siehe <see cref="SchreiberStarten"/>.
    /// </remarks>
    public void Absorb(string device, IEnumerable<BepFileInfo> files)
    {
        // Was ein Muster trifft, kommt nicht in den Index. Erst beim
        // Anwenden zu pruefen waere zu spaet: der Eintrag stuende im Baum,
        // zaehlte im Rueckstand und muesste an jeder einzelnen Stelle wieder
        // herausgerechnet werden.
        //
        // Dasselbe fuer die Verwaltung der Gegenstelle -- ihre
        // Ordnerkennzeichnung, ihre Sicherung, ihre Musterliste. Sie stand
        // bisher im Index, ohne dass je etwas daraus wurde: angewendet wurde
        // sie nicht, gezaehlt aber schon.
        files = files.Where(f => !IsHousekeeping(f.Name));

        if (_config.Ignored.Count > 0)
            files = files.Where(f => !_config.IsIgnored(f.Name));

        var stapel = files as IReadOnlyList<BepFileInfo> ?? [.. files];
        if (stapel.Count == 0) return;

        SchreiberStarten();

        // Blockiert, wenn die Schlange voll ist -- und das ist die Bremse:
        // die Leseschleife wartet, das TCP-Fenster schliesst sich, die
        // Gegenstelle schickt langsamer.
        try { _indexSchlange.Add((device, stapel)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Angehalten: die Schlange ist geschlossen oder fort.
        }
    }


    /// <summary>Wie viele Index-Eintraege in dieser Sitzung hereinkamen.</summary>
    private int _aufgenommen;

    /// <summary>Wann die Gegenstelle zuletzt etwas zu tun gab.</summary>
    private DateTime _letzteMeldung = DateTime.MinValue;

    /// <summary>So lange muss Ruhe sein, bevor wieder "fertig" gilt.</summary>
    /// <remarks>
    /// Ohne diese Wartezeit fiele die Anzeige zwischen zwei Ankuendigungen
    /// jedes Mal kurz auf "abgeglichen" zurueck und flackerte.
    /// </remarks>
    private static readonly TimeSpan Ruhe = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Wie viele Dateien die Gegenstelle fuehrt, die hier noch nicht so
    /// dastehen, und wie viel das zusammen ist.
    /// </summary>
    /// <remarks>
    /// Der Arbeitsvorrat, gemessen und nicht geschaetzt. Gezaehlt wird beim
    /// Durchgang ueber den Ordner; zwischen zwei Durchgaengen zieht jeder
    /// uebernommene Eintrag die Zahl herunter, damit sie sich bewegt und
    /// nicht minutenlang steht.
    /// </remarks>
    public int Outstanding { get; private set; }

    public long OutstandingBytes { get; private set; }

    /// <summary>
    /// Die offenen Namen einzeln, mit Groesse und Grund.
    /// </summary>
    /// <remarks>
    /// Eine Zahl sagt, dass etwas aussteht. Sie sagt nicht, was -- und bei
    /// vier offenen Dateien von 976 ist genau das die Frage. Gefuellt beim
    /// Durchgang ueber den Ordner, gedeckelt, weil eine frisch verbundene
    /// Freigabe jede ihrer Dateien offen hat.
    /// </remarks>
    public IReadOnlyList<OutstandingItem> OutstandingItems { get; private set; } = [];

    /// <summary>
    /// Dateien, die die Gegenstelle nennt, aber selbst nicht haelt.
    /// </summary>
    /// <remarks>
    /// Nicht abgeglichen und nicht zu beschaffen. Sie zaehlen nicht zum
    /// Rueckstand: sonst stuende der Balken fuer immer kurz vor hundert und
    /// der Zustand auf "gleicht ab", ohne dass irgendein Handgriff daran
    /// etwas aendern koennte.
    ///
    /// Genannt werden sie trotzdem. Verschweigen hiesse zu behaupten, alles
    /// sei da.
    /// </remarks>
    public int Awaiting { get; private set; }

    public long AwaitingBytes { get; private set; }

    /// <summary>
    /// Was im Ordner steht, Platzhalter eingerechnet.
    /// </summary>
    /// <remarks>
    /// Nicht dasselbe wie der Cache. Ein Platzhalter steht da und zaehlt
    /// hier mit, haelt aber keinen Inhalt. Erst beide Zahlen nebeneinander
    /// sagen etwas: was abgeglichen ist und was davon Platz belegt.
    /// </remarks>
    public int LocalFiles { get; private set; }

    public long LocalBytes { get; private set; }

    /// <summary>
    /// Wie viele Dateien der Abgleich umfasst und wie viel das zusammen ist.
    /// </summary>
    /// <remarks>
    /// Der Nenner. Gezaehlt beim selben Durchgang wie der Rueckstand, damit
    /// beide Zahlen vom selben Zeitpunkt stammen -- sonst koennte der Anteil
    /// aus zwei Messungen entstehen und ueber hundert Prozent hinauslaufen.
    /// </remarks>
    public int IndexFiles { get; private set; }

    public long IndexTotalBytes { get; private set; }

    /// <summary>
    /// Der Umfang des Abgleichs: alle Namen, die es auf einer der beiden
    /// Seiten gibt.
    /// </summary>
    /// <remarks>
    /// Der Nenner fuer den Anteil. Nicht der Index allein, denn eine Datei,
    /// die nur hier liegt, gehoert auch zum Abgleich -- sie muss noch hinaus.
    /// </remarks>
    public int SyncTotal { get; private set; }

    public long SyncTotalBytes { get; private set; }

    /// <summary>Wann zuletzt ueber den Ordner gegangen wurde.</summary>
    public DateTime LastScan { get; private set; }

    /// <summary>
    /// Die Gegenstelle ist noch nicht fertig -- sie hat angekuendigt oder
    /// laedt selbst noch.
    /// </summary>
    public void PeerBusy()
    {
        _letzteMeldung = DateTime.UtcNow;
        if (Phase == SyncPhase.Fertig) UpdateOutstandingPhase();
    }

    /// <summary>Meldet, wie viele Eintraege ein Durchgang uebernommen hat.</summary>
    /// <remarks>
    /// Eine Fortschreibung zwischen zwei Messungen, keine eigene Messung. Sie
    /// kann daneben liegen; der naechste Durchgang setzt sie gerade.
    /// </remarks>
    private void Fortschritt(int uebernommen)
    {
        if (uebernommen <= 0) return;

        Outstanding = Math.Max(0, Outstanding - uebernommen);
        UpdateOutstandingPhase();
    }

    /// <summary>Traegt den Arbeitsvorrat in die Phase ein.</summary>
    private void UpdateOutstandingPhase()
    {
        if (Phase is not (SyncPhase.Fertig or SyncPhase.Abgleich)) return;

        // Erledigte von insgesamt, damit der Balken einen Bezug hat. Ohne
        // Rueckstand bleibt nur die Frage, ob die Gegenstelle noch redet.
        if (Outstanding == 0 && DateTime.UtcNow - _letzteMeldung >= Ruhe) return;

        // Gezaehlt wird in Bytes und nicht in Dateien. Eine von tausend
        // Dateien kann die Haelfte des Ordners sein; ein Anteil nach
        // Stueckzahl stuende dann bei 99,9 Prozent und waere trotzdem eine
        // halbe Stunde von fertig entfernt.
        var gesamt = SyncTotalBytes;
        var offen = Math.Min(OutstandingBytes, gesamt);

        SetPhase(SyncPhase.Abgleich,
            (int)((gesamt - offen) / 1024), (int)(gesamt / 1024));
    }

    /// <summary>
    /// Prueft, ob wieder Ruhe eingekehrt ist.
    /// </summary>
    /// <remarks>
    /// Drei Bedingungen, und alle muessen gelten: nichts liegt mehr an, was
    /// zu uebernehmen waere, kein Rueckstand gegenueber dem Index, und seit
    /// der letzten Meldung ist es eine Weile still. Die erste allein reichte
    /// nicht -- zwischen zwei Stapeln ist die Warteschlange auch leer.
    /// </remarks>
    private void SettlePhase()
    {
        if (Phase != SyncPhase.Abgleich) return;
        if (!_incoming.IsEmpty || Outstanding > 0) return;
        if (DateTime.UtcNow - _letzteMeldung < Ruhe) return;

        SetPhase(SyncPhase.Fertig);
    }

    // ------------------------------------------------------------ Start und Stopp

    public async Task StartAsync(string device, BepConnection connection, CancellationToken ct)
    {
        await PrepareAsync(device, connection, ct);
        await CommitAsync(ct);
    }

    /// <summary>
    /// Holt den Index der Gegenstelle, ohne im Explorer etwas anzulegen.
    /// </summary>
    /// <remarks>
    /// Der erste von zwei Schritten. Wer einen angebotenen Ordner uebernimmt,
    /// soll vorher sehen, was darin ist, und das steht erst mit dem Index
    /// fest. Zusaetzliche Kosten entstehen nicht, denn der Index kommt
    /// ohnehin, sobald wir den Ordner ankuendigen.
    /// </remarks>
    public async Task PrepareAsync(string device, BepConnection connection, CancellationToken ct)
    {
        _connections[device] = connection;
        State = ShareState.Wartet;

        // Eine neue Sitzung beginnt mit einem Index. Erst danach sind
        // Nachtraege moeglich, und die brauchen die Nummer ihres Vorgaengers.
        // Das gilt je Gegenstelle: die anderen sind davon nicht beruehrt.
        _indexSentTo[device] = false;
        _lastSentTo[device] = 0;

        try
        {
            var wartete = System.Diagnostics.Stopwatch.StartNew();
            await WaitForIndexAsync(ct);
            _log($"[{FolderId}] Index der Gegenstelle da nach {wartete.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Legt die Platzhalter an. Ab hier ist der Ordner im Explorer sichtbar.
    /// Vorher ist nichts geschehen, was zurueckzunehmen waere.
    /// </summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        try
        {
            // Vor dem Anlegen: was nicht mehr dazugehoert, soll auch keinen
            // Platzhalter bekommen. Der Index kann Namen fuehren, die eine
            // aeltere Fassung hereingelassen hat.
            var uhr = System.Diagnostics.Stopwatch.StartNew();

            // Und nicht alle auf einmal.
            //
            // Acht Ordner starten miteinander, und jeder will lesen, rechnen
            // und schreiben. Die Platte gibt das nicht her: was gleichzeitig
            // laeuft, ist nicht schneller fertig, sondern nur gleichzeitig
            // langsam -- und der Rechner steht derweil.
            //
            // Zwei zur Zeit. Die uebrigen warten hier und zeigen dabei
            // "wartet"; sie sind gestartet, verbunden und haben ihren Index,
            // nur der teure Teil steht an.
            await Anlauf.WaitAsync(ct).ConfigureAwait(false);

            try
            {

            // Ausdruecklich auf einen eigenen Faden.
            //
            // Der Aufruf kommt ueber das Verbinden aus der Oberflaeche, und
            // was hier folgt, ist rechnende Arbeit mit einem Zugriff auf das
            // Dateisystem je Eintrag: das Aufraeumen des Index, das Anlegen
            // der Verzeichnisse, das Pruefen jedes Platzhalters. Bei
            // fuenfundvierzigtausend Dateien sind das zwoelf bis siebzehn
            // Sekunden, in denen das Fenster nichts zeichnet und auf keinen
            // Klick antwortet.
            await ImHintergrund(async () =>
            {
                PurgeIgnored();
                await ProjectAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            _log($"[{FolderId}] Platzhalter vorbereitet in {uhr.ElapsedMilliseconds} ms.");
            }
            finally
            {
                Anlauf.Release();
            }

            State = ShareState.Bereit;

            await ApplyModeAsync(ct);
            SetPhase(SyncPhase.Fertig);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Der Abbruch, mit Herkunft.
    /// </summary>
    /// <remarks>
    /// Frueher stand hier nur die Meldung. "Index was out of range" ohne
    /// Angabe der Stelle ist keine Auskunft, sondern eine Aufforderung zum
    /// Raten. Art und die obersten Stufen des Aufrufwegs stehen jetzt dabei;
    /// der ganze Weg waere im Protokollfenster unlesbar.
    /// </remarks>
    private void Fail(Exception exception)
    {
        State = ShareState.Fehler;
        SetPhase(SyncPhase.Ruht);
        _log($"[{FolderId}] {Herkunft(exception)}");
    }

    internal static string Herkunft(Exception exception)
    {
        var stellen = (exception.StackTrace ?? "")
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(z => z.Trim())
            .Take(4);

        return $"{exception.GetType().Name}: {exception.Message}" +
               string.Concat(stellen.Select(z => Environment.NewLine + "    " + z));
    }

    private async Task WaitForIndexAsync(CancellationToken ct)
    {
        // Nach einem Neustart liegt der Index bereits vor. Diese Phase ist
        // dann sofort beendet.
        SetPhase(SyncPhase.Index, _index!.Count);
        if (_index.Count > 0) return;

        _log($"[{FolderId}] warte auf den Index ...");
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var signalled = await _indexArrived.WaitAsync(TimeSpan.FromSeconds(3), ct);
            if (!signalled && _index.Count > 0) break;
        }

        if (_index.Count == 0)
            throw new InvalidOperationException(
                "kein Index empfangen -- ist der Ordner mit diesem Geraet geteilt?");
    }

    public async Task StopAsync()
    {
        if (State == ShareState.Gestoppt) return;

        await StopLocalLoopAsync();

        FinishOutgoing();

        _wache?.Dispose();
        _wache = null;

        // Der Schreiber laeuft, bis die Schlange geschlossen ist. Was noch
        // darin steht, wird nicht mehr geschrieben -- die Gegenstelle schickt
        // es beim naechsten Verbinden noch einmal, denn quittiert haben wir
        // es nie.
        try { _indexSchlange.CompleteAdding(); } catch (Exception) { }

        _cache?.Save();
        _cache?.LeaveLimits();
        _mount?.Dispose();
        _mount = null;
        _connections.Clear();
        IsPaused = false;
        State = ShareState.Gestoppt;
    }

    /// <summary>
    /// Haelt an, ohne die Platzhalter aufzugeben. Anfragen werden abgewiesen
    /// statt liegengelassen. Ein wartender Zugriff wuerde den Explorer
    /// blockieren, bis Windows die Anfrage von sich aus abbricht.
    /// </summary>
    public void Pause()
    {
        if (State != ShareState.Bereit) return;
        IsPaused = true;
        _log($"[{FolderId}] angehalten: keine Uebertragung, keine Aenderung im Ordner.");
        State = ShareState.Pausiert;
    }

    /// <summary>
    /// Nimmt die Verbindung weg, ohne den Ordner aufzugeben.
    /// </summary>
    /// <remarks>
    /// Der Unterschied zu <see cref="StopAsync"/>: der Sync-Root bleibt
    /// eingehaengt, der Cache angemeldet und der Hintergrundlauf am Leben.
    /// Lokal wird also weiter indexiert -- das kostet nichts auf der Verbindung
    /// und erspart beim Fortsetzen einen vollstaendigen Durchgang.
    /// </remarks>
    public void DropConnection(string device)
    {
        _connections.TryRemove(device, out _);
        _indexSentTo.TryRemove(device, out _);
        _lastSentTo.TryRemove(device, out _);
    }

    /// <summary>
    /// Nimmt eine neue Verbindung an, ohne den Ordner neu aufzubauen.
    /// </summary>
    /// <remarks>
    /// Nach dem Fortsetzen steht der Ordner noch genauso da wie vorher. Ihn
    /// erneut anzulegen hiesse, Sync-Root und Platzhalter ein zweites Mal
    /// aufzubauen, waehrend die ersten noch stehen.
    ///
    /// Zurueckgesetzt wird nur, was zur Sitzung gehoert: eine neue Verbindung
    /// beginnt mit einem vollstaendigen Index, und Nachtraege brauchen die
    /// Nummer ihres Vorgaengers.
    /// </remarks>
    public void Rebind(string device, BepConnection connection)
    {
        _connections[device] = connection;
        _indexSentTo[device] = false;
        _lastSentTo[device] = 0;

        if (State == ShareState.Gestoppt) return;
        if (!IsPaused) State = ShareState.Bereit;
    }

    public void Resume()
    {
        if (State != ShareState.Pausiert) return;
        IsPaused = false;
        State = ShareState.Bereit;
        _log($"[{FolderId}] fortgesetzt.");

        // Was waehrend der Pause angekuendigt wurde, liegt noch in der
        // Schlange. Der Hintergrundlauf soll es gleich sehen und nicht erst
        // beim naechsten Takt.
        Wake();
    }

    // ------------------------------------------------------------ Platzhalter

    /// <summary>
    /// So viele Ordner duerfen gleichzeitig ihren teuren Anlauf haben.
    /// </summary>
    /// <remarks>
    /// Programmweit, nicht je Gegenstelle: die Platte ist eine.
    /// </remarks>
    private static readonly SemaphoreSlim Anlauf = new(2, 2);

    /// <summary>
    /// Die Fassung der Sync-Wurzel, wie sie in der Registrierung steht.
    /// </summary>
    /// <remarks>
    /// Sie entscheidet, ob beim Start neu angemeldet wird. Wer an den
    /// Eigenschaften einer Wurzel etwas aendert -- Name, Symbol, Gruppierung,
    /// eine der Richtlinien --, zaehlt sie hoch. Sonst bleibt es beim
    /// Bestand, und die Aenderung kommt nie an.
    ///
    /// 0.2: ShowSiblingsAsGroup gesetzt.
    /// 0.3: und wieder zurueckgenommen -- Windows gruppiert damit nach
    /// Elternverzeichnis und benennt den Knoten danach.
    /// </remarks>
    private const string SyncRootFassung = "0.3";

    private async Task ProjectAsync()
    {
        _log($"[{FolderId}] registriere Sync-Root: {_config.LocalPath}");

        // Ueber StorageProviderSyncRootManager statt CfRegisterSyncRoot: nur
        // dieser Weg legt den Registry-Schluessel an, in den die
        // Vorschau-Erweiterung eingetragen wird. Ausserdem erscheint der
        // Ordner mit Namen und Symbol in der Navigationsleiste des Explorers.
        var name = string.IsNullOrWhiteSpace(_config.Label) ? FolderId : _config.Label;
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        _syncRootId = await WinRtSyncRoot.RegisterAsync(_config.LocalPath, $"SyncT {name}", SyncRootFassung);
        _log($"[{FolderId}] Sync-Root angemeldet in {uhr.ElapsedMilliseconds} ms.");

        var statePath = Path.Combine(_app.HomeDirectory, $"cache-{FolderId}.json");
        // "Vollstaendig lokal" nimmt am Limit nicht teil. Dort darf kein
        // Speicherplatz freigegeben werden, sonst gilt die Zusage nicht.
        var limits = _config.Mode == ShareMode.AlwaysLocal ? null : _app.Cache;
        if (limits is not null) limits.Log ??= _log;

        _cache = new HydrationCache(_config.LocalPath, limits, statePath, _log)
        {
            // Der Cache speichert nur Groessen und Zugriffszeiten. Ob eine
            // Datei wiederbeschaffbar ist, steht im Index der Gegenstelle.
            MayEvict = MayEvict
        };

        _thumbnails = new ThumbnailStore(_app.ThumbnailDirectory);
        _thumbnails.Prepare();

        // Einmal je Sitzung. Eine Ablage, die vor dieser Regel entstanden
        // ist, bekaeme das Attribut sonst erst beim naechsten Konflikt -- und
        // liegt bis dahin sichtbar mitten in der Freigabe.
        if (_config.KeepVersions) VersteckeWurzel();

        // Der Eintrag muss stehen, bevor die Shell den Sync-Root uebernimmt.
        // Sie liest seine Eigenschaften beim Anmelden. Deshalb wird danach
        // noch einmal angemeldet, damit sie den Vorschau-Erzeuger erfasst.
        uhr.Restart();
        RegisterThumbnailProvider();
        _syncRootId = await WinRtSyncRoot.RegisterAsync(_config.LocalPath, $"SyncT {name}", SyncRootFassung);
        _log($"[{FolderId}] Vorschau-Erweiterung und zweite Anmeldung in {uhr.ElapsedMilliseconds} ms.");

        ApplyExplorerVisibility();

        _mount = new CloudFilterMount(_config.LocalPath, this, _log);

        // Die Meldungen der Cloud-Files-Schicht sind der Ausloeser fuer die
        // Erkennung lokaler Aenderungen. Sie melden nur das Ereignis; ob es
        // eine Aenderung war, entscheidet Evaluate anhand des blocks_hash.
        BeobachteOrdner();

        _mount.FileClosed += NoteLocalChange;
        _mount.FileDeleted += NoteLocalDelete;
        _mount.FileRenamed += (before, after) =>
        {
            // Ein Umbenennen ist fuer das Protokoll eine neue Datei und eine
            // Loeschung -- in dieser Reihenfolge. Liegt eine Seite ausserhalb
            // der Freigabe, bleibt ihr Pfad leer und der Teil entfaellt.
            //
            // Die Zuordnung wird gemerkt, bevor die Meldungen laufen. Ein
            // Platzhalter traegt seinen Inhalt nicht bei sich; unter dem alten
            // Namen steht aber seine Blockliste, und damit laesst er sich
            // ankuendigen, ohne ihn erst zu holen.
            var zusammen = before.Length > 0 && after.Length > 0
                           && NameOf(before) is { } alt && NameOf(after) is { } neu;

            if (zusammen) _renamedFrom[NameOf(after)!] = NameOf(before)!;

            // Die Loeschung des alten Namens geht nur allein hinaus. Gehoert
            // sie zu einem Umbenennen, wartet sie darauf, dass der neue Name
            // angekuendigt ist -- sonst kuendigen wir eine Loeschung an, deren
            // Gegenstueck nie folgt, und der Inhalt ist ueberall fort.
            if (before.Length > 0 && !zusammen) NoteLocalDelete(before);

            if (after.Length > 0) NoteLocalChange(after);
        };

        _mount.Connect();

        // Zuerst der eigene Bestand, dann der fremde.
        //
        // Der Konfliktweg verlangt einen eigenen Indexeintrag: nur wer weiss,
        // was er selbst hat, kann feststellen, dass beide Seiten geaendert
        // haben. Bei einem frisch uebernommenen Ordner gibt es diesen Eintrag
        // fuer keine Datei. Wendet man in diesem Zustand den fremden Index an,
        // gewinnt die Gegenstelle jede Abweichung stillschweigend -- der
        // Ordner sieht danach richtig aus, und was hier stand, ist fort.
        //
        // Deshalb wird vorher gelesen und gerechnet. Danach hat jede
        // vorhandene Datei ihre Blockliste, und der Vergleich ist ein
        // Vergleich und kein Zugestaendnis.
        await AdoptLocalAsync();

        // Vor dem Anlegen, und das ist die ganze Pointe: das Anlegen richtet
        // sich nach dem Bestand der Gegenstelle. Eine Datei, die hier
        // geloescht wurde, waehrend das Programm nicht lief, bekaeme dabei
        // sofort wieder einen Platzhalter -- und die Loeschung waere nicht
        // mehr zu sehen, sondern rueckgaengig gemacht.
        MarkierungPruefen();
        OfflineGeloeschte();

        SetPhase(SyncPhase.Platzhalter);
        _mount.ProjectPlaceholders(
            (done, total) => SetPhase(SyncPhase.Platzhalter, done, total),
            nurVerzeichnisse: _config.Mode == ShareMode.AlwaysLocal);

        // Das Anlegen der Platzhalter deckt nur einen Teil ab: es legt an, was
        // fehlt. Eine Datei, die die Gegenstelle inzwischen geloescht oder
        // geaendert hat, bleibt dabei stehen wie sie ist. Deshalb wird der
        // ganze Index einmal geprueft.
        //
        // Der Durchgang ist billig. Fuer einen Platzhalter, dessen Groesse und
        // Zeit zum Index passen, endet er nach zwei Vergleichen; das ist der
        // Normalfall fuer nahezu jeden Eintrag.
        _incoming.Clear();
        lock (_indexGate) QueueIncoming(_index!.AllNames().ToList());

        SetPhase(SyncPhase.Cache);
        _cache.ReconcileWithDisk();
        CacheChanged?.Invoke();

        // Erst jetzt steht fest, was lokal liegt. Der Durchgang vergleicht den
        // Bestand auf der Platte mit dem eigenen Index und merkt sich, was zu
        // pruefen ist; gerechnet und gesendet wird im Hintergrund.
        ScanLocal();
        StartLocalLoop();
    }

    /// <summary>
    /// Holt den Inhalt einer Datei und schreibt ihn selbst hinein.
    /// </summary>
    /// <remarks>
    /// Nicht ueber den Rueckruf des Dateisystems. Der wird nur bedient, wenn
    /// ein fremder Prozess die Datei oeffnet: dann fuellt Windows sie und die
    /// Attribute wechseln. Oeffnet der Anbieter sie selbst, meldet CfExecute
    /// Erfolg und schreibt nichts -- die Datei behaelt "bei Bedarf abrufen",
    /// Windows wartet seine Minutenfrist ab und fragt erneut. Im Protokoll
    /// stand es nebeneinander: derselbe Weg, einmal von Directory Opus
    /// ausgeloest und einmal von uns, einmal 0x420 und einmal 0x401620.
    ///
    /// Also schreiben wir die Datei selbst und kennzeichnen sie danach als
    /// abgeglichen. Stueckweise, damit eine grosse Datei nicht vollstaendig
    /// im Arbeitsspeicher steht.
    /// </remarks>
    /// <summary>
    /// Legt eine Datei ohne Inhalt an -- als richtige Datei, nicht als
    /// Platzhalter.
    /// </summary>
    /// <remarks>
    /// Denselben Weg wie eine geholte Datei: erst daneben, dann an die
    /// Stelle. Ein Abbruch unterwegs laesst den Platzhalter stehen.
    /// </remarks>
    private void LeereDateiAnlegen(string path, string name, BepFileInfo file)
    {
        using var hold = HoldHydration(name);

        var temp = path + ".synct-neu";

        try
        {
            using (new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { }

            File.SetLastWriteTimeUtc(temp, DateTimeOffset.FromUnixTimeSeconds(file.ModifiedS).UtcDateTime);

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            _cache?.NoteContent(name, 0);
            _cache?.MarkInSync(name);

            var uebernommen = file.Clone();
            uebernommen.Sequence = 0;
            Store(uebernommen, StateClean);
        }
        catch (Exception)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            throw;
        }
    }

    private async Task MaterialiseAsync(string path, CancellationToken ct)
    {
        if (NameOf(path) is not { } name) return;

        BepFileInfo file;
        lock (_indexGate)
            if (_index is null || !_index.TryGet(name, out file!)) return;

        if (file.Deleted) return;

        // Eine Datei ohne Bytes hat nichts zu holen -- und wer nichts holt,
        // braucht dafuer auch keine Verbindung. Sie entsteht hier an Ort und
        // Stelle.
        //
        // Als Platzhalter kann sie nie fertig werden: das Fuellen holt
        // Bloecke, und es gibt keine. Sie galt damit dauerhaft als nicht
        // abgeglichen. Der Zustand stand fuer immer auf "gleicht ab", der
        // Balken auf hundert Prozent neben null offenen Bytes -- ein
        // Rueckstand, an dem kein Handgriff etwas geaendert haette.
        if (file.Size == 0)
        {
            LeereDateiAnlegen(path, name, file);
            return;
        }

        // Ohne Bloecke, aber mit Groesse: die Gegenstelle fuehrt den Namen
        // und haelt den Inhalt nicht. Hier ist nichts zu beschaffen.
        if (file.Blocks.Count == 0) return;

        // Warum nicht ueber CfHydratePlaceholder, was viel einfacher waere:
        //
        // Der Rueckruf wird nur bedient, wenn ein fremder Prozess die Datei
        // oeffnet. Fordert der Anbieter seine eigene Datei an, meldet
        // CfExecute Erfolg -- und schreibt nichts. Gemessen: 8779 B
        // durchgereicht, Ergebnis 0x00000000, Attribute unveraendert
        // 0x401620, also weiterhin "bei Bedarf abrufen". Windows stellt den
        // Rueckruf eine Minute spaeter erneut zu, mit demselben Ausgang; bei
        // zwei Plaetzen sind das zwei Dateien je Minute.
        //
        // Deshalb der Umweg: in eine Nebendatei holen, den Platzhalter
        // entfernen, die Nebendatei an seine Stelle setzen und sie als
        // abgeglichen kennzeichnen. Der letzte Schritt macht sie wieder zum
        // Platzhalter, diesmal mit Inhalt.

        var leitung = LineFor(name)
            ?? throw new InvalidOperationException($"\"{FolderId}\" ist nicht verbunden.");

        var transfer = new TransferInfo(FolderId, name, file.Size, TransferDirection.Herein);
        TransferStarted?.Invoke(transfer);
        transfer.State = TransferState.Laeuft;

        // Waehrend wir schreiben, ist jede Meldung darueber unsere eigene.
        using var hold = HoldHydration(name);

        var temp = path + ".synct-neu";

        // Welcher Schritt gerade laeuft. Die Meldung der Cloud-Files-Schicht
        // nennt die Datei, aber nicht den Aufruf; fuenf Schritte fassen
        // Dateien an, und sie scheitern aus verschiedenen Gruenden.
        var schritt = "die Zieldatei anlegen";

        try
        {
            await using (var ziel = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                const int Stueck = 8 << 20;

                for (long offset = 0; offset < file.Size;)
                {
                    var nehmen = (int)Math.Min(Stueck, file.Size - offset);

                    var data = await FileFetcher.FetchRangeAsync(
                        leitung, FolderId, file, offset, nehmen, _app.Parallelism, null, ct)
                        .ConfigureAwait(false);

                    await ziel.WriteAsync(data, ct).ConfigureAwait(false);

                    offset += nehmen;
                    transfer.DoneBytes = offset;
                    NoteReceived(data.Length);
                    schritt = "den Inhalt holen";
                }
            }

            schritt = "den Zeitstempel setzen";
            File.SetLastWriteTimeUtc(temp, DateTimeOffset.FromUnixTimeSeconds(file.ModifiedS).UtcDateTime);

            // Erst jetzt an die Stelle der leeren Datei. Ein Abbruch unterwegs
            // laesst den Platzhalter stehen, statt eine halbe Datei zu
            // hinterlassen.
            // Bei "vollstaendig lokal" steht dort nichts mehr, was zu
            // entfernen waere -- die Datei entsteht gleich hier zum ersten Mal.
            schritt = "den Platzhalter entfernen";
            if (File.Exists(path)) File.Delete(path);

            schritt = "die Datei einsetzen";
            File.Move(temp, path);

            schritt = "sie in den Bestand aufnehmen";
            _cache?.NoteContent(name, file.Size);
            _cache?.MarkInSync(name);

            // In den eigenen Bestand aufnehmen -- als Fassung der
            // Gegenstelle, denn von dort kommt sie.
            //
            // Ohne diesen Eintrag haelt der naechste Durchgang jede eben
            // geschriebene Datei fuer eine fremde Aenderung: er kennt sie
            // nicht, rechnet ihre Blockliste und vergleicht. Bei 972 Dateien
            // sind das 335 MB, die noch einmal von der Platte gelesen werden,
            // nur um festzustellen, was wir gerade selbst geschrieben haben.
            //
            // Die Sequenznummer bleibt null: angekuendigt haben wir diese
            // Fassung nie, und im Index darf die Null nicht stehen.
            var uebernommen = file.Clone();
            uebernommen.Sequence = 0;
            Store(uebernommen, StateClean);

            transfer.State = TransferState.Fertig;
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Fehler;
            transfer.Error = $"{schritt}: {ex.Message}";

            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }

            // Mit dem Schritt und der Fehlerzahl davor. "Die Clouddatei-
            // Metadaten sind beschaedigt" sagt nicht, welcher Aufruf das
            // meldet, und ohne den Aufruf ist die Meldung nicht zu verwerten.
            throw new IOException(
                $"{schritt}: {ex.Message} (0x{ex.HResult:X8})", ex);
        }
        finally
        {
            TransferFinished?.Invoke(transfer);
        }
    }

    /// <summary>
    /// Nimmt auf, was schon im Ordner liegt.
    /// </summary>
    /// <remarks>
    /// Der Durchgang findet die vorhandenen Dateien, die Bewertung rechnet
    /// ihre Blocklisten. Was Byte fuer Byte dem entspricht, was die
    /// Gegenstelle fuehrt, wird still uebernommen; alles andere wird
    /// angekuendigt, denn es ist unsere Fassung und sie kennt sie nicht.
    ///
    /// Platzhalter bleiben aussen vor: der Durchgang uebergeht sie, und sie
    /// haetten auch nichts zu rechnen. Beim ersten Mal gibt es ohnehin keine.
    ///
    /// Das kostet einmal Lesen ueber den ganzen Ordner. Es ist der Preis
    /// dafuer, dass die Gegenstelle danach nichts gewinnt, was sie nicht
    /// beweisen kann.
    /// </remarks>
    private async Task AdoptLocalAsync()
    {
        // Ausdruecklich auf einen eigenen Faden, und nicht bloss "await".
        //
        // Der Aufruf kommt ueber das Verbinden aus der Oberflaeche, und beides
        // hier ist rechnende Arbeit ohne Wartepunkt: der Durchgang ueber den
        // Ordner und das Rechnen der Blocklisten. Bei sechzig Gigabyte sind
        // das anderthalb Minuten, in denen das Fenster nichts zeichnet und auf
        // keinen Klick antwortet -- es sieht aus, als sei es abgestuerzt.
        await ImHintergrund(AufnehmenAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// Fuehrt eine lange Arbeit auf einem eigenen, nachrangigen Faden aus.
    /// </summary>
    /// <remarks>
    /// Task.Run nimmt einen Faden aus dem Vorrat, und der laeuft mit der
    /// gewoehnlichen Rangstufe -- derselben, die auch das Fenster hat. Wer
    /// zwanzig Gigabyte liest und hasht, gewinnt damit gegen jeden Klick,
    /// den das Betriebssystem gerade zustellen will.
    ///
    /// Ein eigener Faden unterhalb dieser Stufe kehrt das um: die Arbeit
    /// laeuft, solange niemand sonst etwas will, und tritt zurueck, sobald
    /// doch. Sie dauert dadurch nicht laenger -- ausser jemand bedient das
    /// Programm, und dann ist das genau richtig.
    ///
    /// Der Vorrat taugt dafuer nicht: seine Faeden gehoeren allen, und ihre
    /// Rangstufe zu aendern hiesse, sie auch fuer den naechsten zu aendern,
    /// der sie bekommt.
    /// </remarks>
    private static Task ImHintergrund(Func<Task> arbeit)
    {
        var fertig = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var faden = new Thread(() =>
        {
            try
            {
                arbeit().GetAwaiter().GetResult();
                fertig.TrySetResult();
            }
            catch (Exception ex)
            {
                fertig.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Aufnehmen",
            Priority = ThreadPriority.BelowNormal
        };

        faden.Start();
        return fertig.Task;
    }

    private async Task AufnehmenAsync()
    {
        ScanLocal(quiet: true);
        if (_dirty.IsEmpty) return;

        var anzahl = _dirty.Count;
        SetPhase(SyncPhase.Index, 0, anzahl);
        _log($"[{FolderId}] {anzahl} vorhandene Dateien werden aufgenommen ...");

        try
        {
            await PublishAsync(
                CancellationToken.None,
                fertig => SetPhase(SyncPhase.Index, fertig, anzahl));
        }
        catch (Exception ex)
        {
            // Eine Aufnahme, die scheitert, ist kein Grund, den Ordner nicht
            // einzuhaengen. Der Hintergrundlauf holt sie nach.
            _log($"[{FolderId}] Aufnahme des Bestands: {ex.Message}");
        }
    }

    /// <summary>
    /// Meldet die Shell-Erweiterung an, damit der Explorer die vorbereiteten
    /// Vorschauen zeigt statt eines Ersatzsymbols.
    /// </summary>
    private void RegisterThumbnailProvider()
    {
        if (!_app.GenerateThumbnails || _syncRootId is null || _thumbnails is null) return;

        try
        {
            ThumbnailProviderRegistration.RegisterStore(_thumbnails.Directory);

            // Die DLL ist die Zugabe, nicht die Bedingung. Bedient wird die
            // Shell von der Klasse, die der laufende Client anmeldet -- frueher
            // stieg diese Methode ohne DLL vorzeitig aus, und damit fielen
            // auch der Eintrag am Sync-Root und der Erzeuger selbst weg. In
            // einer veroeffentlichten Version waeren so gar keine Vorschauen
            // entstanden.
            if (ThumbnailProviderRegistration.FindLibrary() is { } library)
            {
                ThumbnailProviderRegistration.RegisterClass(library);
                ThumbnailProviderRegistration.RegisterMenu(library);
            }

            if (!ThumbnailProviderRegistration.AttachToSyncRoot(_syncRootId))
                _log($"[{FolderId}] Vorschau-Erweiterung liess sich nicht am Sync-Root eintragen.");

            // Zusaetzlich zur Eintragung in der Registrierung. Solange der
            // Client laeuft, beantwortet er Anfragen selbst.
            ThumbnailService.EnsureStarted(_log);

            // Die Vorschau-Kette laeuft ueber statische Einstiegspunkte und
            // kennt kein Protokoll. Hier bekommt sie eines.
            Melden ??= _log;

            lock (Laufende)
            {
                if (!Laufende.Contains(this)) Laufende.Add(this);
                ThumbnailProviderRegistration.PublishShares(Laufende.Select(s => s._config.LocalPath));
            }
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Vorschau-Erweiterung: {ex.Message}");
        }
    }

    /// <summary>
    /// Holt nach, was bei "vollstaendig lokal" noch leer dasteht.
    /// </summary>
    /// <remarks>
    /// Die Namen kommen aus dem Durchgang ueber den Ordner, der sie ohnehin
    /// feststellt. Gefragt wird nur, was die Gegenstelle auch haelt: was sie
    /// selbst nicht hat, zaehlt als Warten und nicht als Rueckstand.
    /// </remarks>
    private async Task FetchMissingAsync(CancellationToken ct)
    {
        if (_config.Mode != ShareMode.AlwaysLocal || IsPaused) return;
        if (_connections.IsEmpty) return;

        var offen = _ohneInhalt;
        if (offen.Count == 0) return;

        _log($"[{FolderId}] {offen.Count} Platzhalter werden nachgeholt ...");

        var done = 0;
        SetPhase(SyncPhase.Inhalte, 0, offen.Count);

        await Parallel.ForEachAsync(
            offen,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (name, token) =>
            {
                try
                {
                    await MaterialiseAsync(LocalPathOf(name), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"  {name}: {ex.Message}");
                }

                SetPhase(SyncPhase.Inhalte, Interlocked.Increment(ref done), offen.Count);
            }).ConfigureAwait(false);

        // Der naechste Durchgang misst neu. Bis dahin gilt die Liste als
        // abgearbeitet -- sonst liefe sie im naechsten Takt noch einmal.
        _ohneInhalt = [];
    }

    /// <summary>
    /// Setzt durch, ob diese Freigabe im Explorer-Baum steht.
    /// </summary>
    /// <remarks>
    /// Nach jedem Anmelden und nach jeder Aenderung der Einstellung. Das
    /// Anmelden setzt die Eigenschaft auf sichtbar zurueck.
    /// </remarks>
    public void ApplyExplorerVisibility()
    {
        if (_syncRootId is null) return;

        if (!WinRtSyncRoot.ShowInTree(_syncRootId, _config.ShowInExplorer) && !_config.ShowInExplorer)
            _log($"[{FolderId}] liess sich nicht aus dem Explorer-Baum nehmen.");
    }

    /// <summary>Meldet Aenderungen im Ordner, ohne dass jemand danach sucht.</summary>
    private FileSystemWatcher? _wache;

    /// <summary>
    /// Haengt einen Beobachter an den Ordner.
    /// </summary>
    /// <remarks>
    /// Die Meldungen der Cloud-Files-Schicht decken nur ab, was durch sie
    /// hindurchgeht. Eine Datei, die ein anderes Programm neu in den Ordner
    /// schreibt, war nie ein Platzhalter -- fuer sie kommt keine. Bisher fand
    /// sie nur der Durchgang, und der musste deshalb jede Minute ueber jede
    /// Datei laufen: bei einundsiebzigtausend Dateien der teuerste Posten des
    /// Programms.
    ///
    /// Der Beobachter meldet sie sofort und kostet nichts, solange nichts
    /// geschieht. Der Durchgang bleibt als Sicherheitsnetz, nur seltener.
    ///
    /// Was wir selbst schreiben, faellt in NoteLocalChange heraus -- dort
    /// steht die Sperre, die eine laufende Hydration von einer fremden
    /// Aenderung unterscheidet.
    /// </remarks>
    private void BeobachteOrdner()
    {
        if (!_config.WatchChanges)
        {
            _log($"[{FolderId}] ohne Beobachter, wie eingestellt: es wird regelmaessig durchgegangen.");
            OhneBeobachter();
            return;
        }

        try
        {
            var wache = new FileSystemWatcher(_config.LocalPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite | NotifyFilters.Size,

                // Der Kern haelt die Ereignisse in einem Puffer, bis wir sie
                // abholen. Laeuft er ueber, gehen sie verloren -- und bei
                // einem Programm, das tausend Dateien auf einmal schreibt,
                // laeuft der voreingestellte schnell ueber.
                InternalBufferSize = 64 * 1024
            };

            wache.Created += (_, e) => NoteLocalChange(Lang(e.FullPath));
            wache.Changed += (_, e) => NoteLocalChange(Lang(e.FullPath));
            wache.Deleted += (_, e) => NoteLocalDelete(Lang(e.FullPath));
            wache.Renamed += (_, e) =>
            {
                // Der alte Pfad wird selbst zusammengesetzt, nicht aus
                // OldFullPath gelesen: bei sehr langen Pfaden wirft die
                // Eigenschaft, und ein Wurf im Rueckruf des Beobachters nimmt
                // den ganzen Beobachter mit.
                var vorher = Path.Combine(Path.GetDirectoryName(e.FullPath) ?? "", e.OldName ?? "");
                NoteLocalDelete(Lang(vorher));
                NoteLocalChange(Lang(e.FullPath));
            };

            // Ein uebergelaufener Puffer heisst: wir wissen nicht, was uns
            // entgangen ist. Dann hilft nur der Durchgang, und zwar sofort.
            wache.Error += (_, e) =>
            {
                _log($"[{FolderId}] Beobachter: {e.GetException().Message} -- es wird neu durchgegangen.");
                _lastScan = DateTime.MinValue;
                Wake();
            };

            wache.EnableRaisingEvents = true;
            _wache = wache;
            MitBeobachter();
        }
        catch (Exception ex)
        {
            // Ohne Beobachter faellt das Programm auf den Durchgang zurueck.
            // Das ist langsamer, aber vollstaendig.
            _log($"[{FolderId}] kein Beobachter fuer \"{_config.LocalPath}\": {ex.Message}");
            OhneBeobachter();
        }
    }

    /// <summary>
    /// Loest einen kurzen 8.3-Namen in den langen auf.
    /// </summary>
    /// <remarks>
    /// Der Beobachter meldet gelegentlich Pfade in der alten Schreibweise --
    /// "PROGRA~1" statt "Program Files". Unter einem solchen Namen findet
    /// sich im Index nichts, und die Aenderung ginge verloren.
    ///
    /// Gefragt wird nur, wenn eine Tilde vorkommt. Der Aufruf kostet einen
    /// Zugriff auf das Dateisystem, und der Normalfall soll ihn nicht zahlen.
    /// </remarks>
    private static string Lang(string pfad)
    {
        if (!pfad.Contains('~')) return pfad;

        try
        {
            var puffer = new char[1024];
            var laenge = GetLongPathNameW(pfad, puffer, (uint)puffer.Length);

            // 0 heisst Fehler, groesser als der Puffer heisst zu lang. In
            // beiden Faellen bleibt der gemeldete Pfad die beste Auskunft.
            return laenge > 0 && laenge < puffer.Length ? new string(puffer, 0, (int)laenge) : pfad;
        }
        catch (Exception)
        {
            return pfad;
        }
    }

    [System.Runtime.InteropServices.LibraryImport(
        "kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling =
            System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial uint GetLongPathNameW(string kurz, char[] lang, uint groesse);

    /// <summary>
    /// Legt den Beobachter neu an, wenn er ausgefallen ist.
    /// </summary>
    /// <remarks>
    /// Ein ausgehaengtes Laufwerk nimmt ihn mit: er meldet einen Fehler und
    /// stellt die Arbeit ein. Kommt das Laufwerk zurueck, kommt er nicht von
    /// selbst mit -- der Ordner waere danach still, ohne dass es jemand
    /// bemerkt, bis der naechste vollstaendige Durchgang laeuft.
    ///
    /// Geprueft wird im Hintergrundlauf. Solange er steht, ist das ein
    /// Vergleich zweier Flaggen.
    /// </remarks>
    private void PflegeBeobachter()
    {
        if (!_config.WatchChanges) return;
        if (_wache is { EnableRaisingEvents: true }) return;
        if (!Directory.Exists(_config.LocalPath)) return;

        _wache?.Dispose();
        _wache = null;

        _log($"[{FolderId}] der Beobachter wird neu angelegt.");
        BeobachteOrdner();

        // Was waehrend seiner Abwesenheit geschah, hat er nicht gemeldet.
        _lastScan = DateTime.MinValue;
    }

    /// <summary>Woran eine wirklich eingehaengte Freigabe zu erkennen ist.</summary>
    private string MarkerPath => Path.Combine(_config.LocalPath, MarkerFolder);

    /// <summary>
    /// Legt die Ordnermarkierung an, falls sie fehlt.
    /// </summary>
    /// <remarks>
    /// Ein leeres Verzeichnis, versteckt, das nie uebertragen wird. Sein Wert
    /// liegt allein darin, dass es fehlt, wenn der Ordner nicht da ist: bei
    /// einer nicht eingehaengten Platte, einem getrennten Netzlaufwerk, einer
    /// fehlenden Speicherkarte sieht das Verzeichnis leer aus statt
    /// unerreichbar. Ohne dieses Unterscheidungsmerkmal waere jede solche
    /// Stoerung von einer Loeschung des gesamten Ordners nicht zu trennen --
    /// und wir wuerden sie an die Gegenstelle weitergeben.
    /// </remarks>
    private void MarkierungAnlegen()
    {
        if (!MarkierungAnlegen(_config.LocalPath, out var fehler))
            _log($"[{FolderId}] Ordnermarkierung liess sich nicht anlegen: {fehler}");
    }

    /// <summary>
    /// Legt die Markierung an. Aufgerufen beim Verbinden einer Freigabe.
    /// </summary>
    /// <remarks>
    /// Das Verbinden ist der Augenblick, in dem der Anwender erklaert, dass
    /// dieser Ordner der richtige ist. Genau dort gehoert die Markierung hin
    /// -- und sonst nirgends von selbst.
    /// </remarks>
    public static bool MarkierungAnlegen(string localPath, out string fehler)
    {
        fehler = "";

        try
        {
            var pfad = Path.Combine(localPath, MarkerFolder);
            if (Directory.Exists(pfad)) return true;

            var marker = Directory.CreateDirectory(pfad);
            marker.Attributes |= FileAttributes.Hidden;
            return true;
        }
        catch (Exception ex)
        {
            fehler = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Nimmt die Markierung fort. Aufgerufen beim Trennen einer Freigabe.
    /// </summary>
    /// <remarks>
    /// Ein Ordner, der zu keiner Freigabe mehr gehoert, soll auch nicht
    /// bezeugen, dass er zu einer gehoert. Wird er spaeter wieder verbunden,
    /// entsteht sie dabei neu -- und bis dahin bescheinigt sie nichts.
    /// </remarks>
    public void MarkierungEntfernen()
    {
        try
        {
            if (Directory.Exists(MarkerPath)) Directory.Delete(MarkerPath, recursive: true);
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Ordnermarkierung liess sich nicht entfernen: {ex.Message}");
        }
    }

    /// <summary>Ob die Ordnermarkierung fehlt. Fuer die Oberflaeche.</summary>
    public bool MarkierungFehlt
        => _config.LocalPath.Length > 0 && !Directory.Exists(MarkerPath);

    /// <summary>
    /// Stellt die Ordnermarkierung auf ausdrueckliche Anweisung wieder her.
    /// </summary>
    /// <remarks>
    /// Von selbst geschieht das nie, ausser beim ersten Lauf einer Freigabe.
    /// Die Markierung wieder hinzustellen heisst zu erklaeren, dass dies der
    /// richtige Ordner ist und sein Inhalt vollstaendig -- und was danach
    /// fehlt, gilt als geloescht und wird weitergegeben. Diese Erklaerung
    /// kann nur abgeben, wer den Ordner kennt.
    /// </remarks>
    public void MarkierungHerstellen()
    {
        MarkierungAnlegen();
        _log($"[{FolderId}] Ordnermarkierung wiederhergestellt.");
    }

    /// <summary>
    /// Bricht den Start ab, wenn der Ordner nicht erreichbar ist.
    /// </summary>
    /// <remarks>
    /// Nicht nur Loeschungen sind dann gefaehrlich, sondern auch das
    /// Gegenteil: wo der gemeinte Ordner fehlt, wuerde alles neu angelegt --
    /// auf irgendeinem Laufwerk, das gerade unter diesem Pfad erreichbar ist.
    /// Syncthing haelt einen solchen Ordner deshalb ganz an, und das ist
    /// richtig.
    /// </remarks>
    private void MarkierungPruefen()
    {
        if (Directory.Exists(MarkerPath)) return;

        // Der einzige Fall, in dem sie von selbst entsteht: wir fuehren noch
        // keine einzige eigene Datei. Dann steht nichts auf dem Spiel -- ohne
        // eigenen Bestand kann nichts faelschlich als geloescht gelten. Das
        // deckt den Augenblick zwischen dem Vorbereiten und dem Verbinden
        // einer neuen Freigabe ab; regulaer entsteht sie beim Verbinden.
        if (_index is null || _index.LocalCount == 0)
        {
            MarkierungAnlegen();
            return;
        }

        throw new InvalidOperationException(
            $"Die Ordnermarkierung \"{MarkerFolder}\" fehlt, obwohl hier {_index.LocalCount} eigene " +
            "Dateien gefuehrt werden. Der Ordner ist damit nicht erreichbar -- eine nicht " +
            "eingehaengte Platte, ein getrenntes Netzlaufwerk, ein umbenannter Ordner. Es wird " +
            "nichts abgeglichen, bis er wieder da ist. Ist es doch der richtige Ordner, stellt " +
            "\"Ordnermarkierung wiederherstellen\" sie wieder her.");
    }

    /// <summary>
    /// Findet, was geloescht wurde, waehrend das Programm nicht lief.
    /// </summary>
    /// <remarks>
    /// Waehrend des Betriebs meldet das Dateisystem jede Loeschung. Was
    /// dazwischen geschieht, meldet niemand, und aus blosser Abwesenheit wird
    /// sonst nie auf eine Loeschung geschlossen -- eine fehlende Datei kann
    /// verschoben, umbenannt oder von einem Laufwerk sein, das gerade nicht
    /// da ist.
    ///
    /// Zwei Bedingungen machen den Schluss trotzdem sicher. Die Markierung
    /// sagt, dass der Ordner wirklich da ist und nicht bloss leer aussieht.
    /// Und gezaehlt wird nur, was wir selbst als vorhanden gefuehrt haben --
    /// das trennt "der Benutzer hat geloescht" von "wir haben es nie geholt".
    /// Ohne die zweite Bedingung meldete eine Freigabe im Modus
    /// "vollstaendig lokal" beim ersten Start jede noch nicht geholte Datei
    /// als geloescht.
    /// </remarks>
    private void OfflineGeloeschte()
    {
        if (_index is null) return;

        if (!Directory.Exists(MarkerPath))
        {
            // Beim ersten Lauf einer Freigabe gibt es sie noch nicht -- und
            // dann ist auch nichts zu vergleichen. Woran das zu erkennen ist:
            // wir fuehren noch keine einzige eigene Datei.
            if (_index.LocalCount == 0)
            {
                MarkierungAnlegen();
                return;
            }

            // Eigener Bestand vorhanden und die Markierung weg: das ist der
            // Fall, fuer den es sie gibt. Sie wird von niemandem geloescht,
            // also fehlt hier nicht sie, sondern der Ordner.
            //
            // Angelegt wird sie jetzt gerade nicht. Sie wieder hinzustellen
            // hiesse, das Merkmal zu beseitigen: beim naechsten Start waere
            // sie da, der Ordner leer, und jede Datei gaelte als geloescht.
            _log($"[{FolderId}] Die Ordnermarkierung \"{MarkerFolder}\" fehlt, obwohl hier " +
                 $"{_index.LocalCount} eigene Dateien gefuehrt werden. Der Ordner ist damit nicht " +
                 "erreichbar -- eine nicht eingehaengte Platte, ein getrenntes Netzlaufwerk, ein " +
                 "umbenannter Ordner. Es wird keine Loeschung gemeldet.");
            return;
        }

        var fehlend = new List<string>();

        foreach (var eigen in _index.LocalFrom(0))
        {
            if (eigen.Deleted || IsHousekeeping(eigen.Name)) continue;

            var path = LocalPathOf(eigen.Name);
            var da = eigen.Type == FileInfoType.Directory
                ? Directory.Exists(path)
                : File.Exists(path);

            if (!da) fehlend.Add(eigen.Name);
        }

        if (fehlend.Count == 0) return;

        // Dieselbe Schranke wie im laufenden Betrieb. Eine grosse Zahl auf
        // einmal ist fast nie eine Loeschung.
        if (fehlend.Count > MaximumDeletions)
        {
            _log($"[{FolderId}] {fehlend.Count} eigene Dateien fehlen im Ordner. " +
                 "Das sind zu viele fuer eine Loeschung von Hand; es wird nichts gemeldet. " +
                 "Stimmt der Ausgangsordner noch?");
            return;
        }

        foreach (var name in fehlend) _removed[name] = 0;

        _log($"[{FolderId}] {fehlend.Count} Dateien wurden entfernt, waehrend das Programm nicht lief. " +
             "Die Loeschung wird weitergegeben.");
    }

    /// <summary>
    /// Ob der Inhalt dieser Datei hier noch fehlt.
    /// </summary>
    /// <remarks>
    /// Zwei Faelle, und seit "vollstaendig lokal" ohne Platzhalter arbeitet,
    /// ist der erste der Normalfall: die Datei ist gar nicht da. Der zweite
    /// ist ein Platzhalter ohne Inhalt -- aus einer frueheren Fassung oder aus
    /// einem Wechsel der Betriebsart.
    ///
    /// Dieselben drei Merkmale wie beim Durchgang ueber den Ordner. Ob eine
    /// Datei Inhalt haelt, darf nicht an zwei Stellen unterschiedlich
    /// beantwortet werden.
    /// </remarks>
    private static bool FehltHier(string path)
    {
        if (!File.Exists(path)) return true;

        return ((uint)new System.IO.FileInfo(path).Attributes
                & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0;
    }

    private async Task ApplyModeAsync(CancellationToken ct)
    {
        if (_config.Mode != ShareMode.AlwaysLocal) return;

        // "Vollstaendig lokal bereithalten" bedeutet, auf jede Datei einmal
        // zuzugreifen. Der erste Lesezugriff loest die Hydration aus.
        // Leere Dateien gehoeren dazu. Sie waren hier ausgenommen, weil es
        // nichts zu laden gibt -- aber "nichts zu laden" heisst nicht "schon
        // da": als Platzhalter blieben sie fuer immer leer.
        //
        var pending = Enumerate()
            .Where(e => !e.IsDirectory)
            .Select(e => LocalPathOf(e.RelativePath))
            .Where(FehltHier)
            .ToList();

        if (pending.Count == 0) return;
        _log($"[{FolderId}] Modus AlwaysLocal: lade {pending.Count} noch fehlende Dateien herunter ...");

        // Drei Zahlen, und sie duerfen nicht verwechselt werden: was versucht
        // wurde, was tatsaechlich hier liegt, und was scheiterte. Gezaehlt
        // wurden bisher die Versuche und "heruntergeladen" genannt -- 381 von
        // 3341 im Fenster, waehrend daneben 264 von 264 MB offen standen.
        var versucht = 0;
        var geholt = 0;
        var gescheitert = 0;
        var beschaedigt = 0;
        SetPhase(SyncPhase.Inhalte, 0, pending.Count);

        // Ausdruecklich auf einen eigenen Faden, und nicht bloss "await".
        //
        // Der Aufruf kommt ueber das Verbinden aus der Oberflaeche. Ohne
        // Task.Run erbt jede Fortsetzung dieser Schleife deren Zusammenhang:
        // die Rueckkehr nach jedem Lesen wird dem Oberflaechen-Faden
        // zugestellt und muss sich dort einreihen. Ein Lauf ueber tausend
        // Dateien gehoert nicht in die Warteschlange des Fensters.
        await Task.Run(() => Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (path, token) =>
            {
                try
                {
                    await MaterialiseAsync(path, token).ConfigureAwait(false);
                    Interlocked.Increment(ref geholt);
                }
                catch (Exception ex) when (IstBeschaedigt(ex))
                {
                    // Einmal zaehlen, nicht je Datei eine Zeile. Der Grund ist
                    // fuer alle derselbe, und er steht unten in einem Satz.
                    Interlocked.Increment(ref beschaedigt);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref gescheitert);
                    _log($"  {Path.GetFileName(path)}: {ex.Message}");
                }

                var fertig = Interlocked.Increment(ref versucht);

                // Der Balken zeigt, was hier liegt, nicht, was versucht wurde.
                var da = Volatile.Read(ref geholt);
                SetPhase(SyncPhase.Inhalte, da, pending.Count);

                if (fertig % 50 == 0)
                {
                    var daneben = Volatile.Read(ref gescheitert) + Volatile.Read(ref beschaedigt);
                    _log($"[{FolderId}] {da} von {pending.Count} geholt" +
                         (daneben > 0 ? $", {daneben} gescheitert" : "") + ".");
                }
            }), ct).ConfigureAwait(false);

        if (beschaedigt > 0)
        {
            _log($"[{FolderId}] {beschaedigt} Platzhalter meldet Windows als beschaedigt. " +
                 "Sie lassen sich weder lesen noch loeschen noch umbenennen, solange der " +
                 "Ordner angemeldet ist. Abhilfe: den Ordner hier entfernen und neu anlegen; " +
                 "der Inhalt liegt auf der Gegenstelle.");
        }

        // "Vollstaendig lokal" nur, wenn es auch stimmt.
        _log(geholt == pending.Count
            ? $"[{FolderId}] vollstaendig lokal."
            : $"[{FolderId}] {geholt} von {pending.Count} geholt, {pending.Count - geholt} fehlen weiterhin.");
    }

    /// <summary>
    /// Ob Windows den Platzhalter selbst fuer beschaedigt haelt.
    /// </summary>
    /// <remarks>
    /// ERROR_CLOUD_FILE_METADATA_CORRUPT (363). In diesem Zustand geht gar
    /// nichts mehr: kein Lesen, kein Loeschen, kein Umbenennen, nicht einmal
    /// ueber fsutil. Der Dateisystemfilter weist jeden Zugriff ab, weil er
    /// die Verwaltungsdaten der Datei nicht entziffern kann.
    ///
    /// Es hat deshalb keinen Zweck, es Datei fuer Datei zu melden. Der Grund
    /// ist fuer alle derselbe.
    /// </remarks>
    private static bool IstBeschaedigt(Exception ex)
        => ex is IOException && (ex.HResult & 0xFFFF) == 363;

    // ------------------------------------------------------------ Vorschaubilder

    /// <summary>Alle laufenden Freigaben. Der Vorschau-Erzeuger sucht hier die zustaendige.</summary>
    private static readonly List<ShareHost> Laufende = [];

    /// <summary>
    /// Erzeugt die Vorschau zu einem lokalen Pfad, sofern eine Freigabe
    /// zustaendig ist. Aufrufer ist die Shell-Erweiterung.
    /// </summary>
    public static bool ProduceThumbnail(string localFilePath)
    {
        ShareHost[] shares;
        lock (Laufende) shares = [.. Laufende];

        // Zwei verschiedene Faelle, und sie duerfen nicht dieselbe Meldung
        // bekommen: niemand ist zustaendig, oder jemand war zustaendig und
        // konnte nichts erzeugen. Der zweite nennt seinen Grund selbst.
        var zustaendig = false;

        foreach (var share in shares)
        {
            if (!share.Owns(localFilePath)) continue;

            zustaendig = true;
            if (share.Produce(localFilePath)) return true;
        }

        // Der Fall, in dem die Anfrage ueberhaupt ankam und trotzdem nichts
        // geschieht -- ohne diese Zeile sieht er genauso aus wie eine Anfrage,
        // die nie gestellt wurde.
        if (!zustaendig)
        {
            Melden?.Invoke($"Vorschau fuer \"{localFilePath}\": keine zustaendige Freigabe " +
                           $"unter {shares.Length} laufenden.");
        }

        return false;
    }

    /// <summary>
    /// Wohin die Vorschau-Kette meldet.
    /// </summary>
    /// <remarks>
    /// Die Kette laeuft ueber statische Einstiegspunkte, weil die
    /// Shell-Erweiterung keine Freigabe kennt. Ein Protokoll je Freigabe gibt
    /// es dort nicht; ohne diesen Weg bleibt jede Absage stumm.
    /// </remarks>
    public static Action<string>? Melden { get; set; }

    /// <summary>Die Freigabe, zu der dieser Pfad gehoert.</summary>
    public static ShareHost? Owning(string localPath)
    {
        ShareHost[] shares;
        lock (Laufende) shares = [.. Laufende];

        return shares.FirstOrDefault(s => s.Owns(localPath));
    }

    /// <summary>
    /// Haelt die genannten Pfade lokal oder gibt ihren Platz frei.
    /// </summary>
    /// <remarks>
    /// Fuer das Kontextmenue. Windows kennt beide Befehle nur je Datei; bei
    /// einem Ordner mit tausend Bildern ist das keine brauchbare Geste. Ein
    /// Verzeichnis wird deshalb aufgeloest, und die Auswahl darf gemischt
    /// sein.
    ///
    /// Beim Freigeben gilt dieselbe Sperre wie ueberall: eine Datei, die die
    /// Platzhalter-Schwelle nicht erreicht hat, behaelt ihren Inhalt. Der
    /// Befehl aus dem Menue ist eine Bitte, keine Vollmacht.
    /// </remarks>
    public (int Files, long Bytes) SetLocal(IEnumerable<string> paths, bool keep)
    {
        var anzahl = 0;
        long bytes = 0;

        foreach (var pfad in Dateien(paths))
        {
            if (NameOf(pfad) is not { } name) continue;

            try
            {
                if (keep)
                {
                    _mount?.SetPinned(pfad, true);

                    // Anheften allein holt nichts. Ein einziges gelesenes Byte
                    // loest die Hydration der ganzen Datei aus -- derselbe Weg,
                    // den auch "vollstaendig lokal" nimmt.
                    if (IsPlaceholder(pfad))
                    {
                        using var strom = File.OpenRead(pfad);
                        strom.ReadByte();
                    }
                }
                else
                {
                    _mount?.SetPinned(pfad, false);
                    if (!MayEvict(name) || _cache?.Evict(name) != true) continue;
                }

                anzahl++;
                bytes += new FileInfo(pfad).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"[{FolderId}] \"{name}\": {ex.Message}");
            }
        }

        if (!keep) _cache?.Persist();
        CacheChanged?.Invoke();

        return (anzahl, bytes);
    }

    /// <summary>Der Name eines Pfades, wie das Protokoll ihn fuehrt.</summary>
    public string? RelativeNameOf(string localPath) => Owns(localPath) ? NameOf(localPath) : null;

    /// <summary>
    /// Wie viele Dateien unter diesen Namen die Platzhalter-Schwelle noch
    /// nicht erreicht haben.
    /// </summary>
    /// <remarks>
    /// Dieselbe Frage, die der Auswahlbaum je Knoten beantwortet -- hier fuer
    /// das Kontextmenue, das keinen Baum hat.
    /// </remarks>
    public int Blocking(IReadOnlyList<string> names)
    {
        if (_index is null) return 0;

        var offen = 0;

        lock (_indexGate)
            foreach (var (name, _, _, isDirectory, hatInhalt) in _index.EnumerateLight())
            {
                if (isDirectory || hatInhalt) continue;

                foreach (var zweig in names)
                    if (name.Equals(zweig, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(zweig + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        offen++;
                        break;
                    }
            }

        return offen;
    }

    /// <summary>
    /// Die oberste Ebene des Index: Verzeichnisse und lose Dateien.
    /// </summary>
    /// <remarks>
    /// Fuer den Uebergang von "alles ausgewaehlt" -- das steht als leere
    /// Liste -- zu einer ausgeschriebenen Auswahl, aus der sich etwas
    /// herausnehmen laesst. Die losen Dateien werden dabei durch ihren
    /// Sammeleintrag vertreten, sonst stuenden sie einzeln in der Datei.
    /// </remarks>
    public List<string> TopLevelNames()
    {
        var namen = new List<string> { "*" };
        if (_index is null) return namen;

        lock (_indexGate)
            foreach (var (name, _, _, isDirectory, _) in _index.EnumerateLight())
                if (isDirectory && !name.Contains('/') && name.Length > 0)
                    namen.Add(name);

        return namen;
    }

    /// <summary>Loest Verzeichnisse in ihre Dateien auf, Auswahl bleibt Auswahl.</summary>
    private static IEnumerable<string> Dateien(IEnumerable<string> paths)
    {
        foreach (var pfad in paths)
        {
            if (File.Exists(pfad)) { yield return pfad; continue; }
            if (!Directory.Exists(pfad)) continue;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0
            };

            foreach (var datei in Directory.EnumerateFiles(pfad, "*", options))
                yield return datei;
        }
    }

    private bool Owns(string localFilePath)
    {
        var root = _config.LocalPath.TrimEnd(Path.DirectorySeparatorChar);
        return localFilePath.Length > root.Length
               && localFilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && localFilePath[root.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Beschafft die Vorschau zu genau dieser Datei.
    /// </summary>
    /// <remarks>
    /// Es wird nichts auf Vorrat erzeugt. 500 Bilder vorab kosten 500
    /// Bloecke, von denen die meisten nie angesehen werden. Geholt wird genau
    /// das, wonach gefragt wurde.
    ///
    /// Ein Vorlauf auf die uebrigen Bilder desselben Ordners lag nahe, war
    /// aber falsch: der Explorer greift fuer die Ordnersymbole auf einzelne
    /// Bilder aus Unterordnern zu, und jeder dieser Zugriffe haette den
    /// ganzen Unterordner nachgezogen. Gemessen wurden so 502 von 511 Bildern
    /// statt der 145, nach denen tatsaechlich gefragt wurde.
    /// </remarks>
    private bool Produce(string localFilePath)
    {
        // Jede Absage nennt ihren Grund. Eine Vorschau, die ausbleibt, sieht
        // sonst immer gleich aus -- gleich ob niemand gefragt hat, die
        // Verbindung fehlt, der Schalter aus ist oder die Datei schon einmal
        // ohne eingebettetes Bild angesehen wurde.
        if (_thumbnails is null || _index is null)
            return Nein(localFilePath, "der Ordner ist noch nicht bereit");

        if (_connections.IsEmpty)
            return Nein(localFilePath, "keine Verbindung zu einer Gegenstelle");

        if (!_app.GenerateThumbnails)
            return Nein(localFilePath, "Vorschaubilder sind abgeschaltet");

        if (_thumbnails.KnownWithout(localFilePath))
            return Nein(localFilePath, "die Datei traegt kein eingebettetes Bild");

        return Await(FetchThumbnailAsync(RelativeOf(localFilePath), CancellationToken.None));
    }

    /// <summary>Schreibt den Grund einer Absage und liefert <c>false</c>.</summary>
    /// <remarks>
    /// Je Datei einmal. Der Explorer fragt nach derselben Datei mehrfach, und
    /// ein Grund, der sich nicht aendert, gehoert einmal ins Protokoll.
    /// </remarks>
    private bool Nein(string localFilePath, string grund)
    {
        if (_warned.TryAdd("vorschau:" + localFilePath, 0))
            _log($"[{FolderId}] keine Vorschau fuer \"{Path.GetFileName(localFilePath)}\": {grund}.");

        return false;
    }

    /// <summary>
    /// Wartet auf ein Ergebnis, hoechstens jedoch bis zum Ablauf einer Frist.
    /// </summary>
    /// <remarks>
    /// Der Aufruf kommt aus einer COM-Methode, die ein Ergebnis zurueckgeben
    /// muss. Warten laesst sich deshalb nicht vermeiden. Die Frist ist
    /// trotzdem noetig: antwortet die Gegenstelle nicht, soll der Explorer
    /// sein Ersatzsymbol zeigen, statt den Ordner anzuhalten.
    /// </remarks>
    private bool Await(Task<bool> work)
    {
        try
        {
            return work.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Vorschau: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Holt den Dateikopf ueber BEP und legt die darin eingebettete Vorschau
    /// ab. Ein Kopf ist genau ein Block. Der Platzhalter bleibt dehydriert.
    /// </summary>
    private async Task<bool> FetchThumbnailAsync(string relativePath, CancellationToken ct)
    {
        if (_thumbnails is null || _index is null || _connections.IsEmpty) return false;

        var local = LocalPathOf(relativePath);

        // Ein Vorschaubild ist ein Dateikopf und damit Uebertragung.
        if (IsPaused) return Nein(local, "der Abgleich ist angehalten");

        if (_thumbnails.Has(local)) return true;

        // Jede Absage nennt ihren Grund. Blieben diese hier stumm, meldete der
        // Aufrufer "keine zustaendige Freigabe" -- und das waere falsch.
        if (!_index.TryGet(relativePath, out var file))
            return Nein(local, "die Gegenstelle fuehrt die Datei nicht");

        if (file.Size <= 0)
            return Nein(local, "die Datei ist leer");

        await _thumbnailGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Waehrend des Wartens kann ein anderer Aufruf fertig geworden sein.
            if (_thumbnails.Has(local)) return true;

            var wanted = Math.Min(ExifThumbnail.RequiredPrefixBytes, file.Size);
            if (LineFor(file.Name) is not { } vorschauVerbindung)
                return Nein(local, "keine erreichbare Gegenstelle haelt diese Datei");

            var head = await FileFetcher.FetchRangeAsync(
                vorschauVerbindung, FolderId, file, 0, wanted, _app.Parallelism, ct: ct)
                .ConfigureAwait(false);

            NoteReceived(head.Length);

            var thumbnail = ExifThumbnail.TryExtract(head, out var grund);
            if (thumbnail is null)
            {
                // Vermerkt, damit derselbe Dateikopf nicht bei jeder Ansicht
                // erneut geholt wird.
                _thumbnails.MarkWithout(local);

                // Mit der Zahl daneben, denn "kein Vorschaubild" und "der
                // Dateianfang kam gar nicht an" sind zwei verschiedene Dinge.
                return Nein(local, $"{grund} ({head.Length} von {wanted} Bytes geholt)");
            }

            _thumbnails.Save(local, thumbnail);
            ThumbnailProduced?.Invoke(Interlocked.Increment(ref _thumbnailsMade));
            return true;
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private string RelativeOf(string localFilePath)
    {
        var root = _config.LocalPath.TrimEnd(Path.DirectorySeparatorChar);
        return localFilePath[root.Length..]
            .TrimStart(Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    // ------------------------------------------------------------ Cache

    public async Task EnforceLimitsAsync()
    {
        // Angehalten wird kein Platz freigegeben. Das Freigeben loescht lokalen
        // Inhalt, und genau davor soll das Anhalten schuetzen.
        if (IsPaused) return;

        if (_cache is null) return;
        await _cache.EnforceLimitsAsync();
        CacheChanged?.Invoke();
    }

    public string Stats()
        => _cache is null
            ? $"[{FolderId}] noch nicht bereit"
            : $"[{FolderId}] {_cache.FileCount} Dateien lokal, " +
              $"{_cache.UsedBytes / (1024.0 * 1024.0):0.0} MB" +
              (_cache.MaxBytes > 0 ? $" von {_cache.MaxBytes / (1024.0 * 1024.0):0.0} MB" : "");

    // ------------------------------------------------------------ IContentSource

    public IReadOnlyList<VirtualEntry> Enumerate()
        => _index!.EnumerateLight()
            // Auch hier, nicht nur beim Aufnehmen in den Index. Ueber diesen
            // Weg entstehen die Platzhalter, und er ging bisher an der
            // Pruefung vorbei: eine ".stfolder" der Gegenstelle wurde als
            // Verzeichnis angelegt und ihre Markierungsdatei geholt --
            // Verwaltung eines fremden Geraets, hier nachgebaut.
            .Where(e => !IsHousekeeping(e.Name))
            .Where(e => !_config.IsIgnored(e.Name))
            .Where(e => _config.Includes(e.Name, e.IsDirectory))
            .Select(e => new VirtualEntry(
                e.Name, e.Size, DateTimeOffset.FromUnixTimeSeconds(e.ModifiedS), e.IsDirectory))
            .ToList();

    /// <summary>
    /// Die Uebertragungen, die gerade als ein Bereich laufen. Je Datei gibt
    /// es eine.
    /// </summary>
    private readonly ConcurrentDictionary<string, TransferInfo> _ranges = new(StringComparer.Ordinal);

    public IDisposable BeginRange(string relativePath, long totalLength)
    {
        // Erst pruefen, ob der Platz ueberhaupt reicht. Wird erst geladen und
        // danach aufgeraeumt, ist die Uebertragung bereits gelaufen.
        var limit = _cache is null ? CacheLimits.Limit.None : _app.Cache.CanHold(totalLength, _config.LocalPath);
        var grenzen = _app.Cache.LimitsFor(_config.LocalPath);
        if (limit != CacheLimits.Limit.None)
        {
            var hit = new CacheLimitHit(
                FolderId, relativePath, totalLength,
                limit == CacheLimits.Limit.Usage,
                limit == CacheLimits.Limit.Usage ? grenzen.MaxBytes : grenzen.MinimumFreeBytes);

            LimitReached?.Invoke(hit);
            throw new IOException(
                $"\"{relativePath}\" passt nicht: " +
                (hit.UsageLimit ? "groesser als das Verbrauchs Limit" : "es bliebe zu wenig frei"));
        }

        var transfer = new TransferInfo(FolderId, relativePath, totalLength);
        _ranges[relativePath] = transfer;
        TransferStarted?.Invoke(transfer);

        // Solange der Bereich laeuft, schreiben wir selbst in diese Datei.
        // Die Meldungen, die daraus entstehen, sind keine Aenderung von aussen.
        return new Range(this, relativePath, transfer, HoldHydration(relativePath));
    }

    /// <summary>Schliesst den Bereich ab, sobald die Hydration ihn verlaesst.</summary>
    private sealed class Range(ShareHost host, string path, TransferInfo transfer, IDisposable hold) : IDisposable
    {
        public void Dispose()
        {
            hold.Dispose();

            // Nur den eigenen Eintrag entfernen. Eine zweite, ueberlappende
            // Anfrage kann ihn inzwischen ersetzt haben.
            host._ranges.TryRemove(new KeyValuePair<string, TransferInfo>(path, transfer));

            if (transfer.State != TransferState.Fehler)
            {
                transfer.State = TransferState.Fertig;
                transfer.DoneBytes = transfer.TotalBytes;
            }

            host.TransferFinished?.Invoke(transfer);
        }
    }

    public async Task<byte[]> ReadAsync(string relativePath, long offset, long length, CancellationToken ct)
    {
        if (IsPaused)
            throw new InvalidOperationException($"\"{FolderId}\" ist angehalten.");
        if (_connections.IsEmpty)
            throw new InvalidOperationException($"\"{FolderId}\" ist nicht verbunden.");

        if (!_index!.TryGet(relativePath, out var file))
            throw new FileNotFoundException($"\"{relativePath}\" ist nicht im Index.");

        // Gehoert dieses Stueck zu einem angemeldeten Bereich, zaehlt es auf
        // dessen Eintrag ein. Sonst, etwa bei einem einzelnen Zugriff,
        // bekommt es einen eigenen Eintrag.
        var part = _ranges.TryGetValue(relativePath, out var running);
        var transfer = running ?? new TransferInfo(FolderId, relativePath, length);

        if (!part) TransferStarted?.Invoke(transfer);

        var already = part ? transfer.DoneBytes : 0;

        // Ein einzelnes Stueck kann auch ohne umschliessenden Bereich kommen.
        // Auch dann schreiben wir selbst in die Datei.
        using var hold = HoldHydration(relativePath);

        // Ab hier steht der Auftrag in der Warteschlange, bis ein Platz frei wird.
        var angestellt = Environment.TickCount64;
        await _hydrationGate.WaitAsync(ct).ConfigureAwait(false);

        var gewartet = Environment.TickCount64 - angestellt;
        if (gewartet > 2000)
            _log($"[{FolderId}] \"{relativePath}\" wartete {gewartet} ms auf einen Platz.");
        try
        {
            transfer.State = TransferState.Laeuft;

            var blockSize = Math.Max(file.BlockSize, 1);
            var progress = new Progress<int>(blocks =>
                transfer.DoneBytes = already + Math.Min((long)blocks * blockSize, length));

            // Geholt wird bei einer Gegenstelle, die den Inhalt fuehrt. Eine,
            // die den Namen nur kennt, haette nichts zu liefern.
            var leitung = LineFor(relativePath)
                ?? throw new InvalidOperationException($"\"{FolderId}\" ist nicht verbunden.");

            var data = await FileFetcher.FetchRangeAsync(
                leitung, FolderId, file, offset, length, _app.Parallelism, progress, ct)
                .ConfigureAwait(false);

            transfer.DoneBytes = already + data.Length;
            if (!part) transfer.State = TransferState.Fertig;
            NoteReceived(data.Length);

            _cache?.NoteHydrated(relativePath, data.Length);
            CacheChanged?.Invoke();

            // Nach dem Zuwachs pruefen, ob das Limit noch eingehalten ist.
            // Das laeuft im Hintergrund, damit der Hydrations-Rueckruf nicht
            // darauf wartet.
            _ = Task.Run(EnforceLimitsAsync, CancellationToken.None);

            return data;
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Fehler;
            transfer.Error = ex.Message;
            throw;
        }
        finally
        {
            _hydrationGate.Release();

            // Ein einzelnes Stueck beendet den Bereich nicht. Das tut erst
            // der umschliessende Bereich.
            if (!part) TransferFinished?.Invoke(transfer);
        }
    }

    // ------------------------------------------------------------ Ausliefern

    /// <summary>
    /// Beantwortet eine Blockanfrage der Gegenstelle.
    /// </summary>
    /// <remarks>
    /// Herausgegeben wird nur, was hier bereits vollstaendig auf der Platte
    /// liegt. Eine dehydrierte Datei zu lesen wuerde sie ueber genau die
    /// Verbindung herunterladen, von der die Anfrage kam. Die Bytes liefen
    /// also im Kreis und kaemen als unsere Antwort zurueck. Deshalb wird in
    /// diesem Fall abgesagt und nicht hydriert.
    ///
    /// Geprueft wird der Reihe nach: temporaere Datei, Bestand, Pfad,
    /// Materialisierung, Bereich, Hash. Jede Absage schreibt eine Zeile ins
    /// Protokoll, ein Erfolg keine. Sonst entstuenden hier tausende Zeilen.
    /// </remarks>
    public async Task<(ErrorCode Code, byte[] Data)> ServeAsync(BepRequest request, CancellationToken ct)
    {
        // Unvollstaendige Uebertragungen liegen bei Syncthing in
        // .syncthing.*.tmp. Solche Dateien fuehren wir nicht.
        if (request.FromTemporary)
            return Deny(request, ErrorCode.NoSuchFile, "nach der temporaeren Datei gefragt");

        // Gefragt wird beides: der Index der Gegenstelle und der eigene
        // Bestand. Was wir selbst angekuendigt haben, steht nur im zweiten.
        if (!KnownHere(request.Name))
            return Deny(request, ErrorCode.NoSuchFile, "weder im Index noch im eigenen Bestand");

        // Der Name kommt von aussen. Ohne diese Pruefung waere ein "../" darin
        // ein Lesezugriff auf beliebige Dateien dieses Rechners.
        var local = ResolveInside(request.Name);
        if (local is null)
            return Deny(request, ErrorCode.NoSuchFile, "der Name fuehrt aus der Freigabe heraus");

        var info = new System.IO.FileInfo(local);
        if (!info.Exists)
            return Deny(request, ErrorCode.NoSuchFile, "liegt hier nicht");

        if (((uint)info.Attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0)
            return Deny(request, ErrorCode.NoSuchFile, "liegt hier nur als Platzhalter");

        if (request.Size <= 0 || request.Size > MaximumRequestSize)
            return Deny(request, ErrorCode.NoSuchFile, $"unmoegliche Blockgroesse {request.Size}");

        if (request.Offset < 0 || request.Offset > info.Length - request.Size)
            return Deny(request, ErrorCode.NoSuchFile,
                $"Bereich {request.Offset}+{request.Size} liegt nicht in {info.Length} Bytes");

        byte[] data;
        try
        {
            data = new byte[request.Size];
            await using var stream = new FileStream(
                local, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.Asynchronous);

            // Zwischen der Pruefung oben und dieser Stelle kann die Datei
            // freigegeben worden sein. Das Oeffnen allein holt sie noch nicht,
            // erst das Lesen wuerde es. Deshalb wird hier noch einmal
            // geprueft, solange das guenstig ist. Sonst wird die Datei vom
            // Server heruntergeladen, nur um sie zurueckzugeben.
            if (IsPlaceholder(local))
                return Deny(request, ErrorCode.NoSuchFile, "liegt hier nicht mehr vor");

            stream.Seek(request.Offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Deny(request, ErrorCode.Generic, ex.Message);
        }

        // Der Hash gehoert zur Anfrage, nicht zu unserer Datei. Die
        // Gegenstelle gibt damit an, welchen Inhalt sie erwartet. Weicht
        // unser Hash ab, ist unsere Kopie eine andere und darf nicht als der
        // angeforderte Block ausgeliefert werden.
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), request.Hash.Span))
            return Deny(request, ErrorCode.InvalidFile, "unsere Bytes ergeben einen anderen Hash");

        NoteSent(data.Length);
        NoteServed(request.Name, info.Length, data.Length);
        return (ErrorCode.NoError, data);
    }

    /// <summary>Die Dateien, die gerade zur Gegenstelle hinausgehen.</summary>
    private readonly ConcurrentDictionary<string, TransferInfo> _outgoing = new(StringComparer.Ordinal);

    /// <summary>So lange nach dem letzten Block gilt ein Schwung als laufend.</summary>
    private const long BurstIdleMs = 3_000;

    private readonly Lock _burstGate = new();
    private long _burstDone;
    private long _burstTotal;
    private long _burstTicks;

    /// <summary>Laeuft gerade eine Uebertragung, in welcher Richtung auch immer?</summary>
    public bool Transferring => ActiveProgress.Total > 0;

    /// <summary>
    /// Was gerade laeuft, zusammengezaehlt.
    /// </summary>
    /// <remarks>
    /// Der Rueckstand misst, ob die Indizes uebereinstimmen. Das ist eine
    /// andere Frage als "laeuft gerade etwas": sobald die Gegenstelle unsere
    /// Ankuendigung zurueckspiegelt, steht der Rueckstand auf null, waehrend
    /// die Datei noch Block fuer Block hinausgeht.
    ///
    /// Gezaehlt wird der ganze Schwung und nicht jede Datei fuer sich. Zwischen
    /// zwei Dateien ist die Liste der laufenden Auslieferungen fuer einen
    /// Augenblick leer; wer daran ablaest, ob etwas laeuft, laesst den Balken
    /// bei jeder Datei einmal verschwinden. Erst wenn eine Weile lang kein
    /// Block mehr hinausging, faengt die Zaehlung von vorn an.
    /// </remarks>
    public (long Done, long Total) ActiveProgress
    {
        get
        {
            long done = 0, total = 0;
            foreach (var t in _ranges.Values) { done += t.DoneBytes; total += t.TotalBytes; }

            lock (_burstGate)
            {
                if (_outgoing.IsEmpty && Environment.TickCount64 - _burstTicks > BurstIdleMs)
                {
                    _burstDone = 0;
                    _burstTotal = 0;
                }

                done += _burstDone;
                total += _burstTotal;
            }

            return (done, total);
        }
    }

    /// <summary>So lange darf eine Auslieferung ohne neuen Block dastehen.</summary>
    private const long OutgoingIdleMs = 30_000;

    /// <summary>
    /// Fuehrt Buch ueber eine laufende Auslieferung.
    /// </summary>
    /// <remarks>
    /// Der eingehende Weg klammert seine Stuecke selbst: die Cloud-Files-
    /// Schicht sagt, wann ein Bereich beginnt und wann er endet. Hier gibt es
    /// nichts dergleichen -- die Gegenstelle fragt Block fuer Block und sagt
    /// weder vorher noch nachher etwas. Die Klammer entsteht deshalb hier: der
    /// erste Block einer Datei oeffnet sie, der letzte schliesst sie, und was
    /// dazwischen stehen bleibt, raeumt <see cref="SweepOutgoing"/> fort.
    ///
    /// Ohne diese Buchfuehrung lief jede Auslieferung ohne jede Anzeige durch.
    /// Wer wissen wollte, ob gerade etwas hinausgeht, sah eine leere Liste.
    /// </remarks>
    private void NoteServed(string relativePath, long total, long bytes)
    {
        var frisch = false;

        var transfer = _outgoing.GetOrAdd(relativePath, pfad =>
        {
            frisch = true;
            var neu = new TransferInfo(FolderId, pfad, total, TransferDirection.Hinaus)
            {
                State = TransferState.Laeuft
            };

            TransferStarted?.Invoke(neu);
            return neu;
        });

        lock (_burstGate)
        {
            if (frisch) _burstTotal += total;
            _burstDone += bytes;
            _burstTicks = Environment.TickCount64;
        }

        bool fertig;

        // Mehrere Anfragen zu derselben Datei koennen gleichzeitig laufen.
        lock (transfer)
        {
            transfer.DoneBytes = Math.Min(transfer.TotalBytes, transfer.DoneBytes + bytes);
            transfer.Touched = Environment.TickCount64;
            fertig = transfer.DoneBytes >= transfer.TotalBytes;
        }

        if (fertig) FinishOutgoing(relativePath, transfer);
    }

    /// <summary>
    /// Beendet alle laufenden Auslieferungen.
    /// </summary>
    /// <remarks>
    /// Beim Anhalten und beim Trennen. Ohne das bleiben sie in der Liste
    /// stehen und behaupten "sendet", waehrend niemand mehr sendet: die
    /// Freigabe wird beim naechsten Verbinden neu aufgebaut, ihre Buchfuehrung
    /// faengt bei null an, und die alten Eintraege findet danach niemand mehr,
    /// der sie abschliessen koennte.
    /// </remarks>
    public void FinishOutgoing()
    {
        foreach (var (name, transfer) in _outgoing) FinishOutgoing(name, transfer);
    }

    /// <summary>Beendet Auslieferungen, zu denen nichts mehr nachkommt.</summary>
    private void SweepOutgoing()
    {
        foreach (var (name, transfer) in _outgoing)
        {
            if (Environment.TickCount64 - transfer.Touched < OutgoingIdleMs) continue;
            FinishOutgoing(name, transfer);
        }
    }

    private void FinishOutgoing(string relativePath, TransferInfo transfer)
    {
        // Nur den eigenen Eintrag entfernen. Eine zweite Anfrage kann ihn
        // inzwischen ersetzt haben.
        if (!_outgoing.TryRemove(new KeyValuePair<string, TransferInfo>(relativePath, transfer))) return;

        transfer.State = TransferState.Fertig;
        TransferFinished?.Invoke(transfer);
    }

    private (ErrorCode Code, byte[] Data) Deny(BepRequest request, ErrorCode code, string reason)
    {
        _log($"[{FolderId}] Anfrage nach \"{request.Name}\" Block {request.BlockNo} abgelehnt: {reason}.");
        return (code, []);
    }

    /// <summary>
    /// Setzt einen Namen aus dem Protokoll in einen lokalen Pfad um, oder
    /// liefert <c>null</c>, wenn er aus der Freigabe herausfuehrt.
    /// </summary>
    /// <remarks>
    /// Geprueft wird am aufgeloesten Pfad, nicht am Text. Nur so faellt auch
    /// auf, was ueber Umwege aus der Freigabe hinausfuehrt. Ein absoluter
    /// Name ist besonders heikel: <see cref="Path.Combine(string, string)"/>
    /// uebernimmt ihn stillschweigend und verwirft den Wurzelpfad.
    /// </remarks>
    private string? ResolveInside(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0')) return null;

        try
        {
            var relative = name.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative)) return null;

            var root = Path.GetFullPath(_config.LocalPath).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, relative));

            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch (ArgumentException)
        {
            // Zeichen, die Windows in einem Pfad nicht zulaesst.
            return null;
        }
    }

    // ------------------------------------------------------------ Ende

    /// <summary>
    /// Loest die Bindung vollstaendig: Sync-Root abmelden, Vorschaubilder
    /// verwerfen, Index loeschen. Die lokalen Dateien bleiben erhalten. Ueber
    /// sie entscheidet der Aufrufer.
    /// </summary>
    public async Task UnbindAsync()
    {
        await StopAsync();

        // Ein Ordner, der zu keiner Freigabe mehr gehoert, soll auch nicht
        // bezeugen, dass er zu einer gehoert.
        MarkierungEntfernen();

        // Erst die Platzhalter aufloesen, dann die Wurzel abmelden.
        //
        // Ein Platzhalter ohne angemeldete Wurzel ist fuer Windows kaputt: es
        // findet den Anbieter nicht mehr, der seinen Inhalt liefern koennte.
        // Der Ordner liess sich danach nicht einmal loeschen -- "Die
        // Clouddatei-Metadaten sind beschaedigt und nicht lesbar"
        // (0x8007016B).
        //
        // Aufgeloest, nicht geloescht: eine geholte Datei behaelt ihren
        // Inhalt, ein leerer Platzhalter wird eine leere Datei. Was hier lag,
        // bleibt liegen; es gehoert nur zu keiner Freigabe mehr.
        var offen = 0;
        try { offen = _mount?.RevertPlaceholders() ?? 0; }
        catch (Exception ex) { _log($"[{FolderId}] Platzhalter aufloesen: {ex.Message}"); offen = -1; }

        if (_syncRootId is not null)
        {
            lock (Laufende) Laufende.Remove(this);
            ThumbnailProviderRegistration.DetachFromSyncRoot(_syncRootId);

            // Die Wurzel bleibt angemeldet, solange auch nur ein Platzhalter
            // steht. Eine angemeldete Wurzel ohne laufenden Anbieter ist
            // harmlos -- der naechste Start nimmt sie wieder auf. Ein
            // Platzhalter ohne Wurzel dagegen ist endgueltig verloren.
            if (offen != 0)
            {
                _log($"[{FolderId}] {(offen < 0 ? "Platzhalter konnten nicht geprueft werden" : $"{offen} Platzhalter stehen noch")}; " +
                     "der Ordner bleibt angemeldet. Abmelden wuerde sie unbrauchbar machen.");
            }
            else
            {
                try { WinRtSyncRoot.Unregister(_syncRootId); } catch { /* schon weg */ }
            }

            _syncRootId = null;
        }

        // Erst ausraeumen, dann schliessen.
        //
        // Das Loeschen der Datei ist der sauberere Weg, aber der
        // unzuverlaessigere -- sie muss dafuer freigegeben sein. Ausgeraeumt
        // wird ueber die offene Verbindung, und das gelingt immer. Bleibt die
        // Datei danach liegen, ist sie wenigstens leer.
        try { _index?.ClearAll(); }
        catch (Exception ex) { _log($"[{FolderId}] Index liess sich nicht ausraeumen: {ex.Message}"); }

        _index?.Dispose();
        _index = null;

        // Nicht "egal": bleibt der Index liegen, findet der naechste Versuch
        // ihn vor und holt sich nichts Neues -- der Baum von heute, einen
        // Monat spaeter. Was scheitert, gehoert ins Protokoll.
        var databasePath = Path.Combine(_app.HomeDirectory, $"index-{FolderId}.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(databasePath + suffix); }
            catch (Exception ex) { _log($"[{FolderId}] \"{databasePath + suffix}\" bleibt liegen: {ex.Message}"); }
        }

        try { File.Delete(Path.Combine(_app.HomeDirectory, $"cache-{FolderId}.json")); }
        catch (Exception ex) { _log($"[{FolderId}] Cache-Stand bleibt liegen: {ex.Message}"); }

        _log($"[{FolderId}] Bindung geloest.");
    }

    private string LocalPathOf(string relativePath)
        => Path.Combine(_config.LocalPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Wie oft der Index verdichtet wird.</summary>
    /// <remarks>
    /// Beim Beenden geprueft, hoechstens einmal die Woche ausgefuehrt. Wer
    /// sein Programm taeglich beendet, verdichtet sonst taeglich, und das ist
    /// bei einem Ordner, an dem sich nichts aendert, vergeudete Zeit.
    /// </remarks>
    private static readonly TimeSpan Verdichtungsabstand = TimeSpan.FromDays(7);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        // Beim Beenden, denn dabei wird die Datenbank vollstaendig neu
        // geschrieben und waehrenddessen geht nichts anderes. Es wartet
        // niemand mehr darauf.
        try
        {
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            lock (_indexGate)
                if (_index?.CompactIfDue(Verdichtungsabstand) == true)
                    _log($"[{FolderId}] Index verdichtet in {uhr.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Index verdichten: {ex.Message}");
        }

        _index?.Dispose();
        _index = null;
        _indexArrived.Dispose();
        _hydrationGate.Dispose();
        _localWork.Dispose();
    }
}
