using System.Security.Cryptography;
using System.Text;

namespace SyncTClient.Mount;

/// <summary>
/// Der lokale Vorrat an Vorschaubildern.
/// </summary>
/// <remarks>
/// Windows ruft fuer dehydrierte Platzhalter absichtlich keinen
/// Vorschau-Erzeuger auf. Damit soll verhindert werden, dass das
/// Durchblaettern eines Ordners alle Dateien herunterlaedt. Aus demselben
/// Grund kommen wir aber auch nicht dazu, das eingebettete Vorschaubild
/// anzubieten.
///
/// Deshalb wird es vorher angelegt: der Client holt im Hintergrund den
/// Dateikopf, einen einzigen Block von 128 KiB, und legt die darin enthaltene
/// EXIF-Vorschau hier ab. Die Shell-Erweiterung liest spaeter nur noch aus
/// diesem Vorrat und kommt ohne Netz und ohne Wartezeit aus.
/// </remarks>
public sealed class ThumbnailStore(string directory)
{
    public string Directory { get; } = Path.GetFullPath(directory);

    /// <summary>
    /// Wo die Vorschau einer Datei liegt. Der Schluessel ist der absolute
    /// lokale Pfad, weil die Shell-Erweiterung spaeter nur diesen kennt.
    /// </summary>
    /// <remarks>
    /// Der Dateiname ist der Hash dieses Pfades. Das umgeht
    /// Laengenbegrenzungen und Sonderzeichen und streut die Dateien ueber
    /// Unterverzeichnisse, damit keines zu gross wird.
    /// </remarks>
    public string PathFor(string localFilePath) => PathFor(Directory, localFilePath);

    /// <summary>Dieselbe Zuordnung, wie sie die Shell-Erweiterung nachbildet.</summary>
    public static string PathFor(string directory, string localFilePath)
    {
        var normalized = Path.GetFullPath(localFilePath).ToLowerInvariant();
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(directory, name[..2], name[2..] + ".jpg");
    }

    public bool Has(string localFilePath) => File.Exists(PathFor(localFilePath));

    /// <summary>
    /// Wo vermerkt ist, dass eine Datei keine eingebettete Vorschau hat.
    /// </summary>
    /// <remarks>
    /// Ohne diesen Vermerk wuerde der Kopf jeder vorschaulosen Datei immer
    /// wieder geholt. Der Explorer fragt bei jedem Blick in den Ordner
    /// erneut, und ein Fehlschlag ist fuer ihn nicht von "noch nicht
    /// vorhanden" zu unterscheiden.
    /// </remarks>
    private static string MarkerFor(string directory, string localFilePath)
        => Path.ChangeExtension(PathFor(directory, localFilePath), ".leer");

    public bool KnownWithout(string localFilePath) => File.Exists(MarkerFor(Directory, localFilePath));

    /// <summary>
    /// Wo die Ausrichtung des Bildes vermerkt ist.
    /// </summary>
    /// <remarks>
    /// Als Nebendatei, wie der Vermerk fuer "kein eingebettetes Bild". Das
    /// eingebettete Bild selbst traegt die Ausrichtung nicht -- sie steht im
    /// Kopf der Hauptdatei, und der liegt beim Anzeigen nicht mehr vor.
    ///
    /// Angelegt wird sie nur, wenn etwas zu drehen ist. Eine Datei je
    /// aufrechtem Bild waere Aufwand fuer die Auskunft "nichts zu tun".
    /// </remarks>
    public static string AusrichtungFor(string directory, string localFilePath)
        => Path.ChangeExtension(PathFor(directory, localFilePath), ".dreh");

