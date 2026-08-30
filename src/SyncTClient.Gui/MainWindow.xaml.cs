using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

public partial class MainWindow : Window
{
    /// <summary>Wieviele abgeschlossene Übertragungen sichtbar bleiben.</summary>
    private const int KeepFinished = 25;

    private readonly string _configPath;
    private readonly ObservableCollection<TransferInfo> _transfers = [];
    private readonly ObservableCollection<PeerItem> _peers = [];
    private readonly ObservableCollection<FolderItem> _folders = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _cts = new();

    private AppConfig _config = new();
    private DeviceIdentity? _identity;

    private PeerItem? _peer;
    private FolderItem? _folder;
    private FolderNode? _tree;
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();

        // Meldungen kommen aus dem Threadpool; Bindungen wollen den
        // Oberflaechen-Thread.
        TransferInfo.UiContext = SynchronizationContext.Current;

        TransferList.ItemsSource = _transfers;
        PeerList.ItemsSource = _peers;
        FolderList.ItemsSource = _folders;

        _refresh.Tick += (_, _) => RefreshAll();
        _refresh.Start();

        _configPath = FindConfig() ?? Path.GetFullPath("synct.json");
        ConfigPathBox.Text = _configPath;

        Load();
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
            OwnIdBox.Text = _identity.Id.ToString();
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
            host.StateChanged += _ => Dispatcher.Invoke(RefreshAll);
            host.OfferedChanged += () => Dispatcher.Invoke(RebuildFolders);
            host.ShareAdded += WireShare;
            _peers.Add(new PeerItem(host));
        }

        if (_peers.Count > 0) PeerList.SelectedIndex = 0;
        Status($"{_peers.Count} Gegenstelle(n), {_config.Shares.Count} Ordner konfiguriert.");
    }

    private void WireShare(ShareHost share) => Dispatcher.Invoke(() =>
    {
        share.StateChanged += _ => Dispatcher.Invoke(RefreshAll);
        share.TransferStarted += t => Dispatcher.Invoke(() => AddTransfer(t));
        share.TransferFinished += _ => Dispatcher.Invoke(TrimTransfers);
        share.CacheChanged += () => Dispatcher.Invoke(RefreshStatus);
        share.ThumbnailProgress += (done, total) => Dispatcher.Invoke(() =>
            Status($"[{share.FolderId}] Vorschaubilder: {done} von {total}"));
    });

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Die Oberflaeche ist der Sync-Dienst: wer sie oeffnet, will in aller
        // Regel, dass es laeuft.
        foreach (var item in _peers.Where(p => p.Config.AutoConnect).ToList())
            await ConnectAsync(item);
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
            RefreshAll();
        }
    }

    private async void OnConnectPeer(object sender, RoutedEventArgs e)
    {
        if (_peer is null) return;

        if (_peer.Host.State == PeerState.Verbunden) await _peer.Host.DisconnectAsync();
        else await ConnectAsync(_peer);

        RefreshAll();
    }

    private void OnAddPeer(object sender, RoutedEventArgs e)
    {
        var dialog = new PeerDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _config.Peers.Add(dialog.Result);
        Persist();
        Load();
        Status($"Gegenstelle {dialog.Result.Display} hinzugefügt.");
    }

    private async void OnRemovePeer(object sender, RoutedEventArgs e)
    {
        if (_peer is null) return;

        var shares = _config.SharesOf(_peer.Config).ToList();
        var question = shares.Count == 0
            ? $"Gegenstelle „{_peer.Display}“ entfernen?"
            : $"Gegenstelle „{_peer.Display}“ mit {shares.Count} Ordner(n) entfernen?\n\n" +
              "Die Bindungen werden gelöst. Bereits heruntergeladene Dateien bleiben liegen.";

        if (MessageBox.Show(question, "Entfernen", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;

        foreach (var share in shares)
        {
            await _peer.Host.UnbindAsync(share.FolderId);
            _config.Shares.Remove(share);
        }

        await _peer.Host.DisposeAsync();
        _config.Peers.Remove(_peer.Config);
        Persist();
        Load();
    }

    private void OnPeerSelected(object sender, SelectionChangedEventArgs e)
    {
        _peer = PeerList.SelectedItem as PeerItem;
        RebuildFolders();
        RefreshAll();
    }

    // ------------------------------------------------------------ Ordner

    /// <summary>
    /// Vereint, was wir uebernommen haben, mit dem, was die Gegenstelle
    /// anbietet -- letzteres steht in ihrem ClusterConfig.
    /// </summary>
    private void RebuildFolders()
    {
        var selected = _folder?.FolderId;
        _folders.Clear();

        if (_peer is null) { FolderHeader.Text = "Ordner"; return; }
        FolderHeader.Text = $"Ordner von {_peer.Display}";

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var offer in _peer.Host.Offered)
        {
            seen.Add(offer.FolderId);
            _folders.Add(new FolderItem(offer.FolderId, offer.Label, _peer.Host.ShareFor(offer.FolderId)));
        }

        // Konfigurierte Ordner, die die Gegenstelle (noch) nicht nennt.
        foreach (var share in _config.SharesOf(_peer.Config).Where(s => !seen.Contains(s.FolderId)))
            _folders.Add(new FolderItem(share.FolderId, share.Label, _peer.Host.ShareFor(share.FolderId)));

        if (selected is not null)
            FolderList.SelectedItem = _folders.FirstOrDefault(f => f.FolderId == selected);
        if (FolderList.SelectedItem is null && _folders.Count > 0)
            FolderList.SelectedIndex = 0;
    }

    private void OnFolderSelected(object sender, SelectionChangedEventArgs e)
    {
        _folder = FolderList.SelectedItem as FolderItem;
        LoadSettings();
        RefreshAll();
    }

    /// <summary>Doppelklick oeffnet den Ordner im Explorer.</summary>
    private void OnFolderDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var path = _folder?.Share?.Config.LocalPath;
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

    private async void OnAcceptFolder(object sender, RoutedEventArgs e)
    {
        if (_peer is null || _folder is null || _folder.Accepted) return;

        var localPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT", _folder.FolderId);

        var share = new ShareConfig
        {
            FolderId = _folder.FolderId,
            PeerDeviceId = _peer.Config.DeviceId,
            Label = _folder.Label,
            LocalPath = localPath,
            Mode = ShareMode.OnDemand,
            CacheMaxBytes = 2L * 1024 * 1024 * 1024
        };

        _config.Shares.Add(share);
        Persist();

        Status($"Übernehme {_folder.Display} nach {localPath} ...");
        try
        {
            await _peer.Host.AcceptAsync(share, _cts.Token);
            Status($"{_folder.Display} übernommen.");
        }
        catch (Exception ex)
        {
            Status($"Übernehmen fehlgeschlagen: {ex.Message}");
        }

        RebuildFolders();
        RefreshAll();
    }

    private async void OnUnbind(object sender, RoutedEventArgs e)
    {
        if (_peer is null || _folder is null || !_folder.Accepted) return;

        var share = _config.Shares.FirstOrDefault(s => s.FolderId == _folder.FolderId);
        var path = share?.LocalPath ?? "";

        if (MessageBox.Show(
                $"Bindung zu „{_folder.Display}“ lösen?\n\n" +
                $"Die Platzhalter unter {path} werden abgemeldet, Index und Vorschaubilder verworfen.\n" +
                "Bereits heruntergeladene Dateien bleiben liegen.",
                "Bindung lösen", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;

        await _peer.Host.UnbindAsync(_folder.FolderId);
        if (share is not null) _config.Shares.Remove(share);
        Persist();

        RebuildFolders();
        RefreshAll();
    }

    private void OnPauseShare(object sender, RoutedEventArgs e)
    {
        var share = _folder?.Share;
        if (share is null) return;

        if (share.State == ShareState.Pausiert) share.Resume();
        else share.Pause();

        RefreshAll();
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

    private void RefreshAll()
    {
        foreach (var peer in _peers) peer.Refresh();
        foreach (var folder in _folders) folder.Refresh();
        RefreshStatus();
        UpdateButtons();
    }

    private void RefreshStatus()
    {
        var share = _folder?.Share;

        if (_folder is null)
        {
            ShareTitle.Text = "—";
            ShareSubtitle.Text = "";
            CacheText.Text = "—";
            CacheBar.Value = 0;
            StatIndex.Text = StatSize.Text = StatLocal.Text = StatThumbs.Text = "—";
            return;
        }

        ShareTitle.Text = _folder.Display;

        if (share is null)
        {
            ShareSubtitle.Text = "Angeboten, aber nicht übernommen.";
            CacheText.Text = "—";
            CacheBar.Value = 0;
            StatIndex.Text = StatSize.Text = StatLocal.Text = StatThumbs.Text = "—";
            return;
        }

        ShareSubtitle.Text = $"{_peer?.Display} · {share.Config.LocalPath} · {_folder.StateText}";

        var used = share.CacheUsedBytes;
        var max = share.CacheMaxBytes;

        if (max > 0)
        {
            CacheBar.Value = Math.Min(100, 100.0 * used / max);
            CacheText.Text = $"{used / (1024.0 * 1024.0):0.#} von {max / (1024.0 * 1024.0):0.#} MB";
        }
        else
        {
            CacheBar.Value = 0;
            CacheText.Text = $"{used / (1024.0 * 1024.0):0.#} MB (kein Budget)";
        }

        var (thumbCount, thumbBytes) = share.ThumbnailUsage();

        StatIndex.Text = share.IndexCount.ToString("N0");
        StatSize.Text = $"{share.IndexBytes / (1024.0 * 1024.0):N0} MB";
        StatLocal.Text = $"{share.CacheFileCount:N0}";
        StatThumbs.Text = $"{thumbCount:N0}";
        ThumbInfo.Text = $"{thumbCount:N0} Vorschaubilder, {thumbBytes / (1024.0 * 1024.0):0.#} MB " +
                         $"unter {Path.Combine(HomeDirectory, "thumbs")}";
    }

    private void UpdateButtons()
    {
        var connected = _peer?.Host.State == PeerState.Verbunden;
        ConnectButton.Content = connected ? "Trennen" : "Verbinden";
        ConnectButton.IsEnabled = _peer is not null;

        AcceptButton.IsEnabled = connected && _folder is { Accepted: false };
        UnbindButton.IsEnabled = _folder is { Accepted: true };

        var state = _folder?.Share?.State;
        PauseButton.IsEnabled = state is ShareState.Bereit or ShareState.Pausiert;
        PauseButton.Content = state == ShareState.Pausiert ? "Fortsetzen" : "Anhalten";

        DetailPanel.IsEnabled = _folder is { Accepted: true };
    }

    private void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    });

    private void OnCopyId(object sender, RoutedEventArgs e)
    {
        if (_identity is null) return;
        Clipboard.SetText(_identity.Id.ToString());
        Status("Device-ID kopiert.");
    }

    // ------------------------------------------------------------ Einstellungen

    private void LoadSettings()
    {
        var share = _folder?.Share;
        if (share is null) { FolderTree.ItemsSource = null; _tree = null; return; }

        var config = share.Config;
        _loading = true;
        LocalPathBox.Text = config.LocalPath;
        ModeBox.SelectedIndex = config.Mode == ShareMode.AlwaysLocal ? 1 : 0;
        CacheBudgetBox.Text = (config.CacheMaxBytes / (1024 * 1024)).ToString();
        ThumbsBox.IsChecked = config.GenerateThumbnails;
        AutoStartBox.IsChecked = config.AutoStart;
        UpdateCacheEnabled();
        _loading = false;

        LoadTree(config);
    }

    private void LoadTree(ShareConfig share)
    {
        FolderTree.ItemsSource = null;
        _tree = null;

        var databasePath = Path.Combine(HomeDirectory, $"index-{share.FolderId}.db");
        if (!File.Exists(databasePath))
        {
            TreeStatus.Text = "Noch kein Index — den Ordner einmal starten.";
            return;
        }

        try
        {
            // WAL-Modus erlaubt das Mitlesen, auch waehrend der Ordner laeuft.
            using var index = new PersistentFolderIndex(databasePath, share.FolderId);
            var entries = index.EnumerateLight().Select(e => (e.Name, e.Size, e.IsDirectory)).ToList();

            _tree = FolderNode.Build(entries);
            ApplySelection(_tree, share);
            _tree.RecomputeUpwards();

            FolderTree.ItemsSource = new[] { _tree };
            TreeStatus.Text = $"{_tree.FileCount} Dateien, {_tree.TotalBytes / (1024.0 * 1024.0):0.#} MB insgesamt";
        }
        catch (Exception ex)
        {
            TreeStatus.Text = $"Index nicht lesbar: {ex.Message}";
        }
    }

    /// <summary>Uebertraegt die gespeicherte Auswahl auf den frisch gebauten Baum.</summary>
    private static void ApplySelection(FolderNode root, ShareConfig share)
    {
        var everything = share.Included.Count == 0;
        Apply(root);

        void Apply(FolderNode node)
        {
            node.InitializeChecked(everything || share.Includes(node.Path));
            foreach (var child in node.Children) Apply(child);
        }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateCacheEnabled();
    }

    private void UpdateCacheEnabled()
    {
        // Bei "vollstaendig lokal" gibt es nichts zu verdraengen.
        var onDemand = ModeBox.SelectedIndex == 0;
        CachePanel.IsEnabled = onDemand;
        CacheHint.Text = onDemand
            ? "Ist das Budget überschritten, wird freigegeben, was am längsten nicht geöffnet wurde. Angeheftete Dateien bleiben."
            : "In diesem Modus wird alles lokal vorgehalten; ein Budget gilt nicht.";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (_tree is not null) _tree.IsChecked = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        if (_tree is not null) _tree.IsChecked = false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var share = _folder?.Share?.Config;
        if (share is null) { Status("Kein übernommener Ordner ausgewählt."); return; }

        if (!long.TryParse(CacheBudgetBox.Text.Trim(), out var megabytes) || megabytes < 0)
        {
            Status("Das Cache-Budget muss eine Zahl in MB sein (0 = unbegrenzt).");
            return;
        }

        share.LocalPath = LocalPathBox.Text.Trim();
        share.Mode = ModeBox.SelectedIndex == 1 ? ShareMode.AlwaysLocal : ShareMode.OnDemand;
        share.CacheMaxBytes = megabytes * 1024 * 1024;
        share.GenerateThumbnails = ThumbsBox.IsChecked == true;
        share.AutoStart = AutoStartBox.IsChecked == true;

        if (_tree is not null)
        {
            // Der Baum zeigt nur Verzeichnisse. Eintraege, die auf einzelne
            // Dateien zeigen, kann er nicht darstellen -- die wuerden beim
            // Speichern stillschweigend verschwinden. Also behalten wir sie,
            // solange nicht ohnehin alles ausgewaehlt ist.
            var selection = FolderNode.CollectIncluded(_tree);
            if (selection.Count > 0)
            {
                var known = new HashSet<string>(AllPaths(_tree), StringComparer.OrdinalIgnoreCase);
                selection.AddRange(share.Included.Where(p => !known.Contains(p)));
            }
            share.Included = selection;
        }

        Persist();
        var scope = share.Included.Count == 0 ? "alles" : $"{share.Included.Count} Zweig(e)";
        Status($"Gespeichert — {scope} ausgewählt. Wirksam nach dem nächsten Verbinden.");
    }

    private static IEnumerable<string> AllPaths(FolderNode node)
    {
        yield return node.Path;
        foreach (var child in node.Children)
            foreach (var path in AllPaths(child))
                yield return path;
    }

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
