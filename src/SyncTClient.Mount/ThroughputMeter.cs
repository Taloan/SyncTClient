namespace SyncTClient.Mount;

/// <summary>Ein Messpunkt: gelesene und gesendete Bytes je Sekunde.</summary>
public readonly record struct ThroughputPoint(double Read, double Written);

/// <summary>
/// Zeichnet den Durchsatz der letzten Stunden auf.
/// </summary>
/// <remarks>
/// Gespeichert wird sekundengenau in einem Ringpuffer. Drei Stunden sind
/// 10800 Werte je Richtung, also ein paar Zehntel Megabyte. Das ist guenstiger,
/// als mehrere Aufloesungen nebeneinander zu pflegen, und jede gewuenschte
/// Zeitspanne laesst sich daraus zusammenfassen.
///
/// Die Verbindungen liefern nur Gesamtzaehler seit dem Verbinden. Daraus
/// entsteht hier die Rate, indem jede Sekunde die Differenz zur Vorsekunde
/// gebildet wird. Faellt ein Zaehler zurueck, weil eine Verbindung neu
/// aufgebaut wurde, gilt die Differenz als null statt als negativer Ausschlag.
/// </remarks>
public sealed class ThroughputMeter : IDisposable
{
    /// <summary>Drei Stunden, sekundenweise.</summary>
    public const int Capacity = 3 * 60 * 60;

    private readonly double[] _read = new double[Capacity];
    private readonly double[] _written = new double[Capacity];
    private readonly Func<(long Read, long Written)> _source;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private long _lastRead;
    private long _lastWritten;
    private long _seconds;
    private bool _primed;

    public ThroughputMeter(Func<(long Read, long Written)> source)
    {
        _source = source;
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Gesamtzaehler seit dem Start des Programms.</summary>
    public (long Read, long Written) Total { get; private set; }

    private void Sample()
    {
        try
        {
            var (read, written) = _source();

            lock (_gate)
            {
                Total = (read, written);

                // Der erste Wert hat keinen Vorgaenger und ergaebe sonst einen
                // Ausschlag in Hoehe des gesamten bisherigen Verkehrs.
                if (_primed)
                {
                    var slot = (int)(_seconds % Capacity);
                    _read[slot] = Math.Max(0, read - _lastRead);
                    _written[slot] = Math.Max(0, written - _lastWritten);
                    _seconds++;
                }
                else
                {
                    _primed = true;
                }

                _lastRead = read;
                _lastWritten = written;
            }
        }
        catch
        {
            // Ein Messfehler darf den Zeitgeber nicht beenden.
        }
    }

    /// <summary>
    /// Fasst die letzte <paramref name="window"/> zu <paramref name="buckets"/>
    /// Saeulen zusammen, aelteste zuerst.
    /// </summary>
    public ThroughputPoint[] Series(TimeSpan window, int buckets)
    {
        var result = new ThroughputPoint[buckets];
        var jeKorb = Math.Max(1, (int)Math.Round(window.TotalSeconds / buckets));

        lock (_gate)
        {
            // An absoluten Sekunden ausgerichtet, nicht an "jetzt".
            //
            // Vorher wurden die Koerbe rueckwaerts von der aktuellen Sekunde
            // aus abgeteilt. Bei jedem Takt verschob sich damit jede
            // Korbgrenze um eine Sekunde, und dieselben Messwerte fielen in
            // andere Koerbe -- das Bild sah jede Sekunde anders aus, obwohl
            // sich an den Daten kaum etwas geaendert hatte.
            //
            // Mit fester Ausrichtung bleibt der Inhalt eines Korbes stehen.
            // Das Diagramm rueckt nur dann um eine Saeule weiter, wenn eine
            // Korbgrenze ueberschritten wird.
            var letzterKorb = _seconds / jeKorb;

            for (var b = 0; b < buckets; b++)
            {
                var korb = letzterKorb - (buckets - 1 - b);
                if (korb < 0) continue;

                double read = 0, written = 0;
                var n = 0;

                for (var k = 0; k < jeKorb; k++)
                {
                    var index = korb * jeKorb + k;
                    if (index < 0 || index >= _seconds) continue;
                    if (_seconds - index > Capacity) continue;

                    var slot = (int)(index % Capacity);
                    read += _read[slot];
                    written += _written[slot];
                    n++;
                }

                if (n > 0) result[b] = new ThroughputPoint(read / n, written / n);
            }
        }

        return result;
    }

    public void Dispose() => _timer.Dispose();
}
