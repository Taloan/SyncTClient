using System.Windows;
using SyncTClient.Mount;
using SyncTClient.Vfs;

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
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly List<CacheNode> _wurzeln = [];
    private readonly AppConfig _config;
    private readonly string _wurzel;
    private readonly Func<string, VolumeUsage?> _belegung;
    private readonly Func<string, Task<string>> _freigeben;

    /// <param name="laufwerk">Die Wurzel des Datenträgers, etwa <c>C:</c>.</param>
    /// <param name="belegung">Der Satz über Belegung und Limit, wie in der Statistik.</param>
    /// <param name="shares">Alle Freigaben, die auf diesem Datenträger liegen.</param>
    /// <param name="config">Für die Grenzen dieses Datenträgers.</param>
    /// <param name="zahlen">Belegung und Verdrängbares, für die Zeile über dem Knopf.</param>
    /// <param name="freigeben">Gibt auf diesem Datenträger frei, was freigegeben werden darf.</param>
    public VolumeWindow(
        string laufwerk, string belegung, IReadOnlyList<(ShareHost Host, string Name)> shares,
        AppConfig config, Func<string, VolumeUsage?> zahlen, Func<string, Task<string>> freigeben)
    {
        InitializeComponent();

        _config = config;
        _wurzel = laufwerk;
        _belegung = zahlen;
        _freigeben = freigeben;

        TitleText.Text = App.S("S.Vol.For", laufwerk);
        SubtitleText.Text = belegung;

        var grenzen = config.LimitsFor(laufwerk);
        MaxGbBox.Text = (grenzen.MaxBytes / Gigabyte).ToString();
        MinFreeGbBox.Text = (grenzen.MinimumFreeBytes / Gigabyte).ToString();

        // Nicht hier ausrechnen. Was sich verdraengen laesst, geht ueber den
        // Bestand jeder Freigabe dieses Datentraegers und fragt fuer jede
        // Datei den Index -- Sekunden, und das Fenster waere noch gar nicht
        // zu sehen. Solange steht dort derselbe Satz wie ueber dem Baum.
        EvictText.Text = App.S("S.Vol.Loading");

        // Erst zeigen, dann sammeln. Der Aufbau geht über den Index jeder
        // Freigabe dieses Datenträgers -- bei hunderttausend Einträgen
        // Sekunden, und auf dem Oberflächen-Thread stünde solange alles.
        // Zwei Wege, die beide ueber den Index gehen. Sie laufen nebeneinander:
        // die Zahlen sind meist eher da als der Baum, und dann steht schon
        // etwas, waehrend der Baum noch entsteht.
        Loaded += async (_, _) => await Task.WhenAll(SammelnAsync(shares), ZahlenZeigenAsync());
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
        // Erst prüfen, dann übernehmen. Ein halb angenommener Dialog, der sich
        // trotzdem schließt, ist schlimmer als einer, der stehen bleibt.
        if (!long.TryParse(MaxGbBox.Text.Trim(), out var max) || max < 1 ||
            !long.TryParse(MinFreeGbBox.Text.Trim(), out var frei) || frei < 0)
        {
            Hint.Text = App.S("G.VolumeInvalid", _wurzel);
            return;
        }

        var bisher = _config.LimitsFor(_wurzel);
        GrenzenGeaendert = bisher.MaxBytes != max * Gigabyte
                        || bisher.MinimumFreeBytes != frei * Gigabyte;

        if (GrenzenGeaendert) _config.SetLimits(_wurzel, max * Gigabyte, frei * Gigabyte);

        var auftraege = new List<(ShareHost Host, string Path, bool Lokal)>();
        foreach (var wurzel in _wurzeln) wurzel.Sammeln(auftraege);

        Auftraege = auftraege;
        DialogResult = true;
    }

    /// <summary>Ob die Grenzen dieses Datenträgers geändert wurden.</summary>
    /// <remarks>
    /// Der Aufrufer schreibt die Konfiguration. Dieses Fenster ändert sie nur
    /// im Speicher -- so wie es der Dialog der Einstellungen auch tut.
    /// </remarks>
    public bool GrenzenGeaendert { get; private set; }

    /// <summary>
    /// Die Zeile über dem Knopf: was sich auf diesem Datenträger freigeben
    /// lässt.
    /// </summary>
    /// <remarks>
    /// Im Hintergrund: das Ermitteln geht ueber den Bestand jeder Freigabe
    /// dieses Datentraegers. Auf dem Faden, der das Fenster zeichnet, staende
    /// solange alles -- und genau das war es einmal.
    /// </remarks>
    private async Task ZahlenZeigenAsync()
    {
        var zahlen = await Task.Run(() => _belegung(_wurzel));

        var dateien = zahlen?.EvictableFiles ?? 0;
        var bytes = zahlen?.EvictableBytes ?? 0;

        EvictText.Text = App.S("M.VolumeEvictable", Format.Count(dateien), Format.Bytes(bytes));
        ReleaseButton.Content = App.S("M.VolumeRelease", Format.Count(dateien), Format.Bytes(bytes));
        ReleaseButton.IsEnabled = dateien > 0;
    }

    /// <summary>
    /// Gibt auf diesem Datenträger frei, was freigegeben werden darf.
    /// </summary>
    /// <remarks>
    /// Dieselben Sperren wie überall: angeheftete Dateien bleiben, und die
    /// Platzhalter-Schwelle gilt auch hier. Der Knopf ist eine Bitte.
    /// </remarks>
    private async void OnRelease(object sender, RoutedEventArgs e)
    {
        ReleaseButton.IsEnabled = false;
        Hint.Text = App.S("G.Clearing");

        try
        {
            Hint.Text = await _freigeben(_wurzel);
        }
        catch (Exception ex)
        {
            Hint.Text = App.S("G.ClearFailed", ex.Message);
        }
        finally
        {
            await ZahlenZeigenAsync();
        }
    }

    /// <summary>Was auszuführen ist. Erst nach dem Bestätigen belegt.</summary>
    public IReadOnlyList<(ShareHost Host, string Path, bool Lokal)> Auftraege { get; private set; } = [];
}
