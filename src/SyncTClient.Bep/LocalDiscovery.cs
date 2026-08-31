using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Google.Protobuf;
using SyncTClient.Bep.Proto;

namespace SyncTClient.Bep;

/// <summary>
/// Die Erkennung im eigenen Netz: rufen und zuhören.
/// </summary>
/// <remarks>
/// Ohne diese Hälfte bleibt ein Gerät unsichtbar. Eine Gegenstelle, bei der
/// als Adresse "dynamic" steht, sucht genau hier -- findet sie nichts, ruft
/// sie nie an, und die Frage "möchte sich verbinden" stellt sich nie.
///
/// Das Paket ist ein Magic und ein Protobuf, verschickt an den Rundruf des
/// Netzes. Wer eine Adresse ohne Host nennt ("tcp://0.0.0.0:22000"), meint
/// die Absenderadresse seines Pakets -- die eigene IP kennt ein Rechner mit
/// mehreren Netzwerkkarten nicht zuverlaessig, der Empfaenger dagegen schon.
/// </remarks>
public sealed class LocalDiscovery : IAsyncDisposable
{
    /// <summary>Derselbe Port wie bei Syncthing -- sonst hoert uns niemand.</summary>
    public const int Port = 21027;

    /// <summary>Dasselbe Magic wie vor dem Hello.</summary>
    private const uint Magic = 0x2EA7D90B;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Nach dieser Zeit ohne Lebenszeichen gilt ein Geraet als weg.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(90);

    private readonly DeviceIdentity _identity;
    private readonly int _listenPort;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ulong _instance = (ulong)Random.Shared.NextInt64(1, long.MaxValue);

    private readonly Dictionary<string, Entry> _seen = new(StringComparer.OrdinalIgnoreCase);

    private UdpClient? _socket;
    private Task? _receiveLoop;
    private Task? _announceLoop;

    private sealed record Entry(IReadOnlyList<string> Addresses, DateTime Seen);

    public LocalDiscovery(DeviceIdentity identity, int listenPort, Action<string> log)
    {
        _identity = identity;
        _listenPort = listenPort;
        _log = log;
    }

    /// <summary>Ein Gerät hat sich zum ersten Mal gemeldet.</summary>
    public event Action<DeviceId, string>? Discovered;

    /// <summary>
    /// Beginnt zu rufen und zuzuhören. Ein belegter Port ist kein Grund
    /// aufzugeben -- rufen laesst sich auch ohne zuzuhoeren.
    /// </summary>
    public bool Start()
    {
        try
        {
            // Ohne ReuseAddress kaeme ein echtes Syncthing auf demselben
            // Rechner nicht mehr an den Port -- oder wir nicht.
            _socket = new UdpClient { EnableBroadcast = true };
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));

            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _announceLoop = Task.Run(() => AnnounceLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            _log($"Lokale Erkennung nicht moeglich: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unter welchen Adressen dieses Gerät zuletzt im Netz gesehen wurde.
    /// </summary>
    public IReadOnlyList<string> AddressesFor(DeviceId device)
    {
        var key = device.ToString();

        lock (_seen)
        {
            if (!_seen.TryGetValue(key, out var entry)) return [];

            // Eine alte Auskunft ist schlechter als keine: sie schickt uns an
            // eine Adresse, an der schon lange niemand mehr ist.
            return DateTime.UtcNow - entry.Seen > Lifetime ? [] : entry.Addresses;
        }
    }

    // ------------------------------------------------------------ Rufen

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { SendAnnounce(); }
            catch (Exception ex) { _log($"Lokaler Ruf ging nicht hinaus: {ex.Message}"); }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SendAnnounce()
    {
        var announce = new Announce
        {
            Id = ByteString.CopyFrom(_identity.Id.Span),
            InstanceId = _instance
        };

        // Die eigenen Adressen ausdruecklich nennen. Der Empfaenger duerfte
        // sie sonst aus dem Absender des Pakets nehmen -- und der ist falsch,
        // sobald etwas dazwischen umschreibt: ein Container mit eigener
        // Bruecke sieht dann seine Bruecke statt uns und ruft sich selbst an.
        foreach (var address in LocalAddresses())
            announce.Addresses.Add($"tcp://{address}:{_listenPort}");

        // Kennt der Rechner keine brauchbare eigene Adresse, bleibt nur der
        // Absender.
        if (announce.Addresses.Count == 0)
            announce.Addresses.Add($"tcp://0.0.0.0:{_listenPort}");

        var payload = announce.ToByteArray();
        var packet = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0), Magic);
        payload.CopyTo(packet.AsSpan(4));

        foreach (var target in BroadcastAddresses())
        {
            // Ein Netz, das gerade nicht erreichbar ist, darf die anderen
            // nicht aufhalten.
            try { _socket!.Send(packet, packet.Length, new IPEndPoint(target, Port)); }
            catch (SocketException) { /* dieses Netz eben nicht */ }
        }
    }

