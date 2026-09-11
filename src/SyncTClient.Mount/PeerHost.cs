using System.Collections.Concurrent;
using Google.Protobuf;
using SyncTClient.Bep;
using SyncTClient.Bep.Proto;

namespace SyncTClient.Mount;

public enum PeerState
{
    Getrennt,
    Verbindet,
    Verbunden,
    Fehler
}

/// <summary>Ein Ordner, den die Gegenstelle mit uns teilt.</summary>
/// <param name="Accepted">Ob wir ihn schon uebernommen haben.</param>
public sealed record OfferedFolder(string FolderId, string Label, bool Accepted)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? FolderId : $"{Label} ({FolderId})";
}

/// <summary>
/// Eine Gegenstelle mit allen Ordnern, die von ihr kommen.
/// </summary>
/// <remarks>
/// Es gibt eine Verbindung je Geraet, nicht je Ordner. So macht es Syncthing,
/// und nur so laesst sich feststellen, was eine Gegenstelle ueberhaupt
/// anbietet. Sie zaehlt die Ordner im ClusterConfig auf, den sie gleich nach
/// dem Hello schickt.
/// </remarks>
public sealed class PeerHost : IAsyncDisposable
{
    private readonly PeerConfig _config;
    private readonly AppConfig _app;
    private readonly DeviceIdentity _identity;
    private readonly Action<string> _log;
    // Die Freigaben werden aus mehreren Threads gelesen: aus der Leseschleife
    // (Route) und, seit die Gegenstelle Bloecke anfordern kann, aus dem
    // Threadpool. Ein Dictionary vertraegt gleichzeitiges Lesen und Schreiben
    // nicht. Geschrieben wird beim Uebernehmen und beim Loesen.
    private readonly ConcurrentDictionary<string, ShareHost> _shares = new(StringComparer.Ordinal);

    private BepConnection? _connection;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<ClusterConfig> _clusterConfig = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PeerState _state = PeerState.Getrennt;

    /// <param name="registry">
    /// Woher die Ordner kommen. Mehrere Gegenstellen teilen sich dieselben
    /// Objekte; ohne diese Ablage legte jede ihre eigenen an, und zwei
    /// Teilnehmer eines Ordners haetten zwei Sync-Roots und zwei Schreiber
    /// auf einer Datenbank.
    /// </param>
    public PeerHost(
        PeerConfig config, AppConfig app, DeviceIdentity identity, Action<string> log,
        ShareRegistry? registry = null)
    {
        _config = config;
        _app = app;
        _identity = identity;
        _log = log;
        _registry = registry ?? new ShareRegistry(app, identity, log);
    }

    private readonly ShareRegistry _registry;

    public PeerConfig Config => _config;
    public string DeviceId => _config.DeviceId;

    /// <summary>Wie sich die Gegenstelle selbst nennt.</summary>
    public string ReportedName { get; private set; } = "";

    public string ClientVersion { get; private set; } = "";

    public string Display => string.IsNullOrWhiteSpace(_config.Name)
        ? (string.IsNullOrWhiteSpace(ReportedName) ? _config.ShortId : ReportedName)
        : _config.Name;

