using System.Windows;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

public partial class PeerDialog : Window
{
    /// <summary>Die Gegenstelle, die geändert wird. Null steht für eine neue.</summary>
    private readonly PeerConfig? _existing;

    public PeerConfig Result { get; private set; } = new();

    private readonly List<ShareChoice> _shares = [];

    /// <summary>
    /// Die Ordner, an denen dieses Geraet teilnehmen soll.
    /// </summary>
    /// <remarks>
    /// Der Dialog schreibt sie nicht selbst. Er kennt die Geraete-ID erst,
    /// wenn er geschlossen wird, und bei einer neuen Gegenstelle gibt es sie
    /// vorher gar nicht.
    /// </remarks>
    public IReadOnlyList<string> SharedFolders =>
        [.. _shares.Where(s => s.Shared).Select(s => s.FolderId)];

    public PeerDialog(PeerConfig? existing, IReadOnlyList<ShareConfig> shares)
    {
        InitializeComponent();

        _existing = existing;
        LoadShares(existing, shares);

        if (existing is null) return;

        Title = App.S("S.Peer.TitleEdit");
        AddButton.Content = App.S("S.Peer.Apply");

        NameBox.Text = existing.Name;
        AddressBox.Text = existing.Address;
        DeviceIdBox.Text = existing.DeviceId;
        AutoBox.IsChecked = existing.AutoConnect;
        DiscoveryBox.IsChecked = existing.Discovery;
        RelayBox.IsChecked = existing.Relays;
    }

    /// <summary>
    /// Fuellt die Liste der Ordner. Angekreuzt ist, woran das Geraet
    /// teilnimmt.
    /// </summary>
    private void LoadShares(PeerConfig? existing, IReadOnlyList<ShareConfig> shares)
    {
        var id = existing?.DeviceId ?? "";

        foreach (var share in shares)
        {
            var geteilt = id.Length > 0
                          && (share.PeerDeviceIds.Contains(id, StringComparer.OrdinalIgnoreCase)
                              || share.PeerDeviceId.Equals(id, StringComparison.OrdinalIgnoreCase));

            _shares.Add(new ShareChoice(
                share.FolderId,
                string.IsNullOrWhiteSpace(share.Label) ? share.FolderId : share.Label,
                geteilt));
        }

        ShareList.ItemsSource = _shares;
        NoSharesText.Visibility = _shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        // Eine leere Adresse ist erlaubt. Die Adresse wird dann ueber die Erkennung ermittelt.
        var address = AddressBox.Text.Trim();
        if (address.Length > 0 && !address.Contains(':')) address += ":22000";

        // Lieber hier ablehnen als spaeter beim Verbindungsaufbau. Die
        // Device-ID traegt Pruefziffern, ein Tippfehler faellt sofort auf.
        if (!DeviceId.TryParse(DeviceIdBox.Text.Trim(), out var id, out var error) || id == DeviceId.Empty)
        {
            Hint.Text = error ?? App.S("P.IdIncomplete");
            return;
        }

        // Beim Aendern dieselbe Instanz behalten. Sie steht in der
        // Konfiguration, und die Freigaben verweisen darauf.
        Result = _existing ?? new PeerConfig();

        Result.Name = NameBox.Text.Trim();
        Result.Address = address;
        Result.DeviceId = id.ToString();
        Result.AutoConnect = AutoBox.IsChecked == true;
        Result.Discovery = DiscoveryBox.IsChecked == true;
        Result.Relays = RelayBox.IsChecked == true;

        DialogResult = true;
    }
}

/// <summary>Ein Ordner in der Liste "Geteilte Ordner".</summary>
public sealed class ShareChoice(string folderId, string name, bool shared)
{
    public string FolderId { get; } = folderId;
    public string Name { get; } = name;

    /// <summary>Schreibbar: das Kaestchen bindet darauf.</summary>
    public bool Shared { get; set; } = shared;
}
