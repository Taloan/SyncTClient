using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SyncTClient.Gui;

/// <summary>Welche Dateien noch ausstehen, einzeln.</summary>
/// <remarks>
/// Die Zahl im Knotenfenster sagt, dass etwas aussteht. Bei vier offenen
/// Dateien von 976 ist die eigentliche Frage, welche vier -- und warum.
/// </remarks>
public partial class OutstandingWindow : Window
{
    private sealed record Zeile(string Name, string Size, string Reason);

    private readonly ShareRow _row;

    /// <summary>Schreibt die Konfiguration fort. Die fuehrt die Uebersicht.</summary>
    private readonly Action _speichern;

    /// <summary>
    /// Zieht die Liste nach, solange das Fenster offen ist.
    /// </summary>
    /// <remarks>
    /// Waehrend eines Abgleichs wird sie kuerzer. Eine Momentaufnahme waere
    /// nach wenigen Sekunden falsch, und man saehe ihr das nicht an.
    /// </remarks>
    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromSeconds(1) };

    public OutstandingWindow(ShareRow row, Action speichern)
    {
        InitializeComponent();

        _row = row;
        _speichern = speichern;
        TitleText.Text = App.S("S.Open.For", row.Name);

        Zeigen();

        _takt.Tick += (_, _) => Zeigen();
        _takt.Start();
        Closed += (_, _) => _takt.Stop();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        // Der Rechtsklick soll die Zeile treffen, auf die er zeigt. Sonst
        // gaelte das Menue fuer die vorher ausgewaehlte Zeile.
        var quelle = e.OriginalSource as DependencyObject;
        while (quelle is not null and not DataGridRow)
            quelle = VisualTreeHelper.GetParent(quelle);

        if (quelle is DataGridRow zeile) zeile.IsSelected = true;
    }

    /// <summary>
    /// Nimmt die gewaehlte Datei vom Abgleich aus.
    /// </summary>
    /// <remarks>
    /// Eingetragen wird der Dateiname ohne Pfad. Ein Muster ohne "/" trifft
    /// den Namen auf jeder Ebene der Freigabe -- gemeint ist meist genau das:
    /// eine Arbeitsdatei, die eine Anwendung staendig offen haelt, liegt
    /// selten nur an einer Stelle.
    ///
    /// Danach dasselbe wie beim Speichern der Einstellungen: die Muster gelten
    /// sofort, und was sie treffen, wird aus dem Index genommen. Dateien mit
    /// Inhalt bleiben dabei liegen; entfernt werden nur Platzhalter.
    /// </remarks>
    private void OnIgnore(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not Zeile zeile) return;
        if (_row.Share is not { } host) return;

        // Der Index trennt mit "/", nicht mit dem Trenner des
        // Dateisystems. Gemeint ist der letzte Abschnitt.
        var schnitt = zeile.Name.LastIndexOf('/');
        var muster = schnitt < 0 ? zeile.Name : zeile.Name[(schnitt + 1)..];
        if (muster.Length == 0) return;

        if (!host.Config.Ignored.Contains(muster, StringComparer.OrdinalIgnoreCase))
            host.Config.Ignored.Add(muster);

        _speichern();

        // Im Hintergrund: der Lauf geht ueber den ganzen Index, und das
        // Fenster soll dabei nicht stehen.
        _ = Task.Run(() =>
        {
            host.ApplyExplorerVisibility();
            host.PurgeIgnored();
        });
    }

    private void Zeigen()
    {
        var share = _row.Share;
        var offen = share?.OutstandingItems ?? [];

        Grid.ItemsSource = offen
            .Select(e => new Zeile(e.Name, Format.Bytes(e.Bytes), e.Reason))
            .ToList();

        // Die Zahl darueber, nicht nur die Zeilen darunter. Der Deckel der
        // Liste faellt sonst niemandem auf: sie waere einfach zu kurz.
        SubtitleText.Text = share is null || share.Outstanding == 0
            ? App.S("S.Open.None")
            : offen.Count < share.Outstanding
                ? App.S("S.Open.Some", offen.Count, Format.Count(share.Outstanding),
                        Format.Bytes(share.OutstandingBytes))
                : App.S("S.Open.All", Format.Count(share.Outstanding),
                        Format.Bytes(share.OutstandingBytes));
    }
}
