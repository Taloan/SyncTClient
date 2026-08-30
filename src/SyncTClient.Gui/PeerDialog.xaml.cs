using System.Windows;
using SyncTClient.Bep;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

public partial class PeerDialog : Window
{
    public PeerConfig Result { get; private set; } = new();

    public PeerDialog() => InitializeComponent();

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0) { Hint.Text = "Die Adresse fehlt."; return; }
        if (!address.Contains(':')) address += ":22000";

        // Lieber hier ablehnen als spaeter bei der Verbindung: die Device-ID
        // traegt Pruefziffern, ein Tippfehler faellt sofort auf.
        if (!DeviceId.TryParse(DeviceIdBox.Text.Trim(), out var id, out var error) || id == DeviceId.Empty)
        {
            Hint.Text = error ?? "Die Device-ID ist unvollständig.";
            return;
        }

        Result = new PeerConfig
        {
            Name = NameBox.Text.Trim(),
            Address = address,
            DeviceId = id.ToString(),
            AutoConnect = AutoBox.IsChecked == true
        };

        DialogResult = true;
    }
}
