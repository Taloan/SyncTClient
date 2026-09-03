using System.IO;
using System.Text;
using System.Windows;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Macht aus einem Ordner, der hier bereits liegt, eine eigene Freigabe.
/// </summary>
/// <remarks>
/// Der Gegenweg zum Übernehmen. Dort kommt ein Ordner von einer Gegenstelle,
/// sein Inhalt steht im Index, bevor irgendetwas angelegt wird. Hier steht der
/// Ordner samt Inhalt bereits; zu klären ist nur, unter welcher Kennung er
/// angeboten wird und wem.
///
/// Auf einen Index der Gegenstelle wird dabei nicht gewartet. Sie kennt den
/// Ordner noch gar nicht -- sie bekommt ihn angekündigt und muss ihn erst
/// annehmen.
/// </remarks>
public partial class NewShareWindow : Window
{
    private readonly string _pfad;
    private readonly IReadOnlyList<ShareConfig> _bestehende;
    private readonly List<PeerChoice> _peers = [];

    /// <summary>Was hinterher in die Konfiguration geht. Erst nach OK gültig.</summary>
    public ShareConfig Result { get; private set; } = new();

    /// <summary>Die Geräte-IDs der angekreuzten Gegenstellen.</summary>
    public IReadOnlyList<string> ChosenPeers =>
        [.. _peers.Where(p => p.Shared).Select(p => p.DeviceId)];

    /// <param name="pfad">Der Ordner, aus dem die Freigabe wird.</param>
    /// <param name="bestehende">Alle bereits eingerichteten Freigaben.</param>
    /// <param name="peers">Die bekannten Gegenstellen, in der Reihenfolge der Übersicht.</param>
    public NewShareWindow(
        string pfad,
        IReadOnlyList<ShareConfig> bestehende,
        IEnumerable<PeerConfig> peers,
        Func<string, string?>? reportedName = null)
    {
        InitializeComponent();

        _pfad = pfad;
        _bestehende = bestehende;

        PathText.Text = pfad;

        var name = Path.GetFileName(pfad.TrimEnd(Path.DirectorySeparatorChar));
        LabelBox.Text = name;
        FolderIdBox.Text = Kennung(name);

        // Dieselbe Namenswahl wie in den Freigabe-Einstellungen: der
        // eingetragene Name, sonst der von der Gegenstelle gemeldete, sonst
        // die kurze Kennung.
        foreach (var peer in peers)
        {
            var anzeige = peer.Name;
            if (string.IsNullOrWhiteSpace(anzeige)) anzeige = reportedName?.Invoke(peer.DeviceId) ?? "";
            if (string.IsNullOrWhiteSpace(anzeige)) anzeige = peer.ShortId;

            _peers.Add(new PeerChoice(peer.DeviceId, anzeige, peer.ShortId, shared: false));
        }

        PeerList.ItemsSource = _peers;
        NoPeersText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Macht aus einem Ordnernamen eine Ordnerkennung.
    /// </summary>
    /// <remarks>
    /// Syncthing laesst als Kennung fast alles zu, aber sie steht in
    /// Dateinamen dieses Programms -- der Index heisst "index-&lt;Kennung&gt;.db".
    /// Deshalb bleiben nur Buchstaben, Ziffern und der Bindestrich stehen.
    /// Umlaute werden nicht uebersetzt, sondern weggelassen: eine Kennung ist
    /// keine Beschriftung, die steht daneben.
    /// </remarks>
    private static string Kennung(string name)
    {
        var bau = new StringBuilder(name.Length);
        var strich = false;

        foreach (var zeichen in name.ToLowerInvariant())
        {
            if (zeichen is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                bau.Append(zeichen);
                strich = false;
            }
            else if (!strich && bau.Length > 0)
            {
                bau.Append('-');
                strich = true;
            }
        }

        return bau.ToString().Trim('-');
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var kennung = FolderIdBox.Text.Trim();

        if (kennung.Length == 0)
        {
            Hint.Text = App.S("S.New.NoFolderId");
            return;
        }

        if (_bestehende.Any(s => s.FolderId.Equals(kennung, StringComparison.OrdinalIgnoreCase)))
        {
            Hint.Text = App.S("S.New.FolderIdTaken", kennung);
            return;
        }

        // Derselbe Test wie in den beiden anderen Dialogen: Windows laesst
        // keine Wurzel in einer Wurzel zu, und ein Systemordner taugt gar
        // nicht.
        if (App.WurzelFehler(_pfad, _bestehende.Select(s => s.LocalPath)) is { } fehler)
        {
            Hint.Text = fehler;
            return;
        }

        if (ChosenPeers.Count == 0)
        {
            Hint.Text = App.S("S.New.NoPeerChosen");
            return;
        }

        Result = new ShareConfig
        {
            FolderId = kennung,
            Label = LabelBox.Text.Trim(),
            LocalPath = _pfad,
            Own = true,
            Mode = ModeBox.SelectedIndex == 1 ? ShareMode.AlwaysLocal : ShareMode.OnDemand,
            PeerDeviceId = ChosenPeers[0],
            PeerDeviceIds = [.. ChosenPeers]
        };

        DialogResult = true;
    }
}

