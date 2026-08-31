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
    private readonly Func<string> _cacheUsage;
    private readonly Func<Task<string>> _clearCache;

    /// <summary>Waehrend des Fuellens sollen die Felder nichts ausloesen.</summary>
    private bool _loading;

    /// <summary>Das Datenverzeichnis zeigt auf einen anderen Ort. Das gilt erst beim naechsten Start.</summary>
    public bool HomeChanged { get; private set; }

    public ProgramSettingsWindow(
        AppConfig config, string configDirectory,
        Func<string> cacheUsage, Func<Task<string>> clearCache)
    {
        InitializeComponent();

        _config = config;
        _configDirectory = configDirectory;
        _cacheUsage = cacheUsage;
        _clearCache = clearCache;

        // In der Datei darf der Pfad relativ stehen. Angezeigt wird er
        // ausgeschrieben, sonst bleibt unklar, worauf er sich bezieht.
        _home = Path.GetFullPath(Path.Combine(configDirectory, config.HomeDirectory));

        _loading = true;

        HomeBox.Text = _home;
        SharesRootBox.Text = config.SharesRootOrDefault;
        CacheBudgetBox.Text = Math.Max(1, config.CacheMaxBytes / Gigabyte).ToString();
        MinimumFreeBox.Text = (config.MinimumFreeBytes / Gigabyte).ToString();
        LanguageBox.SelectedIndex = config.Language switch { "de" => 1, "en" => 2, _ => 0 };
        ThemeBox.SelectedIndex = config.Theme switch { "Hell" => 1, "Dunkel" => 2, _ => 0 };

        ShowFreeSpace();
        UsageText.Text = cacheUsage();
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
    private void ShowFreeSpace()
    {
        var path = SharesRootBox.Text.Trim();
        if (path.Length == 0)
        {
            FreeSpaceText.Text = "";
            return;
        }

        var free = CacheBudget.FreeBytesOn(path);
        FreeSpaceText.Text = free < 0
            ? App.S("G.FreeUnknown")
            : App.S("G.FreeOn", Path.GetPathRoot(Path.GetFullPath(path)), Format.Bytes(free));
    }

    private void OnSharesRootChanged(object sender, TextChangedEventArgs e) => ShowFreeSpace();

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

    private void OnBrowseShares(object sender, RoutedEventArgs e) => Browse(SharesRootBox);

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
    private async void OnClearCache(object sender, RoutedEventArgs e)
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
            UsageText.Text = _cacheUsage();
            ShowFreeSpace();
        }
        catch (Exception ex)
        {
            Hint.Text = App.S("G.ClearFailed", ex.Message);
        }
        finally
        {
            ClearButton.IsEnabled = true;
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

        if (!long.TryParse(MinimumFreeBox.Text.Trim(), out var freeGigabytes) || freeGigabytes < 0)
        {
            Hint.Text = App.S("G.FreeInvalid");
            return;
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

        var root = SharesRootBox.Text.Trim();
        var standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT");

        // Der Normalfall bleibt in der Datei leer. Aendert sich der
        // Standardpfad spaeter, gilt dann der neue.
        _config.SharesRoot = root.Length == 0 || PathsEqual(root, standard) ? "" : root;

        _config.CacheMaxBytes = gigabytes * Gigabyte;
        _config.MinimumFreeBytes = freeGigabytes * Gigabyte;
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
