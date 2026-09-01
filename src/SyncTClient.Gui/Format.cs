namespace SyncTClient.Gui;

/// <summary>
/// Dieselben Angaben wie im Protokoll und in den Gruenden eines Rueckstands.
/// </summary>
/// <remarks>
/// Die Rechnung steht in <see cref="Mount.Format"/>, weil sie dort ebenso
/// gebraucht wird. Zwei Fassungen davon waeren zwei Fassungen, die eines Tages
/// verschieden runden.
/// </remarks>
internal static class Format
{
    public static string Bytes(long value) => Mount.Format.Bytes(value);

    public static string Bytes(double value) => Mount.Format.Bytes(value);

    public static string Rate(double bytesPerSecond) => Mount.Format.Rate(bytesPerSecond);

    public static string Count(long value) => Mount.Format.Count(value);
}
