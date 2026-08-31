using System.ComponentModel;
using System.Runtime.CompilerServices;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Eine Zeile der Freigabentabelle.
/// </summary>
/// <remarks>
/// Deckt beide Faelle ab: einen uebernommenen Ordner und einen, den die
/// Gegenstelle nur anbietet. Beide stehen in derselben Liste. Ein angebotener
/// Ordner steht damit an der Stelle, an der er nach dem Uebernehmen ebenfalls
/// steht, nur ohne Zahlen.
/// </remarks>
public sealed class ShareRow(PeerItem peer, string folderId, string label, ShareHost? share)
    : INotifyPropertyChanged
{
    public PeerItem Peer { get; } = peer;
    public string FolderId { get; } = folderId;
    public string Label { get; private set; } = label;
    public ShareHost? Share { get; private set; } = share;

    public bool Accepted => Share is not null;

    /// <summary>
    /// Der Anzeigename. Schreibbar, damit er sich in der Liste umbenennen
    /// laesst.
    /// </summary>
    /// <remarks>
    /// Nur uebernommene Freigaben haben eine eigene Bezeichnung. Bei einem
    /// Ordner, den die Gegenstelle blosz anbietet, kommt der Name von dort --
    /// ihn hier zu aendern haette nichts, woran es haengen bleiben koennte.
    ///
    /// Ein leerer Name faellt auf die Ordner-Kennung zurueck. So bleibt die
    /// Zeile in jedem Fall benennbar, auch wenn jemand das Feld leert.
    /// </remarks>
    public string Name
    {
        get => string.IsNullOrWhiteSpace(Label) ? FolderId : Label;
        set
        {
            if (Share is null) return;

            var neu = (value ?? "").Trim();
            if (neu == FolderId) neu = "";
            if (neu == Label) return;

            Label = neu;
            Share.Config.Label = neu;

            Notify();
            Renamed?.Invoke(this);
        }
    }

    /// <summary>Meldet eine Umbenennung, damit sie gespeichert wird.</summary>
    public event Action<ShareRow>? Renamed;

    /// <summary>
    /// Wird gerade in dieser Zeile getippt?
    /// </summary>
    /// <remarks>
    /// Die Tabelle frischt im Sekundentakt auf. Ohne diese Sperre schriebe
    /// eine Auffrischung mitten im Umbenennen den alten Namen in das
    /// Eingabefeld zurueck.
    /// </remarks>
    public bool Editing { get; set; }

    // ---------------------------------------------------------------- Zustand

    public string StatusText => Share?.State switch
    {
        ShareState.Bereit => Share.Phase == SyncPhase.Fertig ? App.S("R.Ready") : PhaseText,
        ShareState.Wartet => PhaseText,
        ShareState.Pausiert => App.S("R.Paused"),
        ShareState.Fehler => App.S("R.Error"),
        ShareState.Gestoppt => App.S("R.Stopped"),
        _ => App.S("R.NotConnected")
    };

    private string PhaseText => Share?.Phase switch
    {
        SyncPhase.Index => App.S("R.PhaseIndex"),
        SyncPhase.Platzhalter => App.S("R.PhasePlaceholders"),
        SyncPhase.Cache => App.S("R.PhaseCache"),
        SyncPhase.Inhalte => App.S("R.PhaseContent"),
        _ => App.S("R.PhaseWaiting")
    };

    /// <summary>Haken, wenn der Abgleich abgeschlossen ist. Entspricht der gruenen Spalte in Resilio.</summary>
    public bool Ready => Share is { State: ShareState.Bereit, Phase: SyncPhase.Fertig };

    // --------------------------------------------------------------- Knoten

    public bool PeerOnline => Peer.Host.State == PeerState.Verbunden;

    public string PeersText => Accepted ? (PeerOnline ? "1 von 1" : "0 von 1") : "—";

    public string PeerText => Peer.Display;

    // ------------------------------------------------------- Vollständige Kopien

    /// <summary>
    /// Wie viele erreichbare Knoten den Inhalt vorhalten. Der eigene Knoten
    /// ist nicht mitgezählt.
    /// </summary>
    public int Copies => Share?.ReachableCopies ?? 0;

    public string CopiesText => Accepted ? Copies.ToString() : "—";

    /// <summary>
    /// Warnt, wenn kein erreichbarer Knoten den Inhalt mehr vorhält.
    /// </summary>
    /// <remarks>
    /// Ein Platzhalter verweist auf eine Datei, deren Inhalt anderswo liegt.
    /// Hält kein Knoten den Inhalt mehr vor, ist die Datei nicht mehr
    /// abrufbar. Dem Ordner sieht man das nicht an, weil die Namen weiter
    /// angezeigt werden.
    ///
    /// Die Zahl ist eine Untergrenze. Über Knoten, mit denen gerade keine
    /// Verbindung besteht, ist nichts bekannt.
    /// </remarks>
    public bool CopiesAtRisk => Accepted && Copies < 1;

    public string CopiesHint => !Accepted
        ? App.S("R.NotConnectedHint")
        : Copies == 0
            ? App.S("R.CopiesNone")
            : App.S("R.CopiesSome", Copies);

    // ------------------------------------------------------------ Fortschritt

    /// <summary>
    /// Sichtbar, solange der Abgleich laeuft.
    /// </summary>
    /// <remarks>
    /// Ein Balken, der dauerhaft auf 100 steht, traegt keine Information und
    /// lenkt von den Balken ab, deren Abgleich tatsaechlich laeuft.
    /// </remarks>
    public bool Busy => Share is not null
                        && Share.Phase != SyncPhase.Fertig
                        && Share.Phase != SyncPhase.Ruht;

    /// <summary>Unbestimmt, solange die Gesamtzahl unbekannt ist.</summary>
    public bool Indeterminate => Busy && Share!.PhaseTotal == 0;

    public double Percent => Share is null || Share.PhaseTotal == 0
        ? 0
        : Math.Clamp(100.0 * Share.PhaseDone / Share.PhaseTotal, 0, 100);

    public string ProgressText
    {
        get
        {
            if (Share is null) return "";
            if (!Busy) return Share.State == ShareState.Bereit ? "abgeglichen" : "";

            return Share.PhaseTotal == 0
                ? $"{Share.PhaseDone:N0}"
                : $"{Share.PhaseDone:N0} von {Share.PhaseTotal:N0}";
        }
    }

    // ------------------------------------------------------------- Zahlen

    public string ReceivedText => Share is null ? "—" : Format.Bytes(Share.BytesReceived);
    public string SentText => Share is null ? "—" : Format.Bytes(Share.BytesSent);
    public string SizeText => Share is null ? "—" : Format.Bytes(Share.IndexBytes);
    public string LocalSizeText => Share is null ? "—" : Format.Bytes(Share.CacheUsedBytes);
    public string FilesText => Share is null ? "—" : Format.Count(Share.IndexCount);
    public string LocalFilesText => Share is null ? "—" : Format.Count(Share.CacheFileCount);
    public string ThumbsText => Share is null ? "—" : Format.Count(Share.ThumbnailUsage().Count);
    public string PathText => Share?.Config.LocalPath ?? "";
    public string ModeText => Share?.Config.Mode == ShareMode.AlwaysLocal ? App.S("R.ModeAlways") : App.S("R.ModeOnDemand");

    public string BudgetText => Share is null || Share.CacheMaxBytes == 0
        ? "—"
        : Format.Bytes(Share.CacheMaxBytes);

    public string LastTransferText => Share?.LastTransfer is { } t ? t.ToString("HH:mm:ss") : "—";

    // Rohwerte zum Sortieren. Die Textspalten zeigen "9,4 MB" und "944 MB".
    // Alphabetisch sortiert stuende der kleinere Wert hinten.
    public long ReceivedValue => Share?.BytesReceived ?? -1;
    public long SentValue => Share?.BytesSent ?? -1;
    public long SizeValue => Share?.IndexBytes ?? -1;
    public long LocalSizeValue => Share?.CacheUsedBytes ?? -1;
    public long FilesValue => Share?.IndexCount ?? -1;
    public long LocalFilesValue => Share?.CacheFileCount ?? -1;
    public long ThumbsValue => Share?.ThumbnailUsage().Count ?? -1;
    public long BudgetValue => Share?.CacheMaxBytes ?? -1;
    public DateTime LastTransferValue => Share?.LastTransfer ?? DateTime.MinValue;

    // -------------------------------------------------------------- Pflege

    public void Attach(ShareHost host, string label)
    {
        Share = host;
        Label = label;
        Refresh();
    }

    public void Refresh()
    {
        if (Editing) return;

        // Alles auf einmal melden. Die Zeile ist klein, und einzelne
        // Meldungen waeren mehr Aufwand als Ersparnis.
        foreach (var name in new[]
                 {
                     nameof(Name), nameof(StatusText), nameof(Ready), nameof(Accepted),
                     nameof(PeersText), nameof(PeerOnline), nameof(PeerText),
                     nameof(Copies), nameof(CopiesText), nameof(CopiesAtRisk), nameof(CopiesHint),
                     nameof(Busy), nameof(Indeterminate), nameof(Percent), nameof(ProgressText),
                     nameof(ReceivedText), nameof(SentText), nameof(SizeText), nameof(LocalSizeText),
                     nameof(FilesText), nameof(LocalFilesText), nameof(ThumbsText),
                     nameof(PathText), nameof(ModeText), nameof(BudgetText), nameof(LastTransferText),
                     nameof(ReceivedValue), nameof(SentValue), nameof(SizeValue), nameof(LocalSizeValue),
                     nameof(FilesValue), nameof(LocalFilesValue), nameof(ThumbsValue),
                     nameof(BudgetValue), nameof(LastTransferValue)
                 })
        {
            Notify(name);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
