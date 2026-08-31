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

/// <summary>Eine Gegenstelle -- ein Server oder ein anderer Rechner.</summary>
public sealed class PeerConfig
{
    /// <summary>Anzeigename. Leer heisst: den nehmen, den die Gegenstelle nennt.</summary>
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
    /// Ein Relay traegt fremde Bandbreite -- er ist der Weg zu einem Geraet,
    /// das keinen Port nach aussen offen hat, und langsamer als jeder direkte.
    /// Wer beides nicht will, schaltet ihn hier ab und bleibt darauf
    /// angewiesen, dass ein direkter Weg besteht.
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

    /// <summary>Anzeigename; die Gegenstelle nennt meist einen.</summary>
    public string Label { get; set; } = "";

    /// <summary>Wo der Ordner im Explorer erscheint.</summary>
    public string LocalPath { get; set; } = "";

    public ShareMode Mode { get; set; } = ShareMode.OnDemand;

    /// <summary>
    /// Wie viele andere Knoten eine Datei vollstaendig fuehren muessen, bevor
    /// wir unsere Kopie verdraengen duerfen.
    /// </summary>
    /// <remarks>
    /// Verdraengen ist eine einseitige Wette: die Bytes sind weg, und ab dann
    /// haengt die Datei an einem anderen. Mit einer einzigen Gegenstelle --
    /// dem Normalfall gegen einen Server -- ist 1 die einzig sinnvolle
    /// Vorgabe. Wer mehrere Knoten hat und seine Fotos nicht an einem
    /// einzigen Notebook haengen lassen will, setzt 2.
    ///
    /// 0 schaltet die Pruefung ab und verdraengt wie frueher.
    /// </remarks>
    public int MinimumCopies { get; set; } = 1;

    /// <summary>
    /// Teilbaum-Auswahl: nur diese Pfade werden ueberhaupt projiziert. Leer
    /// bedeutet alles. Pfade relativ zum Share, mit / als Trenner.
    /// </summary>
    /// <remarks>
    /// Das ist die Datenseite dessen, was in der Oberflaeche der aufklappbare
    /// Baum ist. Ausgeschlossene Verzeichnisse bekommen nicht einmal einen
    /// Platzhalter -- sie tauchen im Explorer gar nicht auf und kosten auch
    /// keinen Index-Speicher.
    /// </remarks>
    public List<string> Included { get; set; } = [];

