using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;

namespace SyncTClient.ExplorerProvider;

// Diese DLL laedt der Explorer in seinen eigenen Prozess. Sie ist deshalb
// bewusst einfach gehalten: sie liest eine lokale Datei und gibt sie zurueck.
// Sie benutzt kein Netz, wartet auf nichts und ruft nicht in unser Programm
// zurueck. Ein Fehler an dieser Stelle beendet auch den Explorer.

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outerUnknown, in Guid interfaceId, out nint instance);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool @lock);
}

[GeneratedComInterface]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
internal partial interface IInitializeWithFile
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
}

[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid interfaceId, out nint result);
    [PreserveSig] int GetParent(out nint parent);
    [PreserveSig] int GetDisplayName(uint kind, out nint name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(nint other, uint hint, out int order);
}

[GeneratedComInterface]
[Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")]
internal partial interface IInitializeWithItem
{
    [PreserveSig] int Initialize(IShellItem item, uint mode);
}

[GeneratedComInterface]
[Guid("e357fccd-a995-4576-b01f-234630154e96")]
internal partial interface IThumbnailProvider
{
    [PreserveSig] int GetThumbnail(uint requestedSize, out nint bitmap, out int alphaType);
}

/// <summary>Liefert Explorer die vorbereitete Vorschau eines Platzhalters.</summary>
[GeneratedComClass]
internal sealed partial class SyncTThumbnailProvider : IInitializeWithFile, IInitializeWithItem, IThumbnailProvider
{
    private const int Ok = 0;
    private const int Fail = unchecked((int)0x80004005);

    /// <summary>WTS_E_FAILEDEXTRACTION. Der Explorer zeigt dann sein Ersatzsymbol.</summary>
    private const int NoThumbnail = unchecked((int)0x8004B200);

    /// <summary>WTSAT_RGB: undurchsichtig, da JPEG keine Transparenz kennt.</summary>
    private const int AlphaTypeRgb = 1;

    private string? _filePath;

    public int Initialize(string filePath, uint mode)
    {
        _filePath = filePath;
        Trace.Write($"InitializeWithFile: {filePath}");
        return Ok;
    }

    /// <summary>
    /// Der Weg, den Windows fuer Platzhalter bevorzugt: ein IShellItem
    /// impliziert keinen Lesezugriff, ein Dateipfad dagegen schon.
    /// </summary>
    public int Initialize(IShellItem item, uint mode)
    {
        const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH

        var hr = item.GetDisplayName(FileSystemPath, out var name);
        if (hr != 0 || name == 0)
        {
            Trace.Write($"InitializeWithItem: kein Pfad, 0x{(uint)hr:X8}");
            return hr == 0 ? Fail : hr;
        }

        try
        {
            _filePath = Marshal.PtrToStringUni(name);
            Trace.Write($"InitializeWithItem: {_filePath}");
            return Ok;
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    /// <summary>
    /// Laesst die Vorschau erzeugen, gleich in welchem Prozess dieser Code
    /// laeuft.
    /// </summary>
    /// <remarks>
    /// Die Shell hat zwei Wege zu dieser Klasse: die Klasse, die der laufende
    /// Client anmeldet, und die DLL hier, die sie im Wirt startet. Welchen sie
    /// waehlt, entscheidet sie selbst.
    ///
    /// Ueber den ersten Weg steht <see cref="Store.Produce"/> bereit und ruft
    /// den Erzeuger unmittelbar. Ueber den zweiten war bisher niemand
    /// zustaendig: der Vorrat wurde nachgesehen, und war er leer, blieb es
    /// beim Ersatzsymbol. Dieselbe Datei bekam damit eine Vorschau oder keine,
    /// je nachdem, welchen Weg die Shell genommen hatte.
    ///
    /// Ueber die Pipe fuehren beide Wege zum selben Erzeuger. Sie ist dafuer
    /// schon da; das Kontextmenue benutzt sie.
    /// </remarks>
    private static bool Beschaffen(string localFilePath)
    {
        if (Store.Produce is { } direkt) return direkt(localFilePath);

        var antwort = Sync.Send("THUMB", [localFilePath]);
        Trace.Write($"  Client gefragt: {(antwort == "1" ? "erzeugt" : antwort)}");
        return antwort == "1";
    }

    public int GetThumbnail(uint requestedSize, out nint bitmap, out int alphaType)
    {
        bitmap = 0;
        alphaType = AlphaTypeRgb;
        Host.KeepAlive();

        try
        {
            if (string.IsNullOrEmpty(_filePath)) return Fail;

            var cached = Store.PathFor(_filePath);
            if (cached is null)
            {
                Trace.Write("  kein Vorrat-Verzeichnis hinterlegt");
                return NoThumbnail;
            }

            if (!File.Exists(cached))
            {
                // Im Vorrat liegt nichts, also muss der Client sie beschaffen.
                if (!Beschaffen(_filePath) || !File.Exists(cached))
                {
                    Trace.Write($"  keine Vorschau unter {cached}");
                    return NoThumbnail;
                }

                Trace.Write("  auf Zuruf beschafft");
            }

            var drehung = Store.Ausrichtung(_filePath);
            var ok = Gdi.LoadAsBitmap(cached, requestedSize, drehung, out bitmap);
            Trace.Write($"  {requestedSize} px{(drehung > 1 ? $", Ausrichtung {drehung}" : "")} " +
                        $"-> {(ok ? "geliefert" : "GDI+ fehlgeschlagen")}");
            return ok ? Ok : NoThumbnail;
        }
        catch (Exception ex)
        {
            // Aus einer Shell-Erweiterung darf niemals eine Ausnahme nach aussen gelangen.
            Trace.Write("  Ausnahme: " + ex.Message);
            return NoThumbnail;
        }
    }
}


/// <summary>
/// Ein Protokoll fuer die Fehlersuche. Die Erweiterung laeuft in einem
/// fremden Prozess, in den man nicht hineinsehen kann. Ohne dieses Protokoll
/// bliebe unklar, ob Windows sie ueberhaupt aufruft.
/// </summary>
internal static class Trace
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synctexplorer.log");

    public static void Write(string line)
    {
        try
        {
            File.AppendAllText(Path,
                $"{DateTime.Now:HH:mm:ss.fff}  {Environment.ProcessId,6}  {line}{Environment.NewLine}");
        }
        catch { /* das Protokollieren darf den Ablauf nie stoeren */ }
    }
}

/// <summary>
/// Haelt den Wirtsprozess wach, solange Anfragen kommen. Im DLL-Fall gibt es
/// keinen eigenen Wirtsprozess, dann bleibt der Aufruf wirkungslos.
/// </summary>
internal static class Host
{
    public static Action? Alive { get; set; }
    public static void KeepAlive() => Alive?.Invoke();
}

/// <summary>Findet die vorbereitete Vorschau zu einem Dateipfad.</summary>
internal static class Store
{
    /// <summary>
    /// Beschafft eine fehlende Vorschau, falls jemand das kann.
    /// </summary>
    /// <remarks>
    /// Bleibt leer, solange die Erweiterung als DLL in einem fremden Prozess
    /// laeuft, denn dort gibt es keine Verbindung zur Gegenstelle. Laeuft sie
    /// dagegen im Client, traegt dieser hier ein, wie sich ein Dateikopf holen
    /// laesst. Damit muss nichts mehr auf Vorrat erzeugt werden.
    /// </remarks>
    public static Func<string, bool>? Produce { get; set; }

    private static string? _directory;
    private static bool _looked;

    /// <summary>
    /// Die vermerkte Ausrichtung, 1 bis 8. 0 heisst aufrecht.
    /// </summary>
    /// <remarks>
    /// Das eingebettete Bild traegt sie nicht -- sie steht im Kopf der
    /// Hauptdatei, und der liegt hier nicht vor. Der Client legt sie deshalb
    /// als Nebendatei ab, und nur dann, wenn etwas zu drehen ist.
    /// </remarks>
    public static int Ausrichtung(string localFilePath)
    {
        try
        {
            if (PathFor(localFilePath) is not { } bild) return 0;

            var dreh = System.IO.Path.ChangeExtension(bild, ".dreh");
            if (!File.Exists(dreh)) return 0;

            return int.TryParse(File.ReadAllText(dreh).Trim(), out var wert)
                   && wert is >= 1 and <= 8
                ? wert
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static string? PathFor(string localFilePath)
    {
        var directory = Directory();
        if (directory is null) return null;

        // Muss exakt zu ThumbnailStore.PathFor im Client passen.
        var normalized = Path.GetFullPath(localFilePath).ToLowerInvariant();
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(directory, name[..2], name[2..] + ".jpg");
    }

    /// <summary>
    /// Wo der Vorrat liegt, hinterlegt der Client bei der Registrierung.
    /// Einmal nachschlagen genuegt, denn der Explorer laedt die Erweiterung oft.
    /// </summary>
    private static string? Directory()
    {
        if (_looked) return _directory;
        _looked = true;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SyncTClient");
            _directory = key?.GetValue("ThumbnailStore") as string;
        }
        catch
        {
            _directory = null;
        }

        return _directory;
    }
}

/// <summary>
/// Wandelt ein JPEG von der Platte in ein HBITMAP. Das geschieht ueber die
/// flache GDI+-Schnittstelle, die ohne verwaltete Bildbibliothek auskommt.
/// </summary>
internal static partial class Gdi
{
    private static nint _token;
    private static bool _started;
    private static readonly Lock Gate = new();

    /// <summary>
    /// Rechnet die Exif-Ausrichtung in die Angabe von GDI+ um.
    /// </summary>
    /// <remarks>
    /// Exif zaehlt anders: 6 heisst "um 90 Grad im Uhrzeigersinn gedreht
    /// aufgenommen", also muss beim Anzeigen um 90 Grad gedreht werden.
    /// Die gespiegelten Faelle (2, 4, 5, 7) kommen selten vor, kosten aber
    /// nur je eine Zeile.
    /// </remarks>
    private static int DrehungAus(int ausrichtung) => ausrichtung switch
    {
        2 => 4,   // waagerecht gespiegelt
        3 => 2,   // 180 Grad
        4 => 6,   // 180 Grad und gespiegelt
        5 => 5,   // 90 Grad und gespiegelt
        6 => 1,   // 90 Grad
        7 => 7,   // 270 Grad und gespiegelt
        8 => 3,   // 270 Grad
        _ => 0    // aufrecht
    };

    public static bool LoadAsBitmap(string path, uint requestedSize, int ausrichtung, out nint bitmap)
    {
        bitmap = 0;
        if (!Start()) return false;

        nint image = 0, thumbnail = 0;
        try
        {
            if (GdipCreateBitmapFromFile(path, out image) != 0 || image == 0) return false;

            // Vor dem Messen, nicht danach: bei 90 Grad tauschen Breite und
            // Hoehe die Plaetze, und das Einpassen rechnet sonst mit den
            // falschen Kanten.
            if (DrehungAus(ausrichtung) is var dreh && dreh != 0)
                GdipImageRotateFlip(image, dreh);

            if (GdipGetImageWidth(image, out var width) != 0 ||
                GdipGetImageHeight(image, out var height) != 0 ||
                width == 0 || height == 0) return false;

            // In das angeforderte Quadrat einpassen, Seitenverhaeltnis behalten.
            // Nie hochskalieren: eine auf 1024 vergroesserte 256er Vorschau
            // sieht schlechter aus als das Ersatzsymbol.
            var scale = Math.Min(requestedSize / (double)width, requestedSize / (double)height);
            if (scale > 1.0) scale = 1.0;

            var targetWidth = Math.Max(1u, (uint)Math.Round(width * scale));
            var targetHeight = Math.Max(1u, (uint)Math.Round(height * scale));

            if (GdipGetImageThumbnail(image, targetWidth, targetHeight, out thumbnail, 0, 0) != 0 ||
                thumbnail == 0) return false;

            return GdipCreateHBITMAPFromBitmap(thumbnail, out bitmap, 0) == 0 && bitmap != 0;
        }
        finally
        {
            if (thumbnail != 0) GdipDisposeImage(thumbnail);
            if (image != 0) GdipDisposeImage(image);
        }
    }

    private static bool Start()
    {
        lock (Gate)
        {
            if (_started) return _token != 0;
            _started = true;

            var input = new StartupInput { Version = 1 };
            _started = true;
            return GdiplusStartup(out _token, in input, 0) == 0 && _token != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint Version;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [LibraryImport("gdiplus.dll")]
    private static partial int GdiplusStartup(out nint token, in StartupInput input, nint output);

    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GdipCreateBitmapFromFile(string filename, out nint bitmap);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipImageRotateFlip(nint image, int rotateFlipType);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageWidth(nint image, out uint width);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageHeight(nint image, out uint height);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageThumbnail(
        nint image, uint width, uint height, out nint thumbnail, nint callback, nint callbackData);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateHBITMAPFromBitmap(nint bitmap, out nint hbitmap, int background);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDisposeImage(nint image);
}

/// <summary>Die Fabrik, ueber die COM eine Instanz anfordert.</summary>
[GeneratedComClass]
internal sealed partial class Factory : IClassFactory
{
    private const int NoAggregation = unchecked((int)0x80040110);

    /// <summary>Was diese Fabrik herstellt. Die DLL bedient zwei Klassen.</summary>
    public Func<object> Erzeuge { get; init; } = () => new SyncTThumbnailProvider();

    public int CreateInstance(nint outerUnknown, in Guid interfaceId, out nint instance)
    {
        instance = 0;
        if (outerUnknown != 0) return NoAggregation;

        var unknown = Exports.Wrappers.GetOrCreateComInterfaceForObject(
            Erzeuge(), CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in interfaceId, out instance);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public int LockServer(bool @lock) => 0;
}

internal static class Exports
{
    /// <summary>Unsere CLSID. Sie steht so auch in der Registrierung.</summary>
    public static readonly Guid ClassId = new("7E4B2A61-3C9D-4F58-9A17-6D2E5B84C013");

    /// <summary>Die Klasse des Kontextmenues. Eigene Kennung, dieselbe DLL.</summary>
    public static readonly Guid MenuClassId = new("9C4E1F73-5A28-4D61-B0E9-3F7C6A15D482");

    public static readonly StrategyBasedComWrappers Wrappers = new();

    private const int Ok = 0;
    private const int ClassNotAvailable = unchecked((int)0x80040111);

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static unsafe int DllGetClassObject(Guid* classId, Guid* interfaceId, nint* result)
    {
        try
        {
            if (result is null) return unchecked((int)0x80004003);
            *result = 0;

            if (classId is null) return ClassNotAvailable;

            Factory fabrik;
            if (*classId == ClassId) fabrik = new Factory();
            else if (*classId == MenuClassId) fabrik = new Factory { Erzeuge = () => new SyncTContextMenu() };
            else return ClassNotAvailable;

            var unknown = Wrappers.GetOrCreateComInterfaceForObject(
                fabrik, CreateComInterfaceFlags.None);
            try
            {
                return Marshal.QueryInterface(unknown, in *interfaceId, out *result);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch
        {
            return ClassNotAvailable;
        }
    }

    /// <summary>
    /// Die DLL laesst sich nie entladen. Der Explorer haelt sie dann bis zu
    /// seinem Ende. Das kostet ein Megabyte und vermeidet alle Abstuerze, die
    /// beim Entladen waehrend eines laufenden Aufrufs entstehen.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => 1; // S_FALSE
}
