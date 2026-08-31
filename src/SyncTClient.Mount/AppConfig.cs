using System.Text.Json;
using System.Text.Json.Serialization;
using SyncTClient.Vfs;

namespace SyncTClient.Mount;

/// <summary>Wie ein Share lokal vorgehalten wird.</summary>
public enum ShareMode
{
    /// <summary>Nur Platzhalter; Inhalte kommen beim Zugriff und unterliegen dem Cache-Budget.</summary>
    OnDemand,

    /// <summary>Alles wird lokal vorgehalten; das Cache-Budget gilt nicht.</summary>
    AlwaysLocal
}

/// <summary>
/// Was geschieht, wenn beide Seiten dieselbe Datei geaendert haben.
/// </summary>
public enum ConflictResolution
{
    /// <summary>
    /// Beide Fassungen bleiben. Die unterlegene wird umbenannt.
    /// </summary>
    /// <remarks>
    /// Der Name folgt dem Muster von Syncthing:
    /// <c>name.sync-conflict-JJJJMMTT-HHMMSS-KURZID.endung</c>. Ein eigenes
    /// Muster wuerde dazu fuehren, dass beide Seiten fuer denselben Konflikt
    /// verschiedene Dateien anlegen.
    /// </remarks>
    KeepBoth,

    /// <summary>Die zuletzt geaenderte Fassung gewinnt.</summary>
    Newer,

    /// <summary>Die aeltere Fassung gewinnt.</summary>
    Older,

    /// <summary>Die hiesige Fassung gewinnt.</summary>
    Local,

    /// <summary>Die Fassung der Gegenstelle gewinnt.</summary>
    Remote
}

/// <summary>Eine Gegenstelle. Das ist ein Server oder ein anderer Rechner.</summary>
public sealed class PeerConfig
{
    /// <summary>Anzeigename. Ist er leer, wird der Name der Gegenstelle verwendet.</summary>
    public string Name { get; set; } = "";

    /// <summary>Adresse, etwa "192.168.1.42:22000".</summary>
    public string Address { get; set; } = "";

    /// <summary>Erwartete Device-ID der Gegenstelle.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Beim Start der Oberflaeche automatisch verbinden.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>
    /// Diese Gegenstelle beim Erkennungsserver suchen, wenn keine Adresse
    /// eingetragen ist.
    /// </summary>
    public bool Discovery { get; set; } = true;

    /// <summary>
    /// Diese Gegenstelle auch ueber einen Relay ansprechen.
    /// </summary>
    /// <remarks>
    /// Ein Relay leitet fremden Verkehr weiter. Er ist der Weg zu einem Geraet,
    /// das keinen Port nach aussen offen hat, und langsamer als jede direkte
    /// Verbindung. Wer beides nicht will, schaltet ihn hier ab. Dann kommt eine
    /// Verbindung nur zustande, wenn ein direkter Weg besteht.
    /// </remarks>
    public bool Relays { get; set; } = true;

    [JsonIgnore]
    public string ShortId => DeviceId.Length >= 7 ? DeviceId[..7] : DeviceId;

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? ShortId : Name;
}

public sealed class ShareConfig
{
    /// <summary>Die Folder-ID aus Syncthing.</summary>
    public string FolderId { get; set; } = "";

    /// <summary>Von welcher Gegenstelle dieser Ordner kommt.</summary>
    public string PeerDeviceId { get; set; } = "";

    /// <summary>Anzeigename. Die Gegenstelle nennt meist einen.</summary>
    public string Label { get; set; } = "";

    /// <summary>Wo der Ordner im Explorer erscheint.</summary>
    public string LocalPath { get; set; } = "";

    public ShareMode Mode { get; set; } = ShareMode.OnDemand;

    /// <summary>
    /// Was geschieht, wenn beide Seiten dieselbe Datei geaendert haben.
    /// </summary>
    /// <remarks>
    /// Ausser bei <see cref="ConflictResolution.KeepBoth"/> wird die
    /// unterlegene Fassung geloescht. Wirksam wird die Einstellung mit der
    /// Konfliktbehandlung; bis dahin gibt es keine lokalen Aenderungen, die
    /// in einen Konflikt geraten koennten.
    /// </remarks>
    public ConflictResolution Conflict { get; set; } = ConflictResolution.KeepBoth;

