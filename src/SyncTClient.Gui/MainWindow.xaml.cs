using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using SyncTClient.Bep;
using SyncTClient.Mount;
using SyncTClient.Vfs;

namespace SyncTClient.Gui;

public partial class MainWindow : Window
{
    /// <summary>Wieviele abgeschlossene Übertragungen sichtbar bleiben.</summary>
    private const int KeepFinished = 25;

    /// <summary>So viele Säulen hat das Diagramm, unabhängig von der Spanne.</summary>
    private const int Buckets = 120;

    private readonly string _configPath;
    /// <summary>
    /// Was hinausgeht und was hereinkommt, getrennt gefuehrt.
    /// </summary>
    /// <remarks>
    /// Zwei Listen und nicht eine mit Filter: die Anzeige stellt sie
    /// nebeneinander, und zwei Sichten auf dieselbe Sammlung waeren derselbe
    /// Aufwand mit einer Umleitung mehr.
    /// </remarks>
    private readonly ObservableCollection<TransferInfo> _outgoing = [];
    private readonly ObservableCollection<TransferInfo> _incoming = [];
    private readonly ObservableCollection<PeerItem> _peers = [];
    private readonly ObservableCollection<ShareRow> _rows = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _cts = new();

    private AppConfig _config = new();
    private DeviceIdentity? _identity;
    private ThroughputMeter? _meter;
    private BepListener? _listener;

    /// <summary>Die Ordner, einer je Ordner-Kennung.</summary>
    private ShareRegistry? _registry;
    private LocalDiscovery? _local;
    private GlobalAnnouncer? _announcer;
    private TrayIcon? _tray;
    private double _peak;

    /// <summary>
    /// Das Programm wird wirklich beendet. Das X darf das Fenster dann nicht
    /// mehr verstecken.
    /// </summary>
    private bool _exiting;

    /// <summary>Wann die Vorschaubilder zuletzt gezaehlt wurden.</summary>
    /// <remarks>
    /// Gezaehlt wird ueber das Verzeichnis. Im Sekundentakt waere das
    /// verschwendete Arbeit, denn Vorschaubilder entstehen langsam.
    /// </remarks>
    private DateTime _thumbsRead = DateTime.MinValue;

    private ShareRow? _row;

    private ContextMenu? _columnMenu;

    // ------------------------------------------------------- Umbenennen

    /// <summary>Wartezeit zwischen Klick und Eingabefeld.</summary>
    private readonly DispatcherTimer _renameTimer = new();

    /// <summary>Auf welche Zeile sich der wartende Klick bezog.</summary>
    private ShareRow? _renameCandidate;

    /// <summary>
    /// Wir haben das Bearbeiten selbst ausgeloest.
    /// </summary>
    /// <remarks>
    /// Ein Doppelklick auf eine Zelle wuerde sonst von sich aus in den
    /// Bearbeitungsmodus gehen -- und der Doppelklick soll den Ordner oeffnen.
    /// </remarks>
    private bool _renameArmed;

    /// <summary>Welcher Menueintrag zu welcher Spalte gehoert.</summary>
    private readonly List<(DataGridColumn Column, MenuItem Item)> _columnItems = [];

    private MenuItem _menuConnect = new();
    private MenuItem _menuPause = new();
    private MenuItem _menuOpen = new();
    private MenuItem _menuSettings = new();
    private MenuItem _menuUnbind = new();
    private MenuItem _menuRescan = new();
    private MenuItem _menuRebuild = new();
    private MenuItem _menuResync = new();

    /// <summary>Stellt die Ordnermarkierung wieder her. Meist abgeblendet.</summary>
    private MenuItem _menuMarker = new();

    public MainWindow()
    {
        InitializeComponent();

        // Meldungen kommen aus dem Threadpool. Bindungen benoetigen den
        // Oberflaechen-Thread.
        TransferInfo.UiContext = SynchronizationContext.Current;

        UploadList.ItemsSource = _outgoing;
        DownloadList.ItemsSource = _incoming;
        ShareGrid.ItemsSource = _rows;

        // Der Filter sitzt auf der Ansicht, nicht auf der Liste: die Zeilen
        // bleiben vollstaendig, ausgewaehlt wird nur, was davon zu sehen ist.
        // Alles, was ueber _rows rechnet -- Summen, Statistik, der Blick aus
        // dem Infobereich --, sieht damit weiterhin alle.
        CollectionViewSource.GetDefaultView(_rows).Filter =
            o => o is ShareRow zeile && PasstZumFilter(zeile);
        CacheList.ItemsSource = _cacheRows;

        _configPath = AppConfig.DefaultConfigPath();

        Load();
        BuildColumnMenu();
        BuildRowMenu();
        RestoreColumns();

        // Der Zustand muss feststehen, bevor die Anwendung Show() ruft. Danach
        // wuerde sich das Fenster vor den Augen des Benutzers wieder
        // minimieren.
        if (_config.StartMinimized) WindowState = WindowState.Minimized;
        ApplyTray();

        // Falls die Programmdatei umgezogen ist oder anders heisst.
        Autostart.Refresh();

        PrepareRenaming();

        _meter = new ThroughputMeter(CollectWire);
        _refresh.Tick += (_, _) => Tick();
        _refresh.Tick += (_, _) => MarkierungenNachsehen();
        _refresh.Start();

        // Der Draht zum Kontextmenue im Explorer. Er steht, sobald das
        // Programm laeuft -- ohne laufenden Client haben die Eintraege
        // niemanden, der sie ausfuehrt, und zeigen das auch.
        EinbindungPruefen();
        CommandService.Handle = OnCommand;
        CommandService.Danach = NachDemBefehl;
        CommandService.EnsureStarted(AppendLog);
        HorcheAufNetz();
    }

    private string HomeDirectory
        => Path.Combine(Path.GetDirectoryName(_configPath)!, _config.HomeDirectory);

    /// <summary>
    /// Was die Hosts zu sehen bekommen: dieselbe Konfiguration, aber mit
    /// ausgeschriebenem Datenverzeichnis.
    /// </summary>
    /// <remarks>
    /// Absichtlich eine einzige Instanz. An ihr haengt das Verbrauchs Limit, und
    /// das gilt fuer alle Freigaben zusammen. Je Gegenstelle ein eigenes
    /// Limit waere kein programmweites Limit mehr.
    /// </remarks>
    private AppConfig _runtime = new();

    /// <summary>Ein einziger Vorrat, nicht je Abfrage ein neuer.</summary>
    /// <remarks>
    /// <see cref="ThumbnailStore.Usage"/> gibt den zuletzt gezaehlten Stand
    /// zurueck und stoesst die Zaehlung nur an -- ein Durchgang ueber
    /// tausende Dateien gehoert nicht auf den Faden, der das Fenster
    /// zeichnet. Ein bei jeder Abfrage neu angelegter Vorrat hat nie gezaehlt,
    /// meldet deshalb immer null und wirft das Ergebnis seiner eigenen
    /// Zaehlung sofort wieder fort.
    /// </remarks>
    private ThumbnailStore? _vorschau;

    private string _vorschauPfad = "";

    private ThumbnailStore Vorschau()
    {
        var pfad = _config.ThumbnailDirectory;

        if (_vorschau is null || !_vorschauPfad.Equals(pfad, StringComparison.OrdinalIgnoreCase))
        {
            _vorschau = new ThumbnailStore(pfad);
            _vorschauPfad = pfad;
        }

        return _vorschau;
    }

    // ------------------------------------------------------------ Laden

    private void Load()
    {
        // Die Oberflaeche richtet sich selbst ein. Wer sie oeffnet, soll seine
        // Device-ID sehen und weitergeben koennen. Ein Befehl auf der Konsole
        // waere ein Umweg ueber ein Werkzeug, das gerade nicht benutzt wird.
        var firstRun = !File.Exists(_configPath);

        try
        {
            _config = firstRun ? new AppConfig() : AppConfig.Load(_configPath);
            _identity = DeviceIdentity.LoadOrCreate(HomeDirectory);
            if (firstRun)
            {
                // Beim allerersten Start gibt es das Verzeichnis noch nicht.
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                _config.Save(_configPath);
            }
        }
        catch (Exception ex)
        {
            Status(App.S("M.ConfigUnreadable", ex.Message));
            return;
        }

        _runtime = new AppConfig
        {
            HomeDirectory = HomeDirectory,
            DeviceName = _config.DeviceName,
            MinimumCopies = _config.MinimumCopies,

            // Ohne diese Zeile liest _runtime den Vorgabewert, und der
            // programmweite Schalter waere seinerseits wirkungslos -- genau
            // der Zustand, den das Entfernen der zweiten Einstellung beenden
            // sollte.
            GenerateThumbnails = _config.GenerateThumbnails,
            SharesRoot = _config.SharesRoot,
            CacheMaxBytes = _config.CacheMaxBytes,
            MinimumFreeBytes = _config.MinimumFreeBytes,
            VolumeLimits = _config.VolumeLimits,
            Discovery = _config.Discovery,
            Relays = _config.Relays,
            DiscoveryServers = _config.DiscoveryServers,
            Peers = _config.Peers,
            Shares = _config.Shares,
            Parallelism = _config.Parallelism
        };

        // Aussehen und Sprache stehen in derselben Datei wie alles andere.
        // Sie gelten ab hier und nicht erst beim naechsten Start.
        App.ApplyTheme(_config.Theme);
        App.ApplyLanguage(_config.Language);

        // Ebenso die gewaehlte Ansicht. Erst hier, denn vorher stand die
        // Konfiguration noch nicht; der Wechsel meldet sich zurueck und
        // findet dann seinen eigenen Wert vor.
        FilterBox.SelectedIndex = (int)_config.Filter;

        _peers.Clear();

        // Eine Ablage fuer alle Gegenstellen. Ohne sie legte jede ihre eigenen
        // Ordner an, und zwei Teilnehmer desselben Ordners haetten zwei
        // Sync-Roots auf demselben Pfad.
        _registry = new ShareRegistry(_runtime, _identity!, AppendLog);

        foreach (var peerConfig in _config.Peers)
        {
            var host = new PeerHost(peerConfig, _runtime, _identity!, AppendLog, _registry);
            host.StateChanged += _ => Dispatcher.BeginInvoke(RefreshRows);
            host.OfferedChanged += () => Dispatcher.BeginInvoke(RebuildRows);
            host.ShareAdded += WireShare;
            _peers.Add(new PeerItem(host));
        }

        RebuildRows();
        RestartNetwork();
        Status(_peers.Count == 0
            ? App.S("M.NoPeer")
            : App.S("M.Configured", _peers.Count, _config.Shares.Count));
    }

    private void WireShare(ShareHost share) => Dispatcher.Invoke(() =>
    {
        // BeginInvoke und nicht Invoke: diese Meldungen kommen aus dem
        // Rueckruf des Dateisystems, und der darf auf nichts warten.
        //
        // Invoke haelt den meldenden Faden an, bis die Oberflaeche ihn
        // bedient. Steht die Oberflaeche selbst gerade in diesem Rueckruf --
        // sie stoesst das Holen an und wartet darauf --, warten beide
        // aufeinander. Windows bricht das nach zwei Minuten ab, der Rueckruf
        // scheitert mit 0x8007017C, und die naechste Datei faengt von vorn
        // an. Genau das war zu sehen: Bloecke vollstaendig empfangen, danach
        // zwei Minuten nichts.
        //
        // Eine Anzeige, die eine Sekunde spaeter nachzieht, ist niemandem im
        // Weg. Eine, die den Abgleich anhaelt, schon.
        // Zustand, Fortschritt und Cache melden sich nicht mehr einzeln. Der
        // Sekundentakt zieht sie ohnehin nach, und schneller kann niemand
        // lesen. Nur die Uebertragungsliste braucht die Meldung: ein Eintrag,
        // der erst eine Sekunde spaeter erscheint, hat seine Datei
        // moeglicherweise schon hinter sich.
        share.TransferStarted += t => Dispatcher.BeginInvoke(() => AddTransfer(t));
        share.TransferFinished += _ => Dispatcher.BeginInvoke(TrimTransfers);
        share.LimitReached += hit => Dispatcher.BeginInvoke(() => ShowLimit(hit));

        // Die Tabelle enthaelt fuer diesen Ordner noch keine Zeile. Er wurde
        // gerade erst uebernommen oder verbunden.
        RebuildRows();
    });

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Minimiert starten und ein Symbol im Infobereich bedeutet zusammen:
        // gar kein Fenster. Verstecken laesst es sich erst hier, denn Show()
        // ruft die Anwendung selbst auf.
        if (_config.StartMinimized && _tray is not null) Hide();

        // Was im Navigationsbereich steht und zu keiner eingerichteten
        // Freigabe gehoert, wird abgemeldet. Ein solcher Rest ist von Hand
        // nicht zu entfernen: der Ordner dazu ist fort, und ohne ihn bietet
        // weder der Explorer noch dieses Programm eine Handhabe.
        // Die Registrierung zu lesen und zu schreiben ist keine Arbeit fuer
        // den Faden, der das Fenster zeichnet.
        var pfade = _config.Shares.Select(share => share.LocalPath).ToList();
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        foreach (var rest in await Task.Run(
                     () => SyncTClient.Vfs.WinRtSyncRoot.UnregisterStrays(pfade).ToList()))
        {
            AppendLog($"Sync-Wurzeln: {rest}");
        }

