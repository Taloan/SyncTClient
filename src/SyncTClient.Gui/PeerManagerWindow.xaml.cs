using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Verwaltet die Gegenstellen.
/// </summary>
/// <remarks>
/// Bewusst ein eigenes Fenster und kein dauerhafter Bereich im Hauptfenster.
/// Gegenstellen ändern sich selten -- der Platz gehört den Freigaben, und die
/// Zugehörigkeit steht ohnehin in deren Zeile.
/// </remarks>
public partial class PeerManagerWindow : Window
{
    private readonly ObservableCollection<PeerItem> _peers;
    private readonly Func<PeerItem, Task> _connect;
    private readonly Func<PeerItem, Task> _remove;
    private readonly Action _add;

    private PeerItem? _selected;

    public PeerManagerWindow(
        ObservableCollection<PeerItem> peers,
        Action add,
        Func<PeerItem, Task> connect,
        Func<PeerItem, Task> remove)
    {
        InitializeComponent();

        _peers = peers;
        _add = add;
        _connect = connect;
        _remove = remove;

        Grid.ItemsSource = _peers;
        if (_peers.Count > 0) Grid.SelectedIndex = 0;
        UpdateButtons();
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        _selected = Grid.SelectedItem as PeerItem;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ConnectButton.IsEnabled = _selected is not null;
        ConnectButton.Content = _selected?.Host.State == PeerState.Verbunden ? "Trennen" : "Verbinden";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        _add();
        Grid.Items.Refresh();
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        await _connect(_selected);
        foreach (var peer in _peers) peer.Refresh();
        UpdateButtons();
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        await _remove(_selected);
        Grid.Items.Refresh();
        UpdateButtons();
    }
}
