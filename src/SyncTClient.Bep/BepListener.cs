using System.Net;
using System.Net.Sockets;

namespace SyncTClient.Bep;

/// <summary>
/// Nimmt eingehende Verbindungen anderer Geräte entgegen.
/// </summary>
/// <remarks>
/// Ohne diesen Teil baut der Client nur ausgehende Verbindungen auf. Dann
/// erfährt er nie, dass ein anderes Gerät sich verbinden möchte. Die Anfrage
/// "möchte sich verbinden" erreicht nur die Seite, die die Verbindung
/// entgegennimmt.
///
/// Der Handschlag wird hier vollstaendig abgeschlossen, bevor der Benutzer
/// gefragt wird. Erst wenn TLS steht und das Hello ausgetauscht ist, steht
/// fest, welches Gerät die Verbindung aufbaut. Eine Gegenstelle, die dabei
/// nichts sendet, wird nach Ablauf der Frist abgebrochen und haelt keine
/// Verbindung offen.
/// </remarks>
public sealed class BepListener : IAsyncDisposable
{
    /// <summary>So lange darf eine Gegenstelle fuer TLS und Hello brauchen.</summary>
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

    /// <summary>
    /// Eine Gegenstelle hat sich verbunden und den Handschlag bestanden.
    /// </summary>
    /// <remarks>
    /// Wird aus dem Threadpool aufgerufen. Wer die Verbindung nicht
    /// uebernimmt, muss sie schliessen. Andernfalls bleibt sie offen.
    /// </remarks>
    public event Action<BepConnection, IPEndPoint?>? Incoming;

    /// <summary>
    /// Der Port, auf dem tatsaechlich gelauscht wird. 0 bedeutet, dass nicht
    /// gelauscht wird.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Beginnt zu lauschen. Ein belegter Port ist kein Grund abzubrechen. Der
    /// Client kann weiterhin selbst Verbindungen aufbauen, nimmt dann aber
    /// keine entgegen.
    /// </summary>
    public bool Start(int port)
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, port);

            // Ein Socket fuer beide Adressfamilien. Sonst erreichen uns keine
            // Verbindungen ueber IPv4.
            _listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            _listener.Start();

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            _log($"Port {port} laesst sich nicht belegen ({ex.Message}). Eingehende Verbindungen sind nicht moeglich.");
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
                _log($"Nehme keine Verbindungen mehr entgegen: {ex.Message}");
                return;
            }

            // Der Handschlag darf die Schleife nicht aufhalten. Eine langsame
            // Gegenstelle wuerde sonst alle anderen blockieren.
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
            _log($"Verbindung von {remote?.Address.ToString() ?? "unbekannt"} kam nicht zustande: {Ursache(ex)}");
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
    /// <summary>
    /// Die Ausnahme samt ihrer Ursachen, in einer Zeile.
    /// </summary>
    /// <remarks>
    /// "Authentication failed, see inner exception." ist die Aufforderung,
    /// nachzusehen -- und genau die Auskunft, die verlorenging. Ein
    /// gescheiterter Handschlag traegt seinen Grund erst zwei Ebenen tiefer:
    /// welche Fassung, welches Verfahren, welche Ablehnung.
    /// </remarks>
    private static string Ursache(Exception ex)
    {
        var teile = new List<string>(3);

        for (Exception? e = ex; e is not null && teile.Count < 3; e = e.InnerException)
            teile.Add(e.Message);

        return string.Join(" -- ", teile);
    }

}
