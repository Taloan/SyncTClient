using Microsoft.Win32;

namespace SyncTClient.Mount;

/// <summary>
/// Meldet die Shell-Erweiterung an, die Explorer die vorbereiteten
/// Vorschaubilder zeigt.
/// </summary>
/// <remarks>
/// Der Eintrag <c>ThumbnailProvider</c> am Sync-Root ist der Platz, den
/// Windows dafuer vorsieht -- OneDrive und Nextcloud nutzen ihn genauso. Er
/// gilt nur fuer den eigenen Ordner: eine systemweite Uebernahme aller
/// JPEG-Vorschauen findet nicht statt.
///
/// Alles hier laeuft unter HKEY_CURRENT_USER beziehungsweise auf einem
/// Schlüssel, den die Sync-Root-Registrierung dem Benutzer ueberlassen hat.
/// Adminrechte braucht es nicht.
/// </remarks>
public static class ThumbnailProviderRegistration
{
    /// <summary>Muss mit Exports.ClassId in der Erweiterung uebereinstimmen.</summary>
    public const string ClassId = "{7E4B2A61-3C9D-4F58-9A17-6D2E5B84C013}";

    private const string SyncRootManager =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>Sucht die native DLL an den Stellen, an denen sie liegen kann.</summary>
    public static string? FindLibrary()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "synctthumbs.dll");
        if (File.Exists(beside)) return beside;

        // Im Entwicklungsbaum liegt sie im Publish-Ordner ihres eigenen Projekts.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName,
                "src", "SyncTClient.ThumbProvider", "bin", "x64", "Release",
                "net10.0-windows", "win-x64", "publish", "synctthumbs.dll");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Traegt die COM-Klasse ein und hinterlegt, wo der Vorrat liegt. Ohne
    /// diesen zweiten Teil faende die Erweiterung ihre Bilder nicht.
    /// </summary>
    public static void RegisterClass(string libraryPath, string thumbnailDirectory)
    {
        using (var clsid = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{ClassId}"))
        {
            clsid.SetValue(null, "SyncTClient Vorschaubilder");
            using var server = clsid.CreateSubKey("InprocServer32");
            server.SetValue(null, libraryPath);
            server.SetValue("ThreadingModel", "Apartment");
        }

        using var own = Registry.CurrentUser.CreateSubKey(@"Software\SyncTClient");
        own.SetValue("ThumbnailStore", thumbnailDirectory);
    }

    /// <summary>
    /// Haengt die Erweiterung an einen Sync-Root. Der Schluessel entsteht nur
    /// bei der Anmeldung ueber StorageProviderSyncRootManager -- die
    /// Win32-Anmeldung legt ihn nicht an.
    /// </summary>
    public static bool AttachToSyncRoot(string syncRootId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{SyncRootManager}\{syncRootId}", writable: true);
            if (key is null) return false;

            key.SetValue("ThumbnailProvider", ClassId);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void DetachFromSyncRoot(string syncRootId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{SyncRootManager}\{syncRootId}", writable: true);
            key?.DeleteValue("ThumbnailProvider", throwOnMissingValue: false);
        }
        catch (UnauthorizedAccessException) { /* dann bleibt er stehen */ }
    }

    public static void UnregisterClass()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{ClassId}", throwOnMissingSubKey: false); }
        catch (UnauthorizedAccessException) { /* egal */ }
    }

    /// <summary>Alle Sync-Roots, die dieses Programm angemeldet hat.</summary>
    public static IEnumerable<string> OwnSyncRootIds()
    {
        using var root = Registry.LocalMachine.OpenSubKey(SyncRootManager);
        if (root is null) yield break;

        foreach (var name in root.GetSubKeyNames())
            if (name.StartsWith("SyncTClient!", StringComparison.Ordinal))
                yield return name;
    }
}
