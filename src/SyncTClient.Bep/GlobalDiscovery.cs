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
/// Eine feste Adresse setzt voraus, dass die Gegenstelle eine hat. Ein Gerät
/// ohne nach aussen geoeffneten Port hat keine. Es meldet dem
/// Erkennungsserver stattdessen, ueber welchen Relay es erreichbar ist.
///
/// Der Server traegt ein selbstsigniertes Zertifikat und wird wie jedes
/// Syncthing-Geraet an dessen Hash erkannt. Deshalb steht seine Geraete-ID
/// als <c>id=</c> in der Adresse, und deshalb wird sie hier geprueft statt
/// einer Zertifikatskette.
/// </remarks>
public sealed class GlobalDiscovery : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    /// <param name="identity">
    /// Für die Anmeldung nötig. Der Server liest die Geräte-ID aus dem
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

            // Ohne diesen Rueckruf schickt .NET das Zertifikat nur, wenn der
            // Server eine passende Aussteller-Liste nennt. Unser Zertifikat
            // ist selbstsigniert und steht dort nie. Der Server saehe dann
            // eine unbekannte Gegenstelle und wiese die Anmeldung ab.
            handler.SslOptions.LocalCertificateSelectionCallback =
                (_, _, _, _, _) => identity.Certificate;
        }

        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Meldet, unter welchen Adressen dieses Gerät zu erreichen ist, und
    /// liefert zurück, wann die nächste Meldung fällig ist.
    /// </summary>
    /// <remarks>
    /// Eine Adresse ohne Host ("tcp://0.0.0.0:22000") bedeutet, dass der
    /// Server die Absenderadresse dieser Anmeldung einsetzt. Ein Rechner
    /// hinter einem Router kennt seine Adresse von aussen nicht.
    /// </remarks>
    public async Task<TimeSpan> AnnounceAsync(
        IEnumerable<string> addresses, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { addresses = addresses.ToArray() });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // Der Server nennt den Zeitpunkt der naechsten Meldung. Fehlt die
        // Angabe, gilt die uebliche halbe Stunde.
        if (response.Headers.TryGetValues("Reannounce-After", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);

        return TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Die Adressen, die der Server zu diesem Gerät kennt. Eine leere Liste
    /// bedeutet, dass er das Gerät nicht (mehr) kennt. Das ist kein Fehler,
    /// sondern eine fehlende Auskunft.
    /// </summary>
    public async Task<IReadOnlyList<string>> LookupAsync(DeviceId device, CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString(_endpoint.Query);
        query["device"] = device.ToString();

        var url = new UriBuilder(_endpoint) { Query = query.ToString() }.Uri;

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

        // Ein unbekanntes Geraet ist der Normalfall und kein Fehler.
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
    /// Rollen: einer beantwortet Abfragen, zwei nehmen Anmeldungen entgegen,
    /// je einer fuer IPv4 und IPv6. Welche Adresse ein Server sieht, haengt
    /// davon ab, ueber welches Protokoll er angesprochen wird. Die Rollen
    /// stehen als <c>nolookup</c> und <c>noannounce</c> in der Adresse.
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

            // Eine Flagge steht ohne Wert in der Adresse. Sie landet dann
            // unter dem Schluessel null.
            return query.AllKeys.Contains(flag) ||
                   (query[null]?.Split(',').Contains(flag) ?? false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Trennt die Adresse von der darin enthaltenen Geraete-ID. <c>id=</c> ist
    /// eine Angabe fuer den Aufrufer, nicht fuer den Server, und wird deshalb
    /// aus der Adresse entfernt.
    /// </summary>
    private static (Uri Endpoint, DeviceId Expected) Split(string server)
    {
        var builder = new UriBuilder(server);
        var query = HttpUtility.ParseQueryString(builder.Query);

        var id = query["id"];

        // Angaben fuer den Aufrufer, nicht fuer den Server.
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
