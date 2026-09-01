using Vector = SyncTClient.Bep.Proto.Vector;

namespace SyncTClient.Bep;

/// <summary>Wie zwei Versionen derselben Datei zueinander stehen.</summary>
public enum VersionOrder
{
    /// <summary>Beide Seiten kennen denselben Stand.</summary>
    Gleich,

    /// <summary>Die erste Version enthaelt alles aus der zweiten und mehr.</summary>
    Neuer,

    /// <summary>Die zweite Version enthaelt alles aus der ersten und mehr.</summary>
    Aelter,

    /// <summary>
    /// Beide Seiten haben geaendert, ohne die Aenderung der anderen zu kennen.
    /// </summary>
    Nebeneinander
}

/// <summary>
/// Vergleicht Versionsvektoren, wie das Protokoll sie fuehrt.
/// </summary>
/// <remarks>
/// Ein Vektor haelt je Geraet einen Zaehler. Eine Version enthaelt eine
/// andere, wenn sie fuer jedes dort genannte Geraet mindestens denselben
/// Zaehlerstand fuehrt. Enthalten beide einander, ist es derselbe Stand;
/// enthaelt keine die andere, wurde an zwei Stellen unabhaengig geaendert.
///
/// Zeitstempel taugen fuer diese Frage nicht. Sie sagen, welche Aenderung
/// spaeter geschah, nicht ob eine Seite die andere kannte.
/// </remarks>
public static class VersionVectors
{
    public static VersionOrder Compare(Vector? a, Vector? b)
    {
        var links = Counters(a);
        var rechts = Counters(b);

        var enthaeltRechts = Enthaelt(links, rechts);
        var enthaeltLinks = Enthaelt(rechts, links);

        return (enthaeltRechts, enthaeltLinks) switch
        {
            (true, true) => VersionOrder.Gleich,
            (true, false) => VersionOrder.Neuer,
            (false, true) => VersionOrder.Aelter,
            _ => VersionOrder.Nebeneinander
        };
    }

    /// <summary>Der hoechste Zaehlerstand je Geraet.</summary>
    /// <remarks>
    /// Ein Geraet sollte nur einmal vorkommen. Kommt es doch mehrfach vor,
    /// zaehlt der hoechste Stand, denn Zaehler wachsen nur.
    /// </remarks>
    private static Dictionary<ulong, ulong> Counters(Vector? vector)
    {
        var map = new Dictionary<ulong, ulong>();
        if (vector is null) return map;

        foreach (var counter in vector.Counters)
        {
            if (!map.TryGetValue(counter.Id, out var known) || counter.Value > known)
                map[counter.Id] = counter.Value;
        }

        return map;
    }

    private static bool Enthaelt(Dictionary<ulong, ulong> a, Dictionary<ulong, ulong> b)
    {
        foreach (var (id, value) in b)
        {
            // Ein fehlendes Geraet zaehlt als 0. Der leere Vektor ist damit in
            // jedem enthalten, und eine Datei ohne Version verliert gegen jede
            // mit Version.
            if (!a.TryGetValue(id, out var known) || known < value) return false;
        }

        return true;
    }
}