    /// <summary>Die eigenen IPv4-Adressen, unter denen wir ansprechbar sind.</summary>
    private static IEnumerable<IPAddress> LocalAddresses()
        => OwnIPv4().Select(u => u.Address);

    /// <summary>
    /// Der allgemeine Rundruf und der jedes einzelnen Netzes.
    /// </summary>
    /// <remarks>
    /// 255.255.255.255 geht bei mehreren Netzwerkkarten nur ueber eine davon.
    /// Deshalb zusaetzlich der gerichtete Rundruf je Karte -- ausgerechnet aus
    /// Adresse und Maske.
    /// </remarks>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;

        foreach (var unicast in OwnIPv4())
        {
            if (unicast.IPv4Mask is null) continue;

            var address = unicast.Address.GetAddressBytes();
            var mask = unicast.IPv4Mask.GetAddressBytes();
            if (mask.Length != 4) continue;

            var broadcast = new byte[4];
            for (var i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | (byte)~mask[i]);

            yield return new IPAddress(broadcast);
        }
    }

    /// <summary>Jede IPv4-Adresse einer Netzwerkkarte, die etwas taugt.</summary>
    private static IEnumerable<UnicastIPAddressInformation> OwnIPv4()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                // 169.254.x.x heisst "keine Adresse bekommen" -- darunter ist
                // niemand zu erreichen.
                var bytes = unicast.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;

                yield return unicast;
            }
        }
    }

    // ------------------------------------------------------------ Zuhören

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"Lokale Erkennung hoert nicht mehr zu: {ex.Message}");
                return;
            }

            try { Accept(received); }
            catch { /* ein unverstaendliches Paket ist kein Ereignis */ }
        }
    }

    private void Accept(UdpReceiveResult received)
    {
        var buffer = received.Buffer;
        if (buffer.Length < 5) return;
        if (BinaryPrimitives.ReadUInt32BigEndian(buffer) != Magic) return;

        var announce = Announce.Parser.ParseFrom(buffer.AsSpan(4).ToArray());
        if (announce.Id.Length != DeviceId.Length) return;

        var device = DeviceId.FromBytes(announce.Id.Span);

        // Uns selbst zu finden waere nichts wert -- und der Rundruf kommt bei
        // uns selbst wieder an.
        if (device == _identity.Id) return;

        var addresses = announce.Addresses
            .Select(a => WithSource(a, received.RemoteEndPoint.Address))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        if (addresses.Count == 0) return;

        bool isNew;
        lock (_seen)
        {
            isNew = !_seen.ContainsKey(device.ToString());
            _seen[device.ToString()] = new Entry(addresses, DateTime.UtcNow);
        }

        if (isNew)
        {
            _log($"Im Netz gesehen: {device.Short()} unter {string.Join(", ", addresses)}");
            Discovered?.Invoke(device, addresses[0]);
        }
    }

    /// <summary>
    /// Setzt die Absenderadresse ein, wo der Rufer keine nennen konnte.
    /// </summary>
    private static string? WithSource(string address, IPAddress source)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;

        // Nur TCP: alles andere koennen wir ohnehin nicht anwaehlen.
        if (!uri.Scheme.StartsWith("tcp", StringComparison.OrdinalIgnoreCase)) return null;

        var host = uri.Host;
        var unspecified = host.Length == 0 || host is "0.0.0.0" or "::";

        var port = uri.Port > 0 ? uri.Port : 22000;
        return unspecified ? $"tcp://{source}:{port}" : $"tcp://{host}:{port}";
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose();

        foreach (var task in new[] { _receiveLoop, _announceLoop })
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); }
            catch { /* beim Beenden belanglos */ }
        }

        _cts.Dispose();
    }
}
