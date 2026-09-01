using System.IO;
using System.Windows;
using System.Windows.Controls;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Die Einstellungen einer Freigabe: Modus, Limit und Teilbaum.</summary>
public partial class ShareSettingsWindow : Window
{
    private readonly ShareConfig _share;
    private readonly string _homeDirectory;
    private readonly List<PeerChoice> _peers = [];
    private FolderNode? _tree;
    private bool _loading;

    /// <param name="reportedName">
    /// Wie sich eine Gegenstelle selbst nennt. Optional: ohne laufende
    /// Verbindung ist nur die Kennung bekannt.
    /// </param>
    public ShareSettingsWindow(
        ShareConfig share, IReadOnlyList<PeerConfig> peers, string homeDirectory, string title,
        Func<string, string?>? reportedName = null)
    {
        InitializeComponent();

        _share = share;
        _homeDirectory = homeDirectory;
        TitleText.Text = title;

        LoadPeers(peers, reportedName);

        // Das Label vergibt die Gegenstelle, und sie kann es aendern. Die
        // Kennung ist die Identitaet der Freigabe. Sie steht im Pfad, im
        // Protokoll und im Dateinamen des Index.
        SubTitleText.Text = App.S("S.FolderId", share.FolderId);

        _loading = true;
        LabelBox.Text = share.Label;
        LocalPathBox.Text = share.LocalPath;
        ModeBox.SelectedIndex = share.Mode == ShareMode.AlwaysLocal ? 1 : 0;
        ConflictBox.SelectedIndex = (int)share.Conflict;
        // 0 bedeutet: nicht sichern. Ein Kaestchen daneben waere dieselbe
        // Aussage ein zweites Mal.
        VersionDaysBox.Text = (share.KeepVersions ? share.VersionDays : 0).ToString();
        UpdateCacheEnabled();
        _loading = false;

        LoadTree();
    }

    /// <summary>
    /// Fuellt die Liste "Mit diesen Geraeten geteilt".
    /// </summary>
    /// <remarks>
    /// Angekreuzt ist, was in der Freigabe steht. Eine Gegenstelle, die es
    /// noch nicht tut, steht ebenfalls in der Liste -- sonst muesste man zum
    /// Hinzufuegen erst woanders hin.
    ///
    /// Der Name kommt aus drei Quellen, in dieser Reihenfolge: der selbst
    /// vergebene, der von der Gegenstelle gemeldete, und zuletzt die Kennung.
    /// Ohne die mittlere stand hier zweimal dieselbe Zeichenfolge -- die
    /// Kennung als Name und die Kennung als Kennung --, obwohl das Programm
    /// an anderer Stelle "GEGENSTELLE" anzeigt.
    /// </remarks>
    private void LoadPeers(IReadOnlyList<PeerConfig> peers, Func<string, string?>? reportedName)
    {
        foreach (var peer in peers)
        {
            var geteilt = _share.PeerDeviceIds.Contains(peer.DeviceId, StringComparer.OrdinalIgnoreCase)
                          || peer.DeviceId.Equals(_share.PeerDeviceId, StringComparison.OrdinalIgnoreCase);

            var name = peer.Name;
            if (string.IsNullOrWhiteSpace(name)) name = reportedName?.Invoke(peer.DeviceId) ?? "";
            if (string.IsNullOrWhiteSpace(name)) name = peer.ShortId;

            _peers.Add(new PeerChoice(peer.DeviceId, name, peer.ShortId, geteilt));
        }

        PeerList.ItemsSource = _peers;
        NoPeersText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            var entries = index.EnumerateLight()
                .Select(e => (e.Name, e.Size, e.IsDirectory, e.HasContent))
                .ToList();

            _tree = FolderNode.Build(entries);
            ApplySelection(_tree, _share);
            _tree.RecomputeUpwards();
            Sperren(_tree);

            FolderTree.ItemsSource = new[] { _tree };
            TreeStatus.Text = App.S("S2.Summary", _tree.FileCount, Format.Bytes(_tree.TotalBytes));
        }
        catch (Exception ex)
        {
            TreeStatus.Text = $"Index nicht lesbar: {ex.Message}";
        }
    }

    /// <summary>
    /// Sagt, warum ein Zweig sich nicht abwaehlen laesst.
    /// </summary>
    /// <remarks>
    /// Abwaehlen entfernt den Zweig aus dem Ordner. Erlaubt ist das nur, wenn
    /// die Gegenstelle jede Datei darin vollstaendig fuehrt -- sonst waere es
    /// kein Ausschliessen, sondern ein Loeschen der letzten Kopie.
    /// </remarks>
    private void Sperren(FolderNode node)
    {
        node.Refused = knoten =>
            TreeStatus.Text = App.S("S2.Refused", knoten.Name, Format.Count(knoten.Blocking));

        foreach (var child in node.Children) Sperren(child);
    }

    /// <summary>Uebertraegt die gespeicherte Auswahl auf den frisch gebauten Baum.</summary>
    private static void ApplySelection(FolderNode root, ShareConfig share)
    {
        var everything = share.Included.Count == 0;
        Apply(root);

        void Apply(FolderNode node)
        {
            node.InitializeChecked(everything
                || share.Included.Contains(node.Path, StringComparer.OrdinalIgnoreCase)
                || share.Includes(node.Path));
            foreach (var child in node.Children) Apply(child);
        }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateCacheEnabled();
    }

    private void UpdateCacheEnabled()
    {
        // Bei "vollstaendig lokal" wird nie Speicherplatz freigegeben. Damit gibt es auch
        // nichts, wofuer eine Mindestzahl an Kopien gelten koennte.
        var onDemand = ModeBox.SelectedIndex == 0;

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
        if (!int.TryParse(VersionDaysBox.Text.Trim(), out var days) || days < 0)
        {
            SaveHint.Text = App.S("S2.DaysInvalid");
            VersionDaysBox.Focus();
            return;
        }

        _share.Label = LabelBox.Text.Trim();
        _share.LocalPath = LocalPathBox.Text.Trim();
        _share.Mode = ModeBox.SelectedIndex == 1 ? ShareMode.AlwaysLocal : ShareMode.OnDemand;
        _share.Conflict = (ConflictResolution)Math.Max(0, ConflictBox.SelectedIndex);

        // Die Liste ist massgeblich. Der einzelne Eintrag bleibt daneben
        // stehen, solange der Abgleich je Gegenstelle gefuehrt wird; er ist
        // der erste der Liste.
        _share.PeerDeviceIds = [.. _peers.Where(p => p.Shared).Select(p => p.DeviceId)];

        if (_share.PeerDeviceIds.Count > 0
            && !_share.PeerDeviceIds.Contains(_share.PeerDeviceId, StringComparer.OrdinalIgnoreCase))
        {
            _share.PeerDeviceId = _share.PeerDeviceIds[0];
        }
        // Null Tage heisst: sofort loeschen, also gar nicht sichern.
        _share.KeepVersions = days > 0;
        _share.VersionDays = Math.Max(1, days);

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
}

/// <summary>Eine Gegenstelle in der Liste "Mit diesen Geraeten geteilt".</summary>
public sealed class PeerChoice(string deviceId, string name, string shortId, bool shared)
{
    public string DeviceId { get; } = deviceId;
    public string Name { get; } = name;
    public string ShortId { get; } = shortId;

    /// <summary>Schreibbar: das Kaestchen bindet darauf.</summary>
    public bool Shared { get; set; } = shared;
}
