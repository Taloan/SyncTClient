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
    private static readonly ManualResetEventSlim _stop = new(false);
    private static readonly Lock Gate = new();

    /// <summary>Meldet die Klasse an, falls das noch nicht geschehen ist.</summary>
    public static void EnsureStarted(Action<string> log)
    {
        lock (Gate)
        {
            if (_thread is not null) return;

            // Fehlende Vorschauen holt der Client selbst nach.
            Store.Produce = ShareHost.ProduceThumbnail;

            _thread = new Thread(() => Serve(log))
            {
                IsBackground = true,
                Name = "Vorschau-Erzeuger"
            };

            // Mehrthread-Apartment, nicht STA -- und das ist hier wesentlich.
            //
            // Im Einzelthread-Apartment reiht COM alle Aufrufe auf einem
            // Thread auf. Solange die Vorschauen fertig auf der Platte lagen,
            // fiel das nicht auf. Seit wir den Dateikopf bei Bedarf holen,
            // wartet jeder Aufruf aufs Netz, und der naechste kommt erst
            // danach an die Reihe: gemessen 69 Bilder in 45 Sekunden. Im MTA
            // stellt COM die Aufrufe nebenlaeufig zu, und die Drossel weiter
            // unten bestimmt das Tempo statt der Apartment-Grenze.
            _thread.SetApartmentState(ApartmentState.MTA);
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
                // Im MTA stellt COM die Aufrufe auf eigenen Threads zu; eine
                // Nachrichtenschleife braucht es dafuer nicht. Dieser Thread
                // haelt nur die Anmeldung am Leben.
                _stop.Wait();
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

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        in Guid classId, nint unknown, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoResumeClassObjects();
}
