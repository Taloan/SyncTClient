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

    /// <summary>Laeuft dieser Ordner gerade?</summary>
    public bool Accepted => Share is not null;

    /// <summary>
    /// Steht dieser Ordner in der Konfiguration?
    /// </summary>
    /// <remarks>
    /// Nicht dasselbe wie <see cref="Accepted"/>. Angehalten laeuft keine
    /// Freigabe, und ohne Verbindung entsteht keine -- uebernommen ist sie
    /// trotzdem. Wer sie in diesem Zustand trennen will, meint die
    /// Konfiguration, und die ist da.
    /// </remarks>
    public bool Configured { get; set; }

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

    /// <summary>Ob das ganze Programm angehalten ist.</summary>
    /// <remarks>
    /// Angehalten steht ueber allem anderen. Ohne diese Angabe meldete die
    /// Zeile "nicht verbunden" -- richtig, aber irrefuehrend: es klingt nach
    /// einer Stoerung, dabei ist es eine Entscheidung, und die Ursache stand
    /// nur am Knopf oben links.
    /// </remarks>
    public bool AppPaused { get; set; }

    public string StatusText => AppPaused ? App.S("R.Paused") : Share?.State switch
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
        SyncPhase.Abgleich => App.S("R.PhaseSyncing"),
        _ => App.S("R.PhaseWaiting")
    };

    /// <summary>Warndreieck, solange die Freigabe nicht anlaufen konnte.</summary>
    public bool HasError => Share?.State == ShareState.Fehler;

    /// <summary>
    /// Was in der Statusspalte beim Ueberfahren erscheint.
    /// </summary>
    /// <remarks>
    /// Der Wortlaut des Fehlers. Er nennt bereits, was zu tun ist -- eine
    /// zweite, allgemeine Zeile daneben ("ein Fehler ist aufgetreten") saehe
    /// nach Auskunft aus und waere keine.
    /// </remarks>
    public string? ErrorTip => HasError ? Share?.Fehler : null;

    /// <summary>Haken, wenn der Abgleich abgeschlossen ist. Entspricht der gruenen Spalte in Resilio.</summary>
    public bool Ready => Share is { State: ShareState.Bereit, Phase: SyncPhase.Fertig };

    // --------------------------------------------------------------- Knoten

    /// <summary>
    /// Alle Gegenstellen, die an diesem Ordner teilnehmen.
    /// </summary>
    /// <remarks>
    /// <see cref="Peer"/> ist die erste davon. Ueber sie laufen die Aktionen,
    /// die eine Gegenstelle brauchen -- uebernehmen und Bindung loesen. Der
    /// Ordner selbst gehoert keiner von ihnen.
    /// </remarks>
    public List<PeerItem> Peers { get; } = [peer];

    public void AddPeer(PeerItem weitere)
    {
        if (Peers.Any(p => p.Config.DeviceId == weitere.Config.DeviceId)) return;

        Peers.Add(weitere);
        Notify(nameof(PeersText));
        Notify(nameof(PeerOnline));
    }

    public bool PeerOnline => Peers.Any(p => p.Host.State == PeerState.Verbunden);

    /// <summary>Wie viele der beteiligten Gegenstellen gerade erreichbar sind.</summary>
    public string PeersText => Accepted
        ? $"{Peers.Count(p => p.Host.State == PeerState.Verbunden)} von {Peers.Count}"
        : "—";

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
    /// Sichtbar, solange ein Durchlauf mit bekanntem Umfang laeuft.
    /// </summary>
    /// <remarks>
    /// Ein Balken, der dauerhaft auf 100 steht, traegt keine Information und
    /// lenkt von den Balken ab, deren Abgleich tatsaechlich laeuft.
    ///
    /// Waehrend des laufenden Abgleichs nur, wenn wirklich etwas aussteht.
    /// Ein Balken auf hundert Prozent, waehrend die Zeile "gleicht ab" sagt,
    /// widerspricht sich selbst -- und ein voller Balken ist kein
    /// Fortschritt, sondern eine Behauptung.
    /// </remarks>
    public bool Busy => Share is not null
                        && Share.Phase != SyncPhase.Fertig
                        && Share.Phase != SyncPhase.Ruht
                        && (Share.Phase != SyncPhase.Abgleich
                            || Share.Outstanding > 0
                            || Share.Transferring);

    /// <summary>Laeuft gerade eine Uebertragung, in welcher Richtung auch immer?</summary>
    private bool Laeuft => Share is not null
                           && Share.Phase == SyncPhase.Abgleich
                           && Share.Transferring;

    /// <summary>Unbestimmt, solange die Gesamtzahl unbekannt ist.</summary>
    public bool Indeterminate => Busy && !Laeuft && Share!.PhaseTotal == 0;

    public double Percent
    {
        get
        {
            if (Share is null) return 0;

            // Waehrend eine Uebertragung laeuft, zaehlt sie -- nicht der
            // Rueckstand. Der steht dann oft schon auf null, weil die
            // Gegenstelle die Ankuendigung bereits kennt.
            if (Laeuft)
            {
                var (done, total) = Share.ActiveProgress;
                return total <= 0 ? 0 : Math.Clamp(100.0 * done / total, 0, 100);
            }

            return Share.PhaseTotal == 0
                ? 0
                : Math.Clamp(100.0 * Share.PhaseDone / Share.PhaseTotal, 0, 100);
        }
    }

    public string ProgressText
    {
        get
        {
            if (Share is null) return "";

            // Angekuendigt und noch nicht abgerufen ist kein Rueckstand, aber
            // auch nicht abgeglichen: die Gegenstelle fuehrt diese Dateien
            // dann selbst als offen. Stuende hier "abgeglichen", sagten beide
            // Seiten Verschiedenes ueber denselben Ordner.
            if (Share.Outgoing > 0)
                return App.S("R.Outgoing",
                    Format.Count(Share.Outgoing), Format.Bytes(Share.OutgoingBytes));

            // Die Spalte daneben nennt den Zustand bereits. Ihn hier zu
            // wiederholen fuellt Platz und sagt nichts.
            if (!Busy)
                return Share.Phase == SyncPhase.Abgleich ? ""
                    : Share.State == ShareState.Bereit ? App.S("R.Synced")
                    : "";

            // Laeuft gerade etwas, nennt die Zeile das Laufende: den Anteil
            // und was davon noch fehlt. Der Rueckstand taugt dafuer nicht --
            // er steht auf null, sobald die Indizes uebereinstimmen, und das
            // ist lange vor dem letzten Block der Fall.
            if (Laeuft)
            {
                var (done, total) = Share.ActiveProgress;

                // Abgerundet. Gerundet stuenden bei 99,6 Prozent hundert da,
                // waehrend daneben noch offene Bytes genannt sind -- die Zeile
                // widerspraeche sich selbst.
                var anteil = Math.Min(99, Math.Floor(Percent));

                return App.S("R.SyncPercent", $"{anteil:0}", Format.Bytes(total - done));
            }

            // Waehrend des Abgleichs sagt der Anteil mehr als die Stueckzahl.
            // Was noch aussteht, steht in Bytes daneben: 444 Dateien koennen
            // vier Minuten sein oder vier Stunden.
            if (Share.Phase == SyncPhase.Abgleich)
            {
                // Ebenfalls abgerundet: hundert Prozent neben offenen Bytes
                // ist ein Widerspruch in derselben Zeile.
                // Auch bei null offenen Bytes: was aussteht, kann eine
                // leere Datei sein. Hundert Prozent neben "gleicht ab" ist
                // derselbe Widerspruch, nur ohne Bytes.
                var anteil = Share.Outstanding > 0 ? Math.Min(99, Math.Floor(Percent)) : Percent;
                return App.S("R.SyncPercent", $"{anteil:0}", Format.Bytes(Share.OutstandingBytes));
            }

            return Share.PhaseTotal == 0
                ? $"{Share.PhaseDone:N0}"
                : $"{Share.PhaseDone:N0} von {Share.PhaseTotal:N0}";
        }
    }

    // ------------------------------------------------------------- Zahlen

    public string ReceivedText => Share is null ? "—" : Format.Bytes(Share.BytesReceived);
    public string SentText => Share is null ? "—" : Format.Bytes(Share.BytesSent);
    // Alle Zahlen ueber den Umfang stammen aus demselben Durchgang: Dateien,
    // keine Verzeichnisse, und nur was zur Auswahl gehoert. Der rohe Index
    // zaehlt anders, und zwei Zaehlweisen im selben Fenster laden zu einem
    // Vergleich ein, der nicht aufgeht.
    public string SizeText => Share is null ? "—" : Format.Bytes(Share.IndexTotalBytes);
    public string LocalSizeText => Share is null ? "—" : Format.Bytes(Share.CacheUsedBytes);
    public string FilesText => Share is null ? "—" : Format.Count(Share.IndexFiles);
    public string LocalFilesText => Share is null ? "—" : Format.Count(Share.CacheFileCount);
    public string ThumbsText => Share is null ? "—" : Format.Count(Share.ThumbnailUsage().Count);
    public string PathText => Share?.Config.LocalPath ?? "";
    public string ModeText => Share?.Config.Mode == ShareMode.AlwaysLocal ? App.S("R.ModeAlways") : App.S("R.ModeOnDemand");

    public string LimitText => Share is null || Share.CacheMaxBytes == 0
        ? "—"
        : Format.Bytes(Share.CacheMaxBytes);

    public string LastTransferText => Share?.LastTransfer is { } t ? t.ToString("HH:mm:ss") : "—";

    // ------------------------------------------------------- Aufgeklappt

    /// <summary>
    /// Was die Gegenstelle fuehrt: Dateien und Groesse.
    /// </summary>
    /// <remarks>
    /// Der Gegenstand des Vergleichs. Ohne diese Zeile liesse sich der
    /// Rueckstand darunter nicht einordnen -- 444 offene Dateien sind bei
    /// 500 etwas anderes als bei 150.000.
    ///
    /// Genommen wird die Zahl aus demselben Durchgang, aus dem auch die
    /// beiden Zeilen darunter stammen. Der Index enthaelt auch Verzeichnisse
    /// und Ausgeschlossenes; eine Zeile, die anders zaehlt als die daneben,
    /// laedt zu einem Vergleich ein, der nicht aufgeht.
    /// </remarks>
    public string GlobalText => Share is null || Share.IndexFiles == 0
        ? "—"
        : App.S("R.CountAndSize", Format.Count(Share.IndexFiles), Format.Bytes(Share.IndexTotalBytes));

    /// <summary>Was im Ordner steht, Platzhalter eingerechnet.</summary>
    public string LocalText => Share is null
        ? "—"
        : App.S("R.CountAndSize", Format.Count(Share.LocalFiles), Format.Bytes(Share.LocalBytes));

    /// <summary>Wovon der Inhalt hier liegt und Platz belegt.</summary>
    public string ContentText => Share is null
        ? "—"
        : App.S("R.CountAndSize", Format.Count(Share.CacheFileCount), Format.Bytes(Share.CacheUsedBytes));

    /// <summary>Ob die Zeile aufgeklappt ist.</summary>
    private bool _expanded;

    public bool Expanded
    {
        get => _expanded;
        set { _expanded = value; Notify(nameof(Expanded)); }
    }

    public string OutstandingText => Share is null
        ? "—"
        : Share.Outstanding == 0
            ? App.S("R.NothingOutstanding")
            : App.S("R.CountAndSize", Format.Count(Share.Outstanding), Format.Bytes(Share.OutstandingBytes));

    /// <summary>Was die Gegenstelle nennt, aber selbst nicht haelt.</summary>
    public string AwaitingText => Share is null || Share.Awaiting == 0
        ? App.S("R.NothingOutstanding")
        : App.S("R.CountAndSize", Format.Count(Share.Awaiting), Format.Bytes(Share.AwaitingBytes));

    public string LastScanText => Share is { LastScan.Year: > 1 }
        ? Share.LastScan.ToString("HH:mm:ss")
        : "—";

    public string FolderIdText => Share?.FolderId ?? FolderId;

    public string ThumbDetailText => Share is null
        ? "—"
        : App.S("R.CountAndSize",
            Format.Count(Share.ThumbnailUsage().Count),
            Format.Bytes(Share.ThumbnailUsage().Bytes));

    // Rohwerte zum Sortieren. Die Textspalten zeigen "9,4 MB" und "944 MB".
    // Alphabetisch sortiert stuende der kleinere Wert hinten.
    public long ReceivedValue => Share?.BytesReceived ?? -1;
    public long SentValue => Share?.BytesSent ?? -1;
    public long SizeValue => Share?.IndexTotalBytes ?? -1;
    public long LocalSizeValue => Share?.CacheUsedBytes ?? -1;
    public long FilesValue => Share?.IndexFiles ?? -1;
    public long LocalFilesValue => Share?.CacheFileCount ?? -1;
    public long ThumbsValue => Share?.ThumbnailUsage().Count ?? -1;
    public long LimitValue => Share?.CacheMaxBytes ?? -1;
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
                     nameof(HasError), nameof(ErrorTip),
                     nameof(Configured),
                     nameof(PeersText), nameof(PeerOnline),
                     nameof(Copies), nameof(CopiesText), nameof(CopiesAtRisk), nameof(CopiesHint),
                     nameof(Busy), nameof(Indeterminate), nameof(Percent), nameof(ProgressText),
                     nameof(ReceivedText), nameof(SentText), nameof(SizeText), nameof(LocalSizeText),
                     nameof(FilesText), nameof(LocalFilesText), nameof(ThumbsText),
                     nameof(PathText), nameof(ModeText), nameof(LimitText), nameof(LastTransferText),
                     nameof(ReceivedValue), nameof(SentValue), nameof(SizeValue), nameof(LocalSizeValue),
                     nameof(FilesValue), nameof(LocalFilesValue), nameof(ThumbsValue),
                     nameof(LimitValue), nameof(LastTransferValue),
                     nameof(GlobalText), nameof(LocalText), nameof(OutstandingText),
                     nameof(LastScanText), nameof(FolderIdText), nameof(ThumbDetailText),
                     nameof(ContentText), nameof(AwaitingText)
                 })
        {
            Notify(name);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
