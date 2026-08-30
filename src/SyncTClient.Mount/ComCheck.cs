using System.Runtime.InteropServices;

namespace SyncTClient.Mount;

/// <summary>
/// Erzeugt die Vorschau-Erweiterung ueber COM -- unabhaengig davon, ob
/// Windows sie von sich aus fragt.
/// </summary>
/// <remarks>
/// Trennt zwei Fehlerbilder, die sonst gleich aussehen: eine DLL, die sich
/// nicht laden laesst, und eine Verdrahtung, die Windows nicht beachtet.
/// </remarks>
public static class ComCheck
{
    private static readonly Guid ClassId = new("7E4B2A61-3C9D-4F58-9A17-6D2E5B84C013");
    private static readonly Guid Unknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid ThumbnailProvider = new("e357fccd-a995-4576-b01f-234630154e96");
    private static readonly Guid InitializeWithFile = new("b7d14566-0509-4cce-a71f-0a554233bd9b");
    private static readonly Guid InitializeWithItem = new("7f73be3f-fb79-493c-a6c7-7ee14e245841");

    public static int Run()
    {
        var result = 1;
        var thread = new Thread(() => result = Ask());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int Ask()
    {
        Console.WriteLine($"CLSID: {ClassId:B}");

        // CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER: der Anbieter darf eine
        // DLL im eigenen Prozess sein oder ein eigenstaendiges Programm.
        const uint AnyServer = 0x1 | 0x4;
        var hr = CoCreateInstance(in ClassId, 0, AnyServer, in Unknown, out var instance);

        if (hr != 0 || instance == 0)
        {
            Console.WriteLine($"CoCreateInstance schlug fehl: 0x{(uint)hr:X8}");
            Console.WriteLine("  -> weder DLL noch Wirtsprogramm liessen sich starten.");
            return 1;
        }

        Console.WriteLine("CoCreateInstance: erzeugt.");

        try
        {
            foreach (var (name, iid) in new[]
                     {
                         ("IThumbnailProvider ", ThumbnailProvider),
                         ("IInitializeWithFile", InitializeWithFile),
                         ("IInitializeWithItem", InitializeWithItem)
                     })
            {
                var id = iid;
                var queried = Marshal.QueryInterface(instance, in id, out var iface);
                Console.WriteLine($"  {name}  {(queried == 0 ? "vorhanden" : $"0x{(uint)queried:X8}")}");
                if (queried == 0) Marshal.Release(iface);
            }
        }
        finally
        {
            Marshal.Release(instance);
        }

        return 0;
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);
}
