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

public sealed class ShareConfig
{
    /// <summary>Die Folder-ID aus Syncthing.</summary>
    public string FolderId { get; set; } = "";

    /// <summary>Wo der Ordner im Explorer erscheint.</summary>
    public string LocalPath { get; set; } = "";

    public ShareMode Mode { get; set; } = ShareMode.OnDemand;

    /// <summary>
    /// Teilbaum-Auswahl: nur diese Pfade werden ueberhaupt projiziert. Leer
    /// bedeutet alles. Pfade relativ zum Share, mit / als Trenner.
    /// </summary>
    /// <remarks>
    /// Das ist die Datenseite dessen, was in der Oberflaeche der aufklappbare
    /// Baum wird. Ausgeschlossene Verzeichnisse bekommen nicht einmal einen
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
    /// Vorschaubilder im Hintergrund vorbereiten. Kostet je Bild einen Block
    /// von 128 KiB -- ohne sie zeigt der Explorer nur ein Ersatzsymbol.
    /// </summary>
    public bool GenerateThumbnails { get; set; } = true;

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

public sealed class PeerConfig
{
    /// <summary>Adresse des Peers, etwa "192.168.1.42:22000".</summary>
    public string Address { get; set; } = "";

    /// <summary>Erwartete Device-ID des Peers.</summary>
    public string DeviceId { get; set; } = "";
}

public sealed class AppConfig
{
    /// <summary>Verzeichnis fuer Geraetezertifikat, Index-Datenbanken und Cache-Zustand.</summary>
    public string HomeDirectory { get; set; } = "synct-home";

    public PeerConfig Peer { get; set; } = new();

    public List<ShareConfig> Shares { get; set; } = [];

    /// <summary>Parallele Block-Requests je Hydration.</summary>
    public int Parallelism { get; set; } = 8;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load(string path)
        => JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Format)
           ?? throw new InvalidDataException($"\"{path}\" enthaelt keine gueltige Konfiguration.");

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, Format));

    /// <summary>Eine Vorlage zum Ausfuellen.</summary>
    public static AppConfig Template(string address, string deviceId, string folderId) => new()
    {
        Peer = new PeerConfig { Address = address, DeviceId = deviceId },
        Shares =
        [
            new ShareConfig
            {
                FolderId = folderId,
                LocalPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT", folderId),
                Mode = ShareMode.OnDemand,
                CacheMaxBytes = 2L * 1024 * 1024 * 1024
            }
        ]
    };
}
