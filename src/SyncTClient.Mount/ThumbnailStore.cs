using System.Security.Cryptography;
using System.Text;

namespace SyncTClient.Mount;

/// <summary>
/// Der lokale Vorrat an Vorschaubildern.
/// </summary>
/// <remarks>
/// Windows ruft fuer dehydrierte Platzhalter absichtlich keinen
/// Vorschau-Erzeuger auf -- es will verhindern, was wir auch nicht wollen:
/// dass das Durchblaettern eines Ordners alles herunterlaedt. Damit kommen
/// wir aber auch nicht dazu, das eingebettete Vorschaubild anzubieten.
///
/// Also legen wir es vorher an: der Client holt im Hintergrund den Dateikopf
/// -- einen einzigen Block von 128 KiB -- und legt die darin enthaltene
/// EXIF-Vorschau hier ab. Die Shell-Erweiterung liest spaeter nur noch aus
/// diesem Vorrat und kommt ohne Netz und ohne Wartezeit aus.
/// </remarks>
public sealed class ThumbnailStore(string directory)
{
    public string Directory { get; } = Path.GetFullPath(directory);

    /// <summary>
    /// Wo die Vorschau einer Datei liegt -- geschluesselt ueber den absoluten
    /// lokalen Pfad, weil die Shell-Erweiterung spaeter nur den kennt.
    /// </summary>
    /// <remarks>
    /// Der Dateiname ist der Hash dieses Pfades: das umgeht
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
    /// Ohne diesen Vermerk holten wir den Kopf jeder vorschaulosen Datei
    /// wieder und wieder: der Explorer fragt bei jedem Blick in den Ordner
    /// erneut, und ein Fehlschlag sieht fuer ihn aus wie "noch nicht da".
    /// </remarks>
    private static string MarkerFor(string directory, string localFilePath)
        => Path.ChangeExtension(PathFor(directory, localFilePath), ".leer");

    public bool KnownWithout(string localFilePath) => File.Exists(MarkerFor(Directory, localFilePath));

    public void MarkWithout(string localFilePath)
    {
        var path = MarkerFor(Directory, localFilePath);
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }
        catch (IOException) { /* dann fragen wir eben noch einmal */ }
    }

    public void Save(string localFilePath, byte[] jpeg)
    {
        var path = PathFor(localFilePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Erst daneben schreiben, dann umbenennen: sonst koennte die
        // Shell-Erweiterung eine halb geschriebene Datei zu sehen bekommen.
        var temporary = path + ".neu";
        File.WriteAllBytes(temporary, jpeg);
        File.Move(temporary, path, overwrite: true);
    }

    public void Remove(string localFilePath)
    {
        // Auch den Vermerk loeschen: hat sich die Datei geaendert, kann
        // diesmal sehr wohl eine Vorschau darin stecken.
        foreach (var path in new[] { PathFor(localFilePath), MarkerFor(Directory, localFilePath) })
        {
            try { File.Delete(path); }
            catch (IOException) { /* beim naechsten Mal */ }
        }
    }

    public (int Count, long Bytes) Usage()
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
