namespace SyncTClient.Bep;

/// <summary>
/// Meldet dem Erkennungsserver in Abständen, wo dieses Gerät zu erreichen ist.
/// </summary>
/// <remarks>
/// Das Gegenstück zur Abfrage. Wer nur abfragt, findet andere -- gefunden
/// wird er nicht, und eine Gegenstelle mit der Adresse "dynamic" ruft ihn
/// darum nie an.
///
/// Der Server erkennt uns am Geräte-Zertifikat, nicht an einer mitgeschickten
/// Kennung: die Anmeldung ist damit so echt wie die Identität selbst.
/// </remarks>
public sealed class GlobalAnnouncer : IAsyncDisposable
{
    /// <summary>Nach einem Fehlschlag nicht sofort wieder -- der Server hat genug Gäste.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<string> _servers;
    private readonly DeviceIdentity _identity;
    private readonly int _listenPort;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;

    /// <summary>
    /// Was zuletzt gesagt wurde. Ein Server, der eine Woche lang schweigt,
    /// soll das Protokoll nicht mit derselben Zeile füllen.
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

                    // Ohne Host: der Server setzt die Adresse ein, von der die
                    // Anmeldung kam. Von innen kennen wir sie nicht.
                    var next = await discovery
                        .AnnounceAsync([$"tcp://0.0.0.0:{_listenPort}"], ct)
                        .ConfigureAwait(false);

                    // Der ungeduldigste Server gibt den Takt vor.
                    if (reached++ == 0 || next < wait) wait = next;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Ein Server fuer IPv6 scheitert ohne IPv6 -- das ist kein
                    // Grund, den fuer IPv4 auszulassen.
                    missing = Short(server);
                    reason = Reason(ex);
                }
            }

            // Kurz halten: diese Zeile kommt stuendlich wieder, und der Grund
            // interessiert nur, wenn gar nichts ging. Die Minuten stehen
            // absichtlich nicht darin -- sie aendern sich jedes Mal, und dann
            // waere jede Meldung eine neue.
            Say(reached > 0
                ? $"Erkennungsserver: angemeldet ({reached}/{_servers.Count})" +
                  (missing.Length > 0 ? $", ohne {missing}." : ".")
                : $"Erkennungsserver: keiner erreichbar — {missing}: {reason}");

            if (reached == 0) wait = RetryAfterFailure;

            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Der erste Namensteil des Servers -- mehr braucht eine Logzeile nicht.</summary>
    private static string Short(string server)
        => GlobalDiscovery.HostOf(server).Split('.')[0];

    /// <summary>Ein Grund in wenigen Worten; die ganze Ausnahme sprengt jede Zeile.</summary>
    private static string Reason(Exception exception)
    {
        var text = exception.Message.Split('\n')[0].Trim();
        return text.Length <= 60 ? text : text[..57].TrimEnd() + "...";
    }

    /// <summary>Sagt es einmal -- und wieder, sobald sich etwas geändert hat.</summary>
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