    /// <summary>
    /// Ersetzte und geloeschte Fassungen werden aufgehoben, statt sofort
    /// fortzufallen.
    /// </summary>
    /// <remarks>
    /// Sie liegen im Ordner ".stversions" innerhalb der Freigabe. Dieser
    /// Ordner nimmt am Abgleich nicht teil: er wird nicht angekuendigt, nicht
    /// in den Index aufgenommen und beim Freigeben von Speicherplatz nicht
    /// angefasst.
    /// </remarks>
    public bool KeepVersions { get; set; } = true;

    /// <summary>Wie lange eine aufgehobene Fassung liegen bleibt, in Tagen.</summary>
    public int VersionDays { get; set; } = 7;

    /// <summary>
    /// Wie viele andere Knoten eine Datei vollstaendig fuehren muessen, bevor
    /// wir unsere Kopie verdraengen duerfen.
    /// </summary>
    /// <remarks>
    /// Beim Verdraengen werden die lokalen Bytes geloescht. Die Datei liegt
    /// danach nur noch auf den anderen Knoten und muss bei Bedarf von dort
    /// geholt werden. Bei einer einzigen Gegenstelle, dem Normalfall gegen
    /// einen Server, ist 1 die sinnvolle Vorgabe. Wer mehrere Knoten hat und
    /// seine Dateien nicht von einem einzigen Geraet abhaengig machen will,
    /// setzt 2.
    ///
    /// 0 schaltet die Pruefung ab und verdraengt wie frueher.
    /// </remarks>
    public int MinimumCopies { get; set; } = 1;

    /// <summary>
    /// Teilbaum-Auswahl: nur diese Pfade werden ueberhaupt projiziert. Leer
    /// bedeutet alles. Pfade relativ zum Share, mit / als Trenner.
    /// </summary>
    /// <remarks>
    /// Das ist die Datenseite des aufklappbaren Baums in der Oberflaeche.
    /// Ausgeschlossene Verzeichnisse bekommen keinen Platzhalter. Sie
    /// erscheinen nicht im Explorer und belegen keinen Index-Speicher.
    /// </remarks>
    public List<string> Included { get; set; } = [];

