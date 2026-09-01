using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Microsoft.Win32;

namespace SyncTClient.ThumbProvider;

// Auch diese Klasse laedt der Explorer in seinen eigenen Prozess. Sie haelt
// sich deshalb an dieselbe Regel wie der Vorschau-Erzeuger: nichts rechnen,
// nichts entscheiden, auf nichts lange warten. Sie liest die Auswahl, zeigt
// drei Eintraege und schickt Pfade durch eine Pipe. Was daraus wird,
// entscheidet der Client.

[GeneratedComInterface]
[Guid("0000010e-0000-0000-C000-000000000046")]
internal partial interface IDataObject
{
    [PreserveSig] int GetData(in FORMATETC format, out STGMEDIUM medium);
    [PreserveSig] int GetDataHere(in FORMATETC format, ref STGMEDIUM medium);
    [PreserveSig] int QueryGetData(in FORMATETC format);
    [PreserveSig] int GetCanonicalFormatEtc(in FORMATETC format, out FORMATETC result);
    [PreserveSig] int SetData(in FORMATETC format, in STGMEDIUM medium, [MarshalAs(UnmanagedType.Bool)] bool release);
    [PreserveSig] int EnumFormatEtc(uint direction, out nint enumerator);
    [PreserveSig] int DAdvise(in FORMATETC format, uint flags, nint sink, out uint connection);
    [PreserveSig] int DUnadvise(uint connection);
    [PreserveSig] int EnumDAdvise(out nint enumerator);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FORMATETC
{
    public ushort cfFormat;
    public nint ptd;
    public uint dwAspect;
    public int lindex;
    public uint tymed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STGMEDIUM
{
    public uint tymed;
    public nint handle;
    public nint pUnkForRelease;
}

[GeneratedComInterface]
[Guid("000214e8-0000-0000-c000-000000000046")]
internal partial interface IShellExtInit
{
    [PreserveSig] int Initialize(nint folderPidl, IDataObject? dataObject, nint progIdKey);
}

[GeneratedComInterface]
[Guid("000214e4-0000-0000-c000-000000000046")]
internal partial interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idFirst, uint idLast, uint flags);
    [PreserveSig] int InvokeCommand(nint invokeInfo);
    [PreserveSig] int GetCommandString(nuint id, uint kind, nint reserved, nint name, uint max);
}

/// <summary>
/// Der Eintrag "SyncTClient" im Kontextmenü des Datei-Managers.
/// </summary>
/// <remarks>
/// Bewusst die klassische Schnittstelle und nicht IExplorerCommand: im neuen
/// Menü von Windows 11 erscheinen eigene Einträge nur bei paketierten
/// Programmen. Directory Opus und das erweiterte Explorer-Menü zeigen das
/// klassische, und dort steht auch „Immer auf diesem Gerät behalten" von
/// Windows selbst.
/// </remarks>
[GeneratedComClass]
internal sealed partial class SyncTContextMenu : IShellExtInit, IContextMenu
{
    private const int Ok = 0;
    private const int Fail = unchecked((int)0x80004005);
    private const ushort CfHdrop = 15;
    private const uint TymedHGlobal = 1;

    private const uint MfString = 0x0000;
    private const uint MfPopup = 0x0010;
    private const uint MfSeparator = 0x0800;
    private const uint MfGrayed = 0x0001;
    private const uint MfByPosition = 0x0400;

    /// <summary>Die Auswahl, so wie der Datei-Manager sie übergeben hat.</summary>
    private string[] _paths = [];

    private uint _first;

    // ------------------------------------------------------------ Initialize

