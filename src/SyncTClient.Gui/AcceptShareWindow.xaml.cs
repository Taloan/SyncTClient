using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Klärt vor dem Übernehmen, wohin der Ordner gelegt wird, wie viel er belegen
/// darf und welche Zweige übernommen werden.
/// </summary>
/// <remarks>
/// Der Index der Gegenstelle liegt zu diesem Zeitpunkt bereits vor. Er kommt
/// ohnehin, sobald wir den Ordner ankündigen. Im Explorer ist dagegen noch
/// nichts angelegt. Das geschieht erst, wenn hier bestätigt wird.
/// </remarks>
public partial class AcceptShareWindow : Window
{
    private readonly ShareConfig _share;
    private readonly string _homeDirectory;

    private FolderNode? _tree;
    private bool _loading;

    public AcceptShareWindow(ShareConfig share, string homeDirectory, string title)
    {
        InitializeComponent();

        _share = share;
        _homeDirectory = homeDirectory;

        TitleText.Text = title;
        SubTitleText.Text = App.S("S.FolderId", share.FolderId);

        _loading = true;
        LocalPathBox.Text = share.LocalPath;
        ModeBox.SelectedIndex = share.Mode == ShareMode.AlwaysLocal ? 1 : 0;
        ThumbsBox.IsChecked = share.GenerateThumbnails;
        UpdateModeHint();
        _loading = false;

        LoadTree();
    }

    private void LoadTree()
    {
        var databasePath = Path.Combine(_homeDirectory, $"index-{_share.FolderId}.db");
        if (!File.Exists(databasePath))
        {
            TreeStatus.Text = App.S("A.NoIndex");
            return;
        }

        try
        {
            // WAL-Modus erlaubt das Mitlesen, waehrend der Index noch waechst.
            using var index = new PersistentFolderIndex(databasePath, _share.FolderId);
            var entries = index.EnumerateLight().Select(e => (e.Name, e.Size, e.IsDirectory)).ToList();

            _tree = FolderNode.Build(entries);

            // Beim Uebernehmen ist alles ausgewaehlt. Wer nichts abwaehlt,
            // bekommt den ganzen Ordner. Das entspricht der Erwartung.
            _tree.InitializeChecked(true);
            SetAll(_tree, true);
            _tree.RecomputeUpwards();

            FolderTree.ItemsSource = new[] { _tree };
            TreeStatus.Text = App.S("A.TreeSummary", _tree.FileCount, Format.Bytes(_tree.TotalBytes));
        }
        catch (Exception ex)
        {
            TreeStatus.Text = $"Index nicht lesbar: {ex.Message}";
        }
    }

    private static void SetAll(FolderNode node, bool value)
    {
        node.InitializeChecked(value);
        foreach (var child in node.Children) SetAll(child, value);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };

        // Ein Startpfad, den es nicht gibt, oeffnet den Dialog an unbestimmter Stelle.
        var start = LocalPathBox.Text.Trim();
        if (Directory.Exists(start)) dialog.InitialDirectory = start;
        else if (Directory.Exists(Path.GetDirectoryName(start))) dialog.InitialDirectory = Path.GetDirectoryName(start);

        if (dialog.ShowDialog(this) == true)
            LocalPathBox.Text = Path.Combine(dialog.FolderName, _share.FolderId);
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateModeHint();
    }

    private void UpdateModeHint()
        => ModeHint.Text = ModeBox.SelectedIndex == 0
            ? App.S("A.HintOnDemand")
            : App.S("A.HintAlways");

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (_tree is not null) _tree.IsChecked = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        if (_tree is not null) _tree.IsChecked = false;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var path = LocalPathBox.Text.Trim();
        if (path.Length == 0)
        {
            Hint.Text = App.S("A.NoFolder");
            return;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            Hint.Text = App.S("A.PathNotFull");
            return;
        }

        // Ein Ordner, in dem schon etwas liegt, ist erlaubt, wird aber
        // nachgefragt. Windows legt darin Platzhalter an, und das Vorhandene
        // steht danach mitten im Share.
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            var answer = MessageBox.Show(this,
                App.S("S.Accept.NotEmpty"), App.S("S.Accept.NotEmptyTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK) return;
        }

        _share.LocalPath = path;
        _share.Mode = ModeBox.SelectedIndex == 1 ? ShareMode.AlwaysLocal : ShareMode.OnDemand;
        _share.GenerateThumbnails = ThumbsBox.IsChecked == true;

        // Alles ausgewaehlt bedeutet: keine Einschraenkung. Das ist nicht
        // dasselbe wie eine Liste aller Zweige. Eine solche Liste waere
        // falsch, sobald die Gegenstelle einen neuen Ordner anlegt.
        _share.Included = _tree is null || _tree.IsChecked == true
            ? []
            : FolderNode.CollectIncluded(_tree);

        DialogResult = true;
    }
}
