using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
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

    /// <summary>Oeffnet den Ordner einer Freigabe.</summary>
    private readonly Action<string?> _openFolder;
    private readonly Func<bool> _paused;
    private readonly Func<string> _state;

    private readonly ObservableCollection<TransferInfo> _outgoing;
    private readonly ObservableCollection<TransferInfo> _incoming;

    /// <summary>
    /// Was unter "zuletzt übertragen" steht.
    /// </summary>
    /// <remarks>
    /// Eine eigene Liste und nicht die beiden Sammlungen selbst: die Übersicht
    /// hält je Richtung die letzten fünfundzwanzig abgeschlossenen
    /// Übertragungen, und beide zusammen füllten dieses schmale Fenster mit
    /// fünfzig Zeilen. Gefragt ist hier, was gerade geschieht.
    /// </remarks>
    private readonly ObservableCollection<TransferInfo> _zuletzt = [];

    /// <summary>So viele Übertragungen stehen darin.</summary>
    private const int Hoechstens = 10;

    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromSeconds(1) };

    /// <param name="shares">Die Zeilen der Übersicht, unverändert.</param>
    /// <param name="outgoing">Was gerade hinausgeht.</param>
    /// <param name="incoming">Was gerade hereinkommt.</param>
    /// <param name="state">Der Zustand in einem Satz.</param>
    public StatusWindow(
        ObservableCollection<ShareRow> shares,
        ObservableCollection<TransferInfo> outgoing,
        ObservableCollection<TransferInfo> incoming,
        Func<string> state,
        Func<bool> paused,
        Action openWindow,
        Action settings,
        Action togglePause,
        Action<string?> openFolder)
    {
        InitializeComponent();

        _openFolder = openFolder;
        _state = state;
        _paused = paused;
        _openWindow = openWindow;
        _settings = settings;
        _togglePause = togglePause;

        ShareList.ItemsSource = shares;

        // Beide Richtungen in einer Liste. Das kleine Fenster hat keinen
        // Platz fuer zwei Spalten; der Pfeil vor dem Namen sagt die Richtung.
        _outgoing = outgoing;
        _incoming = incoming;
        TransferList.ItemsSource = _zuletzt;

        Nachziehen();

        _takt.Tick += (_, _) => Nachziehen();
        Deactivated += (_, _) => Hide();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _takt.Start(); else _takt.Stop();
        };
    }

    /// <summary>
    /// Stellt die Liste der Übertragungen zusammen.
    /// </summary>
    /// <remarks>
    /// Laufende zuerst: sie sind der Grund, warum jemand auf das Symbol
    /// klickt. Eine große Datei, die seit Minuten übertragen wird, fiele sonst
    /// hinter zehn kleine, die inzwischen fertig wurden.
    ///
    /// Neu aufgebaut wird nur, wenn sich die Auswahl geändert hat. Die
    /// Einträge selbst melden ihren Fortschritt; die Liste jede Sekunde zu
    /// leeren und neu zu füllen ließe sie flackern.
    /// </remarks>
    private void UebertragungenNachziehen()
    {
        var neu = _outgoing.Concat(_incoming)
            .OrderByDescending(t => t.State is TransferState.Laeuft or TransferState.Wartet)
            .ThenByDescending(t => t.Started)
            .Take(Hoechstens)
            .ToList();

        if (neu.SequenceEqual(_zuletzt)) return;

        _zuletzt.Clear();
        foreach (var eintrag in neu) _zuletzt.Add(eintrag);
    }

    private void Nachziehen()
    {
        UebertragungenNachziehen();

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

        // Die Obergrenze ergibt sich aus dem Bildschirm, nicht aus einer
        // einmal geschätzten Zahl.
        //
        // Feste 620 Punkte reichten für vier Freigaben. Bei sieben stand die
        // Liste oben und drängte die zuletzt übertragenen Dateien nach unten
        // aus dem Fenster — dabei sind sie der Grund, warum jemand auf das
        // Symbol klickt. Jetzt wächst das Fenster mit, bis der
        // Arbeitsbereich es nicht mehr hergibt.
        MaxHeight = Math.Max(320, bereich.Height - 24);

        // Erst messen, dann setzen. Die Höhe ergibt sich aus dem Inhalt und
        // steht vor dem ersten Anzeigen noch nicht fest.
        Show();
        UpdateLayout();

        Left = bereich.Right - ActualWidth - 12;
        Top = bereich.Bottom - ActualHeight - 12;

        Activate();
    }

    /// <summary>
    /// Ein Klick auf eine Freigabe oeffnet ihren Ordner.
    /// </summary>
    /// <remarks>
    /// Das Fenster verschwindet dabei ohnehin -- es verbirgt sich, sobald es
    /// den Fokus verliert, und den nimmt der Explorer. Verborgen wird trotzdem
    /// ausdruecklich: sonst bliebe es einen Augenblick stehen und der Klick
    /// saehe aus, als waere er ins Leere gegangen.
    /// </remarks>
    private void OnShareClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShareRow zeile }) return;

        // Eine Freigabe ohne Verbindung hat keinen Ordner. OpenFolder sagt
        // das selbst, wenn der Pfad zwar eingetragen, aber nicht da ist.
        if (string.IsNullOrEmpty(zeile.PathText)) return;

        Hide();
        _openFolder(zeile.PathText);
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
