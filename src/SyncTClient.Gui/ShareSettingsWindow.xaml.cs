using System.IO;
using System.Windows;
using System.Windows.Controls;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Die Einstellungen einer Freigabe -- Modus, Budget, Teilbaum.</summary>
public partial class ShareSettingsWindow : Window
{
    private readonly ShareConfig _share;
    private readonly string _homeDirectory;
    private FolderNode? _tree;
    private bool _loading;

    public ShareSettingsWindow(ShareConfig share, string homeDirectory, string title)
    {
        InitializeComponent();

        _share = share;
        _homeDirectory = homeDirectory;
        TitleText.Text = title;

        _loading = true;
        LocalPathBox.Text = share.LocalPath;
        ModeBox.SelectedIndex = share.Mode == ShareMode.AlwaysLocal ? 1 : 0;
        CacheBudgetBox.Text = (share.CacheMaxBytes / (1024 * 1024)).ToString();
        ThumbsBox.IsChecked = share.GenerateThumbnails;
        AutoStartBox.IsChecked = share.AutoStart;
        UpdateCacheEnabled();
        _loading = false;

        LoadTree();
    }

    private void LoadTree()
    {
        var databasePath = Path.Combine(_homeDirectory, $"index-{_share.FolderId}.db");
        if (!File.Exists(databasePath))
        {
            TreeStatus.Text = "Noch kein Index — den Ordner einmal starten.";
            return;
        }

        try
        {
            // WAL-Modus erlaubt das Mitlesen, auch waehrend der Ordner laeuft.
            using var index = new PersistentFolderIndex(databasePath, _share.FolderId);
            var entries = index.EnumerateLight().Select(e => (e.Name, e.Size, e.IsDirectory)).ToList();

            _tree = FolderNode.Build(entries);
            ApplySelection(_tree, _share);
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
        if (!long.TryParse(CacheBudgetBox.Text.Trim(), out var megabytes) || megabytes < 0)
        {
            SaveHint.Text = "Das Cache-Budget muss eine Zahl in MB sein (0 = unbegrenzt).";
            return;
        }

        _share.LocalPath = LocalPathBox.Text.Trim();
        _share.Mode = ModeBox.SelectedIndex == 1 ? ShareMode.AlwaysLocal : ShareMode.OnDemand;
        _share.CacheMaxBytes = megabytes * 1024 * 1024;
        _share.GenerateThumbnails = ThumbsBox.IsChecked == true;
        _share.AutoStart = AutoStartBox.IsChecked == true;

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
                selection.AddRange(_share.Included.Where(p => !known.Contains(p)));
            }
            _share.Included = selection;
        }

        DialogResult = true;
    }

    private static IEnumerable<string> AllPaths(FolderNode node)
    {
        yield return node.Path;
        foreach (var child in node.Children)
            foreach (var path in AllPaths(child))
                yield return path;
    }
}
