using System.Windows;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

public partial class PeerDialog : Window
{
    /// <summary>Die Gegenstelle, die geändert wird -- oder null für eine neue.</summary>
    private readonly PeerConfig? _existing;

    public PeerConfig Result { get; private set; } = new();

    public PeerDialog() : this(null) { }

    public PeerDialog(PeerConfig? existing)
    {
        InitializeComponent();

        _existing = existing;
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

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        // Eine leere Adresse ist erlaubt: dann sucht die Erkennung.
        var address = AddressBox.Text.Trim();
        if (address.Length > 0 && !address.Contains(':')) address += ":22000";

        // Lieber hier ablehnen als spaeter bei der Verbindung: die Device-ID
        // traegt Pruefziffern, ein Tippfehler faellt sofort auf.
        if (!DeviceId.TryParse(DeviceIdBox.Text.Trim(), out var id, out var error) || id == DeviceId.Empty)
        {
            Hint.Text = error ?? App.S("P.IdIncomplete");
            return;
        }

        // Beim Aendern dieselbe Instanz behalten: sie haengt in der
        // Konfiguration, und daran haengen die Freigaben.
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
