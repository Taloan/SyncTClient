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

    private readonly Func<(int Files, long Bytes)> _thumbUsage;


    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromSeconds(2) };

    /// <param name="thumbUsage">Anzahl und Groesse der Vorschaubilder.</param>
    public ProgramSettingsWindow(
        AppConfig config, string configDirectory,
        Func<(int Files, long Bytes)> thumbUsage,
        Func<Task<string>> clearThumbnails)
    {
        InitializeComponent();

        KopfFuellen();

        _config = config;
        _configDirectory = configDirectory;
        _thumbUsage = thumbUsage;
        _clearCache = clearThumbnails;

        // Solange der Dialog offen ist, aendert sich, was freigegeben werden
        // darf: die Gegenstelle kuendigt an, der Aufraeumtakt laeuft. Ein
        // Knopf, der eine Zahl von vorhin nennt, verspricht etwas anderes,
        // als er tut.
        _takt.Tick += (_, _) => ShowUsage();
        _takt.Start();
        Closed += (_, _) => _takt.Stop();

        // Der Autostart kommt aus der Registrierung: nur dort steht, ob
        // Windows das Programm wirklich startet.
        AutostartBox.IsChecked = Autostart.Enabled;
        StartMinimizedBox.IsChecked = config.StartMinimized;
        CloseToTrayBox.IsChecked = config.CloseToTray;
        UpdateBox.SelectedIndex = config.UpdateCheck switch
        {
            UpdateInterval.Weekly => 1,
            UpdateInterval.Monthly => 2,
            _ => 0
        };

        // In der Datei darf der Pfad relativ stehen. Angezeigt wird er
        // ausgeschrieben, sonst bleibt unklar, worauf er sich bezieht.
        _home = Path.GetFullPath(Path.Combine(configDirectory, config.HomeDirectory));

        _loading = true;

        HomeBox.Text = _home;
        LanguageBox.SelectedIndex = config.Language switch { "de" => 1, "en" => 2, _ => 0 };
        ThemeBox.SelectedIndex = config.Theme switch { "Hell" => 1, "Dunkel" => 2, _ => 0 };

        ThresholdBox.Text = Math.Max(1, config.MinimumCopies).ToString();
        ThumbsBox.IsChecked = config.GenerateThumbnails;
        ShowUsage();
        ShowShellState();
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
    ///
    /// Das Bestimmen der Zahlen laeuft nebenher. Fuer jede Datei wird
    /// nachgeschlagen, ob die Gegenstelle sie fuehrt; im Faden der
    /// Oberflaeche waere das ein Stocken alle paar Sekunden, mitten im
    /// Tippen.
    /// </remarks>
    /// <summary>
    /// Zeigt, was tatsaechlich in der Registrierung steht.
    /// </summary>
    /// <remarks>
    /// Die Eintraege entstehen beim Start von selbst und koennen dabei
    /// lautlos scheitern -- eine fehlende DLL, ein Eintrag, den etwas anderes
    /// ueberschrieben hat. Wer das nachsehen wollte, musste bisher die
    /// Registrierung von Hand durchsuchen.
    /// </remarks>
    private void ShowShellState()
    {
        var zustand = ExplorerRegistration.Nachsehen();

        Eintragen(zustand.Mitgeliefert, ShipName, ShipVersion, ShipChanged, ShipFolder);
        Eintragen(zustand.Eingetragen, RegName, RegVersion, RegChanged, RegFolder);

        var text = App.S("M.ShellSummary",
            App.S(zustand.MenuRegistered ? "M.ShellYes" : "M.ShellNo"),
            zustand.SyncRoots);

        // Der Hinweis nur, wenn er zutrifft. Eine Zeile, die immer dasteht,
        // wird nicht mehr gelesen.
        if (zustand.Veraltet && zustand.ClassRegistered)
            text += Environment.NewLine + Environment.NewLine + App.S("M.ShellOutdated");

        ShellStateText.Text = text;

        // Eintragen geht nur mit Datei; ohne sie waere der Eintrag ein
        // Verweis auf nichts.
        ShellRegisterButton.IsEnabled = zustand.Mitgeliefert.Pfad is not null;
        ShellUnregisterButton.IsEnabled = zustand.ClassRegistered || zustand.MenuRegistered;

        // Erzeugen geht nur, wo der Quelltext liegt.
    }

    /// <summary>Traegt eine Datei in ihre Spalte ein.</summary>
    private static void Eintragen(
        ExplorerRegistration.Datei datei,
        TextBlock name, TextBlock fassung, TextBlock geaendert, TextBlock ordner)
    {
        // Ein Strich, wo nichts ist. Ein leeres Feld sieht aus wie ein Fehler
        // in der Anzeige.
        var fehlt = App.S("M.ShellDash");

        name.Text = datei.Pfad is null ? fehlt : datei.Name;
        fassung.Text = datei.Fassung.Length > 0 ? datei.Fassung : fehlt;
        geaendert.Text = datei.Geaendert.Length > 0 ? datei.Geaendert : fehlt;
        ordner.Text = datei.Pfad is null ? fehlt : datei.Ordner;
    }

    /// <summary>
    /// Traegt die Erweiterung in die Registrierung ein.
    /// </summary>
    /// <remarks>
    /// Alles unter HKEY_CURRENT_USER, deshalb ohne Administratorrechte. Das
    /// Programm tut dasselbe beim ersten Start; der Knopf ist fuer den Fall,
    /// dass jemand die Eintraege herausgenommen hat.
    /// </remarks>
    private async void OnRegisterShell(object sender, RoutedEventArgs e)
    {
        if (ExplorerRegistration.FindLibrary() is not { } library) return;

        ExplorerRegistration.RegisterClass(library);
        ExplorerRegistration.RegisterMenu(library);

        ShowShellState();
        await ExplorerAnbieten();
    }

    /// <summary>
    /// Nimmt die Eintraege wieder heraus.
    /// </summary>
    /// <remarks>
    /// Die angemeldeten Sync-Wurzeln bleiben stehen. Sie zu entfernen hiesse,
    /// die Platzhalter darunter unbrauchbar zu machen -- das geschieht beim
    /// Loesen einer Freigabe und nirgends sonst.
    /// </remarks>
    private async void OnUnregisterShell(object sender, RoutedEventArgs e)
    {
        ExplorerRegistration.UnregisterMenu();
        ExplorerRegistration.UnregisterClass();

        ShowShellState();
        await ExplorerAnbieten();
    }

    /// <summary>
    /// Bietet an, den Explorer neu zu starten.
    /// </summary>
    /// <remarks>
    /// Er liest diese Eintraege beim Start und laedt die DLL in seinen
    /// eigenen Prozess. Solange er laeuft, bleibt es bei dem, was er einmal
    /// geladen hat -- die Aenderung wirkt dann scheinbar nicht.
    ///
    /// Gefragt statt getan: offene Fenster gehen dabei zu, und wer gerade
    /// etwas kopiert, will das selbst entscheiden.
    /// </remarks>
    private async Task ExplorerAnbieten()
    {
        var antwort = MessageBox.Show(this,
            App.S("M.ShellRestartAsk"), App.S("S.Settings.ShellGroup"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (antwort != MessageBoxResult.Yes) return;

        try
        {
            foreach (var prozess in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                try { prozess.Kill(); } catch (Exception) { /* einer genuegt */ }
            }

            // Windows startet ihn meist von selbst. Tut es das nicht, bleibt
            // der Bildschirm leer -- und das waere schlimmer als alles, was
            // hier behoben werden sollte.
            await Task.Delay(2000);

            if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, App.S("S.Settings.ShellGroup"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Zieht die Zahl der Vorschaubilder nach.
    /// </summary>
    /// <remarks>
    /// Die Grenzen je Datentraeger standen hier einmal daneben. Sie sind in
    /// das Fenster der Platzhalter-Verwaltung gewandert: dort gilt jede Zahl
    /// genau einem Laufwerk, statt in einer Liste ueber alle zu stehen.
    /// </remarks>
    private async void ShowUsage()
    {
        (int Files, long Bytes) vorschau;

        try
        {
            vorschau = await Task.Run(_thumbUsage);
        }
        catch (Exception ex)
        {
            // Eine Anzeige ist es nicht wert, den Dialog zu beenden -- aber
            // stillschweigend verschwinden darf sie auch nicht. Genau das ist
            // einmal passiert: ein ganzer Abschnitt blieb leer, und nichts
            // sagte warum.
            Hint.Text = App.S("M.ThumbUsageFailed", ex.Message);
            return;
        }

        ThumbUsageText.Text = App.S("M.ThumbUsage",
            Format.Count(vorschau.Files), Format.Bytes(vorschau.Bytes));

        ClearButton.IsEnabled = vorschau.Files > 0;
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
        // Keine 0: unbegrenzt bedeutet, dass nie Speicherplatz freigegeben wird. Aus
        // "on-demand" wuerde nach ein paar Monaten eine Vollkopie.

        if (!int.TryParse(ThresholdBox.Text.Trim(), out var schwelle) || schwelle < 1)
        {
            Hint.Text = App.S("G.ThresholdInvalid");
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

        // Der Autostart steht nicht in der Konfiguration, sondern in der
        // Registrierung. Er wird deshalb getrennt geschrieben und danach
        // zurueckgelesen. Zuerst, denn nur er kann fehlschlagen.
        var autostart = AutostartBox.IsChecked == true;
        if (autostart != Autostart.Enabled)
        {
            try
            {
                Autostart.Set(autostart);
            }
            catch (Exception ex)
            {
                Hint.Text = App.S("D.AutostartFailed", ex.Message);
                AutostartBox.IsChecked = Autostart.Enabled;
                return;
            }
        }

        _config.StartMinimized = StartMinimizedBox.IsChecked == true;
        _config.CloseToTray = CloseToTrayBox.IsChecked == true;
        _config.UpdateCheck = UpdateBox.SelectedIndex switch
        {
            1 => UpdateInterval.Weekly,
            2 => UpdateInterval.Monthly,
            _ => UpdateInterval.Never
        };
        _config.MinimumCopies = schwelle;
        _config.GenerateThumbnails = ThumbsBox.IsChecked == true;
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

    /// <summary>Wo der Quelltext liegt.</summary>
    private const string Heimat = "https://github.com/Taloan/SyncTClient";

    /// <summary>
    /// Fassung, Erstellungszeitpunkt und die Adresse des Quelltextes.
    /// </summary>
    /// <remarks>
    /// Die Fassung kommt aus <c>Directory.Build.props</c> und steht als
    /// InformationalVersion in der Anwendung -- also genau die Zahl, die auch
    /// auf dem Installer steht. AssemblyVersion waere "0.9.1.0" und damit eine
    /// Stelle mehr, als irgendwo sonst genannt wird.
    ///
    /// Als Erstellungszeitpunkt gilt der Schreibzeitpunkt der Anwendung. Ein
    /// Zaehler waere schoener, aber es gibt keinen: gebaut wird hier, nicht auf
    /// einem Server, der mitzaehlt. Das Datum beantwortet dieselbe Frage --
    /// welcher Stand laeuft hier -- und kann nicht falsch sein.
    /// </remarks>
    private void KopfFuellen()
    {
        var anwendung = System.Reflection.Assembly.GetEntryAssembly();

        var fassung = anwendung?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        // Bei einem Bau mit Quellenverweis haengt "+<Pruefsumme>" hinten dran.
        if (fassung is not null && fassung.IndexOf('+') is var plus && plus > 0)
            fassung = fassung[..plus];

        fassung ??= anwendung?.GetName().Version?.ToString() ?? "?";

        var erstellt = string.Empty;
        try
        {
            var datei = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(datei) && File.Exists(datei))
                erstellt = File.GetLastWriteTime(datei).ToString("dd.MM.yyyy HH:mm");
        }
        catch (Exception)
        {
            // Ohne Zugriff auf die eigene Datei bleibt es bei der Fassung.
            // Ein Dialog, der deswegen nicht aufgeht, waere die schlechtere
            // Antwort auf eine Zeile Beiwerk.
        }

        VersionText.Text = erstellt.Length > 0
            ? App.S("S.Settings.VersionBuild", fassung, erstellt)
            : App.S("S.Settings.Version", fassung);

        HomeText.Text = Heimat;
    }

    /// <summary>
    /// Oeffnet die Adresse im eingestellten Browser.
    /// </summary>
    /// <remarks>
    /// <c>UseShellExecute</c> muss an sein: ohne das versucht .NET, die
    /// Adresse als ausfuehrbare Datei zu starten, und das schlaegt fehl.
    /// </remarks>
    private void OnOpenHome(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Heimat) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, App.S("S.Settings.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
