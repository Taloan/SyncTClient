using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using SyncTClient.Bep;
using SyncTClient.Mount;

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
    private double _peak;

    private ShareRow? _row;

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
        RestoreColumns();

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

    private AppConfig RuntimeConfig => new()
    {
        HomeDirectory = HomeDirectory,
        Peers = _config.Peers,
        Shares = _config.Shares,
        Parallelism = _config.Parallelism
    };

    // ------------------------------------------------------------ Laden

    private void Load()
    {
        if (!File.Exists(_configPath))
        {
            Status("Keine Konfiguration gefunden. Erst \"synctmount --init\" ausführen.");
            return;
        }

        try
        {
            _config = AppConfig.Load(_configPath);
            _identity = DeviceIdentity.LoadOrCreate(HomeDirectory);
        }
        catch (Exception ex)
        {
            Status($"Konfiguration nicht lesbar: {ex.Message}");
            return;
        }

        _peers.Clear();
        foreach (var peerConfig in _config.Peers)
        {
            var host = new PeerHost(peerConfig, RuntimeConfig, _identity!, AppendLog);
            host.StateChanged += _ => Dispatcher.Invoke(RefreshRows);
            host.OfferedChanged += () => Dispatcher.Invoke(RebuildRows);
            host.ShareAdded += WireShare;
            _peers.Add(new PeerItem(host));
        }

        RebuildRows();
        Status($"{_peers.Count} Gegenstelle(n), {_config.Shares.Count} Ordner konfiguriert.");
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

    private void UpdateCache()
    {
        // Ohne Auswahl die Summe ueber alles -- so steht dort nie ein
        // nichtssagender Strich.
        var shares = _row?.Share is { } single
            ? [single]
            : _rows.Select(r => r.Share).OfType<ShareHost>().ToList();

        if (shares.Count == 0)
        {
            CacheText.Text = "—";
            CacheBar.Value = 0;
            return;
        }

        var used = shares.Sum(s => s.CacheUsedBytes);
        var max = shares.Sum(s => s.CacheMaxBytes);
        var scope = _row?.Share is not null ? _row.Name : $"{shares.Count} Freigaben";

        if (max > 0)
        {
            CacheBar.Value = Math.Min(100, 100.0 * used / max);
            CacheText.Text = $"{Format.Bytes(used)} von {Format.Bytes(max)} · {scope}";
        }
        else
        {
            CacheBar.Value = 0;
            CacheText.Text = $"{Format.Bytes(used)} (kein Budget) · {scope}";
        }
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

        ShareGrid.ContextMenu = menu;
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

    // ------------------------------------------------------------ Gegenstellen

    private async Task ConnectAsync(PeerItem item)
    {
        var shares = _config.SharesOf(item.Config).Where(s => s.AutoStart);
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
        var dialog = new PeerManagerWindow(_peers, AddPeer, TogglePeer, RemovePeerAsync) { Owner = this };
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
        Status($"Gegenstelle {dialog.Result.Display} hinzugefügt.");
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
            ? $"Gegenstelle „{item.Display}“ entfernen?"
            : $"Gegenstelle „{item.Display}“ mit {shares.Count} Ordner(n) entfernen?\n\n" +
              "Die Bindungen werden gelöst. Bereits heruntergeladene Dateien bleiben liegen.";

        if (MessageBox.Show(question, "Entfernen", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
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

    private async void OnAcceptFolder(object sender, RoutedEventArgs e)
    {
        if (_row is null || _row.Accepted) return;

        var localPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT", _row.FolderId);

        var share = new ShareConfig
        {
            FolderId = _row.FolderId,
            PeerDeviceId = _row.Peer.Config.DeviceId,
            Label = _row.Label,
            LocalPath = localPath,
            Mode = ShareMode.OnDemand,
            CacheMaxBytes = 2L * 1024 * 1024 * 1024
        };

        _config.Shares.Add(share);
        Persist();

        Status($"Übernehme {_row.Name} nach {localPath} ...");
        try
        {
            await _row.Peer.Host.AcceptAsync(share, _cts.Token);
            Status($"{_row.Name} übernommen.");
        }
        catch (Exception ex)
        {
            Status($"Übernehmen fehlgeschlagen: {ex.Message}");
        }

        RebuildRows();
    }

    private async void OnUnbind(object sender, RoutedEventArgs e)
    {
        if (_row is null || !_row.Accepted) return;

        var share = _config.Shares.FirstOrDefault(s => s.FolderId == _row.FolderId);
        var path = share?.LocalPath ?? "";

        if (MessageBox.Show(
                $"Bindung zu „{_row.Name}“ lösen?\n\n" +
                $"Die Platzhalter unter {path} werden abgemeldet, Index und Vorschaubilder verworfen.\n" +
                "Bereits heruntergeladene Dateien bleiben liegen.",
                "Bindung lösen", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
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
        if (share is null) { Status("Kein übernommener Ordner ausgewählt."); return; }

        var dialog = new ShareSettingsWindow(share, HomeDirectory, _row!.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        Persist();
        var scope = share.Included.Count == 0 ? "alles" : $"{share.Included.Count} Zweig(e)";
        Status($"Gespeichert — {scope} ausgewählt. Wirksam nach dem nächsten Verbinden.");
        RefreshRows();
    }

    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OnOpenFolder(sender, e);

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = _row?.Share?.Config.LocalPath;
        if (string.IsNullOrEmpty(path)) return;

        if (!Directory.Exists(path))
        {
            Status($"{path} gibt es (noch) nicht.");
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
            Status($"Öffnen fehlgeschlagen: {ex.Message}");
        }
    }

    private void OnShowDevice(object sender, RoutedEventArgs e)
    {
        var thumbs = _rows.Select(r => r.Share).OfType<ShareHost>()
            .Select(s => s.ThumbnailUsage())
            .Aggregate((Count: 0, Bytes: 0L), (a, b) => (a.Count + b.Count, a.Bytes + b.Bytes));

        new DeviceWindow(
            _identity?.Id.ToString() ?? "—",
            _configPath,
            $"{thumbs.Count:N0} Vorschaubilder, {Format.Bytes(thumbs.Bytes)} unter " +
            Path.Combine(HomeDirectory, "thumbs"))
        { Owner = this }.ShowDialog();
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

        AcceptButton.IsEnabled = connected && _row is { Accepted: false };
        UnbindButton.IsEnabled = _row is { Accepted: true };
        SettingsButton.IsEnabled = _row is { Accepted: true };
        OpenButton.IsEnabled = _row is { Accepted: true };

        var state = _row?.Share?.State;
        PauseButton.IsEnabled = state is ShareState.Bereit or ShareState.Pausiert;
        PauseButton.Content = state == ShareState.Pausiert ? "Fortsetzen" : "Anhalten";
    }

    private void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    });

    private void Persist()
    {
        try { _config.Save(_configPath); }
        catch (Exception ex) { Status($"Speichern fehlgeschlagen: {ex.Message}"); }
    }

    private void Status(string message) => StatusBar.Text = message;

    // ------------------------------------------------------------ Ende

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _refresh.Stop();
        _meter?.Dispose();
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
