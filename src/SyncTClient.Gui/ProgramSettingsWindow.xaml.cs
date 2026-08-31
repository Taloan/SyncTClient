using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SyncTClient.Mount;
using SyncTClient.Vfs;

namespace SyncTClient.Gui;

/// <summary>
/// Was fuer das ganze Programm gilt. Diese Einstellungen haengen an keiner
/// Freigabe. Sonst waeren sie fuer jemanden, der noch keine Freigabe hat,
/// nicht erreichbar.
/// </summary>
public partial class ProgramSettingsWindow : Window
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly AppConfig _config;
    private readonly string _configDirectory;
    private readonly string _home;
    private readonly Func<Task<string>> _clearCache;

    /// <summary>Waehrend des Fuellens sollen die Felder nichts ausloesen.</summary>
    private bool _loading;

    /// <summary>Das Datenverzeichnis zeigt auf einen anderen Ort. Das gilt erst beim naechsten Start.</summary>
    public bool HomeChanged { get; private set; }

    private readonly Func<IReadOnlyList<VolumeUsage>> _volumes;
    private readonly Func<string, Task<string>> _release;
    private readonly Func<(int Files, long Bytes)> _thumbUsage;

    private readonly ObservableCollection<VolumeRow> _rows = [];

    /// <summary>Eine Zeile je Datentraeger, so wie sie im Fenster steht.</summary>
    /// <remarks>
    /// Die beiden Grenzen stehen hier als Text und nicht als Zahl. Waehrend
    /// jemand tippt, ist das Feld zwischendurch leer oder unvollstaendig; eine
    /// Zahl muesste dann raten, was gemeint ist. Geprueft wird beim Speichern.
    /// </remarks>
    private sealed class VolumeRow
    {
        public string Text { get; init; } = "";
        public string ButtonText { get; init; } = "";
        public string Root { get; init; } = "";
        public bool CanRelease { get; init; }
        public string MaxGb { get; set; } = "";
        public string MinFreeGb { get; set; } = "";
    }

    /// <param name="volumes">Was auf welchem Datentraeger liegt.</param>
    /// <param name="release">Gibt einen Datentraeger frei und meldet das Ergebnis.</param>
    /// <param name="thumbUsage">Anzahl und Groesse der Vorschaubilder.</param>
    public ProgramSettingsWindow(
        AppConfig config, string configDirectory,
        Func<IReadOnlyList<VolumeUsage>> volumes,
        Func<string, Task<string>> release,
        Func<(int Files, long Bytes)> thumbUsage,
        Func<Task<string>> clearThumbnails)
    {
        InitializeComponent();

        _config = config;
        _configDirectory = configDirectory;
        _volumes = volumes;
        _release = release;
        _thumbUsage = thumbUsage;
        _clearCache = clearThumbnails;

        VolumeList.ItemsSource = _rows;

        // In der Datei darf der Pfad relativ stehen. Angezeigt wird er
        // ausgeschrieben, sonst bleibt unklar, worauf er sich bezieht.
        _home = Path.GetFullPath(Path.Combine(configDirectory, config.HomeDirectory));

        _loading = true;

        HomeBox.Text = _home;
        CacheBudgetBox.Text = Math.Max(1, config.CacheMaxBytes / Gigabyte).ToString();
        MinimumFreeBox.Text = (config.MinimumFreeBytes / Gigabyte).ToString();
        LanguageBox.SelectedIndex = config.Language switch { "de" => 1, "en" => 2, _ => 0 };
        ThemeBox.SelectedIndex = config.Theme switch { "Hell" => 1, "Dunkel" => 2, _ => 0 };

        ThresholdBox.Text = Math.Max(1, config.MinimumCopies).ToString();
        ThumbsBox.IsChecked = config.GenerateThumbnails;
        ShowUsage();
        ParallelismBox.Text = config.Parallelism.ToString();
        ListenBox.IsChecked = config.Listen;
        ListenPortBox.Text = config.ListenPort.ToString();
        LocalDiscoveryBox.IsChecked = config.LocalDiscovery;
        AnnounceBox.IsChecked = config.Announce;
        DiscoveryBox.IsChecked = config.Discovery;
        DiscoveryServerBox.Text = string.Join(Environment.NewLine, config.DiscoveryServers);
        RelaysBox.IsChecked = config.Relays;

        _loading = false;
    }

    /// <summary>
    /// Zeigt, wieviel auf dem Laufwerk des Cache-Verzeichnisses frei ist. Das
    /// ist die Zahl, mit der das Mindestmass verglichen wird.
    /// </summary>

    /// <summary>
    /// Sprache und Farbschema wirken sofort. So ist die Auswahl unmittelbar zu
    /// sehen und muss nicht erwartet werden.
    /// </summary>
    private void OnLookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        App.ApplyLanguage(LanguageBox.SelectedIndex switch { 1 => "de", 2 => "en", _ => null });
        App.ApplyTheme(ThemeBox.SelectedIndex switch { 1 => "Hell", 2 => "Dunkel", _ => null });
    }

    private void OnBrowseHome(object sender, RoutedEventArgs e) => Browse(HomeBox);


    private void Browse(TextBox box)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };

        // Ein Startpfad, den es nicht gibt, oeffnet den Dialog an unbestimmter Stelle.
        if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;

        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }

    /// <summary>
    /// Leert den Cache auf Zuruf. Das Verzeichnis bleibt bestehen, denn es ist
    /// zugleich der Platz der Freigaben. Nur der Inhalt wird wieder zum
    /// Platzhalter.
    /// </summary>
    /// <summary>
    /// Fuellt die Zeilen je Datentraeger und die Zeile der Vorschaubilder.
    /// </summary>
    /// <remarks>
    /// Neben dem Belegten steht, wie viel davon sich ueberhaupt freigeben
    /// laesst. Ohne diese zweite Zahl sieht ein Knopf, der nichts bewirkt,
    /// wie ein Fehler aus -- dabei liegt es meist daran, dass die
    /// Platzhalter-Schwelle noch nicht erreicht ist.
    /// </remarks>
    private void ShowUsage()
    {
        _rows.Clear();

        foreach (var volume in _volumes())
        {
            var frei = volume.FreeBytes < 0 ? App.S("G.FreeUnknown") : Format.Bytes(volume.FreeBytes);

            _rows.Add(new VolumeRow
            {
                Text = App.S("M.VolumeLine",
                    volume.Root, frei,
                    Format.Bytes(volume.UsedBytes), Format.Count(volume.Files),
                    Format.Count(volume.EvictableFiles), Format.Bytes(volume.EvictableBytes)),
                ButtonText = App.S("M.VolumeRelease",
                    Format.Count(volume.EvictableFiles), Format.Bytes(volume.EvictableBytes)),
                Root = volume.Root,
                CanRelease = volume.EvictableFiles > 0,
                MaxGb = (volume.MaxBytes / Gigabyte).ToString(),
                MinFreeGb = (volume.MinimumFreeBytes / Gigabyte).ToString()
            });
        }


        var (dateien, bytes) = _thumbUsage();
        ThumbUsageText.Text = App.S("M.ThumbUsage", Format.Count(dateien), Format.Bytes(bytes));
        ClearButton.IsEnabled = dateien > 0;
    }

    private async void OnReleaseVolume(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string root }) return;

        Hint.Text = App.S("G.Clearing");
        try
        {
            Hint.Text = await _release(root);
        }
        catch (Exception ex)
        {
            Hint.Text = App.S("G.ClearFailed", ex.Message);
        }
        finally
        {
            ShowUsage();
        }
    }

    private async void OnClearThumbnails(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            App.S("G.ClearBody"), App.S("G.ClearTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        ClearButton.IsEnabled = false;
        Hint.Text = App.S("G.Clearing");

        try
        {
            Hint.Text = await _clearCache();
        }
        catch (Exception ex)
        {
            Hint.Text = App.S("G.ClearFailed", ex.Message);
        }
        finally
        {
            ShowUsage();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Keine 0: unbegrenzt bedeutet, dass nie etwas verdraengt wird. Aus
        // "bei Bedarf" wuerde nach ein paar Monaten eine Vollkopie.
        if (!long.TryParse(CacheBudgetBox.Text.Trim(), out var gigabytes) || gigabytes < 1)
        {
            Hint.Text = App.S("G.BudgetInvalid");
            return;
        }

        if (!int.TryParse(ThresholdBox.Text.Trim(), out var schwelle) || schwelle < 1)
        {
            Hint.Text = App.S("G.ThresholdInvalid");
            return;
        }

        if (!long.TryParse(MinimumFreeBox.Text.Trim(), out var freeGigabytes) || freeGigabytes < 0)
        {
            Hint.Text = App.S("G.FreeInvalid");
            return;
        }

        // Erst pruefen, dann setzen: eine halb uebernommene Liste waere
        // schlimmer als eine abgelehnte.
        var grenzen = new List<(string Root, long Max, long Free)>();

        foreach (var zeile in _rows)
        {
            if (!long.TryParse(zeile.MaxGb.Trim(), out var max) || max < 1 ||
                !long.TryParse(zeile.MinFreeGb.Trim(), out var free) || free < 0)
            {
                Hint.Text = App.S("G.VolumeInvalid", zeile.Root);
                return;
            }

            grenzen.Add((zeile.Root, max * Gigabyte, free * Gigabyte));
        }

        if (!int.TryParse(ParallelismBox.Text.Trim(), out var parallelism) || parallelism < 1)
        {
            Hint.Text = App.S("G.ParallelInvalid");
            return;
        }

        if (!int.TryParse(ListenPortBox.Text.Trim(), out var listenPort) ||
            listenPort < 1 || listenPort > 65535)
        {
            Hint.Text = App.S("G.PortInvalid");
            return;
        }

        var home = HomeBox.Text.Trim();
        if (home.Length == 0)
        {
            Hint.Text = App.S("G.HomeMissing");
            return;
        }

        // Nur aendern, wenn der Pfad wirklich auf einen anderen Ort zeigt.
        // Sonst wuerde aus dem relativen Eintrag der Vorlage ein absoluter Pfad.
        if (!PathsEqual(Path.Combine(_configDirectory, home), _home))
        {
            _config.HomeDirectory = home;
            HomeChanged = true;
        }

        _config.MinimumCopies = schwelle;
        _config.GenerateThumbnails = ThumbsBox.IsChecked == true;
        _config.CacheMaxBytes = gigabytes * Gigabyte;
        _config.MinimumFreeBytes = freeGigabytes * Gigabyte;

        // Nur was von der Vorgabe abweicht, kommt in die Datei. Sonst stuende
        // dort nach dem ersten Speichern jedes Laufwerk, das gerade sichtbar
        // war -- und eine spaeter geaenderte Vorgabe wuerde nirgends mehr
        // wirken.
        foreach (var (root, max, free) in grenzen)
            if (max == _config.CacheMaxBytes && free == _config.MinimumFreeBytes)
                _config.VolumeLimits.RemoveAll(v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            else
                _config.SetLimits(root, max, free);
        _config.Parallelism = parallelism;
        _config.Listen = ListenBox.IsChecked == true;
        _config.ListenPort = listenPort;
        _config.LocalDiscovery = LocalDiscoveryBox.IsChecked == true;
        _config.Announce = AnnounceBox.IsChecked == true;
        _config.Discovery = DiscoveryBox.IsChecked == true;
        _config.DiscoveryServers =
        [
            .. DiscoveryServerBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
        _config.Relays = RelaysBox.IsChecked == true;
        _config.Language = LanguageBox.SelectedIndex switch { 1 => "de", 2 => "en", _ => "" };
        _config.Theme = ThemeBox.SelectedIndex switch { 1 => "Hell", 2 => "Dunkel", _ => "" };

        DialogResult = true;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // Ungueltige Pfade gelten als ungleich. Der Fehler zeigt sich beim Speichern.
            return false;
        }
    }
}
