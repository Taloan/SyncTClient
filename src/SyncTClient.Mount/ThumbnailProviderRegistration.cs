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
    /// Hinterlegt, wo der Vorrat liegt.
    /// </summary>
    /// <remarks>
    /// Getrennt von der Klassen-Eintragung, und das ist wichtig: der
    /// Vorschau-Erzeuger findet seinen Vorrat ueber diesen Wert, auch wenn er
    /// im Client selbst laeuft und gar keine DLL im Spiel ist.
    /// </remarks>
    public static void RegisterStore(string thumbnailDirectory)
    {
        using var own = Registry.CurrentUser.CreateSubKey(@"Software\SyncTClient");
        own.SetValue("ThumbnailStore", thumbnailDirectory);
    }

    /// <summary>Die Klasse des Kontextmenues. Eigene Kennung, dieselbe DLL.</summary>
    public const string MenuClassId = "{9C4E1F73-5A28-4D61-B0E9-3F7C6A15D482}";

    /// <summary>
    /// Sagt der Erweiterung, wo die Freigaben liegen.
    /// </summary>
    /// <remarks>
    /// Die Erweiterung laeuft im fremden Prozess und soll dort nichts fragen
    /// muessen. Ein Rechtsklick ausserhalb einer Freigabe darf sie nichts
    /// kosten -- also steht die Antwort in der Registrierung, und sie liest
    /// sie einmal.
    /// </remarks>
    public static void PublishShares(IEnumerable<string> localPaths)
    {
        using var own = Registry.CurrentUser.CreateSubKey(@"Software\SyncTClient");
        own.SetValue("Shares", localPaths.ToArray(), RegistryValueKind.MultiString);

        // Der Programmpfad steht hier ebenfalls, obwohl ihn auch RegisterMenu
        // schreibt: jenes laeuft nur, wenn die DLL im Baum gefunden wird, und
        // es steht in derselben Kette wie das Eintragen am Sync-Root. Faellt
        // die Kette aus, fehlt sonst das Symbol vor einem Eintrag, den die
        // bereits eingetragene Klasse durchaus noch anzeigt.
        if (Environment.ProcessPath is { } programm)
            own.SetValue("Programm", programm);
    }

    /// <summary>
    /// Traegt das Kontextmenue ein.
    /// </summary>
    /// <remarks>
    /// Als klassische Erweiterung und ohne Wirt: ein Kontextmenue muss im
    /// Prozess des Datei-Managers laufen, sonst kann es kein Menue in dessen
    /// Fenster haengen. Das ist der Unterschied zum Vorschau-Erzeuger, der
    /// gerade deshalb in dllhost.exe sitzt.
    ///
    /// Eingetragen wird fuer Verzeichnisse und fuer alle Dateien. Ob die
    /// Eintraege erscheinen, entscheidet die Erweiterung selbst anhand der
    /// Auswahl -- die Registrierung kann das nicht, sie kennt nur Dateitypen.
    /// </remarks>
    public static void RegisterMenu(string libraryPath)
    {
        // Woher das Symbol fuer den Menueeintrag kommt. Die Erweiterung laeuft
        // im Datei-Manager und weiss nicht, wo dieses Programm liegt; in der
        // DLL selbst steckt kein Symbol, sie ist ein reiner Anbieter.
        if (Environment.ProcessPath is { } programm)
        {
            using var own = Registry.CurrentUser.CreateSubKey(@"Software\SyncTClient");
            own.SetValue("Programm", programm);
        }

        using (var clsid = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{MenuClassId}"))
        {
            clsid.SetValue(null, "SyncTClient");

            using var server = clsid.CreateSubKey("InprocServer32");
            server.SetValue(null, libraryPath);
            server.SetValue("ThreadingModel", "Apartment");
        }

        foreach (var pfad in new[]
                 {
                     @"Directory\shellex\ContextMenuHandlers\SyncTClient",
                     @"*\shellex\ContextMenuHandlers\SyncTClient"
                 })
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{pfad}");
            key.SetValue(null, MenuClassId);
        }
    }

    /// <summary>
    /// Traegt die COM-Klasse als DLL ein.
    /// </summary>
    /// <remarks>
    /// Nur moeglich, wenn die native DLL vorliegt. In einer veroeffentlichten
    /// Version ist sie es nicht -- sie ist ein eigenes NativeAOT-Projekt, das
    /// niemand referenziert. Die Vorschauen haengen davon aber nicht ab: die
    /// Shell erreicht den Erzeuger ueber die Klasse, die der laufende Client
    /// anmeldet. Dieser Weg hier ist die Zugabe fuer den Fall, dass der
    /// Client gerade nicht laeuft.
    /// </remarks>
    public static void RegisterClass(string libraryPath)
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

    /// <summary>
    /// Eine Datei, in ihre Bestandteile zerlegt.
    /// </summary>
    /// <remarks>
    /// Getrennt und nicht als eine Zeile: die Anzeige stellt zwei davon
    /// nebeneinander, und verglichen wird dabei Fassung mit Fassung und
    /// Datum mit Datum. Als fortlaufender Satz war das nicht zu lesen.
    /// </remarks>
    public readonly record struct Datei(string? Pfad, string Fassung, string Geaendert)
    {
        public string Name => Pfad is null ? "" : Path.GetFileName(Pfad);

        public string Ordner => Pfad is null ? "" : Path.GetDirectoryName(Pfad) ?? "";
    }

    /// <summary>
    /// Was von diesem Programm in der Registrierung steht.
    /// </summary>
    public readonly record struct Zustand(
        Datei Mitgeliefert, Datei Eingetragen, bool MenuRegistered, int SyncRoots)
    {
        /// <summary>Ob die Klasse ueberhaupt eingetragen ist.</summary>
        public bool ClassRegistered => Eingetragen.Pfad is not null;

        /// <summary>
        /// Ob die mitgelieferte Datei eine andere ist als die eingetragene.
        /// </summary>
        /// <remarks>
        /// Nach einem Umzug des Programms oder einer neuen Fassung zeigt der
        /// Eintrag noch auf die alte Datei. Der Explorer laedt dann weiter
        /// jene -- ein Fehler, der sich als "die Aenderung wirkt nicht"
        /// zeigt und sonst nirgends.
        /// </remarks>
        public bool Veraltet
            => Mitgeliefert.Pfad is not null
               && (Eingetragen.Pfad is null
                   || !string.Equals(Mitgeliefert.Pfad, Eingetragen.Pfad, StringComparison.OrdinalIgnoreCase)
                   || Mitgeliefert.Fassung != Eingetragen.Fassung
                   || Mitgeliefert.Geaendert != Eingetragen.Geaendert);
    }

    /// <summary>
    /// Sieht nach, was tatsaechlich eingetragen ist.
    /// </summary>
    /// <remarks>
    /// Alles hier geschieht beim Start von selbst, und alles kann lautlos
    /// scheitern: eine fehlende DLL, ein Eintrag, der nach einem Umzug ins
    /// Leere zeigt, eine neuere Datei neben einem alten Eintrag. Wer das
    /// nachsehen wollte, musste bisher die Registrierung durchsuchen.
    /// </remarks>
    public static Zustand Nachsehen()
        => new(
            Lesen(FindLibrary()),
            Lesen(Wert($@"Software\Classes\CLSID\{ClassId}\InprocServer32")),
            Eingetragen($@"Software\Classes\Directory\shellex\ContextMenuHandlers\SyncTClient"),
            OwnSyncRootIds().Count());

    /// <summary>
    /// Fassung und Datum einer Datei.
    /// </summary>
    /// <remarks>
    /// Beides, denn die Fassung allein genuegt nicht: eine DLL, die zwischen
    /// zwei Builds dieselbe Nummer traegt, ist trotzdem eine andere Datei.
    /// </remarks>
    private static Datei Lesen(string? path)
    {
        if (path is null) return new Datei(null, "", "");
        if (!File.Exists(path)) return new Datei(path, "", "");

        try
        {
            var fassung = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
            return new Datei(path, fassung, File.GetLastWriteTime(path).ToString("dd.MM.yyyy HH:mm"));
        }
        catch (Exception)
        {
            return new Datei(path, "", "");
        }
    }

    private static string? Wert(string pfad)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(pfad);
            return key?.GetValue(null) as string is { Length: > 0 } wert ? wert : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Eingetragen(string pfad)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(pfad);
            return key?.GetValue(null) is string wert && wert.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Nimmt das Kontextmenue wieder heraus.
    /// </summary>
    /// <remarks>
    /// Das Gegenstueck zu <see cref="RegisterMenu"/>. Bisher gab es keines --
    /// wer das Programm entfernte, liess seine Eintraege stehen, und der
    /// Explorer suchte fortan bei jedem Rechtsklick eine DLL, die es nicht
    /// mehr gibt.
    /// </remarks>
    public static void UnregisterMenu()
    {
        foreach (var pfad in new[]
                 {
                     $@"Software\Classes\CLSID\{MenuClassId}",
                     @"Software\Classes\Directory\shellex\ContextMenuHandlers\SyncTClient",
                     @"Software\Classes\*\shellex\ContextMenuHandlers\SyncTClient"
                 })
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(pfad, throwOnMissingSubKey: false); }
            catch (UnauthorizedAccessException) { /* dann bleibt er stehen */ }
        }
    }

    /// <summary>
    /// Nimmt die Vorschau-Klasse und die eigenen Werte wieder heraus.
    /// </summary>
    /// <remarks>
    /// Auch "Software\SyncTClient": dort stehen der Vorrat, die Liste der
    /// Freigaben und der Programmpfad. Sie sind fuer die Erweiterung
    /// gedacht; ohne sie hat niemand mehr etwas davon.
    /// </remarks>
    public static void UnregisterClass()
    {
        foreach (var path in new[]
                 {
                     $@"Software\Classes\CLSID\{ClassId}",
                     $@"Software\Classes\AppID\{AppId}",
                     @"Software\SyncTClient"
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
