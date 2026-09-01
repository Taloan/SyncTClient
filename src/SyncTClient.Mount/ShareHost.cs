using System.Security.Cryptography;
using System.Collections.Concurrent;
using SyncTClient.Bep;
using SyncTClient.Vfs;
using BepFileInfo = SyncTClient.Bep.Proto.FileInfo;
using BepRequest = SyncTClient.Bep.Proto.Request;
using ErrorCode = SyncTClient.Bep.Proto.ErrorCode;

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

    private BepConnection? _connection;
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
    public long MaxSequence => _index?.MaxSequence ?? 0;
    public ulong PeerIndexId => _index?.PeerIndexId ?? 0;

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

        // Auch hier die Sperre. Der Aufruf kommt aus der Verdraengung, und die
        // laeuft in einem anderen Faden als der Hintergrundlauf.
        lock (_indexGate)
            return _index.TryGet(relativePath, out var file) && HasContent(file) ? 1 : 0;
    }

    /// <summary>
    /// Ob eine Datei nach der Ankuendigung der Gegenstelle wiederbeschaffbar
    /// ist. Das ist die Bedingung dafuer, die lokale Kopie zu verdraengen.
    /// </summary>
    private bool MayEvict(string relativePath)
    {
        var wanted = _app.MinimumCopies;
        return wanted <= 0 || HoldersOf(relativePath) >= wanted;
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
    public int ReachableCopies => _connection is not null && (_index?.Count ?? 0) > 0 ? 1 : 0;

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

    /// <summary>Die Gegenstelle hat ihren Index neu aufgebaut. Der lokale ist damit unbrauchbar.</summary>
    public void ResetIndex(ulong newPeerIndexId)
    {
        _log($"[{FolderId}] die Gegenstelle hat ihren Index neu aufgebaut -- verwerfe den lokalen.");
        _index!.Clear();
        _index.PeerIndexId = newPeerIndexId;
    }

    public void RememberPeerIndexId(ulong id)
    {
        if (_index is not null) _index.PeerIndexId = id;
    }

    /// <summary>Nimmt einen Stapel Index-Eintraege auf, den der PeerHost zugestellt hat.</summary>
    public void Absorb(IEnumerable<BepFileInfo> files)
    {
        // Der Hintergrundlauf schreibt in dieselbe Datenbank. Sie haengt an
        // einer einzigen Verbindung und vertraegt keine zwei Schreiber.
        IReadOnlyList<string> changed;
        lock (_indexGate) changed = _index!.Absorb(files);

        _indexArrived.Release();

        if (Phase == SyncPhase.Index) SetPhase(SyncPhase.Index, _index.Count);

        // Der Index sagt nur, was die Gegenstelle fuehrt. Damit es auch im
        // Ordner steht, muss jeder genannte Name angewendet werden: angelegt,
        // ersetzt oder entfernt. Das geschieht im Hintergrundlauf, nicht hier.
        //
        // Nur was dabei liegen bleibt, ist Redebedarf. Eine Gegenstelle, die
        // ueber ausgeschlossene Namen oder alte Fassungen redet, sagt nichts,
        // was uns fehlt -- und "gleicht ab" waere dann genauso falsch wie
        // vorher "abgeglichen".
        if (QueueIncoming(changed) > 0) PeerBusy();
    }

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

    public async Task StartAsync(BepConnection connection, CancellationToken ct)
    {
        await PrepareAsync(connection, ct);
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
    public async Task PrepareAsync(BepConnection connection, CancellationToken ct)
    {
        _connection = connection;
        State = ShareState.Wartet;

        // Eine neue Sitzung beginnt mit einem Index. Erst danach sind
        // Nachtraege moeglich, und die brauchen die Nummer ihres Vorgaengers.
        _indexSent = false;
        _lastSentSequence = 0;

        try
        {
            await WaitForIndexAsync(ct);
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
            await ProjectAsync();
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

        _cache?.Save();
        _cache?.LeaveLimits();
        _mount?.Dispose();
        _mount = null;
        _connection = null;
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
    /// Nimmt die Leitung weg, ohne den Ordner aufzugeben.
    /// </summary>
    /// <remarks>
    /// Der Unterschied zu <see cref="StopAsync"/>: der Sync-Root bleibt
    /// eingehaengt, der Cache angemeldet und der Hintergrundlauf am Leben.
    /// Lokal wird also weiter indexiert -- das kostet nichts auf der Leitung
    /// und erspart beim Fortsetzen einen vollstaendigen Durchgang.
    /// </remarks>
    public void DropConnection() => _connection = null;

    /// <summary>
    /// Nimmt eine neue Leitung an, ohne den Ordner neu aufzubauen.
    /// </summary>
    /// <remarks>
    /// Nach dem Fortsetzen steht der Ordner noch genauso da wie vorher. Ihn
    /// erneut anzulegen hiesse, Sync-Root und Platzhalter ein zweites Mal
    /// aufzubauen, waehrend die ersten noch stehen.
    ///
    /// Zurueckgesetzt wird nur, was zur Sitzung gehoert: eine neue Leitung
    /// beginnt mit einem vollstaendigen Index, und Nachtraege brauchen die
    /// Nummer ihres Vorgaengers.
    /// </remarks>
    public void Rebind(BepConnection connection)
    {
        _connection = connection;
        _indexSent = false;
        _lastSentSequence = 0;

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

    private async Task ProjectAsync()
    {
        _log($"[{FolderId}] registriere Sync-Root: {_config.LocalPath}");

        // Ueber StorageProviderSyncRootManager statt CfRegisterSyncRoot: nur
        // dieser Weg legt den Registry-Schluessel an, in den die
        // Vorschau-Erweiterung eingetragen wird. Ausserdem erscheint der
        // Ordner mit Namen und Symbol in der Navigationsleiste des Explorers.
        var name = string.IsNullOrWhiteSpace(_config.Label) ? FolderId : _config.Label;
        _syncRootId = await WinRtSyncRoot.RegisterAsync(_config.LocalPath, $"SyncT {name}", "0.1");

        var statePath = Path.Combine(_app.HomeDirectory, $"cache-{FolderId}.json");
        // "Vollstaendig lokal" nimmt am Limit nicht teil. Dort darf nichts
        // verdraengt werden, sonst gilt die Zusage nicht.
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

        // Der Eintrag muss stehen, bevor die Shell den Sync-Root uebernimmt.
        // Sie liest seine Eigenschaften beim Anmelden. Deshalb wird danach
        // noch einmal angemeldet, damit sie den Vorschau-Erzeuger erfasst.
        RegisterThumbnailProvider();
        _syncRootId = await WinRtSyncRoot.RegisterAsync(_config.LocalPath, $"SyncT {name}", "0.1");

        _mount = new CloudFilterMount(_config.LocalPath, this, _log);

        // Die Meldungen der Cloud-Files-Schicht sind der Ausloeser fuer die
        // Erkennung lokaler Aenderungen. Sie melden nur das Ereignis; ob es
        // eine Aenderung war, entscheidet Evaluate anhand des blocks_hash.
        _mount.FileClosed += NoteLocalChange;
        _mount.FileDeleted += NoteLocalDelete;
        _mount.FileRenamed += (before, after) =>
        {
            // Ein Umbenennen ist fuer das Protokoll eine Loeschung und eine
            // neue Datei. Liegt eine Seite ausserhalb der Freigabe, bleibt
            // ihr Pfad leer und der Teil entfaellt.
            if (before.Length > 0) NoteLocalDelete(before);
            if (after.Length > 0) NoteLocalChange(after);
        };

        _mount.Connect();

        SetPhase(SyncPhase.Platzhalter);
        _mount.ProjectPlaceholders((done, total) => SetPhase(SyncPhase.Platzhalter, done, total));

        // Das Anlegen der Platzhalter deckt nur einen Teil ab: es legt an, was
        // fehlt. Eine Datei, die die Gegenstelle inzwischen geloescht oder
        // geaendert hat, bleibt dabei stehen wie sie ist. Deshalb wird der
        // ganze Index einmal durchgesehen.
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
            // einer veroeffentlichten Fassung waeren so gar keine Vorschauen
            // entstanden.
            if (ThumbnailProviderRegistration.FindLibrary() is { } library)
                ThumbnailProviderRegistration.RegisterClass(library);

            if (!ThumbnailProviderRegistration.AttachToSyncRoot(_syncRootId))
                _log($"[{FolderId}] Vorschau-Erweiterung liess sich nicht am Sync-Root eintragen.");

            // Zusaetzlich zur Eintragung in der Registrierung. Solange der
            // Client laeuft, beantwortet er Anfragen selbst.
            ThumbnailService.EnsureStarted(_log);

            lock (Laufende)
                if (!Laufende.Contains(this)) Laufende.Add(this);
        }
        catch (Exception ex)
        {
            _log($"[{FolderId}] Vorschau-Erweiterung: {ex.Message}");
        }
    }

    private async Task ApplyModeAsync(CancellationToken ct)
    {
        if (_config.Mode != ShareMode.AlwaysLocal) return;

        // "Vollstaendig lokal bereithalten" bedeutet, auf jede Datei einmal
        // zuzugreifen. Der erste Lesezugriff loest die Hydration aus.
        var pending = Enumerate()
            .Where(e => !e.IsDirectory && e.Size > 0)
            .Select(e => LocalPathOf(e.RelativePath))
            .Where(p => File.Exists(p) && ((uint)new System.IO.FileInfo(p).Attributes & RecallOnDataAccess) != 0)
            .ToList();

        if (pending.Count == 0) return;
        _log($"[{FolderId}] Modus AlwaysLocal: lade {pending.Count} noch fehlende Dateien herunter ...");

        var done = 0;
        SetPhase(SyncPhase.Inhalte, 0, pending.Count);
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (path, token) =>
            {
                try
                {
                    // Ein einziges Byte genuegt: der Lesezugriff loest die
                    // Hydration der ganzen Datei aus.
                    await using var stream = File.OpenRead(path);
                    var probe = new byte[1];
                    await stream.ReadExactlyAsync(probe, token);
                }
                catch (Exception ex)
                {
                    _log($"  {Path.GetFileName(path)}: {ex.Message}");
                }

                var fertig = Interlocked.Increment(ref done);
                SetPhase(SyncPhase.Inhalte, fertig, pending.Count);
                if (fertig % 50 == 0) _log($"[{FolderId}] {fertig}/{pending.Count} heruntergeladen.");
            });

        _log($"[{FolderId}] vollstaendig lokal.");
    }

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

        foreach (var share in shares)
            if (share.Owns(localFilePath) && share.Produce(localFilePath))
                return true;

        return false;
    }

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
        if (_thumbnails is null || _index is null || _connection is null) return false;
        if (!_app.GenerateThumbnails) return false;
        if (_thumbnails.KnownWithout(localFilePath)) return false;

        return Await(FetchThumbnailAsync(RelativeOf(localFilePath), CancellationToken.None));
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
        if (_thumbnails is null || _index is null || _connection is null) return false;

        // Ein Vorschaubild ist ein Dateikopf und damit Uebertragung.
        if (IsPaused) return false;

        var local = LocalPathOf(relativePath);
        if (_thumbnails.Has(local)) return true;
        if (!_index.TryGet(relativePath, out var file) || file.Size <= 0) return false;

        await _thumbnailGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Waehrend des Wartens kann ein anderer Aufruf fertig geworden sein.
            if (_thumbnails.Has(local)) return true;

            var wanted = Math.Min(ExifThumbnail.RequiredPrefixBytes, file.Size);
            var head = await FileFetcher.FetchRangeAsync(
                _connection, FolderId, file, 0, wanted, _app.Parallelism, ct: ct)
                .ConfigureAwait(false);

            NoteReceived(head.Length);

            var thumbnail = ExifThumbnail.TryExtract(head);
            if (thumbnail is null)
            {
                _thumbnails.MarkWithout(local);
                return false;
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
        // Angehalten wird kein Platz freigegeben. Verdraengen loescht lokalen
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
        if (_connection is null)
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
        await _hydrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            transfer.State = TransferState.Laeuft;

            var blockSize = Math.Max(file.BlockSize, 1);
            var progress = new Progress<int>(blocks =>
                transfer.DoneBytes = already + Math.Min((long)blocks * blockSize, length));

            var data = await FileFetcher.FetchRangeAsync(
                _connection, FolderId, file, offset, length, _app.Parallelism, progress, ct)
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
            // verdraengt worden sein. Das Oeffnen allein holt sie noch nicht,
            // erst das Lesen wuerde es. Deshalb wird hier noch einmal
            // geprueft, solange das guenstig ist. Sonst wird die Datei vom
            // Server heruntergeladen, nur um sie zurueckzugeben.
            if (IsPlaceholder(local))
                return Deny(request, ErrorCode.NoSuchFile, "wurde inzwischen verdraengt");

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
        return (ErrorCode.NoError, data);
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
    /// verwerfen, Index loeschen. Die lokalen Dateien bleiben liegen. Ueber
    /// sie entscheidet der Aufrufer.
    /// </summary>
    public async Task UnbindAsync()
    {
        await StopAsync();

        if (_syncRootId is not null)
        {
            lock (Laufende) Laufende.Remove(this);
            ThumbnailProviderRegistration.DetachFromSyncRoot(_syncRootId);
            try { WinRtSyncRoot.Unregister(_syncRootId); } catch { /* schon weg */ }
            _syncRootId = null;
        }

        _index?.Dispose();
        _index = null;

        var databasePath = Path.Combine(_app.HomeDirectory, $"index-{FolderId}.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(databasePath + suffix); } catch { /* egal */ }

        try { File.Delete(Path.Combine(_app.HomeDirectory, $"cache-{FolderId}.json")); } catch { /* egal */ }

        _log($"[{FolderId}] Bindung geloest.");
    }

    private string LocalPathOf(string relativePath)
        => Path.Combine(_config.LocalPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _index?.Dispose();
        _index = null;
        _indexArrived.Dispose();
        _hydrationGate.Dispose();
        _localWork.Dispose();
    }
}
