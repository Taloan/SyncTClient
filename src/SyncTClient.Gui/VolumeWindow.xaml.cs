using System.Windows;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Was auf einem Datenträger als Platzhalter geführt wird, über alle Freigaben
/// dieses Datenträgers hinweg.
/// </summary>
/// <remarks>
/// Aufgezählt wird, was auf das Limit dieses Datenträgers angerechnet wird —
/// bei „bei Bedarf" der ganze Bestand, bei „vollständig lokal" das
/// Freigegebene. Ob der Inhalt gerade hier liegt, steht daneben und entscheidet
/// über den Anfangszustand des Kästchens.
///
/// Angekreuzt heißt: der Inhalt soll hier liegen. Das ist derselbe Weg wie
/// „Immer auf diesem Gerät behalten" im Kontextmenü, nur über einen Baum statt
/// über die Auswahl im Dateimanager. Abgekreuzt ist der Gegenweg.
/// </remarks>
public partial class VolumeWindow : Window
{
    private readonly List<CacheNode> _wurzeln = [];

    /// <param name="laufwerk">Die Wurzel des Datenträgers, etwa <c>C:</c>.</param>
    /// <param name="belegung">Der Satz über Belegung und Limit, wie in der Statistik.</param>
    /// <param name="shares">Alle Freigaben, die auf diesem Datenträger liegen.</param>
    public VolumeWindow(string laufwerk, string belegung, IReadOnlyList<(ShareHost Host, string Name)> shares)
    {
        InitializeComponent();

        TitleText.Text = App.S("S.Vol.For", laufwerk);
        SubtitleText.Text = belegung;

        // Erst zeigen, dann sammeln. Der Aufbau geht über den Index jeder
        // Freigabe dieses Datenträgers -- bei hunderttausend Einträgen
        // Sekunden, und auf dem Oberflächen-Thread stünde solange alles.
        Loaded += async (_, _) => await SammelnAsync(shares);
    }

    private async Task SammelnAsync(IReadOnlyList<(ShareHost Host, string Name)> shares)
    {
        var wurzeln = await Task.Run(() =>
        {
            var liste = new List<CacheNode>();

            foreach (var (host, name) in shares)
            {
                var eintraege = host.CacheEintraege();
                if (eintraege.Count == 0) continue;

                liste.Add(CacheNode.Bauen(host, name, eintraege));
            }

            return liste;
        });

        // Die Knoten sind auf dem anderen Faden entstanden; gebunden werden
        // sie erst hier. Vorher hängt keine Anzeige daran, das ist erlaubt.
        _wurzeln.AddRange(wurzeln);
        Tree.ItemsSource = _wurzeln;

        LoadingPanel.Visibility = Visibility.Collapsed;
        Tree.Visibility = Visibility.Visible;
        ApplyButton.IsEnabled = true;

        // Eine leere Liste unter einer Überschrift sieht aus wie ein Fehler.
        if (_wurzeln.Count == 0) Hint.Text = App.S("S.Vol.Empty");
    }

    /// <summary>
    /// Führt aus, was am Baum geändert wurde.
    /// </summary>
    /// <remarks>
    /// Nur die Änderung, und je Zweig nur einmal: ein angekreuztes Verzeichnis
    /// ist ein Auftrag, kein Verzeichnis samt jeder Datei darin.
    /// <see cref="ShareHost.SetLocal"/> löst es selbst auf.
    ///
    /// Im Hintergrund, weil das Anfordern der Inhalte über die Leitung geht.
    /// Das Fenster schließt sofort; gemeldet wird in der Statuszeile und im
    /// Protokoll, wie überall sonst bei diesen beiden Handgriffen.
    /// </remarks>
    private void OnApply(object sender, RoutedEventArgs e)
    {
        var auftraege = new List<(ShareHost Host, string Path, bool Lokal)>();
        foreach (var wurzel in _wurzeln) wurzel.Sammeln(auftraege);

        Auftraege = auftraege;
        DialogResult = true;
    }

    /// <summary>Was auszuführen ist. Erst nach dem Bestätigen belegt.</summary>
    public IReadOnlyList<(ShareHost Host, string Path, bool Lokal)> Auftraege { get; private set; } = [];
}
