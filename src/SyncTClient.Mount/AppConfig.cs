using System.Text.Json;
using System.Text.Json.Serialization;
using SyncTClient.Vfs;

namespace SyncTClient.Mount;

/// <summary>Wie ein Share lokal vorgehalten wird.</summary>
public enum ShareMode
{
    /// <summary>Nur Platzhalter; Inhalte kommen beim Zugriff und unterliegen dem Verbrauchs Limit.</summary>
    OnDemand,

    /// <summary>Alles wird lokal vorgehalten; das Verbrauchs Limit gilt nicht.</summary>
    AlwaysLocal
}

/// <summary>
/// Was geschieht, wenn beide Seiten dieselbe Datei geaendert haben.
/// </summary>
public enum ConflictResolution
{
    /// <summary>
    /// Beide Versionen bleiben. Umbenannt wird die lokale.
    /// </summary>
    /// <remarks>
    /// Welche der beiden umbenannt wird, ist keine Bewertung, sondern eine
    /// Festlegung: es ist immer die hiesige. Die Gegenstelle behaelt den
    /// Namen, weil sie ihn auch bei allen anderen Knoten behaelt -- wuerde
    /// jeder Knoten die fremde Version umbenennen, entstuenden aus einem
    /// Konflikt so viele Dateien wie es Knoten gibt.
    ///
    /// Der Name folgt dem Muster von Syncthing, an der Stelle der Kurzkennung
    /// steht aber der Geraetename:
    /// <c>name.sync-conflict-JJJJMMTT-HHMMSS-GERAET.endung</c>. Eine
    /// Kurzkennung waere zwar auf beiden Seiten dieselbe, sagt aber niemandem,
    /// an welchem Geraet die Version entstand.
    /// </remarks>
    KeepBoth,

    /// <summary>Die zuletzt geaenderte Version gewinnt.</summary>
    Newer,

    /// <summary>Die aeltere Version gewinnt.</summary>
    Older,

    /// <summary>Die hiesige Version gewinnt.</summary>
    Local,

    /// <summary>Die Version der Gegenstelle gewinnt.</summary>
    Remote
}

/// <summary>Welche Freigaben die Uebersicht zeigt.</summary>
public enum ShareFilter
{
    /// <summary>Alle, auch die blosz angebotenen.</summary>
    Alle,

    /// <summary>Nur die, die gerade laufen.</summary>
    Verbunden,

    /// <summary>Nur die, die gerade nicht laufen.</summary>
    Getrennt
}

/// <summary>Eine Gegenstelle. Das ist ein Server oder ein anderer Rechner.</summary>
public sealed class PeerConfig
{
    /// <summary>Anzeigename. Ist er leer, wird der Name der Gegenstelle verwendet.</summary>
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
    /// Ein Relay leitet fremden Verkehr weiter. Er ist der Weg zu einem Geraet,
    /// das keinen Port nach aussen offen hat, und langsamer als jede direkte
    /// Verbindung. Wer beides nicht will, schaltet ihn hier ab. Dann kommt eine
    /// Verbindung nur zustande, wenn ein direkter Weg besteht.
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

    /// <summary>
    /// Alle Gegenstellen, die an diesem Ordner teilnehmen.
    /// </summary>
    /// <remarks>
    /// Ein Ordner ist ein Ordner, kein Verhaeltnis zu einer Gegenstelle. Er
    /// hat einen Pfad, eine Auswahl und einen Index; wer daran teilnimmt,
    /// steht hier.
    ///
    /// <see cref="PeerDeviceId"/> bleibt daneben stehen, solange der Abgleich
    /// noch je Gegenstelle gefuehrt wird. Der Eintrag ist der erste dieser
    /// Liste.
    /// </remarks>
    public List<string> PeerDeviceIds { get; set; } = [];

    /// <summary>Anzeigename. Die Gegenstelle nennt meist einen.</summary>
    public string Label { get; set; } = "";

    /// <summary>Wo der Ordner im Explorer erscheint.</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>Hier entstanden, statt von einer Gegenstelle uebernommen.</summary>
    /// <remarks>
    /// Eine uebernommene Freigabe wartet beim Start auf den Index der
    /// Gegenstelle: ohne ihn weiss sie nicht, was in ihr liegt, und ohne
    /// diesen Index waere jeder Platzhalter geraten. Eine hier entstandene
    /// weiss es selbst -- der Ordner steht bereits da. Auf einen Index zu
    /// warten hiesse hier, auf eine Gegenstelle zu warten, die den Ordner
    /// erst noch annehmen muss, und das kann Tage dauern.
    /// </remarks>
    public bool Own { get; set; }

    public ShareMode Mode { get; set; } = ShareMode.OnDemand;

