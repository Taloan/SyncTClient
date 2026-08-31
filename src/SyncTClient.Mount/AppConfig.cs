using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// Budget des lokalen Caches in Bytes. Nur bei <see cref="ShareMode.OnDemand"/>
    /// wirksam; 0 bedeutet unbegrenzt.
    /// </summary>
    public long CacheMaxBytes { get; set; }

    /// <summary>
    /// Beim Start der Oberflaeche automatisch hochfahren. Sie ist der
    /// Sync-Dienst -- wer sie oeffnet, will in aller Regel, dass es laeuft.
    /// </summary>
    public bool AutoStart { get; set; } = true;

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

    public List<PeerConfig> Peers { get; set; } = [];

    public List<ShareConfig> Shares { get; set; } = [];

    /// <summary>Parallele Block-Requests je Hydration.</summary>
    public int Parallelism { get; set; } = 8;

    /// <summary>
    /// Aeltere Fassungen kannten genau eine Gegenstelle. Beim Laden wird sie in
    /// die Liste ueberfuehrt, damit bestehende Konfigurationen weiterlaufen.
    /// </summary>
    public PeerConfig? Peer { get; set; }

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

        var fallback = Peers.FirstOrDefault()?.DeviceId ?? "";
        foreach (var share in Shares)
            if (string.IsNullOrEmpty(share.PeerDeviceId))
                share.PeerDeviceId = fallback;
    }

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, Format));

    /// <summary>Eine Vorlage zum Ausfuellen.</summary>
    public static AppConfig Template(string address, string deviceId, string folderId) => new()
    {
        Peers = [new PeerConfig { Address = address, DeviceId = deviceId }],
        Shares =
        [
            new ShareConfig
            {
                FolderId = folderId,
                PeerDeviceId = deviceId,
                LocalPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT", folderId),
                Mode = ShareMode.OnDemand,
                CacheMaxBytes = 2L * 1024 * 1024 * 1024
            }
        ]
    };
}
