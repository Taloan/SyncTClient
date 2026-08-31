namespace SyncTClient.Bep;

/// <summary>
/// Meldet dem Erkennungsserver in Abständen, wo dieses Gerät zu erreichen ist.
/// </summary>
/// <remarks>
/// Das Gegenstück zur Abfrage. Wer nur abfragt, findet andere Geräte, wird
/// aber selbst nicht gefunden. Eine Gegenstelle mit der Adresse "dynamic"
/// baut dann nie eine Verbindung zu diesem Gerät auf.
///
/// Der Server erkennt dieses Gerät am Geräte-Zertifikat, nicht an einer
/// mitgeschickten Kennung. Die Anmeldung ist damit genauso belastbar wie die
/// Identität selbst.
/// </remarks>
public sealed class GlobalAnnouncer : IAsyncDisposable
{
    /// <summary>
    /// Wartezeit nach einem Fehlschlag. Ein sofortiger neuer Versuch würde den
    /// Server zusätzlich belasten.
    /// </summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<string> _servers;
    private readonly DeviceIdentity _identity;
    private readonly int _listenPort;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;

    /// <summary>
    /// Die zuletzt ausgegebene Meldung. Ein Server, der eine Woche lang nicht
    /// antwortet, soll das Protokoll nicht mit derselben Zeile füllen.
    /// </summary>
    private string? _lastSaid;

    public GlobalAnnouncer(
        IEnumerable<string> servers, DeviceIdentity identity, int listenPort, Action<string> log)
    {
        _servers = [.. servers];
        _identity = identity;
        _listenPort = listenPort;
        _log = log;
    }

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = RetryAfterFailure;
            var reached = 0;
            var missing = "";
            var reason = "";

            foreach (var server in _servers)
            {
                try
                {
                    using var discovery = new GlobalDiscovery(server, _identity);

                    // Adresse ohne Host: der Server setzt die Adresse ein, von
                    // der die Anmeldung kam. Dieser Rechner kennt seine
                    // Adresse von aussen nicht.
                    var next = await discovery
                        .AnnounceAsync([$"tcp://0.0.0.0:{_listenPort}"], ct)
                        .ConfigureAwait(false);

                    // Es gilt die kuerzeste von allen Servern genannte Frist.
                    if (reached++ == 0 || next < wait) wait = next;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Ein Server fuer IPv6 scheitert ohne IPv6. Das ist kein
                    // Grund, den Server fuer IPv4 auszulassen.
                    missing = Short(server);
                    reason = Reason(ex);
                }
            }

            // Die Meldung bleibt kurz: sie kommt stuendlich wieder, und der
            // Grund ist nur wichtig, wenn kein Server erreichbar war. Die
            // Minuten stehen absichtlich nicht darin. Sie aendern sich jedes
            // Mal, und dann waere jede Meldung eine neue.
            Say(reached > 0
                ? $"Erkennungsserver: angemeldet ({reached}/{_servers.Count})" +
                  (missing.Length > 0 ? $", ohne {missing}." : ".")
                : $"Erkennungsserver: keiner erreichbar — {missing}: {reason}");

            if (reached == 0) wait = RetryAfterFailure;

            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Der erste Namensteil des Servers. Mehr wird in einer Logzeile nicht
    /// gebraucht.
    /// </summary>
    private static string Short(string server)
        => GlobalDiscovery.HostOf(server).Split('.')[0];

    /// <summary>
    /// Ein gekuerzter Fehlergrund. Der vollstaendige Ausnahmetext ist fuer
    /// eine Logzeile zu lang.
    /// </summary>
    private static string Reason(Exception exception)
    {
        var text = exception.Message.Split('\n')[0].Trim();
        return text.Length <= 60 ? text : text[..57].TrimEnd() + "...";
    }

    /// <summary>
    /// Gibt die Meldung nur aus, wenn sie sich seit der letzten Ausgabe
    /// geändert hat.
    /// </summary>
    private void Say(string message)
    {
        if (message == _lastSaid) return;

        _lastSaid = message;
        _log(message);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* beim Beenden belanglos */ }
        }

        _cts.Dispose();
    }
}