    /// <summary>
    /// Vorschaubilder im Hintergrund vorbereiten. Kostet je Bild einen Block
    /// von 128 KiB -- ohne sie zeigt der Explorer nur ein Ersatzsymbol.
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
            // Elternverzeichnisse einer Auswahl muessen sichtbar bleiben,
            // sonst haengt der ausgewaehlte Zweig in der Luft.
            if (prefix.StartsWith(relativePath + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

public sealed class AppConfig
{
    /// <summary>Verzeichnis fuer Geraetezertifikat, Index-Datenbanken und Cache-Zustand.</summary>
    public string HomeDirectory { get; set; } = "synct-home";

    /// <summary>
    /// Wo neu uebernommene Ordner im Explorer erscheinen. Leer heisst:
    /// "SyncT" im Benutzerprofil.
    /// </summary>
    public string SharesRoot { get; set; } = "";

    /// <summary>
    /// Budget des lokalen Caches in Bytes -- fuer alle Freigaben zusammen.
    /// </summary>
    /// <remarks>
    /// Der Cache hat kein eigenes Verzeichnis: zwischengespeichert ist eine
    /// Datei, die an ihrem Platz unter <see cref="SharesRoot"/> liegt und ihre
    /// Bytes lokal hat. Zu begrenzen ist deshalb die Summe, nicht ein Ort.
    ///
    /// 0 haette "unbegrenzt" bedeutet: nichts wird je verdraengt, und was
    /// einmal geoeffnet wurde, bleibt liegen. Nach ein paar Monaten waere das
    /// eine Vollkopie und "bei Bedarf herunterladen" nur noch ein Wort.
    /// Die Oberflaeche laesst darum nur ganze Gigabyte ab 1 zu.
    /// </remarks>
    public long CacheMaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// So viel Platz soll auf dem Laufwerk des Caches frei bleiben. 0 heisst:
    /// keine Ruecksicht.
    /// </summary>
    /// <remarks>
    /// Die zweite Grenze neben <see cref="CacheMaxBytes"/>. Es gilt, was
    /// zuerst greift.
    /// </remarks>
    public long MinimumFreeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public List<PeerConfig> Peers { get; set; } = [];

    public List<ShareConfig> Shares { get; set; } = [];

    /// <summary>
    /// Farbschema der Oberflaeche: "Hell", "Dunkel" oder leer fuer das, was
    /// Windows eingestellt hat.
    /// </summary>
    public string Theme { get; set; } = "";

    /// <summary>
    /// Sprache der Oberflaeche: "de", "en" oder leer fuer die des Systems.
    /// </summary>
    public string Language { get; set; } = "";

    /// <summary>
    /// Beim Start kein Fenster aufziehen.
    /// </summary>
    /// <remarks>
    /// Zusammen mit <see cref="CloseToTray"/> heisst das: gar kein Fenster,
    /// nur das Symbol im Infobereich. Ohne das Symbol bleibt es beim
    /// minimierten Fenster in der Taskleiste -- sonst waere das Programm da
    /// und niemand kaeme mehr daran.
    ///
    /// Ob Windows das Programm ueberhaupt startet, steht nicht hier: dieser
    /// Eintrag liegt in der Registry, wo Windows ihn liest.
    /// </remarks>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Das X versteckt das Fenster, statt das Programm zu beenden.
    /// </summary>
    /// <remarks>
    /// Die Oberflaeche ist der Sync-Dienst -- wer sie schliesst, meint meist
    /// das Fenster und nicht den Abgleich. Beendet wird dann ueber das
    /// Kontextmenue des Symbols im Infobereich.
    /// </remarks>
    public bool CloseToTray { get; set; }

    /// <summary>Parallele Block-Requests je Hydration.</summary>
    public int Parallelism { get; set; } = 8;

    /// <summary>
    /// Anrufe anderer Geraete annehmen.
    /// </summary>
    /// <remarks>
    /// Wer nur selbst anruft, erfaehrt nie, dass ihn jemand kennenlernen
    /// moechte -- und ist fuer eine Gegenstelle unerreichbar, deren Adresse
    /// wir nicht kennen.
    /// </remarks>
    public bool Listen { get; set; } = true;

    /// <summary>Port fuer eingehende Verbindungen. Syncthings Vorgabe ist 22000.</summary>
    public int ListenPort { get; set; } = 22000;

    /// <summary>
    /// Gegenstellen ohne feste Adresse beim Erkennungsserver suchen.
    /// </summary>

    /// <summary>
    /// Im eigenen Netz rufen und zuhoeren.
    /// </summary>
    /// <remarks>
    /// Beides gehoert zusammen: wer nur zuhoert, findet andere -- gefunden
    /// wird er nicht.
    /// </remarks>
    public bool LocalDiscovery { get; set; } = true;

    /// <summary>
    /// Dem Erkennungsserver melden, wo dieses Geraet erreichbar ist.
    /// </summary>
    /// <remarks>
    /// Erst dadurch kann eine Gegenstelle, bei der als Adresse "dynamic"
    /// steht, uns anrufen. Der Server erfaehrt dabei unsere IP-Adresse.
    /// </remarks>
    public bool Announce { get; set; } = true;

    /// <summary>
    /// Was das eigene Netz gerade hergibt. Kein Teil der Konfiguration --
    /// die Oberflaeche haengt es hier ein, damit die Gegenstellen es sehen.
    /// </summary>
    [JsonIgnore]
    public SyncTClient.Bep.LocalDiscovery? Local { get; set; }
    /// <remarks>
    /// Der Server erfaehrt dabei, nach welchem Geraet gefragt wird, und
    /// nebenbei unsere IP-Adresse. Wer das nicht will, traegt Adressen von
    /// Hand ein und schaltet dies ab.
    /// </remarks>
    public bool Discovery { get; set; } = true;

    /// <summary>
    /// Relays ueberhaupt verwenden.
    /// </summary>
    /// <remarks>
    /// Zusammen mit <see cref="PeerConfig.Relays"/> ein Und: hier der
    /// Hauptschalter, dort die Entscheidung je Gegenstelle.
    /// </remarks>
    public bool Relays { get; set; } = true;

    /// <summary>
    /// Der Erkennungsserver. Das <c>id=</c> darin ist seine Geraete-ID --
    /// daran wird er erkannt, denn sein Zertifikat ist selbstsigniert.
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

    /// <summary>Die Server, die Fragen beantworten.</summary>
    [JsonIgnore]
    public IEnumerable<string> LookupServers => DiscoveryServers.Where(SyncTClient.Bep.GlobalDiscovery.AllowsLookup);

    /// <summary>Die Server, die Anmeldungen entgegennehmen.</summary>
    [JsonIgnore]
    public IEnumerable<string> AnnounceServers => DiscoveryServers.Where(SyncTClient.Bep.GlobalDiscovery.AllowsAnnounce);

    /// <summary>
    /// Aeltere Fassungen kannten genau eine Gegenstelle. Beim Laden wird sie in
    /// die Liste ueberfuehrt, damit bestehende Konfigurationen weiterlaufen.
    /// </summary>
    public PeerConfig? Peer { get; set; }

    private CacheBudget? _budget;

    /// <summary>Das Budget, das sich alle Freigaben teilen.</summary>
    [JsonIgnore]
    public CacheBudget Cache => _budget ??=
        new CacheBudget(CacheMaxBytes, MinimumFreeBytes, SharesRootOrDefault);

    [JsonIgnore]
    public string SharesRootOrDefault => string.IsNullOrWhiteSpace(SharesRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT")
        : SharesRoot;

    /// <summary>
    /// Wo die Vorschaubilder liegen: im Cache-Verzeichnis, aber neben den
    /// Freigaben statt darin.
    /// </summary>
    /// <remarks>
    /// Sie gehoeren zum Cache -- sie entstehen aus fremden Dateien und
    /// entstehen jederzeit neu. Innerhalb einer Freigabe waeren sie ein
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
            // Der alte Vorgabewert konnte nur fragen, nicht anmelden -- er ist
            // in der neuen Liste ohnehin enthalten.
            if (!DiscoveryServers.Contains(single) && !single.Contains("noannounce"))
                DiscoveryServers.Insert(0, single);

            DiscoveryServer = null;
        }

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