    /// <summary>
    /// Was geschieht, wenn beide Seiten dieselbe Datei geaendert haben.
    /// </summary>
    /// <remarks>
    /// Ausser bei <see cref="ConflictResolution.KeepBoth"/> bleibt nur eine
    /// Version im Ordner. Die andere wird unter <c>.stversions</c> abgelegt,
    /// sofern <see cref="KeepVersions"/> eingeschaltet ist.
    /// </remarks>
    public ConflictResolution Conflict { get; set; } = ConflictResolution.KeepBoth;

    /// <summary>
    /// Ersetzte und geloeschte Versionen werden gesichert, statt sofort
    /// geloescht zu werden.
    /// </summary>
    /// <remarks>
    /// Sie liegen im Ordner ".stversions" innerhalb der Freigabe. Dieser
    /// Ordner nimmt am Abgleich nicht teil: er wird nicht angekuendigt, nicht
    /// in den Index aufgenommen und beim Freigeben von Speicherplatz nicht
    /// angefasst.
    /// </remarks>
    public bool KeepVersions { get; set; } = true;

    /// <summary>Wie lange eine gesicherte Version erhalten bleibt, in Tagen.</summary>
    public int VersionDays { get; set; } = 7;

    /// <summary>
    /// Wie viele andere Knoten eine Datei vollstaendig fuehren muessen, bevor
    /// ihr Speicherplatz hier freigegeben werden darf.
    /// </summary>
    /// <remarks>
    /// Beim Freigeben werden die lokalen Bytes geloescht; die Datei bleibt als
    /// Platzhalter stehen und wird beim naechsten Oeffnen wieder geladen. Sie
    /// liegt danach nur noch auf den anderen Knoten. Bei einer einzigen
    /// Gegenstelle, dem Normalfall gegen einen Server, ist 1 die sinnvolle
    /// Vorgabe. Wer mehrere Knoten hat und seine Dateien nicht von einem
    /// einzigen Geraet abhaengig machen will, setzt 2.
    ///
    /// Kleiner als 1 ist nicht zulaessig: dann koennte der letzte Inhalt einer
    /// Datei im Netz verschwinden.
    /// </remarks>
    public int MinimumCopies { get; set; } = 1;

    /// <summary>
    /// Teilbaum-Auswahl: nur diese Pfade werden ueberhaupt projiziert. Leer
    /// bedeutet alles. Pfade relativ zum Share, mit / als Trenner.
    /// </summary>
    /// <remarks>
    /// Das ist die Datenseite des aufklappbaren Baums in der Oberflaeche.
    /// Ausgeschlossene Verzeichnisse bekommen keinen Platzhalter. Sie
    /// erscheinen nicht im Explorer und belegen keinen Index-Speicher.
    /// </remarks>
    public List<string> Included { get; set; } = [];

    /// <summary>
    /// Muster, die vom Abgleich ausgenommen sind. Eines je Zeile.
    /// </summary>
    /// <remarks>
    /// Etwas anderes als <see cref="Included"/>. Der Baum sagt, welche Zweige
    /// auf diesem Geraet liegen sollen -- was er abwaehlt, wird trotzdem
    /// uebertragen. Ein Muster nimmt den Namen ganz heraus: er wird nicht
    /// angekuendigt, nicht geholt und nicht angelegt.
    ///
    /// Vorhandene Dateien werden davon nicht angefasst. Ein Muster sagt "das
    /// gehoert nicht zum Abgleich", nicht "das darf weg".
    ///
    /// Die Schreibweise steht bei <see cref="IgnoreRules"/>.
    /// </remarks>
    public List<string> Ignored { get; set; } = [];

    /// <summary>Die uebersetzten Muster, gemerkt bis sich die Liste aendert.</summary>
    /// <remarks>
    /// Uebersetzt wird einmal, gefragt wird je Datei und Durchgang. Beides in
    /// einem Feld, damit ein Wechsel nicht halb sichtbar wird: die Muster von
    /// eben mit dem Stand von jetzt waeren schlimmer als ein Umweg.
    /// </remarks>
    private sealed record Stand(string Quelle, IgnoreRules Regeln);

    [JsonIgnore] private Stand? _stand;

    [JsonIgnore]
    public IgnoreRules Rules
    {
        get
        {
            var quelle = string.Join('\n', Ignored);
            var stand = _stand;
            if (stand is null || stand.Quelle != quelle)
                _stand = stand = new Stand(quelle, IgnoreRules.Parse(Ignored));

            return stand.Regeln;
        }
    }

    /// <summary>Nimmt ein Muster diesen Namen aus dem Abgleich?</summary>
    public bool IsIgnored(string relativePath)
        => Ignored.Count > 0 && Rules.Matches(relativePath);

