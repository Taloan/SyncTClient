using System.Net.Sockets;
using System.Text.Json;

namespace SyncTClient.Bep;

/// <summary>
/// Meldet dieses Gerät bei einem Relay an und nimmt vermittelte Verbindungen
/// entgegen.
/// </summary>
/// <remarks>
/// Das Gegenstück zu <see cref="RelayClient"/>. Wer nur selbst über einen
/// Relay wählt, erreicht andere — wird aber nicht erreicht. Eine Gegenstelle,
/// die hinter einem Router liegt, kann dann nie zu uns verbinden, und wenn
/// beide Seiten so stehen, kommt nie eine Verbindung zustande.
///
/// Angemeldet wird mit einer ausgehenden Verbindung, die offen bleibt. Sie
/// ist der Weg, auf dem der Relay eine Einladung schicken kann; ohne sie
/// gäbe es keinen. Genau das ist der Zweck: eine offene Leitung nach draußen
/// ersetzt den offenen Port nach innen.
///
/// Solange die Anmeldung steht, trägt <see cref="Adresse"/> die
/// <c>relay://</c>-Adresse, unter der dieses Gerät erreichbar ist. Sie gehört
/// in die Anmeldung beim Erkennungsserver, sonst weiß niemand davon.
/// </remarks>
public sealed class RelayListener : IAsyncDisposable
{
    /// <summary>Woher die Liste der öffentlichen Relays kommt.</summary>
    public const string PoolAdresse = "https://relays.syncthing.net/endpoint";

    /// <summary>So lange darf ein einzelner Schritt dauern.</summary>
    private static readonly TimeSpan Frist = TimeSpan.FromSeconds(15);

    /// <summary>In diesem Abstand geht ein Lebenszeichen hinaus.</summary>
    private static readonly TimeSpan Lebenszeichen = TimeSpan.FromMinutes(1);

    /// <summary>So lange wird gewartet, bevor ein Fehlschlag wiederholt wird.</summary>
    private static readonly TimeSpan NachFehlschlag = TimeSpan.FromMinutes(2);

    private readonly DeviceIdentity _identity;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _schreibsperre = new(1, 1);

    private Task? _lauf;

    public RelayListener(DeviceIdentity identity, Action<string> log)
    {
        _identity = identity;
        _log = log;
    }

    /// <summary>
    /// Eine Gegenstelle hat uns über den Relay erreicht.
    /// </summary>
    /// <remarks>
    /// Der zweite Wert sagt, welche Rolle wir im TLS-Handschlag haben. Wer
    /// die Leitung nicht übernimmt, muss sie schließen.
    /// </remarks>
    public event Action<Stream, bool>? Eingehend;

    /// <summary>
    /// Unter dieser Adresse ist dieses Gerät über den Relay zu erreichen —
    /// oder <c>null</c>, solange keine Anmeldung steht.
    /// </summary>
    public string? Adresse { get; private set; }

    /// <summary>Beginnt, sich anzumelden, und bleibt dabei.</summary>
    /// <param name="vorgabe">
    /// Ein fest eingetragener Relay. Ist nichts angegeben, wird die
    /// öffentliche Liste befragt.
    /// </param>
    public void Start(string? vorgabe = null) => _lauf = Task.Run(() => LaufAsync(vorgabe, _cts.Token));

