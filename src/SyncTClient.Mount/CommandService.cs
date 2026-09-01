using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SyncTClient.Mount;

/// <summary>
/// Nimmt Befehle der Shell-Erweiterung entgegen.
/// </summary>
/// <remarks>
/// Eine benannte Pipe und keine COM-Aktivierung. Der Weg über COM wäre der
/// naheliegende — die Erweiterung kennt unsere Klassen ohnehin —, aber das
/// Aktivieren einer eigenen CLSID als lokaler Server schlägt auf diesem
/// System bis heute unerklärt mit REGDB_E_CLASSNOTREG fehl. Eine Pipe hat
/// dieses Problem nicht, ist in beiden Richtungen sichtbar und lässt sich mit
/// einem Zweizeiler von Hand prüfen.
///
/// Die Pipe gehört dem angemeldeten Benutzer. Ein Befehl von hier verändert
/// Dateien in seinen Freigaben; niemand sonst hat darauf etwas zu suchen.
/// </remarks>
public static class CommandService
{
    /// <summary>Der Name, unter dem die Erweiterung uns findet.</summary>
    public const string PipeName = "SyncTClient.Commands";

    private static readonly object Gate = new();
    private static Thread? _thread;

    /// <summary>
    /// Was ein Befehl bewirkt. Gesetzt von der Oberfläche.
    /// </summary>
    /// <remarks>
    /// Die Entscheidung, was „ausblenden" bedeutet, gehört nicht hierher: sie
    /// betrifft die Konfiguration, und die führt die Oberfläche. Dieser Dienst
    /// nimmt entgegen und gibt zurück, mehr nicht.
    /// </remarks>
    public static Func<string, IReadOnlyList<string>, string>? Handle { get; set; }

    public static void EnsureStarted(Action<string> log)
    {
        lock (Gate)
        {
            if (_thread is not null) return;

            _thread = new Thread(() => Serve(log))
            {
                IsBackground = true,
                Name = "Befehle"
            };

            _thread.Start();
        }
    }

    /// <summary>
    /// Nach so vielen Fehlversuchen in Folge wird aufgegeben.
    /// </summary>
    /// <remarks>
    /// Ein belegter Name geht nicht von selbst frei. Alle zwei Sekunden
    /// dieselbe Zeile zu schreiben verdeckt jede andere Meldung und aendert
    /// nichts.
    /// </remarks>
    private const int MaximumFehler = 3;

    private static void Serve(Action<string> log)
    {
        var fehler = 0;

        while (true)
        {
            try
            {
                using var pipe = Erzeugen();
                pipe.WaitForConnection();

                // Steht die Leitung, war es kein dauerhaftes Hindernis.
                fehler = 0;

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                var zeile = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(zeile)) continue;

                var teile = zeile.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (teile.Length < 2) continue;

                var antwort = Handle?.Invoke(teile[0], teile[1..]) ?? "";
                writer.WriteLine(antwort);

                // Ohne dieses Warten schliesst das Verwerfen der Pipe den
                // Puffer, bevor die Gegenseite gelesen hat.
                pipe.WaitForPipeDrain();
            }
            catch (Exception ex)
            {
                if (++fehler > MaximumFehler)
                {
                    log($"Befehlsdienst: {ex.Message} -- aufgegeben. " +
                        "Das Kontextmenue im Explorer bleibt ohne Wirkung.");
                    return;
                }

                log($"Befehlsdienst: {ex.Message}");

                // Nicht in einer engen Schleife scheitern.
                Thread.Sleep(2000);
            }
        }
    }

    private static NamedPipeServerStream Erzeugen()
    {
        var rechte = new PipeSecurity();
        rechte.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None,
            0, 0, rechte);
    }
}
