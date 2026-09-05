using System.Collections.ObjectModel;
using System.ComponentModel;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Ein Knoten im Baum eines Datenträgers: eine Freigabe, ein Verzeichnis oder
/// eine einzelne Datei.
/// </summary>
/// <remarks>
/// Ein eigenes Modell und nicht <see cref="FolderNode"/>. Jener fasst die
/// Dateien eines Verzeichnisses zu einem Sammelknoten zusammen — für die
/// Zweigauswahl einer Freigabe ist das richtig, hier nicht: gefragt ist ein
/// Kästchen je Datei.
///
/// Das Kästchen bedeutet „liegt lokal". Angekreuzt heißt: der Inhalt soll hier
/// sein und bleiben. Nicht angekreuzt heißt: Platzhalter — der Inhalt zählt
/// zum Cache und wird verdrängt, sobald der Platz gebraucht wird.
/// </remarks>
public sealed class CacheNode(string name, string path, CacheNode? parent) : INotifyPropertyChanged
{
    public string Name { get; } = name;

    /// <summary>Der Pfad ab der Wurzel der Freigabe, mit "/" getrennt. Leer bei der Freigabe selbst.</summary>
    public string Path { get; } = path;

    public CacheNode? Parent { get; } = parent;

    public ObservableCollection<CacheNode> Children { get; } = [];

    /// <summary>Die Freigabe, zu der dieser Knoten gehört.</summary>
    public ShareHost? Host { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>Ob der Inhalt gerade hier liegt. Nur bei Dateien belegt.</summary>
    public bool Hydriert { get; init; }

    public long Bytes { get; set; }

    public int Dateien { get; set; }

    /// <summary>Wie viele davon ihren Inhalt gerade hier halten.</summary>
    public int Gefuellt { get; set; }

    private bool _expanded;

    public bool IsExpanded
    {
        get => _expanded;
        set { _expanded = value; Melden(nameof(IsExpanded)); }
    }

    /// <summary>
    /// Der Zustand, mit dem der Baum aufgebaut wurde.
    /// </summary>
    /// <remarks>
    /// Nur die Änderung wird ausgeführt. Ohne diesen Vergleich stünde beim
    /// Bestätigen jede Datei des Datenträgers auf der Liste, und aus einem
    /// Kästchen würden hunderttausend Handgriffe.
    /// </remarks>
    public bool? Anfangs { get; private set; }

    private bool? _checked;

    /// <summary>Liegt der Inhalt lokal? Bei Verzeichnissen unbestimmt, wenn gemischt.</summary>
    public bool? IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;

            _checked = value;
            Melden(nameof(IsChecked));

            // Nach unten durchreichen, nach oben nachrechnen. Ein Verzeichnis
            // anzukreuzen meint seinen ganzen Zweig.
            if (value is { } fest)
                foreach (var kind in Children)
                    kind.IsChecked = fest;

            Parent?.Nachrechnen();
        }
    }

    public string Summary => IsDirectory || Children.Count > 0
        ? Dateien == 0
            ? ""
            : $"{Dateien} Dateien · {Format.Bytes(Bytes)} · {Gefuellt} lokal"
        : Format.Bytes(Bytes) + (Hydriert ? " · lokal" : " · Platzhalter");

    /// <summary>Setzt den Anfangszustand, ohne ihn als Änderung zu werten.</summary>
    public void Festhalten()
    {
        foreach (var kind in Children) kind.Festhalten();

        if (Children.Count > 0)
        {
            var alle = Children.All(k => k.IsChecked == true);
            var keiner = Children.All(k => k.IsChecked == false);
            _checked = alle ? true : keiner ? false : null;
        }

        Anfangs = _checked;
        Melden(nameof(IsChecked));
    }

    private void Nachrechnen()
    {
        var alle = Children.All(k => k.IsChecked == true);
        var keiner = Children.All(k => k.IsChecked == false);

        var neu = alle ? true : keiner ? (bool?)false : null;
        if (_checked == neu) return;

        _checked = neu;
        Melden(nameof(IsChecked));
        Parent?.Nachrechnen();
    }

    /// <summary>
    /// Sammelt, was sich geändert hat -- je Freigabe und je Richtung.
    /// </summary>
    /// <remarks>
    /// Von oben nach unten und beim ersten geänderten Zweig abbrechen: ein
    /// angekreuztes Verzeichnis ist ein Auftrag, kein Verzeichnis samt jeder
    /// Datei darin. <see cref="ShareHost.SetLocal"/> löst Verzeichnisse selbst
    /// auf.
    /// </remarks>
    public void Sammeln(List<(ShareHost Host, string Path, bool Lokal)> auftraege)
    {
        if (_checked is { } fest && fest != Anfangs && Host is { } host && Path.Length > 0)
        {
            auftraege.Add((host, Path, fest));
            return;
        }

        foreach (var kind in Children) kind.Sammeln(auftraege);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Melden(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ------------------------------------------------------------ Aufbau

    /// <summary>
    /// Baut den Baum einer Freigabe aus ihren Cache-Einträgen.
    /// </summary>
    /// <remarks>
    /// Verzeichnisse entstehen aus den Pfaden der Dateien und nicht aus den
    /// Verzeichniseinträgen des Index: ein Verzeichnis, in dem nichts zum
    /// Cache zählt, gehört nicht in diesen Baum, steht aber im Index.
    /// </remarks>
    public static CacheNode Bauen(ShareHost host, string titel, IEnumerable<ShareHost.CacheEintrag> eintraege)
    {
        var wurzel = new CacheNode(titel, "", null) { Host = host, IsDirectory = true, IsExpanded = true };
        var knoten = new Dictionary<string, CacheNode>(StringComparer.OrdinalIgnoreCase) { [""] = wurzel };

        CacheNode Verzeichnis(string pfad)
        {
            if (knoten.TryGetValue(pfad, out var da)) return da;

            var schnitt = pfad.LastIndexOf('/');
            var eltern = Verzeichnis(schnitt < 0 ? "" : pfad[..schnitt]);
            var name = schnitt < 0 ? pfad : pfad[(schnitt + 1)..];

            var neu = new CacheNode(name, pfad, eltern) { Host = host, IsDirectory = true };
            eltern.Children.Add(neu);
            knoten[pfad] = neu;
            return neu;
        }

        foreach (var eintrag in eintraege
                     .Where(e => !e.IsDirectory)
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var schnitt = eintrag.Name.LastIndexOf('/');
            var eltern = Verzeichnis(schnitt < 0 ? "" : eintrag.Name[..schnitt]);

            var datei = new CacheNode(
                schnitt < 0 ? eintrag.Name : eintrag.Name[(schnitt + 1)..],
                eintrag.Name,
                eltern)
            {
                Host = host,
                Bytes = eintrag.Size,
                Hydriert = eintrag.Hydriert,
                IsChecked = eintrag.Hydriert
            };

            eltern.Children.Add(datei);

            for (var oben = eltern; oben is not null; oben = oben.Parent)
            {
                oben.Dateien++;
                oben.Bytes += eintrag.Size;
                if (eintrag.Hydriert) oben.Gefuellt++;
            }
        }

        wurzel.Festhalten();
        return wurzel;
    }
}
