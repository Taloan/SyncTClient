using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SyncTClient.ThumbProvider;

namespace SyncTClient.Mount;

/// <summary>
/// Bietet den Vorschau-Erzeuger aus dem laufenden Client heraus an.
/// </summary>
/// <remarks>
/// Genau so macht es Microsofts CloudMirror-Beispiel: kein Eintrag als
/// eigenstaendiges Programm, sondern <c>CoRegisterClassObject</c> aus dem
/// Anbieterprozess. Wer den Ordner bereitstellt, beantwortet auch die Fragen
/// dazu -- das erspart einen zweiten Prozess und einen zweiten Zustand.
///
/// Gemessen: ohne diese Anmeldung scheitert eine Aktivierung ueber
/// <c>CLSCTX_LOCAL_SERVER</c> mit <c>REGDB_E_CLASSNOTREG</c>, mit ihr liefert
/// sie die Vorschau. Die Eintragung als In-Prozess-DLL bleibt daneben
/// bestehen; welchen der beiden Wege die Shell im Einzelfall waehlt, bestimmt
/// sie selbst.
/// </remarks>
public static class ThumbnailService
{
    private static Thread? _thread;
    private static readonly Lock Gate = new();

    /// <summary>Meldet die Klasse an, falls das noch nicht geschehen ist.</summary>
    public static void EnsureStarted(Action<string> log)
    {
        lock (Gate)
        {
            if (_thread is not null) return;

            _thread = new Thread(() => Serve(log))
            {
                IsBackground = true,
                Name = "Vorschau-Erzeuger"
            };

            // Die Shell erwartet ein Einzelthread-Apartment; ohne Nachrichten-
            // schleife bliebe jede Anfrage liegen.
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private static void Serve(Action<string> log)
    {
        var wrappers = new StrategyBasedComWrappers();
        var factory = wrappers.GetOrCreateComInterfaceForObject(new Factory(), CreateComInterfaceFlags.None);

        try
        {
            var classId = Exports.ClassId;
            var hr = CoRegisterClassObject(
                in classId, factory, ClassContextLocalServer,
                RegClsMultipleUse | RegClsSuspended, out var cookie);

            if (hr != 0)
            {
                log($"Vorschau-Erzeuger nicht anmeldbar: 0x{(uint)hr:X8}");
                return;
            }

            // Erst ab hier duerfen Anfragen ankommen -- so geht keine
            // verloren, die zwischen Anmeldung und Bereitschaft eintrifft.
            CoResumeClassObjects();

            try
            {
                while (GetMessage(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(in message);
                    DispatchMessage(in message);
                }
            }
            finally
            {
                CoRevokeClassObject(cookie);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private const uint ClassContextLocalServer = 0x4;
    private const uint RegClsMultipleUse = 1;
    private const uint RegClsSuspended = 4;

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
}