    public PeerState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(value); }
    }

    public string? LastError { get; private set; }

    /// <summary>Was die Gegenstelle mit uns teilt, auch die noch nicht uebernommenen Ordner.</summary>
    public IReadOnlyList<OfferedFolder> Offered { get; private set; } = [];

    public IReadOnlyCollection<ShareHost> Shares => [.. _shares.Values];

    /// <summary>Die laufende Verbindung, oder null, solange keine besteht.</summary>
    public BepConnection? Connection => _connection;

    /// <summary>Bytes, die seit dem Verbinden ueber diese Verbindung liefen.</summary>
    public (long Read, long Written) Wire =>
        _connection is null ? (0, 0) : (_connection.BytesRead, _connection.BytesWritten);

    public event Action<PeerState>? StateChanged;
    public event Action? OfferedChanged;

    /// <summary>
    /// Nimmt einen Ordner in die eigene Liste und hoert auf seine Verbindungen.
    /// </summary>
    /// <remarks>
    /// Nur beim ersten Mal wird das Ereignis abonniert. Ein Ordner kann
    /// mehrfach durch diese Stelle laufen -- beim Fortsetzen etwa --, und
    /// zwei Abonnements meldeten denselben Ausfall zweimal.
    /// </remarks>
    private void Uebernehmen(ShareHost host)
    {
        if (_shares.TryAdd(host.FolderId, host)) host.LineLost += OnLineLost;
        else _shares[host.FolderId] = host;
    }

    /// <summary>
    /// Eine Verbindung zu dieser Gegenstelle ist geschlossen. Also ist die Gegenstelle
    /// getrennt, auch wenn der Leser es noch nicht bemerkt hat.
    /// </summary>
    /// <remarks>
    /// Ohne diesen Schluss stuende die Gegenstelle weiter als verbunden da,
    /// waehrend jede Ankuendigung scheitert -- und weil sie als verbunden
    /// gilt, versucht auch niemand, neu zu verbinden.
    /// </remarks>
    private void OnLineLost(string device)
    {
        if (!string.Equals(device, DeviceId, StringComparison.OrdinalIgnoreCase)) return;
        if (State != PeerState.Verbunden) return;

        _log($"[{Display}] Verbindung geschlossen, gilt als getrennt.");

        _ = Task.Run(async () =>
        {
            try { await DisconnectAsync(); }
            catch (Exception ex) { _log($"[{Display}] Trennen: {ex.Message}"); }
        });
    }

    public ShareHost? ShareFor(string folderId)
        => _shares.TryGetValue(folderId, out var share) ? share : null;

    // ------------------------------------------------------------ Verbinden

    public async Task ConnectAsync(IEnumerable<ShareConfig> shares, CancellationToken ct = default)
    {
        if (State is PeerState.Verbunden or PeerState.Verbindet) return;

        var token = Begin(ct);

        try
        {
            await RunSessionAsync(await DialAsync(token), shares, token);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Baut die Verbindung zur Gegenstelle auf, unter der eingetragenen
    /// Adresse oder unter den Adressen aus der Erkennung.
    /// </summary>
    /// <remarks>
    /// Mehrere Adressen sind der Normalfall: ein Geraet meldet seine lokale
    /// Adresse, seine oeffentliche und die seines Relays. Verwendet wird die
    /// erste, ueber die eine Verbindung zustande kommt.
    /// </remarks>
    /// <summary>So lange darf ein einzelner Versuch dauern.</summary>
    /// <remarks>
    /// Ohne eigene Frist entscheidet das Betriebssystem, und das laesst einen
    /// Verbindungsversuch an eine tote Adresse gut zwanzig Sekunden laufen.
    /// </remarks>
    private static readonly TimeSpan Versuchsfrist = TimeSpan.FromSeconds(10);

    /// <summary>Dasselbe für einen Weg über einen Relay.</summary>
    /// <remarks>
    /// Mehr, weil mehr geschieht: eine Verbindung zum Relay mit TLS, die
    /// Anfrage, die Einladung, eine zweite Verbindung zur vermittelten
    /// Leitung, und darauf noch einmal der Handschlag zwischen den beiden
    /// Geräten. Zwei Handschläge und drei Wege hin und zurück — mit zehn
    /// Sekunden bräche ein Weg ab, der nur langsam ist.
    /// </remarks>
    private static readonly TimeSpan Relayfrist = TimeSpan.FromSeconds(25);

    private async Task<BepConnection> DialAsync(CancellationToken ct)
    {
        var expected = DeviceId.Length > 0 ? Bep.DeviceId.Parse(DeviceId) : Bep.DeviceId.Empty;
        var candidates = await CandidatesAsync(expected, ct);

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "keine Adresse bekannt -- weder eingetragen noch von der Erkennung genannt.");

        // Zwei Gruppen, nacheinander. Innerhalb einer Gruppe gleichzeitig.
        //
        // Ein Relay reicht die Bytes ueber einen fremden Rechner und ist
        // langsamer als jeder direkte Weg. Die Oberflaeche sagt zu, dass er
        // nur verwendet wird, wenn keine direkte Verbindung zustande kommt --
        // deshalb kommt er erst dran, wenn alle direkten gescheitert sind.
        //
        // Gleichzeitig innerhalb der Gruppe, weil es sonst dauert: die
        // Erkennung nannte fuer ein Telefon neunzehn Adressen, davon die
        // meisten tot. Nacheinander vergehen so Minuten, bevor der letzte Weg
        // an der Reihe ist -- gemessen, nicht geschaetzt.
        var direkt = new List<string>();
        var vermittelt = new List<string>();

        foreach (var adresse in candidates)
            (IstRelay(adresse) ? vermittelt : direkt).Add(adresse);

        Exception? letzter = null;

        if (direkt.Count > 0)
        {
            var (verbindung, fehler) = await WettlaufAsync(direkt, expected, "direkt", ct);
            if (verbindung is not null) return verbindung;
            letzter = fehler;
        }

        if (vermittelt.Count == 0)
            throw letzter ?? new IOException("keine der Adressen fuehrte zu einer Verbindung.");

        if (!_app.Relays || !_config.Relays)
        {
            _log($"[{Display}] Relay uebergangen -- fuer diese Gegenstelle abgeschaltet.");
            throw letzter ?? new NotSupportedException(
                "nur ueber einen Relay zu erreichen, und Relays sind abgeschaltet.");
        }

        var (ueberRelay, relayFehler) = await WettlaufAsync(vermittelt, expected, "ueber Relay", ct);
        if (ueberRelay is not null) return ueberRelay;

        throw relayFehler ?? letzter
            ?? new IOException("keine der Adressen fuehrte zu einer Verbindung.");
    }

    private static bool IstRelay(string adresse)
        => adresse.StartsWith("relay://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Versucht alle Adressen einer Gruppe gleichzeitig. Die erste
    /// Verbindung, die zustande kommt, gewinnt.
    /// </summary>
    /// <remarks>
    /// Was danach noch zustande kommt, wird geschlossen. Zwei Verbindungen zu
    /// derselben Gegenstelle waeren eine zuviel.
    ///
    /// Die Fehlschlaege werden gezaehlt und in einer Zeile genannt, nicht
    /// einzeln. Sechzehn tote Adressen sind sonst sechzehn Zeilen, die alle
    /// dasselbe sagen.
    /// </remarks>
    private async Task<(BepConnection? Verbindung, Exception? Fehler)> WettlaufAsync(
        IReadOnlyList<string> adressen, Bep.DeviceId expected, string was, CancellationToken ct)
    {
        _log(adressen.Count == 1
            ? $"[{Display}] verbinde {was}: {adressen[0]}"
            : $"[{Display}] verbinde {was}: {adressen.Count} Adressen gleichzeitig.");

        using var abbruch = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var laeuft = adressen
            .Select(adresse => (Adresse: adresse, Aufgabe: VersuchAsync(adresse, expected, abbruch.Token)))
            .ToList();

        Exception? letzter = null;
        var gruende = new Dictionary<string, int>(StringComparer.Ordinal);

        while (laeuft.Count > 0)
        {
            var fertig = await Task.WhenAny(laeuft.Select(e => e.Aufgabe)).ConfigureAwait(false);
            var eintrag = laeuft.First(e => e.Aufgabe == fertig);
            laeuft.Remove(eintrag);

            BepConnection verbindung;

            try
            {
                verbindung = await fertig.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                letzter = ex;
                var grund = Kurzgrund(ex);
                gruende[grund] = gruende.GetValueOrDefault(grund) + 1;
                continue;
            }

            await abbruch.CancelAsync().ConfigureAwait(false);
            Aufraeumen(laeuft.Select(e => e.Aufgabe));

            _log($"[{Display}] verbunden ueber {eintrag.Adresse}.");
            return (verbindung, null);
        }

        _log($"[{Display}] keine der {adressen.Count} Adressen fuehrte zum Ziel " +
             $"({string.Join(", ", gruende.Select(g => g.Value == 1 ? g.Key : $"{g.Value}x {g.Key}"))}).");

        return (null, letzter);
    }

    /// <summary>Ein einzelner Versuch, mit eigener Frist.</summary>
    private async Task<BepConnection> VersuchAsync(
        string adresse, Bep.DeviceId expected, CancellationToken ct)
    {
        using var frist = CancellationTokenSource.CreateLinkedTokenSource(ct);
        frist.CancelAfter(IstRelay(adresse) ? Relayfrist : Versuchsfrist);

        if (IstRelay(adresse))
        {
            // Kein "using": gelingt der Handschlag, gehoert der Strom der
            // Verbindung und darf hier nicht geschlossen werden.
            RelayClient.Leitung? leitung = null;

            try
            {
                leitung = await RelayClient.VerbindenAsync(
                    adresse, _identity, expected, t => _log($"[{Display}] {t}"), frist.Token);

                // Wer den Handschlag annimmt, bestimmt der Relay. Beide
                // Seiten muessen sich daran halten.
                return leitung.AlsServer
                    ? await BepConnection.AcceptOverAsync(
                        leitung.Strom, _identity, _app.DeviceName, frist.Token)
                    : await BepConnection.ConnectOverAsync(
                        leitung.Strom, _identity, expected, _app.DeviceName, frist.Token);
            }
            catch (Exception ex) when (leitung is not null && ex is EndOfStreamException or IOException)
            {
                // Die Leitung stand, und mitten im Handschlag war Schluss.
                //
                // Gemessen an einer Gegenstelle mit Syncthing v2.1.3: gleich
                // nach dem Beenden dieses Clients scheiterten drei Versuche
                // hintereinander genau so; nach neunzig Sekunden Wartezeit
                // kamen zwei Versuche in unter zwei Sekunden durch. Die
                // Gegenstelle fuehrt nach einem Abriss noch die alte
                // Verbindung und weist eine zweite ab, bis ihre eigene Frist
                // greift.
                //
                // "Unable to read beyond the end of the stream" sagt das
                // nicht, und wer es liest, sucht den Fehler bei sich.
                leitung.Dispose();

                throw new IOException(
                    "die vermittelte Leitung stand, die Gegenstelle hat den Handschlag aber " +
                    "geschlossen -- meist fuehrt sie noch die vorige Verbindung und bemerkt " +
                    "erst nach ein bis zwei Minuten, dass sie fort ist", ex);
            }
            catch
            {
                leitung?.Dispose();
                throw;
            }
        }

        var (host, port) = SplitHostPort(Bare(adresse));

        if (adresse.StartsWith("quic://", StringComparison.OrdinalIgnoreCase))
        {
            if (!BepQuic.Moeglich)
                throw new NotSupportedException("dieser Rechner bringt kein QUIC mit.");

            return await BepQuic.ConnectAsync(
                host, port, _identity, expected, _app.DeviceName, frist.Token);
        }

        return await BepConnection.ConnectAsync(
            host, port, _identity, expected, _app.DeviceName, frist.Token);
    }

    /// <summary>
    /// Schliesst, was nach dem Gewinner noch zustande kommt.
    /// </summary>
    /// <remarks>
    /// Nebenher und ohne darauf zu warten: der Aufrufer hat seine Verbindung,
    /// und ein Versuch, der noch in seiner Frist haengt, soll ihn nicht
    /// aufhalten.
    /// </remarks>
    private static void Aufraeumen(IEnumerable<Task<BepConnection>> uebrige)
    {
        foreach (var aufgabe in uebrige)
        {
            _ = aufgabe.ContinueWith(
                static fertig =>
                {
                    if (fertig.IsCompletedSuccessfully)
                        _ = fertig.Result.DisposeAsync(
                            "nicht gebraucht, eine andere Adresse war schneller");
                },
                TaskScheduler.Default);
        }
    }

    /// <summary>Der Grund eines Fehlschlags, kurz genug zum Zaehlen.</summary>
    /// <remarks>
    /// Gezaehlt wird nach diesem Text, gleiche Gruende landen also in einer
    /// Zahl. Deshalb steht bei einer Zeitueberschreitung ein fester Text --
    /// die Meldungen der drei Wege lauten verschieden und meinen dasselbe.
    ///
    /// Ueberall sonst gilt die Meldung selbst, und das ist der Punkt.
    /// Vorher stand hier der Name der Ausnahme, und im Protokoll erschien
    /// "keine der 1 Adressen fuehrte zum Ziel (IOException)". Was der Relay
    /// tatsaechlich geantwortet hatte -- "die Sitzung wurde abgelehnt: der
    /// Relay fuehrt bereits eine Sitzung zwischen diesen beiden Geraeten" --
    /// war damit fort, und der Fehlschlag sagte nichts.
    ///
    /// Ein Name ohne Meldung ist keine Auskunft. Er nennt die Bauart des
    /// Fehlers und verschweigt den Fehler.
    /// </remarks>
    private static string Kurzgrund(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => "Zeitueberschreitung",
        System.Net.Quic.QuicException q
            when q.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            => "Zeitueberschreitung",

        // Der Fehlercode ist hier die Auskunft; die Meldung dazu ist der
        // ausformulierte Text des Betriebssystems und in jeder Sprache anders.
        System.Net.Sockets.SocketException s => s.SocketErrorCode.ToString(),

        _ => Gekuerzt(ex.Message)
    };

    /// <summary>
    /// Eine Meldung, die in eine Protokollzeile passt.
    /// </summary>
    /// <remarks>
    /// Manche Meldungen tragen einen zweiten Satz mit dem Rat, es spaeter
    /// erneut zu versuchen. In einer Zeile, die schon mehrere Gruende
    /// aufzaehlt, ist das Fuellung.
    /// </remarks>
    private static string Gekuerzt(string meldung)
    {
        var text = meldung.Trim();
        return text.Length <= 120 ? text : text[..117] + "...";
    }

    /// <summary>Alle Adressen, unter denen die Gegenstelle zu versuchen ist.</summary>
    private async Task<IReadOnlyList<string>> CandidatesAsync(Bep.DeviceId expected, CancellationToken ct)
    {
        // Ist eine Adresse eingetragen, wird nicht gesucht.
        if (!string.IsNullOrWhiteSpace(_config.Address) &&
            !_config.Address.Equals("dynamic", StringComparison.OrdinalIgnoreCase))
            return [_config.Address];

        if (expected == Bep.DeviceId.Empty) return [];

        // Zuerst im eigenen Netz nachsehen. Das ist schneller und genauer,
        // und kein Server erfaehrt dabei, wonach gesucht wird.
        var local = _app.Local?.AddressesFor(expected) ?? [];
        if (local.Count > 0)
        {
            _log($"[{Display}] im lokalen Netz gefunden: {string.Join(", ", local)}");
            return local;
        }

        if (!_app.Discovery || !_config.Discovery)
        {
            _log($"[{Display}] keine Adresse eingetragen, und die Erkennung ist abgeschaltet.");
            return [];
        }

        _log($"[{Display}] frage die Erkennung nach {expected.Short()} ...");

        foreach (var server in _app.LookupServers)
        {
            try
            {
                using var discovery = new GlobalDiscovery(server);
                var found = await discovery.LookupAsync(expected, ct);

                // Die Antwort des ersten Servers mit einem Ergebnis genuegt,
                // die uebrigen liefern dieselben Adressen.
                if (found.Count == 0) continue;

                _log($"[{Display}] Erkennung nennt {found.Count}: {string.Join(", ", found)}");
                return found;
            }
            catch (Exception ex)
            {
                _log($"[{Display}] {Bep.GlobalDiscovery.HostOf(server)} antwortet nicht: {ex.Message}");
            }
        }

        _log($"[{Display}] die Erkennung kennt keine Adresse.");
        return [];
    }

    /// <summary>Entfernt aus einer Adresse das Schema und alles hinter dem Host.</summary>
    private static string Bare(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        var bare = scheme < 0 ? address : address[(scheme + 3)..];

        var slash = bare.IndexOf('/');
        return slash < 0 ? bare : bare[..slash];
    }

    /// <summary>
    /// Uebernimmt eine Verbindung, die die Gegenstelle aufgebaut hat.
    /// </summary>
    /// <remarks>
    /// Ab dem Hello gibt es keinen Unterschied mehr. Keine Nachricht des
    /// Protokolls sagt, welche Seite die Verbindung aufgebaut hat. Deshalb
    /// unterscheidet sich hier nur der Aufbau, die Sitzung selbst ist
    /// dieselbe.
    ///
    /// Nur auf diesem Weg erreicht uns eine Gegenstelle, zu der wir keine
    /// Verbindung aufbauen koennen, weil ihre Adresse wechselt oder weil sie
    /// hinter einem Router liegt.
    /// </remarks>
    public async Task AcceptAsync(
        BepConnection connection, IEnumerable<ShareConfig> shares, CancellationToken ct = default)
    {
        if (State is PeerState.Verbunden or PeerState.Verbindet)
        {
            // Eine zweite Verbindung zur selben Gegenstelle wird nicht
            // gebraucht. Der Grund geht mit, sonst liest die Gegenstelle nur
            // ein Abreissen und waehlt unveraendert weiter an.
            _log($"[{Display}] zweite eingehende Verbindung abgewiesen, es besteht bereits eine.");
            await connection.DisposeAsync("bereits verbunden");
            return;
        }

        var token = Begin(ct);
        _log($"[{Display}] eingehende Verbindung angenommen.");

        try
        {
            await RunSessionAsync(connection, shares, token);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    private CancellationToken Begin(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _clusterConfig = new TaskCompletionSource<ClusterConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = PeerState.Verbindet;
        LastError = null;
        return _cts.Token;
    }

    private void Fail(Exception ex)
    {
        LastError = ex.Message;
        State = PeerState.Fehler;
        _log($"[{Display}] Fehler: {ex.Message}");
    }

    /// <summary>Alles, was nach dem Hello gleich ablaeuft, ob aufgebaut oder angenommen.</summary>
    private async Task RunSessionAsync(
        BepConnection connection, IEnumerable<ShareConfig> shares, CancellationToken token)
    {
        _connection = connection;

        ReportedName = connection.PeerHello.DeviceName;
        ClientVersion = connection.PeerHello.ClientVersion;
        _log($"[{Display}] verbunden ({ClientVersion})");

        connection.ClusterConfigReceived += cc => OnClusterConfig(cc);
        connection.IndexReceived += m => { Melde("Index", m.Folder, m.Files); Route(m.Folder, m.Files); };
        connection.IndexUpdateReceived += m => { Melde("Index-Aktualisierung", m.Folder, m.Files); Route(m.Folder, m.Files); };
        connection.Log = _log;
        connection.PeerBusyOn += folderId =>
        {
            if (_shares.TryGetValue(folderId, out var busy)) busy.PeerBusy();
        };
        connection.MessageReceived += (typ, bytes) => Zaehle(typ, bytes, true);
        connection.MessageSent += (typ, bytes) => Zaehle(typ, bytes, false);
        connection.Serve = ServeAsync;

        _readLoop = connection.RunAsync(token);

        // Und jemanden, der ihr Ende bemerkt. Ohne das faellt eine
        // abgebrochene Verbindung niemandem auf.
        _ = VerlustBemerken(_readLoop, connection, token);

        // Die Ordner vorbereiten, bevor angekuendigt wird. Ihr Indexstand
        // geht in die Ankuendigung ein.
        foreach (var share in shares)
        {
            // Nach einem Anhalten steht der Ordner noch. Dann bekommt er nur
            // die neue Verbindung; ihn ein zweites Mal aufzubauen hiesse,
            // Sync-Root und Platzhalter neben die bestehenden zu setzen.
            if (_shares.TryGetValue(share.FolderId, out var bestehend))
            {
                bestehend.Rebind(DeviceId, connection);
                continue;
            }

            var host2 = _registry.GetOrAdd(share, out var frisch);
            Uebernehmen(host2);

            // Nur ein neu entstandener Ordner ist anzumelden. Ein zweiter
            // Teilnehmer findet einen fertigen vor, der schon in der Tabelle
            // steht.
            if (frisch) ShareAdded?.Invoke(host2);

            // Und der findet ihn nicht nur vor, er muss ihm auch seine
            // Verbindung geben.
            //
            // Ein Ordner, den eine andere Gegenstelle schon gestartet hat, ist
            // fuer diese Verbindung neu, fuer die Registratur aber nicht. Er
            // steht damit weder in der Schlange darunter -- die nimmt nur
            // gestoppte -- noch ist er hier gebunden worden. Ohne die Bindung
            // kennt er unsere Verbindung nicht: er kuendigt ihr nichts an und
            // nimmt von ihr nichts entgegen.
            //
            // Gemessen an einem Ordner mit zwei Gegenstellen: die zuerst
            // verbundene startete ihn, die zweite wurde nie eingetragen. Ihr
            // Index kam nie an, sie zaehlte nie als Halter, und jede
            // Blockanfrage ging an die erste -- auch dann, wenn die zweite
            // dieselben Dateien hielt.
            //
            // Ohne Bedingung auf den Zustand. Ein Ordner, der gerade erst
            // startet, steht noch auf "gestoppt", und die zweite Gegenstelle
            // faellt genau in dieses Fenster: sie wuerde weder gebunden noch
            // gestartet, weil der Start der ersten schon laeuft. Rebind
            // selbst kennt den Zustand und laesst ihn in Ruhe -- es traegt
            // nur die Verbindung ein, und genau darum geht es.
            if (!frisch) host2.Rebind(DeviceId, connection);
        }

        await NegotiateAsync(token);
        State = PeerState.Verbunden;

        // Nebeneinander, nicht nacheinander.
        //
        // Ein Ordner mit fuenfzehntausend Dateien rechnet beim Aufnehmen des
        // Bestands minutenlang. Nacheinander gestartet standen alle anderen
        // derweil auf "gestoppt" -- nicht weil sie unerreichbar waeren,
        // sondern weil sie noch nicht an der Reihe waren. Und genau das war
        // ihnen nicht anzusehen.
        //
        // Auf der Leitung ist das gefahrlos: die Verbindung reiht ihre
        // Schreibvorgaenge selbst hintereinander ein.
        await Task.WhenAll(_shares.Values
            // Ein Ordner, der schon steht, ist mit der neuen Verbindung fertig.
            .Where(share => share.State == ShareState.Gestoppt)
            .Select(async share =>
            {
                try { await share.StartAsync(DeviceId, connection, token); }
                catch (Exception ex) { _log($"[{share.FolderId}] {ex.Message}"); }
            }));
    }

    public event Action<ShareHost>? ShareAdded;

    /// <summary>
    /// Kuendigt alle Ordner dieser Gegenstelle in einer einzigen Nachricht an
    /// und nennt je Ordner unseren Stand, damit nur Neueres kommt.
    /// </summary>
    /// <summary>
    /// Kuendigt die Ordner erneut an, ohne die Verbindung zu loesen.
    /// </summary>
    /// <remarks>
    /// Fuer den Neuabgleich: nach dem Verwerfen steht die Sequenz auf null,
    /// und die Gegenstelle schickt daraufhin alles.
    /// </remarks>
    public Task RenegotiateAsync(CancellationToken ct = default)
        => _connection is null ? Task.CompletedTask : NegotiateAsync(ct);

    /// <summary>
    /// Reicht eine Freigabe an eine Verbindung nach, die schon steht.
    /// </summary>
    /// <remarks>
    /// Welche Ordner eine Gegenstelle bekommt, steht beim Verbinden fest: die
    /// Liste geht als Parameter in die Sitzung. Wer danach eine Freigabe um
    /// eine Gegenstelle erweitert, aendert die Konfiguration -- und die
    /// laufende Sitzung weiss davon nichts. Die Gegenstelle erfaehrt den
    /// Ordner erst beim naechsten Verbinden, also nach einem Neustart.
    ///
    /// Das ist genau der Handgriff, mit dem jemand eine zweite Quelle
    /// eintraegt, und genau dann will er nicht neu starten muessen.
    /// </remarks>
    public async Task ShareNachreichenAsync(ShareConfig share, CancellationToken ct = default)
    {
        if (_connection is not { } verbindung) return;
        if (State != PeerState.Verbunden) return;
        if (_shares.ContainsKey(share.FolderId)) return;

        var host = _registry.GetOrAdd(share, out var frisch);
        Uebernehmen(host);
        if (frisch) ShareAdded?.Invoke(host);

        if (host.State != ShareState.Gestoppt) host.Rebind(DeviceId, verbindung);

        _log($"[{Display}] Ordner \"{share.FolderId}\" nachgereicht, wird angekuendigt.");

        // Erst ankuendigen, dann starten -- der Indexstand des Ordners geht in
        // die Ankuendigung ein, so wie beim Verbinden auch.
        await NegotiateAsync(ct);

        if (host.State != ShareState.Gestoppt) return;

        try { await host.StartAsync(DeviceId, verbindung, ct); }
        catch (Exception ex) { _log($"[{host.FolderId}] {ex.Message}"); }
    }

    private async Task NegotiateAsync(CancellationToken ct)
    {
        // Syncthing schickt seinen ClusterConfig sofort nach dem Hello. Kommt
        // er wider Erwarten nicht, wird bei null begonnen.
        await Task.WhenAny(_clusterConfig.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));

        var peerFolders = _clusterConfig.Task.IsCompletedSuccessfully
            ? _clusterConfig.Task.Result.Folders
            : [];

        var announcement = new ClusterConfig();

        foreach (var share in _shares.Values)
        {
            var peerIndexId = 0UL;
            var maxSequence = 0L;

            var folder = peerFolders.FirstOrDefault(f => f.Id == share.FolderId);
            var peerDevice = folder?.Devices.FirstOrDefault(
                d => Bep.DeviceId.FromBytes(d.Id.Span) == _connection!.PeerId);

            if (peerDevice is not null)
            {
                // Eintraege aus einer Datenbank von vor dem Umbau tragen kein
                // Geraet. Sie gehoeren der ersten Gegenstelle, die sich
                // meldet -- damals gab es nur eine.
                share.AdoptLegacy(DeviceId);

                var bekannt = share.PeerIndexIdFor(DeviceId);

                if (bekannt != 0 && bekannt != peerDevice.IndexId)
                    share.ResetIndex(DeviceId, peerDevice.IndexId);
                else
                    share.RememberPeerIndexId(DeviceId, peerDevice.IndexId);

                peerIndexId = peerDevice.IndexId;
                maxSequence = share.MaxSequenceFor(DeviceId);

                // Womit der Ordner sagen kann, wann sein Index vollstaendig
                // ist. Diese Zahl ist die hoechste Sequenz, die die
                // Gegenstelle fuehrt; alles darunter steht noch aus.
                share.NoteZielSequenz(DeviceId, peerDevice.MaxSequence);
            }

            if (maxSequence > 0)
                _log($"[{share.FolderId}] setze bei Sequenz {maxSequence} fort ({share.IndexCount} Eintraege bekannt).");

            var entry = new Folder
            {
                Id = share.FolderId,
                Label = share.Config.Label,
                Type = FolderType.SendReceive
            };
            // Unser eigener Eintrag. Beide Zahlen sind verbindliche Angaben
            // an die Gegenstelle: eine wechselnde IndexId bedeutet, dass die
            // Gegenstelle alles bisher ueber uns Gespeicherte verwerfen soll.
            // NegotiateAsync laeuft auch mitten in der Sitzung, etwa beim
            // Uebernehmen eines Ordners.
            entry.Devices.Add(new Device
            {
                Id = ByteString.CopyFrom(_identity.Id.Span),
                MaxSequence = share.LocalSequence,
                IndexId = share.OwnIndexId
            });
            entry.Devices.Add(new Device
            {
                Id = ByteString.CopyFrom(_connection!.PeerId.Span),
                MaxSequence = maxSequence,
                IndexId = peerIndexId
            });
            announcement.Folders.Add(entry);
        }

        await _connection!.SendClusterConfigAsync(announcement, ct);
    }

    private void OnClusterConfig(ClusterConfig config)
    {
        _clusterConfig.TrySetResult(config);
        _log($"[{Display}] Ordnerliste: {config.Folders.Count} Ordner angeboten.");

        Offered = config.Folders
            .Select(f => new OfferedFolder(f.Id, f.Label, _shares.ContainsKey(f.Id)))
            .OrderBy(f => f.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        PruefeRueckzug(config);

        OfferedChanged?.Invoke();
    }

    /// <summary>
    /// Freigaben, die hier uebernommen sind, die die Gegenstelle aber nicht
    /// mehr anbietet.
    /// </summary>
    private readonly HashSet<string> _nichtAngeboten = new(StringComparer.Ordinal);

    /// <summary>
    /// Haelt fest, welche uebernommenen Freigaben die Gegenstelle nicht mehr
    /// anbietet.
    /// </summary>
    /// <remarks>
    /// Wer eine Freigabe drueben entfernt, hinterlaesst hier einen Ordner
    /// voller Platzhalter, die niemand mehr fuellen kann. Ohne diese Pruefung
    /// faellt das erst auf, wenn jemand eine der Dateien oeffnet -- und dann
    /// sieht es nach einem Fehler dieses Programms aus.
    ///
    /// Gemeldet wird der Wechsel und nicht der Zustand. Die Ordnerliste kommt
    /// bei jedem Verbindungsaufbau; eine Zeile je Liste waere Rauschen. Der
    /// Vermerk ueberdauert deshalb auch einen Verbindungsabbruch.
    /// </remarks>
    private void PruefeRueckzug(ClusterConfig config)
    {
        var angeboten = config.Folders.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var share in _shares.Values)
        {
            if (angeboten.Contains(share.FolderId))
            {
                if (_nichtAngeboten.Remove(share.FolderId))
                    _log($"[{Display}] bietet \"{share.FolderId}\" wieder an.");

                continue;
            }

            if (!_nichtAngeboten.Add(share.FolderId)) continue;

            _log($"[{Display}] bietet \"{share.FolderId}\" nicht mehr an. Der Inhalt der " +
                 "Platzhalter dieser Freigabe kann von dieser Gegenstelle nicht mehr " +
                 "uebertragen werden.");
        }
    }

    // ------------------------------------------------------------ Protokoll

    private readonly Lock _verkehrSperre = new();
    private readonly Dictionary<(MessageType Typ, bool Rein), (int Anzahl, long Bytes)> _verkehr = [];
    private DateTime _verkehrSeit = DateTime.UtcNow;

    /// <summary>Wie lange Kleinkram gesammelt wird, bevor eine Zeile entsteht.</summary>
    private static readonly TimeSpan Verkehrsfenster = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Sagt im Protokoll, was ein Index gebracht hat.
    /// </summary>
    /// <remarks>
    /// Die interessante Zahl ist die hoechste Sequenz. An ihr sieht man, ob
    /// die Gegenstelle etwas Neues gefunden hat oder nur wiederholt, was
    /// bereits bekannt war.
    /// </remarks>
    private void Melde(string art, string folderId, IEnumerable<Bep.Proto.FileInfo> files)
    {
        var bekannt = _shares.ContainsKey(folderId) ? "" : " (nicht uebernommen)";
        var anzahl = 0;
        long sequenz = 0;

        foreach (var file in files)
        {
            anzahl++;
            if (file.Sequence > sequenz) sequenz = file.Sequence;
        }

        _log($"[{Display}] {art} {folderId}{bekannt}: {anzahl} Eintraege, Sequenz bis {sequenz}.");
    }

    /// <summary>
    /// Sammelt den Kleinkram und meldet ihn hoechstens einmal je Minute.
    /// </summary>
    /// <remarks>
    /// Ohne diese Zeilen zeigt das Diagramm Verkehr, der Status sagt
    /// "abgeglichen", und dazwischen steht nichts. Eine Zeile je Nachricht
    /// waere aber unbrauchbar: waehrend eine Datei geholt wird, sind es
    /// tausende Bloecke, und die Freigabe meldet den Vorgang ohnehin.
    ///
    /// Index, Ordnerliste und Abschied haben eigene Zeilen und werden hier
    /// nicht noch einmal gezaehlt.
    /// </remarks>
    private void Zaehle(MessageType typ, int bytes, bool rein)
    {
        if (typ is MessageType.Index or MessageType.IndexUpdate
                or MessageType.ClusterConfig or MessageType.Close) return;

        string zeile;

        lock (_verkehrSperre)
        {
            var schluessel = (typ, rein);
            var (anzahl, summe) = _verkehr.GetValueOrDefault(schluessel);
            _verkehr[schluessel] = (anzahl + 1, summe + bytes);

            if (DateTime.UtcNow - _verkehrSeit < Verkehrsfenster) return;

            zeile = string.Join(", ", _verkehr
                .OrderByDescending(e => e.Value.Bytes)
                .Select(e => $"{e.Value.Anzahl}x {Benennung(e.Key.Typ)} " +
                             $"{(e.Key.Rein ? "empfangen" : "gesendet")} ({Menge(e.Value.Bytes)})"));

            _verkehr.Clear();
            _verkehrSeit = DateTime.UtcNow;
        }

        _log($"[{Display}] Verbindung: {zeile}.");
    }

    private static string Benennung(MessageType typ) => typ switch
    {
        MessageType.Ping => "Lebenszeichen",
        MessageType.DownloadProgress => "Fortschrittsmeldung",
        MessageType.Request => "Blockanfrage",
        MessageType.Response => "Blockantwort",
        _ => typ.ToString()
    };

    private static string Menge(long bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes / (1024.0 * 1024.0):0.0} MB";

    private void Route(string folderId, IEnumerable<Bep.Proto.FileInfo> files)
    {
        if (_shares.TryGetValue(folderId, out var share)) share.Absorb(DeviceId, files);
    }

    /// <summary>
    /// Reicht eine Blockanfrage der Gegenstelle an den zustaendigen Ordner
    /// weiter, so wie <see cref="Route"/> es fuer Index-Nachrichten tut.
    /// </summary>
    /// <remarks>
    /// Die Anfrage nennt den Ordner selbst. Ist er hier nicht eingerichtet,
    /// faellt die Antwort so aus wie fuer eine unbekannte Datei.
    /// </remarks>
    private Task<(ErrorCode Code, byte[] Data)> ServeAsync(Request request, CancellationToken ct)
    {
        if (_shares.TryGetValue(request.Folder, out var share))
            return share.ServeAsync(request, ct);

        _log($"[{Display}] Anfrage nach \"{request.Name}\" abgelehnt: Ordner \"{request.Folder}\" ist hier nicht eingerichtet.");
        return Task.FromResult<(ErrorCode, byte[])>((ErrorCode.NoSuchFile, []));
    }

    // ------------------------------------------------------------ Verwalten

    /// <summary>
    /// Erster Schritt zum Uebernehmen: den Ordner ankuendigen und seinen
    /// Index holen. Im Explorer entsteht dabei noch nichts.
    /// </summary>
    /// <remarks>
    /// Getrennt vom zweiten Schritt, damit vorher gefragt werden kann, wohin
    /// der Ordner soll und welche Zweige daraus uebernommen werden. Beides
    /// laesst sich erst entscheiden, wenn der Inhalt bekannt ist.
    /// </remarks>
    /// <param name="angelegt">
    /// Wird gerufen, sobald der Ordner besteht -- vor dem Warten auf den
    /// Index. Wer den Fortschritt zeigen will, braucht ihn zu diesem
    /// Zeitpunkt und nicht erst, wenn alles vorbei ist.
    /// </param>
    public async Task<ShareHost> PrepareAsync(
        ShareConfig share, CancellationToken ct = default, Action<ShareHost>? angelegt = null)
    {
        if (_connection is null) throw new InvalidOperationException("nicht verbunden.");

        var host = _registry.GetOrAdd(share, out _);
        Uebernehmen(host);
        _shares[share.FolderId] = host;
        angelegt?.Invoke(host);

        // Erneut ankuendigen, jetzt mit dem neuen Ordner.
        await NegotiateAsync(ct);
        await host.PrepareAsync(DeviceId, _connection, ct);

        return host;
    }

    /// <summary>Zweiter Schritt: uebernehmen, was bestaetigt wurde.</summary>
    public async Task CommitAsync(ShareHost host, CancellationToken ct = default)
    {
        ShareAdded?.Invoke(host);
        await host.CommitAsync(ct);
        MarkOffered();
    }

    /// <summary>
    /// Nimmt zurueck, was <see cref="PrepareAsync"/> angelegt hat.
    /// </summary>
    /// <remarks>
    /// Ohne die erneute Ankuendigung schickt die Gegenstelle weiter
    /// Aktualisierungen fuer einen Ordner, der nicht uebernommen wurde.
    /// </remarks>
    public async Task DiscardAsync(ShareHost host)
    {
        _shares.TryRemove(host.FolderId, out _);

        // Auch aus der Ablage. Sie haelt je Ordner genau einen Host, und
        // GetOrAdd findet ihn beim naechsten Versuch wieder -- verworfen,
        // ohne Index, mit dem Pfad von vorhin. Der Index der Gegenstelle
        // wuerde dann nicht neu geholt, denn wir haetten ihn ja schon.
        //
        // Einen Monat spaeter waere das ein Baum aus dem letzten Versuch.
        _registry.Remove(host.FolderId, out _);

        await host.UnbindAsync();
        await host.DisposeAsync();

        if (_connection is not null && State == PeerState.Verbunden)
        {
            try { await NegotiateAsync(CancellationToken.None); }
            catch (Exception ex) { _log($"[{Display}] {ex.Message}"); }
        }

        MarkOffered();
    }

    /// <summary>Beide Schritte auf einmal, fuer Aufrufer ohne Rueckfrage.</summary>
    public async Task<ShareHost> AcceptAsync(ShareConfig share, CancellationToken ct = default)
    {
        var host = await PrepareAsync(share, ct);
        await CommitAsync(host, ct);
        return host;
    }

    public async Task UnbindAsync(string folderId)
    {
        if (!_shares.TryRemove(folderId, out var share)) return;

        // Die eigene Verbindung wird in jedem Fall abgegeben.
        share.DropConnection(DeviceId);

        // Aufgeloest wird der Ordner genau einmal. Nehmen mehrere Gegenstellen
        // teil, ruft die Oberflaeche jede von ihnen; wer ihn nicht mehr in der
        // Ablage findet, hat nichts mehr aufzuloesen.
        if (!_registry.Remove(folderId, out _)) return;

        await share.UnbindAsync();
        await share.DisposeAsync();
        MarkOffered();
    }

    private void MarkOffered()
    {
        Offered = Offered.Select(o => o with { Accepted = _shares.ContainsKey(o.FolderId) }).ToList();
        OfferedChanged?.Invoke();
    }

    /// <summary>
    /// Wartet das Ende der Leseschleife ab und zieht die Folgerung daraus.
    /// </summary>
    /// <remarks>
    /// Die Schleife lief bisher unbeobachtet: gestartet, in ein Feld gelegt,
    /// nie erwartet. Brach die Verbindung von selbst ab -- das Netz fort, das
    /// WLAN aus, die Gegenstelle neu gestartet --, verschwand die Ausnahme in
    /// einer Task, die niemand ansah.
    ///
    /// Die Folgen reichten weiter als die fehlende Meldung. Der Zustand blieb
    /// auf "verbunden": die Oberflaeche zeigte eine Leitung, die es nicht mehr
    /// gab, und der Wiederverbinder sucht nach Gegenstellen im Zustand
    /// "getrennt" -- eine Bedingung, die so nie eintrat. Die ganze
    /// Wiederaufnahme konnte nicht anspringen.
    ///
    /// Das Ereignis "Closed" der Verbindung half dabei nicht: es wurde
    /// ausgeloest, aber nirgends abonniert.
    ///
    /// Gemessen an einem Abend: die Gegenstelle verlor die Verbindung um
    /// 21:16:48 und nahm sie zwoelf Minuten lang nicht wieder auf; hier stand
    /// dazu keine einzige Zeile.
    /// </remarks>
    private async Task VerlustBemerken(
        Task lauf, BepConnection connection, CancellationToken token)
    {
        string? grund = null;

        try { await lauf.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (StilleException ex) { grund = ex.Message; }
        catch (Exception ex) { grund = ShareHost.Herkunft(ex); }

        // Wir haben selbst getrennt. Anhalten und Trennen raeumen bereits auf
        // und sagen es auch; eine zweite Meldung nennte eine zweite Ursache,
        // die es nicht gibt.
        if (token.IsCancellationRequested) return;

        // Inzwischen haengt eine andere Verbindung an dieser Stelle.
        if (!ReferenceEquals(_connection, connection)) return;

        _log(grund is null
            ? $"[{Display}] Die Gegenstelle hat die Verbindung beendet."
            : $"[{Display}] Verbindung verloren: {grund}");

        // Derselbe Weg wie beim Anhalten: die Ordner bleiben eingehaengt, nur
        // die Leitung faellt weg. Damit steht der Zustand auf "getrennt", und
        // der Wiederverbinder nimmt die Gegenstelle beim naechsten Takt auf.
        try { await SuspendAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log($"[{Display}] Aufraeumen nach dem Verbindungsverlust: {ex.Message}");
        }
    }

    /// <summary>
    /// Legt die Verbindung still und laesst die Ordner stehen.
    /// </summary>
    /// <remarks>
    /// Fuer das Anhalten. Trennen wuerde die Sync-Roots aushaengen; dann
    /// haengt der Explorer an jedem Platzhalter, den jemand anfasst, und der
    /// Hintergrundlauf hoerte auf, lokal zu indexieren. Angehalten soll
    /// heissen: keine Verbindung -- nicht: kein Ordner.
    /// </remarks>
    public async Task SuspendAsync()
    {
        if (State != PeerState.Verbunden) return;

        foreach (var share in _shares.Values) share.DropConnection(DeviceId);

        if (_cts is not null) await _cts.CancelAsync();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _readLoop = null;
        State = PeerState.Getrennt;
        _log($"[{Display}] Verbindung getrennt, Ordner bleiben eingehaengt.");
    }

    public async Task DisconnectAsync()
    {
        foreach (var share in _shares.Values) await share.StopAsync();

        if (_cts is not null) await _cts.CancelAsync();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _readLoop = null;
        State = PeerState.Getrennt;
        _log($"[{Display}] getrennt.");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        foreach (var share in _shares.Values) await share.DisposeAsync();
        _shares.Clear();
        _cts?.Dispose();
    }

    private static (string Host, int Port) SplitHostPort(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon < 0 ? (address, 22000) : (address[..colon], int.Parse(address[(colon + 1)..]));
    }
}