    private async Task LaufAsync(string? vorgabe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? gewaehlt = null;

            try
            {
                gewaehlt = vorgabe ?? await WaehlenAsync(ct).ConfigureAwait(false);

                if (gewaehlt is null)
                {
                    _log("Relay: kein erreichbarer Relay gefunden.");
                }
                else
                {
                    await AnmeldenUndWartenAsync(gewaehlt, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var wo = gewaehlt is null ? "Relay" : $"Relay {RelayClient.Zerlegen(gewaehlt).Host}";
                _log($"{wo}: {ex.Message}");
            }
            finally
            {
                Adresse = null;
            }

            try { await Task.Delay(NachFehlschlag, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Holt die öffentliche Liste und nimmt den ersten Relay, der antwortet.
    /// </summary>
    /// <remarks>
    /// Nicht den nächstgelegenen und nicht den schnellsten. Die Liste ist
    /// nach Zufall geordnet zurückzugeben — wer immer den ersten nimmt,
    /// belastet denselben Rechner. Deshalb wird gemischt und der erste
    /// genommen, der eine Anmeldung annimmt.
    /// </remarks>
    private async Task<string?> WaehlenAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Frist };

        var text = await http.GetStringAsync(PoolAdresse, ct).ConfigureAwait(false);
        using var pool = JsonDocument.Parse(text);

        if (!pool.RootElement.TryGetProperty("relays", out var liste))
            throw new InvalidDataException("Die Relay-Liste nennt keine Relays.");

        var adressen = new List<string>();

        foreach (var eintrag in liste.EnumerateArray())
            if (eintrag.TryGetProperty("url", out var url) && url.GetString() is { } wert)
                adressen.Add(wert);

        if (adressen.Count == 0) return null;

        _log($"Relay: {adressen.Count} oeffentliche Relays gefunden.");
        return adressen[Random.Shared.Next(adressen.Count)];
    }

    /// <summary>Meldet an und bleibt, bis die Leitung abreißt.</summary>
    private async Task AnmeldenUndWartenAsync(string adresse, CancellationToken ct)
    {
        var relay = RelayClient.Zerlegen(adresse);

        using var tcp = new TcpClient { NoDelay = true };

        await tcp.ConnectAsync(relay.Host, relay.Port, ct)
            .AsTask().WaitAsync(Frist, ct).ConfigureAwait(false);

        var tls = await BepTls
            .ConnectAsync(tcp.GetStream(), _identity, ct, BepTls.RelayProtocolName)
            .ConfigureAwait(false);

        if (relay.Relay != DeviceId.Empty)
        {
            var gemessen = DeviceId.FromCertificate(tls.PeerCertificate);
            if (gemessen != relay.Relay)
                throw new InvalidDataException(
                    $"nennt sich {relay.Relay.Short()}, sein Zertifikat sagt {gemessen.Short()}.");
        }

        await SendenAsync(tls.Stream, RelayProtocol.Art.BeimRelayAnmelden, [], ct).ConfigureAwait(false);

        var antwort = await RelayProtocol.LesenAsync(tls.Stream, ct)
            .WaitAsync(Frist, ct).ConfigureAwait(false);

        if (antwort.Art == RelayProtocol.Art.RelayVoll)
            throw new IOException("ist ausgelastet.");

        if (antwort.Art != RelayProtocol.Art.Antwort)
            throw new InvalidDataException($"antwortet auf die Anmeldung mit {antwort.Art}.");

        var geprueft = RelayProtocol.AntwortLesen(antwort.Rumpf);
        if (geprueft.Code != 0)
            throw new IOException($"lehnt die Anmeldung ab: {geprueft.Meldung} (Code {geprueft.Code}).");

        Adresse = adresse;
        _log($"Relay {relay.Host}: angemeldet. Dieses Geraet ist darueber erreichbar.");

        using var eigenerAbbruch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var puls = Task.Run(() => PulsAsync(tls.Stream, eigenerAbbruch.Token), eigenerAbbruch.Token);

        try
        {
            await HoerenAsync(tls.Stream, relay, eigenerAbbruch.Token).ConfigureAwait(false);
        }
        finally
        {
            await eigenerAbbruch.CancelAsync().ConfigureAwait(false);
            try { await puls.ConfigureAwait(false); } catch (Exception) { /* endet mit dem Abbruch */ }
        }
    }

    /// <summary>Nimmt entgegen, was der Relay schickt.</summary>
    private async Task HoerenAsync(Stream strom, RelayClient.Adresse relay, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var nachricht = await RelayProtocol.LesenAsync(strom, ct).ConfigureAwait(false);

            switch (nachricht.Art)
            {
                case RelayProtocol.Art.Ping:
                    await SendenAsync(strom, RelayProtocol.Art.Pong, [], ct).ConfigureAwait(false);
                    break;

                case RelayProtocol.Art.Pong:
                    break;

                case RelayProtocol.Art.Einladung:
                    var einladung = RelayProtocol.EinladungLesen(nachricht.Rumpf);
                    var wer = DeviceId.FromBytes(einladung.Von);

                    _log($"Relay {relay.Host}: {wer.Short()} moechte sich verbinden.");

                    // Nebenher. Waehrend die Leitung aufgebaut wird, koennen
                    // weitere Einladungen kommen, und der Relay erwartet
                    // weiterhin Antworten auf seine Lebenszeichen.
                    _ = Task.Run(() => LeitungAnnehmenAsync(relay, einladung, ct), ct);
                    break;

                case RelayProtocol.Art.RelayVoll:
                    throw new IOException("ist ausgelastet und trennt die Anmeldung.");

                default:
                    break;
            }
        }
    }

    /// <summary>Baut die eingeladene Leitung auf und reicht sie weiter.</summary>
    private async Task LeitungAnnehmenAsync(
        RelayClient.Adresse relay, RelayProtocol.Einladung einladung, CancellationToken ct)
    {
        var host = string.IsNullOrEmpty(einladung.Adresse) ? relay.Host : einladung.Adresse;
        TcpClient? tcp = null;

        try
        {
            tcp = new TcpClient { NoDelay = true };

            await tcp.ConnectAsync(host, einladung.Port, ct)
                .AsTask().WaitAsync(Frist, ct).ConfigureAwait(false);

            var strom = tcp.GetStream();

            await RelayProtocol.SendenAsync(
                strom, RelayProtocol.Art.SitzungBeitreten,
                RelayProtocol.SitzungBeitretenRumpf(einladung.Schluessel), ct).ConfigureAwait(false);

            var nachricht = await RelayProtocol.LesenAsync(strom, ct)
                .WaitAsync(Frist, ct).ConfigureAwait(false);

            if (nachricht.Art != RelayProtocol.Art.Antwort)
                throw new InvalidDataException($"Auf den Beitritt kam {nachricht.Art}.");

            var antwort = RelayProtocol.AntwortLesen(nachricht.Rumpf);
            if (antwort.Code != 0)
                throw new IOException($"{antwort.Meldung} (Code {antwort.Code})");

            var uebernommen = tcp;
            tcp = null;

            // Wer die Leitung nicht uebernimmt, muss sie schliessen. Steht
            // kein Empfaenger bereit, geschieht das hier.
            if (Eingehend is { } empfaenger)
                empfaenger(new RelayStrom(uebernommen), einladung.AlsServer);
            else
                uebernommen.Dispose();
        }
        catch (Exception ex)
        {
            _log($"Relay {relay.Host}: die eingeladene Leitung kam nicht zustande: {ex.Message}");
        }
        finally
        {
            tcp?.Dispose();
        }
    }

    /// <summary>
    /// Schickt in Abständen ein Lebenszeichen.
    /// </summary>
    /// <remarks>
    /// Nicht wegen des Relays — der fragt selbst. Sondern wegen des Routers
    /// davor: eine Verbindung, über die nichts läuft, fällt nach einigen
    /// Minuten aus seiner Übersetzungstabelle, und danach erreicht uns keine
    /// Einladung mehr, ohne dass eine Seite einen Fehler sähe.
    /// </remarks>
    private async Task PulsAsync(Stream strom, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Lebenszeichen, ct).ConfigureAwait(false);
                await SendenAsync(strom, RelayProtocol.Art.Ping, [], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sendet unter einer Sperre.
    /// </summary>
    /// <remarks>
    /// Das Lebenszeichen und die Antwort auf ein Ping laufen nebeneinander.
    /// Zwei Schreibvorgänge, die sich überschneiden, ergäben eine Nachricht,
    /// die niemand mehr lesen kann.
    /// </remarks>
    private async Task SendenAsync(
        Stream strom, RelayProtocol.Art art, byte[] rumpf, CancellationToken ct)
    {
        await _schreibsperre.WaitAsync(ct).ConfigureAwait(false);

        try { await RelayProtocol.SendenAsync(strom, art, rumpf, ct).ConfigureAwait(false); }
        finally { _schreibsperre.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_lauf is not null)
        {
            try { await _lauf.ConfigureAwait(false); }
            catch (Exception) { /* beendet sich */ }
        }

        _cts.Dispose();
        _schreibsperre.Dispose();
    }

    /// <summary>Der Strom einer vermittelten Leitung, der seinen Socket mitnimmt.</summary>
    private sealed class RelayStrom(TcpClient tcp) : Stream
    {
        private readonly NetworkStream _strom = tcp.GetStream();

        public override bool CanRead => _strom.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _strom.CanWrite;
        public override bool CanTimeout => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int ReadTimeout
        {
            get => _strom.ReadTimeout;
            set => _strom.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => _strom.WriteTimeout;
            set => _strom.WriteTimeout = value;
        }

        public override void Flush() => _strom.Flush();

        public override Task FlushAsync(CancellationToken ct) => _strom.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count)
            => _strom.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _strom.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _strom.ReadAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count)
            => _strom.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _strom.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _strom.WriteAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _strom.Dispose();
                tcp.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
