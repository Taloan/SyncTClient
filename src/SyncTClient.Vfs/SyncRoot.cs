using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace SyncTClient.Vfs;

/// <summary>
/// Meldet ein Verzeichnis bei Windows als Sync-Root an. Erst danach kann darin
/// ein Platzhalter angelegt werden.
/// </summary>
public static class SyncRoot
{
    /// <summary>
    /// Registriert <paramref name="path"/>. Ist dort bereits ein Sync-Root
    /// angemeldet, wird die Registrierung aktualisiert statt zu scheitern.
    /// </summary>
    public static unsafe void Register(string path, string providerName, string providerVersion)
    {
        Directory.CreateDirectory(path);

        var namePtr = Marshal.StringToHGlobalUni(providerName);
        var versionPtr = Marshal.StringToHGlobalUni(providerVersion);
        // Die Identitaet unterscheidet mehrere Roots desselben Anbieters.
        var identityPtr = Marshal.StringToHGlobalUni(path);

        try
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = (char*)namePtr,
                ProviderVersion = (char*)versionPtr,
                SyncRootIdentity = (void*)identityPtr,
                SyncRootIdentityLength = (uint)((path.Length + 1) * sizeof(char))
            };

            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                // FULL statt PARTIAL. Das ist ein bewusster Tausch.
                //
                // PARTIAL sagt Windows zu, dass Lesezugriffe auf einen noch
                // unvollstaendigen Platzhalter bedient werden. Das wirkt
                // sparsamer, ist es aber nicht: die Shell nutzt diese Zusage
                // und liest fuer ein Vorschaubild die Datei selbst, statt den
                // angemeldeten Vorschau-Erzeuger zu fragen. Gemessen im
                // Explorer: beim blossen Durchblaettern eines Ordners wuchs
                // der Cache auf 79 MB, und der Erzeuger wurde kein einziges
                // Mal aufgerufen.
                //
                // FULL bedeutet, dass jeder Zugriff die ganze Datei
                // nachlaedt. Die Shell kann dann nicht mehr teilweise
                // hineinlesen und greift auf die Anbieterkette zurueck, also
                // auf unseren Vorrat aus den Dateikoepfen, der ohne Netz
                // auskommt. Nextcloud macht es genauso (vfs_cfapi,
                // CfApiWrapper).
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_NONE
                },
                // FULL: wir legen alle Platzhalter selbst an, Windows muss
                // Verzeichnisse nicht on-demand nachfragen.
                Population = new CF_POPULATION_POLICY
                {
                    Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL,
                    Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE
                },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
                PlaceholderManagement = CF_PLACEHOLDER_MANAGEMENT_POLICY.CF_PLACEHOLDER_MANAGEMENT_POLICY_DEFAULT
            };

            fixed (char* pathPtr = path)
            {
                // Ohne diese beiden Flags haelt Windows den Wurzelordner fuer
                // unvollstaendig und verlangt beim ersten Auflisten eine
                // On-Demand-Population. Diese wird nicht bedient, weil alle
                // Platzhalter selbst angelegt werden. Die Folge waere ein
                // Timeout schon beim Oeffnen des Ordners.
                var result = PInvoke.CfRegisterSyncRoot(
                    pathPtr, &registration, &policies,
                    CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE
                    | CF_REGISTER_FLAGS.CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT
                    | CF_REGISTER_FLAGS.CF_REGISTER_FLAG_MARK_IN_SYNC_ON_ROOT);
                Marshal.ThrowExceptionForHR(result);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(versionPtr);
            Marshal.FreeHGlobal(identityPtr);
        }
    }

    /// <summary>
    /// Hebt die Registrierung auf. Bereits angelegte Platzhalter bleiben als
    /// Dateien liegen. Dehydrierte Platzhalter werden dabei zu leeren Dateien.
    /// </summary>
    public static void Unregister(string path)
        => Marshal.ThrowExceptionForHR(PInvoke.CfUnregisterSyncRoot(path));
}
