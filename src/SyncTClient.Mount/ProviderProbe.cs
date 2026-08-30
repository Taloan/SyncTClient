using System.Runtime.InteropServices;

namespace SyncTClient.Mount;

/// <summary>
/// Ruft einen Vorschau-Anbieter unmittelbar auf -- ohne die Shell dazwischen.
/// </summary>
/// <remarks>
/// <c>--thumbcheck</c> fragt Windows und bekommt im Zweifel ein
/// Standardsymbol; ob der Anbieter dabei ueberhaupt gefragt wurde, bleibt
/// offen. Hier gehen wir den Weg selbst: Klasse erzeugen, mit der Datei
/// bekanntmachen, Vorschau anfordern. Damit trennen sich zwei Fehlerbilder,
/// die sonst gleich aussehen -- ein Anbieter, der nichts liefert, und einer,
/// den niemand fragt.
///
/// Mit einer fremden CLSID laesst sich damit auch nachsehen, wie ein Anbieter
/// arbeitet, der nachweislich funktioniert.
/// </remarks>
public static class ProviderProbe
{
    private static readonly Guid Unknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid ShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid ThumbnailProvider = new("e357fccd-a995-4576-b01f-234630154e96");
    private static readonly Guid InitializeWithFile = new("b7d14566-0509-4cce-a71f-0a554233bd9b");
    private static readonly Guid InitializeWithItem = new("7f73be3f-fb79-493c-a6c7-7ee14e245841");

    public static int Run(Guid classId, string path, uint width, uint context)
    {
        var result = 1;
        var thread = new Thread(() => result = Ask(classId, path, width, context));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int Ask(Guid classId, string path, uint width, uint context)
    {
        var full = Path.GetFullPath(path);
        Console.WriteLine($"Anbieter: {classId:B}");
        Console.WriteLine($"Datei:    {full}");

        const uint recallOnDataAccess = 0x0040_0000;
        var attributes = (uint)new FileInfo(full).Attributes;
        Console.WriteLine($"Zustand:  {(((attributes & recallOnDataAccess) != 0) ? "Platzhalter (nicht lokal)" : "lokal vorhanden")}");
        Console.WriteLine();

        // Der Kontext entscheidet ueber die Bauform, und das ist hier keine
        // Nebensache: erlaubt man INPROC, laedt COM die DLL in den eigenen
        // Prozess und umgeht dabei einen etwaigen Surrogat. Ein Anbieter, der
        // auf die Abschottung des Surrogats baut, scheitert dann -- und man
        // haelt ihn faelschlich fuer kaputt.
        Console.WriteLine($"Kontext:  {Describe(context)}");
        var hr = CoCreateInstance(in classId, 0, context, in Unknown, out var instance);
        if (hr != 0 || instance == 0)
        {
            Console.WriteLine($"CoCreateInstance: 0x{(uint)hr:X8} -- Klasse liess sich nicht erzeugen.");
            return 1;
        }

        Console.WriteLine("CoCreateInstance: erzeugt.");

        try
        {
            if (!Initialize(instance, full)) return 1;

            var iid = ThumbnailProvider;
            if (Marshal.QueryInterface(instance, in iid, out var provider) != 0)
            {
                Console.WriteLine("IThumbnailProvider: nicht vorhanden.");
                return 1;
            }

            try
            {
                var getThumbnail = Method<GetThumbnailFn>(provider, 3);
                nint bitmap = 0;
                try
                {
                    hr = getThumbnail(provider, width, out bitmap, out var alpha);
                    Console.WriteLine(hr == 0
                        ? $"GetThumbnail: {BitmapFingerprint.Describe(bitmap)} (Alpha {alpha})"
                        : $"GetThumbnail: 0x{(uint)hr:X8}");
                    return hr == 0 ? 0 : 1;
                }
                finally
                {
                    BitmapFingerprint.Release(bitmap);
                }
            }
            finally
            {
                Marshal.Release(provider);
            }
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    /// <summary>
    /// Macht den Anbieter mit der Datei bekannt.
    /// </summary>
    /// <remarks>
    /// Die Shell bevorzugt <c>IInitializeWithItem</c>, weil ein Shell-Element
    /// mehr weiss als ein Pfad -- bei Platzhaltern etwa den Sync-Root, zu dem
    /// es gehoert. Wer nur <c>IInitializeWithFile</c> anbietet, wird trotzdem
    /// bedient; deshalb probieren wir beides in dieser Reihenfolge.
    /// </remarks>
    private static bool Initialize(nint instance, string full)
    {
        var withItem = InitializeWithItem;
        if (Marshal.QueryInterface(instance, in withItem, out var initItem) == 0)
        {
            try
            {
                var shellItemId = ShellItem;
                var hr = SHCreateItemFromParsingName(full, 0, in shellItemId, out var item);
                if (hr != 0 || item == 0)
                {
                    Console.WriteLine($"SHCreateItemFromParsingName: 0x{(uint)hr:X8}");
                    return false;
                }

                try
                {
                    hr = Method<InitializeWithItemFn>(initItem, 3)(initItem, item, 0);
                    Console.WriteLine($"IInitializeWithItem.Initialize: {(hr == 0 ? "ok" : $"0x{(uint)hr:X8}")}");
                    return hr == 0;
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
            finally
            {
                Marshal.Release(initItem);
            }
        }

        var withFile = InitializeWithFile;
        if (Marshal.QueryInterface(instance, in withFile, out var initFile) == 0)
        {
            try
            {
                var hr = Method<InitializeWithFileFn>(initFile, 3)(initFile, full, 0);
                Console.WriteLine($"IInitializeWithFile.Initialize: {(hr == 0 ? "ok" : $"0x{(uint)hr:X8}")}");
                return hr == 0;
            }
            finally
            {
                Marshal.Release(initFile);
            }
        }

        Console.WriteLine("Weder IInitializeWithItem noch IInitializeWithFile vorhanden.");
        return false;
    }

    public const uint InProc = 0x1;
    public const uint LocalServer = 0x4;

    private static string Describe(uint context) => context switch
    {
        InProc => "nur INPROC (DLL im eigenen Prozess)",
        LocalServer => "nur LOCAL_SERVER (Surrogat oder eigenes Programm)",
        _ => "INPROC oder LOCAL_SERVER"
    };

    /// <summary>Holt die n-te Methode aus der Methodentabelle einer Schnittstelle.</summary>
    private static T Method<T>(nint iface, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(iface);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * nint.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeWithItemFn(nint self, nint item, uint mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int InitializeWithFileFn(nint self, [MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetThumbnailFn(nint self, uint width, out nint bitmap, out uint alpha);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, in Guid interfaceId, out nint item);
}
