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

    /// <summary>
    /// So viele Verbindungen duerfen zugleich offen sein.
    /// </summary>
    /// <remarks>
    /// Mehr als eine ist noetig, seit die Erweiterung auch Vorschauen ueber
    /// diesen Weg anfordert. Wer keinen Platz findet, wartet beim Verbinden;
    /// die Frist dort ist kurz, damit der Explorer nicht stehenbleibt.
    /// </remarks>
    private const int GleichzeitigeVerbindungen = 16;

    private static void Serve(Action<string> log)
    {
        var fehler = 0;

        while (true)
        {
            try
            {
                var pipe = Erzeugen();
                pipe.WaitForConnection();

                // Kam eine Verbindung zustande, war es kein dauerhaftes Hindernis.
                fehler = 0;

                // Die naechste Verbindung darf nicht warten, bis diese fertig
                // ist. Ein Kontextmenue-Befehl ist sofort beantwortet, eine
                // Vorschau dagegen wartet auf die Gegenstelle -- und der
                // Explorer fragt einen ganzen Ordner auf einmal ab. Nach der
                // Reihe bedient, liefe die Frist der meisten Anfragen ab,
                // bevor sie an der Reihe waeren.
                _ = Task.Run(() => Bedienen(pipe, log));
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

    /// <summary>
    /// Beantwortet eine einzelne Verbindung.
    /// </summary>
    /// <remarks>
    /// Ein Fehler hier betrifft nur diese eine Verbindung und zaehlt deshalb
    /// nicht gegen <see cref="MaximumFehler"/>. Der Zaehler dort ist fuer den
    /// belegten Namen gedacht, nicht fuer einen Befehl, der schiefgeht.
    /// </remarks>
    private static void Bedienen(NamedPipeServerStream pipe, Action<string> log)
    {
        try
        {
            using (pipe)
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                var zeile = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(zeile)) return;

                var teile = zeile.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (teile.Length < 2) return;

                var antwort = Handle?.Invoke(teile[0], teile[1..]) ?? "";
                writer.WriteLine(antwort);

                // Ohne dieses Warten schliesst das Verwerfen der Pipe den
                // Puffer, bevor die Gegenseite gelesen hat.
                pipe.WaitForPipeDrain();
            }
        }
        catch (Exception ex)
        {
            log($"Befehlsdienst: {ex.Message}");
        }
    }

    /// <summary>
    /// Schickt einen Befehl an eine laufende Instanz.
    /// </summary>
    /// <remarks>
    /// Derselbe Weg, den die Shell-Erweiterung nimmt. Er wird auch beim
    /// Starten gebraucht: eine zweite Instanz hat nichts zu tun, ausser der
    /// ersten zu sagen, dass jemand sie sucht.
    ///
    /// Die Wartezeit ist kurz. Antwortet niemand, laeuft eben keine Instanz
    /// -- oder eine, die gerade beschaeftigt ist; in beiden Faellen ist
    /// Weitermachen besser als Warten.
    /// </remarks>
    /// <returns>Die Antwort, oder <c>null</c>, wenn niemand zuhoert.</returns>
    public static string? Send(string befehl, IReadOnlyList<string> argumente, int millisekunden = 1500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(millisekunden);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8);

            // Der Dienst trennt an Tabulatoren und braucht mindestens zwei
            // Felder. Ein Befehl ohne Argument bekommt deshalb einen Strich.
            var felder = argumente.Count > 0 ? string.Join('\t', argumente) : "-";
            writer.WriteLine($"{befehl}\t{felder}");

            return reader.ReadLine() ?? "";
        }
        catch (Exception)
        {
            return null;
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
            PipeName, PipeDirection.InOut, GleichzeitigeVerbindungen,
            PipeTransmissionMode.Byte, PipeOptions.None,
            0, 0, rechte);
    }
}
