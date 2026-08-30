using System.IO;
using System.Windows;
using System.Windows.Controls;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

public partial class MainWindow : Window
{
    private readonly string _configPath;
    private AppConfig _config = new();
    private ShareConfig? _current;
    private FolderNode? _tree;
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();

        // Die Oberflaeche startet im eigenen Ausgabeverzeichnis, die
        // Konfiguration liegt aber beim Client. Von dort aus nach oben suchen.
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

    // ------------------------------------------------------------ Laden

    private void Load()
    {
        if (!File.Exists(_configPath))
        {
            Status($"Keine Konfiguration gefunden. Erst \"synctmount --init\" ausführen.");
            return;
        }

        try
        {
            _config = AppConfig.Load(_configPath);
        }
        catch (Exception ex)
        {
            Status($"Konfiguration nicht lesbar: {ex.Message}");
            return;
        }

        ShareList.ItemsSource = _config.Shares;
        if (_config.Shares.Count > 0) ShareList.SelectedIndex = 0;
        Status($"{_config.Shares.Count} Freigabe(n) geladen.");
    }

    private void OnShareSelected(object sender, SelectionChangedEventArgs e)
    {
        _current = ShareList.SelectedItem as ShareConfig;
        DetailPanel.IsEnabled = _current is not null;
        if (_current is null) return;

        _loading = true;
        LocalPathBox.Text = _current.LocalPath;
        ModeBox.SelectedIndex = _current.Mode == ShareMode.AlwaysLocal ? 1 : 0;
        CacheBudgetBox.Text = (_current.CacheMaxBytes / (1024 * 1024)).ToString();
        UpdateCacheEnabled();
        _loading = false;

        LoadTree(_current);
    }

    private void LoadTree(ShareConfig share)
    {
        FolderTree.ItemsSource = null;
        _tree = null;

        var databasePath = Path.Combine(
            Path.GetDirectoryName(_configPath)!, _config.HomeDirectory, $"index-{share.FolderId}.db");

        if (!File.Exists(databasePath))
        {
            TreeStatus.Text = "Noch kein Index — den Client einmal laufen lassen.";
            return;
        }

        try
        {
            // WAL-Modus erlaubt das Mitlesen, auch waehrend der Client laeuft.
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

    // ------------------------------------------------------------ Bedienung

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
            var scope = _current.Included.Count == 0
                ? "alles"
                : $"{_current.Included.Count} Zweig(e)";
            Status($"Gespeichert — {scope} ausgewählt. Der Client übernimmt es beim nächsten Start.");
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
}
