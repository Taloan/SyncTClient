using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly ObservableCollection<TransferInfo> _transfers = [];
    private readonly ObservableCollection<PeerItem> _peers = [];
    private readonly ObservableCollection<ShareRow> _rows = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _cts = new();

    private AppConfig _config = new();
    private DeviceIdentity? _identity;
    private ThroughputMeter? _meter;
    private BepListener? _listener;
    private LocalDiscovery? _local;
    private GlobalAnnouncer? _announcer;
    private TrayIcon? _tray;
    private double _peak;

    /// <summary>
    /// Es wird wirklich beendet -- das X darf dann nicht mehr verstecken.
    /// </summary>
    private bool _exiting;

    /// <summary>Wieviele Vorschaubilder liegen, ueber alle Freigaben.</summary>
    /// <remarks>
    /// Gezaehlt wird ueber das Verzeichnis. Im Sekundentakt waere das
    /// verschwendete Arbeit -- Vorschaubilder entstehen langsam.
    /// </remarks>
    private DateTime _thumbsRead = DateTime.MinValue;

    private ShareRow? _row;

    private MenuItem _menuConnect = new();
    private MenuItem _menuPause = new();
    private MenuItem _menuOpen = new();
    private MenuItem _menuSettings = new();
    private MenuItem _menuUnbind = new();

    public MainWindow()
    {
        InitializeComponent();

        // Meldungen kommen aus dem Threadpool; Bindungen wollen den
        // Oberflaechen-Thread.
        TransferInfo.UiContext = SynchronizationContext.Current;

        TransferList.ItemsSource = _transfers;
        ShareGrid.ItemsSource = _rows;

        _configPath = FindConfig() ?? Path.GetFullPath("synct.json");

        Load();
        BuildColumnMenu();
        BuildRowMenu();
        RestoreColumns();

        // Der Zustand muss stehen, bevor die Anwendung Show() ruft -- danach
        // waere es ein Fenster, das sich vor den Augen des Benutzers wieder
        // wegduckt.
        if (_config.StartMinimized) WindowState = WindowState.Minimized;
        ApplyTray();

        _meter = new ThroughputMeter(CollectWire);
        _refresh.Tick += (_, _) => Tick();
        _refresh.Start();
    }

    private static string? FindConfig()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "synct.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private string HomeDirectory
        => Path.Combine(Path.GetDirectoryName(_configPath)!, _config.HomeDirectory);

    /// <summary>
    /// Was die Hosts zu sehen bekommen: dieselbe Konfiguration, aber mit
    /// ausgeschriebenem Datenverzeichnis.
    /// </summary>
    /// <remarks>
    /// Eine einzige Instanz, absichtlich. An ihr haengt das Cache-Budget, und
    /// das gilt fuer alle Freigaben zusammen -- je Gegenstelle ein eigenes
    /// waere kein programmweites mehr.
    /// </remarks>
    private AppConfig _runtime = new();

    // ------------------------------------------------------------ Laden

    private void Load()
    {
        // Die Oberflaeche richtet sich selbst ein. Wer sie oeffnet, soll seine
        // Device-ID sehen und weitergeben koennen -- ein Befehl auf der
        // Konsole waere ein Umweg durch ein Werkzeug, das er gerade nicht
        // benutzt.
        var firstRun = !File.Exists(_configPath);

        try
        {
            _config = firstRun ? new AppConfig() : AppConfig.Load(_configPath);
            _identity = DeviceIdentity.LoadOrCreate(HomeDirectory);
            if (firstRun) _config.Save(_configPath);
        }
        catch (Exception ex)
        {
            Status(App.S("M.ConfigUnreadable", ex.Message));
            return;
        }

        _runtime = new AppConfig
        {
            HomeDirectory = HomeDirectory,
            SharesRoot = _config.SharesRoot,
            CacheMaxBytes = _config.CacheMaxBytes,
            MinimumFreeBytes = _config.MinimumFreeBytes,
            Discovery = _config.Discovery,
            Relays = _config.Relays,
            DiscoveryServers = _config.DiscoveryServers,
            Peers = _config.Peers,
            Shares = _config.Shares,
            Parallelism = _config.Parallelism
        };

        // Aussehen und Sprache stehen in derselben Datei wie alles andere --
        // also gelten sie ab hier, nicht erst beim naechsten Start.
        App.ApplyTheme(_config.Theme);
        App.ApplyLanguage(_config.Language);

        _peers.Clear();
        foreach (var peerConfig in _config.Peers)
        {
            var host = new PeerHost(peerConfig, _runtime, _identity!, AppendLog);
            host.StateChanged += _ => Dispatcher.Invoke(RefreshRows);
            host.OfferedChanged += () => Dispatcher.Invoke(RebuildRows);
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
        share.StateChanged += _ => Dispatcher.Invoke(RefreshRows);
        share.SyncProgressChanged += () => Dispatcher.Invoke(RefreshRows);
        share.TransferStarted += t => Dispatcher.Invoke(() => AddTransfer(t));
        share.TransferFinished += _ => Dispatcher.Invoke(TrimTransfers);
        share.CacheChanged += () => Dispatcher.Invoke(RefreshRows);

        // Die Zeile fuer diesen Ordner kennt ihn noch nicht -- er wurde
        // gerade erst uebernommen oder verbunden.
        RebuildRows();
    });

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Minimiert starten und ein Symbol im Infobereich heisst zusammen: gar
        // kein Fenster. Verstecken laesst es sich erst hier -- Show() kommt von
        // der Anwendung, nicht von uns.
        if (_config.StartMinimized && _tray is not null) Hide();

        // Die Oberflaeche ist der Sync-Dienst: wer sie oeffnet, will in aller
        // Regel, dass es laeuft.
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
        var selected = (_row?.Peer.Config.DeviceId, _row?.FolderId);
        _rows.Clear();

        foreach (var peer in _peers)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var offer in peer.Host.Offered)
            {
                seen.Add(offer.FolderId);
                _rows.Add(new ShareRow(peer, offer.FolderId, offer.Label, peer.Host.ShareFor(offer.FolderId)));
            }

            // Konfigurierte Ordner, die die Gegenstelle (noch) nicht nennt.
            foreach (var share in _config.SharesOf(peer.Config).Where(s => !seen.Contains(s.FolderId)))
                _rows.Add(new ShareRow(peer, share.FolderId, share.Label, peer.Host.ShareFor(share.FolderId)));
        }

        ShareGrid.SelectedItem = _rows.FirstOrDefault(
            r => r.Peer.Config.DeviceId == selected.Item1 && r.FolderId == selected.Item2)
            ?? _rows.FirstOrDefault();

        RefreshRows();
    }

    private void RefreshRows()
    {
        foreach (var peer in _peers) peer.Refresh();
        foreach (var row in _rows) row.Refresh();
        UpdateButtons();
        UpdateCache();
        UpdateThumbnails();
    }

    private void OnShareSelected(object sender, SelectionChangedEventArgs e)
    {
        _row = ShareGrid.SelectedItem as ShareRow;
        UpdateButtons();
        UpdateCache();
    }

    // ------------------------------------------------------------ Takt

    private void Tick()
    {
        RefreshRows();
        UpdateThroughput();
    }

    private (long Read, long Written) CollectWire()
    {
        long read = 0, written = 0;
        foreach (var peer in _peers)
        {
            var (r, w) = peer.Host.Wire;
            read += r;
            written += w;
        }
        return (read, written);
    }

    private void UpdateThroughput()
    {
        if (_meter is null) return;

        var window = Range30.IsChecked == true ? TimeSpan.FromMinutes(30)
            : Range180.IsChecked == true ? TimeSpan.FromHours(3)
            : TimeSpan.FromMinutes(5);

        var series = _meter.Series(window, Buckets);
        Chart.Show(series);

        // Die aktuelle Rate ist die juengste Saeule der kurzen Spanne -- bei
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
    /// Der Cache ist einer -- die Anzeige zeigt darum die Summe, nicht die
    /// gerade ausgewaehlte Zeile.
    /// </summary>
    private void UpdateCache()
    {
        var used = _rows.Select(r => r.Share).OfType<ShareHost>().Sum(s => s.CacheUsedBytes);
        var max = _config.CacheMaxBytes;

        if (max > 0)
        {
            CacheBar.Value = Math.Min(100, 100.0 * used / max);
            CacheText.Text = App.S("M.CacheOf", Format.Bytes(used), Format.Bytes(max));
        }
        else
        {
            CacheBar.Value = 0;
            CacheText.Text = App.S("M.CacheNoBudget", Format.Bytes(used));
        }

        UpdateFreeSpace();
    }

    /// <summary>
    /// Der freie Platz auf dem Laufwerk des Caches -- die zweite Grenze, und
    /// die einzige, die auch von aussen bewegt wird.
    /// </summary>
    private void UpdateFreeSpace()
    {
        var root = _config.SharesRootOrDefault;
        var free = CacheBudget.FreeBytesOn(root);

        if (free < 0)
        {
            FreeText.Text = App.S("M.FreeUnknown");
            FreeText.ToolTip = root;
            return;
        }

        FreeText.Text = App.S("M.Free", Format.Bytes(free));
        FreeText.ToolTip = _config.MinimumFreeBytes > 0
            ? App.S("M.FreeShould", root, Format.Bytes(_config.MinimumFreeBytes))
            : root;
    }

    private void UpdateThumbnails()
    {
        if (DateTime.UtcNow - _thumbsRead < TimeSpan.FromSeconds(5)) return;
        _thumbsRead = DateTime.UtcNow;

        var (count, bytes) = _rows.Select(r => r.Share).OfType<ShareHost>()
            .Select(s => s.ThumbnailUsage())
            .Aggregate((Count: 0, Bytes: 0L), (a, b) => (a.Count + b.Count, a.Bytes + b.Bytes));

        ThumbText.Text = $"{Format.Count(count)} / {Format.Bytes(bytes)}";
        ThumbText.ToolTip = _config.ThumbnailDirectory;
    }

    private void OnRangeChanged(object sender, RoutedEventArgs e) => UpdateThroughput();

    // ------------------------------------------------------------ Spalten

    /// <summary>
    /// Rechtsklick auf die Kopfzeile blendet Spalten ein und aus.
    /// </summary>
    /// <remarks>
    /// Name und Status bleiben immer stehen -- eine Tabelle ohne Bezeichnung
    /// und ohne Zustand waere nur noch eine Zahlenwand.
    /// </remarks>
    private void BuildColumnMenu()
    {
        var menu = new ContextMenu();

        foreach (var column in ShareGrid.Columns.Skip(2))
        {
            var item = new MenuItem
            {
                Header = column.Header,
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
        }

        // An die Kopfzeile, nicht an das ganze Gitter: ein Rechtsklick auf
        // eine Zeile meint die Zeile, nicht die Spalten.
        var basis = TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var kopfzeile = basis is null
            ? new Style(typeof(DataGridColumnHeader))
            : new Style(typeof(DataGridColumnHeader), basis);

        kopfzeile.Setters.Add(new Setter(ContextMenuProperty, menu));
        ShareGrid.ColumnHeaderStyle = kopfzeile;
    }

    /// <summary>
    /// Was sich mit einem Share anstellen lässt -- am Share selbst.
    /// </summary>
    /// <remarks>
    /// Eine Knopfleiste über der Tabelle sagte dasselbe, nur weiter weg: die
    /// Knöpfe galten für die ausgewählte Zeile, standen aber nicht bei ihr.
    /// </remarks>
    private void BuildRowMenu()
    {
        _menuConnect = Eintrag("S.Menu.Connect", OnAcceptFolder);
        _menuPause = Eintrag("S.Menu.Pause", OnPauseShare);
        _menuOpen = Eintrag("S.Menu.Open", OnOpenFolder);
        _menuSettings = Eintrag("S.Menu.Settings", OnShowSettings);
        _menuUnbind = Eintrag("S.Menu.Unbind", OnUnbind);

        var menu = new ContextMenu();
        menu.Items.Add(_menuConnect);
        menu.Items.Add(_menuPause);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuOpen);
        menu.Items.Add(_menuSettings);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuUnbind);

        ShareGrid.ContextMenu = menu;

        // Ohne Zeile gibt es nichts zu tun -- dann bleibt es zu.
        ShareGrid.ContextMenuOpening += (_, e) => { if (_row is null) e.Handled = true; };
    }

    private static MenuItem Eintrag(string schluessel, RoutedEventHandler klick)
    {
        var item = new MenuItem();

        // Als Verweis, nicht als Text: dann folgt der Eintrag der Sprache.
        item.SetResourceReference(HeaderedItemsControl.HeaderProperty, schluessel);
        item.Click += klick;
        return item;
    }

    private void OnGridRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Der Rechtsklick soll die Zeile treffen, auf die er zeigt -- sonst
        // gälte das Menü für die vorher ausgewählte.
        var quelle = e.OriginalSource as DependencyObject;
        while (quelle is not null and not DataGridRow)
            quelle = System.Windows.Media.VisualTreeHelper.GetParent(quelle);

        if (quelle is DataGridRow zeile) zeile.IsSelected = true;
    }

    private string ColumnFile => Path.Combine(HomeDirectory, "gui-spalten.txt");

    private void SaveColumns()
    {
        try
        {
            Directory.CreateDirectory(HomeDirectory);
            File.WriteAllLines(ColumnFile, ShareGrid.Columns
                .Where(c => c.Visibility != Visibility.Visible)
                .Select(c => c.Header?.ToString() ?? ""));
        }
        catch (IOException) { /* dann eben beim naechsten Mal */ }
    }

    private void RestoreColumns()
    {
        if (!File.Exists(ColumnFile)) return;

        try
        {
            var hidden = new HashSet<string>(File.ReadAllLines(ColumnFile), StringComparer.Ordinal);

            foreach (var column in ShareGrid.Columns.Skip(2))
                column.Visibility = hidden.Contains(column.Header?.ToString() ?? "")
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            if (ShareGrid.ContextMenu is not null)
                foreach (var item in ShareGrid.ContextMenu.Items.OfType<MenuItem>())
                    item.IsChecked = !hidden.Contains(item.Header?.ToString() ?? "");
        }
        catch (IOException) { /* Standardspalten bleiben */ }
    }

    // ------------------------------------------------------------ Einklappen

    /// <summary>Wie hoch der Bereich stand, bevor er eingeklappt wurde.</summary>
    private GridLength _transferHeight = new(170);

    private void OnTransfersToggled(object sender, RoutedEventArgs e)
    {
        // Die Zeile muss mitgehen, sonst bliebe ein leerer Streifen stehen --
        // und der Ziehgriff haette nichts mehr zu ziehen.
        if (TransferRow is null || Splitter is null) return;

        if (TransferPanel.IsExpanded)
        {
            TransferRow.Height = _transferHeight;
            Splitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (TransferRow.Height.IsAbsolute) _transferHeight = TransferRow.Height;
            TransferRow.Height = GridLength.Auto;
            Splitter.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Was der Cache gerade hält.
    /// </summary>
    /// <remarks>
    /// Die Buchführung liegt in den laufenden Freigaben -- steht keine, ist
    /// die Zahl nicht bekannt, und dann sagt sie das lieber als eine Null.
    /// Die Vorschaubilder sind unabhaengig davon zu zaehlen: sie liegen als
    /// Dateien da.
    /// </remarks>
    private string CacheUsage()
    {
        var shares = _rows.Select(r => r.Share).OfType<ShareHost>().ToList();
        var thumbs = new ThumbnailStore(_config.ThumbnailDirectory).Usage();
        var preview = App.S("M.Previews", Format.Count(thumbs.Count), Format.Bytes(thumbs.Bytes));

        if (shares.Count == 0)
            return App.S("M.UsedUnknown", preview);

        var files = shares.Sum(s => s.CacheFileCount);
        var bytes = shares.Sum(s => s.CacheUsedBytes);

        return App.S("M.Used", Format.Bytes(bytes), Format.Count(files), preview);
    }

    /// <summary>
    /// Leert den Cache und meldet, was er noch hielt.
    /// </summary>
    /// <remarks>
    /// Die Vorschaubilder gehoeren dazu: sie liegen im selben Verzeichnis,
    /// sind aus fremden Dateien entstanden und entstehen jederzeit neu.
    /// </remarks>
    private async Task<string> ClearCacheAsync()
    {
        var (files, bytes) = await _runtime.Cache.ClearAsync();
        var thumbs = new ThumbnailStore(_config.ThumbnailDirectory).Clear();

        // Sonst zeigt die Leiste noch fuenf Sekunden lang die alten Zahlen.
        _thumbsRead = DateTime.MinValue;
        RefreshRows();

        if (files == 0 && thumbs.Count == 0)
            return _rows.Any(r => r.Share is not null)
                ? App.S("M.NothingLocal")
                : App.S("M.NothingRunning");

        return App.S("M.Cleared",
            Format.Count(files), Format.Bytes(bytes),
            Format.Count(thumbs.Count), Format.Bytes(thumbs.Bytes));
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
    /// Rufen ohne zu lauschen waere eine Einladung an eine Tuer, die es nicht
    /// gibt -- deshalb haengen Erkennung und Anmeldung am Lauscher.
    /// </remarks>
    private async void RestartNetwork()
    {
        await StopNetworkAsync();

        if (_identity is null || !_config.Listen) return;

        var listener = new BepListener(_identity, "SyncTClient", AppendLog);
        listener.Incoming += OnIncoming;

        if (!listener.Start(_config.ListenPort))
        {
            await listener.DisposeAsync();
            return;
        }

        _listener = listener;
        AppendLog($"Nehme Anrufe auf Port {listener.Port} entgegen.");

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
        // Erst freigeben, dann neu belegen -- sonst sind die Ports besetzt,
        // und zwar von uns selbst.
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
    /// Ein Geraet hat angerufen. Bekannte kommen durch, alle anderen werden
    /// vorgestellt -- in die Liste schleicht sich niemand.
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
                await connection.DisposeAsync();
                Status(App.S("M.Rejected", name));
                return;
            }

            _config.Peers.Add(new PeerConfig { Name = name, Address = address, DeviceId = id });
            Persist();
            Load();

            peer = _peers.FirstOrDefault(p => p.Config.DeviceId == id);
        }

        if (peer is null || peer.Host.State is PeerState.Verbunden or PeerState.Verbindet)
        {
            // Eine zweite Leitung zur selben Gegenstelle waere eine zu viel.
            await connection.DisposeAsync();
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
    /// Besitzer sein -- der Anruf kann kommen, bevor es steht.
    /// </summary>
    private MessageBoxResult Ask(string text, string caption)
        => IsLoaded
            ? MessageBox.Show(this, text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question);

    /// <summary>
    /// Unter welcher Adresse wir die Gegenstelle spaeter selbst erreichen.
    /// </summary>
    /// <remarks>
    /// Ihr Quellport ist fluechtig und waere zum Zurueckrufen wertlos; gemeint
    /// ist der Port, auf dem sie ihrerseits lauscht -- bei Syncthing 22000.
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
        var dialog = new PeerDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _config.Peers.Add(dialog.Result);
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

        var dialog = new PeerDialog(item.Config) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // Die Freigaben zeigen auf die alte ID. Ohne das Nachziehen haengen
        // sie an einer Gegenstelle, die es nicht mehr gibt.
        if (!string.Equals(before, item.Config.DeviceId, StringComparison.Ordinal))
            foreach (var share in _config.Shares.Where(s => s.PeerDeviceId == before))
                share.PeerDeviceId = item.Config.DeviceId;

        Persist();
        Load();
        Status(App.S("M.PeerChanged", item.Config.Display));
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
        // Der Klick kommt aus einer Zelle; deren Datensatz ist gemeint, nicht
        // zwangslaeufig die ausgewaehlte Zeile.
        if ((sender as Hyperlink)?.DataContext is not ShareRow row) return;

        new SharePeersWindow(row) { Owner = this }.ShowDialog();
    }

    // ------------------------------------------------------------ Freigaben

    /// <summary>
    /// Übernimmt einen angebotenen Ordner -- aber erst, nachdem gefragt wurde.
    /// </summary>
    /// <remarks>
    /// In zwei Schritten: erst den Index holen, damit im Dialog steht, was
    /// drin ist; im Explorer entsteht dabei nichts. Erst die Bestätigung legt
    /// den Ordner an. Wer abbricht, hinterlässt keine Spur.
    /// </remarks>
    private async void OnAcceptFolder(object sender, RoutedEventArgs e)
    {
        if (_row is null || _row.Accepted) return;

        var row = _row;
        var draft = new ShareConfig
        {
            FolderId = row.FolderId,
            PeerDeviceId = row.Peer.Config.DeviceId,
            Label = row.Label,
            LocalPath = Path.Combine(_config.SharesRootOrDefault, row.FolderId),
            Mode = ShareMode.OnDemand
        };

        Status(App.S("M.Asking", row.Name));

        ShareHost host;
        try
        {
            host = await row.Peer.Host.PrepareAsync(draft, _cts.Token);
        }
        catch (Exception ex)
        {
            Status(App.S("M.ContentUnavailable", row.Name, ex.Message));
            RebuildRows();
            return;
        }

        var dialog = new AcceptShareWindow(draft, HomeDirectory, row.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            await row.Peer.Host.DiscardAsync(host);
            Status(App.S("M.NotConnected", row.Name));
            RebuildRows();
            return;
        }

        _config.Shares.Add(draft);
        Persist();

        Status(App.S("M.Connecting", row.Name, draft.LocalPath));
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
        if (_row is null || !_row.Accepted) return;

        var share = _config.Shares.FirstOrDefault(s => s.FolderId == _row.FolderId);
        var path = share?.LocalPath ?? "";

        if (MessageBox.Show(
                App.S("M.UnbindBody", _row.Name, path),
                App.S("S.Menu.Unbind"), MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;

        await _row.Peer.Host.UnbindAsync(_row.FolderId);
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

    private void OnShowSettings(object sender, RoutedEventArgs e)
    {
        var share = _row?.Share?.Config;
        if (share is null) { Status(App.S("M.NoShareSelected")); return; }

        var dialog = new ShareSettingsWindow(share, HomeDirectory, _row!.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        Persist();
        var scope = share.Included.Count == 0
            ? App.S("M.ScopeAll")
            : App.S("M.ScopeBranches", share.Included.Count);

        Status(App.S("M.SavedScope", scope));
        RefreshRows();
    }

    /// <summary>
    /// Der Doppelklick tut das Naheliegende: was noch nicht verbunden ist,
    /// wird verbunden; was verbunden ist, wird geöffnet.
    /// </summary>
    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_row is null) return;

        if (_row.Accepted) OnOpenFolder(sender, e);
        else OnAcceptFolder(sender, e);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = _row?.Share?.Config.LocalPath;
        if (string.IsNullOrEmpty(path)) return;

        if (!Directory.Exists(path))
        {
            Status(App.S("M.NoPath", path));
            return;
        }

        try
        {
            // Explorer ausdruecklich: ein anderer Dateimanager als Standard
            // wuerde die Platzhalter mit eigener Dekodierung anfassen.
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
    /// Was der Geraetedialog an Fenster-Einstellungen aendert, gilt sofort:
    /// geschrieben und angewandt, ohne Umweg ueber einen Neustart.
    /// </summary>
    private void SaveWindowBehaviour()
    {
        Persist();
        ApplyTray();
    }

    /// <summary>
    /// Das Symbol im Infobereich gibt es nur, wenn das X das Fenster verstecken
    /// soll. Ohne Symbol waere ein verstecktes Fenster nicht mehr zu holen --
    /// beides ist dieselbe Entscheidung.
    /// </summary>
    private void ApplyTray()
    {
        if (_config.CloseToTray)
        {
            _tray ??= new TrayIcon(this, Quit);
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
    /// Die Einstellungen des Programms. Sie haengen an keiner Freigabe --
    /// sonst kaeme niemand an sie heran, der noch keine hat.
    /// </summary>
    private void OnShowProgramSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new ProgramSettingsWindow(
            _config, Path.GetDirectoryName(_configPath)!, CacheUsage, ClearCacheAsync)
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

    private void AddTransfer(TransferInfo transfer)
    {
        _transfers.Insert(0, transfer);
        TrimTransfers();
    }

    private void TrimTransfers()
    {
        // Laufende bleiben, von den abgeschlossenen nur die letzten paar.
        foreach (var stale in _transfers
                     .Where(t => t.State is TransferState.Fertig or TransferState.Fehler)
                     .Skip(KeepFinished).ToList())
        {
            _transfers.Remove(stale);
        }

        var running = _transfers.Count(t => t.State == TransferState.Laeuft);
        var waiting = _transfers.Count(t => t.State == TransferState.Wartet);
        QueueText.Text = running + waiting == 0
            ? "nichts unterwegs"
            : $"{running} aktiv, {waiting} in der Warteschlange";
    }

    private void UpdateButtons()
    {
        var connected = _row?.Peer.Host.State == PeerState.Verbunden;

        _menuConnect.IsEnabled = connected && _row is { Accepted: false };
        _menuUnbind.IsEnabled = _row is { Accepted: true };
        _menuSettings.IsEnabled = _row is { Accepted: true };
        _menuOpen.IsEnabled = _row is { Accepted: true };

        var state = _row?.Share?.State;
        _menuPause.IsEnabled = state is ShareState.Bereit or ShareState.Pausiert;
        _menuPause.SetResourceReference(HeaderedItemsControl.HeaderProperty,
            state == ShareState.Pausiert ? "S.Menu.Resume" : "S.Menu.Pause");
    }

    private void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    });

    private void Persist()
    {
        try { _config.Save(_configPath); }
        catch (Exception ex) { Status(App.S("M.SaveFailed", ex.Message)); }
    }

    private void Status(string message) => StatusBar.Text = message;

    // ------------------------------------------------------------ Ende

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Das X beendet nicht, es versteckt. Beendet wird ueber das
        // Kontextmenue des Symbols -- und das kommt hier ueber Quit() an.
        if (_tray is not null && !_exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tray?.Dispose();

        _refresh.Stop();
        _meter?.Dispose();

        await StopNetworkAsync();
        await _cts.CancelAsync();

        // Ohne sauberes Abmelden bleiben Platzhalter zurueck, die niemand
        // mehr bedienen kann.
        foreach (var peer in _peers)
        {
            try { await peer.Host.DisposeAsync(); }
            catch { /* beim Beenden belanglos */ }
        }
    }
}