    public void MarkWithout(string localFilePath)
    {
        var path = MarkerFor(Directory, localFilePath);
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }
        catch (IOException) { /* dann wird spaeter erneut gefragt */ }
    }

    public void Save(string localFilePath, byte[] jpeg, int ausrichtung = 0)
    {
        if (ausrichtung is >= 2 and <= 8)
        {
            try
            {
                var dreh = AusrichtungFor(Directory, localFilePath);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dreh)!);
                File.WriteAllText(dreh, ausrichtung.ToString());
            }
            catch (IOException) { /* dann eben aufrecht */ }
        }

        var path = PathFor(localFilePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Erst daneben schreiben, dann umbenennen. Sonst koennte die
        // Shell-Erweiterung eine halb geschriebene Datei lesen.
        var temporary = path + ".neu";
        File.WriteAllBytes(temporary, jpeg);
        File.Move(temporary, path, overwrite: true);
    }

    public void Remove(string localFilePath)
    {
        // Auch den Vermerk loeschen. Hat sich die Datei geaendert, kann sie
        // diesmal eine Vorschau enthalten.
        foreach (var path in new[] { PathFor(localFilePath), MarkerFor(Directory, localFilePath) })
        {
            try { File.Delete(path); }
            catch (IOException) { /* beim naechsten Mal */ }
        }
    }

    /// <summary>Legt das Verzeichnis an und markiert es als versteckt.</summary>
    /// <remarks>
    /// Der Vorrat liegt neben den Freigaben. Er soll dort nicht sichtbar sein
    /// und nicht bearbeitet werden.
    /// </remarks>
    public void Prepare()
    {
        try
        {
            var info = new DirectoryInfo(Directory);
            if (info.Exists) return;

            info.Create();
            info.Attributes |= FileAttributes.Hidden;
        }
        catch (IOException) { /* dann bleibt es sichtbar */ }
        catch (UnauthorizedAccessException) { /* dito */ }
    }

    /// <summary>
    /// Loescht den ganzen Vorrat. Was gebraucht wird, entsteht neu. Die
    /// Vorlage dazu liegt bei der Gegenstelle.
    /// </summary>
    public (int Count, long Bytes) Clear()
    {
        // Der gezaehlte Stand gilt nicht mehr. Ohne dies zeigte die Anzeige
        // bis zur naechsten Zaehlung weiter die alte Menge.
        _bestand = (0, 0);
        _gezaehlt = DateTime.MinValue;

        if (!System.IO.Directory.Exists(Directory)) return (0, 0);

        var count = 0;
        long bytes = 0;

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
        {
            long size = 0;
            try { size = new FileInfo(file).Length; } catch { /* egal */ }

            // Die Shell-Erweiterung liest womoeglich gerade eines davon.
            try { File.Delete(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) count++;
            bytes += size;
        }

        return (count, bytes);
    }

    private (int Count, long Bytes) _bestand;
    private DateTime _gezaehlt = DateTime.MinValue;
    private int _zaehltGerade;

    /// <summary>
    /// Wie viele Vorschaubilder liegen hier und wie gross sind sie zusammen?
    /// </summary>
    /// <remarks>
    /// Die zuletzt gezaehlte Menge, nicht die gerade vorliegende. Gezaehlt
    /// wird im Hintergrund und hoechstens alle paar Sekunden.
    ///
    /// Der Aufrufer ist eine Eigenschaft in der Tabelle, und die wird bei
    /// jedem Takt gelesen, mehrfach je Zeile. Ein Durchgang ueber das
    /// Verzeichnis an dieser Stelle heisst: Plattenzugriffe auf dem Faden,
    /// der das Fenster zeichnet, dutzendfach je Sekunde. Solange sonst nichts
    /// los ist, faellt das nicht auf; waehrend drei Freigaben ihren Index
    /// schreiben, steht das Fenster.
    /// </remarks>
    public (int Count, long Bytes) Usage()
    {
        if (DateTime.UtcNow - _gezaehlt > TimeSpan.FromSeconds(10)
            && Interlocked.Exchange(ref _zaehltGerade, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try { _bestand = Zaehlen(); }
                catch (Exception) { /* die alte Zahl bleibt stehen */ }
                finally
                {
                    _gezaehlt = DateTime.UtcNow;
                    Interlocked.Exchange(ref _zaehltGerade, 0);
                }
            });
        }

        return _bestand;
    }

    private (int Count, long Bytes) Zaehlen()
    {
        if (!System.IO.Directory.Exists(Directory)) return (0, 0);

        var count = 0;
        long bytes = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.jpg", SearchOption.AllDirectories))
        {
            count++;
            try { bytes += new FileInfo(file).Length; } catch { /* egal */ }
        }
        return (count, bytes);
    }
}
