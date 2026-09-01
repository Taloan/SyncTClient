namespace SyncTClient.Bep;

/// <summary>
/// Zaehlt alle Bytes, die dieses Programm ueber irgendeine Verbindung
/// geschickt oder empfangen hat.
/// </summary>
/// <remarks>
/// Programmweit und nicht je Verbindung, weil die Frage programmweit ist:
/// "wie viel ging seit dem Start ueber den Draht". Je Verbindung gezaehlt
/// laesst sich diese Frage nicht beantworten, ohne alle Verbindungen zu
/// kennen -- auch die abgebrochenen, die ersetzten und die, die gerade noch
/// eine Anfrage zu Ende bedienen. Wer eine davon uebersieht, zaehlt zu wenig
/// und merkt es nicht.
///
/// Erhoeht wird ausschliesslich in <see cref="CountingStream"/>, und jede
/// Verbindung legt einen solchen Strom auf ihren TLS-Strom. Damit gibt es
/// keinen Weg an diesem Zaehler vorbei.
/// </remarks>
public static class WireTally
{
    private static long _read;
    private static long _written;

    /// <summary>Gelesen und geschrieben, seit das Programm laeuft.</summary>
    public static (long Read, long Written) Totals
        => (Interlocked.Read(ref _read), Interlocked.Read(ref _written));

    internal static void AddRead(long bytes) => Interlocked.Add(ref _read, bytes);

    internal static void AddWritten(long bytes) => Interlocked.Add(ref _written, bytes);
}
