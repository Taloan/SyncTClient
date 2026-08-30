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
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };

    private AppConfig _config = new();
    private DeviceIdentity? _identity;
    private readonly Dictionary<string, ShareHost> _hosts = new(StringComparer.Ordinal);

    private ShareConfig? _current;
    private FolderNode? _tree;
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();

        // Meldungen kommen aus dem Threadpool; Bindungen wollen den
        // Oberflaechen-Thread.
        TransferInfo.UiContext = SynchronizationContext.Current;

        TransferList.ItemsSource = _transfers;
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();

        _configPath = FindConfig() ?? Path.GetFullPath("synct.json");
        ConfigPathBox.Text = _configPath;

        Load();
        UpdateButtons();
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

    private ShareHost? CurrentHost
        => _current is not null && _hosts.TryGetValue(_current.FolderId, out var host) ? host : null;

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
            _identity = DeviceIdentity.LoadOrCreate(
                Path.Combine(Path.GetDirectoryName(_configPath)!, _config.HomeDirectory));
        }
        catch (Exception ex)
        {
            Status($"Konfiguration nicht lesbar: {ex.Message}");
            return;
        }

        ShareBox.ItemsSource = _config.Shares;
        if (_config.Shares.Count > 0) ShareBox.SelectedIndex = 0;
        Status($"Bereit. Eigene Device-ID: {_identity!.Id}");
    }

    private void OnShareSelected(object sender, SelectionChangedEventArgs e)
    {
        _current = ShareBox.SelectedItem as ShareConfig;
        DetailPanel.IsEnabled = _current is not null;
        if (_current is null) return;

        _loading = true;
        LocalPathBox.Text = _current.LocalPath;
        ModeBox.SelectedIndex = _current.Mode == ShareMode.AlwaysLocal ? 1 : 0;
        CacheBudgetBox.Text = (_current.CacheMaxBytes / (1024 * 1024)).ToString();
        UpdateCacheEnabled();
        _loading = false;

        LoadTree(_current);
        RefreshStatus();
        UpdateButtons();
    }

    // ------------------------------------------------------------ Steuerung

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Die Oberflaeche ist der Sync-Dienst: wer sie oeffnet, will in aller
        // Regel, dass es laeuft. Beendet wird beim Schliessen des Fensters.
        foreach (var share in _config.Shares.Where(s => s.AutoStart).ToList())
            await StartShareAsync(share);
    }

    private ShareHost EnsureHost(ShareConfig share)
    {
        if (_hosts.TryGetValue(share.FolderId, out var existing)) return existing;

        var home = Path.Combine(Path.GetDirectoryName(_configPath)!, _config.HomeDirectory);
        var app = new AppConfig
        {
            HomeDirectory = home,
            Peer = _config.Peer,
            Parallelism = _config.Parallelism,
            Shares = _config.Shares
        };

        var host = new ShareHost(share, app, _identity!, AppendLog);
        host.StateChanged += _ => Dispatcher.Invoke(() => { UpdateButtons(); RefreshStatus(); });
        host.TransferStarted += t => Dispatcher.Invoke(() => AddTransfer(t));
        host.TransferFinished += _ => Dispatcher.Invoke(TrimTransfers);
        host.CacheChanged += () => Dispatcher.Invoke(RefreshStatus);
        host.ThumbnailProgress += (done, total) => Dispatcher.Invoke(() =>
            Status($"Vorschaubilder: {done} von {total}"));

        _hosts[share.FolderId] = host;
        return host;
    }

    private async Task StartShareAsync(ShareConfig share)
    {
        if (_identity is null) return;

        var host = EnsureHost(share);
        if (host.State == ShareState.Pausiert) { host.Resume(); return; }
        if (host.State is ShareState.Bereit or ShareState.Verbindet) return;

        SetBusy(true);
        try
        {
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            Status($"[{share.FolderId}] Start fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            UpdateButtons();
            RefreshStatus();
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_current is not null) await StartShareAsync(_current);
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        var host = CurrentHost;
        if (host is null) return;

        if (host.State == ShareState.Pausiert) host.Resume();
        else host.Pause();

        UpdateButtons();
        RefreshStatus();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        var host = CurrentHost;
        if (host is null) return;

        SetBusy(true);
        try { await host.StopAsync(); }
        catch (Exception ex) { Status($"Stoppen fehlgeschlagen: {ex.Message}"); }
        finally { SetBusy(false); UpdateButtons(); RefreshStatus(); }
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = PauseButton.IsEnabled = StopButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void UpdateButtons()
    {
        var state = CurrentHost?.State ?? ShareState.Gestoppt;

        StartButton.IsEnabled = state is ShareState.Gestoppt or ShareState.Fehler or ShareState.Pausiert;
        PauseButton.IsEnabled = state is ShareState.Bereit or ShareState.Pausiert;
        StopButton.IsEnabled = state is not ShareState.Gestoppt;

        PauseButton.Content = state == ShareState.Pausiert ? "Fortsetzen" : "Anhalten";
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
        var finished = _transfers
            .Where(t => t.State is TransferState.Fertig or TransferState.Fehler)
            .Skip(KeepFinished)
            .ToList();

        foreach (var stale in finished) _transfers.Remove(stale);
        RefreshQueueText();
    }

    private void RefreshQueueText()
    {
        var running = _transfers.Count(t => t.State == TransferState.Laeuft);
        var waiting = _transfers.Count(t => t.State == TransferState.Wartet);
        QueueText.Text = running + waiting == 0
            ? "nichts unterwegs"
            : $"{running} aktiv, {waiting} in der Warteschlange";
    }

    private void RefreshStatus()
    {
        var host = CurrentHost;

        if (host is null)
        {
            StateText.Text = "Gestoppt";
            StateDetail.Text = _current is null ? "" : $"{_current.LocalPath}";
            CacheText.Text = "—";
            CacheBar.Value = 0;
            IndexText.Text = "";
            RefreshQueueText();
            return;
        }

        StateText.Text = host.State switch
        {
            ShareState.Verbindet => "Verbindet ...",
            ShareState.Bereit => "Bereit",
            ShareState.Pausiert => "Angehalten",
            ShareState.Fehler => "Fehler",
            _ => "Gestoppt"
        };

        StateDetail.Text = host.State == ShareState.Bereit || host.State == ShareState.Pausiert
            ? $"{host.PeerName} · {host.Config.LocalPath}"
            : host.Config.LocalPath;

        var used = host.CacheUsedBytes;
        var max = host.CacheMaxBytes;

        if (max > 0)
        {
            CacheBar.Value = Math.Min(100, 100.0 * used / max);
            CacheText.Text = $"{host.CacheFileCount} Dateien · " +
                             $"{used / (1024.0 * 1024.0):0.#} von {max / (1024.0 * 1024.0):0.#} MB";
        }
        else
        {
            CacheBar.Value = 0;
            CacheText.Text = $"{host.CacheFileCount} Dateien · {used / (1024.0 * 1024.0):0.#} MB (kein Budget)";
        }

        IndexText.Text = host.IndexCount > 0 ? $"{host.IndexCount} Einträge im Index" : "";
        RefreshQueueText();
    }

    private void AppendLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    // ------------------------------------------------------------ Baum

    private void LoadTree(ShareConfig share)
    {
        FolderTree.ItemsSource = null;
        _tree = null;

        var databasePath = Path.Combine(
            Path.GetDirectoryName(_configPath)!, _config.HomeDirectory, $"index-{share.FolderId}.db");

        if (!File.Exists(databasePath))
        {
            TreeStatus.Text = "Noch kein Index — die Freigabe einmal starten.";
            return;
        }

        try
        {
            // WAL-Modus erlaubt das Mitlesen, auch waehrend die Freigabe laeuft.
            using var index = new PersistentFolderIndex(databasePath, share.FolderId);
            var entries = index.EnumerateLight()
                .Select(e => (e.Name, e.Size, e.IsDirectory))
                .ToList();

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

    // ------------------------------------------------------------ Einstellungen

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateCacheEnabled();
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

    private void OnReload(object sender, RoutedEventArgs e) => Load();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current is null) { Status("Keine Freigabe ausgewählt."); return; }

        if (!long.TryParse(CacheBudgetBox.Text.Trim(), out var megabytes) || megabytes < 0)
        {
            Status("Das Cache-Budget muss eine Zahl in MB sein (0 = unbegrenzt).");
            return;
        }

        _current.LocalPath = LocalPathBox.Text.Trim();
        _current.Mode = ModeBox.SelectedIndex == 1 ? ShareMode.AlwaysLocal : ShareMode.OnDemand;
        _current.CacheMaxBytes = megabytes * 1024 * 1024;

        if (_tree is not null)
        {
            // Der Baum zeigt nur Verzeichnisse. Eintraege, die auf einzelne
            // Dateien zeigen, kann er nicht darstellen -- die wuerden beim
            // Speichern stillschweigend verschwinden. Also behalten wir sie,
            // solange nicht ohnehin alles ausgewaehlt ist.
            var selection = FolderNode.CollectIncluded(_tree);
            if (selection.Count > 0)
            {
                var knownDirectories = new HashSet<string>(
                    AllPaths(_tree), StringComparer.OrdinalIgnoreCase);
                selection.AddRange(_current.Included.Where(p => !knownDirectories.Contains(p)));
            }
            _current.Included = selection;
        }

        try
        {
            _config.Save(_configPath);
            var scope = _current.Included.Count == 0 ? "alles" : $"{_current.Included.Count} Zweig(e)";
            Status($"Gespeichert — {scope} ausgewählt. Wirksam nach Stoppen und Starten der Freigabe.");
        }
        catch (Exception ex)
        {
            Status($"Speichern fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>Alle Verzeichnispfade des Baums -- um zu erkennen, was er darstellen kann.</summary>
    private static IEnumerable<string> AllPaths(FolderNode node)
    {
        yield return node.Path;
        foreach (var child in node.Children)
            foreach (var path in AllPaths(child))
                yield return path;
    }

    private void Status(string message) => StatusBar.Text = message;

    // ------------------------------------------------------------ Ende

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _refresh.Stop();

        // Ohne sauberes Abmelden bleiben die Platzhalter zurueck, ohne dass
        // jemand sie bedienen koennte.
        foreach (var host in _hosts.Values)
        {
            try { await host.DisposeAsync(); }
            catch { /* beim Beenden ist ein Fehler hier belanglos */ }
        }
    }
}
