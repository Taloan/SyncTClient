using System.Windows;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Wer diese Freigabe trägt, und was mit ihm noch offen ist.</summary>
public partial class SharePeersWindow : Window
{
    private sealed record Zeile(string Activity, string Name, string Address, string Outstanding);

    public SharePeersWindow(ShareRow row)
    {
        InitializeComponent();

        TitleText.Text = $"Knoten – {row.Name}";
        SubtitleText.Text = row.Accepted ? row.PathText : App.S("N.NotConnected");

        Grid.ItemsSource = new[] { Beschreibe(row) };
    }

    private static Zeile Beschreibe(ShareRow row)
    {
        var host = row.Peer.Host;

        var aktivitaet = !row.Accepted
            ? "Angeboten"
            : host.State switch
            {
                PeerState.Verbunden when row.Ready => "Synchronisiert mit",
                PeerState.Verbunden => "Gleicht ab mit",
                PeerState.Verbindet => "Verbindet mit",
                PeerState.Fehler => "Nicht erreichbar",
                _ => "Getrennt"
            };

        return new Zeile(aktivitaet, host.Display, host.Config.Address, Offen(row));
    }

    /// <summary>
    /// Was gegenüber dieser Gegenstelle noch aussteht.
    /// </summary>
    /// <remarks>
    /// Bei „bei Bedarf“ steht nichts aus: die Platzhalter sind vollständig,
    /// die Inhalte kommen absichtlich erst beim Zugriff. Nur „vollständig
    /// lokal“ hat einen echten Rückstand, und der ist die Differenz zwischen
    /// Index und dem, was lokal liegt.
    /// </remarks>
    private static string Offen(ShareRow row)
    {
        var share = row.Share;
        if (share is null) return "—";

        if (share.Phase is not (SyncPhase.Fertig or SyncPhase.Ruht))
            return share.PhaseTotal > 0
                ? $"{share.PhaseDone:N0} von {share.PhaseTotal:N0}"
                : App.S("N.Running");

        if (share.Config.Mode != ShareMode.AlwaysLocal)
            return "nichts – Inhalte kommen bei Bedarf";

        var fehlend = share.IndexCount - share.CacheFileCount;
        return fehlend <= 0 ? "nichts" : $"{fehlend:N0} Dateien";
    }
}
