using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SyncTClient.Mount;

/// <summary>Was die Abfrage ergeben hat.</summary>
/// <param name="Version">Die Fassung der neuesten Freigabe, etwa 0.9.2.</param>
/// <param name="Seite">Die Adresse der Freigabe im Browser.</param>
public sealed record NeuereFassung(Version Version, string Seite);

/// <summary>
/// Sieht bei GitHub nach, ob eine neuere Fassung vorliegt.
/// </summary>
/// <remarks>
/// Absichtlich nur nachsehen und melden. Herunterladen und ausfuehren waere
/// technisch machbar, aber hier falsch:
///
/// Das Programm traegt keine Signatur -- ein Zertifikat kostet jaehrlich Geld.
/// Ohne sie kann es nicht pruefen, dass eine geladene Datei vom Urheber
/// stammt; eine Pruefsumme von derselben Seite beweist nichts, wenn die Seite
/// das Problem ist. Ein Programm, das im Hintergrund eine ausfuehrbare Datei
/// holt und startet, ist ausserdem genau das Muster, an dem Schadsoftware
/// erkannt wird.
///
/// Dazu haelt der Dateimanager die Shell-Erweiterung geladen, solange er
/// laeuft, und eine Installation fuer alle Benutzer braucht
/// Administratorrechte. Ein stiller Austausch muesste beides aufloesen.
///
/// Ein Hinweis und ein Verweis auf die Seite umgehen das alles.
/// </remarks>
public static class UpdateCheck
{
    /// <summary>Wo die Freigaben liegen.</summary>
    private const string Api = "https://api.github.com/repos/Taloan/SyncTClient/releases/latest";

    /// <summary>Die Seite, die der Verweis oeffnet.</summary>
    public const string Seite = "https://github.com/Taloan/SyncTClient/releases/latest";

    /// <summary>
    /// Ist die Abfrage nach diesem Abstand faellig?
    /// </summary>
    public static bool Faellig(UpdateInterval abstand, DateTimeOffset zuletzt)
    {
        var tage = abstand switch
        {
            UpdateInterval.Weekly => 7,
            UpdateInterval.Monthly => 30,
            _ => 0
        };

        if (tage == 0) return false;

        // Ein Zeitpunkt in der Zukunft steht fuer eine verstellte Uhr oder
        // eine von Hand bearbeitete Konfiguration. Dann lieber einmal zu viel
        // nachsehen als nie wieder.
        return zuletzt > DateTimeOffset.Now || DateTimeOffset.Now - zuletzt >= TimeSpan.FromDays(tage);
    }

    /// <summary>
    /// Fragt die neueste Freigabe ab und meldet sie, wenn sie neuer ist als
    /// die laufende.
    /// </summary>
    /// <remarks>
    /// Gibt <c>null</c> zurueck, wenn nichts Neueres vorliegt oder die Abfrage
    /// nicht durchkam. Ein Fehlschlag ist kein Ereignis: kein Netz, ein
    /// privates Verzeichnis, GitHub nicht erreichbar -- in keinem dieser
    /// Faelle soll das Programm etwas melden.
    /// </remarks>
    public static async Task<NeuereFassung?> AbfragenAsync(
        Version laufend, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            // GitHub weist Abfragen ohne Kennung ab.
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("SyncTClient", laufend.ToString()));
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var antwort = await http.GetAsync(Api, ct).ConfigureAwait(false);
            if (!antwort.IsSuccessStatusCode) return null;

            await using var strom = await antwort.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(strom, cancellationToken: ct).ConfigureAwait(false);

            if (!json.RootElement.TryGetProperty("tag_name", out var etikett)) return null;
            if (json.RootElement.TryGetProperty("draft", out var entwurf) && entwurf.GetBoolean()) return null;

            if (Lesen(etikett.GetString()) is not { } dort) return null;
            if (dort <= laufend) return null;

            var seite = json.RootElement.TryGetProperty("html_url", out var url)
                ? url.GetString() ?? Seite
                : Seite;

            return new NeuereFassung(dort, seite);
        }
        catch (Exception)
        {
            // Siehe oben: ein Fehlschlag ist kein Ereignis.
            return null;
        }
    }

    /// <summary>
    /// Die Zahl aus einem Etikett wie <c>v0.9.2</c>.
    /// </summary>
    /// <remarks>
    /// Verglichen wird als <see cref="Version"/> und nicht als Zeichenkette:
    /// sonst gilt "0.10.0" als aelter als "0.9.2", weil "1" vor "9" kommt.
    /// </remarks>
    public static Version? Lesen(string? etikett)
    {
        if (string.IsNullOrWhiteSpace(etikett)) return null;

        var roh = etikett.Trim();
        if (roh.StartsWith('v') || roh.StartsWith('V')) roh = roh[1..];

        // Ein angehaengtes "+<Pruefsumme>" oder "-rc1" gehoert nicht zur Zahl.
        var schnitt = roh.IndexOfAny(['+', '-', ' ']);
        if (schnitt > 0) roh = roh[..schnitt];

        return Version.TryParse(roh, out var fassung) ? Normal(fassung) : null;
    }

    /// <summary>
    /// Auf drei Stellen gebracht.
    /// </summary>
    /// <remarks>
    /// <see cref="Version"/> unterscheidet "0.9.1" von "0.9.1.0": die eine hat
    /// Revision -1, die andere 0, und der Vergleich faellt entsprechend aus.
    /// Die Fassung der Anwendung kommt vierstellig, das Etikett dreistellig --
    /// ohne Angleich waere jede Freigabe scheinbar aelter als das Laufende.
    /// </remarks>
    public static Version Normal(Version fassung)
        => new(fassung.Major, fassung.Minor, Math.Max(fassung.Build, 0));
}
