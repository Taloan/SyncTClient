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

            // Ein Knoten im Navigationsbereich statt einer Zeile je Ordner.
            //
            // Windows gruppiert die Wurzeln desselben Anbieters -- das ist der
            // Teil der Kennung vor dem ersten Ausrufezeichen -- unter einem
            // gemeinsamen Eintrag. Ohne das stand jede Freigabe einzeln neben
            // "Dieser PC", und bei einer Handvoll Ordnern war der Baum nicht
            // mehr zu lesen.
            ShowSiblingsAsGroup = true,
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
    /// <summary>
    /// Meldet ab, was hier angemeldet ist und zu keinem der genannten Pfade
    /// gehoert.
    /// </summary>
    /// <remarks>
    /// Eine Wurzel ueberlebt das Programm: sie steht in der Registrierung und
    /// nicht in unserer Konfiguration. Wer eine Freigabe wieder entfernt,
    /// ohne dass das Abmelden durchlief -- ein Absturz, ein Versuch, eine von
    /// Hand geloeschte Konfiguration --, hinterlaesst einen Eintrag im
    /// Navigationsbereich, den niemand mehr wegbekommt: der Ordner dazu ist
    /// fort, und ohne ihn bietet weder der Explorer noch dieses Programm eine
    /// Handhabe.
    ///
    /// Deshalb beim Start ein Abgleich mit dem, was wirklich eingerichtet
    /// ist. Verglichen werden Pfade, nicht Kennungen: die Kennung leitet sich
    /// zwar aus dem Pfad ab, aber das ist eine Eigenschaft dieser Fassung und
    /// keine Zusage.
    /// </remarks>
    public static IEnumerable<string> UnregisterStrays(IEnumerable<string> configured)
    {
        var bekannt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pfad in configured)
        {
            if (string.IsNullOrWhiteSpace(pfad)) continue;
            try { bekannt.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(pfad))); }
            catch (Exception) { /* unbrauchbarer Pfad, dann eben nicht */ }
        }

        foreach (var (id, pfad) in ListOwn().ToList())
        {
            var voll = pfad;
            try { voll = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pfad)); }
            catch (Exception) { /* der Pfad ist fort; abmelden ist dann erst recht richtig */ }

            if (bekannt.Contains(voll)) continue;

            string? fehler = null;
            try { Unregister(id); }
            catch (Exception ex) { fehler = ex.Message; }

            yield return fehler is null ? pfad : $"{pfad} -- {fehler}";
        }
    }

    public static IEnumerable<(string Id, string Path)> ListOwn()
    {
        foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            if (root.Id.StartsWith("SyncTClient!", StringComparison.Ordinal))
                yield return (root.Id, root.Path?.Path ?? "");
        }
    }
}
