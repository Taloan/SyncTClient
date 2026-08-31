using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;

namespace SyncTClient.ThumbProvider;

// Diese DLL laedt der Explorer in seinen eigenen Prozess. Sie ist deshalb
// bewusst dumm gehalten: sie liest eine lokale Datei und gibt sie zurueck.
// Kein Netz, keine Wartezeit, kein Rueckruf in unser Programm. Ein Fehler
// hier reisst den Explorer mit, nicht nur uns.

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

    /// <summary>WTS_E_FAILEDEXTRACTION -- Explorer nimmt dann sein Ersatzsymbol.</summary>
    private const int NoThumbnail = unchecked((int)0x8004B200);

    /// <summary>WTSAT_RGB: undurchsichtig, JPEG kennt keine Transparenz.</summary>
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
    /// impliziert -- anders als ein Dateipfad -- keinen Lesezugriff.
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
                // Nichts im Vorrat -- vielleicht laesst sie sich beschaffen.
                // Wenn wir im Client laufen, hat der eine Leitung dorthin;
                // als DLL im fremden Prozess bleibt es beim Nachsehen.
                if (Store.Produce is null || !Store.Produce(_filePath) || !File.Exists(cached))
                {
                    Trace.Write($"  keine Vorschau unter {cached}");
                    return NoThumbnail;
                }

                Trace.Write("  auf Zuruf beschafft");
            }

            var ok = Gdi.LoadAsBitmap(cached, requestedSize, out bitmap);
            Trace.Write($"  {requestedSize} px -> {(ok ? "geliefert" : "GDI+ fehlgeschlagen")}");
            return ok ? Ok : NoThumbnail;
        }
        catch (Exception ex)
        {
            // Aus einer Shell-Erweiterung darf niemals etwas herausfliegen.
            Trace.Write("  Ausnahme: " + ex.Message);
            return NoThumbnail;
        }
    }
}


/// <summary>
/// Ein Protokoll fuer die Fehlersuche. Die Erweiterung laeuft in einem
/// fremden Prozess, in den man nicht hineinsehen kann -- ohne Spur bliebe
/// unklar, ob Windows sie ueberhaupt aufruft.
/// </summary>
internal static class Trace
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synctthumbs.log");

    public static void Write(string line)
    {
        try
        {
            File.AppendAllText(Path,
                $"{DateTime.Now:HH:mm:ss.fff}  {Environment.ProcessId,6}  {line}{Environment.NewLine}");
        }
        catch { /* die Fehlersuche darf nie stoeren */ }
    }
}

/// <summary>
/// Haelt den Wirtsprozess wach, solange Anfragen kommen. Im DLL-Fall gibt es
/// keinen -- dann tut das hier nichts.
/// </summary>
internal static class Host
{
    public static Action? Alive;
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
    /// steckt -- dort gibt es keine Verbindung zur Gegenstelle. Laeuft sie
    /// dagegen im Client, traegt der hier ein, wie sich ein Dateikopf holen
    /// laesst. Damit muss nichts mehr auf Vorrat erzeugt werden.
    /// </remarks>
    public static Func<string, bool>? Produce;

    private static string? _directory;
    private static bool _looked;

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
    /// Einmal nachschlagen genuegt -- der Explorer laedt uns oft.
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
/// JPEG von der Platte zu einem HBITMAP -- ueber die flache GDI+-Schnittstelle,
/// die ohne verwaltete Bildbibliothek auskommt.
/// </summary>
internal static partial class Gdi
{
    private static nint _token;
    private static bool _started;
    private static readonly Lock Gate = new();

    public static bool LoadAsBitmap(string path, uint requestedSize, out nint bitmap)
    {
        bitmap = 0;
        if (!Start()) return false;

        nint image = 0, thumbnail = 0;
        try
        {
            if (GdipCreateBitmapFromFile(path, out image) != 0 || image == 0) return false;

            if (GdipGetImageWidth(image, out var width) != 0 ||
                GdipGetImageHeight(image, out var height) != 0 ||
                width == 0 || height == 0) return false;

            // In das angeforderte Quadrat einpassen, Seitenverhaeltnis behalten.
            // Nie hochskalieren: eine 256er Vorschau auf 1024 aufgeblasen sieht
            // schlechter aus als das Ersatzsymbol.
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

    public int CreateInstance(nint outerUnknown, in Guid interfaceId, out nint instance)
    {
        instance = 0;
        if (outerUnknown != 0) return NoAggregation;

        var unknown = Exports.Wrappers.GetOrCreateComInterfaceForObject(
            new SyncTThumbnailProvider(), CreateComInterfaceFlags.None);
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
    /// <summary>Unsere CLSID -- steht so auch in der Registrierung.</summary>
    public static readonly Guid ClassId = new("7E4B2A61-3C9D-4F58-9A17-6D2E5B84C013");

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

            if (classId is null || *classId != ClassId) return ClassNotAvailable;

            var unknown = Wrappers.GetOrCreateComInterfaceForObject(
                new Factory(), CreateComInterfaceFlags.None);
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
    /// Wir lassen uns nie entladen. Der Explorer haelt die DLL dann bis zu
    /// seinem Ende -- das kostet ein Megabyte und erspart eine ganze Klasse
    /// von Abstuerzen beim Entladen waehrend eines laufenden Aufrufs.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => 1; // S_FALSE
}
