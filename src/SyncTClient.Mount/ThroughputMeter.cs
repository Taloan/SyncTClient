namespace SyncTClient.Mount;

/// <summary>Ein Messpunkt: gelesene und gesendete Bytes je Sekunde.</summary>
public readonly record struct ThroughputPoint(double Read, double Written);

/// <summary>
/// Fuehrt Buch ueber den Durchsatz der letzten Stunden.
/// </summary>
/// <remarks>
/// Gespeichert wird sekundengenau in einem Ringpuffer -- drei Stunden sind
/// 10800 Werte je Richtung, also ein paar Zehntel Megabyte. Das ist billiger
/// als mehrere Aufloesungen nebeneinander zu pflegen, und jede gewuenschte
/// Zeitspanne laesst sich daraus zusammenfassen.
///
/// Die Verbindungen liefern nur Gesamtzaehler seit dem Verbinden. Hier
/// entsteht daraus die Rate, indem jede Sekunde die Differenz zur Vorsekunde
/// gebildet wird. Faellt ein Zaehler zurueck -- eine Verbindung wurde neu
/// aufgebaut --, gilt die Differenz als null statt als negativer Ausschlag.
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

                // Der erste Wert hat keinen Vorgaenger und waere sonst ein
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
            // Ein Messfehler darf den Zeitgeber nicht abwuergen.
        }
    }

    /// <summary>
    /// Fasst die letzte <paramref name="window"/> zu <paramref name="buckets"/>
    /// Saeulen zusammen, aelteste zuerst.
    /// </summary>
    public ThroughputPoint[] Series(TimeSpan window, int buckets)
    {
        var wanted = Math.Clamp((int)window.TotalSeconds, buckets, Capacity);
        var result = new ThroughputPoint[buckets];

        lock (_gate)
        {
            var perBucket = (double)wanted / buckets;

            for (var b = 0; b < buckets; b++)
            {
                // Rueckwaerts von jetzt: Korb 0 liegt am weitesten zurueck.
                var fromEnd = (int)Math.Round((buckets - b) * perBucket);
                var toEnd = (int)Math.Round((buckets - b - 1) * perBucket);

                double read = 0, written = 0;
                var n = 0;

                for (var offset = fromEnd; offset > toEnd; offset--)
                {
                    var index = _seconds - offset;
                    if (index < 0 || _seconds - index > Capacity) continue;

                    var slot = (int)(index % Capacity);
                    read += _read[slot];
                    written += _written[slot];
                    n++;
                }

                result[b] = n == 0
                    ? new ThroughputPoint(0, 0)
                    : new ThroughputPoint(read / n, written / n);
            }
        }

        return result;
    }

    public void Dispose() => _timer.Dispose();
}
