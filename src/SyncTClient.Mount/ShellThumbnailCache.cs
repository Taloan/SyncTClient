using System.Runtime.InteropServices;

namespace SyncTClient.Mount;

/// <summary>
/// Fragt die Vorschau ueber den Zwischenspeicher der Shell ab. Das ist der
/// Weg, den der Explorer selbst nimmt.
/// </summary>
/// <remarks>
/// <c>IShellItemImageFactory</c> ist bequemer, liegt aber eine Ebene zu hoch:
/// es liefert im Zweifel ein Symbol und gibt nicht an, ob ein Anbieter
/// aufgerufen wurde. <c>IThumbnailCache</c> ist die Ebene darunter. Vor allem
/// kennt es Flags, die es sonst nirgends gibt, etwa die Aufforderung, den
/// Anbieter in einem eigenen Prozess zu betreiben. Genau daran hat sich die
/// Fehlersuche aufgehalten, deshalb werden die Flags hier einzeln
/// durchgemessen.
/// </remarks>
public static class ShellThumbnailCache
{
    private static readonly Guid LocalThumbnailCache = new("50EF4544-AC9F-4A8E-B21B-8A26180DB13F");
    private static readonly Guid IThumbnailCache = new("F676C15D-596A-4CE2-8234-33996F445DB1");
    private static readonly Guid IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static int Run(string path, uint width)
    {
        var result = 1;
        var thread = new Thread(() => result = Ask(path, width));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int Ask(string path, uint width)
    {
        var full = Path.GetFullPath(path);
        Console.WriteLine($"Datei: {full}");

        const uint recallOnDataAccess = 0x0040_0000;
        var attributes = (uint)new FileInfo(full).Attributes;
        Console.WriteLine($"Zustand: {(((attributes & recallOnDataAccess) != 0) ? "Platzhalter (nicht lokal)" : "lokal vorhanden")}");
        Console.WriteLine();

        var cacheId = LocalThumbnailCache;
        var cacheIid = IThumbnailCache;
        var hr = CoCreateInstance(in cacheId, 0, 0x1 | 0x4, in cacheIid, out var cache);
        if (hr != 0 || cache == 0)
        {
            Console.Error.WriteLine($"Zwischenspeicher nicht erreichbar: 0x{(uint)hr:X8}");
            return 1;
        }

        var itemIid = IShellItem;
        hr = SHCreateItemFromParsingName(full, 0, in itemIid, out var item);
        if (hr != 0 || item == 0)
        {
            Console.Error.WriteLine($"SHCreateItemFromParsingName: 0x{(uint)hr:X8}");
            Marshal.Release(cache);
            return 1;
        }

        try
        {
            var getThumbnail = Method<GetThumbnailFn>(cache, 3);

            foreach (var (name, flags) in Probes)
            {
                nint shared = 0;
                try
                {
                    hr = getThumbnail(cache, item, width, flags, out shared, out var cacheFlags, out _);
                    if (hr != 0 || shared == 0)
                    {
                        Console.WriteLine($"  {name} -> 0x{(uint)hr:X8}");
                        continue;
                    }

                    var getBitmap = Method<GetSharedBitmapFn>(shared, 3);
                    if (getBitmap(shared, out var bitmap) != 0)
                    {
                        Console.WriteLine($"  {name} -> Bitmap nicht lesbar");
                        continue;
                    }

                    Console.WriteLine($"  {name} -> {BitmapFingerprint.Describe(bitmap)}, Herkunft {cacheFlags}");
                }
                finally
                {
                    if (shared != 0) Marshal.Release(shared);
                }
            }
        }
        finally
        {
            Marshal.Release(item);
            Marshal.Release(cache);
        }

        return 0;
    }

    /// <remarks>
    /// FORCEEXTRACTION umgeht den Zwischenspeicher. Ohne dieses Flag werden
    /// alte Bilder gemessen statt der Anbieterkette. REQUIRESURROGATE
    /// erzwingt den eigenen Prozess, INPROC verbietet ihn. Welcher der beiden
    /// Wege bedient wird, ist genau die offene Frage.
    /// </remarks>
    private static readonly (string Name, uint Flags)[] Probes =
    [
        ("EXTRACT           ", 0x00),
        ("INCACHEONLY       ", 0x01),
        ("FASTEXTRACT       ", 0x02),
        ("FORCEEXTRACTION   ", 0x04),
        ("FORCE|DONOTCACHE  ", 0x04 | 0x20),
        ("FORCE|SKIPFAST    ", 0x04 | 0x80),
        ("FORCE|EXTRACTINPROC", 0x04 | 0x100),
        ("FORCE|REQUIRESURROGATE", 0x04 | 0x800)
    ];

    private static T Method<T>(nint iface, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(iface);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * nint.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetThumbnailFn(
        nint self, nint item, uint width, uint flags,
        out nint shared, out uint cacheFlags, out ThumbnailId id);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSharedBitmapFn(nint self, out nint bitmap);

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct ThumbnailId { }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, in Guid interfaceId, out nint item);
}
