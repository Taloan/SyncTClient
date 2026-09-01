namespace SyncTClient.Mount;

/// <summary>Formatiert Zahlen fuer die Anzeige, nicht fuer die Speicherung.</summary>
/// <remarks>
/// Steht hier und nicht in der Oberflaeche, weil auch das Protokoll und die
/// Gruende eines Rueckstands Groessen nennen. "1620608 B statt 1559971 B" ist
/// richtig und trotzdem unlesbar; der Unterschied zweier Zahlen faellt in
/// Megabyte auf und in Bytes nicht.
/// </remarks>
public static class Format
{
    private static readonly string[] Einheiten = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long value) => Bytes((double)value);

    public static string Bytes(double value)
    {
        var i = 0;
        while (Math.Abs(value) >= 1024 && i < Einheiten.Length - 1) { value /= 1024; i++; }

        // Unter zehn eine Nachkommastelle, darueber keine. Bei "9,4 MB" traegt
        // die Nachkommastelle Information, bei "943,7 MB" verlaengert sie die
        // Angabe gegenueber "944 MB" ohne Gewinn.
        var ziffern = i == 0 || value >= 10 ? "0" : "0.0";
        return $"{value.ToString(ziffern)} {Einheiten[i]}";
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Count(long value) => value.ToString("N0");
}
