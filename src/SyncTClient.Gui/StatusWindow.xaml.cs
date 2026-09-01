using System.Collections.ObjectModel;
using System.Windows;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Der Blick aus dem Infobereich: was gerade läuft, ohne das ganze Fenster zu
/// öffnen.
/// </summary>
/// <remarks>
/// Es zeigt dieselben Sammlungen wie die Übersicht, nur schmal. Nichts wird
/// ein zweites Mal beschafft; eine eigene Buchführung wäre eine zweite
/// Wahrheit, die irgendwann von der ersten abweicht.
///
/// Das Fenster verschwindet, sobald es den Eingabefokus verliert. Genau das
/// erwartet man von einem Fenster, das an einem Symbol hängt -- und es erspart
/// den Knopf zum Schließen.
/// </remarks>
public partial class StatusWindow : Window
{
    private readonly Action _openWindow;
    private readonly Action _settings;
    private readonly Action _togglePause;
    private readonly Func<bool> _paused;
    private readonly Func<string> _state;

    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromSeconds(1) };

    /// <param name="shares">Die Zeilen der Übersicht, unverändert.</param>
    /// <param name="transfers">Was gerade geholt wird.</param>
    /// <param name="state">Der Zustand in einem Satz.</param>
    public StatusWindow(
        ObservableCollection<ShareRow> shares,
        ObservableCollection<TransferInfo> transfers,
        Func<string> state,
        Func<bool> paused,
        Action openWindow,
        Action settings,
        Action togglePause)
    {
        InitializeComponent();

        _state = state;
        _paused = paused;
        _openWindow = openWindow;
        _settings = settings;
        _togglePause = togglePause;

        ShareList.ItemsSource = shares;
        TransferList.ItemsSource = transfers;

        Nachziehen();

        _takt.Tick += (_, _) => Nachziehen();
        Deactivated += (_, _) => Hide();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _takt.Start(); else _takt.Stop();
        };
    }

    private void Nachziehen()
    {
        StateText.Text = _state();
        PauseButton.Content = App.S(_paused() ? "S.Tray.Resume" : "S.Tray.Pause");

        // Der Hinweis steht nur da, solange nichts geholt wird. Eine
        // Überschrift über einer leeren Liste sieht aus, als fehle etwas.
        var still = TransferList.Items.Count == 0;
        QuietText.Visibility = still ? Visibility.Visible : Visibility.Collapsed;
        TransferHeader.Visibility = still ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Zeigt das Fenster über dem Infobereich.
    /// </summary>
    /// <remarks>
    /// Gerechnet wird gegen den Arbeitsbereich und nicht gegen den Bildschirm:
    /// die Taskleiste gehört nicht dazu, und sie steht nicht bei jedem unten.
    /// </remarks>
    public void ZeigenAmRand()
    {
        Nachziehen();

        var bereich = SystemParameters.WorkArea;

        // Erst messen, dann setzen. Die Höhe ergibt sich aus dem Inhalt und
        // steht vor dem ersten Anzeigen noch nicht fest.
        Show();
        UpdateLayout();

        Left = bereich.Right - ActualWidth - 12;
        Top = bereich.Bottom - ActualHeight - 12;

        Activate();
    }

    private void OnOpenWindow(object sender, RoutedEventArgs e)
    {
        Hide();
        _openWindow();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        Hide();
        _settings();
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        _togglePause();
        Nachziehen();
    }
}
