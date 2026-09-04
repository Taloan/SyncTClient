using System.Windows;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Zeigt, welche Gegenstellen diese Freigabe vorhalten und was ihnen gegenüber noch aussteht.</summary>
public partial class SharePeersWindow : Window
{
    /// <param name="HasItems">
    /// Ob es etwas zu zeigen gibt. Ein Verweis, der ein leeres Fenster
    /// oeffnet, ist ein gebrochenes Versprechen.
    /// </param>
    private sealed record Zeile(
        string Activity, string Name, string Address, string Outstanding, bool HasItems);

    private readonly ShareRow _row;

    /// <summary>Schreibt die Konfiguration fort. Nur durchgereicht.</summary>
    private readonly Action _speichern;

    /// <summary>
    /// Zieht die Zeile nach, solange das Fenster offen ist.
    /// </summary>
    /// <remarks>
    /// Der Rueckstand aendert sich waehrend des Abgleichs. Eine Momentaufnahme
    /// waere nach wenigen Sekunden falsch, und man saehe ihr das nicht an.
    /// </remarks>
    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromSeconds(1) };

    public SharePeersWindow(ShareRow row, Action speichern)
    {
        InitializeComponent();

        _row = row;
        _speichern = speichern;

        TitleText.Text = $"Knoten – {row.Name}";
        SubtitleText.Text = row.Accepted ? row.PathText : App.S("N.NotConnected");

        Zeigen();

        _takt.Tick += (_, _) => Zeigen();
        _takt.Start();
        Closed += (_, _) => _takt.Stop();
    }

    // Je Teilnehmer eine Zeile. Vorher stand hier nur die erste Gegenstelle --
    // aus einer Zeit, in der ein Ordner genau einer gehoerte.
    private void Zeigen()
        => Grid.ItemsSource = _row.Peers.Select(p => Beschreibe(_row, p)).ToList();

    private static Zeile Beschreibe(ShareRow row, PeerItem peer)
    {
        var host = peer.Host;

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

        return new Zeile(
            aktivitaet, host.Display, host.Config.Address, Offen(row),
            row.Share?.OutstandingItems.Count > 0);
    }

    private void OnShowOutstanding(object sender, RoutedEventArgs e)
        => new OutstandingWindow(_row, _speichern) { Owner = this }.ShowDialog();

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
