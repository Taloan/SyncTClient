using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Web;

namespace SyncTClient.Bep;

/// <summary>
/// Fragt einen Erkennungsserver, unter welchen Adressen ein Gerät gerade
/// erreichbar ist.
/// </summary>
/// <remarks>
/// Eine feste Adresse setzt voraus, dass die Gegenstelle eine hat. Wer zu
/// Hause keinen Port nach aussen oeffnet, hat keine -- er meldet dem
/// Erkennungsserver stattdessen, ueber welchen Relay er zu sprechen ist.
///
/// Der Server traegt ein selbstsigniertes Zertifikat; erkannt wird er wie
/// jedes Syncthing-Geraet an dessen Hash. Deshalb steht seine Geraete-ID als
/// <c>id=</c> in der Adresse, und deshalb wird sie hier geprueft statt einer
/// Zertifikatskette.
/// </remarks>
public sealed class GlobalDiscovery : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    /// <param name="identity">
    /// Für die Anmeldung nötig: der Server liest die Geräte-ID aus dem
    /// Zertifikat, nicht aus der Nachricht. Zum Abfragen nicht nötig.
    /// </param>
    public GlobalDiscovery(string server, DeviceIdentity? identity = null, TimeSpan? timeout = null)
    {
        var (endpoint, expected) = Split(server);
        _endpoint = endpoint;

        var handler = new SocketsHttpHandler();

        if (expected != DeviceId.Empty)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                DeviceId.FromCertificate(certificate.GetRawCertData()) == expected;
        }

        if (identity is not null)
        {
            handler.SslOptions.ClientCertificates = new X509Certificate2Collection(identity.Certificate);

            // Ohne diese Wahl schickt .NET das Zertifikat nur, wenn der Server
            // eine passende Aussteller-Liste nennt. Unseres ist selbstsigniert
            // und stuende dort nie -- der Server saehe einen anonymen Gast und
            // wiese die Anmeldung ab.
            handler.SslOptions.LocalCertificateSelectionCallback =
                (_, _, _, _, _) => identity.Certificate;
        }

        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Meldet, unter welchen Adressen dieses Gerät zu erreichen ist, und
    /// nennt zurück, wann die nächste Meldung fällig ist.
    /// </summary>
    /// <remarks>
    /// Eine Adresse ohne Host ("tcp://0.0.0.0:22000") heisst: nimm die
    /// Absenderadresse dieser Anmeldung. Welche das von aussen ist, weiss ein
    /// Rechner hinter einem Router nicht.
    /// </remarks>
    public async Task<TimeSpan> AnnounceAsync(
        IEnumerable<string> addresses, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { addresses = addresses.ToArray() });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // Wann wieder, sagt der Server. Sagt er nichts, ist die halbe Stunde
        // die uebliche Antwort.
        if (response.Headers.TryGetValues("Reannounce-After", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);

        return TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Was der Server über dieses Gerät weiss. Eine leere Liste heisst: er
    /// kennt es nicht (mehr) -- kein Fehler, nur keine Auskunft.
    /// </summary>
    public async Task<IReadOnlyList<string>> LookupAsync(DeviceId device, CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString(_endpoint.Query);
        query["device"] = device.ToString();

        var url = new UriBuilder(_endpoint) { Query = query.ToString() }.Uri;

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

        // Ein unbekanntes Geraet ist der Normalfall, kein Ausfall.
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("addresses", out var addresses) ||
            addresses.ValueKind != JsonValueKind.Array)
            return [];

        return [.. addresses.EnumerateArray()
            .Select(a => a.GetString())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!)];
    }

    /// <summary>
    /// Ob dieser Server zum Abfragen gedacht ist.
    /// </summary>
    /// <remarks>
    /// Die Voreinstellung von Syncthing nennt drei Server mit verteilten
    /// Rollen: einer beantwortet Fragen, zwei nehmen Anmeldungen entgegen --
    /// je einer fuer IPv4 und IPv6, denn welche Adresse ein Server sieht,
    /// haengt daran, worueber er angesprochen wird. Die Rollen stehen als
    /// <c>nolookup</c> und <c>noannounce</c> in der Adresse.
    /// </remarks>
    public static bool AllowsLookup(string server) => !HasFlag(server, "nolookup");

    /// <summary>Der Name eines Servers, ohne den Rest der Adresse.</summary>
    public static string HostOf(string server)
    {
        try { return new Uri(server).Host; }
        catch { return server; }
    }

    /// <summary>Ob dieser Server Anmeldungen entgegennimmt.</summary>
    public static bool AllowsAnnounce(string server) => !HasFlag(server, "noannounce");

    private static bool HasFlag(string server, string flag)
    {
        try
        {
            var query = HttpUtility.ParseQueryString(new UriBuilder(server).Query);

            // Eine Flagge steht ohne Wert da; dann landet sie im Schluessel null.
            return query.AllKeys.Contains(flag) ||
                   (query[null]?.Split(',').Contains(flag) ?? false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Trennt die Adresse von der Geraete-ID darin. <c>id=</c> ist eine
    /// Anweisung an uns, nicht an den Server -- sie geht nicht mit hinaus.
    /// </summary>
    private static (Uri Endpoint, DeviceId Expected) Split(string server)
    {
        var builder = new UriBuilder(server);
        var query = HttpUtility.ParseQueryString(builder.Query);

        var id = query["id"];

        // Anweisungen an den Aufrufer, nicht an den Server.
        query.Remove("id");
        query.Remove("noannounce");
        query.Remove("nolookup");
        builder.Query = query.ToString();

        var expected = DeviceId.Empty;
        if (!string.IsNullOrWhiteSpace(id) && !DeviceId.TryParse(id, out expected, out var error))
            throw new FormatException($"Die Geraete-ID des Erkennungsservers ist unbrauchbar: {error}");

        return (builder.Uri, expected);
    }

    public void Dispose() => _http.Dispose();
}
