using System.Collections.ObjectModel;
using System.Globalization;
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

    public MainWindow()
    {
        InitializeComponent();

        // Meldungen kommen aus dem Threadpool. Bindungen benoetigen den
        // Oberflaechen-Thread.
        TransferInfo.UiContext = SynchronizationContext.Current;

        TransferList.ItemsSource = _transfers;
        ShareGrid.ItemsSource = _rows;

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
        _refresh.Start();
    }

    private string HomeDirectory
        => Path.Combine(Path.GetDirectoryName(_configPath)!, _config.HomeDirectory);

    /// <summary>
    /// Was die Hosts zu sehen bekommen: dieselbe Konfiguration, aber mit
    /// ausgeschriebenem Datenverzeichnis.
    /// </summary>
    /// <remarks>
    /// Absichtlich eine einzige Instanz. An ihr haengt das Cache-Budget, und
    /// das gilt fuer alle Freigaben zusammen. Je Gegenstelle ein eigenes
    /// Budget waere kein programmweites Budget mehr.
    /// </remarks>
    private AppConfig _runtime = new();

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
            GenerateThumbnails = _config.GenerateThumbnails,
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

        // Aussehen und Sprache stehen in derselben Datei wie alles andere.
        // Sie gelten ab hier und nicht erst beim naechsten Start.
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
        share.LimitReached += hit => Dispatcher.Invoke(() => ShowLimit(hit));

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

        // Die Oberflaeche ist zugleich der Sync-Dienst. Wer sie oeffnet, will
        // in aller Regel, dass der Abgleich laeuft.
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
                _rows.Add(Wire(new ShareRow(peer, offer.FolderId, offer.Label, peer.Host.ShareFor(offer.FolderId))));
            }

            // Konfigurierte Ordner, die die Gegenstelle (noch) nicht nennt.
            foreach (var share in _config.SharesOf(peer.Config).Where(s => !seen.Contains(s.FolderId)))
                _rows.Add(Wire(new ShareRow(peer, share.FolderId, share.Label, peer.Host.ShareFor(share.FolderId))));
        }

        ShareGrid.SelectedItem = _rows.FirstOrDefault(
            r => r.Peer.Config.DeviceId == selected.Item1 && r.FolderId == selected.Item2)
            ?? _rows.FirstOrDefault();

        RefreshRows();
    }

    /// <summary>Haengt sich an die Umbenennung, damit sie gespeichert wird.</summary>
    private ShareRow Wire(ShareRow row)
    {
        row.Renamed += OnShareRenamed;
        return row;
    }

    private void RefreshRows()
    {
        ApplyPause();

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

    private static DataGridCell? FindCell(DependencyObject? source)
    {
        while (source is not null and not DataGridCell)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

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
    /// Angehalten heisst: keine Inhalte mehr holen. Die Platzhalter bleiben
    /// stehen, der Index laeuft weiter mit -- sonst waere nach dem Fortsetzen
    /// erst einmal ein voller Abgleich faellig.
    /// </remarks>
    private void OnTogglePauseAll(object? sender = null, RoutedEventArgs? e = null)
    {
        _config.Paused = !_config.Paused;
        Persist();

        ApplyPause();
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
        _tray?.Show(Zustand());
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

        if (uebernommen.Any(r => r.Busy) || _transfers.Any(t => t.State == TransferState.Laeuft))
            return TrayStatus.Synchronisiert;

        return TrayStatus.Erledigt;
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
    /// Es gibt nur einen Cache. Die Anzeige zeigt darum die Summe und nicht
    /// die gerade ausgewaehlte Zeile.
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
    /// Der freie Platz auf dem Laufwerk des Caches. Das ist die zweite Grenze
    /// und die einzige, die sich auch von aussen aendert.
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

        // Ohne Laufwerk ist die Zahl nicht einzuordnen.
        var drive = Path.GetPathRoot(Path.GetFullPath(root))?.TrimEnd(Path.DirectorySeparatorChar) ?? root;
        FreeText.Text = App.S("M.Free", Format.Bytes(free), drive);
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

        ThumbText.Text = App.S("M.PreviewShort", Format.Count(count), Format.Bytes(bytes));
        ThumbText.ToolTip = _config.ThumbnailDirectory;

        // Die Summe ueber alle Freigaben. Gezaehlt wird der Index, nicht die
        // Platte: er sagt, was zur Freigabe gehoert, unabhaengig davon, was
        // davon gerade lokal liegt.
        var shares = _rows.Select(r => r.Share).OfType<ShareHost>().ToList();
        OverallText.Text = App.S("M.OverallShort",
            Format.Count(shares.Sum(s => (long)s.IndexCount)),
            Format.Bytes(shares.Sum(s => s.IndexBytes)));
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

        foreach (var column in ShareGrid.Columns.Skip(2))
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

        var menu = new ContextMenu();
        menu.Items.Add(_menuConnect);
        menu.Items.Add(_menuPause);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuOpen);
        menu.Items.Add(_menuSettings);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuUnbind);

        ShareGrid.ContextMenu = menu;

        // Ohne ausgewaehlte Zeile gibt es nichts zu tun. Das Menue bleibt dann zu.
        ShareGrid.ContextMenuOpening += (_, e) => { if (_row is null) e.Handled = true; };
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
                    // Eine Datei aus einer aelteren Fassung. Die Vorgabe bleibt.
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

                // Name und Status bleiben immer sichtbar.
                if (nummer >= 2)
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
        var thumbs = new ThumbnailStore(_config.ThumbnailDirectory).Usage();
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
    /// Vorschaubilder liegen im selben Verzeichnis und entstehen bei Bedarf
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
        var thumbs = new ThumbnailStore(_config.ThumbnailDirectory).Clear();
        Refreshed();

        return Task.FromResult(thumbs.Count == 0
            ? App.S("M.NothingThumbs")
            : App.S("M.ClearedThumbs", Format.Count(thumbs.Count), Format.Bytes(thumbs.Bytes)));
    }

    private async Task<string> ClearAllAsync()
    {
        var (files, bytes) = await _runtime.Cache.ClearAsync();
        var thumbs = new ThumbnailStore(_config.ThumbnailDirectory).Clear();
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

        var thumbs = new ThumbnailStore(_config.ThumbnailDirectory).Clear();
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
            // Eine zweite Verbindung zur selben Gegenstelle wird nicht gebraucht.
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
    /// Besitzer sein. Der Anruf kann eintreffen, bevor das Fenster steht.
    /// </summary>
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
        // Der Klick kommt aus einer Zelle. Gemeint ist deren Datensatz, nicht
        // zwangslaeufig die ausgewaehlte Zeile.
        if ((sender as Hyperlink)?.DataContext is not ShareRow row) return;

        new SharePeersWindow(row) { Owner = this }.ShowDialog();
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
            _tray ??= new TrayIcon(this, Quit, () => OnTogglePauseAll());
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
    /// Die Einstellungen des Programms. Sie haengen an keiner Freigabe. Sonst
    /// waeren sie fuer jemanden, der noch keine Freigabe hat, nicht erreichbar.
    /// </summary>
    private void OnShowProgramSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new ProgramSettingsWindow(
            _config, Path.GetDirectoryName(_configPath)!,
            () => _runtime.Cache.Volumes,
            ReleaseVolumeAsync,
            () => new ThumbnailStore(_config.ThumbnailDirectory).Usage(),
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
        var text = hit.Budget
            ? App.S("M.LimitBudget", name, Format.Bytes(hit.Needed), Format.Bytes(hit.Limit))
            : App.S("M.LimitFree", name, Format.Bytes(hit.Needed), Format.Bytes(hit.Limit));

        Status(text.Split('\n')[0]);
        MessageBox.Show(this, text, App.S("M.LimitTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
    }
}
