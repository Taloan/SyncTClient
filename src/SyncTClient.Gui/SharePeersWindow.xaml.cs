using System.Windows;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Zeigt, welche Gegenstellen diese Freigabe vorhalten und was ihnen gegenüber noch aussteht.</summary>
public partial class SharePeersWindow : Window
{
    private sealed record Zeile(string Activity, string Name, string Address, string Outstanding);

    private readonly ShareRow _row;

    /// <summary>
    /// Zieht die Zeile nach, solange das Fenster offen ist.
    /// </summary>
    /// <remarks>
    /// Der Rueckstand aendert sich waehrend des Abgleichs. Eine Momentaufnahme
    /// waere nach wenigen Sekunden falsch, und man saehe ihr das nicht an.
    /// </remarks>
    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromSeconds(1) };

    public SharePeersWindow(ShareRow row)
    {
        InitializeComponent();

        _row = row;

        TitleText.Text = $"Knoten – {row.Name}";
        SubtitleText.Text = row.Accepted ? row.PathText : App.S("N.NotConnected");

        Zeigen();

        _takt.Tick += (_, _) => Zeigen();
        _takt.Start();
        Closed += (_, _) => _takt.Stop();
    }

    private void Zeigen() => Grid.ItemsSource = new[] { Beschreibe(_row) };

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
    /// Dieselbe Zahl, die auch der Balken in der Übersicht zeigt: die
    /// Differenz zwischen beiden Ständen, in beide Richtungen gerechnet. Sie
    /// steht in Bytes, denn 443 Dateien können vier Minuten sein oder vier
    /// Stunden.
    ///
    /// Ein Platzhalter zählt nicht als Rückstand. Er ist bei „on-demand“ der
    /// erwünschte Zustand, und sein Inhalt gehört absichtlich nicht hierher.
    /// </remarks>
    private static string Offen(ShareRow row)
    {
        var share = row.Share;
        if (share is null) return "—";

        if (share.Outstanding <= 0)
            return share.Phase is SyncPhase.Fertig or SyncPhase.Ruht
                ? App.S("N.Nothing")
                : App.S("N.Running");

        return App.S("N.Outstanding",
            Format.Bytes(share.OutstandingBytes),
            Format.Bytes(share.SyncTotalBytes),
            Format.Count(share.Outstanding));
    }
}
