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

[StructLayout(LayoutKind.Sequential)]
internal struct MENUITEMINFOW
{
    public uint cbSize;
    public uint fMask;
    public uint fType;
    public uint fState;
    public uint wID;
    public nint hSubMenu;
    public nint hbmpChecked;
    public nint hbmpUnchecked;
    public nint dwItemData;
    public nint dwTypeData;
    public uint cch;
    public nint hbmpItem;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
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

        _first = idFirst;

        var laeuft = Sync.ClientLaeuft();
        var grau = laeuft ? 0u : MfGrayed;

        // Ausserhalb jeder Freigabe gibt es genau eine sinnvolle Handlung:
        // aus dem Ordner eine machen. Die uebrigen Eintraege setzen einen
        // Abgleich voraus, den es dort nicht gibt.
        if (!_paths.Any(Sync.Inside))
        {
            // Und nur fuer einen einzelnen Ordner. Eine Freigabe hat einen
            // Pfad; bei mehreren waere nicht zu sagen, welcher gemeint ist,
            // und eine Datei kann keine Wurzel sein.
            if (_paths.Length != 1 || !Directory.Exists(_paths[0])) return 0;

            var einzeln = CreatePopupMenu();
            if (einzeln == 0) return 0;

            AppendMenuW(einzeln, MfString | grau, idFirst + 3, "Als Freigabe anbieten ...");
            InsertMenuW(menu, indexMenu, MfByPosition | MfPopup, einzeln, "SyncTClient");
            SetzeSymbol(menu, indexMenu);

            return unchecked((int)(0x00000000 | 4u));
        }

        // Sonst nur innerhalb einer Freigabe, und dann ganz. Eine Auswahl,
        // die halb darin und halb daneben liegt, hat keine Handlung, die auf
        // beide Haelften passt.
        if (!_paths.All(Sync.Inside)) return 0;

        var untermenue = CreatePopupMenu();
        if (untermenue == 0) return 0;

        AppendMenuW(untermenue, MfString | grau, idFirst + 0, "Immer auf diesem Gerät behalten");
        AppendMenuW(untermenue, MfString | grau, idFirst + 1, "Speicherplatz freigeben");
        AppendMenuW(untermenue, MfSeparator, 0, null);

        // Nur fuer Ordner, und nur wenn ausschliesslich Ordner gewaehlt sind.
        // Auf eine einzelne Datei angewandt hiesse "ausblenden" etwas
        // anderes als in den Einstellungen.
        if (_paths.All(Directory.Exists))
            AppendMenuW(untermenue, MfString | grau, idFirst + 2, "Diesen Ordner ausblenden");

        InsertMenuW(menu, indexMenu, MfByPosition | MfPopup, untermenue, "SyncTClient");
        SetzeSymbol(menu, indexMenu);

