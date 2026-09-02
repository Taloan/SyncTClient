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
    /// <summary>Wo Windows die angemeldeten Wurzeln fuehrt.</summary>
    private const string SyncRootManagerKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>
    /// Das Symbol im Navigationsbereich.
    /// </summary>
    /// <remarks>
    /// Bisher die Windows-Wolke aus <c>imageres.dll</c>. Sie steht dort fuer
    /// jeden Anbieter und war von OneDrive nicht zu unterscheiden.
    ///
    /// Genommen wird das Symbol der laufenden Programmdatei -- denselben Weg
    /// geht Nextcloud, dessen Eintrag auf seine eigene Exe zeigt. Der Pfad
    /// steht in der Registrierung und gilt, bis neu angemeldet wird; zieht
    /// das Programm um, holt die naechste Anmeldung ihn nach.
    ///
    /// Ohne Programmdatei -- gehostet, aus einem Testlauf heraus -- bleibt es
    /// bei der Wolke. Ein Eintrag, der ins Leere zeigt, waere schlechter als
    /// ein fremdes Symbol.
    /// </remarks>
    private static string Symbol
        => Environment.ProcessPath is { Length: > 0 } exe
            ? exe
            : @"%SystemRoot%\system32\imageres.dll,-1043";

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

        // Eine Wurzel ueberlebt das Programm. Nach einem gewoehnlichen Neustart
        // steht sie noch genau so da, wie wir sie hinterlassen haben -- und
        // dann ist das Anmelden reine Arbeit ohne Wirkung.
        //
        // Es ist keine billige Arbeit: jeder Aufruf schreibt in die
        // Registrierung und benachrichtigt die Shell. Bei einer Handvoll
        // Freigaben, zweimal je Freigabe, stand der Rechner beim Start ein
        // paar Sekunden.
        //
        // Die Fassung entscheidet, ob neu angemeldet wird. Wer an den
        // Eigenschaften etwas aendert, zaehlt sie hoch; sonst bleibt es beim
        // Bestand.
        if (await StehtSchonSoDa(id, providerVersion).ConfigureAwait(false)) return id;

        var folder = await StorageFolder.GetFolderFromPathAsync(full);

        var info = new StorageProviderSyncRootInfo
        {
            Id = id,
            Path = folder,
            DisplayNameResource = displayName,
            IconResource = Symbol,
            Version = providerVersion,

            // Der eigentliche Grund fuer diesen Weg.
            HydrationPolicy = StorageProviderHydrationPolicy.Partial,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.StreamingAllowed,

            // Wir legen alle Platzhalter selbst an.
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,

            // Jede Freigabe steht fuer sich im Navigationsbereich.
            //
            // Mit true gruppiert Windows -- aber nicht nach Anbieter, wie es
            // die Bezeichnung nahelegt, sondern nach gemeinsamem
            // Elternverzeichnis, und es benennt den Knoten danach. Aus sieben
            // Freigaben wurden vier Knoten mit den Namen "DATA", "dirkm",
            // "GPSoftware" und "johnsadventures.com". Das sagt niemandem
            // etwas und ist schlechter als sieben ehrliche Zeilen.
            //
            // Ein einziger Knoten, so wie ihn Nextcloud hat, waere nur mit
            // einer einzigen Wurzel zu haben -- ein Pfad, unter dem alles
            // liegt. Genau das geht hier nicht: der Ordner des Background
            // Switcher muss unter AppData\Roaming\johnsadventures.com liegen
            // und der von Directory Opus unter AppData\Roaming\GPSoftware,
            // weil diese Programme dort lesen. Das ist der Grund, warum es
            // dieses Programm ueberhaupt gibt.
            ShowSiblingsAsGroup = false,
            // Context ist Pflicht und darf nicht leer sein.
            Context = CryptographicBuffer.ConvertStringToBinary(
                full, BinaryStringEncoding.Utf8)
        };

        // Ausdruecklich auf einen eigenen Faden. Der Aufruf ist synchron und
        // kommt ueber das Verbinden aus der Oberflaeche; dort gehoert er
        // nicht hin.
        await Task.Run(() => StorageProviderSyncRootManager.Register(info)).ConfigureAwait(false);
        return id;
    }

    /// <summary>Ist diese Wurzel schon in genau dieser Fassung angemeldet?</summary>
    /// <remarks>
    /// Faellt der Aufruf durch, ist sie es nicht. Das ist der Normalfall beim
    /// ersten Mal und kein Fehler.
    /// </remarks>
    private static async Task<bool> StehtSchonSoDa(string id, string providerVersion)
        => await Task.Run(() =>
        {
            try
            {
                var vorhanden = StorageProviderSyncRootManager.GetSyncRootInformationForId(id);
                return vorhanden.Version == providerVersion;
            }
            catch (Exception)
            {
                return false;
            }
        }).ConfigureAwait(false);

    /// <summary>
    /// Bestimmt, ob die Wurzel im Navigationsbereich des Explorers steht.
    /// </summary>
    /// <remarks>
    /// Die Anmeldeschnittstelle kennt dafuer keinen Schalter. Windows legt
    /// beim Anmelden einen Namensraum-Eintrag an und merkt sich dessen
    /// Kennung bei der Wurzel; ob der Eintrag im Baum erscheint, entscheidet
    /// die Eigenschaft "System.IsPinnedToNamespaceTree" an seiner Klasse.
    /// OneDrive benutzt dieselbe.
    ///
    /// Gesetzt wird nach jedem Anmelden, denn das Anmelden setzt sie auf
    /// eins zurueck.
    ///
    /// Die Wurzel selbst bleibt unberuehrt: Platzhalter, Wolkensymbole und
    /// Kontextmenue haengen an ihr und nicht am Eintrag im Baum.
    /// </remarks>
    /// <returns>Ob die Eigenschaft gesetzt werden konnte.</returns>
    public static bool ShowInTree(string id, bool sichtbar)
    {
        try
        {
            using var wurzel = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                SyncRootManagerKey + @"\" + id);

            if (wurzel?.GetValue("NamespaceCLSID") is not string clsid || clsid.Length == 0) return false;

            using var klasse = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Classes\CLSID\" + clsid, writable: true);

            if (klasse is null) return false;

            klasse.SetValue("System.IsPinnedToNamespaceTree", sichtbar ? 1 : 0,
                            Microsoft.Win32.RegistryValueKind.DWord);
            return true;
        }
        catch (Exception)
        {
            // Kein Zugriff, kein Eintrag, umbenannte Schluessel. Der Ordner
            // steht dann im Baum -- unschoen, aber nicht schaedlich.
            return false;
        }
    }

    public static void Unregister(string id)
        => StorageProviderSyncRootManager.Unregister(id);

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

        var eigene = ListOwn().ToList();
        yield return $"{eigene.Count} eigene Sync-Wurzeln angemeldet, {bekannt.Count} eingerichtet.";

        foreach (var (id, pfad) in eigene)
        {
            var voll = pfad;
            try { voll = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pfad)); }
            catch (Exception) { /* der Pfad ist fort; abmelden ist dann erst recht richtig */ }

            // Ein leerer Pfad heisst: der Ordner ist nicht mehr aufzuloesen.
            // Das ist kein Zweifelsfall, sondern der Rest selbst.
            if (voll.Length > 0 && bekannt.Contains(voll)) continue;

            var wer = voll.Length > 0 ? voll : id;

            string? fehler = null;
            try { Unregister(id); }
            catch (Exception ex) { fehler = ex.Message; }

            yield return fehler is null ? $"abgemeldet: {wer}" : $"bleibt: {wer} -- {fehler}";
        }
    }

    /// <summary>
    /// Alle von diesem Programm angemeldeten Wurzeln. Wird zum Aufraeumen
    /// gebraucht.
    /// </summary>
    /// <remarks>
    /// Gelesen wird die Registrierung, nicht
    /// <c>GetCurrentSyncRoots</c>. Die Schnittstelle laesst genau die
    /// Eintraege aus, um die es beim Aufraeumen geht: fehlt der Ordner, faellt
    /// die Wurzel aus ihrer Liste heraus -- steht aber weiter in der
    /// Registrierung und belegt ihren Pfad.
    ///
    /// Gemessen: sieben gemeldete Wurzeln bei zehn eingetragenen. Die
    /// fehlende hing an einem geloeschten Ordner, war deshalb weder zu finden
    /// noch abzumelden, und verhinderte die Anmeldung des Elternordners --
    /// Windows laesst keine Wurzel zu, die eine andere enthaelt. Zu beheben
    /// war das nur, indem der Benutzer den Schluessel von Hand loeschte.
    ///
    /// Der Aufbau steht fest: unter <c>SyncRootManager\&lt;Kennung&gt;</c>
    /// liegt <c>UserSyncRoots</c>, dessen Werte je Benutzer-SID den Pfad
    /// nennen. Die Kennung traegt die SID bereits; gesucht wird nur, was
    /// diesem Benutzer gehoert, damit ein zweites Konto am selben Rechner
    /// unberuehrt bleibt.
    /// </remarks>
    public static IEnumerable<(string Id, string Path)> ListOwn()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "S-1-0-0";
        var praefix = $"SyncTClient!{sid}!";

        using var verwaltung = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SyncRootManagerKey);
        if (verwaltung is null) yield break;

        string[] kennungen;
        try { kennungen = verwaltung.GetSubKeyNames(); }
        catch (Exception) { yield break; }

        foreach (var id in kennungen)
        {
            if (!id.StartsWith(praefix, StringComparison.OrdinalIgnoreCase)) continue;

            // Ein einzelner unlesbarer Eintrag darf die Liste nicht kippen.
            // Ohne Pfad bleibt die Kennung, und die genuegt zum Abmelden.
            var pfad = "";
            try
            {
                using var wurzeln = verwaltung.OpenSubKey(id + @"\UserSyncRoots");
                if (wurzeln is not null && wurzeln.GetValue(sid) is string p)
                    pfad = p;
            }
            catch (Exception) { /* unlesbar; dann eben ohne Pfad */ }

            yield return (id, pfad);
        }
    }
}