    /// <summary>
    /// Vorschaubilder im Hintergrund vorbereiten. Das kostet je Bild einen
    /// Block von 128 KiB. Ohne Vorschaubilder zeigt der Explorer ein
    /// Ersatzsymbol.
    /// </summary>
    public bool GenerateThumbnails { get; set; } = true;

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Label) ? FolderId : $"{Label} ({FolderId})";

    public bool Includes(string relativePath)
    {
        if (Included.Count == 0) return true;

        foreach (var prefix in Included)
        {
            if (relativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            if (relativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return true;
            // Elternverzeichnisse einer Auswahl muessen sichtbar bleiben.
            // Sonst ist der ausgewaehlte Zweig im Explorer nicht erreichbar.
            if (prefix.StartsWith(relativePath + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

/// <summary>Die Grenzen eines Datentraegers, wie sie in der Datei stehen.</summary>
/// <remarks>
/// Es gibt hier bewusst keinen Eintrag fuer jedes Laufwerk des Rechners,
/// sondern nur fuer die, an denen jemand etwas geaendert hat. Ein Laufwerk
/// kann verschwinden -- ein Wechseldatentraeger, ein getrenntes Netzlaufwerk
/// --, und ein Eintrag dafuer soll niemanden stoeren, wenn es wiederkommt.
/// </remarks>
public sealed class VolumeLimitConfig
{
    /// <summary>Die Laufwerkswurzel, etwa <c>C:\</c>.</summary>
    public string Root { get; set; } = "";

    /// <summary>Hoechstens so viel darf hier belegt sein. 0 = kein Limit.</summary>
    public long MaxBytes { get; set; }

    /// <summary>So viel soll hier frei bleiben. 0 = unbeachtet.</summary>
    public long MinimumFreeBytes { get; set; }
}

public sealed class AppConfig
{
    /// <summary>
    /// Wie sich dieses Geraet den Gegenstellen nennt.
    /// </summary>
    /// <remarks>
    /// Der Name steht im Hello und ist das erste, was eine Gegenstelle von
    /// uns sieht -- in Syncthings Oberflaeche steht er neben der Kennung.
    /// Vorgabe ist der Rechnername, denn den kennt der Benutzer bereits, und
    /// eine erfundene Bezeichnung waere auf der Gegenseite nur ein Raetsel.
    /// </remarks>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Bei wie vielen anderen Knoten eine Datei vollstaendig vorliegen muss,
    /// bevor sie hier zum Platzhalter werden darf.
    /// </summary>
    /// <remarks>
    /// Programmweit und nicht je Freigabe: es ist eine Sicherheitsvorgabe,
    /// keine Eigenart eines einzelnen Ordners. Wer sie fuer eine Freigabe
    /// lockern koennte, wuerde sie irgendwann fuer die falsche lockern.
    ///
    /// Mindestens 1. Bei 0 gaebe es keine Zusicherung mehr, dass eine
    /// freigegebene Datei ueberhaupt noch irgendwo liegt.
    /// </remarks>
    public int MinimumCopies { get; set; } = 1;

    /// <summary>Vorschaubilder aus den Dateikoepfen gewinnen.</summary>
    /// <remarks>
    /// Ebenfalls programmweit. Die Erweiterung, die sie an die Shell
    /// liefert, wird einmal angemeldet und gilt fuer alle Freigaben; sie je
    /// Freigabe an- und abzuschalten haette also gar keine Entsprechung.
    /// </remarks>
    public bool GenerateThumbnails { get; set; } = true;

    /// <summary>
    /// Der Abgleich ist angehalten.
    /// </summary>
    /// <remarks>
    /// Ein Modus und keine einmalige Handlung: er gilt auch fuer Freigaben,
    /// die erst spaeter bereit werden, und er ueberlebt einen Neustart. Wer
    /// anhaelt, weil die Leitung gebraucht wird, will nicht, dass das naechste
    /// Hochfahren die Entscheidung stillschweigend zuruecknimmt.
    ///
    /// Sichtbar bleibt er trotzdem: das Zeichen im Infobereich steht dann auf
    /// orange, und die Schaltflaeche heisst "Fortsetzen".
    /// </remarks>
    public bool Paused { get; set; }

    /// <summary>Verzeichnis fuer Geraetezertifikat, Index-Datenbanken und Cache-Zustand.</summary>
    public string HomeDirectory { get; set; } = "synct-home";

    /// <summary>
    /// Wo neu uebernommene Ordner im Explorer erscheinen. Ist der Wert leer,
    /// wird "SyncT" im Benutzerprofil verwendet.
    /// </summary>
    public string SharesRoot { get; set; } = "";

    /// <summary>
    /// Die Vorgabe fuer Datentraeger, fuer die noch nichts eingestellt wurde.
    /// </summary>
    /// <remarks>
    /// Der Cache hat kein eigenes Verzeichnis. Zwischengespeichert ist eine
    /// Datei, die an ihrem Platz unter <see cref="SharesRoot"/> liegt und ihre
    /// Bytes lokal vorhaelt. Begrenzt wird deshalb die Summe dieser Dateien,
    /// nicht ein Verzeichnis.
    ///
    /// 0 wuerde "unbegrenzt" bedeuten: es wird nichts verdraengt, und jede
    /// einmal geoeffnete Datei bleibt lokal liegen. Nach einigen Monaten waere
    /// der Bestand eine Vollkopie, und es wuerde nichts mehr bei Bedarf
    /// geholt. Die Oberflaeche laesst darum nur ganze Gigabyte ab 1 zu.
    /// </remarks>
    public long CacheMaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Die Vorgabe dafuer, wie viel auf einem Datentraeger frei bleiben soll.
    /// 0 schaltet diese Grenze ab.
    /// </summary>
    /// <remarks>
    /// Die zweite Grenze neben <see cref="CacheMaxBytes"/>. Es greift die
    /// Grenze, die zuerst erreicht wird.
    /// </remarks>
    public long MinimumFreeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Was auf einzelnen Datentraegern abweichend gilt.</summary>
    public List<VolumeLimitConfig> VolumeLimits { get; set; } = [];

    /// <summary>Die Grenzen einer Laufwerkswurzel, sonst die Vorgabe.</summary>
    public VolumeLimits LimitsFor(string root)
    {
        var eigen = VolumeLimits.FirstOrDefault(
            v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

        return eigen is null
            ? new VolumeLimits(CacheMaxBytes, MinimumFreeBytes)
            : new VolumeLimits(eigen.MaxBytes, eigen.MinimumFreeBytes);
    }

    /// <summary>Legt die Grenzen eines Datentraegers fest.</summary>
    public void SetLimits(string root, long maxBytes, long minimumFreeBytes)
    {
        var eigen = VolumeLimits.FirstOrDefault(
            v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

        if (eigen is null)
            VolumeLimits.Add(eigen = new VolumeLimitConfig { Root = root });

        eigen.MaxBytes = maxBytes;
        eigen.MinimumFreeBytes = minimumFreeBytes;
    }

    public List<PeerConfig> Peers { get; set; } = [];

    public List<ShareConfig> Shares { get; set; } = [];

    /// <summary>
    /// Farbschema der Oberflaeche: "Hell", "Dunkel" oder leer fuer die
    /// Einstellung von Windows.
    /// </summary>
    public string Theme { get; set; } = "";

    /// <summary>
    /// Sprache der Oberflaeche: "de", "en" oder leer fuer die Sprache des
    /// Systems.
    /// </summary>
    public string Language { get; set; } = "";

    /// <summary>
    /// Beim Start kein Fenster aufziehen.
    /// </summary>
    /// <remarks>
    /// Zusammen mit <see cref="CloseToTray"/> ergibt das: kein Fenster, nur
    /// das Symbol im Infobereich. Ohne dieses Symbol bleibt es beim
    /// minimierten Fenster in der Taskleiste, sonst liefe das Programm ohne
    /// jede Bedienmoeglichkeit.
    ///
    /// Ob Windows das Programm ueberhaupt startet, steht nicht hier. Dieser
    /// Eintrag liegt in der Registry, wo Windows ihn liest.
    /// </remarks>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Das X versteckt das Fenster, statt das Programm zu beenden.
    /// </summary>
    /// <remarks>
    /// Die Oberflaeche ist zugleich der Sync-Dienst. Wer sie schliesst, meint
    /// meist das Fenster und nicht den Abgleich. Beendet wird das Programm
    /// dann ueber das Kontextmenue des Symbols im Infobereich.
    /// </remarks>
    public bool CloseToTray { get; set; }

    /// <summary>Parallele Block-Requests je Hydration.</summary>
    public int Parallelism { get; set; } = 8;

    /// <summary>
    /// Anrufe anderer Geraete annehmen.
    /// </summary>
    /// <remarks>
    /// Ohne eingehende Verbindungen kann der Aufbau nur von diesem Geraet
    /// ausgehen. Eine Gegenstelle, deren Adresse wir nicht kennen, bleibt
    /// dann unerreichbar.
    /// </remarks>
    public bool Listen { get; set; } = true;

    /// <summary>Port fuer eingehende Verbindungen. Syncthings Vorgabe ist 22000.</summary>
    public int ListenPort { get; set; } = 22000;

    /// <summary>
    /// Gegenstellen ohne feste Adresse beim Erkennungsserver suchen.
    /// </summary>

    /// <summary>
    /// Im eigenen Netz Ankuendigungen senden und empfangen.
    /// </summary>
    /// <remarks>
    /// Beides gehoert zusammen. Wer nur empfaengt, findet andere Geraete,
    /// wird aber selbst nicht gefunden.
    /// </remarks>
    public bool LocalDiscovery { get; set; } = true;

    /// <summary>
    /// Dem Erkennungsserver melden, wo dieses Geraet erreichbar ist.
    /// </summary>
    /// <remarks>
    /// Erst dadurch kann eine Gegenstelle, bei der als Adresse "dynamic"
    /// steht, eine Verbindung zu uns aufbauen. Der Server erfaehrt dabei
    /// unsere IP-Adresse.
    /// </remarks>
    public bool Announce { get; set; } = true;

    /// <summary>
    /// Die im eigenen Netz gefundenen Geraete. Kein Teil der Konfiguration.
    /// Die Oberflaeche traegt das Objekt hier ein, damit die Gegenstellen
    /// darauf zugreifen koennen.
    /// </summary>
    [JsonIgnore]
    public SyncTClient.Bep.LocalDiscovery? Local { get; set; }
    /// <remarks>
    /// Der Server erfaehrt dabei, nach welchem Geraet gefragt wird, und
    /// ausserdem unsere IP-Adresse. Wer das nicht will, traegt Adressen von
    /// Hand ein und schaltet diese Option ab.
    /// </remarks>
    public bool Discovery { get; set; } = true;

    /// <summary>
    /// Relays ueberhaupt verwenden.
    /// </summary>
    /// <remarks>
    /// Wirkt mit <see cref="PeerConfig.Relays"/> als Und-Verknuepfung. Hier
    /// steht der Hauptschalter, dort die Entscheidung je Gegenstelle.
    /// </remarks>
    public bool Relays { get; set; } = true;

    /// <summary>
    /// Der Erkennungsserver. Das <c>id=</c> darin ist seine Geraete-ID. Daran
    /// wird er erkannt, denn sein Zertifikat ist selbstsigniert.
    /// </summary>
    public List<string> DiscoveryServers { get; set; } =
    [
        "https://discovery.syncthing.net/v2/?noannounce&id=" + SyncthingDiscoveryId,
        "https://discovery-v4.syncthing.net/v2/?nolookup&id=" + SyncthingDiscoveryId,
        "https://discovery-v6.syncthing.net/v2/?nolookup&id=" + SyncthingDiscoveryId
    ];

    /// <summary>Die Geraete-ID der Erkennungsserver von Syncthing.</summary>
    private const string SyncthingDiscoveryId =
        "LYXKCHX-VI3NYZR-ALCJBHF-WMZYSPK-QG6QJA3-MPFYMSO-U56GTUK-NA2MIAW";

    /// <summary>
    /// Aeltere Fassungen kannten genau einen Server. Beim Laden wird er
    /// uebernommen, sofern er nicht ohnehin der alte Vorgabewert war.
    /// </summary>
    public string? DiscoveryServer { get; set; }

    /// <summary>Die Server, bei denen sich Adressen abfragen lassen.</summary>
    [JsonIgnore]
    public IEnumerable<string> LookupServers => DiscoveryServers.Where(SyncTClient.Bep.GlobalDiscovery.AllowsLookup);

    /// <summary>Die Server, bei denen sich dieses Geraet anmelden kann.</summary>
    [JsonIgnore]
    public IEnumerable<string> AnnounceServers => DiscoveryServers.Where(SyncTClient.Bep.GlobalDiscovery.AllowsAnnounce);

    /// <summary>
    /// Aeltere Fassungen kannten genau eine Gegenstelle. Beim Laden wird sie in
    /// die Liste ueberfuehrt, damit bestehende Konfigurationen weiterlaufen.
    /// </summary>
    public PeerConfig? Peer { get; set; }

    private CacheBudget? _budget;

    /// <summary>Die Ueberwachung des belegten Platzes.</summary>
    /// <remarks>
    /// Das Budget bekommt die Grenzen nicht mitgegeben, sondern fragt sie hier
    /// nach. So wirkt eine Aenderung in den Einstellungen sofort und nicht
    /// erst beim naechsten Start.
    /// </remarks>
    [JsonIgnore]
    public CacheBudget Cache => _budget ??= new CacheBudget(LimitsFor);

    [JsonIgnore]
    public string SharesRootOrDefault => string.IsNullOrWhiteSpace(SharesRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT")
        : SharesRoot;

    /// <summary>
    /// Wo die Vorschaubilder liegen. Sie liegen im Cache-Verzeichnis, aber
    /// neben den Freigaben statt darin.
    /// </summary>
    /// <remarks>
    /// Sie gehoeren zum Cache. Sie entstehen aus fremden Dateien und lassen
    /// sich jederzeit neu erzeugen. Innerhalb einer Freigabe waeren sie ein
    /// Sync-Root im Sync-Root, und Windows wuerde versuchen, sie zu
    /// projizieren.
    /// </remarks>
    [JsonIgnore]
    public string ThumbnailDirectory => Path.Combine(SharesRootOrDefault, ".preview");

    public PeerConfig? PeerFor(ShareConfig share)
        => Peers.FirstOrDefault(p => p.DeviceId == share.PeerDeviceId) ?? Peers.FirstOrDefault();

    public IEnumerable<ShareConfig> SharesOf(PeerConfig peer)
        => Shares.Where(s => s.PeerDeviceId == peer.DeviceId);

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Format)
                     ?? throw new InvalidDataException($"\"{path}\" enthaelt keine gueltige Konfiguration.");
        config.Migrate();
        return config;
    }

    /// <summary>
    /// Macht aus einem relativen Datenverzeichnis einen vollstaendigen Pfad.
    /// Bezugspunkt ist die Konfigurationsdatei.
    /// </summary>
    /// <remarks>
    /// Ein relativer Pfad in einer Datei ist relativ zu dieser Datei. Wird er
    /// gegen das Arbeitsverzeichnis gerechnet, findet dieselbe Konfiguration
    /// je nach Startverzeichnis ein anderes Datenverzeichnis und damit einen
    /// anderen Index und ein anderes Geraetezertifikat.
    ///
    /// Die Oberflaeche loest den Pfad fuer ihre Laufzeitfassung selbst auf und
    /// laesst den Eintrag in der Datei relativ, weil sie die Datei
    /// zurueckschreibt. Die Konsole schreibt nicht zurueck und darf den
    /// Eintrag deshalb hier ersetzen.
    /// </remarks>
    /// <summary>
    /// Wo die Konfiguration liegt, wenn niemand etwas anderes sagt.
    /// </summary>
    /// <remarks>
    /// Zwei Faelle, in dieser Reihenfolge:
    ///
    /// Liegt eine <c>synct.json</c> beim Programm oder darueber, gilt sie.
    /// Das ist der tragbare Fall -- Entwicklungsbaum, USB-Stick, ein Ordner,
    /// den man mitnimmt.
    ///
    /// Sonst <c>%LOCALAPPDATA%\SyncTClient</c>. Unter <c>C:\Program Files</c>
    /// darf ein gewoehnlicher Benutzer nicht schreiben; eine installierte
    /// Fassung koennte dort weder Zertifikat noch Index anlegen.
    ///
    /// Bewusst <em>Local</em> und nicht das wandernde <c>%APPDATA%</c>: das
    /// Geraetezertifikat ist die Kennung genau dieses Rechners. Wanderte es
    /// mit, traeten zwei Rechner im Verbund unter derselben Kennung auf. Und
    /// Index und Vorschaubilder gehen in die hunderte Megabyte -- die will
    /// niemand bei jeder Anmeldung ueber das Netz schieben.
    /// </remarks>
    public static string DefaultConfigPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "synct.json");
            if (File.Exists(candidate)) return candidate;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncTClient", "synct.json");
    }

    public void ResolveAgainst(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (string.IsNullOrEmpty(directory)) return;

        HomeDirectory = Path.GetFullPath(Path.Combine(directory, HomeDirectory));
    }

    /// <summary>Hebt eine Konfiguration der alten Form auf die neue.</summary>
    private void Migrate()
    {
        if (Peer is not null && Peers.Count == 0)
        {
            Peers.Add(Peer);
            Peer = null;
        }

        if (DiscoveryServer is { Length: > 0 } single)
        {
            // Der alte Vorgabewert erlaubte nur Abfragen, keine Anmeldung. Er
            // ist in der neuen Liste bereits enthalten.
            if (!DiscoveryServers.Contains(single) && !single.Contains("noannounce"))
                DiscoveryServers.Insert(0, single);

            DiscoveryServer = null;
        }

        // 0 hiess: ohne Pruefung freigeben. Damit konnte der letzte Inhalt
        // einer Datei im Netz verschwinden. Der Wert wird angehoben.
        foreach (var share in Shares.Where(s => s.MinimumCopies < 1))
            share.MinimumCopies = 1;

        var fallback = Peers.FirstOrDefault()?.DeviceId ?? "";
        foreach (var share in Shares)
            if (string.IsNullOrEmpty(share.PeerDeviceId))
                share.PeerDeviceId = fallback;
    }

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, Format));

    /// <summary>Eine Vorlage zum Ausfuellen.</summary>
    public static AppConfig Template(string address, string deviceId, string folderId)
    {
        var config = new AppConfig { Peers = [new PeerConfig { Address = address, DeviceId = deviceId }] };
        config.Shares.Add(new ShareConfig
        {
            FolderId = folderId,
            PeerDeviceId = deviceId,
            LocalPath = Path.Combine(config.SharesRootOrDefault, folderId),
            Mode = ShareMode.OnDemand
        });
        return config;
    }
}
