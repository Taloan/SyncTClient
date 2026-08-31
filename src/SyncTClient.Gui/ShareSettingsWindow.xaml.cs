using System.IO;
using System.Windows;
using System.Windows.Controls;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Die Einstellungen einer Freigabe: Modus, Budget und Teilbaum.</summary>
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

        // Das Label vergibt die Gegenstelle, und sie kann es aendern. Die
        // Kennung ist die Identitaet der Freigabe. Sie steht im Pfad, im
        // Protokoll und im Dateinamen des Index.
        SubTitleText.Text = App.S("S.FolderId", share.FolderId);

        _loading = true;
        LocalPathBox.Text = share.LocalPath;
        ModeBox.SelectedIndex = share.Mode == ShareMode.AlwaysLocal ? 1 : 0;
        MinimumCopiesBox.Text = share.MinimumCopies.ToString();
        ConflictBox.SelectedIndex = (int)share.Conflict;
        ThumbsBox.IsChecked = share.GenerateThumbnails;
        KeepVersionsBox.IsChecked = share.KeepVersions;
        VersionDaysBox.Text = share.VersionDays.ToString();
        UpdateVersionFields();
        UpdateCacheEnabled();
        _loading = false;

        LoadTree();
    }

    private void LoadTree()
    {
        var databasePath = Path.Combine(_homeDirectory, $"index-{_share.FolderId}.db");
        if (!File.Exists(databasePath))
        {
            TreeStatus.Text = App.S("S2.NoIndex");
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
            TreeStatus.Text = App.S("S2.Summary", _tree.FileCount, Format.Bytes(_tree.TotalBytes));
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
        // Bei "vollstaendig lokal" wird nie verdraengt. Damit gibt es auch
        // nichts, wofuer eine Mindestzahl an Kopien gelten koennte.
        var onDemand = ModeBox.SelectedIndex == 0;
        CopiesPanel.IsEnabled = onDemand;

        // Als Tooltip statt als Absatz im Fenster. Die Erklaerung wird
        // einmal gelesen, die Einstellung oft benutzt.
        ModeBox.ToolTip = new TextBlock
        {
            Text = onDemand ? App.S("S2.CacheOnDemand") : App.S("S2.CacheAlways"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
        };
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
        // Ohne Sicherung bleibt der bisherige Wert stehen. Das Feld ist dann
        // abgeschaltet und sein Inhalt ohne Bedeutung.
        var days = _share.VersionDays;

        if (KeepVersionsBox.IsChecked == true
            && (!int.TryParse(VersionDaysBox.Text.Trim(), out days) || days < 1))
        {
            MessageBox.Show(this, App.S("S2.DaysInvalid"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            VersionDaysBox.Focus();
            return;
        }

        if (KeepVersionsBox.IsChecked != true) days = _share.VersionDays;

        if (!int.TryParse(MinimumCopiesBox.Text.Trim(), out var copies) || copies < 1)
        {
            SaveHint.Text = App.S("S2.CopiesInvalid");
            return;
        }

        _share.MinimumCopies = copies;
        _share.LocalPath = LocalPathBox.Text.Trim();
        _share.Mode = ModeBox.SelectedIndex == 1 ? ShareMode.AlwaysLocal : ShareMode.OnDemand;
        _share.Conflict = (ConflictResolution)Math.Max(0, ConflictBox.SelectedIndex);
        _share.GenerateThumbnails = ThumbsBox.IsChecked == true;
        _share.KeepVersions = KeepVersionsBox.IsChecked == true;
        _share.VersionDays = days;

        if (_tree is not null)
        {
            // Der Baum zeigt nur Verzeichnisse. Eintraege, die auf einzelne
            // Dateien zeigen, kann er nicht darstellen. Sie wuerden beim
            // Speichern ohne Hinweis verschwinden und werden deshalb
            // uebernommen, solange nicht ohnehin alles ausgewaehlt ist.
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
    private void OnKeepVersionsChanged(object sender, RoutedEventArgs e) => UpdateVersionFields();

    private void UpdateVersionFields()
    {
        var an = KeepVersionsBox.IsChecked == true;
        VersionDaysLabel.IsEnabled = an;
        VersionDaysBox.IsEnabled = an;
    }
}