    /// <summary>
    /// Der Abstand zwischen zwei vollstaendigen Durchgaengen, in Sekunden.
    /// </summary>
    /// <remarks>
    /// Eine Stunde als Vorgabe, wie bei Syncthing. Ein grosser Ordner, der
    /// sich selten aendert, vertraegt mehr; ein Laufwerk, dessen Meldungen
    /// man nicht traut, braucht weniger.
    ///
    /// Der tatsaechliche Abstand streut zwischen drei Vierteln und fuenf
    /// Vierteln davon, damit mehrere Freigaben nicht im Gleichschritt laufen.
    ///
    /// Ohne Beobachter gilt hoechstens eine Minute, gleich was hier steht:
    /// dann ist der Durchgang die einzige Quelle.
    /// </remarks>
    public int ScanIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Aenderungen im Ordner ueber das Dateisystem melden lassen.
    /// </summary>
    /// <remarks>
    /// Nicht jedes Laufwerk kann das. Netzlaufwerke, eingehaengte Freigaben
    /// und manche Treiber melden gar nichts oder nur einen Teil -- und man
    /// sieht es ihnen nicht an, denn der Beobachter laesst sich anlegen und
    /// schweigt dann.
    ///
    /// Abgeschaltet faellt das Programm auf den vollstaendigen Durchgang
    /// zurueck, und der laeuft dann wieder in kurzen Abstaenden. Das kostet
    /// Rechenzeit, ist aber vollstaendig.
    /// </remarks>
    public bool WatchChanges { get; set; } = true;

