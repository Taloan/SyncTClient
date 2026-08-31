using System.ComponentModel;
using System.Runtime.CompilerServices;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Eine Zeile der Freigabentabelle.
/// </summary>
/// <remarks>
/// Deckt beide Faelle ab: einen uebernommenen Ordner und einen, den die
/// Gegenstelle nur anbietet. Sie in einer Liste zu fuehren erspart die Frage,
/// wo ein angebotener Ordner denn hingehoert -- er steht da, wo er nach dem
/// Uebernehmen auch stehen wird, nur ohne Zahlen.
/// </remarks>
public sealed class ShareRow(PeerItem peer, string folderId, string label, ShareHost? share)
    : INotifyPropertyChanged
{
    public PeerItem Peer { get; } = peer;
    public string FolderId { get; } = folderId;
    public string Label { get; private set; } = label;
    public ShareHost? Share { get; private set; } = share;

    public bool Accepted => Share is not null;

    public string Name => string.IsNullOrWhiteSpace(Label) ? FolderId : Label;

    // ---------------------------------------------------------------- Zustand

    public string StatusText => Share?.State switch
    {
        ShareState.Bereit => Share.Phase == SyncPhase.Fertig ? "bereit" : PhaseText,
        ShareState.Wartet => PhaseText,
        ShareState.Pausiert => "angehalten",
        ShareState.Fehler => "Fehler",
        ShareState.Gestoppt => "gestoppt",
        _ => "nicht übernommen"
    };

    private string PhaseText => Share?.Phase switch
    {
        SyncPhase.Index => "Index wird gelesen",
        SyncPhase.Platzhalter => "Platzhalter werden angelegt",
        SyncPhase.Cache => "Cache wird abgeglichen",
        SyncPhase.Inhalte => "Inhalte werden geholt",
        _ => "wartet"
    };

    /// <summary>Haken, wenn alles steht -- wie bei Resilio die gruene Spalte.</summary>
    public bool Ready => Share is { State: ShareState.Bereit, Phase: SyncPhase.Fertig };

    // --------------------------------------------------------------- Knoten

    public bool PeerOnline => Peer.Host.State == PeerState.Verbunden;

    public string PeersText => Accepted ? (PeerOnline ? "1 von 1" : "0 von 1") : "—";

    public string PeerText => Peer.Display;

    // ------------------------------------------------------- Vollständige Kopien

    /// <summary>
    /// Wie viele erreichbare Knoten den Inhalt tragen -- wir selbst nicht
    /// mitgezählt.
    /// </summary>
    public int Copies => Share?.ReachableCopies ?? 0;

    public string CopiesText => Accepted ? Copies.ToString() : "—";

    /// <summary>
    /// Warnt, wenn die Platzhalter an einem seidenen Faden hängen.
    /// </summary>
    /// <remarks>
    /// Ein Platzhalter ist ein Versprechen auf eine Datei. Hält niemand mehr
    /// den Inhalt, ist das Versprechen wertlos -- und das sieht man dem
    /// Ordner nicht an, weil die Namen weiter dastehen.
    ///
    /// Die Zahl ist eine Untergrenze: über Knoten, mit denen wir gerade nicht
    /// verbunden sind, wissen wir nichts.
    /// </remarks>
    public bool CopiesAtRisk => Accepted && Copies < 1;

    public string CopiesHint => !Accepted
        ? "Noch nicht übernommen."
        : Copies == 0
            ? "Kein erreichbarer Knoten führt diesen Ordner — Platzhalter lassen sich " +
              "gerade nicht öffnen. Ob verdrängt werden darf, entscheidet weiterhin die " +
              "zuletzt empfangene Ankündigung, nicht die Erreichbarkeit."
            : $"{Copies} erreichbarer Knoten führt den Inhalt. Über nicht verbundene " +
              "Knoten wissen wir nichts — die Zahl ist eine Untergrenze.";

    // ------------------------------------------------------------ Fortschritt

    /// <summary>
    /// Sichtbar, solange der Abgleich laeuft.
    /// </summary>
    /// <remarks>
    /// Ein Balken, der dauerhaft auf 100 steht, sagt nichts und lenkt vom
    /// einen ab, der tatsaechlich laeuft.
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
    public string SizeText => Share is null ? "—" : Format.Bytes(Share.IndexBytes);
    public string LocalSizeText => Share is null ? "—" : Format.Bytes(Share.CacheUsedBytes);
    public string FilesText => Share is null ? "—" : Format.Count(Share.IndexCount);
    public string LocalFilesText => Share is null ? "—" : Format.Count(Share.CacheFileCount);
    public string ThumbsText => Share is null ? "—" : Format.Count(Share.ThumbnailUsage().Count);
    public string PathText => Share?.Config.LocalPath ?? "";
    public string ModeText => Share?.Config.Mode == ShareMode.AlwaysLocal ? "vollständig lokal" : "bei Bedarf";

    public string BudgetText => Share is null || Share.CacheMaxBytes == 0
        ? "—"
        : Format.Bytes(Share.CacheMaxBytes);

    public string LastTransferText => Share?.LastTransfer is { } t ? t.ToString("HH:mm:ss") : "—";

    // Rohwerte zum Sortieren. Die Textspalten zeigen "9,4 MB" und "944 MB" --
    // alphabetisch stuende das kleinere davon hinten.
    public long ReceivedValue => Share?.BytesReceived ?? -1;
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
        // Alles auf einmal: die Zeile ist klein, und einzelne Meldungen
        // waeren mehr Buchhaltung als Ersparnis.
        foreach (var name in new[]
                 {
                     nameof(Name), nameof(StatusText), nameof(Ready), nameof(Accepted),
                     nameof(PeersText), nameof(PeerOnline), nameof(PeerText),
                     nameof(Copies), nameof(CopiesText), nameof(CopiesAtRisk), nameof(CopiesHint),
                     nameof(Busy), nameof(Indeterminate), nameof(Percent), nameof(ProgressText),
                     nameof(ReceivedText), nameof(SizeText), nameof(LocalSizeText),
                     nameof(FilesText), nameof(LocalFilesText), nameof(ThumbsText),
                     nameof(PathText), nameof(ModeText), nameof(BudgetText), nameof(LastTransferText),
                     nameof(ReceivedValue), nameof(SizeValue), nameof(LocalSizeValue),
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
