namespace SyncTClient.Gui;

/// <summary>Zahlen, wie man sie liest und nicht wie sie gespeichert sind.</summary>
internal static class Format
{
    private static readonly string[] Einheiten = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long value) => Bytes((double)value);

    public static string Bytes(double value)
    {
        var i = 0;
        while (Math.Abs(value) >= 1024 && i < Einheiten.Length - 1) { value /= 1024; i++; }

        // Unter zehn eine Nachkommastelle, darueber keine: "9,4 MB" sagt
        // etwas, "943,7 MB" ist nur laenger als "944 MB".
        var ziffern = i == 0 || value >= 10 ? "0" : "0.0";
        return $"{value.ToString(ziffern)} {Einheiten[i]}";
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Count(long value) => value.ToString("N0");
}