    /// <summary>
    /// Ob diese Freigabe im Navigationsbereich des Explorers steht.
    /// </summary>
    /// <remarks>
    /// Der Eintrag entsteht beim Anmelden der Sync-Wurzel und laesst sich
    /// dort nicht abbestellen; die Schnittstelle kennt keinen Schalter
    /// dafuer. Abgeschaltet wird er nachtraeglich, ueber dieselbe Eigenschaft,
    /// die auch OneDrive dafuer benutzt.
    ///
    /// Die Wurzel bleibt in jedem Fall bestehen. Platzhalter, Wolkensymbole
    /// und Kontextmenue arbeiten weiter -- der Ordner steht nur nicht mehr
    /// neben "Dieser PC".
    /// </remarks>
    public bool ShowInExplorer { get; set; } = true;

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Label) ? FolderId : $"{Label} ({FolderId})";

    /// <param name="isDirectory">
    /// Ob der Name ein Verzeichnis meint. Fuer "Dateien in X" ist das der
    /// Unterschied zwischen dazugehoeren und nicht: ein Unterverzeichnis
    /// liegt genauso unmittelbar in X wie eine Datei, gehoert aber nicht zu
    /// dessen losen Dateien.
    /// </param>
    public bool Includes(string relativePath, bool isDirectory = false)
    {
        if (Included.Count == 0) return true;

        foreach (var prefix in Included)
        {
            // "Dateien in X" -- gemeint ist, was unmittelbar in X liegt, ohne
            // die Unterverzeichnisse. Ohne diese Form liesse sich ein Ordner,
            // von dem ein Unterordner abgewaehlt ist, nur ganz oder gar nicht
            // beschreiben, und seine losen Dateien fielen stillschweigend
            // heraus.
            if (prefix == "*" || prefix.EndsWith("/*", StringComparison.Ordinal))
            {
                var ordner = prefix == "*" ? "" : prefix[..^2];

                if (!isDirectory
                    && ParentOf(relativePath).Equals(ordner, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Der Ordner selbst und alles darueber muss sichtbar bleiben,
                // sonst ist das Ausgewaehlte im Explorer nicht erreichbar.
                if (ordner.Equals(relativePath, StringComparison.OrdinalIgnoreCase)) return true;
                if (ordner.StartsWith(relativePath + "/", StringComparison.OrdinalIgnoreCase)) return true;

                continue;
            }

            if (relativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            if (relativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return true;
            // Elternverzeichnisse einer Auswahl muessen sichtbar bleiben.
            // Sonst ist der ausgewaehlte Zweig im Explorer nicht erreichbar.
            if (prefix.StartsWith(relativePath + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Das Verzeichnis, in dem dieser Name liegt. Leer fuer die Wurzel.</summary>
    private static string ParentOf(string relativePath)
    {
        var schnitt = relativePath.LastIndexOf('/');
        return schnitt < 0 ? "" : relativePath[..schnitt];
    }
}

/// <summary>Die Grenzen eines Datentraegers, wie sie in der Datei stehen.</summary>
/// <remarks>
/// Es gibt hier bewusst keinen Eintrag fuer jedes Laufwerk des Rechners,
/// sondern nur fuer die, an denen jemand etwas geaendert hat. Ein Laufwerk
/// kann verschwinden -- ein Wechseldatentraeger, ein getrenntes Netzlaufwerk
/// --, und ein Eintrag dafuer soll niemanden stoeren, wenn es wiederkommt.
/// </remarks>
public sealed class VolumeLimitConfig
{
    /// <summary>Die Laufwerkswurzel, etwa <c>C:\</c>.</summary>
    public string Root { get; set; } = "";

    /// <summary>Hoechstens so viel darf hier belegt sein. 0 = kein Limit.</summary>
    public long MaxBytes { get; set; }

    /// <summary>So viel soll hier frei bleiben. 0 = unbeachtet.</summary>
    public long MinimumFreeBytes { get; set; }
}

public sealed class AppConfig
{
    /// <summary>
    /// Wie sich dieses Geraet den Gegenstellen nennt.
    /// </summary>
    /// <remarks>
    /// Der Name steht im Hello und ist das erste, was eine Gegenstelle von
    /// uns sieht -- in Syncthings Oberflaeche steht er neben der Kennung.
    /// Vorgabe ist der Rechnername, denn den kennt der Benutzer bereits, und
    /// eine erfundene Bezeichnung waere auf der Gegenseite nur ein Raetsel.
    /// </remarks>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Bei wie vielen anderen Knoten eine Datei vollstaendig vorliegen muss,
    /// bevor sie hier zum Platzhalter werden darf.
    /// </summary>
    /// <remarks>
    /// Programmweit und nicht je Freigabe: es ist eine Sicherheitsvorgabe,
    /// keine Eigenart eines einzelnen Ordners. Wer sie fuer eine Freigabe
    /// lockern koennte, wuerde sie irgendwann fuer die falsche lockern.
    ///
    /// Mindestens 1. Bei 0 gaebe es keine Zusicherung mehr, dass eine
    /// freigegebene Datei ueberhaupt noch irgendwo liegt.
    /// </remarks>
    public int MinimumCopies { get; set; } = 1;

    /// <summary>Vorschaubilder aus den Dateikoepfen gewinnen.</summary>
    /// <remarks>
    /// Ebenfalls programmweit. Die Erweiterung, die sie an die Shell
    /// liefert, wird einmal angemeldet und gilt fuer alle Freigaben; sie je
    /// Freigabe an- und abzuschalten haette also gar keine Entsprechung.
    /// </remarks>
    public bool GenerateThumbnails { get; set; } = true;

    /// <summary>
    /// Der Abgleich ist angehalten.
    /// </summary>
    /// <remarks>
    /// Ein Modus und keine einmalige Handlung: er gilt auch fuer Freigaben,
    /// die erst spaeter bereit werden, und er ueberlebt einen Neustart. Wer
    /// anhaelt, weil die Verbindung gebraucht wird, will nicht, dass das naechste
    /// Hochfahren die Entscheidung stillschweigend zuruecknimmt.
    ///
    /// Sichtbar bleibt er trotzdem: das Zeichen im Infobereich steht dann auf
    /// orange, und die Schaltflaeche heisst "Fortsetzen".
    /// </remarks>
    public bool Paused { get; set; }

    /// <summary>
    /// Welche Freigaben die Uebersicht zeigt.
    /// </summary>
    /// <remarks>
    /// Eine Ansicht und keine Handlung: gefiltert wird, was zu sehen ist,
    /// nicht, was laeuft. Sie ueberlebt den Neustart, weil sie sonst bei
    /// jedem Start zurueckspraenge -- und wer auf die nicht verbundenen sieht,
    /// tut das meist ueber mehrere Sitzungen hinweg.
    /// </remarks>
    public ShareFilter Filter { get; set; } = ShareFilter.Alle;

    /// <summary>Verzeichnis fuer Geraetezertifikat, Index-Datenbanken und Cache-Zustand.</summary>
    public string HomeDirectory { get; set; } = "synct-home";

    /// <summary>
    /// Wo neu uebernommene Ordner im Explorer erscheinen. Ist der Wert leer,
    /// wird "SyncT" im Benutzerprofil verwendet.
    /// </summary>
    public string SharesRoot { get; set; } = "";

    /// <summary>
    /// Die Vorgabe fuer Datentraeger, fuer die noch nichts eingestellt wurde.
    /// </summary>
    /// <remarks>
    /// Der Cache hat kein eigenes Verzeichnis. Zwischengespeichert ist eine
    /// Datei, die an ihrem Platz unter <see cref="SharesRoot"/> liegt und ihre
    /// Bytes lokal vorhaelt. Begrenzt wird deshalb die Summe dieser Dateien,
    /// nicht ein Verzeichnis.
    ///
    /// 0 wuerde "unbegrenzt" bedeuten: es wird nie Speicherplatz freigegeben, und jede
    /// einmal geoeffnete Datei bleibt lokal erhalten. Nach einigen Monaten waere
    /// der Bestand eine Vollkopie, und es wuerde nichts mehr on-demand
    /// geholt. Die Oberflaeche laesst darum nur ganze Gigabyte ab 1 zu.
    /// </remarks>
    public long CacheMaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Die Vorgabe dafuer, wie viel auf einem Datentraeger frei bleiben soll.
    /// 0 schaltet diese Grenze ab.
    /// </summary>
    /// <remarks>
    /// Die zweite Grenze neben <see cref="CacheMaxBytes"/>. Es greift die
    /// Grenze, die zuerst erreicht wird.
    /// </remarks>
    public long MinimumFreeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Was auf einzelnen Datentraegern abweichend gilt.</summary>
    public List<VolumeLimitConfig> VolumeLimits { get; set; } = [];

    /// <summary>Die Grenzen einer Laufwerkswurzel, sonst die Vorgabewerte.</summary>
    public VolumeLimits LimitsFor(string root)
    {
        var eigen = VolumeLimits.FirstOrDefault(
            v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

        return eigen is null
            ? DefaultLimitsFor(root)
            : new VolumeLimits(eigen.MaxBytes, eigen.MinimumFreeBytes);
    }

    /// <summary>
    /// Womit ein Datentraeger anfaengt, auf dem zum ersten Mal eine Freigabe
    /// entsteht.
    /// </summary>
    /// <remarks>
    /// Der freizuhaltende Platz ist ein Anteil und keine feste Zahl. Zehn
    /// Gigabyte sind auf einer 8-TB-Platte nichts und auf einem kleinen
    /// Datentraeger die halbe Miete; zehn Prozent passen auf beiden.
    ///
    /// Gerechnet wird einmal, beim ersten Mal. Danach steht die Zahl in der
    /// Datei und aendert sich nicht mehr von selbst -- ein Wert, der beim
    /// naechsten Start ein anderer waere, liesse sich nicht einstellen.
    /// </remarks>
    public VolumeLimits DefaultLimitsFor(string root)
    {
        long gesamt = 0;
        try { gesamt = new DriveInfo(root).TotalSize; } catch (Exception) { }

        return new VolumeLimits(CacheMaxBytes, gesamt > 0 ? gesamt / 10 : MinimumFreeBytes);
    }

    /// <summary>
    /// Sorgt dafuer, dass der Datentraeger dieses Pfades eigene Werte hat.
    /// </summary>
    /// <param name="seed">
    /// Womit angefangen wird, wenn es noch keinen Eintrag gibt. Ohne Angabe
    /// die Vorgabewerte.
    /// </param>
    public void EnsureLimits(string path, VolumeLimits? seed = null)
    {
        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch (Exception) { return; }

        if (root.Length == 0) return;
        if (VolumeLimits.Any(v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase))) return;

        seed ??= DefaultLimitsFor(root);
        SetLimits(root, seed.MaxBytes, seed.MinimumFreeBytes);
    }

    /// <summary>Legt die Grenzen eines Datentraegers fest.</summary>
    public void SetLimits(string root, long maxBytes, long minimumFreeBytes)
    {
        var eigen = VolumeLimits.FirstOrDefault(
            v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

        if (eigen is null)
            VolumeLimits.Add(eigen = new VolumeLimitConfig { Root = root });

        eigen.MaxBytes = maxBytes;
        eigen.MinimumFreeBytes = minimumFreeBytes;
    }

    public List<PeerConfig> Peers { get; set; } = [];

    public List<ShareConfig> Shares { get; set; } = [];

    /// <summary>
    /// Farbschema der Oberflaeche: "Hell", "Dunkel" oder leer fuer die
    /// Einstellung von Windows.
    /// </summary>
    public string Theme { get; set; } = "";

    /// <summary>
    /// Sprache der Oberflaeche: "de", "en" oder leer fuer die Sprache des
    /// Systems.
    /// </summary>
    public string Language { get; set; } = "";

    /// <summary>
    /// Beim Start kein Fenster aufziehen.
    /// </summary>
    /// <remarks>
    /// Zusammen mit <see cref="CloseToTray"/> ergibt das: kein Fenster, nur
    /// das Symbol im Infobereich. Ohne dieses Symbol bleibt es beim
    /// minimierten Fenster in der Taskleiste, sonst liefe das Programm ohne
    /// jede Bedienmoeglichkeit.
    ///
    /// Ob Windows das Programm ueberhaupt startet, steht nicht hier. Dieser
    /// Eintrag liegt in der Registry, wo Windows ihn liest.
    /// </remarks>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Das X versteckt das Fenster, statt das Programm zu beenden.
    /// </summary>
    /// <remarks>
    /// Die Oberflaeche ist zugleich der Sync-Dienst. Wer sie schliesst, meint
    /// meist das Fenster und nicht den Abgleich. Beendet wird das Programm
    /// dann ueber das Kontextmenue des Symbols im Infobereich.
    /// </remarks>
    public bool CloseToTray { get; set; }

    /// <summary>Parallele Block-Requests je Hydration.</summary>
    public int Parallelism { get; set; } = 8;

    /// <summary>
    /// Anrufe anderer Geraete annehmen.
    /// </summary>
    /// <remarks>
    /// Ohne eingehende Verbindungen kann der Aufbau nur von diesem Geraet
    /// ausgehen. Eine Gegenstelle, deren Adresse wir nicht kennen, bleibt
    /// dann unerreichbar.
    /// </remarks>
    public bool Listen { get; set; } = true;

    /// <summary>Port fuer eingehende Verbindungen. Syncthings Vorgabe ist 22000.</summary>
    public int ListenPort { get; set; } = 22000;

    /// <summary>
    /// Gegenstellen ohne feste Adresse beim Erkennungsserver suchen.
    /// </summary>

    /// <summary>
    /// Im eigenen Netz Ankuendigungen senden und empfangen.
    /// </summary>
    /// <remarks>
    /// Beides gehoert zusammen. Wer nur empfaengt, findet andere Geraete,
    /// wird aber selbst nicht gefunden.
    /// </remarks>
    public bool LocalDiscovery { get; set; } = true;

    /// <summary>
    /// Dem Erkennungsserver melden, wo dieses Geraet erreichbar ist.
    /// </summary>
    /// <remarks>
    /// Erst dadurch kann eine Gegenstelle, bei der als Adresse "dynamic"
    /// steht, eine Verbindung zu uns aufbauen. Der Server erfaehrt dabei
    /// unsere IP-Adresse.
    /// </remarks>
    public bool Announce { get; set; } = true;

    /// <summary>
    /// Die im eigenen Netz gefundenen Geraete. Kein Teil der Konfiguration.
    /// Die Oberflaeche traegt das Objekt hier ein, damit die Gegenstellen
    /// darauf zugreifen koennen.
    /// </summary>
    [JsonIgnore]
    public SyncTClient.Bep.LocalDiscovery? Local { get; set; }
    /// <remarks>
    /// Der Server erfaehrt dabei, nach welchem Geraet gefragt wird, und
    /// ausserdem unsere IP-Adresse. Wer das nicht will, traegt Adressen von
    /// Hand ein und schaltet diese Option ab.
    /// </remarks>
    public bool Discovery { get; set; } = true;

    /// <summary>
    /// Relays ueberhaupt verwenden.
    /// </summary>
    /// <remarks>
    /// Wirkt mit <see cref="PeerConfig.Relays"/> als Und-Verknuepfung. Hier
    /// steht der Hauptschalter, dort die Entscheidung je Gegenstelle.
    /// </remarks>
    public bool Relays { get; set; } = true;

    /// <summary>
    /// Der Erkennungsserver. Das <c>id=</c> darin ist seine Geraete-ID. Daran
    /// wird er erkannt, denn sein Zertifikat ist selbstsigniert.
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
    /// Aeltere Versionen kannten genau einen Server. Beim Laden wird er
    /// uebernommen, sofern er nicht ohnehin der alte Vorgabewert war.
    /// </summary>
    public string? DiscoveryServer { get; set; }

    /// <summary>Die Server, bei denen sich Adressen abfragen lassen.</summary>
    [JsonIgnore]
    public IEnumerable<string> LookupServers => DiscoveryServers.Where(SyncTClient.Bep.GlobalDiscovery.AllowsLookup);

    /// <summary>Die Server, bei denen sich dieses Geraet anmelden kann.</summary>
    [JsonIgnore]
    public IEnumerable<string> AnnounceServers => DiscoveryServers.Where(SyncTClient.Bep.GlobalDiscovery.AllowsAnnounce);

    /// <summary>
    /// Aeltere Versionen kannten genau eine Gegenstelle. Beim Laden wird sie in
    /// die Liste ueberfuehrt, damit bestehende Konfigurationen weiterlaufen.
    /// </summary>
    public PeerConfig? Peer { get; set; }

    private CacheLimits? _limits;

    /// <summary>Die Ueberwachung des belegten Platzes.</summary>
    /// <remarks>
    /// Das Limit bekommt die Grenzen nicht mitgegeben, sondern fragt sie hier
    /// nach. So wirkt eine Aenderung in den Einstellungen sofort und nicht
    /// erst beim naechsten Start.
    /// </remarks>
    [JsonIgnore]
    public CacheLimits Cache => _limits ??= new CacheLimits(LimitsFor);

    [JsonIgnore]
    public string SharesRootOrDefault => string.IsNullOrWhiteSpace(SharesRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT")
        : SharesRoot;

    /// <summary>
    /// Wo die Vorschaubilder liegen. Sie liegen im Cache-Verzeichnis, aber
    /// neben den Freigaben statt darin.
    /// </summary>
    /// <remarks>
    /// Sie gehoeren zum Cache. Sie entstehen aus fremden Dateien und lassen
    /// sich jederzeit neu erzeugen. Innerhalb einer Freigabe waeren sie ein
    /// Sync-Root im Sync-Root, und Windows wuerde versuchen, sie zu
    /// projizieren.
    /// </remarks>
    [JsonIgnore]
    public string ThumbnailDirectory => Path.Combine(SharesRootOrDefault, ".preview");

    public PeerConfig? PeerFor(ShareConfig share)
        => Peers.FirstOrDefault(p => p.DeviceId == share.PeerDeviceId) ?? Peers.FirstOrDefault();

    /// <summary>
    /// Die Ordner, an denen diese Gegenstelle teilnimmt.
    /// </summary>
    /// <remarks>
    /// Massgeblich ist die Liste. Der einzelne Eintrag steht daneben, solange
    /// aeltere Konfigurationen ihn tragen; er wird beim Laden in die Liste
    /// gehoben.
    /// </remarks>
    public IEnumerable<ShareConfig> SharesOf(PeerConfig peer)
        => Shares.Where(s => s.PeerDeviceIds.Contains(peer.DeviceId, StringComparer.OrdinalIgnoreCase)
                          || s.PeerDeviceId == peer.DeviceId);

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

    /// <summary>
    /// Macht aus einem relativen Datenverzeichnis einen vollstaendigen Pfad.
    /// Bezugspunkt ist die Konfigurationsdatei.
    /// </summary>
    /// <remarks>
    /// Ein relativer Pfad in einer Datei ist relativ zu dieser Datei. Wird er
    /// gegen das Arbeitsverzeichnis gerechnet, findet dieselbe Konfiguration
    /// je nach Startverzeichnis ein anderes Datenverzeichnis und damit einen
    /// anderen Index und ein anderes Geraetezertifikat.
    ///
    /// Die Oberflaeche loest den Pfad fuer ihre Laufzeitversion selbst auf und
    /// laesst den Eintrag in der Datei relativ, weil sie die Datei
    /// zurueckschreibt. Die Konsole schreibt nicht zurueck und darf den
    /// Eintrag deshalb hier ersetzen.
    /// </remarks>
    /// <summary>
    /// Wo die Konfiguration liegt, wenn niemand etwas anderes sagt.
    /// </summary>
    /// <remarks>
    /// Zwei Faelle, in dieser Reihenfolge:
    ///
    /// Liegt eine <c>synct.json</c> beim Programm oder darueber, gilt sie.
    /// Das ist der tragbare Fall -- Entwicklungsbaum, USB-Stick, ein Ordner,
    /// den man mitnimmt.
    ///
    /// Sonst <c>%LOCALAPPDATA%\SyncTClient</c>. Unter <c>C:\Program Files</c>
    /// darf ein gewoehnlicher Benutzer nicht schreiben; eine installierte
    /// Version koennte dort weder Zertifikat noch Index anlegen.
    ///
    /// Bewusst <em>Local</em> und nicht das wandernde <c>%APPDATA%</c>: das
    /// Geraetezertifikat ist die Kennung genau dieses Rechners. Wanderte es
    /// mit, traeten zwei Rechner im Verbund unter derselben Kennung auf. Und
    /// Index und Vorschaubilder gehen in die hunderte Megabyte -- die will
    /// niemand bei jeder Anmeldung ueber das Netz schieben.
    /// </remarks>
    public static string DefaultConfigPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "synct.json");
            if (File.Exists(candidate)) return candidate;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncTClient", "synct.json");
    }

    public void ResolveAgainst(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (string.IsNullOrEmpty(directory)) return;

        HomeDirectory = Path.GetFullPath(Path.Combine(directory, HomeDirectory));
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
            // Der alte Vorgabewert erlaubte nur Abfragen, keine Anmeldung. Er
            // ist in der neuen Liste bereits enthalten.
            if (!DiscoveryServers.Contains(single) && !single.Contains("noannounce"))
                DiscoveryServers.Insert(0, single);

            DiscoveryServer = null;
        }

        // 0 hiess: ohne Pruefung freigeben. Damit konnte der letzte Inhalt
        // einer Datei im Netz verschwinden. Der Wert wird angehoben.
        foreach (var share in Shares.Where(s => s.MinimumCopies < 1))
            share.MinimumCopies = 1;

        // Frueher galten die beiden Zahlen fuer jeden Datentraeger gleich.
        // Bestehende Einstellungen bleiben damit erhalten; erst Laufwerke, die
        // spaeter dazukommen, fangen mit den Vorgabewerten an.
        var alt = new VolumeLimits(CacheMaxBytes, MinimumFreeBytes);
        foreach (var share in Shares.Where(s => !string.IsNullOrWhiteSpace(s.LocalPath)))
            EnsureLimits(share.LocalPath, alt);

        var fallback = Peers.FirstOrDefault()?.DeviceId ?? "";
        foreach (var share in Shares)
        {
            if (string.IsNullOrEmpty(share.PeerDeviceId))
                share.PeerDeviceId = fallback;

            // Aeltere Dateien kennen nur die eine Gegenstelle. Sie ist der
            // erste Eintrag der Liste.
            if (share.PeerDeviceIds.Count == 0 && !string.IsNullOrEmpty(share.PeerDeviceId))
                share.PeerDeviceIds.Add(share.PeerDeviceId);
        }
    }

    /// <summary>
    /// Schreibt die Konfiguration -- ganz oder gar nicht, mit Sicherungen.
    /// </summary>
    /// <remarks>
    /// Hier stand ein WriteAllText. Das schreibt in die vorhandene Datei
    /// hinein: geht dabei etwas schief -- ein Absturz, eine volle Platte,
    /// ein Stromausfall --, steht dort eine halbe Datei, und mit ihr sind
    /// saemtliche Freigaben fort. Die Ordner auf der Platte blieben, aber
    /// niemand wuesste mehr, zu wem sie gehoeren.
    ///
    /// Deshalb erst daneben schreiben und dann tauschen. File.Replace macht
    /// beides in einem Zug und legt die bisherige Fassung als ".bak"
    /// beiseite; ein Abbruch unterwegs laesst die alte Datei unberuehrt.
    ///
    /// Dazu eine Sicherung je Tag, die zehn juengsten bleiben. Der ".bak"
    /// hilft gegen einen missglueckten Schreibvorgang, nicht gegen einen
    /// Fehler, der erst drei Tage spaeter auffaellt.
    /// </remarks>
    public void Save(string path)
    {
        var text = JsonSerializer.Serialize(this, Format);
        var voll = Path.GetFullPath(path);

        if (!File.Exists(voll))
        {
            File.WriteAllText(voll, text);
            return;
        }

        var neben = voll + ".neu";
        File.WriteAllText(neben, text);

        // Der dritte Parameter ist die Sicherung der bisherigen Fassung.
        // Ohne ihn waere der Tausch zwar auch atomar, aber die alte Datei
        // waere fort -- und genau sie ist im Zweifel die richtige.
        File.Replace(neben, voll, voll + ".bak", ignoreMetadataErrors: true);

        Sichern(voll, text);
    }

    /// <summary>So viele Tagessicherungen bleiben liegen.</summary>
    private const int Sicherungen = 10;

    /// <summary>
    /// Legt das Geraetezertifikat mit zur Sicherung.
    /// </summary>
    /// <remarks>
    /// Ohne dieses Zertifikat ist dieser Rechner ein anderer. Die Geraete-ID
    /// wird daraus abgeleitet; geht es verloren, bekommt er eine neue, und
    /// jede Gegenstelle muss sie erst wieder freigeben. Die Konfiguration
    /// allein zurueckzuspielen brauchte danach niemand -- sie nennt Ordner,
    /// die uns nicht mehr gehoeren.
    ///
    /// Die Datei enthaelt den privaten Schluessel. Sie gehoert damit an
    /// dieselbe Stelle wie die Konfiguration und an keine, die weitergegeben
    /// wird.
    /// </remarks>
    private void SichereKennung(string voll, string ordner, string name)
    {
        try
        {
            var heim = Path.Combine(Path.GetDirectoryName(voll)!, HomeDirectory);
            var pfx = Path.Combine(heim, "device.pfx");
            if (!File.Exists(pfx)) return;

            File.Copy(pfx, Path.Combine(ordner, $"{name}-{DateTime.Now:yyyy-MM-dd}-device.pfx"), overwrite: true);
        }
        catch (Exception)
        {
            // Die Konfiguration ist gesichert; das ist der wichtigere Teil.
        }
    }

    /// <summary>
    /// Legt hoechstens eine Sicherung je Tag ab und raeumt die alten weg.
    /// </summary>
    /// <remarks>
    /// Je Tag und nicht je Speichern: wer an einem Nachmittag zwanzig Mal
    /// etwas umstellt, braucht keine zwanzig Staende, sondern den von
    /// gestern.
    /// </remarks>
    private void Sichern(string voll, string text)
    {
        try
        {
            var ordner = Path.Combine(Path.GetDirectoryName(voll)!, "synct-sicherungen");
            Directory.CreateDirectory(ordner);

            var name = Path.GetFileNameWithoutExtension(voll);
            var heute = Path.Combine(ordner, $"{name}-{DateTime.Now:yyyy-MM-dd}.json");

            if (File.Exists(heute)) return;

            File.WriteAllText(heute, text);
            SichereKennung(voll, ordner, name);

            // Beide Sorten getrennt zaehlen: sonst raeumte eine Sorte die
            // andere weg, sobald von einer mehr da sind.
            foreach (var muster in new[] { $"{name}-*.json", $"{name}-*-device.pfx" })
            {
                foreach (var alt in Directory.GetFiles(ordner, muster)
                             .OrderByDescending(f => f, StringComparer.Ordinal)
                             .Skip(Sicherungen))
                {
                    try { File.Delete(alt); } catch (Exception) { /* beim naechsten Mal */ }
                }
            }
        }
        catch (Exception)
        {
            // Eine Sicherung, die nicht gelingt, darf das Speichern nicht
            // aufhalten. Die Konfiguration steht zu diesem Zeitpunkt schon.
        }
    }

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
