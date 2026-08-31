using System.Security.Principal;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace SyncTClient.Vfs;

/// <summary>
/// Meldet einen Sync-Root ueber <see cref="StorageProviderSyncRootManager"/> an
/// statt ueber <c>CfRegisterSyncRoot</c>.
/// </summary>
/// <remarks>
/// Dieser Weg traegt die Hydrations-Politik als ausdrueckliche Eigenschaft.
/// Vermutlich wird <c>CF_HYDRATION_POLICY_PARTIAL</c> nur so beachtet. Ueber
/// die Win32-Registrierung allein forderte Windows selbst bei einem
/// 4-KB-Lesezugriff die ganze Datei an.
///
/// Nebenwirkung: der Ordner erscheint mit Namen und Symbol in der
/// Navigationsleiste des Explorers, so wie OneDrive.
/// </remarks>
public static class WinRtSyncRoot
{
    /// <summary>
    /// Registriert <paramref name="path"/> und liefert die vergebene Id
    /// zurueck, die zum Abmelden gebraucht wird.
    /// </summary>
    public static async Task<string> RegisterAsync(
        string path, string displayName, string providerVersion)
    {
        Directory.CreateDirectory(path);
        var full = Path.GetFullPath(path);

        // Die Id muss geraeteweit eindeutig sein. Ueblich ist
        // <Anbieter>!<Benutzer-SID>!<Konto>. Als Konto wird der Pfad
        // verwendet, damit mehrere Shares desselben Benutzers unterschieden
        // werden.
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "S-1-0-0";
        var id = $"SyncTClient!{sid}!{Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.Unicode.GetBytes(full)))[..16]}";

        var folder = await StorageFolder.GetFolderFromPathAsync(full);

        var info = new StorageProviderSyncRootInfo
        {
            Id = id,
            Path = folder,
            DisplayNameResource = displayName,
            // Ein vorhandenes Systemsymbol. Eigene Ressourcen kommen spaeter.
            IconResource = @"%SystemRoot%\system32\imageres.dll,-1043",
            Version = providerVersion,

            // Der eigentliche Grund fuer diesen Weg.
            HydrationPolicy = StorageProviderHydrationPolicy.Partial,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.StreamingAllowed,

            // Wir legen alle Platzhalter selbst an.
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,

            ShowSiblingsAsGroup = false,
            // Context ist Pflicht und darf nicht leer sein.
            Context = CryptographicBuffer.ConvertStringToBinary(
                full, BinaryStringEncoding.Utf8)
        };

        StorageProviderSyncRootManager.Register(info);
        return id;
    }

    public static void Unregister(string id)
        => StorageProviderSyncRootManager.Unregister(id);

    /// <summary>
    /// Alle von diesem Programm angemeldeten Roots. Wird zum Aufraeumen
    /// gebraucht.
    /// </summary>
    public static IEnumerable<(string Id, string Path)> ListOwn()
    {
        foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            if (root.Id.StartsWith("SyncTClient!", StringComparison.Ordinal))
                yield return (root.Id, root.Path?.Path ?? "");
        }
    }
}