    public int Initialize(nint folderPidl, IDataObject? dataObject, nint progIdKey)
    {
        _paths = [];
        if (dataObject is null) return Fail;

        var format = new FORMATETC
        {
            cfFormat = CfHdrop,
            dwAspect = 1,
            lindex = -1,
            tymed = TymedHGlobal
        };

        if (dataObject.GetData(in format, out var medium) != Ok) return Fail;

        try
        {
            unsafe
            {
                var anzahl = DragQueryFileW(medium.handle, 0xFFFFFFFF, null, 0);
                var namen = new List<string>((int)anzahl);

                for (uint i = 0; i < anzahl; i++)
                {
                    var laenge = DragQueryFileW(medium.handle, i, null, 0);
                    if (laenge == 0) continue;

                    var puffer = new char[laenge + 1];
                    fixed (char* zeiger = puffer)
                        if (DragQueryFileW(medium.handle, i, zeiger, laenge + 1) > 0)
                            namen.Add(new string(zeiger));
                }

                _paths = [.. namen];
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }

        return _paths.Length > 0 ? Ok : Fail;
    }

    // ------------------------------------------------------------ Menü

    public int QueryContextMenu(nint menu, uint indexMenu, uint idFirst, uint idLast, uint flags)
    {
        // CMF_DEFAULTONLY: der Aufrufer will nur die Standardaktion wissen.
        if ((flags & 0x00000001) != 0) return 0;

        // Nur innerhalb einer Freigabe. Ausserhalb hat keiner der Eintraege
        // eine Bedeutung, und ein Menue, das ueberall auftaucht, ist eine
        // Zumutung.
        if (!_paths.All(Sync.Inside)) return 0;

        _first = idFirst;

        var untermenue = CreatePopupMenu();
        if (untermenue == 0) return 0;

        var laeuft = Sync.ClientLaeuft();
        var grau = laeuft ? 0u : MfGrayed;

        AppendMenuW(untermenue, MfString | grau, idFirst + 0, "Immer auf diesem Gerät behalten");
        AppendMenuW(untermenue, MfString | grau, idFirst + 1, "Speicherplatz freigeben");
        AppendMenuW(untermenue, MfSeparator, 0, null);

        // Nur fuer Ordner, und nur wenn ausschliesslich Ordner gewaehlt sind.
        // Auf eine einzelne Datei angewandt hiesse "ausblenden" etwas
        // anderes als in den Einstellungen.
        if (_paths.All(Directory.Exists))
            AppendMenuW(untermenue, MfString | grau, idFirst + 2, "Diesen Ordner ausblenden");

        InsertMenuW(menu, indexMenu, MfByPosition | MfPopup, untermenue, "SyncTClient");

        // Anzahl der vergebenen Kennungen, als HRESULT nach Vorschrift.
        return unchecked((int)(0x00000000 | 3u));
    }

    public int InvokeCommand(nint invokeInfo)
    {
        if (invokeInfo == 0) return Fail;

        // CMINVOKECOMMANDINFO: cbSize, fMask, hwnd, lpVerb ... lpVerb traegt
        // bei einem Menueeintrag die Kennung in den unteren 16 Bit.
        var verb = Marshal.ReadIntPtr(invokeInfo, IntPtr.Size == 8 ? 24 : 12);
        if ((ulong)verb > 0xFFFF) return Fail;

        var befehl = (long)verb switch
        {
            0 => "PIN",
            1 => "FREE",
            2 => "HIDE",
            _ => null
        };

        if (befehl is null) return Fail;

        var antwort = Sync.Send(befehl, _paths);
        if (!string.IsNullOrWhiteSpace(antwort))
            MessageBoxW(0, antwort, "SyncTClient", 0x00000040);

        return Ok;
    }

    public int GetCommandString(nuint id, uint kind, nint reserved, nint name, uint max) => Fail;

    // ------------------------------------------------------------ Windows

    // Ohne StringBuilder: der Quellcode-Erzeuger fuer P/Invokes kennt ihn
    // nicht. Ein Puffer aus Zeichen tut dasselbe und spart eine Umwandlung.
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static unsafe partial uint DragQueryFileW(nint drop, uint index, char* file, uint max);

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(ref STGMEDIUM medium);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, uint id, string? text);

    [LibraryImport("user32.dll", EntryPoint = "InsertMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InsertMenuW(nint menu, uint position, uint flags, nint id, string? text);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}

/// <summary>Der Draht zum laufenden Client.</summary>
internal static class Sync
{
    /// <summary>Muss zu CommandService.PipeName passen.</summary>
    private const string PipeName = "SyncTClient.Commands";

    private static string[]? _roots;

    /// <summary>
    /// Die Wurzeln der Freigaben, wie der Client sie hinterlegt hat.
    /// </summary>
    /// <remarks>
    /// Einmal gelesen und behalten. Der Explorer laedt diese DLL fuer die
    /// Dauer seines Lebens; sie bei jedem Rechtsklick neu zu lesen waere ein
    /// Registrierungszugriff je Klick.
    /// </remarks>
    private static string[] Roots()
    {
        if (_roots is not null) return _roots;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SyncTClient");
            _roots = key?.GetValue("Shares") as string[] ?? [];
        }
        catch (Exception)
        {
            _roots = [];
        }

        return _roots;
    }

    public static bool Inside(string path)
    {
        foreach (var wurzel in Roots())
        {
            var root = wurzel.TrimEnd(Path.DirectorySeparatorChar);

            if (path.Length > root.Length
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && path[root.Length] == Path.DirectorySeparatorChar)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Ob der Client laeuft. Ohne ihn haben die Eintraege niemanden, der sie
    /// ausfuehrt; sie erscheinen dann grau statt zu scheitern.
    /// </summary>
    public static bool ClientLaeuft()
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\", PipeName).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string Send(string befehl, IReadOnlyList<string> pfade)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);

            // Kurz warten und dann aufgeben. Ein Kontextmenue, das den
            // Explorer anhaelt, ist schlimmer als eines, das nichts tut.
            pipe.Connect(3000);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8);

            writer.WriteLine(befehl + "\t" + string.Join('\t', pfade));
            return reader.ReadLine() ?? "";
        }
        catch (Exception ex)
        {
            return $"SyncTClient antwortet nicht: {ex.Message}";
        }
    }
}