        // Anzahl der vergebenen Kennungen, als HRESULT nach Vorschrift.
        return unchecked((int)(0x00000000 | 3u));
    }

    // ------------------------------------------------------------ Symbol

    private static nint _symbol;
    private static bool _symbolVersucht;

    private static unsafe void SetzeSymbol(nint menu, uint position)
    {
        var symbol = Symbol();
        if (symbol == 0) return;

        var info = new MENUITEMINFOW
        {
            cbSize = (uint)sizeof(MENUITEMINFOW),
            fMask = MiimBitmap,
            hbmpItem = symbol
        };

        SetMenuItemInfoW(menu, position, true, ref info);
    }

    /// <summary>
    /// Das Programmsymbol, gezeichnet als Bitmap.
    /// </summary>
    /// <remarks>
    /// Ein Menue nimmt kein Symbol entgegen, sondern eine Bitmap. Das Symbol
    /// wird deshalb aus dem Programm geholt und auf eine Flaeche mit
    /// Alphakanal gezeichnet. Ohne diesen Kanal stuende es auf einem
    /// schwarzen Rechteck, denn ein Symbol traegt seine Durchsichtigkeit
    /// selbst mit.
    ///
    /// Einmal gebaut und behalten. Der Datei-Manager haelt diese DLL, solange
    /// er laeuft, und baut das Menue bei jedem Rechtsklick neu auf; das Bild
    /// jedesmal neu zu zeichnen waere Arbeit fuer nichts. Die Bitmap bleibt
    /// bis zum Ende des Prozesses stehen, das Menue verweist darauf.
    /// </remarks>
    private static unsafe nint Symbol()
    {
        if (_symbolVersucht) return _symbol;
        _symbolVersucht = true;

        var programm = Sync.Programm();
        if (programm is null) return 0;

        nint ikone = 0;
        if (ExtractIconExW(programm, 0, null, &ikone, 1) == 0 || ikone == 0) return 0;

        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);

        try
        {
            // Die Groesse, die das System fuer kleine Symbole vorsieht. Bei
            // hoher Punktdichte ist das mehr als sechzehn Punkte.
            var kante = GetSystemMetrics(SmCxSmIcon);
            if (kante <= 0) kante = 16;

            var kopf = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = kante,

                // Negativ: die erste Zeile im Speicher ist die oberste.
                biHeight = -kante,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            var bitmap = CreateDIBSection(dc, ref kopf, 0, out _, 0, 0);
            if (bitmap == 0) return 0;

            var vorher = SelectObject(dc, bitmap);
            DrawIconEx(dc, 0, 0, ikone, kante, kante, 0, 0, DiNormal);
            SelectObject(dc, vorher);

            _symbol = bitmap;
            return bitmap;
        }
        finally
        {
            DeleteDC(dc);
            ReleaseDC(0, screen);
            DestroyIcon(ikone);
        }
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
            3 => "ADD",
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

    private const uint MiimBitmap = 0x00000080;
    private const int SmCxSmIcon = 49;
    private const uint DiNormal = 0x0003;

    [LibraryImport("user32.dll", EntryPoint = "SetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuItemInfoW(nint menu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MENUITEMINFOW info);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint ExtractIconExW(string file, int index, nint* large, nint* small, uint icons);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(nint dc, int x, int y, nint icon,
        int width, int height, uint step, nint brush, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint handle);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER header,
        uint usage, out nint bits, nint section, uint offset);
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
    ///
    /// Eine leere Liste wird allerdings nicht behalten. Sie heisst meist, dass
    /// der Client seit dem Start dieses Datei-Managers noch nicht gelaufen
    /// ist. Behielten wir sie, laege bis zu dessen Neustart jeder Ordner
    /// "ausserhalb" -- auch die Freigaben, und dort erschienen dann die
    /// falschen Eintraege.
    /// </remarks>
    private static string[] Roots()
    {
        if (_roots is { Length: > 0 }) return _roots;

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

    /// <summary>Wo das Programm liegt, dessen Symbol im Menue steht.</summary>
    public static string? Programm()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SyncTClient");
            var pfad = key?.GetValue("Programm") as string;
            return File.Exists(pfad) ? pfad : null;
        }
        catch (Exception)
        {
            return null;
        }
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

    /// <summary>
    /// Wie lange auf eine Antwort gewartet wird.
    /// </summary>
    /// <remarks>
    /// Grosszuegig, denn "immer lokal setzen" arbeitet ueber die ganze
    /// Auswahl, bevor es antwortet. Aber endlich: das Lesen selbst kennt
    /// keine Frist, und ein Client, der aus welchem Grund auch immer nicht
    /// antwortet, hielte den Datei-Manager sonst bis zum Abschiessen fest.
    /// Genau das ist einmal passiert.
    /// </remarks>
    private static readonly TimeSpan Geduld = TimeSpan.FromSeconds(15);

    public static string Send(string befehl, IReadOnlyList<string> pfade)
    {
        // Der Austausch laeuft auf einem eigenen Faden, damit das Warten
        // darauf eine Frist bekommen kann. Laeuft sie ab, bleibt der Faden
        // zwar stehen -- er haengt an einer Pipe, die niemand mehr liest --,
        // aber das Fenster, in dem geklickt wurde, ist wieder ansprechbar.
        var austausch = Task.Run(() => Austausch(befehl, pfade));

        return austausch.Wait(Geduld)
            ? austausch.Result
            : "SyncTClient antwortet nicht.";
    }

    private static string Austausch(string befehl, IReadOnlyList<string> pfade)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);

            // Kurz warten und dann aufgeben. Ein Kontextmenue, das den
            // Explorer anhaelt, ist schlimmer als eines, das nichts tut.
            pipe.Connect(3000);

            // leaveOpen bei beiden, und das ist noetig: sonst raeumt der
            // zuletzt angelegte zuerst auf, schliesst dabei die Pipe, und der
            // andere will danach noch einmal leeren. Die Ausnahme daraus
            // fliegt aus dem try heraus und ueberschreibt die Antwort, die
            // laengst da ist, mit "Cannot access a closed pipe". Die Pipe
            // selbst wird von ihrem eigenen using geschlossen.
            using var writer = new StreamWriter(
                pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(
                pipe, Encoding.UTF8, false, 1024, leaveOpen: true);

            writer.WriteLine(befehl + "\t" + string.Join('\t', pfade));
            return reader.ReadLine() ?? "";
        }
        catch (Exception ex)
        {
            return $"SyncTClient antwortet nicht: {ex.Message}";
        }
    }
}
