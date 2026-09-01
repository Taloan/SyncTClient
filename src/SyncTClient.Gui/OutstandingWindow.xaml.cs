using System.Windows;

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

    /// <summary>
    /// Zieht die Liste nach, solange das Fenster offen ist.
    /// </summary>
    /// <remarks>
    /// Waehrend eines Abgleichs wird sie kuerzer. Eine Momentaufnahme waere
    /// nach wenigen Sekunden falsch, und man saehe ihr das nicht an.
    /// </remarks>
    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromSeconds(1) };

    public OutstandingWindow(ShareRow row)
    {
        InitializeComponent();

        _row = row;
        TitleText.Text = App.S("S.Open.For", row.Name);

        Zeigen();

        _takt.Tick += (_, _) => Zeigen();
        _takt.Start();
        Closed += (_, _) => _takt.Stop();
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
