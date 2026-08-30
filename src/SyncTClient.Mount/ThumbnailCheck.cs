using System.Runtime.InteropServices;

namespace SyncTClient.Mount;

/// <summary>
/// Fragt Windows nach der Vorschau einer Datei -- genau so, wie der Explorer
/// es tut.
/// </summary>
/// <remarks>
/// Dateimanager bringen eigene Bilddekodierung mit und umgehen die
/// Shell-Erweiterung; ein Blick in einen von ihnen sagt also nichts darueber,
/// ob unsere Registrierung greift. <c>IShellItemImageFactory</c> ist der Weg,
/// den Windows selbst nimmt.
/// </remarks>
public static class ThumbnailCheck
{
    public static int Run(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            Console.Error.WriteLine($"Nicht gefunden: {full}");
            return 1;
        }

        var result = 1;

        // Die Shell will einen Einzelthread-Apartment.
        var thread = new Thread(() => result = Ask(full));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int Ask(string full)
    {
        Console.WriteLine($"Datei:   {full}");

        var attributes = (uint)new FileInfo(full).Attributes;
        Console.WriteLine($"Zustand: {(((attributes & 0x0040_0000) != 0) ? "Platzhalter (nicht lokal)" : "lokal vorhanden")}");

        var store = new ThumbnailStore(
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SyncTClient")
                ?.GetValue("ThumbnailStore") as string ?? "");
        var cached = store.Directory.Length > 1 ? store.PathFor(full) : null;
        Console.WriteLine($"Vorrat:  {(cached is not null && File.Exists(cached) ? cached : "keine vorbereitete Vorschau")}");
        Console.WriteLine();

        var iid = typeof(IShellItemImageFactory).GUID;
        var hr = SHCreateItemFromParsingName(full, 0, in iid, out var factory);
        if (hr != 0 || factory is null)
        {
            Console.Error.WriteLine($"SHCreateItemFromParsingName: 0x{(uint)hr:X8}");
            return 1;
        }

        try
        {
            // Verschiedene Flag-Kombinationen: THUMBNAILONLY verbietet jeden
            // Rueckfall und blockiert offenbar auch den Weg ueber den
            // Sync-Root-Anbieter -- selbst OneDrive scheitert damit. Was der
            // Explorer wirklich schickt, faellt hier auf.
            var probes = new (string Name, int Flags)[]
            {
                ("Standard        ", 0x00),
                ("BIGGERSIZEOK    ", 0x01),
                ("MEMORYONLY      ", 0x02),
                ("THUMBNAILONLY   ", 0x08),
                ("INCACHEONLY     ", 0x10),
                ("SCALEUP         ", 0x100)
            };

            foreach (var (name, flags) in probes)
            {
                nint bitmap = 0;
                try
                {
                    factory.GetImage(new Size { Width = 256, Height = 256 }, flags, out bitmap);

                    if (bitmap == 0) { Console.WriteLine($"  {name} -> nichts"); continue; }

                    var info = new BitmapInfo();
                    GetObject(bitmap, Marshal.SizeOf<BitmapInfo>(), ref info);
                    Console.WriteLine($"  {name} -> {info.Width}x{info.Height}, {info.BitsPerPixel} bpp");
                }
                catch (COMException ex)
                {
                    Console.WriteLine($"  {name} -> 0x{(uint)ex.HResult:X8}");
                }
                finally
                {
                    if (bitmap != 0) DeleteObject(bitmap);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPerPixel;
        public nint Bits;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(Size size, int flags, out nint bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, in Guid interfaceId, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint handle, int size, ref BitmapInfo info);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
