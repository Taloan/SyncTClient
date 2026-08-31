using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SyncTClient.ThumbProvider;

/// <summary>
/// Der Vorschau-Erzeuger als eigenstaendiges Programm statt als DLL im
/// Explorer-Prozess.
/// </summary>
/// <remarks>
/// OneDrive ist genauso aufgebaut: seine Ueberlagerungssymbole kommen aus
/// FileSyncShell64.dll im Explorer-Prozess, der Vorschau-Erzeuger dagegen aus
/// FileCoAuth.exe als LocalServer32. Fuer Platzhalter ist das sinnvoll. Ein
/// Anbieter, der moeglicherweise Daten nachladen muss, gehoert nicht in den
/// Vorschau-Wirt des Explorers.
///
/// Damit entfaellt auch NativeAOT: ausserhalb des Explorers darf eine
/// .NET-Laufzeit vorausgesetzt werden.
///
/// COM startet dieses Programm on-demand selbst. Es meldet seine Fabrik an,
/// pumpt Nachrichten und beendet sich, wenn eine Zeit lang keine Anfrage mehr
/// eingetroffen ist.
/// </remarks>
internal static class Program
{
    /// <summary>Nach dieser Zeit ohne Anfrage beendet sich das Programm.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private static DateTime _lastCall = DateTime.UtcNow;

    public static void Touch() => _lastCall = DateTime.UtcNow;

    [STAThread]
    private static int Main(string[] args)
    {
        // Mit -register/-unregister laesst sich die Anmeldung von aussen
        // pruefen, ohne den Client zu starten.
        if (args.Contains("-register")) return Registration.Write();
        if (args.Contains("-unregister")) return Registration.Remove();

        Host.Alive = Touch;
        Trace.Write($"Host startet, PID {Environment.ProcessId}");

        var wrappers = new StrategyBasedComWrappers();
        var factory = wrappers.GetOrCreateComInterfaceForObject(new Factory(), CreateComInterfaceFlags.None);

        try
        {
            var classId = Exports.ClassId;
            var hr = CoRegisterClassObject(
                in classId, factory, ClassContextLocalServer, RegClsMultipleUse | RegClsSuspended, out var cookie);

            if (hr != 0)
            {
                Trace.Write($"CoRegisterClassObject: 0x{(uint)hr:X8}");
                return 1;
            }

            // Erst ab hier duerfen Anfragen ankommen. So geht keine Anfrage
            // verloren, die zwischen Anmeldung und Bereitschaft eintrifft.
            CoResumeClassObjects();
            Trace.Write("Fabrik angemeldet, warte auf Anfragen");

            Pump();

            CoRevokeClassObject(cookie);
            Trace.Write("Host beendet");
            return 0;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>
    /// Nachrichtenschleife mit Zeitgeber: COM stellt Aufrufe ueber Nachrichten
    /// zu, und ohne Pumpe bliebe jede Anfrage liegen.
    /// </summary>
    private static void Pump()
    {
        const uint TimerId = 1;
        SetTimer(0, TimerId, 30_000, 0);

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (message.Message == WmTimer && DateTime.UtcNow - _lastCall > IdleTimeout) break;

            TranslateMessage(in message);
            DispatchMessage(in message);
        }

        KillTimer(0, TimerId);
    }

    private const uint ClassContextLocalServer = 0x4;
    private const uint RegClsMultipleUse = 1;
    private const uint RegClsSuspended = 4;
    private const uint WmTimer = 0x0113;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        in Guid classId, nint unknown, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoResumeClassObjects();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, nint window, uint first, uint last);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint window, uint id, uint interval, nint callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(nint window, uint id);
}

/// <summary>Traegt das Programm als COM-Server ein.</summary>
internal static class Registration
{
    private static string ClassId => Exports.ClassId.ToString("B").ToUpperInvariant();

    public static int Write()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) { Console.Error.WriteLine("Eigener Pfad unbekannt."); return 1; }

        using var clsid = Microsoft.Win32.Registry.CurrentUser
            .CreateSubKey($@"Software\Classes\CLSID\{ClassId}");
        clsid.SetValue(null, "SyncTClient Vorschaubilder");

        // LocalServer32 statt InprocServer32: COM startet das Programm bei
        // Bedarf, statt eine DLL in den Explorer zu laden.
        using var server = clsid.CreateSubKey("LocalServer32");
        server.SetValue(null, $"\"{exe}\"");

        // Eine vorhandene DLL-Anmeldung muss entfernt werden, sonst hat sie Vorrang.
        try { clsid.DeleteSubKeyTree("InprocServer32", throwOnMissingSubKey: false); } catch { }

        Console.WriteLine($"Eingetragen: {ClassId} -> {exe}");
        return 0;
    }

    public static int Remove()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\CLSID\{ClassId}", throwOnMissingSubKey: false);
            Console.WriteLine("Ausgetragen.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }
}
