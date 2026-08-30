using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SyncTClient.Gui;

/// <summary>
/// Ein Knoten im Auswahlbaum. Das Haekchen kennt drei Zustaende: ganz
/// ausgewaehlt, gar nicht, oder teilweise -- letzteres ergibt sich aus den
/// Kindern und laesst sich nicht direkt anklicken.
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

    public long TotalBytes { get; set; }

    public int FileCount { get; set; }

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
        set => SetChecked(value ?? false, fromUser: true);
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
        // sobald sich seine Kinder uneinig sind.
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

    /// <summary>Setzt den Zustand ohne Weitergabe -- fuers Aufbauen aus der Konfiguration.</summary>
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
        IEnumerable<(string Name, long Size, bool IsDirectory)> entries)
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
                // Groesse und Anzahl bis zur Wurzel hochzaehlen, damit jeder
                // Knoten zeigt, was seine Auswahl kostet.
                for (var walker = node; walker is not null; walker = walker.Parent)
                {
                    walker.FileCount++;
                    walker.TotalBytes += entry.Size;
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
