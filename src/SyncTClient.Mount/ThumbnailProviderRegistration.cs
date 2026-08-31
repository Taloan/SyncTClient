using Microsoft.Win32;

namespace SyncTClient.Mount;

/// <summary>
/// Meldet die Shell-Erweiterung an, damit der Explorer die vorbereiteten
/// Vorschaubilder zeigt.
/// </summary>
/// <remarks>
/// Der Eintrag <c>ThumbnailProvider</c> am Sync-Root ist der Platz, den
/// Windows dafuer vorsieht. OneDrive und Nextcloud nutzen ihn genauso. Er
/// gilt nur fuer den eigenen Ordner: eine systemweite Uebernahme aller
/// JPEG-Vorschauen findet nicht statt.
///
/// Alles hier laeuft unter HKEY_CURRENT_USER beziehungsweise auf einem
/// Schlüssel, den die Sync-Root-Registrierung dem Benutzer ueberlassen hat.
/// Adminrechte werden nicht benoetigt.
/// </remarks>
public static class ThumbnailProviderRegistration
{
    /// <summary>Muss mit Exports.ClassId in der Erweiterung uebereinstimmen.</summary>
    public const string ClassId = "{7E4B2A61-3C9D-4F58-9A17-6D2E5B84C013}";

    /// <summary>
    /// Anwendungskennung, die COM anweist, die DLL in einem eigenen Prozess zu
    /// betreiben statt im Aufrufer.
    /// </summary>
    /// <remarks>
    /// Dieser Eintrag ist notwendig. Gemessen an Nextcloud, das dieselbe
    /// Bauform verwendet: derselbe Anbieter und dieselbe Datei liefern im
    /// eigenen Prozess erzeugt <c>E_FAIL</c>, ueber den Surrogat dagegen eine
    /// Vorschau. Ohne diesen Eintrag laedt COM die DLL beim Aufrufer und
    /// umgeht die Abschottung, die die Shell dabei voraussetzt.
    /// </remarks>
    public const string AppId = "{C2A9B4D7-5E31-4A88-9F60-71B3E8C42D19}";

    private const string SyncRootManager =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>Sucht die native DLL an den Stellen, an denen sie liegen kann.</summary>
    public static string? FindLibrary()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "synctthumbs.dll");
        if (File.Exists(beside)) return beside;

        // Im Entwicklungsbaum liegt sie im Publish-Ordner ihres eigenen
        // Projekts, je nach Plattformwahl mit oder ohne x64-Zwischenstufe.
        string[][] variants =
        [
            ["bin", "Release", "net10.0-windows", "win-x64", "publish"],
            ["bin", "x64", "Release", "net10.0-windows", "win-x64", "publish"]
        ];

        var newest = (Path: (string?)null, Written: DateTime.MinValue);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            foreach (var variant in variants)
            {
                var candidate = Path.Combine(
                    [directory.FullName, "src", "SyncTClient.ThumbProvider", .. variant, "synctthumbs.dll"]);

                // Beide Varianten koennen nebeneinander liegen. Die aeltere
                // waere eine schwer zu findende Fehlerquelle.
                if (File.Exists(candidate) && File.GetLastWriteTimeUtc(candidate) > newest.Written)
                    newest = (candidate, File.GetLastWriteTimeUtc(candidate));
            }

            if (newest.Path is not null) return newest.Path;
        }

        return null;
    }

    /// <summary>
    /// Traegt die COM-Klasse ein und hinterlegt, wo der Vorrat liegt. Ohne
    /// den zweiten Teil findet die Erweiterung ihre Bilder nicht.
    /// </summary>
    public static void RegisterClass(string libraryPath, string thumbnailDirectory)
    {
        using (var clsid = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{ClassId}"))
        {
            clsid.SetValue(null, "SyncTClient Vorschaubilder");
            clsid.SetValue("AppID", AppId);

            using var server = clsid.CreateSubKey("InprocServer32");
            server.SetValue(null, libraryPath);
            server.SetValue("ThreadingModel", "Apartment");
        }

        // Ein leerer DllSurrogate-Wert waehlt den mitgelieferten Wirt
        // (dllhost.exe). Der Wert muss vorhanden und leer sein. Fehlt er,
        // laeuft die DLL im Aufrufer.
        using (var appId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppID\{AppId}"))
        {
            appId.SetValue(null, "SyncTClient Vorschaubilder");
            appId.SetValue("DllSurrogate", "");
        }

        using var own = Registry.CurrentUser.CreateSubKey(@"Software\SyncTClient");
        own.SetValue("ThumbnailStore", thumbnailDirectory);
    }

    /// <summary>
    /// Haengt die Erweiterung an einen Sync-Root. Der Schluessel entsteht nur
    /// bei der Anmeldung ueber StorageProviderSyncRootManager. Die
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
        foreach (var path in new[]
                 {
                     $@"Software\Classes\CLSID\{ClassId}",
                     $@"Software\Classes\AppID\{AppId}"
                 })
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch (UnauthorizedAccessException) { /* egal */ }
        }
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
