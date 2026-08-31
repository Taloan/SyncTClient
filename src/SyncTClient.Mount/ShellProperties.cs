using System.Runtime.InteropServices;

namespace SyncTClient.Mount;

/// <summary>
/// Zeigt, was die Shell ueber eine Datei weiss.
/// </summary>
/// <remarks>
/// Ob der Explorer einen Vorschau-Anbieter aufruft, haengt davon ab, ob er die
/// Datei ueberhaupt einem Anbieter zuordnet. Genau das steht in diesen
/// Eigenschaften. Sie sind ausserdem die Quelle der Spalten "Status" und
/// "Verfuegbarkeit". Ein Vergleich mit einem Anbieter, der funktioniert,
/// zeigt, ob unsere Platzhalter dort ankommen, wo sie sollen.
/// </remarks>
public static class ShellProperties
{
    private static readonly Guid IShellItem2 = new("7E9FB0D3-919F-4307-AB2E-9B1860310C93");

    private static readonly string[] Interesting =
    [
        "System.StorageProviderId",
        "System.StorageProviderCallerVersionUIDisplayName",
        "System.FilePlaceholderStatus",
        "System.OfflineAvailability",
        "System.OfflineStatus",
        "System.FileAttributes",
        "System.ItemType"
    ];

    public static int Run(string path)
    {
        var result = 1;
        var thread = new Thread(() => result = Ask(path));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int Ask(string path)
    {
        var full = Path.GetFullPath(path);
        Console.WriteLine($"Datei: {full}");

        var iid = IShellItem2;
        var hr = SHCreateItemFromParsingName(full, 0, in iid, out var item);
        if (hr != 0 || item == 0)
        {
            Console.Error.WriteLine($"SHCreateItemFromParsingName: 0x{(uint)hr:X8}");
            return 1;
        }

        try
        {
            var getString = Method<GetStringFn>(item, 17);
            var getUInt32 = Method<GetUInt32Fn>(item, 18);

            foreach (var name in Interesting)
            {
                if (PSGetPropertyKeyFromName(name, out var key) != 0)
                {
                    Console.WriteLine($"  {name,-52} unbekannt");
                    continue;
                }

                // Erst als Text, dann als Zahl. Welchen Typ eine Eigenschaft
                // hat, gibt die Shell nicht vorab an.
                if (getString(item, in key, out var text) == 0 && text != 0)
                {
                    Console.WriteLine($"  {name,-52} {Marshal.PtrToStringUni(text)}");
                    Marshal.FreeCoTaskMem(text);
                }
                else if (getUInt32(item, in key, out var number) == 0)
                {
                    Console.WriteLine($"  {name,-52} {number}");
                }
                else
                {
                    Console.WriteLine($"  {name,-52} --");
                }
            }
        }
        finally
        {
            Marshal.Release(item);
        }

        return 0;
    }

    private static T Method<T>(nint iface, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(iface);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * nint.Size));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStringFn(nint self, in PropertyKey key, out nint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt32Fn(nint self, in PropertyKey key, out uint value);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, in Guid interfaceId, out nint item);
}
