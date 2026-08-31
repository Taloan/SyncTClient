using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SyncTClient.Mount;
using SyncTClient.Vfs;

namespace SyncTClient.Gui;

/// <summary>
/// Was fuer das ganze Programm gilt. Diese Einstellungen haengen an keiner
/// Freigabe -- sonst kaeme niemand an sie heran, der noch keine hat.
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

    /// <summary>Das Datenverzeichnis zeigt woanders hin -- das gilt erst beim naechsten Start.</summary>
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

        // In der Datei darf der Pfad relativ stehen; zu sehen bekommt man ihn
        // ausgeschrieben, sonst raet man, worauf er sich bezieht.
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
    /// Zeigt, wieviel auf dem Laufwerk des Cache-Verzeichnisses frei ist --
    /// die Zahl, gegen die das Mindestmass laeuft.
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
    /// Sprache und Farbschema wirken sofort -- man will sehen, was man wählt,
    /// und nicht raten, wie es aussehen wird.
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

        // Ein Startpunkt, den es nicht gibt, laesst den Dialog irgendwo aufgehen.
        if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;

        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }

    /// <summary>
    /// Leert den Cache auf Zuruf. Das Verzeichnis bleibt stehen -- es ist
    /// zugleich der Platz der Freigaben; nur der Inhalt wird wieder zum
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
        // Kein 0: unbegrenzt hiesse, dass nie etwas weicht -- und damit waere
        // aus "bei Bedarf" nach ein paar Monaten eine Vollkopie geworden.
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

        // Nur anfassen, wenn es wirklich woanders hinzeigt -- sonst wuerde aus
        // dem relativen Eintrag der Vorlage ein absoluter Pfad.
        if (!PathsEqual(Path.Combine(_configDirectory, home), _home))
        {
            _config.HomeDirectory = home;
            HomeChanged = true;
        }

        var root = SharesRootBox.Text.Trim();
        var standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncT");

        // Der Normalfall bleibt leer in der Datei: dann zieht er mit, wenn er
        // sich einmal aendern sollte.
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
            // Unsinnige Pfade sind nicht gleich -- sie fliegen beim Speichern auf.
            return false;
        }
    }
}
