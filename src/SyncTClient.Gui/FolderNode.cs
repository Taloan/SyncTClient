using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SyncTClient.Gui;

/// <summary>
/// Ein Knoten im Auswahlbaum. Das Haekchen hat drei Zustaende: ganz
/// ausgewaehlt, gar nicht ausgewaehlt und teilweise ausgewaehlt. Der dritte
/// Zustand ergibt sich aus den Kindern und laesst sich nicht direkt anklicken.
/// </summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private bool? _isChecked = true;
    private bool _isExpanded;
    private bool _updating;

    public FolderNode(string name, string path, FolderNode? parent)
    {
        Name = name;
        Path = path;
        Parent = parent;
    }

    public string Name { get; }

    /// <summary>Pfad relativ zum Share, mit / als Trenner. Leer beim Wurzelknoten.</summary>
    public string Path { get; }

    public FolderNode? Parent { get; }

    public ObservableCollection<FolderNode> Children { get; } = [];

    /// <summary>
    /// Wahr fuer den Knoten, der die losen Dateien eines Verzeichnisses
    /// vertritt.
    /// </summary>
    /// <remarks>
    /// Ohne ihn liesse sich ein Verzeichnis, von dem ein Unterordner
    /// abgewaehlt ist, nicht mehr vollstaendig beschreiben: es ist dann
    /// teilweise ausgewaehlt, und in der Auswahl stehen nur seine
    /// ausgewaehlten Unterordner. Seine eigenen Dateien kaemen darin nicht
    /// vor und fielen heraus, ohne dass jemand sie abgewaehlt haette.
    ///
    /// OneDrive loest es genauso: "Dateien ausserhalb von Ordnern" ganz oben
    /// und "Dateien in X" in jedem Verzeichnis darunter.
    /// </remarks>
    public bool IsFileBucket { get; init; }

    public long TotalBytes { get; set; }

    public int FileCount { get; set; }

    /// <summary>
    /// Wie viele Dateien unter diesem Knoten die Platzhalter-Schwelle noch
    /// nicht erreicht haben.
    /// </summary>
    /// <remarks>
    /// Abwaehlen heisst entfernen. Das ist nur dann kein Verlust, wenn die
    /// Gegenstelle jede Datei des Zweiges vollstaendig fuehrt. Solange auch
    /// nur eine fehlt, bleibt das Kaestchen gesetzt.
    /// </remarks>
    public int Blocking { get; set; }

    public bool Removable => Blocking == 0;

    /// <summary>Wird gerufen, wenn ein Abwaehlen abgelehnt wurde.</summary>
    public Action<FolderNode>? Refused { get; set; }

    public string Summary => FileCount == 0
        ? ""
        : $"{FileCount} Dateien, {TotalBytes / (1024.0 * 1024.0):0.#} MB";

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Notify(); }
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (value == false && !Removable)
            {
                Refused?.Invoke(this);

                // Das Kaestchen hat sich beim Klick schon umgestellt. Ohne
                // diese Meldung bliebe es leer, waehrend die Auswahl
                // unveraendert ist.
                Notify(nameof(IsChecked));
                return;
            }

            SetChecked(value ?? false, fromUser: true);
        }
    }

    private void SetChecked(bool? value, bool fromUser)
    {
        if (_isChecked == value) return;

        _isChecked = value;
        Notify(nameof(IsChecked));

        if (_updating) return;

        // Nach unten durchreichen: was der Nutzer hier setzt, gilt fuer den
        // ganzen Teilbaum.
        if (fromUser && value is bool definite)
        {
            foreach (var child in Children)
            {
                child._updating = false;
                child.SetChecked(definite, fromUser: true);
            }
        }

        // Nach oben neu berechnen: der Elternknoten ist teilweise ausgewaehlt,
        // sobald seine Kinder unterschiedliche Zustaende haben.
        Parent?.RefreshFromChildren();
    }

    private void RefreshFromChildren()
    {
        if (Children.Count == 0) return;

        var allChecked = Children.All(c => c._isChecked == true);
        var noneChecked = Children.All(c => c._isChecked == false);

        _updating = true;
        SetChecked(allChecked ? true : noneChecked ? false : null, fromUser: false);
        _updating = false;
    }

    /// <summary>Setzt den Zustand ohne Weitergabe an Eltern und Kinder. Fuer den Aufbau aus der Konfiguration.</summary>
    public void InitializeChecked(bool value)
    {
        _isChecked = value;
        Notify(nameof(IsChecked));
    }

    /// <summary>Berechnet alle Elternzustaende neu, von unten nach oben.</summary>
    public void RecomputeUpwards()
    {
        foreach (var child in Children) child.RecomputeUpwards();
        RefreshFromChildren();
    }

    // ------------------------------------------------------------ Auswahl lesen

    /// <summary>
    /// Die kuerzeste Liste von Pfaden, die die Auswahl beschreibt: ein
    /// vollstaendig ausgewaehlter Zweig wird durch seinen obersten Knoten
    /// vertreten, seine Kinder werden nicht einzeln aufgezaehlt.
    /// </summary>
    public static List<string> CollectIncluded(FolderNode root)
    {
        // Alles ausgewaehlt heisst in der Konfiguration: leere Liste.
        if (root.IsChecked == true) return [];

        var result = new List<string>();
        Walk(root);
        return result;

        void Walk(FolderNode node)
        {
            foreach (var child in node.Children)
            {
                if (child.IsChecked == true) result.Add(child.Path);
                else if (child.IsChecked is null) Walk(child);
            }
        }
    }

    // ------------------------------------------------------------ Aufbau

    /// <summary>Baut den Verzeichnisbaum aus den Eintraegen des Index.</summary>
    public static FolderNode Build(
        IEnumerable<(string Name, long Size, bool IsDirectory, bool HasContent)> entries)
    {
        var root = new FolderNode("(alles)", "", null) { IsExpanded = true };
        var lookup = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = root
        };

        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var directory = entry.IsDirectory
                ? entry.Name
                : ParentOf(entry.Name);

            var node = EnsureNode(directory);

            if (!entry.IsDirectory)
            {
                // Die Datei gehoert in den Sammelknoten ihres Verzeichnisses.
                // Groesse und Anzahl zaehlen von dort bis zur Wurzel hoch,
                // damit jeder Knoten den Umfang seiner Auswahl anzeigt.
                for (var walker = Bucket(node); walker is not null; walker = walker.Parent)
                {
                    walker.FileCount++;
                    walker.TotalBytes += entry.Size;
                    if (!entry.HasContent) walker.Blocking++;
                }
            }
        }

        return root;

        FolderNode EnsureNode(string path)
        {
            if (lookup.TryGetValue(path, out var existing)) return existing;

            var parent = EnsureNode(ParentOf(path));
            var node = new FolderNode(LeafOf(path), path, parent);
            parent.Children.Add(node);
            lookup[path] = node;
            return node;
        }
    }

    /// <summary>Der Sammelknoten eines Verzeichnisses, angelegt beim ersten Bedarf.</summary>
    private static FolderNode Bucket(FolderNode node)
    {
        var vorhanden = node.Children.FirstOrDefault(c => c.IsFileBucket);
        if (vorhanden is not null) return vorhanden;

        var name = node.Parent is null
            ? App.S("S2.LooseRoot")
            : App.S("S2.LooseIn", node.Name);

        var bucket = new FolderNode(name, node.Path.Length == 0 ? "*" : node.Path + "/*", node)
        {
            IsFileBucket = true
        };

        // Ganz oben, wie bei OneDrive. Die Dateien eines Verzeichnisses stehen
        // vor seinen Unterverzeichnissen.
        node.Children.Insert(0, bucket);
        return bucket;
    }

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static string LeafOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    // ------------------------------------------------------------ Bindung

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
