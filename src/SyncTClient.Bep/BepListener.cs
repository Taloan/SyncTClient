using System.Net;
using System.Net.Sockets;

namespace SyncTClient.Bep;

/// <summary>
/// Nimmt Anrufe anderer Geräte entgegen.
/// </summary>
/// <remarks>
/// Ohne diesen Teil kennt der Client nur eine Richtung: er ruft an. Wer nur
/// anruft, erfährt nie, dass ihn jemand kennenlernen möchte -- die Frage
/// "möchte sich verbinden" kann nur stellen, wer angerufen wird.
///
/// Der Handschlag laeuft hier zu Ende, bevor irgendjemand gefragt wird: erst
/// wenn TLS steht und das Hello ausgetauscht ist, steht fest, <em>wer</em>
/// anruft. Ein Anrufer, der dabei schweigt, faellt nach kurzer Zeit heraus
/// und haelt keine Verbindung offen.
/// </remarks>
public sealed class BepListener : IAsyncDisposable
{
    /// <summary>So lange darf ein Anrufer fuer TLS und Hello brauchen.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly DeviceIdentity _identity;
    private readonly string _deviceName;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _loop;

    public BepListener(DeviceIdentity identity, string deviceName, Action<string> log)
    {
        _identity = identity;
        _deviceName = deviceName;
        _log = log;
    }

    /// <summary>Eine Gegenstelle hat angerufen und den Handschlag bestanden.</summary>
    /// <remarks>
    /// Kommt aus dem Threadpool. Wer die Verbindung nicht uebernimmt, muss sie
    /// schliessen -- sonst bleibt sie stehen.
    /// </remarks>
    public event Action<BepConnection, IPEndPoint?>? Incoming;

    /// <summary>Der Port, auf dem tatsaechlich gelauscht wird. 0 heisst: gar nicht.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Beginnt zu lauschen. Ein belegter Port ist kein Grund aufzugeben -- der
    /// Client kann weiter selbst anrufen, er wird nur nicht angerufen.
    /// </summary>
    public bool Start(int port)
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, port);

            // Ein Socket fuer beide Familien; sonst blieben IPv4-Anrufer aussen vor.
            _listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            _listener.Start();

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            _log($"Port {port} laesst sich nicht belegen ({ex.Message}). Eingehende Anrufe bleiben aus.");
            _listener = null;
            Port = 0;
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"Warte nicht mehr auf Anrufe: {ex.Message}");
                return;
            }

            // Der Handschlag darf die Schleife nicht aufhalten: ein zaeher
            // Anrufer wuerde sonst alle anderen blockieren.
            _ = Task.Run(() => HandshakeAsync(tcp, ct), CancellationToken.None);
        }
    }

    private async Task HandshakeAsync(TcpClient tcp, CancellationToken ct)
    {
        var remote = tcp.Client.RemoteEndPoint as IPEndPoint;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);

            var connection = await BepConnection
                .AcceptAsync(tcp, _identity, _deviceName, timeout.Token)
                .ConfigureAwait(false);

            if (Incoming is null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Incoming.Invoke(connection, remote);
        }
        catch (Exception ex)
        {
            _log($"Anruf von {remote?.Address.ToString() ?? "unbekannt"} kam nicht zustande: {ex.Message}");
            tcp.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* beim Beenden belanglos */ }
        }

        _cts.Dispose();
    }
}