        AppendLog($"Sync-Wurzeln geprueft in {uhr.ElapsedMilliseconds} ms.");

        // Die Oberflaeche ist zugleich der Sync-Dienst. Wer sie oeffnet, will
        // in aller Regel, dass der Abgleich laeuft.
        // Angehalten heisst angehalten -- auch ueber einen Neustart hinweg.
        // Sonst waere der Zustand nach dem naechsten Start wieder aufgehoben,
        // ohne dass jemand ihn aufgehoben haette. Damit das nicht wie eine
        // Stoerung aussieht, wird es beim Start auch gesagt.
        if (_config.Paused)
        {
            Status(App.S("M.PausedAll"));
            return;
        }

        foreach (var item in _peers.Where(p => p.Config.AutoConnect).ToList())
            await ConnectAsync(item);
    }

    // ------------------------------------------------------------ Zeilen

    /// <summary>
    /// Baut die Tabelle aus allem, was es gibt: uebernommene Ordner und
    /// solche, die eine Gegenstelle nur anbietet.
    /// </summary>
    private void RebuildRows()
    {
        var selected = _row?.FolderId;
        _rows.Clear();

        // Eine Zeile je Ordner, nicht je Paar aus Ordner und Gegenstelle.
        // Nehmen zwei am selben Ordner teil, ist es ein Ordner -- mit einem
        // Pfad, einer Auswahl und einem Index.
        var nachOrdner = new Dictionary<string, ShareRow>(StringComparer.Ordinal);

        void Aufnehmen(PeerItem peer, string folderId, string label, ShareHost? share)
        {
            if (nachOrdner.TryGetValue(folderId, out var vorhanden))
            {
                vorhanden.AddPeer(peer);
                return;
            }

            var zeile = Wire(new ShareRow(peer, folderId, label, share));
            nachOrdner[folderId] = zeile;
            _rows.Add(zeile);
        }

        // Ein Ordner bekommt seine Freigabe nur, wenn er auch uebernommen ist.
        //
        // Beim Uebernehmen entsteht sie schon vor der Rueckfrage: erst muss
        // der Index da sein, sonst hat der Dialog keinen Baum zu zeigen. Sie
        // traegt dabei einen vorlaeufigen Pfad, den niemand bestaetigt hat.
        // Stand sie in der Zeile, sah man diesen Pfad und ihren Zustand,
        // bevor ueberhaupt gefragt war -- und nach einem Abbruch blieb beides
        // stehen.
        ShareHost? Uebernommene(PeerItem peer, string folderId)
            => _config.Shares.Any(s => s.FolderId.Equals(folderId, StringComparison.Ordinal))
                ? peer.Host.ShareFor(folderId)
                : null;

        foreach (var peer in _peers)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var offer in peer.Host.Offered)
            {
                seen.Add(offer.FolderId);
                Aufnehmen(peer, offer.FolderId, offer.Label, Uebernommene(peer, offer.FolderId));
            }

            // Konfigurierte Ordner, die die Gegenstelle (noch) nicht nennt.
            foreach (var share in _config.SharesOf(peer.Config).Where(s => !seen.Contains(s.FolderId)))
                Aufnehmen(peer, share.FolderId, share.Label, Uebernommene(peer, share.FolderId));
        }

        // Uebernommen ist, was in der Konfiguration steht -- unabhaengig
        // davon, ob gerade eine Freigabe dazu laeuft.
        foreach (var zeile in _rows)
            zeile.Configured = _config.Shares.Any(s => s.FolderId == zeile.FolderId);

        ShareGrid.SelectedItem = _rows.FirstOrDefault(r => r.FolderId == selected)
            ?? _rows.FirstOrDefault();

        RefreshRows();
    }

    /// <summary>Haengt sich an die Umbenennung, damit sie gespeichert wird.</summary>
    private ShareRow Wire(ShareRow row)
    {
        row.Renamed += OnShareRenamed;
        return row;
    }

    /// <summary>Gehoert diese Zeile in die gewaehlte Ansicht?</summary>
    private bool PasstZumFilter(ShareRow zeile) => _config.Filter switch
    {
        // "Verbunden" heisst: es laeuft eine Freigabe dazu. Angeboten, aber
        // nicht uebernommen zaehlt nicht dazu -- die Zeile sagt genau das
        // auch in ihrer Spalte "Status".
        ShareFilter.Verbunden => zeile.Accepted,
        ShareFilter.Getrennt => !zeile.Accepted,
        _ => true
    };

    /// <summary>Wie viele Zeilen zuletzt liefen. Daran merkt der Filter, dass er nachziehen muss.</summary>
    private int _zuletztLaufend = -1;

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        var gewaehlt = (ShareFilter)Math.Max(0, FilterBox.SelectedIndex);
        if (gewaehlt == _config.Filter) return;

        _config.Filter = gewaehlt;
        Persist();

        CollectionViewSource.GetDefaultView(_rows).Refresh();
    }

    private void RefreshRows()
    {
        ApplyPause();

        // Der Filter fragt nach einem Zustand, und der aendert sich waehrend
        // des Laufs: eine Freigabe wird bereit, eine Verbindung faellt aus.
        // Nachgezogen wird aber nur, wenn sich die Zahl auch geaendert hat --
        // ein Auffrischen im Sekundentakt setzte Auswahl und Bildlauf jedesmal
        // zurueck.
        var laufend = _rows.Count(r => r.Accepted);
        if (laufend != _zuletztLaufend)
        {
            _zuletztLaufend = laufend;
            if (_config.Filter != ShareFilter.Alle)
                CollectionViewSource.GetDefaultView(_rows).Refresh();
        }

        foreach (var row in _rows) row.AppPaused = _config.Paused;
        foreach (var peer in _peers) peer.Refresh();
        foreach (var row in _rows) row.Refresh();
        UpdateButtons();
        UpdateCache();
        UpdateThumbnails();
    }

    /// <summary>
    /// Richtet das Umbenennen in der Liste ein.
    /// </summary>
    /// <remarks>
    /// Wie im Explorer: ein Klick waehlt aus, ein zweiter nach kurzer Pause
    /// oeffnet das Eingabefeld, ein Doppelklick oeffnet den Ordner. Die Pause
    /// ist die Doppelklickzeit des Systems -- eine erfundene Zahl waere fuer
    /// den einen zu kurz und fuer den anderen zu lang.
    ///
    /// Bearbeitbar ist ausschliesslich die Namensspalte. Die Tabelle muss
    /// dafuer als Ganzes bearbeitbar sein, weil eine schreibgeschuetzte
    /// Tabelle jede einzelne Spalte uebersteuert; alle uebrigen Spalten
    /// werden deshalb einzeln gesperrt.
    /// </remarks>
    private void PrepareRenaming()
    {
        foreach (var column in ShareGrid.Columns)
            column.IsReadOnly = !ReferenceEquals(column, ColName);

        ShareGrid.IsReadOnly = false;

        _renameTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 150);
        _renameTimer.Tick += (_, _) => BeginRename();

        ShareGrid.PreviewMouseLeftButtonUp += OnGridLeftButtonUp;
        ShareGrid.BeginningEdit += OnBeginningEdit;
        ShareGrid.CellEditEnding += OnCellEditEnding;
    }

    private void OnGridLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _renameTimer.Stop();

        if (e.ClickCount > 1) return;
        if (FindCell(e.OriginalSource as DependencyObject) is not { } cell) return;
        if (!ReferenceEquals(cell.Column, ColName) || cell.IsEditing) return;

        // Erst auswaehlen, dann umbenennen. Der Klick, der eine andere Zeile
        // auswaehlt, soll nichts weiter ausloesen.
        if (cell.DataContext is not ShareRow row || !row.Accepted) return;
        if (!ReferenceEquals(row, _row)) return;

        _renameCandidate = row;
        _renameTimer.Start();
    }

    /// <summary>Die Zelle, in der ein Klick gelandet ist.</summary>
    /// <remarks>
    /// Nicht jeder Klick landet auf einem Element des Sichtbaums. Ein
    /// Hyperlink ist ein ContentElement -- er steht in einem Textfluss und
    /// nicht in der Zeichenordnung. VisualTreeHelper.GetParent wirft fuer
    /// ihn, und der Klick auf die Knotenspalte oder den Ordnerpfad beendete
    /// damit das Programm.
    ///
    /// Der Weg nach oben nimmt deshalb, was zur jeweiligen Art passt: den
    /// Sichtbaum fuer das Sichtbare, den Textfluss fuer das Geschriebene.
    /// </remarks>
    private static DataGridCell? FindCell(DependencyObject? source)
    {
        while (source is not null and not DataGridCell)
        {
            source = source switch
            {
                System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    => System.Windows.Media.VisualTreeHelper.GetParent(source),

                ContentElement content
                    => ContentOperations.GetParent(content) ?? LogicalTreeHelper.GetParent(content),

                _ => LogicalTreeHelper.GetParent(source)
            };
        }

        return source as DataGridCell;
    }

    private void BeginRename()
    {
        _renameTimer.Stop();

        if (_renameCandidate is null || !ReferenceEquals(_renameCandidate, _row)) return;

        _renameCandidate.Editing = true;
        _renameArmed = true;

        ShareGrid.CurrentCell = new DataGridCellInfo(_renameCandidate, ColName);
        ShareGrid.BeginEdit();

        _renameArmed = false;
    }

    /// <summary>Nur die selbst ausgeloeste Bearbeitung ist erlaubt.</summary>
    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (!_renameArmed) e.Cancel = true;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is ShareRow row) row.Editing = false;
    }

    /// <summary>Das Eingabefeld bekommt den Fokus und den ganzen Text markiert.</summary>
    private void OnNameEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;

        box.Focus();
        box.SelectAll();
    }

    /// <summary>Der neue Name gilt sofort und wird gespeichert.</summary>
    private void OnShareRenamed(ShareRow row)
    {
        Persist();
        Status(App.S("M.Renamed", row.FolderId, row.Name));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>
    /// Haelt den Abgleich an oder setzt ihn fort.
    /// </summary>
    /// <remarks>
    /// Angehalten heisst: keine Uebertragung und keine Aenderung im Ordner.
    /// Die Platzhalter bleiben stehen und der Ordner bleibt eingehaengt.
    ///
    /// Die Verbindung wird nur hier getrennt und wieder aufgebaut, nicht in
    /// <see cref="ApplyPause"/>: das laeuft im Sekundentakt mit, und ein
    /// Verbindungsaufbau je Sekunde waere keiner.
    /// </remarks>
    private void OnTogglePauseAll(object? sender = null, RoutedEventArgs? e = null)
    {
        _config.Paused = !_config.Paused;
        Persist();

        ApplyPause();
        _ = ApplyPauseToPeersAsync();
        RefreshRows();

        Status(App.S(_config.Paused ? "M.PausedAll" : "M.ResumedAll"));
    }

    /// <summary>
    /// Zieht den Modus auf alle Freigaben nach.
    /// </summary>
    /// <remarks>
    /// Laeuft bei jeder Auffrischung und nicht nur beim Umschalten. Eine
    /// Freigabe, die gerade erst hochfaehrt, laesst sich noch nicht anhalten;
    /// ohne diese Nachfuehrung liefe sie los, sobald sie bereit ist, und der
    /// Modus waere eine einmalige Handlung statt einer Einstellung.
    /// </remarks>
    private void ApplyPause()
    {
        foreach (var share in _rows.Select(r => r.Share).OfType<ShareHost>())
        {
            if (_config.Paused) share.Pause();
            else share.Resume();
        }

        PauseAllButton.Content = App.S(_config.Paused ? "S.Main.ResumeAll" : "S.Main.PauseAll");
    }

    /// <summary>
    /// Trennt beim Anhalten die Verbindungen und baut sie beim Fortsetzen wieder
    /// auf.
    /// </summary>
    /// <remarks>
    /// "Alles anhalten" soll heissen: kein Datenverkehr. Die Ankuendigungen
    /// der Gegenstelle sind zwar klein, aber sie sind Verkehr, und wer wegen
    /// einer knappen Verbindung anhaelt, hat kein Interesse an Ausnahmen.
    ///
    /// Eine einzelne angehaltene Freigabe trennt dagegen nichts: die Verbindung
    /// gehoert der Gegenstelle und nicht dem Ordner, und die uebrigen Ordner
    /// derselben Gegenstelle laufen weiter.
    /// </remarks>
    private async Task ApplyPauseToPeersAsync()
    {
        foreach (var item in _peers.ToList())
        {
            try
            {
                if (_config.Paused)
                {
                    if (item.Host.State == PeerState.Verbunden) await item.Host.SuspendAsync();
                }
                else if (item.Config.AutoConnect && item.Host.State == PeerState.Getrennt)
                {
                    await ConnectAsync(item);
                }
            }
            catch (Exception ex)
            {
                Status($"[{item.Display}] {ex.Message}");
            }
        }
    }

    /// <summary>Klappt die Zeile auf oder zu.</summary>
    private void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShareRow row })
            row.Expanded = !row.Expanded;

        // Sonst waehlt der Klick nebenbei die Zeile aus und aendert damit,
        // was unten in den Reitern steht.
        e.Handled = true;
    }

    private void OnShareSelected(object sender, SelectionChangedEventArgs e)
    {
        _row = ShareGrid.SelectedItem as ShareRow;
        UpdateButtons();
        UpdateCache();
    }

    // ------------------------------------------------------------ Takt

    /// <summary>Wann zuletzt aufgeraeumt wurde.</summary>
    private DateTime _lastEnforce = DateTime.MinValue;

    /// <summary>So oft wird nachgesehen, ob die Grenzen noch eingehalten sind.</summary>
    private static readonly TimeSpan EnforceInterval = TimeSpan.FromMinutes(1);

    /// <remarks>
    /// Hier und nirgends sonst werden die Zeilen nachgezogen.
    ///
    /// Frueher stiess jede Meldung einer Freigabe sofort einen vollstaendigen
    /// Neuaufbau an. Waehrend "vollstaendig lokal" tausend Dateien holt, sind
    /// das tausend Durchlaeufe -- jeder ueber alle Zeilen, alle Gegenstellen,
    /// den Cache und die Vorschauen -- und alle auf dem einen Faden, den die
    /// Oberflaeche hat. Der Abgleich wartete mit.
    /// </remarks>
    /// <summary>
    /// Der Sekundentakt, mit einer Uhr an jedem Abschnitt.
    /// </summary>
    /// <remarks>
    /// Die Uhren bleiben stehen, auch wenn der Fehler gefunden ist, den sie
    /// gefunden haben. Sie kosten nichts -- ein Stopwatch in einer Methode,
    /// die ohnehin jede Sekunde laeuft -- und sie schweigen, solange alles
    /// in Ordnung ist.
    ///
    /// Ihren Wert haben sie gezeigt: "Takt 7762 ms, davon Zeilen 7761" war
    /// die eine Zeile, die eine Woche Raten ersetzt haette. Wer sie nach dem
    /// ersten Erfolg ausbaut, faengt beim naechsten Mal wieder von vorn an.
    ///
    /// Gemeldet wird nur ein Takt ueber einer halben Sekunde, im
    /// Protokollfeld und in der Datei daneben. Wer das Fenster gerade nicht
    /// bedienen kann, kommt an das Feld nicht heran.
    /// </remarks>
    /// <summary>
    /// Sieht bei jeder Freigabe nach ihrer Ordnermarkierung.
    /// </summary>
    /// <remarks>
    /// Von hier aus, weil im Fehlerzustand innerhalb der Freigabe nichts mehr
    /// laeuft: die Hintergrundschleife startet erst nach der Pruefung, und die
    /// Pruefung ist gerade das, was fehlgeschlagen ist. Der Takt der
    /// Oberflaeche ist der einzige, der dann noch geht.
    ///
    /// Der Aufruf kehrt sofort zurueck, wenn nichts zu tun ist. Nur wenn eine
    /// Markierung verschwindet oder wiederkommt, geschieht ueberhaupt etwas.
    /// </remarks>
    /// <summary>
    /// Sagt einmal beim Start, wenn die Einbindung nicht steht.
    /// </summary>
    /// <remarks>
    /// Die Eintraege entstehen von selbst, sobald eine Freigabe anlaeuft --
    /// aber nur, wenn die Vorschau-DLL neben dem Programm liegt. Sie ist ein
    /// eigenes Projekt und fehlt einem Ordner, in den jemand nur die
    /// uebrigen Dateien kopiert hat. Ohne diesen Hinweis bleibt es dabei,
    /// dass "die Vorschaubilder eben nicht gehen", ohne dass irgendwo stuende,
    /// warum.
    /// </remarks>
    private void EinbindungPruefen()
    {
        var zustand = ExplorerRegistration.Nachsehen();

        if (zustand.Mitgeliefert.Pfad is null) AppendLog(App.S("M.ShellMissing"));
        else if (zustand.Veraltet && zustand.ClassRegistered) AppendLog(App.S("M.ShellStale"));
    }

    private void MarkierungenNachsehen()
    {
        foreach (var zeile in _rows)
        {
            if (zeile.Share is { } share)
                _ = share.MarkierungNachsehen(_cts.Token);
        }
    }

    private void Tick()
    {
        var uhr = System.Diagnostics.Stopwatch.StartNew();

        FlushLog();
        var t1 = uhr.ElapsedMilliseconds;

        RefreshRows();
        var t2 = uhr.ElapsedMilliseconds;

        UpdateThroughput();
        var t3 = uhr.ElapsedMilliseconds;

        _tray?.Show(Zustand());
        var t4 = uhr.ElapsedMilliseconds;

        EnforceLimits();
        var t5 = uhr.ElapsedMilliseconds;

        VersucheWiederzuverbinden();
        var t6 = uhr.ElapsedMilliseconds;

        if (t6 < 500) return;

        var zeile =
            $"Takt {t6} ms: Protokoll {t1}, Zeilen {t2 - t1}, Durchsatz {t3 - t2}, " +
            $"Infobereich {t4 - t3}, Grenzen {t5 - t4}, Verbinden {t6 - t5}.";

        AppendLog(zeile);
        App.Vermerken(zeile);
    }

    // ------------------------------------------------------------ Wiederverbinden

    /// <summary>Wann zuletzt ein Verbindungsversuch lief.</summary>
    private DateTime _letzterVersuch = DateTime.MinValue;

    /// <summary>Der Abstand bis zum naechsten Versuch.</summary>
    /// <remarks>
    /// Er verdoppelt sich mit jedem Fehlschlag. Ein Netz, das seit einer
    /// Stunde fort ist, kommt nicht dadurch zurueck, dass man alle fuenfzehn
    /// Sekunden danach fragt -- es fuellt nur das Protokoll.
    /// </remarks>
    private TimeSpan _abstand = ErsterAbstand;

    private static readonly TimeSpan ErsterAbstand = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GroessterAbstand = TimeSpan.FromMinutes(5);

    /// <summary>Laeuft gerade ein Versuch?</summary>
    private int _versuchLaeuft;

    /// <summary>
    /// Nimmt getrennte Gegenstellen wieder auf.
    /// </summary>
    /// <remarks>
    /// Eine Verbindung geht verloren, ohne dass jemand etwas falsch gemacht
    /// hat: das WLAN bricht ab, ein VPN legt sich davor, der Server startet
    /// neu. Bisher blieb der Zustand danach auf "getrennt" stehen, bis jemand
    /// von Hand verband oder das Programm neu startete -- und "alles
    /// angehalten" umzuschalten war der einzige Weg, der es nebenbei tat.
    ///
    /// Angehalten heisst angehalten: dann wird nicht verbunden.
    /// </remarks>
    private void VersucheWiederzuverbinden()
    {
        if (_config.Paused) return;
        if (DateTime.UtcNow - _letzterVersuch < _abstand) return;

        var offen = _peers
            .Where(p => p.Config.AutoConnect && p.Host.State == PeerState.Getrennt)
            .ToList();

        if (offen.Count == 0)
        {
            // Alles steht. Der naechste Ausfall soll nicht erst in fuenf
            // Minuten bemerkt werden.
            _abstand = ErsterAbstand;
            return;
        }

        // Ein Versuch dauert, bis eine Verbindung steht oder scheitert. Der
        // Takt laeuft weiter; ohne diese Sperre lagen bald zehn Versuche
        // uebereinander.
        if (Interlocked.Exchange(ref _versuchLaeuft, 1) == 1) return;

        _letzterVersuch = DateTime.UtcNow;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                foreach (var item in offen)
                {
                    Status(App.S("M.Reconnecting", item.Display));
                    await ConnectAsync(item);
                }

                _abstand = _peers.Any(p => p.Config.AutoConnect && p.Host.State == PeerState.Getrennt)
                    ? Verdoppeln(_abstand)
                    : ErsterAbstand;
            }
            finally
            {
                Interlocked.Exchange(ref _versuchLaeuft, 0);
            }
        });
    }

    private static TimeSpan Verdoppeln(TimeSpan abstand)
        => abstand * 2 > GroessterAbstand ? GroessterAbstand : abstand * 2;

    /// <summary>
    /// Horcht darauf, dass sich am Netz etwas aendert.
    /// </summary>
    /// <remarks>
    /// Ein VPN, das sich verbindet oder trennt, aendert die Adressen dieses
    /// Rechners -- ebenso ein WLAN, das zurueckkommt. Das ist der Augenblick,
    /// in dem ein Versuch sich lohnt, und nicht erst der naechste Abstand.
    ///
    /// Das Ereignis kommt aus einem fremden Faden. Verbunden wird deshalb
    /// nicht hier, sondern beim naechsten Takt: gesetzt wird nur, dass er
    /// nicht mehr zu warten braucht.
    /// </remarks>
    private void HorcheAufNetz()
        => System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            _abstand = ErsterAbstand;
            _letzterVersuch = DateTime.MinValue;
        };

    /// <summary>
    /// Zieht die Grenzen der Datentraeger nach.
    /// </summary>
    /// <remarks>
    /// Nach jeder Hydration wird ohnehin geprueft. Dieser Takt deckt die
    /// Faelle ab, in denen von aussen etwas dazukommt: hineinkopierte
    /// Dateien werden nie geholt, also loest sie auch nichts aus, und sie
    /// blieben sonst liegen, bis das naechste Mal etwas hydriert wird.
    ///
    /// Der Befehlszeilenbetrieb hatte diesen Takt von Anfang an. Der
    /// Oberflaeche fehlte er -- und damit blieben genau die 12 GB liegen,
    /// die jemand hineinkopiert hatte.
    /// </remarks>
    private void EnforceLimits()
    {
        if (DateTime.UtcNow - _lastEnforce < EnforceInterval) return;
        _lastEnforce = DateTime.UtcNow;

        var shares = _rows.Select(r => r.Share).OfType<ShareHost>().ToList();
        if (shares.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var share in shares)
            {
                try { await share.EnforceLimitsAsync(); }
                catch (Exception ex) { AppendLog($"[{share.FolderId}] {ex.Message}"); }
            }
        });
    }

    /// <summary>
    /// Was das Zeichen im Infobereich melden soll.
    /// </summary>
    /// <remarks>
    /// Die Reihenfolge ist die Rangfolge. Ein Fehler ueberdeckt alles andere,
    /// denn er verlangt eine Entscheidung. Eine fehlende Verbindung kommt als
    /// naechstes: ohne sie sagt jeder weitere Zustand nichts aus.
    ///
    /// "Angehalten" gilt nur, wenn wirklich alles angehaelt. Laeuft daneben
    /// noch eine Freigabe, ist deren Fortschritt die nuetzlichere Auskunft.
    /// </remarks>
    private TrayStatus Zustand()
    {
        var uebernommen = _rows.Where(r => r.Share is not null).ToList();

        if (uebernommen.Any(r => r.Share!.State == ShareState.Fehler)) return TrayStatus.Fehler;
        if (!_peers.Any(p => p.Host.State == PeerState.Verbunden)) return TrayStatus.Getrennt;

        if (uebernommen.Count > 0 && uebernommen.All(r => r.Share!.State == ShareState.Pausiert))
            return TrayStatus.Pausiert;

        if (uebernommen.Any(r => r.Busy) || Laufend().Any(t => t.State == TransferState.Laeuft))
            return TrayStatus.Synchronisiert;

        return TrayStatus.Erledigt;
    }

    /// <summary>
    /// Was ueber den Draht ging, seit das Programm laeuft.
    /// </summary>
    /// <remarks>
    /// Aus dem programmweiten Zaehler, nicht aus den Verbindungen der
    /// Gegenstellen. Vorher wurde je Gegenstelle der Stand ihrer aktuellen
    /// Verbindung gelesen -- und damit nur der einen. Bytes, die ueber eine
    /// ersetzte, eine zusaetzlich angenommene oder eine gerade auslaufende
    /// Verbindung gingen, tauchten in der Anzeige nie auf. Bei einer
    /// Uebertragung von 156 MB standen dort 57 KB.
    /// </remarks>
    private (long Read, long Written) CollectWire() => WireTally.Totals;

    private void UpdateThroughput()
    {
        if (_meter is null) return;

        var window = Range30.IsChecked == true ? TimeSpan.FromMinutes(30)
            : Range180.IsChecked == true ? TimeSpan.FromHours(3)
            : TimeSpan.FromMinutes(5);

        var series = _meter.Series(window, Buckets);
        Chart.Show(series);

        // Die aktuelle Rate ist die juengste Saeule der kurzen Spanne. Bei
        // drei Stunden waere ein Korb 90 Sekunden lang und die Anzeige traege.
        var jetzt = _meter.Series(TimeSpan.FromSeconds(5), 1)[0];
        RateDown.Text = Format.Rate(jetzt.Read);
        RateUp.Text = Format.Rate(jetzt.Written);

        _peak = Math.Max(_peak, Math.Max(jetzt.Read, jetzt.Written));
        PeakRate.Text = Format.Rate(_peak);

        var (read, written) = _meter.Total;
        TotalDown.Text = Format.Bytes(read);
        TotalUp.Text = Format.Bytes(written);
    }

    /// <summary>
    /// Der Fuellstand in der Fussleiste.
    /// </summary>
    /// <remarks>
    /// Gezeigt wird der Datentraeger, der seiner Grenze am naechsten ist --
    /// also der, auf dem als naechstes etwas weichen muss. Eine Summe ueber
    /// alle Laufwerke waere die falsche Zahl: sie kann harmlos aussehen,
    /// waehrend auf einer einzelnen Platte schon Speicherplatz freigegeben wird.
    ///
    /// Bei nur einem Laufwerk ist das dasselbe wie vorher, und die Zeile
    /// bleibt so knapp wie zuvor.
    /// </remarks>
    /// <summary>Eine Zeile der Laufwerksanzeige.</summary>
    private sealed record CacheRow(string Drive, double Percent, string Text, string Tip);

    private readonly ObservableCollection<CacheRow> _cacheRows = [];

    /// <summary>
    /// Der Fuellstand je Laufwerk, gemessen am Verbrauchs Limit dieses
    /// Laufwerks.
    /// </summary>
    /// <remarks>
    /// Eine Summe ueber alle Laufwerke waere die falsche Zahl. Die Grenze gilt
    /// je Datentraeger; die Summe kann harmlos aussehen, waehrend ein
    /// einzelnes Laufwerk die Grenze schon ueberschreitet.
    /// </remarks>
    private void UpdateCache()
    {
        // _runtime traegt die laufenden Freigaben; an dessen Grenzen melden
        // sich die Caches an. _config ist nur die Datei.
        var volumes = _runtime.Cache.Volumes;

        _cacheRows.Clear();

        foreach (var volume in volumes)
        {
            var laufwerk = volume.Root.TrimEnd(Path.DirectorySeparatorChar);
            var frei = volume.FreeBytes < 0
                ? App.S("M.FreeUnknown")
                : App.S("M.Free", Format.Bytes(volume.FreeBytes), laufwerk);

            _cacheRows.Add(new CacheRow(
                laufwerk,
                volume.MaxBytes > 0 ? Math.Min(100, 100.0 * volume.UsedBytes / volume.MaxBytes) : 0,
                volume.MaxBytes > 0
                    ? App.S("M.CacheOf", Format.Bytes(volume.UsedBytes), Format.Bytes(volume.MaxBytes))
                    : App.S("M.CacheNoLimit", Format.Bytes(volume.UsedBytes)),
                frei));
        }

        // Ohne Freigabe gibt es kein Laufwerk. Eine leere Liste unter einer
        // Ueberschrift sieht aus wie ein Fehler.
        CacheText.Text = volumes.Count == 0 ? App.S("M.CacheNone") : "";
        CacheText.Visibility = volumes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Oeffnet den Baum eines Datentraegers.
    /// </summary>
    /// <remarks>
    /// Die Zeile nennt nur die Summe. Welche Dateien das sind und welche davon
    /// ihren Inhalt gerade hier halten, steht im Baum -- und dort laesst sich
    /// je Verzeichnis oder je Datei entscheiden, was lokal liegen soll.
    /// </remarks>
    private async void OnVolumeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CacheRow zeile) return;

        // Alle Freigaben, die auf diesem Datentraeger liegen. Das Limit gilt
        // je Datentraeger, also gehoeren sie in einen Baum.
        var wurzel = Path.GetPathRoot(zeile.Drive + Path.DirectorySeparatorChar) ?? zeile.Drive;

        var shares = _rows
            .Where(r => r.Share is not null)
            .Where(r => string.Equals(
                Path.GetPathRoot(r.Share!.Config.LocalPath), wurzel, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Host: r.Share!, r.Name))
            .ToList();

        var fenster = new VolumeWindow(zeile.Drive, zeile.Text, shares) { Owner = this };
        if (fenster.ShowDialog() != true || fenster.Auftraege.Count == 0) return;

        Status(App.S("M.VolumeWorking", Format.Count(fenster.Auftraege.Count)));

        // Im Hintergrund: das Anfordern der Inhalte geht ueber die Leitung,
        // und der Baum kann ganze Zweige auf einmal betreffen.
        await Task.Run(() =>
        {
            var dateien = 0;
            long bytes = 0;

            foreach (var gruppe in fenster.Auftraege.GroupBy(a => (a.Host, a.Lokal)))
            {
                var pfade = gruppe.Select(a => gruppe.Key.Host.LocalPathOf(a.Path)).ToList();

                try
                {
                    var (n, b) = gruppe.Key.Host.SetLocal(pfade, gruppe.Key.Lokal);
                    dateien += n;
                    bytes += b;
                }
                catch (Exception ex)
                {
                    AppendLog($"[{gruppe.Key.Host.FolderId}] {ex.Message}");
                }
            }

            Dispatcher.BeginInvoke(() =>
                Status(App.S("M.VolumeDone", Format.Count(dateien), Format.Bytes(bytes))));
        });

        RefreshRows();
    }

    private void UpdateThumbnails()
    {
        if (DateTime.UtcNow - _thumbsRead < TimeSpan.FromSeconds(5)) return;
        _thumbsRead = DateTime.UtcNow;

        var (count, bytes) = _rows.Select(r => r.Share).OfType<ShareHost>()
            .Select(s => s.ThumbnailUsage())
            .Aggregate((Count: 0, Bytes: 0L), (a, b) => (a.Count + b.Count, a.Bytes + b.Bytes));

        ThumbText.Text = App.S("M.PreviewShort", Format.Count(count), Format.Bytes(bytes));
        ThumbText.ToolTip = _config.ThumbnailDirectory;

        // Die Summe ueber alle Freigaben. Gezaehlt wird der Index, nicht die
        // Platte: er sagt, was zur Freigabe gehoert, unabhaengig davon, was
        // davon gerade lokal liegt.
        var shares = _rows.Select(r => r.Share).OfType<ShareHost>().ToList();
        OverallText.Text = App.S("M.OverallShort",
            Format.Count(shares.Sum(s => (long)s.IndexFiles)),
            Format.Bytes(shares.Sum(s => s.IndexTotalBytes)));
    }

    private void OnRangeChanged(object sender, RoutedEventArgs e) => UpdateThroughput();

    // ------------------------------------------------------------ Spalten

    /// <summary>
    /// Rechtsklick auf die Kopfzeile blendet Spalten ein und aus.
    /// </summary>
    /// <remarks>
    /// Name und Status bleiben immer sichtbar. Ohne Bezeichnung und ohne
    /// Zustand waere die Tabelle nur noch eine Ansammlung von Zahlen.
    /// </remarks>
    /// <summary>
    /// Die Beschriftung einer Spalte im Klartext.
    /// </summary>
    /// <remarks>
    /// Der Kopf mancher Spalten ist ein gezeichnetes Symbol. Als Menueintrag
    /// taugt es nicht, und dasselbe Element liesse sich ohnehin nicht an zwei
    /// Stellen zugleich einhaengen. Was das Symbol bedeutet, steht in seinem
    /// Tooltip -- dort wird es abgeholt.
    /// </remarks>
    private static string ColumnLabel(DataGridColumn column) => column.Header switch
    {
        string text => text,
        FrameworkElement element when element.ToolTip is string hint => hint,
        FrameworkElement element when element.ToolTip is TextBlock block => block.Text,
        _ => column.SortMemberPath ?? ""
    };

    private void BuildColumnMenu()
    {
        var menu = new ContextMenu();
        _columnItems.Clear();

        // 0 ist das Winkelzeichen, 1 der Name, 2 der Status. Die drei stehen
        // nicht zur Wahl: ohne sie waere die Zeile nicht mehr zu bedienen.
        foreach (var column in ShareGrid.Columns.Skip(3))
        {
            var item = new MenuItem
            {
                Header = ColumnLabel(column),
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true
            };

            var spalte = column;
            item.Click += (_, _) =>
            {
                spalte.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveColumns();
            };

            menu.Items.Add(item);
            _columnItems.Add((column, item));
        }

        // An die Kopfzeile, nicht an das ganze Gitter. Ein Rechtsklick auf
        // eine Zeile bezieht sich auf die Zeile, nicht auf die Spalten.
        var basis = TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var kopfzeile = basis is null
            ? new Style(typeof(DataGridColumnHeader))
            : new Style(typeof(DataGridColumnHeader), basis);

        kopfzeile.Setters.Add(new Setter(ContextMenuProperty, menu));
        _columnMenu = menu;
        ShareGrid.ColumnHeaderStyle = kopfzeile;

        // Der Kopf steht ueber seinen Werten, nicht daneben. Zahlen und
        // gezeichnete Symbole stehen in der Mitte ihrer Spalte; ein links
        // ausgerichteter Kopf darueber sieht aus, als gehoerte er zur Spalte
        // davor.
        //
        // Ausgenommen bleiben Name, Status und Ordner: dort steht Text, und
        // Text liest sich an einer gemeinsamen linken Kante besser.
        var mitte = new Style(typeof(DataGridColumnHeader), kopfzeile);
        mitte.Setters.Add(new Setter(
            Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));

        foreach (var column in ShareGrid.Columns.Skip(2))
        {
            if (ReferenceEquals(column, ColPath)) continue;
            column.HeaderStyle = mitte;
        }
    }

    /// <summary>
    /// Was sich mit einem Share tun lässt, direkt am Share.
    /// </summary>
    /// <remarks>
    /// Eine Knopfleiste über der Tabelle bot dieselben Befehle. Die Knöpfe
    /// galten für die ausgewählte Zeile, standen aber nicht bei ihr.
    /// </remarks>
    private void BuildRowMenu()
    {
        _menuConnect = Eintrag("S.Menu.Connect", OnAcceptFolder);
        _menuPause = Eintrag("S.Menu.Pause", OnPauseShare);
        _menuOpen = Eintrag("S.Menu.Open", OnOpenFolder);
        _menuSettings = Eintrag("S.Menu.Settings", OnShowSettings);
        _menuUnbind = Eintrag("S.Menu.Unbind", OnUnbind);
        _menuRescan = Eintrag("S.Menu.Rescan", OnRescan);
        _menuRebuild = Eintrag("S.Menu.Rebuild", OnRebuild);
        _menuResync = Eintrag("S.Menu.Resync", OnResync);
        _menuMarker = Eintrag("S.Menu.FixMarker", OnFixMarker);

        var menu = new ContextMenu();
        menu.Items.Add(_menuConnect);
        menu.Items.Add(_menuPause);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuOpen);
        menu.Items.Add(_menuSettings);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuRescan);
        menu.Items.Add(_menuRebuild);
        menu.Items.Add(_menuResync);
        menu.Items.Add(_menuMarker);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuUnbind);

        ShareGrid.ContextMenu = menu;

        // Ohne ausgewaehlte Zeile gibt es nichts zu tun. Das Menue bleibt dann zu.
        ShareGrid.ContextMenuOpening += (_, e) => { if (_row is null) e.Handled = true; };
    }

    /// <summary>
    /// Sucht sofort nach Aenderungen im Ordner.
    /// </summary>
    /// <remarks>
    /// Der Durchgang laeuft ohnehin jede Minute, und der Beobachter meldet
    /// jede Aenderung sofort. Gebraucht wird der Befehl, wenn der Beobachter
    /// Ereignisse verloren hat -- und dann will man nicht warten.
    /// </remarks>
    /// <summary>
    /// Stellt die Ordnermarkierung wieder her.
    /// </summary>
    /// <remarks>
    /// Nur erreichbar, solange sie fehlt. Wer sie wiederherstellt, erklaert
    /// damit, dass dies der richtige Ordner ist und sein Inhalt vollstaendig:
    /// was danach fehlt, gilt als geloescht und wird an die Gegenstellen
    /// weitergegeben. Deshalb geschieht es nur auf ausdrueckliche Anweisung.
    /// </remarks>
    private async void OnFixMarker(object sender, RoutedEventArgs e)
    {
        if (_row?.Share is not { } share) return;

        // Gefragt wird vorher, und die Frage nennt die Folge. Der Menuepunkt
        // allein waere ein Knopf ohne Warnung -- und was er auslaest, ist von
        // hier aus nicht mehr zurueckzunehmen.
        var antwort = Warnen(
            App.S("M.FixMarkerBody", _row.Name, share.Config.LocalPath),
            App.S("M.FixMarkerTitle"));

        if (antwort != MessageBoxResult.Yes) return;

        var name = _row.Name;

        share.MarkierungHerstellen();
        Status(App.S("M.MarkerRestored", name));

        // Und gleich neu anlaufen lassen.
        //
        // Die Pruefung findet beim Anlauf statt; die Freigabe stuende sonst
        // weiter auf "Fehler", obwohl der Grund behoben ist, und muesste ueber
        // einen Neustart des Programms wieder in Gang gebracht werden. Ein
        // Handgriff, der die Ursache beseitigt und die Wirkung stehenlaesst,
        // ist ein halber.
        try
        {
            await share.CommitAsync(_cts.Token);
        }
        catch (Exception)
        {
            // Scheitert es erneut, steht der Grund bereits im Protokoll und
            // an der Zeile. Ein zweites Mal dasselbe zu melden hilft nicht.
        }
        finally
        {
            RebuildRows();
            UpdateButtons();
        }
    }

    private void OnRescan(object sender, RoutedEventArgs e)
    {
        if (_row?.Share is not { } share) return;

        share.RescanNow();
        Status(App.S("M.Rescanning", _row.Name));
    }

    /// <summary>
    /// Verwirft den Bestand aller beteiligten Gegenstellen und fragt neu.
    /// </summary>
    /// <remarks>
    /// Von selbst geschieht das nur, wenn eine Gegenstelle ihre Index-Id
    /// wechselt. Bleibt ein Bestand aus anderem Grund stehen, ist das der
    /// einzige Weg heraus -- deshalb mit Rueckfrage, aber ohne Umschweife.
    /// </remarks>
    /// <summary>
    /// Rechnet die Blocklisten neu, statt sich auf Groesse und Zeit zu
    /// verlassen.
    /// </summary>
    private void OnRebuild(object sender, RoutedEventArgs e)
    {
        if (_row?.Share is not { } share) return;

        var anzahl = share.RebuildIndex();
        Status(App.S("M.Rebuilding", _row.Name, Format.Count(anzahl)));
    }

    private async void OnResync(object sender, RoutedEventArgs e)
    {
        if (_row?.Share is not { } share) return;

        if (MessageBox.Show(this, App.S("S2.ResyncAsk"), _row.Name,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var gefragt = 0;

        foreach (var teilnehmer in _row.Peers)
        {
            share.Resync(teilnehmer.Config.DeviceId);

            try
            {
                await teilnehmer.Host.RenegotiateAsync(_cts?.Token ?? default);
                gefragt++;
            }
            catch (Exception ex)
            {
                AppendLog($"[{teilnehmer.Display}] Neuabgleich: {ex.Message}");
            }
        }

        Status(App.S("M.Resyncing", _row.Name, gefragt));
    }

    private static MenuItem Eintrag(string schluessel, RoutedEventHandler klick)
    {
        var item = new MenuItem();

        // Als Verweis, nicht als Text. So folgt der Eintrag der eingestellten Sprache.
        item.SetResourceReference(HeaderedItemsControl.HeaderProperty, schluessel);
        item.Click += klick;
        return item;
    }

    private void OnGridRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Der Rechtsklick soll die Zeile treffen, auf die er zeigt. Sonst
        // gälte das Menü für die vorher ausgewählte Zeile.
        var quelle = e.OriginalSource as DependencyObject;
        while (quelle is not null and not DataGridRow)
            quelle = System.Windows.Media.VisualTreeHelper.GetParent(quelle);

        if (quelle is DataGridRow zeile) zeile.IsSelected = true;
    }

    private string ColumnFile => Path.Combine(HomeDirectory, "gui-spalten.txt");

    /// <summary>
    /// Sichtbarkeit, Breite und Reihenfolge der Spalten.
    /// </summary>
    /// <remarks>
    /// Je Zeile eine Spalte, durch Tabulatoren getrennt: laufende Nummer,
    /// sichtbar, Breite, Position. Die Breite ist eine Zahl in Punkten oder
    /// ein Stern fuer die Spalte, die den Rest ausfuellt.
    ///
    /// Die laufende Nummer ist die Reihenfolge, in der die Spalten angelegt
    /// sind, nicht ihre Position auf dem Schirm. Frueher stand hier der
    /// Kopftext. Das ging so lange gut, wie jede Spalte einen hatte: seit
    /// einige ein gezeichnetes Symbol tragen, heissen zwei von ihnen gleich,
    /// und beim Lesen stiessen zwei Zeilen mit demselben Schluessel
    /// aufeinander. Der Kopftext taugte ohnehin nicht, denn er wechselt mit
    /// der Sprache.
    ///
    /// Aeltere Dateien werden verworfen. Ihre Zeilen beginnen nicht mit einer
    /// Zahl, und ein Spaltenlayout ist kein Verlust, der eine Umrechnung
    /// lohnt.
    /// </remarks>
    private void SaveColumns()
    {
        try
        {
            Directory.CreateDirectory(HomeDirectory);

            File.WriteAllLines(ColumnFile, ShareGrid.Columns.Select((column, nummer) =>
            {
                var width = column.Width.IsStar
                    ? "*"
                    : ((int)Math.Round(column.ActualWidth)).ToString(CultureInfo.InvariantCulture);

                return string.Join('\t',
                    nummer.ToString(CultureInfo.InvariantCulture),
                    column.Visibility == Visibility.Visible ? "1" : "0",
                    width,
                    column.DisplayIndex.ToString(CultureInfo.InvariantCulture));
            }));
        }
        catch (IOException) { /* die Spaltenauswahl wird dann beim naechsten Mal geschrieben */ }
    }

    private void RestoreColumns()
    {
        if (!File.Exists(ColumnFile)) return;

        try
        {
            var zeilen = File.ReadAllLines(ColumnFile);

            // Gespeichert wird je Spaltennummer. Kommt eine Spalte hinzu,
            // verschieben sich alle dahinter, und die alten Masse gehoerten
            // dann zur jeweils falschen Spalte. Bei abweichender Anzahl also
            // lieber von vorn.
            if (zeilen.Length != ShareGrid.Columns.Count) return;

            var stored = new Dictionary<int, string[]>();

            foreach (var line in zeilen)
            {
                var parts = line.Split('\t');

                if (parts.Length < 4
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nummer)
                    || nummer < 0 || nummer >= ShareGrid.Columns.Count)
                {
                    // Eine Datei aus einer aelteren Version. Die Vorgabe bleibt.
                    return;
                }

                stored[nummer] = parts;
            }

            // Nach gespeicherter Position sortiert zuweisen. DataGrid schiebt
            // beim Setzen von DisplayIndex die uebrigen Spalten weiter; in
            // aufsteigender Reihenfolge bleibt das Ergebnis vorhersagbar.
            foreach (var (nummer, parts) in stored.OrderBy(e =>
                         int.Parse(e.Value[3], CultureInfo.InvariantCulture)))
            {
                var column = ShareGrid.Columns[nummer];

                // Winkelzeichen, Name und Status bleiben immer sichtbar.
                if (nummer >= 3)
                    column.Visibility = parts[1] == "1" ? Visibility.Visible : Visibility.Collapsed;

                if (parts[2] == "*")
                    column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                else if (double.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var width)
                         && width >= 20)
                    column.Width = new DataGridLength(width);

                if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 0 && index < ShareGrid.Columns.Count)
                {
                    try { column.DisplayIndex = index; }
                    catch (ArgumentException) { /* Reihenfolge bleibt, wie sie ist */ }
                }
            }

            UpdateColumnMenu();
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException)
        {
            // Ein Spaltenlayout ist kein Grund, das Fenster nicht zu oeffnen.
        }
    }

    private void UpdateColumnMenu()
    {
        foreach (var (column, item) in _columnItems)
            item.IsChecked = column.Visibility == Visibility.Visible;
    }


    /// <summary>
    /// Was der Cache gerade hält.
    /// </summary>
    /// <remarks>
    /// Die Buchführung liegt in den laufenden Freigaben. Läuft keine, ist die
    /// Zahl nicht bekannt, und die Anzeige meldet das statt einer Null. Die
    /// Vorschaubilder werden unabhaengig davon gezaehlt, denn sie liegen als
    /// Dateien vor.
    /// </remarks>
    private string CacheUsage()
    {
        var shares = _rows.Select(r => r.Share).OfType<ShareHost>().ToList();
        var thumbs = Vorschau().Usage();
        var preview = App.S("M.Previews", Format.Count(thumbs.Count), Format.Bytes(thumbs.Bytes));

        if (shares.Count == 0)
            return App.S("M.UsedUnknown", preview);

        var files = shares.Sum(s => s.CacheFileCount);
        var bytes = shares.Sum(s => s.CacheUsedBytes);

        return App.S("M.Used", Format.Bytes(bytes), Format.Count(files), preview);
    }

    /// <summary>
    /// Leert den Cache und die Vorschaubilder und meldet, was frei wurde.
    /// </summary>
    /// <remarks>
    /// Beides zusammen, wie es der Einstellungsdialog anfordert. Die
    /// Vorschaubilder liegen im selben Verzeichnis und entstehen on-demand
    /// neu.
    /// </remarks>
    /// <summary>Gibt auf einem Datentraeger frei, was freigegeben werden darf.</summary>
    private async Task<string> ReleaseVolumeAsync(string root)
    {
        var (files, bytes) = await _runtime.Cache.ClearAsync(root);
        Refreshed();

        return files == 0
            ? App.S("M.NothingLocal")
            : App.S("M.ReleasedVolume", root, Format.Count(files), Format.Bytes(bytes));
    }

    private Task<string> ClearThumbnailsAsync()
    {
        var thumbs = Vorschau().Clear();
        Refreshed();

        return Task.FromResult(thumbs.Count == 0
            ? App.S("M.NothingThumbs")
            : App.S("M.ClearedThumbs", Format.Count(thumbs.Count), Format.Bytes(thumbs.Bytes)));
    }

    private async Task<string> ClearAllAsync()
    {
        var (files, bytes) = await _runtime.Cache.ClearAsync();
        var thumbs = Vorschau().Clear();
        Refreshed();

        if (files == 0 && thumbs.Count == 0)
            return _rows.Any(r => r.Share is not null)
                ? App.S("M.NothingLocal")
                : App.S("M.NothingRunning");

        return App.S("M.Cleared",
            Format.Count(files), Format.Bytes(bytes),
            Format.Count(thumbs.Count), Format.Bytes(thumbs.Bytes));
    }

    /// <summary>Nur die zwischengespeicherten Dateien; die Vorschaubilder bleiben.</summary>
    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, App.S("G.ClearBody"), App.S("G.ClearTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var (files, bytes) = await _runtime.Cache.ClearAsync();
        Refreshed();

        Status(files == 0
            ? _rows.Any(r => r.Share is not null) ? App.S("M.NothingLocal") : App.S("M.NothingRunning")
            : App.S("M.ClearedCache", Format.Count(files), Format.Bytes(bytes)));
    }

    /// <summary>Nur den Vorrat an Vorschaubildern.</summary>
    private void OnClearThumbnails(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, App.S("G.ClearThumbsBody"), App.S("G.ClearThumbsTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var thumbs = Vorschau().Clear();
        Refreshed();

        Status(thumbs.Count == 0
            ? App.S("M.NothingThumbs")
            : App.S("M.ClearedThumbs", Format.Count(thumbs.Count), Format.Bytes(thumbs.Bytes)));
    }

    /// <summary>Zahlen sofort neu erheben statt bis zum naechsten Takt zu warten.</summary>
    private void Refreshed()
    {
        _thumbsRead = DateTime.MinValue;
        RefreshRows();
    }

    // ------------------------------------------------------------ Anrufe

    /// <summary>
    /// Setzt auf, was dieses Gerät im Netz sichtbar und erreichbar macht:
    /// den Lauscher, den Ruf ins eigene Netz und die Anmeldung beim
    /// Erkennungsserver.
    /// </summary>
    /// <remarks>
    /// Nach jedem Laden neu: alles drei haengt an der Geraete-Identitaet und
    /// am Port, und beides kommt von dort.
    ///
    /// Ein Ruf ins Netz ohne laufenden Lauscher nennt eine Adresse, unter der
    /// niemand antwortet. Erkennung und Anmeldung haengen deshalb am Lauscher.
    /// </remarks>
    private async void RestartNetwork()
    {
        await StopNetworkAsync();

        if (_identity is null || !_config.Listen) return;

        var listener = new BepListener(_identity, _config.DeviceName, AppendLog);
        listener.Incoming += OnIncoming;

        if (!listener.Start(_config.ListenPort))
        {
            await listener.DisposeAsync();
            return;
        }

        _listener = listener;
        AppendLog($"Nehme Verbindungen auf Port {listener.Port} entgegen.");

        if (_config.LocalDiscovery)
        {
            var local = new LocalDiscovery(_identity, listener.Port, AppendLog);
            if (local.Start())
            {
                _local = local;
                _runtime.Local = local;
                AppendLog($"Rufe alle 30 Sekunden ins eigene Netz (UDP {LocalDiscovery.Port}).");
            }
            else
            {
                await local.DisposeAsync();
            }
        }

        if (_config.Announce)
        {
            _announcer = new GlobalAnnouncer(
                _config.AnnounceServers, _identity, listener.Port, AppendLog);
            _announcer.Start();
        }
    }

    private async Task StopNetworkAsync()
    {
        // Erst freigeben, dann neu belegen. Sonst sind die Ports durch das
        // eigene Programm belegt.
        var listener = _listener;
        var local = _local;
        var announcer = _announcer;

        _listener = null;
        _local = null;
        _announcer = null;
        _runtime.Local = null;

        if (announcer is not null) await announcer.DisposeAsync();
        if (local is not null) await local.DisposeAsync();
        if (listener is not null) await listener.DisposeAsync();
    }

    private void OnIncoming(BepConnection connection, IPEndPoint? remote)
        => Dispatcher.Invoke(() => HandleIncoming(connection, remote));

    /// <summary>
    /// Ein Geraet hat angerufen. Bekannte Geraete werden angenommen, bei allen
    /// anderen wird nachgefragt. Ohne Bestaetigung kommt kein Geraet in die
    /// Liste.
    /// </summary>
    private async void HandleIncoming(BepConnection connection, IPEndPoint? remote)
    {
        var id = connection.PeerId.ToString();
        var name = connection.PeerHello.DeviceName;
        var address = AddressOf(remote);

        var peer = _peers.FirstOrDefault(
            p => string.Equals(p.Config.DeviceId, id, StringComparison.OrdinalIgnoreCase));

        if (peer is null)
        {
            AppendLog($"{name} von {address} moechte sich verbinden ({id[..7]}).");
            Status(App.S("M.WantsToConnect", name));

            var answer = Ask(
                App.S("M.WantsToConnectBody", name, address, id),
                App.S("M.UnknownPeer"));

            if (answer != MessageBoxResult.Yes)
            {
                await connection.DisposeAsync("abgelehnt");
                Status(App.S("M.Rejected", name));
                return;
            }

            _config.Peers.Add(new PeerConfig { Name = name, Address = address, DeviceId = id });
            Persist();
            Load();

            peer = _peers.FirstOrDefault(p => p.Config.DeviceId == id);
        }

        if (peer is null)
        {
            // Das Eintragen ist nicht durchgekommen. Ohne Eintrag gibt es
            // niemanden, der die Verbindung fuehren koennte.
            AppendLog($"{name} von {address} liess sich nicht eintragen.");
            await connection.DisposeAsync("nicht eingetragen");
            return;
        }

        if (peer.Host.State is PeerState.Verbunden or PeerState.Verbindet)
        {
            // Eine zweite Verbindung zur selben Gegenstelle wird nicht
            // gebraucht. Beide Seiten waehlen einander an, das trifft sich
            // regelmaessig; eine der beiden muss weichen.
            //
            // Der Grund geht mit, und die Zeile steht auch im eigenen
            // Protokoll: sonst wirkt das Abweisen auf beiden Seiten wie ein
            // Abriss, den niemand zu verantworten scheint.
            AppendLog($"[{peer.Display}] zweite Verbindung von {address} abgewiesen, es besteht bereits eine.");
            await connection.DisposeAsync("bereits verbunden");
            return;
        }

        try
        {
            await peer.Host.AcceptAsync(
                connection, _config.SharesOf(peer.Config), _cts.Token);
        }
        catch (Exception ex)
        {
            Status($"[{peer.Display}] {ex.Message}");
        }
        finally
        {
            RebuildRows();
        }
    }

    /// <summary>
    /// Fragt nach. Ein Fenster, das noch nicht gezeigt wurde, darf kein
    /// Besitzer sein. Der Anruf kann eintreffen, bevor das Fenster steht.
    /// </summary>
    /// <summary>
    /// Fragt vor einem Schritt, der Folgen hat.
    /// </summary>
    /// <remarks>
    /// Vorbelegt ist "Nein". Wer die Frage mit der Eingabetaste wegdrueckt,
    /// ohne sie gelesen zu haben, soll damit nichts ausloesen.
    /// </remarks>
    private MessageBoxResult Warnen(string text, string caption)
        => IsLoaded
            ? MessageBox.Show(this, text, caption,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(text, caption,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

    private MessageBoxResult Ask(string text, string caption)
        => IsLoaded
            ? MessageBox.Show(this, text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question);

    /// <summary>
    /// Unter welcher Adresse wir die Gegenstelle spaeter selbst erreichen.
    /// </summary>
    /// <remarks>
    /// Ihr Quellport ist fluechtig und zum Zurueckrufen nicht zu gebrauchen.
    /// Gemeint ist der Port, auf dem sie ihrerseits lauscht. Bei Syncthing ist
    /// das 22000.
    /// </remarks>
    private static string AddressOf(IPEndPoint? remote)
    {
        if (remote is null) return "";

        var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        return $"{address}:22000";
    }

    // ------------------------------------------------------------ Gegenstellen

    private async Task ConnectAsync(PeerItem item)
    {
        var shares = _config.SharesOf(item.Config);
        try
        {
            await item.Host.ConnectAsync(shares, _cts.Token);
        }
        catch (Exception ex)
        {
            Status($"[{item.Display}] {ex.Message}");
        }
        finally
        {
            RebuildRows();
        }
    }

    private void OnManagePeers(object sender, RoutedEventArgs e)
    {
        var dialog = new PeerManagerWindow(_peers, AddPeer, EditPeer, TogglePeer, RemovePeerAsync) { Owner = this };
        dialog.ShowDialog();
        RebuildRows();
    }

    private void AddPeer()
    {
        var dialog = new PeerDialog(null, _config.Shares) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _config.Peers.Add(dialog.Result);
        ApplySharing(dialog.Result.DeviceId, dialog.SharedFolders);
        Persist();
        Load();
        Status(App.S("M.PeerAdded", dialog.Result.Display));
    }

    /// <summary>
    /// Aendert eine bestehende Gegenstelle. Ohne diesen Weg waeren Adresse,
    /// Erkennung und Relay einmalig beim Anlegen zu entscheiden.
    /// </summary>
    private void EditPeer(PeerItem item)
    {
        var before = item.Config.DeviceId;

        var dialog = new PeerDialog(item.Config, _config.Shares) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // Die Freigaben zeigen auf die alte ID. Ohne das Nachziehen haengen
        // sie an einer Gegenstelle, die es nicht mehr gibt.
        if (!string.Equals(before, item.Config.DeviceId, StringComparison.Ordinal))
        {
            foreach (var share in _config.Shares)
            {
                if (share.PeerDeviceId == before) share.PeerDeviceId = item.Config.DeviceId;

                var stelle = share.PeerDeviceIds.FindIndex(
                    d => d.Equals(before, StringComparison.OrdinalIgnoreCase));

                if (stelle >= 0) share.PeerDeviceIds[stelle] = item.Config.DeviceId;
            }
        }

        ApplySharing(item.Config.DeviceId, dialog.SharedFolders);

        Persist();
        Load();
        Status(App.S("M.PeerChanged", item.Config.Display));
    }

    /// <summary>
    /// Traegt ein, an welchen Ordnern dieses Geraet teilnimmt.
    /// </summary>
    /// <remarks>
    /// Dieselbe Liste, die der Reiter "Teilen" im Ordner-Dialog fuehrt, nur
    /// von der anderen Seite betrachtet. Beide schreiben in
    /// <see cref="ShareConfig.PeerDeviceIds"/>.
    /// </remarks>
    private void ApplySharing(string deviceId, IReadOnlyList<string> folders)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        foreach (var share in _config.Shares)
        {
            var soll = folders.Contains(share.FolderId, StringComparer.Ordinal);
            var ist = share.PeerDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase);

            if (soll && !ist) share.PeerDeviceIds.Add(deviceId);
            if (!soll && ist)
                share.PeerDeviceIds.RemoveAll(d => d.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task TogglePeer(PeerItem item)
    {
        if (item.Host.State == PeerState.Verbunden) await item.Host.DisconnectAsync();
        else await ConnectAsync(item);
    }

    private async Task RemovePeerAsync(PeerItem item)
    {
        var shares = _config.SharesOf(item.Config).ToList();
        var question = shares.Count == 0
            ? App.S("M.RemovePeer", item.Display)
            : App.S("M.RemovePeerShares", item.Display, shares.Count);

        if (MessageBox.Show(question, App.S("M.Remove"), MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;

        foreach (var share in shares)
        {
            await item.Host.UnbindAsync(share.FolderId);
            _config.Shares.Remove(share);
        }

        await item.Host.DisposeAsync();
        _config.Peers.Remove(item.Config);
        Persist();
        Load();
    }

    private void OnShowPeers(object sender, RoutedEventArgs e)
    {
        // Der Klick kommt aus einer Zelle. Gemeint ist deren Datensatz, nicht
        // zwangslaeufig die ausgewaehlte Zeile.
        if ((sender as Hyperlink)?.DataContext is not ShareRow row) return;

        new SharePeersWindow(row, Persist) { Owner = this }.ShowDialog();
    }

    // ------------------------------------------------------------ Freigaben

    /// <summary>
    /// Übernimmt einen angebotenen Ordner, aber erst nach einer Rückfrage.
    /// </summary>
    /// <remarks>
    /// In zwei Schritten: erst den Index holen, damit im Dialog steht, was der
    /// Ordner enthält. Im Explorer entsteht dabei nichts. Erst die Bestätigung
    /// legt den Ordner an. Ein Abbruch hinterlässt nichts.
    /// </remarks>
    private async void OnAcceptFolder(object sender, RoutedEventArgs e)
    {
        if (_row is null || _row.Accepted) return;

        var row = _row;
        var draft = new ShareConfig
        {
            FolderId = row.FolderId,
            PeerDeviceId = row.Peer.Config.DeviceId,
            PeerDeviceIds = [.. row.Peers.Select(p => p.Config.DeviceId)],
            Label = row.Label,
            LocalPath = Path.Combine(_config.SharesRootOrDefault, row.FolderId),
            Mode = ShareMode.OnDemand
        };

        Status(App.S("M.Asking", row.Name));

        // Ein Fenster statt einer Zeile am unteren Rand. Der Index eines
        // grossen Ordners braucht Minuten, und in dieser Zeit sah es aus, als
        // geschehe nichts -- der Satz in der Statuszeile steht an einer
        // Stelle, an der niemand hinsieht, waehrend er auf einen Dialog
        // wartet.
        var lauf = new ProgressWindow(row.Name) { Owner = this };
        lauf.Show();

        // Der Abbruch des Anwenders und das Beenden des Programms enden
        // beide hier. Ohne die Verknuepfung liefe das Einlesen nach dem
        // Schliessen des Fensters weiter.
        using var frist = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, lauf.Abbruch);

        ShareHost host;
        try
        {
            host = await row.Peer.Host.PrepareAsync(draft, frist.Token, lauf.Verfolge);
        }
        catch (Exception ex)
        {
            lauf.Close();

            // Angelegt war der Ordner schon, bevor der Index angefordert
            // wurde. Bleibt er stehen, findet der naechste Versuch ihn in
            // der Ablage wieder -- ohne Index, ohne Platzhalter -- und holt
            // ihn nicht neu.
            if (lauf.Ordner is { } begonnen)
            {
                try { await row.Peer.Host.DiscardAsync(begonnen); }
                catch (Exception weg) { AppendLog($"[{row.FolderId}] verworfen: {weg.Message}"); }
            }

            Status(lauf.Abgebrochen
                ? App.S("M.NotConnected", row.Name)
                : App.S("M.ContentUnavailable", row.Name, ex.Message));

            RebuildRows();
            return;
        }

        lauf.Close();

        var dialog = new AcceptShareWindow(
            draft, HomeDirectory, row.Name, AndereWurzeln(draft)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            // Ein Abbruch soll nichts hinterlassen. Scheitert das Aufraeumen,
            // ist das eine Meldung wert -- aber die Zeile muss danach trotzdem
            // neu gebaut werden, sonst steht dort eine halbe Freigabe.
            try { await row.Peer.Host.DiscardAsync(host); }
            catch (Exception ex) { AppendLog($"[{row.FolderId}] verworfen: {ex.Message}"); }

            Status(App.S("M.NotConnected", row.Name));
            RebuildRows();
            return;
        }

        _config.Shares.Add(draft);

        // Das Verbinden ist der Augenblick, in dem erklaert wird, dass dieser
        // Ordner der richtige ist. Genau dort gehoert die Markierung hin.
        if (!ShareHost.MarkierungAnlegen(draft.LocalPath, out var markerFehler))
            AppendLog($"[{draft.FolderId}] Ordnermarkierung liess sich nicht anlegen: {markerFehler}");

        // Der erste Share auf einem Laufwerk legt dessen Werte fest. Zehn
        // Prozent des Datentraegers werden dabei einmal ausgerechnet und
        // stehen danach als Zahl in der Datei.
        // _runtime teilt sich die Liste mit _config, ein Aufruf genuegt.
        _config.EnsureLimits(draft.LocalPath);
        Persist();

        Status(App.S("M.Connecting", row.Name, draft.LocalPath));

        // Ohne eigenes Fenster. Ab hier steht der Ordner in der Uebersicht,
        // und seine Zeile zeigt dieselbe Phase mit demselben Balken. Ein
        // zweites Fenster daneben sagt nichts Neues und verdeckt die Zeile,
        // die es sagt.
        try
        {
            await row.Peer.Host.CommitAsync(host, _cts.Token);
            Status(App.S("M.Connected", row.Name));
        }
        catch (Exception ex)
        {
            Status(App.S("M.ConnectFailed", ex.Message));
        }

        RebuildRows();
    }

    private async void OnUnbind(object sender, RoutedEventArgs e)
    {
        if (_row is null || !_row.Configured) return;

        var share = _config.Shares.FirstOrDefault(s => s.FolderId == _row.FolderId);
        var path = share?.LocalPath ?? "";

        if (MessageBox.Show(
                App.S("M.UnbindBody", _row.Name, path),
                App.S("S.Menu.Unbind"), MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;

        // Jede beteiligte Gegenstelle nimmt ihre Verbindung heraus. Aufgeloest
        // wird der Ordner dabei einmal, von der ersten -- die uebrigen finden
        // ihn nicht mehr in der Ablage.
        foreach (var teilnehmer in _row.Peers)
            await teilnehmer.Host.UnbindAsync(_row.FolderId);
        if (share is not null) _config.Shares.Remove(share);
        Persist();

        RebuildRows();
    }

    private void OnPauseShare(object sender, RoutedEventArgs e)
    {
        var share = _row?.Share;
        if (share is null) return;

        if (share.State == ShareState.Pausiert) share.Resume();
        else share.Pause();

        RefreshRows();
    }

    /// <summary>
    /// Die Ordner der uebrigen Freigaben, ohne die genannte.
    /// </summary>
    /// <remarks>
    /// Die Dialoge pruefen damit den gewaehlten Pfad. Verglichen wird die
    /// Freigabe selbst und nicht ihr Pfad: beim Uebernehmen steht der
    /// Entwurf noch nicht in der Liste, beim Aendern steht er darin.
    /// </remarks>
    private IReadOnlyList<string> AndereWurzeln(ShareConfig ausser)
        => [.. _config.Shares
            .Where(s => !ReferenceEquals(s, ausser))
            .Select(s => s.LocalPath)];

    private void OnShowSettings(object sender, RoutedEventArgs e)
    {
        var share = _row?.Share?.Config;
        if (share is null) { Status(App.S("M.NoShareSelected")); return; }

        var dialog = new ShareSettingsWindow(
            share, _config.Peers, HomeDirectory, _row!.Name,
            id => _peers.FirstOrDefault(p =>
                p.Config.DeviceId.Equals(id, StringComparison.OrdinalIgnoreCase))?.Host.ReportedName)
        { Owner = this };
        if (dialog.ShowDialog() != true) return;

        Persist();
        var scope = share.Included.Count == 0
            ? App.S("M.ScopeAll")
            : App.S("M.ScopeBranches", share.Included.Count);

        Status(App.S("M.SavedScope", scope));
        RefreshRows();

        // Bisher wurde die Auswahl nur aufgeschrieben. Sie galt fuer alles,
        // was danach kam, und liess liegen, was schon dastand -- auch nach
        // einem Neustart, denn kein Durchgang raeumt auf, was nicht mehr
        // dazugehoert.
        if (_row?.Share is not { } host) return;

        _ = Task.Run(() =>
        {
            // Zuerst die Muster: was sie treffen, gehoert gar nicht mehr zum
            // Abgleich und braucht auch nicht mehr geprueft zu werden, ob es
            // hier liegen darf.
            host.ApplyExplorerVisibility();

            var (ausgenommen, _) = host.PurgeIgnored();
            if (ausgenommen > 0)
                Dispatcher.BeginInvoke(() => Status(App.S("M.Purged", _row.Name, ausgenommen)));

            var (files, bytes) = host.PruneExcluded();

            // Und der Gegenweg: was wieder dazugehoert, muss auch wieder
            // angelegt werden. Ohne diesen Anstoss bleibt ein Zweig fort, den
            // jemand versehentlich abgewaehlt und gleich wieder angehakt hat.
            host.RequeueAll();

            if (files == 0) return;

            Dispatcher.Invoke(() =>
                Status(App.S("M.Pruned", Format.Count(files), Format.Bytes(bytes))));
        });
    }

    /// <summary>
    /// Der Doppelklick öffnet die Einstellungen des Shares.
    /// </summary>
    /// <remarks>
    /// Bei einem noch nicht verbundenen Share ist das der Verbinden-Dialog.
    /// Er stellt dieselben Fragen, nur zum ersten Mal. Den Ordner öffnet der
    /// Verweis in der Spalte "Ordner".
    /// </remarks>
    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Ein zweiter Klick war es also doch. Nicht umbenennen, sondern oeffnen.
        _renameTimer.Stop();

        if (_row is null) return;

        if (_row.Accepted) OnShowSettings(sender, e);
        else OnAcceptFolder(sender, e);
    }

    /// <summary>Der Verweis in der Spalte "Ordner".</summary>
    private void OnOpenPath(object sender, RoutedEventArgs e)
    {
        // Der Klick kommt aus einer Zelle. Gemeint ist deren Zeile, nicht
        // zwangsläufig die ausgewählte.
        if ((sender as System.Windows.Documents.Hyperlink)?.DataContext is ShareRow row)
            OpenFolder(row.Share?.Config.LocalPath);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
        => OpenFolder(_row?.Share?.Config.LocalPath);

    private void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        if (!Directory.Exists(path))
        {
            Status(App.S("M.NoPath", path));
            return;
        }

        try
        {
            // Ausdruecklich der Explorer. Ein anderer Dateimanager als
            // Standard wuerde die Platzhalter mit eigener Dekodierung
            // behandeln.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status(App.S("M.OpenFailed", ex.Message));
        }
    }

    private void OnShowDevice(object sender, RoutedEventArgs e)
        => new DeviceWindow(_identity?.Id.ToString() ?? "—", _configPath, _config, SaveWindowBehaviour)
        { Owner = this }.ShowDialog();

    /// <summary>
    /// Was der Geraetedialog an Fenster-Einstellungen aendert, gilt sofort. Die
    /// Werte werden geschrieben und angewandt, ein Neustart ist nicht noetig.
    /// </summary>
    private void SaveWindowBehaviour()
    {
        Persist();
        ApplyTray();
    }

    /// <summary>
    /// Das Symbol im Infobereich gibt es nur, wenn das X das Fenster verstecken
    /// soll. Ohne Symbol liesse sich ein verstecktes Fenster nicht mehr
    /// zurueckholen. Beides ist dieselbe Entscheidung.
    /// </summary>
    private void ApplyTray()
    {
        if (_config.CloseToTray)
        {
            if (_tray is null)
            {
                _tray = new TrayIcon(this, Quit, () => OnTogglePauseAll());
                _tray.ShowStatus = ZustandZeigen;
            }
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
        }
    }

    /// <summary>
    /// Beenden aus dem Kontextmenue des Symbols.
    /// </summary>
    /// <remarks>
    /// Ueber <see cref="Window.Close"/>, damit dieselbe Aufraeumlogik laeuft
    /// wie beim X: abmelden, Freigaben schliessen. Ein Shutdown daran vorbei
    /// liesse Platzhalter zurueck, die niemand mehr bedienen kann.
    /// </remarks>
    private void Quit()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// Was nach dem Absenden der Antwort geschieht.
    /// </summary>
    /// <remarks>
    /// Hier steht alles, was ein Fenster nach vorn holt. Waehrend der Befehl
    /// beantwortet wird, wartet der Datei-Manager auf die Antwort und
    /// bearbeitet keine Nachrichten -- ihn in diesem Augenblick in den
    /// Hintergrund zu schieben legt beide Programme still. Erst antworten,
    /// dann handeln.
    ///
    /// Geprueft wird ein zweites Mal statt gemerkt: dieser Aufruf kommt aus
    /// einem anderen Faden als der Befehl, und ein Zustand dazwischen waere
    /// eine Verabredung, die bei zwei Befehlen zugleich nicht mehr gilt.
    /// </remarks>
    private void NachDemBefehl(string befehl, IReadOnlyList<string> pfade)
    {
        if (befehl == "SHOW")
        {
            Dispatcher.BeginInvoke(Restore);
            return;
        }

        if (befehl != "ADD" || pfade.Count != 1 || !Directory.Exists(pfade[0])) return;

        var pfad = pfade[0];
        Dispatcher.BeginInvoke(() => { Restore(); _ = NeueFreigabe(pfad); });
    }

    /// <summary>
    /// Macht aus einem Ordner, der hier schon liegt, eine eigene Freigabe.
    /// </summary>
    /// <remarks>
    /// Der Gegenweg zum Uebernehmen. Dort wird ein Ordner angelegt, weil eine
    /// Gegenstelle ihn anbietet; hier wird ein Ordner angeboten, weil er
    /// dasteht. Auf den Index der Gegenstelle wird nicht gewartet -- sie muss
    /// den Ordner erst annehmen, und bis dahin ist der Bestand hier der
    /// ganze Bestand.
    /// </remarks>
    private async Task NeueFreigabe(string pfad)
    {
        var dialog = new NewShareWindow(
            pfad, _config.Shares, _config.Peers,
            id => _peers.FirstOrDefault(p =>
                p.Config.DeviceId.Equals(id, StringComparison.OrdinalIgnoreCase))?.Host.ReportedName)
        { Owner = this };

        if (dialog.ShowDialog() != true) return;

        var share = dialog.Result;
        _config.Shares.Add(share);

        // Wie beim Uebernehmen: die Markierung erklaert diesen Ordner zur
        // Wurzel einer Freigabe, und das Laufwerk bekommt seine Grenzen.
        if (!ShareHost.MarkierungAnlegen(share.LocalPath, out var markerFehler))
            AppendLog($"[{share.FolderId}] Ordnermarkierung liess sich nicht anlegen: {markerFehler}");

        _config.EnsureLimits(share.LocalPath);
        Persist();

        Status(App.S("M.ShareAdded", share.FolderId, share.LocalPath));

        // Jede beteiligte und erreichbare Gegenstelle bekommt den Ordner
        // sofort angekuendigt. Die uebrigen finden ihn beim naechsten
        // Verbinden in ihrer Liste.
        foreach (var peer in _peers.Where(p =>
                     share.PeerDeviceIds.Contains(p.Config.DeviceId, StringComparer.OrdinalIgnoreCase)))
        {
            if (peer.Host.State != PeerState.Verbunden) continue;

            try { await peer.Host.AcceptAsync(share, _cts.Token); }
            catch (Exception ex) { Status($"[{share.FolderId}] {ex.Message}"); }
        }

        RebuildRows();
    }

    /// <summary>
    /// Fuehrt aus, was aus dem Kontextmenue kommt.
    /// </summary>
    /// <remarks>
    /// Die Erweiterung im Explorer schickt nur Pfade. Was daraus wird,
    /// entscheidet sich hier: sie kennt weder die Auswahl einer Freigabe noch
    /// die Platzhalter-Schwelle, und sie soll beides auch nicht kennen. Eine
    /// Erweiterung, die im fremden Prozess laeuft, haelt man klein.
    /// </remarks>
    private string OnCommand(string befehl, IReadOnlyList<string> pfade)
    {
        // Vor dem Sprung auf den Oberflaechen-Thread, und das ist noetig: eine
        // Vorschau wartet auf die Gegenstelle, bis zu zwanzig Sekunden, und
        // der Explorer fragt einen ganzen Ordner auf einmal ab. Auf dem
        // Oberflaechen-Thread stuende das Fenster so lange still. Der Erzeuger
        // wird ohnehin schon aus mehreren COM-Threads zugleich aufgerufen.
        if (befehl == "THUMB")
            return pfade.Count == 1 && ShareHost.ProduceThumbnail(pfade[0]) ? "1" : "";

        // Welcher Eintrag angeklickt wurde, und worauf. Ohne diese Zeile liess
        // sich nicht unterscheiden, ob ein Befehl nicht wirkte oder ob ein
        // anderer angekommen war als gemeint. Die blosse Auskunft nicht: sie
        // kommt bei jedem Rechtsklick und saehe wie eine Handlung aus.
        if (befehl != "STATE") AppendLog($"Kontextmenue: {befehl} fuer " +
                  string.Join(", ", pfade.Take(3).Select(p => $"\"{System.IO.Path.GetFileName(p)}\"")) +
                  (pfade.Count > 3 ? $" und {pfade.Count - 3} weitere" : ""));

        return Dispatcher.Invoke(() =>
        {
            // Vor der Suche nach der Freigabe: dieser Befehl nennt keine
            // Pfade, er kommt von einer zweiten Instanz, die sich gleich
            // wieder beendet.
            // Beide holen ein Fenster nach vorn und beantworten sich
            // deshalb nur. Getan wird es in NachDemBefehl, wenn die Antwort
            // heraus ist.
            if (befehl == "SHOW") return "";

            if (befehl == "ADD")
                return pfade.Count == 1 && Directory.Exists(pfade[0]) ? "" : App.S("C.OneFolder");

            var host = pfade.Select(ShareHost.Owning).OfType<ShareHost>().FirstOrDefault();
            if (host is null) return App.S("C.NoShare");

            switch (befehl)
            {
                // Nur eine Auskunft. Sie kommt aus dem Aufbau des
                // Kontextmenues und muss schnell sein.
                case "STATE":
                    return host.Haltung(pfade);

                case "PIN":
                case "FREE":
                {
                    // Weder hier noch jetzt.
                    //
                    // Freigeben oeffnet die Datei mit Schreibzugriff, und
                    // Windows laesst das warten, solange ein anderer sie in
                    // der Hand hat. In der Hand hat sie in diesem Augenblick
                    // der Datei-Manager -- er hat sie eben markiert und wartet
                    // hier auf unsere Antwort. Beide standen dann still.
                    //
                    // Auf dem Oberflaechen-Thread kaeme dazu, dass das Fenster
                    // nicht mehr zeichnet. Die Vorschau war aus demselben
                    // Grund schon ausgenommen.
                    var behalten = befehl == "PIN";
                    var auswahl = pfade.ToList();

                    _ = Task.Run(() =>
                    {
                        var (files, bytes) = host.SetLocal(auswahl, keep: behalten);
                        var satz = App.S(behalten ? "C.Pinned" : "C.Freed",
                            Format.Count(files), Format.Bytes(bytes));

                        AppendLog($"[{host.FolderId}] {satz}");
                        Dispatcher.BeginInvoke(() => Status(satz));
                    });

                    // Ohne Antwort und damit ohne Meldungsfenster: das
                    // Ergebnis steht wenige Augenblicke spaeter in der
                    // Statuszeile und im Protokoll. Auf es zu warten war
                    // gerade das Problem.
                    return "";
                }

                case "HIDE":
                    return Ausblenden(host, pfade);

                default:
                    return "";
            }
        });
    }

    /// <summary>
    /// Nimmt einen Zweig aus der Auswahl, mit derselben Sperre wie im Dialog.
    /// </summary>
    /// <remarks>
    /// Ausblenden entfernt. Erlaubt ist es nur, wenn die Gegenstelle jede
    /// Datei des Zweiges vollstaendig fuehrt -- sonst waere es kein
    /// Ausschliessen, sondern das Loeschen der letzten Kopie. Die Sperre sitzt
    /// hier und nicht nur im Baum: ueber das Kontextmenue kommt man an ihm
    /// vorbei.
    /// </remarks>
    private string Ausblenden(ShareHost host, IReadOnlyList<string> pfade)
    {
        var share = _config.Shares.FirstOrDefault(s => s.FolderId == host.FolderId);
        if (share is null) return App.S("C.NoShare");

        var namen = pfade.Select(host.RelativeNameOf).OfType<string>().ToList();
        if (namen.Count == 0) return App.S("C.NoShare");

        if (host.Blocking(namen) is var offen && offen > 0)
            return App.S("C.Blocked", Format.Count(offen));

        // Die Auswahl steht als Liste der eingeschlossenen Zweige, und die
        // fuehrt Praefixe. Ein Zweig laesst sich daraus nicht einfach
        // streichen -- die Ebenen darueber muessen erst ausgeschrieben
        // werden, sonst steht der Unterordner weiterhin in einem Praefix und
        // nichts geschieht.
        share.Included = host.AufgeklappteAuswahl(namen);

        Persist();

        _ = Task.Run(() =>
        {
            var (files, bytes) = host.PruneExcluded();
            host.RequeueAll();

            Dispatcher.Invoke(() =>
                Status(App.S("M.Pruned", Format.Count(files), Format.Bytes(bytes))));
        });

        return App.S("C.Hidden", Format.Count(namen.Count));
    }

    private StatusWindow? _status;

    /// <summary>
    /// Zeigt den Zustand am Rand, ohne das Hauptfenster zu holen.
    /// </summary>
    /// <remarks>
    /// Das Fenster wird einmal gebaut und danach nur noch gezeigt und
    /// versteckt. Es haengt an denselben Sammlungen wie die Uebersicht; ein
    /// neues Fenster je Klick muesste sie jedes Mal neu binden, und die
    /// laufenden Uebertragungen faenden sich in der Anzeige nicht wieder.
    /// </remarks>
    private void ZustandZeigen()
    {
        _status ??= new StatusWindow(
            _rows, _outgoing, _incoming,
            () => App.S(Zustand() switch
            {
                TrayStatus.Synchronisiert => "S.Tray.Syncing",
                TrayStatus.Erledigt => "S.Tray.Done",
                TrayStatus.Pausiert => "S.Tray.Paused",
                TrayStatus.Fehler => "S.Tray.Failed",
                _ => "S.Tray.Offline"
            }),
            () => _config.Paused,
            Restore,
            () => OnShowProgramSettings(this, new RoutedEventArgs()),
            () => OnTogglePauseAll(),
            OpenFolder);

        if (_status.IsVisible) _status.Hide();
        else _status.ZeigenAmRand();
    }

    /// <summary>Holt das Hauptfenster zurueck.</summary>
    /// <remarks>
    /// Activate allein genuegt nicht, wenn der Aufruf aus einem anderen
    /// Prozess kommt: Windows laesst ein Fenster nur von dem Programm nach
    /// vorn holen, das gerade die Eingabe hat. Der Umweg ueber Topmost ist
    /// der uebliche -- kurz nach oben, sofort wieder zurueck.
    /// </remarks>
    private void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        var oben = Topmost;
        Topmost = true;
        Topmost = oben;
    }

    /// <summary>
    /// Die Einstellungen des Programms. Sie haengen an keiner Freigabe. Sonst
    /// waeren sie fuer jemanden, der noch keine Freigabe hat, nicht erreichbar.
    /// </summary>
    private void OnShowProgramSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new ProgramSettingsWindow(
            _config, Path.GetDirectoryName(_configPath)!,
            () => _runtime.Cache.VolumesWithCandidates(),
            ReleaseVolumeAsync,
            () => Vorschau().Usage(),
            ClearThumbnailsAsync)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;

        Persist();
        App.ApplyTheme(_config.Theme);
        App.ApplyLanguage(_config.Language);

        // Ein Menue aus Windows Forms folgt keinem Woerterbuch von selbst.
        _tray?.Translate();

        RestartNetwork();
        Status(dialog.HomeChanged ? App.S("M.SavedHome") : App.S("M.Saved"));
    }

    // ------------------------------------------------------------ Anzeige

    /// <summary>Beide Richtungen zusammen, fuer die Zaehlungen.</summary>
    private IEnumerable<TransferInfo> Laufend() => _outgoing.Concat(_incoming);

    private void AddTransfer(TransferInfo transfer)
    {
        var liste = transfer.Direction == TransferDirection.Hinaus ? _outgoing : _incoming;
        liste.Insert(0, transfer);
        TrimTransfers();
    }

    private void TrimTransfers()
    {
        Kuerzen(_outgoing);
        Kuerzen(_incoming);

        var running = Laufend().Count(t => t.State == TransferState.Laeuft);
        var waiting = Laufend().Count(t => t.State == TransferState.Wartet);
        // Der Hinweis liegt hinter den Listen und ist nur zu sehen, solange
        // sie leer sind. Ueber ihnen naehme er dauerhaft eine Zeile weg.
        QueueText.Visibility = _outgoing.Count + _incoming.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        QueueText.Text = running + waiting == 0
            ? "nichts unterwegs"
            : $"{running} aktiv, {waiting} in der Warteschlange";
    }

    /// <summary>Laufende bleiben, von den abgeschlossenen nur die letzten paar.</summary>
    private static void Kuerzen(ObservableCollection<TransferInfo> liste)
    {
        foreach (var stale in liste
                     .Where(t => t.State is TransferState.Fertig or TransferState.Fehler)
                     .Skip(KeepFinished).ToList())
        {
            liste.Remove(stale);
        }
    }

    private void UpdateButtons()
    {
        var connected = _row?.Peer.Host.State == PeerState.Verbunden;

        // Uebernehmen braucht eine Leitung; alles andere betrifft den eigenen
        // Bestand. Trennen und Einstellungen gehen deshalb auch angehalten:
        // wirksam wird es beim Fortsetzen, vorbereiten muss man es jetzt
        // koennen.
        _menuConnect.IsEnabled = connected && _row is { Accepted: false, Configured: false };
        _menuUnbind.IsEnabled = _row is { Configured: true };
        _menuRescan.IsEnabled = _row is { Accepted: true };
        _menuRebuild.IsEnabled = _row is { Accepted: true };
        _menuResync.IsEnabled = _row is { Accepted: true } && connected;

        // Nur bei genau diesem Fehler, und dann auch nur sichtbar.
        //
        // Nicht "die Markierung fehlt" allein: das trifft auch auf eine
        // Freigabe zu, die aus einem anderen Grund noch nicht angelaufen ist.
        // Ein Eintrag, der oft dasteht und selten gemeint ist, laedt zum
        // Draufklicken ein -- und hier hat das Folgen.
        _menuMarker.IsEnabled = _row?.Share is { State: ShareState.Fehler, MarkierungFehlt: true };
        _menuMarker.Visibility = _menuMarker.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        _menuSettings.IsEnabled = _row is { Configured: true };
        _menuOpen.IsEnabled = _row is { Configured: true };

        var state = _row?.Share?.State;
        _menuPause.IsEnabled = state is ShareState.Bereit or ShareState.Pausiert;
        _menuPause.SetResourceReference(HeaderedItemsControl.HeaderProperty,
            state == ShareState.Pausiert ? "S.Menu.Resume" : "S.Menu.Pause");
    }

    /// <summary>Wann zuletzt zu welcher Datei gewarnt wurde.</summary>
    /// <remarks>
    /// Windows fragt nach einer abgelehnten Datei sofort wieder an. Ohne
    /// Sperre entstünde binnen Sekunden ein Stapel gleicher Fenster.
    /// </remarks>
    private readonly Dictionary<string, DateTime> _complained = [];

    private void ShowLimit(CacheLimitHit hit)
    {
        var key = $"{hit.FolderId}/{hit.Name}";
        if (_complained.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1))
            return;

        _complained[key] = DateTime.UtcNow;

        var name = Path.GetFileName(hit.Name);
        var text = hit.UsageLimit
            ? App.S("M.LimitUsage", name, Format.Bytes(hit.Needed), Format.Bytes(hit.Limit))
            : App.S("M.LimitFree", name, Format.Bytes(hit.Needed), Format.Bytes(hit.Limit));

        Status(text.Split('\n')[0]);
        MessageBox.Show(this, text, App.S("M.LimitTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Was noch ins Protokollfeld geschrieben werden muss.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _protokoll = new();

    /// <summary>So viele Zeichen bleiben im Feld stehen.</summary>
    /// <remarks>
    /// Ein TextBox misst seinen Text neu, wenn er sich aendert. Bei einem
    /// Feld, das ueber Stunden waechst, wird das langsam -- und zwar auf dem
    /// Faden, der auch das Fenster zeichnet.
    /// </remarks>
    private const int ProtokollGrenze = 400_000;

    /// <summary>
    /// Nimmt eine Protokollzeile entgegen.
    /// </summary>
    /// <remarks>
    /// Hier stand ein Dispatcher.Invoke, und das ist der synchrone: jeder
    /// Faden, der etwas zu sagen hatte, wartete darauf, dass die Oberflaeche
    /// es hinschreibt. Bei einem grossen Ordner kommen zehn Zeilen in der
    /// Sekunde, jede mit einem AppendText und einem ScrollToEnd auf einem
    /// Feld, das dabei immer laenger wird -- und alle uebrigen Faeden standen
    /// derweil in der Schlange. Das Programm antwortete nur noch alle paar
    /// Sekunden, und die Zeitstempel im Protokoll waren die des Schreibens
    /// und nicht die des Ereignisses.
    ///
    /// Jetzt wird nur eingereiht. Die Zeit wird hier genommen, damit sie
    /// stimmt; geschrieben wird einmal je Takt.
    /// </remarks>
    private void AppendLog(string line)
        => _protokoll.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");

    /// <summary>
    /// Schreibt alles Aufgelaufene in einem Zug.
    /// </summary>
    /// <remarks>
    /// Ans Ende gerollt wird nur, wenn dort schon jemand war. Wer weiter oben
    /// liest oder gerade etwas markiert, wird sonst bei jedem Takt
    /// weggerissen -- und genau das machte es unmoeglich, eine Stelle im
    /// Protokoll zu markieren.
    /// </remarks>
    private void FlushLog()
    {
        if (_protokoll.IsEmpty) return;

        var zeilen = new System.Text.StringBuilder();
        while (_protokoll.TryDequeue(out var zeile)) zeilen.AppendLine(zeile);

        // Solange jemand das Feld in der Hand hat, wird daran nicht gezogen
        // und nichts darin ersetzt.
        //
        // Beides riss den Rollbalken weg: das Rollen ans Ende, weil man beim
        // Ziehen des Reglers am unteren Rand als "am Ende" gilt, und das
        // Kuerzen, weil ein neu gesetzter Text die Mausfuehrung abbricht.
        var inBenutzung = LogBox.IsMouseOver
                          && System.Windows.Input.Mouse.LeftButton
                             == System.Windows.Input.MouseButtonState.Pressed;

        var amEnde = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;

        LogBox.AppendText(zeilen.ToString());

        // Gekuerzt wird erst deutlich ueber der Grenze und dann auf einmal.
        // Bei jedem Takt ein Stueck abzuschneiden hiesse, den Text bei jedem
        // Takt neu zu setzen -- und damit jede Markierung zu verlieren.
        if (!inBenutzung && LogBox.SelectionLength == 0
            && LogBox.Text.Length > ProtokollGrenze * 3 / 2)
        {
            // Am naechsten Zeilenanfang abschneiden, nicht mitten im Wort.
            var schnitt = LogBox.Text.IndexOf('\n', LogBox.Text.Length - ProtokollGrenze);
            if (schnitt > 0) LogBox.Text = LogBox.Text[(schnitt + 1)..];
        }

        if (amEnde && !inBenutzung) LogBox.ScrollToEnd();
    }

    private void Persist()
    {
        try { _config.Save(_configPath); }
        catch (Exception ex) { Status(App.S("M.SaveFailed", ex.Message)); }
    }

    private void Status(string message) => StatusBar.Text = message;

    // ------------------------------------------------------------ Ende

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Das X beendet das Programm nicht, es versteckt das Fenster. Beendet
        // wird ueber das Kontextmenue des Symbols, das hier ueber Quit()
        // ankommt.
        if (_tray is not null && !_exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tray?.Dispose();

        // Das Zustandsfenster wird versteckt, nicht geschlossen -- es soll
        // beim naechsten Klick sofort dastehen. Ein verstecktes Fenster zaehlt
        // aber als offenes, und solange eines offen ist, endet die Anwendung
        // nicht. Ohne diese Zeile blieb der Prozess nach "Beenden" liegen,
        // ohne Fenster und ohne Symbol.
        _status?.Close();
        _status = null;

        _refresh.Stop();
        _meter?.Dispose();
        SaveColumns();

        await StopNetworkAsync();
        await _cts.CancelAsync();

        // Ohne sauberes Abmelden bleiben Platzhalter zurueck, die niemand
        // mehr bedienen kann.
        foreach (var peer in _peers)
        {
            try { await peer.Host.DisposeAsync(); }
            catch { /* Fehler beim Beenden werden nicht mehr behandelt */ }
        }

        // Und zum Schluss ausdruecklich. Dieser Rueckruf ist async void: die
        // Anwendung schliesst das Fenster, sobald er zum ersten Mal wartet,
        // und was danach noch offen ist, kennt sie nicht mehr.
        Application.Current?.Shutdown();
    }
}
